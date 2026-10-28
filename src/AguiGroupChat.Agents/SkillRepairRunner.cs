using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace AguiGroupChat.Agents;

/// <summary>
/// 闭环事件记录后的观察者（1.0.360）：Web 侧用它接告警 / Webhook。
/// 放在 Agents 是为了让自动修复工作线程（同样在 Agents）能通知到平台层，
/// 而不必让 Agents 反向引用 Web。
/// </summary>
public interface ISkillLoopObserver
{
    void OnOutcomeRecorded(string skillId, string outcome);
}

/// <summary>
/// 一次修复流水线的结果（1.0.360）：自测 → 写正文 → 回归 → 回滚 / 恢复 → 经验 → 闭环事件。
/// </summary>
/// <param name="Skipped">该技能不参与服务端自测（本机执行 / 不支持的类型）。</param>
/// <param name="Threw">修复过程抛异常（未改动技能）。</param>
/// <param name="Ok">自测通过。</param>
/// <param name="Attempts">模型改写尝试次数。</param>
/// <param name="LastError">最后一次自测错误。</param>
/// <param name="Applied">本次是否写入了新正文且未被回滚。</param>
/// <param name="Resumed">调用结束时技能是否处于启用状态。</param>
/// <param name="LessonSaved">是否沉淀了正向修复经验。</param>
/// <param name="RolledBack">回归未通过且已回滚到修复前正文。</param>
/// <param name="RegressionRan">本次跑的回归用例数。</param>
/// <param name="RegressionPassed">回归全部通过（或没有可跑的回归）。</param>
/// <param name="Outcome">闭环结局（skipped / failed / rolledback / applied / resumed）；未记录时为空串。</param>
public sealed record SkillRepairRunResult(
    bool Skipped,
    bool Threw,
    bool Ok,
    int Attempts,
    string? LastError,
    string? CorrectedBody,
    string? CorrectedDescription,
    bool Applied,
    bool Resumed,
    bool LessonSaved,
    bool RolledBack,
    int RegressionRan,
    bool RegressionPassed,
    SkillRegressionRunResult? Regression,
    string Outcome,
    long DurationMs);

