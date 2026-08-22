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
using AguiGroupChat.Hub.Persistence.Postgres;
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
/// 全部 PG 测试共享同一测试库表（agui_test），必须串行执行，避免并行 TRUNCATE 相互干扰。
/// </summary>
[CollectionDefinition("Postgres", DisableParallelization = true)]
public sealed class PostgresTestCollection { }

/// <summary>
/// PostgreSQL 集成测试基类：需要本地 / 容器内可用的 PostgreSQL 测试库（默认连接 agui_test）。
/// 未配置数据库时全部跳过（不影响默认 memory 模式的其余测试）；每个测试前清空全部表。
/// 连接串可用环境变量 <c>AGUI_PG_TEST_CONN</c> 覆盖；建库示例：
///   docker run -d --name agui-pg-test -e POSTGRES_PASSWORD=postgres -e POSTGRES_DB=agui_test -p 55432:5432 postgres:16
/// 运行：dotnet test --filter "Category=Postgres"
/// </summary>
[Collection("Postgres")]
public abstract class PostgresTestBase : IDisposable
{
    protected static readonly bool PgAvailable;
    protected static readonly string PgConnectionString;

    protected PostgresStore Store { get; }
    protected PostgresGroupStore Groups { get; }
    protected PostgresUserStore Users { get; }
    protected PostgresAgentRegistryStore AgentRegistrations { get; }
    protected ChangeHub Changes { get; } = new();

    static PostgresTestBase()
    {
        PgConnectionString = Environment.GetEnvironmentVariable("AGUI_PG_TEST_CONN")
            ?? "Host=localhost;Port=5432;Database=agui_test;Username=postgres;Password=postgres";
        try
        {
            using var probe = new PostgresStore(PgConnectionString).Open();
            PgAvailable = true;
        }
        catch
        {
            PgAvailable = false;
        }
    }

    protected PostgresTestBase()
    {
        if (!PgAvailable) return; // 未配置 PostgreSQL：跳过（构造不触碰数据库）
        Store = new PostgresStore(PgConnectionString);
        Store.EnsureSchema();
        Groups = new PostgresGroupStore(Store);
        Users = new PostgresUserStore(Store);
        AgentRegistrations = new PostgresAgentRegistryStore(Store);
        ResetTables();
    }

