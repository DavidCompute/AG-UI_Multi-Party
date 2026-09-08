using AguiGroupChat.Agents;
using AguiGroupChat.Hub.Agents;
using AguiGroupChat.Hub.Models;
using AguiGroupChat.Hub.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>
/// 数字员工「记忆拟人类型」单元测试：类型 → 单次召回参数（TopK / 阈值 / 提示分支 / 近期窗口 / 口吻说明），
/// 以及 AgentMessageMemory 把单次覆盖真正传给 store.Search 的链路。
/// </summary>
public sealed class MemoryProfileTests
{
    private static MemoryOptions Memory() => new()
    {
        TopK = 5,
        MinScore = 0.25,
        PersonalTopK = 4,
        PersonalMinScore = 0.25,
    };

    private static MemoryProfile Profile(string type, string? styleMode = null, string? persona = null,
        int? topK = null, int? personalTopK = null, double? minScore = null, double? personalMinScore = null)
        => new()
        {
            MemoryType = type,
            StyleMode = styleMode,
            PersonaCard = persona,
            TopK = topK,
            PersonalTopK = personalTopK,
            MinScore = minScore,
            PersonalMinScore = personalMinScore,
        };

    [Fact]
    public void Resolve_NoProfileOrUnknownType_ReturnsNull()
    {
        Assert.Null(MemoryProfileTuning.Resolve(null, Memory(), "你好"));
        // 未知 key / 留空：视为未配置（兼容旧数据，行为沿用全局）
        Assert.Null(MemoryProfileTuning.Resolve(Profile("custom"), Memory(), "你好"));
        Assert.Null(MemoryProfileTuning.Resolve(Profile(""), Memory(), "你好"));
    }

    [Fact]
    public void Resolve_Broad_WidensRecallAndLowersThreshold()
    {
        var r = MemoryProfileTuning.Resolve(Profile(MemoryPersonalityTypes.Broad), Memory(), "怎么安排后端架构?");
        Assert.NotNull(r);
        // 多取 + 阈值放低（把“也许相关”也捞回来），个人记忆同理
        Assert.Equal(9, r!.Tuning.TopK);              // ceil(5×1.8)
        Assert.Equal(0.15, r.Tuning.MinScore);        // 0.25 − 0.12（下限 0.15）
        Assert.Equal(7, r.Tuning.PersonalTopK);       // ceil(4×1.6)
        Assert.Equal(0.15, r.Tuning.PersonalMinScore);
        Assert.Contains("广记型", r.RecallNote);
        Assert.Contains("我记得好像是", r.RecallNote); // 置信度降级语气（不伪造记忆）
        Assert.False(r.CueDetected);
    }

    [Fact]
    public void Resolve_Deep_StricterAndFewer()
    {
        var r = MemoryProfileTuning.Resolve(Profile(MemoryPersonalityTypes.Deep), Memory(), "你好");
        Assert.NotNull(r);
        Assert.Equal(3, r!.Tuning.TopK);              // ceil(5×0.5) 宁缺毋滥
        Assert.Equal(2, r.Tuning.PersonalTopK);
        Assert.Equal(0.37, r.Tuning.MinScore);        // 0.25 + 0.12
        Assert.Null(r.RecencyWindowDays);
        Assert.Contains("深记型", r.RecallNote);
        Assert.Contains("宁可少说也不要讲错", r.RecallNote);
    }

    [Fact]
    public void Resolve_SlowToLearn_OnlyRepeatedThingsSurface()
    {
        var r = MemoryProfileTuning.Resolve(Profile(MemoryPersonalityTypes.SlowToLearn), Memory(), "你好");
        Assert.NotNull(r);
        Assert.Equal(4, r!.Tuning.TopK);              // ceil(5×0.75)
        Assert.Equal(3, r.Tuning.PersonalTopK);
        Assert.Equal(0.31, r.Tuning.MinScore);        // 0.25 + 0.06
        Assert.Contains("难录入型", r.RecallNote);
    }

    [Fact]
    public void Resolve_CueDependent_DefaultWeak_ReminderWidens()
    {
        var memory = Memory();
        // 平时提取弱：默认比全局更克制
        var calm = MemoryProfileTuning.Resolve(Profile(MemoryPersonalityTypes.CueDependent), memory, "随便聊聊最近怎么样");
        Assert.NotNull(calm);
        Assert.False(calm!.CueDetected);
        Assert.Equal(3, calm.Tuning.TopK);            // ceil(5×0.55)
        Assert.Equal(0.33, calm.Tuning.MinScore);     // 0.25 + 0.08

        // 提示分支：对方在给回忆线索 → 临时放宽提取
        var cued = MemoryProfileTuning.Resolve(Profile(MemoryPersonalityTypes.CueDependent), memory, "你还记得上次我们聊的技术方案吗？");
        Assert.NotNull(cued);
        Assert.True(cued!.CueDetected);
        Assert.Equal(8, cued.Tuning.TopK);            // ceil(5×1.6)，至少比基准多 2
        Assert.Equal(0.18, cued.Tuning.MinScore);     // 阈值放低到能捞回更多候选
        Assert.Equal(7, cued.Tuning.PersonalTopK);
        Assert.Contains("想不起来", cued.RecallNote);
    }

    [Fact]
    public void Resolve_FastForgetting_RecencyWindowAndStricterScore()
    {
        var r = MemoryProfileTuning.Resolve(Profile(MemoryPersonalityTypes.FastForgetting), Memory(), "你好");
        Assert.NotNull(r);
        Assert.Equal(4, r!.Tuning.TopK);              // ceil(5×0.7)
        Assert.Equal(0.31, r.Tuning.MinScore);
        Assert.Equal(21, r.RecencyWindowDays);        // 只对最近 21 天清晰
        Assert.Contains("快速遗忘型", r.RecallNote);
    }

