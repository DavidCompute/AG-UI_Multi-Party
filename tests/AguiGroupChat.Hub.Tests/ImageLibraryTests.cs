using System.Text;
using AguiGroupChat.Agents;
using AguiGroupChat.Hub.Models;
using AguiGroupChat.Hub.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>
/// 图库（<see cref="ImageLibraryCatalog"/>）：用户上传图片 → 自动描述 → 向量化 → 文档技能按语义检索配图。
///
/// <para>
/// 之所以要有它：让配图<b>不依赖外网</b>。内网部署里 Wikimedia 这类图库通常不可达，
/// “公司自己有一批图，数字员工配图时自动挑”才是真实需求。
/// </para>
///
/// <para>
/// 测试沿用知识库的假件套路（FakeStore + FakeEmbedding）：图库与知识库共用同一张向量表
/// （<c>img:{libId}</c> / <c>sender_type='img'</c>），只要不真连 pgvector / sqlite-vec 就能纯内存验。
/// </para>
/// </summary>
public sealed class ImageLibraryTests
{
    private sealed class FakeStore : IMessageMemoryStore
    {
        public List<MessageMemoryRecord> Records { get; } = [];
        public List<string> RemovedGroups { get; } = [];
        /// <summary>模拟纯语义检索空手（用于验证关键词召回兜底）。</summary>
        public bool VectorSearchReturnsEmpty { get; set; }

        public void EnsureSchema() { }
        public void Upsert(MessageMemoryRecord record)
        {
            Records.RemoveAll(r => r.MessageId == record.MessageId);
            Records.Add(record);
        }
        public void Remove(string groupId, string messageId) => Records.RemoveAll(r => r.GroupId == groupId && r.MessageId == messageId);
        public void RemoveGroup(string groupId)
        {
            RemovedGroups.Add(groupId);
            Records.RemoveAll(r => r.GroupId == groupId);
        }
        public void ClearAll() => Records.Clear();
        public IReadOnlyList<MessageMemoryItem> ListMessages(string? groupId, string? senderId, string? keyword, int limit, int offset)
            => Records.Where(r => (groupId is null || r.GroupId == groupId)
                            && (senderId is null || r.SenderId == senderId)
                            && (string.IsNullOrWhiteSpace(keyword) || r.Content.Contains(keyword, StringComparison.Ordinal)))
                    .Select(r => new MessageMemoryItem(r.MessageId, r.GroupId, r.TopicId, r.SenderId, r.SenderType, r.Content, r.Timestamp, r.Importance, r.ExpiresAt))
                    .OrderByDescending(r => r.Timestamp).Take(limit).ToList();
        public long CountMessages(string? groupId, string? senderId, string? keyword) => 0;
        public IReadOnlyList<MessageMemoryGroupStat> GroupStats(long nowMs) => [];
        public bool DeleteByMessageId(string messageId) => false;
        public bool UpdateImportance(string messageId, int importance) => false;
        public int SetExpiry(string? groupId, long? expiresAt, long nowMs) => 0;
        public int PruneExpired(long nowMs) => 0;
        public IReadOnlyList<MessageMemoryHit> Search(string groupId, string? agentId, float[] embedding, int topK, double minScore, string scope)
            => VectorSearchReturnsEmpty
                ? []
                : Records.Where(r => r.GroupId == groupId && r.SenderType == "img")
                    .Select(r => new MessageMemoryHit(r.MessageId, r.Content, r.SenderId, r.Timestamp, 0.9))
                    .Take(topK).ToList();
        public IReadOnlyList<MessageMemoryHit> SearchPerson(string personId, string currentGroupId, float[] embedding, int topK, double minScore) => [];
    }

    private sealed class FakeEmbedding : IEmbeddingProvider
    {
        private readonly TimeSpan _delay;
        public FakeEmbedding(TimeSpan delay = default) => _delay = delay;
        public async Task<float[]?> EmbedAsync(string text, CancellationToken ct = default)
        {
            if (_delay > TimeSpan.Zero) await Task.Delay(_delay, ct);
            return string.IsNullOrWhiteSpace(text) ? null : [0.1f, 0.2f, 0.3f];
        }
        public void Dispose() { }
    }

