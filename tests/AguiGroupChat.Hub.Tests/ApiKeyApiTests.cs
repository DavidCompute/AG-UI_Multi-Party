using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AguiGroupChat.Agents;
using AguiGroupChat.Hub;
using AguiGroupChat.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>
/// 托管 API 密钥管理（<c>/ag-ui/admin/api-keys</c>）集成测试：创建返回一次性密钥 → 用该密钥调用
/// 对外接口（<c>/agui/v1/me</c>）成功 → 吊销后失效；仅管理员；绑定账号校验；列表脱敏。
/// </summary>
public sealed class ApiKeyApiServerFixture : IAsyncLifetime
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
            ["Auth:FirstUserIsAdmin"] = "false", // 不自动自举：管理员只由名单决定
            ["Auth:AdminUserIds"] = "ak_admin",
        });
        HubApp.ConfigureServices(builder);
        builder.Services.AddAgentFramework(builder.Configuration);
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));

        App = builder.Build();
        HubApp.MapEndpoints(App);
        App.MapApiKeyApi();
        App.MapExternalApi();
        await App.StartAsync();
        HttpBase = App.Urls.First();
    }

    public async Task DisposeAsync() => await App.DisposeAsync();
}

public sealed class ApiKeyApiTests : IClassFixture<ApiKeyApiServerFixture>
{
    private readonly ApiKeyApiServerFixture _fx;
    private readonly HttpClient _client;

    public ApiKeyApiTests(ApiKeyApiServerFixture fx)
    {
        _fx = fx;
        _client = new HttpClient { BaseAddress = new Uri(fx.HttpBase) };
    }

    private static HttpRequestMessage Authed(HttpMethod method, string path, string token)
    {
        var req = new HttpRequestMessage(method, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return req;
    }

    private async Task<string> TokenAsync(string username)
    {
        var res = await _client.PostAsJsonAsync("/ag-ui/user/register", new { username, password = "secret1", nickname = username });
        if (!res.IsSuccessStatusCode)
            res = await _client.PostAsJsonAsync("/ag-ui/user/login", new { username, password = "secret1" });
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
    }

    [Fact]
    public async Task CreateUseRevoke_EndToEnd()
    {
        var admin = await TokenAsync("ak_admin");

        using var create = Authed(HttpMethod.Post, "/ag-ui/admin/api-keys", admin);
        create.Content = JsonContent.Create(new { name = "e2e", username = "ak_admin" });
        var created = await (await _client.SendAsync(create)).Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetString()!;
        var secret = created.GetProperty("secret").GetString()!;
        Assert.StartsWith("ak_", secret);

        // 用密钥调对外接口：以绑定账号身份通过
        using var me = Authed(HttpMethod.Get, "/agui/v1/me", secret);
        var meRes = await _client.SendAsync(me);
        Assert.Equal(HttpStatusCode.OK, meRes.StatusCode);
        Assert.Equal("ak_admin", (await meRes.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("username").GetString());

        // 列表脱敏：回前缀，不回密钥 / 哈希
        using var list = Authed(HttpMethod.Get, "/ag-ui/admin/api-keys", admin);
        var arr = await (await _client.SendAsync(list)).Content.ReadFromJsonAsync<JsonElement>();
        var row = arr.EnumerateArray().First(k => k.GetProperty("id").GetString() == id);
        Assert.False(string.IsNullOrEmpty(row.GetProperty("keyPrefix").GetString()));
        Assert.False(row.TryGetProperty("secret", out _));
        Assert.False(row.TryGetProperty("keyHash", out _));

        // 吊销后：同一密钥立即失效
        using var del = Authed(HttpMethod.Delete, "/ag-ui/admin/api-keys/" + id, admin);
        Assert.True((await _client.SendAsync(del)).IsSuccessStatusCode);

        using var me2 = Authed(HttpMethod.Get, "/agui/v1/me", secret);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.SendAsync(me2)).StatusCode);
    }

    [Fact]
    public async Task RequiresAdmin()
    {
        var plain = await TokenAsync("ak_plain");
        using var req = Authed(HttpMethod.Post, "/ag-ui/admin/api-keys", plain);
        req.Content = JsonContent.Create(new { name = "x", username = "ak_plain" });
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(req)).StatusCode);
    }

    [Fact]
    public async Task UnknownUsername_IsRejected()
    {
        var admin = await TokenAsync("ak_admin");
        using var req = Authed(HttpMethod.Post, "/ag-ui/admin/api-keys", admin);
        req.Content = JsonContent.Create(new { name = "ghost", username = "no_such_user" });
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.SendAsync(req)).StatusCode);
    }
}
