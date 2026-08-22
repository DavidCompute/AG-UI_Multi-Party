using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace AguiGroupChat.Agents;

/// <summary>
/// AG-UI 桥接 HTTP(S) 客户端（standard 方言）：与 <see cref="AguiBridgeClient"/>（WS standard）共用
/// <see cref="AguiBridgeProtocol"/> 解析——POST RunAgentInput（AGUI.Abstractions 结构，用户服务端已验证兼容）
/// 后消费 SSE 事件流，审批中断（RUN_FINISHED + outcome.interrupts）统一产出 interrupt 事件；
/// 恢复时 POST RunAgentInput + resume 数组。
/// </summary>
public sealed class AguiBridgeHttpStandardClient : IAguiBridgeClient
{
    private readonly string _endpoint;
    private readonly string? _token;
    private readonly ILogger _logger;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    private HttpClient? _http;
    private SseLineReader? _reader;
    private string? _runId; // 首发 RunAgentInput 的 runId：resume 必须复用同一 runId，外部服务才能恢复原运行
    // TOOL_CALL_ARGS 累积的工具参数（toolCallId → JSON 字符串），审批中断回填 toolCall.arguments 用
    private readonly Dictionary<string, string> _toolArgs = new(StringComparer.Ordinal);
    // TOOL_CALL_START 记录的工具名（toolCallId → name），审批中断回填 toolName 用
    private readonly Dictionary<string, string> _toolNames = new(StringComparer.Ordinal);
    private readonly BridgeAttachmentAccumulator _atts = new(); // base64 内容流附件累积（STARTED→CONTENT→FINISHED）
    private string? _selfMessageId; // hub 方言回显消息 id（standard 方言不使用，仅为 Parse 签名一致性保留）

    public AguiBridgeHttpStandardClient(string endpoint, string? token, ILogger logger)
    {
        _endpoint = endpoint.TrimEnd('/');
        _token = token;
        _logger = logger;
    }

    private const int MaxSseLineChars = 2 * 1024 * 1024; // SSE data: 单行上限 2MB，超限断开（防外部服务下发巨型行撑爆内存）

