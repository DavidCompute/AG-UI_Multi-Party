using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using AguiGroupChat.Hub.Infra;
using AguiGroupChat.Hub.Models;
using AguiGroupChat.Hub.Options;
using AguiGroupChat.Hub.Storage;
using AguiGroupChat.Hub.Users;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

public sealed class UserAuthServiceTests
{
    private static AuthService CreateAuth(AuthOptions? options = null)
        => new(new InMemoryUserStore(), options ?? new AuthOptions(), TimeProvider.System, NullLogger<AuthService>.Instance);

    [Fact]
    public void Register_CreatesAccountWithUserPrefixAndAutoLoginable()
    {
        var auth = CreateAuth();
        var user = auth.Register("alice", "secret1", "小爱", null);

        Assert.StartsWith("user_", user.UserId);
        Assert.Equal("小爱", user.Nickname);
        Assert.NotEqual("secret1", user.PasswordHash);

        // 注册后可通过登录获取令牌
        var login = auth.Login("alice", "secret1");
        Assert.Equal(user.UserId, login.User.UserId);
        Assert.NotNull(login.Token);
    }

    [Fact]
    public void Register_DuplicateUsername_ThrowsUserExists()
    {
        var auth = CreateAuth();
        auth.Register("alice", "secret1", null, null);

        var ex = Assert.Throws<AguiProtocolException>(() => auth.Register("ALICE", "other6", null, null));
        Assert.Equal(ErrorCodes.UserExists, ex.ErrorCode);
    }

    [Fact]
    public void Register_ShortUsernameOrPassword_ThrowsBadRequest()
    {
        var auth = CreateAuth();
        Assert.Equal(ErrorCodes.BadRequest,
            Assert.Throws<AguiProtocolException>(() => auth.Register("ab", "secret1", null, null)).ErrorCode);
        Assert.Equal(ErrorCodes.BadRequest,
            Assert.Throws<AguiProtocolException>(() => auth.Register("alice", "123", null, null)).ErrorCode);
    }

    [Fact]
    public void Login_WrongPassword_ThrowsBadCredentials()
    {
        var auth = CreateAuth();
        auth.Register("alice", "secret1", null, null);

        var ex = Assert.Throws<AguiProtocolException>(() => auth.Login("alice", "wrong-pass"));
        Assert.Equal(ErrorCodes.UserBadCredentials, ex.ErrorCode);
        Assert.Equal(ErrorCodes.UserBadCredentials,
            Assert.Throws<AguiProtocolException>(() => auth.Login("nobody", "secret1")).ErrorCode);
    }

    [Fact]
    public void ValidateToken_ReturnsUser_AndInvalidatesAfterLogout()
    {
        var auth = CreateAuth();
        auth.Register("alice", "secret1", null, null);
        var token = auth.Login("alice", "secret1").Token;

        var user = auth.ValidateToken(token);
        Assert.NotNull(user);
        Assert.Equal("alice", user!.Username);

        auth.Logout(token);
        Assert.Null(auth.ValidateToken(token));
    }

    [Fact]
    public void ValidateToken_UnknownOrNullToken_ReturnsNull()
    {
        var auth = CreateAuth();
        Assert.Null(auth.ValidateToken(null));
        Assert.Null(auth.ValidateToken("bogus"));
    }

    [Fact]
    public void ValidateToken_ExpiredSession_ReturnsNull()
    {
        var time = new FakeTimeProvider();
        var auth = new AuthService(new InMemoryUserStore(), new AuthOptions { SessionTtlHours = 1 }, time, NullLogger<AuthService>.Instance);
        auth.Register("alice", "secret1", null, null);
        var token = auth.Login("alice", "secret1").Token;

        Assert.NotNull(auth.ValidateToken(token));

        time.UtcNow = time.UtcNow.AddHours(2); // 超过 1 小时有效期
        Assert.Null(auth.ValidateToken(token));
    }

    [Fact]
    public void ValidateToken_SlidingExpiry_KeepsSessionAlive()
    {
        var time = new FakeTimeProvider();
        var auth = new AuthService(new InMemoryUserStore(), new AuthOptions { SessionTtlHours = 1 }, time, NullLogger<AuthService>.Instance);
        auth.Register("alice", "secret1", null, null);
        var token = auth.Login("alice", "secret1").Token;

        // 55 分钟后仍在有效期，且每次校验滑动续期
        for (var i = 0; i < 10; i++)
        {
            time.UtcNow = time.UtcNow.AddMinutes(55);
            Assert.NotNull(auth.ValidateToken(token));
        }
    }

