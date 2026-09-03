using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace AguiGroupChat.Agents;

/// <summary>一次“自测 + 自动修复”的结果。Skipped=true：该技能未纳入自测（无法安全自动评估）。</summary>
public sealed record SkillAutoFixResult(
    string SkillId,
    bool Skipped,
    bool Ok,
    int Attempts,
    string? LastError,
    string? CorrectedBody,
    string? CorrectedDescription);

/// <summary>
/// 对“自动编排”生成的技能做<b>生成后自测 + 报错自动修复</b>，直到能正确运行（或用尽尝试）。
///
/// 自测口径（开关由 <see cref="AgentOptions.SkillAutoTestServerShell"/> 控制，默认 server shell 也盲跑）：
///   - prompt（server）：拿模板真实问一次模型，能产出非空文本才通过（编排主力类型，最常出错）；
///   - http（server）  ：<b>不发真实外呼</b>（防副作用 / SSRF），只校验 Body JSON 配置（url/method），不合法则修复；
///   - shell（server） ：开关开启时<b>盲跑一次</b>（在 data/skillruns 沙箱、60s 超时兜底），
///                       以退出码 / 超时 / “技能执行失败”等标志判失败；失败则按报错让模型修正文并复测；
///   - client（本机/隧道）与其它：一律跳过（服务端无法替你评估本机 shell 结果）。
/// Provider=mock 时跳过模型环节。
/// 修复：把「原技能 + 冒烟输入 + 报错」交给大模型，仅重写同一 id/kind 的正文（必要时含描述），复测；
/// 最多 <paramref name="maxAttempts"/> 轮。
/// </summary>
public sealed class SkillAutoFixer
{
    private const int MaxShellProbeChars = 6_000; // 盲跑 shell 的输出太长会占爆修复上下文，截断后只拿报错尾巴

    private readonly AgentOptions _options;
    private readonly AgentCatalog? _catalog;   // server 技能真实执行（echo shell 盲跑）用
    private readonly ILogger _logger;

    public SkillAutoFixer(AgentOptions options, AgentCatalog? catalog, ILoggerFactory loggerFactory)
    {
        _options = options;
        _catalog = catalog;
        _logger = loggerFactory.CreateLogger<SkillAutoFixer>();
    }

    private bool Mock => string.Equals(_options.Provider, "mock", StringComparison.OrdinalIgnoreCase);

    /// <summary>对单个技能自测，失败则自动修复并复测。调用方据此决定用原版还是修正版落库。</summary>
    public async Task<SkillAutoFixResult> VerifyOrRepairAsync(
        AgentSkillDefinition skill, int maxAttempts = 3, CancellationToken ct = default)
    {
        var def = skill ?? throw new ArgumentNullException(nameof(skill));

        bool server = def.ExecutionLocation == AgentSkillExecutionLocation.Server;
        bool shellEligible = _options.SkillAutoTestServerShell && _catalog is not null && def.Kind == AgentSkillKind.Shell;
        bool selfTestable = server && (def.Kind == AgentSkillKind.Prompt || def.Kind == AgentSkillKind.Http || shellEligible);
        if (!selfTestable || Mock)
            return new SkillAutoFixResult(def.SkillId, Skipped: true, Ok: false, Attempts: 0, null, null, null);

        var attempt = 0;
        string lastError = "";
        var body = def.Body ?? "";
        var description = def.Description ?? "";
        while (attempt < maxAttempts)
        {
            attempt++;
            var (ok, err) = await ProbeAsync(def, body, description, ct).ConfigureAwait(false);
            if (ok)
            {
                _logger.LogInformation("技能自测通过：{SkillId}(kind={Kind},attempt={N})", def.SkillId, def.Kind, attempt);
                return new SkillAutoFixResult(def.SkillId, false, true, attempt, null,
                    attempt == 1 ? null : body, attempt == 1 ? null : description);
            }
            lastError = err ?? "";
            _logger.LogInformation("技能自测未通过(attempt={N})：{SkillId} {Err}", attempt, def.SkillId, lastError);
            if (attempt >= maxAttempts) break;

            var repaired = await RepairOnceAsync(def, description, body, lastError, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(repaired)) { lastError = "模型未能给出可用修复。"; break; }
            body = repaired;
        }
        return new SkillAutoFixResult(def.SkillId, false, false, attempt, lastError,
            attempt >= 2 ? body : null, attempt >= 2 ? description : null);
    }

