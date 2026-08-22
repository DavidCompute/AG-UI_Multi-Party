using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using AguiGroupChat.Agents;
using AguiGroupChat.Hub.Agents;
using AguiGroupChat.Hub.Infra;
using AguiGroupChat.Hub.Models;
using AguiGroupChat.Hub.Storage;
using AguiGroupChat.Hub.Users;
using AGUI.Abstractions;
using Microsoft.Agents.AI;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>
/// Microsoft Agent Framework 网关测试：使用 mock 提供方（无需密钥），
/// 验证 智能体触发 → 流式事件回灌 → 落库与扇出 的完整链路，
/// 以及 AG-UI 桥接（对接外部 AG-UI 服务）的双方言链路。
/// </summary>
public sealed class AgentGatewayTests
{
    private static AgentGateway CreateGateway(HubFixture f, out AgentOptions options)
    {
        options = new AgentOptions
        {
            Provider = "mock",
            Agents =
            [
                new AgentDefinition
                {
                    AgentId = "agent_a",
                    Nickname = "测试助手",
                    Description = "测试",
                    Instructions = "你是测试助手",
                    TriggerMode = AgentTriggerMode.Mentioned,
                },
            ],
        };
        var catalog = new AgentCatalog(options, NullLoggerFactory.Instance, new ServiceCollection().BuildServiceProvider());
        var services = new ServiceCollection().AddSingleton(f.Hub).BuildServiceProvider();
        return new AgentGateway(catalog, services, options, attachmentStore: null, NullLogger<AgentGateway>.Instance);
    }

    /// <summary>装配带「角色交接」（1.2）的网关：agent_a 整轮委托给 child1。</summary>
    private static AgentGateway CreateRelayGateway(HubFixture f)
    {
        var options = new AgentOptions
        {
            Provider = "mock",
            Agents =
            [
                new AgentDefinition
                {
                    AgentId = "agent_resp", Nickname = "答复专家", Description = "测试", Instructions = "你是答复专家，输出：交接答复",
                    TriggerMode = AgentTriggerMode.Mentioned,
                },
                new AgentDefinition
                {
                    AgentId = "agent_a", Nickname = "前台专员", Description = "测试", Instructions = "你只是前台，不自己回答",
                    TriggerMode = AgentTriggerMode.Mentioned,
                    RelayToAgentId = "agent_resp",
                },
            ],
        };
        var catalog = new AgentCatalog(options, NullLoggerFactory.Instance, new ServiceCollection().BuildServiceProvider());
        var services = new ServiceCollection().AddSingleton(f.Hub).BuildServiceProvider();
        return new AgentGateway(catalog, services, options, attachmentStore: null, NullLogger<AgentGateway>.Instance);
    }

    [Fact]
    public async Task Invoke_RelayToAgent_StreamsRelayReplyAsHost()
    {
        var f = new HubFixture();
        var group = await f.Hub.CreateGroupAsync(new GroupCreateRequest
        {
            GroupName = "g", OwnerId = "user_1", MemberIds = ["agent_a"],
            Members = [new MemberSeed { MemberId = "agent_a", MemberType = MemberType.Agent, Nickname = "前台专员" }],
        });
        var (conn, inbox) = f.NewConnection("user_1");
        await f.Hub.SubscribeAsync(conn, [group.GroupId]);
        f.Drain(inbox);

        var gateway = CreateRelayGateway(f);
        var result = await gateway.InvokeAsync(new AgentInvocationContext(
            GroupId: group.GroupId, ThreadId: "thread_" + group.GroupId,
            AgentId: "agent_a", AgentNickname: "前台专员", TriggerMessageId: "msg_trig",
            TriggerUserId: "user_1", Content: "帮我看看这个问题", Mentions: [], MentionAll: false), CancellationToken.None);

        Assert.True(result.Accepted, "角色交接运行失败: " + result.ErrorCode);
        var events = f.Drain(inbox).Select(HubFixture.Parse).ToList();
        var start = events.First(e => e.GetProperty("type").GetString() == EventTypes.TextMessageStart);
        var messageId = start.GetProperty("messageId").GetString()!;
        var stored = f.Store.GetMessage(group.GroupId, messageId);
        Assert.NotNull(stored);
        // 最终正文是中继智能体（agent_resp）的答复，以宿主 agent_a 的身份发出
        Assert.Contains("答复专家", stored!.Content);
    }

    /// <summary>装配带编排流水线（1.1）的网关：宿主 agent_pipe 依次调用两个子智能体。</summary>
    private static AgentGateway CreatePipelineGateway(HubFixture f)
    {
        var options = new AgentOptions
        {
            Provider = "mock",
            Agents =
            [
                new AgentDefinition
                {
                    AgentId = "child1", Nickname = "代码助手", Description = "测试", Instructions = "你是代码助手，输出 代码：",
                    TriggerMode = AgentTriggerMode.Mentioned,
                },
                new AgentDefinition
                {
                    AgentId = "child2", Nickname = "文档助手", Description = "测试", Instructions = "你是文档助手，输出 文档：",
                    TriggerMode = AgentTriggerMode.Mentioned,
                },
                new AgentDefinition
                {
                    AgentId = "agent_pipe", Nickname = "总编辑", Description = "测试", Instructions = "聚合",
                    TriggerMode = AgentTriggerMode.Mentioned,
                    Pipeline =
                    [
                        new AgentPipelineStep { StepAgentId = "child1", Prompt = "先写代码" },
                        new AgentPipelineStep { StepAgentId = "child2", Prompt = "再补文档" },
                    ],
                },
            ],
        };
        var catalog = new AgentCatalog(options, NullLoggerFactory.Instance, new ServiceCollection().BuildServiceProvider());
        var services = new ServiceCollection().AddSingleton(f.Hub).BuildServiceProvider();
        return new AgentGateway(catalog, services, options, attachmentStore: null, NullLogger<AgentGateway>.Instance);
    }

    [Fact]
    public async Task Invoke_Pipeline_RunsStepsInOrder_AndAggregatesIntoGroup()
    {
        var f = new HubFixture();
        var group = await f.Hub.CreateGroupAsync(new GroupCreateRequest
        {
            GroupName = "g", OwnerId = "user_1", MemberIds = ["agent_pipe"],
            Members = [new MemberSeed { MemberId = "agent_pipe", MemberType = MemberType.Agent, Nickname = "总编辑" }],
        });
        var (conn, inbox) = f.NewConnection("user_1");
        await f.Hub.SubscribeAsync(conn, [group.GroupId]);
        f.Drain(inbox); // ACK + 快照

        var gateway = CreatePipelineGateway(f);
        var result = await gateway.InvokeAsync(new AgentInvocationContext(
            GroupId: group.GroupId, ThreadId: "thread_" + group.GroupId,
            AgentId: "agent_pipe", AgentNickname: "总编辑", TriggerMessageId: "msg_trig",
            TriggerUserId: "user_1", Content: "做一个登录模块", Mentions: [], MentionAll: false), CancellationToken.None);

        var inboxEvents = f.Drain(inbox);
        Assert.True(result.Accepted, "流水线运行失败: " + result.ErrorCode + " 事件=" + string.Join(";", inboxEvents));

        var events = inboxEvents.Select(HubFixture.Parse).ToList();
        var types = events.Select(e => e.GetProperty("type").GetString()).ToList();
        Assert.Equal(EventTypes.GroupTyping, types[0]);      // 开始时广播 typing
        Assert.Equal(EventTypes.TextMessageStart, types[1]);
        Assert.Contains(EventTypes.TextMessageContent, types);
        Assert.Equal(EventTypes.TextMessageEnd, types[^2]);
        Assert.Equal(EventTypes.GroupTyping, types[^1]);

        // 聚合正文应同时包含两个子智能体的输出
        var start = events[1];
        var messageId = start.GetProperty("messageId").GetString()!;
        var stored = f.Store.GetMessage(group.GroupId, messageId);
        Assert.NotNull(stored);
        Assert.True(stored!.Content.Contains("【代码助手】"), stored.Content);
        Assert.True(stored.Content.Contains("【文档助手】"), stored.Content);
    }

    private static AgentGateway CreateBridgeGateway(HubFixture f, string endpoint, string mode = "standard")
    {
        var options = new AgentOptions
        {
            Provider = "mock", // 即使配置了模型提供方，桥接角色也不走本地大模型
            AguiBridge = new AguiBridgeOptions { Mode = mode },
            Agents =
            [
                new AgentDefinition
                {
                    AgentId = "agent_bridge",
                    Nickname = "外部助手",
                    Description = "桥接外部 AG-UI 服务",
                    Instructions = "",
                    TriggerMode = AgentTriggerMode.Mentioned,
                    BridgeEndpoint = endpoint,
                },
            ],
        };
        var catalog = new AgentCatalog(options, NullLoggerFactory.Instance, new ServiceCollection().BuildServiceProvider());
        var services = new ServiceCollection().AddSingleton(f.Hub).BuildServiceProvider();
        return new AgentGateway(catalog, services, options, attachmentStore: null, NullLogger<AgentGateway>.Instance);
    }

    /// <summary>外部 AG-UI 增量游标持久化往返：restore（启动恢复）→ snapshot（定时落盘）数据一致，
    /// 网关重启后按话题的增量会话游标不丢失。</summary>
    [Fact]
    public void BridgeCursor_SnapshotAndRestore_RoundTrips()
    {
        var f = new HubFixture();
        var gateway = CreateBridgeGateway(f, "ws://127.0.0.1:1/ws");

        // 模拟启动恢复（扩展区 restore 回调）：话题级 + 群级游标各一条
        var restored = """
        {"agent_bridge|thread_g:topic_1":"msg_100","agent_bridge|thread_g":"msg_200"}
        """;
        using (var doc = JsonDocument.Parse(restored))
            gateway.RestoreBridgeCursors(doc.RootElement.Clone());

        // 快照（扩展区 snapshot 回调，落盘内容）与恢复内容一致
        var snap = (Dictionary<string, string>)gateway.SnapshotBridgeCursors();
        Assert.Equal(2, snap.Count);
        Assert.Equal("msg_100", snap["agent_bridge|thread_g:topic_1"]);
        Assert.Equal("msg_200", snap["agent_bridge|thread_g"]);

        // JSON 序列化往返（落盘 → 再恢复）不丢数据
        var json = JsonSerializer.Serialize(snap, AguiJson.Options);
        using var doc2 = JsonDocument.Parse(json);
        var gateway2 = CreateBridgeGateway(f, "ws://127.0.0.1:1/ws");
        gateway2.RestoreBridgeCursors(doc2.RootElement.Clone());
        var snap2 = (Dictionary<string, string>)gateway2.SnapshotBridgeCursors();
        Assert.Equal(2, snap2.Count);
        Assert.Equal("msg_100", snap2["agent_bridge|thread_g:topic_1"]);
        Assert.Equal("msg_200", snap2["agent_bridge|thread_g"]);
    }

