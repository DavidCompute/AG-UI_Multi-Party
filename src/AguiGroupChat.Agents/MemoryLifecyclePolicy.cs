using System.Text;
using AguiGroupChat.Hub.Models;

namespace AguiGroupChat.Agents;

/// <summary>一条记忆在生命周期周期任务中的处置决定（只改元数据，不改写原文）。</summary>
public readonly record struct MemoryLifecycleDecision(
    bool Promote,
    bool ClearExpiry,
    long? ExpiresAt,
    string Reason)
{
    public bool HasChange => Promote || ClearExpiry || ExpiresAt.HasValue;

    public static readonly MemoryLifecycleDecision Keep = new(false, false, null, "keep");
}

/// <summary>
/// 记忆「巩固 / 遗忘」的确定性规则（1.0.340）。
///
/// <para>
/// 写入侧（<see cref="MemoryProfileWritePolicy"/>）已对新记忆做即时拟人化；本规则补的是<b>存量记忆</b>
/// 与<b>随时间变化</b>的部分，由 <see cref="MemoryLifecycleService"/> 周期执行：
/// </para>
/// <list type="bullet">
///   <item><b>深记型</b>：旧普通记忆补升为「重要」并清除过期时间（烙印一旦形成就不随时间淡化）；</item>
///   <item><b>难录入型</b>：同一员工的同一内容重复出现达到阈值后视为“终于记住”——升为重要并不过期；</item>
///   <item><b>快速遗忘型</b>：按缩短后的保留期收紧过期时间（仅在平台开启自动遗忘时）；</item>
///   <item><b>广记 / 存得住想不起 / 未配置 / 用户消息</b>：按全局保留期补齐过期时间，不额外改写内容；</item>
///   <item>任何情况下都不自动降级、不自动延长重要级记忆的寿命。</item>
/// </list>
/// </summary>
public static class MemoryLifecyclePolicy
{
    private const long HourMs = 3_600_000L;
    private const long DayMs = 86_400_000L;

    /// <summary>判定一条记忆本轮的处置。重复次数只对「同员工 + 同规范化内容」计数。</summary>
    public static MemoryLifecycleDecision Decide(
        MessageMemoryItem item,
        MemoryProfile? profile,
        MemoryOptions options,
        int repeatCount,
        long nowMs)
    {
        var age = Math.Max(0, nowMs - item.Timestamp);
        var isAgent = item.SenderType.Equals("Agent", StringComparison.OrdinalIgnoreCase);
        var type = isAgent ? profile?.MemoryType : null;

        switch (type)
        {
            case MemoryPersonalityTypes.Deep:
            {
                var threshold = Math.Max(1, options.DeepConsolidateAfterHours) * HourMs;
                var promote = item.Importance == MemoryImportance.Normal && age >= threshold;
                var clear = item.ExpiresAt is not null;
                if (!promote && !clear) return MemoryLifecycleDecision.Keep;
                return new MemoryLifecycleDecision(promote, clear, null,
                    promote ? "deep-promote" : "deep-clear-expiry");
            }

            case MemoryPersonalityTypes.SlowToLearn:
            {
                var threshold = Math.Max(2, options.SlowToLearnRepeatThreshold);
                if (repeatCount >= threshold)
                {
                    var promote = item.Importance == MemoryImportance.Normal;
                    var clear = item.ExpiresAt is not null;
                    if (promote || clear)
                        return new MemoryLifecycleDecision(promote, clear, null,
                            promote ? "slow-to-learn-consolidate" : "slow-to-learn-clear-expiry");
                }
                return GlobalRetention(item, options);
            }

            case MemoryPersonalityTypes.FastForgetting:
            {
                if (options.RetentionDays <= 0) return MemoryLifecycleDecision.Keep;
                if (item.Importance != MemoryImportance.Normal) return MemoryLifecycleDecision.Keep;
                var days = MemoryProfileWritePolicy.RetentionDaysFor(profile, options);
                if (days <= 0) return MemoryLifecycleDecision.Keep;
                var target = item.Timestamp + days * DayMs;
                if (item.ExpiresAt is { } existing && existing <= target)
                    return MemoryLifecycleDecision.Keep;
                return new MemoryLifecycleDecision(false, false, target, "fast-forgetting-shorten");
            }

            default:
                return GlobalRetention(item, options);
        }
    }

    /// <summary>全局保留期补齐：普通记忆缺失过期时间、或过期时间比当前保留期更晚时，收紧到「写入时间 + 保留天数」。</summary>
    private static MemoryLifecycleDecision GlobalRetention(MessageMemoryItem item, MemoryOptions options)
    {
        if (options.RetentionDays <= 0 || item.Importance != MemoryImportance.Normal)
            return MemoryLifecycleDecision.Keep;
        var target = item.Timestamp + options.RetentionDays * DayMs;
        if (item.ExpiresAt is { } existing && existing <= target)
            return MemoryLifecycleDecision.Keep;
        return new MemoryLifecycleDecision(false, false, target, "global-retention");
    }

    /// <summary>
    /// 难录入型的重复判定键：同一员工 + 规范化内容（仅保留字母 / 数字并转小写）。
    /// 规范化后少于 8 个字符的内容不参与重复计数，避免“好的 / 收到”这类短句被误巩固。
    /// </summary>
    public static string? RepeatKey(string senderId, string content)
    {
        var normalized = NormalizeContent(content);
        return normalized.Length < 8 ? null : senderId + "\u001F" + normalized;
    }

    private static string NormalizeContent(string content)
    {
        var sb = new StringBuilder(content.Length);
        foreach (var c in content)
        {
            if (!char.IsLetterOrDigit(c)) continue;
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }
}
