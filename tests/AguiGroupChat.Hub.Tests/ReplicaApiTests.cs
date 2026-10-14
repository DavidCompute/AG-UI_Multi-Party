using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AguiGroupChat.Agents;
using AguiGroupChat.Hub;
using AguiGroupChat.Hub.Persistence;
using AguiGroupChat.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>
/// 跨副本诊断端点 <c>GET /ag-ui/admin/replicas</c> 集成测试：Operator+ 才可读、返回自身副本与汇总、
/// 反映注册表里发布的其它副本快照、单副本（无 Redis）时 redis 段为 null。
/// </summary>
public sealed class ReplicaApiServerFixture : IAsyncLifetime
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
            ["Auth:FirstUserIsAdmin"] = "false",
            ["Auth:AdminUserIds"] = "rep_admin",
        });
        HubApp.ConfigureServices(builder);
        builder.Services.AddAgentFramework(builder.Configuration);
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));

        App = builder.Build();
        HubApp.MapEndpoints(App);
        App.MapAdminApi(); // /replicas 挂在管理员控制台 API 下
        await App.StartAsync();
        HttpBase = App.Urls.First();
    }

    public async Task DisposeAsync() => await App.DisposeAsync();
}

public sealed class ReplicaApiTests : IClassFixture<ReplicaApiServerFixture>
{
    private readonly ReplicaApiServerFixture _fx;
    private readonly HttpClient _client;

    public ReplicaApiTests(ReplicaApiServerFixture fx)
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
    public async Task RequiresOperatorOrAbove()
    {
        var plain = await TokenAsync("rep_plain");
        using var req = Authed(HttpMethod.Get, "/ag-ui/admin/replicas", plain);
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(req)).StatusCode);
    }

    [Fact]
    public async Task ReturnsSelfAndAggregate()
    {
        var admin = await TokenAsync("rep_admin");
        using var req = Authed(HttpMethod.Get, "/ag-ui/admin/replicas", admin);
        var res = await _client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var d = await res.Content.ReadFromJsonAsync<JsonElement>();
        var selfId = d.GetProperty("selfId").GetString()!;
        Assert.False(string.IsNullOrEmpty(selfId));
        Assert.True(d.GetProperty("count").GetInt32() >= 1);
        var replicas = d.GetProperty("replicas").EnumerateArray().ToList();
        Assert.Contains(replicas, r => r.GetProperty("isSelf").GetBoolean() && r.GetProperty("replicaId").GetString() == selfId);
        Assert.True(d.GetProperty("aggregate").GetProperty("invocations").GetInt64() >= 0);
        // 单副本（无 Redis）→ redis 段为 null
        Assert.Equal(JsonValueKind.Null, d.GetProperty("redis").ValueKind);
    }

    [Fact]
    public async Task ReflectsPublishedReplicaSnapshots()
    {
        var admin = await TokenAsync("rep_admin");
        // 直接经注册表发布一个「另一副本」的快照（模拟多副本共享注册表）
        var registry = _fx.App.Services.GetRequiredService<IReplicaRegistry>();
        registry.Publish(new ReplicaSnapshot { ReplicaId = "other-replica", Host = "h2", Pid = 42, Invocations = 9, WorkingSetBytes = 1024 });

        using var req = Authed(HttpMethod.Get, "/ag-ui/admin/replicas", admin);
        var d = await (await _client.SendAsync(req)).Content.ReadFromJsonAsync<JsonElement>();
        var other = d.GetProperty("replicas").EnumerateArray().First(r => r.GetProperty("replicaId").GetString() == "other-replica");
        Assert.Equal(9, other.GetProperty("invocations").GetInt64());
        Assert.False(other.GetProperty("isSelf").GetBoolean());
        Assert.True(d.GetProperty("aggregate").GetProperty("invocations").GetInt64() >= 9);
    }
}
