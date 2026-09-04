using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using AguiGroupChat.Hub.Persistence;
using AguiGroupChat.Hub.Models;
using Microsoft.Extensions.Logging;

namespace AguiGroupChat.Agents;

/// <summary>
/// 一支“运营组织”在某次落库后产生的对象清单（团队 key → 该批 [agentId…, skillId…]）。
/// 目的：让内置的组织角色对同一支团队反复覆盖时，能先删除上一版、只保留最新一版（跨会话/跨重启亦如此）。
/// 经 <see cref="AgentHosting.RegisterOrgTeamPersistence"/> 落入已有持久化扩展区（memory 快照 / postgres agui_sections）。
/// </summary>
public sealed class OrgTeamRecord
{
    public string Key { get; set; } = "";
    public string Title { get; set; } = "";
    public List<string> Agents { get; set; } = [];
    public List<string> Skills { get; set; } = [];
    public string? SupportCircleGroupId { get; set; }
    public long CreatedAtMs { get; set; }
    public long UpdatedAtMs { get; set; }
}

/// <summary>内存权威 + ChangeHub 持久化的窄映射（团队 key → 上次落库产生的对象）。</summary>
public sealed class OrgTeamStore
{
    private readonly ChangeHub? _changes;
    private readonly ConcurrentDictionary<string, OrgTeamRecord> _byKey = new(StringComparer.Ordinal);
    public OrgTeamStore(ChangeHub? changes = null) => _changes = changes;

    public OrgTeamRecord? Get(string key) => _byKey.TryGetValue(key, out var r) ? r : null;
    public IReadOnlyList<OrgTeamRecord> All() => _byKey.Values.ToList();

    public OrgTeamRecord Upsert(string key, string title, List<string> agents, List<string> skills, string? supportGroupId)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var rec = _byKey.AddOrUpdate(key,
            _ => new OrgTeamRecord { Key = key, Title = title, Agents = agents, Skills = skills, SupportCircleGroupId = supportGroupId, CreatedAtMs = now, UpdatedAtMs = now },
            (_, old) => new OrgTeamRecord { Key = key, Title = title, Agents = agents, Skills = skills, SupportCircleGroupId = supportGroupId, CreatedAtMs = old.CreatedAtMs == 0 ? now : old.CreatedAtMs, UpdatedAtMs = now });
        _changes?.Notify();
        return rec;
    }

    public bool Remove(string key, out OrgTeamRecord? removed)
    {
        var ok = _byKey.TryRemove(key, out removed);
        if (ok) _changes?.Notify();
        return ok;
    }

    public void RestoreAll(IEnumerable<OrgTeamRecord> records)
    {
        _byKey.Clear();
        foreach (var r in records)
            if (!string.IsNullOrWhiteSpace(r.Key)) _byKey[r.Key] = r;
    }

    public IReadOnlyList<OrgTeamRecord> SnapshotAll() => _byKey.Values.Select(r => new OrgTeamRecord
    {
        Key = r.Key, Title = r.Title, Agents = new List<string>(r.Agents), Skills = new List<string>(r.Skills),
        SupportCircleGroupId = r.SupportCircleGroupId, CreatedAtMs = r.CreatedAtMs, UpdatedAtMs = r.UpdatedAtMs,
    }).ToList();
}

/// <summary>
/// 受控的内置组织提交器：把“构建好的最终稿”真正落成库中一支（数字员工 + 技能 + 连接），并支持<b>按团队 key 覆盖</b>——
/// 覆盖时先删除该 key 上一版产生的对象、再按新稿重建，使同一支反复修改只在库里留下最新版。
/// 本服务不触碰 Web 端现有 <c>orchestrate/apply</c> handler，仅复用底层原语（catalog/skillCatalog）达成同样的低层落库语义。
/// 默认只落数字员工与技能；本实现不下发/不建客服知聚（support circle）语义，需建群请走 Web 一方。
/// </summary>
public sealed class OrgTeamCommitter
{
    private const int MaxTitle = 200;
    private readonly AgentCatalog _catalog;
    private readonly AgentSkillCatalog _skills;
    private readonly OrgTeamStore _store;
    private readonly ILogger _logger;

    public OrgTeamCommitter(AgentCatalog catalog, AgentSkillCatalog skills, OrgTeamStore store, ILoggerFactory loggerFactory)
    {
        _catalog = catalog;
        _skills = skills;
        _store = store;
        _logger = loggerFactory.CreateLogger<OrgTeamCommitter>();
    }

