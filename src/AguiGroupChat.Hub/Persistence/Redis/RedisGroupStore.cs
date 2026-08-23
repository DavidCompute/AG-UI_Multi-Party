using AguiGroupChat.Hub.Models;
using AguiGroupChat.Hub.Storage;
using StackExchange.Redis;

namespace AguiGroupChat.Hub.Persistence.Redis;

/// <summary>
/// Redis 群组存储（6.2 Web 多副本横向扩展）：把群 / 成员 / 话题 / 消息序列化为 JSON 存于 Redis，
/// 多副本读写同一批 key 实现共享。语义与内存 / 关系库实现一致：
/// 消息按 <c>agui:msgs:{gid}</c> 有序列表维护（追加序即时间序）；撤回标志独立 key 存储（
/// <see cref="GroupMessage.Recalled"/> 被 <c>[JsonIgnore]</c>，无法随正文 round-trip）；
/// 原地修改经 <c>UpdateX</c> 显式写回（与既有调用模型一致）。
/// </summary>
public sealed class RedisGroupStore : IGroupStore
{
    private static readonly RedisValue RecalledTrue = "1";
    private readonly RedisContext _ctx;

    public RedisGroupStore(RedisContext ctx) => _ctx = ctx;

    // ================= 群组 =================

    public bool AddGroup(Group group)
    {
        var ok = _ctx.Db.StringSet(RedisContext.GroupKey(group.GroupId), RedisContext.Serialize(group),
            when: When.NotExists);
        return ok;
    }

    public bool CreateGroupWithMembers(Group group, IReadOnlyList<GroupMember> members)
    {
        if (!AddGroup(group)) return false;
        foreach (var m in members) AddMember(group.GroupId, m);
        return true;
    }

    public Group? GetGroup(string groupId)
    {
        var v = _ctx.Db.StringGet(RedisContext.GroupKey(groupId));
        return RedisContext.Deserialize<Group>(v);
    }

    public void UpdateGroup(Group group)
        => _ctx.Db.StringSet(RedisContext.GroupKey(group.GroupId), RedisContext.Serialize(group));

    public bool RemoveGroup(string groupId)
    {
        var db = _ctx.Db;
        // 删除消息索引与全部消息 key（先取 id 列表再逐个删）
        var ids = db.ListRange(RedisContext.MsgIndexKey(groupId)).Select(x => x.ToString()).ToArray();
        var keys = new List<RedisKey> { RedisContext.GroupKey(groupId) };
        keys.AddRange(ids.Select(id => (RedisKey)RedisContext.MsgKey(groupId, id)));
        keys.AddRange(ids.Select(id => (RedisKey)RedisContext.RecalledKey(groupId, id)));
        keys.Add(RedisContext.MembersKey(groupId));
        keys.Add(RedisContext.TopicsKey(groupId));
        keys.Add(RedisContext.MsgIndexKey(groupId));
        keys.Add(RedisContext.LastMsgKey(groupId));
        db.KeyDelete(keys.Distinct().ToArray());
        return true;
    }

    public IReadOnlyList<Group> AllGroups()
    {
        // Redis 无原生扫描 group key 的高效方式；用 SCAN 按前缀取全部群。
        var server = _ctx.Mux.GetServer(_ctx.Mux.GetEndPoints()[0]);
        var gids = server.Keys(pattern: "agui:group:*")
            .Select(k => k.ToString()["agui:group:".Length..]).ToArray();
        var values = _ctx.Db.StringGet(gids.Select(g => (RedisKey)RedisContext.GroupKey(g)).ToArray());
        return values.Where(v => !v.IsNullOrEmpty).Select(v => RedisContext.Deserialize<Group>(v!)!).ToList();
    }

    // ================= 成员 =================

    public bool AddMember(string groupId, GroupMember member)
        => _ctx.Db.HashSet(RedisContext.MembersKey(groupId),
            member.MemberId, RedisContext.Serialize(member), When.NotExists);

    public bool IsMember(string groupId, string memberId)
        => _ctx.Db.HashExists(RedisContext.MembersKey(groupId), memberId);

    public GroupMember? GetMember(string groupId, string memberId)
    {
        var v = _ctx.Db.HashGet(RedisContext.MembersKey(groupId), memberId);
        return RedisContext.Deserialize<GroupMember>(v);
    }

