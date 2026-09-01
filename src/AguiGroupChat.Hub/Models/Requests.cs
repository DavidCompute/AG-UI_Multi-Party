using System.Text.Json;

namespace AguiGroupChat.Hub.Models;

// ================= 客户端上行请求（协议 §5 / §4.6） =================

/// <summary>创建群组（协议 5.2）。Members 为 Hub 扩展，用于指定成员昵称 / 类型。</summary>
public sealed class GroupCreateRequest
{
    public required string GroupName { get; set; }
    public required string OwnerId { get; set; }
    public IReadOnlyList<string>? MemberIds { get; set; }
    public string? GroupAvatar { get; set; }

    /// <summary>是否私密群（私密群的记忆仅限群内检索）。</summary>
    public bool IsPrivate { get; set; }

    /// <summary>知聚类型（<see cref="GroupKind"/>。为 Support 时创建「客服知聚」）。</summary>
    public GroupKind Kind { get; set; } = GroupKind.Normal;
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

/// <summary>成员明细（Hub 扩展，可选）。</summary>
public sealed class MemberSeed
{
    public required string MemberId { get; set; }
    public MemberType MemberType { get; set; } = MemberType.User;
    public string? Nickname { get; set; }
    public string? Avatar { get; set; }
}

/// <summary>更新群信息（协议 5.2）。</summary>
public sealed class GroupUpdateRequest
{
    public required string GroupId { get; set; }
    public required IReadOnlyList<string> UpdateFields { get; set; }
    public required Dictionary<string, JsonElement> GroupInfo { get; set; }
    public required string OperatorId { get; set; }
}

/// <summary>解散群组（Hub API）。</summary>
public sealed class GroupDisbandRequest
{
    public required string GroupId { get; set; }
    public required string OperatorId { get; set; }
}

/// <summary>添加成员（协议 5.2）。MemberDetails 为 Hub 扩展。</summary>
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

/// <summary>更新成员信息（协议 4.3 GROUP_MEMBER_UPDATED 的上行形式）。</summary>
public sealed class GroupMemberUpdateRequest
{
    public required string GroupId { get; set; }
    public required string MemberId { get; set; }
    public required IReadOnlyList<string> UpdateFields { get; set; }
    public required Dictionary<string, JsonElement> MemberInfo { get; set; }
    public required string OperatorId { get; set; }
}

/// <summary>发送群文本消息（协议 5.1）。</summary>
public sealed class GroupMessageSendRequest
{
    public required string GroupId { get; set; }
    /// <summary>缺省时取 thread_ + groupId。</summary>
    public string? ThreadId { get; set; }
    /// <summary>消息所属话题（群聊扩展）：缺省 "main"。</summary>
    public string? TopicId { get; set; }
    /// <summary>发送者 ID。经 WebSocket 上行时由服务端以连接身份覆盖（防伪造）；
    /// HTTP 面由鉴权身份解析。可空：WS 上行允许不携带（避免 required 缺失导致反序列化返回 null）。</summary>
    public string? UserId { get; set; }
    public required string Content { get; set; }
    public IReadOnlyList<string>? Mentions { get; set; }
    public bool MentionAll { get; set; }
    public string? ReplyToMessageId { get; set; }
    public MessageVisibility? Visibility { get; set; }
    public IReadOnlyList<string>? VisibleMemberIds { get; set; }
    /// <summary>消息附件（Hub 扩展）：前端先上传文件取得附件信息，再随消息携带。</summary>
    public IReadOnlyList<AttachmentInfo>? Attachments { get; set; }
    /// <summary>请求方所在客户端/机器（内网桥的 --client 标识，前端经同机回环自动发现携带）。</summary>
    public string? BridgeClient { get; set; }
}

/// <summary>群内新建话题（Hub 扩展）。SourceMessageId 非空时：该消息迁移为新话题的起点（原话题移除）。</summary>
public sealed class GroupTopicCreateRequest
{
    public required string GroupId { get; set; }
    public required string Name { get; set; }
    public required string OperatorId { get; set; }
    public string? SourceMessageId { get; set; }
}

/// <summary>删除话题：仅群主 / 管理员或话题创建者；话题下消息迁移回主话题 main。</summary>
public sealed class GroupTopicDeleteRequest
{
    public required string GroupId { get; set; }
    public required string TopicId { get; set; }
    public required string OperatorId { get; set; }
}

/// <summary>清空话题聊天记录（含主话题 main）：仅群主 / 管理员；话题保留，消息与对应语义记忆一并清除。</summary>
public sealed class GroupTopicClearRequest
{
    public required string GroupId { get; set; }
    public required string TopicId { get; set; }
    public required string OperatorId { get; set; }
}

/// <summary>群主转让：仅当前群主可调用；目标须为群内非群主用户成员。转让后原群主降为群管理员。</summary>
public sealed class GroupTransferOwnerRequest
{
    public required string GroupId { get; set; }
    /// <summary>新群主（群内用户成员）。</summary>
    public required string NewOwnerId { get; set; }
    /// <summary>操作者（当前群主）。Http 面由鉴权身份解析覆盖。</summary>
    public string? OperatorId { get; set; }
}

/// <summary>撤回群消息（Hub API / WS 上行）。</summary>
public sealed class GroupMessageRecallRequest
{
    public required string GroupId { get; set; }
    public required string MessageId { get; set; }
    /// <summary>操作者 ID。经 WS 上行时由连接身份覆盖；HTTP 面由鉴权身份解析。可空：WS 允许不携带。</summary>
    public string? OperatorId { get; set; }
}

/// <summary>重新回答：撤回最后一条智能体消息并用其触发消息重新调用（Hub API / WS 上行）。</summary>
public sealed class GroupMessageRegenerateRequest
{
    public required string GroupId { get; set; }
    /// <summary>目标话题（缺省回退到消息自身话题）。</summary>
    public string? TopicId { get; set; }
    /// <summary>待重答的智能体消息 ID（必须是该话题最后一条消息）。</summary>
    public required string MessageId { get; set; }
    /// <summary>操作者 ID。经 WS 上行时由连接身份覆盖；HTTP 面由鉴权身份解析。</summary>
    public string? OperatorId { get; set; }
}

/// <summary>停止智能体运行（「停止生成」）：触发者本人或同群管理员可执行（Hub API）。</summary>
public sealed class AgentStopRequest
{
    /// <summary>智能体运行 ID（TEXT_MESSAGE_START 事件的 runId）。</summary>
    public required string RunId { get; set; }
    public required string GroupId { get; set; }
    /// <summary>操作者 ID。经 WS 上行时由连接身份覆盖；HTTP 面由鉴权身份解析。</summary>
    public string? OperatorId { get; set; }
}

/// <summary>正在输入状态（协议 4.4 GROUP_TYPING 的上行形式）。</summary>
public sealed class GroupTypingRequest
{
    public required string GroupId { get; set; }
    /// <summary>经 WebSocket 上行时由服务端以连接身份覆盖。可空：WS 允许不携带。</summary>
    public string? MemberId { get; set; }
    public MemberType MemberType { get; set; } = MemberType.User;
    public required bool IsTyping { get; set; }
}

/// <summary>消息已读回执（协议 4.4）。</summary>
public sealed class GroupReadRequest
{
    public required string GroupId { get; set; }
    /// <summary>经 WebSocket 上行时由服务端以连接身份覆盖。可空：WS 允许不携带。</summary>
    public string? MemberId { get; set; }
    public required string ReadMessageId { get; set; }
}

/// <summary>
/// 人机交互决策（Hub 扩展，协议 4.5）：触发者对 AGENT_INTERACTION_REQUEST 作出批准 / 拒绝。
/// 服务端校验决策者必须是交互请求的 TargetMemberId（触发者），其他群成员无权决策。
/// </summary>
public sealed class GroupInteractionResolveRequest
{
    public required string GroupId { get; set; }
    /// <summary>审批中断 ID（AGENT_INTERACTION_REQUEST 的 interruptId）。</summary>
    public required string InterruptId { get; set; }
    /// <summary>决策者。经 WebSocket 上行时由服务端以连接身份覆盖；HTTP 面由鉴权身份解析。可空：WS 允许不携带。</summary>
    public string? MemberId { get; set; }
    /// <summary>true = 批准（执行工具）；false = 拒绝（跳过工具）。input 类型交互提交时恒为 true。</summary>
    public required bool Approved { get; set; }
    /// <summary>kind=input 交互的用户输入文本（工具审批类型为空）。</summary>
    public string? Input { get; set; }
    /// <summary>kind=input 交互按 responseSchema 提交的完整 payload（单选 / 多选 / 数字 / 多字段对象）。</summary>
    public JsonElement? Payload { get; set; }
    /// <summary>kind=client_tool 交互前端执行后的结果（文本 / JSON 字符串）：回传后由网关作为工具结果回灌模型。</summary>
    public string? ToolResult { get; set; }
    /// <summary>true = 对本次运行启用批量批准（后续同类审批自动放行，不再逐个打断）。仅批准（Approved=true）时生效。</summary>
    public bool ApproveAll { get; set; }
}

/// <summary>GROUP_SUBSCRIBE：客户端请求订阅指定群组（协议 4.6）。</summary>
public sealed class SubscribeRequest
{
    public required IReadOnlyList<string> GroupIds { get; set; }
    public long? Timestamp { get; set; }
}

/// <summary>GROUP_UNSUBSCRIBE：取消订阅指定群组（协议 4.6）。</summary>
public sealed class UnsubscribeRequest
{
    public required IReadOnlyList<string> GroupIds { get; set; }
    public long? Timestamp { get; set; }
}

/// <summary>SSE 场景的 HTTP 订阅管理（Hub API，connectionId 来自 GROUP_CONNECTED 握手）。</summary>
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

