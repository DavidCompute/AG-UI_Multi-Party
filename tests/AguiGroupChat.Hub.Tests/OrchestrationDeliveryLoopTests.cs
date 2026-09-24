using AguiGroupChat.Agents;
using AguiGroupChat.Agents.BuiltinSkills;
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

    // ---------- 演示文稿（pptx）交付的编排适配 ----------

    [Fact]
    public void PptWithTable_IsClassifiedAsPptNotExcel()
    {
        // 真实踩到：先判“表格”会把“做份 PPT，含对比表格”判成 Excel 交付，
        // 于是本函数因为“没有 xlsx_ 技能”而误报，真正的 pptx 交付能力反而被忽略。
        var plan = Plan(("writer", ["copywriting"]));
        var warn = AgentOrchestrator.DetectDeliveryGap(
            plan, "做一份《知聚平台介绍》的 PPT，含一张对比表格与一个柱状图");

        Assert.NotNull(warn);
        Assert.Contains("演示文稿", warn);
        Assert.Contains("pptx_", warn);
    }

    [Fact]
    public void NoGap_WhenTeamAlreadyHasPptxSkill()
    {
        var plan = Plan(("writer", ["copywriting"]), ("deck", ["pptx_deck"]));
        Assert.Null(AgentOrchestrator.DetectDeliveryGap(plan, "做一份产品介绍 PPT"));
    }

    [Fact]
    public void VagueDocWithTable_StaysWordNotExcel()
    {
        // 含糊表述没提任何明确格式词：兜底需与历史口径一致——“文档”优先当 Word，
        // 否则“写份文档，内含表格”会因为先撞上“表格”而被要求有 xlsx_ 技能（误报）。
        var plan = Plan(("writer", ["docx_report"]));
        Assert.Null(AgentOrchestrator.DetectDeliveryGap(plan, "写份文档，里面含一张表格"));
    }

    [Fact]
    public void GapHint_NamesTheBuiltinPptxSkill()
    {
        var plan = Plan(("writer", ["copywriting"]));
        var warn = AgentOrchestrator.DetectDeliveryGap(plan, "帮我做一套演示文稿");

        Assert.NotNull(warn);
        Assert.Contains("pptx_deck", warn); // 点名内置技能，模型/用户才有可直接引用的对象
    }

    [Fact]
    public void Prompt_TellsModelToReuseBuiltinPptxSkill()
    {
        // 编排提示词必须像 docx 那样点名 pptx_deck，否则模型会自造空壳 PPT 技能
        var prompt = AgentOrchestrator.BuildPromptForTest("做一个产品发布 PPT，最终要 pptx 文件", null);
        Assert.Contains("pptx_deck", prompt);
        Assert.Contains("要 PPT / 演示文稿", prompt);
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

    // ---------- 计划阶段：dotnet 交付技能的输入识别与空输入保护 ----------

    [Fact]
    public void DotnetDeliverySkill_IsFlaggedAsNeedingUpstreamInput()
    {
        // 实测踩到：docx_report / md_to_docx 的参数走 JSON 函数签名，
        // 正文里没有 ${xxx} 占位符，旧口径 RequiredUpstreamInputs 会把它当成“无需输入”，
        // 于是规划器把它排在产出岗之前，导出的 Word 只有标题。
        var skill = new AgentSkillDefinition
        {
            SkillId = "md_to_docx", Kind = AgentSkillKind.Dotnet,
            Name = "Markdown 转 Word 文档(.docx)",
            Description = "把已定稿的稿件排版导出为 Word .docx 文件，生成后可直接下载。",
            Body = "public class Skill { public static string Run(string input) { return \"{}\"; } }",
        };

        var inputs = AgentGatewayHelpers.RequiredUpstreamInputs(skill);
        Assert.NotEmpty(inputs);
    }

    [Fact]
    public void PromptSkill_IsNotFlaggedAsNeedingUpstreamInput()
    {
        // prompt 技能不产出文件，不该被当成交付环节；避免把它也强拉到链末
        var skill = new AgentSkillDefinition
        {
            SkillId = "marketing_copy_prompt", Kind = AgentSkillKind.Prompt,
            Name = "市场推广文案撰写模板",
            Description = "给出推广文案的写作框架与要点。",
        };

        Assert.Empty(AgentGatewayHelpers.RequiredUpstreamInputs(skill));
    }

    [Fact]
    public void ShellSkillWithPlaceholder_StillReportsPlaceholderName()
    {
        // 原有口径不能丢：有 ${query} 的技能仍按占位符名上报
        var skill = new AgentSkillDefinition
        {
            SkillId = "check_disk", Kind = AgentSkillKind.Shell,
            Description = "检查磁盘占用。",
            Body = "df -h ${query}",
        };

        Assert.Equal(["query"], AgentGatewayHelpers.RequiredUpstreamInputs(skill));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("（未返回内容）")]
    [InlineData("(未返回内容)")]
    [InlineData("（无）")]
    [InlineData("（空）")]
    [InlineData("——")]
    [InlineData("。")]
    public void LooksLikeEmptyInput_DetectsPlaceholdersAndPunctuationOnly(string? text)
    {
        // 上游返回空时，网关把字面量“（未返回内容）”继续往下传，后续技能会照着它生成空壳文件
        Assert.True(AgentGatewayHelpers.LooksLikeEmptyInput(text));
    }

    [Fact]
    public void LooksLikeEmptyInput_PassesRealContent()
    {
        var text = "# 知聚市场推广文案\n\n## 一、投放基线\n\n- 受众：技术决策者";
        Assert.False(AgentGatewayHelpers.LooksLikeEmptyInput(text));
    }

    [Fact]
    public void PlanPrompt_RequiresDeliverySkillsAfterProducingRoles()
    {
        // 规划器提示词必须把“先产出、后导出”写成硬规则，
        // 否则模型会把 docx_report 排在执笔岗之前，拿到空输入。
        var prompt = AgentGateway.BuildPlannerPromptText("协调员", "写一份推广文案并导出 Word", "- [技能] docx_report：导出 Word");
        Assert.Contains("交付类技能必须排在产出内容之后", prompt);
        Assert.Contains("导出 / 排版 / 转换", prompt);
    }

    [Theory]
    [InlineData("（请用中文回复，提问者消息以中文为主。）")]
    [InlineData("（質問は日本語です。日本語で回答してください。）")]
    [InlineData("（无）")]
    public void PlatformPreambleLines_AreRecognized(string line)
    {
        // 实测踩到：docx_gongwen 收到的输入就是这句语言提示，长度超过 2 字但根本不是正文，
        // 技能拿去当 JSON 解析 → JsonReaderException。
        Assert.True(AgentGatewayHelpers.IsPlatformPreambleLine(line));
        Assert.True(AgentGatewayHelpers.LooksLikeEmptyInput(line));
    }

    [Fact]
    public void LanguageHint_IsStrippedFromLatestUserUtterance()
    {
        // 真实平台消息形状：语言提示 → 群历史 → 用户本次发言（无边界标记）
        var platform = "（请用中文回复，提问者消息以中文为主。）\n"
                     + "以下是群最近对话：\n"
                     + "David：先前的闲聊\n"
                     + "内容负责人：一版旧稿\n"
                     + "帮我写一份推广文案";
        var utterance = AgentGatewayHelpers.ExtractLatestUserUtterance(platform);

        Assert.NotNull(utterance);
        Assert.DoesNotContain("请用中文回复", utterance);
        Assert.Contains("帮我写一份推广文案", utterance);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("（未返回内容）")]
    [InlineData("（请用中文回复，提问者消息以中文为主。）")]
    [InlineData("写完了")]
    public void LooksTooThinForDelivery_RejectsPreambleAndShortText(string? text)
    {
        // 交付类技能的输入闸门：短句 / 前言都生成不出有意义的文件，必须先走兼底
        Assert.True(AgentGatewayHelpers.LooksTooThinForDelivery(text));
    }

    [Fact]
    public void LooksTooThinForDelivery_PassesSubstantialCopy()
    {
        var text = "# 知聚市场推广文案\n\n## 一、投放基线\n\n" + new string('字', 200);
        Assert.False(AgentGatewayHelpers.LooksTooThinForDelivery(text));
    }

    // ---------- 文档生成技能：识别与“不自动直调” ----------

    [Theory]
    [InlineData("docx_gongwen", "公文生成（Word）", "生成规范排版的党政机关公文 Word 文档")]
    [InlineData("docx_report", "工作报告生成（Word）", "生成工作报告 / 工作总结类 Word 文档，支持分章节")]
    [InlineData("docx_notice", "通知生成（Word）", "生成通知类的 Word 文档")]
    [InlineData("md_to_docx", "Markdown 转 Word 文档(.docx)", "把已定稿的稿件排版导出为 Word .docx 文件")]
    public void DocumentGenerators_AreRecognized(string id, string name, string desc)
    {
        // 这些技能的入参是结构化 JSON，必须由模型当工具调用构造；
        // 计划路径直接调它们会塑出空壳文档（实测：Word 只有标题）。
        var skill = new AgentSkillDefinition
        {
            SkillId = id, Name = name, Description = desc,
            Kind = AgentSkillKind.Dotnet, Body = "public class S { }"
        };
        Assert.True(AgentGatewayHelpers.IsDocumentGenerator(skill));
        // 同时必须被认作“需要上游输入”，否则计划里它不会被排在产出岗之后
        Assert.NotEmpty(AgentGatewayHelpers.RequiredUpstreamInputs(skill));
    }

    [Fact]
    public void PromptSkill_IsNotADocumentGenerator()
    {
        var skill = new AgentSkillDefinition
        {
            SkillId = "marketing_copy_prompt", Kind = AgentSkillKind.Prompt,
            Description = "给出推广文案的写作框架，提到 Word 文档但自己不产文件。"
        };
        Assert.False(AgentGatewayHelpers.IsDocumentGenerator(skill));
    }

    [Theory]
    [InlineData("docx_report", "docx_", true)]
    [InlineData("md_to_docx", "docx_", true)]   // 后缀式命名：只判 StartsWith 会漏掉
    [InlineData("promo_docs/export_docx", "docx_", true)]
    [InlineData("make_xlsx", "xlsx_", true)]
    [InlineData("marketing_copy_prompt", "docx_", false)]
    [InlineData("check_disk", "docx_", false)]
    public void DeliverablePrefix_MatchesPrefixAndSuffixNaming(string id, string prefix, bool expected)
    {
        Assert.Equal(expected, AgentGatewayHelpers.SkillMatchesDeliverablePrefix(id, prefix));
    }

    [Fact]
    public void DeliveryPrompt_RequiresCompleteContentAndNamesTheSkill()
    {
        // 实测踩到：交付兑底只给“标题 + 目录”的骨架，用户拿到的 Word 只有标题。
        // 提示词必须同时要求：①点名要调的技能；②内容逐节填满；③不得因流程拒交。
        var prompt = AgentGateway.BuildDeliveryPrompt("Word 文档", "docx_report", "根据附件写推广文案，我要 word");

        Assert.Contains("docx_report", prompt);
        Assert.Contains("内容必须完整", prompt);
        Assert.Contains("sections", prompt);
        Assert.Contains("绝不允许只传标题", prompt);
        Assert.Contains("为由拒交", prompt);
    }

    [Fact]
    public void DeliveryPrompt_WithoutKnownSkill_StillDemandsContent()
    {
        var prompt = AgentGateway.BuildDeliveryPrompt("Excel 表格", null, "整理成表格");
        Assert.Contains("文件生成技能", prompt);
        Assert.Contains("内容必须完整", prompt);
    }

    [Fact]
    public void DeliveryPrompt_RetryTellsModelItDidNotCallTheTool()
    {
        // 实测踩到：主管链路下交付岗只回“已完成 Word 导出”而没调工具（群历史里已有它自己的类似发言），
        // 用户拿不到文件。第二次尝试必须把“上次没真调工具 / 别信历史里的已完成”说清楚。
        var prompt = AgentGateway.BuildDeliveryPrompt("Word 文档", "docx_report", "写简介并导出", retryNoToolCall: true);

        Assert.Contains("并没有真正调用工具", prompt);
        Assert.Contains("不要相信对话历史", prompt);
        Assert.Contains("docx_report", prompt);
    }

    [Fact]
    public void DeliveryPrompt_FirstAttemptHasNoRetryWording()
    {
        var prompt = AgentGateway.BuildDeliveryPrompt("Word 文档", "docx_report", "写简介并导出");
        Assert.DoesNotContain("并没有真正调用工具", prompt);
    }

    [Fact]
    public void DeliveryResult_ReadsBlocksAndProduceFileMarker()
    {
        // 内置 docx 技能返回 { ok, scene, path, blocks, produce_file, message }：
        // 用 blocks 区分“真出了文档”与“只出了一张封面”（实测踩到 blocks=4 的封面文档）。
        var thick = "{\"ok\":true,\"path\":\"/app/docs/a.docx\",\"blocks\":12,\"produce_file\":{\"path\":\"/app/docs/a.docx\"}}";
        var (hasFile, blocks) = AgentGateway.ParseDeliveryResult(thick);
        Assert.True(hasFile);
        Assert.Equal(12, blocks);
    }

    [Fact]
    public void DeliveryResult_CoverOnlyDocumentHasLowBlocks()
    {
        var cover = "{\"ok\":true,\"path\":\"/app/docs/b.docx\",\"blocks\":4,\"produce_file\":{\"path\":\"/app/docs/b.docx\"}}";
        var (hasFile, blocks) = AgentGateway.ParseDeliveryResult(cover);
        Assert.True(hasFile);
        Assert.True(blocks < 5, "只有封面的文档应低于交付阈值");
    }

    [Fact]
    public void DeliveryResult_NoToolCallHasNoFile()
    {
        var (hasFile, blocks) = AgentGateway.ParseDeliveryResult("我已经完成了导出。");
        Assert.False(hasFile);
        Assert.Equal(0, blocks);
    }

    // ---------- 文档技能入参校验（拦住“只有标题”的空壳文档） ----------

    private static AgentSkillDefinition DocxSkill() => new()
    {
        SkillId = "docx_notice", Name = "通知生成（Word）", Kind = AgentSkillKind.Dotnet,
        Description = "生成通知类的 Word 文档", Body = "public class S { }",
    };

    [Fact]
    public void DocInput_RejectedWhenOnlyTitleProvided()
    {
        // 实测踩到：模型把正文写在聊天里，只给工具传 title/subtitle，用户拿到的 Word 只有标题。
        var why = AgentGatewayHelpers.ValidateDocumentSkillInput(DocxSkill(), "{\"title\":\"知聚平台简介\",\"subtitle\":\"副标题\"}");
        Assert.NotNull(why);
        Assert.Contains("sections", why);
        Assert.Contains("只有标题", why);
    }

    [Fact]
    public void DocInput_RejectedWhenSectionsEmpty()
    {
        var why = AgentGatewayHelpers.ValidateDocumentSkillInput(DocxSkill(), "{\"title\":\"T\",\"sections\":[]}");
        Assert.NotNull(why);
    }

    [Fact]
    public void DocInput_AcceptedWhenSectionsHaveContent()
    {
        var ok = AgentGatewayHelpers.ValidateDocumentSkillInput(DocxSkill(),
            "{\"title\":\"T\",\"sections\":[{\"heading\":\"一、简介\"},{\"paragraph\":\"正文\"}]}");
        Assert.Null(ok);
    }

    [Fact]
    public void DocInput_AcceptedForMarkdownStylePayload()
    {
        // md_to_docx 这类吃 markdown 的技能：正文非空就放行
        var ok = AgentGatewayHelpers.ValidateDocumentSkillInput(DocxSkill(), "{\"markdown\":\"# 标题\\n正文\"}");
        Assert.Null(ok);
    }

    [Fact]
    public void DocInput_NotBlockedWhenInputIsPlainMarkdown()
    {
        // 非 JSON（直接吃 Markdown 正文）不拦：交给技能自己处理
        Assert.Null(AgentGatewayHelpers.ValidateDocumentSkillInput(DocxSkill(), "# 知聚平台简介\n\n正文内容"));
    }

    [Fact]
    public void DocInput_NotBlockedWhenJsonIsMalformed()
    {
        // 解析不了就不拦：让技能报真实错误，不要掩盖问题
        Assert.Null(AgentGatewayHelpers.ValidateDocumentSkillInput(DocxSkill(), "{不是合法 JSON"));
    }

    [Fact]
    public void DocInput_RejectedForTypeTextShapeInsteadOfKeyAsType()
    {
        // 实测踩到（真实入参）：模型用了 {type,text} 这种“合理但错误”的形状，
        // 技能一个块都不识别 → 用户拿到的 Word 只有标题（2741 字节 / 3 段）。
        var bad = "{\"title\":\"知聚平台简介\",\"sections\":["
                + "{\"type\":\"quote\",\"text\":\"一句话定位…\"},"
                + "{\"type\":\"heading\",\"level\":1,\"text\":\"一、平台简介\"},"
                + "{\"type\":\"paragraph\",\"text\":\"知聚是…\"}]}";
        var why = AgentGatewayHelpers.ValidateDocumentSkillInput(DocxSkill(), bad);

        Assert.NotNull(why);
        Assert.Contains("键名即类型", why);
        Assert.Contains("\"paragraph\"", why);  // 提示里要给出正确形状
    }

    [Fact]
    public void DocInput_AcceptedForKeyAsTypeShape()
    {
        var good = "{\"title\":\"T\",\"sections\":["
                 + "{\"heading\":\"一、简介\",\"level\":1},"
                 + "{\"paragraph\":\"正文\"},"
                 + "{\"bullets\":[\"要点\"]},"
                 + "{\"table\":{\"headers\":[\"a\"],\"rows\":[[\"b\"]]}}]}";
        Assert.Null(AgentGatewayHelpers.ValidateDocumentSkillInput(DocxSkill(), good));
    }

    [Fact]
    public void DocxSkillDescriptions_DocumentTheKeyAsTypeShape()
    {
        // 描述必须把形状写清楚，否则模型会自创 {type,text} 形状（实测）
        foreach (var d in BuiltinDocxSkills.Definitions)
        {
            Assert.Contains("键名即块类型", d.Description);
            Assert.Contains("type:'heading'", d.Description);
            Assert.Contains("\"paragraph\"", d.Description);
        }
    }

    [Fact]
    public void DocInput_RejectedWhenQueryIsEmpty()
    {
        Assert.NotNull(AgentGatewayHelpers.ValidateDocumentSkillInput(DocxSkill(), null));
    }

    // ---------- pptx 技能：正文在 slides（type 区分页型），与 docx 的 sections 不同口径 ----------

    private static AgentSkillDefinition PptxSkill() => new()
    {
        SkillId = "pptx_deck", Name = "演示文稿生成（PPT）", Kind = AgentSkillKind.Dotnet,
        Description = "生成 PowerPoint 演示文稿（.pptx），含封面、目录、内容页等。", Body = "public class S { }",
    };

    [Fact]
    public void Pptx_IsRecognizedAsPresentationSkill()
    {
        Assert.True(AgentGatewayHelpers.IsPresentationSkill(PptxSkill()));
        Assert.False(AgentGatewayHelpers.IsPresentationSkill(DocxSkill()));
    }

    [Fact]
    public void Pptx_AcceptedWhenSlidesHaveTypes()
    {
        var ok = AgentGatewayHelpers.ValidateDocumentSkillInput(PptxSkill(),
            "{\"title\":\"T\",\"slides\":[{\"type\":\"cover\",\"title\":\"封面\"},{\"type\":\"content\",\"title\":\"要点\",\"bullets\":[\"甲\"]}]}");
        Assert.Null(ok);
    }

    [Fact]
    public void Pptx_RejectedWhenSlidesMissing()
    {
        // 把 docx 的 sections 形状用在 pptx 上 → 该拦（否则产出空壳 PPT）
        var why = AgentGatewayHelpers.ValidateDocumentSkillInput(PptxSkill(),
            "{\"title\":\"T\",\"sections\":[{\"heading\":\"一\"}]}");
        Assert.NotNull(why);
        Assert.Contains("slides", why);
    }

    [Fact]
    public void Pptx_RejectedWhenSlidesEmpty()
    {
        Assert.NotNull(AgentGatewayHelpers.ValidateDocumentSkillInput(PptxSkill(), "{\"slides\":[]}"));
    }

    [Fact]
    public void Pptx_RejectedWhenNoSlideHasType()
    {
        var why = AgentGatewayHelpers.ValidateDocumentSkillInput(PptxSkill(),
            "{\"slides\":[{\"title\":\"封面\"},{\"title\":\"正文\"}]}");
        Assert.NotNull(why);
        Assert.Contains("type", why);
    }

    [Fact]
    public void Pptx_RejectedForDocxShapeSoTheTwoConventionsDoNotMix()
    {
        // 反向：docx 技能收到 slides 也要拦（防止模型把两套约定搞反）
        var why = AgentGatewayHelpers.ValidateDocumentSkillInput(DocxSkill(),
            "{\"title\":\"T\",\"slides\":[{\"type\":\"content\"}]}");
        Assert.NotNull(why);
        Assert.Contains("sections", why);
    }

    [Theory]
    [InlineData("read")]
    [InlineData("qa")]
    [InlineData("edit")]
    public void Pptx_NonGeneratingActions_AreAcceptedWithoutSlides(string action)
    {
        // 实测踩到（真实事故）：用户说“领导不喜欢黑色背景”，模型自然地用 action=read / edit 去读旧稿、
        // 改主题——而旧实现一律要求 slides，于是这些调用被当“入参不合格”拒掉，模型反复重试后
        // 对用户说“我没有文件读取能力 / 交付不了”。产物本来就在磁盘上，却被自己的校验拦住。
        foreach (var payload in new[]
                 {
                     $"{{\"action\":\"{action}\",\"path\":\"/app/docs/旧稿.pptx\"}}",
                     $"{{\"action\":\"{action.ToUpperInvariant()}\",\"path\":\"/app/docs/旧稿.pptx\"}}", // 大小写不敏感
                 })
            Assert.Null(AgentGatewayHelpers.ValidateDocumentSkillInput(PptxSkill(), payload));
    }

    [Fact]
    public void Pptx_StillRejectsGenerationWithoutSlides()
    {
        // 豁免只针对读取 / 自检 / 改动：真正“从零生成”依旧必须有 slides（否则产出空壳 PPT）
        var why = AgentGatewayHelpers.ValidateDocumentSkillInput(PptxSkill(),
            "{\"action\":\"generate\",\"title\":\"T\"}");
        Assert.NotNull(why);
        Assert.Contains("slides", why);
    }

    [Fact]
    public void ParseDeliveryResult_ReadsSlidesForPptx()
    {
        // pptx 技能报 slides（页数）；厚度判定要认得它，否则会被当成“0 内容”
        var (hasFile, content) = AgentGateway.ParseDeliveryResult(
            "{\"ok\":true,\"slides\":9,\"produce_file\":{\"path\":\"/x.pptx\"}}");
        Assert.True(hasFile);
        Assert.Equal(9, content);
    }

    // ---------- xlsx 技能：正文在 sheets（列定义 + 数据行），与 docx/pptx 又不同 ----------

    private static AgentSkillDefinition XlsxSkill() => new()
    {
        SkillId = "xlsx_book", Name = "表格生成（Excel）", Kind = AgentSkillKind.Dotnet,
        Description = "生成 Excel 工作簿（.xlsx），含多工作表、公式与数字格式。", Body = "public class S { }",
    };

    [Fact]
    public void Xlsx_IsRecognizedAsSpreadsheetSkill()
    {
        Assert.True(AgentGatewayHelpers.IsSpreadsheetSkill(XlsxSkill()));
        Assert.False(AgentGatewayHelpers.IsSpreadsheetSkill(DocxSkill()));
        Assert.False(AgentGatewayHelpers.IsSpreadsheetSkill(PptxSkill()));
    }

    [Fact]
    public void Xlsx_AcceptedWhenSheetsHaveColumnsAndRows()
    {
        var ok = AgentGatewayHelpers.ValidateDocumentSkillInput(XlsxSkill(),
            "{\"title\":\"经营分析\",\"sheets\":[{\"name\":\"明细\","
            + "\"columns\":[{\"header\":\"月份\"},{\"header\":\"金额\"}],"
            + "\"rows\":[[\"1月\",12000],[\"2月\",15000]]}]}");
        Assert.Null(ok);
    }

    [Fact]
    public void Xlsx_AcceptedForHeadersShorthand()
    {
        var ok = AgentGatewayHelpers.ValidateDocumentSkillInput(XlsxSkill(),
            "{\"sheets\":[{\"headers\":[\"指标\",\"金额\"],\"rows\":[[\"总收入\",100]]}]}");
        Assert.Null(ok);
    }

    [Fact]
    public void Xlsx_RejectedWhenSheetsMissing()
    {
        // 只给 title → 产物是一张空表（与 docx 只有标题是同一类失败）
        var why = AgentGatewayHelpers.ValidateDocumentSkillInput(XlsxSkill(), "{\"title\":\"报表\"}");
        Assert.NotNull(why);
        Assert.Contains("sheets", why);
    }

    [Fact]
    public void Xlsx_RejectedWhenSheetsEmpty()
    {
        Assert.NotNull(AgentGatewayHelpers.ValidateDocumentSkillInput(XlsxSkill(), "{\"sheets\":[]}"));
    }

    [Fact]
    public void Xlsx_RejectedWhenSheetHasNoRows()
    {
        // 只有列定义没有数据行 → 仍是空表
        var why = AgentGatewayHelpers.ValidateDocumentSkillInput(XlsxSkill(),
            "{\"sheets\":[{\"name\":\"明细\",\"columns\":[{\"header\":\"月份\"}],\"rows\":[]}]}");
        Assert.NotNull(why);
        Assert.Contains("数据行", why);
    }

    [Fact]
    public void Xlsx_AnalyzeMode_IsNotTreatedAsDelivery()
    {
        // analyze 是读取/分析已有文件，不产出文件，不该被“空壳”闸门拦住
        Assert.Null(AgentGatewayHelpers.ValidateDocumentSkillInput(XlsxSkill(),
            "{\"action\":\"analyze\",\"path\":\"/app/docs/已有.xlsx\"}"));
    }

    [Fact]
    public void Xlsx_DeliveryThickness_CountsRowsNotBlocks()
    {
        // xlsx 报的是 sheets/rows/columns，没有 blocks。若沿用块数口径会永远算 0 → 无谓重试。
        var text = "{\"ok\":true,\"sheets\":2,\"rows\":14,\"columns\":5,"
                 + "\"produce_file\":{\"path\":\"/app/docs/a.xlsx\"}}";
        var (hasFile, content) = AgentGateway.ParseDeliveryResult(text, "xlsx_");
        Assert.True(hasFile);
        Assert.Equal(14, content);
    }

    [Fact]
    public void Xlsx_DeliveryThickness_OneRowTableCountsAsThin()
    {
        var text = "{\"ok\":true,\"sheets\":1,\"rows\":1,\"produce_file\":{\"path\":\"/a.xlsx\"}}";
        var (hasFile, content) = AgentGateway.ParseDeliveryResult(text, "xlsx_");
        Assert.True(hasFile);
        Assert.True(content < 3, "只有表头 / 一行的表应低于交付阈值");
    }

    [Fact]
    public void Pptx_MetricIsNotPollutedByRowsInTheSamePayload()
    {
        // 度量按类型选：一份 pptx 结果里即使出现 rows 字段，也不该被拿来做厚度判定
        var text = "{\"ok\":true,\"slides\":2,\"rows\":99,\"produce_file\":{\"path\":\"/a.pptx\"}}";
        var (_, content) = AgentGateway.ParseDeliveryResult(text, "pptx_");
        Assert.Equal(2, content);
    }

    // ---------- pdf 技能：正文在 blocks（或直接给 markdown） ----------

    private static AgentSkillDefinition PdfSkill() => new()
    {
        SkillId = "pdf_doc", Name = "PDF 文档生成", Kind = AgentSkillKind.Dotnet,
        Description = "生成打印级 PDF（报告 / 方案 / 简历），含封面、目录与多种内容块。", Body = "public class S { }",
    };

    [Fact]
    public void Pdf_IsRecognizedAsPdfSkill()
    {
        Assert.True(AgentGatewayHelpers.IsPdfSkill(PdfSkill()));
        Assert.False(AgentGatewayHelpers.IsPdfSkill(DocxSkill()));
        Assert.False(AgentGatewayHelpers.IsPdfSkill(XlsxSkill()));
    }

    [Fact]
    public void Pdf_IsNotInferredFromDescriptionAlone()
    {
        // 很多技能描述里会顺带提“可导出 PDF”；若按描述匹配，会把吃 sections 的技能
        // 错当 PDF 技能、用 blocks 口径去卡它。只认 id / 名称。
        var docxLike = new AgentSkillDefinition
        {
            SkillId = "report_export", Name = "报告导出", Kind = AgentSkillKind.Dotnet,
            Description = "把内容导出为规范排版的 Word 文档，也可另存为 PDF。", Body = "public class S { }",
        };
        Assert.False(AgentGatewayHelpers.IsPdfSkill(docxLike));
    }

    [Fact]
    public void Pdf_AcceptedWhenBlocksHaveTypes()
    {
        var ok = AgentGatewayHelpers.ValidateDocumentSkillInput(PdfSkill(),
            "{\"title\":\"白皮书\",\"blocks\":[{\"type\":\"h1\",\"text\":\"一、概述\"},"
            + "{\"type\":\"p\",\"text\":\"正文…\"}]}");
        Assert.Null(ok);
    }

    [Fact]
    public void Pdf_AcceptedForMarkdownPayload()
    {
        Assert.Null(AgentGatewayHelpers.ValidateDocumentSkillInput(PdfSkill(),
            "{\"markdown\":\"# 标题\\n\\n正文\"}"));
    }

    [Fact]
    public void Pdf_RejectedWhenBlocksMissing()
    {
        var why = AgentGatewayHelpers.ValidateDocumentSkillInput(PdfSkill(), "{\"title\":\"白皮书\"}");
        Assert.NotNull(why);
        Assert.Contains("blocks", why);
    }

    [Fact]
    public void Pdf_RejectedWhenNoBlockHasType()
    {
        var why = AgentGatewayHelpers.ValidateDocumentSkillInput(PdfSkill(),
            "{\"blocks\":[{\"text\":\"没有 type\"}]}");
        Assert.NotNull(why);
        Assert.Contains("type", why);
    }

    [Fact]
    public void Pdf_DeliveryThickness_UsesBlocks()
    {
        var text = "{\"ok\":true,\"pages\":5,\"blocks\":18,\"produce_file\":{\"path\":\"/a.pdf\"}}";
        var (hasFile, content) = AgentGateway.ParseDeliveryResult(text, "pdf_");
        Assert.True(hasFile);
        Assert.Equal(18, content);
    }

    // ---------- 编排：要 Excel / PDF 时点名内置技能 ----------

    [Fact]
    public void NoGap_WhenTeamAlreadyHasXlsxSkill()
    {
        var plan = Plan(("writer", ["copywriting"]), ("book", ["xlsx_book"]));
        Assert.Null(AgentOrchestrator.DetectDeliveryGap(plan, "把数据整理成一张 Excel 表"));
    }

    [Fact]
    public void NoGap_WhenTeamAlreadyHasPdfSkill()
    {
        var plan = Plan(("writer", ["copywriting"]), ("printer", ["pdf_doc"]));
        Assert.Null(AgentOrchestrator.DetectDeliveryGap(plan, "给我出一份 PDF 报告"));
    }

    [Fact]
    public void GapHint_NamesTheBuiltinXlsxSkill()
    {
        var plan = Plan(("writer", ["copywriting"]));
        var warn = AgentOrchestrator.DetectDeliveryGap(plan, "整理成 Excel 表发我");
        Assert.NotNull(warn);
        Assert.Contains("xlsx_book", warn);
    }

    [Fact]
    public void GapHint_NamesTheBuiltinPdfSkill()
    {
        var plan = Plan(("writer", ["copywriting"]));
        var warn = AgentOrchestrator.DetectDeliveryGap(plan, "要一份 pdf 交付稿");
        Assert.NotNull(warn);
        Assert.Contains("pdf_doc", warn);
    }

    [Fact]
    public void Prompt_TellsModelToReuseBuiltinXlsxAndPdfSkills()
    {
        var prompt = AgentOrchestrator.BuildPromptForTest("做一个团队，最终要 excel 和 pdf", null);
        Assert.Contains("xlsx_book", prompt);
        Assert.Contains("pdf_doc", prompt);
    }

    // ---------- 计划空答复兜底：必须说实话，不能宣称“已收集各岗位结果” ----------

    [Fact]
    public void PlanFallback_NoStepRan_SaysSoAndListsSkips()
    {
        // 实测踩到：计划里每一步都被跳过（例如只有一步“调 pptx 技能”，它被改走交付兑底），
        // 旧实现却固定回“本轮已按计划收集了各岗位的结果，但未汇总出可展示的最终文本”——
        // 用户拿到的是一句与事实不符的空话，也不知道下一步该怎么做。
        var text = AgentGateway.BuildNoOutputFallback(0,
            ["第1步「PPT 生成」：文档生成技能不在此阶段执行，改由交付环节直接生成文件"]);

        Assert.Contains("没有一步真正执行", text);
        Assert.Contains("第1步「PPT 生成」", text);
        Assert.DoesNotContain("已按计划收集了各岗位的结果", text);
        Assert.DoesNotContain("已按计划收集各岗位", text);
    }

    [Fact]
    public void PlanFallback_StepsRanButProducedNothing_ReportsTheCount()
    {
        // 区分“一步都没跑”与“跑了但没有任何产出”：后者不能说自己什么都没做
        var text = AgentGateway.BuildNoOutputFallback(2, []);

        Assert.Contains("2 个步骤", text);
        Assert.DoesNotContain("没有一步真正执行", text);
        Assert.DoesNotContain("已按计划收集了各岗位的结果", text);
    }

    [Fact]
    public void PlanFallback_OffersANextStepInsteadOfJustApologizing()
    {
        var text = AgentGateway.BuildNoOutputFallback(0, []);
        Assert.Contains("告诉我需要生成的具体内容", text);
    }

    // ---------- 交付兑底拿到计划内产出（否则从用户原始请求从零重写） ----------

    [Fact]
    public void DeliveryPrompt_UsesUpstreamDraftAsSourceMaterial()
    {
        // 实测踩到：计划阶段各岗位已写好稿子，交付岗却只看着用户那句原始请求重写，
        // 终稿与前面成果对不上，用户还白等了整个计划的时间。
        var prompt = AgentGateway.BuildDeliveryPrompt("Word 文档", "docx_report", "帮我写推广文案，要 word",
            upstreamDraft: "【文案写手】\n知聚是一个多智能体协作平台……");

        Assert.Contains("组织内已产出的内容", prompt);
        Assert.Contains("知聚是一个多智能体协作平台", prompt);
        Assert.Contains("不要重新构思一遍", prompt);
        Assert.Contains("docx_report", prompt);
    }

    [Fact]
    public void DeliveryPrompt_WithoutDraft_HasNoDraftSection()
    {
        var prompt = AgentGateway.BuildDeliveryPrompt("Word 文档", "docx_report", "写简介并导出");
        Assert.DoesNotContain("组织内已产出的内容", prompt);
    }

    [Fact]
    public void DeliveryPrompt_DraftIsTruncatedToKeepPromptBounded()
    {
        // 计划各步产出可能很长：必须截断（取尾部，靠后的步通常是汇总/定稿），否则撑爆下游上下文
        var huge = new string('甲', AgentGateway.MaxUpstreamDraftChars + 5000) + "尾部标记";
        var prompt = AgentGateway.BuildDeliveryPrompt("Word 文档", "docx_report", "要 word", upstreamDraft: huge);

        Assert.Contains("尾部标记", prompt);
        Assert.Contains("前文从前略", prompt);
        Assert.True(prompt.Length < AgentGateway.MaxUpstreamDraftChars + 3000,
            $"提示词应被截断，实际长度 {prompt.Length}");
    }

    [Fact]
    public void DeliveryPrompt_RetryAlsoCarriesTheDraft()
    {
        // 第二次尝试同样要用上素材，否则重试等于从零再来一遍
        var prompt = AgentGateway.BuildDeliveryPrompt("Word 文档", "docx_report", "要 word",
            retryNoToolCall: true, upstreamDraft: "【文案写手】\n完整初稿内容");

        Assert.Contains("并没有真正调用工具", prompt);
        Assert.Contains("完整初稿内容", prompt);
    }

    // ---------- 交付物类型：用户那句没格式词时听计划的 ----------

    [Theory]
    [InlineData("pptx_deck", "pptx_", "演示文稿")]
    [InlineData("xlsx_book", "xlsx_", "Excel 表格")]
    [InlineData("pdf_doc", "pdf_", "PDF 文档")]
    [InlineData("docx_report", "docx_", "Word 文档")]
    [InlineData("md_to_docx", "docx_", "Word 文档")] // 后缀式命名也要认
    [InlineData("copywriting", null, null)]           // 不是文件技能 → 不插手
    [InlineData("", null, null)]
    [InlineData(null, null, null)]
    public void DeliverableFromSkillId_MapsPlanSkillToDeliverable(string? skillId, string? prefix, string? label)
    {
        // 实测踩到：单聊里接着说“希望有一些插图”，句中没有格式词 → 交付判断直接放弃 → 用户什么都没拿到；
        // 而前一句“我希望ppt是绿色的”能出文件，只因句子里恰好有“ppt”。
        // 计划已经点名了文件技能，就该据它确定交付物。
        var got = AgentGateway.DeliverableFromSkillId(skillId);
        if (prefix is null)
        {
            Assert.Null(got);
            return;
        }
        Assert.NotNull(got);
        Assert.Equal(prefix, got!.Value.SkillPrefix);
        Assert.Equal(label, got.Value.Label);
    }

    [Fact]
    public void WantedDeliverable_ReturnsNullForIterationWithoutFormatWord()
    {
        // 记录缺陷现场：这句本身确实判不出交付物 —— 所以必须靠计划里的技能补齐，
        // 而不能再依赖 WantedDeliverable(context.Content) 作为“要不要交付”的唯一判据。
        Assert.Null(AgentGateway.WantedDeliverable("希望有一些插图"));
        Assert.NotNull(AgentGateway.WantedDeliverable("我希望ppt是绿色的ai风格"));
    }

    // ---------- 计划说明什么时候补发（不能与交付结果叠成两条矛盾说明） ----------

    [Fact]
    public void PlanText_AppendedWhenDeliverySilentlyGaveUp()
    {
        // 交付没接手，且计划侧本来就不准备发正文 → 不补就是一条空消息（用户反馈的正是这个）
        Assert.True(AgentGateway.ShouldAppendPlanText(awaitingInteraction: false, handled: false, planText: "（说明）"));
    }

    [Theory]
    [InlineData(false, true, "（说明）")]   // 交付已给用户结果 → 不能再叠一段“什么都没跑”
    [InlineData(true, false, "（说明）")]    // 正在等审批：正文已清空，会由恢复流接管
    [InlineData(false, false, "")]           // 没东西可补
    [InlineData(false, false, null)]
    public void PlanText_NotAppendedWhenItWouldConfuse(bool awaiting, bool handled, string? planText)
    {
        Assert.False(AgentGateway.ShouldAppendPlanText(awaiting, handled, planText));
    }

    [Fact]
    public void PlanText_AppendedWhenDeliveryFailedToProduceFile()
    {
        // 实测：交付岗跑了两次都没真调出文件（Handled=true，只发了一句“没生成文件”），
        // 而计划期间各岗位已产出的正文素材全被丢掉 → 用户只看到一句失败话术。
        // 这时必须把计划素材补上。
        Assert.True(AgentGateway.ShouldAppendPlanText(awaitingInteraction: false, handled: true,
            planText: "【文案写手】\n知聚是一个多智能体协作平台……", fileGenerationFailed: true));
    }

    [Fact]
    public void PlanText_NotAppendedForFailedFileWhenThereIsNothingToShow()
    {
        // 没素材可补时不能凭空造一段说明
        Assert.False(AgentGateway.ShouldAppendPlanText(awaitingInteraction: false, handled: true,
            planText: "", fileGenerationFailed: true));
        // 正在等审批时依然不补（正文已清空，由恢复流接管）
        Assert.False(AgentGateway.ShouldAppendPlanText(awaitingInteraction: true, handled: true,
            planText: "（说明）", fileGenerationFailed: true));
    }

    // ---------- 派发子智能体的提示词（不能把“用户原始请求”弄丢） ----------

    [Fact]
    public void AssignmentPrompt_AlwaysCarriesUserRequest_EvenWhenPreviousStepIsPlaceholder()
    {
        // 实测（用户反复反馈“已经出现好多回”）：计划的第二步起，子岗位回
        // “我这边还没有收到具体需求（消息内容为空）”，点「重新回答」又正常。
        // 根因：原实现每步跑完把“问题”整段替换成上一步产出；上一步产出为空（退化成占位串）时，
        // 后续岗位就再也看不到用户到底要什么。
        var prompt = AgentGateway.BuildAssignmentPrompt("项目总监", "用户原始请求：做一个推广文案团队，我要最终形成word文档",
            previousStep: "（未返回内容）", priorSummary: null);

        Assert.Contains("用户原始请求", prompt);
        Assert.Contains("做一个推广文案团队", prompt);
        Assert.Contains("项目总监", prompt);
        // 占位串不能当“上一步产出”投喂，否则模型会把它当成要解决的问题
        Assert.DoesNotContain("（未返回内容）", prompt);
    }

    [Fact]
    public void AssignmentPrompt_KeepsRequestWhenPreviousStepIsUnrelated()
    {
        // 上一步产出有内容但与需求无关（比如只是一段寒暄），也不能把需求顶替掉
        var prompt = AgentGateway.BuildAssignmentPrompt("项目总监", "用户原始请求：写“知聚”市场推广文案",
            previousStep: "好的，我理解了。", priorSummary: "【需求分析师】\n受众是中小企业 IT 负责人");

        Assert.Contains("写“知聚”市场推广文案", prompt);
        Assert.Contains("上一步产出（仅供参考，不是你要回答的问题）", prompt);
        Assert.Contains("前序各岗位已产出", prompt);
    }

    [Fact]
    public void AssignmentPrompt_NamesTheDispatcherNotTheTarget()
    {
        // 原实现把指派者写成目标岗位自己（“你正被「目标」指派处理”）→ 等于告诉模型“你自己指派你自己”
        var prompt = AgentGateway.BuildAssignmentPrompt("项目总监", "用户原始请求：出一份 PPT", null, null);

        Assert.Contains("你正被上级「项目总监」指派处理", prompt);
        Assert.Contains("用户原始请求", prompt);
        // 没有上一步产出时不应出现“仅供参考”那一节（页脚里提到的名字不算）
        Assert.DoesNotContain("【上一步产出（仅供参考", prompt);
    }

    [Fact]
    public void AssignmentPrompt_IsBounded()
    {
        // 计划输入 / 前序产出都可能很长，必须截断，否则撑爆下游上下文
        var request = "请求头部标记" + new string('甲', AgentGateway.MaxAssignmentRequestChars + 2000) + "请求尾部标记";
        var prior = new string('乙', AgentGateway.MaxAssignmentPriorChars + 2000) + "前序尾部标记";
        var prompt = AgentGateway.BuildAssignmentPrompt("总监", request, "上一步尾部标记" + new string('丙', 6000), prior);

        Assert.Contains("前文从前略", prompt);
        Assert.Contains("前序尾部标记", prompt);
        Assert.True(prompt.Length < AgentGateway.MaxAssignmentRequestChars
                + AgentGateway.MaxAssignmentPreviousChars + AgentGateway.MaxAssignmentPriorChars + 600,
            $"提示词应被截断，实际长度 {prompt.Length}");
    }
}
