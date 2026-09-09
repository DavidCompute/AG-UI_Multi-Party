using AguiGroupChat.Hub.Models;
using AguiGroupChat.Hub.Options;
using AguiGroupChat.Hub.Storage;
using AguiGroupChat.Hub.Users;
using AguiGroupChat.Web;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>
/// 记忆治理「角色 × 可管理范围」矩阵单元测试：
/// 平台管理员全局治理；知聚群主 / 群管理员整群治理；普通成员仅本人；Operator 无特权。
/// </summary>
public sealed class MemoryGovernanceTests
{
    private static IGroupStore BuildStore()
    {
        var store = new InMemoryGroupStore();
        var owner = "u_owner";
        store.AddGroup(new Group { GroupId = "g1", GroupName = "一", OwnerId = owner, CreateTime = 1 });
        store.AddGroup(new Group { GroupId = "g2", GroupName = "二", OwnerId = "u_o2", CreateTime = 1 });
        AddMember(store, "g1", owner, GroupRole.Owner);
        AddMember(store, "g1", "u_admin", GroupRole.Admin);
        AddMember(store, "g1", "u_mem", GroupRole.Normal);
        AddMember(store, "g1", "u_op", GroupRole.Normal);
        AddMember(store, "g2", "u_o2", GroupRole.Owner);
        return store;
    }

    private static void AddMember(IGroupStore store, string gid, string memberId, GroupRole role)
        => store.AddMember(gid, new GroupMember
        {
            MemberId = memberId,
            MemberType = MemberType.User,
            Nickname = memberId,
            Role = role,
            JoinTime = 1,
        });

    private static UserAccount User(string id, bool isAdmin = false, PlatformRole platform = PlatformRole.User)
        => new()
        {
            UserId = id,
            Username = id,
            Nickname = id,
            PasswordHash = "h",
            PasswordSalt = "s",
            IsAdmin = isAdmin,
            PlatformRole = platform,
            CreatedAt = 1,
            UpdatedAt = 1,
        };

    private static AuthService Auth(params UserAccount[] users)
    {
        var store = new InMemoryUserStore();
        foreach (var u in users) store.AddUser(u);
        return new AuthService(store, new AuthOptions(), TimeProvider.System, NullLogger<AuthService>.Instance);
    }

    private static MessageMemoryItem Item(string messageId, string groupId, string sender)
        => new(messageId, groupId, "main", sender, "user", $"内容 {messageId}", 1, 0, null);

    [Fact]
    public void GroupManager_OwnerAndAdmin_CanManageWholeGroup_MemberCannot()
    {
        var store = BuildStore();
        Assert.True(MemoryGovernance.IsGroupManager(store, "u_owner", "g1"));
        Assert.True(MemoryGovernance.IsGroupManager(store, "u_admin", "g1"));
        Assert.False(MemoryGovernance.IsGroupManager(store, "u_mem", "g1"));
        Assert.False(MemoryGovernance.IsGroupManager(store, "u_owner", "g2")); // 非本人群
    }

    [Fact]
    public void PlatformAdmin_IsGlobalManager_AcrossAllGroups_EvenNonMember()
    {
        var auth = Auth(User("u_admin", isAdmin: true));
        var store = BuildStore();
        Assert.True(MemoryGovernance.IsGlobalManager(auth, "u_admin"));
        // 不在 g2 的成员名单中也可整群治理（平台管理员语义）
        Assert.True(MemoryGovernance.CanManageAllInGroup(store, auth, "u_admin", "g2"));
    }

    [Fact]
    public void Operator_HasNoMemoryPrivileges_WithoutGroupRole()
    {
        var auth = Auth(User("u_op", platform: PlatformRole.Operator));
        var store = BuildStore();
        Assert.False(MemoryGovernance.IsGlobalManager(auth, "u_op")); // 只读运维不提升治理权限
        Assert.False(MemoryGovernance.CanManageAllInGroup(store, auth, "u_op", "g1")); // 普通成员口径
    }

    [Fact]
    public void ItemManage_OwnerAndAdminAndGlobal_CanManageOthers_NormalOnlyOwn()
    {
        var store = BuildStore();
        var auth = Auth(User("u_admin", isAdmin: true));
        var otherItem = Item("m1", "g1", "u_other");
        var ownItem = Item("m2", "g1", "u_mem");

        // 群主 / 群管理员可管理他人记忆
        Assert.True(MemoryGovernance.CanManageItem(store, auth, "u_owner", otherItem));
        Assert.True(MemoryGovernance.CanManageItem(store, auth, "u_admin", otherItem));
        // 平台管理员可跨群管理
        Assert.True(MemoryGovernance.CanManageItem(store, auth, "u_admin", otherItem));
        // 普通成员：他人记忆不可管理、本人记忆可管理
        var authMem = Auth(User("u_mem"));
        Assert.False(MemoryGovernance.CanManageItem(store, authMem, "u_mem", otherItem));
        Assert.True(MemoryGovernance.CanManageItem(store, authMem, "u_mem", ownItem));
    }
}
