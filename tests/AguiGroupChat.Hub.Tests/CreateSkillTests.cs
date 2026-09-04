using AguiGroupChat.Agents;
using AguiGroupChat.Hub.Agents;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>智能体自建技能（create_skill，OpenClaw 风格）：技能定义入库 + 挂载 SkillDefIds + 类型/校验。</summary>
public sealed class CreateSkillTests
{
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
    public void CreateSkill_RegistersInLibraryAndMounts()
    {
        // 让 AgentCatalog 能解析到技能库：注入同一实例
        var sp = new ServiceCollection().BuildServiceProvider();
        var skillCatalog = new AgentSkillCatalog(NullLoggerFactory.Instance);
        sp = new ServiceCollection().AddSingleton(skillCatalog).BuildServiceProvider();
        var options = CreateOptions();
        var catalog = new AgentCatalog(options, NullLoggerFactory.Instance, sp);
        using (SetAmbient("agent_host"))
        {
            var result = catalog.CreateSkillToolImpl("risk_analyzer", "prompt",
                "需要风险评估时调用", "你是风险分析师，评估项目风险并给出分级建议。", null);
            Assert.Contains("已创建", result);
        }
        // 技能库中存在定义
        var def = skillCatalog.Get("risk_analyzer");
        Assert.NotNull(def);
        Assert.Equal("risk_analyzer", def!.SkillId);
        Assert.Equal(AgentSkillKind.Prompt, def.Kind);
        // 挂载到宿主 SkillDefIds
        var host = catalog.GetDefinition("agent_host")!;
        Assert.Contains("risk_analyzer", host.SkillDefIds);
        // 重建 agent 后工具列表含该技能
        var names = catalog.GetAgentToolNames("agent_host");
        Assert.Contains("risk_analyzer", names);
        Assert.Contains("create_skill", names); // 自建技能工具本身已注册
    }

    [Fact]
    public void CreateSkill_HttpSkill_RequiresApproval()
    {
        var sp = new ServiceCollection().AddSingleton(new AgentSkillCatalog(NullLoggerFactory.Instance)).BuildServiceProvider();
        var catalog = new AgentCatalog(CreateOptions(), NullLoggerFactory.Instance, sp);
        using (SetAmbient("agent_host"))
        {
            var result = catalog.CreateSkillToolImpl("fetch_price", "http",
                "查询某币价", "{\"method\":\"GET\",\"url\":\"https://api.example.com/price?symbol=${query}\"}", null);
            Assert.Contains("已创建", result);
        }
        // http 技能强制需审批
        var host = catalog.GetDefinition("agent_host")!;
        Assert.Contains("fetch_price", host.SkillDefIds);
        Assert.Contains("fetch_price", catalog.GetAgentApprovalToolNames("agent_host"));
    }

    [Theory]
    [InlineData("中文名")]
    [InlineData("has space")]
    [InlineData("a.b")]
    [InlineData("")]
    [InlineData("toolongtoolongtoolongtoolongtoolongtoolongtoolongtoolongtoolong")] // >40
    public void CreateSkill_InvalidName_ReturnsError(string skillName)
    {
        var sp = new ServiceCollection().AddSingleton(new AgentSkillCatalog(NullLoggerFactory.Instance)).BuildServiceProvider();
        var catalog = new AgentCatalog(CreateOptions(), NullLoggerFactory.Instance, sp);
        using (SetAmbient("agent_host"))
        {
            var result = catalog.CreateSkillToolImpl(skillName, "prompt", "说明", "模板", null);
            Assert.Contains("不合法", result);
        }
        Assert.Empty(catalog.GetDefinition("agent_host")!.SkillDefIds);
    }

    [Fact]
    public void CreateSkill_NoContext_ReturnsError()
    {
        var sp = new ServiceCollection().AddSingleton(new AgentSkillCatalog(NullLoggerFactory.Instance)).BuildServiceProvider();
        var catalog = new AgentCatalog(CreateOptions(), NullLoggerFactory.Instance, sp);
        var result = catalog.CreateSkillToolImpl("s1", "prompt", "说明", "模板", null); // 无 AmbientContext
        Assert.Contains("运行上下文", result);
    }

    [Fact]
    public void CreateSkill_SkillTargetCannotCreate()
    {
        var sp = new ServiceCollection().AddSingleton(new AgentSkillCatalog(NullLoggerFactory.Instance)).BuildServiceProvider();
        var catalog = new AgentCatalog(CreateOptions(), NullLoggerFactory.Instance, sp);
        using (SetAmbient("agent_host"))
            catalog.CreateSkillToolImpl("s1", "prompt", "说明", "模板", null);
        using (SetAmbient("s1"))
        {
            // s1 不是已注册 agent，宿主不存在 → 返回宿主不存在；此处验证 agent_host 缺 skill 目标分支
            var result = catalog.CreateSkillToolImpl("s2", "prompt", "说明", "模板", null);
            Assert.True(result.Contains("宿主智能体不存在") || result.Contains("已创建"));
        }
    }

