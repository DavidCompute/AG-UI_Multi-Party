using AguiGroupChat.Hub.Agents;
using AguiGroupChat.Hub.Infra;
using AguiGroupChat.Hub.Messaging;
using AguiGroupChat.Hub.Models;
using AguiGroupChat.Hub.Options;
using AguiGroupChat.Hub.Users;
using AguiGroupChat.Hub.Storage;
using Microsoft.Extensions.Logging;

namespace AguiGroupChat.Hub.Transport;

/// <summary>协议 §5 客户端上行 HTTP API（群组管理 + 消息发送 + 订阅管理 + 智能体注册）。
/// 身份解析与 WS / SSE 端点一致：携带有效令牌时以令牌身份为准（覆盖请求体中的 operatorId / userId / memberId，
/// 防止登录会话被篡改 / 冒充他人）；未携带令牌时回退到请求体身份（兼容旧客户端 / 演示模式），
/// 除非 <see cref="AuthOptions.RequireTokenOnRealTime"/> = true（一律 401）。
/// GET 查询接口（群列表 / 快照 / 成员 / 历史分页 / 话题）同样要求身份，且仅群成员可读；
/// 防私密群内容泄露（协议 §5.4 的公开只读语义已收紧为成员可见）。</summary>
public static class HttpGroupApi
{
    public static void MapGroupApi(this WebApplication app)
    {
        var root = app.MapGroup("/ag-ui");

        root.MapGet("/health", (ConnectionManager connections, GroupHub hub) => Results.Json(new
        {
            status = "ok",
            connections = connections.ConnectionCount,
            groups = hub.Store.AllGroups().Count,
        }));

        // 某成员加入的群列表（前端「我的群」入口）：仅本人可查自己的群列表
        root.MapGet("/member/{memberId}/groups", async (string memberId, HttpContext ctx, AuthService auth, AuthOptions authOptions, GroupHub hub, CancellationToken ct)
            => await RunAsync(async () =>
            {
                var (identity, error) = RequireIdentity(ctx, auth, authOptions);
                if (identity is null) return error!;
                if (identity != memberId)
                    return Results.Json(new AguiError(ErrorCodes.GroupPermissionDenied, "仅本人可查询自己的群列表"),
                        statusCode: StatusCodes.Status403Forbidden);
                var result = hub.Store.GroupsOf(memberId).Select(g =>
                {
                    var me = hub.Store.GetMember(g.GroupId, memberId);
                    var (lastMessageAt, unreadCount, byTopic) = hub.UnreadInfo(g.GroupId, memberId);
                    return new
                    {
                        g.GroupId,
                        g.GroupName,
                        g.GroupAvatar,
                        g.MemberCount,
                        g.OwnerId,
                        g.IsPrivate,
                        myRole = me?.Role,
                        myNickname = me?.Nickname,
                        // 活跃度排序 / 未读提示（前端按 lastMessageAt 降序展示，未读徽标）
                        lastMessageAt,
                        unreadCount,
                        unreadByTopic = byTopic,
                    };
                });
                return Results.Ok(result);
            }));

        var group = root.MapGroup("/group");

        // ---- 群组生命周期（协议 5.2）----
        group.MapPost("/create", async (GroupCreateRequest req, HttpContext ctx, AuthService auth, AuthOptions authOptions, GroupHub hub, CancellationToken ct)
            => await RunAsync(async () =>
            {
                var (identity, error) = RequireIdentity(ctx, auth, authOptions, fallback: req.OwnerId);
                if (identity is null) return error!;
                req.OwnerId = identity; // 创建者 = 服务端解析身份，防伪造
                return Results.Ok(await hub.CreateGroupAsync(req, ct));
            }));
        group.MapPost("/update", async (GroupUpdateRequest req, HttpContext ctx, AuthService auth, AuthOptions authOptions, GroupHub hub, CancellationToken ct)
            => await RunAsync(async () =>
            {
                var (identity, error) = RequireIdentity(ctx, auth, authOptions, fallback: req.OperatorId);
                if (identity is null) return error!;
                req.OperatorId = identity;
                return Results.Ok(await hub.UpdateGroupAsync(req, ct));
            }));
        group.MapPost("/disband", async (GroupDisbandRequest req, HttpContext ctx, AuthService auth, AuthOptions authOptions, GroupHub hub, CancellationToken ct)
            => await RunAsync(async () =>
            {
                var (identity, error) = RequireIdentity(ctx, auth, authOptions, fallback: req.OperatorId);
                if (identity is null) return error!;
                req.OperatorId = identity; // 解散校验基于服务端身份：登录用户无法冒充群主解散
                await hub.DisbandGroupAsync(req, ct);
                return Results.Ok(new { ok = true });
            }));

        // ---- 群成员（协议 5.2）----
        group.MapPost("/member/add", async (GroupMemberAddRequest req, HttpContext ctx, AuthService auth, AuthOptions authOptions, GroupHub hub, CancellationToken ct)
            => await RunAsync(async () =>
            {
                var (identity, error) = RequireIdentity(ctx, auth, authOptions, fallback: req.OperatorId);
                if (identity is null) return error!;
                req.OperatorId = identity;
                return Results.Ok(await hub.AddMembersAsync(req, ct));
            }));
        group.MapPost("/member/remove", async (GroupMemberRemoveRequest req, HttpContext ctx, AuthService auth, AuthOptions authOptions, GroupHub hub, CancellationToken ct)
            => await RunAsync(async () =>
            {
                var (identity, error) = RequireIdentity(ctx, auth, authOptions, fallback: req.OperatorId);
                if (identity is null) return error!;
                req.OperatorId = identity;
                return Results.Ok(await hub.RemoveMembersAsync(req, ct));
            }));
        group.MapPost("/member/leave", async (GroupMemberLeaveRequest req, HttpContext ctx, AuthService auth, AuthOptions authOptions, GroupHub hub, CancellationToken ct)
            => await RunAsync(async () =>
            {
                var (identity, error) = RequireIdentity(ctx, auth, authOptions, fallback: req.MemberId);
                if (identity is null) return error!;
                req.MemberId = identity; // 退群者 = 服务端解析身份
                await hub.LeaveGroupAsync(req.GroupId, req.MemberId, ct);
                return Results.Ok(new { ok = true });
            }));
        group.MapPost("/member/update", async (GroupMemberUpdateRequest req, HttpContext ctx, AuthService auth, AuthOptions authOptions, GroupHub hub, CancellationToken ct)
            => await RunAsync(async () =>
            {
                var (identity, error) = RequireIdentity(ctx, auth, authOptions, fallback: req.OperatorId);
                if (identity is null) return error!;
                req.OperatorId = identity;
                return Results.Ok(await hub.UpdateMemberAsync(req, ct));
            }));

        // ---- 群消息（协议 5.1）----
        group.MapPost("/message/send", async (GroupMessageSendRequest req, HttpContext ctx, AuthService auth, AuthOptions authOptions, GroupHub hub, CancellationToken ct)
            => await RunAsync(async () =>
            {
                var (identity, error) = RequireIdentity(ctx, auth, authOptions, fallback: req.UserId);
                if (identity is null) return error!;
                req.UserId = identity; // 发送者 = 服务端解析身份，防冒充他人发言
                return Results.Ok(await hub.SendMessageAsync(req, ct));
            }));

        // ---- 人机交互（协议 4.5）：触发者批准 / 拒绝智能体的交互请求（仅触发者可决策）----
        group.MapPost("/interaction/resolve", async (GroupInteractionResolveRequest req, HttpContext ctx, AuthService auth, AuthOptions authOptions, GroupHub hub, AguiGroupChat.Hub.Infra.AuditLogService audit, CancellationToken ct)
            => await RunAsync(async () =>
            {
                var (identity, error) = RequireIdentity(ctx, auth, authOptions, fallback: req.MemberId);
                if (identity is null) return error!;
                req.MemberId = identity; // 决策者 = 服务端解析身份（网关再校验 == 触发者）
                var resolved = await hub.ResolveAgentInteractionAsync(req, ct);
                if (!resolved)
                {
                    audit.Record("interaction.resolve", identity, null, groupId: req.GroupId,
                        result: "error", detail: "决策失败（不存在 / 已过期 / 非触发者）");
                    return Results.BadRequest(new AguiError(ErrorCodes.BadRequest,
                        "交互请求不存在、已过期，或您不是该请求的决策者（仅触发者可交互）"));
                }
                audit.Record("interaction.resolve", identity, auth.GetUser(identity)?.Username, groupId: req.GroupId,
                    targetType: "interaction", targetId: req.InterruptId, result: req.Approved ? "ok" : "denied",
                    detail: (req.Approved ? "批准" : "拒绝") + (req.ApproveAll ? "（批量批准）" : ""));
                return Results.Ok(new { resolved = true });
            }));
        group.MapPost("/message/recall", async (GroupMessageRecallRequest req, HttpContext ctx, AuthService auth, AuthOptions authOptions, GroupHub hub, CancellationToken ct)
            => await RunAsync(async () =>
            {
                var (identity, error) = RequireIdentity(ctx, auth, authOptions, fallback: req.OperatorId);
                if (identity is null) return error!;
                req.OperatorId = identity;
                await hub.RecallMessageAsync(req, ct);
                return Results.Ok(new { ok = true });
            }));
        group.MapPost("/message/regenerate", async (GroupMessageRegenerateRequest req, HttpContext ctx, AuthService auth, AuthOptions authOptions, GroupHub hub, CancellationToken ct)
            => await RunAsync(async () =>
            {
                var (identity, error) = RequireIdentity(ctx, auth, authOptions);
                if (identity is null) return error!;
                req.OperatorId = identity;
                await hub.RegenerateMessageAsync(req, ct);
                return Results.Ok(new { ok = true });
            }));
        // 停止智能体运行（「停止生成」）：触发者本人或同群管理员可执行
        group.MapPost("/agent/stop", async (AgentStopRequest req, HttpContext ctx, AuthService auth, AuthOptions authOptions, GroupHub hub, CancellationToken ct)
            => await RunAsync(async () =>
            {
                var (identity, error) = RequireIdentity(ctx, auth, authOptions);
                if (identity is null) return error!;
                req.OperatorId = identity;
                var stopped = await hub.StopAgentRunAsync(req, ct);
                return Results.Ok(new { stopped });
            }));
        group.MapPost("/message/typing", async (GroupTypingRequest req, HttpContext ctx, AuthService auth, AuthOptions authOptions, GroupHub hub, CancellationToken ct)
            => await RunAsync(async () =>
            {
                var (identity, error) = RequireIdentity(ctx, auth, authOptions, fallback: req.MemberId);
                if (identity is null) return error!;
                req.MemberId = identity;
                await hub.BroadcastTypingAsync(req, ct);
                return Results.Ok(new { ok = true });
            }));
        group.MapPost("/message/read", async (GroupReadRequest req, HttpContext ctx, AuthService auth, AuthOptions authOptions, GroupHub hub, CancellationToken ct)
            => await RunAsync(async () =>
            {
                var (identity, error) = RequireIdentity(ctx, auth, authOptions, fallback: req.MemberId);
                if (identity is null) return error!;
                req.MemberId = identity;
                await hub.BroadcastReadAsync(req, ct);
                return Results.Ok(new { ok = true });
            }));

        // ---- 群话题（Hub 扩展）----
        group.MapPost("/topic/create", async (GroupTopicCreateRequest req, HttpContext ctx, AuthService auth, AuthOptions authOptions, GroupHub hub, CancellationToken ct)
            => await RunAsync(async () =>
            {
                var (identity, error) = RequireIdentity(ctx, auth, authOptions, fallback: req.OperatorId);
                if (identity is null) return error!;
                req.OperatorId = identity;
                return Results.Ok(await hub.CreateTopicAsync(req, ct));
            }));
        group.MapPost("/topic/delete", async (GroupTopicDeleteRequest req, HttpContext ctx, AuthService auth, AuthOptions authOptions, GroupHub hub, CancellationToken ct)
            => await RunAsync(async () =>
            {
                var (identity, error) = RequireIdentity(ctx, auth, authOptions, fallback: req.OperatorId);
                if (identity is null) return error!;
                req.OperatorId = identity;
                await hub.DeleteTopicAsync(req, ct);
                return Results.Ok(new { deleted = true, topicId = req.TopicId });
            }));
        group.MapPost("/topic/clear", async (GroupTopicClearRequest req, HttpContext ctx, AuthService auth, AuthOptions authOptions, GroupHub hub, CancellationToken ct)
            => await RunAsync(async () =>
            {
                var (identity, error) = RequireIdentity(ctx, auth, authOptions, fallback: req.OperatorId);
                if (identity is null) return error!;
                req.OperatorId = identity;
                var removed = await hub.ClearTopicMessagesAsync(req, ct);
                return Results.Ok(new { cleared = true, topicId = req.TopicId, removedCount = removed });
            }));
        group.MapGet("/{groupId}/topics", async (string groupId, HttpContext ctx, AuthService auth, AuthOptions authOptions, GroupHub hub, CancellationToken ct)
            => await RunAsync(async () =>
            {
                var (identity, error) = RequireIdentity(ctx, auth, authOptions);
                if (identity is null) return error!;
                if (hub.Store.GetGroup(groupId) is null)
                    return Results.NotFound(new AguiError(ErrorCodes.GroupNotFound, "群组不存在"));
                if (!hub.Store.IsMember(groupId, identity))
                    return Results.Json(new AguiError(ErrorCodes.GroupPermissionDenied, "仅群成员可查看群内容"),
                        statusCode: StatusCodes.Status403Forbidden);
                return Results.Ok(hub.Store.ListTopics(groupId));
            }));

        // 跨话题主题关联（5.1）：返回与指定话题<b>讨论内容最相关</b>的其它话题（按共享关键词评分）。
        group.MapGet("/{groupId}/topics/related", async (string groupId, string? topicId, HttpContext ctx, AuthService auth, AuthOptions authOptions, GroupHub hub, CancellationToken ct)
            => await RunAsync(async () =>
            {
                var (identity, error) = RequireIdentity(ctx, auth, authOptions);
                if (identity is null) return error!;
                if (hub.Store.GetGroup(groupId) is null)
                    return Results.NotFound(new AguiError(ErrorCodes.GroupNotFound, "群组不存在"));
                if (!hub.Store.IsMember(groupId, identity))
                    return Results.Json(new AguiError(ErrorCodes.GroupPermissionDenied, "仅群成员可查看群内容"),
                        statusCode: StatusCodes.Status403Forbidden);

                var messages = hub.Store.AllMessages(groupId).Where(m => !m.Recalled && !string.IsNullOrWhiteSpace(m.Content)).ToList();
                var textByTopic = messages
                    .GroupBy(m => string.IsNullOrEmpty(m.TopicId) ? "main" : m.TopicId)
                    .ToDictionary(g => g.Key, g => string.Join(" ", g.Select(m => m.Content)), StringComparer.Ordinal);

                var target = string.IsNullOrWhiteSpace(topicId) ? "main" : topicId;
                if (!textByTopic.TryGetValue(target, out var targetText))
                    return Results.Ok(new { topicId = target, related = Array.Empty<object>() }); // 目标无内容，无关联

                var related = textByTopic
                    .Where(kv => kv.Key != target)
                    .Select(kv =>
                    {
                        var score = TopicRelatedness(targetText, kv.Value);
                        return (TopicId: kv.Key, Score: score,
                            Name: hub.Store.GetTopic(groupId, kv.Key)?.Name ?? kv.Key);
                    })
                    .Where(r => r.Score > 0.02)
                    .OrderByDescending(r => r.Score)
                    .ThenBy(r => r.TopicId)
                    .Take(6)
                    .Select(r => new { r.TopicId, r.Name, score = Math.Round(r.Score, 3) })
                    .ToList();
                return Results.Ok(new { topicId = target, related });
            }));

        // 话题消息历史分页：before=游标消息 ID（不含），count 默认 50（上限 100）。
        // 返回与快照 latestMessages 相同结构的 SnapshotMessage 列表（按时间序，过滤撤回）。
        group.MapGet("/{groupId}/topics/{topicId}/messages", async (string groupId, string topicId, string? before, int? count, HttpContext ctx, AuthService auth, AuthOptions authOptions, GroupHub hub, CancellationToken ct)
            => await RunAsync(async () =>
            {
                var (identity, error) = RequireIdentity(ctx, auth, authOptions);
                if (identity is null) return error!;
                if (hub.Store.GetGroup(groupId) is null)
                    return Results.NotFound(new AguiError(ErrorCodes.GroupNotFound, "群组不存在"));
                if (topicId != "main" && hub.Store.GetTopic(groupId, topicId) is null)
                    return Results.NotFound(new AguiError(ErrorCodes.GroupNotFound, "话题不存在"));
                if (!hub.Store.IsMember(groupId, identity))
                    return Results.Json(new AguiError(ErrorCodes.GroupPermissionDenied, "仅群成员可查看群内容"),
                        statusCode: StatusCodes.Status403Forbidden);
                var limit = Math.Clamp(count ?? 50, 1, 100);
                var messages = hub.Store.MessagesBefore(groupId, before, limit, topicId)
                    .Where(m => !m.Recalled && GroupHub.CanSeeMessage(m, identity))
                    .Select(m => new SnapshotMessage
                    {
                        MessageId = m.MessageId,
                        SenderId = m.SenderId,
                        SenderNickname = m.SenderNickname,
                        Content = m.Content,
                        TopicId = m.TopicId,
                        ReplyToMessageId = m.ReplyToMessageId,
                        Attachments = m.Attachments,
                        Mentions = m.Mentions,
                        MentionAll = m.MentionAll,
                        Reasoning = m.Reasoning,
                        Timestamp = m.Timestamp,
                    })
                    .ToList();
                return Results.Ok(messages);
            }));

        // ---- 查询（群内容仅成员可见，防私密群内容泄露）----
        group.MapGet("/{groupId}", async (string groupId, HttpContext ctx, AuthService auth, AuthOptions authOptions, GroupHub hub, CancellationToken ct)
            => await RunAsync(async () =>
            {
                var (identity, error) = RequireIdentity(ctx, auth, authOptions);
                if (identity is null) return error!;
                if (hub.Store.GetGroup(groupId) is null)
                    return Results.NotFound(new AguiError(ErrorCodes.GroupNotFound, "群组不存在"));
                if (!hub.Store.IsMember(groupId, identity))
                    return Results.Json(new AguiError(ErrorCodes.GroupPermissionDenied, "仅群成员可查看群内容"),
                        statusCode: StatusCodes.Status403Forbidden);
                return Results.Ok(await hub.BuildSnapshotAsync(groupId, identity, ct));
            }));
        group.MapGet("/{groupId}/members", async (string groupId, HttpContext ctx, AuthService auth, AuthOptions authOptions, GroupHub hub, CancellationToken ct)
            => await RunAsync(async () =>
            {
                var (identity, error) = RequireIdentity(ctx, auth, authOptions);
                if (identity is null) return error!;
                if (hub.Store.GetGroup(groupId) is null)
                    return Results.NotFound(new AguiError(ErrorCodes.GroupNotFound, "群组不存在"));
                if (!hub.Store.IsMember(groupId, identity))
                    return Results.Json(new AguiError(ErrorCodes.GroupPermissionDenied, "仅群成员可查看群内容"),
                        statusCode: StatusCodes.Status403Forbidden);
                return Results.Ok(hub.Store.ListMembers(groupId));
            }));

        // 消息历史分页（前端虚拟滚动「加载更早消息」）：before=游标消息 ID（不含），count 默认 50（上限 100）。
        // 返回与快照 latestMessages 相同结构的 SnapshotMessage 列表（按时间序，过滤撤回）。
        group.MapGet("/{groupId}/messages", async (string groupId, string? before, int? count, HttpContext ctx, AuthService auth, AuthOptions authOptions, GroupHub hub, CancellationToken ct)
            => await RunAsync(async () =>
            {
                var (identity, error) = RequireIdentity(ctx, auth, authOptions);
                if (identity is null) return error!;
                if (hub.Store.GetGroup(groupId) is null)
                    return Results.NotFound(new AguiError(ErrorCodes.GroupNotFound, "群组不存在"));
                if (!hub.Store.IsMember(groupId, identity))
                    return Results.Json(new AguiError(ErrorCodes.GroupPermissionDenied, "仅群成员可查看群内容"),
                        statusCode: StatusCodes.Status403Forbidden);
                var limit = Math.Clamp(count ?? 50, 1, 100);
                var messages = hub.Store.MessagesBefore(groupId, before, limit)
                    .Where(m => !m.Recalled && GroupHub.CanSeeMessage(m, identity))
                    .Select(m => new SnapshotMessage
                    {
                        MessageId = m.MessageId,
                        SenderId = m.SenderId,
                        SenderNickname = m.SenderNickname,
                        Content = m.Content,
                        TopicId = m.TopicId,
                        ReplyToMessageId = m.ReplyToMessageId,
                        Attachments = m.Attachments,
                        Mentions = m.Mentions,
                        MentionAll = m.MentionAll,
                        Reasoning = m.Reasoning,
                        Timestamp = m.Timestamp,
                    })
                    .ToList();
                return Results.Ok(messages);
            }));

        // 消息全文搜索（群内，仅群成员）：q 关键词（必填），topicId 可选限定话题，count 默认 20（上限 100）。
        // 返回按时间倒序的 SnapshotMessage 列表，过滤撤回与不可见（定向 / 私密）消息。
        group.MapGet("/{groupId}/messages/search", async (string groupId, string q, string? topicId, int? count, HttpContext ctx, AuthService auth, AuthOptions authOptions, GroupHub hub, CancellationToken ct)
            => await RunAsync(async () =>
            {
                var (identity, error) = RequireIdentity(ctx, auth, authOptions);
                if (identity is null) return error!;
                if (hub.Store.GetGroup(groupId) is null)
                    return Results.NotFound(new AguiError(ErrorCodes.GroupNotFound, "群组不存在"));
                if (!hub.Store.IsMember(groupId, identity))
                    return Results.Json(new AguiError(ErrorCodes.GroupPermissionDenied, "仅群成员可搜索群消息"),
                        statusCode: StatusCodes.Status403Forbidden);
                if (string.IsNullOrWhiteSpace(q))
                    return Results.BadRequest(new AguiError(ErrorCodes.BadRequest, "缺少搜索关键词 q"));
                var limit = Math.Clamp(count ?? 20, 1, 100);
                var messages = hub.Store.SearchMessages(groupId, q.Trim(), string.IsNullOrWhiteSpace(topicId) ? null : topicId, limit)
                    .Where(m => !m.Recalled && GroupHub.CanSeeMessage(m, identity))
                    .Select(m => new SnapshotMessage
                    {
                        MessageId = m.MessageId,
                        SenderId = m.SenderId,
                        SenderNickname = m.SenderNickname,
                        Content = m.Content,
                        TopicId = m.TopicId,
                        ReplyToMessageId = m.ReplyToMessageId,
                        Attachments = m.Attachments,
                        Mentions = m.Mentions,
                        MentionAll = m.MentionAll,
                        Reasoning = m.Reasoning,
                        Timestamp = m.Timestamp,
                    })
                    .ToList();
                return Results.Ok(messages);
            }));

        // 多智能体讨论：用户 @ 多个智能体发起话题，按序串行触发（前序智能体的回复作为后序的群历史上下文），
        // 后台执行（智能体回复经 WS 实时广播），接口立即返回已受理。
        group.MapPost("/{groupId}/discussion", async (string groupId, DiscussionHttpRequest req, HttpContext ctx, AuthService auth, AuthOptions authOptions, GroupHub hub, IAgentGateway gateway, ILoggerFactory loggerFactory, CancellationToken ct)
            => await RunAsync(async () =>
            {
                var (identity, error) = RequireIdentity(ctx, auth, authOptions);
                if (identity is null) return error!;
                if (hub.Store.GetGroup(groupId) is null)
                    return Results.NotFound(new AguiError(ErrorCodes.GroupNotFound, "群组不存在"));
                if (!hub.Store.IsMember(groupId, identity))
                    return Results.Json(new AguiError(ErrorCodes.GroupPermissionDenied, "仅群成员可发起讨论"),
                        statusCode: StatusCodes.Status403Forbidden);
                if (string.IsNullOrWhiteSpace(req.Content))
                    return Results.BadRequest(new AguiError(ErrorCodes.BadRequest, "讨论主题不能为空"));
                var agentIds = (req.AgentIds ?? []).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToList();
                if (agentIds.Count == 0)
                    return Results.BadRequest(new AguiError(ErrorCodes.BadRequest, "请至少选择一位智能体参与讨论"));
                // 参与讨论的智能体必须是群成员（防把外部/无权智能体拉进群话题）
                foreach (var agentId in agentIds)
                {
                    var member = hub.Store.GetMember(groupId, agentId);
                    if (member is null || member.MemberType != MemberType.Agent)
                        return Results.BadRequest(new AguiError(ErrorCodes.BadRequest, $"智能体 {agentId} 不在该群，无法参与讨论"));
                }

                var topicId = string.IsNullOrWhiteSpace(req.TopicId) ? "main" : req.TopicId;
                var threadId = "thread_" + groupId;
                var inviterNick = hub.Store.GetMember(groupId, identity)?.Nickname ?? identity;
                var theme = req.Content.Trim();
                // 后台串行触发：前一个智能体回复落库后，后一个在群历史中能看到（BuildUserMessageAsync 注入最近对话）
                _ = Task.Run(async () =>
                {
                    foreach (var agentId in agentIds)
                    {
                        try
                        {
                            var member = hub.Store.GetMember(groupId, agentId);
                            var content = $"【群讨论】{inviterNick} 邀请你参与讨论「{theme}」。请先阐述你的观点，再回应其他智能体的发言（如有）；保持简洁，直接开始。";
                            await gateway.InvokeAsync(new AgentInvocationContext(
                                GroupId: groupId,
                                ThreadId: threadId,
                                AgentId: agentId,
                                AgentNickname: member?.Nickname ?? agentId,
                                TriggerMessageId: "",
                                TriggerUserId: identity,
                                Content: content,
                                Mentions: [],
                                MentionAll: false,
                                TopicId: topicId), CancellationToken.None);
                        }
                        catch (Exception ex)
                        {
                            loggerFactory.CreateLogger("HttpGroupApi").LogWarning(ex, "讨论触发失败 agent={AgentId}", agentId);
                        }
                    }
                });
                return Results.Ok(new { started = true, agents = agentIds });
            }));

        // 搜索结果定位：返回指定消息及其前后各 count/2 条（按时间序，供前端跳转后重建窗口）。
        group.MapGet("/{groupId}/messages/around", async (string groupId, string messageId, string? topicId, int? count, HttpContext ctx, AuthService auth, AuthOptions authOptions, GroupHub hub, CancellationToken ct)
            => await RunAsync(async () =>
            {
                var (identity, error) = RequireIdentity(ctx, auth, authOptions);
                if (identity is null) return error!;
                if (hub.Store.GetGroup(groupId) is null)
                    return Results.NotFound(new AguiError(ErrorCodes.GroupNotFound, "群组不存在"));
                if (!hub.Store.IsMember(groupId, identity))
                    return Results.Json(new AguiError(ErrorCodes.GroupPermissionDenied, "仅群成员可查看群内容"),
                        statusCode: StatusCodes.Status403Forbidden);
                var target = hub.Store.GetMessage(groupId, messageId);
                if (target is null)
                    return Results.NotFound(new AguiError(ErrorCodes.GroupMessageNotFound, "消息不存在"));
                var topic = string.IsNullOrWhiteSpace(topicId) ? null : topicId;
                var half = Math.Clamp((count ?? 40) / 2, 1, 50);
                var before = hub.Store.MessagesBefore(groupId, messageId, half, topic);
                var after = hub.Store.MessagesAfter(groupId, messageId, half, topic);
                var messages = before.Concat(new[] { target }).Concat(after)
                    .Where(m => !m.Recalled && GroupHub.CanSeeMessage(m, identity))
                    .Select(m => new SnapshotMessage
                    {
                        MessageId = m.MessageId,
                        SenderId = m.SenderId,
                        SenderNickname = m.SenderNickname,
                        Content = m.Content,
                        TopicId = m.TopicId,
                        ReplyToMessageId = m.ReplyToMessageId,
                        Attachments = m.Attachments,
                        Mentions = m.Mentions,
                        MentionAll = m.MentionAll,
                        Reasoning = m.Reasoning,
                        Timestamp = m.Timestamp,
                    })
                    .ToList();
                return Results.Ok(messages);
            }));

        // ---- 订阅管理（SSE 场景：以 GROUP_CONNECTED 返回的 connectionId 动态订阅 / 退订）----
        root.MapPost("/group/subscribe", async (SseSubscribeRequest req, HttpContext ctx, AuthService auth, AuthOptions authOptions, ConnectionManager connections, GroupHub hub, CancellationToken ct)
            => await RunAsync(async () =>
            {
                var (identity, error) = RequireIdentity(ctx, auth, authOptions);
                if (identity is null) return error!;
                var connection = connections.Get(req.ConnectionId);
                if (connection is null)
                    return Results.NotFound(new AguiError(ErrorCodes.GroupSubscribeFailed, "连接不存在或已断开"));
                if (connection.MemberId != identity)
                    return Results.Json(new AguiError(ErrorCodes.GroupPermissionDenied, "只能管理自己的连接"),
                        statusCode: StatusCodes.Status403Forbidden);
                await hub.SubscribeAsync(connection, req.GroupIds, ct);
                return Results.Ok(new { subscribed = true });
            }));
        root.MapPost("/group/unsubscribe", async (SseSubscribeRequest req, HttpContext ctx, AuthService auth, AuthOptions authOptions, ConnectionManager connections, GroupHub hub, CancellationToken ct)
            => await RunAsync(async () =>
            {
                var (identity, error) = RequireIdentity(ctx, auth, authOptions);
                if (identity is null) return error!;
                var connection = connections.Get(req.ConnectionId);
                if (connection is null)
                    return Results.NotFound(new AguiError(ErrorCodes.GroupSubscribeFailed, "连接不存在或已断开"));
                if (connection.MemberId != identity)
                    return Results.Json(new AguiError(ErrorCodes.GroupPermissionDenied, "只能管理自己的连接"),
                        statusCode: StatusCodes.Status403Forbidden);
                await hub.UnsubscribeAsync(connection, req.GroupIds, ct);
                return Results.Ok(new { unsubscribed = true });
            }));

        // ---- 智能体注册（协议 §6 触发规则，Hub 管理面）----
        // 权限与 Web 面（AgentApi /agents/register）对齐：群必须存在、调用者必须是群成员、
        // 被注册的智能体必须是该群成员；防止任意用户向他人群注册监听规则 / 污染触发表。
        root.MapPost("/agent/register", (AgentRegisterRequest req, HttpContext ctx, AuthService auth, AuthOptions authOptions, GroupHub hub, AgentRegistry registry) =>
        {
            var (identity, error) = RequireIdentity(ctx, auth, authOptions);
            if (identity is null) return error!;
            var groupIds = (req.GroupIds ?? []).Distinct().ToList();
            if (groupIds.Count == 0)
                return Results.BadRequest(new AguiError(ErrorCodes.BadRequest, "groupIds 不能为空"));
            foreach (var groupId in groupIds)
            {
                if (hub.Store.GetGroup(groupId) is null)
                    return Results.NotFound(new AguiError(ErrorCodes.GroupNotFound, "群组不存在"));
                if (!hub.Store.IsMember(groupId, identity))
                    return Results.Json(new AguiError(ErrorCodes.GroupPermissionDenied, "仅群成员可注册触发规则"),
                        statusCode: StatusCodes.Status403Forbidden);
                if (!hub.Store.IsMember(groupId, req.AgentId))
                    return Results.Json(new AguiError(ErrorCodes.AgentPermissionDenied, "智能体不在该群，无法注册触发规则"),
                        statusCode: StatusCodes.Status403Forbidden);
            }
            req.GroupIds = groupIds; // 去重后的群列表写回（不触发额外权限面）
            registry.Register(req);
            return Results.Ok(new { registered = true, agentId = req.AgentId, groupIds });
        });
        // ---- 智能体注销：指定群 → 调用者须为该群主 / 管理员；未指定群（全部注销）→ 仅系统管理员可执行 ----
        root.MapPost("/agent/unregister", (AgentUnregisterRequest req, HttpContext ctx, AuthService auth, AuthOptions authOptions, GroupHub hub, AgentRegistry registry) =>
        {
            var (identity, error) = RequireIdentity(ctx, auth, authOptions);
            if (identity is null) return error!;
            var groupIds = req.GroupIds ?? [];
            if (groupIds.Count == 0)
            {
                if (!auth.IsAdmin(identity))
                    return Results.Json(new AguiError(ErrorCodes.GroupPermissionDenied, "注销全部群的触发规则仅系统管理员可执行，请指定群后重试"),
                        statusCode: StatusCodes.Status403Forbidden);
            }
            else
            {
                foreach (var groupId in groupIds.Distinct())
                {
                    var group = hub.Store.GetGroup(groupId);
                    if (group is null)
                        return Results.NotFound(new AguiError(ErrorCodes.GroupNotFound, "群组不存在"));
                    var isManager = group.OwnerId == identity
                        || hub.Store.GetMember(groupId, identity) is { Role: GroupRole.Admin };
                    if (!isManager)
                        return Results.Json(new AguiError(ErrorCodes.GroupPermissionDenied, "仅群主 / 管理员可注销该群的触发规则"),
                            statusCode: StatusCodes.Status403Forbidden);
                    if (!hub.Store.IsMember(groupId, req.AgentId))
                        return Results.Json(new AguiError(ErrorCodes.AgentPermissionDenied, "智能体不在该群"),
                            statusCode: StatusCodes.Status403Forbidden);
                }
            }
            registry.Unregister(req.AgentId, groupIds);
            return Results.Ok(new { unregistered = true, agentId = req.AgentId });
        });
    }

