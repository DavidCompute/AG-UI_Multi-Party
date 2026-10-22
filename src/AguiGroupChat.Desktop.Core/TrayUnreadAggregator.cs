using System.Text.Json;

namespace AguiGroupChat.Desktop;

/// <summary>跨窗口未读总数（消息 + 通知中心）。</summary>
public readonly record struct TrayUnreadTotals(int Total, int Messages, int Notifications);

/// <summary>单个知聚的未读条目（仅桌面壳内部使用，不出进程外）。</summary>
public sealed record TrayUnreadGroup(string Id, string Name, int Count);

/// <summary>一个窗口的未读快照（1.0.311：托盘右键菜单可展开「窗口 → 知聚」）。</summary>
/// <param name="Total">该窗口总未读（消息 + 通知）。</param>
/// <param name="Messages">未读消息数。</param>
/// <param name="Notifications">未读通知数（已排除 message 类）。</param>
/// <param name="Label">窗口标识：当前打开的知聚名（未选中知聚时为空）。</param>
/// <param name="Hwnd">该窗口的主窗口句柄（用于把窗口提到前台；0 = 未知）。</param>
/// <param name="IsSelf">是否是本进程自己的条目。</param>
/// <param name="Groups">该窗口内按知聚拆分的未读（最多 6 条，按未读数降序）。</param>
public sealed record TrayUnreadWindow(
    int Total,
    int Messages,
    int Notifications,
    string Label,
    long Hwnd,
    bool IsSelf,
    IReadOnlyList<TrayUnreadGroup> Groups);

/// <summary>
/// 桌面多窗口未读聚合（1.0.310；1.0.311 增加按窗口 / 知聚明细）：每个 UI 进程把自己的未读数写成
/// <c>%LocalAppData%\AguiGroupChat\unread\unread-&lt;id&gt;.json</c>，各窗口周期读取并求和，
/// 使多个托盘图标 / 任务栏覆盖徽标显示同一个总数；托盘菜单可再展开每个窗口的知聚明细。
/// 文件只含计数、窗口句柄与知聚名，不含消息内容。
/// </summary>
public sealed class TrayUnreadAggregator : IDisposable
{
    private const string Prefix = "unread-";
    private const int MaxGroupsPerWindow = 6;
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
        => Publish(total, messages, notifications, null, 0, null);

    /// <summary>发布本窗口当前未读 + 明细（窗口标识 / 句柄 / 各知聚未读）。</summary>
    public void Publish(
        int total,
        int messages,
        int notifications,
        string? label,
        long hwnd,
        IReadOnlyList<TrayUnreadGroup>? groups)
    {
        try
        {
            Directory.CreateDirectory(_dir);
            var entry = new Entry
            {
                Total = Math.Max(0, total),
                Messages = Math.Max(0, messages),
                Notifications = Math.Max(0, notifications),
                Label = string.IsNullOrWhiteSpace(label) ? null : label!.Trim(),
                Hwnd = hwnd,
                UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Groups = groups is null
                    ? []
                    : groups.Where((g) => g.Count > 0)
                        .OrderByDescending((g) => g.Count)
                        .Take(MaxGroupsPerWindow)
                        .Select((g) => new EntryGroup { Id = g.Id, Name = g.Name, Count = g.Count })
                        .ToList(),
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
        foreach (var w in ReadWindows())
        {
            total += w.Total;
            messages += w.Messages;
            notifications += w.Notifications;
        }
        return new TrayUnreadTotals(total, messages, notifications);
    }

    /// <summary>读取所有新鲜窗口的未读快照（含明细），本进程条目 IsSelf=true。按未读降序、自身优先。</summary>
    public IReadOnlyList<TrayUnreadWindow> ReadWindows()
    {
        var now = DateTimeOffset.UtcNow;
        var result = new List<TrayUnreadWindow>();
        try
        {
            if (!Directory.Exists(_dir)) return result;
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
                    var isSelf = string.Equals(Path.GetFullPath(path), Path.GetFullPath(_file),
                        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
                    var groups = (entry.Groups ?? [])
                        .Where((g) => g is not null && g.Count > 0 && !string.IsNullOrWhiteSpace(g.Id))
                        .Select((g) => new TrayUnreadGroup(g.Id!, g.Name ?? "", g.Count))
                        .ToList();
                    result.Add(new TrayUnreadWindow(
                        Math.Max(0, entry.Total),
                        Math.Max(0, entry.Messages),
                        Math.Max(0, entry.Notifications),
                        entry.Label ?? "",
                        entry.Hwnd,
                        isSelf,
                        groups));
                }
                catch { /* 单个文件损坏：跳过 */ }
            }
        }
        catch { /* 目录不可读：返回空 */ }
        return result
            .OrderByDescending((w) => w.IsSelf)
            .ThenByDescending((w) => w.Total)
            .ToList();
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
        public string? Label { get; set; }
        public long Hwnd { get; set; }
        public long UpdatedAt { get; set; }
        public List<EntryGroup?>? Groups { get; set; }
    }

    private sealed class EntryGroup
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public int Count { get; set; }
    }
}
