using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using AguiGroupChat.Hub.Messaging;
using AguiGroupChat.Hub.Persistence;
using AguiGroupChat.Hub.Models;
using Microsoft.Extensions.DependencyInjection;
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
    private readonly IServiceProvider _services;
    private readonly AgentCatalog _catalog;
    private readonly AgentSkillCatalog _skills;
    private readonly OrgTeamStore _store;
    private readonly AgentOptions _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly GroupHub? _hub;

    public OrgTeamCommitter(IServiceProvider services, AgentCatalog catalog, AgentSkillCatalog skills, OrgTeamStore store, AgentOptions options, ILoggerFactory loggerFactory)
    {
        _services = services;
        _catalog = catalog;
        _skills = skills;
        _store = store;
        _options = options;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<OrgTeamCommitter>();
        _hub = services.GetService<GroupHub>();
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

    /// <summary>公开：把某 key 上一版在该库产生的对象一并退役。</summary>
    public void Retire(string key) => RetireOld((key ?? "").Trim());

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
        if (plan.createSupportCircle && _hub is null)
            return (false, "当前环境未装配群服务，不能直接建客服知聚；请改用“一键编排→建客服知聚”，或先不带 createSupportCircle 建纯团队。");

        // 覆盖语义：先退役该 key 上一批（若曾建过），让官方唯一引擎用干净原始 id 整支（重新）建出来。
        try { RetireOld(key); }
        catch { /* 个别对象可能已被手动删：忽略，按现存不在场往下走 */ }

        var skillSpecs = (plan.skills ?? []).Select(s => new OrgPlanSkill
        {
            SkillId = s.skillId, Name = s.name, Description = s.description, Kind = s.kind,
            Body = s.body, ExecutionLocation = s.executionLocation, RequiresApproval = s.requiresApproval,
        }).ToList();
        var agentSpecs = (plan.agents ?? []).Select(a => new OrgPlanAgent
        {
            AgentId = a.agentId, Nickname = a.nickname, Description = a.description, Instructions = a.instructions,
            TriggerMode = a.triggerMode, SkillIds = a.skillIds, AssignmentIds = a.assignmentIds,
            EscalationAgentId = a.escalationAgentId, RelayToAgentId = a.relayToAgentId,
        }).ToList();

        OrgApplyResult result;
        try
        {
            result = await OrgApplyEngine.ExecuteAsync(
                ownerId: ownerId, isAdmin: true, skills: skillSpecs, agents: agentSpecs,
                createSupportCircle: plan.createSupportCircle, supportCircleName: null, title: plan.title,
                catalog: _catalog, skillCatalog: _skills, hub: _hub!, agentOptions: _options,
                loggerFactory: _loggerFactory, ct: ct);
        }
        catch (OrgApplyException ex)
        {
            return (false, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "org commit（经官方 apply 引擎）失败：key={Key}", key);
            return (false, "落库失败（未写入）：" + ex.Message);
        }

        var title = (plan.title ?? "").Trim();
        if (title.Length > MaxTitle) title = title[..MaxTitle];
        _store.Upsert(key, title, result.Agents, result.Skills, result.SupportCircleGroupId);
        return (true, $"已用官方一键编排引擎把「{(title.Length > 0 ? title : "这支团队")}」整批落库：数字员工 {string.Join(",", result.Agents)}；技能 {string.Join(",", result.Skills)}。库里只保留本批（key={key}）。");
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
