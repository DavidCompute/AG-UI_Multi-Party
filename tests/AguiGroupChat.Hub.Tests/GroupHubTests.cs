using System.Text.Json;
using AguiGroupChat.Hub.Infra;
using AguiGroupChat.Hub.Models;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

public sealed class GroupHubTests
{
    // ================= 群组生命周期 =================

    [Fact]
    public async Task CreateGroup_StoresMembersAndDerivesAgentType()
    {
        var f = new HubFixture();
        var group = await HubFixture.CreateGroupAsync(f.Hub, "评审群", "user_1", "user_2", "agent_a");

        Assert.StartsWith("group_", group.GroupId);
        Assert.Equal(3, group.MemberCount);
        Assert.Equal("评审群", group.GroupName);
        Assert.Equal("user_1", group.OwnerId);
        Assert.True(f.Store.IsMember(group.GroupId, "user_1"));
        Assert.Equal(MemberType.Agent, f.Store.GetMember(group.GroupId, "agent_a")!.MemberType);
        Assert.Equal(GroupRole.Owner, f.Store.GetMember(group.GroupId, "user_1")!.Role);
    }

    [Fact]
    public async Task CreateGroup_OverMemberLimit_ThrowsGroupFull()
    {
        var f = new HubFixture(maxMembers: 2);
        var ex = await Assert.ThrowsAsync<AguiProtocolException>(() =>
            HubFixture.CreateGroupAsync(f.Hub, "g", "user_1", "user_2", "user_3"));
        Assert.Equal(ErrorCodes.GroupFull, ex.ErrorCode);
    }

    /// <summary>回归：建群时在线成员（有 WS 连接）应立即显示在线，不能一律 Offline——
    /// 否则新群成员列表用户状态错误、分身互斥逻辑误判（在线用户被隐藏）。</summary>
    [Fact]
    public async Task CreateGroup_InitializesOnlineStatusByConnection()
    {
        var f = new HubFixture();
        f.NewConnection("user_1"); // 在线（已建立 WS 连接）
        f.NewConnection("user_2"); // 在线
        // user_3 无连接（离线）

        var group = await HubFixture.CreateGroupAsync(f.Hub, "g", "user_1", "user_2", "user_3");

        Assert.Equal(OnlineStatus.Online, f.Store.GetMember(group.GroupId, "user_1")!.OnlineStatus);
        Assert.Equal(OnlineStatus.Online, f.Store.GetMember(group.GroupId, "user_2")!.OnlineStatus);
        Assert.Equal(OnlineStatus.Offline, f.Store.GetMember(group.GroupId, "user_3")!.OnlineStatus);
    }

    /// <summary>回归：向已有群加人时同样按实际连接状态初始化在线状态。</summary>
    [Fact]
    public async Task AddMembers_InitializesOnlineStatusByConnection()
    {
        var f = new HubFixture();
        f.NewConnection("user_9"); // 在线
        var group = await HubFixture.CreateGroupAsync(f.Hub, "g", "user_1");

        var added = await f.Hub.AddMembersAsync(new GroupMemberAddRequest
        {
            GroupId = group.GroupId,
            OperatorId = "user_1",
            MemberIds = ["user_9", "user_10"],
        });

        Assert.Equal(OnlineStatus.Online, f.Store.GetMember(group.GroupId, "user_9")!.OnlineStatus);
        Assert.Equal(OnlineStatus.Offline, f.Store.GetMember(group.GroupId, "user_10")!.OnlineStatus);
    }

    [Fact]
    public async Task UpdateGroup_BroadcastsGroupUpdatedToSubscribers()
    {
        var f = new HubFixture();
        var group = await HubFixture.CreateGroupAsync(f.Hub, "旧名", "user_1", "user_2");
        var (conn, inbox) = f.NewConnection("user_1");
        await f.Hub.SubscribeAsync(conn, [group.GroupId]);
        Assert.Equal(2, f.Drain(inbox).Count); // ACK + 快照

        await f.Hub.UpdateGroupAsync(new GroupUpdateRequest
        {
            GroupId = group.GroupId,
            OperatorId = "user_1",
            UpdateFields = ["groupName", "groupAvatar"],
            GroupInfo = new()
            {
                ["groupName"] = JsonSerializer.SerializeToElement("新名"),
                ["groupAvatar"] = JsonSerializer.SerializeToElement("https://x/new.png"),
            },
        });

        var evt = f.Drain(inbox).Select(HubFixture.Parse).Single();
        Assert.Equal(EventTypes.GroupUpdated, evt.GetProperty("type").GetString());
        Assert.Equal("groupName", evt.GetProperty("updateFields")[0].GetString());
        Assert.Equal("新名", evt.GetProperty("groupInfo").GetProperty("groupName").GetString());
        Assert.Equal("user_1", evt.GetProperty("operatorId").GetString());
        Assert.Equal("新名", f.Store.GetGroup(group.GroupId)!.GroupName);
    }

    [Fact]
    public async Task CreateGroup_IsPrivate_PersistedAndInSnapshot()
    {
        var f = new HubFixture();
        var group = await f.Hub.CreateGroupAsync(new GroupCreateRequest
        {
            GroupName = "机密群",
            OwnerId = "user_1",
            IsPrivate = true,
        });

        Assert.True(group.IsPrivate);
        Assert.True(f.Store.GetGroup(group.GroupId)!.IsPrivate);

        var snapshot = await f.Hub.BuildSnapshotAsync(group.GroupId, viewerId: "user_1");
        Assert.True(snapshot.GroupInfo.IsPrivate);
    }

    [Fact]
    public async Task UpdateGroup_IsPrivate_AppliedAndPersisted()
    {
        var f = new HubFixture();
        var group = await HubFixture.CreateGroupAsync(f.Hub, "公开群", "user_1", "user_2");
        Assert.False(group.IsPrivate);

        await f.Hub.UpdateGroupAsync(new GroupUpdateRequest
        {
            GroupId = group.GroupId,
            OperatorId = "user_1",
            UpdateFields = ["isPrivate"],
            GroupInfo = new() { ["isPrivate"] = JsonSerializer.SerializeToElement(true) },
        });

        Assert.True(f.Store.GetGroup(group.GroupId)!.IsPrivate);
    }

