using System.Threading.Channels;
using AguiGroupChat.Agents;
using AguiGroupChat.Hub.Infra;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AguiGroupChat.Web;

/// <summary>
/// 基于 HTTP/SSE 的反向隧道入口（Hub 公网侧）：
/// <c>GET /ag-ui/native-tunnel/connect</c> 让内网本机桥主动连入并注册（绑定一个数字员工 agentId），
/// Hub 沿该 SSE 长连接下行「客户端技能」执行任务；桥执行后 <c>POST /ag-ui/native-tunnel/result</c> 回传结果。
/// 这样内网桥无需公网 IP，即可被公网 Hub / 数字员工网关调用执行本机 shell。
/// </summary>
public static class NativeTunnelApi
{
    public static void MapNativeTunnelApi(this WebApplication app)
    {
        // —— 桥连入（SSE 长连接）：内网桥主动出站发起，注册到隧道服务 ——
        app.MapGet("/ag-ui/native-tunnel/connect", async (HttpContext ctx, CancellationToken ct) =>
        {
            var service = ctx.RequestServices.GetRequiredService<NativeTunnelService>();
            var options = ctx.RequestServices.GetRequiredService<NativeTunnelOptions>();
            var agent = ctx.Request.Query["agent"].ToString();
            var token = ctx.Request.Query["token"].ToString();

            // 限流（单 IP 滑动窗口）：防暴力猜令牌 / DDoS——Hub 是公网端点
            var ipKey = ctx.Connection.RemoteIpAddress?.ToString() ?? "?";
            var limiter = ctx.RequestServices.GetRequiredService<NativeTunnelRateLimitBag>().Connect;
            if (!limiter.Allow(ipKey, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()))
            {
                ctx.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                await ctx.Response.WriteAsync("请求过于频繁，请稍后再试", ct);
                return;
            }
            // 鉴权：逐 agent 专属令牌优先，未配置时用全局令牌；agent 必须配置了有效令牌
            if (string.IsNullOrWhiteSpace(agent) || !options.HasTokenFor(agent) || !options.IsTokenValid(agent, token))
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await ctx.Response.WriteAsync("未授权：隧道令牌不匹配", ct);
                return;
            }

            // 每连接一个下行事件队列：service.Execute 经 Push 把任务写入；后台任务沿 SSE 写出
            var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(64)
            { FullMode = BoundedChannelFullMode.Wait });
            var bridgeId = "br_" + Guid.NewGuid().ToString("N")[..8];
            var conn = service.Register(agent, bridgeId, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                (payload, _taskId, c) => channel.Writer.WriteAsync(payload, c).AsTask());

            // SSE 响应头
            ctx.Response.ContentType = "text/event-stream";
            ctx.Response.Headers["Cache-Control"] = "no-cache";
            ctx.Response.Headers["Connection"] = "keep-alive";
            ctx.Response.Headers["X-Accel-Buffering"] = "no"; // 关闭反向代理缓冲（nginx 等），保证事件即时下行
            if (ctx.Response.HttpContext.Features.Get<IHttpResponseBodyFeature>() is { } body)
            {
                // 推送式资源：禁止中间件/框架缓冲整个响应（SSE 需边写边发）
            }

            var logger = ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("NativeTunnelApi");
            logger.LogInformation("内网桥经隧道注册：agent={Agent} bridge={Bridge}", agent, bridgeId);

            // 从下行队列逐条写到 SSE
            async Task Pump()
            {
                var enumerator = channel.Reader.ReadAllAsync(ct).GetAsyncEnumerator();
                await using (enumerator.ConfigureAwait(false))
                {
                    while (await enumerator.MoveNextAsync().ConfigureAwait(false))
                    {
                        var evt = enumerator.Current;
                        await ctx.Response.WriteAsync($"data: {evt}\n\n", ct).ConfigureAwait(false);
                        await ctx.Response.Body.FlushAsync(ct).ConfigureAwait(false);
                    }
                }
            }

            // 保持连接直到桥断开 / 取消
            try
            {
                var pump = Pump();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                await pump.ConfigureAwait(false);
            }
            catch (OperationCanceledException) { /* 客户端断开或请求取消 */ }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "隧道流异常：agent={Agent}", agent);
            }
            finally
            {
                service.DropAllForAgent(agent);
                logger.LogInformation("内网桥隧道断开：agent={Agent}", agent);
            }
        });

        // —— 桥回传执行结果 ——
        app.MapPost("/ag-ui/native-tunnel/result", (NativeTunnelResultRequest req, HttpContext ctx) =>
        {
            var service = ctx.RequestServices.GetRequiredService<NativeTunnelService>();
            var options = ctx.RequestServices.GetRequiredService<NativeTunnelOptions>();
            // 鉴权：结果回传必须带该 agent 的令牌，防伪造结果 / 无 token 刷接口（内网隧道桥用同一令牌 POST）
            if (string.IsNullOrWhiteSpace(req.TaskId)
                || !options.HasTokenFor(req.Agent) || !options.IsTokenValid(req.Agent, req.Token))
                return Results.Json(new { error = "未授权：结果回传令牌无效" }, statusCode: StatusCodes.Status401Unauthorized);
            // 限流：单 IP 滑动窗口，防 DDoS
            var ipKey = ctx.Connection.RemoteIpAddress?.ToString() ?? "?";
            var limiter = ctx.RequestServices.GetRequiredService<NativeTunnelRateLimitBag>().Result;
            if (!limiter.Allow(ipKey, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()))
                return Results.Json(new { error = "请求过于频繁" }, statusCode: StatusCodes.Status429TooManyRequests);

            service.Complete(req.TaskId, req.Output, req.ErrorLog);
            return Results.Ok(new { received = true });
        });
    }
}

