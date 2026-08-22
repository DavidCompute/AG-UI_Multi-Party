using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace AguiGroupChat.Agents;

/// <summary>
/// 根据一句话简介生成智能体完整角色设定（身份定位 / 职责范围 / 回复风格要求），
/// 供前端「✨ 生成」按钮填充 Instructions 系统提示词。
/// Provider=mock 时输出确定性模板（无模型调用），便于本地演示与测试。
/// </summary>
public static class AgentInstructionsGenerator
{
    /// <summary>生成完整角色设定文本。真实模型走 OpenAI 兼容接口；mock 走模板。</summary>
    public static async Task<string> GenerateAsync(AgentOptions options, string description, ILogger logger, CancellationToken ct)
    {
        var desc = (description ?? "").Trim();
        if (desc.Length < 2) throw new InvalidOperationException("一句话简介至少 2 个字符");
        if (desc.Length > 200) throw new InvalidOperationException("一句话简介最长 200 字符");

        if (string.Equals(options.Provider, "mock", StringComparison.OrdinalIgnoreCase))
        {
            // mock：确定性模板（无需 API Key，便于本地演示与测试）
            return BuildTemplate(desc);
        }

        IChatClient client;
        try
        {
            var isDeepSeek = string.Equals(options.Provider, "deepseek", StringComparison.OrdinalIgnoreCase);
            client = AgentCatalog.BuildOpenAIChatClient(
                options, new AgentDefinition { AgentId = "instructions_gen", Nickname = "角色设定生成器" }, isDeepSeek).AsIChatClient();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("角色设定生成需要可用的模型配置（" + ex.Message + "）", ex);
        }

        try
        {
            var prompt =
                "你是 AI 角色设定生成器。根据用户的一句话简介，为该智能体生成完整「系统提示词」（Instructions）。\n" +
                "必须包含三个部分（markdown 分节）：\n" +
                "1. 身份定位：你是什么角色、代表谁、在什么场景下工作；\n" +
                "2. 职责范围：你负责什么、不负责什么（边界），以及基本工作方法；\n" +
                "3. 回复风格要求：语气、结构、长度、专业程度等。\n" +
                "以「你是…」开头，用第二人称「你」描述，总长 500 字以内，只输出设定正文，不要任何解释或前缀。\n\n" +
                $"一句话简介：{desc}";

            var resp = await client.GetResponseAsync([new ChatMessage(ChatRole.User, prompt)], cancellationToken: ct);
            var text = resp.Text?.Trim();
            if (string.IsNullOrWhiteSpace(text))
                throw new InvalidOperationException("角色设定生成返回为空，请重试");
            logger.LogInformation("已根据一句话简介生成角色设定（{Chars} 字）：{Description}", text.Length,
                desc.Length > 100 ? desc[..100] + "…" : desc); // 日志内容截断：简介可能含用户隐私，只记录前 100 字符
            return text;
        }
        finally
        {
            client.Dispose();
        }
    }

    /// <summary>mock 模式的确定性模板：围绕一句话简介组织身份 / 职责 / 风格三段。</summary>
    private static string BuildTemplate(string description)
    {
        return $"你是「{description}」领域的专业智能体。\n\n" +
               "## 身份定位\n" +
               $"你是专注于「{description}」的专业助手，以清晰、专业、可信赖的方式为群成员提供帮助。" +
               "你了解该领域的常见问题与最佳实践，能给出可落地的建议。\n\n" +
               "## 职责范围\n" +
               "- 负责：围绕「{description}」解答疑问、提供方案、整理与归纳信息；\n" +
               "- 不负责：与「{description}」无关的事务（礼貌说明并引导回主题）；\n" +
               "- 信息不确定时如实说明，不编造事实；涉及敏感操作（发布公告等）会先征求确认。\n\n" +
               "## 回复风格要求\n" +
               "- 语气专业、友好、简洁；\n" +
               "- 结论先行，再给理由与要点；\n" +
               "- 复杂问题分点作答，必要时给出示例；\n" +
               "- 回复长度适中，避免冗长。\n\n" +
               "（当前为 Mock 模式模拟生成，配置 Agents:ApiKey 后可接入真实模型自动生成更贴合的角色设定。）";
    }
}
