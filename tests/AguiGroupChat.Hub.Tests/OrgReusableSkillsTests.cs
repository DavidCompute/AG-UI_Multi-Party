using AguiGroupChat.Agents;
using AguiGroupChat.Agents.Tools;
using AguiGroupChat.Hub.Agents;
using AguiGroupChat.Hub.Messaging;
using AguiGroupChat.Hub.Models;
using AguiGroupChat.Hub.Options;
using AguiGroupChat.Hub.Storage;
using AguiGroupChat.Hub.Users;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>
/// 组织编排复用技能库现成技能：方案可直接引用库内 skillId（不必在 skills 里重复定义），
/// 且「库内优先」——同名时以库内那份为准，不重复落一份。
/// </summary>
public sealed class OrgReusableSkillsTests
{
    private static (AgentCatalog Catalog, AgentSkillCatalog Skills, GroupHub Hub) CreateSut(AgentOptions options)
    {
        var loggerFactory = NullLoggerFactory.Instance;
        var catalog = new AgentCatalog(options, loggerFactory, new ServiceCollection().BuildServiceProvider());
        var skills = new AgentSkillCatalog(loggerFactory, options);
        var hub = new GroupHub(
            new InMemoryGroupStore(200), new InMemoryUserStore(), new ConnectionManager(),
            new AgentRegistry(), new AgentTriggerService(new AgentRegistry()), new RecordingGateway(),
            new GroupChatOptions { MaxGroupMembers = 50, MessageHistoryLimit = 200, SnapshotMessageCount = 50 },
            TimeProvider.System, NullLogger<GroupHub>.Instance);
        return (catalog, skills, hub);
    }

    private static AgentOptions Opts() => new() { Provider = "mock" };

    private static OrgPlanAgent Agent(string id, params string[] skillIds) => new()
    {
        AgentId = id, Nickname = id, Description = "d", Instructions = "i", TriggerMode = "mentioned",
        SkillIds = [.. skillIds], AssignmentIds = [], EscalationAgentId = null,
    };

    [Fact]
    public void ToReusableSkills_ExcludesOrgToolsButKeepsDocx()
    {
        var opts = Opts();
        var skills = new AgentSkillCatalog(NullLoggerFactory.Instance, opts);
        skills.Upsert(new AgentSkillDefinition { SkillId = "org_design", Name = "设计", Kind = AgentSkillKind.Prompt });
        skills.Upsert(new AgentSkillDefinition { SkillId = "org_deploy", Name = "落库", Kind = AgentSkillKind.Org_deploy });

        var reusable = AgentOrchestrator.ToReusableSkills(skills.ListAll());
        var ids = reusable.Select(r => r.SkillId).ToList();

        // docx_* 是内置技能，应可选
        Assert.Contains("docx_gongwen", ids);
        Assert.Contains("docx_report", ids);
        // 构建师自身工具不应被编排到普通岗位
        Assert.DoesNotContain("org_design", ids);
        Assert.DoesNotContain("org_deploy", ids);
    }

    [Fact]
    public async Task Apply_MountsLibrarySkillReferencedDirectly()
    {
        // 方案里的岗位直接引用库内内置技能 docx_report（skills 数组为空）→ 应能落库并挂上
        var (catalog, skills, hub) = CreateSut(Opts());
        Assert.NotNull(skills.Get("docx_report"));

        var result = await OrgApplyEngine.ExecuteAsync(
            "boss", isAdmin: true,
            NoSkills, [Agent("writer", "docx_report")],
            createSupportCircle: false, supportCircleName: null, title: "文案组",
            catalog, skills, hub, Opts(), NullLoggerFactory.Instance, CancellationToken.None);

        var def = catalog.GetDefinition("writer");
        Assert.NotNull(def);
        Assert.Equal(["docx_report"], def!.SkillDefIds);
        Assert.NotNull(skills.Get("docx_report"));
    }

    private static readonly IReadOnlyList<OrgPlanSkill> NoSkills = [];

    [Fact]
    public async Task Apply_BodyBearingSkillWithLibraryName_StillRenamesToAvoidClobber()
    {
        // 与既有语义一致：方案自建（有正文）的技能即使与库内同名，也改名避重（docx_report → docx_report_2），
        // 不覆盖库内那份。只有“空正文”才被视为对库内技能的纯引用。
        var (catalog, skills, hub) = CreateSut(Opts());
        var plan = new List<OrgPlanSkill>
        {
            new() { SkillId = "docx_report", Name = "自建报告", Description = "d", Kind = "prompt", Body = "有正文" },
        };
        var result = await OrgApplyEngine.ExecuteAsync(
            "boss", isAdmin: true,
            plan, [Agent("writer", "docx_report")],
            createSupportCircle: false, supportCircleName: null, title: null,
            catalog, skills, hub, Opts(), NullLoggerFactory.Instance, CancellationToken.None);

        // 库内那份未被覆盖
        Assert.Equal(AgentSkillKind.Dotnet, skills.Get("docx_report")!.Kind);
        Assert.Contains("DocumentFormat.OpenXml", skills.Get("docx_report")!.Body);
        // 自建的改名落库
        var created = skills.Get("docx_report_2");
        Assert.NotNull(created);
        Assert.Equal("有正文", created!.Body);
    }

