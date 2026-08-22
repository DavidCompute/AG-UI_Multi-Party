using System.Text.Json;

namespace AguiGroupChat.Hub.Models;

/// <summary>群成员模型（协议 2.2）。用户为 user_xxx，智能体为 agent_xxx。</summary>
public sealed class GroupMember
{
    public required string MemberId { get; init; }

    public required MemberType MemberType { get; init; }

    /// <summary>群内显示昵称。</summary>
    public required string Nickname { get; set; }

    public string? Avatar { get; set; }

    /// <summary>群角色：owner / admin / normal。</summary>
    public required GroupRole Role { get; set; }

    /// <summary>在线状态：online / offline / busy。</summary>
    public OnlineStatus OnlineStatus { get; set; } = OnlineStatus.Online;

    /// <summary>入群时间戳（毫秒级）。</summary>
    public required long JoinTime { get; init; }

    /// <summary>群内触发模式（仅智能体成员；null = 未注册 / 跟随角色默认，见 <see cref="IsTriggerOverridden"/>）。</summary>
    public string? TriggerMode { get; set; }

    /// <summary>群内触发关键词（TriggerMode=keyword 时）。</summary>
    public IReadOnlyList<string>? Keywords { get; set; }

    /// <summary>是否在群内显式覆盖了角色默认触发模式（角色编辑不再覆写本群）。</summary>
    public bool IsTriggerOverridden { get; set; }

    public Dictionary<string, object?>? Extra { get; set; }

    /// <summary>
    /// 频道级 RBAC 权限（4.2）：以 <c>Extra["rbac"]</c> JSON 对象持久化（随 extra 列 / JSON 快照 round-trip）。
    /// 未设置时透传角色默认（全部允许，保持既有行为）；管理员可在群内显式限制某成员的细粒度能力。
    /// </summary>
    public GroupMemberPermissions? RbacPermissions
    {
        get => Extra is not null && Extra.TryGetValue("rbac", out var v) && v is GroupMemberPermissions p
            ? p
            : (Extra is not null && Extra.TryGetValue("rbac", out var v2) && v2 is JsonElement je ? FromPermissionsJson(je) : null);
        set
        {
            Extra ??= new(StringComparer.Ordinal);
            if (value is null) Extra.Remove("rbac");
            else Extra["rbac"] = value;
        }
    }

    /// <summary>是否允许该成员触发 / @ 智能体（提及 / 全量监听 / 关键词 / 语境触发）。未显式限制默认允许。</summary>
    public bool CanInvokeAgents => RbacPermissions?.CanInvokeAgents ?? true;

    /// <summary>是否允许该成员批准 / 拒绝人机交互审批（HITL 决策）。未显式限制默认允许。</summary>
    public bool CanApproveInteractions => RbacPermissions?.CanApprove ?? true;

    private static GroupMemberPermissions? FromPermissionsJson(JsonElement je)
        => System.Text.Json.JsonSerializer.Deserialize<GroupMemberPermissions>(je, System.Text.Json.JsonSerializerOptions.Default);
}

/// <summary>
/// 群成员频道级 RBAC 权限（4.2）：<c>CanInvokeAgents</c>（谁能触发 / @ 智能体）与
/// <c>CanApprove</c>（谁能批准人机交互）。显式置 false 时限制；布尔可空：null = 跟随角色默认允许。
/// 经 <see cref="GroupMember.RbacPermissions"/> 以 Extra["rbac"] 对象持久化。
/// </summary>
public sealed class GroupMemberPermissions
{
    /// <summary>是否允许触发 / @ 智能体（提及 / 全量监听 / 关键词 / 语境触发）。</summary>
    public bool? CanInvokeAgents { get; set; }

    /// <summary>是否允许批准 / 拒绝人机交互审批（HITL 决策）。</summary>
    public bool? CanApprove { get; set; }
}
