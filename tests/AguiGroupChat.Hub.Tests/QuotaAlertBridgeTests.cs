using System.Net;
using AguiGroupChat.Hub.Agents;
using AguiGroupChat.Hub.Models;
using AguiGroupChat.Hub.Storage;
using AguiGroupChat.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>配额阈值 → Webhook 桥接（1.0.270）：回调真的经既有投递管线发出 QUOTA_THRESHOLD。</summary>
public sealed class QuotaAlertBridgeTests
{
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (Bodies) Bodies.Add(body);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task WireQuotaAlerts_DeliversQuotaThresholdEvent()
    {
        var handler = new RecordingHandler();
        var state = new WebhookState();
        state.Add(new WebhookSubscription
        {
            Id = "wh_q",
            Name = "quota",
            Url = "https://example.com/hook",
            Secret = "whsec_q",
            EventTypes = [EventTypes.QuotaThreshold],
        });
        // 由 provider 负责释放（Dispose 非幂等，不要重复 using）
        var dispatcher = new WebhookDispatcher(state, null, NullLogger<WebhookDispatcher>.Instance, new HttpClient(handler));
        dispatcher.Start();
        var usage = new AgentUsageService(new InMemoryUsageStore(), () => 100, () => 0, NullLogger<AgentUsageService>.Instance);

        var services = new ServiceCollection();
        services.AddSingleton(usage);
        services.AddSingleton(dispatcher);
        using var provider = services.BuildServiceProvider();
        provider.WireQuotaAlerts();

        usage.RecordUsage("a", "u1", "", 90, 0, 0); // 90 / 100 → warn

        // 投递在后台线程：最多等 3 秒
        for (var i = 0; i < 30 && handler.Bodies.Count == 0; i++) await Task.Delay(100);

        var body = Assert.Single(handler.Bodies);
        Assert.Contains(EventTypes.QuotaThreshold, body);
        Assert.Contains("\"warn\"", body);
        Assert.Contains("\"user\"", body);
        Assert.Contains("\"u1\"", body);
    }

    [Fact]
    public void WireQuotaAlerts_NoOpWithoutDispatcher()
    {
        var usage = new AgentUsageService(new InMemoryUsageStore(), () => 100, () => 0, NullLogger<AgentUsageService>.Instance);
        var services = new ServiceCollection();
        services.AddSingleton(usage);
        using var provider = services.BuildServiceProvider();

        // 精简宿主没有 WebhookDispatcher：不抛、不设置回调（保持零开销）
        provider.WireQuotaAlerts();
        Assert.Null(usage.OnQuotaThreshold);
    }
}
