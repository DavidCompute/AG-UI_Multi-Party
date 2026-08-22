using AguiGroupChat.Hub.Users;
using Npgsql;

namespace AguiGroupChat.Hub.Persistence.Postgres;

/// <summary>
/// PostgreSQL 用户账号存储：userId 主键 + username 唯一（大小写不敏感，与内存实现一致）。
/// 密码哈希 / 盐以 TEXT 存储（Base64），不参与任何查询比较。
/// </summary>
public sealed class PostgresUserStore : IUserStore
{
    private readonly PostgresStore _pg;

    public PostgresUserStore(PostgresStore pg) => _pg = pg;

    public bool AddUser(UserAccount user)
    {
        try
        {
            using var conn = _pg.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO agui_users (user_id, username, password_hash, password_salt, nickname, avatar, created_at, updated_at, personal_memory_enabled, is_admin, is_disabled)
                VALUES (@uid, @username, @hash, @salt, @nick, @avatar, @created, @updated, @personal, @isAdmin, @isDisabled)
                """;
            cmd.Parameters.AddWithValue("uid", user.UserId);
            cmd.Parameters.AddWithValue("username", user.Username);
            cmd.Parameters.AddWithValue("hash", user.PasswordHash);
            cmd.Parameters.AddWithValue("salt", user.PasswordSalt);
            cmd.Parameters.AddWithValue("nick", user.Nickname);
            cmd.Parameters.AddWithValue("avatar", (object?)user.Avatar ?? DBNull.Value);
            cmd.Parameters.AddWithValue("created", user.CreatedAt);
            cmd.Parameters.AddWithValue("updated", user.UpdatedAt);
            cmd.Parameters.AddWithValue("personal", user.PersonalMemoryEnabled);
            cmd.Parameters.AddWithValue("isAdmin", user.IsAdmin);
            cmd.Parameters.AddWithValue("isDisabled", user.IsDisabled);
            cmd.ExecuteNonQuery();
            return true;
        }
        catch (PostgresException ex) when (ex.SqlState == "23505")
        {
            // user_id 或用户名（含大小写不敏感唯一索引）冲突
            return false;
        }
    }

    public UserAccount? GetUserById(string userId)
    {
        using var conn = _pg.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM agui_users WHERE user_id = @uid";
        cmd.Parameters.AddWithValue("uid", userId);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadUser(reader) : null;
    }

    public UserAccount? GetUserByUsername(string username)
    {
        using var conn = _pg.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM agui_users WHERE LOWER(username) = LOWER(@username)";
        cmd.Parameters.AddWithValue("username", username);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadUser(reader) : null;
    }

    public bool UpdateUser(UserAccount user)
    {
        using var conn = _pg.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE agui_users
            SET password_hash = @hash, password_salt = @salt, nickname = @nick, avatar = @avatar,
                personal_memory_enabled = @personal, is_admin = @isAdmin, is_disabled = @isDisabled, updated_at = @updated
            WHERE user_id = @uid
            """;
        cmd.Parameters.AddWithValue("hash", user.PasswordHash);
        cmd.Parameters.AddWithValue("salt", user.PasswordSalt);
        cmd.Parameters.AddWithValue("nick", user.Nickname);
        cmd.Parameters.AddWithValue("avatar", (object?)user.Avatar ?? DBNull.Value);
        cmd.Parameters.AddWithValue("personal", user.PersonalMemoryEnabled);
        cmd.Parameters.AddWithValue("isAdmin", user.IsAdmin);
        cmd.Parameters.AddWithValue("isDisabled", user.IsDisabled);
        cmd.Parameters.AddWithValue("updated", user.UpdatedAt);
        cmd.Parameters.AddWithValue("uid", user.UserId);
        return cmd.ExecuteNonQuery() > 0;
    }

    public IReadOnlyList<UserAccount> ListUsers()
    {
        using var conn = _pg.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM agui_users ORDER BY created_at";
        var list = new List<UserAccount>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) list.Add(ReadUser(reader));
        return list;
    }

    public void ClearAll()
    {
        using var conn = _pg.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM agui_users";
        cmd.ExecuteNonQuery();
    }

    private static UserAccount ReadUser(NpgsqlDataReader r) => new()
    {
        UserId = r.GetString(0),
        Username = r.GetString(1),
        PasswordHash = r.GetString(2),
        PasswordSalt = r.GetString(3),
        Nickname = r.GetString(4),
        Avatar = r.IsDBNull(5) ? null : r.GetString(5),
        CreatedAt = r.GetInt64(6),
        UpdatedAt = r.GetInt64(7),
        PersonalMemoryEnabled = r.IsDBNull(8) ? false : r.GetBoolean(8),
        IsAdmin = r.IsDBNull(9) ? false : r.GetBoolean(9),
        IsDisabled = r.IsDBNull(10) ? false : r.GetBoolean(10),
    };
}
