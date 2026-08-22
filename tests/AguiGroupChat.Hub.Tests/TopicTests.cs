using System.Net.Http.Json;
using System.Text.Json;
using AguiGroupChat.Hub.Infra;
using AguiGroupChat.Hub.Models;
using AguiGroupChat.Hub.Storage;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>
/// 群话题（群聊扩展）：创建话题、消息归属话题、按话题分页过滤。
/// </summary>
public sealed class TopicTests
{
    // ================= 单元测试（Hub + 内存存储） =================

    [Fact]
    public async Task CreateTopic_StoresAndBroadcasts()
    {
        var f = new HubFixture();
        var group = await HubFixture.CreateGroupAsync(f.Hub, "g", "user_1");
        var (conn, inbox) = f.NewConnection("user_1");
        await f.Hub.SubscribeAsync(conn, [group.GroupId]);
        f.Drain(inbox); // ACK + 快照

        var topic = await f.Hub.CreateTopicAsync(new GroupTopicCreateRequest
        {
            GroupId = group.GroupId,
            Name = "需求评审",
            OperatorId = "user_1",
        });

        Assert.StartsWith("topic_", topic.TopicId);
        Assert.Equal("需求评审", topic.Name);
        Assert.Equal("user_1", topic.CreatorId);
        Assert.NotNull(f.Store.GetTopic(group.GroupId, topic.TopicId));
        Assert.Contains(f.Store.ListTopics(group.GroupId), t => t.TopicId == topic.TopicId);

        var evt = f.Drain(inbox).Select(HubFixture.Parse).Single();
        Assert.Equal(EventTypes.GroupTopicCreated, evt.GetProperty("type").GetString());
        Assert.Equal(topic.TopicId, evt.GetProperty("topic").GetProperty("topicId").GetString());
        Assert.Equal("需求评审", evt.GetProperty("topic").GetProperty("name").GetString());
    }

    [Fact]
    public async Task CreateTopic_NonMember_ThrowsPermissionDenied()
    {
        var f = new HubFixture();
        var group = await HubFixture.CreateGroupAsync(f.Hub, "g", "user_1");
        var ex = await Assert.ThrowsAsync<AguiProtocolException>(() =>
            f.Hub.CreateTopicAsync(new GroupTopicCreateRequest { GroupId = group.GroupId, Name = "x", OperatorId = "outsider" }));
        Assert.Equal(ErrorCodes.GroupPermissionDenied, ex.ErrorCode);
    }

    [Fact]
    public async Task CreateTopic_EmptyName_ThrowsBadRequest()
    {
        var f = new HubFixture();
        var group = await HubFixture.CreateGroupAsync(f.Hub, "g", "user_1");
        var ex = await Assert.ThrowsAsync<AguiProtocolException>(() =>
            f.Hub.CreateTopicAsync(new GroupTopicCreateRequest { GroupId = group.GroupId, Name = "  ", OperatorId = "user_1" }));
        Assert.Equal(ErrorCodes.BadRequest, ex.ErrorCode);
    }

    // ================= 删除话题 =================

    [Fact]
    public async Task DeleteTopic_RemovesMessagesAndMemory_AndBroadcasts()
    {
        var f = new HubFixture();
        var group = await HubFixture.CreateGroupAsync(f.Hub, "g", "user_1", "user_2");
        var (conn, inbox) = f.NewConnection("user_2");
        await f.Hub.SubscribeAsync(conn, [group.GroupId]);
        f.Drain(inbox); // ACK + 快照

        var topic = await f.Hub.CreateTopicAsync(new GroupTopicCreateRequest
        { GroupId = group.GroupId, Name = "专项", OperatorId = "user_1" });
        var msg = await f.Hub.SendMessageAsync(new GroupMessageSendRequest
        { GroupId = group.GroupId, TopicId = topic.TopicId, UserId = "user_1", Content = "话题内消息" });
        f.Drain(inbox);

        var ok = await f.Hub.DeleteTopicAsync(new GroupTopicDeleteRequest
        { GroupId = group.GroupId, TopicId = topic.TopicId, OperatorId = "user_1" });
        Assert.True(ok);
        // 话题记录与话题下消息一并删除（聊天记录不保留）
        Assert.Null(f.Store.GetTopic(group.GroupId, topic.TopicId));
        Assert.Null(f.Store.GetMessage(group.GroupId, msg.MessageId));
        // 全群广播 GROUP_TOPIC_DELETED
        var evt = f.Drain(inbox).Select(HubFixture.Parse).Single();
        Assert.Equal(EventTypes.GroupTopicDeleted, evt.GetProperty("type").GetString());
        Assert.Equal(topic.TopicId, evt.GetProperty("topicId").GetString());
    }

