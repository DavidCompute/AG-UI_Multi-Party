using AguiGroupChat.Hub.Models;

namespace AguiGroupChat.Hub.Agents;

/// <summary>
/// 智能体语义记忆（RAG）：群消息落库后异步向量化写入，触发回复前按语义相似度检索并注入上下文。
/// 未启用时 GroupHub / AgentGateway 收到 null，记忆功能对既有流程完全透明。
/// 实现见 <c>AguiGroupChat.Agents.AgentMessageMemory</c>。
/// </summary>
public interface IMessageMemory
{
    /// <summary>写入一条记忆（fire-and-forget：内部异步向量化，失败不影响主流程）。</summary>
    void Remember(MessageMemoryEntry entry);

    /// <summary>删除 / 标记撤回消息的记忆（撤回后不再被检索命中）。</summary>
    void Forget(string groupId, string messageId);

    /// <summary>解散群时删除该群全部记忆（物理删除）。</summary>
    void RemoveGroup(string groupId);

    /// <summary>按语义相似度检索历史记忆（端点不可用 / 未启用时返回空）。
    /// groupId 为当前触发群；agentId 为该智能体，Scope=agent 时按它所在的所有群检索。</summary>
    Task<IReadOnlyList<MessageMemoryHit>> SearchAsync(string groupId, string agentId, string query, CancellationToken ct = default);

    /// <summary>按语义相似度检索某个人（用户或智能体）自己的历史发言（个人记忆），跨群且遵守私密群隔离。</summary>
    Task<IReadOnlyList<MessageMemoryHit>> SearchPersonAsync(string personId, string currentGroupId, string query, CancellationToken ct = default);

    // ================= 记忆治理（分群分级 / 自动遗忘 / 可视化） =================

    /// <summary>分页列出记忆条目（可视化；未启用时返回空）。</summary>
    IReadOnlyList<MessageMemoryItem> ListMessages(string? groupId, string? senderId, string? keyword, int limit, int offset);

    /// <summary>记忆条目总数（与 <see cref="ListMessages"/> 同过滤条件）。</summary>
    long CountMessages(string? groupId, string? senderId, string? keyword);

    /// <summary>各群记忆统计（可视化总览）。</summary>
    IReadOnlyList<MessageMemoryGroupStat> GroupStats();

    /// <summary>按 messageId 取单条记忆（无向量；供所有权校验）。默认返回 null。</summary>
    MessageMemoryItem? GetByMessageId(string messageId) => null;

    /// <summary>物理删除单条记忆（可视化「删除」）。</summary>
    bool DeleteByMessageId(string messageId);

    /// <summary>调整单条记忆级别（0 普通 / 1 重要 / 2 关键）。</summary>
    bool UpdateImportance(string messageId, int importance);

    /// <summary>手动遗忘：把某群（groupId 为空 = 全部群）记忆统一设过期；<paramref name="retentionHours"/> 为空表示立即遗忘。</summary>
    int ForgetGroup(string? groupId, double? retentionHours);

    /// <summary>物理删除已过期记忆（自动遗忘定时清理执行）。</summary>
    int PruneExpired();
}
