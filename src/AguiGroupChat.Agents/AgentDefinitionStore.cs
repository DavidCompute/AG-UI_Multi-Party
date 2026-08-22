using AguiGroupChat.Hub.Agents;

namespace AguiGroupChat.Agents;

/// <summary>包装 <see cref="AgentCatalog"/> 的只读归属查询（GroupHub 校验私密智能体归属用）。</summary>
public sealed class AgentDefinitionStore : IAgentDefinitionStore
{
    private readonly AgentCatalog _catalog;

    public AgentDefinitionStore(AgentCatalog catalog) => _catalog = catalog;

    public AgentDefinitionInfo? GetDefinition(string agentId)
    {
        var def = _catalog.GetDefinition(agentId);
        return def is null ? null : new AgentDefinitionInfo(def.AgentId, def.Nickname, def.IsPrivate, def.OwnerId, def.EnableWorkTools);
    }
}
