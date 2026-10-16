using AguiGroupChat.Agents;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>
/// 落库前的「自检清单」与「连接归一」。
///
/// 回归背景（真实场景）：一键编排 / 组织架构构建师建出的团队常出现两类退化——
/// ① 只给了「向上问题提升」链、没有「向下任务指派」，组织成了单向上报，主管没法往下派活；
/// ② 需求要文件却没人能产出（或交付岗被写成“等定稿才出文件”的门卫）。
/// 自检把这些问题逐条列清（而不是只显示第一条 deliveryWarning），让用户在确认落库前就能看到并修。
/// </summary>
public sealed class OrgPlanSelfCheckTests
{
    private static OrchestratedAgent Agent(string id, string[] skills, string[]? assign = null, string? esc = null)
        => new()
        {
            AgentId = id, Nickname = id, Description = "d", Instructions = "i",
            SkillIds = [.. skills], AssignmentIds = assign is null ? null : [.. assign], EscalationAgentId = esc,
        };

    private static bool Has(IReadOnlyList<AgentOrchestrator.OrgPlanCheck> checks, string code)
        => checks.Any(c => c.Code == code);

    private static string LevelOf(IReadOnlyList<AgentOrchestrator.OrgPlanCheck> checks, string code)
        => checks.First(c => c.Code == code).Level;

    // ---- 结构 / 连接 ----

    [Fact]
    public void FlatPlan_ReportsNoConnections()
    {
        var plan = new OrchestrationPlan { Title = "T" };
        plan.Agents.Add(Agent("a", ["s1"]));
        plan.Agents.Add(Agent("b", ["s1"]));

        var checks = AgentOrchestrator.SelfCheck(plan, "随便做个分析");

        Assert.True(Has(checks, "no_connections"));
        Assert.Equal("error", LevelOf(checks, "no_connections"));
        Assert.False(Has(checks, "ok"));
    }

    [Fact]
    public void EscalationOnly_ReportsNoAssignment()
    {
        // 只有“问题提升”、没有“任务指派”——正是用户反馈的退化形态
        var plan = new OrchestrationPlan { Title = "T" };
        plan.Agents.Add(Agent("mgr", ["s1"]));
        plan.Agents.Add(Agent("sub", ["s1"], esc: "mgr"));

        var checks = AgentOrchestrator.SelfCheck(plan, "随便做个分析");

        Assert.True(Has(checks, "no_assignment"));
        Assert.Equal("error", LevelOf(checks, "no_assignment"));
    }

    [Fact]
    public void AssignmentOnly_IsAWarningNotAnError()
    {
        var plan = new OrchestrationPlan { Title = "T" };
        plan.Agents.Add(Agent("mgr", ["s1"], assign: ["sub"]));
        plan.Agents.Add(Agent("sub", ["s1"]));

        var checks = AgentOrchestrator.SelfCheck(plan, "随便做个分析");

        Assert.True(Has(checks, "no_escalation"));
        Assert.Equal("warn", LevelOf(checks, "no_escalation"));
    }

    [Fact]
    public void PairedConnections_PassWithOkSummary()
    {
        var plan = new OrchestrationPlan { Title = "T" };
        plan.Agents.Add(Agent("mgr", ["s1"], assign: ["sub"]));
        plan.Agents.Add(Agent("sub", ["s1"], esc: "mgr"));

        var checks = AgentOrchestrator.SelfCheck(plan, "随便做个分析");

        Assert.True(Has(checks, "connections_ok"));
        Assert.True(Has(checks, "ok"));
        Assert.DoesNotContain(checks, c => c.Level == "error");
    }

    [Fact]
    public void DanglingLink_IsError()
    {
        var plan = new OrchestrationPlan { Title = "T" };
        plan.Agents.Add(Agent("mgr", ["s1"], assign: ["nobody"]));
        plan.Agents.Add(Agent("sub", ["s1"], esc: "mgr"));

        var checks = AgentOrchestrator.SelfCheck(plan, "随便做个分析");

        Assert.True(Has(checks, "dangling_link"));
        Assert.Equal("error", LevelOf(checks, "dangling_link"));
    }

    [Fact]
    public void SelfEscalation_IsWarned()
    {
        var plan = new OrchestrationPlan { Title = "T" };
        plan.Agents.Add(Agent("mgr", ["s1"], assign: ["sub"]));
        plan.Agents.Add(Agent("sub", ["s1"], esc: "sub"));

        var checks = AgentOrchestrator.SelfCheck(plan, "随便做个分析");

        Assert.True(Has(checks, "self_escalation"));
    }

    // ---- 技能 / 交付闭环 ----

    [Fact]
    public void AgentWithoutSkill_IsWarned()
    {
        var plan = new OrchestrationPlan { Title = "T" };
        plan.Agents.Add(Agent("mgr", ["s1"], assign: ["sub"]));
        plan.Agents.Add(Agent("sub", [], esc: "mgr"));

        var checks = AgentOrchestrator.SelfCheck(plan, "随便做个分析");

        Assert.True(Has(checks, "agent_without_skill"));
        Assert.Equal("warn", LevelOf(checks, "agent_without_skill"));
    }

