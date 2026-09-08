using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace AguiGroupChat.Web;

/// <summary>
/// 本机桥（NativeBridge）Windows 安装包（MSI）下载 / 上传 API：
///   GET  /ag-ui/native-bridge/download/info        —— 安装包信息（登录用户；含 available / 版本 / 大小）
///   GET  /ag-ui/native-bridge/download/file        —— 下载安装包 .msi（登录用户；<c>Content-Disposition: attachment</c>）
///   POST /ag-ui/native-bridge/download/upload      —— 上传/替换安装包 .msi（仅系统管理员）
///   GET  /ag-ui/native-bridge/download/tokens      —— 已签发绑定型令牌列表（仅管理员）
///   POST /ag-ui/native-bridge/download/tokens/revoke —— 吊销绑定型令牌（仅管理员）
///   POST /ag-ui/native-bridge/download/setup-token —— 登录用户领取“在线配置”令牌（登出吊销）
///   POST /ag-ui/native-bridge/download/setup-token/revoke —— 登出吊销：使该用户全部 setup 令牌失效
/// 存储：目录默认 <c>data/nativebridge-download</c>（Docker 落在持久卷 /app/data 下；可经
/// <c>NativeBridgeDownload:Dir</c> 覆盖），同一时刻只保留一个 MSI（新上传自动替换旧包）。
///
/// 连接模型（登录即连、登出即断）：
///   MSI 为<b>通用安装包</b>（免装 .NET 运行时；安装即注册本机开机自启，桥以待配置模式启动）。
///   用户登录平台后，前端把本平台地址与一枚 setup 令牌（<c>note=setup:{{userId}}</c>，绑定型、
///   可吊销、每次领取先吊销旧令牌）经本机回环写到桥，桥据此连接；登出时吊销全部 setup 令牌并
///   让桥断开、清除本机配置。连接令牌不在下载包内（MSI 无法按下载者注入），避免全站隧道密钥
///   出现在网页 / 剪贴板 / 安装包。
/// </summary>
public sealed class NativeBridgeDownloadOptions
{
    /// <summary>安装包存放目录（相对内容根，默认 data/nativebridge-download）。</summary>
    public string Dir { get; set; } = "data/nativebridge-download";

    /// <summary>单包最大字节数（默认 500MB；Kestrel 上传体上限 200MB 实际先约束）。</summary>
    public long MaxZipBytes { get; set; } = 500L * 1024 * 1024;
}

/// <summary>安装包元信息（供 info 端点返回）。</summary>
public sealed record NativeBridgePackageInfo(
    bool Available, string? FileName, string? Version, long? SizeBytes, long? UploadedAtMs);

