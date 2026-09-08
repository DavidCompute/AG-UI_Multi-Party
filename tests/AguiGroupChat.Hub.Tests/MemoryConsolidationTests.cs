using AguiGroupChat.Agents;
using AguiGroupChat.Hub.Models;
using AguiGroupChat.Hub.Persistence;
using AguiGroupChat.Hub.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>记忆 / 结论沉淀（1.3）测试：原始文本入库 + 群「关键」记忆聚合为知识文档。</summary>
public sealed class MemoryConsolidationTests
{
    private sealed class MemStore : IMessageMemoryStore
    {
        public List<MessageMemoryRecord> Records { get; } = [];
        public List<MessageMemoryItem> Items { get; } = [];

        public void EnsureSchema() { }
        public void Upsert(MessageMemoryRecord record) => Records.Add(record);
        public void Remove(string groupId, string messageId) { }
        public void RemoveGroup(string groupId) { }
        public void ClearAll() { }
        public IReadOnlyList<MessageMemoryItem> ListMessages(string? groupId, string? senderId, string? keyword, int limit, int offset)
            => Items.Where(m => groupId is null || m.GroupId == groupId)
                .Where(m => senderId is null || m.SenderId == senderId)
                .Where(m => keyword is null || m.Content.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                .Skip(offset).Take(limit).ToList();
        public long CountMessages(string? groupId, string? senderId, string? keyword) => 0;
        public IReadOnlyList<MessageMemoryGroupStat> GroupStats(long nowMs) => [];
        public bool DeleteByMessageId(string messageId) => false;
        public bool UpdateImportance(string messageId, int importance) => false;
        public int SetExpiry(string? groupId, long? expiresAt, long nowMs) => 0;
        public int PruneExpired(long nowMs) => 0;
        public IReadOnlyList<MessageMemoryHit> Search(string groupId, string? agentId, float[] embedding, int topK, double minScore, string scope) => [];
        public IReadOnlyList<MessageMemoryHit> SearchPerson(string personId, string currentGroupId, float[] embedding, int topK, double minScore) => [];
    }

    private sealed class FakeEmbedding : IEmbeddingProvider
    {
        public Task<float[]?> EmbedAsync(string text, CancellationToken ct = default)
            => Task.FromResult<float[]?>(new float[8]);
        public void Dispose() { }
    }

