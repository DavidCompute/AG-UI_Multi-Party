using System.Text;
using System.Text.Json;
using AguiGroupChat.Hub.Models;

namespace AguiGroupChat.Agents;

/// <summary>
/// AG-UI 桥接协议解析（WebSocket 与 HTTP 传输共用）：
/// standard 方言解析 ASSISTANT_MESSAGE / RUN_UPDATED / RUN_COMPLETED / RUN_ERROR；
/// hub 方言解析 TEXT_MESSAGE_*，仅接受「回复自己的消息」（按 replyToMessageId == 自己消息 id 匹配），
/// 过滤自身消息回显、其他成员消息与订阅 ACK / 快照；镜像部署（外部 agent 与本地 agentId 相同）
/// 也能正确区分：自己的回显无 replyToMessageId，外部回复带 replyToMessageId。
/// </summary>
internal static class AguiBridgeProtocol
{
    /// <summary>解析一条外部事件 JSON。返回 null 表示事件无关（忽略）。
    /// <paramref name="selfMessageId"/>（hub 方言）：自己发送消息回显的 messageId，
    /// 由调用方以 ref 维护（跨连接复用同一客户端时保持），用于识别外部智能体回复的回复目标。</summary>
    public static AguiBridgeEvent? Parse(JsonDocument doc, bool hubMode, string agentId, HashSet<string> acceptedReplies, ref string? selfMessageId)
    {
        var type = doc.RootElement.TryGetProperty("type", out var t) ? t.GetString() : null;
        if (string.IsNullOrEmpty(type)) return null;

        if (hubMode)
        {
            return type switch
            {
                "TEXT_MESSAGE_START" => TrackExternalStart(doc, agentId, acceptedReplies, ref selfMessageId),
                "TEXT_MESSAGE_CONTENT" when MatchAccepted(doc, acceptedReplies) =>
                    new AguiBridgeEvent("content", Delta: ReadString(doc.RootElement, "delta")),
                "TEXT_MESSAGE_END" when MatchAccepted(doc, acceptedReplies) => new AguiBridgeEvent("end"),
                // hub 方言的外部智能体工具调用开始（匹配自己的回复消息）
                "TOOL_CALL_START" when MatchAccepted(doc, acceptedReplies) => new AguiBridgeEvent("tool",
                    ToolCallId: ReadString(doc.RootElement, "toolCallId"),
                    ToolName: ReadString(doc.RootElement, "toolCallName") ?? ReadString(doc.RootElement, "toolName")),
                // hub 方言的人机交互：外部 Hub 智能体请求审批（仅触发者可决策）
                "AGENT_INTERACTION_REQUEST" => ParseHubInterrupt(doc),
                "RUN_ERROR" => new AguiBridgeEvent("error",
                    ErrorCode: ReadString(doc.RootElement, "errorCode"),
                    ErrorMessage: ReadString(doc.RootElement, "message")),
                _ => null,
            };
        }

        // standard 方言：兼容 AGUI.Abstractions（AG-UI .NET SDK）的 TEXT_MESSAGE_* / RUN_FINISHED / RUN_ERROR
        // 与原生 AG-UI 事件的 ASSISTANT_MESSAGE / RUN_UPDATED / RUN_COMPLETED 两类服务端。
        // 注意：TEXT_MESSAGE_END 不是运行终止——AGUI.AspNetCore 在消息结束后仍可能下发
        // TOOL_CALL_* 与携带审批中断的 RUN_FINISHED（outcome.type=interrupt），权威终止事件是
        // RUN_FINISHED / RUN_COMPLETED / TURN_COMPLETED；若服务端未发终止事件则随连接关闭自然结束。
        return type switch
        {
            // standard 方言 TEXT_MESSAGE_START：可能携带 attachments（AGUI 2.x），批量转为附件事件
            "TEXT_MESSAGE_START" => ParseStartAttachments(doc),
            "TEXT_MESSAGE_CONTENT" => new AguiBridgeEvent("content", Delta: ReadString(doc.RootElement, "delta")),
            "ASSISTANT_MESSAGE" => new AguiBridgeEvent("content", Delta: ReadAssistantText(doc)),
            "RUN_UPDATED" => new AguiBridgeEvent("content", Delta: ReadNestedString(doc.RootElement, "payload", "delta")),
            // 外部 AG-UI 服务的思考过程（如 OpenCode 的 REASONING_MESSAGE_CONTENT）：以 💭 前缀渲染为过程段
            "REASONING_MESSAGE_CONTENT" => new AguiBridgeEvent("reasoning", Delta: ReadString(doc.RootElement, "delta")),
            // 工具调用开始（TOOL_CALL_START）：广播 TOOL_CALL_START 群事件，前端渲染「🔧 调用工具：xxx」
            "TOOL_CALL_START" => new AguiBridgeEvent("tool",
                ToolCallId: ReadString(doc.RootElement, "toolCallId"),
                ToolName: ReadString(doc.RootElement, "toolCallName") ?? ReadString(doc.RootElement, "toolName")),
            // TOOL_CALL_ARGS：工具调用参数增量（可能是分帧 JSON），供审批中断回填 toolCall.arguments；
            // TOOL_CALL_END 时网关把累积的完整参数广播为 TOOL_CALL_ARGS 群事件（前端展示）
            "TOOL_CALL_ARGS" => new AguiBridgeEvent("tool_args",
                ToolCallId: ReadString(doc.RootElement, "toolCallId"),
                Delta: ReadString(doc.RootElement, "delta")),
            "TOOL_CALL_END" => new AguiBridgeEvent("tool_end",
                ToolCallId: ReadString(doc.RootElement, "toolCallId")),
            // TOOL_CALL_RESULT（OpenCode）：工具执行结果回灌，前端与工具调用行关联展示
            "TOOL_CALL_RESULT" => new AguiBridgeEvent("tool_result",
                ToolCallId: ReadString(doc.RootElement, "toolCallId"),
                Delta: ReadString(doc.RootElement, "content")),
            // 附件事件（AGUI 2.x）：带 url 的附件直接产出；base64 内容流按 STARTED → CONTENT（分帧）→ FINISHED 由客户端累积产出
            "ATTACHMENT_STARTED" => ParseAttachmentStarted(doc),
            "ATTACHMENT_CONTENT" => new AguiBridgeEvent("att_content",
                ToolCallId: ReadString(doc.RootElement, "id"),
                Delta: ReadNestedString(doc.RootElement, "content", "value") ?? ReadString(doc.RootElement, "value")),
            "ATTACHMENT_FINISHED" => new AguiBridgeEvent("att_finish",
                ToolCallId: ReadString(doc.RootElement, "id")),
            // 动作开始（ACTION_STARTED）：以「🔧」过程行渲染（与工具调用同级的过程信息）
            "ACTION_STARTED" => new AguiBridgeEvent("action",
                ToolName: ReadNestedString(doc.RootElement, "payload", "action", "name")
                    ?? ReadString(doc.RootElement, "actionName")
                    ?? ReadString(doc.RootElement, "name")),
            // 任务进度快照（OpenCode ACTIVITY_SNAPSHOT，activityType=opencode.todo）：todo 状态流 → 前端实时进度块
            "ACTIVITY_SNAPSHOT" => ParseActivitySnapshot(doc.RootElement),
            // 独立的审批中断事件（AGUI 2.x INTERRUPT_STARTED）：携带 interrupt 对象或顶层字段
            "INTERRUPT_STARTED" => ParseInterruptStarted(doc),
            "RUN_FINISHED" => ParseRunFinished(doc),
            "RUN_COMPLETED" or "TURN_COMPLETED" => new AguiBridgeEvent("end"),
            "RUN_ERROR" or "TURN_ERROR" => new AguiBridgeEvent("error",
                ErrorCode: ReadNestedString(doc.RootElement, "payload", "error", "code")
                    ?? ReadString(doc.RootElement, "code"),
                ErrorMessage: ReadNestedString(doc.RootElement, "payload", "error", "message")
                    ?? ReadString(doc.RootElement, "message")),
            _ => null,
        };
    }

