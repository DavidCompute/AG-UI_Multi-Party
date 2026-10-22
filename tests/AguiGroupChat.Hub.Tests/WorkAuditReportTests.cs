using AguiGroupChat.Hub.Models;
using AguiGroupChat.Hub.Storage;
using AguiGroupChat.Web;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>
/// 工作区操作审计汇总（1.0.314）：从 planJson.audit 抽出条目、按时间 / 员工 / 关键词筛选，
/// 并能导出 CSV。审计不参与计划步骤，这里也不依赖 steps。
/// </summary>
public sealed class WorkAuditReportTests
{
    private static InMemoryGroupStore NewStore()
    {
        var store = new InMemoryGroupStore(200);
        store.AddGroup(new Group
        {
            GroupId = "g1",
            GroupName = "市场推广",
            OwnerId = "user_1",
            CreateTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });
        return store;
    }

    private static GroupMessage Msg(string id, string agentId, long at, string? planJson, string topic = "main")
        => new()
        {
            MessageId = id,
            GroupId = "g1",
            ThreadId = "g1",
            SenderId = agentId,
            SenderType = MemberType.Agent,
            SenderNickname = agentId,
            Content = "reply",
            Timestamp = at,
            TopicId = topic,
            PlanJson = planJson,
        };

    private const string PlanWithTwo = """
        {"title":"执行计划","steps":[],"audit":[
          {"tool":"work_run_command","text":"⚡ 执行 whoami · 退出码 0","ok":true,"durationMs":12},
          {"tool":"work_run_command","text":"⚠ 执行 curl · 不在白名单","ok":false,"durationMs":1}
        ]}
        """;

    [Fact]
    public void Collect_ExtractsAuditRows()
    {
        var store = NewStore();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        store.AddMessage(Msg("m1", "agent_a", now, PlanWithTwo));
        store.AddMessage(Msg("m2", "agent_a", now - 1000, null)); // 无计划：跳过

        var r = WorkAuditReport.Collect(store, _ => "文案推广", 7, null, null, 100);

        Assert.Equal(2, r.Items.Count);
        Assert.Equal("市场推广", r.Items[0].GroupName);
        Assert.Equal("文案推广", r.Items[0].AgentId);
        Assert.Equal("m1", r.Items[0].MessageId);
        Assert.Contains(r.Items, (e) => !e.Ok && e.Text.Contains("curl"));
        Assert.Equal(1, r.ScannedMessages);
    }

    [Fact]
    public void Collect_FiltersByAgentAndKeywordAndDays()
    {
        var store = NewStore();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        store.AddMessage(Msg("m1", "agent_a", now, PlanWithTwo));
        store.AddMessage(Msg("m2", "agent_b", now, PlanWithTwo));
        store.AddMessage(Msg("old", "agent_a", now - 10L * 24 * 3600 * 1000, PlanWithTwo));

        Assert.Equal(2, WorkAuditReport.Collect(store, null, 7, "agent_a", null, 100).Items.Count);   // m1 的两条
        Assert.Equal(4, WorkAuditReport.Collect(store, null, 30, "agent_a", null, 100).Items.Count); // + old 的两条
        Assert.Equal(2, WorkAuditReport.Collect(store, null, 7, null, "curl", 100).Items.Count);     // m1 + m2 各一条
        Assert.Empty(WorkAuditReport.Collect(store, null, 7, "agent_c", null, 100).Items);
    }

    [Fact]
    public void Collect_RespectsLimitAndMarksTruncated()
    {
        var store = NewStore();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        store.AddMessage(Msg("m1", "agent_a", now, PlanWithTwo));
        var r = WorkAuditReport.Collect(store, null, 7, null, null, 1);
        Assert.Single(r.Items);
        Assert.True(r.Truncated);
    }

    [Fact]
    public void Collect_IgnoresMalformedPlanJson()
    {
        var store = NewStore();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        store.AddMessage(Msg("m1", "agent_a", now, "{not json"));
        store.AddMessage(Msg("m2", "agent_a", now, """{"title":"x","steps":[]}"""));
        Assert.Empty(WorkAuditReport.Collect(store, null, 7, null, null, 100).Items);
    }

    [Fact]
    public void ToCsv_HasBomHeaderAndEscapesQuotes()
    {
        var store = NewStore();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        store.AddMessage(Msg("m1", "agent_a", now, """
            {"audit":[{"tool":"work_write_file","text":"💾 写入 a,\"b\".txt","ok":true,"durationMs":3}]}
            """));

        var csv = WorkAuditReport.ToCsv(WorkAuditReport.Collect(store, null, 7, null, null, 100).Items);

        Assert.StartsWith("\uFEFF", csv);
        Assert.Contains("time,group,agent,tool,ok,durationMs,text,messageId,topicId", csv);
        Assert.Contains("\"💾 写入 a,\"\"b\"\".txt\"", csv);
        Assert.Contains(",true,3,", csv);
    }
}
