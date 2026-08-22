namespace AguiGroupChat.Hub.Models;

/// <summary>协议错误码（协议 §7 错误码扩展）。</summary>
public static class ErrorCodes
{
    /// <summary>群组不存在。</summary>
    public const string GroupNotFound = "GROUP_NOT_FOUND";

    /// <summary>无群组操作权限。</summary>
    public const string GroupPermissionDenied = "GROUP_PERMISSION_DENIED";

    /// <summary>目标成员不在群组内。</summary>
    public const string GroupMemberNotExist = "GROUP_MEMBER_NOT_EXIST";

    /// <summary>群成员数量达上限。</summary>
    public const string GroupFull = "GROUP_FULL";

    /// <summary>消息不存在或已撤回。</summary>
    public const string GroupMessageNotFound = "GROUP_MESSAGE_NOT_FOUND";

    /// <summary>群组订阅失败。</summary>
    public const string GroupSubscribeFailed = "GROUP_SUBSCRIBE_FAILED";

    /// <summary>Hub 扩展：请求格式错误（无法解析 / 缺少字段）。</summary>
    public const string BadRequest = "BAD_REQUEST";

    // ---- 用户管理（Hub 扩展）----

    /// <summary>用户不存在。</summary>
    public const string UserNotFound = "USER_NOT_FOUND";

    /// <summary>用户名已被注册。</summary>
    public const string UserExists = "USER_EXISTS";

    /// <summary>用户名或密码错误。</summary>
    public const string UserBadCredentials = "USER_BAD_CREDENTIALS";

    /// <summary>旧密码不正确（修改密码时）。</summary>
    public const string UserPasswordInvalid = "USER_PASSWORD_INVALID";

    /// <summary>未登录或令牌无效 / 已过期。</summary>
    public const string UserUnauthorized = "USER_UNAUTHORIZED";

    // ---- 智能体管理（Hub 扩展）----

    /// <summary>智能体不存在（未在目录中声明）。</summary>
    public const string AgentNotFound = "AGENT_NOT_FOUND";

    /// <summary>智能体 ID 已被占用。</summary>
    public const string AgentExists = "AGENT_EXISTS";

    /// <summary>私密智能体仅创建者可操作（拉入群 / 编辑 / 删除）。</summary>
    public const string AgentPermissionDenied = "AGENT_PERMISSION_DENIED";
}

/// <summary>HTTP 上行错误响应体：{"code": "...", "message": "..."}。</summary>
public sealed record AguiError(string Code, string Message);
