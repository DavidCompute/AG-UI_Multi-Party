using AguiGroupChat.Hub.Models;

namespace AguiGroupChat.Hub.Users;

/// <summary>
/// 用户账号（Hub 扩展）。UserId 采用 user_xxx 格式，与群成员体系（memberId）直接复用，
/// 注册用户可被添加为群成员并参与群聊。
/// </summary>
public sealed class UserAccount
{
    /// <summary>用户 ID（user_xxx），即群成员体系中的 memberId。</summary>
    public required string UserId { get; init; }

    /// <summary>登录名（全局唯一，大小写不敏感，不可变）。</summary>
    public required string Username { get; init; }

    /// <summary>PBKDF2 密码哈希（Base64）。</summary>
    public required string PasswordHash { get; set; }

    /// <summary>PBKDF2 随机盐（Base64）。</summary>
    public required string PasswordSalt { get; set; }

    /// <summary>显示昵称（建群时作为群内默认昵称）。</summary>
    public string Nickname { get; set; } = "";

    public string? Avatar { get; set; }

    /// <summary>
    /// 是否开启个人记忆（默认关闭）。开启后，智能体回复该用户时才会检索注入 TA 的历史发言
    /// （跨群、遵守私密群隔离）。关闭时 TA 的发言仍参与群记忆，但不作为个人记忆被读取。
    /// </summary>
    public bool PersonalMemoryEnabled { get; set; }

    /// <summary>注册时间戳（毫秒级）。</summary>
    public long CreatedAt { get; init; }

    /// <summary>最近资料 / 密码变更时间戳（毫秒级）。</summary>
    public long UpdatedAt { get; set; }

    /// <summary>
    /// 是否系统管理员：可执行导出/导入/重置/模型配置等管理操作。
    /// 首个注册账号默认自动成为管理员（Auth:FirstUserIsAdmin），亦可经 Auth:AdminUserIds 显式指定。
    /// 为向后兼容保留：新代码优先使用 <see cref="PlatformRole"/>，<see cref="IsAdmin"/> 的语义映射为「生效角色至少为 Admin」。
    /// </summary>
    public bool IsAdmin { get; set; }

    /// <summary>
    /// 平台级角色（RBAC 分层）：User / Operator / Admin / SuperAdmin。
    /// 显式设置时优先；未显式时按 <see cref="IsAdmin"/> / Auth:AdminUserIds 推导为至少 <see cref="PlatformRole.Admin"/>。
    /// </summary>
    public PlatformRole PlatformRole { get; set; } = PlatformRole.User;

    /// <summary>
    /// 是否被管理员禁用（默认否）。禁用后无法登录，已有会话令牌立即失效（管理员控制台操作）。
    /// </summary>
    public bool IsDisabled { get; set; }
}
