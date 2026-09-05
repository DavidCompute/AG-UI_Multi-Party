using System.Collections.Concurrent;

namespace AguiGroupChat.Hub.Messaging;

/// <summary>
/// 一条客户端连接（WebSocket / SSE / 测试替身）。
/// Sender 把序列化好的 JSON 事件投递到该连接的发送通道。
/// </summary>
public sealed class HubConnection
{
    public required string ConnectionId { get; init; }

    /// <summary>身份：连接建立时携带的 memberId。</summary>
    public required string MemberId { get; init; }

    /// <summary>传输类型：websocket / sse / test。</summary>
    public required string Transport { get; init; }

    public required Func<string, CancellationToken, Task> Sender { get; init; }

    /// <summary>服务端取消源：吊销 / 禁用 / 改密等会话失效事件用它主动终止本连接（由端点设置，缺省为空=测试替身不断连）。</summary>
    public CancellationTokenSource? AbortSource { get; set; }

    private readonly ConcurrentDictionary<string, byte> _subscribedGroups = new();

    public bool IsSubscribed(string groupId) => _subscribedGroups.ContainsKey(groupId);

    /// <summary>订阅群组（幂等），返回是否首次订阅。</summary>
    public bool Subscribe(string groupId) => _subscribedGroups.TryAdd(groupId, 0);

    public bool Unsubscribe(string groupId) => _subscribedGroups.TryRemove(groupId, out _);

    public IReadOnlyList<string> SubscribedGroups => _subscribedGroups.Keys.ToList();

    public Task SendAsync(string json, CancellationToken ct) => Sender(json, ct);
}
