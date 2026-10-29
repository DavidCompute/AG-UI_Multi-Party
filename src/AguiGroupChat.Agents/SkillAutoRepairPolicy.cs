using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AguiGroupChat.Agents;

/// <summary>一次自动修复的门控结果（1.0.360，纯数据）。</summary>
/// <param name="Allowed">是否允许此刻自动修复。</param>
/// <param name="Reason"><c>ok</c> / <c>cooldown</c>（人工或自动刚修过）/ <c>daily-cap</c>（当日自动修复次数已满）。</param>
public sealed record SkillAutoRepairDecision(bool Allowed, string Reason);

/// <summary>
/// 自动修复门控（1.0.360，<b>纯函数</b>）。
///
/// <para>
/// 闭环告警说“这个技能需要人看”，自动修复只是先把最便宜的一步做掉：再试一次模型改写。
/// 为了不让它变成刷模型费用的循环，两道闸：
/// </para>
/// <list type="bullet">
///   <item><c>cooldown</c>：该技能最近一次**自动**修复在冷却窗口内 → 不修。只卡自动这条线，
///         人工修复不受影响（管理员显式点修复总该执行）。</item>
///   <item><c>daily-cap</c>：最近 24 小时的自动修复次数达到上限 → 不修，留给人工。</item>
/// </list>
/// </summary>
public static class SkillAutoRepairPolicy
{
    public const int DefaultCooldownMinutes = 60;
    public const int DefaultMaxPerDay = 3;
    private const long DayMs = 24 * 60 * 60 * 1000;

    public static SkillAutoRepairDecision Evaluate(
        IReadOnlyList<SkillLoopEvent>? events,
        long nowMs,
        int cooldownMinutes = DefaultCooldownMinutes,
        int maxPerDay = DefaultMaxPerDay)
    {
        var list = (events ?? []).Where(e => e.TimestampMs <= nowMs).ToList();
        var autos = list
            .Where(e => string.Equals(e.Source, SkillRepairSources.Auto, StringComparison.Ordinal))
            .ToList();
        if (autos.Count == 0) return new SkillAutoRepairDecision(true, "ok");

        var cooldownMs = Math.Max(0, cooldownMinutes) * 60_000L;
        var lastAuto = autos.Max(e => e.TimestampMs);
        if (cooldownMs > 0 && nowMs - lastAuto < cooldownMs)
            return new SkillAutoRepairDecision(false, "cooldown");

        var cap = maxPerDay <= 0 ? int.MaxValue : maxPerDay;
        var autoToday = autos.Count(e => nowMs - e.TimestampMs < DayMs);
        if (autoToday >= cap) return new SkillAutoRepairDecision(false, "daily-cap");

        return new SkillAutoRepairDecision(true, "ok");
    }
}

/// <summary>一条待自动修复的任务（1.0.360）。</summary>
public sealed record SkillAutoRepairJob(string SkillId, string Reason, long QueuedAtMs);

