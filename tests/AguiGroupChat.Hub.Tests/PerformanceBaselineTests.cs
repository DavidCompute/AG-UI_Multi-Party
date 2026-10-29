using System.Diagnostics;
using AguiGroupChat.Agents;
using AguiGroupChat.Hub.Agents;
using AguiGroupChat.Hub.Models;
using AguiGroupChat.Hub.Storage;
using AguiGroupChat.Web;
using Xunit;
using Xunit.Abstractions;

namespace AguiGroupChat.Hub.Tests;

/// <summary>
/// 性能 / 容量基线（1.0.368）：把近期新增的热路径耗时「钉住」——
/// 用<b>存储层上限</b>的数据量跑，阈值取实测的几十倍，只用于捕捉“数量级劣化”，
/// 不做精确基准（CI 机器差异大，紧阈值只会变成随机红灯）。
///
/// <para>
/// 覆盖：闭环聚合与健康度（2000 事件上限）、产物质量画像（5000 事件上限）、
/// 编排候选构造（200 技能）、组织体检报告（200 岗位 + 闭环 + 巡检）、
/// 记忆冲突检测与存量扫描（500/800 条）。
/// </para>
/// </summary>
public sealed class PerformanceBaselineTests
{
    private const int LoopCap = 2000;       // SkillLoopEventStore.MaxEvents
    private const int ArtifactCap = 5000;   // ArtifactQualityStore 上限
    private readonly ITestOutputHelper _out;

    public PerformanceBaselineTests(ITestOutputHelper output) => _out = output;

    private static SkillLoopEvent LoopEvt(int i, long now)
    {
        // 用 i%7 而不是 i%5：60 能被 5 整除，会让 12 个技能“全是 skipped”而被健康度排除。
        var outcome = (i % 7) switch
        {
            0 => SkillLoopOutcomes.Applied,
            1 => SkillLoopOutcomes.Resumed,
            2 => SkillLoopOutcomes.RolledBack,
            3 => SkillLoopOutcomes.Failed,
            4 => SkillLoopOutcomes.Applied,
            5 => SkillLoopOutcomes.Resumed,
            _ => SkillLoopOutcomes.Skipped,
        };
        return new SkillLoopEvent($"lp_{i}", $"sk_{i % 60}", outcome, 2, 100 + i % 900,
            i % 3, i % 2 == 0, now - (i * 1000L))
        {
            Source = i % 4 == 0 ? SkillRepairSources.Auto : SkillRepairSources.Manual,
        };
    }

    private static ArtifactQualityEvent QualityEvt(int i, long now) => new(
        $"a{i % 40}", $"sk_{i % 60}", "docx", 55 + i % 45, i % 4 != 0,
        i % 7 == 0 ? ["缺少正文"] : [], "g1", now - i * 1000L);

    /// <summary>跑 <paramref name="runs"/> 次取最小耗时，断言低于 <paramref name="budgetMs"/>。</summary>
    private void Guard(string name, int budgetMs, int runs, Action action)
    {
        action(); // 预热（JIT / 静态初始化不算进基线）
        var best = long.MaxValue;
        for (var i = 0; i < Math.Max(1, runs); i++)
        {
            var sw = Stopwatch.StartNew();
            action();
            sw.Stop();
            best = Math.Min(best, sw.ElapsedMilliseconds);
        }
        _out.WriteLine($"{name}: {best}ms（预算 {budgetMs}ms）");
        Assert.True(best < budgetMs, $"{name} 耗时 {best}ms，超过预算 {budgetMs}ms（疑似数量级劣化）");
    }

    [Fact]
    public void LoopAggregation_AtStoreCap()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var events = Enumerable.Range(0, LoopCap).Select(i => LoopEvt(i, now)).ToList();

