using System.Text.Json;

namespace AguiGroupChat.Hub.Models;

/// <summary>
/// 协议事件名（协议 §4）。
/// GROUP_CONNECTED / GROUP_MESSAGE_SEND / GROUP_MESSAGE_RECALL 为 Hub 扩展事件，
/// 旧客户端可安全忽略。
/// </summary>
public static class EventTypes
{
    // ---- 群组生命周期（协议 4.2）----
    public const string GroupCreated = "GROUP_CREATED";
    public const string GroupUpdated = "GROUP_UPDATED";
    public const string GroupDisbanded = "GROUP_DISBANDED";

    // ---- 群成员（协议 4.3）----
    public const string GroupMemberJoined = "GROUP_MEMBER_JOINED";
    public const string GroupMemberLeft = "GROUP_MEMBER_LEFT";
    public const string GroupMemberUpdated = "GROUP_MEMBER_UPDATED";

    // ---- 群消息（原生事件扩展，协议 4.4）----
    public const string TextMessageStart = "TEXT_MESSAGE_START";
    public const string TextMessageContent = "TEXT_MESSAGE_CONTENT";
    /// <summary>智能体思考过程增量（AG-UI 思考模式，独立于正文流式回灌；Hub 扩展）。</summary>
    public const string TextMessageReasoning = "TEXT_MESSAGE_REASONING";
    public const string TextMessageEnd = "TEXT_MESSAGE_END";
    /// <summary>智能体消息运行中/结束时追加外部附件（AG-UI 桥接回灌，Hub 扩展）。</summary>
    public const string TextMessageAttachments = "TEXT_MESSAGE_ATTACHMENTS";
    /// <summary>工作型智能体消息结束时回附其工作区 PLAN.md 的结构化步骤计划（任务规划可视化）。</summary>
    public const string TextMessagePlan = "TEXT_MESSAGE_PLAN";
    /// <summary>智能体消息内容重置（人机交互中断时清空已回灌的中间内容，等恢复后最终结果一次性返回）。</summary>
    public const string TextMessageReset = "TEXT_MESSAGE_RESET";
    public const string GroupMessageRecalled = "GROUP_MESSAGE_RECALLED";
    public const string GroupTyping = "GROUP_TYPING";
    public const string GroupMessageRead = "GROUP_MESSAGE_READ";

    // ---- 工具调用（原生事件扩展，协议 4.5）----
    public const string ToolCallStart = "TOOL_CALL_START";
    /// <summary>工具调用参数（TOOL_CALL_ARGS 分帧到达时，桥接在 TOOL_CALL_END 后广播完整参数，Hub 扩展）。</summary>
    public const string ToolCallArgs = "TOOL_CALL_ARGS";
    /// <summary>工具执行结果回灌（本地工具 / 外部 AG-UI TOOL_CALL_RESULT，Hub 扩展）。</summary>
    public const string ToolCallResult = "TOOL_CALL_RESULT";
    /// <summary>任务进度快照（外部 AG-UI ACTIVITY_SNAPSHOT，如 OpenCode 的 todo 状态流：pending → in_progress → completed）。</summary>
    public const string ActivitySnapshot = "ACTIVITY_SNAPSHOT";

    // ---- 人机交互（Hub 扩展，协议 4.5）----
    /// <summary>下行：智能体需要用户交互（如工具审批），运行中断等待决策。交互对象仅限触发者（targetMemberId）。</summary>
    public const string AgentInteractionRequest = "AGENT_INTERACTION_REQUEST";
    /// <summary>WS 上行：触发者对交互请求作出决策（批准 / 拒绝）。</summary>
    public const string AgentInteractionResolve = "AGENT_INTERACTION_RESOLVE";
    /// <summary>下行：触发者已作出决策，全群广播（其他成员同步看到卡片状态变化）。</summary>
    public const string AgentInteractionResolved = "AGENT_INTERACTION_RESOLVED";

