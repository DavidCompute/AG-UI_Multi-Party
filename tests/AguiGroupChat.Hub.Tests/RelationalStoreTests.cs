using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AguiGroupChat.Agents;
using AguiGroupChat.Hub.Agents;
using AguiGroupChat.Hub.Infra;
using AguiGroupChat.Hub.Messaging;
using AguiGroupChat.Hub.Models;
using AguiGroupChat.Hub.Options;
using AguiGroupChat.Hub.Persistence;
using AguiGroupChat.Hub.Persistence.Relational;
using AguiGroupChat.Hub.Storage;
using AguiGroupChat.Hub.Users;
using AguiGroupChat.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>
/// MySQL / SQLite 共用存储测试基类：具体提供器（Sqlite / MySql 测试类）各继承一份，
/// 覆盖群 / 成员 / 话题 / 消息 / 分页 / 撤回 / 原地修改 / 用户 / 触发规则 / 扩展区 /
/// 流式防抖 / 全应用重启恢复。提供器不可用（如本机无 MySQL）时静默跳过。
/// </summary>
public abstract class RelationalStoreTestsBase : IDisposable
{
    /// <summary>SQLite 测试库文件（每个测试实例独立）。</summary>
    protected readonly string SqliteFile = Path.Combine(Path.GetTempPath(), $"agui-rel-{Guid.NewGuid():N}.db");

    protected RelationalStore Db = null!;
    protected RelationalGroupStore Groups = null!;
    protected RelationalUserStore Users = null!;
    protected RelationalAgentRegistryStore AgentRegistrations = null!;
    protected ChangeHub Changes = new();

    // ---- 由具体提供器实现 ----
    protected abstract string ProviderName { get; }
    protected abstract string ProviderConnectionString { get; }
    protected abstract bool ProviderAvailable { get; }
    protected abstract RelationalStore CreateStore(string connectionString);
    protected abstract void ResetTables(RelationalStore db);

    protected RelationalStoreTestsBase()
    {
        if (!ProviderAvailable) return;
        Db = CreateStore(ProviderConnectionString);
        Db.EnsureSchema();
        Groups = new RelationalGroupStore(Db);
        Users = new RelationalUserStore(Db);
        AgentRegistrations = new RelationalAgentRegistryStore(Db);
        ResetTables(Db);
    }

    private bool Ready => Db is not null;

    public virtual void Dispose()
    {
        try { File.Delete(SqliteFile); } catch { }
    }

    protected static GroupMember Member(string id, string nickname, MemberType type = MemberType.User, GroupRole role = GroupRole.Normal)
        => new()
        {
            MemberId = id,
            MemberType = type,
            Nickname = nickname,
            Role = role,
            OnlineStatus = OnlineStatus.Online,
            JoinTime = 1000,
        };

    protected static GroupMessage Msg(string id, string groupId, string sender, string content, long ts = 2000, string topicId = "main")
        => new()
        {
            MessageId = id,
            GroupId = groupId,
            TopicId = topicId,
            ThreadId = groupId,
            SenderId = sender,
            SenderType = MemberType.User,
            SenderNickname = sender,
            Content = content,
            Timestamp = ts,
        };

    // ================= 用例 =================

    [Fact]
    public void Group_Member_Topic_Message_RoundTrip()
    {
        if (!Ready) return;

        Groups.AddGroup(new Group { GroupId = "g1", GroupName = "关系库群", OwnerId = "u1", CreateTime = 100 });
        Groups.AddMember("g1", Member("u1", "小张"));
        Groups.AddMember("g1", Member("agent_a", "助手", MemberType.Agent));
        Groups.AddTopic(new GroupTopic { TopicId = "t1", GroupId = "g1", Name = "话题A", CreatorId = "u1", CreatedAt = 200 });
        Groups.AddMessage(Msg("m1", "g1", "u1", "第一条", 300, "t1"));

        // 新实例读回（模拟重启）
        var fresh = new RelationalGroupStore(Db);
        Assert.Equal("关系库群", fresh.GetGroup("g1")!.GroupName);
        Assert.Equal(2, fresh.MemberCount("g1"));
        Assert.True(fresh.IsMember("g1", "u1"));
        Assert.Equal(MemberType.Agent, fresh.GetMember("g1", "agent_a")!.MemberType);
        Assert.Equal("话题A", fresh.GetTopic("g1", "t1")!.Name);
        var msgs = fresh.AllMessages("g1");
        Assert.Single(msgs);
        Assert.Equal("第一条", msgs[0].Content);
        Assert.Equal("t1", msgs[0].TopicId);
        Assert.False(msgs[0].Recalled);
    }

