namespace AguiGroupChat.Agents;

/// <summary>
/// 语义记忆的文本向量化抽象：智能体记忆（RAG）的 embedding 来源可插拔——
/// 远程 OpenAI 兼容端点（HTTP）或本地 llama.cpp（LLamaSharp）模型。
/// </summary>
public interface IEmbeddingProvider : IDisposable
{
    /// <summary>把文本向量化为 float 数组；失败返回 null（调用方按「检索为空」处理，不影响主流程）。</summary>
    Task<float[]?> EmbedAsync(string text, CancellationToken ct = default);
}
