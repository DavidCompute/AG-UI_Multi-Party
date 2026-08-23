using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using AguiGroupChat.Sdk.Models;

namespace AguiGroupChat.Sdk;

/// <summary>
/// 实时传输类型：WebSocket（全双工）或 SSE（单向下行）。
/// </summary>
public enum RealtimeTransport
{
    /// <summary>WebSocket 全双工（默认）：支持上行事件与下行广播。</summary>
    WebSocket,

    /// <summary>SSE 单向下行：只能接收事件，订阅经 HTTP（POST /ag-ui/group/subscribe）完成。</summary>
    Sse,
}

/// <summary>
/// 实时通道客户端：封装 /ws（WebSocket）与 /sse（SSE）连接、GROUP_CONNECTED 握手、
/// 订阅 / 退订、上行事件发送与下行事件分发。
///
/// 用法：
/// <code>
/// using var realtime = new AguiRealtimeClient(options);
/// realtime.On&lt;TextMessageContentEvent&gt;(e => Console.WriteLine(e.Delta));
/// await realtime.ConnectAsync(["group_001"], ct);
/// await realtime.SubscribeAsync(["group_002"], ct);
/// await realtime.SendMessageAsync(new GroupMessageSendRequest { ... });
/// </code>
///
/// 传输方式由 <see cref="AguiClientOptions.Transport"/> 决定：
/// WebSocket 全双工（支持上行事件）；SSE 单向下行（订阅经 <see cref="AguiClient"/> 的
/// POST /ag-ui/group/subscribe 完成，ConnectAsync 会把 groupIds 拼进 URL）。
/// </summary>
public sealed class AguiRealtimeClient : IAsyncDisposable
{
    private readonly AguiClientOptions _options;
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private readonly Dictionary<string, List<Delegate>> _handlers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<Action<AguiEvent>>> _rawHandlers = new(StringComparer.Ordinal);
    private readonly object _sendLock = new();
    private int _disposed;
    private bool _stopRequested;

    public AguiRealtimeClient(AguiClientOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (options.BaseUri is null)
            throw new ArgumentException("BaseUri 不能为空", nameof(options));
    }

    /// <summary>
    /// 当前会话令牌。可依赖 <see cref="AguiClient"/> 登录后共享，或独立提供。
    /// 连接时写入 URL（&amp;token=...）。令牌变化后需重连生效。
    /// </summary>
    public string? Token { get; set; }

    /// <summary>是否 WebSocket 传输（false = SSE）。</summary>
    public bool IsWebSocket => _options.Transport == RealtimeTransport.WebSocket;

    /// <summary>是否已连接（WebSocket 打开且未终止）。</summary>
    public bool IsConnected => IsWebSocket && _ws is { State: WebSocketState.Open } && !_stopRequested;

    /// <summary>最后建立的 connectionId（GROUP_CONNECTED 握手）。SSE 场景用它做动态订阅。</summary>
    public string? ConnectionId { get; private set; }

    /// <summary>连接建立 + 收到 GROUP_CONNECTED 时触发。</summary>
    public event Action<GroupConnectedEvent>? Connected;

    /// <summary>连接意外断开时触发（正常关闭也触发，ex = null）。</summary>
    public event Action<Exception?>? Disconnected;

    /// <summary>收到任意事件时触发。</summary>
    public event Action<AguiEvent>? AnyEvent;

    /// <summary>订阅某类事件的强类型处理器（T 必须继承 <see cref="AguiEvent"/>）。</summary>
    public void On<T>(Action<T> handler) where T : AguiEvent
    {
        var typeName = EventTypeOf<T>();
        if (!_handlers.TryGetValue(typeName, out var list))
            _handlers[typeName] = list = [];
        list.Add(handler);
    }

    /// <summary>按事件名字符串订阅原始事件（适用于 Hub 扩展的新事件类型）。</summary>
    public void OnRaw(string eventType, Action<AguiEvent> handler)
    {
        if (!_rawHandlers.TryGetValue(eventType, out var list))
            _rawHandlers[eventType] = list = [];
        list.Add(handler);
    }

