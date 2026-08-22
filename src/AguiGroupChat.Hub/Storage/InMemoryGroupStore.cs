using System.Collections.Concurrent;
using AguiGroupChat.Hub.Models;
using AguiGroupChat.Hub.Persistence;

namespace AguiGroupChat.Hub.Storage;

/// <summary>
/// 进程内线程安全的内存存储：群组 / 成员 / 消息历史（有界）。
/// 数据变更经 <see cref="ChangeHub"/> 通知持久化服务（在线状态等瞬时量不通知）。
/// </summary>
public sealed class InMemoryGroupStore : IGroupStore
{
    private readonly ConcurrentDictionary<string, Group> _groups = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, GroupMember>> _members = new();
    private readonly ConcurrentDictionary<string, List<GroupMessage>> _messages = new();
    private readonly ConcurrentDictionary<string, List<GroupTopic>> _topics = new();
    private readonly ConcurrentDictionary<(string MemberId, string GroupId, string TopicId), long> _reads = new();
    private readonly int _historyLimit;
    private readonly ChangeHub? _changes;

    public InMemoryGroupStore(int historyLimit = 200, ChangeHub? changes = null)
    {
        _historyLimit = historyLimit;
        _changes = changes;
    }

    public bool AddGroup(Group group)
    {
        var ok = _groups.TryAdd(group.GroupId, group);
        if (ok) _changes?.Notify();
        return ok;
    }

    /// <summary>事务性建群：内存实现无事务概念，依次写入（群冲突返回 false；成员按 AddMember 语义写入）。</summary>
    public bool CreateGroupWithMembers(Group group, IReadOnlyList<GroupMember> members)
    {
        if (!AddGroup(group)) return false;
        foreach (var m in members) AddMember(group.GroupId, m);
        return true;
    }

    public Group? GetGroup(string groupId) => _groups.TryGetValue(groupId, out var g) ? g : null;

    public bool RemoveGroup(string groupId)
    {
        _members.TryRemove(groupId, out _);
        _messages.TryRemove(groupId, out _);
        _topics.TryRemove(groupId, out _);
        foreach (var key in _reads.Keys.Where(k => k.GroupId == groupId).ToList())
            _reads.TryRemove(key, out _);
        var ok = _groups.TryRemove(groupId, out _);
        if (ok) _changes?.Notify();
        return ok;
    }

    // ================= 话题（群聊扩展） =================

    public bool AddTopic(GroupTopic topic)
    {
        var list = _topics.GetOrAdd(topic.GroupId, _ => new());
        lock (list)
        {
            if (list.Any(t => t.TopicId == topic.TopicId)) return false;
            list.Add(topic);
        }
        _changes?.Notify();
        return true;
    }

    public GroupTopic? GetTopic(string groupId, string topicId)
    {
        if (!_topics.TryGetValue(groupId, out var list)) return null;
        lock (list) return list.FirstOrDefault(t => t.TopicId == topicId); // 与写路径（AddTopic/RemoveTopic）同一把锁
    }

    public bool RemoveTopic(string groupId, string topicId)
    {
        if (!_topics.TryGetValue(groupId, out var list)) return false;
        bool removed;
        lock (list)
        {
            removed = list.RemoveAll(t => t.TopicId == topicId) > 0;
            if (removed) _changes?.Notify();
        }
        foreach (var key in _reads.Keys.Where(k => k.GroupId == groupId && k.TopicId == topicId).ToList())
            _reads.TryRemove(key, out _);
        return removed;
    }

    public IReadOnlyList<GroupTopic> ListTopics(string groupId)
    {
        if (!_topics.TryGetValue(groupId, out var list)) return [];
        lock (list) return list.ToList(); // 与写路径（AddTopic/RemoveTopic）同一把锁
    }

    public int RemoveTopicMessages(string groupId, string topicId)
    {
        if (!_messages.TryGetValue(groupId, out var list)) return 0;
        lock (list)
        {
            // main 话题兼容历史 NULL topic_id（旧消息无话题归属）
            var removed = list.RemoveAll(m => m.TopicId == topicId || (topicId == "main" && m.TopicId is null));
            if (removed > 0) _changes?.Notify();
            return removed;
        }
    }

