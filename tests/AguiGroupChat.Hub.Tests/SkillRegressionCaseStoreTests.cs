using AguiGroupChat.Agents;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>
/// 技能回归用例库（1.0.355）：幂等创建、按技能上限、结果回写、删除与持久化往返。
/// </summary>
public sealed class SkillRegressionCaseStoreTests
{
    [Fact]
    public void Ensure_IsIdempotentPerSkillAndKind()
    {
        var store = new SkillRegressionCaseStore();

        var first = store.Ensure("docx_report", "Docx", 70);
        var second = store.Ensure("docx_report", "Docx", 80);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first!.Id, second!.Id);
        Assert.Single(store.List("docx_report"));
        Assert.Equal(70, first.MinScore); // 幂等：不因再次调用改阈值
        Assert.Equal("failure-alert", first.Source);

        var pptx = store.Ensure("docx_report", "Pptx");
        Assert.NotNull(pptx);
        Assert.NotEqual(first.Id, pptx!.Id);
        Assert.Equal(2, store.List("docx_report").Count);

        Assert.Null(store.Ensure(" ", "Docx"));
        Assert.Null(store.Ensure("docx_report", " "));
    }

    [Fact]
    public void Ensure_CapsCasesPerSkill()
    {
        var store = new SkillRegressionCaseStore();
        for (var i = 0; i < SkillRegressionCaseStore.MaxPerSkill + 3; i++)
            store.Ensure("s", "K" + i);

        var list = store.List("s");
        Assert.Equal(SkillRegressionCaseStore.MaxPerSkill, list.Count);
        // 最新的保留
        Assert.Contains(list, c => c.Kind == "K" + (SkillRegressionCaseStore.MaxPerSkill + 2));
    }

    [Fact]
    public void RecordRun_UpdatesMatchingCaseOnly()
    {
        var store = new SkillRegressionCaseStore();
        var a = store.Ensure("docx", "Docx")!;
        var b = store.Ensure("pptx", "Pptx")!;

        var updated = store.RecordRun(a.Id, passed: true, score: 92, atMs: 1234);

        Assert.NotNull(updated);
        Assert.True(updated!.LastPassed);
        Assert.Equal(92, updated.LastScore);
        Assert.Equal(1234, updated.LastRunAtMs);
        Assert.Null(store.List("pptx").Single(x => x.Id == b.Id).LastPassed);
        Assert.Null(store.RecordRun("missing", true, 1, 1));
    }

    [Fact]
    public void Remove_ScopesToSkill()
    {
        var store = new SkillRegressionCaseStore();
        var a = store.Ensure("docx", "Docx")!;
        store.Ensure("pptx", "Pptx");

        Assert.False(store.Remove("pptx", a.Id));
        Assert.True(store.Remove("docx", a.Id));
        Assert.Empty(store.List("docx"));
        Assert.Equal(1, store.RemoveSkill("pptx"));
        Assert.Empty(store.List("pptx"));
    }

    [Fact]
    public void BlocksResume_OnlyWhenARealCaseFailed()
    {
        Assert.False(SkillRegressionRunner.BlocksResume(null));
        Assert.False(SkillRegressionRunner.BlocksResume(new SkillRegressionRunResult(0, true, [])));

        Assert.False(SkillRegressionRunner.BlocksResume(new SkillRegressionRunResult(2, true,
        [
            new SkillRegressionCaseResult("a", "Docx", false, true, 90, []),
            new SkillRegressionCaseResult("b", "Pptx", false, true, 88, []),
        ])));

        Assert.True(SkillRegressionRunner.BlocksResume(new SkillRegressionRunResult(2, false,
        [
            new SkillRegressionCaseResult("a", "Docx", false, true, 90, []),
            new SkillRegressionCaseResult("b", "Pptx", false, false, 40, ["缺少正文"]),
        ])));

        // 全部跳过（无统一输入契约的类型）：不算阻塞
        Assert.False(SkillRegressionRunner.BlocksResume(new SkillRegressionRunResult(0, true,
            [new SkillRegressionCaseResult("a", "Archive", true, false, 0, [])])));
    }

    [Fact]
    public void Restore_RoundTripsAndDropsInvalid()
    {
        var src = new SkillRegressionCaseStore();
        var a = src.Ensure("docx", "Docx")!;
        src.RecordRun(a.Id, true, 88, 999);

        var dst = new SkillRegressionCaseStore();
        dst.RestoreState((IReadOnlyList<SkillRegressionCase>)src.SnapshotState());

        var restored = dst.List("docx").Single();
        Assert.Equal(a.Id, restored.Id);
        Assert.True(restored.LastPassed);
        Assert.Equal(88, restored.LastScore);

        dst.RestoreState([new SkillRegressionCase("sr_bad", "", "Docx", 70, "x", 1)]);
        Assert.Empty(dst.List("docx"));
    }
}
