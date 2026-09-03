using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace AguiGroupChat.Agents;

/// <summary>
/// 技能“试运行/自测”的建议参数生成器：根据技能说明 + 正文，让大模型给出一个<b>典型示例 query/参数</b>，
/// 用户在试运行时可直接用它或微调（避免每次都要手搓一个不合适的输入）。
/// Provider=mock 或模型不可用时返回通用示例，不抛（试运行口仍可用）。
/// </summary>
public static class SkillQuerySuggester
{
    private const string MockDefault = "你好";

    public static async Task<string> SuggestAsync(
        AgentOptions options, AgentSkillDefinition skill, ILogger logger, CancellationToken ct = default)
    {
        var kind = skill.Kind;
        var body = (skill.Body ?? "").Trim();
        var description = (skill.Description ?? "").Trim();
        if (string.Equals(options.Provider, "mock", StringComparison.OrdinalIgnoreCase))
            return kind == AgentSkillKind.Http ? MockDefault : (kind == AgentSkillKind.Shell && !body.Contains("${query}", StringComparison.Ordinal) ? "" : MockDefault);

        try
        {
            var isDeepSeek = string.Equals(options.Provider, "deepseek", StringComparison.OrdinalIgnoreCase);
            using var client = AgentCatalog.BuildOpenAIChatClient(
                options, new AgentDefinition { AgentId = "skill_suggester", Nickname = "技能参数建议" }, isDeepSeek).AsIChatClient();
            var kindNote = kind switch
            {
                AgentSkillKind.Http => "该技能是 http：返回应填入 ${query}/请求的一个<b>典型真实示例值</b>（如城市名 / 商品名 / 编号等，尽量简短单行）。",
                AgentSkillKind.Shell => "该技能是 shell 命令/脚本：若正文里用到 ${query}，返回一个典型示例参数值；若正文不使用任何入参（探针/巡检类），则返回空字符串。",
                _ => "该技能是 prompt 提示词/模板：返回一个<b>小的典型业务请求</b>，能让模板产出有价值回答。",
            };
            var prompt =
                "你是技能试运行助手。根据技能说明与正文，为该技能的“试运行”给一个合适的<b>示例输入（单个字符串）</b>，方便用户直接试跑。\n" +
                $"技能ID：{skill.SkillId}\n技能类型：{kind}\n技能说明：{description}\n技能正文：\n```\n{body}\n```\n\n" +
                kindNote + "\n只输出这一个示例字符串本身，不要引号包裹、不要解释、不要换行（如确无需参数则输出空）。";
            var resp = await client.GetResponseAsync([new ChatMessage(ChatRole.User, prompt)], cancellationToken: ct);
            var text = (resp.Text ?? "").Trim().Trim('"', '\'', '`');
            return text.Length > 200 ? text[..200] : text;
        }
        catch (Exception ex)
        {
            logger.LogWarning("技能参数建议生成失败，回退通用示例：{SkillId} {Err}", skill.SkillId, ex.Message);
            return MockDefault;
        }
    }
}
