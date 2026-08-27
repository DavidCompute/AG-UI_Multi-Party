# AguiGroupChat.Sdk — 第三方应用接入 SDK

[English](README.en.md) | **简体中文**

`AguiGroupChat.Sdk` 是 AG-UI 群聊扩展协议 Hub 的官方 .NET 客户端 SDK，面向需要**把第三方应用接入 Hub** 的开发者。
它封装了 Hub 暴露的全部对外能力，开发者无需关心 WebSocket / SSE / 鉴权等底层细节。

- **目标框架**：`net8.0` / `net10.0`（尽量宽的 TFM，.NET 8+ 应用可直接引用）
- **零外部运行时依赖**：仅使用 BCL 内建的 `System.Net.Http.Json` 与 `System.Net.WebSockets.Client`
- **接入方式**：HTTP 上行 API（请求 / 响应）+ 实时下行事件（WebSocket 全双工 / SSE 单向）

---

## 快速开始

### 1. 添加项目引用

```xml
<ItemGroup>
  <ProjectReference Include="..\src\AguiGroupChat.Sdk\AguiGroupChat.Sdk.csproj" />
</ItemGroup>
```

或引用打包后的 NuGet 包（`dotnet pack -c Release`）：

```sh
dotnet pack src/AguiGroupChat.Sdk -c Release
```

### 2. HTTP：登录 + 群聊

```csharp
using AguiGroupChat.Sdk;
using AguiGroupChat.Sdk.Models;

var options = new AguiClientOptions { BaseUri = new Uri("http://localhost:5100") };
using var client = new AguiClient(options);

// 注册（注册即登录）；v1.0.75 起不再播种演示账号 zhangsan/lisi，首次运行请先注册
var auth = await client.RegisterAsync("zhangsan", "123456");
client.Token = auth.Token;               // SDK 自动携带 Bearer 令牌

// 建群
var group = await client.CreateGroupAsync(new GroupCreateRequest
{
    GroupName = "新产品评审",
    OwnerId = auth.UserId!,
    MemberIds = ["user_1002"],
});

// 发消息并 @ 智能体
await client.SendMessageAsync(new GroupMessageSendRequest
{
    GroupId = group!.GroupId,
    UserId = auth.UserId,
    Content = "请评估 V2 需求",
    Mentions = ["agent_prd"],
});

// 拉历史 / 搜索
var history = await client.GetMessagesAsync(group.GroupId);
var hits     = await client.SearchMessagesAsync(group.GroupId, "V2");
```

### 3. 实时通道：订阅并接收推送

```csharp
await using var realtime = new AguiRealtimeClient(options) { Token = auth.Token };

realtime.On<GroupConnectedEvent>(e  => Console.WriteLine($"已连接 {e.ConnectionId}"));
realtime.On<TextMessageContentEvent>(e => Console.Write(e.Delta));   // 智能体流式正文
realtime.On<TextMessageEndEvent>(_   => Console.WriteLine());
realtime.On<GroupTypingEvent>(e      => Console.WriteLine($"[输入中] {e.MemberId}"));

await realtime.ConnectAsync(["group_001", "group_002"], CancellationToken.None);
// ConnectAsync 会自动订阅；之后也可增删：
await realtime.SubscribeAsync(["group_003"], ct);
await realtime.UnsubscribeAsync(["group_001"], ct);

// WS 全双工：直接经实时通道上行（等效 HTTP 写接口）
await realtime.SendMessageAsync(new GroupMessageSendRequest {
    GroupId = "group_001", Content = "hello", Mentions = ["agent_prd"],
});
```

### 4. 处理错误

```csharp
try
{
    await client.LoginAsync("bad", "bad");
}
catch (AguiException ex)
{
    Console.WriteLine($"{ex.Code}: {ex.Message} (HTTP {ex.StatusCode})");
    // 例如 USER_BAD_CREDENTIALS: ... (HTTP 401)
}
```

---

## 组件

