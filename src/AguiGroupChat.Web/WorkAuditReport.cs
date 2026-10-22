using System.Globalization;
using System.Text;
using System.Text.Json;
using AguiGroupChat.Hub.Storage;

namespace AguiGroupChat.Web;

/// <summary>
/// 跨消息汇总「工作区操作审计」（1.0.314）：计划卡里的 <c>audit[]</c> 原本只跟着单条消息走，
/// 想按员工 / 时间 / 命令查就得一条条翻聊天记录。这里把最近 N 天带 <c>planJson</c> 的回复扫一遍，
/// 抽出审计行给管理控制台筛选与导出。
///
/// <para>性能边界：只扫 <c>PlanJson</c> 非空的消息（工作型员工才会写），按时间截止过滤；
/// 天数上限 90、条数上限 5000。</para>
/// </summary>
public static class WorkAuditReport
{
    /// <summary>一条审计记录（已补上所属知聚 / 消息 / 时间）。</summary>
    public sealed record Entry(
        string GroupId,
        string GroupName,
        string AgentId,
        string MessageId,
        string TopicId,
        long At,
        string Tool,
        bool Ok,
        long DurationMs,
        string Text);

    public sealed record Result(IReadOnlyList<Entry> Items, bool Truncated, int ScannedMessages);

    public static Result Collect(
        IGroupStore store,
        Func<string, string?>? nicknameOf,
        int days,
        string? agentId,
        string? query,
        int limit)
    {
        var clampedDays = Math.Clamp(days <= 0 ? 7 : days, 1, 90);
        var max = Math.Clamp(limit <= 0 ? 500 : limit, 1, 5000);
        var cutoff = DateTimeOffset.UtcNow.AddDays(-clampedDays).ToUnixTimeMilliseconds();
        var needle = string.IsNullOrWhiteSpace(query) ? null : query.Trim();
        var agentFilter = string.IsNullOrWhiteSpace(agentId) ? null : agentId.Trim();

        var items = new List<Entry>();
        var scanned = 0;
        var truncated = false;

        foreach (var group in store.AllGroups())
        {
            if (items.Count >= max) { truncated = true; break; }
            IReadOnlyList<Hub.Models.GroupMessage> messages;
            try { messages = store.AllMessages(group.GroupId); }
            catch { continue; }

            for (var i = messages.Count - 1; i >= 0; i--)
            {
                var m = messages[i];
                // 不依赖存储实现是否按时间排序（内存实现按插入顺序）：早于截止时间就跳过而不是停止
                if (m.Timestamp < cutoff) continue;
                if (string.IsNullOrEmpty(m.PlanJson)) continue;
                if (agentFilter is not null && !string.Equals(m.SenderId, agentFilter, StringComparison.Ordinal)) continue;
                scanned++;
                if (!TryReadAudit(m.PlanJson!, out var rows)) continue;

                foreach (var row in rows)
                {
                    if (needle is not null
                        && !row.Text.Contains(needle, StringComparison.OrdinalIgnoreCase)
                        && !row.Tool.Contains(needle, StringComparison.OrdinalIgnoreCase)) continue;
                    items.Add(new Entry(
                        group.GroupId,
                        group.GroupName,
                        nicknameOf?.Invoke(m.SenderId) ??
                            (string.IsNullOrWhiteSpace(m.SenderNickname) ? m.SenderId : m.SenderNickname!),
                        m.MessageId,
                        m.TopicId,
                        m.Timestamp,
                        row.Tool,
                        row.Ok,
                        row.DurationMs,
                        row.Text));
                    if (items.Count >= max) { truncated = true; break; }
                }
                if (truncated) break;
            }
        }

        return new Result(items.OrderByDescending((e) => e.At).ToList(), truncated, scanned);
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
        sb.AppendLine("time,group,agent,tool,ok,durationMs,text,messageId,topicId");
        foreach (var e in items)
        {
            sb.Append(Esc(DateTimeOffset.FromUnixTimeMilliseconds(e.At).ToLocalTime()
                .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))).Append(',')
              .Append(Esc(e.GroupName)).Append(',')
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
