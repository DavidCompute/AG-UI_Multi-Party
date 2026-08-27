using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using AguiGroupChat.Hub.Agents;
using AguiGroupChat.Hub.Models;
using AguiGroupChat.Hub.Persistence;
using Microsoft.Extensions.Logging;

namespace AguiGroupChat.Agents;

/// <summary>
/// 图谱记忆（Graph RAG）编排：把群消息异步抽取为「实体-关系-实体」写入图存储（<see cref="IGraphMemoryStore"/>，
/// PostgreSQL/SQLite 各自实现），触发回复前按查询语义召回种子实体 + n 跳图遍历取回子图供注入。
/// 与 <see cref="AgentMessageMemory"/> 并列但独立：embedding 复用同一 <see cref="IEmbeddingProvider"/>；
/// 未启用（Memory.GraphEnabled=false / store 不可用）时对既有流程完全透明。
/// </summary>
public sealed class GraphMemory : IGraphMemory, IDisposable
{
    private readonly IGraphMemoryStore? _store;
    private readonly IEmbeddingProvider _embedding;
    private readonly GraphEntityExtractor _extractor;
    private readonly MemoryOptions _options;
    private readonly ILogger<GraphMemory> _logger;
    private readonly SemaphoreSlim _embeddingLimiter = new(2);
    private readonly Channel<GraphMessageEntry> _writeQueue;
    private readonly CancellationTokenSource _writeCts = new();

    public GraphMemory(
        IGraphMemoryStore? store,
        IEmbeddingProvider embedding,
        GraphEntityExtractor extractor,
        AgentOptions options,
        ILogger<GraphMemory> logger)
    {
        _store = store;
        _embedding = embedding;
        _extractor = extractor;
        _options = options.Memory;
        _logger = logger;
        _writeQueue = Channel.CreateBounded<GraphMessageEntry>(
            new BoundedChannelOptions(Math.Max(16, _options.GraphQueueCapacity))
            {
                FullMode = BoundedChannelFullMode.DropWrite,
                SingleReader = true,
            });
        if (_store is not null && _options.GraphEnabled)
        {
            _logger.LogInformation("图谱记忆已启用：TopK={TopK} MinScore={MinScore} Hops={Hops} MaxNodes={MaxNodes}",
                _options.GraphTopK, _options.GraphMinScore, _options.GraphHops, _options.GraphMaxNodes);
            _ = Task.Run(ProcessWriteQueueAsync);
        }
        else
        {
            _logger.LogDebug("图谱记忆未启用（GraphEnabled=false 或图存储不可用）");
        }
    }

    public void Remember(GraphMessageEntry entry)
    {
        if (_store is null || !_options.GraphEnabled || string.IsNullOrWhiteSpace(entry.Content)) return;
        if (!_writeQueue.Writer.TryWrite(entry))
            _logger.LogDebug("图谱抽取队列已满（{Capacity}），本条已丢弃：{GroupId}", _options.GraphQueueCapacity, entry.GroupId);
    }

    public void RemoveGroup(string groupId)
    {
        if (_store is null) return;
        try { _store.RemoveGroup(groupId); }
        catch (Exception ex) { _logger.LogDebug(ex, "图谱群删除失败：{GroupId}", groupId); }
    }

    public void ClearAll()
    {
        if (_store is null) return;
        try { _store.ClearAll(); }
        catch (Exception ex) { _logger.LogWarning(ex, "图谱清空失败"); }
    }

