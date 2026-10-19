using System.Text.Json;
using AguiGroupChat.Hub.Models;
using AguiGroupChat.Web;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>
/// 临时放额到期提醒（1.0.274 / 1.0.275）：扫描「覆写仍生效 + 有到期时间」的知聚，
/// 进入提醒窗口发 QUOTA_OVERRIDE_EXPIRING，到期后的宽限期内发一次 QUOTA_OVERRIDE_EXPIRED；
/// 同一额度每种事件只发一次，取消 / 改期 / 到期后去重键被清理。
/// </summary>
public sealed class QuotaExpiryReminderTests
{
    private static QuotaExpiryReminderService NewService(HubFixture f, ConfigGovernanceState? cfg = null)
        => new(f.Hub, cfg ?? new ConfigGovernanceState(), dispatcher: null, NullLogger<QuotaExpiryReminderService>.Instance);

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
        var evt = Assert.Single(first.Expiring);
        Assert.Empty(first.Expired);
        Assert.Equal("QUOTA_OVERRIDE_EXPIRING", evt.Type);
        Assert.Equal(group.GroupId, evt.GroupId);
        Assert.Equal("即将到期知聚", evt.GroupName);
        Assert.Equal(5000, evt.Quota);
        Assert.Equal(until, evt.Until);
        Assert.InRange(evt.RemainingSeconds, 7199, 7200);

        // 同一额度：后续扫描不重复提醒（即使仍在窗口内）
        Assert.Empty(svc.RunOnce(f.Hub.NowMs).Expiring);
        clock.Advance(TimeSpan.FromHours(1));
        Assert.Empty(svc.RunOnce(f.Hub.NowMs).Expiring);

        // 已到期：不再发「即将到期」，但存储旧值保留
        clock.Advance(TimeSpan.FromHours(2));
        Assert.Empty(svc.RunOnce(f.Hub.NowMs).Expiring);
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
        Assert.Empty(svc.RunOnce(f.Hub.NowMs).Expiring);   // 48h > 24h 窗口

        clock.Advance(TimeSpan.FromHours(25));            // 剩 23h → 进入窗口
        var evt = Assert.Single(svc.RunOnce(f.Hub.NowMs).Expiring);
        Assert.Equal(group.GroupId, evt.GroupId);
        Assert.Equal(8000, evt.Quota);
    }

    [Fact]
    public async Task RunOnce_LongTermOverride_NoReminder()
    {
        var f = new HubFixture();
        var group = await HubFixture.CreateGroupAsync(f.Hub, "长期放额", "user_1", "user_2");
        await SetAllowanceAsync(f, group.GroupId, 3000, until: null); // 无到期时间

        var result = NewService(f).RunOnce(f.Hub.NowMs);
        Assert.Empty(result.Expiring);
        Assert.Empty(result.Expired);
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
        Assert.Single(svc.RunOnce(f.Hub.NowMs).Expiring);

        // 管理员把到期时间改到新的（仍在窗口内）→ 新额度应重新提醒
        var secondUntil = clock.GetUtcNow().AddHours(5).ToUnixTimeMilliseconds();
        await SetAllowanceAsync(f, group.GroupId, 6000, secondUntil);
        var evt = Assert.Single(svc.RunOnce(f.Hub.NowMs).Expiring);
        Assert.Equal(secondUntil, evt.Until);
        Assert.Equal(6000, evt.Quota);

        // 同一新额度不再重复
        Assert.Empty(svc.RunOnce(f.Hub.NowMs).Expiring);
    }

    [Fact]
    public async Task RunOnce_LeadHoursAreConfigurable()
    {
        var clock = new FakeClock();
        var f = new HubFixture(time: clock);
        var cfg = new ConfigGovernanceState { QuotaReminderLeadHours = 48, QuotaReminderScanMinutes = 30 };
        var group = await HubFixture.CreateGroupAsync(f.Hub, "提前两天提醒", "user_1", "user_2");
        var until = clock.GetUtcNow().AddHours(40).ToUnixTimeMilliseconds();
        await SetAllowanceAsync(f, group.GroupId, 7000, until);

        var svc = NewService(f, cfg);
        Assert.Equal(48, svc.LeadHours);
        Assert.Equal(30, svc.ScanMinutes);

        // 40h > 默认 24h，但配置成 48h 后应提醒
        var evt = Assert.Single(svc.RunOnce(f.Hub.NowMs).Expiring);
        Assert.Equal(group.GroupId, evt.GroupId);
        Assert.Equal(7000, evt.Quota);
    }

    [Fact]
    public async Task RunOnce_EmitsExpiredOnceWithinGrace()
    {
        var clock = new FakeClock();
        var f = new HubFixture(time: clock);
        var group = await HubFixture.CreateGroupAsync(f.Hub, "已到期知聚", "user_1", "user_2");
        var until = clock.GetUtcNow().AddHours(-1).ToUnixTimeMilliseconds();
        await SetAllowanceAsync(f, group.GroupId, 4000, until);

        var svc = NewService(f);
        var result = svc.RunOnce(f.Hub.NowMs);
        Assert.Empty(result.Expiring);
        var evt = Assert.Single(result.Expired);
        Assert.Equal("QUOTA_OVERRIDE_EXPIRED", evt.Type);
        Assert.Equal(group.GroupId, evt.GroupId);
        Assert.Equal("已到期知聚", evt.GroupName);
        Assert.Equal(4000, evt.Quota);
        Assert.Equal(until, evt.Until);
        Assert.InRange(evt.ExpiredSeconds, 3599, 3600);

        // 同一额度只补发一次
        Assert.Empty(svc.RunOnce(f.Hub.NowMs).Expired);
    }

    [Fact]
    public async Task RunOnce_OldExpiryBeyondGrace_NoExpiredEvent()
    {
        var clock = new FakeClock();
        var f = new HubFixture(time: clock);
        var group = await HubFixture.CreateGroupAsync(f.Hub, "很久前到期", "user_1", "user_2");
        var until = clock.GetUtcNow().AddHours(-48).ToUnixTimeMilliseconds();
        await SetAllowanceAsync(f, group.GroupId, 4000, until);

        // 超过 24h 宽限期：重启 / 升级后不会为历史数据洪水式补发
        var svc = NewService(f);
        Assert.Empty(svc.RunOnce(f.Hub.NowMs).Expired);
        Assert.Empty(svc.RunOnce(f.Hub.NowMs).Expired);
    }

    [Fact]
    public async Task RunOnce_ClearedAllowance_StopsBothEvents()
    {
        var clock = new FakeClock();
        var f = new HubFixture(time: clock);
        var group = await HubFixture.CreateGroupAsync(f.Hub, "取消放额", "user_1", "user_2");
        var until = clock.GetUtcNow().AddHours(2).ToUnixTimeMilliseconds();
        await SetAllowanceAsync(f, group.GroupId, 5000, until);

        var svc = NewService(f);
        Assert.Single(svc.RunOnce(f.Hub.NowMs).Expiring);

        await SetAllowanceAsync(f, group.GroupId, -1, null); // 取消覆写（同时清到期时间）
        var result = svc.RunOnce(f.Hub.NowMs);
        Assert.Empty(result.Expiring);
        Assert.Empty(result.Expired);
    }
}
