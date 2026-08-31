using System.Collections.Concurrent;

namespace AguiGroupChat.Agents;

/// <summary>
/// 基于 HTTP/SSE 的反向隧道（内网穿透）：内网本机桥<b>主动向 Hub 发起一条 SSE 长连接</b>并注册，
/// Hub 把「客户端技能」的执行请求沿隧道下行推给该桥执行，桥执行后经 <c>POST /ag-ui/native-tunnel/result</c> 回传。
/// 从而让「没有公网 IP 的内网机器上的本机桥」也能被公网 Hub（进而被数字员工网关）调用执行 shell。
///
/// 桥可注册为两种范围：
///  - 逐数字员工（agentId）：只服务被 <c>--agent &lt;id&gt;</c> 绑定的那一个数字员工——隔离强，但每个员工要各自起桥；
///  - <b>整个平台</b>（<see cref="PlatformWideScope"/>，即 <c>*</c>）：信任平台，一座桥即可服务任意数字员工的客户端技能执行。
///   优先用逐员工桥；某员工无专属桥时回落到平台级桥。
///
/// 本服务只承担「路由 + 结果等待」：SSE 下行的实际写入由 API 层（Web）在桥连入时提供的 <see cref="TunnelConnection.Push"/> 委托完成，
/// 以便独立单测（不依赖真实 HTTP 流）。用「隧道令牌」校验桥的合法注册（全局或逐 agent，详见 <c>NativeTunnelOptions</c>）。
/// </summary>
public sealed class NativeTunnelService
{
    /// <summary>平台级桥的作用域标识：一座桥服务任意数字员工（不绑定具体 agent）。</summary>
    public const string PlatformWideScope = "*";

    /// <summary>一条已注册的内网桥连接（绑定到某数字员工，或平台级 <see cref="PlatformWideScope"/>）。</summary>
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
    // 按客户端（机器）路由：clientId → 桥（多台机器各有专属桥时可做到“请求来自哪台就在哪台执行”）
    private readonly ConcurrentDictionary<string, TunnelConnection> _byClient = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<string?>> _pending = new(StringComparer.Ordinal);

    private long _seq;

    /// <summary>
    /// 是否有一座桥能服务该数字员工的客户端技能执行：
    /// 该员工有专属桥，或已有一座平台级桥（<see cref="PlatformWideScope"/>）承接任意员工。
    /// </summary>
    public bool HasTunnel(string agentId)
    {
        if (string.IsNullOrWhiteSpace(agentId)) return false;
        return _byAgent.ContainsKey(agentId) || _byAgent.ContainsKey(PlatformWideScope);
    }

    /// <summary>
    /// 桥连入注册（API 层调用，并传入 SSE 下行写入委托）。同一作用域（agentId 或 <see cref="PlatformWideScope"/>）重复注册会<b>替换</b>旧连接（旧连接断开）。
    /// 返回注册的连接（可置回）；若作用域已被其它桥占用且不是自己，可决定是否抢注 —— 这里直接替换并释放旧连接。
    /// </summary>
    public TunnelConnection Register(string agentId, string bridgeId, long nowMs, Func<string, string, CancellationToken, Task> push)
    {
        // 先移除旧连接（若同作用域已有），避免多桥同绑一个作用域
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

    /// <summary>卸载某作用域（agentId 或 <see cref="PlatformWideScope"/>）的隧道（桥断开时调用）。</summary>
    public void Unregister(string agentId) => _byAgent.TryRemove(agentId, out _);

    /// <summary>卸载某个客户端（机器）桥（桥断开时调用）。</summary>
    public void DropClient(string clientId) => _byClient.TryRemove(clientId, out _);

    // ================= 按客户端（机器）路由：区分“请求来自哪台客户端” =================

    /// <summary>该客户端（机器名）是否有一座在线桥（以 <c>--client</c> 注册）。</summary>
    public bool HasClient(string clientId)
        => !string.IsNullOrWhiteSpace(clientId) && _byClient.ContainsKey(clientId);

    /// <summary>注册一台按客户端（机器）绑定的桥；同 clientId 重复注册替换旧连接。返回连接。</summary>
    public TunnelConnection RegisterClient(string clientId, string bridgeId, long nowMs, Func<string, string, CancellationToken, Task> push)
    {
        if (_byClient.TryGetValue(clientId, out _))
            _byClient.TryRemove(clientId, out _);
        var conn = new TunnelConnection
        {
            AgentId = PlatformWideScope, // 客户端桥不区分 agent，仅按机器路由
            BridgeId = bridgeId,
            RegisteredAtMs = nowMs,
            Push = push,
            Lease = new ConnectionLease(() => _byClient.TryRemove(clientId, out _)),
        };
        _byClient[clientId] = conn;
        return conn;
    }

    /// <summary>向指定客户端的桥下行任务并等待结果（按机器路由）。无该客户端桥 / 超时 / 失败返回 null。</summary>
    public async Task<string?> ExecuteForClientAsync(
        string clientId, string command, string? cwd, int? timeoutSec, string? query,
        TimeSpan? waitTimeout, CancellationToken ct)
    {
        if (!_byClient.TryGetValue(clientId, out var conn)) return null;
        return await ExecuteInternalAsync(conn, command, cwd, timeoutSec, query, waitTimeout, ct);
    }

    /// <summary>向能服务该数字员工的内网桥下行一个「执行客户端技能」任务并等待其回传结果：
    /// 优先该员工的专属桥，否则回落到平台级桥（<see cref="PlatformWideScope"/>）。
    /// 返回输出文本；无可用桥 / 超时 / 失败返回 null。</summary>
    public async Task<string?> ExecuteAsync(
        string agentId, string command, string? cwd, int? timeoutSec, string? query,
        TimeSpan? waitTimeout, CancellationToken ct)
    {
        if (!_byAgent.TryGetValue(agentId, out var conn) && !_byAgent.TryGetValue(PlatformWideScope, out conn))
            return null;
        return await ExecuteInternalAsync(conn, command, cwd, timeoutSec, query, waitTimeout, ct);
    }

    private async Task<string?> ExecuteInternalAsync(
        TunnelConnection conn, string command, string? cwd, int? timeoutSec, string? query,
        TimeSpan? waitTimeout, CancellationToken ct)
    {
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
