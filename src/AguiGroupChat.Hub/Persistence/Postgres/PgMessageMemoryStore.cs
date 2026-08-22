using System.Globalization;
using AguiGroupChat.Hub.Models;
using Npgsql;

namespace AguiGroupChat.Hub.Persistence.Postgres;

/// <summary>
/// PostgreSQL + pgvector 语义记忆存储：消息内容向量化后写入 agui_message_memory 表，
/// 检索用余弦距离（<c>embedding &lt;-&gt; query</c>）走 HNSW 索引。
/// 向量参数以 pgvector 文本格式「[0.1,0.2,…]」显式 <c>::vector</c> 转换传递，
/// 不引入额外类型映射，兼容 Npgsql 各版本。
/// pgvector 扩展不可用（未安装 / 非 PostgreSQL）时自动降级：写入与检索静默失效，不影响群聊主流程。
/// </summary>
public sealed class PgMessageMemoryStore : IMessageMemoryStore
{
    private readonly PostgresStore _pg;
    private readonly int _dimensions;
    private readonly ILogger<PgMessageMemoryStore> _logger;
    private volatile bool _ready;

    public PgMessageMemoryStore(PostgresStore pg, int dimensions, ILogger<PgMessageMemoryStore> logger)
    {
        _pg = pg;
        _dimensions = Math.Max(8, dimensions);
        _logger = logger;
    }

    public void EnsureSchema()
    {
        try
        {
            using var conn = _pg.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $$"""
                CREATE EXTENSION IF NOT EXISTS vector;
                CREATE TABLE IF NOT EXISTS agui_message_memory (
                    message_id TEXT PRIMARY KEY,
                    group_id TEXT NOT NULL,
                    topic_id TEXT NOT NULL DEFAULT 'main',
                    sender_id TEXT NOT NULL,
                    sender_type TEXT NOT NULL,
                    content TEXT NOT NULL,
                    embedding vector({{_dimensions}}),
                    timestamp BIGINT NOT NULL,
                    recalled BOOLEAN NOT NULL DEFAULT FALSE,
                    importance INTEGER NOT NULL DEFAULT 0,
                    expires_at BIGINT
                );
                CREATE INDEX IF NOT EXISTS idx_message_memory_hnsw ON agui_message_memory USING hnsw (embedding vector_cosine_ops);
                CREATE INDEX IF NOT EXISTS idx_message_memory_group ON agui_message_memory(group_id, timestamp);
                -- 旧库迁移（CREATE TABLE IF NOT EXISTS 不修改已有表）
                ALTER TABLE agui_message_memory ADD COLUMN IF NOT EXISTS importance INTEGER NOT NULL DEFAULT 0;
                ALTER TABLE agui_message_memory ADD COLUMN IF NOT EXISTS expires_at BIGINT;
                """;
            cmd.ExecuteNonQuery();
            _ready = true;
            _logger.LogInformation("语义记忆已启用（pgvector，向量维度 {Dimensions}）", _dimensions);
        }
        catch (Exception ex)
        {
            _ready = false;
            _logger.LogWarning(ex, "语义记忆初始化失败（需 PostgreSQL + pgvector 扩展），记忆功能已禁用");
        }
    }

