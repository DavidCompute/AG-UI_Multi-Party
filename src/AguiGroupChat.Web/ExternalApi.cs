using AguiGroupChat.Hub;
using AguiGroupChat.Hub.Infra;
using AguiGroupChat.Hub.Messaging;
using AguiGroupChat.Hub.Models;
using AguiGroupChat.Hub.Storage;
using AguiGroupChat.Hub.Users;

namespace AguiGroupChat.Web;

/// <summary>
/// 版本化对外 REST API（<c>/agui/v1</c>）——入站集成面：外部系统用 <b>API 密钥</b>（或登录会话）
/// 主动向知聚发消息、读历史、并按需<b>同步等回</b>数字员工的回复。
///
/// 与既有的 <c>/ag-ui/group/*</c>（协议线格式，Hub 内部面）不同，这一层是**稳定的对外契约**：
///   - 路径前缀带版本 `<c>/agui/v1</c>`，请求 / 响应字段显式定义（不直接暴露内部 DTO）；
///   - 鉴权只接受真实身份（<c>Authorization: Bearer &lt;apiKey|token&gt;</c>），并以该身份做成员 / 可见性校验；
///   - 发送后可 <c>waitMs</c> 长轮询，等该次触发的数字员工回复**完整**（正文不再增长、流已结束）再返回，
///     便于脚本 / 工作流一步拿到结果（不等则为异步：发完即返回，用历史接口或 Webhook 取结果）。
///
/// 端点：
///   GET  /agui/v1/me
///   GET  /agui/v1/groups
///   POST /agui/v1/groups/{groupId}/messages
///   GET  /agui/v1/groups/{groupId}/messages
///   GET  /agui/v1/groups/{groupId}/replies
///   GET  /agui/v1/groups/{groupId}/messages/{messageId}
/// </summary>
public static class ExternalApi
{
    /// <summary><c>waitMs</c> 上限：长轮询单请求最多阻塞 45 秒（防止外部客户端挂死连接 / 占满线程）。</summary>
    private const int MaxWaitMs = 45_000;

    /// <summary>长轮询轮询间隔。</summary>
    private const int PollIntervalMs = 250;

