using AguiGroupChat.Hub.Infra;

namespace AguiGroupChat.Hub.Persistence.Postgres;

/// <summary>审计日志 PostgreSQL 独立表存储：表 <c>agui_audit</c>（EnsureSchema 幂等建表）。</summary>
public sealed class PostgresAuditStore : IAuditStore
{
    private readonly PostgresStore _pg;

    public PostgresAuditStore(PostgresStore pg) => _pg = pg;

    public void Append(AuditEntry entry)
    {
        try
        {
            using var conn = _pg.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO agui_audit (id, ts, action, actor_id, actor_username, group_id, target_type, target_id, detail, result)
                VALUES (@id, @ts, @action, @actorId, @actorUsername, @groupId, @targetType, @targetId, @detail, @result)
                ON CONFLICT (id) DO NOTHING
                """;
            cmd.Parameters.AddWithValue("id", entry.Id);
            cmd.Parameters.AddWithValue("ts", entry.Timestamp);
            cmd.Parameters.AddWithValue("action", entry.Action);
            cmd.Parameters.AddWithValue("actorId", entry.ActorId);
            cmd.Parameters.AddWithValue("actorUsername", entry.ActorUsername);
            cmd.Parameters.AddWithValue("groupId", (object?)entry.GroupId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("targetType", (object?)entry.TargetType ?? DBNull.Value);
            cmd.Parameters.AddWithValue("targetId", (object?)entry.TargetId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("detail", (object?)entry.Detail ?? DBNull.Value);
            cmd.Parameters.AddWithValue("result", entry.Result);
            cmd.ExecuteNonQuery();
        }
        catch (Npgsql.PostgresException ex) when (ex.SqlState == "23505") { /* id 冲突（幂等重放）静默忽略 */ }
    }

    public IReadOnlyList<AuditEntry> Query(int limit, string? actor = null, string? action = null,
        string? targetId = null, long? fromMs = null, long? toMs = null)
    {
        using var conn = _pg.Open();
        using var cmd = conn.CreateCommand();
        var where = BuildWhere(cmd, actor, action, targetId, fromMs, toMs);
        cmd.CommandText = $"""
            SELECT id, ts, action, actor_id, actor_username, group_id, target_type, target_id, detail, result
            FROM agui_audit WHERE {where}
            ORDER BY ts DESC, id DESC LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("limit", Math.Max(1, limit));
        return ReadAll(cmd);
    }

    public IReadOnlyList<AuditEntry> QueryAll(string? actor = null, string? action = null,
        string? targetId = null, long? fromMs = null, long? toMs = null)
    {
        using var conn = _pg.Open();
        using var cmd = conn.CreateCommand();
        var where = BuildWhere(cmd, actor, action, targetId, fromMs, toMs);
        cmd.CommandText = $"""
            SELECT id, ts, action, actor_id, actor_username, group_id, target_type, target_id, detail, result
            FROM agui_audit WHERE {where}
            ORDER BY ts ASC, id ASC
            """;
        return ReadAll(cmd);
    }

    public long Count()
    {
        using var conn = _pg.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM agui_audit";
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0);
    }

    public int Prune(int keepLatest)
    {
        using var conn = _pg.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            DELETE FROM agui_audit
            WHERE id NOT IN (
                SELECT id FROM (
                    SELECT id FROM agui_audit ORDER BY ts DESC, id DESC LIMIT @keep
                ) keep
            )
            """;
        cmd.Parameters.AddWithValue("keep", Math.Max(0, keepLatest));
        return cmd.ExecuteNonQuery();
    }

    public void Clear()
    {
        using var conn = _pg.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM agui_audit";
        cmd.ExecuteNonQuery();
    }

    private static string BuildWhere(Npgsql.NpgsqlCommand cmd, string? actor, string? action,
        string? targetId, long? fromMs, long? toMs)
    {
        var groups = new List<string>();
        string Like(string column)
        {
            cmd.Parameters.AddWithValue($"p_{column.Replace("_", "")}", $"%{(column == "action" ? action : column == "target_id" ? targetId : actor)}%");
            return $"LOWER({column}) LIKE LOWER(@p_{column.Replace("_", "")})";
        }
        if (!string.IsNullOrWhiteSpace(actor)) groups.Add($"({Like("actor_id")} OR {Like("actor_username")})");
        if (!string.IsNullOrWhiteSpace(action)) groups.Add(Like("action"));
        if (!string.IsNullOrWhiteSpace(targetId)) groups.Add(Like("target_id"));
        if (fromMs is not null) { groups.Add("ts >= @from"); cmd.Parameters.AddWithValue("from", fromMs.Value); }
        if (toMs is not null) { groups.Add("ts <= @to"); cmd.Parameters.AddWithValue("to", toMs.Value); }
        return groups.Count == 0 ? "TRUE" : string.Join(" AND ", groups);
    }

    private static List<AuditEntry> ReadAll(Npgsql.NpgsqlCommand cmd)
    {
        var list = new List<AuditEntry>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new AuditEntry
            {
                Id = reader.GetString(0),
                Timestamp = reader.GetInt64(1),
                Action = reader.GetString(2),
                ActorId = reader.GetString(3),
                ActorUsername = reader.GetString(4),
                GroupId = reader.IsDBNull(5) ? null : reader.GetString(5),
                TargetType = reader.IsDBNull(6) ? null : reader.GetString(6),
                TargetId = reader.IsDBNull(7) ? null : reader.GetString(7),
                Detail = reader.IsDBNull(8) ? null : reader.GetString(8),
                Result = reader.IsDBNull(9) ? "ok" : reader.GetString(9),
            });
        }
        return list;
    }
}
