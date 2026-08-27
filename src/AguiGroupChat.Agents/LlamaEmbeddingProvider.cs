using LLama;
using LLama.Common;
using Microsoft.Extensions.Logging;

namespace AguiGroupChat.Agents;

/// <summary>
/// 本地 llama.cpp（LLamaSharp）embedding 提供方：直接加载 GGUF embedding 模型
/// （如 nomic-embed-text-v1.5 768 维 / bge-m3 1024 维），全程离线、无需 embedding 服务。
/// 单例常驻：模型首次加载较慢（CPU 数百 MB ~ 1GB），后续调用走同一实例。
/// </summary>
public sealed class LlamaEmbeddingProvider : IEmbeddingProvider
{
    private readonly LLamaWeights _model;
    private readonly LLamaEmbedder _embedder;
    private readonly ILogger _logger;
    private readonly int _contextSize;
    private readonly SemaphoreSlim _gate = new(1, 1); // llama.cpp 推理非线程安全，串行化

    /// <summary>
    /// 超长文本分段 embedding 的保守字符/token 比。
    /// 实证（bge-m3, context=2048）：中文约 1 字/token，超 context 会在 ~2000 字左右开始失败；
    /// 因此取 0.9（更小的字符/token 比 → 单次放更少字 → 留有安全余量覆盖标点/生僻字）。
    /// 注意取值必须 &lt; 1：若 &gt; 1（如 2.0）会假设 0.5 token/字，使单次字数上限超过模型真实 context，导致长文本返回空向量。
    /// </summary>
    private const double SafeCharsPerToken = 0.9;

    /// <summary>分段重叠比例：避免切在语义边界处丢失上下文。</summary>
    private const double SegmentOverlapRatio = 0.2;

    public LlamaEmbeddingProvider(string modelPath, int contextSize, int threads, ILogger logger)
    {
        _logger = logger;
        var parameters = new ModelParams(modelPath)
        {
            ContextSize = (uint)Math.Max(64, contextSize),
            Embeddings = true, // embedding 模式（不生成文本，只输出向量）
            Threads = Math.Max(1, threads),
        };
        _contextSize = (int)parameters.ContextSize;
        _model = LLamaWeights.LoadFromFile(parameters);
        _embedder = new LLamaEmbedder(_model, parameters, logger);
        _logger.LogInformation("本地 embedding 模型已加载：{Model}（context={ContextSize}，threads={Threads}，维度={Dimensions}）",
            modelPath, parameters.ContextSize, parameters.Threads, _model.EmbeddingSize);
    }

    /// <summary>GGUF 模型实际向量维度（供 AgentHosting 建表校验：配置维度不符会导致向量写入失败）。</summary>
    public int Dimension => _model.EmbeddingSize;

    /// <inheritdoc />
    public async Task<float[]?> EmbedAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        List<string> segments = [];
        await _gate.WaitAsync(ct);
        try
        {
            // 输入超出 llama.cpp context 会抛异常（长文档 / 长消息）；按 context 估算字符上限分段 embedding 后平均
            segments = SplitForEmbedding(text, _contextSize);
            if (segments.Count == 1)
            {
                var single = await _embedder.GetEmbeddings(text, ct);
                if (single.Count == 0 || single[0].Length == 0) return null;
                return single[0];
            }

            var vectors = new List<float[]>();
            foreach (var seg in segments)
            {
                var r = await _embedder.GetEmbeddings(seg, ct);
                if (r.Count == 0 || r[0].Length == 0) return null;
                vectors.Add(r[0]);
            }
            var dim = vectors[0].Length;
            var avg = new float[dim];
            foreach (var v in vectors)
                for (var i = 0; i < dim; i++) avg[i] += v[i] / vectors.Count;
            return avg;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "本地 embedding 计算失败（文本长度 {Length}，分段 {Count}）", text.Length, segments.Count);
            return null;
        }
        finally { _gate.Release(); }
    }

    /// <summary>按 context 估算字符上限把长文本切成可单次 embedding 的段（带重叠）；不超限时原样返回。</summary>
    internal static List<string> SplitForEmbedding(string text, int contextSize)
    {
        var maxChars = Math.Max(64, (int)(contextSize * SafeCharsPerToken));
        if (text.Length <= maxChars) return [text];
        var segments = new List<string>();
        var step = maxChars - (int)(maxChars * SegmentOverlapRatio);
        var pos = 0;
        while (pos < text.Length)
        {
            var len = Math.Min(maxChars, text.Length - pos);
            segments.Add(text.Substring(pos, len));
            if (pos + len >= text.Length) break;
            pos += step;
        }
        return segments;
    }

    public void Dispose()
    {
        _gate.Dispose();
        _embedder.Dispose();
        _model.Dispose();
    }
}
