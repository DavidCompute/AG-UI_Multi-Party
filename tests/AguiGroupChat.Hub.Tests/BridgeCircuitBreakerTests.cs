using AguiGroupChat.Agents;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>桥接断线自动重连退避（3.1）测试：多次连续失败后打开 / 单次失败不触发 / 成功复位。</summary>
public sealed class BridgeCircuitBreakerTests
{
    [Fact]
    public void SingleFailure_DoesNotOpen()
    {
        var cb = new BridgeCircuitBreaker();
        var now = 1000L;
        cb.Record("ext", isFailure: true, now);
        Assert.False(cb.IsOpen("ext", now + 1));
    }

    [Fact]
    public void RepeatedFailures_OpenBackoff_ThenExpires()
    {
        var cb = new BridgeCircuitBreaker();
        var now = 1000L;
        cb.Record("ext", isFailure: true, now);        // 第 1 次
        cb.Record("ext", isFailure: true, now + 1);    // 第 2 次 → 打开（2s）
        Assert.True(cb.IsOpen("ext", now + 10));
        // 超出退避窗口（且 < ResetAfterMs）→ 重新允许，但计数仍在
        Assert.False(cb.IsOpen("ext", now + 2500));
    }

    [Fact]
    public void Success_ResetsCount()
    {
        var cb = new BridgeCircuitBreaker();
        var now = 1000L;
        cb.Record("ext", isFailure: true, now);
        cb.Record("ext", isFailure: false, now + 1);   // 成功清零
        Assert.False(cb.IsOpen("ext", now + 2));
        // 之后再失败一次（新开计数，未到 2 次）→ 不打开
        cb.Record("ext", isFailure: true, now + 3);
        Assert.False(cb.IsOpen("ext", now + 4));
    }

    [Fact]
    public void LongGap_WithoutFailure_ActsAsRecovered()
    {
        var cb = new BridgeCircuitBreaker();
        var now = 1000L;
        cb.Record("ext", isFailure: true, now);
        cb.Record("ext", isFailure: true, now + 1);    // 打开
        // 长时间无失败（超过 ResetAfterMs）→ 视为恢复，重新允许
        Assert.False(cb.IsOpen("ext", now + ResetAfterMsPublic + 1));
    }

    private static readonly long ResetAfterMsPublic = 30_000;
}
