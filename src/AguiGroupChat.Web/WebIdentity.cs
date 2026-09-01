using AguiGroupChat.Hub.Models;
using AguiGroupChat.Hub.Users;

namespace AguiGroupChat.Web;

/// <summary>
/// Web HTTP API 共享身份解析：仅接受 Authorization: Bearer 或 ?token=（会话令牌 / 对外 API 密钥）。
/// <b>不再信任 <c>?memberId=</c></b>：这些端点是状态变更 / 敏感读取（附件、记忆、定时任务、链接代理、
/// 客户端技能桥、市场、模型配置），必须由真实认证身份发起，防无凭据身份冒充。
/// 需要 <c>?memberId=</c> 的实时通道（WS/SSE 演示、外部智能体桥）走各自独立的身份解析，不受影响。
/// </summary>
internal static class WebIdentity
{
    /// <summary>经 <see cref="ResolveIdentityFilter"/> 解析后，把身份写入 HttpContext.Items 的键。</summary>
    public const string IdentityKey = "webidentity";

    public static (string? UserId, IResult? Error) ResolveIdentity(HttpContext ctx, AuthService auth)
    {
        var token = ResolveToken(ctx.Request);
        if (string.IsNullOrEmpty(token))
            return (null, Results.Json(new AguiError(ErrorCodes.UserUnauthorized, "缺少身份令牌（登录会话或 API 密钥）"),
                statusCode: StatusCodes.Status401Unauthorized));

        // 1) 会话令牌，2) 对外 API 密钥（6.4）：命中配置的 ApiKeys 时以绑定用户身份访问（免登录程序化接入）
        var user = auth.ValidateToken(token) ?? auth.ResolveApiKey(token);
        if (user is null)
            return (null, Results.Json(new AguiError(ErrorCodes.UserUnauthorized, "未登录或令牌无效"),
                statusCode: StatusCodes.Status401Unauthorized));
        return (user.UserId, null);
    }

    public static string? ResolveToken(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return header["Bearer ".Length..].Trim();
        var query = request.Query["token"].ToString();
        return string.IsNullOrEmpty(query) ? null : query;
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
            var (userId, error) = ResolveIdentity(http, auth);
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
            var (userId, error) = RequireRole(http, auth, PlatformRole.Admin);
            if (error is not null) return error;
            http.Items[IdentityKey] = userId;
            return await next(context);
        }
    }

    /// <summary>
    /// 需要指定最小平台角色的端点过滤器（RBAC 分层）：解析登录身份后校验生效角色 >= <paramref name="min"/>。
    /// 供按角色细分管理端点（如 <see cref="PlatformRole.Operator"/> 只读运维、<see cref="PlatformRole.SuperAdmin"/> 平台角色管理）。
    /// </summary>
    public sealed class RequireRoleFilter(PlatformRole min) : IEndpointFilter
    {
        private readonly PlatformRole _min = min;

        public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
        {
            var http = context.HttpContext;
            var auth = http.RequestServices.GetRequiredService<AuthService>();
            var (userId, error) = RequireRole(http, auth, _min);
            if (error is not null) return error;
            http.Items[IdentityKey] = userId;
            return await next(context);
        }
    }

    /// <summary>解析登录身份并校验生效平台角色 >= <paramref name="min"/>；不满足返回 403。</summary>
    private static (string? UserId, IResult? Error) RequireRole(HttpContext ctx, AuthService auth, PlatformRole min)
    {
        var (userId, error) = ResolveIdentity(ctx, auth);
        if (userId is null) return (null, error);
        if (!auth.HasRole(userId, min))
            return (null, Results.Json(new AguiError(ErrorCodes.GroupPermissionDenied,
                    $"权限不足：需要平台角色 {RoleName(min)} 或更高"),
                statusCode: StatusCodes.Status403Forbidden));
        return (userId, null);
    }

    /// <summary>平台角色名（camelCase 输出给 API：user / operator / admin / superadmin）。</summary>
    public static string RoleName(PlatformRole role) => PlatformRoleUtil.Name(role);
}
