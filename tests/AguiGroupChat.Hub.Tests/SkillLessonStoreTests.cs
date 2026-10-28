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

    // ---------- 经验管理（1.0.351） ----------

    [Fact]
    public void AddManual_CreatesNoteAndTrimsInput()
    {
        var store = new SkillLessonStore();
        Assert.Null(store.AddManual("docx", "writer", "   ", null, null));

        var lesson = store.AddManual("docx", "writer", " 先备齐正文 ", [" 缺少正文 ", "  "], " 调用前检查 ");

        Assert.NotNull(lesson);
        Assert.Equal(SkillLessonKinds.Note, lesson!.Kind);
        Assert.Equal("先备齐正文", lesson.Summary);
        Assert.Equal(["缺少正文"], lesson.Missing);
        Assert.Equal("调用前检查", lesson.Fix);
        Assert.False(lesson.Resolved);
        Assert.Contains("经验：先备齐正文", store.BuildPromptBlock("writer", "docx"));
    }

    [Fact]
    public void Remove_OnlyDeletesMatchingSkill()
    {
        var store = new SkillLessonStore();
        var docx = store.AddManual("docx", null, "无正文", null, null)!;
        store.AddManual("pptx", null, "无备注", null, null);

        Assert.False(store.Remove("pptx", docx.Id)); // 跨技能不得删
        Assert.True(store.Remove("docx", docx.Id));
        Assert.Empty(store.ForSkill("docx"));
        Assert.Single(store.ForSkill("pptx"));
        Assert.False(store.Remove("docx", docx.Id)); // 已删除
    }

    [Fact]
    public void Resolve_BatchMarksOnlyMatchingSkillAndIsIdempotent()
    {
        var store = new SkillLessonStore();
        var a = store.AddManual("docx", null, "a", null, null)!;
        var b = store.AddManual("docx", null, "b", null, null)!;
        var c = store.AddManual("pptx", null, "c", null, null)!;

        Assert.Equal(1, store.Resolve("docx", [a.Id]));
        Assert.Equal(0, store.Resolve("docx", [a.Id])); // 幂等：已解决的不会重复计数
        Assert.Equal(0, store.Resolve("pptx", [a.Id])); // 跨技能不得改

        Assert.True(store.ForSkill("docx").Single(x => x.Id == a.Id).Resolved);
        Assert.False(store.ForSkill("docx").Single(x => x.Id == b.Id).Resolved);
        Assert.False(store.ForSkill("pptx").Single(x => x.Id == c.Id).Resolved);
        Assert.Equal(0, store.Resolve("docx", []));
    }

    // ---------- 落库兜底块（1.0.353） ----------

    [Fact]
    public void BuildInstructionBlock_ListsActiveFailuresAndSkipsResolved()
    {
        var store = new SkillLessonStore();
        Assert.Equal("", store.BuildInstructionBlock(["docx"]));

        store.Add(Lesson("docx", "writer", SkillLessonKinds.Failure, "连续 2/3 次产物未达标", 100, ["缺少正文"], "先备齐正文"));
        store.Add(Lesson("pptx", null, SkillLessonKinds.Failure, "已解决", 200, resolved: true));

        var block = store.BuildInstructionBlock(["docx", "pptx"]);

        Assert.Contains("【技能经验·必须遵守】", block);
        Assert.Contains("docx", block);
        Assert.Contains("缺少正文", block);
        Assert.Contains("先备齐正文", block);
        Assert.DoesNotContain("pptx", block); // 已解决的不写
    }

    [Fact]
    public void BuildInstructionBlock_CapsSkillCount()
    {
        var store = new SkillLessonStore();
        foreach (var sid in new[] { "s1", "s2", "s3", "s4" })
            store.Add(Lesson(sid, null, SkillLessonKinds.Failure, "失败", 100, ["缺东西"]));

        var block = store.BuildInstructionBlock(["s1", "s2", "s3", "s4"], maxSkills: 2);

        Assert.Contains("s1", block);
        Assert.Contains("s2", block);
        Assert.DoesNotContain("s3", block);
        Assert.DoesNotContain("s4", block);
    }
}
