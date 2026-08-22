using AguiGroupChat.Hub.Agents;
using AguiGroupChat.Hub.Models;
using Npgsql;

namespace AguiGroupChat.Hub.Persistence.Postgres;

/// <summary>
/// PostgreSQL 智能体触发规则存储：群内注册表（agent_id, group_id）复合主键，
/// 随 AgentRegistry 写通（Register / Unregister / UpdateNickname 即时落库），启动时整体加载。
/// </summary>
public sealed class PostgresAgentRegistryStore : IAgentRegistryStore
{
    private readonly PostgresStore _pg;

    public PostgresAgentRegistryStore(PostgresStore pg) => _pg = pg;

    public IReadOnlyList<AgentRegistration> LoadAll()
    {
        using var conn = _pg.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT agent_id, nickname, group_id, trigger_mode, keywords, is_overridden
            FROM agui_agent_registrations
            """;
        var list = new List<AgentRegistration>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new AgentRegistration(
                AgentId: reader.GetString(0),
                Nickname: reader.GetString(1),
                GroupId: reader.GetString(2),
                TriggerMode: Enum.Parse<AgentTriggerMode>(reader.GetString(3)),
                Keywords: ReadKeywords(reader),
                IsOverridden: reader.GetBoolean(5)));
        }
        return list;
    }

    public void Upsert(AgentRegistration registration)
    {
        using var conn = _pg.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO agui_agent_registrations (agent_id, group_id, nickname, trigger_mode, keywords, is_overridden)
            VALUES (@aid, @gid, @nick, @tMode, @keywords, @overridden)
            ON CONFLICT (agent_id, group_id) DO UPDATE SET
                nickname = EXCLUDED.nickname,
                trigger_mode = EXCLUDED.trigger_mode,
                keywords = EXCLUDED.keywords,
                is_overridden = EXCLUDED.is_overridden
            """;
        cmd.Parameters.AddWithValue("aid", registration.AgentId);
        cmd.Parameters.AddWithValue("gid", registration.GroupId);
        cmd.Parameters.AddWithValue("nick", registration.Nickname);
        cmd.Parameters.AddWithValue("tMode", registration.TriggerMode.ToString());
        cmd.Parameters.AddWithValue("keywords", KeywordsJson(registration.Keywords));
        cmd.Parameters.AddWithValue("overridden", registration.IsOverridden);
        cmd.ExecuteNonQuery();
    }

    public void Delete(string agentId, string? groupId)
    {
        using var conn = _pg.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = groupId is null
            ? "DELETE FROM agui_agent_registrations WHERE agent_id = @aid"
            : "DELETE FROM agui_agent_registrations WHERE agent_id = @aid AND group_id = @gid";
        cmd.Parameters.AddWithValue("aid", agentId);
        if (groupId is not null) cmd.Parameters.AddWithValue("gid", groupId);
        cmd.ExecuteNonQuery();
    }

    private static IReadOnlyList<string> ReadKeywords(NpgsqlDataReader r)
    {
        if (r.IsDBNull(4)) return [];
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<List<string>>(r.GetString(4)) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static string KeywordsJson(IReadOnlyList<string> keywords)
        => System.Text.Json.JsonSerializer.Serialize(keywords ?? []);
}
