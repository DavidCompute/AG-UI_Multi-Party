using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace AguiGroupChat.Agents;

/// <summary>
/// 根据群成员生成群名称（6-12 字）。创建群时用户不填群名可调用：
/// Provider=mock 时输出确定性模板（无模型调用），便于本地演示与测试。
/// </summary>
public static class GroupNameGenerator
{
    /// <summary>生成群名。真实模型走 OpenAI 兼容接口；mock 走模板。</summary>
    public static async Task<string> GenerateAsync(AgentOptions options, IReadOnlyList<string> memberNames, ILogger logger, CancellationToken ct)
    {
        var names = (memberNames ?? [])
            .Select(n => (n ?? "").Trim())
            .Where(n => n.Length > 0)
            .Take(8)
            .ToList();
        if (names.Count == 0) throw new InvalidOperationException("至少需要 1 个成员名称");

        if (string.Equals(options.Provider, "mock", StringComparison.OrdinalIgnoreCase))
        {
            // mock：确定性模板（无需 API Key，便于本地演示与测试）
            return BuildTemplate(names);
        }

        IChatClient client;
        try
        {
            var isDeepSeek = string.Equals(options.Provider, "deepseek", StringComparison.OrdinalIgnoreCase);
            client = AgentCatalog.BuildOpenAIChatClient(
                options, new AgentDefinition { AgentId = "group_name_gen", Nickname = "群名生成器" }, isDeepSeek).AsIChatClient();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("群名生成需要可用的模型配置（" + ex.Message + "）", ex);
        }

        try
        {
            var members = string.Join("、", names);
            var prompt =
                "你是群聊群名生成器。根据群成员昵称，为这个群取一个贴切的名字。\n" +
                "要求：6-12 个汉字（不含标点与空格），能体现成员构成或群的用途/主题，简洁好记；\n" +
                "只输出群名本身，不要任何解释、引号或前后缀。\n\n" +
                $"群成员：{members}";

            var resp = await client.GetResponseAsync([new ChatMessage(ChatRole.User, prompt)], cancellationToken: ct);
            var text = (resp.Text ?? "").Trim().Trim('"', '「', '」', '“', '”', '《', '》');
            if (string.IsNullOrWhiteSpace(text))
                throw new InvalidOperationException("群名生成返回为空，请重试");
            text = Normalize(text);
            // 日志内容截断：成员昵称列表可能很长（昵称属用户数据），只记录前 100 字符
            var membersSummary = members.Length > 100 ? members[..100] + "…" : members;
            logger.LogInformation("已生成群名（{Len} 字）：{Name}（成员：{Members}）", text.Length, text, membersSummary);
            return text;
        }
        finally
        {
            client.Dispose();
        }
    }

    /// <summary>清理模型输出：去掉标点 / 空白，截断到 12 字；过短时以通用后缀补齐到至少 6 字。</summary>
    private static string Normalize(string name)
    {
        var chars = name.Where(c => !char.IsWhiteSpace(c) && !char.IsPunctuation(c)).Take(12).ToArray();
        if (chars.Length < 6)
        {
            var suffix = "交流群";
            return new string(chars) + suffix[..Math.Min(suffix.Length, 6 - chars.Length)];
        }
        return new string(chars);
    }

    /// <summary>mock 模式的确定性模板：取前两个成员昵称 + 主题后缀，截断 / 补齐到 6-12 字。</summary>
    private static string BuildTemplate(IReadOnlyList<string> names)
    {
        var head = string.Concat(names.Take(2));
        return Normalize(head + "协作群");
    }
}
