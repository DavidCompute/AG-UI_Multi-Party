namespace AguiGroupChat.Agents;

/// <summary>
/// 一次「自愈闭环不健康」判定的结果（1.0.359，纯数据，便于单测与日志）。
/// </summary>
/// <param name="ShouldAlert">最近窗口内的有效修复次数是否达到门槛且命中至少一条判据。</param>
/// <param name="Attempts">窗口内有效修复次数（不含 skipped）。</param>
/// <param name="Successes">窗口内成功次数（applied + resumed）。</param>
/// <param name="RolledBack">窗口内回滚次数。</param>
/// <param name="Failed">窗口内失败次数（自测未通过，未改动技能）。</param>
/// <param name="SuccessRate">窗口内成功率（0~1，四位小数）。</param>
/// <param name="AvgMs">窗口内平均耗时（毫秒，排除 skipped）。</param>
/// <param name="Reasons">命中的判据：<c>rollback</c> / <c>failure</c> / <c>low-success</c>。</param>
public sealed record SkillLoopAlertDecision(
    bool ShouldAlert,
    int Attempts,
    int Successes,
    int RolledBack,
    int Failed,
    double SuccessRate,
    long AvgMs,
    string[] Reasons);

/// <summary>
/// 自愈闭环告警判定（1.0.359，<b>纯函数</b>）。
///
/// <para>
/// 产物质量告警（1.0.345）看的是「产物好不好」，这里看的是「修得好不好」：同一技能最近
/// <c>windowAttempts</c> 次<b>实际发生</b>的修复尝试（skipped 表示技能不参与服务端自测，不算一次尝试）里，
/// 只要命中以下任一条就告警：
/// </para>
/// <list type="bullet">
///   <item><c>rollback</c>：回滚次数 ≥ <c>rollbacksToAlert</c>（默认 2）——反复改坏，说明修复方向本身有问题；</item>
///   <item><c>failure</c>：自测失败次数 ≥ <c>failuresToAlert</c>（默认 2）——模型写不出可用正文；</item>
///   <item><c>low-success</c>：成功率 &lt; <c>successRateThreshold</c>（默认 0.5）——修十次成三次。</item>
/// </list>
///
/// <para>
/// 有效尝试不足 <c>minAttempts</c>（默认 2）时不告警：单次失败可能只是输入问题，
/// 与产物质量告警「连续两次才算技能缺陷」的口径保持一致。
/// </para>
/// </summary>
public static class SkillLoopAlertPolicy
{
    public const int DefaultWindowAttempts = 5;
    public const int DefaultMinAttempts = 2;
    public const double DefaultSuccessRateThreshold = 0.5;
    public const int DefaultRollbacksToAlert = 2;
    public const int DefaultFailuresToAlert = 2;

    public static SkillLoopAlertDecision Evaluate(
        IReadOnlyList<SkillLoopEvent>? events,
        int windowAttempts = DefaultWindowAttempts,
        int minAttempts = DefaultMinAttempts,
        double successRateThreshold = DefaultSuccessRateThreshold,
        int rollbacksToAlert = DefaultRollbacksToAlert,
        int failuresToAlert = DefaultFailuresToAlert)
    {
        var window = Math.Max(1, windowAttempts);
        var need = Math.Clamp(minAttempts, 1, window);

        // skipped 不是一次真实尝试：窗口只在有效事件里取，避免“跳过”把历史失败挤出窗口。
        var recent = (events ?? [])
            .Where(e => !string.IsNullOrWhiteSpace(e.Outcome))
            .Where(e => e.Outcome != SkillLoopOutcomes.Skipped)
            .OrderBy(e => e.TimestampMs)
            .TakeLast(window)
            .ToList();
        if (recent.Count < need)
            return new SkillLoopAlertDecision(false, recent.Count, 0, 0, 0, 0, 0, []);

        var successes = recent.Count(e => e.Outcome is SkillLoopOutcomes.Applied or SkillLoopOutcomes.Resumed);
        var rolledBack = recent.Count(e => e.Outcome == SkillLoopOutcomes.RolledBack);
        var failed = recent.Count(e => e.Outcome == SkillLoopOutcomes.Failed);
        var rate = Math.Round((double)successes / recent.Count, 4);
        var avgMs = (long)Math.Round(recent.Average(e => Math.Max(0, e.DurationMs)));

        var reasons = new List<string>(3);
        if (rolledBack >= Math.Max(1, rollbacksToAlert)) reasons.Add("rollback");
        if (failed >= Math.Max(1, failuresToAlert)) reasons.Add("failure");
        if (rate < successRateThreshold) reasons.Add("low-success");

        return new SkillLoopAlertDecision(
            reasons.Count > 0, recent.Count, successes, rolledBack, failed, rate, avgMs, reasons.ToArray());
    }
}

/// <summary>
/// 一条自愈闭环告警（1.0.359）：修复反复失败 / 回滚，需要人看一眼技能定义与回归用例。
/// 与产物质量告警一样只留内存（重启即清空）——它是「近期需要处理」的运营信号。
/// </summary>
public sealed record SkillLoopAlert(
    string SkillId,
    string SkillName,
    int Attempts,
    int Successes,
    int RolledBack,
    int Failed,
    double SuccessRate,
    long AvgMs,
    string[] Reasons,
    bool RegressionEnsured,
    long TimestampMs);

/// <summary>自愈闭环告警存储（1.0.359）：有界内存（默认 200 条），供前端通知中心轮询。</summary>
public sealed class SkillLoopAlertStore
{
    public const int MaxAlerts = 200;

    private readonly object _gate = new();
    private readonly List<SkillLoopAlert> _alerts = new();

    public void Add(SkillLoopAlert alert)
    {
        if (string.IsNullOrWhiteSpace(alert.SkillId)) return;
        lock (_gate)
        {
            _alerts.Add(alert);
            if (_alerts.Count > MaxAlerts)
                _alerts.RemoveRange(0, _alerts.Count - MaxAlerts);
        }
    }

    public IReadOnlyList<SkillLoopAlert> Snapshot(long sinceMs = 0)
    {
        lock (_gate)
            return _alerts.Where(a => a.TimestampMs >= sinceMs).ToArray();
    }
}