    [Fact]
    public void RecallMessage_PersistsRecalledFlag()
    {
        if (!Ready) return;

        Groups.AddGroup(new Group { GroupId = "g2", GroupName = "撤回群", OwnerId = "u1", CreateTime = 100 });
        Groups.AddMember("g2", Member("u1", "小张"));
        Groups.AddMessage(Msg("m1", "g2", "u1", "撤回我"));
        Assert.True(Groups.RecallMessage("g2", "m1"));
        Assert.False(Groups.RecallMessage("g2", "m1"));

        Assert.True(new RelationalGroupStore(Db).GetMessage("g2", "m1")!.Recalled);
    }

    [Fact]
    public void MessagesBefore_CursorPagination()
    {
        if (!Ready) return;

        Groups.AddGroup(new Group { GroupId = "g3", GroupName = "分页群", OwnerId = "u1", CreateTime = 100 });
        Groups.AddMember("g3", Member("u1", "小张"));
        for (var i = 0; i < 10; i++)
            Groups.AddMessage(Msg($"m{i}", "g3", "u1", $"第{i}条", 2000 + i));

        Assert.Equal(5, Groups.RecentMessages("g3", 5).Count);
        Assert.Equal("第9条", Groups.RecentMessages("g3", 1)[0].Content);
        Assert.Equal(["第2条", "第3条", "第4条"], Groups.MessagesBefore("g3", "m5", 3).Select(m => m.Content).ToArray());
        Assert.Empty(Groups.MessagesBefore("g3", "m0", 3));
        Assert.Empty(Groups.MessagesBefore("g3", "nope", 3));
    }

    /// <summary>正向增量（外部 AG-UI 桥接会话建立后只发新消息）：游标之后按时间序，支持话题过滤。</summary>
    [Fact]
    public void MessagesAfter_CursorPagination()
    {
        if (!Ready) return;

        Groups.AddGroup(new Group { GroupId = "g4", GroupName = "增量群", OwnerId = "u1", CreateTime = 100 });
        Groups.AddMember("g4", Member("u1", "小张"));
        for (var i = 0; i < 10; i++)
            Groups.AddMessage(Msg($"a{i}", "g4", "u1", $"第{i}条", 3000 + i));

        // 游标 m5 之后：m6 / m7 / m8（时间序，旧→新）
        Assert.Equal(["第6条", "第7条", "第8条"], Groups.MessagesAfter("g4", "a5", 3).Select(m => m.Content).ToArray());
        // 最后一条之后无增量；游标不存在 → 空
        Assert.Empty(Groups.MessagesAfter("g4", "a9", 3));
        Assert.Empty(Groups.MessagesAfter("g4", "nope", 3));
        // 话题过滤：main 话题消息 + 其他话题消息，游标后只返回同话题增量（topicId 为空则不限制）
        Groups.AddMessage(Msg("t1", "g4", "u1", "话题1内容", 3010, topicId: "topic_1"));
        Assert.Equal(["第9条"], Groups.MessagesAfter("g4", "a8", 5, "main").Select(m => m.Content).ToArray());
        Assert.Equal(["话题1内容"], Groups.MessagesAfter("g4", "a8", 5, "topic_1").Select(m => m.Content).ToArray());
        Assert.Equal(["第9条", "话题1内容"], Groups.MessagesAfter("g4", "a8", 5).Select(m => m.Content).ToArray());
        Assert.Empty(Groups.MessagesAfter("g4", "t1", 5, "main")); // 话题1的游标在 main 话题无后续
    }

