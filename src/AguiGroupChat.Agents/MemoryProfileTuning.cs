using System.Text.RegularExpressions;
using AguiGroupChat.Hub.Agents;

namespace AguiGroupChat.Agents;

/// <summary>
/// 数字员工「记忆拟人类型」的抽取参数解析（读取侧：回复前调取记忆时如何回忆）。
///
/// 五档拟人（用户原话归纳）：
///   1. 广记型（broad）—— 记得多、不一定准；
///   2. 深记型（deep）—— 记得少而久、一旦记住很难忘（宁缺毋滥）；
///   3. 难录入型（slowToLearn）—— 新信息难刻入，需要反复多次才记住；
///   4. 存得住想不起型（cueDependent）—— 存着但提取通道弱，见提示才恍然大悟；
///   5. 快速遗忘型（fastForgetting）—— 痕迹衰减快，只对最近发生的事清晰（自动倾向近期）。
///
/// 记忆本体为知聚共享历史（所有类型共用同一写入），因此本解析只作用于<b>抽取</b>：
/// 每次 agent run 前按类型算出本次检索的 TopK / 最小相似度 / 回忆提示分支 / 近期窗口，
/// 再作为单次覆盖传给 <c>AgentMessageMemory.SearchAsync / SearchPersonAsync</c>（store 层真正生效），
/// 并在注入段落前附一句与类型相符的「召回口吻」软性说明。数字员工未配置（MemoryProfile 为 null）时
/// 本解析返回 null，行为与全局原逻辑完全一致（向后兼容）。
///
/// 拟人边界（不伪造记忆）：广记型“记混”通过<b>置信度降级语气</b>呈现而非改写内容；存得住想不起型
/// 通过<b>提示词分支</b>临时放宽检索而非凭空补全；快速遗忘型用<b>近期窗口</b>筛掉旧记忆，不删除落库数据。
/// </summary>
public static class MemoryProfileTuning
{
    private const int GroupTopKMax = 24;
    private const int PersonalTopKMax = 16;

    /// <summary>快速遗忘型默认“近期窗口”：只召回最近 N 天内的记忆（模拟痕迹快速衰减，不删落库数据）。</summary>
    private const int FastForgettingRecencyDays = 21;

