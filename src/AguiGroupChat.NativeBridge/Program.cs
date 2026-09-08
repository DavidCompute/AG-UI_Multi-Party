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
//   AguiGroupChat.NativeBridge --config bridge-config.txt
//       或
//   AguiGroupChat.NativeBridge --tunnel https://你的Hub域名 --tunnel-token <隧道令牌> \
//       [--agent <数字员工id>] [--client <机器名>] [--local-port 17321] [--allowed-origin http://host:5200] [--local-https]
//     --config        读取同目录/指定 bridge-config.txt（key=value，# 注释行），内含 SERVER/TOKEN/AGENT/CLIENT/LOCAL_PORT；
//                     TOKEN 可为 enc:v1: 密文（此时用同目录 bridge.key 或 --token-key 自动解密）
//     --token-key     显式解密密钥（base64 32 字节）；缺省从 --config 同目录 bridge.key 读取
//     --tunnel        公网 Hub 基址（可选；不配则取 --config 的 SERVER）
//     --tunnel-token  隧道令牌（可选；不配则取 --config 的 TOKEN，支持 enc:v1: 密文）
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

// 读 key=value 配置（# 注释、忽略空行），供 --config 使用。
static Dictionary<string, string> LoadConfigFile(string path)
{
    var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return map;
    foreach (var raw in File.ReadAllLines(path))
    {
        var line = raw.Trim();
        if (line.Length == 0 || line.StartsWith('#')) continue;
        var eq = line.IndexOf('=');
        if (eq <= 0) continue;
        map[line[..eq].Trim()] = line[(eq + 1)..].Trim();
    }
    return map;
}

string configPath = GetArg(args, "--config", "").Trim();
var cfg = LoadConfigFile(configPath);
// 命令行显式参数优先；缺省回落配置值（SERVER/TOKEN/AGENT/CLIENT/LOCAL_PORT）
string tunnelHub = GetArg(args, "--tunnel", cfg.GetValueOrDefault("SERVER", ""));     // 公网 Hub 基址
string tunnelAgent = GetArg(args, "--agent", cfg.GetValueOrDefault("AGENT", "")).Trim(); // 绑定的数字员工 id
string tunnelClient = GetArg(args, "--client", cfg.GetValueOrDefault("CLIENT", "")).Trim(); // 本机标识
int localPort = int.TryParse(GetArg(args, "--local-port", cfg.GetValueOrDefault("LOCAL_PORT", "17321")), out var p) ? p : 17321;
bool localHttps = GetArg(args, "--local-https", "") == "1";

// 令牌：支持明文（历史）与 enc:v1: 密文（网页下载安装包默认格式）。密钥=同目录 bridge.key 或 --token-key
string rawToken = GetArg(args, "--tunnel-token", cfg.GetValueOrDefault("TOKEN", ""));
string tokenKeyBase64 = GetArg(args, "--token-key", "").Trim();
string keyFile = string.IsNullOrEmpty(configPath) ? "" : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(configPath))!, "bridge.key");
string tunnelToken = BridgeTokenCipher.TryDecrypt(rawToken, tokenKeyBase64, keyFile, out var tokenErr) ?? "";
if (rawToken.StartsWith("enc:", StringComparison.Ordinal) && string.IsNullOrEmpty(tunnelToken))
{
    Console.Error.WriteLine($"令牌解密失败：{tokenErr}");
    Console.Error.WriteLine("  — 需将解压包里的 bridge.key 与 bridge-config.txt 放在同一目录，或用 --token-key <base64> 显式传入。");
    return 3;
}

// 隧道必须指定 Hub 与令牌；缺一不可（经命令行或 --config 提供）
if (string.IsNullOrWhiteSpace(tunnelHub) || string.IsNullOrWhiteSpace(tunnelToken))
{
    Console.Error.WriteLine("用法: AguiGroupChat.NativeBridge --config bridge-config.txt");
    Console.Error.WriteLine("   或: AguiGroupChat.NativeBridge --tunnel <Hub基址> --tunnel-token <令牌> [--agent <数字员工id>] [--client <机器名>] [--local-port <端口>]");
    Console.Error.WriteLine("  --config        配置文件（key=value，# 注释；内含 SERVER/TOKEN/AGENT/CLIENT/LOCAL_PORT）");
    Console.Error.WriteLine("  --token-key     显式解密密钥 base64（缺省读 --config 同目录 bridge.key）");
    Console.Error.WriteLine("  --tunnel        公网 Hub 基址（如 https://hub.example.com）");
    Console.Error.WriteLine("  --tunnel-token  隧道令牌（支持明文或 enc:v1: 加密格式）");
    Console.Error.WriteLine("  --agent         可选：不填=服务整个平台(*)；填了只服务该数字员工");
    Console.Error.WriteLine("  --client        可选：本机唯一标识（默认生成并持久化一个 UUID，避免机器名重名）——按请求来源路由到这台机器");
    Console.Error.WriteLine("  回环发现 : 1)本机浏览器读回环标识自动绑定“本机执行客户端”；2) --local-port <端口>(默认17321, 0关闭)；3) --local-https 用自签证书HTTPS（浏览器需信任）");
    return 2;
}
// 本机唯一标识：显式 --client 用之；否则用持久化的随机 UUID（机器名易重名，用 UUID 保证跨机器唯一、重启不变）
string clientId = string.IsNullOrWhiteSpace(tunnelClient) ? ClientIdStore.LoadOrCreate() : tunnelClient;
string agentScope = string.IsNullOrWhiteSpace(tunnelAgent) ? "*" : tunnelAgent;