    // ---- 群订阅（协议 4.6）----
    public const string GroupSubscribe = "GROUP_SUBSCRIBE";
    public const string GroupSubscribeAck = "GROUP_SUBSCRIBE_ACK";
    public const string GroupUnsubscribe = "GROUP_UNSUBSCRIBE";

    // ---- 群话题（Hub 扩展）----
    public const string GroupTopicCreated = "GROUP_TOPIC_CREATED";
    /// <summary>消息被迁移到其他话题（“以此消息新建话题”）。</summary>
    public const string GroupMessageTopicMoved = "GROUP_MESSAGE_TOPIC_MOVED";
    /// <summary>话题被删除（其下消息迁移回主话题）。</summary>
    public const string GroupTopicDeleted = "GROUP_TOPIC_DELETED";
    /// <summary>话题聊天记录被清空（话题保留，消息与记忆一并清除）。</summary>
    public const string GroupTopicCleared = "GROUP_TOPIC_CLEARED";

    // ---- 群状态同步（协议 4.7）----
    public const string GroupStateSnapshot = "GROUP_STATE_SNAPSHOT";

    // ---- 错误（原生 RUN_ERROR 扩展，协议 §7）----
    public const string RunError = "RUN_ERROR";

    // ---- Hub 扩展（协议未定义）----
    /// <summary>连接建立握手，携带 connectionId（SSE 场景可据此动态订阅）。</summary>
    public const string GroupConnected = "GROUP_CONNECTED";
    /// <summary>WS 上行：发送群消息（等效 POST /ag-ui/group/message/send）。</summary>
    public const string GroupMessageSend = "GROUP_MESSAGE_SEND";
    /// <summary>WS 上行：撤回群消息（等效 POST /ag-ui/group/message/recall）。</summary>
    public const string GroupMessageRecall = "GROUP_MESSAGE_RECALL";
    /// <summary>WS 上行：重新回答最后一条智能体消息（等效 POST /ag-ui/group/message/regenerate）。</summary>
    public const string GroupMessageRegenerate = "GROUP_MESSAGE_REGENERATE";
}

// ================= 服务端下行事件 =================

/// <summary>GROUP_CREATED：群组创建成功，推送给所有初始成员（协议 4.2）。</summary>
public sealed class GroupCreatedEvent
{
    public string Type => EventTypes.GroupCreated;
    public required string GroupId { get; init; }
    public required Group GroupInfo { get; init; }
    public required IReadOnlyList<GroupMember> Members { get; init; }
    public required long Timestamp { get; init; }
}

/// <summary>GROUP_UPDATED：群组基础信息变更时全群广播（协议 4.2）。</summary>
public sealed class GroupUpdatedEvent
{
    public string Type => EventTypes.GroupUpdated;
    public required string GroupId { get; init; }
    public required IReadOnlyList<string> UpdateFields { get; init; }
    public required Dictionary<string, JsonElement> GroupInfo { get; init; }
    public required string OperatorId { get; init; }
    public required long Timestamp { get; init; }
}

/// <summary>GROUP_DISBANDED：群组解散时全群推送，推送后服务端终止该群所有事件（协议 4.2）。</summary>
public sealed class GroupDisbandedEvent
{
    public string Type => EventTypes.GroupDisbanded;
    public required string GroupId { get; init; }
    public required string OperatorId { get; init; }
    public required long Timestamp { get; init; }
}

/// <summary>GROUP_MEMBER_JOINED：新成员入群时全群广播，支持批量加入（协议 4.3）。</summary>
public sealed class GroupMemberJoinedEvent
{
    public string Type => EventTypes.GroupMemberJoined;
    public required string GroupId { get; init; }
    public required IReadOnlyList<GroupMember> Members { get; init; }
    public required string OperatorId { get; init; }
    public required long Timestamp { get; init; }
}

/// <summary>GROUP_MEMBER_LEFT：成员主动退群或被移出时全群广播（协议 4.3）。</summary>
public sealed class GroupMemberLeftEvent
{
    public string Type => EventTypes.GroupMemberLeft;
    public required string GroupId { get; init; }
    public required IReadOnlyList<string> MemberIds { get; init; }
    public required LeaveType LeaveType { get; init; }
    public required string OperatorId { get; init; }
    public required long Timestamp { get; init; }
}

