using System.Globalization;
using System.Text;
using System.Text.Json;
using AguiGroupChat.Hub.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AguiGroupChat.Web;

/// <summary>
/// 工作区操作审计的按月归档（1.0.315）：每个自然月一个 JSONL 文件
/// <c>{ContentRoot}/data/work-audit/audit-YYYY-MM.jsonl</c>，一行一条 <see cref="WorkAuditEntry"/>。
///
/// <para>为什么要单独存：查询「最近 N 天干了什么」原先要扫全部知聚的全部消息并逐条解析 <c>planJson</c>，
/// 随消息量线性变慢；归档后按月份直接读文件，查询成本只与命中行数相关。同时它也是合规意义上的审计留存——
/// 消息被撤回 / 删除后，归档行仍在（与 chat 记录的“可消失”语义不同）。</para>
///
/// <para>失败策略：写失败只记 warning，绝不影响消息主流程（审计仍随 <c>planJson</c> 落库）；读失败同样降级为空，
/// 由调用方退回直接扫描。</para>
/// </summary>
public sealed class WorkAuditArchive : IWorkAuditSink
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string Prefix = "audit-";

    private readonly string _dir;
    private readonly ILogger<WorkAuditArchive> _logger;
    private readonly object _gate = new();
    private readonly Dictionary<string, MonthCache> _cache = new(StringComparer.OrdinalIgnoreCase);
    private const int MaxCachedEntries = 100_000;

    private sealed class MonthCache
    {
        public long Length { get; set; }
        public long Ticks { get; set; }
        public List<WorkAuditEntry> Entries { get; } = [];
        public HashSet<string> Keys { get; } = new(StringComparer.Ordinal);
    }

    public WorkAuditArchive(IHostEnvironment env, ILogger<WorkAuditArchive> logger)
        : this(Path.Combine(env.ContentRootPath, "data", "work-audit"), logger)
    {
    }

    /// <summary>直接指定目录（测试用）。</summary>
    public WorkAuditArchive(string directory, ILogger<WorkAuditArchive> logger)
    {
        _dir = directory;
        _logger = logger;
        try { Directory.CreateDirectory(_dir); } catch { /* 不可写：后续读取降级为空 */ }
    }

    /// <summary>归档根目录（诊断 / 测试用）。</summary>
    public string RootDir => _dir;

    public void Record(WorkAuditEntry entry)
    {
        if (entry is null || string.IsNullOrWhiteSpace(entry.Text)) return;
        try
        {
            var line = JsonSerializer.Serialize(entry, Json) + "\n";
            lock (_gate)
            {
                Directory.CreateDirectory(_dir);
                File.AppendAllText(PathFor(entry.At), line, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "工作区审计归档写入失败（已忽略）：message={MessageId}", entry.MessageId);
        }
    }

    /// <summary>读取 <paramref name="sinceMs"/> 之后（含）的审计行，按 key 去重（回填与实时写入可能重叠）。
    /// 月份文件按「已解析前缀」缓存：重复查询只增量解析新追加的行。</summary>
    public IReadOnlyList<WorkAuditEntry> Read(long sinceMs, int max = 20000)
    {
        var result = new List<WorkAuditEntry>();
        foreach (var path in MonthFiles(sinceMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()))
        {
            foreach (var e in ReadMonth(path))
            {
                if (e.At < sinceMs) continue;
                result.Add(e);
                if (result.Count >= max) return result;
            }
        }
        return result;
    }

    /// <summary>读取单个月份文件（缓存 + 尾部增量）；失败返回空。</summary>
    private IReadOnlyList<WorkAuditEntry> ReadMonth(string path)
    {
        try
        {
            var ticks = File.GetLastWriteTimeUtc(path).Ticks;
            lock (_gate)
            {
                if (!_cache.TryGetValue(path, out var cached))
                {
                    cached = new MonthCache();
                    cached.Length = AppendFrom(path, cached, 0);
                    cached.Ticks = ticks;
                    _cache[path] = cached;
                    EvictIfNeeded();
                    return cached.Entries;
                }
                if (cached.Ticks != ticks)
                {
                    cached.Length = AppendFrom(path, cached, cached.Length);
                    cached.Ticks = ticks;
                }
                return cached.Entries;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "工作区审计归档读取失败（跳过该月）：{Path}", path);
            return [];
        }
    }

    /// <summary>从 <paramref name="offset"/> 开始解析并并入缓存；只消费到最后一个换行符
    /// （避开写了一半的末行，等下次写入后再读）。返回新的已消费偏移。</summary>
    private static long AppendFrom(string path, MonthCache cache, long offset)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (offset > fs.Length) offset = 0;
        var available = fs.Length - offset;
        if (available <= 0) return offset;
        fs.Seek(offset, SeekOrigin.Begin);
        var buffer = new byte[available];
        var read = 0;
        while (read < buffer.Length)
        {
            var n = fs.Read(buffer, read, buffer.Length - read);
            if (n <= 0) break;
            read += n;
        }
        if (read == 0) return offset;
        var lastNewline = Array.LastIndexOf(buffer, (byte)'\n', read - 1);
        if (lastNewline < 0) return offset;

        var text = Encoding.UTF8.GetString(buffer, 0, lastNewline + 1);
        foreach (var line in text.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            WorkAuditEntry? e;
            try { e = JsonSerializer.Deserialize<WorkAuditEntry>(line, Json); }
            catch { continue; }
            if (e is null) continue;
            if (!cache.Keys.Add(KeyOf(e))) continue;
            cache.Entries.Add(e);
        }
        return offset + lastNewline + 1;
    }

    /// <summary>缓存条目总量上限：超过则丢掉最早的月份（下次读再解析）。</summary>
    private void EvictIfNeeded()
    {
        var total = _cache.Values.Sum((c) => c.Entries.Count);
        if (total <= MaxCachedEntries) return;
        foreach (var key in _cache.Keys.OrderBy((k) => k, StringComparer.OrdinalIgnoreCase).ToList())
        {
            if (total <= MaxCachedEntries) break;
            total -= _cache[key].Entries.Count;
            _cache.Remove(key);
        }
    }

    /// <summary>删除早于保留期的月份文件，返回删除数（retentionDays ≤ 0 表示永久保留）。</summary>
    public int Sweep(int retentionDays)
    {
        if (retentionDays <= 0) return 0;
        var cutoff = DateTimeOffset.UtcNow.AddDays(-retentionDays);
        var deleted = 0;
        try
        {
            foreach (var path in Directory.EnumerateFiles(_dir, Prefix + "*.jsonl"))
            {
                var month = MonthOf(path);
                if (month is null) continue;
                // 整月都早于保留期才删：不切碎当月数据
                if (month.Value.AddMonths(1) > cutoff) continue;
                try { File.Delete(path); deleted++; }
                catch (Exception ex) { _logger.LogDebug(ex, "归档文件删除失败：{Path}", path); }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "归档清理扫描失败：{Dir}", _dir);
        }
        return deleted;
    }

    private string PathFor(long atMs)
        => Path.Combine(_dir, Prefix + DateTimeOffset.FromUnixTimeMilliseconds(atMs).UtcDateTime
            .ToString("yyyy-MM", CultureInfo.InvariantCulture) + ".jsonl");

    private static string KeyOf(WorkAuditEntry e)
        => string.Concat(e.MessageId, "|", e.Tool, "|", e.DurationMs.ToString(CultureInfo.InvariantCulture), "|", e.Text);

    /// <summary>覆盖 [since, until] 的所有月份文件路径（不存在的跳过）。</summary>
    private IEnumerable<string> MonthFiles(long sinceMs, long untilMs)
    {
        var start = DateTimeOffset.FromUnixTimeMilliseconds(Math.Max(0, sinceMs)).UtcDateTime;
        var end = DateTimeOffset.FromUnixTimeMilliseconds(untilMs).UtcDateTime;
        var month = new DateTime(start.Year, start.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var guard = 0;
        while (month <= end && guard++ < 600)
        {
            var path = Path.Combine(_dir, Prefix + month.ToString("yyyy-MM", CultureInfo.InvariantCulture) + ".jsonl");
            if (File.Exists(path)) yield return path;
            month = month.AddMonths(1);
        }
    }

    private static DateTimeOffset? MonthOf(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        if (!name.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        return DateTimeOffset.TryParseExact(name[Prefix.Length..], "yyyy-MM", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var d)
            ? d
            : null;
    }
}

/// <summary>
/// 归档维护服务（1.0.315）：启动 10 分钟后按 <see cref="Agents.AgentOptions.WorkAuditRetentionDays"/>
/// 清掉过期月份文件，此后每 12 小时一次。保留期 ≤ 0 时不做任何事。
/// </summary>
public sealed class WorkAuditArchiveService : IHostedService, IDisposable
{
    private readonly WorkAuditArchive _archive;
    private readonly Agents.AgentOptions _options;
    private readonly ILogger<WorkAuditArchiveService> _logger;
    private Timer? _timer;

    public WorkAuditArchiveService(WorkAuditArchive archive, Agents.AgentOptions options, ILogger<WorkAuditArchiveService> logger)
    {
        _archive = archive;
        _options = options;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_options.WorkAuditRetentionDays <= 0) return Task.CompletedTask;
        _timer = new Timer(_ => RunOnce(), null, TimeSpan.FromMinutes(10), TimeSpan.FromHours(12));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _timer?.Dispose();
        _timer = null;
        return Task.CompletedTask;
    }

    private void RunOnce()
    {
        try
        {
            var deleted = _archive.Sweep(_options.WorkAuditRetentionDays);
            if (deleted > 0)
                _logger.LogInformation("工作区审计归档清理：删除 {Count} 个过期月份文件（保留 {Days} 天）",
                    deleted, _options.WorkAuditRetentionDays);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "工作区审计归档定时清理异常");
        }
    }

    public void Dispose() => _timer?.Dispose();
}
