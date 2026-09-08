using System.Collections.Concurrent;

namespace AguiGroupChat.Hub.Infra;

/// <summary>
/// 操作审计日志（环形缓冲，4.3 审计）：记录关键/敏感操作（人机审批决策、导出 / 导入、
/// 重置、模型配置变更、管理员禁用 / 重置密码 / 删除账号、平台角色变更等），供管理员控制台查询导出。
/// 默认进程内环形存储（上限 <see cref="AuditLogService"/>），并注册持久化（<c>auditLog</c> 扩展区 /
/// JSON 快照 section）——memory 单文件模式随核心快照持久化、数据库 / Redis 模式经 <c>ISectionStore</c> 落库，
/// 服务重启后审计记录不再丢失。
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

    /// <summary>
    /// 查询审计日志（按时间倒序，最近优先）。支持可选过滤：操作者（userId / 用户名，大小写不敏感子串）、
    /// 操作名（子串）、目标 ID（子串）、时间范围（[fromMs, toMs]，UTC 毫秒含端点）。limit 最多 200。
    /// </summary>
    public IReadOnlyList<AuditEntry> Query(int limit = 100, string? actor = null, string? action = null,
        string? targetId = null, long? fromMs = null, long? toMs = null)
    {
        lock (_gate)
        {
            if (_entries.Count == 0) return System.Array.Empty<AuditEntry>();
            if (limit <= 0) limit = 100;                       // 旧调用兼容：0 回退默认
            limit = Math.Min(limit, Math.Min(_entries.Count, 200)); // 硬上限 200
            // 从最新（链表尾）开始过滤；OrderByDescending 稳定排序：同毫秒突发按插入序保持最新在前
            var query = _entries.Reverse()
                .Where(e => Matches(e, actor, action, targetId, fromMs, toMs))
                .OrderByDescending(e => e.Timestamp)
                .Take(limit)
                .ToArray();
            return query;
        }
    }

    /// <summary>是否命中全部过滤条件（null / 空串表示不限制）。</summary>
    private static bool Matches(AuditEntry e, string? actor, string? action, string? targetId, long? fromMs, long? toMs)
        => (string.IsNullOrWhiteSpace(actor)
                || e.ActorId.Contains(actor, StringComparison.OrdinalIgnoreCase)
                || e.ActorUsername.Contains(actor, StringComparison.OrdinalIgnoreCase))
           && (string.IsNullOrWhiteSpace(action)
                || e.Action.Contains(action, StringComparison.OrdinalIgnoreCase))
           && (string.IsNullOrWhiteSpace(targetId)
                || (e.TargetId?.Contains(targetId, StringComparison.OrdinalIgnoreCase) ?? false))
           && (fromMs is null || e.Timestamp >= fromMs.Value)
           && (toMs is null || e.Timestamp <= toMs.Value);

    /// <summary>当前累计条目数。</summary>
    public int Count { get { lock (_gate) return _entries.Count; } }

    /// <summary>导出全部审计条目（按时间正序 = 写入序），供持久化快照（memory JSON / 数据库扩展区）。</summary>
    public IReadOnlyList<AuditEntry> Snapshot()
    {
        lock (_gate) return _entries.ToList();
    }

    /// <summary>从快照恢复审计条目（服务启动时）：清空既有后按序重建，序号推进到最大已有值避免 ID 冲突。
    /// 超容量条目丢弃（与 Record 的环形缓冲语义一致）。</summary>
    public void Restore(IEnumerable<AuditEntry> entries)
    {
        lock (_gate)
        {
            _entries.Clear();
            long maxSeq = 0;
            foreach (var e in entries)
            {
                if (e.Id is not null && e.Id.StartsWith("aud_", StringComparison.Ordinal)
                    && long.TryParse(e.Id[4..], out var n) && n > maxSeq) maxSeq = n;
                _entries.AddLast(e);
                while (_entries.Count > Capacity) _entries.RemoveFirst();
            }
            _seq = Math.Max(_seq, maxSeq);
        }
    }

    /// <summary>
    /// 导出全部命中条目（按时间正序，供 CSV 导出）。同 <see cref="Query"/> 过滤条件，但不设 200 条上限
    /// （环形缓冲总容量 5000，单次导出不会超过该值）。
    /// </summary>
    public IReadOnlyList<AuditEntry> QueryAll(string? actor = null, string? action = null,
        string? targetId = null, long? fromMs = null, long? toMs = null)
    {
        lock (_gate)
        {
            return _entries.Where(e => Matches(e, actor, action, targetId, fromMs, toMs))
                .OrderBy(e => e.Timestamp).ToArray();
        }
    }
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
