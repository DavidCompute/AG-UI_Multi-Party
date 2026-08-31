using System.Collections.Concurrent;

namespace AguiGroupChat.Agents;

/// <summary>
/// 基于 HTTP/SSE 的反向隧道（内网穿透）：内网本机桥<b>主动向 Hub 发起一条 SSE 长连接</b>并注册（绑定一个数字员工 agentId），
/// Hub 把「对该数字员工的某个客户端技能」的执行请求沿隧道下行推给该桥执行，桥执行后经 <c>POST /ag-ui/native-tunnel/result</c> 回传。
/// 从而让「没有公网 IP 的内网机器上的本机桥」也能被公网 Hub（进而被数字员工网关）调用执行 shell。
///
/// 本服务只承担「路由 + 结果等待」：SSE 下行的实际写入由 API 层（Web）在桥连入时提供的 <see cref="TunnelConnection.Push"/> 委托完成，
/// 以便独立单测（不依赖真实 HTTP 流）。用固定「隧道令牌」校验桥的合法注册（首版从简，可按需细化到逐 agent）。
/// </summary>
public sealed class NativeTunnelService
{
    /// <summary>一条已注册的内网桥连接（绑定到某数字员工）。</summary>
    public sealed class TunnelConnection
    {
        public required string AgentId { get; init; }
        public required string BridgeId { get; init; }
        public required long RegisteredAtMs { get; init; }
        /// <summary>SSE 下行：把一条任务事件写到桥的开放 SSE 流。</summary>
        public required Func<string, string, CancellationToken, Task> Push { get; init; }
        /// <summary>断开时释放（桥掉线 / 下线）。</summary>
        public required IDisposable Lease { get; init; }
    }

    private readonly ConcurrentDictionary<string, TunnelConnection> _byAgent = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<string?>> _pending = new(StringComparer.Ordinal);

    private long _seq;

    /// <summary>是否已有一台内网桥为该数字员工注册（隧道可执行）。</summary>
    public bool HasTunnel(string agentId)
        => !string.IsNullOrWhiteSpace(agentId) && _byAgent.ContainsKey(agentId);

    /// <summary>
    /// 桥连入注册（API 层调用，并传入 SSE 下行写入委托）。同一 agentId 重复注册会<b>替换</b>旧连接（旧连接断开）。
    /// 返回注册的连接（可置回）；若 agentId 已被其它桥占用且不是自己，可决定是否抢注 —— 这里直接替换并释放旧连接。
    /// </summary>
    public TunnelConnection Register(string agentId, string bridgeId, long nowMs, Func<string, string, CancellationToken, Task> push)
    {
        // 先移除旧连接（若同 agent 已有），避免多桥同绑一个 agent
        if (_byAgent.TryGetValue(agentId, out var _))
            _byAgent.TryRemove(agentId, out _);
        var conn = new TunnelConnection
        {
            AgentId = agentId,
            BridgeId = bridgeId,
            RegisteredAtMs = nowMs,
            Push = push,
            Lease = new ConnectionLease(() => _byAgent.TryRemove(agentId, out _)),
        };
        _byAgent[agentId] = conn;
        return conn;
    }

    /// <summary>卸载某 agentId 的隧道（桥断开时调用）。</summary>
    public void Unregister(string agentId) => _byAgent.TryRemove(agentId, out _);

    /// <summary>向该 agentId 的内网桥下行一个「执行客户端技能」任务并等待其回传结果。
    /// 返回输出文本；桥未注册 / 超时 / 失败返回 null。</summary>
    public async Task<string?> ExecuteAsync(
        string agentId, string command, string? cwd, int? timeoutSec, string? query,
        TimeSpan? waitTimeout, CancellationToken ct)
    {
        if (!_byAgent.TryGetValue(agentId, out var conn))
            return null;
        var taskId = "task_" + Interlocked.Increment(ref _seq).ToString("x") + "_" + Guid.NewGuid().ToString("N")[..8];
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[taskId] = tcs;
        var payload = System.Text.Json.JsonSerializer.Serialize(new
        {
            taskId,
            kind = "shell",
            command,
            cwd = cwd ?? ".",
            timeoutSec = timeoutSec ?? 30,
            query = query ?? (object?)null,
        });
        try
        {
            await conn.Push(payload, taskId, ct);
        }
        catch
        {
            _pending.TryRemove(taskId, out _);
            return null; // 下行失败（桥掉线）
        }
        try
        {
            return await tcs.Task.WaitAsync(waitTimeout ?? TimeSpan.FromMinutes(2), ct);
        }
        catch (TimeoutException) { return null; }
        catch (OperationCanceledException) { return null; }
        finally { _pending.TryRemove(taskId, out _); }
    }

    /// <summary>桥执行完成回传结果（API 层 <c>POST /ag-ui/native-tunnel/result</c> 调用）。按 taskId 回填等待方。</summary>
    public void Complete(string taskId, string? output, string? errorLog)
    {
        if (string.IsNullOrWhiteSpace(taskId)) return;
        if (_pending.TryRemove(taskId, out var tcs))
        {
            var result = string.IsNullOrWhiteSpace(output) ? (errorLog ?? "（本机执行无输出）") : output;
            tcs.TrySetResult(result);
        }
    }

    /// <summary>桥掉线：清理其 pending 任务（置为取消），并卸载注册。</summary>
    public void DropAllForAgent(string agentId)
    {
        Unregister(agentId);
        foreach (var kv in _pending)
            if (kv.Value.Task.IsCompleted == false) kv.Value.TrySetResult(null);
    }

    private sealed class ConnectionLease(Action onDispose) : IDisposable
    {
        private Action? _a = onDispose;
        public void Dispose() => Interlocked.Exchange(ref _a, null)?.Invoke();
    }
}