    private static (ImageLibraryCatalog Catalog, FakeStore Store, string Root) NewCatalog(TimeSpan embedDelay = default)
    {
        var root = Path.Combine(Path.GetTempPath(), "agui-img-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        var services = new ServiceCollection();
        var store = new FakeStore();
        services.AddSingleton<IMessageMemoryStore>(store);
        services.AddSingleton<IEmbeddingProvider>(new FakeEmbedding(embedDelay));
        var sp = services.BuildServiceProvider();
        // Provider=mock → 没有视觉模型：自动描述拿不到，走“文件名 + 手填描述”这条降级路径
        var catalog = new ImageLibraryCatalog(new AgentOptions { Provider = "mock" }, sp,
            NullLoggerFactory.Instance, root);
        return (catalog, store, root);
    }

    /// <summary>造一张尺寸可被文件头解析出来的 PNG（只需要魔数 + IHDR 宽高）。</summary>
    private static byte[] FakePng(int w, int h)
    {
        var b = new byte[32];
        b[0] = 0x89; b[1] = 0x50; b[2] = 0x4E; b[3] = 0x47;
        b[12] = (byte)'I'; b[13] = (byte)'H'; b[14] = (byte)'D'; b[15] = (byte)'R';
        b[16] = (byte)(w >> 24); b[17] = (byte)(w >> 16); b[18] = (byte)(w >> 8); b[19] = (byte)w;
        b[20] = (byte)(h >> 24); b[21] = (byte)(h >> 16); b[22] = (byte)(h >> 8); b[23] = (byte)h;
        return b;
    }

    private static async Task<(string LibId, ImageAsset Asset)> AddImageAsync(
        ImageLibraryCatalog catalog, string libId, string fileName, string caption, int w = 1600, int h = 900)
    {
        var (asset, error) = catalog.AddAsset(libId, fileName, "image/png", FakePng(w, h), "user_1");
        Assert.Null(error);
        Assert.NotNull(asset);
        if (caption.Length > 0) await catalog.UpdateAssetAsync(libId, asset!.AssetId, caption, null);
        else await catalog.WaitForProcessingAsync(asset!.AssetId, TimeSpan.FromSeconds(10));
        return (libId, asset!);
    }

    // ================= 上传 → 异步入库 =================

    [Fact]
    public void AddAsset_ReturnsProcessingImmediately_FileWritten()
    {
        // 用慢速 embedding，才能稳定地看到“上传请求不阻塞”的那个瞬间：
        // 没有它的话后台任务在断言之前就跑完了（状态已是 ready）
        var (catalog, store, root) = NewCatalog(TimeSpan.FromMilliseconds(300));
        var lib = catalog.CreateLibrary("公司图库", "", "user_1");

        var (asset, error) = catalog.AddAsset(lib.LibId, "meeting.png", "image/png", FakePng(1600, 900), "user_1");

        Assert.Null(error);
        Assert.NotNull(asset);
        Assert.Equal("processing", asset!.Status);
        // 文件立刻落盘（描述/向量化才是后台的），路径可解析
        var path = catalog.ResolveAssetPath(lib, asset);
        Assert.NotNull(path);
        Assert.True(File.Exists(path));
        Assert.StartsWith(Path.GetFullPath(root), Path.GetFullPath(path!));
        // 尺寸从文件头读出来（前端展示 + 技能判断会不会被拉糊都要用）
        Assert.Equal(1600, asset.Width);
        Assert.Equal(900, asset.Height);
        Assert.True(catalog.IsProcessing(asset.AssetId));
        Assert.Empty(store.Records);
    }

    [Fact]
    public async Task AddAsset_ThenReady_VectorWrittenUnderImgGroup()
    {
        var (catalog, store, _) = NewCatalog();
        var lib = catalog.CreateLibrary("公司图库", "", "user_1");
        var (_, asset) = await AddImageAsync(catalog, lib.LibId, "meeting.png", "现代会议室，长桌，落地窗，团队讨论");

        Assert.Equal("ready", asset.Status);
        var rec = Assert.Single(store.Records);
        // 群维度与 sender_type 是约定：普通群记忆检索会把 img 行排除（见两个 store 的 kb/img 过滤）
        Assert.Equal(ImageLibraryCatalog.ImgGroupPrefix + lib.LibId, rec.GroupId);
        Assert.Equal("img", rec.SenderType);
        Assert.Equal(asset.AssetId, rec.MessageId);
        Assert.Contains("现代会议室", rec.Content);
        // 文件名也参与向量化（描述没写清时兜住）
        Assert.Contains("meeting.png", rec.Content);
    }

    [Fact]
    public void AddAsset_RejectsNonImageAndOversizeAndMissingLibrary()
    {
        var (catalog, _, _) = NewCatalog();
        var lib = catalog.CreateLibrary("公司图库", "", "user_1");

        Assert.Contains("只支持图片", catalog.AddAsset(lib.LibId, "a.exe", "application/octet-stream", [1, 2, 3], null).Error);
        Assert.Contains("图库不存在", catalog.AddAsset("img_missing", "a.png", "image/png", [1, 2, 3], null).Error);
        Assert.Contains("内容为空", catalog.AddAsset(lib.LibId, "a.png", "image/png", [], null).Error);
    }

    // ================= 语义检索 =================

    [Fact]
    public async Task Search_ReturnsHitWithReadablePath()
    {
        var (catalog, _, _) = NewCatalog();
        var lib = catalog.CreateLibrary("公司图库", "", "user_1");
        var (_, asset) = await AddImageAsync(catalog, lib.LibId, "meeting.png", "现代会议室，长桌，落地窗");

        var hits = await catalog.SearchAsync([lib.LibId], "会议室", topK: 3, minScore: 0.25);

        var hit = Assert.Single(hits);
        Assert.Equal(asset.AssetId, hit.AssetId);
        Assert.Equal("公司图库", hit.LibName);
        Assert.Equal("现代会议室，长桌，落地窗", hit.Caption);
        // 技能要拿这个路径直接嵌入 —— 必须是真实存在、可读的文件
        Assert.True(File.Exists(hit.Path));
    }

    [Fact]
    public async Task Search_IgnoresNotReadyAssets()
    {
        var (catalog, store, _) = NewCatalog();
        var lib = catalog.CreateLibrary("公司图库", "", "user_1");
        var (_, asset) = await AddImageAsync(catalog, lib.LibId, "x.png", "描述");
        // 手动置为非 ready：真实场景对应“向量化失败”或“服务重启中断”。
        // （靠后台时序去构造是不稳的 —— 后台很可能早就跑完了）
        asset.Status = "error";
        asset.Error = "embedding 不可用";

        var hits = await catalog.SearchAsync([lib.LibId], "描述", topK: 3, minScore: 0.25);
        Assert.Empty(hits);
        // 向量还在表里，只是不再被当作可用配图
        Assert.Single(store.Records);
    }

    [Fact]
    public async Task Search_FallsBackToKeywordRecall()
    {
        var (catalog, store, _) = NewCatalog();
        var lib = catalog.CreateLibrary("公司图库", "", "user_1");
        await AddImageAsync(catalog, lib.LibId, "sku.png", "产品包装盒正面照，蓝色，含 SKU-2026 标签");
        // 向量检索空手（相似度低于阈值），但描述里**词面命中** SKU-2026 → 关键词召回要把它捞回来
        store.VectorSearchReturnsEmpty = true;

        var hits = await catalog.SearchAsync([lib.LibId], "SKU-2026", topK: 3, minScore: 0.9);
        Assert.Single(hits);
    }

    /// <summary>
    /// 关键词兜底**不得**在零词面重叠时也召回。
    ///
    /// <para>
    /// 钉住一个真实 bug：<see cref="AguiGroupChat.Agents.Bm25Ranker.Score"/> 是 sigmoid 归一化，
    /// 零重叠也返回 0.5；而兜底原本写的是“bm25 &gt; 0 才算命中” → 等于不筛，
    /// 于是<b>任何</b>查询都能把整个图库以 0.5 分召回：表现为“无意义关键词也配上了一张任意照片”，
    /// 并且 minScore 形同虚设（0.5 &gt; 默认 0.25）。
    /// </para>
    /// </summary>
    [Fact]
    public async Task Search_KeywordRecall_IgnoresZeroOverlap()
    {
        var (catalog, store, _) = NewCatalog();
        var lib = catalog.CreateLibrary("公司图库", "", "user_1");
        await AddImageAsync(catalog, lib.LibId, "people.png", "年度优秀员工颁奖合影，舞台红毯，五个人举着奖杯");
        store.VectorSearchReturnsEmpty = true;   // 向量这条路空手，只剩关键词兜底

        // 毫无词面交集的查询：不该“命中”任何图
        var hits = await catalog.SearchAsync([lib.LibId], "qzxv-不存在-9987", topK: 3, minScore: 0.25);
        Assert.Empty(hits);

        // 真有词面交集的查询：兜底仍然要能把它捞回来（别把功能一起修没了）
        var real = await catalog.SearchAsync([lib.LibId], "颁奖合影", topK: 3, minScore: 0.9);
        Assert.Single(real);
    }

    [Fact]
    public async Task Search_ScopedToOneLibrary()
    {
        var (catalog, _, _) = NewCatalog();
        var a = catalog.CreateLibrary("A 库", "", "user_1");
        var b = catalog.CreateLibrary("B 库", "", "user_1");
        await AddImageAsync(catalog, a.LibId, "a.png", "会议室");
        await AddImageAsync(catalog, b.LibId, "b.png", "会议室");

        var hits = await catalog.SearchAsync([b.LibId], "会议室", topK: 5, minScore: 0.25);
        Assert.All(hits, h => Assert.Equal(b.LibId, h.LibId));
    }

    // ================= 改描述 → 重新向量化 / 删除 =================

    [Fact]
    public async Task UpdateCaption_ReVectorizes()
    {
        var (catalog, store, _) = NewCatalog();
        var lib = catalog.CreateLibrary("公司图库", "", "user_1");
        var (_, asset) = await AddImageAsync(catalog, lib.LibId, "x.png", "旧描述");

        Assert.Null(await catalog.UpdateAssetAsync(lib.LibId, asset.AssetId, "新描述：数据中心机房", ["机房", "服务器"]));

        var rec = Assert.Single(store.Records);
        Assert.Contains("新描述", rec.Content);
        Assert.Contains("机房", rec.Content);
        Assert.Equal("ready", asset.Status);
    }

    [Fact]
    public async Task RemoveAsset_DropsVectorAndFile()
    {
        var (catalog, store, _) = NewCatalog();
        var lib = catalog.CreateLibrary("公司图库", "", "user_1");
        var (_, asset) = await AddImageAsync(catalog, lib.LibId, "x.png", "描述");
        var path = catalog.ResolveAssetPath(lib, asset)!;

        Assert.True(catalog.RemoveAsset(lib.LibId, asset.AssetId));

        Assert.Empty(store.Records);
        Assert.False(File.Exists(path));
        Assert.Empty(lib.Assets);
    }

    [Fact]
    public async Task RemoveLibrary_DropsVectorsAndFiles()
    {
        var (catalog, store, _) = NewCatalog();
        var lib = catalog.CreateLibrary("公司图库", "", "user_1");
        await AddImageAsync(catalog, lib.LibId, "x.png", "描述");
        var dir = Path.GetDirectoryName(catalog.ResolveAssetPath(lib, lib.Assets[0])!)!;

        Assert.True(catalog.RemoveLibrary(lib.LibId));

        Assert.Contains(ImageLibraryCatalog.ImgGroupPrefix + lib.LibId, store.RemovedGroups);
        Assert.False(Directory.Exists(dir));
        Assert.Null(catalog.GetLibrary(lib.LibId));
    }

    // ================= 权限（与知识库一致） =================

    [Fact]
    public void Permissions_MirrorKnowledgeBase()
    {
        var (catalog, _, _) = NewCatalog();
        var mine = catalog.CreateLibrary("我的", "", "user_1");
        var system = catalog.CreateLibrary("系统级", "", null);
        var shared = catalog.CreateLibrary("共享给群 G", "", "user_2");
        shared.SharedGroupIds = ["g1"];

        var u1 = new HashSet<string>(StringComparer.Ordinal) { "g1" };
        var other = new HashSet<string>(StringComparer.Ordinal) { "g9" };

        Assert.True(catalog.CanRead(mine, "user_1", other, false));      // 自己的
        Assert.False(catalog.CanWrite(mine, "user_2", false));           // 别人不能改
        Assert.True(catalog.CanRead(system, "user_3", other, false));    // 系统级人人可读
        Assert.False(catalog.CanWrite(system, "user_3", false));         // 但只读
        Assert.True(catalog.CanRead(shared, "user_3", u1, false));       // 共享群成员
        Assert.False(catalog.CanWrite(shared, "user_3", false));         // 群共享只读
        Assert.True(catalog.CanRead(mine, "admin", other, true));        // 管理员
        // 可见列表只回该用户看得见的
        var visible = catalog.ListLibraries("user_3", u1, false).Select(l => l.LibId).ToList();
        Assert.Contains(system.LibId, visible);
        Assert.Contains(shared.LibId, visible);
        Assert.DoesNotContain(mine.LibId, visible);
    }

    // ================= 检索范围句柄（技能回调的越权防线） =================

    [Fact]
    public void SearchScope_OnlyResolvesRegisteredHandles()
    {
        var (catalog, _, _) = NewCatalog();
        var handle = catalog.RegisterSearchScope(["img_a", "img_b"], "agent_1");

        Assert.Equal(["img_a", "img_b"], catalog.ResolveSearchScope(handle));
        // 技能（模型）自造 / 篡改的句柄一律不认 —— 这是防止“改入参读别人图库”的那道门
        Assert.Null(catalog.ResolveSearchScope("iscope_forged"));
        Assert.Null(catalog.ResolveSearchScope(null));
        Assert.Null(catalog.ResolveSearchScope(""));
    }

    // ================= 重启恢复 =================

    [Fact]
    public async Task RestoreAll_MarksInterruptedProcessingAsError()
    {
        var (catalog, _, _) = NewCatalog();
        var lib = catalog.CreateLibrary("公司图库", "", "user_1");
        var (_, asset) = await AddImageAsync(catalog, lib.LibId, "x.png", "描述");
        // 模拟“服务在向量化中途退出”：等后台跑完后手动置回 processing，
        // 再走一次与真实持久化相同的序列化/反序列化（快照必须是独立副本，否则后台写入会穿透）
        asset.Status = "processing";
        var json = System.Text.Json.JsonSerializer.Serialize(catalog.ListAll(), AguiGroupChat.Hub.Infra.AguiJson.Options);
        var snapshot = System.Text.Json.JsonSerializer.Deserialize<List<ImageLibrary>>(json, AguiGroupChat.Hub.Infra.AguiJson.Options)!;

        var (fresh, _, _) = NewCatalog();
        fresh.RestoreAll(snapshot);

        var restored = fresh.GetLibrary(lib.LibId);
        Assert.NotNull(restored);
        var restoredAsset = Assert.Single(restored!.Assets);
        Assert.Equal("error", restoredAsset.Status);
        Assert.Contains("重新上传", restoredAsset.Error);
    }
}

/// <summary>图片尺寸探测（只读文件头，不引图像库）。</summary>
public sealed class ImageSizeProbeTests
{
    private static byte[] Png(int w, int h)
    {
        var b = new byte[24];
        b[0] = 0x89; b[1] = 0x50; b[2] = 0x4E; b[3] = 0x47;
        b[16] = (byte)(w >> 24); b[17] = (byte)(w >> 16); b[18] = (byte)(w >> 8); b[19] = (byte)w;
        b[20] = (byte)(h >> 24); b[21] = (byte)(h >> 16); b[22] = (byte)(h >> 8); b[23] = (byte)h;
        return b;
    }

