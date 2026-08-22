using System.Net;
using AguiGroupChat.Agents;
using AguiGroupChat.Hub.Agents;
using AguiGroupChat.Hub.Messaging;
using AguiGroupChat.Hub.Models;
using AguiGroupChat.Hub.Options;
using AguiGroupChat.Hub.Persistence;
using AguiGroupChat.Hub.Persistence.Postgres;
using AguiGroupChat.Hub.Storage;
using AguiGroupChat.Hub.Users;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>mock OpenAI 兼容 /v1/embeddings 的 HttpMessageHandler（MessageMemoryTests 与 pgvector 全链路测试共用）。</summary>
internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    public string? LastRequestBody;
    public string ResponseJson = """{"data":[{"embedding":[1.0,0.0,0.0]}]}""";
    public bool Fail;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequestBody = request.Content?.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
        if (Fail) throw new HttpRequestException("connect failed");
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(ResponseJson),
        });
    }
}

/// <summary>语义记忆（RAG）单元测试：GroupHub 写入钩子 / 撤回清理、embedding 服务、prompt 段落排版。</summary>
public sealed class MessageMemoryTests
{
    // ================= 测试替身 =================

    private sealed class FakeMemory : IMessageMemory
    {
        public List<MessageMemoryEntry> Remembered { get; } = [];
        public List<(string GroupId, string MessageId)> Forgotten { get; } = [];
        public List<string> RemovedGroups { get; } = [];
        public IReadOnlyList<MessageMemoryHit> SearchResult { get; set; } = [];
        public void Remember(MessageMemoryEntry entry) => Remembered.Add(entry);
        public void Forget(string groupId, string messageId) => Forgotten.Add((groupId, messageId));
        public void RemoveGroup(string groupId) => RemovedGroups.Add(groupId);
        public Task<IReadOnlyList<MessageMemoryHit>> SearchAsync(string groupId, string agentId, string query, CancellationToken ct = default)
            => Task.FromResult(SearchResult);
        public Task<IReadOnlyList<MessageMemoryHit>> SearchPersonAsync(string personId, string currentGroupId, string query, CancellationToken ct = default)
            => Task.FromResult(SearchResult);
        public IReadOnlyList<MessageMemoryItem> ListMessages(string? groupId, string? senderId, string? keyword, int limit, int offset) => [];
        public long CountMessages(string? groupId, string? senderId, string? keyword) => 0;
        public IReadOnlyList<MessageMemoryGroupStat> GroupStats() => [];
        public bool DeleteByMessageId(string messageId) => false;
        public bool UpdateImportance(string messageId, int importance) => false;
        public int ForgetGroup(string? groupId, double? retentionHours) => 0;
        public int PruneExpired() => 0;
    }

    private sealed class FakeMemoryStore : IMessageMemoryStore
    {
        public List<MessageMemoryRecord> Records { get; } = [];
        public IReadOnlyList<MessageMemoryHit> SearchResult { get; set; } = [];
        public List<string> RemovedGroups { get; } = [];
        public void EnsureSchema() { }
        public void Upsert(MessageMemoryRecord record) => Records.Add(record);
        public void Remove(string groupId, string messageId) { }
        public void RemoveGroup(string groupId) => RemovedGroups.Add(groupId);
        public void ClearAll() => Records.Clear();
        public IReadOnlyList<MessageMemoryItem> ListMessages(string? groupId, string? senderId, string? keyword, int limit, int offset) => [];
        public long CountMessages(string? groupId, string? senderId, string? keyword) => 0;
        public IReadOnlyList<MessageMemoryGroupStat> GroupStats(long nowMs) => [];
        public bool DeleteByMessageId(string messageId) => false;
        public bool UpdateImportance(string messageId, int importance) => false;
        public int SetExpiry(string? groupId, long? expiresAt, long nowMs) => 0;
        public int PruneExpired(long nowMs) => 0;
        public IReadOnlyList<MessageMemoryHit> Search(string groupId, string? agentId, float[] embedding, int topK, double minScore, string scope)
            => SearchResult;
        public IReadOnlyList<MessageMemoryHit> SearchPerson(string personId, string currentGroupId, float[] embedding, int topK, double minScore)
            => SearchResult;
    }

