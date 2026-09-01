using System.Data.Common;
using AguiGroupChat.Hub.Models;
using AguiGroupChat.Hub.Users;

namespace AguiGroupChat.Hub.Persistence.Relational;

/// <summary>
/// MySQL / SQLite 共用用户账号存储：userId 主键 + username 唯一（大小写不敏感，与内存 / PostgreSQL 实现一致）。
/// 密码哈希 / 盐以文本列存储（Base64），不参与任何查询比较。
/// </summary>
public sealed class RelationalUserStore : IUserStore
{
    private readonly RelationalStore _db;

    public RelationalUserStore(RelationalStore db) => _db = db;

    public bool AddUser(UserAccount user)
    {
        try
        {
            using var conn = _db.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO agui_users (user_id, username, password_hash, password_salt, nickname, avatar, created_at, updated_at, personal_memory_enabled, is_admin, is_disabled, platform_role)
                VALUES (@uid, @username, @hash, @salt, @nick, @avatar, @created, @updated, @personal, @isAdmin, @isDisabled, @platformRole)
                """;
            cmd.AddWithValue("uid", user.UserId);
            cmd.AddWithValue("username", user.Username);
            cmd.AddWithValue("hash", user.PasswordHash);
            cmd.AddWithValue("salt", user.PasswordSalt);
            cmd.AddWithValue("nick", user.Nickname);
            cmd.AddWithValue("avatar", (object?)user.Avatar ?? DBNull.Value);
            cmd.AddWithValue("created", user.CreatedAt);
            cmd.AddWithValue("updated", user.UpdatedAt);
            cmd.AddWithValue("personal", user.PersonalMemoryEnabled);
            cmd.AddWithValue("isAdmin", user.IsAdmin);
            cmd.AddWithValue("isDisabled", user.IsDisabled);
            cmd.AddWithValue("platformRole", UserRoleToString(user.PlatformRole));
            cmd.ExecuteNonQuery();
            return true;
        }
        catch (Exception ex) when (_db.IsDuplicate(ex))
        {
            // user_id 或用户名（含大小写不敏感唯一索引）冲突
            return false;
        }
    }

    public UserAccount? GetUserById(string userId)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT user_id, username, password_hash, password_salt, nickname, avatar, created_at, updated_at, personal_memory_enabled, is_admin, is_disabled, platform_role FROM agui_users WHERE user_id = @uid";
        cmd.AddWithValue("uid", userId);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadUser(reader) : null;
    }

    public UserAccount? GetUserByUsername(string username)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT user_id, username, password_hash, password_salt, nickname, avatar, created_at, updated_at, personal_memory_enabled, is_admin, is_disabled, platform_role FROM agui_users WHERE LOWER(username) = LOWER(@username)";
        cmd.AddWithValue("username", username);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadUser(reader) : null;
    }

    public bool UpdateUser(UserAccount user)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE agui_users
            SET password_hash = @hash, password_salt = @salt, nickname = @nick, avatar = @avatar,
                personal_memory_enabled = @personal, is_admin = @isAdmin, is_disabled = @isDisabled,
                platform_role = @platformRole, updated_at = @updated
            WHERE user_id = @uid
            """;
        cmd.AddWithValue("hash", user.PasswordHash);
        cmd.AddWithValue("salt", user.PasswordSalt);
        cmd.AddWithValue("nick", user.Nickname);
        cmd.AddWithValue("avatar", (object?)user.Avatar ?? DBNull.Value);
        cmd.AddWithValue("personal", user.PersonalMemoryEnabled);
        cmd.AddWithValue("isAdmin", user.IsAdmin);
        cmd.AddWithValue("isDisabled", user.IsDisabled);
        cmd.AddWithValue("platformRole", UserRoleToString(user.PlatformRole));
        cmd.AddWithValue("updated", user.UpdatedAt);
        cmd.AddWithValue("uid", user.UserId);
        return cmd.ExecuteNonQuery() > 0;
    }

    public IReadOnlyList<UserAccount> ListUsers()
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT user_id, username, password_hash, password_salt, nickname, avatar, created_at, updated_at, personal_memory_enabled, is_admin, is_disabled, platform_role FROM agui_users ORDER BY created_at";
        var list = new List<UserAccount>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) list.Add(ReadUser(reader));
        return list;
    }

    public void ClearAll()
        => _db.ExecuteScript("DELETE FROM agui_users");

    private static UserAccount ReadUser(DbDataReader r) => new()
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
        PlatformRole = ReadPlatformRole(r),
    };

    /// <summary>读取平台角色列（旧表无该列时按 IsAdmin 推导为 Admin，兼容迁移前窗口）。</summary>
    private static PlatformRole ReadPlatformRole(DbDataReader r)
    {
        try
        {
            var idx = r.GetOrdinal("platform_role");
            if (idx < 0 || r.IsDBNull(idx)) return PlatformRole.User;
            var s = r.GetString(idx);
            if (Enum.TryParse<PlatformRole>(s, true, out var role)) return role;
        }
        catch (Exception ex) when (ex is InvalidCastException or IndexOutOfRangeException or System.ArgumentOutOfRangeException) { }
        var adminIdx = r.GetOrdinal("is_admin");
        return adminIdx >= 0 && !r.IsDBNull(adminIdx) && r.GetBoolean(adminIdx) ? PlatformRole.Admin : PlatformRole.User;
    }

    /// <summary>平台角色 → 存库字符串（user / operator / admin / superadmin）。</summary>
    private static string UserRoleToString(PlatformRole role) => role switch
    {
        PlatformRole.Operator => "operator",
        PlatformRole.Admin => "admin",
        PlatformRole.SuperAdmin => "superadmin",
        _ => "user",
    };
}
