using System.Collections.Concurrent;

namespace AguiGroupChat.Agents;

/// <summary>
/// 桥接断线/失败**自动重连退避（3.1 的一部分）**：按智能体跟踪连续失败次数，
/// 当某端点<b>连续失败 ≥ 阈值</b>时进入"打开"状态，在退避窗口内对新的调用<b>提前返回而不重连</b>
/// （指数退避，避免外部服务不可达时高频重试打爆其服务 / 本地资源）；成功即清零、滚动恢复。
/// 只在<b>连续多次失败</b>后生效，单次偶发失败不会触发（不改变既有单次失败路径）。
/// </summary>
public sealed class BridgeCircuitBreaker
{
    private const int OpenAfterConsecutiveFailures = 2;
    private const long ResetAfterMs = 30_000; // 成功或超过该时长后重置计数
    private const long BaseBackoffMs = 2_000;

    private sealed class State { public int Failures; public long LastFailAtMs; public long BlockUntilMs; }

    private readonly ConcurrentDictionary<string, State> _states = new(StringComparer.Ordinal);

    /// <summary>是否处于退避打开（此时应避免立即重连）。</summary>
    public bool IsOpen(string agentId, long nowMs)
    {
        if (!_states.TryGetValue(agentId, out var s)) return false;
        if (nowMs - s.LastFailAtMs > ResetAfterMs) return false;  // 长时间无失败，视为已恢复
        return nowMs < s.BlockUntilMs;
    }

    /// <summary>记录一次调用结果（isFailure=true 计连续失败；成功清零）。</summary>
    public void Record(string agentId, bool isFailure, long nowMs)
    {
        if (!isFailure)
        {
            _states.TryRemove(agentId, out _);
            return;
        }
        var s = _states.GetOrAdd(agentId, _ => new State());
        s.Failures++;
        s.LastFailAtMs = nowMs;
        if (s.Failures >= OpenAfterConsecutiveFailures)
        {
            var backoff = BaseBackoffMs * Math.Min(8, 1 << (s.Failures - OpenAfterConsecutiveFailures)); // 指数，上限 16s
            s.BlockUntilMs = nowMs + backoff;
        }
    }
}
