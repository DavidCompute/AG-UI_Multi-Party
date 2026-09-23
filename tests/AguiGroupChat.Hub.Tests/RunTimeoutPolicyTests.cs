using AguiGroupChat.Agents;
using AguiGroupChat.Hub.Models;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>
/// 复杂度自适应超时（<see cref="RunComplexityEstimator"/> / <see cref="RunTimeoutPolicy"/>）单测。
///
/// <para>
/// 目的：钉住两条不变量——
/// 1）估算器是<b>确定性纯函数</b>（同一输入永远同一档位；恢复运行重算得到同一预算的前提）；
/// 2）预算只“放宽”不“收紧”（任何档位 / 任何夹紧都不会低于运营者原本配的 StreamTimeoutMinutes 与技能预算）。
/// </para>
/// </summary>
public sealed class RunTimeoutPolicyTests
{
    /// <summary>真实踩过的请求：“一次提交 41 页 + 长备注，服务端处理超限”——估算应达繁重档。</summary>
    private const string HeavyRequest = "请给我做一份41页的PPT推广方案，要完整详细，先做大纲再逐页写，最后配图";

    /// <summary>只有“交付物格式”一个信号 → 常规档。</summary>
    private const string StandardRequest = "帮我写一份市场推广文案，要 word 文档";

    // ===================== 复杂度估算 =====================

    [Fact]
    public void Estimate_ChitChat_IsSimple()
    {
        var (tier, score, reasons) = RunComplexityEstimator.Estimate("你好，今天天气怎么样？", null);
        Assert.Equal(RunComplexity.Simple, tier);
        Assert.Equal(0, score);
        Assert.Empty(reasons);
    }

    [Fact]
    public void Estimate_ShortDeliverableRequest_IsStandard()
    {
        // 只有“交付物格式”一个信号（+2）→ 常规档，不该因为提到 word 就直接跳到繁重。
        var (tier, score, _) = RunComplexityEstimator.Estimate(StandardRequest, null);
        Assert.Equal(RunComplexity.Standard, tier);
        Assert.Equal(2, score);
    }

    [Fact]
    public void Estimate_FortyOnePageDeck_IsHeavy()
    {
        var (tier, score, reasons) = RunComplexityEstimator.Estimate(HeavyRequest, null);
        Assert.Equal(RunComplexity.Heavy, tier);
        Assert.True(score >= 7, $"应达繁重档，实际 {score}");
        Assert.Contains(reasons, r => r.Contains("41 页"));
    }

    [Theory]
    [InlineData("做 3 页", RunComplexity.Simple)]      // 页数 +1，未达常规档
    [InlineData("做 8 页", RunComplexity.Standard)]    // 页数 +2
    [InlineData("做 25 页", RunComplexity.Standard)]   // 页数 +3
    public void Estimate_PageCountThresholds(string content, RunComplexity expected)
        => Assert.Equal(expected, RunComplexityEstimator.Estimate(content, null).Tier);

    [Theory]
    [InlineData("写 400 字", RunComplexity.Simple)]      // +1，未达常规档
    [InlineData("写 2000 字", RunComplexity.Standard)]   // +2
    [InlineData("写 6000 字", RunComplexity.Standard)]   // +3
    [InlineData("写 6k 字", RunComplexity.Standard)]     // 千字写法等价于 6000 字
    public void Estimate_LengthThresholds(string content, RunComplexity expected)
        => Assert.Equal(expected, RunComplexityEstimator.Estimate(content, null).Tier);

    [Fact]
    public void Estimate_MultiStepAndExhaustive_StackUp()
    {
        // 多步措辞 3 处（上限）+ 穷尽性 1 → 4 分 → 复杂档。
        var (tier, score, _) = RunComplexityEstimator.Estimate("先列大纲，然后逐条展开，最后分别给出结论，要全面", null);
        Assert.Equal(RunComplexity.Complex, tier);
        Assert.Equal(4, score);
    }

    [Fact]
    public void Estimate_MultiStepMarkersAreCapped()
    {
        var (_, longScore, _) = RunComplexityEstimator.Estimate(
            "先做A，然后做B，接着做C，最后做D，再分别逐条逐项逐页逐张每个都写一遍", null);
        // 多步计分上限为 3：无论写多少个标记都不该继续叠加。
        Assert.Equal(3, longScore);
    }

