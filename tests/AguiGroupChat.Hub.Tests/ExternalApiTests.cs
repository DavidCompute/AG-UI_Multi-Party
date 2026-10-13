using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
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
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>
/// 版本化对外 REST API（<c>/agui/v1</c>，入站集成）集成测试：真实 Kestrel 上的
/// 鉴权 / 身份 / 知聚列表 / 发消息 / 历史 / 单条 / 回复长轮询 / 同步等回复。
/// </summary>
public sealed class ExternalApiServerFixture : IAsyncLifetime
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
        });
        HubApp.ConfigureServices(builder);
        builder.Services.AddAgentFramework(builder.Configuration);
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));

        App = builder.Build();
        HubApp.MapEndpoints(App);
        App.MapExternalApi();
        await App.StartAsync();
        HttpBase = App.Urls.First();
    }

    public async Task DisposeAsync() => await App.DisposeAsync();
}

public sealed class ExternalApiTests : IClassFixture<ExternalApiServerFixture>
{
    private readonly ExternalApiServerFixture _fx;
    private readonly HttpClient _client;

    public ExternalApiTests(ExternalApiServerFixture fx)
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

    private const string AgentId = "agent_helper";

    private async Task<string> CreateGroupAsync(string token, string name = "对外 API 群")
    {
        using var req = Authed(HttpMethod.Post, "/ag-ui/group/create", token);
        req.Content = JsonContent.Create(new
        {
            groupName = name,
            ownerId = "ignored",
            memberIds = new[] { AgentId },
            members = new[] { new { memberId = AgentId, memberType = "agent", nickname = "小助手" } },
        });
        var res = await _client.SendAsync(req);
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("groupId").GetString()!;
    }

    private async Task<JsonElement> SendAsync(string token, string groupId, string content, int waitMs = 0)
    {
        using var req = Authed(HttpMethod.Post, $"/agui/v1/groups/{groupId}/messages", token);
        req.Content = JsonContent.Create(new { content, waitMs });
        var res = await _client.SendAsync(req);
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadFromJsonAsync<JsonElement>();
    }

    private GroupHub Hub => _fx.App.Services.GetRequiredService<GroupHub>();

    /// <summary>直接经 Hub 发布一条完整的智能体回复（模拟数字员工已答完）。</summary>
    private static async Task<string> PublishAgentReplyAsync(GroupHub hub, string groupId, string replyTo, string text)
    {
        var msg = await hub.PublishAgentMessageStartAsync(new AgentMessageStartInput
        {
            GroupId = groupId,
            AgentId = AgentId,
            ReplyToMessageId = replyTo,
        });
        await hub.AppendAgentContentAsync(groupId, msg.MessageId, text);
        await hub.EndAgentMessageAsync(groupId, msg.MessageId);
        return msg.MessageId;
    }

