using System.Collections.Concurrent;
using AguiGroupChat.Hub.Messaging;
using AguiGroupChat.Hub.Models;
using AguiGroupChat.Hub.Persistence;
using Microsoft.Extensions.Logging;

namespace AguiGroupChat.Web;

/// <summary>
/// 临时放额到期提醒（1.0.274 / 1.0.275）：周期性扫描知聚的**配额覆写到期时间**，
/// 在剩余时间进入提醒窗口时发 <c>QUOTA_OVERRIDE_EXPIRING</c>，到期后（宽限期内）再发一条
/// <c>QUOTA_OVERRIDE_EXPIRED</c>，形成「即将到期 → 已回退」的完整闭环。
/// <para>
/// 为什么用轮询而不是逐条定时器：到期时间存在群数据的 <c>extra</c> 里，可被管理员随时改 / 取消，
/// 轮询天然跟随最新值；提醒提前量与扫描间隔都可从「配置治理」热改（下一轮扫描生效）。
/// </para>
/// <para>
/// 去重：同一额度（<c>groupId + until</c>）每种事件在进程内只发一次。额度被取消 / 到期时间被改写 /
/// 已到期（对「即将到期」）/ 知聚解散时，去重键在下一次扫描被清掉——若之后又设回同样的到期时间，会重新提醒。
/// 重启后可能再发一次（与 <c>QUOTA_THRESHOLD</c> 同约定，告警平台可按 <c>groupId + until</c> 去重）。
/// </para>
/// </summary>
public sealed class QuotaExpiryReminderService : IDisposable
{
    /// <summary>默认提前多久提醒：24 小时（可由配置治理 `quotaReminderLeadHours` 覆盖）。</summary>
    public const int DefaultLeadHours = 24;

    /// <summary>默认扫描间隔：15 分钟（可由配置治理 `quotaReminderScanMinutes` 覆盖）。</summary>
    public const int DefaultScanMinutes = 15;

    /// <summary>「已到期」事件只在到期后的 24 小时内补发一次；更早的历史过期数据不会在重启时洪水式补发。</summary>
    public static readonly TimeSpan ExpiredGrace = TimeSpan.FromHours(24);

    private readonly GroupHub _hub;
    private readonly ConfigGovernanceState _config;
    private readonly QuotaReminderStateStore _state;
    private readonly ChangeHub _changes;
    private readonly WebhookDispatcher? _dispatcher;
    private readonly ILogger<QuotaExpiryReminderService> _logger;
    private Timer? _timer;
    private bool _started;
    private int _running;

    public QuotaExpiryReminderService(GroupHub hub, ConfigGovernanceState config, QuotaReminderStateStore state,
        ChangeHub changes, WebhookDispatcher? dispatcher, ILogger<QuotaExpiryReminderService> logger)
    {
        _hub = hub;
        _config = config;
        _state = state;
        _changes = changes;
        _dispatcher = dispatcher;
        _logger = logger;
    }

    /// <summary>当前生效的提醒提前量（小时）；非法 / 未配置时回退默认。</summary>
    public int LeadHours
        => _config.QuotaReminderLeadHours is > 0 and <= 720 ? _config.QuotaReminderLeadHours.Value : DefaultLeadHours;

    /// <summary>当前生效的扫描间隔（分钟）；非法 / 未配置时回退默认。</summary>
    public int ScanMinutes
        => _config.QuotaReminderScanMinutes is > 0 and <= 1440 ? _config.QuotaReminderScanMinutes.Value : DefaultScanMinutes;

    /// <summary>启动周期扫描（应用就绪后调用；重复调用无副作用）。</summary>
    public void Start()
    {
        lock (this)
        {
            if (_started) return;
            _started = true;
            // 首次延迟 1 分钟：避开启动瞬间的持久化 / 迁移工作。
            ScheduleNext(TimeSpan.FromMinutes(1));
        }
    }

    public void Stop()
    {
        lock (this)
        {
            _started = false;
            _timer?.Dispose();
            _timer = null;
        }
    }

    /// <summary>重新按当前配置安排下一轮（保存配置后可选调用；不调也会在当前等待结束后自然生效）。</summary>
    public void Reschedule()
    {
        lock (this) { if (_started) ScheduleNext(TimeSpan.FromSeconds(5)); }
    }

