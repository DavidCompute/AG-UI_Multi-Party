using AguiGroupChat.Agents;
using AguiGroupChat.Hub.Models;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>
/// 存量记忆一致性体检（1.0.364，纯函数）：找出“同一句话差一个值”的疑似冲突对。
/// 判定是启发式的：召回宽一点、交人工裁决；重点验证不把“补充说明 / 不同主题”误报进去。
/// </summary>
public sealed class MemoryConflictScanTests
{
    private static MessageMemoryItem Item(string id, string content, long ts,
        string senderType = "user", string? supersededBy = null)
        => new(id, "g1", "main", "u1", senderType, content, ts, 0, null, supersededBy);

    [Fact]
    public void SlotChange_IsFlaggedEvenWithoutCorrectionCue()
    {
        // 存量数据里通常没有“改成”，只能靠“共同开头 + 槽位词”判：负责人是 X
        var pairs = MemoryConflictScan.FindPairs(
        [
            Item("m1", "项目负责人是李四", 1000),
            Item("m2", "项目负责人是张三", 2000),
        ]);

        var p = Assert.Single(pairs);
        Assert.Equal("m1", p.Older.MessageId);
        Assert.Equal("m2", p.Newer.MessageId);
        Assert.Equal(6, p.SharedPrefix);
    }

    [Fact]
    public void NumericChange_IsFlagged()
    {
        var pairs = MemoryConflictScan.FindPairs(
        [
            Item("m1", "会议时间3点", 1000),
            Item("m2", "会议时间4点", 2000),
        ]);
        Assert.Single(pairs);
    }

    [Fact]
    public void PunctuationDoesNotHideThePair()
    {
        var pairs = MemoryConflictScan.FindPairs(
        [
            Item("m1", "项目负责人：李四", 1000),
            Item("m2", "项目负责人是张三", 2000),
        ]);
        Assert.Single(pairs);
    }

    [Fact]
    public void ComplementaryStatement_IsNotAConflict()
    {
        // 一方是另一方的前缀 = 补充说明（或截断），不是矛盾
        var pairs = MemoryConflictScan.FindPairs(
        [
            Item("m1", "负责人是李四", 1000),
            Item("m2", "负责人是李四的助理小王", 2000),
        ]);
        Assert.Empty(pairs);
    }

    [Fact]
    public void SamePrefixButDifferentSubject_IsNotFlagged()
    {
        // 共同开头很长，但前缀停在“了”上、尾部也没有数字 / 更正线索 → 不是同一属性
        var pairs = MemoryConflictScan.FindPairs(
        [
            Item("m1", "今天完成了报告", 1000),
            Item("m2", "今天完成了PPT", 2000),
        ]);
        Assert.Empty(pairs);
    }

    [Fact]
    public void SameTimestampOrIdenticalContent_IsIgnored()
    {
        Assert.Empty(MemoryConflictScan.FindPairs(
        [
            Item("m1", "项目负责人是李四", 1000),
            Item("m2", "项目负责人是张三", 1000),
        ]));
        Assert.Empty(MemoryConflictScan.FindPairs(
        [
            Item("m1", "项目负责人是李四", 1000),
            Item("m2", "项目负责人是李四", 2000),
        ]));
    }

    [Fact]
    public void KnowledgeAndImageRows_AreSkipped()
    {
        Assert.Empty(MemoryConflictScan.FindPairs(
        [
            Item("m1", "项目负责人是李四", 1000, senderType: "kb"),
            Item("m2", "项目负责人是张三", 2000, senderType: "img"),
        ]));
    }

    [Fact]
    public void LongTexts_AreSkipped()
    {
        var tail = new string('甲', 260);
        Assert.Empty(MemoryConflictScan.FindPairs(
        [
            Item("m1", "项目负责人是李四" + tail, 1000),
            Item("m2", "项目负责人是张三" + tail, 2000),
        ]));
    }

    [Fact]
    public void AlreadySupersededPairs_AreHiddenByDefault()
    {
        var items = new[]
        {
            Item("m1", "项目负责人是李四", 1000, supersededBy: "m2"),
            Item("m2", "项目负责人是张三", 2000),
        };

        Assert.Empty(MemoryConflictScan.FindPairs(items));
        Assert.Single(MemoryConflictScan.FindPairs(items, includeAlreadySuperseded: true));
    }

    [Fact]
    public void PairsAreOrderedByNewestAndCapped()
    {
        var items = new[]
        {
            Item("a1", "负责人是李四", 1000),
            Item("a2", "负责人是张三", 2000),
            Item("b1", "预算金额是五万", 3000),
            Item("b2", "预算金额是八万", 4000),
            Item("c1", "交付地址是上海", 5000),
            Item("c2", "交付地址是北京", 6000),
        };

        var all = MemoryConflictScan.FindPairs(items, maxPairs: 10);
        Assert.Equal(3, all.Count);
        Assert.Equal("c2", all[0].Newer.MessageId);

        var capped = MemoryConflictScan.FindPairs(items, maxPairs: 2);
        Assert.Equal(2, capped.Count);
        Assert.Equal("c2", capped[0].Newer.MessageId);
    }

    [Fact]
    public void TooShortTexts_AreIgnored()
    {
        Assert.Empty(MemoryConflictScan.FindPairs(
        [
            Item("m1", "是a", 1000),
            Item("m2", "是b", 2000),
        ]));
        Assert.Null(MemoryConflictScan.Judge("", "项目负责人是张三"));
    }

    [Fact]
    public void Normalize_StripsPunctuationAndCase()
    {
        Assert.Equal("项目负责人李四", MemoryConflictScan.Normalize("项目负责人：李四！"));
        Assert.Equal("ab12", MemoryConflictScan.Normalize("AB 12"));
        Assert.Equal("", MemoryConflictScan.Normalize(" —— "));
    }
}
