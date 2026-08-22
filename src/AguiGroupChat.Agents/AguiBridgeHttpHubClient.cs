using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace AguiGroupChat.Agents;

/// <summary>
/// AG-UI 桥接 HTTP(S) 客户端（hub 方言）：
/// 对接本项目群聊 Hub 的 HTTP 面——先 GET /sse?memberId=&amp;groupIds= 建立 SSE 下行订阅，
/// 再 POST /ag-ui/group/message/send 上行群消息；外部 agent 的流式回复经 SSE 事件回灌。
/// standard 方言的 HTTP(S) 传输不走本类，由 <see cref="AgentGateway"/> 使用官方 AGUIChatClient。
/// </summary>
public sealed class AguiBridgeHttpHubClient : IAguiBridgeClient
{
    private readonly string _endpoint;
    private readonly string? _token;
    private readonly string _agentId;
    private readonly ILogger _logger;
    private readonly HashSet<string> _acceptedReplies = new(StringComparer.Ordinal); // 外部回复消息集合
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    private HttpClient? _http;
    private StreamReader? _reader;
    private string? _selfMessageId; // hub 方言：自己发送消息回显的 messageId（从回显 START 捕获，供识别外部回复）

    public AguiBridgeHttpHubClient(string endpoint, string? token, string agentId, ILogger logger)
    {
        _endpoint = endpoint.TrimEnd('/');
        _token = token;
        _agentId = agentId;
        _logger = logger;
    }

    /// <summary>初始化 HttpClient（SSE 流式连接由 <see cref="SendUserMessageAsync"/> 按需建立，需要 groupId）。
    /// 禁用自动重定向：302 会绕过端点校验，重定向改由 <see cref="BridgeHttpRedirects"/> 手动逐跳重新校验（防 SSRF）。</summary>
    public Task ConnectAsync(string agentId, CancellationToken ct)
    {
        var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
        if (!string.IsNullOrEmpty(_token))
            http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _token);
        _http = http;
        _logger.LogInformation("AG-UI 桥接（HTTP hub）已就绪：{Endpoint}", _endpoint);
        return Task.CompletedTask;
    }

    /// <summary>先订阅群 SSE 下行，再发送群消息；回复事件随后由 <see cref="ReceiveAsync"/> 消费。
    /// hub 方言不需要 runId（GROUP_MESSAGE_SEND 无运行概念），参数仅为接口一致。</summary>
    public async Task SendUserMessageAsync(string messageId, string threadId, string runId, string content, string groupId, string agentId, CancellationToken ct)
    {
        var http = _http ?? throw new InvalidOperationException("桥接未连接");

        // 1. 建立 SSE 下行（groupIds 即订阅目标；真实 Hub 连接后心跳保活，回复事件流式到达）
        var sseUri = $"{_endpoint}/sse?memberId={Uri.EscapeDataString(agentId)}&groupIds={Uri.EscapeDataString(groupId)}";
        using var sseRequest = new HttpRequestMessage(HttpMethod.Get, sseUri);
        // 手动跟随重定向（每跳校验），响应流不释放（由 _reader 持有直到 DisposeAsync）
        var sseResponse = await BridgeHttpRedirects.SendAsync(http, sseRequest, ct);
        sseResponse.EnsureSuccessStatusCode();
        var stream = await sseResponse.Content.ReadAsStreamAsync(ct);
        _reader = new StreamReader(stream, Encoding.UTF8);

        // 2. 上行群消息（userId 以 agent 身份标识，事件回显按 agentId 过滤）
        var sendRequest = new HttpRequestMessage(HttpMethod.Post, $"{_endpoint}/ag-ui/group/message/send")
        {
            Content = JsonContent.Create(
                new
                {
                    groupId,
                    userId = agentId,
                    content,
                    timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                },
                options: _json),
        };
        using var send = await BridgeHttpRedirects.SendAsync(http, sendRequest, ct);
        send.EnsureSuccessStatusCode();
        _logger.LogInformation("AG-UI 桥接（HTTP hub）已发送群消息：group={GroupId} message={MessageId}", groupId, messageId);
    }

    /// <summary>消费 SSE 事件流：心跳（: ping）与无关事件跳过，外部 agent 回复按 hub 方言解析。</summary>
    public async IAsyncEnumerable<AguiBridgeEvent> ReceiveAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var reader = _reader ?? throw new InvalidOperationException("桥接尚未订阅");
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
            if (line is null) yield break;       // SSE 流正常结束（服务端主动关闭），保持静默

            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue; // 空行 / : ping 心跳
            var payload = line["data:".Length..].Trim();
            if (payload.Length == 0) continue;

            AguiBridgeEvent? evt = null;
            try
            {
                using var doc = JsonDocument.Parse(payload);
                evt = AguiBridgeProtocol.Parse(doc, hubMode: true, _agentId, _acceptedReplies, ref _selfMessageId);
            }
            catch (JsonException ex)
            {
                _logger.LogDebug(ex, "AG-UI 桥接（HTTP hub）事件解析失败，已忽略");
            }
            if (evt is not null) yield return evt;
        }
    }

    public ValueTask DisposeAsync()
    {
        var reader = _reader;
        _reader = null;
        if (reader is not null) reader.Dispose();
        var http = _http;
        _http = null;
        _selfMessageId = null; // 连接级状态复位（下次运行重新捕获回显消息 id）
        http?.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>人机交互恢复（hub 方言 HTTP）：POST /ag-ui/group/interaction/resolve，成员身份 = agentId。
    /// standard 方言的 toolCall 参数在 hub 方言无意义，忽略；kind=input 型转发用户输入 / 完整 payload。</summary>
    public async Task ResumeInteractionAsync(string interruptId, string threadId, string runId, string groupId, bool approved, CancellationToken ct,
        string? toolCallId = null, string? toolName = null, JsonElement? toolArguments = null,
        string? input = null, string? inputField = null, JsonElement? payload = null)
    {
        var http = _http ?? throw new InvalidOperationException("桥接未连接");
        var request = new HttpRequestMessage(HttpMethod.Post, $"{_endpoint}/ag-ui/group/interaction/resolve?memberId={Uri.EscapeDataString(_agentId)}")
        {
            Content = JsonContent.Create(
                new { groupId, interruptId, approved, input, payload = payload is { ValueKind: JsonValueKind.Object } p ? (JsonElement?)p.Clone() : null },
                options: _json),
        };
        using var send = await BridgeHttpRedirects.SendAsync(http, request, ct);
        send.EnsureSuccessStatusCode();
        // 脱敏：input 是用户输入，只记录长度，不输出内容
        _logger.LogInformation("AG-UI 桥接（HTTP hub）交互决策已发送：interrupt={InterruptId} approved={Approved} inputLen={InputLen}",
            interruptId, approved, input?.Length ?? 0);
    }
}
