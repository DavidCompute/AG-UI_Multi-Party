using AguiGroupChat.Agents;
using AguiGroupChat.Hub.Models;
using AguiGroupChat.Hub.Users;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace AguiGroupChat.Web;

/// <summary>
/// 用户 AI 分身 HTTP API（Web 组合根扩展）：启用 / 停用 / 查询。
/// 分身 = 基于用户公开群发言自动生成的私密智能体（agentId = twin_{userId}），
/// 加入用户所在的所有公开群，按用户设定的触发方式回复。
/// </summary>
public static class TwinApi
{
    public static void MapTwinApi(this WebApplication app)
    {
        var root = app.MapGroup("/ag-ui/twin");

        // ---- 查询分身状态 ----
        root.MapGet("/", (HttpContext ctx, AuthService auth, TwinService twins) =>
        {
            var user = WebIdentity.User(ctx, auth);
            if (user is null) return Unauthorized();
            var status = twins.GetStatus(user.UserId);
            return status is null
                ? Results.Ok(new { enabled = false })
                : Results.Ok(status);
        }).AddEndpointFilter(new WebIdentity.RequireTokenFilter());

        // ---- 启用分身（需登录）：生成人设并加入用户所在全部公开群 ----
        root.MapPost("/enable", async (TwinEnableHttpRequest req, HttpContext ctx, AuthService auth, TwinService twins, CancellationToken ct) =>
        {
            var user = WebIdentity.User(ctx, auth);
            if (user is null) return Unauthorized();
            var mode = Enum.TryParse<AgentTriggerMode>(req.TriggerMode, true, out var m)
                ? m
                : AgentTriggerMode.Mentioned;
            try
            {
                var status = await twins.EnableAsync(user.UserId, mode, ct);
                return Results.Ok(status);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new AguiError(ErrorCodes.BadRequest, $"分身启用失败：{ex.Message}"));
            }
        }).AddEndpointFilter(new WebIdentity.RequireTokenFilter());

        // ---- 修改触发方式（需登录）：更新分身定义并同步全部公开群的触发规则 ----
        root.MapPost("/trigger", async (TwinEnableHttpRequest req, HttpContext ctx, AuthService auth, TwinService twins, CancellationToken ct) =>
        {
            var user = WebIdentity.User(ctx, auth);
            if (user is null) return Unauthorized();
            var mode = Enum.TryParse<AgentTriggerMode>(req.TriggerMode, true, out var m)
                ? m
                : AgentTriggerMode.Mentioned;
            var status = await twins.UpdateTriggerAsync(user.UserId, mode, ct);
            return status is null
                ? Results.BadRequest(new AguiError(ErrorCodes.AgentNotFound, "分身未启用"))
                : Results.Ok(status);
        }).AddEndpointFilter(new WebIdentity.RequireTokenFilter());

        // ---- 同步分身到全部公开群（需登录）：补齐启用后新建 / 加入的公开群，不重建人设 ----
        root.MapPost("/sync", async (HttpContext ctx, AuthService auth, TwinService twins, CancellationToken ct) =>
        {
            var user = WebIdentity.User(ctx, auth);
            if (user is null) return Unauthorized();
            var status = await twins.SyncGroupsAsync(user.UserId, ct);
            return status is null
                ? Results.BadRequest(new AguiError(ErrorCodes.AgentNotFound, "分身未启用"))
                : Results.Ok(status);
        }).AddEndpointFilter(new WebIdentity.RequireTokenFilter());

        // ---- 停用分身（需登录）：删除分身并退出全部群 ----
        root.MapPost("/disable", async (HttpContext ctx, AuthService auth, TwinService twins, CancellationToken ct) =>
        {
            var user = WebIdentity.User(ctx, auth);
            if (user is null) return Unauthorized();
            var removed = await twins.DisableAsync(user.UserId, ct);
            return Results.Ok(new { disabled = removed });
        }).AddEndpointFilter(new WebIdentity.RequireTokenFilter());
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

    private static IResult Unauthorized(string message = "未登录或令牌无效")
        => Results.Json(new AguiError(ErrorCodes.UserUnauthorized, message), statusCode: StatusCodes.Status401Unauthorized);
}

/// <summary>启用分身请求（triggerMode 为字符串，便于前端直接传递）。</summary>
public sealed record TwinEnableHttpRequest(string TriggerMode = "mentioned");