    [Fact]
    public async Task DeleteTopic_NonCreatorNonManager_ThrowsPermissionDenied()
    {
        var f = new HubFixture();
        var group = await HubFixture.CreateGroupAsync(f.Hub, "g", "user_1", "user_2");
        var topic = await f.Hub.CreateTopicAsync(new GroupTopicCreateRequest
        { GroupId = group.GroupId, Name = "t", OperatorId = "user_1" });

        var ex = await Assert.ThrowsAsync<AguiProtocolException>(() =>
            f.Hub.DeleteTopicAsync(new GroupTopicDeleteRequest
            { GroupId = group.GroupId, TopicId = topic.TopicId, OperatorId = "user_2" }));
        Assert.Equal(ErrorCodes.GroupPermissionDenied, ex.ErrorCode);
    }

    [Fact]
    public async Task DeleteTopic_Main_ThrowsBadRequest()
    {
        var f = new HubFixture();
        var group = await HubFixture.CreateGroupAsync(f.Hub, "g", "user_1");
        var ex = await Assert.ThrowsAsync<AguiProtocolException>(() =>
            f.Hub.DeleteTopicAsync(new GroupTopicDeleteRequest
            { GroupId = group.GroupId, TopicId = "main", OperatorId = "user_1" }));
        Assert.Equal(ErrorCodes.BadRequest, ex.ErrorCode);
    }

    // ================= 清空话题聊天记录 =================

    [Fact]
    public async Task ClearTopic_RemovesMessagesAndMemory_KeepsTopic_AndBroadcasts()
    {
        var f = new HubFixture();
        var group = await HubFixture.CreateGroupAsync(f.Hub, "g", "user_1", "user_2");
        var (conn, inbox) = f.NewConnection("user_2");
        await f.Hub.SubscribeAsync(conn, [group.GroupId]);
        f.Drain(inbox); // ACK + 快照

        var topic = await f.Hub.CreateTopicAsync(new GroupTopicCreateRequest
        { GroupId = group.GroupId, Name = "专项", OperatorId = "user_1" });
        var msg = await f.Hub.SendMessageAsync(new GroupMessageSendRequest
        { GroupId = group.GroupId, TopicId = topic.TopicId, UserId = "user_1", Content = "话题内消息" });
        f.Drain(inbox);

        var removed = await f.Hub.ClearTopicMessagesAsync(new GroupTopicClearRequest
        { GroupId = group.GroupId, TopicId = topic.TopicId, OperatorId = "user_1" });
        Assert.Equal(1, removed);
        // 话题保留，消息物理删除
        Assert.NotNull(f.Store.GetTopic(group.GroupId, topic.TopicId));
        Assert.Null(f.Store.GetMessage(group.GroupId, msg.MessageId));
        // 全群广播 GROUP_TOPIC_CLEARED
        var evt = f.Drain(inbox).Select(HubFixture.Parse).Single();
        Assert.Equal(EventTypes.GroupTopicCleared, evt.GetProperty("type").GetString());
        Assert.Equal(topic.TopicId, evt.GetProperty("topicId").GetString());
    }

    [Fact]
    public async Task ClearTopic_MainTopic_ClearsMainMessages()
    {
        var f = new HubFixture();
        var group = await HubFixture.CreateGroupAsync(f.Hub, "g", "user_1", "user_2");
        var msg = await f.Hub.SendMessageAsync(new GroupMessageSendRequest
        { GroupId = group.GroupId, UserId = "user_1", Content = "主话题消息" });
        var inTopic = await f.Hub.SendMessageAsync(new GroupMessageSendRequest
        { GroupId = group.GroupId, TopicId = "main", UserId = "user_1", Content = "显式 main" });

        var removed = await f.Hub.ClearTopicMessagesAsync(new GroupTopicClearRequest
        { GroupId = group.GroupId, TopicId = "main", OperatorId = "user_1" });
        Assert.Equal(2, removed);
        Assert.Null(f.Store.GetMessage(group.GroupId, msg.MessageId));
        Assert.Null(f.Store.GetMessage(group.GroupId, inTopic.MessageId));
    }

