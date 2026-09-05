using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using AguiGroupChat.Hub.Infra;
using AguiGroupChat.Hub.Messaging;
using AguiGroupChat.Hub.Models;
using AguiGroupChat.Hub.Options;
using AguiGroupChat.Hub.Users;

namespace AguiGroupChat.Hub.Transport;

/// <summary>
/// WebSocket 下行端点：/ws?memberId=user_1001[&amp;token=...]。
/// 连接建立后先收到 GROUP_CONNECTED 握手，再通过 GROUP_SUBSCRIBE / GROUP_UNSUBSCRIBE 事件管理订阅；
/// 也支持 GROUP_MESSAGE_SEND / GROUP_MESSAGE_RECALL / GROUP_TYPING / GROUP_MESSAGE_READ 上行。
/// 身份解析：携带有效 token 时以令牌身份为准（忽略 memberId 参数）；
/// 未携带 token 时回退到 memberId 直连（兼容旧客户端），除非 Auth:RequireTokenOnRealTime=true。
/// </summary>
public sealed class WebSocketEndpoint
{
    private readonly ConnectionManager _connections;
    private readonly GroupHub _hub;
    private readonly AuthService _auth;
    private readonly AuthOptions _authOptions;
    private readonly ILogger<WebSocketEndpoint> _logger;

    public WebSocketEndpoint(ConnectionManager connections, GroupHub hub, AuthService auth, AuthOptions authOptions, ILogger<WebSocketEndpoint> logger)
    {
        _connections = connections;
        _hub = hub;
        _auth = auth;
        _authOptions = authOptions;
        _logger = logger;
    }

