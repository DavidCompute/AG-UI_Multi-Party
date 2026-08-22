namespace AguiGroupChat.Hub.Users;

/// <summary>
/// 用户账号存储抽象。默认实现为进程内内存存储（<see cref="InMemoryUserStore"/>）；
/// 多实例 / 持久化场景可替换为 Redis、数据库等实现，与 <c>IGroupStore</c> 同理。
/// </summary>
public interface IUserStore
{
    bool AddUser(UserAccount user);
    UserAccount? GetUserById(string userId);
    UserAccount? GetUserByUsername(string username);
    bool UpdateUser(UserAccount user);
    IReadOnlyList<UserAccount> ListUsers();

    /// <summary>清空全部账号（系统初始化用）。</summary>
    void ClearAll();
}
