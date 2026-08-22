using System.Collections.Concurrent;

namespace AguiGroupChat.Hub.Storage;

/// <summary>进程内线程安全的用量统计（按 日期+智能体+触发者 聚合，变更通知持久化）。</summary>
public sealed class InMemoryUsageStore : IUsageStore
{
    private readonly ConcurrentDictionary<(string Date, string AgentId, string UserId), UsageAggregate> _rows = new();
    private readonly Persistence.ChangeHub? _changes;

    public InMemoryUsageStore(Persistence.ChangeHub? changes = null) => _changes = changes;

    public void RecordUsage(string date, string agentId, string userId, long prompt, long completion, long reasoning)
    {
        _rows.AddOrUpdate((date, agentId, userId),
            _ => new UsageAggregate(date, agentId, userId, prompt, completion, reasoning, 1),
            (_, old) => old with
            {
                PromptTokens = old.PromptTokens + prompt,
                CompletionTokens = old.CompletionTokens + completion,
                ReasoningTokens = old.ReasoningTokens + reasoning,
                Calls = old.Calls + 1,
            });
        _changes?.Notify();
    }

    public long GetUserUsage(string userId, string date)
        => _rows.Where(kv => kv.Key.Date == date && kv.Key.UserId == userId)
            .Sum(kv => kv.Value.TotalTokens);

    public IReadOnlyList<UsageAggregate> GetUsageBetween(string fromDate, string toDate)
        => _rows.Where(kv => string.CompareOrdinal(kv.Key.Date, fromDate) >= 0 && string.CompareOrdinal(kv.Key.Date, toDate) <= 0)
            .Select(kv => kv.Value)
            .OrderBy(a => a.Date).ThenBy(a => a.AgentId).ThenBy(a => a.UserId)
            .ToList();

    public void ClearAll()
    {
        _rows.Clear();
        _changes?.Notify();
    }
}
