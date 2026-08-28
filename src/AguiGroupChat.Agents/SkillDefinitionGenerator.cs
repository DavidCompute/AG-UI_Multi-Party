using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace AguiGroupChat.Agents;

/// <summary>
/// 根据用户的<b>自然语言需求</b>生成一份完整的技能配置（名称 / ID / 类型 / 说明 / 正文 / 执行位置 / ClientRunner 等），
/// 供前端「🤖 用自然语言生成技能」填入技能库表单，降低技能库配置门槛。
/// Provider=mock 时输出确定性模板（无模型调用），便于本地演示与测试。
/// </summary>
public sealed record GeneratedSkillDefinition(
    string Name,
    string? SkillId,
    string Kind,
    string Description,
    string Body,
    string ExecutionLocation,
    string? ClientRunner,
    bool RequiresApproval);

public static class SkillDefinitionGenerator
{
    public static async Task<GeneratedSkillDefinition> GenerateAsync(AgentOptions options, string request, bool preferClient, ILogger logger, CancellationToken ct)
    {
        var req = (request ?? "").Trim();
        if (req.Length < 2) throw new InvalidOperationException("需求描述至少 2 个字符");
        if (req.Length > 500) throw new InvalidOperationException("需求描述最长 500 字符");

        if (string.Equals(options.Provider, "mock", StringComparison.OrdinalIgnoreCase))
        {
            return BuildTemplate(req, preferClient);
        }

        IChatClient client;
        try
        {
            var isDeepSeek = string.Equals(options.Provider, "deepseek", StringComparison.OrdinalIgnoreCase);
            client = AgentCatalog.BuildOpenAIChatClient(
                options, new AgentDefinition { AgentId = "skill_gen", Nickname = "技能生成器" }, isDeepSeek).AsIChatClient();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("技能生成需要可用的模型配置（" + ex.Message + "）", ex);
        }

        try
        {
            var prompt = BuildPrompt(req, preferClient);
            var resp = await client.GetResponseAsync([new ChatMessage(ChatRole.User, prompt)], cancellationToken: ct);
            var text = resp.Text?.Trim();
            if (string.IsNullOrWhiteSpace(text))
                throw new InvalidOperationException("技能生成返回为空，请重试");
            var parsed = Parse(text);
            logger.LogInformation("已根据需求生成技能配置（kind={Kind}）：{Request}", parsed.Kind, Truncate(req));
            return parsed;
        }
        finally
        {
            client.Dispose();
        }
    }

    private static string BuildPrompt(string request, bool preferClient)
    {
        return
            "你是企业技能生成器。根据用户的自然语言需求，生成一份<b>可直接保存的技能库配置</b>，只输出一个 JSON，不要任何其他文字。\n\n" +
            "技能库技能类型（kind）三种：\n" +
            "- shell：可执行命令/脚本。body 填该命令/脚本本身（Windows 用 PowerShell 命令；Linux/macOS 用 bash）。命令要精简、输出结构化（如磁盘用 Get-CimInstance，结果尽量用 ConvertTo-Json 或简洁文本），避免超长。\n" +
            "- http：调用外部 HTTP 接口。body 填 JSON：{\"method\":\"GET\",\"url\":\"<带 ${query} 占位>\",\"headers\":{},\"body\":null}。\n" +
            "- prompt：提示词/流程模板（无外部执行）。body 填模板文本。\n\n" +
            "执行位置（executionLocation）：\n" +
            "- server：服务端执行（默认）。\n" +
            "- client：客户端/本机执行。" + (preferClient
                ? "（<b>优先考虑 client</b>：若需求是查本机/桌面信息、执行本机命令，应设为 client。）\n"
                : "仅在需求明确要对本机做操作时才用 client。\n") +
            "当 executionLocation=client 时，必须同时填 clientRunner 为可被前端解析的 JSON 字符串：\n" +
            "- shell（客户端）：{\"kind\":\"shell\",\"command\":\"<完整命令>\",\"cwd\":\".\",\"timeoutSec\":30}\n" +
            "- http（客户端）：{\"kind\":\"http\",\"method\":\"GET\",\"url\":\"<url>\",\"headers\":{}}\n" +
            "（clientRunner 里的 command 要与 body 一致。）\n\n" +
            "skillId 必须是 ASCII 字母/数字/下划线/连字符（长度 ≤40），如 disk_usage_check、hostname_lookup。\n" +
            "requiresApproval：shell 一律 true；http/prompt 视执行位置：client 一律 true，server 可 false（无副作用时可 false）。（服务端/网关会强制校验，这里给建议值。）\n\n" +
            "请只输出如下 JSON：\n" +
            "{\"name\":\"<中文名，简短>\",\"skillId\":\"<ascii_id>\",\"kind\":\"shell|http|prompt\",\"description\":\"<给模型的调用说明：何时调用、参数、返回什么，50~150字>\",\"body\":\"<正文>\",\"executionLocation\":\"server|client\",\"clientRunner\":<null 或含转义引号的 JSON 字符串>,\"requiresApproval\":<bool>}\n\n" +
            "用户需求：" + request;
    }

