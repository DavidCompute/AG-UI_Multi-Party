using System.Collections.Concurrent;
using System.Text.Json;
using AguiGroupChat.Hub.Infra;
using AguiGroupChat.Hub.Persistence.Redis;
using StackExchange.Redis;

namespace AguiGroupChat.Hub.Users;

/// <summary>登录会话（内部存储模型）。令牌哈希为字典/Redis 键。</summary>
public sealed class UserSession
{
    public required string TokenHash { get; init; }
    public required string UserId { get; init; }
    public string SessionId { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset IssuedAt { get; set; }
    /// <summary>滑动续期时长（表示续期策略，非绝对有效期）。</summary>
    public TimeSpan Ttl { get; init; }
}

/// <summary>
/// 登录会话存储抽象。默认实现为进程内（<see cref="InMemorySessionStore"/>, 6.2 中 memory 模式）；
/// Redis 模式提供 <see cref="RedisSessionStore"/> 使多副本共享同一批会话（一台登录、各副本即可用），
/// 支撑 6.2 Web 多副本横向扩展。
/// </summary>
public interface ISessionStore
{
    UserSession? TryGet(string tokenHash);
    void Upsert(UserSession session, TimeSpan ttl);
    bool Remove(string tokenHash);
    IReadOnlyList<UserSession> All();
    void Clear();
}

/// <summary>进程内登录会话存储（默认 memory 模式）。</summary>
public sealed class InMemorySessionStore : ISessionStore
{
    private readonly ConcurrentDictionary<string, UserSession> _sessions = new(StringComparer.Ordinal);

    public UserSession? TryGet(string tokenHash) => _sessions.TryGetValue(tokenHash, out var s) ? s : null;
    public void Upsert(UserSession session, TimeSpan ttl) => _sessions[session.TokenHash] = session;
    public bool Remove(string tokenHash) => _sessions.TryRemove(tokenHash, out _);
    public IReadOnlyList<UserSession> All() => _sessions.Values.ToList();
    public void Clear() => _sessions.Clear();
}

/// <summary>
/// Redis 登录会话存储（6.2 多副本共享）：每个会话写 <c>agui:sessions:{tokenHash}</c> hash
/// （字段 userId / sessionId / expiresAtMs / issuedAtMs / ttlMs），带 TTL（过期自动剔除）；
/// 多副本读写同一批 key 使会话跨副本一致。
/// </summary>
public sealed class RedisSessionStore : ISessionStore
{
    private static readonly string SessionPrefix = "agui:sessions:";
    private readonly RedisContext _ctx;

    public RedisSessionStore(RedisContext ctx) => _ctx = ctx;

    public UserSession? TryGet(string tokenHash)
    {
        var values = _ctx.Db.HashGetAll(SessionKey(tokenHash));
        if (values.Length == 0) return null;
        return FromHash(tokenHash, values);
    }

    public void Upsert(UserSession session, TimeSpan ttl)
    {
        // 用 ExpiresAt - 现在 折算剩余 TTL（滑动续期后 ExpiresAt 已前移）；Redis TTL 最小 1 秒
        var remainingMs = Math.Max(1000, (long)(session.ExpiresAt - DateTimeOffset.Now).TotalMilliseconds);
        var key = SessionKey(session.TokenHash);
        _ctx.Db.HashSet(key, new HashEntry[]
        {
            new("userId", session.UserId),
            new("sessionId", session.SessionId),
            new("expiresAtMs", session.ExpiresAt.ToUnixTimeMilliseconds()),
            new("issuedAtMs", session.IssuedAt.ToUnixTimeMilliseconds()),
            new("ttlMs", remainingMs),
        });
        _ctx.Db.KeyExpire(key, TimeSpan.FromMilliseconds(remainingMs));
    }

    public bool Remove(string tokenHash) => _ctx.Db.KeyDelete(SessionKey(tokenHash));

    public IReadOnlyList<UserSession> All()
    {
        var server = _ctx.Mux.GetServer(_ctx.Mux.GetEndPoints()[0]);
        var result = new List<UserSession>();
        foreach (var key in server.Keys(pattern: SessionPrefix + "*"))
        {
            var tokenHash = key.ToString()[SessionPrefix.Length..];
            var values = _ctx.Db.HashGetAll(key);
            if (values.Length > 0) result.Add(FromHash(tokenHash, values));
        }
        return result;
    }

    public void Clear()
    {
        var server = _ctx.Mux.GetServer(_ctx.Mux.GetEndPoints()[0]);
        var keys = server.Keys(pattern: SessionPrefix + "*").ToArray();
        if (keys.Length > 0) _ctx.Db.KeyDelete(keys);
    }

    private static RedisKey SessionKey(string tokenHash) => SessionPrefix + tokenHash;

    private static UserSession FromHash(string tokenHash, HashEntry[] values)
    {
        return new UserSession
        {
            TokenHash = tokenHash,
            UserId = Field(values, "userId") ?? "",
            SessionId = Field(values, "sessionId") ?? "",
            ExpiresAt = DateTimeOffset.FromUnixTimeMilliseconds(FieldLong(values, "expiresAtMs")),
            IssuedAt = DateTimeOffset.FromUnixTimeMilliseconds(FieldLong(values, "issuedAtMs")),
            Ttl = TimeSpan.FromMilliseconds(FieldLong(values, "ttlMs")),
        };
    }

    private static string? Field(HashEntry[] fields, string name)
    {
        var e = Array.Find(fields, f => f.Name == name);
        return e.Name.IsNull || e.Value.IsNullOrEmpty ? null : e.Value.ToString();
    }

    private static long FieldLong(HashEntry[] fields, string name)
    {
        var e = Array.Find(fields, f => f.Name == name);
        return e.Name.IsNull || e.Value.IsNullOrEmpty ? 0 : (long)e.Value;
    }
}
