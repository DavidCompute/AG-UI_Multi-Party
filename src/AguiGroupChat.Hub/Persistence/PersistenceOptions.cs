namespace AguiGroupChat.Hub.Persistence;

/// <summary>持久化配置（appsettings.json 的 Persistence 节点）。</summary>
public sealed class PersistenceOptions
{
    /// <summary>是否启用持久化。false 时保持纯内存模式。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 状态文件路径（相对内容根目录或绝对路径），如 data/agui-state.json。
    /// 为空时禁用持久化。
    /// </summary>
    public string? FilePath { get; set; } = "data/agui-state.json";

    /// <summary>后台落盘间隔（秒）。变更后由该定时器合并写入。</summary>
    public int FlushIntervalSeconds { get; set; } = 5;
}
