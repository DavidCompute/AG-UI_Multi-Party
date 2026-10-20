using System.Text.Json;
using AguiGroupChat.Hub.Infra;
using AguiGroupChat.Hub.Persistence;
using AguiGroupChat.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>
/// 投递日志持久化（1.0.292）：最近 100 条投递记录跨重启保持，
/// 顺序（旧→新）与截断（保留最新）在快照 / 恢复两侧一致。
/// </summary>
public sealed class WebhookDeliveryPersistenceTests
{
    private static WebhookDispatcher NewDispatcher()
        => new(new WebhookState(), vault: null, NullLogger<WebhookDispatcher>.Instance);

    private static WebhookDeliveryRecord Rec(int i) => new()
    {
        DeliveryId = "wd_" + i,
        SubscriptionId = "wh_1",
        EventType = i % 2 == 0 ? "GROUP_CREATED" : "IMAGE_WRITE_PENDING",
        Url = "https://example.com/hook",
        Attempt = 1,
        StatusCode = 200,
        Ok = i % 2 == 0,
        AtMs = i,
        DurationMs = i,
        Error = i % 2 == 0 ? null : "HTTP 500",
    };

    [Fact]
    public void RestoreLog_KeepsChronologicalOrder_AndNewestOnCap()
    {
        using var d = NewDispatcher();
        d.RestoreLog(Enumerable.Range(0, WebhookDispatcher.MaxLog + 5).Select(Rec).ToList());

        var snapshot = d.SnapshotLog();
        Assert.Equal(WebhookDispatcher.MaxLog, snapshot.Count); // 超限保留最新
        Assert.Equal("wd_5", snapshot[0].DeliveryId);
        Assert.Equal("wd_104", snapshot[^1].DeliveryId);

        var recent = d.RecentDeliveries(); // 新→旧（界面口径）
        Assert.Equal("wd_104", recent[0].DeliveryId);
        Assert.Equal("wd_5", recent[^1].DeliveryId);

        d.RestoreLog(null); // 空快照不破坏现有内容
        Assert.Equal(WebhookDispatcher.MaxLog, d.RecentDeliveries().Count);
    }

    [Fact]
    public void DeliveryLog_RoundTripsThroughPersistenceSection()
    {
        var store1 = new CaptureSectionStore();
        var d1 = NewDispatcher();
        var sp1 = new ServiceCollection()
            .AddSingleton(d1)
            .AddSingleton<ISectionStore>(store1)
            .BuildServiceProvider();
        sp1.RegisterWebhookDeliveryPersistence();

        d1.RestoreLog([Rec(1), Rec(2), Rec(3)]);
        var json = JsonSerializer.SerializeToElement(store1.Snapshots["webhookDeliveries"](), AguiJson.Options);

        var d2 = NewDispatcher();
        var store2 = new CaptureSectionStore();
        new ServiceCollection()
            .AddSingleton(d2)
            .AddSingleton<ISectionStore>(store2)
            .BuildServiceProvider()
            .RegisterWebhookDeliveryPersistence();

        store2.Restores["webhookDeliveries"](json);

        var restored = d2.RecentDeliveries();
        Assert.Equal(3, restored.Count);
        Assert.Equal("wd_3", restored[0].DeliveryId);
        Assert.Equal("wd_1", restored[2].DeliveryId);
        Assert.Equal("https://example.com/hook", restored[0].Url);
        Assert.False(restored[0].Ok);
        Assert.Equal("HTTP 500", restored[0].Error);
    }

    /// <summary>捕获扩展区注册的回调（无数据库 / 无持久化文件时验证快照 + 恢复语义）。</summary>
    private sealed class CaptureSectionStore : ISectionStore
    {
        public Dictionary<string, Func<object?>> Snapshots { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, Action<JsonElement>> Restores { get; } = new(StringComparer.Ordinal);

        public void AddSection(string name, Func<object?> snapshot, Action<JsonElement> restore)
        {
            Snapshots[name] = snapshot;
            Restores[name] = restore;
        }

        public void LoadSections() { }
        public void Flush() { }
    }
}
