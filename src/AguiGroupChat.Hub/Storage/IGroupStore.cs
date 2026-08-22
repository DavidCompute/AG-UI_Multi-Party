using AguiGroupChat.Hub.Models;

namespace AguiGroupChat.Hub.Storage;

/// <summary>
/// 群组数据存储抽象。默认实现为进程内内存存储（<see cref="InMemoryGroupStore"/>）；
/// 多实例 / 持久化场景可替换为 Redis、数据库等实现。
/// </summary>
public interface IGroupStore
{
    // 群组
    bool AddGroup(Group group);
    Group? GetGroup(string groupId);
    bool RemoveGroup(string groupId);

    /// <summary>事务性建群：群与首批成员一次写入（数据库实现单事务、失败回滚；内存实现依次写入）。</summary>
    bool CreateGroupWithMembers(Group group, IReadOnlyList<GroupMember> members);

    // 成员
    bool AddMember(string groupId, GroupMember member);
    bool IsMember(string groupId, string memberId);
    GroupMember? GetMember(string groupId, string memberId);
    bool RemoveMember(string groupId, string memberId);
    bool UpdateMemberStatus(string groupId, string memberId, OnlineStatus status);
    IReadOnlyList<GroupMember> ListMembers(string groupId);
    int MemberCount(string groupId);
    IReadOnlyList<Group> GroupsOf(string memberId);

    // 话题（群聊扩展；默认话题 main 不落库）
    bool AddTopic(GroupTopic topic);
    GroupTopic? GetTopic(string groupId, string topicId);
    IReadOnlyList<GroupTopic> ListTopics(string groupId);

    /// <summary>删除话题（仅话题记录；话题下消息的迁移由调用方处理）。</summary>
    bool RemoveTopic(string groupId, string topicId);

    /// <summary>物理删除某话题下的全部消息（话题删除时聊天记录一并清除），返回删除条数。</summary>
    int RemoveTopicMessages(string groupId, string topicId);

    // 消息
    void AddMessage(GroupMessage message);

    /// <summary>原地修改后的群组落库（如改群名 / 头像）。内存实现为空操作（对象已原地变更），数据库实现写库。</summary>
    void UpdateGroup(Group group);

    /// <summary>原地修改后的成员落库（如改角色 / 昵称 / 头像，不含在线状态等瞬时量）。内存实现为空操作。</summary>
    void UpdateMember(string groupId, GroupMember member);

    /// <summary>原地修改后的消息落库（如流式内容追加 / 话题迁移）。内存实现为空操作。</summary>
    void UpdateMessage(GroupMessage message);
    GroupMessage? GetMessage(string groupId, string messageId);
    bool RecallMessage(string groupId, string messageId);
    IReadOnlyList<GroupMessage> RecentMessages(string groupId, int count, string? topicId = null);

    /// <summary>按游标分页：返回指定消息之前（更早）的 count 条，按时间序（旧 → 新）；
    /// beforeMessageId 为空时回退返回最近 count 条；游标为首条或不存在时返回空。
    /// topicId 非空时仅在指定话题内分页。</summary>
    IReadOnlyList<GroupMessage> MessagesBefore(string groupId, string? beforeMessageId, int count, string? topicId = null);

    /// <summary>按游标正向增量：返回指定消息之后（更新）的 count 条，按时间序（旧 → 新）；
    /// 游标不存在时返回空。供外部 AG-UI 桥接「会话建立后只发增量」使用。
    /// topicId 非空时仅在指定话题内过滤。</summary>
    IReadOnlyList<GroupMessage> MessagesAfter(string groupId, string afterMessageId, int count, string? topicId = null);

    /// <summary>某群的全部消息（按时间序，含撤回），供持久化快照使用。</summary>
    IReadOnlyList<GroupMessage> AllMessages(string groupId);

    /// <summary>按关键词全文搜索群内消息（不区分大小写子串匹配），按时间倒序返回最多 limit 条；
    /// topicId 非空时限定话题；结果含已撤回消息（由调用方过滤）。</summary>
    IReadOnlyList<GroupMessage> SearchMessages(string groupId, string keyword, string? topicId, int limit);

    /// <summary>物理删除指定时间戳之前（更早）的消息（数据保留策略），groupId 为空 = 全部群；返回删除条数。</summary>
    int DeleteMessagesBefore(long beforeTimestamp, string? groupId = null);

    IReadOnlyList<Group> AllGroups();

    // 已读位点与未读（群列表活跃度排序 / 未读提示）：读位点按 (成员, 群, 话题) 记录最后已读消息时间戳
    long GetReadAt(string memberId, string groupId, string topicId);
    void SetReadAt(string memberId, string groupId, string topicId, long timestamp);

    /// <summary>该群最近一条消息的时间戳（无消息返回 null；用于群列表活跃度排序）。</summary>
    long? LastMessageAt(string groupId);

    /// <summary>某话题（topicId 为 null 时全部话题）在 afterTimestamp 之后、未撤回的消息数（未读计数）。</summary>
    int CountUnread(string groupId, string? topicId, long afterTimestamp);

    /// <summary>清空全部群 / 成员 / 话题 / 消息 / 已读位点（系统初始化用）。</summary>
    void ClearAll();
}
