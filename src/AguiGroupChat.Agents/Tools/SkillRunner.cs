using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace AguiGroupChat.Agents.Tools;

/// <summary>
/// OpenClaw 式技能执行器：按 <see cref="AgentSkillDefinition.Kind"/> 执行一个可复用技能。
///   - Shell：在技能专属沙箱目录里执行脚本 / 命令（多行脚本支持；可用 ${query} 引用调用方传入的参数）。
///   - Http：调用外部 HTTP(S) 接口（SSRF 防护 + 手动逐跳重定向校验；${query} / ${params} 可做占位）。
///   - Prompt：无可执行代码；返回「模板 + 参数」给模型自行推理 / 聚合。
/// 安全默认：执行一律假定由外层 <see cref="ApprovalRequiredAIFunction"/> 审批包装（shell/http 强制）。
/// </summary>
internal sealed class SkillRunner
{
    private const int MaxOutputChars = 12_000;      // 单次技能返回最大输出（防撑爆模型上下文）
    private const int DefaultHttpTimeoutSec = 30;
    private readonly ILogger _logger;
    private readonly string _sandboxRoot;           // 技能沙箱根（data/skillruns）
    private readonly bool _allowPrivateEndpoints;  // 是否放行本机 / 内网（默认 false → 保留 SSRF 防护）

    public SkillRunner(string sandboxRoot, ILoggerFactory loggerFactory, bool allowPrivateEndpoints = false)
    {
        _sandboxRoot = Path.GetFullPath(Path.TrimEndingDirectorySeparator(sandboxRoot)) + Path.DirectorySeparatorChar;
        _logger = loggerFactory.CreateLogger<SkillRunner>();
        _allowPrivateEndpoints = allowPrivateEndpoints;
    }

    /// <summary>技能运行沙箱目录（按技能 ID 干净命名）。</summary>
    private string SandboxDir(string skillId)
    {
        var safe = AgentSkillDefinition.ToAsciiToolId(skillId, new HashSet<string>(StringComparer.Ordinal));
        return Path.Combine(_sandboxRoot, safe);
    }

