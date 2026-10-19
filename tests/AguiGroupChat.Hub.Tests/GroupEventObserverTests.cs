using AguiGroupChat.Hub.Messaging;
using AguiGroupChat.Hub.Models;
using AguiGroupChat.Web;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>
/// <see cref="GroupHub"/> 出站事件观察者（Webhook 钩子）测试：发消息 / 建群时观察者收到对应事件；
/// 观察者抛异常不得影响正常扇出（集成是增强，不是主流程依赖）。
/// </summary>
public sealed class GroupEventObserverTests
{
    private sealed class RecordingObserver : IGroupEventObserver
    {
        public List<(string GroupId, string? Type)> Events { get; } = [];
        public List<object> Objects { get; } = [];
        public void OnEvent(string groupId, object evt)
        {
            Events.Add((groupId, WebhookDispatcher.EventTypeOf(evt)));
            Objects.Add(evt);
        }
    }

    private sealed class ThrowingObserver : IGroupEventObserver
    {
        public void OnEvent(string groupId, object evt) => throw new InvalidOperationException("boom");
    }

    private static GroupHub CreateHub(HubFixture f, IGroupEventObserver observer) => new(
        f.Store, f.Users, f.Connections, f.Agents, f.Triggers, f.Gateway, f.Options,
        TimeProvider.System, NullLogger<GroupHub>.Instance, eventObserver: observer);

    [Fact]
    public async Task Observer_ReceivesGroupAndMessageEvents()
    {
        var f = new HubFixture();
        var observer = new RecordingObserver();
        var hub = CreateHub(f, observer);

        var group = await hub.CreateGroupAsync(new GroupCreateRequest { GroupName = "集成群", OwnerId = "user_1" });
        await hub.SendMessageAsync(new GroupMessageSendRequest { GroupId = group.GroupId, UserId = "user_1", Content = "hello" });

        Assert.Contains(observer.Events, e => e.GroupId == group.GroupId && e.Type == EventTypes.GroupCreated);
        Assert.Contains(observer.Events, e => e.GroupId == group.GroupId && e.Type == EventTypes.TextMessageStart);
        Assert.Contains(observer.Events, e => e.GroupId == group.GroupId && e.Type == EventTypes.TextMessageEnd);
    }

    [Fact]
    public async Task Observer_FiresEvenWithNoSubscribedConnections()
    {
        // 无任何已订阅连接时扇出会提前返回，但出站钩子仍应先于该判断触发（外部系统与在线人数无关）
        var f = new HubFixture();
        var observer = new RecordingObserver();
        var hub = CreateHub(f, observer);

        var group = await hub.CreateGroupAsync(new GroupCreateRequest { GroupName = "无人在线", OwnerId = "user_1" });
        await hub.SendMessageAsync(new GroupMessageSendRequest { GroupId = group.GroupId, UserId = "user_1", Content = "hi" });

        Assert.Contains(observer.Events, e => e.Type == EventTypes.TextMessageEnd);
    }

    [Fact]
    public async Task Observer_ReceivesGroupUpdated_CarryingOverrideValues()
    {
        // 成本控制类覆写变更无专用事件：它随 GROUP_UPDATED 下发（updateFields + groupInfo 新值）。
        // 这条护栏防止以后有人把 updateFields / groupInfo 从事件里削掉（外部审计就会静默丢信息）。
        var f = new HubFixture();
        var observer = new RecordingObserver();
        var hub = CreateHub(f, observer);
        var group = await hub.CreateGroupAsync(new GroupCreateRequest { GroupName = "覆写集成", OwnerId = "user_1" });

        await hub.UpdateGroupAsync(new GroupUpdateRequest
        {
            GroupId = group.GroupId,
            OperatorId = "user_1",
            UpdateFields = ["quotaPerGroup", "model"],
            GroupInfo = new()
            {
                ["quotaPerGroup"] = System.Text.Json.JsonSerializer.SerializeToElement(5000),
                ["model"] = System.Text.Json.JsonSerializer.SerializeToElement("deepseek-flash"),
            },
        });

        var evt = Assert.Single(observer.Objects.OfType<GroupUpdatedEvent>());
        Assert.Contains("quotaPerGroup", evt.UpdateFields);
        Assert.Contains("model", evt.UpdateFields);
        Assert.Equal(5000, evt.GroupInfo["quotaPerGroup"].GetInt64());
        Assert.Equal("deepseek-flash", evt.GroupInfo["model"].GetString());
        Assert.Equal("user_1", evt.OperatorId);
    }

    [Fact]
    public async Task ObserverThrowing_DoesNotBreakSend()
    {
        var f = new HubFixture();
        var hub = CreateHub(f, new ThrowingObserver());

        var group = await hub.CreateGroupAsync(new GroupCreateRequest { GroupName = "异常观察者", OwnerId = "user_1" });
        var msg = await hub.SendMessageAsync(new GroupMessageSendRequest { GroupId = group.GroupId, UserId = "user_1", Content = "still ok" });

        Assert.Equal("still ok", msg.Content);
        Assert.Single(f.Store.AllMessages(group.GroupId));
    }
}
