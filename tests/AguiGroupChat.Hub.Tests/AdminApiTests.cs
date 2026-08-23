using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AguiGroupChat.Agents;
using AguiGroupChat.Hub;
using AguiGroupChat.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json.Serialization;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>自托管 Kestrel 集成测试夹具：Hub + 智能体网关（mock）+ 管理员控制台 API。</summary>
public sealed class AdminApiServerFixture : IAsyncLifetime
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
            ["Agents:Provider"] = "mock",
            ["Persistence:Enabled"] = "false",
            ["Auth:RequireTokenOnRealTime"] = "false",
            // 固定管理员账号（首个注册用户在其他测试先注册时不一定是管理员）
            ["Auth:AdminUserIds"] = "admin_chief",
        });
        HubApp.ConfigureServices(builder);
        builder.Services.AddAgentFramework(builder.Configuration);
        builder.Services.AddSingleton(new ConfigGovernanceState()); // 配置治理（6.3）
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));

        App = builder.Build();
        HubApp.MapEndpoints(App);
        App.MapAdminApi();
        App.MapConfigGovernanceApi();
        await App.StartAsync();
        HttpBase = App.Urls.First();
    }

    public async Task DisposeAsync()
    {
        if (App is not null) await App.DisposeAsync();
    }
}

public sealed class AdminApiIntegrationTests : IClassFixture<AdminApiServerFixture>
{
    private readonly AdminApiServerFixture _fixture;
    private readonly HttpClient _client;

    public AdminApiIntegrationTests(AdminApiServerFixture fixture)
    {
        _fixture = fixture;
        _client = new HttpClient { BaseAddress = new Uri(fixture.HttpBase) };
    }

    private async Task<JsonElement> RegisterAsync(string username, string password = "secret1")
    {
        var res = await _client.PostAsJsonAsync("/ag-ui/user/register", new { username, password, nickname = username });
        if (res.StatusCode == HttpStatusCode.Conflict)
        {
            // 共享 fixture：同名用户可能已被其他测试注册 → 回退登录拿令牌
            var login = await _client.PostAsJsonAsync("/ag-ui/user/login", new { username, password });
            login.EnsureSuccessStatusCode();
            return await login.Content.ReadFromJsonAsync<JsonElement>();
        }
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static HttpRequestMessage Authed(HttpMethod method, string path, string token)
    {
        var req = new HttpRequestMessage(method, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return req;
    }

    [Fact]
    public async Task AdminUsers_ListDisableEnableResetPassword()
    {
        // 管理员（配置名单）与目标用户
        var admin = await RegisterAsync("admin_chief");
        var adminToken = admin.GetProperty("token").GetString()!;
        var target = await RegisterAsync("target_user");
        var targetId = target.GetProperty("userId").GetString()!;
        var targetToken = target.GetProperty("token").GetString()!;

        // 用户列表（管理员）包含禁用状态
        using var listReq = Authed(HttpMethod.Get, "/ag-ui/admin/users", adminToken);
        var list = (await (await _client.SendAsync(listReq)).Content.ReadFromJsonAsync<JsonElement[]>() ?? []);
        var targetEntry = Assert.Single(list, u => u.GetProperty("username").GetString() == "target_user");
        Assert.False(targetEntry.GetProperty("isDisabled").GetBoolean());

        // 非管理员访问 → 403
        var outsider = await RegisterAsync("plain_user");
        var outsiderToken = outsider.GetProperty("token").GetString()!;
        using var denied = Authed(HttpMethod.Get, "/ag-ui/admin/users", outsiderToken);
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(denied)).StatusCode);

        // 禁用目标：旧令牌立即失效 + 无法再登录
        using var disableReq = Authed(HttpMethod.Post, $"/ag-ui/admin/users/{targetId}/disabled", adminToken);
        disableReq.Content = JsonContent.Create(new { disabled = true });
        (await _client.SendAsync(disableReq)).EnsureSuccessStatusCode();
        using var staleMe = Authed(HttpMethod.Get, "/ag-ui/user/me", targetToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.SendAsync(staleMe)).StatusCode);
        var deniedLogin = await _client.PostAsJsonAsync("/ag-ui/user/login", new { username = "target_user", password = "secret1" });
        Assert.Equal(HttpStatusCode.Unauthorized, deniedLogin.StatusCode);

