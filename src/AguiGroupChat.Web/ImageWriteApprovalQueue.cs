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

    private readonly object _lock = new();
    private readonly Dictionary<string, PendingImageWrite> _items = new(StringComparer.Ordinal);
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

    /// <summary>取出并移除（批准 / 拒绝都先取出）。</summary>
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

    public int Count { get { lock (_lock) return _items.Count; } }

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

/// <summary>持久化扩展区「imageWritePending」：待审批元数据跨重启保持（字节在磁盘）。</summary>
public static class ImageWriteApprovalPersistence
{
    public static void RegisterImageWriteApprovalPersistence(this IServiceProvider services)
    {
        var queue = services.GetRequiredService<ImageWriteApprovalQueue>();
        Func<object?> snapshot = () => queue.Snapshot().ToList();
        Action<JsonElement> restore = element =>
        {
            var items = element.Deserialize<List<PendingImageWriteDto>>(AguiJson.Options);
            queue.Restore(items);
        };

        var persistence = services.GetService<PersistenceService>();
        if (persistence is not null)
            persistence.AddSection("imageWritePending", snapshot, restore);
        else
            services.GetService<ISectionStore>()?.AddSection("imageWritePending", snapshot, restore);
    }
}
