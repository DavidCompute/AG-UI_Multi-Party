using System.Text.Json;
using AguiGroupChat.Hub.Models;
using AguiGroupChat.Web;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>
/// 临时放额到期提醒（1.0.274）：只扫描「覆写仍生效 + 有到期时间 + 进入 24 小时提醒窗口」的知聚，
/// 同一额度（groupId + until）进程内只提醒一次；额度取消 / 改期 / 到期后去重键被清理。
/// </summary>
public sealed class QuotaExpiryReminderTests
{
    private static QuotaExpiryReminderService NewService(HubFixture f)
        => new(f.Hub, dispatcher: null, NullLogger<QuotaExpiryReminderService>.Instance);

    private static Task SetAllowanceAsync(HubFixture f, string gid, long quota, long? until)
    {
        var info = new Dictionary<string, JsonElement> { ["quotaPerGroup"] = JsonSerializer.SerializeToElement(quota) };
        var fields = new List<string> { "quotaPerGroup" };
        if (until is { } u)
        {
            info["quotaPerGroupUntil"] = JsonSerializer.SerializeToElement(u);
            fields.Add("quotaPerGroupUntil");
        }
        return f.Hub.UpdateGroupAsync(new GroupUpdateRequest
        {
            GroupId = gid,
            OperatorId = "user_1",
            UpdateFields = fields.ToArray(),
            GroupInfo = info,
        });
    }

    [Fact]
    public async Task RunOnce_EmitsInsideLeadWindow_OncePerAllowance()
    {
        var clock = new FakeClock();
        var f = new HubFixture(time: clock);
        var group = await HubFixture.CreateGroupAsync(f.Hub, "即将到期知聚", "user_1", "user_2");
        var until = clock.GetUtcNow().AddHours(2).ToUnixTimeMilliseconds();
        await SetAllowanceAsync(f, group.GroupId, 5000, until);

        var svc = NewService(f);
        var first = svc.RunOnce(f.Hub.NowMs);
        var evt = Assert.Single(first);
        Assert.Equal("QUOTA_OVERRIDE_EXPIRING", evt.Type);
        Assert.Equal(group.GroupId, evt.GroupId);
        Assert.Equal("即将到期知聚", evt.GroupName);
        Assert.Equal(5000, evt.Quota);
        Assert.Equal(until, evt.Until);
        Assert.InRange(evt.RemainingSeconds, 7199, 7200);

        // 同一额度：后续扫描不重复提醒（即使仍在窗口内）
        Assert.Empty(svc.RunOnce(f.Hub.NowMs));
        clock.Advance(TimeSpan.FromHours(1));
        Assert.Empty(svc.RunOnce(f.Hub.NowMs));

        // 已到期：不再提醒，但存储旧值保留
        clock.Advance(TimeSpan.FromHours(2));
        Assert.Empty(svc.RunOnce(f.Hub.NowMs));
        Assert.Equal(5000, f.Store.GetGroup(group.GroupId)!.QuotaOverride);
        Assert.Equal(until, f.Store.GetGroup(group.GroupId)!.QuotaOverrideUntil);
    }

    [Fact]
    public async Task RunOnce_OutsideLeadWindow_DoesNotEmitUntilItEnters()
    {
        var clock = new FakeClock();
        var f = new HubFixture(time: clock);
        var group = await HubFixture.CreateGroupAsync(f.Hub, "两天后到期", "user_1", "user_2");
        var until = clock.GetUtcNow().AddHours(48).ToUnixTimeMilliseconds();
        await SetAllowanceAsync(f, group.GroupId, 8000, until);

        var svc = NewService(f);
        Assert.Empty(svc.RunOnce(f.Hub.NowMs));           // 48h > 24h 窗口

        clock.Advance(TimeSpan.FromHours(25));            // 剩 23h → 进入窗口
        var evt = Assert.Single(svc.RunOnce(f.Hub.NowMs));
        Assert.Equal(group.GroupId, evt.GroupId);
        Assert.Equal(8000, evt.Quota);
    }

    [Fact]
    public async Task RunOnce_LongTermOverride_NoReminder()
    {
        var f = new HubFixture();
        var group = await HubFixture.CreateGroupAsync(f.Hub, "长期放额", "user_1", "user_2");
        await SetAllowanceAsync(f, group.GroupId, 3000, until: null); // 无到期时间

        Assert.Empty(NewService(f).RunOnce(f.Hub.NowMs));
    }

    [Fact]
    public async Task RunOnce_ChangedExpiry_RaisesForNewDeadline()
    {
        var clock = new FakeClock();
        var f = new HubFixture(time: clock);
        var group = await HubFixture.CreateGroupAsync(f.Hub, "改期放额", "user_1", "user_2");
        var firstUntil = clock.GetUtcNow().AddHours(2).ToUnixTimeMilliseconds();
        await SetAllowanceAsync(f, group.GroupId, 5000, firstUntil);

        var svc = NewService(f);
        Assert.Single(svc.RunOnce(f.Hub.NowMs));

        // 管理员把到期时间改到新的（仍在窗口内）→ 新额度应重新提醒
        var secondUntil = clock.GetUtcNow().AddHours(5).ToUnixTimeMilliseconds();
        await SetAllowanceAsync(f, group.GroupId, 6000, secondUntil);
        var evt = Assert.Single(svc.RunOnce(f.Hub.NowMs));
        Assert.Equal(secondUntil, evt.Until);
        Assert.Equal(6000, evt.Quota);

        // 同一新额度不再重复
        Assert.Empty(svc.RunOnce(f.Hub.NowMs));
    }
}
