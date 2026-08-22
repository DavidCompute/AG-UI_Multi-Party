using System.Text.Json;
using AguiGroupChat.Hub.Infra;
using AguiGroupChat.Hub.Models;
using AguiGroupChat.Hub.Storage;
using Npgsql;

namespace AguiGroupChat.Hub.Persistence.Postgres;

/// <summary>
/// PostgreSQL 实现：群组 / 成员 / 话题 / 消息。接口保持同步（Npgsql 同步 API），
/// 枚举与列表字段以字符串 / JSON 文本列存储，分页与游标语义与内存实现一致。
/// </summary>
public sealed class PostgresGroupStore : IGroupStore
{
    private readonly PostgresStore _pg;

    public PostgresGroupStore(PostgresStore pg) => _pg = pg;

    // ================= 群组 =================

    public bool AddGroup(Group group)
    {
        using var conn = _pg.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO agui_groups (group_id, group_name, group_avatar, owner_id, member_count, create_time, extra, is_private)
            VALUES (@gid, @name, @avatar, @owner, @count, @time, @extra, @isPrivate)
            ON CONFLICT (group_id) DO NOTHING
            """;
        AddGroupParams(cmd, group);
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>事务性建群：先插群、再逐成员插入，全部成功才提交（失败自动回滚，防半建状态）。</summary>
    public bool CreateGroupWithMembers(Group group, IReadOnlyList<GroupMember> members)
    {
        using var conn = _pg.Open();
        using var tx = conn.BeginTransaction();
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO agui_groups (group_id, group_name, group_avatar, owner_id, member_count, create_time, extra, is_private)
                VALUES (@gid, @name, @avatar, @owner, @count, @time, @extra, @isPrivate)
                ON CONFLICT (group_id) DO NOTHING
                """;
            AddGroupParams(cmd, group);
            if (cmd.ExecuteNonQuery() == 0) return false; // ID 冲突：群已存在（事务随 using 释放自动回滚）
        }
        foreach (var m in members)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO agui_group_members
                    (group_id, member_id, member_type, nickname, avatar, role, online_status, join_time,
                     trigger_mode, keywords, is_trigger_overridden, extra)
                VALUES (@gid, @mid, @type, @nick, @avatar, @role, @status, @join, @tMode, @keywords, @overridden, @extra)
                ON CONFLICT (group_id, member_id) DO NOTHING
                """;
            AddMemberParams(cmd, group.GroupId, m);
            cmd.ExecuteNonQuery(); // 成员重复视为已存在（与 AddMember 语义一致）；其余异常向上抛 → 事务回滚
        }
        tx.Commit();
        return true;
    }

    public Group? GetGroup(string groupId)
    {
        using var conn = _pg.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM agui_groups WHERE group_id = @gid";
        cmd.Parameters.AddWithValue("gid", groupId);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadGroup(reader) : null;
    }

    public bool RemoveGroup(string groupId)
    {
        using var conn = _pg.Open();
        using var tx = conn.BeginTransaction();
        // 逐条 DELETE（Npgsql extended protocol 不支持带参数的多语句批处理）：
        //   按外键顺序先删子表再删主表：agui_agent_registrations → agui_messages → agui_topics → agui_group_members → agui_groups
        //   每条命令挂到同一事务，全部成功才 Commit，异常时 using 释放自动回滚
        foreach (var table in new[]
        {
            "agui_agent_registrations", "agui_messages", "agui_topics", "agui_group_members", "agui_group_reads", "agui_groups",
        })
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"DELETE FROM {table} WHERE group_id = @gid";
            cmd.Parameters.AddWithValue("gid", groupId);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return true;
    }

    public IReadOnlyList<Group> AllGroups()
    {
        using var conn = _pg.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM agui_groups";
        var list = new List<Group>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) list.Add(ReadGroup(reader));
        return list;
    }

    // ================= 成员 =================

    public bool AddMember(string groupId, GroupMember member)
    {
        using var conn = _pg.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO agui_group_members
                (group_id, member_id, member_type, nickname, avatar, role, online_status, join_time,
                 trigger_mode, keywords, is_trigger_overridden, extra)
            VALUES (@gid, @mid, @type, @nick, @avatar, @role, @status, @join, @tMode, @keywords, @overridden, @extra)
            ON CONFLICT (group_id, member_id) DO NOTHING
            """;
        AddMemberParams(cmd, groupId, member);
        return cmd.ExecuteNonQuery() > 0;
    }

    public bool IsMember(string groupId, string memberId)
    {
        using var conn = _pg.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM agui_group_members WHERE group_id = @gid AND member_id = @mid)";
        cmd.Parameters.AddWithValue("gid", groupId);
        cmd.Parameters.AddWithValue("mid", memberId);
        return (bool)cmd.ExecuteScalar()!;
    }

    public GroupMember? GetMember(string groupId, string memberId)
    {
        using var conn = _pg.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM agui_group_members WHERE group_id = @gid AND member_id = @mid";
        cmd.Parameters.AddWithValue("gid", groupId);
        cmd.Parameters.AddWithValue("mid", memberId);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadMember(reader) : null;
    }

    public bool RemoveMember(string groupId, string memberId)
    {
        using var conn = _pg.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM agui_group_members WHERE group_id = @gid AND member_id = @mid";
        cmd.Parameters.AddWithValue("gid", groupId);
        cmd.Parameters.AddWithValue("mid", memberId);
        return cmd.ExecuteNonQuery() > 0;
    }

    public bool UpdateMemberStatus(string groupId, string memberId, OnlineStatus status)
    {
        using var conn = _pg.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE agui_group_members SET online_status = @status WHERE group_id = @gid AND member_id = @mid";
        cmd.Parameters.AddWithValue("status", status.ToString());
        cmd.Parameters.AddWithValue("gid", groupId);
        cmd.Parameters.AddWithValue("mid", memberId);
        return cmd.ExecuteNonQuery() > 0;
    }

    public IReadOnlyList<GroupMember> ListMembers(string groupId)
    {
        using var conn = _pg.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM agui_group_members WHERE group_id = @gid ORDER BY join_time";
        cmd.Parameters.AddWithValue("gid", groupId);
        var list = new List<GroupMember>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) list.Add(ReadMember(reader));
        return list;
    }

    public int MemberCount(string groupId)
    {
        using var conn = _pg.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM agui_group_members WHERE group_id = @gid";
        cmd.Parameters.AddWithValue("gid", groupId);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public IReadOnlyList<Group> GroupsOf(string memberId)
    {
        using var conn = _pg.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT g.* FROM agui_groups g
            JOIN agui_group_members m ON m.group_id = g.group_id
            WHERE m.member_id = @mid ORDER BY g.create_time
            """;
        cmd.Parameters.AddWithValue("mid", memberId);
        var list = new List<Group>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) list.Add(ReadGroup(reader));
        return list;
    }

    // ================= 话题 =================

    public bool AddTopic(GroupTopic topic)
    {
        using var conn = _pg.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO agui_topics (topic_id, group_id, name, creator_id, created_at)
            VALUES (@tid, @gid, @name, @creator, @created)
            ON CONFLICT (topic_id) DO NOTHING
            """;
        cmd.Parameters.AddWithValue("tid", topic.TopicId);
        cmd.Parameters.AddWithValue("gid", topic.GroupId);
        cmd.Parameters.AddWithValue("name", topic.Name);
        cmd.Parameters.AddWithValue("creator", topic.CreatorId);
        cmd.Parameters.AddWithValue("created", topic.CreatedAt);
        return cmd.ExecuteNonQuery() > 0;
    }

    public bool RemoveTopic(string groupId, string topicId)
    {
        using var conn = _pg.Open();
        using var tx = conn.BeginTransaction();
        // 删除话题 + 清理该话题已读位点：同一事务，全部成功才提交（防话题删了 reads 残留 / 反向的半删状态）
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM agui_topics WHERE group_id = @gid AND topic_id = @tid";
            cmd.Parameters.AddWithValue("gid", groupId);
            cmd.Parameters.AddWithValue("tid", topicId);
            if (cmd.ExecuteNonQuery() == 0) return false; // 话题不存在：无事可做，事务随 using 释放回滚
        }
        using (var del = conn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM agui_group_reads WHERE group_id = @gid AND topic_id = @tid";
            del.Parameters.AddWithValue("gid", groupId);
            del.Parameters.AddWithValue("tid", topicId);
            del.ExecuteNonQuery();
        }
        tx.Commit();
        return true;
    }

    public int RemoveTopicMessages(string groupId, string topicId)
    {
        using var conn = _pg.Open();
        using var cmd = conn.CreateCommand();
        // main 话题兼容历史 NULL topic_id（旧消息无话题归属）
        cmd.CommandText = "DELETE FROM agui_messages WHERE group_id = @gid AND (topic_id = @tid OR (@tid = 'main' AND topic_id IS NULL))";
        cmd.Parameters.AddWithValue("gid", groupId);
        cmd.Parameters.AddWithValue("tid", topicId);
        return cmd.ExecuteNonQuery();
    }

    public GroupTopic? GetTopic(string groupId, string topicId)
    {
        using var conn = _pg.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM agui_topics WHERE group_id = @gid AND topic_id = @tid";
        cmd.Parameters.AddWithValue("gid", groupId);
        cmd.Parameters.AddWithValue("tid", topicId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        return new GroupTopic
        {
            TopicId = reader.GetString(0),
            GroupId = reader.GetString(1),
            Name = reader.GetString(2),
            CreatorId = reader.GetString(3),
            CreatedAt = reader.GetInt64(4),
        };
    }

    public IReadOnlyList<GroupTopic> ListTopics(string groupId)
    {
        using var conn = _pg.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM agui_topics WHERE group_id = @gid ORDER BY created_at";
        cmd.Parameters.AddWithValue("gid", groupId);
        var list = new List<GroupTopic>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new GroupTopic
            {
                TopicId = reader.GetString(0),
                GroupId = reader.GetString(1),
                Name = reader.GetString(2),
                CreatorId = reader.GetString(3),
                CreatedAt = reader.GetInt64(4),
            });
        }
        return list;
    }

    // ================= 消息 =================

    public void AddMessage(GroupMessage message)
    {
        using var conn = _pg.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO agui_messages
                (message_id, group_id, topic_id, thread_id, sender_id, sender_type, sender_nickname,
                 reply_to_message_id, mentions, mention_all, visibility, visible_member_ids, attachments,
                 content, reasoning, timestamp, recalled)
            VALUES (@mid, @gid, @topic, @thread, @sender, @senderType, @nick,
                    @reply, @mentions, @mentionAll, @visibility, @visible, @attachments,
                    @content, @reasoning, @time, @recalled)
            ON CONFLICT (message_id) DO NOTHING
            """;
        cmd.Parameters.AddWithValue("mid", message.MessageId);
        cmd.Parameters.AddWithValue("gid", message.GroupId);
        cmd.Parameters.AddWithValue("topic", message.TopicId);
        cmd.Parameters.AddWithValue("thread", message.ThreadId);
        cmd.Parameters.AddWithValue("sender", message.SenderId);
        cmd.Parameters.AddWithValue("senderType", message.SenderType.ToString());
        cmd.Parameters.AddWithValue("nick", message.SenderNickname);
        cmd.Parameters.AddWithValue("reply", (object?)message.ReplyToMessageId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("mentions", Json(message.Mentions));
        cmd.Parameters.AddWithValue("mentionAll", message.MentionAll);
        cmd.Parameters.AddWithValue("visibility", message.Visibility.ToString());
        cmd.Parameters.AddWithValue("visible", Json(message.VisibleMemberIds));
        cmd.Parameters.AddWithValue("attachments", Json(message.Attachments));
        cmd.Parameters.AddWithValue("content", message.Content);
        cmd.Parameters.AddWithValue("reasoning", (object?)message.Reasoning ?? DBNull.Value);
        cmd.Parameters.AddWithValue("time", message.Timestamp);
        cmd.Parameters.AddWithValue("recalled", message.Recalled);
        cmd.ExecuteNonQuery();
    }

    public GroupMessage? GetMessage(string groupId, string messageId)
    {
        using var conn = _pg.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM agui_messages WHERE group_id = @gid AND message_id = @mid";
        cmd.Parameters.AddWithValue("gid", groupId);
        cmd.Parameters.AddWithValue("mid", messageId);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadMessage(reader) : null;
    }

    public bool RecallMessage(string groupId, string messageId)
    {
        using var conn = _pg.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE agui_messages SET recalled = TRUE WHERE group_id = @gid AND message_id = @mid AND recalled = FALSE";
        cmd.Parameters.AddWithValue("gid", groupId);
        cmd.Parameters.AddWithValue("mid", messageId);
        return cmd.ExecuteNonQuery() > 0;
    }

    public IReadOnlyList<GroupMessage> RecentMessages(string groupId, int count, string? topicId = null)
    {
        var rows = QueryMessages(groupId, topicId, limit: count, before: null, beforeTimestamp: null);
        return rows;
    }

    public IReadOnlyList<GroupMessage> MessagesAfter(string groupId, string afterMessageId, int count, string? topicId = null)
    {
        if (count <= 0) return []; // 与内存版一致：count <= 0 返回空列表
        using var conn = _pg.Open();
        // 游标时间戳（不区分话题，与 MessagesBefore 定位语义一致）
        long? ts = null;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT timestamp FROM agui_messages WHERE group_id = @gid AND message_id = @mid";
            cmd.Parameters.AddWithValue("gid", groupId);
            cmd.Parameters.AddWithValue("mid", afterMessageId);
            var val = cmd.ExecuteScalar();
            ts = val is null or DBNull ? null : Convert.ToInt64(val);
        }
        if (ts is null) return []; // 游标不存在 → 无增量
        using var cmd2 = conn.CreateCommand();
        var where = "group_id = @gid AND (timestamp > @ts OR (timestamp = @ts AND message_id > @after))";
        cmd2.Parameters.AddWithValue("gid", groupId);
        cmd2.Parameters.AddWithValue("ts", ts.Value);
        cmd2.Parameters.AddWithValue("after", afterMessageId);
        if (topicId is not null)
        {
            where += " AND topic_id = @topic";
            cmd2.Parameters.AddWithValue("topic", topicId);
        }
        cmd2.CommandText = $"""
            SELECT * FROM agui_messages
            WHERE {where}
            ORDER BY timestamp ASC, message_id ASC
            LIMIT {Math.Min(count, 5000)}
            """;
        var list = new List<GroupMessage>();
        using var reader = cmd2.ExecuteReader();
        while (reader.Read()) list.Add(ReadMessage(reader));
        return list;
    }

    public IReadOnlyList<GroupMessage> MessagesBefore(string groupId, string? beforeMessageId, int count, string? topicId = null)
    {
        if (count <= 0) return [];
        if (string.IsNullOrEmpty(beforeMessageId))
            return QueryMessages(groupId, topicId, count, null, null);

        // 定位游标消息的时间戳
        long? ts = null;
        using (var conn = _pg.Open())
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT timestamp FROM agui_messages WHERE group_id = @gid AND message_id = @mid";
            cmd.Parameters.AddWithValue("gid", groupId);
            cmd.Parameters.AddWithValue("mid", beforeMessageId);
            var val = cmd.ExecuteScalar();
            ts = val is null or DBNull ? null : Convert.ToInt64(val);
        }
        if (ts is null) return []; // 游标不存在 → 没有更早消息（与内存实现语义一致）
        return QueryMessages(groupId, topicId, count, beforeMessageId, ts);
    }

    /// <summary>全量枚举：无 LIMIT（供删除话题 / 分身语料收集等需要完整历史读取的场景，不受 5000 条分页钳制影响）。</summary>
    public IReadOnlyList<GroupMessage> AllMessages(string groupId)
    {
        using var conn = _pg.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT * FROM agui_messages
            WHERE group_id = @gid
            ORDER BY timestamp, message_id
            """;
        cmd.Parameters.AddWithValue("gid", groupId);
        var list = new List<GroupMessage>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) list.Add(ReadMessage(reader));
        return list;
    }

    public IReadOnlyList<GroupMessage> SearchMessages(string groupId, string keyword, string? topicId, int limit)
    {
        if (string.IsNullOrWhiteSpace(keyword) || limit <= 0) return [];
        using var conn = _pg.Open();
        using var cmd = conn.CreateCommand();
        // 大小写不敏感子串匹配（ILIKE 可用 pg_trgm GIN 索引）；LIKE 通配符 %/_ 转义（ESCAPE '\'），防用户输入 % 匹配全表
        var escaped = keyword.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
        var where = "group_id = @gid AND content ILIKE @kw ESCAPE '\\'";
        cmd.Parameters.AddWithValue("gid", groupId);
        cmd.Parameters.AddWithValue("kw", "%" + escaped + "%");
        if (topicId is not null)
        {
            where += " AND topic_id = @topic";
            cmd.Parameters.AddWithValue("topic", topicId);
        }
        cmd.CommandText = $"""
            SELECT * FROM agui_messages
            WHERE {where}
            ORDER BY timestamp DESC, message_id DESC
            LIMIT {Math.Min(limit, 100)}
            """;
        var list = new List<GroupMessage>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) list.Add(ReadMessage(reader));
        return list;
    }

    public int DeleteMessagesBefore(long beforeTimestamp, string? groupId = null)
    {
        using var conn = _pg.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = groupId is null
            ? "DELETE FROM agui_messages WHERE timestamp < @ts"
            : "DELETE FROM agui_messages WHERE group_id = @gid AND timestamp < @ts";
        cmd.Parameters.AddWithValue("ts", beforeTimestamp);
        if (groupId is not null) cmd.Parameters.AddWithValue("gid", groupId);
        return cmd.ExecuteNonQuery();
    }

    // ================= 已读位点与未读（群列表活跃度 / 未读提示） =================

    public long GetReadAt(string memberId, string groupId, string topicId)
    {
        using var conn = _pg.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT read_at FROM agui_group_reads WHERE member_id = @mid AND group_id = @gid AND topic_id = @tid";
        cmd.Parameters.AddWithValue("mid", memberId);
        cmd.Parameters.AddWithValue("gid", groupId);
        cmd.Parameters.AddWithValue("tid", topicId);
        var val = cmd.ExecuteScalar();
        return val is null or DBNull ? 0 : Convert.ToInt64(val);
    }

    public void SetReadAt(string memberId, string groupId, string topicId, long timestamp)
    {
        using var conn = _pg.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO agui_group_reads (member_id, group_id, topic_id, read_at)
            VALUES (@mid, @gid, @tid, @at)
            ON CONFLICT (member_id, group_id, topic_id) DO UPDATE
                SET read_at = GREATEST(agui_group_reads.read_at, EXCLUDED.read_at)  -- 只前进不回退：并发旧位点写回不覆盖
            """;
        cmd.Parameters.AddWithValue("mid", memberId);
        cmd.Parameters.AddWithValue("gid", groupId);
        cmd.Parameters.AddWithValue("tid", topicId);
        cmd.Parameters.AddWithValue("at", timestamp);
        cmd.ExecuteNonQuery();
    }

    public long? LastMessageAt(string groupId)
    {
        using var conn = _pg.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT MAX(timestamp) FROM agui_messages WHERE group_id = @gid";
        cmd.Parameters.AddWithValue("gid", groupId);
        var val = cmd.ExecuteScalar();
        return val is null or DBNull ? null : Convert.ToInt64(val);
    }

    public int CountUnread(string groupId, string? topicId, long afterTimestamp)
    {
        using var conn = _pg.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*) FROM agui_messages
            WHERE group_id = @gid AND recalled = FALSE AND timestamp > @at
              AND (@topic IS NULL OR topic_id = @topic OR (@topic = 'main' AND topic_id IS NULL))
            """;
        cmd.Parameters.AddWithValue("gid", groupId);
        cmd.Parameters.AddWithValue("at", afterTimestamp);
        cmd.Parameters.AddWithValue("topic", (object?)topicId ?? DBNull.Value);
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }

    // ================= 原地修改落库（GroupHub 在内存对象上原地变更后调用） =================

    public void UpdateGroup(Group group)
    {
        using var conn = _pg.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE agui_groups SET group_name = @name, group_avatar = @avatar, is_private = @isPrivate WHERE group_id = @gid";
        cmd.Parameters.AddWithValue("name", group.GroupName);
        cmd.Parameters.AddWithValue("avatar", (object?)group.GroupAvatar ?? DBNull.Value);
        cmd.Parameters.AddWithValue("isPrivate", group.IsPrivate);
        cmd.Parameters.AddWithValue("gid", group.GroupId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>成员资料落库：更新角色 / 昵称 / 头像 / 扩展字段（在线状态为连接态瞬时量，由 UpdateMemberStatus 维护）。
    /// extra 一并落库以持久化频道级 RBAC 权限（Extra["rbac"]）等扩展字段。</summary>
    public void UpdateMember(string groupId, GroupMember member)
    {
        using var conn = _pg.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE agui_group_members
            SET nickname = @nick, avatar = @avatar, role = @role, extra = @extra
            WHERE group_id = @gid AND member_id = @mid
            """;
        cmd.Parameters.AddWithValue("nick", member.Nickname);
        cmd.Parameters.AddWithValue("avatar", (object?)member.Avatar ?? DBNull.Value);
        cmd.Parameters.AddWithValue("role", member.Role.ToString());
        cmd.Parameters.AddWithValue("extra", Json(member.Extra));
        cmd.Parameters.AddWithValue("gid", groupId);
        cmd.Parameters.AddWithValue("mid", member.MemberId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>消息落库：更新可变字段（流式内容、话题迁移、桥接附件追加）。撤回走 RecallMessage。</summary>
    public void UpdateMessage(GroupMessage message)
    {
        using var conn = _pg.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE agui_messages SET topic_id = @topic, content = @content, attachments = @attachments, reasoning = @reasoning WHERE group_id = @gid AND message_id = @mid";
        cmd.Parameters.AddWithValue("topic", message.TopicId);
        cmd.Parameters.AddWithValue("content", message.Content);
        cmd.Parameters.AddWithValue("attachments", Json(message.Attachments));
        cmd.Parameters.AddWithValue("reasoning", (object?)message.Reasoning ?? DBNull.Value);
        cmd.Parameters.AddWithValue("gid", message.GroupId);
        cmd.Parameters.AddWithValue("mid", message.MessageId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>启动时把所有成员的在线状态复位为 Offline（在线状态为连接态，重启后一律离线，与 JSON 快照恢复语义一致）。</summary>
    public void ResetAllOnlineStatuses()
    {
        using var conn = _pg.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE agui_group_members SET online_status = 'Offline'";
        cmd.ExecuteNonQuery();
    }

    /// <summary>分页查询：按 (timestamp, message_id) 字典序取 before 之前（不含）的最新 count 条，返回时间序（旧→新）。</summary>
    private IReadOnlyList<GroupMessage> QueryMessages(
        string groupId, string? topicId, int limit, string? before, long? beforeTimestamp)
    {
        if (limit <= 0) return []; // 与内存版一致：count <= 0 返回空列表
        using var conn = _pg.Open();
        using var cmd = conn.CreateCommand();
        var where = "group_id = @gid";
        cmd.Parameters.AddWithValue("gid", groupId);
        if (topicId is not null)
        {
            where += " AND topic_id = @topic";
            cmd.Parameters.AddWithValue("topic", topicId);
        }
        if (before is not null && beforeTimestamp is not null)
        {
            where += " AND (timestamp < @ts OR (timestamp = @ts AND message_id < @before))";
            cmd.Parameters.AddWithValue("ts", beforeTimestamp.Value);
            cmd.Parameters.AddWithValue("before", before);
        }
        cmd.CommandText = $"""
            SELECT * FROM agui_messages
            WHERE {where}
            ORDER BY timestamp DESC, message_id DESC
            LIMIT {Math.Min(limit, 5000)}
            """;
        var list = new List<GroupMessage>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) list.Add(ReadMessage(reader));
        list.Reverse(); // 时间序（旧→新）
        return list;
    }

    // ================= 读取与序列化 =================

    private static Group ReadGroup(NpgsqlDataReader r) => new()
    {
        GroupId = r.GetString(0),
        GroupName = r.GetString(1),
        GroupAvatar = r.IsDBNull(2) ? null : r.GetString(2),
        OwnerId = r.GetString(3),
        MemberCount = r.GetInt32(4),
        CreateTime = r.GetInt64(5),
        Extra = FromJson<Dictionary<string, object?>>(r.IsDBNull(6) ? null : r.GetString(6)),
        IsPrivate = r.IsDBNull(7) ? false : r.GetBoolean(7),
    };

    private static GroupMember ReadMember(NpgsqlDataReader r) => new()
    {
        MemberId = r.GetString(1),
        MemberType = Enum.Parse<MemberType>(r.GetString(2)),
        Nickname = r.GetString(3),
        Avatar = r.IsDBNull(4) ? null : r.GetString(4),
        Role = Enum.Parse<GroupRole>(r.GetString(5)),
        OnlineStatus = Enum.Parse<OnlineStatus>(r.GetString(6)),
        JoinTime = r.GetInt64(7),
        TriggerMode = r.IsDBNull(8) ? null : r.GetString(8),
        Keywords = FromJson<List<string>>(r.IsDBNull(9) ? null : r.GetString(9)),
        IsTriggerOverridden = r.GetBoolean(10),
        Extra = FromJson<Dictionary<string, object?>>(r.IsDBNull(11) ? null : r.GetString(11)),
    };

    private static GroupMessage ReadMessage(NpgsqlDataReader r) => new()
    {
        // 按列名读取（非位置索引）：旧库迁移时 ALTER TABLE 追加的列在表末尾，位置索引会错位
        MessageId = r.GetString(r.GetOrdinal("message_id")),
        GroupId = r.GetString(r.GetOrdinal("group_id")),
        TopicId = r.GetString(r.GetOrdinal("topic_id")),
        ThreadId = r.GetString(r.GetOrdinal("thread_id")),
        SenderId = r.GetString(r.GetOrdinal("sender_id")),
        SenderType = Enum.Parse<MemberType>(r.GetString(r.GetOrdinal("sender_type"))),
        SenderNickname = r.GetString(r.GetOrdinal("sender_nickname")),
        ReplyToMessageId = r.IsDBNull(r.GetOrdinal("reply_to_message_id")) ? null : r.GetString(r.GetOrdinal("reply_to_message_id")),
        Mentions = FromJson<List<string>>(r.IsDBNull(r.GetOrdinal("mentions")) ? null : r.GetString(r.GetOrdinal("mentions"))) ?? [],
        MentionAll = r.GetBoolean(r.GetOrdinal("mention_all")),
        Visibility = Enum.Parse<MessageVisibility>(r.GetString(r.GetOrdinal("visibility"))),
        VisibleMemberIds = FromJson<List<string>>(r.IsDBNull(r.GetOrdinal("visible_member_ids")) ? null : r.GetString(r.GetOrdinal("visible_member_ids"))) ?? [],
        Attachments = FromJson<List<AttachmentInfo>>(r.IsDBNull(r.GetOrdinal("attachments")) ? null : r.GetString(r.GetOrdinal("attachments"))) ?? [],
        Content = r.GetString(r.GetOrdinal("content")),
        Reasoning = r.IsDBNull(r.GetOrdinal("reasoning")) ? null : r.GetString(r.GetOrdinal("reasoning")),
        Timestamp = r.GetInt64(r.GetOrdinal("timestamp")),
        Recalled = r.GetBoolean(r.GetOrdinal("recalled")),
    };

    private static void AddGroupParams(NpgsqlCommand cmd, Group group)
    {
        cmd.Parameters.AddWithValue("gid", group.GroupId);
        cmd.Parameters.AddWithValue("name", group.GroupName);
        cmd.Parameters.AddWithValue("avatar", (object?)group.GroupAvatar ?? DBNull.Value);
        cmd.Parameters.AddWithValue("owner", group.OwnerId);
        cmd.Parameters.AddWithValue("count", group.MemberCount);
        cmd.Parameters.AddWithValue("time", group.CreateTime);
        cmd.Parameters.AddWithValue("extra", Json(group.Extra));
        cmd.Parameters.AddWithValue("isPrivate", group.IsPrivate);
    }

    private static void AddMemberParams(NpgsqlCommand cmd, string groupId, GroupMember m)
    {
        cmd.Parameters.AddWithValue("gid", groupId);
        cmd.Parameters.AddWithValue("mid", m.MemberId);
        cmd.Parameters.AddWithValue("type", m.MemberType.ToString());
        cmd.Parameters.AddWithValue("nick", m.Nickname);
        cmd.Parameters.AddWithValue("avatar", (object?)m.Avatar ?? DBNull.Value);
        cmd.Parameters.AddWithValue("role", m.Role.ToString());
        cmd.Parameters.AddWithValue("status", m.OnlineStatus.ToString());
        cmd.Parameters.AddWithValue("join", m.JoinTime);
        cmd.Parameters.AddWithValue("tMode", (object?)m.TriggerMode ?? DBNull.Value);
        cmd.Parameters.AddWithValue("keywords", Json(m.Keywords));
        cmd.Parameters.AddWithValue("overridden", m.IsTriggerOverridden);
        cmd.Parameters.AddWithValue("extra", Json(m.Extra));
    }

    private static string Json<T>(T? value) => value is null ? "null" : JsonSerializer.Serialize(value, AguiJson.Options);

    private static T? FromJson<T>(string? json)
    {
        if (string.IsNullOrEmpty(json) || json == "null") return default;
        try { return JsonSerializer.Deserialize<T>(json, AguiJson.Options); }
        catch { return default; }
    }

    public void ClearAll()
    {
        using var conn = _pg.Open();
        using var tx = conn.BeginTransaction();
        foreach (var table in new[]
        {
            "agui_agent_registrations", "agui_messages", "agui_topics", "agui_group_members", "agui_group_reads", "agui_groups",
        })
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"DELETE FROM {table}";
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }
}
