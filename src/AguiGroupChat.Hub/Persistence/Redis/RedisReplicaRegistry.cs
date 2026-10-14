using StackExchange.Redis;

namespace AguiGroupChat.Hub.Persistence.Redis;

/// <summary>
/// Redis 副本注册表（6.1 跨副本诊断 / 6.2 多副本）：每个副本周期性把自身快照写入
/// <c>agui:replica:{id}</c>（TTL 60s，心跳 15s → 约 4 次漏发后自动消失），并把 id 登记到集合
/// <c>agui:replicas</c>；读取时顺带清理已过期的索引项，因此无需额外的 GC 任务。
/// 任一副本都能通过它看到**全部活跃副本**，从而做跨副本对比 / 定位异常副本。
/// </summary>
public sealed class RedisReplicaRegistry : IReplicaRegistry
{
    /// <summary>快照 TTL（秒）。心跳间隔应显著小于它（见 ReplicaMetricsPublisher）。</summary>
    public const int TtlSeconds = 60;

    private readonly RedisContext _ctx;
    private readonly ReplicaIdentity _self;
    private readonly ILogger<RedisReplicaRegistry> _logger;

    public RedisReplicaRegistry(RedisContext ctx, ReplicaIdentity self, ILogger<RedisReplicaRegistry> logger)
    {
        _ctx = ctx;
        _self = self;
        _logger = logger;
    }

    public string SelfId => _self.Id;

    public void Publish(ReplicaSnapshot snapshot)
    {
        try
        {
            var db = _ctx.Db;
            db.StringSet(RedisContext.ReplicaKey(snapshot.ReplicaId), RedisContext.Serialize(snapshot),
                TimeSpan.FromSeconds(TtlSeconds));
            db.SetAdd(RedisContext.ReplicaIndexKey, snapshot.ReplicaId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "发布副本快照到 Redis 失败（副本 {ReplicaId}）", snapshot.ReplicaId);
        }
    }

    public IReadOnlyList<ReplicaSnapshot> List()
    {
        try
        {
            var db = _ctx.Db;
            var ids = db.SetMembers(RedisContext.ReplicaIndexKey);
            if (ids.Length == 0) return [];
            var result = new List<ReplicaSnapshot>(ids.Length);
            foreach (var id in ids)
            {
                if (id.IsNullOrEmpty) continue;
                var value = db.StringGet(RedisContext.ReplicaKey(id!));
                if (value.IsNullOrEmpty)
                {
                    // 快照已过期（副本下线 / 卡死）：顺带把索引里的死项清掉
                    db.SetRemove(RedisContext.ReplicaIndexKey, id);
                    continue;
                }
                var snapshot = RedisContext.Deserialize<ReplicaSnapshot>(value);
                if (snapshot is not null) result.Add(snapshot);
            }
            return result.OrderBy(r => r.ReplicaId, StringComparer.Ordinal).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "从 Redis 读取副本快照失败");
            return [];
        }
    }
}
