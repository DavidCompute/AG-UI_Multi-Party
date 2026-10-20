using AguiGroupChat.Agents;
using AguiGroupChat.Hub.Infra;
using AguiGroupChat.Hub.Messaging;
using AguiGroupChat.Hub.Models;
using AguiGroupChat.Hub.Storage;
using AguiGroupChat.Hub.Users;

namespace AguiGroupChat.Web;

/// <summary>
/// 图库 HTTP API：创建 / 列表 / 删除图库，上传图片（复用附件上传拿到的 attachmentId）。
///
/// <para>
/// 图库供<b>文档技能</b>按语义检索自动配图（PPT 的 <c>imageQuery</c> 优先查图库），
/// 从而让配图不依赖外网。权限与知识库完全一致：
/// 创建者可管理自己的图库、系统级（OwnerId=null）只读、群级共享只读、管理员全量。
/// </para>
///
/// <para>
/// <c>/ag-ui/images/search</c> 是给<b>技能</b>用的内部接口，见 <see cref="SelfApi"/>：
/// 它返回服务器上的文件路径，因此只对自令牌开放路径字段；普通登录用户调用只拿元数据。
/// </para>
/// </summary>
public static class ImageLibraryApi
{
    public static void MapImageLibraryApi(this WebApplication app)
    {
        var root = app.MapGroup("/ag-ui/image-libs");

        // ---- 创建图库（需登录）----
        root.MapPost("/", (ImageLibraryCreateRequest req, HttpContext ctx, AuthService auth,
            ImageLibraryCatalog catalog, GroupHub hub) =>
        {
            var user = WebIdentity.User(ctx, auth);
            if (user is null) return AgentApi.Unauthorized();
            if (string.IsNullOrWhiteSpace(req.Name))
                return Results.BadRequest(new AguiError(ErrorCodes.BadRequest, "图库名称不能为空"));
            if (req.SharedGroupIds is { Count: > 0 })
            {
                foreach (var g in req.SharedGroupIds)
                {
                    if (hub.Store.GetGroup(g) is null)
                        return Results.NotFound(new AguiError(ErrorCodes.GroupNotFound, $"共享群不存在：{g}"));
                    if (!auth.IsAdmin(user.UserId) && !hub.Store.IsMember(g, user.UserId))
                        return Results.Json(new AguiError(ErrorCodes.GroupPermissionDenied, $"仅该群成员可把图库共享到 {g}"),
                            statusCode: StatusCodes.Status403Forbidden);
                }
            }
            var lib = catalog.CreateLibrary(req.Name, req.Description ?? "", user.UserId);
            if (req.SharedGroupIds is { Count: > 0 }) lib.SharedGroupIds = req.SharedGroupIds.Distinct().ToList();
            if (req.MinScore is { } ms) catalog.SetStrictness(lib.LibId, ms);
            return Results.Ok(ToDto(lib));
        }).AddEndpointFilter(new WebIdentity.RequireTokenFilter());

        // ---- 更新图库设置（目前只有「检索严格度」）----
        //     为何要按库调：图库描述风格差别大 —— 短人名/标签型描述得分普遍偏高（门槛要收紧），
        //     长句型描述下标题式查询得分偏低（实测 0.62，门槛要放松）。一个全局值两头都不合适。
        root.MapPut("/{libId}", (string libId, ImageLibraryUpdateRequest req, HttpContext ctx, AuthService auth,
            ImageLibraryCatalog catalog) =>
        {
            var user = AgentApi.RequireUser(ctx, auth);
            if (user is null) return AgentApi.Unauthorized();
            var lib = catalog.GetLibrary(libId);
            if (lib is null) return Results.NotFound(new AguiError(ErrorCodes.ImageLibraryNotFound, $"图库不存在：{libId}"));
            if (!catalog.CanWrite(lib, user.UserId, auth.IsAdmin(user.UserId)))
                return Results.Json(new AguiError(ErrorCodes.ImageLibraryPermissionDenied, "只有创建者或管理员可修改图库设置"),
                    statusCode: StatusCodes.Status403Forbidden);
            var error = catalog.SetStrictness(libId, req.MinScore);
            return error is null ? Results.Ok(ToDto(lib)) : Results.BadRequest(new AguiError(ErrorCodes.BadRequest, error));
        }).AddEndpointFilter(new WebIdentity.RequireTokenFilter());

        // ---- 可见列表（系统级 + 自己创建的 + 群共享 + 管理员），含图片清单 ----
        root.MapGet("/", (HttpContext ctx, AuthService auth, ImageLibraryCatalog catalog, GroupHub hub) =>
        {
            var user = AgentApi.RequireUser(ctx, auth);
            var scope = Scope(ctx, auth, hub, user?.UserId);
            var libs = catalog.ListLibraries(user?.UserId, scope.MemberGroupIds, scope.IsAdmin)
                .Select(ToDto).ToList();
            return Results.Ok(new { libraries = libs });
        }).AddEndpointFilter(new WebIdentity.RequireTokenFilter());

        // ---- 删除图库（创建者 / 管理员；系统级只读）----
        root.MapDelete("/{libId}", (string libId, HttpContext ctx, AuthService auth,
            ImageLibraryCatalog catalog) =>
        {
            var user = AgentApi.RequireUser(ctx, auth);
            if (user is null) return AgentApi.Unauthorized();
            var lib = catalog.GetLibrary(libId);
            if (lib is null) return Results.NotFound(new AguiError(ErrorCodes.ImageLibraryNotFound, $"图库不存在：{libId}"));
            if (!catalog.CanWrite(lib, user.UserId, auth.IsAdmin(user.UserId)))
                return Results.Json(new AguiError(ErrorCodes.ImageLibraryPermissionDenied, "只有创建者或管理员可删除该图库"),
                    statusCode: StatusCodes.Status403Forbidden);
            catalog.RemoveLibrary(libId);
            return Results.Ok(new { ok = true });
        }).AddEndpointFilter(new WebIdentity.RequireTokenFilter());

        // ---- 上传图片（复用 /ag-ui/upload 拿到的 attachmentId，再登记进图库）----
        root.MapPost("/{libId}/assets", (string libId, ImageAssetAddRequest req, HttpContext ctx, AuthService auth,
            ImageLibraryCatalog catalog, AttachmentStore attachments) =>
        {
            var user = AgentApi.RequireUser(ctx, auth);
            if (user is null) return AgentApi.Unauthorized();
            var lib = catalog.GetLibrary(libId);
            if (lib is null) return Results.NotFound(new AguiError(ErrorCodes.ImageLibraryNotFound, $"图库不存在：{libId}"));
            if (!catalog.CanWrite(lib, user.UserId, auth.IsAdmin(user.UserId)))
                return Results.Json(new AguiError(ErrorCodes.ImageLibraryPermissionDenied, "只有创建者或管理员可上传图片"),
                    statusCode: StatusCodes.Status403Forbidden);
            if (string.IsNullOrWhiteSpace(req.AttachmentId))
                return Results.BadRequest(new AguiError(ErrorCodes.BadRequest, "attachmentId 不能为空"));

            var img = attachments.TryReadImageBytes(req.AttachmentId.Trim());
            if (img is null)
                return Results.BadRequest(new AguiError(ErrorCodes.BadRequest,
                    "附件不存在或不是图片（请先经 /ag-ui/upload 上传 png/jpg/jpeg/gif/bmp/webp）"));

            // 内容去重（1.0.278）：同一张图重复上传直接返回已有记录，不重复占向量 / 配额
            if (catalog.FindDuplicate(lib.LibId, img.Value.Bytes) is { } dupAsset)
                return Results.Ok(ToAssetDto(lib, dupAsset));

            var name = string.IsNullOrWhiteSpace(req.FileName) ? req.AttachmentId.Trim() + ".png" : req.FileName!;
            var (asset, error) = catalog.AddAsset(lib.LibId, name, img.Value.ContentType, img.Value.Bytes, user.UserId);
            if (asset is null) return Results.BadRequest(new AguiError(ErrorCodes.BadRequest, error ?? "上传失败"));
            if (!string.IsNullOrWhiteSpace(req.Caption)) asset.Caption = req.Caption!.Trim();
            if (req.Tags is { Count: > 0 }) asset.Tags = req.Tags.Where(t => !string.IsNullOrWhiteSpace(t)).Take(20).ToList();
            return Results.Ok(ToAssetDto(lib, asset));
        }).AddEndpointFilter(new WebIdentity.RequireTokenFilter());

        // ---- 改描述 / 标签（改了会重新向量化）----
        root.MapPut("/{libId}/assets/{assetId}", async (string libId, string assetId, ImageAssetUpdateRequest req,
            HttpContext ctx, AuthService auth, ImageLibraryCatalog catalog, CancellationToken ct) =>
        {
            var user = AgentApi.RequireUser(ctx, auth);
            if (user is null) return AgentApi.Unauthorized();
            var lib = catalog.GetLibrary(libId);
            if (lib is null) return Results.NotFound(new AguiError(ErrorCodes.ImageLibraryNotFound, $"图库不存在：{libId}"));
            if (!catalog.CanWrite(lib, user.UserId, auth.IsAdmin(user.UserId)))
                return Results.Json(new AguiError(ErrorCodes.ImageLibraryPermissionDenied, "只有创建者或管理员可修改图片"),
                    statusCode: StatusCodes.Status403Forbidden);
            var error = await catalog.UpdateAssetAsync(lib.LibId, assetId, req.Caption, req.Tags, ct);
            var asset = lib.Assets.FirstOrDefault(a => a.AssetId == assetId);
            if (asset is null) return Results.NotFound(new AguiError(ErrorCodes.ImageLibraryNotFound, $"图片不存在：{assetId}"));
            return error is null
                ? Results.Ok(ToAssetDto(lib, asset))
                : Results.BadRequest(new AguiError(ErrorCodes.BadRequest, error));
        }).AddEndpointFilter(new WebIdentity.RequireTokenFilter());

        // ---- 配图反馈（可纠正）：不相关 / 相关；检索时按「不相关 − 相关」降权（C）----
        root.MapPost("/{libId}/assets/{assetId}/feedback", (string libId, string assetId, ImageAssetFeedbackRequest req,
            HttpContext ctx, AuthService auth, ImageLibraryCatalog catalog) =>
        {
            var user = AgentApi.RequireUser(ctx, auth);
            if (user is null) return AgentApi.Unauthorized();
            var lib = catalog.GetLibrary(libId);
            if (lib is null) return Results.NotFound(new AguiError(ErrorCodes.ImageLibraryNotFound, $"图库不存在：{libId}"));
            if (!catalog.CanWrite(lib, user.UserId, auth.IsAdmin(user.UserId)))
                return Results.Json(new AguiError(ErrorCodes.ImageLibraryPermissionDenied, "只有创建者或管理员可标注配图反馈"),
                    statusCode: StatusCodes.Status403Forbidden);
            var error = catalog.RecordFeedback(lib.LibId, assetId, req.Relevant, req.Query);
            var asset = lib.Assets.FirstOrDefault(a => a.AssetId == assetId);
            if (asset is null) return Results.NotFound(new AguiError(ErrorCodes.ImageLibraryNotFound, $"图片不存在：{assetId}"));
            return error is null
                ? Results.Ok(ToAssetDto(lib, asset))
                : Results.BadRequest(new AguiError(ErrorCodes.BadRequest, error));
        }).AddEndpointFilter(new WebIdentity.RequireTokenFilter());

        // ---- 删除图片 ----
        root.MapDelete("/{libId}/assets/{assetId}", (string libId, string assetId, HttpContext ctx,
            AuthService auth, ImageLibraryCatalog catalog) =>
        {
            var user = AgentApi.RequireUser(ctx, auth);
            if (user is null) return AgentApi.Unauthorized();
            var lib = catalog.GetLibrary(libId);
            if (lib is null) return Results.NotFound(new AguiError(ErrorCodes.ImageLibraryNotFound, $"图库不存在：{libId}"));
            if (!catalog.CanWrite(lib, user.UserId, auth.IsAdmin(user.UserId)))
                return Results.Json(new AguiError(ErrorCodes.ImageLibraryPermissionDenied, "只有创建者或管理员可删除图片"),
                    statusCode: StatusCodes.Status403Forbidden);
            return catalog.RemoveAsset(lib.LibId, assetId)
                ? Results.Ok(new { ok = true })
                : Results.NotFound(new AguiError(ErrorCodes.ImageLibraryNotFound, $"图片不存在：{assetId}"));
        }).AddEndpointFilter(new WebIdentity.RequireTokenFilter());

        // ---- 原图读取（前端缩略图 / 预览；需登录且可读该图库）----
        root.MapGet("/{libId}/assets/{assetId}/raw", (string libId, string assetId, HttpContext ctx,
            AuthService auth, ImageLibraryCatalog catalog, GroupHub hub) =>
        {
            var user = AgentApi.RequireUser(ctx, auth);
            if (user is null) return AgentApi.Unauthorized();
            var lib = catalog.GetLibrary(libId);
            if (lib is null) return Results.NotFound(new AguiError(ErrorCodes.ImageLibraryNotFound, "图库不存在"));
            var scope = Scope(ctx, auth, hub, user.UserId);
            if (!catalog.CanRead(lib, user.UserId, scope.MemberGroupIds, scope.IsAdmin))
                return Results.Json(new AguiError(ErrorCodes.ImageLibraryPermissionDenied, "无权访问该图库"),
                    statusCode: StatusCodes.Status403Forbidden);
            var asset = lib.Assets.FirstOrDefault(a => a.AssetId == assetId);
            var path = asset is null ? null : catalog.ResolveAssetPath(lib, asset);
            if (path is null || asset is null) return Results.NotFound(new AguiError(ErrorCodes.ImageLibraryNotFound, "图片不存在"));
            return Results.File(path, asset.ContentType, asset.FileName, enableRangeProcessing: true);
        }).AddEndpointFilter(new WebIdentity.RequireTokenFilter());

        // ---- 语义检索（技能经自令牌调用；登录用户也可调，但拿不到服务器路径）----
        //     放在 /ag-ui/images/search，与图库 CRUD 分开：它是“内部能力”而不是资源管理。
        app.MapPost("/ag-ui/images/search", async (ImageSearchRequest req, HttpContext ctx, AuthService auth,
            ImageLibraryCatalog catalog, GroupHub hub, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Query))
                return Results.BadRequest(new AguiError(ErrorCodes.BadRequest, "query 不能为空"));

            var selfCall = SelfApi.IsSelf(ctx);
            var user = selfCall ? null : AgentApi.RequireUser(ctx, auth);
            if (!selfCall && user is null) return AgentApi.Unauthorized();

            var scope = Scope(ctx, auth, hub, user?.UserId);
            // 范围（**安全关键**）：技能回调只认平台登记的句柄，绝不信请求里自报的图库 ID ——
            // 技能入参是模型生成的，能自报就能越权读别人的图库。
            // 登录用户（前端调试）则限定在自己可读的图库里，同样忽略自报范围。
            IReadOnlyList<string> libIds;
            if (selfCall)
            {
                libIds = catalog.ResolveSearchScope(req.ScopeHandle) ?? [];
            }
            else
            {
                var readable = catalog.ListLibraries(user!.UserId, scope.MemberGroupIds, scope.IsAdmin)
                    .Select(l => l.LibId).ToHashSet(StringComparer.Ordinal);
                libIds = req.LibraryIds is { Count: > 0 }
                    ? req.LibraryIds.Where(readable.Contains).Distinct().ToList()
                    : readable.ToList();
            }

            var topK = Math.Clamp(req.TopK ?? 3, 1, 12);
            var minScore = req.MinScore is > 0 and < 1 ? req.MinScore!.Value : 0.25;
            var hits = await catalog.SearchAsync(libIds, req.Query.Trim(), topK, minScore, ct);

            return Results.Ok(new
            {
                query = req.Query.Trim(),
                libraryIds = libIds,
                count = hits.Count,
                images = hits.Select(h => new
                {
                    h.AssetId,
                    h.LibId,
                    h.LibName,
                    h.FileName,
                    h.Caption,
                    h.Tags,
                    h.MatchKind,
                    h.MismatchCount,
                    h.RelevantCount,
                    h.ContentType,
                    score = Math.Round(h.Score, 4),
                    h.Width,
                    h.Height,
                    // 服务器本地路径只给自令牌（技能要拿它直接嵌入文件）；登录用户走 /raw 下载
                    path = selfCall ? h.Path : null,
                    url = $"/ag-ui/image-libs/{h.LibId}/assets/{h.AssetId}/raw",
                }),
            });
        });
        // 注意：这个端点**不能**挂 RequireTokenFilter —— 它要接受两种身份：
        // ① 登录用户（前端调试）；② **技能的自令牌**（技能与平台同进程，没有用户会话）。
        // 过滤器只认用户令牌，会把技能请求在我方鉴权之前就 401 掉（实测踩过：注入生效、
        // 但技能一直静默回落网络）。鉴权在 handler 里自己做：IsSelf 或 RequireUser，两者都不过就 401。

        // ---- 技能写入（1.0.277）：自令牌 + 写能力句柄 ----
        // 安全模型：① 必须是技能自调用（AGUI_SELF_TOKEN）；② 必须持有平台注入的 imageWriteScopeId（10 分钟、
        // 绑定触发者 / 岗位 / 配额）；③ 只能写入**本轮新建**的图库；④ 每轮配额与审计。
        app.MapPost("/ag-ui/images/libraries", (ImageLibraryCreateBySkillRequest req, HttpContext ctx,
            ImageLibraryCatalog catalog, AuthService auth, AuditLogService audit) =>
        {
            if (!SelfApi.IsSelf(ctx))
                return Results.Json(new AguiError(ErrorCodes.ImageLibraryPermissionDenied, "仅技能自调用可写入图库"),
                    statusCode: StatusCodes.Status403Forbidden);
            var scope = catalog.ResolveWriteScope(req.WriteScopeId);
            if (scope is null)
                return Results.Json(new AguiError(ErrorCodes.ImageLibraryPermissionDenied, "写入能力句柄无效或已过期"),
                    statusCode: StatusCodes.Status403Forbidden);

            var name = (req.Name ?? "").Trim();
            if (name.Length == 0) return Results.BadRequest(new AguiError(ErrorCodes.BadRequest, "图库名称不能为空"));
            if (name.Length > 80) return Results.BadRequest(new AguiError(ErrorCodes.BadRequest, "图库名称过长（≤80）"));
            if (!catalog.TryConsumeLibraryCreate(scope))
                return Results.Json(new AguiError(ErrorCodes.BadRequest, $"本轮最多新建 {scope.MaxLibraries} 个图库"),
                    statusCode: StatusCodes.Status429TooManyRequests);

            var lib = catalog.CreateLibrary(name, req.Description ?? "", scope.OwnerUserId);
            catalog.AllowWriteLibrary(scope, lib.LibId); // 后续只能在这个库里传图
            audit.Record("skill.imageLibrary.create", scope.OwnerUserId, auth.GetUser(scope.OwnerUserId)?.Username,
                targetType: "imageLibrary", targetId: lib.LibId,
                detail: $"技能 {scope.SkillId ?? "-"}（岗位 {scope.AgentId ?? "-"}）新建图库「{lib.Name}」");
            return Results.Ok(new { libId = lib.LibId, name = lib.Name, ownerId = lib.OwnerId });
        });

        app.MapPost("/ag-ui/images/libraries/{libId}/assets", (string libId, ImageAssetAddBySkillRequest req, HttpContext ctx,
            ImageLibraryCatalog catalog, AttachmentStore attachments, AuthService auth, AuditLogService audit) =>
        {
            if (!SelfApi.IsSelf(ctx))
                return Results.Json(new AguiError(ErrorCodes.ImageLibraryPermissionDenied, "仅技能自调用可写入图库"),
                    statusCode: StatusCodes.Status403Forbidden);
            var scope = catalog.ResolveWriteScope(req.WriteScopeId);
            if (scope is null)
                return Results.Json(new AguiError(ErrorCodes.ImageLibraryPermissionDenied, "写入能力句柄无效或已过期"),
                    statusCode: StatusCodes.Status403Forbidden);
            if (!catalog.IsWriteLibraryAllowed(scope, libId))
                return Results.Json(new AguiError(ErrorCodes.ImageLibraryPermissionDenied, "该句柄无权写入此图库（只允许写入本轮新建或管理员授权的图库）"),
                    statusCode: StatusCodes.Status403Forbidden);
            var lib = catalog.GetLibrary(libId);
            if (lib is null) return Results.NotFound(new AguiError(ErrorCodes.ImageLibraryNotFound, $"图库不存在：{libId}"));

            byte[] bytes;
            string? contentType = null;
            var fileName = (req.FileName ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(req.AttachmentId))
            {
                var img = attachments.TryReadImageBytes(req.AttachmentId.Trim());
                if (img is null)
                    return Results.BadRequest(new AguiError(ErrorCodes.BadRequest, "附件不存在或不是图片"));
                bytes = img.Value.Bytes;
                contentType = img.Value.ContentType;
                if (fileName.Length == 0) fileName = req.AttachmentId.Trim() + ".png";
            }
            else if (!string.IsNullOrWhiteSpace(req.Base64))
            {
                try { bytes = Convert.FromBase64String(req.Base64); }
                catch { return Results.BadRequest(new AguiError(ErrorCodes.BadRequest, "base64 不是合法编码")); }
                if (fileName.Length == 0)
                    return Results.BadRequest(new AguiError(ErrorCodes.BadRequest, "base64 上传需提供 fileName（含扩展名）"));
            }
            else
            {
                return Results.BadRequest(new AguiError(ErrorCodes.BadRequest, "attachmentId 或 base64 至少提供一个"));
            }
            if (bytes.LongLength > scope.MaxImageBytes)
                return Results.BadRequest(new AguiError(ErrorCodes.BadRequest,
                    $"图片过大（本轮上限 {scope.MaxImageBytes / 1024 / 1024} MB）"));
            // 内容去重（1.0.278）：同图直接返回已有记录（不消耗配额）
            if (catalog.FindDuplicate(lib.LibId, bytes) is { } dup)
                return Results.Ok(new { duplicated = true, asset = ToAssetDto(lib, dup) });

            // 写「已有/授权库」且技能要求审批 → 入队，管理员批准后才落盘（1.0.279）；
            // 写本轮新建的库不受限制（隔离、风险低）。
            if (scope.RequiresApproval && !catalog.WasCreatedInRun(scope, lib.LibId))
            {
                if (!catalog.TryConsumeImageUpload(scope))
                    return Results.Json(new AguiError(ErrorCodes.BadRequest, $"本轮最多上传 {scope.MaxImages} 张图片"),
                        statusCode: StatusCodes.Status429TooManyRequests);
                var queue = ctx.RequestServices.GetService<ImageWriteApprovalQueue>();
                if (queue is null)
                    return Results.Json(new AguiError(ErrorCodes.BadRequest, "审批队列不可用（宿主未注册）"),
                        statusCode: StatusCodes.Status503ServiceUnavailable);
                var pending = queue.Add(scope.OwnerUserId, scope.SkillId, scope.AgentId, lib.LibId,
                    fileName, contentType, bytes, req.Caption, req.Tags, out var qerr);
                if (pending is null)
                    return Results.Json(new AguiError(ErrorCodes.BadRequest, qerr ?? "入队失败"),
                        statusCode: StatusCodes.Status429TooManyRequests);
                audit.Record("skill.imageLibrary.pending", scope.OwnerUserId, auth.GetUser(scope.OwnerUserId)?.Username,
                    targetType: "imageLibrary", targetId: lib.LibId,
                    detail: $"技能 {scope.SkillId ?? "-"}（岗位 {scope.AgentId ?? "-"}）请求写入 {fileName}，待审批 {pending.Id}");
                // 主动感知（1.0.281）：推一条 Webhook；不绑定知聚（groupId = ""），只有未限定知聚的订阅会收到
                ctx.RequestServices.GetService<WebhookDispatcher>()?.OnEvent("", new ImageWritePendingEvent
                {
                    PendingId = pending.Id,
                    OwnerUserId = scope.OwnerUserId,
                    SkillId = scope.SkillId,
                    AgentId = scope.AgentId,
                    LibId = lib.LibId,
                    LibName = lib.Name,
                    FileName = fileName,
                    Bytes = bytes.LongLength,
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                });
                return Results.Json(new { pendingId = pending.Id, status = "pending" }, statusCode: StatusCodes.Status202Accepted);
            }

            if (!catalog.TryConsumeImageUpload(scope))
                return Results.Json(new AguiError(ErrorCodes.BadRequest, $"本轮最多上传 {scope.MaxImages} 张图片"),
                    statusCode: StatusCodes.Status429TooManyRequests);

            var (asset, error) = catalog.AddAsset(lib.LibId, fileName, contentType ?? "", bytes, scope.OwnerUserId);
            if (asset is null) return Results.BadRequest(new AguiError(ErrorCodes.BadRequest, error ?? "上传失败"));
            // 写入来源标记（1.0.279）：图库界面可区分「技能/岗位写入」与「手动上传」
            asset.SourceSkillId = scope.SkillId;
            asset.SourceAgentId = scope.AgentId;
            if (!string.IsNullOrWhiteSpace(req.Caption)) asset.Caption = req.Caption!.Trim();
            if (req.Tags is { Count: > 0 }) asset.Tags = req.Tags.Where(t => !string.IsNullOrWhiteSpace(t)).Take(20).ToList();
            audit.Record("skill.imageLibrary.upload", scope.OwnerUserId, auth.GetUser(scope.OwnerUserId)?.Username,
                targetType: "imageLibrary", targetId: lib.LibId,
                detail: $"技能 {scope.SkillId ?? "-"}（岗位 {scope.AgentId ?? "-"}）上传图片 {fileName}（{bytes.LongLength / 1024} KB）");
            return Results.Ok(ToAssetDto(lib, asset));
        });

        // ---- 图库写入审批（1.0.279，管理员）：技能写已有/授权库时的待处理队列 ----
        var pendingGroup = app.MapGroup("/ag-ui/admin/image-write-pending");

        pendingGroup.MapGet("/", (HttpContext ctx, ImageLibraryCatalog catalog, AuthService auth) =>
        {
            var queue = ctx.RequestServices.GetService<ImageWriteApprovalQueue>();
            if (queue is null) return Results.Ok(new { items = Array.Empty<object>() });
            var items = queue.List().Select(x => new
            {
                x.Id,
                x.OwnerUserId,
                ownerName = auth.GetUser(x.OwnerUserId)?.Username,
                x.SkillId,
                x.AgentId,
                x.LibId,
                libName = catalog.GetLibrary(x.LibId)?.Name,
                x.FileName,
                bytes = x.Bytes.LongLength,
                x.Caption,
                x.Tags,
                x.AtMs,
            }).ToList();
            return Results.Ok(new { items });
        }).AddEndpointFilter(new WebIdentity.RequireAdminFilter());

        // ---- 最近处理（1.0.283 / 1.0.284 持久化）：批准 / 拒绝 / 失败的回执 ----
        //     让管理员在图库管理里就能看到「刚处理了什么」，不必去审计日志逐条翻。
        pendingGroup.MapGet("/history", (HttpContext ctx, int? limit) =>
        {
            var queue = ctx.RequestServices.GetService<ImageWriteApprovalQueue>();
            var take = Math.Clamp(limit ?? 20, 1, ImageWriteApprovalQueue.MaxHistory);
            return Results.Ok(new { items = queue?.RecentResolved(take) ?? Array.Empty<ResolvedImageWrite>() });
        }).AddEndpointFilter(new WebIdentity.RequireAdminFilter());

        // ---- 清空最近处理（1.0.286）：只清回执，不影响待审批队列 / 已落盘图片 ----
        pendingGroup.MapDelete("/history", (HttpContext ctx, AuthService auth, AuditLogService audit) =>
        {
            var queue = ctx.RequestServices.GetService<ImageWriteApprovalQueue>();
            if (queue is null) return Results.Ok(new { cleared = 0 });
            var cleared = queue.ClearHistory();
            var me = WebIdentity.UserId(ctx);
            audit.Record("skill.imageLibrary.historyClear", me ?? "", auth.GetUser(me ?? "")?.Username,
                targetType: "imageLibrary", targetId: "history", detail: $"清空最近处理回执{cleared} 条");
            return Results.Ok(new { cleared });
        }).AddEndpointFilter(new WebIdentity.RequireAdminFilter());

        pendingGroup.MapPost("/{id}/approve", (string id, HttpContext ctx, ImageLibraryCatalog catalog,
            AuthService auth, AuditLogService audit) =>
        {
            var queue = ctx.RequestServices.GetService<ImageWriteApprovalQueue>();
            if (queue is null) return Results.Problem("审批队列不可用", statusCode: 503);
            // 1.0.289：先查看、成功才移除——失败（库不在 / 落盘报错）时保留待审批项与字节，便于处理后重试
            var item = queue.Peek(id);
            if (item is null)
                return Results.NotFound(new AguiError(ErrorCodes.ImageLibraryNotFound, $"待审批项不存在或已处理：{id}"));
            var lib = catalog.GetLibrary(item.LibId);
            if (lib is null)
            {
                EmitWriteResolved(ctx, item, catalog, auth, "failed", reason: "library_missing");
                return Results.NotFound(new AguiError(ErrorCodes.ImageLibraryNotFound,
                    $"目标图库已不存在：{item.LibId}（该项仍待审批，可重建后重试或直接拒绝）"));
            }

            var dup = catalog.FindDuplicate(lib.LibId, item.Bytes);
            if (dup is not null)
            {
                queue.Remove(id);
                EmitWriteResolved(ctx, item, catalog, auth, "approved", duplicated: true, assetId: dup.AssetId);
                return Results.Ok(new { applied = true, duplicated = true, asset = ToAssetDto(lib, dup) });
            }

            var (asset, error) = catalog.AddAsset(lib.LibId, item.FileName, item.ContentType ?? "", item.Bytes, item.OwnerUserId);
            if (asset is null)
            {
                EmitWriteResolved(ctx, item, catalog, auth, "failed", reason: "write_failed");
                return Results.BadRequest(new AguiError(ErrorCodes.BadRequest,
                    (error ?? "落盘失败") + "（该项仍待审批，可稍后重试）"));
            }
            asset.SourceSkillId = item.SkillId;
            asset.SourceAgentId = item.AgentId;
            if (!string.IsNullOrWhiteSpace(item.Caption)) asset.Caption = item.Caption!.Trim();
            if (item.Tags is { Count: > 0 }) asset.Tags = item.Tags.Where(t => !string.IsNullOrWhiteSpace(t)).Take(20).ToList();
            queue.Remove(id);
            audit.Record("skill.imageLibrary.upload", item.OwnerUserId, auth.GetUser(item.OwnerUserId)?.Username,
                targetType: "imageLibrary", targetId: lib.LibId,
                detail: $"审批通过 {item.Id}：技能 {item.SkillId ?? "-"}（岗位 {item.AgentId ?? "-"}）写入 {item.FileName}");
            EmitWriteResolved(ctx, item, catalog, auth, "approved", assetId: asset.AssetId);
            return Results.Ok(new { applied = true, duplicated = false, asset = ToAssetDto(lib, asset) });
        }).AddEndpointFilter(new WebIdentity.RequireAdminFilter());

        pendingGroup.MapPost("/{id}/reject", (string id, HttpContext ctx, ImageLibraryCatalog catalog, AuthService auth, AuditLogService audit) =>
        {
            var queue = ctx.RequestServices.GetService<ImageWriteApprovalQueue>();
            if (queue is null) return Results.Problem("审批队列不可用", statusCode: 503);
            if (!queue.TryTake(id, out var item) || item is null)
                return Results.NotFound(new AguiError(ErrorCodes.ImageLibraryNotFound, $"待审批项不存在或已处理：{id}"));
            audit.Record("skill.imageLibrary.rejected", item.OwnerUserId, auth.GetUser(item.OwnerUserId)?.Username,
                targetType: "imageLibrary", targetId: item.LibId,
                detail: $"拒绝 {item.Id}：技能 {item.SkillId ?? "-"}（岗位 {item.AgentId ?? "-"}）写入 {item.FileName}");
            EmitWriteResolved(ctx, item, catalog, auth, "rejected");
            return Results.Ok(new { rejected = true });
        }).AddEndpointFilter(new WebIdentity.RequireAdminFilter());
    }

    /// <summary>
    /// 审批结果回执（1.0.282 / 1.0.283）：批准 / 拒绝 / 失败后记录一条「最近处理」（图库管理展示），
    /// 并推出站 Webhook，与入队时的 <see cref="ImageWritePendingEvent"/> 配对；不绑定知聚（groupId = ""）。
    /// 取不到调度器 / 队列（测试宿主 / 未注册）时静默跳过——审批本身不受影响。
    /// </summary>
    private static void EmitWriteResolved(HttpContext ctx, PendingImageWrite item, ImageLibraryCatalog catalog,
        AuthService auth, string status, bool duplicated = false, string? assetId = null, string? reason = null)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var libName = catalog.GetLibrary(item.LibId)?.Name;
        var me = WebIdentity.UserId(ctx);
        var decidedBy = auth.GetUser(me ?? "")?.Username ?? me;

        ctx.RequestServices.GetService<ImageWriteApprovalQueue>()?.RecordResolved(new ResolvedImageWrite
        {
            PendingId = item.Id,
            Status = status,
            Reason = reason,
            LibId = item.LibId,
            LibName = libName,
            FileName = item.FileName,
            SkillId = item.SkillId,
            AgentId = item.AgentId,
            AssetId = assetId,
            Duplicated = duplicated,
            DecidedBy = decidedBy,
            AtMs = now,
        });

        ctx.RequestServices.GetService<WebhookDispatcher>()?.OnEvent("", new ImageWriteResolvedEvent
        {
            PendingId = item.Id,
            OwnerUserId = item.OwnerUserId,
            SkillId = item.SkillId,
            AgentId = item.AgentId,
            LibId = item.LibId,
            LibName = libName,
            FileName = item.FileName,
            Bytes = item.Bytes.LongLength,
            Status = status,
            Duplicated = duplicated,
            AssetId = assetId,
            Reason = reason,
            Timestamp = now,
        });
    }

    /// <summary>当前调用者的可见范围（成员群 + 是否管理员）。</summary>
    private static (HashSet<string> MemberGroupIds, bool IsAdmin) Scope(HttpContext ctx, AuthService auth, GroupHub hub, string? userId)
    {
        var isAdmin = userId is not null && auth.IsAdmin(userId);
        var groups = userId is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : hub.Store.GroupsOf(userId).Select(g => g.GroupId).ToHashSet(StringComparer.Ordinal);
        return (groups, isAdmin);
    }

    private static object ToDto(ImageLibrary lib) => new
    {
        lib.LibId,
        lib.Name,
        lib.Description,
        lib.OwnerId,
        lib.SharedGroupIds,
        lib.UpdatedAtMs,
        // 检索严格度（null = 未设置，沿用调用方传的 minScore）；界面用它回显下拉
        lib.MinScore,
        assets = lib.Assets.Select(a => ToAssetDto(lib, a)).ToList(),
    };

    private static object ToAssetDto(ImageLibrary lib, ImageAsset a) => new
    {
        a.AssetId,
        a.FileName,
        a.ContentType,
        a.Width,
        a.Height,
        a.Bytes,
        a.Caption,
        a.Tags,
        a.MismatchCount,
        a.RelevantCount,
        a.Status,
        a.Error,
        a.UploadedAtMs,
        a.UploadedBy,
        a.SourceSkillId,
        a.SourceAgentId,
        url = $"/ag-ui/image-libs/{lib.LibId}/assets/{a.AssetId}/raw",
    };
}

