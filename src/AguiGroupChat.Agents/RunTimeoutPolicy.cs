using System.Text.RegularExpressions;
using AguiGroupChat.Hub.Models;
using Microsoft.Extensions.Logging;

namespace AguiGroupChat.Agents;

/// <summary>一次运行的任务复杂档位：由触发消息文本与附件元数据估得（见 <see cref="RunComplexityEstimator"/>）。</summary>
public enum RunComplexity
{
    /// <summary>简单：寒暄 / 单点问答。</summary>
    Simple,

    /// <summary>常规：一般性写作或检索请求。</summary>
    Standard,

    /// <summary>复杂：成篇交付物、多项罗列、多步要求。</summary>
    Complex,

    /// <summary>繁重：长篇幅 + 多步 + 多附件叠加。</summary>
    Heavy,
}

/// <summary>
/// 复杂度自适应超时配置（appsettings：<c>Agents:Execution:ComplexityTimeouts</c>）。
///
/// <para>
/// 背景：原先所有运行共用 <see cref="ExecutionOptions.StreamTimeoutMinutes"/>（默认 5 分钟）这一条固定预算。
/// 但“寒暄”与“41 页带插图的 PPT”是两种量级的工作——固定值要么对小任务过宽，要么对大任务过窄
/// （实测撞到过“一次提交 41 页 + 长备注，服务端处理超限”）。这里按估算出的档位乘一个倍率，
/// 并用 <see cref="MaxRunTimeoutMinutes"/> 兜住上界，避免放开后出现失控的长挂起。
/// </para>
///
/// <para>
/// 关键取舍：倍率是<b>乘在既有 StreamTimeoutMinutes 之上</b>的，而不是替换它——运营者原有的全局时限调优
/// 仍然生效，只是按任务量级按比例放宽。
/// </para>
/// </summary>
public sealed class ComplexityTimeoutOptions
{
    /// <summary>是否启用复杂度自适应超时（默认开）。关闭后所有运行一律用 StreamTimeoutMinutes 原值。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>常规档倍率（默认 1.5）。</summary>
    public double StandardMultiplier { get; set; } = 1.5;

    /// <summary>复杂档倍率（默认 2）。</summary>
    public double ComplexMultiplier { get; set; } = 2;

    /// <summary>繁重档倍率（默认 3）。</summary>
    public double HeavyMultiplier { get; set; } = 3;

    /// <summary>
    /// 单次运行主预算的绝对上界（分钟，默认 30）。无论倍率算出来多大都不会超过它；
    /// 小于 StreamTimeoutMinutes 时以 StreamTimeoutMinutes 为准（配置只放宽、不收紧）。
    /// </summary>
    public int MaxRunTimeoutMinutes { get; set; } = 30;

    /// <summary>单个技能（.NET 技能宿主）预算的绝对上界（毫秒，默认 300000 = 5 分钟）。</summary>
    public int MaxSkillTimeoutMs { get; set; } = 300_000;

    /// <summary>客户端（本机桥）技能单次调用预算的绝对上界（秒，默认 600）。</summary>
    public int MaxClientSkillTimeoutSec { get; set; } = 600;

    /// <summary>
    /// 常规档的自动放行上限（默认 40）。语义见 <see cref="AutoApprovedHeavy"/>。
    /// </summary>
    public int AutoApprovedStandard { get; set; } = 40;

    /// <summary>复杂档的自动放行上限（默认 60）。语义见 <see cref="AutoApprovedHeavy"/>。</summary>
    public int AutoApprovedComplex { get; set; } = 60;

