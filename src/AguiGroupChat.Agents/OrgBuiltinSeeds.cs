using AguiGroupChat.Hub.Models;

namespace AguiGroupChat.Agents;

/// <summary>
/// 内置组织构建师规范内容（以 david 2026-09-05 导出为权威快照固化）。
/// 一键清除（/ag-ui/reset）会清空全部运行态数字员工与技能库；本省是<b>内置默认</b>，可用于
/// “管理员一键恢复内置组织工具”时按原样重建：org_architect + org_design + org_deploy（owner=当前管理员，不建群/客服知聚）。
/// </summary>
public static class OrgBuiltinSeeds
{
    public const string OrgArchitectAgentId = "org_architect";
    public const string OrgDesignSkillId = "org_design";
    public const string OrgDeploySkillId = "org_deploy";

    public static readonly string OrgArchitectNickname = "组织架构构建师";
    public static readonly string OrgArchitectDescription = "内置组织构建师：群里@它即可经技能把组织直接建到/整支更新到库（仅系统管理员放行）。";

    /// <summary>org_architect 指令（含“整支构建优先走一键式 org_plan_draft”的最新口径）。</summary>
    public static readonly string OrgArchitectInstructions =
"""
你是内置「组织架构构建师」：用户在群里@你，就能把一支组织直接经技能建出来/整支更新到库（不靠人把 JSON 粘贴到其它页面）。

【工作流】
1) @你处收到某支需求或改动：调用 org_design 得到含完整 agents 的最终稿 JSON（含稳定 teamKey）。
2) 若系统管理员（群主/超管）在会话里说了"落库 / 建好 / 就按这版落库 / 更新"，你**立即**调用 org_commit（参数：teamKey = 最终稿里的 teamKey，planJson = 上面那段完整 JSON），不要再让用户补第二次确认，也不要只回文字。
3) org_commit 返回后按结果收尾：成功→列出建出/覆盖的数字员工 id 与技能，引导加到群里用；失败→按其报错修正 JSON（补够 agents、修引用、闭合括号）后**用同一 teamKey 重试**，直到成功或到达仍失败的明确原因，不再踢回给用户重复复述。

【边界】
- 只有系统管理员明确让落库时才调用 org_commit；非管理员发“落库”则交最终稿并说明由管理员在会话里放行，绝不写库。
- org_design 产出后你先把它给用户核对行为；待用户或管理员说“就按这版落库”再调用 org_commit。
中文、简洁、可执行。
【整支构建优先走“一键式”】
- 当用户要的是一支全新的组织／团队、或让你“设计／构建／打造一个完整组织架构”并给出（不仅个别岗位的）完整能力时，**优先调用 org_plan_draft（参数=用户那句话的构建需求）**，用它产出与网页“一键组织编排”同级别的结构化初稿 JSON：岗位 + 各岗 skillIds + 每个技能(kind 会按 shell/http/prompt/dotnet 智能选、executionLocation 按 server/client)+ 岗位连接。这能避免你把整支组织手写成全是 pure prompt 的软稿。
- 取到 org_plan_draft 返回后：用可读概览呈现给用户，逐项等用户/系统管理员认可（如“就按这版落库”）；认可后再用 org_commit 以同一段成稿 JSON、同一稳定 teamKey 落库。注意 org_plan_draft 只产稿不写库，别在用户确认前落库。
- 若用户是对已有一支组织做局部小改（只动一两个岗位/技能），仍可按需用 org_design 逐条精致，不必每次整支重拟。
""";

