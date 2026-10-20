using AguiGroupChat.Hub.Infra;
using AguiGroupChat.Hub.Persistence;
using System.Text.Json;

namespace AguiGroupChat.Web;

/// <summary>一条待审批的图库写入（1.0.279）：技能写「已有/授权库」时先入队，管理员批准后才落盘。</summary>
public sealed record PendingImageWrite
{
    public required string Id { get; init; }
    public required string OwnerUserId { get; init; }
    public string? SkillId { get; init; }
    public string? AgentId { get; init; }
    public required string LibId { get; init; }
    public required string FileName { get; init; }
    public string? ContentType { get; init; }
    public required byte[] Bytes { get; init; }
    public string? Caption { get; init; }
    public List<string>? Tags { get; init; }
    public long AtMs { get; init; }
    public long ExpiresAtMs { get; init; }
}

/// <summary>待审批项的持久化元数据（不含图片字节；字节存在同目录的 <c>{id}.bin</c>）。</summary>
public sealed record PendingImageWriteDto(
    string Id, string OwnerUserId, string? SkillId, string? AgentId, string LibId,
    string FileName, string? ContentType, string? Caption, List<string>? Tags, long AtMs, long ExpiresAtMs);

/// <summary>
/// 已处理的一条回执（1.0.283）：批准 / 拒绝 / 失败的近期记录，供图库管理「最近处理」展示。
/// 只保留进程内的最新若干条（重启清空）；要长期追溯请查审计日志。
/// </summary>
public sealed record ResolvedImageWrite
{
    public required string PendingId { get; init; }
    /// <summary><c>approved</c> / <c>rejected</c> / <c>failed</c>。</summary>
    public required string Status { get; init; }
    public string? Reason { get; init; }
    public required string LibId { get; init; }
    public string? LibName { get; init; }
    public required string FileName { get; init; }
    public string? SkillId { get; init; }
    public string? AgentId { get; init; }
    public string? AssetId { get; init; }
    public bool Duplicated { get; init; }
    /// <summary>处理人（管理员用户名；取不到时回落到 userId）。</summary>
    public string? DecidedBy { get; init; }
    public required long AtMs { get; init; }
}

/// <summary>「最近处理」的持久化元数据（1.0.284；不含图片字节）。</summary>
public sealed record ResolvedImageWriteDto(
    string PendingId, string Status, string? Reason, string LibId, string? LibName,
    string FileName, string? SkillId, string? AgentId, string? AssetId, bool Duplicated,
    string? DecidedBy, long AtMs);

/// <summary>
/// 图库写入审批队列（1.0.279 / 1.0.280）：
/// <list type="bullet">
/// <item>只用于技能写**已有/授权图库**；写本轮新建的库仍立即生效（隔离、风险低）。</item>
/// <item>每项保留图片字节（最大 10MB），因此总字节 / 条数 / 每人条数都有硬上限。</item>
/// <item>24 小时未处理自动丢弃。</item>
/// <item><b>1.0.280</b>：元数据落持久化扩展区、字节落 <c>{root}/{id}.bin</c>，重启后自动恢复；
/// 若持久化关闭则退化为纯内存（启动时清理超过 TTL 的孤儿文件）。</item>
/// </list>
/// </summary>
public sealed class ImageWriteApprovalQueue
{
    public static readonly TimeSpan Ttl = TimeSpan.FromHours(24);
    public const int MaxItems = 50;
    public const long MaxTotalBytes = 64L * 1024 * 1024;
    public const int MaxPerOwner = 20;
    /// <summary>「最近处理」保留的条数上限（进程内；不参与持久化）。</summary>
    public const int MaxHistory = 50;

    private readonly object _lock = new();
    private readonly Dictionary<string, PendingImageWrite> _items = new(StringComparer.Ordinal);
    private readonly object _historyLock = new();
    private readonly LinkedList<ResolvedImageWrite> _history = new();
    private readonly string? _root;
    private readonly ChangeHub? _changes;

    /// <param name="root">字节落地目录（null = 纯内存，测试 / 精简宿主用）。</param>
    public ImageWriteApprovalQueue(string? root = null, ChangeHub? changes = null)
    {
        _root = string.IsNullOrWhiteSpace(root) ? null : Path.GetFullPath(root!);
        _changes = changes;
        if (_root is not null)
        {
            try { Directory.CreateDirectory(_root); CleanupOrphanFiles(); }
            catch { /* 目录不可写：退化为纯内存 */ _root = null; }
        }
    }

