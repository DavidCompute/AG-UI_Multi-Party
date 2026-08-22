using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AguiGroupChat.Hub.Models;
using AguiGroupChat.Hub.Storage;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>
/// 消息历史分页：InMemoryGroupStore.MessagesBefore 单测 + HTTP 分页端点集成测试
/// （前端虚拟滚动「加载更早消息」依赖这两层）。
/// </summary>
public sealed class MessageHistoryTests
{
    // ================= InMemoryGroupStore.MessagesBefore 单测 =================

    private static GroupMessage Msg(string gid, int seq) => new()
    {
        MessageId = $"msg_{seq:D3}",
        GroupId = gid,
        ThreadId = $"thread_{gid}",
        SenderId = "user_1",
        SenderType = MemberType.User,
        SenderNickname = "张三",
        Content = $"内容 {seq}",
        Timestamp = 1000 + seq,
    };

    private static InMemoryGroupStore SeedStore(string gid, int count)
    {
        var store = new InMemoryGroupStore(200);
        for (var i = 0; i < count; i++) store.AddMessage(Msg(gid, i));
        return store;
    }

    [Fact]
    public void MessagesBefore_ReturnsCountMessagesStrictlyBeforeCursor_InChronologicalOrder()
    {
        var store = SeedStore("g", 10);
        var page = store.MessagesBefore("g", "msg_006", 3);
        Assert.Equal(new[] { "msg_003", "msg_004", "msg_005" }, page.Select(m => m.MessageId).ToArray());
    }

    [Fact]
    public void MessagesBefore_CursorIsFirstMessage_ReturnsEmpty()
    {
        var store = SeedStore("g", 10);
        Assert.Empty(store.MessagesBefore("g", "msg_000", 5));
    }

    [Fact]
    public void MessagesBefore_UnknownCursor_ReturnsEmpty()
    {
        var store = SeedStore("g", 10);
        Assert.Empty(store.MessagesBefore("g", "msg_missing", 5));
    }

    [Fact]
    public void MessagesBefore_NullCursor_FallsBackToRecent()
    {
        var store = SeedStore("g", 10);
        var page = store.MessagesBefore("g", null, 3);
        Assert.Equal(new[] { "msg_007", "msg_008", "msg_009" }, page.Select(m => m.MessageId).ToArray());
    }

    [Fact]
    public void MessagesBefore_MoreRequestedThanAvailable_ReturnsAllBefore()
    {
        var store = SeedStore("g", 10);
        var page = store.MessagesBefore("g", "msg_006", 100);
        Assert.Equal(
            new[] { "msg_000", "msg_001", "msg_002", "msg_003", "msg_004", "msg_005" },
            page.Select(m => m.MessageId).ToArray());
    }

    [Fact]
    public void MessagesBefore_RequestExceedsHistoryWindow_StopsAtOldest()
    {
        var store = SeedStore("g", 10);
        var page = store.MessagesBefore("g", "msg_003", 50);
        Assert.Equal(new[] { "msg_000", "msg_001", "msg_002" }, page.Select(m => m.MessageId).ToArray());
    }

    [Fact]
    public void MessagesBefore_UnknownGroup_ReturnsEmpty()
    {
        var store = SeedStore("g", 3);
        Assert.Empty(store.MessagesBefore("other", "msg_000", 10));
    }

    // ================= InMemoryGroupStore.MessagesAfter（正向增量）单测 =================

    [Fact]
    public void MessagesAfter_ReturnsMessagesStrictlyAfterCursor_InChronologicalOrder()
    {
        var store = SeedStore("g", 10);
        var page = store.MessagesAfter("g", "msg_004", 3);
        Assert.Equal(new[] { "msg_005", "msg_006", "msg_007" }, page.Select(m => m.MessageId).ToArray());
    }

    [Fact]
    public void MessagesAfter_CursorIsLastMessage_ReturnsEmpty()
    {
        var store = SeedStore("g", 10);
        Assert.Empty(store.MessagesAfter("g", "msg_009", 5));
    }

    [Fact]
    public void MessagesAfter_UnknownCursor_ReturnsEmpty()
    {
        var store = SeedStore("g", 10);
        Assert.Empty(store.MessagesAfter("g", "msg_missing", 5));
    }

    [Fact]
    public void MessagesAfter_MoreRequestedThanAvailable_ReturnsAllAfter()
    {
        var store = SeedStore("g", 10);
        var page = store.MessagesAfter("g", "msg_003", 100);
        Assert.Equal(
            new[] { "msg_004", "msg_005", "msg_006", "msg_007", "msg_008", "msg_009" },
            page.Select(m => m.MessageId).ToArray());
    }

