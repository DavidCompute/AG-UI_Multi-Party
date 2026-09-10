using AguiGroupChat.Hub.Agents;
using AguiGroupChat.Agents.UserGroups;

namespace AguiGroupChat.Agents;

/// <summary>把 <see cref="UserGroupStore"/> 的只读成员判断暴露给 Hub 层（细粒度授权准入门校验）。</summary>
public sealed class UserGroupService : IUserGroupService
{
    private readonly UserGroupStore _groups;
    public UserGroupService(UserGroupStore groups) => _groups = groups;

    public bool UserInAnyGroup(string userId, IReadOnlyList<string>? groupIds)
        => groupIds is not null && _groups.UserInAny(userId, groupIds);

    public IReadOnlyList<string> GroupsOfUser(string userId)
        => _groups.List().Where(g => g.MemberUserIds?.Contains(userId, StringComparer.Ordinal) ?? false)
            .Select(g => g.GroupId).ToList();
}