/// <summary>配图反馈（可纠正）：relevant=true 记正反馈，false 记「不相关」（检索时按 query 维度降分）。
/// <paramref name="Query"/> 为触发检索的主题词（可空 = 对所有查询生效）。</summary>
public sealed record ImageAssetFeedbackRequest(bool Relevant, string? Query = null);

public sealed record ImageLibraryCreateRequest(string Name, string? Description = null, List<string>? SharedGroupIds = null,
    double? MinScore = null);

/// <summary>更新图库设置：<c>minScore</c> = 检索严格度（0.30~0.95；null = 恢复为“沿用调用方传的值”）。</summary>
public sealed record ImageLibraryUpdateRequest(double? MinScore = null);

public sealed record ImageAssetAddRequest(string AttachmentId, string? FileName = null, string? Caption = null,
    List<string>? Tags = null);

public sealed record ImageAssetUpdateRequest(string? Caption, List<string>? Tags = null);

public sealed record ImageSearchRequest(string Query, List<string>? LibraryIds = null, int? TopK = null,
    double? MinScore = null, string? ScopeHandle = null);

/// <summary>技能建库请求（1.0.277）：必须带平台注入的写能力句柄，仅自令牌可调。</summary>
public sealed record ImageLibraryCreateBySkillRequest(string WriteScopeId, string Name, string? Description = null);

/// <summary>技能传图请求（1.0.277）：<c>attachmentId</c> 或 <c>base64</c> 二选一；仅可写入本轮新建的图库。</summary>
public sealed record ImageAssetAddBySkillRequest(string WriteScopeId, string? AttachmentId = null,
    string? FileName = null, string? Base64 = null, string? Caption = null, List<string>? Tags = null);
