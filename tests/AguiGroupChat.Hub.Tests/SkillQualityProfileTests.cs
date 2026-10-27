using AguiGroupChat.Agents;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>
/// 技能质量画像 → 编排（1.0.348）：
/// 聚合口径、风险判定、候选提示词注入，以及方案自检中的质量预警。
/// </summary>
public sealed class SkillQualityProfileTests
{
    private static ArtifactQualityEvent Evt(string skillId, long atMs, int score, bool satisfied = true, params string[] missing)
        => new("agent_a", skillId, "Docx", score, satisfied, missing, "g1", atMs);

    [Fact]
    public void Build_AggregatesRunsValidityScoreAndMissing()
    {
        var events = new[]
        {
            Evt("docx_report", 100, 90),
            Evt("docx_report", 200, 60),                       // 低分（< 70）
            Evt("docx_report", 300, 95, satisfied: false, "缺少正文", "只有标题"),
            Evt("docx_report", 400, 50, satisfied: false, "缺少正文"),
            Evt("other_skill", 500, 100),
        };

        var map = SkillQualityProfiles.Build(events);

        var p = map["docx_report"];
        Assert.Equal(4, p.Runs);
        Assert.Equal(2, p.Valid);
        Assert.Equal(0.5, p.ValidRate);
        Assert.Equal(73.8, p.AvgScore); // (90+60+95+50)/4 = 73.75
        Assert.Equal(3, p.Low);
        Assert.Equal(400, p.LastAtMs);
        Assert.Equal("缺少正文", p.TopMissing[0]); // 出现 2 次，排第一
        Assert.Contains("只有标题", p.TopMissing);

        Assert.Equal(1, map["other_skill"].Runs);
        Assert.Equal(1, map["other_skill"].ValidRate);
    }

    [Fact]
    public void IsRisky_RequiresEnoughRuns()
    {
        // 只有 2 次低分：样本不足，不判定为风险
        Assert.False(new SkillQualityProfile("s", 2, 0, 0, 40, 2, [], 1).IsRisky);
        // 3 次里合格率 33%：风险
        Assert.True(new SkillQualityProfile("s", 3, 1, 1.0 / 3, 80, 2, [], 1).IsRisky);
        // 3 次都合格但均分 < 70：风险（例如产物契约满足但内容评分低）
        Assert.True(new SkillQualityProfile("s", 3, 3, 1, 69.9, 0, [], 1).IsRisky);
        // 3 次稳定：正常
        Assert.False(new SkillQualityProfile("s", 3, 3, 1, 95, 0, [], 1).IsRisky);
    }

    [Fact]
    public void Describe_IncludesRateAndWarning()
    {
        var risky = new SkillQualityProfile("s", 4, 1, 0.25, 61.5, 3, ["缺少正文"], 1).Describe();
        Assert.Contains("4 次", risky);
        Assert.Contains("25%", risky);
        Assert.Contains("61.5", risky);
        Assert.Contains("质量偏低", risky);
        Assert.Contains("缺少正文", risky);

        var good = new SkillQualityProfile("s", 4, 4, 1, 92.5, 0, [], 1).Describe();
        Assert.DoesNotContain("质量偏低", good);
    }

    [Fact]
    public void ToReusableSkills_AttachesQualityNote()
    {
        var skills = new[]
        {
            new AgentSkillDefinition { SkillId = "docx_report", Name = "报告", Kind = AgentSkillKind.Dotnet },
            new AgentSkillDefinition { SkillId = "new_skill", Name = "新技能", Kind = AgentSkillKind.Prompt },
        };
        var quality = new Dictionary<string, SkillQualityProfile>(StringComparer.Ordinal)
        {
            ["docx_report"] = new("docx_report", 5, 2, 0.4, 66, 3, ["缺少正文"], 1),
        };

        var list = AgentOrchestrator.ToReusableSkills(skills, quality);

        Assert.Contains("质量偏低", list.Single(r => r.SkillId == "docx_report").QualityNote);
        Assert.Null(list.Single(r => r.SkillId == "new_skill").QualityNote);
    }

    [Fact]
    public void Prompt_IncludesQualityNoteAndRule()
    {
        var reusable = new List<AgentOrchestrator.ReusableSkill>
        {
            new("docx_report", "报告", "生成工作报告", "dotnet", "近 30 天 5 次，合格率 40%，平均 66.0 分（质量偏低，慎用）"),
        };

        var prompt = AgentOrchestrator.BuildPromptForTest("做一个文案推广组，要出 Word", reusable);

        Assert.Contains("近期质量", prompt);
        Assert.Contains("质量偏低", prompt);
        Assert.Contains("合格率高", prompt);
    }

