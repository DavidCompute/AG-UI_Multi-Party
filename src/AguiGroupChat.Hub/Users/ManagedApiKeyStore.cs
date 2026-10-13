using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace AguiGroupChat.Hub.Users;

/// <summary>
/// 一条托管 API 密钥（管理员在控制台创建，用于外部系统程序化接入）。
/// 明文密钥只在创建时返回一次；此处只保存 <see cref="KeyHash"/>（SHA-256，密钥为高熵随机值，无需加盐拉伸）。
/// </summary>
public sealed class ManagedApiKey
{
    public string Id { get; set; } = "";

    /// <summary>展示名（如「n8n 集成」）。</summary>
    public string Name { get; set; } = "";

    /// <summary>绑定的账号用户名（继承其群成员 / 权限 / 管理员标记）。</summary>
    public string Username { get; set; } = "";

    /// <summary>密钥前缀（形如 <c>ak_xxxxxxxx…</c>），仅用于界面辨识，不含足够信息反推密钥。</summary>
    public string KeyPrefix { get; set; } = "";

    /// <summary>密钥明文的 SHA-256（十六进制小写）。</summary>
    public string KeyHash { get; set; } = "";

    public long CreatedAt { get; set; }

    /// <summary>创建者 userId。</summary>
    public string? CreatedBy { get; set; }

    /// <summary>最近一次使用时间（毫秒），仅信息性（不保证每请求都刷新）。</summary>
    public long? LastUsedAt { get; set; }
}

/// <summary>
/// 托管 API 密钥存储：管理员经控制台创建 / 吊销，<see cref="AuthService.ResolveApiKey"/> 据此鉴权。
/// 进程内保存并在变更时通知持久化（扩展区「apiKeys」）。明文永不落盘，只存哈希。
/// </summary>
public sealed class ManagedApiKeyStore
{
    private readonly ConcurrentDictionary<string, ManagedApiKey> _keys = new(StringComparer.Ordinal);

    /// <summary>LastUsedAt 刷新节流：同一密钥 60 秒内不重复写（避免每请求都触发持久化）。</summary>
    private const long TouchThrottleMs = 60_000;

    public IReadOnlyList<ManagedApiKey> List() => _keys.Values.OrderByDescending(k => k.CreatedAt).ToList();

    public ManagedApiKey? Get(string id) => _keys.TryGetValue(id, out var k) ? k : null;

    public void Add(ManagedApiKey key) => _keys[key.Id] = key;

    public bool Remove(string id) => _keys.TryRemove(id, out _);

    public void ReplaceAll(IEnumerable<ManagedApiKey> keys)
    {
        _keys.Clear();
        foreach (var k in keys) _keys[k.Id] = k;
    }

    /// <summary>
    /// 按密钥明文匹配（比较哈希，恒定时间）；命中则刷新 <see cref="ManagedApiKey.LastUsedAt"/>（节流）并返回该条，未命中返回 null。
    /// </summary>
    public ManagedApiKey? Match(string? token, long nowMs)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var hash = Hash(token.Trim());
        foreach (var k in _keys.Values)
        {
            if (string.IsNullOrEmpty(k.KeyHash)) continue;
            if (!FixedTimeEquals(k.KeyHash, hash)) continue;
            if (k.LastUsedAt is null || nowMs - k.LastUsedAt.Value >= TouchThrottleMs)
                k.LastUsedAt = nowMs;
            return k;
        }
        return null;
    }

    /// <summary>生成新密钥明文：<c>ak_</c> + 32 字节 base64url（约 43 字符）。</summary>
    public static string Generate()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        var b64 = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return "ak_" + b64;
    }

    /// <summary>密钥明文的 SHA-256（十六进制小写）。</summary>
    public static string Hash(string secret) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret))).ToLowerInvariant();

    /// <summary>用于界面辨识的前缀（保留 <c>ak_</c> + 8 位）。</summary>
    public static string PrefixOf(string secret)
        => secret.Length <= 11 ? secret : secret[..11] + "…";

    private static bool FixedTimeEquals(string a, string b)
    {
        var ba = Encoding.UTF8.GetBytes(a);
        var bb = Encoding.UTF8.GetBytes(b);
        if (ba.Length != bb.Length) return false;
        var d = 0;
        for (var i = 0; i < ba.Length; i++) d |= ba[i] ^ bb[i];
        return d == 0;
    }
}