    public static void MapExternalApi(this WebApplication app)
    {
        var v1 = app.MapGroup("/agui/v1").AddEndpointFilter(new WebIdentity.RequireIdentityFilter());

        // ---- 调用者身份 ----
        v1.MapGet("/me", (HttpContext ctx, AuthService auth) =>
        {
            var me = WebIdentity.UserId(ctx)!;
            var user = auth.GetUser(me);
            return Results.Ok(new
            {
                userId = me,
                username = user?.Username,
                nickname = user?.Nickname,
                platformRole = PlatformRoleUtil.Name(auth.ResolveRole(me)),
            });
        });

        // ---- 我参与的知聚 ----
        v1.MapGet("/groups", (HttpContext ctx, IGroupStore store) =>
        {
            var me = WebIdentity.UserId(ctx)!;
            var groups = store.GroupsOf(me).Select(g => new
            {
                groupId = g.GroupId,
                name = g.GroupName,
                kind = g.Kind.ToString(),
                isPrivate = g.IsPrivate,
                isDirectChat = g.IsDirectChat,
                memberCount = g.MemberCount,
                ownerId = g.OwnerId,
                lastMessageAt = store.LastMessageAt(g.GroupId),
            }).ToList();
            return Results.Ok(groups);
        });

        // ---- 发送消息（可选同步等回复）----
        v1.MapPost("/groups/{groupId}/messages", async (string groupId, ExternalSendRequest req,
            HttpContext ctx, GroupHub hub, CancellationToken ct) => await RunAsync(async () =>
        {
            var me = WebIdentity.UserId(ctx)!;
            var (group, error) = Authorize(hub, groupId, me);
            if (error is not null) return error;

            var content = (req.Content ?? "").Trim();
            if (content.Length == 0) return Bad(ErrorCodes.BadRequest, "content 不能为空");
            if (content.Length > 20_000) return Bad(ErrorCodes.BadRequest, "content 过长（≤20000 字符）");

            var topicId = string.IsNullOrWhiteSpace(req.TopicId) ? "main" : req.TopicId!.Trim();
            var sent = await hub.SendMessageAsync(new GroupMessageSendRequest
            {
                GroupId = groupId,
                TopicId = topicId,
                UserId = me, // 发送者 = 服务端解析身份（始终覆盖，防伪造）
                Content = content,
                Mentions = req.Mentions,
                MentionAll = req.MentionAll ?? false,
                ReplyToMessageId = req.ReplyToMessageId,
                Attachments = req.Attachments,
            }, ct);

            var waitMs = Math.Clamp(req.WaitMs ?? 0, 0, MaxWaitMs);
            var reply = waitMs > 0
                ? await WaitForReplyAsync(hub, groupId, sent.MessageId, topicId, sent.Timestamp, me, waitMs, ct)
                : null;

            return Results.Ok(new
            {
                messageId = sent.MessageId,
                groupId,
                topicId,
                createdAt = sent.Timestamp,
                reply,
            });
        }));

        // ---- 消息历史（after=时间游标升序；否则 before=消息游标取更早）----
        v1.MapGet("/groups/{groupId}/messages", (string groupId, string? topicId, long? after, string? before, int? limit,
            HttpContext ctx, GroupHub hub) =>
        {
            var me = WebIdentity.UserId(ctx)!;
            var (group, error) = Authorize(hub, groupId, me);
            if (error is not null) return error;

            var take = Math.Clamp(limit ?? 50, 1, 200);
            var tid = string.IsNullOrWhiteSpace(topicId) ? null : topicId!.Trim();
            IEnumerable<GroupMessage> source = after is { } ts
                ? hub.Store.AllMessages(groupId).Where(m => m.Timestamp > ts).OrderBy(m => m.Timestamp)
                : hub.Store.MessagesBefore(groupId, before, take);

            var messages = source
                .Where(m => !m.Recalled && hub.CanSeeMessageAware(m, me))
                .Where(m => tid is null || string.Equals(m.TopicId ?? "main", tid, StringComparison.Ordinal))
                .Take(take)
                .Select(m => ToDto(m, groupId, hub))
                .ToList();
            return Results.Ok(messages);
        });

        // ---- 单条消息 ----
        v1.MapGet("/groups/{groupId}/messages/{messageId}", (string groupId, string messageId,
            HttpContext ctx, GroupHub hub) =>
        {
            var me = WebIdentity.UserId(ctx)!;
            var (group, error) = Authorize(hub, groupId, me);
            if (error is not null) return error;
            var msg = hub.Store.GetMessage(groupId, messageId);
            if (msg is null || msg.Recalled) return Results.NotFound(new AguiError(ErrorCodes.GroupMessageNotFound, "消息不存在或已撤回"));
            if (!hub.CanSeeMessageAware(msg, me)) return Results.Json(new AguiError(ErrorCodes.GroupPermissionDenied, "无权查看该消息"), statusCode: StatusCodes.Status403Forbidden);
            return Results.Ok(ToDto(msg, groupId, hub));
        });

        // ---- 轮询数字员工回复（after=时间游标升序；waitMs 长轮询到有新回复为止）----
        v1.MapGet("/groups/{groupId}/replies", async (string groupId, long? after, string? topicId, int? limit, int? waitMs,
            HttpContext ctx, GroupHub hub, CancellationToken ct) => await RunAsync(async () =>
        {
            var me = WebIdentity.UserId(ctx)!;
            var (group, error) = Authorize(hub, groupId, me);
            if (error is not null) return error;

            var tid = string.IsNullOrWhiteSpace(topicId) ? null : topicId!.Trim();
            var take = Math.Clamp(limit ?? 20, 1, 200);
            var since = after ?? 0;
            var budget = Math.Clamp(waitMs ?? 0, 0, MaxWaitMs);
            var deadline = Environment.TickCount64 + budget;

            while (true)
            {
                var replies = AgentReplies(hub, groupId, tid, since, me).Take(take).ToList();
                if (replies.Count > 0 || budget == 0 || Environment.TickCount64 >= deadline)
                    return Results.Ok(replies.Select(m => ToDto(m, groupId, hub)).ToList());
                await Task.Delay(PollIntervalMs, ct);
            }
        }));
    }

    // ================= 内部工具 =================

    /// <summary>发送后等待该次触发的数字员工回复完整：正文非空、且流式灌入已结束（不再增长）。</summary>
    private static async Task<object?> WaitForReplyAsync(GroupHub hub, string groupId, string sentMessageId, string topicId,
        long sinceMs, string viewer, int waitMs, CancellationToken ct)
    {
        var deadline = Environment.TickCount64 + waitMs;
        while (true)
        {
            var candidate = FindReplyCandidate(hub, groupId, sentMessageId, topicId, sinceMs, viewer);
            if (candidate is not null
                && !string.IsNullOrEmpty(candidate.Content)
                && !hub.IsAgentMessageStreaming(candidate.MessageId))
                return ToDto(candidate, groupId, hub); // 完整

            if (Environment.TickCount64 >= deadline)
                // 超时：有半截就返回半截并标注 complete=false（不给调用方空等）
                return candidate is null ? null : ToDto(candidate, groupId, hub, complete: false);

            await Task.Delay(PollIntervalMs, ct);
        }
    }