    /// <summary>群内显式覆盖角色默认触发模式：true 时角色编辑不覆写本群设定；
    /// false 时该群跟随角色默认（角色编辑自动同步）。</summary>
    public bool Override { get; set; }
}

/// <summary>智能体注销（Hub 管理面）。</summary>
public sealed class AgentUnregisterRequest
{
    public required string AgentId { get; set; }
    public IReadOnlyList<string>? GroupIds { get; set; }
}

/// <summary>
/// 智能体应答消息开启参数（协议 4.4）。由 IAgentGateway 调用
/// PublishAgentMessageStartAsync 后，经 AppendAgentContentAsync / EndAgentMessageAsync 流式灌入。
/// </summary>
public sealed class AgentMessageStartInput
{
    public required string GroupId { get; set; }
    public required string AgentId { get; set; }
    /// <summary>AG-UI runId（智能体运行标识）。</summary>
    public string? RunId { get; set; }
    /// <summary>消息所属话题（群聊扩展）：默认 "main" 为群主话题；可因“以此消息新建话题”迁移到新话题。</summary>
    public string TopicId { get; set; } = "main";
    /// <summary>引用回复的目标消息 ID（通常是触发的用户消息）。</summary>
    public string? ReplyToMessageId { get; set; }
    public IReadOnlyList<string>? Mentions { get; set; }
    public bool MentionAll { get; set; }
    public MessageVisibility Visibility { get; set; } = MessageVisibility.All;
    public IReadOnlyList<string>? VisibleMemberIds { get; set; }
}
