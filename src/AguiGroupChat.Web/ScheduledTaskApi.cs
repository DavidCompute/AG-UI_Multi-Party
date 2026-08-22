using AguiGroupChat.Agents;
using AguiGroupChat.Hub.Infra;
using AguiGroupChat.Hub.Messaging;
using AguiGroupChat.Hub.Models;
using AguiGroupChat.Hub.Options;
using AguiGroupChat.Hub.Persistence;
using AguiGroupChat.Hub.Users;

namespace AguiGroupChat.Web;

/// <summary>
/// 重复性定时任务管理 API（1.4，值班智能体）：
///   GET    /ag-ui/scheduled-tasks —— 可见任务列表（我所在群的智能体任务；管理员看全部）
///   POST   /ag-ui/scheduled-tasks —— 创建任务 {agentId, name, cron, prompt?, groupId?, enabled?}
///   PUT    /ag-ui/scheduled-tasks/{taskId} —— 更新（名称 / cron / 汇报指令 / 目标群 / 启用）
///   DELETE /ag-ui/scheduled-tasks/{taskId} —— 删除
/// 权限：任务面向「智能体加入的群」值守，创建 / 编辑须为相关群的成员（管理员任意）。
/// </summary>
public static class ScheduledTaskApi
{
    public static void MapScheduledTaskApi(this WebApplication app)
    {
        var root = app.MapGroup("/ag-ui/scheduled-tasks");

        // ---- 列表 ----
        root.MapGet("/", (HttpContext ctx, AuthService auth, AuthOptions authOptions,
            AguiGroupChat.Agents.ScheduledTaskService scheduled, GroupHub hub, AgentCatalog catalog) =>
        {
            var (userId, error) = WebIdentity.ResolveIdentity(ctx, auth, authOptions);
            if (error is not null) return error;
            if (userId is null) return error!;
            var isAdmin = auth.IsAdmin(userId);
            var myGroups = hub.Store.GroupsOf(userId).Select(g => g.GroupId).ToHashSet(StringComparer.Ordinal);
            var tasks = scheduled.List().Where(t =>
            {
                if (isAdmin) return true;
                // 非管理员：只能看到自己参与群内智能体的任务
                if (!string.IsNullOrWhiteSpace(t.GroupId)) return myGroups.Contains(t.GroupId);
                return hub.Store.GroupsOf(t.AgentId).Select(g => g.GroupId).Any(myGroups.Contains);
            }).Select(t => new
            {
                t.TaskId, t.AgentId, t.Name, t.Cron, t.Prompt, t.GroupId, t.Enabled, t.LastFiredAt,
                agentNickname = catalog.GetDefinition(t.AgentId)?.Nickname ?? t.AgentId,
            });
            return Results.Ok(tasks);
        });

        // ---- 创建 ----
        root.MapPost("/", (ScheduledTaskHttpRequest req, HttpContext ctx, AuthService auth, AuthOptions authOptions,
            AguiGroupChat.Agents.ScheduledTaskService scheduled, GroupHub hub, AgentCatalog catalog, ChangeHub changes) =>
        {
            var (userId, error) = WebIdentity.ResolveIdentity(ctx, auth, authOptions);
            if (error is not null) return error;
            if (userId is null) return error!;
            if (catalog.GetDefinition(req.AgentId) is null)
                return Results.BadRequest(new AguiError(ErrorCodes.AgentNotFound, "智能体不存在"));
            if (ScheduledTaskService.ValidateCron(req.Cron) is { } cronErr)
                return Results.BadRequest(new AguiError(ErrorCodes.BadRequest, cronErr));
            var groupErr = EnsureGroupAccess(req.GroupId, req.AgentId, hub, userId, auth);
            if (groupErr is not null) return groupErr;

            var task = new ScheduledTask
            {
                TaskId = "sched_" + IdGenerator.NewId(),
                AgentId = req.AgentId.Trim(),
                Name = string.IsNullOrWhiteSpace(req.Name) ? req.AgentId.Trim() : req.Name.Trim(),
                Cron = req.Cron.Trim(),
                Prompt = string.IsNullOrWhiteSpace(req.Prompt) ? null : req.Prompt.Trim(),
                GroupId = string.IsNullOrWhiteSpace(req.GroupId) ? null : req.GroupId.Trim(),
                Enabled = req.Enabled ?? true,
            };
            scheduled.Upsert(task);
            changes.Notify();
            return Results.Ok(new { ok = true, taskId = task.TaskId });
        });

        // ---- 更新 ----
        root.MapPut("/{taskId}", (string taskId, ScheduledTaskHttpRequest req, HttpContext ctx, AuthService auth, AuthOptions authOptions,
            AguiGroupChat.Agents.ScheduledTaskService scheduled, GroupHub hub, AgentCatalog catalog, ChangeHub changes) =>
        {
            var (userId, error) = WebIdentity.ResolveIdentity(ctx, auth, authOptions);
            if (error is not null) return error;
            if (userId is null) return error!;
            var task = scheduled.Get(taskId);
            if (task is null) return Results.NotFound(new AguiError(ErrorCodes.BadRequest, "任务不存在"));
            if (catalog.GetDefinition(task.AgentId) is null)
                return Results.BadRequest(new AguiError(ErrorCodes.AgentNotFound, "智能体不存在"));
            if (ScheduledTaskService.ValidateCron(req.Cron) is { } cronErr)
                return Results.BadRequest(new AguiError(ErrorCodes.BadRequest, cronErr));
            var groupErr = EnsureGroupAccess(req.GroupId, task.AgentId, hub, userId, auth);
            if (groupErr is not null) return groupErr;

            task.Name = string.IsNullOrWhiteSpace(req.Name) ? task.Name : req.Name.Trim();
            task.Cron = req.Cron.Trim();
            task.Prompt = string.IsNullOrWhiteSpace(req.Prompt) ? null : req.Prompt.Trim();
            task.GroupId = string.IsNullOrWhiteSpace(req.GroupId) ? null : req.GroupId.Trim();
            if (req.Enabled is { } en) task.Enabled = en;
            changes.Notify();
            return Results.Ok(new { ok = true });
        });

        // ---- 删除 ----
        root.MapDelete("/{taskId}", (string taskId, HttpContext ctx, AuthService auth, AuthOptions authOptions,
            AguiGroupChat.Agents.ScheduledTaskService scheduled, ChangeHub changes) =>
        {
            var (userId, error) = WebIdentity.ResolveIdentity(ctx, auth, authOptions);
            if (error is not null) return error;
            if (userId is null) return error!;
            if (scheduled.Remove(taskId))
            {
                changes.Notify();
                return Results.Ok(new { ok = true });
            }
            return Results.NotFound(new AguiError(ErrorCodes.BadRequest, "任务不存在"));
        });
    }

