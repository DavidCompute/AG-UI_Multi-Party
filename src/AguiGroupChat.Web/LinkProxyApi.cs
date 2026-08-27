using System.Net;
using System.Net.Sockets;
using AguiGroupChat.Hub.Models;
using AguiGroupChat.Hub.Options;
using AguiGroupChat.Hub.Users;

namespace AguiGroupChat.Web;

/// <summary>链接代理配置（appsettings 的 LinkProxy 节）。</summary>
public sealed class LinkProxyOptions
{
    /// <summary>单次代理最大响应字节数（超过则截断并追加提示）。</summary>
    public long MaxBytes { get; set; } = 8 * 1024 * 1024;

    /// <summary>目标请求超时秒数。</summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// 是否允许访问私网 / 环回地址。默认 false：仅代理公网地址（SSRF 收紧）。
    /// 外部 AG-UI 返回的内网链接（127.0.0.1 / 192.168.x.x）如确有代访需求，可在配置中显式开启。
    /// 关闭后：初始 URL 与每个重定向跳转均按「域名解析逐 IP」校验，命中环回 / 私网 / 链路本地即拒绝。
    /// </summary>
    public bool AllowPrivate { get; set; } = false;
}

/// <summary>
/// 链接代理 HTTP API：
///   GET /ag-ui/proxy?url=&lt;encoded&gt; —— Hub 代访目标链接并返回内容（需登录 token 或 demo 身份）
/// 前端把智能体回复 Markdown 中的 http/https 链接重写为本站代理地址：浏览器端无法直连的
/// 内网 / 混合内容地址由 Hub 侧统一访问。HTML 响应以 CSP sandbox 沙箱化，防脚本执行。
/// </summary>
public static class LinkProxyApi
{
    private static readonly HttpClient Http = CreateClient();

