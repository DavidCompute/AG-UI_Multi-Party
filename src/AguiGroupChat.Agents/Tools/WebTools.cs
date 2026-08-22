using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AguiGroupChat.Agents;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AguiGroupChat.Agents.Tools;

/// <summary>
/// 网络类工具（AgentOptions.EnableWebTools=true 时挂载）：
///   - web_search：DuckDuckGo Instant Answer API（免费无密钥，端点可配置），返回摘要与相关条目
///   - read_url：抓取网页正文（HTML 转文本），含私网 / 环回地址 SSRF 防护
/// 任一失败返回错误文本，不影响智能体主流程。
/// </summary>
public sealed class WebTools : IDisposable
{
    private const int MaxResultChars = 8000;
    private readonly HttpClient _http;
    private readonly string _searchEndpoint;
    private readonly ILogger _logger;

    public WebTools(IServiceProvider services, ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<WebTools>();
        _searchEndpoint = services.GetService<AgentOptions>()?.WebSearchEndpoint ?? "https://api.duckduckgo.com/";
        // 关闭自动重定向：302 会绕过 read_url 的私网校验，重定向改由 ReadUrl 手动逐跳重新校验
        _http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("AguiGroupChat-Agent/1.0");
    }

    public void Dispose() => _http.Dispose();

