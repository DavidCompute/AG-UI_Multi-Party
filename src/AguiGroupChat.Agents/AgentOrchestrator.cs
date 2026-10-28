using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace AguiGroupChat.Agents;

/// <summary>
/// 一键组织编排：根据一句话需求，用模型生成<b>数字员工组织架构方案</b>——岗位清单、
/// 每个岗位要挂载的技能、以及岗位之间连接（向下指派 / 向上提升 / 汇报中继）。
/// 生成结果<b>不落库</b>，由前端预览确认后再交实际创建端点落库（见 AgentApi.OrchestrateApply）。
/// Provider=mock 时输出确定性模板（无模型调用），便于本地演示与测试。
/// </summary>
public static class AgentOrchestrator
{
    /// <summary>
    /// 技能库中可复用技能的轻量投影（供编排提示词列举）。只带模型判断所需的最小信息：
    /// id / 名称 / 用途说明 / 类型 —— 不带正文（正文进提示词会很长且无用）。
    /// </summary>
    /// <param name="QualityNote">近 30 天产物质量画像（1.0.348；无记录时为 null），用于让模型优先选可靠的技能。</param>
    /// <param name="ExperienceNote">未解决的历史失败经验（1.0.350；无则为 null），用于让模型在岗位说明里写明规避步骤。</param>
    /// <param name="HealthNote">近 30 天修复健康度（1.0.361；只在修复不稳时才有值），用于提醒模型慎用或设计兵底方案。</param>
    public sealed record ReusableSkill(
        string SkillId, string Name, string Description, string Kind,
        string? QualityNote = null, string? ExperienceNote = null, string? HealthNote = null);

    /// <summary>
    /// 从技能库构造可复用技能清单（只列非内置的也能列；内置的同样可复用）。
    /// <paramref name="quality"/> 为近 30 天技能质量画像：带上时每个候选会附上合格率 / 均分 / 主要缺失项，
    /// 模型据此优先选择稳定技能、避开持续低分的技能。
    /// <param name="lessons">为岗位经验库（1.0.350）：带上时把未解决的失败经验也列进候选，
    /// 让编排能提前设计规避步骤（如岗位说明里要求先备齐正文 / 页数）。</param>
    /// <param name="loopHealth">为近 30 天修复健康度（1.0.361）：带上时对“修也修不好”的技能附提醒，
    /// 让模型避开反复回滚的技能，或至少把兵底方案（先跑回归 / 换同类技能）写进岗位说明。</param>
    public static IReadOnlyList<ReusableSkill> ToReusableSkills(
        IEnumerable<AgentSkillDefinition>? skills,
        IReadOnlyDictionary<string, SkillQualityProfile>? quality = null,
        SkillLessonStore? lessons = null,
        IReadOnlyDictionary<string, SkillLoopHealth>? loopHealth = null)
        => (skills ?? [])
            .Where(s => !string.IsNullOrWhiteSpace(s.SkillId))
            // 已暂停（产物质量连续低分）的技能不再作为编排候选（1.0.347）：
            // 否则模型会把它排进岗位，而运行时又因暂停而不挂载，形成“看起来有技能、实际不执行”的假交付。
            .Where(s => !s.Suspended)
            // org_deploy / org_design 属构建师自身的工具，不应被编排挂到普通岗位
            .Where(s => s.Kind != AgentSkillKind.Org_deploy)
            .Where(s => !string.Equals(s.SkillId, "org_design", StringComparison.OrdinalIgnoreCase))
            .Select(s =>
            {
                var qualityNote = quality is not null && quality.TryGetValue(s.SkillId, out var q) && q.Runs > 0
                    ? q.Describe() : null;
                var failures = lessons?.FailuresForSkill(s.SkillId, 2) ?? [];
                var experienceNote = failures.Count > 0
                    ? string.Join("；", failures.Select(l => l.ToPromptLine()))
                    : null;
                var healthNote = loopHealth is not null
                                 && loopHealth.TryGetValue(s.SkillId, out var h) && h.IsRisky
                    ? (h.IsAvoid ? "修复后仍不稳定：" : "修复不够稳：") + h.Describe()
                    : null;
                return new ReusableSkill(
                    s.SkillId, s.Name ?? s.SkillId, s.Description ?? "", s.Kind.ToString().ToLowerInvariant(),
                    qualityNote, experienceNote, healthNote);
            })
            .ToList();
    /// <summary>生成组织方案。真实模型走 OpenAI 兼容接口；mock 走确定性模板。</summary>
    /// <param name="reusableSkills">技能库中可复用的现成技能（可为空）。编排优先引用它们，避免重复造轮子。</param>
    /// <param name="allowDotnet">调用者是否有权建 dotnet 技能（仅系统管理员）。false 时提示词会引导模型避开它，
    /// 否则模型选了 dotnet 会在落库阶段直接被权限校验拒掉（见 OrgApplyEngine）。</param>
    public static async Task<OrchestrationPlan> GenerateAsync(AgentOptions options, string requirement, ILogger logger, CancellationToken ct,
        IReadOnlyList<ReusableSkill>? reusableSkills = null, bool allowDotnet = true)
    {
        var req = (requirement ?? "").Trim();
        if (req.Length < 2) throw new InvalidOperationException("需求描述至少 2 个字符");
        if (req.Length > 5000) throw new InvalidOperationException("需求描述最长 5000 字符");

        if (string.Equals(options.Provider, "mock", StringComparison.OrdinalIgnoreCase))
            return BuildTemplate(req);

        IChatClient client;
        try
        {
            var isDeepSeek = string.Equals(options.Provider, "deepseek", StringComparison.OrdinalIgnoreCase);
            var ov = AgentCatalog.StructuredFastModel(isDeepSeek); // 组织方案按严格 JSON 解析：格式任务，不进 reasoner
            client = AgentCatalog.BuildOpenAIChatClient(
                options, new AgentDefinition { AgentId = "orchestrator", Nickname = "组织编排器" }, isDeepSeek, ov, AgentCatalog.DeepSeekThinking.Disabled).AsIChatClient();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("组织编排需要可用的模型配置（" + ex.Message + "）", ex);
        }

        try
        {
            var prompt = BuildPrompt(req, reusableSkills, allowDotnet);
            var resp = await client.GetResponseAsync([new ChatMessage(ChatRole.User, prompt)], cancellationToken: ct);
            var text = resp.Text?.Trim();
            if (string.IsNullOrWhiteSpace(text))
                throw new InvalidOperationException("组织编排返回为空，请重试");
            var plan = Parse(text);
            logger.LogInformation("已根据需求生成组织方案（{Agents} 名 / {Skills} 技能）：{Req}",
                plan.Agents.Count, plan.Skills.Count, Truncate(req));
            return plan;
        }
        finally
        {
            client.Dispose();
        }
    }

