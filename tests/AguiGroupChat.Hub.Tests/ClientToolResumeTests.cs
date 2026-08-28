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
    public async Task Resume_WithCreateResponseAndInjectedResult_DeliversResultToModel()
    {
        // 客户端技能占位函数（不再依赖它：恢复改由 BuildResumeMessage 直接注入 User 消息，规避 MSAGENT 不执行 stub 的问题）
        var tool = new ApprovalRequiredAIFunction(
            AIFunctionFactory.Create(() =>
                Task.FromResult("客户端执行（占位）"), "sk_hostname", "查询主机名"));
        // 模拟：首轮返回工具调用；恢复后的模型续答检查输入里是否有注入的前端结果文本
        var callsHolder = new List<List<ChatMessage>>();
        var mock = new RecordingMockClient(idx =>
        {
            if (idx == 0)
                return new AIContent[] { new FunctionCallContent("call_1", "sk_hostname", null) };
            var msgs = callsHolder[idx];
            var hasInjected = msgs.Any(m => m.Text?.Contains("DESKTOP-PROBE-123") == true);
            return new AIContent[] { new TextContent("sawInjected=" + (hasInjected ? "TRUE" : "FALSE")) };
        });
        mock.Calls = callsHolder; // 让 mock 把收到的输入写进同一容器，供上述闭包检查
        var agent = new ChatClientAgent(mock, "probe", null, null, new[] { tool }, NullLoggerFactory.Instance, new ServiceCollection().BuildServiceProvider());
        var session = await agent.CreateSessionAsync();

        // 第一轮触发中断
        var first = new List<AgentResponseUpdate>();
        await foreach (var u in agent.RunStreamingAsync(new ChatMessage(ChatRole.User, "hostname?"), session))
            first.Add(u);
        var approval = Assert.Single(first.SelectMany(u => u.Contents).OfType<ToolApprovalRequestContent>());

        // 恢复：按 BuildResumeMessage 的新行为——先 CreateResponse(true)，再追加一条 User 消息把前端结果直接注入模型上下文（含回归校验指令）
        var resumeInput = new ChatMessage[]
        {
            new(ChatRole.User, [approval.CreateResponse(approved: true)]),
            new ChatMessage(ChatRole.User, "[前端工具] sk_hostname 已在本机执行完毕，下面是它返回的数据：\nDESKTOP-PROBE-123\n\n请先对这份数据进行回归校验，再作答。"),
        };
        var resumeTexts = new List<string>();
        await foreach (var u in agent.RunStreamingAsync(resumeInput, session))
            if (u.Text is { Length: > 0 } t) resumeTexts.Add(t);

        // 关键：模型续答调用收到的输入里包含注入的前端结果（DESKTOP-PROBE-123）
        Assert.Contains(resumeTexts, t => t.Contains("sawInjected=TRUE"));
        var last = mock.Calls[^1];
        Assert.Contains(last, m => m.Role == ChatRole.User && m.Text?.Contains("DESKTOP-PROBE-123") == true);
    }
}
