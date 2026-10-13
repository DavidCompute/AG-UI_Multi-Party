using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AguiGroupChat.Agents;
using AguiGroupChat.Hub;
using AguiGroupChat.Hub.Messaging;
using AguiGroupChat.Hub.Models;
using AguiGroupChat.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>
/// 出站 Webhook 端到端：主应用（Hub + Webhook 调度器）+ 一个模拟外部系统的接收端。
/// 覆盖 管理 API 鉴权 / 创建返回一次性密钥 / 列表脱敏 / 真实群事件触发投递 / HMAC 验签。
/// </summary>
public sealed class WebhookE2EFixture : IAsyncLifetime
{
    public sealed record ReceiverHit(string EventType, string Signature, long Timestamp, string Body);

    public WebApplication App { get; private set; } = null!;
    public string HttpBase { get; private set; } = null!;
    public string ReceiverBase { get; private set; } = null!;
    public ConcurrentQueue<ReceiverHit> Hits { get; } = new();

    private WebApplication _receiver = null!;

    public async Task InitializeAsync()
    {
        // ---- 接收端：模拟外部回调地址（记录事件类型 / 签名 / 时间戳 / 原始 body）----
        var rb = WebApplication.CreateBuilder();
        rb.WebHost.UseUrls("http://127.0.0.1:0");
        rb.Logging.ClearProviders();
        _receiver = rb.Build();
        _receiver.MapPost("/hook", async ctx =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            var body = await reader.ReadToEndAsync();
            long.TryParse(ctx.Request.Headers["X-KnowGath-Timestamp"].ToString(), out var ts);
            Hits.Enqueue(new ReceiverHit(
                ctx.Request.Headers["X-KnowGath-Event"].ToString(),
                ctx.Request.Headers["X-KnowGath-Signature"].ToString(), ts, body));
            ctx.Response.StatusCode = 200;
        });
        await _receiver.StartAsync();
        ReceiverBase = _receiver.Urls.First();

        // ---- 主应用：Hub + 出站 Webhook（含真实调度器）----
        var builder = HubApp.CreateBuilder([]);
        builder.Environment.EnvironmentName = "Testing";
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["GroupChat:SeedSampleData"] = "false",
            ["Agents:Provider"] = "mock",
            ["Persistence:Enabled"] = "false",
            ["Auth:AdminUserIds"] = "wh_admin",
        });
        HubApp.ConfigureServices(builder);
        builder.Services.AddAgentFramework(builder.Configuration);
        builder.Services.AddSingleton(new WebhookState());
        builder.Services.AddSingleton<WebhookDispatcher>();
        builder.Services.AddSingleton<IGroupEventObserver>(sp => sp.GetRequiredService<WebhookDispatcher>());
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));

        App = builder.Build();
        HubApp.MapEndpoints(App);
        App.MapWebhookApi();
        await App.StartAsync();
        HttpBase = App.Urls.First();
        App.Services.GetRequiredService<WebhookDispatcher>().Start();
    }

    public async Task DisposeAsync()
    {
        await App.DisposeAsync();
        await _receiver.DisposeAsync();
    }
}

public sealed class WebhookApiTests : IClassFixture<WebhookE2EFixture>
{
    private readonly WebhookE2EFixture _fx;
    private readonly HttpClient _client;

    public WebhookApiTests(WebhookE2EFixture fx)
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

    private async Task<string> AdminTokenAsync()
    {
        var res = await _client.PostAsJsonAsync("/ag-ui/user/register", new { username = "wh_admin", password = "secret1", nickname = "wh" });
        if (!res.IsSuccessStatusCode)
            res = await _client.PostAsJsonAsync("/ag-ui/user/login", new { username = "wh_admin", password = "secret1" });
        res.EnsureSuccessStatusCode();
        var doc = await res.Content.ReadFromJsonAsync<JsonElement>();
        return doc.GetProperty("token").GetString()!;
    }

