using AguiGroupChat.Hub.Agents;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AguiGroupChat.Agents;

/// <summary>
/// 语义记忆维护服务（自动遗忘策略的执行者）：由宿主自动启动，定时调用
/// <see cref="IMessageMemory.PruneExpired"/> 物理清理已过期记忆（首次 5 分钟后、此后每小时一次），
/// 并记录清理数量日志。记忆未启用（IMessageMemory 为 null 占位）时内部跳过，不启动定时器。
/// </summary>
public sealed class MemoryMaintenanceService : IHostedService, IDisposable
{
    private readonly IMessageMemory? _memory;
    private readonly ILogger<MemoryMaintenanceService> _logger;
    private Timer? _timer;

    public MemoryMaintenanceService(IMessageMemory? memory, ILogger<MemoryMaintenanceService> logger)
    {
        _memory = memory;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_memory is null) return Task.CompletedTask; // 记忆未启用（null 占位）：不启动维护
        _timer = new Timer(_ => RunOnce(), null, TimeSpan.FromMinutes(5), TimeSpan.FromHours(1));
        _logger.LogDebug("语义记忆维护已启动（自动遗忘：每小时清理过期记忆）");
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
            var pruned = _memory!.PruneExpired();
            if (pruned > 0)
                _logger.LogInformation("自动遗忘：已清理 {Count} 条过期记忆", pruned);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "自动遗忘定时清理异常");
        }
    }

    public void Dispose() => _timer?.Dispose();
}
