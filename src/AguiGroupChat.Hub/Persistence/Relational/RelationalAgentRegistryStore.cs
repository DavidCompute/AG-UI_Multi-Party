using System.Data.Common;
using AguiGroupChat.Hub.Agents;
using AguiGroupChat.Hub.Models;

namespace AguiGroupChat.Hub.Persistence.Relational;

/// <summary>
/// MySQL / SQLite 共用智能体触发规则存储：群内注册表（agent_id, group_id）复合主键，
/// 随 AgentRegistry 写通（Register / Unregister / UpdateNickname 即时落库），启动时整体加载。
/// </summary>
public sealed class RelationalAgentRegistryStore : IAgentRegistryStore
{
    private readonly RelationalStore _db;

    public RelationalAgentRegistryStore(RelationalStore db) => _db = db;

    public IReadOnlyList<AgentRegistration> LoadAll()
    {
        using var conn = _db.Open();
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
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = _db.Dialect.Upsert(
            "agui_agent_registrations",
            "agent_id, group_id, nickname, trigger_mode, keywords, is_overridden",
            "@aid, @gid, @nick, @tMode, @keywords, @overridden",
            "agent_id, group_id",
            "nickname, trigger_mode, keywords, is_overridden");
        cmd.AddWithValue("aid", registration.AgentId);
        cmd.AddWithValue("gid", registration.GroupId);
        cmd.AddWithValue("nick", registration.Nickname);
        cmd.AddWithValue("tMode", registration.TriggerMode.ToString());
        cmd.AddWithValue("keywords", KeywordsJson(registration.Keywords));
        cmd.AddWithValue("overridden", registration.IsOverridden);
        cmd.ExecuteNonQuery();
    }

    public void Delete(string agentId, string? groupId)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = groupId is null
            ? "DELETE FROM agui_agent_registrations WHERE agent_id = @aid"
            : "DELETE FROM agui_agent_registrations WHERE agent_id = @aid AND group_id = @gid";
        cmd.AddWithValue("aid", agentId);
        if (groupId is not null) cmd.AddWithValue("gid", groupId);
        cmd.ExecuteNonQuery();
    }

    private static IReadOnlyList<string> ReadKeywords(DbDataReader r)
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
