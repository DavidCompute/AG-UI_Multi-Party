using AguiGroupChat.Hub.Agents;
using AguiGroupChat.Hub.Messaging;
using AguiGroupChat.Hub.Models;
using AguiGroupChat.Hub.Options;
using AguiGroupChat.Hub.Storage;
using AguiGroupChat.Hub.Users;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>
/// 数字员工单聊（kind=direct）：用户 ↔ 数字员工的 1:1 私有会话。
/// 验证：幂等建群、跨用户会话彼此独立、direct 群标志/私密性随存储往返保持，
/// 以及 C1——私聊中真人发的普通（未 @）消息按“直达”触发另一端数字员工。
/// </summary>
public sealed class DirectChatTests
{
    private static (GroupHub Hub, RecordingGateway Gateway) CreateSut()
    {
        var options = new GroupChatOptions
        {
            MaxGroupMembers = 50,
            MessageHistoryLimit = 200,
            SnapshotMessageCount = 50,
        };
        var store = new InMemoryGroupStore(options.MessageHistoryLimit);
        var users = new InMemoryUserStore();
        var connections = new ConnectionManager();
        var agents = new AgentRegistry();
        var triggers = new AgentTriggerService(agents);
        var gateway = new RecordingGateway();
        var hub = new GroupHub(store, users, connections, agents, triggers, gateway, options,
            TimeProvider.System, NullLogger<GroupHub>.Instance);
        return (hub, gateway);
    }

    private static async Task WaitUntilAsync(Func<bool> cond, int timeoutMs = 3000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!cond() && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        Assert.True(cond(), "等待条件超时");
    }

    [Fact]
    public async Task TryEnsureDirectChat_IsIdempotent_AndPersistedAsDirect()
    {
        var (hub, _) = CreateSut();

        var a = await hub.TryEnsureDirectChatAsync("user_1", "agent_chat", "单聊助手", null);
        Assert.True(a.IsDirectChat);
        Assert.True(a.IsPrivate);
        Assert.Equal("user_1", a.OwnerId);
        Assert.Equal(2, a.MemberCount);
        Assert.True(hub.Store.IsMember(a.GroupId, "user_1"));
        Assert.True(hub.Store.IsMember(a.GroupId, "agent_chat"));

        // 幂等：第二次仍返回同一确定性单聊群，且成员不重复
        var again = await hub.TryEnsureDirectChatAsync("user_1", "agent_chat", "单聊助手", null);
        Assert.Equal(a.GroupId, again.GroupId);
        Assert.Equal(2, again.MemberCount);

        // 存储往返后 kind=direct / 私密属性不丢
        var reloaded = hub.Store.GetGroup(a.GroupId);
        Assert.NotNull(reloaded);
        Assert.True(reloaded!.IsDirectChat);
        Assert.True(reloaded.IsPrivate);
    }

    [Fact]
    public async Task TryEnsureDirectChat_DifferentUsers_GetIsolatedGroups()
    {
        var (hub, _) = CreateSut();

        var a = await hub.TryEnsureDirectChatAsync("user_1", "agent_chat", "单聊助手", null);
        var b = await hub.TryEnsureDirectChatAsync("user_2", "agent_chat", "单聊助手", null);

        // 不同用户各自独立单聊：群号不同，会话彼此隔离
        Assert.NotEqual(a.GroupId, b.GroupId);
        Assert.True(hub.Store.IsMember(a.GroupId, "user_1"));
        Assert.False(hub.Store.IsMember(a.GroupId, "user_2"));
        Assert.True(hub.Store.IsMember(b.GroupId, "user_2"));
        Assert.False(hub.Store.IsMember(b.GroupId, "user_1"));
    }

    [Fact]
    public async Task DirectChat_PlainMessageWithoutAt_TriggersSoleAgentInMentionedMode()
    {
        var (hub, gateway) = CreateSut();

        var direct = await hub.TryEnsureDirectChatAsync("user_1", "agent_chat2", "二号助手", null);

        // C1：私聊里发普通（不 @）消息，就应触发另一端那唯一数字员工（Mentioned 语义、必发言）
        await hub.SendMessageAsync(new GroupMessageSendRequest
        {
            GroupId = direct.GroupId,
            UserId = "user_1",
            Content = "请简单打个招呼", // 无 Mentions、非 @
        });

        await WaitUntilAsync(() => gateway.Calls.Any(c => c.AgentId == "agent_chat2"));
        var call = Assert.Single(gateway.Calls, c => c.AgentId == "agent_chat2");
        Assert.Equal(direct.GroupId, call.GroupId);
        Assert.Equal(AgentTriggerMode.Mentioned, call.TriggerMode); // 直达：不走语境沉默
    }
}