    /// <summary>记录个人记忆检索调用次数的测试替身。</summary>
    private sealed class CountingMemory : IMessageMemory
    {
        public int PersonSearches;
        public IReadOnlyList<MessageMemoryHit> GroupHits { get; set; } = [];
        public void Remember(MessageMemoryEntry entry) { }
        public void Forget(string groupId, string messageId) { }
        public void RemoveGroup(string groupId) { }
        public Task<IReadOnlyList<MessageMemoryHit>> SearchAsync(string groupId, string agentId, string query, CancellationToken ct = default)
            => Task.FromResult(GroupHits);
        public Task<IReadOnlyList<MessageMemoryHit>> SearchPersonAsync(string personId, string currentGroupId, string query, CancellationToken ct = default)
        {
            PersonSearches++;
            return Task.FromResult<IReadOnlyList<MessageMemoryHit>>([]);
        }
        public IReadOnlyList<MessageMemoryItem> ListMessages(string? groupId, string? senderId, string? keyword, int limit, int offset) => [];
        public long CountMessages(string? groupId, string? senderId, string? keyword) => 0;
        public IReadOnlyList<MessageMemoryGroupStat> GroupStats() => [];
        public bool DeleteByMessageId(string messageId) => false;
        public bool UpdateImportance(string messageId, int importance) => false;
        public int ForgetGroup(string? groupId, double? retentionHours) => 0;
        public int PruneExpired() => 0;
    }

    /// <summary>个人记忆默认关闭：需全局开启（PersonalTopK>0）+ 智能体开启 + 触发者用户开启，三重条件同时满足才注入
    /// （MSAGENT AIContextProvider：InvokingAsync 返回 AIContext.Instructions 注入）。</summary>
    [Theory]
    [InlineData(true, true, true, 1)]   // 都开启 → 检索
    [InlineData(true, false, true, 0)]  // 智能体未开启 → 不检索
    [InlineData(true, true, false, 0)]  // 用户未开启（未注册默认关）→ 不检索
    [InlineData(false, true, true, 0)]  // 全局关闭（PersonalTopK=0）→ 不检索
    public async Task MemoryProvider_PersonalMemory_RequiresAllSwitches(bool globalOn, bool agentOn, bool userOn, int expected)
    {
        var f = new HubFixture();
        var triggerUserId = userOn ? "user_pm" : "user_off";
        if (userOn)
        {
            f.Users.AddUser(new UserAccount
            {
                UserId = "user_pm", Username = "pm", PasswordHash = "h", PasswordSalt = "s",
                CreatedAt = 1, UpdatedAt = 1, PersonalMemoryEnabled = true,
            });
        }
        var group = await HubFixture.CreateGroupAsync(f.Hub, "g", triggerUserId, "agent_a");

        var options = new AgentOptions
        {
            Provider = "mock",
            Memory = new MemoryOptions { PersonalTopK = globalOn ? 3 : 0 },
            Agents =
            [
                new AgentDefinition
                {
                    AgentId = "agent_a", Nickname = "测试助手", Description = "测试", Instructions = "你是测试助手",
                    TriggerMode = AgentTriggerMode.Mentioned, PersonalMemoryEnabled = agentOn,
                },
            ],
        };
        var catalog = new AgentCatalog(options, NullLoggerFactory.Instance, new ServiceCollection().BuildServiceProvider());
        var services = new ServiceCollection().AddSingleton(f.Hub).AddSingleton(catalog).BuildServiceProvider();
        var memory = new CountingMemory();
        var provider = new MemoryContextProvider(options, services, NullLogger<MemoryContextProvider>.Instance, memory);

        var context = new AgentInvocationContext(
            GroupId: group.GroupId, ThreadId: "thread_" + group.GroupId, AgentId: "agent_a", AgentNickname: "测试助手",
            TriggerMessageId: "msg_trigger", TriggerUserId: triggerUserId, Content: "你好", Mentions: [], MentionAll: false);

        var prev = AgentGateway.AmbientContext.Value;
        AgentGateway.AmbientContext.Value = context;
        try
        {
            var testAgent = new ChatClientAgent(new MockChatClient(options.Agents[0]), options.Agents[0].Instructions,
                options.Agents[0].Nickname, options.Agents[0].Description, null, NullLoggerFactory.Instance, services);
#pragma warning disable MAAI001 // experimental API（1.17 评估版 InvokingContext）
            var aiContext = await provider.InvokingAsync(new AIContextProvider.InvokingContext(testAgent, null, new AIContext()), CancellationToken.None);
#pragma warning restore MAAI001
            Assert.Equal(expected, memory.PersonSearches);
        }
        finally { AgentGateway.AmbientContext.Value = prev; }
    }

    /// <summary>群记忆经 MemoryContextProvider 注入 AIContext.Instructions（MSAGENT 标准注入点）。</summary>
    [Fact]
    public async Task MemoryProvider_InjectsGroupMemoryIntoInstructions()
    {
        var f = new HubFixture();
        var group = await HubFixture.CreateGroupAsync(f.Hub, "g", "user_1", "agent_a");
        var options = new AgentOptions
        {
            Provider = "mock",
            Memory = new MemoryOptions { TopK = 5, MinScore = 0.1 },
            Agents =
            [
                new AgentDefinition
                {
                    AgentId = "agent_a", Nickname = "测试助手", Description = "测试", Instructions = "你是测试助手",
                    TriggerMode = AgentTriggerMode.Mentioned,
                },
            ],
        };
        var catalog = new AgentCatalog(options, NullLoggerFactory.Instance, new ServiceCollection().BuildServiceProvider());
        var services = new ServiceCollection().AddSingleton(f.Hub).AddSingleton(catalog).BuildServiceProvider();
        var memory = new CountingMemory
        {
            GroupHits = [new MessageMemoryHit("m_old", "历史决策：用 WebSocket 推送", "user_1", 1750000000000, 0.8)],
        };
        var provider = new MemoryContextProvider(options, services, NullLogger<MemoryContextProvider>.Instance, memory);

        var context = new AgentInvocationContext(
            GroupId: group.GroupId, ThreadId: "thread_" + group.GroupId, AgentId: "agent_a", AgentNickname: "测试助手",
            TriggerMessageId: "msg_trigger", TriggerUserId: "user_1", Content: "推送方案", Mentions: [], MentionAll: false);

        var prev = AgentGateway.AmbientContext.Value;
        AgentGateway.AmbientContext.Value = context;
        try
        {
            var testAgent = new ChatClientAgent(new MockChatClient(options.Agents[0]), options.Agents[0].Instructions,
                options.Agents[0].Nickname, options.Agents[0].Description, null, NullLoggerFactory.Instance, services);
#pragma warning disable MAAI001 // experimental API（1.17 评估版 InvokingContext）
            var aiContext = await provider.InvokingAsync(new AIContextProvider.InvokingContext(testAgent, null, new AIContext()), CancellationToken.None);
#pragma warning restore MAAI001
            Assert.Contains("历史决策：用 WebSocket 推送", aiContext.Instructions);
            Assert.Contains("相关历史记忆", aiContext.Instructions);
        }
        finally { AgentGateway.AmbientContext.Value = prev; }
    }

    private static AgentGateway CreateContextualGateway(HubFixture f)
    {
        var options = new AgentOptions
        {
            Provider = "mock",
            Agents =
            [
                new AgentDefinition
                {
                    AgentId = "agent_ctx",
                    Nickname = "语境助手",
                    Description = "测试",
                    Instructions = "你是语境助手",
                    TriggerMode = AgentTriggerMode.Contextual,
                },
            ],
        };
        var catalog = new AgentCatalog(options, NullLoggerFactory.Instance, new ServiceCollection().BuildServiceProvider());
        var services = new ServiceCollection().AddSingleton(f.Hub).BuildServiceProvider();
        return new AgentGateway(catalog, services, options, attachmentStore: null, NullLogger<AgentGateway>.Instance);
    }

