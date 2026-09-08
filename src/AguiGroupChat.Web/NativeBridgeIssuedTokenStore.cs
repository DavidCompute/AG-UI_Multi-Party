using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AguiGroupChat.Web;

/// <summary>
/// 本机桥安装包“绑定型”连接令牌签发器：
///   - 网页下载安装包时签发一个随机令牌（不再把全局 NATIVE_TUNNEL_TOKEN 放进包里）；
///   - 令牌<b>首次连接时绑定到发起桥的 client 标识</b>（本机桥默认持久化一个随机 UUID 作为机器标识）；
///     此后只有同一 client（同一台机器）能用该包连接，包被拷贝到别的机器会因 client 不匹配被拒；
///   - 管理员可查看 / 吊销已签发令牌。
/// 存储：令牌仅以 SHA-256 哈希落盘（data/nativebridge-issued-tokens.json，明文令牌不落盘）；
/// 吊销 = 从哈希表删除该条目，之后携带旧包的桥连接即被拒。
/// 兼容：平台全局 / 逐 agent 令牌（NativeTunnel:Token / AgentTokens）校验路径保持不变——仅安装包下载改用本签发器。
/// </summary>
public sealed class NativeBridgeIssuedTokenStore
{
    public sealed record Entry(string Hash, string? ClientId, long CreatedAtMs, long LastUsedAtMs, string? Note);

    public sealed record IssueResult(string Token, string Hash);

    public sealed record AuthorizeResult(bool IsIssuedToken, bool Allowed, string? Error, bool NewlyBound = false);

    private readonly string _filePath;
    private readonly Dictionary<string, Entry> _byHash = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    public NativeBridgeIssuedTokenStore(IWebHostEnvironment env)
        : this(Path.Combine(env.ContentRootPath, "data", "nativebridge-issued-tokens.json"))
    {
    }

    /// <summary>测试可注入临时文件路径，避免污染 / 读到真实持久化文件。</summary>
    public NativeBridgeIssuedTokenStore(string filePath)
    {
        _filePath = filePath;
        Load();
    }

    // ================= 签发 / 吊销 / 查询 =================

    /// <summary>签发一枚随机令牌；仅返回哈希的条目持久化。调用方把明文令牌注入下载包。</summary>
    public IssueResult Issue(string? note = null)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_'); // base64url 随机令牌
        var hash = HashOf(token);
        lock (_lock)
        {
            _byHash[hash] = new Entry(hash, null, NowMs(), NowMs(), note);
            SaveLocked();
        }
        return new IssueResult(token, hash);
    }

    /// <summary>吊销一枚令牌（按哈希）；返回是否删除成功。</summary>
    public bool Revoke(string hash)
    {
        lock (_lock)
        {
            var removed = _byHash.Remove(hash);
            if (removed) SaveLocked();
            return removed;
        }
    }

    /// <summary>吊销某登录用户签发的全部令牌（登出时调用：该用户领到的 setup 令牌随之失效）。</summary>
    public int RevokeAllForNote(string note)
    {
        if (string.IsNullOrWhiteSpace(note)) return 0;
        lock (_lock)
        {
            var doomed = _byHash.Values.Where(e => string.Equals(e.Note, note, StringComparison.Ordinal)).Select(e => e.Hash).ToList();
            foreach (var h in doomed) _byHash.Remove(h);
            if (doomed.Count > 0) SaveLocked();
            return doomed.Count;
        }
    }

    /// <summary>当前全部条目（按签发时间倒序），供管理员查看 / 吊销。</summary>
    public IReadOnlyList<Entry> List() => _byHash.Values.OrderByDescending(e => e.CreatedAtMs).ToList();

    public long Count { get { lock (_lock) return _byHash.Count; } }

    // ================= 鉴权 =================

    /// <summary>连接鉴权：令牌不是本签发器所发 → IsIssuedToken=false（由调用方回落到全局/逐 agent 令牌）。
    /// 是所发令牌：首次连接绑定到该 client；client 不匹配（包被拷到其它机器）→ 拒绝。</summary>
    public AuthorizeResult AuthorizeConnect(string? token, string? clientId)
    {
        if (string.IsNullOrWhiteSpace(token)) return new AuthorizeResult(false, false, null);
        var hash = HashOf(token);
        lock (_lock)
        {
            if (!_byHash.TryGetValue(hash, out var entry)) return new AuthorizeResult(false, false, null);
            // 首次连接：绑定到这台机器的 client 标识
            if (string.IsNullOrWhiteSpace(entry.ClientId))
            {
                if (string.IsNullOrWhiteSpace(clientId))
                    return new AuthorizeResult(true, false, "该安装包令牌需要在带 --client 机器标识的连接中使用（请确认本机桥已正常启动）。");
                _byHash[hash] = entry with { ClientId = clientId, LastUsedAtMs = NowMs() };
                SaveLocked();
                return new AuthorizeResult(true, true, null, NewlyBound: true);
            }
            if (!string.Equals(entry.ClientId, clientId, StringComparison.Ordinal))
                return new AuthorizeResult(true, false, "该安装包令牌已被另一台客户端绑定：本安装包不能复制到其它机器重复使用，请重新下载安装包。");
            _byHash[hash] = entry with { LastUsedAtMs = NowMs() };
            return new AuthorizeResult(true, true, null);
        }
    }

    /// <summary>结果回传鉴权：仅校验是本签发器有效令牌（连接期已做 client 绑定，回传无需再带 client）。</summary>
    public bool IsValidIssuedToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        var hash = HashOf(token);
        lock (_lock)
        {
            if (!_byHash.TryGetValue(hash, out var entry)) return false;
            _byHash[hash] = entry with { LastUsedAtMs = NowMs() };
            return true;
        }
    }

    private static string HashOf(string token)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    // ================= 持久化（仅哈希落盘） =================

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return;
            var json = File.ReadAllText(_filePath);
            var arr = JsonSerializer.Deserialize<List<Entry>>(json, JsonOpt);
            if (arr is null) return;
            foreach (var e in arr)
                if (!string.IsNullOrWhiteSpace(e.Hash)) _byHash[e.Hash] = e;
        }
        catch (Exception) { /* 损坏/缺失：从空表开始，下次保存重建 */ }
    }

    private void SaveLocked()
    {
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_filePath, JsonSerializer.Serialize(_byHash.Values.ToList(), JsonOpt));
        }
        catch (Exception) { /* 落盘失败不阻断鉴权（内存态仍生效，重启后丢失该次签发） */ }
    }

    private static readonly JsonSerializerOptions JsonOpt = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
