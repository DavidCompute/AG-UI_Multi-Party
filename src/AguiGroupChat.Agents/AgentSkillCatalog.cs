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
        // 内置技能重放：与 appsettings 种子不同 —— 快照里的同名项优先（用户可编辑它）；
        // 但如果用户在界面上删除了内置技能，快照里就没有它，这里会把它带回来。
        // 这是有意为之：内置技能属“开箱即用”能力，删了重启就回来；要永久关闭请用
        // Agents:BuiltinDocxSkills=false。（若要“删了就永久没了”，需另存一份删除墓碑，代价大于收益。）
        foreach (var d in AguiGroupChat.Agents.BuiltinSkills.BuiltinDocxSkills.Definitions)
        {
            if (!_builtinSkills.Contains(d.SkillId)) continue;
            _skills.TryAdd(d.SkillId,
                AguiGroupChat.Agents.BuiltinSkills.BuiltinDocxSkills.Build(d.SkillId, d.ResourceSuffix, d.Name, d.Description));
        }
        foreach (var s in skills)
        {
            if (string.IsNullOrWhiteSpace(s.SkillId)) continue;
            _skills[s.SkillId] = s;
        }
        _logger.LogInformation("技能库恢复 {Count} 条（appsettings 种子 {SeedCount}，内置 {BuiltinCount}）",
            _skills.Count, _seeds.Count, _builtinSkills.Count);
    }
}
