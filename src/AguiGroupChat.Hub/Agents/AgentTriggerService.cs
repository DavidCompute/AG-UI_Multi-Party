using AguiGroupChat.Hub.Models;

namespace AguiGroupChat.Hub.Agents;

/// <summary>
/// 智能体触发规则评估（协议 §6）：
///   Mentioned   —— 消息 mentions 包含对应 agentId（或 mentionAll）时触发；
///   AllMessages —— 全量监听，接收所有群消息自行判断；
///   Keyword     —— 命中关键词时触发。
/// </summary>
public sealed class AgentTriggerService
{
    private readonly AgentRegistry _registry;

    public AgentTriggerService(AgentRegistry registry) => _registry = registry;

    public IReadOnlyList<AgentRegistration> Evaluate(GroupMessage msg)
    {
        var hits = new List<AgentRegistration>();
        foreach (var reg in _registry.ForGroup(msg.GroupId))
        {
            // 智能体自身发送的消息不触发自身
            if (reg.AgentId == msg.SenderId) continue;

            // 被 @（或 @全体）的智能体必定触发，不受触发模式限制；其余按注册的触发模式评估
            var matched = msg.MentionAll || msg.Mentions.Contains(reg.AgentId) || reg.TriggerMode switch
            {
                AgentTriggerMode.AllMessages => true,
                AgentTriggerMode.Keyword => reg.Keywords.Any(k => msg.Content.Contains(k, StringComparison.OrdinalIgnoreCase)),
                AgentTriggerMode.Contextual => true,
                _ => false,
            };
            if (matched) hits.Add(reg);
        }
        return hits;
    }
}