    /// <summary>连接到 Hub 并订阅指定群。ws 场景自动发送 GROUP_SUBSCRIBE；sse 场景在 URL 携带 groupIds。</summary>
    public async Task ConnectAsync(IEnumerable<string>? groupIds = null, CancellationToken ct = default)
    {
        ThrowIfDisposed();

        var groups = (groupIds ?? []).Where(g => !string.IsNullOrWhiteSpace(g)).Distinct().ToList();
        var token = Token ?? _options.TokenProvider?.Invoke();
        var scheme = _options.BaseUri.Scheme == Uri.UriSchemeHttps
            ? (IsWebSocket ? "wss" : "https")
            : (IsWebSocket ? "ws" : "http");
        var uri = new UriBuilder(_options.BaseUri)
        {
            Scheme = scheme,
            Path = _options.BaseUri.AbsolutePath.TrimEnd('/') + (IsWebSocket ? "/ws" : "/sse"),
        };

        var query = new StringBuilder();
        if (IsWebSocket && !string.IsNullOrEmpty(token))
            AppendPair("token", token);
        else if (!IsWebSocket)
        {
            if (groups.Count > 0) AppendPair("groupIds", string.Join(",", groups));
            if (!string.IsNullOrEmpty(token)) AppendPair("token", token);
        }
        uri.Query = query.ToString();

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _stopRequested = false;

        if (IsWebSocket)
        {
            _ws = new ClientWebSocket();
            await _ws.ConnectAsync(uri.Uri, _cts.Token).ConfigureAwait(false);
            _ = Task.Run(() => ReceiveLoopWsAsync(_ws, _cts.Token), CancellationToken.None);
            await WaitForConnectedAsync(ct).ConfigureAwait(false);
            if (groups.Count > 0)
                await SubscribeAsync(groups, ct).ConfigureAwait(false);
        }
        else
        {
            _ = Task.Run(() => ReceiveSseLoopAsync(uri.Uri, _cts.Token), CancellationToken.None);
            // SSE：GROUP_CONNECTED 异步到达，等待握手拿到 connectionId
            await WaitForConnectedAsync(ct).ConfigureAwait(false);
        }

        void AppendPair(string key, string value)
        {
            if (query.Length > 0) query.Append('&');
            query.Append(key).Append('=').Append(Uri.EscapeDataString(value));
        }
    }

    /// <summary>订阅群组（ws：上行 GROUP_SUBSCRIBE；sse：需配合 AguiClient.SubscribeSseAsync）。</summary>
    public Task SubscribeAsync(IEnumerable<string> groupIds, CancellationToken ct = default)
    {
        var groups = (groupIds ?? []).Where(g => !string.IsNullOrWhiteSpace(g)).Distinct().ToList();
        if (groups.Count == 0) return Task.CompletedTask;
        if (!IsWebSocket)
            throw new InvalidOperationException("SSE 传输需经 HTTP 订阅：调用 AguiClient.SubscribeSseAsync(connectionId, groupIds)。");
        return SendEventAsync(EventTypes.GroupSubscribe, new SubscribeRequest { GroupIds = groups }, ct);
    }

    /// <summary>取消订阅群组（仅 ws）。</summary>
    public Task UnsubscribeAsync(IEnumerable<string> groupIds, CancellationToken ct = default)
    {
        var groups = (groupIds ?? []).Where(g => !string.IsNullOrWhiteSpace(g)).Distinct().ToList();
        if (groups.Count == 0) return Task.CompletedTask;
        if (!IsWebSocket)
            throw new InvalidOperationException("SSE 传输需经 HTTP 退订：调用 AguiClient.UnsubscribeSseAsync(connectionId, groupIds)。");
        return SendEventAsync(EventTypes.GroupUnsubscribe, new SubscribeRequest { GroupIds = groups }, ct);
    }

    /// <summary>经 WS 上行发送群消息（等效 POST /ag-ui/group/message/send）。发送者以连接身份为准。</summary>
    public Task SendMessageAsync(GroupMessageSendRequest request, CancellationToken ct = default)
        => SendEventAsync(EventTypes.GroupMessageSend, request, ct);

    /// <summary>经 WS 上行撤回消息。</summary>
    public Task RecallMessageAsync(GroupMessageRecallRequest request, CancellationToken ct = default)
        => SendEventAsync(EventTypes.GroupMessageRecall, request, ct);

    /// <summary>经 WS 上行重新回答最后一条智能体消息。</summary>
    public Task RegenerateMessageAsync(GroupMessageRegenerateRequest request, CancellationToken ct = default)
        => SendEventAsync(EventTypes.GroupMessageRegenerate, request, ct);

