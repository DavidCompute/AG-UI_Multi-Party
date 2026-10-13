using AguiGroupChat.Agents;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>轻量运行指标（6.1 可观测性）测试：计数器与快照。</summary>
public sealed class MetricsTests
{
    [Fact]
    public void RecordInvocation_AccumulatesAndAggregatesByAgent()
    {
        var m = new MetricsService();
        m.RecordInvocation("agent_a", accepted: true, isBridge: false, isBridgeFailure: false, outputChars: 100);
        m.RecordInvocation("agent_a", accepted: false, isBridge: false, isBridgeFailure: false, outputChars: 0);
        m.RecordInvocation("agent_b", accepted: true, isBridge: true, isBridgeFailure: false, outputChars: 50);

        var snap = m.Snapshot();
        var props = GetProps(snap);
        Assert.Equal(3L, props.Invocations);
        Assert.Equal(2L, props.Accepted);
        Assert.Equal(1L, props.Rejected);
        Assert.Equal(1L, props.BridgeCalls);
        Assert.Equal(0L, props.BridgeFailures);
        Assert.Equal(150L, props.OutputChars);
    }

    [Fact]
    public void RecordBridgeFailure_Counted()
    {
        var m = new MetricsService();
        m.RecordInvocation("ext", accepted: false, isBridge: true, isBridgeFailure: true, outputChars: 0);
        var snap = m.Snapshot();
        Assert.Equal(1L, GetProps(snap).BridgeFailures);
    }

    [Fact]
    public void RecordMemoryResult_Counted()
    {
        var m = new MetricsService();
        m.RecordMemoryResult(hit: true);
        m.RecordMemoryResult(hit: true);
        m.RecordMemoryResult(hit: false);
        var snap = m.Snapshot();
        Assert.Equal(2L, GetProps(snap).MemoryHitCount);
        Assert.Equal(1L, GetProps(snap).MemoryEmptySearch);
    }

    [Fact]
    public void Snapshot_IncludesProcessMetrics()
    {
        var m = new MetricsService();
        var snap = m.Snapshot();
        var proc = snap.GetType().GetProperty("process")!.GetValue(snap)!;
        var pt = proc.GetType();
        Assert.True((long)pt.GetProperty("workingSetBytes")!.GetValue(proc)! > 0);
        Assert.True((int)pt.GetProperty("threadCount")!.GetValue(proc)! > 0);
        Assert.True((int)pt.GetProperty("processorCount")!.GetValue(proc)! > 0);
        Assert.True((long)pt.GetProperty("gcHeapBytes")!.GetValue(proc)! >= 0);
        Assert.True((long)pt.GetProperty("allocatedBytes")!.GetValue(proc)! > 0);
    }

    [Fact]
    public void Snapshot_IncludesTrendRing()
    {
        var m = new MetricsService();
        m.RecordInvocation("a", accepted: true, isBridge: false, isBridgeFailure: false, outputChars: 1);
        var snap = m.Snapshot();
        var trend = (System.Collections.IEnumerable)snap.GetType().GetProperty("trend")!.GetValue(snap)!;
        var list = trend.Cast<object>().ToList();
        Assert.Single(list); // 首次采样落一点
        var pt = list[0].GetType();
        Assert.Equal(1L, (long)pt.GetProperty("invocations")!.GetValue(list[0])!);
    }

    [Fact]
    public void RenderPrometheus_ContainsCoreMetricsAndLabels()
    {
        var m = new MetricsService();
        m.RecordInvocation("agent_a", accepted: true, isBridge: true, isBridgeFailure: false, outputChars: 12);
        m.RecordInvocation("agent_a", accepted: false, isBridge: true, isBridgeFailure: true, outputChars: 0);
        m.RecordMemoryResult(hit: true);

        var text = m.RenderPrometheusText();

        Assert.Contains("# TYPE knowgath_agent_invocations_total counter", text);
        Assert.Contains("knowgath_agent_invocations_total 2", text);
        Assert.Contains("knowgath_agent_accepted_total 1", text);
        Assert.Contains("knowgath_agent_rejected_total 1", text);
        Assert.Contains("knowgath_bridge_calls_total 2", text);
        Assert.Contains("knowgath_bridge_failures_total 1", text);
        Assert.Contains("knowgath_memory_hits_total 1", text);
        Assert.Contains("knowgath_output_chars_total 12", text);
        Assert.Contains("knowgath_agent_invocations_by_agent_total{agent_id=\"agent_a\"} 2", text);
        Assert.Contains("knowgath_process_working_set_bytes", text);
        Assert.Contains("knowgath_gc_collections_total{generation=\"0\"}", text);
        Assert.Contains("knowgath_up 1", text);
    }

    [Fact]
    public void RenderPrometheus_EscapesLabelValues()
    {
        var m = new MetricsService();
        m.RecordInvocation("a\"b\\c", accepted: true, isBridge: false, isBridgeFailure: false, outputChars: 0);
        var text = m.RenderPrometheusText();
        // 反斜杠 / 双引号在标签值里必须转义（否则抓取端解析失败）
        Assert.Contains("agent_id=\"a\\\"b\\\\c\"}", text);
    }

    private static (long Invocations, long Accepted, long Rejected, long BridgeCalls, long BridgeFailures, long MemoryHitCount, long MemoryEmptySearch, long OutputChars) GetProps(object snap)
    {
        var t = snap.GetType();
        return (
            (long)t.GetProperty("invocations")!.GetValue(snap)!,
            (long)t.GetProperty("accepted")!.GetValue(snap)!,
            (long)t.GetProperty("rejected")!.GetValue(snap)!,
            (long)t.GetProperty("bridgeCalls")!.GetValue(snap)!,
            (long)t.GetProperty("bridgeFailures")!.GetValue(snap)!,
            (long)t.GetProperty("memoryHitCount")!.GetValue(snap)!,
            (long)t.GetProperty("memoryEmptySearch")!.GetValue(snap)!,
            (long)t.GetProperty("outputChars")!.GetValue(snap)!);
    }
}