    /// <summary>流式生成组织方案文本：逐个 token 产出已到达的文本增量，供调用方实时转发（SSE）/ 展示生成过程。
    /// 结束后把完整文本交给调用方 <see cref="Parse"/> 得到结构化方案。mock 模式无真实模型，整段模板拆几段产出。</summary>
    public static async IAsyncEnumerable<string> StreamTextAsync(
        AgentOptions options, string requirement, ILogger logger,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct,
        IReadOnlyList<ReusableSkill>? reusableSkills = null, bool allowDotnet = true)
    {
        var req = (requirement ?? "").Trim();
        if (string.Equals(options.Provider, "mock", StringComparison.OrdinalIgnoreCase))
        {
            var text = JsonSerializer.Serialize(BuildTemplate(req));
            const int chunk = 80;
            for (var i = 0; i < text.Length; i += chunk)
            {
                ct.ThrowIfCancellationRequested();
                yield return text.Substring(i, Math.Min(chunk, text.Length - i));
                await Task.Yield();
            }
            yield break;
        }

        IChatClient client;
        try
        {
            var isDeepSeek = string.Equals(options.Provider, "deepseek", StringComparison.OrdinalIgnoreCase);
            var ov2 = AgentCatalog.StructuredFastModel(isDeepSeek); // 同：流式组织的 JSON 也按常规模型，保证速度与有效
            client = AgentCatalog.BuildOpenAIChatClient(
                options, new AgentDefinition { AgentId = "orchestrator", Nickname = "组织编排器" }, isDeepSeek, ov2, AgentCatalog.DeepSeekThinking.Disabled).AsIChatClient();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("组织编排需要可用的模型配置（" + ex.Message + "）", ex);
        }

        try
        {
            var prompt = BuildPrompt(req, reusableSkills, allowDotnet);
            await foreach (var update in client.GetStreamingResponseAsync(
                [new ChatMessage(ChatRole.User, prompt)], cancellationToken: ct))
            {
                if (string.IsNullOrEmpty(update.Text)) continue;
                yield return update.Text;
            }
            logger.LogInformation("组织编排流式文本已接收完毕：{Req}", Truncate(req));
        }
        finally
        {
            client.Dispose();
        }
    }