    private static AgentMessageMemory CreateMemory(FakeMemoryStore store, FakeHttpMessageHandler handler, bool enabled = true)
    {
        var options = new AgentOptions
        {
            Memory = new MemoryOptions
            {
                Enabled = enabled,
                EmbeddingEndpoint = "http://localhost:11434/v1",
                EmbeddingModel = "nomic-embed-text",
                EmbeddingDimensions = 3,
            },
        };
        return new AgentMessageMemory(store, options, NullLogger<AgentMessageMemory>.Instance, new HttpClient(handler));
    }

    // ================= GroupHub 写入钩子 =================

    [Fact]
    public async Task SendMessage_WritesMemory_AgentEnd_WritesFullContent_Recall_Clears()
    {
        var f = new HubFixture();
        var memory = new FakeMemory();
        var hub = new GroupHub(f.Store, f.Users, f.Connections, f.Agents, f.Triggers, f.Gateway, f.Options,
            TimeProvider.System, NullLogger<GroupHub>.Instance, changes: null, memory);

        var group = await hub.CreateGroupAsync(new GroupCreateRequest { GroupName = "记忆群", OwnerId = "user_1", MemberIds = ["agent_a"] });

        // 用户消息落库即写入记忆
        var msg = await hub.SendMessageAsync(new GroupMessageSendRequest { GroupId = group.GroupId, UserId = "user_1", Content = "第一条用户消息" });
        Assert.Contains(memory.Remembered, e => e.MessageId == msg.MessageId && e.Content == "第一条用户消息");

        // 智能体流式消息：未结束时（内容未完整）不写记忆，End 后写入完整内容
        var started = await hub.PublishAgentMessageStartAsync(new AgentMessageStartInput { GroupId = group.GroupId, AgentId = "agent_a", TopicId = "main" });
        await hub.AppendAgentContentAsync(group.GroupId, started.MessageId, "智能体");
        Assert.DoesNotContain(memory.Remembered, e => e.MessageId == started.MessageId);
        await hub.AppendAgentContentAsync(group.GroupId, started.MessageId, "回复内容");
        await hub.EndAgentMessageAsync(group.GroupId, started.MessageId);
        Assert.Contains(memory.Remembered, e => e.MessageId == started.MessageId && e.Content == "智能体回复内容");

        // 撤回 → 清理记忆
        await hub.RecallMessageAsync(new GroupMessageRecallRequest { GroupId = group.GroupId, MessageId = msg.MessageId, OperatorId = "user_1" });
        Assert.Contains(memory.Forgotten, x => x.MessageId == msg.MessageId);
    }

    [Fact]
    public async Task DisbandGroup_RemovesGroupMemory()
    {
        var f = new HubFixture();
        var memory = new FakeMemory();
        var hub = new GroupHub(f.Store, f.Users, f.Connections, f.Agents, f.Triggers, f.Gateway, f.Options,
            TimeProvider.System, NullLogger<GroupHub>.Instance, changes: null, memory);

        var group = await hub.CreateGroupAsync(new GroupCreateRequest { GroupName = "待解散群", OwnerId = "user_1" });
        await hub.DisbandGroupAsync(new GroupDisbandRequest { GroupId = group.GroupId, OperatorId = "user_1" });

        // 解散群 → 该群全部语义记忆被删除（物理删除）
        Assert.Contains(memory.RemovedGroups, gid => gid == group.GroupId);
    }

    [Fact]
    public async Task NoMemoryInjected_EverythingStillWorks()
    {
        var f = new HubFixture();
        var hub = new GroupHub(f.Store, f.Users, f.Connections, f.Agents, f.Triggers, f.Gateway, f.Options,
            TimeProvider.System, NullLogger<GroupHub>.Instance);

        var group = await hub.CreateGroupAsync(new GroupCreateRequest { GroupName = "无记忆群", OwnerId = "user_1" });
        await hub.SendMessageAsync(new GroupMessageSendRequest { GroupId = group.GroupId, UserId = "user_1", Content = "正常消息" });
        Assert.Equal(1, f.Store.AllMessages(group.GroupId).Count);
    }

