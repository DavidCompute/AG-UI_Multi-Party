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
