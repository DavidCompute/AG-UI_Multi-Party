using MySqlConnector;

namespace AguiGroupChat.Hub.Persistence.Relational;

/// <summary>
/// MySQL 存储（含 MySQL 协议兼容的 TiDB / OceanBase / PolarDB for MySQL / Aurora MySQL）。
/// 用户名大小写不敏感唯一性依赖函数索引 <c>((LOWER(username)))</c>（需 MySQL 8.0.13+ / TiDB 5.0+），
/// 与 PostgreSQL 实现的 LOWER 索引语义一致。索引创建无 IF NOT EXISTS 语法，
/// 经 information_schema 幂等守护，重复启动不报错。
/// </summary>
public sealed class MySqlStore : RelationalStore
{
    public MySqlStore(string connectionString)
        : base(connectionString, () => new MySqlConnection(connectionString), SqlDialect.MySql)
    {
    }

    /// <summary>启动时建表（幂等）：表 IF NOT EXISTS + 索引经 information_schema 守护。</summary>
    public override void EnsureSchema()
    {
        ExecuteScript(TableSql);
        EnsureColumn("agui_groups", "is_private", "ALTER TABLE agui_groups ADD COLUMN is_private TINYINT(1) NOT NULL DEFAULT 0");
        EnsureColumn("agui_users", "personal_memory_enabled", "ALTER TABLE agui_users ADD COLUMN personal_memory_enabled TINYINT(1) NOT NULL DEFAULT 0");
        EnsureColumn("agui_users", "is_admin", "ALTER TABLE agui_users ADD COLUMN is_admin TINYINT(1) NOT NULL DEFAULT 0");
        EnsureColumn("agui_messages", "reasoning", "ALTER TABLE agui_messages ADD COLUMN reasoning MEDIUMTEXT NULL");
        EnsureColumn("agui_messages", "agent_chain", "ALTER TABLE agui_messages ADD COLUMN agent_chain MEDIUMTEXT NULL");
        EnsureIndex("agui_group_members", "idx_members_member",
            "CREATE INDEX idx_members_member ON agui_group_members(member_id)");
        EnsureIndex("agui_topics", "idx_topics_group",
            "CREATE INDEX idx_topics_group ON agui_topics(group_id)");
        EnsureIndex("agui_messages", "idx_messages_group",
            "CREATE INDEX idx_messages_group ON agui_messages(group_id, timestamp)");
        EnsureIndex("agui_messages", "idx_messages_topic",
            "CREATE INDEX idx_messages_topic ON agui_messages(group_id, topic_id, timestamp)");
        EnsureIndex("agui_users", "idx_users_username_ci",
            "CREATE UNIQUE INDEX idx_users_username_ci ON agui_users ((LOWER(username)))");
    }

    /// <summary>旧库迁移：MySQL 不支持 ADD COLUMN IF NOT EXISTS，经 information_schema.columns 检测列存在性（幂等）。</summary>
    private void EnsureColumn(string table, string column, string ddl)
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT COUNT(*) FROM information_schema.columns
                WHERE table_schema = DATABASE() AND table_name = @t AND column_name = @c
                """;
            cmd.AddWithValue("t", table);
            cmd.AddWithValue("c", column);
            if (Convert.ToInt64(cmd.ExecuteScalar()) > 0) return; // 已存在

            cmd.Parameters.Clear();
            cmd.CommandText = ddl;
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[MySqlStore] 列迁移失败（已忽略）: {ex.Message}");
        }
    }

    private void EnsureIndex(string table, string index, string ddl)
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT COUNT(*) FROM information_schema.statistics
                WHERE table_schema = DATABASE() AND table_name = @t AND index_name = @i
                """;
            cmd.AddWithValue("t", table);
            cmd.AddWithValue("i", index);
            if (Convert.ToInt64(cmd.ExecuteScalar()) > 0) return; // 已存在

