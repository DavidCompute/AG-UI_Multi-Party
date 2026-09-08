using System.Collections.Concurrent;
using AguiGroupChat.Hub.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AguiGroupChat.Agents;

/// <summary>一行自动沉淀状态（按群：目标知识库 + 已沉淀到的时间水位，用于重启去重）。</summary>
public sealed record AutoConsolidationRow(string GroupId, string KbId, long WatermarkMs);

/// <summary>
/// 重要结论「自动周期沉淀」（记忆治理深化）：按配置间隔把各知聚<b>新增</b>的「关键」级记忆
/// 自动聚合为知识文档，写入该知聚专属知识库（自动创建、群成员可读可绑定）。
/// 每条成功写入都会推进该群的时间水位（经扩展区「autoMemoryConsolidation」跨重启保持），
/// 因此不会重复沉淀同一批结论，也不会因重启丢失水位。
/// 记忆未启用 / 自动沉淀关闭 / 无可写目标时静默跳过，不影响群聊主流程。
/// </summary>
public sealed class MemoryAutoConsolidationService : IHostedService, IDisposable
{
    private readonly IGroupStore _groups;
    private readonly KnowledgeBaseCatalog _kbs;
    private readonly AgentOptions _options;
    private readonly IServiceProvider _services;
    private readonly ILogger<MemoryAutoConsolidationService> _logger;
    private readonly ConcurrentDictionary<string, AutoConsolidationRow> _state = new(StringComparer.Ordinal);
    private Timer? _timer;

    public MemoryAutoConsolidationService(
        IGroupStore groups,
        KnowledgeBaseCatalog kbs,
        AgentOptions options,
        IServiceProvider services,
        ILogger<MemoryAutoConsolidationService> logger)
    {
        _groups = groups;
        _kbs = kbs;
        _options = options;
        _services = services;
        _logger = logger;
    }

    // ================= IHostedService =================

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Memory.Enabled || !_options.Memory.AutoConsolidateEnabled) return Task.CompletedTask;
        var hours = Math.Clamp(_options.Memory.AutoConsolidateIntervalHours, 1, 24 * 30);
        // 首轮给应用一段稳定运行时间（10 分钟），此后按配置间隔周期执行
        _timer = new Timer(_ => RunOnce(), null, TimeSpan.FromMinutes(10), TimeSpan.FromHours(hours));
        _logger.LogInformation("重要结论自动周期沉淀已启动：每 {Hours} 小时一轮", hours);
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
        try { RunSweepAsync(CancellationToken.None).GetAwaiter().GetResult(); }
        catch (Exception ex) { _logger.LogWarning(ex, "重要结论自动周期沉淀异常（下轮重试）"); }
    }

    // ================= 状态（扩展区持久化） =================

    public object SnapshotState()
        => _state.Values.OrderBy(r => r.GroupId, StringComparer.Ordinal)
            .Select(r => new AutoConsolidationRow(r.GroupId, r.KbId, r.WatermarkMs)).ToList();

    public void RestoreState(IReadOnlyList<AutoConsolidationRow>? rows)
    {
        _state.Clear();
        if (rows is null) return;
        foreach (var row in rows)
        {
            if (!string.IsNullOrWhiteSpace(row.GroupId) && !string.IsNullOrWhiteSpace(row.KbId))
                _state[row.GroupId] = row;
        }
    }

    // ================= 执行 =================

    /// <summary>手动触发一轮全量扫描（测试 / 运维排障用）。返回本次成功沉淀的知聚数。</summary>
    public async Task<int> RunSweepAsync(CancellationToken ct)
    {
        if (!_options.Memory.Enabled || !_options.Memory.AutoConsolidateEnabled) return 0;
        var memoryStore = _services.GetService(typeof(AguiGroupChat.Hub.Persistence.IMessageMemoryStore)) as AguiGroupChat.Hub.Persistence.IMessageMemoryStore;
        if (memoryStore is null) return 0;

        var succeeded = 0;
        foreach (var group in _groups.AllGroups())
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                if (await SweepGroupAsync(group, memoryStore, ct)) succeeded++;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "自动沉淀失败（跳过该群）：group={GroupId}", group.GroupId);
            }
        }
        if (succeeded > 0)
            _logger.LogInformation("重要结论自动周期沉淀完成：{Count} 个知聚写入新文档", succeeded);
        return succeeded;
    }

    private async Task<bool> SweepGroupAsync(
        AguiGroupChat.Hub.Models.Group group,
        AguiGroupChat.Hub.Persistence.IMessageMemoryStore memoryStore,
        CancellationToken ct)
    {
        var gid = group.GroupId;
        var row = EnsureGroupTarget(group);
        if (row is null) return false;
        var since = row.WatermarkMs > 0 ? row.WatermarkMs : (long?)null;
        var result = await _kbs.ConsolidateGroupMemoriesSinceAsync(gid, row.KbId, memoryStore, since, ct);
        if (result.Error is not null)
        {
            _logger.LogDebug("自动沉淀被跳过（group={GroupId}）：{Reason}", gid, result.Error);
            return false;
        }
        if (result.Doc is null)
            return false; // 无新增关键记忆，水位不变
        if (result.WatermarkMs is { } wm && wm > row.WatermarkMs)
            _state.TryUpdate(gid, row with { WatermarkMs = wm }, row);
        _logger.LogInformation("自动沉淀已写入文档（group={GroupId} kb={KbId} doc={Doc} memories={Count}）",
            gid, row.KbId, result.Doc.DocId, result.MemoryCount);
        return true;
    }

    /// <summary>为群取得 / 创建专属自动沉淀知识库，并登记水位行（并发安全：TryGetValue 后 AddOrUpdate）。</summary>
    private AutoConsolidationRow? EnsureGroupTarget(AguiGroupChat.Hub.Models.Group group)
    {
        if (_state.TryGetValue(group.GroupId, out var existing))
        {
            // 外部可能手动删除了该知识库：存在但已失效 → 重建
            return _kbs.GetKb(existing.KbId) is not null
                ? existing
                : CreateTarget(group);
        }
        return CreateTarget(group);
    }

    private AutoConsolidationRow CreateTarget(AguiGroupChat.Hub.Models.Group group)
    {
        var name = $"自动沉淀·{group.GroupName}";
        var kb = _kbs.CreateKb(name, $"由系统按配置周期自动沉淀本知聚「关键」级记忆；群成员可查看并绑定检索。", group.OwnerId);
        kb.SharedGroupIds.Add(group.GroupId); // 开放给群成员只读 / 绑定
        kb.Description = "由系统按配置周期自动沉淀本知聚「关键」级记忆；群成员可查看并绑定检索。";
        var row = new AutoConsolidationRow(group.GroupId, kb.KbId, 0);
        _state[group.GroupId] = row;
        _logger.LogInformation("已为知聚创建自动沉淀知识库：group={GroupId} kb={KbId}", group.GroupId, kb.KbId);
        return row;
    }
}
