using AguiGroupChat.Agents;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>
/// 向量化并发闸门（<see cref="EmbeddingGates"/>）：把「交互检索」与「后台批量」拆成两个互不抢占的池子。
///
/// <para>
/// 起因：容器重启后后台「自动周期沉淀 / 导入」长时间占满 embedding（知识库单片 4096 字 ≈ 35 秒），
/// 把交互检索排到 60 秒超时，日志表现为「语义记忆检索失败」——看着像 embedding 服务挂了，
/// 实际是被自己的后台任务饿死。这里钉住两件事：
/// ① 后台池占满时，交互池仍能立即取得槽位（不会互相阻塞）；
/// ② 等不到槽位时<b>如实返回 false</b>，让调用方降级（交互→本次不注入记忆；后台→跳过本条）而不是无限等。
/// </para>
/// </summary>
public sealed class EmbeddingGatesTests
{
    [Fact]
    public async Task Interactive_IsNotBlocked_WhenBackgroundPoolIsSaturated()
    {
        var gates = new EmbeddingGates(interactiveConcurrency: 3, backgroundConcurrency: 1);
        using var _ = gates;

        // 占满后台池（模拟知识库入库正在跑长任务）
        Assert.True(await gates.EnterBackgroundAsync(TimeSpan.FromMilliseconds(50)));

        // 交互池完全不受影响：应当立刻拿到槽位（若两者共用一个信号量，这里就会超时）
        Assert.True(await gates.EnterInteractiveAsync(TimeSpan.FromMilliseconds(50)));
        gates.ExitInteractive();

        gates.ExitBackground();
    }

    [Fact]
    public async Task Background_IsNotBlocked_WhenInteractivePoolIsSaturated()
    {
        var gates = new EmbeddingGates(interactiveConcurrency: 1, backgroundConcurrency: 1);
        using var _ = gates;

        Assert.True(await gates.EnterInteractiveAsync(TimeSpan.FromMilliseconds(50)));

        // 反向同样成立：交互占满不会拦住后台（否则批量任务会永远跑不动）
        Assert.True(await gates.EnterBackgroundAsync(TimeSpan.FromMilliseconds(50)));
        gates.ExitBackground();

        gates.ExitInteractive();
    }

    [Fact]
    public async Task ReturnsFalse_WhenPoolExhausted_InsteadOfWaitingForever()
    {
        var gates = new EmbeddingGates(interactiveConcurrency: 1, backgroundConcurrency: 1);
        using var _ = gates;

        Assert.True(await gates.EnterInteractiveAsync(TimeSpan.FromMilliseconds(30)));
        // 第二个请求等不到 → 必须如实返回 false（调用方据此降级），而不是挂住整个回复流程
        Assert.False(await gates.EnterInteractiveAsync(TimeSpan.FromMilliseconds(30)));
        gates.ExitInteractive();

        // 释放后又能拿到（证明返回 false 时没有泄漏槽位）
        Assert.True(await gates.EnterInteractiveAsync(TimeSpan.FromMilliseconds(30)));
        gates.ExitInteractive();
    }

    [Fact]
    public async Task Concurrency_IsRespectedPerPool()
    {
        var gates = new EmbeddingGates(interactiveConcurrency: 2, backgroundConcurrency: 1);
        using var _ = gates;

        Assert.True(await gates.EnterInteractiveAsync(TimeSpan.FromMilliseconds(30)));
        Assert.True(await gates.EnterInteractiveAsync(TimeSpan.FromMilliseconds(30)));
        Assert.False(await gates.EnterInteractiveAsync(TimeSpan.FromMilliseconds(30))); // 第 3 个超出容量
        gates.ExitInteractive();
        gates.ExitInteractive();
    }

    [Fact]
    public async Task IllegalConcurrency_FallsBackToOne()
    {
        var gates = new EmbeddingGates(interactiveConcurrency: 0, backgroundConcurrency: -3);
        using var _ = gates;

        Assert.True(await gates.EnterInteractiveAsync(TimeSpan.FromMilliseconds(30)));
        Assert.False(await gates.EnterInteractiveAsync(TimeSpan.FromMilliseconds(30)));
        gates.ExitInteractive();

        Assert.True(await gates.EnterBackgroundAsync(TimeSpan.FromMilliseconds(30)));
        Assert.False(await gates.EnterBackgroundAsync(TimeSpan.FromMilliseconds(30)));
        gates.ExitBackground();
    }
}