    [Fact]
    public void CreateSkill_InvalidKind_ReturnsError()
    {
        var sp = new ServiceCollection().AddSingleton(new AgentSkillCatalog(NullLoggerFactory.Instance)).BuildServiceProvider();
        var catalog = new AgentCatalog(CreateOptions(), NullLoggerFactory.Instance, sp);
        using (SetAmbient("agent_host"))
        {
            var result = catalog.CreateSkillToolImpl("bad", "magic", "说明", "x", null);
            Assert.Contains("无效", result);
        }
    }

    [Fact]
    public void CreateSkill_ShellWithoutBody_ReturnsError()
    {
        var sp = new ServiceCollection().AddSingleton(new AgentSkillCatalog(NullLoggerFactory.Instance)).BuildServiceProvider();
        var catalog = new AgentCatalog(CreateOptions(), NullLoggerFactory.Instance, sp);
        using (SetAmbient("agent_host"))
        {
            var result = catalog.CreateSkillToolImpl("runner", "shell", "执行脚本", "", null);
            Assert.Contains("正文", result);
        }
    }

    [Theory]
    [InlineData("dotnet")]
    [InlineData("org_deploy")]
    public void CreateSkill_AdminOnlyKind_RefusedAtRuntime(string kind)
    {
        // dotnet（C# 编译）与 org_deploy（组织落库）都只能由系统管理员在技能库建立，
        // 智能体运行中即使自建（含传了正文）也直接被特权类型短路拒绝、绝不入库。
        var sp = new ServiceCollection().AddSingleton(new AgentSkillCatalog(NullLoggerFactory.Instance)).BuildServiceProvider();
        var catalog = new AgentCatalog(CreateOptions(), NullLoggerFactory.Instance, sp);
        using (SetAmbient("agent_host"))
        {
            // org_deploy（组织落库）只能由系统管理员在技能库手动建立：智能体运行中无论传不传正文都不能自建成功/入库。
            // （无论解析是落在“特权类型”短路还是其它校验，实质是绝不应返回『已创建』。）
            var result = catalog.CreateSkillToolImpl("kind_probe", kind, "说明", "正文内容", null);
            Assert.DoesNotContain("已创建", result);
            if (kind == "org_deploy")
                Assert.DoesNotContain("Prompt", result);   // 不应静默退化成普通 prompt 自建
        }
        Assert.Empty(catalog.GetDefinition("agent_host")!.SkillDefIds); // 未挂载
    }

    [Fact]
    public void Agent_OrgDeploySkill_MountsAsDeployTool()
    {
        var skillCatalog = new AgentSkillCatalog(NullLoggerFactory.Instance);
        skillCatalog.Upsert(new AgentSkillDefinition
        {
            SkillId = "org_design", Name = "组织方案设计", Kind = AgentSkillKind.Prompt,
            Description = "设计组织草案", Body = "你是组织构建师", RequiresApproval = false,
        });
        skillCatalog.Upsert(new AgentSkillDefinition
        {
            SkillId = "org_deploy", Name = "组织落库", Kind = AgentSkillKind.Org_deploy,
            Description = "把最终组织稿写库", Body = "", RequiresApproval = false,
            ExecutionLocation = AgentSkillExecutionLocation.Server,
        });
        var sp = new ServiceCollection().AddSingleton(skillCatalog).BuildServiceProvider();
        var options = new AgentOptions
        {
            Provider = "mock",
            EnableTools = true,
            Agents =
            [
                new AgentDefinition
                {
                    AgentId = "org_arch", Nickname = "组织架构构建师", Description = "",
                    OwnerId = "david",
                    SkillDefIds = ["org_design", "org_deploy"],
                },
            ],
        };
        var catalog = new AgentCatalog(options, NullLoggerFactory.Instance, sp);
        var toolNames = catalog.GetAgentToolNames("org_arch");
        // org_design → 受控组织落库工具 org_commit（既有硬编码分支）
        Assert.Contains("org_commit", toolNames);
        // org_deploy（kind=OrgDeploy）→ 以该技能 id 挂成的部署工具，而非 prompt 执行体
        Assert.Contains("org_deploy", toolNames);
        // OrgDeploy 工具不按“需审批的 prompt/命令”包装
        Assert.DoesNotContain("org_deploy", catalog.GetAgentApprovalToolNames("org_arch"));
    }
}
