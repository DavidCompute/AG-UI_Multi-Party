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
