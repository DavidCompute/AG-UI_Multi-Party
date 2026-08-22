using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AguiGroupChat.Hub.Agents;
using AguiGroupChat.Hub.Infra;
using AguiGroupChat.Hub.Models;
using AguiGroupChat.Hub.Options;
using AguiGroupChat.Hub.Storage;
using AguiGroupChat.Hub.Users;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

// ================= HTTP 群接口鉴权（默认回退模式） =================

public sealed class HttpGroupApiAuthTests : IClassFixture<HubServerFixture>
{
    private readonly HubServerFixture _fixture;
    private readonly HttpClient _client;

    public HttpGroupApiAuthTests(HubServerFixture fixture)
    {
        _fixture = fixture;
        _client = new HttpClient { BaseAddress = new Uri(fixture.HttpBase) };
    }

    private async Task<(string Token, string UserId)> RegisterAsync(string username)
    {
        var res = await _client.PostAsJsonAsync("/ag-ui/user/register", new { username, password = "secret1", nickname = username });
        res.EnsureSuccessStatusCode();
        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        return (json.GetProperty("token").GetString()!, json.GetProperty("userId").GetString()!);
    }

    [Fact]
    public async Task GroupCreate_WithToken_OverridesBodyOwnerId()
    {
        var (token, userId) = await RegisterAsync("owner_alice");

        using var req = new HttpRequestMessage(HttpMethod.Post, "/ag-ui/group/create");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        req.Content = JsonContent.Create(new
        {
            groupName = "鉴权群",
            ownerId = "user_evil", // 尝试伪造群主
            memberIds = new[] { "user_evil" },
        });
        var res = await _client.SendAsync(req);
        res.EnsureSuccessStatusCode();

        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(userId, json.GetProperty("ownerId").GetString()); // 创建者 = 令牌身份，而非伪造值
    }

    [Fact]
    public async Task GroupDisband_WithOtherToken_ImpersonatingOwner_Returns403()
    {
        var (ownerToken, _) = await RegisterAsync("owner_bob");
        var (attackerToken, attackerId) = await RegisterAsync("attacker_carol");

        // 群主（令牌身份）创建群（body ownerId 会被令牌身份覆盖）
        using var create = new HttpRequestMessage(HttpMethod.Post, "/ag-ui/group/create");
        create.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ownerToken);
        create.Content = JsonContent.Create(new { groupName = "解散群", ownerId = "owner_placeholder", memberIds = new[] { attackerId } });
        var createRes = await _client.SendAsync(create);
        createRes.EnsureSuccessStatusCode();
        var groupId = (await createRes.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("groupId").GetString()!;

        // 攻击者用自己令牌冒充群主解散 → 403（operatorId 被令牌身份覆盖）
        using var disband = new HttpRequestMessage(HttpMethod.Post, "/ag-ui/group/disband");
        disband.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", attackerToken);
        disband.Content = JsonContent.Create(new { groupId, operatorId = "owner_placeholder" });
        var disbandRes = await _client.SendAsync(disband);
        Assert.Equal(HttpStatusCode.Forbidden, disbandRes.StatusCode);
    }

    [Fact]
    public async Task MessageSend_OverlongContent_Returns400()
    {
        // 回退模式（无 token）：请求体身份即可
        var create = await _client.PostAsJsonAsync("/ag-ui/group/create",
            new { groupName = "长度群", ownerId = "user_len" });
        create.EnsureSuccessStatusCode();
        var groupId = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("groupId").GetString()!;

        var overlong = new string('长', 50_001);
        var res = await _client.PostAsJsonAsync("/ag-ui/group/message/send",
            new { groupId, userId = "user_len", content = overlong });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);

