using AguiGroupChat.Hub.Models;

namespace AguiGroupChat.Agents;

/// <summary>一对疑似冲突的存量记忆（1.0.364）：同一说法的新旧两版口径不一致。</summary>
/// <param name="Older">更早的那条（人工确认后可被取代）。</param>
/// <param name="Newer">更晚的那条。</param>
/// <param name="SharedPrefix">两条共同的开头长度（判定依据，便于人工快速判断“是不是同一件事”）。</param>
/// <param name="Score">重合度（0..1，越大越像同一句话改了个值）。</param>
public sealed record MemoryConflictPair(
    MessageMemoryItem Older,
    MessageMemoryItem Newer,
    int SharedPrefix,
    double Score);

/// <summary>
/// 存量记忆一致性体检（1.0.364，<b>纯函数</b>）：写入期的 <see cref="MemoryConflictDetector"/>
/// 只能管「以后」——历史里已经并存的矛盾（同一项目两个负责人）不会自己消失。
/// 这里做<b>离线扫描</b>：同一知聚内两两比较，找出「像同一句话、只差一个值」的短句对，交给人工裁决。
///
/// <para><b>为何是启发式而非 LLM</b>：这是体检清单，不是自动判决——召回宽一点、让人看；
/// 误报的成本只是“多看一眼”，但漏报会让矛盾一直存在。因此这里只做词汇级判定，不调用模型：
/// </para>
/// <list type="number">
///   <item>只比**短句**（默认 ≤ 200 字符）——长文顺带提到同一件事不是冲突；</item>
///   <item>跳过知识库 / 图库行（它们是切片与描述，不是事实陈述）；</item>
///   <item>要求**共同开头 ≥ 4 字**（或二元组重合度 ≥ 0.75）——同一主题、同一句式；</item>
///   <item>其中一条是另一条的**前缀**时不算冲突（那是补充说明，例如「负责人是李四」→「负责人是李四的助理小王」）。</item>
/// </list>
/// </summary>
public static class MemoryConflictScan
{
    /// <summary>默认参与扫描的最长文本（字符）。</summary>
    public const int DefaultMaxChars = 200;

    /// <summary>默认最小共同开头长度。</summary>
    public const int DefaultMinSharedPrefix = 4;

    /// <summary>默认最小二元组重合度（共同开头不足时的兜底判据）。</summary>
    public const double DefaultMinContainment = 0.75;

    /// <summary>
    /// 找出疑似冲突对。结果按“更晚的那条”时间倒序（最近的说法优先看），最多 <paramref name="maxPairs"/> 对。
    /// <paramref name="includeAlreadySuperseded"/> = false 时跳过旧说法已被取代的对（已处理过的不再打扰）。
    /// </summary>
    public static IReadOnlyList<MemoryConflictPair> FindPairs(
        IEnumerable<MessageMemoryItem>? items,
        int maxChars = DefaultMaxChars,
        int minSharedPrefix = DefaultMinSharedPrefix,
        double minContainment = DefaultMinContainment,
        int maxPairs = 50,
        bool includeAlreadySuperseded = false)
    {
        var list = (items ?? [])
            .Where(i => i is not null)
            .Where(i => !string.IsNullOrWhiteSpace(i.Content))
            .Where(i => i.Content.Length <= Math.Max(1, maxChars))
            .Where(i => !i.SenderType.Equals("kb", StringComparison.OrdinalIgnoreCase))
            .Where(i => !i.SenderType.Equals("img", StringComparison.OrdinalIgnoreCase))
            .Where(i => includeAlreadySuperseded || string.IsNullOrEmpty(i.SupersededBy))
            .OrderBy(i => i.Timestamp)
            .ToList();
        if (list.Count < 2) return [];

        var pairs = new List<MemoryConflictPair>();
        for (var i = 1; i < list.Count; i++)
        {
            var newer = list[i];
            for (var j = 0; j < i; j++)
            {
                var older = list[j];
                if (older.Timestamp >= newer.Timestamp) continue;
                if (string.Equals(older.Content, newer.Content, StringComparison.Ordinal)) continue;

                var res = Judge(older.Content, newer.Content, minSharedPrefix, minContainment);
                if (res is null) continue;
                pairs.Add(new MemoryConflictPair(older, newer, res.Value.SharedPrefix, res.Value.Score));
            }
        }

        return pairs
            .OrderByDescending(p => p.Newer.Timestamp)
            .ThenByDescending(p => p.Score)
            .Take(Math.Max(1, maxPairs))
            .ToList();
    }

