using System.Diagnostics;
using AguiGroupChat.Agents;
using AguiGroupChat.Hub.Models;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>
/// 暂停来源判定（1.0.367）：自动修复不得越过管理员的手工暂停。
/// 另含存量扫描的规模回归（预计算后 800 条应在毫秒级完成）。
/// </summary>
public sealed class SkillSuspensionTests
{
    private static AgentSkillDefinition Skill(bool suspended, string? reason = null)
        => new() { SkillId = "sk", Name = "技能", Suspended = suspended, SuspendedReason = reason };

    [Fact]
    public void NotSuspended_MayAutoRepair()
    {
        Assert.True(SkillSuspension.MayAutoRepair(Skill(false)));
    }

    [Fact]
    public void ArtifactAutoSuspend_MayAutoRepair()
    {
        Assert.True(SkillSuspension.MayAutoRepair(Skill(true, "产物质量连续 2/3 次未达标（阈值 70）")));
    }

    [Fact]
    public void ManualSuspend_BlocksAutoRepair()
    {
        Assert.False(SkillSuspension.MayAutoRepair(Skill(true, "管理员手工暂停")));
        Assert.False(SkillSuspension.MayAutoRepair(Skill(true, "切到新方案，先停用")));
        Assert.False(SkillSuspension.MayAutoRepair(Skill(true, null)));
    }

    [Fact]
    public void IsArtifactAutoSuspend_MatchesPrefixOnly()
    {
        Assert.True(SkillSuspension.IsArtifactAutoSuspend("产物质量连续 2/3 次未达标"));
        Assert.False(SkillSuspension.IsArtifactAutoSuspend("管理员手工暂停"));
        Assert.False(SkillSuspension.IsArtifactAutoSuspend(""));
        Assert.False(SkillSuspension.IsArtifactAutoSuspend(null));
    }

    [Fact]
    public void ConflictScan_StaysFastOnLargeGroups()
    {
        // 800 条 → 32 万对。预计算后应远低于秒级；给 5 秒上限只为捕捉「又退回每对重复规范化」的回归。
        var items = Enumerable.Range(0, 800)
            .Select(i => new MessageMemoryItem(
                $"m{i}", "g1", "main", "u1", "user",
                $"第{i}项负责人是员工{i % 97}号，交付时间是{i % 31}号", i * 1000L, 0, null))
            .ToList();

        var sw = Stopwatch.StartNew();
        var pairs = MemoryConflictScan.FindPairs(items, maxPairs: 200);
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 5000, $"扫描 800 条耗时 {sw.ElapsedMilliseconds}ms（疑似退化为逐对规范化）");
        Assert.NotNull(pairs);
    }
}