    [Fact]
    public void Resolve_MicroTuningOverrides_WinOverPreset()
    {
        // 深记型但用户显式微调放宽：取更多、阈值更低
        var r = MemoryProfileTuning.Resolve(Profile(MemoryPersonalityTypes.Deep, topK: 12, minScore: 0.18), Memory(), "你好");
        Assert.NotNull(r);
        Assert.Equal(12, r!.Tuning.TopK);
        Assert.Equal(0.18, r.Tuning.MinScore);
        Assert.Equal(2, r.Tuning.PersonalTopK);       // 个人记忆未微调 → 仍按深记型默认
    }

    [Fact]
    public void Resolve_DigestStyleAndPersona_AppendedToNote()
    {
        var r = MemoryProfileTuning.Resolve(
            Profile(MemoryPersonalityTypes.Deep, styleMode: "digest", persona: "说话干脆，不爱啰嗦。"), Memory(), "你好");
        Assert.NotNull(r);
        Assert.Equal("digest", r!.StyleMode);
        Assert.Contains("概括成要点", r.RecallNote);
        Assert.Contains("说话干脆", r.RecallNote);
    }

    [Theory]
    [InlineData("你还记得上次说的吗", true)]
    [InlineData("记得之前聊过 WebSocket", true)]
    [InlineData("上次的方案可以再说一下吗", true)]
    [InlineData("remind me what we discussed", true)]
    [InlineData("今天天气不错", false)]
    [InlineData(null, false)]
    public void DetectRecallCue_MatchesChineseEnglishReminders(string? query, bool expected)
        => Assert.Equal(expected, MemoryProfileTuning.DetectRecallCue(query));

    // ================= AgentMessageMemory → store 覆盖链路 =================

    [Fact]
    public async Task AgentMessageMemory_PassesTuning_IntoStoreSearch()
    {
        var store = new CapturingStore();
        var options = new AgentOptions
        {
            Provider = "mock",
            Memory = new MemoryOptions { Enabled = true, TopK = 5, MinScore = 0.25, PersonalTopK = 4, PersonalMinScore = 0.25, HybridSearch = false },
        };
        using var memory = new AgentMessageMemory(store, options, NullLogger<AgentMessageMemory>.Instance, new FixedEmbeddingProvider());
        var tuning = new MemoryRetrievalTuning { TopK = 9, MinScore = 0.15, PersonalTopK = 7, PersonalMinScore = 0.15 };

        await memory.SearchAsync("g1", "agent_a", "架构怎么定", CancellationToken.None, tuning);
        Assert.Equal(9, store.LastGroupTopK);
        Assert.Equal(0.15, store.LastGroupMinScore);

        await memory.SearchPersonAsync("user_1", "g1", "我偏好", CancellationToken.None, tuning);
        Assert.Equal(7, store.LastPersonalTopK);
        Assert.Equal(0.15, store.LastPersonalMinScore);
    }

    [Fact]
    public async Task AgentMessageMemory_NoTuning_UsesGlobalDefaults()
    {
        var store = new CapturingStore();
        var options = new AgentOptions
        {
            Provider = "mock",
            Memory = new MemoryOptions { Enabled = true, TopK = 5, MinScore = 0.25, PersonalTopK = 4, PersonalMinScore = 0.25, HybridSearch = false },
        };
        using var memory = new AgentMessageMemory(store, options, NullLogger<AgentMessageMemory>.Instance, new FixedEmbeddingProvider());

        await memory.SearchAsync("g1", "agent_a", "架构怎么定");
        Assert.Equal(5, store.LastGroupTopK);
        Assert.Equal(0.25, store.LastGroupMinScore);
    }

    private sealed class FixedEmbeddingProvider : IEmbeddingProvider
    {
        public Task<float[]?> EmbedAsync(string text, CancellationToken ct = default) => Task.FromResult<float[]?>(new float[4]);
        public void Dispose() { }
    }

    /// <summary>记录 store.Search / SearchPerson 收到 topK / minScore 的测试替身。</summary>
    private sealed class CapturingStore : IMessageMemoryStore
    {
        public int LastGroupTopK = -1;
        public double LastGroupMinScore = -1;
        public int LastPersonalTopK = -1;
        public double LastPersonalMinScore = -1;
        public void EnsureSchema() { }
        public void Upsert(MessageMemoryRecord record) { }
        public void Remove(string groupId, string messageId) { }
        public void RemoveGroup(string groupId) { }
        public void ClearAll() { }
        public IReadOnlyList<MessageMemoryHit> Search(string groupId, string? agentId, float[] embedding, int topK, double minScore, string scope)
        {
            LastGroupTopK = topK;
            LastGroupMinScore = minScore;
            return [];
        }
        public IReadOnlyList<MessageMemoryHit> SearchPerson(string personId, string currentGroupId, float[] embedding, int topK, double minScore)
        {
            LastPersonalTopK = topK;
            LastPersonalMinScore = minScore;
            return [];
        }
        public IReadOnlyList<MessageMemoryItem> ListMessages(string? groupId, string? senderId, string? keyword, int limit, int offset) => [];
        public long CountMessages(string? groupId, string? senderId, string? keyword) => 0;
        public IReadOnlyList<MessageMemoryGroupStat> GroupStats(long nowMs) => [];
        public bool DeleteByMessageId(string messageId) => false;
        public bool UpdateImportance(string messageId, int importance) => false;
        public int SetExpiry(string? groupId, long? expiresAt, long nowMs) => 0;
        public int PruneExpired(long nowMs) => 0;
    }
}
