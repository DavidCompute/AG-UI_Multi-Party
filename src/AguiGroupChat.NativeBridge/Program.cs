using System.Text.Json;
using AguiGroupChat.NativeBridge;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// 《本机工具桥》—— 隧道专用模式（内网穿透）+ 本机回环发现服务：
// 1) 反向隧道：在以“没有公网 IP 的内网机器”上运行，主动出站连到公网 Hub，为数字员工在本机执行客户端 shell 技能。
// 2) 回环发现：同一台机器上的浏览器读取本机桥的机器/客户端标识，用于“本机执行客户端”自动绑定
//    （这样浏览器无需手填机器名，即可把发起的客户端 shell 路由到这台机器的桥）。
// 本桥不含旧版的「前端直接调用的客户端工具执行端点」。
//
// 用法:
//   AguiGroupChat.NativeBridge --tunnel https://你的Hub域名 --tunnel-token <隧道令牌> \
//       [--agent <数字员工id>] [--client <机器名>] [--local-port 17321] [--allowed-origin http://host:5200] [--local-https]
//     --tunnel        公网 Hub 基址（必填，如 https://hub.example.com）
//     --tunnel-token  与 Hub 侧 NativeTunnel:Token（或逐 agent 令牌）一致的令牌（必填）
//     --agent         可选：不填 = 服务整个平台（scope=*）；填了只服务该数字员工
//     --client        可选：本机标识（默认取本机名）——按“请求来自哪台客户端”路由到这台机器
//     --local-port    可选：本机回环发现服务端口（默认 17321）；0 = 关闭回环发现
//     --allowed-origin 可选：允许读取回环信息的网页来源（CORS 白名单；不配则仅同源可读）
//     --local-https   可选：回环发现服务用 HTTPS（自签证书；浏览器需信任该证书才能读取）
// 命令行 shell 交由 ShellRunner 在隔离沙箱目录运行 + 超时 + 输出截断。

static string GetArg(string[] args, string key, string def)
{
    for (int i = 0; i < args.Length - 1; i++)
        if (string.Equals(args[i], key, StringComparison.OrdinalIgnoreCase))
            return args[i + 1];
    return def;
}

string tunnelHub = GetArg(args, "--tunnel", "");                      // 公网 Hub 基址（必填）
string tunnelAgent = GetArg(args, "--agent", "").Trim();              // 绑定的数字员工 id（可选；空 = 平台级）
string tunnelToken = GetArg(args, "--tunnel-token", "");              // Hub 侧隧道令牌（必填）
string tunnelClient = GetArg(args, "--client", "").Trim();            // 本机标识（可选；默认取本机名）
int localPort = int.TryParse(GetArg(args, "--local-port", "17321"), out var p) ? p : 17321;
string allowedOrigin = GetArg(args, "--allowed-origin", "");          // 回环发现 CORS 白名单（可选）
bool localHttps = GetArg(args, "--local-https", "") == "1";

// 隧道必须指定 Hub 与令牌；缺一不可
if (string.IsNullOrWhiteSpace(tunnelHub) || string.IsNullOrWhiteSpace(tunnelToken))
{
    Console.Error.WriteLine("用法: AguiGroupChat.NativeBridge --tunnel <Hub基址> --tunnel-token <隧道令牌> [--agent <数字员工id>] [--client <机器名>] [--local-port <端口>] [--allowed-origin <网页源>] [--local-https]");
    Console.Error.WriteLine("  --tunnel        公网 Hub 基址（必填，如 https://hub.example.com）");
    Console.Error.WriteLine("  --tunnel-token  与 Hub 侧 NativeTunnel:Token 一致的令牌（必填）");
    Console.Error.WriteLine("  --agent         可选：不填=服务整个平台(*)；填了只服务该数字员工");
    Console.Error.WriteLine("  --client        可选：本机标识（默认取本机名）——按请求来源路由到这台机器");
    Console.Error.WriteLine("  --local-port    可选：本机回环发现端口（默认 17321；0=关闭）");
    Console.Error.WriteLine("  --allowed-origin 可选：允许读回环信息的网页来源 CORS 白名单");
    Console.Error.WriteLine("  --local-https   可选：回环发现用 HTTPS（自签证书，浏览器需信任）");
    return 2;
}
string clientId = string.IsNullOrWhiteSpace(tunnelClient) ? Environment.MachineName : tunnelClient;
string agentScope = string.IsNullOrWhiteSpace(tunnelAgent) ? "*" : tunnelAgent;

Console.WriteLine("====================================================");
Console.WriteLine("AguiGroupChat NativeBridge（隧道模式）已启动");
Console.WriteLine($"  隧道 Hub : {tunnelHub}");
Console.WriteLine($"  服务范围 : {(agentScope == "*" ? "整个平台(*)" : "数字员工 " + tunnelAgent)}");
Console.WriteLine($"  本机标识 : {clientId}（按客户端路由用）");
if (localPort > 0)
    Console.WriteLine($"  回环发现 : {(localHttps ? "https" : "http")}://127.0.0.1:{localPort}/ag-ui/bridge/info  允许来源={(string.IsNullOrWhiteSpace(allowedOrigin) ? "仅同源" : allowedOrigin)}");
else
    Console.WriteLine("  回环发现 : 已关闭（--local-port 0）");
Console.WriteLine("  停止     : Ctrl+C");
Console.WriteLine("====================================================");

var tunnel = new NativeTunnelClient(tunnelHub, agentScope, tunnelToken, clientId);

// 隧道（后台）+ 回环发现服务（前台阻塞）并行运行；任一退出视为进程结束
var tunnelTask = Task.Run(() => tunnel.RunAsync(CancellationToken.None));

if (localPort > 0)
{
    await RunLoopbackDiscoveryAsync(localPort, allowedOrigin, localHttps, clientId, agentScope, tunnel);
}
else
{
    await tunnelTask;
}

return 0;

/// <summary>极简回环发现服务：仅供同机浏览器读取本机桥的机器/客户端标识（非敏感），用于“本机执行客户端”自动绑定。
/// 防护：仅回环 127.0.0.1 监听 + CORS 白名单（限制被哪个网页源读取）；不含任何令牌 / 密钥等敏感数据。</summary>
static async Task RunLoopbackDiscoveryAsync(int port, string? allowedOrigin, bool useHttps, string clientId, string agentScope, NativeTunnelClient tunnel)
{
    var builder = WebApplication.CreateBuilder();
    builder.Logging.SetMinimumLevel(LogLevel.Warning);
    builder.WebHost.UseUrls(useHttps ? $"https://127.0.0.1:{port}" : $"http://127.0.0.1:{port}");
    if (useHttps)
        builder.WebHost.ConfigureKestrel(k => k.ConfigureHttpsDefaults(h => h.ServerCertificate = DevCert.CreateSelfSigned()));
    // CORS：仅放行配置的可信来源（网页源）；不加则仅同源可读（浏览器从非本页源读取会被跨域拦截）
    if (!string.IsNullOrWhiteSpace(allowedOrigin))
        builder.Services.AddCors(c => c.AddDefaultPolicy(p => p.WithOrigins(allowedOrigin).AllowAnyHeader()));
    var app = builder.Build();
    if (!string.IsNullOrWhiteSpace(allowedOrigin)) app.UseCors();
    app.MapGet("/ag-ui/bridge/info", () => Results.Json(new
    {
        client = clientId,
        agentScope,
        online = true,
        ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
    }));
    // 由隧道状态动态反映在线与否
    app.MapGet("/ag-ui/bridge/health", () => Results.Json(new { status = "ok", client = clientId }));
    await app.RunAsync();
}