        var normal = await _client.PostAsJsonAsync("/ag-ui/group/message/send",
            new { groupId, userId = "user_len", content = "正常消息" });
        normal.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task InteractionResolve_UnknownGroup_Returns404()
    {
        // 回退模式（memberId 放行）→ 群不存在 → 404（路由可达且走正常校验）
        var res = await _client.PostAsJsonAsync("/ag-ui/group/interaction/resolve",
            new { groupId = "group_x", interruptId = "interrupt_x", memberId = "user_x", approved = true });
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task AgentRegister_WithoutIdentity_Returns401_WithMemberId_Ok()
    {
        // HttpGroupApi 的 /ag-ui/agent/register（复数 groupIds）：无 token、无 memberId、无回退字段 → 401
        var noId = await _client.PostAsJsonAsync("/ag-ui/agent/register",
            new { agentId = "agent_x", nickname = "n", groupIds = new[] { "group_1" } });
        Assert.Equal(HttpStatusCode.Unauthorized, noId.StatusCode);

        // 注册触发规则需群存在且调用者 / 智能体均为群成员：先建群，再以回退模式 ?memberId= 注册
        var create = await _client.PostAsJsonAsync("/ag-ui/group/create",
            new { groupName = "注册群", ownerId = "user_demo", memberIds = new[] { "agent_x" } });
        create.EnsureSuccessStatusCode();
        var groupId = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("groupId").GetString()!;

        // 回退模式带 ?memberId= → 成功（不传 triggerMode：Hub 项目 HTTP 枚举为数字序列化）
        var withId = await _client.PostAsJsonAsync("/ag-ui/agent/register?memberId=user_demo",
            new { agentId = "agent_x", nickname = "n", groupIds = new[] { groupId } });
        Assert.Equal(HttpStatusCode.OK, withId.StatusCode);
        var json = await withId.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(groupId, json.GetProperty("groupIds")[0].GetString());
    }
}

// ================= RequireTokenOnRealTime = true（强制鉴权） =================

public sealed class SecureHubServerFixture : IAsyncLifetime
{
    public WebApplication App { get; private set; } = null!;
    public string HttpBase { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var builder = HubApp.CreateBuilder([]);
        builder.Environment.EnvironmentName = "Testing";
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["GroupChat:SeedSampleData"] = "false",
            ["Persistence:Enabled"] = "false",
            ["Auth:RequireTokenOnRealTime"] = "true", // 强制鉴权模式
        });
        HubApp.ConfigureServices(builder);
        App = builder.Build();
        HubApp.MapEndpoints(App);
        await App.StartAsync();
        HttpBase = App.Urls.First();
    }

    public async Task DisposeAsync()
    {
        if (App is not null) await App.DisposeAsync();
    }
}

public sealed class SecureModeTests : IClassFixture<SecureHubServerFixture>
{
    private readonly SecureHubServerFixture _fixture;
    private readonly HttpClient _client;

    public SecureModeTests(SecureHubServerFixture fixture)
    {
        _fixture = fixture;
        _client = new HttpClient { BaseAddress = new Uri(fixture.HttpBase) };
    }