    [Fact]
    public async Task Invoke_StreamsAgentReply_IntoGroupFanout()
    {
        var f = new HubFixture();
        var group = await f.Hub.CreateGroupAsync(new GroupCreateRequest
        {
            GroupName = "g",
            OwnerId = "user_1",
            MemberIds = ["agent_a"],
            Members =
            [
                new MemberSeed { MemberId = "agent_a", MemberType = MemberType.Agent, Nickname = "测试助手" },
            ],
        });
        var (conn, inbox) = f.NewConnection("user_1");
        await f.Hub.SubscribeAsync(conn, [group.GroupId]);
        f.Drain(inbox); // ACK + 快照

        var gateway = CreateGateway(f, out _);

        var result = await gateway.InvokeAsync(new AgentInvocationContext(
            GroupId: group.GroupId,
            ThreadId: "thread_" + group.GroupId,
            AgentId: "agent_a",
            AgentNickname: "测试助手",
            TriggerMessageId: "msg_trigger",
            TriggerUserId: "user_1",
            Content: "帮我梳理需求",
            Mentions: [],
            MentionAll: false), CancellationToken.None);

        var inboxEvents = f.Drain(inbox);
        Assert.True(result.Accepted, "网关运行失败: " + result.ErrorCode + " 事件=" + string.Join(";", inboxEvents));
        Assert.NotNull(result.RunId);

        // 事件序列：typing(true) → START → CONTENT xN → END → typing(false)
        var events = inboxEvents.Select(HubFixture.Parse).ToList();
        var types = events.Select(e => e.GetProperty("type").GetString()).ToList();

        Assert.Equal(EventTypes.GroupTyping, types[0]);
        Assert.Equal(EventTypes.TextMessageStart, types[1]);
        Assert.Contains(EventTypes.TextMessageContent, types);
        Assert.Equal(EventTypes.TextMessageEnd, types[^2]);
        Assert.Equal(EventTypes.GroupTyping, types[^1]);

        var start = events[1];
        Assert.Equal("assistant", start.GetProperty("role").GetString());
        Assert.Equal("agent", start.GetProperty("senderType").GetString());
        Assert.Equal("agent_a", start.GetProperty("senderId").GetString());
        Assert.Equal("测试助手", start.GetProperty("senderNickname").GetString());
        Assert.Equal("msg_trigger", start.GetProperty("replyToMessageId").GetString());
        Assert.Equal(result.RunId, start.GetProperty("runId").GetString());
        Assert.Equal(group.GroupId, start.GetProperty("groupId").GetString());

        // 消息落库且内容完整（delta 拼接）
        var messageId = start.GetProperty("messageId").GetString()!;
        var stored = f.Store.GetMessage(group.GroupId, messageId);
        Assert.NotNull(stored);
        Assert.Equal(MemberType.Agent, stored!.SenderType);
        Assert.Contains("测试助手", stored.Content);
        Assert.True(stored.Content.Length > 0);
    }

    /// <summary>装配带 TaskService 的网关：任务编排回写闭环需在网关 DI 中注册 TaskService。</summary>
    private static AgentGateway CreateGatewayWithTasks(HubFixture f, ITaskStore store, out AgentOptions options)
    {
        options = new AgentOptions
        {
            Provider = "mock",
            Agents =
            [
                new AgentDefinition
                {
                    AgentId = "agent_t",
                    Nickname = "工作助手",
                    Description = "测试",
                    Instructions = "你是工作助手",
                    TriggerMode = AgentTriggerMode.Mentioned,
                },
            ],
        };
        var catalog = new AgentCatalog(options, NullLoggerFactory.Instance, new ServiceCollection().BuildServiceProvider());
        var services = new ServiceCollection()
            .AddSingleton(f.Hub)
            .AddSingleton<ITaskStore>(store)
            .AddSingleton(new TaskService(store, NullLogger<TaskService>.Instance))
            .BuildServiceProvider();
        return new AgentGateway(catalog, services, options, attachmentStore: null, NullLogger<AgentGateway>.Instance);
    }

    /// <summary>任务编排闭环：携带 TaskId 的运行直接完成时，网关把任务回写为 Finished（结果 = 智能体回复全文）。</summary>
    [Fact]
    public async Task Invoke_WithTaskId_MarksTaskFinishedOnDirectCompletion()
    {
        var f = new HubFixture();
        var group = await f.Hub.CreateGroupAsync(new GroupCreateRequest
        {
            GroupName = "g",
            OwnerId = "user_1",
            MemberIds = ["agent_t"],
            Members = [new MemberSeed { MemberId = "agent_t", MemberType = MemberType.Agent, Nickname = "工作助手" }],
        });
        var store = new InMemoryTaskStore();
        var tasks = new TaskService(store, NullLogger<TaskService>.Instance);
        var taskId = tasks.CreateTask(group.GroupId, "agent_t", "user_1", "main", "任务", "写一个报告");
        tasks.MarkRunning(taskId);

        var gateway = CreateGatewayWithTasks(f, store, out _);
        var result = await gateway.InvokeAsync(new AgentInvocationContext(
            GroupId: group.GroupId,
            ThreadId: "thread_" + group.GroupId,
            AgentId: "agent_t",
            AgentNickname: "工作助手",
            TriggerMessageId: "msg_trigger",
            TriggerUserId: "user_1",
            Content: "帮我梳理需求",
            Mentions: [],
            MentionAll: false,
            TaskId: taskId), CancellationToken.None);

        Assert.True(result.Accepted, "网关运行失败: " + result.ErrorCode);
        var task = tasks.Get(taskId)!;
        Assert.Equal(WorkTaskStatus.Finished, task.Status);
        Assert.False(string.IsNullOrWhiteSpace(task.Result));
    }

    [Fact]
    public async Task Invoke_WithTaskId_NoTaskService_DoesNotThrow()
    {
        // 未注册 TaskService 的网关（原始 CreateGateway）带 TaskId 运行不应抛异常
        var f = new HubFixture();
        var group = await f.Hub.CreateGroupAsync(new GroupCreateRequest
        {
            GroupName = "g",
            OwnerId = "user_1",
            MemberIds = ["agent_a"],
            Members = [new MemberSeed { MemberId = "agent_a", MemberType = MemberType.Agent, Nickname = "测试助手" }],
        });
        var gateway = CreateGateway(f, out _);
        var result = await gateway.InvokeAsync(new AgentInvocationContext(
            GroupId: group.GroupId,
            ThreadId: "thread_" + group.GroupId,
            AgentId: "agent_a",
            AgentNickname: "测试助手",
            TriggerMessageId: "msg_trigger",
            TriggerUserId: "user_1",
            Content: "帮我梳理需求",
            Mentions: [],
            MentionAll: false,
            TaskId: "task_nonexistent"), CancellationToken.None);
        Assert.True(result.Accepted, "网关运行失败: " + result.ErrorCode);
    }

    [Fact]
    public async Task Invoke_ContextualMode_SpeaksWhenContextDecidesYes()
    {
        var f = new HubFixture();
        var group = await f.Hub.CreateGroupAsync(new GroupCreateRequest
        {
            GroupName = "g",
            OwnerId = "user_1",
            MemberIds = ["agent_ctx"],
            Members =
            [
                new MemberSeed { MemberId = "agent_ctx", MemberType = MemberType.Agent, Nickname = "语境助手" },
            ],
        });
        var (conn, inbox) = f.NewConnection("user_1");
        await f.Hub.SubscribeAsync(conn, [group.GroupId]);
        f.Drain(inbox); // ACK + 快照

        var gateway = CreateContextualGateway(f);

        // 消息包含「帮我」→ mock 语境决策为 YES → 正常发言
        var result = await gateway.InvokeAsync(new AgentInvocationContext(
            GroupId: group.GroupId,
            ThreadId: "thread_" + group.GroupId,
            AgentId: "agent_ctx",
            AgentNickname: "语境助手",
            TriggerMessageId: "msg_trigger",
            TriggerUserId: "user_1",
            Content: "请帮我看看这个方案",
            Mentions: [],
            MentionAll: false), CancellationToken.None);

        Assert.True(result.Accepted, "发言失败: " + result.ErrorCode);
        var types = f.Drain(inbox).Select(HubFixture.Parse)
            .Select(e => e.GetProperty("type").GetString()).ToList();
        Assert.Contains(EventTypes.TextMessageStart, types);
        Assert.Contains(EventTypes.TextMessageContent, types);
        Assert.Contains(EventTypes.TextMessageEnd, types);
    }

    [Fact]
    public async Task Invoke_ContextualMode_StaysSilentWhenContextDecidesNo()
    {
        var f = new HubFixture();
        var group = await f.Hub.CreateGroupAsync(new GroupCreateRequest
        {
            GroupName = "g",
            OwnerId = "user_1",
            MemberIds = ["agent_ctx"],
            Members =
            [
                new MemberSeed { MemberId = "agent_ctx", MemberType = MemberType.Agent, Nickname = "语境助手" },
            ],
        });
        var (conn, inbox) = f.NewConnection("user_1");
        await f.Hub.SubscribeAsync(conn, [group.GroupId]);
        f.Drain(inbox);

        var gateway = CreateContextualGateway(f);

        // 纯寒暄消息 → mock 语境决策为 NO → 保持沉默（不发任何事件）
        var result = await gateway.InvokeAsync(new AgentInvocationContext(
            GroupId: group.GroupId,
            ThreadId: "thread_" + group.GroupId,
            AgentId: "agent_ctx",
            AgentNickname: "语境助手",
            TriggerMessageId: "msg_trigger",
            TriggerUserId: "user_1",
            Content: "今天天气不错",
            Mentions: [],
            MentionAll: false), CancellationToken.None);

        Assert.False(result.Accepted);
        Assert.Equal("AGENT_DECIDED_SILENT", result.ErrorCode);
        Assert.Empty(f.Drain(inbox)); // 没有任何事件广播
    }

    [Fact]
    public async Task Invoke_GroupTriggerMode_OverridesRoleDefault()
    {
        var f = new HubFixture();
        var group = await f.Hub.CreateGroupAsync(new GroupCreateRequest
        {
            GroupName = "g",
            OwnerId = "user_1",
            MemberIds = ["agent_a"],
            Members =
            [
                new MemberSeed { MemberId = "agent_a", MemberType = MemberType.Agent, Nickname = "测试助手" },
            ],
        });
        var (conn, inbox) = f.NewConnection("user_1");
        await f.Hub.SubscribeAsync(conn, [group.GroupId]);
        f.Drain(inbox);

        var gateway = CreateGateway(f, out _); // 角色默认 TriggerMode = Mentioned

        // 群内把触发方式覆盖为 contextual：语境决策 YES（含「帮我」）→ 应发言
        var speak = await gateway.InvokeAsync(new AgentInvocationContext(
            GroupId: group.GroupId,
            ThreadId: "thread_" + group.GroupId,
            AgentId: "agent_a",
            AgentNickname: "测试助手",
            TriggerMessageId: "msg_trigger",
            TriggerUserId: "user_1",
            Content: "请帮我看看这个方案",
            Mentions: [],
            MentionAll: false,
            TriggerMode: AgentTriggerMode.Contextual), CancellationToken.None);
        Assert.True(speak.Accepted, "发言失败: " + speak.ErrorCode);
        var types = f.Drain(inbox).Select(HubFixture.Parse)
            .Select(e => e.GetProperty("type").GetString()).ToList();
        Assert.Contains(EventTypes.TextMessageStart, types);
        Assert.Contains(EventTypes.TextMessageContent, types);
        Assert.Contains(EventTypes.TextMessageEnd, types);

        // 同一角色、同群，群内语境决策 NO（纯寒暄）→ 保持沉默（证明走的是群内覆盖的语境分支）
        var silent = await gateway.InvokeAsync(new AgentInvocationContext(
            GroupId: group.GroupId,
            ThreadId: "thread_" + group.GroupId,
            AgentId: "agent_a",
            AgentNickname: "测试助手",
            TriggerMessageId: "msg_trigger",
            TriggerUserId: "user_1",
            Content: "今天天气不错",
            Mentions: [],
            MentionAll: false,
            TriggerMode: AgentTriggerMode.Contextual), CancellationToken.None);
        Assert.False(silent.Accepted);
        Assert.Equal("AGENT_DECIDED_SILENT", silent.ErrorCode);
        Assert.Empty(f.Drain(inbox));
    }

