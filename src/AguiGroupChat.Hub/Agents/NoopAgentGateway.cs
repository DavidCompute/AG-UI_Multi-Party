namespace AguiGroupChat.Hub.Agents;

/// <summary>
/// 默认空实现：不发起真实 AG-UI 调用，仅记录日志。
/// 接入真实 AG-UI 网关时替换此实现（DI 注册处 Program.cs）。
/// </summary>
public sealed class NoopAgentGateway : IAgentGateway
{
    private readonly ILogger<NoopAgentGateway> _logger;

    public NoopAgentGateway(ILogger<NoopAgentGateway> logger) => _logger = logger;

    public Task<AgentInvocationResult> InvokeAsync(AgentInvocationContext context, CancellationToken ct)
    {
        _logger.LogInformation(
            "【预留接口】收到智能体调用请求（未接入真实 AG-UI）：{AgentId} @ {GroupId}，触发消息 {MessageId}",
            context.AgentId, context.GroupId, context.TriggerMessageId);
        return Task.FromResult(new AgentInvocationResult(false, null, "AGENT_GATEWAY_NOT_CONFIGURED"));
    }

    public Task<bool> IsAvailableAsync(string agentId, CancellationToken ct)
        => Task.FromResult(false);

    public Task<bool> ResolveInteractionAsync(string interruptId, string memberId, bool approved, string? input, System.Text.Json.JsonElement? payload, CancellationToken ct, bool approveAll = false)
        => Task.FromResult(false); // 空实现：不存在任何待决策的交互请求

    public bool StopRun(string runId, string operatorId, string groupId, bool isManager)
        => false; // 空实现：无活跃运行
}