    [Fact]
    public async Task UpdateGroup_NonAdmin_ThrowsPermissionDenied()
    {
        var f = new HubFixture();
        var group = await HubFixture.CreateGroupAsync(f.Hub, "g", "user_1", "user_2");
        var ex = await Assert.ThrowsAsync<AguiProtocolException>(() => f.Hub.UpdateGroupAsync(new GroupUpdateRequest
        {
            GroupId = group.GroupId,
            OperatorId = "user_2",
            UpdateFields = ["groupName"],
            GroupInfo = new() { ["groupName"] = JsonSerializer.SerializeToElement("x") },
        }));
        Assert.Equal(ErrorCodes.GroupPermissionDenied, ex.ErrorCode);
    }

    [Fact]
    public async Task Disband_OnlyOwner_ThenGroupBecomesUnavailable()
    {
        var f = new HubFixture();
        var group = await HubFixture.CreateGroupAsync(f.Hub, "g", "user_1", "user_2");
        var (conn, inbox) = f.NewConnection("user_1");
        await f.Hub.SubscribeAsync(conn, [group.GroupId]);
        f.Drain(inbox);

        var denied = await Assert.ThrowsAsync<AguiProtocolException>(() => f.Hub.DisbandGroupAsync(new GroupDisbandRequest
        {
            GroupId = group.GroupId, OperatorId = "user_2",
        }));
        Assert.Equal(ErrorCodes.GroupPermissionDenied, denied.ErrorCode);

        await f.Hub.DisbandGroupAsync(new GroupDisbandRequest { GroupId = group.GroupId, OperatorId = "user_1" });
        Assert.Equal(EventTypes.GroupDisbanded, HubFixture.TypeOf(f.Drain(inbox).Single()));
        Assert.Null(f.Store.GetGroup(group.GroupId));

        var ex = await Assert.ThrowsAsync<AguiProtocolException>(() => f.Hub.SendMessageAsync(new GroupMessageSendRequest
        {
            GroupId = group.GroupId, UserId = "user_1", Content = "x",
        }));
        Assert.Equal(ErrorCodes.GroupNotFound, ex.ErrorCode);
    }

    // ================= 订阅与快照 =================

    [Fact]
    public async Task Subscribe_ReturnsAckAndSnapshot()
    {
        var f = new HubFixture();
        var group = await HubFixture.CreateGroupAsync(f.Hub, "评审群", "user_1", "user_2", "agent_a");
        var (conn, inbox) = f.NewConnection("user_1");

        await f.Hub.SubscribeAsync(conn, [group.GroupId]);

        var events = f.Drain(inbox).Select(HubFixture.Parse).ToList();
        Assert.Equal([EventTypes.GroupSubscribeAck, EventTypes.GroupStateSnapshot], events.Select(e => e.GetProperty("type").GetString()));

        var ack = events[0];
        Assert.Equal(group.GroupId, ack.GetProperty("successGroupIds")[0].GetString());
        Assert.Equal(0, ack.GetProperty("failedGroupIds").GetArrayLength());

        var snapshot = events[1];
        Assert.Equal(group.GroupId, snapshot.GetProperty("groupId").GetString());
        Assert.Equal("评审群", snapshot.GetProperty("groupInfo").GetProperty("groupName").GetString());
        Assert.Equal(3, snapshot.GetProperty("members").GetArrayLength());
        Assert.Equal(0, snapshot.GetProperty("latestMessages").GetArrayLength());
    }

    [Fact]
    public async Task Subscribe_NonMember_FailsWithAck()
    {
        var f = new HubFixture();
        var group = await HubFixture.CreateGroupAsync(f.Hub, "g", "user_1");
        var (conn, inbox) = f.NewConnection("outsider");

        await f.Hub.SubscribeAsync(conn, [group.GroupId]);

        var ack = HubFixture.Parse(f.Drain(inbox).Single());
        Assert.Equal(EventTypes.GroupSubscribeAck, ack.GetProperty("type").GetString());
        Assert.Equal(1, ack.GetProperty("failedGroupIds").GetArrayLength());
        Assert.Equal(group.GroupId, ack.GetProperty("failedGroupIds")[0].GetString());
        Assert.Equal("无群组访问权限或群组不存在", ack.GetProperty("failReason").GetString());
    }

    [Fact]
    public async Task Snapshot_IncludesRecentMessages()
    {
        var f = new HubFixture();
        var group = await HubFixture.CreateGroupAsync(f.Hub, "g", "user_1", "user_2");
        await f.Hub.SendMessageAsync(new GroupMessageSendRequest { GroupId = group.GroupId, UserId = "user_1", Content = "你好" });

        var (conn, inbox) = f.NewConnection("user_1");
        await f.Hub.SubscribeAsync(conn, [group.GroupId]);

        var events = f.Drain(inbox).Select(HubFixture.Parse).ToList();
        Assert.Equal(EventTypes.GroupSubscribeAck, events[0].GetProperty("type").GetString());
        var snapshot = events[1];
        Assert.Equal(EventTypes.GroupStateSnapshot, snapshot.GetProperty("type").GetString());
        var latest = snapshot.GetProperty("latestMessages");
        Assert.Equal(1, latest.GetArrayLength());
        Assert.Equal("你好", latest[0].GetProperty("content").GetString());
        Assert.Equal("user_1", latest[0].GetProperty("senderId").GetString());
    }

    // ================= 消息扇出 =================

