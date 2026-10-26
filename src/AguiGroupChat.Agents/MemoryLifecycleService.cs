using System.Collections.Concurrent;
using AguiGroupChat.Hub.Models;
using AguiGroupChat.Hub.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AguiGroupChat.Agents;

/// <summary>一轮记忆生命周期任务的执行摘要（管理端 / 日志可观测）。</summary>
public sealed record MemoryLifecycleReport(
    long StartedAtMs,
    long FinishedAtMs,
    int Scanned,
    int Promoted,
    int ExpiriesSet,
    int ExpiriesCleared,
    int Pruned)
{
    public int Changed => Promoted + ExpiriesSet + ExpiriesCleared;
}

/// <summary>
/// 记忆「巩固 / 遗忘」周期任务（1.0.340）：按 <see cref="MemoryLifecyclePolicy"/> 扫描存量记忆并调整
/// 级别 / 过期时间，让五档记忆拟人类型不仅作用于新写入与检索，也随时间持续推进。
///
/// <para>
/// 任务只改元数据（importance / expires_at），不改写、不删除原文；删除仍由 <see cref="MemoryMaintenanceService"/>
/// 按过期时间执行。重要级记忆不会被自动过期；深记型 / 已巩固的难录入型会被提升并清除过期时间。
/// 单轮条数有上限，避免大库一次扫描过久；未启用记忆或关闭生命周期时静默跳过。
/// </para>
/// </summary>
public sealed class MemoryLifecycleService : IHostedService, IDisposable
{
    private readonly AgentCatalog _catalog;
    private readonly AgentOptions _options;
    private readonly IServiceProvider _services;
    private readonly ILogger<MemoryLifecycleService> _logger;
    private readonly ConcurrentDictionary<string, MemoryProfile?> _profiles = new(StringComparer.Ordinal);
    private Timer? _timer;
    private int _running;
    private MemoryLifecycleReport? _lastReport;

    public MemoryLifecycleService(
        AgentCatalog catalog,
        AgentOptions options,
        IServiceProvider services,
        ILogger<MemoryLifecycleService> logger)
    {
        _catalog = catalog;
        _options = options;
        _services = services;
        _logger = logger;
    }

