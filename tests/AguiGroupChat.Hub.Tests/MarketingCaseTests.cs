using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AguiGroupChat.Agents;
using AguiGroupChat.Hub;
using AguiGroupChat.Hub.Agents;
using AguiGroupChat.Hub.Messaging;
using AguiGroupChat.Hub.Models;
using AguiGroupChat.Hub.Options;
using AguiGroupChat.Hub.Persistence;
using AguiGroupChat.Hub.Storage;
using AguiGroupChat.Hub.Users;
using AguiGroupChat.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>
/// 市场推广案例（知聚 8.4 的切入故事）自动化验证：
///   第 2 步 —— 「✨ 生成角色设定」+ 创建多个数字员工；「🗣 多位数字员工讨论」按序接力发言；
///   第 5 步 —— 「发布公告」命中人机交互审批：运行中断 → 仅触发者可批准 → 批准后恢复回灌；
///   第 4 步 —— 记忆治理：列入条目 → 标记重要/关键 → 手动遗忘 / 自动过期截断 → 导出「记忆即数据包」。
/// 使用内存（mock 网关）与 SQLite 记忆存储，无外部依赖、确定性可重复。
/// </summary>
[Trait("Category", "Marketing")]
public sealed class MarketingCaseTests
{
    // =========================================================================
    // Part A · 数字员工协作中枢（HTTP + mock 网关）：建角色 → 建知聚 → 多数字员工讨论
    // =========================================================================