public static class NativeBridgeDownloadApi
{
    // 命名约束：发布脚本产出 AguiGroupChat-NativeBridge-<版本>-win-x64.msi（ASCII 文件名）
    private static readonly Regex SafeName = new(@"^[A-Za-z0-9._-]+$", RegexOptions.Compiled);
    private static readonly Regex VersionPattern = new(@"AguiGroupChat-NativeBridge-(.+?)(?:-win-x64)?\.msi$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    /// <summary>上传替换临界区互斥：防止两名管理员并发上传互相删包。</summary>
    private static readonly object UploadLock = new();

    /// <summary>MSI（Windows Installer 复合文档 OLE2）文件头魔数：用于上传校验，防误传任意文件。</summary>
    private static readonly byte[] MsiHeaderMagic = { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 };

    public static void MapNativeBridgeDownloadApi(this WebApplication app)
    {
        var root = app.MapGroup("/ag-ui/native-bridge/download");

        // ---- 安装包信息（登录用户）：前端资料弹窗据此显示「下载」按钮 / 版本 ----
        root.MapGet("/info", (HttpContext ctx) =>
        {
            var pkg = FindPackage(ctx);
            return Results.Ok(new
            {
                available = pkg is not null,
                fileName = pkg?.FileName,
                version = pkg?.Version,
                sizeBytes = pkg?.SizeBytes,
                uploadedAtMs = pkg?.UploadedAtMs,
                platform = "win-x64",
                selfContained = true, // 无需安装 .NET 运行时
                kind = "msi",
            });
        }).AddEndpointFilter(new WebIdentity.RequireIdentityFilter());

        // ---- 下载安装包（登录用户；?token= 供 <a> 直链，Content-Disposition 强制下载）----
        // 静态 MSI 原样下发：通用安装包内不含令牌；连接由“登录后网页在线下发配置”完成（见 setup-token）。
        root.MapGet("/file", (HttpContext ctx) =>
        {
            var pkg = FindPackage(ctx);
            if (pkg is null || pkg.FileName is null)
                return Results.NotFound(new { error = "本机桥安装包尚未上传，请联系系统管理员发布。" });
            var path = Path.Combine(ResolveDir(ctx), pkg.FileName);
            if (!File.Exists(path))
                return Results.NotFound(new { error = "本机桥安装包文件缺失，请联系系统管理员重新上传。" });
            return Results.File(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read),
                "application/octet-stream", fileDownloadName: pkg.FileName,
                lastModified: DateTimeOffset.FromUnixTimeMilliseconds(pkg.UploadedAtMs ?? 0), enableRangeProcessing: false);
        }).AddEndpointFilter(new WebIdentity.RequireIdentityFilter());

        // ---- 上传 / 替换安装包（仅管理员）：校验 MSI 文件头后替换旧包 ----
        root.MapPost("/upload", async (HttpContext ctx, ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("AguiGroupChat.Web.NativeBridgeDownload");
            var options = ctx.RequestServices.GetRequiredService<NativeBridgeDownloadOptions>();
            if (!ctx.Request.HasFormContentType || ctx.Request.Form.Files.Count == 0)
                return Results.BadRequest(new { error = "请选择要上传的本机桥安装包 .msi（表单字段 file）" });

            var file = ctx.Request.Form.Files[0];
            var name = file.FileName.Trim().Replace('\\', '/');
            name = name[(name.LastIndexOf('/') + 1)..];
            if (!name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase) || !SafeName.IsMatch(name))
                return Results.BadRequest(new { error = "文件名不合法：请使用 ASCII 文件名并以 .msi 结尾（如 AguiGroupChat-NativeBridge-1.0.120-win-x64.msi）" });
            if (file.Length <= 0 || file.Length > Math.Min(options.MaxZipBytes, 200L * 1024 * 1024))
                return Results.BadRequest(new { error = "文件大小超出允许范围（0 ~ 200MB）" });

            // 校验 MSI 文件头（OLE2 复合文档魔数 D0CF11E0…），防误传任意文件
            try
            {
                await using var stream = file.OpenReadStream();
                var head = new byte[MsiHeaderMagic.Length];
                var read = await stream.ReadAsync(head.AsMemory(0, head.Length), ct);
                if (read != head.Length || !head.SequenceEqual(MsiHeaderMagic))
                    return Results.BadRequest(new { error = "文件头不是有效的 Windows 安装包（.msi）" });
            }
            catch (IOException)
            {
                return Results.BadRequest(new { error = "读取上传文件失败，请重试" });
            }

            // 写临时文件成功后原子替换；同时清理旧包（同一时刻只保留一个安装包）。
            // 进程内互斥只包住“删旧+换名”的临界区：大文件拷贝在锁外完成，避免阻塞其它请求。
            var dir = ResolveDir(ctx);
            Directory.CreateDirectory(dir);
            var temp = Path.Combine(dir, name + ".uploading");
            try
            {
                await using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await file.CopyToAsync(fs, ct);
                }
                lock (UploadLock)
                {
                    foreach (var old in Directory.EnumerateFiles(dir, "*.msi"))
                    {
                        try { File.Delete(old); }
                        catch (IOException) { /* 正被下载流占用：跳过（FindPackage 按最后写入时间取最新，不影响） */ }
                    }
                    File.Move(temp, Path.Combine(dir, name), overwrite: true);
                }
            }
            finally
            {
                if (File.Exists(temp)) { try { File.Delete(temp); } catch { /* 忽略清理失败 */ } }
            }

            logger.LogInformation("本机桥安装包已更新：{Name}（{Bytes} 字节）", name, file.Length);
            var pkg = new NativeBridgePackageInfo(true, name, ParseVersion(name), file.Length, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            return Results.Ok(new { uploaded = true, available = true, fileName = pkg.FileName, version = pkg.Version, sizeBytes = pkg.SizeBytes });
        }).AddEndpointFilter(new WebIdentity.RequireAdminFilter());

        // ---- 已签发的绑定型令牌（仅管理员）：列表 + 吊销 ----
        root.MapGet("/tokens", (HttpContext ctx) =>
        {
            var store = ctx.RequestServices.GetRequiredService<NativeBridgeIssuedTokenStore>();
            return Results.Ok(new
            {
                entries = store.List().Select(e => new
                {
                    id = e.Hash,          // 吊销用 id；只回显哈希，不回显明文令牌
                    client = e.ClientId,  // null = 尚未被任何机器绑定（令牌尚未用于连接）
                    createdAtMs = e.CreatedAtMs,
                    lastUsedAtMs = e.LastUsedAtMs,
                    note = e.Note,
                    bound = e.ClientId is not null,
                }),
            });
        }).AddEndpointFilter(new WebIdentity.RequireAdminFilter());

