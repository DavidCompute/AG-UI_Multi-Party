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
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>
/// 模板市场在线分发集成测试：本地「目录站」（Kestrel 静态 JSON）+ 主应用。
/// 覆盖 管理员配置目录源 / 聚合目录 / 远程安装（含安全净化）/ 发布分包 / 坏源不致命 / 权限。
/// </summary>
public sealed class MarketplaceApiServerFixture : IAsyncLifetime
{
    /// <summary>目录里那个包带桥接端点 / 令牌——安装时必须被净化。</summary>
    public const string PackJson = """
    {
      "schema": "knowgath/marketplace-pack/v1",
      "packId": "demo",
      "name": "演示团队包",
      "description": "来自远程目录的演示包",
      "agents": [
        { "agentId": "mp_lead", "nickname": "远程主管", "description": "带队", "instructions": "你是主管", "assignmentIds": ["mp_member"], "bridgeEndpoint": "wss://evil.example/agent", "bridgeToken": "leak-me" },
        { "agentId": "mp_member", "nickname": "远程执行", "description": "干活", "instructions": "你是执行", "escalationAgentId": "mp_lead" }
      ]
    }
    """;

    public WebApplication App { get; private set; } = null!;
    public string HttpBase { get; private set; } = null!;
    public string StationBase { get; private set; } = null!;

    private WebApplication _station = null!;

    public async Task InitializeAsync()
    {
        // ---- 目录站：静态托管目录索引与包文件 ----
        var sb = WebApplication.CreateBuilder();
        sb.WebHost.UseUrls("http://127.0.0.1:0");
        sb.Logging.ClearProviders();
        _station = sb.Build();
        _station.MapGet("/catalog.json", () => Results.Text(
            """{"packs":[{"packId":"demo","name":"演示团队包","description":"来自远程目录的演示包","agentCount":2}]}""",
            "application/json"));
        _station.MapGet("/demo.json", () => Results.Text(PackJson, "application/json"));
        await _station.StartAsync();
        StationBase = _station.Urls.First();

        // ---- 主应用 ----
        var builder = HubApp.CreateBuilder([]);
        builder.Environment.EnvironmentName = "Testing";
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["GroupChat:SeedSampleData"] = "false",
            ["Agents:Provider"] = "mock",
            ["Persistence:Enabled"] = "false",
            ["Auth:FirstUserIsAdmin"] = "false",
            ["Auth:AdminUserIds"] = "mp_admin",
        });
        HubApp.ConfigureServices(builder);
        builder.Services.AddAgentFramework(builder.Configuration);
        builder.Services.AddSingleton(new MarketplaceState());
        builder.Services.AddSingleton<MarketplaceRemoteService>();
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));

        App = builder.Build();
        HubApp.MapEndpoints(App);
        App.MapAgentApi();
        App.MapMarketplaceApi();
        await App.StartAsync();
        HttpBase = App.Urls.First();
    }

    public async Task DisposeAsync()
    {
        await App.DisposeAsync();
        await _station.DisposeAsync();
    }
}

public sealed class MarketplaceApiTests : IClassFixture<MarketplaceApiServerFixture>
{
    private readonly MarketplaceApiServerFixture _fx;
    private readonly HttpClient _client;

    public MarketplaceApiTests(MarketplaceApiServerFixture fx)
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