            cmd.Parameters.Clear();
            cmd.CommandText = ddl;
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            // 低版本 MySQL / 语法不兼容时忽略索引创建（功能不受影响，仅查询性能）
            Console.Error.WriteLine($"[MySqlStore] 索引创建失败（已忽略）: {ex.Message}");
        }
    }

    private const string TableSql = """
        CREATE TABLE IF NOT EXISTS agui_groups (
            group_id VARCHAR(64) PRIMARY KEY,
            group_name VARCHAR(128) NOT NULL,
            group_avatar VARCHAR(512) NULL,
            owner_id VARCHAR(64) NOT NULL,
            member_count INT NOT NULL DEFAULT 0,
            create_time BIGINT NOT NULL,
            extra TEXT NULL,
            is_private TINYINT(1) NOT NULL DEFAULT 0
        ) CHARACTER SET utf8mb4;

        CREATE TABLE IF NOT EXISTS agui_group_members (
            group_id VARCHAR(64) NOT NULL,
            member_id VARCHAR(64) NOT NULL,
            member_type VARCHAR(16) NOT NULL,
            nickname VARCHAR(128) NOT NULL,
            avatar VARCHAR(512) NULL,
            role VARCHAR(16) NOT NULL,
            online_status VARCHAR(16) NOT NULL,
            join_time BIGINT NOT NULL,
            trigger_mode VARCHAR(16) NULL,
            keywords TEXT NULL,
            is_trigger_overridden TINYINT(1) NOT NULL DEFAULT 0,
            extra TEXT NULL,
            PRIMARY KEY (group_id, member_id)
        ) CHARACTER SET utf8mb4;

        CREATE TABLE IF NOT EXISTS agui_topics (
            topic_id VARCHAR(64) PRIMARY KEY,
            group_id VARCHAR(64) NOT NULL,
            name VARCHAR(128) NOT NULL,
            creator_id VARCHAR(64) NOT NULL,
            created_at BIGINT NOT NULL
        ) CHARACTER SET utf8mb4;

        CREATE TABLE IF NOT EXISTS agui_messages (
            message_id VARCHAR(64) PRIMARY KEY,
            group_id VARCHAR(64) NOT NULL,
            topic_id VARCHAR(64) NOT NULL DEFAULT 'main',
            thread_id VARCHAR(64) NOT NULL,
            sender_id VARCHAR(64) NOT NULL,
            sender_type VARCHAR(16) NOT NULL,
            sender_nickname VARCHAR(128) NOT NULL,
            reply_to_message_id VARCHAR(64) NULL,
            mentions TEXT NULL,
            mention_all TINYINT(1) NOT NULL DEFAULT 0,
            visibility VARCHAR(16) NOT NULL DEFAULT 'all',
            visible_member_ids TEXT NULL,
            attachments MEDIUMTEXT NULL,
            content MEDIUMTEXT NOT NULL,
            reasoning MEDIUMTEXT NULL,
            agent_chain MEDIUMTEXT NULL,
            timestamp BIGINT NOT NULL,
            recalled TINYINT(1) NOT NULL DEFAULT 0
        ) CHARACTER SET utf8mb4;

        CREATE TABLE IF NOT EXISTS agui_users (
            user_id VARCHAR(64) PRIMARY KEY,
            username VARCHAR(64) NOT NULL,
            password_hash VARCHAR(128) NOT NULL,
            password_salt VARCHAR(128) NOT NULL,
            nickname VARCHAR(128) NOT NULL DEFAULT '',
            avatar VARCHAR(512) NULL,
            created_at BIGINT NOT NULL,
            updated_at BIGINT NOT NULL,
            personal_memory_enabled TINYINT(1) NOT NULL DEFAULT 0,
            is_admin TINYINT(1) NOT NULL DEFAULT 0
        ) CHARACTER SET utf8mb4;

        CREATE TABLE IF NOT EXISTS agui_agent_registrations (
            agent_id VARCHAR(64) NOT NULL,
            group_id VARCHAR(64) NOT NULL,
            nickname VARCHAR(128) NOT NULL DEFAULT '',
            trigger_mode VARCHAR(16) NOT NULL,
            keywords TEXT NULL,
            is_overridden TINYINT(1) NOT NULL DEFAULT 0,
            PRIMARY KEY (agent_id, group_id)
        ) CHARACTER SET utf8mb4;

        CREATE TABLE IF NOT EXISTS agui_group_reads (
            member_id VARCHAR(64) NOT NULL,
            group_id VARCHAR(64) NOT NULL,
            topic_id VARCHAR(64) NOT NULL,
            read_at BIGINT NOT NULL DEFAULT 0,
            PRIMARY KEY (member_id, group_id, topic_id)
        ) CHARACTER SET utf8mb4;
        CREATE INDEX idx_reads_group ON agui_group_reads(group_id);

        CREATE TABLE IF NOT EXISTS agui_sections (
            name VARCHAR(128) PRIMARY KEY,
            payload MEDIUMTEXT NOT NULL
        ) CHARACTER SET utf8mb4;

        CREATE TABLE IF NOT EXISTS agui_usage (
            usage_date VARCHAR(16) NOT NULL,
            agent_id VARCHAR(128) NOT NULL,
            user_id VARCHAR(128) NOT NULL,
            prompt_tokens BIGINT NOT NULL DEFAULT 0,
            completion_tokens BIGINT NOT NULL DEFAULT 0,
            reasoning_tokens BIGINT NOT NULL DEFAULT 0,
            calls BIGINT NOT NULL DEFAULT 0,
            PRIMARY KEY (usage_date, agent_id, user_id)
        ) CHARACTER SET utf8mb4;

        CREATE TABLE IF NOT EXISTS agui_tasks (
            task_id VARCHAR(64) PRIMARY KEY,
            group_id VARCHAR(64) NOT NULL,
            agent_id VARCHAR(128) NOT NULL,
            user_id VARCHAR(128) NOT NULL,
            topic_id VARCHAR(64) NOT NULL DEFAULT 'main',
            title VARCHAR(512) NOT NULL,
            status VARCHAR(16) NOT NULL,
            progress INT NOT NULL DEFAULT 0,
            content MEDIUMTEXT NOT NULL,
            result MEDIUMTEXT,
            error MEDIUMTEXT,
            created_at BIGINT NOT NULL,
            finished_at BIGINT
        ) CHARACTER SET utf8mb4;
        CREATE INDEX idx_tasks_user ON agui_tasks(user_id, created_at);
        CREATE INDEX idx_tasks_group ON agui_tasks(group_id, created_at);
        """;
}