    [Fact]
    public void ChangePassword_WrongOldPassword_Throws()
    {
        var auth = CreateAuth();
        var user = auth.Register("alice", "secret1", null, null);

        var ex = Assert.Throws<AguiProtocolException>(() => auth.ChangePassword(user.UserId, "wrong-old", "newpass1"));
        Assert.Equal(ErrorCodes.UserPasswordInvalid, ex.ErrorCode);
    }

    [Fact]
    public void ChangePassword_InvalidatesOldSessions_AndNewPasswordWorks()
    {
        var auth = CreateAuth();
        var user = auth.Register("alice", "secret1", null, null);
        var token1 = auth.Login("alice", "secret1").Token;

        auth.ChangePassword(user.UserId, "secret1", "newpass1");

        // 旧令牌全部吊销
        Assert.Null(auth.ValidateToken(token1));
        // 旧密码不可登录，新密码可登录
        Assert.Throws<AguiProtocolException>(() => auth.Login("alice", "secret1"));
        Assert.NotNull(auth.Login("alice", "newpass1").Token);
    }

    [Fact]
    public void UpdateProfile_ChangesNicknameAndAvatar()
    {
        var auth = CreateAuth();
        var user = auth.Register("alice", "secret1", "旧昵称", null);

        var updated = auth.UpdateProfile(user.UserId, "新昵称", "avatar_url");
        Assert.Equal("新昵称", updated.Nickname);
        Assert.Equal("avatar_url", updated.Avatar);
        Assert.True(updated.UpdatedAt >= updated.CreatedAt);
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}

public sealed class UserDisplayNameTests
{
    private static UserAccount Account(string userId, string username, string? nickname)
        => new()
        {
            UserId = userId,
            Username = username,
            Nickname = nickname ?? "",
            PasswordHash = "h",
            PasswordSalt = "s",
            CreatedAt = 0,
        };

    [Fact]
    public async Task CreateGroup_RegisteredOwner_ShowsAccountNickname()
    {
        var f = new HubFixture();
        f.Users.AddUser(Account("user_1", "zhangsan", "张三"));

        var group = await HubFixture.CreateGroupAsync(f.Hub, "群", "user_1", "user_2");

        Assert.Equal("张三", f.Store.GetMember(group.GroupId, "user_1")!.Nickname);
        Assert.Equal("user_2", f.Store.GetMember(group.GroupId, "user_2")!.Nickname); // 未注册用户显示用户 ID
    }

    [Fact]
    public async Task CreateGroup_RegisteredOwner_WithoutNickname_FallsBackToUsername()
    {
        var f = new HubFixture();
        f.Users.AddUser(Account("user_1", "zhangsan", null));

        var group = await HubFixture.CreateGroupAsync(f.Hub, "群", "user_1");

        Assert.Equal("zhangsan", f.Store.GetMember(group.GroupId, "user_1")!.Nickname);
    }

    [Fact]
    public async Task AddMembers_RegisteredUser_GetsAccountDisplayName()
    {
        var f = new HubFixture();
        f.Users.AddUser(Account("user_2", "lisi", "李四"));
        var group = await HubFixture.CreateGroupAsync(f.Hub, "群", "user_1");

        await f.Hub.AddMembersAsync(new GroupMemberAddRequest
        {
            GroupId = group.GroupId,
            MemberIds = ["user_2"],
            OperatorId = "user_1",
        });

        Assert.Equal("李四", f.Store.GetMember(group.GroupId, "user_2")!.Nickname);
    }

    [Fact]
    public async Task SyncUserDisplayName_UpdatesAllGroupsAndBroadcasts()
    {
        var f = new HubFixture();
        f.Users.AddUser(Account("user_1", "zhangsan", "旧名"));
        var group1 = await HubFixture.CreateGroupAsync(f.Hub, "群A", "user_1", "user_2");
        var group2 = await HubFixture.CreateGroupAsync(f.Hub, "群B", "user_1");

        // 订阅群A的成员连接
        var (conn, inbox) = f.NewConnection("user_2");
        await f.Hub.SubscribeAsync(conn, [group1.GroupId]);
        f.Drain(inbox); // 清空握手 / ACK / 快照

        // 账号昵称变更后同步
        f.Users.GetUserById("user_1")!.Nickname = "新名";
        await f.Hub.SyncUserDisplayNameAsync("user_1");

        Assert.Equal("新名", f.Store.GetMember(group1.GroupId, "user_1")!.Nickname);
        Assert.Equal("新名", f.Store.GetMember(group2.GroupId, "user_1")!.Nickname);

        var types = HubFixture.TypesOf(f.Drain(inbox));
        Assert.Contains("GROUP_MEMBER_UPDATED", types);
    }

