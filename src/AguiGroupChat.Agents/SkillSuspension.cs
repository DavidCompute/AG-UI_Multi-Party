namespace AguiGroupChat.Agents;

/// <summary>
/// 暂停来源判定（1.0.367）：自动修复只应介入「因产物质量被自动隔离」的技能。
///
/// <para>
/// 管理员手工暂停（或带任意原因的暂停）是**人的决定**——自动修复如果把它恢复了，
/// 等于绕过管理员的封禁，属于越权副作用。因此两侧（告警桥排队、工作线程执行）都要判：
/// 只有未暂停，或暂停原因来自产物质量自动隔离时，才允许自动修复。
/// </para>
/// </summary>
public static class SkillSuspension
{
    /// <summary>产物质量自动隔离的原因前缀（与 <c>ArtifactAlertBridge</c> 写入的一致）。</summary>
    public const string ArtifactPrefix = "产物质量";

    public static bool IsArtifactAutoSuspend(string? reason)
        => !string.IsNullOrWhiteSpace(reason) && reason.StartsWith(ArtifactPrefix, StringComparison.Ordinal);

    /// <summary>自动修复是否可以介入该技能。</summary>
    public static bool MayAutoRepair(AgentSkillDefinition skill)
        => !skill.Suspended || IsArtifactAutoSuspend(skill.SuspendedReason);
}
