using System.Collections.Concurrent;
using AguiGroupChat.Hub.Persistence;

namespace AguiGroupChat.Hub.Users;

/// <summary>进程内线程安全的用户账号存储（userId 主键 + username 唯一索引），变更通知持久化。</summary>
public sealed class InMemoryUserStore : IUserStore
{
    private readonly ConcurrentDictionary<string, UserAccount> _byId = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _byUsername = new(StringComparer.OrdinalIgnoreCase);
    private readonly ChangeHub? _changes;

    public InMemoryUserStore(ChangeHub? changes = null) => _changes = changes;

    public bool AddUser(UserAccount user)
    {
        if (!_byId.TryAdd(user.UserId, user)) return false;
        if (!_byUsername.TryAdd(user.Username, user.UserId))
        {
            _byId.TryRemove(user.UserId, out _);
            return false;
        }
        _changes?.Notify();
        return true;
    }

    public UserAccount? GetUserById(string userId)
        => _byId.TryGetValue(userId, out var user) ? user : null;

    public UserAccount? GetUserByUsername(string username)
        => _byUsername.TryGetValue(username, out var id) && _byId.TryGetValue(id, out var user) ? user : null;

    public bool UpdateUser(UserAccount user)
    {
        var ok = _byId.ContainsKey(user.UserId);
        if (ok) _changes?.Notify();
        return ok;
    }

    public IReadOnlyList<UserAccount> ListUsers() => _byId.Values.ToList();

    public void ClearAll()
    {
        _byId.Clear();
        _byUsername.Clear();
        _changes?.Notify();
    }
}