    [Fact]
    public async Task RequiresAuth()
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/agui/v1/me");
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.SendAsync(req)).StatusCode);
    }

    [Fact]
    public async Task Me_ReturnsCallerIdentity()
    {
        var token = await TokenAsync("v1_user");
        using var req = Authed(HttpMethod.Get, "/agui/v1/me", token);
        var me = await (await _client.SendAsync(req)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("v1_user", me.GetProperty("username").GetString());
        Assert.False(string.IsNullOrEmpty(me.GetProperty("userId").GetString()));
    }

    [Fact]
    public async Task SendThenReadHistoryAndSingle()
    {
        var token = await TokenAsync("v1_user2");
        var gid = await CreateGroupAsync(token);

        var sent = await SendAsync(token, gid, "hello v1");
        var messageId = sent.GetProperty("messageId").GetString()!;
        Assert.True(sent.GetProperty("reply").ValueKind == JsonValueKind.Null); // 未请求等待 → 无回复

        // 历史（默认按 before 游标取更早，升序）
        using var hist = Authed(HttpMethod.Get, $"/agui/v1/groups/{gid}/messages", token);
        var list = await (await _client.SendAsync(hist)).Content.ReadFromJsonAsync<JsonElement>();
        var mine = list.EnumerateArray().First(m => m.GetProperty("messageId").GetString() == messageId);
        Assert.Equal("hello v1", mine.GetProperty("content").GetString());
        Assert.Equal("User", mine.GetProperty("senderType").GetString());
        Assert.False(mine.GetProperty("fromAgent").GetBoolean());

        // 单条
        using var one = Authed(HttpMethod.Get, $"/agui/v1/groups/{gid}/messages/{messageId}", token);
        Assert.Equal(HttpStatusCode.OK, (await _client.SendAsync(one)).StatusCode);

        // 不存在
        using var missing = Authed(HttpMethod.Get, $"/agui/v1/groups/{gid}/messages/msg_nope", token);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.SendAsync(missing)).StatusCode);

        // 群列表包含该知聚
        using var groups = Authed(HttpMethod.Get, "/agui/v1/groups", token);
        var gl = await (await _client.SendAsync(groups)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains(gl.EnumerateArray(), g => g.GetProperty("groupId").GetString() == gid);
    }

    [Fact]
    public async Task NonMember_IsForbidden()
    {
        var owner = await TokenAsync("v1_owner");
        var outsider = await TokenAsync("v1_outsider");
        var gid = await CreateGroupAsync(owner, "owner 的群");

        using var send = Authed(HttpMethod.Post, $"/agui/v1/groups/{gid}/messages", outsider);
        send.Content = JsonContent.Create(new { content = "sneak" });
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(send)).StatusCode);

        using var read = Authed(HttpMethod.Get, $"/agui/v1/groups/{gid}/messages", outsider);
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(read)).StatusCode);
    }

    [Fact]
    public async Task Replies_LongPoll_ReturnsPublishedAgentReply()
    {
        var token = await TokenAsync("v1_user3");
        var gid = await CreateGroupAsync(token);
        var sent = await SendAsync(token, gid, "问题");
        var sentId = sent.GetProperty("messageId").GetString()!;
        var sentTs = sent.GetProperty("createdAt").GetInt64();

        // 后台 400ms 后发布一条完整回复 → 长轮询应立即返回
        _ = Task.Run(async () =>
        {
            await Task.Delay(400);
            await PublishAgentReplyAsync(Hub, gid, sentId, "答复内容");
        });

        using var req = Authed(HttpMethod.Get, $"/agui/v1/groups/{gid}/replies?waitMs=8000&after={sentTs - 1}", token);
        var replies = await (await _client.SendAsync(req)).Content.ReadFromJsonAsync<JsonElement>();
        var first = replies.EnumerateArray().First();
        Assert.Equal("答复内容", first.GetProperty("content").GetString());
        Assert.True(first.GetProperty("fromAgent").GetBoolean());
        Assert.True(first.GetProperty("complete").GetBoolean());
    }

    [Fact]
    public async Task Send_WithWaitMs_ReturnsCompleteReply()
    {
        var token = await TokenAsync("v1_user4");
        var gid = await CreateGroupAsync(token);

        // 后台等到消息出现后发布回复（模拟数字员工处理）
        _ = Task.Run(async () =>
        {
            await Task.Delay(500);
            var latest = Hub.Store.AllMessages(gid).Where(m => m.SenderType == MemberType.User).OrderBy(m => m.Timestamp).LastOrDefault();
            if (latest is not null) await PublishAgentReplyAsync(Hub, gid, latest.MessageId, "同步答复");
        });

        var resp = await SendAsync(token, gid, "同步等待", waitMs: 10000);
        var reply = resp.GetProperty("reply");
        Assert.Equal(JsonValueKind.Object, reply.ValueKind);
        Assert.Equal("同步答复", reply.GetProperty("content").GetString());
        Assert.True(reply.GetProperty("complete").GetBoolean());
    }

    [Fact]
    public async Task Replies_NoWait_ReturnsEmptyWhenNone()
    {
        var token = await TokenAsync("v1_user5");
        var gid = await CreateGroupAsync(token);
        using var req = Authed(HttpMethod.Get, $"/agui/v1/groups/{gid}/replies?waitMs=0", token);
        var replies = await (await _client.SendAsync(req)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Array, replies.ValueKind);
        Assert.Empty(replies.EnumerateArray());
    }
}