    /// <summary>最近一轮摘要（尚未运行过为 null）。</summary>
    public MemoryLifecycleReport? LastReport => Volatile.Read(ref _lastReport);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var memory = _options.Memory;
        if (!memory.Enabled || !memory.LifecycleEnabled) return Task.CompletedTask;
        var hours = Math.Clamp(memory.LifecycleIntervalHours, 1, 24 * 30);
        // 首轮给应用稳定运行时间，此后按配置间隔执行
        _timer = new Timer(_ => RunOnce(), null, TimeSpan.FromMinutes(15), TimeSpan.FromHours(hours));
        _logger.LogInformation(
            "记忆生命周期任务已启动：每 {Hours} 小时一轮，单轮上限 {Max} 条（深记 {Deep}h / 难录入重复 {Repeat} 次）",
            hours, Math.Clamp(memory.LifecycleMaxPerRun, 1, 50_000),
            Math.Clamp(memory.DeepConsolidateAfterHours, 1, 24 * 365),
            Math.Clamp(memory.SlowToLearnRepeatThreshold, 2, 100));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _timer?.Dispose();
        _timer = null;
        return Task.CompletedTask;
    }

    public void Dispose() => _timer?.Dispose();

    private void RunOnce()
    {
        try { RunOnceAsync(CancellationToken.None).GetAwaiter().GetResult(); }
        catch (Exception ex) { _logger.LogWarning(ex, "记忆生命周期任务异常（下轮重试）"); }
    }

    /// <summary>手动执行一轮（测试 / 运维排障）。并发调用时后来者直接返回当前摘要。</summary>
    public async Task<MemoryLifecycleReport> RunOnceAsync(CancellationToken ct)
    {
        var memory = _options.Memory;
        var started = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (!memory.Enabled || !memory.LifecycleEnabled)
            return new MemoryLifecycleReport(started, started, 0, 0, 0, 0, 0);

        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            return LastReport ?? new MemoryLifecycleReport(started, started, 0, 0, 0, 0, 0);

        try
        {
            var store = _services.GetService(typeof(IMessageMemoryStore)) as IMessageMemoryStore;
            if (store is null)
                return new MemoryLifecycleReport(started, started, 0, 0, 0, 0, 0);

            _profiles.Clear(); // 每轮重新读取员工配置，改完记忆类型无需重启
            var now = started;
            var items = Scan(store, Math.Clamp(memory.LifecycleMaxPerRun, 1, 50_000));
            var repeatCounts = CountRepeats(items);

            int promoted = 0, expiriesSet = 0, expiriesCleared = 0;
            foreach (var item in items)
            {
                if (ct.IsCancellationRequested) break;
                // 已过期的留给清理任务，避免被巩固逻辑“复活”
                if (item.ExpiresAt is { } expired && expired <= now) continue;

                var profile = item.SenderType.Equals("Agent", StringComparison.OrdinalIgnoreCase)
                    ? ProfileFor(item.SenderId)
                    : null;
                var repeats = 0;
                if (profile?.MemoryType == MemoryPersonalityTypes.SlowToLearn
                    && MemoryLifecyclePolicy.RepeatKey(item.SenderId, item.Content) is { } key)
                    repeatCounts.TryGetValue(key, out repeats);

                var decision = MemoryLifecyclePolicy.Decide(item, profile, memory, repeats, now);
                if (!decision.HasChange) continue;

                if (decision.Promote)
                {
                    if (!store.PromoteImportance(item.MessageId, MemoryImportance.Important))
                        continue; // 升级失败则本轮不继续清过期，避免只改一半
                    promoted++;
                }
                if (decision.ClearExpiry && store.SetExpiryForMessage(item.MessageId, null))
                    expiriesCleared++;
                if (decision.ExpiresAt is { } expiresAt
                    && store.SetExpiryForMessage(item.MessageId, expiresAt))
                    expiriesSet++;
            }

            var pruned = store.PruneExpired(now);
            var report = new MemoryLifecycleReport(
                started, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                items.Count, promoted, expiriesSet, expiriesCleared, pruned);
            Volatile.Write(ref _lastReport, report);
            if (report.Changed > 0 || pruned > 0)
                _logger.LogInformation(
                    "记忆生命周期完成：扫描 {Scanned}，巩固 {Promoted}，收紧过期 {Set}，清除过期 {Cleared}，清理 {Pruned}",
                    report.Scanned, report.Promoted, report.ExpiriesSet, report.ExpiriesCleared, report.Pruned);
            return report;
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    /// <summary>按时间倒序分页扫描（只读元数据，不取向量）。最多 <paramref name="max"/> 条。</summary>
    private static List<MessageMemoryItem> Scan(IMessageMemoryStore store, int max)
    {
        const int pageSize = 500;
        var items = new List<MessageMemoryItem>(Math.Min(max, pageSize));
        var offset = 0;
        while (items.Count < max)
        {
            var take = Math.Min(pageSize, max - items.Count);
            var chunk = store.ListMessages(null, null, null, take, offset);
            if (chunk.Count == 0) break;
            items.AddRange(chunk);
            offset += chunk.Count;
            if (chunk.Count < take) break;
        }
        return items;
    }

    private Dictionary<string, int> CountRepeats(IReadOnlyList<MessageMemoryItem> items)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            if (!item.SenderType.Equals("Agent", StringComparison.OrdinalIgnoreCase)) continue;
            if (ProfileFor(item.SenderId)?.MemoryType != MemoryPersonalityTypes.SlowToLearn) continue;
            if (MemoryLifecyclePolicy.RepeatKey(item.SenderId, item.Content) is not { } key) continue;
            counts[key] = counts.TryGetValue(key, out var n) ? n + 1 : 1;
        }
        return counts;
    }

    private MemoryProfile? ProfileFor(string agentId)
        => _profiles.GetOrAdd(agentId, id => _catalog.GetDefinition(id)?.MemoryProfile);
}
