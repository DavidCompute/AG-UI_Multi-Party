using AguiGroupChat.Agents;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>智能体 / 技能市场（3.3）测试：目录 / 一键导入 / agentId 冲突改 ID。</summary>
public sealed class MarketplaceTests
{
    private static (MarketplaceService Market, AgentCatalog Catalog) Create()
    {
        var options = new AgentOptions { Provider = "mock" };
        var catalog = new AgentCatalog(options, NullLoggerFactory.Instance, new ServiceCollection().BuildServiceProvider());
        return (new MarketplaceService(catalog), catalog);
    }

    [Fact]
    public void Packs_ListsBuiltin()
    {
        var (market, _) = Create();
        var packs = market.Packs();
        Assert.NotEmpty(packs);
        Assert.Contains(packs, p => p.PackId == "business-legal");
        Assert.All(packs, p => Assert.NotEmpty(p.Agents));
    }

    [Fact]
    public void ImportPack_CreatesAgentsOwnedByUser()
    {
        var (market, catalog) = Create();
        var result = market.ImportPack("business-legal", "user_1");
        Assert.Equal(2, result.AgentsCreated);

        var def = catalog.GetDefinition("agent_legal_counsel");
        Assert.NotNull(def);
        Assert.Equal("user_1", def!.OwnerId);
        Assert.Equal("法律顾问", def.Nickname);
    }

    [Fact]
    public void ImportPack_Conflict_RenamesNewAgentId()
    {
        var (market, catalog) = Create();
        // 先手动占用一个 agentId
        catalog.Upsert(new AgentDefinition { AgentId = "agent_legal_counsel", Nickname = "已有", Instructions = "" });
        var result = market.ImportPack("business-legal", "user_1");
        Assert.Equal(2, result.AgentsCreated);
        // 冲突的智能体被改 ID，未覆盖已有
        Assert.Equal("已有", catalog.GetDefinition("agent_legal_counsel")!.Nickname);
        Assert.NotNull(catalog.GetDefinition("agent_legal_counsel_2"));
    }

    [Fact]
    public void ImportPack_UnknownPack_Throws()
        => Assert.Throws<AguiGroupChat.Hub.Infra.AguiProtocolException>(() => Create().Market.ImportPack("nope", "user_1"));
}
