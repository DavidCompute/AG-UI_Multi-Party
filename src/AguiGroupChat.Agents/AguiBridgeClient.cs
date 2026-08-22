using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using AguiGroupChat.Hub.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace AguiGroupChat.Agents;

/// <summary>桥接客户端接收到的外部 AG-UI 事件。</summary>
/// <param name="Type">content（文本增量）/ reasoning（思考过程）/ tool（工具调用开始）/ action（动作开始）/
/// attachment（附件，见 <paramref name="Attachments"/>）/ end（运行结束）/ error（运行错误）/ interrupt（人机交互中断）。</param>
/// <param name="Approval">standard+HTTP（AGUIChatClient）路径的审批请求对象，供 CreateResponse 恢复。</param>
/// <param name="Attachments">attachment 事件携带的外部附件（可能是多条，如 TEXT_MESSAGE_START 的附件数组）。</param>
public sealed record AguiBridgeEvent(
    string Type,
    string? Delta = null,
    string? ErrorCode = null,
    string? ErrorMessage = null,
    string? InterruptId = null,
    string? ToolCallId = null,
    string? ToolName = null,
    JsonElement? ToolArguments = null,
    string? InterruptMessage = null,
    ToolApprovalRequestContent? Approval = null,
    IReadOnlyList<BridgeAttachment>? Attachments = null,
    string? AttachmentName = null,
    string? AttachmentContentType = null,
    string? InterruptKind = null,
    string? InputField = null,
    IReadOnlyList<string>? InterruptOptions = null,
    JsonElement? ResponseSchema = null,
    IReadOnlyList<BridgeQuestion>? Questions = null);

/// <summary>外部 AG-UI 服务下发的附件（ATTACHMENT_* 事件 / TEXT_MESSAGE_START.attachments / ASSISTANT_MESSAGE 图片）。</summary>
/// <param name="Name">文件名（可能为空，回退 URL 文件名）。</param>
/// <param name="Url">附件地址（外部 http(s) 或 data: 等；鉴权由外部服务侧处理，前端按 scheme 白名单校验）。</param>
/// <param name="Kind">image（图片预览）/ file（文件卡片）。</param>
public sealed record BridgeAttachment(string Name, string Url, string Kind);

/// <summary>base64 内容流附件累积：ATTACHMENT_STARTED（无 url）→ ATTACHMENT_CONTENT（分帧）→ ATTACHMENT_FINISHED 组装 data URL 附件。
/// 带体积上限保护（超限丢弃，防止外部服务下发巨型附件撑爆内存）。</summary>
internal sealed class PendingBridgeAttachment
{
    private const long MaxBase64Length = 20 * 1024 * 1024; // 累积上限 20MB base64（约 15MB 原始字节）
    private readonly StringBuilder _b64 = new();
    private bool _overflow;

    public PendingBridgeAttachment(string? name, string? contentType)
    {
        Name = name;
        ContentType = contentType;
    }

    public string? Name { get; }
    public string? ContentType { get; }

    public void Append(string? delta)
    {
        if (_overflow || string.IsNullOrEmpty(delta)) return;
        if (_b64.Length + delta.Length > MaxBase64Length) { _b64.Clear(); _overflow = true; return; }
        _b64.Append(delta);
    }

    public AguiBridgeEvent? ToAttachmentEvent()
    {
        if (_overflow || _b64.Length == 0) return null;
        var kind = ContentType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true ? "image" : "file";
        var url = "data:" + (string.IsNullOrWhiteSpace(ContentType) ? "application/octet-stream" : ContentType) + ";base64," + _b64;
        var name = string.IsNullOrWhiteSpace(Name) ? (kind == "image" ? "image.png" : "attachment.bin") : Name;
        return new AguiBridgeEvent("attachment", Attachments: [new BridgeAttachment(name, url, kind)]);
    }
}

/// <summary>桥接客户端共享的 base64 附件事件累积器（WS 与 HTTP standard 客户端各持一个实例）。</summary>
internal sealed class BridgeAttachmentAccumulator
{
    private readonly Dictionary<string, PendingBridgeAttachment> _pending = new(StringComparer.Ordinal);

