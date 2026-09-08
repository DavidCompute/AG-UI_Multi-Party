using AguiGroupChat.Agents;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>编排计划「暂停 / 继续」闸门（CoordinatedPlanControlStore / PlanGate）单元测试。</summary>
public sealed class CoordinatedPlanControlStoreTests
{
    [Fact]
    public void Begin_Find_End_Lifecycle()
    {
        var store = new CoordinatedPlanControlStore();
        Assert.Null(store.Find("m1"));

        var gate = store.Begin("m1", "g1", "agent_a", "user_1");
        Assert.NotNull(gate);
        Assert.Same(gate, store.Find("m1"));
        Assert.Equal("user_1", gate!.TriggerUserId);

        store.End("m1");
        Assert.Null(store.Find("m1"));
        store.End("m1"); // 幂等
    }

    [Fact]
    public void Pause_Resume_FlipsState_Idempotent()
    {
        var gate = new PlanGate("m1", "g1", "a", "u");
        Assert.False(gate.IsPaused);

        gate.Pause();
        Assert.True(gate.IsPaused);
        gate.Pause(); // 重复暂停幂等
        Assert.True(gate.IsPaused);

        gate.Resume();
        Assert.False(gate.IsPaused);
        gate.Resume(); // 未暂停时继续为空操作
        Assert.False(gate.IsPaused);
    }

    [Fact]
    public async Task WaitWhilePaused_BlocksUntilResume_AndPassesThroughWhenRunning()
    {
        var gate = new PlanGate("m1", "g1", "a", "u");
        // 运行中不阻塞
        await gate.WaitWhilePausedAsync(CancellationToken.None);

        gate.Pause();
        var waiter = gate.WaitWhilePausedAsync(CancellationToken.None);
        await Task.Delay(50);
        Assert.False(waiter.IsCompleted); // 暂停中：等待继续信号
        gate.Resume();
        await waiter.WaitAsync(TimeSpan.FromSeconds(2));

        // 可再暂停一轮（新一轮暂停使用全新信号源，不能吃到上一轮的已完成信号）
        gate.Pause();
        var waiter2 = gate.WaitWhilePausedAsync(CancellationToken.None);
        await Task.Delay(30);
        Assert.False(waiter2.IsCompleted);
        gate.Resume();
        await waiter2.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task WaitWhilePaused_RespectsCancellation()
    {
        var gate = new PlanGate("m1", "g1", "a", "u");
        gate.Pause();
        using var cts = new CancellationTokenSource(100);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gate.WaitWhilePausedAsync(cts.Token));
    }
}