    /// <summary>
    /// 繁重档的自动放行上限（默认 100）：同一运行内“自动放行”（已同意技能 / 批量批准）的工具调用次数上限。
    ///
    /// <para>
    /// 为什么也要按档位：<see cref="ExecutionOptions.MaxAutoApprovedRounds"/> 是一条固定值，而
    /// “一次合法的大活”本身就会调用几十次技能（40 页 PPT：出正文 → 逐页配图 → 校验 → 追加），
    /// 固定值只能按最大量级去配，于是小任务白白获得宽松额度、大任务又可能被误杀。
    /// 这里复用同一套复杂度档位（<see cref="RunComplexityEstimator"/>）按量级放宽，
    /// 与超时预算同源同档，不再是一组孤立的魔数。
    /// </para>
    ///
    /// <para>
    /// 关键取舍：档位值<b>可以突破</b> <see cref="ExecutionOptions.MaxAutoApprovedRounds"/>——
    /// 后者退化为“简单档 / 关闭自适应时的兜底”，取值时与档位值<b>取较大者</b>（只放宽不收紧）：
    /// 运营者把基准值调高过档位值时以运营者的为准，绝不会因自适应反而收紧。
    /// </para>
    ///
    /// <para>
    /// 注意：这是一道“失控循环熔断”，不是吞吐节流——墙钟成本已由运行超时兜住，
    /// 因此给得比 <see cref="ExecutionOptions.MaxInteractionRounds"/>（人工审批轮数）宽松得多
    ///（自动放行根本不打断用户）。
    /// </para>
    /// </summary>
    public int AutoApprovedHeavy { get; set; } = 100;

    /// <summary>档位 → 倍率。</summary>
    public double Multiplier(RunComplexity complexity) => complexity switch
    {
        RunComplexity.Heavy => HeavyMultiplier,
        RunComplexity.Complex => ComplexMultiplier,
        RunComplexity.Standard => StandardMultiplier,
        _ => 1,
    };

    /// <summary>
    /// 档位 → 自动放行上限；<see cref="RunComplexity.Simple"/> 返回 0 表示“本档不做覆盖”。
    /// 调用方须与运营者基准值取较大者（见 <see cref="RunTimeoutPolicy.Build"/>），这里不夹紧。
    /// </summary>
    public int AutoApprovedLimit(RunComplexity complexity) => complexity switch
    {
        RunComplexity.Heavy => AutoApprovedHeavy,
        RunComplexity.Complex => AutoApprovedComplex,
        RunComplexity.Standard => AutoApprovedStandard,
        _ => 0,
    };

    /// <summary>夹紧非法值（非正 / 非有限 / 越界）到默认；倍率下限 1（永不为“收紧超时”）。</summary>
    public ComplexityTimeoutOptions Normalize(ILogger? logger = null)
    {
        StandardMultiplier = Ratio(StandardMultiplier, Default.StandardMultiplier, 1, 4, nameof(StandardMultiplier), logger);
        ComplexMultiplier = Ratio(ComplexMultiplier, Default.ComplexMultiplier, 1, 6, nameof(ComplexMultiplier), logger);
        HeavyMultiplier = Ratio(HeavyMultiplier, Default.HeavyMultiplier, 1, 10, nameof(HeavyMultiplier), logger);
        MaxRunTimeoutMinutes = Positive(MaxRunTimeoutMinutes, Default.MaxRunTimeoutMinutes, 1, 24 * 60, nameof(MaxRunTimeoutMinutes), logger);
        MaxSkillTimeoutMs = Positive(MaxSkillTimeoutMs, Default.MaxSkillTimeoutMs, 1_000, 3_600_000, nameof(MaxSkillTimeoutMs), logger);
        MaxClientSkillTimeoutSec = Positive(MaxClientSkillTimeoutSec, Default.MaxClientSkillTimeoutSec, 10, 3_600, nameof(MaxClientSkillTimeoutSec), logger);
        AutoApprovedStandard = Positive(AutoApprovedStandard, Default.AutoApprovedStandard, 1, 10_000, nameof(AutoApprovedStandard), logger);
        AutoApprovedComplex = Positive(AutoApprovedComplex, Default.AutoApprovedComplex, 1, 10_000, nameof(AutoApprovedComplex), logger);
        AutoApprovedHeavy = Positive(AutoApprovedHeavy, Default.AutoApprovedHeavy, 1, 10_000, nameof(AutoApprovedHeavy), logger);
        return this;
    }

    /// <summary>出厂默认。</summary>
    public static ComplexityTimeoutOptions Default { get; } = new();

