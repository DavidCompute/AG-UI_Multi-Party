using AguiGroupChat.Agents;
using AguiGroupChat.Hub.Models;
using AguiGroupChat.Hub.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>
/// 记忆向量化的<b>输入长度</b>预算。
///
/// <para>
/// 起因是一次真实的“语义记忆检索失败：HttpClient.Timeout of 60 seconds elapsing”。实测（4 核 CPU + bge-m3）：
/// embedding 耗时由输入长度主导，约 <b>8.5ms/字符</b> —— 6 字约 0.3 秒，1500 字要 12.7 秒。
/// 而写入路径原本把<b>整条消息原文不限长</b>送进 embedding（一条 5000 字的回复要 40 秒以上），
/// 检索路径的 query 上限是 2000 字（≈17 秒/条 × 一轮约 4 条检索），两者叠加就把 60 秒超时撞穿了。
/// </para>
///
/// <para>
/// 这里钉住两条不变量：① 写入侧入库文本与向量输入<b>完全一致</b>（否则检索命中却对不上内容）；
/// ② 写入与检索两侧都真的按各自的字符上限截断，且默认值是收紧后的那一组。
/// </para>
/// </summary>
public sealed class MemorySizeBudgetTests
{
    private const int Cap = 800; // 与 MemoryOptions.MaxWriteChars 默认值一致

    [Fact]
    public void Defaults_AreTheTightenedCaps()
    {
        var m = new AgentOptions().Memory;
        Assert.Equal(500, m.MaxQueryChars);                 // 旧值 2000 → 单条检索可达 17 秒
        Assert.Equal(Cap, m.MaxWriteChars);                 // 新增：写入侧原本完全不限长
        Assert.Equal(1_500, m.MaxCharsPerMemory);
        Assert.Equal(5, m.EmbeddingConnectTimeoutSeconds);  // 把“连不上”与“排队中”分开
        Assert.Equal(10, m.SlowEmbeddingWarnSeconds);
    }

    /// <summary>记录每次向量化实际收到的文本，并记录落库的文本，用于比对两者是否一致。</summary>
    private sealed class CapturingMemory : IMessageMemoryStore
    {
        public List<string> EmbeddedTexts { get; } = [];
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

    private sealed class CapturingEmbedding : IEmbeddingProvider
    {
        private readonly List<string> _sink;
        public CapturingEmbedding(List<string> sink) => _sink = sink;
        public Task<float[]?> EmbedAsync(string text, CancellationToken ct = default)
        {
            lock (_sink) _sink.Add(text ?? "");
            return Task.FromResult<float[]?>(new float[4]);
        }
        public void Dispose() { }
    }

    private static AgentMessageMemory NewMemory(CapturingMemory store, List<string> embedded, MemoryOptions? memory = null)
    {
        var options = new AgentOptions { Provider = "mock", Memory = memory ?? new MemoryOptions { Enabled = true } };
        return new AgentMessageMemory(store, options, NullLogger<AgentMessageMemory>.Instance, new CapturingEmbedding(embedded));
    }

    [Fact]
    public async Task Remember_TruncatesBothStoredAndEmbeddedText()
    {
        var store = new CapturingMemory();
        var embedded = store.EmbeddedTexts;
        var memory = NewMemory(store, embedded);

        // 一条 5000 字的“长回复”：按实测比例，不截断就要 40 秒以上才能向量化完
        var longContent = new string('长', 5_000);
        memory.Remember(new MessageMemoryEntry("m1", "g", "main", "agent_a", "Agent", longContent, 1_700_000_000_000));

        var record = await WaitForRecordAsync(store);
        Assert.NotNull(record);

        // 入库文本与向量输入必须一致，且都被截到上限
        Assert.Equal(Cap, record!.Content.Length);
        var embeddedText = Assert.Single(embedded);
        Assert.Equal(record.Content, embeddedText);
    }

    [Fact]
    public async Task Remember_ShortContent_IsStoredUnchanged()
    {
        var store = new CapturingMemory();
        var memory = NewMemory(store, store.EmbeddedTexts);
        memory.Remember(new MessageMemoryEntry("m2", "g", "main", "u1", "User", "我们约定周五交稿", 1_700_000_000_000));

        var record = await WaitForRecordAsync(store);
        Assert.NotNull(record);
        Assert.Equal("我们约定周五交稿", record!.Content);
    }

    [Fact]
    public async Task SearchAsync_TruncatesTheQuery()
    {
        var store = new CapturingMemory();
        var embedded = store.EmbeddedTexts;
        var memory = NewMemory(store, embedded);

        // 检索 query 按 MaxQueryChars 截断：不截的话单条就要十几秒，一轮约 4 条检索直接撞穿超时
        await memory.SearchAsync("g", "agent_a", new string('长', 5_000), CancellationToken.None);

        var embeddedText = Assert.Single(embedded);
        Assert.Equal(500, embeddedText.Length);
    }

    [Fact]
    public async Task SearchAsync_HonorsCustomQueryCap()
    {
        var store = new CapturingMemory();
        var embedded = store.EmbeddedTexts;
        var memory = NewMemory(store, embedded, new MemoryOptions { Enabled = true, MaxQueryChars = 128 });

        await memory.SearchAsync("g", "agent_a", new string('长', 5_000), CancellationToken.None);

        Assert.Equal(128, Assert.Single(embedded).Length);
    }

    [Fact]
    public async Task Remember_HonorsCustomWriteCap()
    {
        var store = new CapturingMemory();
        var memory = NewMemory(store, store.EmbeddedTexts, new MemoryOptions { Enabled = true, MaxWriteChars = 64 });
        memory.Remember(new MessageMemoryEntry("m3", "g", "main", "u1", "User", new string('长', 1_000), 1_700_000_000_000));

        var record = await WaitForRecordAsync(store);
        Assert.NotNull(record);
        Assert.Equal(64, record!.Content.Length);
    }

    /// <summary>等待后台写入消费者落库（fire-and-forget，给足 3 秒轮询）。</summary>
    private static async Task<MessageMemoryRecord?> WaitForRecordAsync(CapturingMemory store)
    {
        for (var i = 0; i < 60; i++)
        {
            lock (store.Records) { if (store.Records.Count > 0) return store.Records[0]; }
            await Task.Delay(50);
        }
        return null;
    }
}