    // plan JSON 轻量 DTO（对齐系统一键编排的输入字段，关键字小驼峰）
    private sealed class CommittedSkill { public string? skillId { get; set; } = null; public string? name { get; set; } public string? description { get; set; } public string? kind { get; set; } public string? body { get; set; } public string? executionLocation { get; set; } public bool? requiresApproval { get; set; } }
    private sealed class CommittedAgent { public string? agentId { get; set; } public string? nickname { get; set; } public string? description { get; set; } public string? instructions { get; set; } public string? triggerMode { get; set; } public List<string>? skillIds { get; set; } public List<string>? assignmentIds { get; set; } public string? escalationAgentId { get; set; } public string? relayToAgentId { get; set; } }
    private sealed class CommittedPlan { public string title { get; set; } = ""; public List<CommittedSkill>? skills { get; set; } public List<CommittedAgent>? agents { get; set; } public bool createSupportCircle { get; set; } = false; }

    /// <summary>移除某团队上一版在此库产生的数字员工与技能（仅删除由本记录登记的、仍存在的对象；404/不存在跳过）。</summary>
    private void RetireOld(string key)
    {
        var old = _store.Get(key);
        if (old is null) return;
        foreach (var id in old.Agents)
        {
            if (_catalog.GetDefinition(id) is not { } def) continue;
            if (def.IsSkillTarget || string.IsNullOrEmpty(def.OwnerId)) continue; // 不删技能目标 / 系统内置
            _catalog.Remove(id);
        }
        foreach (var sid in old.Skills)
        {
            if (_skills.Get(sid) is { } s && !string.IsNullOrEmpty(s.OwnerId)) _skills.Remove(sid);
        }
    }

    /// <summary>把一份最终方案落地到库。覆盖=真——先退役该 key 上一版对象再重建。成功 true 返回 {agents/skills}；失败 false 带 message（部分失败已回滚=不写入）。</summary>
    public async Task<(bool ok, string message)> CommitAsync(string key, string planJson, string ownerId, bool isAdmin, CancellationToken ct = default)
    {
        if (!isAdmin)
            return (false, "只有系统管理员可以在群里真正落库组织。你作为普通用户获得的是方案预览；请平台管理员把该最终稿放行落库后即生效。本次未写入任何数据。");
        key = (key ?? "").Trim();
        if (key.Length == 0) return (false, "缺少团队 key（请给这支组织起个英文短名字，如 it_support）。");
        CommittedPlan? plan;
        try { plan = JsonSerializer.Deserialize<CommittedPlan>(planJson, JsonOpt); }
        catch (Exception ex) { return (false, "方案 JSON 无法解析：" + ex.Message); }
        if (plan is null || (plan.agents ?? []).Count == 0) return (false, "方案里没有数字员工。");
        if (plan.createSupportCircle) return (false, "本受控通道当前不直接建客服知聚；请走“一键编排→确认建客服知聚”，或去掉 createSupportCircle 以纯团队形式落库。");

        // 全部校验通过前不被写入：先解析/校验，成功才 Retire+落
        var newSkills = new List<(string id, AgentSkillDefinition def)>();
        var usedSkill = new HashSet<string>(_skills.ListAll().Select(s => s.SkillId), StringComparer.Ordinal);
        foreach (var s in plan.skills ?? [])
        {
            var name = (s.name ?? "").Trim();
            var desc = (s.description ?? "").Trim();
            var body = (s.body ?? "").Trim();
            if (name.Length == 0 || desc.Length == 0 || body.Length == 0) return (false, $"技能「{name}」缺少名称/描述/正文。");
            var kind = Enum.TryParse<AgentSkillKind>(s.kind, true, out var k) ? k : AgentSkillKind.Prompt;
            if (!isAdmin && kind is AgentSkillKind.Shell or AgentSkillKind.Http or AgentSkillKind.Dotnet)
                return (false, $"技能类型 {kind} 仅系统管理员可建。");
            // id 规范化 + 避让其余（非本团队）已占用
            var raw = (s.skillId ?? "").Trim();
            var id = AgentSkillDefinition.IsValidAsciiToolId(raw) ? raw : SanitizeId(raw, "skill");
            id = AvailableId(id, usedSkill);
            usedSkill.Add(id);
            var execLoc = string.Equals(s.executionLocation, "client", StringComparison.OrdinalIgnoreCase) ? AgentSkillExecutionLocation.Client : AgentSkillExecutionLocation.Server;
            var approvable = s.requiresApproval ?? true;
            var reqApprove = kind == AgentSkillKind.Shell || execLoc == AgentSkillExecutionLocation.Client || approvable;
            newSkills.Add((id, new AgentSkillDefinition { SkillId = id, Name = name.Substring(0, Math.Min(200, name.Length)), Description = desc, Kind = kind, Body = body, RequiresApproval = reqApprove, ExecutionLocation = execLoc, OwnerId = ownerId }));
        }
        var usedAgent = new HashSet<string>(_catalog.ListDefinitions().Select(d => d.AgentId), StringComparer.Ordinal);
        var idMap = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var a in plan.agents!)
        {
            var nick = (a.nickname ?? "").Trim();
            if (nick.Length == 0) return (false, "存在缺少昵称的数字员工。");
            var raw = (a.agentId ?? "").Trim();
            var alt = SanitizeId((raw.Length > 0 ? raw : nick), "agent");
            var id = AvailableId(alt, usedAgent);
            usedAgent.Add(id);
            idMap[raw.Length > 0 ? raw : nick] = id;
        }
        // 引用目标校验：所引技能必须先在本方案定义成数字员工内建技能，连接目标必须在本方案内
        foreach (var a in plan.agents!)
        {
            var nick = (a.nickname ?? "").Trim();
            foreach (var sid in a.skillIds ?? [])
                if (!newSkills.Any(ns => ns.id == sid))
                    return (false, $"数字员工「{nick}」引用了未定义技能：{sid}");
            foreach (var dep in (a.assignmentIds ?? []).Concat(new[] { a.escalationAgentId, a.relayToAgentId }).Where(x => !string.IsNullOrWhiteSpace(x)))
                if (!idMap.ContainsKey(dep!))
                    return (false, $"数字员工「{nick}」的连接目标未定义：{dep}");
        }