    private static double Ratio(double value, double fallback, double min, double max, string name, ILogger? logger)
    {
        if (double.IsFinite(value) && value >= min && value <= max) return value;
        logger?.LogWarning("Agents:Execution:ComplexityTimeouts:{Name} 配置非法（{Value}），已回退默认值 {Fallback}", name, value, fallback);
        return fallback;
    }

    private static int Positive(int value, int fallback, int min, int max, string name, ILogger? logger)
    {
        if (value >= min && value <= max) return value;
        logger?.LogWarning("Agents:Execution:ComplexityTimeouts:{Name} 配置非法（{Value}），已回退默认值 {Fallback}", name, value, fallback);
        return fallback;
    }
}

/// <summary>
/// 一次运行的超时预算（由 <see cref="RunTimeoutPolicy.Build"/> 算出）。
///
/// <para>
/// 预算是“本次运行”的整体概念：主预算（<see cref="Run"/>）控制模型 / 桥接流式调用的挂起保护，
/// <see cref="SkillTimeoutMs"/> 控制技能宿主的单技能预算，<see cref="ClientSkillTimeoutSec"/> 控制
/// 客户端技能经内网隧道执行的等待上界。三者一起放宽才不会出现“运行预算放开了、技能 60 秒先掐断”的短板。
/// </para>
///
/// <para>
/// <see cref="MaxAutoApprovedRounds"/> 是同一档位下的另一面：预算放开了，运行就该被允许真的把活干完，
/// 因此自动放行的工具调用上限也按量级定档（否则“时间够了、轮数却先到”照样会被误杀）。
/// </para>
/// </summary>
public sealed record RunTimeoutBudget(
    RunComplexity Complexity,
    int Score,
    double Multiplier,
    int BaseMinutes,
    int CeilingMinutes,
    int SkillTimeoutMs,
    int ClientSkillTimeoutSec,
    int MaxAutoApprovedRounds,
    IReadOnlyList<string> Reasons)
{
    /// <summary>本次运行的主预算（受倍率与上界夹紧）。</summary>
    public TimeSpan Run => Scale(1);

    /// <summary>
    /// 在本次预算上再乘一个阶段系数后夹紧（如指派链 ×3：计划 + 递归补查 + 交付三段共用一条链）。
    /// </summary>
    public TimeSpan Scale(double stageFactor)
        => TimeSpan.FromMinutes(Math.Clamp(BaseMinutes * Multiplier * stageFactor, 1, CeilingMinutes));

    /// <summary>单行摘要（日志 / 排查用）。</summary>
    public string Describe()
        => $"{Complexity}（分 {Score}，×{Multiplier:0.##}，{BaseMinutes}→{Run.TotalMinutes:0.#} 分钟"
           + $"，自动放行≤{MaxAutoApprovedRounds}"
           + (Reasons.Count > 0 ? $"，依据：{string.Join("、", Reasons)}" : "")
           + "）";
}

/// <summary>
/// 任务复杂度估算器（纯函数、确定性、无 IO）：只吃「触发消息文本 + 附件元数据 + 角色是否扇出」，
/// 与具体模型 / 环境无关，因此可被单测完全钉住，也让「恢复运行」时重算得到同一结果。
///
/// <para>
/// 为什么要确定性：同一轮运行会在多个入口重复计算预算（首轮 / 桥接恢复 / 审批恢复），
/// 若估算依赖时间或外部状态，恢复路径就会拿到与首轮不同的预算，出现“恢复后莫名其妙被掐断”。
/// </para>
///
/// <para>
/// 信号口径：只对正文<b>前 <see cref="PrefixChars"/> 字</b>做匹配（长消息后段几乎都是粘贴的素材，
/// 不改变任务量级的判断），且每条规则都要求“数字 + 量词”这类硬信号，避免普通用词误判。
/// </para>
/// </summary>
public static partial class RunComplexityEstimator
{
    /// <summary>参与匹配的正文字符数上限。</summary>
    private const int PrefixChars = 2_000;

