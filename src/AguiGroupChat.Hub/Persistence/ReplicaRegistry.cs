namespace AguiGroupChat.Hub.Persistence;

/// <summary>
/// 一个 Web 副本的近期运行快照（跨副本诊断用）：身份 + 心跳时间 + 业务计数器 + 进程内存。
/// 只放「诊断足够用」的少量字段（不为每副本复制整份趋势环 / 分员工明细）。
/// </summary>
public sealed class ReplicaSnapshot
{
    public string ReplicaId { get; set; } = "";
    public string Host { get; set; } = "";
    public int Pid { get; set; }
    /// <summary>该副本站点接收请求的起始时间（ms）。</summary>
    public long StartedAtMs { get; set; }
    /// <summary>最近一次心跳发布时间（ms）——用于判断副本是否存活 / 陈旧。</summary>
    public long LastSeenMs { get; set; }

    public long Invocations { get; set; }
    public long Accepted { get; set; }
    public long Rejected { get; set; }
    public long BridgeCalls { get; set; }
    public long BridgeFailures { get; set; }
    public long MemoryHits { get; set; }
    public long MemoryEmpty { get; set; }
    public long OutputChars { get; set; }
    public long WorkingSetBytes { get; set; }
    public long GcHeapBytes { get; set; }
}

/// <summary>
/// 本进程的副本身份：稳定标识 = 配置 <c>Observability:ReplicaId</c>（推荐在 K8s 用 pod 名 / 序号）
/// 或回退为「主机名:进程号」。用于跨副本诊断里区分「哪个副本」。
/// </summary>
public sealed class ReplicaIdentity
{
    public string Id { get; }
    public string Host { get; }
    public int Pid { get; }
    public long StartedAtMs { get; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public ReplicaIdentity(string? configuredId = null)
    {
        Host = SafeHostName();
        Pid = Environment.ProcessId;
        Id = string.IsNullOrWhiteSpace(configuredId) ? $"{Host}:{Pid}" : configuredId!.Trim();
    }

    private static string SafeHostName()
    {
        try { return Environment.MachineName; } catch { return "unknown"; }
    }
}

/// <summary>
/// 副本注册表：发布自身快照 + 列出活跃副本。
/// Redis 多副本（6.2 / 6.1 跨副本诊断）实现跨进程共享；其余存储提供方回退为「仅自身」。
/// </summary>
public interface IReplicaRegistry
{
    /// <summary>本副本身份 id。</summary>
    string SelfId { get; }

    /// <summary>发布（覆盖）自身快照。</summary>
    void Publish(ReplicaSnapshot snapshot);

    /// <summary>列出当前活跃副本（best-effort，失败返回空）。</summary>
    IReadOnlyList<ReplicaSnapshot> List();
}

/// <summary>
/// 单副本注册表（memory / sqlite / postgres / mysql 部署）：只登记自身，列表即自身最近一次快照。
/// 这样「跨副本诊断」界面在单副本下也自洽（显示 1 个副本），无需特判。
/// </summary>
public sealed class LocalReplicaRegistry : IReplicaRegistry
{
    private readonly ReplicaIdentity _self;
    private volatile ReplicaSnapshot? _last;

    public LocalReplicaRegistry(ReplicaIdentity self) => _self = self;

    public string SelfId => _self.Id;

    public void Publish(ReplicaSnapshot snapshot) => _last = snapshot;

    public IReadOnlyList<ReplicaSnapshot> List() => _last is null ? [] : [_last];
}
