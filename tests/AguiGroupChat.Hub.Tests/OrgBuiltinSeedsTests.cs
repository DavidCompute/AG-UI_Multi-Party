using AguiGroupChat.Agents;
using AguiGroupChat.Hub.Models;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>veterinary：内置组织工具（管理员“恢复”）的规范内容——org_architect 引用 org_design/org_deploy；技能恰当地定型。</summary>
public sealed class OrgBuiltinSeedsTests
{
    [Fact]
    public void DefaultAgent_MountsBothBuiltinSkills()
    {
        var ag = OrgBuiltinSeeds.BuildDefaultOrgArchitectAgent("owner_x");
        Assert.Equal("org_architect", ag.AgentId);
        Assert.Equal(AgentTriggerMode.Mentioned, ag.TriggerMode);
        Assert.Contains("org_design", ag.SkillDefIds);
        Assert.Contains("org_deploy", ag.SkillDefIds);
        Assert.Equal("owner_x", ag.OwnerId);
        Assert.False(ag.IsPrivate);
    }

    [Fact]
    public void DefaultOrgDesign_IsPromptServerWithBody()
    {
        var sk = OrgBuiltinSeeds.BuildDefaultOrgDesignSkill("owner_x");
        Assert.Equal(AgentSkillKind.Prompt, sk.Kind);
        Assert.Equal(AgentSkillExecutionLocation.Server, sk.ExecutionLocation);
        Assert.False(sk.RequiresApproval);
        Assert.False(string.IsNullOrWhiteSpace(sk.Body));
        Assert.Contains("{{query}}", sk.Body); // 保留占位符且被固化为规范正文
    }

    [Fact]
    public void DefaultOrgDeploy_IsControlledOrgDeployKind_WithSensibleDescription()
    {
        var sk = OrgBuiltinSeeds.BuildDefaultOrgDeploySkill("owner_x");
        Assert.Equal(AgentSkillKind.Org_deploy, sk.Kind);
        Assert.Equal("", sk.Body); // 受控动作，无可执行正文
        Assert.False(string.IsNullOrWhiteSpace(sk.Description));
        Assert.DoesNotContain("cc kind test", sk.Description); // 方案 B：不再把测试残留固化为默认文案
    }
}
