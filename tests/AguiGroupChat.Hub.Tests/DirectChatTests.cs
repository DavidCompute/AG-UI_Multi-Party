using AguiGroupChat.Hub.Agents;
using AguiGroupChat.Hub.Infra;
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
    public async Task TryEnsureDirectChat_UsesAgentAvatarAsChatAvatar_AndRefreshesOnReenter()
    {
        var (hub, _) = CreateSut();

        // 新建：单聊头像 = 对端数字员工头像（前端侧栏据此渲染，不再显示默认群图标）
        var a = await hub.TryEnsureDirectChatAsync("user_1", "agent_avatar", "头像助手", "/files/att_x/avatar.png");
        Assert.Equal("/files/att_x/avatar.png", a.GroupAvatar);
        Assert.Equal(2, a.MemberCount);

        // 对端换头像后再次进入：幂等分支把群头像同步为新头像
        var again = await hub.TryEnsureDirectChatAsync("user_1", "agent_avatar", "头像助手", "/files/att_y/avatar2.png");
        Assert.Equal(a.GroupId, again.GroupId);
        Assert.Equal("/files/att_y/avatar2.png", hub.Store.GetGroup(a.GroupId)!.GroupAvatar);
    }

    [Fact]
    public async Task TryEnsureDirectChat_AfterDisband_RecreateClearsTombstone_AndIsUsable()
    {
        // 回归：确定性单聊群被解散后再次进入（同进程重建同 ID）——若不清除“已解散”内存墓碑，
        // 订阅 / 快照 / 发消息都会判“群组不存在或已解散”，表现为“点进单聊报错 group_direct_xxx”。
        var (hub, _) = CreateSut();

        var a = await hub.TryEnsureDirectChatAsync("user_1", "agent_loop", "循环助手", null);
        await hub.DisbandGroupAsync(new GroupDisbandRequest { GroupId = a.GroupId, OperatorId = "user_1" });
        Assert.Null(hub.Store.GetGroup(a.GroupId)); // 解散已删存储行

        // 再次进入：同一确定性 ID 重建；墓碑须被清除，随后发消息（走 GetGroupOrThrow）不再报已解散
        var b = await hub.TryEnsureDirectChatAsync("user_1", "agent_loop", "循环助手", null);
        Assert.Equal(a.GroupId, b.GroupId);
        Assert.NotNull(hub.Store.GetGroup(b.GroupId));
        Assert.True(hub.Store.IsMember(b.GroupId, "user_1"));

        await hub.SendMessageAsync(new GroupMessageSendRequest
        {
            GroupId = b.GroupId,
            UserId = "user_1",
            Content = "解散后重建的会话应立即可用",
        });
    }

    /// <summary>带准入依赖的 SUT：可注入智能体定义与用户组，用于验证单聊的组白名单准入。</summary>
    private static GroupHub CreateGuardedSut(IAgentDefinitionStore defs, IUserGroupService groups)
    {
        var options = new GroupChatOptions
        {
            MaxGroupMembers = 50,
            MessageHistoryLimit = 200,
            SnapshotMessageCount = 50,
        };
        return new GroupHub(
            new InMemoryGroupStore(options.MessageHistoryLimit),
            new InMemoryUserStore(),
            new ConnectionManager(),
            new AgentRegistry(),
            new AgentTriggerService(new AgentRegistry()),
            new RecordingGateway(),
            options,
            TimeProvider.System,
            NullLogger<GroupHub>.Instance,
            agentDefinitions: defs,
            userGroups: groups);
    }

    private sealed class StubDefs(AgentDefinitionInfo info) : IAgentDefinitionStore
    {
        public AgentDefinitionInfo? GetDefinition(string agentId) => agentId == info.AgentId ? info : null;
    }

    private sealed class StubGroups(params string[] members) : IUserGroupService
    {
        public bool UserInAnyGroup(string userId, IReadOnlyList<string>? groupIds)
            => groupIds is { Count: > 0 } && groupIds.Contains("ug_rnd") && members.Contains(userId);

        public IReadOnlyList<string> GroupsOfUser(string userId)
            => members.Contains(userId) ? ["ug_rnd"] : [];
    }

    [Fact]
    public async Task TryEnsureDirectChat_CreatePath_RejectsUserOutsideAllowlist()
    {
        // 组白名单内的数字员工，白名单外用户首次单聊 → 拒绝
        var defs = new StubDefs(new AgentDefinitionInfo("agent_g", "受限助手", false, "boss", ["ug_rnd"]));
        var hub = CreateGuardedSut(defs, new StubGroups("dev_a"));

        await Assert.ThrowsAsync<AguiProtocolException>(
            () => hub.TryEnsureDirectChatAsync("outsider", "agent_g", "受限助手", null));
        // 白名单内成员可以建
        var ok = await hub.TryEnsureDirectChatAsync("dev_a", "agent_g", "受限助手", null);
        Assert.True(hub.Store.IsMember(ok.GroupId, "dev_a"));
    }

    [Fact]
    public async Task TryEnsureDirectChat_ReusePath_AlsoEnforcesAllowlist()
    {
        // 回归：复用已存在会话的分支曾早于准入校验返回，导致用户被移出白名单后仍能继续单聊（fail-open）。
        // 这里用「白名单可切换」的桩模拟“先合法、后移出”：
        var defs = new StubDefs(new AgentDefinitionInfo("agent_g", "受限助手", false, "boss", ["ug_rnd"]));
        var online = new HashSet<string>(StringComparer.Ordinal);
        var groups = new SwitchableGroups(online);
        var hub = CreateGuardedSut(defs, groups);

        online.Add("dev_a");
        var first = await hub.TryEnsureDirectChatAsync("dev_a", "agent_g", "受限助手", null);
        // 幂等复用：仍在白名单 → 正常返回同一会话
        var again = await hub.TryEnsureDirectChatAsync("dev_a", "agent_g", "受限助手", null);
        Assert.Equal(first.GroupId, again.GroupId);

        // 被移出白名单后：即使会话已存在，也必须拒绝（不能靠复用绕过）
        online.Remove("dev_a");
        await Assert.ThrowsAsync<AguiProtocolException>(
            () => hub.TryEnsureDirectChatAsync("dev_a", "agent_g", "受限助手", null));
    }

    private sealed class SwitchableGroups(HashSet<string> members) : IUserGroupService
    {
        public bool UserInAnyGroup(string userId, IReadOnlyList<string>? groupIds)
            => groupIds is { Count: > 0 } && groupIds.Contains("ug_rnd") && members.Contains(userId);

        public IReadOnlyList<string> GroupsOfUser(string userId)
            => members.Contains(userId) ? ["ug_rnd"] : [];
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