    private async Task WaitForHitAsync(Func<WebhookE2EFixture.ReceiverHit, bool> predicate, int timeoutMs = 8000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (_fx.Hits.Any(predicate)) return;
            await Task.Delay(100);
        }
    }

    [Fact]
    public async Task Webhook_RequiresAdmin()
    {
        var user = await _client.PostAsJsonAsync("/ag-ui/user/register", new { username = "wh_plain", password = "secret1", nickname = "p" });
        if (!user.IsSuccessStatusCode)
            user = await _client.PostAsJsonAsync("/ag-ui/user/login", new { username = "wh_plain", password = "secret1" });
        user.EnsureSuccessStatusCode();
        var token = (await user.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;

        using var req = Authed(HttpMethod.Get, "/ag-ui/admin/webhooks", token);
        var res = await _client.SendAsync(req);
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task Webhook_CreateRejectsBadUrl()
    {
        var token = await AdminTokenAsync();
        using var req = Authed(HttpMethod.Post, "/ag-ui/admin/webhooks", token);
        req.Content = JsonContent.Create(new { name = "bad", url = "ftp://nope" });
        var res = await _client.SendAsync(req);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Webhook_CreateReturnsSecretOnce_ListHidesIt_ThenDeliversOnEvent()
    {
        var token = await AdminTokenAsync();

        // 1) 新建订阅：返回一次性明文密钥
        using var create = Authed(HttpMethod.Post, "/ag-ui/admin/webhooks", token);
        create.Content = JsonContent.Create(new
        {
            name = "e2e",
            url = _fx.ReceiverBase + "/hook",
            eventTypes = new[] { EventTypes.TextMessageEnd },
        });
        var created = await (await _client.SendAsync(create)).Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetString()!;
        var secret = created.GetProperty("secret").GetString()!;
        Assert.StartsWith("whsec_", secret);

        // 2) 列表脱敏：不回明文密钥，只标 hasSecret
        using var list = Authed(HttpMethod.Get, "/ag-ui/admin/webhooks", token);
        var listDoc = await (await _client.SendAsync(list)).Content.ReadFromJsonAsync<JsonElement>();
        var first = listDoc.EnumerateArray().First(s => s.GetProperty("id").GetString() == id);
        Assert.True(first.GetProperty("hasSecret").GetBoolean());
        Assert.False(first.TryGetProperty("secret", out _));

        // 3) 真实群事件 → 调度器投递到接收端（只订阅 TEXT_MESSAGE_END）
        using var gc = Authed(HttpMethod.Post, "/ag-ui/group/create", token);
        gc.Content = JsonContent.Create(new { groupName = "webhook 群", ownerId = "ignored" });
        var group = await (await _client.SendAsync(gc)).Content.ReadFromJsonAsync<JsonElement>();
        var groupId = group.GetProperty("groupId").GetString()!;

        using var send = Authed(HttpMethod.Post, "/ag-ui/group/message/send", token);
        send.Content = JsonContent.Create(new { groupId, content = "hello webhook" });
        (await _client.SendAsync(send)).EnsureSuccessStatusCode();

        await WaitForHitAsync(h => h.EventType == EventTypes.TextMessageEnd);
        var hit = _fx.Hits.FirstOrDefault(h => h.EventType == EventTypes.TextMessageEnd);
        Assert.NotNull(hit);

        // 4) HMAC 验签：签名 = sha256 hex(HMAC(secret, "{timestamp}.{body}"))
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var expected = "sha256=" + Convert.ToHexString(
            hmac.ComputeHash(Encoding.UTF8.GetBytes(hit!.Timestamp + "." + hit.Body))).ToLowerInvariant();
        Assert.Equal(expected, hit.Signature);

        // 5) 载荷信封：event / groupId / data.type
        using var body = JsonDocument.Parse(hit.Body);
        Assert.Equal(EventTypes.TextMessageEnd, body.RootElement.GetProperty("event").GetString());
        Assert.Equal(groupId, body.RootElement.GetProperty("groupId").GetString());
        Assert.Equal(EventTypes.TextMessageEnd, body.RootElement.GetProperty("data").GetProperty("type").GetString());

        // 6) 清理
        using var del = Authed(HttpMethod.Delete, "/ag-ui/admin/webhooks/" + id, token);
        Assert.True((await _client.SendAsync(del)).IsSuccessStatusCode);
    }
}
