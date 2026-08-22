using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

// 示例 WS 客户端：
//   --ws        ws://localhost:5100/ws
//   --memberId  user_1001
//   --token     会话令牌（可选；Auth:RequireTokenOnRealTime=true 时必须）
//   --groupIds  group_001,group_002
//   --send      "可选：连接后发送一条群消息"
//   --seconds   监听时长（默认 20 秒）
const string DefaultWs = "ws://localhost:5100/ws";

var wsUrl = DefaultWs;
var memberId = "user_1001";
string? token = null;
var groupIds = new List<string> { "group_001" };
string? send = null;
var durationSeconds = 20;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--ws" when i + 1 < args.Length: wsUrl = args[++i]; break;
        case "--memberId" when i + 1 < args.Length: memberId = args[++i]; break;
        case "--token" when i + 1 < args.Length: token = args[++i]; break;
        case "--groupIds" when i + 1 < args.Length: groupIds = args[++i].Split(',').Where(s => s.Length > 0).ToList(); break;
        case "--send" when i + 1 < args.Length: send = args[++i]; break;
        case "--seconds" when i + 1 < args.Length: int.TryParse(args[++i], out durationSeconds); break;
        case "--help" or "-h":
            Console.WriteLine("用法: AguiGroupChat.Client [--ws ws://localhost:5100/ws] [--memberId user_1001] [--token xxx] [--groupIds group_001] [--send \"消息内容\"] [--seconds 20]");
            return;
    }
}

using var ws = new ClientWebSocket();
var uri = new Uri($"{wsUrl}{(wsUrl.Contains('?') ? "&" : "?")}memberId={memberId}" + (string.IsNullOrEmpty(token) ? "" : $"&token={Uri.EscapeDataString(token)}"));
await ws.ConnectAsync(uri, CancellationToken.None);
Console.WriteLine($"[连接成功] {uri}");

var recvTask = ReceiveLoopAsync(ws);

await Task.Delay(300);
await SendJsonAsync(ws, new
{
    type = "GROUP_SUBSCRIBE",
    groupIds,
    timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
});
Console.WriteLine($"[订阅] {string.Join(", ", groupIds)}");

if (send is not null)
{
    await Task.Delay(500);
    await SendJsonAsync(ws, new
    {
        type = "GROUP_MESSAGE_SEND",
        groupId = groupIds.FirstOrDefault() ?? "group_001",
        userId = memberId,
        content = send,
        mentions = new[] { "agent_prd" },
    });
    Console.WriteLine($"[发送] {send}");
}

await Task.Delay(TimeSpan.FromSeconds(durationSeconds));
Console.WriteLine($"[结束] 监听 {durationSeconds} 秒后退出");
ws.Dispose();
await recvTask;

async Task SendJsonAsync(ClientWebSocket socket, object payload)
{
    var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    await socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);
}

async Task ReceiveLoopAsync(ClientWebSocket socket)
{
    var buffer = new byte[64 * 1024];
    try
    {
        while (socket.State == WebSocketState.Open)
        {
            var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
            if (result.MessageType == WebSocketMessageType.Close) break;
            if (result.MessageType != WebSocketMessageType.Text) continue;
            Console.WriteLine($"[收到] {Encoding.UTF8.GetString(buffer, 0, result.Count)}");
        }
    }
    catch (WebSocketException ex)
    {
        Console.WriteLine($"[连接中断] {ex.Message}");
    }
}