    /// <summary>ACTIVITY_SNAPSHOT（OpenCode todo 状态流）：content 为 todo 数组 → todo 事件；非数组忽略。</summary>
    private static AguiBridgeEvent? ParseActivitySnapshot(JsonElement el)
    {
        var content = ReadElement(el, "content");
        if (content is not { ValueKind: JsonValueKind.Array }) return null;
        return new AguiBridgeEvent("todo", Delta: content.Value.ToString());
    }

    /// <summary>standard 方言 TEXT_MESSAGE_START 携带的附件数组（AGUI 2.x）：批量转为 attachment 事件；无附件返回 null。</summary>
    private static AguiBridgeEvent? ParseStartAttachments(JsonDocument doc)
    {
        if (!doc.RootElement.TryGetProperty("attachments", out var atts) || atts.ValueKind != JsonValueKind.Array)
            return null;
        var list = new List<BridgeAttachment>();
        foreach (var att in atts.EnumerateArray())
        {
            var b = ReadBridgeAttachment(att, ReadString(att, "name"));
            if (b is not null) list.Add(b);
        }
        return list.Count == 0 ? null : new AguiBridgeEvent("attachment", Attachments: list);
    }

    /// <summary>ATTACHMENT_STARTED：source.url（或 url）可直接作为附件；无 url（base64 内容流）产出 att_start 占位事件，由客户端累积。</summary>
    private static AguiBridgeEvent? ParseAttachmentStarted(JsonDocument doc)
    {
        var att = ParseBridgeAttachment(doc.RootElement);
        if (att is not null) return new AguiBridgeEvent("attachment", Attachments: [att]);
        // base64 内容流：产出 att_start（携带 id / name / contentType），客户端随后累积 ATTACHMENT_CONTENT 增量、FINISHED 时组装
        var id = ReadString(doc.RootElement, "id") ?? ReadString(doc.RootElement, "attachmentId");
        if (string.IsNullOrEmpty(id)) return null;
        return new AguiBridgeEvent("att_start",
            ToolCallId: id,
            AttachmentName: ReadString(doc.RootElement, "name") ?? ReadString(doc.RootElement, "fileName"),
            AttachmentContentType: ReadString(doc.RootElement, "contentType") ?? ReadString(doc.RootElement, "type"));
    }

