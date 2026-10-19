using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AguiGroupChat.Hub.Models;
using AguiGroupChat.Web;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>
/// 出站 Webhook（对外集成）测试：订阅过滤（事件类型 / 群 / 噪声排除）、投递载荷与 HMAC 签名、
/// 失败重试到位、测试投递。
/// </summary>
public sealed class WebhookTests
{
    /// <summary>可记录请求并按键次返回指定响应的假 HTTP 处理器。</summary>
    private sealed class RecordingHandler(Func<int, HttpResponseMessage> responder) : HttpMessageHandler
    {
        private readonly Func<int, HttpResponseMessage> _responder = responder;
        public List<(HttpRequestMessage Request, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (Requests) Requests.Add((request, body));
            return _responder(Requests.Count);
        }
    }

    private static WebhookDispatcher Create(RecordingHandler handler)
        => new(new WebhookState(), null, NullLogger<WebhookDispatcher>.Instance, new HttpClient(handler));

    private static WebhookSubscription Sub(params string[] events) => new()
    {
        Id = "wh_1",
        Name = "test",
        Url = "https://example.com/hook",
        Secret = "whsec_test",
        EventTypes = [.. events],
    };

    [Fact]
    public void Sign_MatchesIndependentHmac()
    {
        const long ts = 1_700_000_000_000;
        const string body = "{\"a\":1}";
        var sig = WebhookDispatcher.Sign("whsec_test", ts, body);

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes("whsec_test"));
        var expected = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes($"{ts}.{body}"))).ToLowerInvariant();
        Assert.Equal(expected, sig);
    }

    [Fact]
    public void ShouldDeliver_RespectsEventTypeFilter()
    {
        var sub = Sub(EventTypes.TextMessageEnd);
        Assert.True(WebhookDispatcher.ShouldDeliver(sub, "group_1", EventTypes.TextMessageEnd));
        Assert.False(WebhookDispatcher.ShouldDeliver(sub, "group_1", EventTypes.TextMessageStart));
    }

    [Fact]
    public void ShouldDeliver_ExcludesStreamingNoise_WhenNoExplicitTypes()
    {
        var sub = Sub(); // 空 = 全部「非噪声」事件
        Assert.False(WebhookDispatcher.ShouldDeliver(sub, "group_1", EventTypes.TextMessageContent));
        Assert.False(WebhookDispatcher.ShouldDeliver(sub, "group_1", EventTypes.TextMessageReasoning));
        Assert.False(WebhookDispatcher.ShouldDeliver(sub, "group_1", EventTypes.GroupTyping));
        Assert.False(WebhookDispatcher.ShouldDeliver(sub, "group_1", EventTypes.GroupMessageRead));
        Assert.True(WebhookDispatcher.ShouldDeliver(sub, "group_1", EventTypes.TextMessageEnd));
    }

    [Fact]
    public void ShouldDeliver_RespectsGroupFilter()
    {
        var sub = Sub(EventTypes.TextMessageEnd);
        sub.GroupId = "group_A";
        Assert.True(WebhookDispatcher.ShouldDeliver(sub, "group_A", EventTypes.TextMessageEnd));
        Assert.False(WebhookDispatcher.ShouldDeliver(sub, "group_B", EventTypes.TextMessageEnd));
    }

    [Fact]
    public void EventTypeOf_ReadsTypeProperty()
    {
        object evt = new TextMessageEndEvent { MessageId = "m", GroupId = "g", Timestamp = 1 };
        Assert.Equal(EventTypes.TextMessageEnd, WebhookDispatcher.EventTypeOf(evt));
    }

    [Fact]
    public void QuotaThreshold_IsInEventCatalog_AndUserScopeSkipsGroupScopedSubscriptions()
    {
        Assert.Contains(EventTypes.QuotaThreshold, WebhookApi.EventCatalog);

        // 个人作用域传空 groupId：限定知聚的订阅收不到，未限定的收得到
        var scoped = Sub(EventTypes.QuotaThreshold);
        scoped.GroupId = "group_A";
        var any = Sub(EventTypes.QuotaThreshold);
        Assert.False(WebhookDispatcher.ShouldDeliver(scoped, "", EventTypes.QuotaThreshold));
        Assert.True(WebhookDispatcher.ShouldDeliver(any, "", EventTypes.QuotaThreshold));
    }

    [Fact]
    public async Task DeliverEventAsync_QuotaThresholdEnvelope()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var dispatcher = Create(handler);
        var sub = Sub(EventTypes.QuotaThreshold);
        var evt = new QuotaThresholdEvent
        {
            Scope = "group", ScopeId = "group_1", Level = "over",
            Used = 1200, Quota = 1000, Date = "2026-01-01", Timestamp = 1,
        };

        var record = await dispatcher.DeliverEventAsync(sub, EventTypes.QuotaThreshold, "group_1", evt);

        Assert.True(record.Ok);
        var (request, body) = Assert.Single(handler.Requests);
        Assert.Equal(EventTypes.QuotaThreshold, request.Headers.GetValues("X-KnowGath-Event").Single());
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(EventTypes.QuotaThreshold, doc.RootElement.GetProperty("event").GetString());
        var data = doc.RootElement.GetProperty("data");
        Assert.Equal("group", data.GetProperty("scope").GetString());
        Assert.Equal("over", data.GetProperty("level").GetString());
        Assert.Equal(1200, data.GetProperty("used").GetInt64());
        Assert.Equal(1000, data.GetProperty("quota").GetInt64());
    }

    [Fact]
    public async Task DeliverEventAsync_PostsSignedEnvelope()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var dispatcher = Create(handler);
        var sub = Sub(EventTypes.TextMessageEnd);
        var evt = new TextMessageEndEvent { MessageId = "msg_1", GroupId = "group_1", Timestamp = 123 };

        var record = await dispatcher.DeliverEventAsync(sub, EventTypes.TextMessageEnd, "group_1", evt);

        Assert.True(record.Ok);
        Assert.Equal(200, record.StatusCode);
        Assert.Equal(1, record.Attempt);
        var (request, body) = Assert.Single(handler.Requests);

        Assert.Equal(EventTypes.TextMessageEnd, request.Headers.GetValues("X-KnowGath-Event").Single());
        Assert.False(string.IsNullOrEmpty(request.Headers.GetValues("X-KnowGath-Delivery").Single()));
        var ts = long.Parse(request.Headers.GetValues("X-KnowGath-Timestamp").Single());
        var signature = request.Headers.GetValues("X-KnowGath-Signature").Single();
        Assert.Equal("sha256=" + WebhookDispatcher.Sign("whsec_test", ts, body), signature);

        using var doc = JsonDocument.Parse(body);
        Assert.Equal(EventTypes.TextMessageEnd, doc.RootElement.GetProperty("event").GetString());
        Assert.Equal("group_1", doc.RootElement.GetProperty("groupId").GetString());
        Assert.Equal("msg_1", doc.RootElement.GetProperty("data").GetProperty("messageId").GetString());
        Assert.Equal(EventTypes.TextMessageEnd, doc.RootElement.GetProperty("data").GetProperty("type").GetString());
    }

    [Fact]
    public async Task DeliverEventAsync_RetriesUntilSuccess()
    {
        var handler = new RecordingHandler(count => new HttpResponseMessage(count < 3 ? HttpStatusCode.InternalServerError : HttpStatusCode.OK));
        using var dispatcher = Create(handler);
        dispatcher.RetryBackoffSeconds = [0, 0]; // 测试免等待
        var sub = Sub(EventTypes.TextMessageEnd);
        var evt = new TextMessageEndEvent { MessageId = "m", GroupId = "group_1", Timestamp = 1 };

        var record = await dispatcher.DeliverEventAsync(sub, EventTypes.TextMessageEnd, "group_1", evt);

        Assert.True(record.Ok);
        Assert.Equal(3, record.Attempt);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task DeliverEventAsync_GivesUpAfterMaxAttempts()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.BadGateway));
        using var dispatcher = Create(handler);
        dispatcher.RetryBackoffSeconds = [0, 0];
        var sub = Sub(EventTypes.TextMessageEnd);
        var evt = new TextMessageEndEvent { MessageId = "m", GroupId = "group_1", Timestamp = 1 };

        var record = await dispatcher.DeliverEventAsync(sub, EventTypes.TextMessageEnd, "group_1", evt);

        Assert.False(record.Ok);
        Assert.Equal(3, record.Attempt);
        Assert.Equal(502, record.StatusCode);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task SendTestAsync_SendsPingEnvelope()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        using var dispatcher = Create(handler);
        var sub = Sub(EventTypes.TextMessageEnd);

        var record = await dispatcher.SendTestAsync(sub, CancellationToken.None);

        Assert.True(record.Ok);
        var (request, body) = Assert.Single(handler.Requests);
        Assert.Equal("PING", request.Headers.GetValues("X-KnowGath-Event").Single());
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("PING", doc.RootElement.GetProperty("event").GetString());
        Assert.True(doc.RootElement.GetProperty("data").TryGetProperty("message", out _));
    }

    [Fact]
    public void GenerateSecret_HasPrefixAndEntropy()
    {
        var s = WebhookApi.GenerateSecret();
        Assert.StartsWith("whsec_", s);
        Assert.True(s.Length > 20);
    }
}
