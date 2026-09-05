using AguiGroupChat.Agents;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>
/// <see cref="ExecutionOptions"/> 出厂默认与规范化（Normalize 非法值回退默认）单测。
/// 目的：锁定「配置化覆盖默认与原 AgentGateway 时序常量一致」这一不变量，非法配置绝不带病运行。
/// </summary>
public sealed class ExecutionOptionsTests
{
    [Fact]
    public void Defaults_MatchOriginalGatewayTimingConstants()
    {
        var d = ExecutionOptions.Default;
        Assert.Equal(5, d.StreamTimeoutMinutes);        // 模型 / 桥接流式调用超时
        Assert.Equal(2, d.MaxModelAttempts);            // 模型流重试上限
        Assert.Equal(10, d.InteractionTtlMinutes);      // 人机交互超时
        Assert.Equal(30, d.SessionLockTtlMinutes);      // 会话锁 TTL
        Assert.Equal(30, d.ApprovedSkillTtlMinutes);    // 已批准客户端技能记忆过期
        Assert.Equal(512, d.SessionLockMaxEntries);     // 会话锁表条目上限
        Assert.Equal(12, d.CoordinatorPlanMaxItems);    // 编排计划清单项
        Assert.Equal(8, d.CoordinatorPlanMaxSteps);     // 编排计划步骤数
        Assert.Equal(5, d.MaxRecursiveRounds);          // 递归补查轮次
        Assert.Equal(4, d.MaxRouteDepth);               // 指派 / 提升路由深度
        Assert.Equal(5, d.MaxInteractionRounds);        // 审批交互轮数
    }

