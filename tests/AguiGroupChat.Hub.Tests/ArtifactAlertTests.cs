using AguiGroupChat.Agents;
using AguiGroupChat.Hub.Agents;
using AguiGroupChat.Hub.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>
/// 低质量技能告警与自动隔离（1.0.345）：
/// 连续低分判定口径、告警存储、技能暂停状态往返，以及暂停后
/// 「不挂载 / 不执行」的运行时行为。
/// </summary>
public sealed class ArtifactAlertTests
{
    private static ArtifactQualityEvent Evt(long atMs, int score, bool satisfied = true, params string[] missing)
        => new("agent_a", "skill_x", "Docx", score, satisfied, missing, "g1", atMs);

    // ---------- 判定口径 ----------

    [Fact]
    public void TwoLowRunsInWindowOfThree_Alerts()
    {
        var history = new[] { Evt(1, 90), Evt(2, 40), Evt(3, 55) };
        var d = ArtifactAlertPolicy.Evaluate(history, 70, 3, 2);
        Assert.True(d.ShouldAlert);
        Assert.Equal(3, d.Runs);
        Assert.Equal(2, d.LowRuns);
        Assert.Equal(61.7, d.AvgScore);
    }

    [Fact]
    public void SingleLowRun_DoesNotAlert()
    {
        var history = new[] { Evt(1, 90), Evt(2, 95), Evt(3, 40) };
        var d = ArtifactAlertPolicy.Evaluate(history, 70, 3, 2);
        Assert.False(d.ShouldAlert);
        Assert.Equal(1, d.LowRuns);
    }

    [Fact]
    public void OnlyLatestWindowCounts_OlderLowsDoNotAlert()
    {
        // 更早的两次低分已滑出窗口：最近 3 次都是高分 → 不告警
        var history = new[] { Evt(1, 10), Evt(2, 20), Evt(3, 95), Evt(4, 96), Evt(5, 97) };
        var d = ArtifactAlertPolicy.Evaluate(history, 70, 3, 2);
        Assert.False(d.ShouldAlert);
        Assert.Equal(3, d.Runs);
        Assert.Equal(0, d.LowRuns);
    }

    [Fact]
    public void UnsatisfiedArtifact_CountsAsLow_EvenWithHighScore()
    {
        var history = new[] { Evt(1, 95, satisfied: false, "缺少正文"), Evt(2, 95, satisfied: false, "缺少正文") };
        var d = ArtifactAlertPolicy.Evaluate(history, 70, 3, 2);
        Assert.True(d.ShouldAlert);
        Assert.Equal(2, d.LowRuns);
        Assert.Contains("缺少正文", d.Missing);
    }

    [Fact]
    public void EmptyHistory_NeverAlerts()
    {
        var d = ArtifactAlertPolicy.Evaluate([], 70, 3, 2);
        Assert.False(d.ShouldAlert);
        Assert.Equal(0, d.Runs);
    }

    [Fact]
    public void LowRunsToAlert_IsClampedIntoWindow()
    {
        // lowRunsToAlert 大于窗口时收敛到窗口数，不会永远不告警
        var history = new[] { Evt(1, 10), Evt(2, 10), Evt(3, 10) };
        var d = ArtifactAlertPolicy.Evaluate(history, 70, 3, 99);
        Assert.True(d.ShouldAlert);
        Assert.Equal(3, d.LowRuns);
    }

    // ---------- 告警存储 ----------

    [Fact]
    public void AlertStore_FiltersBySince_AndKeepsNewest()
    {
        var store = new ArtifactAlertStore();
        store.Add(new ArtifactQualityAlert("s1", "技能1", "a1", "岗位1", "docx", 3, 2, 40, 70, [], false, "g1", 100));
        store.Add(new ArtifactQualityAlert("s2", "技能2", "a1", "岗位1", "docx", 3, 3, 10, 70, [], true, "g1", 200));
        Assert.Equal(2, store.Snapshot().Count);
        var recent = store.Snapshot(150);
        Assert.Single(recent);
        Assert.Equal("s2", recent[0].SkillId);
    }