    [Fact]
    public async Task ClearTopic_NonManager_ThrowsPermissionDenied()
    {
        var f = new HubFixture();
        var group = await HubFixture.CreateGroupAsync(f.Hub, "g", "user_1", "user_2");
        await f.Hub.SendMessageAsync(new GroupMessageSendRequest
        { GroupId = group.GroupId, UserId = "user_1", Content = "x" });

        var ex = await Assert.ThrowsAsync<AguiProtocolException>(() =>
            f.Hub.ClearTopicMessagesAsync(new GroupTopicClearRequest
            { GroupId = group.GroupId, TopicId = "main", OperatorId = "user_2" }));
        Assert.Equal(ErrorCodes.GroupPermissionDenied, ex.ErrorCode);
    }

    [Fact]
    public async Task DeleteTopic_MissingTopic_ThrowsNotFound()
    {
        var f = new HubFixture();
        var group = await HubFixture.CreateGroupAsync(f.Hub, "g", "user_1");
        var ex = await Assert.ThrowsAsync<AguiProtocolException>(() =>
            f.Hub.DeleteTopicAsync(new GroupTopicDeleteRequest
            { GroupId = group.GroupId, TopicId = "topic_ghost", OperatorId = "user_1" }));
        Assert.Equal(ErrorCodes.GroupNotFound, ex.ErrorCode);
    }

    [Fact]
    public async Task SendMessage_WithTopicId_StoresAndBroadcastsTopic()
    {
        var f = new HubFixture();
        var group = await HubFixture.CreateGroupAsync(f.Hub, "g", "user_1");
        var topic = await f.Hub.CreateTopicAsync(new GroupTopicCreateRequest
        { GroupId = group.GroupId, Name = "闲聊", OperatorId = "user_1" });

        var msg = await f.Hub.SendMessageAsync(new GroupMessageSendRequest
        {
            GroupId = group.GroupId,
            TopicId = topic.TopicId,
            UserId = "user_1",
            Content = "hello topic",
        });

        Assert.Equal(topic.TopicId, msg.TopicId);
        var stored = f.Store.GetMessage(group.GroupId, msg.MessageId)!;
        Assert.Equal(topic.TopicId, stored.TopicId);
    }

    [Fact]
    public async Task SendMessage_DefaultTopic_IsMain()
    {
        var f = new HubFixture();
        var group = await HubFixture.CreateGroupAsync(f.Hub, "g", "user_1");
        var msg = await f.Hub.SendMessageAsync(new GroupMessageSendRequest
        { GroupId = group.GroupId, UserId = "user_1", Content = "hi" });
        Assert.Equal("main", msg.TopicId);
    }

    [Fact]
    public async Task SendMessage_UnknownTopic_Throws()
    {
        var f = new HubFixture();
        var group = await HubFixture.CreateGroupAsync(f.Hub, "g", "user_1");
        var ex = await Assert.ThrowsAsync<AguiProtocolException>(() =>
            f.Hub.SendMessageAsync(new GroupMessageSendRequest
            { GroupId = group.GroupId, TopicId = "topic_nope", UserId = "user_1", Content = "x" }));
        Assert.Equal(ErrorCodes.GroupNotFound, ex.ErrorCode);
    }

    [Fact]
    public async Task StartEvent_CarriesTopicId()
    {
        var f = new HubFixture();
        var group = await HubFixture.CreateGroupAsync(f.Hub, "g", "user_1", "user_2");
        var topic = await f.Hub.CreateTopicAsync(new GroupTopicCreateRequest
        { GroupId = group.GroupId, Name = "t", OperatorId = "user_1" });
        var (conn, inbox) = f.NewConnection("user_2");
        await f.Hub.SubscribeAsync(conn, [group.GroupId]);
        f.Drain(inbox);

        await f.Hub.SendMessageAsync(new GroupMessageSendRequest
        { GroupId = group.GroupId, TopicId = topic.TopicId, UserId = "user_1", Content = "x" });

        var start = f.Drain(inbox).Select(HubFixture.Parse)
            .First(e => e.GetProperty("type").GetString() == EventTypes.TextMessageStart);
        Assert.Equal(topic.TopicId, start.GetProperty("topicId").GetString());
    }