    [Fact]
    public async Task SendMessage_FansOutTrioToAllSubscribedMembers()
    {
        var f = new HubFixture();
        var group = await HubFixture.CreateGroupAsync(f.Hub, "g", "user_1", "user_2");
        var (c1, inbox1) = f.NewConnection("user_1");
        var (c2, inbox2) = f.NewConnection("user_2");
        await f.Hub.SubscribeAsync(c1, [group.GroupId]);
        await f.Hub.SubscribeAsync(c2, [group.GroupId]);
        f.Drain(inbox1); f.Drain(inbox2);

        var msg = await f.Hub.SendMessageAsync(new GroupMessageSendRequest
        {
            GroupId = group.GroupId,
            UserId = "user_1",
            Content = "大家看下需求",
            Mentions = ["user_2"],
        });

        var events1 = f.Drain(inbox1).Select(HubFixture.Parse).ToList();
        var events2 = f.Drain(inbox2).Select(HubFixture.Parse).ToList();

        Assert.Equal(
            ["TEXT_MESSAGE_START", "TEXT_MESSAGE_CONTENT", "TEXT_MESSAGE_END"],
            events1.Select(e => e.GetProperty("type").GetString()));
        Assert.Equal(
            ["TEXT_MESSAGE_START", "TEXT_MESSAGE_CONTENT", "TEXT_MESSAGE_END"],
            events2.Select(e => e.GetProperty("type").GetString()));

        Assert.Equal(msg.MessageId, events1[0].GetProperty("messageId").GetString());
        Assert.Equal("大家看下需求", events1[1].GetProperty("delta").GetString());
        Assert.Equal(group.GroupId, events1[2].GetProperty("groupId").GetString());

        // 存储层落库
        var stored = f.Store.GetMessage(group.GroupId, msg.MessageId);
        Assert.NotNull(stored);
        Assert.Equal("thread_" + group.GroupId, msg.ThreadId);
        Assert.Equal("大家看下需求", stored!.Content);
    }

    [Fact]
    public async Task SendMessage_StartEvent_CarriesGroupExtensionFields()
    {
        var f = new HubFixture();
        var group = await HubFixture.CreateGroupAsync(f.Hub, "g", "user_1", "user_2");
        var (conn, inbox) = f.NewConnection("user_1");
        await f.Hub.SubscribeAsync(conn, [group.GroupId]);
        f.Drain(inbox);

        await f.Hub.SendMessageAsync(new GroupMessageSendRequest
        {
            GroupId = group.GroupId,
            UserId = "user_1",
            Content = "内容",
            Mentions = ["user_2"],
            ReplyToMessageId = null,
        });

        var start = HubFixture.Parse(f.Drain(inbox)[0]);
        Assert.Equal("user", start.GetProperty("role").GetString());
        Assert.Equal(group.GroupId, start.GetProperty("groupId").GetString());
        Assert.Equal("user_1", start.GetProperty("senderId").GetString());
        Assert.Equal("user", start.GetProperty("senderType").GetString());
        Assert.Equal("thread_" + group.GroupId, start.GetProperty("threadId").GetString());
        Assert.Equal("user_2", start.GetProperty("mentions")[0].GetString());
        Assert.Equal("all", start.GetProperty("visibility").GetString());
    }

    [Fact]
    public async Task SendMessage_WithAttachments_CarriesThemInStartAndSnapshot()
    {
        var f = new HubFixture();
        var group = await HubFixture.CreateGroupAsync(f.Hub, "g", "user_1", "user_2");
        var (conn, inbox) = f.NewConnection("user_1");
        await f.Hub.SubscribeAsync(conn, [group.GroupId]);
        f.Drain(inbox);

        var att = new AttachmentInfo
        {
            AttachmentId = "att_test",
            Name = "需求.md",
            ContentType = "text/markdown",
            Size = 128,
            Url = "/ag-ui/files/att_test/需求.md",
            Kind = "text",
        };
        var msg = await f.Hub.SendMessageAsync(new GroupMessageSendRequest
        {
            GroupId = group.GroupId,
            UserId = "user_1",
            Content = "看下附件",
            Attachments = [att],
        });

        Assert.Equal(att, Assert.Single(msg.Attachments));

        var start = HubFixture.Parse(f.Drain(inbox)[0]);
        var attachments = start.GetProperty("attachments");
        Assert.Equal(1, attachments.GetArrayLength());
        Assert.Equal("att_test", attachments[0].GetProperty("attachmentId").GetString());
        Assert.Equal("text", attachments[0].GetProperty("kind").GetString());
        Assert.Equal("/ag-ui/files/att_test/需求.md", attachments[0].GetProperty("url").GetString());

        // 快照同样携带附件元信息（历史消息渲染依赖）
        var snapshot = await f.Hub.BuildSnapshotAsync(group.GroupId, viewerId: "user_1");
        var sm = Assert.Single(snapshot.LatestMessages);
        Assert.Equal("att_test", Assert.Single(sm.Attachments).AttachmentId);
    }

    [Fact]
    public async Task SendMessage_AudioAttachment_CarriesAudioKindNotTextExtracted()
    {
        // 富媒体（5.2）：语音消息附件 kind=audio，仅携元数据供前端播放，不进模型文本上下文。
        var f = new HubFixture();
        var group = await HubFixture.CreateGroupAsync(f.Hub, "g", "user_1", "user_2");
        var (conn, inbox) = f.NewConnection("user_1");
        await f.Hub.SubscribeAsync(conn, [group.GroupId]);
        f.Drain(inbox);

        var att = new AttachmentInfo
        {
            AttachmentId = "att_voice",
            Name = "语音-2026.webm",
            ContentType = "audio/webm",
            Size = 4096,
            Url = "/ag-ui/files/att_voice/语音-2026.webm",
            Kind = "audio",
        };
        Assert.False(AguiGroupChat.Hub.Storage.AttachmentStore.IsExtractable(att), "语音附件不应注入模型文本上下文");

        var msg = await f.Hub.SendMessageAsync(new GroupMessageSendRequest
        {
            GroupId = group.GroupId,
            UserId = "user_1",
            Content = "",
            Attachments = [att],
        });
        Assert.Equal("audio", Assert.Single(msg.Attachments).Kind);

        var start = HubFixture.Parse(f.Drain(inbox)[0]);
        Assert.Equal("audio", start.GetProperty("attachments")[0].GetProperty("kind").GetString());
    }

