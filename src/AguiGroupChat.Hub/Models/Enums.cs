namespace AguiGroupChat.Hub.Models;

/// <summary>成员类型：用户 / 智能体（协议 2.2）。</summary>
public enum MemberType { User, Agent }

/// <summary>
/// 平台级角色（系统管理员分层）：权限从低到高 ——
///  <see cref="User"/> 普通用户；<see cref="Operator"/> 运维（只读系统状态 / 审计 / 用量 / 启停账号，无数据导出、模型配置、品牌、治理）；
///  <see cref="Admin"/> 系统管理员（既有 IsAdmin 的完整管理权限 + 运维权限）；
///  <see cref="SuperAdmin"/> 超级管理员（在 Admin 之上可管理管理员名单 / 授予或回收他人平台角色）。
/// 向后兼容：账号 <see cref="AguiGroupChat.Hub.Users.UserAccount.IsAdmin"/> 为 true 或命中 Auth:AdminUserIds 时，其生效角色至少为 <see cref="Admin"/>。
/// </summary>
public enum PlatformRole { User = 0, Operator = 1, Admin = 2, SuperAdmin = 3 }

/// <summary>平台角色名工具：对 API 输出 camelCase（user / operator / admin / superadmin）。</summary>
public static class PlatformRoleUtil
{
    public static string Name(PlatformRole role)
    {
        // 角色名统一为全小写（user / operator / admin / superadmin），与前端 i18n key 及 Enum.TryParse 一致；
        // 不能用只小写首字母——SuperAdmin 会变成 superAdmin 而与约定不符。
        return role.ToString().ToLowerInvariant();
    }
}
/// <summary>群角色：群主 / 管理员 / 普通成员（协议 2.2）。</summary>
public enum GroupRole { Owner, Admin, Normal }

/// <summary>知聚类型（在公有 / 私有之上扩展）。</summary>
public enum GroupKind
{
    /// <summary>普通知聚（公有 / 私有，按 IsPrivate 区分可见与记忆作用域）。</summary>
    Normal,

    /// <summary>客服知聚：创建者拉客服团队（真人 + 数字员工，均视为客服）进入；
    /// 其它用户可看到并进入，但非客服成员进入后只能看到自己的会话内容（客服可见全部）。</summary>
    Support,
}

/// <summary>在线状态（协议 2.2）。</summary>
public enum OnlineStatus { Online, Offline, Busy }

/// <summary>消息可见范围（协议 2.3）。</summary>
public enum MessageVisibility { All, Mentioned, Private }

/// <summary>离群类型（协议 4.3）：主动退群 / 被移出。</summary>
public enum LeaveType { Voluntary, Kick }

/// <summary>
/// 智能体触发模式（协议 §6）。
/// </summary>
public enum AgentTriggerMode
{
    /// <summary>提及触发：被 @ 或 @全体时才发言。</summary>
    Mentioned,

    /// <summary>全量监听：每条消息都接收（是否回复由网关自行决定）。</summary>
    AllMessages,

    /// <summary>关键词触发：命中关键词才发言。</summary>
    Keyword,

    /// <summary>
    /// 语境触发：对所有消息做语境判断，由模型根据上下文自主决定是否发言
    /// （不要求 @ 或关键词）。判断由 IAgentGateway 实现，返回 AGENT_DECIDED_SILENT 表示保持沉默。
    /// </summary>
    Contextual,
}
