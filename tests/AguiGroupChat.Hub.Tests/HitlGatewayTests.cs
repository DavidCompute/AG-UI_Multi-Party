using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AguiGroupChat.Agents;
using AguiGroupChat.Hub.Agents;
using AguiGroupChat.Hub.Messaging;
using AguiGroupChat.Hub.Models;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>模拟 IChatClient：首轮流式返回一次工具调用（FunctionCallContent），之后返回普通文本。</summary>
public sealed class ToolCallingMockClient : IChatClient
{
    private readonly List<ChatResponseUpdate> _updates;
    private int _calls;

    public ToolCallingMockClient(params ChatResponseUpdate[] updates) => _updates = updates.ToList();

    public object? GetService(Type serviceType, object? serviceKey = null)
        => serviceType.IsInstanceOfType(this) ? this : null;

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var u in _updates) yield return u;
        _calls++;
        if (_calls == 1)
        {
            // 首轮模型决定调用工具
            yield return new ChatResponseUpdate(ChatRole.Assistant,
                [new FunctionCallContent("call_hitl_1", "get_current_time", arguments: null)]);
        }
        else
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, "时间已获取。");
        }
    }

    public void Dispose() { }
}

/// <summary>
/// AG-UI 人机交互（HITL）测试：ChatClientAgent 对 ApprovalRequiredAIFunction 包装的工具产生审批中断；
/// 触发者决策（批准 / 拒绝）后同一 AgentSession 恢复运行，工具执行并继续流式输出。
/// </summary>
public sealed class HitlAgentFlowTests
{
    [Fact]
    public async Task ChatClientAgent_WithApprovalTool_EmitsApprovalRequest_AndResumes()
    {
        var approvalTool = new ApprovalRequiredAIFunction(
            AIFunctionFactory.Create(() => DateTimeOffset.UtcNow.ToString("O"), "get_current_time", "返回当前服务器时间"));
        var agent = new ChatClientAgent(
            new ToolCallingMockClient(),
            "审批测试", null, null,
            new[] { approvalTool },
            NullLoggerFactory.Instance,
            new ServiceCollection().BuildServiceProvider());

        var session = await agent.CreateSessionAsync();

        // 第一轮：模型调用需审批工具 → 运行中断，产出 ToolApprovalRequestContent
        var updates = new List<AgentResponseUpdate>();
        await foreach (var u in agent.RunStreamingAsync([new ChatMessage(ChatRole.User, "现在几点了？")], session))
            updates.Add(u);
        var approval = updates.SelectMany(u => u.Contents).OfType<ToolApprovalRequestContent>().FirstOrDefault();
        Assert.NotNull(approval);
        var fc = Assert.IsType<FunctionCallContent>(approval!.ToolCall);
        Assert.Equal("get_current_time", fc.Name);

        // 恢复：决策作为 User 消息回灌同一 session → 工具执行 + 文本继续
        var decision = approval.CreateResponse(approved: true);
        var resume = new List<AgentResponseUpdate>();
        await foreach (var u in agent.RunStreamingAsync([new ChatMessage(ChatRole.User, [decision])], session))
            resume.Add(u);

        Assert.Contains(resume.SelectMany(u => u.Contents), c => c is FunctionResultContent); // 工具已执行
        Assert.Contains(resume, u => u.Text is { Length: > 0 });
    }

    /// <summary>网关级端到端：群聊触发 → 审批中断广播 → 仅触发者可决策 → 批准后恢复回灌。</summary>
    [Fact]
    public async Task Gateway_Hitl_Interrupts_And_OnlyTriggererCanResolve()
    {
        var f = new HubFixture();
        var group = await f.Hub.CreateGroupAsync(new GroupCreateRequest
        {
            GroupName = "g",
            OwnerId = "user_1",
            MemberIds = ["user_2", "agent_hitl"],
            Members =
            [
                new MemberSeed { MemberId = "agent_hitl", MemberType = MemberType.Agent, Nickname = "审批助手" },
            ],
        });
        var (triggererConn, triggererInbox) = f.NewConnection("user_1");
        var (otherConn, otherInbox) = f.NewConnection("user_2");
        await f.Hub.SubscribeAsync(triggererConn, [group.GroupId]);
        await f.Hub.SubscribeAsync(otherConn, [group.GroupId]);
        f.Drain(triggererInbox);
        f.Drain(otherInbox);

        var options = new AgentOptions
        {
            Provider = "mock",
            EnableTools = true, // mock 客户端将按关键词模拟工具调用
            Agents =
            [
                new AgentDefinition
                {
                    AgentId = "agent_hitl", Nickname = "审批助手", Description = "测试", Instructions = "你是审批助手",
                    TriggerMode = AgentTriggerMode.Mentioned,
                },
            ],
        };
        var catalog = new AgentCatalog(options, NullLoggerFactory.Instance, new ServiceCollection().BuildServiceProvider());
        var services = new ServiceCollection().AddSingleton(f.Hub).BuildServiceProvider();
        var gateway = new AgentGateway(catalog, services, options, attachmentStore: null, NullLogger<AgentGateway>.Instance);

        // 触发者请求发布公告 → mock 模拟调用 publish_announcement（需审批）→ 运行中断
        var result = await gateway.InvokeAsync(new AgentInvocationContext(
            GroupId: group.GroupId,
            ThreadId: "thread_" + group.GroupId,
            AgentId: "agent_hitl",
            AgentNickname: "审批助手",
            TriggerMessageId: "msg_trigger",
            TriggerUserId: "user_1",
            Content: "帮我发布公告：放假通知",
            Mentions: [],
            MentionAll: false), CancellationToken.None);

        Assert.False(result.Accepted);
        Assert.Equal("AGENT_AWAITING_INTERACTION", result.ErrorCode);

        // 广播了 AGENT_INTERACTION_REQUEST（双方都可见），字段齐全
        var requestEvent = FindEvent(f.Drain(triggererInbox), EventTypes.AgentInteractionRequest);
        Assert.Equal(EventTypes.AgentInteractionRequest, requestEvent.GetProperty("type").GetString());
        Assert.Equal("publish_announcement", requestEvent.GetProperty("toolName").GetString());
        Assert.Equal("user_1", requestEvent.GetProperty("targetMemberId").GetString());
        var interruptId = requestEvent.GetProperty("interruptId").GetString()!;
        Assert.False(string.IsNullOrWhiteSpace(interruptId));
        // 其他成员也能看到（可见性透明），但无权决策
        var otherEvents = f.Drain(otherInbox);
        Assert.Contains(otherEvents, e => HubFixture.TypeOf(e) == EventTypes.AgentInteractionRequest);

        // 非触发者（user_2）尝试决策 → 拒绝
        Assert.False(await gateway.ResolveInteractionAsync(interruptId, "user_2", true, null, null, CancellationToken.None));

        // 触发者（user_1）批准 → 恢复运行，工具执行，回复回灌群聊
        Assert.True(await gateway.ResolveInteractionAsync(interruptId, "user_1", true, null, null, CancellationToken.None));

        // 等待恢复后的流式回复（批准文本，复用中断前同一条消息：不产生新的 TEXT_MESSAGE_START）
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        var triggererEvents = new List<string>();
        while (DateTimeOffset.UtcNow < deadline)
        {
            triggererEvents.AddRange(f.Drain(triggererInbox));
            if (triggererEvents.Any(e => HubFixture.TypeOf(e) == EventTypes.TextMessageEnd)) break;
            await Task.Delay(100);
        }
        // 恢复后最终结果回灌到同一条消息：不应出现新的智能体 TEXT_MESSAGE_START（旧行为是新消息）
        Assert.DoesNotContain(triggererEvents, e =>
            HubFixture.TypeOf(e) == EventTypes.TextMessageStart
            && HubFixture.Parse(e).GetProperty("senderId").GetString() == "agent_hitl");
        var texts = triggererEvents
            .Where(e => HubFixture.TypeOf(e) == EventTypes.TextMessageContent)
            .Select(e => HubFixture.Parse(e).GetProperty("delta").GetString());
        Assert.Contains(texts, t => t is not null && t.Contains("已批准", StringComparison.Ordinal));
    }

