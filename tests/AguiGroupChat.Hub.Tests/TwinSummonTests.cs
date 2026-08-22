using AguiGroupChat.Hub.Agents;
using AguiGroupChat.Hub.Messaging;
using AguiGroupChat.Hub.Models;
using AguiGroupChat.Hub.Options;
using AguiGroupChat.Hub.Storage;
using AguiGroupChat.Hub.Users;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>录制式网关：记录收到的调用上下文，供断言「是否被召唤 / 触发模式」。</summary>
public sealed class RecordingGateway : IAgentGateway
{
    public List<AgentInvocationContext> Calls { get; } = new();

    public Task<AgentInvocationResult> InvokeAsync(AgentInvocationContext context, CancellationToken ct)
    {
        Calls.Add(context);
        return Task.FromResult(new AgentInvocationResult(true, "run_" + Calls.Count, null));
    }

    public Task<bool> IsAvailableAsync(string agentId, CancellationToken ct) => Task.FromResult(true);

    public Task<bool> ResolveInteractionAsync(string interruptId, string memberId, bool approved, string? input, System.Text.Json.JsonElement? payload, CancellationToken ct, bool approveAll = false)
        => Task.FromResult(true);

    public bool StopRun(string runId, string operatorId, string groupId, bool isManager) => false;
}

/// <summary>固定返回单个用户的分身信息（agentId 为 null 表示未启用分身）。</summary>
public sealed class StubTwinSync : ITwinAgentSync
{
    private readonly string? _agentId;
    private readonly string _nickname;

    public StubTwinSync(string? agentId, string nickname = "分身")
    {
        _agentId = agentId;
        _nickname = nickname;
    }

    public TwinAgentInfo? GetTwinAgent(string userId)
        => _agentId is null ? null : new TwinAgentInfo(_agentId, _nickname);
}

/// <summary>
/// 用户 @ 自己且已启用分身 → 显式召唤分身回答（即使在线、即使分身常规暂停）：
/// 强制以「提及」语义加入调用（不走语境决策），触发模式为 Mentioned。
/// </summary>
public sealed class TwinSummonTests
{
    private sealed record Sut(GroupHub Hub, RecordingGateway Gateway, ConnectionManager Connections);

