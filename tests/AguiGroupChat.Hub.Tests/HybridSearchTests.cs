using System.Text;
using AguiGroupChat.Agents;
using AguiGroupChat.Hub.Models;
using AguiGroupChat.Hub.Persistence;
using AguiGroupChat.Hub.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>混合检索（2.1）测试：BM25 评分 / 融合精排（不改变召回集合，只在集合内调序）。</summary>
public sealed class HybridSearchTests
{
    // ================= Bm25Ranker =================

    [Fact]
    public void Score_ExactTermMatch_HigherThanNoMatch()
    {
        var same = Bm25Ranker.Score("报销流程 发票 审批", "报销需要先提交发票，然后走审批流程，最后到账");
        var unrelated = Bm25Ranker.Score("报销流程 发票 审批", "今天天气很好，适合去公园散步");
        Assert.True(same > unrelated, $"same={same} unrelated={unrelated}");
    }

    [Fact]
    public void Score_MoreTermOverlap_Higher()
    {
        var two = Bm25Ranker.Score("方案 数据库 缓存", "我们用某个方案，选了数据库做存储");
        var one = Bm25Ranker.Score("方案 数据库 缓存", "我们讨论了很久这个话题，没有什么结论");
        Assert.True(two > one);
    }

    [Fact]
    public void FusedScore_ImportanceBoosts()
    {
        // 重要级 2 的记忆即使 cosine 略低也应得到更高融合分（在集合内优先）
        var imp = Bm25Ranker.FusedScore(0.8, 0.3, importance: 2, bm25Weight: 0.35);
        var norm = Bm25Ranker.FusedScore(0.8, 0.3, importance: 0, bm25Weight: 0.35);
        Assert.True(imp > norm);
    }

    // ================= HybridRerank（在集合内调序） =================

    private sealed class MemStore : IMessageMemoryStore
    {
        public List<MessageMemoryHit> Hits { get; set; } = [];
        public void EnsureSchema() { }
        public void Upsert(MessageMemoryRecord record) { }
        public void Remove(string groupId, string messageId) { }
        public void RemoveGroup(string groupId) { }
        public void ClearAll() { }
        public IReadOnlyList<MessageMemoryHit> Search(string groupId, string? agentId, float[] embedding, int topK, double minScore, string scope) => Hits;
        public IReadOnlyList<MessageMemoryHit> SearchPerson(string personId, string currentGroupId, float[] embedding, int topK, double minScore) => Hits;
        public IReadOnlyList<MessageMemoryItem> ListMessages(string? groupId, string? senderId, string? keyword, int limit, int offset) => [];
        public long CountMessages(string? groupId, string? senderId, string? keyword) => 0;
        public IReadOnlyList<MessageMemoryGroupStat> GroupStats(long nowMs) => [];
        public bool DeleteByMessageId(string messageId) => false;
        public bool UpdateImportance(string messageId, int importance) => false;
        public int SetExpiry(string? groupId, long? expiresAt, long nowMs) => 0;
        public int PruneExpired(long nowMs) => 0;
    }

    private sealed class FixedEmbedding : IEmbeddingProvider
    {
        public Task<float[]?> EmbedAsync(string text, CancellationToken ct = default) => Task.FromResult<float[]?>(new float[4]);
        public void Dispose() { }
    }

    [Fact]
    public async Task SearchAsync_HybridReranks_WithinSameSet()
    {
        var store = new MemStore();
        // 稠密检索已命中两条；第二条更含查询关键词，但第二条余弦稍低
        store.Hits =
        [
            new MessageMemoryHit("m1", "我们评估了技术方案", "u1", 1, Score: 0.91, Importance: 0, "g"),
            new MessageMemoryHit("m2", "方案推荐：数据库选型用 Postgres，缓存用 Redis", "u2", 2, Score: 0.89, Importance: 0, "g"),
        ];
        var options = new AgentOptions { Provider = "mock", Memory = new MemoryOptions { Enabled = true, HybridSearch = true } };
        var services = new ServiceCollection().AddSingleton<IMessageMemoryStore>(store).AddSingleton<IEmbeddingProvider>(new FixedEmbedding()).BuildServiceProvider();
        var memory = new AgentMessageMemory(store, options, NullLogger<AgentMessageMemory>.Instance, new FixedEmbedding());

        var hits = await memory.SearchAsync("g", "agent_x", "Postgres 缓存 数据库选型", CancellationToken.None);
        // 返回集合与条数不变
        Assert.Equal(2, hits.Count);
        Assert.Equal(new[] { "m1", "m2" }.ToHashSet(), hits.Select(h => h.MessageId).ToHashSet());
        // 精词命中的 m2 应排到最前
        Assert.Equal("m2", hits[0].MessageId);
    }
}
