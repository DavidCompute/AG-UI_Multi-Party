using AguiGroupChat.Hub.Infra;
using AguiGroupChat.Hub.Persistence;
using System.Text.Json;

namespace AguiGroupChat.Web;

/// <summary>
/// 技能「试运行」产出附件的归属登记。
///
/// <para>
/// 为什么需要它：附件访问校验（<see cref="AttachmentApi"/>）的规则是「附件必须命中你能访问的知聚里某条未撤回消息，
/// 或者是头像」。而**技能库试运行**产出的稿子没有挂在任何知聚消息上 —— 于是用户手动试运行一份内置文档技能、
/// 界面给出下载 / 预览入口，点下去却只会拿到 403（自己的东西自己看不了）。
/// 这里记下「这个附件是哪个用户试运行产出的」，让产出者本人可读。
/// </para>
///
/// <para>
/// 只在<strong>产出者本人</strong>名下放行（<see cref="IsOwnedBy"/>），不是所有人可读 ——
/// 试运行的稿子可能含未公开内容，跨用户可读等于开了个越权读取口子。
/// </para>
///
/// <para>
/// 持久化到扩展区：重启后历史试运行的链接仍然有效（否则一重启「昨天的试运行结果」就全变成无权访问）。
/// 按保留期（与预览缓存一致 7 天）与每人条数上限裁剪，不会无限增长。
/// </para>
/// </summary>
public sealed class SkillRunArtifactStore
{
    /// <summary>一条试运行产物记录。</summary>
    public sealed record Artifact(string AttachmentId, string OwnerId, long CreatedAtMs);

    /// <summary>保留期：超过即在下一次登记时被顺带裁掉（与预览缓存同口径）。</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    /// <summary>每人最多保留的试运行产物条数（超出裁掉最旧的）。</summary>
    public const int MaxPerUser = 50;

    private readonly object _lock = new();
    private readonly Dictionary<string, Artifact> _byAttachmentId = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock;

    public SkillRunArtifactStore(TimeProvider? clock = null) => _clock = clock ?? TimeProvider.System;

    /// <summary>登记「该附件由该用户试运行产出」。空值静默忽略（调用点不必到处判空）。</summary>
    public void Register(string? attachmentId, string? ownerId)
    {
        if (string.IsNullOrWhiteSpace(attachmentId) || string.IsNullOrWhiteSpace(ownerId)) return;
        lock (_lock)
        {
            _byAttachmentId[attachmentId] = new Artifact(attachmentId, ownerId, _clock.GetUtcNow().ToUnixTimeMilliseconds());
            Prune();
        }
    }

    /// <summary>该附件是否由该用户试运行产出（产出者本人可读）。</summary>
    public bool IsOwnedBy(string attachmentId, string userId)
    {
        if (string.IsNullOrWhiteSpace(attachmentId) || string.IsNullOrWhiteSpace(userId)) return false;
        lock (_lock)
        {
            return _byAttachmentId.TryGetValue(attachmentId, out var a)
                && string.Equals(a.OwnerId, userId, StringComparison.Ordinal);
        }
    }

    /// <summary>快照（持久化用）。</summary>
    public IReadOnlyList<Artifact> Snapshot()
    {
        lock (_lock) return _byAttachmentId.Values.ToList();
    }

    /// <summary>恢复（重启后读回）。</summary>
    public void Restore(IReadOnlyList<Artifact> records)
    {
        lock (_lock)
        {
            _byAttachmentId.Clear();
            foreach (var r in records)
            {
                if (string.IsNullOrWhiteSpace(r.AttachmentId) || string.IsNullOrWhiteSpace(r.OwnerId)) continue;
                _byAttachmentId[r.AttachmentId] = r;
            }
            Prune();
        }
    }

    /// <summary>清空全部登记（系统初始化「清空一切」用）。</summary>
    public void ClearAll()
    {
        lock (_lock) _byAttachmentId.Clear();
    }

    /// <summary>裁剪：过期条目 + 每人超出上限的最旧条目。调用方须持锁。</summary>
    private void Prune()
    {
        var cutoff = _clock.GetUtcNow().Subtract(Retention).ToUnixTimeMilliseconds();
        foreach (var key in _byAttachmentId.Where(kv => kv.Value.CreatedAtMs < cutoff).Select(kv => kv.Key).ToList())
            _byAttachmentId.Remove(key);

        foreach (var group in _byAttachmentId.Values.GroupBy(a => a.OwnerId, StringComparer.Ordinal))
        {
            var ordered = group.OrderByDescending(a => a.CreatedAtMs).ToList();
            foreach (var stale in ordered.Skip(MaxPerUser))
                _byAttachmentId.Remove(stale.AttachmentId);
        }
    }
}

/// <summary>试运行产物归属的持久化注册（扩展方法必须落在静态类上，故与状态类分开）。</summary>
public static class SkillRunArtifactStorePersistence
{
    /// <summary>注册到持久化扩展区「skillRunArtifacts」：重启后试运行产物的访问链接依旧有效。</summary>
    public static void RegisterSkillRunArtifactStorePersistence(this IServiceProvider services)
    {
        var store = services.GetRequiredService<SkillRunArtifactStore>();
        Func<object?> snapshot = () => store.Snapshot().Select(a => (object)a).ToList();
        Action<JsonElement> restore = element => store.Restore(
            element.Deserialize<List<SkillRunArtifactStore.Artifact>>(AguiJson.Options) ?? []);

        var persistence = services.GetService<PersistenceService>();
        if (persistence is not null)
        {
            persistence.AddSection("skillRunArtifacts", snapshot, restore);
        }
        else
        {
            services.GetService<ISectionStore>()?.AddSection("skillRunArtifacts", snapshot, restore);
        }
    }
}