/// <summary>
/// 自动修复队列（1.0.360）：有界（64），满了丢最旧的——自动修复是尽力而为，不能反压主流程。
/// </summary>
public sealed class SkillAutoRepairQueue
{
    private readonly Channel<SkillAutoRepairJob> _channel =
        Channel.CreateBounded<SkillAutoRepairJob>(new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

    private int _queued;
    private int _processed;

    /// <summary>累计入队数（供看板显示）。</summary>
    public int Queued => Volatile.Read(ref _queued);

    /// <summary>累计处理数（含因门控被跳过的）。</summary>
    public int Processed => Volatile.Read(ref _processed);

    public bool TryEnqueue(string skillId, string reason)
    {
        if (string.IsNullOrWhiteSpace(skillId)) return false;
        Interlocked.Increment(ref _queued);
        return _channel.Writer.TryWrite(
            new SkillAutoRepairJob(skillId, reason, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
    }

    internal ChannelReader<SkillAutoRepairJob> Reader => _channel.Reader;

    internal void MarkProcessed() => Interlocked.Increment(ref _processed);
}

/// <summary>
/// 自动修复工作线程（1.0.360）：闭环告警命中后，在后台按门控再试一次修复。
///
/// <para>
/// 只处理<b>非内置</b>（<c>OwnerId != null</c>）且服务端可自测的技能——内置技能由发布流程维护，
/// 本机执行的技能在服务端根本跑不了自测。修复结果照样写闭环事件，因此会再经
/// <see cref="ISkillLoopObserver"/> 走一次告警判定；门控（冷却 + 每日上限）保证不会无限重试。
/// </para>
/// </summary>
public sealed class SkillAutoRepairWorker : BackgroundService
{
    private const long LookbackMs = 7 * 24 * 60 * 60 * 1000;

    private readonly SkillAutoRepairQueue _queue;
    private readonly IServiceProvider _services;
    private readonly AgentSkillCatalog _catalog;
    private readonly AgentCatalog _agents;
    private readonly AgentOptions _options;
    private readonly SkillLoopEventStore _loop;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;

    public SkillAutoRepairWorker(
        SkillAutoRepairQueue queue,
        IServiceProvider services,
        AgentSkillCatalog catalog,
        AgentCatalog agents,
        AgentOptions options,
        SkillLoopEventStore loop,
        ILoggerFactory loggerFactory)
    {
        _queue = queue;
        _services = services;
        _catalog = catalog;
        _agents = agents;
        _options = options;
        _loop = loop;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger("SkillAutoRepair");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await RunOneAsync(job, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "自动修复失败（忽略）：skill={SkillId}", job.SkillId);
            }
            finally
            {
                _queue.MarkProcessed();
            }
        }
    }

    private async Task RunOneAsync(SkillAutoRepairJob job, CancellationToken ct)
    {
        var options = _options.ArtifactQuality;
        if (!options.AutoRepairEnabled) return;

        var skill = _catalog.Get(job.SkillId);
        if (skill is null || skill.OwnerId is null) return;
        if (skill.ExecutionLocation == AgentSkillExecutionLocation.Client) return;
        // 人工暂停是人的决定：自动修复不得越过它（只处理产物质量自动隔离 / 未暂停）。
        if (!SkillSuspension.MayAutoRepair(skill))
        {
            _logger.LogInformation("自动修复跳过：技能被人工暂停（{Reason}）skill={SkillId}", skill.SuspendedReason, job.SkillId);
            return;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var history = _loop.Query(now - LookbackMs)
            .Where(e => string.Equals(e.SkillId, job.SkillId, StringComparison.Ordinal))
            .ToList();
        var decision = SkillAutoRepairPolicy.Evaluate(
            history, now, options.AutoRepairCooldownMinutes, options.AutoRepairMaxPerDay);
        if (!decision.Allowed)
        {
            _logger.LogDebug("自动修复跳过：skill={SkillId} reason={Reason}", job.SkillId, decision.Reason);
            return;
        }

        _logger.LogInformation("自动修复开始：skill={SkillId} trigger={Reason}", job.SkillId, job.Reason);
        var run = await SkillRepairRunner.RunAsync(
            skill, _catalog, _agents, _options, _loggerFactory,
            _services.GetService(typeof(SkillRegressionCaseStore)) as SkillRegressionCaseStore,
            _services.GetService(typeof(SkillLessonStore)) as SkillLessonStore,
            _loop,
            _services.GetService(typeof(ISkillLoopObserver)) as ISkillLoopObserver,
            apply: true, resume: null, source: SkillRepairSources.Auto, ct);
        _logger.LogInformation(
            "自动修复结束：skill={SkillId} outcome={Outcome} applied={Applied} rolledBack={RolledBack} ms={Ms}",
            job.SkillId, run.Outcome, run.Applied, run.RolledBack, run.DurationMs);
    }
}
