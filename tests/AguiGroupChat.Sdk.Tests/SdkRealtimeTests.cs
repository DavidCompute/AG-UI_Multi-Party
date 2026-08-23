using System.Net.WebSockets;
using AguiGroupChat.Sdk;
using AguiGroupChat.Sdk.Models;
using Xunit;

namespace AguiGroupChat.Sdk.Tests;

/// <summary>
/// 验证 SDK 实时通道（WebSocket）能对接真实 Hub：登录 → 连接 → 订阅 → 上行消息 → 收到推送事件。
/// </summary>
public sealed class SdkRealtimeTests : IClassFixture<SdkServerFixture>
{
    private readonly SdkServerFixture _fixture;

    public SdkRealtimeTests(SdkServerFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Connect_Subscribe_SendMessage_ReceivesEvents()
    {
        // ---- 登录 ----
        using var http = new AguiClient(new AguiClientOptions { BaseUri = new Uri(_fixture.HttpBase) });
        var auth = await http.LoginAsync("zhangsan", "123456");
        http.Token = auth.Token;

        // 找到种子群
        var groups = await http.GetMyGroupsAsync("user_1001");
        var groupId = groups!.Single(g => g.GroupId is not null).GroupId!;

        // ---- 连接实时通道 ----
        var connected = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var snapshotGroup = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var messageStart = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var messageContent = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        var messageEnd = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var realtime = new AguiRealtimeClient(new AguiClientOptions { BaseUri = new Uri(_fixture.HttpBase) })
        {
            Token = auth.Token,
        };

        realtime.On<GroupConnectedEvent>(e => connected.TrySetResult(e.ConnectionId ?? ""));
        realtime.On<GroupStateSnapshotEvent>(e => { if (e.GroupId == groupId) snapshotGroup.TrySetResult(groupId); });
        realtime.On<TextMessageStartEvent>(e => messageStart.TrySetResult(e.MessageId ?? ""));
        realtime.On<TextMessageContentEvent>(e => messageContent.TrySetResult(Task.CompletedTask));
        realtime.On<TextMessageEndEvent>(e => messageEnd.TrySetResult(Task.CompletedTask));

        await realtime.ConnectAsync(new[] { groupId });
        Assert.True(realtime.IsConnected);
        Assert.False(string.IsNullOrEmpty(realtime.ConnectionId));

        // 等待握手（GROUP_CONNECTED）与快照到达（订阅成功标志）
        var connTask = await Task.WhenAny(connected.Task, Task.Delay(TimeSpan.FromSeconds(8)));
        Assert.True(connTask == connected.Task, "未在超时前收到 GROUP_CONNECTED 握手");
        Assert.Equal(realtime.ConnectionId, await connected.Task);
        var snapTask = await Task.WhenAny(snapshotGroup.Task, Task.Delay(TimeSpan.FromSeconds(8)));
        Assert.True(snapTask == snapshotGroup.Task, "未在超时前收到 GROUP_STATE_SNAPSHOT（订阅失败？）");

        // ---- 经 WS 上行消息 ----
        await realtime.SendMessageAsync(new GroupMessageSendRequest
        {
            GroupId = groupId,
            UserId = "user_1001",
            Content = "实时通道测试：请简要回复",
            Mentions = ["agent_prd"],
        });

        var startOk = await Task.WhenAny(messageStart.Task, Task.Delay(TimeSpan.FromSeconds(8)));
        Assert.True(startOk == messageStart.Task, "未收到 TEXT_MESSAGE_START（智能体未回复？）");
        var _ = await Task.WhenAny(messageContent.Task, Task.Delay(TimeSpan.FromSeconds(8)));

        await realtime.DisposeAsync();
    }

    [Fact]
    public async Task UnauthorizedConnection_IsRejected()
    {
        // 强制令牌模式下，不带令牌连 WS 应被拒绝（HTTP 401 前握手即失败）。
        // 此处直接以错误令牌连接，SDK 层 ConnectAsync 会抛 WebSocketException。
        await using var realtime = new AguiRealtimeClient(new AguiClientOptions { BaseUri = new Uri(_fixture.HttpBase) })
        {
            Token = "not-a-valid-token",
        };
        await Assert.ThrowsAsync<WebSocketException>(async () => await realtime.ConnectAsync(new[] { "group_001" }));
    }
}