    [Fact]
    public void DeliveryGap_IsListedAsErrorAlongsideStructure()
    {
        // 要 Word 却没人能产出：应作为一条 error 列出，而不是只截取第一条告警
        var plan = new OrchestrationPlan { Title = "T" };
        plan.Agents.Add(Agent("writer", ["longform_copy"]));

        var checks = AgentOrchestrator.SelfCheck(plan, "做个文案团队，我要最终形成 word 文档");

        Assert.True(Has(checks, "delivery_gap"));
        Assert.Equal("error", LevelOf(checks, "delivery_gap"));
        Assert.True(Has(checks, "no_connections")); // 结构问题同时列出（不止一条）
    }

    [Fact]
    public void NoAgents_ShortCircuits()
    {
        var checks = AgentOrchestrator.SelfCheck(new OrchestrationPlan { Title = "T" }, "随便");
        Assert.Single(checks);
        Assert.Equal("no_agents", checks[0].Code);
    }

    // ---- 连接归一（Parse 阶段）----

    // 组一个最小可解析的方案 JSON：{"title":"T", <agentsFragment>}（片段已自带末尾 "}"）
    private static string Json(string agentsFragment) => "{\"title\":\"T\"," + agentsFragment;

    [Fact]
    public void Parse_FillsAssignmentFromEscalation()
    {
        // 只有“提升”链：归一后上级应有“任务指派”
        var text = Json("\"agents\":[" +
            "{\"agentId\":\"mgr\",\"nickname\":\"主管\",\"skillIds\":[\"s1\"],\"assignmentIds\":[],\"escalationAgentId\":null}," +
            "{\"agentId\":\"sub\",\"nickname\":\"执行\",\"skillIds\":[\"s1\"],\"assignmentIds\":[],\"escalationAgentId\":\"mgr\"}]}");

        var plan = AgentOrchestrator.Parse(text);

        var mgr = plan.Agents.Single(a => a.AgentId == "mgr");
        Assert.NotNull(mgr.AssignmentIds);
        Assert.Contains("sub", mgr.AssignmentIds!);
    }

    [Fact]
    public void Parse_FillsEscalationFromAssignment()
    {
        // 反向：只有“指派”链（无提升目标）→ 归一后下级应获得提升目标
        var text = Json("\"agents\":[" +
            "{\"agentId\":\"mgr\",\"nickname\":\"主管\",\"skillIds\":[\"s1\"],\"assignmentIds\":[\"sub\"],\"escalationAgentId\":null}," +
            "{\"agentId\":\"sub\",\"nickname\":\"执行\",\"skillIds\":[\"s1\"],\"assignmentIds\":[],\"escalationAgentId\":null}]}");

        var plan = AgentOrchestrator.Parse(text);

        var sub = plan.Agents.Single(a => a.AgentId == "sub");
        Assert.Equal("mgr", sub.EscalationAgentId);
    }

    [Fact]
    public void Parse_KeepsExplicitEscalation()
    {
        var text = Json("\"agents\":[" +
            "{\"agentId\":\"mgr\",\"nickname\":\"主管\",\"skillIds\":[\"s1\"],\"assignmentIds\":[\"sub\"],\"escalationAgentId\":null}," +
            "{\"agentId\":\"boss\",\"nickname\":\"总监\",\"skillIds\":[\"s1\"],\"assignmentIds\":[\"sub\"],\"escalationAgentId\":null}," +
            "{\"agentId\":\"sub\",\"nickname\":\"执行\",\"skillIds\":[\"s1\"],\"assignmentIds\":[],\"escalationAgentId\":\"boss\"}]}");

        var plan = AgentOrchestrator.Parse(text);

        var sub = plan.Agents.Single(a => a.AgentId == "sub");
        Assert.Equal("boss", sub.EscalationAgentId); // 显式提升目标不被覆盖
    }

    [Fact]
    public void Parse_DoesNotInventEscalationForSharedSubordinate()
    {
        // 被两个上级指派的下级：上级不唯一，不猜测提升目标
        var text = Json("\"agents\":[" +
            "{\"agentId\":\"m1\",\"nickname\":\"甲\",\"skillIds\":[\"s1\"],\"assignmentIds\":[\"sub\"],\"escalationAgentId\":null}," +
            "{\"agentId\":\"m2\",\"nickname\":\"乙\",\"skillIds\":[\"s1\"],\"assignmentIds\":[\"sub\"],\"escalationAgentId\":null}," +
            "{\"agentId\":\"sub\",\"nickname\":\"执行\",\"skillIds\":[\"s1\"],\"assignmentIds\":[],\"escalationAgentId\":null}]}");

        var plan = AgentOrchestrator.Parse(text);

        var sub = plan.Agents.Single(a => a.AgentId == "sub");
        Assert.Null(sub.EscalationAgentId);
    }

    [Fact]
    public void Parse_NormalizedPlanPassesSelfCheck()
    {
        var text = Json("\"agents\":[" +
            "{\"agentId\":\"mgr\",\"nickname\":\"主管\",\"skillIds\":[\"s1\"],\"assignmentIds\":[],\"escalationAgentId\":null}," +
            "{\"agentId\":\"sub\",\"nickname\":\"执行\",\"skillIds\":[\"s1\"],\"assignmentIds\":[],\"escalationAgentId\":\"mgr\"}]}");

        var plan = AgentOrchestrator.Parse(text);
        var checks = AgentOrchestrator.SelfCheck(plan, "随便做个分析");

        Assert.DoesNotContain(checks, c => c.Level == "error");
    }
}