/// <summary>已装配的隧道限流器（按 IOptions 值构建，供端点复用同一实例统计）。</summary>
public sealed class NativeTunnelRateLimitBag
{
    public SlidingRateLimiter Connect { get; }
    public SlidingRateLimiter Result { get; }

    public NativeTunnelRateLimitBag(NativeTunnelOptions options)
    {
        Connect = new SlidingRateLimiter(options.ConnectRateLimitPerMinute);
        Result = new SlidingRateLimiter(options.ResultRateLimitPerMinute);
    }
}

/// <summary>
/// 隧道配置（appsettings 的 "NativeTunnel" 节 / 环境变量，如 <c>NativeTunnel__Token</c>）。
/// 令牌鉴权：逐 agent 专属令牌（<c>NativeTunnel:AgentTokens__&lt;agentId&gt;</c>）优先，未配置时回落全局 <see cref="Token"/>。
/// </summary>
public sealed class NativeTunnelOptions
{
    /// <summary>全局隧道令牌（所有数字员工默认共用；逐 agent 配置后按 agent 严格匹配）。</summary>
    public string? Token { get; set; } = "";

    /// <summary>逐数字员工的专属隧道令牌（键为 agentId）。配置后，该普通员工注册 / 回传都必须用它自己的令牌。</summary>
    public Dictionary<string, string> AgentTokens { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>是否已为该 agent 配置有效令牌（全局或逐 agent）。</summary>
    public bool HasTokenFor(string? agent)
        => (!string.IsNullOrWhiteSpace(agent)
               && AgentTokens.TryGetValue(agent, out var aTok) && !string.IsNullOrWhiteSpace(aTok))
           || !string.IsNullOrWhiteSpace(Token);

    /// <summary>校验某 agent 的注册 / 回传令牌：逐 agent 专属令牌存在则必须严格匹配，否则用全局令牌兜底。</summary>
    public bool IsTokenValid(string? agent, string? token)
    {
        if (!string.IsNullOrWhiteSpace(agent)
            && AgentTokens.TryGetValue(agent, out var aTok) && !string.IsNullOrWhiteSpace(aTok))
            return string.Equals(token, aTok, StringComparison.Ordinal);
        return !string.IsNullOrWhiteSpace(Token) && string.Equals(token, Token, StringComparison.Ordinal);
    }

    /// <summary>connect 端点单 IP 每分钟最多尝试次数（防暴力猜测令牌 / DDoS）。</summary>
    public int ConnectRateLimitPerMinute { get; set; } = 120;

    /// <summary>result 端点单 IP 每分钟最多接收次数（防伪造结果刷接口 / DDoS）。</summary>
    public int ResultRateLimitPerMinute { get; set; } = 600;
}

/// <summary>无依赖的内存滑动窗口限流：按（键）统计窗口内调用次数，超限拒绝。用于公网隧道端点的 DDoS / 暴力防御。</summary>
public sealed class SlidingRateLimiter
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (long StartMs, int Count)> _buckets =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly long _windowMs;
    private readonly int _max;

    public SlidingRateLimiter(int perMinute)
    {
        _max = Math.Max(1, perMinute);
        _windowMs = 60_000;
    }

    /// <summary>本次调用是否被允许；超限返回 false。</summary>
    public bool Allow(string key, long nowMs)
    {
        while (true)
        {
            var (start, count) = _buckets.GetOrAdd(key, _ => (nowMs, 0));
            if (nowMs - start >= _windowMs) // 窗口过期：重置
            {
                if (_buckets.TryUpdate(key, (nowMs, 1), (start, count))) return true;
                continue; // 竞争重试
            }
            if (count >= _max) return false;
            if (_buckets.TryUpdate(key, (start, count + 1), (start, count))) return true;
        }
    }
}

/// <summary>桥执行完成回传的执行结果请求体。<c>Agent</c>/<c>Token</c> 用于鉴权，防止伪造结果。</summary>
public sealed record NativeTunnelResultRequest(string TaskId, string? Output, string? ErrorLog = null, string? Agent = null, string? Token = null);
