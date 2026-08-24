using System.Data.Common;
using System.Text.Json;
using AguiGroupChat.Hub.Infra;
using AguiGroupChat.Hub.Models;
using AguiGroupChat.Hub.Storage;

namespace AguiGroupChat.Hub.Persistence.Relational;

/// <summary>
/// MySQL / SQLite 共用群组存储：群组 / 成员 / 话题 / 消息（含分页游标、撤回）。
/// 基于 DbConnection / DbDataReader 编程，方言差异（UPSERT、重复键判定）由 <see cref="RelationalStore"/> 隔离；
/// 语义与内存 / PostgreSQL 实现一致：枚举与列表字段以字符串 / JSON 文本列存储。
/// </summary>
public sealed class RelationalGroupStore : IGroupStore
{
    private readonly RelationalStore _db;

    public RelationalGroupStore(RelationalStore db) => _db = db;

    // ================= 群组 =================

    public bool AddGroup(Group group)
        => TryInsert("""
            INSERT INTO agui_groups (group_id, group_name, group_avatar, owner_id, member_count, create_time, extra, is_private)
            VALUES (@gid, @name, @avatar, @owner, @count, @time, @extra, @isPrivate)
            """, cmd => AddGroupParams(cmd, group));

    /// <summary>事务性建群：先插群、再逐成员插入，全部成功才提交（失败自动回滚，防半建状态）。</summary>
    public bool CreateGroupWithMembers(Group group, IReadOnlyList<GroupMember> members)
    {
        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO agui_groups (group_id, group_name, group_avatar, owner_id, member_count, create_time, extra, is_private)
                VALUES (@gid, @name, @avatar, @owner, @count, @time, @extra, @isPrivate)
                """;
            AddGroupParams(cmd, group);
            try
            {
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex) when (_db.IsDuplicate(ex))
            {
                return false; // ID 冲突：群已存在（事务随 using 释放自动回滚）
            }
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
                """;
            AddMemberParams(cmd, group.GroupId, m);
            try
            {
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex) when (_db.IsDuplicate(ex))
            {
                // 成员重复视为已存在（与 AddMember 语义一致）；其余异常向上抛 → 事务回滚
            }
        }
        tx.Commit();
        return true;
    }

    public Group? GetGroup(string groupId)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM agui_groups WHERE group_id = @gid";
        cmd.AddWithValue("gid", groupId);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadGroup(reader) : null;
    }

    public bool RemoveGroup(string groupId)
    {
        using var conn = _db.Open();
        // 事务包裹：全部删除成功才 Commit；中途异常时 using 释放事务自动回滚，避免半删状态
        using var tx = conn.BeginTransaction();
        foreach (var table in new[]
        {
            "agui_group_members", "agui_topics", "agui_messages", "agui_agent_registrations", "agui_group_reads", "agui_groups",
        })
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"DELETE FROM {table} WHERE group_id = @gid";
            cmd.AddWithValue("gid", groupId);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return true;
    }

    public IReadOnlyList<Group> AllGroups()
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM agui_groups";
        var list = new List<Group>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) list.Add(ReadGroup(reader));
        return list;
    }

    // ================= 成员 =================

    public bool AddMember(string groupId, GroupMember member)
        => TryInsert("""
            INSERT INTO agui_group_members
                (group_id, member_id, member_type, nickname, avatar, role, online_status, join_time,
                 trigger_mode, keywords, is_trigger_overridden, extra)
            VALUES (@gid, @mid, @type, @nick, @avatar, @role, @status, @join, @tMode, @keywords, @overridden, @extra)
            """, cmd => AddMemberParams(cmd, groupId, member));

    public bool IsMember(string groupId, string memberId)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM agui_group_members WHERE group_id = @gid AND member_id = @mid)";
        cmd.AddWithValue("gid", groupId);
        cmd.AddWithValue("mid", memberId);
        return Convert.ToInt64(cmd.ExecuteScalar()) != 0;
    }

    public GroupMember? GetMember(string groupId, string memberId)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM agui_group_members WHERE group_id = @gid AND member_id = @mid";
        cmd.AddWithValue("gid", groupId);
        cmd.AddWithValue("mid", memberId);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadMember(reader) : null;
    }

    public bool RemoveMember(string groupId, string memberId)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM agui_group_members WHERE group_id = @gid AND member_id = @mid";
        cmd.AddWithValue("gid", groupId);
        cmd.AddWithValue("mid", memberId);
        return cmd.ExecuteNonQuery() > 0;
    }

    public bool UpdateMemberStatus(string groupId, string memberId, OnlineStatus status)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE agui_group_members SET online_status = @status WHERE group_id = @gid AND member_id = @mid";
        cmd.AddWithValue("status", status.ToString());
        cmd.AddWithValue("gid", groupId);
        cmd.AddWithValue("mid", memberId);
        return cmd.ExecuteNonQuery() > 0;
    }

    public IReadOnlyList<GroupMember> ListMembers(string groupId)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM agui_group_members WHERE group_id = @gid ORDER BY join_time";
        cmd.AddWithValue("gid", groupId);
        var list = new List<GroupMember>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) list.Add(ReadMember(reader));
        return list;
    }

    public int MemberCount(string groupId)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM agui_group_members WHERE group_id = @gid";
        cmd.AddWithValue("gid", groupId);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public IReadOnlyList<Group> GroupsOf(string memberId)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT g.* FROM agui_groups g
            JOIN agui_group_members m ON m.group_id = g.group_id
            WHERE m.member_id = @mid ORDER BY g.create_time
            """;
        cmd.AddWithValue("mid", memberId);
        var list = new List<Group>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) list.Add(ReadGroup(reader));
        return list;
    }

    // ================= 话题 =================

    public bool AddTopic(GroupTopic topic)
        => TryInsert("""
            INSERT INTO agui_topics (topic_id, group_id, name, creator_id, created_at)
            VALUES (@tid, @gid, @name, @creator, @created)
            """, cmd =>
        {
            cmd.AddWithValue("tid", topic.TopicId);
            cmd.AddWithValue("gid", topic.GroupId);
            cmd.AddWithValue("name", topic.Name);
            cmd.AddWithValue("creator", topic.CreatorId);
            cmd.AddWithValue("created", topic.CreatedAt);
        });

    public bool RemoveTopic(string groupId, string topicId)
    {
        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();
        // 删除话题 + 清理该话题已读位点：同一事务，全部成功才提交（防话题删了 reads 残留 / 反向的半删状态）
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM agui_topics WHERE group_id = @gid AND topic_id = @tid";
            cmd.AddWithValue("gid", groupId);
            cmd.AddWithValue("tid", topicId);
            if (cmd.ExecuteNonQuery() == 0) return false; // 话题不存在：无事可做，事务随 using 释放回滚
        }
        using (var del = conn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM agui_group_reads WHERE group_id = @gid AND topic_id = @tid";
            del.AddWithValue("gid", groupId);
            del.AddWithValue("tid", topicId);
            del.ExecuteNonQuery();
        }
        tx.Commit();
        return true;
    }

    public int RemoveTopicMessages(string groupId, string topicId)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        // main 话题兼容历史 NULL topic_id（旧消息无话题归属）
        cmd.CommandText = "DELETE FROM agui_messages WHERE group_id = @gid AND (topic_id = @tid OR (@tid = 'main' AND topic_id IS NULL))";
        cmd.AddWithValue("gid", groupId);
        cmd.AddWithValue("tid", topicId);
        return cmd.ExecuteNonQuery();
    }

    public GroupTopic? GetTopic(string groupId, string topicId)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM agui_topics WHERE group_id = @gid AND topic_id = @tid";
        cmd.AddWithValue("gid", groupId);
        cmd.AddWithValue("tid", topicId);
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
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM agui_topics WHERE group_id = @gid ORDER BY created_at";
        cmd.AddWithValue("gid", groupId);
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
        try
        {
            using var conn = _db.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO agui_messages
                    (message_id, group_id, topic_id, thread_id, sender_id, sender_type, sender_nickname,
                     reply_to_message_id, mentions, mention_all, visibility, visible_member_ids, attachments,
                     content, reasoning, agent_chain, timestamp, recalled)
                VALUES (@mid, @gid, @topic, @thread, @sender, @senderType, @nick,
                        @reply, @mentions, @mentionAll, @visibility, @visible, @attachments,
                        @content, @reasoning, @agentChain, @time, @recalled)
                """;
            AddMessageParams(cmd, message);
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex) when (_db.IsDuplicate(ex)) { /* message_id 冲突：静默跳过（同内存 / PostgreSQL 语义） */ }
    }

    public GroupMessage? GetMessage(string groupId, string messageId)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM agui_messages WHERE group_id = @gid AND message_id = @mid";
        cmd.AddWithValue("gid", groupId);
        cmd.AddWithValue("mid", messageId);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadMessage(reader) : null;
    }

    public bool RecallMessage(string groupId, string messageId)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE agui_messages SET recalled = 1 WHERE group_id = @gid AND message_id = @mid AND recalled = 0";
        cmd.AddWithValue("gid", groupId);
        cmd.AddWithValue("mid", messageId);
        return cmd.ExecuteNonQuery() > 0;
    }

    public IReadOnlyList<GroupMessage> RecentMessages(string groupId, int count, string? topicId = null)
        => QueryMessages(groupId, topicId, limit: count, before: null, beforeTimestamp: null);

    public IReadOnlyList<GroupMessage> MessagesAfter(string groupId, string afterMessageId, int count, string? topicId = null)
    {
        if (count <= 0) return []; // 与内存版一致：count <= 0 返回空列表
        using var conn = _db.Open();
        // 游标时间戳（不区分话题，与 MessagesBefore 定位语义一致）
        long? ts = null;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT timestamp FROM agui_messages WHERE group_id = @gid AND message_id = @mid";
            cmd.AddWithValue("gid", groupId);
            cmd.AddWithValue("mid", afterMessageId);
            var val = cmd.ExecuteScalar();
            ts = val is null or DBNull ? null : Convert.ToInt64(val);
        }
        if (ts is null) return []; // 游标不存在 → 无增量
        using var cmd2 = conn.CreateCommand();
        var where = "group_id = @gid AND (timestamp > @ts OR (timestamp = @ts AND message_id > @after))";
        cmd2.AddWithValue("gid", groupId);
        cmd2.AddWithValue("ts", ts.Value);
        cmd2.AddWithValue("after", afterMessageId);
        if (topicId is not null)
        {
            where += " AND topic_id = @topic";
            cmd2.AddWithValue("topic", topicId);
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
        using (var conn = _db.Open())
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT timestamp FROM agui_messages WHERE group_id = @gid AND message_id = @mid";
            cmd.AddWithValue("gid", groupId);
            cmd.AddWithValue("mid", beforeMessageId);
            var val = cmd.ExecuteScalar();
            ts = val is null or DBNull ? null : Convert.ToInt64(val);
        }
        if (ts is null) return []; // 游标不存在 → 没有更早消息（与内存实现语义一致）
        return QueryMessages(groupId, topicId, count, beforeMessageId, ts);
    }

    /// <summary>全量枚举：无 LIMIT（供删除话题 / 分身语料收集等需要完整历史读取的场景，不受 5000 条分页钳制影响）。</summary>
    public IReadOnlyList<GroupMessage> AllMessages(string groupId)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT * FROM agui_messages
            WHERE group_id = @gid
            ORDER BY timestamp, message_id
            """;
        cmd.AddWithValue("gid", groupId);
        var list = new List<GroupMessage>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) list.Add(ReadMessage(reader));
        return list;
    }

    public IReadOnlyList<GroupMessage> SearchMessages(string groupId, string keyword, string? topicId, int limit)
    {
        if (string.IsNullOrWhiteSpace(keyword) || limit <= 0) return [];
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        // 统一大小写不敏感：LOWER 两端比较（SQLite/MySQL 默认 collation 已不敏感，Postgres 敏感——LOWER 保证一致）；
        // LIKE 通配符 %/_ 转义（ESCAPE '\'），防止用户输入 % 时匹配到全表
        var escaped = keyword.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
        var where = "group_id = @gid AND LOWER(content) LIKE LOWER(@kw) ESCAPE '\\'";
        cmd.AddWithValue("gid", groupId);
        cmd.AddWithValue("kw", "%" + escaped + "%");
        if (topicId is not null)
        {
            where += " AND topic_id = @topic";
            cmd.AddWithValue("topic", topicId);
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
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = groupId is null
            ? "DELETE FROM agui_messages WHERE timestamp < @ts"
            : "DELETE FROM agui_messages WHERE group_id = @gid AND timestamp < @ts";
        cmd.AddWithValue("ts", beforeTimestamp);
        if (groupId is not null) cmd.AddWithValue("gid", groupId);
        return cmd.ExecuteNonQuery();
    }

    // ================= 已读位点与未读（群列表活跃度 / 未读提示） =================

    public long GetReadAt(string memberId, string groupId, string topicId)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT read_at FROM agui_group_reads WHERE member_id = @mid AND group_id = @gid AND topic_id = @tid";
        cmd.AddWithValue("mid", memberId);
        cmd.AddWithValue("gid", groupId);
        cmd.AddWithValue("tid", topicId);
        var val = cmd.ExecuteScalar();
        return val is null or DBNull ? 0 : Convert.ToInt64(val);
    }

    public void SetReadAt(string memberId, string groupId, string topicId, long timestamp)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        // 只前进不回退：冲突时仅当新值更大才覆盖（MySQL GREATEST + 行别名 / SQLite MAX + excluded，见 SqlDialect.MonotonicUpsert）
        cmd.CommandText = _db.Dialect.MonotonicUpsert(
            "agui_group_reads",
            "member_id, group_id, topic_id, read_at",
            "@mid, @gid, @tid, @at",
            "member_id, group_id, topic_id",
            "read_at");
        cmd.AddWithValue("mid", memberId);
        cmd.AddWithValue("gid", groupId);
        cmd.AddWithValue("tid", topicId);
        cmd.AddWithValue("at", timestamp);
        cmd.ExecuteNonQuery();
    }

    public long? LastMessageAt(string groupId)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT MAX(timestamp) FROM agui_messages WHERE group_id = @gid";
        cmd.AddWithValue("gid", groupId);
        var val = cmd.ExecuteScalar();
        return val is null or DBNull ? null : Convert.ToInt64(val);
    }

    public int CountUnread(string groupId, string? topicId, long afterTimestamp)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*) FROM agui_messages
            WHERE group_id = @gid AND recalled = 0 AND timestamp > @at
              AND (@topic IS NULL OR topic_id = @topic OR (@topic = 'main' AND topic_id IS NULL))
            """;
        cmd.AddWithValue("gid", groupId);
        cmd.AddWithValue("at", afterTimestamp);
        cmd.AddWithValue("topic", (object?)topicId ?? DBNull.Value);
        var val = cmd.ExecuteScalar();
        return Convert.ToInt32(val ?? 0);
    }

    // ================= 原地修改落库（GroupHub 在内存对象上原地变更后调用） =================

    public void UpdateGroup(Group group)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE agui_groups SET group_name = @name, group_avatar = @avatar, is_private = @isPrivate WHERE group_id = @gid";
        cmd.AddWithValue("name", group.GroupName);
        cmd.AddWithValue("avatar", (object?)group.GroupAvatar ?? DBNull.Value);
        cmd.AddWithValue("isPrivate", group.IsPrivate);
        cmd.AddWithValue("gid", group.GroupId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>成员资料落库：更新角色 / 昵称 / 头像 / 扩展字段（在线状态为连接态瞬时量，由 UpdateMemberStatus 维护）。
    /// extra 一并落库以持久化频道级 RBAC 权限（Extra["rbac"]）等扩展字段。</summary>
    public void UpdateMember(string groupId, GroupMember member)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE agui_group_members
            SET nickname = @nick, avatar = @avatar, role = @role, extra = @extra
            WHERE group_id = @gid AND member_id = @mid
            """;
        cmd.AddWithValue("nick", member.Nickname);
        cmd.AddWithValue("avatar", (object?)member.Avatar ?? DBNull.Value);
        cmd.AddWithValue("role", member.Role.ToString());
        cmd.AddWithValue("extra", Json(member.Extra));
        cmd.AddWithValue("gid", groupId);
        cmd.AddWithValue("mid", member.MemberId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>消息落库：更新可变字段（流式内容、话题迁移、桥接附件追加）。撤回走 RecallMessage。</summary>
    public void UpdateMessage(GroupMessage message)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE agui_messages SET topic_id = @topic, content = @content, attachments = @attachments, reasoning = @reasoning, agent_chain = @agentChain WHERE group_id = @gid AND message_id = @mid";
        cmd.AddWithValue("topic", message.TopicId);
        cmd.AddWithValue("content", message.Content);
        cmd.AddWithValue("attachments", Json(message.Attachments));
        cmd.AddWithValue("reasoning", (object?)message.Reasoning ?? DBNull.Value);
        cmd.AddWithValue("agentChain", (object?)message.AgentChain ?? DBNull.Value);
        cmd.AddWithValue("gid", message.GroupId);
        cmd.AddWithValue("mid", message.MessageId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>启动时把所有成员的在线状态复位为 Offline（在线状态为连接态，重启后一律离线，与 JSON 快照恢复语义一致）。</summary>
    public void ResetAllOnlineStatuses()
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE agui_group_members SET online_status = 'Offline'";
        cmd.ExecuteNonQuery();
    }

    // ================= 内部 =================

    private bool TryInsert(string sql, Action<DbCommand> bind)
    {
        try
        {
            using var conn = _db.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            bind(cmd);
            cmd.ExecuteNonQuery();
            return true;
        }
        catch (Exception ex) when (_db.IsDuplicate(ex))
        {
            return false; // 主键 / 唯一键冲突（与 PostgreSQL ON CONFLICT DO NOTHING 语义一致）
        }
    }

    /// <summary>分页查询：按 (timestamp, message_id) 字典序取 before 之前（不含）的最新 count 条，返回时间序（旧→新）。</summary>
    private IReadOnlyList<GroupMessage> QueryMessages(
        string groupId, string? topicId, int limit, string? before, long? beforeTimestamp)
    {
        if (limit <= 0) return []; // 与内存版一致：count <= 0 返回空列表
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        var where = "group_id = @gid";
        cmd.AddWithValue("gid", groupId);
        if (topicId is not null)
        {
            where += " AND topic_id = @topic";
            cmd.AddWithValue("topic", topicId);
        }
        if (before is not null && beforeTimestamp is not null)
        {
            where += " AND (timestamp < @ts OR (timestamp = @ts AND message_id < @before))";
            cmd.AddWithValue("ts", beforeTimestamp.Value);
            cmd.AddWithValue("before", before);
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

    private static Group ReadGroup(DbDataReader r) => new()
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

    private static GroupMember ReadMember(DbDataReader r) => new()
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

    private static GroupMessage ReadMessage(DbDataReader r) => new()
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
        AgentChain = r.IsDBNull(r.GetOrdinal("agent_chain")) ? null : r.GetString(r.GetOrdinal("agent_chain")),
        Timestamp = r.GetInt64(r.GetOrdinal("timestamp")),
        Recalled = r.GetBoolean(r.GetOrdinal("recalled")),
    };

    private static void AddGroupParams(DbCommand cmd, Group group)
    {
        cmd.AddWithValue("gid", group.GroupId);
        cmd.AddWithValue("name", group.GroupName);
        cmd.AddWithValue("avatar", (object?)group.GroupAvatar ?? DBNull.Value);
        cmd.AddWithValue("owner", group.OwnerId);
        cmd.AddWithValue("count", group.MemberCount);
        cmd.AddWithValue("time", group.CreateTime);
        cmd.AddWithValue("extra", Json(group.Extra));
        cmd.AddWithValue("isPrivate", group.IsPrivate);
    }

    private static void AddMemberParams(DbCommand cmd, string groupId, GroupMember m)
    {
        cmd.AddWithValue("gid", groupId);
        cmd.AddWithValue("mid", m.MemberId);
        cmd.AddWithValue("type", m.MemberType.ToString());
        cmd.AddWithValue("nick", m.Nickname);
        cmd.AddWithValue("avatar", (object?)m.Avatar ?? DBNull.Value);
        cmd.AddWithValue("role", m.Role.ToString());
        cmd.AddWithValue("status", m.OnlineStatus.ToString());
        cmd.AddWithValue("join", m.JoinTime);
        cmd.AddWithValue("tMode", (object?)m.TriggerMode ?? DBNull.Value);
        cmd.AddWithValue("keywords", Json(m.Keywords));
        cmd.AddWithValue("overridden", m.IsTriggerOverridden);
        cmd.AddWithValue("extra", Json(m.Extra));
    }

    private static void AddMessageParams(DbCommand cmd, GroupMessage m)
    {
        cmd.AddWithValue("mid", m.MessageId);
        cmd.AddWithValue("gid", m.GroupId);
        cmd.AddWithValue("topic", m.TopicId);
        cmd.AddWithValue("thread", m.ThreadId);
        cmd.AddWithValue("sender", m.SenderId);
        cmd.AddWithValue("senderType", m.SenderType.ToString());
        cmd.AddWithValue("nick", m.SenderNickname);
        cmd.AddWithValue("reply", (object?)m.ReplyToMessageId ?? DBNull.Value);
        cmd.AddWithValue("mentions", Json(m.Mentions));
        cmd.AddWithValue("mentionAll", m.MentionAll);
        cmd.AddWithValue("visibility", m.Visibility.ToString());
        cmd.AddWithValue("visible", Json(m.VisibleMemberIds));
        cmd.AddWithValue("attachments", Json(m.Attachments));
        cmd.AddWithValue("content", m.Content);
        cmd.AddWithValue("reasoning", (object?)m.Reasoning ?? DBNull.Value);
        cmd.AddWithValue("agentChain", (object?)m.AgentChain ?? DBNull.Value);
        cmd.AddWithValue("time", m.Timestamp);
        cmd.AddWithValue("recalled", m.Recalled);
    }

    private static string Json<T>(T? value) => value is null ? "null" : JsonSerializer.Serialize(value, AguiJson.Options);

    private static T? FromJson<T>(string? json)
    {
        if (string.IsNullOrEmpty(json) || json == "null") return default;
        try { return JsonSerializer.Deserialize<T>(json, AguiJson.Options); }
        catch { return default; }
    }

    public void ClearAll()
        => _db.ExecuteScript("""
            DELETE FROM agui_agent_registrations;
            DELETE FROM agui_group_reads;
            DELETE FROM agui_messages;
            DELETE FROM agui_topics;
            DELETE FROM agui_group_members;
            DELETE FROM agui_groups
            """);
}
