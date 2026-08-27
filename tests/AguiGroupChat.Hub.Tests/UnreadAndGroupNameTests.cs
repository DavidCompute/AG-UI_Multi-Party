using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AguiGroupChat.Hub.Storage;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>
/// 群列表活跃度 / 未读提示（读位点落库 + 已读回执清零）、群名自动生成、头像附件访问放行。
/// </summary>
public sealed class UnreadAndGroupNameTests : IClassFixture<AgentApiServerFixture>
{
    private readonly AgentApiServerFixture _fixture;
    private readonly HttpClient _client;

    public UnreadAndGroupNameTests(AgentApiServerFixture fixture)
    {
        _fixture = fixture;
        _client = new HttpClient { BaseAddress = new Uri(fixture.HttpBase) };
    }

    [Fact]
    public async Task GroupList_ReturnsActivityAndUnread_ReadReceiptClears()
    {
        var (token, userId) = await RegisterUserAsync("unread_u1");
        var gid = await CreateGroupAsync(token, "未读测试群");

        // 发一条消息 → 群列表应显示 lastMessageAt 与未读 1
        var send = await _client.PostAsJsonAsync("/ag-ui/group/message/send", new { groupId = gid, userId, content = "你好" });
        send.EnsureSuccessStatusCode();
        var msgId = (await send.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("messageId").GetString()!;

        var list = await GroupsOfAsync(token, userId);
        var g = Assert.Single(list, x => x.GetProperty("groupId").GetString() == gid);
        Assert.True(g.GetProperty("lastMessageAt").GetInt64() > 0);
        Assert.Equal(1, g.GetProperty("unreadCount").GetInt32());
        Assert.Equal(1, g.GetProperty("unreadByTopic").GetProperty("main").GetInt32());

        // 已读回执（带消息 ID）→ 读位点落库，未读清零
        var read = await _client.PostAsJsonAsync("/ag-ui/group/message/read", new { groupId = gid, memberId = userId, readMessageId = msgId });
        read.EnsureSuccessStatusCode();

        var list2 = await GroupsOfAsync(token, userId);
        var g2 = Assert.Single(list2, x => x.GetProperty("groupId").GetString() == gid);
        Assert.Equal(0, g2.GetProperty("unreadCount").GetInt32());
    }

    [Fact]
    public async Task GroupName_GenerateWithMembers_ReturnsName()
    {
        var token = await RegisterAsync("gn_u1");
        using var req = new HttpRequestMessage(HttpMethod.Post, "/ag-ui/group/generate-name")
        {
            Content = JsonContent.Create(new { memberNames = new[] { "张三", "李四", "需求助手" } }),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var res = await _client.SendAsync(req);
        res.EnsureSuccessStatusCode();
        var name = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("groupName").GetString()!;
        Assert.InRange(name.Length, 6, 12); // mock 确定性：张三李四协作群（7 字）
        Assert.DoesNotContain("\n", name);
    }

    [Fact]
    public async Task GroupName_NoMembers_Returns400()
    {
        var token = await RegisterAsync("gn_u2");
        using var req = new HttpRequestMessage(HttpMethod.Post, "/ag-ui/group/generate-name")
        {
            Content = JsonContent.Create(new { memberNames = new string[] { } }),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.SendAsync(req)).StatusCode);
    }

    [Fact]
    public async Task GroupName_WithoutToken_Returns401()
    {
        var res = await _client.PostAsJsonAsync("/ag-ui/group/generate-name", new { memberNames = new[] { "张三" } });
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    /// <summary>话题未读：话题内新消息计入该话题未读（main 不计），供前端话题栏徽标提示。</summary>
    [Fact]
    public async Task TopicUnread_CountedByTopic_InGroupList()
    {
        var (token, userId) = await RegisterUserAsync("tunread_u1");
        var gid = await CreateGroupAsync(token, "话题未读群");

        // 建话题并在话题内发消息
        var topic = await _client.SendAsync(AuthMessage(HttpMethod.Post, "/ag-ui/group/topic/create", token,
            new { groupId = gid, name = "需求评审", operatorId = userId }));
        topic.EnsureSuccessStatusCode();
        var topicId = (await topic.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("topicId").GetString()!;

        var send = await _client.PostAsJsonAsync("/ag-ui/group/message/send",
            new { groupId = gid, topicId, userId, content = "话题内消息" });
        send.EnsureSuccessStatusCode();
        var msgId = (await send.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("messageId").GetString()!;

        var list = await GroupsOfAsync(token, userId);
        var g = Assert.Single(list, x => x.GetProperty("groupId").GetString() == gid);
        Assert.Equal(1, g.GetProperty("unreadCount").GetInt32()); // 主话题 0 + 话题 1
        Assert.Equal(0, g.GetProperty("unreadByTopic").GetProperty("main").GetInt32());
        Assert.Equal(1, g.GetProperty("unreadByTopic").GetProperty(topicId).GetInt32());

        // 已读该话题消息 → 该话题未读清零
        var read = await _client.PostAsJsonAsync("/ag-ui/group/message/read",
            new { groupId = gid, memberId = userId, readMessageId = msgId });
        read.EnsureSuccessStatusCode();
        var list2 = await GroupsOfAsync(token, userId);
        var g2 = Assert.Single(list2, x => x.GetProperty("groupId").GetString() == gid);
        Assert.Equal(0, g2.GetProperty("unreadByTopic").GetProperty(topicId).GetInt32());
    }

    /// <summary>头像附件：未设为任何用户/智能体头像时 403；设为用户头像后已登录用户可访问（GET /files 放行）。</summary>
    [Fact]
    public async Task AvatarAttachment_BlockedUntilAssignedAsAvatar()
    {
        var (token, userId) = await RegisterUserAsync("av_u1");
        var (url, attachmentId) = await UploadPngAsync(token, "avatar.png");

        // 未作为头像：不属于任何群消息附件 → 403
        var before = await _client.SendAsync(AuthMessage(HttpMethod.Get, $"{url}?token={Uri.EscapeDataString(token)}", token));
        Assert.Equal(HttpStatusCode.Forbidden, before.StatusCode);

        // 设为用户头像 → 放行（头像用于群成员 / 消息渲染）
        var profile = await _client.SendAsync(AuthMessage(HttpMethod.Put, "/ag-ui/user/profile", token,
            new { nickname = "头像用户", avatar = url }));
        profile.EnsureSuccessStatusCode();

        var after = await _client.SendAsync(AuthMessage(HttpMethod.Get, $"{url}?token={Uri.EscapeDataString(token)}", token));
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);

        // 他人登录也能访问该头像附件（群里要渲染我的头像）
        var (token2, _) = await RegisterUserAsync("av_u2");
        var asOther = await _client.SendAsync(AuthMessage(HttpMethod.Get, $"{url}?token={Uri.EscapeDataString(token2)}", token2));
        Assert.Equal(HttpStatusCode.OK, asOther.StatusCode);
        Assert.NotNull(attachmentId);
    }

    /// <summary>群头像附件：设为群头像后已登录用户可访问（群列表 / 群设置预览渲染）。</summary>
    [Fact]
    public async Task GroupAvatarAttachment_AccessibleAfterAssignedAsGroupAvatar()
    {
        var (token, userId) = await RegisterUserAsync("gav_u1");
        var (url, _) = await UploadPngAsync(token, "group.png");
        var gid = await CreateGroupAsync(token, "头像群");

        // 群头像未设置时访问 → 403（不属于任何群消息附件）
        var before = await _client.SendAsync(AuthMessage(HttpMethod.Get, $"{url}?token={Uri.EscapeDataString(token)}", token));
        Assert.Equal(HttpStatusCode.Forbidden, before.StatusCode);

        // 群主更新群头像 → 放行（登录用户即可访问）
        var upd = await _client.SendAsync(AuthMessage(HttpMethod.Post, "/ag-ui/group/update", token,
            new { groupId = gid, operatorId = userId, updateFields = new[] { "groupAvatar" }, groupInfo = new { groupAvatar = url } }));
        upd.EnsureSuccessStatusCode();

        var after = await _client.SendAsync(AuthMessage(HttpMethod.Get, $"{url}?token={Uri.EscapeDataString(token)}", token));
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
    }

    // ================= 辅助 =================

    private async Task<(string Token, string UserId)> RegisterUserAsync(string username)
    {
        var res = await _client.PostAsJsonAsync("/ag-ui/user/register", new { username, password = "secret1", nickname = username });
        res.EnsureSuccessStatusCode();
        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        return (json.GetProperty("token").GetString()!, json.GetProperty("userId").GetString()!);
    }

    private async Task<string> RegisterAsync(string username)
        => (await RegisterUserAsync(username)).Token;

    private async Task<string> CreateGroupAsync(string token, string groupName)
    {
        var res = await _client.SendAsync(AuthMessage(HttpMethod.Post, "/ag-ui/group/create", token,
            new { groupName, ownerId = (string?)null, memberIds = new string[] { }, members = new object[] { } }));
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("groupId").GetString()!;
    }

    private async Task<JsonElement[]> GroupsOfAsync(string token, string memberId)
    {
        var res = await _client.SendAsync(AuthMessage(HttpMethod.Get, $"/ag-ui/member/{memberId}/groups", token));
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadFromJsonAsync<JsonElement[]>() ?? [];
    }

    private async Task<(string Url, string? AttachmentId)> UploadPngAsync(string token, string fileName)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }), "file", fileName);
        using var req = new HttpRequestMessage(HttpMethod.Post, "/ag-ui/upload") { Content = form };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var res = await _client.SendAsync(req);
        res.EnsureSuccessStatusCode();
        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        var url = json.GetProperty("attachments")[0].GetProperty("url").GetString()!;
        var id = url.Split('/')[^2];
        return (url, id.StartsWith("att_") ? id : null);
    }

    private static HttpRequestMessage AuthMessage(HttpMethod method, string url, string token, object? body = null)
    {
        var msg = new HttpRequestMessage(method, url);
        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) msg.Content = JsonContent.Create(body);
        return msg;
    }
}