    // ================= AgentMessageMemory（embedding 服务） =================

    [Fact]
    public async Task SearchAsync_EmbedsQuery_AndReturnsHits()
    {
        var handler = new FakeHttpMessageHandler();
        var store = new FakeMemoryStore
        {
            SearchResult = [new MessageMemoryHit("m1", "历史内容", "user_1", 1000, 0.8)],
        };
        using var mem = CreateMemory(store, handler);

        var hits = await mem.SearchAsync("g1", "agent_a", "semantic query text");
        Assert.Single(hits);
        Assert.Equal("m1", hits[0].MessageId);
        Assert.NotNull(handler.LastRequestBody);
        Assert.Contains("nomic-embed-text", handler.LastRequestBody);
        Assert.Contains("semantic query text", handler.LastRequestBody);
    }

    [Fact]
    public async Task Remember_EmbedsAndWrites_FireAndForget()
    {
        var handler = new FakeHttpMessageHandler();
        var store = new FakeMemoryStore();
        using var mem = CreateMemory(store, handler);

        mem.Remember(new MessageMemoryEntry("m1", "g1", "main", "user_1", "User", "需要记忆的内容", 1000));
        await Task.Delay(500); // 等待 fire-and-forget 完成
        Assert.Single(store.Records);
        Assert.Equal("m1", store.Records[0].MessageId);
        Assert.Equal(3, store.Records[0].Embedding.Length);
    }

    [Fact]
    public async Task SearchPersonAsync_EmbedsAndQueriesStore()
    {
        var handler = new FakeHttpMessageHandler();
        var store = new FakeMemoryStore
        {
            SearchResult = [new MessageMemoryHit("m1", "我偏好用 Kotlin 写后端", "user_1", 1000, 0.8)],
        };
        using var mem = CreateMemory(store, handler);

        var hits = await mem.SearchPersonAsync("user_1", "g1", "技术栈偏好");
        Assert.Single(hits);
        Assert.Equal("m1", hits[0].MessageId);
        Assert.Equal("user_1", hits[0].SenderId);
    }

    [Fact]
    public async Task Search_EndpointDown_ReturnsEmpty_NoThrow()
    {
        var handler = new FakeHttpMessageHandler { Fail = true };
        var store = new FakeMemoryStore();
        using var mem = CreateMemory(store, handler);

        var hits = await mem.SearchAsync("g1", "agent_a", "查询");
        Assert.Empty(hits);
    }

    [Fact]
    public void Disabled_IsNoop()
    {
        var handler = new FakeHttpMessageHandler();
        var store = new FakeMemoryStore();
        using var mem = CreateMemory(store, handler, enabled: false);

        mem.Remember(new MessageMemoryEntry("m1", "g1", "main", "user_1", "User", "内容", 1000));
        Task.Delay(200).Wait();
        Assert.Empty(store.Records);
        Assert.Empty(mem.SearchAsync("g1", "agent_a", "查询").Result);
    }

    // ================= prompt 段落排版 =================

    [Fact]
    public void BuildMemorySection_FormatsHits_EmptyWhenNone()
    {
        var hits = new[]
        {
            new MessageMemoryHit("m1", "V2 需要支持 WebSocket 推送", "user_1", 1750000000000, 0.82),
            new MessageMemoryHit("m2", new string('长', 1000), "agent_a", 1750000100000, 0.71),
        };
        var section = MemoryContextProvider.BuildMemorySection(hits, 600);
        Assert.Contains("历史记忆", section);
        Assert.Contains("V2 需要支持 WebSocket 推送", section);
        Assert.Contains("user_1", section);
        Assert.DoesNotContain(new string('长', 1000), section); // 长内容被截断
        Assert.Equal("", MemoryContextProvider.BuildMemorySection([], 600));
    }

    [Fact]
    public void BuildPersonSection_FormatsPersonHits_EmptyWhenNone()
    {
        var hits = new[]
        {
            new MessageMemoryHit("m1", "我偏好用 Kotlin 写后端", "user_1", 1750000000000, 0.85),
        };
        var section = MemoryContextProvider.BuildPersonSection("user_1", hits, 600);
        Assert.Contains("user_1 的个人记忆", section);
        Assert.Contains("Kotlin", section);
        Assert.Equal("", MemoryContextProvider.BuildPersonSection("user_1", [], 600));
    }
}

