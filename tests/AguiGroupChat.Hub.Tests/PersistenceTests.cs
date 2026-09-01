using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AguiGroupChat.Agents;
using AguiGroupChat.Hub.Agents;
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
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>持久化服务单元测试：快照 → 落盘 → 新实例恢复 的完整 round-trip。</summary>
public sealed class PersistenceServiceTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"agui-persist-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        try { File.Delete(_file); } catch { }
        try { File.Delete(_file + ".tmp"); } catch { }
    }

    private static (ChangeHub Changes, IUserStore Users, InMemoryGroupStore Groups, AuthService Auth, AgentRegistry Registry) BuildStores()
    {
        var changes = new ChangeHub();
        var users = new InMemoryUserStore(changes);
        var groups = new InMemoryGroupStore(200, changes);
        var auth = new AuthService(users, new AuthOptions(), TimeProvider.System, NullLogger<AuthService>.Instance, changes);
        var registry = new AgentRegistry(changes);
        return (changes, users, groups, auth, registry);
    }

    private PersistenceService CreateService(ChangeHub changes, IUserStore users, InMemoryGroupStore groups, AuthService auth, AgentRegistry registry)
        => new(users, groups, auth, registry, new PersistenceOptions { Enabled = true, FilePath = _file }, changes, NullLogger<PersistenceService>.Instance);

    /// <summary>带签名密钥的持久化服务（验证快照签名：写入签名 → 重启校验通过）。</summary>
    private PersistenceService CreateServiceSigned(ChangeHub changes, IUserStore users, InMemoryGroupStore groups, AuthService auth, AgentRegistry registry, string key)
        => new(users, groups, auth, registry, new PersistenceOptions { Enabled = true, FilePath = _file, SnapshotSigningKey = key }, changes, NullLogger<PersistenceService>.Instance);

    [Fact]
    public void RoundTrip_UsersSessionsGroupsMessagesRegistry_AllRestored()
    {
        // ---- 第一次生命周期：写入数据并落盘 ----
        var (changes, users, groups, auth, registry) = BuildStores();
        var svc = CreateService(changes, users, groups, auth, registry);

        var user = auth.Register("alice", "secret1", "小爱", null);
        var token = auth.Login("alice", "secret1").Token;
        var sessionCount = auth.SnapshotSessions().Count;

        var group = new Group
        {
            GroupId = "group_p1",
            GroupName = "持久化测试群",
            OwnerId = user.UserId,
            CreateTime = 1000,
        };
        groups.AddGroup(group);
        groups.AddMember(group.GroupId, new GroupMember
        {
            MemberId = user.UserId,
            MemberType = MemberType.User,
            Nickname = "小爱",
            Role = GroupRole.Owner,
            OnlineStatus = OnlineStatus.Online,
            JoinTime = 1000,
        });
        groups.AddMember(group.GroupId, new GroupMember
        {
            MemberId = "user_other",
            MemberType = MemberType.User,
            Nickname = "路人",
            Role = GroupRole.Normal,
            OnlineStatus = OnlineStatus.Offline,
            JoinTime = 1001,
        });

        var msg1 = new GroupMessage
        {
            MessageId = "msg_1",
            GroupId = group.GroupId,
            ThreadId = group.GroupId,
            SenderId = user.UserId,
            SenderType = MemberType.User,
            SenderNickname = "小爱",
            Content = "你好",
            Timestamp = 2000,
        };
        var msg2 = new GroupMessage
        {
            MessageId = "msg_2",
            GroupId = group.GroupId,
            ThreadId = group.GroupId,
            SenderId = "user_other",
            SenderType = MemberType.User,
            SenderNickname = "路人",
            Content = "你好呀",
            Timestamp = 2001,
        };
        groups.AddMessage(msg1);
        groups.AddMessage(msg2);
        groups.RecallMessage(group.GroupId, "msg_2");

        registry.Register(new AgentRegisterRequest
        {
            AgentId = "agent_a",
            GroupIds = [group.GroupId],
            TriggerMode = AgentTriggerMode.Keyword,
            Keywords = ["测试"],
        });

        svc.Flush();
        Assert.True(File.Exists(_file));

        // ---- 模拟重启：全新存储 + 新服务实例恢复 ----
        var (changes2, users2, groups2, auth2, registry2) = BuildStores();
        var svc2 = CreateService(changes2, users2, groups2, auth2, registry2);
        Assert.True(svc2.Load());

        // 用户与登录会话
        Assert.Equal("小爱", users2.GetUserByUsername("alice")!.Nickname);
        Assert.Equal(user.UserId, auth2.ValidateToken(token)!.UserId);
        Assert.Equal(sessionCount, auth2.SnapshotSessions().Count);

        // 群、成员（在线状态重置为离线）、消息与撤回标记
        var restoredGroup = groups2.GetGroup("group_p1");
        Assert.NotNull(restoredGroup);
        Assert.Equal(2, groups2.MemberCount("group_p1"));
        Assert.All(groups2.ListMembers("group_p1"), m => Assert.Equal(OnlineStatus.Offline, m.OnlineStatus));
        var restoredMessages = groups2.AllMessages("group_p1");
        Assert.Equal(2, restoredMessages.Count);
        Assert.Equal("你好", restoredMessages[0].Content);
        Assert.True(restoredMessages[1].Recalled); // 撤回状态保留

        // 触发规则
        var regs = registry2.ForGroup("group_p1");
        var reg = Assert.Single(regs);
        Assert.Equal("agent_a", reg.AgentId);
        Assert.Equal(AgentTriggerMode.Keyword, reg.TriggerMode);
        Assert.Contains("测试", reg.Keywords);
    }

    [Fact]
    public void SignedSnapshot_RoundTrips_AndRejectsTamper()
    {
        const string key = "test-signing-key";
        // ---- 第一次：写入并落盘（带签名）----
        var (changes, users, groups, auth, registry) = BuildStores();
        var svc = CreateServiceSigned(changes, users, groups, auth, registry, key); // 先建服务（订阅变更），再搬数据
        var user = auth.Register("bob", "secret1", "小波", null);
        var token = auth.Login("bob", "secret1").Token; // 签发的会话随快照持久化
        Assert.True(SaveAndClose(svc));

        // 磁盘 JSON 应带签名域且非空
        var raw = File.ReadAllText(_file);
        Assert.Contains("\"signature\":\"", raw);

        // ---- 模拟重启：正确密钥可恢复 ----
        var (ch2, us2, gr2, au2, rg2) = BuildStores();
        var svc2 = CreateServiceSigned(ch2, us2, gr2, au2, rg2, key);
        Assert.True(svc2.Load());
        Assert.Equal(user.UserId, au2.ValidateToken(token)?.UserId);

        // ---- 篡改：改用户名应被签名拒绝 ----
        var tampered = raw.Replace("\"username\":\"bob\"", "\"username\":\"mallory\"");
        File.WriteAllText(_file, tampered);
        var (ch3, us3, gr3, au3, rg3) = BuildStores();
        var svc3 = CreateServiceSigned(ch3, us3, gr3, au3, rg3, key);
        Assert.False(svc3.Load());
        Assert.Empty(us3.ListUsers());

        // ---- 换密钥：也应拒绝（防密钥轮换/伪造）----
        var (ch4, us4, gr4, au4, rg4) = BuildStores();
        var svc4 = CreateServiceSigned(ch4, us4, gr4, au4, rg4, "another-key");
        Assert.False(svc4.Load());
    }

    /// <summary>写入数据并手动触发落盘（模拟一次生命周期结束）。</summary>
    private static bool SaveAndClose(PersistenceService svc)
    {
        svc.Flush();
        return true;
    }

    [Fact]
    public void Load_CorruptFile_ReturnsFalse_WithoutCrash()
    {
        File.WriteAllText(_file, "{ this is not valid json !!!");

        var (changes, users, groups, auth, registry) = BuildStores();
        var svc = CreateService(changes, users, groups, auth, registry);

        Assert.False(svc.Load());
        Assert.Empty(users.ListUsers());
        // 损坏文件应被备份，防止静默用空态覆盖仅存的一份数据
        Assert.NotEmpty(Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(_file) + ".bad-*"));
    }

    [Fact]
    public void Load_MissingFile_ReturnsFalse()
    {
        var (changes, users, groups, auth, registry) = BuildStores();
        var svc = CreateService(changes, users, groups, auth, registry);
        Assert.False(svc.Load());
    }

    [Fact]
    public void Disabled_DoesNotWriteFile()
    {
        var changes = new ChangeHub();
        var users = new InMemoryUserStore(changes);
        var groups = new InMemoryGroupStore(200, changes);
        var auth = new AuthService(users, new AuthOptions(), TimeProvider.System, NullLogger<AuthService>.Instance, changes);
        var svc = new PersistenceService(users, groups, auth, new AgentRegistry(changes),
            new PersistenceOptions { Enabled = false, FilePath = _file }, changes, NullLogger<PersistenceService>.Instance);

        users.AddUser(new UserAccount { UserId = "user_x", Username = "x", Nickname = "X", PasswordHash = "h", PasswordSalt = "s", CreatedAt = 1 });

        svc.Flush();
        Assert.False(File.Exists(_file));
    }
}