    /// <summary>
    /// 解析身份（同 WS / SSE）：有效令牌 → 令牌身份；无令牌 → `?memberId=` 查询参数，再回退到
    /// 请求体 / 路径携带的身份（兼容旧客户端与演示模式）；<see cref="AuthOptions.RequireTokenOnRealTime"/> = true 时一律 401。
    /// </summary>
    private static (string? Identity, IResult? Error) RequireIdentity(
        HttpContext ctx, AuthService auth, AuthOptions authOptions, string? fallback = null)
    {
        var token = ResolveToken(ctx.Request);
        if (!string.IsNullOrEmpty(token))
        {
            var user = auth.ValidateToken(token);
            if (user is null)
                return (null, Results.Json(new AguiError(ErrorCodes.UserUnauthorized, "未登录或令牌无效"),
                    statusCode: StatusCodes.Status401Unauthorized));
            return (user.UserId, null);
        }
        if (authOptions.RequireTokenOnRealTime)
            return (null, Results.Json(new AguiError(ErrorCodes.UserUnauthorized, "缺少身份令牌（Auth:RequireTokenOnRealTime=true）"),
                statusCode: StatusCodes.Status401Unauthorized));
        var memberId = ctx.Request.Query["memberId"].ToString();
        if (string.IsNullOrWhiteSpace(memberId)) memberId = fallback;
        return string.IsNullOrWhiteSpace(memberId)
            ? (null, Results.Json(new AguiError(ErrorCodes.UserUnauthorized, "缺少身份（登录 token 或 memberId）"),
                statusCode: StatusCodes.Status401Unauthorized))
            : (memberId.Trim(), null);
    }

