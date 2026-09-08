using System.Text;
using System.Text.RegularExpressions;

namespace AguiGroupChat.Agents;

/// <summary>一条「建议 @ 谁」结果。</summary>
public sealed record MentionSuggestion(string AgentId, string Nickname, int Score, string Reason);

/// <summary>参与评分的数字员工画像（只取评分所需字段，与存储/目录解耦）。</summary>
public sealed record MentionCandidateProfile(
    string AgentId, string Nickname, string Description, IReadOnlyList<string>? Keywords);

/// <summary>
/// 输入时「建议 @ 谁」：本地轻量规则评分（不调模型、零成本、可单测）。
/// 信号强度：显式触发词（<see cref="AgentDefinition.Keywords"/>）&gt; 消息直呼昵称 &gt;
/// 职责描述与输入文本的中英词项重叠。仅当用户尚未 @ 任何人时前端才查询；
/// 输出按分数降序取前 <see cref="MaxSuggestions"/> 名。
/// </summary>
public static class MentionSuggester
{
    public const int MaxSuggestions = 3;

    /// <summary>输入文本低于该长度不提示（避免边打字边打扰）。</summary>
    public const int MinTextChars = 5;

    /// <summary>候选最低分（低于视为噪声不返回）。</summary>
    public const int MinScore = 3;

    /// <summary>职责描述参与评分的最大长度（截断尾部，避免长描述主导）。</summary>
    public const int MaxDescriptionChars = 160;

    private static readonly Regex AsciiWord = new(@"[A-Za-z0-9_]{2,}", RegexOptions.Compiled);

    public static IReadOnlyList<MentionSuggestion> Suggest(
        string? text, IReadOnlyList<MentionCandidateProfile>? profiles, int max = MaxSuggestions)
    {
        if (string.IsNullOrWhiteSpace(text) || profiles is null || profiles.Count == 0) return [];
        var norm = text.Trim();
        if (norm.Length < MinTextChars) return [];

        var textBigrams = CjkBigrams(norm);
        var textWords = AsciiWord.Matches(norm.ToLowerInvariant())
            .Select(m => m.Value).ToHashSet(StringComparer.Ordinal);

        var scored = new List<MentionSuggestion>();
        foreach (var p in profiles)
        {
            var (score, reason) = Score(p, norm, textBigrams, textWords);
            if (score >= MinScore)
                scored.Add(new MentionSuggestion(p.AgentId, p.Nickname, score, reason));
        }
        return scored
            .OrderByDescending(s => s.Score)
            .ThenBy(s => s.AgentId, StringComparer.Ordinal)
            .Take(max)
            .ToList();
    }

    private static (int Score, string Reason) Score(
        MentionCandidateProfile p, string norm, HashSet<string> textBigrams, HashSet<string> textWords)
    {
        var score = 0;
        var hits = new List<string>();

        // 1) 显式触发词命中（信息量最大）
        var keywords = (p.Keywords ?? [])
            .Select(k => (k ?? "").Trim()).Where(k => k.Length > 0).Take(6).ToList();
        if (keywords.Count > 0)
        {
            var kwHits = keywords.Count(k => norm.Contains(k, StringComparison.OrdinalIgnoreCase));
            if (kwHits > 0) { score += Math.Min(18, kwHits * 6); hits.Add("触发词"); }
        }

        // 2) 输入直接称呼昵称
        var nick = p.Nickname?.Trim();
        if (!string.IsNullOrEmpty(nick) && nick.Length >= 2 && norm.Contains(nick, StringComparison.Ordinal))
        {
            score += 5;
            hits.Add("昵称");
        }

        // 3) 职责描述中英词项重叠（上限封顶，防长描述主导）
        var desc = p.Description ?? "";
        if (desc.Length > MaxDescriptionChars) desc = desc[..MaxDescriptionChars];
        if (desc.Length > 0)
        {
            var descBigrams = CjkBigrams(desc);
            var bigOverlap = textBigrams.Count(descBigrams.Contains);
            if (bigOverlap > 0) score += Math.Min(6, bigOverlap);
            var descWords = AsciiWord.Matches(desc.ToLowerInvariant())
                .Select(m => m.Value).ToHashSet(StringComparer.Ordinal);
            var wordOverlap = textWords.Count(descWords.Contains);
            if (wordOverlap > 0) score += Math.Min(4, wordOverlap * 2);
            if (bigOverlap > 0 || wordOverlap > 0) hits.Add("职责");
        }

        return (score, string.Join("、", hits.Distinct()));
    }

    /// <summary>提取文本内连续汉字的相邻二元组（中文词项近似）。非汉字断句。</summary>
    public static HashSet<string> CjkBigrams(string text)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(text)) return result;
        var run = new StringBuilder();
        foreach (var ch in text)
        {
            if (IsCjk(ch)) run.Append(ch);
            else { AddBigrams(run, result); run.Clear(); }
        }
        AddBigrams(run, result);
        return result;
    }

    private static bool IsCjk(char ch) => ch is >= '\u4E00' and <= '\u9FFF';

    private static void AddBigrams(StringBuilder run, HashSet<string> outSet)
    {
        for (var i = 0; i + 1 < run.Length; i++)
            outSet.Add(run.ToString(i, 2));
    }
}
