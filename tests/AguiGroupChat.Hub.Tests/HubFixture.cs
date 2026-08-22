using System.Text.Json;
using System.Threading.Channels;
using AguiGroupChat.Hub.Agents;
using AguiGroupChat.Hub.Infra;
using AguiGroupChat.Hub.Messaging;
using AguiGroupChat.Hub.Models;
using AguiGroupChat.Hub.Options;
using AguiGroupChat.Hub.Storage;
using AguiGroupChat.Hub.Users;
using Microsoft.Extensions.Logging.Abstractions;

namespace AguiGroupChat.Hub.Tests;

/// <summary>测试夹具：内存存储 + 真实连接管理器 + 测试替身连接。</summary>
public sealed class HubFixture
{
    public GroupChatOptions Options { get; }
    public InMemoryGroupStore Store { get; }
    public InMemoryUserStore Users { get; } = new();
    public ConnectionManager Connections { get; } = new();
    public AgentRegistry Agents { get; } = new();
    public AgentTriggerService Triggers { get; }
    public NoopAgentGateway Gateway { get; }
    public GroupHub Hub { get; }

    public HubFixture(int maxMembers = 50)
    {
        Options = new GroupChatOptions
        {
            MaxGroupMembers = maxMembers,
            MessageHistoryLimit = 200,
            SnapshotMessageCount = 50,
        };
        Store = new InMemoryGroupStore(Options.MessageHistoryLimit);
        Triggers = new AgentTriggerService(Agents);
        Gateway = new NoopAgentGateway(NullLogger<NoopAgentGateway>.Instance);
        Hub = new GroupHub(Store, Users, Connections, Agents, Triggers, Gateway, Options, TimeProvider.System, NullLogger<GroupHub>.Instance);
    }

    /// <summary>注册一个测试替身连接，所有下行事件写入 channel。</summary>
    public (HubConnection Connection, Channel<string> Inbox) NewConnection(string memberId)
    {
        var channel = Channel.CreateUnbounded<string>();
        var connection = new HubConnection
        {
            ConnectionId = Guid.NewGuid().ToString("N")[..16],
            MemberId = memberId,
            Transport = "test",
            Sender = (json, ct) => channel.Writer.WriteAsync(json, ct).AsTask(),
        };
        Connections.Register(connection);
        return (connection, channel);
    }

    /// <summary>取出连接收到的事件（Hub 方法 await 完成后即可安全读取）。</summary>
    public List<string> Drain(Channel<string> inbox)
    {
        var list = new List<string>();
        while (inbox.Reader.TryRead(out var json)) list.Add(json);
        return list;
    }

    public static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    public static string? TypeOf(string json) => Parse(json).GetProperty("type").GetString();

    public static IReadOnlyList<string?> TypesOf(IEnumerable<string> events) => events.Select(TypeOf).ToList();

    public static async Task<Group> CreateGroupAsync(GroupHub hub, string name = "测试群", string owner = "user_1", params string[] members)
        => await hub.CreateGroupAsync(new GroupCreateRequest
        {
            GroupName = name,
            OwnerId = owner,
            MemberIds = members,
        });
}
