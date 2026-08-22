using AguiGroupChat.Hub.Agents;
using AguiGroupChat.Hub.Messaging;
using Microsoft.Extensions.Logging;

namespace AguiGroupChat.Agents;

/// <summary>
/// 智能体定时任务调度器：每分钟检查所有声明了 <see cref="AgentDefinition.Schedule"/>（5 段 cron，UTC）的智能体，
/// 命中当前时刻时向该智能体加入的<b>每个群</b>触发一次汇报运行（虚拟上下文：触发者 = system）。
/// 与消息触发（@ / 关键词 / 语境）独立，两者可同时生效。
/// 防重入：同一 (智能体, 群) 的上一轮运行未结束时跳过本轮，避免模型调用超时导致任务堆积。
/// </summary>
public sealed class AgentScheduler : IDisposable
{
    private const int TickIntervalMs = 60 * 1000; // 分钟粒度匹配 cron

    private readonly AgentCatalog _catalog;
    private readonly AgentGateway _gateway;
    private readonly GroupHub _hub;
    private readonly ScheduledTaskService? _scheduled;
    private readonly ILogger<AgentScheduler> _logger;
    private Timer? _timer;
    private readonly HashSet<string> _inFlight = new(StringComparer.Ordinal); // 正在运行的 agentId|groupId，防重入
    private readonly object _gate = new();

    public AgentScheduler(AgentCatalog catalog, AgentGateway gateway, GroupHub hub, ILogger<AgentScheduler> logger, ScheduledTaskService? scheduled = null)
    {
        _catalog = catalog;
        _gateway = gateway;
        _hub = hub;
        _scheduled = scheduled;
        _logger = logger;
    }

    /// <summary>启动调度器（应用就绪后调用；幂等）。</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_timer is not null) return;
            _timer = new Timer(_ => Tick(), null, TickIntervalMs, TickIntervalMs);
            _logger.LogInformation("智能体定时任务调度器已启动（每分钟检查一次 cron）");
        }
    }

    /// <summary>停止调度器（幂等）。</summary>
    public void Stop()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
        }
    }

    private void Tick()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var def in _catalog.ListDefinitions())
        {
            if (string.IsNullOrWhiteSpace(def.Schedule)) continue;
            if (!CronSchedule.TryParse(def.Schedule, out var cron, out var error) || cron is null)
            {
                if (error is not null)
                    _logger.LogWarning("智能体 {AgentId} 定时表达式非法（已忽略）：{Error}", def.AgentId, error);
                continue;
            }
            if (!cron.Matches(now)) continue;

            // 该智能体加入的全部群（成员关系以 store 为准）
            foreach (var groupId in _hub.Store.GroupsOf(def.AgentId).Select(g => g.GroupId))
                Fire(def, groupId, now);
        }

        // 重复性定时任务（1.4）：比单一 Schedule 更细——可按任务分别配置 cron / 汇报指令 / 目标群
        if (_scheduled is not null)
        {
            foreach (var task in _scheduled.Due(now))
            {
                var groupIds = string.IsNullOrWhiteSpace(task.GroupId)
                    ? _hub.Store.GroupsOf(task.AgentId).Select(g => g.GroupId)
                    : [task.GroupId];
                foreach (var groupId in groupIds)
                {
                    if (_hub.Store.GetGroup(groupId) is null) continue;
                    FireScheduled(task, groupId, now);
                }
            }
        }
    }

    private void FireScheduled(ScheduledTask task, string groupId, DateTimeOffset now)
    {
        var key = task.AgentId + "|" + groupId + "|" + task.TaskId;
        lock (_gate)
        {
            if (!_inFlight.Add(key)) return; // 上一轮未结束，跳过
        }
        var def = _catalog.GetDefinition(task.AgentId);
        var nickname = def?.Nickname ?? task.AgentId;
        _logger.LogInformation("重复性定时任务触发：task={TaskId} agent={AgentId} group={GroupId}", task.TaskId, task.AgentId, groupId);
        _ = Task.Run(async () =>
        {
            try
            {
                var content = string.IsNullOrWhiteSpace(task.Prompt)
                    ? $"【定时任务：{task.Name}】现在是 {now:yyyy-MM-dd HH:mm}（UTC）。请按你的职责主动向群里值守发言（汇报 / 催办 / 提醒），直接开始，无需确认。"
                    : $"【定时任务：{task.Name}】现在是 {now:yyyy-MM-dd HH:mm}（UTC）。本任务要求：\n{task.Prompt}\n\n请依此值守发言，直接开始，无需确认。";
                await _gateway.InvokeAsync(new AgentInvocationContext(
                    GroupId: groupId,
                    ThreadId: "thread_" + groupId,
                    AgentId: task.AgentId,
                    AgentNickname: nickname,
                    TriggerMessageId: "",
                    TriggerUserId: "system",
                    Content: content,
                    Mentions: [],
                    MentionAll: false), CancellationToken.None);
                task.LastFiredAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "重复性定时任务运行失败：task={TaskId} agent={AgentId} group={GroupId}", task.TaskId, task.AgentId, groupId);
            }
            finally
            {
                lock (_gate) _inFlight.Remove(key);
            }
        });
    }

    private void Fire(AgentDefinition def, string groupId, DateTimeOffset now)
    {
        var key = def.AgentId + "|" + groupId;
        lock (_gate)
        {
            if (!_inFlight.Add(key)) return; // 上一轮未结束，跳过本轮
        }
        _logger.LogInformation("智能体定时任务触发：agent={AgentId} group={GroupId} cron={Schedule}",
            def.AgentId, groupId, def.Schedule);
        _ = Task.Run(async () =>
        {
            try
            {
                var content = $"【定时任务触发】现在是 {now:yyyy-MM-dd HH:mm}（UTC）。"
                    + "请按你的职责主动向群里发言：例如汇报当前进展、发出提醒、或处理该时段应完成的事项。直接开始，无需确认。";
                await _gateway.InvokeAsync(new AgentInvocationContext(
                    GroupId: groupId,
                    ThreadId: "thread_" + groupId,
                    AgentId: def.AgentId,
                    AgentNickname: def.Nickname,
                    TriggerMessageId: "",
                    TriggerUserId: "system",
                    Content: content,
                    Mentions: [],
                    MentionAll: false), CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "智能体定时任务运行失败：agent={AgentId} group={GroupId}", def.AgentId, groupId);
            }
            finally
            {
                lock (_gate) _inFlight.Remove(key);
            }
        });
    }

    public void Dispose() => Stop();
}
