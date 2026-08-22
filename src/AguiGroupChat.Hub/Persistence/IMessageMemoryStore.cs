using AguiGroupChat.Hub.Models;

namespace AguiGroupChat.Hub.Persistence;

/// <summary>
/// 语义记忆的向量存储抽象（PostgreSQL + pgvector 实现见 <see cref="Postgres.PgMessageMemoryStore"/>）。
/// </summary>
public interface IMessageMemoryStore
{
    /// <summary>启动时建表 + 建 HNSW 索引（幂等；pgvector 扩展不可用时内部降级为不可用，不抛异常）。</summary>
    void EnsureSchema();

    /// <summary>写入 / 覆盖一条记忆（含向量与分级 / 过期字段）。</summary>
    void Upsert(MessageMemoryRecord record);

    /// <summary>撤回消息的记忆（标记删除，检索不再命中）。</summary>
    void Remove(string groupId, string messageId);

    /// <summary>解散群时删除该群全部记忆（物理删除，群已不存在无需保留）。</summary>
    void RemoveGroup(string groupId);

    /// <summary>清空全部记忆（系统初始化用）。</summary>
    void ClearAll();

    /// <summary>按余弦相似度检索 top-k；scope=agent 时限定该智能体所在的所有群；scope=all 时跨全部群。
    /// 自动遗忘：已过期（expires_at 非空且早于当前时间）的记忆不参与检索；同相似度下重要级优先。</summary>
    IReadOnlyList<MessageMemoryHit> Search(string groupId, string? agentId, float[] embedding, int topK, double minScore, string scope);

    /// <summary>检索某个人（用户或智能体）自己的历史发言（个人记忆），跨群且遵守私密群隔离
    /// （非当前触发群的私密群内容不进入个人记忆）。同样过滤过期记忆、重要级优先。</summary>
    IReadOnlyList<MessageMemoryHit> SearchPerson(string personId, string currentGroupId, float[] embedding, int topK, double minScore);

    // ================= 记忆治理（分群分级 / 自动遗忘 / 可视化） =================

    /// <summary>分页列出某群（或全部群）的记忆条目（不含向量），支持按发送者与关键词过滤；用于记忆可视化。</summary>
    IReadOnlyList<MessageMemoryItem> ListMessages(string? groupId, string? senderId, string? keyword, int limit, int offset);

    /// <summary>记忆条目总数（与 <see cref="ListMessages"/> 相同的过滤条件），用于分页。</summary>
    long CountMessages(string? groupId, string? senderId, string? keyword);

    /// <summary>各群记忆统计（条数 / 最近时间 / 已过期数），用于可视化总览。</summary>
    IReadOnlyList<MessageMemoryGroupStat> GroupStats(long nowMs);

    /// <summary>按 messageId 取单条记忆（无向量；用于所有权校验等）。默认返回 null（未实现时调用方应回退）。</summary>
    MessageMemoryItem? GetByMessageId(string messageId) => null;

    /// <summary>物理删除单条记忆（记忆可视化「删除」）。</summary>
    bool DeleteByMessageId(string messageId);

    /// <summary>调整单条记忆的级别（0 普通 / 1 重要 / 2 关键）。</summary>
    bool UpdateImportance(string messageId, int importance);

    /// <summary>把某群（或全部群）的记忆统一设置为过期时间（手动遗忘策略：保留最近 N 天时
    /// 把「更早的记忆」设为过去时间戳 → 立即失效；未来时间戳 = 延后遗忘）。返回受影响条数。</summary>
    int SetExpiry(string? groupId, long? expiresAt, long nowMs);

    /// <summary>物理删除已过期记忆（自动遗忘的清理执行；返回删除条数）。</summary>
    int PruneExpired(long nowMs);
}