    [Fact]
    public void Estimate_DocumentAttachments_AddWeight()
    {
        var attachments = new List<AttachmentInfo>
        {
            Attachment("a1", "document", 1_000),
            Attachment("a2", "text", 1_000),
        };
        var (tier, score, reasons) = RunComplexityEstimator.Estimate("请据此整理", attachments);
        Assert.Equal(RunComplexity.Standard, tier);
        Assert.Equal(2, score);
        Assert.Contains(reasons, r => r.Contains("2 个文档附件"));
    }

    [Fact]
    public void Estimate_LargeAttachment_AddsExtraWeight()
    {
        var (_, score, reasons) = RunComplexityEstimator.Estimate("请据此整理",
            [Attachment("a1", "document", 3 * 1024 * 1024)]);
        Assert.Equal(2, score); // 文档附件 +1，≥2MB +1
        Assert.Contains(reasons, r => r.Contains("≥2MB"));
    }

    [Fact]
    public void Estimate_Images_NeedFiveToCount()
    {
        var four = Enumerable.Range(0, 4).Select(i => Attachment($"i{i}", "image", 100)).ToList();
        Assert.Equal(0, RunComplexityEstimator.Estimate("看图", four).Score);

        var five = Enumerable.Range(0, 5).Select(i => Attachment($"i{i}", "image", 100)).ToList();
        Assert.Equal(1, RunComplexityEstimator.Estimate("看图", five).Score);
    }

    [Fact]
    public void Estimate_FanOutRole_AddsWeight()
    {
        Assert.Equal(1, RunComplexityEstimator.Estimate("你好", null, fanOut: true).Score);
        // 单靠“会向下指派”不应把寒暄推到常规档以上。
        Assert.Equal(RunComplexity.Simple, RunComplexityEstimator.Estimate("你好", null, fanOut: true).Tier);
    }

    [Fact]
    public void Estimate_IgnoresTextBeyondPrefix()
    {
        // 长消息后段多为粘贴的素材，不参与量级判断：2000 字之后的“100页报告”不该被计入。
        var content = "你好" + new string('x', 5_000) + " 请做一份100页报告";
        var (tier, score, _) = RunComplexityEstimator.Estimate(content, null);
        Assert.Equal(RunComplexity.Simple, tier);
        Assert.Equal(0, score);
    }

    [Fact]
    public void Estimate_IsDeterministic()
    {
        // 恢复运行时会用同一份触发上下文重算，因此必须得到完全相同的档位与依据。
        var first = RunComplexityEstimator.Estimate(HeavyRequest, null);
        var second = RunComplexityEstimator.Estimate(HeavyRequest, null);
        Assert.Equal(first.Tier, second.Tier);
        Assert.Equal(first.Score, second.Score);
        Assert.Equal(first.Reasons, second.Reasons);
    }

    // ===================== 预算换算 =====================

    [Fact]
    public void Build_HeavyTask_ScalesRunBudgetByMultiplier()
    {
        var budget = Build(HeavyRequest, 5);
        Assert.Equal(RunComplexity.Heavy, budget.Complexity);
        Assert.Equal(TimeSpan.FromMinutes(15), budget.Run); // 5 × 3
    }

    [Fact]
    public void Build_SimpleTask_KeepsBaseBudget()
        => Assert.Equal(TimeSpan.FromMinutes(5), Build("你好", 5).Run);

    [Fact]
    public void Build_Disabled_KeepsBaseBudgetAndNeutralBudget()
    {
        var exec = new ExecutionOptions
        {
            StreamTimeoutMinutes = 5,
            ComplexityTimeouts = new ComplexityTimeoutOptions { Enabled = false },
        };
        var budget = RunTimeoutPolicy.Build(
            HeavyRequest, null, false, exec, new AgentOptions());

        Assert.Equal(RunComplexity.Simple, budget.Complexity);
        Assert.Equal(1, budget.Multiplier);
        Assert.Equal(TimeSpan.FromMinutes(5), budget.Run);
        Assert.Equal(new AgentOptions().BuiltinSkillTimeoutMs, budget.SkillTimeoutMs);
        Assert.Equal(exec.MaxAutoApprovedRounds, budget.MaxAutoApprovedRounds);
    }

