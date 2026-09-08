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

/// <summary>全局智能检索的 Kestrel 集成测试夹具（memory 存储 + mock 网关，记忆未启用）。</summary>
public sealed class GlobalSearchServerFixture : IAsyncLifetime
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
        });
        HubApp.ConfigureServices(builder);
        builder.Services.AddAgentFramework(builder.Configuration);
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));

        App = builder.Build();
        HubApp.MapEndpoints(App);
        App.MapGlobalSearchApi();
        await App.StartAsync();
        HttpBase = App.Urls.First();
    }

    public async Task DisposeAsync()
    {
        if (App is not null) await App.DisposeAsync();
    }
}

public sealed class GlobalSearchApiTests : IClassFixture<GlobalSearchServerFixture>
{
    private readonly GlobalSearchServerFixture _fixture;
    private readonly HttpClient _client;

    public GlobalSearchApiTests(GlobalSearchServerFixture fixture)
    {
        _fixture = fixture;
        _client = new HttpClient { BaseAddress = new Uri(fixture.HttpBase) };
    }

    private async Task<(string UserId, string Token)> RegisterAsync(string username)
    {
        var res = await _client.PostAsJsonAsync("/ag-ui/user/register", new { username, password = "secret123", nickname = username });
        if (res.StatusCode == HttpStatusCode.Conflict)
        {
            res = await _client.PostAsJsonAsync("/ag-ui/user/login", new { username, password = "secret123" });
        }
        res.EnsureSuccessStatusCode();
        var d = await res.Content.ReadFromJsonAsync<JsonElement>();
        return (d.GetProperty("userId").GetString()!, d.GetProperty("token").GetString()!);
    }

    private static HttpRequestMessage Authed(HttpMethod method, string path, string token)
    {
        var req = new HttpRequestMessage(method, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return req;
    }

    [Fact]
    public async Task Search_VisibleMessagesOnly_NoLeakToOutsider()
    {
        var (ownerId, ownerToken) = await RegisterAsync("gsearch_owner");
        var (memberId, memberToken) = await RegisterAsync("gsearch_member");
        var (outsiderId, outsiderToken) = await RegisterAsync("gsearch_outsider");

        // 建群：owner 拉 member 入群
        string gid;
        using (var create = Authed(HttpMethod.Post, "/ag-ui/group/create", ownerToken))
        {
            create.Content = JsonContent.Create(new { groupName = "火星探测组", ownerId = ownerId, memberIds = new[] { memberId } });
            var created = await _client.SendAsync(create);
            created.EnsureSuccessStatusCode();
            gid = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("groupId").GetString()!;
        }

        async Task<string> Send(string token, string content, string visibility = "all", string[]? visibleTo = null)
        {
            using var send = Authed(HttpMethod.Post, "/ag-ui/group/message/send", token);
            send.Content = JsonContent.Create(new
            {
                groupId = gid,
                content,
                visibility,
                visibleMemberIds = visibleTo ?? Array.Empty<string>(),
            });
            var res = await _client.SendAsync(send);
            res.EnsureSuccessStatusCode();
            return (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("messageId").GetString()!;
        }

        await Send(ownerToken, "火星探测任务将于明日发射 A1");
        await Send(memberToken, "火星车遥测数据已收到 B2");
        // 私密消息只对 owner 定向：member 与群外用户均不可见
        await Send(ownerToken, "绝密火星内部代号 C3", visibility: "private", visibleTo: [ownerId]);

        // member：可见 A1/B2（群全员），看不到 C3（未定向给他）
        var memberRes = await _client.SendAsync(Authed(HttpMethod.Get, $"/ag-ui/search?q=火星&limit=40", memberToken));
        memberRes.EnsureSuccessStatusCode();
        var memberBody = await memberRes.Content.ReadFromJsonAsync<JsonElement>();
        var memberMsgs = memberBody.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal(2, memberMsgs.Count);
        Assert.Contains(memberMsgs, m => m.GetProperty("snippet").GetString()!.Contains("A1"));
        Assert.Contains(memberMsgs, m => m.GetProperty("snippet").GetString()!.Contains("B2"));
        Assert.DoesNotContain(memberMsgs, m => m.GetProperty("snippet").GetString()!.Contains("C3"));
        Assert.Contains(memberMsgs, m => m.GetProperty("groupName").GetString() == "火星探测组");

        // owner：三句都在（含私密）
        var ownerRes = await _client.SendAsync(Authed(HttpMethod.Get, "/ag-ui/search?q=火星", ownerToken));
        ownerRes.EnsureSuccessStatusCode();
        var ownerMsgs = (await ownerRes.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal(3, ownerMsgs.Count);

        // 群外用户：完全看不到该群内容
        var outRes = await _client.SendAsync(Authed(HttpMethod.Get, "/ag-ui/search?q=火星", outsiderToken));
        outRes.EnsureSuccessStatusCode();
        var outMsgs = (await outRes.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("messages").EnumerateArray().ToList();
        Assert.Empty(outMsgs);

        // 关键词过短 → 400
        var shortRes = await _client.SendAsync(Authed(HttpMethod.Get, "/ag-ui/search?q=%E7%81%AB", outsiderToken));
        Assert.Equal(HttpStatusCode.BadRequest, shortRes.StatusCode);

        _ = memberId; _ = outsiderId;
    }
}
