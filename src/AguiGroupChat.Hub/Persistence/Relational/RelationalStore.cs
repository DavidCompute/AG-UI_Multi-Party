using System.Data.Common;

namespace AguiGroupChat.Hub.Persistence.Relational;

/// <summary>
/// MySQL / SQLite 共用关系库基础设施：连接工厂 + 多语句脚本执行（建表 / 清库）。
/// 两个提供器共用同一套 <see cref="RelationalGroupStore"/> / <see cref="RelationalUserStore"/> /
/// <see cref="RelationalAgentRegistryStore"/> / <see cref="RelationalSectionStore"/> 实现
/// （基于 DbConnection / DbDataReader 编程），差异仅在于建表 DDL 与方言（<see cref="SqlDialect"/>）。
/// </summary>
public abstract class RelationalStore
{
    private readonly Func<DbConnection> _factory;

    public string ConnectionString { get; }
    public SqlDialect Dialect { get; }

    protected RelationalStore(string connectionString, Func<DbConnection> factory, SqlDialect dialect)
    {
        ConnectionString = connectionString;
        _factory = factory;
        Dialect = dialect;
    }

    public DbConnection Open()
    {
        var conn = _factory();
        try
        {
            conn.Open();
        }
        catch
        {
            conn.Dispose(); // 连接失败时释放句柄，避免泄漏
            throw;
        }
        return conn;
    }

    public bool IsDuplicate(Exception ex) => Dialect.IsDuplicate(ex);

    /// <summary>启动时建表（幂等，实现见各提供器）。</summary>
    public abstract void EnsureSchema();

    /// <summary>按「;」拆分执行多语句脚本（建表 / 清库等 DDL，语句内不含分号）。</summary>
    public void ExecuteScript(string script)
    {
        using var conn = Open();
        foreach (var statement in SplitStatements(script))
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = statement;
            cmd.ExecuteNonQuery();
        }
    }

    internal static IEnumerable<string> SplitStatements(string script)
    {
        foreach (var part in script.Split(';'))
        {
            var s = part.Trim();
            if (s.Length > 0) yield return s;
        }
    }
}
