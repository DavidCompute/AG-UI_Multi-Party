using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>记录输入消息的屏蔽 IChatClient：用于观察 MSAGENT 恢复时把哪些消息传给 provider（是否含历史 assistant tool_call）。</summary>
public sealed class RecordingMockClient : IChatClient
{
    public List<List<ChatMessage>> Calls = new();
    private readonly Func<int, IList<AIContent>> _respond;

    public RecordingMockClient(Func<int, IList<AIContent>> respond) => _respond = respond;

    public object? GetService(Type serviceType, object? serviceKey = null)
        => serviceType.IsInstanceOfType(this) ? this : null;

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var snapshot = messages.ToList();
        Calls.Add(snapshot);
        int callIdx = Calls.Count - 1;
        var contents = _respond(callIdx) ?? [];
        yield return new ChatResponseUpdate(ChatRole.Assistant, contents: contents);
        yield break;
    }

    public void Dispose() { }
}

public sealed class ClientToolResumeTests
{
    [Fact]
    public async Task CreatingApprovalResponse_WithStoredClientResult_DeliversRealToolResult_ToModel()
    {
        // 客户端技能占位函数从 ClientToolResultStore 读取前端回传结果（无参：模型调用时无需绑定参数，避免参数缺失→Function failed）
        var tool = new ApprovalRequiredAIFunction(
            AIFunctionFactory.Create(() =>
                Task.FromResult(AguiGroupChat.Agents.ClientToolResultStore.ConsumeOrDefault("sk_hostname")
                    ?? "客户端执行（占位）"), "sk_hostname", "查询主机名"));
        var mock = new RecordingMockClient(idx =>
            idx == 0
                ? new AIContent[] { new FunctionCallContent("call_1", "sk_hostname", null) }
                : new AIContent[] { new TextContent("已获取。") });
        var agent = new ChatClientAgent(mock, "probe", null, null, new[] { tool }, NullLoggerFactory.Instance, new ServiceCollection().BuildServiceProvider());
        var session = await agent.CreateSessionAsync();

        AguiGroupChat.Agents.ClientToolResultStore.Clear();

        // 第一轮触发中断
        var first = new List<AgentResponseUpdate>();
        await foreach (var u in agent.RunStreamingAsync(new ChatMessage(ChatRole.User, "hostname?"), session))
            first.Add(u);
        var approval = Assert.Single(first.SelectMany(u => u.Contents).OfType<ToolApprovalRequestContent>());
        var fc = Assert.IsType<FunctionCallContent>(approval.ToolCall);
        Assert.Equal("sk_hostname", fc.Name);

        // 恢复：先写入前端回传的真实结果，再用 CreateResponse(true)（批准后 MSAGENT 执行占位函数读取该结果）
        AguiGroupChat.Agents.ClientToolResultStore.Put("sk_hostname", "DESKTOP-PROBE-123");
        var resume = new List<AgentResponseUpdate>();
        await foreach (var u in agent.RunStreamingAsync(
            new ChatMessage(ChatRole.User, [approval.CreateResponse(approved: true)]), session))
            resume.Add(u);

        // MSAGENT 批准后执行占位函数 → 应产生带真实结果的 FunctionResultContent，模型据此继续
        var fr = Assert.Single(resume.SelectMany(u => u.Contents).OfType<FunctionResultContent>());
        Assert.Equal("DESKTOP-PROBE-123", fr.Result?.ToString());
    }
}
