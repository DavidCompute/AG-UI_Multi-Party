using AguiGroupChat.Agents;
using AguiGroupChat.Hub.Agents;
using AguiGroupChat.Hub.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>
/// 多跳技能链测试：数字员工可通过技能逐层激活下游数字员工（A→B→C），
/// 并能在构建期破坏循环引用（A→B→A）与深度上限，避免无限递归。
/// </summary>
public sealed class SkillChainTests
{
    private static AgentCatalog BuildCatalog(params AgentDefinition[] agents)
    {
        var options = new AgentOptions
        {
            Provider = "mock",
            EnableTools = true,
            Agents = agents.ToList(),
        };
        return new AgentCatalog(options, NullLoggerFactory.Instance, new ServiceCollection().BuildServiceProvider());
    }

    private static AgentDefinition Def(string id, string nickname, params AgentSkillConfig[] skills) => new()
    {
        AgentId = id,
        Nickname = nickname,
        Description = "",
        Instructions = $"你是{nickname}",
        TriggerMode = AgentTriggerMode.Mentioned,
        Skills = skills.Length == 0 ? null : skills.ToList(),
    };

    private static AgentSkillConfig Skill(string skillId, string target) => new()
    {
        SkillId = skillId,
        Description = "调用技能",
        TargetAgentId = target,
    };

    [Fact]
    public void MultiHop_SkillTargetCarriesItsOwnSkill()
    {
        // 万事通 →(skill_hr) hr专员 →(skill_handbook) 员工手册解读专家
        var catalog = BuildCatalog(
            Def("wst", "万事通", Skill("skill_hr", "hr")),
            Def("hr", "hr专员", Skill("skill_handbook", "handbook")),
            Def("handbook", "员工手册解读专家"));

        // 构建顶层 万事通（会递归构建 skill 目标 hr，而 hr 作为技能目标现在也挂载了自己的技能）
        var wstTools = catalog.GetAgentToolNames("wst"); // 触发 GetOrCreate("wst")，递归填充 hr / handbook 的缓存
        Assert.Contains("skill_hr", wstTools);

        // 关键：hr 作为「万事通 的技能目标」构建时，缓存已带 skill_handbook -> 多跳技能链成立
        Assert.Contains("skill_handbook", catalog.GetCachedToolNames("hr"));
        Assert.Contains("skill_handbook", catalog.GetAgentToolNames("hr"));
    }

    [Fact]
    public void Cycle_AToBToA_TerminatesWithoutInfiniteRecursion()
    {
        // A →(skill_a_to_b) B →(skill_b_to_a) A：应在构建期破环，不栈溢出、不超时
        var catalog = BuildCatalog(
            Def("a", "A", Skill("skill_a_to_b", "b")),
            Def("b", "B", Skill("skill_b_to_a", "a")));

        // 关键回归防护：构建 A 必须正常返回（而非 StackOverflow / 挂死）
        var aTools = catalog.GetAgentToolNames("a");
        Assert.Contains("skill_a_to_b", aTools);
        // B 作为 A 的技能目标：由于 A 已在链中，B 不再递归展开 A 的技能 -> 不会无限
        var bTools = catalog.GetAgentToolNames("b");
        Assert.Contains("skill_b_to_a", bTools);
    }
}
