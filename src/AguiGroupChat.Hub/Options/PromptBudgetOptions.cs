using Microsoft.Extensions.Logging;

namespace AguiGroupChat.Hub.Options;

/// <summary>
/// 提示词（prompt）装配的字符预算（appsettings 顶层节点 <c>PromptBudget</c>）。
///
/// <para>
/// 为什么需要它：此前这些上限散落在两处——<see cref="Storage.AttachmentStore"/> 的附件注入长度常量、
/// 以及 AgentGateway 里的一批 <c>const</c>（历史条数 / 单条截断 / 历史附件预算 / 附图数量）。
/// 它们的共同问题是：<b>各自独立、且截断完全静默</b>——用户看到“文字被截断”，却查不出是哪一层截的、
/// 截掉了多少。这里把它们收拢成一处可配预算，并新增一道<b>总闸门</b>（<see cref="MaxTotalChars"/>）。
/// </para>
///
/// <para>
/// 设计要点：单看每一项都很宽松（各项上限之和远大于总闸门），真正兜住规模的是总闸门 + 分层降级
/// （先牺牲历史与历史附件，当前消息永不截断）。这样“放宽单项”不会再意外把 prompt 撑爆。
/// </para>
///
/// <para>
/// 默认值依据（2026-09 实测，模型 DeepSeek-V4.1-Flash，官方推荐 <c>context_window = 1M tokens</c>）：
/// 本平台单次模型调用的实际 prompt 用量为 3.5K~17.5K tokens（窗口的 0.35%~1.75%），
/// 因此旧的一组值（历史单条 500 字符、附件单文件 12K、合计 60K）属于“按 128K 窗口时代定的保守值”，
/// 明显偏紧。这里按“P99 不超过窗口 1/5”重设，并把全部字段做成可配以便回退。
/// </para>
/// </summary>
public sealed class PromptBudgetOptions
{
    /// <summary>
    /// <b>总闸门</b>：单次模型调用的“消息侧”字符预算（默认 200000）。
    /// 超出时按 <see cref="TruncationOrder"/> 分层降级，而不是让 prompt 无限增长。
    /// 注意它<b>不含</b>记忆 / 知识库注入（那部分由 <c>Agents:Memory</c> 的 TopK × MaxCharsPerMemory 各自兜住），
    /// 真实总用量以模型返回的 prompt tokens 为准（超 <see cref="WarnPromptTokens"/> 会打 WARN）。
    /// </summary>
    public int MaxTotalChars { get; set; } = 200_000;

    /// <summary>注入模型上下文的群历史消息条数（滑动窗口）。默认 12。</summary>
    public int HistoryWindowMessages { get; set; } = 12;

    /// <summary>群历史里<b>单条</b>消息的正文截断长度（默认 4000；旧值 500 过紧，长消息第二轮就丢细节）。</summary>
    public int MaxCharsPerHistoryMessage { get; set; } = 4_000;

    /// <summary>群历史整段字符预算（默认 48000）：从最新往回填，填满即停，短消息不浪费额度。</summary>
    public int MaxHistoryChars { get; set; } = 48_000;

    /// <summary>把历史消息里可提取文本的附件重新内联给模型的总字符预算（默认 96000），支撑“先传文档、隔轮追问”。</summary>
    public int MaxHistoryInlineTextChars { get; set; } = 96_000;

    /// <summary>当前消息附件自动注入的单文件字符上限（默认 40000，每个文件各自享有）。</summary>
    public int AttachmentMaxTextCharsPerFile { get; set; } = 40_000;

    /// <summary>当前消息附件自动注入的总字符预算（默认 200000，全部附件共享）；超出的附件仅给元数据、可经 read_attachment 续读。</summary>
    public int AttachmentMaxTextCharsTotal { get; set; } = 200_000;

    /// <summary>单轮最多喂入的当前附图数（默认 4；超出仅元数据，防多模态 payload 过大）。</summary>
    public int MaxContextImages { get; set; } = 4;

    /// <summary>跟随提问时回喂的历史图片上限（默认 4）。</summary>
    public int MaxHistoryImages { get; set; } = 4;

    /// <summary>
    /// 实际 prompt tokens 超过此值打一条 WARN（默认 100000）。用于把“实际发了多大”变成可观测——
    /// 字符预算是估算，真实规模只有模型返回的 usage 说了算。
    /// </summary>
    public int WarnPromptTokens { get; set; } = 100_000;