    [Fact]
    public async Task Invoke_UnknownAgent_ReturnsNotConfigured()
    {
        var f = new HubFixture();
        var group = await HubFixture.CreateGroupAsync(f.Hub, "g", "user_1", "agent_a");
        var gateway = CreateGateway(f, out _);

        var result = await gateway.InvokeAsync(new AgentInvocationContext(
            group.GroupId, "thread_" + group.GroupId, "agent_missing", "x", "m", "u", "hi", [], false), CancellationToken.None);

        Assert.False(result.Accepted);
        Assert.Equal("AGENT_NOT_CONFIGURED", result.ErrorCode);
    }

    [Fact]
    public async Task Invoke_InjectsRecentGroupHistory_WindowedAndTruncated()
    {
        var f = new HubFixture();
        var group = await f.Hub.CreateGroupAsync(new GroupCreateRequest
        {
            GroupName = "g",
            OwnerId = "user_1",
            MemberIds = ["agent_a"],
            Members =
            [
                new MemberSeed { MemberId = "agent_a", MemberType = MemberType.Agent, Nickname = "测试助手" },
            ],
        });
        var gateway = CreateGateway(f, out _);

        // 群历史：正常消息 + 超长消息（超截断阈值）+ 撤回消息
        await f.Hub.SendMessageAsync(new GroupMessageSendRequest { GroupId = group.GroupId, UserId = "user_1", Content = "第一条历史消息" });
        var longPrefix = "超长消息开头标记";
        var longMsg = await f.Hub.SendMessageAsync(new GroupMessageSendRequest
        {
            GroupId = group.GroupId,
            UserId = "user_1",
            Content = longPrefix + new string('长', 600),
        });
        var recalled = await f.Hub.SendMessageAsync(new GroupMessageSendRequest { GroupId = group.GroupId, UserId = "user_1", Content = "这条将被撤回，不应进入上下文" });
        await f.Hub.RecallMessageAsync(new GroupMessageRecallRequest { GroupId = group.GroupId, MessageId = recalled.MessageId, OperatorId = "user_1" });

        var result = await gateway.InvokeAsync(new AgentInvocationContext(
            GroupId: group.GroupId,
            ThreadId: "thread_" + group.GroupId,
            AgentId: "agent_a",
            AgentNickname: "测试助手",
            TriggerMessageId: "msg_trigger",
            TriggerUserId: "user_1",
            Content: "总结一下",
            Mentions: [],
            MentionAll: false), CancellationToken.None);
        Assert.True(result.Accepted, result.ErrorCode ?? "");

        // 智能体回复已落库，直接取最后一条 agent 消息断言上下文注入
        var agentMsg = f.Store.AllMessages(group.GroupId).Last(m => m.SenderType == MemberType.Agent);

        Assert.Contains("第一条历史消息", agentMsg.Content);              // 窗口包含最近正常消息
        Assert.Contains(longPrefix, agentMsg.Content);                    // 超长消息被包含（截断后）
        // 截断断言：超长消息 610 字符 → 单条截断 500，回复中“长”字符总数应 ≤ 500
        var longCharCount = agentMsg.Content.Count(c => c == '长');
        Assert.True(longCharCount <= 500, $"截断未生效：{longCharCount} 个「长」");
        Assert.DoesNotContain("不应进入上下文", agentMsg.Content);         // 撤回消息不进上下文
        Assert.Contains("总结一下", agentMsg.Content);                    // 当前消息
    }

    // ================= AG-UI 桥接（对接外部 AG-UI 服务） =================

    [Fact]
    public async Task Invoke_BridgeStandardMode_StreamsExternalReplyIntoGroup()
    {
        var f = new HubFixture();
        using var server = await StartFakeAguiServerAsync(async (ws, ct) =>
        {
            // 读上行消息（RunAgentInput 结构），取 user 消息文本
            var userText = await ReadOneJsonAsync(ws, ct);
            var text = userText.RootElement
                .GetProperty("messages")[0]
                .GetProperty("content").GetString();

            // 回标准 AG-UI 事件流：ASSISTANT_MESSAGE（累计）+ RUN_UPDATED（增量）+ RUN_COMPLETED
            await SendJsonAsync(ws, new
            {
                type = "ASSISTANT_MESSAGE",
                messageId = "msg_ext",
                threadId = "t",
                requestId = "r",
                context = new { time = DateTimeOffset.UtcNow.ToString("O") },
                payload = new { content = new[] { new { type = "text", text = "外部回复：" + text } } },
            }, ct);
            await SendJsonAsync(ws, new
            {
                type = "RUN_UPDATED",
                runId = "run_ext",
                threadId = "t",
                requestId = "r",
                context = new { time = DateTimeOffset.UtcNow.ToString("O") },
                payload = new { delta = "，已收到你的请求" },
            }, ct);
            await SendJsonAsync(ws, new { type = "RUN_COMPLETED", runId = "run_ext", threadId = "t", requestId = "r", context = new { time = "" }, payload = new { } }, ct);
        });

        var group = await f.Hub.CreateGroupAsync(new GroupCreateRequest
        {
            GroupName = "g",
            OwnerId = "user_1",
            MemberIds = ["agent_bridge"],
            Members =
            [
                new MemberSeed { MemberId = "agent_bridge", MemberType = MemberType.Agent, Nickname = "外部助手" },
            ],
        });
        var gateway = CreateBridgeGateway(f, server.Url);
        var result = await gateway.InvokeAsync(new AgentInvocationContext(
            group.GroupId, "thread_" + group.GroupId, "agent_bridge", "外部助手",
            "msg_trigger", "user_1", "你好", [], false), CancellationToken.None);

        Assert.True(result.Accepted, "桥接失败: " + result.ErrorCode + " | 服务端: " + server.LastError);
        var reply = f.Store.AllMessages(group.GroupId).Last(m => m.SenderType == MemberType.Agent);
        Assert.Contains("外部回复：你好", reply.Content);
        Assert.Contains("已收到你的请求", reply.Content);
    }

    [Fact]
    public async Task Invoke_BridgeHubMode_SubscribesAndFiltersOwnEcho()
    {
        var f = new HubFixture();
        using var server = await StartFakeAguiServerAsync(async (ws, ct) =>
        {
            // 连接后先收到 GROUP_SUBSCRIBE（订阅），再收到 GROUP_MESSAGE_SEND（发送）
            var sub = await ReadOneJsonAsync(ws, ct);
            Assert.Equal("GROUP_SUBSCRIBE", sub.RootElement.GetProperty("type").GetString());
            var send = await ReadOneJsonAsync(ws, ct);
            Assert.Equal("GROUP_MESSAGE_SEND", send.RootElement.GetProperty("type").GetString());

            // 模拟外部 Hub：先回显发送者自己的消息（应被过滤），再回外部 agent 的回复
            await SendJsonAsync(ws, new { type = "TEXT_MESSAGE_START", messageId = "msg_echo", role = "user", threadId = "t", groupId = "g", senderId = "agent_bridge", senderNickname = "外部助手", timestamp = 1L }, ct);
            await SendJsonAsync(ws, new { type = "TEXT_MESSAGE_CONTENT", messageId = "msg_echo", delta = "自己消息的回显不应进入回复" }, ct);
            await SendJsonAsync(ws, new { type = "TEXT_MESSAGE_END", messageId = "msg_echo", groupId = "g", timestamp = 1L }, ct);

            await SendJsonAsync(ws, new { type = "TEXT_MESSAGE_START", messageId = "msg_ext_reply", role = "assistant", threadId = "t", groupId = "g", senderId = "agent_ext", senderNickname = "外部智能体", replyToMessageId = "msg_echo", timestamp = 1L }, ct);
            await SendJsonAsync(ws, new { type = "TEXT_MESSAGE_CONTENT", messageId = "msg_ext_reply", delta = "外部 Hub 智能体回复" }, ct);
            await SendJsonAsync(ws, new { type = "TEXT_MESSAGE_CONTENT", messageId = "msg_ext_reply", delta = "（第二段）" }, ct);
            await SendJsonAsync(ws, new { type = "TEXT_MESSAGE_END", messageId = "msg_ext_reply", groupId = "g", timestamp = 1L }, ct);
        });

        var group = await f.Hub.CreateGroupAsync(new GroupCreateRequest
        {
            GroupName = "g",
            OwnerId = "user_1",
            MemberIds = ["agent_bridge"],
            Members =
            [
                new MemberSeed { MemberId = "agent_bridge", MemberType = MemberType.Agent, Nickname = "外部助手" },
            ],
        });
        var gateway = CreateBridgeGateway(f, server.Url, mode: "hub");
        var result = await gateway.InvokeAsync(new AgentInvocationContext(
            group.GroupId, "thread_" + group.GroupId, "agent_bridge", "外部助手",
            "msg_trigger", "user_1", "帮我看看", [], false), CancellationToken.None);

        Assert.True(result.Accepted, "桥接失败: " + result.ErrorCode + " | 服务端: " + server.LastError);
        var reply = f.Store.AllMessages(group.GroupId).Last(m => m.SenderType == MemberType.Agent);
        Assert.Contains("外部 Hub 智能体回复", reply.Content);
        Assert.Contains("（第二段）", reply.Content);
        Assert.DoesNotContain("自己消息的回显", reply.Content); // 自己的回显被过滤
    }

