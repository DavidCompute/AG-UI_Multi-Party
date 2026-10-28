namespace AguiGroupChat.Agents;

/// <summary>
/// 经验清理（1.0.354，纯函数）：挑出“回看反而下降”的未解决经验，供一键停用 / 删除。
///
/// <para>
/// 只挑有足够样本且判定为 <see cref="LessonEffectiveness.Declined"/> 的经验（前后各 ≥ 2 次），
/// 已解决的不再重复挑；正向修复经验也可能被挑中（修复后反而更差时同样该复核）。
/// </para>
/// </summary>
public static class LessonCleanup
{
    public static IReadOnlyList<string> SelectDeclined(
        IEnumerable<SkillLesson>? lessons,
        IReadOnlyList<ArtifactQualityEvent>? events)
    {
        return (lessons ?? [])
            .Where(l => !l.Resolved)
            .Where(l => LessonEffectiveness.Evaluate(events, l.TimestampMs)?.Status == LessonEffectiveness.Declined)
            .Select(l => l.Id)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }
}
