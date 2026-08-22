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
}