    [Fact]
    public async Task CreateGroup_RegisteredUser_AvatarFallsBackFromAccount()
    {
        var f = new HubFixture();
        var account = Account("user_1", "zhangsan", "张三");
        account.Avatar = "/ag-ui/files/att_x/me.png";
        f.Users.AddUser(account);

        // 未显式携带头像 → 群成员头像回退到账号头像
        var group = await HubFixture.CreateGroupAsync(f.Hub, "群", "user_1");
        Assert.Equal("/ag-ui/files/att_x/me.png", f.Store.GetMember(group.GroupId, "user_1")!.Avatar);
    }

    [Fact]
    public async Task SyncUserAvatar_UpdatesAllGroupsAndBroadcasts()
    {
        var f = new HubFixture();
        f.Users.AddUser(Account("user_1", "zhangsan", "旧名"));
        var group1 = await HubFixture.CreateGroupAsync(f.Hub, "群A", "user_1", "user_2");
        var group2 = await HubFixture.CreateGroupAsync(f.Hub, "群B", "user_1");

        var (conn, inbox) = f.NewConnection("user_2");
        await f.Hub.SubscribeAsync(conn, [group1.GroupId]);
        f.Drain(inbox);

        // 账号头像变更后同步到所有群成员，并广播 GROUP_MEMBER_UPDATED
        f.Users.GetUserById("user_1")!.Avatar = "/ag-ui/files/att_x/me.png";
        await f.Hub.SyncUserAvatarAsync("user_1");

        Assert.Equal("/ag-ui/files/att_x/me.png", f.Store.GetMember(group1.GroupId, "user_1")!.Avatar);
        Assert.Equal("/ag-ui/files/att_x/me.png", f.Store.GetMember(group2.GroupId, "user_1")!.Avatar);

        var types = HubFixture.TypesOf(f.Drain(inbox));
        Assert.Contains("GROUP_MEMBER_UPDATED", types);
    }
}

public sealed class UserApiIntegrationTests : IClassFixture<HubServerFixture>
{
    private readonly HubServerFixture _fixture;
    private readonly HttpClient _client;

    public UserApiIntegrationTests(HubServerFixture fixture)
    {
        _fixture = fixture;
        _client = new HttpClient { BaseAddress = new Uri(fixture.HttpBase) };
    }

    // ================= 账号生命周期 =================

    [Fact]
    public async Task Register_Login_Me_ChangePassword_FullHttpFlow()
    {
        // 注册即返回令牌
        var register = await _client.PostAsJsonAsync("/ag-ui/user/register", new
        {
            username = "carol",
            password = "secret1",
            nickname = "小C",
        });
        register.EnsureSuccessStatusCode();
        var auth = await register.Content.ReadFromJsonAsync<JsonElement>();
        var userId = auth.GetProperty("userId").GetString()!;
        var token = auth.GetProperty("token").GetString()!;
        Assert.StartsWith("user_", userId);
        Assert.Equal("小C", auth.GetProperty("nickname").GetString());

        // me（Bearer 令牌）
        using var meReq = new HttpRequestMessage(HttpMethod.Get, "/ag-ui/user/me");
        meReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var me = await _client.SendAsync(meReq);
        me.EnsureSuccessStatusCode();
        var meJson = await me.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("carol", meJson.GetProperty("username").GetString());

        // 未带令牌 → 401
        var anonymous = await _client.GetAsync("/ag-ui/user/me");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        // 重复注册 → 409
        var duplicate = await _client.PostAsJsonAsync("/ag-ui/user/register", new { username = "carol", password = "secret1" });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal(ErrorCodes.UserExists, (await duplicate.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        // 登录成功 / 错误密码 401
        var badLogin = await _client.PostAsJsonAsync("/ag-ui/user/login", new { username = "carol", password = "nope-nope" });
        Assert.Equal(HttpStatusCode.Unauthorized, badLogin.StatusCode);
        var login = await _client.PostAsJsonAsync("/ag-ui/user/login", new { username = "carol", password = "secret1" });
        login.EnsureSuccessStatusCode();
        var loginJson = await login.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(userId, loginJson.GetProperty("userId").GetString());
        var token2 = loginJson.GetProperty("token").GetString()!;

        // 修改密码（旧令牌被吊销 → 再访问 me 401）
        using var pwReq = new HttpRequestMessage(HttpMethod.Post, "/ag-ui/user/password")
        {
            Content = JsonContent.Create(new { oldPassword = "secret1", newPassword = "newpass1" }),
        };
        pwReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token2);
        (await _client.SendAsync(pwReq)).EnsureSuccessStatusCode();

        using var staleMe = new HttpRequestMessage(HttpMethod.Get, "/ag-ui/user/me");
        staleMe.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token2);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.SendAsync(staleMe)).StatusCode);

