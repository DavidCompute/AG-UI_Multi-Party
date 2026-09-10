using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using AguiGroupChat.Hub.Infra;

namespace AguiGroupChat.Agents.UserGroups;

/// <summary>
/// 用户组目录（内存权威 + 变更订阅通知）。仅承载“分组成员关系”；是否把某资源授权给某组，
/// 由各<b>资源对象本身</b>携带的“允许访问的用户组 id 列表”（如 <c>AgentDefinition.AllowedGroupIds</c>）
/// 判定。空 / 未配置列表 = 不按用户组限制（沿用既有可见性），向后兼容。
/// </summary>
public sealed class UserGroupStore
{
    private readonly ConcurrentDictionary<string, UserGroup> _byId = new(StringComparer.Ordinal);
    private readonly Func<object?>? _notify;

    public UserGroupStore(Func<object?>? notify = null) => _notify = notify;

    public IReadOnlyList<UserGroup> List() => _byId.Values.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList();

    public UserGroup? Get(string groupId)
        => !string.IsNullOrWhiteSpace(groupId) && _byId.TryGetValue(groupId, out var g) ? g : null;

    /// <summary>新增 / 覆盖一个分组。返回是否新增。</summary>
    public UserGroup Upsert(UserGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);
        _byId[group.GroupId] = group;
        _notify?.Invoke();
        return group;
    }

    public bool Remove(string groupId)
    {
        var ok = _byId.TryRemove(groupId, out _);
        if (ok) _notify?.Invoke();
        return ok;
    }

    /// <summary>某 userId 是否命中给定的任一用户组（id 列表）。列表为空 / 全部未知返回 false（调用方按“空白名单=不设限”处理）。</summary>
    public bool UserInAny(string userId, IEnumerable<string>? groupIds)
    {
        if (groupIds is null) return false;
        foreach (var gid in groupIds)
        {
            if (string.IsNullOrWhiteSpace(gid)) continue;
            if (_byId.TryGetValue(gid, out var g) && (g.MemberUserIds?.Contains(userId, StringComparer.Ordinal) ?? false)) return true;
        }
        return false;
    }

    /// <summary>组内全部 userId（规范化去重）。</summary>
    public IReadOnlySet<string> MembersOf(string groupId)
        => _byId.TryGetValue(groupId, out var g)
            ? g.MemberUserIds?.ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);

    /// <summary>快照（持久化）：以新对象整体返回，避免调用方改到内部集合。</summary>
    public List<UserGroup> Snapshot() => _byId.Values
        .Select(g => new UserGroup { GroupId = g.GroupId, Name = g.Name, Description = g.Description, MemberUserIds = new List<string>(g.MemberUserIds ?? []), CreatedAtMs = g.CreatedAtMs })
        .ToList();

    public void RestoreAll(IEnumerable<UserGroup> groups)
    {
        _byId.Clear();
        foreach (var g in groups)
            if (!string.IsNullOrWhiteSpace(g.GroupId)) _byId[g.GroupId] = g;
    }

    /// <summary>将一节持久化载荷还原到目录（载荷形态：{Groups:[{...}]} 或裸数组）。非法则忽略，不影响已加载目录。</summary>
    public void RestoreSection(string payload)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(payload)) return;
            var trimmed = payload.TrimStart();
            IEnumerable<UserGroup>? els = trimmed.StartsWith('[')
                ? JsonSerializer.Deserialize<List<UserGroup>>(payload)
                : (JsonSerializer.Deserialize<UserGroupsSnapshot>(payload)?.Groups ?? [])
                    .Select(FromElement);
            if (els is null) return;
            RestoreAll(els.Where(g => !string.IsNullOrWhiteSpace(g.GroupId)).ToList());
        }
        catch { /* 载荷损坏：保留已正确读取的目录（已清空时由调用方决定） */ }
    }

    public string SnapshotJson()
        => JsonSerializer.Serialize(new UserGroupsSnapshot { Groups = Snapshot().Select(ToElement).ToList() }, JsonOpt);

    internal static UserGroupPromptElement ToElement(UserGroup g) => new()
    {
        GroupId = g.GroupId, Name = g.Name, Description = g.Description,
        MemberUserIds = new List<string>(g.MemberUserIds ?? []), CreatedAtMs = g.CreatedAtMs,
    };

    internal static UserGroup FromElement(UserGroupPromptElement e) => new()
    {
        GroupId = e.GroupId ?? "", Name = e.Name ?? "", Description = e.Description ?? "",
        MemberUserIds = e.MemberUserIds?.ToList() ?? [], CreatedAtMs = e.CreatedAtMs,
    };

    private static readonly JsonSerializerOptions JsonOpt = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
