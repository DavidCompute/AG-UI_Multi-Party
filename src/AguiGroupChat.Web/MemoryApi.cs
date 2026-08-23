using AguiGroupChat.Hub.Agents;
using AguiGroupChat.Hub.Messaging;
using AguiGroupChat.Hub.Models;
using AguiGroupChat.Hub.Options;
using AguiGroupChat.Hub.Storage;
using AguiGroupChat.Hub.Users;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;

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

        // ---- 记忆时间线（2.2）：按话题回放记忆的<b>时间演进</b>（旧→新），便于复盘「某主题结论如何演化」 ----
        root.MapGet("/timeline", (HttpContext ctx, AuthService auth, AuthOptions authOptions, IGroupStore store, IUserStore users, IMessageMemory memory,
            string? groupId, string? topicId, string? keyword, int limit = 200) =>
        {
            var (userId, error) = WebIdentity.ResolveIdentity(ctx, auth, authOptions);
            if (error is not null) return error;
            if (userId is null) return error!;
            // 只能回放自己所在群的记忆
            if (!string.IsNullOrWhiteSpace(groupId) && !store.IsMember(groupId, userId))
                return Results.Json(new AguiError(ErrorCodes.GroupPermissionDenied, "仅群成员可查看该群记忆"),
                    statusCode: StatusCodes.Status403Forbidden);

            var items = memory.ListMessages(groupId, null, keyword, Math.Clamp(limit, 1, 500), 0).ToList();
            if (!string.IsNullOrWhiteSpace(topicId)) items = items.Where(m => m.TopicId == topicId).ToList();
            // 时间演进：旧 → 新（ListMessages 返回新→旧，这里取反并按话题分组）
            var asc = items.OrderBy(m => m.Timestamp).ToList();
            return Results.Ok(new
            {
                groupId, topicId, keyword, count = asc.Count,
                topics = asc.GroupBy(m => m.TopicId).Select(g => new
                {
                    topicId = g.Key,
                    startMs = g.Min(m => m.Timestamp),
                    endMs = g.Max(m => m.Timestamp),
                    steps = g.Select(m => new
                    {
                        m.MessageId, m.Timestamp, m.SenderId, m.SenderType,
                        senderNickname = ResolveSenderName(store, users, m.GroupId, m.SenderId, m.SenderType),
                        m.Content, m.Importance,
                    }).ToList(),
                }).ToList(),
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
            AguiGroupChat.Agents.KnowledgeBaseCatalog kbs,
            CancellationToken ct) =>
        {
            // 语义记忆未启用（存储未注册）时该端点不可用，返回明确错误而非崩溃
            var memoryStore = ctx.RequestServices.GetService<AguiGroupChat.Hub.Persistence.IMessageMemoryStore>();
            if (memoryStore is null)
                return Results.BadRequest(new AguiError(ErrorCodes.BadRequest, "语义记忆（IMessageMemoryStore）未启用，无法沉淀群记忆"));
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

        // ---- 跨实例记忆同步（2.3）：导出「记忆即数据包」 / 增量导入（打通桌面 / Web 孤岛）----
        // 导出：groupId 为空 = 全部自己所在群；since 毫秒时间戳 = 仅导该时间之后的增量；limit / offset 分页。
        root.MapGet("/export", (HttpContext ctx, AuthService auth, AuthOptions authOptions, IGroupStore store, IMessageMemory memory)
            => ExportMemories(ctx, auth, authOptions, store, memory));

        // 导入：body 为导出产生的数组（或 {items:[...]}）；逐条向量化写入（按 messageId 去重）。
        root.MapPost("/import", async (JsonElement body, HttpContext ctx, AuthService auth, AuthOptions authOptions,
            IMessageMemory memory, CancellationToken ct) =>
        {
            var (_, error) = WebIdentity.ResolveIdentity(ctx, auth, authOptions);
            if (error is not null) return error;
            var items = body.ValueKind == JsonValueKind.Array ? body :
                (body.TryGetProperty("items", out var arr) ? arr : default);
            if (items.ValueKind != JsonValueKind.Array)
                return Results.BadRequest(new AguiError(ErrorCodes.BadRequest, "请求体需为记忆数组或 {items:[...]}"));
            var list = items.EnumerateArray().Select(ParseMemoryItem).Where(x => x is not null).Select(x => x!).ToList();
            if (list.Count == 0)
                return Results.BadRequest(new AguiError(ErrorCodes.BadRequest, "没有可导入的记忆条目"));
            var imported = await memory.ImportMemoriesAsync(list, ct);
            return Results.Ok(new { ok = true, imported, provided = list.Count });
        });
    }

    private static IResult ExportMemories(HttpContext ctx, AuthService auth, AuthOptions authOptions, IGroupStore store, IMessageMemory memory)
    {
        var (userId, error) = WebIdentity.ResolveIdentity(ctx, auth, authOptions);
        if (error is not null) return error;
        if (userId is null) return error!;

        var groupId = ctx.Request.Query["groupId"].ToString();
        var since = long.TryParse(ctx.Request.Query["since"].ToString(), out var sMs) ? sMs : 0;
        var limit = int.TryParse(ctx.Request.Query["limit"].ToString(), out var lo) ? Math.Clamp(lo, 1, 10_000) : 2000;
        var offset = int.TryParse(ctx.Request.Query["offset"].ToString(), out var of) ? Math.Max(0, of) : 0;

        // 校验：groupId 为空时仅可导自己所在群；指定群时须为该群成员（管理员可导任意群）
        if (!string.IsNullOrWhiteSpace(groupId))
        {
            if (store.GetGroup(groupId) is null)
                return Results.NotFound(new AguiError(ErrorCodes.GroupNotFound, "群不存在"));
            if (!store.IsMember(groupId, userId) && !auth.IsAdmin(userId))
                return Results.Json(new AguiError(ErrorCodes.GroupPermissionDenied, "仅群成员可见该群记忆"),
                    statusCode: StatusCodes.Status403Forbidden);
        }
        else if (!auth.IsAdmin(userId))
        {
            // 非管理员：只导出自己所在群（避免枚举他人群记忆）
            groupId = null; // 服务端 ExportMemories 传 null 会导出全部群 → 需按成员过滤
            var memberGroups = store.GroupsOf(userId).Select(g => g.GroupId).ToHashSet(StringComparer.Ordinal);
            // 逐群导出并合并（保持游标语义按各群时间线）+ 上限保护
            var collected = new List<MessageMemoryItem>();
            foreach (var gid in memberGroups)
            {
                const int page = 1000;
                int off = 0;
                while (true)
                {
                    var chunk = memory.ExportMemories(gid, since, page, off);
                    if (chunk.Count == 0) break;
                    collected.AddRange(chunk);
                    off += chunk.Count;
                    if (chunk.Count < page) break;
                }
            }
            collected = collected.OrderByDescending(m => m.Timestamp).Skip(offset).Take(limit).ToList();
            return Results.Ok(new { total = collected.Count, items = collected });
        }

        var items = memory.ExportMemories(groupId, since, limit, offset);
        return Results.Ok(new { total = memory.CountMemories(groupId, since), items });
    }

    /// <summary>解析一条导出记忆（字段缺失 / 非法则返回 null 跳过）。</summary>
    private static MessageMemoryItem? ParseMemoryItem(JsonElement e)
    {
        try
        {
            var msg = e.TryGetProperty("messageId", out var v) ? v.GetString() : null;
            var gid = e.TryGetProperty("groupId", out var vg) ? vg.GetString() : null;
            var content = e.TryGetProperty("content", out var vc) ? vc.GetString() : null;
            if (string.IsNullOrWhiteSpace(msg) || string.IsNullOrWhiteSpace(gid) || string.IsNullOrWhiteSpace(content)) return null;
            return new MessageMemoryItem(
                msg,
                gid,
                e.TryGetProperty("topicId", out var vt) ? vt.GetString() ?? "" : "",
                e.TryGetProperty("senderId", out var vs) ? vs.GetString() ?? "" : "",
                e.TryGetProperty("senderType", out var vst) ? vst.GetString() ?? "user" : "user",
                content,
                e.TryGetProperty("timestamp", out var t) && t.TryGetInt64(out var ts) ? ts : 0,
                e.TryGetProperty("importance", out var vi) && vi.TryGetInt32(out var imp) ? imp : MemoryImportance.Normal,
                e.TryGetProperty("expiresAt", out var ve) && ve.TryGetInt64(out var exp) && exp > 0 ? exp : null);
        }
        catch { return null; }
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
