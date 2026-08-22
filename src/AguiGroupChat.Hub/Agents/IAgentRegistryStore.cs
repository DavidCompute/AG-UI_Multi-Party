using AguiGroupChat.Hub.Models;

namespace AguiGroupChat.Hub.Agents;

/// <summary>
/// 群内智能体触发规则（<see cref="AgentRegistry"/>）的持久化存储（PostgreSQL 实现见
/// <c>AguiGroupChat.Hub.Persistence.Postgres.PostgresAgentStore</c>）。内存模式下为 null（不注入）。
/// </summary>
public interface IAgentRegistryStore
{
    IReadOnlyList<AgentRegistration> LoadAll();
    void Upsert(AgentRegistration registration);
    void Delete(string agentId, string? groupId);
}