    /// <summary>从附件元素提取 BridgeAttachment（name/contentType/source.url）；无有效 url 返回 null。</summary>
    private static BridgeAttachment? ParseBridgeAttachment(JsonElement att)
    {
        var name = ReadString(att, "name") ?? ReadString(att, "fileName");
        var contentType = ReadString(att, "contentType") ?? ReadString(att, "type");
        var url = ReadNestedString(att, "source", "url")
            ?? ReadNestedString(att, "source", "uri")
            ?? ReadString(att, "url")
            ?? ReadNestedString(att, "content", "url");
        if (string.IsNullOrWhiteSpace(url)) return null; // base64 内容流暂不支持
        var kind = contentType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true ? "image" : "file";
        if (string.IsNullOrWhiteSpace(name)) name = GuessFileName(url, kind);
        return new BridgeAttachment(name, url, kind);
    }

    /// <summary>从附件数组元素读取（hub 方言 / standard TEXT_MESSAGE_START 共用）。</summary>
    private static BridgeAttachment? ReadBridgeAttachment(JsonElement att, string? fallbackName)
    {
        var b = ParseBridgeAttachment(att);
        if (b is null) return null;
        if (string.IsNullOrWhiteSpace(b.Name) && !string.IsNullOrWhiteSpace(fallbackName))
            return b with { Name = fallbackName };
        return b;
    }

    /// <summary>由 URL 推断文件名（附件未命名时回退）。</summary>
    private static string GuessFileName(string url, string kind)
    {
        try
        {
            var path = new Uri(url).AbsolutePath;
            var file = Path.GetFileName(path);
            if (!string.IsNullOrWhiteSpace(file)) return file;
        }
        catch (Exception) { /* 非法 URL：用类别名 */ }
        return kind == "image" ? "image.png" : "attachment.bin";
    }

    /// <summary>独立 INTERRUPT_STARTED 事件（AGUI 2.x）：interrupt 对象在 payload.interrupt 或顶层，复用 ParseInterrupt 提取。</summary>
    private static AguiBridgeEvent? ParseInterruptStarted(JsonDocument doc)
    {
        var el = doc.RootElement;
        if (el.TryGetProperty("payload", out var payload) && payload.TryGetProperty("interrupt", out var inner))
            el = inner;
        else if (el.TryGetProperty("interrupt", out var inner2))
            el = inner2;
        if (el.ValueKind != JsonValueKind.Object) return null;
        return ParseInterrupt(el);
    }

    /// <summary>standard 方言 RUN_FINISHED：outcome.type=interrupt 时产出人机交互中断事件（AG-UI 协议）；否则视为运行结束。</summary>
    private static AguiBridgeEvent? ParseRunFinished(JsonDocument doc)
    {
        var outcome = ReadObject(doc.RootElement, "outcome");
        var outcomeType = ReadString(outcome, "type") ?? ReadString(outcome, "outcomeType");
        if (string.Equals(outcomeType, "interrupt", StringComparison.OrdinalIgnoreCase))
        {
            if (outcome.TryGetProperty("interrupts", out var interrupts)
                && interrupts.ValueKind == JsonValueKind.Array
                && interrupts.GetArrayLength() > 0)
            {
                return ParseInterrupt(interrupts[0]);
            }
        }
        return new AguiBridgeEvent("end");
    }

