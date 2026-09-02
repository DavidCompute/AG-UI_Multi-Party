using AguiGroupChat.Hub.Infra;
using AguiGroupChat.Hub.Models;
using AguiGroupChat.Agents;
using System.Text.Json;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>客服知聚（support circle）：创建者可拉客服团队，其它用户可见并可进入，但非客服只能看到自己的会话。</summary>
public sealed class SupportCircleTests
{
    private async Task<Group> CreateSupportCircleAsync(HubFixture f, string owner = "user_1", params string[] team)
        => await f.Hub.CreateGroupAsync(new GroupCreateRequest
        {
            GroupName = "客服知聚",
            OwnerId = owner,
            Kind = GroupKind.Support,
            MemberIds = team,
        });

    [Fact]
    public async Task Typing_SupportCircle_CustomerAndStaffSeeEachOther_ButNotOtherCustomers()
    {
        var f = new HubFixture();
        var g = await CreateSupportCircleAsync(f, "user_staff", "agent_support");
        await f.Hub.EnterSupportCircleAsync(g.GroupId, "user_customer");
        await f.Hub.EnterSupportCircleAsync(g.GroupId, "user_customer2");

        var (cStaff, inStaff) = f.NewConnection("user_staff");
        var (cCust, inCust) = f.NewConnection("user_customer");
        var (cCust2, inCust2) = f.NewConnection("user_customer2");
        await f.Hub.SubscribeAsync(cStaff, [g.GroupId]);
        await f.Hub.SubscribeAsync(cCust, [g.GroupId]);
        await f.Hub.SubscribeAsync(cCust2, [g.GroupId]);
        f.Drain(inStaff); f.Drain(inCust); f.Drain(inCust2);

        // 客服输入 → 顾客参与者应收到（之前被排除，看不到“客服正在输入”），且其它顾客也看到（客服对全体顾客可见）
        await f.Hub.BroadcastTypingAsync(new GroupTypingRequest { GroupId = g.GroupId, MemberId = "user_staff", IsTyping = true });
        Assert.Contains(f.Drain(inCust), e => IsTypingOf(e, "user_staff"));
        Assert.Contains(f.Drain(inCust2), e => IsTypingOf(e, "user_staff"));

        // 顾客输入 → 客服收到；其它顾客（隔离）不应收到该顾客的 typing
        await f.Hub.BroadcastTypingAsync(new GroupTypingRequest { GroupId = g.GroupId, MemberId = "user_customer", IsTyping = true });
        Assert.Contains(f.Drain(inStaff), e => IsTypingOf(e, "user_customer"));
        Assert.DoesNotContain(f.Drain(inCust2), e => IsTypingOf(e, "user_customer"));
    }

    private static bool IsTypingOf(string json, string memberId)
    {
        if (HubFixture.TypeOf(json) != EventTypes.GroupTyping) return false;
        var evt = JsonSerializer.Deserialize<JsonElement>(json);
        return evt.GetProperty("memberId").GetString() == memberId;
    }

    [Fact]
    public void AgentContext_SupportCircle_IncludesCustomerSession_ButNotOthers()
    {
        // 客服知聚：智能体上下文应包含“本次顾客自己的提问 + 定向回给该顾客的客服消息”，
        // 但绝不混入其它顾客的私聊（隔离）。
        GroupMessage Make(string id, string sender, string content, string? visibleTo)
            => new()
            {
                MessageId = id, GroupId = "g", ThreadId = "g", SenderId = sender,
                SenderType = MemberType.User, SenderNickname = sender, Content = content,
                Timestamp = 1,
                Visibility = visibleTo is null ? MessageVisibility.All : MessageVisibility.Private,
                VisibleMemberIds = visibleTo is null ? [] : [visibleTo],
            };
        var custMsg = Make("m1", "custA", "我的电脑开不了机", "custA");            // 顾客自己的提问
        var staffReply = Make("m2", "agent", "帮您检查一下", "custA");           // 定向回给该顾客的客服消息
        var otherCust = Make("m3", "custB", "我的打印机坏了", "custB");         // 其它顾客的私聊

        Assert.True(AgentGateway.IsVisibleForAgentContext(custMsg, "custA", supportCircle: true));   // 顾客自己的提问
        Assert.True(AgentGateway.IsVisibleForAgentContext(staffReply, "custA", supportCircle: true)); // 定向回给该顾客的客服消息
        Assert.False(AgentGateway.IsVisibleForAgentContext(otherCust, "custA", supportCircle: true)); // 其它顾客的私聊不进上下文

        // 普通知聚仍只注入全群可见消息
        var privateMsg = Make("m4", "x", "私聊", "y");
        var publicMsg = Make("m5", "x", "公开", null);
        Assert.False(AgentGateway.IsVisibleForAgentContext(privateMsg, "x", supportCircle: false));
        Assert.True(AgentGateway.IsVisibleForAgentContext(publicMsg, "x", supportCircle: false));
    }

