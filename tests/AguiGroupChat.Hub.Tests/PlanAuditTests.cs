using AguiGroupChat.Hub.Messaging;
using AguiGroupChat.Hub.Models;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>
/// 工作区操作审计写入执行计划卡（1.0.311；1.0.312 起单独成组 `audit`）：
/// 首次追加自动建卡，再次追加累积，条数超限截断，全部随 <c>planJson</c> 落库（刷新后仍可回显）；
/// 不参与步骤进度（`steps` 保持为空）。
/// </summary>
public sealed class PlanAuditTests
{
    private static PlanAuditInfo Item(string tool, string text, bool ok = true) =>
        new() { Tool = tool, Text = text, Ok = ok, DurationMs = 12 };

    [Fact]
    public async Task AppendMessagePlanAudit_CreatesThenExtendsPersistedAudit()
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

        await f.Hub.AppendMessagePlanAuditAsync(group.GroupId, started.MessageId,
            [Item("work_run_command", "⚡ 执行 whoami · 退出码 0")]);

        var first = HubFixture.Parse(f.Drain(inbox).Single());
        Assert.Equal(EventTypes.TextMessagePlan, first.GetProperty("type").GetString());
        Assert.Equal(started.MessageId, first.GetProperty("messageId").GetString());
        Assert.Equal(1, first.GetProperty("audit").GetArrayLength());
        Assert.Equal(0, first.GetProperty("steps").GetArrayLength()); // 审计不参与步骤进度

        await f.Hub.AppendMessagePlanAuditAsync(group.GroupId, started.MessageId,
            [Item("work_write_file", "💾 写入 notes/a.txt（12 字节）"), Item("work_run_command", "⚠ 执行 curl · 不在白名单", ok: false)]);

        var stored = f.Store.GetMessage(group.GroupId, started.MessageId)!;
        Assert.NotNull(stored.PlanJson);
        using var doc = System.Text.Json.JsonDocument.Parse(stored.PlanJson!);
        var root = doc.RootElement;
        Assert.Equal("执行计划", root.GetProperty("title").GetString());
        var audit = root.GetProperty("audit");
        Assert.Equal(3, audit.GetArrayLength());
        Assert.Equal("work_run_command", audit[0].GetProperty("tool").GetString());
        Assert.True(audit[0].GetProperty("ok").GetBoolean());
        Assert.False(audit[2].GetProperty("ok").GetBoolean());
        Assert.Contains("notes/a.txt", audit[1].GetProperty("text").GetString());
    }

    [Fact]
    public async Task AppendMessagePlanAudit_PreservesExistingSteps()
    {
        // 编排阶段已广播过步骤：追加审计不能把步骤冲掉，也不能改它们的编号
        var f = new HubFixture();
        var group = await HubFixture.CreateGroupAsync(f.Hub, "g", "user_1", "agent_a");
        var started = await f.Hub.PublishAgentMessageStartAsync(new AgentMessageStartInput
        {
            GroupId = group.GroupId,
            AgentId = "agent_a",
            ReplyToMessageId = null,
        });

        await f.Hub.BroadcastMessagePlanAsync(group.GroupId, started.MessageId, "执行计划",
        [
            new PlanStepInfo { Id = 1, Text = "为「文案」分配工作", Done = true },
            new PlanStepInfo { Id = 2, Text = "综合各步结果并给出最终答复", Done = false },
        ]);
        await f.Hub.AppendMessagePlanAuditAsync(group.GroupId, started.MessageId,
            [Item("work_list_dir", "📁 列出目录 .（3 项）")]);

        using var doc = System.Text.Json.JsonDocument.Parse(f.Store.GetMessage(group.GroupId, started.MessageId)!.PlanJson!);
        Assert.Equal(2, doc.RootElement.GetProperty("steps").GetArrayLength());
        Assert.Equal(1, doc.RootElement.GetProperty("steps")[0].GetProperty("id").GetInt32());
        Assert.Equal(1, doc.RootElement.GetProperty("audit").GetArrayLength());
    }

    [Fact]
    public async Task AppendMessagePlanAudit_AfterStreamEnded_IsSilentlySkipped()
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

        await f.Hub.AppendMessagePlanAuditAsync(group.GroupId, started.MessageId,
            [Item("work_run_command", "⚡ 执行 whoami")]);

        Assert.Null(f.Store.GetMessage(group.GroupId, started.MessageId)!.PlanJson);
    }
}
