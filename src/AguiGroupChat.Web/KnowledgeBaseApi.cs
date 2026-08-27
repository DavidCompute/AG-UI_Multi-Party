using AguiGroupChat.Agents;
using AguiGroupChat.Hub.Infra;
using AguiGroupChat.Hub.Messaging;
using AguiGroupChat.Hub.Models;
using AguiGroupChat.Hub.Options;
using AguiGroupChat.Hub.Users;

namespace AguiGroupChat.Web;

/// <summary>
/// 知识库 HTTP API（Web 组合根扩展）：创建 / 列表 / 删除知识库，上传文档（复用附件上传的 attachmentId
/// 提取文本 + 切片向量化）。知识库供智能体绑定（AgentDefinition.KnowledgeBaseIds），回复前检索注入。
/// 权限：创建者可管理自己的知识库；系统级（OwnerId=null）知识库只读（不开放修改）。
/// </summary>
public static class KnowledgeBaseApi
{
    public static void MapKnowledgeBaseApi(this WebApplication app)
    {
        var root = app.MapGroup("/ag-ui/kb");

        // ---- 创建知识库（需登录）----
        root.MapPost("/", (KbCreateRequest req, HttpContext ctx, AuthService auth,
            KnowledgeBaseCatalog catalog, GroupHub hub) =>
        {
            var user = WebIdentity.User(ctx, auth);
            if (user is null) return AgentApi.Unauthorized();
            if (string.IsNullOrWhiteSpace(req.Name))
                return Results.BadRequest(new AguiError(ErrorCodes.BadRequest, "知识库名称不能为空"));
            // 群级共享（2.4）：校验目标群存在且调用者是成员（或管理员）
            if (req.SharedGroupIds is { Count: > 0 })
            {
                foreach (var g in req.SharedGroupIds)
                {
                    if (hub.Store.GetGroup(g) is null) return Results.NotFound(new AguiError(ErrorCodes.GroupNotFound, $"共享群不存在：{g}"));
                    if (!auth.IsAdmin(user.UserId) && !hub.Store.IsMember(g, user.UserId))
                        return Results.Json(new AguiError(ErrorCodes.GroupPermissionDenied, $"仅该群成员可把知识库共享到 {g}"),
                            statusCode: StatusCodes.Status403Forbidden);
                }
            }
            var kb = catalog.CreateKb(req.Name, req.Description ?? "", user.UserId);
            if (req.SharedGroupIds is { Count: > 0 }) kb.SharedGroupIds = req.SharedGroupIds.Distinct().ToList();
            return Results.Ok(new { kbId = kb.KbId, kb.Name, kb.Description, kb.OwnerId, kb.SharedGroupIds });
        }).AddEndpointFilter(new WebIdentity.RequireTokenFilter());

        // ---- 可见列表（系统级 + 自己创建的 + 群共享 + 管理员），含文档清单 ----
        root.MapGet("/", (HttpContext ctx, AuthService auth, KnowledgeBaseCatalog catalog, GroupHub hub) =>
        {
            var user = AgentApi.RequireUser(ctx, auth);
            var isAdmin = user is not null && auth.IsAdmin(user.UserId);
            var memberGroupIds = user is null
                ? new HashSet<string>(StringComparer.Ordinal)
                : hub.Store.GroupsOf(user.UserId).Select(g => g.GroupId).ToHashSet(StringComparer.Ordinal);
            var kbs = catalog.ListKbs(user?.UserId, memberGroupIds, isAdmin)
                .Select(k => new
                {
                    k.KbId,
                    k.Name,
                    k.Description,
                    k.OwnerId,
                    k.SharedGroupIds,
                    k.UpdatedAtMs,
                    canManage = user is not null && catalog.CanWrite(k, user.UserId, isAdmin),
                    Documents = k.Documents.Select(d => new { d.DocId, d.FileName, d.ChunkCount, d.Status, d.Error, d.AddedAtMs }),
                });
            return Results.Ok(kbs);
        });

        // ---- 删除知识库（仅创建者；删除后其向量一并清除，绑定它的智能体检索为空）----
        root.MapDelete("/{kbId}", (string kbId, HttpContext ctx, AuthService auth, KnowledgeBaseCatalog catalog) =>
        {
            var user = WebIdentity.User(ctx, auth);
            if (user is null) return AgentApi.Unauthorized();
            var kb = catalog.GetKb(kbId);
            if (kb is null) return Results.NotFound(new AguiError(ErrorCodes.AgentNotFound, "知识库不存在"));
            if (kb.OwnerId != user.UserId)
                return Results.Json(new AguiError(ErrorCodes.AgentPermissionDenied, "仅创建者可删除知识库"),
                    statusCode: StatusCodes.Status403Forbidden);
            catalog.RemoveKb(kbId);
            return Results.Ok(new { deleted = true, kbId });
        }).AddEndpointFilter(new WebIdentity.RequireTokenFilter());

        // ---- 添加文档（仅创建者；attachmentId 来自 POST /ag-ui/upload）----
        root.MapPost("/{kbId}/documents", async (string kbId, KbAddDocumentRequest req, HttpContext ctx, AuthService auth, KnowledgeBaseCatalog catalog, CancellationToken ct) =>
        {
            var user = WebIdentity.User(ctx, auth);
            if (user is null) return AgentApi.Unauthorized();
            var kb = catalog.GetKb(kbId);
            if (kb is null) return Results.NotFound(new AguiError(ErrorCodes.AgentNotFound, "知识库不存在"));
            if (kb.OwnerId != user.UserId)
                return Results.Json(new AguiError(ErrorCodes.AgentPermissionDenied, "仅创建者可管理知识库文档"),
                    statusCode: StatusCodes.Status403Forbidden);
            if (string.IsNullOrWhiteSpace(req.AttachmentId))
                return Results.BadRequest(new AguiError(ErrorCodes.BadRequest, "attachmentId 不能为空（先经 POST /ag-ui/upload 上传）"));
            // 立即返回“处理中”记录；提取文本 / 切片 / 向量化在后台执行，前端按 Status 轮询
            var (doc, error) = await catalog.AddDocumentAsync(kbId, req.AttachmentId, ct);
            if (doc is null)
                return Results.BadRequest(new AguiError(ErrorCodes.BadRequest, error ?? "文档添加失败"));
            return Results.Ok(new { added = true, kbId, docId = doc.DocId, doc.FileName, doc.ChunkCount, doc.Status, doc.Error });
        }).AddEndpointFilter(new WebIdentity.RequireTokenFilter());

        // ---- 移除文档（仅创建者）----
        root.MapDelete("/{kbId}/documents/{docId}", (string kbId, string docId, HttpContext ctx, AuthService auth, KnowledgeBaseCatalog catalog) =>
        {
            var user = WebIdentity.User(ctx, auth);
            if (user is null) return AgentApi.Unauthorized();
            var kb = catalog.GetKb(kbId);
            if (kb is null) return Results.NotFound(new AguiError(ErrorCodes.AgentNotFound, "知识库不存在"));
            if (kb.OwnerId != user.UserId)
                return Results.Json(new AguiError(ErrorCodes.AgentPermissionDenied, "仅创建者可管理知识库文档"),
                    statusCode: StatusCodes.Status403Forbidden);
            if (!catalog.RemoveDocument(kbId, docId))
                return Results.NotFound(new AguiError(ErrorCodes.AgentNotFound, "文档不存在"));
            return Results.Ok(new { removed = true, kbId, docId });
        }).AddEndpointFilter(new WebIdentity.RequireTokenFilter());
    }
}

/// <summary>创建知识库请求。</summary>
public sealed record KbCreateRequest(string Name, string? Description, IReadOnlyList<string>? SharedGroupIds = null);

/// <summary>添加文档请求：attachmentId 来自附件上传（POST /ag-ui/upload）。</summary>
public sealed record KbAddDocumentRequest(string AttachmentId);
