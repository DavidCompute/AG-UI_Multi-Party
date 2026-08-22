namespace AguiGroupChat.Hub.Storage;

/// <summary>任务状态。</summary>
public enum WorkTaskStatus
{
    /// <summary>排队中。</summary>
    Queue,
    /// <summary>执行中。</summary>
    Running,
    /// <summary>已完成。</summary>
    Finished,
    /// <summary>失败。</summary>
    Failed,
    /// <summary>已取消。</summary>
    Cancelled
}

public static class WorkTaskStatusExtensions
{
    public static WorkTaskStatus Parse(string s) => s.Trim().ToLowerInvariant() switch
    {
        "queue" => WorkTaskStatus.Queue,
        "running" => WorkTaskStatus.Running,
        "finished" => WorkTaskStatus.Finished,
        "failed" => WorkTaskStatus.Failed,
        "cancelled" => WorkTaskStatus.Cancelled,
        _ => WorkTaskStatus.Queue,
    };
}

/// <summary>工作任务实体（工作型智能体的任务级编排）。</summary>
public sealed class WorkTask
{
    public required string TaskId { get; init; }
    public required string GroupId { get; init; }
    public required string AgentId { get; init; }
    public required string UserId { get; init; }
    public string TopicId { get; init; } = "main";
    public required string Title { get; init; }
    public required string Content { get; init; }
    public WorkTaskStatus Status { get; set; } = WorkTaskStatus.Queue;
    public int Progress { get; set; }
    public string? Result { get; set; }
    public string? Error { get; set; }
    public long CreatedAt { get; init; }
    public long? FinishedAt { get; set; }
}

/// <summary>任务存储抽象（创建 / 状态更新 / 查询）。</summary>
public interface ITaskStore
{
    bool Add(WorkTask task);
    WorkTask? Get(string taskId);
    bool Update(WorkTask task);
    IReadOnlyList<WorkTask> ListForUser(string userId, int limit);
    IReadOnlyList<WorkTask> ListForGroup(string groupId, int limit);
    void ClearAll();
}
