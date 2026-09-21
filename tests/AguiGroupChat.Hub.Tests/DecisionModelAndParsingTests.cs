using AguiGroupChat.Agents;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>
/// 判定类「小决策」（该不该发言 / 派给谁）的回归：模型选择 + 概率解析 + 空输出语义。
///
/// <para>
/// 为何要单独钉住：这些调用的失败方式**全都没有异常**。实测（生产实际使用的 deepseek-flash）：
/// 思考模式下输出预算 8 / 64 会被思维链吃光 → 正文为空 →
/// 发言判定的 <c>StartsWith("YES")</c> 恒为假（“永远不发言”）、
/// 指派路由把空输出当成“无候选”（“只有问题提升、没有任务指派”）。
/// 两边都只是静默退化，因此必须有测试把它们钉住。
/// </para>
/// </summary>
public sealed class DecisionModelAndParsingTests
{
    private static AgentOptions Opts(bool thinking, bool deepseek = true) => new()
    {
        Provider = deepseek ? "deepseek" : "openai",
        Model = "deepseek-chat",
        ThinkingMode = thinking,
    };

    // ============ ① 判定调用不得走推理模型 ============

    [Fact]
    public void DecisionModel_IgnoresThinkingMode_SoReasoningNeverEatsTheBudget()
    {
        var options = Opts(thinking: true);
        var def = new AgentDefinition { AgentId = "agent_x", Nickname = "X" };

        var normal = AgentCatalog.ResolveModelName(options, def, isDeepSeek: true);
        var decision = AgentCatalog.ResolveDecisionModelName(options, def, isDeepSeek: true);

        // 日常回复在思考模式下走推理模型……
        Assert.NotEqual(options.Model, normal);
        // ……而判定调用必须仍走配置的非推理模型（否则小预算被思维链吃光、正文为空）
        Assert.Equal(options.Model, decision);
        Assert.NotEqual(normal, decision);
    }

    [Fact]
    public void DecisionModel_RespectsExplicitOverride_AndAgentModel()
    {
        var withOverride = Opts(thinking: true);
        withOverride.DecisionModel = "my-router";
        Assert.Equal("my-router", AgentCatalog.ResolveDecisionModelName(withOverride, null, true));

        var withAgentModel = Opts(thinking: true);
        Assert.Equal("agent-special",
            AgentCatalog.ResolveDecisionModelName(withAgentModel, new AgentDefinition { AgentId = "a", Nickname = "A", Model = "agent-special" }, true));
    }

    // ============ ② 概率解析（用生产实测到的真实 logprobs 形态）============

    /// <summary>生产实测的 DeepSeek logprobs 形态：首个 token 是 NO（-0.011），top 里还有 YES（-4.53）与中文候选。</summary>
    private static readonly (string Token, double LogProbability)[] MeasuredNoDominant =
    [
        ("NO", -0.0109041305),
        ("YES", -4.526867),
        ("_NO", -11.64584),
        ("是", -12.182035),
        ("不", -12.272516),
    ];

    [Fact]
    public void ParseYesNo_FromMeasuredLogProbs_GivesProbabilityAndAnswer()
    {
        var (p, answer) = AgentCatalog.ParseYesNo(MeasuredNoDominant, "NO");

        Assert.NotNull(p);
        Assert.InRange(p!.Value, 0.005, 0.02);   // 实测约 0.011
        Assert.False(answer);
    }

    [Fact]
    public void ParseYesNo_YesDominant_CrossesHalf()
    {
        var tokens = new (string, double)[] { ("YES", -0.05), ("NO", -3.2) };

        var (p, answer) = AgentCatalog.ParseYesNo(tokens, "YES");

        Assert.NotNull(p);
        Assert.True(p!.Value > 0.9);
        Assert.True(answer);
    }

