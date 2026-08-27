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
        // 模拟：首轮返回工具调用；恢复后的模型续答根据输入里是否含前端结果给出「看到了 / 没看到」
        var callsHolder = new List<List<ChatMessage>>();
        var mock = new RecordingMockClient(idx =>
        {
            if (idx == 0)
                return new AIContent[] { new FunctionCallContent("call_1", "sk_hostname", null) };
            // 检查本轮输入里模型能否看到 FunctionResultContent
            var msgs = callsHolder[idx];
            var hasResult = msgs.SelectMany(x => x.Contents).Any(c =>
                c is FunctionResultContent r && r.Result?.ToString()?.Contains("DESKTOP-PROBE-123") == true);
            return new AIContent[] { new TextContent("sawResult=" + (hasResult ? "TRUE" : "FALSE")) };
        });
        mock.Calls = callsHolder; // 让 mock 把收到的输入写进同一容器，供上述闭包检查
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
        var resumeTexts = new List<string>();
        await foreach (var u in agent.RunStreamingAsync(
            new ChatMessage(ChatRole.User, [approval.CreateResponse(approved: true)]), session))
            if (u.Text is { Length: > 0 } t) resumeTexts.Add(t);

        // 关键：MSAGENT 在工具执行后应带 FunctionResult 再次回调 provider，模型续答能「看到」前端结果而非只见工具调用
        Assert.Contains(resumeTexts, t => t.Contains("sawResult=TRUE"));

        // 工具执行产生带真实结果的 FunctionResultContent
        var last = mock.Calls[^1];
        Assert.Contains(last.SelectMany(x => x.Contents), c =>
            c is FunctionResultContent r && r.Result?.ToString()?.Contains("DESKTOP-PROBE-123") == true);
    }
}
