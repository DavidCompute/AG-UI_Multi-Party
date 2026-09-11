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

    [Fact]
    public void ArchitectInstructions_DoNotAskModelToJudgeAdminIdentity()
    {
        // 实测踩到：构建师以“本群对话里我无法确认你的管理员身份，所以不会擅自写库”为由拒绝落库，
        // 整个建团流程断在这里。根因是指令让它去“预判身份”，而它在会话里无从得知。
        // 正确口径：直接调 org_commit，由工具强制校验权限、按工具返回如实转述。
        var text = OrgBuiltinSeeds.OrgArchitectInstructions;
        Assert.Contains("不要自己判断用户是不是管理员", text);
        Assert.Contains("权限由 org_commit 工具本身强制校验", text);
        // 不得再留“非管理员发落库就交稿不写库”这类让模型自行推断的旧口令
        Assert.DoesNotContain("非管理员发", text);
    }
}