/// <summary>
/// 技能修复流水线（1.0.360）：把原先写在 <c>POST /ag-ui/skills/{id}/repair</c> 里的
/// 「自测修复 → 写正文 → 回归 → 回滚 / 恢复 → 经验沉淀 → 闭环事件 → 告警」抽成一处，
/// 人工修复与闭环告警触发的**自动修复**走完全相同的路径，避免两套实现漂移。
///
/// <para>
/// 关键不变式（与 1.0.356 / 1.0.357 一致）：先写正文<b>不立即恢复</b>；回归全部通过才恢复挂载；
/// 回归未通过则回滚正文并保持暂停。
/// </para>
/// </summary>
public static class SkillRepairRunner
{
    public static async Task<SkillRepairRunResult> RunAsync(
        AgentSkillDefinition skill,
        AgentSkillCatalog catalog,
        AgentCatalog agents,
        AgentOptions options,
        ILoggerFactory loggerFactory,
        SkillRegressionCaseStore? regressions,
        SkillLessonStore? lessons,
        SkillLoopEventStore? loopStore,
        ISkillLoopObserver? loopObserver,
        bool apply,
        bool? resume,
        string source,
        CancellationToken ct)
    {
        var skillId = skill.SkillId;
        var watch = Stopwatch.StartNew();

        SkillAutoFixResult fix;
        try
        {
            var fixer = new SkillAutoFixer(options, agents, loggerFactory);
            fix = await fixer.VerifyOrRepairAsync(skill, maxAttempts: 3, ct);
        }
        catch (Exception ex)
        {
            loggerFactory.CreateLogger("SkillApi.Repair").LogWarning(ex, "技能诊断修复异常：{SkillId}", skillId);
            return new SkillRepairRunResult(
                false, true, false, 0, "修复过程异常：" + ex.Message, null, null,
                false, catalog.Get(skillId)?.Suspended == false, false, false, 0, true, null,
                "", watch.ElapsedMilliseconds);
        }

        var applied = false;
        // 回滚保护（1.0.357）：先记住修复前的正文，回归不通过时恢复。
        var previousBody = skill.Body;
        var previousDescription = skill.Description;
        var previousBuiltinVersion = skill.BuiltinVersion;
        // 先只写正文、不立即恢复：回归不通过时必须保持暂停（1.0.356）。
        if (apply && fix.Ok && !fix.Skipped && fix.CorrectedBody is { Length: > 0 } body)
            applied = catalog.ApplyRepair(skillId, body, fix.CorrectedDescription, resume: false);

        // 修复后自动回归（1.0.356）：有回归用例就真实跑一遍标准样例，结果回写用例。
        SkillRegressionRunResult? regression = null;
        if (apply && fix.Ok && !fix.Skipped && regressions is not null && catalog.Get(skillId) is { } live)
        {
            var cases = regressions.List(skillId);
            if (cases.Count > 0)
                regression = await SkillRegressionRunner.RunAsync(live, cases, agents, regressions, ct);
        }
        var regressionBlocked = SkillRegressionRunner.BlocksResume(regression);

        // 回归未通过：把技能恢复到修复前的正文（1.0.357），避免留下“可能更差”的版本。
        var rolledBack = false;
        if (applied && regressionBlocked)
        {
            rolledBack = catalog.RestoreBody(skillId, previousBody, previousDescription, previousBuiltinVersion);
            if (rolledBack) applied = false;
        }

        // 自测通过且回归通过（或没有可跑的回归）：才恢复挂载并失效缓存。
        if (apply && fix.Ok && !fix.Skipped && !regressionBlocked)
        {
            if (resume != false) catalog.Resume(skillId);
            agents.InvalidateAgentsUsingSkill(skillId);
        }
        else if (apply && fix.Ok && !fix.Skipped)
        {
            // 回归未通过：保持（或继续）暂停；新正文已写入，仍要让缓存重建。
            agents.InvalidateAgentsUsingSkill(skillId);
        }

        // 修复成功、真的改了正文且回归通过：旧失败经验标为已解决，并沉淀正向修复经验（1.0.349）。
        var lessonSaved = false;
        if (applied && !regressionBlocked && lessons is not null)
        {
            lessons.ResolveFailures(skillId);
            lessons.Add(new SkillLesson(
                "sl_" + Guid.NewGuid().ToString("N")[..12],
                skillId, null, SkillLessonKinds.Repair,
                $"模型修正后自测通过（第 {fix.Attempts} 次）",
                [],
                "按新正文执行；产出后仍确认交付文件齐全",
                "", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Resolved: false));
            lessonSaved = true;
        }

        // 回归未通过：记一条失败经验（含“已回滚”），避免下次再试同一种改法。
        if (regressionBlocked && lessons is not null && regression is not null)
        {
            var failed = regression.Results.Where(r => !r.Skipped && !r.Passed).ToList();
            var detail = string.Join("、", failed.Select(r => $"{r.Kind} {r.Score} 分"));
            var missing = failed.SelectMany(r => r.Missing)
                .Where(m => !string.IsNullOrWhiteSpace(m))
                .Distinct(StringComparer.Ordinal)
                .Take(3)
                .ToArray();
            lessons.Add(new SkillLesson(
                "sl_" + Guid.NewGuid().ToString("N")[..12],
                skillId, null, SkillLessonKinds.Failure,
                $"修复后回归未通过（{detail}）" + (rolledBack ? "，已回滚到修复前正文" : ""),
                missing,
                "不要沿用这次改法；先复现回归失败再重新修复，或改用更可靠的执行方案",
                "", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Resolved: false));
        }

        // 自愈闭环事件（1.0.358）：记录这次修复尝试的结局与耗时，供质量看板统计。
        var outcome = fix.Skipped
            ? SkillLoopOutcomes.Skipped
            : !fix.Ok ? SkillLoopOutcomes.Failed
            : rolledBack ? SkillLoopOutcomes.RolledBack
            : applied ? SkillLoopOutcomes.Applied
            : SkillLoopOutcomes.Resumed;
        if (apply)
        {
            loopStore?.Record(skillId, outcome, fix.Attempts, watch.ElapsedMilliseconds,
                regression?.Ran ?? 0, !regressionBlocked, source);
            // 自愈闭环告警（1.0.359）：反复失败 / 回滚时补回归用例并通知运营。
            loopObserver?.OnOutcomeRecorded(skillId, outcome);
        }

        watch.Stop();
        return new SkillRepairRunResult(
            fix.Skipped, false, fix.Ok, fix.Attempts, fix.LastError,
            fix.CorrectedBody, fix.CorrectedDescription,
            applied, catalog.Get(skillId)?.Suspended == false, lessonSaved, rolledBack,
            regression?.Ran ?? 0, !regressionBlocked, regression, apply ? outcome : "",
            watch.ElapsedMilliseconds);
    }
}
