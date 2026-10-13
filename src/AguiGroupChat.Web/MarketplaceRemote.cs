using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using AguiGroupChat.Agents;
using AguiGroupChat.Hub.Infra;
using AguiGroupChat.Hub.Models;

namespace AguiGroupChat.Web;

/// <summary>一个远程市场目录源（管理员配置的静态 JSON 目录地址）。</summary>
public sealed class MarketplaceSource
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public long CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
}

/// <summary>远程目录源配置（持久化扩展区「marketplace」的载体）。</summary>
public sealed class MarketplaceState
{
    private readonly object _gate = new();
    private readonly List<MarketplaceSource> _sources = [];

    public IReadOnlyList<MarketplaceSource> Snapshot() { lock (_gate) return _sources.ToList(); }

    public MarketplaceSource? Get(string id) { lock (_gate) return _sources.FirstOrDefault(s => s.Id == id); }

    public void Add(MarketplaceSource source) { lock (_gate) _sources.Add(source); }

    public bool Remove(string id)
    {
        lock (_gate)
        {
            var i = _sources.FindIndex(s => s.Id == id);
            if (i < 0) return false;
            _sources.RemoveAt(i);
            return true;
        }
    }

    public void ReplaceAll(IEnumerable<MarketplaceSource> items)
    {
        lock (_gate)
        {
            _sources.Clear();
            _sources.AddRange(items);
        }
    }
}

/// <summary>远程目录里的一个包条目。</summary>
public sealed record RemotePackEntry(string PackId, string Name, string Description, string? Url, int AgentCount);

/// <summary>一次远程目录拉取结果（失败不抛，返回 Ok=false + Error，便于聚合展示）。</summary>
public sealed record RemoteCatalogResult(bool Ok, string? Error, IReadOnlyList<RemotePackEntry> Packs);

/// <summary>
/// 市场分发包文档（在线分发的传输格式）。schema 固定 <see cref="MarketplaceRemoteService.PackSchema"/>。
/// 目录索引与包文件都是纯 JSON，可放在任意静态服务器 / 对象存储上，无需专用后端。
/// </summary>
public sealed class MarketplacePackDocument
{
    public string Schema { get; set; } = MarketplaceRemoteService.PackSchema;
    public string PackId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public List<AgentDefinition> Agents { get; set; } = [];
}

/// <summary>
/// 远程市场：拉取目录索引与分发包（带短 TTL 缓存、尺寸上限、http/https 校验、超时）。
///
/// 目录契约（<c>GET &lt;source.Url&gt;</c>）：
/// <code>{ "packs": [ { "packId": "...", "name": "...", "description": "...", "url": "...", "agentCount": 2 } ] }</code>
/// （顶层数组亦可）；<c>url</c> 可省略，此时按「目录地址同目录 + <c>&lt;packId&gt;.json</c>」解析。
///
/// 包契约（<c>GET &lt;pack.url&gt;</c>）：<see cref="MarketplacePackDocument"/>。
/// </summary>
public sealed class MarketplaceRemoteService : IDisposable
{
    public const string PackSchema = "knowgath/marketplace-pack/v1";

    /// <summary>单个响应体上限（目录 / 包）：2MB——分发的是定义（文本），不该更大。</summary>
    private const int MaxBytes = 2 * 1024 * 1024;
    private const int TimeoutMs = 10_000;
    private const int MaxAgentsPerPack = 100;

    private readonly ILogger<MarketplaceRemoteService> _logger;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly ConcurrentDictionary<string, (long AtMs, RemoteCatalogResult Result)> _cache = new(StringComparer.Ordinal);

    /// <summary>目录缓存 TTL（秒）。测试可调小 / 调 0 关闭缓存。</summary>
    public int CacheTtlSeconds { get; set; } = 60;

