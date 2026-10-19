using System.Collections.Concurrent;
using AguiGroupChat.Hub.Models;
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
    private readonly Func<string, long?>? _groupQuotaOverride;
    private readonly ILogger<AgentUsageService> _logger;

    /// <summary>配额阈值告警回调（1.0.270）：当日用量**首次**跨过 80% / 100% 时触发，由组合根桥接到出站 Webhook。
    /// 未设置则完全不做事（不产生额外查询）。</summary>
    public Action<QuotaThresholdEvent>? OnQuotaThreshold { get; set; }

    /// <summary>已告警过的最高水位（键 = <c>日期|作用域|ID</c>，值 = warn / over）：每作用域每级每日一次，避免刷屏。
    /// 跨日时整体清空（键带日期，无需逐条过期）。</summary>
    private readonly ConcurrentDictionary<string, string> _thresholdNotified = new(StringComparer.Ordinal);
    private readonly object _thresholdGate = new();
    private string _thresholdDate = "";

    /// <summary>主构造：配额值实时读取（由调用方以委托注入）。
    /// <paramref name="groupQuotaOverride"/>（可选）= 知聚级覆写解析器（由 Agents 层注入 `GroupHub.GroupQuotaOverride`），
    /// 使 <see cref="EffectiveGroupQuota"/> 与阈值告警也拿到「本知聚自己的额度」。</summary>
    public AgentUsageService(IUsageStore store, Func<long> quotaPerUser, Func<long> quotaPerGroup,
        ILogger<AgentUsageService> logger, Func<string, long?>? groupQuotaOverride = null)
    {
        _store = store;
        _quotaPerUser = quotaPerUser;
        _quotaPerGroup = quotaPerGroup;
        _groupQuotaOverride = groupQuotaOverride;
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
            CheckThresholds(date, userId, groupId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "用量记录失败（不影响主流程）：agent={AgentId}", agentId);
        }
    }

    /// <summary>
    /// 阈值告警（1.0.270）：记录用量后检查个人 / 知聚两个作用域，**首次**跨过 80%（warn）/
    /// 100%（over）时回调 <see cref="OnQuotaThreshold"/>。
    /// 成本控制：未配配额（0）或已报过 over 的作用域**直接跳过**，不做多余的 store 查询。
    /// </summary>
    private void CheckThresholds(string date, string userId, string groupId)
    {
        var notify = OnQuotaThreshold;
        if (notify is null) return;
        ResetThresholdsIfNewDay(date);

        // 个人配额：system 是定时任务虚拟触发者，**刻意豁免**——不产生个人超额告警
        if (!string.IsNullOrEmpty(userId) && !string.Equals(userId, SystemUserId, StringComparison.Ordinal)
            && !AlreadyOver(date, "user", userId))
        {
            var quota = DailyQuotaPerUser;
            if (quota > 0) RaiseThreshold(date, "user", userId, GetUserTodayTokens(userId), quota, notify);
        }

        // 知聚配额：分母用**本知聚生效配额**（支持知聚级覆写）
        if (!string.IsNullOrEmpty(groupId) && !AlreadyOver(date, "group", groupId))
        {
            var quota = EffectiveGroupQuota(groupId);
            if (quota > 0) RaiseThreshold(date, "group", groupId, GetGroupTodayTokens(groupId), quota, notify);
        }
    }

    private static int LevelRank(string level) => level switch { "over" => 2, "warn" => 1, _ => 0 };

    private bool AlreadyOver(string date, string scope, string id)
        => _thresholdNotified.TryGetValue($"{date}|{scope}|{id}", out var v) && v == "over";

    /// <summary>只在**水位上升**时告警（ok → warn → over），同一作用域同一级当日只发一次。</summary>
    private void RaiseThreshold(string date, string scope, string id, long used, long quota, Action<QuotaThresholdEvent> notify)
    {
        var level = QuotaLevel(used, quota);
        if (level == "ok") return;
        var key = $"{date}|{scope}|{id}";
        var seen = _thresholdNotified.TryGetValue(key, out var v) ? v : "ok";
        if (LevelRank(level) <= LevelRank(seen)) return;
        _thresholdNotified[key] = level;
        try
        {
            notify(new QuotaThresholdEvent
            {
                Scope = scope,
                ScopeId = id,
                Level = level,
                Used = used,
                Quota = quota,
                Date = date,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "配额阈值告警回调失败（忽略）");
        }
    }

    /// <summary>跨 UTC 日时清空告警状态（键已带日期，但不清会无限增长）。</summary>
    private void ResetThresholdsIfNewDay(string date)
    {
        if (string.Equals(_thresholdDate, date, StringComparison.Ordinal)) return;
        lock (_thresholdGate)
        {
            if (string.Equals(_thresholdDate, date, StringComparison.Ordinal)) return;
            _thresholdNotified.Clear();
            _thresholdDate = date;
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
        var quota = EffectiveGroupQuota(groupId, quotaOverride);
        if (quota <= 0) return null;
        var used = GetGroupTodayTokens(groupId);
        if (used < quota) return null;
        return new QuotaCheckResult(quota, used);
    }

    /// <summary>某知聚的**生效**配额：显式参数 > 知聚级覆写解析器 > 全局 `Agents:DailyTokenQuotaPerGroup`（0 = 不限）。
    /// 单一解析点，供校验、展示与阈值告警共用（避免多处各写一遍回退逻辑）。</summary>
    public long EffectiveGroupQuota(string groupId, long? quotaOverride = null)
    {
        var ov = quotaOverride
            ?? (string.IsNullOrEmpty(groupId) ? null : _groupQuotaOverride?.Invoke(groupId));
        return ov is { } o && o >= 0 ? o : DailyQuotaPerGroup;
    }

    /// <summary>今日各触发者用量汇总（一次查询聚合，按 token 降序）——供管理员「用量统计」的**分用户明细 / 个人配额水位**。
    /// 与 <see cref="GetUserTodayTokens"/> 同源（同一张按日聚合表），但一次取回全部触发者；
    /// <c>system</c>（定时任务虚拟触发者）也会出现在结果里，但它**不受个人配额约束**（水位由调用方标为 exempt）。</summary>
    public IReadOnlyList<UserUsageToday> GetUserTodaySummary()
    {
        var today = Today();
        return _store.GetUsageBetween(today, today)
            .Where(r => !string.IsNullOrEmpty(r.UserId))
            .GroupBy(r => r.UserId, StringComparer.Ordinal)
            .Select(g => new UserUsageToday(g.Key, g.Sum(r => r.TotalTokens), g.Sum(r => r.Calls)))
            .OrderByDescending(x => x.Tokens)
            .ThenBy(x => x.UserId, StringComparer.Ordinal)
            .ToList();
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

/// <summary>今日某知聚的用量汇总（管理员「用量统计」分知聚明细）。</summary>
public sealed record GroupUsageToday(string GroupId, long Tokens, long Calls);

/// <summary>今日某触发者的用量汇总（管理员「用量统计」分用户明细 / 个人配额水位）。</summary>
public sealed record UserUsageToday(string UserId, long Tokens, long Calls);
