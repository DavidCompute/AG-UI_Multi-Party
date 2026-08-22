using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;

namespace AguiGroupChat.Hub.Messaging;

/// <summary>
/// 连接注册表 + 群 → 订阅连接索引。负责连接生命周期与「群组事件扇出目标」的解析。
/// 注册时实施连接数上限：同成员 ≤ <see cref="MaxConnectionsPerMember"/>、同 IP ≤ <see cref="MaxConnectionsPerIp"/>，
/// 超限拒绝（防单账号 / 单 IP 批量建连耗尽连接与内存资源）。
/// </summary>
public sealed class ConnectionManager
{
    /// <summary>同一成员最多活跃连接数。</summary>
    public const int MaxConnectionsPerMember = 10;

    /// <summary>同一客户端 IP 最多活跃连接数。</summary>
    public const int MaxConnectionsPerIp = 50;

    private readonly ILogger<ConnectionManager> _logger;
    private readonly ConcurrentDictionary<string, HubConnection> _connections = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _groupSubscribers = new();
    private readonly ConcurrentDictionary<string, int> _memberConnections = new();
    private readonly ConcurrentDictionary<string, int> _ipConnections = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _ipByConnectionId = new(StringComparer.Ordinal);

    public ConnectionManager(ILogger<ConnectionManager>? logger = null)
    {
        _logger = logger ?? NullLogger<ConnectionManager>.Instance;
    }

    /// <summary>
    /// 注册连接：先做「同成员 ≤ <see cref="MaxConnectionsPerMember"/>、同 IP ≤ <see cref="MaxConnectionsPerIp"/>」
    /// 数量检查，超限返回 false（不注册）；成功返回 true。IP 由调用方传入（WebSocket / SSE 端点取 RemoteIpAddress）。
    /// </summary>
    public bool Register(HubConnection connection, string? clientIp = null)
    {
        var ip = string.IsNullOrWhiteSpace(clientIp) ? null : clientIp.Trim();
        // CAS 递增保证并发下计数不会越过上限（先检查、再原子递增，竞争时重试）
        if (TryIncrementCount(_memberConnections, connection.MemberId, MaxConnectionsPerMember) < 0)
        {
            _logger.LogWarning("拒绝连接：成员 {Member} 活跃连接数已达上限（{Max}）", connection.MemberId, MaxConnectionsPerMember);
            return false;
        }
        if (ip is not null && TryIncrementCount(_ipConnections, ip, MaxConnectionsPerIp) < 0)
        {
            DecrementCount(_memberConnections, connection.MemberId); // 回滚成员计数
            _logger.LogWarning("拒绝连接：IP {Ip} 活跃连接数已达上限（{Max}）", ip, MaxConnectionsPerIp);
            return false;
        }
        _connections[connection.ConnectionId] = connection;
        if (ip is not null) _ipByConnectionId[connection.ConnectionId] = ip;
        return true;
    }

    public void Unregister(string connectionId)
    {
        if (!_connections.TryRemove(connectionId, out var connection)) return;
        foreach (var groupId in connection.SubscribedGroups)
            Unsubscribe(connection, groupId);
        DecrementCount(_memberConnections, connection.MemberId);
        if (_ipByConnectionId.TryRemove(connectionId, out var ip))
            DecrementCount(_ipConnections, ip);
    }

    /// <summary>CAS 递增计数；超过上限返回 -1（不递增），成功返回递增后的值。</summary>
    private static int TryIncrementCount(ConcurrentDictionary<string, int> dict, string key, int max)
    {
        while (true)
        {
            if (dict.TryGetValue(key, out var count))
            {
                if (count >= max) return -1;
                if (dict.TryUpdate(key, count + 1, count)) return count + 1;
            }
            else if (dict.TryAdd(key, 1))
            {
                return 1;
            }
        }
    }

    /// <summary>CAS 递减计数；归零后移除键（并发安全）。</summary>
    private static void DecrementCount(ConcurrentDictionary<string, int> dict, string key)
    {
        while (true)
        {
            if (!dict.TryGetValue(key, out var count) || count <= 0)
            {
                dict.TryRemove(key, out _);
                return;
            }
            if (count == 1)
            {
                if (dict.TryRemove(key, out _)) return;
            }
            else if (dict.TryUpdate(key, count - 1, count))
            {
                return;
            }
        }
    }

    /// <summary>订阅群组（幂等）。</summary>
    public bool Subscribe(HubConnection connection, string groupId)
    {
        connection.Subscribe(groupId);
        _groupSubscribers.GetOrAdd(groupId, _ => new()).TryAdd(connection.ConnectionId, 0);
        return true;
    }

    public bool Unsubscribe(HubConnection connection, string groupId)
    {
        if (!connection.Unsubscribe(groupId)) return false;
        if (_groupSubscribers.TryGetValue(groupId, out var set))
        {
            set.TryRemove(connection.ConnectionId, out _);
            // 空集清理：仅当集合仍为空时移除（TryRemove(KeyValuePair) 按值匹配，
            // 并发期间被重新填充 / 重建的集合不会被误删，安全写法）
            if (set.IsEmpty)
                _groupSubscribers.TryRemove(new KeyValuePair<string, ConcurrentDictionary<string, byte>>(groupId, set));
        }
        return true;
    }

    public IEnumerable<HubConnection> SubscribersOf(string groupId)
    {
        if (!_groupSubscribers.TryGetValue(groupId, out var set)) yield break;
        foreach (var (connectionId, _) in set)
            if (_connections.TryGetValue(connectionId, out var connection))
                yield return connection;
    }

    public HubConnection? Get(string connectionId)
        => _connections.TryGetValue(connectionId, out var c) ? c : null;

    /// <summary>某成员的全部活跃连接（用于发送者回显直连等不依赖订阅索引的场景）。</summary>
    public IReadOnlyList<HubConnection> ConnectionsOf(string memberId)
        => _connections.Values.Where(c => c.MemberId == memberId).ToList();

    /// <summary>某成员当前活跃的连接数（用于在线状态判定）。</summary>
    public int MemberConnectionCount(string memberId)
        => _memberConnections.TryGetValue(memberId, out var c) ? c : 0;

    public int ConnectionCount => _connections.Count;

    public int SubscribedGroupCount => _groupSubscribers.Count;

    public IReadOnlyList<HubConnection> All() => _connections.Values.ToList();
}