Console.WriteLine("====================================================");
Console.WriteLine("AguiGroupChat NativeBridge（隧道模式）已启动");
Console.WriteLine($"  隧道 Hub : {tunnelHub}");
Console.WriteLine($"  服务范围 : {(agentScope == "*" ? "整个平台(*)" : "数字员工 " + tunnelAgent)}");
Console.WriteLine($"  本机标识 : {clientId}（按客户端路由用）");
if (localPort > 0)
    Console.WriteLine($"  回环发现 : {(localHttps ? "https" : "http")}://127.0.0.1:{localPort}/ag-ui/bridge/info  （仅本机可读；非敏感机器标识）");
else
    Console.WriteLine("  回环发现 : 已关闭（--local-port 0）");
Console.WriteLine("  停止     : Ctrl+C");
Console.WriteLine("====================================================");

var tunnel = new NativeTunnelClient(tunnelHub, agentScope, tunnelToken, clientId);

// 隧道（后台）+ 回环发现服务（前台阻塞）并行运行；任一退出视为进程结束
var tunnelTask = Task.Run(() => tunnel.RunAsync(CancellationToken.None));

if (localPort > 0)
{
    // 回环发现是可选的辅助服务：端口被占用/绑定失败时绝不能拖垮隧道——降级为仅隧道运行并提示。
    try
    {
        await RunLoopbackDiscoveryAsync(localPort, localHttps, clientId, agentScope);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[回环发现] 启动失败（端口 {localPort} 可能被占用），已降级为仅隧道运行：{ex.Message}");
        await tunnelTask;
    }
}
else
{
    await tunnelTask;
}

return 0;

/// <summary>极简回环发现服务：仅供同机浏览器读取本机桥的机器/客户端标识（非敏感），用于“本机执行客户端”自动绑定。
/// 仅监听回环 127.0.0.1；该端点只返回机器名等非敏感标识，并返回跨源可读，不含任何令牌 / 密钥等敏感数据。</summary>
static async Task RunLoopbackDiscoveryAsync(int port, bool useHttps, string clientId, string agentScope)
{
    var builder = WebApplication.CreateBuilder();
    builder.Logging.SetMinimumLevel(LogLevel.Warning);
    builder.WebHost.UseUrls(useHttps ? $"https://127.0.0.1:{port}" : $"http://127.0.0.1:{port}");
    if (useHttps)
        builder.WebHost.ConfigureKestrel(k => k.ConfigureHttpsDefaults(h => h.ServerCertificate = DevCert.CreateSelfSigned()));
    // 回环发现端点允许同机页面跨源读取：仅监听 127.0.0.1、只返回非敏感标识，对回环 GET 放开 CORS。
    // Chrome/Edge 对「从普通网页访问本机」有 Private Network Access(PNA) 预检——需在 OPTIONS 响应里带
    // Access-Control-Allow-Private-Network: true，否则即使 AAAO:* 也会被拦。
    var app = builder.Build();
    app.MapMethods("/ag-ui/bridge/{**rest}", ["OPTIONS"], (HttpContext ctx) =>
    {
        ctx.Response.Headers["Access-Control-Allow-Origin"] = "*";
        ctx.Response.Headers["Access-Control-Allow-Methods"] = "GET, OPTIONS";
        ctx.Response.Headers["Access-Control-Allow-Headers"] = "Content-Type, Authorization";
        ctx.Response.Headers["Access-Control-Allow-Private-Network"] = "true";
        ctx.Response.StatusCode = StatusCodes.Status204NoContent;
        return Task.CompletedTask;
    });
    app.MapGet("/ag-ui/bridge/info", (HttpContext ctx) =>
    {
        ctx.Response.Headers["Access-Control-Allow-Origin"] = "*";
        ctx.Response.Headers["Access-Control-Allow-Headers"] = "Content-Type";
        return Results.Json(new { client = clientId, agentScope, online = true, ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
    });
    app.MapGet("/ag-ui/bridge/health", (HttpContext ctx) =>
    {
        ctx.Response.Headers["Access-Control-Allow-Origin"] = "*";
        return Results.Json(new { status = "ok", client = clientId });
    });
    await app.RunAsync();
}
