using AguiGroupChat.Agents;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>
/// 智能体网关配置测试：Provider 解析、API Key 来源优先级（显式配置 > DEEPSEEK_API_KEY > OPENAI_API_KEY）。
/// </summary>
public sealed class AgentConfigTests
{
    private static AgentOptions BindOptions(params (string Key, string? Value)[] entries)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(entries.ToDictionary(e => e.Key, e => e.Value, StringComparer.OrdinalIgnoreCase))
            .Build();
        var services = new ServiceCollection();
        services.AddAgentFramework(config);
        return services.BuildServiceProvider().GetRequiredService<AgentOptions>();
    }

    [Fact]
    public void AgentHosting_ResolvesApiKeyFromDeepSeekEnvVar()
    {
        var options = BindOptions(
            ("Agents:Provider", "deepseek"),
            ("DEEPSEEK_API_KEY", "sk-deepseek-env"));

        Assert.Equal("deepseek", options.Provider);
        Assert.Equal("sk-deepseek-env", options.ApiKey);
    }

    [Fact]
    public void AgentHosting_FallsBackToOpenAiEnvVar()
    {
        var options = BindOptions(
            ("Agents:Provider", "openai"),
            ("OPENAI_API_KEY", "sk-openai-env"));

        Assert.Equal("sk-openai-env", options.ApiKey);
    }

    [Fact]
    public void AgentHosting_ExplicitConfigWinsOverEnvVar()
    {
        var options = BindOptions(
            ("Agents:Provider", "deepseek"),
            ("Agents:ApiKey", "sk-explicit"),
            ("DEEPSEEK_API_KEY", "sk-env"));

        Assert.Equal("sk-explicit", options.ApiKey);
    }

    [Fact]
    public void AgentCatalog_DeepSeekProvider_CreatesAgentWithoutKeyOverride()
    {
        var options = new AgentOptions
        {
            Provider = "deepseek",
            ApiKey = "sk-test",
            Agents =
            [
                new AgentDefinition
                {
                    AgentId = "agent_ds",
                    Nickname = "DeepSeek 助手",
                    Description = "测试",
                    Instructions = "你是 DeepSeek 助手",
                },
            ],
        };
        var catalog = new AgentCatalog(options, NullLoggerFactory.Instance, new ServiceCollection().BuildServiceProvider());

        var agent = catalog.GetOrCreate("agent_ds");

        Assert.NotNull(agent);
        Assert.Equal("DeepSeek 助手", agent.Name);
        Assert.Equal("测试", agent.Description);
    }

    [Fact]
    public void AgentCatalog_DeepSeekProvider_ThrowsWhenApiKeyMissing()
    {
        var options = new AgentOptions
        {
            Provider = "deepseek",
            Agents =
            [
                new AgentDefinition { AgentId = "agent_ds", Nickname = "D", Instructions = "x" },
            ],
        };
        var catalog = new AgentCatalog(options, NullLoggerFactory.Instance, new ServiceCollection().BuildServiceProvider());

        var ex = Assert.Throws<InvalidOperationException>(() => catalog.GetOrCreate("agent_ds"));
        Assert.Contains("API Key", ex.Message);
    }
}
