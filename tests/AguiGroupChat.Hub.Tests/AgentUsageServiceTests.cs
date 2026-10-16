using AguiGroupChat.Hub.Agents;
using AguiGroupChat.Hub.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>模型用量统计与配额（内存 store）。</summary>
public sealed class AgentUsageServiceTests
{
    private static AgentUsageService Create(long quota = 0)
        => new(new InMemoryUsageStore(), quota, NullLogger<AgentUsageService>.Instance);

    /// <summary>主构造（委托实时读配额）：供热改 / 知聚配额用例使用。</summary>
    private static AgentUsageService CreateLive(Func<long> quotaPerUser, Func<long> quotaPerGroup)
        => new(new InMemoryUsageStore(), quotaPerUser, quotaPerGroup, NullLogger<AgentUsageService>.Instance);

    [Fact]
    public void RecordUsage_AccumulatesPerDayPerAgentPerUser()
    {
        var svc = Create();
        svc.RecordUsage("agent_a", "user_1", 100, 50, 10);
        svc.RecordUsage("agent_a", "user_1", 200, 30, 5);   // 同键累加
        svc.RecordUsage("agent_b", "user_1", 10, 10, 0);    // 不同智能体
        svc.RecordUsage("agent_a", "user_2", 5, 5, 0);      // 不同用户

        Assert.Equal(100 + 200 + 50 + 30 + 10 + 5 + 10 + 10, svc.GetUserTodayTokens("user_1")); // 415
        Assert.Equal(10, svc.GetUserTodayTokens("user_2"));
        Assert.Equal(0, svc.GetUserTodayTokens("nobody"));
    }

    [Fact]
    public void RecordUsage_IgnoresZeroOrNegative()
    {
        var svc = Create();
        svc.RecordUsage("agent_a", "user_1", 0, 0, 0);
        Assert.Equal(0, svc.GetUserTodayTokens("user_1"));
    }

    [Fact]
    public void Quota_EnforcedPerUser_SystemExempt()
    {
        var svc = Create(quota: 100);
        Assert.Null(svc.CheckUserQuota("user_1")); // 未用满

        svc.RecordUsage("agent_a", "user_1", 60, 40, 0); // 达到 100
        var hit = svc.CheckUserQuota("user_1");
        Assert.NotNull(hit);
        Assert.Equal(100, hit!.Quota);
        Assert.Equal(100, hit.Used);
        Assert.Equal(0, hit.Remaining);

        // 定时任务（system）不受个人配额限制
        Assert.Null(svc.CheckUserQuota(AgentUsageService.SystemUserId));
    }

    [Fact]
    public void DailyQuotaPerUser_IsReadLive_NotCopiedAtConstruction()
    {
        // 回归：曾把配额在构造时拷贝成字段，导致「配置治理」热改配额只重启后才生效（静默失效）。
        var quota = 0L;
        var svc = CreateLive(() => quota, () => 0);
        Assert.Equal(0, svc.DailyQuotaPerUser);

        quota = 500; // 管理员热改（改的是被委托读取的那个值）
        Assert.Equal(500, svc.DailyQuotaPerUser);

        svc.RecordUsage("agent_a", "user_1", 300, 200, 0); // 达到 500
        Assert.NotNull(svc.CheckUserQuota("user_1"));

        quota = 0; // 再热改为不限 → 立即解除
        Assert.Null(svc.CheckUserQuota("user_1"));
    }

    [Fact]
    public void OldConstructor_KeepsFixedQuotaAndDisablesGroupQuota()
    {
        var svc = Create(quota: 100);
        Assert.Equal(100, svc.DailyQuotaPerUser);
        Assert.Equal(0, svc.DailyQuotaPerGroup);
        svc.RecordUsage("agent_a", "user_1", "group_k", 80, 20, 0);
        Assert.Null(svc.CheckGroupQuota("group_k")); // 旧构造不限知聚
    }

    [Fact]
    public void GroupQuota_Enforced_IncludesSystemTrigger()
    {
        var svc = CreateLive(() => 0, () => 100);
        Assert.Null(svc.CheckGroupQuota("group_k")); // 未用满

        // 定时任务以虚拟触发者 system 发起：不计个人配额，但**计入知聚总账**
        svc.RecordUsage("agent_a", AgentUsageService.SystemUserId, "group_k", 60, 40, 0);
        Assert.Equal(100, svc.GetGroupTodayTokens("group_k"));

        var hit = svc.CheckGroupQuota("group_k");
        Assert.NotNull(hit);
        Assert.Equal(100, hit!.Quota);
        Assert.Equal(100, hit.Used);

        // 个人配额不豁免 system，但这里按 user 配额为 0（不限），故个人检查返回 null
        Assert.Null(svc.CheckUserQuota(AgentUsageService.SystemUserId));
    }

    [Fact]
    public void GetGroupTodayTokens_DoesNotCrossGroups()
    {
        var svc = CreateLive(() => 0, () => 0);
        svc.RecordUsage("agent_a", "user_1", "group_k1", 100, 50, 10);
        svc.RecordUsage("agent_a", "user_1", "group_k2", 7, 3, 0);

        Assert.Equal(160, svc.GetGroupTodayTokens("group_k1"));
        Assert.Equal(10, svc.GetGroupTodayTokens("group_k2"));
        Assert.Equal(0, svc.GetGroupTodayTokens("group_unknown"));
        Assert.Equal(0, svc.GetGroupTodayTokens("")); // 无群上下文不计
    }

    [Fact]
    public void GetDailySummary_AggregatesByDay()
    {
        var svc = Create();
        svc.RecordUsage("agent_a", "user_1", 100, 50, 10);
        var summary = svc.GetDailySummary(7);
        var today = Assert.Single(summary);
        Assert.Equal(AgentUsageService.Today(), today.Date);
        Assert.Equal(160, today.TotalTokens);
        Assert.Equal(100, today.PromptTokens);
        Assert.Equal(50, today.CompletionTokens);
        Assert.Equal(10, today.ReasoningTokens);
        Assert.Equal(1, today.Calls);
    }
}