    [Fact]
    public async Task SendMessage_Snapshot_CarriesMentions()
    {
        var f = new HubFixture();
        var group = await HubFixture.CreateGroupAsync(f.Hub, "g", "user_1", "user_2");
        await f.Hub.SendMessageAsync(new GroupMessageSendRequest
        {
            GroupId = group.GroupId,
            UserId = "user_1",
            Content = "大家好",
            Mentions = ["user_2"],
            MentionAll = false,
        });

        var snapshot = await f.Hub.BuildSnapshotAsync(group.GroupId, viewerId: "user_1");
        var sm = Assert.Single(snapshot.LatestMessages);
        Assert.Equal(["user_2"], sm.Mentions);
        Assert.False(sm.MentionAll);

        // @全体场景
        await f.Hub.SendMessageAsync(new GroupMessageSendRequest
        {
            GroupId = group.GroupId,
            UserId = "user_2",
            Content = "通知",
            MentionAll = true,
        });
        var snapshot2 = await f.Hub.BuildSnapshotAsync(group.GroupId, viewerId: "user_1");
        Assert.True(snapshot2.LatestMessages[^1].MentionAll);
        Assert.Empty(snapshot2.LatestMessages[^1].Mentions);
    }

    [Fact]
    public async Task SendMessage_AttachmentOnly_EmptyContentAllowed()
    {
        var f = new HubFixture();
        var group = await HubFixture.CreateGroupAsync(f.Hub, "g", "user_1");

        var msg = await f.Hub.SendMessageAsync(new GroupMessageSendRequest
        {
            GroupId = group.GroupId,
            UserId = "user_1",
            Content = "",
            Attachments =
            [
                new AttachmentInfo
                {
                    AttachmentId = "att_pic",
                    Name = "screenshot.png",
                    ContentType = "image/png",
                    Size = 2048,
                    Url = "/ag-ui/files/att_pic/screenshot.png",
                    Kind = "image",
                },
            ],
        });

        Assert.Equal("att_pic", Assert.Single(msg.Attachments).AttachmentId);
        Assert.Empty(msg.Content);
    }

    [Fact]
    public async Task SendMessage_PrivateVisibility_OnlyVisibleMembersAndSenderReceive()
    {
        var f = new HubFixture();
        var group = await HubFixture.CreateGroupAsync(f.Hub, "g", "user_1", "user_2", "user_3");
        var (c1, inbox1) = f.NewConnection("user_1");
        var (c2, inbox2) = f.NewConnection("user_2");
        var (c3, inbox3) = f.NewConnection("user_3");
        await f.Hub.SubscribeAsync(c1, [group.GroupId]);
        await f.Hub.SubscribeAsync(c2, [group.GroupId]);
        await f.Hub.SubscribeAsync(c3, [group.GroupId]);
        f.Drain(inbox1); f.Drain(inbox2); f.Drain(inbox3);

        await f.Hub.SendMessageAsync(new GroupMessageSendRequest
        {
            GroupId = group.GroupId,
            UserId = "user_1",
            Content = "私密内容",
            Visibility = MessageVisibility.Private,
            VisibleMemberIds = ["user_2"],
        });

        Assert.Equal(3, f.Drain(inbox1).Count); // 发送者回显
        Assert.Equal(3, f.Drain(inbox2).Count);
        Assert.Empty(f.Drain(inbox3));
    }

    [Fact]
    public async Task SendMessage_EchoToSender_DoesNotRequireSubscription()
    {
        // 协议 2.3：发送者恒收到自己的消息（回显），不依赖订阅状态。
        // 回归：断线重连后连接可能失去订阅，若回显依赖订阅索引则发送者连自己消息都看不到。
        var f = new HubFixture();
        var group = await HubFixture.CreateGroupAsync(f.Hub, "g", "user_1", "user_2");
        var (c1, inbox1) = f.NewConnection("user_1"); // user_1 连接但【不订阅】
        var (c2, inbox2) = f.NewConnection("user_2");
        await f.Hub.SubscribeAsync(c2, [group.GroupId]);
        f.Drain(inbox1); f.Drain(inbox2);

        await f.Hub.SendMessageAsync(new GroupMessageSendRequest
        {
            GroupId = group.GroupId,
            UserId = "user_1",
            Content = "回显测试",
        });

        // 未订阅的发送者仍收到完整回显三元组
        Assert.Equal(
            ["TEXT_MESSAGE_START", "TEXT_MESSAGE_CONTENT", "TEXT_MESSAGE_END"],
            HubFixture.TypesOf(f.Drain(inbox1)));
        // 订阅者正常收到（发送者不再重复通过群扇出收到）
        Assert.Equal(3, f.Drain(inbox2).Count);
    }

    [Fact]
    public async Task SendMessage_MentionedVisibility_OnlyMentionedAndSenderReceive()
    {
        var f = new HubFixture();
        var group = await HubFixture.CreateGroupAsync(f.Hub, "g", "user_1", "user_2", "agent_a");
        var (c1, inbox1) = f.NewConnection("user_1");
        var (c2, inbox2) = f.NewConnection("user_2");
        var (cAgent, inboxAgent) = f.NewConnection("agent_a");
        await f.Hub.SubscribeAsync(c1, [group.GroupId]);
        await f.Hub.SubscribeAsync(c2, [group.GroupId]);
        await f.Hub.SubscribeAsync(cAgent, [group.GroupId]);
        f.Drain(inbox1); f.Drain(inbox2); f.Drain(inboxAgent);

        await f.Hub.SendMessageAsync(new GroupMessageSendRequest
        {
            GroupId = group.GroupId,
            UserId = "user_1",
            Content = "@需求助手 帮我看看",
            Visibility = MessageVisibility.Mentioned,
            Mentions = ["agent_a"],
        });

        Assert.Equal(3, f.Drain(inbox1).Count);
        Assert.Equal(3, f.Drain(inboxAgent).Count);
        Assert.Empty(f.Drain(inbox2));
    }

    [Fact]
    public async Task SendMessage_NonMember_ThrowsPermissionDenied()
    {
        var f = new HubFixture();
        var group = await HubFixture.CreateGroupAsync(f.Hub, "g", "user_1");

        var ex = await Assert.ThrowsAsync<AguiProtocolException>(() => f.Hub.SendMessageAsync(new GroupMessageSendRequest
        {
            GroupId = group.GroupId, UserId = "outsider", Content = "x",
        }));
        Assert.Equal(ErrorCodes.GroupPermissionDenied, ex.ErrorCode);
    }