    [Fact]
    public async Task Create_SupportCircle_MarksInvitedTeamAsStaffAndNotPrivate()
    {
        var f = new HubFixture();
        var g = await CreateSupportCircleAsync(f, "user_1", "user_2", "agent_support");

        Assert.True(g.IsSupportCircle);
        Assert.False(g.IsPrivate); // 客服知聚必须对所有用户可见
        // 创建者 = Owner，拉入的团队成员 = Admin（客服）
        Assert.Equal(GroupRole.Owner, f.Store.GetMember(g.GroupId, "user_1")!.Role);
        Assert.Equal(GroupRole.Admin, f.Store.GetMember(g.GroupId, "user_2")!.Role);
        Assert.Equal(GroupRole.Admin, f.Store.GetMember(g.GroupId, "agent_support")!.Role);
        Assert.Equal(MemberType.Agent, f.Store.GetMember(g.GroupId, "agent_support")!.MemberType);
    }

    [Fact]
    public async Task Enter_SupportCircle_RegistersCustomerAsParticipant_NotMember()
    {
        var f = new HubFixture();
        var g = await CreateSupportCircleAsync(f, "user_1", "user_staff");

        var returned = await f.Hub.EnterSupportCircleAsync(g.GroupId, "user_customer");

        // 顾客不是群成员（成员表仅客服团队），成员数不因顾客进入而变化
        Assert.False(f.Store.IsMember(g.GroupId, "user_customer"));
        Assert.Equal(2, f.Store.MemberCount(g.GroupId)); // 创建者 + 客服
        // 但已成为客服知聚参与者，可访问 / 聊天
        Assert.True(f.Hub.IsSupportCustomer(g.GroupId, "user_customer"));
        Assert.True(f.Hub.CanParticipate(g.GroupId, "user_customer"));
        Assert.Equal(g.GroupId, returned.GroupId);
    }

    [Fact]
    public async Task Enter_NormalGroup_Throws()
    {
        var f = new HubFixture();
        var g = await HubFixture.CreateGroupAsync(f.Hub, "普通群", "user_1");
        var ex = await Assert.ThrowsAsync<AguiProtocolException>(() => f.Hub.EnterSupportCircleAsync(g.GroupId, "user_2"));
        Assert.Equal(ErrorCodes.GroupPermissionDenied, ex.ErrorCode);
    }

    [Fact]
    public async Task CustomerMessage_IsScopedToSelf_StaffSeesAll_OtherCustomerDoesNot()
    {
        var f = new HubFixture();
        var g = await CreateSupportCircleAsync(f, "user_staff1"); // 客服
        await f.Hub.EnterSupportCircleAsync(g.GroupId, "customer_a");
        await f.Hub.EnterSupportCircleAsync(g.GroupId, "customer_b");

        // 顾客 A 发的消息 → 只能自己和客服看到
        var msg = await f.Hub.SendMessageAsync(new GroupMessageSendRequest
        {
            GroupId = g.GroupId,
            UserId = "customer_a",
            Content = "我的主机是坏的",
        });

        Assert.Equal(MessageVisibility.Private, msg.Visibility);
        Assert.Equal(["customer_a"], msg.VisibleMemberIds);

        // 客服可见（客服视野 = 全部）
        Assert.True(f.Hub.CanSeeMessageAware(msg, "user_staff1"));
        // 顾客 A 可见（自己的会话）
        Assert.True(f.Hub.CanSeeMessageAware(msg, "customer_a"));
        // 顾客 B 不可见（其它顾客的会话）
        Assert.False(f.Hub.CanSeeMessageAware(msg, "customer_b"));
        // 非成员不可见
        Assert.False(f.Hub.CanSeeMessageAware(msg, "outsider"));
    }