    /// <summary>hub 方言 AGENT_INTERACTION_REQUEST → 中断事件（字段直接映射；kind=input/choice/multi_choice 时附带输入字段与选项）。</summary>
    private static AguiBridgeEvent? ParseHubInterrupt(JsonDocument doc)
    {
        var (kind, inputField, options) = ResolveInterruptKind(doc.RootElement);
        var args = ReadElement(doc.RootElement, "toolArguments");
        return new AguiBridgeEvent("interrupt",
            InterruptId: ReadString(doc.RootElement, "interruptId"),
            ToolCallId: ReadString(doc.RootElement, "toolCallId"),
            ToolName: ReadString(doc.RootElement, "toolName"),
            ToolArguments: args,
            InterruptMessage: ResolveInterruptMessage(ReadString(doc.RootElement, "message"), args, doc.RootElement),
            InterruptKind: kind,
            InputField: inputField,
            InterruptOptions: options,
            ResponseSchema: ReadElement(doc.RootElement, "responseSchema"),
            Questions: ExtractQuestions(doc.RootElement));
    }

    /// <summary>中断问题文本兜底：顶层 message 为空/空白时（外部 question 工具常只把问题放在工具参数里），
    /// 按优先级从 metadata.questions（结构化问题数组）→ 工具参数提取——question 字段优先，其次常见问题字段；
    /// 兼容 arguments 为对象或 JSON 字符串两种形态（AGUI.AspNetCore 的 metadata.function_call.arguments 为序列化字符串）。
    /// 均无则返回 null。</summary>
    private static string? ResolveInterruptMessage(string? message, JsonElement? toolArguments, JsonElement interrupt)
    {
        // OpenCode 等外部服务的 message 常为占位文案（"OpenCode requires additional input"），
        // 真实问题在 metadata.questions——存在结构化问题时优先展示问题（message 仅为占位时覆盖）
        var questions = ExtractQuestionsText(interrupt);
        if (questions is not null && IsPlaceholderMessage(message)) return questions;
        if (!string.IsNullOrWhiteSpace(message)) return message;
        if (questions is not null) return questions;
        var candidates = new List<JsonElement>();
        if (toolArguments is { ValueKind: JsonValueKind.Object } ta) candidates.Add(ta);
        foreach (var path in new[]
                 {
                     new[] { "metadata", "function_call", "arguments" },
                     new[] { "metadata", "agent_framework", "function_call", "arguments" },
                 })
        {
            var nested = ReadNestedElement(interrupt, path);
            if (nested is { ValueKind: JsonValueKind.Object }) candidates.Add(nested.Value);
            else if (ReadNestedString(interrupt, path) is { } raw)
            {
                try
                {
                    using var doc = JsonDocument.Parse(raw);
                    if (doc.RootElement.ValueKind == JsonValueKind.Object) candidates.Add(doc.RootElement.Clone());
                }
                catch (JsonException) { /* 非 JSON 字符串 → 跳过 */ }
            }
        }
        foreach (var c in candidates)
        {
            foreach (var key in new[] { "question", "prompt", "text", "message", "content", "description", "input" })
            {
                if (ReadString(c, key) is { } q && q.Trim().Length > 0) return q;
            }
        }
        return null;
    }

