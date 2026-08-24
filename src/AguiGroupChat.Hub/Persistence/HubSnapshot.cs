using System.Text.Json;
using AguiGroupChat.Hub.Agents;
using AguiGroupChat.Hub.Models;
using AguiGroupChat.Hub.Users;

namespace AguiGroupChat.Hub.Persistence;

/// <summary>
/// 持久化快照：单一 JSON 文件承载全部状态。
/// 结构化部分（用户 / 会话 / 群组 / 触发规则）由 Hub 层负责；
/// 扩展区（Sections）供 Web 层注册智能体定义等额外数据。
/// </summary>
public sealed class HubSnapshot
{
    public int Version { get; set; } = 1;

    public long SavedAt { get; set; }

    public List<UserAccount> Users { get; set; } = [];

    public List<PersistedSession> Sessions { get; set; } = [];

    public List<PersistedGroup> Groups { get; set; } = [];

    public List<AgentRegistration> Registrations { get; set; } = [];

    /// <summary>扩展区：name → JSON 值（如 agents 智能体定义列表），由上层注册读写回调。</summary>
    public Dictionary<string, JsonElement> Sections { get; set; } = [];
}

/// <summary>持久化的登录会话（令牌 + 身份 + 过期时间）。</summary>
public sealed class PersistedSession
{
    public required string Token { get; init; }
    public required string UserId { get; init; }
    public long ExpiresAt { get; init; }
    /// <summary>签发时间戳（毫秒级）。旧快照无此字段时按 ExpiresAt - 滑动 TTL 推算。</summary>
    public long? IssuedAt { get; init; }
    /// <summary>会话唯一标识（多设备会话管理用；旧快照无此字段 = 会话在恢复时签到 Id）。</summary>
    public string? SessionId { get; init; }
}

/// <summary>持久化的群组及其成员、话题与消息。</summary>
public sealed class PersistedGroup
{
    public required Group Group { get; init; }
    public List<GroupMember> Members { get; set; } = [];
    public List<GroupTopic> Topics { get; set; } = [];
    public List<PersistedMessage> Messages { get; set; } = [];
}

/// <summary>
/// 持久化的消息（GroupMessage 的副本，含撤回标记——原模型对该字段标注了 JsonIgnore 不参与对外序列化）。
/// </summary>
public sealed class PersistedMessage
{
    public required string MessageId { get; init; }
    public required string GroupId { get; init; }
    public required string ThreadId { get; init; }
    public string TopicId { get; init; } = "main";
    public required string SenderId { get; init; }
    public MemberType SenderType { get; init; }
    public required string SenderNickname { get; init; }
    public string? ReplyToMessageId { get; init; }
    public List<string> Mentions { get; init; } = [];
    public bool MentionAll { get; init; }
    public MessageVisibility Visibility { get; init; }
    public List<string> VisibleMemberIds { get; init; } = [];
    public List<AttachmentInfo> Attachments { get; init; } = [];
    public required string Content { get; set; }
    /// <summary>智能体思考过程（AG-UI 思考模式，独立于正文；可空）。</summary>
    public string? Reasoning { get; set; }
    /// <summary>智能体技能调用链（链路可视化；可空）。</summary>
    public string? AgentChain { get; set; }
    public required long Timestamp { get; init; }
    public bool Recalled { get; init; }
}