    [Fact]
    public void StreamTimeoutMinutes_IsFiveMinutes()
    {
        Assert.Equal(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(ExecutionOptions.Default.StreamTimeoutMinutes));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public void Normalize_FallsBackToDefaults_OnNonPositive_StreamTimeout(int illegal)
    {
        var exec = new ExecutionOptions { StreamTimeoutMinutes = illegal };
        exec.Normalize();
        Assert.Equal(ExecutionOptions.Default.StreamTimeoutMinutes, exec.StreamTimeoutMinutes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Normalize_FallsBackToDefaults_OnNonPositive_EveryField(int illegal)
    {
        var defaults = ExecutionOptions.Default;
        var exec = new ExecutionOptions
        {
            MaxModelAttempts = illegal,
            InteractionTtlMinutes = illegal,
            SessionLockTtlMinutes = illegal,
            ApprovedSkillTtlMinutes = illegal,
            SessionLockMaxEntries = illegal,
            CoordinatorPlanMaxItems = illegal,
            CoordinatorPlanMaxSteps = illegal,
            MaxRecursiveRounds = illegal,
            MaxRouteDepth = illegal,
            MaxInteractionRounds = illegal,
        };
        exec.Normalize();
        Assert.Equal(defaults.MaxModelAttempts, exec.MaxModelAttempts);
        Assert.Equal(defaults.InteractionTtlMinutes, exec.InteractionTtlMinutes);
        Assert.Equal(defaults.SessionLockTtlMinutes, exec.SessionLockTtlMinutes);
        Assert.Equal(defaults.ApprovedSkillTtlMinutes, exec.ApprovedSkillTtlMinutes);
        Assert.Equal(defaults.SessionLockMaxEntries, exec.SessionLockMaxEntries);
        Assert.Equal(defaults.CoordinatorPlanMaxItems, exec.CoordinatorPlanMaxItems);
        Assert.Equal(defaults.CoordinatorPlanMaxSteps, exec.CoordinatorPlanMaxSteps);
        Assert.Equal(defaults.MaxRecursiveRounds, exec.MaxRecursiveRounds);
        Assert.Equal(defaults.MaxRouteDepth, exec.MaxRouteDepth);
        Assert.Equal(defaults.MaxInteractionRounds, exec.MaxInteractionRounds);
    }

    [Fact]
    public void Normalize_KeepsPositiveConfiguredValues()
    {
        var exec = new ExecutionOptions
        {
            StreamTimeoutMinutes = 9,
            MaxModelAttempts = 3,
            InteractionTtlMinutes = 15,
        };
        exec.Normalize();
        Assert.Equal(9, exec.StreamTimeoutMinutes);
        Assert.Equal(3, exec.MaxModelAttempts);
        Assert.Equal(15, exec.InteractionTtlMinutes);
    }

    [Fact]
    public void Normalize_ReturnsSameInstance()
    {
        var exec = new ExecutionOptions { StreamTimeoutMinutes = -1 };
        Assert.Same(exec, exec.Normalize());
    }

    // ---- Step B：可配执行分派顺序 + 平台 / 角色级开关 ----

    [Fact]
    public void Defaults_ValidExecutionOrder_AndAllSwitchesOn()
    {
        var d = ExecutionOptions.Default;
        Assert.Equal(["bridge", "pipeline", "relay", "org_route", "streaming"], d.ExecutionOrder);
        Assert.True(d.EnableBridge);
        Assert.True(d.EnablePipeline);
        Assert.True(d.EnableRelay);
        Assert.True(d.EnableOrgRoute);
    }

    [Fact]
    public void Normalize_KeepsValidOrder_Untouched()
    {
        var exec = new ExecutionOptions
        {
            EnablePipeline = false,
            ExecutionOrder = ["relay", "pipeline", "org_route", "bridge", "streaming"],
        };
        exec.Normalize();
        Assert.Equal(["relay", "pipeline", "org_route", "bridge", "streaming"], exec.ExecutionOrder);
        Assert.False(exec.EnablePipeline); // 平台开关非白名单成员，保持原值
        Assert.True(exec.EnableBridge);
        Assert.True(exec.EnableRelay);
        Assert.True(exec.EnableOrgRoute);
    }

    [Fact]
    public void Normalize_Dedupes_AndStripsUnknownTokens()
    {
        var exec = new ExecutionOptions
        {
            ExecutionOrder = ["bridge", "bridge", "bogus", "org_route", "relay", "pipeline", ""],
        };
        exec.Normalize();
        // 未知 / 空剔除；重复 bridge 去重一次；相对顺序保留；streaming 恒补至末尾。
        Assert.Equal(["bridge", "org_route", "relay", "pipeline", "streaming"], exec.ExecutionOrder);
    }

    [Fact]
    public void Normalize_AppendsStreamingAtEnd_WhenMissing()
    {
        var exec = new ExecutionOptions { ExecutionOrder = ["bridge", "org_route"] };
        exec.Normalize();
        Assert.Equal(["bridge", "org_route", "streaming"], exec.ExecutionOrder);
    }

    [Fact]
    public void Normalize_MovesStreamingToEnd_IfSuppliedEarly()
    {
        var exec = new ExecutionOptions { ExecutionOrder = ["streaming", "bridge", "relay"] };
        exec.Normalize();
        Assert.Equal(["bridge", "relay", "streaming"], exec.ExecutionOrder);
    }

    [Fact]
    public void Normalize_FallsBackToDefaultOrder_OnWholeInvalid()
    {
        // 全部是非白名单 token / 空 → 整表非法，回退全默认顺序。
        var exec = new ExecutionOptions { ExecutionOrder = ["bogus", "", "wat"] };
        exec.Normalize();
        Assert.Equal(ExecutionOptions.Default.ExecutionOrder, exec.ExecutionOrder);
    }

    [Fact]
    public void Normalize_FallsBackToDefaultOrder_OnEmptyOrder()
    {
        var exec = new ExecutionOptions { ExecutionOrder = [] };
        exec.Normalize();
        Assert.Equal(ExecutionOptions.Default.ExecutionOrder, exec.ExecutionOrder);
    }

    [Fact]
    public void RoleLevelDisableOverrides_DefaultNull_MeaningFollowPlatform()
    {
        // 角色级覆盖与旧状态等价：null = 跟随平台，不影响持久化 / 旧 import / restart。
        var def = new AgentDefinition { AgentId = "a", Nickname = "a", Description = "", Instructions = "" };
        Assert.Null(def.DisableBridge);
        Assert.Null(def.DisableRelay);
        Assert.Null(def.DisableOrgRoute);
    }
}