/// <summary>pgvector 语义记忆存储集成测试：需要带 pgvector 扩展的 PostgreSQL 测试库（AGUI_PG_TEST_CONN）。</summary>
[Trait("Category", "Postgres")]
public sealed class PgMessageMemoryStoreTests : PostgresTestBase
{
    private static readonly bool PgVectorAvailable;

    static PgMessageMemoryStoreTests()
    {
        try
        {
            using var conn = new PostgresStore(PgConnectionString).Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM pg_available_extensions WHERE name = 'vector'";
            PgVectorAvailable = cmd.ExecuteScalar() is not null;
        }
        catch
        {
            PgVectorAvailable = false;
        }
    }

    [Fact]
    public void Upsert_Search_Recall_CrossGroup_RoundTrip()
    {
        if (!PgAvailable || !PgVectorAvailable) return;

        var store = new PgMessageMemoryStore(Store, 8, NullLogger<PgMessageMemoryStore>.Instance);
        store.EnsureSchema();
        ClearMemoryTable();

        // 群成员：agent_a 在 g1（scope=agent 的检索范围依赖群成员表）
        Groups.AddGroup(new Group { GroupId = "g1", GroupName = "群1", OwnerId = "user_1", CreateTime = 1 });
        Groups.AddGroup(new Group { GroupId = "g2", GroupName = "群2", OwnerId = "user_2", CreateTime = 2 });
        Groups.AddMember("g1", new GroupMember
        {
            MemberId = "agent_a", MemberType = MemberType.Agent, Nickname = "助手",
            Role = GroupRole.Normal, OnlineStatus = OnlineStatus.Offline, JoinTime = 1,
        });

        var v1 = new float[] { 1, 0, 0, 0, 0, 0, 0, 0 };
        var v2 = new float[] { 0, 1, 0, 0, 0, 0, 0, 0 };
        store.Upsert(new MessageMemoryRecord("m1", "g1", "main", "user_1", "User", "需要 WebSocket 推送", v1, 1000));
        store.Upsert(new MessageMemoryRecord("m2", "g1", "main", "agent_a", "Agent", "V2 技术选型", v2, 2000));

        // 相似查询只命中 m1（余弦距离阈值 0.9）
        var hits = store.Search("g1", null, new float[] { 0.95f, 0.05f, 0, 0, 0, 0, 0, 0 }, 5, 0.9, "group");
        var hit = Assert.Single(hits);
        Assert.Equal("m1", hit.MessageId);
        Assert.True(hit.Score > 0.9);
        Assert.Equal("需要 WebSocket 推送", hit.Content);

        // 跨群：scope=group 不命中；scope=all 命中
        Assert.Empty(store.Search("g2", null, v1, 5, 0.9, "group"));
        Assert.Single(store.Search("g2", null, v1, 5, 0.9, "all"));

        // scope=agent：仅命中该智能体所在群的记忆（agent_a 在 g1，不在 g2）
        Assert.Single(store.Search("g9", "agent_a", v1, 5, 0.9, "agent"));
        Assert.Empty(store.Search("g9", "agent_none", v1, 5, 0.9, "agent"));

        // 撤回后不再命中
        store.Remove("g1", "m1");
        Assert.Empty(store.Search("g1", null, v1, 5, 0.9, "group"));

        // 同 message_id 覆盖：换向量后按新向量命中（v3 与 m2 的 v2 不相似，只命中 m1）
        var v3 = new float[] { 0.5f, 0.5f, 0, 0, 0, 0, 0, 0 };
        store.Upsert(new MessageMemoryRecord("m1", "g1", "main", "user_1", "User", "更新后的内容", v3, 3000));
        var updated = Assert.Single(store.Search("g1", null, v3, 5, 0.9, "group"));
        Assert.Equal("m1", updated.MessageId);
        Assert.Equal("更新后的内容", updated.Content);
    }

