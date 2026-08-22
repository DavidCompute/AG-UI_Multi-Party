using AguiGroupChat.Hub.Agents;
using AguiGroupChat.Hub.Messaging;
using Microsoft.Extensions.Logging;

namespace AguiGroupChat.Agents;

/// <summary>
/// 一条<b>重复性定时任务</b>（1.4）：比单一 <see cref="AgentDefinition.Schedule"/> 更细——可为同一智能体
/// 配置多条任务，各自带名称、cron 与自定义汇报指令，按计划向群值守发言（如每日周报 / 到点催办）。
/// 经 <see cref="ScheduledTaskService"/> 持久化到扩展区（agui_sections / JSON 快照）。
/// </summary>
public sealed class ScheduledTask
{
    public required string TaskId { get; init; }
    /// <summary>归属智能体 ID。任务只能由该智能体执行（向它加入的群汇报）。</summary>
    public required string AgentId { get; set; }
    /// <summary>任务名称（如「每日站会催办」）。</summary>
    public string Name { get; set; } = "";
    /// <summary>cron 表达式（5 段，UTC）。</summary>
    public string Cron { get; set; } = "";
    /// <summary>触发时的汇报指令（给智能体的提示词，可空则用默认值守话术）。</summary>
    public string? Prompt { get; set; }
    /// <summary>目标群（空 = 该智能体所在的全部群，与 Schedule 一致）。</summary>
    public string? GroupId { get; set; }
    /// <summary>是否启用。禁用时不触发（保留配置）。</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>最近一次触发时间戳（毫秒，UTC），供界面展示。</summary>
    public long? LastFiredAt { get; set; }
}

/// <summary>
/// 重复性定时任务服务：内存持有 + 扩展区持久化，由 <see cref="AgentScheduler"/> 每分钟轮询触发。
/// </summary>
public sealed class ScheduledTaskService
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ScheduledTask> _tasks = new(StringComparer.Ordinal);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _lastFiredKey = new(StringComparer.Ordinal); // agentId|groupId → 触发时间戳（防同分钟重入）

    public IReadOnlyList<ScheduledTask> List(string? agentId = null)
        => _tasks.Values.Where(t => agentId is null || t.AgentId == agentId).OrderBy(t => t.AgentId).ToList();

    public ScheduledTask Get(string taskId) => _tasks.TryGetValue(taskId, out var t) ? t : null!;

    /// <summary>新增任务；taskId 由调用方生成（如 sched_xxx）。返回 null 表示 taskId 冲突。</summary>
    public ScheduledTask? Upsert(ScheduledTask task)
    {
        var exists = _tasks.ContainsKey(task.TaskId);
        _tasks[task.TaskId] = task;
        return exists ? task : task;
    }

    public bool Remove(string taskId)
    {
        var ok = _tasks.TryRemove(taskId, out _);
        if (ok)
        {
            foreach (var k in _lastFiredKey.Where(kv => kv.Key.StartsWith(taskId + "|", StringComparison.Ordinal)).Select(kv => kv.Key).ToList())
                _lastFiredKey.TryRemove(k, out _);
        }
        return ok;
    }

    public void Clear() { _tasks.Clear(); _lastFiredKey.Clear(); }

    /// <summary>快照（持久化）。</summary>
    public IReadOnlyList<ScheduledTask> Snapshot() => _tasks.Values.ToList();

    /// <summary>恢复。清空后再灌入。</summary>
    public void Restore(IEnumerable<ScheduledTask> tasks)
    {
        _tasks.Clear();
        foreach (var t in tasks) if (!string.IsNullOrWhiteSpace(t.TaskId)) _tasks[t.TaskId] = t;
    }

    /// <summary>校验并解析一条任务的 cron，非法返回中文错误。</summary>
    public static string? ValidateCron(string? cron)
    {
        if (string.IsNullOrWhiteSpace(cron)) return "cron 不能为空";
        if (!CronSchedule.TryParse(cron, out _, out var error) || error is not null)
        {
            var detail = error ?? "语法错误";
            return $"cron 不合法：{detail}";
        }
        return null;
    }

    /// <summary>由调度器每分钟调用：对命中当前时刻的启用任务，产出待触发集合（本分钟内不重复）。</summary>
    public IReadOnlyList<ScheduledTask> Due(DateTimeOffset now)
    {
        var due = new List<ScheduledTask>();
        foreach (var t in _tasks.Values)
        {
            if (!t.Enabled || string.IsNullOrWhiteSpace(t.Cron)) continue;
            if (!CronSchedule.TryParse(t.Cron, out var cron, out _) || cron is null) continue;
            if (!cron.Matches(now)) continue;
            var key = t.TaskId + "|" + now.Date + "|" + now.Hour + "|" + now.Minute; // 分钟级去重
            if (_lastFiredKey.TryAdd(key, now.ToUnixTimeMilliseconds()))
                due.Add(t);
        }
        return due;
    }
}
