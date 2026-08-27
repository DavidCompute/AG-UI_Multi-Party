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
        foreach (var s in skills)
        {
            if (string.IsNullOrWhiteSpace(s.SkillId)) continue;
            _skills[s.SkillId] = s;
        }
        _logger.LogInformation("技能库恢复 {Count} 条（种子 {SeedCount}）", _skills.Count, _seeds.Count);
    }
}
