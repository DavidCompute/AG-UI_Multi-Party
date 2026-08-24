using AguiGroupChat.Hub.Agents;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace AguiGroupChat.Agents.Tools;

/// <summary>
/// 技能调用器（MSAGENT 智能体间调用）：把目标智能体作为可调用子代理，经框架
/// <see cref="AgentSession"/> 执行一次 run 并返回其回复文本。由 AgentCatalog 为每个
/// 已配置技能创建，包装为 AIFunction 挂到宿主智能体的工具列表，模型按需调用。
/// 任一失败返回错误文本，不影响宿主智能体主流程。
/// </summary>
internal sealed class AgentSkillCall
{
    private readonly ChatClientAgent _agent;
    private readonly string _targetAgentId;
    private readonly string _targetNickname;
    private readonly string _skillId;
    private readonly ILogger _logger;

    /// <summary>链路展示文本截断长度（避免把整段长答复塞进消息元数据）。</summary>
    private const int MaxChainTextChars = 600;

    public AgentSkillCall(ChatClientAgent agent, string targetAgentId, string targetNickname, string? skillId, ILoggerFactory loggerFactory)
    {
        _agent = agent;
        _targetAgentId = targetAgentId;
        _targetNickname = targetNickname;
        _skillId = skillId ?? "";
        _logger = loggerFactory.CreateLogger<AgentSkillCall>();
    }

    private static string Truncate(string? s, int max)
        => string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s[..max] + "…";

    /// <summary>以 query 作为用户消息调用目标智能体，返回其回复文本。</summary>
    public async Task<string> InvokeAsync(string query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query)) return "查询内容为空。";
        try
        {
            // 子智能体以技能方式运行：把 ambient 上下文切到目标智能体（AgentId=目标），
            // 使 MemoryContextProvider 注入的是目标智能体自己的知识库 / 群记忆，而不是宿主的。
            // 原来直接复用宿主的 ambient 上下文，会导致“技能激活带知识库的智能体2”却检索了智能体1的知识库。
            var prev = AgentGateway.AmbientContext.Value;
            if (prev is not null && !string.Equals(prev.AgentId, _targetAgentId, StringComparison.Ordinal))
            {
                AgentGateway.AmbientContext.Value = prev with
                {
                    AgentId = _targetAgentId,
                    AgentNickname = _targetNickname,
                    Content = query,
                };
            }
            try
            {
                // 链路可视化：记录本次技能调用到链构造器（嵌套调用会自动成为子节点）
                var chain = SkillChainBuilder.Ambient.Value;
                ChainNode? node = null;
                if (chain is not null)
                {
                    node = chain.Push(new ChainNode
                    {
                        AgentId = _targetAgentId,
                        AgentNickname = _targetNickname,
                        SkillId = _skillId,
                        Query = Truncate(query, MaxChainTextChars),
                    });
                }
                try
                {
                    var session = await _agent.CreateSessionAsync(ct);
                    // 以技能方式调用子智能体：附加强化指令，促使其在需要时调用自身的子技能、
                    // 并把子技能检索到的结论写入最终答复，确保多跳技能链（A→B→C）的结论逐层回传。
                    var message = "请你就以下请求给出答复。"
                        + "如果有任何你掌握的下游子技能（specialist/skill）能更准确地回答，请先调用它们，"
                        + $"并把它们的结论清晰地包含在你的最终回复中，不要只写“已调用”或“请查阅”。\n\n请求：{query}";
                    var response = await _agent.RunAsync([new ChatMessage(ChatRole.User, message)], session, null, ct);
                    var text = ExtractResponseText(response);
                    if (node is not null) node.Result = Truncate(text, MaxChainTextChars);
                    return string.IsNullOrWhiteSpace(text) ? "（子智能体未返回内容）" : text;
                }
                finally
                {
                    if (node is not null) chain!.Pop(); // 回退到父级作用域
                }
            }
            finally
            {
                AgentGateway.AmbientContext.Value = prev;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "技能调用失败：{Query}", query);
            return "技能调用失败：" + ex.Message;
        }
    }

    /// <summary>稳健提取子智能体最终答复文本：优先 <see cref="AgentResponse.Text"/>，
    /// 部分 OpenAI 兼容客户端（含推理模型）不填充该字段，回退到最终 assistant 消息的文本内容。</summary>
    private static string ExtractResponseText(Microsoft.Agents.AI.AgentResponse response)
    {
        var text = response.Text;
        if (!string.IsNullOrWhiteSpace(text)) return text;
        // 消息最后一条 assistant 消息的 Text 或其 TextContent
        var msgs = response.Messages;
        for (var i = msgs.Count - 1; i >= 0; i--)
        {
            var m = msgs[i];
            if (m.Role != ChatRole.Assistant) continue;
            var mt = m.Text;
            if (!string.IsNullOrWhiteSpace(mt)) return mt;
            foreach (var c in m.Contents)
                if (c is TextContent tc && !string.IsNullOrWhiteSpace(tc.Text)) return tc.Text;
        }
        return "";
    }
}
