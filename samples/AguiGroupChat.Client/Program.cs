using AguiGroupChat.Sdk;
using AguiGroupChat.Sdk.Models;

// 第三方接入 SDK 示例：演示如何用一个 AguiClient（HTTP）+ AguiRealtimeClient（WS）接入 Hub。
//
// 用法:
//   AguiGroupChat.Client --base http://localhost:5100 [--login zhangsan 123456] [--groupIds group_001] [--send "Hello"] [--seconds 20]
//   --login 可选：不提供则用 SDK 注册新账号再登录；提供则直接登录。
const string DefaultBase = "http://localhost:5100";

var baseUri = DefaultBase;
string? loginUser = "zhangsan", loginPass = "123456";
string? registerUser = null, registerPass = null;
var groupIds = new List<string> { "group_001" };
string? send = null;
var durationSeconds = 20;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--base" or "--ws" when i + 1 < args.Length: baseUri = args[++i]; break;
        case "--login" when i + 2 < args.Length: loginUser = args[++i]; loginPass = args[++i]; break;
        case "--register" when i + 2 < args.Length: registerUser = args[++i]; registerPass = args[++i]; break;
        case "--groupIds" when i + 1 < args.Length: groupIds = args[++i].Split(',').Where(s => s.Length > 0).ToList(); break;
        case "--send" when i + 1 < args.Length: send = args[++i]; break;
        case "--seconds" when i + 1 < args.Length: int.TryParse(args[++i], out durationSeconds); break;
        case "--help" or "-h":
            Console.WriteLine("用法: AguiGroupChat.Client [--base http://localhost:5100] [--login 用户名 密码] [--register 用户名 密码] [--groupIds g1,g2] [--send \"消息\"] [--seconds 20]");
            return;
    }
}

using var cts = new CancellationTokenSource();

var options = new AguiClientOptions { BaseUri = new Uri(baseUri) };

// 1) HTTP 客户端：登录 / 注册，取得会话令牌
using var client = new AguiClient(options);
AuthResponse auth;
if (registerUser is not null)
{
    auth = await client.RegisterAsync(registerUser, registerPass!);
    Console.WriteLine($"[注册成功] userId={auth.UserId}");
}
else
{
    auth = await client.LoginAsync(loginUser!, loginPass!);
    Console.WriteLine($"[登录成功] userId={auth.UserId} isAdmin={auth.IsAdmin}");
}
client.Token = auth.Token;

// 2) 实时客户端：复用同一令牌，连接 WebSocket 并订阅群
await using var realtime = new AguiRealtimeClient(options) { Token = auth.Token };
realtime.On<GroupConnectedEvent>(e => Console.WriteLine($"[握手] connectionId={e.ConnectionId}"));
realtime.On<TextMessageContentEvent>(e => Console.Write(e.Delta));
realtime.On<TextMessageEndEvent>(_ => Console.WriteLine());
realtime.On<TextMessageStartEvent>(e => Console.Write($"[{e.SenderNickname}] "));
realtime.On<GroupTypingEvent>(e => Console.WriteLine($"[输入中] {e.MemberId} isTyping={e.IsTyping}"));
realtime.AnyEvent += e => { if (send is null) Console.WriteLine($"[事件] {e.Type}"); };

await realtime.ConnectAsync(groupIds, cts.Token);
Console.WriteLine($"[已连接] 订阅 {string.Join(", ", groupIds)}");

// 可选：发送一条消息，@ 需求助手示例
if (send is not null)
{
    await realtime.SendMessageAsync(new GroupMessageSendRequest
    {
        GroupId = groupIds.FirstOrDefault() ?? "group_001",
        Content = send,
        Mentions = ["agent_prd"],
    });
    Console.WriteLine($"[已发送] {send}");
}

await Task.Delay(TimeSpan.FromSeconds(durationSeconds), cts.Token);
Console.WriteLine($"[结束] 监听 {durationSeconds} 秒后退出");