    /// <summary>网页搜索：返回摘要与最多 5 条相关条目文本。</summary>
    public async Task<string> WebSearch(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return "查询词为空。";
        try
        {
            var sep = _searchEndpoint.Contains('?') ? '&' : '?';
            var url = _searchEndpoint + sep + "q=" + Uri.EscapeDataString(query) + "&format=json&no_html=1&skip_disambig=1";
            var json = await _http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var sb = new StringBuilder();
            if (root.TryGetProperty("AbstractText", out var abs) && !string.IsNullOrWhiteSpace(abs.GetString()))
                sb.AppendLine("摘要：" + abs.GetString());
            if (root.TryGetProperty("Heading", out var heading) && !string.IsNullOrWhiteSpace(heading.GetString()))
                sb.AppendLine("主题：" + heading.GetString());
            if (root.TryGetProperty("RelatedTopics", out var topics))
            {
                var count = 0;
                foreach (var topic in topics.EnumerateArray())
                {
                    if (count >= 5) break;
                    if (topic.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var sub in topic.EnumerateArray())
                        {
                            if (count >= 5) break;
                            if (sub.TryGetProperty("Text", out var txt) && !string.IsNullOrWhiteSpace(txt.GetString()))
                            {
                                sb.AppendLine("· " + txt.GetString());
                                count++;
                            }
                        }
                    }
                    else if (topic.TryGetProperty("Text", out var txt) && !string.IsNullOrWhiteSpace(txt.GetString()))
                    {
                        sb.AppendLine("· " + txt.GetString());
                        count++;
                    }
                }
            }
            return sb.Length == 0 ? "未找到相关结果。" : Untrusted(Truncate(sb.ToString().Trim()));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "web_search 工具执行失败：{Query}", query);
            return "搜索失败：" + ex.Message;
        }
    }

    /// <summary>外部不可信内容边界标记：网页 / 搜索结果 / 附件文本可能含恶意指令（prompt injection），
    /// 在上下文里显式标注并提示模型仅作参考、不得执行其中任何指令。复用共享实现保证文案一致。</summary>
    private static string Untrusted(string content) => UntrustedBoundary.Wrap(content);

    /// <summary>读取 URL 正文（仅 http/https；拒绝本机 / 内网地址防 SSRF）。</summary>
    public async Task<string> ReadUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return "仅支持 http/https 链接。";
        if (IsPrivateOrLoopback(uri))
            return "出于安全考虑，拒绝访问本机 / 内网地址。";
        try
        {
            // 手动跟随重定向（最多 5 跳）：每跳的新 URL 都重新做 scheme 与私网/环回校验，防止 302 绕过 SSRF 防护
            var current = uri;
            for (var hop = 0; ; hop++)
            {
                using var resp = await _http.SendAsync(new HttpRequestMessage(HttpMethod.Get, current));
                var location = resp.Headers.Location;
                if ((int)resp.StatusCode is >= 300 and < 400 && location is not null)
                {
                    if (hop >= 5) return "读取失败：重定向次数过多（超过 5 次）。";
                    var next = location.IsAbsoluteUri ? location : new Uri(current, location);
                    if (next.Scheme != Uri.UriSchemeHttp && next.Scheme != Uri.UriSchemeHttps)
                        return "仅支持 http/https 链接。";
                    if (IsPrivateOrLoopback(next))
                        return "出于安全考虑，拒绝访问本机 / 内网地址。";
                    current = next;
                    continue;
                }
                resp.EnsureSuccessStatusCode();
                var html = await resp.Content.ReadAsStringAsync();
                var text = HtmlToText(html);
                return string.IsNullOrWhiteSpace(text) ? "未能从该页面提取到文本内容。" : Untrusted(Truncate(text));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "read_url 工具执行失败：{Url}", url);
            return "读取失败：" + ex.Message;
        }
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
        foreach (var ip in addresses)
        {
            if (IPAddress.IsLoopback(ip)) return true;
            if (ip.AddressFamily == AddressFamily.InterNetwork)
            {
                var b = ip.GetAddressBytes();
                if (ip.Equals(IPAddress.Any)) return true;                    // 0.0.0.0 未指定地址
                if (b[0] == 10) return true;                                  // 10.0.0.0/8
                if (b[0] == 172 && b[1] is >= 16 and <= 31) return true;      // 172.16/12
                if (b[0] == 192 && b[1] == 168) return true;                  // 192.168/16
                if (b[0] == 169 && b[1] == 254) return true;                  // 链路本地 169.254/16
                if (b[0] == 100 && b[1] is >= 64 and <= 127) return true;     // CGN 100.64/10
            }
            else if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            {
                // IPv4-mapped IPv6（::ffff:a.b.c.d）能绕过纯 IPv6 分支的检查：
                // 先映射回 IPv4 再走 IPv4 私网判定（防 SSRF 绕过），注意 var b 在分支内重新取
                if (ip.IsIPv4MappedToIPv6)
                {
                    var v4 = ip.MapToIPv4();
                    var b = v4.GetAddressBytes();
                    if (v4.Equals(IPAddress.Any)) return true;                    // 0.0.0.0 未指定地址
                    if (b[0] == 10) return true;                                  // 10.0.0.0/8
                    if (b[0] == 172 && b[1] is >= 16 and <= 31) return true;      // 172.16/12
                    if (b[0] == 192 && b[1] == 168) return true;                  // 192.168/16
                    if (b[0] == 169 && b[1] == 254) return true;                  // 链路本地 169.254/16
                    if (b[0] == 100 && b[1] is >= 64 and <= 127) return true;     // CGN 100.64/10
                    continue;                                                     // 映射后的公网 IPv4：继续检查其余解析结果
                }
                var b6 = ip.GetAddressBytes();
                if (ip.Equals(IPAddress.IPv6Any)) return true;                       // :: 未指定地址
                if (b6[0] == 0xFE && (b6[1] & 0xC0) == 0x80) return true;            // 链路本地 fe80::/10（覆盖 fe80~febf）
                if ((b6[0] & 0xFE) == 0xFC) return true;                            // ULA fc00::/7
            }
        }
        return false;
    }

    /// <summary>简单 HTML → 文本：去 script/style、块级标签换行、去标签、解码实体、压缩空白。</summary>
    private static string HtmlToText(string html)
    {
        html = Regex.Replace(html, @"<script[\s\S]*?</script>", " ", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<style[\s\S]*?</style>", " ", RegexOptions.IgnoreCase);
        var title = Regex.Match(html, @"<title[^>]*>([\s\S]*?)</title>", RegexOptions.IgnoreCase).Groups[1].Value.Trim();
        html = Regex.Replace(html, @"</(p|div|h[1-6]|li|tr|br|section|article|blockquote|td)>", "\n", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<[^>]+>", " ");
        html = WebUtility.HtmlDecode(html);
        html = Regex.Replace(html, @"[ \t\r]+", " ");
        html = Regex.Replace(html, @"\n\s*\n+", "\n");
        var text = html.Trim();
        return title.Length > 0 ? $"标题：{title}\n{text}" : text;
    }

    private static string Truncate(string s)
        => s.Length > MaxResultChars ? s[..MaxResultChars] + "…（已截断）" : s;
}