    private static string BuildPrompt(string requirement, IReadOnlyList<ReusableSkill>? reusableSkills = null, bool allowDotnet = true)
    {
        var deliberate = AgentCatalog.DeliberateFirstLine;
        // kind 引导按权限分支：dotnet 仅系统管理员可建（OrgApplyEngine 会校验），
        // 非管理员编排时必须引导模型避开，否则选了 dotnet 会在落库时被直接拒掉。
        var kindLines = allowDotnet
            ? "- kind：按岗位职责<b>智能选择</b>：\n" +
              "    dotnet：需要生成/处理文档（Word/Excel/PPT/PDF）、图片、加解密、解析结构化数据、读写文件等「要真干活」的能力——" +
              "<b>优先选它</b>。正文为 C# 源码，须含 public class Skill { public static string Run(string input) }，" +
              "可用平台自带 .NET 运行时与 BCL，必要时在首行写 #r \"nuget: 包名, 版本\" 联网还原第三方库。executionLocation 固定 server。\n" +
              "    shell：只能在目标机器上用 <b>bash + coreutils + curl + perl</b> 完成的事（文本处理、文件操作、HTTP 调用）。" +
              SkillSandboxCapabilities.Describe() + "\n" +
              "    http：调用外部 HTTP 接口。\n" +
              "    prompt：纯文本/知识/写作/流程模板（没有外部执行）。\n" +
              "禁则将文档处理、图片处理、复杂计算等本应用 dotnet 的活写成 shell。\n"
            : "- kind：按岗位职责<b>智能选择</b>——需要本机/系统操作（查电脑信息、执行命令、操作文件/磁盘等）用 <b>shell</b>；" +
              "需要调用外部 HTTP 接口用 <b>http</b>；纯文本/知识/写作/流程模板用 <b>prompt</b>。不要一律 prompt。\n" +
              "（dotnet 类型仅系统管理员可建，本次调用者无权，<b>禁止</b>返回 kind=dotnet，也不要试图绕过。）\n" +
              "- shell 只能使用目标机器上确实存在的命令：" + SkillSandboxCapabilities.Describe() + "\n";
        return deliberate +
            "你是企业数字化组织架构设计师。根据用户的一句需求，设计一套「数字员工组织架构 + 各岗位技能 + 岗位连接」方案；" +
            "先按开头的“取舍概述”要求简述后，再只输出最终的一段 JSON（除简述外的成稿不要附其它文字）。\n\n" +
            "数字员工（agents）字段：\n" +
            "- agentId：ASCII 字母/数字/下划线/连字符（≤40）。\n" +
            "- nickname：中文角色名。\n" +
            "- description：一句话职责（供模型指派时判断语境）。\n" +
            "- instructions：该角色的系统提示（身份/职责/风格，150~300 字）。\n" +
            "- triggerMode：mentioned（默认，@ 触发）。\n" +
            "- skillIds：本岗位要挂载的技能 ID 列表（引用下方 skills 里的 skillId）。\n" +
            "- assignmentIds：向下指派白名单（可把不归属自己的任务指派给哪个下级，填下级 agentId）。\n" +
            "- escalationAgentId：向上提升目标（通常是其上级 agentId）。\n" +
            "- relayToAgentId：整轮交接目标（可选，较少用）。\n" +
            "- memoryProfile：按【该岗位实际的记忆需求】从五档拟人 preset 里选一个<b>key 字符串</b>（只影响它日后如何回忆知聚历史，不改变能力本身）：\n" +
            "    broad 广记型（记得多、细节易混，适合要接收大量高频往来的一线/客服/信息岗）；\n" +
            "    deep 深记型（记得少而久、宁缺毋滥，适合要长期记牢关键决定与重要上下文的统筹/主管岗）；\n" +
            "    slowToLearn 难录入型（新信息需反复几次才刻入，适合重复性、按固定套路执行后再不会忘的岗位）；\n" +
            "    cueDependent 存得住想不起型（见线索提示才想起，适合需记住客户/合作方过往偏好、常要顺着话题回想的顾问/售后/客户成功岗）；\n" +
            "    fastForgetting 快速遗忘型（旧事淡忘、只清楚近期，适合只处理当下、不必背旧账的值班/速查岗）。\n" +
            "    拿不准就 null（沿用全局默认召回）；不要在 memoryProfile 里塞对象或编造其它值。\n\n" +
            "技能（skills）字段：\n" +
            "- skillId：ASCII 且 ≤40。\n" +
            "- name：中文名。\n" +
            "- description：给模型的调用说明（何时调用/参数/返回，50~150 字）。\n" +
            kindLines +
            "- body：prompt 填模板文本；dotnet 填完整可编译的 C# 源码（不要围栏）；shell 填命令/脚本（Linux 用 bash/sh）；" +
            "http 填 {\"method\":\"GET\",\"url\":\"${query}\",\"headers\":{}}。\n" +
            "- executionLocation：dotnet 用 <b>server</b>（服务端编译执行，可直接产出文件）；" +
            "shell 用 <b>client</b>（在本机执行，需批准）；http/prompt 用 server。\n" +
            "- requiresApproval：shell 一律 true；dotnet 一律 true；http 一律 true；executionLocation=client 一律 true；纯 prompt 服务端可 false。\n\n" +
            "连接原则（务必同时给全<b>两个方向</b>的连接，不要只给“问题提升”）：\n" +
            "- 有直接下级的岗位（主管/组长/经理…）必须在 assignmentIds 里列出它的<b>全部直接下级 agentId</b>——这是“任务指派”链（上级可把任务指派给下级）；\n" +
            "- 非顶层的岗位把 escalationAgentId 指向自己的直接上级——这是“问题提升”链；\n" +
            "- 顶层主管的 escalationAgentId 留空；叶子执行岗没有下级、assignmentIds 留空。\n" +
            "要点：凡是<b>被别人设为 escalationAgentId 的岗位</b>，它的 assignmentIds 必须包含那些提升到它的下级，不能留空——否则只有“下级往上报问题”、没有“上级往下派任务”，组织不成立。\n" +
            // 交付闭环：这是编排最容易被忽略、后果最实的一点 ——
            // 早先提示词只描述“岗位 + 技能 + 连接”，没要求团队能真正把东西交到用户手上，
            // 于是常出现：用户明确要 Word，团队里却没有任何一个能产出 .docx 的岗位；
            // 或把“出报表”写成只能生成文字描述的 prompt 技能。用户最终拿不到东西。
            "【交付闭环（很重要，必须满足）】\n" +
            "- 先判断：用户的需求里<b>最终要让用户拿到什么</b>（一份 Word / Excel / PDF / PPT 文件、一份可直接用的成品等）。\n" +
            "- 队伍里<b>必须至少有一个岗位真正具备产出该交付物的能力</b>，且它的技能能实际执行（dotnet / shell / http），" +
            "而不是只能“写出文字描述”的 prompt 技能。\n" +
            "    · <b>要 Word 就优先直接引用内置技能 docx_report / docx_gongwen / docx_notice</b>（它们已能真产出 .docx），\n" +
            "      把它们写进交付岗的 skillIds，<b>不要</b>自己再造一个只能写固定文字的“文档生成”技能 —— 实测踩到：\n" +
            "      模型自造了 docx_build_dotnet（正文只 new Text(\"交付稿\") 写死一行字）与 docx_pack_shell（只 ls 列目录），\n" +
            "      既产不出真内容、也不知产物如何交付，结果反复重试直到触发平台的交互轮数保护而终止。\n" +
            "    · 仅当内置技能确实不满足需求（如需特殊版式/其他格式）才新造，且自造的产出技能<b>必须真正把内容写进文件</b>。\n" +
            "    · <b>要 PPT / 演示文稿就优先直接引用内置技能 pptx_deck</b>（封面/目录/章节分隔/内容/两栏/表格/" +
            "      指标卡/引言/图片/图表/小结/结束页等页型已齐备，支持主题配色与字体），把它写进交付岗的 skillIds。\n" +
            "    · <b>要 Excel / 表格就优先直接引用内置技能 xlsx_book</b>（多工作表、列定义与数字格式、" +
            "      合计行、冻结窗格与筛选、公式优先），把它写进交付岗的 skillIds。\n" +
            "    · <b>要 PDF 就优先直接引用内置技能 pdf_doc</b>（设计令牌驱动的封面/目录/标题/正文/列表/表格/图表等，" +
            "      输出为可打印的 PDF），把它写进交付岗的 skillIds。\n" +
            "    · xlsx / pdf 同理：先看技能库里有没有现成的，没有才新造能真写文件的 dotnet 技能。\n" +
            "    · <b>禁止</b>用“只能生成文字”的 prompt 技能冒充交付能力；也<b>不要</b>造只打印目录 / 只写占位文字的空壳技能。\n" +
            "- 自造的产出技能成功时必须返回 <b>produce_file</b>（含 path / name / bytes），且文件必须写入实际正文，" +
            "不能只有标题 / 封面 / 空表；平台会按格式校验产物，空壳会被判为未交付。\n" +
            "- 该交付岗应是<b>交付链末端</b>（叶子岗、无 assignmentIds）：上游出内容 → 它负责生成文件。\n" +
            "- 交付岗的 instructions 要写清楚：<b>用户直接要求交付文件时，应当直接产出文件，不要因为“未走完内部审批”而拒交</b>；" +
            "内部质检 / 签核只能作为“会在文件里标注待核实项”这类非阻断手段，不能变成拿不到文件的理由。\n" +
            // 实测踩到：模型把“内部流程”写成了对用户的准入门槛 ——
            // 交付岗人设写成“仅接收主笔签发的定稿版本生成 .docx，不接受未过合规的稿件”，
            // 结果用户直接要 Word 时，该岗位不生成文件，反问用户要定稿 / 合规结论，用户啥也拿不到。
            "- 交付岗的 instructions <b>严禁</b>出现把内部流程当成交付前提的写法。具体禁止这类句子：\n" +
            "    · “仅接收…才生成 / 仅接收…才导出”（如“仅接收主笔签发的定稿版本生成 .docx”）；\n" +
            "    · “不接受未过合规的稿件 / 未过审不出文件 / 没有定稿我不出文件”；\n" +
            "    · “须先经…审批 / 先确认…后再生成”（把审批当成交付前置条件）。\n" +
            "  正确写法：<b>拿到任何可用材料就先出一版文件</b>（材料不完整时也要出，并在文内列入“待确认项”），" +
            "仅在确实一点材料都没有时才回报缺少什么。例如可写：\n" +
            "    · “根据现有材料直接整理为 .docx；材料不完整时在附页列出待确认项，不以等待定稿为由不出文件。”\n" +
            "- 上述“直接交付”只约束<b>交付岗自己</b>：其它岗位（主笔 / 合规 / 审核）仍可各司其职，" +
            "但它们的规矩不能变成交付岗拒交的理由。\n" +
            "- 上游岗位的 instructions 里说明“定稿后交 X 岗导出文件”，让交付链路在职责描述上也是通的。\n" +
            "- <b>技能要一次性把活干完</b>：要交付的岗位不需要再编“归档 / 打包 / 列清单”之类的小步骤技能，\n" +
            "  一个能产出文件的技能就够；多而无用的执行技能只会让链路反复停下来等审批。\n" +
            "技能要贴合岗位职责，数量 1~6 个。\n\n" +
            BuildReusableSkillsSection(reusableSkills) +
            "只输出最终 JSON（如下结构，字段补齐、可加多余岗位/技能项；简述已在开头给出，最终成稿不再重复简述，也不要 " + Fence() + " 围栏）：\n" +
            "{\"title\":\"<组织名>\",\"agents\":[{\"agentId\":\"\",\"nickname\":\"\",\"description\":\"\",\"instructions\":\"\",\"triggerMode\":\"mentioned\",\"skillIds\":[],\"assignmentIds\":[],\"escalationAgentId\":null,\"relayToAgentId\":null,\"memoryProfile\":\"deep\"}],\"skills\":[{\"skillId\":\"\",\"name\":\"\",\"description\":\"\",\"kind\":\"prompt\",\"body\":\"\",\"executionLocation\":\"server\",\"requiresApproval\":false}]}\n\n" +
            "用户需求：" + requirement;
    }

