using AguiGroupChat.Agents;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>
/// 自动修复门控（1.0.360，纯函数）：只卡自动这条线，人工修复不受影响；
/// 冷却 + 每日上限防止反复刷模型费用。
/// </summary>
public sealed class SkillAutoRepairPolicyTests
{
    private const long Minute = 60_000;
    private const long Hour = 60 * Minute;
    private const long Now = 1_700_000_000_000;

    private static SkillLoopEvent Evt(string source, long atMs, string outcome = SkillLoopOutcomes.Failed)
        => new("lp_x", "sk1", outcome, 1, 100, 0, false, atMs) { Source = source };

    [Fact]
    public void NoHistory_Allows()
    {
        var d = SkillAutoRepairPolicy.Evaluate([], Now);
        Assert.True(d.Allowed);
        Assert.Equal("ok", d.Reason);
    }

    [Fact]
    public void RecentAutoAttempt_IsCoolingDown()
    {
        var d = SkillAutoRepairPolicy.Evaluate([Evt(SkillRepairSources.Auto, Now - 10 * Minute)], Now);
        Assert.False(d.Allowed);
        Assert.Equal("cooldown", d.Reason);
    }

    [Fact]
    public void RecentManualAttempt_DoesNotBlockAuto()
    {
        // 人工刚修过：自动修复仍可进行（人工修复不该被自动化“抢跑”判定掉）
        var d = SkillAutoRepairPolicy.Evaluate([Evt(SkillRepairSources.Manual, Now - 5 * Minute)], Now);
        Assert.True(d.Allowed);
    }

    [Fact]
    public void AutoAttemptBeyondCooldown_IsAllowed()
    {
        var d = SkillAutoRepairPolicy.Evaluate([Evt(SkillRepairSources.Auto, Now - 2 * Hour)], Now);
        Assert.True(d.Allowed);
    }

    [Fact]
    public void ReachingDailyCap_StopsAutoRepair()
    {
        var events = new[]
        {
            Evt(SkillRepairSources.Auto, Now - 5 * Hour),
            Evt(SkillRepairSources.Auto, Now - 4 * Hour),
            Evt(SkillRepairSources.Auto, Now - 3 * Hour),
        };

        var d = SkillAutoRepairPolicy.Evaluate(events, Now, cooldownMinutes: 60, maxPerDay: 3);

        Assert.False(d.Allowed);
        Assert.Equal("daily-cap", d.Reason);
    }

    [Fact]
    public void OldAutoAttempts_AreOutsideTheDay()
    {
        var events = new[]
        {
            Evt(SkillRepairSources.Auto, Now - 25 * Hour),
            Evt(SkillRepairSources.Auto, Now - 26 * Hour),
            Evt(SkillRepairSources.Auto, Now - 27 * Hour),
        };

        var d = SkillAutoRepairPolicy.Evaluate(events, Now, cooldownMinutes: 60, maxPerDay: 3);

        Assert.True(d.Allowed);
    }

    [Fact]
    public void ManualAttempts_DoNotCountTowardTheDailyCap()
    {
        var events = new[]
        {
            Evt(SkillRepairSources.Manual, Now - 5 * Hour),
            Evt(SkillRepairSources.Manual, Now - 4 * Hour),
            Evt(SkillRepairSources.Manual, Now - 3 * Hour),
        };

        var d = SkillAutoRepairPolicy.Evaluate(events, Now, cooldownMinutes: 60, maxPerDay: 3);

        Assert.True(d.Allowed);
    }

    [Fact]
    public void ZeroCapMeansUnlimited()
    {
        var events = Enumerable.Range(0, 10)
            .Select(i => Evt(SkillRepairSources.Auto, Now - (i + 2) * Hour))
            .ToList();

        var d = SkillAutoRepairPolicy.Evaluate(events, Now, cooldownMinutes: 60, maxPerDay: 0);

        Assert.True(d.Allowed);
    }

    [Fact]
    public void FutureEvents_AreIgnored()
    {
        var d = SkillAutoRepairPolicy.Evaluate([Evt(SkillRepairSources.Auto, Now + 5 * Hour)], Now);
        Assert.True(d.Allowed);
    }

    [Fact]
    public void Queue_CountsEnqueueAndProcessed()
    {
        var queue = new SkillAutoRepairQueue();
        Assert.True(queue.TryEnqueue("sk1", "test"));
        Assert.False(queue.TryEnqueue("", "test"));
        Assert.Equal(1, queue.Queued);
        Assert.Equal(0, queue.Processed);
    }
}
