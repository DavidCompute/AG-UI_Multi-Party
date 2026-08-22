namespace AguiGroupChat.Hub.Models;

/// <summary>
/// 群聊话题：群内的独立讨论线。默认话题 "main"（群主话题）始终存在，不落库；
/// 新话题由群成员创建（本群可见，消息归属对应话题）。
/// </summary>
public sealed record GroupTopic
{
    public required string TopicId { get; init; }
    public required string GroupId { get; init; }
    public required string Name { get; init; }
    public required string CreatorId { get; init; }
    public required long CreatedAt { get; init; }
}