    [Fact]
    public void SearchPerson_OnlyOwnHistory_WithPrivateIsolation()
    {
        if (!PgAvailable || !PgVectorAvailable) return;

        var store = new PgMessageMemoryStore(Store, 8, NullLogger<PgMessageMemoryStore>.Instance);
        store.EnsureSchema();
        ClearMemoryTable();

        Groups.AddGroup(new Group { GroupId = "g_open", GroupName = "公开群", OwnerId = "user_1", CreateTime = 1 });
        Groups.AddGroup(new Group { GroupId = "g_private", GroupName = "私密群", OwnerId = "user_2", CreateTime = 2, IsPrivate = true });

        var v = new float[] { 1, 0, 0, 0, 0, 0, 0, 0 };
        // user_1 在公开群与私密群的发言；user_2 在公开群的发言
        store.Upsert(new MessageMemoryRecord("m_u1_open", "g_open", "main", "user_1", "User", "我偏好用 Kotlin 写后端", v, 1000));
        store.Upsert(new MessageMemoryRecord("m_u1_private", "g_private", "main", "user_1", "User", "机密：薪酬方案讨论中", v, 2000));
        store.Upsert(new MessageMemoryRecord("m_u2_open", "g_open", "main", "user_2", "User", "我们团队用 Java", v, 3000));

        // 在公开群触发：user_1 的个人记忆 = 公开群的发言，私密群发言被隔离、他人发言被过滤
        var fromOpen = store.SearchPerson("user_1", "g_open", v, 10, 0.9);
        var hit = Assert.Single(fromOpen);
        Assert.Equal("m_u1_open", hit.MessageId);

        // 在私密群内触发：user_1 的个人记忆可包含私密群自身发言
        var fromPrivate = store.SearchPerson("user_1", "g_private", v, 10, 0.9);
        Assert.Contains(fromPrivate, h => h.MessageId == "m_u1_private");
        Assert.Contains(fromPrivate, h => h.MessageId == "m_u1_open");

        // 他人（user_2）的个人记忆互不混淆
        var user2 = Assert.Single(store.SearchPerson("user_2", "g_open", v, 10, 0.9));
        Assert.Equal("m_u2_open", user2.MessageId);
    }

    [Fact]
    public void RemoveGroup_DeletesAllGroupMemory()
    {
        if (!PgAvailable || !PgVectorAvailable) return;

        var store = new PgMessageMemoryStore(Store, 8, NullLogger<PgMessageMemoryStore>.Instance);
        store.EnsureSchema();
        ClearMemoryTable();

        var v = new float[] { 1, 0, 0, 0, 0, 0, 0, 0 };
        store.Upsert(new MessageMemoryRecord("m1", "g_del", "main", "user_1", "User", "将被删除的内容", v, 1000));
        store.Upsert(new MessageMemoryRecord("m2", "g_del", "main", "agent_a", "Agent", "同群另一条", v, 2000));
        store.Upsert(new MessageMemoryRecord("m3", "g_keep", "main", "user_1", "User", "其他群保留", v, 3000));

        store.RemoveGroup("g_del");

        // 该群记忆物理删除，其他群不受影响
        Assert.Empty(store.Search("g_del", null, v, 10, 0.9, "group"));
        Assert.Single(store.Search("g_keep", null, v, 10, 0.9, "group"));
    }

