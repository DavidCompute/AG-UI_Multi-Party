using AguiGroupChat.Agents;
using AguiGroupChat.Hub.Messaging;
using AguiGroupChat.Hub.Models;
using AguiGroupChat.Hub.Options;
using AguiGroupChat.Hub.Storage;
using AguiGroupChat.Hub.Users;

namespace AguiGroupChat.Web;

/// <summary>
/// 附件 HTTP API（Web 组合根扩展）：
///   POST /ag-ui/upload             —— multipart 上传（需登录 token 或 demo 身份 memberId）
///   GET  /ag-ui/files/{id}/{name}  —— 下载 / 预览附件（按原格式返回）
///   GET  /ag-ui/preview/{id}       —— 办公文档在线查看（docx / xlsx / pptx → PDF 内联）
/// 上传返回附件元信息列表，前端随 GROUP_MESSAGE_SEND / POST message/send 携带。
/// </summary>
public static class AttachmentApi
{
    /// <summary>单次请求最多上传文件数。</summary>
    private const int MaxFilesPerRequest = 9;

    public static void MapAttachmentApi(this WebApplication app)
    {
        var root = app.MapGroup("/ag-ui");

        root.MapPost("/upload", async (HttpContext ctx, AttachmentStore store) =>
            {
                var userId = WebIdentity.UserId(ctx)!; // 身份已由 RequireIdentityFilter 解析校验

                if (!ctx.Request.HasFormContentType || !ctx.Request.Form.Files.Any())
                    return Results.BadRequest(new AguiError(ErrorCodes.BadRequest, "缺少上传文件（multipart/form-data 的 file 字段）"));

            var files = ctx.Request.Form.Files.Take(MaxFilesPerRequest).ToList();
            var attachments = new List<AttachmentInfo>(files.Count);
            foreach (var file in files)
            {
                if (file.Length <= 0)
                    continue;
                // 扩展名白名单：拒绝可执行 / 脚本 / 内联渲染类文件（防存储型 XSS）
                if (!AttachmentStore.IsAllowedUploadExtension(file.FileName))
                    return Results.BadRequest(new AguiError(ErrorCodes.BadRequest,
                        $"附件「{file.FileName}」是不支持的文件类型"));
                if (file.Length > AttachmentStore.MaxFileBytes)
                    return Results.BadRequest(new AguiError(ErrorCodes.BadRequest,
                        $"附件「{file.FileName}」超过大小上限（{AttachmentStore.MaxFileBytes / 1024 / 1024} MB）"));

                await using var stream = file.OpenReadStream();
                var info = store.Save(file.FileName, file.ContentType, stream, file.Length);
                attachments.Add(info);
            }

            if (attachments.Count == 0)
                return Results.BadRequest(new AguiError(ErrorCodes.BadRequest, "没有可保存的文件"));

            return Results.Ok(new { attachments });
        }).AddEndpointFilter(new WebIdentity.RequireIdentityFilter());

        // 附件下载：按附件 ID 定位目录，文件名仅用于展示（下载时保留原名）。
        // 鉴权：需登录身份，且仅附件所在群（任一）的成员可访问；脚本 / 内联渲染类强制附件下载。
        root.MapGet("/files/{attachmentId}/{fileName}", async (string attachmentId, string fileName, HttpContext ctx, AttachmentStore store, IGroupStore groupStore, AuthService auth, AgentCatalog catalog, AguiGroupChat.Hub.Messaging.GroupHub hub) =>
        {
            var (path, denied) = ResolveAndAuthorize(attachmentId, ctx, store, groupStore, auth, catalog, hub);
            if (denied is not null) return denied;

            ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";

            var contentType = GuessContentType(path!);
            // 可执行 / 脚本 / 内联渲染类与压缩包等：强制附件下载，禁止浏览器内联渲染（防存储型 XSS）
            if (ForceDownload(path!))
            {
                var downloadName = string.IsNullOrWhiteSpace(fileName) ? Path.GetFileName(path!) : fileName;
                return Results.File(path!, contentType, fileDownloadName: downloadName, enableRangeProcessing: true);
            }
            return Results.File(path!, contentType, enableRangeProcessing: true);
        }).AddEndpointFilter(new WebIdentity.RequireIdentityFilter());

        // 办公文档「在线查看」：docx / xlsx / pptx 由服务端转成 PDF 后**内联**返回，前端在弹窗 iframe 中渲染。
        // 为什么不复用 /files/{id}/{name}：那个端点按原格式返回（浏览器拿到 docx 只会下载 / 存盘），
        // 语义也不同——这里**只**返回可内联渲染的 PDF（含 .pdf 直通）。
        // 鉴权：与下载完全一致（同一段 CanAccessAttachment），否则就是一个绕过权限的读取口子。
        root.MapGet("/preview/{attachmentId}", async (string attachmentId, HttpContext ctx, AttachmentStore store,
            IGroupStore groupStore, AuthService auth, AgentCatalog catalog,
            AguiGroupChat.Hub.Messaging.GroupHub hub, IServiceProvider services) =>
        {
            var (path, denied) = ResolveAndAuthorize(attachmentId, ctx, store, groupStore, auth, catalog, hub);
            if (denied is not null) return denied;

            // 用 GetService 而不是构造注入：在线查看是**可选能力**（服务端可能没装 LibreOffice），
            // 而构造注入时只要某个组合根漏注册（如只 MapAttachmentApi 的精简宿主 / 测试夹具），
            // minimal API 会把该参数当**请求体**推断，于是整个应用的所有路由一起 500 ——
            // 那等于把可选能力写成了硬依赖（已踩：一次漏注册挂了 63 个无关用例）。
            var converter = services.GetService<IDocPreviewConverter>();
            if (converter is null)
                return Results.Json(new AguiError(ErrorCodes.DocumentPreviewFailed,
                        "服务端未启用文档在线查看（缺少预览转换服务注册），请下载后用本地应用打开"),
                    statusCode: StatusCodes.Status503ServiceUnavailable);

            var outcome = await converter.GetOrCreateAsync(attachmentId, path!, ctx.RequestAborted);
            if (!outcome.Ok)
                return Results.Json(new AguiError(
                        outcome.Failure == PreviewFailure.Unsupported ? ErrorCodes.BadRequest : ErrorCodes.DocumentPreviewFailed,
                        outcome.Message),
                    statusCode: PreviewStatusCode(outcome.Failure));

            ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
            // 转换产物对同一附件是稳定的：允许浏览器短时缓存，避免重复点开就重新下载整个 PDF
            ctx.Response.Headers["Cache-Control"] = "private, max-age=300";
            // 不设 Content-Disposition: attachment —— 让浏览器用内置 PDF 阅读器内联打开
            return Results.File(outcome.PdfPath!, "application/pdf", enableRangeProcessing: true);
        }).AddEndpointFilter(new WebIdentity.RequireIdentityFilter());
    }

