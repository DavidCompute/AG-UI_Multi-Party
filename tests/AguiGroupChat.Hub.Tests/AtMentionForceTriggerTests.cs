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
/// 规则：任何智能体被 @（或 @全体）时必定触发，不受触发模式限制，
/// 并以 Mentioned 语义调用（跳过语境沉默决策，确保必发言）。
/// </summary>
public sealed class AtMentionForceTriggerTests
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
        Assert.True(cond(), "等待条件超时（智能体调用未在期限内到达）");
    }

    /// <summary>注册关键词智能体并加入群：关键词不命中时，只有被 @ 才触发。</summary>
    [Fact]
    public async Task KeywordAgent_AtTriggered_RepliesWithMentionedMode()
    {
        var (hub, gateway) = CreateSut();
        var group = await HubFixture.CreateGroupAsync(hub, "群", "user_1", "agent_kw");
        hub.RegisterAgent(new AgentRegisterRequest
        {
            AgentId = "agent_kw",
            Nickname = "关键词助手",
            GroupIds = [group.GroupId],
            TriggerMode = AgentTriggerMode.Keyword,
            Keywords = ["报表"],
        });

        await hub.SendMessageAsync(new GroupMessageSendRequest
        {
            GroupId = group.GroupId,
            UserId = "user_1",
            Content = "@关键词助手 帮我看看这个群聊", // 未命中关键词，仅被 @
            Mentions = ["agent_kw"],
        });

        await WaitUntilAsync(() => gateway.Calls.Count > 0);
        var call = Assert.Single(gateway.Calls);
        Assert.Equal("agent_kw", call.AgentId);
        Assert.Equal(AgentTriggerMode.Mentioned, call.TriggerMode); // 显式提及语义 → 必发言
    }

    [Fact]
    public async Task KeywordAgent_MentionAll_Replies()
    {
        var (hub, gateway) = CreateSut();
        var group = await HubFixture.CreateGroupAsync(hub, "群", "user_1", "agent_kw");
        hub.RegisterAgent(new AgentRegisterRequest
        {
            AgentId = "agent_kw",
            Nickname = "关键词助手",
            GroupIds = [group.GroupId],
            TriggerMode = AgentTriggerMode.Keyword,
            Keywords = ["报表"],
        });

        await hub.SendMessageAsync(new GroupMessageSendRequest
        {
            GroupId = group.GroupId,
            UserId = "user_1",
            Content = "@全体 大家好",
            MentionAll = true,
        });

        await WaitUntilAsync(() => gateway.Calls.Count > 0);
        var call = Assert.Single(gateway.Calls);
        Assert.Equal("agent_kw", call.AgentId);
        Assert.Equal(AgentTriggerMode.Mentioned, call.TriggerMode);
    }

    [Fact]
    public async Task KeywordAgent_NotMentioned_NoKeywordHit_NotTriggered()
    {
        var (hub, gateway) = CreateSut();
        var group = await HubFixture.CreateGroupAsync(hub, "群", "user_1", "agent_kw");
        hub.RegisterAgent(new AgentRegisterRequest
        {
            AgentId = "agent_kw",
            Nickname = "关键词助手",
            GroupIds = [group.GroupId],
            TriggerMode = AgentTriggerMode.Keyword,
            Keywords = ["报表"],
        });

        await hub.SendMessageAsync(new GroupMessageSendRequest
        {
            GroupId = group.GroupId,
            UserId = "user_1",
            Content = "随便聊聊今天天气不错",
        });

        await Task.Delay(150); // 给足扇出与触发评估时间
        Assert.Empty(gateway.Calls);
    }

    /// <summary>语境智能体被 @ 时以 Mentioned 语义调用（不再走语境沉默决策）。</summary>
    [Fact]
    public async Task ContextualAgent_AtTriggered_PassesMentionedMode()
    {
        var (hub, gateway) = CreateSut();
        var group = await HubFixture.CreateGroupAsync(hub, "群", "user_1", "agent_ctx");
        hub.RegisterAgent(new AgentRegisterRequest
        {
            AgentId = "agent_ctx",
            Nickname = "语境助手",
            GroupIds = [group.GroupId],
            TriggerMode = AgentTriggerMode.Contextual,
        });

        await hub.SendMessageAsync(new GroupMessageSendRequest
        {
            GroupId = group.GroupId,
            UserId = "user_1",
            Content = "@语境助手 在吗",
            Mentions = ["agent_ctx"],
        });

        await WaitUntilAsync(() => gateway.Calls.Count > 0);
        var call = Assert.Single(gateway.Calls);
        Assert.Equal("agent_ctx", call.AgentId);
        Assert.Equal(AgentTriggerMode.Mentioned, call.TriggerMode);
    }
}
