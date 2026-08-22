using AguiGroupChat.Agents;
using AguiGroupChat.Hub.Agents;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>智能体自我生成技能（create_skill + HITL 审批）：技能目标智能体创建 / 挂载 / 隐藏 / 上限 / 校验。</summary>
public sealed class CreateSkillTests
{
    private static AgentCatalog CreateCatalog(AgentOptions options)
        => new(options, NullLoggerFactory.Instance, new ServiceCollection().BuildServiceProvider());

    private static AgentOptions CreateOptions()
        => new()
        {
            Provider = "mock",
            EnableTools = true,
            Agents =
            [
                new AgentDefinition
                {
                    AgentId = "agent_host", Nickname = "主助手", Description = "", Instructions = "你是主助手",
                },
            ],
        };

    private static IDisposable SetAmbient(string agentId, string triggerUserId = "user_1")
    {
        var ctx = new AgentInvocationContext("g1", "t1", agentId, "主助手", "msg1", triggerUserId, "hi", [], false);
        var prev = AgentGateway.AmbientContext.Value;
        AgentGateway.AmbientContext.Value = ctx;
        return new DelegateDisposable(() => AgentGateway.AmbientContext.Value = prev);
    }

    private sealed class DelegateDisposable(Action dispose) : IDisposable
    {
        private readonly Action _dispose = dispose;
        public void Dispose() => _dispose();
    }

    [Fact]
    public void CreateSkill_RegistersTargetAndMounts()
    {
        var options = CreateOptions();
        var catalog = CreateCatalog(options);
        using (SetAmbient("agent_host"))
        {
            var result = catalog.CreateSkillTool("risk_analyzer", "你是风险分析师，评估项目风险并给出分级建议。", "需要风险评估时调用");
            Assert.Contains("已创建", result);
        }
        // 技能目标智能体已创建（IsSkillTarget）
        var target = catalog.GetDefinition("skill_risk_analyzer");
        Assert.NotNull(target);
        Assert.True(target!.IsSkillTarget);
        Assert.Contains("风险分析师", target.Instructions);
        // 挂载到宿主定义
        var host = catalog.GetDefinition("agent_host")!;
        var skill = Assert.Single(host.Skills!);
        Assert.Equal("risk_analyzer", skill.SkillId);
        Assert.Equal("skill_risk_analyzer", skill.TargetAgentId);
        // 重建 agent 后工具列表含新技能
        var names = catalog.GetAgentToolNames("agent_host");
        Assert.Contains("risk_analyzer", names);
        Assert.Contains("create_skill", names); // 自建技能工具本身已注册
    }

    [Fact]
    public void CreateSkill_SameName_UpdatesInstructions()
    {
        var options = CreateOptions();
        var catalog = CreateCatalog(options);
        using (SetAmbient("agent_host"))
        {
            catalog.CreateSkillTool("analyst", "v1 人设", "");
            var result = catalog.CreateSkillTool("analyst", "v2 人设（覆盖）", "新说明");
            Assert.Contains("已创建", result);
        }
        var target = catalog.GetDefinition("skill_analyst")!;
        Assert.Contains("v2", target.Instructions);
        var skill = Assert.Single(catalog.GetDefinition("agent_host")!.Skills!);
        Assert.Equal("新说明", skill.Description);
    }

    [Theory]
    [InlineData("中文名")]
    [InlineData("has space")]
    [InlineData("a.b")]
    [InlineData("")]
    [InlineData("toolongtoolongtoolongtoolongtoolongtoolongtoolongtoolongtoolong")] // >40
    public void CreateSkill_InvalidName_ReturnsError(string skillName)
    {
        var options = CreateOptions();
        var catalog = CreateCatalog(options);
        using (SetAmbient("agent_host"))
        {
            var result = catalog.CreateSkillTool(skillName, "人设", "说明");
            Assert.Contains("不合法", result);
        }
        Assert.Null(catalog.GetDefinition("agent_host")!.Skills);
    }

    [Fact]
    public void CreateSkill_NoContext_ReturnsError()
    {
        var catalog = CreateCatalog(CreateOptions());
        var result = catalog.CreateSkillTool("s1", "人设", "说明"); // 无 AmbientContext
        Assert.Contains("运行上下文", result);
    }

    [Fact]
    public void CreateSkill_SkillTargetCannotCreate()
    {
        var options = CreateOptions();
        var catalog = CreateCatalog(options);
        using (SetAmbient("agent_host"))
            catalog.CreateSkillTool("s1", "人设", "说明");
        using (SetAmbient("skill_s1"))
        {
            var result = catalog.CreateSkillTool("s2", "人设", "说明");
            Assert.Contains("不能再创建技能", result);
        }
    }

    [Fact]
    public void CreateSkill_ExceedsLimit_ReturnsError()
    {
        var options = CreateOptions();
        var catalog = CreateCatalog(options);
        using var ambient = SetAmbient("agent_host");
        for (var i = 0; i < 10; i++)
        {
            var r = catalog.CreateSkillTool($"skill_{i}", "人设", "说明");
            Assert.Contains("已创建", r);
        }
        var result = catalog.CreateSkillTool("overflow", "人设", "说明");
        Assert.Contains("上限", result);
        Assert.DoesNotContain("overflow", catalog.GetAgentToolNames("agent_host"));
    }

    [Fact]
    public void CreateSkill_EmptyInstructions_ReturnsError()
    {
        var catalog = CreateCatalog(CreateOptions());
        using (SetAmbient("agent_host"))
        {
            var result = catalog.CreateSkillTool("s1", "   ", "说明");
            Assert.Contains("人设", result);
        }
    }
}