    /// <summary>
    /// 检查方案的<b>交付闭环</b>：用户要的交付物，队伍里是否真有人能产出。
    ///
    /// <para>
    /// 为什么单独做一次校验（而不是只靠提示词）：提示词只能“尽量引导”，模型仍会波动 ——
    /// 实测同样需求，有时产出带交付岗，有时全是 prompt 技能、没人能出文件。
    /// 这里做确定性检查，把问题<b>提前暴露在前端预览</b>，而不是等用户拿到一团文字才发现。
    /// </para>
    ///
    /// <remarks>
    /// 判定口径（与 <c>WantedDeliverable</c> 一致：<b>明确格式词优先、中文泛称靠后</b>）：
    /// 需求里提到 ppt/pptx/演示文稿/幻灯片 → 需有 pptx_；xlsx/excel → xlsx_；word/docx → docx_；pdf → pdf_；
    /// 都没命中时再看中文泛称：「文档」→ docx_、「表格」→ xlsx_。
    /// 注：新造技能也计入（模型可能自建产出能力）；只看 skillId 前缀，不解析正文。
    /// </remarks>
    /// </summary>
    /// <returns>缺失交付能力的提醒文案；无问题返回 null。</returns>
    public static string? DetectDeliveryGap(OrchestrationPlan plan, string requirement)
    {
        var req = (requirement ?? "").ToLowerInvariant();
        bool Has(params string[] keys) => keys.Any(k => req.Contains(k, StringComparison.OrdinalIgnoreCase));

        string? prefix = null;
        var label = "";
        // 明确的格式词优先：一份 PPT 里的“表格”是页内元素，不是要交付 Excel。
        // 实测踩到：先判“表格”会把“做份 PPT，含对比表格”判成 Excel 交付，
        // 于是本函数会因为“没有 xlsx_ 技能”而误报，而真正的 pptx 交付能力被忽略。
        if (Has("pptx", "powerpoint", "ppt", "幻灯片", "演示文稿")) { prefix = "pptx_"; label = "演示文稿"; }
        else if (Has("xlsx", "excel", "电子表格", "工作簿")) { prefix = "xlsx_"; label = "Excel 表格"; }
        else if (Has("docx", "word", ".doc", "文稿")) { prefix = "docx_"; label = "Word 文档"; }
        else if (Has("pdf")) { prefix = "pdf_"; label = "PDF 文档"; }
        // 中文泛称兜底：含糊表述（如“写份文档，内含表格”）按旧口径优先当 Word，避免判定回退
        else if (Has("文档")) { prefix = "docx_"; label = "Word 文档"; }
        else if (Has("表格")) { prefix = "xlsx_"; label = "Excel 表格"; }
        if (prefix is null) return null; // 需求本身不要文件，不检查

        // 全方案检索：岗位挂了匹配技能，或 skills 里定义了匹配技能（供岗位引用）
        var hit = plan.Agents.Any(a => (a.SkillIds ?? []).Any(id => id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                  || plan.Skills.Any(s => (s.SkillId ?? "").StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        if (hit) return null;

        // 点名该格式的<b>内置</b>技能，让模型/用户有可直接引用的对象
        var builtin = prefix switch
        {
            "pptx_" => "pptx_deck",
            "xlsx_" => "xlsx_book",
            "docx_" => "docx_report",
            "pdf_" => "pdf_doc",
            _ => null,
        };
        var hint = builtin is null ? $"（如内置 {prefix}* 技能）" : $"（可直接引用内置 {builtin}）";
        return $"用户需要交付{label}，但本方案没有任何岗位具备产出 {label} 的能力（无 {prefix}* 技能）——"
             + $"照此落库后用户将拿不到文件。请重新编排，或先到「技能库」确认已有可用的 {prefix}* 技能{hint}。";
    }

    /// <summary>
    /// 检查方案里是否含有<b>空壳交付技能</b>：名字像能产出文件，实现却写不出真内容。
    ///
    /// <para>
    /// 实测踩到：模型为满足“要 Word”自造了 <c>docx_build_dotnet</c>，正文只 <c>new Text("交付稿")</c> 写死一行字，
    /// 且不返回 produce_file 标记；另有 <c>docx_pack_shell</c> 只有 97 字符、内容就是 <c>ls</c> 列目录。
    /// 它们既产不出内容、产物也无法交付，模型只能反复重试，最终触发交互轮数保护而终止。
    /// </para>
    ///
    /// 判据（宁可漏报不可误报，只抓特征明显的）：
    /// 名字命中交付前缀，且（正文过短 或 正文里出现明显的占位 / 空壳痕迹）。
    /// </summary>
    public static string? DetectHollowDeliverySkill(OrchestrationPlan plan)
    {
        string[] prefixes = ["docx_", "xlsx_", "pptx_", "pdf_"];
        var hollow = new List<string>();

        foreach (var s in plan.Skills)
        {
            var id = s.SkillId ?? "";
            if (!prefixes.Any(p => id.StartsWith(p, StringComparison.OrdinalIgnoreCase))) continue;

            var body = s.Body ?? "";
            var kind = (s.Kind ?? "").ToLowerInvariant();
            // prompt 类型的“文档生成”技能本质产不出文件
            if (kind == "prompt") { hollow.Add(id); continue; }
            // 正文过短：不可能真写出一个有内容的文档
            if (body.Trim().Length < 200) { hollow.Add(id); continue; }
            // 明显空壳痕迹
            if (body.Contains("交付稿\")", StringComparison.Ordinal) && !body.Contains("sections", StringComparison.OrdinalIgnoreCase))
            { hollow.Add(id); continue; }
        }

        if (hollow.Count == 0) return null;
        return "本方案含有疑似空洞的交付技能：" + string.Join("、", hollow)
             + " —— 它们看起来能产出文件，实际写不出真内容（正文过短 / 是 prompt 类型 / 只写占位文字）。"
             + "建议改为直接引用内置 docx_report（Word）、pptx_deck（PPT）、xlsx_book（Excel）、pdf_doc（PDF）等成熟技能，否则用户拿不到可用的交付物。";
    }

    /// <summary>
    /// 检查<b>交付岗是否被写成了“流程门卫”</b>：把内部流程（等定稿、等合规、等审批）当成对用户的准入条件。
    ///
    /// <para>
    /// 实测踩到：编排出的“Word 交付专员”人设写成
    /// “仅接收主笔签发的定稿版本生成 .docx，不接受未过合规的稿件”——
    /// 于是用户直接要文件时，它不出文件，反问用户要定稿与合规结论，用户啥也拿不到。
    /// 根因是编排器把“岗位规矩”写成了“交付前置条件”，本方法做确定性检查并给出修正建议。
    /// </para>
    ///
    /// 判定口径（只限挂了交付前缀技能的岗位，宁可漏报不可误报）：
    /// instructions / description 里出现“仅接收…才 / 不接受… / 须先…后才 / 未过…不出”这类阻断式表述。
    /// </summary>
    /// <returns>命中的问题描述；无问题返回 null。</returns>
    public static string? DetectDeliveryGatekeeper(OrchestrationPlan plan)
    {
        string[] prefixes = ["docx_", "xlsx_", "pptx_", "pdf_"];
        // 阻断式表述：把“前置条件”写成了“不出文件的理由”
        string[] blockers =
        [
            "仅接收", "不接受未", "未过合规", "未过审", "须先经", "需先经", "必须先经",
            "定稿后才", "定稿后再", "审核通过后", "审批通过后", "确认后再", "通过后才",
        ];
        var hits = new List<string>();
        foreach (var a in plan.Agents)
        {
            if (!(a.SkillIds ?? []).Any(id => prefixes.Any(p => id.StartsWith(p, StringComparison.OrdinalIgnoreCase))))
                continue; // 只检查真正具备交付能力的岗位
            var text = (a.Instructions ?? "") + "\n" + (a.Description ?? "");
            if (string.IsNullOrWhiteSpace(text)) continue;
            var found = blockers.FirstOrDefault(b => text.Contains(b, StringComparison.Ordinal));
            if (found is not null) hits.Add($"{a.AgentId}（命中“{found}”）");
        }

        if (hits.Count == 0) return null;
        return "交付岗被写成了“流程门卫”：" + string.Join("、", hits)
             + " —— 把内部流程（等定稿 / 等合规 / 等审批）当成了对用户的交付前提，"
             + "照此落库后用户直接要文件时会被反问要定稿，拿不到东西。"
             + "请改成：拿到可用材料就先出文件，材料不完整时在文内列入“待确认项”，不以等待定稿为由不出文件。";
    }

    /// <summary>组织方案自检项。<see cref="Level"/>：error=落库前必须修，warn=建议修，info=通过 / 摘要。</summary>
    public sealed record OrgPlanCheck(string Code, string Level, string Message);

    /// <summary>
    /// 落库前的<b>自检清单</b>：把“结构是否成立、连接是否成对、是否真能交付”这些确定性判断汇总成一组可展示的条目。
    ///
    /// <para>
    /// 为什么单独做（而不是只靠提示词 / 只给一条 deliveryWarning）：提示词只能“尽量引导”，模型仍会波动。
    /// 实测两类退化最常见：① 只给“问题提升”链、没有“任务指派”，组织退化成单向上报，主管没法往下派活；
    /// ② 需求要文件却没人能产出，或交付岗被写成“等定稿 / 等审批才出文件”的门卫。
    /// 这里把这些问题<b>逐条列清</b>（而不是只显示第一条），让用户在“确认落库”之前就能看到并修，
    /// 而不是等跑完一轮、拿不到东西才发现。
    /// </para>
    /// </summary>
    /// <returns>自检条目（顺序：结构 / 连接 → 技能 → 交付闭环 → 通过摘要）；全部通过时含一条 <c>ok</c> 摘要。</returns>
    public static IReadOnlyList<OrgPlanCheck> SelfCheck(
        OrchestrationPlan plan, string requirement,
        IReadOnlyDictionary<string, SkillQualityProfile>? quality = null,
        SkillLessonStore? lessons = null,
        IReadOnlyDictionary<string, SkillLoopHealth>? loopHealth = null)
    {
        var checks = new List<OrgPlanCheck>();
        var agents = plan.Agents ?? [];
        if (agents.Count == 0)
        {
            checks.Add(new OrgPlanCheck("no_agents", "error", "方案里没有任何数字员工，无法落库。"));
            return checks;
        }
        var ids = agents.Where(a => !string.IsNullOrWhiteSpace(a.AgentId))
                        .Select(a => a.AgentId!.Trim()).ToHashSet(StringComparer.Ordinal);

        // —— 结构：连接（向下任务指派 / 向上问题提升 / 汇报中继）——
        var assignEdges = 0; var escEdges = 0; var relayEdges = 0;
        var dangling = new List<string>();
        foreach (var a in agents)
        {
            var self = (a.AgentId ?? "").Trim();
            if (self.Length == 0) continue;
            foreach (var d in (a.AssignmentIds ?? []).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()))
            {
                assignEdges++;
                if (!ids.Contains(d)) dangling.Add($"{self}→{d}");
            }
            var up = (a.EscalationAgentId ?? "").Trim();
            if (up.Length > 0)
            {
                escEdges++;
                if (string.Equals(up, self, StringComparison.Ordinal))
                    checks.Add(new OrgPlanCheck("self_escalation", "warn", $"岗位「{self}」把「问题提升」指向了自己（自环），应改指向其直接上级。"));
                else if (!ids.Contains(up)) dangling.Add($"{self}↑{up}");
            }
            var relay = (a.RelayToAgentId ?? "").Trim();
            if (relay.Length > 0) { relayEdges++; if (!ids.Contains(relay)) dangling.Add($"{self}⇄{relay}"); }
        }

        if (dangling.Count > 0)
            checks.Add(new OrgPlanCheck("dangling_link", "error",
                "有连接指向了方案里不存在的岗位（落库会被直接拒绝）：" + string.Join("、", dangling)
                + "。请把这些连接改到真实存在的 agentId，或去掉。"));

        if (assignEdges == 0 && escEdges == 0)
            checks.Add(new OrgPlanCheck("no_connections", "error",
                "这套组织没有任何岗位连接：既没有「向下任务指派」、也没有「向上问题提升」，落库后会各自为战、不成为组织。"
                + "请在主管岗的 assignmentIds 里列出其直接下级（任务指派），并让下级用 escalationAgentId 指回直接上级（问题提升）。"));
        else if (assignEdges == 0)
            checks.Add(new OrgPlanCheck("no_assignment", "error",
                "这套组织只有「向上问题提升」、没有「向下任务指派」——下级只会往上抛问题，上级却没法把任务派下去。"
                + "请在有下级的岗位（主管/组长/经理…）的 assignmentIds 里补上其直接下级的 agentId。"));
        else if (escEdges == 0)
            checks.Add(new OrgPlanCheck("no_escalation", "warn",
                "这套组织只有「向下任务指派」、没有「向上问题提升」——一线遇到自己不能决断的问题时没有上报出口。"
                + "建议让非顶层岗位用 escalationAgentId 指向其直接上级。"));
        else
            checks.Add(new OrgPlanCheck("connections_ok", "info",
                $"连接成对：任务指派 {assignEdges} 条、问题提升 {escEdges} 条"
                + (relayEdges > 0 ? $"、汇报中继 {relayEdges} 条" : "") + "。"));

        // —— 技能：是否人人有活可干 ——
        var noSkill = agents.Where(a => !string.IsNullOrWhiteSpace(a.AgentId) && (a.SkillIds ?? []).Count == 0)
                            .Select(a => a.AgentId!.Trim()).ToList();
        if (noSkill.Count > 0)
            checks.Add(new OrgPlanCheck("agent_without_skill", "warn",
                "以下岗位没有挂载任何技能（可能不知道该怎么干活）：" + string.Join("、", noSkill) + "。建议至少各挂一个与职责相符的技能。"));

        // —— 技能质量：方案引用的技能近期是否持续交残次品（1.0.348）——
        if (quality is { Count: > 0 })
        {
            var risky = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var a in agents)
                foreach (var sid in (a.SkillIds ?? []).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()))
                {
                    if (!quality.TryGetValue(sid, out var q) || !q.IsRisky) continue;
                    if (seen.Add(sid)) risky.Add($"「{sid}」（{q.Describe()}）");
                }
            if (risky.Count > 0)
                checks.Add(new OrgPlanCheck("skill_quality_risk", "warn",
                    "以下技能近期产物质量偏低，挂到岗位上可能持续交残次品：" + string.Join("；", risky)
                    + "。建议先在技能库点 🩺 诊断修复，或改用更可靠的技能后再落库。"));
        }

        // —— 技能经验：方案挂载的技能是否有未解决的历史问题（1.0.350）——
        if (lessons is not null)
        {
            var hits = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var a in agents)
                foreach (var sid in (a.SkillIds ?? []).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()))
                {
                    if (!seen.Add(sid)) continue;
                    var failures = lessons.FailuresForSkill(sid, 2);
                    if (failures.Count == 0) continue;
                    hits.Add($"「{sid}」" + string.Join("；", failures.Select(l => l.ToPromptLine())));
                }
            if (hits.Count > 0)
                checks.Add(new OrgPlanCheck("skill_experience_risk", "warn",
                    "以下技能有未解决的历史问题（来自真实失败）：" + string.Join("；", hits)
                    + "。建议在对应岗位的 instructions 里写明规避步骤，或先在技能库点 🩺 修复。"));
        }

        // —— 修复健康：方案挂载的技能是不是“修也修不好”（1.0.361）——
        if (loopHealth is { Count: > 0 })
        {
            var avoid = new List<string>();
            var watch = new List<string>();
            var seenHealth = new HashSet<string>(StringComparer.Ordinal);
            foreach (var a in agents)
                foreach (var sid in (a.SkillIds ?? []).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()))
                {
                    if (!seenHealth.Add(sid)) continue;
                    if (!loopHealth.TryGetValue(sid, out var h) || !h.IsRisky) continue;
                    (h.IsAvoid ? avoid : watch).Add($"「{sid}」（{h.Describe()}）");
                }
            if (avoid.Count > 0)
                checks.Add(new OrgPlanCheck("skill_loop_risk", "warn",
                    "以下技能最近反复修复后仍失败或回滚，挂到新岗位上很可能反复卡住：" + string.Join(";", avoid)
                    + "。建议改用同类更稳定的技能，或先在技能库看「💡 经验 / 🧪 回归用例」并手工调整。"));
            if (watch.Count > 0)
                checks.Add(new OrgPlanCheck("skill_loop_risk", "warn",
                    "以下技能最近的修复不够稳：" + string.Join(";", watch)
                    + "。建议在对应岗位的 instructions 里写明兵底做法（先跑回归确认可用）。"));
        }

        // —— 交付闭环：需求要的交付物是否真有人能产出 ——
        var delivery = DetectDeliveryGap(plan, requirement);
        if (delivery is not null) checks.Add(new OrgPlanCheck("delivery_gap", "error", delivery));
        var hollow = DetectHollowDeliverySkill(plan);
        if (hollow is not null) checks.Add(new OrgPlanCheck("hollow_delivery", "error", hollow));
        var gatekeeper = DetectDeliveryGatekeeper(plan);
        if (gatekeeper is not null) checks.Add(new OrgPlanCheck("delivery_gatekeeper", "error", gatekeeper));

        if (!checks.Any(c => c.Level is "error" or "warn"))
            checks.Add(new OrgPlanCheck("ok", "info",
                $"自检通过：岗位 {agents.Count} 个、技能 {plan.Skills.Count} 个，连接与交付闭环均无问题。"));
        return checks;
    }

    /// <summary>测试钩子：暴露提示词构造，便于断言“可复用技能列表确实进了提示词”。勿在生产路径调用。</summary>
    internal static string BuildPromptForTest(string requirement, IReadOnlyList<ReusableSkill>? reusableSkills, bool allowDotnet = true)
        => BuildPrompt(requirement, reusableSkills, allowDotnet);

    /// <summary>围栏字符（避免在源码里字面书写三重反引号）。</summary>
    private static string Fence() => new string((char)96, 3);

    /// <summary>
    /// 可复用技能清单段落：列出技能库中现成技能，要求模型<b>优先引用</b>（直接写进 skillIds，
    /// 不需要在 skills 里重复定义），只有库里确实没有合适能力时才自建。
    /// </summary>
    private static string BuildReusableSkillsSection(IReadOnlyList<ReusableSkill>? reusableSkills)
    {
        if (reusableSkills is null || reusableSkills.Count == 0) return "";

        var sb = new StringBuilder();
        sb.Append("【技能库中已有的可复用技能】（优先复用；直接把这些 skillId 写进岗位的 skillIds，<b>不要</b>在 skills 里重复定义）：\n");
        foreach (var s in reusableSkills.Take(40))
        {
            var desc = s.Description.Length > 120 ? s.Description.Substring(0, 120) + "…" : s.Description;
            sb.Append("- ").Append(s.SkillId).Append("（").Append(s.Name).Append("，kind=").Append(s.Kind).Append("）：").Append(desc).Append('\n');
            if (!string.IsNullOrWhiteSpace(s.QualityNote))
                sb.Append("    · 近期质量：").Append(s.QualityNote).Append('\n');
            if (!string.IsNullOrWhiteSpace(s.ExperienceNote))
                sb.Append("    · 已知坑：").Append(s.ExperienceNote).Append('\n');
            if (!string.IsNullOrWhiteSpace(s.HealthNote))
                sb.Append("    · 修复健康：").Append(s.HealthNote).Append('\n');
        }
        sb.Append("\n复用规则（很重要）：\n");
        sb.Append("- 若某岗位的职责能被上面的技能覆盖（如“生成 Word 文档/公文/报告”对应 docx_*、“做 PPT/演示文稿”对应 pptx_deck、" +
                  "“做 Excel/表格/报表”对应 xlsx_book、“出 PDF 报告”对应 pdf_doc），**必须直接引用它**，不要另建 prompt 技能。\n");
        sb.Append("- 引用写在 skillIds 里；skills 数组里<b>只放</b>库中没有、需要新造的技能。\n");
        sb.Append("- 若 skills 里新造的技能与上面某个已有技能同 id，视为重复，应改为直接引用。\n");
        sb.Append("- 标注「质量偏低」的技能慎用：优先选择合格率高、均分高的技能；若某个职责只有低质量技能能覆盖，" +
                  "宁可自建一个更可靠的技能（写清楚完整执行步骤与产物要求），也不要把持续交残次品的技能排进岗位。\n");
        sb.Append("- 标注「已知坑」的技能：在对应岗位的 instructions 里写明规避步骤（如“调用前先备齐正文 / 页数 / 数据，" +
                  "产出后检查交付文件是否齐全”），不要忽略历史失败经验。\n");
        sb.Append("- 标注「修复健康」的技能：这说明它最近反复修也修不稳（回滚 / 自测失败）。**优先选同类更稳的技能**；" +
                  "若职责只能用它，就在岗位 instructions 里写明兵底做法（先跑 🧪 回归确认可用，不通过就改用其他方式产出）。\n\n");
        return sb.ToString();
    }

    /// <summary>解析模型生成的 JSON 文本为结构化方案（供流式端点收尾使用）。失败抛明确异常。</summary>
    public static OrchestrationPlan Parse(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start) throw new InvalidOperationException("组织编排结果不是有效 JSON");
        OrchestrationPlan plan;
        try
        {
            plan = JsonSerializer.Deserialize<OrchestrationPlan>(text.Substring(start, end - start + 1),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new InvalidOperationException("组织编排结果为空");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("组织编排结果 JSON 解析失败：" + ex.Message, ex);
        }
        plan.Agents ??= [];
        plan.Skills ??= [];
        if (plan.Agents.Count == 0 || plan.Agents.All(a => string.IsNullOrWhiteSpace(a.AgentId)))
            throw new InvalidOperationException("组织编排结果缺少数字员工");
        // 记忆拟人 preset 归一：模型没给 / 给成未知值时，按岗位职责关键词启发式兜底（可读、不中断整支落库）；
        // 仍给不出 → null = 沿用全局召回（与旧行为一致，绝不因该字段失败）。
        foreach (var agent in plan.Agents)
            agent.MemoryProfile ??= SuggestMemoryProfile(agent);
        // 连接归一（双向）：既由“提升目标”反推“直接下级”补 assignmentIds（任务指派），
        // 也由“指派目标”反推“直接上级”补 escalationAgentId（问题提升），
        // 避免模型只给单向链（只上抛 / 只下派）把组织生成成半残结构。
        NormalizeConnections(plan);
        return plan;
    }

    /// <summary>连接归一：把“任务指派”（向下的 assignmentIds）与“问题提升”（向上的 escalationAgentId）两条链补成全对。
    /// 只增不改：已有显式连接保留原样，只为空白处补全；叶子 / 顶层不受影响。</summary>
    private static void NormalizeConnections(OrchestrationPlan plan)
    {
        var agents = plan.Agents ?? [];
        var ids = agents.Where(a => !string.IsNullOrWhiteSpace(a.AgentId))
                        .Select(a => a.AgentId!.Trim()).ToHashSet(StringComparer.Ordinal);
        // 自 → 直接上级（仅用于防自环 / 二环的判断）
        var upOf = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var a in agents)
        {
            var self = (a.AgentId ?? "").Trim();
            var up = (a.EscalationAgentId ?? "").Trim();
            if (self.Length == 0 || up.Length == 0 || up == self || !ids.Contains(up)) continue;
            upOf[self] = up;
        }

        // 方向一：由“提升目标”反推其直接下级，并入上级的 assignmentIds（任务指派）。
        var subsOf = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (child, parent) in upOf)
        {
            if (!subsOf.TryGetValue(parent, out var l)) subsOf[parent] = l = [];
            l.Add(child);
        }
        foreach (var a in agents)
        {
            var self = (a.AgentId ?? "").Trim();
            if (self.Length == 0 || !subsOf.TryGetValue(self, out var subs)) continue;
            var existing = (a.AssignmentIds ?? []).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).ToList();
            var seen = new HashSet<string>(existing, StringComparer.Ordinal);
            var merged = new List<string>(existing.Count + subs.Count);
            merged.AddRange(existing);
            var changed = false;
            foreach (var sub in subs)
                // 跳过“上级反过来又提升到该下级”的二环，避免把互指写成互相指派
                if (!string.Equals(upOf.GetValueOrDefault(self) ?? "", sub, StringComparison.Ordinal) && seen.Add(sub))
                { merged.Add(sub); changed = true; }
            if (changed) a.AssignmentIds = merged;
        }

        // 方向二：由“指派目标”反推其直接上级，为尚无可提升目标的岗位补上 escalationAgentId（问题提升）。
        // 仅在唯一上级时才反推（多上级留待人工指定），并跳过会与本岗位现有提升目标互指的情况。
        var bossesOf = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var a in agents)
        {
            var self = (a.AgentId ?? "").Trim();
            if (self.Length == 0) continue;
            foreach (var d in (a.AssignmentIds ?? []).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct(StringComparer.Ordinal))
            {
                if (d == self || !ids.Contains(d)) continue;
                if (!bossesOf.TryGetValue(d, out var l)) bossesOf[d] = l = [];
                l.Add(self);
            }
        }
        foreach (var a in agents)
        {
            var self = (a.AgentId ?? "").Trim();
            if (self.Length == 0 || !string.IsNullOrWhiteSpace(a.EscalationAgentId)) continue;
            if (!bossesOf.TryGetValue(self, out var bosses) || bosses.Count != 1) continue;
            var boss = bosses[0];
            // 二环保护：若该“上级”本就提升到本岗位，则不再互相补全
            if (string.Equals(upOf.GetValueOrDefault(boss) ?? "", self, StringComparison.Ordinal)) continue;
            a.EscalationAgentId = boss;
        }
    }

    /// <summary>按岗位称呼 / 职责文本启发式推断合适记忆拟人 preset（模型漏填时的可读兜底）。无把握返回 null。</summary>
    private static MemoryProfile? SuggestMemoryProfile(OrchestratedAgent agent)
    {
        var hay = string.Concat(agent.Nickname, " ", agent.Description, " ", agent.Instructions);
        foreach (var (keywords, preset) in MemoryRoleHeuristics)
            if (keywords.Any(k => hay.Contains(k, StringComparison.OrdinalIgnoreCase)))
                return MemoryPersonalityTypes.FromKey(preset);
        return null;
    }

    /// <summary>岗位职责 → 记忆拟人 preset 的启发式规则（越靠前优先；含中文与常见英文岗位词）。</summary>
    private static readonly (string[] Keywords, string Preset)[] MemoryRoleHeuristics =
    [
        (["主管", "经理", "组长", "负责人", "总监", "队长", "室长", "科长", "manager", "supervisor", "leader", "director", "head of"], MemoryPersonalityTypes.Deep),
        (["客服", "接待", "话务", "热线", "一线", "专员", "接线", "前台", "support", "reception", "hotline", "agent"], MemoryPersonalityTypes.Broad),
        (["售后", "客户成功", "客户关系", "顾问", "维系", "销售", "商务", "account", "after-sales", "customer success", "consultant"], MemoryPersonalityTypes.CueDependent),
        (["值班", "轮班", "临时", "速查", "oncall", "shift", "quick"], MemoryPersonalityTypes.FastForgetting),
        (["培训", "带教", "教练", "教学", "训练", "新人", "coach", "trainer", "mentor"], MemoryPersonalityTypes.SlowToLearn),
    ];

    private static string Truncate(string s) => s.Length <= 60 ? s : s[..60] + "…";

    /// <summary>mock 模式确定性模板：围绕需求生成一个示例组织（主管 + 两个执行岗 + 各自技能 + 连接）。</summary>
    private static OrchestrationPlan BuildTemplate(string requirement)
    {
        // mock 用带随机序号的纯 ASCII id（避免中文/符号转成重复下划线，也避免多次生成/重复 apply 在同一实例撞 ID）
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var manager = "mgr_" + suffix;
        var exec1 = "exec_" + suffix + "_a";
        var exec2 = "exec_" + suffix + "_b";
        var skill1 = "skill_" + suffix + "_a";
        var skill2 = "skill_" + suffix + "_b";
        var brief = requirement.Length > 30 ? requirement[..30] + "…" : requirement;

        // 确定性演示：主管=深记（长期记牢关键决策与分工）、执行岗A=广记（执行往来量大、记得多）、执行岗B=存得住想不起（凭提示回想具体事务）。
        return new OrchestrationPlan
        {
            Title = $"「{brief}」组织",
            Agents =
            [
                new OrchestratedAgent { AgentId = manager, Nickname = "主管", Description = $"统筹「{brief}」", Instructions = $"你是一位数字员工主管，负责统筹「{brief}」相关工作，评判断语境并指派给合适的下一层执行岗。", TriggerMode = "mentioned", SkillIds = [skill1, skill2], AssignmentIds = [exec1, exec2], EscalationAgentId = null, RelayToAgentId = null, MemoryProfile = MemoryPersonalityTypes.FromKey(MemoryPersonalityTypes.Deep) },
                new OrchestratedAgent { AgentId = exec1, Nickname = "执行岗A", Description = $"负责「{brief}」的部分执行", Instructions = $"你是执行岗A，负责完成「{brief}」相关工作，遇到不属于自己的任务应说明并上抛。", TriggerMode = "mentioned", SkillIds = [skill1], AssignmentIds = [], EscalationAgentId = manager, RelayToAgentId = null, MemoryProfile = MemoryPersonalityTypes.FromKey(MemoryPersonalityTypes.Broad) },
                new OrchestratedAgent { AgentId = exec2, Nickname = "执行岗B", Description = $"负责「{brief}」的部分执行", Instructions = $"你是执行岗B，负责完成「{brief}」相关工作，遇到不属于自己的任务应说明并上抛。", TriggerMode = "mentioned", SkillIds = [skill2], AssignmentIds = [], EscalationAgentId = manager, RelayToAgentId = null, MemoryProfile = MemoryPersonalityTypes.FromKey(MemoryPersonalityTypes.CueDependent) },
            ],
            Skills =
            [
                new OrchestratedSkill { SkillId = skill1, Name = "A 类事务处理", Kind = "prompt", Description = $"处理与「{brief}」相关的 A 类事务。", Body = $"请针对与「{brief}」相关的 A 类事务，给出专业、可落地的处理建议。", ExecutionLocation = "server", RequiresApproval = false },
                new OrchestratedSkill { SkillId = skill2, Name = "本机信息速查", Kind = "shell", Description = $"获取本机基本信息与资源占用的运维速查（演示 skill 示例：读系统信息）。", Body = "$d=[Environment]::GetFolderPath('Desktop'); if(Test-Path $d){ Write-Output $d } else { Write-Output 'no-desk' }", ExecutionLocation = "client", RequiresApproval = true },
            ],
        };
    }
}

