using AguiGroupChat.Agents;
using AguiGroupChat.Hub.Models;
using AguiGroupChat.Hub.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>
/// 记忆写入侧「重复编码」去重（ROADMAP §二）。
///
/// <para>
/// 不变量：写入向量化之前，先用<b>实际入库文本</b>（净化 + 截断后）查进程内 LRU 缓存；
/// 命中即复用向量、跳过 embedding——相同文本在同一模型下向量恒等，故语义完全等价
/// （<b>每条消息仍各自落库、按自己的 messageId 可被检索</b>），只是省掉重复推理。
/// 缓存有界（LRU），容量 0 时关闭、行为与旧版完全一致。
/// </para>
/// </summary>
public sealed class MemoryDedupCacheTests
{
    // ================= 缓存本体（纯单元） =================

    [Fact]
    public void Cache_StoresAndReturnsVector()
    {
        var cache = new MemoryEmbeddingCache(4);
        var v = new float[] { 1, 2, 3 };
        cache.Set("a", v);
        Assert.Same(v, cache.TryGet("a"));
        Assert.Null(cache.TryGet("missing"));
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void Cache_EvictsLeastRecentlyUsed_WhenOverCapacity()
    {
        var cache = new MemoryEmbeddingCache(2);
        cache.Set("a", new float[] { 1 });
        cache.Set("b", new float[] { 2 });
        // 访问 a → a 变为最近使用，b 成为最久未使用
        Assert.NotNull(cache.TryGet("a"));
        cache.Set("c", new float[] { 3 });

        Assert.Equal(2, cache.Count);
        Assert.NotNull(cache.TryGet("a"));
        Assert.Null(cache.TryGet("b")); // 被淘汰
        Assert.NotNull(cache.TryGet("c"));
    }

    [Fact]
    public void Cache_IgnoresEmptyVector()
    {
        var cache = new MemoryEmbeddingCache(4);
        cache.Set("a", System.Array.Empty<float>());
        Assert.Equal(0, cache.Count);
        Assert.Null(cache.TryGet("a"));
    }

    [Fact]
    public void DefaultOption_EnablesCache_WithSaneCapacity()
    {
        Assert.Equal(512, new AgentOptions().Memory.WriteEmbeddingCacheSize);
    }

    // ================= 写入路径（端到端） =================

    [Fact]
    public async Task Remember_IdenticalContent_EmbedsOnce_ButStoresEveryMessage()
    {
        var store = new CapturingMemory();
        var embedding = new CountingEmbedding();
        var metrics = new MetricsService();
        using var memory = NewMemory(store, embedding, metrics);

        memory.Remember(new MessageMemoryEntry("m1", "g", "main", "agent_a", "Agent", "收到，我来处理", 1));
        memory.Remember(new MessageMemoryEntry("m2", "g", "main", "agent_b", "Agent", "收到，我来处理", 2));

        var records = await WaitForRecordsAsync(store, 2);
        Assert.Equal(2, records.Count);                    // 每条消息都保留自己的记忆（可分别检索）
        Assert.Equal(1, embedding.Calls);                  // 但只向量化了一次
        Assert.Equal(records[0].Embedding, records[1].Embedding); // 复用同一向量
        Assert.Equal(records[0].Content, records[1].Content);
        Assert.Contains("knowgath_memory_embedding_cache_hits_total 1", metrics.RenderPrometheusText());
        Assert.Contains("knowgath_memory_embedding_cache_misses_total 1", metrics.RenderPrometheusText());
    }

    [Fact]
    public async Task Remember_DifferentContent_EmbedsEach()
    {
        var store = new CapturingMemory();
        var embedding = new CountingEmbedding();
        using var memory = NewMemory(store, embedding, new MetricsService());

        memory.Remember(new MessageMemoryEntry("m1", "g", "main", "u1", "User", "第一个问题", 1));
        memory.Remember(new MessageMemoryEntry("m2", "g", "main", "u1", "User", "第二个问题", 2));

        var records = await WaitForRecordsAsync(store, 2);
        Assert.Equal(2, records.Count);
        Assert.Equal(2, embedding.Calls);
    }

    [Fact]
    public async Task Remember_CacheDisabled_EmbedsEveryMessage()
    {
        var store = new CapturingMemory();
        var embedding = new CountingEmbedding();
        using var memory = NewMemory(store, embedding, new MetricsService(),
            new MemoryOptions { Enabled = true, WriteEmbeddingCacheSize = 0 });

        memory.Remember(new MessageMemoryEntry("m1", "g", "main", "u1", "User", "重复的话", 1));
        memory.Remember(new MessageMemoryEntry("m2", "g", "main", "u1", "User", "重复的话", 2));

        await WaitForRecordsAsync(store, 2);
        Assert.Equal(2, embedding.Calls); // 关闭缓存 → 不做去重，行为与旧版一致
    }

    [Fact]
    public async Task Remember_TruncatedToSameText_IsDeduplicated()
    {
        // 不同原文，但按 MaxWriteChars 截断后相同 → 向量输入一致 → 应命中缓存
        var store = new CapturingMemory();
        var embedding = new CountingEmbedding();
        using var memory = NewMemory(store, embedding, new MetricsService(),
            new MemoryOptions { Enabled = true, MaxWriteChars = 4, WriteEmbeddingCacheSize = 8 });

        memory.Remember(new MessageMemoryEntry("m1", "g", "main", "u1", "User", "前缀一样甲乙丙", 1));
        memory.Remember(new MessageMemoryEntry("m2", "g", "main", "u1", "User", "前缀一样丁戊己", 2));

        var records = await WaitForRecordsAsync(store, 2);
        Assert.Equal(2, records.Count);
        Assert.Equal("前缀一样", records[0].Content);
        Assert.Equal(1, embedding.Calls);
    }

    // ================= 导入路径 =================

    [Fact]
    public async Task Import_IdenticalContent_ReusesCachedVector()
    {
        var store = new CapturingMemory();
        var embedding = new CountingEmbedding();
        using var memory = NewMemory(store, embedding, new MetricsService());

        var items = new List<MessageMemoryItem>
        {
            new("i1", "g", "main", "u1", "User", "跨实例导入的内容", 10, MemoryImportance.Normal, null),
            new("i2", "g", "main", "u1", "User", "跨实例导入的内容", 11, MemoryImportance.Normal, null),
        };
        var n = await memory.ImportMemoriesAsync(items, CancellationToken.None);

        Assert.Equal(2, n);
        Assert.Equal(1, embedding.Calls);
        Assert.Equal(2, store.Records.Count);
    }

    // ================= 夹具 =================

    private static AgentMessageMemory NewMemory(CapturingMemory store, CountingEmbedding embedding, MetricsService metrics, MemoryOptions? memory = null)
        => new(store, new AgentOptions { Provider = "mock", Memory = memory ?? new MemoryOptions { Enabled = true } },
            NullLogger<AgentMessageMemory>.Instance, embedding, authorProfileResolver: null, gates: null, metrics: metrics);

    private sealed class CountingEmbedding : IEmbeddingProvider
    {
        private int _calls;
        public int Calls => _calls;
        public Task<float[]?> EmbedAsync(string text, CancellationToken ct = default)
        {
            var n = Interlocked.Increment(ref _calls);
            // 每次调用返回可区分向量：两处向量相等 ⇒ 必然复用了同一次结果
            return Task.FromResult<float[]?>([n, 0, 0, 0]);
        }
        public void Dispose() { }
    }

    private sealed class CapturingMemory : IMessageMemoryStore
    {
        public List<MessageMemoryRecord> Records { get; } = [];
        public void EnsureSchema() { }
        public void Upsert(MessageMemoryRecord record) { lock (Records) Records.Add(record); }
        public void Remove(string groupId, string messageId) { }
        public void RemoveGroup(string groupId) { }
        public void ClearAll() { }
        public IReadOnlyList<MessageMemoryHit> Search(string groupId, string? agentId, float[] embedding, int topK, double minScore, string scope) => [];
        public IReadOnlyList<MessageMemoryHit> SearchPerson(string personId, string currentGroupId, float[] embedding, int topK, double minScore) => [];
        public IReadOnlyList<MessageMemoryItem> ListMessages(string? groupId, string? senderId, string? keyword, int limit, int offset) => [];
        public long CountMessages(string? groupId, string? senderId, string? keyword) => 0;
        public IReadOnlyList<MessageMemoryGroupStat> GroupStats(long nowMs) => [];
        public bool DeleteByMessageId(string messageId) => false;
        public bool UpdateImportance(string messageId, int importance) => false;
        public int SetExpiry(string? groupId, long? expiresAt, long nowMs) => 0;
        public int PruneExpired(long nowMs) => 0;
    }

    private static async Task<List<MessageMemoryRecord>> WaitForRecordsAsync(CapturingMemory store, int expected)
    {
        for (var i = 0; i < 100; i++)
        {
            lock (store.Records) { if (store.Records.Count >= expected) return store.Records.ToList(); }
            await Task.Delay(50);
        }
        lock (store.Records) return store.Records.ToList();
    }
}
