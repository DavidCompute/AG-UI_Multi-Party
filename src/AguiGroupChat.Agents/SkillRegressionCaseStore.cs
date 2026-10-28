namespace AguiGroupChat.Agents;

/// <summary>
/// 一条技能回归用例（1.0.355）：由真实产物失败触发，用<b>标准样例输入</b>重跑该技能，
/// 校验产物契约仍然通过——防止修复 / 后续改动再次退化。
///
/// <para>
/// 刻意不保存触发失败时的用户输入：回归用的是 <see cref="SkillArtifactContract.SampleInput"/> 的
/// 确定性样例，既避免把用户内容写进评测数据，也让结果可复现。
/// </para>
/// </summary>
/// <param name="Id">用例 ID。</param>
/// <param name="SkillId">技能 ID。</param>
/// <param name="Kind">产物类型（Docx / Xlsx / Pptx / Pdf）。</param>
/// <param name="MinScore">通过所需的最低分（默认与告警同口径 70）。</param>
/// <param name="Source">来源（如 <c>failure-alert</c>）。</param>
/// <param name="CreatedAtMs">创建时间（Unix 毫秒）。</param>
/// <param name="LastPassed">最近一次是否通过（null = 从未跑过）。</param>
/// <param name="LastScore">最近一次评分（0~100）。</param>
/// <param name="LastRunAtMs">最近一次运行时间（0 = 未跑过）。</param>
public sealed record SkillRegressionCase(
    string Id,
    string SkillId,
    string Kind,
    int MinScore,
    string Source,
    long CreatedAtMs,
    bool? LastPassed = null,
    int LastScore = 0,
    long LastRunAtMs = 0);

/// <summary>
/// 技能回归用例库（1.0.355）：有界内存（默认 500 条）+ 扩展区持久化。
/// 同一「技能 + 产物类型」只保留一条（<see cref="Ensure"/> 幂等），避免告警反复刷用例。
/// </summary>
public sealed class SkillRegressionCaseStore
{
    public const int MaxCases = 500;

    /// <summary>每个技能最多保留的用例数（按创建时间裁最旧）。</summary>
    public const int MaxPerSkill = 5;

    private readonly object _gate = new();
    private readonly List<SkillRegressionCase> _cases = new();

    /// <summary>幂等创建：同「技能 + 产物类型」已有用例时直接返回，不重复添加。</summary>
    public SkillRegressionCase? Ensure(string skillId, string kind, int minScore = 70, string source = "failure-alert")
    {
        if (string.IsNullOrWhiteSpace(skillId) || string.IsNullOrWhiteSpace(kind)) return null;
        lock (_gate)
        {
            var existing = _cases.FirstOrDefault(c =>
                string.Equals(c.SkillId, skillId, StringComparison.Ordinal)
                && string.Equals(c.Kind, kind, StringComparison.Ordinal));
            if (existing is not null) return existing;

            var created = new SkillRegressionCase(
                "sr_" + Guid.NewGuid().ToString("N")[..12],
                skillId, kind, Math.Clamp(minScore, 0, 100), source,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            _cases.Add(created);
            var mine = _cases.Where(c => string.Equals(c.SkillId, skillId, StringComparison.Ordinal))
                .OrderByDescending(c => c.CreatedAtMs)
                .Skip(MaxPerSkill)
                .Select(c => c.Id)
                .ToHashSet(StringComparer.Ordinal);
            if (mine.Count > 0) _cases.RemoveAll(c => mine.Contains(c.Id));
            if (_cases.Count > MaxCases)
            {
                var drop = _cases.OrderBy(c => c.CreatedAtMs).Take(_cases.Count - MaxCases)
                    .Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
                _cases.RemoveAll(c => drop.Contains(c.Id));
            }
            return created;
        }
    }

    public IReadOnlyList<SkillRegressionCase> List(string skillId)
    {
        lock (_gate)
            return _cases.Where(c => string.Equals(c.SkillId, skillId, StringComparison.Ordinal))
                .OrderByDescending(c => c.CreatedAtMs)
                .ToArray();
    }

    /// <summary>记录一次回归结果；返回更新后的用例（不存在返回 null）。</summary>
    public SkillRegressionCase? RecordRun(string caseId, bool passed, int score, long atMs)
    {
        lock (_gate)
        {
            for (var i = 0; i < _cases.Count; i++)
            {
                if (!string.Equals(_cases[i].Id, caseId, StringComparison.Ordinal)) continue;
                var updated = _cases[i] with
                {
                    LastPassed = passed,
                    LastScore = Math.Clamp(score, 0, 100),
                    LastRunAtMs = atMs,
                };
                _cases[i] = updated;
                return updated;
            }
            return null;
        }
    }

    public bool Remove(string skillId, string caseId)
    {
        if (string.IsNullOrWhiteSpace(skillId) || string.IsNullOrWhiteSpace(caseId)) return false;
        lock (_gate)
            return _cases.RemoveAll(c =>
                string.Equals(c.Id, caseId, StringComparison.Ordinal)
                && string.Equals(c.SkillId, skillId, StringComparison.Ordinal)) > 0;
    }

    public int RemoveSkill(string skillId)
    {
        if (string.IsNullOrWhiteSpace(skillId)) return 0;
        lock (_gate) return _cases.RemoveAll(c => string.Equals(c.SkillId, skillId, StringComparison.Ordinal));
    }

    public object SnapshotState()
    {
        lock (_gate) return _cases.ToArray();
    }

    public void RestoreState(IReadOnlyList<SkillRegressionCase>? rows)
    {
        lock (_gate)
        {
            _cases.Clear();
            if (rows is null) return;
            foreach (var row in rows
                         .Where(r => !string.IsNullOrWhiteSpace(r.SkillId) && !string.IsNullOrWhiteSpace(r.Kind))
                         .OrderBy(r => r.CreatedAtMs)
                         .TakeLast(MaxCases))
                _cases.Add(row);
        }
    }
}
