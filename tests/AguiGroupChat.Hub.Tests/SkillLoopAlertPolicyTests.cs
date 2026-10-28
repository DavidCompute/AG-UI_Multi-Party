using AguiGroupChat.Agents;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>
/// 自愈闭环告警判定（1.0.359，纯函数）：回滚 / 失败 / 低成功率三条判据与窗口口径。
/// </summary>
public sealed class SkillLoopAlertPolicyTests
{
    private static SkillLoopEvent Evt(string outcome, long atMs)
        => new("lp_" + outcome + atMs, "sk1", outcome, 1, 100, 0, true, atMs);

    [Fact]
    public void TwoRollbacks_AlertForRollback()
    {
        var events = new[]
        {
            Evt(SkillLoopOutcomes.RolledBack, 1),
            Evt(SkillLoopOutcomes.RolledBack, 2),
        };

        var d = SkillLoopAlertPolicy.Evaluate(events);

        Assert.True(d.ShouldAlert);
        Assert.Equal(2, d.Attempts);
        Assert.Equal(2, d.RolledBack);
        Assert.Contains("rollback", d.Reasons);
        Assert.Contains("low-success", d.Reasons); // 成功率 0/2 同样命中低成功率
    }

    [Fact]
    public void TwoFailures_AlertForFailureAndLowSuccess()
    {
        var events = new[]
        {
            Evt(SkillLoopOutcomes.Failed, 1),
            Evt(SkillLoopOutcomes.Failed, 2),
            Evt(SkillLoopOutcomes.Applied, 3),
        };

        var d = SkillLoopAlertPolicy.Evaluate(events);

        Assert.True(d.ShouldAlert);
        Assert.Equal(3, d.Attempts);
        Assert.Equal(1, d.Successes);
        Assert.Equal(0.3333, Math.Round(d.SuccessRate, 4));
        Assert.Contains("failure", d.Reasons);
        Assert.Contains("low-success", d.Reasons);
        Assert.DoesNotContain("rollback", d.Reasons);
    }

    [Fact]
    public void SingleFailure_DoesNotAlert()
    {
        var d = SkillLoopAlertPolicy.Evaluate([Evt(SkillLoopOutcomes.Failed, 1)]);

        Assert.False(d.ShouldAlert);
        Assert.Equal(1, d.Attempts);
        Assert.Empty(d.Reasons);
    }

    [Fact]
    public void SkippedEvents_DoNotCountAsAttempts()
    {
        // 只有 skipped：不是真实尝试 → 不告警
        var d = SkillLoopAlertPolicy.Evaluate(
        [
            Evt(SkillLoopOutcomes.Skipped, 1),
            Evt(SkillLoopOutcomes.Skipped, 2),
            Evt(SkillLoopOutcomes.Skipped, 3),
        ]);
        Assert.False(d.ShouldAlert);
        Assert.Equal(0, d.Attempts);
    }

    [Fact]
    public void SkippedEvents_DoNotPushFailuresOutOfWindow()
    {
        // 窗口 3：两次回滚之间夹着 skipped，仍应看到 2 次回滚
        var events = new[]
        {
            Evt(SkillLoopOutcomes.RolledBack, 1),
            Evt(SkillLoopOutcomes.Skipped, 2),
            Evt(SkillLoopOutcomes.RolledBack, 3),
            Evt(SkillLoopOutcomes.Applied, 4),
        };

        var d = SkillLoopAlertPolicy.Evaluate(events, windowAttempts: 3);

        Assert.True(d.ShouldAlert);
        Assert.Equal(3, d.Attempts); // rolledback / rolledback / applied
        Assert.Contains("rollback", d.Reasons);
    }

    [Fact]
    public void OldFailures_OutsideWindow_DoNotAlert()
    {
        var events = new[]
        {
            Evt(SkillLoopOutcomes.RolledBack, 1),
            Evt(SkillLoopOutcomes.RolledBack, 2),
            Evt(SkillLoopOutcomes.Applied, 3),
            Evt(SkillLoopOutcomes.Resumed, 4),
            Evt(SkillLoopOutcomes.Applied, 5),
        };

        var d = SkillLoopAlertPolicy.Evaluate(events, windowAttempts: 3);

        Assert.False(d.ShouldAlert);
        Assert.Equal(3, d.Attempts);
        Assert.Equal(1.0, d.SuccessRate);
    }

    [Fact]
    public void SuccessRateExactlyAtThreshold_DoesNotTriggerLowSuccess()
    {
        // 1 成功 + 1 失败 → 50%，不命中 low-success，但失败次数未到 2 → 不告警
        var d = SkillLoopAlertPolicy.Evaluate(
        [
            Evt(SkillLoopOutcomes.Applied, 1),
            Evt(SkillLoopOutcomes.Failed, 2),
        ]);

        Assert.False(d.ShouldAlert);
        Assert.Equal(0.5, d.SuccessRate);
    }

    [Fact]
    public void AverageDuration_ExcludesSkipped()
    {
        var events = new[]
        {
            new SkillLoopEvent("lp_a", "sk1", SkillLoopOutcomes.Failed, 1, 200, 0, false, 1),
            new SkillLoopEvent("lp_b", "sk1", SkillLoopOutcomes.Failed, 1, 400, 0, false, 2),
            new SkillLoopEvent("lp_c", "sk1", SkillLoopOutcomes.Skipped, 1, 9000, 0, true, 3),
        };

        var d = SkillLoopAlertPolicy.Evaluate(events);

        Assert.True(d.ShouldAlert);
        Assert.Equal(300, d.AvgMs);
    }

    [Fact]
    public void AlertStore_IsBoundedAndFiltersSince()
    {
        var store = new SkillLoopAlertStore();
        for (var i = 0; i < SkillLoopAlertStore.MaxAlerts + 5; i++)
            store.Add(new SkillLoopAlert("sk1", "技能", 2, 0, 2, 0, 0, 100, ["rollback"], false, i));

        Assert.Equal(SkillLoopAlertStore.MaxAlerts, store.Snapshot().Count);
        Assert.Equal(105, store.Snapshot(100).Count); // 保留 5..204，其中 ≥100 的 105 条
    }
}
