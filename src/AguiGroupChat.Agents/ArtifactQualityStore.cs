namespace AguiGroupChat.Agents;

/// <summary>一次技能产物契约校验的记录（用于绩效 / 交付看板）。</summary>
public sealed record ArtifactQualityEvent(
    string AgentId,
    string SkillId,
    string Kind,
    int Score,
    bool Satisfied,
    string[] Missing,
    string GroupId,
    long TimestampMs);

/// <summary>
/// 产物质量事件存储（1.0.343）：只记录“有明确产物契约”的技能执行，按数字员工聚合后供
/// 绩效看板与组织体检使用。有界内存（默认 5000 条），跨重启持久化到扩展区 <c>artifactQuality</c>。
/// </summary>
public sealed class ArtifactQualityStore
{
    /// <summary>保留的最近事件上限；超出裁掉最旧的（交付质量看板是近期运营视图，不需要无限历史）。</summary>
    public const int MaxEvents = 5000;

    private readonly object _gate = new();
    private readonly List<ArtifactQualityEvent> _events = new();

    public void Record(ArtifactQualityEvent evt)
    {
        if (string.IsNullOrWhiteSpace(evt.AgentId) || string.IsNullOrWhiteSpace(evt.Kind)) return;
        lock (_gate)
        {
            _events.Add(evt);
            if (_events.Count > MaxEvents)
                _events.RemoveRange(0, _events.Count - MaxEvents);
        }
    }

    public IReadOnlyList<ArtifactQualityEvent> Snapshot()
    {
        lock (_gate) return _events.ToArray();
    }

    public IReadOnlyList<ArtifactQualityEvent> Query(long sinceMs, string? agentId = null, string? groupId = null)
    {
        lock (_gate)
        {
            return _events
                .Where(e => e.TimestampMs >= sinceMs)
                .Where(e => string.IsNullOrWhiteSpace(agentId) || string.Equals(e.AgentId, agentId, StringComparison.Ordinal))
                .Where(e => string.IsNullOrWhiteSpace(groupId) || string.Equals(e.GroupId, groupId, StringComparison.Ordinal))
                .ToArray();
        }
    }

    public object SnapshotState() => Snapshot();

    public void RestoreState(IReadOnlyList<ArtifactQualityEvent>? rows)
    {
        lock (_gate)
        {
            _events.Clear();
            if (rows is null) return;
            foreach (var row in rows
                         .Where(r => !string.IsNullOrWhiteSpace(r.AgentId) && !string.IsNullOrWhiteSpace(r.Kind))
                         .OrderBy(r => r.TimestampMs)
                         .TakeLast(MaxEvents))
                _events.Add(row);
        }
    }
}