        // ---- 吊销已签发令牌（仅管理员）：body { id = 令牌哈希 } ----
        root.MapPost("/tokens/revoke", (TokenRevokeRequest req, HttpContext ctx) =>
        {
            if (string.IsNullOrWhiteSpace(req.Id)) return Results.BadRequest(new { error = "缺少要吊销的令牌 id" });
            var store = ctx.RequestServices.GetRequiredService<NativeBridgeIssuedTokenStore>();
            var ok = store.Revoke(req.Id.Trim());
            return Results.Ok(new { revoked = ok });
        }).AddEndpointFilter(new WebIdentity.RequireAdminFilter());

        // ---- 登录用户领取“本机桥在线配置”setup 令牌（每次登录领一枚，登出吊销）：
        //      前端把 { server, setupToken } 写到本机桥回环 → 桥连入；服务器在桥首次连接时绑定其 client。
        //      令牌按用户记 note=setup:{userId}，登出时经 /setup-token/revoke 全部吊销。
        //      任何登录用户均可领取——这是客服知聚“顾客在自己电脑运行本机桥”的在线配置路径；
        //      令牌为绑定型（首次连接绑机器）+ 可吊销，不等同全站密钥。 ----
        root.MapPost("/setup-token", (HttpContext ctx) =>
        {
            var store = ctx.RequestServices.GetRequiredService<NativeBridgeIssuedTokenStore>();
            var userId = WebIdentity.UserId(ctx);
            if (userId is null) return Results.Json(new { error = "未登录" }, statusCode: StatusCodes.Status401Unauthorized);
            var note = "setup:" + userId;
            // 每次领取吊销该用户旧 setup 令牌，避免堆积 / 旧令牌仍可连
            store.RevokeAllForNote(note);
            var issued = store.Issue(note);
            return Results.Ok(new
            {
                server = $"{ctx.Request.Scheme}://{ctx.Request.Host}",
                setupToken = issued.Token,
                note,
            });
        }).AddEndpointFilter(new WebIdentity.RequireIdentityFilter());

        // ---- 登出吊销：使该用户领取的所有 setup 令牌失效（桥失去合法令牌，无法再连/重连） ----
        root.MapPost("/setup-token/revoke", (HttpContext ctx) =>
        {
            var store = ctx.RequestServices.GetRequiredService<NativeBridgeIssuedTokenStore>();
            var userId = WebIdentity.UserId(ctx);
            if (userId is null) return Results.Json(new { error = "未登录" }, statusCode: StatusCodes.Status401Unauthorized);
            var revoked = store.RevokeAllForNote("setup:" + userId);
            return Results.Ok(new { revoked });
        }).AddEndpointFilter(new WebIdentity.RequireIdentityFilter());
    }

    // ================= helpers =================

    private static string ResolveDir(HttpContext ctx)
    {
        var options = ctx.RequestServices.GetRequiredService<NativeBridgeDownloadOptions>();
        var env = ctx.RequestServices.GetRequiredService<IWebHostEnvironment>();
        var path = options.Dir;
        if (!Path.IsPathRooted(path))
            path = Path.Combine(env.ContentRootPath, path);
        return Path.GetFullPath(path);
    }

    private static NativeBridgePackageInfo? FindPackage(HttpContext ctx)
    {
        var dir = ResolveDir(ctx);
        if (!Directory.Exists(dir)) return null;
        var packages = Directory.EnumerateFiles(dir, "*.msi").ToList();
        if (packages.Count == 0) return null;
        // 同名旧包上传中残留不在候选（.uploading 后缀非 .msi）；存在多个时取最新
        var newest = packages
            .Select(p => new FileInfo(p))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .First();
        var fileName = newest.Name;
        return new NativeBridgePackageInfo(
            true, fileName, ParseVersion(fileName), newest.Length,
            new DateTimeOffset(newest.LastWriteTimeUtc).ToUnixTimeMilliseconds());
    }

    private static string? ParseVersion(string fileName)
    {
        var m = VersionPattern.Match(fileName);
        return m.Success ? m.Groups[1].Value : null;
    }
}

/// <summary>吊销绑定型令牌的请求体（id = 令牌哈希，见 /tokens 列表）。</summary>
public sealed record TokenRevokeRequest(string? Id);