    /// <summary>
    /// 附件“能不能拿”：解析路径 + 鉴权（404 / 403）。下载与在线查看**共用这一段**，
    /// 因此“两者权限一致”是结构上成立的，而不是靠两处手写保持一致。
    /// </summary>
    private static (string? Path, IResult? Denied) ResolveAndAuthorize(string attachmentId, HttpContext ctx,
        AttachmentStore store, IGroupStore groupStore, AuthService auth, AgentCatalog catalog,
        AguiGroupChat.Hub.Messaging.GroupHub hub)
    {
        var userId = WebIdentity.UserId(ctx)!; // 身份已由 RequireIdentityFilter 解析校验
        var path = store.ResolvePath(attachmentId);
        if (path is null)
            return (null, Results.NotFound(new AguiError(ErrorCodes.GroupMessageNotFound, "附件不存在或已删除")));

        // 试运行产物归属：可选服务（精简宿主可能没注册），取不到就退化为“不额外放行”
        var runArtifacts = ctx.RequestServices.GetService<SkillRunArtifactStore>();
        if (!CanAccessAttachment(attachmentId, userId, groupStore, auth, catalog, hub, runArtifacts))
            return (null, Results.Json(new AguiError(ErrorCodes.GroupPermissionDenied, "无权访问该附件"),
                statusCode: StatusCodes.Status403Forbidden));

        return (path, null);
    }

    /// <summary>预览失败的 HTTP 状态码：不可用是「服务端能力缺失」（503，运维去装 LibreOffice 即可恢复），
    /// 转换失败是「这份文档转不出来」（500）。</summary>
    private static int PreviewStatusCode(PreviewFailure failure) => failure switch
    {
        PreviewFailure.Unsupported => StatusCodes.Status400BadRequest,
        PreviewFailure.MissingSource => StatusCodes.Status404NotFound,
        PreviewFailure.Unavailable => StatusCodes.Status503ServiceUnavailable,
        _ => StatusCodes.Status500InternalServerError,
    };