    /// <summary>档位分界：≥<see cref="HeavyAt"/> 繁重，≥<see cref="ComplexAt"/> 复杂，≥<see cref="StandardAt"/> 常规。</summary>
    private const int HeavyAt = 7;
    private const int ComplexAt = 4;
    private const int StandardAt = 2;

    // ---- 信号正则：均要求“数字 + 量词”或明确的多步 / 穷尽性措辞，避免普通用词误判 ----

    /// <summary>交付物格式词（成篇交付：需要排版 / 分页 / 可能配图，是量级跳档的主因）。</summary>
    [GeneratedRegex(@"(docx|pptx|xlsx|pdf|ppt|word|powerpoint|excel|幻灯片|演示文稿|演示稿|汇报稿|表格|文档|报告|方案|白皮书|说明书|手册|纪要|周报|月报)",
        RegexOptions.IgnoreCase)]
    private static partial Regex DeliverableRx();

    /// <summary>页数（“41 页”）。</summary>
    [GeneratedRegex(@"(\d{1,3})\s*(?:页|頁)")]
    private static partial Regex PagesRx();

    /// <summary>字数（“3000 字”）。</summary>
    [GeneratedRegex(@"(\d{1,5})\s*(?:字|词|詞)")]
    private static partial Regex CharsRx();

    /// <summary>千字写法（“3k 字 / 3千字”）。</summary>
    [GeneratedRegex(@"(\d{1,4})\s*[kK千]\s*(?:字|词|詞)")]
    private static partial Regex KiloCharsRx();

    /// <summary>条目数（“12 个岗位 / 8 条要点”）。不含“页”（已由 <see cref="PagesRx"/> 单独计，避免重复加权）。</summary>
    [GeneratedRegex(@"(\d{1,3})\s*(?:个|個|条|條|项|項|张|張|篇|款|幅|组|組|轮|輪|章节|小节|部分|岗位|部门|角色)")]
    private static partial Regex ItemsRx();

    /// <summary>多步措辞（“先…再…最后 / 分别 / 逐条 / 每页”）：出现越多说明要串起来的环节越多。</summary>
    [GeneratedRegex(@"(首先|其次|然后|然後|接着|接著|最后|最後|第一步|第二步|第三步|分别|分別|逐个|逐個|逐条|逐條|逐项|逐項|逐一|依次|每页|每頁|每张|每張|每个|每個|逐页|逐頁)")]
    private static partial Regex StepRx();

    /// <summary>穷尽性措辞（“完整 / 详细 / 全面”）：通常意味着更长的输出。</summary>
    [GeneratedRegex(@"(完整|详细|詳細|详尽|詳盡|全面|深入|全套|完整版|尽可能|盡可能|尽量|豐富|丰富|细致|細緻|详实|詳實)")]
    private static partial Regex ExhaustiveRx();

    /// <summary><see cref="StepRx"/> 的计分上限（超过三个环节就不再叠加）。</summary>
    private const int MaxStepHits = 3;

    /// <summary>文档类附件的计分上限。</summary>
    private const int MaxDocAttachmentHits = 3;

    /// <summary>“大附件”门槛（2 MB）。</summary>
    private const long BigAttachmentBytes = 2L * 1024 * 1024;

