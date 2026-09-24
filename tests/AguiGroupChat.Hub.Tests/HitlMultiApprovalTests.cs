using System.Text.Json;
using AguiGroupChat.Agents;
using AguiGroupChat.Hub.Agents;
using AguiGroupChat.Hub.Messaging;
using AguiGroupChat.Hub.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>
/// 一个模型轮次里提出<b>多个</b>需审批的工具调用时的行为。
///
/// <para>
/// 为什么要有这组用例（实测事故）：真实运行里模型一次提了两个 pptx_deck 调用，网关只弹一张卡、
/// 只回了一条 <c>ToolApprovalResponseContent</c>。日志里<b>三次</b> “HTTP 400 reasoning_content must be
/// passed back” 全部紧跟在 “一轮两条审批请求” 的那一步之后，而单审批的恢复从未失败——因此把
/// “一轮多条审批必须一起收下、一起回应” 定为行为契约并用本例钉住：卡片要如实告知条数，
/// 且恢复后运行能正常跑完（不再落成“本轮回复未能生成可展示的正文”的空白兑底）。
/// </para>
///
/// <para>
/// 注：本例不代表能完整复现 DeepSeek 侧的 400（那取决于 MSAGENT/M.E.AI 在恢复时如何重建
/// 带工具调用的 assistant 消息，本地无法模拟）——但它钉住了<b>我们能控制的那一半</b>：
/// 不再产生“无人回应的审批请求”。崩溃后不让用户白跑的那一半由
/// <c>ResumeRunAsync</c> 的“先保产物”分支负责。
/// </para>
/// </summary>
public sealed class HitlMultiApprovalTests
{
    private static AgentGateway NewGateway(HubFixture f)
    {
        var options = new AgentOptions
        {
            Provider = "mock",
            EnableTools = true,
            Agents =
            [
                new AgentDefinition
                {
                    AgentId = "agent_hitl", Nickname = "审批助手", Description = "测试", Instructions = "你是审批助手",
                    TriggerMode = AgentTriggerMode.Mentioned,
                },
            ],
        };
        var catalog = new AgentCatalog(options, NullLoggerFactory.Instance, new ServiceCollection().BuildServiceProvider());
        var services = new ServiceCollection().AddSingleton(f.Hub).BuildServiceProvider();
        return new AgentGateway(catalog, services, options, attachmentStore: null, NullLogger<AgentGateway>.Instance);
    }

    /// <summary>一轮两个审批：批准后必须两条决议一起回，运行正常继续（不再抛“no matching ToolApprovalResponseContent”）。</summary>
    [Fact]
    public async Task Gateway_TwoApprovalsInOneTurn_AreBothAnswered_OnResume()
    {
        var f = new HubFixture();
        var group = await f.Hub.CreateGroupAsync(new GroupCreateRequest
        {
            GroupName = "g",
            OwnerId = "user_1",
            MemberIds = ["agent_hitl"],
            Members = [new MemberSeed { MemberId = "agent_hitl", MemberType = MemberType.Agent, Nickname = "审批助手" }],
        });
        var (conn, inbox) = f.NewConnection("user_1");
        await f.Hub.SubscribeAsync(conn, [group.GroupId]);
        f.Drain(inbox);
        var gateway = NewGateway(f);

        // 两次提及「公告」→ mock 在同一轮次里提出两个需审批的调用
        var result = await gateway.InvokeAsync(new AgentInvocationContext(
            GroupId: group.GroupId,
            ThreadId: "thread_" + group.GroupId,
            AgentId: "agent_hitl",
            AgentNickname: "审批助手",
            TriggerMessageId: "msg_trigger",
            TriggerUserId: "user_1",
            Content: "帮我发布公告：放假通知；再发布公告：值班安排",
            Mentions: [],
            MentionAll: false), CancellationToken.None);

        Assert.False(result.Accepted);
        Assert.Equal("AGENT_AWAITING_INTERACTION", result.ErrorCode);

        // 交互卡要如实说明本轮有 2 项（否则用户以为只需确认一个）
        JsonElement requestEvent = default;
        var events = f.Drain(inbox);
        foreach (var e in events)
        {
            var j = HubFixture.Parse(e);
            if (j.GetProperty("type").GetString() == EventTypes.AgentInteractionRequest) { requestEvent = j; break; }
        }
        Assert.NotEqual(default, requestEvent);
        var cardMessage = requestEvent.GetProperty("message").GetString() ?? "";
        Assert.Contains("2 项", cardMessage);
        var interruptId = requestEvent.GetProperty("interruptId").GetString()!;

        // 批准一次 → 两条决议都应回灌；漏一条就会在恢复里抛错，这里的“已批准”文本就出不来
        Assert.True(await gateway.ResolveInteractionAsync(interruptId, "user_1", true, null, null, CancellationToken.None));

        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        var texts = new List<string?>();
        while (DateTimeOffset.UtcNow < deadline)
        {
            foreach (var e in f.Drain(inbox))
            {
                var j = HubFixture.Parse(e);
                var type = j.GetProperty("type").GetString();
                if (type == EventTypes.TextMessageContent) texts.Add(j.GetProperty("delta").GetString());
                if (type == EventTypes.TextMessageEnd) { deadline = DateTimeOffset.MinValue; break; }
            }
            if (deadline == DateTimeOffset.MinValue) break;
            await Task.Delay(100);
        }

        var joined = string.Join("", texts);
        Assert.Contains("已批准", joined);
        Assert.DoesNotContain("未能生成可展示的正文", joined);
    }
}
