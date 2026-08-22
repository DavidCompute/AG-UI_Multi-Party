using System.Text.Json;
using AguiGroupChat.Hub.Infra;
using AguiGroupChat.Hub.Messaging;
using AguiGroupChat.Hub.Models;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>细粒度 RBAC（4.2）测试：频道级权限（谁可触发智能体 / 谁可审批）的模型与群内更新。</summary>
public sealed class RbacTests
{
    // ================= 模型层：GroupMemberPermissions 与 Extra["rbac"] round-trip =================

    [Fact]
    public void Member_Permissions_DefaultAllAllowed()
    {
        var member = new GroupMember { MemberId = "u", MemberType = MemberType.User, Nickname = "u", Role = GroupRole.Normal, JoinTime = 1 };
        Assert.True(member.CanInvokeAgents);
        Assert.True(member.CanApproveInteractions);
    }

    [Fact]
    public void Member_Permissions_ExplicitFalse_DisablesCapability()
    {
        var member = new GroupMember { MemberId = "u", MemberType = MemberType.User, Nickname = "u", Role = GroupRole.Normal, JoinTime = 1 };
        member.RbacPermissions = new GroupMemberPermissions { CanInvokeAgents = false };
        Assert.False(member.CanInvokeAgents);
        Assert.True(member.CanApproveInteractions); // 未显式设置 → 默认允许
    }

    [Fact]
    public void Member_Permissions_RoundTripThroughExtra_AsJsonElement()
    {
        var member = new GroupMember { MemberId = "u", MemberType = MemberType.User, Nickname = "u", Role = GroupRole.Normal, JoinTime = 1 };
        member.RbacPermissions = new GroupMemberPermissions { CanApprove = false };

        // 落库把 Extra 序列化为 JSON 字符串，读取端反序列化为 Extra（值为 JsonElement）
        var json = JsonSerializer.Serialize(member.Extra);
        var extra = JsonSerializer.Deserialize<Dictionary<string, object?>>(json);
        var reloaded = new GroupMember
        {
            MemberId = "u", MemberType = MemberType.User, Nickname = "u", Role = GroupRole.Normal, JoinTime = 1,
            Extra = extra,
        };
        Assert.False(reloaded.CanApproveInteractions);
        Assert.True(reloaded.CanInvokeAgents); // canInvokeAgents 未显式设置 → 默认允许
    }

    // ================= 群内更新：仅群主 / 管理员可设置成员细粒度权限 =================

    private static JsonElement PermissionsJson(bool? invoke, bool? approve)
        => JsonSerializer.SerializeToElement(new GroupMemberPermissions { CanInvokeAgents = invoke, CanApprove = approve });

    [Fact]
    public async Task UpdatePermissions_Admin_CanSet()
    {
        var f = new HubFixture();
        var group = await HubFixture.CreateGroupAsync(f.Hub, "g", "user_1", "user_2", "user_3");

        await f.Hub.UpdateMemberAsync(new GroupMemberUpdateRequest
        {
            GroupId = group.GroupId,
            MemberId = "user_2",
            OperatorId = "user_1", // 群主
            UpdateFields = ["permissions"],
            MemberInfo = new() { ["permissions"] = PermissionsJson(invoke: false, approve: null) },
        });

        var updated = f.Store.GetMember(group.GroupId, "user_2")!;
        Assert.False(updated.CanInvokeAgents);
        Assert.True(updated.CanApproveInteractions);
    }

    [Fact]
    public async Task UpdatePermissions_NormalMember_Denied()
    {
        var f = new HubFixture();
        var group = await HubFixture.CreateGroupAsync(f.Hub, "g", "user_1", "user_2", "user_3");

        await Assert.ThrowsAsync<AguiProtocolException>(() => f.Hub.UpdateMemberAsync(new GroupMemberUpdateRequest
        {
            GroupId = group.GroupId,
            MemberId = "user_3",
            OperatorId = "user_2", // 普通成员
            UpdateFields = ["permissions"],
            MemberInfo = new() { ["permissions"] = PermissionsJson(invoke: false, approve: false) },
        }));
    }
}
