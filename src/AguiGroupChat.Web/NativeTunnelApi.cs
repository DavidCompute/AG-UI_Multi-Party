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
            var configuredToken = ctx.RequestServices.GetRequiredService<NativeTunnelOptions>().Token;
            var agent = ctx.Request.Query["agent"].ToString();
            var token = ctx.Request.Query["token"].ToString();
            if (string.IsNullOrWhiteSpace(agent) || string.IsNullOrWhiteSpace(configuredToken)
                || !string.Equals(token, configuredToken, StringComparison.Ordinal))
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
            service.Complete(req.TaskId, req.Output, req.ErrorLog);
            return Results.Ok(new { received = true });
        });
    }
}

/// <summary>隧道配置（appsettings 的 "NativeTunnel" 节 / 环境变量，如 NativeTunnel__Token）。</summary>
public sealed class NativeTunnelOptions
{
    public string? Token { get; set; } = "";
}

/// <summary>桥执行完成回传的执行结果请求体。</summary>
public sealed record NativeTunnelResultRequest(string TaskId, string? Output, string? ErrorLog = null);
