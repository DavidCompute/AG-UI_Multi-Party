using AguiGroupChat.Hub.Models;
using AguiGroupChat.Hub.Options;
using AguiGroupChat.Hub.Users;

namespace AguiGroupChat.Web;

/// <summary>
/// Web HTTP API 共享身份解析：优先校验 Authorization: Bearer 或 ?token=；
/// 无 token 时，<see cref="AuthOptions.RequireTokenOnRealTime"/> = true（默认）一律 401
/// （不再信任 ?memberId=），为 false 时回退信任 ?memberId=（与 WS/SSE 演示模式一致）。
/// </summary>
internal static class WebIdentity
{
    public static (string? UserId, IResult? Error) ResolveIdentity(HttpContext ctx, AuthService auth, AuthOptions authOptions)
    {
        var token = ResolveToken(ctx.Request);
        if (!string.IsNullOrEmpty(token))
        {
            // 1) 会话令牌
            var user = auth.ValidateToken(token);
            if (user is null)
            {
                // 2) 对外 API 密钥（6.4）：命中配置的 ApiKeys 时以绑定用户身份访问（免登录程序化接入）
                user = auth.ResolveApiKey(token);
            }
            if (user is null)
                return (null, Results.Json(new AguiError(ErrorCodes.UserUnauthorized, "未登录或令牌无效"),
                    statusCode: StatusCodes.Status401Unauthorized));
            return (user.UserId, null);
        }

        // 强制令牌模式：无有效 token 一律拒绝，不信任 ?memberId= 回退（防任意冒充）
        if (authOptions.RequireTokenOnRealTime)
            return (null, Results.Json(new AguiError(ErrorCodes.UserUnauthorized, "缺少身份令牌（Auth:RequireTokenOnRealTime=true）"),
                statusCode: StatusCodes.Status401Unauthorized));

        var memberId = ctx.Request.Query["memberId"].ToString();
        return string.IsNullOrWhiteSpace(memberId)
            ? (null, Results.Json(new AguiError(ErrorCodes.UserUnauthorized, "缺少身份（登录 token 或 memberId）"),
                statusCode: StatusCodes.Status401Unauthorized))
            : (memberId.Trim(), null);
    }

    public static string? ResolveToken(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return header["Bearer ".Length..].Trim();
        var query = request.Query["token"].ToString();
        return string.IsNullOrEmpty(query) ? null : query;
    }

    /// <summary>
    /// 管理员身份解析（导出/导入/重置/模型配置等管理操作专用）：先按 <see cref="ResolveIdentity"/>
    /// 校验登录身份，再校验该用户为系统管理员（AuthService.IsAdmin）。
    /// </summary>
    public static (string? UserId, IResult? Error) RequireAdmin(HttpContext ctx, AuthService auth, AuthOptions authOptions)
    {
        var (userId, error) = ResolveIdentity(ctx, auth, authOptions);
        if (userId is null) return (null, error);
        if (!auth.IsAdmin(userId))
            return (null, Results.Json(new AguiError(ErrorCodes.GroupPermissionDenied, "仅系统管理员可执行此操作"),
                statusCode: StatusCodes.Status403Forbidden));
        return (userId, null);
    }
}
