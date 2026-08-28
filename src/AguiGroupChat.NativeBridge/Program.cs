using System.Net;
using AguiGroupChat.NativeBridge;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// 《本机工具桥》—— 在浏览器所在主机（如 aibook）上运行，让浏览器执行本机 shell 命令。
// Docker 版：服务器在别处，浏览器的 shell 工具必须由「浏览器所在主机」的本机进程执行，
// 本桥就是这个本机执行通道：它监听 loopback，接收前端带令牌的 POST，执行命令并返回输出。
//
// 用法: AguiGroupChat.NativeBridge [--port 17321] [--allowed-origin http://host:5200]
// 安全模型:
//   - 仅回环地址 (127.0.0.1) 监听，默认不暴露到局域网
//   - 令牌鉴权：启动时生成随机令牌（可用 --token 固定），浏览器请求须带 Authorization: Bearer
//   - CORS：AllowedOrigin 白名单（默认宽松关闭但依赖令牌；生产建议配置 Docker 页面源）
//   - 命令在专属沙箱目录运行 + 超时 + 输出截断（见 ShellRunner）

static string GetArg(string[] args, string key, string def)
{
    for (int i = 0; i < args.Length - 1; i++)
        if (string.Equals(args[i], key, StringComparison.OrdinalIgnoreCase))
            return args[i + 1];
    return def;
}

int port = int.TryParse(GetArg(args, "--port", "17321"), out var p) ? p : 17321;
string? fixedToken = GetArg(args, "--token", "");
string allowedOrigin = GetArg(args, "--allowed-origin", "");

// 令牌：未显式给定则生成随机（打印给用户看，浏览器侧需配置）
var token = string.IsNullOrWhiteSpace(fixedToken) ? Base64UrlToken.New(32) : fixedToken.Trim();

// 打印启动信息（前端需知道地址+令牌才能执行 shell）
Console.WriteLine("====================================================");
Console.WriteLine($"AguiGroupChat NativeBridge 已启动");
Console.WriteLine($"  监听地址 : http://127.0.0.1:{port}");
Console.WriteLine($"  前端配置 : 设置 → 本机工具桥地址 = http://127.0.0.1:{port}/ag-ui/client-tool");
Console.WriteLine($"  令牌     : {token}");
Console.WriteLine($"  允许来源 : {(string.IsNullOrEmpty(allowedOrigin) ? "（未配置，令牌鉴权兜底）" : allowedOrigin)}");
Console.WriteLine("  停止     : Ctrl+C");
Console.WriteLine("====================================================");

var builder = WebApplication.CreateBuilder(args);
builder.Logging.SetMinimumLevel(LogLevel.Warning); // 减少噪音
builder.WebHost.UseUrls($"http://127.0.0.1:{port}");

// CORS：仅放行配置的可信源（Docker 页面源）。AddCors 必须在 Build 前注册服务，否则 UseCors 运行时因解析不到 ICorsService 而崩溃。
if (!string.IsNullOrEmpty(allowedOrigin))
{
    builder.Services.AddCors(c =>
        c.AddDefaultPolicy(p => p.WithOrigins(allowedOrigin).AllowAnyHeader().AllowAnyMethod()));
}

var app = builder.Build();

if (!string.IsNullOrEmpty(allowedOrigin))
{
    // 未配置时也不开放任意源（依赖令牌 + 回环）
    app.UseCors();
}

var bridge = new ShellRunner();

app.MapPost("/ag-ui/client-tool", async (NativeBridgeRunRequest req, HttpContext ctx, CancellationToken ct) =>
{
    // 令牌鉴权
    var auth = ctx.Request.Headers.Authorization.ToString();
    if (!auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
        || !string.Equals(auth["Bearer ".Length..].Trim(), token, StringComparison.Ordinal))
        return Results.Json(new { output = (string?)null, error = "未授权：本机工具桥需要有效令牌" }, statusCode: StatusCodes.Status401Unauthorized);

    if (string.IsNullOrWhiteSpace(req.Command))
        return Results.BadRequest(new { error = "缺少要执行的 shell 命令（command）" });

    try
    {
        var output = await bridge.RunAsync(req.Command, req.Cwd, req.TimeoutSec, req.Query, ct);
        return Results.Ok(new { output });
    }
    catch (OperationCanceledException)
    {
        return Results.Json(new { output = (string?)null, error = "本机命令执行已取消（超时或连接中断）。" }, statusCode: StatusCodes.Status408RequestTimeout);
    }
    catch (Exception ex)
    {
        return Results.Json(new { output = (string?)null, error = "本机命令执行失败：" + ex.Message }, statusCode: StatusCodes.Status500InternalServerError);
    }
});

// 探活（供前端/运维判断桥是否可连）
app.MapGet("/ag-ui/native-bridge/health", () =>
    Results.Ok(new { status = "ok", host = Environment.MachineName }));

await app.RunAsync();

/// <summary>纯 URL 安全的随机 base64（令牌）。</summary>
file static class Base64UrlToken
{
    public static string New(int bytes)
        => Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(bytes))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>本机桥执行请求体。</summary>
public sealed record NativeBridgeRunRequest(
    string Kind,
    string? Command,
    string? Cwd,
    int? TimeoutSec,
    string? Query = null);
