using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace AguiGroupChat.Agents;

/// <summary>
/// OpenAI 兼容 <c>/v1/embeddings</c> 远程向量化（Ollama / vLLM / Azure OpenAI 等）。
/// 从 <see cref="AgentMessageMemory"/> 提取的 HTTP 实现，作为 <see cref="IEmbeddingProvider"/> 的默认实现。
/// </summary>
public sealed class HttpEmbeddingProvider : IEmbeddingProvider
{
    private readonly HttpClient _http;
    private readonly string _model;
    private readonly ILogger _logger;

    public HttpEmbeddingProvider(string endpoint, string model, string? apiKey, int timeoutSeconds, ILogger logger)
    {
        _model = model;
        _logger = logger;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds)) };
        if (!string.IsNullOrEmpty(apiKey))
            _http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
        _http.BaseAddress = new Uri((endpoint ?? "").TrimEnd('/') + "/");
    }

    /// <summary>注入外部 HttpClient（测试用 mock / 共享实例）；未设 BaseAddress 时回退官方端点。</summary>
    public HttpEmbeddingProvider(HttpClient http, string model, ILogger logger)
    {
        _model = model;
        _logger = logger;
        _http = http;
        _http.BaseAddress ??= new Uri("https://api.openai.com/v1/");
    }

    /// <inheritdoc />
    public async Task<float[]?> EmbedAsync(string text, CancellationToken ct = default)
    {
        var payload = new { model = _model, input = text };
        var resp = await _http.PostAsJsonAsync("embeddings", payload, ct);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        if (!doc.RootElement.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Array
            || data.GetArrayLength() == 0)
            return null;
        var emb = data[0].GetProperty("embedding");
        var vector = new float[emb.GetArrayLength()];
        var i = 0;
        foreach (var e in emb.EnumerateArray()) vector[i++] = e.GetSingle();
        return vector;
    }

    public void Dispose() => _http.Dispose();
}
