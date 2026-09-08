using System.Collections.Concurrent;

namespace AguiGroupChat.Agents;

/// <summary>
/// 轻量运行指标（6.1 可观测性）：进程内累积计数器，供管理员控制台 /ag-ui/admin/metrics 查看。
/// 廉价原子累加，不采样不落库（与 <see cref="AguiGroupChat.Hub.Agents.AgentUsageService"/> 的按日 token 配额分开）。
/// </summary>
public sealed class MetricsService
{
    private long _invocations;
    private long _accepted;
    private long _rejected;
    private long _bridgeCalls;
    private long _bridgeFailures;
    private long _memoryHitCount;
    private long _memoryEmptySearch;
    private long _outputChars;
    private readonly ConcurrentDictionary<string, long> _byAgent = new(StringComparer.Ordinal);
    private readonly long _startedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    // 趋势采样环：每 60 秒记录一次累计值，保留最近 4 小时（240 点），供运行指标页画趋势线
    private const long TrendIntervalMs = 60_000;
    private const int TrendMaxPoints = 240;
    private readonly object _trendLock = new();
    private readonly List<TrendPoint> _trend = new();
    private long _lastTrendMs;

    public MetricsService() => _lastTrendMs = _startedAt;

    private sealed class TrendPoint
    {
        public long AtMs;
        public long Invocations;
        public long Accepted;
        public long Rejected;
        public long BridgeCalls;
        public long BridgeFailures;
        public long MemoryHitCount;
        public long MemoryEmptySearch;
        public long OutputChars;
    }

    public void RecordInvocation(string agentId, bool accepted, bool isBridge, bool isBridgeFailure, long outputChars)
    {
        Interlocked.Increment(ref _invocations);
        if (accepted) Interlocked.Increment(ref _accepted); else Interlocked.Increment(ref _rejected);
        if (isBridge) { Interlocked.Increment(ref _bridgeCalls); if (isBridgeFailure) Interlocked.Increment(ref _bridgeFailures); }
        Interlocked.Add(ref _outputChars, outputChars);
        _byAgent.AddOrUpdate(agentId, _ => 1, (_, c) => c + 1);
    }

    public void RecordMemoryResult(bool hit)
    {
        if (hit) Interlocked.Increment(ref _memoryHitCount); else Interlocked.Increment(ref _memoryEmptySearch);
    }

    /// <summary>读取侧惰性采样：距上次样本 ≥60s 时把当前累计值记入趋势环（保留最近 4 小时）。</summary>
    private void RollTrendSample(long nowMs)
    {
        lock (_trendLock)
        {
            if (_trend.Count > 0 && nowMs - _lastTrendMs < TrendIntervalMs) return;
            _trend.Add(new TrendPoint
            {
                AtMs = nowMs,
                Invocations = Volatile.Read(ref _invocations),
                Accepted = Volatile.Read(ref _accepted),
                Rejected = Volatile.Read(ref _rejected),
                BridgeCalls = Volatile.Read(ref _bridgeCalls),
                BridgeFailures = Volatile.Read(ref _bridgeFailures),
                MemoryHitCount = Volatile.Read(ref _memoryHitCount),
                MemoryEmptySearch = Volatile.Read(ref _memoryEmptySearch),
                OutputChars = Volatile.Read(ref _outputChars),
            });
            _lastTrendMs = nowMs;
            if (_trend.Count > TrendMaxPoints) _trend.RemoveAt(0);
        }
    }

    public object Snapshot()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        RollTrendSample(now);
        List<object> trend;
        lock (_trendLock)
        {
            trend = _trend.Select(t => (object)new
            {
                atMs = t.AtMs,
                invocations = t.Invocations,
                accepted = t.Accepted,
                rejected = t.Rejected,
                bridgeCalls = t.BridgeCalls,
                bridgeFailures = t.BridgeFailures,
                memoryHitCount = t.MemoryHitCount,
                memoryEmptySearch = t.MemoryEmptySearch,
                outputChars = t.OutputChars,
            }).ToList();
        }
        return new
        {
            startedAtMs = _startedAt,
            uptimeSeconds = (long)(now - _startedAt) / 1000,
            invocations = Volatile.Read(ref _invocations),
            accepted = Volatile.Read(ref _accepted),
            rejected = Volatile.Read(ref _rejected),
            bridgeCalls = Volatile.Read(ref _bridgeCalls),
            bridgeFailures = Volatile.Read(ref _bridgeFailures),
            memoryHitCount = Volatile.Read(ref _memoryHitCount),
            memoryEmptySearch = Volatile.Read(ref _memoryEmptySearch),
            outputChars = Volatile.Read(ref _outputChars),
            byAgent = _byAgent.OrderByDescending(kv => kv.Value).Take(30).Select(kv => new { agentId = kv.Key, count = kv.Value }),
            trend,
        };
    }
}
