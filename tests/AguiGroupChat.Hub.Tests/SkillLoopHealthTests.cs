using AguiGroupChat.Agents;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>
/// 技能修复健康度（1.0.361，纯函数）：把闭环事件汇总成编排可用的「修得好不好」判断。
/// </summary>
public sealed class SkillLoopHealthTests
{
    private const long Day = 24 * 60 * 60 * 1000;
    private const long Now = 1_700_000_000_000;

    private static SkillLoopEvent Evt(string skillId, string outcome, long atMs)
        => new("lp_x", skillId, outcome, 1, 100, 0, false, atMs);

    [Fact]
    public void TwoRollbacks_MarkAvoid()
    {
        var map = SkillLoopHealthBoard.Build(
        [
            Evt("sk", SkillLoopOutcomes.RolledBack, Now - 2 * Day),
            Evt("sk", SkillLoopOutcomes.RolledBack, Now - Day),
        ], Now);

        var h = map["sk"];
        Assert.Equal(2, h.Attempts);
        Assert.Equal(0, h.Successes);
        Assert.Equal(2, h.RolledBack);
        Assert.True(h.IsAvoid);
        Assert.False(h.IsWatch);
        Assert.True(h.IsRisky);
        Assert.Contains("回滚 2 次", h.Describe());
    }

    [Fact]
    public void SingleFailure_IsWatchNotAvoid()
    {
        var map = SkillLoopHealthBoard.Build(
        [
            Evt("sk", SkillLoopOutcomes.Failed, Now - Day),
            Evt("sk", SkillLoopOutcomes.Applied, Now - 2 * Day),
        ], Now);

        var h = map["sk"];
        Assert.False(h.IsAvoid);
        Assert.True(h.IsWatch);
        Assert.Equal(0.5, h.SuccessRate);
    }

    [Fact]
    public void HealthySkill_IsNotRisky()
    {
        var map = SkillLoopHealthBoard.Build(
        [
            Evt("sk", SkillLoopOutcomes.Applied, Now - Day),
            Evt("sk", SkillLoopOutcomes.Resumed, Now - 2 * Day),
        ], Now);

        var h = map["sk"];
        Assert.False(h.IsRisky);
        Assert.Equal(1, h.SuccessRate);
        Assert.Equal(2, h.Successes);
    }

    [Fact]
    public void SkippedEvents_AreExcluded()
    {
        var map = SkillLoopHealthBoard.Build(
        [
            Evt("sk", SkillLoopOutcomes.Skipped, Now - Day),
            Evt("sk", SkillLoopOutcomes.Applied, Now - 2 * Day),
        ], Now);

        var h = map["sk"];
        Assert.Equal(1, h.Attempts);
        Assert.False(h.IsRisky);
    }

    [Fact]
    public void EventsOutsideWindow_AreIgnored()
    {
        var map = SkillLoopHealthBoard.Build(
        [
            Evt("sk", SkillLoopOutcomes.RolledBack, Now - 40 * Day),
            Evt("sk", SkillLoopOutcomes.RolledBack, Now - 41 * Day),
        ], Now);

        Assert.False(map.ContainsKey("sk"));
    }

    [Fact]
    public void EmptySkillId_IsIgnoredAndBlankInputIsEmpty()
    {
        Assert.Empty(SkillLoopHealthBoard.Build(null, Now));
        Assert.Empty(SkillLoopHealthBoard.Build([Evt("", SkillLoopOutcomes.Failed, Now - Day)], Now));
    }

    [Fact]
    public void Build_SeparatesSkillsAndIgnoresFutureEvents()
    {
        var map = SkillLoopHealthBoard.Build(
        [
            Evt("a", SkillLoopOutcomes.Failed, Now - Day),
            Evt("b", SkillLoopOutcomes.Applied, Now - Day),
            Evt("b", SkillLoopOutcomes.Failed, Now + Day),
        ], Now);

        Assert.Equal(2, map.Count);
        Assert.True(map["a"].IsWatch);
        Assert.Equal(1, map["b"].Attempts); // 未来事件不计入
    }

    [Fact]
    public void SelfCheck_WarnsAboutAvoidAndWatchSkills()
    {
        var plan = new OrchestrationPlan
        {
            Agents = [new OrchestratedAgent { AgentId = "writer", Nickname = "写手", SkillIds = ["flaky", "wonky"] }],
            Skills = [],
        };
        var health = new Dictionary<string, SkillLoopHealth>(StringComparer.Ordinal)
        {
            ["flaky"] = new("flaky", 2, 0, 2, 0, 0),
            ["wonky"] = new("wonky", 2, 1, 0, 1, 0.5),
        };

        var checks = AgentOrchestrator.SelfCheck(plan, "写报告", null, null, health);
        var hits = checks.Where(c => c.Code == "skill_loop_risk").ToList();

        Assert.Equal(2, hits.Count);
        Assert.All(hits, c => Assert.Equal("warn", c.Level));
        Assert.Contains(hits, c => c.Message.Contains("反复修复后仍失败或回滚") && c.Message.Contains("flaky"));
        Assert.Contains(hits, c => c.Message.Contains("修复不够稳") && c.Message.Contains("wonky"));
    }

    [Fact]
    public void SelfCheck_WithoutLoopHealth_NoLoopCheck()
    {
        var plan = new OrchestrationPlan
        {
            Agents = [new OrchestratedAgent { AgentId = "writer", Nickname = "写手", SkillIds = ["flaky"] }],
            Skills = [],
        };

        var checks = AgentOrchestrator.SelfCheck(plan, "写报告");

        Assert.DoesNotContain(checks, c => c.Code == "skill_loop_risk");
    }
}
