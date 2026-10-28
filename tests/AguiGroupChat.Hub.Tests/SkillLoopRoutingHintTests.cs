using AguiGroupChat.Agents;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>
/// 修复健康的运行时反哺（1.0.362，纯函数）：指派路由的候选参考行 + 工具说明里的自检提醒。
/// </summary>
public sealed class SkillLoopRoutingHintTests
{
    private static SkillLoopHealth Avoid(string id) => new(id, 2, 0, 2, 0, 0);
    private static SkillLoopHealth Watch(string id) => new(id, 2, 1, 0, 1, 0.5);
    private static SkillLoopHealth Healthy(string id) => new(id, 2, 2, 0, 0, 1);

    [Fact]
    public void AssignmentLines_ReportAvoidSkill()
    {
        var health = new Dictionary<string, SkillLoopHealth>(StringComparer.Ordinal) { ["flaky"] = Avoid("flaky") };

        var lines = SkillLoopRoutingHint.BuildAssignmentLines(
            health, ["a1"], _ => ["flaky"], id => "助理");

        var line = Assert.Single(lines);
        Assert.Contains("a1（助理）", line);
        Assert.Contains("flaky", line);
        Assert.Contains("修复后仍不稳定", line);
        Assert.Contains("回滚 2 次", line);
    }

    [Fact]
    public void AssignmentLines_ReportWatchSkill()
    {
        var health = new Dictionary<string, SkillLoopHealth>(StringComparer.Ordinal) { ["wonky"] = Watch("wonky") };

        var lines = SkillLoopRoutingHint.BuildAssignmentLines(health, ["a1"], _ => ["wonky"], id => id);

        Assert.Contains("修复不够稳", Assert.Single(lines));
    }

    [Fact]
    public void AssignmentLines_SilentForHealthyCandidates()
    {
        var health = new Dictionary<string, SkillLoopHealth>(StringComparer.Ordinal) { ["steady"] = Healthy("steady") };

        Assert.Empty(SkillLoopRoutingHint.BuildAssignmentLines(health, ["a1"], _ => ["steady"], id => id));
        Assert.Empty(SkillLoopRoutingHint.BuildAssignmentLines(null, ["a1"], _ => ["steady"], id => id));
        Assert.Empty(SkillLoopRoutingHint.BuildAssignmentLines(health, [], _ => ["steady"], id => id));
    }

    [Fact]
    public void AssignmentLines_PreferAvoidAndCountTheRest()
    {
        var health = new Dictionary<string, SkillLoopHealth>(StringComparer.Ordinal)
        {
            ["wonky"] = Watch("wonky"),
            ["flaky"] = Avoid("flaky"),
        };

        var line = Assert.Single(SkillLoopRoutingHint.BuildAssignmentLines(
            health, ["a1"], _ => ["wonky", "flaky"], id => id));

        Assert.Contains("flaky", line);
        Assert.Contains("另有 1 个技能修复不稳", line);
    }

    [Fact]
    public void AssignmentLines_IgnoreUnknownSkillIds()
    {
        var health = new Dictionary<string, SkillLoopHealth>(StringComparer.Ordinal) { ["flaky"] = Avoid("flaky") };

        Assert.Empty(SkillLoopRoutingHint.BuildAssignmentLines(health, ["a1"], _ => ["not_mounted"], id => id));
        Assert.Empty(SkillLoopRoutingHint.BuildAssignmentLines(health, ["a1"], _ => [], id => id));
    }

    [Fact]
    public void ToolWarning_AvoidRequiresSelfCheck()
    {
        var warn = SkillLoopRoutingHint.BuildToolWarning(Avoid("flaky"), "flaky");

        Assert.StartsWith(SkillLoopRoutingHint.WarningPrefix, warn);
        Assert.Contains("必须自检交付物", warn);
        Assert.Contains("不要", warn);
    }

    [Fact]
    public void ToolWarning_WatchAsksForSpotCheck()
    {
        var warn = SkillLoopRoutingHint.BuildToolWarning(Watch("wonky"), "wonky");

        Assert.StartsWith(SkillLoopRoutingHint.WarningPrefix, warn);
        Assert.Contains("抽查", warn);
        Assert.DoesNotContain("必须自检", warn);
    }

    [Fact]
    public void ToolWarning_EmptyForHealthyOrMissing()
    {
        Assert.Equal("", SkillLoopRoutingHint.BuildToolWarning(Healthy("steady"), "steady"));
        Assert.Equal("", SkillLoopRoutingHint.BuildToolWarning(null, "steady"));
        Assert.Equal("", SkillLoopRoutingHint.BuildToolWarning(Avoid("flaky"), ""));
    }
}
