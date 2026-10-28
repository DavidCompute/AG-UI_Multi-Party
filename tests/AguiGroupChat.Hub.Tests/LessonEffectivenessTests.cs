using AguiGroupChat.Agents;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>
/// 经验有效性回看（1.0.352）：以经验时间切分前后样本，判断合格率变化。
/// </summary>
public sealed class LessonEffectivenessTests
{
    private static ArtifactQualityEvent Evt(long atMs, int score, bool satisfied = true)
        => new("agent_a", "docx_report", "Docx", score, satisfied, [], "g1", atMs);

    [Fact]
    public void Improved_WhenPassRateRisesAfterLesson()
    {
        var events = new[]
        {
            Evt(100, 40, false), Evt(200, 50, false), Evt(300, 60, true),
            Evt(1000, 90), Evt(1100, 95), Evt(1200, 92),
        };

        var e = LessonEffectiveness.Evaluate(events, 500)!;

        Assert.Equal(LessonEffectiveness.Improved, e.Status);
        Assert.Equal(3, e.BeforeRuns);
        Assert.Equal(3, e.AfterRuns);
        Assert.Equal(0.3333, e.BeforeRate);
        Assert.Equal(1, e.AfterRate);
        Assert.Equal(0.6667, e.Delta);
    }

    [Fact]
    public void Improved_WhenCrossingEightyPercent()
    {
        // 70% → 85%：提升不足 10 个百分点，但跨过 80% 阈值，视为改善
        var events = new[]
        {
            Evt(100, 70, true), Evt(200, 70, true), Evt(300, 50, false), Evt(350, 50, false),
            Evt(1000, 90), Evt(1100, 50, false), Evt(1200, 90), Evt(1300, 90), Evt(1400, 90),
            Evt(1500, 90), Evt(1600, 90),
        };

        var e = LessonEffectiveness.Evaluate(events, 500)!;

        Assert.Equal(0.5, e.BeforeRate);
        Assert.Equal(0.8571, e.AfterRate);
        Assert.Equal(0.3571, e.Delta);
        Assert.Equal(LessonEffectiveness.Improved, e.Status);
    }

    [Fact]
    public void Declined_WhenPassRateDropsAfterLesson()
    {
        var events = new[]
        {
            Evt(100, 90), Evt(200, 90), Evt(300, 90),
            Evt(1000, 40, false), Evt(1100, 50, false), Evt(1200, 90),
        };

        var e = LessonEffectiveness.Evaluate(events, 500)!;

        Assert.Equal(LessonEffectiveness.Declined, e.Status);
        Assert.True(e.Delta <= -0.1);
    }

    [Fact]
    public void Flat_WhenNoMeaningfulChange()
    {
        var events = new[] { Evt(100, 90), Evt(200, 90), Evt(1000, 88), Evt(1100, 92) };
        var e = LessonEffectiveness.Evaluate(events, 500)!;
        Assert.Equal(LessonEffectiveness.Flat, e.Status);
        Assert.Equal(0, e.Delta);
    }

    [Fact]
    public void Insufficient_WhenEitherSideHasTooFewSamples()
    {
        // 写入前样本太少
        var beforeFew = new[] { Evt(100, 90), Evt(1000, 90), Evt(1100, 90) };
        Assert.Equal(LessonEffectiveness.Insufficient, LessonEffectiveness.Evaluate(beforeFew, 500)!.Status);

        // 写入后样本太少
        var afterFew = new[] { Evt(100, 40, false), Evt(200, 40, false), Evt(1000, 90) };
        Assert.Equal(LessonEffectiveness.Insufficient, LessonEffectiveness.Evaluate(afterFew, 500)!.Status);
    }

    [Fact]
    public void EventAtLessonTimestamp_CountsAsAfter()
    {
        var events = new[] { Evt(100, 40, false), Evt(200, 40, false), Evt(500, 90), Evt(600, 90) };
        var e = LessonEffectiveness.Evaluate(events, 500)!;
        Assert.Equal(2, e.BeforeRuns);
        Assert.Equal(2, e.AfterRuns);
        Assert.Equal(1, e.AfterRate);
    }

    [Fact]
    public void SelectDeclined_OnlyUnresolvedLessonsWithEnoughDecliningSamples()
    {
        var events = new[]
        {
            // 写入前：3 次全合格
            new ArtifactQualityEvent("a", "docx", "Docx", 90, true, [], "g", 100),
            new ArtifactQualityEvent("a", "docx", "Docx", 90, true, [], "g", 200),
            new ArtifactQualityEvent("a", "docx", "Docx", 90, true, [], "g", 300),
            // 写入后：1/3 合格 → declined
            new ArtifactQualityEvent("a", "docx", "Docx", 50, false, [], "g", 1000),
            new ArtifactQualityEvent("a", "docx", "Docx", 90, true, [], "g", 1100),
            new ArtifactQualityEvent("a", "docx", "Docx", 40, false, [], "g", 1200),
            // 另一条经验之后只有 1 次执行 → 样本不足
            new ArtifactQualityEvent("a", "docx", "Docx", 90, true, [], "g", 5000),
        };
        var lessons = new[]
        {
            new SkillLesson("sl_bad", "docx", null, SkillLessonKinds.Failure, "降了", [], null, "g", 500, false),
            new SkillLesson("sl_resolved", "docx", null, SkillLessonKinds.Failure, "已解决", [], null, "g", 500, true),
            new SkillLesson("sl_few", "docx", null, SkillLessonKinds.Failure, "样本少", [], null, "g", 4000, false),
        };

        var ids = LessonCleanup.SelectDeclined(lessons, events);

        Assert.Equal(["sl_bad"], ids);
    }

    [Fact]
    public void SelectDeclined_NoEvents_ReturnsEmpty()
    {
        var lessons = new[] { new SkillLesson("sl", "docx", null, SkillLessonKinds.Note, "x", [], null, "", 1, false) };
        Assert.Empty(LessonCleanup.SelectDeclined(lessons, []));
        Assert.Empty(LessonCleanup.SelectDeclined(null, null));
    }

    [Fact]
    public void CapsSamplesPerSide_AndHandlesEmpty()
    {
        var many = Enumerable.Range(1, 15).Select(i => Evt(i * 10, 40, false))
            .Concat(Enumerable.Range(1, 15).Select(i => Evt(1000 + i * 10, 90)))
            .ToArray();

        var e = LessonEffectiveness.Evaluate(many, 500)!;
        Assert.Equal(LessonEffectiveness.MaxSamplesPerSide, e.BeforeRuns);
        Assert.Equal(LessonEffectiveness.MaxSamplesPerSide, e.AfterRuns);
        Assert.Equal(LessonEffectiveness.Improved, e.Status);

        Assert.Null(LessonEffectiveness.Evaluate([], 500));
        Assert.Null(LessonEffectiveness.Evaluate(null, 500));
    }
}