    [Fact]
    public async Task StaffReply_IsScopedToTheRepliedCustomer()
    {
        var f = new HubFixture();
        var g = await CreateSupportCircleAsync(f, "user_staff1");
        await f.Hub.EnterSupportCircleAsync(g.GroupId, "customer_a");

        var customerMsg = await f.Hub.SendMessageAsync(new GroupMessageSendRequest
        {
            GroupId = g.GroupId,
            UserId = "customer_a",
            Content = "屏幕坏了",
        });

        // 客服回复该顾客 → 定向到该顾客
        var reply = await f.Hub.SendMessageAsync(new GroupMessageSendRequest
        {
            GroupId = g.GroupId,
            UserId = "user_staff1",
            Content = "请发一张照片",
            ReplyToMessageId = customerMsg.MessageId,
        });

        Assert.Equal(MessageVisibility.Private, reply.Visibility);
        Assert.Equal(["customer_a"], reply.VisibleMemberIds);
        Assert.True(f.Hub.CanSeeMessageAware(reply, "customer_a"));
        Assert.False(f.Hub.CanSeeMessageAware(reply, "outsider"));
    }

    [Fact]
    public async Task StaffGeneralMessage_IsStaffOnly_NotVisibleToCustomers()
    {
        var f = new HubFixture();
        var g = await CreateSupportCircleAsync(f, "user_staff1", "user_staff2");
        await f.Hub.EnterSupportCircleAsync(g.GroupId, "customer_a");

        // 客服内部沟通（非回复某顾客）→ 仅客服可见，顾客看不到
        var internalMsg = await f.Hub.SendMessageAsync(new GroupMessageSendRequest
        {
            GroupId = g.GroupId,
            UserId = "user_staff1",
            Content = "今晚排班调整",
        });

        Assert.Equal(MessageVisibility.Private, internalMsg.Visibility);
        Assert.True(f.Hub.CanSeeMessageAware(internalMsg, "user_staff1"));
        Assert.True(f.Hub.CanSeeMessageAware(internalMsg, "user_staff2"));
        Assert.False(f.Hub.CanSeeMessageAware(internalMsg, "customer_a"));
    }

    [Fact]
    public async Task Snapshot_ScopesCustomerToOwnConversationOnly()
    {
        var f = new HubFixture();
        var g = await CreateSupportCircleAsync(f, "user_staff1");
        await f.Hub.EnterSupportCircleAsync(g.GroupId, "customer_a");
        await f.Hub.EnterSupportCircleAsync(g.GroupId, "customer_b");

        await f.Hub.SendMessageAsync(new GroupMessageSendRequest { GroupId = g.GroupId, UserId = "customer_a", Content = "A 的问题" });
        await f.Hub.SendMessageAsync(new GroupMessageSendRequest { GroupId = g.GroupId, UserId = "customer_b", Content = "B 的问题" });

        var aSnap = await f.Hub.BuildSnapshotAsync(g.GroupId, "customer_a");
        var bSnap = await f.Hub.BuildSnapshotAsync(g.GroupId, "customer_b");
        var staffSnap = await f.Hub.BuildSnapshotAsync(g.GroupId, "user_staff1");

        // 顾客 A 只能看到自己的会话内容，看不到 B 的
        Assert.Contains(aSnap.LatestMessages, m => m.Content == "A 的问题");
        Assert.DoesNotContain(aSnap.LatestMessages, m => m.Content == "B 的问题");
        // 顾客 B 只能看到自己的
        Assert.Contains(bSnap.LatestMessages, m => m.Content == "B 的问题");
        Assert.DoesNotContain(bSnap.LatestMessages, m => m.Content == "A 的问题");
        // 客服看到全部
        Assert.Contains(staffSnap.LatestMessages, m => m.Content == "A 的问题");
        Assert.Contains(staffSnap.LatestMessages, m => m.Content == "B 的问题");
    }

    [Fact]
    public void Kind_SurvivesStoreRoundTrip_WhenExtraValueIsJsonElement()
    {
        // 客服知聚 kind 存在 Extra["kind"]；数据库存储把 extra 作为 JSON 列读回时，字符串值反序列化
        // 为 JsonElement 而非 CLR string。此前 Group.Kind 只识别 string，导致持久化 reload 后识别为普通知聚
        // （/discover 为空、/enter 报“仅客服知聚可自行进入”）。此处复现并验证已修复。
        var g = new Group
        {
            GroupId = "group_x",
            GroupName = "客服",
            OwnerId = "user_1",
            MemberCount = 1,
            CreateTime = 1,
            IsPrivate = false,
            Extra = AguiJson.Deserialize<Dictionary<string, object?>>("{\"kind\":\"support\"}"), // 值类型 = JsonElement
        };

        Assert.True(g.IsSupportCircle);
        Assert.Equal(GroupKind.Support, g.Kind);
    }
}
