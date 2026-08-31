using System.Text.Json;

namespace AguiGroupChat.Sdk.Models;

// ================= 客户端上行请求（协议 §5 / §4.6）=================

/// <summary>成员明细（Hub 扩展，可选）。</summary>
public sealed class MemberSeed
{
    public required string MemberId { get; set; }
    public MemberType MemberType { get; set; } = MemberType.User;
    public string? Nickname { get; set; }
    public string? Avatar { get; set; }
}

/// <summary>创建群组（协议 5.2）。</summary>
public sealed class GroupCreateRequest
{
    public required string GroupName { get; set; }
    public required string OwnerId { get; set; }
    public IReadOnlyList<string>? MemberIds { get; set; }
    public string? GroupAvatar { get; set; }
    public bool IsPrivate { get; set; }
    public IReadOnlyList<MemberSeed>? Members { get; set; }
    public Dictionary<string, object?>? Extra { get; set; }
}

/// <summary>多智能体讨论请求体：选定群内智能体按序参与同一话题（后台串行触发）。</summary>
public sealed class DiscussionHttpRequest
{
    public required string Content { get; set; }
    public IReadOnlyList<string>? AgentIds { get; set; }
    public string? TopicId { get; set; }
}

/// <summary>更新群信息（协议 5.2）。</summary>
public sealed class GroupUpdateRequest
{
    public required string GroupId { get; set; }
    public required IReadOnlyList<string> UpdateFields { get; set; }
    public required Dictionary<string, object?> GroupInfo { get; set; }
    public required string OperatorId { get; set; }
}

/// <summary>解散群组（Hub API）。</summary>
public sealed class GroupDisbandRequest
{
    public required string GroupId { get; set; }
    public required string OperatorId { get; set; }
}

/// <summary>添加成员（协议 5.2）。</summary>
public sealed class GroupMemberAddRequest
{
    public required string GroupId { get; set; }
    public required IReadOnlyList<string> MemberIds { get; set; }
    public required string OperatorId { get; set; }
    public IReadOnlyList<MemberSeed>? MemberDetails { get; set; }
}

/// <summary>移除成员（协议 5.2）。</summary>
public sealed class GroupMemberRemoveRequest
{
    public required string GroupId { get; set; }
    public required IReadOnlyList<string> MemberIds { get; set; }
    public required string OperatorId { get; set; }
}

/// <summary>主动退群（Hub API）。</summary>
public sealed class GroupMemberLeaveRequest
{
    public required string GroupId { get; set; }
    public required string MemberId { get; set; }
}

/// <summary>更新成员信息（协议 4.3 上行形式）。</summary>
public sealed class GroupMemberUpdateRequest
{
    public required string GroupId { get; set; }
    public required string MemberId { get; set; }
    public required IReadOnlyList<string> UpdateFields { get; set; }
    public required Dictionary<string, object?> MemberInfo { get; set; }
    public required string OperatorId { get; set; }
}

/// <summary>发送群文本消息（协议 5.1）。</summary>
public sealed class GroupMessageSendRequest
{
    public required string GroupId { get; set; }
    public string? ThreadId { get; set; }
    public string? TopicId { get; set; }
    /// <summary>发送者 ID。经 WebSocket 上行时由服务端以连接身份覆盖；HTTP 面由鉴权身份解析。</summary>
    public string? UserId { get; set; }
    public required string Content { get; set; }
    public IReadOnlyList<string>? Mentions { get; set; }
    public bool MentionAll { get; set; }
    public string? ReplyToMessageId { get; set; }
    public MessageVisibility? Visibility { get; set; }
    public IReadOnlyList<string>? VisibleMemberIds { get; set; }
    public IReadOnlyList<AttachmentInfo>? Attachments { get; set; }
    /// <summary>请求方所在客户端/机器（内网桥的 --client 标识）。</summary>
    public string? BridgeClient { get; set; }
}

/// <summary>群内新建话题（Hub 扩展）。</summary>
public sealed class GroupTopicCreateRequest
{
    public required string GroupId { get; set; }
    public required string Name { get; set; }
    public required string OperatorId { get; set; }
    public string? SourceMessageId { get; set; }
}

/// <summary>删除话题：仅群主 / 管理员或话题创建者。</summary>
public sealed class GroupTopicDeleteRequest
{
    public required string GroupId { get; set; }
    public required string TopicId { get; set; }
    public required string OperatorId { get; set; }
}

/// <summary>清空话题聊天记录（含主话题 main）。</summary>
public sealed class GroupTopicClearRequest
{
    public required string GroupId { get; set; }
    public required string TopicId { get; set; }
    public required string OperatorId { get; set; }
}

/// <summary>撤回群消息（Hub API / WS 上行）。</summary>
public sealed class GroupMessageRecallRequest
{
    public required string GroupId { get; set; }
    public required string MessageId { get; set; }
    public string? OperatorId { get; set; }
}

/// <summary>重新回答：撤回最后一条智能体消息并重新调用（Hub API / WS 上行）。</summary>
public sealed class GroupMessageRegenerateRequest
{
    public required string GroupId { get; set; }
    public string? TopicId { get; set; }
    public required string MessageId { get; set; }
    public string? OperatorId { get; set; }
}

/// <summary>停止智能体运行（「停止生成」）。</summary>
public sealed class AgentStopRequest
{
    public required string RunId { get; set; }
    public required string GroupId { get; set; }
    public string? OperatorId { get; set; }
}

/// <summary>正在输入状态（协议 4.4 上行形式）。</summary>
public sealed class GroupTypingRequest
{
    public required string GroupId { get; set; }
    public string? MemberId { get; set; }
    public MemberType MemberType { get; set; } = MemberType.User;
    public required bool IsTyping { get; set; }
}

/// <summary>消息已读回执（协议 4.4）。</summary>
public sealed class GroupReadRequest
{
    public required string GroupId { get; set; }
    public string? MemberId { get; set; }
    public required string ReadMessageId { get; set; }
}

/// <summary>人机交互决策（Hub 扩展，协议 4.5）。</summary>
public sealed class GroupInteractionResolveRequest
{
    public required string GroupId { get; set; }
    public required string InterruptId { get; set; }
    public string? MemberId { get; set; }
    public required bool Approved { get; set; }
    public string? Input { get; set; }
    public JsonElement? Payload { get; set; }
    public bool ApproveAll { get; set; }
}

/// <summary>GROUP_SUBSCRIBE / GROUP_UNSUBSCRIBE 上行事件。</summary>
public sealed class SubscribeRequest
{
    public required IReadOnlyList<string> GroupIds { get; set; }
    public long? Timestamp { get; set; }
}

/// <summary>SSE 场景的 HTTP 订阅管理（connectionId 来自 GROUP_CONNECTED 握手）。</summary>
public sealed class SseSubscribeRequest
{
    public required string ConnectionId { get; set; }
    public required IReadOnlyList<string> GroupIds { get; set; }
}

/// <summary>智能体注册（协议 §6 触发规则，Hub 管理面）。</summary>
public sealed class AgentRegisterRequest
{
    public required string AgentId { get; set; }
    public string Nickname { get; set; } = "";
    public required IReadOnlyList<string> GroupIds { get; set; }
    public AgentTriggerMode TriggerMode { get; set; } = AgentTriggerMode.Mentioned;
    public IReadOnlyList<string>? Keywords { get; set; }
    public bool Override { get; set; }
}

/// <summary>智能体注销（Hub 管理面）。</summary>
public sealed class AgentUnregisterRequest
{
    public required string AgentId { get; set; }
    public IReadOnlyList<string>? GroupIds { get; set; }
}