    /// <summary>入队；超出上限时返回 null 并给出 <paramref name="error"/>。</summary>
    public PendingImageWrite? Add(string ownerUserId, string? skillId, string? agentId, string libId,
        string fileName, string? contentType, byte[] bytes, string? caption, List<string>? tags, out string? error)
    {
        error = null;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        lock (_lock)
        {
            PruneLocked(now);
            if (_items.Count >= MaxItems) { error = $"待审批写入已达上限（{MaxItems} 条），请先处理"; return null; }
            if (_items.Values.Count(x => x.OwnerUserId == ownerUserId) >= MaxPerOwner)
            {
                error = $"你的待审批写入已达上限（{MaxPerOwner} 条），请先处理";
                return null;
            }
            if (_items.Values.Sum(x => x.Bytes.LongLength) + bytes.LongLength > MaxTotalBytes)
            {
                error = "待审批写入总大小已达上限，请先处理";
                return null;
            }

            var item = new PendingImageWrite
            {
                Id = "iwp_" + IdGenerator.NewId(),
                OwnerUserId = ownerUserId,
                SkillId = skillId,
                AgentId = agentId,
                LibId = libId,
                FileName = fileName,
                ContentType = contentType,
                Bytes = bytes,
                Caption = caption,
                Tags = tags,
                AtMs = now,
                ExpiresAtMs = now + (long)Ttl.TotalMilliseconds,
            };
            if (_root is not null)
            {
                try { File.WriteAllBytes(Path.Combine(_root, item.Id + ".bin"), bytes); }
                catch (Exception ex)
                {
                    error = "待审批写入落盘失败：" + ex.Message;
                    return null;
                }
            }
            _items[item.Id] = item;
            _changes?.Notify();
            return item;
        }
    }

