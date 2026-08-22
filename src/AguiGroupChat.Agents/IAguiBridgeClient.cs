using System.Text.Json;

namespace AguiGroupChat.Agents;

/// <summary>
/// AG-UI 桥接传输接口：WebSocket 与 HTTP(S) 两种传输的统一抽象，
/// 由 <see cref="AgentGateway"/> 按端点 scheme（ws/wss ↔ http/https）选择实现。
/// </summary>
public interface IAguiBridgeClient : IAsyncDisposable
{
    /// <summary>建立与外部服务的连接（HTTP 传输为无状态，仅校验端点可用性）。</summary>
    Task ConnectAsync(string agentId, CancellationToken ct);

    /// <summary>发送用户消息（standard：USER_MESSAGE；hub：订阅 + GROUP_MESSAGE_SEND）。
    /// runId：standard 方言直接作为上行 RunAgentInput 的 runId——必须与恢复时
    /// <see cref="ResumeInteractionAsync"/> 传回的 runId 一致，外部服务才能关联到被中断的运行。</summary>
    Task SendUserMessageAsync(string messageId, string threadId, string runId, string content, string groupId, string agentId, CancellationToken ct);

    /// <summary>接收外部回复事件流。</summary>
    IAsyncEnumerable<AguiBridgeEvent> ReceiveAsync(CancellationToken ct);

    /// <summary>
    /// 人机交互恢复（协议 4.5）：触发者决策后向外部服务发送恢复指令（standard：RunAgentInput+resume；
    /// hub：AGENT_INTERACTION_RESOLVE / HTTP 等效接口），外部服务随后继续推送回复事件。
    /// <summary>人机交互恢复：standard → 上行 RunAgentInput + resume 数组；hub → AGENT_INTERACTION_RESOLVE。
    /// toolCallId / toolName / toolArguments：standard 方言审批恢复需回传被批准的工具调用信息（AGUIToolApprovalResumePayload）；
    /// input / inputField：请求用户输入型中断（kind=input）时回传用户文本，payload 以 inputField 为键；
    /// payload：kind=input 按 responseSchema 提交的完整对象（单选 / 多选 / 数字 / 多字段）——优先于 input。</summary>
    Task ResumeInteractionAsync(string interruptId, string threadId, string runId, string groupId, bool approved, CancellationToken ct,
        string? toolCallId = null, string? toolName = null, JsonElement? toolArguments = null,
        string? input = null, string? inputField = null, JsonElement? payload = null);
}
