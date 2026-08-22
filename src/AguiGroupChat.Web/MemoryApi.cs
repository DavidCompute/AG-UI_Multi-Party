using AguiGroupChat.Hub.Agents;
using AguiGroupChat.Hub.Messaging;
using AguiGroupChat.Hub.Models;
using AguiGroupChat.Hub.Options;
using AguiGroupChat.Hub.Storage;
using AguiGroupChat.Hub.Users;

namespace AguiGroupChat.Web;

/// <summary>
/// 记忆治理 HTTP API（记忆分群分级 / 自动遗忘 / 可视化）：
///   GET  /ag-ui/memory/groups              —— 用户所在各群的记忆统计（总览）
///   GET  /ag-ui/memory                     —— 记忆条目列表（按群 / 发送者 / 关键词筛选，分页）
///   POST /ag-ui/memory/{messageId}/importance —— 调整单条记忆级别（0 普通 / 1 重要 / 2 关键）
///   DELETE /ag-ui/memory/{messageId}       —— 物理删除单条记忆
///   POST /ag-ui/memory/forget              —— 手动遗忘：按群（或全部）设过期，可保留最近 N 小时
/// 权限：仅可查看 / 治理<b>自己所在群</b>的记忆（服务端按成员身份校验）。
/// </summary>
public static class MemoryApi
{
    public static void MapMemoryApi(this WebApplication app)
    {
        var root = app.MapGroup("/ag-ui/memory");

        // ---- 总览：各群记忆统计 ----
        root.MapGet("/groups", (HttpContext ctx, AuthService auth, AuthOptions authOptions, IGroupStore store, IMessageMemory memory) =>
        {
            var (userId, error) = WebIdentity.ResolveIdentity(ctx, auth, authOptions);
            if (error is not null) return error;
            if (userId is null) return error!;
            var memberGroups = store.GroupsOf(userId).Select(g => g.GroupId).ToHashSet(StringComparer.Ordinal);
            var stats = memory.GroupStats()
                .Where(s => memberGroups.Contains(s.GroupId))
                .Select(s => new
                {
                    groupId = s.GroupId,
                    groupName = store.GetGroup(s.GroupId)?.GroupName ?? s.GroupId,
                    count = s.Count,
                    lastAt = s.LastAt,
                    expiredCount = s.ExpiredCount,
                })
                .ToList();
            return Results.Ok(stats);
        });

        // ---- 记忆条目列表（可视化） ----
        root.MapGet("/", (HttpContext ctx, AuthService auth, AuthOptions authOptions, IGroupStore store, IUserStore users, IMessageMemory memory,
            string? groupId, string? senderId, string? keyword, int limit = 50, int offset = 0) =>
        {
            var (userId, error) = WebIdentity.ResolveIdentity(ctx, auth, authOptions);
            if (error is not null) return error;
            if (userId is null) return error!;

            // 群过滤：只能查看自己所在群
            if (!string.IsNullOrWhiteSpace(groupId) && !store.IsMember(groupId, userId))
                return Results.Json(new AguiError(ErrorCodes.GroupPermissionDenied, "仅群成员可查看该群记忆"),
                    statusCode: StatusCodes.Status403Forbidden);

            var isAdmin = auth.IsAdmin(userId);
            var items = memory.ListMessages(groupId, senderId, keyword, Math.Clamp(limit, 1, 500), Math.Max(0, offset));
            return Results.Ok(new
            {
                total = memory.CountMessages(groupId, senderId, keyword),
                items = items.Select(m => new
                {
                    m.MessageId,
                    m.GroupId,
                    groupName = store.GetGroup(m.GroupId)?.GroupName ?? m.GroupId,
                    m.TopicId,
                    m.SenderId,
                    m.SenderType,
                    senderNickname = ResolveSenderName(store, users, m.GroupId, m.SenderId, m.SenderType),
                    m.Content,
                    m.Timestamp,
                    m.Importance,
                    m.ExpiresAt,
                    canManage = isAdmin || m.SenderId == userId, // 仅本人 / 管理员可删除、分级
                }),
            });
        });

        // ---- 调整单条记忆级别 ----
        root.MapPost("/{messageId}/importance", (string messageId, MemoryImportanceHttpRequest req, HttpContext ctx,
            AuthService auth, AuthOptions authOptions, IMessageMemory memory) =>
        {
            var (userId, error) = WebIdentity.ResolveIdentity(ctx, auth, authOptions);
            if (error is not null) return error;
            if (userId is null) return error!;
            if (!MemoryImportance.IsValid(req.Importance))
                return Results.BadRequest(new AguiError(ErrorCodes.BadRequest, "importance 取值范围 0（普通）/ 1（重要）/ 2（关键）"));
            var ownership = CheckOwnership(memory, auth, messageId, userId);
            if (ownership is not null) return ownership;
            return memory.UpdateImportance(messageId, req.Importance)
                ? Results.Ok(new { ok = true })
                : Results.NotFound(new AguiError(ErrorCodes.GroupMessageNotFound, "记忆不存在"));
        });

        // ---- 物理删除单条记忆 ----
        root.MapDelete("/{messageId}", (string messageId, HttpContext ctx, AuthService auth, AuthOptions authOptions, IMessageMemory memory) =>
        {
            var (userId, error) = WebIdentity.ResolveIdentity(ctx, auth, authOptions);
            if (error is not null) return error;
            if (userId is null) return error!;
            var ownership = CheckOwnership(memory, auth, messageId, userId);
            if (ownership is not null) return ownership;
            return memory.DeleteByMessageId(messageId)
                ? Results.Ok(new { ok = true })
                : Results.NotFound(new AguiError(ErrorCodes.GroupMessageNotFound, "记忆不存在"));
        });

        // ---- 手动遗忘：按群（或全部）设过期，可选保留最近 N 小时 ----
        root.MapPost("/forget", (MemoryForgetHttpRequest req, HttpContext ctx, AuthService auth, AuthOptions authOptions,
            IGroupStore store, IMessageMemory memory) =>
        {
            var (userId, error) = WebIdentity.ResolveIdentity(ctx, auth, authOptions);
            if (error is not null) return error;
            if (userId is null) return error!;

            var isAdmin = auth.IsAdmin(userId);
            if (!isAdmin && !string.IsNullOrWhiteSpace(req.GroupId) && !store.IsMember(req.GroupId, userId))
                return Results.Json(new AguiError(ErrorCodes.GroupPermissionDenied, "仅群成员可遗忘该群记忆"),
                    statusCode: StatusCodes.Status403Forbidden);

            // 权限：管理员可遗忘任意记忆（按群 / 全部群设过期）；普通用户仅可遗忘自己的记忆
            // （按发送者过滤后物理删除——普通用户的遗忘范围天然限制在本人发言，不触碰他人记忆）
            var affected = isAdmin
                ? memory.ForgetGroup(string.IsNullOrWhiteSpace(req.GroupId) ? null : req.GroupId, req.RetentionHours)
                : ForgetOwnMessages(memory, string.IsNullOrWhiteSpace(req.GroupId) ? null : req.GroupId, userId, req.RetentionHours);
            return Results.Ok(new { ok = true, affected });
        });

        // ---- 记忆 / 结论沉淀为知识库文档（1.3）：把某群「关键」级别的记忆聚合写入指定知识库 ----
        root.MapPost("/consolidate", async (MemoryConsolidateHttpRequest req, HttpContext ctx,
            AuthService auth, AuthOptions authOptions, IGroupStore store,
            AguiGroupChat.Agents.KnowledgeBaseCatalog kbs, AguiGroupChat.Hub.Persistence.IMessageMemoryStore memoryStore,
            CancellationToken ct) =>
        {
            var (userId, error) = WebIdentity.ResolveIdentity(ctx, auth, authOptions);
            if (error is not null) return error;
            if (userId is null) return error!;
            if (string.IsNullOrWhiteSpace(req.GroupId) || string.IsNullOrWhiteSpace(req.KbId))
                return Results.BadRequest(new AguiError(ErrorCodes.BadRequest, "groupId 与 kbId 均必填"));
            // 只能沉淀「自己所在群」的记忆
            if (!store.IsMember(req.GroupId, userId))
                return Results.Json(new AguiError(ErrorCodes.GroupPermissionDenied, "仅群成员可沉淀该群记忆"),
                    statusCode: StatusCodes.Status403Forbidden);
            // 目标知识库须为系统级或当前用户创建（只读他人知识库不可写入）
            var kb = kbs.GetKb(req.KbId);
            if (kb is null)
                return Results.NotFound(new AguiError(ErrorCodes.BadRequest, "知识库不存在"));
            if (kb.OwnerId is not null && kb.OwnerId != userId && !auth.IsAdmin(userId))
                return Results.Json(new AguiError(ErrorCodes.AgentPermissionDenied, "只能将该群结论沉淀到系统级或自己创建的知识库"),
                    statusCode: StatusCodes.Status403Forbidden);

            var (doc, err, count) = await kbs.ConsolidateGroupMemoriesAsync(req.GroupId, req.KbId, memoryStore, ct);
            if (err is not null)
                return Results.BadRequest(new AguiError(ErrorCodes.BadRequest, err));
            return Results.Ok(new
            {
                ok = true,
                docId = doc!.DocId,
                fileName = doc.FileName,
                status = doc.Status,
                memoryCount = count,
            });
        });
    }