    [Fact]
    public async Task Snapshot_FiltersPrivateMessages_ByViewer()
    {
        // 隐私回归：历史快照必须与实时扇出同样按可见性过滤——非接收者拉取快照看不到私聊消息
        var f = new HubFixture();
        var group = await HubFixture.CreateGroupAsync(f.Hub, "g", "user_1", "user_2", "user_3");
        await f.Hub.SendMessageAsync(new GroupMessageSendRequest
        {
            GroupId = group.GroupId, UserId = "user_1", Content = "私密内容",
            Visibility = MessageVisibility.Private,
            VisibleMemberIds = ["user_2"],
        });

        // 发送者与目标成员可见
        Assert.Equal(1, (await f.Hub.BuildSnapshotAsync(group.GroupId, viewerId: "user_1")).LatestMessages.Count);
        Assert.Equal(1, (await f.Hub.BuildSnapshotAsync(group.GroupId, viewerId: "user_2")).LatestMessages.Count);
        // 非接收者（user_3）看不到
        Assert.Empty((await f.Hub.BuildSnapshotAsync(group.GroupId, viewerId: "user_3")).LatestMessages);
    }

    [Fact]
    public async Task Snapshot_FiltersMentionedMessages_ByViewer()
    {
        var f = new HubFixture();
        var group = await HubFixture.CreateGroupAsync(f.Hub, "g", "user_1", "user_2", "user_3");
        await f.Hub.SendMessageAsync(new GroupMessageSendRequest
        {
            GroupId = group.GroupId, UserId = "user_1", Content = "@user_2 定向内容",
            Visibility = MessageVisibility.Mentioned,
            Mentions = ["user_2"],
        });

        Assert.Equal(1, (await f.Hub.BuildSnapshotAsync(group.GroupId, viewerId: "user_1")).LatestMessages.Count);
        Assert.Equal(1, (await f.Hub.BuildSnapshotAsync(group.GroupId, viewerId: "user_2")).LatestMessages.Count);
        Assert.Empty((await f.Hub.BuildSnapshotAsync(group.GroupId, viewerId: "user_3")).LatestMessages);
    }

    [Fact]
    public async Task Snapshot_AllMessages_VisibleToEveryone()
    {
        var f = new HubFixture();
        var group = await HubFixture.CreateGroupAsync(f.Hub, "g", "user_1", "user_2", "user_3");
        await f.Hub.SendMessageAsync(new GroupMessageSendRequest
        {
            GroupId = group.GroupId, UserId = "user_1", Content = "大家好",
        });

        Assert.Equal(1, (await f.Hub.BuildSnapshotAsync(group.GroupId, viewerId: "user_3")).LatestMessages.Count);
    }

    [Fact]
    public async Task SendMessage_ReplyToMissingMessage_ThrowsMessageNotFound()
    {
        var f = new HubFixture();
        var group = await HubFixture.CreateGroupAsync(f.Hub, "g", "user_1");

        var ex = await Assert.ThrowsAsync<AguiProtocolException>(() => f.Hub.SendMessageAsync(new GroupMessageSendRequest
        {
            GroupId = group.GroupId, UserId = "user_1", Content = "x", ReplyToMessageId = "msg_missing",
        }));
        Assert.Equal(ErrorCodes.GroupMessageNotFound, ex.ErrorCode);
    }

    [Fact]
    public async Task RecallMessage_SenderCanRecall_OthersCannot()
    {
        var f = new HubFixture();
        var group = await HubFixture.CreateGroupAsync(f.Hub, "g", "user_1", "user_2");
        var (c1, inbox1) = f.NewConnection("user_1");
        var (c2, inbox2) = f.NewConnection("user_2");
        await f.Hub.SubscribeAsync(c1, [group.GroupId]);
        await f.Hub.SubscribeAsync(c2, [group.GroupId]);
        f.Drain(inbox1); f.Drain(inbox2);

        var msg = await f.Hub.SendMessageAsync(new GroupMessageSendRequest { GroupId = group.GroupId, UserId = "user_1", Content = "要撤回" });
        f.Drain(inbox1); f.Drain(inbox2);

        // 非发送者撤回 → 权限拒绝
        var denied = await Assert.ThrowsAsync<AguiProtocolException>(() => f.Hub.RecallMessageAsync(new GroupMessageRecallRequest
        {
            GroupId = group.GroupId, MessageId = msg.MessageId, OperatorId = "user_2",
        }));
        Assert.Equal(ErrorCodes.GroupPermissionDenied, denied.ErrorCode);

        await f.Hub.RecallMessageAsync(new GroupMessageRecallRequest
        {
            GroupId = group.GroupId, MessageId = msg.MessageId, OperatorId = "user_1",
        });

        Assert.Equal(EventTypes.GroupMessageRecalled, HubFixture.TypeOf(f.Drain(inbox1).Single()));
        Assert.Equal(EventTypes.GroupMessageRecalled, HubFixture.TypeOf(f.Drain(inbox2).Single()));
        Assert.True(f.Store.GetMessage(group.GroupId, msg.MessageId)!.Recalled);

        // 重复撤回 → 消息不存在
        var again = await Assert.ThrowsAsync<AguiProtocolException>(() => f.Hub.RecallMessageAsync(new GroupMessageRecallRequest
        {
            GroupId = group.GroupId, MessageId = msg.MessageId, OperatorId = "user_1",
        }));
        Assert.Equal(ErrorCodes.GroupMessageNotFound, again.ErrorCode);
    }

    [Fact]
    public async Task BroadcastTyping_ExcludesActor()
    {
        var f = new HubFixture();
        var group = await HubFixture.CreateGroupAsync(f.Hub, "g", "user_1", "user_2");
        var (c1, inbox1) = f.NewConnection("user_1");
        var (c2, inbox2) = f.NewConnection("user_2");
        await f.Hub.SubscribeAsync(c1, [group.GroupId]);
        await f.Hub.SubscribeAsync(c2, [group.GroupId]);
        f.Drain(inbox1); f.Drain(inbox2);

        await f.Hub.BroadcastTypingAsync(new GroupTypingRequest { GroupId = group.GroupId, MemberId = "user_2", IsTyping = true });

        var typing = HubFixture.Parse(f.Drain(inbox1).Single());
        Assert.Equal(EventTypes.GroupTyping, typing.GetProperty("type").GetString());
        Assert.Equal("user_2", typing.GetProperty("memberId").GetString());
        Assert.Equal("user", typing.GetProperty("memberType").GetString());
        Assert.True(typing.GetProperty("isTyping").GetBoolean());
        Assert.Empty(f.Drain(inbox2)); // 发送者自己不收到
    }