    [Fact]
    public void InPlaceUpdates_Persist()
    {
        if (!Ready) return;

        Groups.AddGroup(new Group { GroupId = "g4", GroupName = "原群名", OwnerId = "u1", CreateTime = 100 });
        Groups.AddMember("g4", Member("u1", "小张"));
        Groups.AddMessage(Msg("m1", "g4", "u1", "初始内容"));

        var group = Groups.GetGroup("g4")!;
        group.GroupName = "改后群名";
        Groups.UpdateGroup(group);

        var member = Groups.GetMember("g4", "u1")!;
        member.Nickname = "老张";
        member.Role = GroupRole.Admin;
        Groups.UpdateMember("g4", member);

        var msg = Groups.GetMessage("g4", "m1")!;
        msg.Content += "追加内容";
        msg.TopicId = "t_new";
        Groups.UpdateMessage(msg);

        var fresh = new RelationalGroupStore(Db);
        Assert.Equal("改后群名", fresh.GetGroup("g4")!.GroupName);
        Assert.Equal("老张", fresh.GetMember("g4", "u1")!.Nickname);
        Assert.Equal(GroupRole.Admin, fresh.GetMember("g4", "u1")!.Role);
        var restored = fresh.GetMessage("g4", "m1")!;
        Assert.Equal("初始内容追加内容", restored.Content);
        Assert.Equal("t_new", restored.TopicId);
    }

    [Fact]
    public void ResetAllOnlineStatuses_ForcesOffline()
    {
        if (!Ready) return;

        Groups.AddGroup(new Group { GroupId = "g5", GroupName = "状态群", OwnerId = "u1", CreateTime = 100 });
        Groups.AddMember("g5", Member("u1", "小张"));
        Groups.UpdateMemberStatus("g5", "u1", OnlineStatus.Online);

        var fresh = new RelationalGroupStore(Db);
        fresh.ResetAllOnlineStatuses();
        Assert.Equal(OnlineStatus.Offline, fresh.GetMember("g5", "u1")!.OnlineStatus);
    }

    [Fact]
    public void IsPrivate_RoundTrip_AndUpdate()
    {
        if (!Ready) return;

        Groups.AddGroup(new Group { GroupId = "g6", GroupName = "私密群", OwnerId = "u1", CreateTime = 100, IsPrivate = true });
        Groups.AddGroup(new Group { GroupId = "g7", GroupName = "公开群", OwnerId = "u1", CreateTime = 101 });

        // 新实例读回：私密标记持久化，公开群默认为 false
        var fresh = new RelationalGroupStore(Db);
        Assert.True(fresh.GetGroup("g6")!.IsPrivate);
        Assert.False(fresh.GetGroup("g7")!.IsPrivate);

        // 原地修改路径（模拟 GroupHub.UpdateGroupAsync）：私密 → 公开
        var group = fresh.GetGroup("g6")!;
        group.IsPrivate = false;
        fresh.UpdateGroup(group);
        Assert.False(new RelationalGroupStore(Db).GetGroup("g6")!.IsPrivate);
    }

