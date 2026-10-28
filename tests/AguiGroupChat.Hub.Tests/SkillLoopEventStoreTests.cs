using AguiGroupChat.Agents;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>
/// 自愈闭环指标（1.0.358）：事件库边界与汇总口径（成功率排除 skipped、平均耗时口径）。
/// </summary>
public sealed class SkillLoopEventStoreTests
{
    private static SkillLoopEvent Evt(string skillId, string outcome, long durationMs,
        int regressionRan = 0, bool regressionPassed = false, long atMs = 1)
        => new("lp_" + skillId + outcome + atMs, skillId, outcome, 2, durationMs, regressionRan, regressionPassed, atMs);

    [Fact]
    public void Summarize_CountsOutcomesAndExcludesSkippedFromRateAndAverage()
    {
        var events = new[]
        {
            Evt("a", SkillLoopOutcomes.Applied, 100),
            Evt("a", SkillLoopOutcomes.Resumed, 300),
            Evt("b", SkillLoopOutcomes.Failed, 500),
            Evt("b", SkillLoopOutcomes.RolledBack, 700, regressionRan: 2, regressionPassed: false),
            Evt("c", SkillLoopOutcomes.Skipped, 900),
        };

        var s = SkillLoopMetrics.Summarize(events);

        Assert.Equal(5, s.Attempts);
        Assert.Equal(1, s.Applied);
        Assert.Equal(1, s.Resumed);
        Assert.Equal(1, s.Failed);
        Assert.Equal(1, s.RolledBack);
        Assert.Equal(1, s.Skipped);
        Assert.Equal(0.5, s.SuccessRate); // (1+1)/(5-1)
        Assert.Equal(400, s.AvgMs);       // (100+300+500+700)/4
        Assert.Equal(2, s.RegressionRuns);
        Assert.Equal(0, s.RegressionPassed);
    }

    [Fact]
    public void Summarize_EmptyAndAllSkipped()
    {
        var empty = SkillLoopMetrics.Summarize([]);
        Assert.Equal(0, empty.Attempts);
        Assert.Equal(0, empty.SuccessRate);
        Assert.Equal(0, empty.AvgMs);

        var skipped = SkillLoopMetrics.Summarize([Evt("c", SkillLoopOutcomes.Skipped, 50)]);
        Assert.Equal(1, skipped.Attempts);
        Assert.Equal(0, skipped.SuccessRate);
        Assert.Equal(0, skipped.AvgMs);
    }

    [Fact]
    public void Summarize_CountsRegressionPassWhenRan()
    {
        var s = SkillLoopMetrics.Summarize(
        [
            Evt("a", SkillLoopOutcomes.Applied, 100, regressionRan: 3, regressionPassed: true),
            Evt("a", SkillLoopOutcomes.RolledBack, 100, regressionRan: 2, regressionPassed: false),
        ]);

        Assert.Equal(5, s.RegressionRuns);
        Assert.Equal(1, s.RegressionPassed);
    }

    [Fact]
    public void Store_RecordsBoundAndRoundTrips()
    {
        var store = new SkillLoopEventStore();
        store.Record("s1", SkillLoopOutcomes.Applied, 2, 120, 0, true);
        store.Record("", SkillLoopOutcomes.Applied, 1, 10, 0, true); // 无技能：不落库
        store.Record("s2", SkillLoopOutcomes.RolledBack, 3, 900, 2, false);

        var snap = store.Snapshot();
        Assert.Equal(2, snap.Count);
        Assert.Equal(2, store.Query(0).Count);
        Assert.Empty(store.Query(long.MaxValue));

        var dst = new SkillLoopEventStore();
        dst.RestoreState(snap);
        Assert.Equal(2, dst.Snapshot().Count);

        dst.RestoreState([new SkillLoopEvent("lp_bad", "", SkillLoopOutcomes.Applied, 1, 1, 0, true, 1)]);
        Assert.Empty(dst.Snapshot());
    }

    [Fact]
    public void Store_IsBounded()
    {
        var store = new SkillLoopEventStore();
        for (var i = 0; i < SkillLoopEventStore.MaxEvents + 5; i++)
            store.Record("s", SkillLoopOutcomes.Failed, 1, i, 0, false);

        Assert.Equal(SkillLoopEventStore.MaxEvents, store.Snapshot().Count);
    }

    [Fact]
    public void BySkill_AggregatesPerSkillAndExcludesSkippedFromAverage()
    {
        var events = new[]
        {
            Evt("a", SkillLoopOutcomes.Applied, 100, atMs: 10),
            Evt("a", SkillLoopOutcomes.RolledBack, 300, atMs: 20),
            Evt("a", SkillLoopOutcomes.Skipped, 999, atMs: 30),
            Evt("b", SkillLoopOutcomes.Resumed, 50, atMs: 40),
        };

        var rows = SkillLoopMetrics.BySkill(events);

        Assert.Equal(2, rows.Count);
        var a = rows.Single(r => r.SkillId == "a");
        Assert.Equal(3, a.Attempts);     // skipped 计入尝试数
        Assert.Equal(1, a.Applied);      // applied + resumed
        Assert.Equal(1, a.RolledBack);
        Assert.Equal(1, a.Skipped);
        Assert.Equal(200, a.AvgMs);      // 只算 applied/rolledback：(100+300)/2
        Assert.Equal(30, a.LastMs);

        var b = rows.Single(r => r.SkillId == "b");
        Assert.Equal(1, b.Attempts);
        Assert.Equal(1, b.Applied);
        Assert.Equal(50, b.AvgMs);
    }

    [Fact]
    public void BySkill_EmptyAndAllSkipped()
    {
        Assert.Empty(SkillLoopMetrics.BySkill([]));

        var rows = SkillLoopMetrics.BySkill([Evt("a", SkillLoopOutcomes.Skipped, 500)]);
        Assert.Single(rows);
        Assert.Equal(1, rows[0].Attempts);
        Assert.Equal(0, rows[0].Applied);
        Assert.Equal(0, rows[0].AvgMs);
    }
}
