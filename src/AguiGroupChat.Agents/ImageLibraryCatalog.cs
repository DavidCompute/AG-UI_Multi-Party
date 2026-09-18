using System.Collections.Concurrent;
using AguiGroupChat.Hub.Infra;
using AguiGroupChat.Hub.Models;
using AguiGroupChat.Hub.Persistence;
using AguiGroupChat.Hub.Storage;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AguiGroupChat.Agents;

/// <summary>
/// 图库目录：管理图库与图片元数据（内存 + 快照持久化，同 <see cref="KnowledgeBaseCatalog"/> 机制）。
///
/// <para>
/// 每张图片的**语义描述**向量化后存入 <see cref="IMessageMemoryStore"/>
/// （GroupId 约定 <c>img:{LibId}</c>、<c>sender_type='img'</c>、MessageId = assetId），
/// 文档技能（PPT 等）解析 <c>imageQuery</c> 时按语义检索取回本地文件路径直接嵌入。
/// </para>
///
/// <para>
/// 为什么不复用知识库：知识库的正文是**文本切片**（一篇文档 N 片、按片检索并注入模型上下文）；
/// 图库一张图**就是一个整体**，检索结果要给的是**文件路径**而不是给模型读的文字。
/// 两者共用向量表与 embedding，但目录语义、状态机与消费方都不同，混在一起只会互相牵制。
/// </para>
///
/// <para>
/// 为何要做这件事：让配图**不依赖外网**。内网部署里 Wikimedia 之类图库通常不可达，
/// “公司自己有一批图，数字员工配图时自动挑”才是真实需求。
/// </para>
/// </summary>
public sealed class ImageLibraryCatalog
{
    /// <summary>GroupId 约定前缀：图库向量的群维度 = img:{LibId}。</summary>
    public const string ImgGroupPrefix = "img:";

    /// <summary>单库图片上限（防一次上传把向量表塞爆）。</summary>
    public const int MaxAssetsPerLibrary = 2000;

    /// <summary>自动描述的长度上限（过长对检索没帮助，只会稀释向量）。</summary>
    private const int MaxCaptionChars = 400;

