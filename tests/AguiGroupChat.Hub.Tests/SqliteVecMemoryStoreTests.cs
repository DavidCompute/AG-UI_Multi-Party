using AguiGroupChat.Hub.Models;
using AguiGroupChat.Hub.Persistence;
using AguiGroupChat.Hub.Persistence.Relational;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>SQLite + sqlite-vec 语义记忆存储测试（vec0 扩展缺失时自动走 BLOB 降级路径）。</summary>
public sealed class SqliteVecMemoryStoreTests
{
    private static (SqliteStore Store, SqliteVecMessageMemoryStore Memory) Create(int dims = 8)
    {
        // cache=shared：SQLite :memory: 每个连接独立，必须用共享缓存内存库保证建表/写入/检索同库；
        // 库名唯一：避免并行测试共享同一内存库互相污染
        var store = new SqliteStore($"Data Source=file:sqlitevec-{Guid.NewGuid():N}?mode=memory&cache=shared");
        store.EnsureSchema();
        var memory = new SqliteVecMessageMemoryStore(store, dims, NullLogger<SqliteVecMessageMemoryStore>.Instance);
        memory.EnsureSchema();
        return (store, memory);
    }

    /// <summary>测试用 SQL 直插群 / 成员元数据（私密隔离过滤依赖这两张表）。</summary>
    private static void AddGroup(SqliteStore store, string groupId, bool isPrivate)
    {
        using var conn = store.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO agui_groups (group_id, group_name, owner_id, member_count, create_time, is_private) VALUES (@id, @n, 'owner', 0, 1, @p)";
        cmd.AddWithValue("id", groupId);
        cmd.AddWithValue("n", groupId);
        cmd.AddWithValue("p", isPrivate ? 1 : 0);
        cmd.ExecuteNonQuery();
    }

    private static void AddMember(SqliteStore store, string groupId, string memberId)
    {
        using var conn = store.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO agui_group_members (group_id, member_id, member_type, nickname, role, online_status, join_time) VALUES (@g, @m, 'agent', @m, 'normal', 'online', 1)";
        cmd.AddWithValue("g", groupId);
        cmd.AddWithValue("m", memberId);
        cmd.ExecuteNonQuery();
    }

    private static MessageMemoryRecord Rec(string id, string group, string sender, string content, float[] v, long ts = 1000)
        => new(id, group, "main", sender, "user", content, v, ts);

    [Fact]
    public void Upsert_Search_ReturnsSimilarHit_SortedByScore()
    {
        var (store, memory) = Create();
        memory.Upsert(Rec("m1", "g1", "u1", "今天天气很好", [1, 0, 0, 0, 0, 0, 0, 0]));
        memory.Upsert(Rec("m2", "g1", "u1", "完全无关内容", [0, 1, 0, 0, 0, 0, 0, 0]));
        memory.Upsert(Rec("m3", "g2", "u2", "另一条天气相关", [0.9f, 0.1f, 0, 0, 0, 0, 0, 0]));

        // scope=all：跨群检索，m1 相似度最高
        var hits = memory.Search("g1", null, [1, 0, 0, 0, 0, 0, 0, 0], topK: 5, minScore: 0.5, scope: "all");
        Assert.Contains(hits, h => h.MessageId == "m1");
        Assert.Contains(hits, h => h.MessageId == "m3");
        Assert.DoesNotContain(hits, h => h.MessageId == "m2");
        Assert.True(hits[0].MessageId == "m1", "相似度最高的应排第一");
        Assert.True(hits[0].Score >= hits[1].Score);
    }

    [Fact]
    public void Search_ScopeAgent_OnlyGroupsWhereAgentIsMember()
    {
        var (store, memory) = Create();
        memory.Upsert(Rec("m1", "g1", "u1", "群1的内容", [1, 0, 0, 0, 0, 0, 0, 0]));
        memory.Upsert(Rec("m2", "g2", "u1", "群2的内容", [0.9f, 0.1f, 0, 0, 0, 0, 0, 0]));
        AddMember(store, "g1", "agent_a");

        // agent 只属于 g1：scope=agent 只命中 g1 的记忆
        var hits = memory.Search("g3", "agent_a", [1, 0, 0, 0, 0, 0, 0, 0], topK: 5, minScore: 0.5, scope: "agent");
        Assert.Contains(hits, h => h.MessageId == "m1");
        Assert.DoesNotContain(hits, h => h.MessageId == "m2");
    }

