using AguiGroupChat.Agents;
using AguiGroupChat.Agents.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>
/// 技能执行预算：内置文档类技能要比用户自建技能给得更长。
///
/// <para>
/// 起因是一次真实故障：PPT 技能用 <c>imageQuery</c> 联网取照片，3 张就撞上
/// <see cref="DotnetSkillHost"/> 默认的 10 秒上限，返回「.NET 技能执行超时」——
/// 结果是<b>整份稿子都没有</b>，比降级成题图差得多。
/// </para>
///
/// <para>
/// 这里用一个真的会 sleep 的技能来验，不依赖网络：预算小时必须超时，预算大时必须成功。
/// </para>
/// </summary>
public sealed class SkillTimeoutBudgetTests
{
    /// <summary>睡 2.5 秒再返回的技能：用来把“超时 / 不超时”钉死。</summary>
    private const string SlowSkill =
        "public class Skill { public static string Run(string input) "
        + "{ System.Threading.Thread.Sleep(2500); return \"done:\" + input; } }";

    private static SkillRunner Runner(int dotnetMs, int builtinMs)
        => new SkillRunner(
            Path.Combine(Path.GetTempPath(), "agui-skill-timeout-" + Guid.NewGuid().ToString("N")),
            NullLoggerFactory.Instance,
            dotnetTimeoutMs: dotnetMs,
            builtinTimeoutMs: builtinMs);

    /// <summary>用户自建技能：用 <c>dotnetTimeoutMs</c>。</summary>
    private static AgentSkillDefinition UserSkill(string body)
        => new() { SkillId = "slow_user", Name = "slow_user", Kind = AgentSkillKind.Dotnet, Body = body };

    /// <summary>内置技能：特征就是 <c>BuiltinVersion</c> 非空（与库里内置技能一致）。</summary>
    private static AgentSkillDefinition BuiltinSkill(string body)
        => new()
        {
            SkillId = "slow_builtin", Name = "slow_builtin", Kind = AgentSkillKind.Dotnet, Body = body,
            BuiltinVersion = "2026-09-18.1",
        };

    [Fact]
    public async Task BuiltinSkill_GetsTheLongerBudget()
    {
        // 内置预算 8 秒 > 技能耗时 2.5 秒 → 应当跑完（用户预算是 1 毫秒，若走错就会超时）
        var runner = Runner(dotnetMs: 1, builtinMs: 8_000);
        var res = await runner.InvokeAsync(BuiltinSkill(SlowSkill), "x", CancellationToken.None);
        Assert.Contains("done:x", res);
        Assert.DoesNotContain("超时", res);
    }

    [Fact]
    public async Task UserSkill_StillUsesTheShortBudget()
    {
        // 用户预算 1 毫秒 → 必须超时（证明没有把长预算顺手给了所有技能）
        var runner = Runner(dotnetMs: 1, builtinMs: 8_000);
        var res = await runner.InvokeAsync(UserSkill(SlowSkill), "x", CancellationToken.None);
        Assert.Contains("超时", res);
    }

    [Fact]
    public void Defaults_KeepUserSkillsAtTenSeconds()
    {
        var options = new AgentOptions();
        Assert.Equal(10_000, options.DotnetSkillTimeoutMs);
        Assert.True(options.BuiltinSkillTimeoutMs >= 60_000,
            "内置文档技能会联网取图，预算不能低于 60 秒");
    }

    // ===================== 复杂度自适应（RunTimeoutPolicy.Ambient） =====================

    /// <summary>一次“繁重”任务的运行预算（内置技能预算 8 秒 × 3 = 24 秒）。</summary>
    private static RunTimeoutBudget HeavyBudget(int builtinSkillMs)
        => RunTimeoutPolicy.Build(
            "请给我做一份41页的PPT推广方案，要完整详细，先做大纲再逐页写，最后配图", null, false,
            new ExecutionOptions { StreamTimeoutMinutes = 5 },
            new AgentOptions { BuiltinSkillTimeoutMs = builtinSkillMs });

    [Fact]
    public async Task BuiltinSkill_BudgetIsRaisedByComplexityBudget()
    {
        // 内置预算 1 毫秒（单看必然超时），但本次运行是繁重任务 → 环境预算把它抬到 24 秒。
        var previous = RunTimeoutPolicy.Ambient;
        try
        {
            var budget = HeavyBudget(builtinSkillMs: 8_000);
            Assert.Equal(24_000, budget.SkillTimeoutMs);
            RunTimeoutPolicy.Ambient = budget;

            var runner = Runner(dotnetMs: 1, builtinMs: 1);
            var res = await runner.InvokeAsync(BuiltinSkill(SlowSkill), "x", CancellationToken.None);
            Assert.Contains("done:x", res);
            Assert.DoesNotContain("超时", res);
        }
        finally
        {
            RunTimeoutPolicy.Ambient = previous;
        }
    }

    [Fact]
    public async Task UserSkill_IsNotRaisedByComplexityBudget()
    {
        // 复杂度自适应该只放宽“内置文档类技能”：用户自建技能的主文不受我们控制，仍走短预算。
        var previous = RunTimeoutPolicy.Ambient;
        try
        {
            RunTimeoutPolicy.Ambient = HeavyBudget(builtinSkillMs: 8_000);

            var runner = Runner(dotnetMs: 1, builtinMs: 1);
            var res = await runner.InvokeAsync(UserSkill(SlowSkill), "x", CancellationToken.None);
            Assert.Contains("超时", res);
        }
        finally
        {
            RunTimeoutPolicy.Ambient = previous;
        }
    }
}
