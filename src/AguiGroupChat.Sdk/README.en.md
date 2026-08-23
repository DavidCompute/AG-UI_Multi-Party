# AguiGroupChat.Sdk — SDK for Third-Party Application Integration

**English** | [简体中文](README.md)

`AguiGroupChat.Sdk` is the official .NET client SDK for the AG-UI Group Chat Hub, aimed at developers who need to **integrate third-party applications into the Hub**.
It wraps all external capabilities exposed by the Hub, so developers don't have to deal with low-level details such as WebSocket / SSE / authentication.

- **Target frameworks**: `net8.0` / `net10.0` (a broad TFM, so .NET 8+ apps can reference it directly)
- **Zero external runtime dependencies**: uses only `System.Net.Http.Json` and `System.Net.WebSockets.Client` built into the BCL
- **Integration approach**: HTTP upstream API (request / response) + real-time downstream events (WebSocket full-duplex / SSE one-way)

---

## Quick Start

### 1. Add a Project Reference

```xml
<ItemGroup>
  <ProjectReference Include="..\src\AguiGroupChat.Sdk\AguiGroupChat.Sdk.csproj" />
</ItemGroup>
```

Or reference the packaged NuGet package (`dotnet pack -c Release`):

```sh
dotnet pack src/AguiGroupChat.Sdk -c Release
```

### 2. HTTP: Login + Group Chat

```csharp
using AguiGroupChat.Sdk;
using AguiGroupChat.Sdk.Models;

var options = new AguiClientOptions { BaseUri = new Uri("http://localhost:5100") };
using var client = new AguiClient(options);

// Login (registering logs you in too: client.RegisterAsync(...))
var auth = await client.LoginAsync("zhangsan", "123456");
client.Token = auth.Token;               // The SDK attaches the Bearer token automatically

// Create a group
var group = await client.CreateGroupAsync(new GroupCreateRequest
{
    GroupName = "新产品评审",
    OwnerId = auth.UserId!,
    MemberIds = ["user_1002"],
});

// Send a message and @-mention the agent
await client.SendMessageAsync(new GroupMessageSendRequest
{
    GroupId = group!.GroupId,
    UserId = auth.UserId,
    Content = "请评估 V2 需求",
    Mentions = ["agent_prd"],
});

// Fetch history / search
var history = await client.GetMessagesAsync(group.GroupId);
var hits     = await client.SearchMessagesAsync(group.GroupId, "V2");
```

### 3. Real-time Channel: Subscribe and Receive Push

```csharp
await using var realtime = new AguiRealtimeClient(options) { Token = auth.Token };

realtime.On<GroupConnectedEvent>(e  => Console.WriteLine($"已连接 {e.ConnectionId}"));
realtime.On<TextMessageContentEvent>(e => Console.Write(e.Delta));   // agent streaming text
realtime.On<TextMessageEndEvent>(_   => Console.WriteLine());
realtime.On<GroupTypingEvent>(e      => Console.WriteLine($"[输入中] {e.MemberId}"));

await realtime.ConnectAsync(["group_001", "group_002"], ct);
// ConnectAsync subscribes automatically; after that you can add/remove:
await realtime.SubscribeAsync(["group_003"], ct);
await realtime.UnsubscribeAsync(["group_001"], ct);

// WS full-duplex: send upstream directly through the real-time channel (equivalent to the HTTP write API)
await realtime.SendMessageAsync(new GroupMessageSendRequest {
    GroupId = "group_001", Content = "hello", Mentions = ["agent_prd"],
});
```

### 4. Handling Errors

```csharp
try
{
    await client.LoginAsync("bad", "bad");
}
catch (AguiException ex)
{
    Console.WriteLine($"{ex.Code}: {ex.Message} (HTTP {ex.StatusCode})");
    // e.g. USER_BAD_CREDENTIALS: ... (HTTP 401)
}
```

---

## Components