/// <summary>应用级集成测试：完整服务重启后，用户 / 会话 / 群 / 消息 / 智能体全部保留。</summary>
public sealed class PersistenceIntegrationTests
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"agui-persist-app-{Guid.NewGuid():N}.json");

    private static async Task<(WebApplication App, string Base)> StartAppAsync(string dataFile)
    {
        var builder = HubApp.CreateBuilder([]);
        builder.Environment.EnvironmentName = "Testing";
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["GroupChat:SeedSampleData"] = "false",
            ["Agents:Provider"] = "mock",
            ["Persistence:Enabled"] = "true",
            ["Persistence:FilePath"] = dataFile,
            ["Persistence:FlushIntervalSeconds"] = "1",
            ["Auth:RequireTokenOnRealTime"] = "false", // 回退模式（默认已改为强制令牌）
        });
        HubApp.ConfigureServices(builder);
        builder.Services.AddAgentFramework(builder.Configuration);
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));

        var app = builder.Build();
        HubApp.MapEndpoints(app);
        app.MapAgentApi();
        app.Services.RegisterAgentPersistence();
        HubApp.InitializePersistence(app);
        await app.StartAsync();
        return (app, app.Urls.First());
    }

    [Fact]
    public async Task Restart_KeepsUsersSessionsGroupsMessagesAndAgents()
    {
        string userId, token, groupId, groupName;
        try
        {
            // ============ 第一次运行：写入数据 ============
            var (app1, base1) = await StartAppAsync(_file);
            try
            {
                using var client1 = new HttpClient { BaseAddress = new Uri(base1) };

                var reg = await client1.PostAsJsonAsync("/ag-ui/user/register", new { username = "persist_user", password = "secret1", nickname = "持久用户" });
                reg.EnsureSuccessStatusCode();
                var auth = await reg.Content.ReadFromJsonAsync<JsonElement>();
                userId = auth.GetProperty("userId").GetString()!;
                token = auth.GetProperty("token").GetString()!;

                var create = await client1.PostAsJsonAsync("/ag-ui/group/create", new { groupName = "持久化群", ownerId = userId });
                create.EnsureSuccessStatusCode();
                var group = await create.Content.ReadFromJsonAsync<JsonElement>();
                groupId = group.GetProperty("groupId").GetString()!;
                groupName = group.GetProperty("groupName").GetString()!;

                var send = await client1.PostAsJsonAsync("/ag-ui/group/message/send", new { groupId, userId, content = "重启后我还在" });
                send.EnsureSuccessStatusCode();

                using var agentReq = new HttpRequestMessage(HttpMethod.Post, "/ag-ui/agents")
                {
                    Content = JsonContent.Create(new { agentId = "agent_keep", nickname = "常驻助手", description = "持久化", instructions = "常驻", triggerMode = "mentioned" }),
                };
                agentReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                (await client1.SendAsync(agentReq)).EnsureSuccessStatusCode();

                // 显式冲刷，保证落盘后再“重启”
                app1.Services.GetRequiredService<PersistenceService>().Flush();
            }
            finally
            {
                await app1.DisposeAsync();
            }

            // ============ 第二次运行：从同一文件恢复 ============
            var (app2, base2) = await StartAppAsync(_file);
            try
            {
                using var client2 = new HttpClient { BaseAddress = new Uri(base2) };

                // 会话令牌仍然有效（无需重新登录）
                using var meReq = new HttpRequestMessage(HttpMethod.Get, "/ag-ui/user/me");
                meReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                var me = await client2.SendAsync(meReq);
                me.EnsureSuccessStatusCode();
                Assert.Equal(userId, (await me.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("userId").GetString());

                // 账号可用旧密码登录
                var login = await client2.PostAsJsonAsync("/ag-ui/user/login", new { username = "persist_user", password = "secret1" });
                login.EnsureSuccessStatusCode();

                // 群与消息保留
                var detail = await client2.GetAsync($"/ag-ui/group/{groupId}?memberId={userId}");
                detail.EnsureSuccessStatusCode();
                var snapshot = await detail.Content.ReadFromJsonAsync<JsonElement>();
                Assert.Equal(groupName, snapshot.GetProperty("groupInfo").GetProperty("groupName").GetString());
                Assert.Equal("重启后我还在", snapshot.GetProperty("latestMessages")[0].GetProperty("content").GetString());

                // 智能体保留
                var agents = await client2.GetFromJsonAsync<JsonElement[]>("/ag-ui/agents") ?? [];
                Assert.Contains(agents, a => a.GetProperty("agentId").GetString() == "agent_keep");
            }
            finally
            {
                await app2.DisposeAsync();
            }
        }
        finally
        {
            try { File.Delete(_file); } catch { }
            try { File.Delete(_file + ".tmp"); } catch { }
        }
    }
}