    [Fact]
    public async Task Apply_LibraryEntryInPlanWithEmptyBody_IsAccepted()
    {
        // org_design 的指引要求：引用库内技能时也在 skills 里列一份（body 可为空）。
        // 这类条目必须被识别为“复用”，不能因缺正文而报错。
        var (catalog, skills, hub) = CreateSut(Opts());
        var plan = new List<OrgPlanSkill>
        {
            new() { SkillId = "docx_report", Name = "工作报告生成（Word）", Description = "生成工作报告", Kind = "dotnet", Body = "" },
        };
        await OrgApplyEngine.ExecuteAsync(
            "boss", isAdmin: true, plan, [Agent("writer", "docx_report")],
            createSupportCircle: false, supportCircleName: null, title: null,
            catalog, skills, hub, Opts(), NullLoggerFactory.Instance, CancellationToken.None);

        // 未因空正文报错，且库内那份仍是原来的 dotnet 技能
        Assert.Equal(AgentSkillKind.Dotnet, skills.Get("docx_report")!.Kind);
        Assert.Equal(["docx_report"], catalog.GetDefinition("writer")!.SkillDefIds);
    }

    [Fact]
    public async Task Apply_StillRejectsTrulyUnknownSkill()
    {
        // 既不在技能库、方案里也没定义的技能 id：仍应拒绝（防止坏方案静默落库）
        var (catalog, skills, hub) = CreateSut(Opts());
        var ex = await Assert.ThrowsAsync<OrgApplyException>(() => OrgApplyEngine.ExecuteAsync(
            "boss", isAdmin: true, NoSkills, [Agent("writer", "no_such_skill")],
            createSupportCircle: false, supportCircleName: null, title: null,
            catalog, skills, hub, Opts(), NullLoggerFactory.Instance, CancellationToken.None));
        Assert.Contains("未定义技能", ex.Message);
    }

    [Fact]
    public async Task Apply_MixesLibraryAndNewlyBuiltSkills()
    {
        // B 方案：同一岗位既能引用库内技能，也能挂本次新建的技能
        var (catalog, skills, hub) = CreateSut(Opts());
        var plan = new List<OrgPlanSkill>
        {
            new() { SkillId = "my_prompt", Name = "自建", Description = "d", Kind = "prompt", Body = "内容" },
        };
        await OrgApplyEngine.ExecuteAsync(
            "boss", isAdmin: true, plan, [Agent("writer", "docx_report", "my_prompt")],
            createSupportCircle: false, supportCircleName: null, title: null,
            catalog, skills, hub, Opts(), NullLoggerFactory.Instance, CancellationToken.None);

        var ids = catalog.GetDefinition("writer")!.SkillDefIds;
        Assert.Contains("docx_report", ids);   // 库内
        Assert.Contains("my_prompt", ids);     // 新建
        Assert.NotNull(skills.Get("my_prompt"));
    }

    [Fact]
    public void ReusableSkillsSection_IsIncludedInPrompt()
    {
        // 关键：可复用技能必须真的进入编排提示词（否则模型无从得知库里有什么）。
        // 用 mock 不走提示词，因此这里直接验证提示词内容（通过测试钩子暴露）。
        var reusable = new List<AgentOrchestrator.ReusableSkill>
        {
            new("docx_report", "工作报告生成（Word）", "生成工作报告/总结/方案", "dotnet"),
        };
        var prompt = AgentOrchestrator.BuildPromptForTest("做一个文案推广组，需要能出报告", reusable);

        Assert.Contains("docx_report", prompt);
        Assert.Contains("工作报告生成（Word）", prompt);
        Assert.Contains("优先复用", prompt);
        // 且必须告诉模型引用写在 skillIds（而非重复定义）
        Assert.Contains("skillIds", prompt);
        Assert.Contains("不要", prompt);
    }

    [Fact]
    public void ReusableSkillsSection_AbsentWhenNoLibrary()
    {
        var prompt = AgentOrchestrator.BuildPromptForTest("随便建个组", null);
        Assert.DoesNotContain("技能库中已有的可复用技能", prompt);
    }

    [Fact]
    public void ReusableSkillsSection_CapsListLength()
    {
        // 技能库很大时不应把提示词撑爆：最多列 40 条
        var many = Enumerable.Range(1, 60)
            .Select(i => new AgentOrchestrator.ReusableSkill($"skill_{i}", $"技能{i}", "d", "prompt"))
            .ToList();
        var prompt = AgentOrchestrator.BuildPromptForTest("建组", many);
        Assert.Contains("skill_1", prompt);
        Assert.Contains("skill_40", prompt);
        Assert.DoesNotContain("skill_41", prompt);
    }
}
