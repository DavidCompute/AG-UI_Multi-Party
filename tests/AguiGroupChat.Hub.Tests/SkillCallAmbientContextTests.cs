using AguiGroupChat.Agents;
using AguiGroupChat.Agents.Tools;
using AguiGroupChat.Hub.Agents;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>
/// 技能调用（智能体间）时 ambient 上下文切换测试：技能激活带知识库的智能体 2 时，
/// MemoryContextProvider 应注入智能体 2 的知识库（AgentId=目标），而不是宿主的。
/// </summary>
public sealed class SkillCallAmbientContextTests
{
    /// <summary>记录 run 期间读取到的 ambient AgentId，用于断言上下文是否切到目标智能体。</summary>
    private sealed class RecordingChatClient : IChatClient
    {
        public string? ObservedAgentId;

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            ObservedAgentId = AgentGateway.AmbientContext.Value?.AgentId;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "技能目标回复")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ObservedAgentId = AgentGateway.AmbientContext.Value?.AgentId;
            await Task.CompletedTask;
            yield return new ChatResponseUpdate(ChatRole.Assistant, "技能目标回复");
        }

        public void Dispose() { }
    }

    [Fact]
    public async Task SkillInvoke_SwitchesAmbientContextToTargetAgent_ThenRestores()
    {
        var client = new RecordingChatClient();
        var target = new ChatClientAgent(client, "你是目标智能体", "目标", "描述", null, NullLoggerFactory.Instance,
            new ServiceCollection().BuildServiceProvider());
        var skillCall = new AgentSkillCall(target, "agent_target", "目标", "skill_x", NullLoggerFactory.Instance);

        // 宿主（智能体 1）正运行：ambient 指向宿主
        var prev = AgentGateway.AmbientContext.Value;
        var hostContext = new AgentInvocationContext(
            "g1", "t1", "agent_host", "宿主", "msg1", "user_1", "专享福利假是什么", [], false);
        AgentGateway.AmbientContext.Value = hostContext;
        try
        {
            var result = await skillCall.InvokeAsync("专享福利假是什么", CancellationToken.None);

            Assert.Equal("技能目标回复", result);
            // 目标智能体 run 期间：ambient 必须是目标智能体（这样才检索它的知识库）
            Assert.Equal("agent_target", client.ObservedAgentId);
            // run 结束后 ambient 已恢复为宿主
            Assert.Equal("agent_host", AgentGateway.AmbientContext.Value!.AgentId);
        }
        finally
        {
            AgentGateway.AmbientContext.Value = prev;
        }
    }
}