| 类型 | 说明 |
|---|---|
| `AguiClient` | HTTP 客户端：身份认证、群组 / 成员 / 话题 / 消息 / 智能体 / 附件管理、SSE 动态订阅 |
| `AguiRealtimeClient` | 实时通道：WebSocket 全双工或 SSE 单向，强类型事件分发 + WS 上行 |
| `AguiClientOptions` | 配置 Hub 基址、令牌提供者、超时、传输方式 |
| `AguiException` / `AguiError` | 统一错误模型（协议错误码 + HTTP 状态码 + 原始响应） |
| `Models.*` | 请求 / 响应 DTO 与事件类型，与 Hub 的 JSON 线格式一致 |

### `AguiClient` 能力速览

- **认证**：`RegisterAsync` / `LoginAsync` / `LogoutAsync` / `GetCurrentUserAsync` / `ChangePasswordAsync` / `UpdateProfileAsync` / `ListUsersAsync`
- **群组**：`CreateGroupAsync` / `UpdateGroupAsync` / `DisbandGroupAsync` / `GetGroupSnapshotAsync` / `GetGroupMembersAsync` / `GetMyGroupsAsync`
- **话题 / 成员**：`CreateTopicAsync` / `DeleteTopicAsync` / `ClearTopicAsync` / `GetTopicsAsync` / `AddMembersAsync` / `RemoveMembersAsync` / `LeaveGroupAsync` / `UpdateMemberAsync`
- **消息**：`SendMessageAsync` / `RecallMessageAsync` / `RegenerateMessageAsync` / `StopAgentRunAsync` / `SendTypingAsync` / `SendReadAsync` / `GetMessagesAsync` / `GetTopicMessagesAsync` / `SearchMessagesAsync` / `StartDiscussionAsync` / `ResolveInteractionAsync`
- **智能体**：`ListAgentsAsync` / `CreateAgentAsync` / `UpdateAgentAsync` / `DeleteAgentAsync` / `RegisterAgentAsync` / `UnregisterAgentAsync`
- **附件**：`UploadAsync`
- **SSE 订阅**：`SubscribeSseAsync` / `UnsubscribeSseAsync`

### 实时事件分发

`AguiRealtimeClient.On<T>()` 按强类型订阅；`OnRaw(type, handler)` 订阅 Hub 新增的动态事件类型；
`AnyEvent` 订阅全部事件。全部事件模型（`GROUP_CONNECTED`、`TEXT_MESSAGE_START/CONTENT/END`、
`GROUP_STATE_SNAPSHOT`、`AGENT_INTERACTION_*`、`GROUP_TOPIC_*` 等）见 `Models/Events.cs`。

---

## 鉴权说明

- Hub 默认（`Auth:RequireTokenOnRealTime=true`）要求**有效会话令牌**；SDK 在登录 / 注册后自动在
  `Authorization: Bearer` 头携带令牌，`AguiClient.Token` 与 `AguiRealtimeClient.Token` 均可覆写。
- 实时通道连接时令牌写入 URL（`?token=`）或由 SDK 自动处理；WS 场景下服务端以**令牌身份**为准，
  SDK 无需（也不应）传递 `memberId`。
- 所有写接口发送者 / 操作者一律以服务端解析的令牌身份为**唯一可信来源**，SDK 传参仅为兼容回退模式。

---

## JSON 约定

SDK 使用与网关完全一致的序列化约定（`camelCase` 属性、忽略 `null`、枚举序列化为 `camelCase` 字符串），
与协议 §2 及网关的 `ConfigureHttpJsonOptions(JsonStringEnumConverter...)` 对齐。
若自建代理网关未配置枚举字符串化，请按同一约定配置，否则响应反序列化会抛 `AguiException(BAD_REQUEST)`。

---

## 示例

- 完整命令行示例见 [`samples/AguiGroupChat.Client`](../../samples/AguiGroupChat.Client)，演示登录 → HTTP 建群 / 发消息 → 实时订阅 → 流式接收智能体回复。
- 端到端集成测试见 [`tests/AguiGroupChat.Sdk.Tests`](../../tests/AguiGroupChat.Sdk.Tests)（自托管真实 Hub，含 WebSocket 全链路）。
