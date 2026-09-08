using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace AguiGroupChat.Agents;

/// <summary>
/// 沉淀文档「模型润色」（1.3 后续增强）：把确定性拼接的群关键结论原文，交给模型整理成
/// 结构化条目（关键结论 / 已定决策 / 待办与负责人 / 未决问题 / 关键背景），更利于日后绑定检索与阅读。
/// mock 提供方 / 未配置 API Key 时 <see cref="CanPolish"/> 返回 false（调用方直接用原文）；
/// 润色失败 / 输出为空同样回退原文，绝不阻断沉淀主流程。
/// </summary>
public sealed class ConclusionPolisher
{
    private readonly AgentOptions _options;
    private readonly ILogger<ConclusionPolisher> _logger;

    public ConclusionPolisher(AgentOptions options, ILogger<ConclusionPolisher> logger)
    {
        _options = options;
        _logger = logger;
    }

    /// <summary>当前是否具备润色条件（真实提供方 + 已配置 API Key）。</summary>
    public bool CanPolish
        => !string.Equals(_options.Provider, "mock", StringComparison.OrdinalIgnoreCase)
           && !string.IsNullOrWhiteSpace(_options.ApiKey);

    /// <summary>润色一段沉淀原文；失败 / 无可用模型返回 null（调用方回退原文）。</summary>
    public async Task<string?> PolishAsync(string rawMarkdown, CancellationToken ct)
    {
        if (!CanPolish || string.IsNullOrWhiteSpace(rawMarkdown)) return null;
        try
        {
            var isDeepSeek = string.Equals(_options.Provider, "deepseek", StringComparison.OrdinalIgnoreCase);
            var ov = AgentCatalog.StructuredFastModel(isDeepSeek); // 结构化整理属格式任务：不进 reasoner，速度快、稳定
            using var client = AgentCatalog.BuildOpenAIChatClient(
                _options, new AgentDefinition { AgentId = "conclusion_polish", Nickname = "结论整理器" }, isDeepSeek, ov).AsIChatClient();
            var prompt =
                "你是群聊关键结论的整理员。下面是一段从群对话里按时间抽取的「关键级记忆」原文（可能包含口语、重复与闲聊），" +
                "请把它整理成一份结构清晰、便于日后检索的知识文档，Markdown 格式：\n" +
                "1. 以 `# 群关键结论` 开头；\n" +
                "2. 分节整理：`## 已确认结论`（共识/决定）、`## 待办事项`（含负责人，如有）、`## 未决问题`、`## 关键背景`（必要的事实/上下文）；\n" +
                "3. 语言与原文一致；不要编造原文没有的信息；不要复述我的指令；\n" +
                "4. 只输出整理后的文档正文，不要 ``` 围栏、不要前后缀说明。\n\n【原文】\n" + UntrustedBoundary.Wrap(rawMarkdown);
            var resp = await client.GetResponseAsync([new ChatMessage(ChatRole.User, prompt)], cancellationToken: ct);
            var text = (resp.Text ?? "").Trim();
            if (text.Length == 0) return null;
            return text.Length > 12_000 ? text[..12_000] : text;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "沉淀文档润色失败（回退原文）");
            return null;
        }
    }
}
