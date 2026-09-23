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
    private readonly int _slowWarnSeconds;

    public HttpEmbeddingProvider(string endpoint, string model, string? apiKey, int timeoutSeconds, ILogger logger,
        int slowWarnSeconds = 5, int connectTimeoutSeconds = 5)
    {
        _model = model;
        _logger = logger;
        _slowWarnSeconds = Math.Max(1, slowWarnSeconds);
        // 关键：把「连不上」与「排队中」拆开。
        //   ConnectTimeout 只管建连 → 服务未启 / 端口不通时<b>秒级判死</b>，不再白等满整个总预算；
        //   HttpClient.Timeout 是总预算（连接 + 排队 + 推理）→ 排队 / 慢推理不被误杀。
        // 为何必须拆：原实现只有一条扁平超时，两者共享同一预算，“它只是排了队”与“它真的死了”无法区分。
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(Math.Max(1, connectTimeoutSeconds)),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5), // 避免长跑进程用到已失效的 DNS / 连接
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds)) };
        if (!string.IsNullOrEmpty(apiKey))
            _http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
        _http.BaseAddress = new Uri((endpoint ?? "").TrimEnd('/') + "/");
    }

    /// <summary>注入外部 HttpClient（测试用 mock / 共享实例）；未设 BaseAddress 时回退官方端点。</summary>
    public HttpEmbeddingProvider(HttpClient http, string model, ILogger logger)
    {
        _model = model;
        _logger = logger;
        _slowWarnSeconds = 5;
        _http = http;
        _http.BaseAddress ??= new Uri("https://api.openai.com/v1/");
    }

    /// <inheritdoc />
    public async Task<float[]?> EmbedAsync(string text, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
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

        // 耗时留痕：embedding 耗时由<b>输入长度</b>主导（实测 4 核 CPU 上约 8.5ms/字符：
        // 6 字≈0.3s、1500 字≈12.7s）。没有这行就很容易把“单条输入太长”误判成“服务慢/并发不够”。
        sw.Stop();
        var chars = text?.Length ?? 0;
        if (sw.Elapsed.TotalSeconds >= _slowWarnSeconds)
            _logger.LogWarning("embedding 较慢：{Ms} ms（输入 {Chars} 字符，模型 {Model}）。"
                + "耗时由输入长度主导（4 核 CPU 上约 8.5ms/字符），请确认是否有超长输入未经截断就到不了 embedding",
                sw.ElapsedMilliseconds, chars, _model);
        else
            _logger.LogDebug("embedding 完成：{Ms} ms（输入 {Chars} 字符）", sw.ElapsedMilliseconds, chars);
        return vector;
    }

    public void Dispose() => _http.Dispose();
}