    public MarketplaceRemoteService(ILogger<MarketplaceRemoteService> logger, HttpClient? http = null)
    {
        _logger = logger;
        _ownsHttp = http is null;
        _http = http ?? new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
        {
            Timeout = Timeout.InfiniteTimeSpan, // 单次超时由 CancellationTokenSource 控制
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("KnowGath-Marketplace/1.0");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    /// <summary>拉取一个源的目录（带缓存）。网络 / 解析失败返回 Ok=false（不抛）。</summary>
    public async Task<RemoteCatalogResult> FetchCatalogAsync(MarketplaceSource source, CancellationToken ct)
    {
        var now = Environment.TickCount64;
        if (CacheTtlSeconds > 0 && _cache.TryGetValue(source.Url, out var hit)
            && now - hit.AtMs < CacheTtlSeconds * 1000L)
            return hit.Result;

        RemoteCatalogResult result;
        try
        {
            var text = await GetStringAsync(source.Url, ct);
            result = new RemoteCatalogResult(true, null, ParseCatalog(source.Url, text));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "拉取市场目录失败：{Url}", source.Url);
            result = new RemoteCatalogResult(false, ex.Message, []);
        }
        if (CacheTtlSeconds > 0) _cache[source.Url] = (now, result);
        return result;
    }

    /// <summary>拉取并校验一个分发包。失败抛出（消息可直接回给用户）。</summary>
    public async Task<MarketplacePackDocument> FetchPackAsync(MarketplaceSource source, string packId, CancellationToken ct)
    {
        var catalog = await FetchCatalogAsync(source, ct);
        if (!catalog.Ok) throw new AguiProtocolException(ErrorCodes.BadRequest, $"目录不可用：{catalog.Error}");
        var entry = catalog.Packs.FirstOrDefault(p => string.Equals(p.PackId, packId, StringComparison.Ordinal))
            ?? throw new AguiProtocolException(ErrorCodes.BadRequest, $"目录中不存在包：{packId}");
        var url = ResolvePackUrl(source.Url, entry);

        var text = await GetStringAsync(url, ct);
        var doc = AguiJson.Deserialize<MarketplacePackDocument>(text)
            ?? throw new AguiProtocolException(ErrorCodes.BadRequest, "包文件不是合法 JSON");
        Validate(doc);
        if (string.IsNullOrWhiteSpace(doc.PackId)) doc.PackId = entry.PackId;
        if (string.IsNullOrWhiteSpace(doc.Name)) doc.Name = entry.Name;
        return doc;
    }

    private static void Validate(MarketplacePackDocument doc)
    {
        if (!string.IsNullOrEmpty(doc.Schema) && !string.Equals(doc.Schema, PackSchema, StringComparison.Ordinal))
            throw new AguiProtocolException(ErrorCodes.BadRequest, $"不支持的包格式：{doc.Schema}（期望 {PackSchema}）");
        if (doc.Agents.Count == 0) throw new AguiProtocolException(ErrorCodes.BadRequest, "包内没有任何数字员工定义");
        if (doc.Agents.Count > MaxAgentsPerPack)
            throw new AguiProtocolException(ErrorCodes.BadRequest, $"包过大（{doc.Agents.Count} > {MaxAgentsPerPack}）");
        foreach (var a in doc.Agents)
        {
            if (string.IsNullOrWhiteSpace(a.AgentId) || string.IsNullOrWhiteSpace(a.Nickname))
                throw new AguiProtocolException(ErrorCodes.BadRequest, "包内条目缺少 agentId / nickname");
        }
    }

    /// <summary>解析包地址：条目给出绝对 URL 则用之；否则按「目录同目录 / &lt;packId&gt;.json」解析。</summary>
    private static string ResolvePackUrl(string catalogUrl, RemotePackEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.Url))
        {
            if (Uri.TryCreate(entry.Url, UriKind.Absolute, out var abs)) return abs.ToString();
            if (Uri.TryCreate(catalogUrl, UriKind.Absolute, out var baseUri)
                && Uri.TryCreate(baseUri, entry.Url, out var rel)) return rel.ToString();
        }
        if (Uri.TryCreate(catalogUrl, UriKind.Absolute, out var b) && Uri.TryCreate(b, entry.PackId + ".json", out var d))
            return d.ToString();
        throw new AguiProtocolException(ErrorCodes.BadRequest, "无法解析包地址");
    }

    private static IReadOnlyList<RemotePackEntry> ParseCatalog(string sourceUrl, string text)
    {
        using var doc = JsonDocument.Parse(text);
        JsonElement arr;
        if (doc.RootElement.ValueKind == JsonValueKind.Array) arr = doc.RootElement;
        else if (doc.RootElement.ValueKind == JsonValueKind.Object
                 && doc.RootElement.TryGetProperty("packs", out var p) && p.ValueKind == JsonValueKind.Array) arr = p;
        else throw new AguiProtocolException(ErrorCodes.BadRequest, "目录格式不合法（期望 {\"packs\":[…]} 或数组）");

        var list = new List<RemotePackEntry>();
        foreach (var e in arr.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object) continue;
            var packId = Str(e, "packId") ?? Str(e, "id");
            if (string.IsNullOrWhiteSpace(packId)) continue;
            var name = Str(e, "name") ?? packId!;
            var desc = Str(e, "description") ?? "";
            var url = Str(e, "url");
            var count = e.TryGetProperty("agentCount", out var ac) && ac.ValueKind == JsonValueKind.Number ? ac.GetInt32() : 0;
            list.Add(new RemotePackEntry(packId!.Trim(), name!.Trim(), desc, url, count));
        }
        return list;
    }

    private static string? Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private async Task<string> GetStringAsync(string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new AguiProtocolException(ErrorCodes.BadRequest, "地址需为 http / https 绝对 URL");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeoutMs);
        using var resp = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        if (!resp.IsSuccessStatusCode)
            throw new AguiProtocolException(ErrorCodes.BadRequest, $"HTTP {(int)resp.StatusCode}");
        await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token);
        var bytes = await ReadBoundedAsync(stream, MaxBytes, cts.Token);
        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>限长读取：超过上限即抛错（防超大响应吃内存）。</summary>
    private static async Task<byte[]> ReadBoundedAsync(Stream stream, int limit, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            if (ms.Length + read > limit)
                throw new AguiProtocolException(ErrorCodes.BadRequest, "响应体过大");
            ms.Write(buffer, 0, read);
        }
        return ms.ToArray();
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
