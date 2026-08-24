using Npgsql;

namespace AguiGroupChat.Hub.Persistence.Postgres;

/// <summary>PostgreSQL 存储：共享连接工厂 + 自动建表。</summary>
public sealed class PostgresStore
{
    internal readonly string ConnectionString;

    public PostgresStore(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("Storage:ConnectionString 未配置（Storage.Provider=postgres 时必填）");
        ConnectionString = connectionString;
    }

    internal NpgsqlConnection Open()
    {
        var conn = new NpgsqlConnection(ConnectionString);
        try
        {
            conn.Open();
        }
        catch
        {
            conn.Dispose(); // 连接失败时释放句柄，避免泄漏
            throw;
        }
        return conn;
    }

    /// <summary>清空全部业务表（系统初始化用，保留 schema）：按外键顺序先子后主，单事务提交。</summary>
    public void ClearAllData()
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        foreach (var table in new[]
        {
            "agui_message_memory", "agui_agent_registrations", "agui_group_reads",
            "agui_messages", "agui_topics", "agui_group_members", "agui_groups",
            "agui_users", "agui_sections", "agui_usage", "agui_tasks",
        })
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"DELETE FROM {table}";
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>启动时建表（幂等）。</summary>
    public void EnsureSchema()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS agui_groups (
                group_id TEXT PRIMARY KEY,
                group_name TEXT NOT NULL,
                group_avatar TEXT,
                owner_id TEXT NOT NULL,
                member_count INTEGER NOT NULL DEFAULT 0,
                create_time BIGINT NOT NULL,
                extra TEXT,
                is_private BOOLEAN NOT NULL DEFAULT FALSE
            );
            -- 旧库迁移（CREATE TABLE IF NOT EXISTS 不修改已有表）
            ALTER TABLE agui_groups ADD COLUMN IF NOT EXISTS is_private BOOLEAN NOT NULL DEFAULT FALSE;

            CREATE TABLE IF NOT EXISTS agui_group_members (
                group_id TEXT NOT NULL,
                member_id TEXT NOT NULL,
                member_type TEXT NOT NULL,
                nickname TEXT NOT NULL,
                avatar TEXT,
                role TEXT NOT NULL,
                online_status TEXT NOT NULL,
                join_time BIGINT NOT NULL,
                trigger_mode TEXT,
                keywords TEXT,
                is_trigger_overridden BOOLEAN NOT NULL DEFAULT FALSE,
                extra TEXT,
                PRIMARY KEY (group_id, member_id)
            );
            CREATE INDEX IF NOT EXISTS idx_members_member ON agui_group_members(member_id);

