using AguiGroupChat.Desktop;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>桌面多窗口未读聚合：求和、清除自身、忽略陈旧文件。</summary>
public sealed class TrayUnreadAggregatorTests
{
    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "agui-unread-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Aggregate_SumsWindows()
    {
        var dir = NewDir();
        try
        {
            var a = new TrayUnreadAggregator(dir, _ => { }, "win_a");
            var b = new TrayUnreadAggregator(dir, _ => { }, "win_b");
            a.Publish(2, 1, 1);
            b.Publish(3, 2, 1);

            var totals = a.ReadTotals();
            Assert.Equal(5, totals.Total);
            Assert.Equal(3, totals.Messages);
            Assert.Equal(2, totals.Notifications);
            b.Dispose();
            a.Dispose();
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* 忽略 */ }
        }
    }

    [Fact]
    public void Dispose_RemovesOwnFile()
    {
        var dir = NewDir();
        try
        {
            var a = new TrayUnreadAggregator(dir, _ => { }, "win_a");
            a.Publish(4, 4, 0);
            Assert.Single(Directory.GetFiles(dir, "unread-*.json"));
            a.Dispose();
            Assert.Empty(Directory.GetFiles(dir, "unread-*.json"));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* 忽略 */ }
        }
    }

    [Fact]
    public void Aggregate_IgnoresStaleFile()
    {
        var dir = NewDir();
        try
        {
            var stale = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds();
            File.WriteAllText(
                Path.Combine(dir, "unread-stale.json"),
                $"{{\"Total\":9,\"Messages\":9,\"Notifications\":0,\"UpdatedAt\":{stale}}}");

            var a = new TrayUnreadAggregator(dir, _ => { }, "win_a");
            a.Publish(1, 0, 1);
            var totals = a.ReadTotals();
            Assert.Equal(1, totals.Total);
            Assert.Equal(1, totals.Notifications);
            a.Dispose();
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* 忽略 */ }
        }
    }
}