    /// <summary>
    /// 估算复杂度。<paramref name="fanOut"/> 为真表示该角色会把任务向下指派 / 走编排流水线
    /// （计划 + 递归补查 + 交付，一条链的耗时天然更长），单独加一档权重。
    /// </summary>
    public static (RunComplexity Tier, int Score, IReadOnlyList<string> Reasons) Estimate(
        string? content, IReadOnlyList<AttachmentInfo>? attachments, bool fanOut = false)
    {
        var reasons = new List<string>();
        var score = 0;
        var head = Head(content);

        if (head.Length > 0)
        {
            if (DeliverableRx().IsMatch(head)) { score += 2; reasons.Add("交付物格式"); }

            var pages = MaxNumber(PagesRx(), head);
            if (pages >= 20) { score += 3; reasons.Add($"{pages} 页"); }
            else if (pages >= 8) { score += 2; reasons.Add($"{pages} 页"); }
            else if (pages >= 3) { score += 1; reasons.Add($"{pages} 页"); }

            var chars = Math.Max(MaxNumber(CharsRx(), head), MaxNumber(KiloCharsRx(), head) * 1_000);
            if (chars >= 5_000) { score += 3; reasons.Add($"{chars} 字"); }
            else if (chars >= 1_500) { score += 2; reasons.Add($"{chars} 字"); }
            else if (chars >= 500) { score += 1; reasons.Add($"{chars} 字"); }

            var items = MaxNumber(ItemsRx(), head);
            if (items >= 10) { score += 2; reasons.Add($"{items} 项"); }
            else if (items >= 4) { score += 1; reasons.Add($"{items} 项"); }

            var steps = Math.Min(MaxStepHits, StepRx().Matches(head).Count);
            if (steps > 0) { score += steps; reasons.Add($"{steps} 处多步要求"); }

            if (ExhaustiveRx().IsMatch(head)) { score += 1; reasons.Add("穷尽性要求"); }
        }

        var (docAttachments, textBytes, images) = Tally(attachments);
        if (docAttachments > 0)
        {
            score += Math.Min(MaxDocAttachmentHits, docAttachments);
            reasons.Add($"{docAttachments} 个文档附件");
        }
        if (textBytes >= BigAttachmentBytes) { score += 1; reasons.Add("附件 ≥2MB"); }
        if (images >= 5) { score += 1; reasons.Add($"{images} 张图"); }

        if (fanOut) { score += 1; reasons.Add("会向下指派"); }

        var tier = score >= HeavyAt ? RunComplexity.Heavy
            : score >= ComplexAt ? RunComplexity.Complex
            : score >= StandardAt ? RunComplexity.Standard
            : RunComplexity.Simple;
        return (tier, score, reasons);
    }

    /// <summary>取正文前缀；空 / 空白归一为等长空串。</summary>
    private static string Head(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return "";
        var trimmed = content.Trim();
        return trimmed.Length <= PrefixChars ? trimmed : trimmed[..PrefixChars];
    }

    /// <summary>正则第 1 组里的最大数值（无匹配 / 无法解析 → 0）。</summary>
    private static int MaxNumber(Regex rx, string text)
    {
        var max = 0;
        foreach (Match m in rx.Matches(text))
        {
            if (m.Groups.Count > 1 && int.TryParse(m.Groups[1].Value, out var v) && v > max) max = v;
        }
        return max;
    }

    /// <summary>附件盘点：文档类附件数、文档类总字节、图片数。</summary>
    private static (int DocAttachments, long TextBytes, int Images) Tally(IReadOnlyList<AttachmentInfo>? attachments)
    {
        if (attachments is not { Count: > 0 }) return (0, 0, 0);
        var docs = 0;
        long bytes = 0;
        var images = 0;
        foreach (var a in attachments)
        {
            var kind = a.Kind?.ToLowerInvariant();
            if (kind == "image") { images++; continue; }
            if (kind is "text" or "document") { docs++; bytes += Math.Max(0, a.Size); }
        }
        return (docs, bytes, images);
    }
}

/// <summary>
/// 运行超时策略：把「复杂度估算 → 超时预算」这一件事收敛到一个地方，并以 <see cref="Ambient"/> 广播给
/// 拿不到触发上下文的组件（技能宿主 <c>SkillRunner</c>、审批循环 <c>AgentGateway.ResumeRunAsync</c> 的自动放行熔断）。
///
/// <para>
/// 为什么用环境预算而不是逐层传参：技能执行发生在工具调用内部（模型决定 → 框架调 AIFunction → SkillRunner），
/// 中间隔着模型 SDK，无法把“本次运行的预算”作为参数传进去。这与仓库既有的
/// <c>SkillChainBuilder.Ambient</c> / <c>ToolResultCollector.Ambient</c> 是同一套路数。
/// </para>
///
/// <para>
/// 传播语义：<see cref="AsyncLocal{T}"/> 只向下游 async 流转，不回流到调用方或兄弟任务，
/// 因此并发运行之间不会串味（每个 run 是独立的异步流）。网关在每个入口（首轮 / 桥接 / 审批恢复）各设一次。
/// </para>
/// </summary>
public static class RunTimeoutPolicy
{
    /// <summary>客户端（本机桥）技能单次调用的默认上界（秒）：与既有硬编码 180 秒一致，倍率在此基础上放宽。</summary>
    private const int BaseClientSkillTimeoutSec = 180;