    [Fact]
    public void Build_ClampsToCeiling()
    {
        var exec = new ExecutionOptions
        {
            StreamTimeoutMinutes = 5,
            ComplexityTimeouts = new ComplexityTimeoutOptions { HeavyMultiplier = 10, MaxRunTimeoutMinutes = 12 },
        };
        var budget = RunTimeoutPolicy.Build(HeavyRequest, null, false, exec, new AgentOptions());
        Assert.Equal(TimeSpan.FromMinutes(12), budget.Run); // 5×10 被 12 分钟夹住
    }

    [Fact]
    public void Build_CeilingNeverShrinksBelowBaseBudget()
    {
        // 配置只“放宽”不“收紧”：Max 小于 StreamTimeoutMinutes 时以 StreamTimeoutMinutes 为准。
        var exec = new ExecutionOptions
        {
            StreamTimeoutMinutes = 20,
            ComplexityTimeouts = new ComplexityTimeoutOptions { MaxRunTimeoutMinutes = 3 },
        };
        Assert.Equal(TimeSpan.FromMinutes(20), RunTimeoutPolicy.Build("你好", null, false, exec, new AgentOptions()).Run);
    }

    [Fact]
    public void Build_ScalesBuiltinSkillTimeout()
    {
        var options = new AgentOptions { BuiltinSkillTimeoutMs = 60_000 };
        var budget = Build(HeavyRequest, 5, options);
        Assert.Equal(180_000, budget.SkillTimeoutMs); // 60s × 3
    }

    [Fact]
    public void Build_SkillTimeoutIsClampedByMax()
    {
        var options = new AgentOptions { BuiltinSkillTimeoutMs = 60_000 };
        var exec = new ExecutionOptions
        {
            StreamTimeoutMinutes = 5,
            ComplexityTimeouts = new ComplexityTimeoutOptions { MaxSkillTimeoutMs = 100_000 },
        };
        var budget = RunTimeoutPolicy.Build("请给我做一份41页的PPT推广方案", null, false, exec, options);
        Assert.Equal(100_000, budget.SkillTimeoutMs);
    }

    [Fact]
    public void Build_NeverShrinksSkillTimeout()
    {
        // 即便档位是“简单”，技能预算也不低于既有值（复杂度只放宽，不收紧）。
        var options = new AgentOptions { BuiltinSkillTimeoutMs = 60_000 };
        Assert.Equal(60_000, Build("你好", 5, options).SkillTimeoutMs);
    }

    [Fact]
    public void Build_ScalesClientSkillCeiling()
    {
        Assert.Equal(180 * 3, Build(HeavyRequest, 5).ClientSkillTimeoutSec);
        Assert.Equal(180, Build("你好", 5).ClientSkillTimeoutSec);
    }

    [Fact]
    public void Scale_AppliesStageFactor_AndClampsToCeiling()
    {
        // 指派链 ×3：5 × 3(繁重) × 3(阶段) = 45，被 30 分钟上界夹住。
        Assert.Equal(TimeSpan.FromMinutes(30), Build(HeavyRequest, 5).Scale(3));

        // 常规任务 ×3 仍在界内：5 × 1.5 × 3 = 22.5。
        Assert.Equal(TimeSpan.FromMinutes(22.5), Build(StandardRequest, 5).Scale(3));
    }

    [Fact]
    public void Budget_Describe_MentionsTierAndMultiplying()
    {
        var text = Build(HeavyRequest, 5).Describe();
        Assert.Contains("Heavy", text);
        Assert.Contains("41 页", text);
    }

    // ===================== 配置夹紧 =====================

    [Fact]
    public void ComplexityOptions_Defaults()
    {
        var d = ComplexityTimeoutOptions.Default;
        Assert.True(d.Enabled);
        Assert.Equal(1.5, d.StandardMultiplier);
        Assert.Equal(2, d.ComplexMultiplier);
        Assert.Equal(3, d.HeavyMultiplier);
        Assert.Equal(30, d.MaxRunTimeoutMinutes);
        Assert.Equal(300_000, d.MaxSkillTimeoutMs);
        Assert.Equal(600, d.MaxClientSkillTimeoutSec);
        Assert.Equal(40, d.AutoApprovedStandard);
        Assert.Equal(60, d.AutoApprovedComplex);
        Assert.Equal(100, d.AutoApprovedHeavy);
    }