    /// <summary>按类型做一次冒烟，返回 (是否通过, 报错)。</summary>
    private async Task<(bool Ok, string? Error)> ProbeAsync(AgentSkillDefinition def, string body, string description, CancellationToken ct)
    {
        if (def.Kind == AgentSkillKind.Http)
            return HttpLint(body);
        if (def.Kind == AgentSkillKind.Shell)
            return await ShellBlindRunAsync(def, body, ct).ConfigureAwait(false);
        return await PromptSelfTalkAsync(def, body, description, ct).ConfigureAwait(false);
    }

    private async Task<(bool Ok, string? Error)> PromptSelfTalkAsync(AgentSkillDefinition def, string body, string description, CancellationToken ct)
    {
        try
        {
            var isDeepSeek = string.Equals(_options.Provider, "deepseek", StringComparison.OrdinalIgnoreCase);
            var ov = isDeepSeek ? "deepseek-chat" : null; // 确定性短问答，强制常规模型
            using var client = AgentCatalog.BuildOpenAIChatClient(
                _options, new AgentDefinition { AgentId = "skill_selfcheck", Nickname = "技能自测器" }, isDeepSeek, ov).AsIChatClient();
            var prompt =
                "以下是一个数字员工的“技能”。请把它当作给你的指令现场执行一次并给出<b>具体结果</b>（这是自测，直接作答）。\n" +
                $"技能说明：{description}\n模板：\n{body}\n\n自测请求：请对一个小而典型的样例执行本技能并输出结果。";
            var resp = await client.GetResponseAsync([new ChatMessage(ChatRole.User, prompt)], cancellationToken: ct);
            var txt = (resp.Text ?? "").Trim();
            return txt.Length == 0 ? (false, "prompt 技能自测：模型未产出任何内容") : (true, null);
        }
        catch (Exception ex)
        {
            return (false, "prompt 技能自测失败：" + ex.Message);
        }
    }

