using System.Text.Json;
using AguiGroupChat.Hub.Infra;
using AguiGroupChat.Hub.Models;

namespace AguiGroupChat.Agents;

/// <summary>确定性编排计划的单个步骤。Action: dispatch | skill | answer。</summary>
internal sealed record PlanStep(string Action, string Target, string? Note);

/// <summary>编排计划上下文：计划步骤 + 可指派的员工清单 + 可调用的技能库。供随消息流逐项执行。</summary>
internal sealed record CoordinatedPlan(
    List<PlanStep> Steps,
    IReadOnlyList<AgentDefinition> Reached,
    Dictionary<string, AgentSkillDefinition> Skills,
    string Input);

/// <summary>
/// 智能体网关的纯静态工具集合：与实例状态（会话 / 流式缓冲 / 交互现场）无关的格式化、
/// 校验与文本处理。把这类无副作用的逻辑从 <see cref="AgentGateway"/>（God class）中剥离，
/// 便于独立单测且降低网关类的认知负载。改这些方法不影响任何运行状态。
/// </summary>
internal static class AgentGatewayHelpers
{
    /// <summary>工具调用参数 / 结果的展示截断长度（结果可能很大，如 bash 输出，放宽到 5000 供前端滚动查看）。</summary>
    internal const int MaxToolResultChars = 5000;

    /// <summary>外部 AG-UI 附件名最大长度（超长截断，防前端 / 存储被撑爆）。</summary>
    internal const int MaxAttachmentNameChars = 255;

    /// <summary>按端点 scheme 判断是否走 WebSocket 桥接传输（http/https 走官方 AGUIChatClient）。</summary>
    internal static bool IsWebSocketEndpoint(string endpoint)
        => endpoint.StartsWith("ws://", StringComparison.OrdinalIgnoreCase)
            || endpoint.StartsWith("wss://", StringComparison.OrdinalIgnoreCase);

    /// <summary>模型调用可重试错误：HTTP 429（限流）或 5xx（网关暂时性故障）；连接重置 / 超时也可重试。
    /// 取消（OperationCanceledException）不算可重试，走正常取消路径。</summary>
    internal static bool IsRetryableModelError(Exception ex)
    {
        if (ex is HttpRequestException { StatusCode: { } code })
            return (int)code == 429 || (int)code >= 500;
        return ex is IOException or TimeoutException;
    }

    /// <summary>外部 AG-UI 会话 threadId 派生：main 话题沿用群级 threadId，非 main 话题追加话题后缀（会话按话题隔离）。</summary>
    internal static string BuildExternalThreadId(string threadId, string? topicId)
        => string.IsNullOrEmpty(topicId) || topicId == "main" ? threadId : $"{threadId}:{topicId}";

