namespace AguiGroupChat.Hub.Options;

/// <summary>一条 API 密钥：映射到指定的系统用户名（登录即可用的账号，须已注册）。</summary>
public sealed class ApiKeyEntry
{
    /// <summary>密钥（明文，调用侧 Authorization: Bearer &lt;apiKey&gt;）。</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>该密钥代表的用户名（绑定到其账号身份，继承其群成员 / 权限）。</summary>
    public string Username { get; set; } = "";
}

/// <summary>用户认证配置（appsettings.json 的 Auth 节点）。</summary>
public sealed class AuthOptions
{
    /// <summary>登录会话有效期（小时），滑动续期。</summary>
    public int SessionTtlHours { get; set; } = 168;

    /// <summary>
    /// WS / SSE 是否强制要求有效 token。为 true（默认，公网部署建议保持开启）时：未携带或
    /// 无效 token 一律 401 拒绝，防止无 token 时凭 ?memberId= 任意冒充他人身份。
    /// 为 false 时：携带 token 则按令牌身份连接、校验失败拒绝；未携带则回退到旧的 memberId
    /// 直连模式（兼容现有客户端与测试）。
    /// </summary>
    public bool RequireTokenOnRealTime { get; set; } = true;

    /// <summary>
    /// 系统管理员名单（逗号分隔的 userId 或 username，与账号标记 IsAdmin 叠加生效）。
    /// 导出/导入/重置/模型配置等管理操作仅管理员可执行。
    /// </summary>
    public string AdminUserIds { get; set; } = "";

    /// <summary>首个注册账号自动成为管理员（默认开启：单机 / 桌面部署的首个用户即管理员）。</summary>
    public bool FirstUserIsAdmin { get; set; } = true;

    /// <summary>
    /// WS / SSE 跨站来源白名单（逗号分隔的完整 Origin，如 https://chat.example.com）。
    /// 空 = 仅允许同源连接（浏览器跨站页面无法冒充建立实时连接，防 CSWSH）；
    /// 非浏览器客户端（无 Origin 头）不受影响。
    /// </summary>
    public string AllowedOrigins { get; set; } = "";

    /// <summary>会话绝对过期天数：滑动续期之上叠加硬上限（被盗令牌即使持续续期也会过期，默认 30 天）。</summary>
    public int AbsoluteSessionTtlDays { get; set; } = 30;

    /// <summary>
    /// 对外程序化访问的 **API 密钥**（6.4）：<c>[{ "apiKey": "...", "username": "..." }]</c>，
    /// 客户端可用 <c>Authorization: Bearer &lt;apiKey&gt;</c> 免登录地以该用户名身份调用 HTTP API
    /// （继承该账号的群成员 / 权限 / 管理员标记）。用于脚本 / 集成对接；注意明文保存、请使用强随机值，
    /// 并限制到只读或最小权限账号。留空则不启用。
    /// </summary>
    public List<ApiKeyEntry> ApiKeys { get; set; } = [];
}
