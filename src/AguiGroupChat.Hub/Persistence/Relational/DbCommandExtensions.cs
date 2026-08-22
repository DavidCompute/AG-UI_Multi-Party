using System.Data.Common;

namespace AguiGroupChat.Hub.Persistence.Relational;

/// <summary>
/// 跨提供器（MySQL / SQLite）的参数绑定扩展：DbCommand 没有内置 AddWithValue，
/// 这里统一用 CreateParameter 绑定 @ 命名参数，null → DBNull.Value。
/// </summary>
public static class DbCommandExtensions
{
    public static void AddWithValue(this DbCommand cmd, string name, object? value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value ?? DBNull.Value;
        cmd.Parameters.Add(p);
    }
}