    // ---- HTTP(S) 传输桥接 ----

    [Fact]
    public async Task Invoke_BridgeHttp_StandardSse_StreamsReply()
    {
        var f = new HubFixture();
        var server = await StartFakeHttpAguiServerAsync(app =>
        {
            app.MapPost("/", async (HttpContext ctx) =>
            {
                // 用 AGUI.Abstractions（与用户服务器同款）真实反序列化发送体：
                // 结构不符会抛异常 → 服务端 500 → 桥接失败，测试即暴露问题
                using var reader = new StreamReader(ctx.Request.Body);
                var body = await reader.ReadToEndAsync();
                var input = JsonSerializer.Deserialize<RunAgentInput>(body);
                Assert.NotNull(input);
                var um = Assert.IsType<AGUIUserMessage>(Assert.Single(input!.Messages));
                Assert.Equal("你好", um.Content.Value);
                // 官方 AGUIChatClient 默认不注入 context（空数组）
                Assert.NotNull(input.Context);
                Assert.Empty(input.Context);

                ctx.Response.ContentType = "text/event-stream";
                // AGUI.Abstractions（AG-UI .NET SDK）服务端事件流：RUN_STARTED → TEXT_MESSAGE_* → RUN_FINISHED
                // （官方 AGUIChatClient 会校验 RUN_FINISHED 与 RUN_STARTED 的 threadId/runId 一致）
                await WriteSseAsync(ctx, new { type = "RUN_STARTED", threadId = "t", runId = "run_ext", input = new { }, timestamp = 1L });
                await WriteSseAsync(ctx, new { type = "TEXT_MESSAGE_START", messageId = "msg_ext", role = "assistant", name = "外部专家" });
                await WriteSseAsync(ctx, new { type = "TEXT_MESSAGE_CONTENT", messageId = "msg_ext", delta = "AGUI 外部回复：" });
                await WriteSseAsync(ctx, new { type = "TEXT_MESSAGE_CONTENT", messageId = "msg_ext", delta = "你好" });
                await WriteSseAsync(ctx, new { type = "TEXT_MESSAGE_END", messageId = "msg_ext" });
                await WriteSseAsync(ctx, new { type = "RUN_FINISHED", threadId = "t", runId = "run_ext", outcome = new { type = "success" } });
                await ctx.Response.Body.FlushAsync();
            });
        });
        await using var _ = server;

        var group = await f.Hub.CreateGroupAsync(new GroupCreateRequest
        {
            GroupName = "g", OwnerId = "user_1", MemberIds = ["agent_bridge"],
            Members = [new MemberSeed { MemberId = "agent_bridge", MemberType = MemberType.Agent, Nickname = "外部助手" }],
        });
        var gateway = CreateBridgeGateway(f, server.HttpBase);
        var result = await gateway.InvokeAsync(new AgentInvocationContext(
            group.GroupId, "thread_" + group.GroupId, "agent_bridge", "外部助手",
            "msg_trigger", "user_1", "你好", [], false), CancellationToken.None);

        Assert.True(result.Accepted, "桥接失败: " + result.ErrorCode);
        var reply = f.Store.AllMessages(group.GroupId).Last(m => m.SenderType == MemberType.Agent);
        Assert.Contains("AGUI 外部回复：你好", reply.Content);
    }

    [Fact]
    public async Task Invoke_BridgeHttp_StandardSse_OneShotReply()
    {
        var f = new HubFixture();
        var server = await StartFakeHttpAguiServerAsync(app =>
        {
            app.MapPost("/", async (HttpContext ctx) =>
            {
                // 官方 AGUIChatClient 只消费 SSE 事件流：RUN_STARTED → 一段 TEXT_MESSAGE → RUN_FINISHED
                ctx.Response.ContentType = "text/event-stream";
                await WriteSseAsync(ctx, new { type = "RUN_STARTED", threadId = "t", runId = "run_oneshot", input = new { }, timestamp = 1L });
                await WriteSseAsync(ctx, new { type = "TEXT_MESSAGE_START", messageId = "msg_oneshot", role = "assistant", name = "外部专家" });
                await WriteSseAsync(ctx, new { type = "TEXT_MESSAGE_CONTENT", messageId = "msg_oneshot", delta = "一次性回复：需求已收到" });
                await WriteSseAsync(ctx, new { type = "TEXT_MESSAGE_END", messageId = "msg_oneshot" });
                await WriteSseAsync(ctx, new { type = "RUN_FINISHED", threadId = "t", runId = "run_oneshot", outcome = new { type = "success" } });
                await ctx.Response.Body.FlushAsync();
            });
        });
        await using var _ = server;

        var group = await f.Hub.CreateGroupAsync(new GroupCreateRequest
        {
            GroupName = "g", OwnerId = "user_1", MemberIds = ["agent_bridge"],
            Members = [new MemberSeed { MemberId = "agent_bridge", MemberType = MemberType.Agent, Nickname = "外部助手" }],
        });
        var gateway = CreateBridgeGateway(f, server.HttpBase);
        var result = await gateway.InvokeAsync(new AgentInvocationContext(
            group.GroupId, "thread_" + group.GroupId, "agent_bridge", "外部助手",
            "msg_trigger", "user_1", "需求", [], false), CancellationToken.None);

        Assert.True(result.Accepted, "桥接失败: " + result.ErrorCode);
        var reply = f.Store.AllMessages(group.GroupId).Last(m => m.SenderType == MemberType.Agent);
        Assert.Contains("一次性回复：需求已收到", reply.Content);
    }

    [Fact]
    public async Task Invoke_BridgeHttp_StandardSse_AccumulatesMultiDelta()
    {
        var f = new HubFixture();
        var server = await StartFakeHttpAguiServerAsync(app =>
        {
            app.MapPost("/", async (HttpContext ctx) =>
            {
                // 官方 AGUIChatClient 把 TEXT_MESSAGE_CONTENT 增量逐帧映射为 TextContent；
                // 桥接层按累计文本取增量，验证多段 delta 拼接
                ctx.Response.ContentType = "text/event-stream";
                await WriteSseAsync(ctx, new { type = "RUN_STARTED", threadId = "t", runId = "run_multi", input = new { }, timestamp = 1L });
                await WriteSseAsync(ctx, new { type = "TEXT_MESSAGE_START", messageId = "m_asst", role = "assistant", name = "外部专家" });
                await WriteSseAsync(ctx, new { type = "TEXT_MESSAGE_CONTENT", messageId = "m_asst", delta = "第一段：需求已收到" });
                await WriteSseAsync(ctx, new { type = "TEXT_MESSAGE_CONTENT", messageId = "m_asst", delta = "，第二段补充说明" });
                await WriteSseAsync(ctx, new { type = "TEXT_MESSAGE_END", messageId = "m_asst" });
                await WriteSseAsync(ctx, new { type = "RUN_FINISHED", threadId = "t", runId = "run_multi", outcome = new { type = "success" } });
                await ctx.Response.Body.FlushAsync();
            });
        });
        await using var _ = server;

        var group = await f.Hub.CreateGroupAsync(new GroupCreateRequest
        {
            GroupName = "g", OwnerId = "user_1", MemberIds = ["agent_bridge"],
            Members = [new MemberSeed { MemberId = "agent_bridge", MemberType = MemberType.Agent, Nickname = "外部助手" }],
        });
        var gateway = CreateBridgeGateway(f, server.HttpBase);
        var result = await gateway.InvokeAsync(new AgentInvocationContext(
            group.GroupId, "thread_" + group.GroupId, "agent_bridge", "外部助手",
            "msg_trigger", "user_1", "你好", [], false), CancellationToken.None);

        Assert.True(result.Accepted, "桥接失败: " + result.ErrorCode);
        var reply = f.Store.AllMessages(group.GroupId).Last(m => m.SenderType == MemberType.Agent);
        Assert.Contains("第一段：需求已收到，第二段补充说明", reply.Content);
    }