    private static readonly AsyncLocal<RunTimeoutBudget?> _ambient = new();

    /// <summary>当前异步流的运行超时预算（未进入运行流程时为 null）。</summary>
    public static RunTimeoutBudget? Ambient
    {
        get => _ambient.Value;
        set => _ambient.Value = value;
    }

    /// <summary>
    /// 估算复杂度并算出本次运行的超时预算。<paramref name="content"/> / <paramref name="attachments"/> 取自触发消息
    /// （恢复路径用 <c>PendingInteraction</c> 里保留的同一份上下文，因此结果与首轮一致）。
    /// 同一档位也定了本次运行的自动放行次数上限（<see cref="RunTimeoutBudget.MaxAutoApprovedRounds"/>）。
    /// </summary>
    public static RunTimeoutBudget Build(
        string? content, IReadOnlyList<AttachmentInfo>? attachments, bool fanOut,
        ExecutionOptions execution, AgentOptions options)
    {
        var opt = execution.ComplexityTimeouts ?? new ComplexityTimeoutOptions();
        var baseMinutes = Math.Max(1, execution.StreamTimeoutMinutes);
        var baseSkillMs = Math.Max(1, options.BuiltinSkillTimeoutMs);
        var baseAuto = Math.Max(1, execution.MaxAutoApprovedRounds);

        // 关闭时也要产出预算对象：调用方无需分支，且 Run 恰好等于原 StreamTimeoutMinutes（行为等价旧版）。
        if (!opt.Enabled)
            return new RunTimeoutBudget(RunComplexity.Simple, 0, 1, baseMinutes, baseMinutes,
                baseSkillMs, BaseClientSkillTimeoutSec, baseAuto, []);

        var (tier, score, reasons) = RunComplexityEstimator.Estimate(content, attachments, fanOut);
        var multiplier = opt.Multiplier(tier);

        // 上界只放宽、不收紧：配置的 Max 小于既有 StreamTimeoutMinutes 时以既有值为准。
        var ceiling = Math.Max(baseMinutes, opt.MaxRunTimeoutMinutes);

        // 技能预算同倍率放宽，但夹在「既有值」与「上限」之间——绝不因复杂度而调低技能预算。
        var skillMs = (int)Math.Clamp(baseSkillMs * multiplier, baseSkillMs, Math.Max(baseSkillMs, opt.MaxSkillTimeoutMs));
        var clientSec = (int)Math.Clamp(BaseClientSkillTimeoutSec * multiplier, BaseClientSkillTimeoutSec,
            Math.Max(BaseClientSkillTimeoutSec, opt.MaxClientSkillTimeoutSec));

        // 自动放行上限同档定档，同样只放宽不收紧：档位值低于运营者基准值（或本档不覆盖）时用基准值。
        var autoRounds = Math.Max(baseAuto, opt.AutoApprovedLimit(tier));

        return new RunTimeoutBudget(tier, score, multiplier, baseMinutes, ceiling, skillMs, clientSec, autoRounds, reasons);
    }

    /// <summary>
    /// 估算并把结果设为当前异步流的环境预算，返回该预算。网关各运行入口调用。
    /// </summary>
    public static RunTimeoutBudget Install(
        string? content, IReadOnlyList<AttachmentInfo>? attachments, bool fanOut,
        ExecutionOptions execution, AgentOptions options)
    {
        var budget = Build(content, attachments, fanOut, execution, options);
        Ambient = budget;
        return budget;
    }
}