    /// <summary>外部 question 工具的 message 占位判定：空 / 很短且含 additional input 等特征文案时，
    /// 视为占位（真实问题在 metadata.questions），允许被结构化问题文本覆盖。</summary>
    private static bool IsPlaceholderMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return true;
        var t = message.Trim();
        return t.Length <= 40 && (t.Contains("additional input", StringComparison.OrdinalIgnoreCase)
            || t.Contains("requires input", StringComparison.OrdinalIgnoreCase)
            || t.Contains("needs input", StringComparison.OrdinalIgnoreCase)
            || t.Contains("waiting for input", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>从 metadata.questions（外部 question 工具的结构化问题数组，如 OpenCode）拼接问题文本：
    /// 每个问题一行（含 header），选项以 · 前缀列在问题下方，供前端直接展示。无 questions 返回 null。</summary>
    private static string? ExtractQuestionsText(JsonElement interrupt)
    {
        var questions = ExtractQuestions(interrupt);
        if (questions is null) return null;
        var sb = new StringBuilder();
        var idx = 1;
        foreach (var q in questions)
        {
            if (sb.Length > 0) sb.AppendLine();
            sb.Append(idx++).Append(". ");
            if (q.Header is { } header && header.Trim().Length > 0)
                sb.Append('[').Append(header).Append("] ");
            sb.Append(q.Question);
            if (q.Options is { Count: > 0 })
            {
                foreach (var o in q.Options)
                    sb.AppendLine().Append("   · ").Append(o.Label);
            }
        }
        return sb.Length > 0 ? sb.ToString() : null;
    }

    /// <summary>解析 metadata.questions（OpenCode 结构化问题数组）为 BridgeQuestion 列表；无则返回 null。</summary>
    private static IReadOnlyList<BridgeQuestion>? ExtractQuestions(JsonElement interrupt)
    {
        if (!interrupt.TryGetProperty("metadata", out var metadata) || metadata.ValueKind != JsonValueKind.Object
            || !metadata.TryGetProperty("questions", out var questions) || questions.ValueKind != JsonValueKind.Array)
            return null;
        var list = new List<BridgeQuestion>();
        foreach (var q in questions.EnumerateArray())
        {
            var question = ReadString(q, "question");
            if (string.IsNullOrWhiteSpace(question)) continue;
            List<BridgeQuestionOption>? opts = null;
            if (q.TryGetProperty("options", out var optsEl) && optsEl.ValueKind == JsonValueKind.Array)
            {
                opts = [];
                foreach (var o in optsEl.EnumerateArray())
                {
                    if (ReadString(o, "label") is { } label && label.Trim().Length > 0)
                        opts.Add(new BridgeQuestionOption(label, ReadString(o, "description")));
                }
            }
            var multiple = q.TryGetProperty("multiple", out var multiEl)
                && multiEl.ValueKind == JsonValueKind.True;
            list.Add(new BridgeQuestion(ReadString(q, "header"), question, opts, multiple));
        }
        return list.Count > 0 ? list : null;
    }

    /// <summary>从单个 AG-UI interrupt 对象提取中断信息（AGUIInterrupt / 原生 AG-UI 两种形态）。</summary>
    private static AguiBridgeEvent ParseInterrupt(JsonElement interrupt)
    {
        var toolCallId = ReadString(interrupt, "toolCallId");
        var message = ReadString(interrupt, "message");
        var toolName = ReadNestedString(interrupt, "metadata", "function_call", "name")
            ?? ReadNestedString(interrupt, "metadata", "agent_framework", "function_call", "name")
            ?? ReadString(interrupt, "toolName")
            ?? ExtractToolNameFromMessage(message) // 无 metadata 时（AGUI.AspNetCore）从 message 提取
            ?? toolCallId
            ?? "unknown";
        var args = ReadElement(interrupt, "toolArguments")
            ?? ReadNestedElement(interrupt, "metadata", "function_call", "arguments")
            ?? ReadNestedElement(interrupt, "metadata", "agent_framework", "function_call", "arguments");
        var (kind, inputField, options) = ResolveInterruptKind(interrupt);
        return new AguiBridgeEvent("interrupt",
            InterruptId: ReadString(interrupt, "id") ?? ReadString(interrupt, "interruptId"),
            ToolCallId: toolCallId,
            ToolName: toolName,
            ToolArguments: args,
            InterruptMessage: ResolveInterruptMessage(message, args, interrupt)
                ?? $"智能体请求你确认：是否执行操作「{toolName}」？",
            InterruptKind: kind,
            InputField: inputField,
            InterruptOptions: options,
            ResponseSchema: ReadElement(interrupt, "responseSchema"),
            Questions: ExtractQuestions(interrupt));
    }

    /// <summary>按中断的 responseSchema（JSON Schema）判定交互类型与可选项：
    /// 仅 approved(boolean) → 工具审批（approval）；string + enum → 单选（choice）；array of enum → 多选（multi_choice）；
    /// 纯 string / 其他标量 → 请求输入（input）。返回 (kind, 输入字段名, 可选项)。</summary>
    private static (string Kind, string? InputField, IReadOnlyList<string>? Options) ResolveInterruptKind(JsonElement interrupt)
    {
        if (!interrupt.TryGetProperty("responseSchema", out var schema) || schema.ValueKind != JsonValueKind.Object)
            return ("approval", null, null);

        var schemaType = ReadString(schema, "type");
        if (string.Equals(schemaType, "string", StringComparison.OrdinalIgnoreCase))
        {
            var opts = ReadEnumOptions(schema);
            return opts.Count > 0 ? ("choice", "answer", opts) : ("input", "answer", null);
        }
        if (string.Equals(schemaType, "array", StringComparison.OrdinalIgnoreCase))
        {
            var opts = ReadEnumOptions(ReadObject(schema, "items"));
            return ("multi_choice", "answer", opts.Count > 0 ? opts : null);
        }
        if (string.Equals(schemaType, "object", StringComparison.OrdinalIgnoreCase)
            && schema.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in props.EnumerateObject())
            {
                var isBoolean = p.Value.ValueKind == JsonValueKind.Object
                    && string.Equals(ReadString(p.Value, "type"), "boolean", StringComparison.OrdinalIgnoreCase);
                if (isBoolean && p.Name.Equals("approved", StringComparison.OrdinalIgnoreCase)) continue; // approved 布尔 → 审批
                var pType = p.Value.ValueKind == JsonValueKind.Object ? ReadString(p.Value, "type") : null;
                var opts = ReadEnumOptions(p.Value);
                if (string.Equals(pType, "string", StringComparison.OrdinalIgnoreCase))
                    return opts.Count > 0 ? ("choice", p.Name, opts) : ("input", p.Name, null);
                if (string.Equals(pType, "array", StringComparison.OrdinalIgnoreCase))
                    return ("multi_choice", p.Name, opts.Count > 0 ? opts : null);
                return ("input", p.Name, null); // number / boolean / 其他 → 按文本输入处理
            }
        }
        return ("approval", null, null);
    }

    /// <summary>恢复时回传的用户输入值：单选提交纯字符串；多选提交 JSON 数组（["a","b"]）；
    /// 数字 / 布尔 / 对象按 JSON 解析为原生类型；纯文本按字符串。</summary>
    public static object? ResumeInputValue(string? input)
    {
        if (string.IsNullOrEmpty(input)) return null;
        try
        {
            using var doc = JsonDocument.Parse(input);
            var v = doc.RootElement;
            if (v.ValueKind == JsonValueKind.String) return v.GetString();
            if (v.ValueKind is JsonValueKind.Array or JsonValueKind.Object or JsonValueKind.Number
                or JsonValueKind.True or JsonValueKind.False)
                return v.Clone();
        }
        catch (JsonException) { /* 非 JSON → 按字符串 */ }
        return input;
    }

    /// <summary>读取 schema 的 enum 字符串选项列表（无 enum 返回空列表）。</summary>
    private static List<string> ReadEnumOptions(JsonElement schemaEl)
    {
        var list = new List<string>();
        if (schemaEl.ValueKind == JsonValueKind.Object && schemaEl.TryGetProperty("enum", out var en)
            && en.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in en.EnumerateArray())
            {
                if (e.ValueKind == JsonValueKind.String) list.Add(e.GetString()!);
                else if (e.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                    list.Add(e.ToString());
            }
        }
        return list;
    }

    /// <summary>从「Approval required for tool call: send_email」类消息中提取工具名。</summary>
    private static string? ExtractToolNameFromMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return null;
        var marker = "tool call:";
        var idx = message.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;
        return message[(idx + marker.Length)..].Trim().Trim(' ', '.', '?', '！', '？');
    }

