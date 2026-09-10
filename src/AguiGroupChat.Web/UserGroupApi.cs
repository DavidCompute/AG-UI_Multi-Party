using AguiGroupChat.Agents.UserGroups;
using AguiGroupChat.Hub.Infra;
using AguiGroupChat.Hub.Models;
using AguiGroupChat.Hub.Users;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace AguiGroupChat.Web;

/// <summary>用户分组新增 / 更新请求。</summary>
/// <param name="GroupId">留空自动生成 ug_xxx；填已有 id = 覆盖更新。</param>
public sealed record CreateGroupReq(string? GroupId, string Name, string? Description = null, IReadOnlyList<string>? MemberUserIds = null);

/// <summary>
/// 用户分组 / 组织单元管理 —— 细粒度授权的前端面。
/// 分组仅平台管理员（≥ <see cref="PlatformRole.Admin"/>）维护；把某资源（数字员工）授权给哪些分组，
/// 由资源自身携带白名单（AgentDefinition.AllowedGroupIds）决定，真正拒绝在服务端访问点经 UserGroupStore 判定。
/// 导出到前端分三种形态：管理员拿全量（含成员）；本人只拿“我所属分组 id”（/mine）。
/// </summary>
public static class UserGroupApi
{
    public static void MapUserGroupApi(this WebApplication app)
    {
        var root = app.MapGroup("/ag-ui/usergroups");

        // 本人所属分组（任何登录用户；不含他组成员明细）
        root.MapGet("/mine", (HttpContext ctx, AuthService auth, UserGroupStore groups) =>
        {
            var user = WebIdentity.User(ctx, auth);
            if (user is null) return Results.Unauthorized();
            var mine = groups.List()
                .Where(g => g.MemberUserIds?.Contains(user.UserId, StringComparer.Ordinal) ?? false)
                .Select(g => (gid: g.GroupId, name: g.Name))
                .ToList();
            return Results.Ok(new { mine });
        }).AddEndpointFilter(new WebIdentity.RequireTokenFilter());

        // 全量列表（仅管理员；附带成员 userId）
        root.MapGet("/", (HttpContext ctx, AuthService auth, UserGroupStore groups) =>
        {
            var guard = AdminOnly(ctx, auth);
            if (guard is not null) return guard;
            return Results.Ok(groups.List().Select(g => ToFullDto(g)).ToList());
        }).AddEndpointFilter(new WebIdentity.RequireTokenFilter());

        // 新增 / 更新（幂等按 groupId）：创建传空 groupId 自动生成 ug_xxx
        root.MapPost("/", (CreateGroupReq? req, HttpContext ctx, AuthService auth, UserGroupStore groups, ILoggerFactory lf) =>
        {
            var guard = AdminOnly(ctx, auth);
            if (guard is not null) return guard;
            if (req is null) return Results.BadRequest(new AguiError(ErrorCodes.BadRequest, "请求体缺失"));

            var name = (req.Name ?? "").Trim();
            if (name.Length == 0) return Results.BadRequest(new AguiError(ErrorCodes.BadRequest, "分组名称不能为空"));
            if (name.Length > 80) return Results.BadRequest(new AguiError(ErrorCodes.BadRequest, "分组名称过长，最多 80 字"));

            var wantId = (req.GroupId ?? "").Trim();
            if (wantId.Length > 0 && !wantId.StartsWith("ug_", StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new AguiError(ErrorCodes.BadRequest, "分组 ID 需以 ug_ 开头"));
            var id = wantId.Length > 0 ? wantId : "ug_" + IdGenerator.NewId();
            var existing = groups.Get(id);

            groups.Upsert(new UserGroup
            {
                GroupId = id,
                Name = name,
                Description = (req.Description ?? "").Trim(),
                MemberUserIds = (req.MemberUserIds ?? [])
                    .Where(m => !string.IsNullOrWhiteSpace(m))
                    .Select(m => m.Trim())
                    .Distinct(StringComparer.Ordinal)
                    .ToList(),
                CreatedAtMs = existing?.CreatedAtMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            });
            lf.CreateLogger("UserGroupApi").LogInformation("用户分组写入/更新：{GroupId} name={Name}", id, name);
            return Results.Ok(new { id, name });
        }).AddEndpointFilter(new WebIdentity.RequireTokenFilter());

        // 删除分组（不级联删除资源上的白名单项；资源会让出该组授权为无效引用由访问判定天然忽略）
        root.MapDelete("/{groupId}", (string groupId, HttpContext ctx, AuthService auth, UserGroupStore groups) =>
        {
            var guard = AdminOnly(ctx, auth);
            if (guard is not null) return guard;
            var removed = groups.Remove(groupId.Trim());
            if (!removed) return Results.NotFound(new AguiError(ErrorCodes.AgentNotFound, "分组不存在"));
            return Results.Ok(new { deleted = true, groupId });
        }).AddEndpointFilter(new WebIdentity.RequireTokenFilter());
    }

    private static IResult? AdminOnly(HttpContext ctx, AuthService auth)
    {
        var user = WebIdentity.User(ctx, auth);
        if (user is null) return Results.Unauthorized();
        return auth.ResolveRole(user.UserId) < PlatformRole.Admin
            ? Results.Json(new AguiError(ErrorCodes.AgentPermissionDenied, "仅系统管理员可管理用户分组"),
                statusCode: StatusCodes.Status403Forbidden)
            : null;
    }

    private static object ToFullDto(UserGroup g) => new
    {
        g.GroupId,
        g.Name,
        g.Description,
        MemberUserIds = (g.MemberUserIds ?? []).ToList(),
    };
}
