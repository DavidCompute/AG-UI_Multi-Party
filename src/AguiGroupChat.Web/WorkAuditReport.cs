using System.Globalization;
using System.Text;
using System.Text.Json;
using AguiGroupChat.Hub.Storage;

namespace AguiGroupChat.Web;

/// <summary>
/// 工作区操作审计查询（1.0.314；1.0.315 起改读按月归档）：
///
/// <para>来源优先级：① <see cref="WorkAuditArchive"/>（按月 JSONL，查询成本只与命中行数相关）；
/// ② 归档为空（首次使用 / 归档不可用）时回扫带 <c>planJson</c> 的回复，并把结果回填归档，
/// 让后续查询走归档。即使回填与实时写入重叠，读取侧按 key 去重不会重复计数。</para>
///
/// <para>回扫边界：跳过「最近消息早于截止时间」的知聚（<see cref="IGroupStore.LastMessageAt"/>），
/// 天数上限 90、条数上限 5000。</para>
/// </summary>
public static class WorkAuditReport
{
    /// <summary>一条审计记录（已补上所属知聚 / 消息 / 时间）。</summary>
    public sealed record Entry(
        string GroupId,
        string GroupName,
        string AgentId,
        string AgentName,
        string MessageId,
        string TopicId,
        long At,
        string Tool,
        bool Ok,
        long DurationMs,
        string Text);

    /// <summary>筛选项（知聚 / 数字员工）：取时间范围内的全部行统计，不受当前筛选影响。</summary>
    public sealed record Facet(string Id, string Name, int Count);

    /// <summary>按天的工具分布（1.0.321）：用于审计看板柱状图。</summary>
    public sealed record TimelineDay(
        string Date, int Commands, int Writes, int Reads, int Listings, int Other, int Failed)
    {
        public int Total => Commands + Writes + Reads + Listings + Other + Failed;
    }

    /// <summary>看板的一根柱（1.0.322）：按天 / 按员工 / 按知聚都归一到这个形状。</summary>
    public sealed record TimelineBucket(
        string Key, string Label, int Commands, int Writes, int Reads, int Listings, int Other, int Failed)
    {
        public int Total => Commands + Writes + Reads + Listings + Other + Failed;
    }