/// <summary>GROUP_MEMBER_UPDATED：成员角色、昵称、在线状态变更时推送（协议 4.3）。</summary>
public sealed class GroupMemberUpdatedEvent
{
    public string Type => EventTypes.GroupMemberUpdated;
    public required string GroupId { get; init; }
    public required string MemberId { get; init; }
    public required IReadOnlyList<string> UpdateFields { get; init; }
    public required Dictionary<string, JsonElement> MemberInfo { get; init; }
    public required string OperatorId { get; init; }
    public required long Timestamp { get; init; }
}

/// <summary>TEXT_MESSAGE_START：消息开始（原生事件 + 协议 2.3 群聊扩展字段，协议 4.4）。</summary>
public sealed class TextMessageStartEvent
{
    public string Type => EventTypes.TextMessageStart;
    public required string MessageId { get; init; }
    /// <summary>user（用户上行） / assistant（智能体应答）。</summary>
    public required string Role { get; init; }
    public required string ThreadId { get; init; }
    /// <summary>智能体运行 ID（AG-UI runId）。</summary>
    public string? RunId { get; init; }
    public required string GroupId { get; init; }
    public required string SenderId { get; init; }
    public required MemberType SenderType { get; init; }
    public required string SenderNickname { get; init; }
    public string? ReplyToMessageId { get; init; }
    /// <summary>消息所属话题（群聊扩展）：默认 "main" 为群主话题。</summary>
    public string TopicId { get; init; } = "main";
    public IReadOnlyList<string> Mentions { get; init; } = [];
    public bool MentionAll { get; init; }
    public MessageVisibility Visibility { get; init; } = MessageVisibility.All;
    public IReadOnlyList<string> VisibleMemberIds { get; init; } = [];
    /// <summary>消息附件（Hub 扩展，见 <see cref="AttachmentInfo"/>）。</summary>
    public IReadOnlyList<AttachmentInfo> Attachments { get; init; } = [];
    /// <summary>智能体思考过程（AG-UI 思考模式：独立于正文展示，可空）。</summary>
    public string? Reasoning { get; init; }
    /// <summary>智能体技能调用链（链路可视化；可空 = 无技能调用）。</summary>
    public string? AgentChain { get; init; }
    public required long Timestamp { get; init; }
}

/// <summary>TEXT_MESSAGE_CONTENT：消息增量（原生格式完全不变，通过 messageId 与 START 关联，协议 4.4）。</summary>
public sealed class TextMessageContentEvent
{
    public string Type => EventTypes.TextMessageContent;
    public required string MessageId { get; init; }
    public required string Delta { get; init; }
    /// <summary>所属群（Hub 扩展字段：协议 4.4 不要求，供前端在 START 丢失时按群定位，外部客户端可忽略）。</summary>
    public string? GroupId { get; init; }
}

/// <summary>TEXT_MESSAGE_REASONING：智能体思考过程增量（AG-UI 思考模式，独立于正文流式回灌；Hub 扩展）。</summary>
public sealed class TextMessageReasoningEvent
{
    public string Type => EventTypes.TextMessageReasoning;
    public required string MessageId { get; init; }
    public required string Delta { get; init; }
    /// <summary>所属群（Hub 扩展字段，供前端在 START 丢失时按群定位）。</summary>
    public string? GroupId { get; init; }
}

/// <summary>TEXT_MESSAGE_ATTACHMENTS：智能体消息运行中追加外部附件（AG-UI 桥接回灌，Hub 扩展）。</summary>
public sealed class TextMessageAttachmentsEvent
{
    public string Type => EventTypes.TextMessageAttachments;
    public required string MessageId { get; init; }
    public required string GroupId { get; init; }
    public required IReadOnlyList<AttachmentInfo> Attachments { get; init; }
    public required long Timestamp { get; init; }
}