/// <summary>一份完整的组织编排方案（岗位 + 技能 + 连接）。由 <see cref="AgentOrchestrator"/> 生成，前端预览确认后落库。</summary>
public sealed class OrchestrationPlan
{
    public string? Title { get; set; }
    public List<OrchestratedAgent> Agents { get; set; } = [];
    public List<OrchestratedSkill> Skills { get; set; } = [];
}

/// <summary>一个数字员工岗位及其连接关系。</summary>
public sealed class OrchestratedAgent
{
    public string? AgentId { get; set; }
    public string? Nickname { get; set; }
    public string? Description { get; set; }
    public string? Instructions { get; set; }
    public string? TriggerMode { get; set; }
    public List<string> SkillIds { get; set; } = [];
    public List<string>? AssignmentIds { get; set; }
    public string? EscalationAgentId { get; set; }
    public string? RelayToAgentId { get; set; }

    /// <summary>该岗位的记忆拟人 preset。模型 JSON 里写 key 字符串或 {memoryType,…} 对象均兼容（见 <see cref="OrchestratedMemoryProfileConverter"/>）；
    /// null = 该岗位不单独配置、召回沿用全局（向后兼容）。序列化时始终写成 preset key 字符串，保证 org_commit 回读契约简单。</summary>
    [System.Text.Json.Serialization.JsonConverter(typeof(OrchestratedMemoryProfileConverter))]
    public MemoryProfile? MemoryProfile { get; set; }
}

