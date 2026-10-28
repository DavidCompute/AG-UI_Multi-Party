using System.Text;

namespace AguiGroupChat.Agents;

/// <summary>一条潜在冲突的既有记忆（Score 由向量检索给出，0..1）。</summary>
public sealed record MemoryConflictCandidate(string MessageId, string Content, long Timestamp, double Score);

/// <summary>一条判定结果：哪条旧记忆被新事实取代，以及为什么。</summary>
public sealed record MemoryConflict(string OlderMessageId, string Reason);

/// <summary>
/// 记忆冲突检测（1.0.363，<b>纯函数</b>）：同一事实被新信息推翻时，把旧记忆标为「已被取代」，
/// 让检索不再把矛盾的旧结论当事实用。
///
/// <para><b>设计取舍：宁可漏判，不要误判。</b>误标会让模型丢掉本来正确的上下文，比留着旧说法更糟。
/// 因此同时要求四个条件，缺一不可：</para>
/// <list type="number">
///   <item>新的这条**明确是更正**（含「改成 / 改为 / 换成 / 不再是 / 更正为 …」这类线索）；</item>
///   <item>两者都是**短句**（默认 ≤ 200 字符）——长消息里顺带提到“改”不该判别的记忆失效；</item>
///   <item>向量相似度 ≥ <paramref name="minScore"/>（默认 0.9，语义上说的是同一件事）；</item>
///   <item>共享 ≥ <paramref name="minSharedBigrams"/> 个字符二元组（词面锚点，防高相似但主题不同）。</item>
/// </list>
///
/// <para>检测在<b>写入侧</b>做：写入时本来就有向量，顺带检索一次同群近邻即可，
/// 不需要额外的定时扫描，也不会拖慢用户回复。</para>
/// </summary>
public static class MemoryConflictDetector
{
    /// <summary>更正线索（用户明确表达“以后按新的算”）。</summary>
    public static readonly string[] CorrectionCues =
    [
        "改成", "改为", "改由", "更正为", "修正为", "换成", "换为", "换成了", "替换为",
        "变更为", "调整为", "更新为", "不再是", "不再用", "不再由", "取消了", "取消", "作废", "废除",
        "已经改", "现在由", "现在改", "现已改", "以后用", "以后按", "今后按", "不再使用",
    ];

    /// <summary>新的这条是否包含更正线索。</summary>
    public static bool HasCorrectionCue(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        foreach (var cue in CorrectionCues)
            if (text.Contains(cue, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>
    /// 判定哪些旧记忆应被新记忆取代。返回按相似度从高到低、最多 <paramref name="maxMarks"/> 条。
    /// 新内容不含更正线索 / 过长时直接返回空（绝大多数消息走这条快路径）。
    /// </summary>
    public static IReadOnlyList<MemoryConflict> Detect(
        string? newContent,
        IReadOnlyList<MemoryConflictCandidate>? candidates,
        long newTimestamp,
        double minScore = 0.9,
        int maxChars = 200,
        int minSharedBigrams = 2,
        int maxMarks = 2)
    {
        var fresh = (newContent ?? "").Trim();
        if (fresh.Length == 0 || fresh.Length > Math.Max(1, maxChars)) return [];
        if (!HasCorrectionCue(fresh)) return [];
        if (candidates is not { Count: > 0 }) return [];

        var freshGrams = Bigrams(fresh);
        if (freshGrams.Count == 0) return [];

        var hits = new List<(MemoryConflict Conflict, double Score)>();
        foreach (var c in candidates)
        {
            if (c is null || string.IsNullOrWhiteSpace(c.MessageId) || string.IsNullOrWhiteSpace(c.Content)) continue;
            if (c.Timestamp >= newTimestamp) continue;                 // 只取代更早的
            if (c.Content.Length > Math.Max(1, maxChars)) continue;    // 旧记忆也必须是短句
            if (c.Score < minScore) continue;

            var shared = SharedBigrams(freshGrams, c.Content);
            if (shared < Math.Max(1, minSharedBigrams)) continue;

            hits.Add((new MemoryConflict(c.MessageId,
                $"更正线索 + 语义相近（相似度 {c.Score:0.00}，共享词组 {shared} 个）"), c.Score));
        }

        return hits
            .OrderByDescending(x => x.Score)
            .Take(Math.Max(1, maxMarks))
            .Select(x => x.Conflict)
            .ToList();
    }

    /// <summary>字符二元组（只保留字母 / 数字 / 汉字，忽略空白与标点）。</summary>
    public static HashSet<string> Bigrams(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
        }
        var set = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i + 1 < sb.Length; i++)
            set.Add(sb.ToString(i, 2));
        if (set.Count == 0 && sb.Length == 1) set.Add(sb.ToString());
        return set;
    }

    /// <summary>两条文本共享的二元组数量（词面锚点）。</summary>
    public static int SharedBigrams(HashSet<string> freshGrams, string other)
    {
        if (freshGrams.Count == 0 || string.IsNullOrWhiteSpace(other)) return 0;
        var n = 0;
        foreach (var gram in Bigrams(other))
            if (freshGrams.Contains(gram)) n++;
        return n;
    }
}