    public void Upsert(MessageMemoryRecord record)
    {
        if (!_ready || record.Embedding.Length == 0) return;
        try
        {
            using var conn = _pg.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO agui_message_memory (message_id, group_id, topic_id, sender_id, sender_type, content, embedding, timestamp, recalled, importance, expires_at)
                VALUES (@mid, @gid, @topic, @sender, @senderType, @content, @emb::vector, @time, FALSE, @importance, @expires)
                ON CONFLICT (message_id) DO UPDATE SET
                    group_id = EXCLUDED.group_id,
                    topic_id = EXCLUDED.topic_id,
                    sender_id = EXCLUDED.sender_id,
                    sender_type = EXCLUDED.sender_type,
                    content = EXCLUDED.content,
                    embedding = EXCLUDED.embedding,
                    timestamp = EXCLUDED.timestamp,
                    importance = EXCLUDED.importance,
                    expires_at = EXCLUDED.expires_at
                """;
            cmd.Parameters.AddWithValue("mid", record.MessageId);
            cmd.Parameters.AddWithValue("gid", record.GroupId);
            cmd.Parameters.AddWithValue("topic", record.TopicId);
            cmd.Parameters.AddWithValue("sender", record.SenderId);
            cmd.Parameters.AddWithValue("senderType", record.SenderType);
            cmd.Parameters.AddWithValue("content", record.Content);
            cmd.Parameters.AddWithValue("emb", ToVectorText(record.Embedding));
            cmd.Parameters.AddWithValue("time", record.Timestamp);
            cmd.Parameters.AddWithValue("importance", record.Importance);
            cmd.Parameters.AddWithValue("expires", (object?)record.ExpiresAt ?? DBNull.Value);
            cmd.ExecuteNonQuery();
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
            using var conn = _pg.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE agui_message_memory SET recalled = TRUE WHERE group_id = @gid AND message_id = @mid";
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
            using var conn = _pg.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM agui_message_memory WHERE group_id = @gid";
            cmd.Parameters.AddWithValue("gid", groupId);
            cmd.ExecuteNonQuery();
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
            using var conn = _pg.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM agui_message_memory";
            cmd.ExecuteNonQuery();
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
            using var conn = _pg.Open();
            using var cmd = conn.CreateCommand();
            var sql = """
                SELECT message_id, group_id, topic_id, sender_id, sender_type, content, timestamp, importance, expires_at
                FROM agui_message_memory
                WHERE recalled = FALSE
                """;
            if (!string.IsNullOrWhiteSpace(groupId)) { sql += " AND group_id = @gid"; }
            if (!string.IsNullOrWhiteSpace(senderId)) { sql += " AND sender_id = @sender"; }
            if (!string.IsNullOrWhiteSpace(keyword)) { sql += " AND content ILIKE @kw"; }
            sql += " ORDER BY timestamp DESC LIMIT @limit OFFSET @offset";
            cmd.CommandText = sql;
            if (!string.IsNullOrWhiteSpace(groupId)) cmd.Parameters.AddWithValue("gid", groupId);
            if (!string.IsNullOrWhiteSpace(senderId)) cmd.Parameters.AddWithValue("sender", senderId);
            if (!string.IsNullOrWhiteSpace(keyword)) cmd.Parameters.AddWithValue("kw", $"%{keyword}%");
            cmd.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, 500));
            cmd.Parameters.AddWithValue("offset", Math.Max(0, offset));

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
            using var conn = _pg.Open();
            using var cmd = conn.CreateCommand();
            var sql = "SELECT COUNT(*) FROM agui_message_memory WHERE recalled = FALSE";
            if (!string.IsNullOrWhiteSpace(groupId)) sql += " AND group_id = @gid";
            if (!string.IsNullOrWhiteSpace(senderId)) sql += " AND sender_id = @sender";
            if (!string.IsNullOrWhiteSpace(keyword)) sql += " AND content ILIKE @kw";
            cmd.CommandText = sql;
            if (!string.IsNullOrWhiteSpace(groupId)) cmd.Parameters.AddWithValue("gid", groupId);
            if (!string.IsNullOrWhiteSpace(senderId)) cmd.Parameters.AddWithValue("sender", senderId);
            if (!string.IsNullOrWhiteSpace(keyword)) cmd.Parameters.AddWithValue("kw", $"%{keyword}%");
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
            using var conn = _pg.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT group_id,
                       COUNT(*) AS cnt,
                       MAX(timestamp) AS last_at,
                       COUNT(*) FILTER (WHERE expires_at IS NOT NULL AND expires_at <= @now) AS expired
                FROM agui_message_memory
                WHERE recalled = FALSE
                GROUP BY group_id
                ORDER BY last_at DESC
                """;
            cmd.Parameters.AddWithValue("now", nowMs);
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
            using var conn = _pg.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM agui_message_memory WHERE message_id = @mid";
            cmd.Parameters.AddWithValue("mid", messageId);
            return cmd.ExecuteNonQuery() > 0;
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
            using var conn = _pg.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT message_id, group_id, topic_id, sender_id, sender_type, content, timestamp, importance, expires_at
                FROM agui_message_memory WHERE message_id = @mid AND recalled = FALSE
                """;
            cmd.Parameters.AddWithValue("mid", messageId);
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
            using var conn = _pg.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE agui_message_memory SET importance = @imp WHERE message_id = @mid";
            cmd.Parameters.AddWithValue("imp", importance);
            cmd.Parameters.AddWithValue("mid", messageId);
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
            using var conn = _pg.Open();
            using var cmd = conn.CreateCommand();
            // 手动遗忘：expiresAt 为 null 时表示「立即过期」（设为过去时间戳）；否则按给定时间戳设过期
            cmd.CommandText = "UPDATE agui_message_memory SET expires_at = @expires WHERE recalled = FALSE"
                + (string.IsNullOrWhiteSpace(groupId) ? "" : " AND group_id = @gid");
            cmd.Parameters.AddWithValue("expires", expiresAt ?? nowMs - 1);
            if (!string.IsNullOrWhiteSpace(groupId)) cmd.Parameters.AddWithValue("gid", groupId);
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
            using var conn = _pg.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM agui_message_memory WHERE expires_at IS NOT NULL AND expires_at <= @now AND importance = 0";
            cmd.Parameters.AddWithValue("now", nowMs);
            // 自动遗忘豁免：仅清理普通记忆（importance=0）；用户手动标记重要（>0）的记忆不受保留期限制
            return cmd.ExecuteNonQuery();
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
            using var conn = _pg.Open();
            using var cmd = conn.CreateCommand();
            // 检索范围：group = 仅当前触发群；agent = 该智能体所在的所有群；all = 全部群
            var scopeKey = scope.ToLowerInvariant();
            var groupFilter = scopeKey switch
            {
                "group" => " AND m.group_id = @gid",
                "all" => "",
                _ => " AND m.group_id IN (SELECT group_id FROM agui_group_members WHERE member_id = @agent)",
            };
            // 私密群记忆隔离：私密群的记忆仅允许在当前触发群（本群）内被检索到；
            // 智能体在其他群触发时（scope=agent/all）一律排除私密群内容。
            // 知识库行（sender_type='kb'）仅在其专属检索（groupId=kb:{id}）中读取，
            // 普通群记忆检索一律排除（防止 kb: 伪群向量混入群记忆 RAG）。
            cmd.CommandText = $"""
                SELECT m.message_id, m.content, m.sender_id, m.timestamp,
                       1 - (m.embedding <=> @q::vector) AS score, m.importance, m.group_id
                FROM agui_message_memory m
                LEFT JOIN agui_groups g ON g.group_id = m.group_id
                WHERE m.recalled = FALSE
                  AND m.embedding IS NOT NULL
                  AND (m.expires_at IS NULL OR m.expires_at > @now)
                  AND (m.sender_type <> 'kb' OR m.group_id LIKE 'kb:%')
                  AND 1 - (m.embedding <=> @q::vector) >= @minScore{groupFilter}
                  AND (m.group_id = @gid OR COALESCE(g.is_private, FALSE) = FALSE)
                ORDER BY m.embedding <=> @q::vector, m.importance DESC
                LIMIT @k
                """;
            cmd.Parameters.AddWithValue("q", ToVectorText(embedding));
            cmd.Parameters.AddWithValue("minScore", minScore);
            cmd.Parameters.AddWithValue("k", topK); // 已在方法入口 Math.Clamp(1, 100)
            cmd.Parameters.AddWithValue("now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            // @gid 用于私密过滤（所有 scope 均需要）：私密群自身检索仅保留本群记忆
            cmd.Parameters.AddWithValue("gid", groupId);
            if (scopeKey is not ("group" or "all")) cmd.Parameters.AddWithValue("agent", agentId);

            var list = new List<MessageMemoryHit>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(new MessageMemoryHit(
                    MessageId: reader.GetString(0),
                    Content: reader.GetString(1),
                    SenderId: reader.GetString(2),
                    Timestamp: reader.GetInt64(3),
                    Score: reader.GetDouble(4),
                    Importance: reader.GetInt32(5),
                    GroupId: reader.GetString(6)));
            }
            return list;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "语义记忆检索失败");
            return [];
        }
    }

    /// <summary>检索某个人（用户或智能体）自己的历史发言（个人记忆）：按 sender_id 过滤，
    /// 跨群检索且遵守私密群隔离（非当前触发群的私密群内容不进入个人记忆）。</summary>
    public IReadOnlyList<MessageMemoryHit> SearchPerson(string personId, string currentGroupId, float[] embedding, int topK, double minScore)
    {
        if (!_ready || embedding.Length == 0) return [];
        try
        {
            using var conn = _pg.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT m.message_id, m.content, m.sender_id, m.timestamp,
                       1 - (m.embedding <=> @q::vector) AS score, m.importance, m.group_id
                FROM agui_message_memory m
                LEFT JOIN agui_groups g ON g.group_id = m.group_id
                WHERE m.recalled = FALSE
                  AND m.embedding IS NOT NULL
                  AND m.sender_id = @person
                  AND (m.expires_at IS NULL OR m.expires_at > @now)
                  AND 1 - (m.embedding <=> @q::vector) >= @minScore
                  AND (m.group_id = @gid OR COALESCE(g.is_private, FALSE) = FALSE)
                ORDER BY m.embedding <=> @q::vector, m.importance DESC
                LIMIT @k
                """;
            cmd.Parameters.AddWithValue("q", ToVectorText(embedding));
            cmd.Parameters.AddWithValue("person", personId);
            cmd.Parameters.AddWithValue("gid", currentGroupId);
            cmd.Parameters.AddWithValue("minScore", minScore);
            cmd.Parameters.AddWithValue("k", Math.Max(1, topK));
            cmd.Parameters.AddWithValue("now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

            var list = new List<MessageMemoryHit>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(new MessageMemoryHit(
                    MessageId: reader.GetString(0),
                    Content: reader.GetString(1),
                    SenderId: reader.GetString(2),
                    Timestamp: reader.GetInt64(3),
                    Score: reader.GetDouble(4),
                    Importance: reader.GetInt32(5),
                    GroupId: reader.GetString(6)));
            }
            return list;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "个人记忆检索失败");
            return [];
        }
    }

    /// <summary>float[] → pgvector 文本格式「[0.1,0.2,…]」（G9 保证往返精度）。</summary>
    internal static string ToVectorText(float[] v)
        => "[" + string.Join(",", v.Select(f => f.ToString("G9", CultureInfo.InvariantCulture))) + "]";
}
