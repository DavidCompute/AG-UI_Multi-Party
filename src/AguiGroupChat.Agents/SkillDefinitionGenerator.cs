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
    public static async Task<GeneratedSkillDefinition> GenerateAsync(
        AgentOptions options, string request, bool preferClient, bool allowDotnet, ILogger logger, CancellationToken ct, string? runContextNote = null)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));
        var req = request.Trim();
        if (req.Length < 2) throw new InvalidOperationException("需求描述至少 2 个字符");
        if (req.Length > 500) throw new InvalidOperationException("需求描述最长 500 字符");

        if (string.Equals(options.Provider, "mock", StringComparison.OrdinalIgnoreCase))
            return BuildTemplate(req, preferClient);

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
            var prompt = BuildPrompt(req, preferClient, allowDotnet, runContextNote);
            var resp = await client.GetResponseAsync([new ChatMessage(ChatRole.User, prompt)], cancellationToken: ct);
            var text = resp.Text?.Trim();
            if (string.IsNullOrWhiteSpace(text))
                throw new InvalidOperationException("技能生成返回为空，请重试");
            var parsed = Parse(text, allowDotnet);
            logger.LogInformation("已根据需求生成技能配置（kind={Kind},dotnet={Allow}）：{Request}", parsed.Kind, allowDotnet, Truncate(req));
            return parsed;
        }
        finally
        {
            client.Dispose();
        }
    }

    private static string BuildPrompt(string request, bool preferClient, bool allowDotnet, string? runContextNote)
    {
        string kinds = allowDotnet
            ? "- prompt：提示词 / 流程模板（无外部执行）。body 填模板文本。\\n- http：HTTP 配置。\\n- shell：命令脚本。\\n- dotnet：C# 源码技能（body 为 C# 源码，须含 public static string Run(string input)，可在客户端/本机或服务端编译运行）。\\n"
            : "- prompt：提示词 / 流程模板。body 填模板。\\n- http：HTTP 配置。\\n- shell：命令 / 脚本。\\n（dotnet 类型仅系统管理员可用，普通用户请勿返回。）\\n";
        // runEnv + runSettings：runEnv 说明技能将运行的操作系统与 shell 约定；runSettings 进一步给出“目标执行位置”
        // 与“应使用的脚本方言 / 运行方式”，让模型产出与目标平台匹配的可执行代码。
        var target = runContextNote;
        string runSummary;
        if (string.IsNullOrEmpty(target))
            runSummary = preferClient ? "本机运行（浏览器所在的用户机器，系统未知，脚本请尽量跨平台或采用常见 PowerShell/cmd 语法）"
                : "服务端运行（" + OsLabel() + "）";
        else
            runSummary = target;
        string runSettings;
        if (runSummary.Contains("Windows"))
            runSettings = "目标是 Windows，shell 技能正文请写 PowerShell / cmd 命令，不要写 bash 语法（无 bash）；dotnet 技能含 C# 源码不要依赖非 .NET 桥接工具。";
        else if (runSummary.Contains("macOS") || runSummary.Contains("Linux"))
            runSettings = "目标是 " + (runSummary.Contains("macOS") ? "macOS" : "Linux") + "，shell 技能正文请写 bash/sh 语法；dotnet 技能写跨平台 C# 源码。";
        else
            runSettings = "目标环境未明确上报，shell 技能请尽量兼顾 PowerShell 与 bash 或说明所需环境；dotnet 技能写跨平台 C# 源码。";

        return
            "你是企业技能生成器。根据用户的自然语言需求，生成一份能直接保存的技能库配置。\n\n" +
            "技能类型（kind）可选：\n" + kinds +
            "执行位置（executionLocation）：server（在服务端执行为主）/ client（在本机/前端执行为主）" +
            (preferClient ? "。本次需求倾向在本机(client)执行。\n" : "。默认 server，仅当用户要针对本机操作时用 client。\n") +
            (allowDotnet
                ? "若需读/写本机 Excel/Word/文件/Registry 或调用 .NET 库这类需要本地能力的，应优先用 kind=dotnet。" +
                  "dotnet 技能的正文是 C# 源码，须含 public static string Run(string input) 返回 string；" +
                  (preferClient
                    ? "本次目标是本机，executionLocation 请写 client（由本机桥在用户机器编译执行，可直接访问该机路径/文件）。\n"
                    : "executionLocation 按“目标环境”选择：操作本机文件建议 client（本机桥执行），纯计算/服务端数据则 server（Roslyn 编译执行）。\n")
                : "不要生成 kind=dotnet（非系统管理员无权建），也不要试图绕过。\n") +
            "skillId 用 ASCII（字母/数字/_/-，≤40）。只输出如下 JSON：\n" +
            "{\"name\":\"中文名\",\"skillId\":\"id\",\"kind\":\"(按允许类型)\",\"description\":\"给模型的调用说明，50~150 字\",\"body\":\"正文\",\"executionLocation\":\"server|client\",\"clientRunner\":null,\"requiresApproval\":true}\n\n" +
            runSummary + "。\n" + runSettings + "\n\n" +
            "用户需求：" + request;
    }

    private static string OsLabel() =>
        OperatingSystem.IsWindows() ? "Windows" : (OperatingSystem.IsMacOS() ? "macOS" : "Linux");

    private static GeneratedSkillDefinition Parse(string text, bool allowDotnet)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start) throw new InvalidOperationException("技能生成结果不是有效 JSON");
        using var doc = JsonDocument.Parse(text.Substring(start, end - start + 1));
        var r = doc.RootElement;
        string G(string k) => r.TryGetProperty(k, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";
        var name = G("name");
        var kind = G("kind").ToLowerInvariant();
        var description = G("description");
        var body = G("body");
        var execLoc = G("executionLocation").ToLowerInvariant() == "client" ? "client" : "server";
        var requiresApproval = r.TryGetProperty("requiresApproval", out var ra) && ra.ValueKind == JsonValueKind.True;
        var clientRunner = G("clientRunner");
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(kind) || string.IsNullOrWhiteSpace(body))
            throw new InvalidOperationException("技能生成结果缺少必要字段（name/kind/body）");
        bool allowed = kind switch
        {
            "prompt" or "shell" or "http" => true,
            "dotnet" => allowDotnet,
            _ => false,
        };
        if (!allowed) kind = "prompt"; // 非允许/非管理员生成的 dotnet 回退为普通模板
        // shell 一律需审批（安全兜底），客户端执行一律需审批
        var finalApproval = kind == "shell" || execLoc == "client" || requiresApproval;
        return new GeneratedSkillDefinition(name, NullIfEmpty(G("skillId")), kind, description, body, execLoc, NullIfEmpty(clientRunner), finalApproval);
    }

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string Truncate(string s) => s.Length <= 60 ? s : s[..60] + "…";

    private static string SanitizeId(string raw)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var c in raw)
            sb.Append(char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_');
        return sb.ToString().Trim('_');
    }

    private static GeneratedSkillDefinition BuildTemplate(string request, bool preferClient)
    {
        var id = SanitizeId("auto_" + request);
        var friendly = request.Length > 40 ? request[..40] + "…" : request;
        return new GeneratedSkillDefinition(
            "自动生成技能", id, "prompt", $"根据需求「{request}」生成的技能：请按模板与请求直接综合作答。",
            $"请针对以下请求给出专业处理：\n{friendly}", "server", null, RequiresApproval: false);
    }
}