    /// <summary>按「优先回引本条消息的智能体回复，其次该话题内本条之后的任意智能体回复」定位候选回复。</summary>
    private static GroupMessage? FindReplyCandidate(GroupHub hub, string groupId, string sentMessageId, string topicId,
        long sinceMs, string viewer)
    {
        var candidates = hub.Store.AllMessages(groupId)
            .Where(m => !m.Recalled
                        && m.SenderType == MemberType.Agent
                        && m.Timestamp >= sinceMs
                        && string.Equals(m.TopicId ?? "main", topicId, StringComparison.Ordinal)
                        && hub.CanSeeMessageAware(m, viewer))
            .OrderBy(m => m.Timestamp)
            .ToList();
        return candidates.FirstOrDefault(m => m.ReplyToMessageId == sentMessageId) ?? candidates.FirstOrDefault();
    }

    private static IEnumerable<GroupMessage> AgentReplies(GroupHub hub, string groupId, string? topicId, long sinceMs, string viewer)
        => hub.Store.AllMessages(groupId)
            .Where(m => !m.Recalled
                        && m.SenderType == MemberType.Agent
                        && m.Timestamp > sinceMs
                        && (topicId is null || string.Equals(m.TopicId ?? "main", topicId, StringComparison.Ordinal))
                        && hub.CanSeeMessageAware(m, viewer))
            .OrderBy(m => m.Timestamp);

    private static (Group? Group, IResult? Error) Authorize(GroupHub hub, string groupId, string userId)
    {
        var group = hub.Store.GetGroup(groupId);
        if (group is null) return (null, Results.NotFound(new AguiError(ErrorCodes.GroupNotFound, "知聚不存在")));
        if (!hub.CanParticipate(groupId, userId))
            return (null, Results.Json(new AguiError(ErrorCodes.GroupPermissionDenied, "仅知聚成员（或客服参与者）可调用"),
                statusCode: StatusCodes.Status403Forbidden));
        return (group, null);
    }

    private static object ToDto(GroupMessage m, string groupId, GroupHub hub, bool complete = true) => new
    {
        messageId = m.MessageId,
        groupId,
        topicId = m.TopicId ?? "main",
        senderId = m.SenderId,
        senderType = m.SenderType.ToString(),
        senderNickname = m.SenderNickname,
        content = m.Content,
        replyToMessageId = m.ReplyToMessageId,
        timestamp = m.Timestamp,
        fromAgent = m.SenderType == MemberType.Agent,
        complete = m.SenderType != MemberType.Agent || (!hub.IsAgentMessageStreaming(m.MessageId) && !string.IsNullOrEmpty(m.Content) && complete),
        attachments = m.Attachments.Select(a => new { a.AttachmentId, a.Name, a.ContentType, a.Size, a.Url, a.Kind }),
        mentions = m.Mentions,
        mentionAll = m.MentionAll,
        reasoning = m.Reasoning,
    };

    private static IResult Bad(string code, string message) => Results.BadRequest(new AguiError(code, message));

    private static async Task<IResult> RunAsync(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (AguiProtocolException ex)
        {
            var status = ex.ErrorCode switch
            {
                ErrorCodes.GroupNotFound or ErrorCodes.GroupMemberNotExist or ErrorCodes.GroupMessageNotFound => StatusCodes.Status404NotFound,
                ErrorCodes.GroupPermissionDenied or ErrorCodes.AgentPermissionDenied => StatusCodes.Status403Forbidden,
                ErrorCodes.UserUnauthorized => StatusCodes.Status401Unauthorized,
                ErrorCodes.GroupFull => StatusCodes.Status409Conflict,
                _ => StatusCodes.Status400BadRequest,
            };
            return Results.Json(new AguiError(ex.ErrorCode, ex.Message), statusCode: status);
        }
    }
}

/// <summary>对外 API 发送消息请求体（全部可选，除 content）。</summary>
public sealed record ExternalSendRequest(
    string? Content,
    string? TopicId,
    string[]? Mentions,
    bool? MentionAll,
    string? ReplyToMessageId,
    IReadOnlyList<AttachmentInfo>? Attachments,
    int? WaitMs);