    /// <summary>执行技能，返回结果文本。任一环节失败返回错误文本，不抛异常（宿主模型继续）。</summary>
    public async Task<string> InvokeAsync(AgentSkillDefinition skill, string query, CancellationToken ct)
    {
        try
        {
            return skill.Kind switch
            {
                AgentSkillKind.Shell => await RunShellAsync(skill, query, ct),
                AgentSkillKind.Http => await RunHttpAsync(skill, query, ct),
                _ => RunPrompt(skill, query),
            };
        }
        catch (OperationCanceledException) { return "技能执行已取消。"; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "技能执行失败：{SkillId}", skill.SkillId);
            return "技能执行失败：" + ex.Message;
        }
    }

    // =============== Shell ===============

    private async Task<string> RunShellAsync(AgentSkillDefinition skill, string query, CancellationToken ct)
    {
        var dir = SandboxDir(skill.SkillId);
        Directory.CreateDirectory(dir);
        var body = skill.Body ?? "";

        // 把调用参数写入环境/输入：${query} 占位替换；同时写 args.env 供脚本读取
        var effective = body.Replace("${query}", query ?? "", StringComparison.Ordinal);

        // 写出脚本文件（首行 shebang 优先；无则按 Interpreter 决定，否则 bash）
        var scriptPath = Path.Combine(dir, "run.sh");
        // 中文 / 特殊字符参数经 JSON 注入环境变量，脚本内可用 $QUERY / "$ARGV_JSON"
        var argvJson = JsonSerializer.Serialize(new { query = query ?? "" });

        // 解释器选择：显式 Interpreter（如 python3 / node）→ 直接解释；否则有 shebang 用 shebang；否则 bash -e
        string fileName, args, workDir = dir;
        var trimmed = effective.TrimStart();
        string? shebangInterp = null;
        if (trimmed.StartsWith("#!", StringComparison.Ordinal))
        {
            var line = trimmed[..trimmed.IndexOf('\n')];
            shebangInterp = line[2..].Trim();
        }
        var interpreter = !string.IsNullOrWhiteSpace(skill.Interpreter) ? skill.Interpreter.Trim() : null;
        if (!string.IsNullOrWhiteSpace(interpreter))
        {
            // 用显式解释器执行脚本文件（避免依赖可执行位 / shebang 解析差异）
            scriptPath = Path.Combine(dir, interpreter.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0] + "_run.sh");
            File.WriteAllText(scriptPath, effective, Encoding.UTF8);
            fileName = interpreter.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
            args = "\"" + scriptPath + "\"";
        }
        else if (shebangInterp is not null)
        {
            scriptPath = Path.Combine(dir, "run");
            File.WriteAllText(scriptPath, effective, Encoding.UTF8);
            fileName = "/bin/sh";
            args = "\"" + scriptPath + "\"";
        }
        else
        {
            // 无 shebang 无 interpreter：当 bash 脚本执行（多行命令）。为避免参数注入，查询经环境变量传递，不进命令行
            File.WriteAllText(scriptPath, effective, Encoding.UTF8);
            fileName = "/bin/bash";
            args = "\"" + scriptPath + "\"";
        }

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = args,
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.Environment["QUERY"] = query ?? "";
        psi.Environment["ARGV_JSON"] = argvJson;
        psi.Environment["SKILL_ID"] = skill.SkillId;

        using var proc = new Process { StartInfo = psi };
        proc.Start();
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);
        var completed = proc.WaitForExit(milliseconds: 60_000);
        if (!completed) proc.Kill(entireProcessTree: true);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (!completed) return "技能命令超时（超过 60 秒），已终止。";

        var sb = new StringBuilder();
        if (stdout.Length > 0) sb.AppendLine(stdout.TrimEnd());
        if (stderr.Length > 0) sb.AppendLine("stderr: " + stderr.TrimEnd());
        sb.AppendLine($"（退出码 {proc.ExitCode}）");
        return Truncate(sb.ToString().TrimEnd());
    }

    // =============== HTTP ===============

    private async Task<string> RunHttpAsync(AgentSkillDefinition skill, string query, CancellationToken ct)
    {
        // Body 是 JSON 配置：{ method, url, headers?:{}, body?:string }，支持 ${query} ${params} 占位
        string method = "GET", url = "", bodyTemplate = "";
        Dictionary<string, string>? headers = null;
        try
        {
            using var cfg = JsonDocument.Parse(string.IsNullOrWhiteSpace(skill.Body) ? "{}" : skill.Body);
            var root = cfg.RootElement;
            if (root.TryGetProperty("method", out var m)) method = (m.GetString() ?? "GET").ToUpperInvariant();
            if (root.TryGetProperty("url", out var u)) url = u.GetString() ?? "";
            if (root.TryGetProperty("body", out var b)) bodyTemplate = b.GetString() ?? "";
            if (root.TryGetProperty("headers", out var h) && h.ValueKind == JsonValueKind.Object)
            {
                headers = new();
                foreach (var p in h.EnumerateObject()) headers[p.Name] = p.Value.GetString() ?? "";
            }
        }
        catch (Exception ex)
        {
            return "HTTP 技能配置（Body）不是合法 JSON：请用 {\"method\":\"GET\",\"url\":\"...\",\"headers\":{},\"body\":\"...\"} 格式。" + ex.Message;
        }

        var resolved = Resolve(query, skill.ParametersJson);
        if (string.IsNullOrWhiteSpace(url)) return "HTTP 技能缺少 url（Body 的 {\"url\":\"...\"}）。";
        try
        {
            url = Substitute(url, resolved);
            bodyTemplate = Substitute(bodyTemplate, resolved);
        }
        catch { return "HTTP 技能 url / body 占位替换失败。"; }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return "HTTP 技能仅支持 http/https 链接。";
        if (IsPrivateOrLoopback(uri)) return "出于安全考虑，HTTP 技能拒绝访问本机 / 内网地址。";

        var timeout = Math.Max(5, Math.Min(120, skill.HttpTimeoutSeconds > 0 ? skill.HttpTimeoutSeconds : DefaultHttpTimeoutSec));
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(timeout) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("AguiGroupChat-Skill/1.0");
        foreach (var (k, v) in headers ?? [])
            if (!string.Equals(k, "Content-Type", StringComparison.OrdinalIgnoreCase)) http.DefaultRequestHeaders.TryAddWithoutValidation(k, v);

        // 手动逐跳重定向（最多 5 跳）每跳重做私网校验（防 302 绕过 SSRF）
        var current = uri;
        string raw;
        for (var hop = 0; ; hop++)
        {
            using var req = BuildMessage(method, current, bodyTemplate, resolved, headers);
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            var location = resp.Headers.Location;
            if ((int)resp.StatusCode is >= 300 and < 400 && location is not null && !(method is "POST" or "PUT" or "PATCH"))
            {
                if (hop >= 5) return "HTTP 技能重定向过多（超过 5 跳），已放弃。";
                var next = location.IsAbsoluteUri ? location : new Uri(current, location);
                if (next.Scheme != Uri.UriSchemeHttp && next.Scheme != Uri.UriSchemeHttps) return "仅支持 http/https。";
                if (IsPrivateOrLoopback(next)) return "HTTP 技能重定向目标为本机 / 内网地址，已拒绝。";
                current = next;
                continue;
            }
            var statusText = $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}";
            var text = resp.Content is null ? "" : await resp.Content.ReadAsStringAsync(ct);
            raw = $"{statusText}\n\n{text}";
            break;
        }
        var outText = Truncate(StripHtml(raw));
        return string.IsNullOrWhiteSpace(outText) ? "（接口未返回内容）" : outText;
    }

    private static HttpRequestMessage BuildMessage(string method, Uri uri, string bodyTemplate, Dictionary<string, string> resolved, Dictionary<string, string>? headers)
    {
        var req = new HttpRequestMessage(new HttpMethod(method), uri);
        if (!string.IsNullOrWhiteSpace(bodyTemplate) && method is "POST" or "PUT" or "PATCH")
        {
            var content = new StringContent(bodyTemplate, Encoding.UTF8, "application/json");
            req.Content = content;
        }
        return req;
    }

    // =============== Prompt ===============

    private static string RunPrompt(AgentSkillDefinition skill, string query)
    {
        // 提示词 / 流程模板：无可执行代码。把模板 + 参数交给模型自行推理 / 聚合。
        var sb = new StringBuilder();
        sb.AppendLine("（提示词技能，无外部执行）请按下方技能要求结合你的能力处理下面的请求，并直接给出结果：");
        if (!string.IsNullOrWhiteSpace(skill.Body))
            sb.AppendLine("【技能要求】\n" + skill.Body.TrimEnd());
        sb.AppendLine("【请求】\n" + (query ?? ""));
        return Truncate(sb.ToString().TrimEnd());
    }

    // =============== 工具方法 ===============

    /// <summary>把 query 与技能参数合并为占位替换字典。</summary>
    private static Dictionary<string, string> Resolve(string query, string parametersJson)
    {
        var resolved = new Dictionary<string, string>(StringComparer.Ordinal) { ["query"] = query ?? "" };
        if (string.IsNullOrWhiteSpace(parametersJson)) return resolved;
        try
        {
            using var doc = JsonDocument.Parse(parametersJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return resolved;
            foreach (var p in doc.RootElement.EnumerateObject())
                resolved[p.Name] = p.Value.GetString() ?? "";
        }
        catch { /* 参数不是合法 JSON 时忽略 */ }
        return resolved;
    }

    private static string Substitute(string template, Dictionary<string, string> resolved)
    {
        if (string.IsNullOrEmpty(template)) return template;
        return Regex.Replace(template, @"\$\{(\w+)\}", m =>
            resolved.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);
    }

    private static string Truncate(string? s)
        => string.IsNullOrWhiteSpace(s) ? "" : s.Length <= MaxOutputChars ? s : s[..MaxOutputChars] + "\n…（输出已截断）";

    /// <summary>极简 HTML → 文本（去标签，保留换行）。</summary>
    private static string StripHtml(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return html;
        var t = Regex.Replace(html, "(?is)<(script|style)[^>]*>.*?</\\1>", "");
        t = Regex.Replace(t, "(?s)<br\\s*/?>", "\n");
        t = Regex.Replace(t, "(?s)</(p|div|li|tr|h[1-6])>", "\n");
        t = Regex.Replace(t, "<[^>]+>", "");
        t = System.Net.WebUtility.HtmlDecode(t);
        t = Regex.Replace(t, "[\r\n]{3,}", "\n\n");
        return t.Trim();
    }

    /// <summary>拒绝私网 / 环回 / 链路本地 / 非公网地址（DNS 解析后逐 IP 判定）。
    /// 当 <see cref="_allowPrivateEndpoints"/> 为 true 时放行（用于需要访问本机 / 内网接口的部署）。</summary>
    internal bool IsPrivateOrLoopback(Uri uri)
    {
        if (_allowPrivateEndpoints) return false;
        var host = uri.Host;
        if (IPAddress.TryParse(host, out var ip))
        {
            return IsRestrictedIp(ip);
        }
        try
        {
            var addresses = System.Net.Dns.GetHostAddresses(host);
            foreach (var a in addresses)
                if (IsRestrictedIp(a)) return true;
        }
        catch { return true; }
        return false;
    }

    internal static bool IsRestrictedIp(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
            var b = ip.GetAddressBytes();
            if (ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.Broadcast)) return true;
            if (b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254) || (b[0] == 127)) return true;
        }
        return false;
    }
}
