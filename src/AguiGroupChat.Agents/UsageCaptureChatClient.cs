using Microsoft.Extensions.AI;
using OpenAI.Chat;
using AIChatMessage = Microsoft.Extensions.AI.ChatMessage;
using AIChatOptions = Microsoft.Extensions.AI.ChatOptions;

namespace AguiGroupChat.Agents;

/// <summary>
/// IChatClient 装饰器：在模型调用流式输出处捕获 token 用量（OpenAI 兼容端的 usage 帧），
/// 立即经 <see cref="AgentUsageService"/> 落库（统计 / 配额）。挂在 ChatClientAgent 管道的最底层
/// （AsAIAgent 的 clientFactory），避免上层管道（StreamingUpdatePipelineResponse）丢弃 RawRepresentation。
/// 触发者身份从 <see cref="AgentGateway.AmbientContext"/>（AsyncLocal）读取——仅智能体网关驱动时记录，
/// 外部直接调用 / 无业务上下文时不记录。
/// </summary>
internal sealed class UsageCaptureChatClient : IChatClient
{
    private readonly IChatClient _inner;
    private readonly Lazy<AguiGroupChat.Hub.Agents.AgentUsageService?> _usage;

    public UsageCaptureChatClient(IChatClient inner, Lazy<AguiGroupChat.Hub.Agents.AgentUsageService?> usage)
    {
        _inner = inner;
        _usage = usage;
    }

    public Task<ChatResponse> GetResponseAsync(IEnumerable<AIChatMessage> messages, AIChatOptions? options = null, CancellationToken ct = default)
        => CaptureNonStreamingAsync(_inner.GetResponseAsync(messages, options, ct), ct);

    private async Task<ChatResponse> CaptureNonStreamingAsync(Task<ChatResponse> task, CancellationToken ct)
    {
        var response = await task;
        if (response.RawRepresentation is ChatCompletion { Usage: { } u })
            Record(u.InputTokenCount, u.OutputTokenCount, u.OutputTokenDetails?.ReasoningTokenCount ?? 0);
        return response;
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<AIChatMessage> messages, AIChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        // 暂存最后一个 usage 帧，流结束时提交一次（避免同一运行的多个 usage 帧重复累计）
        (long Input, long Output, long Reasoning)? pending = null;
        await foreach (var update in _inner.GetStreamingResponseAsync(messages, options, ct))
        {
            // 流式 usage 帧（OpenAI include_usage）：RawRepresentation 在此层仍是原始类型，管道之上会被转换丢弃
            if (update.RawRepresentation is StreamingChatCompletionUpdate { Usage: { } u })
                pending = (u.InputTokenCount, u.OutputTokenCount, u.OutputTokenDetails?.ReasoningTokenCount ?? 0);
            yield return update;
        }
        if (pending is { } p) Record(p.Input, p.Output, p.Reasoning);
    }

    private void Record(long input, long output, long reasoning)
    {
        if (_usage.Value is not { } usage) return;
        var ctx = AgentGateway.AmbientContext.Value;
        if (ctx is null) return; // 非网关驱动（如技能子代理经网关间接驱动仍带上下文）
        usage.RecordUsage(ctx.AgentId, ctx.TriggerUserId, input, output, reasoning);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => _inner.GetService(serviceType, serviceKey);

    public void Dispose() => _inner.Dispose();
}