    [Fact]
    public void MessagesAfter_FiltersByTopic()
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
                MessageId = $"topic1_{i}", GroupId = gid, TopicId = "topic_1", ThreadId = "thread_g",
                SenderId = "u", SenderType = MemberType.User, SenderNickname = "n", Content = $"t1 {i}", Timestamp = 100 + i,
            });
        }
        // 游标 main_3 之后：main 话题仅 main_4；topic_1 话题的 t1 消息不计入
        Assert.Equal(new[] { "main_4" }, store.MessagesAfter("g", "main_3", 10, "main").Select(m => m.MessageId).ToArray());
        // 跨话题：同时间序下 topic_1 消息时间戳更大，会被取到（topicId 为空不限制）= main_4 + 5 条 topic1
        Assert.Equal(6, store.MessagesAfter("g", "main_3", 10).Count);
    }

    // ================= HTTP 分页端点集成测试 =================

    public sealed class PaginationEndpointTests : IClassFixture<HubServerFixture>
    {
        private readonly HubServerFixture _fixture;
        private readonly HttpClient _client;

        public PaginationEndpointTests(HubServerFixture fixture)
        {
            _fixture = fixture;
            _client = new HttpClient { BaseAddress = new Uri(fixture.HttpBase) };
        }

        private async Task<(string GroupId, List<string> Ids)> CreateGroupAndSendAsync(int count)
        {
            var create = await _client.PostAsJsonAsync("/ag-ui/group/create", new
            {
                groupName = "分页测试群",
                ownerId = "user_p1",
            });
            create.EnsureSuccessStatusCode();
            var groupId = (await create.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("groupId").GetString()!;

            var ids = new List<string>();
            for (var i = 0; i < count; i++)
            {
                var send = await _client.PostAsJsonAsync("/ag-ui/group/message/send", new
                {
                    groupId,
                    userId = "user_p1",
                    content = $"消息 {i:00}",
                });
                send.EnsureSuccessStatusCode();
                var msg = await send.Content.ReadFromJsonAsync<JsonElement>();
                ids.Add(msg.GetProperty("messageId").GetString()!);
            }
            return (groupId, ids);
        }

        private static string[] IdsOf(List<JsonElement> page)
            => page.Select(m => m.GetProperty("messageId").GetString()!).ToArray();

        [Fact]
        public async Task MessagesEndpoint_PaginatesBeforeCursor()
        {
            var (groupId, ids) = await CreateGroupAndSendAsync(60);

            var res = await _client.GetAsync($"/ag-ui/group/{groupId}/messages?memberId=user_p1&before={ids[30]}&count=20");
            res.EnsureSuccessStatusCode();
            var page = await res.Content.ReadFromJsonAsync<List<JsonElement>>();

            Assert.Equal(20, page!.Count);
            Assert.Equal(ids.GetRange(10, 20), IdsOf(page)); // 严格在游标之前、按时间序
        }

        [Fact]
        public async Task MessagesEndpoint_BeforeFirstMessage_ReturnsEmpty()
        {
            var (groupId, ids) = await CreateGroupAndSendAsync(10);
            var res = await _client.GetAsync($"/ag-ui/group/{groupId}/messages?memberId=user_p1&before={ids[0]}&count=50");
            res.EnsureSuccessStatusCode();
            var page = await res.Content.ReadFromJsonAsync<List<JsonElement>>();
            Assert.Empty(page!);
        }

        [Fact]
        public async Task MessagesEndpoint_DefaultCount_ReturnsUpTo50()
        {
            var (groupId, ids) = await CreateGroupAndSendAsync(60);
            var res = await _client.GetAsync($"/ag-ui/group/{groupId}/messages?memberId=user_p1&before={ids[^1]}");
            res.EnsureSuccessStatusCode();
            var page = await res.Content.ReadFromJsonAsync<List<JsonElement>>();
            Assert.Equal(50, page!.Count);
            Assert.Equal(ids.GetRange(9, 50), IdsOf(page));
        }

        [Fact]
        public async Task MessagesEndpoint_CountClampedTo100()
        {
            var (groupId, ids) = await CreateGroupAndSendAsync(60);
            var res = await _client.GetAsync($"/ag-ui/group/{groupId}/messages?memberId=user_p1&before={ids[30]}&count=1000");
            res.EnsureSuccessStatusCode();
            var page = await res.Content.ReadFromJsonAsync<List<JsonElement>>();
            Assert.Equal(30, page!.Count); // 游标前仅 30 条，count 上限 100 不影响
        }

        [Fact]
        public async Task MessagesEndpoint_FiltersRecalled()
        {
            var (groupId, ids) = await CreateGroupAndSendAsync(10);

            var recall = await _client.PostAsJsonAsync("/ag-ui/group/message/recall", new
            {
                groupId,
                messageId = ids[4],
                operatorId = "user_p1",
            });
            recall.EnsureSuccessStatusCode();

            var res = await _client.GetAsync($"/ag-ui/group/{groupId}/messages?memberId=user_p1&before={ids[^1]}&count=20");
            res.EnsureSuccessStatusCode();
            var page = await res.Content.ReadFromJsonAsync<List<JsonElement>>();
            var returned = IdsOf(page!);
            Assert.DoesNotContain(ids[4], returned);
            Assert.Equal(8, returned.Length); // 游标前 9 条候选 - 1 条已撤回
        }

        [Fact]
        public async Task MessagesEndpoint_UnknownGroup_Returns404()
        {
            var res = await _client.GetAsync("/ag-ui/group/group_nope/messages?memberId=user_p1&before=msg_x&count=10");
            Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        }
    }
}