    public bool AddMember(string groupId, GroupMember member)
    {
        var ok = _members.GetOrAdd(groupId, _ => new()).TryAdd(member.MemberId, member);
        if (ok) _changes?.Notify();
        return ok;
    }

    public bool IsMember(string groupId, string memberId)
        => _members.TryGetValue(groupId, out var set) && set.ContainsKey(memberId);

    public GroupMember? GetMember(string groupId, string memberId)
        => _members.TryGetValue(groupId, out var set) && set.TryGetValue(memberId, out var m) ? m : null;

    public bool RemoveMember(string groupId, string memberId)
    {
        var ok = _members.TryGetValue(groupId, out var set) && set.TryRemove(memberId, out _);
        if (ok) _changes?.Notify();
        return ok;
    }

    public bool UpdateMemberStatus(string groupId, string memberId, OnlineStatus status)
    {
        var member = GetMember(groupId, memberId);
        if (member is null) return false;
        member.OnlineStatus = status;
        return true; // 在线状态为瞬时量，不触发持久化
    }

    public IReadOnlyList<GroupMember> ListMembers(string groupId)
        => _members.TryGetValue(groupId, out var set) ? set.Values.ToList() : [];

    public int MemberCount(string groupId)
        => _members.TryGetValue(groupId, out var set) ? set.Count : 0;

    public IReadOnlyList<Group> GroupsOf(string memberId)
        => _groups.Values.Where(g => IsMember(g.GroupId, memberId)).ToList();

    public IReadOnlyList<Group> AllGroups() => _groups.Values.ToList();

    public void AddMessage(GroupMessage message)
    {
        var list = _messages.GetOrAdd(message.GroupId, _ => new());
        lock (list)
        {
            if (list.Any(m => m.MessageId == message.MessageId)) return; // 与数据库版 TryInsert 语义一致：重复 message_id 静默跳过，不重复追加
            list.Add(message);
            if (list.Count > _historyLimit)
                list.RemoveRange(0, list.Count - _historyLimit);
        }
        _changes?.Notify();
    }

    // ================= 原地修改落库（内存实现：对象已在内存中原地变更，无需额外操作） =================

    public void UpdateGroup(Group group) { }

    public void UpdateMember(string groupId, GroupMember member) { }

    public void UpdateMessage(GroupMessage message) { }

    public GroupMessage? GetMessage(string groupId, string messageId)
    {
        if (!_messages.TryGetValue(groupId, out var list)) return null;
        lock (list) return list.FirstOrDefault(m => m.MessageId == messageId);
    }

    public bool RecallMessage(string groupId, string messageId)
    {
        var msg = GetMessage(groupId, messageId);
        if (msg is null || msg.Recalled) return false;
        msg.Recalled = true;
        _changes?.Notify();
        return true;
    }

    public IReadOnlyList<GroupMessage> RecentMessages(string groupId, int count, string? topicId = null)
    {
        if (!_messages.TryGetValue(groupId, out var list)) return [];
        lock (list)
        {
            var filtered = topicId is null ? list : list.Where(m => m.TopicId == topicId).ToList();
            return filtered.Skip(Math.Max(0, filtered.Count - count)).ToList();
        }
    }

    public IReadOnlyList<GroupMessage> MessagesBefore(string groupId, string? beforeMessageId, int count, string? topicId = null)
    {
        if (count <= 0) return [];
        if (!_messages.TryGetValue(groupId, out var list)) return [];
        lock (list)
        {
            var filtered = topicId is null ? list : list.Where(m => m.TopicId == topicId).ToList();
            if (filtered.Count == 0) return [];
            // 与数据库版一致：按 (timestamp DESC, message_id DESC) 取游标前（不含）count 条，再反转为时间序（旧→新）
            if (string.IsNullOrEmpty(beforeMessageId))
            {
                var latest = filtered.OrderByDescending(m => m.Timestamp).ThenByDescending(m => m.MessageId)
                    .Take(count).ToList();
                latest.Reverse();
                return latest;
            }
            // 游标定位不区分话题（与数据库版按 group_id + message_id 查时间戳一致）
            var cursor = list.FirstOrDefault(m => m.MessageId == beforeMessageId);
            if (cursor is null) return []; // 游标不存在 → 没有更早消息
            var before = filtered
                .Where(m => m.Timestamp < cursor.Timestamp
                    || (m.Timestamp == cursor.Timestamp && string.CompareOrdinal(m.MessageId, beforeMessageId) < 0))
                .OrderByDescending(m => m.Timestamp).ThenByDescending(m => m.MessageId)
                .Take(count).ToList();
            before.Reverse(); // 时间序（旧→新）
            return before;
        }
    }