    [Fact]
    public void Search_PrivateGroupMemory_Isolated_OutsideCurrentGroup()
    {
        var (store, memory) = Create();
        memory.Upsert(Rec("m_priv", "g_priv", "u1", "私密群内容", [1, 0, 0, 0, 0, 0, 0, 0]));
        memory.Upsert(Rec("m_pub", "g_pub", "u1", "公开群内容", [0.9f, 0.1f, 0, 0, 0, 0, 0, 0]));
        AddGroup(store, "g_priv", isPrivate: true);
        AddGroup(store, "g_pub", isPrivate: false);
        AddMember(store, "g_pub", "agent_a");
        AddMember(store, "g_priv", "agent_a");

        // 在公开群触发（scope=agent）：私密群记忆被隔离
        var hits = memory.Search("g_pub", "agent_a", [1, 0, 0, 0, 0, 0, 0, 0], topK: 5, minScore: 0.5, scope: "agent");
        Assert.Contains(hits, h => h.MessageId == "m_pub");
        Assert.DoesNotContain(hits, h => h.MessageId == "m_priv");

        // 在私密群本群内：可命中自己的私密记忆
        var inPriv = memory.Search("g_priv", "agent_a", [1, 0, 0, 0, 0, 0, 0, 0], topK: 5, minScore: 0.5, scope: "agent");
        Assert.Contains(inPriv, h => h.MessageId == "m_priv");
    }

    [Fact]
    public void SearchPerson_FiltersBySender_AndIsolatesPrivateGroups()
    {
        var (store, memory) = Create();
        memory.Upsert(Rec("m_u1_pub", "g_pub", "u1", "u1 公开发言", [1, 0, 0, 0, 0, 0, 0, 0]));
        memory.Upsert(Rec("m_u2", "g_pub", "u2", "u2 的发言", [0.9f, 0.1f, 0, 0, 0, 0, 0, 0]));
        memory.Upsert(Rec("m_u1_priv", "g_priv", "u1", "u1 私密发言", [0.95f, 0.05f, 0, 0, 0, 0, 0, 0]));
        AddGroup(store, "g_priv", isPrivate: true);
        AddGroup(store, "g_pub", isPrivate: false);

        // 在公开群检索 u1 的个人记忆：私密群的 u1 发言被隔离
        var hits = memory.SearchPerson("u1", "g_pub", [1, 0, 0, 0, 0, 0, 0, 0], topK: 5, minScore: 0.5);
        Assert.Contains(hits, h => h.MessageId == "m_u1_pub");
        Assert.DoesNotContain(hits, h => h.MessageId == "m_u2");
        Assert.DoesNotContain(hits, h => h.MessageId == "m_u1_priv");

        // 在私密群本群内：可命中本人私密发言
        var inPriv = memory.SearchPerson("u1", "g_priv", [1, 0, 0, 0, 0, 0, 0, 0], topK: 5, minScore: 0.5);
        Assert.Contains(inPriv, h => h.MessageId == "m_u1_priv");
    }

    [Fact]
    public void RemoveGroup_DeletesAllMemories()
    {
        var (store, memory) = Create();
        memory.Upsert(Rec("m1", "g1", "u1", "内容", [1, 0, 0, 0, 0, 0, 0, 0]));
        memory.Upsert(Rec("m2", "g1", "u1", "内容2", [0.9f, 0.1f, 0, 0, 0, 0, 0, 0]));
        memory.RemoveGroup("g1");

        var hits = memory.Search("g1", null, [1, 0, 0, 0, 0, 0, 0, 0], topK: 5, minScore: 0.1, scope: "all");
        Assert.Empty(hits);
    }
}
