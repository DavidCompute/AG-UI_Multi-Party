using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AguiGroupChat.Agents;
using AguiGroupChat.Hub.Models;
using AguiGroupChat.Hub.Users;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace AguiGroupChat.Web;

/// <summary>
/// 客户端技能本机桥配置：<c>RequireAdmin</c>=true 时，本端点仅系统管理员可调用（共享多用户 Web 部署应开启，
/// 封堵「任意登录用户可在宿主机执行任意 shell」的 RCE 面）；默认 false 保持单机桌面 / 自托管的「本机执行」UX。
/// </summary>
public sealed class ClientToolOptions
{
    /// <summary>是否仅系统管理员可调用本机桥（共享多用户部署置 true）。默认 false（单机 / 自托管场景）。</summary>
    public bool RequireAdmin { get; set; }

    /// <summary>是否“宿主即用户本机”（桌面版自托管）。为 true 时，<c>ExecutionLocation=Client</c> 的 dotnet / shell 技能可
    /// 直接在 Web 宿主上执行（无需独立本机桥）——只有桌面 / 自托管应置 true；Docker + 远端浏览器必须保持 false（其宿主非用户机器）。
    /// 桌面版在其 <c>DesktopApp.Start</c> 里写入 <c>ClientTool:IsHostLocal=true</c>；Docker 环境默认 false。</summary>
    public bool IsHostLocal { get; set; }
}

/// <summary>
/// 客户端执行技能的本机桥 HTTP API：前端把「在客户端执行」的 shell 技能交到此端点执行（隔离工作目录 + 超时 + 输出截断）。
/// 复用 HITL 通道：先由 <see cref="AguiGroupChat.Agents.AgentGateway"/> 下发 <c>kind=client_tool</c> 交互卡，
/// 前端在自己的浏览器 / WebView 里点「在客户端执行」→ 本端点执行并回传 <c>toolResult</c> → 网关回灌模型继续。
/// HTTP 类客户端技能由前端直接用 <c>fetch</c> 执行（浏览器跨域 / 地址可达性由客户端自身决定），不经此桥。
///
/// <b>部署安全注意（共享多用户部署专属风险）</b>：本端点在本端点所在的宿主机上执行命令。<b>单机桌面 / 自托管</b>（
/// 首注册账号默认管理员、服务跑在操作者自己机器上）属设计内的「本地执行」，风险可接受。<b>共享多用户 Web 部署</b>下
/// 此处等同「任意登录用户可在服务器上执行任意 shell」的 RCE 面。共享多用户部署请<b>勿在宿主机启用本端点</b>，
/// 或改走 <see cref="AguiGroupChat.Agents.NativeTunnelService"/>（内网本机桥：token 鉴权、路由到各自机器执行）；
/// 若必须启用，请把 <see cref="ClientToolOptions.RequireAdmin"/> 置为 true，仅系统管理员可执行。
/// 命令由前端依技能 <c>ClientRunner</c> 回传，此处仍做 kind 白名单 / 长度上限 / 隔离目录 / 超时等护栏。
/// </summary>
public static class ClientToolBridgeApi
{
    // 单次执行最大时长（秒）：与前端默认一致，超时后强制终止进程树
    private const int MaxTimeoutSec = 30;
    // 单次返回最大输出字符数：防止把超大命令输出塞进模型上下文 / 回灌消息
    private const int MaxOutputChars = 12_000;
    // 单条 shell 命令最大字符数：本机桥仅承接 shell 客户端技能，拒绝超大 / 异常体量命令（防畸形请求放大）
    private const int MaxCommandChars = 16_384;

