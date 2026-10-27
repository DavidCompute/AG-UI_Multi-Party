namespace AguiGroupChat.Agents;

/// <summary>技能经验类型（1.0.349）。</summary>
public static class SkillLessonKinds
{
    /// <summary>产物连续低分 / 交付失败的原因。</summary>
    public const string Failure = "failure";
    /// <summary>修复成功后的正向经验（怎么改好的）。</summary>
    public const string Repair = "repair";
    /// <summary>人工补写的经验笔记（1.0.351）。</summary>
    public const string Note = "note";
}

/// <summary>
/// 一条技能经验（1.0.349）：把「这个岗位用这个技能时为什么失败、后来怎么修好的」沉淀下来，
/// 下次挂载该技能时随工具说明注入给模型，避免重蹈覆辙。
/// </summary>
/// <param name="Id">经验 ID。</param>
/// <param name="SkillId">技能 ID。</param>
/// <param name="AgentId">岗位 ID；<c>null</c> = 技能级经验（不绑定具体岗位）。</param>
/// <param name="Kind">类型，见 <see cref="SkillLessonKinds"/>。</param>
/// <param name="Summary">一句话说明（失败原因 / 修复结果）。</param>
/// <param name="Missing">产物缺失项（失败经验）。</param>
/// <param name="Fix">避免 / 改进方式。</param>
/// <param name="GroupId">触发所在的知聚（可为空串）。</param>
/// <param name="TimestampMs">经验产生时间（Unix 毫秒）。</param>
/// <param name="Resolved">是否已解决（技能修复成功后旧失败经验置为已解决，不再注入）。</param>
public sealed record SkillLesson(
    string Id,
    string SkillId,
    string? AgentId,
    string Kind,
    string Summary,
    string[] Missing,
    string? Fix,
    string GroupId,
    long TimestampMs,
    bool Resolved)
{
    /// <summary>转成一行可进模型上下文的经验（简洁、不含用户隐私内容）。</summary>
    public string ToPromptLine()
    {
        var missing = Missing is { Length: > 0 } ? "（" + string.Join("、", Missing) + "）" : "";
        if (string.Equals(Kind, SkillLessonKinds.Repair, StringComparison.Ordinal))
            return "修复经验：" + Summary + (string.IsNullOrWhiteSpace(Fix) ? "" : "；" + Fix);
        if (string.Equals(Kind, SkillLessonKinds.Note, StringComparison.Ordinal))
            return "经验：" + Summary + missing + (string.IsNullOrWhiteSpace(Fix) ? "" : "；做法：" + Fix);
        return "上次失败：" + Summary + missing + (string.IsNullOrWhiteSpace(Fix) ? "" : "；改进：" + Fix);
    }
}

/// <summary>
/// 岗位技能经验库（1.0.349）：有界内存（默认 1000 条）+ 扩展区持久化。
///
/// <para>
/// 与语义记忆（<c>IMessageMemory</c>、向量检索）分开存放是刻意的：经验是<b>确定性的工程笔记</b>
/// （哪个技能、什么缺失项、怎么改），不需要 embedding、不能被相似度稀释，也不应混进聊天记忆的
/// 召回与遗忘曲线。它随技能生命周期走：技能删除即清空，修复成功即把旧失败经验标记为已解决。
/// </para>
/// </summary>
public sealed class SkillLessonStore
{
    /// <summary>保留的经验上限；按时间裁剪最旧的。</summary>
    public const int MaxLessons = 1000;

    /// <summary>同一「技能 + 岗位 + 类型」只保留最近几条，避免同一问题刷屏。</summary>
    public const int MaxPerSkillAgent = 3;

    /// <summary>注入模型时最多几条（工具说明不能无限长）。</summary>
    public const int MaxPromptLines = 3;

    private readonly object _gate = new();
    private readonly List<SkillLesson> _lessons = new();

