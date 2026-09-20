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
        a.Status,
        a.Error,
        a.UploadedAtMs,
        a.UploadedBy,
        url = $"/ag-ui/image-libs/{lib.LibId}/assets/{a.AssetId}/raw",
    };
}

public sealed record ImageLibraryCreateRequest(string Name, string? Description = null, List<string>? SharedGroupIds = null,
    double? MinScore = null);

/// <summary>更新图库设置：<c>minScore</c> = 检索严格度（0.30~0.95；null = 恢复为“沿用调用方传的值”）。</summary>
public sealed record ImageLibraryUpdateRequest(double? MinScore = null);

public sealed record ImageAssetAddRequest(string AttachmentId, string? FileName = null, string? Caption = null,
    List<string>? Tags = null);

public sealed record ImageAssetUpdateRequest(string? Caption, List<string>? Tags = null);

public sealed record ImageSearchRequest(string Query, List<string>? LibraryIds = null, int? TopK = null,
    double? MinScore = null, string? ScopeHandle = null);