    /// <summary>追踪 att_start / att_content / att_finish 并返回产出事件：累积中返回 null，FINISHED 返回 attachment；其余事件原样返回。</summary>
    public AguiBridgeEvent? Track(AguiBridgeEvent evt)
    {
        switch (evt.Type)
        {
            case "att_start" when evt.ToolCallId is not null:
                _pending[evt.ToolCallId] = new PendingBridgeAttachment(evt.AttachmentName, evt.AttachmentContentType);
                return null;
            case "att_content" when evt.ToolCallId is not null && _pending.TryGetValue(evt.ToolCallId, out var p):
                p.Append(evt.Delta);
                return null;
            case "att_finish" when evt.ToolCallId is not null && _pending.Remove(evt.ToolCallId, out var done):
                return done.ToAttachmentEvent();
            default:
                return evt;
        }
    }

    public void Clear() => _pending.Clear();
}

/// <summary>
/// AG-UI 桥接 WebSocket 客户端：把群聊触发消息以 AG-UI 协议转发给外部 AG-UI 服务，
/// 并把外部服务的流式回复转成统一事件流供回灌群聊。
/// 支持两种方言：
///   standard —— 标准 AG-UI 事件（上行 USER_MESSAGE；下行 ASSISTANT_MESSAGE / RUN_UPDATED / RUN_COMPLETED / RUN_ERROR）；
///   hub      —— 本项目群聊扩展协议（上行 GROUP_MESSAGE_SEND；下行 TEXT_MESSAGE_START / CONTENT / END / RUN_ERROR）。
/// </summary>
public sealed class AguiBridgeClient : IAguiBridgeClient
{
    private readonly string _endpoint;
    private readonly string _mode;
    private readonly string? _token;
    private readonly string _agentId;
    private readonly int _connectTimeoutSeconds;
    private readonly ILogger _logger;
    private readonly HashSet<string> _acceptedReplies = new(StringComparer.Ordinal); // hub 方言：外部回复消息集合
    private readonly Dictionary<string, string> _toolArgs = new(StringComparer.Ordinal); // TOOL_CALL_ARGS 累积参数
    private readonly Dictionary<string, string> _toolNames = new(StringComparer.Ordinal); // TOOL_CALL_START 工具名（审批回填用）
    private readonly BridgeAttachmentAccumulator _atts = new(); // base64 内容流附件累积（STARTED→CONTENT→FINISHED）
    private const int MaxFrameChars = 2 * 1024 * 1024; // 单帧（多 WS 分片累计）上限 2MB，超限断开（防外部服务下发巨型帧撑爆内存）
    private ClientWebSocket? _ws;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);
    private string? _runId; // 首发 RunAgentInput 的 runId：resume 必须复用同一 runId
    private string? _selfMessageId; // hub 方言：自己发送消息回显的 messageId（从回显 START 捕获，供识别外部回复）

    public AguiBridgeClient(string endpoint, string mode, string? token, string agentId, ILogger logger, int connectTimeoutSeconds = 10)
    {
        _endpoint = endpoint;
        _mode = mode;
        _token = token;
        _agentId = agentId;
        _logger = logger;
        _connectTimeoutSeconds = connectTimeoutSeconds;
    }

    public bool IsHubMode => string.Equals(_mode, "hub", StringComparison.OrdinalIgnoreCase);

    /// <summary>建立连接。hub 方言以 agentId 作为身份参数（memberId）。</summary>
    public async Task ConnectAsync(string agentId, CancellationToken ct)
    {
        var uri = IsHubMode
            ? new Uri(_endpoint + (_endpoint.Contains('?') ? "&" : "?") + "memberId=" + Uri.EscapeDataString(agentId))
            : new Uri(_endpoint);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(_connectTimeoutSeconds));
        var ws = new ClientWebSocket();
        if (!string.IsNullOrEmpty(_token))
            ws.Options.SetRequestHeader("Authorization", "Bearer " + _token);
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
        try
        {
            await ws.ConnectAsync(uri, cts.Token);
        }
        catch (Exception ex)
        {
            ws.Dispose();
            throw new InvalidOperationException($"AG-UI 桥接连接失败：{uri}", ex);
        }
        _ws = ws;
        _logger.LogInformation("AG-UI 桥接已连接（{Mode}）：{Endpoint}", _mode, _endpoint);
    }

    /// <summary>发送用户消息。standard 方言发送 USER_MESSAGE（runId 由调用方传入，与恢复时一致）；hub 方言先订阅群再发送 GROUP_MESSAGE_SEND。</summary>
    public async Task SendUserMessageAsync(string messageId, string threadId, string runId, string content, string groupId, string agentId, CancellationToken ct)
    {
        if (IsHubMode)
        {
            // 先订阅群：外部 Hub 只向已订阅连接推送事件
            await SendAsync(new
            {
                type = "GROUP_SUBSCRIBE",
                groupIds = new[] { groupId },
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            }, ct);
            await SendAsync(new
            {
                type = "GROUP_MESSAGE_SEND",
                groupId,
                userId = agentId,
                content,
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            }, ct);
            return;
        }

        await SendAsync(new
        {
            // AGUI.Abstractions RunAgentInput 结构（AG-UI .NET SDK）：
            //   messages: 消息列表（user 文本）；context: AGUIContext 列表（description/value）
            threadId,
            runId,
            messages = new[]
            {
                new { id = messageId, role = "user", content },
            },
            context = new[]
            {
                new { description = "time", value = DateTimeOffset.UtcNow.ToString("O") },
            },
        }, ct);
    }

    /// <summary>接收外部事件流（阻塞直到连接关闭或取消）。</summary>
    public async IAsyncEnumerable<AguiBridgeEvent> ReceiveAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var buffer = new byte[32 * 1024];
        while (_ws is { State: WebSocketState.Open } && !ct.IsCancellationRequested)
        {
            var sb = new StringBuilder();
            AguiBridgeEvent? disconnectError = null; // 连接异常断开事件（迭代器限制：不能在 catch 内 yield，先暂存）
            var gotCloseFrame = false;
            try
            {
                WebSocketReceiveResult result;
                do
                {
                    result = await _ws.ReceiveAsync(buffer, ct);
                    if (result.MessageType == WebSocketMessageType.Close) { gotCloseFrame = true; break; }
                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                        // 多分片累计上限：防外部服务下发巨型帧撑爆内存（超限抛异常 → 网关按运行错误处理并断开）
                        if (sb.Length > MaxFrameChars)
                            throw new InvalidOperationException($"AG-UI 桥接单帧消息超过 {MaxFrameChars / 1024 / 1024}MB 上限，已断开");
                    }
                }
                while (!result.EndOfMessage);
            }
            catch (OperationCanceledException) { yield break; }
            catch (WebSocketException)
            {
                // 外部服务异常断开（非正常 Close 帧）：不再静默结束——产出断开错误事件，网关按错误处理回复不完整
                disconnectError = new AguiBridgeEvent("error", ErrorCode: "AGENT_BRIDGE_DISCONNECTED", ErrorMessage: "外部 AG-UI 服务连接中断");
            }
            if (disconnectError is not null)
            {
                yield return disconnectError;
                yield break;
            }
            if (gotCloseFrame)
            {
                // 正常 Close 帧：静默结束（不产生 error）
                try { await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "closed", ct); }
                catch { }
                yield break;
            }
            if (sb.Length == 0) continue;

            AguiBridgeEvent? evt = null;
            try
            {
                using var doc = JsonDocument.Parse(sb.ToString());
                evt = AguiBridgeProtocol.Parse(doc, IsHubMode, _agentId, _acceptedReplies, ref _selfMessageId);
                if (evt is not null && evt.Type == "tool_args")
                    AguiBridgeProtocol.TrackToolArgs(evt.ToolCallId, evt.Delta, _toolArgs);
                else if (evt is not null && evt.Type == "tool")
                    AguiBridgeProtocol.TrackToolName(evt.ToolCallId, evt.ToolName, _toolNames);
                else if (evt is not null && evt.Type == "tool_end")
                    evt = AguiBridgeProtocol.EnrichToolEndArgs(evt, _toolArgs); // 回填分帧累积的完整参数（前端展示）
                else if (evt is not null && evt.Type == "interrupt")
                {
                    evt = AguiBridgeProtocol.EnrichInterruptArgs(evt, _toolArgs);
                    evt = AguiBridgeProtocol.EnrichInterruptToolName(evt, _toolNames);
                }
                // base64 内容流附件累积：att_start → att_content → att_finish（产出 data URL 附件）
                if (evt is not null) evt = _atts.Track(evt);
            }
            catch (JsonException ex)
            {
                _logger.LogDebug(ex, "AG-UI 桥接事件解析失败，已忽略");
            }
            if (evt is not null) yield return evt;
        }
    }

    private async Task SendAsync(object evt, CancellationToken ct)
    {
        var ws = _ws ?? throw new InvalidOperationException("桥接未连接");
        var json = JsonSerializer.Serialize(evt, _json);
        var bytes = Encoding.UTF8.GetBytes(json);
        await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
    }

    /// <summary>人机交互恢复：standard → 上行 RunAgentInput（resume 数组，AG-UI 协议）；hub → AGENT_INTERACTION_RESOLVE。
    /// kind=input 型中断：优先按 responseSchema 提交的完整 payload（单选 / 多选 / 数字 / 多字段）；否则以 inputField 为键回传文本。</summary>
    public async Task ResumeInteractionAsync(string interruptId, string threadId, string runId, string groupId, bool approved, CancellationToken ct,
        string? toolCallId = null, string? toolName = null, JsonElement? toolArguments = null,
        string? input = null, string? inputField = null, JsonElement? payload = null)
    {
        if (IsHubMode)
        {
            await SendAsync(new
            {
                type = "AGENT_INTERACTION_RESOLVE",
                groupId,
                interruptId,
                approved,
                input,
                payload = payload is { ValueKind: JsonValueKind.Object } p ? (JsonElement?)p.Clone() : null,
            }, ct);
            return;
        }

        // AGUI.Abstractions RunAgentInput：同 threadId / runId + resume 数组恢复被中断的运行；
        // payload 按 AGUIToolApprovalResumePayload 标准：approved + 被批准工具的 toolCall（callId/name/arguments）；
        // 请求输入型：完整 payload 对象原样回传，或 { inputField: 用户输入 }（无 approved / toolCall）
        var resumePayload = BuildResumePayload(approved, toolCallId, toolName, toolArguments, input, inputField, payload);
        // 脱敏：只记录 payload 字段名与值规模，不输出序列化内容（用户输入可能含敏感信息）
        _logger.LogInformation("AG-UI 桥接（WS standard）交互决策已发送：interrupt={InterruptId} approved={Approved} payload={PayloadSummary}",
            interruptId, approved, BridgeLogging.DescribePayload(resumePayload));
        await SendAsync(new
        {
            threadId,
            runId = _runId ?? runId, // 复用首发 runId：外部服务据此关联原运行并继续
            messages = Array.Empty<object>(),
            resume = new[]
            {
                new
                {
                    interruptId,
                    status = "resolved",
                    payload = resumePayload,
                },
            },
        }, ct);
    }

    /// <summary>构建 resume payload：完整 payload（schema 表单）优先 → { inputField: input } → AGUIToolApprovalResumePayload。</summary>
    private static object BuildResumePayload(bool approved, string? toolCallId, string? toolName, JsonElement? toolArguments,
        string? input, string? inputField, JsonElement? payload)
    {
        if (payload is { ValueKind: JsonValueKind.Object } p)
            return p.Clone();
        if (input is not null && !string.IsNullOrWhiteSpace(inputField))
            return new Dictionary<string, object> { [inputField] = AguiBridgeProtocol.ResumeInputValue(input)! };
        var approval = new Dictionary<string, object> { ["approved"] = approved };
        if (toolCallId is not null || toolName is not null)
        {
            var toolCall = new Dictionary<string, object?> { ["callId"] = toolCallId, ["name"] = toolName };
            if (toolArguments is { ValueKind: JsonValueKind.Object } args)
                toolCall["arguments"] = args.Clone();
            approval["toolCall"] = toolCall;
        }
        return approval;
    }

    public async ValueTask DisposeAsync()
    {
        if (_ws is null) return;
        try { await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None); }
        catch { }
        _ws.Dispose();
        _ws = null;
        _runId = null;
        _selfMessageId = null; // 连接级状态复位（下次运行重新捕获回显消息 id）
        _atts.Clear();
    }
}
