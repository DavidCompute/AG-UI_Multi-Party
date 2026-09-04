using System.Text.Json;
using AguiGroupChat.Hub;
using AguiGroupChat.Hub.Messaging;
using AguiGroupChat.Hub.Models;
using AguiGroupChat.Hub.Users;
using Microsoft.Extensions.Logging;

namespace AguiGroupChat.Agents;

/// <summary>校验 / 业务错误：把原 Web apply 里“return 400/403”的语义带出，由调用方转 HTTP。</summary>
public sealed class OrgApplyException : Exception
{
    public string? Code { get; }
    public bool Forbidden { get; }
    public OrgApplyException(string code, string message, bool forbidden = false) : base(message) { Code = code; Forbidden = forbidden; }
}

/// <summary>编排方案技能条目（与 /orchestrate/apply 输入一致）。</summary>
public sealed class OrgPlanSkill
{
    public string? SkillId { get; set; } public string? Name { get; set; } public string? Description { get; set; }
    public string? Kind { get; set; } public string? Body { get; set; }
    public string? ExecutionLocation { get; set; } public bool? RequiresApproval { get; set; }
}
/// <summary>编排方案数字员工岗位。</summary>
public sealed class OrgPlanAgent
{
    public string? AgentId { get; set; } public string? Nickname { get; set; } public string? Description { get; set; }
    public string? Instructions { get; set; } public string? TriggerMode { get; set; }
    public IReadOnlyList<string>? SkillIds { get; set; } public IReadOnlyList<string>? AssignmentIds { get; set; }
    public string? EscalationAgentId { get; set; } public string? RelayToAgentId { get; set; }
}
/// <summary>apply 执行结果。</summary>
public sealed class OrgApplyResult
{
    public List<string> Agents { get; set; } = [];
    public List<string> Skills { get; set; } = [];
    public string? SupportCircleGroupId { get; set; }
    public List<object> Smoke { get; set; } = [];
}

/// <summary>
/// 官方组织落库引擎(唯一)：被「一键编排 apply」与“组织架构构建师”共用的同一份代码。
/// 行为与历史 /orchestrate/apply 完全一致：技能自动去重、server 技能自测（SkillAutoFixer at most 3次）、
/// 数字员工去重改名、连接目标校验、可选建客服知聚并注册触发规则。
/// </summary>
public static class OrgApplyEngine
{
    /// <summary>按给定方案真实落库。校验失败抛 <see cref="OrgApplyException"/>；成功返回结果（建客服知聚时 groupId 一并返回）。</summary>
    public static async Task<OrgApplyResult> ExecuteAsync(
        string ownerId, bool isAdmin, IReadOnlyList<OrgPlanSkill> skills, IReadOnlyList<OrgPlanAgent> agents,
        bool createSupportCircle, string? supportCircleName, string? title,
        AgentCatalog catalog, AgentSkillCatalog skillCatalog, GroupHub hub, AgentOptions agentOptions,
        ILoggerFactory loggerFactory, CancellationToken ct)
    {
        // ---- 1. 技能：去重 + 记录原→新映射，供数字员工引用重映射 (保持官方语义) ----
        var occupiedSkills = new HashSet<string>(skillCatalog.ListAll().Select(s => s.SkillId), StringComparer.Ordinal);
        var skillIdMap = new Dictionary<string, string>(StringComparer.Ordinal);
        var builtSkills = new List<AgentSkillDefinition>(skills.Count);
        foreach (var s in skills)
        {
            var orig = (s.SkillId ?? "").Trim();
            var id = AgentSkillDefinition.IsValidAsciiToolId(orig)
                ? AgentSkillDefinition.ToAsciiToolId(orig, occupiedSkills)
                : AgentSkillDefinition.ToAsciiToolId(orig, occupiedSkills);
            if (string.IsNullOrWhiteSpace(id)) throw new OrgApplyException(ErrorCodes.BadRequest, "技能标识不能为空");
            skillIdMap[orig] = id;
            if (string.IsNullOrWhiteSpace(s.Name) || string.IsNullOrWhiteSpace(s.Description) || string.IsNullOrWhiteSpace(s.Body))
                throw new OrgApplyException(ErrorCodes.BadRequest, $"技能「{id}」缺少名称 / 描述 / 正文");
            var kind = Enum.TryParse<AgentSkillKind>(s.Kind, true, out var k) ? k : AgentSkillKind.Prompt;
            if ((kind is AgentSkillKind.Shell or AgentSkillKind.Http or AgentSkillKind.Dotnet) && !isAdmin)
                throw new OrgApplyException(ErrorCodes.SkillPermissionDenied, $"仅管理员可建技能类型 {kind}", forbidden: true);
            var execLoc = string.Equals(s.ExecutionLocation, "client", StringComparison.OrdinalIgnoreCase)
                ? AgentSkillExecutionLocation.Client : AgentSkillExecutionLocation.Server;
            var requiresApproval = kind == AgentSkillKind.Shell || execLoc == AgentSkillExecutionLocation.Client || (s.RequiresApproval ?? true);
            builtSkills.Add(new AgentSkillDefinition
            {
                SkillId = id, Name = s.Name.Trim(), Description = s.Description.Trim(), Kind = kind,
                Body = s.Body ?? "", RequiresApproval = requiresApproval, ExecutionLocation = execLoc,
                ClientRunner = BuildClientRunner(kind, execLoc, s.Body ?? ""), OwnerId = ownerId,
            });
        }

        // ---- 1.5 技能自检（server 执行 skills 冒烟 + 自动修复，<=3次） ----
        var smokeResults = new List<object>();
        if (builtSkills.Count > 0)
        {
            var autoFixer = new SkillAutoFixer(agentOptions, catalog, loggerFactory);
            for (var i = 0; i < builtSkills.Count; i++)
            {
                var def = builtSkills[i];
                var smoke = await autoFixer.VerifyOrRepairAsync(def, maxAttempts: 3, ct).ConfigureAwait(false);
                smokeResults.Add(new { skillId = def.SkillId, skipped = smoke.Skipped, ok = smoke.Ok, attempts = smoke.Attempts, repaired = smoke.CorrectedBody != null, lastError = smoke.LastError });
                if (smoke.Ok && smoke.CorrectedBody != null)
                {
                    def.Body = smoke.CorrectedBody;
                    if (!string.IsNullOrWhiteSpace(smoke.CorrectedDescription)) def.Description = smoke.CorrectedDescription!;
                }
            }
        }

        if (agents.Count == 0) throw new OrgApplyException(ErrorCodes.BadRequest, "方案里没有数字员工");
        var occupiedAgents = new HashSet<string>(catalog.ListDefinitions().Select(d => d.AgentId), StringComparer.Ordinal);
        var agentIdMap = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var a in agents)
        {
            var orig = (a.AgentId ?? "").Trim();
            var id = AgentSkillDefinition.ToAsciiToolId(orig, occupiedAgents, "agent");
            if (id.StartsWith(TwinService.AgentIdPrefix, StringComparison.Ordinal))
                id = TwinService.AgentIdPrefix + "_" + id;
            if (string.IsNullOrWhiteSpace(id)) throw new OrgApplyException(ErrorCodes.BadRequest, $"数字员工 ID 非法：{orig}");
            if (string.IsNullOrWhiteSpace(a.Nickname)) throw new OrgApplyException(ErrorCodes.BadRequest, $"数字员工「{orig}」缺少昵称");
            agentIdMap[orig] = id;
        }
        foreach (var a in agents)
        {
            var finalId = agentIdMap[(a.AgentId ?? "").Trim()];
            foreach (var sid in a.SkillIds ?? [])
                if (!skillIdMap.TryGetValue(sid, out _))
                    throw new OrgApplyException(ErrorCodes.BadRequest, $"数字员工「{finalId}」引用了未定义技能：{sid}");
            foreach (var dep in (a.AssignmentIds ?? []).Concat(new[] { a.EscalationAgentId, a.RelayToAgentId }).Where(x => !string.IsNullOrWhiteSpace(x)))
                if (!agentIdMap.TryGetValue(dep!, out _))
                    throw new OrgApplyException(ErrorCodes.BadRequest, $"数字员工「{finalId}」的连接目标未定义：{dep}");
        }

