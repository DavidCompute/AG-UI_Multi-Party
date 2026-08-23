using AguiGroupChat.Hub.Agents;
using AguiGroupChat.Hub.Infra;
using AguiGroupChat.Hub.Models;
using AguiGroupChat.Hub.Options;
using AguiGroupChat.Hub.Persistence;
using AguiGroupChat.Hub.Persistence.Redis;
using AguiGroupChat.Hub.Storage;
using AguiGroupChat.Hub.Users;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>
/// Redis 存储集成测试（6.2 Web 多副本）：覆盖群 / 成员 / 话题 / 消息 / 分页 / 撤回 /
/// 原地修改 / 用户 / 触发规则 / 任务 / 用量 / 扩展区 / 跨实例共享会话。
/// 需要本地 / 容器内可用的 Redis（默认 localhost:6390，可经环境变量 <c>AGUI_REDIS_TEST_URL</c> 覆盖）；
/// 未配置 / 不可达时全部跳过（不影响默认 memory 模式的其余测试）。
/// 启动示例：docker run -d --name agui-redis-test -p 6390:6379 redis:7
/// 运行：dotnet test --filter "Category=Redis"
/// </summary>
[Collection("Redis")]
[Trait("Category", "Redis")]
public sealed class RedisStoreTests : IDisposable
{
    private static readonly bool RedisAvailable;
    private static readonly string RedisUrl;

    private readonly RedisContext _ctx;
    private readonly RedisGroupStore Groups;
    private readonly RedisUserStore Users;
    private readonly RedisAgentRegistryStore AgentRegistrations;
    private readonly RedisTaskStore Tasks;
    private readonly RedisUsageStore Usage;
    private readonly RedisSectionStore Sections;

    static RedisStoreTests()
    {
        RedisUrl = Environment.GetEnvironmentVariable("AGUI_REDIS_TEST_URL") ?? "localhost:6390";
        try
        {
            using var probe = ConnectionMultiplexer.Connect(RedisUrl, options => options.AbortOnConnectFail = false);
            RedisAvailable = probe.GetDatabase().Ping().TotalMilliseconds >= 0;
        }
        catch
        {
            RedisAvailable = false;
        }
    }

    [CollectionDefinition("Redis", DisableParallelization = true)]
    public sealed class RedisCollection : ICollectionFixture<object> { }

    public RedisStoreTests()
    {
        if (!RedisAvailable) { _ctx = null!; Groups = null!; Users = null!; AgentRegistrations = null!; Tasks = null!; Usage = null!; Sections = null!; return; }
        _ctx = new RedisContext(RedisUrl);
        _ctx.FlushAguiKeys();
        Groups = new RedisGroupStore(_ctx);
        Users = new RedisUserStore(_ctx);
        AgentRegistrations = new RedisAgentRegistryStore(_ctx);
        Tasks = new RedisTaskStore(_ctx);
        Usage = new RedisUsageStore(_ctx);
        Sections = new RedisSectionStore(_ctx, new ChangeHub(), NullLogger<RedisSectionStore>.Instance);
    }

    public void Dispose()
    {
        if (!RedisAvailable) return;
        try { Sections.Dispose(); } catch { }
        try { _ctx.FlushAguiKeys(); } catch { }
        try { _ctx.Dispose(); } catch { }
    }

    private bool Ready => RedisAvailable;

    // ---- 新实例（模拟另一副本 / 重启）：底层仍是同一 Redis，读同一批 key ----
    private RedisGroupStore FreshGroups => new(_ctx);
    private RedisContext FreshContext => new(RedisUrl);