    [Fact]
    public void MessagesBefore_FiltersByTopic()
    {
        var store = new InMemoryGroupStore(200);
        var gid = "g";
        for (var i = 0; i < 10; i++)
        {
            store.AddMessage(new GroupMessage
            {
                MessageId = $"main_{i:D2}", GroupId = gid, TopicId = "main", ThreadId = "thread_g",
                SenderId = "user_1", SenderType = MemberType.User, SenderNickname = "张三",
                Content = $"main {i}", Timestamp = 1000 + i,
            });
            store.AddMessage(new GroupMessage
            {
                MessageId = $"topic_{i:D2}", GroupId = gid, TopicId = "topic_a", ThreadId = "thread_g",
                SenderId = "user_1", SenderType = MemberType.User, SenderNickname = "张三",
                Content = $"topic {i}", Timestamp = 2000 + i,
            });
        }

        var page = store.MessagesBefore(gid, "topic_07", 3, "topic_a");
        Assert.Equal(new[] { "topic_04", "topic_05", "topic_06" }, page.Select(m => m.MessageId).ToArray());

        // 游标在另一话题中 → 视为不存在，返回空
        Assert.Empty(store.MessagesBefore(gid, "main_05", 5, "topic_a"));
        // 空游标 → 最近 count 条（话题内）
        var recent = store.MessagesBefore(gid, null, 3, "main");
        Assert.Equal(new[] { "main_07", "main_08", "main_09" }, recent.Select(m => m.MessageId).ToArray());
        // 不传话题 → 全部（向后兼容）
        Assert.Equal(20, store.MessagesBefore(gid, null, 100).Count);
    }

    [Fact]
    public void RecentMessages_FiltersByTopic()
    {
        var store = new InMemoryGroupStore(200);
        var gid = "g";
        for (var i = 0; i < 5; i++)
        {
            store.AddMessage(new GroupMessage
            {
                MessageId = $"main_{i}", GroupId = gid, TopicId = "main", ThreadId = "thread_g",
                SenderId = "u", SenderType = MemberType.User, SenderNickname = "n", Content = $"{i}", Timestamp = i,
            });
            store.AddMessage(new GroupMessage
            {
                MessageId = $"t_{i}", GroupId = gid, TopicId = "t", ThreadId = "thread_g",
                SenderId = "u", SenderType = MemberType.User, SenderNickname = "n", Content = $"{i}", Timestamp = i,
            });
        }
        Assert.Equal(3, store.RecentMessages(gid, 3, "t").Count);
        Assert.All(store.RecentMessages(gid, 3, "t"), m => Assert.Equal("t", m.TopicId));
        Assert.Equal(10, store.RecentMessages(gid, 100).Count); // 不传话题 → 全部
    }

    [Fact]
    public async Task CreateTopic_WithSourceMessage_MovesMessageAndBroadcasts()
    {
        var f = new HubFixture();
        var group = await HubFixture.CreateGroupAsync(f.Hub, "g", "user_1", "user_2");
        var msg = await f.Hub.SendMessageAsync(new GroupMessageSendRequest
        { GroupId = group.GroupId, UserId = "user_1", Content = "这段要单独讨论" });
        Assert.Equal("main", msg.TopicId);

        var (conn, inbox) = f.NewConnection("user_2");
        await f.Hub.SubscribeAsync(conn, [group.GroupId]);
        f.Drain(inbox); // ACK + 快照

        var topic = await f.Hub.CreateTopicAsync(new GroupTopicCreateRequest
        { GroupId = group.GroupId, Name = "专项讨论", OperatorId = "user_1", SourceMessageId = msg.MessageId });

        // 消息已迁移到新话题
        var moved = f.Store.GetMessage(group.GroupId, msg.MessageId)!;
        Assert.Equal(topic.TopicId, moved.TopicId);

        var types = f.Drain(inbox).Select(HubFixture.TypeOf).ToList();
        Assert.Contains(EventTypes.GroupMessageTopicMoved, types);
        Assert.Contains(EventTypes.GroupTopicCreated, types);

        // 新话题内能取到该消息（作为起点）
        var topicRecent = f.Store.RecentMessages(group.GroupId, 50, topic.TopicId);
        Assert.Contains(topicRecent, m => m.MessageId == msg.MessageId);
        // 主话题内不再包含
        var mainRecent = f.Store.RecentMessages(group.GroupId, 50, "main");
        Assert.DoesNotContain(mainRecent, m => m.MessageId == msg.MessageId);
    }

    [Fact]
    public async Task CreateTopic_RecalledSource_Throws()
    {
        var f = new HubFixture();
        var group = await HubFixture.CreateGroupAsync(f.Hub, "g", "user_1");
        var msg = await f.Hub.SendMessageAsync(new GroupMessageSendRequest
        { GroupId = group.GroupId, UserId = "user_1", Content = "x" });
        await f.Hub.RecallMessageAsync(new GroupMessageRecallRequest
        { GroupId = group.GroupId, MessageId = msg.MessageId, OperatorId = "user_1" });

        var ex = await Assert.ThrowsAsync<AguiProtocolException>(() =>
            f.Hub.CreateTopicAsync(new GroupTopicCreateRequest
            { GroupId = group.GroupId, Name = "t", OperatorId = "user_1", SourceMessageId = msg.MessageId }));
        Assert.Equal(ErrorCodes.GroupMessageNotFound, ex.ErrorCode);
    }