    private static Sut CreateSut(string? twinAgentId = null)
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
            TimeProvider.System, NullLogger<GroupHub>.Instance,
            twinSync: twinAgentId is null ? null : new StubTwinSync(twinAgentId));
        return new Sut(hub, gateway, connections);
    }

    /// <summary>注册一个在线连接（使成员处于在线状态，分身常规触发即被暂停）。</summary>
    private static void BringOnline(ConnectionManager connections, string memberId)
    {
        connections.Register(new HubConnection
        {
            ConnectionId = "conn_" + memberId,
            MemberId = memberId,
            Transport = "test",
            Sender = (_, _) => Task.CompletedTask,
        });
    }

    private static async Task WaitUntilAsync(Func<bool> cond, int timeoutMs = 3000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!cond() && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        Assert.True(cond(), "等待条件超时（智能体调用未在期限内到达）");
    }

    [Fact]
    public async Task AtSelf_WithTwin_SummonsTwin_EvenWhenOnline()
    {
        var sut = CreateSut(twinAgentId: "twin_user_1");
        var group = await HubFixture.CreateGroupAsync(sut.Hub, "群", "user_1", "twin_user_1");
        // 分身注册为「提及」触发：普通规则对 user_1（而非 twin_user_1）不命中，只能靠召唤
        sut.Hub.RegisterAgent(new AgentRegisterRequest
        {
            AgentId = "twin_user_1",
            Nickname = "分身",
            GroupIds = [group.GroupId],
            TriggerMode = AgentTriggerMode.Mentioned,
        });
        BringOnline(sut.Connections, "user_1"); // 用户在线 → 分身常规暂停

        await sut.Hub.SendMessageAsync(new GroupMessageSendRequest
        {
            GroupId = group.GroupId,
            UserId = "user_1",
            Content = "@自己 请回答",
            Mentions = ["user_1"],
        });

        await WaitUntilAsync(() => sut.Gateway.Calls.Count > 0);
        var call = Assert.Single(sut.Gateway.Calls);
        Assert.Equal("twin_user_1", call.AgentId);
        Assert.Equal(AgentTriggerMode.Mentioned, call.TriggerMode);
    }

    [Fact]
    public async Task AtSelf_TwinContextual_Online_BypassesPause()
    {
        var sut = CreateSut(twinAgentId: "twin_user_1");
        var group = await HubFixture.CreateGroupAsync(sut.Hub, "群", "user_1", "twin_user_1");
        // 语境触发：Evaluate 会命中，但用户在线导致分身常规被暂停过滤；召唤应把分身加回
        sut.Hub.RegisterAgent(new AgentRegisterRequest
        {
            AgentId = "twin_user_1",
            Nickname = "分身",
            GroupIds = [group.GroupId],
            TriggerMode = AgentTriggerMode.Contextual,
        });
        BringOnline(sut.Connections, "user_1");

        await sut.Hub.SendMessageAsync(new GroupMessageSendRequest
        {
            GroupId = group.GroupId,
            UserId = "user_1",
            Content = "@自己 请回答",
            Mentions = ["user_1"],
        });

        await WaitUntilAsync(() => sut.Gateway.Calls.Count > 0);
        var call = Assert.Single(sut.Gateway.Calls);
        Assert.Equal("twin_user_1", call.AgentId);
        Assert.Equal(AgentTriggerMode.Mentioned, call.TriggerMode);
    }

    [Fact]
    public async Task AtSomeoneElse_DoesNotSummonTwin()
    {
        var sut = CreateSut(twinAgentId: "twin_user_1");
        var group = await HubFixture.CreateGroupAsync(sut.Hub, "群", "user_1", "twin_user_1");
        sut.Hub.RegisterAgent(new AgentRegisterRequest
        {
            AgentId = "twin_user_1",
            Nickname = "分身",
            GroupIds = [group.GroupId],
            TriggerMode = AgentTriggerMode.Contextual,
        });
        BringOnline(sut.Connections, "user_1"); // 在线 → 语境触发的分身被常规暂停

        await sut.Hub.SendMessageAsync(new GroupMessageSendRequest
        {
            GroupId = group.GroupId,
            UserId = "user_1",
            Content = "@别人 在吗",
            Mentions = ["user_2"],
        });

        await Task.Delay(150); // 给足扇出与触发评估时间
        Assert.Empty(sut.Gateway.Calls);
    }

    [Fact]
    public async Task AtSelf_TwinNotGroupMember_NoSummon()
    {
        var sut = CreateSut(twinAgentId: "twin_user_1");
        // 分身已启用，但未加入本群
        var group = await HubFixture.CreateGroupAsync(sut.Hub, "群", "user_1");
        BringOnline(sut.Connections, "user_1");

        await sut.Hub.SendMessageAsync(new GroupMessageSendRequest
        {
            GroupId = group.GroupId,
            UserId = "user_1",
            Content = "@自己 请回答",
            Mentions = ["user_1"],
        });

        await Task.Delay(150);
        Assert.Empty(sut.Gateway.Calls);
    }

    [Fact]
    public async Task AtSelf_NoTwinConfigured_NoSummon()
    {
        var sut = CreateSut(twinAgentId: null); // 未启用分身
        var group = await HubFixture.CreateGroupAsync(sut.Hub, "群", "user_1");
        BringOnline(sut.Connections, "user_1");

        await sut.Hub.SendMessageAsync(new GroupMessageSendRequest
        {
            GroupId = group.GroupId,
            UserId = "user_1",
            Content = "@自己 请回答",
            Mentions = ["user_1"],
        });

        await Task.Delay(150);
        Assert.Empty(sut.Gateway.Calls);
    }
}