    [Fact]
    public async Task CollaborateHub_CreateRole_GenerateInstructions_LaunchDiscussion()
    {
        await using var fixture = new MarketingMockServer();
        using var client = new HttpClient { BaseAddress = new Uri(fixture.HttpBase) };
        var user = await RegisterAsync(client, "case_owner");
        var token = user.GetProperty("token").GetString()!;
        var userId = user.GetProperty("userId").GetString()!;

        // ① 「✨ 生成角色设定」：一句简介 → 结构化系统提示词（身份定位 / 职责范围 / 回复风格）
        using (var gen = Authed(HttpMethod.Post, "/ag-ui/agents/generate-instructions", token))
        {
            gen.Content = JsonContent.Create(new { description = "产品需求分析助手" });
            var res = await client.SendAsync(gen);
            res.EnsureSuccessStatusCode();
            var instructions = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("instructions").GetString()!;
            Assert.Contains("身份", instructions);
            Assert.Contains("职责", instructions);
            Assert.Contains("回复风格", instructions);
        }

        // ② 创建两个数字员工并加入知聚
        foreach (var (id, nick, mode) in new[]
        {
            ("agent_case_prd", "产品助理", "mentioned"),
            ("agent_case_code", "代码帮", "mentioned"),
        })
        {
            using var createAgent = Authed(HttpMethod.Post, "/ag-ui/agents", token);
            createAgent.Content = JsonContent.Create(new
            {
                agentId = id, nickname = nick, description = "案例角色",
                instructions = $"你是{nick}，请针对问题给一段简短答复。", triggerMode = mode, keywords = new string[0],
            });
            var created = await client.SendAsync(createAgent);
            Assert.True(created.IsSuccessStatusCode, $"创建数字员工失败: {await created.Content.ReadAsStringAsync()}");
        }

        var create = await client.PostAsJsonAsync("/ag-ui/group/create", new
        {
            groupName = "技术讨论",
            ownerId = userId,
            memberIds = new[] { userId, "agent_case_prd", "agent_case_code" },
            members = new[]
            {
                new { memberId = "agent_case_prd", memberType = "agent", nickname = "产品助理" },
                new { memberId = "agent_case_code", memberType = "agent", nickname = "代码帮" },
            },
        });
        create.EnsureSuccessStatusCode();
        var groupId = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("groupId").GetString()!;

        // ③ 「🗣 多位数字员工讨论」：两个数字员工按序接力发言
        using var discuss = Authed(HttpMethod.Post, $"/ag-ui/group/{groupId}/discussion", token);
        discuss.Content = JsonContent.Create(new { content = "我们当前 API 权限模型怎么设计？", agentIds = new[] { "agent_case_prd", "agent_case_code" } });
        var discussRes = await client.SendAsync(discuss);
        discussRes.EnsureSuccessStatusCode();
        Assert.Equal(2, (await discussRes.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("agents").GetArrayLength());

        // 轮询快照：两个数字员工都应已发言
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (DateTimeOffset.UtcNow < deadline)
        {
            using var snap = Authed(HttpMethod.Get, $"/ag-ui/group/{groupId}", token);
            var snapRes = await client.SendAsync(snap);
            snapRes.EnsureSuccessStatusCode();
            var arr = (await snapRes.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("latestMessages").EnumerateArray().ToArray();
            var agents = arr
                .Where(m => (m.GetProperty("senderId").GetString() ?? "").StartsWith("agent_"))
                .Select(m => m.GetProperty("senderId").GetString()).ToList();
            if (agents.Contains("agent_case_prd") && agents.Contains("agent_case_code")) break;
            await Task.Delay(100);
        }
        var agm = await WaitAgentReplyAsync(client, token, groupId, "agent_case_code"); // 也确认代码帮发言
        Assert.NotNull(agm);
    }

    /// <summary>@ 触发（提及即回）：被 @ 的数字员工必定触发并以其 Mentioned 语义调用——对应知聚案例「@代码帮 帮我 review」。</summary>
    [Fact]
    public async Task Mention_TriggersTheMentionedDigitalWorker()
    {
        var (hub, gateway) = CreateRecordedSut();
        var group = await HubFixture.CreateGroupAsync(hub, "技术讨论", "user_1", "agent_case_code");
        hub.RegisterAgent(new AgentRegisterRequest
        {
            AgentId = "agent_case_code", Nickname = "代码帮", GroupIds = [group.GroupId],
            TriggerMode = AgentTriggerMode.Mentioned,
        });

        // 成员 @代码帮（提及）→ 触发
        await hub.SendMessageAsync(new GroupMessageSendRequest
        {
            GroupId = group.GroupId, UserId = "user_1",
            Content = "@代码帮 帮我 review 这段代码", Mentions = ["agent_case_code"],
        });

        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (gateway.Calls.Count == 0 && DateTime.UtcNow < deadline) await Task.Delay(20);
        var call = Assert.Single(gateway.Calls);
        Assert.Equal("agent_case_code", call.AgentId);
        Assert.Contains("帮我 review 这段代码", call.Content, StringComparison.Ordinal);
    }

    // =========================================================================
    // Part B · 人机交互审批（发布公告）：mock 网关 + 真 AgentGateway + HubFixture
    // =========================================================================

    [Fact]
    public async Task Hitl_PublishAnnouncement_Interrupts_OnlyTriggererCanApprove_ThenResumes()
    {
        var f = new HubFixture();
        var group = await f.Hub.CreateGroupAsync(new GroupCreateRequest
        {
            GroupName = "发布知聚", OwnerId = "user_1",
            MemberIds = ["user_2", "agent_case_pub"],
            Members = [new MemberSeed { MemberId = "agent_case_pub", MemberType = MemberType.Agent, Nickname = "发布助手" }],
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
            EnableTools = true, // mock 客户端按关键词模拟调用需审批工具 publish_announcement
            Agents =
            [
                new AgentDefinition { AgentId = "agent_case_pub", Nickname = "发布助手", Description = "测试", Instructions = "你是发布助手",
                    TriggerMode = AgentTriggerMode.Mentioned },
            ],
        };
        var catalog = new AgentCatalog(options, NullLoggerFactory.Instance, new ServiceCollection().BuildServiceProvider());
        var services = new ServiceCollection().AddSingleton(f.Hub).BuildServiceProvider();
        var gateway = new AgentGateway(catalog, services, options, attachmentStore: null, NullLogger<AgentGateway>.Instance);

        // 触发者请求发布公告 → mock 模拟调用 publish_announcement（需审批）→ 运行中断
        var result = await gateway.InvokeAsync(new AgentInvocationContext(
            GroupId: group.GroupId, ThreadId: "thread_" + group.GroupId,
            AgentId: "agent_case_pub", AgentNickname: "发布助手",
            TriggerMessageId: "msg_trigger", TriggerUserId: "user_1",
            Content: "帮我发布公告：放假通知", Mentions: [], MentionAll: false), CancellationToken.None);
        Assert.False(result.Accepted);
        Assert.Equal("AGENT_AWAITING_INTERACTION", result.ErrorCode);

        // 广播 AGENT_INTERACTION_REQUEST：字段齐全，双方可见
        var requestEvent = FindEvent(f.Drain(triggererInbox), EventTypes.AgentInteractionRequest);
        Assert.Equal("publish_announcement", requestEvent.GetProperty("toolName").GetString());
        Assert.Equal("user_1", requestEvent.GetProperty("targetMemberId").GetString());
        var interruptId = requestEvent.GetProperty("interruptId").GetString()!;
        Assert.True(!string.IsNullOrWhiteSpace(interruptId));
        Assert.Contains(f.Drain(otherInbox), e => HubFixture.TypeOf(e) == EventTypes.AgentInteractionRequest);

        // 非触发者（user_2）无法决策；触发者（user_1）批准 → 恢复运行并回灌
        Assert.False(await gateway.ResolveInteractionAsync(interruptId, "user_2", true, null, null, CancellationToken.None));
        Assert.True(await gateway.ResolveInteractionAsync(interruptId, "user_1", true, null, null, CancellationToken.None));

        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        var events = new List<string>();
        while (DateTimeOffset.UtcNow < deadline)
        {
            events.AddRange(f.Drain(triggererInbox));
            if (events.Any(e => HubFixture.TypeOf(e) == EventTypes.TextMessageEnd)) break;
            await Task.Delay(100);
        }
        var texts = events
            .Where(e => HubFixture.TypeOf(e) == EventTypes.TextMessageContent)
            .Select(e => HubFixture.Parse(e).GetProperty("delta").GetString());
        Assert.Contains(texts, t => t is not null && t.Contains("已批准", StringComparison.Ordinal));
    }

    // =========================================================================
    // Part C · 记忆治理（沉淀 → 分级 → 遗忘 → 导出），SQLite 记忆 + 假 embedding
    // =========================================================================

    [Fact]
    public async Task Memory_Crystallize_LevelSet_ForgetAndExport()
    {
        var store = new MemoryTrackingStore();
        using var memory = new AgentMessageMemory(store,
            new AgentOptions { Memory = new MemoryOptions { Enabled = true, EmbeddingDimensions = 8 } },
            NullLogger<AgentMessageMemory>.Instance, new FakeEmbedding());

        // ① 沉淀：讨论形成的关键记忆自动向量化落库
        store.Upsert(new MessageMemoryRecord("m1", "g1", "main", "agent_case_prd", "agent",
            "权限模型采用 RBAC，对外发布必须过审", new float[8], 1000, MemoryImportance.Normal, null));
        store.Upsert(new MessageMemoryRecord("m2", "g1", "main", "agent_case_code", "agent",
            "数据库选型用 Postgres，缓存用 Redis", new float[8], 2000, MemoryImportance.Normal, null));

        // ② 治理：把关键决策标记为「重要 / 关键」
        Assert.True(store.UpdateImportance("m1", (int)MemoryImportance.Critical));
        Assert.Equal(MemoryImportance.Critical, store.GetByMessageId("m1")?.Importance);

        // ③ 可视化：按知聚 / 关键词检索可读条目（store.ListMessages 对应记忆管理界面数据源）
        var listed = store.ListMessages("g1", null, "Postgres", 50, 0);
        Assert.Contains(listed, m => m.MessageId == "m2");

        // ④ 自动遗忘 / 过期截断：过期的记忆不参与检索
        Assert.Equal(0, store.PruneExpired(nowMs: 10_000)); // 无过期 → 不删
        store.SetExpiry(null, expiresAt: 500, nowMs: 10_000); // 给全部设过期
        Assert.Equal(2, store.PruneExpired(nowMs: 10_000));  // 已过期 → 物理清理

        // ⑤ 导出「记忆即数据包」（记忆跨实例同步 / 备份）：按 messageId 去重、向量重算迁移
        // 此时 m1 / m2 已被过期清理，仅剩 m3
        store.Upsert(new MessageMemoryRecord("m3", "g1", "main", "user_1", "user",
            "下周二发布 V2", new float[8], 3000, MemoryImportance.Normal, null));
        var exported = memory.ExportMemories("g1", 0, 5000, 0);
        Assert.Single(exported); // 仅 m3（m1 / m2 已过期清理）
        Assert.Contains(exported, m => m.MessageId == "m3");
        var targetStore = new MemoryTrackingStore();
        using var target = new AgentMessageMemory(targetStore,
            new AgentOptions { Memory = new MemoryOptions { Enabled = true, EmbeddingDimensions = 8 } },
            NullLogger<AgentMessageMemory>.Instance, new FakeEmbedding());
        var imported = await target.ImportMemoriesAsync(exported);
        Assert.Equal(1, imported);
        Assert.Equal(0, await target.ImportMemoriesAsync(exported)); // 幂等去重
    }

    // =========================================================================
    // 基础设施
    // =========================================================================

    private static async Task<JsonElement> RegisterAsync(HttpClient client, string username)
    {
        var res = await client.PostAsJsonAsync("/ag-ui/user/register", new { username, password = "secret1", nickname = username });
        if (res.StatusCode == HttpStatusCode.Conflict)
        {
            var login = await client.PostAsJsonAsync("/ag-ui/user/login", new { username, password = "secret1" });
            login.EnsureSuccessStatusCode();
            return await login.Content.ReadFromJsonAsync<JsonElement>();
        }
        if (!res.IsSuccessStatusCode)
            throw new Xunit.Sdk.XunitException($"register {username} 失败: {(int)res.StatusCode} {await res.Content.ReadAsStringAsync()}");
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static HttpRequestMessage Authed(HttpMethod method, string path, string token)
    {
        var req = new HttpRequestMessage(method, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return req;
    }

    /// <summary>轮询群快照，直到指定数字员工发言，返回其消息（找不到返回 null）。</summary>
    private static async Task<JsonElement?> WaitAgentReplyAsync(HttpClient client, string token, string groupId, string agentId)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            using var req = Authed(HttpMethod.Get, $"/ag-ui/group/{groupId}", token);
            var res = await client.SendAsync(req);
            if (res.IsSuccessStatusCode)
            {
                var snap = await res.Content.ReadFromJsonAsync<JsonElement>();
                var hit = snap.GetProperty("latestMessages").EnumerateArray()
                    .FirstOrDefault(m => (m.GetProperty("senderId").GetString() ?? "") == agentId);
                if (hit.ValueKind != JsonValueKind.Undefined) return hit;
            }
            await Task.Delay(100);
        }
        return null;
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

    /// <summary>录制网关的 GroupHub：用于断言 @ 触发命中（对应知聚案例「@ 触发达」）。</summary>
    private static (GroupHub Hub, RecordingGateway Gateway) CreateRecordedSut()
    {
        var options = new GroupChatOptions { MaxGroupMembers = 50, MessageHistoryLimit = 200, SnapshotMessageCount = 50 };
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

    private sealed class FakeEmbedding : IEmbeddingProvider
    {
        public Task<float[]?> EmbedAsync(string text, CancellationToken ct = default) => Task.FromResult<float[]?>(new float[8]);
        public void Dispose() { }
    }

    /// <summary>内存记忆存储：支持列入列表 / 分级 / 遗忘 / 导出所需的全部操作（案例第 4 步）。</summary>
    private sealed class MemoryTrackingStore : IMessageMemoryStore
    {
        public List<MessageMemoryRecord> Records { get; } = [];
        public void EnsureSchema() { }
        public void Upsert(MessageMemoryRecord record)
        {
            var i = Records.FindIndex(r => r.MessageId == record.MessageId);
            if (i >= 0) Records[i] = record; else Records.Add(record);
        }
        public void Remove(string groupId, string messageId) { }
        public void RemoveGroup(string groupId) => Records.RemoveAll(r => r.GroupId == groupId);
        public void ClearAll() => Records.Clear();

        public IReadOnlyList<MessageMemoryItem> ListMessages(string? groupId, string? senderId, string? keyword, int limit, int offset)
            => Records.Where(m => groupId is null || m.GroupId == groupId)
                .Where(m => senderId is null || m.SenderId == senderId)
                .Where(m => keyword is null || m.Content.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(m => m.Timestamp).Skip(offset).Take(Math.Min(limit, 5000))
                .Select(ToItem).ToList();

        public long CountMessages(string? groupId, string? senderId, string? keyword) => 0;
        public IReadOnlyList<MessageMemoryGroupStat> GroupStats(long nowMs) => [];
        public MessageMemoryItem? GetByMessageId(string messageId)
        {
            var r = Records.FirstOrDefault(x => x.MessageId == messageId);
            return r is null ? null : ToItem(r);
        }
        public bool DeleteByMessageId(string messageId) => Records.RemoveAll(r => r.MessageId == messageId) > 0;
        public bool UpdateImportance(string messageId, int importance)
        {
            var i = Records.FindIndex(r => r.MessageId == messageId);
            if (i < 0) return false;
            var r = Records[i];
            Records[i] = new MessageMemoryRecord(r.MessageId, r.GroupId, r.TopicId, r.SenderId, r.SenderType, r.Content, r.Embedding, r.Timestamp, importance, r.ExpiresAt);
            return true;
        }
        public int SetExpiry(string? groupId, long? expiresAt, long nowMs)
        {
            var count = 0;
            for (var i = 0; i < Records.Count; i++)
            {
                var r = Records[i];
                if (groupId is not null && r.GroupId != groupId) continue;
                if (expiresAt.HasValue) { Records[i] = new MessageMemoryRecord(r.MessageId, r.GroupId, r.TopicId, r.SenderId, r.SenderType, r.Content, r.Embedding, r.Timestamp, r.Importance, expiresAt); count++; }
            }
            return count;
        }
        public int PruneExpired(long nowMs) => Records.RemoveAll(r => r.ExpiresAt is { } e && e < nowMs);
        public IReadOnlyList<MessageMemoryHit> Search(string groupId, string? agentId, float[] embedding, int topK, double minScore, string scope) => [];
        public IReadOnlyList<MessageMemoryHit> SearchPerson(string personId, string currentGroupId, float[] embedding, int topK, double minScore) => [];

        private static MessageMemoryItem ToItem(MessageMemoryRecord r)
            => new(r.MessageId, r.GroupId, r.TopicId, r.SenderId, r.SenderType, r.Content, r.Timestamp, r.Importance, r.ExpiresAt);
    }

    /// <summary>mock 提供商的数字员工协作中枢服务器（对应知聚案例 Part A）。</summary>
    private sealed class MarketingMockServer : IAsyncDisposable
    {
        public WebApplication App { get; }
        public string HttpBase { get; }

        public MarketingMockServer()
        {
            var builder = HubApp.CreateBuilder([]);
            builder.Environment.EnvironmentName = "Testing";
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["GroupChat:SeedSampleData"] = "false",
                ["Agents:Provider"] = "mock",
                ["Persistence:Enabled"] = "false",
                ["Auth:RequireTokenOnRealTime"] = "false",
                ["Auth:AdminUserIds"] = "case_owner",
            });
            HubApp.ConfigureServices(builder);
            builder.Services.AddAgentFramework(builder.Configuration);
            builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));

            App = builder.Build();
            HubApp.MapEndpoints(App);
            App.MapAgentApi();
            App.StartAsync().GetAwaiter().GetResult();
            HttpBase = App.Urls.First();
        }

        public ValueTask DisposeAsync() => App.DisposeAsync();
    }
}
