using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace AguiGroupChat.Hub.Users;

/// <summary>用户 TOTP（base32 密钥）与其启停状态。</summary>
public sealed record UserTotp(string SecretBase32, bool Enabled);

/// <summary>
/// 登录二次验证（TOTP，RFC 6238，4.4）：为一个账号签发 / 校验 6 位动态码（HMAC-SHA1，30 秒窗口、
/// ±1 窗口容忍 + 防重放窗口号）。密钥为 RFC 4648 base32（客户端如 Google Authenticator 可录入）。
/// 经扩展区「totpSecrets」持久化（见 <c>RegisterTotpPersistence</c>）。
/// </summary>
public sealed class TotpService
{
    private readonly ConcurrentDictionary<string, UserTotp> _users = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _lastUsedWindow = new(StringComparer.Ordinal); // 防重放

    public bool IsEnabled(string userId) => _users.TryGetValue(userId, out var t) && t.Enabled;

    /// <summary>签发（或重新签发）密钥并返回明文（客户端录入用）；未启用，须 confirm 后生效。</summary>
    public string Enroll(string userId)
    {
        var secret = RandomBase32(20);
        _users[userId] = new UserTotp(secret, false);
        return secret;
    }

    /// <summary>校验一次动态码后启用 TOTP。</summary>
    public bool Confirm(string userId, string code)
    {
        if (!_users.TryGetValue(userId, out var t)) return false;
        if (!Validate(userId, t.SecretBase32, code)) return false;
        _users[userId] = t with { Enabled = true };
        return true;
    }

    /// <summary>停用 TOTP（须提供当前有效码以证明持有原设备）。</summary>
    public bool Disable(string userId, string code)
    {
        if (!_users.TryGetValue(userId, out var t)) return false;
        if (!Validate(userId, t.SecretBase32, code)) return false;
        _users.TryRemove(userId, out _);
        return true;
    }

    /// <summary>校验用户当前动态码（6 位）。</summary>
    public bool Verify(string userId, string code)
        => _users.TryGetValue(userId, out var t) && t.Enabled && Validate(userId, t.SecretBase32, code);

    /// <summary>登录校验：未启用 TOTP 直接放行；启用则要求有效码。</summary>
    public bool VerifyLogin(string userId, string? code)
    {
        if (!IsEnabled(userId)) return true;
        return !string.IsNullOrWhiteSpace(code) && Verify(userId, code.Trim());
    }

    // ================= RFC 6238 =================

    /// <summary>RFC 6238 动态码（公开，供测试 / 外部工具复算）。counter 为时间步序号（30s 一格）。</summary>
    public static string TOTP(string secretBase32, long counter)
    {
        var key = Base32Decode(secretBase32);
        var msg = BitConverter.GetBytes(System.Net.IPAddress.HostToNetworkOrder(counter));
        using var hmac = new HMACSHA1(key);
        var hash = hmac.ComputeHash(msg);
        var offset = hash[^1] & 0x0F;
        var binary = (hash[offset] & 0x7F) << 24 | (hash[offset + 1] & 0xFF) << 16 | (hash[offset + 2] & 0xFF) << 8 | (hash[offset + 3] & 0xFF);
        return (binary % 1_000_000).ToString("D6");
    }

    private bool Validate(string userId, string secretBase32, string code)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length != 6 || !code.All(char.IsDigit)) return false;
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;
        for (var w = now - 1; w <= now + 1; w++)
        {
            if (_lastUsedWindow.TryGetValue(userId, out var last) && last >= w) continue; // 防重放
            if (TOTP(secretBase32, w) == code)
            {
                _lastUsedWindow[userId] = w;
                return true;
            }
        }
        return false;
    }

    private static string RandomBase32(int bytes)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var rnd = RandomNumberGenerator.GetBytes(bytes);
        var sb = new StringBuilder(bytes);
        foreach (var b in rnd) sb.Append(alphabet[b & 31]);
        return sb.ToString();
    }

    private static byte[] Base32Decode(string input)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var clean = input.Trim().ToUpperInvariant().TrimEnd('=');
        var bits = 0; var value = 0;
        var output = new List<byte>();
        foreach (var c in clean)
        {
            var idx = alphabet.IndexOf(c);
            if (idx < 0) continue;
            value = (value << 5) | idx;
            bits += 5;
            if (bits >= 8)
            {
                output.Add((byte)((value >> (bits - 8)) & 0xFF));
                bits -= 8;
            }
        }
        return output.ToArray();
    }

    // --------------- 持久化（扩展区「totpSecrets」） ---------------

    public IReadOnlyList<KeyValuePair<string, UserTotp>> Snapshot() => _users.ToList();
    public void Restore(IEnumerable<KeyValuePair<string, UserTotp>> users)
    {
        _users.Clear();
        foreach (var kv in users) if (!string.IsNullOrWhiteSpace(kv.Key)) _users[kv.Key] = kv.Value;
    }
    public void Clear() => _users.Clear();
}