    public async Task<GraphSubgraph> SearchAsync(string groupId, string query, CancellationToken ct = default)
    {
        if (_store is null || !_options.GraphEnabled || string.IsNullOrWhiteSpace(query)) return new GraphSubgraph([], []);
        try
        {
            var maxQueryChars = Math.Max(1, _options.MaxQueryChars);
            var q = query.Length > maxQueryChars ? query[..maxQueryChars] : query;
            var embedding = await _embedding.EmbedAsync(q, ct);
            if (embedding is null || embedding.Length == 0) return new GraphSubgraph([], []);

            var seeds = _store.SearchEntities(embedding, Math.Max(1, _options.GraphTopK), _options.GraphMinScore, groupId);
            if (seeds.Count == 0) return new GraphSubgraph([], []);

            // 各种子做 n 跳遍历，去重合并实体与边，受 MaxNodes 上限保护
            var entities = new Dictionary<string, GraphEntityHit>(StringComparer.Ordinal);
            var edges = new List<GraphEdgeHit>();
            foreach (var seed in seeds.Take(Math.Max(1, _options.GraphTopK)))
            {
                var sub = _store.ExpandSubgraph(seed.EntityId, _options.GraphHops, _options.GraphMaxNodes);
                foreach (var e in sub.Entities) entities.TryAdd(e.EntityId, e with { Score = e.Score != 0 ? e.Score : seed.Score, Hop = e.Hop });
                foreach (var ed in sub.Edges)
                    if (!edges.Any(x => x.SourceId == ed.SourceId && x.Relation == ed.Relation && x.TargetId == ed.TargetId))
                        edges.Add(ed);
                if (entities.Count >= _options.GraphMaxNodes) break;
            }

            var ordered = entities.Values
                .OrderBy(e => e.Hop)
                .ThenByDescending(e => e.Score)
                .Take(_options.GraphMaxNodes)
                .ToList();
            if (ordered.Count == 0) return new GraphSubgraph([], []);
            _logger.LogDebug("图谱检索命中 {Entities} 实体 / {Edges} 边（group={GroupId}）", ordered.Count, edges.Count, groupId);
            return new GraphSubgraph(ordered, edges);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "图谱检索失败（检查 embedding 提供方是否可用）");
            return new GraphSubgraph([], []);
        }
    }

    public GraphStats Stats() => _store?.Stats() ?? new GraphStats(0, 0, 0);

    private async Task ProcessWriteQueueAsync()
    {
        try
        {
            await foreach (var entry in _writeQueue.Reader.ReadAllAsync(_writeCts.Token))
            {
                try
                {
                    if (!await _embeddingLimiter.WaitAsync(TimeSpan.FromSeconds(10))) continue;
                }
                catch (OperationCanceledException) { return; }
                try
                {
                    var content = AgentMessageMemory.SanitizeForMemory(entry.Content);
                    if (content.Length > _options.GraphMaxChars) content = content[.._options.GraphMaxChars];
                    var extraction = await _extractor.ExtractAsync(content, _writeCts.Token);
                    if (extraction.Entities.Count == 0 && extraction.Edges.Count == 0) continue;

                    // 实体与边名做统一 normalize（图传播引用一致），并向量化实体名供种子召回
                    var idByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var e in extraction.Entities)
                    {
                        var id = NormalizeEntityId(e.Name);
                        idByName[e.Name] = id;
                        var emb = await _embedding.EmbedAsync(e.Name);
                        if (emb is null || emb.Length == 0) continue;
                        _store!.UpsertEntity(new GraphEntityRecord(id, e.Name, e.Type ?? "Concept", entry.GroupId,
                            e.Description, emb, entry.Timestamp));
                    }
                    foreach (var ed in extraction.Edges)
                    {
                        var srcId = idByName.TryGetValue(ed.Source, out var s) ? s : NormalizeEntityId(ed.Source);
                        var dstId = idByName.TryGetValue(ed.Target, out var d) ? d : NormalizeEntityId(ed.Target);
                        if (srcId == dstId) continue;
                        _store!.UpsertEdge(new GraphEdgeRecord(srcId, ed.Relation, dstId, entry.GroupId, ed.Source, ed.Target));
                    }
                    _logger.LogDebug("图谱已写入：group={GroupId} 实体{Entities} 边{Edges}",
                        entry.GroupId, extraction.Entities.Count, extraction.Edges.Count);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "图谱抽取写入失败：group={GroupId}", entry.GroupId);
                }
                finally { _embeddingLimiter.Release(); }
            }
        }
        catch (OperationCanceledException) { /* 释放时取消 */ }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "图谱抽取队列异常终止");
        }
    }

    /// <summary>实体 id 规范化：去首尾空白 + 小写取 SHA-256 前 20 位十六进制（幂等、防特殊字符污染主键）。</summary>
    internal static string NormalizeEntityId(string name)
    {
        var key = (name ?? "").Trim();
        if (key.Length == 0) return key;
        var lower = key.ToLowerInvariant();
        // 长度足够短时直接用规范化名做 id，便于调试阅读
        if (lower.Length > 0 && lower.Length <= 80 && lower.All(ch => char.IsLetterOrDigit(ch) || ch is '_' or '-'))
            return lower;
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(lower));
        var sb = new StringBuilder(20);
        foreach (var b in bytes.Take(10)) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    public void Dispose()
    {
        _writeCts.Cancel();
        _writeQueue.Writer.TryComplete();
        _embeddingLimiter.Dispose();
    }
}
