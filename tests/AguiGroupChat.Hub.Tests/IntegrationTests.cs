using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>
/// 自托管真实 Kestrel 的集成测试夹具：复用 Program 的同一套装配逻辑（HubApp），
/// 端口 0 随机绑定，启动后从 App.Urls 读取真实地址。
/// </summary>
public sealed class HubServerFixture : IAsyncLifetime
{
    public WebApplication App { get; private set; } = null!;
    public string HttpBase { get; private set; } = null!;
    public string WsBase { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var builder = HubApp.CreateBuilder([]);
        builder.Environment.EnvironmentName = "Testing";
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["GroupChat:SeedSampleData"] = "false",
            ["Persistence:Enabled"] = "false",
            // 协议集成测试走 memberId 直连回退模式（默认已改为强制令牌，这里显式关闭以覆盖回退路径）
            ["Auth:RequireTokenOnRealTime"] = "false",
        });
        HubApp.ConfigureServices(builder);

        App = builder.Build();
        HubApp.MapEndpoints(App);
        await App.StartAsync();

        HttpBase = App.Urls.First();
        WsBase = HttpBase.Replace("http://", "ws://");
    }

    public async Task DisposeAsync()
    {
        if (App is not null) await App.DisposeAsync();
    }
}

public sealed class IntegrationTests : IClassFixture<HubServerFixture>
{
    private readonly HubServerFixture _fixture;
    private readonly HttpClient _client;

    public IntegrationTests(HubServerFixture fixture)
    {
        _fixture = fixture;
        _client = new HttpClient { BaseAddress = new Uri(fixture.HttpBase) };
    }

    // ================= HTTP API =================

    [Fact]
    public async Task CreateGroup_SendMessage_GetSnapshot_HttpFlow()
    {
        var create = await _client.PostAsJsonAsync("/ag-ui/group/create", new
        {
            groupName = "集成测试群",
            ownerId = "user_t1",
            memberIds = new[] { "user_t2" },
        });
        create.EnsureSuccessStatusCode();
        var group = await create.Content.ReadFromJsonAsync<JsonElement>();
        var groupId = group.GetProperty("groupId").GetString()!;
        Assert.Equal(2, group.GetProperty("memberCount").GetInt32());

        var send = await _client.PostAsJsonAsync("/ag-ui/group/message/send", new
        {
            groupId,
            userId = "user_t1",
            content = "hello",
            mentions = new[] { "user_t2" },
        });
        send.EnsureSuccessStatusCode();
        var msg = await send.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("hello", msg.GetProperty("content").GetString());
        Assert.StartsWith("msg_", msg.GetProperty("messageId").GetString());

        var detail = await _client.GetAsync($"/ag-ui/group/{groupId}?memberId=user_t1");
        detail.EnsureSuccessStatusCode();
        var snapshot = await detail.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("集成测试群", snapshot.GetProperty("groupInfo").GetProperty("groupName").GetString());
        Assert.Equal(1, snapshot.GetProperty("latestMessages").GetArrayLength());
    }

    [Fact]
    public async Task MemberGroups_IncludeIsPrivate_AndPrivatePersistsAfterNewGroup()
    {
        // 建一个私密群，确认「我的群」列表带出 isPrivate（回归：手写 DTO 曾漏字段导致前端误判重置）
        var createPrivate = await _client.PostAsJsonAsync("/ag-ui/group/create", new
        {
            groupName = "机密群",
            ownerId = "user_t1",
            isPrivate = true,
        });
        createPrivate.EnsureSuccessStatusCode();
        var privateGroup = await createPrivate.Content.ReadFromJsonAsync<JsonElement>();
        var privateGroupId = privateGroup.GetProperty("groupId").GetString()!;
        Assert.True(privateGroup.GetProperty("isPrivate").GetBoolean());

        // 再建一个普通群（模拟“建新群”操作）
        var createNormal = await _client.PostAsJsonAsync("/ag-ui/group/create", new
        {
            groupName = "普通群",
            ownerId = "user_t1",
        });
        createNormal.EnsureSuccessStatusCode();

        var list = await _client.GetAsync("/ag-ui/member/user_t1/groups?memberId=user_t1");
        list.EnsureSuccessStatusCode();
        var groups = await list.Content.ReadFromJsonAsync<List<JsonElement>>();
        Assert.NotNull(groups); // 成功响应必返回成员群列表
        var priv = groups.First(g => g.GetProperty("groupId").GetString() == privateGroupId);
        Assert.True(priv.GetProperty("isPrivate").GetBoolean(), "私密群的 isPrivate 应在成员群列表中带出");

        // 群详情（快照）同样保留
        var detail = await _client.GetAsync($"/ag-ui/group/{privateGroupId}?memberId=user_t1");
        detail.EnsureSuccessStatusCode();
        var snapshot = await detail.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(snapshot.GetProperty("groupInfo").GetProperty("isPrivate").GetBoolean());
    }