    [Fact]
    public void ReadsPng()
    {
        Assert.True(ImageSizeProbe.TryRead(Png(1920, 1080), out var w, out var h));
        Assert.Equal(1920, w);
        Assert.Equal(1080, h);
    }

    [Fact]
    public void ReadsGif()
    {
        var b = new byte[20];
        b[0] = (byte)'G'; b[1] = (byte)'I'; b[2] = (byte)'F'; b[3] = (byte)'8';
        b[6] = 0x40; b[7] = 0x01;    // 320
        b[8] = 0xC8; b[9] = 0x00;    // 200
        Assert.True(ImageSizeProbe.TryRead(b, out var w, out var h));
        Assert.Equal(320, w);
        Assert.Equal(200, h);
    }

    [Fact]
    public void ReadsBmp()
    {
        var b = new byte[30];
        b[0] = (byte)'B'; b[1] = (byte)'M';
        b[18] = 0x20; b[19] = 0x03;  // 800
        b[22] = 0x58; b[23] = 0x02;  // 600
        Assert.True(ImageSizeProbe.TryRead(b, out var w, out var h));
        Assert.Equal(800, w);
        Assert.Equal(600, h);
    }

    /// <summary>JPEG 要按段长跳，不能逐字节找 FFC0 —— 段内数据里到处都是 FF。</summary>
    [Fact]
    public void ReadsJpeg_ByScanningSegments()
    {
        var b = new byte[40];
        b[0] = 0xFF; b[1] = 0xD8;              // SOI
        b[2] = 0xFF; b[3] = 0xE0; b[4] = 0x00; b[5] = 0x04; b[6] = 0x11; b[7] = 0x22;   // APP0，跳 6 字节
        b[8] = 0xFF; b[9] = 0xC0; b[10] = 0x00; b[11] = 0x11; b[12] = 0x08;            // SOF0
        b[13] = 0x04; b[14] = 0x38;            // 高 1080
        b[15] = 0x07; b[16] = 0x80;            // 宽 1920
        Assert.True(ImageSizeProbe.TryRead(b, out var w, out var h));
        Assert.Equal(1920, w);
        Assert.Equal(1080, h);
    }

    [Fact]
    public void UnknownFormat_ReturnsFalse()
    {
        Assert.False(ImageSizeProbe.TryRead(Encoding.UTF8.GetBytes("这不是图片，只是一段文本内容"), out _, out _));
        Assert.False(ImageSizeProbe.TryRead([1, 2, 3], out _, out _));
    }
}
