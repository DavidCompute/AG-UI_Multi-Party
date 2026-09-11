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
            // 历史消息行：直到出现非“某人：内容”形式的行为止
            if (skippedPreamble && kept.Count == 0 && LooksLikeHistoryLine(t)) continue;
            kept.Add(l);
        }
        var joined = string.Join("\n", kept).Trim();
        return joined.Length > 0 ? StripAttachmentSections(joined) : null;
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