    [Fact]
    public void SelfCheck_WarnsWhenPlanUsesRiskySkill()
    {
        var plan = new OrchestrationPlan
        {
            Agents = [new OrchestratedAgent { AgentId = "writer", Nickname = "写手", SkillIds = ["docx_report"] }],
            Skills = [],
        };
        var quality = new Dictionary<string, SkillQualityProfile>(StringComparer.Ordinal)
        {
            ["docx_report"] = new("docx_report", 4, 1, 0.25, 60, 3, ["缺少正文"], 1),
        };

        var checks = AgentOrchestrator.SelfCheck(plan, "写一份报告", quality);

        var risk = Assert.Single(checks, c => c.Code == "skill_quality_risk");
        Assert.Equal("warn", risk.Level);
        Assert.Contains("docx_report", risk.Message);
        Assert.Contains("诊断修复", risk.Message);
    }

    [Fact]
    public void SelfCheck_NoQualityData_NoQualityCheck()
    {
        var plan = new OrchestrationPlan
        {
            Agents = [new OrchestratedAgent { AgentId = "writer", Nickname = "写手", SkillIds = ["docx_report"] }],
            Skills = [],
        };
        var checks = AgentOrchestrator.SelfCheck(plan, "写一份报告");
        Assert.DoesNotContain(checks, c => c.Code == "skill_quality_risk");
    }

    // ---------- 技能经验进编排（1.0.350） ----------

    [Fact]
    public void ToReusableSkills_AttachesExperienceNote()
    {
        var lessons = new SkillLessonStore();
        lessons.Add(new SkillLesson("sl_1", "docx_report", "writer", SkillLessonKinds.Failure,
            "连续 2/3 次产物未达标", ["缺少正文"], "先备齐正文", "g1", 100, false));

        var skills = new[]
        {
            new AgentSkillDefinition { SkillId = "docx_report", Name = "报告", Kind = AgentSkillKind.Dotnet },
            new AgentSkillDefinition { SkillId = "new_skill", Name = "新技能", Kind = AgentSkillKind.Prompt },
        };

        var list = AgentOrchestrator.ToReusableSkills(skills, quality: null, lessons: lessons);

        Assert.Contains("缺少正文", list.Single(r => r.SkillId == "docx_report").ExperienceNote);
        Assert.Null(list.Single(r => r.SkillId == "new_skill").ExperienceNote);
    }

    [Fact]
    public void Prompt_IncludesExperienceNoteAndRule()
    {
        var reusable = new List<AgentOrchestrator.ReusableSkill>
        {
            new("docx_report", "报告", "生成工作报告", "dotnet", null,
                "上次失败：连续 2/3 次产物未达标（缺少正文）；改进：先备齐正文"),
        };

        var prompt = AgentOrchestrator.BuildPromptForTest("做一个文案组，要出 Word", reusable);

        Assert.Contains("已知坑", prompt);
        Assert.Contains("缺少正文", prompt);
        Assert.Contains("规避步骤", prompt);
    }

    [Fact]
    public void SelfCheck_WarnsForUnresolvedSkillExperience()
    {
        var plan = new OrchestrationPlan
        {
            Agents = [new OrchestratedAgent { AgentId = "writer", Nickname = "写手", SkillIds = ["docx_report"] }],
            Skills = [],
        };
        var lessons = new SkillLessonStore();
        lessons.Add(new SkillLesson("sl_1", "docx_report", "writer", SkillLessonKinds.Failure,
            "连续 2/3 次产物未达标", ["缺少正文"], "先备齐正文", "g1", 100, false));

        var checks = AgentOrchestrator.SelfCheck(plan, "写一份报告", quality: null, lessons: lessons);

        var risk = Assert.Single(checks, c => c.Code == "skill_experience_risk");
        Assert.Equal("warn", risk.Level);
        Assert.Contains("docx_report", risk.Message);
        Assert.Contains("缺少正文", risk.Message);
    }

    [Fact]
    public void SelfCheck_ResolvedLesson_NoExperienceCheck()
    {
        var plan = new OrchestrationPlan
        {
            Agents = [new OrchestratedAgent { AgentId = "writer", Nickname = "写手", SkillIds = ["docx_report"] }],
            Skills = [],
        };
        var lessons = new SkillLessonStore();
        lessons.Add(new SkillLesson("sl_1", "docx_report", "writer", SkillLessonKinds.Failure,
            "已解决", ["缺少正文"], null, "g1", 100, Resolved: true));

        var checks = AgentOrchestrator.SelfCheck(plan, "写一份报告", quality: null, lessons: lessons);
        Assert.DoesNotContain(checks, c => c.Code == "skill_experience_risk");
    }
}