    /// <summary>server shell 盲跑一次，用退出码 / 超时 / 执行失败标志判成败。query 用空串（不塞入随机文本，避免污染命令）。</summary>
    private async Task<(bool Ok, string? Error)> ShellBlindRunAsync(AgentSkillDefinition def, string body, CancellationToken ct)
    {
        if (_catalog is null) return (false, "技能执行器不可用，无法盲跑 shell");
        try
        {
            // body 可能在修复中已变；复制一份带着 body 的真实定义去执行
            var copy = new AgentSkillDefinition
            {
                SkillId = def.SkillId,
                Name = def.Name,
                Description = def.Description,
                Kind = def.Kind,
                Body = body,
                ParametersJson = def.ParametersJson,
                Interpreter = def.Interpreter,
                ExecutionLocation = def.ExecutionLocation,
            };
            var outText = await _catalog.RunSkillAsync(copy, "", ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(outText)) return (false, "shell 盲跑：命令无任何输出");
            var trimmed = outText.Trim();
            if (trimmed.StartsWith("技能执行失败", StringComparison.OrdinalIgnoreCase)
                || trimmed.Contains("已终止", StringComparison.Ordinal)
                || trimmed.Contains("超时", StringComparison.Ordinal)
                || IsNonZeroExit(trimmed))
            {
                var tail = trimmed.Length > MaxShellProbeChars ? trimmed[^MaxShellProbeChars..] : trimmed;
                return (false, "shell 盲跑未通过：\n" + tail);
            }
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, "shell 盲跑失败：" + ex.Message);
        }
    }

    /// <summary>从输出尾部解析“（退出码 N）”，非 0 记为失败。</summary>
    private static bool IsNonZeroExit(string text)
    {
        var idx = text.LastIndexOf("（退出码 ", StringComparison.Ordinal);
        if (idx < 0) idx = text.LastIndexOf("(exit code ", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return false;
        var seg = text[idx..];
        var m = System.Text.RegularExpressions.Regex.Match(seg, @"\d+");
        return m.Success ? m.Value != "0" : false;
    }

    private (bool Ok, string? Error) HttpLint(string body)
    {
        try
        {
            using var cfg = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            var root = cfg.RootElement;
            if (!root.TryGetProperty("url", out var u) || string.IsNullOrWhiteSpace(u.GetString()))
                return (false, "HTTP 技能正文缺少 {\"url\":\"...\"}，无法发起请求。");
            var url = u.GetString()!;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                return (false, $"HTTP 技能 url 不合法：{url}");
            if (root.TryGetProperty("method", out var m) && m.GetString() is { Length: > 0 } mm)
            {
                var mmu = mm.ToUpperInvariant();
                if (mmu is not ("GET" or "POST" or "PUT" or "PATCH" or "DELETE" or "HEAD" or "OPTIONS"))
                    return (false, $"HTTP 技能 method 不合法：{mm}");
            }
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, "HTTP 技能正文不是合法 JSON 配置：" + ex.Message);
        }
    }

    /// <summary>针对报错让大模型重写正文；返回新正文，失败返回 null。</summary>
    private async Task<string?> RepairOnceAsync(AgentSkillDefinition def, string description, string body, string lastError, CancellationToken ct)
    {
        try
        {
            var isDeepSeek = string.Equals(_options.Provider, "deepseek", StringComparison.OrdinalIgnoreCase);
            var ov = isDeepSeek ? "deepseek-chat" : null; // 结构化改写，强制常规模型
            using var client = AgentCatalog.BuildOpenAIChatClient(
                _options, new AgentDefinition { AgentId = "skill_repair", Nickname = "技能修复器" }, isDeepSeek, ov).AsIChatClient();
            var heading = def.Kind switch
            {
                AgentSkillKind.Http => "该技能是 HTTP 配置：请只输出一段<b>合法 JSON 配置</b>作正文，形如 {\"method\":\"GET\",\"url\":\"...\",\"headers\":{},\"body\":null}；url 需 http/https 且真实可用（可含 ${query} 占位）。不要任何其它文字。",
                AgentSkillKind.Shell => "该技能是 shell 命令 / 脚本：请修正为<b>可在该服务器环境直接运行、退出码为 0 且输出明确结果</b>的命令/脚本。注意避免过长，不依赖未安装程序（若确实需要可先说明）。只输出命令/脚本本身，不要 ``` 围栏、不要任何解释。",
                _ => "该技能是提示词 / 流程模板：修正为结构清晰、可让执行的模型产出具体有用回答的分步指令 / 占位模板。",
            };
            var prompt =
                "你是技能修复器。下面这个自动生成的技能自测失败，请按报错修复它的正文（id 与 kind 保持不变）：\n" +
                $"技能ID：{def.SkillId}\n技能类型 kind：{def.Kind}\n技能说明：{description}\n技能正文：\n```\n{body}\n```\n自测报错：{lastError}\n\n" +
                heading + "\n请只输出修正后的技能正文。";
            var resp = await client.GetResponseAsync([new ChatMessage(ChatRole.User, prompt)], cancellationToken: ct);
            var txt = (resp.Text ?? "").Trim();
            if (txt.Length == 0) return null;
            if (def.Kind == AgentSkillKind.Http)
            {
                var json = ExtractJson(txt);
                try { using var _ = JsonDocument.Parse(json); return json; }
                catch { return null; }
            }
            var clean = StripFence(txt);
            return clean.Length is > 0 and <= 20_000 ? clean : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "技能修复调用失败：{SkillId}", def.SkillId);
            return null;
        }
    }

    private static string StripFence(string s)
    {
        var t = s.Trim();
        if (t.StartsWith("```", StringComparison.Ordinal))
        {
            var nl = t.IndexOf('\n');
            if (nl >= 0) t = t[(nl + 1)..];
            if (t.EndsWith("```", StringComparison.Ordinal)) t = t[..^3];
        }
        return t.Trim();
    }

    private static string ExtractJson(string s)
    {
        var t = StripFence(s);
        var start = t.IndexOf('{');
        var end = t.LastIndexOf('}');
        return start >= 0 && end >= start ? t[start..(end + 1)] : t;
    }
}
