namespace AguiGroupChat.Hub.Agents;

/// <summary>
/// 用户分组目录的只读查询（细粒度授权）：实现见 AguiGroupChat.Agents（包装 UserGroupStore），
/// 与 <see cref="IAgentDefinitionStore"/> 相同，接口定义在 Hub、实现在 Agents，避免 Hub 反向依赖 Agents 目录。
/// </summary>
public interface IUserGroupService
{
    /// <summary>判某账号是否命中给定任一用户组 id；groupId 为空/未知返回 false。</summary>
    bool UserInAnyGroup(string userId, System.Collections.Generic.IReadOnlyList<string>? groupIds);

    /// <summary>某账号所属的全部用户组 id。</summary>
    System.Collections.Generic.IReadOnlyList<string> GroupsOfUser(string userId);
}
