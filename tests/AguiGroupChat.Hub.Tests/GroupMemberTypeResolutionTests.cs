using AguiGroupChat.Hub.Agents;
using AguiGroupChat.Hub.Messaging;
using AguiGroupChat.Hub.Models;
using AguiGroupChat.Hub.Options;
using AguiGroupChat.Hub.Storage;
using AguiGroupChat.Hub.Users;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>
/// 成员类型判定：往知聚里加数字员工时，不能只靠 ID 前缀判断“这是不是智能体”。
///
/// <para>
/// 为何要钉住：前缀判断只认 <c>agent_*</c>（前端新建的数字员工），而组织化编排 / 内置组织工具产出的岗位 ID
/// 不带该前缀（<c>bl_commander</c>、<c>org_architect</c> …）。当调用方只传 <c>MemberIds</c> 而没带
/// <c>MemberDetails</c>（<c>/ag-ui/group/member/add</c> 的常见用法）时，这类数字员工会被记成真人成员：
/// 触发规则不注册（消息不唤起它），且它一旦要发言就在发消息时抛「发送者不是智能体成员」——
/// 报错点离成因很远。实测就是往知聚里加 <c>bl_field_validator</c> 后，它的回复直接失败。
/// </para>
/// </summary>
public sealed class GroupMemberTypeResolutionTests
{
    private static GroupHub CreateSut(IAgentDefinitionStore? defs = null)
    {
        var options = new GroupChatOptions
        {
            MaxGroupMembers = 50,
            MessageHistoryLimit = 200,
            SnapshotMessageCount = 50,
        };
        return new GroupHub(
            new InMemoryGroupStore(options.MessageHistoryLimit),
            new InMemoryUserStore(),
            new ConnectionManager(),
            new AgentRegistry(),
            new AgentTriggerService(new AgentRegistry()),
            new RecordingGateway(),
            options,
            TimeProvider.System,
            NullLogger<GroupHub>.Instance,
            agentDefinitions: defs);
    }

    private sealed class StubDefs(params string[] agentIds) : IAgentDefinitionStore
    {
        public AgentDefinitionInfo? GetDefinition(string agentId)
            => agentIds.Contains(agentId, StringComparer.Ordinal)
                ? new AgentDefinitionInfo(agentId, agentId, false, null)
                : null;
    }

    [Fact]
    public async Task AddMember_OrchestratedAgentId_WithoutMemberDetails_IsRecordedAsAgent()
    {
        // 关键：bl_field_validator 不带 agent_ 前缀，目录里能查到 → 必须按智能体成员记
        var hub = CreateSut(new StubDefs("bl_field_validator"));
        var group = await hub.CreateGroupAsync(new GroupCreateRequest { GroupName = "t", OwnerId = "user_1" });

        await hub.AddMembersAsync(new GroupMemberAddRequest
        {
            GroupId = group.GroupId,
            OperatorId = "user_1",
            MemberIds = ["bl_field_validator"],
        });

        Assert.Equal(MemberType.Agent, hub.Store.GetMember(group.GroupId, "bl_field_validator")!.MemberType);
    }

    [Fact]
    public async Task CreateGroup_OrchestratedAgentId_InMemberIds_IsRecordedAsAgent()
    {
        var hub = CreateSut(new StubDefs("bl_commander"));
        var group = await hub.CreateGroupAsync(new GroupCreateRequest
        {
            GroupName = "t",
            OwnerId = "user_1",
            MemberIds = ["bl_commander"],
        });

        Assert.Equal(MemberType.Agent, hub.Store.GetMember(group.GroupId, "bl_commander")!.MemberType);
    }

    [Fact]
    public async Task AddMember_RealUser_StaysUser()
    {
        // 目录里查不到 → 仍是真人成员（不能把普通用户误记成智能体）
        var hub = CreateSut(new StubDefs("bl_field_validator"));
        var group = await hub.CreateGroupAsync(new GroupCreateRequest { GroupName = "t", OwnerId = "user_1" });

        await hub.AddMembersAsync(new GroupMemberAddRequest
        {
            GroupId = group.GroupId,
            OperatorId = "user_1",
            MemberIds = ["user_2"],
        });

        Assert.Equal(MemberType.User, hub.Store.GetMember(group.GroupId, "user_2")!.MemberType);
    }

    [Fact]
    public async Task AddMember_WithoutAgentDefinitionStore_FallsBackToIdPrefix()
    {
        // 未注入智能体目录（如轻量部署 / 部分测试）时保持原行为：agent_ 前缀 → 智能体
        var hub = CreateSut(defs: null);
        var group = await hub.CreateGroupAsync(new GroupCreateRequest { GroupName = "t", OwnerId = "user_1" });

        await hub.AddMembersAsync(new GroupMemberAddRequest
        {
            GroupId = group.GroupId,
            OperatorId = "user_1",
            MemberIds = ["agent_x", "user_3"],
        });

        Assert.Equal(MemberType.Agent, hub.Store.GetMember(group.GroupId, "agent_x")!.MemberType);
        Assert.Equal(MemberType.User, hub.Store.GetMember(group.GroupId, "user_3")!.MemberType);
    }

    [Fact]
    public async Task AddMember_ExplicitMemberDetails_Win_OverCatalog()
    {
        // 显式 MemberDetails 优先（前端即走此路）：目录里查不到也能按声明的类型记
        var hub = CreateSut(defs: null);
        var group = await hub.CreateGroupAsync(new GroupCreateRequest { GroupName = "t", OwnerId = "user_1" });

        await hub.AddMembersAsync(new GroupMemberAddRequest
        {
            GroupId = group.GroupId,
            OperatorId = "user_1",
            MemberIds = ["bl_normalizer"],
            MemberDetails = [new MemberSeed { MemberId = "bl_normalizer", MemberType = MemberType.Agent, Nickname = "数据补全与规范化岗" }],
        });

        var m = hub.Store.GetMember(group.GroupId, "bl_normalizer")!;
        Assert.Equal(MemberType.Agent, m.MemberType);
        Assert.Equal("数据补全与规范化岗", m.Nickname);
    }
}