    protected void ResetTables()
    {
        using var conn = Store.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            TRUNCATE agui_groups, agui_group_members, agui_topics, agui_messages,
                     agui_users, agui_agent_registrations, agui_sections CASCADE
            """;
        cmd.ExecuteNonQuery();
    }

    public virtual void Dispose() { }

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
}

[Trait("Category", "Postgres")]
public sealed class PostgresGroupStoreTests : PostgresTestBase
{
    [Fact]
    public void Group_Member_Topic_Message_RoundTrip_AcrossInstances()
    {
        if (!PgAvailable) return;

        Groups.AddGroup(new Group { GroupId = "g1", GroupName = "PG群", OwnerId = "u1", CreateTime = 100 });
        Groups.AddMember("g1", Member("u1", "小张"));
        Groups.AddMember("g1", Member("agent_a", "助手", MemberType.Agent));
        Groups.AddTopic(new GroupTopic { TopicId = "t1", GroupId = "g1", Name = "话题A", CreatorId = "u1", CreatedAt = 200 });
        Groups.AddMessage(Msg("m1", "g1", "u1", "第一条", 300, "t1"));

        // 新实例读回（模拟重启后从库恢复）
        var fresh = new PostgresGroupStore(Store);
        var group = fresh.GetGroup("g1");
        Assert.NotNull(group);
        Assert.Equal("PG群", group!.GroupName);
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
        if (!PgAvailable) return;

        Groups.AddGroup(new Group { GroupId = "g2", GroupName = "撤回群", OwnerId = "u1", CreateTime = 100 });
        Groups.AddMember("g2", Member("u1", "小张"));
        Groups.AddMessage(Msg("m1", "g2", "u1", "撤回我"));
        Assert.True(Groups.RecallMessage("g2", "m1"));
        Assert.False(Groups.RecallMessage("g2", "m1")); // 二次撤回失败

        // 新实例读回：撤回标记保留（不再重新可见）
        var fresh = new PostgresGroupStore(Store);
        Assert.True(fresh.GetMessage("g2", "m1")!.Recalled);
    }

    [Fact]
    public void MessagesBefore_CursorPagination_MatchesMemorySemantics()
    {
        if (!PgAvailable) return;

        Groups.AddGroup(new Group { GroupId = "g3", GroupName = "分页群", OwnerId = "u1", CreateTime = 100 });
        Groups.AddMember("g3", Member("u1", "小张"));
        for (var i = 0; i < 10; i++)
            Groups.AddMessage(Msg($"m{i}", "g3", "u1", $"第{i}条", 2000 + i));

        Assert.Equal(5, Groups.RecentMessages("g3", 5).Count);
        Assert.Equal("第9条", Groups.RecentMessages("g3", 1)[0].Content);

        // 游标 m5 → 返回 m2..m4（3 条，时间序）
        var before = Groups.MessagesBefore("g3", "m5", 3);
        Assert.Equal(["第2条", "第3条", "第4条"], before.Select(m => m.Content).ToArray());

        // 游标为首条 → 空
        Assert.Empty(Groups.MessagesBefore("g3", "m0", 3));
        // 游标不存在 → 空
        Assert.Empty(Groups.MessagesBefore("g3", "nope", 3));
    }

    [Fact]
    public void InPlaceUpdates_Persist_GroupMemberMessage()
    {
        if (!PgAvailable) return;

        Groups.AddGroup(new Group { GroupId = "g4", GroupName = "原群名", OwnerId = "u1", CreateTime = 100 });
        Groups.AddMember("g4", Member("u1", "小张"));
        Groups.AddMessage(Msg("m1", "g4", "u1", "初始内容"));

        // 模拟 GroupHub 的原地修改路径
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

        // 新实例读回
        var fresh = new PostgresGroupStore(Store);
        Assert.Equal("改后群名", fresh.GetGroup("g4")!.GroupName);
        var m = fresh.GetMember("g4", "u1")!;
        Assert.Equal("老张", m.Nickname);
        Assert.Equal(GroupRole.Admin, m.Role);
        var restored = fresh.GetMessage("g4", "m1")!;
        Assert.Equal("初始内容追加内容", restored.Content);
        Assert.Equal("t_new", restored.TopicId);
    }

    [Fact]
    public void ResetAllOnlineStatuses_ForcesOffline()
    {
        if (!PgAvailable) return;

        Groups.AddGroup(new Group { GroupId = "g5", GroupName = "状态群", OwnerId = "u1", CreateTime = 100 });
        Groups.AddMember("g5", Member("u1", "小张"));
        Groups.UpdateMemberStatus("g5", "u1", OnlineStatus.Online);

        var fresh = new PostgresGroupStore(Store);
        fresh.ResetAllOnlineStatuses();
        Assert.Equal(OnlineStatus.Offline, fresh.GetMember("g5", "u1")!.OnlineStatus);
    }

    [Fact]
    public void IsPrivate_RoundTrip_AndUpdate()
    {
        if (!PgAvailable) return;

        Groups.AddGroup(new Group { GroupId = "g6", GroupName = "私密群", OwnerId = "u1", CreateTime = 100, IsPrivate = true });
        Groups.AddGroup(new Group { GroupId = "g7", GroupName = "公开群", OwnerId = "u1", CreateTime = 101 });

        // 新实例读回：私密标记持久化，公开群默认为 false
        var fresh = new PostgresGroupStore(Store);
        Assert.True(fresh.GetGroup("g6")!.IsPrivate);
        Assert.False(fresh.GetGroup("g7")!.IsPrivate);

        // 原地修改路径（模拟 GroupHub.UpdateGroupAsync）：私密 → 公开
        var group = fresh.GetGroup("g6")!;
        group.IsPrivate = false;
        fresh.UpdateGroup(group);
        Assert.False(new PostgresGroupStore(Store).GetGroup("g6")!.IsPrivate);
    }
}

[Trait("Category", "Postgres")]
public sealed class PostgresUserStoreTests : PostgresTestBase
{
    [Fact]
    public void Add_Get_Update_List_RoundTrip()
    {
        if (!PgAvailable) return;

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
        Assert.False(Users.AddUser(new UserAccount
        {
            UserId = "user_2",
            Username = "alice", // 大小写不敏感冲突
            PasswordHash = "h",
            PasswordSalt = "s",
            CreatedAt = 101,
            UpdatedAt = 101,
        }));

        // 新实例读回 + 大小写不敏感查询
        var fresh = new PostgresUserStore(Store);
        Assert.Equal("小爱", fresh.GetUserByUsername("alice")!.Nickname);
        Assert.Equal("小爱", fresh.GetUserByUsername("ALICE")!.Nickname);
        Assert.Null(fresh.GetUserByUsername("bob"));
        Assert.Equal(user.UserId, fresh.GetUserById("user_1")!.UserId);

        var updated = fresh.GetUserById("user_1")!;
        updated.Nickname = "新昵称";
        updated.PasswordHash = "hash2";
        updated.UpdatedAt = 200;
        Assert.True(fresh.UpdateUser(updated));

        var fresh2 = new PostgresUserStore(Store);
        Assert.Equal("新昵称", fresh2.GetUserById("user_1")!.Nickname);
        Assert.Equal("hash2", fresh2.GetUserById("user_1")!.PasswordHash);
        Assert.True(fresh2.GetUserById("user_1")!.PersonalMemoryEnabled);

        // 个人记忆开关可原地更新（AuthService.UpdateProfile 路径）
        var toggled = fresh2.GetUserById("user_1")!;
        toggled.PersonalMemoryEnabled = false;
        fresh2.UpdateUser(toggled);
        Assert.False(new PostgresUserStore(Store).GetUserById("user_1")!.PersonalMemoryEnabled);
        Assert.Single(fresh2.ListUsers());
    }
}

[Trait("Category", "Postgres")]
public sealed class PostgresAgentRegistryStoreTests : PostgresTestBase
{
    [Fact]
    public void Upsert_LoadAll_Delete_RoundTrip()
    {
        if (!PgAvailable) return;

        AgentRegistrations.Upsert(new AgentRegistration("agent_a", "助手A", "g1", AgentTriggerMode.Mentioned, ["a", "b"]));
        AgentRegistrations.Upsert(new AgentRegistration("agent_a", "助手A", "g2", AgentTriggerMode.Keyword, ["测试"], IsOverridden: true));
        AgentRegistrations.Upsert(new AgentRegistration("agent_b", "助手B", "g1", AgentTriggerMode.Contextual, []));

        var fresh = new PostgresAgentRegistryStore(Store);
        var all = fresh.LoadAll();
        Assert.Equal(3, all.Count);
        var reg = Assert.Single(all.Where(r => r.AgentId == "agent_a" && r.GroupId == "g2"));
        Assert.Equal(AgentTriggerMode.Keyword, reg.TriggerMode);
        Assert.Contains("测试", reg.Keywords);
        Assert.True(reg.IsOverridden);

        // 更新（写通）
        fresh.Upsert(new AgentRegistration("agent_a", "改名A", "g2", AgentTriggerMode.AllMessages, ["新词"], IsOverridden: false));
        var fresh2 = new PostgresAgentRegistryStore(Store);
        var updated = Assert.Single(fresh2.LoadAll().Where(r => r.AgentId == "agent_a" && r.GroupId == "g2"));
        Assert.Equal("改名A", updated.Nickname);
        Assert.Equal(AgentTriggerMode.AllMessages, updated.TriggerMode);

        // 按群删除
        fresh2.Delete("agent_a", "g2");
        var fresh3 = new PostgresAgentRegistryStore(Store);
        Assert.Equal(2, fresh3.LoadAll().Count);

        // 全量删除
        fresh3.Delete("agent_a", null);
        var fresh4 = new PostgresAgentRegistryStore(Store);
        Assert.Single(fresh4.LoadAll());
        Assert.Equal("agent_b", fresh4.LoadAll()[0].AgentId);
    }

    [Fact]
    public void AgentRegistry_WithStore_WriteThroughAndRestore()
    {
        if (!PgAvailable) return;

        // 构造时从库加载；Register 写通
        var registry = new AgentRegistry(Changes, AgentRegistrations);
        registry.Register(new AgentRegisterRequest
        {
            AgentId = "agent_c",
            Nickname = "注册即落库",
            GroupIds = ["g9"],
            TriggerMode = AgentTriggerMode.Mentioned,
        });
        registry.UpdateNickname("agent_c", "改名了");

        // 新实例（模拟重启）从库恢复
        var fresh = new AgentRegistry(Changes, new PostgresAgentRegistryStore(Store));
        var reg = fresh.ForGroupAgent("g9", "agent_c");
        Assert.NotNull(reg);
        Assert.Equal("改名了", reg!.Nickname);

        fresh.Unregister("agent_c", ["g9"]);
        Assert.Empty(new AgentRegistry(Changes, new PostgresAgentRegistryStore(Store)).AllRegistrations());
    }
}

[Trait("Category", "Postgres")]
public sealed class PostgresSectionStoreTests : PostgresTestBase
{
    [Fact]
    public void Section_RoundTrip_AcrossInstances()
    {
        if (!PgAvailable) return;

        var catalog = new AgentCatalog(
            new AgentOptions { Provider = "mock" },
            NullLoggerFactory.Instance,
            new ServiceCollection().BuildServiceProvider(),
            Changes);
        catalog.Upsert(new AgentDefinition { AgentId = "agent_s1", Nickname = "落库智能体", Instructions = "人设" });

        var sections = new PostgresSectionStore(Store, Changes, NullLogger<PostgresSectionStore>.Instance);
        sections.AddSection("agents",
            () => catalog.ListDefinitions().Select(d => (object)d).ToList(),
            element => catalog.RestoreAll(element.Deserialize<List<AgentDefinition>>(AguiJson.Options) ?? []));

        // 模拟变更 → Flush 落库
        Changes.Notify();
        sections.Flush();
        // 未变更时 Flush 不重复写（快照内容一致即跳过）
        Changes.Notify();
        sections.Flush();

        // 新实例：从库恢复
        var fresh = new PostgresSectionStore(Store, new ChangeHub(), NullLogger<PostgresSectionStore>.Instance);
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
}

/// <summary>流式内容防抖写库测试：窗口内只写首条增量，消息结束时强制全量落库。</summary>
[Trait("Category", "Postgres")]
public sealed class PostgresStreamingDebounceTests : PostgresTestBase
{
    [Fact]
    public async Task StreamingContent_DebouncedUntilEnd_ThenFlushed()
    {
        if (!PgAvailable) return;

        var changes = new ChangeHub();
        var groups = new PostgresGroupStore(Store);
        var users = new PostgresUserStore(Store);
        var connections = new ConnectionManager();
        var registry = new AgentRegistry(changes, new PostgresAgentRegistryStore(Store));
        var triggers = new AgentTriggerService(registry);
        var gateway = new NoopAgentGateway(NullLogger<NoopAgentGateway>.Instance);
        // 防抖窗口放大到 60 秒：验证窗口内合并、仅首条与结束写库
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

        // 首条增量立即写（防抖窗口起点）；窗口内第二条不写库
        await hub.AppendAgentContentAsync(group.GroupId, started.MessageId, "第一段");
        Assert.Equal("第一段", new PostgresGroupStore(Store).GetMessage(group.GroupId, started.MessageId)!.Content);

        await hub.AppendAgentContentAsync(group.GroupId, started.MessageId, "第二段");
        await hub.AppendAgentContentAsync(group.GroupId, started.MessageId, "第三段");
        Assert.Equal("第一段", new PostgresGroupStore(Store).GetMessage(group.GroupId, started.MessageId)!.Content);

        // 消息结束：全量内容落库
        await hub.EndAgentMessageAsync(group.GroupId, started.MessageId);
        Assert.Equal("第一段第二段第三段", new PostgresGroupStore(Store).GetMessage(group.GroupId, started.MessageId)!.Content);
    }

    [Fact]
    public async Task StreamingContent_WriteThroughWithZeroDebounce()
    {
        if (!PgAvailable) return;

        var changes = new ChangeHub();
        var groups = new PostgresGroupStore(Store);
        var users = new PostgresUserStore(Store);
        var connections = new ConnectionManager();
        var registry = new AgentRegistry(changes, new PostgresAgentRegistryStore(Store));
        var triggers = new AgentTriggerService(registry);
        var gateway = new NoopAgentGateway(NullLogger<NoopAgentGateway>.Instance);
        // MessageWriteDebounceMs=0：每次增量立即写库（旧行为）
        var options = new GroupChatOptions { MessageWriteDebounceMs = 0 };
        var hub = new GroupHub(groups, users, connections, registry, triggers, gateway, options,
            TimeProvider.System, NullLogger<GroupHub>.Instance, changes);

        var group = await hub.CreateGroupAsync(new GroupCreateRequest
        {
            GroupName = "直写群",
            OwnerId = "user_1",
            MemberIds = ["agent_a"],
        });
        var started = await hub.PublishAgentMessageStartAsync(new AgentMessageStartInput
        {
            GroupId = group.GroupId,
            AgentId = "agent_a",
            TopicId = "main",
        });

        await hub.AppendAgentContentAsync(group.GroupId, started.MessageId, "甲");
        Assert.Equal("甲", new PostgresGroupStore(Store).GetMessage(group.GroupId, started.MessageId)!.Content);
        await hub.AppendAgentContentAsync(group.GroupId, started.MessageId, "乙");
        Assert.Equal("甲乙", new PostgresGroupStore(Store).GetMessage(group.GroupId, started.MessageId)!.Content);

        await hub.EndAgentMessageAsync(group.GroupId, started.MessageId);
        Assert.Equal("甲乙", new PostgresGroupStore(Store).GetMessage(group.GroupId, started.MessageId)!.Content);
    }
}

/// <summary>应用级集成测试：完整服务以 PostgreSQL 为存储重启后，用户 / 群 / 消息 / 智能体全部保留。</summary>
[Trait("Category", "Postgres")]
public sealed class PostgresAppIntegrationTests : PostgresTestBase
{
    [Fact]
    public async Task Restart_KeepsUsersGroupsMessagesAndAgents()
    {
        if (!PgAvailable) return;

        string userId, token, groupId, groupName;
        try
        {
            // ============ 第一次运行：写入数据 ============
            var (app1, base1) = await StartPgAppAsync();
            try
            {
                using var client1 = new HttpClient { BaseAddress = new Uri(base1) };

                var reg = await client1.PostAsJsonAsync("/ag-ui/user/register", new { username = "pg_user", password = "secret1", nickname = "PG用户" });
                reg.EnsureSuccessStatusCode();
                var auth = await reg.Content.ReadFromJsonAsync<JsonElement>();
                userId = auth.GetProperty("userId").GetString()!;
                token = auth.GetProperty("token").GetString()!;

                var create = await client1.PostAsJsonAsync("/ag-ui/group/create", new { groupName = "PG持久化群", ownerId = userId });
                create.EnsureSuccessStatusCode();
                var group = await create.Content.ReadFromJsonAsync<JsonElement>();
                groupId = group.GetProperty("groupId").GetString()!;
                groupName = group.GetProperty("groupName").GetString()!;

                var send = await client1.PostAsJsonAsync("/ag-ui/group/message/send", new { groupId, userId, content = "落库消息" });
                send.EnsureSuccessStatusCode();

                using var agentReq = new HttpRequestMessage(HttpMethod.Post, "/ag-ui/agents")
                {
                    Content = JsonContent.Create(new { agentId = "agent_pg", nickname = "PG助手", description = "持久化", instructions = "常驻", triggerMode = "mentioned" }),
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

            // ============ 第二次运行：从 PostgreSQL 恢复 ============
            var (app2, base2) = await StartPgAppAsync();
            try
            {
                using var client2 = new HttpClient { BaseAddress = new Uri(base2) };

                // 会话跨重启保持（保持登录状态）：app1 签发的令牌在 app2 仍有效（会话已随 agui_sections 持久化，重启时恢复）
                using var meReq = new HttpRequestMessage(HttpMethod.Get, "/ag-ui/user/me");
                meReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                var me = await client2.SendAsync(meReq);
                me.EnsureSuccessStatusCode();

                // 账号仍可用密码登录
                var login = await client2.PostAsJsonAsync("/ag-ui/user/login", new { username = "pg_user", password = "secret1" });
                login.EnsureSuccessStatusCode();
                var loginResult = await login.Content.ReadFromJsonAsync<JsonElement>();
                var token2 = loginResult.GetProperty("token").GetString()!;

                // 群与消息保留
                var detail = await client2.GetAsync($"/ag-ui/group/{groupId}?memberId={userId}");
                detail.EnsureSuccessStatusCode();
                var snapshot = await detail.Content.ReadFromJsonAsync<JsonElement>();
                Assert.Equal(groupName, snapshot.GetProperty("groupInfo").GetProperty("groupName").GetString());
                Assert.Equal("落库消息", snapshot.GetProperty("latestMessages")[0].GetProperty("content").GetString());

                // 智能体保留（新登录令牌 + 管理接口）
                using var agentsReq = new HttpRequestMessage(HttpMethod.Get, "/ag-ui/agents");
                agentsReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token2);
                var agentsResp = await client2.SendAsync(agentsReq);
                agentsResp.EnsureSuccessStatusCode();
                var agents = await agentsResp.Content.ReadFromJsonAsync<JsonElement[]>() ?? [];
                Assert.Contains(agents, a => a.GetProperty("agentId").GetString() == "agent_pg");

                // 成员在线状态重启后为离线（HTTP 枚举为 PascalCase 字符串，忽略大小写比较）
                var members = snapshot.GetProperty("members");
                Assert.All(members.EnumerateArray(), m =>
                    Assert.Equal("offline", m.GetProperty("onlineStatus").GetString(), ignoreCase: true));
            }
            finally
            {
                await app2.DisposeAsync();
            }
        }
        finally
        {
            ResetTables();
        }
    }

    private async Task<(WebApplication App, string Base)> StartPgAppAsync()
    {
        var builder = HubApp.CreateBuilder([]);
        builder.Environment.EnvironmentName = "Testing";
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["GroupChat:SeedSampleData"] = "false",
            ["Agents:Provider"] = "mock",
            ["Storage:Provider"] = "postgres",
            ["Storage:ConnectionString"] = PgConnectionString,
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