    /// <summary>桥接附件回灌：AppendAgentAttachmentsAsync 按 URL 去重合并附件到流式消息并广播 TEXT_MESSAGE_ATTACHMENTS；
    /// 消息结束后不可再追加（流式状态已清理）。</summary>
    [Fact]
    public async Task AppendAgentAttachments_UpdatesMessageAndBroadcasts()
    {
        var f = new HubFixture();
        var group = await HubFixture.CreateGroupAsync(f.Hub, "g", "user_1", "agent_a");
        var (conn, inbox) = f.NewConnection("user_1");
        await f.Hub.SubscribeAsync(conn, [group.GroupId]);
        f.Drain(inbox); // 清掉订阅 ACK / 快照

        var started = await f.Hub.PublishAgentMessageStartAsync(new AgentMessageStartInput
        {
            GroupId = group.GroupId,
            AgentId = "agent_a",
            ReplyToMessageId = null,
        });
        f.Drain(inbox);

        var att = new AttachmentInfo
        {
            AttachmentId = "ext_1", Name = "报告.pdf", ContentType = "application/pdf", Size = 0,
            Url = "https://ext.example.com/r.pdf", Kind = "file",
        };
        // 第二条 URL 相同 → 去重
        await f.Hub.AppendAgentAttachmentsAsync(group.GroupId, started.MessageId,
            [att, new AttachmentInfo { AttachmentId = "ext_2", Name = "报告.pdf", ContentType = "application/pdf", Size = 0, Url = "https://ext.example.com/r.pdf", Kind = "file" }]);

        var stored = f.Store.GetMessage(group.GroupId, started.MessageId)!;
        Assert.Single(stored.Attachments);
        Assert.Equal("报告.pdf", stored.Attachments[0].Name);

        var evt = HubFixture.Parse(f.Drain(inbox).Single());
        Assert.Equal(EventTypes.TextMessageAttachments, evt.GetProperty("type").GetString());
        Assert.Equal(started.MessageId, evt.GetProperty("messageId").GetString());
        Assert.Single(evt.GetProperty("attachments").EnumerateArray());

        // 消息结束后流式状态清理 → 再追加报错
        await f.Hub.EndAgentMessageAsync(group.GroupId, started.MessageId);
        await Assert.ThrowsAsync<AguiProtocolException>(() =>
            f.Hub.AppendAgentAttachmentsAsync(group.GroupId, started.MessageId, [att]));
    }

    /// <summary>任务计划可视化：BroadcastMessagePlanAsync 广播 TEXT_MESSAGE_PLAN（工作型智能体的 PLAN.md 步骤）。</summary>
    [Fact]
    public async Task BroadcastMessagePlan_BroadcastsPlanToMembers()
    {
        var f = new HubFixture();
        var group = await HubFixture.CreateGroupAsync(f.Hub, "g", "user_1", "agent_a");
        var (conn, inbox) = f.NewConnection("user_1");
        await f.Hub.SubscribeAsync(conn, [group.GroupId]);
        f.Drain(inbox);

        var started = await f.Hub.PublishAgentMessageStartAsync(new AgentMessageStartInput
        {
            GroupId = group.GroupId,
            AgentId = "agent_a",
            ReplyToMessageId = null,
        });
        f.Drain(inbox);

        await f.Hub.BroadcastMessagePlanAsync(group.GroupId, started.MessageId, "整理报告",
        [
            new PlanStepInfo { Id = 1, Text = "采集网页", Done = true },
            new PlanStepInfo { Id = 2, Text = "生成报告", Done = false },
        ]);

        var evt = HubFixture.Parse(f.Drain(inbox).Single());
        Assert.Equal(EventTypes.TextMessagePlan, evt.GetProperty("type").GetString());
        Assert.Equal(started.MessageId, evt.GetProperty("messageId").GetString());
        Assert.Equal("整理报告", evt.GetProperty("title").GetString());
        var steps = evt.GetProperty("steps").EnumerateArray().ToList();
        Assert.Equal(2, steps.Count);
        Assert.True(steps[0].GetProperty("done").GetBoolean());
        Assert.False(steps[1].GetProperty("done").GetBoolean());
    }

    /// <summary>思考模式：AppendAgentReasoningAsync 独立于正文累积存储并广播 TEXT_MESSAGE_REASONING 增量；
    /// 结束事件携带完整思考快照；ResetAgentContentAsync 同时清空思考内容。</summary>
    [Fact]
    public async Task AppendAgentReasoning_StoresAndBroadcasts_EndCarriesSnapshot()
    {
        var f = new HubFixture();
        var group = await HubFixture.CreateGroupAsync(f.Hub, "g", "user_1", "agent_a");
        var (conn, inbox) = f.NewConnection("user_1");
        await f.Hub.SubscribeAsync(conn, [group.GroupId]);
        f.Drain(inbox); // 清掉订阅 ACK / 快照

        var started = await f.Hub.PublishAgentMessageStartAsync(new AgentMessageStartInput
        {
            GroupId = group.GroupId,
            AgentId = "agent_a",
            ReplyToMessageId = null,
        });
        f.Drain(inbox);

        // 两条思考增量 + 一条正文增量（互不干扰）
        await f.Hub.AppendAgentReasoningAsync(group.GroupId, started.MessageId, "先分析需求");
        await f.Hub.AppendAgentReasoningAsync(group.GroupId, started.MessageId, "，再给出方案。");
        await f.Hub.AppendAgentContentAsync(group.GroupId, started.MessageId, "最终答复正文");

        var stored = f.Store.GetMessage(group.GroupId, started.MessageId)!;
        Assert.Equal("先分析需求，再给出方案。", stored.Reasoning); // 思考独立累积
        Assert.Equal("最终答复正文", stored.Content); // 正文不受思考影响

        // 广播两条 TEXT_MESSAGE_REASONING 增量（正文增量不影响思考计数）
        var reasonings = f.Drain(inbox).Select(HubFixture.Parse)
            .Where(e => e.GetProperty("type").GetString() == EventTypes.TextMessageReasoning).ToList();
        Assert.Equal(2, reasonings.Count);
        Assert.Equal("先分析需求", reasonings[0].GetProperty("delta").GetString());
        Assert.Equal("，再给出方案。", reasonings[1].GetProperty("delta").GetString());

        // 结束事件携带完整思考快照（供前端回放）
        f.Drain(inbox);
        await f.Hub.EndAgentMessageAsync(group.GroupId, started.MessageId);
        var end = HubFixture.Parse(f.Drain(inbox).Single());
        Assert.Equal(EventTypes.TextMessageEnd, end.GetProperty("type").GetString());
        Assert.Equal("先分析需求，再给出方案。", end.GetProperty("reasoning").GetString());
    }

