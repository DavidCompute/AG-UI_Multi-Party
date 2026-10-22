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

    /// <summary><paramref name="Source"/>：<c>archive</c> = 读归档 / <c>scan</c> = 回扫消息。</summary>
    public sealed record Result(IReadOnlyList<Entry> Items, bool Truncated, int ScannedMessages, string Source = "scan");

    /// <summary>兼容旧调用（测试 / 无归档宿主）：直接扫消息。</summary>
    public static Result Collect(
        IGroupStore store,
        Func<string, string?>? nicknameOf,
        int days,
        string? agentId,
        string? query,
        int limit)
        => Collect(store, nicknameOf, archive: null, days, agentId, query, limit);

    public static Result Collect(
        IGroupStore store,
        Func<string, string?>? nicknameOf,
        WorkAuditArchive? archive,
        int days,
        string? agentId,
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

        var items = new List<Entry>();
        var truncated = false;
        foreach (var r in rows
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

        return new Result(items, truncated, scanned, source);
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

    /// <summary>导出 CSV（带 BOM，Excel 直接打开不乱码）。</summary>
    public static string ToCsv(IReadOnlyList<Entry> items)
    {
        static string Esc(string? v)
        {
            var s = v ?? "";
            return s.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        }

        var sb = new StringBuilder();
        sb.Append('\uFEFF');
        sb.AppendLine("time,group,agent,agentId,tool,ok,durationMs,text,messageId,topicId");
        foreach (var e in items)
        {
            sb.Append(Esc(DateTimeOffset.FromUnixTimeMilliseconds(e.At).ToLocalTime()
                .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))).Append(',')
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
