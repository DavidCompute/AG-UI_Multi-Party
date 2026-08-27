using AguiGroupChat.Hub.Agents;
using AguiGroupChat.Hub.Messaging;
using AguiGroupChat.Hub.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>分身触发策略：仅在归属用户离线时启用（用户在线时分身暂停）。</summary>
public sealed class TwinTriggerTests
{
    private sealed class RecordingGateway : IAgentGateway
    {
        public List<string> InvokedAgents { get; } = [];
        public Task<AgentInvocationResult> InvokeAsync(AgentInvocationContext context, CancellationToken ct)
        {
            InvokedAgents.Add(context.AgentId);
            return Task.FromResult(new AgentInvocationResult(true, "run_1", null));
        }

        public Task<bool> IsAvailableAsync(string agentId, CancellationToken ct) => Task.FromResult(true);

        public Task<bool> ResolveInteractionAsync(string interruptId, string memberId, bool approved, string? input, System.Text.Json.JsonElement? payload, CancellationToken ct, bool approveAll = false, string? toolResult = null)
            => Task.FromResult(false);

        public bool StopRun(string runId, string operatorId, string groupId, bool isManager) => false;
    }

    private static async Task<GroupHub> BuildHubAsync(HubFixture f, RecordingGateway gateway)
    {
        var hub = new GroupHub(f.Store, f.Users, f.Connections, f.Agents, f.Triggers, gateway, f.Options,
            TimeProvider.System, NullLogger<GroupHub>.Instance);
        await hub.CreateGroupAsync(new GroupCreateRequest
        {
            GroupName = "g",
            OwnerId = "user_1",
            MemberIds = ["twin_user_1", "agent_a"],
            Members =
            [
                new MemberSeed { MemberId = "twin_user_1", MemberType = MemberType.Agent, Nickname = "「u1」的分身" },
                new MemberSeed { MemberId = "agent_a", MemberType = MemberType.Agent, Nickname = "助手A" },
            ],
        });
        return hub;
    }

    [Fact]
    public async Task Twin_NotTriggered_WhenOwnerOnline_Triggered_WhenOffline()
    {
        var f = new HubFixture();
        var gateway = new RecordingGateway();
        var hub = await BuildHubAsync(f, gateway);

        var group = f.Store.AllGroups().Single();
        f.Agents.Register(new AgentRegisterRequest { AgentId = "twin_user_1", Nickname = "「u1」的分身", GroupIds = [group.GroupId], TriggerMode = AgentTriggerMode.AllMessages });
        f.Agents.Register(new AgentRegisterRequest { AgentId = "agent_a", Nickname = "助手A", GroupIds = [group.GroupId], TriggerMode = AgentTriggerMode.AllMessages });

        // 用户离线（无连接）→ 发消息 → 分身与普通智能体都被触发
        await hub.SendMessageAsync(new GroupMessageSendRequest { GroupId = group.GroupId, UserId = "user_1", Content = "离线时的问题" });
        await Task.Delay(200);
        Assert.Contains("twin_user_1", gateway.InvokedAgents);
        gateway.InvokedAgents.Clear();

        // 用户上线（注册连接）→ 发消息 → 分身暂停（不触发），普通智能体仍触发
        f.NewConnection("user_1");
        await hub.SendMessageAsync(new GroupMessageSendRequest { GroupId = group.GroupId, UserId = "user_1", Content = "在线时的问题" });
        await Task.Delay(200);
        Assert.DoesNotContain("twin_user_1", gateway.InvokedAgents);
        Assert.Contains("agent_a", gateway.InvokedAgents);
    }
}
