namespace AguiGroupChat.Hub.Persistence;

/// <summary>
/// 进程内变更通知中心：各存储 / 服务在数据变更时调用 <see cref="Notify"/>，
/// <see cref="PersistenceService"/> 订阅后标记脏位，由后台定时器合并落盘。
/// 仅用于驱动持久化，不影响业务逻辑。
/// </summary>
public sealed class ChangeHub
{
    private readonly object _gate = new();
    private event Action? _changed;

    public void Subscribe(Action handler)
    {
        lock (_gate) _changed += handler;
    }

    public void Notify()
    {
        Action? handler;
        lock (_gate) handler = _changed;
        handler?.Invoke();
    }
}
