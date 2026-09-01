using Microsoft.Data.Sqlite;

namespace AguiGroupChat.Hub.Persistence.Relational;

/// <summary>
/// SQLite 存储（单文件零部署）。默认连接串 <c>Data Source=data/agui.sqlite</c>（相对路径由装配层基于内容根解析）。
/// 建表与 PostgreSQL 同构：TEXT 列 + INTEGER 布尔 + 表达式唯一索引（LOWER(username)，SQLite 3.9+）。
/// </summary>
public sealed class SqliteStore : RelationalStore
{
    public SqliteStore(string connectionString)
        : base(AppendPragmas(connectionString), () => new SqliteConnection(AppendPragmas(connectionString)), SqlDialect.Sqlite)
    {
    }

    /// <summary>
    /// 追加 SQLite 运行参数（Microsoft.Data.Sqlite 仅支持有限连接串关键字）：
    /// 忙等待超时用 Default Timeout（单位秒，5000ms = 5s）。
    /// 保持默认连接池开启：文件库与命名共享内存库（mode=memory&cache=shared）依赖池中常驻连接
    /// 维持共享缓存存活，关闭池化会在连接全部关闭后销毁内存库、导致下一次 Open 拿到空库。
    /// WAL 日志模式不支持连接串关键字，改由 <see cref="EnsureSchema"/> 建库时 PRAGMA 开启并随库文件持久化。
    /// </summary>
    private static string AppendPragmas(string connectionString)
    {
        var csb = new SqliteConnectionStringBuilder(connectionString);
        csb.DefaultTimeout = 5; // 忙等待 5000ms（Microsoft.Data.Sqlite 的 Default Timeout 以秒为单位）
        return csb.ToString();
    }

    /// <summary>启动时建表（幂等），并确保数据文件所在目录存在。</summary>
    public override void EnsureSchema()
    {
        EnsureDirectory();
        ExecuteScript(SchemaSql);
        if (!IsMemoryDatabase)
            ExecuteScript("PRAGMA journal_mode = WAL"); // WAL：并发读改写；模式随库文件持久化（内存库不支持 WAL，跳过）
        EnsureColumn("agui_groups", "is_private", "ALTER TABLE agui_groups ADD COLUMN is_private INTEGER NOT NULL DEFAULT 0");
        EnsureColumn("agui_users", "personal_memory_enabled", "ALTER TABLE agui_users ADD COLUMN personal_memory_enabled INTEGER NOT NULL DEFAULT 0");
        EnsureColumn("agui_users", "is_admin", "ALTER TABLE agui_users ADD COLUMN is_admin INTEGER NOT NULL DEFAULT 0");
        EnsureColumn("agui_users", "is_disabled", "ALTER TABLE agui_users ADD COLUMN is_disabled INTEGER NOT NULL DEFAULT 0");
        EnsureColumn("agui_users", "platform_role", "ALTER TABLE agui_users ADD COLUMN platform_role TEXT NOT NULL DEFAULT 'user'");
        EnsureColumn("agui_messages", "reasoning", "ALTER TABLE agui_messages ADD COLUMN reasoning TEXT");
        EnsureColumn("agui_messages", "agent_chain", "ALTER TABLE agui_messages ADD COLUMN agent_chain TEXT");
        EnsureColumn("agui_messages", "plan_json", "ALTER TABLE agui_messages ADD COLUMN plan_json TEXT");
    }

    /// <summary>是否内存库（:memory: 或 file: URI 的 mode=memory），内存库无持久化文件，不适用 WAL 等文件级参数。</summary>
    private bool IsMemoryDatabase => IsMemoryConnectionString(ConnectionString);