/// <summary>InMemory 读位点 / 未读计数单元测试（含撤回过滤）。</summary>
public sealed class InMemoryReadStateTests
{
    [Fact]
    public void ReadState_Unread_And_LastMessageAt()
    {
        var store = new InMemoryGroupStore();
        store.AddGroup(new Models.Group { GroupId = "g1", GroupName = "群", OwnerId = "u1", MemberCount = 0, CreateTime = 1 });
        Assert.Null(store.LastMessageAt("g1"));
        Assert.Equal(0, store.GetReadAt("u1", "g1", "main"));

        store.AddMessage(new Models.GroupMessage
        {
            MessageId = "m1", GroupId = "g1", ThreadId = "t", TopicId = "main",
            SenderId = "u1", SenderType = Models.MemberType.User, SenderNickname = "u",
            Content = "a", Timestamp = 100,
        });
        store.AddMessage(new Models.GroupMessage
        {
            MessageId = "m2", GroupId = "g1", ThreadId = "t", TopicId = "main",
            SenderId = "u1", SenderType = Models.MemberType.User, SenderNickname = "u",
            Content = "b", Timestamp = 200,
        });
        Assert.Equal(200, store.LastMessageAt("g1"));
        Assert.Equal(2, store.CountUnread("g1", null, 0));
        Assert.Equal(2, store.CountUnread("g1", "main", 0));

        // 已读位点推进到 100 → 只剩 m2 未读
        store.SetReadAt("u1", "g1", "main", 100);
        Assert.Equal(100, store.GetReadAt("u1", "g1", "main"));
        Assert.Equal(1, store.CountUnread("g1", "main", 100));

        // 撤回的消息不计数
        store.RecallMessage("g1", "m2");
        Assert.Equal(0, store.CountUnread("g1", "main", 100));
    }
}