    /// <summary>按「员工 / 知聚」聚合工具分布（1.0.322）：总量降序，失败单独成段。</summary>
    public static IReadOnlyList<TimelineBucket> GroupTimeline(IReadOnlyList<Entry> items, string dimension)
    {
        var byAgent = string.Equals(dimension, "agent", StringComparison.OrdinalIgnoreCase);
        var map = new Dictionary<string, (string? Label, int C, int W, int R, int L, int O, int F)>(StringComparer.Ordinal);
        foreach (var e in items)
        {
            var key = byAgent ? e.AgentId : e.GroupId;
            if (string.IsNullOrWhiteSpace(key)) continue;
            if (!map.TryGetValue(key, out var b))
                b.Label = byAgent
                    ? (string.IsNullOrWhiteSpace(e.AgentName) ? e.AgentId : e.AgentName)
                    : (string.IsNullOrWhiteSpace(e.GroupName) ? e.GroupId : e.GroupName);
            if (!e.Ok)
            {
                b.F++;
                map[key] = b;
                continue;
            }
            switch (e.Tool)
            {
                case "work_run_command": b.C++; break;
                case "work_write_file": b.W++; break;
                case "work_read_file": b.R++; break;
                case "work_list_dir": b.L++; break;
                default: b.O++; break;
            }
            map[key] = b;
        }
        return map
            .Select((kv) => new TimelineBucket(kv.Key, kv.Value.Label ?? kv.Key,
                kv.Value.C, kv.Value.W, kv.Value.R, kv.Value.L, kv.Value.O, kv.Value.F))
            .OrderByDescending((x) => x.Total)
            .ThenBy((x) => x.Label, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>把审计行按 UTC 日期分桶（缺失的日期补 0），供看板画柱状图。</summary>
    public static IReadOnlyList<TimelineDay> Timeline(IReadOnlyList<Entry> items, int days)
    {
        var n = Math.Clamp(days <= 0 ? 7 : days, 1, 90);
        var today = DateTimeOffset.UtcNow.UtcDateTime.Date;
        var buckets = new SortedDictionary<string, (int C, int W, int R, int L, int O, int F)>(StringComparer.Ordinal);
        for (var i = n - 1; i >= 0; i--)
            buckets[today.AddDays(-i).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)] = (0, 0, 0, 0, 0, 0);

        foreach (var e in items)
        {
            var key = DateTimeOffset.FromUnixTimeMilliseconds(e.At).UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (!buckets.TryGetValue(key, out var b)) continue;
            // 失败单独成段（不重复计入工具分类），否则堆叠柱的总量会被重复计数
            if (!e.Ok)
            {
                b.F++;
                buckets[key] = b;
                continue;
            }
            switch (e.Tool)
            {
                case "work_run_command": b.C++; break;
                case "work_write_file": b.W++; break;
                case "work_read_file": b.R++; break;
                case "work_list_dir": b.L++; break;
                default: b.O++; break;
            }
            buckets[key] = b;
        }

        return buckets.Select((kv) => new TimelineDay(kv.Key, kv.Value.C, kv.Value.W, kv.Value.R, kv.Value.L, kv.Value.O, kv.Value.F)).ToList();
    }

    /// <summary><paramref name="Source"/>：<c>archive</c> = 读归档 / <c>scan</c> = 回扫消息。</summary>
    public sealed record Result(
        IReadOnlyList<Entry> Items,
        bool Truncated,
        int ScannedMessages,
        string Source,
        IReadOnlyList<Facet> Groups,
        IReadOnlyList<Facet> Agents);

    /// <summary>兼容旧调用（测试 / 无归档宿主）：直接扫消息。</summary>
    public static Result Collect(
        IGroupStore store,
        Func<string, string?>? nicknameOf,
        int days,
        string? agentId,
        string? query,
        int limit)
        => Collect(store, nicknameOf, archive: null, days, agentId, groupId: null, query, limit);

    public static Result Collect(
        IGroupStore store,
        Func<string, string?>? nicknameOf,
        WorkAuditArchive? archive,
        int days,
        string? agentId,
        string? groupId,
        string? query,
        int limit)
    {
        var clampedDays = Math.Clamp(days <= 0 ? 7 : days, 1, 90);
        var max = Math.Clamp(limit <= 0 ? 500 : limit, 1, 5000);
        var cutoff = DateTimeOffset.UtcNow.AddDays(-clampedDays).ToUnixTimeMilliseconds();

        var rows = new List<WorkAuditEntry>();
        var scanned = 0;
        var source = "scan";

        if (archive is not null)
        {
            try { rows.AddRange(archive.Read(cutoff)); } catch { /* 归档不可读：退回扫描 */ }
            if (rows.Count > 0) source = "archive";
        }

        if (rows.Count == 0)
        {
            var (scannedRows, scannedMessages) = Scan(store, nicknameOf, cutoff);
            scanned = scannedMessages;
            rows = scannedRows;
            if (archive is not null)
            {
                foreach (var r in rows) archive.Record(r); // 回填：下次查询走归档
            }
        }

        var needle = string.IsNullOrWhiteSpace(query) ? null : query.Trim();
        var agentFilter = string.IsNullOrWhiteSpace(agentId) ? null : agentId.Trim();
        var groupFilter = string.IsNullOrWhiteSpace(groupId) ? null : groupId.Trim();

        // 筛选项：按时间范围内的全部行统计（不受员工 / 知聚 / 关键词筛选影响）
        var groupFacets = rows
            .GroupBy((x) => x.GroupId, StringComparer.Ordinal)
            .Select((g) => new Facet(g.Key, g.First().GroupName, g.Count()))
            .OrderByDescending((f) => f.Count).ThenBy((f) => f.Name, StringComparer.CurrentCulture)
            .ToList();
        var agentFacets = rows
            .GroupBy((x) => x.AgentId, StringComparer.Ordinal)
            .Where((g) => !string.IsNullOrWhiteSpace(g.Key))
            .Select((g) => new Facet(g.Key,
                g.Select((x) => x.AgentName).FirstOrDefault((n) => !string.IsNullOrWhiteSpace(n)) ?? g.Key, g.Count()))
            .OrderByDescending((f) => f.Count).ThenBy((f) => f.Name, StringComparer.CurrentCulture)
            .ToList();

        var items = new List<Entry>();
        var truncated = false;
        foreach (var r in rows
            .Where((x) => groupFilter is null || string.Equals(x.GroupId, groupFilter, StringComparison.Ordinal))
            .Where((x) => agentFilter is null || string.Equals(x.AgentId, agentFilter, StringComparison.Ordinal))
            .Where((x) => needle is null
                || x.Text.Contains(needle, StringComparison.OrdinalIgnoreCase)
                || x.Tool.Contains(needle, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending((x) => x.At))
        {
            items.Add(new Entry(r.GroupId, r.GroupName, r.AgentId, r.AgentName, r.MessageId, r.TopicId, r.At,
                r.Tool, r.Ok, r.DurationMs, r.Text));
            if (items.Count >= max) { truncated = true; break; }
        }

        return new Result(items, truncated, scanned, source, groupFacets, agentFacets);
    }

    /// <summary>直接扫消息（归档回填 / 无归档宿主）：从 <c>planJson.audit</c> 抽行。</summary>
    public static (List<WorkAuditEntry> Rows, int ScannedMessages) Scan(
        IGroupStore store,
        Func<string, string?>? nicknameOf,
        long cutoffMs)
    {
        var rows = new List<WorkAuditEntry>();
        var scanned = 0;

        foreach (var group in store.AllGroups())
        {
            // 整支知聚最近都没有消息 → 不可能有本区间的审计，直接跳过（省掉一次全量消息读取）
            try
            {
                var last = store.LastMessageAt(group.GroupId);
                if (last is null || last < cutoffMs) continue;
            }
            catch { /* 读不到活跃度：保守扫描 */ }

            IReadOnlyList<Hub.Models.GroupMessage> messages;
            try { messages = store.AllMessages(group.GroupId); }
            catch { continue; }

            for (var i = 0; i < messages.Count; i++)
            {
                var m = messages[i];
                if (m.Timestamp < cutoffMs) continue;
                if (string.IsNullOrEmpty(m.PlanJson)) continue;
                scanned++;
                if (!TryReadAudit(m.PlanJson!, out var auditRows)) continue;

                var agent = nicknameOf?.Invoke(m.SenderId) ??
                    (string.IsNullOrWhiteSpace(m.SenderNickname) ? m.SenderId : m.SenderNickname!);
                foreach (var row in auditRows)
                {
                    rows.Add(new WorkAuditEntry(
                        group.GroupId,
                        group.GroupName,
                        m.SenderId,
                        agent,
                        m.MessageId,
                        m.TopicId,
                        m.Timestamp,
                        row.Tool,
                        row.Ok,
                        row.DurationMs,
                        row.Text));
                }
            }
        }
        return (rows, scanned);
    }

    /// <summary>按天钻取（1.0.324）：保留落在某个 UTC 日期内的审计行；date 为空 / 格式不合法时原样返回。</summary>
    public static IReadOnlyList<Entry> FilterByDate(IReadOnlyList<Entry> items, string? date)
    {
        if (string.IsNullOrWhiteSpace(date)) return items;
        if (!DateTime.TryParseExact(date.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var day)) return items;
        var from = new DateTimeOffset(DateTime.SpecifyKind(day, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
        var to = from + 86_400_000L;
        return items.Where((e) => e.At >= from && e.At < to).ToList();
    }

    /// <summary>按工具筛选可见行（1.0.325）：command / write / read / list / other；其余值（含 all）不筛。</summary>
    public static IReadOnlyList<Entry> FilterByTool(IReadOnlyList<Entry> items, string? tool)
    {
        var t = (tool ?? "").Trim().ToLowerInvariant();
        if (t.Length == 0 || t == "all") return items;
        var isKnown = t is "command" or "write" or "read" or "list" or "other";
        if (!isKnown) return items;
        return items.Where((e) => t switch
        {
            "command" => e.Tool == "work_run_command",
            "write" => e.Tool == "work_write_file",
            "read" => e.Tool == "work_read_file",
            "list" => e.Tool == "work_list_dir",
            _ => e.Tool is not ("work_run_command" or "work_write_file" or "work_read_file" or "work_list_dir"),
        }).ToList();
    }

    /// <summary>导出 CSV（带 BOM，Excel 直接打开不乱码）。</summary>
    public static string ToCsv(IReadOnlyList<Entry> items)
    {
        var sb = new StringBuilder();
        sb.Append('\uFEFF');
        sb.AppendLine("time,group,agent,agentId,tool,ok,durationMs,text,messageId,topicId");
        foreach (var e in items)
        {
            sb.Append(Esc(LocalTime(e.At))).Append(',')
              .Append(Esc(e.GroupName)).Append(',')
              .Append(Esc(string.IsNullOrWhiteSpace(e.AgentName) ? e.AgentId : e.AgentName)).Append(',')
              .Append(Esc(e.AgentId)).Append(',')
              .Append(Esc(e.Tool)).Append(',')
              .Append(e.Ok ? "true" : "false").Append(',')
              .Append(e.DurationMs).Append(',')
              .Append(Esc(e.Text)).Append(',')
              .Append(Esc(e.MessageId)).Append(',')
              .Append(Esc(e.TopicId)).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>按消息聚合导出 CSV（1.0.318）：一行一条回答，含操作数 / 异常数 / 总耗时。
    /// 与管理控制台「按消息聚合」视图同一口径。</summary>
    public static string ToGroupedCsv(IReadOnlyList<Entry> items)
    {
        var sb = new StringBuilder();
        sb.Append('\uFEFF');
        sb.AppendLine("time,group,agent,actions,failures,durationMs,firstText,messageId,topicId");
        foreach (var g in items.GroupBy((e) => e.MessageId, StringComparer.Ordinal)
                     .OrderByDescending((g) => g.Max((x) => x.At)))
        {
            var first = g.First();
            var at = g.Max((x) => x.At);
            sb.Append(Esc(LocalTime(at))).Append(',')
              .Append(Esc(first.GroupName)).Append(',')
              .Append(Esc(string.IsNullOrWhiteSpace(first.AgentName) ? first.AgentId : first.AgentName)).Append(',')
              .Append(g.Count()).Append(',')
              .Append(g.Count((x) => !x.Ok)).Append(',')
              .Append(g.Sum((x) => x.DurationMs)).Append(',')
              .Append(Esc(first.Text)).Append(',')
              .Append(Esc(first.MessageId)).Append(',')
              .Append(Esc(first.TopicId)).Append('\n');
        }
        return sb.ToString();
    }

    private static string LocalTime(long atMs)
        => DateTimeOffset.FromUnixTimeMilliseconds(atMs).ToLocalTime()
            .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>看板导出 CSV（1.0.323）：一行一个桶（天 / 员工 / 知聚），带失败率与 BOM。</summary>
    public static string ToTimelineCsv(IReadOnlyList<TimelineBucket> buckets)
    {
        var sb = new StringBuilder();
        sb.Append('\uFEFF');
        sb.AppendLine("bucket,total,commands,writes,reads,listings,other,failed,failureRate");
        foreach (var b in buckets)
        {
            var rate = b.Total > 0 ? b.Failed * 100.0 / b.Total : 0;
            sb.Append(Esc(b.Key)).Append(',')
              .Append(b.Total).Append(',')
              .Append(b.Commands).Append(',')
              .Append(b.Writes).Append(',')
              .Append(b.Reads).Append(',')
              .Append(b.Listings).Append(',')
              .Append(b.Other).Append(',')
              .Append(b.Failed).Append(',')
              .Append(rate.ToString("F1", CultureInfo.InvariantCulture)).Append('\n');
        }
        return sb.ToString();
    }

    private static string Esc(string? v)
    {
        var s = v ?? "";
        return s.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }

    private sealed record Row(string Tool, string Text, bool Ok, long DurationMs);

    /// <summary>从 planJson 里读 <c>audit[]</c>；格式不符 / 为空返回 false（不抛）。</summary>
    private static bool TryReadAudit(string planJson, out List<Row> rows)
    {
        rows = [];
        try
        {
            using var doc = JsonDocument.Parse(planJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            if (!doc.RootElement.TryGetProperty("audit", out var audit) || audit.ValueKind != JsonValueKind.Array) return false;
            foreach (var item in audit.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var text = item.TryGetProperty("text", out var tx) && tx.ValueKind == JsonValueKind.String ? tx.GetString() : null;
                if (string.IsNullOrWhiteSpace(text)) continue;
                rows.Add(new Row(
                    item.TryGetProperty("tool", out var tl) && tl.ValueKind == JsonValueKind.String ? (tl.GetString() ?? "") : "",
                    text!,
                    !item.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.False,
                    item.TryGetProperty("durationMs", out var dm) && dm.TryGetInt64(out var d) ? d : 0));
            }
            return rows.Count > 0;
        }
        catch
        {
            return false;
        }
    }
}