/// <summary>TEXT_MESSAGE_PLAN：工作型智能体消息结束时回附其工作区 PLAN.md 的结构化步骤计划（任务规划可视化）。</summary>
public sealed class TextMessagePlanEvent
{
    public string Type => EventTypes.TextMessagePlan;
    public required string MessageId { get; init; }
    public required string GroupId { get; init; }
    /// <summary>计划标题（PLAN.md 首行 # 之后的文字），无则留空。</summary>
    public string? Title { get; init; }
    /// <summary>步骤清单（含完成状态）。</summary>
    public required IReadOnlyList<PlanStepInfo> Steps { get; init; }
    public required long Timestamp { get; init; }
}

/// <summary>任务计划中的一步（前端渲染勾选清单 / 进度）。</summary>
public sealed class PlanStepInfo
{
    /// <summary>步骤序号（从 1 开始）。</summary>
    public required int Id { get; init; }
    /// <summary>步骤描述文本。</summary>
    public required string Text { get; init; }
    /// <summary>是否已完成。</summary>
    public bool Done { get; init; }
}

/// <summary>TEXT_MESSAGE_END：消息结束（协议 4.4；Reasoning 为消息结束时思考内容的完整快照，供前端回放）。</summary>
public sealed class TextMessageEndEvent
{
    public string Type => EventTypes.TextMessageEnd;
    public required string MessageId { get; init; }
    public required string GroupId { get; init; }
    /// <summary>消息结束时思考过程完整内容（可空 = 无思考内容）。</summary>
    public string? Reasoning { get; init; }
    /// <summary>消息结束时智能体技能调用链（链路可视化；可空 = 无技能调用）。</summary>
    public string? AgentChain { get; init; }
    /// <summary>消息结束时工作型智能体任务计划（JSON 序列化 { title, steps }；可空 = 无计划）。</summary>
    public string? PlanJson { get; init; }
    public required long Timestamp { get; init; }
}

/// <summary>智能体消息内容重置：清空已回灌的中间内容（人机交互中断场景），前端清空显示并等待恢复后的最终结果。</summary>
public sealed class TextMessageResetEvent
{
    public string Type => EventTypes.TextMessageReset;
    public required string MessageId { get; init; }
    public required string GroupId { get; init; }
    public required long Timestamp { get; init; }
}

/// <summary>GROUP_MESSAGE_RECALLED：消息撤回事件，全群广播（协议 4.4）。</summary>
public sealed class GroupMessageRecalledEvent
{
    public string Type => EventTypes.GroupMessageRecalled;
    public required string GroupId { get; init; }
    public required string MessageId { get; init; }
    public required string OperatorId { get; init; }
    public required long Timestamp { get; init; }
}

/// <summary>GROUP_TYPING：成员正在输入状态提示（协议 4.4）。</summary>
public sealed class GroupTypingEvent
{
    public string Type => EventTypes.GroupTyping;
    public required string GroupId { get; init; }
    public required string MemberId { get; init; }
    public required MemberType MemberType { get; init; }
    public required bool IsTyping { get; init; }
    public required long Timestamp { get; init; }
}

/// <summary>GROUP_MESSAGE_READ：消息已读回执（协议 4.4，可选实现）。</summary>
public sealed class GroupMessageReadEvent
{
    public string Type => EventTypes.GroupMessageRead;
    public required string GroupId { get; init; }
    public required string MemberId { get; init; }
    public required string ReadMessageId { get; init; }
    public required long Timestamp { get; init; }
}

/// <summary>TOOL_CALL_START：工具调用（原生事件 + 群字段，控制结果可见范围，协议 4.5）。</summary>
public sealed class ToolCallStartEvent
{
    public string Type => EventTypes.ToolCallStart;
    public required string ToolCallId { get; init; }
    public required string ToolCallName { get; init; }
    /// <summary>工具参数（JSON 文本，可空；外部桥接参数分帧到达时由 TOOL_CALL_ARGS 补发）。</summary>
    public string? ToolArguments { get; init; }
    public required string ParentMessageId { get; init; }
    public required string GroupId { get; init; }
    public required string TriggerUserId { get; init; }
    public MessageVisibility Visibility { get; init; } = MessageVisibility.All;
    public IReadOnlyList<string> VisibleMemberIds { get; init; } = [];
    public required long Timestamp { get; init; }
}

