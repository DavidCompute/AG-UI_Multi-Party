namespace AguiGroupChat.Hub.Infra;

/// <summary>
/// 审计日志<b>独立表</b>存储抽象（企业合规）：PostgreSQL / MySQL / SQLite 模式用专用表 <c>agui_audit</c> 承载，
/// 避免把全量审计塞进 <c>agui_sections</c> JSON blob（检索 / 导出 / 保留裁剪都可走 SQL）。
/// 未注册该存储时 <see cref="AuditLogService"/> 回退到进程内环形缓冲 + 快照持久化（memory / Redis 模式）。
/// </summary>
public interface IAuditStore
{
    /// <summary>追加一条审计记录（id 冲突静默忽略，保证幂等重放不炸）。</summary>
    void Append(AuditEntry entry);

    /// <summary>按过滤条件查询最近 <paramref name="limit"/> 条（时间倒序，最新在前）。</summary>
    IReadOnlyList<AuditEntry> Query(int limit, string? actor = null, string? action = null,
        string? targetId = null, long? fromMs = null, long? toMs = null);

    /// <summary>导出全部命中（时间正序，CSV 用；调用方自带上限语义）。</summary>
    IReadOnlyList<AuditEntry> QueryAll(string? actor = null, string? action = null,
        string? targetId = null, long? fromMs = null, long? toMs = null);

    /// <summary>当前总条数。</summary>
    long Count();

    /// <summary>保留最新 <paramref name="keepLatest"/> 条，删除更旧的（保留策略）。返回删除条数。</summary>
    int Prune(int keepLatest);

    /// <summary>清空全部（系统初始化 / 测试用）。</summary>
    void Clear();
}
