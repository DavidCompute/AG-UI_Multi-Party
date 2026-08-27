using System.Reflection;
using AguiGroupChat.Agents;
using AguiGroupChat.Web;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>
/// 技能库请求 → 定义构造（SkillApi.BuildDef）的审批默认策略：
/// - Shell 技能永远强制需审批（任意本机命令执行面最大）；
/// - HTTP / 提示词技能按调用方 RequiresApproval 决定（HTTP 可关以自动调用，不再被强制 true）。
/// </summary>
public sealed class SkillApiApprovalTests
{
    private static (AgentSkillDefinition? Def, object? Err) BuildDef(
        string? skillId, string name, string kind, bool? requiresApproval)
    {
        var req = new SkillDefHttpRequest(
            SkillId: skillId, Name: name, Description: "description",
            Kind: kind, Body: "{\"method\":\"GET\",\"url\":\"http://127.0.0.1\"}",
            ParametersJson: null, Interpreter: null, HttpTimeoutSeconds: 30,
            RequiresApproval: requiresApproval);
        var m = typeof(SkillApi).GetMethod("BuildDef", BindingFlags.NonPublic | BindingFlags.Static)!;
        dynamic result = m.Invoke(null, new object?[] { req, "user_x", true })!;
        return (result.Item1, result.Item2);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [InlineData(null)]
    public void HttpSkill_Approval_HonorsRequiresApproval(bool? flag)
    {
        var (def, err) = BuildDef("http_skill", "HTTP", "http", flag);
        Assert.Null(err);
        Assert.NotNull(def);
        // HTTP 技能不再被强制 true：跟随调用方显式选择，null 时默认 true（安全兜底）
        Assert.Equal(flag ?? true, def!.RequiresApproval);
    }

    [Fact]
    public void ShellSkill_IsAlwaysForcedApproval()
    {
        // 即便调用方传 false，Shell 仍强制审批（安全红线不可关）
        var (def, err) = BuildDef("shell_skill", "Shell", "shell", requiresApproval: false);
        Assert.Null(err);
        Assert.NotNull(def);
        Assert.True(def!.RequiresApproval);
    }

    [Fact]
    public void PromptSkill_DefaultsToApproval_ButCanBeDisabled()
    {
        var (dOn, eOn) = BuildDef("p_on", "Prompt", "prompt", null);
        Assert.True(dOn!.RequiresApproval);

        var (dOff, eOff) = BuildDef("p_off", "Prompt", "prompt", false);
        Assert.False(dOff!.RequiresApproval);
    }
}
