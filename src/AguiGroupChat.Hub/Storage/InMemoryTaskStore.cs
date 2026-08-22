using System.Collections.Concurrent;
using AguiGroupChat.Hub.Persistence;

namespace AguiGroupChat.Hub.Storage;

/// <summary>进程内线程安全的任务存储（按 taskId 索引，变更通知持久化）。</summary>
public sealed class InMemoryTaskStore : ITaskStore
{
    private readonly ConcurrentDictionary<string, WorkTask> _tasks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (string GroupId, string UserId)> _index = new(StringComparer.Ordinal);
    private readonly ChangeHub? _changes;

    public InMemoryTaskStore(ChangeHub? changes = null) => _changes = changes;

    public bool Add(WorkTask task)
    {
        if (!_tasks.TryAdd(task.TaskId, task)) return false;
        _index[task.TaskId] = (task.GroupId, task.UserId);
        _changes?.Notify();
        return true;
    }

    public WorkTask? Get(string taskId) => _tasks.TryGetValue(taskId, out var t) ? t : null;

    public bool Update(WorkTask task)
    {
        var ok = _tasks.TryUpdate(task.TaskId, task, _tasks.TryGetValue(task.TaskId, out var cur) ? cur : null!);
        if (ok) _changes?.Notify();
        return ok;
    }

    public IReadOnlyList<WorkTask> ListForUser(string userId, int limit)
        => _tasks.Values.Where(t => t.UserId == userId).OrderByDescending(t => t.CreatedAt).Take(limit).ToList();

    public IReadOnlyList<WorkTask> ListForGroup(string groupId, int limit)
        => _tasks.Values.Where(t => t.GroupId == groupId).OrderByDescending(t => t.CreatedAt).Take(limit).ToList();

    public void ClearAll()
    {
        _tasks.Clear();
        _index.Clear();
        _changes?.Notify();
    }
}