    /// <summary>“存得住想不起型”回忆提示检测：用户消息里带这类词视为正在给回忆线索，临时放宽检索。</summary>
    private static readonly Regex RecallCueRegex = new(
        @"记得|还记得|上次|之前|以前|回想|想起来|remind|remember|last time|previously|earlier",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>按数字员工的记忆拟人配置解析本次召回参数。未配置 / 未知类型返回 null（沿用全局，完全向后兼容）。</summary>
    public static MemoryProfileTuningResult? Resolve(MemoryProfile? profile, MemoryOptions memory, string? query)
    {
        if (profile is null || !MemoryPersonalityTypes.IsKnown(profile.MemoryType)) return null;

        var cueDetected = DetectRecallCue(query);
        var baseTopK = Math.Max(1, memory.TopK);
        var baseMin = ClampScore(memory.MinScore);
        var basePTopK = Math.Max(1, memory.PersonalTopK);
        var basePMin = ClampScore(memory.PersonalMinScore);

        // 默认值：跟随全局（类型微调只覆盖需要差异的维度）
        int groupTopK = baseTopK, personalTopK = basePTopK;
        double groupMin = baseMin, personalMin = basePMin;
        int? recencyDays = null;
        string recallNote;

        switch (profile.MemoryType)
        {
            case MemoryPersonalityTypes.Broad:
                // 编码宽：多取一些 + 阈值放低（把“也许相关”的边角也捞回来），但口吻必须带不确定
                groupTopK = ScaleUp(baseTopK, 1.8, min: Math.Min(GroupTopKMax, baseTopK + 1), max: GroupTopKMax);
                personalTopK = ScaleUp(basePTopK, 1.6, min: Math.Min(PersonalTopKMax, basePTopK + 1), max: PersonalTopKMax);
                groupMin = Math.Max(0.15, baseMin - 0.12);
                personalMin = Math.Max(0.15, basePMin - 0.10);
                recallNote = "你是「广记型」记忆：记得很多，但细节容易记混、甚至张冠李戴。引用上方记忆时，"
                    + "对具体数字、人名、日期等细节若记忆里不够清晰，请用「我记得好像是…」「不太确定是不是…」这类留有余地的说法，"
                    + "并优先引用带时间与来源、相似度更高的条目；不要为了显得完整而凭空补全细节。";
                break;

            case MemoryPersonalityTypes.Deep:
                // 痕迹牢固但宁缺毋滥：只取最贴近、最确定的少数条目（阈值抬高）
                groupTopK = ScaleDown(baseTopK, 0.5);
                personalTopK = ScaleDown(basePTopK, 0.5);
                groupMin = Math.Min(0.85, baseMin + 0.12);
                personalMin = Math.Min(0.85, basePMin + 0.12);
                recallNote = "你是「深记型」记忆：记得少而牢，能被你想起的通常是反复确认、印象很深的事。"
                    + "引用上方记忆时，只引用与问题相关且你有把握的部分；拿不准就明确说记不清，宁可少说也不要讲错。";
                break;

            case MemoryPersonalityTypes.SlowToLearn:
                // 能想起的多半是反复出现的“刻进去”内容：比默认更克制（取更少、阈值略高）
                groupTopK = ScaleDown(baseTopK, 0.75);
                personalTopK = ScaleDown(basePTopK, 0.75);
                groupMin = Math.Min(0.80, baseMin + 0.06);
                personalMin = Math.Min(0.80, basePMin + 0.06);
                recallNote = "你是「难录入型」记忆：新东西要反复确认你才能记住，能被你想起的多半是反复提及、印象较深的内容。"
                    + "引用上方记忆时，把它们当作相对可靠的既有结论来用；若某条记忆与当前情况冲突，请先指出冲突再谨慎作答。";
                break;

            case MemoryPersonalityTypes.CueDependent:
                if (cueDetected)
                {
                    // 提示分支：对方正在给线索 → 临时放宽提取（取更多、阈值降低），尽力“想起来”
                    groupTopK = ScaleUp(baseTopK, 1.6, min: Math.Min(GroupTopKMax, baseTopK + 2), max: GroupTopKMax);
                    personalTopK = ScaleUp(basePTopK, 1.6, min: Math.Min(PersonalTopKMax, basePTopK + 2), max: PersonalTopKMax);
                    groupMin = Math.Max(0.18, baseMin - 0.12);
                    personalMin = Math.Max(0.18, basePMin - 0.10);
                }
                else
                {
                    // 平时提取弱：自己回想容易卡壳（默认取更少、阈值更高）
                    groupTopK = ScaleDown(baseTopK, 0.55);
                    personalTopK = ScaleDown(basePTopK, 0.55);
                    groupMin = Math.Min(0.82, baseMin + 0.08);
                    personalMin = Math.Min(0.82, basePMin + 0.08);
                }
                recallNote = cueDetected
                    ? "你是「存得住、想不起型」记忆：平时自己回想容易卡壳，需要提示才恍然大悟。"
                        + "对方正在给你回忆线索（如「记得吗 / 上次 / 之前」）——请借助线索尽力回想相关历史，"
                        + "把与之吻合的记忆细节展开帮对方回忆；若仍想不起，请明确说“这部分我想不起来了”，不要编造。"
                    : "你是「存得住、想不起型」记忆：信息其实存着，但自己回想常卡壳、见到提示才恍然大悟。"
                        + "请凭当前话题尽力联想；若上方记忆与话题关联较弱，宁可先请对方补一点线索（时间 / 名字 / 关键词），"
                        + "也不要生硬地引用无关内容。";
                break;

            case MemoryPersonalityTypes.FastForgetting:
                // 只对近期清晰：召回倾向最近 RecencyWindowDays 天（在 Provider 二次筛窗），阈值略高
                groupTopK = ScaleDown(baseTopK, 0.7);
                personalTopK = ScaleDown(basePTopK, 0.7);
                groupMin = Math.Min(0.85, baseMin + 0.06);
                personalMin = Math.Min(0.85, basePMin + 0.06);
                recencyDays = FastForgettingRecencyDays;
                recallNote = "你是「快速遗忘型」记忆：旧事容易淡忘，能清晰记住的大多是最近发生的。"
                    + "引用上方记忆时，主要引用较新发生的内容；较早的信息若与当前话题相关，请标注它是较早的事并请对方确认，不要当作新近事实。";
                break;

            default:
                return null; // 未知类型视为未配置（防御）
        }

        // 用户微调覆盖：显式给出时优先于类型推算值
        if (profile.TopK is int tk) groupTopK = Math.Clamp(tk, 1, GroupTopKMax);
        if (profile.PersonalTopK is int ptk) personalTopK = Math.Clamp(ptk, 1, PersonalTopKMax);
        if (profile.MinScore is double ms) groupMin = ClampScore(ms);
        if (profile.PersonalMinScore is double pms) personalMin = ClampScore(pms);

        // 口吻模式与个性化人设（软性说明；narrate 不做，防幻觉）
        var sb = new System.Text.StringBuilder(recallNote);
        var styleMode = string.Equals(profile.StyleMode, "digest", StringComparison.OrdinalIgnoreCase) ? "digest" : "recall";
        if (styleMode == "digest")
            sb.Append("回复时请先把相关记忆概括成要点（简短带过时间与是谁说的），再自然接入你的回答。");
        if (!string.IsNullOrWhiteSpace(profile.PersonaCard))
            sb.Append("你回忆往事时的个人口吻：" + profile.PersonaCard.Trim() + "。");

        return new MemoryProfileTuningResult
        {
            MemoryType = profile.MemoryType,
            StyleMode = styleMode,
            PersonaCard = string.IsNullOrWhiteSpace(profile.PersonaCard) ? null : profile.PersonaCard.Trim(),
            CueDetected = cueDetected,
            RecencyWindowDays = recencyDays,
            RecallNote = sb.ToString().Trim(),
            Tuning = new MemoryRetrievalTuning
            {
                TopK = groupTopK,
                MinScore = groupMin,
                PersonalTopK = personalTopK,
                PersonalMinScore = personalMin,
            },
        };
    }

    /// <summary>检测当前触发消息是否含“回忆提示”（存得住想不起型的分支依据）。</summary>
    public static bool DetectRecallCue(string? query)
        => !string.IsNullOrWhiteSpace(query) && RecallCueRegex.IsMatch(query);

    /// <summary>向上缩放 TopK：至少比基准多 minDelta（扩量）但不超过上限（吸足更多候选）。</summary>
    private static int ScaleUp(int baseValue, double factor, int min, int max)
        => Math.Clamp(Math.Max(min, (int)Math.Ceiling(baseValue * factor)), 1, max);

    /// <summary>向下缩放 TopK：宁缺毋滥 / 默认弱提取（基准为 1 时保持 1，避免取不到任何候选）。</summary>
    private static int ScaleDown(int baseValue, double factor)
        => Math.Max(1, (int)Math.Ceiling(baseValue * factor));

    /// <summary>相似度阈值收敛到安全区间（0.05..0.92），避免类型推导超出边界。</summary>
    private static double ClampScore(double score)
        => Math.Clamp(score, 0.05, 0.92);
}

/// <summary>按记忆拟人类型解析出的单次召回结果（供 MemoryContextProvider 消费）。</summary>
public sealed class MemoryProfileTuningResult
{
    /// <summary>记忆类型 key（broad / deep / …）。</summary>
    public required string MemoryType { get; init; }

    /// <summary>口吻模式（recall / digest）。</summary>
    public string? StyleMode { get; init; }

    /// <summary>人设卡片（未配置为 null）。</summary>
    public string? PersonaCard { get; init; }

    /// <summary>触发消息是否命中“回忆提示”（存得住想不起型提示分支）。</summary>
    public bool CueDetected { get; init; }

    /// <summary>近期窗口（天）：非空时 Provider 只保留该时段内的记忆（快速遗忘型）。</summary>
    public int? RecencyWindowDays { get; init; }

    /// <summary>召回口吻软性说明（注入到记忆段落之前；无记忆注入时不使用）。</summary>
    public required string RecallNote { get; init; }

    /// <summary>本次检索的单次覆盖参数（传给 AgentMessageMemory.SearchAsync / SearchPersonAsync）。</summary>
    public MemoryRetrievalTuning Tuning { get; init; } = new();
}