    /// <summary>当前待审批列表（最新在前；不含字节，避免把 10MB 图片塞进列表响应）。</summary>
    public IReadOnlyList<PendingImageWrite> List()
    {
        lock (_lock)
        {
            PruneLocked(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            return _items.Values.OrderByDescending(x => x.AtMs).ToList();
        }
    }

    /// <summary>取出并移除（拒绝时用；批准失败时不要用它，否则会丢掉字节）。</summary>
    public bool TryTake(string id, out PendingImageWrite? item)
    {
        lock (_lock)
        {
            PruneLocked(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            if (!_items.Remove(id, out var found)) { item = null; return false; }
            DeleteFileLocked(id);
            _changes?.Notify();
            item = found;
            return true;
        }
    }

    /// <summary>查看而不移除（1.0.289）：批准需要「先试写、成功才移除」，失败时保留字节以便重试。</summary>
    public PendingImageWrite? Peek(string id)
    {
        lock (_lock)
        {
            PruneLocked(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            return _items.GetValueOrDefault(id);
        }
    }

    /// <summary>写成功后移除（1.0.289）：删字节并通知持久化。</summary>
    public bool Remove(string id)
    {
        lock (_lock)
        {
            if (!_items.Remove(id)) return false;
            DeleteFileLocked(id);
            _changes?.Notify();
            return true;
        }
    }

    public int Count { get { lock (_lock) return _items.Count; } }

    /// <summary>登记一条已处理回执（最新在前，超出 <see cref="MaxHistory"/> 丢最旧的）。</summary>
    public void RecordResolved(ResolvedImageWrite item)
    {
        lock (_historyLock)
        {
            _history.AddFirst(item);
            while (_history.Count > MaxHistory) _history.RemoveLast();
        }
        _changes?.Notify(); // 让持久化尽快落盘（无变更钩子时忽略）
    }

    /// <summary>最近的已处理回执（新→旧；默认 20 条，上限 <see cref="MaxHistory"/>）。</summary>
    public IReadOnlyList<ResolvedImageWrite> RecentResolved(int limit = 20)
    {
        lock (_historyLock) return _history.Take(Math.Clamp(limit, 1, MaxHistory)).ToList();
    }

    /// <summary>清空「最近处理」（返回清掉的条数；持久化下次冲刷时同步）。</summary>
    public int ClearHistory()
    {
        lock (_historyLock)
        {
            var n = _history.Count;
            _history.Clear();
            if (n > 0) _changes?.Notify();
            return n;
        }
    }

    /// <summary>持久化快照（新→旧；最多 <see cref="MaxHistory"/> 条）。</summary>
    public IReadOnlyList<ResolvedImageWriteDto> SnapshotHistory()
    {
        lock (_historyLock) return _history.Select(ToDto).ToList();
    }

    /// <summary>从持久化快照恢复（保持新→旧顺序；跳过缺主键 / 状态的行，超限截断）。</summary>
    public void RestoreHistory(IEnumerable<ResolvedImageWriteDto>? items)
    {
        if (items is null) return;
        lock (_historyLock)
        {
            _history.Clear();
            foreach (var d in items)
            {
                if (string.IsNullOrWhiteSpace(d.PendingId) || string.IsNullOrWhiteSpace(d.Status)) continue;
                _history.AddLast(new ResolvedImageWrite
                {
                    PendingId = d.PendingId,
                    Status = d.Status,
                    Reason = d.Reason,
                    LibId = d.LibId ?? "",
                    LibName = d.LibName,
                    FileName = d.FileName ?? "",
                    SkillId = d.SkillId,
                    AgentId = d.AgentId,
                    AssetId = d.AssetId,
                    Duplicated = d.Duplicated,
                    DecidedBy = d.DecidedBy,
                    AtMs = d.AtMs,
                });
                if (_history.Count >= MaxHistory) break;
            }
        }
    }

    private static ResolvedImageWriteDto ToDto(ResolvedImageWrite x)
        => new(x.PendingId, x.Status, x.Reason, x.LibId, x.LibName, x.FileName, x.SkillId, x.AgentId,
            x.AssetId, x.Duplicated, x.DecidedBy, x.AtMs);

    /// <summary>持久化快照（元数据；字节已在磁盘上）。</summary>
    public IReadOnlyList<PendingImageWriteDto> Snapshot()
    {
        lock (_lock)
        {
            return _items.Values
                .OrderBy(x => x.AtMs)
                .Select(x => new PendingImageWriteDto(x.Id, x.OwnerUserId, x.SkillId, x.AgentId, x.LibId,
                    x.FileName, x.ContentType, x.Caption, x.Tags, x.AtMs, x.ExpiresAtMs))
                .ToList();
        }
    }

    /// <summary>从持久化快照恢复；缺文件 / 过期项跳过，超限时保留最新。</summary>
    public void Restore(IEnumerable<PendingImageWriteDto>? items)
    {
        if (items is null) return;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        lock (_lock)
        {
            _items.Clear();
            foreach (var d in items.OrderByDescending(x => x.AtMs))
            {
                if (now >= d.ExpiresAtMs) { DeleteFileLocked(d.Id); continue; }
                if (_items.Count >= MaxItems) { DeleteFileLocked(d.Id); continue; }
                if (string.IsNullOrWhiteSpace(d.Id) || _items.ContainsKey(d.Id)) continue;
                byte[] bytes = [];
                if (_root is not null)
                {
                    var path = Path.Combine(_root, d.Id + ".bin");
                    try
                    {
                        if (!File.Exists(path)) continue; // 元数据有、文件没了：不可审批，跳过
                        bytes = File.ReadAllBytes(path);
                    }
                    catch { continue; }
                }
                if (bytes.LongLength == 0) continue;
                if (_items.Values.Sum(x => x.Bytes.LongLength) + bytes.LongLength > MaxTotalBytes)
                {
                    DeleteFileLocked(d.Id);
                    continue;
                }
                _items[d.Id] = new PendingImageWrite
                {
                    Id = d.Id,
                    OwnerUserId = d.OwnerUserId,
                    SkillId = d.SkillId,
                    AgentId = d.AgentId,
                    LibId = d.LibId,
                    FileName = d.FileName,
                    ContentType = d.ContentType,
                    Bytes = bytes,
                    Caption = d.Caption,
                    Tags = d.Tags,
                    AtMs = d.AtMs,
                    ExpiresAtMs = d.ExpiresAtMs,
                };
            }
        }
    }

    private void PruneLocked(long nowMs)
    {
        foreach (var id in _items.Where(kv => nowMs >= kv.Value.ExpiresAtMs).Select(kv => kv.Key).ToList())
        {
            _items.Remove(id);
            DeleteFileLocked(id);
        }
    }

    private void DeleteFileLocked(string id)
    {
        if (_root is null) return;
        try { File.Delete(Path.Combine(_root, id + ".bin")); } catch { /* 忽略 */ }
    }

    /// <summary>启动时清理超过 TTL 的孤儿字节文件（持久化关闭时不会恢复元数据，避免磁盘泄漏）。</summary>
    private void CleanupOrphanFiles()
    {
        if (_root is null) return;
        try
        {
            var cutoff = DateTime.UtcNow - Ttl;
            foreach (var path in Directory.EnumerateFiles(_root, "*.bin"))
            {
                try { if (File.GetLastWriteTimeUtc(path) < cutoff) File.Delete(path); } catch { /* 忽略 */ }
            }
        }
        catch { /* 忽略 */ }
    }
}

/// <summary>持久化扩展区「imageWritePending」（待审批）+「imageWriteHistory」（最近处理，1.0.284）。</summary>
public static class ImageWriteApprovalPersistence
{
    public static void RegisterImageWriteApprovalPersistence(this IServiceProvider services)
    {
        var queue = services.GetRequiredService<ImageWriteApprovalQueue>();

        void Register(string name, Func<object?> snapshot, Action<JsonElement> restore)
        {
            var persistence = services.GetService<PersistenceService>();
            if (persistence is not null)
                persistence.AddSection(name, snapshot, restore);
            else
                services.GetService<ISectionStore>()?.AddSection(name, snapshot, restore);
        }

        Register("imageWritePending",
            () => queue.Snapshot().ToList(),
            element =>
            {
                var items = element.Deserialize<List<PendingImageWriteDto>>(AguiJson.Options);
                queue.Restore(items);
            });

        Register("imageWriteHistory",
            () => queue.SnapshotHistory().ToList(),
            element =>
            {
                try
                {
                    var items = element.Deserialize<List<ResolvedImageWriteDto>>(AguiJson.Options);
                    queue.RestoreHistory(items);
                }
                catch { /* 旧 / 损坏数据：忽略，不阻断启动 */ }
            });
    }
}