            CREATE TABLE IF NOT EXISTS agui_topics (
                topic_id TEXT PRIMARY KEY,
                group_id TEXT NOT NULL,
                name TEXT NOT NULL,
                creator_id TEXT NOT NULL,
                created_at BIGINT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_topics_group ON agui_topics(group_id);

            CREATE TABLE IF NOT EXISTS agui_messages (
                message_id TEXT PRIMARY KEY,
                group_id TEXT NOT NULL,
                topic_id TEXT NOT NULL DEFAULT 'main',
                thread_id TEXT NOT NULL,
                sender_id TEXT NOT NULL,
                sender_type TEXT NOT NULL,
                sender_nickname TEXT NOT NULL,
                reply_to_message_id TEXT,
                mentions TEXT,
                mention_all BOOLEAN NOT NULL DEFAULT FALSE,
                visibility TEXT NOT NULL DEFAULT 'all',
                visible_member_ids TEXT,
                attachments TEXT,
                content TEXT NOT NULL,
                reasoning TEXT,
                agent_chain TEXT,
                timestamp BIGINT NOT NULL,
                recalled BOOLEAN NOT NULL DEFAULT FALSE
            );
            CREATE INDEX IF NOT EXISTS idx_messages_group ON agui_messages(group_id, timestamp);
            CREATE INDEX IF NOT EXISTS idx_messages_topic ON agui_messages(group_id, topic_id, timestamp);
            -- 旧库迁移（CREATE TABLE IF NOT EXISTS 不修改已有表）
            ALTER TABLE agui_messages ADD COLUMN IF NOT EXISTS reasoning TEXT;
            ALTER TABLE agui_messages ADD COLUMN IF NOT EXISTS agent_chain TEXT;

            CREATE TABLE IF NOT EXISTS agui_users (
                user_id TEXT PRIMARY KEY,
                username TEXT NOT NULL UNIQUE,
                password_hash TEXT NOT NULL,
                password_salt TEXT NOT NULL,
                nickname TEXT NOT NULL DEFAULT '',
                avatar TEXT,
                created_at BIGINT NOT NULL,
                updated_at BIGINT NOT NULL,
                personal_memory_enabled BOOLEAN NOT NULL DEFAULT FALSE,
                is_admin BOOLEAN NOT NULL DEFAULT FALSE
            );
            -- 旧库迁移（CREATE TABLE IF NOT EXISTS 不修改已有表）
            ALTER TABLE agui_users ADD COLUMN IF NOT EXISTS personal_memory_enabled BOOLEAN NOT NULL DEFAULT FALSE;
            ALTER TABLE agui_users ADD COLUMN IF NOT EXISTS is_admin BOOLEAN NOT NULL DEFAULT FALSE;
            ALTER TABLE agui_users ADD COLUMN IF NOT EXISTS is_disabled BOOLEAN NOT NULL DEFAULT FALSE;
            -- 与内存实现一致：用户名唯一且大小写不敏感
            CREATE UNIQUE INDEX IF NOT EXISTS idx_users_username_ci ON agui_users (LOWER(username));

            CREATE TABLE IF NOT EXISTS agui_agent_registrations (
                agent_id TEXT NOT NULL,
                group_id TEXT NOT NULL,
                nickname TEXT NOT NULL DEFAULT '',
                trigger_mode TEXT NOT NULL,
                keywords TEXT,
                is_overridden BOOLEAN NOT NULL DEFAULT FALSE,
                PRIMARY KEY (agent_id, group_id)
            );

            CREATE TABLE IF NOT EXISTS agui_group_reads (
                member_id TEXT NOT NULL,
                group_id TEXT NOT NULL,
                topic_id TEXT NOT NULL,
                read_at BIGINT NOT NULL DEFAULT 0,
                PRIMARY KEY (member_id, group_id, topic_id)
            );
            CREATE INDEX IF NOT EXISTS idx_reads_group ON agui_group_reads(group_id);

            CREATE TABLE IF NOT EXISTS agui_sections (
                name TEXT PRIMARY KEY,
                payload TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS agui_usage (
                usage_date TEXT NOT NULL,
                agent_id TEXT NOT NULL,
                user_id TEXT NOT NULL,
                prompt_tokens BIGINT NOT NULL DEFAULT 0,
                completion_tokens BIGINT NOT NULL DEFAULT 0,
                reasoning_tokens BIGINT NOT NULL DEFAULT 0,
                calls BIGINT NOT NULL DEFAULT 0,
                PRIMARY KEY (usage_date, agent_id, user_id)
            );

            CREATE TABLE IF NOT EXISTS agui_tasks (
                task_id TEXT PRIMARY KEY,
                group_id TEXT NOT NULL,
                agent_id TEXT NOT NULL,
                user_id TEXT NOT NULL,
                topic_id TEXT NOT NULL DEFAULT 'main',
                title TEXT NOT NULL,
                status TEXT NOT NULL,            -- queue / running / finished / failed / cancelled
                progress INTEGER NOT NULL DEFAULT 0,
                content TEXT NOT NULL,           -- 任务指令（触发消息）
                result TEXT,                     -- 完成结果摘要
                error TEXT,                      -- 失败原因
                created_at BIGINT NOT NULL,
                finished_at BIGINT
            );
            CREATE INDEX IF NOT EXISTS idx_tasks_user ON agui_tasks(user_id, created_at);
            CREATE INDEX IF NOT EXISTS idx_tasks_group ON agui_tasks(group_id, created_at);
            """;
        cmd.ExecuteNonQuery();

        // 全文搜索加速：pg_trgm GIN 索引支撑 ILIKE '%kw%'（SearchMessages，PostgresGroupStore）。
        // 扩展 / 索引不可用时静默降级（功能不受影响，仅搜索退化为全表扫描）
        try
        {
            using (var conn2 = Open())
            using (var cmd2 = conn2.CreateCommand())
            {
                cmd2.CommandText = "CREATE EXTENSION IF NOT EXISTS pg_trgm";
                cmd2.ExecuteNonQuery();
                cmd2.CommandText = "CREATE INDEX IF NOT EXISTS idx_messages_content_trgm ON agui_messages USING gin (content gin_trgm_ops)";
                cmd2.ExecuteNonQuery();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WARN] pg_trgm 索引创建失败（搜索将退化为全表扫描）：{ex.Message}");
        }
    }
}