        // 新密码可登录，旧密码不可
        var oldPwLogin = await _client.PostAsJsonAsync("/ag-ui/user/login", new { username = "carol", password = "secret1" });
        Assert.Equal(HttpStatusCode.Unauthorized, oldPwLogin.StatusCode);
        var newLogin = await _client.PostAsJsonAsync("/ag-ui/user/login", new { username = "carol", password = "newpass1" });
        newLogin.EnsureSuccessStatusCode();

        // 修改资料
        var newToken = (await newLogin.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
        using var profileReq = new HttpRequestMessage(HttpMethod.Put, "/ag-ui/user/profile")
        {
            Content = JsonContent.Create(new { nickname = "Carol新", avatar = "a1" }),
        };
        profileReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", newToken);
        var profile = await _client.SendAsync(profileReq);
        profile.EnsureSuccessStatusCode();
        Assert.Equal("Carol新", (await profile.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("nickname").GetString());

        // 登出后令牌失效
        using var logoutReq = new HttpRequestMessage(HttpMethod.Post, "/ag-ui/user/logout");
        logoutReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", newToken);
        (await _client.SendAsync(logoutReq)).EnsureSuccessStatusCode();
        using var afterLogout = new HttpRequestMessage(HttpMethod.Get, "/ag-ui/user/me");
        afterLogout.Headers.Authorization = new AuthenticationHeaderValue("Bearer", newToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.SendAsync(afterLogout)).StatusCode);
    }

    [Fact]
    public async Task SearchMessages_ReturnsHits_OnlyForMembers()
    {
        var auth = await RegisterAsync("search_user");
        var userId = auth.GetProperty("userId").GetString()!;
        var token = auth.GetProperty("token").GetString()!;

        // 建群（成员 = 自己）
        var create = await _client.PostAsJsonAsync("/ag-ui/group/create", new { groupName = "搜索群", ownerId = userId, memberIds = new[] { userId } });
        create.EnsureSuccessStatusCode();
        var groupId = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("groupId").GetString()!;

        // 发两条消息（回退模式：请求体 userId 即身份）
        await _client.PostAsJsonAsync("/ag-ui/group/message/send", new { groupId, userId, content = "今天天气很好，适合爬山", timestamp = 1000L });
        await _client.PostAsJsonAsync("/ag-ui/group/message/send", new { groupId, userId, content = "报销需要开发票", timestamp = 2000L });

        // 关键词命中（Bearer 令牌访问）
        using var req = new HttpRequestMessage(HttpMethod.Get, $"/ag-ui/group/{groupId}/messages/search?q={Uri.EscapeDataString("发票")}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var res = await _client.SendAsync(req);
        res.EnsureSuccessStatusCode();
        var hits = (await res.Content.ReadFromJsonAsync<JsonElement[]>() ?? []);
        Assert.Single(hits);
        Assert.Equal("报销需要开发票", hits[0].GetProperty("content").GetString());

        // 无关键词 → 400
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.GetAsync($"/ag-ui/group/{groupId}/messages/search")).StatusCode);

