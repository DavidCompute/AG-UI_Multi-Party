using AguiGroupChat.Agents;
using AguiGroupChat.Hub.Persistence;

namespace AguiGroupChat.Web;

/// <summary>
/// 副本心跳发布器（跨副本诊断）：周期性把本进程的指标快照写入 <see cref="IReplicaRegistry"/>。
/// Redis 多副本部署下各副本互见（同一批 key），因此任一副本都能看到全部活跃副本；
/// 单副本部署下写进 <see cref="LocalReplicaRegistry"/>（仅自身）。间隔显著小于快照 TTL，
/// 保证在线副本始终在列表中可见。
/// </summary>
public sealed class ReplicaMetricsPublisher : IDisposable
{
    /// <summary>心跳间隔（秒）。须显著小于 <c>RedisReplicaRegistry.TtlSeconds</c>（60s）。</summary>
    public const int IntervalSeconds = 15;

    private readonly MetricsService _metrics;
    private readonly IReplicaRegistry _registry;
    private readonly ReplicaIdentity _identity;
    private readonly ILogger<ReplicaMetricsPublisher> _logger;
    private Timer? _timer;

    public ReplicaMetricsPublisher(MetricsService metrics, IReplicaRegistry registry, ReplicaIdentity identity,
        ILogger<ReplicaMetricsPublisher> logger)
    {
        _metrics = metrics;
        _registry = registry;
        _identity = identity;
        _logger = logger;
    }

    /// <summary>启动心跳（幂等）：立即发布一次，此后每 <see cref="IntervalSeconds"/> 秒一次。</summary>
    public void Start()
    {
        if (_timer is not null) return;
        PublishOnce();
        _timer = new Timer(_ => PublishOnce(), null,
            TimeSpan.FromSeconds(IntervalSeconds), TimeSpan.FromSeconds(IntervalSeconds));
    }

    private void PublishOnce()
    {
        try
        {
            var c = _metrics.Counters();
            _registry.Publish(new ReplicaSnapshot
            {
                ReplicaId = _identity.Id,
                Host = _identity.Host,
                Pid = _identity.Pid,
                StartedAtMs = _identity.StartedAtMs,
                LastSeenMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Invocations = c.Invocations,
                Accepted = c.Accepted,
                Rejected = c.Rejected,
                BridgeCalls = c.BridgeCalls,
                BridgeFailures = c.BridgeFailures,
                MemoryHits = c.MemoryHits,
                MemoryEmpty = c.MemoryEmpty,
                OutputChars = c.OutputChars,
                WorkingSetBytes = c.WorkingSetBytes,
                GcHeapBytes = c.GcHeapBytes,
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "发布副本心跳失败（副本 {ReplicaId}）", _identity.Id);
        }
    }

    public void Dispose() => _timer?.Dispose();
}