    /// <summary>回归：HTTP standard 桥接遇到 AGUI.AspNetCore 真实事件序列——TEXT_MESSAGE_END 后仍跟
    /// TOOL_CALL_* 与 RUN_FINISHED 审批中断，必须忽略 TEXT_MESSAGE_END（非运行终止）并检测到中断 → 广播审批卡片。</summary>
    [Fact]
    public async Task Invoke_BridgeHttp_StandardSse_EndBeforeRunFinishedInterrupt_TriggersHitlCard()
    {
        var f = new HubFixture();
        var server = await StartFakeHttpAguiServerAsync(app =>
        {
            app.MapPost("/", async (HttpContext ctx) =>
            {
                ctx.Response.ContentType = "text/event-stream";
                // 与真实 AGUI.AspNetCore 服务完全一致的事件序列：
                // RUN_STARTED → TEXT_MESSAGE_START/CONTENT/END → TOOL_CALL_* → RUN_FINISHED(outcome=interrupt)
                await WriteSseAsync(ctx, new { type = "RUN_STARTED", threadId = "t", runId = "run_ext", input = new { }, timestamp = 1L });
                await WriteSseAsync(ctx, new { type = "TEXT_MESSAGE_START", messageId = "msg_asst", role = "assistant", name = "外部专家" });
                await WriteSseAsync(ctx, new { type = "TEXT_MESSAGE_CONTENT", messageId = "msg_asst", delta = "正在准备发送邮件…" });
                await WriteSseAsync(ctx, new { type = "TEXT_MESSAGE_END", messageId = "msg_asst" });
                await WriteSseAsync(ctx, new { type = "TOOL_CALL_START", toolCallId = "call_ext_1", toolCallName = "send_email", parentMessageId = "msg_asst" });
                await WriteSseAsync(ctx, new { type = "TOOL_CALL_ARGS", toolCallId = "call_ext_1", delta = "{\"to\":\"david@lingtong.com\"}" });
                await WriteSseAsync(ctx, new { type = "TOOL_CALL_END", toolCallId = "call_ext_1" });
                await WriteSseAsync(ctx, new
                {
                    type = "RUN_FINISHED", threadId = "t", runId = "run_ext",
                    outcome = new
                    {
                        type = "interrupt",
                        interrupts = new[]
                        {
                            new
                            {
                                id = "ficc_call_ext_1", reason = "tool_call",
                                message = "Approval required for tool call: send_email",
                                toolCallId = "call_ext_1",
                                responseSchema = new { type = "object", properties = new { approved = new { type = "boolean" } }, required = new[] { "approved" } },
                            },
                        },
                    },
                });
                await ctx.Response.Body.FlushAsync();
            });
        });
        await using var _ = server;

        var group = await f.Hub.CreateGroupAsync(new GroupCreateRequest
        {
            GroupName = "g", OwnerId = "user_1", MemberIds = ["agent_bridge"],
            Members = [new MemberSeed { MemberId = "agent_bridge", MemberType = MemberType.Agent, Nickname = "外部助手" }],
        });
        var (conn, inbox) = f.NewConnection("user_1");
        await f.Hub.SubscribeAsync(conn, [group.GroupId]);
        f.Drain(inbox);

        var gateway = CreateBridgeGateway(f, server.HttpBase, mode: "standard");
        var result = await gateway.InvokeAsync(new AgentInvocationContext(
            group.GroupId, "thread_" + group.GroupId, "agent_bridge", "外部助手",
            "msg_trigger", "user_1", "发邮件给david@lingtong.com，主题：hello，内容：hello again.", [], false), CancellationToken.None);

        // 运行被中断：等待交互而非直接回复
        Assert.False(result.Accepted);
        Assert.Equal("AGENT_AWAITING_INTERACTION", result.ErrorCode);

        // 群聊收到审批卡片事件（工具名从 message 提取，触发者=user_1）
        var requestEvent = f.Drain(inbox).Select(HubFixture.Parse)
            .First(e => e.GetProperty("type").GetString() == EventTypes.AgentInteractionRequest);
        Assert.Equal("send_email", requestEvent.GetProperty("toolName").GetString());
        Assert.Equal("call_ext_1", requestEvent.GetProperty("toolCallId").GetString());
        Assert.Equal("user_1", requestEvent.GetProperty("targetMemberId").GetString());
    }

