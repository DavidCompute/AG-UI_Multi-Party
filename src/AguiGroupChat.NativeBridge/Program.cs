using AguiGroupChat.NativeBridge;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

// 《本机工具桥》—— 隧道模式（内网穿透）+ 本机回环服务：
// 1) 反向隧道：主动出站连到平台 Hub，为数字员工在本机执行客户端 shell/dotnet 技能。
// 2) 回环服务（127.0.0.1）：同机浏览器读取本机桥标识；并在用户登录平台后，由平台网页
//    在线下发连接配置（POST /ag-ui/bridge/setup）或断开（POST /ag-ui/bridge/teardown）。
//
// 使用方式（两种模式）：
//   A. 待配置模式（默认）：不带隧道参数启动 → 只监听回环；平台网页在用户登录后自动下发
//      { server, setupToken } → 桥保存配置（加密）并连接；登出时网页调 teardown → 断开并清除配置。
//   B. 静态配置（旧用法/手动）：--config bridge-config.txt 或 --tunnel/--tunnel-token → 启动即连。
//
// 安全模型：
//   - 回环仅监听 127.0.0.1；写端点通过 CORS 预检要求 JSON + 自定义头，避免陌生网页直写本机桥；
//   - setup 携带的 setupToken 是服务器签发的“绑定型令牌”（登录用户从平台 API 领取，一次性、
//     首次连接绑定 client、可吊销）。桥自身不校验令牌内容——由服务器在连接时鉴权绑定。
//   - teardown 删除本机保存的配置：登出即断开，重启不会带着旧配置自动连。
//
// 选项:
//   --config <file> / --token-key / --tunnel / --tunnel-token / --agent / --client
//   --local-port <默认17321, 0关闭> / --local-https

static string GetArg(string[] args, string key, string def)
{
    for (int i = 0; i < args.Length - 1; i++)
        if (string.Equals(args[i], key, StringComparison.OrdinalIgnoreCase))
            return args[i + 1];
    return def;
}

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
string tunnelHub = GetArg(args, "--tunnel", cfg.GetValueOrDefault("SERVER", ""));
string tunnelAgent = GetArg(args, "--agent", cfg.GetValueOrDefault("AGENT", "")).Trim();
string tunnelClientArg = GetArg(args, "--client", cfg.GetValueOrDefault("CLIENT", "")).Trim();
int localPort = int.TryParse(GetArg(args, "--local-port", cfg.GetValueOrDefault("LOCAL_PORT", "17321")), out var p) ? p : 17321;
bool localHttps = GetArg(args, "--local-https", "") == "1";

// 令牌：支持明文（历史）与 enc:v1: 密文。密钥=同目录 bridge.key 或 --token-key
string rawToken = GetArg(args, "--tunnel-token", cfg.GetValueOrDefault("TOKEN", ""));
string tokenKeyBase64 = GetArg(args, "--token-key", "").Trim();
string? keyFile = null;
if (rawToken.StartsWith("enc:", StringComparison.Ordinal))
{
    keyFile = !string.IsNullOrEmpty(configPath)
        ? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(configPath))!, "bridge.key")
        : (tokenKeyBase64.Length > 0 ? null : null);
}
string tunnelToken = BridgeTokenCipher.TryDecrypt(rawToken, tokenKeyBase64, keyFile, out var tokenErr) ?? "";
if (rawToken.StartsWith("enc:", StringComparison.Ordinal) && string.IsNullOrEmpty(tunnelToken))
{
    Console.Error.WriteLine($"令牌解密失败：{tokenErr}");
    Console.Error.WriteLine("  — 需将 bridge.key 与 bridge-config.txt 放在同一目录，或用 --token-key <base64> 显式传入。");
    return 3;
}

// 本机唯一标识：显式 --client / 配置 CLIENT 用之；否则用持久化的随机 UUID
string clientId = !string.IsNullOrWhiteSpace(tunnelClientArg) ? tunnelClientArg : ClientIdStore.LoadOrCreate();
string agentScope = string.IsNullOrWhiteSpace(tunnelAgent) ? "*" : tunnelAgent;

// —— 桥运行状态（隧道生命周期管理，供回环端点启停）——
var runtime = new BridgeRuntime
{
    ClientId = clientId,
    AgentScope = agentScope,
    ConfigPath = configPath,
    ConfigDir = !string.IsNullOrEmpty(configPath)
        ? (Path.GetDirectoryName(Path.GetFullPath(configPath)) ?? ".")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AguiGroupChat", "NativeBridge"),
};

// 模式 B：显式隧道参数（或启动时已有配置文件且含 SERVER/TOKEN）→ 启动即连（自启 / 手动）
if (!string.IsNullOrWhiteSpace(tunnelHub) && !string.IsNullOrWhiteSpace(tunnelToken))
    runtime.Connect(tunnelHub, tunnelToken, agentScope, clientId);

