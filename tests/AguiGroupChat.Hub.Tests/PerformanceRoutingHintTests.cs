using AguiGroupChat.Agents;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>绩效反哺编排的提示构建（纯函数）：仅列出候选中有足够近期反馈者。</summary>
public sealed class PerformanceRoutingHintTests
{
    private static readonly long Now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    [Fact]
    public void BuildLines_OnlyCandidatesWithEnoughRecentFeedback()
    {
        var fb = new[]
        {
            new MessageFeedbackEntry { MessageId = "m1", GroupId = "g", AgentId = "a1", UserId = "u1", Value = 1, CreatedAtMs = Now },
            new MessageFeedbackEntry { MessageId = "m2", GroupId = "g", AgentId = "a1", UserId = "u2", Value = 1, CreatedAtMs = Now },
            new MessageFeedbackEntry { MessageId = "m3", GroupId = "g", AgentId = "a1", UserId = "u3", Value = -1, CreatedAtMs = Now }, // a1: 2/3 ≈ 67%
            new MessageFeedbackEntry { MessageId = "m4", GroupId = "g", AgentId = "a2", UserId = "u1", Value = 1, CreatedAtMs = Now },  // a2：样本仅 1 → 不足
            new MessageFeedbackEntry { MessageId = "m5", GroupId = "g", AgentId = "a3", UserId = "u1", Value = 1, CreatedAtMs = Now - 40L * 24 * 3600 * 1000 }, // a3：窗口外
        };
        var names = new Dictionary<string, string> { ["a1"] = "一", ["a2"] = "二", ["a3"] = "三" };

        var lines = PerformanceRoutingHint.BuildLines(fb, ["a1", "a2", "a3", "a4"],
            id => names.TryGetValue(id, out var n) ? n : id, Now, 30, 2);

        var only = Assert.Single(lines);
        Assert.Contains("a1", only);
        Assert.Contains("67%", only);
        Assert.Contains("2👍/1👎", only);
    }

    [Fact]
    public void BuildLines_LowerMinRatings_IncludesSparseCandidate()
    {
        var fb = new[]
        {
            new MessageFeedbackEntry { MessageId = "m1", GroupId = "g", AgentId = "a2", UserId = "u1", Value = 1, CreatedAtMs = Now },
        };
        var lines = PerformanceRoutingHint.BuildLines(fb, ["a2"], id => id, Now, 30, 1);
        Assert.Contains("100%", Assert.Single(lines));
    }

    [Fact]
    public void BuildLines_DuplicateCandidates_DoNotThrow()
    {
        var fb = new[]
        {
            new MessageFeedbackEntry { MessageId = "m1", GroupId = "g", AgentId = "a1", UserId = "u1", Value = 1, CreatedAtMs = Now },
        };
        var lines = PerformanceRoutingHint.BuildLines(fb, ["a1", "a1"], id => id, Now, 30, 1);
        Assert.Equal(2, lines.Count); // 重复候选仅重复提示行，不抛「重复键」
    }

    [Fact]
    public void BuildLines_NoFeedback_Empty()
    {
        var lines = PerformanceRoutingHint.BuildLines([], ["a1"], id => id, Now, 30, 1);
        Assert.Empty(lines);
    }
}