    /// <summary>回归：恢复流再次返回审批中断时，桥接连接必须保留给第二轮决策（不能被 finally 释放），
    /// 第二轮批准后内容才最终回灌——覆盖真实邮件助手「resume 后模型再次触发审批」场景。</summary>
    [Fact]
    public async Task Invoke_BridgeHttp_StandardSse_ReInterrupt_KeepsClientForSecondDecision()
    {
        var f = new HubFixture();
        var callCount = 0;
        var server = await StartFakeHttpAguiServerAsync(app =>
        {
            app.MapPost("/", async (HttpContext ctx) =>
            {
                ctx.Response.ContentType = "text/event-stream";
                var call = Interlocked.Increment(ref callCount);
                if (call == 1)
                {
                    // 首条运行：文本 + 审批中断
                    await WriteSseAsync(ctx, new { type = "RUN_STARTED", threadId = "t", runId = "run_ext", input = new { }, timestamp = 1L });
                    await WriteSseAsync(ctx, new { type = "TEXT_MESSAGE_START", messageId = "m1", role = "assistant" });
                    await WriteSseAsync(ctx, new { type = "TEXT_MESSAGE_CONTENT", messageId = "m1", delta = "准备发送…" });
                    await WriteSseAsync(ctx, new { type = "TEXT_MESSAGE_END", messageId = "m1" });
                    await WriteSseAsync(ctx, new { type = "RUN_FINISHED", threadId = "t", runId = "run_ext", outcome = new { type = "interrupt", interrupts = new[] { new { id = "interrupt_ext_1", reason = "tool_call", message = "Approval required for tool call: send_email", toolCallId = "call_1" } } } });
                }
                else if (call == 2)
                {
                    // 恢复流：再次中断（第二个工具需要审批）
                    await WriteSseAsync(ctx, new { type = "RUN_STARTED", threadId = "t", runId = "run_ext", input = new { }, timestamp = 1L });
                    await WriteSseAsync(ctx, new { type = "RUN_FINISHED", threadId = "t", runId = "run_ext", outcome = new { type = "interrupt", interrupts = new[] { new { id = "interrupt_ext_2", reason = "tool_call", message = "Approval required for tool call: send_second_email", toolCallId = "call_2" } } } });
                }
                else
                {
                    // 第二次恢复流：最终内容 + 成功
                    await WriteSseAsync(ctx, new { type = "RUN_STARTED", threadId = "t", runId = "run_ext", input = new { }, timestamp = 1L });
                    await WriteSseAsync(ctx, new { type = "TEXT_MESSAGE_START", messageId = "m2", role = "assistant" });
                    await WriteSseAsync(ctx, new { type = "TEXT_MESSAGE_CONTENT", messageId = "m2", delta = "邮件已发送" });
                    await WriteSseAsync(ctx, new { type = "TEXT_MESSAGE_END", messageId = "m2" });
                    await WriteSseAsync(ctx, new { type = "RUN_FINISHED", threadId = "t", runId = "run_ext", outcome = new { type = "success" } });
                }
                await ctx.Response.Body.FlushAsync();
            });
        });
        await using var _ = server;

        var group = await f.Hub.CreateGroupAsync(new GroupCreateRequest
        {
            GroupName = "g", OwnerId = "user_1", MemberIds = ["agent_bridge"],
            Members = [new MemberSeed { MemberId = "agent_bridge", MemberType = MemberType.Agent, Nickname = "外部助手" }],
        });
        var (conn, inbox) = f.NewConnection("user_1");
        await f.Hub.SubscribeAsync(conn, [group.GroupId]);
        f.Drain(inbox);

        var gateway = CreateBridgeGateway(f, server.HttpBase, mode: "standard");
        var result = await gateway.InvokeAsync(new AgentInvocationContext(
            group.GroupId, "thread_" + group.GroupId, "agent_bridge", "外部助手",
            "msg_trigger", "user_1", "发邮件", [], false), CancellationToken.None);

        Assert.False(result.Accepted);
        Assert.Equal("AGENT_AWAITING_INTERACTION", result.ErrorCode);

        // 第一张审批卡片
        var firstCard = WaitForEventType(f, inbox, EventTypes.AgentInteractionRequest, "send_email");
        var interruptId1 = firstCard.GetProperty("interruptId").GetString()!;

        // 批准第一张 → 恢复流返回第二个中断 → 第二张卡片（桥接连接未被释放）
        Assert.True(await gateway.ResolveInteractionAsync(interruptId1, "user_1", true, null, null, CancellationToken.None));
        var secondCard = WaitForEventType(f, inbox, EventTypes.AgentInteractionRequest, "send_second_email");
        var interruptId2 = secondCard.GetProperty("interruptId").GetString()!;
        Assert.Equal("call_2", secondCard.GetProperty("toolCallId").GetString());

        // 批准第二张 → 最终内容回灌群聊
        Assert.True(await gateway.ResolveInteractionAsync(interruptId2, "user_1", true, null, null, CancellationToken.None));
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline
               && !(f.Store.AllMessages(group.GroupId).LastOrDefault(m => m.SenderType == MemberType.Agent)?.Content.Contains("邮件已发送") == true))
            await Task.Delay(100);
        var reply = f.Store.AllMessages(group.GroupId).Last(m => m.SenderType == MemberType.Agent);
        Assert.Contains("邮件已发送", reply.Content);
        Assert.Equal(3, callCount); // 首条 + 两次恢复
    }

    /// <summary>轮询连接收件箱直到出现指定类型且工具名匹配的事件（决策为异步广播）。</summary>
    private static JsonElement WaitForEventType(HubFixture f, Channel<string> inbox, string type, string toolName)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var found = f.Drain(inbox).Select(HubFixture.Parse)
                .FirstOrDefault(e => e.GetProperty("type").GetString() == type
                    && e.GetProperty("toolName").GetString() == toolName);
            if (found.ValueKind != JsonValueKind.Undefined) return found;
            Thread.Sleep(50);
        }
        throw new TimeoutException($"等待事件超时：{type}/{toolName}");
    }

    [Fact]
    public async Task Invoke_BridgeHttp_HubMode_CascadesViaSse()
    {
        var f = new HubFixture();
        // 模拟真实 Hub 的 SSE 行为：连接后保持打开，等群消息 POST 后再下发事件
        var sentGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = await StartFakeHttpAguiServerAsync(app =>
        {
            app.MapPost("/ag-ui/group/message/send", () =>
            {
                sentGate.TrySetResult();
                return Results.Ok(new { messageId = "msg_sent" });
            });
            app.MapGet("/sse", async (HttpContext ctx) =>
            {
                ctx.Response.ContentType = "text/event-stream";
                await ctx.Response.Body.FlushAsync(); // 先送响应头，让客户端继续 POST
                await sentGate.Task.WaitAsync(ctx.RequestAborted);
                // 自身消息回显（应被过滤）
                await WriteSseAsync(ctx, new { type = "TEXT_MESSAGE_START", messageId = "msg_echo", role = "user", threadId = "t", groupId = "g", senderId = "agent_bridge", senderNickname = "外部助手", timestamp = 1L });
                await WriteSseAsync(ctx, new { type = "TEXT_MESSAGE_CONTENT", messageId = "msg_echo", delta = "自己的回显" });
                await WriteSseAsync(ctx, new { type = "TEXT_MESSAGE_END", messageId = "msg_echo", groupId = "g", timestamp = 1L });
                // 外部 agent 回复（replyToMessageId 指向回显消息 → 被接受回灌）
                await WriteSseAsync(ctx, new { type = "TEXT_MESSAGE_START", messageId = "msg_ext", role = "assistant", threadId = "t", groupId = "g", senderId = "agent_remote", senderNickname = "远程助手", replyToMessageId = "msg_echo", timestamp = 1L });
                await WriteSseAsync(ctx, new { type = "TEXT_MESSAGE_CONTENT", messageId = "msg_ext", delta = "HTTP 级联回复：收到" });
                await WriteSseAsync(ctx, new { type = "TEXT_MESSAGE_END", messageId = "msg_ext", groupId = "g", timestamp = 1L });
                await ctx.Response.Body.FlushAsync();
            });
        });
        await using var _ = server;

        var group = await f.Hub.CreateGroupAsync(new GroupCreateRequest
        {
            GroupName = "g", OwnerId = "user_1", MemberIds = ["agent_bridge"],
            Members = [new MemberSeed { MemberId = "agent_bridge", MemberType = MemberType.Agent, Nickname = "外部助手" }],
        });
        var gateway = CreateBridgeGateway(f, server.HttpBase, mode: "hub");
        var result = await gateway.InvokeAsync(new AgentInvocationContext(
            group.GroupId, "thread_" + group.GroupId, "agent_bridge", "外部助手",
            "msg_trigger", "user_1", "帮我看看", [], false), CancellationToken.None);

        Assert.True(result.Accepted, "桥接失败: " + result.ErrorCode);
        var reply = f.Store.AllMessages(group.GroupId).Last(m => m.SenderType == MemberType.Agent);
        Assert.Contains("HTTP 级联回复：收到", reply.Content);
        Assert.DoesNotContain("自己的回显", reply.Content);
    }

    private static async Task WriteSseAsync(HttpContext ctx, object evt)
    {
        await ctx.Response.WriteAsync("data: " + JsonSerializer.Serialize(evt) + "\n\n");
    }

    private static async Task<FakeHttpAguiServer> StartFakeHttpAguiServerAsync(Action<WebApplication> map)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        map(app);
        await app.StartAsync();
        return new FakeHttpAguiServer(app, app.Urls.First().TrimEnd('/'));
    }

    private sealed class FakeHttpAguiServer : IAsyncDisposable
    {
        private readonly WebApplication _app;
        public string HttpBase { get; }
        public FakeHttpAguiServer(WebApplication app, string httpBase) { _app = app; HttpBase = httpBase; }
        public ValueTask DisposeAsync() => _app.DisposeAsync();
    }

    [Fact]
    public async Task Invoke_BridgeConnectionFailure_ReportsBridgeError()
    {
        var f = new HubFixture();
        var group = await HubFixture.CreateGroupAsync(f.Hub, "g", "user_1", "agent_bridge");
        // 指向未监听的端口 → 连接失败 → RUN_ERROR 事件回灌，Invoke 返回错误码
        var gateway = CreateBridgeGateway(f, "ws://127.0.0.1:1/ws", mode: "standard");
        var result = await gateway.InvokeAsync(new AgentInvocationContext(
            group.GroupId, "thread_" + group.GroupId, "agent_bridge", "外部助手",
            "msg_trigger", "user_1", "hi", [], false), CancellationToken.None);

        Assert.False(result.Accepted);
        Assert.Equal("AGENT_BRIDGE_ERROR", result.ErrorCode);
    }

    // ---- AG-UI 桥接人机交互（HITL，协议 4.5） ----------------

    /// <summary>standard 方言 WS 桥接：外部服务返回 RUN_FINISHED+interrupt → 运行中断广播审批卡片（仅触发者可决策）
    /// → 批准后向外部发送 RunAgentInput+resume → 外部继续回复回灌群聊。</summary>
    [Fact]
    public async Task Invoke_BridgeStandardMode_Hitl_Interrupts_And_Resumes()
    {
        var resumeReceived = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var f = new HubFixture();
        using var server = await StartFakeAguiServerAsync(async (ws, ct) =>
        {
            // 1. 首条上行 RunAgentInput（用户消息）
            var first = await ReadOneJsonAsync(ws, ct);
            Assert.True(first.RootElement.TryGetProperty("messages", out var msgs) && msgs.GetArrayLength() == 1);

            // 2. 回 RUN_FINISHED + outcome.interrupts（AG-UI 协议审批中断）
            await SendJsonAsync(ws, new { type = "RUN_STARTED", threadId = "t", runId = "run_ext", context = new { } }, ct);
            await SendJsonAsync(ws, new
            {
                type = "RUN_FINISHED",
                threadId = "t", runId = "run_ext", context = new { },
                outcome = new
                {
                    type = "interrupt",
                    interrupts = new[]
                    {
                        new
                        {
                            id = "approval_ext_1",
                            reason = "tool_call",
                            message = "Approve tool call publish_announcement?",
                            toolCallId = "call_ext_1",
                            metadata = new
                            {
                                function_call = new { name = "publish_announcement", arguments = new { announcement = "放假通知" } },
                            },
                        },
                    },
                },
            }, ct);

            // 3. 决策恢复：收到第二条上行 RunAgentInput（含 resume 数组，interruptId 为外部 id）
            var resume = await ReadOneJsonAsync(ws, ct);
            var resumeArr = resume.RootElement.GetProperty("resume");
            var entry = resumeArr[0];
            // AGUIToolApprovalResumePayload：字段是 approved（非 accepted），并回传被批准工具的 toolCall
            var payload = entry.GetProperty("payload");
            var toolCall = payload.GetProperty("toolCall");
            resumeReceived.SetResult(
                entry.GetProperty("interruptId").GetString() + ":" +
                payload.GetProperty("approved").GetBoolean() + ":" +
                toolCall.GetProperty("name").GetString() + ":" +
                toolCall.GetProperty("arguments").GetProperty("announcement").GetString());

            // 4. 回恢复后的回复
            await SendJsonAsync(ws, new
            {
                type = "ASSISTANT_MESSAGE",
                messageId = "msg_ext2", threadId = "t", requestId = "r2",
                context = new { time = "" },
                payload = new { content = new[] { new { type = "text", text = "已批准并执行公告发布" } } },
            }, ct);
            await SendJsonAsync(ws, new { type = "RUN_COMPLETED", runId = "run_ext", threadId = "t", requestId = "r2", context = new { time = "" }, payload = new { } }, ct);
        });

        var group = await f.Hub.CreateGroupAsync(new GroupCreateRequest
        {
            GroupName = "g",
            OwnerId = "user_1",
            MemberIds = ["agent_bridge"],
            Members = [new MemberSeed { MemberId = "agent_bridge", MemberType = MemberType.Agent, Nickname = "外部助手" }],
        });
        var (conn, inbox) = f.NewConnection("user_1");
        await f.Hub.SubscribeAsync(conn, [group.GroupId]);
        f.Drain(inbox);

        var gateway = CreateBridgeGateway(f, server.Url, mode: "standard");
        var result = await gateway.InvokeAsync(new AgentInvocationContext(
            group.GroupId, "thread_" + group.GroupId, "agent_bridge", "外部助手",
            "msg_trigger", "user_1", "发布公告：放假", [], false), CancellationToken.None);

        Assert.False(result.Accepted);
        Assert.Equal("AGENT_AWAITING_INTERACTION", result.ErrorCode);

        // 群聊收到审批卡片事件（含外部工具名与触发者）
        var requestEvent = f.Drain(inbox).Select(HubFixture.Parse)
            .First(e => e.GetProperty("type").GetString() == EventTypes.AgentInteractionRequest);
        Assert.Equal("publish_announcement", requestEvent.GetProperty("toolName").GetString());
        Assert.Equal("user_1", requestEvent.GetProperty("targetMemberId").GetString());
        var interruptId = requestEvent.GetProperty("interruptId").GetString()!;

        // 非触发者（user_2）无权决策；触发者（user_1）批准 → 恢复
        Assert.False(await gateway.ResolveInteractionAsync(interruptId, "user_2", true, null, null, CancellationToken.None));
        Assert.True(await gateway.ResolveInteractionAsync(interruptId, "user_1", true, null, null, CancellationToken.None));

        // 外部服务收到恢复指令：外部 interruptId + approved=true + toolCall（name + arguments）
        var resumeInfo = await resumeReceived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("approval_ext_1:True:publish_announcement:放假通知", resumeInfo);

        // 恢复后的回复回灌群聊（等待 CONTENT 实际落地，恢复为异步）
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline
               && !(f.Store.AllMessages(group.GroupId).LastOrDefault(m => m.SenderType == MemberType.Agent)?.Content.Contains("已批准并执行公告发布") == true))
            await Task.Delay(100);
        var reply = f.Store.AllMessages(group.GroupId).Last(m => m.SenderType == MemberType.Agent);
        Assert.Contains("已批准并执行公告发布", reply.Content);
    }

    /// <summary>hub 方言 WS 桥接：外部 Hub 智能体广播 AGENT_INTERACTION_REQUEST → 本网关中断广播审批卡片
    /// → 触发者决策 → 向外部 Hub 发送 AGENT_INTERACTION_RESOLVE → 外部继续回复回灌。</summary>
    [Fact]
    public async Task Invoke_BridgeHubMode_Hitl_Interrupts_And_Resolves()
    {
        var resolveReceived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var replySent = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var f = new HubFixture();
        using var server = await StartFakeAguiServerAsync(async (ws, ct) =>
        {
            // 订阅 + 发送
            var sub = await ReadOneJsonAsync(ws, ct);
            Assert.Equal("GROUP_SUBSCRIBE", sub.RootElement.GetProperty("type").GetString());
            var send = await ReadOneJsonAsync(ws, ct);
            Assert.Equal("GROUP_MESSAGE_SEND", send.RootElement.GetProperty("type").GetString());

            // 自身消息回显（应被过滤，同时捕获 selfMessageId 供后续回复匹配）
            await SendJsonAsync(ws, new { type = "TEXT_MESSAGE_START", messageId = "msg_echo", role = "user", threadId = "t", groupId = "g", senderId = "agent_bridge", senderNickname = "外部助手", timestamp = 1L }, ct);
            await SendJsonAsync(ws, new { type = "TEXT_MESSAGE_CONTENT", messageId = "msg_echo", delta = "自己的回显" }, ct);
            await SendJsonAsync(ws, new { type = "TEXT_MESSAGE_END", messageId = "msg_echo", groupId = "g", timestamp = 1L }, ct);

            // 回 AGENT_INTERACTION_REQUEST（hub 方言人机交互）
            await SendJsonAsync(ws, new
            {
                type = "AGENT_INTERACTION_REQUEST",
                groupId = "g", messageId = "msg_ext", threadId = "t", runId = "run_ext",
                interruptId = "interrupt_hub_1", toolCallId = "call_hub_1",
                toolName = "publish_announcement",
                toolArguments = new { announcement = "放假通知" },
                message = "Approve publish?",
                targetMemberId = "user_1",
                timestamp = 1L,
            }, ct);

            // 等触发者决策：收到 AGENT_INTERACTION_RESOLVE
            var resolve = await ReadOneJsonAsync(ws, ct);
            Assert.Equal("AGENT_INTERACTION_RESOLVE", resolve.RootElement.GetProperty("type").GetString());
            Assert.Equal("interrupt_hub_1", resolve.RootElement.GetProperty("interruptId").GetString());
            resolveReceived.SetResult(resolve.RootElement.GetProperty("approved").GetBoolean());

            // 回恢复后的回复（replyToMessageId 指向回显消息 → 被接受回灌）
            await SendJsonAsync(ws, new { type = "TEXT_MESSAGE_START", messageId = "msg_ext2", role = "assistant", threadId = "t", groupId = "g", senderId = "agent_ext", senderNickname = "外部智能体", replyToMessageId = "msg_echo", timestamp = 1L }, ct);
            await SendJsonAsync(ws, new { type = "TEXT_MESSAGE_CONTENT", messageId = "msg_ext2", delta = "已批准，公告已发布" }, ct);
            await SendJsonAsync(ws, new { type = "TEXT_MESSAGE_END", messageId = "msg_ext2", groupId = "g", timestamp = 1L }, ct);
            replySent.SetResult(true);
        });

        var group = await f.Hub.CreateGroupAsync(new GroupCreateRequest
        {
            GroupName = "g",
            OwnerId = "user_1",
            MemberIds = ["agent_bridge"],
            Members = [new MemberSeed { MemberId = "agent_bridge", MemberType = MemberType.Agent, Nickname = "外部助手" }],
        });
        var (conn, inbox) = f.NewConnection("user_1");
        await f.Hub.SubscribeAsync(conn, [group.GroupId]);
        f.Drain(inbox);

        var gateway = CreateBridgeGateway(f, server.Url, mode: "hub");
        var result = await gateway.InvokeAsync(new AgentInvocationContext(
            group.GroupId, "thread_" + group.GroupId, "agent_bridge", "外部助手",
            "msg_trigger", "user_1", "发布公告：放假", [], false), CancellationToken.None);

        Assert.False(result.Accepted);
        Assert.Equal("AGENT_AWAITING_INTERACTION", result.ErrorCode);

        // 群聊收到审批卡片事件
        var requestEvent = f.Drain(inbox).Select(HubFixture.Parse)
            .First(e => e.GetProperty("type").GetString() == EventTypes.AgentInteractionRequest);
        Assert.Equal("publish_announcement", requestEvent.GetProperty("toolName").GetString());
        Assert.Equal("user_1", requestEvent.GetProperty("targetMemberId").GetString());
        var interruptId = requestEvent.GetProperty("interruptId").GetString()!;

        // 非触发者无权；触发者批准 → 外部 Hub 收到 AGENT_INTERACTION_RESOLVE（approved=true）
        Assert.False(await gateway.ResolveInteractionAsync(interruptId, "user_2", false, null, null, CancellationToken.None));
        Assert.True(await gateway.ResolveInteractionAsync(interruptId, "user_1", true, null, null, CancellationToken.None));
        Assert.True(await resolveReceived.Task.WaitAsync(TimeSpan.FromSeconds(10)), "外部服务未收到 AGENT_INTERACTION_RESOLVE");

        // 恢复后的回复回灌群聊（等待 CONTENT 实际落地，恢复为异步）
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline
               && !(f.Store.AllMessages(group.GroupId).LastOrDefault(m => m.SenderType == MemberType.Agent)?.Content.Contains("已批准，公告已发布") == true))
            await Task.Delay(100);
        var reply = f.Store.AllMessages(group.GroupId).Last(m => m.SenderType == MemberType.Agent);
        Assert.Contains("已批准，公告已发布", reply.Content);
    }

    // ---- 假外部 AG-UI 服务（HttpListener WebSocket） ----

    private static async Task<FakeAguiServer> StartFakeAguiServerAsync(Func<WebSocket, CancellationToken, Task> handler)
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();
        var server = new FakeAguiServer(listener, $"ws://localhost:{port}/ws");
        _ = Task.Run(async () =>
        {
            try
            {
                while (listener.IsListening)
                {
                    var ctx = await listener.GetContextAsync();
                    if (ctx.Request.IsWebSocketRequest)
                    {
                        var wsCtx = await ctx.AcceptWebSocketAsync(null);
                        _ = Task.Run(async () =>
                        {
                            try { await handler(wsCtx.WebSocket, CancellationToken.None); }
                            catch (Exception ex) { server.LastError = ex.ToString(); }
                            finally
                            {
                                try { await wsCtx.WebSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None); }
                                catch { }
                                wsCtx.WebSocket.Dispose();
                            }
                        });
                    }
                    else
                    {
                        ctx.Response.StatusCode = 404;
                        ctx.Response.Close();
                    }
                }
            }
            catch { /* 测试结束关闭 */ }
        });
        return server;
    }

    private sealed class FakeAguiServer : IDisposable
    {
        private readonly HttpListener _listener;
        public string Url { get; }
        public string? LastError { get; set; }
        public FakeAguiServer(HttpListener listener, string url) { _listener = listener; Url = url; }
        public void Dispose() => _listener.Close();
    }

    private static async Task<JsonDocument> ReadOneJsonAsync(WebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[32 * 1024];
        var sb = new StringBuilder();
        WebSocketReceiveResult r;
        do
        {
            r = await ws.ReceiveAsync(buffer, ct);
            sb.Append(Encoding.UTF8.GetString(buffer, 0, r.Count));
        }
        while (!r.EndOfMessage);
        return JsonDocument.Parse(sb.ToString());
    }

    private static Task SendJsonAsync(WebSocket ws, object evt, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(evt, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, ct);
    }

    [Fact]
    public async Task MockChatClient_StreamsIncrementalChunks()
    {
        var client = new MockChatClient(new AgentDefinition
        {
            AgentId = "agent_a",
            Nickname = "测试助手",
            Instructions = "x",
        });

        var updates = new List<Microsoft.Extensions.AI.ChatResponseUpdate>();
        await foreach (var u in client.GetStreamingResponseAsync(
            [new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "你好")]))
        {
            updates.Add(u);
        }

        // 与真实 OpenAI 兼容客户端一致：每帧为增量片段，拼接后等于完整回复
        Assert.True(updates.Count >= 2);
        Assert.True(updates.All(u => u.Text!.Length > 0));
        var full = string.Concat(updates.Select(u => u.Text));
        Assert.Contains("你好", full);
        Assert.Contains("测试助手", full);
    }

    [Theory]
    [InlineData("", "第一段", "第一段")]                 // 首帧
    [InlineData("第一段", "第一段第二段", "第二段")]      // 累计文本 → 取新增部分
    [InlineData("第一段", "第二段", "第二段")]           // 增量片段 → 整体作为 delta
    [InlineData("abc", "abc", "")]                    // 无新增内容
    public void ComputeTextDelta_HandlesCumulativeAndIncremental(string accumulated, string text, string expected)
    {
        Assert.Equal(expected, AgentGateway.ComputeTextDelta(accumulated, text));
    }

    [Fact]
    public async Task Hub_PublishAgentMessage_StartAppendEnd_RoundTrip()
    {
        var f = new HubFixture();
        var group = await HubFixture.CreateGroupAsync(f.Hub, "g", "user_1", "user_2", "agent_a");
        var (c1, inbox1) = f.NewConnection("user_1");
        var (c2, inbox2) = f.NewConnection("user_2");
        await f.Hub.SubscribeAsync(c1, [group.GroupId]);
        await f.Hub.SubscribeAsync(c2, [group.GroupId]);
        f.Drain(inbox1); f.Drain(inbox2);

        // private 可见性：仅 user_2 + 发送者（agent_a）
        var started = await f.Hub.PublishAgentMessageStartAsync(new AgentMessageStartInput
        {
            GroupId = group.GroupId,
            AgentId = "agent_a",
            RunId = "run_1",
            ReplyToMessageId = "msg_x",
            Visibility = MessageVisibility.Private,
            VisibleMemberIds = ["user_2"],
        });
        await f.Hub.AppendAgentContentAsync(group.GroupId, started.MessageId, "第一段");
        await f.Hub.AppendAgentContentAsync(group.GroupId, started.MessageId, "第二段");
        await f.Hub.EndAgentMessageAsync(group.GroupId, started.MessageId);

        // 存储内容为增量拼接
        Assert.Equal("第一段第二段", f.Store.GetMessage(group.GroupId, started.MessageId)!.Content);

        // user_2 收到 START + 2×CONTENT + END；user_1（未被定向）不收到
        var inbox2Events = f.Drain(inbox2);
        Assert.Equal(4, inbox2Events.Count);
        Assert.Empty(f.Drain(inbox1));

        var types = HubFixture.TypesOf(inbox2Events);
        Assert.Equal(
            ["TEXT_MESSAGE_START", "TEXT_MESSAGE_CONTENT", "TEXT_MESSAGE_CONTENT", "TEXT_MESSAGE_END"],
            types);
    }
}
