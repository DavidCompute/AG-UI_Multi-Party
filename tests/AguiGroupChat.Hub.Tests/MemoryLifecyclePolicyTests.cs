using AguiGroupChat.Agents;
using AguiGroupChat.Hub.Models;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>记忆生命周期规则的确定性判定（巩固 / 收紧遗忘 / 不误伤重要记忆）。</summary>
public sealed class MemoryLifecyclePolicyTests
{
    private const long Hour = 3_600_000L;
    private const long Day = 86_400_000L;
    private const long Now = 2_000_000_000_000L;

    private static MessageMemoryItem Item(
        string sender = "agent_a",
        string senderType = "Agent",
        int importance = MemoryImportance.Normal,
        long? expiresAt = null,
        long ageMs = 0,
        string content = "这是一条足够长的记忆内容")
        => new("m1", "g1", "main", sender, senderType, content, Now - ageMs, importance, expiresAt);

    private static MemoryProfile Profile(string type) => new() { MemoryType = type };

    [Fact]
    public void Deep_NormalOldMemory_PromotesAndClearsExpiry()
    {
        var decision = MemoryLifecyclePolicy.Decide(
            Item(expiresAt: Now + Day, ageMs: 25 * Hour),
            Profile(MemoryPersonalityTypes.Deep),
            new MemoryOptions(), repeatCount: 0, nowMs: Now);

        Assert.True(decision.Promote);
        Assert.True(decision.ClearExpiry);
        Assert.Null(decision.ExpiresAt);
    }

    [Fact]
    public void Deep_AlreadyImportant_ClearsExpiryOnly()
    {
        var decision = MemoryLifecyclePolicy.Decide(
            Item(importance: MemoryImportance.Important, expiresAt: Now + Day, ageMs: Hour),
            Profile(MemoryPersonalityTypes.Deep),
            new MemoryOptions(), repeatCount: 0, nowMs: Now);

        Assert.False(decision.Promote);
        Assert.True(decision.ClearExpiry);
    }

    [Fact]
    public void SlowToLearn_RepeatedEnough_Consolidates()
    {
        var decision = MemoryLifecyclePolicy.Decide(
            Item(content: "反复确认过的同一套流程", expiresAt: Now + Day),
            Profile(MemoryPersonalityTypes.SlowToLearn),
            new MemoryOptions { SlowToLearnRepeatThreshold = 3 }, repeatCount: 3, nowMs: Now);

        Assert.True(decision.Promote);
        Assert.True(decision.ClearExpiry);
    }

    [Fact]
    public void SlowToLearn_NotRepeated_FallsBackToGlobalRetention()
    {
        var decision = MemoryLifecyclePolicy.Decide(
            Item(ageMs: 10 * Day), Profile(MemoryPersonalityTypes.SlowToLearn),
            new MemoryOptions { RetentionDays = 7, SlowToLearnRepeatThreshold = 3 },
            repeatCount: 2, nowMs: Now);

        Assert.False(decision.Promote);
        Assert.Equal(Now - 10 * Day + 7 * Day, decision.ExpiresAt);
    }

    [Fact]
    public void FastForgetting_ShortensToProfileRetention()
    {
        var decision = MemoryLifecyclePolicy.Decide(
            Item(ageMs: 3 * Day), Profile(MemoryPersonalityTypes.FastForgetting),
            new MemoryOptions { RetentionDays = 10 }, repeatCount: 0, nowMs: Now);

        // 10 天 × 40% = 4 天
        Assert.Equal(Now - 3 * Day + 4 * Day, decision.ExpiresAt);
        Assert.False(decision.Promote);
    }

    [Fact]
    public void FastForgetting_WithoutGlobalRetention_KeepsMemory()
    {
        var decision = MemoryLifecyclePolicy.Decide(
            Item(ageMs: 30 * Day), Profile(MemoryPersonalityTypes.FastForgetting),
            new MemoryOptions { RetentionDays = 0 }, repeatCount: 0, nowMs: Now);

        Assert.False(decision.HasChange);
    }

    [Fact]
    public void ImportantMemory_IsNeverAutoExpired()
    {
        var decision = MemoryLifecyclePolicy.Decide(
            Item(importance: MemoryImportance.Important, ageMs: 365 * Day),
            Profile(MemoryPersonalityTypes.Broad),
            new MemoryOptions { RetentionDays = 7 }, repeatCount: 0, nowMs: Now);

        Assert.False(decision.HasChange);
    }

    [Fact]
    public void UserMemory_BackfillsGlobalRetentionWhenMissing()
    {
        var decision = MemoryLifecyclePolicy.Decide(
            Item(sender: "u1", senderType: "User", ageMs: 10 * Day),
            profile: null,
            new MemoryOptions { RetentionDays = 7 }, repeatCount: 0, nowMs: Now);

        Assert.Equal(Now - 10 * Day + 7 * Day, decision.ExpiresAt);
    }

    [Fact]
    public void RepeatKey_IgnoresShortOrPunctuationOnlyContent()
    {
        Assert.Null(MemoryLifecyclePolicy.RepeatKey("agent_a", "收到！"));
        Assert.Null(MemoryLifecyclePolicy.RepeatKey("agent_a", "——，。"));

        var key = MemoryLifecyclePolicy.RepeatKey("agent_a", "同一套流程，反复确认！");
        Assert.NotNull(key);
        Assert.Equal(key, MemoryLifecyclePolicy.RepeatKey("agent_a", "同一套流程 反复确认"));
        Assert.NotEqual(key, MemoryLifecyclePolicy.RepeatKey("agent_b", "同一套流程 反复确认"));
    }
}