    public static void MapClientToolBridgeApi(this WebApplication app, string path = "/ag-ui/client-tool", ClientToolOptions? options = null)
    {
        // 默认行为：从 DI 取配置（Program.cs 注册的 ClientToolOptions 单例）；未注册（如测试夹具）或缺省时用默认；
        // 测试也可显式传入覆写以固定 RequireAdmin 取值
        var effective = options ?? app.Services.GetService<ClientToolOptions>() ?? new ClientToolOptions();
        var root = app.MapGroup(path);
        root.MapPost("/", async (ClientToolRunRequest req, HttpContext ctx, IWebHostEnvironment env, ILoggerFactory loggerFactory, AuthService auth, CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("AguiGroupChat.Web.ClientToolBridge");
            // 客户端执行技能一律需审批（网关侧已强制 RequiresApproval=true），此处仅作本机桥执行；
            // 命令正文来自技能定义 / 前端回传，运行于隔离沙箱目录。

            // 部署护栏：共享多用户部署开启 ClientTool:RequireAdmin 后，仅系统管理员可执行（封堵宿主机任意 shell 的 RCE 面）。
            // 默认关闭，保持单机桌面 / 自托管「本机执行」UX（首注册账号默认管理员）。
            var actorUserId = WebIdentity.UserId(ctx);
            if (effective.RequireAdmin && (actorUserId is null || !auth.IsAdmin(actorUserId)))
                return Results.Json(new AguiError(ErrorCodes.GroupPermissionDenied, "共享部署下仅系统管理员可执行客户端本机技能（ClientTool:RequireAdmin）"),
                    statusCode: StatusCodes.Status403Forbidden);

            // 仅承接 shell 客户端技能：回绝未知 / 其它类型（防未来扩展被误用；HTTP 类客户端技能由前端 fetch 直连，不经此桥）
            if (!string.Equals(req.Kind, "shell", StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = "本机桥仅支持 shell 类客户端技能执行" });
            if (string.IsNullOrWhiteSpace(req.Command))
                return Results.BadRequest(new { error = "缺少要执行的 shell 命令（command）" });
            if (req.Command.Length > MaxCommandChars)
                return Results.BadRequest(new { error = $"shell 命令超过长度上限（{MaxCommandChars} 字符）" });

            // 以请求者 userId 为沙箱隔离维度，避免不同账号写同一目录
            var userId = WebIdentity.UserId(ctx);
            var rootDir = Path.Combine(env.ContentRootPath, "data", "clienttoolruns", SanitizeSegment(userId ?? "anonymous"));
            Directory.CreateDirectory(rootDir);

            string output;
            try
            {
                output = await RunShellAsync(rootDir, req.Command, req.Cwd, req.TimeoutSec, req.Query, ct);
                ClientToolTrace.Write($"BRIDGE-OK cmd={req.Command} outputLen={output.Length} outputHead={output.Substring(0, Math.Min(120, output.Length)).Replace(Environment.NewLine, " ")}");
            }
            catch (OperationCanceledException)
            {
                ClientToolTrace.Write($"BRIDGE-CANCEL cmd={req.Command}");
                return Results.Json(new { output = null as string, error = "客户端技能执行已取消（超时或连接中断）。" }, statusCode: StatusCodes.Status408RequestTimeout);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "客户端技能（shell）执行失败：{Cmd}", req.Command);
                ClientToolTrace.Write($"BRIDGE-ERR cmd={req.Command} error={ex.Message}");
                return Results.Json(new { output = null as string, error = "客户端技能执行失败：" + ex.Message }, statusCode: StatusCodes.Status500InternalServerError);
            }

            return Results.Ok(new { output });
        }).AddEndpointFilter(new WebIdentity.RequireIdentityFilter());
    }

    private static async Task<string> RunShellAsync(string rootDir, string command, string? cwd, int? timeoutSec, string? query, CancellationToken ct)
        => await HostShell.RunAsync(rootDir, command, cwd, timeoutSec, query, ct);

    private static string SanitizeSegment(string s) => HostShell.SanitizeSegment(s);
}

/// <summary>客户端（shell）工具本机桥执行请求。</summary>
public sealed record ClientToolRunRequest(
    string Kind,
    string? Command,
    string? Cwd,
    int? TimeoutSec,
    string? Query = null);
