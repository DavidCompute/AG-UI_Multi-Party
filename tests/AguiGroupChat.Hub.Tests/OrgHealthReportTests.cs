using AguiGroupChat.Agents;
using AguiGroupChat.Web;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>组织体检报告（纯函数）：总览 / 关注 / 明细 / 层级 / 建议。</summary>
public sealed class OrgHealthReportTests
{
    [Fact]
    public void BuildMarkdown_IncludesOverviewFlagsHierarchyAndCost()
    {
        var stats = new[]
        {
            new AgentPerformance.AgentStat("a1", "低分助手", 10, 1000, 500, 0, 1500, 1, 4, "2026-10-10"), // 1/5 好评 → low-praise
            new AgentPerformance.AgentStat("a2", "闲置专员", 0, 0, 0, 0, 0, 0, 0, null),                  // 无调用 → idle
        };
        var defs = new Dictionary<string, AgentDefinition>
        {
            ["a1"] = new AgentDefinition { AgentId = "a1", Nickname = "低分助手", AssignmentIds = ["a2"] },
            ["a2"] = new AgentDefinition { AgentId = "a2", Nickname = "闲置专员", EscalationAgentId = "a1" },
        };
        var pricing = new PricingOptions { InputPerMillion = 2m, OutputPerMillion = 8m, Currency = "$" };
        var evalCases = new List<EvalCase>
        {
            new() { CaseId = "e1", Name = "用例一", AgentId = "a1", LastPassed = true, LastRunAtMs = 1_700_000_000_000 },
            new() { CaseId = "e2", Name = "用例二", AgentId = "a2", LastPassed = false, LastRunAtMs = 1_700_000_100_000 },
        };

        var md = OrgHealthReport.BuildMarkdown(30, "2026-09-11", "2026-10-10", stats, defs, pricing, 1_700_000_000_000, evalCases);

        Assert.Contains("# 组织健康报告", md);
        Assert.Contains("## 二、需要关注（2）", md);
        Assert.Contains("低分助手", md);
        Assert.Contains("闲置专员", md);
        Assert.Contains("指派 → 闲置专员", md);
        Assert.Contains("提升 → 低分助手", md);
        Assert.Contains("$0.01", md); // 2×1000/1e6 + 8×500/1e6 = 0.006 → 0.01
        Assert.Contains("## 五、建议", md);
        Assert.Contains("## 六、评测回归", md);
        Assert.Contains("通过率 50%", md); // 2 条已跑，1 条通过
    }

    [Fact]
    public void BuildMarkdown_NoPricing_ShowsDashAndHealthyAdvice()
    {
        var stats = new[] { new AgentPerformance.AgentStat("a1", "好员工", 5, 10, 10, 0, 20, 3, 0, "2026-10-10") };
        var defs = new Dictionary<string, AgentDefinition> { ["a1"] = new AgentDefinition { AgentId = "a1", Nickname = "好员工" } };

        var md = OrgHealthReport.BuildMarkdown(7, "2026-10-04", "2026-10-10", stats, defs, new PricingOptions(), 1_700_000_000_000);

        Assert.Contains("未配置单价", md);
        Assert.Contains("需要关注（0）", md);
        Assert.Contains("组织运行健康", md);
    }

    [Fact]
    public void BuildMarkdown_ReportsArtifactQualityAndAdvice()
    {
        var stats = new[]
        {
            new AgentPerformance.AgentStat("a1", "空壳专员", 5, 100, 100, 0, 200, 0, 0, "2026-10-10",
                ArtifactRuns: 4, ArtifactValid: 1, ArtifactAvgScore: 40, ArtifactLow: 3),
        };
        var defs = new Dictionary<string, AgentDefinition> { ["a1"] = new AgentDefinition { AgentId = "a1", Nickname = "空壳专员" } };

        var md = OrgHealthReport.BuildMarkdown(30, "2026-09-11", "2026-10-10", stats, defs, new PricingOptions(), 1_700_000_000_000);

        Assert.Contains("产物质量：平均 40.0/100", md);
        Assert.Contains("产物质量偏低", md);
        Assert.Contains("40.0/100 · 25%", md);
    }

    [Fact]
    public void BuildMarkdown_WithPrevStats_ShowsPeriodOverPeriodLine()
    {
        // 本期：调用 200；上期：100 → ▲100%
        var stats = new[] { new AgentPerformance.AgentStat("a1", "好员工", 200, 100, 100, 0, 200, 3, 0, "2026-10-10") };
        var prev = new[] { new AgentPerformance.AgentStat("a1", "好员工", 100, 50, 50, 0, 100, 1, 1, "2026-09-10") };
        var defs = new Dictionary<string, AgentDefinition> { ["a1"] = new AgentDefinition { AgentId = "a1", Nickname = "好员工" } };

        var md = OrgHealthReport.BuildMarkdown(30, "2026-09-11", "2026-10-10", stats, defs, new PricingOptions(), 1_700_000_000_000, null, prev);

        Assert.Contains("环比上一 30 天", md);
        Assert.Contains("调用 ▲100%", md);
        // 好评率：本期 3/3=100%、上期 1/2=50% → ▲...
        Assert.Contains("好评率 ▲", md);
    }
}
