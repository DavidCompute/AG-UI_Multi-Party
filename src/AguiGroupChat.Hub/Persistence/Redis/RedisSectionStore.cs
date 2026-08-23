using System.Text.Json;
using AguiGroupChat.Hub.Infra;
using AguiGroupChat.Hub.Persistence;

namespace AguiGroupChat.Hub.Persistence.Redis;

/// <summary>
/// Redis 扩展区存储（6.2 多副本共享）：把上层注册的扩展区（智能体定义 / 知识库目录 / 桥接游标 /
/// 模型配置 / TOTP 密钥 / 登录会话）序列化为 JSON 存于 <c>agui:section:{name}</c>，
/// 供多副本整体共享。变更经 <see cref="ChangeHub"/> 标记脏位后定时合并写入（默认 5 秒），
/// 与上次写入内容相同时跳过——策略与 <see cref="Postgres.PostgresSectionStore"/> 一致。
/// </summary>
public sealed class RedisSectionStore : ISectionStore, IDisposable
{
    private readonly RedisContext _ctx;
    private readonly ILogger<RedisSectionStore> _logger;
    private readonly object _gate = new();
    private readonly Dictionary<string, (Func<object?> Snapshot, Action<JsonElement> Restore)> _sections = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _lastWritten = new(StringComparer.Ordinal);
    private Timer? _timer;
    private bool _dirty;
    private int _flushInProgress;

    public RedisSectionStore(RedisContext ctx, ChangeHub changeHub, ILogger<RedisSectionStore> logger)
    {
        _ctx = ctx;
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
        var db = _ctx.Db;
        var rows = new Dictionary<string, string>(StringComparer.Ordinal);
        var server = _ctx.Mux.GetServer(_ctx.Mux.GetEndPoints()[0]);
        foreach (var key in server.Keys(pattern: "agui:section:*"))
        {
            var name = key.ToString()["agui:section:".Length..];
            var val = db.StringGet(key);
            if (!val.IsNullOrEmpty) rows[name] = val.ToString();
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
        if (Interlocked.Exchange(ref _flushInProgress, 1) == 1) return;
        if (Interlocked.Exchange(ref _dirty, false) == false)
        {
            Interlocked.Exchange(ref _flushInProgress, 0);
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
                _ctx.Db.StringSet(RedisContext.SectionKey(name), json);
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

    private void MarkDirty() => Volatile.Write(ref _dirty, true);

    public void Dispose() => _timer?.Dispose();
}
