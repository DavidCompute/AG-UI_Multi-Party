using System.Text;
using System.Text.RegularExpressions;

namespace AguiGroupChat.Agents;

/// <summary>
/// 轻量 BM25 词项评分（2.1 混合检索的精排组件 + 知识库关键词召回兜底）。在既有稠密命中集合内对内容重排：
/// 用简化 BM25（对数词频 IDF、固定 K1/b）对查询词打分后与余弦相似度线性融合，并把重要级作为次级键。
/// 不改变召回集合与条数，只在同集合内调序，故与纯向量检索相比不会引入假阳性召回。
/// </summary>
public static partial class Bm25Ranker
{
    // ASCII 数字 / 字母 / 下划线整词（≥2 位，单个字母多为停用噪音）
    [GeneratedRegex(@"[a-zA-Z0-9_]{2,}")] private static partial Regex AsciiTokenRx();

    /// <summary>常见中文功能词二元组（如“我们 / 这个 / 进行 / 需要”），不计入词项相似度（去噪）。</summary>
    private static readonly HashSet<string> CjkStopBigrams = new(StringComparer.Ordinal)
    {
        "一个", "没有", "这个", "那个", "什么", "怎么", "可以", "进行", "以及", "还有",
        "因为", "但是", "然后", "现在", "已经", "需要", "知道", "觉得", "还是", "就是",
        "时候", "通过", "对于", "由于", "所以", "目前", "是否", "可能", "比较", "这样",
        "那样", "我们", "你们", "他们", "一下", "之后", "之前", "一种", "非常", "如果",
    };

    /// <summary>
    /// 词项切分：ASCII 数字/字母/下划线整词；中文取<b>相邻汉字二元组</b>（相邻两个字成词项，
    /// 兼顾中文无空格特点且比逐字 unigram 更精准），过滤高频功能词二元组。
    /// 孤立单字（整段汉字只有 1 个）不作为词项——单字检索噪音远大于价值。
    /// </summary>
    private static IEnumerable<string> Tokens(string s)
    {
        if (string.IsNullOrEmpty(s)) yield break;
        foreach (Match m in AsciiTokenRx().Matches(s))
            yield return m.Value.ToLowerInvariant();
        foreach (var bg in CjkBigramsOf(s))
        {
            if (!CjkStopBigrams.Contains(bg)) yield return bg;
        }
    }

    private static bool IsCjk(char ch) => ch is >= '\u4E00' and <= '\u9FFF';

    private static IEnumerable<string> CjkBigramsOf(string s)
    {
        var run = new StringBuilder();
        foreach (var ch in s)
        {
            if (IsCjk(ch)) run.Append(ch);
            else
            {
                foreach (var bg in EmitBigrams(run)) yield return bg;
                run.Clear();
            }
        }
        foreach (var bg in EmitBigrams(run)) yield return bg;
    }

    private static IEnumerable<string> EmitBigrams(StringBuilder run)
    {
        for (var i = 0; i + 1 < run.Length; i++)
            yield return run.ToString(i, 2);
    }

    /// <summary>对 query 与一段 text 计算简化 BM25 分数并经 Sigmoid 归一化到 [0,1]。</summary>
    public static double Score(string query, string text)
    {
        if (string.IsNullOrWhiteSpace(query) || string.IsNullOrWhiteSpace(text)) return 0;
        var queryTerms = Tokens(query).ToList();
        if (queryTerms.Count == 0) return 0;
        var textTerms = Tokens(text).ToList();
        if (textTerms.Count == 0) return 0;

        var tf = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var t in textTerms) tf[t] = tf.TryGetValue(t, out var c) ? c + 1 : 1;

        double satf = 0;
        var distinct = queryTerms.Distinct(StringComparer.Ordinal).ToList();
        foreach (var q in distinct)
        {
            if (tf.TryGetValue(q, out var f) && f > 0)
                satf += (double)f / (f + 2.0);
        }
        satf /= Math.Max(1, distinct.Count);
        return 1.0 / (1.0 + Math.Exp(-4.0 * satf));
    }

    /// <summary>融合评分：返回 [0..1]，越高越靠前。cosine 为归一化余弦相似度（原 Score，通常已近 [0,1]）。</summary>
    public static double FusedScore(double cosine, double bm25, double importance, double bm25Weight)
    {
        var w = Math.Clamp(bm25Weight, 0, 0.8);
        var textScore = cosine * (1 - w) + bm25 * w; // 文本相似度（cosine + BM25 融合）
        return textScore * (1 + importance);          // 重要级加成（仅作排序用，不改变命中集合）
    }

    /// <summary>
    /// 语义相关度（5.1 跨话题关联）：两段文本的共享关键词评分（Jaccard-ish，含重要度/词频加权），返回 [0,1]。
    /// 用于判断某话题与其它话题的讨论内容是否相关（供前端展示「也在此主题讨论过」）。
    /// </summary>
    public static double Relatedness(string a, string b)
    {
        var ta = Tokens(a ?? "").ToList();
        var tb = Tokens(b ?? "").ToList();
        if (ta.Count == 0 || tb.Count == 0) return 0;
        var setA = new HashSet<string>(ta, StringComparer.Ordinal);
        var setB = new HashSet<string>(tb, StringComparer.Ordinal);
        var inter = setA.Count(x => setB.Contains(x));
        if (inter == 0) return 0;
        var union = setA.Count + setB.Count - inter;
        return (double)inter / Math.Max(1, union); // Jaccard，取值 [0,1]
    }
}
