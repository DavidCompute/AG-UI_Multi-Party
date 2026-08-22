using AguiGroupChat.Hub.Options;
using AguiGroupChat.Hub.Storage;
using Microsoft.Extensions.Logging;

namespace AguiGroupChat.Hub.Persistence;

/// <summary>
/// 消息保留策略（数据自动清理）：按 <see cref="GroupChatOptions.MessageRetentionDays"/> 每天检查一次，
/// 物理删除超过保留天数的历史消息（按消息时间戳；群 / 成员 / 话题结构保留）。
/// 与记忆自动遗忘（Agents:Memory:RetentionDays）独立：本服务只清理消息表。
/// 配置为 0（默认）不启用。
/// </summary>
public sealed class MessageRetentionService : IDisposable
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

    private readonly IGroupStore _store;
    private readonly GroupChatOptions _options;
    private readonly ILogger<MessageRetentionService> _logger;
    private Timer? _timer;

    public MessageRetentionService(IGroupStore store, GroupChatOptions options, ILogger<MessageRetentionService> logger)
    {
        _store = store;
        _options = options;
        _logger = logger;
    }

    /// <summary>启动周期检查（应用就绪后调用；幂等）。首轮延迟 1 分钟（避开启动峰值），之后每 24 小时。</summary>
    public void Start()
    {
        if (_options.MessageRetentionDays <= 0)
        {
            _logger.LogInformation("消息保留策略未启用（GroupChat:MessageRetentionDays=0，不清理历史消息）");
            return;
        }
        lock (this)
        {
            if (_timer is not null) return;
            _timer = new Timer(_ => RunOnce(), null, TimeSpan.FromMinutes(1), CheckInterval);
            _logger.LogInformation("消息保留策略已启用：超过 {Days} 天的历史消息每天清理一次", _options.MessageRetentionDays);
        }
    }

    public void Stop() => Dispose();

    /// <summary>立即执行一次清理（供测试 / 手动触发）。</summary>
    public int RunOnce()
    {
        if (_options.MessageRetentionDays <= 0) return 0;
        var cutoff = DateTimeOffset.UtcNow.AddDays(-_options.MessageRetentionDays).ToUnixTimeMilliseconds();
        try
        {
            var removed = _store.DeleteMessagesBefore(cutoff);
            if (removed > 0)
                _logger.LogInformation("消息保留清理完成：删除 {Count} 条超过 {Days} 天的历史消息", removed, _options.MessageRetentionDays);
            return removed;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "消息保留清理失败（下次周期自动重试）");
            return 0;
        }
    }

    public void Dispose()
    {
        lock (this)
        {
            _timer?.Dispose();
            _timer = null;
        }
    }
}