    [Fact]
    public async Task CreateTopic_MissingSource_Throws()
    {
        var f = new HubFixture();
        var group = await HubFixture.CreateGroupAsync(f.Hub, "g", "user_1");
        var ex = await Assert.ThrowsAsync<AguiProtocolException>(() =>
            f.Hub.CreateTopicAsync(new GroupTopicCreateRequest
            { GroupId = group.GroupId, Name = "t", OperatorId = "user_1", SourceMessageId = "msg_nope" }));
        Assert.Equal(ErrorCodes.GroupMessageNotFound, ex.ErrorCode);
    }

    // ================= HTTP 集成测试 =================

    public sealed class TopicEndpointTests : IClassFixture<HubServerFixture>
    {
        private readonly HubServerFixture _fixture;
        private readonly HttpClient _client;

        public TopicEndpointTests(HubServerFixture fixture)
        {
            _fixture = fixture;
            _client = new HttpClient { BaseAddress = new Uri(fixture.HttpBase) };
        }

        [Fact]
        public async Task CreateTopic_And_SnapshotCarriesTopics()
        {
            var create = await _client.PostAsJsonAsync("/ag-ui/group/create", new { groupName = "话题群", ownerId = "user_t1" });
            create.EnsureSuccessStatusCode();
            var groupId = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("groupId").GetString()!;

            var created = await _client.PostAsJsonAsync("/ag-ui/group/topic/create", new
            {
                groupId,
                name = "需求评审",
                operatorId = "user_t1",
            });
            created.EnsureSuccessStatusCode();
            var topic = await created.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("需求评审", topic.GetProperty("name").GetString());

            var snapshot = await _client.GetAsync($"/ag-ui/group/{groupId}?memberId=user_t1");
            snapshot.EnsureSuccessStatusCode();
            var snap = await snapshot.Content.ReadFromJsonAsync<JsonElement>();
            var topics = snap.GetProperty("topics");
            Assert.Equal(1, topics.GetArrayLength());
            Assert.Equal("需求评审", topics[0].GetProperty("name").GetString());
        }

        [Fact]
        public async Task TopicMessagesEndpoint_PaginatesByTopic()
        {
            var create = await _client.PostAsJsonAsync("/ag-ui/group/create", new { groupName = "分页群", ownerId = "user_t2" });
            var groupId = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("groupId").GetString()!;
            var topic = (await (await _client.PostAsJsonAsync("/ag-ui/group/topic/create", new
            {
                groupId,
                name = "讨论",
                operatorId = "user_t2",
            })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("topicId").GetString()!;

            // 主话题 3 条 + 目标话题 8 条
            for (var i = 0; i < 3; i++)
            {
                await _client.PostAsJsonAsync("/ag-ui/group/message/send", new { groupId, userId = "user_t2", content = $"main {i}" });
            }
            var topicIds = new List<string>();
            for (var i = 0; i < 8; i++)
            {
                var send = await _client.PostAsJsonAsync("/ag-ui/group/message/send", new { groupId, topicId = topic, userId = "user_t2", content = $"topic {i}" });
                var msg = await send.Content.ReadFromJsonAsync<JsonElement>();
                topicIds.Add(msg.GetProperty("messageId").GetString()!);
            }

            var res = await _client.GetAsync($"/ag-ui/group/{groupId}/topics/{topic}/messages?memberId=user_t2&before={topicIds[6]}&count=3");
            res.EnsureSuccessStatusCode();
            var page = await res.Content.ReadFromJsonAsync<List<JsonElement>>();
            Assert.Equal(3, page!.Count);
            Assert.Equal(topicIds.GetRange(3, 3), page.Select(m => m.GetProperty("messageId").GetString()!).ToArray());
            Assert.All(page, m => Assert.Equal(topic, m.GetProperty("topicId").GetString()));

            // 未知话题 → 404
            var nope = await _client.GetAsync($"/ag-ui/group/{groupId}/topics/topic_nope/messages?memberId=user_t2");
            Assert.Equal(System.Net.HttpStatusCode.NotFound, nope.StatusCode);
        }
    }
}
