using AguiGroupChat.Hub.Models;

namespace AguiGroupChat.Agents;

/// <summary>
/// 数字员工「记忆拟人类型」的<b>写入侧</b>策略：仅对“该数字员工本人发言”的落库调整
/// （由 <see cref="AgentMessageMemory"/> 写队列按作者 agentId 的 MemoryProfile 调用）。
/// 记忆本体仍为知聚共享历史，未配置（profile 为 null）或非本人发言一律返回默认，与旧行为完全一致。
///
/// 写入侧拟人（保守、可解释，绝不删数据）：
/// <list type="bullet">
///   <item><b>深记型（deep）</b>：“说出口即烙印”——本人发言自动升为「重要」级记忆
///        （importance=Important，不随自动遗忘过期、同相似度检索优先），模拟记得少而久、难忘；</item>
///   <item><b>快速遗忘型（fastForgetting）</b>：在平台开启自动遗忘（RetentionDays&gt;0）时，
///        本人<b>普通</b>发言的保留期缩短到全局的约 40%（最少 2 天），模拟“时间一长自动淡化”；
///        平台未开自动遗忘时不主动引入过期（尊重运维选择，淡忘由读取侧的近期窗口呈现）。</item>
///   <item><b>广记 / 难录入 / 存得住想不起</b>：写入无差别——差异集中在召回侧（见 <see cref="MemoryProfileTuning"/>）。</item>
/// </list>
/// </summary>
public static class MemoryProfileWritePolicy
{
    /// <summary>快速遗忘型最短保留天数（兜底下限，避免过于激进到无法复盘）。</summary>
    public const int MinFastForgettingRetentionDays = 2;

    /// <summary>快速遗忘型保留天数相对全局自动遗忘的折算比例。</summary>
    private const double FastForgettingRetentionRatio = 0.4;

    /// <summary>解析写入级别：深记型本人发言自动升为「重要」（不低于已有级别）；其余原样。</summary>
    public static int ImportanceFor(MemoryProfile? profile, int baseImportance)
        => profile?.MemoryType == MemoryPersonalityTypes.Deep
            ? Math.Max(baseImportance, MemoryImportance.Important)
            : baseImportance;

    /// <summary>解析写入过期：快速遗忘型在自动遗忘开启时返回缩短后的过期时间；其余返回 null（沿用全局逻辑）。</summary>
    public static long? ExpiryFor(MemoryProfile? profile, MemoryOptions options, long timestampMs)
    {
        if (profile?.MemoryType != MemoryPersonalityTypes.FastForgetting || options.RetentionDays <= 0) return null;
        var days = Math.Max(MinFastForgettingRetentionDays, (int)Math.Ceiling(options.RetentionDays * FastForgettingRetentionRatio));
        return timestampMs + days * 86_400_000L;
    }
}
