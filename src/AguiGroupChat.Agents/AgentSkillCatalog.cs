using System.Collections.Concurrent;
using System.Text.Json;
using AguiGroupChat.Hub.Persistence;
using Microsoft.Extensions.Logging;

namespace AguiGroupChat.Agents;

/// <summary>
/// 技能库：全局可复用的技能定义目录（OpenClaw 风格）。
/// 与 <see cref="AgentCatalog"/> 同模式——内存索引 + 变更通知 + 快照持久化恢复。
/// 技能按 SkillId（ASCII 工具名）索引；任意数字员工经其 <see cref="AgentDefinition.SkillDefIds"/> 挂载引用。
/// </summary>
public sealed class AgentSkillCatalog
{
    private readonly ILogger<AgentSkillCatalog> _logger;
    private readonly ConcurrentDictionary<string, AgentSkillDefinition> _skills = new(StringComparer.Ordinal);
    private readonly List<AgentSkillDefinition> _seeds = []; // appsettings 种子，常驻（恢复/删除不丢）
    private readonly List<string> _builtinSkills = []; // 内置技能 id（正文来自嵌入资源；恢复时重放，但不阻止用户在界面删除）

    public AgentSkillCatalog(ILoggerFactory loggerFactory, AgentOptions? options = null)
    {
        _logger = loggerFactory.CreateLogger<AgentSkillCatalog>();
        // 以 appsettings（AgentOptions.Skills）为常驻种子：提供开箱即用的可复用技能，与智能体（AgentCatalog）同模式
        foreach (var s in options?.Skills ?? [])
        {
            if (string.IsNullOrWhiteSpace(s.SkillId)) continue;
            if (!_skills.TryAdd(s.SkillId, s)) continue;
            _seeds.Add(s);
        }
        // 内置「文档生成」技能（公义 / 通知公告 / 工作报告）：正文随程序集分发，开箱即用
        if (AguiGroupChat.Agents.BuiltinSkills.BuiltinDocxSkills.IsEnabled(options?.BuiltinDocxSkills))
        {
            foreach (var d in AguiGroupChat.Agents.BuiltinSkills.BuiltinDocxSkills.Definitions)
            {
                if (!_skills.TryAdd(d.SkillId,
                        AguiGroupChat.Agents.BuiltinSkills.BuiltinDocxSkills.Build(d.SkillId, d.ResourceSuffix, d.Name, d.Description)))
                    continue;
                _builtinSkills.Add(d.SkillId);
            }
        }
        if (_seeds.Count > 0)
            _logger.LogInformation("技能库播种 {Count} 条（来自 AgentOptions.Skills）", _seeds.Count);
    }

    /// <summary>技能 ID → 定义；不存在返回 null。若所给引用不是 ASCII 工具 ID（历史数据 / 中文引用），按原名再查一次。</summary>
    public AgentSkillDefinition? Get(string skillId)
        => string.IsNullOrWhiteSpace(skillId)
            ? null
            : _skills.TryGetValue(skillId, out var d) ? d : null;

    /// <summary>全部技能（定义顺序）。</summary>
    public IReadOnlyList<AgentSkillDefinition> ListAll()
        => _skills.Values.ToList();

    /// <summary>新增 / 更新技能定义（SkillId 存在则覆盖）。</summary>
    public void Upsert(AgentSkillDefinition def)
    {
        _skills[def.SkillId] = def;
        _logger.LogInformation("技能库更新：{SkillId}（{Kind}，{Name}）", def.SkillId, def.Kind, def.Name);
    }

    /// <summary>删除技能定义；返回是否存在。种子技能不可删除（AppSettings 声明，常驻）。</summary>
    public bool Remove(string skillId)
    {
        if (_seeds.Any(s => s.SkillId == skillId)) return false;
        return _skills.TryRemove(skillId, out _);
    }

    /// <summary>是否已存在该技能 ID。</summary>
    public bool Contains(string skillId) => _skills.ContainsKey(skillId);

    /// <summary>从持久化快照恢复：先重放常驻种子，再由快照按 SkillId 覆盖（持久化版本优先）。</summary>
    public void RestoreAll(IEnumerable<AgentSkillDefinition> skills)
    {
        _skills.Clear();
        foreach (var s in _seeds) _skills.TryAdd(s.SkillId, s);
        // 内置技能重放。三个来源按优先级：
        //   1) 随程序集发布的内置正文（本次版本）
        //   2) 快照里用户改过的（Build 时已打上 BuiltinVersion = null → 视为用户自己的，不覆盖）
        //
        // 为什么不能简单“快照优先”：内置技能正文会随平台升级而修 bug（如落盘目录/字体/命名），
        // 若旧快照永远压新正文，用户升级后仍跑旧实现（本仓库真实踩到过）。
        // 因此改为：快照里那份若是“未改过的内置版”（带 BuiltinVersion），就用新正文刷新；
        // 用户真正编辑过的（BuiltinVersion 已置空）仍以用户为准。
        var freshBuiltins = new Dictionary<string, AgentSkillDefinition>(StringComparer.Ordinal);
        foreach (var d in AguiGroupChat.Agents.BuiltinSkills.BuiltinDocxSkills.Definitions)
        {
            if (!_builtinSkills.Contains(d.SkillId)) continue;
            var def = AguiGroupChat.Agents.BuiltinSkills.BuiltinDocxSkills.Build(d.SkillId, d.ResourceSuffix, d.Name, d.Description);
            freshBuiltins[d.SkillId] = def;
        }
        foreach (var s in skills)
        {
            if (string.IsNullOrWhiteSpace(s.SkillId)) continue;
            if (freshBuiltins.TryGetValue(s.SkillId, out var fresh) && IsUntouchedBuiltin(s))
            {
                _skills[s.SkillId] = fresh;
                continue;
            }
            _skills[s.SkillId] = s;
        }
        // 快照里没有的（如被删过 / 首次上线）→ 补回内置
        foreach (var kv in freshBuiltins) _skills.TryAdd(kv.Key, kv.Value);
        _logger.LogInformation("技能库恢复 {Count} 条（appsettings 种子 {SeedCount}，内置 {BuiltinCount}）",
            _skills.Count, _seeds.Count, _builtinSkills.Count);
    }

    /// <summary>
    /// 判断快照里那份内置技能是否“未被用户改过”，以决定升级时是否用新正文刷新。
    /// <para>两种可判定为“未改过”的情形：</para>
    /// <list type="item">
    ///   <item>带 BuiltinVersion 标记（字段上线后的正常播种/刷新）；</item>
    ///   <item>字段上线前就已持久化的旧内置技能：无 builtinVersion、且 OwnerId 为 null（平台内置）。
    ///     这类当时没有任何编辑入口保留归属，因此视为未改过。</item>
    /// </list>
    /// <para>用户经界面编辑过的会写入 OwnerId（编辑器）且 BuiltinVersion 被置空，因此不会被误刷。</para>
    /// </summary>
    private static bool IsUntouchedBuiltin(AgentSkillDefinition snapshot)
        => snapshot.BuiltinVersion is not null || snapshot.OwnerId is null;
}