        // 校验通过后落库（覆盖模式先把上版退役）
        RetireOld(key);
        foreach (var (sid, def) in newSkills) _skills.Upsert(def);
        var createdAgents = new List<string>();
        foreach (var a in plan.agents!)
        {
            var nick = (a.nickname ?? "").Trim();
            var raw = (a.agentId ?? "").Trim();
            var origId = raw.Length > 0 ? raw : nick;
            var finalId = idMap[origId.Length > 0 ? origId : nick];
            createdAgents.Add(finalId);
            var remapSkill = (a.skillIds ?? []).Where(sid => newSkills.Any(ns => ns.id == sid)).Select(sid => newSkills.First(ns => ns.id == sid).id).ToList();
            var remapAssign = (a.assignmentIds ?? []).Where(x => idMap.ContainsKey(x)).Select(x => idMap[x]).ToList();
            var def = new AgentDefinition
            {
                AgentId = finalId,
                Nickname = nick.Substring(0, Math.Min(200, nick.Length)),
                Description = a.description?.Trim() ?? "",
                Instructions = a.instructions?.Trim() ?? "",
                TriggerMode = Enum.TryParse<AgentTriggerMode>(a.triggerMode, true, out var tm) ? tm : AgentTriggerMode.Mentioned,
                SkillDefIds = remapSkill,
                AssignmentIds = remapAssign,
                EscalationAgentId = string.IsNullOrWhiteSpace(a.escalationAgentId) ? null : (idMap.TryGetValue(a.escalationAgentId, out var ec) ? ec : null),
                RelayToAgentId = string.IsNullOrWhiteSpace(a.relayToAgentId) ? null : (idMap.TryGetValue(a.relayToAgentId, out var rc) ? rc : null),
                OwnerId = ownerId,
            };
            _catalog.Upsert(def);
        }
        var title = (plan.title ?? "").Trim();
        if (title.Length > MaxTitle) title = title[..MaxTitle];
        _store.Upsert(key, title, createdAgents, newSkills.Select(ns => ns.id).ToList(), null);
        return (true, $"已把「{(title.Length > 0 ? title : "这支团队")}」整批落库更新到最新一版：数字员工 {string.Join(",", createdAgents)}；技能 {string.Join(",", newSkills.Select(ns => ns.id))}。库里只保留本批（key={key}）。");
    }

    private static string AvailableId(string preferred, HashSet<string> occupied)
    {
        if (occupied.Add(preferred)) return preferred;
        for (var i = 2; ; i++)
        {
            var id = $"{preferred}_{i}";
            if (occupied.Add(id)) return id;
        }
    }

    private static string SanitizeId(string raw, string kind)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var c in raw)
        {
            if (char.IsAsciiLetterOrDigit(c) || c is '_' or '-') sb.Append(c);
            else if (c == ' ' || c == '\t') { if (sb.Length > 0 && sb[^1] != '_') sb.Append('_'); }
        }
        var s = sb.ToString().Trim('_');
        return (s.Length == 0 ? kind + "_" : s.Length > 40 ? s[..40] : s).ToLowerInvariant();
    }

    private static readonly JsonSerializerOptions JsonOpt = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
