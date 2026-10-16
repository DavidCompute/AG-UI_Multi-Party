using AguiGroupChat.Hub.Storage;
using Microsoft.Extensions.Logging;

namespace AguiGroupChat.Hub.Agents;

/// <summary>
/// <summary>模型 token 用量统计与配额服务：每次智能体模型调用结束后记录 (日期, 智能体, 触发者) 的用量，
/// 管理员可查看按天汇总；配额按「触发者当日累计 token」（Agents:DailyTokenQuotaPerUser）与
/// 「知聚当日累计 token」（Agents:DailyTokenQuotaPerGroup）执行，均 0 = 不限，超限拒绝新触发。
/// 定时任务以虚拟触发者 <see cref="SystemUserId"/> 发起，不计入个人配额，但**计入知聚配额**。
/// <para>
/// 配额值以 <c>Func&lt;long&gt;</c> **实时读取**（由 Agents 层注入 `() =&gt; options.DailyTokenQuota…`），
/// 而非在构造时拷贝——否则管理员在「配置治理」热改配额只会在重启后才生效（曾踩到这个静默失效）。
/// 本服务位于 Hub 层，不能直接引用 Agents 层的 AgentOptions，故用委托解耦。
/// </para>
/// </summary>
public sealed class AgentUsageService
{
    /// <summary>定时任务触发的虚拟触发者 ID：不参与个人配额（系统任务不应被用户配额误伤）。</summary>
    public const string SystemUserId = "system";

    /// <summary>知聚配额「接近上限」预警阈值：当日用量达到配额的该比例（含）即视为 warn。
    /// 配额本就与用量同源（同一张「按知聚」聚合表），故这里只是阈值判定，不引入新状态。</summary>
    public const double QuotaWarnRatio = 0.8;

    /// <summary>知聚配额水位：<c>ok</c>（未启用 / 未达阈值）、<c>warn</c>（≥ {@link QuotaWarnRatio}）、<c>over</c>（≥ 配额）。</summary>
    public static string QuotaLevel(long tokens, long quota)
    {
        if (quota <= 0) return "ok";            // 未启用配额：水位无意义
        if (tokens >= quota) return "over";
        return tokens >= quota * QuotaWarnRatio ? "warn" : "ok";
    }

    private readonly IUsageStore _store;
    private readonly Func<long> _quotaPerUser;
    private readonly Func<long> _quotaPerGroup;
    private readonly ILogger<AgentUsageService> _logger;

    /// <summary>主构造：配额值实时读取（由调用方以委托注入）。</summary>
    public AgentUsageService(IUsageStore store, Func<long> quotaPerUser, Func<long> quotaPerGroup, ILogger<AgentUsageService> logger)
    {
        _store = store;
        _quotaPerUser = quotaPerUser;
        _quotaPerGroup = quotaPerGroup;
        _logger = logger;
    }

    /// <summary>兼容旧签名（测试 / 旧调用方）：固定个人配额、不限知聚。新代码请用主构造。</summary>
    public AgentUsageService(IUsageStore store, long dailyQuotaPerUser, ILogger<AgentUsageService> logger)
        : this(store, () => Math.Max(0, dailyQuotaPerUser), () => 0, logger)
    {
    }

    /// <summary>当前 UTC 日期（YYYY-MM-DD，配额 / 统计按 UTC 日切分，行为可预测）。</summary>
    public static string Today() => DateTimeOffset.UtcNow.ToString("yyyy-MM-dd");

    /// <summary>记录一次模型调用用量（幂等累加；不含知聚维度，供测试 / 无群上下文场景）。</summary>
    public void RecordUsage(string agentId, string userId, long promptTokens, long completionTokens, long reasoningTokens)
        => RecordUsage(agentId, userId, "", promptTokens, completionTokens, reasoningTokens);

