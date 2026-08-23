using System.Text.Json;

namespace AguiGroupChat.Sdk.Models;

/// <summary>协议事件名（协议 §4）。</summary>
public static class EventTypes
{
    public const string GroupConnected = "GROUP_CONNECTED";
    public const string GroupCreated = "GROUP_CREATED";
    public const string GroupUpdated = "GROUP_UPDATED";
    public const string GroupDisbanded = "GROUP_DISBANDED";
    public const string GroupMemberJoined = "GROUP_MEMBER_JOINED";
    public const string GroupMemberLeft = "GROUP_MEMBER_LEFT";
    public const string GroupMemberUpdated = "GROUP_MEMBER_UPDATED";

    public const string TextMessageStart = "TEXT_MESSAGE_START";
    public const string TextMessageContent = "TEXT_MESSAGE_CONTENT";
    public const string TextMessageReasoning = "TEXT_MESSAGE_REASONING";
    public const string TextMessageEnd = "TEXT_MESSAGE_END";
    public const string TextMessageAttachments = "TEXT_MESSAGE_ATTACHMENTS";
    public const string TextMessagePlan = "TEXT_MESSAGE_PLAN";
    public const string TextMessageReset = "TEXT_MESSAGE_RESET";
    public const string GroupMessageRecalled = "GROUP_MESSAGE_RECALLED";
    public const string GroupTyping = "GROUP_TYPING";
    public const string GroupMessageRead = "GROUP_MESSAGE_READ";

    public const string ToolCallStart = "TOOL_CALL_START";
    public const string ToolCallArgs = "TOOL_CALL_ARGS";
    public const string ToolCallResult = "TOOL_CALL_RESULT";
    public const string ActivitySnapshot = "ACTIVITY_SNAPSHOT";

    public const string AgentInteractionRequest = "AGENT_INTERACTION_REQUEST";
    public const string AgentInteractionResolved = "AGENT_INTERACTION_RESOLVED";

    public const string GroupSubscribe = "GROUP_SUBSCRIBE";
    public const string GroupSubscribeAck = "GROUP_SUBSCRIBE_ACK";
    public const string GroupUnsubscribe = "GROUP_UNSUBSCRIBE";

    public const string GroupTopicCreated = "GROUP_TOPIC_CREATED";
    public const string GroupMessageTopicMoved = "GROUP_MESSAGE_TOPIC_MOVED";
    public const string GroupTopicDeleted = "GROUP_TOPIC_DELETED";
    public const string GroupTopicCleared = "GROUP_TOPIC_CLEARED";

    public const string GroupStateSnapshot = "GROUP_STATE_SNAPSHOT";
    public const string RunError = "RUN_ERROR";

    // ---- WS 上行事件（由 AguiRealtimeClient 辅助发送）----
    public const string GroupMessageSend = "GROUP_MESSAGE_SEND";
    public const string GroupMessageRecall = "GROUP_MESSAGE_RECALL";
    public const string GroupMessageRegenerate = "GROUP_MESSAGE_REGENERATE";
    public const string AgentInteractionResolve = "AGENT_INTERACTION_RESOLVE";
}

/// <summary>服务端下行事件基类：所有推送事件都附带 type 与 timestamp。</summary>
public class AguiEvent
{
    /// <summary>事件类型，见 <see cref="EventTypes"/>。</summary>
    public string? Type { get; set; }
    public long Timestamp { get; set; }
}

public sealed class GroupConnectedEvent : AguiEvent
{
    public string? ConnectionId { get; set; }
    public string? MemberId { get; set; }
    public string? Transport { get; set; }
}

public sealed class GroupCreatedEvent : AguiEvent
{
    public string? GroupId { get; set; }
    public Group? GroupInfo { get; set; }
    public IReadOnlyList<GroupMember>? Members { get; set; }
}

public sealed class GroupUpdatedEvent : AguiEvent
{
    public string? GroupId { get; set; }
    public IReadOnlyList<string>? UpdateFields { get; set; }
    public JsonElement? GroupInfo { get; set; }
    public string? OperatorId { get; set; }
}

public sealed class GroupDisbandedEvent : AguiEvent
{
    public string? GroupId { get; set; }
    public string? OperatorId { get; set; }
}

public sealed class GroupMemberJoinedEvent : AguiEvent
{
    public string? GroupId { get; set; }
    public IReadOnlyList<GroupMember>? Members { get; set; }
    public string? OperatorId { get; set; }
}

public sealed class GroupMemberLeftEvent : AguiEvent
{
    public string? GroupId { get; set; }
    public IReadOnlyList<string>? MemberIds { get; set; }
    public LeaveType LeaveType { get; set; }
    public string? OperatorId { get; set; }
}

public sealed class GroupMemberUpdatedEvent : AguiEvent
{
    public string? GroupId { get; set; }
    public string? MemberId { get; set; }
    public IReadOnlyList<string>? UpdateFields { get; set; }
    public JsonElement? MemberInfo { get; set; }
    public string? OperatorId { get; set; }
}

public sealed class TextMessageStartEvent : AguiEvent
{
    public string? MessageId { get; set; }
    public string? Role { get; set; }
    public string? ThreadId { get; set; }
    public string? RunId { get; set; }
    public string? GroupId { get; set; }
    public string? SenderId { get; set; }
    public MemberType SenderType { get; set; }
    public string? SenderNickname { get; set; }
    public string? ReplyToMessageId { get; set; }
    public string? TopicId { get; set; }
    public IReadOnlyList<string>? Mentions { get; set; }
    public bool MentionAll { get; set; }
    public MessageVisibility Visibility { get; set; }
    public IReadOnlyList<string>? VisibleMemberIds { get; set; }
    public IReadOnlyList<AttachmentInfo>? Attachments { get; set; }
    public string? Reasoning { get; set; }
}

