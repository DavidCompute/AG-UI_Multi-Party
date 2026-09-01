namespace AguiGroupChat.Sdk.Models;

/// <summary>群组模型（协议 2.1）。</summary>
public sealed class Group
{
    public required string GroupId { get; set; }
    public required string GroupName { get; set; }
    public string? GroupAvatar { get; set; }
    public bool IsPrivate { get; set; }
    public required string OwnerId { get; set; }
    public int MemberCount { get; set; }
    public required long CreateTime { get; set; }
    public Dictionary<string, object?>? Extra { get; set; }
}

/// <summary>群成员模型（协议 2.2）。</summary>
public sealed class GroupMember
{
    public required string MemberId { get; set; }
    public required MemberType MemberType { get; set; }
    public required string Nickname { get; set; }
    public string? Avatar { get; set; }
    public required GroupRole Role { get; set; }
    public OnlineStatus OnlineStatus { get; set; } = OnlineStatus.Online;
    public required long JoinTime { get; set; }
    public string? TriggerMode { get; set; }
    public IReadOnlyList<string>? Keywords { get; set; }
    public bool IsTriggerOverridden { get; set; }
    public Dictionary<string, object?>? Extra { get; set; }
}

/// <summary>群话题（Hub 扩展）。</summary>
public sealed class GroupTopic
{
    public required string TopicId { get; set; }
    public required string GroupId { get; set; }
    public required string Name { get; set; }
    public required string CreatorId { get; set; }
    public required long CreatedAt { get; set; }
}

/// <summary>消息附件（Hub 扩展字段）。</summary>
public sealed class AttachmentInfo
{
    public required string AttachmentId { get; set; }
    public required string Name { get; set; }
    public required string ContentType { get; set; }
    public required long Size { get; set; }
    public required string Url { get; set; }
    public required string Kind { get; set; }
}

/// <summary>群消息（协议 2.3 群聊扩展字段）。</summary>
public sealed class GroupMessage
{
    public required string MessageId { get; set; }
    public required string GroupId { get; set; }
    public string TopicId { get; set; } = "main";
    public required string ThreadId { get; set; }
    public required string SenderId { get; set; }
    public required MemberType SenderType { get; set; }
    public required string SenderNickname { get; set; }
    public string? ReplyToMessageId { get; set; }
    public IReadOnlyList<string> Mentions { get; set; } = [];
    public bool MentionAll { get; set; }
    public MessageVisibility Visibility { get; set; } = MessageVisibility.All;
    public IReadOnlyList<string> VisibleMemberIds { get; set; } = [];
    public IReadOnlyList<AttachmentInfo> Attachments { get; set; } = [];
    public required string Content { get; set; }
    public string? Reasoning { get; set; }
    public required long Timestamp { get; set; }
}

/// <summary>快照 / 历史 / 搜索结果中的消息（与 GROUP_STATE_SNAPSHOT.latestMessages 结构一致）。</summary>
public sealed class SnapshotMessage
{
    public string? MessageId { get; set; }
    public string? SenderId { get; set; }
    public string? SenderNickname { get; set; }
    public string? Content { get; set; }
    public string? TopicId { get; set; }
    public string? ReplyToMessageId { get; set; }
    public IReadOnlyList<AttachmentInfo>? Attachments { get; set; }
    public IReadOnlyList<string>? Mentions { get; set; }
    public bool MentionAll { get; set; }
    public string? Reasoning { get; set; }
    public long Timestamp { get; set; }
}

/// <summary>登录 / 注册响应（Hub 扩展）。</summary>
public sealed class AuthResponse
{
    public string? UserId { get; set; }
    public string? Username { get; set; }
    public string? Nickname { get; set; }
    public string? Avatar { get; set; }
    public bool PersonalMemoryEnabled { get; set; }
    public bool IsAdmin { get; set; }
    /// <summary>会话令牌。注册 / 登录成功后使用。</summary>
    public string? Token { get; set; }
    public long? ExpiresAt { get; set; }
}

/// <summary>用户资料（协议 Hub 扩展）。</summary>
public sealed class UserProfile
{
    public string? UserId { get; set; }
    public string? Username { get; set; }
    public string? Nickname { get; set; }
    public string? Avatar { get; set; }
    public bool PersonalMemoryEnabled { get; set; }
    public bool IsAdmin { get; set; }
    public long? CreatedAt { get; set; }
}

/// <summary>用户目录条目（不含 isAdmin，防泄露管理员身份）。</summary>
public sealed class UserDirectoryEntry
{
    public string? UserId { get; set; }
    public string? Username { get; set; }
    public string? Nickname { get; set; }
    public string? Avatar { get; set; }
    public bool PersonalMemoryEnabled { get; set; }
    public long? CreatedAt { get; set; }
}

/// <summary>智能体目录条目（GET /ag-ui/agents）。</summary>
public sealed class AgentDefinitionDto
{
    public string? AgentId { get; set; }
    public string? Nickname { get; set; }
    public string? Description { get; set; }
    public string? Instructions { get; set; }
    public string? Avatar { get; set; }
    public string? TriggerMode { get; set; }
    public IReadOnlyList<string>? Keywords { get; set; }
    public string? Schedule { get; set; }
    public string? Model { get; set; }
    public string? BridgeEndpoint { get; set; }
    public bool PersonalMemoryEnabled { get; set; }
    public bool IsPrivate { get; set; }
    public string? OwnerId { get; set; }
    public IReadOnlyList<string>? Skills { get; set; }
    public IReadOnlyList<string>? KnowledgeBaseIds { get; set; }
    public IReadOnlyList<string>? RequireApprovalToolNames { get; set; }
    public object? Pipeline { get; set; }
    public string? RelayToAgentId { get; set; }
}

/// <summary>扩展附件上传响应。随 GROUP_MESSAGE_SEND 以 attachments 数组携带。</summary>
public sealed class UploadResult
{
    public IReadOnlyList<AttachmentInfo>? Attachments { get; set; }
}

/// <summary>某个成员已加入的群列表条目（GET /ag-ui/member/{memberId}/groups）。</summary>
public sealed class MemberGroupDto
{
    public string? GroupId { get; set; }
    public string? GroupName { get; set; }
    public string? GroupAvatar { get; set; }
    public int MemberCount { get; set; }
    public string? OwnerId { get; set; }
    public bool IsPrivate { get; set; }
    public string? Kind { get; set; }
    public bool IsSupportCircle { get; set; }
    public string? MyRole { get; set; }
    public string? MyNickname { get; set; }
    public long? LastMessageAt { get; set; }
    public int UnreadCount { get; set; }
    public Dictionary<string, int>? UnreadByTopic { get; set; }
}
