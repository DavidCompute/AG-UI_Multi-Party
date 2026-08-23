namespace AguiGroupChat.Sdk;

/// <summary>
/// Hub HTTP 上行错误响应体：{"code":"...","message":"..."}。
/// 错误码全集见 <see cref="ErrorCodes"/>。
/// </summary>
public sealed record AguiError(string Code, string Message);

/// <summary>
/// 协议错误码（协议 §7 扩展，与 Hub 的 <c>ErrorCodes</c> 对齐）。
/// </summary>
public static class ErrorCodes
{
    public const string GroupNotFound = "GROUP_NOT_FOUND";
    public const string GroupPermissionDenied = "GROUP_PERMISSION_DENIED";
    public const string GroupMemberNotExist = "GROUP_MEMBER_NOT_EXIST";
    public const string GroupFull = "GROUP_FULL";
    public const string GroupMessageNotFound = "GROUP_MESSAGE_NOT_FOUND";
    public const string GroupSubscribeFailed = "GROUP_SUBSCRIBE_FAILED";
    public const string BadRequest = "BAD_REQUEST";

    public const string UserNotFound = "USER_NOT_FOUND";
    public const string UserExists = "USER_EXISTS";
    public const string UserBadCredentials = "USER_BAD_CREDENTIALS";
    public const string UserPasswordInvalid = "USER_PASSWORD_INVALID";
    public const string UserUnauthorized = "USER_UNAUTHORIZED";

    public const string AgentNotFound = "AGENT_NOT_FOUND";
    public const string AgentExists = "AGENT_EXISTS";
    public const string AgentPermissionDenied = "AGENT_PERMISSION_DENIED";
}