    /// <summary>链式调用文本截断（保留原始语义）。</summary>
    internal static string TruncateForChain(string? s)
    {
        const int max = 200;
        return string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max] + "…");
    }

    /// <summary>按固定大小切分长文本（流式分帧 / 前端滚动用）。</summary>
    internal static IEnumerable<string> ChunkReply(string text, int size)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= size) { yield return text; yield break; }
        var pos = 0;
        while (pos < text.Length)
        {
            var len = Math.Min(size, text.Length - pos);
            yield return text.Substring(pos, len);
            pos += len;
        }
    }

    /// <summary>外部 AG-UI 附件 → 群聊消息附件（AttachmentInfo：ext_ id + 外部 URL）。URL 仅放行
    /// http/https 与 data:image 前缀（与前端渲染 scheme 白名单一致），其余丢弃——防外部服务下发
    /// javascript: 等危险 scheme 诱导前端 / 用户访问。附件名截断到 <see cref="MaxAttachmentNameChars"/> 字符。</summary>
    internal static IReadOnlyList<AttachmentInfo> ToAttachmentInfos(IReadOnlyList<BridgeAttachment> attachments)
        => attachments
            .Where(a => IsAllowedAttachmentUrl(a.Url))
            .Select(a => new AttachmentInfo
            {
                AttachmentId = "ext_" + IdGenerator.NewId(),
                Name = TruncateAttachmentName(a.Name),
                ContentType = a.Kind == "image" ? "image/png" : "application/octet-stream",
                Size = 0,
                Url = a.Url,
                Kind = a.Kind,
            }).ToList();

    /// <summary>外部 AG-UI 附件 URL 白名单：仅放行 http/https 与 data:image 前缀，其余 scheme 一律丢弃。</summary>
    internal static bool IsAllowedAttachmentUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        var lower = url.Trim().ToLowerInvariant();
        return lower.StartsWith("http://", StringComparison.Ordinal)
            || lower.StartsWith("https://", StringComparison.Ordinal)
            || lower.StartsWith("data:image", StringComparison.Ordinal);
    }

    /// <summary>附件名截断到 <see cref="MaxAttachmentNameChars"/> 字符（外部文件名可能超长）。</summary>
    internal static string TruncateAttachmentName(string? name)
    {
        var n = string.IsNullOrWhiteSpace(name) ? "attachment" : name;
        return n.Length > MaxAttachmentNameChars ? n[..MaxAttachmentNameChars] : n;
    }

    /// <summary>字节数的人读格式（B / KB / MB）。</summary>
    internal static string FormatBytes(long bytes)
        => bytes >= 1024 * 1024 ? $"{bytes / 1024.0 / 1024.0:0.#} MB"
            : bytes >= 1024 ? $"{bytes / 1024.0:0.#} KB"
            : $"{bytes} B";

    /// <summary>工具执行结果的展示文本：字符串原样，对象序列化 JSON；超长截断。</summary>
    internal static string DescribeToolResult(object? result)
    {
        var text = result switch
        {
            null => "",
            string s => s,
            _ => JsonSerializer.Serialize(result),
        };
        return text.Length > MaxToolResultChars ? text[..MaxToolResultChars] + "…" : text;
    }

    /// <summary>用 AguiJson 反序列化（工作型智能体 publish_file 标记载荷）；失败返回 null。</summary>
    internal static T? AguiJsonOrDefault<T>(string json) where T : class
    {
        try { return JsonSerializer.Deserialize<T>(json, AguiJson.Options); }
        catch { return null; }
    }

    /// <summary>
    /// 模型调用错误的人读描述（脱敏）：只输出固定文案 + 错误码，原始错误详情
    /// （异常链 / 响应体，可能含敏感信息）仅记录到日志（调用点 LogWarning），不向前端透出内部细节。
    /// </summary>
    internal static string DescribeModelError(Exception ex)
    {
        var code = ex is System.ClientModel.ClientResultException { Status: { } status }
            ? status.ToString()
            : "MODEL_ERROR";
        return $"模型调用失败（{code}）";
    }

    /// <summary>技能正文里的外部输入占位符（${query} / ${xxx}）→ 该技能运行时需要填入的参数名。</summary>
    internal static List<string> SkillRequiredInputs(AgentSkillDefinition skill)
        => System.Text.RegularExpressions.Regex.Matches(skill.Body ?? "", @"\$\{([a-zA-Z_][a-zA-Z0-9_]*)\}")
            .Select(m => m.Groups[1].Value).Where(v => !string.IsNullOrWhiteSpace(v)).Distinct().ToList();

    /// <summary>
    /// 该技能是否<b>必须拿到上游产出才能正确执行</b>（供规划器排定“先产出、后转换”的依赖顺序）。
    ///
    /// <para>
    /// 为什么需要单独判别：<see cref="SkillRequiredInputs"/> 只看正文里的 <c>${xxx}</c> 占位符，
    /// 而 <b>dotnet / shell 类技能的入参是函数签名或 stdin，正文里根本没有占位符</b> —— 
    /// 于是规划器标不出【需要输入：…】，“先拿到输入再调该技能”的依赖规则对它们彻底失效。
    /// 实测踩到：docx_report / md_to_docx 被排在执笔岗<b>之前</b>执行，拿到空输入，
    /// 导出的 Word 就一个标题、正文全空。
    /// </para>
    ///
    /// 判据（宁漏勿误，只抓特征明显的）：
    /// 1) 有 <c>${xxx}</c> 占位符 → 需要输入（原口径）；
    /// 2) 转换 / 导出 / 排版类技能（kind=dotnet/shell 且描述里出现“导出/排版/转换/生成…文档”类词）→ 需要输入；
    /// 3) 描述里显式说到入参（Markdown / 正文 / 文本 / 内容 / 稿件 / input）→ 需要输入。
    /// </summary>
    internal static List<string> RequiredUpstreamInputs(AgentSkillDefinition skill)
    {
        var declared = SkillRequiredInputs(skill);
        if (declared.Count > 0) return declared;

        // 只对“真会执行”的技能判定：prompt 技能不产生文件，排错顺序也不会有可下载产物丢失
        if (skill.Kind is not (AgentSkillKind.Dotnet or AgentSkillKind.Shell)) return [];

        // 文档生成器（docx_* / md_to_docx / *xlsx* …）：它的活就是把内容转成文件，必然需要内容。
        // 实测踩到：旧口径只认「生成文档 / 生成文件」连写，而内置技能描述写的是
        // “生成规范排版的党政机关公文 Word 文档”，一个都没命中，于是守卫失效。
        if (IsDocumentGenerator(skill)) return ["content"];

        var text = ((skill.SkillId ?? "") + " " + (skill.Name ?? "") + " " + (skill.Description ?? ""))
            .ToLowerInvariant();

        // 其它导出 / 排版 / 转换类
        string[] convertKeys =
        [
            "导出", "排版", "转换", "转成", "生成文档", "生成文件",
            "export", "convert", "render",
        ];
        if (convertKeys.Any(k => text.Contains(k, StringComparison.Ordinal))) return ["content"];
        if (text.Contains("markdown", StringComparison.Ordinal)) return ["content"];
        return [];
    }

    /// <summary>
    /// 判断一段技能输入是否“实际上是空的”——包括网关自己填的兑底文案。
    ///
    /// <para>
    /// 为什么不能只判 <c>IsNullOrWhiteSpace</c>：上游岗位返回空时，
    /// ExecuteCoordinatedPlanAsync 会把字面量 <c>（未返回内容）</c> 当作结果继续往下传，
    /// 于是后续技能收到一个“看着非空、实际无内容”的占位串，会照着它生成空壳文件。
    /// 实测踩到：docx_report query=（未返回内容） → 导出的 Word 只有标题。
    /// </para>
    /// </summary>
    internal static bool LooksLikeEmptyInput(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return true;
        var t = text.Trim();
        // 网关自己的兑底文案，以及常见等价写法
        string[] placeholders =
        [
            "（未返回内容）", "(未返回内容)", "（无）", "(无)",
            "（未返回）", "（无内容）", "（空）", "（无可用内容）",
        ];
        if (placeholders.Any(p => string.Equals(t, p, StringComparison.Ordinal))) return true;
        // 纯平台前言（语言提示等）→ 不是可排版的正稿。
        // 实测踩到：docx_gongwen 收到的输入是“（请用中文回复，提问者消息以中文为主。）”，
        // 它长度过短但非空，旧口径放过了，于是技能报 JsonReaderException。
        if (IsPlatformPreambleLine(t)) return true;
        // 去掉标点与空白后仍很短 → 基本不可能是一份可排版的正稿
        var core = new string(t.Where(c => !char.IsWhiteSpace(c) && !"（）()【】[]，,。.：:；;！!？?—-‒–".Contains(c)).ToArray());
        return core.Length < 2;
    }

    /// <summary>
    /// 该输入是否“不像是可排版 / 可交付的正文”（过短或全是前言）。
    ///
    /// <para>
    /// 比 <see cref="LooksLikeEmptyInput"/> 更严：额外要求达到一个最小长度。
    /// 用于交付类技能的输入闸门——它们拿到一小段闲聊句也生成不出有意义的文件，
    /// 不如直接用群里已有的产出去兑底（或如实播报原因）。
    /// </para>
    /// </summary>
    internal static bool LooksTooThinForDelivery(string? text, int minChars = 120)
        => LooksLikeEmptyInput(text) || (text?.Trim().Length ?? 0) < minChars;

    /// <summary>
    /// 该技能是否是“文档生成器”（把内容落成 Word/Excel/PPT/PDF 文件）。
    ///
    /// <para>
    /// 为什么需要它：这类技能的入参是<b>结构化 JSON</b>（如 <c>{title, sections:[...]}</c>），
    /// 必须由<b>模型当工具调用</b>才能正确构造。而编排计划路径按“纯文本进、纯文本出”直接调技能，
    /// 会把一段群上下文/前言原样塑给它 → JsonReaderException / 导出空壳文档。
    /// 因此计划路径应跳过这类技能，改由交付兑底（完整流式）让模型自己构造 JSON 并调用。
    /// </para>
    ///
    /// 判据（宁漏勿误）：
    /// 1) 可执行类技能（dotnet/shell），且
    /// 2) 技能 id 或描述里出现文档产出特征（docx / xlsx / pptx / pdf / Word 文档 / Excel …）。
    /// </summary>
    internal static bool IsDocumentGenerator(AgentSkillDefinition skill)
    {
        if (skill.Kind is not (AgentSkillKind.Dotnet or AgentSkillKind.Shell)) return false;
        var text = ((skill.SkillId ?? "") + " " + (skill.Name ?? "") + " " + (skill.Description ?? ""))
            .ToLowerInvariant();
        return text.Contains("docx", StringComparison.Ordinal)
            || text.Contains("xlsx", StringComparison.Ordinal)
            || text.Contains("pptx", StringComparison.Ordinal)
            || text.Contains("pdf", StringComparison.Ordinal)
            || text.Contains("word 文档", StringComparison.Ordinal)
            || text.Contains("excel", StringComparison.Ordinal)
            || text.Contains("演示文稿", StringComparison.Ordinal);
    }

    /// <summary>
    /// 技能 id 是否匹配某交付前缀（docx_ / xlsx_ / pptx_ / pdf_）。
    ///
    /// <para>
    /// 不能只判 <c>StartsWith</c>：内置技能有 <c>docx_report</c>（前缀式），
    /// 但编排出的常见命名是 <c>md_to_docx</c>（后缀式）—— 只判前缀会漏掉它，
    /// 于是“用户要 Word、团队里确实有人能产 Word”却找不到交付人。
    /// </para>
    /// </summary>
    internal static bool SkillMatchesDeliverablePrefix(string? skillId, string prefix)
    {
        if (string.IsNullOrWhiteSpace(skillId)) return false;
        if (skillId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
        var ext = prefix.TrimEnd('_'); // docx / xlsx / pptx / pdf
        return skillId.Contains(ext, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 文档生成技能的入参校验：正文为空时返回“怎么改”的提示（非 null = 拒绝执行），合格返回 null。
    ///
    /// <para>
    /// 为什么在工具层拦：模型经常把正文写在聊天里、却只给工具传 title/subtitle，
    /// 于是用户拿到的 Word 只有标题（实测多次）。在<b>真实执行那一刻</b>校验，
    /// 无论走流式还是审批恢复路径都能拦住，并把可执行的纠正话术回给模型让它重试。
    /// </para>
    ///
    /// 口径（宁放行勿误拦）：
    /// - 入参不是 JSON（如 md_to_docx 直接吃 Markdown 正文）→ 不拦，交技能自己处理；
    /// - JSON 里正文类字段（markdown/content/body/text）非空 → 放行；
    /// - JSON 里 sections 是非空数组 → 放行；
    /// - JSON 解析失败 → 不拦（让技能给出真实报错，避免我们掩盖问题）。
    /// </summary>
    internal static string? ValidateDocumentSkillInput(AgentSkillDefinition skill, string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return RejectText(skill);
        var t = query.Trim();
        if (!t.StartsWith('{')) return null; // 非 JSON：可能是纯 Markdown 正文 / 平台占位，不拦
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(t);
            var root = doc.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object) return null;

            // 1) 正文类字符串字段非空 → 放行
            foreach (var key in new[] { "markdown", "content", "body", "text" })
                if (root.TryGetProperty(key, out var v)
                    && v.ValueKind == System.Text.Json.JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(v.GetString()))
                    return null;

            // 2) sections 非空数组 → 还要看每项的<b>形状</b>对不对
            if (root.TryGetProperty("sections", out var sec))
            {
                if (sec.ValueKind != System.Text.Json.JsonValueKind.Array) return RejectText(skill);
                if (sec.GetArrayLength() == 0) return RejectText(skill);
                // 实测踩到：模型用了“合理但错误”的 {type,text} 形状：
                //   { "type": "heading", "text": "一、简介" }
                // 本类技能的约定是<b>键名即类型</b>：{ "heading": "一、简介", "level": 1 }。
                // 形状不对时技能一个块也不认识，结果就是“只有标题”的空壳文档。
                if (!HasRecognizedSectionShape(sec)) return WrongShapeText(skill);
                return null;
            }

            // 3) 既无正文也无 sections：只有 title 一类的“封面”调用 → 拦
            return RejectText(skill);
        }
        catch
        {
            return null; // 解析失败：交技能报真实错误
        }
    }

    /// <summary>拒绝执行时回给模型的可执行提示（要说清怎么改，否则模型只会重复同样调用）。</summary>
    private static string RejectText(AgentSkillDefinition skill)
        => $"（未生成文件：技能 {skill.SkillId} 的入参里没有正文，照此生成的文档将只有标题。"
         + "请把你刚才写在回复里的正文内容，逐节填进 sections 数组——"
         + "每项形如 {\"heading\":\"一、小节\",\"level\":1}、{\"paragraph\":\"正文段落\"}、{\"bullets\":[\"要点一\",\"要点二\"]}、"
         + "{\"table\":{\"headers\":[\"列1\",\"列2\"],\"rows\":[[\"a\",\"b\"]]}}，然后重新调用本技能。）";

    /// <summary>本类文档技能的可用内容块键（键名即类型）。</summary>
    private static readonly string[] SectionKeys =
        ["heading", "paragraph", "bullets", "numbered", "quote", "table", "image", "chart", "toc", "pageBreak"];

    /// <summary>sections 里是否至少有一项用了约定的键名（而不是 {type,text} 一类自创形状）。</summary>
    private static bool HasRecognizedSectionShape(System.Text.Json.JsonElement sections)
    {
        foreach (var item in sections.EnumerateArray())
        {
            if (item.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
            foreach (var key in SectionKeys)
                if (item.TryGetProperty(key, out _)) return true;
        }
        return false;
    }

    /// <summary>形状不对时回给模型的提示：直接给出正确与错误的对照，模型才能一次改对。</summary>
    private static string WrongShapeText(AgentSkillDefinition skill)
        => $"（未生成文件：技能 {skill.SkillId} 的 sections 形状不对，其中的块不会被识别，生成的文档会没有正文。"
         + "本技能的约定是<b>键名即类型</b>，不要用 {\"type\":\"...\",\"text\":\"...\"} 这种写法。正确示例："
         + "{\"sections\":["
         + "{\"heading\":\"一、平台简介\",\"level\":1},"
         + "{\"paragraph\":\"正文段落…\"},"
         + "{\"bullets\":[\"要点一\",\"要点二\"]},"
         + "{\"numbered\":[\"其一\",\"其二\"]},"
         + "{\"quote\":\"引用/强调文字\"},"
         + "{\"table\":{\"headers\":[\"列1\",\"列2\"],\"rows\":[[\"a\",\"b\"]]}}"
         + "]}。请把正文按这个形状重写后重新调用本技能。）";

    /// <summary>从“上一步输出”（可能含解释性文字）中提取技能可用的纯净输入。</summary>
    internal static string? ExtractCleanValueForSkill(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = System.Text.RegularExpressions.Regex.Match(text, @"https?://[^\s'""<>]+|\b(?:[0-9]{1,3}\.){3}[0-9]{1,3}(?::[0-9]+)?\b");
        if (m.Success) return m.Value.TrimEnd('.', '，', ',', '）', ')', '】', ']');
        return text.Trim().Trim('"', '\'', '“', '”', '，', ',', '。', '.', '：', ':').Trim();
    }

    /// <summary>
    /// 技能是否要求“干净输入”（只给用户本次那句原文，不要群上下文）。
    ///
    /// <para>
    /// 约定写在技能的 <see cref="AgentSkillDefinition.ParametersJson"/> 里：
    /// <c>{"cleanInput":true}</c>（容错：也认 <c>"cleanInput": true</c> / 布尔值写法）。
    /// </para>
    ///
    /// <para>
    /// 为什么需要：编排计划会把整段用户消息上下文（含历史对话与不可信边界包装）投给技能，
    /// 这对“分析类”技能（如排查、综述）是对的，但对“转换 / 排版类”技能是错的 ——
    /// 实测：md_to_docx 在编排路径下把历史聊天记录当正文，导出了 303 段的“文档”。
    /// 技能作者声明这一点后，网关只投用户本次的原文。
    /// </para>
    /// </summary>
    internal static bool WantsCleanInput(AgentSkillDefinition skill)
    {
        var p = skill.ParametersJson;
        if (string.IsNullOrWhiteSpace(p)) return false;
        return System.Text.RegularExpressions.Regex.IsMatch(p, @"""cleanInput""\s*:\s*true",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// 从平台构造的用户消息里取出<b>用户本人本次说的那段话</b>。
    ///
    /// <para>
    /// 平台消息形如：「以下是群最近对话：\nDavid：...\n...\n&lt;untrusted_content&gt;\n用户本次发言\n&lt;/untrusted_content&gt;\n（说明行）」，
    /// 多模态时还可能有附件段。这里剥去前言 / 附件段 / 边界标记，保留边界内的本次发言；
    /// 识不出来时宁可返回整段（交技能自己剥）也不返回空，避免把链路直接断掉。
    /// </para>
    /// </summary>
    internal static string? ExtractLatestUserUtterance(string? platformMessage)
    {
        if (string.IsNullOrWhiteSpace(platformMessage)) return null;
        var s = platformMessage.Replace("\r\n", "\n").Replace("\r", "\n");

        // 取<b>第一个</b>不可信边界块：平台把“用户本次输入”放在最前面，
        // 后续附件段也会各带一个边界块；取最后一个会把附件内容当成用户发言（实测踩到）。
        var open = s.IndexOf("<untrusted_content>", StringComparison.Ordinal);
        if (open >= 0)
        {
            var close = s.IndexOf("</untrusted_content>", open, StringComparison.Ordinal);
            if (close > open)
            {
                var inner = s.Substring(open + "<untrusted_content>".Length, close - open - "<untrusted_content>".Length).Trim();
                if (inner.Length > 0) return StripAttachmentSections(inner);
            }
        }

        // 无边界标记：剥掉可识别的平台前言 / 对话历史行后，取剩余内容。
        // 历史行形如「某人：内容」，连续出现直到第一条不带这种前缀的行（即用户本次发言）。
        var lines = s.Split('\n');
        var kept = new List<string>();
        var skippedPreamble = false;
        foreach (var l in lines)
        {
            var t = l.Trim();
            if (t.StartsWith("以下是群", StringComparison.Ordinal)
                || t.StartsWith("以下是话题", StringComparison.Ordinal)) { skippedPreamble = true; continue; }
            if (t.StartsWith("（以上为外部来源内容", StringComparison.Ordinal)) continue;
            // 平台前言：语言提示、附件说明等纯元信息行，不是用户内容。
            // 实测踩到：规划器把整段 plan.Input 当正文投给排版技能，
            // 而它的开头就是“（请用中文回复，提问者消息以中文为主。）”，
            // 技能拿到后报 JsonReaderException / 导出空壳文档。
            if (IsPlatformPreambleLine(t)) continue;
            // 历史消息行：直到出现非“某人：内容”形式的行为止
            if (skippedPreamble && kept.Count == 0 && LooksLikeHistoryLine(t)) continue;
            kept.Add(l);
        }
        var joined = string.Join("\n", kept).Trim();
        return joined.Length > 0 ? StripAttachmentSections(joined) : null;
    }

    /// <summary>平台自己加的前言行（语言提示 / 附件说明 / 时间戳等），不是用户或上游的业务内容。</summary>
    internal static bool IsPlatformPreambleLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return false;
        var t = line.Trim();
        // 语言提示：由 AgentGateway.DetectReplyLanguageHint 生成
        if (t.StartsWith("（请用中文回复", StringComparison.Ordinal)) return true;
        if (t.StartsWith("（質問は日本語です", StringComparison.Ordinal)) return true;
        if (t.StartsWith("（질문이 한국어입니다", StringComparison.Ordinal)) return true;
        if (t is "（无）" or "(无)") return true;
        return false;
    }

    /// <summary>粗判是否形如「说话人：内容」的对话历史行（说话人短、不含空格与 Markdown 标记）。</summary>
    private static bool LooksLikeHistoryLine(string t)
    {
        var colon = t.IndexOf('：');
        if (colon <= 0 || colon > 20) return false;
        var who = t.Substring(0, colon);
        if (who.Length == 0 || who.Contains(' ') || who.Contains('#')) return false;
        return true;
    }

    /// <summary>去掉平台追加的附件正文段（以【附件 N. ...】开头的块），它们不是“本次要转换的内容”。</summary>
    private static string StripAttachmentSections(string text)
    {
        var idx = text.IndexOf("【附件 ", StringComparison.Ordinal);
        return idx > 0 ? text[..idx].Trim() : text;
    }

    /// <summary>解析路由模型返回的编排计划 JSON（容忍代码块 / 前后缀），非法或空返回 null。</summary>
    internal static List<PlanStep>? ParsePlan(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var cleaned = text.Trim();
        var start = cleaned.IndexOf('{');
        var end = cleaned.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        cleaned = cleaned[start..(end + 1)];
        try
        {
            using var doc = JsonDocument.Parse(cleaned);
            if (!doc.RootElement.TryGetProperty("steps", out var arr) || arr.ValueKind != JsonValueKind.Array) return null;
            var steps = new List<PlanStep>();
            foreach (var e in arr.EnumerateArray())
            {
                var action = e.TryGetProperty("action", out var a) ? a.GetString() : null;
                var target = e.TryGetProperty("target", out var t) ? t.GetString() : null;
                var note = e.TryGetProperty("note", out var n) ? n.GetString() : null;
                if (string.IsNullOrWhiteSpace(action)) continue;
                if (action is "dispatch" or "skill" && string.IsNullOrWhiteSpace(target)) continue;
                steps.Add(new PlanStep(action.Trim(), target?.Trim() ?? "", note));
            }
            return steps;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