/// <summary>TOOL_CALL_ARGS：工具调用参数完整文本（桥接在参数分帧累积完成后补发，Hub 扩展）。</summary>
public sealed class ToolCallArgsEvent
{
    public string Type => EventTypes.ToolCallArgs;
    public required string ToolCallId { get; init; }
    public required string ParentMessageId { get; init; }
    public required string GroupId { get; init; }
    /// <summary>完整参数（JSON 文本）。</summary>
    public required string Args { get; init; }
    public required long Timestamp { get; init; }
}

/// <summary>TOOL_CALL_RESULT：工具执行结果（本地工具 FunctionResultContent / 外部 AG-UI TOOL_CALL_RESULT，Hub 扩展）。</summary>
public sealed class ToolCallResultEvent
{
    public string Type => EventTypes.ToolCallResult;
    public required string ToolCallId { get; init; }
    public required string ParentMessageId { get; init; }
    public required string GroupId { get; init; }
    /// <summary>工具返回结果文本（截断后展示）。</summary>
    public required string Result { get; init; }
    public required long Timestamp { get; init; }
}

/// <summary>ACTIVITY_SNAPSHOT：任务进度快照（外部 AG-UI，如 OpenCode 的 todo 状态流）——
/// 携带 todo 列表（[{content, status: pending|in_progress|completed, priority}]），前端实时渲染进度。</summary>
public sealed class ActivitySnapshotEvent
{
    public string Type => EventTypes.ActivitySnapshot;
    public required string ParentMessageId { get; init; }
    public required string GroupId { get; init; }
    /// <summary>todo 列表（JSON 数组，元素含 content / status / priority）。</summary>
    public required JsonElement Todos { get; init; }
    public required long Timestamp { get; init; }
}

/// <summary>
/// AGENT_INTERACTION_REQUEST：智能体运行中断，请求人机交互（Hub 扩展，协议 4.5）。
/// 广播给全群可见，但只有 <see cref="TargetMemberId"/>（触发者）可以决策；其他成员只读。
/// 前端据此渲染「批准 / 拒绝」卡片；触发者决策后发送 AGENT_INTERACTION_RESOLVE 恢复运行。
/// </summary>
public sealed class AgentInteractionRequestEvent
{
    public string Type => EventTypes.AgentInteractionRequest;
    public required string GroupId { get; init; }
    /// <summary>中断前的智能体消息 ID（用于关联上下文）。</summary>
    public required string MessageId { get; init; }
    public required string ThreadId { get; init; }
    public required string RunId { get; init; }
    /// <summary>审批中断 ID（AG-UI interrupt id），恢复时回传。</summary>
    public required string InterruptId { get; init; }
    /// <summary>工具调用 ID（AG-UI toolCallId）。</summary>
    public required string ToolCallId { get; init; }
    /// <summary>需要交互的工具名（如 publish_announcement）。</summary>
    public required string ToolName { get; init; }
    /// <summary>工具调用参数（JSON 对象）。</summary>
    public JsonElement? ToolArguments { get; init; }
    /// <summary>展示给用户的交互提示（如「是否批准发布公告？」）。</summary>
    public required string Message { get; init; }
    /// <summary>交互类型：approval（工具审批，默认）/ input（请求输入，文本框）/ choice（单选）/ multi_choice（多选）/ client_tool（客户端执行技能）。</summary>
    public string Kind { get; init; } = "approval";
    /// <summary>kind=client_tool 时前端执行所需的运行配置（技能的 ClientRunner JSON，前端执行器据此执行并回传结果）。</summary>
    public string? ClientRunner { get; init; }
    /// <summary>kind=input/choice/multi_choice 时的响应字段名（如 answer），前端提交的用户输入以其为键回传。</summary>
    public string? InputField { get; init; }
    /// <summary>kind=choice/multi_choice 的可选项列表（来自 responseSchema 的 enum）。</summary>
    public IReadOnlyList<string>? Options { get; init; }
    /// <summary>kind=input 时外部服务下发的完整 responseSchema（JSON Schema）：前端据此渲染通用表单（文本 / 单选 enum / 多选 array / 数字 / 多字段）。</summary>
    public JsonElement? ResponseSchema { get; init; }
    /// <summary>外部 question 工具的结构化问题列表（如 OpenCode metadata.questions）：前端逐题渲染选项，答案按问题顺序回传。</summary>
    public IReadOnlyList<BridgeQuestion>? Questions { get; init; }
    /// <summary>唯一可决策的成员（触发者）；其他成员仅可见。</summary>
    public required string TargetMemberId { get; init; }
    public required long Timestamp { get; init; }
}

