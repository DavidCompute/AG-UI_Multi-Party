using AguiGroupChat.Hub.Agents;
using AguiGroupChat.Hub.Infra;
using AguiGroupChat.Hub.Messaging;
using AguiGroupChat.Hub.Models;
using AguiGroupChat.Hub.Options;
using AguiGroupChat.Hub.Storage;
using AguiGroupChat.Hub.Users;

namespace AguiGroupChat.Hub.Transport;

/// <summary>
/// 任务编排 HTTP API（工作型智能体）：
///   POST /ag-ui/tasks —— 创建任务（触发工作型智能体运行，后台执行）
///   GET  /ag-ui/tasks —— 我的任务列表（最近 N 条）
///   GET  /ag-ui/tasks/{groupId}/group —— 某群的任务列表
///   GET  /ag-ui/tasks/{taskId} —— 任务详情（状态 / 进度 / 结果）
/// </summary>
public static class TaskApi
{
    public static void MapTaskApi(this WebApplication app)
    {
        var root = app.MapGroup("/ag-ui/tasks");

        // 创建任务：校验群成员 + 智能体在群内且为工作型，创建任务并后台触发
        root.MapPost("/", async (TaskCreateRequest req, HttpContext ctx, AuthService auth, AuthOptions authOptions,
            GroupHub hub, TaskService tasks, IAgentGateway gateway, CancellationToken ct) =>
        {
            var (identity, error) = RequireIdentity(ctx, auth, authOptions);
            if (identity is null) return error;
            if (hub.Store.GetGroup(req.GroupId) is null)
                return Results.NotFound(new AguiError(ErrorCodes.GroupNotFound, "群组不存在"));
            if (!hub.Store.IsMember(req.GroupId, identity))
                return Results.Json(new AguiError(ErrorCodes.GroupPermissionDenied, "仅群成员可创建任务"),
                    statusCode: StatusCodes.Status403Forbidden);
            var member = hub.Store.GetMember(req.GroupId, req.AgentId);
            if (member is null || member.MemberType != MemberType.Agent)
                return Results.BadRequest(new AguiError(ErrorCodes.BadRequest, "指定智能体不在该群"));
            if (string.IsNullOrWhiteSpace(req.Content))
                return Results.BadRequest(new AguiError(ErrorCodes.BadRequest, "任务指令不能为空"));

            var topicId = string.IsNullOrWhiteSpace(req.TopicId) ? "main" : req.TopicId;
            var taskId = tasks.CreateTask(req.GroupId, req.AgentId, identity, topicId, req.Title, req.Content);
            var task = tasks.Get(taskId)!;
            var memberNick = hub.Store.GetMember(req.GroupId, req.AgentId)?.Nickname ?? req.AgentId;

            // 后台触发智能体：网关内把任务状态从 Queue → Running → Finished/Failed
            _ = Task.Run(async () =>
            {
                try
                {
                    tasks.MarkRunning(taskId);
                    var content = $"【任务】{req.Title}\n{(req.Content ?? "")}\n（任务ID：{taskId}。请完成任务并在结尾简要汇报结果。）";
                    var result = await gateway.InvokeAsync(new AgentInvocationContext(
                        GroupId: req.GroupId,
                        ThreadId: "thread_" + req.GroupId,
                        AgentId: req.AgentId,
                        AgentNickname: memberNick,
                        TriggerMessageId: "",
                        TriggerUserId: identity,
                        Content: content,
                        Mentions: [],
                        MentionAll: false,
                        TopicId: topicId,
                        TaskId: taskId), CancellationToken.None);
                    // 网关已回写成功/失败状态；此处兜底（网关未关联 taskId 时标记完成）
                    var cur = tasks.Get(taskId);
                    if (cur is not null && cur.Status == WorkTaskStatus.Running)
                    {
                        if (result.Accepted)
                        {
                            tasks.MarkFinished(taskId, "任务已完成");
                        }
                        else if (result.ErrorCode == "AGENT_AWAITING_INTERACTION")
                        {
                            // 写操作等需人工批准：审批卡片已推送到群里，保持任务“运行中（等待批准）”，
                            // 不判定失败，用户批准后智能体会继续执行并把结果发回群里。
                            tasks.UpdateProgressNote(taskId, "等待操作批准：请在群里查看推送给你的确认卡片。");
                        }
                        else
                        {
                            tasks.MarkFinished(taskId, $"未完成（{result.ErrorCode}）", result.ErrorCode);
                        }
                    }
                }
                catch (Exception ex)
                {
                    try { tasks.MarkFinished(taskId, null, ex.Message); } catch { }
                }
            });
            return Results.Ok(task);
        });

        // 我的任务列表（仅本人）
        root.MapGet("/", async (int? count, HttpContext ctx, AuthService auth, AuthOptions authOptions, TaskService tasks) =>
        {
            var (identity, error) = RequireIdentity(ctx, auth, authOptions);
            if (identity is null) return error;
            return Results.Ok(tasks.ListForUser(identity, Math.Clamp(count ?? 20, 1, 100)));
        });

        // 某群任务列表（仅成员）
        root.MapGet("/{groupId}/group", async (string groupId, int? count, HttpContext ctx, AuthService auth, AuthOptions authOptions, TaskService tasks, GroupHub hub) =>
        {
            var (identity, error) = RequireIdentity(ctx, auth, authOptions);
            if (identity is null) return error;
            if (!hub.Store.IsMember(groupId, identity))
                return Results.Json(new AguiError(ErrorCodes.GroupPermissionDenied, "仅群成员可查看任务"),
                    statusCode: StatusCodes.Status403Forbidden);
            return Results.Ok(tasks.ListForGroup(groupId, Math.Clamp(count ?? 20, 1, 100)));
        });

        // 任务详情（发起者或同群成员）
        root.MapGet("/{taskId}", async (string taskId, HttpContext ctx, AuthService auth, AuthOptions authOptions, TaskService tasks, GroupHub hub) =>
        {
            var (identity, error) = RequireIdentity(ctx, auth, authOptions);
            if (identity is null) return error;
            var task = tasks.Get(taskId);
            if (task is null) return Results.NotFound(new AguiError(ErrorCodes.GroupNotFound, "任务不存在"));
            if (!hub.Store.IsMember(task.GroupId, identity))
                return Results.Json(new AguiError(ErrorCodes.GroupPermissionDenied, "仅群成员可查看该任务"),
                    statusCode: StatusCodes.Status403Forbidden);
            return Results.Ok(task);
        });
    }

    private static (string? UserId, IResult? Error) RequireIdentity(HttpContext ctx, AuthService auth, AuthOptions authOptions)
    {
        var token = ResolveToken(ctx.Request);
        if (string.IsNullOrEmpty(token))
            return (null, Results.Json(new AguiError(ErrorCodes.UserUnauthorized, "未登录"),
                statusCode: StatusCodes.Status401Unauthorized));
        var user = auth.ValidateToken(token);
        if (user is null)
            return (null, Results.Json(new AguiError(ErrorCodes.UserUnauthorized, "未登录或令牌无效"),
                statusCode: StatusCodes.Status401Unauthorized));
        return (user.UserId, null);
    }

    /// <summary>令牌来源：Authorization: Bearer 头 → ?token= 查询参数。</summary>
    private static string? ResolveToken(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        if (!string.IsNullOrEmpty(header) && header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return header["Bearer ".Length..].Trim();
        return request.Query["token"].ToString();
    }
}

/// <summary>创建任务的请求体。</summary>
public sealed class TaskCreateRequest
{
    public required string GroupId { get; set; }
    public required string AgentId { get; set; }
    public required string Content { get; set; }
    public string? Title { get; set; }
    public string? TopicId { get; set; }
}
