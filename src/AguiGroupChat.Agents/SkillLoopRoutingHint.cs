namespace AguiGroupChat.Agents;

/// <summary>
/// 修复健康的运行时反哺（1.0.362，<b>纯函数</b>）：把闭环健康度变成两句人话——
///
/// <list type="number">
///   <item><b>指派路由</b>：候选下游里有技能「修也修不好」时，给候选加一行参考，让模型优先把活派给更稳的人；</item>
///   <item><b>工具说明</b>：真的要用这个不稳的技能时，在执行前就把提醒写进工具描述，产出后必须自检再交付。</item>
/// </list>
///
/// <para>
/// 与 1.0.361 的「建组织时提醒」不同：这里作用在<b>每一次</b>派活 / 执行上，闭环才真正贯穿到日常干活。
/// 只输出「避用 / 留意」，健康技能不产生任何提示，避免把提示词撑满噪声。
/// </para>
/// </summary>
public static class SkillLoopRoutingHint
{
    /// <summary>工具说明里的醒目前缀（模型对带方括号的硬约束更敏感）。</summary>
    public const string WarningPrefix = "【修复健康·注意】";

    /// <summary>
    /// 指派路由的候选参考行（每个候选最多一行，取最不稳的那个技能）。
    /// 无健康数据 / 候选都健康时返回空列表。
    /// </summary>
    public static IReadOnlyList<string> BuildAssignmentLines(
        IReadOnlyDictionary<string, SkillLoopHealth>? health,
        IReadOnlyList<string> candidateIds,
        Func<string, IReadOnlyList<string>> skillsOf,
        Func<string, string> nameOf)
    {
        if (health is not { Count: > 0 } || candidateIds is not { Count: > 0 }) return [];

        var lines = new List<string>();
        foreach (var cid in candidateIds)
        {
            if (string.IsNullOrWhiteSpace(cid)) continue;
            var risky = (skillsOf(cid) ?? [])
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => health.TryGetValue(s, out var h) && h.IsRisky ? h : null)
                .Where(h => h is not null)
                .Select(h => h!)
                .OrderByDescending(h => h.IsAvoid)
                .ThenByDescending(h => h.RolledBack + h.Failed)
                .ToList();
            if (risky.Count == 0) continue;

            var worst = risky[0];
            var name = nameOf(cid);
            if (string.IsNullOrWhiteSpace(name)) name = cid;
            var extra = risky.Count > 1 ? $"，另有 {risky.Count - 1} 个技能修复不稳" : "";
            lines.Add($"{cid}（{name}）：技能 {worst.SkillId} "
                      + (worst.IsAvoid ? "修复后仍不稳定" : "修复不够稳")
                      + $"（{worst.Describe()}）{extra}");
        }
        return lines;
    }

    /// <summary>
    /// 工具说明里的提醒（不健康时返回单行，健康 / 无数据返回空串）。
    /// 避用级要求「产出后必须自检、不合格就重做」，留意级只要求抽查完整性。
    /// </summary>
    public static string BuildToolWarning(SkillLoopHealth? health, string skillId)
    {
        if (health is null || !health.IsRisky || string.IsNullOrWhiteSpace(skillId)) return "";
        return health.IsAvoid
            ? $"{WarningPrefix}技能 {skillId} {health.Describe()}。执行成功后必须自检交付物（标题 / 正文 / 页数 / 文件是否齐全），"
              + "不合格就重做或改用其他方式产出，不要把半成品直接交付给用户。"
            : $"{WarningPrefix}技能 {skillId} {health.Describe()}。执行成功后请抽查交付物是否完整。";
    }
}