    /// <summary>是否允许私网访问（连接级校验与逐跳预校验共用同一口径；由端点按请求配置同步，进程级静态）。</summary>
    private static volatile bool _allowPrivate;

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            // 关闭出站 trace 传播（与 Program.cs 全局开关一致，避免目标网关因畸形 trace 头拒绝）
            ActivityHeadersPropagator = null,
            // 关闭自动重定向：重定向由调用方手动逐跳跟随并对每个跳转重新校验私网 / 环回（防 SSRF 经 302 绕过）
            AllowAutoRedirect = false,
            // SSRF 连接级防护（防 DNS rebinding TOCTOU）：
            // 上层 IsPrivateOrLoopback 预校验先解析一次域名，此处 ConnectCallback 在 TCP 建立阶段
            // 对实际连接目标**再次**解析并逐 IP 校验私网 / 环回，随后直连校验通过的 IP（固定连接）。
            // 即使预校验后 DNS 重绑定指向私网 / 环回地址，实际建立的连接仍只落在已通过校验的 IP 上，
            // 彻底消除「先解析校验、后连接时重解析」的时间窗口。Host 头 / SNI 仍按请求原始 host 发送。
            ConnectCallback = (ctx, ct) => ConnectAsync(ctx.DnsEndPoint, ct, validatePrivate: !_allowPrivate),
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("AguiGroupChat-LinkProxy/1.0");
        return client;
    }

    public static void MapLinkProxyApi(this WebApplication app)
    {
        var root = app.MapGroup("/ag-ui");

        root.MapGet("/proxy", async (HttpContext ctx, LinkProxyOptions options) =>
        {
            // 身份已由 RequireIdentityFilter 解析校验（本端点仅需已登录，不读取具体用户）
            _allowPrivate = options.AllowPrivate; // 同步连接级校验口径（进程内静态配置，运行期极少变更）

            var url = ctx.Request.Query["url"].ToString();
            if (string.IsNullOrWhiteSpace(url)
                || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                return Results.BadRequest(new AguiError(ErrorCodes.BadRequest, "仅支持 http/https 链接"));

            // 复用静态客户端超时兜底；请求级 CancellationTokenSource 保证按配置超时
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(1, options.TimeoutSeconds)));
            try
            {
                // 手动跟随重定向（最多 5 跳）：初始 URL 与每个跳转都重新校验私网 / 环回，防 SSRF 经 302 绕过
                var current = uri;
                HttpResponseMessage resp;
                var redirects = 0;
                while (true)
                {
                    if (!options.AllowPrivate && IsPrivateOrLoopback(current))
                        return Results.Text("出于安全考虑，拒绝访问本机 / 内网地址。", "text/plain; charset=utf-8");

                    resp = await Http.GetAsync(current, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                    if ((int)resp.StatusCode is 301 or 302 or 303 or 307 or 308)
                    {
                        var location = resp.Headers.Location;
                        resp.Dispose();
                        if (location is null)
                            return Results.Text("目标地址返回重定向但缺少 Location。", "text/plain; charset=utf-8", statusCode: (int)resp.StatusCode);
                        current = location.IsAbsoluteUri ? location : new Uri(current, location);
                        if (++redirects > 5)
                            return Results.Text("重定向次数过多，已停止。", "text/plain; charset=utf-8");
                        continue;
                    }
                    break;
                }

                using (resp)
                {
                    var contentLength = resp.Content.Headers.ContentLength;
                    if (contentLength is > 0 && contentLength > options.MaxBytes)
                        return Results.Text($"目标内容过大（超过 {options.MaxBytes / 1024 / 1024} MB），已拒绝。", "text/plain; charset=utf-8");

                    await using var raw = await resp.Content.ReadAsStreamAsync(cts.Token);
                    using var buffer = new MemoryStream();
                    var copied = 0L;
                    var truncated = false;
                    var chunk = new byte[16 * 1024];
                    while (copied < options.MaxBytes)
                    {
                        var n = await raw.ReadAsync(chunk, cts.Token);
                        if (n == 0) break;
                        var allowed = (int)Math.Min(n, options.MaxBytes - copied);
                        buffer.Write(chunk, 0, allowed);
                        copied += allowed;
                        if (allowed < n) { truncated = true; break; }
                    }

                    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
                    ctx.Response.Headers["Referrer-Policy"] = "no-referrer";

                    var contentType = resp.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
                    // 非 2xx：正文通常为错误页，统一转纯文本展示，避免渲染目标错误页
                    if ((int)resp.StatusCode is < 200 or >= 300)
                    {
                        var body = buffer.Length > 0
                            ? System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length)
                            : "";
                        return Results.Text($"目标地址返回 HTTP {(int)resp.StatusCode}：{body}",
                            "text/plain; charset=utf-8", statusCode: (int)resp.StatusCode);
                    }

                    // HTML 一律沙箱化：禁止脚本 / 表单 / 弹窗 / 同源访问 / 顶层导航，防代理页执行目标页面脚本
                    if (contentType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase))
                        ctx.Response.Headers["Content-Security-Policy"] = "sandbox; default-src 'none'; style-src 'unsafe-inline'; img-src * data:; font-src *";

                    ctx.Response.Headers.ContentType = contentType;
                    // 下载文件名：浏览器按代理地址最后一段取名「proxy」，需推导真实文件名并设置 Content-Disposition
                    // （优先目标响应头 → 目标 URL 路径段 → 按 content-type 兜底扩展名）；HTML 保持沙箱渲染不加头
                    SetContentDisposition(ctx.Response, current, resp, contentType);
                    await ctx.Response.Body.WriteAsync(buffer.GetBuffer().AsMemory(0, (int)buffer.Length), cts.Token);
                    // 截断提示仅追加到文本类内容；二进制直接截断（追加注释会进一步损坏文件）
                    if (truncated && IsTextLike(contentType))
                        await ctx.Response.Body.WriteAsync("\n<!-- 内容超过代理大小上限，已截断 -->"u8.ToArray(), cts.Token);
                    return Results.Empty;
                }
            }
            catch (OperationCanceledException)
            {
                return Results.Text("访问目标链接超时。", "text/plain; charset=utf-8", statusCode: StatusCodes.Status504GatewayTimeout);
            }
            catch (HttpRequestException)
            {
                return Results.Text("无法访问目标链接（连接失败或被拒绝）。", "text/plain; charset=utf-8", statusCode: StatusCodes.Status502BadGateway);
            }
            catch
            {
                return Results.Text("代理访问失败。", "text/plain; charset=utf-8", statusCode: StatusCodes.Status502BadGateway);
            }
        }).AddEndpointFilter(new WebIdentity.RequireIdentityFilter());
    }

    /// <summary>
    /// 设置 Content-Disposition 头：让浏览器下载 / 保存时使用正确文件名（而非代理地址的「proxy」）。
    /// 文件名优先级：目标响应 Content-Disposition → 目标 URL 路径最后段 → content-type 兜底扩展名。
    /// 中文等非 ASCII 文件名同时给出 filename*（RFC 5987）与 ASCII 回退 filename。
    /// HTML 不设（保持沙箱渲染）；图片 / 纯文本 / PDF 用 inline（可预览），其余强制 attachment（下载）。
    /// </summary>
    private static void SetContentDisposition(HttpResponse response, Uri uri, HttpResponseMessage resp, string contentType)
    {
        if (contentType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase)) return;

        var fileName = ResolveFileName(uri, resp, contentType);
        if (string.IsNullOrWhiteSpace(fileName)) return;

        var disposition = IsInlineType(contentType) ? "inline" : "attachment";
        response.Headers.ContentDisposition =
            $"{disposition}; filename=\"{ToAsciiFileName(fileName)}\"; filename*=UTF-8''{Uri.EscapeDataString(fileName)}";
    }

    private static string? ResolveFileName(Uri uri, HttpResponseMessage resp, string contentType)
    {
        // 1) 目标响应头自带文件名（下载型接口常用 Content-Disposition 告知真实文件名）
        var cd = resp.Content.Headers.ContentDisposition;
        if (cd is not null)
        {
            if (!string.IsNullOrWhiteSpace(cd.FileNameStar)) return cd.FileNameStar.Trim();
            if (!string.IsNullOrWhiteSpace(cd.FileName)) return cd.FileName.Trim('"');
        }
        // 2) 目标 URL 路径最后段（如 /files/abc123.pptx → abc123.pptx），要求含扩展名且长度合理
        try
        {
            var name = Uri.UnescapeDataString(uri.AbsolutePath).Split('/').LastOrDefault()?.Trim();
            if (!string.IsNullOrWhiteSpace(name) && name.Length <= 150
                && Path.HasExtension(name) && !name.StartsWith('.') && !name.EndsWith('.'))
                return name;
        }
        catch { /* 解码失败忽略 */ }
        // 3) 兜底：按 content-type 给通用名 + 扩展名
        return "download" + ExtensionFor(contentType);
    }

    /// <summary>ASCII 回退文件名：仅保留安全字符（浏览器保存时非法字符会替换，但 header 不允许裸引号 / 反斜杠 / 分号）。</summary>
    private static string ToAsciiFileName(string fileName)
    {
        var safe = new string(fileName.Where(c => c >= 32 && c < 127
            && c is not '\"' and not '\\' and not ';' and not '\r' and not '\n').ToArray());
        if (string.IsNullOrWhiteSpace(safe)) return "download";
        if (safe.StartsWith('.')) return "download" + safe; // 仅剩扩展名（如中文名被过滤）→ 补通用前缀
        return safe;
    }

    /// <summary>可直接内联预览的类型（浏览器渲染而非强制下载）：图片 / 纯文本 / PDF。</summary>
    private static bool IsInlineType(string contentType)
        => contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
            || contentType.StartsWith("text/plain", StringComparison.OrdinalIgnoreCase)
            || contentType.StartsWith("application/pdf", StringComparison.OrdinalIgnoreCase);

    /// <summary>截断提示只追加到文本类内容，避免破坏二进制文件。</summary>
    private static bool IsTextLike(string contentType)
        => contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
            || contentType.Contains("json", StringComparison.OrdinalIgnoreCase)
            || contentType.Contains("xml", StringComparison.OrdinalIgnoreCase)
            || contentType.Contains("javascript", StringComparison.OrdinalIgnoreCase);

    /// <summary>content-type → 兜底扩展名。</summary>
    private static string ExtensionFor(string contentType)
    {
        if (contentType.StartsWith("image/png", StringComparison.OrdinalIgnoreCase)) return ".png";
        if (contentType.StartsWith("image/jpeg", StringComparison.OrdinalIgnoreCase)) return ".jpg";
        if (contentType.StartsWith("image/gif", StringComparison.OrdinalIgnoreCase)) return ".gif";
        if (contentType.StartsWith("image/webp", StringComparison.OrdinalIgnoreCase)) return ".webp";
        if (contentType.StartsWith("image/svg", StringComparison.OrdinalIgnoreCase)) return ".svg";
        if (contentType.StartsWith("application/pdf", StringComparison.OrdinalIgnoreCase)) return ".pdf";
        if (contentType.StartsWith("text/plain", StringComparison.OrdinalIgnoreCase)) return ".txt";
        if (contentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase)) return ".json";
        if (contentType.StartsWith("application/zip", StringComparison.OrdinalIgnoreCase)) return ".zip";
        if (contentType.StartsWith("application/vnd.openxmlformats-officedocument.wordprocessingml", StringComparison.OrdinalIgnoreCase)) return ".docx";
        if (contentType.StartsWith("application/vnd.openxmlformats-officedocument.presentationml", StringComparison.OrdinalIgnoreCase)) return ".pptx";
        if (contentType.StartsWith("application/vnd.openxmlformats-officedocument.spreadsheetml", StringComparison.OrdinalIgnoreCase)) return ".xlsx";
        return ".bin";
    }

    /// <summary>SSRF 防护：host 为环回 / 私网 / 链路本地（IPv4 / IPv6）时拒绝；域名先解析再逐 IP 校验。</summary>
    private static bool IsPrivateOrLoopback(Uri uri)
    {
        var host = uri.Host;
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)) return true;
        List<IPAddress> addresses;
        try
        {
            if (IPAddress.TryParse(host, out var parsed)) addresses = [parsed];
            else addresses = [.. Dns.GetHostAddresses(host)];
        }
        catch
        {
            return true; // 解析失败一律拒绝
        }
        return addresses.Any(IsPrivateOrLoopbackIp);
    }

    /// <summary>
    /// 单 IP 私网 / 环回 / 链路本地判定（IPv4 / IPv6）。
    /// IPv4 映射的 IPv6 地址（::ffff:a.b.c.d）先还原为 IPv4：映射后落入 IPv4 校验分支，
    /// 避免映射地址绕过 IPv4 私网段校验（如 ::ffff:10.0.0.1）。
    /// </summary>
    private static bool IsPrivateOrLoopbackIp(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            if (ip.Equals(IPAddress.Any)) return true;
            if (b[0] == 10) return true;
            if (b[0] == 172 && b[1] is >= 16 and <= 31) return true;
            if (b[0] == 192 && b[1] == 168) return true;
            if (b[0] == 169 && b[1] == 254) return true;
            if (b[0] == 100 && b[1] is >= 64 and <= 127) return true;
        }
        else if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            var b = ip.GetAddressBytes();
            if (ip.Equals(IPAddress.IPv6Any)) return true;
            if (b[0] == 0xFE && (b[1] & 0xC0) == 0x80) return true;
            if ((b[0] & 0xFE) == 0xFC) return true;
        }
        return false;
    }

    /// <summary>连接阶段目标解析：校验目标 IP 非私网 / 环回后返回可直连的 IP 与端口（<paramref name="validatePrivate"/> 为 false 时不做私网拦截，供显式 AllowPrivate=true 使用）。
    /// 返回 (null, _, 原因) 表示拒绝（供 ConnectAsync 抛异常中断连接）。
    /// 域名全部解析 IP 逐项校验：任一命中私网 / 环回即拒绝；全部通过后取首个地址直连。</summary>
    private static (IPAddress? Ip, int Port, string? Error) ResolveTarget(EndPoint endpoint, bool validatePrivate)
    {
        // 目标为 IP 字面量：直接校验（IPv4 映射地址在 IsPrivateOrLoopbackIp 内先还原）
        if (endpoint is IPEndPoint ipEnd)
        {
            if (validatePrivate && IsPrivateOrLoopbackIp(ipEnd.Address))
                return (null, 0, "目标地址命中私网 / 环回");
            return (ipEnd.Address, ipEnd.Port, null);
        }
        if (endpoint is not DnsEndPoint dns)
            return (null, 0, "目标地址类型不受支持");

        var host = dns.Host;
        if (string.IsNullOrWhiteSpace(host))
            return (null, 0, "目标主机为空");
        if (validatePrivate && string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
            return (null, 0, "目标主机为 localhost");

        IPAddress[] addresses;
        try
        {
            if (IPAddress.TryParse(host, out var literal)) addresses = [literal];
            else addresses = Dns.GetHostAddresses(host);
        }
        catch
        {
            return (null, 0, "目标域名解析失败");
        }
        if (addresses.Length == 0)
            return (null, 0, "目标域名无可用解析记录");

        if (validatePrivate)
        {
            foreach (var raw in addresses)
            {
                var ip = raw.IsIPv4MappedToIPv6 ? raw.MapToIPv4() : raw;
                if (IsPrivateOrLoopbackIp(ip))
                    return (null, 0, "目标地址命中私网 / 环回");
            }
        }
        var first = addresses[0];
        return (first.IsIPv4MappedToIPv6 ? first.MapToIPv4() : first, dns.Port, null);
    }

    /// <summary>解析目标并建立 TCP 连接（连接固定到校验通过的 IP，Host 头 / SNI 仍由 SocketsHttpHandler 按原始 host 发送）。</summary>
    private static async ValueTask<Stream> ConnectAsync(EndPoint endpoint, CancellationToken ct, bool validatePrivate)
    {
        var (targetIp, targetPort, rejectReason) = ResolveTarget(endpoint, validatePrivate);
        if (targetIp is null)
            throw new HttpRequestException($"目标地址被安全策略拒绝（{rejectReason}）");
        var socket = new Socket(targetIp.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true, // 与 SocketsHttpHandler 默认一致：禁用 Nagle 算法降低代理延迟
        };
        try
        {
            await socket.ConnectAsync(new IPEndPoint(targetIp, targetPort), ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
