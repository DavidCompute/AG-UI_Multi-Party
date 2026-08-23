using AguiGroupChat.Agents;
using AguiGroupChat.Hub.Agents;
using AguiGroupChat.Hub.Models;
using AguiGroupChat.Hub.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>
/// 跨实例记忆同步（2.3）测试：导出「记忆即数据包」 + 导入（向量重算 / 按 messageId 去重 / sinceMs 增量）。
/// 用内存存储模拟两个实例，各自用 mock embedding，验证文本记忆可移植迁移。
/// </summary>
public sealed class MessageMemorySyncTests
{
    /// <summary>内存记忆存储（支持导出所需的 ListMessages / GetByMessageId 与导入 Upsert）。</summary>
    private sealed class MemStore : IMessageMemoryStore
    {
        public List<MessageMemoryRecord> Records { get; } = [];

        public void EnsureSchema() { }
        public void Upsert(MessageMemoryRecord record)
        {
            var i = Records.FindIndex(r => r.MessageId == record.MessageId);
            if (i >= 0) Records[i] = record; else Records.Add(record);
        }
        public void Remove(string groupId, string messageId) { }
        public void RemoveGroup(string groupId) { Records.RemoveAll(r => r.GroupId == groupId); }
        public void ClearAll() => Records.Clear();

        public IReadOnlyList<MessageMemoryItem> ListMessages(string? groupId, string? senderId, string? keyword, int limit, int offset)
            => Records
                .Where(m => groupId is null || m.GroupId == groupId)
                .Where(m => senderId is null || m.SenderId == senderId)
                .Where(m => keyword is null || m.Content.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(m => m.Timestamp)
                .Skip(offset).Take(Math.Min(limit, 5000))
                .Select(r => new MessageMemoryItem(r.MessageId, r.GroupId, r.TopicId, r.SenderId, r.SenderType, r.Content, r.Timestamp, r.Importance, r.ExpiresAt))
                .ToList();

        public long CountMessages(string? groupId, string? senderId, string? keyword) => 0;
        public IReadOnlyList<MessageMemoryGroupStat> GroupStats(long nowMs) => [];
        public MessageMemoryItem? GetByMessageId(string messageId)
        {
            var r = Records.FirstOrDefault(x => x.MessageId == messageId);
            return r is null ? null : new MessageMemoryItem(r.MessageId, r.GroupId, r.TopicId, r.SenderId, r.SenderType, r.Content, r.Timestamp, r.Importance, r.ExpiresAt);
        }
        public bool DeleteByMessageId(string messageId) => Records.RemoveAll(r => r.MessageId == messageId) > 0;
        public bool UpdateImportance(string messageId, int importance) { var i = Records.FindIndex(r => r.MessageId == messageId); if (i < 0) return false; var r = Records[i]; Records[i] = new MessageMemoryRecord(r.MessageId, r.GroupId, r.TopicId, r.SenderId, r.SenderType, r.Content, r.Embedding, r.Timestamp, importance, r.ExpiresAt); return true; }
        public int SetExpiry(string? groupId, long? expiresAt, long nowMs) { Records.RemoveAll(r => (groupId is null || r.GroupId == groupId) && (!expiresAt.HasValue || r.ExpiresAt != expiresAt)); return 0; }
        public int PruneExpired(long nowMs) => Records.RemoveAll(r => r.ExpiresAt is { } e && e < nowMs);
        public IReadOnlyList<MessageMemoryHit> Search(string groupId, string? agentId, float[] embedding, int topK, double minScore, string scope) => [];
        public IReadOnlyList<MessageMemoryHit> SearchPerson(string personId, string currentGroupId, float[] embedding, int topK, double minScore) => [];
    }

    private sealed class FakeEmbedding : IEmbeddingProvider
    {
        public Task<float[]?> EmbedAsync(string text, CancellationToken ct = default) => Task.FromResult<float[]?>(new float[8]);
        public void Dispose() { }
    }

    private static AgentMessageMemory CreateMemory(IMessageMemoryStore store)
        => new(store, new AgentOptions { Memory = new MemoryOptions { Enabled = true, EmbeddingDimensions = 8 } }, NullLogger<AgentMessageMemory>.Instance, new FakeEmbedding());

    [Fact]
    public async Task ExportImport_RoundTripsMemoriesAcrossInstances()
    {
        var sourceStore = new MemStore();
        var targetStore = new MemStore();
        using var source = CreateMemory(sourceStore);
        using var target = CreateMemory(targetStore);

        // 源实例写入两条记忆（直接经 upsert 模拟）
        sourceStore.Upsert(new MessageMemoryRecord("m1", "g1", "main", "user_1", "user", "数据库选型用 Postgres", new float[8], 1000, MemoryImportance.Critical, null));
        sourceStore.Upsert(new MessageMemoryRecord("m2", "g1", "main", "agent_1", "agent", "缓存用 Redis", new float[8], 2000, MemoryImportance.Normal, null));

        // 导出（group=g1，since=0）
        var exported = source.ExportMemories("g1", 0, 5000, 0);
        Assert.Equal(2, exported.Count);
        Assert.Contains(exported, m => m.MessageId == "m1" && m.Importance == MemoryImportance.Critical);

        // 导入到目标实例：向量重算，落库
        var imported = await target.ImportMemoriesAsync(exported);
        Assert.Equal(2, imported);
        Assert.Equal(2, targetStore.Records.Count);
        Assert.Contains(targetStore.Records, r => r.MessageId == "m1" && r.Content == "数据库选型用 Postgres");

        // 幂等去重：再次导入同批 → 0（已存在跳过）
        var again = await target.ImportMemoriesAsync(exported);
        Assert.Equal(0, again);
    }

    [Fact]
    public async Task Export_SinceMs_OnlyReturnsIncrement()
    {
        var store = new MemStore();
        store.Upsert(new MessageMemoryRecord("m1", "g1", "main", "user_1", "user", "旧记忆", new float[8], 1000, 0, null));
        store.Upsert(new MessageMemoryRecord("m2", "g1", "main", "user_1", "user", "新记忆", new float[8], 5000, 0, null));
        using var memory = CreateMemory(store);

        // since=3000 → 只导新记忆
        var delta = memory.ExportMemories("g1", 3000, 5000, 0);
        var id = Assert.Single(delta).MessageId;
        Assert.Equal("m2", id);
        Assert.Equal(1, memory.CountMemories("g1", 3000));
    }

    [Fact]
    public void Export_Disabled_Memory_ReturnsEmpty()
    {
        var store = new MemStore();
        store.Upsert(new MessageMemoryRecord("m1", "g1", "main", "user_1", "user", "x", new float[8], 1000, 0, null));
        using var memory = new AgentMessageMemory(store,
            new AgentOptions { Memory = new MemoryOptions { Enabled = false } },
            NullLogger<AgentMessageMemory>.Instance, new FakeEmbedding());
        Assert.Empty(memory.ExportMemories("g1", 0, 100, 0));
        Assert.Equal(0, memory.CountMemories("g1", 0));
    }
}
