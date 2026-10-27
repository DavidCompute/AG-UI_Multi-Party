using System.Globalization;

namespace AguiGroupChat.Agents;

/// <summary>
/// 某个技能近期的产物质量画像（1.0.348）：由 <see cref="ArtifactQualityStore"/> 的事件聚合而来，
/// 供编排（候选排序 / 方案预警）与后续记忆沉淀使用。
/// </summary>
/// <param name="SkillId">技能 ID。</param>
/// <param name="Runs">窗口内带产物契约的执行次数。</param>
/// <param name="Valid">其中产物契约满足的次数。</param>
/// <param name="ValidRate">合格率（0~1，四位小数）。</param>
/// <param name="AvgScore">平均分（0~100，一位小数）。</param>
/// <param name="Low">低分次数（契约不满足或评分 &lt; 70）。</param>
/// <param name="TopMissing">低分记录里出现最多的缺失项（最多 3 条）。</param>
/// <param name="LastAtMs">最近一次执行时间（Unix 毫秒）。</param>
public sealed record SkillQualityProfile(
    string SkillId,
    int Runs,
    int Valid,
    double ValidRate,
    double AvgScore,
    int Low,
    string[] TopMissing,
    long LastAtMs)
{
    /// <summary>是否“质量偏低”：至少 3 次执行，且合格率 &lt; 60% 或平均分 &lt; 70。</summary>
    public bool IsRisky => Runs >= 3 && (ValidRate < 0.6 || AvgScore < 70);

    /// <summary>一段可直接进提示词 / 界面的人类可读描述（固定用小数点，避免区域设置差异）。</summary>
    public string Describe()
    {
        var pct = (int)Math.Round(ValidRate * 100);
        var text = $"近 30 天 {Runs} 次，合格率 {pct}%，平均 {AvgScore.ToString("0.0", CultureInfo.InvariantCulture)} 分";
        if (IsRisky) text += "（质量偏低，慎用；建议先修复或改用更可靠的技能）";
        if (TopMissing.Length > 0) text += "；主要缺失：" + string.Join("、", TopMissing);
        return text;
    }
}

/// <summary>
/// 技能质量画像聚合（1.0.348，纯函数）：把产物质量事件按技能汇总成画像。
/// 窗口由调用方决定（编排默认近 30 天），这里只做确定性聚合，便于单测与跨入口复用。
/// </summary>
public static class SkillQualityProfiles
{
    /// <summary>编排默认回看 30 天：既能反映近期稳定性，又不会被很久以前的故障长期拖累。</summary>
    public const int DefaultWindowDays = 30;

    /// <summary>低分阈值（与绩效看板 / 告警同一口径）。</summary>
    public const int LowScoreThreshold = 70;

    public static Dictionary<string, SkillQualityProfile> Build(IEnumerable<ArtifactQualityEvent>? events)
    {
        var map = new Dictionary<string, SkillQualityProfile>(StringComparer.Ordinal);
        foreach (var group in (events ?? [])
                     .Where(e => !string.IsNullOrWhiteSpace(e.SkillId))
                     .GroupBy(e => e.SkillId, StringComparer.Ordinal))
        {
            var list = group.ToList();
            var runs = list.Count;
            var valid = list.Count(e => e.Satisfied);
            var avg = Math.Round(list.Average(e => Math.Clamp(e.Score, 0, 100)), 1);
            var low = list.Count(IsLow);
            var missing = list.Where(IsLow)
                .SelectMany(e => e.Missing ?? [])
                .Where(m => !string.IsNullOrWhiteSpace(m))
                .GroupBy(m => m, StringComparer.Ordinal)
                .OrderByDescending(x => x.Count())
                .ThenBy(x => x.Key, StringComparer.Ordinal)
                .Take(3)
                .Select(x => x.Key)
                .ToArray();
            map[group.Key] = new SkillQualityProfile(
                group.Key, runs, valid,
                runs == 0 ? 0 : Math.Round((double)valid / runs, 4),
                avg, low, missing, list.Max(e => e.TimestampMs));
        }
        return map;
    }

    /// <summary>从存储读取并聚合；未注册存储（精简宿主 / 测试）时返回空字典。</summary>
    public static Dictionary<string, SkillQualityProfile> Load(ArtifactQualityStore? store, int days = DefaultWindowDays)
    {
        if (store is null) return new Dictionary<string, SkillQualityProfile>(StringComparer.Ordinal);
        var since = DateTimeOffset.UtcNow.AddDays(-Math.Clamp(days, 1, 365)).ToUnixTimeMilliseconds();
        return Build(store.Query(since));
    }

    private static bool IsLow(ArtifactQualityEvent e) => !e.Satisfied || e.Score < LowScoreThreshold;
}
