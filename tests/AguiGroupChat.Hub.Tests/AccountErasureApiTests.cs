using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AguiGroupChat.Agents;
using AguiGroupChat.Hub;
using AguiGroupChat.Hub.Users;
using AguiGroupChat.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json.Serialization;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>账号注销 API + 管理员删除 + 审计导出 CSV 的 Kestrel 集成测试夹具。</summary>
public sealed class AccountApiServerFixture : IAsyncLifetime
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
            ["Auth:AdminUserIds"] = "admin_chief",
        });
        HubApp.ConfigureServices(builder);
        builder.Services.AddAgentFramework(builder.Configuration);
        builder.Services.AddSingleton<AccountErasureService>(); // 账号注销 / 数据擦除编排（与 Program.cs 一致）
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));

        App = builder.Build();
        HubApp.MapEndpoints(App);
        App.MapAdminApi();
        App.MapAccountApi();
        await App.StartAsync();
        HttpBase = App.Urls.First();

        // 先注册一名永久超级管理员：其后测试注册的账号均为普通用户，避免「首个注册自动超管」造成用例互相影响
        using var root = new HttpClient { BaseAddress = new Uri(HttpBase) };
        using (var req = await root.PostAsJsonAsync("/ag-ui/user/register",
                   new { username = "platform_root", password = "secret1", nickname = "平台根管理员" }))
        {
            req.EnsureSuccessStatusCode();
        }
    }

    public async Task DisposeAsync()
    {
        if (App is not null) await App.DisposeAsync();
    }
}

public sealed class AccountErasureApiTests : IClassFixture<AccountApiServerFixture>
{
    private readonly AccountApiServerFixture _fixture;
    private readonly HttpClient _client;

    public AccountErasureApiTests(AccountApiServerFixture fixture)
    {
        _fixture = fixture;
        _client = new HttpClient { BaseAddress = new Uri(fixture.HttpBase) };
    }

    private async Task<JsonElement> RegisterAsync(string username, string password = "secret1")
    {
        var res = await _client.PostAsJsonAsync("/ag-ui/user/register", new { username, password, nickname = username });
        if (res.StatusCode == HttpStatusCode.Conflict)
        {
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

    private async Task<bool> AccountExistsAsync(string token, string userId)
    {
        var me = await _client.SendAsync(Authed(HttpMethod.Get, "/ag-ui/user/me", token));
        if (me.StatusCode == HttpStatusCode.Unauthorized) return false;
        me.EnsureSuccessStatusCode();
        var body = await me.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("userId").GetString() == userId;
    }

    [Fact]
    public async Task SelfServiceDelete_RequiresPassword_ThenRemovesAccountAndSessions()
    {
        var user = await RegisterAsync("solo_user");
        var userId = user.GetProperty("userId").GetString()!;
        var token = user.GetProperty("token").GetString()!;

        // 密码错误 → 401，账号保留
        using var wrongPw = Authed(HttpMethod.Delete, "/ag-ui/account", token);
        wrongPw.Content = JsonContent.Create(new { password = "wrong-pass" });
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.SendAsync(wrongPw)).StatusCode);
        Assert.True(await AccountExistsAsync(token, userId));

        // 密码正确 → 注销成功，账号 / 会话即时失效
        using var del = Authed(HttpMethod.Delete, "/ag-ui/account", token);
        del.Content = JsonContent.Create(new { password = "secret1" });
        var res = await _client.SendAsync(del);
        res.EnsureSuccessStatusCode();
        var report = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(report.GetProperty("accountRemoved").GetBoolean());

        Assert.False(await AccountExistsAsync(token, userId));
        var relogin = await _client.PostAsJsonAsync("/ag-ui/user/login", new { username = "solo_user", password = "secret1" });
        Assert.Equal(HttpStatusCode.Unauthorized, relogin.StatusCode);
    }

    [Fact]
    public async Task AdminDelete_TargetRemoved_AndAuditCsvExported()
    {
        var admin = await RegisterAsync("admin_chief");
        var adminToken = admin.GetProperty("token").GetString()!;
        var victim = await RegisterAsync("doomed_user");
        var victimId = victim.GetProperty("userId").GetString()!;

        // 非管理员删除 → 403
        var outsider = await RegisterAsync("admin_outsider");
        using var denied = Authed(HttpMethod.Delete, $"/ag-ui/admin/users/{victimId}", outsider.GetProperty("token").GetString()!);
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(denied)).StatusCode);

        // 管理员不能经管理接口删除自己
        var adminId = admin.GetProperty("userId").GetString()!;
        using var selfDel = Authed(HttpMethod.Delete, $"/ag-ui/admin/users/{adminId}", adminToken);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.SendAsync(selfDel)).StatusCode);

        // 管理员删除他人 → 账号行消失
        using var del = Authed(HttpMethod.Delete, $"/ag-ui/admin/users/{victimId}?reason=测试", adminToken);
        var res = await _client.SendAsync(del);
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("accountRemoved").GetBoolean());
        Assert.False(await AccountExistsAsync(victim.GetProperty("token").GetString()!, victimId));

        // 审计 CSV：导出应含刚才的删除记录（action=user.account.delete）与表头，且带 UTF-8 BOM
        using var csv = Authed(HttpMethod.Get, $"/ag-ui/admin/audit.csv?action=user.account.delete&targetId={victimId}", adminToken);
        var csvRes = await _client.SendAsync(csv);
        csvRes.EnsureSuccessStatusCode();
        Assert.Equal("text/csv; charset=utf-8", csvRes.Content.Headers.ContentType?.ToString());
        var bytes = await csvRes.Content.ReadAsByteArrayAsync();
        Assert.True(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF); // BOM
        var text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.True(lines.Length >= 2, "至少表头 + 一条记录");
        Assert.StartsWith("id,timeUtc,timeMs,action", lines[0]);
        Assert.Contains("user.account.delete", lines[1]);
        Assert.Contains(victimId, lines[1]); // targetId 列含被删用户的 userId

        // JSON 查询接口同样支持过滤（按目标用户）
        using var json = Authed(HttpMethod.Get, $"/ag-ui/admin/audit?targetId={victimId}&limit=10", adminToken);
        var jsonRes = await _client.SendAsync(json);
        jsonRes.EnsureSuccessStatusCode();
        var jsonBody = await jsonRes.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains(jsonBody.GetProperty("entries").EnumerateArray(),
            e => e.GetProperty("targetId").GetString() == victimId);
    }
}