    /// <summary>org_design（prompt）技能正文 —— 组织架构设计引擎模板。</summary>
    public static readonly string OrgDesignBody =
"""
你是组织架构设计引擎。把用户需求/修改意见整理成一份**可直接传 org_commit 的最终稿 JSON**，要求：
1) 只输出一段可复制的 JSON（不要其它口水话），结构：{ "teamKey":"it_support", "title":"组织名", "skills":[{"skillId":"...","name":"...","description":"...","kind":"prompt|http|shell|dotnet","body":"...","executionLocation":"server|client","requiresApproval":...,"parametersJson":""}], "agents":[{"agentId":"...","nickname":"一线...","description":"...","instructions":"...","triggerMode":"mentioned","skillIds":[...],"assignmentIds":[],"escalationAgentId":"..."或null,"relayToAgentId":null}], "createSupportCircle":false }。
2) 为每个岗位按【职责+本平台可用运行能力】选 kind 与 executionLocation，不要一律 prompt：
   - 仅分析/建议/流程/结构化起草 → kind=prompt、executionLocation=server（最稳妥）。
   - 需要查询外部 HTTP(S) 接口（只读首选）→ kind=http、executionLocation=server。
   - 需要在服务器执行命令/脚本（系统进程/工作区 data/skillruns 等）→ kind=shell、executionLocation=server（服务端沙箱）。
   - 需要**在被触发用户的那台机器（本机）**执行命令（本机磁盘/进程/桌面应用排查等，shell 在本机跑需触发者批准）→ kind=shell、executionLocation=client。
   - 需要 C# 动态执行 → kind=dotnet：serverside Roslyn kind=dotnet+executionLocation=server，需跑在本机 → executionLocation=client（桌面/桥，浏览器本身不能编译 C#）。
   - 每个岗位仅当其职责确实需要执行能力时才给 shell/http/dotnet；拿不准就 prompt 或只读 http，并在 agents 对应岗位 description 注明“执行级能力待系统管理员评估放行后启用”。
   - org_deploy 是“把整份最终稿落库/覆盖（同一 teamKey 只留最新）”的动作，不要把它当作某个普通岗位的执行技能来设置。
3) 权限边界：prompt 任何人可出稿；http/shell/dotnet（建库）与 org_deploy 均需系统管理员建/改/删，本机(client)执行需触发者批准；普通用户只产出这份待审 JSON、绝不落库。不要在没有任何管理员放行时编造“已写库成功”。
4) teamKey 全程稳定；agents 必须>=1 且每个都有 nickname；改动基于现状增量；请给足并闭合大括号，不截断、不占位省略。
输入需求/现有情况：
{{query}}
""";

    /// <summary>org_design（prompt）技能描述。</summary>
    public static readonly string OrgDesignDescription =
        "组织架构设计引擎：把需求/修改意见整理成可直接提交的最终稿 JSON；按每个岗位职责与本平台可用运行能力(kind/executionLocation)设计技能（prompt/http/shell/dotnet，server/client），仅系统管理员可放行建执行级/受控技能，普通用户只出稿不写库。";

    /// <summary>org_deploy（org_deploy）技能描述 —— 受控组织落库动作；正文为空，不投给执行器。</summary>
    public static readonly string OrgDeployDescription =
        "org_deploy：把整支组织最终稿整支落库 / 覆盖（同一 teamKey 只留最新）的受控动作；仅系统管理员放行后写库，普通用户只拿到需放行的说明。";

    /// <summary>构造内置组织角色 agent 定义（owner 由调用方填）。</summary>
    public static AgentDefinition BuildDefaultOrgArchitectAgent(string ownerId)
        => new()
        {
            AgentId = OrgArchitectAgentId,
            Nickname = OrgArchitectNickname,
            Description = OrgArchitectDescription,
            Instructions = OrgArchitectInstructions,
            TriggerMode = AgentTriggerMode.Mentioned,
            Keywords = [],
            Schedule = null,
            Model = null,
            BridgeEndpoint = null,
            BridgeMode = null,
            BridgeToken = null,
            PersonalMemoryEnabled = true,
            IsPrivate = false,
            OwnerId = ownerId,
            Skills = [],
            KnowledgeBaseIds = [],
            RequireApprovalToolNames = [],
            Pipeline = null,
            RelayToAgentId = null,
            AssignmentIds = [],
            EscalationAgentId = null,
            SkillDefIds = [OrgDesignSkillId, OrgDeploySkillId],
            IsSkillTarget = false,
        };

    /// <summary>构造 org_design（prompt）技能定义。</summary>
    public static AgentSkillDefinition BuildDefaultOrgDesignSkill(string ownerId)
        => new()
        {
            SkillId = OrgDesignSkillId,
            Name = "org_design",
            Description = OrgDesignDescription,
            Kind = AgentSkillKind.Prompt,
            Body = OrgDesignBody,
            ExecutionLocation = AgentSkillExecutionLocation.Server,
            RequiresApproval = false,
            Interpreter = null,
            OwnerId = ownerId,
        };

    /// <summary>构造 org_deploy（org_deploy）受控落库动作技能定义。</summary>
    public static AgentSkillDefinition BuildDefaultOrgDeploySkill(string ownerId)
        => new()
        {
            SkillId = OrgDeploySkillId,
            Name = "org_deploy",
            Description = OrgDeployDescription,
            Kind = AgentSkillKind.Org_deploy,
            Body = "",
            ExecutionLocation = AgentSkillExecutionLocation.Server,
            RequiresApproval = false,
            Interpreter = null,
            OwnerId = ownerId,
        };
}