    [Fact]
    public void ParseYesNo_ChineseCandidatesAndDecoratedTokens_AreRecognized()
    {
        // 中文「是/否」+ 变体（带下划线前缀 / 尾随标点）都要能识别 —— 旧的 StartsWith("YES") 会把「是」当成「否」
        Assert.Equal(1, AgentCatalog.NormalizeVote(" 是"));
        Assert.Equal(1, AgentCatalog.NormalizeVote("_YES"));
        Assert.Equal(1, AgentCatalog.NormalizeVote("Yes."));
        Assert.Equal(-1, AgentCatalog.NormalizeVote("否"));
        Assert.Equal(-1, AgentCatalog.NormalizeVote("NO！"));
        Assert.Equal(0, AgentCatalog.NormalizeVote("MAYBE"));

        var (pYes, answer) = AgentCatalog.ParseYesNo([("是", -0.1), ("否", -2.5)], "是");
        Assert.True(pYes > 0.9);
        Assert.True(answer);
    }

    [Fact]
    public void ParseYesNo_NoLogProbs_FallsBackToText_NeverSilentlyNo()
    {
        // 没有概率时退回文本：中文「是」也算“是”（旧实现只认 YES 前缀）
        Assert.Equal((null, true), AgentCatalog.ParseYesNo([], "YES"));
        Assert.Equal((null, true), AgentCatalog.ParseYesNo([], "是"));
        Assert.Equal((null, false), AgentCatalog.ParseYesNo([], "NO."));
        Assert.Equal((null, false), AgentCatalog.ParseYesNo([], "不应该"));
        // 认不出 → null（让调用方“不猜”，而不是默默当“否”）
        Assert.Equal((null, null), AgentCatalog.ParseYesNo([], ""));
        Assert.Equal((null, null), AgentCatalog.ParseYesNo([], null));
        Assert.Equal((null, null), AgentCatalog.ParseYesNo([], "MAYBE LATER")); // 不能因为里面有 Y 就当“是”
        Assert.Equal((null, null), AgentCatalog.ParseYesNo([], "也许可以"));
        Assert.Equal((null, null), AgentCatalog.ParseYesNo([], "不确定"));
    }

    [Fact]
    public void ParseYesNo_IrrelevantTopTokens_FallBackToText()
    {
        // 候选里既没有“是”也没有“否”（例如模型直接开始写别的）→ 概率不可得，交给文本路径
        var (p, answer) = AgentCatalog.ParseYesNo([("好的", -0.2), ("我来", -3.0)], "好的");
        Assert.Null(p);
        Assert.Null(answer);
    }

    // ============ ③ 指派路由的解析与“空输出 ≠ NONE”============

    private static readonly string[] Candidates = ["agent_a", "agent_b", "agent_c"];

    [Fact]
    public void ParseAssignTargets_EmptyOutput_IsNotTreatedAsNone()
    {
        // 空输出 → 空列表；调用方据“原始文本是否为空”区分“模型没说”与“模型说 NONE”。
        // 实测：思考模型预算 64 时正是「空输出」，旧代码把它当成“无候选”而静默不指派。
        Assert.Empty(AgentGateway.ParseAssignTargets("", Candidates));
        Assert.Empty(AgentGateway.ParseAssignTargets("   ", Candidates));
        Assert.Empty(AgentGateway.ParseAssignTargets(null, Candidates));
    }

    [Fact]
    public void ParseAssignTargets_None_And_WhitelistFiltering()
    {
        Assert.Empty(AgentGateway.ParseAssignTargets("NONE", Candidates));
        Assert.Equal(["agent_a"], AgentGateway.ParseAssignTargets("agent_a", Candidates));
        Assert.Equal(["agent_a", "agent_c"], AgentGateway.ParseAssignTargets("agent_a， agent_c", Candidates)); // 中文逗号
        // 白名单外与重复项：过滤 / 去重，保序
        Assert.Equal(["agent_b"], AgentGateway.ParseAssignTargets("agent_b,agent_x,agent_b", Candidates));
    }
}
