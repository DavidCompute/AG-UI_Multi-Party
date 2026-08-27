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
        var m = System.Text.RegularExpressions.Regex.Match(text, @"https?://[^\s'\""<>]+|\b(?:[0-9]{1,3}\.){3}[0-9]{1,3}(?::[0-9]+)?\b");
        if (m.Success) return m.Value.TrimEnd('.', '，', ',', '）', ')', '】', ']');
        return text.Trim().Trim('"', '\'', '“', '”', '，', ',', '。', '.', '：', ':').Trim();
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
