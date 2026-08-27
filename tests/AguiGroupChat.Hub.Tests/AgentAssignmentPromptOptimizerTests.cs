using AguiGroupChat.Agents;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>下一层任务指派指引生成（mock 确定性路径）+ 边界校验。</summary>
public sealed class AgentAssignmentPromptOptimizerTests
{
    private static AgentOptions Mock() => new() { Provider = "mock" };

    [Fact]
    public async Task GenerateAsync_Mock_IncludesEachSubordinateAndOnlyNextLayerSemantics()
    {
        var self = new AgentDefinition { AgentId = "it", Nickname = "IT服务台", Description = "一线IT入口" };
        var subs = new[]
        {
            new AgentDefinition { AgentId = "srv", Nickname = "服务器运维助手", Description = "服务器/Exchange" },
            new AgentDefinition { AgentId = "desk", Nickname = "桌面运维助手", Description = "终端/Office" },
        };
        var guidance = await AgentAssignmentPromptOptimizer.GenerateAsync(Mock(), self, subs, NullLogger.Instance, CancellationToken.None);
        Assert.Contains("下一层任务指派指引", guidance);
        Assert.Contains("服务器运维助手", guidance);
        Assert.Contains("桌面运维助手", guidance);
        Assert.Contains("直接下一层", guidance); // 明确“只看下一层”，不涉及更深层
        Assert.DoesNotContain("Exchange专家", guidance); // 不引入更深层叶子
    }

    [Fact]
    public async Task GenerateAsync_NoSubordinates_Throws()
    {
        var self = new AgentDefinition { AgentId = "leaf", Nickname = "叶节点" };
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => AgentAssignmentPromptOptimizer.GenerateAsync(Mock(), self, [], NullLogger.Instance, CancellationToken.None));
    }
}
