using AguiGroupChat.Agents;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>
/// 记忆冲突检测（1.0.363，纯函数）：宁可漏判不要误判——更正线索、短句、语义相近、词面锚点四条同时满足才取代。
/// </summary>
public sealed class MemoryConflictDetectorTests
{
    private const long Now = 1_700_000_000_000;

    private static MemoryConflictCandidate Cand(string id, string content, double score, long atMs = Now - 60_000)
        => new(id, content, atMs, score);

    [Fact]
    public void ExplicitCorrection_WithCloseNeighbour_IsDetected()
    {
        var conflicts = MemoryConflictDetector.Detect(
            "项目负责人改为张三，下周一交接",
            [Cand("m_old", "项目负责人是李四", 0.93)],
            Now);

        var c = Assert.Single(conflicts);
        Assert.Equal("m_old", c.OlderMessageId);
        Assert.Contains("更正线索", c.Reason);
    }

    [Fact]
    public void NoCorrectionCue_IsNeverDetected()
    {
        Assert.Empty(MemoryConflictDetector.Detect(
            "项目负责人是张三", [Cand("m_old", "项目负责人是李四", 0.99)], Now));
    }

    [Fact]
    public void LowSimilarity_IsIgnored()
    {
        Assert.Empty(MemoryConflictDetector.Detect(
            "项目负责人改为张三", [Cand("m_old", "项目负责人是李四", 0.62)], Now, minScore: 0.9));
    }

    [Fact]
    public void LongMessages_AreIgnored()
    {
        var longOld = new string('甲', 260);
        Assert.Empty(MemoryConflictDetector.Detect(
            "预算改为每月十万", [Cand("m_old", longOld, 0.98)], Now, maxChars: 200));

        var longNew = "预算改为每月十万，" + new string('乙', 220);
        Assert.Empty(MemoryConflictDetector.Detect(
            longNew, [Cand("m_old", "预算每月五万", 0.98)], Now, maxChars: 200));
    }

    [Fact]
    public void OnlyOlderMemories_AreSuperseded()
    {
        Assert.Empty(MemoryConflictDetector.Detect(
            "项目负责人改为张三",
            [Cand("m_newer", "项目负责人是李四", 0.97, atMs: Now + 5_000)],
            Now));
    }

    [Fact]
    public void HighSimilarityWithoutLexicalAnchor_IsIgnored()
    {
        // 语义相近但字面毫无交集：可能是“同主题不同事”，不判取代
        Assert.Empty(MemoryConflictDetector.Detect(
            "负责人改为张三",
            [Cand("m_old", "预算金额是五万", 0.95)],
            Now, minSharedBigrams: 2));
    }

    [Fact]
    public void MarksAreCappedAndOrderedByScore()
    {
        var conflicts = MemoryConflictDetector.Detect(
            "项目负责人改为张三",
            [
                Cand("m_a", "项目负责人是李四", 0.91),
                Cand("m_b", "项目负责人是王五", 0.96),
                Cand("m_c", "项目负责人是老六", 0.93),
            ],
            Now, maxMarks: 2);

        Assert.Equal(2, conflicts.Count);
        Assert.Equal("m_b", conflicts[0].OlderMessageId);
        Assert.Equal("m_c", conflicts[1].OlderMessageId);
    }

    [Fact]
    public void EmptyInputs_AreSafe()
    {
        Assert.Empty(MemoryConflictDetector.Detect("", [Cand("m", "x", 1)], Now));
        Assert.Empty(MemoryConflictDetector.Detect("项目改为张三", null, Now));
        Assert.Empty(MemoryConflictDetector.Detect("项目改为张三", [], Now));
    }

    [Fact]
    public void CorrectionCue_RecognisesCommonPhrasings()
    {
        Assert.True(MemoryConflictDetector.HasCorrectionCue("以后按新流程走"));
        Assert.True(MemoryConflictDetector.HasCorrectionCue("对接人换成了小王"));
        Assert.True(MemoryConflictDetector.HasCorrectionCue("这个方案作废"));
        Assert.False(MemoryConflictDetector.HasCorrectionCue("这个方案正在评审"));
        Assert.False(MemoryConflictDetector.HasCorrectionCue(null));
    }

    [Fact]
    public void Bigrams_IgnorePunctuationAndCase()
    {
        Assert.Equal(MemoryConflictDetector.Bigrams("AB 项目"), MemoryConflictDetector.Bigrams("ab项目"));
        Assert.Empty(MemoryConflictDetector.Bigrams("，。 "));
        Assert.Equal(4, MemoryConflictDetector.SharedBigrams(
            MemoryConflictDetector.Bigrams("项目负责人改为张三"),
            "项目负责人是李四"));
    }
}
