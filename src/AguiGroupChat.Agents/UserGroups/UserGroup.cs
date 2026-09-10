using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using AguiGroupChat.Hub.Users;

namespace AguiGroupChat.Agents.UserGroups;

/// <summary>
/// 用户分组 / 组织单元：用于对<b>平台账号</b>做编组，并以「组 → 资源」授权控制更细粒度访问
/// （例如：哪些分组可访问某数字员工）。组成员是 userId（<see cref="UserAccount.UserId"/>，user_xxx），
/// 与“群聊/知聚（Group）”是<b>两个不同概念</b>——这里是权限语义的「用户组」，不是聊天群。
/// </summary>
public sealed class UserGroup
{
    /// <summary>用户组 ID（ug_xxx）。</summary>
    public required string GroupId { get; init; }

    /// <summary>组名（唯一，展示用）。</summary>
    public string Name { get; set; } = "";

    /// <summary>一句说明（可选）。</summary>
    public string Description { get; set; } = "";

    /// <summary>组成员 userId 列表（可为空）。成员可在多个组中。</summary>
    public List<string> MemberUserIds { get; set; } = [];

    /// <summary>创建时间。</summary>
    [JsonIgnore]
    public long CreatedAtMs { get; set; }
}

/// <summary>一组构成用户授权环的组目录快照（用于持久化，形如 {group,resource} 均可在 section 里整块存储）。</summary>
/// <remarks>当前用途：用户名下可访问的数字员工白名单按“组”建模（<c>AgentDefinition</c> 增加 AllowedGroupIds）。</remarks>
public sealed class UserGroupsSnapshot
{
    public int FormatVersion { get; set; } = 1;
    public List<UserGroupPromptElement>? Groups { get; set; }
}

/// <summary>与 UserGroup 相同字段但在序列化时可显式往返的载体（收敛 Model 与 section 载荷差异）。</summary>
public sealed class UserGroupPromptElement
{
    public string? GroupId { get; set; }
    public string? Name { get; set; }
    public string? Description { get; set; }
    public List<string>? MemberUserIds { get; set; }
    public long CreatedAtMs { get; set; }
}