        foreach (var s in builtSkills) skillCatalog.Upsert(s);
        var created = new List<string>();
        foreach (var a in agents)
        {
            var id = agentIdMap[(a.AgentId ?? "").Trim()];
            var remapSkill = (List<string>?)a.SkillIds?.Select(sid => skillIdMap[sid]).ToList();
            var remapAssign = (a.AssignmentIds ?? []).Where(x => !string.IsNullOrWhiteSpace(x)).Select(d => agentIdMap[d]).ToList();
            catalog.Upsert(new AgentDefinition
            {
                AgentId = id, Nickname = a.Nickname!.Trim(), Description = a.Description ?? "", Instructions = a.Instructions ?? "",
                TriggerMode = Enum.TryParse<AgentTriggerMode>(a.TriggerMode, true, out var tm) ? tm : AgentTriggerMode.Mentioned,
                SkillDefIds = remapSkill ?? [], AssignmentIds = remapAssign,
                EscalationAgentId = string.IsNullOrWhiteSpace(a.EscalationAgentId) ? null : agentIdMap[a.EscalationAgentId],
                RelayToAgentId = string.IsNullOrWhiteSpace(a.RelayToAgentId) ? null : agentIdMap[a.RelayToAgentId],
                OwnerId = ownerId,
            });
            created.Add(id);
        }

        string? supportGroupId = null;
        if (createSupportCircle)
        {
            var circleName = string.IsNullOrWhiteSpace(supportCircleName) ? (title ?? "客服组织") : supportCircleName!.Trim();
            var group = await hub.CreateGroupAsync(new GroupCreateRequest
            {
                GroupName = circleName, OwnerId = ownerId, Kind = GroupKind.Support, MemberIds = created,
                Members = created.Select(id => new MemberSeed
                {
                    MemberId = id, MemberType = MemberType.Agent,
                    Nickname = agents.FirstOrDefault(a => agentIdMap[(a.AgentId ?? "").Trim()] == id)?.Nickname ?? id,
                }).ToList(),
            }, ct);
            supportGroupId = group.GroupId;
            foreach (var id in created)
            {
                var def = catalog.GetDefinition(id);
                hub.RegisterAgent(new AgentRegisterRequest { AgentId = id, Nickname = def?.Nickname ?? id, GroupIds = [group.GroupId], TriggerMode = def?.TriggerMode ?? AgentTriggerMode.Mentioned, Keywords = def?.Keywords });
            }
        }

        return new OrgApplyResult { Agents = created, Skills = builtSkills.Select(s => s.SkillId).ToList(), SupportCircleGroupId = supportGroupId, Smoke = smokeResults };
    }

    /// <summary>客户端执行 shell 技能缺 clientRunner 时的默认构造（与历史保持一致）。</summary>
    internal static string? BuildClientRunner(AgentSkillKind kind, AgentSkillExecutionLocation loc, string body)
    {
        if (kind == AgentSkillKind.Shell && loc == AgentSkillExecutionLocation.Client && !string.IsNullOrWhiteSpace(body))
            return JsonSerializer.Serialize(new { kind = "shell", command = body, cwd = ".", timeoutSec = 30 });
        return null;
    }
}
