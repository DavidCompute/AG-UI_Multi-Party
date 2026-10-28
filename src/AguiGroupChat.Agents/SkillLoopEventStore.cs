namespace AguiGroupChat.Agents;

/// <summary>修复来源（1.0.360）：人工点“一键修复”或闭环告警自动触发。</summary>
public static class SkillRepairSources
{
    public const string Manual = "manual";
    public const string Auto = "auto";
}

/// <summary>一次修复尝试的结局（1.0.358）。</summary>
public static class SkillLoopOutcomes
{
    /// <summary>写入了新正文并恢复挂载。</summary>
    public const string Applied = "applied";
    /// <summary>自测通过（正文未变）并恢复挂载。</summary>
    public const string Resumed = "resumed";
    /// <summary>回归未通过：已回滚并保持暂停。</summary>
    public const string RolledBack = "rolledback";
    /// <summary>自测未通过，未改动技能。</summary>
    public const string Failed = "failed";
    /// <summary>该技能不参与服务端自测（本机执行 / 不支持的类型）。</summary>
    public const string Skipped = "skipped";
}

/// <summary>
/// 一条自愈闭环事件（1.0.358）：记录一次修复尝试的结局与耗时，供质量看板统计
/// “告警 → 修复 → 回归 → 回滚”的实际效果。
/// </summary>
public sealed record SkillLoopEvent(
    string Id,
    string SkillId,
    string Outcome,
    int Attempts,
    long DurationMs,
    int RegressionRan,
    bool RegressionPassed,
    long TimestampMs)
{
    /// <summary>修复来源：<c>manual</c>（人工触发）/ <c>auto</c>（闭环告警自动触发，1.0.360）。</summary>
    public string Source { get; init; } = "manual";
}

/// <summary>
/// 自愈闭环事件库：有界内存（默认 2000 条）+ 扩展区持久化。
/// </summary>
public sealed class SkillLoopEventStore
{
    public const int MaxEvents = 2000;

    private readonly object _gate = new();
    private readonly List<SkillLoopEvent> _events = new();

