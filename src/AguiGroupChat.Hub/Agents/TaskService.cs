using AguiGroupChat.Hub.Infra;
using AguiGroupChat.Hub.Storage;
using Microsoft.Extensions.Logging;

namespace AguiGroupChat.Hub.Agents;

/// <summary>
/// 工作任务编排服务：工作型智能体的任务级编排。创建任务（初始 Queue）→ 后台触发智能体运行 →
/// 网关在运行开始 / 结束回写状态。供 TaskApi（HTTP）与 AgentGateway 使用。
/// </summary>
public sealed class TaskService
{
    private readonly ITaskStore _store;
    private readonly ILogger<TaskService> _logger;

    public TaskService(ITaskStore store, ILogger<TaskService> logger)
    {
        _store = store;
        _logger = logger;
    }

    /// <summary>创建任务并返回 taskId（初始状态 Queue）。</summary>
    public string CreateTask(string groupId, string agentId, string userId, string topicId, string title, string content)
    {
        var id = "task_" + IdGenerator.NewId();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var task = new WorkTask
        {
            TaskId = id,
            GroupId = groupId,
            AgentId = agentId,
            UserId = userId,
            TopicId = string.IsNullOrWhiteSpace(topicId) ? "main" : topicId,
            Title = string.IsNullOrWhiteSpace(title) ? content : title,
            Content = content,
            Status = WorkTaskStatus.Queue,
            CreatedAt = now,
        };
        _store.Add(task);
        return id;
    }

    /// <summary>标记运行开始（Queue → Running）。</summary>
    public void MarkRunning(string taskId) => Update(taskId, t =>
    {
        if (t.Status is WorkTaskStatus.Queue or WorkTaskStatus.Failed) t.Status = WorkTaskStatus.Running;
    });

    /// <summary>标记运行完成（Running → Finished），记录结果摘要。失败时 error 非空。</summary>
    public void MarkFinished(string taskId, string? result, string? error = null)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Update(taskId, t =>
        {
            if (t.Status is not (WorkTaskStatus.Finished or WorkTaskStatus.Cancelled))
            {
                t.Status = error is null ? WorkTaskStatus.Finished : WorkTaskStatus.Failed;
                t.Progress = error is null ? 100 : t.Progress;
                t.Result = result;
                t.Error = error;
                t.FinishedAt = now;
            }
        });
    }

    /// <summary>更新进度（0-100）。</summary>
    public void UpdateProgress(string taskId, int progress) => Update(taskId, t =>
    {
        if (t.Status == WorkTaskStatus.Running) t.Progress = Math.Clamp(progress, 0, 100);
    });

    /// <summary>更新等待提示（不改变状态）：任务因需人工批准而挂起时，写入 Result 说明等待状态。</summary>
    public void UpdateProgressNote(string taskId, string note) => Update(taskId, t =>
    {
        if (t.Status is WorkTaskStatus.Running or WorkTaskStatus.Queue) t.Result = note;
    });

    /// <summary>取消任务：把未结束的任务标记为 Cancelled。</summary>
    public bool Cancel(string taskId)
    {
        var task = _store.Get(taskId);
        if (task is null) return false;
        if (task.Status is WorkTaskStatus.Finished or WorkTaskStatus.Failed or WorkTaskStatus.Cancelled) return false;
        task.Status = WorkTaskStatus.Cancelled;
        task.FinishedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return _store.Update(task);
    }

    public WorkTask? Get(string taskId) => _store.Get(taskId);
    public IReadOnlyList<WorkTask> ListForUser(string userId, int limit) => _store.ListForUser(userId, limit);
    public IReadOnlyList<WorkTask> ListForGroup(string groupId, int limit) => _store.ListForGroup(groupId, limit);

    private void Update(string taskId, Action<WorkTask> mutate)
    {
        var t = _store.Get(taskId);
        if (t is null) return;
        mutate(t);
        _store.Update(t);
    }
}
