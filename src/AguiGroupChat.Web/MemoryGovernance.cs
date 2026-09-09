using AguiGroupChat.Hub.Models;
using AguiGroupChat.Hub.Storage;
using AguiGroupChat.Hub.Users;

namespace AguiGroupChat.Web;

/// <summary>
/// 记忆治理的「角色 × 可管理范围」判定（服务端唯一口径）。
///
/// 分级：
///   <b>Global（平台管理员 Admin / SuperAdmin）</b>——跨全部知聚查看 / 治理任意记忆（等同原 IsAdmin 语义）；
///   <b>GroupManager（群内群主 / 管理员）</b>——仅在其 Owner / Admin 的知聚内可管理<b>整群</b>记忆
///     （分级 / 删除他人记忆、整群遗忘、导入该群），普通群成员不行；
///   <b>MemberOwn（普通成员）</b>——仅可查看所在群记忆，分级 / 删除 / 遗忘只作用于<b>本人</b>发言；
///   <b>Operator（运维）</b>——不因平台角色获得记忆管理特权（保持普通成员口径，避免只读运维被当成治理者）。
/// </summary>
public static class MemoryGovernance
{
    /// <summary>平台级全局记忆管理者：生效角色 ≥ Admin（IsAdmin 已覆盖 Admin/SuperAdmin 与旧配置名单）。</summary>
    public static bool IsGlobalManager(AuthService auth, string? userId) => auth.IsAdmin(userId);

    /// <summary>是否为某知聚的群主 / 群管理员（在该知聚内可整群治理）。</summary>
    public static bool IsGroupManager(IGroupStore store, string userId, string groupId)
        => store.GetMember(groupId, userId)?.Role is GroupRole.Owner or GroupRole.Admin;

    /// <summary>在指定知聚内是否可整群治理（全局管理员 或 该群群主 / 管理员）。</summary>
    public static bool CanManageAllInGroup(IGroupStore store, AuthService auth, string userId, string groupId)
        => IsGlobalManager(auth, userId) || IsGroupManager(store, userId, groupId);

    /// <summary>单条记忆是否可分级 / 删除：全局管理员、该记忆所在群群主 / 管理员、或记忆本人。</summary>
    public static bool CanManageItem(IGroupStore store, AuthService auth, string userId, MessageMemoryItem item)
        => CanManageAllInGroup(store, auth, userId, item.GroupId) || item.SenderId == userId;
}