    [Fact]
    public async Task GroupCreate_WithoutToken_Returns401()
    {
        var res = await _client.PostAsJsonAsync("/ag-ui/group/create",
            new { groupName = "群", ownerId = "user_x", memberIds = new string[] { } });
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task GroupDisband_WithoutToken_Returns401()
    {
        var res = await _client.PostAsJsonAsync("/ag-ui/group/disband",
            new { groupId = "group_x", operatorId = "user_x" });
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task InteractionResolve_WithoutToken_Returns401()
    {
        var res = await _client.PostAsJsonAsync("/ag-ui/group/interaction/resolve",
            new { groupId = "group_x", interruptId = "interrupt_x", memberId = "user_x", approved = true });
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }
}

// ================= AgentApi /register 鉴权（前端建群 / 加成员路径） =================

public sealed class AgentRegisterAuthTests : IClassFixture<AgentApiServerFixture>
{
    private readonly AgentApiServerFixture _fixture;
    private readonly HttpClient _client;

    public AgentRegisterAuthTests(AgentApiServerFixture fixture)
    {
        _fixture = fixture;
        _client = new HttpClient { BaseAddress = new Uri(fixture.HttpBase) };
    }

    [Fact]
    public async Task Register_WithoutIdentity_Returns401()
    {
        var res = await _client.PostAsJsonAsync("/ag-ui/agents/register",
            new { agentId = "agent_x", nickname = "n", groupId = "group_1", triggerMode = "mentioned" });
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Register_WithMemberId_Fallback_Ok()
    {
        // register 校验：群存在 + 调用者是群成员 + 智能体是该群成员（先建群并把智能体加为成员）
        var create = await _client.PostAsJsonAsync("/ag-ui/group/create", new
        {
            groupName = "注册群",
            ownerId = "user_demo",
            memberIds = new[] { "agent_y" },
        });
        create.EnsureSuccessStatusCode();
        var groupId = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("groupId").GetString()!;

        var res = await _client.PostAsJsonAsync("/ag-ui/agents/register?memberId=user_demo",
            new { agentId = "agent_y", nickname = "n", groupId, triggerMode = "mentioned" });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task Register_AgentNotInGroup_Returns403()
    {
        // 智能体不是该群成员 → 403（防外部注册 / 任意群挂靠触发规则）
        var create = await _client.PostAsJsonAsync("/ag-ui/group/create", new { groupName = "空群", ownerId = "user_demo2" });
        create.EnsureSuccessStatusCode();
        var groupId = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("groupId").GetString()!;

        var res = await _client.PostAsJsonAsync("/ag-ui/agents/register?memberId=user_demo2",
            new { agentId = "agent_outside", nickname = "n", groupId, triggerMode = "mentioned" });
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }
}

// ================= 附件存储目录遍历防护 =================

public sealed class AttachmentStoreSecurityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "agui-att-test-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void ResolvePath_ValidId_ReturnsFile()
    {
        var store = new AttachmentStore(_root);
        using var stream = new MemoryStream("hello".Select(c => (byte)c).ToArray());
        var info = store.Save("a.txt", "text/plain", stream, 5);

        var path = store.ResolvePath(info.AttachmentId);
        Assert.NotNull(path);
        Assert.True(File.Exists(path));
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../config")]
    [InlineData("..\\..\\Program Files")]
    [InlineData("att_%2e%2e")]
    [InlineData("../../etc")]
    [InlineData("att_123/../evil")]
    public void ResolvePath_MaliciousId_ReturnsNull(string attachmentId)
    {
        var store = new AttachmentStore(_root);
        Directory.CreateDirectory(Path.Combine(_root, "att_valid"));
        File.WriteAllText(Path.Combine(_root, "att_valid", "f.txt"), "x");
        // 同时验证：即使路径恰好解析到已存在目录，非法 ID 一律拒绝
        Assert.Null(store.ResolvePath(attachmentId));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}

// ================= 登录失败限速 =================

public sealed class AuthServiceRateLimitTests
{
    [Fact]
    public void Login_WrongPasswordThrottled_CorrectPasswordStillWorks()
    {
        var auth = new AuthService(
            new InMemoryUserStore(),
            new AuthOptions { SessionTtlHours = 1 },
            TimeProvider.System,
            NullLogger<AuthService>.Instance);

        auth.Register("alice", "secret1", null, null);

        // 前 10 次错误密码：全部被拒（最后一次起进入限速拒绝）
        for (var i = 0; i < 10; i++)
        {
            var ex = Assert.Throws<AguiProtocolException>(() => auth.Login("alice", "wrong-pass"));
            Assert.Equal(ErrorCodes.UserBadCredentials, ex.ErrorCode);
        }

        // 正确密码不被锁定拦截（先验密后计次：合法用户不会被攻击者刷错密码锁死）
        var ok = auth.Login("alice", "secret1");
        Assert.NotNull(ok.Token);

        // 登录成功清零失败计数：错误密码重新计数，第 10 次再次进入限速
        for (var i = 0; i < 9; i++)
        {
            Assert.Throws<AguiProtocolException>(() => auth.Login("alice", "wrong-pass"));
        }
        var throttled = Assert.Throws<AguiProtocolException>(() => auth.Login("alice", "wrong-pass"));
        Assert.Contains("稍后", throttled.Message);
    }
}