/// <summary>外部 question 工具的结构化问题（如 OpenCode metadata.questions）：供前端逐题渲染选项。</summary>
/// <param name="Header">问题分组标题（可空，如「使用场景」）。</param>
/// <param name="Question">问题文本。</param>
/// <param name="Options">选项列表（label + 可选说明）。</param>
/// <param name="Multiple">是否允许多选（OpenCode 的 multiple 标记；true 时前端渲染勾选，答案以分隔符连接）。</param>
public sealed record BridgeQuestion(string? Header, string Question, IReadOnlyList<BridgeQuestionOption>? Options, bool Multiple = false);

/// <summary>问题选项。</summary>
public sealed record BridgeQuestionOption(string Label, string? Description);

/// <summary>
/// AGENT_INTERACTION_RESOLVED：触发者已对人机交互请求作出决策，全群广播（Hub 扩展，协议 4.5）。
/// 其他成员的卡片同步更新为「已批准 / 已拒绝」状态；决策者本人由本地回显 + 本事件双重更新（幂等）。
/// </summary>
public sealed class AgentInteractionResolvedEvent
{
    public string Type => EventTypes.AgentInteractionResolved;
    public required string GroupId { get; init; }
    /// <summary>对应 AGENT_INTERACTION_REQUEST 的 interruptId。</summary>
    public required string InterruptId { get; init; }
    /// <summary>决策者（即触发者）。</summary>
    public required string MemberId { get; init; }
    /// <summary>true = 批准（执行工具）；false = 拒绝（跳过工具）。</summary>
    public required bool Approved { get; init; }
    /// <summary>kind=input 时用户提交的输入文本（其余类型为空）。</summary>
    public string? Input { get; init; }
    /// <summary>kind=input 时用户按 responseSchema 提交的完整 payload（单选 / 多选 / 数字 / 多字段）；单字段文本时为空（走 Input）。</summary>
    public JsonElement? Payload { get; init; }
    public required long Timestamp { get; init; }
}

/// <summary>GROUP_SUBSCRIBE_ACK：服务端返回订阅结果（协议 4.6）。</summary>
public sealed class GroupSubscribeAckEvent
{
    public string Type => EventTypes.GroupSubscribeAck;
    public required IReadOnlyList<string> SuccessGroupIds { get; init; }
    public required IReadOnlyList<string> FailedGroupIds { get; init; }
    public string? FailReason { get; init; }
    public required long Timestamp { get; init; }
}

/// <summary>GROUP_TOPIC_CREATED：群内新建话题（全群广播）。</summary>
public sealed class GroupTopicCreatedEvent
{
    public string Type => EventTypes.GroupTopicCreated;
    public required string GroupId { get; init; }
    public required GroupTopic Topic { get; init; }
    public required long Timestamp { get; init; }
}

/// <summary>GROUP_MESSAGE_TOPIC_MOVED：某条消息被迁移到指定话题（“以此消息新建话题”的起点）。</summary>
public sealed class GroupMessageTopicMovedEvent
{
    public string Type => EventTypes.GroupMessageTopicMoved;
    public required string GroupId { get; init; }
    public required string MessageId { get; init; }
    public required string TopicId { get; init; }
    public required string OperatorId { get; init; }
    public required long Timestamp { get; init; }
}

