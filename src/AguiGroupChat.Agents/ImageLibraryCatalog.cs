using System.Collections.Concurrent;
using System.Security.Cryptography;
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
        var contentHash = Convert.ToHexString(SHA256.HashData(bytes));

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
            ContentHash = contentHash,
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

    /// <summary>
    /// 内容去重（1.0.278）：在指定图库里找**字节完全相同**的已有图片（SHA-256）。
    /// 旧快照没有 <see cref="ImageAsset.ContentHash"/> 时懒补（仅比对尺寸相同的候选，读盘一次）。
    /// </summary>
    public ImageAsset? FindDuplicate(string libId, byte[] bytes)
    {
        var lib = GetLibrary(libId);
        if (lib is null || bytes.LongLength == 0) return null;
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        foreach (var a in lib.Assets.ToArray())
        {
            if (a.Bytes != bytes.LongLength) continue;
            if (a.ContentHash is { } h)
            {
                if (string.Equals(h, hash, StringComparison.Ordinal)) return a;
                continue;
            }
            var path = ResolveAssetPath(lib, a);
            if (path is null) continue;
            try
            {
                var existing = File.ReadAllBytes(path);
                if (!string.Equals(Convert.ToHexString(SHA256.HashData(existing)), hash, StringComparison.Ordinal)) continue;
                a.ContentHash = hash; // 懒回填：下次直接命中
                return a;
            }
            catch { /* 文件缺失 / 读失败：忽略该候选 */ }
        }
        return null;
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
                    var (caption, tags) = await CaptionAsync(bytes, asset.ContentType, asset.FileName, CancellationToken.None);
                    if (caption is { Length: > 0 }) asset.Caption = caption;
                    // 多模态打标：自动生成的标签只在用户没手填标签时填充（不覆盖人工标注）
                    if (tags.Count > 0 && asset.Tags.Count == 0) asset.Tags = tags;
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
    /// 视觉模型生成图片描述 + 检索标签（多模态打标）。失败 / 未启用视觉（如 mock 提供方、未配视觉模型）返回空 ——
    /// 调用方继续用文件名 + 标签向量化，检索会弱一些，但不会让上传失败。
    /// </summary>
    private async Task<(string? Caption, List<string> Tags)> CaptionAsync(byte[] bytes, string contentType, string fileName, CancellationToken ct)
    {
        if (!_options.VisionEnabled) return (null, []);
        var visionModel = AgentCatalog.ResolveVisionModelName(
            _options, string.Equals(_options.Provider, "deepseek", StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(visionModel)) return (null, []);
        var catalog = _services.GetService<AgentCatalog>();
        if (catalog is null) return (null, []);

        try
        {
            var agent = catalog.CreateSystemVision(visionModel!);
            if (agent is null) return (null, []);
            var session = await agent.CreateSessionAsync(ct);
            var prompt =
                "请为这张图片生成用于**语义检索**的元数据，只输出严格 JSON（不要代码块、不要多余文字）：\n"
                + "{\"caption\":\"<一句话说清主体与场景，并含可直接当检索词的要素：物体/场景/动作/行业/配色/数量/风格；中文，≤160字；不要 Markdown>\",\"tags\":[\"<能区分这张图的检索词或短语>\"]}\n"
                + "tags 给 3~10 个（人名 / 专有号 / 行业词优先）。";
            var msg = new ChatMessage(ChatRole.User, new AIContent[]
            {
                new TextContent(prompt),
                new DataContent(bytes, string.IsNullOrWhiteSpace(contentType) ? "image/jpeg" : contentType),
            });
            var resp = await agent.RunAsync([msg], session, null, ct);
            var text = (resp.Text ?? "").Trim();
            if (text.Length == 0) return (null, []);

            // 容忍模型加代码块 / 前后缀：截取第一个 { 到最后一个 } 再解析
            var start = text.IndexOf('{');
            var end = text.LastIndexOf('}');
            if (start >= 0 && end > start)
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(text[start..(end + 1)]);
                    var root = doc.RootElement;
                    var cap = root.TryGetProperty("caption", out var c) && c.ValueKind == System.Text.Json.JsonValueKind.String
                        ? (c.GetString() ?? "").Trim() : "";
                    var tags = new List<string>();
                    if (root.TryGetProperty("tags", out var tg) && tg.ValueKind == System.Text.Json.JsonValueKind.Array)
                        foreach (var t in tg.EnumerateArray())
                        {
                            var s = (t.ValueKind == System.Text.Json.JsonValueKind.String ? t.GetString() : null)?.Trim() ?? "";
                            if (s.Length > 0 && !tags.Contains(s, StringComparer.Ordinal)) tags.Add(s);
                            if (tags.Count >= 20) break;
                        }
                    if (cap.Length > MaxCaptionChars) cap = cap[..MaxCaptionChars];
                    if (cap.Length > 0 || tags.Count > 0) return (cap.Length > 0 ? cap : null, tags);
                }
                catch { /* 解析失败：回退整段文本当描述 */ }
            }
            return (text.Length > MaxCaptionChars ? text[..MaxCaptionChars] : text, []);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "图库图片自动描述失败（继续用文件名向量化）：{FileName}", fileName);
            return (null, []);
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

    // ================= 写入能力句柄（技能建库 / 传图，1.0.277） =================

    /// <summary>写能力句柄 TTL：10 分钟（与检索句柄同口径）。</summary>
    public const int WriteScopeTtlMs = 10 * 60 * 1000;
    /// <summary>每轮最多新建图库数。</summary>
    public const int DefaultWriteMaxLibraries = 1;
    /// <summary>每轮最多上传图片数。</summary>
    public const int DefaultWriteMaxImages = 20;
    /// <summary>单张图片上限（10 MB；另受附件全局上限约束）。</summary>
    public const long DefaultWriteMaxImageBytes = 10L * 1024 * 1024;

    private readonly ConcurrentDictionary<string, ImageWriteScope> _writeScopes = new(StringComparer.Ordinal);

    /// <summary>
    /// 一次技能调用内的**写入能力**：绑定触发者 / 岗位 / 技能、配额与到期时间。
    /// 技能只能通过平台注入的 <c>imageWriteScopeId</c> 使用，不能自报图库越权。
    /// </summary>
    public sealed class ImageWriteScope
    {
        public required string ScopeId { get; init; }
        public required string OwnerUserId { get; init; }
        public string? AgentId { get; init; }
        public string? SkillId { get; init; }
        public required long ExpiresAtMs { get; init; }
        public int MaxLibraries { get; init; } = DefaultWriteMaxLibraries;
        public int MaxImages { get; init; } = DefaultWriteMaxImages;
        public long MaxImageBytes { get; init; } = DefaultWriteMaxImageBytes;
        /// <summary>写已有/授权库时是否需管理员审批（1.0.279）；新建库不受限。</summary>
        public bool RequiresApproval { get; init; }

        internal readonly object Gate = new();
        internal readonly HashSet<string> AllowedLibIds = new(StringComparer.Ordinal);
        internal readonly HashSet<string> CreatedLibIds = new(StringComparer.Ordinal);
        internal int LibrariesCreated;
        internal int ImagesUploaded;
    }

    /// <summary>登记一个写入能力句柄（平台在调用开启 imageWrite 的技能前签发）。</summary>
    /// <param name="seedLibraryIds">管理员在技能上指定的「可写入的已有图库」（可空）；本轮新建的库另外追加。</param>
    public string RegisterWriteScope(string ownerUserId, string? agentId, string? skillId,
        IReadOnlyList<string>? seedLibraryIds = null, int maxLibraries = DefaultWriteMaxLibraries,
        int maxImages = DefaultWriteMaxImages, bool requiresApproval = false)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var kv in _writeScopes)
            if (now >= kv.Value.ExpiresAtMs) _writeScopes.TryRemove(kv.Key, out _);
        if (_writeScopes.Count > MaxSearchScopes)
            foreach (var kv in _writeScopes.OrderBy(k => k.Value.ExpiresAtMs).Take(_writeScopes.Count - MaxSearchScopes + 100).ToList())
                _writeScopes.TryRemove(kv.Key, out _);

        var handle = "iwscope_" + IdGenerator.NewId();
        var scope = new ImageWriteScope
        {
            ScopeId = handle,
            OwnerUserId = ownerUserId,
            AgentId = agentId,
            SkillId = skillId,
            ExpiresAtMs = now + WriteScopeTtlMs,
            MaxLibraries = Math.Clamp(maxLibraries, 1, 10),
            MaxImages = Math.Clamp(maxImages, 1, 200),
            RequiresApproval = requiresApproval,
        };
        if (seedLibraryIds is { Count: > 0 })
            foreach (var id in seedLibraryIds.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct(StringComparer.Ordinal))
                scope.AllowedLibIds.Add(id);
        _writeScopes[handle] = scope;
        _logger.LogInformation("登记图库写入范围：agent={AgentId} skill={SkillId} owner={Owner} libs={Libs} quota={LibsQuota}/{ImgQuota} approval={Approval} handle={Handle}",
            agentId ?? "-", skillId ?? "-", ownerUserId, scope.AllowedLibIds.Count, scope.MaxLibraries, scope.MaxImages, requiresApproval, handle);
        return handle;
    }

    /// <summary>句柄 → 写入能力；未知 / 已过期返回 null。</summary>
    public ImageWriteScope? ResolveWriteScope(string? handle)
    {
        if (string.IsNullOrWhiteSpace(handle)) return null;
        if (!_writeScopes.TryGetValue(handle!, out var scope)) return null;
        if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() >= scope.ExpiresAtMs)
        {
            _writeScopes.TryRemove(handle!, out _);
            return null;
        }
        return scope;
    }

    /// <summary>消耗一次「新建图库」配额；超出返回 false。</summary>
    public bool TryConsumeLibraryCreate(ImageWriteScope scope)
    {
        lock (scope.Gate)
        {
            if (scope.LibrariesCreated >= scope.MaxLibraries) return false;
            scope.LibrariesCreated++;
            return true;
        }
    }

    /// <summary>消耗一次「上传图片」配额；超出返回 false。</summary>
    public bool TryConsumeImageUpload(ImageWriteScope scope)
    {
        lock (scope.Gate)
        {
            if (scope.ImagesUploaded >= scope.MaxImages) return false;
            scope.ImagesUploaded++;
            return true;
        }
    }

    /// <summary>把本轮新建的图库记入该句柄（后续只能往这些库上传）。</summary>
    public void AllowWriteLibrary(ImageWriteScope scope, string libId)
    {
        lock (scope.Gate)
        {
            scope.CreatedLibIds.Add(libId);
            scope.AllowedLibIds.Add(libId);
        }
    }

    /// <summary>该库是否由本轮技能新建（1.0.279：新建库不受审批队列限制）。</summary>
    public bool WasCreatedInRun(ImageWriteScope scope, string libId)
    {
        lock (scope.Gate) return scope.CreatedLibIds.Contains(libId);
    }

    /// <summary>该句柄是否允许写入指定图库（只允许本轮新建的库）。</summary>
    public bool IsWriteLibraryAllowed(ImageWriteScope scope, string libId)
    {
        lock (scope.Gate) return scope.AllowedLibIds.Contains(libId);
    }

    // ================= 检索 =================

    /// <summary>检索结果：给调用方（技能）的**文件路径** + 给用户看的描述。</summary>
    /// <remarks><see cref="ContentType"/> 是给技能的“能不能嵌入”判断用的：Word 认 png/gif/bmp/tiff，
    /// PDF 只认 png/jpeg —— 图库允许上传 WebP，不告知类型技能就只能靠扩展名猜。</remarks>
    public sealed record ImageHit(string LibId, string LibName, string AssetId, string FileName,
        string Caption, double Score, string ContentType, string Path, int Width, int Height,
        IReadOnlyList<string> Tags, string MatchKind, int MismatchCount, int RelevantCount);

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
            // 本库的检索严格度优先（库设了就用库的，否则用调用方传的）
            var gate = lib.MinScore ?? minScore;
            try
            {
                foreach (var hit in store.Search(ImgGroupPrefix + libId, "img", vec, Math.Max(1, topK), gate, "group"))
                    if (BuildHit(lib, hit.MessageId, hit.Score, "vector", query) is { } h) hits.Add(h);
            }
            catch (Exception ex) { _logger.LogDebug(ex, "图库 {LibId} 检索失败", libId); }
        }

        // 词面召回**不走上面的语义门槛** —— 这是刻意的，也是产品明确要求保留的：
        // 专属名词/专有号（人名“刘佳俊”、型号“SKU-2026”、代号“ORION-7788”）向量表示常常不好，
        // 但词面命中是**强证据**，不该被更严的语义门槛一票否决。
        // 它与“蹭词命中”之间有三道闸：相似度量纲换算 + 固定词面底线（KeywordSimilarityFloor）
        // + **词组证据**（HasPhraseEvidence：查询里得有一段连续词项命中，而不是散落一个常用词 ——
        // 后者在长描述下会被 BM25 累加虚高，实测出过 0.38 分被当成命中）。
        // 行为由这些测试钉住（改这一行前先看它们）：ImageLibraryTests.Search_FallsBackToKeywordRecall
        //（minScore 0.9 仍要召回）、Search_KeywordRecall_IgnoresZeroOverlap、
        // Search_KeywordRecall_RejectsScatteredCommonWordInLongDescription、
        // Search_KeywordRecall_RescuesPersonNameUnderStrictGate、
        // Bm25SimilarityScaleTests 的几条（只有常用词不算命中 / 罕见专有号算命中 / 词组证据边界），知识库侧同理。
        try { hits.AddRange(KeywordRecall(store, libIds, query, topK)); }
        catch (Exception ex) { _logger.LogDebug(ex, "图库关键词召回失败"); }

        return hits
            .GroupBy(h => h.AssetId, StringComparer.Ordinal)
            .Select(g => g.OrderByDescending(h => h.Score).First())
            .Where(h => h.Score > 0)
            .OrderByDescending(h => h.Score)
            .Take(topK)
            .ToList();
    }

    /// <summary>检索严格度的合理区间：低于 0.30 基本等于不筛（实测噪声高到 0.55），高于 0.95 则连本人照片都配不上。</summary>
    public const double MinStrictness = 0.30;
    public const double MaxStrictness = 0.95;

    /// <summary>推荐值（与各文档技能默认传的 minScore 同口径）：<b>标准</b>。</summary>
    public const double StandardStrictness = 0.60;

    /// <summary>
    /// 设置本库的<b>检索严格度</b>（null = 恢复“沿用调用方传的值”）。
    ///
    /// <para>
    /// 会夹到 <see cref="MinStrictness"/>~<see cref="MaxStrictness"/>：
    /// 前端下拉给的是 0.5 / 0.6 / 0.72 三档，但接口不阻止别的值（留手工调参余地）。
    /// </para>
    /// </summary>
    public string? SetStrictness(string libId, double? minScore)
    {
        var lib = GetLibrary(libId);
        if (lib is null) return "图库不存在";
        if (minScore is { } v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return "严格度必须是 0.30~0.95 之间的数字";
            lib.MinScore = Math.Clamp(v, MinStrictness, MaxStrictness);
        }
        else lib.MinScore = null;
        lib.UpdatedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _changes?.Notify();
        return null;
    }

    /// <summary>配图反馈（可纠正）的降分步长与封顶：每条「不相关」扣 Step，最多扣 Cap。</summary>
    public const double MismatchPenaltyStep = 0.08;
    public const double MaxMismatchPenalty = 0.32;

    /// <summary>记录一次配图反馈（可纠正）。<paramref name="query"/> 为触发检索的主题词（空 = 对所有查询生效，记 "*"）；
    /// 反馈按 <b>(查询 → 图)</b> 维度累计，检索时只对<b>同一主题词</b>降分，避免「一次不相关影响该图所有检索」。</summary>
    public string? RecordFeedback(string libId, string assetId, bool relevant, string? query)
    {
        var lib = GetLibrary(libId);
        var asset = lib?.Assets.FirstOrDefault(a => a.AssetId == assetId);
        if (lib is null || asset is null) return "图片不存在";
        var q = NormalizeFeedbackQuery(query);
        if (relevant) { asset.RelevantCount++; asset.RelevantQueries.Add(q); }
        else { asset.MismatchCount++; asset.MismatchQueries.Add(q); }
        TrimQueries(asset.MismatchQueries);
        TrimQueries(asset.RelevantQueries);
        lib.UpdatedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _changes?.Notify();
        return null;
    }

    /// <summary>反馈查询归一化：空 = "*"（对所有查询生效）；否则小写 + 压缩空白。</summary>
    private static string NormalizeFeedbackQuery(string? query)
        => string.IsNullOrWhiteSpace(query)
            ? "*"
            : string.Join(' ', query.Trim().ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static void TrimQueries(List<string> list)
    {
        const int cap = 50;
        if (list.Count > cap) list.RemoveRange(0, list.Count - cap);
    }

    /// <summary>某查询命中的反馈条数（条目等于该查询，或为 "*" 对所有查询生效）。</summary>
    private static int CountFeedback(IReadOnlyList<string> list, string query)
    {
        var n = 0;
        foreach (var e in list) if (e == query || e == "*") n++;
        return n;
    }

    /// <summary>按 (查询 → 图) 维度算降分：net = 该查询下的「不相关 − 相关」；兼容早期「按图全局」计数（无查询列表时）。</summary>
    private static double FeedbackPenalty(ImageAsset asset, string query)
    {
        int mismatch, relevant;
        if (asset.MismatchQueries.Count == 0 && asset.RelevantQueries.Count == 0)
        {
            mismatch = asset.MismatchCount; relevant = asset.RelevantCount; // 兼容 1.0.197 及更早（无查询维度）
        }
        else
        {
            var q = NormalizeFeedbackQuery(query);
            mismatch = CountFeedback(asset.MismatchQueries, q);
            relevant = CountFeedback(asset.RelevantQueries, q);
        }
        var net = Math.Max(0, mismatch - relevant);
        return net == 0 ? 0 : Math.Min(MaxMismatchPenalty, net * MismatchPenaltyStep);
    }

    /// <summary>按 assetId 构造检索结果（拿不到文件时返回 null：目录里有记录但文件已丢的情况）。已就查询应用反馈降分。</summary>
    private ImageHit? BuildHit(ImageLibrary lib, string assetId, double score, string matchKind, string query)
    {
        var asset = lib.Assets.FirstOrDefault(a => a.AssetId == assetId);
        if (asset is null || !string.Equals(asset.Status, "ready", StringComparison.OrdinalIgnoreCase)) return null;
        var path = ResolveAssetPath(lib, asset);
        if (path is null) return null;
        var adjusted = Math.Max(0, score - FeedbackPenalty(asset, query));
        return new ImageHit(lib.LibId, lib.Name, asset.AssetId, asset.FileName, asset.Caption, adjusted,
            asset.ContentType, path, asset.Width, asset.Height, asset.Tags, matchKind, asset.MismatchCount, asset.RelevantCount);
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
                // 必须换算到“与余弦相似度同一量纲”再比：Score() 是 sigmoid 归一化，
                // **零词面重叠也返回 0.5**（sigmoid(0)）。直接拿它当相似度的话，
                // “只共用一个常用词”也有 0.54，于是任何门槛（包括“严格”）都拦不住它（实测踩过）。
                var sim = Bm25Ranker.ToSimilarity(bm25);
                // 词面路另有一条固定底线（不随库的语义严格度变）：笛住罕罕见词，不让常用词蒙混进来。
                if (sim < Bm25Ranker.KeywordSimilarityFloor) continue;
                // 还要求**词组证据**（查询里有一段连续词项都在描述里）。只共享一个常用词不算命中：
                // 描述越长（尤其自动生成的长描述）多个普通词累加越容易蹭过底线，实测“颁奖 团队 合影”
                // 会命中一张描述里恰好有“团队”的 AI 插画（0.38 分）—— 那就是“配图不正确”。
                if (!Bm25Ranker.HasPhraseEvidence(query, it.Content)) continue;
                if (BuildHit(lib, it.MessageId, sim, "keyword", query) is { } h) scored.Add(h);
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