Console.WriteLine("====================================================");
Console.WriteLine("AguiGroupChat NativeBridge 已启动");
Console.WriteLine($"  本机标识 : {clientId}");
Console.WriteLine($"  连接状态 : {(runtime.IsConnected ? "已连入 " + runtime.ServerUrl : "待配置（登录平台网页后自动连接）")}");
if (localPort > 0)
    Console.WriteLine($"  回环服务 : {(localHttps ? "https" : "http")}://127.0.0.1:{localPort}/ag-ui/bridge/info");
Console.WriteLine("  停止     : Ctrl+C");
Console.WriteLine("====================================================");

if (localPort <= 0)
{
    // 无回环服务：只能静态连接；未连接则无事可做
    if (!runtime.IsConnected)
    {
        Console.Error.WriteLine("未提供隧道参数且回环服务已关闭（--local-port 0）：请提供 --tunnel/--tunnel-token 或保留回环以在线配置。");
        return 2;
    }
    await runtime.WaitAsync();
    return 0;
}

try
{
    await RunLoopbackServiceAsync(localPort, localHttps, runtime);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"[回环服务] 启动失败（端口 {localPort} 可能被占用），仅保留已配置的隧道：{ex.Message}");
    if (runtime.IsConnected) await runtime.WaitAsync();
    return 1;
}
return 0;

/// <summary>回环服务：info 只读；setup 接收 {server, setupToken} 后连接；teardown 断开并清除本机配置。</summary>
static async Task RunLoopbackServiceAsync(int port, bool useHttps, BridgeRuntime runtime)
{
    var builder = WebApplication.CreateBuilder();
    builder.Logging.SetMinimumLevel(LogLevel.Warning);
    builder.WebHost.UseUrls(useHttps ? $"https://127.0.0.1:{port}" : $"http://127.0.0.1:{port}");
    if (useHttps)
        builder.WebHost.ConfigureKestrel(k => k.ConfigureHttpsDefaults(h => h.ServerCertificate = DevCert.CreateSelfSigned()));
    var app = builder.Build();

    // 预检：允许同机/本平台页面跨源；写端点要求 application/json + 自定义头，陌生网页无法直写。
    app.MapMethods("/ag-ui/bridge/{**rest}", ["OPTIONS"], (HttpContext ctx) =>
    {
        var origin = ctx.Request.Headers.Origin.ToString();
        ctx.Response.Headers["Access-Control-Allow-Origin"] = string.IsNullOrEmpty(origin) ? "*" : origin;
        ctx.Response.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
        ctx.Response.Headers["Access-Control-Allow-Headers"] = "Content-Type, Authorization, X-Agui-Bridge";
        ctx.Response.Headers["Access-Control-Allow-Private-Network"] = "true";
        ctx.Response.StatusCode = StatusCodes.Status204NoContent;
        return Task.CompletedTask;
    });

    app.MapGet("/ag-ui/bridge/info", (HttpContext ctx) =>
    {
        var origin = ctx.Request.Headers.Origin.ToString();
        ctx.Response.Headers["Access-Control-Allow-Origin"] = string.IsNullOrEmpty(origin) ? "*" : origin;
        return Results.Json(new
        {
            client = runtime.ClientId,
            agentScope = runtime.AgentScope,
            online = true,
            configured = runtime.IsConfigured,
            connected = runtime.IsConnected,
            server = runtime.ServerUrl,
            ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });
    });

    // 登录后网页下发连接配置：{ server, setupToken }
    app.MapPost("/ag-ui/bridge/setup", async (HttpContext ctx) =>
    {
        var origin = ctx.Request.Headers.Origin.ToString();
        ctx.Response.Headers["Access-Control-Allow-Origin"] = string.IsNullOrEmpty(origin) ? "*" : origin;
        try
        {
            // Web 默认大小写不敏感：前端发 camelCase { server, setupToken }
            var body = await System.Text.Json.JsonSerializer.DeserializeAsync<SetupRequest>(ctx.Request.Body,
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
            if (body is null || string.IsNullOrWhiteSpace(body.Server) || string.IsNullOrWhiteSpace(body.SetupToken))
                return Results.BadRequest(new { error = "缺少 server 或 setupToken" });
            var server = body.Server.Trim();
            if (!server.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                && !server.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = "server 不是合法的 http(s) 地址" });

            runtime.ConfigureAndConnect(server, body.SetupToken.Trim());
            return Results.Ok(new { accepted = true, connected = runtime.IsConnected, client = runtime.ClientId });
        }
        catch (Exception ex)
        {
            return Results.BadRequest(new { error = "解析失败：" + ex.Message });
        }
    });

    // 登出时网页调用：断开隧道并清除本机保存的配置（重启不自连）
    app.MapPost("/ag-ui/bridge/teardown", (HttpContext ctx) =>
    {
        var origin = ctx.Request.Headers.Origin.ToString();
        ctx.Response.Headers["Access-Control-Allow-Origin"] = string.IsNullOrEmpty(origin) ? "*" : origin;
        runtime.Disconnect();
        return Results.Ok(new { disconnected = true, configured = false, connected = false });
    });

    app.MapGet("/ag-ui/bridge/health", (HttpContext ctx) =>
    {
        var origin = ctx.Request.Headers.Origin.ToString();
        ctx.Response.Headers["Access-Control-Allow-Origin"] = string.IsNullOrEmpty(origin) ? "*" : origin;
        return Results.Json(new { status = "ok", client = runtime.ClientId, connected = runtime.IsConnected });
    });

    await app.RunAsync();
}

