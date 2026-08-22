using System.Text.Json;
using AguiGroupChat.Hub.Infra;

namespace AguiGroupChat.Hub.Persistence.Postgres;

/// <summary>
/// PostgreSQL 扩展区存储：把上层注册的扩展区（当前为智能体定义）序列化为 JSON 落库到 agui_sections 表。
/// 变更经 <see cref="ChangeHub"/> 标记脏位后定时合并写入（策略与 JSON 快照一致，默认 5 秒）；
/// 与上次写入内容相同时跳过，避免无关变更（如在线状态）频繁写库。
/// </summary>
public sealed class PostgresSectionStore : ISectionStore, IDisposable
{
    private readonly PostgresStore _pg;
    private readonly ILogger<PostgresSectionStore> _logger;
    private readonly object _gate = new();
    private readonly Dictionary<string, (Func<object?> Snapshot, Action<JsonElement> Restore)> _sections = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _lastWritten = new(StringComparer.Ordinal);
    private Timer? _timer;
    private bool _dirty; // 脏位：全部读写经 Volatile.Read / Volatile.Write（见 Flush / MarkDirty）
    private int _flushInProgress;

    public PostgresSectionStore(PostgresStore pg, ChangeHub changeHub, ILogger<PostgresSectionStore> logger)
    {
        _pg = pg;
        _logger = logger;
        changeHub.Subscribe(MarkDirty);
        _timer = new Timer(_ => Flush(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    public void AddSection(string name, Func<object?> snapshot, Action<JsonElement> restore)
    {
        lock (_gate) _sections[name] = (snapshot, restore);
    }

    public void LoadSections()
    {
        Dictionary<string, string> rows;
        using (var conn = _pg.Open())
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT name, payload FROM agui_sections";
            rows = new Dictionary<string, string>(StringComparer.Ordinal);
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) rows[reader.GetString(0)] = reader.GetString(1);
        }

        KeyValuePair<string, (Func<object?> Snapshot, Action<JsonElement> Restore)>[] sections;
        lock (_gate) sections = _sections.ToArray();

        foreach (var (name, section) in sections)
        {
            if (!rows.TryGetValue(name, out var payload)) continue;
            try
            {
                using var doc = JsonDocument.Parse(payload);
                section.Restore(doc.RootElement.Clone());
                _lastWritten[name] = payload;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "扩展区「{Name}」恢复失败，已跳过", name);
            }
        }
    }

    public void Flush()
    {
        // 已有落盘进行中：本轮让位（脏位由进行中那轮保留，清位只发生在真正干活的那一轮，防并发丢变更）
        if (Interlocked.Exchange(ref _flushInProgress, 1) == 1) return;
        // 先原子清位再干活：清位后若有新变更会重新置脏，由下一轮定时器再写，数据不丢（去掉结尾的读-清双检）
        if (Interlocked.Exchange(ref _dirty, false) == false)
        {
            Interlocked.Exchange(ref _flushInProgress, 0); // 无变更：释放落盘标记
            return;
        }
        try
        {
            KeyValuePair<string, (Func<object?> Snapshot, Action<JsonElement> Restore)>[] sections;
            lock (_gate) sections = _sections.ToArray();

            foreach (var (name, section) in sections)
            {
                object? value;
                try { value = section.Snapshot(); }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "扩展区「{Name}」快照失败，已跳过", name);
                    continue;
                }
                var json = value is null ? "null" : AguiJson.Serialize(value);
                if (_lastWritten.TryGetValue(name, out var prev) && prev == json) continue;
                Upsert(name, json);
                _lastWritten[name] = json;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "扩展区落库失败");
        }
        finally
        {
            Interlocked.Exchange(ref _flushInProgress, 0);
        }
    }

    private void Upsert(string name, string payload)
    {
        using var conn = _pg.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO agui_sections (name, payload) VALUES (@name, @payload)
            ON CONFLICT (name) DO UPDATE SET payload = EXCLUDED.payload
            """;
        cmd.Parameters.AddWithValue("name", name);
        cmd.Parameters.AddWithValue("payload", payload);
        cmd.ExecuteNonQuery();
    }

    private void MarkDirty() => Volatile.Write(ref _dirty, true);

    public void Dispose() => _timer?.Dispose();
}