    [Fact]
    public void UserStore_AddGetUpdateList()
    {
        if (!Ready) return;

        var user = new UserAccount
        {
            UserId = "user_1",
            Username = "Alice",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            Nickname = "小爱",
            CreatedAt = 100,
            UpdatedAt = 100,
            PersonalMemoryEnabled = true,
        };
        Assert.True(Users.AddUser(user));
        Assert.False(Users.AddUser(user)); // userId 冲突
        Assert.False(Users.AddUser(new UserAccount // 大小写不敏感冲突
        {
            UserId = "user_2",
            Username = "alice",
            PasswordHash = "h",
            PasswordSalt = "s",
            CreatedAt = 101,
            UpdatedAt = 101,
        }));

        var fresh = new RelationalUserStore(Db);
        Assert.Equal("小爱", fresh.GetUserByUsername("alice")!.Nickname);
        Assert.Equal("小爱", fresh.GetUserByUsername("ALICE")!.Nickname);
        Assert.Null(fresh.GetUserByUsername("bob"));
        Assert.Equal(user.UserId, fresh.GetUserById("user_1")!.UserId);

        var updated = fresh.GetUserById("user_1")!;
        updated.Nickname = "新昵称";
        updated.PasswordHash = "hash2";
        updated.UpdatedAt = 200;
        Assert.True(fresh.UpdateUser(updated));

        var fresh2 = new RelationalUserStore(Db);
        Assert.Equal("新昵称", fresh2.GetUserById("user_1")!.Nickname);
        Assert.Single(fresh2.ListUsers());
        Assert.True(fresh2.GetUserById("user_1")!.PersonalMemoryEnabled);

        // 个人记忆开关可原地更新（AuthService.UpdateProfile 路径）
        var toggled = fresh2.GetUserById("user_1")!;
        toggled.PersonalMemoryEnabled = false;
        fresh2.UpdateUser(toggled);
        Assert.False(new RelationalUserStore(Db).GetUserById("user_1")!.PersonalMemoryEnabled);
    }

    [Fact]
    public void AgentRegistryStore_UpsertLoadAllDelete()
    {
        if (!Ready) return;

        AgentRegistrations.Upsert(new AgentRegistration("agent_a", "助手A", "g1", AgentTriggerMode.Mentioned, ["a", "b"]));
        AgentRegistrations.Upsert(new AgentRegistration("agent_a", "助手A", "g2", AgentTriggerMode.Keyword, ["测试"], IsOverridden: true));
        AgentRegistrations.Upsert(new AgentRegistration("agent_b", "助手B", "g1", AgentTriggerMode.Contextual, []));

        var fresh = new RelationalAgentRegistryStore(Db);
        Assert.Equal(3, fresh.LoadAll().Count);
        var reg = Assert.Single(fresh.LoadAll().Where(r => r.AgentId == "agent_a" && r.GroupId == "g2"));
        Assert.Equal(AgentTriggerMode.Keyword, reg.TriggerMode);
        Assert.True(reg.IsOverridden);

        fresh.Upsert(new AgentRegistration("agent_a", "改名A", "g2", AgentTriggerMode.AllMessages, ["新词"], IsOverridden: false));
        var updated = Assert.Single(fresh.LoadAll().Where(r => r.AgentId == "agent_a" && r.GroupId == "g2"));
        Assert.Equal("改名A", updated.Nickname);
        Assert.Equal(AgentTriggerMode.AllMessages, updated.TriggerMode);

        fresh.Delete("agent_a", "g2");
        Assert.Equal(2, fresh.LoadAll().Count);
        fresh.Delete("agent_a", null);
        Assert.Single(fresh.LoadAll());
        Assert.Equal("agent_b", fresh.LoadAll()[0].AgentId);
    }

    [Fact]
    public void AgentRegistry_WithStore_WriteThrough()
    {
        if (!Ready) return;

        var registry = new AgentRegistry(Changes, AgentRegistrations);
        registry.Register(new AgentRegisterRequest
        {
            AgentId = "agent_c",
            Nickname = "注册即落库",
            GroupIds = ["g9"],
            TriggerMode = AgentTriggerMode.Mentioned,
        });
        registry.UpdateNickname("agent_c", "改名了");

        var fresh = new AgentRegistry(Changes, new RelationalAgentRegistryStore(Db));
        Assert.Equal("改名了", fresh.ForGroupAgent("g9", "agent_c")!.Nickname);

        fresh.Unregister("agent_c", ["g9"]);
        Assert.Empty(new AgentRegistry(Changes, new RelationalAgentRegistryStore(Db)).AllRegistrations());
    }

