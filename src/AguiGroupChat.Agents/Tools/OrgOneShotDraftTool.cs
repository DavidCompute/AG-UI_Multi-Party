using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace AguiGroupChat.Agents.Tools;

/// <summary>
/// 内置组织角色（挂载 org_design，如「组织架构构建师 / 组织运营官」）的「一键式首稿」动作：
/// 把一句组织构建需求交给 <see cref="AgentOrchestrator.GenerateAsync"/>——与网页「一键组织编排」同一个
/// 结构化生成引擎，在<b>一轮收敛 prompt</b> 里产出{岗位 + 各岗 skillIds + 技能(kind/executionLocation/approval) + 连接}
/// 的完整方案 JSON，避免自由多轮对话把组织稿磨成偏 prompt、结构松散的软稿。
/// <para>本动作<b>只生成不落库</b>（无副作用、不写技能/员工/连接、不校验管理员）：生成结果交由模型转述为
/// 「待确认最终稿」给用户；用户在对话里明确全部认可后，再由同组织角色经既有 <c>org_commit</c> 动作
/// （仅平台管理员写库，经 <see cref="OrgApplyEngine"/> 落库）真正提交。</para>
/// </summary>
public sealed class OrgOneShotDraftTool
{
    private readonly AgentOptions _options;
    private readonly ILogger _logger;

    public OrgOneShotDraftTool(AgentOptions options, ILoggerFactory loggerFactory)
    {
        _options = options;
        _logger = loggerFactory.CreateLogger<OrgOneShotDraftTool>();
    }

    /// <summary>
    /// 根据一句构建需求，立即用「一键组织编排」同款的单轮结构化生成产出一版完整组织初稿。
    /// 参数 requirement：用户这次要建的这支组织的目标 / 需求（把分工职责、岗位、需要的技能说清楚）。
    /// 返回完整初稿 JSON（字段与组织落库 apply 即 one-click apply 一致：title/skills/agents）。
    /// 仅生成、不落库：先在对话里呈现成稿、逐项征得用户认可，确认后再用 org_commit 落库。
    /// </summary>
    public async Task<string> Draft(
        [System.ComponentModel.Description("用户这次要构建/打造的组织一句话需求（尽量含岗位分工、职责、期望的技能与层级）")] string requirement)
    {
        if (string.IsNullOrWhiteSpace(requirement))
            return "请先提供要构建的组织需求（一句话即可：目标、分哪些岗位、各岗位要具备的能力）。";

        try
        {
            var plan = await AgentOrchestrator.GenerateAsync(_options, requirement.Trim(), _logger, CancellationToken.None);

            // 与 org_commit 期望的 one-click apply 相一致的字段名（agentId/skillIds/... 小驼峰）
            var json = JsonSerializer.Serialize(plan, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            });

            var summary = (plan.Title ?? "这支团队").Length == 0 ? "这支团队" : plan.Title;
            return $"已用「一键组织编排」同款引擎按你的需求产出一版组织初稿《{summary}》："
                + $"{plan.Agents.Count} 个数字员工（{string.Join("、", plan.Agents.Select(a => a.Nickname ?? a.AgentId ?? ""))}），"
                + $"{plan.Skills.Count} 个技能（含 shell/http/prompt 多种运行方式，非纯 prompt）。"
                + "完整成稿 JSON 请照抄给用户确认（这就是最终稿，落库时 org_commit 的 planJson 用同一段）。以下是完整成稿 JSON：\n" + json;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "org 一键式首稿生成失败");
            return "一键式首稿生成未成功：" + ex.Message + "。请改用逐岗位手工打磨并照 org_design 出稿。";
        }
    }
}
