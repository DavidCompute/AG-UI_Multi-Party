using AguiGroupChat.Agents;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>重复性定时任务（1.4）测试：cron 校验 / 到期取数 / 分钟级去重 / 启停。</summary>
public sealed class ScheduledTaskTests
{
    private static ScheduledTask Task(string id, string cron, bool enabled = true, string? groupId = null)
        => new() { TaskId = id, AgentId = "agent_a", Name = "任务", Cron = cron, GroupId = groupId, Enabled = enabled };

    [Fact]
    public void ValidateCron_AcceptsValid_RejectsInvalid()
    {
        Assert.Null(ScheduledTaskService.ValidateCron("0 9 * * *"));
        Assert.Null(ScheduledTaskService.ValidateCron("*/30 * * * *"));
        Assert.NotNull(ScheduledTaskService.ValidateCron("not-a-cron"));
        Assert.NotNull(ScheduledTaskService.ValidateCron(""));
    }

    [Fact]
    public void Due_MatchesCron_AtHitTime_ButNotOtherTime()
    {
        var svc = new ScheduledTaskService();
        svc.Upsert(Task("t1", "0 9 * * *")); // 每天 9:00 UTC
        var hit = new DateTimeOffset(2026, 1, 5, 9, 0, 0, TimeSpan.Zero);
        var miss = new DateTimeOffset(2026, 1, 5, 10, 0, 0, TimeSpan.Zero);

        Assert.Single(svc.Due(hit));
        Assert.Empty(svc.Due(miss));
    }

    [Fact]
    public void Due_DisabledTask_NotTriggered()
    {
        var svc = new ScheduledTaskService();
        svc.Upsert(Task("t1", "0 9 * * *", enabled: false));
        Assert.Empty(svc.Due(new DateTimeOffset(2026, 1, 5, 9, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void Due_DeduplicatesWithinSameMinute()
    {
        var svc = new ScheduledTaskService();
        svc.Upsert(Task("t1", "0 9 * * *"));
        var hit = new DateTimeOffset(2026, 1, 5, 9, 0, 0, TimeSpan.Zero);
        Assert.Single(svc.Due(hit));
        Assert.Empty(svc.Due(hit)); // 同一分钟不重复
        // 下一分钟（不同 cron 时刻）应再次可触发
        var next = hit.AddDays(1);
        Assert.Single(svc.Due(next));
    }

    [Fact]
    public void AddRemove_RoundTrip()
    {
        var svc = new ScheduledTaskService();
        svc.Upsert(Task("t1", "0 9 * * *"));
        svc.Upsert(Task("t2", "30 18 * * 1-5"));
        Assert.Equal(2, svc.List().Count);
        Assert.True(svc.Remove("t1"));
        Assert.Single(svc.List());

        // 快照 + 恢复（持久化 round-trip）
        var snap = svc.Snapshot();
        var svc2 = new ScheduledTaskService();
        svc2.Restore(snap);
        Assert.Single(svc2.List());
        Assert.Equal("30 18 * * 1-5", svc2.List()[0].Cron);
    }
}