    /// <summary>是否内存库连接串（:memory: 或 file: URI 的 mode=memory）。</summary>
    private static bool IsMemoryConnectionString(string connectionString)
    {
        var csb = new SqliteConnectionStringBuilder(connectionString);
        return csb.DataSource == ":memory:" || csb.DataSource == ":"
            || csb.DataSource.Contains("mode=memory", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>旧库迁移：CREATE TABLE IF NOT EXISTS 不修改已有表，缺失列时补列（PRAGMA 检测，幂等）。</summary>
    private void EnsureColumn(string table, string column, string ddl)
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"PRAGMA table_info({table})";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return;
            }
            using var alter = conn.CreateCommand();
            alter.CommandText = ddl;
            alter.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[SqliteStore] 列迁移失败（已忽略）: {ex.Message}");
        }
    }

    private void EnsureDirectory()
    {
        var csb = new SqliteConnectionStringBuilder(ConnectionString);
        if (string.IsNullOrEmpty(csb.DataSource) || csb.DataSource == ":memory:" || csb.DataSource == ":") return;
        var dir = Path.GetDirectoryName(Path.GetFullPath(csb.DataSource));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    }

    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS agui_groups (
            group_id TEXT PRIMARY KEY,
            group_name TEXT NOT NULL,
            group_avatar TEXT,
            owner_id TEXT NOT NULL,
            member_count INTEGER NOT NULL DEFAULT 0,
            create_time INTEGER NOT NULL,
            extra TEXT,
            is_private INTEGER NOT NULL DEFAULT 0
        );

        CREATE TABLE IF NOT EXISTS agui_group_members (
            group_id TEXT NOT NULL,
            member_id TEXT NOT NULL,
            member_type TEXT NOT NULL,
            nickname TEXT NOT NULL,
            avatar TEXT,
            role TEXT NOT NULL,
            online_status TEXT NOT NULL,
            join_time INTEGER NOT NULL,
            trigger_mode TEXT,
            keywords TEXT,
            is_trigger_overridden INTEGER NOT NULL DEFAULT 0,
            extra TEXT,
            PRIMARY KEY (group_id, member_id)
        );
        CREATE INDEX IF NOT EXISTS idx_members_member ON agui_group_members(member_id);

        CREATE TABLE IF NOT EXISTS agui_topics (
            topic_id TEXT PRIMARY KEY,
            group_id TEXT NOT NULL,
            name TEXT NOT NULL,
            creator_id TEXT NOT NULL,
            created_at INTEGER NOT NULL
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
            mention_all INTEGER NOT NULL DEFAULT 0,
            visibility TEXT NOT NULL DEFAULT 'all',
            visible_member_ids TEXT,
            attachments TEXT,
            content TEXT NOT NULL,
            reasoning TEXT,
            agent_chain TEXT,
            plan_json TEXT,
            timestamp INTEGER NOT NULL,
            recalled INTEGER NOT NULL DEFAULT 0
        );
        CREATE INDEX IF NOT EXISTS idx_messages_group ON agui_messages(group_id, timestamp);
        CREATE INDEX IF NOT EXISTS idx_messages_topic ON agui_messages(group_id, topic_id, timestamp);

        CREATE TABLE IF NOT EXISTS agui_users (
            user_id TEXT PRIMARY KEY,
            username TEXT NOT NULL,
            password_hash TEXT NOT NULL,
            password_salt TEXT NOT NULL,
            nickname TEXT NOT NULL DEFAULT '',
            avatar TEXT,
            created_at INTEGER NOT NULL,
            updated_at INTEGER NOT NULL,
            personal_memory_enabled INTEGER NOT NULL DEFAULT 0,
            is_admin INTEGER NOT NULL DEFAULT 0,
            platform_role TEXT NOT NULL DEFAULT 'user'
        );
        CREATE UNIQUE INDEX IF NOT EXISTS idx_users_username_ci ON agui_users (LOWER(username));

        CREATE TABLE IF NOT EXISTS agui_agent_registrations (
            agent_id TEXT NOT NULL,
            group_id TEXT NOT NULL,
            nickname TEXT NOT NULL DEFAULT '',
            trigger_mode TEXT NOT NULL,
            keywords TEXT,
            is_overridden INTEGER NOT NULL DEFAULT 0,
            PRIMARY KEY (agent_id, group_id)
        );

        CREATE TABLE IF NOT EXISTS agui_group_reads (
            member_id TEXT NOT NULL,
            group_id TEXT NOT NULL,
            topic_id TEXT NOT NULL,
            read_at INTEGER NOT NULL DEFAULT 0,
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
            prompt_tokens INTEGER NOT NULL DEFAULT 0,
            completion_tokens INTEGER NOT NULL DEFAULT 0,
            reasoning_tokens INTEGER NOT NULL DEFAULT 0,
            calls INTEGER NOT NULL DEFAULT 0,
            PRIMARY KEY (usage_date, agent_id, user_id)
        );

        CREATE TABLE IF NOT EXISTS agui_tasks (
            task_id TEXT PRIMARY KEY,
            group_id TEXT NOT NULL,
            agent_id TEXT NOT NULL,
            user_id TEXT NOT NULL,
            topic_id TEXT NOT NULL DEFAULT 'main',
            title TEXT NOT NULL,
            status TEXT NOT NULL,
            progress INTEGER NOT NULL DEFAULT 0,
            content TEXT NOT NULL,
            result TEXT,
            error TEXT,
            created_at INTEGER NOT NULL,
            finished_at INTEGER
        );
        CREATE INDEX IF NOT EXISTS idx_tasks_user ON agui_tasks(user_id, created_at);
        CREATE INDEX IF NOT EXISTS idx_tasks_group ON agui_tasks(group_id, created_at);
        """;
}
