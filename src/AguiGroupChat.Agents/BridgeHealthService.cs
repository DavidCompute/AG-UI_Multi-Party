using System.Collections.Concurrent;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace AguiGroupChat.Agents;

/// <summary>
/// 外部 AG-UI 桥接端点<b>健康度**（3.1）：对配置了桥接（BridgeEndpoint 或全局 AguiBridge.Endpoint）
/// 的智能体，周期性做 TCP 连通性探测并缓存状态，供管理员控制台（/ag-ui/admin/bridge-health）查看。
/// 只读探测，不改变业务；探测失败不影响智能体运行（调用时网关仍会尝试并报错回灌）。
/// </summary>
public sealed class BridgeHealthService : IDisposable
{
    private readonly AgentCatalog _catalog;
    private readonly AgentOptions _options;
    private readonly ILogger<BridgeHealthService> _logger;
    private Timer? _timer;

    /// <summary>agentId → 最近一次探测结果。</summary>
    private readonly ConcurrentDictionary<string, EndpointHealth> _health = new(StringComparer.Ordinal);

    public BridgeHealthService(AgentCatalog catalog, AgentOptions options, ILogger<BridgeHealthService> logger)
    {
        _catalog = catalog;
        _options = options;
        _logger = logger;
    }

    /// <summary>单条端点健康状态。</summary>
    public sealed record EndpointHealth(string AgentId, string Endpoint, bool Up, long? LatencyMs, string Detail, long CheckedAt);

    /// <summary>启动周期性探测（默认每 60 秒；应用就绪后调用）。</summary>
    public void Start(int intervalSeconds = 60)
    {
        lock (this)
        {
            if (_timer is not null) return;
            _timer = new Timer(_ => _ = ProbeAllAsync(CancellationToken.None), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(intervalSeconds));
        }
    }

    public void Stop()
    {
        lock (this) { _timer?.Dispose(); _timer = null; }
    }

    /// <summary>手工触发一次全量探测，返回最新状态列表。</summary>
    public async Task<IReadOnlyList<EndpointHealth>> ProbeAllAsync(CancellationToken ct)
    {
        var endpoints = ResolveEndpoints();
        var tasks = endpoints.Select(e => ProbeAsync(e.AgentId, e.Endpoint, ct));
        await Task.WhenAll(tasks);
        return _health.Values.OrderBy(h => h.AgentId).ToList();
    }

    /// <summary>返回缓存的全部健康状态（未探测过的不包含）。</summary>
    public IReadOnlyList<EndpointHealth> GetStatus()
        => _health.Values.OrderBy(h => h.AgentId).ToList();

    private IReadOnlyList<(string AgentId, string Endpoint)> ResolveEndpoints()
    {
        var list = new List<(string, string)>();
        // 全局默认端点（合成 agentId = 全局）
        if (!string.IsNullOrWhiteSpace(_options.AguiBridge?.Endpoint))
            list.Add(("__global__", _options.AguiBridge!.Endpoint.Trim()));
        // 各智能体显式桥接端点
        foreach (var def in _catalog.ListDefinitions())
        {
            if (!string.IsNullOrWhiteSpace(def.BridgeEndpoint))
                list.Add((def.AgentId, def.BridgeEndpoint.Trim()));
        }
        return list.Distinct().ToList();
    }

    private async Task ProbeAsync(string agentId, string endpoint, CancellationToken ct)
    {
        var checkedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Host.Length == 0)
            {
                Set(agentId, endpoint, false, null, "URL 非法", checkedAt);
                return;
            }
            var port = uri.IsDefaultPort ? (uri.Scheme is "https" or "wss" ? 443 : 80) : uri.Port;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            using var client = new TcpClient(uri.Host, port); // 构造函数同步连接；这里简单 TCP 连通性
            sw.Stop();
            Set(agentId, endpoint, true, sw.ElapsedMilliseconds, $"TCP 可达（{uri.Host}:{port}）", checkedAt);
        }
        catch (Exception ex)
        {
            sw.Stop();
            var detail = ex is SocketException
                ? "连接失败：" + ToFriendly(ex)
                : "探测失败：" + ex.Message;
            Set(agentId, endpoint, false, sw.ElapsedMilliseconds, detail, checkedAt);
        }
    }

    private static string ToFriendly(Exception ex)
    {
        if (ex.Message.Contains("No such host", StringComparison.OrdinalIgnoreCase)) return "域名无法解析";
        if (ex.Message.Contains("refused", StringComparison.OrdinalIgnoreCase)) return "连接被拒绝";
        if (ex.Message.Contains("timed out", StringComparison.OrdinalIgnoreCase)) return "连接超时";
        return ex.Message;
    }

    private void Set(string agentId, string endpoint, bool up, long? latencyMs, string detail, long checkedAt)
    {
        _health[agentId] = new EndpointHealth(agentId, endpoint, up, latencyMs, detail, checkedAt);
        _logger.LogInformation("桥接端点健康度：{AgentId} → {Endpoint} @ {Up}（{Latency}ms）", agentId, endpoint, up ? "UP" : "DOWN", latencyMs?.ToString() ?? "-");
    }

    public void Dispose() => Stop();
}