        Guard("loop.summarize+bySkill(2000)", 400, 3, () =>
        {
            var summary = SkillLoopMetrics.Summarize(events);
            var rows = SkillLoopMetrics.BySkill(events);
            Assert.Equal(LoopCap, summary.Attempts);
            Assert.Equal(60, rows.Count);
        });
    }

    [Fact]
    public void LoopHealthAndPolicies_AtStoreCap()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var events = Enumerable.Range(0, LoopCap).Select(i => LoopEvt(i, now)).ToList();

        Guard("loop.health+alert+autorepair(2000)", 400, 3, () =>
        {
            Assert.Equal(60, SkillLoopHealthBoard.Build(events, now).Count);
            _ = SkillLoopAlertPolicy.Evaluate(events);
            _ = SkillAutoRepairPolicy.Evaluate(events, now);
        });
    }

    [Fact]
    public void ArtifactQualityProfiles_AtStoreCap()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var events = Enumerable.Range(0, ArtifactCap).Select(i => QualityEvt(i, now)).ToList();

        Guard("artifact.profiles+alert(5000)", 600, 3, () =>
        {
            var profiles = SkillQualityProfiles.Build(events);
            Assert.Equal(60, profiles.Count);
            _ = ArtifactAlertPolicy.Evaluate(events.Where(e => e.SkillId == "sk_1").ToList());
        });
    }

    [Fact]
    public void OrchestrationCandidates_WithAllNotes()
    {
        var skills = Enumerable.Range(0, 200)
            .Select(i => new AgentSkillDefinition
            {
                SkillId = $"sk_{i}", Name = $"技能{i}", Description = "生成文档与报告，支持多种模板",
                Kind = AgentSkillKind.Prompt, Body = "模板",
            })
            .ToList();
        var quality = skills.ToDictionary(s => s.SkillId,
            s => new SkillQualityProfile(s.SkillId, 4, 3, 0.75, 82, 1, ["缺少正文"], 1_700_000_000_000L),
            StringComparer.Ordinal);
        var lessons = new SkillLessonStore();
        foreach (var s in skills.Take(50))
            lessons.Add(new SkillLesson($"sl_{s.SkillId}", s.SkillId, null, SkillLessonKinds.Failure,
                "连续 2/3 次产物未达标", ["缺少正文"], "先备齐正文", "g", 1_700_000_000_000L, false));
        var health = skills.ToDictionary(s => s.SkillId,
            s => new SkillLoopHealth(s.SkillId, 2, 0, 2, 0, 0), StringComparer.Ordinal);

        Guard("orchestrator.candidates(200)", 600, 3, () =>
        {
            var reusable = AgentOrchestrator.ToReusableSkills(skills, quality, lessons, health);
            Assert.Equal(200, reusable.Count);
            var prompt = AgentOrchestrator.BuildPromptForTest("做一个文案推广组，要出 Word", reusable);
            Assert.Contains("修复健康", prompt);
        });
    }

    [Fact]
    public void OrgHealthReport_FullSections()
    {
        var stats = Enumerable.Range(0, 200)
            .Select(i => new AgentPerformance.AgentStat($"a{i}", $"员工{i}", 100 + i, 1000, 500, 0, 1500,
                8, 2, "2026-10-10", ArtifactRuns: 5, ArtifactValid: 4, ArtifactAvgScore: 82, ArtifactLow: 1))
            .ToList();
        var defs = stats.ToDictionary(s => s.AgentId, s => new AgentDefinition
        {
            AgentId = s.AgentId, Nickname = s.Name, SkillDefIds = [$"sk_{s.AgentId}"],
            AssignmentIds = [stats[0].AgentId], EscalationAgentId = stats[0].AgentId,
        });
        var pricing = new PricingOptions { InputPerMillion = 2m, OutputPerMillion = 8m, Currency = "$" };
        var evalCases = Enumerable.Range(0, 100)
            .Select(i => new EvalCase { CaseId = $"e{i}", Name = $"用例{i}", AgentId = stats[i % 200].AgentId, LastPassed = i % 3 != 0, LastRunAtMs = 1_700_000_000_000 })
            .ToList();
        var loop = new SkillLoopSummary(100, 60, 10, 10, 20, 0, 0.7, 4200, 40, 35);
        var loopRows = Enumerable.Range(0, 60).Select(i => new OrgHealthReport.SkillLoopRow($"技能{i}", 5, 3, 1, 1, 3200)).ToList();
        var healthRows = Enumerable.Range(0, 50).Select(i => new OrgHealthReport.SkillHealthRow($"员工{i}", $"sk_{i}", $"技能{i}", 3, 0, 2, 1, 0.33, "avoid")).ToList();

        Guard("orghealth.markdown(200 agents, 100 eval, 60 loop, 50 health)", 800, 3, () =>
        {
            var md = OrgHealthReport.BuildMarkdown(30, "2026-09-11", "2026-10-10", stats, defs, pricing,
                1_700_000_000_000, evalCases, stats, loop, loopRows, healthRows);
            Assert.Contains("技能健康巡检", md);
        });
    }

    [Fact]
    public void MemoryConflictDetection_AtApiLimits()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var candidates = Enumerable.Range(0, 6)
            .Select(i => new MemoryConflictCandidate($"m{i}", $"项目负责人是员工{i}号", now - (i + 1) * 1000L, 0.95))
            .ToList();

        Guard("memory.detect(6 candidates)", 50, 5, () =>
        {
            var hits = MemoryConflictDetector.Detect("项目负责人改为张三", candidates, now);
            Assert.NotEmpty(hits);
        });

        var items = Enumerable.Range(0, 500)
            .Select(i => new MessageMemoryItem($"m{i}", "g1", "main", "u1", "user",
                $"第{i}项负责人是员工{i % 97}号，交付时间是{i % 31}号", i * 1000L, 0, null))
            .ToList();

        Guard("memory.scan(500 items)", 3000, 2, () =>
        {
            _ = MemoryConflictScan.FindPairs(items, maxPairs: 200);
        });
    }

    [Fact]
    public void UsageAggregation_AtRealisticSize()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var usage = new List<UsageAggregate>();
        for (var d = 0; d < 30; d++)
            for (var a = 0; a < 50; a++)
                usage.Add(new UsageAggregate($"2026-10-{d + 1:00}", $"a{a}", $"u{a % 10}", 1000, 500, 0, 10));
        var feedback = Enumerable.Range(0, 1000)
            .Select(i => new MessageFeedbackEntry
            {
                MessageId = $"msg{i}",
                GroupId = "g1",
                AgentId = $"a{i % 50}",
                UserId = "u1",
                Value = i % 3 == 0 ? 1 : -1,
                CreatedAtMs = now - i * 1000L,
            })
            .ToList();
        var names = Enumerable.Range(0, 50).ToDictionary(i => $"a{i}", i => $"员工{i}", StringComparer.Ordinal);

        Guard("perf.aggregate(1500 usage, 1000 feedback, 50 agents)", 500, 3, () =>
        {
            var stats = AgentPerformance.Aggregate(usage, feedback, names, 30, now, seedAllAgents: true);
            Assert.Equal(50, stats.Count);
        });
    }
}
