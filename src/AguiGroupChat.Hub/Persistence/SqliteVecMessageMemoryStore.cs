using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using AguiGroupChat.Hub.Models;
using AguiGroupChat.Hub.Persistence.Relational;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace AguiGroupChat.Hub.Persistence;

/// <summary>
/// SQLite + sqlite-vec 语义记忆存储：消息向量写入 vec0 虚拟表（<c>agui_message_memory_vec</c>），
/// 元数据（群 / 发送者 / 内容 / 时间）写入 <c>agui_message_memory</c>，检索用 vec0 的 kNN
/// （余弦距离 <c>distance</c>，score = 1 - distance）。
/// vec0 扩展不可用（未随附 vec0.dll / 平台不兼容）时自动降级：向量存 BLOB 列 + .NET 内存余弦检索，
/// 功能等价（大数据量性能低于向量索引），不影响群聊主流程。
/// </summary>
public sealed class SqliteVecMessageMemoryStore : IMessageMemoryStore
{
    private readonly RelationalStore _db;
    private readonly int _dimensions;
    private readonly ILogger<SqliteVecMessageMemoryStore> _logger;
    private volatile bool _ready;
    private volatile bool _vecEnabled; // true = vec0 虚拟表；false = BLOB 降级

    public SqliteVecMessageMemoryStore(RelationalStore db, int dimensions, ILogger<SqliteVecMessageMemoryStore> logger)
    {
        _db = db;
        _dimensions = Math.Max(8, dimensions);
        _logger = logger;
    }