    [Fact]
    public void AlertStore_IsBounded()
    {
        var store = new ArtifactAlertStore();
        for (var i = 0; i < ArtifactAlertStore.MaxAlerts + 25; i++)
            store.Add(new ArtifactQualityAlert("s" + i, "技能", "a1", "岗位1", "docx", 3, 2, 10, 70, [], false, "", i));
        Assert.Equal(ArtifactAlertStore.MaxAlerts, store.Snapshot().Count);
        Assert.DoesNotContain(store.Snapshot(), a => a.SkillId == "s0");
    }

    // ---------- 暂停状态往返 ----------

    [Fact]
    public void SuspendAndResume_RoundTrip()
    {
        var skills = new AgentSkillCatalog(NullLoggerFactory.Instance, new AgentOptions());
        skills.Upsert(new AgentSkillDefinition { SkillId = "skill_x", Name = "技能X", Kind = AgentSkillKind.Prompt });

        Assert.True(skills.Suspend("skill_x", "连续低分", 1234));
        var suspended = skills.Get("skill_x")!;
        Assert.True(suspended.Suspended);
        Assert.Equal("连续低分", suspended.SuspendedReason);
        Assert.Equal(1234, suspended.SuspendedAtMs);

        Assert.True(skills.Resume("skill_x"));
        var resumed = skills.Get("skill_x")!;
        Assert.False(resumed.Suspended);
        Assert.Null(resumed.SuspendedReason);
        Assert.Null(resumed.SuspendedAtMs);

        Assert.False(skills.Suspend("missing", "x", 1));
        Assert.False(skills.Resume("missing"));
    }

    // ---------- 运行时行为 ----------

    [Fact]
    public async Task SuspendedSkill_IsNotExecuted()
    {
        var catalog = new AgentCatalog(
            new AgentOptions { Provider = "mock" },
            NullLoggerFactory.Instance,
            new ServiceCollection().BuildServiceProvider());
        var skill = new AgentSkillDefinition
        {
            SkillId = "bad_skill",
            Name = "坏技能",
            Kind = AgentSkillKind.Shell,
            Body = "echo should-not-run",
            Suspended = true,
            SuspendedReason = "产物质量连续 2/3 次未达标（阈值 70）",
        };

        var output = await catalog.RunSkillAsync(skill, "");

        Assert.Contains("已暂停", output);
        Assert.Contains("坏技能", output);
        Assert.DoesNotContain("should-not-run", output);
    }

    [Fact]
    public void SuspendedSkill_IsNotMountedToAgent()
    {
        var (catalog, skills) = BuildMountedCatalog();
        skills.Upsert(new AgentSkillDefinition
        {
            SkillId = "low_skill",
            Name = "低质量技能",
            Description = "用于测试挂载",
            Kind = AgentSkillKind.Prompt,
            Body = "模板",
            RequiresApproval = false,
            Suspended = true,
        });

        Assert.DoesNotContain("low_skill", catalog.GetAgentToolNames("writer"));

        // 恢复后重新构建 → 工具回来
        skills.Resume("low_skill");
        catalog.InvalidateAgentsUsingSkill("low_skill");
        Assert.Contains("low_skill", catalog.GetAgentToolNames("writer"));
    }

    private static (AgentCatalog Catalog, AgentSkillCatalog Skills) BuildMountedCatalog()
    {
        var options = new AgentOptions { Provider = "mock", EnableTools = true };
        var skills = new AgentSkillCatalog(NullLoggerFactory.Instance, options);
        var services = new ServiceCollection()
            .AddSingleton(skills)
            .BuildServiceProvider();
        var catalog = new AgentCatalog(options, NullLoggerFactory.Instance, services);
        catalog.Upsert(new AgentDefinition
        {
            AgentId = "writer",
            Nickname = "写手",
            Description = "d",
            Instructions = "你是写手",
            TriggerMode = AgentTriggerMode.Mentioned,
            SkillDefIds = ["low_skill"],
        });
        return (catalog, skills);
    }
}
