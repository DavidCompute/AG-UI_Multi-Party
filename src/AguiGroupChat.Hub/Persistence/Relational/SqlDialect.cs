using Microsoft.Data.Sqlite;
using MySqlConnector;

namespace AguiGroupChat.Hub.Persistence.Relational;

/// <summary>
/// MySQL / SQLite 轻量关系库方言：只封装 UPSERT 语法与重复键异常判定这两处差异。
/// 其余 SQL（LIMIT 分页、@ 命名参数、JSON/TEXT 列、BIGINT 时间戳）两种方言与 Npgsql 通用，
/// 故 MySQL / SQLite 共用同一套 <see cref="RelationalGroupStore"/> 等实现。
/// </summary>
public sealed class SqlDialect
{
    public static readonly SqlDialect MySql = new("mysql");
    public static readonly SqlDialect Sqlite = new("sqlite");

    private readonly bool _mysql;

    private SqlDialect(string name) => _mysql = name == "mysql";

    /// <summary>
    /// 构造 UPSERT：MySQL 用 <c>INSERT ... VALUES (...) AS new ON DUPLICATE KEY UPDATE ... new.col</c>
    /// （8.0.19+ 行别名语法，VALUES(col) 已弃用，兼容 TiDB / OceanBase），
    /// SQLite 用 <c>ON CONFLICT (pk) DO UPDATE SET ... excluded.col</c>（与 PostgreSQL 同语法）。
    /// </summary>
    public string Upsert(string table, string columns, string values, string conflictColumns, string setColumns)
    {
        var sets = string.Join(", ",
            setColumns.Split(',').Select(c => c.Trim()).Select(c =>
                _mysql ? $"{c} = new.{c}" : $"{c} = excluded.{c}"));
        return _mysql
            ? $"INSERT INTO {table} ({columns}) VALUES ({values}) AS new ON DUPLICATE KEY UPDATE {sets}"
            : $"INSERT INTO {table} ({columns}) VALUES ({values}) ON CONFLICT ({conflictColumns}) DO UPDATE SET {sets}";
    }

    /// <summary>重复键冲突判定：MySQL 错误号 1062（ER_DUP_ENTRY）；SQLite 约束错误（SQLITE_CONSTRAINT = 19）。</summary>
    public bool IsDuplicate(Exception ex)
        => ex is MySqlException { Number: 1062 }
           || ex is SqliteException { SqliteErrorCode: 19 };

    /// <summary>
    /// 构造「只前进不回退」的 UPSERT（已读位点等单调字段用）：冲突时仅当新值更大才覆盖，
    /// 防并发下旧位点写回导致已读回退。MySQL 用 GREATEST + 行别名 new，SQLite 用 MAX + excluded（两方言语法不同）。
    /// </summary>
    public string MonotonicUpsert(string table, string columns, string values, string conflictColumns, string setColumn)
    {
        return _mysql
            ? $"INSERT INTO {table} ({columns}) VALUES ({values}) AS new ON DUPLICATE KEY UPDATE {setColumn} = GREATEST({table}.{setColumn}, new.{setColumn})"
            : $"INSERT INTO {table} ({columns}) VALUES ({values}) ON CONFLICT ({conflictColumns}) DO UPDATE SET {setColumn} = MAX({table}.{setColumn}, excluded.{setColumn})";
    }
}
