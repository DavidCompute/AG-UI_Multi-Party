using AguiGroupChat.Agents;
using AguiGroupChat.Hub.Models;
using AguiGroupChat.Hub.Storage;
using Microsoft.AspNetCore.Http;

namespace AguiGroupChat.Web;

/// <summary>
/// 编排计划「暂停 / 继续」接口：
///   POST /ag-ui/plan/pause —— 暂停正在执行的协调计划（下次步骤边界生效，前端计划卡显示「已暂停」）
///   POST /ag-ui/plan/resume —— 让已暂停的计划继续执行剩余步骤
/// body 均 { groupId, messageId }。鉴权：登录且为该群成员，且仅<b>发起该计划的成员</b>或<b>群主/管理员</b>可操作。
/// 计划执行器（AgentGateway.ExecuteCoordinatedPlanAsync）在每步边界检查闸门；消息不存在于闸门（已结束）返回 404。
/// </summary>
public static class PlanControlApi
{
    public static void MapPlanControlApi(this WebApplication app)
    {
        var root = app.MapGroup("/ag-ui/plan");
        root.MapPost("/pause", (PlanControlHttpRequest req, HttpContext ctx, IGroupStore groupStore, CoordinatedPlanControlStore controls) =>
        {
            var (unauth, groupId, messageId) = ValidateAndAuthorize(req, ctx, groupStore, controls);
            if (unauth is not null) return unauth;
            var gate = controls.Find(messageId!);
            if (gate is null)
                return Results.Json(new { error = "该计划不存在或已结束" }, statusCode: StatusCodes.Status404NotFound);
            gate.Pause();
            return Results.Ok(new { paused = true, messageId });
        }).AddEndpointFilter(new WebIdentity.RequireIdentityFilter());

        root.MapPost("/resume", (PlanControlHttpRequest req, HttpContext ctx, IGroupStore groupStore, CoordinatedPlanControlStore controls) =>
        {
            var (unauth, groupId, messageId) = ValidateAndAuthorize(req, ctx, groupStore, controls);
            if (unauth is not null) return unauth;
            var gate = controls.Find(messageId!);
            if (gate is null)
                return Results.Json(new { error = "该计划不存在或已结束" }, statusCode: StatusCodes.Status404NotFound);
            gate.Resume();
            return Results.Ok(new { paused = false, messageId });
        }).AddEndpointFilter(new WebIdentity.RequireIdentityFilter());

        static (IResult? Unauthorized, string? GroupId, string? MessageId) ValidateAndAuthorize(
            PlanControlHttpRequest req, HttpContext ctx, IGroupStore groupStore, CoordinatedPlanControlStore controls)
        {
            var userId = WebIdentity.UserId(ctx);
            if (userId is null)
                return (Results.Json(new { error = "未登录" }, statusCode: StatusCodes.Status401Unauthorized), null, null);
            var groupId = (req.GroupId ?? "").Trim();
            var messageId = (req.MessageId ?? "").Trim();
            if (groupId.Length == 0 || messageId.Length == 0)
                return (Results.BadRequest(new { error = "参数不合法：需 groupId / messageId" }), null, null);
            if (!groupStore.GroupsOf(userId).Any(g => string.Equals(g.GroupId, groupId, StringComparison.Ordinal)))
                return (Results.Json(new { error = "仅群成员可控制该计划" }, statusCode: StatusCodes.Status403Forbidden), null, null);

            // 鉴权：触发者本人放行；群主 / 管理员（含客服团队角色）放行；普通成员即使能看到计划卡也不可暂停别人的计划
            var gate = controls.Find(messageId);
            if (gate is null)
                return (Results.Json(new { error = "该计划不存在或已结束" }, statusCode: StatusCodes.Status404NotFound), groupId, messageId);
            if (!string.Equals(gate.GroupId, groupId, StringComparison.Ordinal))
                return (Results.Json(new { error = "该计划不属于此知聚" }, statusCode: StatusCodes.Status403Forbidden), null, null);
            var member = groupStore.GetMember(groupId, userId);
            var isOwnerOrAdmin = member?.Role is GroupRole.Owner or GroupRole.Admin;
            if (!isOwnerOrAdmin && !string.Equals(gate.TriggerUserId, userId, StringComparison.Ordinal))
                return (Results.Json(new { error = "仅发起该计划的成员或群主/管理员可控制" }, statusCode: StatusCodes.Status403Forbidden), null, null);
            return (null, groupId, messageId);
        }
    }
}

public sealed record PlanControlHttpRequest(string? GroupId, string? MessageId);
