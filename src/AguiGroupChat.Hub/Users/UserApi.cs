using AguiGroupChat.Hub.Infra;
using AguiGroupChat.Hub.Messaging;
using AguiGroupChat.Hub.Models;

namespace AguiGroupChat.Hub.Users;

/// <summary>
/// 用户管理 HTTP API（Hub 扩展）：注册 / 登录 / 登出 / 当前用户 / 修改密码 / 资料维护 / 用户目录。
/// 除注册与登录外均需携带会话令牌（<c>Authorization: Bearer &lt;token&gt;</c>）。
/// </summary>
public static class UserApi
{
    // 注册频率限制（防脚本灌号 / 存储 DoS）：按客户端 IP 统计，1 分钟窗口内最多 5 次
    private const int RegisterWindowMs = 60 * 1000;
    private const int RegisterMaxPerWindow = 5;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (int Count, long FirstMs)> RegisterAttempts = new(StringComparer.Ordinal);

    public static void MapUserApi(this WebApplication app)
    {
        var root = app.MapGroup("/ag-ui/user");

        // ---- 注册 / 登录 / 登出 ----
        root.MapPost("/register", (RegisterHttpRequest req, HttpContext ctx, AuthService auth) =>
            RunAsync(() =>
            {
                // IP 维度限流：防脚本批量注册撑爆用户表（测试环境旁路：并行测试共享 127.0.0.1 会误伤）
                if (!app.Environment.IsEnvironment("Testing"))
                {
                    var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                    var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    var entry = RegisterAttempts.AddOrUpdate(ip,
                        _ => (1, now),
                        (_, old) => now - old.FirstMs < RegisterWindowMs ? (old.Count + 1, old.FirstMs) : (1, now));
                    if (entry.Count > RegisterMaxPerWindow)
                        throw new AguiProtocolException(ErrorCodes.BadRequest, "注册过于频繁，请稍后再试");
                    if (RegisterAttempts.Count > 10000)
                    {
                        foreach (var kv in RegisterAttempts.Where(kv => now - kv.Value.FirstMs >= RegisterWindowMs).ToList())
                            RegisterAttempts.TryRemove(kv.Key, out _);
                    }
                }

                var user = auth.Register(req.Username, req.Password, req.Nickname, req.Avatar);
                // 注册即登录：直接签发会话令牌，无需二次登录（失败锁定键同样按 IP + 用户名）
                return Task.FromResult(Results.Ok(ToAuthResponse(
                    auth.Login(user.Username, req.Password, ctx.Connection.RemoteIpAddress?.ToString()))));
            }));

        root.MapPost("/login", (LoginHttpRequest req, HttpContext ctx, AuthService auth, TotpService totp) =>
            RunAsync(() =>
            {
                var result = auth.Login(req.Username, req.Password, ctx.Connection.RemoteIpAddress?.ToString());
                // 登录二次验证（TOTP，4.4）：账号启用 TOTP 时要求 6 位动态码；缺失 / 错误 → 拒绝登录（密码正确但 2FA 未过）。
                // 先查分用户锁定：窗口内码错超限即拒绝（不消耗新签发会话），防对 6 位动态码的无休止暴力枚举。
                if (totp.IsEnabled(result.User.UserId))
                {
                    if (totp.IsLockedOut(result.User.UserId))
                    {
                        auth.Logout(result.Token);
                        throw new AguiProtocolException(ErrorCodes.UserBadCredentials, "动态验证码尝试次数过多，请稍后再试");
                    }
                    if (!totp.Verify(result.User.UserId, req.TotpCode ?? ""))
                    {
                        auth.Logout(result.Token); // 吊销刚签发的会话，避免未过 2FA 却持有有效令牌
                        throw new AguiProtocolException(ErrorCodes.UserBadCredentials, "需要动态验证码（TOTP）或验证码错误：请在登录请求携带 totpCode");
                    }
                }
                return Task.FromResult(Results.Ok(ToAuthResponse(result)));
            }));

        root.MapPost("/logout", (HttpContext ctx, AuthService auth) =>
        {
            auth.Logout(ResolveToken(ctx));
            return Results.Ok(new { ok = true });
        });

        // ---- 当前用户 / 密码 / 资料（需令牌）----
        root.MapGet("/me", (HttpContext ctx, AuthService auth) =>
        {
            var user = RequireUser(ctx, auth);
            return user is null ? Unauthorized() : Results.Ok(ToProfile(user));
        });

        root.MapPost("/password", (HttpContext ctx, ChangePasswordHttpRequest req, AuthService auth) =>
            RunAsync(() =>
            {
                var user = RequireUser(ctx, auth);
                if (user is null) return Task.FromResult(Unauthorized());
                auth.ChangePassword(user.UserId, req.OldPassword, req.NewPassword);
                return Task.FromResult(Results.Ok(new { ok = true }));
            }));

        root.MapPut("/profile", (HttpContext ctx, UpdateProfileHttpRequest req, AuthService auth, GroupHub hub) =>
            RunAsync(async () =>
            {
                var user = RequireUser(ctx, auth);
                if (user is null) return Unauthorized();
                var updated = auth.UpdateProfile(user.UserId, req.Nickname, req.Avatar, req.PersonalMemoryEnabled, req.PreferredBridgeClient);
                // 昵称 / 头像变更同步到其所有群成员（显示名 / 头像），并广播 GROUP_MEMBER_UPDATED
                await hub.SyncUserDisplayNameAsync(user.UserId);
                await hub.SyncUserAvatarAsync(user.UserId);
                return Results.Ok(ToProfile(updated));
            }));

        // ---- 登录二次验证（TOTP，4.4）：状态 / 签发密钥 / 确认启用 / 停用 ----
        root.MapGet("/totp", (HttpContext ctx, AuthService auth, TotpService totp) =>
        {
            var user = RequireUser(ctx, auth);
            if (user is null) return Unauthorized();
            return Results.Ok(new { enabled = totp.IsEnabled(user.UserId) });
        });

        root.MapPost("/totp/enroll", (HttpContext ctx, AuthService auth, TotpService totp) =>
        {
            var user = RequireUser(ctx, auth);
            if (user is null) return Unauthorized();
            var secret = totp.Enroll(user.UserId);
            return Results.Ok(new { secret, otpauth = BuildOtpauth(user.Username, secret), enabled = false });
        });

        root.MapPost("/totp/confirm", (TotpCodeHttpRequest req, HttpContext ctx, AuthService auth, TotpService totp) =>
            RunAsync(() =>
            {
                var user = RequireUser(ctx, auth);
                if (user is null) return Task.FromResult(Unauthorized());
                if (!totp.Confirm(user.UserId, req.Code ?? ""))
                    throw new AguiProtocolException(ErrorCodes.BadRequest, "TOTP 验证码错误或未先启用（请先 enroll），启用失败");
                return Task.FromResult(Results.Ok(new { ok = true, enabled = true }));
            }));

        root.MapPost("/totp/disable", (TotpCodeHttpRequest req, HttpContext ctx, AuthService auth, TotpService totp) =>
            RunAsync(() =>
            {
                var user = RequireUser(ctx, auth);
                if (user is null) return Task.FromResult(Unauthorized());
                if (!totp.Disable(user.UserId, req.Code ?? ""))
                    throw new AguiProtocolException(ErrorCodes.BadRequest, "TOTP 验证码错误或未启用，停用失败");
                return Task.FromResult(Results.Ok(new { ok = true, enabled = false }));
            }));

        // ---- 多设备会话管理（4.4）：列出 / 吊销指定会话 / 吊销其他全部会话 ----
        root.MapGet("/sessions", (HttpContext ctx, AuthService auth) =>
        {
            var user = RequireUser(ctx, auth);
            if (user is null) return Unauthorized();
            return Results.Ok(new
            {
                current = auth.GetSessionIdOfToken(ResolveToken(ctx)),
                sessions = auth.GetUserSessions(user.UserId),
            });
        });

        root.MapPost("/sessions/revoke", (HttpContext ctx, RevokeSessionHttpRequest req, AuthService auth) =>
            RunAsync(() =>
            {
                var user = RequireUser(ctx, auth);
                if (user is null) return Task.FromResult(Unauthorized());
                if (string.IsNullOrWhiteSpace(req.SessionId))
                    throw new AguiProtocolException(ErrorCodes.BadRequest, "缺少 sessionId");
                if (req.SessionId == auth.GetSessionIdOfToken(ResolveToken(ctx)))
                    throw new AguiProtocolException(ErrorCodes.BadRequest, "不能吊销当前登录会话，请使用「登出」");
                auth.RevokeSession(user.UserId, req.SessionId);
                return Task.FromResult(Results.Ok(new { ok = true, revoked = req.SessionId }));
            }));

        root.MapPost("/sessions/revoke-others", (HttpContext ctx, AuthService auth) =>
            RunAsync(() =>
            {
                var user = RequireUser(ctx, auth);
                if (user is null) return Task.FromResult(Unauthorized());
                var current = auth.GetSessionIdOfToken(ResolveToken(ctx)) ?? "";
                var count = auth.RevokeOtherSessions(user.UserId, current);
                return Task.FromResult(Results.Ok(new { ok = true, revoked = count }));
            }));

        // ---- 用户目录（前端建群成员选择器）：需登录可见（防未授权枚举用户 / 泄露管理员标记）----
        app.MapGet("/ag-ui/users", (HttpContext ctx, AuthService auth) =>
        {
            var user = RequireUser(ctx, auth);
            if (user is null) return Unauthorized();
            // 目录 DTO 不含 isAdmin：管理员标记仅 /me 与登录响应返回（前端 loadUserDirectory 不读取 isAdmin）
            return Results.Ok(auth.ListUsers().Select(ToDirectoryProfile));
        });
    }

