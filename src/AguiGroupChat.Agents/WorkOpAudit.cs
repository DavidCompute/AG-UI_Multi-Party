namespace AguiGroupChat.Agents;

/// <summary>一次工作区操作（<c>work_*</c> 工具调用）的审计记录。</summary>
/// <param name="Tool">工具名，如 <c>work_run_command</c>。</param>
/// <param name="Summary">给用户看的单行摘要（命令原文 / 文件路径 / 退出码 / 耗时）。</param>
/// <param name="Ok">是否成功（被拒绝 / 失败 / 超时为 false）。</param>
/// <param name="DurationMs">耗时（毫秒）。</param>
public sealed record WorkOpRecord(string Tool, string Summary, bool Ok, long DurationMs);

/// <summary>
/// 工作区操作审计采集器（1.0.311）：与 <see cref="ToolResultCollector"/> 同一套路数，
/// 用 <see cref="Ambient"/> 挂到当前异步流上，工作型工具每执行一次就记一条；
/// 运行结束时由网关追加到消息的「执行计划」卡并随 <c>planJson</c> 落库，
/// 使「谁在何时代哪个数字员工执行了什么命令、写了哪些文件」事后可追。
///
/// <para>只记摘要（不含文件正文、不落环境变量），条数上限 40，避免长跑运行把内存撑爆。</para>
/// </summary>
public sealed class WorkOpAudit
{
    private const int MaxOps = 40;

    /// <summary>当前异步流上的审计采集器（未开启采集时为 null）。</summary>
    public static readonly AsyncLocal<WorkOpAudit?> Ambient = new();

    private readonly List<WorkOpRecord> _ops = [];

    /// <summary>已记录的操作（快照，按发生顺序）。</summary>
    public IReadOnlyList<WorkOpRecord> Ops
    {
        get { lock (_ops) return [.. _ops]; }
    }

    public void Add(string tool, string summary, bool ok, long durationMs)
    {
        if (string.IsNullOrWhiteSpace(summary)) return;
        lock (_ops)
        {
            if (_ops.Count >= MaxOps) return;
            _ops.Add(new WorkOpRecord(tool, summary, ok, durationMs));
        }
    }
}
