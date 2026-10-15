namespace AguiGroupChat.Agents;

/// <summary>
/// 绩效反哺编排的提示构建（纯函数，便于单测）：把候选数字员工在统计窗口内的 👍👎 汇总成
/// 「近期绩效」提示行，供任务指派路由注入提示词——让指派更偏向实测靠谱的员工。
/// </summary>
public static class PerformanceRoutingHint
{
    /// <summary>
    /// 生成候选的近期绩效提示行（每行一个候选；反馈样本不足 <paramref name="minRatings"/> 者不列出）。
    /// 返回按候选顺序的行列表；无任何可用反馈时为空。
    /// </summary>
    public static IReadOnlyList<string> BuildLines(
        IEnumerable<MessageFeedbackEntry> feedback,
        IReadOnlyList<string> candidateIds,
        Func<string, string> nameOf,
        long nowMs,
        int windowDays,
        int minRatings)
    {
        var days = Math.Clamp(windowDays, 1, 365);
        var min = Math.Max(1, minRatings);
        var fromMs = nowMs - (long)days * 24 * 3600 * 1000;

        var byAgent = (feedback ?? [])
            .Where(f => f.CreatedAtMs >= fromMs)
            .GroupBy(f => f.AgentId, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => (Up: g.Count(x => x.Value > 0), Down: g.Count(x => x.Value < 0)),
                StringComparer.Ordinal);

        var lines = new List<string>();
        foreach (var id in candidateIds)
        {
            if (string.IsNullOrWhiteSpace(id)) continue;
            if (!byAgent.TryGetValue(id, out var s)) continue;
            var total = s.Up + s.Down;
            if (total < min) continue;
            var rate = (int)Math.Round((double)s.Up / total * 100);
            var name = nameOf(id);
            if (string.IsNullOrWhiteSpace(name)) name = id;
            lines.Add($"{id}（{name}）：近期好评率 {rate}%（{s.Up}👍/{s.Down}👎，{days} 天内）");
        }
        return lines;
    }
}