    /// <summary>图库允许的图片扩展名（与附件白名单里的图片子集一致）。</summary>
    public static readonly HashSet<string> AllowedImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp",
    };

    private readonly AgentOptions _options;
    private readonly IServiceProvider _services;
    private readonly ILogger _logger;
    private readonly ChangeHub? _changes;
    private readonly string _root;
    private readonly ConcurrentDictionary<string, ImageLibrary> _libs = new(StringComparer.Ordinal);

    /// <summary>处理中标记：assetId → 后台任务（供测试等待与去重）。</summary>
    private readonly ConcurrentDictionary<string, Task> _processing = new(StringComparer.Ordinal);

    /// <summary>描述生成 / 向量化并发上限（视觉模型与 embedding 都是资源密集操作）。</summary>
    private static readonly SemaphoreSlim ProcessingGate = new(2, 2);

    /// <param name="root">图库文件根目录（如 data/images）；图片落 <c>{root}/{libId}/{assetId}{ext}</c>。</param>
    public ImageLibraryCatalog(AgentOptions options, IServiceProvider services, ILoggerFactory loggerFactory,
        string root, ChangeHub? changes = null)
    {
        _options = options;
        _services = services;
        _logger = loggerFactory.CreateLogger<ImageLibraryCatalog>();
        _root = Path.GetFullPath(root);
        _changes = changes;
    }

    /// <summary>图库根目录（视频/测试用）。</summary>
    public string Root => _root;

    // ================= 目录管理 =================

    public ImageLibrary CreateLibrary(string name, string description, string? ownerId)
    {
        var lib = new ImageLibrary
        {
            LibId = "img_" + IdGenerator.NewId(),
            Name = name.Trim(),
            Description = description?.Trim() ?? "",
            OwnerId = ownerId,
            UpdatedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
        _libs[lib.LibId] = lib;
        _changes?.Notify();
        return lib;
    }

    public ImageLibrary? GetLibrary(string? libId)
        => libId is not null && _libs.TryGetValue(libId, out var lib) ? lib : null;

    /// <summary>可见列表：系统级 | 当前用户创建 | 管理员 | 当前用户是 SharedGroupIds 中某群成员。</summary>
    public IReadOnlyList<ImageLibrary> ListLibraries(string? userId, IReadOnlySet<string>? memberGroupIds = null, bool isAdmin = false)
        => _libs.Values.Where(l => CanRead(l, userId, memberGroupIds, isAdmin))
            .OrderByDescending(l => l.UpdatedAtMs).ToList();

    /// <summary>是否允许某用户<b>读取 / 绑定</b>该图库（写权限只给创建者 / 管理员）。</summary>
    public bool CanRead(ImageLibrary lib, string? userId, IReadOnlySet<string>? memberGroupIds, bool isAdmin)
        => lib.OwnerId is null
           || (userId is not null && lib.OwnerId == userId)
           || isAdmin
           || (memberGroupIds is { Count: > 0 } && lib.SharedGroupIds.Any(memberGroupIds.Contains));

    /// <summary>是否允许改动图片。
    ///
    /// <para>
    /// 注意与知识库的一个<b>有意差异</b>：知识库的 CanWrite 把系统级（OwnerId=null）也算“可写”，
    /// 而它的文档写的是“系统级只读” —— 两边对不上。这里以文档语义为准：
    /// 系统级图库只有管理员能改（它是全员可见的东西，不能让任意用户往里塞图）。
    /// </para>
    /// </summary>
    public bool CanWrite(ImageLibrary lib, string? userId, bool isAdmin)
        => lib.OwnerId is null
            ? isAdmin
            : isAdmin || (userId is not null && lib.OwnerId == userId);

    public IReadOnlyList<ImageLibrary> ListAll() => _libs.Values.ToList();

    public void RestoreAll(IEnumerable<ImageLibrary> libs)
    {
        _libs.Clear();
        foreach (var lib in libs)
        {
            // 快照恢复时仍有 processing 的图片：说明上次服务在生成描述 / 向量化的中途退出，
            // 标记为失败待重新上传（与知识库文档同一处理）。
            foreach (var asset in lib.Assets)
            {
                if (string.Equals(asset.Status, "processing", StringComparison.OrdinalIgnoreCase))
                {
                    asset.Status = "error";
                    asset.Error = "服务重启导致处理中断，请重新上传";
                }
            }
            _libs[lib.LibId] = lib;
        }
    }

    /// <summary>删除图库：移除其全部向量 + 磁盘文件 + 目录项。</summary>
    public bool RemoveLibrary(string libId)
    {
        if (!_libs.TryRemove(libId, out _)) return false;
        try { _services.GetService<IMessageMemoryStore>()?.RemoveGroup(ImgGroupPrefix + libId); }
        catch (Exception ex) { _logger.LogWarning(ex, "删除图库向量失败：{LibId}", libId); }
        try
        {
            var dir = LibraryDir(libId);
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "删除图库文件失败：{LibId}", libId); }
        _changes?.Notify();
        return true;
    }

    private string LibraryDir(string libId) => Path.Combine(_root, libId);

    // ================= 图片 =================

    /// <summary>
    /// 添加一张图片：立即落盘并返回“处理中”记录，<b>生成描述 → 向量化</b>在后台执行
    /// （视觉模型与 embedding 都耗时，避免上传请求长时间阻塞；前端按 Status 轮询）。
    /// 向量存储 / embedding 不可用时同步返回错误说明。
    /// </summary>
    public (ImageAsset? Asset, string? Error) AddAsset(string libId, string fileName, string contentType, byte[] bytes, string? uploadedBy)
    {
        var lib = GetLibrary(libId);
        if (lib is null) return (null, "图库不存在");
        if (lib.Assets.Count >= MaxAssetsPerLibrary)
            return (null, $"图库图片数已达上限（{MaxAssetsPerLibrary}）");

        var ext = (Path.GetExtension(fileName) ?? "").ToLowerInvariant();
        if (ext.Length == 0 || !AllowedImageExtensions.Contains(ext))
            return (null, "只支持图片：" + string.Join(" / ", AllowedImageExtensions.OrderBy(x => x)));
        if (bytes.LongLength == 0) return (null, "图片内容为空");
        if (bytes.LongLength > AttachmentStore.MaxFileBytes)
            return (null, $"图片过大（上限 {AttachmentStore.MaxFileBytes / 1024 / 1024} MB）");

        var store = _services.GetService<IMessageMemoryStore>();
        var embedding = _services.GetService<IEmbeddingProvider>();
        if (store is null || embedding is null)
            return (null, "图库不可用：需要启用语义记忆（Storage:Provider=postgres/sqlite 且 Agents:Memory:Enabled=true）");

        var assetId = "asset_" + IdGenerator.NewId();
        var stored = assetId + ext;
        try
        {
            var dir = LibraryDir(libId);
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, stored), bytes);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "图库图片落盘失败：{LibId}/{AssetId}", libId, assetId);
            return (null, "图片保存失败：" + ex.Message);
        }

        ImageSizeProbe.TryRead(bytes, out var width, out var height);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var asset = new ImageAsset
        {
            AssetId = assetId,
            FileName = Path.GetFileName(fileName),
            StoredName = stored,
            ContentType = string.IsNullOrWhiteSpace(contentType) ? ContentTypeOf(ext) : contentType,
            Width = width,
            Height = height,
            Bytes = bytes.LongLength,
            Status = "processing",
            UploadedAtMs = now,
            UploadedBy = uploadedBy,
        };
        lock (lib.Assets) lib.Assets.Add(asset);
        lib.UpdatedAtMs = now;
        _changes?.Notify();

        var task = Task.Run(() => ProcessAssetAsync(lib, asset, bytes, store, embedding));
        _processing[assetId] = task;
        _ = task.ContinueWith(t => _processing.TryRemove(assetId, out _), TaskScheduler.Default);
        return (asset, null);
    }

    /// <summary>当前是否有图片正在处理（前端/测试轮询用）。</summary>
    public bool IsProcessing(string assetId) => _processing.ContainsKey(assetId);

    /// <summary>等某张图片处理完成（测试用；超时返回 false）。</summary>
    public async Task<bool> WaitForProcessingAsync(string assetId, TimeSpan timeout)
    {
        if (!_processing.TryGetValue(assetId, out var task)) return true;
        var done = await Task.WhenAny(task, Task.Delay(timeout));
        return done == task;
    }

    /// <summary>后台：生成描述（视觉模型）→ 向量化入库；任何失败把图片标记为 error。</summary>
    private async Task ProcessAssetAsync(ImageLibrary lib, ImageAsset asset, byte[] bytes,
        IMessageMemoryStore store, IEmbeddingProvider embedding)
    {
        try
        {
            if (!await ProcessingGate.WaitAsync(TimeSpan.FromSeconds(60)))
            {
                MarkError(asset, "排队超时，请稍后重试");
                return;
            }
            try
            {
                if (asset.Caption.Length == 0)
                {
                    var caption = await CaptionAsync(bytes, asset.ContentType, asset.FileName, CancellationToken.None);
                    if (caption is { Length: > 0 }) asset.Caption = caption;
                }
                await VectorizeAsync(lib, asset, store, embedding);
            }
            finally
            {
                ProcessingGate.Release();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "图库图片处理失败：{LibId}/{AssetId}", lib.LibId, asset.AssetId);
            MarkError(asset, "处理失败：" + ex.Message);
        }
    }

    /// <summary>描述 + 标签 + 文件名 → embedding → 写入图库向量表。</summary>
    private async Task VectorizeAsync(ImageLibrary lib, ImageAsset asset, IMessageMemoryStore store, IEmbeddingProvider embedding)
    {
        var text = asset.EmbeddingText();
        if (text.Trim().Length == 0)
        {
            // 没有视觉模型也没手填描述：仍然入库，但只能靠文件名 —— 关键词兜底检索还能命中
            asset.Status = "ready";
            asset.Error = null;
            _changes?.Notify();
            return;
        }

        var vec = await embedding.EmbedAsync(text, CancellationToken.None);
        if (vec is null || vec.Length == 0)
        {
            MarkError(asset, "embedding 不可用（本地模型未加载或端点不可达），图片未入库");
            return;
        }

        // 向量化期间图片可能已被移除，写入前再确认（避免孤儿向量）
        if (!_libs.TryGetValue(lib.LibId, out var current)
            || !current.Assets.Any(a => a.AssetId == asset.AssetId))
        {
            _logger.LogInformation("图库 {LibId} 图片 {AssetId} 处理期间已被移除，丢弃向量", lib.LibId, asset.AssetId);
            return;
        }

        store.Upsert(new MessageMemoryRecord(
            MessageId: asset.AssetId,
            GroupId: ImgGroupPrefix + lib.LibId,
            TopicId: "img",
            SenderId: asset.FileName,
            SenderType: "img",
            Content: text,
            Embedding: vec,
            Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));

        asset.Status = "ready";
        asset.Error = null;
        _changes?.Notify();
    }

    /// <summary>
    /// 视觉模型生成图片描述。失败 / 未启用视觉（如 mock 提供方、未配视觉模型）返回 null —— 
    /// 调用方继续用文件名 + 标签向量化，检索会弱一些，但不会让上传失败。
    /// </summary>
    private async Task<string?> CaptionAsync(byte[] bytes, string contentType, string fileName, CancellationToken ct)
    {
        if (!_options.VisionEnabled) return null;
        var visionModel = AgentCatalog.ResolveVisionModelName(
            _options, string.Equals(_options.Provider, "deepseek", StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(visionModel)) return null;
        var catalog = _services.GetService<AgentCatalog>();
        if (catalog is null) return null;

        try
        {
            var agent = catalog.CreateSystemVision(visionModel!);
            if (agent is null) return null;
            var session = await agent.CreateSessionAsync(ct);
            var prompt =
                "请为这张图片写一段用于**语义检索**的描述（之后有人用一句话来找图，要能命中它）。\n"
                + "要求：\n"
                + "① 先一句话说清画面主体与场景（谁/什么、在哪、在做什么）；\n"
                + "② 再列出可直接当检索词的要素：物体、场景、动作、行业、配色、数量、风格（照片/截图/图表/插画）；\n"
                + "③ 用中文，不要 Markdown、不要客套话、不要“这张图片”之类的开头；\n"
                + "④ 总长不超过 200 字。\n"
                + "只输出描述本身。";
            var msg = new ChatMessage(ChatRole.User, new AIContent[]
            {
                new TextContent(prompt),
                new DataContent(bytes, string.IsNullOrWhiteSpace(contentType) ? "image/jpeg" : contentType),
            });
            var resp = await agent.RunAsync([msg], session, null, ct);
            var text = (resp.Text ?? "").Trim();
            if (text.Length == 0) return null;
            return text.Length > MaxCaptionChars ? text[..MaxCaptionChars] : text;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "图库图片自动描述失败（继续用文件名向量化）：{FileName}", fileName);
            return null;
        }
    }

    /// <summary>修改描述 / 标签：改了就要**重新向量化**（检索依据就是它），同样走后台。</summary>
    public async Task<string?> UpdateAssetAsync(string libId, string assetId, string? caption, IReadOnlyList<string>? tags, CancellationToken ct = default)
    {
        var lib = GetLibrary(libId);
        var asset = lib?.Assets.FirstOrDefault(a => a.AssetId == assetId);
        if (lib is null || asset is null) return "图片不存在";
        var store = _services.GetService<IMessageMemoryStore>();
        var embedding = _services.GetService<IEmbeddingProvider>();
        if (store is null || embedding is null) return "图库不可用：需要启用语义记忆";

        asset.Caption = (caption ?? "").Trim();
        if (tags is not null) asset.Tags = tags.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).Take(20).ToList();
        asset.Status = "processing";
        asset.Error = null;
        lib.UpdatedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _changes?.Notify();

        var task = Task.Run(async () =>
        {
            try
            {
                if (!await ProcessingGate.WaitAsync(TimeSpan.FromSeconds(60)))
                {
                    MarkError(asset, "排队超时，请稍后重试");
                    return;
                }
                try { await VectorizeAsync(lib, asset, store, embedding); }
                finally { ProcessingGate.Release(); }
            }
            catch (Exception ex) { MarkError(asset, "处理失败：" + ex.Message); }
        });
        _processing[assetId] = task;
        _ = task.ContinueWith(t => _processing.TryRemove(assetId, out _), TaskScheduler.Default);
        await task;
        return asset.Status == "error" ? asset.Error : null;
    }

    /// <summary>移除一张图片：删向量（先删，避免删文件后向量还命中）+ 删文件 + 删目录项。</summary>
    public bool RemoveAsset(string libId, string assetId)
    {
        var lib = GetLibrary(libId);
        var asset = lib?.Assets.FirstOrDefault(a => a.AssetId == assetId);
        if (lib is null || asset is null) return false;
        try { _services.GetService<IMessageMemoryStore>()?.Remove(ImgGroupPrefix + libId, assetId); }
        catch (Exception ex) { _logger.LogWarning(ex, "移除图库图片向量失败：{AssetId}", assetId); }
        try
        {
            var file = Path.Combine(LibraryDir(libId), asset.StoredName);
            if (File.Exists(file)) File.Delete(file);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "移除图库图片文件失败：{AssetId}", assetId); }
        lock (lib.Assets) lib.Assets.Remove(asset);
        lib.UpdatedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _changes?.Notify();
        return true;
    }

    // ================= 检索范围句柄（技能回调用） =================

    /// <summary>检索范围句柄：句柄 → 允许的图库 ID 列表。</summary>
    private readonly ConcurrentDictionary<string, (IReadOnlyList<string> LibIds, long AtMs)> _searchScopes = new(StringComparer.Ordinal);

    private const int SearchScopeTtlMs = 10 * 60 * 1000;
    private const int MaxSearchScopes = 500;

    /// <summary>
    /// 登记一个「本次调用允许检索哪些图库」的句柄，供技能回调时使用。
    ///
    /// <para>
    /// 为何用句柄而不是直接把图库 ID 交给技能：技能入参是<b>模型生成的</b>，天生不可信 ——
    /// 若技能能自报图库 ID，任何用户都能让模型写个别人的图库 ID 把别人的图读出来。
    /// 句柄是平台在调用前登记的（范围取触发者本人可读的图库），技能只能“拿着句柄问”，越不了权。
    /// </para>
    /// </summary>
    public string RegisterSearchScope(IReadOnlyList<string> libIds, string? agentId)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        // 顺手清理过期 / 超量（长跑进程不能无界增长）
        foreach (var kv in _searchScopes)
            if (now - kv.Value.AtMs > SearchScopeTtlMs) _searchScopes.TryRemove(kv.Key, out _);
        if (_searchScopes.Count > MaxSearchScopes)
            foreach (var kv in _searchScopes.OrderBy(k => k.Value.AtMs).Take(_searchScopes.Count - MaxSearchScopes + 100).ToList())
                _searchScopes.TryRemove(kv.Key, out _);

        var handle = "iscope_" + IdGenerator.NewId();
        _searchScopes[handle] = (libIds, now);
        _logger.LogDebug("登记图库检索范围：agent={AgentId} libs={Count} handle={Handle}", agentId, libIds.Count, handle);
        return handle;
    }

    /// <summary>句柄 → 允许检索的图库 ID；未知 / 过期返回 null。</summary>
    public IReadOnlyList<string>? ResolveSearchScope(string? handle)
    {
        if (string.IsNullOrWhiteSpace(handle)) return null;
        if (!_searchScopes.TryGetValue(handle!, out var entry)) return null;
        if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - entry.AtMs > SearchScopeTtlMs)
        {
            _searchScopes.TryRemove(handle!, out _);
            return null;
        }
        return entry.LibIds;
    }

    // ================= 检索 =================

    /// <summary>检索结果：给调用方（技能）的**文件路径** + 给用户看的描述。</summary>
    /// <remarks><see cref="ContentType"/> 是给技能的“能不能嵌入”判断用的：Word 认 png/gif/bmp/tiff，
    /// PDF 只认 png/jpeg —— 图库允许上传 WebP，不告知类型技能就只能靠扩展名猜。</remarks>
    public sealed record ImageHit(string LibId, string LibName, string AssetId, string FileName,
        string Caption, double Score, string ContentType, string Path, int Width, int Height);

    /// <summary>
    /// 在指定图库集合里按语义检索 top-k 图片（每库各取 topK 条再合并排序），返回**可读的绝对路径**。
    /// 与知识库检索同构：向量命中 + BM25 关键词兜底（描述里的词面命中不该被相似度阈值丢掉）。
    /// </summary>
    public async Task<IReadOnlyList<ImageHit>> SearchAsync(IReadOnlyList<string> libIds, string query,
        int topK, double minScore, CancellationToken ct = default)
    {
        if (libIds.Count == 0 || string.IsNullOrWhiteSpace(query)) return [];
        var store = _services.GetService<IMessageMemoryStore>();
        var embedding = _services.GetService<IEmbeddingProvider>();
        if (store is null || embedding is null) return [];

        float[]? vec;
        try { vec = await embedding.EmbedAsync(query, ct); }
        catch (Exception ex) { _logger.LogDebug(ex, "图库检索 embedding 失败"); return []; }
        if (vec is null || vec.Length == 0) return [];

        var hits = new List<ImageHit>();
        foreach (var libId in libIds)
        {
            var lib = GetLibrary(libId);
            if (lib is null) continue;
            try
            {
                foreach (var hit in store.Search(ImgGroupPrefix + libId, "img", vec, Math.Max(1, topK), minScore, "group"))
                    if (BuildHit(lib, hit.MessageId, hit.Score) is { } h) hits.Add(h);
            }
            catch (Exception ex) { _logger.LogDebug(ex, "图库 {LibId} 检索失败", libId); }
        }

        try { hits.AddRange(KeywordRecall(store, libIds, query, topK)); }
        catch (Exception ex) { _logger.LogDebug(ex, "图库关键词召回失败"); }

        return hits
            .GroupBy(h => h.AssetId, StringComparer.Ordinal)
            .Select(g => g.OrderByDescending(h => h.Score).First())
            .OrderByDescending(h => h.Score)
            .Take(topK)
            .ToList();
    }

    /// <summary>按 assetId 构造检索结果（拿不到文件时返回 null：目录里有记录但文件已丢的情况）。</summary>
    private ImageHit? BuildHit(ImageLibrary lib, string assetId, double score)
    {
        var asset = lib.Assets.FirstOrDefault(a => a.AssetId == assetId);
        if (asset is null || !string.Equals(asset.Status, "ready", StringComparison.OrdinalIgnoreCase)) return null;
        var path = ResolveAssetPath(lib, asset);
        if (path is null) return null;
        return new ImageHit(lib.LibId, lib.Name, asset.AssetId, asset.FileName, asset.Caption, score,
            asset.ContentType, path, asset.Width, asset.Height);
    }

    /// <summary>图片的绝对路径（文件不存在返回 null）。</summary>
    public string? ResolveAssetPath(ImageLibrary lib, ImageAsset asset)
    {
        if (asset.StoredName.Length == 0) return null;
        var path = Path.Combine(LibraryDir(lib.LibId), asset.StoredName);
        // 防目录穿越：StoredName 来自我们自己的命名，但恢复快照时可能是被改过的文件
        if (!Path.GetFullPath(path).StartsWith(Path.GetFullPath(LibraryDir(lib.LibId)) + Path.DirectorySeparatorChar,
                StringComparison.Ordinal)) return null;
        return File.Exists(path) ? path : null;
    }

    /// <summary>关键词召回：BM25 在描述上打分，兜住“词面命中但向量相似度低于阈值”的图。</summary>
    private List<ImageHit> KeywordRecall(IMessageMemoryStore store, IReadOnlyList<string> libIds, string query, int topK)
    {
        var cap = Math.Max(topK, 120);
        var scored = new List<ImageHit>();
        foreach (var libId in libIds)
        {
            var lib = GetLibrary(libId);
            if (lib is null) continue;
            foreach (var it in store.ListMessages(ImgGroupPrefix + libId, null, null, cap, 0))
            {
                var bm25 = Bm25Ranker.Score(query, it.Content);
                // 必须用“> 零重叠基准分”判定真有词面命中：Score() 是 sigmoid 归一化，
                // **零词面重叠也返回 0.5**（sigmoid(0)）。原先写的是“> 0”，等于不筛 ——
                // 于是任何查询都会把整个图库以 0.5 分召回：表现为“无意义关键词也配上了一张任意照片”、
                // 而且 minScore 形同虚设（0.5 > 默认 0.25）。
                if (bm25 <= Bm25Ranker.ZeroOverlapScore) continue;
                if (BuildHit(lib, it.MessageId, bm25) is { } h) scored.Add(h);
            }
        }
        return scored;
    }

    private void MarkError(ImageAsset asset, string error)
    {
        asset.Status = "error";
        asset.Error = error;
        _changes?.Notify();
    }

    /// <summary>扩展名 → MIME（上传时前端可能没给 content type）。</summary>
    private static string ContentTypeOf(string ext) => ext switch
    {
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".bmp" => "image/bmp",
        ".webp" => "image/webp",
        _ => "image/jpeg",
    };
}