    /// <summary>目标群访问校验：群存在、调用者是成员、且智能体在该群内；未指定群时调用者须与该智能体在至少一个共同群。</summary>
    private static IResult? EnsureGroupAccess(string? groupId, string agentId, GroupHub hub, string userId, AuthService auth)
    {
        if (auth.IsAdmin(userId)) return null; // 管理员任意
        if (!string.IsNullOrWhiteSpace(groupId))
        {
            if (hub.Store.GetGroup(groupId) is null)
                return Results.NotFound(new AguiError(ErrorCodes.GroupNotFound, "群组不存在"));
            if (!hub.Store.IsMember(groupId, userId))
                return Results.Json(new AguiError(ErrorCodes.GroupPermissionDenied, "仅群成员可创建该群内智能体的定时任务"),
                    statusCode: StatusCodes.Status403Forbidden);
            if (!hub.Store.IsMember(groupId, agentId))
                return Results.BadRequest(new AguiError(ErrorCodes.BadRequest, "该智能体不在指定群内"));
            return null;
        }
        // 无指定群：须与该智能体在至少一个共同群
        var agentGroups = hub.Store.GroupsOf(agentId).Select(g => g.GroupId).ToHashSet(StringComparer.Ordinal);
        var sharesGroup = hub.Store.GroupsOf(userId).Any(g => agentGroups.Contains(g.GroupId));
        return sharesGroup ? null : Results.Json(new AguiError(ErrorCodes.GroupPermissionDenied, "你与该智能体没有共同群，无法为其配置定时任务"),
            statusCode: StatusCodes.Status403Forbidden);
    }
}

/// <summary>重复性定时任务创建 / 更新请求体。</summary>
public sealed record ScheduledTaskHttpRequest(string AgentId, string Name, string Cron, string? Prompt, string? GroupId, bool? Enabled);