| Type | Description |
|---|---|
| `AguiClient` | HTTP client: authentication, group / member / topic / message / agent / attachment management, dynamic SSE subscriptions |
| `AguiRealtimeClient` | Real-time channel: WebSocket full-duplex or SSE one-way, strongly-typed event dispatch + WS upstream |
| `AguiClientOptions` | Configures the Hub base URI, token provider, timeout, transport mode |
| `AguiException` / `AguiError` | Unified error model (protocol error code + HTTP status code + raw response) |
| `Models.*` | Request / response DTOs and event types, matching the Hub's JSON wire format |

### `AguiClient` Capability Overview

- **Auth**: `RegisterAsync` / `LoginAsync` / `LogoutAsync` / `GetCurrentUserAsync` / `ChangePasswordAsync` / `UpdateProfileAsync` / `ListUsersAsync`
- **Groups**: `CreateGroupAsync` / `UpdateGroupAsync` / `DisbandGroupAsync` / `GetGroupSnapshotAsync` / `GetGroupMembersAsync` / `GetMyGroupsAsync`
- **Topics / Members**: `CreateTopicAsync` / `DeleteTopicAsync` / `ClearTopicAsync` / `GetTopicsAsync` / `AddMembersAsync` / `RemoveMembersAsync` / `LeaveGroupAsync` / `UpdateMemberAsync`
- **Messages**: `SendMessageAsync` / `RecallMessageAsync` / `RegenerateMessageAsync` / `StopAgentRunAsync` / `SendTypingAsync` / `SendReadAsync` / `GetMessagesAsync` / `GetTopicMessagesAsync` / `SearchMessagesAsync` / `StartDiscussionAsync` / `ResolveInteractionAsync`
- **Agents**: `ListAgentsAsync` / `CreateAgentAsync` / `UpdateAgentAsync` / `DeleteAgentAsync` / `RegisterAgentAsync` / `UnregisterAgentAsync`
- **Attachments**: `UploadAsync`
- **SSE Subscriptions**: `SubscribeSseAsync` / `UnsubscribeSseAsync`

### Real-time Event Dispatch

`AguiRealtimeClient.On<T>()` subscribes with strong typing; `OnRaw(type, handler)` subscribes to dynamic event types newly added by the Hub;
`AnyEvent` subscribes to all events. All event models (`GROUP_CONNECTED`, `TEXT_MESSAGE_START/CONTENT/END`,
`GROUP_STATE_SNAPSHOT`, `AGENT_INTERACTION_*`, `GROUP_TOPIC_*`, etc.) are in `Models/Events.cs`.

---

## Auth Notes

- The Hub by default (`Auth:RequireTokenOnRealTime=true`) requires a **valid session token**; after login / registration the SDK automatically attaches the token
  in the `Authorization: Bearer` header, and both `AguiClient.Token` and `AguiRealtimeClient.Token` can be overridden.
- When connecting to the real-time channel, the token is written to the URL (`?token=`) or handled automatically by the SDK; in the WS scenario the server **relies on the token identity**,
  so the SDK does not need to (and should not) pass `memberId`.
- For all write APIs the sender / operator identity determined by the server from the token is the **single source of truth**; SDK parameters only serve as a compatibility fallback.

---

## JSON Conventions

The SDK uses serialization conventions identical to the gateway (camelCase properties, `null` omitted, enums serialized as camelCase strings),
aligned with protocol §2 and the gateway's `ConfigureHttpJsonOptions(JsonStringEnumConverter...)`.
If you build your own proxy gateway without enum stringification configured, configure the same conventions, otherwise response deserialization will throw `AguiException(BAD_REQUEST)`.

---

## Examples

- See the complete command-line example at [`samples/AguiGroupChat.Client`](../../samples/AguiGroupChat.Client), demonstrating login → HTTP group creation / messaging → real-time subscription → streaming receipt of agent replies.
- See end-to-end integration tests at [`tests/AguiGroupChat.Sdk.Tests`](../../tests/AguiGroupChat.Sdk.Tests) (self-hosted real Hub, including the full WebSocket pipeline).