    /// <summary>
    /// 执行一次扫描并返回本轮新产生的事件（测试可直接调用）。未注入 dispatcher 时只返回事件、不投递。
    /// </summary>
    public QuotaExpiryScanResult RunOnce(long nowMs)
    {
        var leadMs = (long)TimeSpan.FromHours(LeadHours).TotalMilliseconds;
        var graceMs = (long)ExpiredGrace.TotalMilliseconds;
        var expiring = new List<QuotaOverrideExpiringEvent>();
        var expired = new List<QuotaOverrideExpiredEvent>();
        var liveKeys = new HashSet<string>(StringComparer.Ordinal);
        var dirty = false;

        foreach (var g in _hub.Store.AllGroups())
        {
            // 已取消：没有额度或没有到期时间，两种事件都不该有
            if (g.QuotaOverride is not { } quota || g.QuotaOverrideUntil is not { } until) continue;

            var key = GroupKey(g.GroupId, until);
            liveKeys.Add(key);
            var delta = until - nowMs;

            if (delta > 0)
            {
                if (delta > leadMs) continue;                        // 尚未进入提醒窗口
                if (!_state.MarkExpiring(key)) continue;            // 同一额度只提醒一次（跨重启保持）
                dirty = true;
                var evt = new QuotaOverrideExpiringEvent
                {
                    GroupId = g.GroupId,
                    GroupName = g.GroupName,
                    Quota = quota,
                    Until = until,
                    RemainingSeconds = delta / 1000,
                    Timestamp = nowMs,
                };
                expiring.Add(evt);
                _dispatcher?.OnEvent(g.GroupId, evt);                // 只有「限定知聚」或全量订阅会收到
                continue;
            }

            // 已到期：只在宽限期内补发一次「已回退」
            var ago = -delta;
            if (ago > graceMs) continue;
            if (!_state.MarkExpired(key)) continue;
            dirty = true;
            var expiredEvt = new QuotaOverrideExpiredEvent
            {
                GroupId = g.GroupId,
                GroupName = g.GroupName,
                Quota = quota,
                Until = until,
                ExpiredSeconds = ago / 1000,
                Timestamp = nowMs,
            };
            expired.Add(expiredEvt);
            _dispatcher?.OnEvent(g.GroupId, expiredEvt);
        }

        // 清理失效去重键：额度取消 / 到期时间改写 / 知聚解散。
        if (_state.Prune(liveKeys)) dirty = true;
        if (dirty) _changes.Notify(); // 驱动持久化（提醒键跨重启保持）

        return new QuotaExpiryScanResult(expiring, expired);
    }

    private void RunSafely()
    {
        try
        {
            if (Interlocked.Exchange(ref _running, 1) == 1) return; // 上一轮仍未结束：跳过本轮
            try
            {
                var result = RunOnce(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                var total = result.Expiring.Count + result.Expired.Count;
                if (total > 0)
                    _logger.LogInformation("临时放额到期提醒：即将到期 {Expiring} 条 / 已回退 {Expired} 条",
                        result.Expiring.Count, result.Expired.Count);
            }
            finally { Interlocked.Exchange(ref _running, 0); }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "临时放额到期提醒扫描异常");
        }
        finally
        {
            // 每轮结束后按**当前配置**重新安排下一轮：配置治理改间隔后，最长等一个旧周期即可生效。
            lock (this) { if (_started) ScheduleNext(TimeSpan.FromMinutes(ScanMinutes)); }
        }
    }

    private void ScheduleNext(TimeSpan delay)
    {
        _timer?.Dispose();
        _timer = new Timer(_ => RunSafely(), null, delay, Timeout.InfiniteTimeSpan);
    }

    private static string GroupKey(string groupId, long until) => groupId + "|" + until.ToString();

    public void Dispose()
    {
        _started = false;
        _timer?.Dispose();
        _timer = null;
    }
}

/// <summary>一轮扫描的结果：即将到期与已到期两组事件。</summary>
public sealed record QuotaExpiryScanResult(
    IReadOnlyList<QuotaOverrideExpiringEvent> Expiring,
    IReadOnlyList<QuotaOverrideExpiredEvent> Expired);