    private static UserAccount? RequireUser(HttpContext ctx, AuthService auth)
        => auth.ValidateToken(ResolveToken(ctx));

    /// <summary>令牌来源：Authorization: Bearer 头 → ?token= 查询参数。</summary>
    private static string? ResolveToken(HttpContext ctx)
    {
        var header = ctx.Request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return header["Bearer ".Length..].Trim();
        var query = ctx.Request.Query["token"].ToString();
        return string.IsNullOrEmpty(query) ? null : query;
    }

    /// <summary>用户目录 DTO：不含 isAdmin（管理员标记不向目录接口暴露，防泄露管理员身份）。</summary>
    private static object ToDirectoryProfile(UserAccount user) => new
    {
        userId = user.UserId,
        username = user.Username,
        nickname = user.Nickname,
        avatar = user.Avatar,
        personalMemoryEnabled = user.PersonalMemoryEnabled,
        createdAt = user.CreatedAt,
    };

    private static object ToProfile(UserAccount user) => new
    {
        userId = user.UserId,
        username = user.Username,
        nickname = user.Nickname,
        avatar = user.Avatar,
        personalMemoryEnabled = user.PersonalMemoryEnabled,
        preferredBridgeClient = user.PreferredBridgeClient,
        isAdmin = user.IsAdmin,
        createdAt = user.CreatedAt,
    };