    [Fact]
    public void AutoApprovedLimit_SimpleTier_DoesNotOverride()
    {
        // 简单档不覆盖：由运营者基准值（MaxAutoApprovedRounds）拿主意。
        Assert.Equal(0, ComplexityTimeoutOptions.Default.AutoApprovedLimit(RunComplexity.Simple));
    }

    [Fact]
    public void ExecutionOptions_Normalize_ClampsComplexityTimeouts()
    {
        var exec = new ExecutionOptions
        {
            ComplexityTimeouts = new ComplexityTimeoutOptions
            {
                HeavyMultiplier = 0.2,      // 小于 1（会“收紧”超时）→ 回退默认
                StandardMultiplier = 99,    // 超上界 → 回退默认
                MaxRunTimeoutMinutes = -5,  // 非法 → 回退默认
                MaxSkillTimeoutMs = 1,      // 低于下限 → 回退默认
                MaxClientSkillTimeoutSec = 0,
                AutoApprovedStandard = 0,
                AutoApprovedComplex = -3,
                AutoApprovedHeavy = 99_999_999, // 超上界 → 回退默认
            },
        };
        exec.Normalize();

        Assert.Equal(1.5, exec.ComplexityTimeouts.StandardMultiplier);
        Assert.Equal(3, exec.ComplexityTimeouts.HeavyMultiplier);
        Assert.Equal(30, exec.ComplexityTimeouts.MaxRunTimeoutMinutes);
        Assert.Equal(300_000, exec.ComplexityTimeouts.MaxSkillTimeoutMs);
        Assert.Equal(600, exec.ComplexityTimeouts.MaxClientSkillTimeoutSec);
        Assert.Equal(40, exec.ComplexityTimeouts.AutoApprovedStandard);
        Assert.Equal(60, exec.ComplexityTimeouts.AutoApprovedComplex);
        Assert.Equal(100, exec.ComplexityTimeouts.AutoApprovedHeavy);
    }

    [Fact]
    public void ExecutionOptions_Normalize_KeepsValidComplexityTimeouts()
    {
        var exec = new ExecutionOptions
        {
            ComplexityTimeouts = new ComplexityTimeoutOptions
            {
                StandardMultiplier = 2,
                ComplexMultiplier = 3,
                HeavyMultiplier = 4,
                MaxRunTimeoutMinutes = 45,
                MaxSkillTimeoutMs = 240_000,
                MaxClientSkillTimeoutSec = 300,
            },
        };
        exec.Normalize();

        Assert.Equal(2, exec.ComplexityTimeouts.StandardMultiplier);
        Assert.Equal(4, exec.ComplexityTimeouts.HeavyMultiplier);
        Assert.Equal(45, exec.ComplexityTimeouts.MaxRunTimeoutMinutes);
        Assert.Equal(240_000, exec.ComplexityTimeouts.MaxSkillTimeoutMs);
        Assert.Equal(300, exec.ComplexityTimeouts.MaxClientSkillTimeoutSec);
    }

    [Fact]
    public void ExecutionOptions_Normalize_ToleratesNullComplexityTimeouts()
    {
        var exec = new ExecutionOptions { ComplexityTimeouts = null! };
        exec.Normalize();
        Assert.NotNull(exec.ComplexityTimeouts);
        Assert.True(exec.ComplexityTimeouts.Enabled);
    }

    // ===================== 自动放行上限定档 =====================

    [Fact]
    public void Build_SimpleTask_KeepsBaseAutoApprovedLimit()
        => Assert.Equal(30, Build("你好", 5).MaxAutoApprovedRounds);

    [Fact]
    public void Build_AutoApprovedLimit_FollowsTier()
    {
        // 默认基准 30：常规档 40、复杂档 60、繁重档 100，全部高于基准。
        Assert.Equal(40, Build(StandardRequest, 5).MaxAutoApprovedRounds);
        Assert.Equal(60, Build("先列大纲，然后逐条展开，最后分别给出结论，要全面", 5).MaxAutoApprovedRounds);
        Assert.Equal(100, Build(HeavyRequest, 5).MaxAutoApprovedRounds);
    }

    [Fact]
    public void Build_AutoApprovedLimit_IsMonotoneWithTier()
    {
        var simple = Build("你好", 5).MaxAutoApprovedRounds;
        var standard = Build(StandardRequest, 5).MaxAutoApprovedRounds;
        var heavy = Build(HeavyRequest, 5).MaxAutoApprovedRounds;
        Assert.True(simple <= standard && standard < heavy, $"档位越高额度应不减：{simple}/{standard}/{heavy}");
    }