/// <summary>
/// 编排岗位 memoryProfile 的宽容解析 / 紧凑写出：
/// 读——接受 null（不配置）、字符串（preset key）或对象（{"memoryType":"deep",…}，兼容模型改写/手工预览），
///      只把已知 preset key 转成 <see cref="MemoryProfile"/>；未知 / 空一律返回 null（防御，不让编排解析失败）。
/// 写——始终输出 preset key 字符串（不是完整对象），让 org_plan_draft 初稿 JSON 与 org_commit 解析的字段都最简。
/// </summary>
public sealed class OrchestratedMemoryProfileConverter : System.Text.Json.Serialization.JsonConverter<MemoryProfile?>
{
    public override MemoryProfile? Read(ref System.Text.Json.Utf8JsonReader reader, System.Type typeToConvert, System.Text.Json.JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case System.Text.Json.JsonTokenType.Null:
                return null;
            case System.Text.Json.JsonTokenType.String:
                return MemoryPersonalityTypes.FromKey(reader.GetString());
            case System.Text.Json.JsonTokenType.StartObject:
            {
                string? key = null;
                using var doc = System.Text.Json.JsonDocument.ParseValue(ref reader);
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    var name = prop.Name.ToLowerInvariant();
                    if (prop.Value.ValueKind == System.Text.Json.JsonValueKind.String && (name is "memorytype" or "type" or "key"))
                    {
                        key = prop.Value.GetString();
                        break;
                    }
                }
                return MemoryPersonalityTypes.FromKey(key); // 对象里没有可用 memoryType 也按未配置处理
            }
            default:
                return null; // 其它形态（数组 / 数值…）防御性忽略
        }
    }

    public override void Write(System.Text.Json.Utf8JsonWriter writer, MemoryProfile? value, System.Text.Json.JsonSerializerOptions options)
    {
        if (value is null) { writer.WriteNullValue(); return; }
        writer.WriteStringValue(value.MemoryType);
    }
}

