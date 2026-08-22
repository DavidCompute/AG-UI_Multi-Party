using AguiGroupChat.Hub.Models;
using AguiGroupChat.Hub.Persistence;
using AguiGroupChat.Hub.Persistence.Relational;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>SQLite 存储集成测试：单文件零部署，本机即可运行（每个用例独立临时库文件）。</summary>
[Trait("Category", "Sqlite")]
public sealed class SqliteRelationalStoreTests : RelationalStoreTestsBase
{
    protected override string ProviderName => "sqlite";
    protected override string ProviderConnectionString => $"Data Source={SqliteFile}";
    protected override bool ProviderAvailable => true;
    protected override RelationalStore CreateStore(string connectionString) => new SqliteStore(connectionString);

    protected override void ResetTables(RelationalStore db)
        => db.ExecuteScript("""
            DELETE FROM agui_sections;
            DELETE FROM agui_agent_registrations;
            DELETE FROM agui_users;
            DELETE FROM agui_messages;
            DELETE FROM agui_topics;
            DELETE FROM agui_group_members;
            DELETE FROM agui_groups;
            """);

    /// <summary>回归：sqlite-vec 扩展（vec0）按连接生效，每次开新连接都必须重新加载；
    /// 此前只在 EnsureSchema 的连接上加载，后续 Upsert / Search（新连接）报 no such module: vec0，记忆完全不可用。</summary>
    [Fact]
    public void SqliteVecMemoryStore_UpsertThenSearch_WorksAcrossConnections()
    {
        var dbFile = Path.Combine(Path.GetTempPath(), $"agui-vec-{Guid.NewGuid():N}.db");
        var db = new SqliteStore($"Data Source={dbFile}");
        try
        {
            db.EnsureSchema(); // 建 agui_groups 等基础表（Search 里 LEFT JOIN agui_groups）
            var mem = new SqliteVecMessageMemoryStore(db, dimensions: 8, NullLogger<SqliteVecMessageMemoryStore>.Instance);
            mem.EnsureSchema(); // TryLoadVec0 + 建 agui_message_memory / vec0 表

            var vec = new float[] { 0.1f, 0.2f, 0.3f, 0.4f, 0.5f, 0.6f, 0.7f, 0.8f };
            mem.Upsert(new MessageMemoryRecord(
                MessageId: "m1", GroupId: "g1", TopicId: "main",
                SenderId: "u1", SenderType: "user", Content: "报销需要开发票 SKY-2026",
                Embedding: vec, Timestamp: 1000));

            // Search 走全新连接：若扩展未按连接加载则此处报错（旧实现）
            var hits = mem.Search("g1", agentId: null, vec, topK: 3, minScore: 0.1, scope: "group");
            var hit = Assert.Single(hits);
            Assert.Equal("m1", hit.MessageId);
            Assert.Contains("报销", hit.Content);

            // 再次 Upsert（另一个新连接）也应成功（旧实现在此报 no such module: vec0）
            mem.Upsert(new MessageMemoryRecord(
                MessageId: "m2", GroupId: "g1", TopicId: "main",
                SenderId: "u1", SenderType: "user", Content: "第二条消息",
                Embedding: vec, Timestamp: 2000));
            Assert.Equal(2, mem.Search("g1", null, vec, topK: 5, minScore: 0.0, scope: "group").Count);
        }
        finally
        {
            try { File.Delete(dbFile); } catch { }
        }
    }

    /// <summary>迁移回归：旧版库（无 reasoning 列）经 EnsureSchema 补列后，消息读取正常——
    /// 此前 ReadMessage 按列位置索引，而 ALTER TABLE 追加的列在表末尾，会让 timestamp / recalled 错位。</summary>
    [Fact]
    public void OldSchema_MigrateAddsReasoning_ReadsMessageCorrectly()
    {
        // 用旧版表结构重建 agui_messages（无 reasoning 列），模拟升级前已存在的数据库
        Db.ExecuteScript("""
            DROP TABLE agui_messages;
            CREATE TABLE agui_messages (
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
                timestamp INTEGER NOT NULL,
                recalled INTEGER NOT NULL DEFAULT 0
            );
            """);

        // 应用启动顺序：先 EnsureSchema 迁移（reasoning 被追加到表末尾），之后才允许消息写入
        Db.EnsureSchema();

        // 旧消息（无思考内容）写入 + 读回：列名解析不依赖列位置，各字段必须原样正确
        var old = Msg("m_old", "g1", "user_1", "旧消息内容", ts: 9000);
        Groups.AddMessage(old);
        var loaded = Groups.GetMessage("g1", "m_old")!;
        Assert.Equal("旧消息内容", loaded.Content);
        Assert.Equal(9000, loaded.Timestamp);
        Assert.False(loaded.Recalled);
        Assert.Null(loaded.Reasoning); // 旧消息无思考内容

        // 迁移后的新消息带思考内容：写入 + 读回
        var withReasoning = Msg("m_new", "g1", "agent_a", "正文", ts: 9100);
        withReasoning.Reasoning = "思考过程";
        Groups.AddMessage(withReasoning);
        var loaded2 = Groups.GetMessage("g1", "m_new")!;
        Assert.Equal("思考过程", loaded2.Reasoning);
        Assert.Equal("正文", loaded2.Content);
        Assert.Equal(9100, loaded2.Timestamp);
    }
}
