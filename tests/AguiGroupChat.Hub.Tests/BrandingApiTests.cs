using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AguiGroupChat.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json.Serialization;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>白标 / 品牌化（6.4）集成测试夹具：Hub + mock 网关 + 登录 + BrandingApi。</summary>
public sealed class BrandingApiServerFixture : IAsyncLifetime
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
            ["Auth:AdminUserIds"] = "brand_admin",
        });
        HubApp.ConfigureServices(builder);
        builder.Services.AddSingleton(new BrandingState());
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));

        App = builder.Build();
        HubApp.MapEndpoints(App);
        App.MapBrandingApi();
        await App.StartAsync();
        HttpBase = App.Urls.First();
    }

    public async Task DisposeAsync()
    {
        if (App is not null) await App.DisposeAsync();
    }
}

public sealed class BrandingApiTests : IClassFixture<BrandingApiServerFixture>
{
    private readonly BrandingApiServerFixture _fixture;
    private readonly HttpClient _client;

    public BrandingApiTests(BrandingApiServerFixture fixture)
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

    [Fact]
    public async Task Branding_Get_IsPublicAndReturnsAppName()
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/ag-ui/settings/branding");
        var res = await _client.SendAsync(req); // 无需登录 → 公开
        res.EnsureSuccessStatusCode();
        var d = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(string.IsNullOrWhiteSpace(d.GetProperty("appName").GetString()));
    }

    [Fact]
    public async Task Branding_Post_OnlyAdminCanChange()
    {
        // 管理员保存品牌配置
        var admin = await RegisterAsync("brand_admin");
        var adminToken = admin.GetProperty("token").GetString()!;
        using var set = Authed(HttpMethod.Post, "/ag-ui/settings/branding", adminToken);
        set.Content = JsonContent.Create(new { appName = "星云协作", primaryColor = "#4f8cff", forceDark = true, tagline = "让每一个团队都有 AI 助手" });
        var setRes = await _client.SendAsync(set);
        setRes.EnsureSuccessStatusCode();

        // 公开读取应返回保存后的品牌
        var get = await (await _client.GetAsync("/ag-ui/settings/branding")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(get.GetProperty("configured").GetBoolean());
        Assert.Equal("星云协作", get.GetProperty("appName").GetString());
        Assert.Equal("#4f8cff", get.GetProperty("primaryColor").GetString());

        // 非管理员保存 → 403
        var normal = await RegisterAsync("brand_normal");
        using var denied = Authed(HttpMethod.Post, "/ag-ui/settings/branding", normal.GetProperty("token").GetString());
        denied.Content = JsonContent.Create(new { appName = "越权改名" });
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(denied)).StatusCode);
    }

    [Fact]
    public async Task Branding_Post_RejectsDangerousLogoAndBadColor()
    {
        var admin = await RegisterAsync("brand_admin");
        var token = admin.GetProperty("token").GetString()!;

        // 非法主色
        using var badColor = Authed(HttpMethod.Post, "/ag-ui/settings/branding", token);
        badColor.Content = JsonContent.Create(new { primaryColor = "red" });
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.SendAsync(badColor)).StatusCode);

        // 危险 schema 的 Logo
        using var badLogo = Authed(HttpMethod.Post, "/ag-ui/settings/branding", token);
        badLogo.Content = JsonContent.Create(new { logoUrl = "javascript:alert(1)" });
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.SendAsync(badLogo)).StatusCode);

        // 合法 Logo（站内相对路径）
        using var okLogo = Authed(HttpMethod.Post, "/ag-ui/settings/branding", token);
        okLogo.Content = JsonContent.Create(new { logoUrl = "/ag-ui/files/att_logo/logo.png", appName = "OK 品牌" });
        var r = await _client.SendAsync(okLogo);
        r.EnsureSuccessStatusCode();
        var d = await (await _client.GetAsync("/ag-ui/settings/branding")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("/ag-ui/files/att_logo/logo.png", d.GetProperty("logoUrl").GetString());
    }
}
