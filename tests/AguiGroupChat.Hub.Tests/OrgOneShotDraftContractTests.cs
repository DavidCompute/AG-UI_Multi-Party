using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using AguiGroupChat.Agents;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>
/// “组织架构构建师”一键式首稿（org_plan_draft，复用 <see cref="AgentOrchestrator"/>）产出的 planJson，
/// 必须与「org_commit → OrgTeamCommitter.CommittedPlan」所解析的 one-click apply 字段完全相合，
/// 否则初稿在管理员确认后无法原样走官方 apply 落库。本测试锁定该序列化契约（纯内存、无网络）。
/// </summary>
public sealed class OrgOneShotDraftContractTests
{
    private static string SerializePlan(OrchestrationPlan plan) => JsonSerializer.Serialize(plan, new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    });

    private static OrchestrationPlan SamplePlan() => new()
    {
        Title = "样例组织",
        Agents =
        [
            new OrchestratedAgent
            {
                AgentId = "mgr", Nickname = "主管", Description = "统筹", Instructions = "你是主管。",
                TriggerMode = "mentioned", SkillIds = ["s_prompt", "s_shell"], AssignmentIds = ["a1"], EscalationAgentId = null, RelayToAgentId = null,
            },
        ],
        Skills =
        [
            new OrchestratedSkill { SkillId = "s_prompt", Name = "接待", Kind = "prompt", Description = "接待模板。", Body = "请接待。", ExecutionLocation = "server", RequiresApproval = false },
            new OrchestratedSkill { SkillId = "s_shell", Name = "本机速查", Kind = "shell", Description = "查信息。", Body = "Write-Host hi", ExecutionLocation = "client", RequiresApproval = true },
        ],
    };

    [Fact]
    public void GeneratedPlan_SerializesToOneClickCompatibleKeys()
    {
        var json = SerializePlan(SamplePlan());
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.True(root.TryGetProperty("title", out _));
        Assert.True(root.TryGetProperty("skills", out var skills));
        Assert.True(root.TryGetProperty("agents", out var agents));
        Assert.True(agents.GetArrayLength() >= 1);
        Assert.True(skills.GetArrayLength() >= 1);

        foreach (var a in agents.EnumerateArray())
        {
            var keys = a.EnumerateObject().Select(p => p.Name).ToHashSet();
            // 必需的常驻键必须存在
            foreach (var prop in new[] { "agentId", "nickname", "description", "instructions", "triggerMode", "skillIds" })
                Assert.True(a.TryGetProperty(prop, out _), "缺少 agents 字段 " + prop);
            // 可选键（可为 null 被省略）：但一旦出现，键名必须落在 OrgTeamCommitter 能解析的小驼峰集合内（防串线/残留 PascalCase）
            var allowed = new[] { "agentId", "nickname", "description", "instructions", "triggerMode", "skillIds", "assignmentIds", "escalationAgentId", "relayToAgentId" };
            Assert.True(keys.IsSubsetOf(allowed), "agents 出现无法被 apply 解析的字段：" + string.Join(",", keys.Except(allowed)));
        }

        var kinds = new HashSet<string>();
        foreach (var s in skills.EnumerateArray())
        {
            var keys = s.EnumerateObject().Select(p => p.Name).ToHashSet();
            var allowed = new[] { "skillId", "name", "description", "kind", "body", "executionLocation", "requiresApproval" };
            Assert.True(keys.IsSubsetOf(allowed), "skills 出现无法被 apply 解析的字段：" + string.Join(",", keys.Except(allowed)));
            foreach (var prop in new[] { "skillId", "name", "description", "kind", "body", "executionLocation" })
                Assert.True(s.TryGetProperty(prop, out _), "缺少 skills 字段 " + prop);
            if (s.TryGetProperty("kind", out var k)) kinds.Add(k.GetString() ?? "");
        }
        // 同稿里应能容纳多种 kind（shell/http/prompt），不只会是 pure prompt —— 这是让构建强于自由软稿的关键
        Assert.True(kinds.Count >= 2);
        Assert.Contains("shell", kinds);
    }

    [Fact]
    public void CommittedPlanStyleJson_IsReparseableByOrgCommitterShape()
    {
        // 模拟 org_commit → OrgTeamCommitter 用 json 反序列化：确保本工具序列化出的 JSON 能用“属性名不敏感 + 同一字段名”读回来
        var json = SerializePlan(SamplePlan());
        var reParsed = JsonSerializer.Deserialize<JsonElement>(json);
        Assert.True(reParsed.TryGetProperty("agents", out var ag));
        Assert.True(ag[0].TryGetProperty("skillIds", out var sids));
        Assert.True(sids.GetArrayLength() >= 1);
    }
}
