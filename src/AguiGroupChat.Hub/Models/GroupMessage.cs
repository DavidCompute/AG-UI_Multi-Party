using System.Text.Json.Serialization;

namespace AguiGroupChat.Hub.Models;

/// <summary>
/// 群消息：原生 AG-UI 消息 + 协议 2.3 群聊扩展字段（全部可选，旧客户端可忽略）。
/// </summary>
public sealed record GroupMessage
{
    public required string MessageId { get; init; }

    /// <summary>所属群组 ID。</summary>
    public required string GroupId { get; init; }

    /// <summary>消息所属话题（群聊扩展）：默认 "main" 为群主话题；可因“以此消息新建话题”迁移到新话题。</summary>
    public string TopicId { get; set; } = "main";

    /// <summary>会话 ID；群聊场景下 threadId 与 groupId 一一对应。</summary>
    public required string ThreadId { get; init; }

    /// <summary>发送者成员 ID。</summary>
    public required string SenderId { get; init; }

    /// <summary>发送者类型：user / agent。</summary>
    public required MemberType SenderType { get; init; }

    /// <summary>发送者群昵称，便于前端直接渲染。</summary>
    public required string SenderNickname { get; init; }

    /// <summary>引用回复的目标消息 ID。</summary>
    public string? ReplyToMessageId { get; init; }

    /// <summary>@ 提及的成员 ID 列表。</summary>
    public IReadOnlyList<string> Mentions { get; init; } = [];

    /// <summary>是否 @ 全体成员。</summary>
    public bool MentionAll { get; init; }

    /// <summary>可见范围：all / mentioned / private。</summary>
    public MessageVisibility Visibility { get; init; } = MessageVisibility.All;

    /// <summary>定向可见成员列表，配合 private 使用。</summary>
    public IReadOnlyList<string> VisibleMemberIds { get; init; } = [];

    /// <summary>消息附件（Hub 扩展，见 <see cref="AttachmentInfo"/>）；桥接外部附件在消息运行中追加。</summary>
    public IReadOnlyList<AttachmentInfo> Attachments { get; set; } = [];

    /// <summary>消息文本内容（智能体流式应答时增量写入）。</summary>
    public required string Content { get; set; }

    /// <summary>智能体思考过程（AG-UI REASONING_MESSAGE_CONTENT 桥接回灌，独立于正文展示；可空 = 无思考内容）。</summary>
    public string? Reasoning { get; set; }

    /// <summary>发送时间戳（毫秒级）。</summary>
    public required long Timestamp { get; init; }

    /// <summary>内部标记：已被撤回（不参与对外序列化）。</summary>
    [JsonIgnore]
    public bool Recalled { get; set; }
}
