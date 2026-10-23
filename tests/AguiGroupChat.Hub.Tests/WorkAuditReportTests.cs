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
    public void Timeline_BucketsByDayAndPadsMissingDays()
    {
        var store = NewStore();
        var now = DateTimeOffset.UtcNow;
        store.AddMessage(Msg("m1", "agent_a", now.ToUnixTimeMilliseconds(), PlanWithTwo));
        store.AddMessage(Msg("m2", "agent_a", now.AddDays(-2).ToUnixTimeMilliseconds(), """
            {"audit":[{"tool":"work_write_file","text":"💾 a.txt","ok":true,"durationMs":2}]}
            """));
        // 超出窗口：不参与统计
        store.AddMessage(Msg("old", "agent_a", now.AddDays(-30).ToUnixTimeMilliseconds(), PlanWithTwo));

        var items = WorkAuditReport.Collect(store, null, 7, null, null, 100).Items;
        var days = WorkAuditReport.Timeline(items, 7);

        Assert.Equal(7, days.Count);
        var today = days[^1];
        Assert.Equal(1, today.Commands);
        Assert.Equal(1, today.Failed); // 失败单列，不重复计入命令
        Assert.Equal(2, today.Total);
        var twoDaysAgo = days[^3];
        Assert.Equal(1, twoDaysAgo.Writes);
        Assert.Equal(1, twoDaysAgo.Total);
        // 中间的空白日补 0
        Assert.Equal(0, days[^2].Total);
        Assert.True(days.Select((d) => d.Date).SequenceEqual(days.Select((d) => d.Date).OrderBy((d) => d, StringComparer.Ordinal)));
    }

    [Fact]
    public void Timeline_ClampsWindowToOneThroughNinetyDays()
    {
        Assert.Equal(7, WorkAuditReport.Timeline([], 0).Count); // 0 → 默认 7 天
        Assert.Equal(90, WorkAuditReport.Timeline([], 999).Count);
        Assert.Equal(3, WorkAuditReport.Timeline([], 3).Count);
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

    [Fact]
    public void ToGroupedCsv_EmitsOneRowPerMessage()
    {
        var store = NewStore();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        store.AddMessage(Msg("m1", "agent_a", now, PlanWithTwo));

        var csv = WorkAuditReport.ToGroupedCsv(WorkAuditReport.Collect(store, null, 7, null, null, 100).Items);

        Assert.StartsWith("\uFEFF", csv);
        Assert.Contains("time,group,agent,actions,failures,durationMs,firstText,messageId,topicId", csv);
        var lines = csv.TrimEnd('\n').Split('\n');
        Assert.Equal(2, lines.Length);       // 表头 + 1 条消息
        Assert.Contains(",2,1,13,", csv);     // 2 项操作 / 1 项异常 / 12+1 ms
        Assert.Contains(",m1,", csv);
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
        public void Sweep_DeletesOnlyFullyExpiredPeriods()
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
        public void Sweep_UsesDailyGranularity()
        {
            // 1.0.317：同一个月内的旧天数会被删，当天的保留（月度分片做不到这一点）
            var (archive, dir) = NewArchive();
            try
            {
                var now = DateTimeOffset.UtcNow;
                archive.Record(Entry("m_today", now.ToUnixTimeMilliseconds()));
                archive.Record(Entry("m_40d", now.AddDays(-40).ToUnixTimeMilliseconds()));
                archive.Record(Entry("m_35d", now.AddDays(-35).ToUnixTimeMilliseconds()));
                Assert.Equal(3, Directory.GetFiles(dir, "audit-*.jsonl").Length);

                Assert.Equal(2, archive.Sweep(30));
                Assert.Single(Directory.GetFiles(dir, "audit-*.jsonl"));
                Assert.Single(archive.Read(now.AddDays(-1).ToUnixTimeMilliseconds()));
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [Fact]
        public void Stats_ReportsFilesBytesAndOldest()
        {
            var (archive, dir) = NewArchive();
            try
            {
                var now = DateTimeOffset.UtcNow;
                archive.Record(Entry("m_now", now.ToUnixTimeMilliseconds()));
                archive.Record(Entry("m_old", now.AddDays(-40).ToUnixTimeMilliseconds()));

                var stats = archive.Stats();

                Assert.Equal(2, stats.Files);
                Assert.True(stats.Bytes > 0);
                Assert.NotNull(stats.Oldest);
                Assert.NotNull(stats.Newest);
                Assert.True(stats.Newest > stats.Oldest);
                Assert.Equal(2, stats.Entries);
                Assert.Equal(0, stats.Failed);
                Assert.Equal(2, stats.Tools["work_run_command"]);
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
                var first = WorkAuditReport.Collect(store, null, archive, 7, null, null, null, 100);
                Assert.Equal("scan", first.Source);
                Assert.Equal(2, first.Items.Count);

                // 第二次：直接读归档，不再扫消息
                var second = WorkAuditReport.Collect(store, null, archive, 7, null, null, null, 100);
                Assert.Equal("archive", second.Source);
                Assert.Equal(2, second.Items.Count);
                Assert.Equal(0, second.ScannedMessages);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [Fact]
        public void Read_PicksUpAppendedRowsAfterCacheWarm()
        {
            var (archive, dir) = NewArchive();
            try
            {
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                archive.Record(Entry("m1", now));
                Assert.Single(archive.Read(now - 1000)); // 预热缓存

                archive.Record(Entry("m2", now));
                var rows = archive.Read(now - 1000);
                Assert.Equal(2, rows.Count);
                Assert.Contains(rows, (r) => r.MessageId == "m2");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [Fact]
        public void Collect_FiltersByGroupAndExposesFacets()
        {
            var (archive, dir) = NewArchive();
            try
            {
                var store = NewStore();
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                store.AddMessage(Msg("m1", "agent_a", now, PlanWithTwo));
                store.AddGroup(new Group
                {
                    GroupId = "g2",
                    GroupName = "客服",
                    OwnerId = "user_1",
                    CreateTime = now,
                });
                store.AddMessage(new GroupMessage
                {
                    MessageId = "m2", GroupId = "g2", ThreadId = "g2", SenderId = "agent_b",
                    SenderType = MemberType.Agent, SenderNickname = "agent_b", Content = "x",
                    Timestamp = now, PlanJson = PlanWithTwo,
                });

                var all = WorkAuditReport.Collect(store, null, archive, 7, null, null, null, 100);
                Assert.Equal(4, all.Items.Count);
                Assert.Equal(2, all.Groups.Count);
                Assert.Equal(2, all.Agents.Count);

                var onlyG2 = WorkAuditReport.Collect(store, null, archive, 7, null, "g2", null, 100);
                Assert.Equal(2, onlyG2.Items.Count);
                Assert.All(onlyG2.Items, (e) => Assert.Equal("客服", e.GroupName));
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }
    }
}
