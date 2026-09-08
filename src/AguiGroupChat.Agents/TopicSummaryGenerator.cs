using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace AguiGroupChat.Agents;

/// <summary>
/// 话题滚动小结的正文生成：给定“此前小结（可选）+ 新一段对话”，产出更新后的紧凑摘要。
/// 与 <see cref="GroupNameGenerator"/> 同一模型接入模式：mock 走确定性模板（本地演示 / 测试无模型调用）。
/// </summary>
public static class TopicSummaryGenerator
{
    /// <summary>单条消息纳入摘要的字符上限。</summary>
    private const int MaxLineChars = 240;
    /// <summary>摘要输入总字符预算（超出从最旧部分截断）。</summary>
    private const int MaxInputChars = 6000;
    /// <summary>摘要输出字符上限（提示词也同步要求）。</summary>
    private const int MaxOutputChars = 700;

    /// <summary>
    /// 生成 / 更新话题小结。返回整理后的摘要文本。
    /// 真实模型走 OpenAI 兼容接口；mock 走确定性模板，便于本地演示与测试。
    /// </summary>
    public static async Task<string> GenerateAsync(
        AgentOptions options,
        string? previousSummary,
        IReadOnlyList<(string Who, string Text)> messages,
        ILogger logger,
        CancellationToken ct)
    {
        if (options is null) throw new ArgumentNullException(nameof(options));

        if (string.Equals(options.Provider, "mock", StringComparison.OrdinalIgnoreCase))
        {
            // mock：确定性模板（无需 API Key，便于本地演示与测试）
            return BuildTemplate(previousSummary, messages);
        }

        IChatClient client;
        try
        {
            var isDeepSeek = string.Equals(options.Provider, "deepseek", StringComparison.OrdinalIgnoreCase);
            var ov = AgentCatalog.StructuredFastModel(isDeepSeek);
            client = AgentCatalog.BuildOpenAIChatClient(
                options, new AgentDefinition { AgentId = "topic_summarizer", Nickname = "话题小结生成器" }, isDeepSeek, ov).AsIChatClient();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("话题小结需要可用的模型配置（" + ex.Message + "）", ex);
        }

        try
        {
            var transcript = BuildTranscript(messages);
            var prompt = new StringBuilder();
            prompt.AppendLine("你是群聊话题的“滚动小结”记录员。把一段对话压缩成紧凑的话题小结，供模型在长对话中回顾更早内容。");
            prompt.AppendLine("要求：");
            prompt.AppendLine("1) 输出 3-8 行中文（可按内容穿插少量术语原词）；");
            prompt.AppendLine("2) 提炼：当前结论 / 已定事项 / 待办与负责人 / 未决问题 / 关键背景；");
            prompt.AppendLine("3) 只输出小结正文，不要问候、不要解释、不要“以下是小结”之类的引导语；");
            prompt.AppendLine($"4) 正文不超过 {MaxOutputChars} 字。");
            if (!string.IsNullOrWhiteSpace(previousSummary))
                prompt.AppendLine("\n此前的小结（在其基础上增量更新，保留仍有效的结论）：\n" + previousSummary);
            prompt.AppendLine("\n新一段对话：\n" + transcript);

            var resp = await client.GetResponseAsync([new ChatMessage(ChatRole.User, prompt.ToString())], cancellationToken: ct);
            var text = (resp.Text ?? "").Trim().Trim('"', '「', '」', '“', '”', '\n', '\r', ' ', '：', ':');
            if (string.IsNullOrWhiteSpace(text))
                throw new InvalidOperationException("话题小结生成返回为空，请重试");
            if (text.Length > MaxOutputChars) text = text[..MaxOutputChars] + "…";
            return text;
        }
        finally
        {
            client.Dispose();
        }
    }

    /// <summary>把消息行整理成“发送者：内容”的按时间序文本，整体做字符预算保护。</summary>
    internal static string BuildTranscript(IReadOnlyList<(string Who, string Text)> messages)
    {
        var lines = new List<string>(messages.Count);
        var used = 0;
        foreach (var (who, textRaw) in messages)
        {
            var whoSafe = (who ?? "").Trim();
            if (whoSafe.Length > 30) whoSafe = whoSafe[..30];
            var text = textRaw ?? "";
            if (text.Length > MaxLineChars) text = text[..MaxLineChars] + "…";
            var line = (whoSafe.Length > 0 ? whoSafe + "：" : "") + text;
            if (used + line.Length > MaxInputChars) break; // 超出预算：从较旧处开始丢弃（messages 按时间序传入）
            lines.Add(line);
            used += line.Length;
        }
        return string.Join("\n", lines);
    }

    /// <summary>mock 模式的确定性模板：条数 + 首尾各一句，便于本地演示与测试断言。</summary>
    private static string BuildTemplate(string? previousSummary, IReadOnlyList<(string Who, string Text)> messages)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(previousSummary))
            parts.Add("承接此前小结：" + Truncate(previousSummary!, 200));
        parts.Add($"本次对话共 {messages.Count} 条。");
        if (messages.Count > 0)
        {
            var first = messages[0].Text.Trim();
            if (first.Length > 0) parts.Add("开头话题：" + Truncate(first, 80));
            var last = messages[^1].Text.Trim();
            if (last.Length > 0) parts.Add("当前进展：" + Truncate(last, 80));
        }
        return string.Join("｜", parts);
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