    private static object ToAuthResponse(LoginResult login) => new
    {
        userId = login.User.UserId,
        username = login.User.Username,
        nickname = login.User.Nickname,
        avatar = login.User.Avatar,
        personalMemoryEnabled = login.User.PersonalMemoryEnabled,
        preferredBridgeClient = login.User.PreferredBridgeClient,
        isAdmin = login.User.IsAdmin,
        token = login.Token,
        expiresAt = login.ExpiresAt,
    };

    private static async Task<IResult> RunAsync(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (AguiProtocolException ex) { return ToResult(ex); }
    }

    private static IResult ToResult(AguiProtocolException ex) => ex.ErrorCode switch
    {
        ErrorCodes.UserNotFound
            => Results.NotFound(new AguiError(ex.ErrorCode, ex.Message)),
        ErrorCodes.UserExists
            => Results.Json(new AguiError(ex.ErrorCode, ex.Message), statusCode: StatusCodes.Status409Conflict),
        ErrorCodes.UserBadCredentials or ErrorCodes.UserUnauthorized
            => Results.Json(new AguiError(ex.ErrorCode, ex.Message), statusCode: StatusCodes.Status401Unauthorized),
        _ => Results.BadRequest(new AguiError(ex.ErrorCode, ex.Message)),
    };

    private static IResult Unauthorized(string message = "未登录或令牌无效")
        => Results.Json(new AguiError(ErrorCodes.UserUnauthorized, message), statusCode: StatusCodes.Status401Unauthorized);

    /// <summary>构造 otpauth 二维码 URI（供 Authenticator 录入）。</summary>
    private static string BuildOtpauth(string username, string secret)
        => "otpauth://totp/" + Uri.EscapeDataString("AGUI") + ":" + Uri.EscapeDataString(username)
           + "?secret=" + secret + "&issuer=AGUI&algorithm=SHA1&digits=6&period=30";
}

// ================= 请求体 =================

public sealed record RegisterHttpRequest(string Username, string Password, string? Nickname, string? Avatar);

public sealed record LoginHttpRequest(string Username, string Password, string? TotpCode = null);

public sealed record ChangePasswordHttpRequest(string OldPassword, string NewPassword);

public sealed record UpdateProfileHttpRequest(string? Nickname, string? Avatar, bool? PersonalMemoryEnabled = null, string? PreferredBridgeClient = null);

/// <summary>吊销指定会话请求体（多设备会话管理，4.4）。</summary>
public sealed record RevokeSessionHttpRequest(string? SessionId);

/// <summary>TOTP 验证码请求体（confirm / disable）。</summary>
public sealed record TotpCodeHttpRequest(string? Code);
