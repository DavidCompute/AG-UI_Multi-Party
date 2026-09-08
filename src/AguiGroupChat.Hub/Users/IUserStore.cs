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

    /// <summary>
    /// 删除用户账号（账号注销 / 管理员彻底删除的数据擦除收尾步骤）。返回是否确实删除；
    /// 默认实现返回 false（未实现该能力的存储 / 测试替身自动兼容，账号删除前置清理需由存储上层完成）。
    /// </summary>
    bool RemoveUser(string userId) => false;

    /// <summary>清空全部账号（系统初始化用）。</summary>
    void ClearAll();
}