    [Fact]
    public void SectionStore_RoundTrip()
    {
        if (!Ready) return;

        var catalog = new AgentCatalog(
            new AgentOptions { Provider = "mock" },
            NullLoggerFactory.Instance,
            new ServiceCollection().BuildServiceProvider(),
            Changes);
        catalog.Upsert(new AgentDefinition { AgentId = "agent_s1", Nickname = "落库智能体", Instructions = "人设" });

        var sections = new RelationalSectionStore(Db, Changes, NullLogger<RelationalSectionStore>.Instance);
        sections.AddSection("agents",
            () => catalog.ListDefinitions().Select(d => (object)d).ToList(),
            element => catalog.RestoreAll(element.Deserialize<List<AgentDefinition>>(AguiJson.Options) ?? []));

        Changes.Notify();
        sections.Flush();

        var fresh = new RelationalSectionStore(Db, new ChangeHub(), NullLogger<RelationalSectionStore>.Instance);
        var freshCatalog = new AgentCatalog(
            new AgentOptions { Provider = "mock" },
            NullLoggerFactory.Instance,
            new ServiceCollection().BuildServiceProvider(),
            new ChangeHub());
        fresh.AddSection("agents",
            () => freshCatalog.ListDefinitions().Select(d => (object)d).ToList(),
            element => freshCatalog.RestoreAll(element.Deserialize<List<AgentDefinition>>(AguiJson.Options) ?? []));
        fresh.LoadSections();

        var restored = freshCatalog.ListDefinitions();
        Assert.Single(restored);
        Assert.Equal("落库智能体", restored[0].Nickname);
    }

    [Fact]
    public async Task StreamingContent_DebouncedUntilEnd_ThenFlushed()
    {
        if (!Ready) return;

        var changes = new ChangeHub();
        var groups = new RelationalGroupStore(Db);
        var users = new RelationalUserStore(Db);
        var connections = new ConnectionManager();
        var registry = new AgentRegistry(changes, new RelationalAgentRegistryStore(Db));
        var triggers = new AgentTriggerService(registry);
        var gateway = new NoopAgentGateway(NullLogger<NoopAgentGateway>.Instance);
        var options = new GroupChatOptions { MessageWriteDebounceMs = 60_000 };
        var hub = new GroupHub(groups, users, connections, registry, triggers, gateway, options,
            TimeProvider.System, NullLogger<GroupHub>.Instance, changes);

        var group = await hub.CreateGroupAsync(new GroupCreateRequest
        {
            GroupName = "流式防抖群",
            OwnerId = "user_1",
            MemberIds = ["agent_a"],
        });
        var started = await hub.PublishAgentMessageStartAsync(new AgentMessageStartInput
        {
            GroupId = group.GroupId,
            AgentId = "agent_a",
            TopicId = "main",
        });

        await hub.AppendAgentContentAsync(group.GroupId, started.MessageId, "第一段");
        Assert.Equal("第一段", new RelationalGroupStore(Db).GetMessage(group.GroupId, started.MessageId)!.Content);

        await hub.AppendAgentContentAsync(group.GroupId, started.MessageId, "第二段");
        await hub.AppendAgentContentAsync(group.GroupId, started.MessageId, "第三段");
        Assert.Equal("第一段", new RelationalGroupStore(Db).GetMessage(group.GroupId, started.MessageId)!.Content);

        await hub.EndAgentMessageAsync(group.GroupId, started.MessageId);
        Assert.Equal("第一段第二段第三段", new RelationalGroupStore(Db).GetMessage(group.GroupId, started.MessageId)!.Content);
    }