/// <summary>GROUP_TOPIC_DELETED：话题被删除（其下消息迁移回主话题 main）。</summary>
public sealed class GroupTopicDeletedEvent
{
    public string Type => EventTypes.GroupTopicDeleted;
    public required string GroupId { get; init; }
    public required string TopicId { get; init; }
    public required string OperatorId { get; init; }
    public required long Timestamp { get; init; }
}

/// <summary>GROUP_TOPIC_CLEARED：话题聊天记录被清空（话题保留，消息与语义记忆一并清除）。</summary>
public sealed class GroupTopicClearedEvent
{
    public string Type => EventTypes.GroupTopicCleared;
    public required string GroupId { get; init; }
    public required string TopicId { get; init; }
    public required string OperatorId { get; init; }
    public required int RemovedCount { get; init; }
    public required long Timestamp { get; init; }
}

/// <summary>GROUP_STATE_SNAPSHOT：成员入群 / 订阅成功后，服务端推送群组完整状态快照（协议 4.7）。</summary>
public sealed class GroupStateSnapshotEvent
{
    public string Type => EventTypes.GroupStateSnapshot;
    public required string GroupId { get; init; }
    public required Group GroupInfo { get; init; }
    public required IReadOnlyList<GroupMember> Members { get; init; }
    /// <summary>群内话题列表（不含默认话题 main）。</summary>
    public IReadOnlyList<GroupTopic> Topics { get; init; } = [];
    public required IReadOnlyList<SnapshotMessage> LatestMessages { get; init; }
    public required long Timestamp { get; init; }
}

/// <summary>快照中的消息摘要。</summary>
public sealed class SnapshotMessage
{
    public required string MessageId { get; init; }
    public required string SenderId { get; init; }
    public required string SenderNickname { get; init; }
    public required string Content { get; init; }
    /// <summary>引用回复的目标消息 ID（前端展示 / 点击定位）。</summary>
    public string? ReplyToMessageId { get; init; }
    /// <summary>消息附件（Hub 扩展，见 <see cref="AttachmentInfo"/>）。</summary>
    public IReadOnlyList<AttachmentInfo> Attachments { get; init; } = [];
    /// <summary>智能体思考过程（AG-UI 思考模式，独立于正文展示；可空）。</summary>
    public string? Reasoning { get; init; }
    /// <summary>智能体技能调用链（链路可视化；可空 = 无技能调用）。</summary>
    public string? AgentChain { get; init; }
    /// <summary>工作型智能体任务计划（JSON 序列化 { title, steps }；可空 = 无计划）。</summary>
    public string? PlanJson { get; init; }
    /// <summary>@ 提及成员（协议 2.3 扩展字段，前端回显）。</summary>
    public IReadOnlyList<string> Mentions { get; init; } = [];
    /// <summary>是否 @ 全体。</summary>
    public bool MentionAll { get; init; }
    /// <summary>消息所属话题（群聊扩展）。</summary>
    public string TopicId { get; init; } = "main";
    public required long Timestamp { get; init; }
}

/// <summary>GROUP_CONNECTED：连接建立握手（Hub 扩展，携带 connectionId 供 SSE 动态订阅）。</summary>
public sealed class GroupConnectedEvent
{
    public string Type => EventTypes.GroupConnected;
    public required string ConnectionId { get; init; }
    public required string MemberId { get; init; }
    public required string Transport { get; init; }
    public required long Timestamp { get; init; }
}

/// <summary>RUN_ERROR：协议 §7 错误码扩展，WS / SSE 通道上的错误下行事件。</summary>
public sealed class RunErrorEvent
{
    public string Type => EventTypes.RunError;
    public string? GroupId { get; init; }
    public required string ErrorCode { get; init; }
    public required string Message { get; init; }
    public required long Timestamp { get; init; }
}