    /// <summary>新增一条经验；同一技能 + 岗位 + 类型只保留最近 <see cref="MaxPerSkillAgent"/> 条。</summary>
    public void Add(SkillLesson lesson)
    {
        if (string.IsNullOrWhiteSpace(lesson.SkillId) || string.IsNullOrWhiteSpace(lesson.Summary)) return;
        lock (_gate)
        {
            bool SameKey(SkillLesson l) =>
                string.Equals(l.SkillId, lesson.SkillId, StringComparison.Ordinal)
                && string.Equals(l.AgentId, lesson.AgentId, StringComparison.Ordinal)
                && string.Equals(l.Kind, lesson.Kind, StringComparison.Ordinal)
                && l.Resolved == lesson.Resolved;

            // 同一键只留最近 N-1 条，再放新的一条：避免同一问题刷屏。
            var keep = _lessons.Where(SameKey)
                .OrderByDescending(l => l.TimestampMs)
                .Take(Math.Max(0, MaxPerSkillAgent - 1))
                .ToList();
            _lessons.RemoveAll(SameKey);
            _lessons.AddRange(keep);
            _lessons.Add(lesson);
            if (_lessons.Count > MaxLessons)
                _lessons.RemoveRange(0, _lessons.Count - MaxLessons);
        }
    }

    /// <summary>某岗位挂载某技能时应看到的经验（岗位级 + 技能级，已解决的排除），最新在前。</summary>
    public IReadOnlyList<SkillLesson> ForAgent(string agentId, string skillId, int max = MaxPromptLines)
    {
        if (string.IsNullOrWhiteSpace(skillId)) return [];
        lock (_gate)
            return _lessons
                .Where(l => !l.Resolved
                            && string.Equals(l.SkillId, skillId, StringComparison.Ordinal)
                            && (l.AgentId is null || string.Equals(l.AgentId, agentId, StringComparison.Ordinal)))
                .OrderByDescending(l => l.TimestampMs)
                .Take(Math.Max(1, max))
                .ToArray();
    }

    /// <summary>某技能的全部经验（含已解决；管理界面 / 测试用）。</summary>
    public IReadOnlyList<SkillLesson> ForSkill(string skillId)
    {
        lock (_gate)
            return _lessons.Where(l => string.Equals(l.SkillId, skillId, StringComparison.Ordinal))
                .OrderByDescending(l => l.TimestampMs)
                .ToArray();
    }

    /// <summary>
    /// 编排用（1.0.350）：某技能跨岗位的<b>未解决失败</b>经验（最新在前，最多 <paramref name="max"/> 条）。
    /// 已在修复成功后解决的旧故障不计入，正向修复经验也不计入“已知坑”。
    /// </summary>
    public IReadOnlyList<SkillLesson> FailuresForSkill(string skillId, int max = 2)
    {
        if (string.IsNullOrWhiteSpace(skillId)) return [];
        lock (_gate)
            return _lessons
                .Where(l => !l.Resolved
                            && string.Equals(l.SkillId, skillId, StringComparison.Ordinal)
                            && !string.Equals(l.Kind, SkillLessonKinds.Repair, StringComparison.Ordinal))
                .OrderByDescending(l => l.TimestampMs)
                .Take(Math.Max(1, max))
                .ToArray();
    }

    /// <summary>
    /// 修复成功后：把该技能的失败经验标记为已解决（技能级 + 所有岗位级），
    /// 只保留正向的修复经验，避免旧故障继续污染上下文。
    /// </summary>
    public int ResolveFailures(string skillId)
    {
        if (string.IsNullOrWhiteSpace(skillId)) return 0;
        lock (_gate)
        {
            var n = 0;
            for (var i = 0; i < _lessons.Count; i++)
            {
                var l = _lessons[i];
                if (l.Resolved || !string.Equals(l.SkillId, skillId, StringComparison.Ordinal)) continue;
                if (string.Equals(l.Kind, SkillLessonKinds.Repair, StringComparison.Ordinal)) continue;
                _lessons[i] = l with { Resolved = true };
                n++;
            }
            return n;
        }
    }

