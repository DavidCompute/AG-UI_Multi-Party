using AguiGroupChat.Agents;
using AguiGroupChat.Hub.Agents;
using AguiGroupChat.Hub.Models;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>
/// 「交付物兜底」：用户明确要文件、而指派/提升链只回了文本时，找挂了对应技能的同事真实生成一次。
///
/// 回归背景（真实场景）：用户带附件说「我需要 word 文档」，编排出的团队走了
/// 排版员 → 总监 → 组长 → 写手 的指派/提升链，最后写手只把稿子**当文本贴出来**，
/// 没人调 docx_* 技能 → 用户拿不到文件。这是「任务被派来派去却没人负责最终交付物」的典型断点。
/// </summary>
public sealed class DeliveryFallbackTests
{
    /// <summary>反射调用私有的 WantedDeliverable（静态、无副作用），断言识别口径。</summary>
    private static (string SkillPrefix, string Label)? Wanted(string text)
    {
        var mi = typeof(AgentGateway).GetMethod("WantedDeliverable",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(mi);
        return ((string, string)?)mi!.Invoke(null, [text]);
    }

    [Theory]
    [InlineData("根据附件帮我写“知聚”市场推广文案，我需要word文档")]
    [InlineData("帮我出一份 Word 文档")]
    [InlineData("导成 docx 给我")]
    [InlineData("生成一个文档")]
    public void DetectsWordDeliveryRequest(string text)
    {
        var want = Wanted(text);
        Assert.NotNull(want);
        Assert.Equal("docx_", want!.Value.SkillPrefix);
    }

    [Theory]
    [InlineData("给我一份 Excel 表格")]
    [InlineData("导出 xlsx")]
    public void DetectsExcelDeliveryRequest(string text)
    {
        var want = Wanted(text);
        Assert.NotNull(want);
        Assert.Equal("xlsx_", want!.Value.SkillPrefix);
    }

    [Theory]
    [InlineData("帮我看看这段代码有没有问题")]
    [InlineData("总结一下今天的会议")]
    [InlineData("")]
    [InlineData(null)]
    public void IgnoresNonDeliveryRequests(string? text)
    {
        // 没要文件 → 不做兜底（避免无谓地多跑一次技能）
        Assert.Null(Wanted(text));
    }

    [Fact]
    public void FindsLeafExecutorOverManager()
    {
        // 组织里既有主管（也挂 docx）也有专职执行岗时，应选执行岗 ——
        // 主管的职责是统筹，交付文件交给它容易又被派下去。
        var gateway = NewGateway(out var skillCatalog, out var agentCatalog);
        skillCatalog.Upsert(Skill("docx_report"));

        agentCatalog.Upsert(Agent("boss", "总监", skills: ["docx_report"], assignment: ["typesetter"]));
        agentCatalog.Upsert(Agent("typesetter", "排版员", skills: ["docx_report"], assignment: []));

        var owner = FindOwner(gateway, agentCatalog, "boss", "docx_");
        Assert.NotNull(owner);
        Assert.Equal("typesetter", owner!.AgentId);
    }

    [Fact]
    public void FindsExecutorThroughEscalationChain()
    {
        // 执行岗常不直属本岗，而在提升链上（本岗 → 上级；上级再派给执行岗）
        var gateway = NewGateway(out var skillCatalog, out var agentCatalog);
        skillCatalog.Upsert(Skill("docx_report"));

        // writer 向上提升到 director；director 下派给 typesetter（执行岗）
        agentCatalog.Upsert(Agent("writer", "写手", skills: ["longform_copy"], assignment: [], escalation: "director"));
        agentCatalog.Upsert(Agent("director", "总监", skills: [], assignment: ["typesetter"]));
        agentCatalog.Upsert(Agent("typesetter", "排版员", skills: ["docx_report"], assignment: []));

        var owner = FindOwner(gateway, agentCatalog, "writer", "docx_");
        Assert.NotNull(owner);
        Assert.Equal("typesetter", owner!.AgentId);
    }

    [Fact]
    public void ReturnsNullWhenNobodyHasTheSkill()
    {
        var gateway = NewGateway(out var skillCatalog, out var agentCatalog);
        skillCatalog.Upsert(Skill("longform_copy"));
        agentCatalog.Upsert(Agent("writer", "写手", skills: ["longform_copy"], assignment: []));

        Assert.Null(FindOwner(gateway, agentCatalog, "writer", "docx_"));
    }

    // ===== 测试脚手架 =====

    private static AgentSkillDefinition Skill(string id) => new()
    {
        SkillId = id, Name = id, Kind = AgentSkillKind.Dotnet, Body = "x",
        ExecutionLocation = AgentSkillExecutionLocation.Server,
    };

    private static AgentDefinition Agent(string id, string nick, string[] skills, string[] assignment, string? escalation = null) => new()
    {
        AgentId = id, Nickname = nick, Description = nick + "的职责",
        Instructions = "i", SkillDefIds = [.. skills],
        AssignmentIds = [.. assignment], EscalationAgentId = escalation,
    };

    private static AgentGateway NewGateway(out AgentSkillCatalog skillCatalog, out AgentCatalog agentCatalog)
    {
        var options = new AgentOptions { Provider = "mock" };
        using var lf = Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance;

        skillCatalog = new AgentSkillCatalog(lf, options);
        // 网关从 DI 解析技能库（_skillCatalog 为 Lazy），因此必须注册进容器，否则组织查找拿不到技能。
        var services = new ServiceCollection()
            .AddSingleton(skillCatalog)
            .BuildServiceProvider();
        agentCatalog = new AgentCatalog(options, lf, services);

        var gateway = new AgentGateway(agentCatalog, services, options, attachmentStore: null,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AgentGateway>.Instance);
        return gateway;
    }

    private static AgentDefinition? FindOwner(AgentGateway gateway, AgentCatalog catalog, string rootId, string prefix)
    {
        // 反射调用私有 FindDeliverableOwner（需要 gateway 自身的 _skillCatalog，由 DI 解析）
        var mi = typeof(AgentGateway).GetMethod("FindDeliverableOwner",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(mi);
        var ctx = new AgentInvocationContext(
            GroupId: "g", ThreadId: "t", AgentId: rootId, AgentNickname: "n",
            TriggerMessageId: "m", TriggerUserId: "u", Content: "c", Mentions: [], MentionAll: false);
        return (AgentDefinition?)mi!.Invoke(gateway, [ctx, prefix]);
    }
}