        // 非成员 → 403（防私密群内容泄露）
        var outsider = await RegisterAsync("outsider_user");
        using var req2 = new HttpRequestMessage(HttpMethod.Get, $"/ag-ui/group/{groupId}/messages/search?q={Uri.EscapeDataString("发票")}");
        req2.Headers.Authorization = new AuthenticationHeaderValue("Bearer", outsider.GetProperty("token").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(req2)).StatusCode);

        // 匿名 → 401
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync($"/ag-ui/group/{groupId}/messages/search?q=发票")).StatusCode);
    }

    [Fact]
    public async Task UserDirectory_ListsRegisteredUsers()
    {
        // 目录需登录可见（防未授权枚举用户）：先注册拿令牌，带 Bearer 访问
        var register = await _client.PostAsJsonAsync("/ag-ui/user/register", new { username = "directory_user", password = "secret1", nickname = "目录用户" });
        register.EnsureSuccessStatusCode();
        var token = (await register.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;

        using var req = new HttpRequestMessage(HttpMethod.Get, "/ag-ui/users");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var res = await _client.SendAsync(req);
        res.EnsureSuccessStatusCode();
        var users = (await res.Content.ReadFromJsonAsync<JsonElement[]>() ?? []);
        Assert.Contains(users, u => u.GetProperty("username").GetString() == "directory_user");

        // 匿名访问被拒（防未授权枚举用户 / 泄露管理员标记）
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync("/ag-ui/users")).StatusCode);

        // 目录 DTO 不暴露 isAdmin（管理员标记仅 /me 与登录响应返回）
        foreach (var u in users)
            Assert.False(u.TryGetProperty("isAdmin", out _), "目录不应暴露 isAdmin");
    }

    // ================= WS / SSE 鉴权 =================

    [Fact]
    public async Task WebSocket_WithValidToken_IdentityOverridesQueryParam()
    {
        var auth = await RegisterAsync("ws_user");
        var userId = auth.GetProperty("userId").GetString()!;
        var token = auth.GetProperty("token").GetString()!;

        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri($"{_fixture.WsBase}/ws?memberId=evil&token={token}"), CancellationToken.None);
        var handshake = await NextEventAsync(ws);
        Assert.Equal("GROUP_CONNECTED", handshake.GetProperty("type").GetString());
        // 令牌身份覆盖 memberId 查询参数（防伪造）
        Assert.Equal(userId, handshake.GetProperty("memberId").GetString());
    }

    [Fact]
    public async Task WebSocket_WithInvalidToken_Returns401()
        => Assert.Equal(HttpStatusCode.Unauthorized, await UpgradeStatusCodeAsync("/ws?memberId=x&token=bogus"));

    [Fact]
    public async Task WebSocket_WithoutToken_StillConnectsLegacyMode()
    {
        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri($"{_fixture.WsBase}/ws?memberId=legacy_1"), CancellationToken.None);
        var handshake = await NextEventAsync(ws);
        Assert.Equal("GROUP_CONNECTED", handshake.GetProperty("type").GetString());
        Assert.Equal("legacy_1", handshake.GetProperty("memberId").GetString());
    }

    [Fact]
    public async Task Sse_WithInvalidToken_Returns401()
    {
        var resp = await _client.GetAsync("/sse?memberId=x&token=bogus");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Sse_WithValidToken_ConnectsAsUser()
    {
        var auth = await RegisterAsync("sse_user");
        var token = auth.GetProperty("token").GetString()!;

        // SSE 为长连接：仅读取响应头即关闭，验证鉴权通过即可
        using var resp = await _client.GetAsync($"/sse?memberId=evil&token={token}", HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();
        Assert.Equal("text/event-stream", resp.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task RequireTokenOnRealTime_RejectsTokenlessConnections()
    {
        // 单独起一个强制 token 的服务实例
        var builder = HubApp.CreateBuilder([]);
        builder.Environment.EnvironmentName = "Testing";
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["GroupChat:SeedSampleData"] = "false",
            ["Auth:RequireTokenOnRealTime"] = "true",
            ["Persistence:Enabled"] = "false",
        });
        HubApp.ConfigureServices(builder);
        await using var app = builder.Build();
        HubApp.MapEndpoints(app);
        await app.StartAsync();
        var httpBase = app.Urls.First();
        using var client = new HttpClient { BaseAddress = new Uri(httpBase) };

        var resp = await client.GetAsync("/sse?memberId=x");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    // ================= 辅助 =================

    private async Task<JsonElement> RegisterAsync(string username)
    {
        var res = await _client.PostAsJsonAsync("/ag-ui/user/register", new { username, password = "secret1", nickname = username });
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>以 HTTP Upgrade 请求探测 WS 端点返回码（服务端拒绝时不会完成 WebSocket 握手）。</summary>
    private async Task<HttpStatusCode> UpgradeStatusCodeAsync(string path)
    {
        using var client = new HttpClient { BaseAddress = new Uri(_fixture.HttpBase) };
        using var req = new HttpRequestMessage(HttpMethod.Get, path)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
        req.Headers.Connection.Add("Upgrade");
        req.Headers.Upgrade.Add(new ProductHeaderValue("websocket"));
        req.Headers.TryAddWithoutValidation("Sec-WebSocket-Key", Convert.ToBase64String(new byte[16]));
        req.Headers.TryAddWithoutValidation("Sec-WebSocket-Version", "13");
        using var resp = await client.SendAsync(req);
        return resp.StatusCode;
    }

    private static async Task<JsonElement> NextEventAsync(ClientWebSocket ws, int timeoutMs = 15000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        var buffer = new byte[64 * 1024];
        var result = await ws.ReceiveAsync(buffer, cts.Token);
        if (result.MessageType == WebSocketMessageType.Close)
            throw new InvalidOperationException("WebSocket 提前关闭");
        var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
