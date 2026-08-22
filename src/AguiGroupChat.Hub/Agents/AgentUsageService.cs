using AguiGroupChat.Hub.Storage;
using Microsoft.Extensions.Logging;

namespace AguiGroupChat.Hub.Agents;

/// <summary>
/// 模型 token 用量统计与配额服务：每次智能体模型调用结束后记录 (日期, 智能体, 触发者) 的用量，
/// 管理员可查看按天汇总；配额（Agents:DailyTokenQuotaPerUser，0 = 不限）按「触发者当日累计 token」执行，
/// 超限拒绝新触发（定时任务 system 触发不计入个人配额）。
/// </summary>
public sealed class AgentUsageService
{
    /// <summary>定时任务触发的虚拟触发者 ID：不参与个人配额（系统任务不应被用户配额误伤）。</summary>
    public const string SystemUserId = "system";

    private readonly IUsageStore _store;
    private readonly long _dailyQuotaPerUser;
    private readonly ILogger<AgentUsageService> _logger;

    public AgentUsageService(IUsageStore store, long dailyQuotaPerUser, ILogger<AgentUsageService> logger)
    {
        _store = store;
        _dailyQuotaPerUser = Math.Max(0, dailyQuotaPerUser);
        _logger = logger;
    }

    /// <summary>当前 UTC 日期（YYYY-MM-DD，配额 / 统计按 UTC 日切分，行为可预测）。</summary>
    public static string Today() => DateTimeOffset.UtcNow.ToString("yyyy-MM-dd");

    /// <summary>记录一次模型调用用量（幂等累加）。</summary>
    public void RecordUsage(string agentId, string userId, long promptTokens, long completionTokens, long reasoningTokens)
    {
        if (promptTokens <= 0 && completionTokens <= 0 && reasoningTokens <= 0) return;
        try
        {
            _store.RecordUsage(Today(), agentId, userId, Math.Max(0, promptTokens), Math.Max(0, completionTokens), Math.Max(0, reasoningTokens));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "用量记录失败（不影响主流程）：agent={AgentId}", agentId);
        }
    }

    /// <summary>某用户当日累计 token（配额检查；无记录返回 0）。</summary>
    public long GetUserTodayTokens(string userId) => _store.GetUserUsage(userId, Today());

    /// <summary>配额配置（0 = 不限）。</summary>
    public long DailyQuotaPerUser => _dailyQuotaPerUser;

    /// <summary>配额校验：超限返回剩余额度信息（null = 未超限 / 未启用配额）。</summary>
    public QuotaCheckResult? CheckUserQuota(string userId)
    {
        if (userId == SystemUserId) return null; // 定时任务不受个人配额限制
        var quota = DailyQuotaPerUser;
        if (quota <= 0) return null;
        var used = GetUserTodayTokens(userId);
        if (used < quota) return null;
        return new QuotaCheckResult(quota, used);
    }

    /// <summary>最近 N 天的按天汇总（管理员用量统计页）。</summary>
    public IReadOnlyList<UsageDaySummary> GetDailySummary(int days)
    {
        var to = DateTimeOffset.UtcNow;
        var from = to.AddDays(-Math.Clamp(days, 1, 90) + 1);
        var rows = _store.GetUsageBetween(from.ToString("yyyy-MM-dd"), to.ToString("yyyy-MM-dd"));
        return rows
            .GroupBy(r => r.Date)
            .Select(g => new UsageDaySummary(
                g.Key,
                g.Sum(r => r.TotalTokens),
                g.Sum(r => r.PromptTokens),
                g.Sum(r => r.CompletionTokens),
                g.Sum(r => r.ReasoningTokens),
                g.Sum(r => r.Calls)))
            .OrderBy(s => s.Date)
            .ToList();
    }
}

/// <summary>配额校验结果（超限时返回）。</summary>
public sealed record QuotaCheckResult(long Quota, long Used)
{
    public long Remaining => Math.Max(0, Quota - Used);
}

/// <summary>按天聚合的用量摘要（管理员查看）。</summary>
public sealed record UsageDaySummary(string Date, long TotalTokens, long PromptTokens, long CompletionTokens, long ReasoningTokens, long Calls);
