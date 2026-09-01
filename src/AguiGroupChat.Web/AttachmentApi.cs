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
///   GET  /ag-ui/files/{id}/{name}  —— 下载 / 预览附件
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

        // 附件下载 / 预览：按附件 ID 定位目录，文件名仅用于展示（下载时保留原名）。
        // 鉴权：需登录身份，且仅附件所在群（任一）的成员可访问；脚本 / 内联渲染类强制附件下载。
        root.MapGet("/files/{attachmentId}/{fileName}", async (string attachmentId, string fileName, HttpContext ctx, AttachmentStore store, IGroupStore groupStore, AuthService auth, AgentCatalog catalog) =>
        {
            var userId = WebIdentity.UserId(ctx)!; // 身份已由 RequireIdentityFilter 解析校验
            var path = store.ResolvePath(attachmentId);
            if (path is null)
                return Results.NotFound(new AguiError(ErrorCodes.GroupMessageNotFound, "附件不存在或已删除"));

            // 访问权校验：遍历该用户所在群的全部消息，附件命中消息必须「未撤回」且「当前用户可见」才放行——
            // 撤回消息的附件不得再被访问（防撤回后仍可下载敏感文件）；定向 / 私聊消息仅命中成员可见，
            // 防止非成员通过附件 ID 枚举越权下载。CanSeeMessage 由另一任务改为 public static（当前若仍为
            // internal 会在构建期报错，主代理会在统一构建前先完成该改动，此处直接调用不做反射绕路）。
            // 客服知聚：客服（Role != Normal）可见全部消息，因此附件也放行；非客服仍按消息可见性判定
            var allowed = groupStore.GroupsOf(userId)
                .Any(g =>
                {
                    var isSupportStaff = g.IsSupportCircle && groupStore.GetMember(g.GroupId, userId) is { Role: not GroupRole.Normal };
                    return groupStore.AllMessages(g.GroupId)
                        .Any(m => !m.Recalled
                            && m.Attachments.Any(a => a.AttachmentId == attachmentId)
                            && (isSupportStaff || GroupHub.CanSeeMessage(m, userId)));
                });
            // 头像附件放行：附件是任意用户 / 智能体（含分身）的头像或**群头像**时，已登录用户可访问——
            // 头像用于群成员 / 群列表 / 消息渲染，本身不含敏感信息；否则上传的头像因不属于任何群消息而被 403 拦截
            if (!allowed)
            {
                allowed = auth.ListUsers().Any(u => u.Avatar?.Contains(attachmentId, StringComparison.Ordinal) == true)
                    || catalog.ListDefinitions().Any(d => d.Avatar?.Contains(attachmentId, StringComparison.Ordinal) == true)
                    || groupStore.AllGroups().Any(g => g.GroupAvatar?.Contains(attachmentId, StringComparison.Ordinal) == true);
            }
            if (!allowed)
                return Results.Json(new AguiError(ErrorCodes.GroupPermissionDenied, "无权访问该附件"),
                    statusCode: StatusCodes.Status403Forbidden);

            ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";

            var contentType = GuessContentType(path);
            // 可执行 / 脚本 / 内联渲染类与压缩包等：强制附件下载，禁止浏览器内联渲染（防存储型 XSS）
            if (ForceDownload(path))
            {
                var downloadName = string.IsNullOrWhiteSpace(fileName) ? Path.GetFileName(path) : fileName;
                return Results.File(path, contentType, fileDownloadName: downloadName, enableRangeProcessing: true);
            }
            return Results.File(path, contentType, enableRangeProcessing: true);
        }).AddEndpointFilter(new WebIdentity.RequireIdentityFilter());
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