public sealed class TextMessageContentEvent : AguiEvent
{
    public string? MessageId { get; set; }
    public string? Delta { get; set; }
    public string? GroupId { get; set; }
}

public sealed class TextMessageReasoningEvent : AguiEvent
{
    public string? MessageId { get; set; }
    public string? Delta { get; set; }
    public string? GroupId { get; set; }
}

public sealed class TextMessageAttachmentsEvent : AguiEvent
{
    public string? MessageId { get; set; }
    public string? GroupId { get; set; }
    public IReadOnlyList<AttachmentInfo>? Attachments { get; set; }
}

public sealed class TextMessagePlanEvent : AguiEvent
{
    public string? MessageId { get; set; }
    public string? GroupId { get; set; }
    public string? Title { get; set; }
    public IReadOnlyList<PlanStepInfo>? Steps { get; set; }
}

public sealed class PlanStepInfo
{
    public int Id { get; set; }
    public string? Text { get; set; }
    public bool Done { get; set; }
}

public sealed class TextMessageEndEvent : AguiEvent
{
    public string? MessageId { get; set; }
    public string? GroupId { get; set; }
    public string? Reasoning { get; set; }
}

public sealed class TextMessageResetEvent : AguiEvent
{
    public string? MessageId { get; set; }
    public string? GroupId { get; set; }
}

public sealed class GroupMessageRecalledEvent : AguiEvent
{
    public string? GroupId { get; set; }
    public string? MessageId { get; set; }
    public string? OperatorId { get; set; }
}

public sealed class GroupTypingEvent : AguiEvent
{
    public string? GroupId { get; set; }
    public string? MemberId { get; set; }
    public MemberType MemberType { get; set; }
    public bool IsTyping { get; set; }
}

public sealed class GroupMessageReadEvent : AguiEvent
{
    public string? GroupId { get; set; }
    public string? MemberId { get; set; }
    public string? ReadMessageId { get; set; }
}

public sealed class ToolCallStartEvent : AguiEvent
{
    public string? ToolCallId { get; set; }
    public string? ToolCallName { get; set; }
    public JsonElement? ToolArguments { get; set; }
    public string? ParentMessageId { get; set; }
    public string? GroupId { get; set; }
    public string? TriggerUserId { get; set; }
}

public sealed class ToolCallArgsEvent : AguiEvent
{
    public string? ToolCallId { get; set; }
    public string? ParentMessageId { get; set; }
    public string? GroupId { get; set; }
    public string? Args { get; set; }
}

public sealed class ToolCallResultEvent : AguiEvent
{
    public string? ToolCallId { get; set; }
    public string? ParentMessageId { get; set; }
    public string? GroupId { get; set; }
    public string? Result { get; set; }
}

public sealed class ActivitySnapshotEvent : AguiEvent
{
    public string? ParentMessageId { get; set; }
    public string? GroupId { get; set; }
    public JsonElement? Todos { get; set; }
}

public sealed class AgentInteractionRequestEvent : AguiEvent
{
    public string? GroupId { get; set; }
    public string? MessageId { get; set; }
    public string? ThreadId { get; set; }
    public string? RunId { get; set; }
    public string? InterruptId { get; set; }
    public string? ToolCallId { get; set; }
    public string? ToolName { get; set; }
    public JsonElement? ToolArguments { get; set; }
    public string? Message { get; set; }
    public string? Kind { get; set; }
    public JsonElement? InputField { get; set; }
    public JsonElement? Options { get; set; }
    public JsonElement? ResponseSchema { get; set; }
    public JsonElement? Questions { get; set; }
    public string? TargetMemberId { get; set; }
}

public sealed class AgentInteractionResolvedEvent : AguiEvent
{
    public string? GroupId { get; set; }
    public string? InterruptId { get; set; }
    public string? MemberId { get; set; }
    public bool Approved { get; set; }
    public string? Input { get; set; }
    public JsonElement? Payload { get; set; }
}

public sealed class GroupSubscribeAckEvent : AguiEvent
{
    public IReadOnlyList<string>? SuccessGroupIds { get; set; }
    public IReadOnlyList<string>? FailedGroupIds { get; set; }
    public string? FailReason { get; set; }
}

public sealed class GroupTopicCreatedEvent : AguiEvent
{
    public string? GroupId { get; set; }
    public GroupTopic? Topic { get; set; }
}

public sealed class GroupMessageTopicMovedEvent : AguiEvent
{
    public string? GroupId { get; set; }
    public string? MessageId { get; set; }
    public string? TopicId { get; set; }
    public string? OperatorId { get; set; }
}

public sealed class GroupTopicDeletedEvent : AguiEvent
{
    public string? GroupId { get; set; }
    public string? TopicId { get; set; }
    public string? OperatorId { get; set; }
}

public sealed class GroupTopicClearedEvent : AguiEvent
{
    public string? GroupId { get; set; }
    public string? TopicId { get; set; }
    public string? OperatorId { get; set; }
    public int RemovedCount { get; set; }
}

public sealed class GroupStateSnapshotEvent : AguiEvent
{
    public string? GroupId { get; set; }
    public Group? GroupInfo { get; set; }
    public IReadOnlyList<GroupMember>? Members { get; set; }
    public IReadOnlyList<GroupTopic>? Topics { get; set; }
    public IReadOnlyList<SnapshotMessage>? LatestMessages { get; set; }
}

public sealed class RunErrorEvent : AguiEvent
{
    public string? GroupId { get; set; }
    public string? ErrorCode { get; set; }
    public string? Message { get; set; }
}