    /// <summary>思考模式 + 人机交互中断：ResetAgentContentAsync 同时清空思考内容（前端思考块随之消失）。</summary>
    [Fact]
    public async Task ResetAgentContent_ClearsReasoning()
    {
        var f = new HubFixture();
        var group = await HubFixture.CreateGroupAsync(f.Hub, "g", "user_1", "agent_a");
        var started = await f.Hub.PublishAgentMessageStartAsync(new AgentMessageStartInput
        {
            GroupId = group.GroupId,
            AgentId = "agent_a",
            ReplyToMessageId = null,
        });
        await f.Hub.AppendAgentReasoningAsync(group.GroupId, started.MessageId, "思考过程");
        await f.Hub.ResetAgentContentAsync(group.GroupId, started.MessageId);
        var stored = f.Store.GetMessage(group.GroupId, started.MessageId)!;
        Assert.Equal("", stored.Content);
        Assert.Null(stored.Reasoning); // 思考一并清空
        await f.Hub.EndAgentMessageAsync(group.GroupId, started.MessageId);
    }

    // ================= 群成员管理 =================

    [Fact]
    public async Task AddMembers_BroadcastsJoined_AndSendsSnapshotToNewMember()
    {
        var f = new HubFixture();
        var group = await HubFixture.CreateGroupAsync(f.Hub, "g", "user_1");
        var (conn, inbox) = f.NewConnection("user_1");
        await f.Hub.SubscribeAsync(conn, [group.GroupId]);
        f.Drain(inbox);

        var added = await f.Hub.AddMembersAsync(new GroupMemberAddRequest
        {
            GroupId = group.GroupId,
            MemberIds = ["user_2", "agent_a"],
            OperatorId = "user_1",
        });

        Assert.Equal(2, added.Count);
        Assert.Equal(3, f.Store.GetGroup(group.GroupId)!.MemberCount);

        var joined = HubFixture.Parse(f.Drain(inbox).Single());
        Assert.Equal(EventTypes.GroupMemberJoined, joined.GetProperty("type").GetString());
        Assert.Equal(2, joined.GetProperty("members").GetArrayLength());
        Assert.Equal("user_2", joined.GetProperty("members")[0].GetProperty("memberId").GetString());

        // 新成员订阅后收到快照（含自己）
        var (newConn, newInbox) = f.NewConnection("user_2");
        await f.Hub.SubscribeAsync(newConn, [group.GroupId]);
        var events = f.Drain(newInbox).Select(HubFixture.Parse).ToList();
        Assert.Equal(EventTypes.GroupStateSnapshot, events[1].GetProperty("type").GetString());
        Assert.Equal(3, events[1].GetProperty("members").GetArrayLength());
    }

    [Fact]
    public async Task RemoveMembers_KicksAndUnsubscribesTarget()
    {
        var f = new HubFixture();
        var group = await HubFixture.CreateGroupAsync(f.Hub, "g", "user_1", "user_2");
        var (c1, inbox1) = f.NewConnection("user_1");
        var (c2, inbox2) = f.NewConnection("user_2");
        await f.Hub.SubscribeAsync(c1, [group.GroupId]);
        await f.Hub.SubscribeAsync(c2, [group.GroupId]);
        f.Drain(inbox1); f.Drain(inbox2);

        await f.Hub.RemoveMembersAsync(new GroupMemberRemoveRequest
        {
            GroupId = group.GroupId,
            MemberIds = ["user_2"],
            OperatorId = "user_1",
        });

        var left = HubFixture.Parse(f.Drain(inbox2).Single());
        Assert.Equal(EventTypes.GroupMemberLeft, left.GetProperty("type").GetString());
        Assert.Equal("kick", left.GetProperty("leaveType").GetString());
        Assert.Equal("user_1", left.GetProperty("operatorId").GetString());
        Assert.Equal("user_2", left.GetProperty("memberIds")[0].GetString());

        // 被移出成员的订阅已解除：后续消息不再收到
        f.Drain(inbox1);
        await f.Hub.SendMessageAsync(new GroupMessageSendRequest { GroupId = group.GroupId, UserId = "user_1", Content = "你好" });
        Assert.Equal(3, f.Drain(inbox1).Count);
        Assert.Empty(f.Drain(inbox2));
        Assert.False(f.Store.IsMember(group.GroupId, "user_2"));
    }

    [Fact]
    public async Task LeaveGroup_Voluntary_ThenCannotSend()
    {
        var f = new HubFixture();
        var group = await HubFixture.CreateGroupAsync(f.Hub, "g", "user_1", "user_2");
        var (c1, inbox1) = f.NewConnection("user_1");
        var (c2, inbox2) = f.NewConnection("user_2");
        await f.Hub.SubscribeAsync(c1, [group.GroupId]);
        await f.Hub.SubscribeAsync(c2, [group.GroupId]);
        f.Drain(inbox1); f.Drain(inbox2);

        await f.Hub.LeaveGroupAsync(group.GroupId, "user_2");

        var left = HubFixture.Parse(f.Drain(inbox2).Single());
        Assert.Equal("voluntary", left.GetProperty("leaveType").GetString());
        Assert.Equal("user_2", left.GetProperty("operatorId").GetString());

        var ex = await Assert.ThrowsAsync<AguiProtocolException>(() => f.Hub.SendMessageAsync(new GroupMessageSendRequest
        {
            GroupId = group.GroupId, UserId = "user_2", Content = "x",
        }));
        Assert.Equal(ErrorCodes.GroupPermissionDenied, ex.ErrorCode);
    }