    public void EnsureSchema()
    {
        try
        {
            // 尝试加载 sqlite-vec 扩展（vec0.dll 需位于应用目录或 SQLITE_EXT_DIR）；失败走 BLOB 降级
            _vecEnabled = TryLoadVec0();
            using var conn = (SqliteConnection)_db.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                CREATE TABLE IF NOT EXISTS agui_message_memory (
                    message_id TEXT PRIMARY KEY,
                    group_id TEXT NOT NULL,
                    topic_id TEXT NOT NULL DEFAULT 'main',
                    sender_id TEXT NOT NULL,
                    sender_type TEXT NOT NULL,
                    content TEXT NOT NULL,
                    embedding BLOB,
                    timestamp BIGINT NOT NULL,
                    recalled INTEGER NOT NULL DEFAULT 0,
                    importance INTEGER NOT NULL DEFAULT 0,
                    expires_at BIGINT
                );
                CREATE INDEX IF NOT EXISTS idx_message_memory_group ON agui_message_memory(group_id, timestamp);
                CREATE INDEX IF NOT EXISTS idx_message_memory_sender ON agui_message_memory(sender_id, timestamp);
                {(_vecEnabled ? $"""
                CREATE VIRTUAL TABLE IF NOT EXISTS agui_message_memory_vec USING vec0(
                    message_id TEXT PRIMARY KEY,
                    embedding float[{_dimensions}] distance_metric=cosine
                );
                """ : "-- vec0 不可用，向量存 BLOB（内存余弦检索）")}
                """;
            cmd.ExecuteNonQuery();
            // 旧库迁移：补记忆治理列（已存在则忽略错误，SQLite ADD COLUMN 幂等由 PRAGMA 保障）
            foreach (var ddl in new[] { "ALTER TABLE agui_message_memory ADD COLUMN importance INTEGER NOT NULL DEFAULT 0",
                                        "ALTER TABLE agui_message_memory ADD COLUMN expires_at BIGINT" })
            {
                try { using var c2 = conn.CreateCommand(); c2.CommandText = ddl; c2.ExecuteNonQuery(); }
                catch { /* 列已存在：忽略 */ }
            }
            if (_vecEnabled)
            {
                try { MigrateVecTableIfNeeded(conn); }
                catch (Exception ex)
                {
                    // 迁移失败不阻断启动：降级 BLOB + 内存余弦（功能等价，检索仍正确）
                    _logger.LogWarning(ex, "sqlite-vec 表迁移失败，降级为 BLOB + 内存余弦检索");
                    _vecEnabled = false;
                }
            }
            _ready = true;
            _logger.LogInformation("语义记忆已启用（SQLite{Mode}，向量维度 {Dimensions}）",
                _vecEnabled ? " + sqlite-vec" : "，vec0 扩展不可用已降级为内存余弦", _dimensions);
        }
        catch (Exception ex)
        {
            _ready = false;
            _logger.LogWarning(ex, "语义记忆初始化失败（SQLite 向量存储），记忆功能已禁用");
        }
    }

    /// <summary>尝试加载 sqlite-vec 扩展（vec0）。e_sqlite3 默认支持 enable_load_extension；LoadExtension 失败视为不可用。</summary>
    private bool TryLoadVec0()
    {
        try
        {
            using var conn = (SqliteConnection)_db.Open();
            conn.EnableExtensions(true);
            conn.LoadExtension("vec0");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "sqlite-vec 扩展（vec0）加载失败，降级为 BLOB + 内存余弦检索");
            return false;
        }
    }

    /// <summary>打开连接并加载 sqlite-vec 扩展。sqlite3_load_extension 按连接生效，
    /// 只加载一次只影响当前连接——后续每次开连接都必须重新加载，否则报 <c>no such module: vec0</c>。</summary>
    private SqliteConnection OpenVec()
    {
        var conn = (SqliteConnection)_db.Open();
        if (_vecEnabled)
        {
            try
            {
                conn.EnableExtensions(true);
                conn.LoadExtension("vec0");
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "sqlite-vec 扩展本次连接加载失败");
            }
        }
        return conn;
    }

    /// <summary>旧版 vec0 表（默认 L2 欧氏度量）迁移为 cosine 度量：
    /// bge-m3 等 embedding 输出未归一化，L2 距离直接做 <c>1 - distance</c> 的相似度无意义（全为负、检索恒空）；
    /// cosine 度量下 <c>distance = 1 - cos</c>，与 PostgreSQL pgvector 的 <c>1 - 余弦距离</c> 语义一致。
    /// 迁移时从元数据表 <c>agui_message_memory.embedding</c>（BLOB）回填向量，一次性执行。</summary>
    private void MigrateVecTableIfNeeded(SqliteConnection conn)
    {
        string? ddl;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'agui_message_memory_vec'";
            ddl = cmd.ExecuteScalar() as string;
        }
        if (ddl is null) return; // 新库：建表语句已带 cosine
        if (ddl.Contains("distance_metric=cosine", StringComparison.OrdinalIgnoreCase)) return;

        using var tx = conn.BeginTransaction();
        try
        {
            using (var drop = conn.CreateCommand())
            {
                drop.Transaction = tx;
                drop.CommandText = "DROP TABLE agui_message_memory_vec";
                drop.ExecuteNonQuery();
            }
            using (var create = conn.CreateCommand())
            {
                create.Transaction = tx;
                create.CommandText = $"CREATE VIRTUAL TABLE agui_message_memory_vec USING vec0(message_id TEXT PRIMARY KEY, embedding float[{_dimensions}] distance_metric=cosine)";
                create.ExecuteNonQuery();
            }

            using var read = conn.CreateCommand();
            read.Transaction = tx;
            read.CommandText = "SELECT message_id, embedding FROM agui_message_memory WHERE embedding IS NOT NULL";
            using var reader = read.ExecuteReader();
            var insert = conn.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = "INSERT INTO agui_message_memory_vec (message_id, embedding) VALUES (@mid, @vec)";
            var pMid = insert.Parameters.Add("@mid", SqliteType.Text);
            var pVec = insert.Parameters.Add("@vec", SqliteType.Text);
            var migrated = 0;
            while (reader.Read())
            {
                pMid.Value = reader.GetString(0);
                pVec.Value = ToVectorText(DecodeVector((byte[])reader.GetValue(1)));
                insert.ExecuteNonQuery();
                migrated++;
            }
            tx.Commit();
            _logger.LogInformation("sqlite-vec 表已迁移为 cosine 距离度量（{Count} 条向量）", migrated);
        }
        catch
        {
            try { tx.Rollback(); } catch { /* 回滚失败忽略 */ }
            throw;
        }
    }

    public void Upsert(MessageMemoryRecord record)
    {
        if (!_ready || record.Embedding.Length == 0) return;
        try
        {
            using var conn = OpenVec();
            using var tx = conn.BeginTransaction();
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO agui_message_memory (message_id, group_id, topic_id, sender_id, sender_type, content, embedding, timestamp, recalled, importance, expires_at)
                    VALUES (@mid, @gid, @topic, @sender, @senderType, @content, @emb, @time, 0, @importance, @expires)
                    ON CONFLICT (message_id) DO UPDATE SET
                        group_id = EXCLUDED.group_id, topic_id = EXCLUDED.topic_id,
                        sender_id = EXCLUDED.sender_id, sender_type = EXCLUDED.sender_type,
                        content = EXCLUDED.content, embedding = EXCLUDED.embedding,
                        timestamp = EXCLUDED.timestamp, importance = EXCLUDED.importance,
                        expires_at = EXCLUDED.expires_at
                    """;
                cmd.Parameters.AddWithValue("mid", record.MessageId);
                cmd.Parameters.AddWithValue("gid", record.GroupId);
                cmd.Parameters.AddWithValue("topic", record.TopicId);
                cmd.Parameters.AddWithValue("sender", record.SenderId);
                cmd.Parameters.AddWithValue("senderType", record.SenderType);
                cmd.Parameters.AddWithValue("content", record.Content);
                cmd.Parameters.AddWithValue("emb", EncodeVector(record.Embedding));
                cmd.Parameters.AddWithValue("time", record.Timestamp);
                cmd.Parameters.AddWithValue("importance", record.Importance);
                cmd.Parameters.AddWithValue("expires", (object?)record.ExpiresAt ?? DBNull.Value);
                cmd.ExecuteNonQuery();
            }
            if (_vecEnabled)
            {
                // vec0 虚拟表不支持 UPSERT（'UPSERT not implemented for virtual table'）：先删后插
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "DELETE FROM agui_message_memory_vec WHERE message_id = @mid";
                cmd.AddWithValue("mid", record.MessageId);
                cmd.ExecuteNonQuery();
            }
            if (_vecEnabled)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT INTO agui_message_memory_vec (message_id, embedding) VALUES (@mid, @vec)";
                cmd.AddWithValue("mid", record.MessageId);
                cmd.AddWithValue("vec", ToVectorText(record.Embedding));
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "语义记忆写入失败：{MessageId}", record.MessageId);
        }
    }

    public void Remove(string groupId, string messageId)
    {
        if (!_ready) return;
        try
        {
            using var conn = OpenVec();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE agui_message_memory SET recalled = 1 WHERE group_id = @gid AND message_id = @mid";
            cmd.Parameters.AddWithValue("gid", groupId);
            cmd.Parameters.AddWithValue("mid", messageId);
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "语义记忆删除失败：{MessageId}", messageId);
        }
    }

    public void RemoveGroup(string groupId)
    {
        if (!_ready) return;
        try
        {
            using var conn = OpenVec();
            using var tx = conn.BeginTransaction();
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "SELECT message_id FROM agui_message_memory WHERE group_id = @gid";
                cmd.Parameters.AddWithValue("gid", groupId);
                var ids = new List<string>();
                using var reader = cmd.ExecuteReader();
                while (reader.Read()) ids.Add(reader.GetString(0));
                foreach (var id in ids)
                {
                    using var del = conn.CreateCommand();
                    del.Transaction = tx;
                    del.CommandText = "DELETE FROM agui_message_memory WHERE message_id = @mid";
                    del.Parameters.AddWithValue("mid", id);
                    del.ExecuteNonQuery();
                    if (_vecEnabled)
                    {
                        using var delVec = conn.CreateCommand();
                        delVec.Transaction = tx;
                        delVec.CommandText = "DELETE FROM agui_message_memory_vec WHERE message_id = @mid";
                        delVec.Parameters.AddWithValue("mid", id);
                        delVec.ExecuteNonQuery();
                    }
                }
            }
            tx.Commit();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "语义记忆群级删除失败：{GroupId}", groupId);
        }
    }

    public void ClearAll()
    {
        if (!_ready) return;
        try
        {
            using var conn = _db.Open();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "DELETE FROM agui_message_memory";
                cmd.ExecuteNonQuery();
            }
            if (_vecEnabled)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "DELETE FROM agui_message_memory_vec";
                cmd.ExecuteNonQuery();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "语义记忆清空失败");
        }
    }

    // ================= 记忆治理（分群分级 / 自动遗忘 / 可视化） =================

    public IReadOnlyList<MessageMemoryItem> ListMessages(string? groupId, string? senderId, string? keyword, int limit, int offset)
    {
        if (!_ready) return [];
        try
        {
            using var conn = _db.Open();
            using var cmd = conn.CreateCommand();
            var sql = "SELECT message_id, group_id, topic_id, sender_id, sender_type, content, timestamp, importance, expires_at FROM agui_message_memory WHERE recalled = 0";
            if (!string.IsNullOrWhiteSpace(groupId)) sql += " AND group_id = @gid";
            if (!string.IsNullOrWhiteSpace(senderId)) sql += " AND sender_id = @sender";
            if (!string.IsNullOrWhiteSpace(keyword)) sql += " AND content LIKE @kw";
            sql += " ORDER BY timestamp DESC LIMIT @limit OFFSET @offset";
            cmd.CommandText = sql;
            if (!string.IsNullOrWhiteSpace(groupId)) cmd.AddWithValue("gid", groupId);
            if (!string.IsNullOrWhiteSpace(senderId)) cmd.AddWithValue("sender", senderId);
            if (!string.IsNullOrWhiteSpace(keyword)) cmd.AddWithValue("kw", $"%{keyword}%");
            cmd.AddWithValue("limit", Math.Clamp(limit, 1, 500));
            cmd.AddWithValue("offset", Math.Max(0, offset));

            var list = new List<MessageMemoryItem>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(new MessageMemoryItem(
                    MessageId: reader.GetString(0),
                    GroupId: reader.GetString(1),
                    TopicId: reader.GetString(2),
                    SenderId: reader.GetString(3),
                    SenderType: reader.GetString(4),
                    Content: reader.GetString(5),
                    Timestamp: reader.GetInt64(6),
                    Importance: reader.GetInt32(7),
                    ExpiresAt: reader.IsDBNull(8) ? null : reader.GetInt64(8)));
            }
            return list;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "记忆列表查询失败");
            return [];
        }
    }

    public long CountMessages(string? groupId, string? senderId, string? keyword)
    {
        if (!_ready) return 0;
        try
        {
            using var conn = _db.Open();
            using var cmd = conn.CreateCommand();
            var sql = "SELECT COUNT(*) FROM agui_message_memory WHERE recalled = 0";
            if (!string.IsNullOrWhiteSpace(groupId)) sql += " AND group_id = @gid";
            if (!string.IsNullOrWhiteSpace(senderId)) sql += " AND sender_id = @sender";
            if (!string.IsNullOrWhiteSpace(keyword)) sql += " AND content LIKE @kw";
            cmd.CommandText = sql;
            if (!string.IsNullOrWhiteSpace(groupId)) cmd.AddWithValue("gid", groupId);
            if (!string.IsNullOrWhiteSpace(senderId)) cmd.AddWithValue("sender", senderId);
            if (!string.IsNullOrWhiteSpace(keyword)) cmd.AddWithValue("kw", $"%{keyword}%");
            return Convert.ToInt64(cmd.ExecuteScalar());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "记忆计数查询失败");
            return 0;
        }
    }

    public IReadOnlyList<MessageMemoryGroupStat> GroupStats(long nowMs)
    {
        if (!_ready) return [];
        try
        {
            using var conn = _db.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT group_id,
                       COUNT(*) AS cnt,
                       MAX(timestamp) AS last_at,
                       COUNT(*) FILTER (WHERE expires_at IS NOT NULL AND expires_at <= @now) AS expired
                FROM agui_message_memory
                WHERE recalled = 0
                GROUP BY group_id
                ORDER BY last_at DESC
                """;
            cmd.AddWithValue("now", nowMs);
            var list = new List<MessageMemoryGroupStat>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(new MessageMemoryGroupStat(
                    GroupId: reader.GetString(0),
                    Count: reader.GetInt32(1),
                    LastAt: reader.GetInt64(2),
                    ExpiredCount: reader.GetInt32(3)));
            }
            return list;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "记忆群统计查询失败");
            return [];
        }
    }

    public bool DeleteByMessageId(string messageId)
    {
        if (!_ready) return false;
        try
        {
            using var conn = OpenVec();
            using var tx = conn.BeginTransaction();
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "DELETE FROM agui_message_memory WHERE message_id = @mid";
                cmd.AddWithValue("mid", messageId);
                var n = cmd.ExecuteNonQuery();
                if (_vecEnabled)
                {
                    using var delVec = conn.CreateCommand();
                    delVec.Transaction = tx;
                    delVec.CommandText = "DELETE FROM agui_message_memory_vec WHERE message_id = @mid";
                    delVec.AddWithValue("mid", messageId);
                    delVec.ExecuteNonQuery();
                }
                tx.Commit();
                return n > 0;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "记忆删除失败：{MessageId}", messageId);
            return false;
        }
    }

    public MessageMemoryItem? GetByMessageId(string messageId)
    {
        if (!_ready || string.IsNullOrWhiteSpace(messageId)) return null;
        try
        {
            using var conn = _db.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT message_id, group_id, topic_id, sender_id, sender_type, content, timestamp, importance, expires_at
                FROM agui_message_memory WHERE message_id = @mid AND recalled = 0
                """;
            cmd.AddWithValue("mid", messageId);
            using var reader = cmd.ExecuteReader();
            return reader.Read()
                ? new MessageMemoryItem(
                    MessageId: reader.GetString(0),
                    GroupId: reader.GetString(1),
                    TopicId: reader.GetString(2),
                    SenderId: reader.GetString(3),
                    SenderType: reader.GetString(4),
                    Content: reader.GetString(5),
                    Timestamp: reader.GetInt64(6),
                    Importance: reader.GetInt32(7),
                    ExpiresAt: reader.IsDBNull(8) ? null : reader.GetInt64(8))
                : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "记忆单条查询失败：{MessageId}", messageId);
            return null;
        }
    }

    public bool UpdateImportance(string messageId, int importance)
    {
        if (!_ready || !MemoryImportance.IsValid(importance)) return false;
        try
        {
            using var conn = _db.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE agui_message_memory SET importance = @imp WHERE message_id = @mid";
            cmd.AddWithValue("imp", importance);
            cmd.AddWithValue("mid", messageId);
            return cmd.ExecuteNonQuery() > 0;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "记忆分级失败：{MessageId}", messageId);
            return false;
        }
    }

    public int SetExpiry(string? groupId, long? expiresAt, long nowMs)
    {
        if (!_ready) return 0;
        try
        {
            using var conn = _db.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE agui_message_memory SET expires_at = @expires WHERE recalled = 0"
                + (string.IsNullOrWhiteSpace(groupId) ? "" : " AND group_id = @gid");
            cmd.AddWithValue("expires", expiresAt ?? nowMs - 1);
            if (!string.IsNullOrWhiteSpace(groupId)) cmd.AddWithValue("gid", groupId);
            return cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "记忆遗忘设置失败：{GroupId}", groupId ?? "*");
            return 0;
        }
    }

    public int PruneExpired(long nowMs)
    {
        if (!_ready) return 0;
        try
        {
            using var conn = OpenVec();
            using var tx = conn.BeginTransaction();
            int deleted;
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                // 自动遗忘豁免：仅清理普通记忆（importance=0）；用户手动标记重要（>0）的记忆不受保留期限制
                cmd.CommandText = "SELECT message_id FROM agui_message_memory WHERE expires_at IS NOT NULL AND expires_at <= @now AND importance = 0";
                cmd.AddWithValue("now", nowMs);
                var ids = new List<string>();
                using var reader = cmd.ExecuteReader();
                while (reader.Read()) ids.Add(reader.GetString(0));
                deleted = ids.Count;
                foreach (var id in ids)
                {
                    using var del = conn.CreateCommand();
                    del.Transaction = tx;
                    del.CommandText = "DELETE FROM agui_message_memory WHERE message_id = @mid";
                    del.AddWithValue("mid", id);
                    del.ExecuteNonQuery();
                    if (_vecEnabled)
                    {
                        using var delVec = conn.CreateCommand();
                        delVec.Transaction = tx;
                        delVec.CommandText = "DELETE FROM agui_message_memory_vec WHERE message_id = @mid";
                        delVec.AddWithValue("mid", id);
                        delVec.ExecuteNonQuery();
                    }
                }
            }
            tx.Commit();
            return deleted;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "过期记忆清理失败");
            return 0;
        }
    }

    public IReadOnlyList<MessageMemoryHit> Search(string groupId, string? agentId, float[] embedding, int topK, double minScore, string scope)
    {
        if (!_ready || embedding.Length == 0) return [];
        topK = Math.Clamp(topK, 1, 100); // 上限钳制：防外部传入超大 topK 拖垮检索（下限保持 ≥1）
        try
        {
            var scopeKey = scope.ToLowerInvariant();
            var groupFilter = scopeKey switch
            {
                "group" => " AND m.group_id = @gid",
                "all" => "",
                _ => " AND m.group_id IN (SELECT group_id FROM agui_group_members WHERE member_id = @agent)",
            };
            // 私密群记忆隔离：私密群的记忆仅允许在当前触发群（本群）内被检索到
            const string privacy = " AND (m.group_id = @gid OR COALESCE(g.is_private, 0) = 0)";
            // 知识库行（sender_type='kb'）仅在其专属检索（groupId=kb:{id}）中读取，普通群记忆一律排除
            const string kbFilter = " AND (m.sender_type <> 'kb' OR m.group_id LIKE 'kb:%')";
            using var conn = OpenVec();
            if (_vecEnabled)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"""
                    SELECT m.message_id, m.content, m.sender_id, m.timestamp,
                           1 - v.distance AS score, m.importance, m.group_id
                    FROM agui_message_memory_vec v
                    JOIN agui_message_memory m ON m.message_id = v.message_id
                    LEFT JOIN agui_groups g ON g.group_id = m.group_id
                    WHERE v.embedding MATCH @q AND v.k = @cand
                      AND m.recalled = 0{groupFilter}{privacy}{kbFilter}
                      AND (m.expires_at IS NULL OR m.expires_at > @now)
                    ORDER BY v.distance, m.importance DESC
                    """;
                cmd.Parameters.AddWithValue("q", ToVectorText(embedding));
                cmd.Parameters.AddWithValue("cand", Math.Max(topK * 4, 50));
                cmd.Parameters.AddWithValue("gid", groupId);
                cmd.Parameters.AddWithValue("now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                if (scopeKey is not ("group" or "all")) cmd.Parameters.AddWithValue("agent", agentId);
                return ReadHits(cmd, minScore, topK); // topK 已在方法入口 Math.Clamp(1, 100)
            }
            return SearchInMemory(conn, groupId, agentId, embedding, topK, minScore, scopeKey, groupFilter, privacy, senderFilter: null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "语义记忆检索失败");
            return [];
        }
    }

    public IReadOnlyList<MessageMemoryHit> SearchPerson(string personId, string currentGroupId, float[] embedding, int topK, double minScore)
    {
        if (!_ready || embedding.Length == 0) return [];
        try
        {
            const string privacy = " AND (m.group_id = @gid OR COALESCE(g.is_private, 0) = 0)";
            const string senderFilter = " AND m.sender_id = @person";
            using var conn = OpenVec();
            if (_vecEnabled)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"""
                    SELECT m.message_id, m.content, m.sender_id, m.timestamp,
                           1 - v.distance AS score, m.importance, m.group_id
                    FROM agui_message_memory_vec v
                    JOIN agui_message_memory m ON m.message_id = v.message_id
                    LEFT JOIN agui_groups g ON g.group_id = m.group_id
                    WHERE v.embedding MATCH @q AND v.k = @cand
                      AND m.recalled = 0{senderFilter}{privacy}
                      AND (m.expires_at IS NULL OR m.expires_at > @now)
                    ORDER BY v.distance, m.importance DESC
                    """;
                cmd.Parameters.AddWithValue("q", ToVectorText(embedding));
                cmd.Parameters.AddWithValue("cand", Math.Max(topK * 4, 50));
                cmd.Parameters.AddWithValue("gid", currentGroupId);
                cmd.Parameters.AddWithValue("person", personId);
                cmd.Parameters.AddWithValue("now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                return ReadHits(cmd, minScore, Math.Max(1, topK));
            }
            return SearchInMemory(conn, currentGroupId, null, embedding, topK, minScore, "agent", "", privacy, senderFilter, personId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "个人记忆检索失败");
            return [];
        }
    }

    /// <summary>读取候选行并按 score 过滤、排序取 topK（vec0 已按距离排序，直接截取即可）。</summary>
    private static IReadOnlyList<MessageMemoryHit> ReadHits(SqliteCommand cmd, double minScore, int topK)
    {
        var list = new List<MessageMemoryHit>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var score = reader.GetDouble(4);
            if (score < minScore) continue;
            list.Add(new MessageMemoryHit(
                MessageId: reader.GetString(0),
                Content: reader.GetString(1),
                SenderId: reader.GetString(2),
                Timestamp: reader.GetInt64(3),
                Score: score,
                Importance: reader.GetInt32(5),
                GroupId: reader.GetString(6)));
            if (list.Count >= topK) break;
        }
        return list;
    }

    /// <summary>BLOB 降级模式：全表扫描 + .NET 余弦相似度（过滤条件与 vec0 路径一致）。</summary>
    private IReadOnlyList<MessageMemoryHit> SearchInMemory(SqliteConnection conn, string groupId, string? agentId,
        float[] embedding, int topK, double minScore, string scopeKey, string groupFilter, string privacy, string? senderFilter, string? personId = null)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT m.message_id, m.content, m.sender_id, m.timestamp, m.embedding, m.importance, m.expires_at, m.group_id
            FROM agui_message_memory m
            LEFT JOIN agui_groups g ON g.group_id = m.group_id
            WHERE m.recalled = 0 AND m.embedding IS NOT NULL AND (m.sender_type <> 'kb' OR m.group_id LIKE 'kb:%'){groupFilter}{privacy}{senderFilter}
              AND (m.expires_at IS NULL OR m.expires_at > @now)
            """;
        if (scopeKey is not ("group" or "all")) cmd.Parameters.AddWithValue("agent", agentId);
        cmd.Parameters.AddWithValue("gid", groupId);
        cmd.Parameters.AddWithValue("now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        if (senderFilter is not null) cmd.Parameters.AddWithValue("person", personId ?? "");

        var scored = new List<(MessageMemoryHit Hit, double Score, int Importance)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var bytes = (byte[])reader.GetValue(4);
            var vec = DecodeVector(bytes);
            if (vec.Length != embedding.Length) continue;
            var score = Cosine(vec, embedding);
            if (score < minScore) continue;
            scored.Add((new MessageMemoryHit(
                MessageId: reader.GetString(0),
                Content: reader.GetString(1),
                SenderId: reader.GetString(2),
                Timestamp: reader.GetInt64(3),
                Score: score,
                Importance: reader.GetInt32(5),
                GroupId: reader.GetString(7)), score, reader.GetInt32(5)));
        }
        return scored
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Importance)
            .Take(Math.Max(1, topK))
            .Select(x => x.Hit)
            .ToList();
    }

    private static double Cosine(float[] a, float[] b)
    {
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += (double)a[i] * b[i];
            na += (double)a[i] * a[i];
            nb += (double)b[i] * b[i];
        }
        return na == 0 || nb == 0 ? 0 : dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }

    /// <summary>向量 → 二进制 BLOB（float32 LE 顺序）。</summary>
    private static byte[] EncodeVector(float[] v)
    {
        var bytes = new byte[v.Length * 4];
        for (var i = 0; i < v.Length; i++)
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 4), v[i]);
        return bytes;
    }

    private static float[] DecodeVector(byte[] bytes)
    {
        var v = new float[bytes.Length / 4];
        for (var i = 0; i < v.Length; i++)
            v[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(i * 4));
        return v;
    }

    /// <summary>向量 → sqlite-vec MATCH 参数（JSON 数组文本，与 pgvector 文本格式同构）。</summary>
    private static string ToVectorText(float[] v)
    {
        var sb = new StringBuilder("[");
        for (var i = 0; i < v.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(v[i].ToString("R", CultureInfo.InvariantCulture));
        }
        sb.Append(']');
        return sb.ToString();
    }
}
