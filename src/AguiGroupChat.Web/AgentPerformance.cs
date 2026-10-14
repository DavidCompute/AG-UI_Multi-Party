using AguiGroupChat.Agents;
using AguiGroupChat.Hub.Storage;

namespace AguiGroupChat.Web;

/// <summary>
/// 数字员工绩效看板的聚合（纯函数，便于单测）：把按「日期 + 员工 + 触发者」的模型用量行
/// 与消息反馈（👍/👎）按数字员工汇总，得到调用量 / token / 好评率。
/// 用量行由调用方按日期区间取；窗口只用于过滤反馈（窗口外的 👍/👎 不计入）。
/// </summary>
public static class AgentPerformance
{
    public sealed record AgentStat(
        string AgentId,
        string Name,
        long Calls,
        long PromptTokens,
        long CompletionTokens,
        long ReasoningTokens,
        long TotalTokens,
        int Up,
        int Down,
        string? LastDate)
    {
        /// <summary>好评率 = 👍 / (👍 + 👎)；无反馈时为 null（前端显示「—」）。</summary>
        public double? PositiveRate => (Up + Down) == 0 ? null : Math.Round((double)Up / (Up + Down), 4);
    }

    public static IReadOnlyList<AgentStat> Aggregate(
        IEnumerable<UsageAggregate> usage,
        IEnumerable<MessageFeedbackEntry> feedback,
        IReadOnlyDictionary<string, string> names,
        int days,
        long nowMs)
    {
        var fromMs = nowMs - (long)Math.Max(1, days) * 24 * 3600 * 1000;
        var acc = new Dictionary<string, Acc>(StringComparer.Ordinal);
        Acc Get(string id) => acc.TryGetValue(id, out var a) ? a : (acc[id] = new Acc());

        foreach (var u in usage)
        {
            if (string.IsNullOrWhiteSpace(u.AgentId)) continue;
            var a = Get(u.AgentId);
            a.Calls += u.Calls;
            a.Prompt += u.PromptTokens;
            a.Completion += u.CompletionTokens;
            a.Reasoning += u.ReasoningTokens;
            if (a.LastDate is null || string.CompareOrdinal(u.Date, a.LastDate) > 0) a.LastDate = u.Date;
        }

        foreach (var f in feedback)
        {
            if (string.IsNullOrWhiteSpace(f.AgentId) || f.CreatedAtMs < fromMs) continue;
            var a = Get(f.AgentId);
            if (f.Value > 0) a.Up++;
            else if (f.Value < 0) a.Down++;
        }

        return acc
            .Select(kv => new AgentStat(
                kv.Key,
                names.TryGetValue(kv.Key, out var n) && !string.IsNullOrWhiteSpace(n) ? n : kv.Key,
                kv.Value.Calls,
                kv.Value.Prompt,
                kv.Value.Completion,
                kv.Value.Reasoning,
                kv.Value.Prompt + kv.Value.Completion + kv.Value.Reasoning,
                kv.Value.Up,
                kv.Value.Down,
                kv.Value.LastDate))
            .OrderByDescending(s => s.Calls)
            .ThenByDescending(s => s.Up + s.Down)
            .ThenBy(s => s.Name, StringComparer.CurrentCulture)
            .ToList();
    }

    private sealed class Acc
    {
        public long Calls, Prompt, Completion, Reasoning;
        public int Up, Down;
        public string? LastDate;
    }
}
