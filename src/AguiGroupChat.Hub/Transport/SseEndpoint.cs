using System.Threading.Channels;
using AguiGroupChat.Hub.Infra;
using AguiGroupChat.Hub.Messaging;
using AguiGroupChat.Hub.Models;
using AguiGroupChat.Hub.Options;
using AguiGroupChat.Hub.Users;

namespace AguiGroupChat.Hub.Transport;

/// <summary>
/// SSE 单向下行端点：/sse?memberId=user_1001[&amp;groupIds=group_001,group_002][&amp;token=...]。
/// 连接建立后先收到 GROUP_CONNECTED 握手（含 connectionId），
/// 之后可调用 POST /ag-ui/group/subscribe 动态增删订阅。
/// 事件格式：data: {json}\n\n，心跳为 SSE 注释行 ": ping"。
/// 身份解析与 WebSocket 端点一致：携带有效 token 时以令牌身份为准。
/// </summary>
public sealed class SseEndpoint
{
    private readonly ConnectionManager _connections;
    private readonly GroupHub _hub;
    private readonly GroupChatOptions _options;
    private readonly AuthService _auth;
    private readonly AuthOptions _authOptions;
    private readonly ILogger<SseEndpoint> _logger;

    public SseEndpoint(ConnectionManager connections, GroupHub hub, GroupChatOptions options, AuthService auth, AuthOptions authOptions, ILogger<SseEndpoint> logger)
    {
        _connections = connections;
        _hub = hub;
        _options = options;
        _auth = auth;
        _authOptions = authOptions;
        _logger = logger;
    }

    public async Task HandleAsync(HttpContext context)
    {
        // 跨站来源校验（防 CSWSH）：同源 / 白名单 / 无 Origin（非浏览器）放行，其余 403
        if (!OriginGuard.IsAllowed(context.Request, _authOptions))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new AguiError(ErrorCodes.GroupPermissionDenied,
                "跨站 EventSource 连接被拒绝（Origin 不在允许范围）"));
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

        var groupIds = (context.Request.Query["groupIds"].ToString() ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // 有界发送队列：慢客户端最多积压 2048 条，超出丢弃新事件（重连时经快照恢复），防无界积压内存膨胀。
        var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(2048)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.DropWrite,
        });
        var connection = new HubConnection
        {
            ConnectionId = IdGenerator.NewId(),
            MemberId = memberId,
            Transport = "sse",
            Sender = (json, ct) => channel.Writer.WriteAsync(json, ct).AsTask(),
        };
        // 连接数上限检查（同成员 ≤ 10 / 同 IP ≤ 50）：须在设置 SSE 响应头之前执行，
        // 否则响应已开始输出无法再改状态码——超限以 HTTP 429 拒绝并记日志（DoS 防护）
        if (!_connections.Register(connection, context.Connection.RemoteIpAddress?.ToString()))
        {
            _logger.LogWarning("SSE 连接被拒绝（连接数超限）：member={Member} ip={Ip}",
                memberId, context.Connection.RemoteIpAddress);
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            await context.Response.WriteAsJsonAsync(new AguiError(ErrorCodes.GroupPermissionDenied, "连接数超限，请稍后重试"));
            return;
        }

        // 独立的可取消源：链接 RequestAborted（客户端断开即终止），且能被 ConnectionManager 在会话吊销 / 禁用 / 改密时主动 Cancel。
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        connection.AbortSource = cts;

        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers.Connection = "keep-alive";
        await context.Response.Body.FlushAsync(cts.Token);
        _logger.LogInformation("SSE 连接建立: {ConnectionId} {MemberId}", connection.ConnectionId, memberId);

        try
        {
            await _hub.OnMemberConnectedAsync(memberId, cts.Token);
            await channel.Writer.WriteAsync(AguiJson.Serialize(new GroupConnectedEvent
            {
                ConnectionId = connection.ConnectionId,
                MemberId = memberId,
                Transport = "sse",
                Timestamp = _hub.NowMs,
            }), cts.Token);

            if (groupIds.Length > 0)
                await _hub.SubscribeAsync(connection, groupIds, cts.Token);

            await WriteLoopAsync(context.Response, channel, cts.Token);
        }
        catch (OperationCanceledException) { /* 客户端断开或服务端终止 */ }
        finally
        {
            _connections.Unregister(connection.ConnectionId);
            await _hub.OnMemberDisconnectedAsync(memberId);
            channel.Writer.TryComplete();
        }
    }

    /// <summary>令牌来源：Authorization: Bearer 头 → ?token= 查询参数。</summary>
    private static string? ResolveToken(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return header["Bearer ".Length..].Trim();
        var query = request.Query["token"].ToString();
        return string.IsNullOrEmpty(query) ? null : query;
    }

    private async Task WriteLoopAsync(HttpResponse response, Channel<string> channel, CancellationToken ct)
    {
        var heartbeat = TimeSpan.FromSeconds(Math.Max(1, _options.HeartbeatIntervalSeconds));
        while (!ct.IsCancellationRequested)
        {
            var readTask = channel.Reader.WaitToReadAsync(ct).AsTask();
            var delayTask = Task.Delay(heartbeat, ct);
            var completed = await Task.WhenAny(readTask, delayTask);

            if (completed == readTask)
            {
                if (!await readTask) break; // 通道已关闭
                while (channel.Reader.TryRead(out var json))
                    await response.WriteAsync($"data: {json}\n\n", ct);
                await response.Body.FlushAsync(ct);
            }
            else
            {
                await response.WriteAsync(": ping\n\n", ct);
                await response.Body.FlushAsync(ct);
            }
        }
    }
}
