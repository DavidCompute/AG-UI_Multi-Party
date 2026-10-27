namespace AguiGroupChat.Agents;

/// <summary>
/// 一条经验的有效性回看（1.0.352）：以经验写入时间为界，对比该技能前后的产物合格率。
/// </summary>
/// <param name="BeforeRuns">写入前样本数（最多 10 次）。</param>
/// <param name="BeforeRate">写入前合格率（0~1）。</param>
/// <param name="BeforeAvg">写入前平均分（0~100）。</param>
/// <param name="AfterRuns">写入后样本数（最多 10 次）。</param>
/// <param name="AfterRate">写入后合格率（0~1）。</param>
/// <param name="AfterAvg">写入后平均分（0~100）。</param>
/// <param name="Delta">合格率变化（后 − 前，百分点以 0~1 表示；四位小数）。</param>
/// <param name="Status">结论，见 <see cref="LessonEffectiveness"/> 常量。</param>
public sealed record SkillLessonEffectiveness(
    int BeforeRuns,
    double BeforeRate,
    double BeforeAvg,
    int AfterRuns,
    double AfterRate,
    double AfterAvg,
    double Delta,
    string Status);

/// <summary>
/// 经验有效性回看（1.0.352，纯函数）。
///
/// <para>
/// 运营关心的是“写了这条经验之后，技能到底有没有变好”。这里用<b>经验写入时间</b>把同一技能的
/// 产物质量事件切成前后两段（各取最近 10 条），比较合格率：
/// </para>
/// <list type="bullet">
///   <item>前后各至少 2 次才判定，否则 <c>insufficient</c>（样本不足，不轻易下结论）；</item>
///   <item>合格率提升 ≥ 10 个百分点，或从 &lt; 80% 提升到 ≥ 80% → <c>improved</c>；</item>
///   <item>下降 ≥ 10 个百分点 → <c>declined</c>（提醒运营复核这条经验是否写错 / 已过时）；</item>
///   <item>其余 → <c>flat</c>。</item>
/// </list>
/// </summary>
public static class LessonEffectiveness
{
    public const string Improved = "improved";
    public const string Declined = "declined";
    public const string Flat = "flat";
    public const string Insufficient = "insufficient";

    /// <summary>前后各至少这么多次才判定。</summary>
    public const int MinRunsPerSide = 2;

    /// <summary>每侧最多取最近这么多次，避免久远历史稀释结论。</summary>
    public const int MaxSamplesPerSide = 10;

    public static SkillLessonEffectiveness? Evaluate(IReadOnlyList<ArtifactQualityEvent>? events, long lessonAtMs)
    {
        var list = (events ?? [])
            .Where(e => !string.IsNullOrWhiteSpace(e.Kind))
            .OrderBy(e => e.TimestampMs)
            .ToList();
        if (list.Count == 0) return null;

        var before = list.Where(e => e.TimestampMs < lessonAtMs).TakeLast(MaxSamplesPerSide).ToList();
        var after = list.Where(e => e.TimestampMs >= lessonAtMs).Take(MaxSamplesPerSide).ToList();
        var beforeRate = Rate(before);
        var afterRate = Rate(after);
        var delta = Math.Round(afterRate - beforeRate, 4);
        var status = before.Count < MinRunsPerSide || after.Count < MinRunsPerSide
            ? Insufficient
            : delta >= 0.10 || (afterRate >= 0.8 && beforeRate < 0.8) ? Improved
            : delta <= -0.10 ? Declined
            : Flat;

        return new SkillLessonEffectiveness(
            before.Count, beforeRate, Avg(before),
            after.Count, afterRate, Avg(after),
            delta, status);
    }

    private static double Rate(IReadOnlyList<ArtifactQualityEvent> items)
        => items.Count == 0 ? 0 : Math.Round((double)items.Count(e => e.Satisfied) / items.Count, 4);

    private static double Avg(IReadOnlyList<ArtifactQualityEvent> items)
        => items.Count == 0 ? 0 : Math.Round(items.Average(e => Math.Clamp(e.Score, 0, 100)), 1);
}
