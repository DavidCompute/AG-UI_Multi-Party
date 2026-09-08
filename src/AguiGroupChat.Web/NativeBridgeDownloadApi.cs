using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using AguiGroupChat.Hub.Users;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace AguiGroupChat.Web;

/// <summary>
/// 本机桥（NativeBridge）Windows 安装包下载 / 上传 API：
///   GET  /ag-ui/native-bridge/download/info        —— 安装包信息（登录用户；含 available / 版本 / 大小）
///   GET  /ag-ui/native-bridge/download/file        —— 下载安装包 zip（登录用户；<c>Content-Disposition: attachment</c>）
///   POST /ag-ui/native-bridge/download/upload      —— 上传/替换安装包 zip（仅系统管理员）
///   GET  /ag-ui/native-bridge/download/connection  —— 本机桥连接参数 / 启动命令（仅系统管理员；含全局隧道令牌）
/// 存储：目录默认 <c>data/nativebridge-download</c>（Docker 落在持久卷 /app/data 下；可经
/// <c>NativeBridgeDownload:Dir</c> 覆盖），同一时刻只保留一个 zip（新上传自动替换旧包）。
///
/// 安全边界：安装包 zip 内<b>不包含任何令牌</b>（启动脚本首次运行生成 bridge-config.txt 由用户自填）；
/// 网页只提供“平台地址 + 索取令牌”指引，连接令牌仅系统管理员可见（connection 端点），
/// 避免全站隧道密钥随包下发给任意/顾客用户。
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
    // 命名约束：发布脚本产出 AguiGroupChat-NativeBridge-<版本>-win-x64.zip（ASCII 文件名）
    private static readonly Regex SafeName = new(@"^[A-Za-z0-9._-]+$", RegexOptions.Compiled);
    private static readonly Regex VersionPattern = new(@"AguiGroupChat-NativeBridge-(.+?)(?:-win-x64)?\.zip$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    /// <summary>上传替换临界区互斥：防止两名管理员并发上传互相删包。</summary>
    private static readonly object UploadLock = new();
    /// <summary>zip 内待注入的连接配置文件（发布脚本内置占位，下载时被重写为当前服务器参数）。</summary>
    private const string ConfigEntry = "bridge-config.txt";
    /// <summary>重打包结果缓存：key=模板文件时间+服务器地址+令牌+是否含令牌，避免每次下载重复压缩大 zip。</summary>
    private static readonly Dictionary<string, byte[]> PackageCache = new();
    private static readonly object CacheLock = new();
    private const int PackageCacheMax = 6;

    /// <summary>允许下载者用 ?server= 覆盖包内平台地址（管理员经内网/本机访问时填对外地址）。</summary>
    private static string ResolveServer(HttpContext ctx)
    {
        var s = ctx.Request.Query["server"].ToString().Trim();
        if (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return s.TrimEnd('/');
        return $"{ctx.Request.Scheme}://{ctx.Request.Host}"; // 默认：当前访问地址
    }

    /// <summary>判断调用者是否管理员（经 RequireIdentity/Admin 过滤器后调用）。</summary>
    private static bool CallerIsAdmin(HttpContext ctx)
    {
        var auth = ctx.RequestServices.GetService<AuthService>();
        var id = WebIdentity.UserId(ctx);
        return auth is not null && id is not null && auth.IsAdmin(id);
    }

    /// <summary>令牌密文格式前缀（与 NativeBridge.BridgeTokenCipher 保持一致的 AES-256-GCM 布局：12B nonce + 16B tag + 密文，base64）。</summary>
    private const string TokenCipherPrefix = "enc:v1:";

    /// <summary>服务器侧加密令牌（随机 AES-256 密钥）：返回 enc:v1: 密文，密钥经 out 返回由调用方写入 bridge.key。</summary>
    private static string EncryptTokenForServer(string token, out string keyBase64)
    {
        var key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        keyBase64 = Convert.ToBase64String(key);
        var nonce = System.Security.Cryptography.RandomNumberGenerator.GetBytes(12);
        var plain = Encoding.UTF8.GetBytes(token);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using (var aes = new System.Security.Cryptography.AesGcm(key, 16))
        {
            aes.Encrypt(nonce, plain, cipher, tag);
        }
        var outBytes = new byte[12 + 16 + cipher.Length];
        nonce.CopyTo(outBytes, 0);
        tag.CopyTo(outBytes, 12);
        cipher.CopyTo(outBytes, 28);
        return TokenCipherPrefix + Convert.ToBase64String(outBytes);
    }

    /// <summary>把模板 zip 重打包：bridge-config.txt 条目写入当前服务器地址（SERVER）与令牌；
    /// 令牌<b>不以明文落盘</b>——管理员下载的包为 enc:v1: AES-GCM 密文 + 同目录 bridge.key（32B base64），
    /// 本机桥 --config 启动时自动解密；普通用户包令牌留空由管理员另行分发。支持 ?server= 覆盖地址。
    /// 令牌来源（管理员）：每次下载<b>新签发一枚“绑定型”令牌</b>（NativeBridgeIssuedTokenStore）——
    /// 首次连接时绑定到那台机器的 client，包被拷贝到别的机器会被拒绝；不再把全局 NATIVE_TUNNEL_TOKEN 放进包。</summary>
    private static byte[] BuildConfiguredPackage(HttpContext ctx, string templatePath, string fileName)
    {
        var origin = ResolveServer(ctx);
        var isAdmin = CallerIsAdmin(ctx);
        var templateWrite = File.GetLastWriteTimeUtc(templatePath).Ticks;
        var cacheKey = $"{fileName}|{templateWrite}|{origin}|{isAdmin}";
        // 管理员包每次签发新令牌 + 新随机密钥/密文，不做缓存；普通用户包内容确定可缓存。
        if (!isAdmin)
        {
            lock (CacheLock)
            {
                if (PackageCache.TryGetValue(cacheKey, out var hit)) return hit;
            }
        }

        // 令牌加密（仅管理员）：本次下载签发一枚绑定型令牌，随机 AES-256 密钥加密后写入 TOKEN，
        // 密钥以 bridge.key 随包交付。
        var tokenEnc = "";
        var keyB64 = "";
        if (isAdmin)
        {
            var issued = ctx.RequestServices.GetRequiredService<NativeBridgeIssuedTokenStore>();
            var userId = WebIdentity.UserId(ctx) ?? "?";
            var token = issued.Issue("安装包下载 by " + userId).Token;
            tokenEnc = EncryptTokenForServer(token, out keyB64);
        }

        var configText =
            "# AguiGroupChat NativeBridge connection config (auto-filled by the platform download).\n" +
            "# Lines starting with # are comments.\n" +
            "SERVER=" + origin + "\n" +
            "TOKEN=" + (isAdmin ? tokenEnc : "") + "\n" +
            "AGENT=\n" +
            "CLIENT=\n" +
            "LOCAL_PORT=17321\n";

        byte[] bytes;
        using (var srcStream = new FileStream(templatePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var src = new ZipArchive(srcStream, ZipArchiveMode.Read, leaveOpen: false))
        using (var outStream = new MemoryStream())
        {
            using (var dst = new ZipArchive(outStream, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var entry in src.Entries)
                {
                    var name = entry.FullName;
                    if (string.Equals(name, ConfigEntry, StringComparison.OrdinalIgnoreCase))
                        continue; // 跳过占位配置，下面统一注入
                    var copy = dst.CreateEntry(name);
                    using var eIn = entry.Open();
                    using var eOut = copy.Open();
                    eIn.CopyTo(eOut);
                }
                if (isAdmin && keyB64.Length > 0)
                {
                    var keyEntry = dst.CreateEntry("bridge.key");
                    using (var w = new StreamWriter(keyEntry.Open(), new UTF8Encoding(false)))
                    {
                        w.Write(keyB64 + "\n");
                    }
                }
                var cfg = dst.CreateEntry(ConfigEntry);
                using (var w = new StreamWriter(cfg.Open(), new UTF8Encoding(false)))
                {
                    w.Write(configText);
                }
            }
            bytes = outStream.ToArray();
        }

        if (!isAdmin)
        {
            lock (CacheLock)
            {
                if (PackageCache.Count >= PackageCacheMax) PackageCache.Clear(); // 简单上限：超出全清
                PackageCache[cacheKey] = bytes;
            }
        }
        return bytes;
    }

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
            });
        }).AddEndpointFilter(new WebIdentity.RequireIdentityFilter());

        // ---- 下载安装包（登录用户；?token= 供 <a> 直链，Content-Disposition 强制下载）----
        // 动态重打包：把 zip 内的 bridge-config.txt 重写为“当前平台地址 + 令牌”（管理员含令牌、
        // 普通用户令牌留空），支持 ?server= 覆盖服务器地址，供内网/本机访问的管理员填对外地址。
        root.MapGet("/file", (HttpContext ctx) =>
        {
            var pkg = FindPackage(ctx);
            if (pkg is null || pkg.FileName is null)
                return Results.NotFound(new { error = "本机桥安装包尚未上传，请联系系统管理员发布。" });
            var templatePath = Path.Combine(ResolveDir(ctx), pkg.FileName);
            if (!File.Exists(templatePath))
                return Results.NotFound(new { error = "本机桥安装包文件缺失，请联系系统管理员重新上传。" });
            var bytes = BuildConfiguredPackage(ctx, templatePath, pkg.FileName);
            return Results.File(bytes, "application/zip", fileDownloadName: pkg.FileName,
                lastModified: DateTimeOffset.FromUnixTimeMilliseconds(pkg.UploadedAtMs ?? 0), enableRangeProcessing: false);
        }).AddEndpointFilter(new WebIdentity.RequireIdentityFilter());

        // ---- 上传 / 替换安装包（仅管理员）：校验 zip 合法且内含 NativeBridge 主程序后替换旧包 ----
        root.MapPost("/upload", async (HttpContext ctx, ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("AguiGroupChat.Web.NativeBridgeDownload");
            var options = ctx.RequestServices.GetRequiredService<NativeBridgeDownloadOptions>();
            if (!ctx.Request.HasFormContentType || ctx.Request.Form.Files.Count == 0)
                return Results.BadRequest(new { error = "请选择要上传的本机桥安装包 zip（表单字段 file）" });

            var file = ctx.Request.Form.Files[0];
            var name = file.FileName.Trim().Replace('\\', '/');
            name = name[(name.LastIndexOf('/') + 1)..];
            if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || !SafeName.IsMatch(name))
                return Results.BadRequest(new { error = "文件名不合法：请使用 ASCII 文件名并以 .zip 结尾（如 AguiGroupChat-NativeBridge-1.0.120-win-x64.zip）" });
            if (file.Length <= 0 || file.Length > Math.Min(options.MaxZipBytes, 200L * 1024 * 1024))
                return Results.BadRequest(new { error = "文件大小超出允许范围（0 ~ 200MB）" });

            // 校验 zip 可打开且包含 NativeBridge 主程序（防误传任意 zip）
            var hasExe = false;
            try
            {
                await using var zipStream = file.OpenReadStream();
                using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);
                foreach (var entry in archive.Entries)
                {
                    if (string.Equals(Path.GetFileName(entry.FullName), "AguiGroupChat.NativeBridge.exe", StringComparison.OrdinalIgnoreCase))
                    { hasExe = true; break; }
                }
            }
            catch (InvalidDataException)
            {
                return Results.BadRequest(new { error = "上传的不是有效的 zip 文件" });
            }
            if (!hasExe)
                return Results.BadRequest(new { error = "zip 中未找到 AguiGroupChat.NativeBridge.exe，不是本机桥安装包" });

            // 写临时文件成功后原子替换；同时清理旧 zip（同一时刻只保留一个安装包）。
            // 进程内互斥只包住“删旧+换名”的临界区：大文件拷贝在锁外异步完成，避免阻塞其它请求。
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
                    foreach (var old in Directory.EnumerateFiles(dir, "*.zip"))
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

        // ---- 连接参数（仅管理员）：平台地址 + 全局隧道令牌，供复制到 bridge-config.txt / 命令行 ----
        root.MapGet("/connection", (HttpContext ctx) =>
        {
            var options = ctx.RequestServices.GetRequiredService<NativeTunnelOptions>();
            var origin = ResolveServer(ctx); // 支持 ?server= 覆盖（内网/本机访问时填对外地址）
            var tokenConfigured = !string.IsNullOrWhiteSpace(options.Token);
            // 注：逐 agent 专属令牌（AgentTokens）属于该数字员工的机密，不在网页回显；全局令牌足够管理员自建桥
            var command = tokenConfigured
                ? $"AguiGroupChat.NativeBridge.exe --tunnel \"{origin}\" --tunnel-token \"{options.Token}\" --local-port 17321"
                : $"AguiGroupChat.NativeBridge.exe --tunnel \"{origin}\" --tunnel-token \"<向管理员索取/在 .env 配置 NATIVE_TUNNEL_TOKEN>\" --local-port 17321";
            return Results.Ok(new
            {
                serverUrl = origin,
                tokenConfigured,
                token = tokenConfigured ? options.Token : null,
                command,
                configHint = "SERVER=" + origin + "\nTOKEN=" + (tokenConfigured ? options.Token : "<令牌>") + "\nAGENT=\nCLIENT=\nLOCAL_PORT=17321",
            });
        }).AddEndpointFilter(new WebIdentity.RequireAdminFilter());

        // ---- 已签发的绑定型安装包令牌（仅管理员）：列表 + 吊销 ----
        root.MapGet("/tokens", (HttpContext ctx) =>
        {
            var store = ctx.RequestServices.GetRequiredService<NativeBridgeIssuedTokenStore>();
            return Results.Ok(new
            {
                entries = store.List().Select(e => new
                {
                    id = e.Hash,          // 吊销用 id；只回显哈希，不回显明文令牌
                    client = e.ClientId,  // null = 尚未被任何机器绑定（包还没运行过）
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
        var zips = Directory.EnumerateFiles(dir, "*.zip").ToList();
        if (zips.Count == 0) return null;
        // 同名旧 zip 上传中残留不在候选（.uploading 后缀非 .zip）；存在多个时取最新
        var newest = zips
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

/// <summary>吊销安装包绑定型令牌的请求体（id = 令牌哈希，见 /tokens 列表）。</summary>
public sealed record TokenRevokeRequest(string? Id);
