using System.Collections.Concurrent;

namespace AguiGroupChat.Hub.Infra;

/// <summary>
/// 操作审计日志（内存环形缓冲，4.3 审计）：记录关键/敏感操作（人机审批决策、导出 / 导入、
/// 重置、模型配置变更、管理员禁用 / 重置密码等），供管理员控制台查询导出。
/// 当下为<b>进程内内存</b>存储（服务重启即清空），满足会话内审计与合规留痕；
/// 需要跨重启持久化的审计可后续接入 <c>agui_usage</c> / 扩展区（sections）或专用审计表。
/// </summary>
public sealed class AuditLogService
{
    /// <summary>环形缓冲上限：超出时丢弃最旧条目（防内存无限增长）。</summary>
    private const int Capacity = 5000;

    // 读取 / 写入并发安全：用锁保护的有序队列（保持时间顺序 + 稳定查询）
    private readonly object _gate = new();
    private readonly LinkedList<AuditEntry> _entries = new();
    private long _seq;

    public AuditLogService() { }

    /// <summary>追加一条审计记录（线程安全）。action 为操作名（如 <c>interaction.resolve</c> / <c>data.export</c>）。</summary>
    public void Record(string action, string actorId, string? actorUsername, string? groupId = null,
        string? targetType = null, string? targetId = null, string? detail = null, string result = "ok")
    {
        lock (_gate)
        {
            var entry = new AuditEntry
            {
                Id = "aud_" + (++_seq),
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Action = action,
                ActorId = actorId,
                ActorUsername = actorUsername ?? actorId,
                GroupId = groupId,
                TargetType = targetType,
                TargetId = targetId,
                Detail = detail,
                Result = result,
            };
            _entries.AddLast(entry);
            while (_entries.Count > Capacity) _entries.RemoveFirst();
        }
    }

    /// <summary>查询最近 <paramref name="limit"/>（最多 200）条，按时间倒序。</summary>
    public IReadOnlyList<AuditEntry> Query(int limit = 100)
    {
        lock (_gate)
        {
            if (_entries.Count == 0) return System.Array.Empty<AuditEntry>();
            var n = Math.Min(limit, Math.Min(_entries.Count, 200));
            var arr = new AuditEntry[n];
            var node = _entries.Last;
            for (var i = 0; i < n && node is not null; i++, node = node.Previous) arr[i] = node.Value;
            return arr;
        }
    }

    /// <summary>当前累计条目数。</summary>
    public int Count { get { lock (_gate) return _entries.Count; } }
}

/// <summary>单条审计记录。</summary>
public sealed class AuditEntry
{
    public required string Id { get; init; }
    /// <summary>操作时间戳（UTC 毫秒）。</summary>
    public required long Timestamp { get; init; }
    /// <summary>操作名（如 interaction.resolve / data.export / data.reset / admin.user.disable / settings.model）。</summary>
    public required string Action { get; init; }
    /// <summary>操作者 userId。</summary>
    public required string ActorId { get; init; }
    /// <summary>操作者用户名（昵称兜底 ID）。</summary>
    public string ActorUsername { get; init; } = "";
    /// <summary>关联群（可选）。</summary>
    public string? GroupId { get; init; }
    /// <summary>目标类型（如 user / agent / kb / message / group）。</summary>
    public string? TargetType { get; init; }
    /// <summary>目标 ID。</summary>
    public string? TargetId { get; init; }
    /// <summary>细节（如被批准的工具名 / 被导出的范围）。</summary>
    public string? Detail { get; init; }
    /// <summary>结果：ok / denied / error。</summary>
    public string Result { get; init; } = "ok";
}
