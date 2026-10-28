namespace AguiGroupChat.Agents;

/// <summary>
/// 技能「修复健康度」（1.0.361）：把自愈闭环的事件汇总成编排能用的判断依据。
///
/// <para>
/// 与产物质量画像（1.0.348）互补：质量画像看的是<b>产出</b>好不好，这里看的是<b>修得好不好</b>——
/// 一个技能可能产出偶尔不合格但一修就好（健康），也可能一直在修、修完还回滚（不该再排进新岗位）。
/// </para>
/// </summary>
/// <param name="Attempts">窗口内有效修复次数（不含 skipped）。</param>
/// <param name="Successes">成功次数（applied + resumed）。</param>
public sealed record SkillLoopHealth(
    string SkillId,
    int Attempts,
    int Successes,
    int RolledBack,
    int Failed,
    double SuccessRate)
{
    /// <summary>修复后仍反复回滚 / 成功率低于一半（至少两次有效尝试）：编排时应当避开。</summary>
    public bool IsAvoid => Attempts >= 2 && (SuccessRate < 0.5 || RolledBack >= 2);

    /// <summary>修过但不算稳（有失败或回滚，或成功率低于 70%）：编排时提醒写明规避步骤。</summary>
    public bool IsWatch => !IsAvoid && Attempts >= 1 && (Failed >= 1 || RolledBack >= 1 || SuccessRate < 0.7);

    public bool IsRisky => IsAvoid || IsWatch;

    /// <summary>一行说明（进提示词 / 自检文案）。</summary>
    public string Describe()
        => $"近 {Attempts} 次修复成功 {Successes} 次（{Math.Round(SuccessRate * 100)}%）、回滚 {RolledBack} 次、失败 {Failed} 次";
}

/// <summary>
/// 修复健康度汇总（1.0.361，<b>纯函数</b>）：只统计窗口内的有效修复事件，供编排候选与自检共用。
/// </summary>
public static class SkillLoopHealthBoard
{
    /// <summary>默认回看 30 天（与质量画像同一窗口，避免两个信号口径打架）。</summary>
    public const int DefaultWindowDays = 30;

    private const long DayMs = 24 * 60 * 60 * 1000;

    public static IReadOnlyDictionary<string, SkillLoopHealth> Build(
        IEnumerable<SkillLoopEvent>? events,
        long nowMs,
        int windowDays = DefaultWindowDays)
    {
        var fromMs = nowMs - Math.Max(1, windowDays) * DayMs;
        var map = new Dictionary<string, SkillLoopHealth>(StringComparer.Ordinal);
        foreach (var group in (events ?? [])
                     .Where(e => !string.IsNullOrWhiteSpace(e.SkillId))
                     .Where(e => e.TimestampMs >= fromMs && e.TimestampMs <= nowMs)
                     .Where(e => e.Outcome != SkillLoopOutcomes.Skipped)
                     .GroupBy(e => e.SkillId, StringComparer.Ordinal))
        {
            var list = group.ToList();
            var attempts = list.Count;
            if (attempts == 0) continue;
            var successes = list.Count(e => e.Outcome is SkillLoopOutcomes.Applied or SkillLoopOutcomes.Resumed);
            map[group.Key] = new SkillLoopHealth(
                group.Key,
                attempts,
                successes,
                list.Count(e => e.Outcome == SkillLoopOutcomes.RolledBack),
                list.Count(e => e.Outcome == SkillLoopOutcomes.Failed),
                Math.Round((double)successes / attempts, 4));
        }
        return map;
    }
}
