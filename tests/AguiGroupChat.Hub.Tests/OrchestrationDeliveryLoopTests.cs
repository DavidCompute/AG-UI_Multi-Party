using AguiGroupChat.Agents;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>
/// 编排的「交付闭环」——用户要的东西，队伍里必须真有人能产出。
///
/// 回归背景（真实场景）：同样一句「我要最终形成 word 文档」，
/// 编排器有时产出带 docx 技能的交付岗，有时产出一堆纯 prompt 岗位 —— 用户拿不到文件。
/// 更常见的是用“只能生成文字描述”的 prompt 技能冒充交付能力（如把 make_excel_report 写成 prompt 模板）。
/// </summary>
public sealed class OrchestrationDeliveryLoopTests
{
    private static OrchestrationPlan Plan(params (string AgentId, string[] Skills)[] agents)
    {
        var p = new OrchestrationPlan { Title = "T" };
        foreach (var (id, skills) in agents)
            p.Agents.Add(new OrchestratedAgent { AgentId = id, Nickname = id, Description = "d", Instructions = "i", SkillIds = [.. skills] });
        return p;
    }

    [Fact]
    public void FlagsGap_WhenUserWantsWordButNobodyCanProduceIt()
    {
        // 全是 prompt 型岗位：写得了字，产不出文件
        var plan = Plan(("writer", ["longform_copy"]), ("reviewer", ["copy_review"]));
        var warn = AgentOrchestrator.DetectDeliveryGap(plan, "做一个文案团队，我要最终形成word文档");

        Assert.NotNull(warn);
        Assert.Contains("Word", warn);
        Assert.Contains("docx_", warn);
    }

    [Fact]
    public void NoGap_WhenSomeoneOwnsDocxSkill()
    {
        var plan = Plan(("writer", ["longform_copy"]), ("formatter", ["docx_report"]));
        Assert.Null(AgentOrchestrator.DetectDeliveryGap(plan, "做一个文案团队，我要最终形成word文档"));
    }

    [Fact]
    public void NoGap_WhenDeliverySkillIsNewlyCreated()
    {
        // 新造技能也算：模型自建产出能力时应放行（只看 skillId 前缀）
        var plan = Plan(("writer", ["longform_copy"]));
        plan.Skills.Add(new OrchestratedSkill { SkillId = "docx_contract_opinion", Name = "审核意见书", Kind = "dotnet" });
        // 岗位没引用，但方案里定义了该技能 → 视为具备（前端会提示用户确认挂载）
        Assert.Null(AgentOrchestrator.DetectDeliveryGap(plan, "合同审核，输出审核意见的 Word 文档"));
    }

    [Fact]
    public void FlagsExcelGap()
    {
        var plan = Plan(("maker", ["make_excel_report"]));
        var warn = AgentOrchestrator.DetectDeliveryGap(plan, "每周把数据整理成 Excel 表发给我");
        Assert.NotNull(warn);
        Assert.Contains("Excel", warn);
        Assert.Contains("xlsx_", warn);
    }

    [Fact]
    public void NoCheck_WhenRequirementDoesNotAskForAFile()
    {
        // 纯分析/咨询类需求不该被这条规则打扰
        var plan = Plan(("analyst", ["market_research"]));
        Assert.Null(AgentOrchestrator.DetectDeliveryGap(plan, "帮我分析一下这个市场的竞争格局"));
    }

    [Fact]
    public void Prompt_TellsModelToGuaranteeDelivery()
    {
        var prompt = AgentOrchestrator.BuildPromptForTest("做一个团队，我要最终形成word文档", null);

        // 必须显式要求交付闭环，否则模型只堆能力、不管用户能否拿到东西
        Assert.Contains("交付闭环", prompt);
        Assert.Contains("必须至少有一个岗位", prompt);
        // 必须禁止用纯 prompt 技能冒充交付能力
        Assert.Contains("禁止", prompt);
        // 交付岗应是链末端，且不能因内部流程拒交
        Assert.Contains("叶子岗", prompt);
        Assert.Contains("不要因为", prompt);
        // 必须要求优先复用内置 docx_*，而不是自造成品
        Assert.Contains("docx_report", prompt);
        Assert.Contains("不要", prompt);
    }

    // ===== 空洞交付技能检测 =====

    [Fact]
    public void FlagsHollow_WhenDeliverySkillIsPromptKind()
    {
        // prompt 类型的“文档生成”技能本质产不出文件
        var plan = new OrchestrationPlan { Title = "T" };
        plan.Agents.Add(new OrchestratedAgent { AgentId = "maker", Nickname = "表格员", SkillIds = ["xlsx_make"] });
        plan.Skills.Add(new OrchestratedSkill { SkillId = "xlsx_make", Kind = "prompt", Body = new string('x', 500) });

        var warn = AgentOrchestrator.DetectHollowDeliverySkill(plan);
        Assert.NotNull(warn);
        Assert.Contains("xlsx_make", warn);
    }