    /// <summary>单次工具调用的累积参数上限（100KB）：防外部服务分帧下发巨型参数撑爆内存。</summary>
    private const int MaxToolArgsChars = 100 * 1024;

    /// <summary>累积 TOOL_CALL_ARGS 增量（toolCallId → JSON 参数字符串，可能分帧到达）。
    /// 超过 <see cref="MaxToolArgsChars"/> 后丢弃后续增量（保留已累积部分，审批回填解析失败则按无参数处理）。</summary>
    public static void TrackToolArgs(string? toolCallId, string? delta, Dictionary<string, string> argsByCallId)
    {
        if (string.IsNullOrEmpty(toolCallId) || string.IsNullOrEmpty(delta)) return;
        argsByCallId.TryGetValue(toolCallId, out var prev);
        var next = prev + delta;
        if (next.Length > MaxToolArgsChars) return; // 超限丢弃：防外部服务下发巨型参数撑爆内存
        argsByCallId[toolCallId] = next;
    }

    /// <summary>记录 TOOL_CALL_START 的工具名（toolCallId → name），供审批中断回填（外部服务中断对象常缺 toolName）。</summary>
    public static void TrackToolName(string? toolCallId, string? toolName, Dictionary<string, string> namesByCallId)
    {
        if (string.IsNullOrEmpty(toolCallId) || string.IsNullOrEmpty(toolName)) return;
        namesByCallId[toolCallId] = toolName;
    }

