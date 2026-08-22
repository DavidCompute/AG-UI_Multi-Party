using AguiGroupChat.Hub.Storage;
using Npgsql;

namespace AguiGroupChat.Hub.Persistence.Postgres;

/// <summary>PostgreSQL 任务存储（agui_tasks）。</summary>
public sealed class PostgresTaskStore : ITaskStore
{
    private readonly PostgresStore _pg;

    public PostgresTaskStore(PostgresStore pg) => _pg = pg;

    public bool Add(WorkTask task)
    {
        try
        {
            using var conn = _pg.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO agui_tasks (task_id, group_id, agent_id, user_id, topic_id, title, status, progress, content, result, error, created_at, finished_at)
                VALUES (@id, @g, @a, @u, @top, @title, @st, @pr, @content, @res, @err, @c, @f)
                """;
            cmd.Parameters.AddWithValue("id", task.TaskId);
            cmd.Parameters.AddWithValue("g", task.GroupId);
            cmd.Parameters.AddWithValue("a", task.AgentId);
            cmd.Parameters.AddWithValue("u", task.UserId);
            cmd.Parameters.AddWithValue("top", task.TopicId);
            cmd.Parameters.AddWithValue("title", task.Title);
            cmd.Parameters.AddWithValue("st", task.Status.ToString().ToLowerInvariant());
            cmd.Parameters.AddWithValue("pr", task.Progress);
            cmd.Parameters.AddWithValue("content", task.Content);
            cmd.Parameters.AddWithValue("res", (object?)task.Result ?? DBNull.Value);
            cmd.Parameters.AddWithValue("err", (object?)task.Error ?? DBNull.Value);
            cmd.Parameters.AddWithValue("c", task.CreatedAt);
            cmd.Parameters.AddWithValue("f", (object?)task.FinishedAt ?? DBNull.Value);
            return cmd.ExecuteNonQuery() > 0;
        }
        catch (PostgresException ex) when (ex.SqlState == "23505") { return false; }
    }

    public WorkTask? Get(string taskId)
    {
        using var conn = _pg.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM agui_tasks WHERE task_id = @id";
        cmd.Parameters.AddWithValue("id", taskId);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadTask(reader) : null;
    }

    public bool Update(WorkTask task)
    {
        using var conn = _pg.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE agui_tasks
            SET status = @st, progress = @pr, result = @res, error = @err, finished_at = @f
            WHERE task_id = @id
            """;
        cmd.Parameters.AddWithValue("st", task.Status.ToString().ToLowerInvariant());
        cmd.Parameters.AddWithValue("pr", task.Progress);
        cmd.Parameters.AddWithValue("res", (object?)task.Result ?? DBNull.Value);
        cmd.Parameters.AddWithValue("err", (object?)task.Error ?? DBNull.Value);
        cmd.Parameters.AddWithValue("f", (object?)task.FinishedAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("id", task.TaskId);
        return cmd.ExecuteNonQuery() > 0;
    }

    public IReadOnlyList<WorkTask> ListForUser(string userId, int limit)
    {
        using var conn = _pg.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM agui_tasks WHERE user_id = @u ORDER BY created_at DESC LIMIT @l";
        cmd.Parameters.AddWithValue("u", userId);
        cmd.Parameters.AddWithValue("l", Math.Min(limit, 100));
        return ReadAll(cmd);
    }

    public IReadOnlyList<WorkTask> ListForGroup(string groupId, int limit)
    {
        using var conn = _pg.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM agui_tasks WHERE group_id = @g ORDER BY created_at DESC LIMIT @l";
        cmd.Parameters.AddWithValue("g", groupId);
        cmd.Parameters.AddWithValue("l", Math.Min(limit, 100));
        return ReadAll(cmd);
    }

    public void ClearAll()
    {
        using var conn = _pg.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM agui_tasks";
        cmd.ExecuteNonQuery();
    }

    private static IReadOnlyList<WorkTask> ReadAll(NpgsqlCommand cmd)
    {
        var list = new List<WorkTask>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) list.Add(ReadTask(reader));
        return list;
    }

    private static WorkTask ReadTask(NpgsqlDataReader r) => new()
    {
        TaskId = r.GetString(0),
        GroupId = r.GetString(1),
        AgentId = r.GetString(2),
        UserId = r.GetString(3),
        TopicId = r.IsDBNull(4) ? "main" : r.GetString(4),
        Title = r.GetString(5),
        Status = WorkTaskStatusExtensions.Parse(r.GetString(6)),
        Progress = r.GetInt32(7),
        Content = r.IsDBNull(8) ? "" : r.GetString(8),
        Result = r.IsDBNull(9) ? null : r.GetString(9),
        Error = r.IsDBNull(10) ? null : r.GetString(10),
        CreatedAt = r.GetInt64(11),
        FinishedAt = r.IsDBNull(12) ? null : r.GetInt64(12),
    };
}
