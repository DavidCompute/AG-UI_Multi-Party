namespace AguiGroupChat.Hub.Agents;

/// <summary>
/// 智能体定义的只读查询（私密智能体的归属校验用）。实现见 AguiGroupChat.Agents
/// （包装 AgentCatalog）——与 IMessageMemory 相同：接口定义在 Hub，实现在 Agents，避免 Hub 反向依赖。
/// </summary>
public interface IAgentDefinitionStore
{
    /// <summary>查询智能体归属信息；未注册返回 null。</summary>
    AgentDefinitionInfo? GetDefinition(string agentId);
}

/// <summary>智能体归属信息（轻量投影）。</summary>
/// <param name="OwnerId">创建者 userId（种子 / appsettings 声明为 null = 系统级）。</param>
/// <param name="EnableWorkTools">是否工作型智能体（讨论 / 调度接入计划-执行用）。</param>
public sealed record AgentDefinitionInfo(string AgentId, string Nickname, bool IsPrivate, string? OwnerId, bool EnableWorkTools = false);
