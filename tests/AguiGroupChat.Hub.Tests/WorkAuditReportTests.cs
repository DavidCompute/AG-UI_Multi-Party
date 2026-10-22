using AguiGroupChat.Hub.Models;
using AguiGroupChat.Hub.Storage;
using AguiGroupChat.Web;
using Microsoft.Extensions.Logging.Abstractions;
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
        Assert.Equal("agent_a", r.Items[0].AgentId);
        Assert.Equal("文案推广", r.Items[0].AgentName);
        Assert.Equal("m1", r.Items[0].MessageId);
        Assert.Contains(r.Items, (e) => !e.Ok && e.Text.Contains("curl"));
        Assert.Equal(1, r.ScannedMessages);
        Assert.Equal("scan", r.Source);
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
        Assert.Contains("time,group,agent,agentId,tool,ok,durationMs,text,messageId,topicId", csv);
        Assert.Contains("\"💾 写入 a,\"\"b\"\".txt\"", csv);
        Assert.Contains(",true,3,", csv);
    }

    /// <summary>归档（1.0.315）：写入 → 读取 → 去重 → 保留期清理。归档后查询不再扫消息。</summary>
    public sealed class Archive
    {
        private static (WorkAuditArchive Archive, string Dir) NewArchive()
        {
            var dir = Path.Combine(Path.GetTempPath(), "agui-wa-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            return (new WorkAuditArchive(dir, NullLogger<WorkAuditArchive>.Instance), dir);
        }

        private static WorkAuditEntry Entry(string msgId, long at, string text = "⚡ 执行 whoami", bool ok = true)
            => new("g1", "市场推广", "agent_a", "文案推广", msgId, "main", at, "work_run_command", ok, 5, text);

        [Fact]
        public void RecordThenRead_DedupesRepeatedRows()
        {
            var (archive, dir) = NewArchive();
            try
            {
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                archive.Record(Entry("m1", now));
                archive.Record(Entry("m1", now)); // 回填与实时写入可能重叠
                Assert.Single(archive.Read(now - 1000));
                Assert.Empty(archive.Read(now + 1000));
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [Fact]
        public void Read_SpansMonths()
        {
            var (archive, dir) = NewArchive();
            try
            {
                var older = DateTimeOffset.UtcNow.AddDays(-45).ToUnixTimeMilliseconds();
                var newer = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                archive.Record(Entry("m_old", older));
                archive.Record(Entry("m_new", newer));
                var all = archive.Read(older - 1000);
                Assert.Equal(2, all.Count);
                Assert.Single(archive.Read(newer - 1000));
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [Fact]
        public void Sweep_DeletesOnlyFullyExpiredMonths()
        {
            var (archive, dir) = NewArchive();
            try
            {
                var now = DateTimeOffset.UtcNow;
                archive.Record(Entry("m_now", now.ToUnixTimeMilliseconds()));
                archive.Record(Entry("m_old", now.AddDays(-400).ToUnixTimeMilliseconds()));
                Assert.Equal(2, Directory.GetFiles(dir, "audit-*.jsonl").Length);

                var deleted = archive.Sweep(365);

                Assert.Equal(1, deleted);
                Assert.Single(Directory.GetFiles(dir, "audit-*.jsonl"));
                Assert.Equal(0, archive.Sweep(0)); // 0 = 永久保留
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [Fact]
        public void Collect_PrefersArchiveAndBackfillsWhenEmpty()
        {
            var (archive, dir) = NewArchive();
            try
            {
                var store = NewStore();
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                store.AddMessage(Msg("m1", "agent_a", now, PlanWithTwo));

                // 首次：归档为空 → 扫消息并回填
                var first = WorkAuditReport.Collect(store, null, archive, 7, null, null, 100);
                Assert.Equal("scan", first.Source);
                Assert.Equal(2, first.Items.Count);

                // 第二次：直接读归档，不再扫消息
                var second = WorkAuditReport.Collect(store, null, archive, 7, null, null, 100);
                Assert.Equal("archive", second.Source);
                Assert.Equal(2, second.Items.Count);
                Assert.Equal(0, second.ScannedMessages);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }
    }
}
