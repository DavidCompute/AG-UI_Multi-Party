namespace AguiGroupChat.Hub.Storage;

/// <summary>
/// 一条工作区操作审计（1.0.315）：由 <c>GroupHub.AppendMessagePlanAuditAsync</c> 在落库时同步给
/// <see cref="IWorkAuditSink"/>，供上层做归档 / 检索（不再依赖逐条翻聊天记录解析 <c>planJson</c>）。
/// </summary>
/// <param name="GroupId">所属知聚。</param>
/// <param name="GroupName">知聚名（归档时冗余，避免历史知聚解散后无法辨认）。</param>
/// <param name="AgentId">执行操作的数字员工（原始 id，供筛选）。</param>
/// <param name="AgentName">数字员工昵称（供展示；历史员工已删时为空）。</param>
/// <param name="MessageId">产生这条审计的回答消息。</param>
/// <param name="TopicId">话题。</param>
/// <param name="At">消息时间戳（毫秒）。</param>
/// <param name="Tool">工具名：work_run_command / work_write_file / work_read_file / work_list_dir。</param>
/// <param name="Ok">是否成功（被拒 / 失败 / 超时为 false）。</param>
/// <param name="DurationMs">耗时（毫秒）。</param>
/// <param name="Text">单行摘要（命令原文 / 路径 / 退出码 / 耗时）。</param>
public sealed record WorkAuditEntry(
    string GroupId,
    string GroupName,
    string AgentId,
    string AgentName,
    string MessageId,
    string TopicId,
    long At,
    string Tool,
    bool Ok,
    long DurationMs,
    string Text);

/// <summary>
/// 工作区操作审计的落地钩子（1.0.315）：Hub 只负责「写到这里」，怎么存、存多久由宿主实现。
/// 未注册实现时（测试 / 轻量宿主）审计仍随消息 <c>planJson</c> 落库，功能不受影响。
/// </summary>
public interface IWorkAuditSink
{
    void Record(WorkAuditEntry entry);
}
