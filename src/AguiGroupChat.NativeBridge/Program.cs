using AguiGroupChat.NativeBridge;

// 《本机工具桥》—— 隧道专用模式（内网穿透）：
// 在以“没有公网 IP 的内网机器”上运行，主动出站连到公网 Hub（反向隧道），为数字员工在本机执行客户端 shell 技能。
// 网关检测到该桥在线（平台级或逐员工）后，把客户端 shell 沿隧道下行推给本机执行，结果回传模型继续作答。
// 本桥只做隧道一件事，不含本地回环 HTTP 服务。

// 用法:
//   AguiGroupChat.NativeBridge --tunnel https://你的Hub域名 --tunnel-token <隧道令牌> [--agent <数字员工id>]
//     --tunnel      公网 Hub 基址（如 https://hub.example.com）
//     --tunnel-token 与 Hub 侧 NativeTunnel:Token（或逐 agent 令牌）一致的令牌
//     --agent       可选：不填 = 服务整个平台（scope=*）；填了只服务该数字员工
// 命令行 shell 交由 ShellRunner 在隔离沙箱目录运行 + 超时 + 输出截断。

static string GetArg(string[] args, string key, string def)
{
    for (int i = 0; i < args.Length - 1; i++)
        if (string.Equals(args[i], key, StringComparison.OrdinalIgnoreCase))
            return args[i + 1];
    return def;
}

string tunnelHub = GetArg(args, "--tunnel", "");           // 公网 Hub 基址（必填）
string tunnelAgent = GetArg(args, "--agent", "").Trim();   // 绑定的数字员工 id（可选；空 = 平台级）
string tunnelToken = GetArg(args, "--tunnel-token", "");   // Hub 侧隧道令牌（必填）
string tunnelClient = GetArg(args, "--client", "").Trim(); // 本机的客户端/机器标识（可选；默认取本机名，用于“按请求客户端路由”）

// 隧道必须指定 Hub 与令牌；缺一不可
if (string.IsNullOrWhiteSpace(tunnelHub) || string.IsNullOrWhiteSpace(tunnelToken))
{
    Console.Error.WriteLine("用法: AguiGroupChat.NativeBridge --tunnel <Hub基址> --tunnel-token <隧道令牌> [--agent <数字员工id>] [--client <机器名>]");
    Console.Error.WriteLine("  --tunnel       公网 Hub 基址（必填，如 https://hub.example.com）");
    Console.Error.WriteLine("  --tunnel-token 与 Hub 侧 NativeTunnel:Token 一致的令牌（必填）");
    Console.Error.WriteLine("  --agent        可选：不填=服务整个平台(*)；填了只服务该数字员工");
    Console.Error.WriteLine("  --client       可选：本机标识（默认取本机名）——不同客户端各自起桥后，可把请求路由到发起请求的那台机器");
    return 2;
}
string clientId = string.IsNullOrWhiteSpace(tunnelClient) ? Environment.MachineName : tunnelClient;

Console.WriteLine("====================================================");
Console.WriteLine("AguiGroupChat NativeBridge（隧道模式）已启动");
Console.WriteLine($"  隧道 Hub : {tunnelHub}");
Console.WriteLine($"  服务范围 : {(string.IsNullOrWhiteSpace(tunnelAgent) ? "整个平台(*)" : "数字员工 " + tunnelAgent)}");
Console.WriteLine($"  本机标识 : {clientId}（按客户端路由用）");
Console.WriteLine("  停止     : Ctrl+C");
Console.WriteLine("====================================================");

var tunnel = new NativeTunnelClient(tunnelHub, string.IsNullOrWhiteSpace(tunnelAgent) ? "*" : tunnelAgent, tunnelToken, clientId);
await tunnel.RunAsync(CancellationToken.None);

return 0;
