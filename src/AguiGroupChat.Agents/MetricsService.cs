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

    public object Snapshot() => new
    {
        startedAtMs = _startedAt,
        uptimeSeconds = (long)(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _startedAt) / 1000,
        invocations = Volatile.Read(ref _invocations),
        accepted = Volatile.Read(ref _accepted),
        rejected = Volatile.Read(ref _rejected),
        bridgeCalls = Volatile.Read(ref _bridgeCalls),
        bridgeFailures = Volatile.Read(ref _bridgeFailures),
        memoryHitCount = Volatile.Read(ref _memoryHitCount),
        memoryEmptySearch = Volatile.Read(ref _memoryEmptySearch),
        outputChars = Volatile.Read(ref _outputChars),
        byAgent = _byAgent.OrderByDescending(kv => kv.Value).Take(30).Select(kv => new { agentId = kv.Key, count = kv.Value }),
    };
}