    [Fact]
    public void FlagsHollow_WhenDeliverySkillBodyTooShort()
    {
        // 真实踩到：docx_pack_shell 只有 97 字符，内容就是 ls 列目录
        var plan = new OrchestrationPlan { Title = "T" };
        plan.Skills.Add(new OrchestratedSkill
        {
            SkillId = "docx_pack_shell", Kind = "shell",
            Body = "set -euo pipefail; OUT=data/skillruns/x; mkdir -p $OUT; ls -l $OUT; echo 产物清单已输出",
        });

        var warn = AgentOrchestrator.DetectHollowDeliverySkill(plan);
        Assert.NotNull(warn);
        Assert.Contains("docx_pack_shell", warn);
    }

    [Fact]
    public void NoHollowFlag_ForSubstantialDotnetDeliverySkill()
    {
        // 正文够长且不是占位写死：不误报
        var plan = new OrchestrationPlan { Title = "T" };
        plan.Skills.Add(new OrchestratedSkill
        {
            SkillId = "docx_custom", Kind = "dotnet",
            Body = "using System;\npublic class Skill { public static string Run(string i) { var sections = i; " + new string('x', 300) + " return \"{}\"; } }",
        });

        Assert.Null(AgentOrchestrator.DetectHollowDeliverySkill(plan));
    }

    [Fact]
    public void NoHollowFlag_WhenNoDeliverySkillsAtAll()
    {
        // 没提及交付技能时不报（缺交付能力由 DetectDeliveryGap 负责）
        var plan = new OrchestrationPlan { Title = "T" };
        plan.Skills.Add(new OrchestratedSkill { SkillId = "market_research", Kind = "prompt", Body = "分析市场" });
        Assert.Null(AgentOrchestrator.DetectHollowDeliverySkill(plan));
    }

    // ---------- 交付岗被写成“流程门卫”（实测踩到：word_delivery 反问用户要定稿）----------

    [Fact]
    public void FlagsGatekeeper_WhenDeliveryAgentDemandsFinalDraftFirst()
    {
        // 真实踩到：word_delivery 的 instructions 写成“仅接收主笔签发的定稿版本生成 .docx”
        var plan = new OrchestrationPlan { Title = "T" };
        plan.Agents.Add(new OrchestratedAgent
        {
            AgentId = "word_delivery", Nickname = "Word 交付专员", SkillIds = ["docx_report"],
            Instructions = "仅接收主笔签发的定稿版本生成 .docx，不接受未过合规的稿件。",
        });

        var warn = AgentOrchestrator.DetectDeliveryGatekeeper(plan);
        Assert.NotNull(warn);
        Assert.Contains("word_delivery", warn);
        Assert.Contains("定稿", warn);
    }

    [Fact]
    public void FlagsGatekeeper_WhenDeliveryAgentRequiresComplianceApproval()
    {
        var plan = new OrchestrationPlan { Title = "T" };
        plan.Agents.Add(new OrchestratedAgent
        {
            AgentId = "excel_delivery", SkillIds = ["xlsx_report"],
            Instructions = "稿件须先经合规审核通过后才导出 Excel。",
        });

        Assert.NotNull(AgentOrchestrator.DetectDeliveryGatekeeper(plan));
    }

    [Fact]
    public void NoGatekeeperFlag_ForCompliantDeliveryAgent()
    {
        // 正确写法：拿到材料就先出稿，不完整列入待确认项
        var plan = new OrchestrationPlan { Title = "T" };
        plan.Agents.Add(new OrchestratedAgent
        {
            AgentId = "word_delivery", SkillIds = ["docx_report"],
            Instructions = "根据现有材料直接整理为 .docx；材料不完整时在附页列出待确认项，不以等待定稿为由不出文件。",
        });

        Assert.Null(AgentOrchestrator.DetectDeliveryGatekeeper(plan));
    }

    [Fact]
    public void NoGatekeeperFlag_ForNonDeliveryAgents()
    {
        // 只约束真正具备交付能力的岗位；主笔/合规岗的流程规矩本身合理，不该误报
        var plan = new OrchestrationPlan { Title = "T" };
        plan.Agents.Add(new OrchestratedAgent
        {
            AgentId = "lead_copywriter", SkillIds = ["copy_plan"],
            Instructions = "定稿后才提交核准流程。",
        });

        Assert.Null(AgentOrchestrator.DetectDeliveryGatekeeper(plan));
    }

    [Fact]
    public void Prompt_ForbidsTurningInternalProcessIntoDeliveryPrecondition()
    {
        var prompt = AgentOrchestrator.BuildPromptForTest("做一个团队，我要最终形成word文档", null);

        Assert.Contains("严禁", prompt);
        Assert.Contains("仅接收", prompt);
        // 要给出“正确写法”，否则模型只会删掉禁止句、留下空泛人设
        Assert.Contains("直接交付", prompt);
    }
}
