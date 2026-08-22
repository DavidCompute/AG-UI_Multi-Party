using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace AguiGroupChat.Agents;

/// <summary>桥接端点声明的能力（3.2 Capability Discovery）：从服务端能力端点解析。</summary>
public sealed record BridgeCapabilities(
    string[] SupportsTools,
    bool SupportsAttachments,
    string[] ApprovalTypes,
    bool Discovered)
{
    public static readonly BridgeCapabilities Unknown = new([], false, [], Discovered: false);
}

/// <summary>
/// 桥接能力协商（3.2）：对配置了桥接（BridgeEndpoint / 全局 AguiBridge.Endpoint）的端点，
/// 尝试 GET <c>{endpoint}/capabilities</c>（http/https）探测服务端能力；ws/wss 或失败回退"未知"。
/// 只读探测、缓存；与 <see cref="BridgeHealthService"/> 互补，供管理员查看（减少人工配置查对）。
/// 不改变桥接调用主流程（即便未探测到能力，桥接照常工作）。缓存键 = 端点 URL（同一端点多 agent 共享）。
/// </summary>
public sealed class BridgeCapabilitiesService
{
    private readonly AgentCatalog _catalog;
    private readonly AgentOptions _options;
    private readonly ILogger<BridgeCapabilitiesService> _logger;
    private static readonly HttpClient Http = CreateHttp();
    private readonly ConcurrentDictionary<string, (string AgentId, string Endpoint, BridgeCapabilities Cap)> _cache = new(StringComparer.OrdinalIgnoreCase);

    private static HttpClient CreateHttp()
    {
        var handler = new SocketsHttpHandler { AllowAutoRedirect = false };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(4) };
    }

    public BridgeCapabilitiesService(AgentCatalog catalog, AgentOptions options, ILogger<BridgeCapabilitiesService> logger)
    {
        _catalog = catalog;
        _options = options;
        _logger = logger;
    }

    /// <summary>解析出的桥接端点（agentId → endpoint），含全局默认。</summary>
    public IReadOnlyList<(string AgentId, string Endpoint)> Endpoints()
    {
        var list = new List<(string, string)>();
        if (!string.IsNullOrWhiteSpace(_options.AguiBridge?.Endpoint))
            list.Add(("__global__", _options.AguiBridge!.Endpoint.Trim()));
        foreach (var def in _catalog.ListDefinitions())
            if (!string.IsNullOrWhiteSpace(def.BridgeEndpoint))
                list.Add((def.AgentId, def.BridgeEndpoint.Trim()));
        // 同端点去重保留首个 agentId 标注
        return list.GroupBy(x => x.Item2, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
    }

    /// <summary>返回缓存的全部能力（未探测为 Discovered=false）。</summary>
    public IReadOnlyList<(string AgentId, string Endpoint, BridgeCapabilities Cap)> GetCached()
        => _cache.Values.OrderBy(v => v.AgentId).ToList();

    /// <summary>对全部端点执行一次能力探测，返回最新结果并更新缓存。</summary>
    public async Task<IReadOnlyList<(string AgentId, string Endpoint, BridgeCapabilities Cap)>> ProbeAllAsync(CancellationToken ct = default)
    {
        var tasks = Endpoints().Select(e => ProbeAsync(e.AgentId, e.Endpoint, ct));
        var results = await Task.WhenAll(tasks);
        return results.OrderBy(r => r.AgentId).ToList();
    }

    private async Task<(string AgentId, string Endpoint, BridgeCapabilities Cap)> ProbeAsync(string agentId, string endpoint, CancellationToken ct)
    {
        var cap = BridgeCapabilities.Unknown;
        if (endpoint.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            endpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            var baseUri = endpoint.TrimEnd('/');
            var probeUrl = $"{baseUri}/capabilities";
            try
            {
                using var resp = await Http.GetAsync(probeUrl, HttpCompletionOption.ResponseHeadersRead, ct);
                if (resp.IsSuccessStatusCode)
                {
                    await using var stream = await resp.Content.ReadAsStreamAsync(ct);
                    var doc = await JsonSerializer.DeserializeAsync<CapabilityDoc>(stream, Json, ct);
                    if (doc is not null)
                    {
                        cap = new BridgeCapabilities(
                            doc.SupportsTools ?? [],
                            doc.SupportsAttachments ?? false,
                            doc.ApprovalTypes ?? [],
                            Discovered: true);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug("桥接能力探测失败：{AgentId} → {Url}（{Msg}）", agentId, probeUrl, ex.Message);
            }
        }
        _cache[endpoint] = (agentId, endpoint, cap);
        return (agentId, endpoint, cap);
    }

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    /// <summary>预期能力端点 JSON 结构（服务端可按 <c>{supportsTools, supportsAttachments, approvalTypes}</c> 返回；未实现时回退"未知"）。</summary>
    private sealed class CapabilityDoc
    {
        public string[]? SupportsTools { get; set; }
        public bool? SupportsAttachments { get; set; }
        public string[]? ApprovalTypes { get; set; }
    }
}