    /// <summary>经 WS 上行发送正在输入状态。</summary>
    public Task SendTypingAsync(GroupTypingRequest request, CancellationToken ct = default)
        => SendEventAsync(EventTypes.GroupTyping, request, ct);

    /// <summary>经 WS 上行发送已读回执。</summary>
    public Task SendReadAsync(GroupReadRequest request, CancellationToken ct = default)
        => SendEventAsync(EventTypes.GroupMessageRead, request, ct);

    /// <summary>经 WS 上行对交互请求作出决策（批准 / 拒绝）。</summary>
    public Task ResolveInteractionAsync(GroupInteractionResolveRequest request, CancellationToken ct = default)
        => SendEventAsync(EventTypes.AgentInteractionResolve, request, ct);

    /// <summary>关闭连接。</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _stopRequested = true;
        _cts?.Cancel();
        if (_ws is { State: WebSocketState.Open })
        {
            try { await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "dispose", CancellationToken.None).ConfigureAwait(false); }
            catch { }
        }
        _ws?.Dispose();
        _cts?.Dispose();
    }

    // ---------------- 发送 ----------------

    private async Task SendEventAsync(string type, object payload, CancellationToken ct)
    {
        ThrowIfDisposed();
        if (!IsWebSocket || _ws is null || _ws.State != WebSocketState.Open)
            throw new InvalidOperationException("WebSocket 未连接，无法上行事件");
        lock (_sendLock)
        {
            if (_ws.State != WebSocketState.Open)
                throw new InvalidOperationException("WebSocket 已断开");
        }
        var envelope = WithType(type, payload);
        var json = AguiJson.Serialize(envelope);
        var bytes = Encoding.UTF8.GetBytes(json);
        await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
    }

    private static object WithType(string type, object payload)
    {
        // 用 JsonObject 写入 type 字段，保持与 WS 上行约定一致（字段顺序无关，type 用于判别）。
        var node = JsonSerializer.SerializeToNode(payload, AguiJson.Options)!;
        var obj = node.AsObject();
        obj["type"] = type;
        return obj;
    }

    // ---------------- 接收（WebSocket） ----------------

    private async Task ReceiveLoopWsAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        var decoder = new UTF8Encoding(false, true).GetDecoder();
        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var sb = new StringBuilder();
                var result = await ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) break;
                if (result.MessageType != WebSocketMessageType.Text) continue;
                AppendDecoded(sb, decoder, buffer, result.Count, result.EndOfMessage);
                while (!result.EndOfMessage)
                {
                    result = await ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                    if (result.MessageType != WebSocketMessageType.Text) break;
                    AppendDecoded(sb, decoder, buffer, result.Count, result.EndOfMessage);
                }
                Dispatch(sb.ToString());
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException ex) { if (!_stopRequested) Disconnected?.Invoke(ex); return; }
        catch (Exception ex) { if (!_stopRequested) Disconnected?.Invoke(ex); return; }
        finally
        {
            if (!_stopRequested)
                Disconnected?.Invoke(null);
        }
    }

    private static void AppendDecoded(StringBuilder sb, Decoder decoder, byte[] buffer, int count, bool flush)
    {
        var charCount = decoder.GetCharCount(buffer, 0, count, flush);
        var chars = new char[charCount];
        decoder.GetChars(buffer, 0, count, chars, 0, flush);
        sb.Append(chars);
    }

    // ---------------- 接收（SSE） ----------------

    private async Task ReceiveSseLoopAsync(Uri uri, CancellationToken ct)
    {
        using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/event-stream"));
        var token = Token ?? _options.TokenProvider?.Invoke();
        if (!string.IsNullOrEmpty(token))
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        try
        {
            await using var stream = await client.GetStreamAsync(uri, ct).ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var data = new StringBuilder();
            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line is null) break;
                if (line.StartsWith("data:", StringComparison.Ordinal))
                    data.Append(line.AsSpan(5).TrimStart(' ').ToString());
                else if (line.Length == 0 && data.Length > 0)
                {
                    Dispatch(data.ToString());
                    data.Clear();
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_stopRequested) Disconnected?.Invoke(ex); }
        finally
        {
            if (!_stopRequested)
                Disconnected?.Invoke(null);
        }
    }

    // ---------------- 分发 ----------------

    private void Dispatch(string json)
    {
        AguiEvent? evt;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("type", out var typeEl))
                return;
            evt = DeserializeRaw(typeEl.GetString(), json);
        }
        catch (JsonException)
        {
            return;
        }
        if (evt is null || string.IsNullOrEmpty(evt.Type)) return;

        AnyEvent?.Invoke(evt);

        if (evt is GroupConnectedEvent connected)
        {
            ConnectionId = connected.ConnectionId;
            Connected?.Invoke(connected);
        }

        if (_handlers.TryGetValue(evt.Type!, out var typedList))
            foreach (var handler in typedList)
                DispatchTyped(handler, evt);

        if (_rawHandlers.TryGetValue(evt.Type!, out var rawList))
            foreach (var handler in rawList)
                handler(evt);
    }

    private static void DispatchTyped(Delegate handler, AguiEvent evt)
    {
        var targetType = handler.Method.GetParameters()[0].ParameterType;
        if (targetType.IsInstanceOfType(evt))
            handler.DynamicInvoke(evt);
    }

    private static AguiEvent? DeserializeRaw(string? type, string json) => type switch
    {
        EventTypes.GroupConnected => AguiJson.Deserialize<GroupConnectedEvent>(json),
        EventTypes.GroupCreated => AguiJson.Deserialize<GroupCreatedEvent>(json),
        EventTypes.GroupUpdated => AguiJson.Deserialize<GroupUpdatedEvent>(json),
        EventTypes.GroupDisbanded => AguiJson.Deserialize<GroupDisbandedEvent>(json),
        EventTypes.GroupMemberJoined => AguiJson.Deserialize<GroupMemberJoinedEvent>(json),
        EventTypes.GroupMemberLeft => AguiJson.Deserialize<GroupMemberLeftEvent>(json),
        EventTypes.GroupMemberUpdated => AguiJson.Deserialize<GroupMemberUpdatedEvent>(json),
        EventTypes.TextMessageStart => AguiJson.Deserialize<TextMessageStartEvent>(json),
        EventTypes.TextMessageContent => AguiJson.Deserialize<TextMessageContentEvent>(json),
        EventTypes.TextMessageReasoning => AguiJson.Deserialize<TextMessageReasoningEvent>(json),
        EventTypes.TextMessageEnd => AguiJson.Deserialize<TextMessageEndEvent>(json),
        EventTypes.TextMessageAttachments => AguiJson.Deserialize<TextMessageAttachmentsEvent>(json),
        EventTypes.TextMessagePlan => AguiJson.Deserialize<TextMessagePlanEvent>(json),
        EventTypes.TextMessageReset => AguiJson.Deserialize<TextMessageResetEvent>(json),
        EventTypes.GroupMessageRecalled => AguiJson.Deserialize<GroupMessageRecalledEvent>(json),
        EventTypes.GroupTyping => AguiJson.Deserialize<GroupTypingEvent>(json),
        EventTypes.GroupMessageRead => AguiJson.Deserialize<GroupMessageReadEvent>(json),
        EventTypes.ToolCallStart => AguiJson.Deserialize<ToolCallStartEvent>(json),
        EventTypes.ToolCallArgs => AguiJson.Deserialize<ToolCallArgsEvent>(json),
        EventTypes.ToolCallResult => AguiJson.Deserialize<ToolCallResultEvent>(json),
        EventTypes.ActivitySnapshot => AguiJson.Deserialize<ActivitySnapshotEvent>(json),
        EventTypes.AgentInteractionRequest => AguiJson.Deserialize<AgentInteractionRequestEvent>(json),
        EventTypes.AgentInteractionResolved => AguiJson.Deserialize<AgentInteractionResolvedEvent>(json),
        EventTypes.GroupSubscribeAck => AguiJson.Deserialize<GroupSubscribeAckEvent>(json),
        EventTypes.GroupTopicCreated => AguiJson.Deserialize<GroupTopicCreatedEvent>(json),
        EventTypes.GroupMessageTopicMoved => AguiJson.Deserialize<GroupMessageTopicMovedEvent>(json),
        EventTypes.GroupTopicDeleted => AguiJson.Deserialize<GroupTopicDeletedEvent>(json),
        EventTypes.GroupTopicCleared => AguiJson.Deserialize<GroupTopicClearedEvent>(json),
        EventTypes.GroupStateSnapshot => AguiJson.Deserialize<GroupStateSnapshotEvent>(json),
        EventTypes.RunError => AguiJson.Deserialize<RunErrorEvent>(json),
        _ => new AguiEvent { Type = type },
    };

    private static string EventTypeOf<T>() where T : AguiEvent
    {
        if (typeof(T) == typeof(GroupConnectedEvent)) return EventTypes.GroupConnected;
        if (typeof(T) == typeof(GroupCreatedEvent)) return EventTypes.GroupCreated;
        if (typeof(T) == typeof(GroupUpdatedEvent)) return EventTypes.GroupUpdated;
        if (typeof(T) == typeof(GroupDisbandedEvent)) return EventTypes.GroupDisbanded;
        if (typeof(T) == typeof(GroupMemberJoinedEvent)) return EventTypes.GroupMemberJoined;
        if (typeof(T) == typeof(GroupMemberLeftEvent)) return EventTypes.GroupMemberLeft;
        if (typeof(T) == typeof(GroupMemberUpdatedEvent)) return EventTypes.GroupMemberUpdated;
        if (typeof(T) == typeof(TextMessageStartEvent)) return EventTypes.TextMessageStart;
        if (typeof(T) == typeof(TextMessageContentEvent)) return EventTypes.TextMessageContent;
        if (typeof(T) == typeof(TextMessageReasoningEvent)) return EventTypes.TextMessageReasoning;
        if (typeof(T) == typeof(TextMessageEndEvent)) return EventTypes.TextMessageEnd;
        if (typeof(T) == typeof(TextMessageAttachmentsEvent)) return EventTypes.TextMessageAttachments;
        if (typeof(T) == typeof(TextMessagePlanEvent)) return EventTypes.TextMessagePlan;
        if (typeof(T) == typeof(TextMessageResetEvent)) return EventTypes.TextMessageReset;
        if (typeof(T) == typeof(GroupMessageRecalledEvent)) return EventTypes.GroupMessageRecalled;
        if (typeof(T) == typeof(GroupTypingEvent)) return EventTypes.GroupTyping;
        if (typeof(T) == typeof(GroupMessageReadEvent)) return EventTypes.GroupMessageRead;
        if (typeof(T) == typeof(ToolCallStartEvent)) return EventTypes.ToolCallStart;
        if (typeof(T) == typeof(ToolCallArgsEvent)) return EventTypes.ToolCallArgs;
        if (typeof(T) == typeof(ToolCallResultEvent)) return EventTypes.ToolCallResult;
        if (typeof(T) == typeof(ActivitySnapshotEvent)) return EventTypes.ActivitySnapshot;
        if (typeof(T) == typeof(AgentInteractionRequestEvent)) return EventTypes.AgentInteractionRequest;
        if (typeof(T) == typeof(AgentInteractionResolvedEvent)) return EventTypes.AgentInteractionResolved;
        if (typeof(T) == typeof(GroupSubscribeAckEvent)) return EventTypes.GroupSubscribeAck;
        if (typeof(T) == typeof(GroupTopicCreatedEvent)) return EventTypes.GroupTopicCreated;
        if (typeof(T) == typeof(GroupMessageTopicMovedEvent)) return EventTypes.GroupMessageTopicMoved;
        if (typeof(T) == typeof(GroupTopicDeletedEvent)) return EventTypes.GroupTopicDeleted;
        if (typeof(T) == typeof(GroupTopicClearedEvent)) return EventTypes.GroupTopicCleared;
        if (typeof(T) == typeof(GroupStateSnapshotEvent)) return EventTypes.GroupStateSnapshot;
        if (typeof(T) == typeof(RunErrorEvent)) return EventTypes.RunError;
        throw new NotSupportedException($"未识别的强类型事件：{typeof(T).Name}，请改用 OnRaw 订阅。");
    }

    private async Task WaitForConnectedAsync(CancellationToken ct)
    {
        // 等待 GROUP_CONNECTED 握手（通常 < 1s）。
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (ConnectionId is null && DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
            await Task.Delay(20, ct).ConfigureAwait(false);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed == 1)
            throw new ObjectDisposedException(nameof(AguiRealtimeClient));
    }
}