        // 管理员不能禁用自己
        using var selfDisable = Authed(HttpMethod.Post, $"/ag-ui/admin/users/{admin.GetProperty("userId").GetString()}/disabled", adminToken);
        selfDisable.Content = JsonContent.Create(new { disabled = true });
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.SendAsync(selfDisable)).StatusCode);

        // 启用：可再次登录
        using var enableReq = Authed(HttpMethod.Post, $"/ag-ui/admin/users/{targetId}/disabled", adminToken);
        enableReq.Content = JsonContent.Create(new { disabled = false });
        (await _client.SendAsync(enableReq)).EnsureSuccessStatusCode();
        var relogin = await _client.PostAsJsonAsync("/ag-ui/user/login", new { username = "target_user", password = "secret1" });
        relogin.EnsureSuccessStatusCode();

        // 重置密码：旧密码失效、新密码可登录
        using var pwReq = Authed(HttpMethod.Post, $"/ag-ui/admin/users/{targetId}/password", adminToken);
        pwReq.Content = JsonContent.Create(new { newPassword = "brand-new-pass" });
        (await _client.SendAsync(pwReq)).EnsureSuccessStatusCode();
        var oldPw = await _client.PostAsJsonAsync("/ag-ui/user/login", new { username = "target_user", password = "secret1" });
        Assert.Equal(HttpStatusCode.Unauthorized, oldPw.StatusCode);
        var newPw = await _client.PostAsJsonAsync("/ag-ui/user/login", new { username = "target_user", password = "brand-new-pass" });
        newPw.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task AdminUsage_ReturnsDailySummary()
    {
        var admin = await RegisterAsync("admin_chief");
        var token = admin.GetProperty("token").GetString()!;
        // 直接记录几条用量（真实调用由 AgentGateway 在流式结束时写入）
        var usage = _fixture.App.Services.GetRequiredService<AguiGroupChat.Hub.Agents.AgentUsageService>();
        usage.RecordUsage("agent_x", "user_a", 100, 50, 10);
        usage.RecordUsage("agent_y", "user_a", 20, 30, 0);

        using var req = Authed(HttpMethod.Get, "/ag-ui/admin/usage?days=7", token);
        var res = await _client.SendAsync(req);
        res.EnsureSuccessStatusCode();
        var d = await res.Content.ReadFromJsonAsync<JsonElement>();
        var days = d.GetProperty("days").EnumerateArray().ToList();
        var today = Assert.Single(days, x => x.GetProperty("date").GetString() == AguiGroupChat.Hub.Agents.AgentUsageService.Today());
        Assert.Equal(210, today.GetProperty("totalTokens").GetInt64());
        Assert.Equal(2, today.GetProperty("calls").GetInt64());

        // 非管理员 → 403
        var outsider = await RegisterAsync("usage_outsider");
        using var denied = Authed(HttpMethod.Get, "/ag-ui/admin/usage", outsider.GetProperty("token").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(denied)).StatusCode);
    }

    [Fact]
    public async Task AdminStatus_ReturnsCounters()
    {
        var admin = await RegisterAsync("admin_chief");
        var token = admin.GetProperty("token").GetString()!;
        using var req = Authed(HttpMethod.Get, "/ag-ui/admin/status", token);
        var res = await _client.SendAsync(req);
        res.EnsureSuccessStatusCode();
        var d = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ok", d.GetProperty("status").GetString());
        Assert.True(d.GetProperty("users").GetInt32() >= 1);
        Assert.True(d.TryGetProperty("connections", out _));
        Assert.True(d.TryGetProperty("messages", out _));
        Assert.True(d.TryGetProperty("dotnetVersion", out _));
    }

    [Fact]
    public async Task AdminConfig_ReturnsReadOnlySnapshot_OnlyForAdmin()
    {
        var admin = await RegisterAsync("admin_chief");
        var token = admin.GetProperty("token").GetString()!;

        using var req = Authed(HttpMethod.Get, "/ag-ui/admin/config", token);
        var res = await _client.SendAsync(req);
        res.EnsureSuccessStatusCode();
        var d = await res.Content.ReadFromJsonAsync<JsonElement>();
        // 关键运维配置均可见
        Assert.True(d.TryGetProperty("auth", out var auth) && auth.TryGetProperty("requireTokenOnRealTime", out _));
        Assert.Equal("memory", d.GetProperty("storage").GetProperty("provider").GetString());
        Assert.True(d.TryGetProperty("agents", out var agents) && agents.TryGetProperty("provider", out _));
        Assert.True(d.TryGetProperty("agents", out var ag) && ag.TryGetProperty("memory", out _));
        Assert.True(d.GetProperty("auth").GetProperty("hasAdminUserIds").GetBoolean());

        // 非管理员 → 403
        var outsider = await RegisterAsync("config_outsider");
        using var denied = Authed(HttpMethod.Get, "/ag-ui/admin/config", outsider.GetProperty("token").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(denied)).StatusCode);
    }

    [Fact]
    public async Task MessagesAround_ReturnsTargetWithContext_OnlyForMembers()
    {
        var user = await RegisterAsync("around_user");
        var token = user.GetProperty("token").GetString()!;
        var userId = user.GetProperty("userId").GetString()!;

        // 建群 + 发三条消息
        var create = await _client.PostAsJsonAsync("/ag-ui/group/create", new { groupName = "定位群", ownerId = userId, memberIds = new[] { userId } });
        create.EnsureSuccessStatusCode();
        var groupId = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("groupId").GetString()!;
        await _client.PostAsJsonAsync("/ag-ui/group/message/send", new { groupId, userId, content = "第一条", timestamp = 1000L });
        var targetId = (await _client.PostAsJsonAsync("/ag-ui/group/message/send", new { groupId, userId, content = "目标消息", timestamp = 2000L }))
            .Content.ReadFromJsonAsync<JsonElement>().Result.GetProperty("messageId").GetString()!;
        await _client.PostAsJsonAsync("/ag-ui/group/message/send", new { groupId, userId, content = "第三条", timestamp = 3000L });

        // around：目标消息前后各 1 条 → 返回 3 条（含目标），按时间序
        using var around = Authed(HttpMethod.Get, $"/ag-ui/group/{groupId}/messages/around?messageId={Uri.EscapeDataString(targetId)}&count=4", token);
        var res = await _client.SendAsync(around);
        res.EnsureSuccessStatusCode();
        var msgs = (await res.Content.ReadFromJsonAsync<JsonElement[]>() ?? []);
        Assert.Equal(3, msgs.Length);
        Assert.Equal(targetId, msgs[1].GetProperty("messageId").GetString());

        // 消息不存在 → 404
        using var missing = Authed(HttpMethod.Get, $"/ag-ui/group/{groupId}/messages/around?messageId=nope", token);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.SendAsync(missing)).StatusCode);

        // 非成员 → 403
        var outsider = await RegisterAsync("around_outsider");
        using var denied = Authed(HttpMethod.Get, $"/ag-ui/group/{groupId}/messages/around?messageId={Uri.EscapeDataString(targetId)}", outsider.GetProperty("token").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(denied)).StatusCode);
    }

    [Fact]
    public async Task TopicsRelated_ReturnsRelatedBySharedKeywords_OnlyForMembers()
    {
        var user = await RegisterAsync("related_user");
        var token = user.GetProperty("token").GetString()!;
        var userId = user.GetProperty("userId").GetString()!;

        var create = await _client.PostAsJsonAsync("/ag-ui/group/create", new { groupName = "关联群", ownerId = userId, memberIds = new[] { userId } });
        create.EnsureSuccessStatusCode();
        var groupId = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("groupId").GetString()!;

        // 主话题（main）与新建话题 t2 都讨论「数据库选型」，含共享关键词
        await _client.PostAsJsonAsync("/ag-ui/group/message/send", new { groupId, userId, topicId = "main", content = "讨论数据库选型，考虑 Postgres 与 MySQL" });
        var tRes = await _client.PostAsJsonAsync("/ag-ui/group/topic/create", new { groupId, operatorId = userId, name = "存储" });
        tRes.EnsureSuccessStatusCode();
        var t2Id = (await tRes.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("topicId").GetString()!;
        await _client.PostAsJsonAsync("/ag-ui/group/message/send", new { groupId, userId, topicId = t2Id, content = "数据库选型：Postgres 更稳定，缓存用 Redis" });
        await _client.PostAsJsonAsync("/ag-ui/group/message/send", new { groupId, userId, topicId = "main", content = "今天天气不错" });

        using var req = Authed(HttpMethod.Get, $"/ag-ui/group/{groupId}/topics/related?topicId={Uri.EscapeDataString("main")}", token);
        var res = await _client.SendAsync(req);
        res.EnsureSuccessStatusCode();
        var d = await res.Content.ReadFromJsonAsync<JsonElement>();
        var related = d.GetProperty("related");
        Assert.True(related.GetArrayLength() >= 1, "应返回至少一个相关话题");
        Assert.Contains(related.EnumerateArray(), r => r.GetProperty("topicId").GetString() == t2Id);

        // 非成员 → 403
        var outsider = await RegisterAsync("related_outsider");
        using var denied = Authed(HttpMethod.Get, $"/ag-ui/group/{groupId}/topics/related?topicId=main", outsider.GetProperty("token").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(denied)).StatusCode);
    }

    [Fact]
    public async Task ConfigGovernance_Post_AdminUpdatesAndPersists()
    {
        var admin = await RegisterAsync("admin_chief"); // 配置名单管理员
        var token = admin.GetProperty("token").GetString()!;

        using var set = Authed(HttpMethod.Post, "/ag-ui/admin/config", token);
        set.Content = JsonContent.Create(new
        {
            sessionTtlHours = 48,
            messageHistoryLimit = 2000,
            maxGroupMembers = 300,
            enableWebTools = true,
            workToolsEnabled = true,
            requireApprovalToolNames = new[] { "publish_announcement", "deploy" },
            allowedFrameOrigins = new[] { "https://portal.example.com" },
        });
        var setRes = await _client.SendAsync(set);
        setRes.EnsureSuccessStatusCode();

        using var get = Authed(HttpMethod.Get, "/ag-ui/admin/config/governance", token);
        var d = await (await _client.SendAsync(get)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(48, d.GetProperty("sessionTtlHours").GetInt32());
        Assert.Equal(2000, d.GetProperty("messageHistoryLimit").GetInt32());
        Assert.Equal(300, d.GetProperty("maxGroupMembers").GetInt32());
        Assert.Contains(d.GetProperty("requireApprovalToolNames").EnumerateArray(), x => x.GetString() == "deploy");
        Assert.Contains(d.GetProperty("allowedFrameOrigins").EnumerateArray(), x => x.GetString() == "https://portal.example.com");
    }

    [Fact]
    public async Task ConfigGovernance_Post_RejectsInvalidAndForbidsNonAdmin()
    {
        var admin = await RegisterAsync("admin_chief");
        var token = admin.GetProperty("token").GetString()!;

        // 非法边界 → 400
        using var bad = Authed(HttpMethod.Post, "/ag-ui/admin/config", token);
        bad.Content = JsonContent.Create(new { messageHistoryLimit = -1 });
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.SendAsync(bad)).StatusCode);

        // 非管理员 → 403
        var normal = await RegisterAsync("cfg_normal_admin");
        using var denied = Authed(HttpMethod.Post, "/ag-ui/admin/config", normal.GetProperty("token").GetString());
        denied.Content = JsonContent.Create(new { sessionTtlHours = 24 });
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(denied)).StatusCode);
    }
}