    /// <summary>分层降级顺序（先牺牲谁），逗号分隔；缺省 <see cref="DefaultTruncationOrder"/>。</summary>
    public string[] TruncationOrder { get; set; } = DefaultTruncationOrder;

    /// <summary>
    /// 出厂降级顺序：群历史 → 历史附件回喂 → 当前附件（降级为仅元数据）。
    /// 当前触发消息与系统提示<b>永不截断</b>，因此不在表里。
    /// </summary>
    public static readonly string[] DefaultTruncationOrder = ["history", "history_attachments", "attachments"];

    /// <summary>合法降级阶段白名单。</summary>
    private static readonly string[] LegalStages = ["history", "history_attachments", "attachments"];

    /// <summary>出厂默认。</summary>
    public static PromptBudgetOptions Default { get; } = new();

    /// <summary>夹紧非法值（非正 / 越界回退默认）并规范化降级顺序。</summary>
    public PromptBudgetOptions Normalize(ILogger? logger = null)
    {
        MaxTotalChars = Positive(MaxTotalChars, Default.MaxTotalChars, 1_000, 10_000_000, nameof(MaxTotalChars), logger);
        HistoryWindowMessages = Positive(HistoryWindowMessages, Default.HistoryWindowMessages, 1, 500, nameof(HistoryWindowMessages), logger);
        MaxCharsPerHistoryMessage = Positive(MaxCharsPerHistoryMessage, Default.MaxCharsPerHistoryMessage, 50, 500_000, nameof(MaxCharsPerHistoryMessage), logger);
        MaxHistoryChars = Positive(MaxHistoryChars, Default.MaxHistoryChars, 100, 5_000_000, nameof(MaxHistoryChars), logger);
        MaxHistoryInlineTextChars = Positive(MaxHistoryInlineTextChars, Default.MaxHistoryInlineTextChars, 0, 5_000_000, nameof(MaxHistoryInlineTextChars), logger);
        AttachmentMaxTextCharsPerFile = Positive(AttachmentMaxTextCharsPerFile, Default.AttachmentMaxTextCharsPerFile, 100, 5_000_000, nameof(AttachmentMaxTextCharsPerFile), logger);
        AttachmentMaxTextCharsTotal = Positive(AttachmentMaxTextCharsTotal, Default.AttachmentMaxTextCharsTotal, 100, 10_000_000, nameof(AttachmentMaxTextCharsTotal), logger);
        MaxContextImages = Positive(MaxContextImages, Default.MaxContextImages, 0, 64, nameof(MaxContextImages), logger);
        MaxHistoryImages = Positive(MaxHistoryImages, Default.MaxHistoryImages, 0, 64, nameof(MaxHistoryImages), logger);
        WarnPromptTokens = Positive(WarnPromptTokens, Default.WarnPromptTokens, 1_000, 10_000_000, nameof(WarnPromptTokens), logger);
        TruncationOrder = NormalizeOrder(TruncationOrder, logger);
        return this;
    }

    /// <summary>按白名单过滤 / 去重降级顺序；整表非法回退默认。</summary>
    private static string[] NormalizeOrder(IEnumerable<string>? order, ILogger? logger)
    {
        var raw = order as string[] ?? order?.ToArray();
        if (raw is not { Length: > 0 })
        {
            logger?.LogWarning("PromptBudget:TruncationOrder 缺失或为空，已回退默认顺序");
            return (string[])DefaultTruncationOrder.Clone();
        }
        var kept = new List<string>(raw.Length);
        foreach (var item in raw)
        {
            var token = item?.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(token) || !LegalStages.Contains(token))
            {
                logger?.LogWarning("PromptBudget:TruncationOrder 含未知阶段「{Token}」（应为 history/history_attachments/attachments 之一），已剔除", item);
                continue;
            }
            if (!kept.Contains(token)) kept.Add(token);
        }
        if (kept.Count == 0)
        {
            logger?.LogWarning("PromptBudget:TruncationOrder 未含任何合法阶段，已回退默认顺序");
            return (string[])DefaultTruncationOrder.Clone();
        }
        return kept.ToArray();
    }

    private static int Positive(int value, int fallback, int min, int max, string name, ILogger? logger)
    {
        if (value >= min && value <= max) return value;
        logger?.LogWarning("PromptBudget:{Name} 配置非法（{Value}，应在 {Min}..{Max}），已回退默认值 {Fallback}", name, value, min, max, fallback);
        return fallback;
    }
}