    /// <summary>令牌来源：Authorization: Bearer 头 → ?token= 查询参数。</summary>
    private static string? ResolveToken(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return header["Bearer ".Length..].Trim();
        var query = request.Query["token"].ToString();
        return string.IsNullOrEmpty(query) ? null : query;
    }

    /// <summary>触发规则注册的群列表。</summary>
    internal static IReadOnlyList<string> ResolveGroupIds(AgentRegisterRequest req)
        => req.GroupIds;

    private static async Task<IResult> RunAsync(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (AguiProtocolException ex) { return ToResult(ex); }
    }

    private static IResult ToResult(AguiProtocolException ex) => ex.ErrorCode switch
    {
        ErrorCodes.GroupNotFound or ErrorCodes.GroupMemberNotExist or ErrorCodes.GroupMessageNotFound
            => Results.NotFound(new AguiError(ex.ErrorCode, ex.Message)),
        ErrorCodes.GroupPermissionDenied or ErrorCodes.AgentPermissionDenied
            => Results.Json(new AguiError(ex.ErrorCode, ex.Message), statusCode: StatusCodes.Status403Forbidden),
        ErrorCodes.GroupFull
            => Results.Json(new AguiError(ex.ErrorCode, ex.Message), statusCode: StatusCodes.Status409Conflict),
        _ => Results.BadRequest(new AguiError(ex.ErrorCode, ex.Message)),
    };

    /// <summary>跨话题关联（5.1）：两段文本的共享关键词相似度（Jaccard on 分词），取值 [0,1]。</summary>
    private static double TopicRelatedness(string a, string b)
    {
        var ta = Tokens(a).ToHashSet(StringComparer.Ordinal);
        var tb = Tokens(b).ToHashSet(StringComparer.Ordinal);
        if (ta.Count == 0 || tb.Count == 0) return 0;
        var inter = ta.Count(tb.Contains);
        if (inter == 0) return 0;
        return (double)inter / Math.Max(1, ta.Count + tb.Count - inter);
    }

    /// <summary>分词：ASCII 字母/数字/下划线整词 + 汉字逐字（与检索层一致，便于跨话题关键词比对）。</summary>
    private static IEnumerable<string> Tokens(string s)
    {
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(s ?? "", @"[a-zA-Z0-9_\u4e00-\u9fa5]+"))
        {
            var t = m.Value;
            var pure = true;
            foreach (var ch in t)
            {
                if (ch is >= '\u4e00' and <= '\u9fa5') { yield return ch.ToString(); pure = false; }
                else break;
            }
            if (pure) yield return t.ToLowerInvariant();
        }
    }
}
