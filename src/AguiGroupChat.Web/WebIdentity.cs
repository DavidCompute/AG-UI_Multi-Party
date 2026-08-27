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
    /// <summary>经 <see cref="ResolveIdentityFilter"/> 解析后，把身份写入 HttpContext.Items 的键。</summary>
    public const string IdentityKey = "webidentity";

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

    /// <summary>从 HttpContext.Items 取回 <see cref="ResolveIdentityFilter"/> 解析并暂存的用户 ID；未校验通过返回 null。</summary>
    public static string? UserId(HttpContext ctx)
        => ctx.Items.TryGetValue(IdentityKey, out var v) ? v as string : null;

    /// <summary>由 HttpContext.Items 中已解析的用户 ID，回读该账号（供需要完整 <see cref="UserAccount"/> 的 handler 用）。</summary>
    public static UserAccount? User(HttpContext ctx, AuthService auth)
    {
        var id = UserId(ctx);
        return id is null ? null : auth.GetUser(id);
    }

    /// <summary>严格令牌身份过滤器（token / ApiKey 二选一，不回退 ?memberId= 演示身份）。
    /// 用于需完整账号且须明确登录的操作（智能体管理 / 分身 / 知识库 / 技能），语义与 <see cref="AgentApi.RequireUser"/> 一致。</summary>
    public sealed class RequireTokenFilter : IEndpointFilter
    {
        public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
        {
            var http = context.HttpContext;
            var auth = http.RequestServices.GetRequiredService<AuthService>();
            var token = ResolveToken(http.Request);
            var user = string.IsNullOrEmpty(token) ? null : (auth.ValidateToken(token) ?? auth.ResolveApiKey(token));
            if (user is null)
                return Results.Json(new AguiError(ErrorCodes.UserUnauthorized, "未登录或令牌无效"),
                    statusCode: StatusCodes.Status401Unauthorized);
            http.Items[IdentityKey] = user.UserId;
            return await next(context);
        }
    }

    /// <summary>由 <see cref="WebIdentity"/> 实现的常规登录身份端点过滤器：先解析再放行，失败以统一错误中止。</summary>
    public sealed class RequireIdentityFilter : IEndpointFilter
    {
        public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
        {
            var http = context.HttpContext;
            var auth = http.RequestServices.GetRequiredService<AuthService>();
            var authOptions = http.RequestServices.GetRequiredService<AuthOptions>();
            var (userId, error) = ResolveIdentity(http, auth, authOptions);
            if (error is not null) return error;
            http.Items[IdentityKey] = userId;
            return await next(context);
        }
    }

    /// <summary>需要系统管理员身份的端点过滤器（在 <see cref="RequireIdentityFilter"/> 基础上追加管理员校验）。</summary>
    public sealed class RequireAdminFilter : IEndpointFilter
    {
        public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
        {
            var http = context.HttpContext;
            var auth = http.RequestServices.GetRequiredService<AuthService>();
            var authOptions = http.RequestServices.GetRequiredService<AuthOptions>();
            var (userId, error) = RequireAdmin(http, auth, authOptions);
            if (error is not null) return error;
            http.Items[IdentityKey] = userId;
            return await next(context);
        }
    }
}
