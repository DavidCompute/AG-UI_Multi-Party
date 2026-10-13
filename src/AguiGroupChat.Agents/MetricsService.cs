using System.Collections.Concurrent;
using System.Text;

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

    /// <summary>进程级资源指标（廉价、无副作用）：工作集 / 私有内存 / 线程 / 句柄。</summary>
    private static (long WorkingSet, long PrivateBytes, int Threads, long Handles) ReadProcess()
    {
        long workingSet = 0, privateBytes = 0, handles = 0;
        var threads = 0;
        try
        {
            using var p = System.Diagnostics.Process.GetCurrentProcess();
            workingSet = p.WorkingSet64;
            privateBytes = p.PrivateMemorySize64;
            handles = p.HandleCount;
            threads = p.Threads.Count;
        }
        catch { /* 取不到就留 0（不影响其它指标） */ }
        return (workingSet, privateBytes, threads, handles);
    }

    /// <summary>进程级资源指标（廉价、无副作用）：工作集 / 私有内存 / 线程 / 句柄 / GC。</summary>
    private static object ProcessMetrics()
    {
        var (workingSet, privateBytes, threads, handles) = ReadProcess();
        return new
        {
            workingSetBytes = workingSet,
            privateBytes,
            threadCount = threads,
            handleCount = handles,
            processorCount = Environment.ProcessorCount,
            gcHeapBytes = GC.GetTotalMemory(forceFullCollection: false),
            gcGen0 = GC.CollectionCount(0),
            gcGen1 = GC.CollectionCount(1),
            gcGen2 = GC.CollectionCount(2),
            allocatedBytes = GC.GetTotalAllocatedBytes(precise: false),
        };
    }

    /// <summary>
    /// 渲染 <b>Prometheus 文本暴露格式</b>（<c>text/plain; version=0.0.4</c>）供 Prometheus 直接抓取，
    /// 或由 OpenTelemetry Collector 的 Prometheus receiver 采集后转发到任意后端。
    /// 指标名前缀统一 <c>knowgath_</c>；分数字员工的调用量用 <c>agent_id</c> 标签（而非拆分成多指标）。
    /// </summary>
    public string RenderPrometheusText()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var sb = new StringBuilder(2048);

        static void Metric(StringBuilder b, string name, string type, string help, long value)
        {
            b.Append("# HELP ").Append(name).Append(' ').Append(help).Append('\n');
            b.Append("# TYPE ").Append(name).Append(' ').Append(type).Append('\n');
            b.Append(name).Append(' ').Append(value).Append('\n');
        }

        Metric(sb, "knowgath_agent_invocations_total", "counter", "Total digital-employee invocations", Volatile.Read(ref _invocations));
        Metric(sb, "knowgath_agent_accepted_total", "counter", "Invocations that produced an answer", Volatile.Read(ref _accepted));
        Metric(sb, "knowgath_agent_rejected_total", "counter", "Invocations rejected / silent", Volatile.Read(ref _rejected));
        Metric(sb, "knowgath_bridge_calls_total", "counter", "AG-UI bridge calls", Volatile.Read(ref _bridgeCalls));
        Metric(sb, "knowgath_bridge_failures_total", "counter", "AG-UI bridge call failures", Volatile.Read(ref _bridgeFailures));
        Metric(sb, "knowgath_memory_hits_total", "counter", "Semantic-memory searches that returned hits", Volatile.Read(ref _memoryHitCount));
        Metric(sb, "knowgath_memory_empty_total", "counter", "Semantic-memory searches that returned nothing", Volatile.Read(ref _memoryEmptySearch));
        Metric(sb, "knowgath_output_chars_total", "counter", "Total characters produced by agents", Volatile.Read(ref _outputChars));

        // 分数字员工调用量（带 agent_id 标签）
        var byAgent = _byAgent.ToArray();
        if (byAgent.Length > 0)
        {
            sb.Append("# HELP knowgath_agent_invocations_by_agent_total Invocations per digital employee\n");
            sb.Append("# TYPE knowgath_agent_invocations_by_agent_total counter\n");
            foreach (var kv in byAgent.OrderBy(k => k.Key, StringComparer.Ordinal))
                sb.Append("knowgath_agent_invocations_by_agent_total{agent_id=\"").Append(EscapeLabel(kv.Key)).Append("\"} ").Append(kv.Value).Append('\n');
        }

        // 进程与 GC
        var (workingSet, privateBytes, threads, handles) = ReadProcess();
        Metric(sb, "knowgath_process_working_set_bytes", "gauge", "Process working set in bytes", workingSet);
        Metric(sb, "knowgath_process_private_bytes", "gauge", "Process private bytes", privateBytes);
        Metric(sb, "knowgath_process_threads", "gauge", "Process thread count", threads);
        Metric(sb, "knowgath_process_handles", "gauge", "Process handle count", handles);
        Metric(sb, "knowgath_processors", "gauge", "Logical processor count", Environment.ProcessorCount);
        Metric(sb, "knowgath_gc_heap_bytes", "gauge", "Managed heap size in bytes", GC.GetTotalMemory(forceFullCollection: false));
        Metric(sb, "knowgath_dotnet_allocated_bytes_total", "counter", "Total bytes allocated by .NET", GC.GetTotalAllocatedBytes(precise: false));
        sb.Append("# HELP knowgath_gc_collections_total GC collections by generation\n");
        sb.Append("# TYPE knowgath_gc_collections_total counter\n");
        sb.Append("knowgath_gc_collections_total{generation=\"0\"} ").Append(GC.CollectionCount(0)).Append('\n');
        sb.Append("knowgath_gc_collections_total{generation=\"1\"} ").Append(GC.CollectionCount(1)).Append('\n');
        sb.Append("knowgath_gc_collections_total{generation=\"2\"} ").Append(GC.CollectionCount(2)).Append('\n');

        Metric(sb, "knowgath_uptime_seconds", "gauge", "Process uptime in seconds", (long)(now - _startedAt) / 1000);
        Metric(sb, "knowgath_start_time_seconds", "gauge", "Process start time (Unix seconds)", _startedAt / 1000);
        Metric(sb, "knowgath_up", "gauge", "1 when the service is up", 1);
        return sb.ToString();
    }

    /// <summary>Prometheus 标签值转义（反斜杠 / 双引号 / 换行）。</summary>
    private static string EscapeLabel(string v)
        => v.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");

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
            // 进程健康（可观测性看板）：内存 / 线程 / GC——与业务计数器一起展示
            process = ProcessMetrics(),
        };
    }
}
