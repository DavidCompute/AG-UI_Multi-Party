using System.Text.Json;

namespace AguiGroupChat.Desktop;

/// <summary>跨窗口未读总数（消息 + 通知中心）。</summary>
public readonly record struct TrayUnreadTotals(int Total, int Messages, int Notifications);

/// <summary>
/// 桌面多窗口未读聚合（1.0.310）：每个 UI 进程把自己的未读数写成
/// <c>%LocalAppData%\AguiGroupChat\unread\unread-&lt;id&gt;.json</c>，各窗口周期读取并求和，
/// 使多个托盘图标 / 任务栏覆盖徽标显示同一个总数。文件只含计数与时间戳，不含知聚名或消息内容。
/// </summary>
public sealed class TrayUnreadAggregator : IDisposable
{
    private const string Prefix = "unread-";
    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan DeleteAfter = TimeSpan.FromMinutes(10);

    private readonly string _dir;
    private readonly string _file;
    private readonly Action<TrayUnreadTotals> _onChanged;
    private readonly TimeSpan _interval;
    private readonly object _gate = new();
    private readonly string _windowId;
    private Timer? _timer;
    private TrayUnreadTotals _last;
    private bool _disposed;

    public TrayUnreadAggregator(
        string directory,
        Action<TrayUnreadTotals> onChanged,
        string? windowId = null,
        TimeSpan? pollInterval = null)
    {
        _dir = directory;
        _onChanged = onChanged;
        _interval = pollInterval ?? TimeSpan.FromSeconds(2);
        _windowId = string.IsNullOrWhiteSpace(windowId) ? Guid.NewGuid().ToString("N") : windowId!;
        _file = Path.Combine(_dir, Prefix + _windowId + ".json");
    }

    /// <summary>开始周期聚合（首次发布 0，避免上一个异常退出残留的计数被误算）。</summary>
    public void Start()
    {
        try { Directory.CreateDirectory(_dir); } catch { /* 忽略 */ }
        Publish(0, 0, 0);
        _timer = new Timer(_ => Poll(), null, _interval, _interval);
    }

    /// <summary>发布本窗口当前未读（覆盖写；失败静默，不阻塞 UI）。</summary>
    public void Publish(int total, int messages, int notifications)
    {
        try
        {
            Directory.CreateDirectory(_dir);
            var entry = new Entry
            {
                Total = Math.Max(0, total),
                Messages = Math.Max(0, messages),
                Notifications = Math.Max(0, notifications),
                UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            };
            var tmp = _file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(entry));
            File.Move(tmp, _file, overwrite: true);
        }
        catch { /* 文件系统不可用：各窗口仍显示自己的未读 */ }
    }

    /// <summary>读取所有新鲜文件并求和；超过 2 分钟的条目视为陈旧（进程可能崩溃），超过 10 分钟顺手删除。</summary>
    public TrayUnreadTotals ReadTotals()
    {
        int total = 0, messages = 0, notifications = 0;
        var now = DateTimeOffset.UtcNow;
        try
        {
            if (!Directory.Exists(_dir)) return default;
            foreach (var path in Directory.EnumerateFiles(_dir, Prefix + "*.json"))
            {
                try
                {
                    var entry = JsonSerializer.Deserialize<Entry>(File.ReadAllText(path));
                    if (entry is null) continue;
                    var updated = DateTimeOffset.FromUnixTimeSeconds(entry.UpdatedAt);
                    var age = now - updated;
                    if (age > DeleteAfter)
                    {
                        try { File.Delete(path); } catch { /* 忽略 */ }
                        continue;
                    }
                    if (age > StaleAfter) continue;
                    total += Math.Max(0, entry.Total);
                    messages += Math.Max(0, entry.Messages);
                    notifications += Math.Max(0, entry.Notifications);
                }
                catch { /* 单个文件损坏：跳过 */ }
            }
        }
        catch { /* 目录不可读：返回 0 */ }
        return new TrayUnreadTotals(total, messages, notifications);
    }

    private void Poll()
    {
        if (_disposed) return;
        var totals = ReadTotals();
        lock (_gate)
        {
            if (totals == _last) return;
            _last = totals;
        }
        try { _onChanged(totals); } catch { /* 窗口已关闭 */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _timer?.Dispose(); } catch { /* 忽略 */ }
        _timer = null;
        try { File.Delete(_file); } catch { /* 忽略 */ }
    }

    private sealed class Entry
    {
        public int Total { get; set; }
        public int Messages { get; set; }
        public int Notifications { get; set; }
        public long UpdatedAt { get; set; }
    }
}
