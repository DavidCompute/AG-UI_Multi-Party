using AguiGroupChat.Hub.Infra;
using AguiGroupChat.Hub.Persistence;
using System.Text.Json;

namespace AguiGroupChat.Web;

/// <summary>
/// 临时放额提醒的**去重状态**（1.0.276）。
///
/// <para>
/// 为什么需要持久化：提醒服务此前用进程内字典去重，重启后同一额度会再推一次
/// <c>QUOTA_OVERRIDE_EXPIRING</c> / <c>QUOTA_OVERRIDE_EXPIRED</c>；虽然告警平台可按
/// <c>groupId + until</c> 去重，但平台侧多一次「重复投递」就多一次误报风险。
/// 这里把已提醒的键落盘，重启后继续沿用——做到跨重启**只提醒一次**。
/// </para>
/// <para>
/// 键格式：<c>groupId|untilMs</c>；两类事件各维护一份。额度取消 / 改期 / 知聚解散时由扫描器
/// 调用 <see cref="Prune"/> 清掉失效键（否则会无限增长）。
/// </para>
/// </summary>
public sealed class QuotaReminderStateStore
{
    /// <summary>持久化传输对象（List 便于 JSON 序列化 / 反序列化）。</summary>
    public sealed record Dto(List<string> Expiring, List<string> Expired);

    private readonly object _lock = new();
    private readonly HashSet<string> _expiring = new(StringComparer.Ordinal);
    private readonly HashSet<string> _expired = new(StringComparer.Ordinal);

    /// <summary>标记「即将到期」已提醒；返回 true 表示这是首次（调用方应发事件）。</summary>
    public bool MarkExpiring(string key)
    {
        lock (_lock) return _expiring.Add(key);
    }

    /// <summary>标记「已回退」已提醒；返回 true 表示这是首次。</summary>
    public bool MarkExpired(string key)
    {
        lock (_lock) return _expired.Add(key);
    }

    /// <summary>清掉不再有效的键（额度取消 / 改期 / 解散）。返回是否发生变化。</summary>
    public bool Prune(IReadOnlySet<string> liveKeys)
    {
        lock (_lock)
        {
            var changed = _expiring.RemoveWhere(k => !liveKeys.Contains(k)) > 0;
            changed |= _expired.RemoveWhere(k => !liveKeys.Contains(k)) > 0;
            return changed;
        }
    }

    /// <summary>当前键数量（诊断 / 测试用）。</summary>
    public (int Expiring, int Expired) Counts
    {
        get { lock (_lock) return (_expiring.Count, _expired.Count); }
    }

    /// <summary>快照（持久化用）。排序保证落盘内容稳定、便于 diff。</summary>
    public IReadOnlyList<string> SnapshotExpiring()
    {
        lock (_lock) return _expiring.OrderBy(k => k, StringComparer.Ordinal).ToList();
    }

    public IReadOnlyList<string> SnapshotExpired()
    {
        lock (_lock) return _expired.OrderBy(k => k, StringComparer.Ordinal).ToList();
    }

    public void Restore(IEnumerable<string>? expiring, IEnumerable<string>? expired)
    {
        lock (_lock)
        {
            _expiring.Clear();
            _expired.Clear();
            if (expiring is not null) foreach (var k in expiring) if (!string.IsNullOrWhiteSpace(k)) _expiring.Add(k);
            if (expired is not null) foreach (var k in expired) if (!string.IsNullOrWhiteSpace(k)) _expired.Add(k);
        }
    }

    public void Clear()
    {
        lock (_lock) { _expiring.Clear(); _expired.Clear(); }
    }
}

/// <summary>持久化扩展区「quotaReminderState」：提醒去重键跨重启保持。</summary>
public static class QuotaReminderStatePersistence
{
    public static void RegisterQuotaReminderStatePersistence(this IServiceProvider services)
    {
        var store = services.GetRequiredService<QuotaReminderStateStore>();
        Func<object?> snapshot = () => new QuotaReminderStateStore.Dto(
            store.SnapshotExpiring().ToList(), store.SnapshotExpired().ToList());
        Action<JsonElement> restore = element =>
        {
            var dto = element.Deserialize<QuotaReminderStateStore.Dto>(AguiJson.Options);
            if (dto is not null) store.Restore(dto.Expiring, dto.Expired);
        };

        var persistence = services.GetService<PersistenceService>();
        if (persistence is not null)
            persistence.AddSection("quotaReminderState", snapshot, restore);
        else
            services.GetService<ISectionStore>()?.AddSection("quotaReminderState", snapshot, restore);
    }
}
