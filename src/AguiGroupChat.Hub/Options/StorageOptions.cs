namespace AguiGroupChat.Hub.Options;

/// <summary>
/// 存储提供器配置：memory（进程内内存，默认；测试与快速启动）或
/// postgres / mysql / sqlite（数据库落盘）。切换数据库时，群组 / 成员 / 话题 / 消息 /
/// 用户 / 智能体规则与定义全部落库，原有的 JSON 单文件快照（Persistence）自动禁用。
///
/// 各提供器连接串示例：
///   postgres: Host=localhost;Port=5432;Database=agui;Username=postgres;Password=***
///   mysql:    Server=localhost;Port=3306;Database=agui;User ID=root;Password=***
///   sqlite:   Data Source=data/agui.sqlite（相对路径基于内容根目录解析；MySQL 8.0.13+ / TiDB 5.0+）
/// </summary>
public sealed class StorageOptions
{
    /// <summary>存储提供器：memory / postgres / mysql / sqlite。</summary>
    public string Provider { get; set; } = "memory";

    /// <summary>数据库连接串（Provider 非 memory 时必填）。</summary>
    public string? ConnectionString { get; set; }

    /// <summary>启动时自动建表（CREATE TABLE IF NOT EXISTS）；关闭后首次启动为 true。</summary>
    public bool AutoCreateSchema { get; set; } = true;
}