    public SkillLoopEvent Record(string skillId, string outcome, int attempts, long durationMs, int regressionRan, bool regressionPassed, string source = SkillRepairSources.Manual)
    {
        var evt = new SkillLoopEvent(
            "lp_" + Guid.NewGuid().ToString("N")[..12],
            skillId ?? "",
            outcome ?? "",
            Math.Max(0, attempts),
            Math.Max(0, durationMs),
            Math.Max(0, regressionRan),
            regressionPassed,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
        {
            Source = string.IsNullOrWhiteSpace(source) ? SkillRepairSources.Manual : source,
        };
        if (string.IsNullOrWhiteSpace(evt.SkillId)) return evt;
        lock (_gate)
        {
            _events.Add(evt);
            if (_events.Count > MaxEvents) _events.RemoveRange(0, _events.Count - MaxEvents);
        }
        return evt;
    }

    public IReadOnlyList<SkillLoopEvent> Query(long sinceMs)
    {
        lock (_gate) return _events.Where(e => e.TimestampMs >= sinceMs).ToArray();
    }

    public IReadOnlyList<SkillLoopEvent> Snapshot()
    {
        lock (_gate) return _events.ToArray();
    }

    public object SnapshotState() => Snapshot();

    public void RestoreState(IReadOnlyList<SkillLoopEvent>? rows)
    {
        lock (_gate)
        {
            _events.Clear();
            if (rows is null) return;
            foreach (var row in rows
                         .Where(r => !string.IsNullOrWhiteSpace(r.SkillId))
                         .OrderBy(r => r.TimestampMs)
                         .TakeLast(MaxEvents))
                _events.Add(row);
        }
    }
}

/// <summary>闭环汇总（1.0.358，纯函数结果）。</summary>
public sealed record SkillLoopSummary(
    int Attempts,
    int Applied,
    int Resumed,
    int Failed,
    int RolledBack,
    int Skipped,
    double SuccessRate,
    long AvgMs,
    int RegressionRuns,
    int RegressionPassed);

/// <summary>
/// 按技能聚合的闭环指标（1.0.359）：尝试数包含 skipped，平均耗时不计 skipped。
/// </summary>
public sealed record SkillLoopSkillStat(
    string SkillId,
    int Attempts,
    int Applied,
    int RolledBack,
    int Failed,
    int Skipped,
    long AvgMs,
    long LastMs);

/// <summary>
/// 闭环指标聚合（1.0.358，纯函数）：成功率 =（applied + resumed）/（尝试 − skipped），
/// 平均耗时不计 skipped；回归次数 / 通过数按事件求和。
/// </summary>
public static class SkillLoopMetrics
{
    public static SkillLoopSummary Summarize(IEnumerable<SkillLoopEvent>? events)
    {
        var list = (events ?? []).Where(e => !string.IsNullOrWhiteSpace(e.Outcome)).ToList();
        int Count(string outcome) => list.Count(e => string.Equals(e.Outcome, outcome, StringComparison.Ordinal));

        var applied = Count(SkillLoopOutcomes.Applied);
        var resumed = Count(SkillLoopOutcomes.Resumed);
        var failed = Count(SkillLoopOutcomes.Failed);
        var rolledBack = Count(SkillLoopOutcomes.RolledBack);
        var skipped = Count(SkillLoopOutcomes.Skipped);
        var effective = list.Count - skipped;
        var effectiveEvents = list
            .Where(e => !string.Equals(e.Outcome, SkillLoopOutcomes.Skipped, StringComparison.Ordinal))
            .ToList();
        var successRate = effective <= 0 ? 0 : Math.Round((double)(applied + resumed) / effective, 4);
        var avgMs = effectiveEvents.Count == 0
            ? 0L
            : (long)Math.Round(effectiveEvents.Average(e => Math.Max(0, e.DurationMs)));
        var regressionRuns = list.Sum(e => Math.Max(0, e.RegressionRan));
        var regressionPassed = list.Count(e => e.RegressionRan > 0 && e.RegressionPassed);
        return new SkillLoopSummary(list.Count, applied, resumed, failed, rolledBack, skipped,
            successRate, avgMs, regressionRuns, regressionPassed);
    }

    /// <summary>
    /// 按技能聚合（1.0.359）：供管理端明细表与组织体检报告共用，避免两处各写一套口径。
    /// 排序：尝试数降序 → 技能 ID 升序（调用方可再按名称重排）。
    /// </summary>
    public static IReadOnlyList<SkillLoopSkillStat> BySkill(IEnumerable<SkillLoopEvent>? events)
    {
        var list = (events ?? []).Where(e => !string.IsNullOrWhiteSpace(e.SkillId)).ToList();
        return list
            .GroupBy(e => e.SkillId, StringComparer.Ordinal)
            .Select(g =>
            {
                var effective = g.Where(e => e.Outcome != SkillLoopOutcomes.Skipped).ToList();
                return new SkillLoopSkillStat(
                    g.Key,
                    g.Count(),
                    g.Count(e => e.Outcome is SkillLoopOutcomes.Applied or SkillLoopOutcomes.Resumed),
                    g.Count(e => e.Outcome == SkillLoopOutcomes.RolledBack),
                    g.Count(e => e.Outcome == SkillLoopOutcomes.Failed),
                    g.Count(e => e.Outcome == SkillLoopOutcomes.Skipped),
                    effective.Count == 0 ? 0L : (long)Math.Round(effective.Average(e => Math.Max(0, e.DurationMs))),
                    g.Max(e => e.TimestampMs));
            })
            .OrderByDescending(x => x.Attempts)
            .ThenBy(x => x.SkillId, StringComparer.Ordinal)
            .ToList();
    }
}