    public void UpdateMember(string groupId, GroupMember member)
        => _ctx.Db.HashSet(RedisContext.MembersKey(groupId), member.MemberId, RedisContext.Serialize(member));

    public bool RemoveMember(string groupId, string memberId)
        => _ctx.Db.HashDelete(RedisContext.MembersKey(groupId), memberId);

    public bool UpdateMemberStatus(string groupId, string memberId, OnlineStatus status)
    {
        var member = GetMember(groupId, memberId);
        if (member is null) return false;
        member.OnlineStatus = status;
        UpdateMember(groupId, member);
        return true;
    }

    public IReadOnlyList<GroupMember> ListMembers(string groupId)
    {
        var entries = _ctx.Db.HashGetAll(RedisContext.MembersKey(groupId));
        return entries.Where(e => !e.Value.IsNullOrEmpty)
            .Select(e => RedisContext.Deserialize<GroupMember>(e.Value!)!).ToList();
    }

    public int MemberCount(string groupId) => (int)_ctx.Db.HashLength(RedisContext.MembersKey(groupId));

    public IReadOnlyList<Group> GroupsOf(string memberId)
        => AllGroups().Where(g => IsMember(g.GroupId, memberId)).ToList();

    // ================= 话题 =================

    public bool AddTopic(GroupTopic topic)
    {
        var db = _ctx.Db;
        var existing = db.HashGet(RedisContext.TopicsKey(topic.GroupId), topic.TopicId);
        if (!existing.IsNullOrEmpty) return false;
        return db.HashSet(RedisContext.TopicsKey(topic.GroupId), topic.TopicId, RedisContext.Serialize(topic), When.NotExists);
    }

    public GroupTopic? GetTopic(string groupId, string topicId)
    {
        var v = _ctx.Db.HashGet(RedisContext.TopicsKey(groupId), topicId);
        return RedisContext.Deserialize<GroupTopic>(v);
    }

    public IReadOnlyList<GroupTopic> ListTopics(string groupId)
    {
        var entries = _ctx.Db.HashGetAll(RedisContext.TopicsKey(groupId));
        return entries.Where(e => !e.Value.IsNullOrEmpty)
            .Select(e => RedisContext.Deserialize<GroupTopic>(e.Value!)!).ToList();
    }

    public bool RemoveTopic(string groupId, string topicId)
        => _ctx.Db.HashDelete(RedisContext.TopicsKey(groupId), topicId);

    public int RemoveTopicMessages(string groupId, string topicId)
    {
        var db = _ctx.Db;
        var msgs = LoadMessages(groupId);
        var removable = msgs.Where(m => m.TopicId == topicId || (topicId == "main" && m.TopicId is null)).ToList();
        if (removable.Count == 0) return 0;
        var removedIds = removable.Select(m => m.MessageId).ToHashSet(StringComparer.Ordinal);
        // 重建消息索引（剔除被移除的）
        var keep = msgs.Where(m => !removedIds.Contains(m.MessageId)).Select(m => m.MessageId).ToArray();
        if (keep.Length == 0) db.KeyDelete(RedisContext.MsgIndexKey(groupId));
        else
        {
            db.KeyDelete(RedisContext.MsgIndexKey(groupId));
            db.ListRightPush(RedisContext.MsgIndexKey(groupId), keep.Select(x => (RedisValue)x).ToArray());
        }
        var keys = removedIds.Select(id => (RedisKey)RedisContext.MsgKey(groupId, id))
            .Concat(removedIds.Select(id => (RedisKey)RedisContext.RecalledKey(groupId, id)))
            .ToArray();
        if (keys.Length > 0) db.KeyDelete(keys);
        return removable.Count;
    }

    // ================= 消息 =================

    public void AddMessage(GroupMessage message)
    {
        var db = _ctx.Db;
        // 去重：消息键不存在才写入（与内存 / 数据库版语义一致：重复 message_id 静默跳过）
        var key = RedisContext.MsgKey(message.GroupId, message.MessageId);
        if (!db.StringSet(key, RedisContext.Serialize(message), when: When.NotExists)) return;
        db.ListRightPush(RedisContext.MsgIndexKey(message.GroupId), message.MessageId);
        db.StringSet(RedisContext.LastMsgKey(message.GroupId), message.Timestamp);
    }

