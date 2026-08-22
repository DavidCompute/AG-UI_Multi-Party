using System.Collections.Concurrent;
using AguiGroupChat.Hub.Models;
using AguiGroupChat.Hub.Persistence;

namespace AguiGroupChat.Hub.Agents;

/// <summary>智能体在某个群内的触发规则（协议 §6）。</summary>
/// <param name="IsOverridden">是否在群内显式覆盖了角色默认触发模式；
/// 角色编辑时仅同步未覆盖（false）的注册，已覆盖的注册保持群内设定。</param>
public sealed record AgentRegistration(
    string AgentId,
    string Nickname,
    string GroupId,
    AgentTriggerMode TriggerMode,
    IReadOnlyList<string> Keywords,
    bool IsOverridden = false);

/// <summary>智能体注册表：记录每个群内智能体的触发规则，变更通知持久化。</summary>
public sealed class AgentRegistry
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, AgentRegistration>> _byGroup = new();
    private readonly ChangeHub? _changes;
    private readonly IAgentRegistryStore? _store;

    /// <summary>PostgreSQL 模式注入 <paramref name="store"/> 时，启动即从库加载全部注册，此后每次变更写通落库。</summary>
    public AgentRegistry(ChangeHub? changes = null, IAgentRegistryStore? store = null)
    {
        _changes = changes;
        _store = store;
        if (store is not null)
            RestoreAll(store.LoadAll()); // 存储不可用时在此快速失败（PG 模式 DB 不可达 = 应用不可用）
    }

    public void Register(AgentRegisterRequest req)
    {
        foreach (var groupId in req.GroupIds.Distinct())
        {
            var map = _byGroup.GetOrAdd(groupId, _ => new());
            var reg = new AgentRegistration(req.AgentId, req.Nickname, groupId, req.TriggerMode, req.Keywords ?? [], req.Override);
            map[req.AgentId] = reg;
            _store?.Upsert(reg);
        }
        _changes?.Notify();
    }

    public void Unregister(string agentId, IReadOnlyList<string>? groupIds)
    {
        var removed = false;
        if (groupIds is null || groupIds.Count == 0)
        {
            foreach (var map in _byGroup.Values) removed |= map.TryRemove(agentId, out _);
            if (removed) _store?.Delete(agentId, null);
        }
        else
        {
            foreach (var groupId in groupIds)
            {
                if (_byGroup.TryGetValue(groupId, out var map) && map.TryRemove(agentId, out _))
                {
                    removed = true;
                    _store?.Delete(agentId, groupId);
                }
            }
        }
        if (removed) _changes?.Notify();
    }

    /// <summary>角色昵称变更后同步所有群注册的昵称（不触碰各群的触发方式 / 覆盖标记）。</summary>
    public void UpdateNickname(string agentId, string nickname)
    {
        var changed = false;
        foreach (var map in _byGroup.Values)
        {
            if (!map.TryGetValue(agentId, out var r) || r.Nickname == nickname) continue;
            var updated = r with { Nickname = nickname };
            map[agentId] = updated;
            _store?.Upsert(updated);
            changed = true;
        }
        if (changed) _changes?.Notify();
    }

    public IReadOnlyList<AgentRegistration> ForGroup(string groupId)
        => _byGroup.TryGetValue(groupId, out var map) ? map.Values.ToList() : [];

    /// <summary>查询某智能体在指定群内的注册规则（未注册返回 null）。</summary>
    public AgentRegistration? ForGroupAgent(string groupId, string agentId)
        => _byGroup.TryGetValue(groupId, out var map) && map.TryGetValue(agentId, out var r) ? r : null;

    /// <summary>导出全部触发规则（供持久化快照）。</summary>
    public IReadOnlyList<AgentRegistration> AllRegistrations()
        => _byGroup.Values.SelectMany(map => map.Values).ToList();

    /// <summary>清空并恢复触发规则（启动恢复用，不触发脏标记）。</summary>
    public void RestoreAll(IEnumerable<AgentRegistration> registrations)
    {
        _byGroup.Clear();
        foreach (var r in registrations)
        {
            var map = _byGroup.GetOrAdd(r.GroupId, _ => new());
            map[r.AgentId] = r;
        }
    }

    /// <summary>清空全部触发注册（系统初始化用；数据库模式由表清空承担，此处仅清内存）。</summary>
    public void Clear()
    {
        _byGroup.Clear();
        _changes?.Notify();
    }
}