    /// <summary>记录一次模型调用用量（幂等累加）：同时写「日期+员工+触发者」与（有知聚时）「日期+知聚+员工+触发者」两张聚合表。</summary>
    public void RecordUsage(string agentId, string userId, string groupId, long promptTokens, long completionTokens, long reasoningTokens)
    {
        if (promptTokens <= 0 && completionTokens <= 0 && reasoningTokens <= 0) return;
        try
        {
            var date = Today();
            var p = Math.Max(0, promptTokens);
            var c = Math.Max(0, completionTokens);
            var r = Math.Max(0, reasoningTokens);
            _store.RecordUsage(date, agentId, userId, p, c, r);
            if (!string.IsNullOrEmpty(groupId)) _store.RecordGroupUsage(date, groupId, agentId, userId, p, c, r);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "用量记录失败（不影响主流程）：agent={AgentId}", agentId);
        }
    }

    /// <summary>某用户当日累计 token（配额检查；无记录返回 0）。</summary>
    public long GetUserTodayTokens(string userId) => _store.GetUserUsage(userId, Today());

    /// <summary>某知聚当日累计 token（配额检查；无记录返回 0）。
    /// 复用既有「按知聚」聚合表的当日行——无需改 <see cref="IUsageStore"/> 接口（4 个存储实现通用）。</summary>
    public long GetGroupTodayTokens(string groupId)
    {
        if (string.IsNullOrEmpty(groupId)) return 0;
        var today = Today();
        return _store.GetGroupUsageBetween(today, today)
            .Where(r => string.Equals(r.GroupId, groupId, StringComparison.Ordinal))
            .Sum(r => r.TotalTokens);
    }

    /// <summary>今日各知聚用量汇总（一次查询聚合，按 token 降序）——供管理员「用量统计」的**分知聚明细**。
    /// 与 <see cref="GetGroupTodayTokens"/> 同源（当日「按知聚」聚合表），但一次取回全部知聚，避免逐群查询。</summary>
    public IReadOnlyList<GroupUsageToday> GetGroupTodaySummary()
    {
        var today = Today();
        return _store.GetGroupUsageBetween(today, today)
            .Where(r => !string.IsNullOrEmpty(r.GroupId))
            .GroupBy(r => r.GroupId, StringComparer.Ordinal)
            .Select(g => new GroupUsageToday(g.Key, g.Sum(r => r.TotalTokens), g.Sum(r => r.Calls)))
            .OrderByDescending(x => x.Tokens)
            .ThenBy(x => x.GroupId, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>按用户配额配置（0 = 不限；实时读配置）。</summary>
    public long DailyQuotaPerUser => Math.Max(0, _quotaPerUser());

    /// <summary>按知聚配额配置（0 = 不限；实时读配置）。</summary>
    public long DailyQuotaPerGroup => Math.Max(0, _quotaPerGroup());

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

    /// <summary>知聚配额校验：超限返回剩余额度信息（null = 未超限 / 未启用配额）。
    /// 不豁免 system 触发——系统任务的消耗同样计入知聚总账（这正是它要管的）。
    /// <paramref name="quotaOverride"/> = 知聚级覆写（`Group.QuotaOverride`，null = 用全局）；
    /// 非 null 时**以覆写为准**（0 = 本知聚不限），全局值仅作回退——所以管理员能只给某个知聚放额，不抬高全平台上限。</summary>
    public QuotaCheckResult? CheckGroupQuota(string groupId, long? quotaOverride = null)
    {
        if (string.IsNullOrEmpty(groupId)) return null;
        var quota = quotaOverride is { } o && o >= 0 ? o : DailyQuotaPerGroup;
        if (quota <= 0) return null;
        var used = GetGroupTodayTokens(groupId);
        if (used < quota) return null;
        return new QuotaCheckResult(quota, used);
    }

    /// <summary>某知聚的**生效**配额：知聚级覆写优先，否则全局 `Agents:DailyTokenQuotaPerGroup`（0 = 不限）。
    /// 单一解析点，供校验与管理页展示共用（避免两处各写一遍回退逻辑）。</summary>
    public long EffectiveGroupQuota(string groupId, long? quotaOverride = null)
        => quotaOverride is { } o && o >= 0 ? o : DailyQuotaPerGroup;

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

/// <summary>今日某知聚的用量汇总（管理员「用量统计」分知聚明细）。</summary>
public sealed record GroupUsageToday(string GroupId, long Tokens, long Calls);