    public GroupMessage? GetMessage(string groupId, string messageId)
    {
        var msg = RedisContext.Deserialize<GroupMessage>(_ctx.Db.StringGet(RedisContext.MsgKey(groupId, messageId)));
        if (msg is null) return null;
        msg.Recalled = _ctx.Db.StringGet(RedisContext.RecalledKey(groupId, messageId)) == RecalledTrue;
        return msg;
    }

    public void UpdateMessage(GroupMessage message)
    {
        _ctx.Db.StringSet(RedisContext.MsgKey(message.GroupId, message.MessageId), RedisContext.Serialize(message));
        if (message.Recalled)
            _ctx.Db.StringSet(RedisContext.RecalledKey(message.GroupId, message.MessageId), RecalledTrue);
    }

    public bool RecallMessage(string groupId, string messageId)
    {
        var db = _ctx.Db;
        if (db.StringGet(RedisContext.MsgKey(groupId, messageId)).IsNullOrEmpty) return false;
        if (db.StringGet(RedisContext.RecalledKey(groupId, messageId)) == RecalledTrue) return false;
        db.StringSet(RedisContext.RecalledKey(groupId, messageId), RecalledTrue);
        return true;
    }

    /// <summary>加载某群全部消息（按索引序即时间序；附撤回标志）。</summary>
    private List<GroupMessage> LoadMessages(string groupId)
    {
        var ids = _ctx.Db.ListRange(RedisContext.MsgIndexKey(groupId)).Select(x => x.ToString()).ToArray();
        if (ids.Length == 0) return [];
        var values = _ctx.Db.StringGet(ids.Select(id => (RedisKey)RedisContext.MsgKey(groupId, id)).ToArray());
        var result = new List<GroupMessage>(ids.Length);
        for (var i = 0; i < ids.Length; i++)
        {
            var msg = RedisContext.Deserialize<GroupMessage>(values[i]);
            if (msg is null) continue;
            msg.Recalled = _ctx.Db.StringGet(RedisContext.RecalledKey(groupId, msg.MessageId)) == RecalledTrue;
            result.Add(msg);
        }
        return result;
    }

    public IReadOnlyList<GroupMessage> AllMessages(string groupId) => LoadMessages(groupId);

    public IReadOnlyList<GroupMessage> RecentMessages(string groupId, int count, string? topicId = null)
    {
        var filtered = FilterByTopic(LoadMessages(groupId), topicId);
        return filtered.Skip(Math.Max(0, filtered.Count - count)).ToList();
    }

    public IReadOnlyList<GroupMessage> MessagesBefore(string groupId, string? beforeMessageId, int count, string? topicId = null)
    {
        if (count <= 0) return [];
        var filtered = FilterByTopic(LoadMessages(groupId), topicId);
        if (filtered.Count == 0) return [];
        IReadOnlyList<GroupMessage> before;
        if (string.IsNullOrEmpty(beforeMessageId))
        {
            before = filtered.OrderByDescending(m => m.Timestamp).ThenByDescending(m => m.MessageId).Take(count).ToList();
        }
        else
        {
            // 游标定位不区分话题（与内存版一致）
            var all = LoadMessages(groupId);
            var cursor = all.FirstOrDefault(m => m.MessageId == beforeMessageId);
            if (cursor is null) return [];
            before = filtered
                .Where(m => m.Timestamp < cursor.Timestamp
                    || (m.Timestamp == cursor.Timestamp && string.CompareOrdinal(m.MessageId, beforeMessageId) < 0))
                .OrderByDescending(m => m.Timestamp).ThenByDescending(m => m.MessageId)
                .Take(count).ToList();
        }
        before = before.Reverse().ToList();
        return before;
    }

