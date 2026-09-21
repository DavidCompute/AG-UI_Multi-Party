namespace AguiGroupChat.Hub.Storage;

/// <summary>附件占用统计（管理员控制台「存储治理」展示用）。</summary>
/// <param name="TotalFiles">磁盘上的附件总数。</param>
/// <param name="TotalBytes">附件总占用字节。</param>
/// <param name="ReferencedFiles">仍被引用的附件数（消息 / 知识库文档 / 头像 / 技能试运行产物）。</param>
/// <param name="OrphanFiles">无人引用、且已超过宽限期的附件数（= 可回收）。</param>
/// <param name="OrphanBytes">可回收字节数。</param>
/// <param name="GracePeriodHours">判定「可回收」所用的宽限期（小时）。</param>
public sealed record AttachmentStorageStats(
    int TotalFiles,
    long TotalBytes,
    int ReferencedFiles,
    int OrphanFiles,
    long OrphanBytes,
    int GracePeriodHours);

/// <summary>一次回收的结果。</summary>
/// <param name="DeletedFiles">实际删除的附件数。</param>
/// <param name="DeletedBytes">实际释放的字节数。</param>
/// <param name="SkippedReferenced">候选里因仍被引用而跳过（未删）的附件数。</param>
public sealed record AttachmentReclaimResult(int DeletedFiles, long DeletedBytes, int SkippedReferenced);

/// <summary>
/// 附件生命周期（判定「无人引用」并回收）。
///
/// <para>
/// 为何接口定义在 Hub、实现在 Web：判定“还有谁引用这个附件”要同时看得到消息（Hub）、
/// 知识库文档与数字员工头像（Agents）、技能试运行产物（Web）——只有 Web 同时引用三者，
/// 而 Hub 不能反向依赖 Agents（既有约定：接口在 Hub、实现在上层，如 <c>IAgentDefinitionStore</c>）。
/// </para>
///
/// <para>
/// 未注册实现时（单元测试等轻量宿主），调用方应把“删除附件”视为不可用，
/// 绝不能退化成“删了消息就当附件也处理了”——那会留下无人知晓的孤儿文件。
/// </para>
/// </summary>
public interface IAttachmentLifecycle
{
    /// <summary>只读统计：磁盘占用、仍被引用数、可回收数（不删任何东西）。</summary>
    AttachmentStorageStats Inspect(TimeSpan gracePeriod);

    /// <summary>
    /// 删除给定附件中“确实无人引用”的那些（用于「清空 / 删除话题时同时删除这批附件」）。
    /// 仍被引用的会被跳过并计入 <see cref="AttachmentReclaimResult.SkippedReferenced"/>。
    /// </summary>
    AttachmentReclaimResult DeleteIfUnreferenced(IReadOnlyCollection<string> attachmentIds);

    /// <summary>回收宽限期内无引用的全部孤儿；<paramref name="dryRun"/>=true 只统计不删。</summary>
    AttachmentReclaimResult ReclaimOrphans(TimeSpan gracePeriod, bool dryRun);
}