    [Fact]
    public async Task LeaveGroup_Owner_ThrowsPermissionDenied()
    {
        var f = new HubFixture();
        var group = await HubFixture.CreateGroupAsync(f.Hub, "g", "user_1");
        var ex = await Assert.ThrowsAsync<AguiProtocolException>(() => f.Hub.LeaveGroupAsync(group.GroupId, "user_1"));
        Assert.Equal(ErrorCodes.GroupPermissionDenied, ex.ErrorCode);
    }

    [Fact]
    public async Task UpdateMember_RoleChange_RequiresAdmin()
    {
        var f = new HubFixture();
        var group = await HubFixture.CreateGroupAsync(f.Hub, "g", "user_1", "user_2", "user_3");
        var (conn, inbox) = f.NewConnection("user_3");
        await f.Hub.SubscribeAsync(conn, [group.GroupId]);
        f.Drain(inbox);

        // 普通成员改他人角色 → 拒绝
        var denied = await Assert.ThrowsAsync<AguiProtocolException>(() => f.Hub.UpdateMemberAsync(new GroupMemberUpdateRequest
        {
            GroupId = group.GroupId,
            MemberId = "user_2",
            OperatorId = "user_3",
            UpdateFields = ["role"],
            MemberInfo = new() { ["role"] = JsonSerializer.SerializeToElement("admin") },
        }));
        Assert.Equal(ErrorCodes.GroupPermissionDenied, denied.ErrorCode);

        // 群主改角色 → 成功并广播（在线状态由服务端维护，不可手动修改）
        await f.Hub.UpdateMemberAsync(new GroupMemberUpdateRequest
        {
            GroupId = group.GroupId,
            MemberId = "user_2",
            OperatorId = "user_1",
            UpdateFields = ["role"],
            MemberInfo = new()
            {
                ["role"] = JsonSerializer.SerializeToElement("admin"),
            },
        });

        Assert.Equal(GroupRole.Admin, f.Store.GetMember(group.GroupId, "user_2")!.Role);
        var evt = HubFixture.Parse(f.Drain(inbox).Single());
        Assert.Equal(EventTypes.GroupMemberUpdated, evt.GetProperty("type").GetString());
        Assert.Equal("admin", evt.GetProperty("memberInfo").GetProperty("role").GetString());
        Assert.Equal("user_1", evt.GetProperty("operatorId").GetString());
    }

    // ================= 在线状态联动 =================

    [Fact]
    public async Task MemberConnect_Disconnect_UpdatesOnlineStatus()
    {
        var f = new HubFixture();
        var group = await HubFixture.CreateGroupAsync(f.Hub, "g", "user_1", "user_2");
        var (observer, inbox) = f.NewConnection("user_1");
        await f.Hub.SubscribeAsync(observer, [group.GroupId]);
        f.Drain(inbox);

        var (conn, _) = f.NewConnection("user_2");
        await f.Hub.OnMemberConnectedAsync("user_2");

        var online = HubFixture.Parse(f.Drain(inbox).Single());
        Assert.Equal(EventTypes.GroupMemberUpdated, online.GetProperty("type").GetString());
        Assert.Equal("online", online.GetProperty("memberInfo").GetProperty("onlineStatus").GetString());

        f.Connections.Unregister(conn.ConnectionId);
        await f.Hub.OnMemberDisconnectedAsync("user_2");

        var offline = HubFixture.Parse(f.Drain(inbox).Single());
        Assert.Equal("offline", offline.GetProperty("memberInfo").GetProperty("onlineStatus").GetString());
        Assert.Equal(OnlineStatus.Offline, f.Store.GetMember(group.GroupId, "user_2")!.OnlineStatus);
    }

    // ================= 工具调用事件扇出（预留接口回灌路径） =================

    [Fact]
    public async Task BroadcastAsync_FansOutAgentEventsToSubscribers()
    {
        var f = new HubFixture();
        var group = await HubFixture.CreateGroupAsync(f.Hub, "g", "user_1", "agent_a");
        var (c1, inbox1) = f.NewConnection("user_1");
        var (cAgent, inboxAgent) = f.NewConnection("agent_a");
        await f.Hub.SubscribeAsync(c1, [group.GroupId]);
        await f.Hub.SubscribeAsync(cAgent, [group.GroupId]);
        f.Drain(inbox1); f.Drain(inboxAgent);

        // 模拟真实 AG-UI 网关回灌的智能体消息三元组
        await f.Hub.BroadcastAsync(group.GroupId, new TextMessageStartEvent
        {
            MessageId = "msg_agent_1",
            Role = "assistant",
            ThreadId = "thread_" + group.GroupId,
            RunId = "run_1",
            GroupId = group.GroupId,
            SenderId = "agent_a",
            SenderType = MemberType.Agent,
            SenderNickname = "需求助手",
            Timestamp = 1,
        });
        await f.Hub.BroadcastAsync(group.GroupId, new TextMessageContentEvent { MessageId = "msg_agent_1", Delta = "收到，我来处理" });
        await f.Hub.BroadcastAsync(group.GroupId, new TextMessageEndEvent { MessageId = "msg_agent_1", GroupId = group.GroupId, Timestamp = 2 });

        Assert.Equal(3, f.Drain(inbox1).Count);
        var start = HubFixture.Parse(f.Drain(inboxAgent)[0]);
        Assert.Equal("assistant", start.GetProperty("role").GetString());
        Assert.Equal("run_1", start.GetProperty("runId").GetString());
        Assert.Equal("agent", start.GetProperty("senderType").GetString());
    }
}
