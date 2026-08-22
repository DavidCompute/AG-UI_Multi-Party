using System.Text.RegularExpressions;

namespace AguiGroupChat.Agents;

/// <summary>
/// 轻量 BM25 词项评分（2.1 混合检索的精排组件）。在<b>既有稠密命中集合内</b>对内容重排：
/// 用简化 BM25（对数词频 IDF、固定 K1/b）对查询词打分后与余弦相似度线性融合，并把重要级作为次级键。
/// 不改变召回集合与条数，只在同集合内调序，故与纯向量检索相比不会引入假阳性召回。
/// </summary>
public static partial class Bm25Ranker
{
    // 提取连续「数字/字母/下划线 + 汉字」块（汉字块会在 <see cref="Tokens"/> 里再逐字切分）
    [GeneratedRegex(@"[a-zA-Z0-9_\u4e00-\u9fa5]+")] private static partial Regex TokenRx();

    /// <summary>分词：ASCII 数字/字母/下划线作为整词；汉字逐字作为独立词（兼顾无空格中文）。</summary>
    private static IEnumerable<string> Tokens(string s)
    {
        foreach (Match m in TokenRx().Matches(s))
        {
            var t = m.Value;
            foreach (var ch in t)
            {
                if (ch is >= '\u4e00' and <= '\u9fa5') yield return ch.ToString(); // 汉字逐字
                else break; // 字母/数字/下划线整体作为一个词（若混有前后汉字，汉字已在前循环处理，此处不进）
            }
            // 字母数字下划线整体词：若整块均为 ASCII，整体 yield；若含汉字则在上面逐字推进后 break，未 yield 整块
            if (t.All(IsAsciiTokenChar)) yield return t.ToLowerInvariant();
        }
    }

    private static bool IsAsciiTokenChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>对 query 与一段 text 计算简化 BM25 分数并经 Sigmoid 归一化到 [0,1]。</summary>
    public static double Score(string query, string text)
    {
        if (string.IsNullOrWhiteSpace(query) || string.IsNullOrWhiteSpace(text)) return 0;
        var queryTerms = Tokens(query).Select(t => t.ToLowerInvariant()).ToList();
        if (queryTerms.Count == 0) return 0;
        var textTerms = Tokens(text).Select(t => t.ToLowerInvariant()).ToList();
        if (textTerms.Count == 0) return 0;

        var tf = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var t in textTerms) tf[t] = tf.TryGetValue(t, out var c) ? c + 1 : 1;

        double satf = 0;
        foreach (var q in queryTerms.Distinct(StringComparer.Ordinal))
        {
            if (tf.TryGetValue(q, out var f) && f > 0)
                satf += (double)f / (f + 2.0);
        }
        satf /= Math.Max(1, queryTerms.Distinct(StringComparer.Ordinal).Count());
        return 1.0 / (1.0 + Math.Exp(-4.0 * satf));
    }

    /// <summary>融合评分：返回 [0..1]，越高越靠前。cosine 为归一化余弦相似度（原 Score，通常已近 [0,1]）。</summary>
    public static double FusedScore(double cosine, double bm25, double importance, double bm25Weight)
    {
        var w = Math.Clamp(bm25Weight, 0, 0.8);
        var textScore = cosine * (1 - w) + bm25 * w; // 文本相似度（cosine + BM25 融合）
        return textScore * (1 + importance);          // 重要级加成（仅作排序用，不改变命中集合）
    }
}
