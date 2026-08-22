using AguiGroupChat.Hub;
using AguiGroupChat.Hub.Options;
using Microsoft.Extensions.DependencyInjection;

// 关闭出站 HTTP 请求的 W3C trace 传播（不附加 traceparent 头）：部分 API 网关（如 DeepSeek）
// 对 traceparent 校验严格，曾出现带该头的请求被网关以 invalid header 拒绝。
AppContext.SetSwitch("System.Net.Http.EnableActivityPropagation", false);

var builder = HubApp.CreateBuilder(args);
HubApp.ConfigureServices(builder);
var app = builder.Build();
HubApp.MapEndpoints(app);

app.MapGet("/", () => Results.Text("""
    AG-UI 群聊扩展协议 Hub（v1.0）
    WebSocket: /ws?memberId=user_1001
    SSE:        /sse?memberId=user_1001&groupIds=group_001
    HTTP API:   /ag-ui/group/create 等（详见 README）
    """, "text/plain"));

// 恢复持久化状态；无历史数据且开启示例数据时才播种
var loaded = HubApp.InitializePersistence(app);
if (!loaded && app.Services.GetRequiredService<GroupChatOptions>().SeedSampleData)
    await HubApp.SeedSampleDataAsync(app);

app.Run();
