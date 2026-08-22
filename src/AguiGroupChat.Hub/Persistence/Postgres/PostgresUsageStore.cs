using AguiGroupChat.Hub.Storage;
using Npgsql;

namespace AguiGroupChat.Hub.Persistence.Postgres;

/// <summary>PostgreSQL 用量统计存储：agui_usage 按「日期 + 智能体 + 触发者」聚合行，调用时 ON CONFLICT 累加。</summary>
public sealed class PostgresUsageStore : IUsageStore
{
    private readonly PostgresStore _pg;

    public PostgresUsageStore(PostgresStore pg) => _pg = pg;

    public void RecordUsage(string date, string agentId, string userId, long prompt, long completion, long reasoning)
    {
        using var conn = _pg.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO agui_usage (usage_date, agent_id, user_id, prompt_tokens, completion_tokens, reasoning_tokens, calls)
            VALUES (@d, @a, @u, @p, @c, @r, 1)
            ON CONFLICT (usage_date, agent_id, user_id)
            DO UPDATE SET
                prompt_tokens = agui_usage.prompt_tokens + EXCLUDED.prompt_tokens,
                completion_tokens = agui_usage.completion_tokens + EXCLUDED.completion_tokens,
                reasoning_tokens = agui_usage.reasoning_tokens + EXCLUDED.reasoning_tokens,
                calls = agui_usage.calls + 1
            """;
        cmd.Parameters.AddWithValue("d", date);
        cmd.Parameters.AddWithValue("a", agentId);
        cmd.Parameters.AddWithValue("u", userId);
        cmd.Parameters.AddWithValue("p", prompt);
        cmd.Parameters.AddWithValue("c", completion);
        cmd.Parameters.AddWithValue("r", reasoning);
        cmd.ExecuteNonQuery();
    }

    public long GetUserUsage(string userId, string date)
    {
        using var conn = _pg.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COALESCE(SUM(prompt_tokens + completion_tokens + reasoning_tokens), 0)
            FROM agui_usage WHERE user_id = @u AND usage_date = @d
            """;
        cmd.Parameters.AddWithValue("u", userId);
        cmd.Parameters.AddWithValue("d", date);
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    public IReadOnlyList<UsageAggregate> GetUsageBetween(string fromDate, string toDate)
    {
        using var conn = _pg.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT usage_date, agent_id, user_id, prompt_tokens, completion_tokens, reasoning_tokens, calls
            FROM agui_usage
            WHERE usage_date BETWEEN @from AND @to
            ORDER BY usage_date, agent_id, user_id
            """;
        cmd.Parameters.AddWithValue("from", fromDate);
        cmd.Parameters.AddWithValue("to", toDate);
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
        using var conn = _pg.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM agui_usage";
        cmd.ExecuteNonQuery();
    }
}
