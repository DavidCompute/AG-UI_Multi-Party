using System.Collections.Concurrent;
using AguiGroupChat.Hub.Messaging;
using AguiGroupChat.Hub.Models;
using Microsoft.Extensions.Logging;

namespace AguiGroupChat.Web;

/// <summary>
/// 临时放额到期提醒（1.0.274）：周期性扫描知聚的**配额覆写到期时间**，当剩余时间进入
/// <see cref="ReminderLead"/> 窗口时，往出站 Webhook 发一条 <c>QUOTA_OVERRIDE_EXPIRING</c>。
/// <para>
/// 为什么用轮询而不是逐条定时器：到期时间存在群数据的 <c>extra</c> 里，可被管理员随时改 / 取消，
/// 轮询天然跟随最新值；15 分钟的扫描间隔对「提前 24 小时提醒」完全够用，也不需要持久化调度状态。
/// </para>
/// <para>
/// 去重：同一额度（<c>groupId + until</c>）在进程内只发一次。额度被取消 / 到期时间被改写 / 已到期时，
/// 去重键在下一次扫描被清掉——若之后又设回同样的到期时间，会重新提醒。重启后可能再发一次（与
/// <c>QUOTA_THRESHOLD</c> 同约定，告警平台可按 <c>groupId + until</c> 去重）。
/// </para>
/// </summary>
public sealed class QuotaExpiryReminderService : IDisposable
{
    /// <summary>提前多久提醒：24 小时（够管理员安排续期或让其自然到期）。</summary>
    public static readonly TimeSpan ReminderLead = TimeSpan.FromHours(24);

    /// <summary>扫描间隔：15 分钟。</summary>
    public static readonly TimeSpan ScanInterval = TimeSpan.FromMinutes(15);

    private readonly GroupHub _hub;
    private readonly WebhookDispatcher? _dispatcher;
    private readonly ILogger<QuotaExpiryReminderService> _logger;
    private readonly ConcurrentDictionary<string, byte> _notified = new(StringComparer.Ordinal);
    private Timer? _timer;

    public QuotaExpiryReminderService(GroupHub hub, WebhookDispatcher? dispatcher, ILogger<QuotaExpiryReminderService> logger)
    {
        _hub = hub;
        _dispatcher = dispatcher;
        _logger = logger;
    }

    /// <summary>启动周期扫描（应用就绪后调用；重复调用无副作用）。</summary>
    public void Start()
    {
        lock (this)
        {
            if (_timer is not null) return;
            // 首次延迟 1 分钟：避开启动瞬间的持久化 / 迁移工作。
            _timer = new Timer(_ => RunSafely(), null, TimeSpan.FromMinutes(1), ScanInterval);
        }
    }

    public void Stop()
    {
        lock (this) { _timer?.Dispose(); _timer = null; }
    }

    /// <summary>
    /// 执行一次扫描并返回本轮新产生的提醒事件（测试可直接调用）。未注入 dispatcher 时只返回事件、不投递。
    /// </summary>
    public IReadOnlyList<QuotaOverrideExpiringEvent> RunOnce(long nowMs)
    {
        var leadMs = (long)ReminderLead.TotalMilliseconds;
        var emitted = new List<QuotaOverrideExpiringEvent>();
        var live = new HashSet<string>(StringComparer.Ordinal);

        foreach (var g in _hub.Store.AllGroups())
        {
            if (!g.IsQuotaOverrideActive(nowMs)) continue;          // 已到期 / 已取消：不提醒
            if (g.QuotaOverrideUntil is not { } until) continue;    // 长期放额：无到期时间
            var remaining = until - nowMs;
            if (remaining <= 0 || remaining > leadMs) continue;     // 尚未进入提醒窗口

            var key = GroupKey(g.GroupId, until);
            live.Add(key);
            if (!_notified.TryAdd(key, 0)) continue;                // 同一额度只提醒一次

            var evt = new QuotaOverrideExpiringEvent
            {
                GroupId = g.GroupId,
                GroupName = g.GroupName,
                Quota = g.QuotaOverride ?? 0,
                Until = until,
                RemainingSeconds = remaining / 1000,
                Timestamp = nowMs,
            };
            emitted.Add(evt);
            _dispatcher?.OnEvent(g.GroupId, evt);                   // 只有「限定知聚」或全量订阅会收到
        }

        // 清理失效去重键：额度取消 / 到期时间改写 / 已到期 / 知聚解散。
        foreach (var key in _notified.Keys)
            if (!live.Contains(key)) _notified.TryRemove(key, out _);

        return emitted;
    }

    private void RunSafely()
    {
        try
        {
            var emitted = RunOnce(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            if (emitted.Count > 0)
                _logger.LogInformation("临时放额到期提醒：已发出 {Count} 条", emitted.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "临时放额到期提醒扫描异常");
        }
    }

    private static string GroupKey(string groupId, long until) => groupId + "|" + until.ToString();

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
    }
}