    /// <summary>
    /// 判定一对文本是否“像同一句话只差一个值”。返回共享前缀长度与重合度；不构成疑似冲突返回 null。
    /// </summary>
    public static (int SharedPrefix, double Score)? Judge(
        string older, string newer, int minSharedPrefix = DefaultMinSharedPrefix, double minContainment = DefaultMinContainment)
    {
        var a = Normalize(older);
        var b = Normalize(newer);
        if (a.Length == 0 || b.Length == 0) return null;
        if (a.Length < 4 || b.Length < 4) return null;
        if (a == b) return null;
        // 一方是另一方的前缀 = 补充说明（或截断），不是矛盾
        if (a.StartsWith(b, StringComparison.Ordinal) || b.StartsWith(a, StringComparison.Ordinal)) return null;

        var prefix = SharedPrefixLength(a, b);
        if (prefix >= Math.Max(1, minSharedPrefix) && IsSlotBoundary(a, b, prefix))
            return (prefix, Math.Round((double)prefix / Math.Min(a.Length, b.Length), 3));

        var hasCue = MemoryConflictDetector.HasCorrectionCue(a) || MemoryConflictDetector.HasCorrectionCue(b);
        // 明确更正 + 共同开头 3 字以上：如「对接人是小王」→「对接人换成了小李」，词面重合不高但语义上是同一槽位
        if (hasCue && prefix >= 3)
            return (prefix, Math.Round((double)prefix / Math.Min(a.Length, b.Length), 3));

        var grams = MemoryConflictDetector.Bigrams(a);
        var shared = MemoryConflictDetector.SharedBigrams(grams, b);
        var containment = grams.Count == 0 ? 0 : Math.Round((double)shared / grams.Count, 3);
        var threshold = hasCue ? Math.Min(minContainment, 0.5) : minContainment;
        return containment >= Math.Max(0.1, threshold) ? (prefix, containment) : null;
    }

    /// <summary>
    /// 共同开头是否停在“属性槽位”上：赋值/归属词结尾（是 / 为 / 由 / 在…）、两侧任一侧带数字（时间 / 金额 / 版本），
    /// 或文本本身含更正线索。这道闸收紧了纯前缀判据——“今天完成了报告 / 今天完成了PPT”这类
    /// 共同开头虽长、但并非同一属性的两句话就不会被误报。
    /// </summary>
    private static bool IsSlotBoundary(string a, string b, int prefix)
    {
        if (prefix <= 0 || prefix >= Math.Min(a.Length, b.Length)) return false;
        const string markers = "是为由在于用叫给";
        if (markers.Contains(a[prefix - 1])) return true;
        var tailA = a[prefix..];
        var tailB = b[prefix..];
        // 一侧尾部以系词开头（“负责人李四” vs “负责人是张三”）：标点差异被规范化后仍能认出同一槽位
        if (markers.Contains(tailA[0]) || markers.Contains(tailB[0])) return true;
        static bool HasDigit(string s) => s.Any(char.IsDigit);
        if (HasDigit(tailA) || HasDigit(tailB)) return true;
        return MemoryConflictDetector.HasCorrectionCue(a) || MemoryConflictDetector.HasCorrectionCue(b);
    }

    /// <summary>两个字符串的共同开头长度（按字符）。</summary>
    public static int SharedPrefixLength(string a, string b)
    {
        var n = Math.Min(a.Length, b.Length);
        var i = 0;
        while (i < n && a[i] == b[i]) i++;
        return i;
    }

    /// <summary>规范化：去空白与标点、统一小写（避免“项目负责人：李四”与“项目负责人是李四”因标点差异漏判）。</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var sb = new System.Text.StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
        }
        return sb.ToString();
    }
}