    /// <summary>初始化 HttpClient（SSE 流由 <see cref="SendUserMessageAsync"/> / <see cref="ResumeInteractionAsync"/> 建立）。
    /// 禁用 W3C traceparent 自动注入：规避 .NET 10 在 Linux 上注入畸形请求头（400 invalid header）的问题；
    /// 禁用自动重定向：302 会绕过端点校验，重定向改由 <see cref="BridgeHttpRedirects"/> 手动逐跳重新校验（防 SSRF）。</summary>
    public Task ConnectAsync(string agentId, CancellationToken ct)
    {
        var handler = new SocketsHttpHandler
        {
            ActivityHeadersPropagator = null,
            AllowAutoRedirect = false,
        };
        var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        if (!string.IsNullOrEmpty(_token))
            http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _token);
        _http = http;
        _logger.LogInformation("AG-UI 桥接（HTTP standard）已就绪：{Endpoint}", _endpoint);
        return Task.CompletedTask;
    }

    /// <summary>上行 RunAgentInput（与官方 AGUIChatClient 相同的结构），建立 SSE 响应流。
    /// runId 由调用方传入（与恢复时一致，外部服务据此关联中断运行）。</summary>
    public async Task SendUserMessageAsync(string messageId, string threadId, string runId, string content, string groupId, string agentId, CancellationToken ct)
    {
        var http = _http ?? throw new InvalidOperationException("桥接未连接");
        _reader?.Dispose();

        var request = new HttpRequestMessage(HttpMethod.Post, _endpoint + "/")
        {
            Content = JsonContent.Create(
                new
                {
                    threadId,
                    runId,
                    messages = new[]
                    {
                        new { id = messageId, role = "user", content },
                    },
                    context = Array.Empty<object>(),
                },
                options: _json),
        };
        // 手动跟随重定向（每跳校验），响应流不释放（由 _reader 持有直到 DisposeAsync）
        var response = await BridgeHttpRedirects.SendAsync(http, request, ct);
        response.EnsureSuccessStatusCode();
        var stream = await response.Content.ReadAsStreamAsync(ct);
        _reader = new SseLineReader(new StreamReader(stream, Encoding.UTF8), MaxSseLineChars);
        _logger.LogInformation("AG-UI 桥接（HTTP standard）已发送：thread={ThreadId} endpoint={Endpoint}", threadId, _endpoint);
    }

    /// <summary>消费 SSE 事件流：心跳与无关事件跳过，AG-UI 事件按 standard 方言解析（含审批中断）。</summary>
    public async IAsyncEnumerable<AguiBridgeEvent> ReceiveAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var reader = _reader ?? throw new InvalidOperationException("桥接尚未发起请求");
        var received = 0;
        _logger.LogInformation("AG-UI 桥接（HTTP standard）开始消费事件流");
        while (true)
        {
            string? line = null;
            AguiBridgeEvent? disconnectError = null; // 连接异常断开事件（迭代器限制：不能在 catch 内 yield，先暂存）
            try
            {
                line = await reader.ReadLineAsync(ct);
            }
            catch (OperationCanceledException) { yield break; }
            catch (IOException)
            {
                // 外部服务异常断开（连接中断）：不再静默结束——产出断开错误事件，网关按错误处理回复不完整
                disconnectError = new AguiBridgeEvent("error", ErrorCode: "AGENT_BRIDGE_DISCONNECTED", ErrorMessage: "外部 AG-UI 服务连接中断");
            }
            if (disconnectError is not null)
            {
                yield return disconnectError;
                yield break;
            }
            if (line is null)
            {
                _logger.LogInformation("AG-UI 桥接（HTTP standard）事件流结束（读到 {Count} 行）", received);
                yield break;       // SSE 流正常结束（服务端主动关闭），保持静默
            }

            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue; // 空行 / 心跳 / 注释
            var payload = line["data:".Length..].Trim();
            if (payload.Length == 0) continue;
            received++;

            AguiBridgeEvent? evt = null;
            try
            {
                using var doc = JsonDocument.Parse(payload);
                evt = AguiBridgeProtocol.Parse(doc, hubMode: false, agentId: "", new HashSet<string>(), ref _selfMessageId);
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
                _logger.LogDebug(ex, "AG-UI 桥接（HTTP standard）事件解析失败，已忽略");
            }
            if (evt is not null)
            {
                _logger.LogDebug("AG-UI 桥接（HTTP standard）事件：{Type}", evt.Type);
                yield return evt;
            }
            else
            {
                _logger.LogDebug("AG-UI 桥接（HTTP standard）无关事件：{Payload}", payload.Length > 200 ? payload[..200] : payload);
            }
        }
    }

    /// <summary>人机交互恢复：上行 RunAgentInput + resume 数组（AG-UI 协议），建立新的 SSE 响应流供继续消费。
    /// toolCall 信息（callId / name / arguments）来自中断前的 TOOL_CALL_* 事件：AGUI.AspNetCore 的
    /// ApprovalRequiredAIFunction 需要 approved + toolCall 才能执行被批准的工具；
    /// 请求输入型（kind=input）：优先完整 payload（单选 / 多选 / 数字 / 多字段），否则 { inputField: 用户输入 }。</summary>
    public async Task ResumeInteractionAsync(string interruptId, string threadId, string runId, string groupId, bool approved, CancellationToken ct,
        string? toolCallId = null, string? toolName = null, JsonElement? toolArguments = null,
        string? input = null, string? inputField = null, JsonElement? payload = null)
    {
        var http = _http ?? throw new InvalidOperationException("桥接未连接");
        _reader?.Dispose();
        _reader = null;
        _toolArgs.Clear();

        Dictionary<string, object> resumePayload;
        if (payload is { ValueKind: JsonValueKind.Object } p)
        {
            // 按 responseSchema 提交的完整 payload（单选 / 多选 / 数字 / 多字段）：对象原样回传
            resumePayload = p.EnumerateObject().ToDictionary(kv => kv.Name, kv => (object)kv.Value.Clone());
        }
        else if (input is not null && !string.IsNullOrWhiteSpace(inputField))
        {
            // 请求输入 / 单选 / 多选（单字段）：按 responseSchema 字段名回传用户输入（多选为 JSON 数组，经 ResumeInputValue 解析）
            resumePayload = new Dictionary<string, object> { [inputField] = AguiBridgeProtocol.ResumeInputValue(input)! };
        }
        else
        {
            resumePayload = new Dictionary<string, object>
            {
                // AGUI.Abstractions AGUIToolApprovalResumePayload：字段名是 approved（非 accepted）
                ["approved"] = approved,
            };
            if (toolCallId is not null || toolName is not null)
            {
                var toolCall = new Dictionary<string, object?> { ["callId"] = toolCallId, ["name"] = toolName };
                if (toolArguments is { ValueKind: JsonValueKind.Object } args)
                    toolCall["arguments"] = args.Clone();
                resumePayload["toolCall"] = toolCall;
            }
        }

        var request = new HttpRequestMessage(HttpMethod.Post, _endpoint + "/")
        {
            Content = JsonContent.Create(
                new
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
                },
                options: _json),
        };
        // 手动跟随重定向（每跳校验），响应流不释放（由 _reader 持有直到 DisposeAsync）
        var response = await BridgeHttpRedirects.SendAsync(http, request, ct);
        response.EnsureSuccessStatusCode();
        var stream = await response.Content.ReadAsStreamAsync(ct);
        _reader = new SseLineReader(new StreamReader(stream, Encoding.UTF8), MaxSseLineChars);
        // 脱敏：只记录 payload 字段名与值规模，不输出序列化内容（用户输入可能含敏感信息）
        _logger.LogInformation("AG-UI 桥接（HTTP standard）交互决策已发送：interrupt={InterruptId} approved={Approved} payload={PayloadSummary}",
            interruptId, approved, BridgeLogging.DescribePayload(resumePayload));
    }

    /// <summary>
    /// 带行长度上限与跨调用残留缓冲的 SSE 行读取器：每次 ReadAsync 可能一次读入多行，
    /// 行后残留字符必须保留供下次调用（否则单行返回后其余事件全部丢失、流提前 EOF）；
    /// 同时限制单行长度，防止外部服务下发巨型行撑爆内存。
    /// </summary>
    private sealed class SseLineReader
    {
        private readonly StreamReader _reader;
        private readonly int _maxLineChars;
        private readonly char[] _buf = new char[4096];
        private readonly StringBuilder _line = new();
        private int _pos;    // _buf 中下一个待消费字符
        private int _count;  // _buf 中有效字符数
        private bool _ended; // 底层流已结束

        public SseLineReader(StreamReader reader, int maxLineChars)
        {
            _reader = reader;
            _maxLineChars = maxLineChars;
        }

        public void Dispose() => _reader.Dispose();

        /// <summary>返回下一行（不含 \n）；流结束且无残留返回 null；单行超限抛 InvalidOperationException。</summary>
        public async Task<string?> ReadLineAsync(CancellationToken ct)
        {
            while (true)
            {
                if (_pos >= _count)
                {
                    if (_ended)
                    {
                        // EOF：残留的末行（无结尾换行）返回一次，之后返回 null
                        if (_line.Length == 0) return null;
                        var final = _line.ToString();
                        _line.Clear();
                        return final;
                    }
                    _count = await _reader.ReadAsync(_buf.AsMemory(), ct);
                    _pos = 0;
                    if (_count == 0) { _ended = true; continue; }
                }
                for (; _pos < _count; _pos++)
                {
                    var c = _buf[_pos];
                    if (c == '\n')
                    {
                        var line = _line.ToString();
                        _line.Clear();
                        _pos++; // 跳过换行符
                        return line;
                    }
                    _line.Append(c);
                    if (_line.Length > _maxLineChars)
                        throw new InvalidOperationException($"AG-UI 桥接（HTTP standard）SSE 单行超过 {_maxLineChars / 1024 / 1024}MB 上限，已断开");
                }
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        var reader = _reader;
        _reader = null;
        if (reader is not null) reader.Dispose();
        var http = _http;
        _http = null;
        _runId = null;
        _toolNames.Clear();
        _atts.Clear();
        http?.Dispose();
        return ValueTask.CompletedTask;
    }
}