    /// <summary>技能删除时清空其全部经验。</summary>
    public int RemoveSkill(string skillId)
    {
        if (string.IsNullOrWhiteSpace(skillId)) return 0;
        lock (_gate) return _lessons.RemoveAll(l => string.Equals(l.SkillId, skillId, StringComparison.Ordinal));
    }

    /// <summary>
    /// 人工补写一条经验笔记（1.0.351）：供经验管理界面纠偏 / 补充运营知识。
    /// 内容为空时返回 null；同「技能 + 岗位」仍按 <see cref="MaxPerSkillAgent"/> 保留最近几条。
    /// </summary>
    public SkillLesson? AddManual(string skillId, string? agentId, string summary, string[]? missing, string? fix)
    {
        var text = (summary ?? "").Trim();
        if (text.Length == 0) return null;
        if (text.Length > 500) text = text[..500];
        var miss = (missing ?? [])
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Select(m => m.Trim())
            .Take(10)
            .ToArray();
        var fixText = string.IsNullOrWhiteSpace(fix) ? null : fix.Trim();
        if (fixText is { Length: > 300 }) fixText = fixText[..300];
        var lesson = new SkillLesson(
            "sl_" + Guid.NewGuid().ToString("N")[..12],
            skillId,
            string.IsNullOrWhiteSpace(agentId) ? null : agentId.Trim(),
            SkillLessonKinds.Note,
            text, miss, fixText, "",
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Resolved: false);
        Add(lesson);
        return lesson;
    }

    /// <summary>删除单条经验（仅当它确实属于该技能时）；返回是否删除。</summary>
    public bool Remove(string skillId, string lessonId)
    {
        if (string.IsNullOrWhiteSpace(skillId) || string.IsNullOrWhiteSpace(lessonId)) return false;
        lock (_gate)
            return _lessons.RemoveAll(l =>
                string.Equals(l.Id, lessonId, StringComparison.Ordinal)
                && string.Equals(l.SkillId, skillId, StringComparison.Ordinal)) > 0;
    }

    /// <summary>
    /// 生成注入到工具说明的「经验」块；没有经验时返回空串。
    /// 不暴露用户原文，只含技能名 / 缺失项 / 改进方式。
    /// </summary>
    public string BuildPromptBlock(string agentId, string skillId)
    {
        var lessons = ForAgent(agentId, skillId);
        if (lessons.Count == 0) return "";
        return "经验（避免重蹈覆辙）：\n" + string.Join("\n", lessons.Select(l => "- " + l.ToPromptLine()));
    }

    /// <summary>
    /// 生成写入岗位说明的「技能经验·必须遵守」块（1.0.353）：组织落库时确定性追加，
    /// 不依赖模型是否记得在 instructions 里写规避步骤。无经验时返回空串。
    /// </summary>
    public string BuildInstructionBlock(IEnumerable<string>? skillIds, int maxSkills = 3)
    {
        var lines = new List<string>();
        foreach (var sid in (skillIds ?? [])
                     .Where(s => !string.IsNullOrWhiteSpace(s))
                     .Select(s => s.Trim())
                     .Distinct(StringComparer.Ordinal)
                     .Take(Math.Max(1, maxSkills)))
        {
            var failures = FailuresForSkill(sid, 1);
            if (failures.Count == 0) continue;
            lines.Add($"- 技能 {sid}：{failures[0].ToPromptLine()}");
        }
        return lines.Count == 0 ? "" : "【技能经验·必须遵守】\n" + string.Join("\n", lines);
    }

    public object SnapshotState() => ForAll();

    public void RestoreState(IReadOnlyList<SkillLesson>? rows)
    {
        lock (_gate)
        {
            _lessons.Clear();
            if (rows is null) return;
            foreach (var row in rows
                         .Where(r => !string.IsNullOrWhiteSpace(r.SkillId) && !string.IsNullOrWhiteSpace(r.Summary))
                         .OrderBy(r => r.TimestampMs)
                         .TakeLast(MaxLessons))
                _lessons.Add(row);
        }
    }

    private SkillLesson[] ForAll()
    {
        lock (_gate) return _lessons.ToArray();
    }
}