    [Fact]
    public void Group_Member_Topic_Message_RoundTrip()
    {
        if (!Ready) return;
        Groups.AddGroup(new Group { GroupId = "g1", GroupName = "Redis群", OwnerId = "u1", CreateTime = 100 });
        Groups.AddMember("g1", Member("u1", "小张"));
        Groups.AddMember("g1", Member("agent_a", "助手", MemberType.Agent));
        Groups.AddTopic(new GroupTopic { TopicId = "t1", GroupId = "g1", Name = "话题A", CreatorId = "u1", CreatedAt = 200 });
        Groups.AddMessage(Msg("m1", "g1", "u1", "第一条", 300, "t1"));

        var fresh = FreshGroups;
        Assert.Equal("Redis群", fresh.GetGroup("g1")!.GroupName);
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

        Assert.True(FreshGroups.GetMessage("g2", "m1")!.Recalled);
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

    [Fact]
    public void MessagesAfter_CursorPagination()
    {
        if (!Ready) return;
        Groups.AddGroup(new Group { GroupId = "g4", GroupName = "增量群", OwnerId = "u1", CreateTime = 100 });
        Groups.AddMember("g4", Member("u1", "小张"));
        for (var i = 0; i < 10; i++)
            Groups.AddMessage(Msg($"a{i}", "g4", "u1", $"第{i}条", 3000 + i));

        Assert.Equal(["第6条", "第7条", "第8条"], Groups.MessagesAfter("g4", "a5", 3).Select(m => m.Content).ToArray());
        Assert.Empty(Groups.MessagesAfter("g4", "a9", 3));
        Assert.Empty(Groups.MessagesAfter("g4", "nope", 3));
        Groups.AddMessage(Msg("t1", "g4", "u1", "话题1内容", 3010, topicId: "topic_1"));
        Assert.Equal(["第9条"], Groups.MessagesAfter("g4", "a8", 5, "main").Select(m => m.Content).ToArray());
        Assert.Equal(["话题1内容"], Groups.MessagesAfter("g4", "a8", 5, "topic_1").Select(m => m.Content).ToArray());
        Assert.Equal(["第9条", "话题1内容"], Groups.MessagesAfter("g4", "a8", 5).Select(m => m.Content).ToArray());
        Assert.Empty(Groups.MessagesAfter("g4", "t1", 5, "main"));
    }

    [Fact]
    public void InPlaceUpdates_Persist_AcrossReplicas()
    {
        if (!Ready) return;
        Groups.AddGroup(new Group { GroupId = "g4b", GroupName = "原群名", OwnerId = "u1", CreateTime = 100 });
        Groups.AddMember("g4b", Member("u1", "小张"));
        Groups.AddMessage(Msg("m1", "g4b", "u1", "初始内容"));

        var group = Groups.GetGroup("g4b")!;
        group.GroupName = "改后群名";
        Groups.UpdateGroup(group);

        var member = Groups.GetMember("g4b", "u1")!;
        member.Nickname = "老张";
        member.Role = GroupRole.Admin;
        Groups.UpdateMember("g4b", member);

        var msg = Groups.GetMessage("g4b", "m1")!;
        msg.Content += "追加内容";
        msg.TopicId = "t_new";
        Groups.UpdateMessage(msg);

        // 另一副本读到同一批 key，应看到全部原地修改（跨副本一致性）
        var fresh = FreshGroups;
        Assert.Equal("改后群名", fresh.GetGroup("g4b")!.GroupName);
        Assert.Equal("老张", fresh.GetMember("g4b", "u1")!.Nickname);
        Assert.Equal(GroupRole.Admin, fresh.GetMember("g4b", "u1")!.Role);
        var restored = fresh.GetMessage("g4b", "m1")!;
        Assert.Equal("初始内容追加内容", restored.Content);
        Assert.Equal("t_new", restored.TopicId);
    }

    [Fact]
    public void IsPrivate_RoundTrip_AndUpdate()
    {
        if (!Ready) return;
        Groups.AddGroup(new Group { GroupId = "g6b", GroupName = "私密群", OwnerId = "u1", CreateTime = 100, IsPrivate = true });
        Groups.AddGroup(new Group { GroupId = "g7b", GroupName = "公开群", OwnerId = "u1", CreateTime = 101 });

        var fresh = FreshGroups;
        Assert.True(fresh.GetGroup("g6b")!.IsPrivate);
        Assert.False(fresh.GetGroup("g7b")!.IsPrivate);

        var group = fresh.GetGroup("g6b")!;
        group.IsPrivate = false;
        fresh.UpdateGroup(group);
        Assert.False(FreshGroups.GetGroup("g6b")!.IsPrivate);
    }

    [Fact]
    public void UserStore_AddGetUpdateList()
    {
        if (!Ready) return;
        Assert.True(Users.AddUser(new UserAccount { UserId = "user_1", Username = "alice", Nickname = "小爱", PasswordHash = "h1", PasswordSalt = "s1", CreatedAt = 1 }));
        // 用户名冲突：AddUser 应失败且不产生残留账号
        Assert.False(Users.AddUser(new UserAccount { UserId = "user_2", Username = "alice", Nickname = "重复名", PasswordHash = "h2", PasswordSalt = "s2", CreatedAt = 2 }));
        Assert.True(Users.AddUser(new UserAccount { UserId = "user_3", Username = "bob", Nickname = "小博", PasswordHash = "h3", PasswordSalt = "s3", CreatedAt = 3, IsAdmin = true }));
        Assert.Null(Users.GetUserById("user_2")); // 冲突账号未残留

        Assert.Equal("小爱", Users.GetUserByUsername("alice")!.Nickname);
        Assert.Equal("小博", Users.GetUserByUsername("bob")!.Nickname);
        Assert.Null(Users.GetUserByUsername("nobody"));

        var u = Users.GetUserById("user_1")!;
        u.Nickname = "小爱改";
        u.IsAdmin = true;
        Users.UpdateUser(u);

        Assert.Equal("小爱改", Users.GetUserById("user_1")!.Nickname);
        Assert.Equal(2, Users.ListUsers().Count);
    }

    [Fact]
    public void AgentRegistry_Upsert_Load_Delete()
    {
        if (!Ready) return;
        AgentRegistrations.Upsert(new AgentRegistration("agent_a", "助手", "g1", AgentTriggerMode.Mentioned, ["财务", "记账"], IsOverridden: true));
        AgentRegistrations.Upsert(new AgentRegistration("agent_b", "法务", "g1", AgentTriggerMode.AllMessages, []));

        var all = AgentRegistrations.LoadAll();
        Assert.Equal(2, all.Count);
        var reg = all.First(r => r.AgentId == "agent_a");
        Assert.Equal("助手", reg.Nickname);
        Assert.Equal(AgentTriggerMode.Mentioned, reg.TriggerMode);
        Assert.True(reg.IsOverridden);
        Assert.Equal(["财务", "记账"], reg.Keywords);

        // 群范围内的删除
        AgentRegistrations.Delete("agent_a", "g1");
        Assert.Single(AgentRegistrations.LoadAll());

        // 全局删除（agentId 无 groupId）
        AgentRegistrations.Upsert(new AgentRegistration("agent_c", "营销", "g2", AgentTriggerMode.Keyword, ["推广"]));
        AgentRegistrations.Upsert(new AgentRegistration("agent_c", "营销2", "g3", AgentTriggerMode.AllMessages, []));
        AgentRegistrations.Delete("agent_c", null);
        Assert.DoesNotContain(AgentRegistrations.LoadAll(), r => r.AgentId == "agent_c");
    }

    [Fact]
    public void TaskStore_Add_Get_Update_List()
    {
        if (!Ready) return;
        Tasks.Add(new WorkTask { TaskId = "t1", GroupId = "g1", AgentId = "agent_a", UserId = "user_1", Title = "周报", Content = "内容", CreatedAt = 100, Status = WorkTaskStatus.Queue });
        Tasks.Add(new WorkTask { TaskId = "t2", GroupId = "g1", AgentId = "agent_a", UserId = "user_1", Title = "日报", Content = "内容", CreatedAt = 200, Status = WorkTaskStatus.Running });

        Assert.Equal("周报", Tasks.Get("t1")!.Title);
        Assert.Single(Tasks.ListForGroup("g1", 1));

        var t = Tasks.Get("t2")!;
        t.Status = WorkTaskStatus.Finished;
        t.Result = "完成";
        Tasks.Update(t);
        Assert.Equal(WorkTaskStatus.Finished, Tasks.Get("t2")!.Status);
        Assert.Equal("完成", Tasks.Get("t2")!.Result);

        Assert.Equal(2, Tasks.ListForUser("user_1", 10).Count);
    }

    [Fact]
    public void UsageStore_RecordAndQuery()
    {
        if (!Ready) return;
        Usage.RecordUsage("2026-01-01", "agent_a", "user_1", 10, 20, 0);
        Usage.RecordUsage("2026-01-01", "agent_b", "user_1", 5, 5, 5);
        Usage.RecordUsage("2026-01-02", "agent_a", "user_1", 100, 100, 0);

        Assert.Equal(10 + 20 + 5 + 5 + 5, Usage.GetUserUsage("user_1", "2026-01-01"));

        var between = Usage.GetUsageBetween("2026-01-01", "2026-01-02");
        Assert.Equal(3, between.Count);
        Assert.Equal(2, between.Count(u => u.Date == "2026-01-01"));
        Assert.Single(between, u => u.Date == "2026-01-02" && u.AgentId == "agent_a" && u.TotalTokens == 200);
    }

    [Fact]
    public void SectionStore_RoundTrip()
    {
        if (!Ready) return;
        var changes = new ChangeHub();
        var local = new RedisSectionStore(_ctx, changes, NullLogger<RedisSectionStore>.Instance);
        var dict = new Dictionary<string, int> { ["a"] = 1 };
        local.AddSection("test", () => dict, je => { dict = AguiJson.Deserialize<Dictionary<string, int>>(je.GetRawText()) ?? new(); });
        changes.Notify();   // 置脏位
        local.Flush();      // 立即写入 Redis

        // 另一副本（模拟新进程 / 多副本）从同一批 key 恢复
        var fresh = new RedisSectionStore(_ctx, new ChangeHub(), NullLogger<RedisSectionStore>.Instance);
        var restored = new Dictionary<string, int>();
        fresh.AddSection("test", () => restored, je => { restored = AguiJson.Deserialize<Dictionary<string, int>>(je.GetRawText()) ?? new(); });
        fresh.LoadSections();
        Assert.Equal(1, restored["a"]);
    }

    /// <summary>跨实例会话共享（6.2 核心价值）：A 副本登录，B 副本用同一令牌即可通过校验。</summary>
    [Fact]
    public void Sessions_SharedAcrossInstances()
    {
        if (!Ready) return;
        var store = new InMemoryUserStore();

        // 副本 A：注册并登录（会话写入共享 RedisSessionStore）
        var authA = new AuthService(store, new AuthOptions { SessionTtlHours = 24 }, TimeProvider.System, NullLogger<AuthService>.Instance, sessions: new RedisSessionStore(_ctx));
        authA.Register("alice", "secret1", "小爱", null);
        var token = authA.Login("alice", "secret1").Token;

        // 副本 B：共享同一批 Redis 会话 key，能直接用 A 签发的令牌通过校验
        var authB = new AuthService(store, new AuthOptions { SessionTtlHours = 24 }, TimeProvider.System, NullLogger<AuthService>.Instance, sessions: new RedisSessionStore(_ctx));
        var validated = authB.ValidateToken(token);
        Assert.NotNull(validated);
        Assert.Equal("alice", validated!.Username);
    }

    private static GroupMember Member(string id, string nickname, MemberType type = MemberType.User, GroupRole role = GroupRole.Normal)
        => new()
        {
            MemberId = id,
            MemberType = type,
            Nickname = nickname,
            Role = role,
            OnlineStatus = OnlineStatus.Online,
            JoinTime = 1000,
        };

    private static GroupMessage Msg(string id, string groupId, string sender, string content, long ts = 2000, string topicId = "main")
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