    public IReadOnlyList<GroupMessage> MessagesAfter(string groupId, string afterMessageId, int count, string? topicId = null)
    {
        if (count <= 0) return [];
        var all = LoadMessages(groupId);
        var cursor = all.FirstOrDefault(m => m.MessageId == afterMessageId);
        if (cursor is null) return [];
        return all
            .Where(m => topicId is null || m.TopicId == topicId)
            .Where(m => m.Timestamp > cursor.Timestamp
                || (m.Timestamp == cursor.Timestamp && string.CompareOrdinal(m.MessageId, afterMessageId) > 0))
            .OrderBy(m => m.Timestamp).ThenBy(m => m.MessageId)
            .Take(count).ToList();
    }

    public IReadOnlyList<GroupMessage> SearchMessages(string groupId, string keyword, string? topicId, int limit)
    {
        if (string.IsNullOrWhiteSpace(keyword) || limit <= 0) return [];
        var kw = keyword.Trim();
        IReadOnlyList<GroupMessage> source;
        if (topicId is null) source = LoadMessages(groupId);
        else
        {
            source = LoadMessages(groupId).Where(m => m.TopicId == topicId).ToList();
        }
        return source.Where(m => m.Content.Contains(kw, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(m => m.Timestamp).ThenByDescending(m => m.MessageId)
            .Take(Math.Min(limit, 100)).ToList();
    }

    public int DeleteMessagesBefore(long beforeTimestamp, string? groupId = null)
    {
        var db = _ctx.Db;
        var totalRemoved = 0;
        if (groupId is not null)
        {
            totalRemoved = DeleteMessagesBeforeInGroup(groupId, beforeTimestamp, db);
        }
        else
        {
            var server = _ctx.Mux.GetServer(_ctx.Mux.GetEndPoints()[0]);
            var gids = server.Keys(pattern: "agui:group:*")
                .Select(k => k.ToString()["agui:group:".Length..]).ToArray();
            foreach (var gid in gids) totalRemoved += DeleteMessagesBeforeInGroup(gid, beforeTimestamp, db);
        }
        return totalRemoved;
    }

    private int DeleteMessagesBeforeInGroup(string groupId, long beforeTimestamp, IDatabase db)
    {
        var msgs = LoadMessages(groupId);
        var removable = msgs.Where(m => m.Timestamp < beforeTimestamp).ToList();
        if (removable.Count == 0) return 0;
        var removedIds = removable.Select(m => m.MessageId).ToHashSet(StringComparer.Ordinal);
        var keep = msgs.Where(m => !removedIds.Contains(m.MessageId)).Select(m => m.MessageId).ToArray();
        db.KeyDelete(RedisContext.MsgIndexKey(groupId));
        if (keep.Length > 0) db.ListRightPush(RedisContext.MsgIndexKey(groupId), keep.Select(x => (RedisValue)x).ToArray());
        var keys = new List<RedisKey>();
        foreach (var id in removedIds)
        {
            keys.Add(RedisContext.MsgKey(groupId, id));
            keys.Add(RedisContext.RecalledKey(groupId, id));
        }
        if (keys.Count > 0) db.KeyDelete(keys.Distinct().ToArray());
        return removable.Count;
    }

    // ================= 已读位点与未读 =================

    public long GetReadAt(string memberId, string groupId, string topicId)
        => (long)_ctx.Db.StringGet(RedisContext.ReadKey(memberId, groupId, topicId));

    public void SetReadAt(string memberId, string groupId, string topicId, long timestamp)
    {
        var key = RedisContext.ReadKey(memberId, groupId, topicId);
        var current = (long)_ctx.Db.StringGet(key);
        if (timestamp > current) _ctx.Db.StringSet(key, timestamp); // 只前进不回退
    }

    public long? LastMessageAt(string groupId)
    {
        var v = _ctx.Db.StringGet(RedisContext.LastMsgKey(groupId));
        return v.IsNullOrEmpty ? null : (long)v;
    }

    public int CountUnread(string groupId, string? topicId, long afterTimestamp)
        => LoadMessages(groupId).Count(m => !m.Recalled && m.Timestamp > afterTimestamp
            && (topicId is null || m.TopicId == topicId || (topicId == "main" && m.TopicId is null)));

    public void ClearAll() => _ctx.FlushAguiKeys();

    private static List<GroupMessage> FilterByTopic(List<GroupMessage> msgs, string? topicId)
        => topicId is null ? msgs : msgs.Where(m => m.TopicId == topicId).ToList();
}
