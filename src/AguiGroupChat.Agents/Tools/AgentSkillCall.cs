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
    private readonly ILogger _logger;

    public AgentSkillCall(ChatClientAgent agent, ILoggerFactory loggerFactory)
    {
        _agent = agent;
        _logger = loggerFactory.CreateLogger<AgentSkillCall>();
    }

    /// <summary>以 query 作为用户消息调用目标智能体，返回其回复文本。</summary>
    public async Task<string> InvokeAsync(string query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query)) return "查询内容为空。";
        try
        {
            var session = await _agent.CreateSessionAsync(ct);
            var response = await _agent.RunAsync([new ChatMessage(ChatRole.User, query)], session, null, ct);
            return string.IsNullOrWhiteSpace(response.Text) ? "（子智能体未返回内容）" : response.Text;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "技能调用失败：{Query}", query);
            return "技能调用失败：" + ex.Message;
        }
    }
}
