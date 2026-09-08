using System.Collections.Concurrent;

namespace AguiGroupChat.Agents;

/// <summary>
/// 编排计划「暂停 / 继续」控制：以 messageId 为键登记一张正在执行的计划卡闸门。
/// 网关在步骤边界检查闸门是否被用户暂停——暂停后挂起等待「继续」信号，恢复后接着执行剩余步骤；
/// HTTP 端点（/ag-ui/plan/pause · resume）与网关执行器共享同一实例。
/// 闸门仅存在于单次计划执行期间（Begin → End），进程重启即自然消失（无需跨重启持久化）。
/// </summary>
public sealed class CoordinatedPlanControlStore
{
    private readonly ConcurrentDictionary<string, PlanGate> _gates = new(StringComparer.Ordinal);

    /// <summary>登记一张新计划闸门（同一消息若已存在则替换）。返回 null 表示参数非法。</summary>
    public PlanGate? Begin(string messageId, string groupId, string agentId, string? triggerUserId)
    {
        if (string.IsNullOrWhiteSpace(messageId)) return null;
        var gate = new PlanGate(messageId, groupId, agentId, triggerUserId);
        _gates[messageId] = gate;
        return gate;
    }

    /// <summary>计划执行结束（含异常收尾）后移除闸门。幂等。</summary>
    public void End(string messageId)
    {
        if (string.IsNullOrWhiteSpace(messageId)) return;
        _gates.TryRemove(messageId, out _);
    }

    /// <summary>按消息找闸门（供 pause / resume 端点查询与鉴权）。</summary>
    public PlanGate? Find(string messageId)
        => !string.IsNullOrWhiteSpace(messageId) && _gates.TryGetValue(messageId, out var g) ? g : null;

    public IReadOnlyList<PlanGate> ActiveGates => _gates.Values.ToList();
}

/// <summary>单张计划闸门：线程安全；同一计划只由执行器单线程消费，并发仅来自 pause/resume 端点。</summary>
public sealed class PlanGate
{
    private readonly object _lock = new();
    private TaskCompletionSource<bool> _resume = Create();
    private bool _paused;

    public PlanGate(string messageId, string groupId, string agentId, string? triggerUserId)
    {
        MessageId = messageId;
        GroupId = groupId;
        AgentId = agentId;
        TriggerUserId = triggerUserId;
    }

    public string MessageId { get; }
    public string GroupId { get; }
    public string AgentId { get; }
    /// <summary>触发该计划的用户（仅触发者可暂停/继续；管理员/群主后端放行）。</summary>
    public string? TriggerUserId { get; }

    public bool IsPaused
    {
        get { lock (_lock) return _paused; }
    }

    /// <summary>请求暂停：置位并准备新一轮“继续”信号（幂等）。</summary>
    public void Pause()
    {
        lock (_lock)
        {
            if (_paused) return;
            _paused = true;
            _resume = Create(); // 本轮暂停使用全新的 TCS，避免吃到上一轮已完成的信号
        }
    }

    /// <summary>请求继续：复位并唤醒执行器。未处于暂停态时为空操作。</summary>
    public void Resume()
    {
        TaskCompletionSource<bool>? signal = null;
        lock (_lock)
        {
            if (!_paused) return;
            _paused = false;
            signal = _resume;
            _resume = Create();
        }
        signal.TrySetResult(true);
    }

    /// <summary>若已暂停则等待继续信号（执行器在步骤边界调用）。未暂停立即返回。</summary>
    public async Task WaitWhilePausedAsync(CancellationToken ct)
    {
        Task wait;
        lock (_lock)
        {
            if (!_paused) return;
            wait = _resume.Task;
        }
        await wait.WaitAsync(ct).ConfigureAwait(false);
    }

    private static TaskCompletionSource<bool> Create()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