    private static GeneratedSkillDefinition Parse(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start) throw new InvalidOperationException("技能生成结果不是有效 JSON");
        JsonDocument? doc = null;
        try { doc = JsonDocument.Parse(text.Substring(start, end - start + 1)); }
        catch (Exception ex) { throw new InvalidOperationException("技能生成结果 JSON 解析失败：" + ex.Message, ex); }
        using (doc)
        {
            var r = doc.RootElement;
            string G(string k) => r.TryGetProperty(k, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";
            var name = G("name");
            var kind = G("kind").ToLowerInvariant();
            var description = G("description");
            var body = G("body");
            var execLoc = G("executionLocation").ToLowerInvariant() == "client" ? "client" : "server";
            var requiresApproval = r.TryGetProperty("requiresApproval", out var ra) && ra.ValueKind == JsonValueKind.True;
            // shell 一律需审批（安全兜底），客户端执行一律需审批
            var finalApproval = kind == "shell" || execLoc == "client" || requiresApproval;
            var clientRunner = G("clientRunner");
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(kind) || string.IsNullOrWhiteSpace(body))
                throw new InvalidOperationException("技能生成结果缺少必要字段（name/kind/body）");
            if (kind is not ("shell" or "http" or "prompt")) kind = "prompt";
            return new GeneratedSkillDefinition(name, NullIfEmpty(G("skillId")), kind, description, body, execLoc, NullIfEmpty(clientRunner), finalApproval);
        }
    }

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string Truncate(string s) => s.Length <= 60 ? s : s[..60] + "…";

    /// <summary>mock 模式确定性模板：围绕需求生成一个客户端 shell 技能示例。</summary>
    private static GeneratedSkillDefinition BuildTemplate(string request, bool preferClient)
    {
        var id = SanitizeId("auto_" + request);
        var friendly = request.Length > 40 ? request[..40] + "…" : request;
        var cmd = "Write-Output \"" + friendly.Replace("\"", "'") + "\"";
        if (preferClient)
            return new GeneratedSkillDefinition(
                "自动生成技能", id, "shell", $"根据需求「{request}」生成的技能：按需求执行一条命令并回传结果。",
                cmd, "client", "{\"kind\":\"shell\",\"command\":\"" + cmd + "\",\"cwd\":\".\",\"timeoutSec\":30}",
                RequiresApproval: true);
        return new GeneratedSkillDefinition(
            "自动生成技能", id, "prompt", $"根据需求「{request}」生成的技能：请按模板与请求直接综合作答。",
            $"请针对以下请求给出专业处理：\n{request}", "server", null, RequiresApproval: false);
    }

    private static string SanitizeId(string s)
    {
        var chars = s.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray();
        var v = new string(chars).Trim('_');
        if (v.Length > 40) v = v[..40];
        return string.IsNullOrWhiteSpace(v) ? "auto_skill" : v;
    }
}