    [Fact]
    public void Search_PrivateGroups_ExcludedOutsideCurrentGroup()
    {
        if (!PgAvailable || !PgVectorAvailable) return;

        var store = new PgMessageMemoryStore(Store, 8, NullLogger<PgMessageMemoryStore>.Instance);
        store.EnsureSchema();
        ClearMemoryTable();

        // agent_a 同时加入公开群 g_open 与两个私密群
        Groups.AddGroup(new Group { GroupId = "g_open", GroupName = "公开群", OwnerId = "user_1", CreateTime = 1 });
        Groups.AddGroup(new Group { GroupId = "g_private", GroupName = "私密群", OwnerId = "user_2", CreateTime = 2, IsPrivate = true });
        Groups.AddGroup(new Group { GroupId = "g_private2", GroupName = "私密群2", OwnerId = "user_2", CreateTime = 3, IsPrivate = true });
        foreach (var gid in new[] { "g_open", "g_private", "g_private2" })
        {
            Groups.AddMember(gid, new GroupMember
            {
                MemberId = "agent_a", MemberType = MemberType.Agent, Nickname = "助手",
                Role = GroupRole.Normal, OnlineStatus = OnlineStatus.Offline, JoinTime = 1,
            });
        }

        var v = new float[] { 1, 0, 0, 0, 0, 0, 0, 0 };
        store.Upsert(new MessageMemoryRecord("m_open", "g_open", "main", "user_1", "User", "公开群内容", v, 1000));
        store.Upsert(new MessageMemoryRecord("m_private", "g_private", "main", "user_2", "User", "私密群内容", v, 2000));
        store.Upsert(new MessageMemoryRecord("m_private2", "g_private2", "main", "user_2", "User", "私密群2内容", v, 3000));

        // 公开群触发（scope=agent）：两个私密群的记忆全部被排除，仅命中公开群自身
        var fromOpen = store.Search("g_open", "agent_a", v, 10, 0.9, "agent");
        var openHit = Assert.Single(fromOpen);
        Assert.Equal("m_open", openHit.MessageId);

        // 私密群内触发（scope=agent）：命中本群内容，排除其他私密群（g_private2），公开群内容保留
        var fromPrivate = store.Search("g_private", "agent_a", v, 10, 0.9, "agent");
        Assert.Contains(fromPrivate, h => h.MessageId == "m_private");
        Assert.DoesNotContain(fromPrivate, h => h.MessageId == "m_private2");
        Assert.Contains(fromPrivate, h => h.MessageId == "m_open");

        // scope=group：仅检索本群，私密群自身内容可命中
        Assert.Single(store.Search("g_private", null, v, 10, 0.9, "group"));
    }

    [Fact]
    public async Task FullChain_GroupHubWritesMemory_AndSearchHits()
    {
        if (!PgAvailable || !PgVectorAvailable) return;

        // 真实 pgvector 存储 + mock embedding 端点（固定向量 → 余弦相似度 1，验证写入→检索全链路）
        var pgStore = new PgMessageMemoryStore(Store, 8, NullLogger<PgMessageMemoryStore>.Instance);
        pgStore.EnsureSchema();
        ClearMemoryTable();

        var handler = new FakeHttpMessageHandler
        {
            ResponseJson = """{"data":[{"embedding":[1.0,0.0,0.0,0.0,0.0,0.0,0.0,0.0]}]}""",
        };
        var options = new AgentOptions
        {
            Memory = new MemoryOptions { Enabled = true, EmbeddingEndpoint = "http://mock/v1", EmbeddingDimensions = 8, Scope = "all" },
        };
        using var memory = new AgentMessageMemory(pgStore, options, NullLogger<AgentMessageMemory>.Instance, new HttpClient(handler));

        // GroupHub 注入真实记忆服务：消息落库 → 异步向量化写入 pgvector
        var f = new HubFixture();
        var hub = new GroupHub(f.Store, f.Users, f.Connections, f.Agents, f.Triggers, f.Gateway, f.Options,
            TimeProvider.System, NullLogger<GroupHub>.Instance, changes: null, memory);
        var group = await hub.CreateGroupAsync(new GroupCreateRequest { GroupName = "记忆全链路", OwnerId = "user_1", MemberIds = ["agent_a"] });
        await hub.SendMessageAsync(new GroupMessageSendRequest
        {
            GroupId = group.GroupId,
            UserId = "user_1",
            Content = "历史决策：采用 WebSocket 推送",
        });

        await Task.Delay(800); // 等待 fire-and-forget 向量化 + 写入完成

        // 智能体触发时按语义检索 → 命中刚才写入的记忆
        var hits = await memory.SearchAsync(group.GroupId, "agent_a", "WebSocket 方案");
        var hit = Assert.Single(hits);
        Assert.Equal("历史决策：采用 WebSocket 推送", hit.Content);
    }

    private void ClearMemoryTable()
    {
        using var conn = Store.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM agui_message_memory";
        cmd.ExecuteNonQuery();
    }
}
