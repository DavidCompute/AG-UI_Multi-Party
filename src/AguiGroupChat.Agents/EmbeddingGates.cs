namespace AguiGroupChat.Agents;

/// <summary>
/// 向量化（embedding）的并发闸门：把「交互路径」（回复前的记忆 / 知识库检索）与「后台路径」
/// （记忆写入、记忆导入、知识库切片与图谱入库）拆成两个互不抢占的池子。
///
/// <para>
/// 为什么必须拆：embedding 服务（本地 bge-m3）在 CPU 上只有一条推理通道，客户端并发度基本等于排队长度。
/// 实测踩到——容器重启后后台「自动周期沉淀 / 导入」会长时间占满 embedding（知识库切片 4096 字 ≈ 35 秒/片），
/// 把交互检索排到 60 秒超时，日志表现为「语义记忆检索失败」，看着像服务挂了，实际是被自己的后台任务饿死。
/// 拆分后后台最多同时占 1 个请求，交互侧至少拿得到其余槽位。
/// </para>
///
/// <para>
/// 容量口径：两个池子之和 = 原来的总并发（默认 3 + 1 = 4），因此<b>不增加</b>对 embedding 服务的压力，
/// 只是重新分配优先权。
/// </para>
///
/// <para>
/// 等待策略：交互侧等不到槽位就<b>降级</b>（记忆是可选上下文，本次不检索比让用户干等更好）；
/// 后台侧可以多等一会，等不到就跳过本条、由下次任务补上。
/// </para>
/// </summary>
public sealed class EmbeddingGates : IDisposable
{
    private readonly SemaphoreSlim _interactive;
    private readonly SemaphoreSlim _background;

    public EmbeddingGates(int interactiveConcurrency, int backgroundConcurrency)
    {
        var i = Math.Max(1, interactiveConcurrency);
        var b = Math.Max(1, backgroundConcurrency);
        _interactive = new SemaphoreSlim(i, i);
        _background = new SemaphoreSlim(b, b);
    }

    /// <summary>占用一个交互槽；等不到（超时 / 取消）返回 false，调用方应降级为“本次不检索”。</summary>
    public Task<bool> EnterInteractiveAsync(TimeSpan waitTimeout, CancellationToken ct = default)
        => _interactive.WaitAsync(waitTimeout, ct);

    /// <summary>释放交互槽（必须与成功的 <see cref="EnterInteractiveAsync"/> 成对）。</summary>
    public void ExitInteractive() => _interactive.Release();

    /// <summary>占用一个后台槽；等不到返回 false（调用方可跳过本条，别把后台线程卡死）。</summary>
    public Task<bool> EnterBackgroundAsync(TimeSpan waitTimeout, CancellationToken ct = default)
        => _background.WaitAsync(waitTimeout, ct);

    /// <summary>释放后台槽。</summary>
    public void ExitBackground() => _background.Release();

    public void Dispose()
    {
        _interactive.Dispose();
        _background.Dispose();
    }
}