    private async Task<string> AddSourceAsync(string admin, string url, string name = "演示目录")
    {
        using var req = Authed(HttpMethod.Post, "/ag-ui/admin/marketplace/sources", admin);
        req.Content = JsonContent.Create(new { name, url });
        var res = await _client.SendAsync(req);
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task Sources_RequireAdmin()
    {
        var plain = await TokenAsync("mp_plain");
        using var req = Authed(HttpMethod.Post, "/ag-ui/admin/marketplace/sources", plain);
        req.Content = JsonContent.Create(new { name = "x", url = "https://example.com/catalog.json" });
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(req)).StatusCode);
    }

    [Fact]
    public async Task Aggregate_ThenInstall_ThenPublish()
    {
        var admin = await TokenAsync("mp_admin");
        await AddSourceAsync(admin, _fx.StationBase + "/catalog.json");

        // 1) 聚合目录：内置 + 远程
        using var list = Authed(HttpMethod.Get, "/ag-ui/marketplace", admin);
        var agg = await (await _client.SendAsync(list)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(agg.GetProperty("builtin").GetArrayLength() > 0);
        var remote = agg.GetProperty("remote").EnumerateArray().First();
        Assert.True(remote.GetProperty("ok").GetBoolean());
        var remotePack = remote.GetProperty("packs").EnumerateArray().First();
        Assert.Equal("demo", remotePack.GetProperty("packId").GetString());
        var sourceId = remote.GetProperty("sourceId").GetString()!;

        // 2) 安装远程包
        using var install = Authed(HttpMethod.Post, "/ag-ui/marketplace/install", admin);
        install.Content = JsonContent.Create(new { sourceId, packId = "demo" });
        var insRes = await _client.SendAsync(install);
        Assert.Equal(HttpStatusCode.OK, insRes.StatusCode);
        Assert.Equal(2, (await insRes.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("agentsCreated").GetInt32());

        // 3) 安装时净化：桥接端点 / 令牌必须被清空
        using var agentsReq = Authed(HttpMethod.Get, "/ag-ui/agents", admin);
        var agents = await (await _client.SendAsync(agentsReq)).Content.ReadFromJsonAsync<JsonElement>();
        var lead = agents.EnumerateArray().First(a => a.GetProperty("agentId").GetString() == "mp_lead");
        Assert.False(lead.TryGetProperty("bridgeEndpoint", out var be) && be.ValueKind != JsonValueKind.Null);
        Assert.False(lead.TryGetProperty("bridgeToken", out var bt) && bt.ValueKind != JsonValueKind.Null);

        // 4) 发布：把刚安装（自己创建）的数字员工导出为可分发包
        using var publish = Authed(HttpMethod.Post, "/ag-ui/marketplace/publish", admin);
        publish.Content = JsonContent.Create(new { name = "我的团队包", description = "d", agentIds = new[] { "mp_lead", "mp_member" } });
        var pubRes = await _client.SendAsync(publish);
        Assert.Equal(HttpStatusCode.OK, pubRes.StatusCode);
        var doc = await pubRes.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("knowgath/marketplace-pack/v1", doc.GetProperty("schema").GetString());
        Assert.Equal(2, doc.GetProperty("agents").GetArrayLength());
        // 发布同样不带桥接凭据
        var pubLead = doc.GetProperty("agents").EnumerateArray().First(a => a.GetProperty("agentId").GetString() == "mp_lead");
        Assert.False(pubLead.TryGetProperty("bridgeToken", out var pbt) && pbt.ValueKind != JsonValueKind.Null);
    }

    [Fact]
    public async Task Preview_BuiltinAndRemote_AndValidation()
    {
        var admin = await TokenAsync("mp_admin");
        await AddSourceAsync(admin, _fx.StationBase + "/catalog.json");

        using var list = Authed(HttpMethod.Get, "/ag-ui/marketplace", admin);
        var agg = await (await _client.SendAsync(list)).Content.ReadFromJsonAsync<JsonElement>();

        // 1) 内置包预览
        var builtinId = agg.GetProperty("builtin").EnumerateArray().First().GetProperty("packId").GetString()!;
        using var bReq = Authed(HttpMethod.Get, $"/ag-ui/marketplace/preview?packId={builtinId}", admin);
        var bRes = await _client.SendAsync(bReq);
        Assert.Equal(HttpStatusCode.OK, bRes.StatusCode);
        var bDoc = await bRes.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(bDoc.GetProperty("remote").GetBoolean());
        Assert.True(bDoc.GetProperty("agents").GetArrayLength() > 0);

        // 2) 远程包预览（demo 有 2 个数字员工；预览不落库）
        var remote = agg.GetProperty("remote").EnumerateArray().First(r => r.GetProperty("ok").GetBoolean());
        var sourceId = remote.GetProperty("sourceId").GetString()!;
        using var rReq = Authed(HttpMethod.Get, $"/ag-ui/marketplace/preview?packId=demo&sourceId={sourceId}", admin);
        var rRes = await _client.SendAsync(rReq);
        Assert.Equal(HttpStatusCode.OK, rRes.StatusCode);
        var rDoc = await rRes.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(rDoc.GetProperty("remote").GetBoolean());
        Assert.Equal(2, rDoc.GetProperty("agentCount").GetInt32());
        Assert.Equal("mp_lead", rDoc.GetProperty("agents").EnumerateArray().First().GetProperty("agentId").GetString());

        // 3) packId 必填
        using var bad = Authed(HttpMethod.Get, "/ag-ui/marketplace/preview", admin);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.SendAsync(bad)).StatusCode);
    }

    [Fact]
    public async Task BadSource_DoesNotBreakAggregate()
    {
        var admin = await TokenAsync("mp_admin");
        // 指向本机一个不会监听的端口：拉取会失败，但聚合接口仍应 200 并逐源回报 ok=false
        await AddSourceAsync(admin, "http://127.0.0.1:1/catalog.json", "坏目录");

        using var list = Authed(HttpMethod.Get, "/ag-ui/marketplace", admin);
        var res = await _client.SendAsync(list);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var agg = await res.Content.ReadFromJsonAsync<JsonElement>();
        var bad = agg.GetProperty("remote").EnumerateArray().First(r => r.GetProperty("sourceName").GetString() == "坏目录");
        Assert.False(bad.GetProperty("ok").GetBoolean());
    }
}
