using AguiGroupChat.Hub.Messaging;
using AguiGroupChat.Hub.Models;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>
/// 工作区操作审计写入执行计划卡（1.0.311）：首次追加自动建卡，再次追加续编号，
/// 全部随 <c>planJson</c> 落库（刷新后仍可回显）。
/// </summary>
public sealed class PlanAuditTests
{
    [Fact]
    public async Task AppendMessagePlanSteps_CreatesThenExtendsPersistedPlan()
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

        await f.Hub.AppendMessagePlanStepsAsync(group.GroupId, started.MessageId,
            [new PlanStepInfo { Id = 0, Text = "⚡ 执行 whoami · 退出码 0", Done = true }]);

        var first = HubFixture.Parse(f.Drain(inbox).Single());
        Assert.Equal(EventTypes.TextMessagePlan, first.GetProperty("type").GetString());
        Assert.Equal(started.MessageId, first.GetProperty("messageId").GetString());
        Assert.Equal(1, first.GetProperty("steps").GetArrayLength());

        await f.Hub.AppendMessagePlanStepsAsync(group.GroupId, started.MessageId,
            [new PlanStepInfo { Id = 0, Text = "💾 写入 notes/a.txt（12 字节）", Done = true }]);

        var stored = f.Store.GetMessage(group.GroupId, started.MessageId)!;
        Assert.NotNull(stored.PlanJson);
        using var doc = System.Text.Json.JsonDocument.Parse(stored.PlanJson!);
        var root = doc.RootElement;
        Assert.Equal("执行计划", root.GetProperty("title").GetString());
        var steps = root.GetProperty("steps");
        Assert.Equal(2, steps.GetArrayLength());
        Assert.Equal(1, steps[0].GetProperty("id").GetInt32());
        Assert.Equal(2, steps[1].GetProperty("id").GetInt32());
        Assert.True(steps[1].GetProperty("done").GetBoolean());
        Assert.Contains("notes/a.txt", steps[1].GetProperty("text").GetString());
    }

    [Fact]
    public async Task AppendMessagePlanSteps_AfterStreamEnded_IsSilentlySkipped()
    {
        var f = new HubFixture();
        var group = await HubFixture.CreateGroupAsync(f.Hub, "g", "user_1", "agent_a");
        var started = await f.Hub.PublishAgentMessageStartAsync(new AgentMessageStartInput
        {
            GroupId = group.GroupId,
            AgentId = "agent_a",
            ReplyToMessageId = null,
        });
        await f.Hub.EndAgentMessageAsync(group.GroupId, started.MessageId);

        await f.Hub.AppendMessagePlanStepsAsync(group.GroupId, started.MessageId,
            [new PlanStepInfo { Id = 0, Text = "⚡ 执行 whoami", Done = true }]);

        Assert.Null(f.Store.GetMessage(group.GroupId, started.MessageId)!.PlanJson);
    }
}
