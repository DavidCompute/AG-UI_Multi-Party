using System.Collections.Concurrent;

namespace AguiGroupChat.Hub.Infra;

/// <summary>
/// 操作审计日志（4.3 审计）：记录关键/敏感操作（人机审批决策、导出 / 导入、
/// 重置、模型配置变更、管理员禁用 / 重置密码 / 删除账号、平台角色变更等），供管理员控制台查询导出。
/// <para>存储双模：</para>
/// - 注入 <see cref="IAuditStore"/>（PostgreSQL / MySQL / SQLite 独立表 <c>agui_audit</c>）→ 写入专用表，
///   查询 / 导出 / 保留裁剪全走 SQL；按 <see cref="Capacity"/> 周期裁剪最旧（保留策略），跨重启天然保留；
/// - 未注入（memory / Redis 模式）→ 进程内环形缓冲（上限 <see cref="Capacity"/>），经 <c>auditLog</c>
///   扩展区 / JSON 快照持久化，重启不丢。
/// </summary>
public sealed class AuditLogService
{
    /// <summary>审计保留上限：超出时丢弃最旧条目（内存环形缓冲 / 独立表的统一保留策略）。</summary>
    private const int Capacity = 5000;

    /// <summary>独立表模式下周期性裁剪的间隔（写入次数；避免每条都跑全表裁剪 SQL）。</summary>
    private const int PruneEveryAppends = 256;

    // 内存回退模式：读取 / 写入并发安全，用锁保护的有序队列（保持时间顺序 + 稳定查询）
    private readonly object _gate = new();
    private readonly LinkedList<AuditEntry> _entries = new();
    private long _seq;

    // 独立表模式（可选）
    private readonly IAuditStore? _store;
    private long _appendsSincePrune;

    public AuditLogService(IAuditStore? store = null)
    {
        _store = store;
        if (store is not null) _ = store.Count(); // 轻量探活：表缺失 / 库不可达时在此快速失败（配置期即暴露）
    }

    /// <summary>追加一条审计记录（线程安全）。action 为操作名（如 <c>interaction.resolve</c> / <c>data.export</c>）。</summary>
    public void Record(string action, string actorId, string? actorUsername, string? groupId = null,
        string? targetType = null, string? targetId = null, string? detail = null, string result = "ok")
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
        if (_store is not null)
        {
            _store.Append(entry);
            // 保留策略：周期性裁剪最旧（容量上限同内存模式）
            if (++_appendsSincePrune >= PruneEveryAppends)
            {
                _appendsSincePrune = 0;
                _store.Prune(Capacity);
            }
            return;
        }
        lock (_gate)
        {
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
        if (_store is not null)
        {
            if (limit <= 0) limit = 100;
            return _store.Query(Math.Min(limit, 200), actor, action, targetId, fromMs, toMs);
        }
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
    public int Count
    {
        get
        {
            if (_store is not null) return (int)Math.Min(_store.Count(), int.MaxValue);
            lock (_gate) return _entries.Count;
        }
    }

    /// <summary>
    /// 导出全部命中条目（按时间正序，供 CSV 导出）。同 <see cref="Query"/> 过滤条件，但不设 200 条上限
    /// （内存模式为环形缓冲总容量，独立表模式为全表命中）。
    /// </summary>
    public IReadOnlyList<AuditEntry> QueryAll(string? actor = null, string? action = null,
        string? targetId = null, long? fromMs = null, long? toMs = null)
    {
        if (_store is not null) return _store.QueryAll(actor, action, targetId, fromMs, toMs);
        lock (_gate)
        {
            return _entries.Where(e => Matches(e, actor, action, targetId, fromMs, toMs))
                .OrderBy(e => e.Timestamp).ToArray();
        }
    }

    /// <summary>导出全部审计条目（按时间正序 = 写入序），供持久化快照（仅内存回退模式使用；独立表模式返回空）。</summary>
    public IReadOnlyList<AuditEntry> Snapshot()
    {
        if (_store is not null) return System.Array.Empty<AuditEntry>();
        lock (_gate) return _entries.ToList();
    }

    /// <summary>从快照恢复审计条目（服务启动时；仅内存回退模式使用）。超容量条目丢弃（与 Record 的环形缓冲语义一致）。</summary>
    public void Restore(IEnumerable<AuditEntry> entries)
    {
        if (_store is not null) return; // 独立表模式数据在表中，无需快照恢复
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

    /// <summary>手动执行保留裁剪（管理员运维 / 测试）；仅独立表模式生效。</summary>
    public int Prune(int keepLatest = Capacity) => _store?.Prune(keepLatest) ?? 0;
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