    [Fact]
    public async Task App_Restart_KeepsUsersGroupsMessagesAndAgents()
    {
        if (!Ready) return;

        string userId, token, groupId, groupName;
        try
        {
            // ============ 第一次运行：写入数据 ============
            var (app1, base1) = await StartAppAsync();
            try
            {
                using var client1 = new HttpClient { BaseAddress = new Uri(base1) };

                var reg = await client1.PostAsJsonAsync("/ag-ui/user/register", new { username = "rel_user", password = "secret1", nickname = "关系库用户" });
                reg.EnsureSuccessStatusCode();
                var auth = await reg.Content.ReadFromJsonAsync<JsonElement>();
                userId = auth.GetProperty("userId").GetString()!;
                token = auth.GetProperty("token").GetString()!;

                var create = await client1.PostAsJsonAsync("/ag-ui/group/create", new { groupName = "关系库群", ownerId = userId });
                create.EnsureSuccessStatusCode();
                var group = await create.Content.ReadFromJsonAsync<JsonElement>();
                groupId = group.GetProperty("groupId").GetString()!;
                groupName = group.GetProperty("groupName").GetString()!;

                var send = await client1.PostAsJsonAsync("/ag-ui/group/message/send", new { groupId, userId, content = "落库消息" });
                send.EnsureSuccessStatusCode();

                using var agentReq = new HttpRequestMessage(HttpMethod.Post, "/ag-ui/agents")
                {
                    Content = JsonContent.Create(new { agentId = "agent_rel", nickname = "关系库助手", description = "持久化", instructions = "常驻", triggerMode = "mentioned" }),
                };
                agentReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                (await client1.SendAsync(agentReq)).EnsureSuccessStatusCode();

                // 显式冲刷扩展区（智能体定义），保证重启前已落库
                app1.Services.GetRequiredService<ISectionStore>().Flush();
            }
            finally
            {
                await app1.DisposeAsync();
            }

            // ============ 第二次运行：从数据库恢复 ============
            var (app2, base2) = await StartAppAsync();
            try
            {
                using var client2 = new HttpClient { BaseAddress = new Uri(base2) };

                // 会话跨重启保持（保持登录状态）：app1 签发的令牌在 app2 仍有效（会话已随 agui_sections 持久化，重启时恢复）
                using var meReq = new HttpRequestMessage(HttpMethod.Get, "/ag-ui/user/me");
                meReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                var me = await client2.SendAsync(meReq);
                me.EnsureSuccessStatusCode();

                var login = await client2.PostAsJsonAsync("/ag-ui/user/login", new { username = "rel_user", password = "secret1" });
                login.EnsureSuccessStatusCode();

                var detail = await client2.GetAsync($"/ag-ui/group/{groupId}?memberId={userId}");
                detail.EnsureSuccessStatusCode();
                var snapshot = await detail.Content.ReadFromJsonAsync<JsonElement>();
                Assert.Equal(groupName, snapshot.GetProperty("groupInfo").GetProperty("groupName").GetString());
                Assert.Equal("落库消息", snapshot.GetProperty("latestMessages")[0].GetProperty("content").GetString());

                using var agentsReq = new HttpRequestMessage(HttpMethod.Get, "/ag-ui/agents");
                agentsReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                var agentsResp = await client2.SendAsync(agentsReq);
                agentsResp.EnsureSuccessStatusCode();
                var agents = await agentsResp.Content.ReadFromJsonAsync<JsonElement[]>() ?? [];
                Assert.Contains(agents, a => a.GetProperty("agentId").GetString() == "agent_rel");
            }
            finally
            {
                await app2.DisposeAsync();
            }
        }
        finally
        {
            ResetTables(Db);
        }
    }

    private async Task<(WebApplication App, string Base)> StartAppAsync()
    {
        var builder = HubApp.CreateBuilder([]);
        builder.Environment.EnvironmentName = "Testing";
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["GroupChat:SeedSampleData"] = "false",
            ["Agents:Provider"] = "mock",
            ["Storage:Provider"] = ProviderName,
            ["Storage:ConnectionString"] = ProviderConnectionString,
            ["Auth:RequireTokenOnRealTime"] = "false", // 回退模式（默认已改为强制令牌）
        });
        HubApp.ConfigureServices(builder);
        builder.Services.AddAgentFramework(builder.Configuration);
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));

        var app = builder.Build();
        HubApp.MapEndpoints(app);
        app.MapAgentApi();
        app.Services.RegisterAgentPersistence();
        app.Services.RegisterSessionPersistence(); // 会话跨重启保持（保持登录状态）
        HubApp.InitializePersistence(app);
        await app.StartAsync();
        return (app, app.Urls.First());
    }
}
