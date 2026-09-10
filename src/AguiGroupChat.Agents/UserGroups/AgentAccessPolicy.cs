using AguiGroupChat.Hub.Models;

namespace AguiGroupChat.Agents.UserGroups;

/// <summary>
/// 基于「用户组白名单」的数字员工访问判定。规则（与 IsPrivate / OwnerId 叠加）：
/// <ul>
///   <li>系统级 / 无 owner 数字员工：非私密时仍受 groupId 白名单约束；无白名单 = 全员默认可见（向后兼容）。</li>
///   <li>系统管理员（<see cref="PlatformRole.Admin"/>/<c>SuperAdmin</c>）：始终放行（管理是全局的，不被组白名单挡住）。</li>
///   <li>普通用户：私密且非 owner → 拒绝（沿用旧逻辑）；否则若 <c>AllowedGroupIds</c> 为空 → 放行；
///       非空 → 仅当该用户属于其中任一用户组才放行。</li>
/// </ul>
/// 这一判定被<b>三处共用</b>：目录列表 / 单聊入口 / 拉入群成员预检，确保“可见 = 可用 = 可拉”。
/// </summary>
public static class AgentAccessPolicy
{
    /// <summary>该用户是否被允许“看到 / 单聊 / 拉入”此数字员工。</summary>
    public static bool CanAccess(
        UserGroupStore groups,
        bool isAdmin,
        string? callerUserId,
        AgentDefinition def)
    {
        if (callerUserId is null) return false;
        if (def.IsSkillTarget) return false;                       // 技能目标永不直接可见（先于 admin：管理员也不得直连技能子代理）
        if (isAdmin) return true;                                  // 管理员全局可见
        if (def.OwnerId is not null && def.OwnerId == callerUserId) return true; // 创建者始终可管自己创建的数字员工（编辑/单聊/列表）
        if (def.IsPrivate) return false;                           // 非 own 且私密：不可见
        var allowed = def.AllowedGroupIds;
        if (allowed is null || allowed.Count == 0) return true;    // 无组白名单 = 不限制（向后兼容）
        return groups.UserInAny(callerUserId, allowed);            // 命中任一授权用户组
    }

    /// <summary>为“拉入/注册触发/启单聊”保留的强校验：CanAccess 之外，仍是仅 owner（+admin）可见。</summary>
    public static bool CanAccess(string? callerUserId, bool isAdmin, string? ownerIdOfAgent)
        => callerUserId is not null && (isAdmin || ownerIdOfAgent is null || ownerIdOfAgent == callerUserId);

    /// <summary>
    /// 目录（数字员工列表 / 成员勾选）可见性判定。与 <see cref="CanAccess"/> 同源，但多一条匿名规则：
    /// 未登录者仍可看到「公开且未限定用户组」的数字员工（目录是登录前也可浏览的公开面）。
    /// 供目录列表与单聊入口共用，避免同一授权规则出现两份实现而漂移。
    /// </summary>
    /// <param name="callerUserId">登录用户 id；匿名传 null。</param>
    public static bool CanSeeInCatalog(
        UserGroupStore groups,
        bool isAdmin,
        string? callerUserId,
        AgentDefinition def)
    {
        // 技能目标（系统自生成的子代理）不出现在目录中
        if (def.IsSkillTarget) return false;
        if (callerUserId is null)
            return !def.IsPrivate && def.AllowedGroupIds is not { Count: > 0 };
        return CanAccess(groups, isAdmin, callerUserId, def);
    }
}