    private static JsonElement FindEvent(List<string> events, string type)
    {
        foreach (var e in events)
        {
            var j = HubFixture.Parse(e);
            if (j.GetProperty("type").GetString() == type) return j;
        }
        throw new Xunit.Sdk.XunitException($"未找到事件 {type}；实际: {string.Join(";", events)}");
    }

    /// <summary>决策后全群广播 AGENT_INTERACTION_RESOLVED：其他成员的卡片同步更新（仅触发者可发起决策）。</summary>
    [Fact]
    public async Task Hub_ResolveInteraction_BroadcastsResolvedToAllMembers()
    {
        var f = new HubFixture();
        var gateway = new StubResolvingGateway { Resolved = true };
        var hub = new GroupHub(f.Store, f.Users, f.Connections, f.Agents, f.Triggers, gateway, f.Options,
            TimeProvider.System, NullLogger<GroupHub>.Instance);
        var group = await hub.CreateGroupAsync(new GroupCreateRequest
        {
            GroupName = "g", OwnerId = "user_1", MemberIds = ["user_2"],
        });
        var (c1, in1) = f.NewConnection("user_1");
        var (c2, in2) = f.NewConnection("user_2");
        await hub.SubscribeAsync(c1, [group.GroupId]);
        await hub.SubscribeAsync(c2, [group.GroupId]);
        f.Drain(in1);
        f.Drain(in2);

        // 触发者决策（批准）→ 全群广播决策结果
        var ok = await hub.ResolveAgentInteractionAsync(new GroupInteractionResolveRequest
        {
            GroupId = group.GroupId, InterruptId = "interrupt_1", MemberId = "user_1", Approved = true,
        });
        Assert.True(ok);

        foreach (var inbox in new[] { in1, in2 })
        {
            var resolved = FindEvent(f.Drain(inbox), EventTypes.AgentInteractionResolved);
            Assert.Equal("user_1", resolved.GetProperty("memberId").GetString());
            Assert.True(resolved.GetProperty("approved").GetBoolean());
        }

        // 决策不存在 → 网关返回 false → 不广播
        gateway.Resolved = false;
        var fail = await hub.ResolveAgentInteractionAsync(new GroupInteractionResolveRequest
        {
            GroupId = group.GroupId, InterruptId = "interrupt_x", MemberId = "user_2", Approved = false,
        });
        Assert.False(fail);
        Assert.DoesNotContain(f.Drain(in1), e => HubFixture.TypeOf(e) == EventTypes.AgentInteractionResolved);
    }

    /// <summary>网关替身：ResolveInteractionAsync 固定返回配置的结果。</summary>
    private sealed class StubResolvingGateway : IAgentGateway
    {
        public bool Resolved = true;
        public Task<AgentInvocationResult> InvokeAsync(AgentInvocationContext context, CancellationToken ct)
            => Task.FromResult(new AgentInvocationResult(true, "run_x", null));
        public Task<bool> IsAvailableAsync(string agentId, CancellationToken ct) => Task.FromResult(true);
        public Task<bool> ResolveInteractionAsync(string interruptId, string memberId, bool approved, string? input, System.Text.Json.JsonElement? payload, CancellationToken ct, bool approveAll = false)
            => Task.FromResult(Resolved);

        public bool StopRun(string runId, string operatorId, string groupId, bool isManager) => false;
    }
}