    /// <summary>单条记忆所有权校验：仅记忆本人（或管理员）可删除 / 分级；越权返回 403。</summary>
    private static IResult? CheckOwnership(IMessageMemory memory, AuthService auth, string messageId, string userId)
    {
        var item = memory.GetByMessageId(messageId);
        if (item is null)
            return Results.NotFound(new AguiError(ErrorCodes.GroupMessageNotFound, "记忆不存在"));
        if (item.SenderId != userId && !auth.IsAdmin(userId))
            return Results.Json(new AguiError(ErrorCodes.GroupPermissionDenied, "仅可管理自己的记忆（管理员可管理任意记忆）"),
                statusCode: StatusCodes.Status403Forbidden);
        return null;
    }

    /// <summary>普通用户遗忘自己的记忆：按发送者过滤出消息，保留最近 N 小时（retentionHours &gt; 0）后物理删除其余。</summary>
    private static int ForgetOwnMessages(IMessageMemory memory, string? groupId, string userId, double? retentionHours)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var keepMs = retentionHours is > 0 ? (long)(retentionHours.Value * 3_600_000) : 0;
        var items = memory.ListMessages(groupId, userId, null, 5000, 0); // 仅自己的记忆
        var affected = 0;
        foreach (var item in items)
        {
            if (keepMs > 0 && item.Timestamp >= now - keepMs) continue; // 保留最近 N 小时
            if (memory.DeleteByMessageId(item.MessageId)) affected++;
        }
        return affected;
    }

    /// <summary>记忆发送者显示名：用户取账号昵称，智能体取群成员昵称，均无则回退原始 ID。</summary>
    private static string ResolveSenderName(IGroupStore store, IUserStore users, string groupId, string senderId, string senderType)
    {
        var member = store.GetMember(groupId, senderId);
        if (member?.Nickname is { Length: > 0 } n && (senderType != "user" || string.IsNullOrWhiteSpace(users.GetUserById(senderId)?.Nickname)))
            return n;
        if (users.GetUserById(senderId)?.Nickname is { Length: > 0 } un) return un;
        return senderId;
    }
}

/// <summary>记忆分级请求体。</summary>
public sealed record MemoryImportanceHttpRequest(int Importance);

/// <summary>手动遗忘请求体：groupId 为空 = 全部群；retentionHours 为空 = 立即遗忘，否则保留最近 N 小时。</summary>
public sealed record MemoryForgetHttpRequest(string? GroupId, double? RetentionHours);

/// <summary>记忆 / 结论沉淀请求体（1.3）：把某群「关键」级记忆写入指定知识库。</summary>
public sealed record MemoryConsolidateHttpRequest(string? GroupId, string? KbId);