    [Fact]
    public async Task SendMessage_NonMember_Returns403WithErrorCode()
    {
        var create = await _client.PostAsJsonAsync("/ag-ui/group/create", new
        {
            groupName = "g",
            ownerId = "user_t1",
        });
        var groupId = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("groupId").GetString()!;

        var send = await _client.PostAsJsonAsync("/ag-ui/group/message/send", new
        {
            groupId,
            userId = "outsider",
            content = "x",
        });
        Assert.Equal(HttpStatusCode.Forbidden, send.StatusCode);
        var err = await send.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("GROUP_PERMISSION_DENIED", err.GetProperty("code").GetString());
    }

    // ================= WebSocket 全流程 =================

    [Fact]
    public async Task WebSocket_Subscribe_SendMessage_Typing_FullFlow()
    {
        // 1. 创建群组（HTTP）
        var create = await _client.PostAsJsonAsync("/ag-ui/group/create", new
        {
            groupName = "WS 测试群",
            ownerId = "user_w1",
            memberIds = new[] { "user_w2" },
        });
        var groupId = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("groupId").GetString()!;

        // 2. user_w1 连接 + 订阅
        using var ws1 = await ConnectAsync("user_w1");
        var handshake1 = await NextEventAsync(ws1);
        Assert.Equal("GROUP_CONNECTED", handshake1.GetProperty("type").GetString());
        Assert.Equal("user_w1", handshake1.GetProperty("memberId").GetString());

        await SendJsonAsync(ws1, new { type = "GROUP_SUBSCRIBE", groupIds = new[] { groupId } });
        var ack1 = await NextEventAsync(ws1);
        Assert.Equal("GROUP_SUBSCRIBE_ACK", ack1.GetProperty("type").GetString());
        Assert.Equal(groupId, ack1.GetProperty("successGroupIds")[0].GetString());
        var snapshot1 = await NextEventAsync(ws1);
        Assert.Equal("GROUP_STATE_SNAPSHOT", snapshot1.GetProperty("type").GetString());
        Assert.Equal(2, snapshot1.GetProperty("members").GetArrayLength());

        // 3. user_w2 连接 + 订阅（user_w1 会收到其上线状态变更）
        using var ws2 = await ConnectAsync("user_w2");
        Assert.Equal("GROUP_CONNECTED", (await NextEventAsync(ws2)).GetProperty("type").GetString());
        var onlineEvt = await NextEventAsync(ws1);
        Assert.Equal("GROUP_MEMBER_UPDATED", onlineEvt.GetProperty("type").GetString());
        Assert.Equal("user_w2", onlineEvt.GetProperty("memberId").GetString());

        await SendJsonAsync(ws2, new { type = "GROUP_SUBSCRIBE", groupIds = new[] { groupId } });
        Assert.Equal("GROUP_SUBSCRIBE_ACK", (await NextEventAsync(ws2)).GetProperty("type").GetString());
        Assert.Equal("GROUP_STATE_SNAPSHOT", (await NextEventAsync(ws2)).GetProperty("type").GetString());

        // 4. user_w1 通过 HTTP 发消息 → 双方收到三元组
        await _client.PostAsJsonAsync("/ag-ui/group/message/send", new
        {
            groupId,
            userId = "user_w1",
            content = "大家好",
        });

        foreach (var ws in new[] { ws1, ws2 })
        {
            var start = await NextEventAsync(ws);
            Assert.Equal("TEXT_MESSAGE_START", start.GetProperty("type").GetString());
            Assert.Equal(groupId, start.GetProperty("groupId").GetString());
            Assert.Equal("user_w1", start.GetProperty("senderId").GetString());
            Assert.Equal("user", start.GetProperty("senderType").GetString());
            Assert.Equal("TEXT_MESSAGE_CONTENT", (await NextEventAsync(ws)).GetProperty("type").GetString());
            Assert.Equal("TEXT_MESSAGE_END", (await NextEventAsync(ws)).GetProperty("type").GetString());
        }

        // 5. user_w2 发送 typing → user_w1 收到（发送者自己不收）
        await SendJsonAsync(ws2, new { type = "GROUP_TYPING", groupId, memberId = "user_w2", isTyping = true });
        var typing = await NextEventAsync(ws1);
        Assert.Equal("GROUP_TYPING", typing.GetProperty("type").GetString());
        Assert.Equal("user_w2", typing.GetProperty("memberId").GetString());
        Assert.True(typing.GetProperty("isTyping").GetBoolean());

        // 6. 非成员订阅 → ACK 失败
        using var ws3 = await ConnectAsync("outsider");
        Assert.Equal("GROUP_CONNECTED", (await NextEventAsync(ws3)).GetProperty("type").GetString());
        await SendJsonAsync(ws3, new { type = "GROUP_SUBSCRIBE", groupIds = new[] { groupId } });
        var failedAck = await NextEventAsync(ws3);
        Assert.Equal("GROUP_SUBSCRIBE_ACK", failedAck.GetProperty("type").GetString());
        Assert.Equal(groupId, failedAck.GetProperty("failedGroupIds")[0].GetString());
    }

    // ================= 辅助 =================

    private async Task<ClientWebSocket> ConnectAsync(string memberId)
    {
        var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri($"{_fixture.WsBase}/ws?memberId={memberId}"), CancellationToken.None);
        return ws;
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

    private static async Task SendJsonAsync(ClientWebSocket ws, object payload)
    {
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);
    }
}