    /// <summary>为审批中断事件回填工具名：外部服务（如 OpenCode）的中断对象通常只有 toolCallId、无 toolName，
    /// 用同一 run 内 TOOL_CALL_START 记录的工具名补全，避免审批卡片显示 callId 占位。</summary>
    public static AguiBridgeEvent EnrichInterruptToolName(AguiBridgeEvent evt, Dictionary<string, string> namesByCallId)
    {
        if (evt.ToolCallId is null) return evt;
        // 已解析出真实工具名（非 callId 兜底占位）则不动；callId 占位（ParseInterrupt 兜底）时用 TOOL_CALL_START 记录的真实名回填
        if (evt.ToolName is not null && !string.Equals(evt.ToolName, evt.ToolCallId, StringComparison.Ordinal)) return evt;
        if (!namesByCallId.TryGetValue(evt.ToolCallId, out var name)) return evt;
        return evt with { ToolName = name };
    }

    /// <summary>为审批中断事件回填 TOOL_CALL_ARGS 累积的工具参数：外部服务（如 AGUI.AspNetCore）的
    /// 中断对象常无 arguments 字段，而 AG-UI 恢复协议要求 toolCall.arguments 才能执行工具。</summary>
    public static AguiBridgeEvent EnrichInterruptArgs(AguiBridgeEvent evt, Dictionary<string, string> argsByCallId)
    {
        if (evt.ToolArguments is not null || evt.ToolCallId is null) return evt;
        if (!argsByCallId.TryGetValue(evt.ToolCallId, out var raw)) return evt;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
                return evt with { ToolArguments = doc.RootElement.Clone() };
        }
        catch (JsonException) { }
        return evt;
    }

    /// <summary>为 TOOL_CALL_END 事件回填完整参数（TOOL_CALL_ARGS 分帧累积）：前端展示工具调用参数详情。</summary>
    public static AguiBridgeEvent EnrichToolEndArgs(AguiBridgeEvent evt, Dictionary<string, string> argsByCallId)
    {
        if (evt.ToolArguments is not null || evt.ToolCallId is null) return evt;
        if (!argsByCallId.TryGetValue(evt.ToolCallId, out var raw) || string.IsNullOrWhiteSpace(raw)) return evt;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
                return evt with { ToolArguments = doc.RootElement.Clone() };
        }
        catch (JsonException) { /* 非对象参数（如裸字符串）：保持 ToolArguments 为空，前端只显示工具名 */ }
        return evt;
    }

    /// <summary>hub 方言：按「回复目标」识别外部 agent 的回复——
    /// 自己的回显（senderId==自己且无 replyToMessageId）只捕获 messageId，不回灌；
    /// 回复目标 == 自己消息的 START 才是外部回复（镜像部署下外部 agent 与本地 agentId 相同也能区分）；
    /// 该 START 携带的附件（本协议 TEXT_MESSAGE_START.attachments）转为批量 attachment 事件。
    /// 其余（其他成员消息 / selfMessageId 未知）一律忽略，避免误收外部群其他消息污染回复。</summary>
    private static AguiBridgeEvent? TrackExternalStart(JsonDocument doc, string agentId, HashSet<string> acceptedReplies, ref string? selfMessageId)
    {
        var senderId = ReadString(doc.RootElement, "senderId");
        var messageId = ReadString(doc.RootElement, "messageId");
        var replyToMessageId = ReadString(doc.RootElement, "replyToMessageId");
        if (string.IsNullOrEmpty(messageId)) return null;

        // 自己发送消息的回显（无引用回复目标）：捕获 messageId 供外部回复匹配，不回灌
        if (string.Equals(senderId, agentId, StringComparison.Ordinal) && string.IsNullOrEmpty(replyToMessageId))
        {
            if (selfMessageId is null) selfMessageId = messageId;
            return null;
        }

        // 外部智能体的回复：回复目标 == 自己发送的消息（selfMessageId 已知）→ 登记该消息
        if (selfMessageId is not null && string.Equals(replyToMessageId, selfMessageId, StringComparison.Ordinal))
        {
            acceptedReplies.Add(messageId);
            // 群内 START 已由本地主动发布，无需回灌；但若携带附件则回灌附件事件（消息结束前随附件一并落库）
            if (doc.RootElement.TryGetProperty("attachments", out var atts) && atts.ValueKind == JsonValueKind.Array)
            {
                var list = new List<BridgeAttachment>();
                foreach (var att in atts.EnumerateArray())
                {
                    var b = ReadBridgeAttachment(att, ReadString(att, "name"));
                    if (b is not null) list.Add(b);
                }
                if (list.Count > 0) return new AguiBridgeEvent("attachment", Attachments: list);
            }
            return null;
        }

        // 其余情况（其他成员消息 / selfMessageId 未知）：忽略，不加 acceptedReplies
        return null;
    }

    private static bool MatchAccepted(JsonDocument doc, HashSet<string> acceptedReplies)
    {
        var messageId = ReadString(doc.RootElement, "messageId");
        return messageId is not null && acceptedReplies.Contains(messageId);
    }

    private static JsonElement ReadObject(JsonElement el, string key)
        => el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Object ? v : default;

    private static JsonElement? ReadElement(JsonElement el, string key)
        => el.TryGetProperty(key, out var v) && v.ValueKind is JsonValueKind.Object or JsonValueKind.Array ? v.Clone() : null;

    private static JsonElement? ReadNestedElement(JsonElement el, params string[] path)
    {
        foreach (var key in path)
        {
            if (!el.TryGetProperty(key, out el)) return null;
        }
        return el.ValueKind is JsonValueKind.Object or JsonValueKind.Array ? el.Clone() : null;
    }

    private static string? ReadAssistantText(JsonDocument doc)
    {
        if (!doc.RootElement.TryGetProperty("payload", out var payload)
            || !payload.TryGetProperty("content", out var content))
            return null;
        // 畸形消息：content 非数组时 EnumerateArray 会抛 InvalidOperationException，先校验再枚举
        if (content.ValueKind != JsonValueKind.Array) return null;
        var sb = new StringBuilder();
        foreach (var part in content.EnumerateArray())
        {
            if (part.TryGetProperty("type", out var pt) && pt.GetString() == "text"
                && part.TryGetProperty("text", out var text))
                sb.Append(text.GetString());
        }
        return sb.ToString();
    }

    /// <summary>按 responseSchema 规范化输入 payload：多选（array）逗号字符串拆数组；integer/number 字符串转数值；boolean 字符串转布尔。
    /// 兼容两种 schema 形态：多字段对象（properties 逐字段）与顶层单字段（choice/multi_choice，以 payload 首个字段套用顶层 schema 类型）。
    /// 前端以字符串统一提交，外部服务按 schema 期望的类型接收。</summary>
    public static JsonElement? NormalizeInputPayload(JsonElement? schema, JsonElement? payload)
    {
        if (payload is not { ValueKind: JsonValueKind.Object } p) return payload;

        // 顶层单字段 schema（无 properties）：以 payload 首个字段名套用顶层 schema 类型；多字段则按 properties 逐字段
        var topDef = default(JsonElement);
        var useTopDef = false;
        if (schema is { ValueKind: JsonValueKind.Object } s)
        {
            if (!(s.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object))
            {
                topDef = s;
                useTopDef = true;
            }
        }

        var result = new Dictionary<string, object?>();
        foreach (var field in p.EnumerateObject())
        {
            JsonElement def;
            if (useTopDef)
            {
                def = topDef;
            }
            else if (schema is { ValueKind: JsonValueKind.Object } s2
                     && s2.TryGetProperty("properties", out var props2) && props2.ValueKind == JsonValueKind.Object
                     && props2.TryGetProperty(field.Name, out var d))
            {
                def = d;
            }
            else
            {
                def = default;
            }

            var type = def.ValueKind == JsonValueKind.Object ? ReadString(def, "type") : null;
            if (type == "array" && field.Value.ValueKind == JsonValueKind.String)
            {
                result[field.Name] = field.Value.GetString()!
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToList();
            }
            else if (type == "boolean" && field.Value.ValueKind == JsonValueKind.String
                     && bool.TryParse(field.Value.GetString(), out var b))
            {
                result[field.Name] = b;
            }
            else if (type == "integer" && field.Value.ValueKind == JsonValueKind.String
                     && long.TryParse(field.Value.GetString(), out var l))
            {
                result[field.Name] = l;
            }
            else if (type == "number" && field.Value.ValueKind == JsonValueKind.String
                     && double.TryParse(field.Value.GetString(), System.Globalization.NumberStyles.Float,
                         System.Globalization.CultureInfo.InvariantCulture, out var dbl))
            {
                result[field.Name] = dbl;
            }
            else
            {
                result[field.Name] = field.Value.Clone();
            }
        }
        return JsonSerializer.SerializeToElement(result);
    }

    private static string? ReadString(JsonElement el, string key)
        => el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    internal static string? ReadNestedString(JsonElement el, params string[] path)
    {
        foreach (var key in path)
        {
            if (!el.TryGetProperty(key, out el)) return null;
        }
        return el.ValueKind == JsonValueKind.String ? el.GetString() : null;
    }
}