    private static (KnowledgeBaseCatalog Catalog, MemStore Store, string KbId) Setup()
    {
        var dir = Path.Combine(Path.GetTempPath(), "agui-kb-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var store = new MemStore();
        var services = new ServiceCollection();
        services.AddSingleton(new AttachmentStore(dir));
        services.AddSingleton<IMessageMemoryStore>(store);
        services.AddSingleton<IEmbeddingProvider>(new FakeEmbedding());
        var sp = services.BuildServiceProvider();
        var catalog = new KnowledgeBaseCatalog(new AgentOptions(), sp, NullLoggerFactory.Instance);
        var kb = catalog.CreateKb("结论库", "", ownerId: "user_1");
        return (catalog, store, kb.KbId);
    }

    [Fact]
    public async Task AddTextDocument_ChunksAndStores()
    {
        var (catalog, store, kbId) = Setup();
        var text = string.Concat(System.Linq.Enumerable.Repeat("这个需求我们决定采用方案B。", 40)); // 长文本 → 多切片
        var (doc, error) = await catalog.AddTextDocumentAsync(kbId, "结论.md", text);
        Assert.Null(error);
        Assert.NotNull(doc);
        Assert.Equal("processing", doc!.Status);
        await catalog.WaitForDocumentAsync(doc.DocId);
        Assert.Equal("ready", doc.Status);
        Assert.True(doc.ChunkCount >= 1);
        var vecStore = store.Records.Where(r => r.GroupId == KnowledgeBaseCatalog.KbGroupPrefix + kbId).ToList();
        Assert.Equal(doc.ChunkCount, vecStore.Count);
    }

    [Fact]
    public async Task ConsolidateGroupMemories_ProducesKbDoc_FromCriticalMemories()
    {
        var (catalog, store, kbId) = Setup();
        const string groupId = "group_1";
        store.Items.Add(new MessageMemoryItem("m1", groupId, "main", "user_1", "user", "结论一：采用方案 B", 1, MemoryImportance.Critical, null));
        store.Items.Add(new MessageMemoryItem("m2", groupId, "main", "user_2", "user", "结论二：预算控制在 50w", 2, MemoryImportance.Critical, null));
        store.Items.Add(new MessageMemoryItem("m3", groupId, "main", "user_1", "user", "普通讨论，不应沉淀", 3, MemoryImportance.Normal, null));

        var (doc, error, count) = await catalog.ConsolidateGroupMemoriesAsync(groupId, kbId, store);
        Assert.Null(error);
        Assert.Equal(2, count); // 仅「关键」级别被沉淀
        Assert.NotNull(doc);
        await catalog.WaitForDocumentAsync(doc!.DocId);
        Assert.Equal("ready", doc.Status);
    }

    [Fact]
    public async Task ConsolidateGroupMemories_NoCritical_ReturnsError()
    {
        var (catalog, store, kbId) = Setup();
        store.Items.Add(new MessageMemoryItem("m1", "group_1", "main", "user_1", "user", "普通讨论", 1, MemoryImportance.Normal, null));

        var (doc, error, count) = await catalog.ConsolidateGroupMemoriesAsync("group_1", kbId, store);
        Assert.Null(doc);
        Assert.Contains("暂无标记为「关键」", error);
        Assert.Equal(0, count);
    }

    // ============ 增量沉淀（sinceMs 水位，供自动周期沉淀去重） ============

    [Fact]
    public async Task ConsolidateSince_OnlyNewerCritical_ReturnsWatermark()
    {
        var (catalog, store, kbId) = Setup();
        const string groupId = "group_inc";
        store.Items.Add(new MessageMemoryItem("old1", groupId, "main", "user_1", "user", "已沉淀的老结论", 1, MemoryImportance.Critical, null));
        store.Items.Add(new MessageMemoryItem("old2", groupId, "main", "user_2", "user", "第二条老结论", 2, MemoryImportance.Critical, null));
        store.Items.Add(new MessageMemoryItem("new1", groupId, "main", "user_1", "user", "新增关键结论", 10, MemoryImportance.Critical, null));
        store.Items.Add(new MessageMemoryItem("new2", groupId, "main", "user_1", "user", "再一条新结论", 20, MemoryImportance.Critical, null));

        // 水位=2：只应沉淀时间戳晚于 2 的两条，返回的新水位应为 20
        var (doc, error, count, watermark) = await catalog.ConsolidateGroupMemoriesSinceAsync(groupId, kbId, store, sinceMs: 2);
        Assert.Null(error);
        Assert.Equal(2, count);
        Assert.NotNull(doc);
        Assert.Equal(20, watermark);
        await catalog.WaitForDocumentAsync(doc!.DocId);
        Assert.Equal("ready", doc.Status);
    }

    [Fact]
    public async Task ConsolidateSince_NoNewMemory_ReturnsNullWithoutError()
    {
        var (catalog, store, kbId) = Setup();
        store.Items.Add(new MessageMemoryItem("old1", "group_inc", "main", "user_1", "user", "已沉淀结论", 1, MemoryImportance.Critical, null));

        var (doc, error, count, watermark) = await catalog.ConsolidateGroupMemoriesSinceAsync("group_inc", kbId, store, sinceMs: 1);
        Assert.Null(doc);
        Assert.Null(error); // 增量模式无新记忆不是错误
        Assert.Equal(0, count);
        Assert.Null(watermark);
    }

    // ============ 自动周期沉淀服务（按群水位扫描） ============

    [Fact]
    public async Task AutoSweep_ConsolidatesNewCritical_ThenSkipsByWatermark()
    {
        var f = new HubFixture();
        var group = await f.Hub.CreateGroupAsync(new GroupCreateRequest { GroupName = "自动沉淀群", OwnerId = "user_1", MemberIds = [] });
        var (catalog, store, _) = Setup();
        store.Items.Add(new MessageMemoryItem("a1", group.GroupId, "main", "user_1", "user", "结论甲：上线方案确定", 1, MemoryImportance.Critical, null));
        store.Items.Add(new MessageMemoryItem("a2", group.GroupId, "main", "user_2", "user", "结论乙：预算确认", 2, MemoryImportance.Critical, null));
        store.Items.Add(new MessageMemoryItem("a3", group.GroupId, "main", "user_1", "user", "普通闲聊", 3, MemoryImportance.Normal, null));

        var options = new AgentOptions { Memory = new MemoryOptions { Enabled = true, AutoConsolidateEnabled = true, AutoConsolidateIntervalHours = 1 } };
        // 服务与目录共用同一 MemStore 实例（服务用它读水位、目录用它切片向量化）
        var svcSp = new ServiceCollection()
            .AddSingleton<IMessageMemoryStore>(store)
            .AddSingleton<IEmbeddingProvider>(new FakeEmbedding())
            .BuildServiceProvider();
        var svc = new MemoryAutoConsolidationService(f.Store, catalog, options, svcSp, NullLogger<MemoryAutoConsolidationService>.Instance);

        // 第一轮：沉淀全部关键记忆（2 条），水位推进
        var first = await svc.RunSweepAsync(CancellationToken.None);
        Assert.Equal(1, first);
        var rows = (System.Collections.IEnumerable)svc.SnapshotState();
        var row = System.Linq.Enumerable.Cast<AutoConsolidationRow>(rows).Single();
        Assert.Equal(group.GroupId, row.GroupId);
        Assert.Equal(2, row.WatermarkMs);

        // 第二轮：没有更新的关键记忆 → 不再产生文档
        var second = await svc.RunSweepAsync(CancellationToken.None);
        Assert.Equal(0, second);
        await catalog.WaitForDocumentAsync(catalog.GetKb(row.KbId)!.Documents[0].DocId); // 确保后台向量化完成后再断言
        var doc = Assert.Single(catalog.GetKb(row.KbId)!.Documents);
        Assert.Equal("ready", doc.Status);
    }
}