/// <summary>setup 请求体：server = 平台地址；setupToken = 登录用户从平台 API 领取的绑定型令牌。</summary>
public sealed record SetupRequest(string? Server, string? SetupToken);

/// <summary>
/// 桥运行状态：管理隧道连接（连接/断开），并在 setup 时把配置（加密令牌）写入本机，供重启自连。
/// 静态连接（--tunnel）只连接不写盘；网页 setup 写盘并连接；登出 teardown 断开并删除配置。
/// </summary>
public sealed class BridgeRuntime
{
    public required string ClientId { get; init; }
    public required string AgentScope { get; init; }
    public required string ConfigPath { get; init; }
    public required string ConfigDir { get; init; }

    private readonly object _lock = new();
    private CancellationTokenSource? _cts;
    private Task? _tunnelTask;
    private string _server = "";
    private bool _hasConfigFile; // setup 已写盘（或启动来自已有配置）

    public bool IsConnected { get { lock (_lock) return _tunnelTask is { IsCompleted: false }; } }

    public bool IsConfigured
    {
        get
        {
            lock (_lock) return _hasConfigFile;
        }
    }

    public string ServerUrl { get { lock (_lock) return _server; } }

    /// <summary>静态连接（命令行 / 启动时已有完整配置）：连上但不改本机配置文件。</summary>
    public void Connect(string server, string token, string agentScope, string clientId)
    {
        lock (_lock)
        {
            _server = server.TrimEnd('/');
            _hasConfigFile = true;
            StartTunnelLocked(server, token, agentScope, clientId);
        }
    }

    /// <summary>网页 setup：把配置加密写盘（供重启自连）并立即连接。</summary>
    public void ConfigureAndConnect(string server, string setupToken)
    {
        lock (_lock)
        {
            _server = server.TrimEnd('/');
            Directory.CreateDirectory(ConfigDir);
            var enc = BridgeTokenCipher.EncryptToken(setupToken, out var keyB64);
            var cfgText =
                "# AguiGroupChat NativeBridge connection config (auto-configured by the platform page).\n" +
                "SERVER=" + _server + "\n" +
                "TOKEN=" + enc + "\n" +
                "AGENT=\nCLIENT=\nLOCAL_PORT=17321\n";
            File.WriteAllText(ConfigFile(), cfgText);
            File.WriteAllText(KeyFile(), keyB64 + "\n");
            _hasConfigFile = true;
            StartTunnelLocked(server, setupToken, AgentScope, ClientId);
        }
    }

    /// <summary>登出断开：停止隧道并删除本机配置（重启回到“待配置”）。</summary>
    public void Disconnect()
    {
        lock (_lock)
        {
            StopTunnelLocked();
            _server = "";
            _hasConfigFile = false;
            foreach (var f in new[] { ConfigFile(), KeyFile() })
            {
                try { if (File.Exists(f)) File.Delete(f); } catch { /* 忽略清理失败 */ }
            }
        }
    }

    public async Task WaitAsync()
    {
        Task? t;
        lock (_lock) t = _tunnelTask;
        if (t is not null) await t;
        else await Task.Delay(Timeout.Infinite); // 待配置：挂起直到 Ctrl+C
    }

    // ================ 内部 ================

    private void StartTunnelLocked(string server, string token, string agentScope, string clientId)
    {
        StopTunnelLocked();
        _cts = new CancellationTokenSource();
        var client = new NativeTunnelClient(server, agentScope, token, clientId);
        _tunnelTask = Task.Run(() => client.RunAsync(_cts.Token));
        Console.WriteLine($"[隧道] 目标：{server}（{agentScope}）——已发起连接");
    }

    private void StopTunnelLocked()
    {
        try { _cts?.Cancel(); } catch { /* 忽略 */ }
        _cts = null;
        _tunnelTask = null;
    }

    private string ConfigFile() => !string.IsNullOrWhiteSpace(ConfigPath)
        ? Path.GetFullPath(ConfigPath)
        : Path.Combine(ConfigDir, "bridge-config.txt");

    private string KeyFile() => Path.Combine(ConfigDir, "bridge.key");
}