    /// <summary>
    /// 附件访问权校验（下载与在线预览<b>共用</b>，避免两处走样后形成越权口子）：
    /// 遍历该用户所能访问的群（成员群 + 客服知聚参与者），附件命中消息必须「未撤回」且「当前用户可见」才放行——
    /// 撤回消息的附件不得再被访问（防撤回后仍可下载敏感文件）；定向 / 私聊消息仅命中成员可见。
    /// 客服知聚：客服（成员）可见全部消息附件；非成员顾客参与者仅可下载自己会话内可见的消息附件。
    /// 头像附件放行：附件是任意用户 / 智能体（含分身）的头像或**群头像**时，已登录用户可访问——
    /// 头像用于群成员 / 群列表 / 消息渲染，本身不含敏感信息；否则上传的头像因不属于任何群消息而被 403 拦截。
    /// 试运行产物放行：技能库试运行产出的稿子不属于任何消息，但**产出者本人**应当能看 / 下载（见 <see cref="SkillRunArtifactStore"/>）。
    /// </summary>
    private static bool CanAccessAttachment(string attachmentId, string userId, IGroupStore groupStore,
        AuthService auth, AgentCatalog catalog, AguiGroupChat.Hub.Messaging.GroupHub hub,
        SkillRunArtifactStore? runArtifacts = null)
    {
        if (runArtifacts?.IsOwnedBy(attachmentId, userId) == true) return true;
        var accessibleGroups = groupStore.GroupsOf(userId).ToList();
        // 补充尚未成为成员的客服知聚（顾客参与者），以便其下载自己会话内的附件
        foreach (var g in groupStore.AllGroups())
            if (g.IsSupportCircle && !accessibleGroups.Any(x => x.GroupId == g.GroupId)
                && hub.CanParticipate(g.GroupId, userId))
                accessibleGroups.Add(g);
        var allowed = accessibleGroups.Any(g =>
        {
            var isSupportStaff = g.IsSupportCircle && groupStore.GetMember(g.GroupId, userId) is { Role: not GroupRole.Normal };
            return groupStore.AllMessages(g.GroupId)
                .Any(m => !m.Recalled
                    && m.Attachments.Any(a => a.AttachmentId == attachmentId)
                    && (isSupportStaff || hub.CanSeeMessageAware(m, userId)));
        });
        if (allowed) return true;
        return auth.ListUsers().Any(u => u.Avatar?.Contains(attachmentId, StringComparison.Ordinal) == true)
            || catalog.ListDefinitions().Any(d => d.Avatar?.Contains(attachmentId, StringComparison.Ordinal) == true)
            || groupStore.AllGroups().Any(g => g.GroupAvatar?.Contains(attachmentId, StringComparison.Ordinal) == true);
    }

    private static string GuessContentType(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            ".pdf" => "application/pdf",
            ".json" => "application/json",
            // 音频（语音消息 5.2）：返回正确 MIME 供 <audio> 内联流式播放（未列入 ForceDownload，安全）
            ".mp3" => "audio/mpeg",
            ".wav" => "audio/wav",
            ".ogg" or ".oga" => "audio/ogg",
            ".m4a" => "audio/mp4",
            ".aac" => "audio/aac",
            ".flac" => "audio/flac",
            ".opus" => "audio/opus",
            // MediaRecorder 语音录音默认产物（webm/opus）；无视频轨时浏览器按音频渲染
            ".webm" => "audio/webm",
            // 允许上传的文本类附件保持内联预览（text/plain 无脚本执行风险）
            ".txt" or ".md" or ".markdown" or ".log" or ".csv" or ".tsv"
                or ".yaml" or ".yml" or ".toml" or ".ini" or ".cfg" or ".conf" or ".properties" or ".env"
                => "text/plain; charset=utf-8",
            // 可执行 / 脚本 / 内联渲染类：一律按二进制处理（配合 ForceDownload 强制附件下载）
            ".svg" or ".js" or ".mjs" or ".css" or ".html" or ".htm" or ".xml" or ".zip"
                => "application/octet-stream",
            _ => "application/octet-stream",
        };
    }

    /// <summary>是否需要强制附件下载（而非内联渲染）：可执行 / 脚本 / 内联渲染类与压缩包等。</summary>
    private static bool ForceDownload(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".svg" or ".js" or ".mjs" or ".css" or ".html" or ".htm" or ".xml" or ".zip";
    }
}
