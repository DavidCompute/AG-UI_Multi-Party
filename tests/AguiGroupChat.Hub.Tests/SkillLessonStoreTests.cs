using AguiGroupChat.Agents;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>
/// 技能经验库（1.0.349）：去重上限、岗位 / 技能级召回、修复后解决旧失败经验、
/// 注入提示词块以及持久化往返。
/// </summary>
public sealed class SkillLessonStoreTests
{
    private static SkillLesson Lesson(
        string skillId, string? agentId, string kind, string summary, long atMs,
        string[]? missing = null, string? fix = null, bool resolved = false, string groupId = "g1")
        => new("sl_" + atMs + "_" + (agentId ?? "all"), skillId, agentId, kind, summary,
            missing ?? [], fix, groupId, atMs, resolved);

    [Fact]
    public void ForAgent_ReturnsGlobalAndAgentLessons_NewestFirst_SkipsResolved()
    {
        var store = new SkillLessonStore();
        store.Add(Lesson("docx", null, SkillLessonKinds.Failure, "技能级旧失败", 100));
        store.Add(Lesson("docx", "writer", SkillLessonKinds.Failure, "岗位失败", 200, ["缺少正文"]));
        store.Add(Lesson("docx", "other", SkillLessonKinds.Failure, "别的岗位失败", 300));
        store.Add(Lesson("docx", "writer", SkillLessonKinds.Failure, "已解决", 400, resolved: true));
        store.Add(Lesson("pptx", "writer", SkillLessonKinds.Failure, "别的技能", 500));

        var got = store.ForAgent("writer", "docx", max: 3);

        Assert.Equal(2, got.Count);
        Assert.Equal("岗位失败", got[0].Summary); // 最新在前
        Assert.Equal("技能级旧失败", got[1].Summary);
        Assert.DoesNotContain(got, l => l.Summary == "别的岗位失败" || l.Summary == "别的技能");
        Assert.DoesNotContain(got, l => l.Resolved);
    }

    [Fact]
    public void Add_KeepsOnlyNewestPerSameKey()
    {
        var store = new SkillLessonStore();
        for (var i = 1; i <= 5; i++)
            store.Add(Lesson("docx", "writer", SkillLessonKinds.Failure, "第" + i + "次", i));

        var got = store.ForAgent("writer", "docx", max: 10);
        Assert.Equal(SkillLessonStore.MaxPerSkillAgent, got.Count);
        Assert.Equal("第5次", got[0].Summary);
        Assert.Equal("第3次", got[^1].Summary);
    }

    [Fact]
    public void ResolveFailures_MarksFailuresButKeepsRepairLessons()
    {
        var store = new SkillLessonStore();
        store.Add(Lesson("docx", "writer", SkillLessonKinds.Failure, "失败", 100));
        store.Add(Lesson("docx", null, SkillLessonKinds.Failure, "技能级失败", 200));
        store.Add(Lesson("docx", null, SkillLessonKinds.Repair, "已修复", 300));

        var n = store.ResolveFailures("docx");

        Assert.Equal(2, n);
        var remaining = store.ForAgent("writer", "docx", 10);
        Assert.Single(remaining);
        Assert.Equal(SkillLessonKinds.Repair, remaining[0].Kind);
        Assert.Contains(store.ForSkill("docx"), l => l.Kind == SkillLessonKinds.Repair && !l.Resolved);
    }

    [Fact]
    public void RemoveSkill_ClearsItsLessonsOnly()
    {
        var store = new SkillLessonStore();
        store.Add(Lesson("docx", "writer", SkillLessonKinds.Failure, "失败", 100));
        store.Add(Lesson("pptx", "writer", SkillLessonKinds.Failure, "失败2", 200));

        Assert.Equal(1, store.RemoveSkill("docx"));
        Assert.Empty(store.ForSkill("docx"));
        Assert.Single(store.ForSkill("pptx"));
    }

    [Fact]
    public void BuildPromptBlock_ContainsActionableFailureLine()
    {
        var store = new SkillLessonStore();
        store.Add(Lesson("docx", "writer", SkillLessonKinds.Failure, "连续 2/3 次产物未达标", 100,
            ["缺少正文"], "调用前确认已提供完整正文"));
        store.Add(Lesson("pptx", "writer", SkillLessonKinds.Failure, "别的技能", 200));

        var block = store.BuildPromptBlock("writer", "docx");

        Assert.Contains("经验", block);
        Assert.Contains("上次失败", block);
        Assert.Contains("缺少正文", block);
        Assert.Contains("调用前确认", block);
        Assert.DoesNotContain("别的技能", block);
        Assert.Equal("", store.BuildPromptBlock("writer", "unknown"));
    }

    [Fact]
    public void FailuresForSkill_IgnoresResolvedAndRepairLessons()
    {
        var store = new SkillLessonStore();
        store.Add(Lesson("docx", "writer", SkillLessonKinds.Failure, "未解决失败", 100, ["缺少正文"]));
        store.Add(Lesson("docx", "writer", SkillLessonKinds.Failure, "已解决失败", 200, resolved: true));
        store.Add(Lesson("docx", null, SkillLessonKinds.Repair, "已修复", 300));

        var got = store.FailuresForSkill("docx", 5);

        Assert.Single(got);
        Assert.Equal("未解决失败", got[0].Summary);
        Assert.Equal(SkillLessonKinds.Failure, got[0].Kind);
    }

    [Fact]
    public void ToPromptLine_RepairUsesPositiveWording()
    {
        var repair = Lesson("docx", null, SkillLessonKinds.Repair, "模型修正后自测通过（第 2 次）", 1,
            fix: "按新正文执行");
        Assert.Contains("修复经验", repair.ToPromptLine());
        Assert.DoesNotContain("上次失败", repair.ToPromptLine());
    }

    [Fact]
    public void RestoreState_RoundTripsAndDropsInvalid()
    {
        var src = new SkillLessonStore();
        src.Add(Lesson("docx", "writer", SkillLessonKinds.Failure, "失败", 100, ["缺少正文"]));
        src.Add(Lesson("docx", null, SkillLessonKinds.Repair, "已修复", 200));

        var dst = new SkillLessonStore();
        dst.RestoreState((IReadOnlyList<SkillLesson>)src.SnapshotState());

        Assert.Equal(2, dst.ForSkill("docx").Count);
        Assert.Contains("缺少正文", dst.BuildPromptBlock("writer", "docx"));

        // 非法行（无技能 / 无说明）不恢复
        dst.RestoreState([new SkillLesson("sl_x", "", null, SkillLessonKinds.Failure, "x", [], null, "", 1, false)]);
        Assert.Empty(dst.ForSkill("docx"));
    }
}
