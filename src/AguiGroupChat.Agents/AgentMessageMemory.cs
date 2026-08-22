using System.Text.Json;
using System.Threading.Channels;
using AguiGroupChat.Hub.Agents;
using AguiGroupChat.Hub.Models;
using AguiGroupChat.Hub.Persistence;
using Microsoft.Extensions.Logging;

namespace AguiGroupChat.Agents;

/// <summary>
/// 语义记忆服务（RAG）：消息经 <see cref="IEmbeddingProvider"/>（OpenAI 兼容 HTTP 端点或
/// LLamaSharp 本地模型）向量化后写入 <see cref="IMessageMemoryStore"/>（PostgreSQL+pgvector /
/// SQLite+sqlite-vec），触发回复前按语义相似度检索。
/// 写入为 fire-and-forget（内部 <c>Task.Run</c>，失败仅记日志）；提供方不可用 / 未启用时检索返回空，
/// 对群聊主流程完全透明。
/// </summary>
public sealed class AgentMessageMemory : IMessageMemory, IDisposable
{
    private readonly IMessageMemoryStore _store;
    private readonly MemoryOptions _options;
    private readonly IEmbeddingProvider _embedding;
    private readonly ILogger<AgentMessageMemory> _logger;
    // embedding 并发上限：消息高峰时排队调用，避免打爆 embedding 服务（Ollama / llama.cpp 单线程推理时尤其需要）
    private readonly SemaphoreSlim _embeddingLimiter = new(4);
    // 记忆写入有界队列（容量 256，满则 DropWrite）：embedding 服务不可用 / 高峰时写入不阻塞调用方，
    // 也不会无界堆积后台任务（fire-and-forget Task 无限堆积会拖垮进程）——队列满时新消息直接放弃
    private const int MemoryWriteQueueCapacity = 256;
    private const int EmbeddingWaitTimeoutSeconds = 10;
    private readonly Channel<MessageMemoryEntry> _writeQueue = Channel.CreateBounded<MessageMemoryEntry>(
        new BoundedChannelOptions(MemoryWriteQueueCapacity)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false,
        });
    private readonly CancellationTokenSource _writeCts = new();

    public AgentMessageMemory(IMessageMemoryStore store, AgentOptions options, ILogger<AgentMessageMemory> logger, IEmbeddingProvider? embeddingProvider = null)
    {
        _store = store;
        _options = options.Memory;
        _logger = logger;
        _embedding = embeddingProvider ?? CreateDefaultProvider(options, logger);
        _logger.LogInformation("语义记忆已启用：embedding {Provider}，模型 {Model}，维度 {Dimensions}，检索范围 {Scope}（TopK={TopK}）",
            _options.Provider, _options.Provider == "llama" ? _options.LlamaModelPath : _options.EmbeddingModel,
            _options.EmbeddingDimensions, _options.Scope, _options.TopK);
        // 后台消费者：串行向量化 + 落库（fire-and-forget，内部已兜底异常）
        _ = Task.Run(ProcessWriteQueueAsync);
    }

    /// <summary>兼容旧签名：注入外部 HttpClient（测试 mock / 共享实例）作为默认 HTTP embedding 提供方。</summary>
    public AgentMessageMemory(IMessageMemoryStore store, AgentOptions options, ILogger<AgentMessageMemory> logger, HttpClient http)
        : this(store, options, logger, new HttpEmbeddingProvider(http, options.Memory.EmbeddingModel, logger))
    {
    }

    /// <summary>未显式注入时按配置构造默认提供方（http → OpenAI 兼容端点）。</summary>
    private static IEmbeddingProvider CreateDefaultProvider(AgentOptions options, ILogger logger)
    {
        var m = options.Memory;
        var endpoint = m.EmbeddingEndpoint ?? options.Endpoint;
        if (string.IsNullOrWhiteSpace(endpoint)) endpoint = "https://api.openai.com/v1";
        return new HttpEmbeddingProvider(endpoint, m.EmbeddingModel, m.EmbeddingApiKey,
            m.EmbeddingTimeoutSeconds, logger);
    }

    /// <summary>写入一条记忆：入有界队列异步向量化（不阻塞调用方，失败仅记日志；并发经信号量限流）。
    /// 自动遗忘：配置 RetentionDays&gt;0 时普通记忆（importance=0）写入即带过期时间；重要记忆永不过期。
    /// 队列满（256 条积压，通常为 embedding 提供方不可用）时丢弃本条，避免后台任务无限堆积。</summary>
    public void Remember(MessageMemoryEntry entry)
    {
        if (!_options.Enabled || string.IsNullOrWhiteSpace(entry.Content)) return;
        if (!_writeQueue.Writer.TryWrite(entry))
            _logger.LogDebug("语义记忆写入队列已满（{Capacity} 条），本条已丢弃：{MessageId}", MemoryWriteQueueCapacity, entry.MessageId);
    }

    /// <summary>记忆写入队列消费者：串行向量化 + 落库；embedding 排队超过
    /// <see cref="EmbeddingWaitTimeoutSeconds"/> 秒视为提供方不可用，放弃本条（不阻塞后续消费）。</summary>
    private async Task ProcessWriteQueueAsync()
    {
        try
        {
            await foreach (var entry in _writeQueue.Reader.ReadAllAsync(_writeCts.Token))
            {
                try
                {
                    // 等待上限：embedding 并发占满（服务不可用 / 推理缓慢）时不再无限排队，超时放弃本条
                    if (!await _embeddingLimiter.WaitAsync(TimeSpan.FromSeconds(EmbeddingWaitTimeoutSeconds)))
                    {
                        _logger.LogWarning("语义记忆写入放弃：embedding 排队超时（{Seconds} 秒），{MessageId} 未写入",
                            EmbeddingWaitTimeoutSeconds, entry.MessageId);
                        continue;
                    }
                }
                catch (OperationCanceledException) { return; } // 释放时取消
                try
                {
                    var embedding = await _embedding.EmbedAsync(entry.Content);
                    if (embedding is null || embedding.Length == 0) continue;
                    var importance = MemoryImportance.Normal;
                    long? expiresAt = null;
                    if (_options.RetentionDays > 0)
                    {
                        // 重要记忆（自动判定：AI 分身 / 智能体结论性发言等）不受自动遗忘影响；普通记忆按保留天数过期
                        expiresAt = entry.Timestamp + _options.RetentionDays * 86_400_000L;
                    }
                    _store.Upsert(new MessageMemoryRecord(
                        entry.MessageId, entry.GroupId, entry.TopicId,
                        entry.SenderId, entry.SenderType, SanitizeForMemory(entry.Content), embedding, entry.Timestamp,
                        importance, expiresAt));
                    _logger.LogDebug("语义记忆已写入：{MessageId}（group={GroupId}，importance={Importance}，expiresAt={ExpiresAt}）",
                        entry.MessageId, entry.GroupId, importance, expiresAt);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "语义记忆写入失败：{MessageId}（检查 embedding 提供方是否可用）", entry.MessageId);
                }
                finally { _embeddingLimiter.Release(); }
            }
        }
        catch (OperationCanceledException) { /* 释放时取消 */ }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "语义记忆写入队列异常终止");
        }
    }

    /// <summary>撤回消息的记忆（标记删除，检索不再命中）。</summary>
    public void Forget(string groupId, string messageId)
    {
        if (!_options.Enabled) return;
        try { _store.Remove(groupId, messageId); }
        catch (Exception ex) { _logger.LogDebug(ex, "语义记忆删除失败：{MessageId}", messageId); }
    }

    /// <summary>解散群时删除该群全部记忆（物理删除，群已不存在无需保留）。</summary>
    public void RemoveGroup(string groupId)
    {
        if (!_options.Enabled) return;
        try { _store.RemoveGroup(groupId); }
        catch (Exception ex) { _logger.LogDebug(ex, "语义记忆群级删除失败：{GroupId}", groupId); }
    }

    // ================= 记忆治理（分群分级 / 自动遗忘 / 可视化） =================

    public IReadOnlyList<MessageMemoryItem> ListMessages(string? groupId, string? senderId, string? keyword, int limit, int offset)
        => !_options.Enabled ? [] : _store.ListMessages(groupId, senderId, keyword, limit, offset);

    public long CountMessages(string? groupId, string? senderId, string? keyword)
        => !_options.Enabled ? 0 : _store.CountMessages(groupId, senderId, keyword);

    public IReadOnlyList<MessageMemoryGroupStat> GroupStats()
        => !_options.Enabled ? [] : _store.GroupStats(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    public bool DeleteByMessageId(string messageId)
    {
        if (!_options.Enabled) return false;
        try { return _store.DeleteByMessageId(messageId); }
        catch (Exception ex) { _logger.LogDebug(ex, "记忆删除失败：{MessageId}", messageId); return false; }
    }

    public MessageMemoryItem? GetByMessageId(string messageId)
    {
        if (!_options.Enabled) return null;
        try { return _store.GetByMessageId(messageId); }
        catch (Exception ex) { _logger.LogDebug(ex, "记忆单条查询失败：{MessageId}", messageId); return null; }
    }

    public bool UpdateImportance(string messageId, int importance)
    {
        if (!_options.Enabled) return false;
        try { return _store.UpdateImportance(messageId, importance); }
        catch (Exception ex) { _logger.LogDebug(ex, "记忆分级失败：{MessageId}", messageId); return false; }
    }

    /// <summary>手动遗忘：groupId 为空 = 全部群；retentionHours 为空 = 立即遗忘，否则保留最近 N 小时。</summary>
    public int ForgetGroup(string? groupId, double? retentionHours)
    {
        if (!_options.Enabled) return 0;
        try
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            long? expiresAt = retentionHours is > 0
                ? now + (long)(retentionHours.Value * 3_600_000)
                : null; // null → 立即过期（store 内落地为过去时间戳）
            return _store.SetExpiry(groupId, expiresAt, now);
        }
        catch (Exception ex) { _logger.LogDebug(ex, "记忆遗忘设置失败：{GroupId}", groupId ?? "*"); return 0; }
    }

    /// <summary>物理删除已过期记忆（自动遗忘定时清理执行）。</summary>
    public int PruneExpired()
    {
        if (!_options.Enabled) return 0;
        try { return _store.PruneExpired(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()); }
        catch (Exception ex) { _logger.LogDebug(ex, "过期记忆清理失败"); return 0; }
    }

    /// <summary>按语义相似度检索历史记忆（提供方不可用 / 未启用时返回空）。
    /// Scope=agent（默认）时检索该智能体所在的所有群。</summary>
    public async Task<IReadOnlyList<MessageMemoryHit>> SearchAsync(string groupId, string agentId, string query, CancellationToken ct = default)
    {
        if (!_options.Enabled || string.IsNullOrWhiteSpace(query)) return [];
        try
        {
            var embedding = await _embedding.EmbedAsync(Truncate(query, _options.MaxQueryChars), ct);
            if (embedding is null || embedding.Length == 0) return [];
            var hits = _store.Search(groupId, agentId, embedding, _options.TopK, _options.MinScore, _options.Scope);
            if (hits.Count > 0)
                _logger.LogDebug("语义记忆检索命中 {Count} 条（agent={AgentId}，scope={Scope}）：{Snippets}",
                    hits.Count, agentId, _options.Scope, string.Join(" | ", hits.Take(3).Select(h =>
                        (h.Content.Length > 40 ? h.Content[..40] + "…" : h.Content).ReplaceLineEndings(" "))));
            return _options.HybridSearch ? HybridRerank(hits, query) : hits;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "语义记忆检索失败（检查 embedding 提供方是否可用）");
            return [];
        }
    }

    /// <summary>按语义相似度检索某个人（用户或智能体）自己的历史发言（个人记忆），跨群且遵守私密群隔离。</summary>
    public async Task<IReadOnlyList<MessageMemoryHit>> SearchPersonAsync(string personId, string currentGroupId, string query, CancellationToken ct = default)
    {
        if (!_options.Enabled || string.IsNullOrWhiteSpace(query)) return [];
        try
        {
            var embedding = await _embedding.EmbedAsync(Truncate(query, _options.MaxQueryChars), ct);
            if (embedding is null || embedding.Length == 0) return [];
            var hits = _store.SearchPerson(personId, currentGroupId, embedding, _options.PersonalTopK, _options.PersonalMinScore);
            if (hits.Count > 0)
                _logger.LogDebug("个人记忆检索命中 {Count} 条（person={PersonId}）：{Snippets}",
                    hits.Count, personId, string.Join(" | ", hits.Take(3).Select(h =>
                        (h.Content.Length > 40 ? h.Content[..40] + "…" : h.Content).ReplaceLineEndings(" "))));
            return _options.HybridSearch ? HybridRerank(hits, query) : hits;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "个人记忆检索失败（检查 embedding 提供方是否可用）");
            return [];
        }
    }

    /// <summary>
    /// 混合检索精排（2.1）：在<b>既有稠密命中集合内</b>，用 BM25 词项评分与余弦相似度融合、重要级加成重排；
    /// 返回的条数与集合不变（只在同集合内调序，避免引入假阳性）。传入集合为空则原样返回。
    /// </summary>
    private IReadOnlyList<MessageMemoryHit> HybridRerank(IReadOnlyList<MessageMemoryHit> hits, string query)
        => hits.Count < 2
            ? hits
            : hits
                .Select(h => (Hit: h, Fused: Bm25Ranker.FusedScore(h.Score, Bm25Ranker.Score(query, h.Content), h.Importance, _options.HybridBm25Weight)))
                .OrderByDescending(x => x.Fused)
                .ThenBy(x => x.Hit.Score)
                .Select(x => x.Hit)
                .ToList();

    /// <summary>写入记忆前净化：剥离 prompt 注入段落（「相关历史记忆」「群最近对话」区块）。
    /// 模拟客户端会把注入段落回显进回复，不剥离会造成记忆嵌套污染。</summary>
    internal static string SanitizeForMemory(string content)
    {
        var c = content ?? "";
        var memoryIdx = c.IndexOf("以下是相关历史记忆", StringComparison.Ordinal);
        if (memoryIdx >= 0)
        {
            var end = c.IndexOf("以下是群最近对话", memoryIdx, StringComparison.Ordinal);
            c = c.Remove(memoryIdx, (end >= 0 ? end : c.Length) - memoryIdx);
        }
        var historyIdx = c.IndexOf("以下是群最近对话", StringComparison.Ordinal);
        if (historyIdx >= 0) c = c[..historyIdx];
        return c.Trim();
    }

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : text[..max];

    public void Dispose()
    {
        _writeCts.Cancel(); // 停止后台消费队列
        _writeQueue.Writer.TryComplete();
        _embeddingLimiter.Dispose();
        _embedding.Dispose();
    }
}