/// <summary>一个可复用技能定义（由编排方案批量生成，供落地时写入技能库）。</summary>
public sealed class OrchestratedSkill
{
    public string? SkillId { get; set; }
    public string? Name { get; set; }
    public string? Description { get; set; }
    public string? Kind { get; set; }

    /// <summary>技能体：模型有时把 prompt 写为字符串、把 http/shell 写为对象/数组。
    /// 这里用 <see cref="FlexibleBodyConverter"/> 兼容成字符串（对象/数组/数值则序列化为紧凑 JSON 文本）。</summary>
    [System.Text.Json.Serialization.JsonConverter(typeof(FlexibleBodyConverter))]
    public string? Body { get; set; }
    public string? ExecutionLocation { get; set; }
    public bool RequiresApproval { get; set; }
}

/// <summary>把技能 body 的 JSON 值宽松地读成字符串：字符串原样；对象 / 数组 / 数值 / 布尔序列化为紧凑 JSON 文本。
/// 真实模型对 http/shell 技能常把 body 写成 JSON 对象，兼容后不报错（后续按字符串写入技能库）。</summary>
public sealed class FlexibleBodyConverter : System.Text.Json.Serialization.JsonConverter<string?>
{
    public override string? Read(ref System.Text.Json.Utf8JsonReader reader, System.Type typeToConvert, System.Text.Json.JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case System.Text.Json.JsonTokenType.String:
            case System.Text.Json.JsonTokenType.Null:
                return reader.GetString();
            case System.Text.Json.JsonTokenType.StartObject:
            case System.Text.Json.JsonTokenType.StartArray:
            {
                using var doc = System.Text.Json.JsonDocument.ParseValue(ref reader);
                return doc.RootElement.GetRawText();
            }
            default:
                // 数值：转字符串（宽松容错，不追求精度）；布尔原样
                if (reader.TryGetDouble(out var d)) return d.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (reader.TokenType == System.Text.Json.JsonTokenType.True) return "true";
                if (reader.TokenType == System.Text.Json.JsonTokenType.False) return "false";
                throw new System.Text.Json.JsonException("不支持的 body JSON 类型：" + reader.TokenType);
        }
    }

    public override void Write(System.Text.Json.Utf8JsonWriter writer, string? value, System.Text.Json.JsonSerializerOptions options)
        => writer.WriteStringValue(value);
}