    public IReadOnlyList<GroupMessage> AllMessages(string groupId)
    {
        if (!_messages.TryGetValue(groupId, out var list)) return [];
        lock (list) return list.ToList();
    }

    public IReadOnlyList<GroupMessage> SearchMessages(string groupId, string keyword, string? topicId, int limit)
    {
        if (string.IsNullOrWhiteSpace(keyword) || limit <= 0) return [];
        if (!_messages.TryGetValue(groupId, out var list)) return [];
        var kw = keyword.Trim();
        lock (list)
        {
            return list.Where(m => (topicId is null || m.TopicId == topicId)
                    && m.Content.Contains(kw, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(m => m.Timestamp).ThenByDescending(m => m.MessageId)
                .Take(Math.Min(limit, 100))
                .ToList();
        }
    }

    public int DeleteMessagesBefore(long beforeTimestamp, string? groupId = null)
    {
        var removed = 0;
        if (groupId is not null)
        {
            if (!_messages.TryGetValue(groupId, out var list)) return 0;
            lock (list)
            {
                removed = list.RemoveAll(m => m.Timestamp < beforeTimestamp);
            }
        }
        else
        {
            foreach (var list in _messages.Values)
            {
                lock (list)
                {
                    removed += list.RemoveAll(m => m.Timestamp < beforeTimestamp);
                }
            }
        }
        if (removed > 0) _changes?.Notify();
        return removed;
    }

    public IReadOnlyList<GroupMessage> MessagesAfter(string groupId, string afterMessageId, int count, string? topicId = null)
    {
        if (count <= 0) return [];
        if (!_messages.TryGetValue(groupId, out var list)) return [];
        lock (list)
        {
            // 与数据库版一致：按 (timestamp ASC, message_id ASC) 取游标后（不含）count 条，时间序（旧→新）
            var cursor = list.FirstOrDefault(m => m.MessageId == afterMessageId);
            if (cursor is null) return []; // 游标不存在 → 无增量
            return list
                .Where(m => topicId is null || m.TopicId == topicId)
                .Where(m => m.Timestamp > cursor.Timestamp
                    || (m.Timestamp == cursor.Timestamp && string.CompareOrdinal(m.MessageId, afterMessageId) > 0))
                .OrderBy(m => m.Timestamp).ThenBy(m => m.MessageId)
                .Take(count).ToList();
        }
    }

    // ================= 已读位点与未读（群列表活跃度 / 未读提示） =================

    public long GetReadAt(string memberId, string groupId, string topicId)
        => _reads.TryGetValue((memberId, groupId, topicId), out var at) ? at : 0;

    public void SetReadAt(string memberId, string groupId, string topicId, long timestamp)
    {
        var key = (memberId, groupId, topicId);
        // 已读位点只前进不回退：仅当新值更大时才覆盖（AddOrUpdate 原子比较，防并发下旧位点回退）
        _reads.AddOrUpdate(key, timestamp, (_, current) => timestamp > current ? timestamp : current);
        _changes?.Notify();
    }

    public long? LastMessageAt(string groupId)
    {
        if (!_messages.TryGetValue(groupId, out var list)) return null;
        lock (list) return list.Count == 0 ? null : list.Max(m => m.Timestamp);
    }

    public int CountUnread(string groupId, string? topicId, long afterTimestamp)
    {
        if (!_messages.TryGetValue(groupId, out var list)) return 0;
        lock (list)
        {
            return list.Count(m => !m.Recalled && m.Timestamp > afterTimestamp
                && (topicId is null || m.TopicId == topicId || (topicId == "main" && m.TopicId is null)));
        }
    }

    public void ClearAll()
    {
        _groups.Clear();
        _members.Clear();
        _messages.Clear();
        _topics.Clear();
        _reads.Clear();
        _changes?.Notify();
    }
}
