using AguiGroupChat.Hub.Storage;

namespace AguiGroupChat.Hub.Persistence.Relational;

/// <summary>MySQL / SQLite 共用用量统计存储（agui_usage 聚合行，方言差异隔离在 RelationalStore）。</summary>
public sealed class RelationalUsageStore : IUsageStore
{
    private readonly RelationalStore _db;

    public RelationalUsageStore(RelationalStore db) => _db = db;

    public void RecordUsage(string date, string agentId, string userId, long prompt, long completion, long reasoning)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        // 单条原子 UPSERT：冲突（同一 date+agent+user 已存在）时对 token/calls 做增量累加，
        // 避免旧「先 UPDATE 未命中再 INSERT」在并发写同键时第二个 INSERT 抛主键冲突。
        cmd.CommandText = _db.Dialect.IncrementUpsert(
            "agui_usage",
            "usage_date, agent_id, user_id, prompt_tokens, completion_tokens, reasoning_tokens, calls",
            "@d, @a, @u, @p, @c, @r, 1",
            "usage_date, agent_id, user_id",
            "prompt_tokens, completion_tokens, reasoning_tokens, calls");
        cmd.AddWithValue("p", prompt);
        cmd.AddWithValue("c", completion);
        cmd.AddWithValue("r", reasoning);
        cmd.AddWithValue("d", date);
        cmd.AddWithValue("a", agentId);
        cmd.AddWithValue("u", userId);
        cmd.ExecuteNonQuery();
    }

    public long GetUserUsage(string userId, string date)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COALESCE(SUM(prompt_tokens + completion_tokens + reasoning_tokens), 0)
            FROM agui_usage WHERE user_id = @u AND usage_date = @d
            """;
        cmd.AddWithValue("u", userId);
        cmd.AddWithValue("d", date);
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    public IReadOnlyList<UsageAggregate> GetUsageBetween(string fromDate, string toDate)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT usage_date, agent_id, user_id, prompt_tokens, completion_tokens, reasoning_tokens, calls
            FROM agui_usage
            WHERE usage_date BETWEEN @from AND @to
            ORDER BY usage_date, agent_id, user_id
            """;
        cmd.AddWithValue("from", fromDate);
        cmd.AddWithValue("to", toDate);
        var list = new List<UsageAggregate>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new UsageAggregate(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6)));
        }
        return list;
    }

    public void ClearAll()
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM agui_usage";
        cmd.ExecuteNonQuery();
    }
}
