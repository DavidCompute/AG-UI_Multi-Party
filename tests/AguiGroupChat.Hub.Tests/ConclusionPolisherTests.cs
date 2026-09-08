using AguiGroupChat.Agents;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>沉淀文档「模型润色」开关行为测试（不发起真实模型调用）。</summary>
public sealed class ConclusionPolisherTests
{
    private static ConclusionPolisher New(string provider, string? apiKey)
        => new(new AgentOptions { Provider = provider, ApiKey = apiKey }, NullLogger<ConclusionPolisher>.Instance);

    [Theory]
    [InlineData("mock", "sk-x")]       // mock 提供方：即使配了 Key 也不润色（保持本地确定性）
    [InlineData("deepseek", "")]       // 真实提供方但缺 Key：不可润色
    [InlineData("", null)]
    public void CanPolish_OnlyWhenRealProviderWithKey(string provider, string? apiKey)
    {
        Assert.False(New(provider, apiKey).CanPolish);
    }

    [Fact]
    public void CanPolish_RealProviderWithKey_True()
    {
        Assert.True(New("deepseek", "sk-x").CanPolish);
    }

    [Fact]
    public async Task PolishAsync_SkipsWhenNotAvailable_ReturnsNull()
    {
        var polisher = New("mock", "sk-x");
        Assert.Null(await polisher.PolishAsync("# 原文", CancellationToken.None));
    }
}
