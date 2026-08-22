namespace AguiGroupChat.Hub.Storage;

/// <summary>按日聚合的 token 用量记录（智能体模型调用统计 / 配额）。</summary>
public sealed record UsageAggregate(
    string Date,          // YYYY-MM-DD（UTC）
    string AgentId,       // 智能体 ID
    string UserId,        // 触发者（定时任务 = "system"）
    long PromptTokens,
    long CompletionTokens,
    long ReasoningTokens,
    long Calls)
{
    public long TotalTokens => PromptTokens + CompletionTokens + ReasoningTokens;
}

/// <summary>
/// 模型用量存储抽象（按「日期 + 智能体 + 触发者」聚合行，调用时 upsert 累加）。
/// 内存实现（<see cref="InMemoryUsageStore"/>）与数据库实现（Postgres / Relational）语义一致。
/// </summary>
public interface IUsageStore
{
    /// <summary>累加一次调用的 token 用量（同日期同键的行 upsert 累加）。</summary>
    void RecordUsage(string date, string agentId, string userId, long promptTokens, long completionTokens, long reasoningTokens);

    /// <summary>某用户指定日期的总 token（配额检查用；未记录返回 0）。</summary>
    long GetUserUsage(string userId, string date);

    /// <summary>指定日期区间（含边界）的全部明细行。</summary>
    IReadOnlyList<UsageAggregate> GetUsageBetween(string fromDate, string toDate);

    /// <summary>清空全部用量记录（系统初始化用）。</summary>
    void ClearAll();
}
