using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AguiGroupChat.Agents;

/// <summary>
/// 启动自检：确认 embedding 端点<b>实际返回的向量维度</b>与配置（<see cref="MemoryOptions.EmbeddingDimensions"/>）一致。
///
/// <para>
/// 为什么需要：进程内的 llama 提供方在 <c>AgentHosting.ResolveEmbeddingDimensions</c> 里能探测到实际维度并按实际维度建表
/// （配置不符时只发一条 WARN，记忆仍然可用）；但 HTTP 提供方（Ollama / llama.cpp / 任意 OpenAI 兼容端点）<b>只能信配置</b>。
/// 一旦端点上的模型与配置维度不一致——例如把 130MB/768 维的 nomic-embed-text 当成 bge-m3(1024 维) 放进去——
/// 向量列已按 1024 维建表，于是<b>写入失败、检索只会返回空</b>：这就是「RAG 静默失效」，
/// 日志里没有显眼错误，用户只觉得数字员工「记不住事」，很难定位。
/// </para>
///
/// <para>
/// 现在的行为：启动时用一条极短文本打一次端点（只为拿维度，不需要语义）。
/// 维度不符 → 记一条 ERROR 并<b>直接禁用语义记忆</b>（宁可显式关闭，也不要静默错）；
/// 探测本身失败（端点暂不可用 / 超时）→ 只记 WARN、<b>不</b>禁用记忆，保持原有的「连不上就当检索为空」行为，
/// 避免把一个可恢复的故障变成永久禁用。
/// </para>
/// </summary>
public sealed class EmbeddingDimensionProbe : IHostedService
{
    /// <summary>探测自身的超时：这只是启动自检，不该把启动拖住（实测 500 字约 3 秒，这里只用十几个字符）。</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(15);

    /// <summary>探测文本：越短越好，只用于取维度。</summary>
    private const string ProbeText = "dimension probe";

    private readonly IEmbeddingProvider _provider;
    private readonly AgentOptions _options;
    private readonly ILogger<EmbeddingDimensionProbe> _logger;

    public EmbeddingDimensionProbe(IEmbeddingProvider provider, AgentOptions options, ILogger<EmbeddingDimensionProbe> logger)
    {
        _provider = provider;
        _options = options;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var m = _options.Memory;
        if (!m.Enabled) return;

        // 本地 llama 提供方无需探测：它按模型实际维度建表（见 AgentHosting.ResolveEmbeddingDimensions），
        // 这里再判一次反而可能因为「配置维度 ≠ 实际维度但表已按实际维度建好」而误禁用。
        if (!string.Equals(m.Provider, "http", StringComparison.OrdinalIgnoreCase)) return;

        // 与建表维度同一口径（AgentHosting.ResolveEmbeddingDimensions 用 Math.Max(8, …)）
        var configured = Math.Max(8, m.EmbeddingDimensions);

        float[]? vector;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(ProbeTimeout);
            vector = await _provider.EmbedAsync(ProbeText, cts.Token);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "embedding 维度自检未完成（端点暂不可用或超时？）：语义记忆保持启用，按配置维度 {Configured} 工作",
                configured);
            return;
        }

        if (vector is null || vector.Length == 0)
        {
            _logger.LogWarning(
                "embedding 维度自检无结果（端点未返回向量，可能不是 OpenAI 兼容的 /v1/embeddings）：语义记忆保持启用，按配置维度 {Configured} 工作",
                configured);
            return;
        }

        if (vector.Length != configured)
        {
            m.Enabled = false;
            _logger.LogError(
                "embedding 维度不一致 → 已禁用语义记忆（本条覆盖启动时那句「语义记忆已启用」）：端点 {Endpoint} 的模型 {Model} 实际返回 {Actual} 维，"
                + "而配置 Agents:Memory:EmbeddingDimensions={Configured} 决定的向量列是 {Configured} 维——在这种状态下继续跑就是「RAG 静默失效」，"
                + "写入会失败、检索只会返回空，日志没有显眼错误，用户只觉得「记不住事」。修法二选一："
                + "① 换成维度一致的模型（bge-m3 为 1024 维，注意别把小模型放到 models/embedding.gguf）；"
                + "或 ② 把 Agents:Memory:EmbeddingDimensions 改成 {Actual} 并重启（换维度等于换向量空间，已入库的记忆需要重灌）。",
                Endpoint(), m.EmbeddingModel, vector.Length, configured, configured, vector.Length);
            return;
        }

        _logger.LogInformation("embedding 维度自检通过：{Actual} 维（模型 {Model}，维度与配置一致）", vector.Length, m.EmbeddingModel);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>与注册 embedding 提供方时的回退顺序保持一致，让日志直接指向用户能改的那个值。</summary>
    private string Endpoint()
    {
        var endpoint = _options.Memory.EmbeddingEndpoint ?? _options.Endpoint;
        return string.IsNullOrWhiteSpace(endpoint) ? "https://api.openai.com/v1" : endpoint;
    }
}
