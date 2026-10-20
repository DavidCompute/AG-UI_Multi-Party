using AguiGroupChat.Hub.Infra;

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

/// <summary>
/// 图库写入审批队列（1.0.279，内存 + TTL）：
/// <list type="bullet">
/// <item>只用于技能写**已有/授权图库**；写本轮新建的库仍立即生效（隔离、风险低）。</item>
/// <item>每项保留图片字节（最大 10MB），因此总字节 / 条数 / 每人条数都有硬上限，防止被技能刷爆内存。</item>
/// <item>24 小时未处理自动丢弃（不是持久队列；重启会丢失，与「短生命周期审批」定位一致）。</item>
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
            _items[item.Id] = item;
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
            item = found;
            return true;
        }
    }

    public int Count { get { lock (_lock) return _items.Count; } }

    private void PruneLocked(long nowMs)
    {
        foreach (var id in _items.Where(kv => nowMs >= kv.Value.ExpiresAtMs).Select(kv => kv.Key).ToList())
            _items.Remove(id);
    }
}
