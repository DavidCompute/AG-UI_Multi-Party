using System.Text.Json;
using AguiGroupChat.Hub.Models;

namespace AguiGroupChat.Hub.Agents;

/// <summary>一次智能体调用所需的上下文（由群消息触发规则产生，协议 §6）。</summary>
/// <param name="TriggerMode">群内生效的触发模式（注册表按群评估的结果）。
/// null 时由网关回退到角色定义（AgentDefinition.TriggerMode）。</param>
/// <param name="Visibility">触发消息的可见范围：智能体回复继承该可见性，定向 / 私密内容不会向全群广播。</param>
/// <param name="VisibleMemberIds">定向可见成员列表（配合 private 使用），随触发消息继承。</param>
public sealed record AgentInvocationContext(
    string GroupId,
    string ThreadId,
    string AgentId,
    string AgentNickname,
    string TriggerMessageId,
    string TriggerUserId,
    string Content,
    IReadOnlyList<string> Mentions,
    bool MentionAll,
    IReadOnlyList<AttachmentInfo>? Attachments = null,
    AgentTriggerMode? TriggerMode = null,
    string TopicId = "main",
    MessageVisibility Visibility = MessageVisibility.All,
    IReadOnlyList<string>? VisibleMemberIds = null,
    // 按客户端（机器）路由：触发请求希望把客户端 shell 执行到哪一台客户端（机器）。
    // 非空且该客户端有在线桥时，网关把客户端 shell 推给那台机器执行；否则回落到 agent/平台作用域。
    string? PreferredBridgeClient = null);

/// <summary>智能体调用的应答结果。</summary>
public sealed record AgentInvocationResult(bool Accepted, string? RunId, string? ErrorCode);

/// <summary>
/// 【预留接口】真实 AG-UI 调用与应答的接入点（协议 §6「智能体群聊触发规则」）。
///
/// 实现方（例如接入真实 AG-UI 运行时 / LLM 网关）需要：
///   1. 根据 <see cref="AgentInvocationContext"/> 发起真实的 AG-UI 调用；
///   2. 将调用产生的 TEXT_MESSAGE_* / TOOL_CALL_* 等事件通过
///      <see cref="Messaging.GroupHub.BroadcastAsync"/> 回灌到群聊扇出，
///      供所有已订阅成员实时接收。
///
/// 当前仓库提供 <see cref="NoopAgentGateway"/> 作为默认空实现，仅记录日志。
/// </summary>
public interface IAgentGateway
{
    Task<AgentInvocationResult> InvokeAsync(AgentInvocationContext context, CancellationToken ct);

    Task<bool> IsAvailableAsync(string agentId, CancellationToken ct);

    /// <summary>
    /// 人机交互决策（协议 4.5）：触发者对 AGENT_INTERACTION_REQUEST 作出批准 / 拒绝（或提交输入 / 按 schema 提交 payload）后，恢复被中断的运行。
    /// 实现方校验 <paramref name="memberId"/> 必须是该交互请求的触发者（TargetMemberId），否则返回 false。
    /// 返回 false 表示交互请求不存在 / 已过期 / 非触发者。
    /// <paramref name="approveAll"/>：是否对<b>本次运行</b>启用批量批准——true 时，该 run 后续的审批工具自动放行（不再打断）。
    /// </summary>
    Task<bool> ResolveInteractionAsync(string interruptId, string memberId, bool approved, string? input, JsonElement? payload, CancellationToken ct, bool approveAll = false, string? toolResult = null);

    /// <summary>停止指定运行（「停止生成」）：取消进行中的模型 / 桥接流式调用。
    /// 命中并已取消返回 true；运行不存在 / 已结束 / 无权限返回 false。</summary>
    bool StopRun(string runId, string operatorId, string groupId, bool isManager);
}