    public async Task HandleAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        // 跨站来源校验（防 CSWSH）：同源 / 白名单 / 无 Origin（非浏览器）放行，其余 403
        if (!OriginGuard.IsAllowed(context.Request, _authOptions))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new AguiError(ErrorCodes.GroupPermissionDenied,
                "跨站 WebSocket 连接被拒绝（Origin 不在允许范围）"));
            return;
        }

        var memberId = context.Request.Query["memberId"].ToString();
        var token = ResolveToken(context.Request);

        // 携带 token → 以令牌身份为准（校验失败直接拒绝）
        if (!string.IsNullOrEmpty(token))
        {
            var user = _auth.ValidateToken(token);
            if (user is null)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(new AguiError(ErrorCodes.UserUnauthorized, "令牌无效或已过期"));
                return;
            }
            memberId = user.UserId;
        }
        else if (_authOptions.RequireTokenOnRealTime)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new AguiError(ErrorCodes.UserUnauthorized, "缺少身份令牌（Auth:RequireTokenOnRealTime=true）"));
            return;
        }

        if (string.IsNullOrWhiteSpace(memberId))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new AguiError(ErrorCodes.GroupPermissionDenied, "缺少 memberId 身份参数"));
            return;
        }

        using var ws = await context.WebSockets.AcceptWebSocketAsync();
        // 有界发送队列：慢 / 断网客户端最多积压 2048 条，超出丢弃新事件（连接断开重连时经快照恢复），
        // 避免大群高频消息时无界积压导致内存膨胀。
        var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(2048)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.DropWrite,
        });
        var connection = new HubConnection
        {
            ConnectionId = IdGenerator.NewId(),
            MemberId = memberId,
            Transport = "websocket",
            Sender = (json, ct) => channel.Writer.WriteAsync(json, ct).AsTask(),
        };
        // 连接数上限检查（同成员 ≤ 10 / 同 IP ≤ 50）：超限以 PolicyViolation 关闭并记日志（DoS 防护）
        if (!_connections.Register(connection, context.Connection.RemoteIpAddress?.ToString()))
        {
            _logger.LogWarning("WebSocket 连接被拒绝（连接数超限）：member={Member} ip={Ip}",
                memberId, context.Connection.RemoteIpAddress);
            await ws.CloseAsync(WebSocketCloseStatus.PolicyViolation, "连接数超限，请稍后重试", CancellationToken.None);
            return;
        }
        _logger.LogInformation("WebSocket 连接建立: {ConnectionId} {MemberId}", connection.ConnectionId, memberId);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        connection.AbortSource = cts; // 让 ConnectionManager 可在会话吊销 / 禁用 / 改密时主动终止本连接
        try
        {
            await _hub.OnMemberConnectedAsync(memberId, cts.Token);
            await channel.Writer.WriteAsync(AguiJson.Serialize(new GroupConnectedEvent
            {
                ConnectionId = connection.ConnectionId,
                MemberId = memberId,
                Transport = "websocket",
                Timestamp = _hub.NowMs,
            }), cts.Token);

            var writerTask = WriteLoopAsync(ws, channel, cts.Token);
            var readerTask = ReadLoopAsync(ws, connection, cts.Token);
            await Task.WhenAny(writerTask, readerTask);
            cts.Cancel();
            try { await Task.WhenAll(writerTask, readerTask); } catch { /* 连接已关闭，忽略 */ }
        }
        catch (OperationCanceledException) { /* 客户端断开 */ }
        finally
        {
            _connections.Unregister(connection.ConnectionId);
            await _hub.OnMemberDisconnectedAsync(memberId);
            channel.Writer.TryComplete();
            if (ws.State is not (WebSocketState.Closed or WebSocketState.Aborted))
            {
                try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "connection closed", CancellationToken.None); }
                catch { }
            }
        }
    }

    /// <summary>令牌来源：Authorization: Bearer 头 → ?token= 查询参数（浏览器 WS 无法自定义头，故支持 query）。</summary>
    private static string? ResolveToken(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return header["Bearer ".Length..].Trim();
        var query = request.Query["token"].ToString();
        return string.IsNullOrEmpty(query) ? null : query;
    }

    private static async Task WriteLoopAsync(WebSocket ws, Channel<string> channel, CancellationToken ct)
    {
        try
        {
            await foreach (var json in channel.Reader.ReadAllAsync(ct))
            {
                if (ws.State != WebSocketState.Open) break;
                var bytes = Encoding.UTF8.GetBytes(json);
                await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
    }

    private async Task ReadLoopAsync(WebSocket ws, HubConnection connection, CancellationToken ct)
    {
        const int MaxInboundBytes = 2 * 1024 * 1024; // 单条上行消息大小上限（按字节累计，防多字节字符绕过字符数限制）
        var buffer = new byte[16 * 1024];
        // 跨帧增量 UTF-8 解码：多字节字符被分帧时逐帧独立解码会产生替换符损坏内容，
        // 用带内部状态的 Decoder 累积解码，保证分帧边界上的多字节序列正确还原（长度上限仍按字节计）
        var decoder = new UTF8Encoding(false, false).GetDecoder();
        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await ws.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close) break;
                if (result.MessageType != WebSocketMessageType.Text) continue;

                var totalBytes = 0;
                var sb = new StringBuilder();
                while (true)
                {
                    totalBytes += result.Count;
                    if (totalBytes > MaxInboundBytes)
                    {
                        _logger.LogWarning("WS 上行消息超过大小上限（{Max} 字节），断开连接：member={Member}", MaxInboundBytes, connection.MemberId);
                        await ws.CloseAsync(WebSocketCloseStatus.MessageTooBig, "message too large", ct);
                        return;
                    }
                    AppendDecoded(sb, decoder, buffer, result.Count, result.EndOfMessage);
                    if (result.EndOfMessage) break;
                    result = await ws.ReceiveAsync(buffer, ct);
                    if (result.MessageType != WebSocketMessageType.Text)
                    {
                        // 二进制 / 异常帧：跳过并断开（协议仅文本帧）
                        await ws.CloseAsync(WebSocketCloseStatus.InvalidMessageType, "text frames only", ct);
                        return;
                    }
                }
                await HandleInboundAsync(sb.ToString(), connection, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
    }

    /// <summary>把一帧字节经增量 Decoder 解码后追加到 StringBuilder（分帧边界的多字节序列由 Decoder 内部状态衔接；末帧 flush 收尾）。</summary>
    private static void AppendDecoded(StringBuilder sb, Decoder decoder, byte[] buffer, int count, bool flush)
    {
        var charCount = decoder.GetCharCount(buffer, 0, count, flush);
        var chars = new char[charCount];
        decoder.GetChars(buffer, 0, count, chars, 0, flush);
        sb.Append(chars);
    }

    private async Task HandleInboundAsync(string json, HubConnection connection, CancellationToken ct)
    {
        string? type;
        try
        {
            using var doc = JsonDocument.Parse(json);
            type = doc.RootElement.TryGetProperty("type", out var t) ? t.GetString() : null;
        }
        catch (JsonException)
        {
            await SendErrorAsync(connection, ErrorCodes.BadRequest, "消息不是合法 JSON 或缺少 type 字段", ct);
            return;
        }

        try
        {
            switch (type)
            {
                case EventTypes.GroupSubscribe:
                    await _hub.SubscribeAsync(connection, Deserialize<SubscribeRequest>(json).GroupIds, ct);
                    break;

                case EventTypes.GroupUnsubscribe:
                    await _hub.UnsubscribeAsync(connection, Deserialize<UnsubscribeRequest>(json).GroupIds, ct);
                    break;

                case EventTypes.GroupMessageSend:
                {
                    var req = Deserialize<GroupMessageSendRequest>(json);
                    req.UserId = connection.MemberId; // 以连接身份为准，防伪造
                    await _hub.SendMessageAsync(req, ct);
                    break;
                }

                case EventTypes.GroupMessageRecall:
                {
                    var req = Deserialize<GroupMessageRecallRequest>(json);
                    req.OperatorId = connection.MemberId;
                    await _hub.RecallMessageAsync(req, ct);
                    break;
                }

                case EventTypes.GroupMessageRegenerate:
                {
                    var req = Deserialize<GroupMessageRegenerateRequest>(json);
                    req.OperatorId = connection.MemberId;
                    await _hub.RegenerateMessageAsync(req, ct);
                    break;
                }

                case EventTypes.GroupTyping:
                {
                    var req = Deserialize<GroupTypingRequest>(json);
                    req.MemberId = connection.MemberId;
                    await _hub.BroadcastTypingAsync(req, ct);
                    break;
                }

                case EventTypes.GroupMessageRead:
                {
                    var req = Deserialize<GroupReadRequest>(json);
                    req.MemberId = connection.MemberId;
                    await _hub.BroadcastReadAsync(req, ct);
                    break;
                }

                case EventTypes.AgentInteractionResolve:
                {
                    var req = Deserialize<GroupInteractionResolveRequest>(json);
                    req.MemberId = connection.MemberId; // 决策者 = 连接身份（网关再校验 == 触发者）
                    var resolved = await _hub.ResolveAgentInteractionAsync(req, ct);
                    if (!resolved)
                        await SendErrorAsync(connection, ErrorCodes.BadRequest,
                            "交互请求不存在、已过期，或您不是该请求的决策者（仅触发者可交互）", ct);
                    break;
                }

                default:
                    _logger.LogDebug("忽略未知事件类型: {Type}", type);
                    break;
            }
        }
        catch (AguiProtocolException ex)
        {
            await SendErrorAsync(connection, ex.ErrorCode, ex.Message, ct);
        }
        catch (JsonException)
        {
            await SendErrorAsync(connection, ErrorCodes.BadRequest, "事件请求体格式错误", ct);
        }
    }

    private static T Deserialize<T>(string json)
        => AguiJson.Deserialize<T>(json) ?? throw new JsonException("无法反序列化");

    private async Task SendErrorAsync(HubConnection connection, string code, string message, CancellationToken ct)
    {
        var evt = new RunErrorEvent { ErrorCode = code, Message = message, Timestamp = _hub.NowMs };
        try { await connection.SendAsync(AguiJson.Serialize(evt), ct); } catch { }
    }
}