    [Fact]
    public void Build_AutoApprovedLimit_Disabled_FallsBackToBaseValue()
    {
        // 关闭自适应 → 与旧版行为等价：只用运营者配的基准值。
        var exec = new ExecutionOptions
        {
            StreamTimeoutMinutes = 5,
            MaxAutoApprovedRounds = 25,
            ComplexityTimeouts = new ComplexityTimeoutOptions { Enabled = false },
        };
        var budget = RunTimeoutPolicy.Build(HeavyRequest, null, false, exec, new AgentOptions());
        Assert.Equal(25, budget.MaxAutoApprovedRounds);
    }

    [Fact]
    public void Build_AutoApprovedLimit_NeverNarrowsConfiguredBase()
    {
        // 只放宽不收紧：运营者把基准值调高过档位值时，以运营者的为准。
        var exec = new ExecutionOptions { StreamTimeoutMinutes = 5, MaxAutoApprovedRounds = 250 };
        var budget = RunTimeoutPolicy.Build(HeavyRequest, null, false, exec, new AgentOptions());
        Assert.Equal(250, budget.MaxAutoApprovedRounds);
    }

    [Fact]
    public void Build_AutoApprovedLimit_CanBreakThroughBaseValue()
    {
        // 关键的取舍：档位值必须能突破基准值，否则“自适应”没有意义。
        var exec = new ExecutionOptions { StreamTimeoutMinutes = 5, MaxAutoApprovedRounds = 10 };
        var budget = RunTimeoutPolicy.Build(HeavyRequest, null, false, exec, new AgentOptions());
        Assert.Equal(100, budget.MaxAutoApprovedRounds);
    }

    [Fact]
    public void Budget_Describe_MentionsAutoApprovedLimit()
    {
        var text = Build(HeavyRequest, 5).Describe();
        Assert.Contains("自动放行≤100", text);
    }

    // ===================== 环境预算传播 =====================

    [Fact]
    public async Task Install_ExposesBudgetToAmbientAcrossAsyncFlow()
    {
        var exec = new ExecutionOptions { StreamTimeoutMinutes = 5 };
        var options = new AgentOptions();
        var previous = RunTimeoutPolicy.Ambient;
        try
        {
            var budget = RunTimeoutPolicy.Install("你好", null, false, exec, options);
            // 环境预算必须能穿过 await 边界（技能宿主在模型工具调用内部读取它）。
            await Task.Yield();
            Assert.Same(budget, RunTimeoutPolicy.Ambient);
            Assert.Equal(TimeSpan.FromMinutes(5), RunTimeoutPolicy.Ambient!.Run);
        }
        finally
        {
            RunTimeoutPolicy.Ambient = previous;
        }
    }

    [Fact]
    public async Task Install_InsideChildFlow_DoesNotLeakBackToCaller()
    {
        // 每个运行是独立的异步流：子流程里安装的预算不该污染调用方（否则并发运行会串味）。
        var previous = RunTimeoutPolicy.Ambient;
        try
        {
            await InstallInChildFlowAsync();
            Assert.Null(RunTimeoutPolicy.Ambient);
        }
        finally
        {
            RunTimeoutPolicy.Ambient = previous;
        }

        static async Task InstallInChildFlowAsync()
        {
            await Task.Yield();
            var budget = RunTimeoutPolicy.Install("你好", null, false,
                new ExecutionOptions { StreamTimeoutMinutes = 5 }, new AgentOptions());
            Assert.NotNull(RunTimeoutPolicy.Ambient);
            Assert.Equal(TimeSpan.FromMinutes(5), budget.Run);
        }
    }

    // ===================== 辅助 =====================

    private static RunTimeoutBudget Build(string content, int baseMinutes, AgentOptions? options = null)
        => RunTimeoutPolicy.Build(content, null, false,
            new ExecutionOptions { StreamTimeoutMinutes = baseMinutes }, options ?? new AgentOptions());

    private static AttachmentInfo Attachment(string id, string kind, long size)
        => new() { AttachmentId = id, Name = id, ContentType = "application/octet-stream", Size = size, Url = "/f/" + id, Kind = kind };
}
