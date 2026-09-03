using System.Collections.Concurrent;
using AguiGroupChat.Hub.Persistence;
using Microsoft.Extensions.Logging;

namespace AguiGroupChat.Agents;

/// <summary>
/// 一份「组织 / 客服 / 办公团队」设计稿草稿。由组织编排类智能体（org_architect 等）在运行时经内置工具
/// <c>org_draft_save / org_draft_load / org_draft_list / org_draft_delete</c> 读写。
/// 设计稿只表达「方案文本」，<b>绝不自动落库创建数字员工 / 技能</b>；真正创建走一键编排的人工确认。
/// </summary>
public sealed class OrgDraft
{
    /// <summary>草稿归属群（同一组织群的多个话题都能读取同一份草稿，便于跨话题续改）。</summary>
    public string GroupId { get; set; } = "";

    /// <summary>草稿短标识（模型自定义，如 it_support_v1），同一群内唯一。</summary>
    public string Slug { get; set; } = "";

    /// <summary>创建时所在的群话题（仅记录来源，不限制读取）。</summary>
    public string TopicId { get; set; } = "main";

    /// <summary>创建者 / 最后修改者 UserId。</summary>
    public string OwnerId { get; set; } = "";

    /// <summary>标题（组织名 / 团队名）。</summary>
    public string Title { get; set; } = "";

    /// <summary>结构化组织方案正文（Markdown / JSON 文本）。</summary>
    public string Content { get; set; } = "";

    /// <summary>修改版本号（每次保存 +1，从 1 开始）。</summary>
    public int Version { get; set; }

    /// <summary>创建时间戳（UTC 毫秒）。</summary>
    public long CreatedAtMs { get; set; }

    /// <summary>最后修改时间戳（UTC 毫秒）。</summary>
    public long UpdatedAtMs { get; set; }
}

/// <summary>
/// 组织设计稿草稿库：以「群 + slug」作用域保存 / 取回结构化组织方案，供数字员工跨话题、跨空档多轮续改。
/// 内存态 = 权威数据；经 <see cref="AgentHosting.RegisterOrgDraftPersistence"/> 注册到持久化扩展区「orgDrafts」，
/// 变更通过 <see cref="ChangeHub"/> 通知落盘 / 落库（与其它目录一致），重启不丢。
/// </summary>
public sealed class OrgDraftStore
{
    private readonly ILogger<OrgDraftStore> _logger;
    private readonly ChangeHub? _changes;
    private readonly ConcurrentDictionary<string, OrgDraft> _byKey = new(StringComparer.Ordinal);

    public OrgDraftStore(ILoggerFactory loggerFactory, ChangeHub? changes = null)
    {
        _logger = loggerFactory.CreateLogger<OrgDraftStore>();
        _changes = changes;
    }

    private static string Key(string groupId, string slug) => $"{groupId}\u0001{slug}";

    /// <summary>已保存草稿的群内短标识列表（含标题 / 版本 / 修改时间，不含正文，便于模型 <c>org_draft_list</c> 概览后挑 <c>org_draft_load</c>）。</summary>
    public IReadOnlyList<OrgDraftSummary> List(string groupId)
    {
        var now = _byKey.Values
            .Where(d => d.GroupId == groupId)
            .Select(d => new OrgDraftSummary(d.Slug, d.Title, d.Version, d.TopicId, d.OwnerId, d.UpdatedAtMs))
            .OrderByDescending(s => s.UpdatedAtMs)
            .ToList();
        return now;
    }

    /// <summary>按 slug 取回草稿全文；不存在返回 null。</summary>
    public OrgDraft? Load(string groupId, string slug)
        => _byKey.TryGetValue(Key(groupId, slug), out var d) ? Clone(d) : null;

    /// <summary>新建或整稿覆盖保存：同 slug 存在则替换为最新版本（版本 +1）；slug 为新则版本 1。</summary>
    public OrgDraft Save(string groupId, string slug, string topicId, string ownerId, string title, string content)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var key = Key(groupId, slug);
        var updated = _byKey.AddOrUpdate(key,
            _ => new OrgDraft { GroupId = groupId, Slug = slug, TopicId = topicId, OwnerId = ownerId, Title = title, Content = content, Version = 1, CreatedAtMs = now, UpdatedAtMs = now },
            (_, existing) => new OrgDraft
            {
                GroupId = groupId,
                Slug = slug,
                TopicId = string.IsNullOrWhiteSpace(existing.TopicId) ? topicId : existing.TopicId,
                OwnerId = ownerId,
                Title = title,
                Content = content,
                Version = existing.Version + 1,
                CreatedAtMs = existing.CreatedAtMs == 0 ? now : existing.CreatedAtMs,
                UpdatedAtMs = now,
            });
        _changes?.Notify();
        return updated;
    }

    /// <summary>删除草稿；返回是否存在。重载 / 快照恢复时调用（不触发变更通知）。</summary>
    public bool Delete(string groupId, string slug)
    {
        var removed = _byKey.TryRemove(Key(groupId, slug), out _);
        if (removed) _changes?.Notify();
        return removed;
    }

    /// <summary>清空全部草稿（测试 / 调试用），不触发持久化通知。</summary>
    public void Clear()
    {
        _byKey.Clear();
        _changes?.Notify();
    }

    /// <summary>整体恢复（启动快照 / 落库恢复用）。</summary>
    public void RestoreAll(IEnumerable<OrgDraft> drafts)
    {
        _byKey.Clear();
        foreach (var d in drafts)
        {
            if (string.IsNullOrWhiteSpace(d?.GroupId) || string.IsNullOrWhiteSpace(d?.Slug)) continue;
            _byKey[Key(d.GroupId!, d.Slug!)] = d;
        }
    }

    /// <summary>快照：返回全部草稿副本，供持久化扩展区序列化。</summary>
    public IReadOnlyList<OrgDraft> SnapshotAll() => _byKey.Values.Select(Clone).ToList();

    private static OrgDraft Clone(OrgDraft d) => new()
    {
        GroupId = d.GroupId,
        Slug = d.Slug,
        TopicId = d.TopicId,
        OwnerId = d.OwnerId,
        Title = d.Title,
        Content = d.Content,
        Version = d.Version,
        CreatedAtMs = d.CreatedAtMs,
        UpdatedAtMs = d.UpdatedAtMs,
    };
}

/// <summary>列表用轻量摘要（不带正文）。</summary>
public sealed record OrgDraftSummary(
    string Slug, string Title, int Version, string TopicId, string OwnerId, long UpdatedAtMs);
