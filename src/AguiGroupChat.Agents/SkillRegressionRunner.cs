namespace AguiGroupChat.Agents;

/// <summary>单条回归用例的执行结果。</summary>
public sealed record SkillRegressionCaseResult(
    string CaseId,
    string Kind,
    bool Skipped,
    bool Passed,
    int Score,
    string[] Missing);

/// <summary>一次回归运行的结果。</summary>
public sealed record SkillRegressionRunResult(
    int Ran,
    bool AllPassed,
    IReadOnlyList<SkillRegressionCaseResult> Results);

/// <summary>
/// 技能回归执行器（1.0.356）：用标准样例真实跑技能并校验收产物契约。
///
/// <para>
/// 供两处共用：经验弹窗的「跑回归」与一键修复后的自动回归。回归不保存用户输入，
/// 全程只使用 <see cref="SkillArtifactContract.SampleInput"/>；暂停中的技能也允许跑
/// （<c>ignoreSuspension</c>），用于验证“是否已修好”。
/// </para>
/// </summary>
public static class SkillRegressionRunner
{
    public const int MaxCasesPerRun = 3;

    public static async Task<SkillRegressionRunResult> RunAsync(
        AgentSkillDefinition skill,
        IReadOnlyList<SkillRegressionCase> cases,
        AgentCatalog agents,
        SkillRegressionCaseStore store,
        CancellationToken ct = default)
    {
        var results = new List<SkillRegressionCaseResult>();
        foreach (var c in (cases ?? []).Take(MaxCasesPerRun))
        {
            if (!Enum.TryParse<SkillArtifactKind>(c.Kind, true, out var kind)
                || !SkillArtifactContract.SupportsGenerationProbe(kind))
            {
                results.Add(new SkillRegressionCaseResult(c.Id, c.Kind, Skipped: true, Passed: false, Score: 0, Missing: []));
                continue;
            }
            var sample = SkillArtifactContract.SampleInput(kind);
            var output = await agents.RunSkillAsync(skill, sample, ct, ignoreSuspension: true);
            var validation = SkillArtifactContract.Validate(skill, output);
            var passed = validation.IsSatisfied && validation.Score >= c.MinScore;
            store.RecordRun(c.Id, passed, validation.Score, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            results.Add(new SkillRegressionCaseResult(
                c.Id, c.Kind, Skipped: false, passed, validation.Score, validation.MissingItems.ToArray()));
        }
        var ran = results.Count(r => !r.Skipped);
        return new SkillRegressionRunResult(ran, results.All(r => r.Skipped || r.Passed), results);
    }

    /// <summary>
    /// 一次回归是否应当阻止“修复后恢复”：有真实跑过的用例且其中有失败。
    /// 无用例 / 全部跳过时不算阻塞（没有可验证的回归面）。
    /// </summary>
    public static bool BlocksResume(SkillRegressionRunResult? result)
        => result is { Ran: > 0 } && !result.AllPassed;
}
