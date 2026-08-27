using System.Collections.Concurrent;
using AguiGroupChat.Hub.Agents;
using AguiGroupChat.Hub.Infra;
using AguiGroupChat.Hub.Models;
using AguiGroupChat.Hub.Persistence;
using AguiGroupChat.Hub.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AguiGroupChat.Agents;

/// <summary>
/// 知识库目录：管理知识库与文档元数据（内存 + 快照持久化，同 AgentCatalog 机制）。
/// 文档正文切片向量化后存入 <see cref="IMessageMemoryStore"/>（GroupId 约定 <c>kb:{KbId}</c>），
/// 智能体回复前按绑定列表检索（见 <see cref="MemoryContextProvider"/>）。
/// 向量存储 / embedding 不可用（未启用语义记忆）时文档入库失败并返回明确错误，不影响群聊主流程。
/// </summary>
public sealed class KnowledgeBaseCatalog
{
    /// <summary>单文档切片上限（防超大文档打爆 embedding / 存储）。</summary>
    public const int MaxChunksPerDoc = 500;

    /// <summary>切片大小（字符）默认值，可用 <c>Agents:Memory:KnowledgeChunkSize</c> 覆盖。</summary>
    internal const int ChunkSize = 4096;

    /// <summary>切片重叠（字符）默认值，可用 <c>Agents:Memory:KnowledgeChunkOverlap</c> 覆盖。</summary>
    internal const int ChunkOverlap = 512;

    /// <summary>实际生效的切片大小（优先读配置，回退默认）。</summary>
    private int ConfigChunkSize => _options.Memory is { KnowledgeChunkSize: > 0 } m ? m.KnowledgeChunkSize : ChunkSize;

    /// <summary>实际生效的切片重叠（优先读配置；默认取切片 1/8，保证 ≤ 切片大小以免后退）。</summary>
    private int ConfigChunkOverlap => _options.Memory is { KnowledgeChunkOverlap: > 0 } m
        ? Math.Min(m.KnowledgeChunkOverlap, ConfigChunkSize - 1)
        : Math.Max(0, ConfigChunkSize / 8);

    /// <summary>GroupId 约定前缀：知识库向量的群维度 = kb:{KbId}。</summary>
    public const string KbGroupPrefix = "kb:";

    private readonly AgentOptions _options;
    private readonly IServiceProvider _services;
    private readonly ILogger _logger;
    private readonly ChangeHub? _changes;
    private readonly ConcurrentDictionary<string, KnowledgeBase> _kbs = new(StringComparer.Ordinal);

    /// <summary>文档处理中标记：docId → 后台任务（供测试等待与去重）。</summary>
    private readonly ConcurrentDictionary<string, Task> _processing = new(StringComparer.Ordinal);

    /// <summary>文档向量化并发上限（embedding 是资源密集操作，避免并发文档上传打爆本地模型 / 存储）。</summary>
    private static readonly SemaphoreSlim ProcessingGate = new(2, 2);

    public KnowledgeBaseCatalog(AgentOptions options, IServiceProvider services, ILoggerFactory loggerFactory, ChangeHub? changes = null)
    {
        _options = options;
        _services = services;
        _logger = loggerFactory.CreateLogger<KnowledgeBaseCatalog>();
        _changes = changes;
    }

    // ================= 目录管理 =================

    /// <summary>创建知识库，返回 KbId。</summary>
    public KnowledgeBase CreateKb(string name, string description, string? ownerId)
    {
        var kb = new KnowledgeBase
        {
            KbId = "kb_" + IdGenerator.NewId(),
            Name = name.Trim(),
            Description = description?.Trim() ?? "",
            OwnerId = ownerId,
            UpdatedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
        _kbs[kb.KbId] = kb;
        _changes?.Notify();
        return kb;
    }

    public KnowledgeBase? GetKb(string kbId) => _kbs.TryGetValue(kbId, out var kb) ? kb : null;

    /// <summary>可见列表：系统级（OwnerId=null）| 当前用户创建 | 管理员 | 当前用户是 SharedGroupIds 中某群成员（群级共享，2.4）。</summary>
    public IReadOnlyList<KnowledgeBase> ListKbs(string? userId, IReadOnlySet<string>? memberGroupIds = null, bool isAdmin = false)
        => _kbs.Values.Where(k =>
                k.OwnerId is null
                || (userId is not null && k.OwnerId == userId)
                || isAdmin
                || (memberGroupIds is { Count: > 0 } && k.SharedGroupIds.Any(memberGroupIds.Contains)))
            .OrderByDescending(k => k.UpdatedAtMs).ToList();

    /// <summary>是否允许某用户<b>读取 / 绑定</b>该知识库（保留只写权限给创建者 / 系统管理员）。</summary>
    public bool CanRead(KnowledgeBase kb, string? userId, IReadOnlySet<string>? memberGroupIds, bool isAdmin)
        => kb.OwnerId is null
           || (userId is not null && kb.OwnerId == userId)
           || isAdmin
           || (memberGroupIds is { Count: > 0 } && kb.SharedGroupIds.Any(memberGroupIds.Contains));

    /// <summary>是否只允许创建者 / 管理员改动文档（群共享为只读）。</summary>
    public bool CanWrite(KnowledgeBase kb, string? userId, bool isAdmin)
        => kb.OwnerId is null || (userId is not null && kb.OwnerId == userId) || isAdmin;

    public IReadOnlyList<KnowledgeBase> ListAll() => _kbs.Values.ToList();

    public void RestoreAll(IEnumerable<KnowledgeBase> kbs)
    {
        _kbs.Clear();
        foreach (var kb in kbs)
        {
            // 快照恢复时若有处理中（processing）文档，说明上次服务在向量化中途退出，标记为失败待重新上传
            foreach (var doc in kb.Documents)
            {
                if (string.Equals(doc.Status, "processing", StringComparison.OrdinalIgnoreCase))
                {
                    doc.Status = "error";
                    doc.Error = "服务重启导致处理中断，请重新上传";
                }
            }
            _kbs[kb.KbId] = kb;
        }
    }

    /// <summary>删除知识库：移除其全部向量 + 图谱（实体/关系） + 目录项。</summary>
    public bool RemoveKb(string kbId)
    {
        if (!_kbs.TryRemove(kbId, out _)) return false;
        try { _services.GetService<IMessageMemoryStore>()?.RemoveGroup(KbGroupPrefix + kbId); }
        catch (Exception ex) { _logger.LogWarning(ex, "删除知识库向量失败：{KbId}", kbId); }
        // 图谱隔离域：知识库实体的群维度 = kb:{KbId}，一并清理（防残留实体/关系被后续检索命中）
        try { _services.GetService<IGraphMemoryStore>()?.RemoveGroup(KbGroupPrefix + kbId); }
        catch (Exception ex) { _logger.LogWarning(ex, "删除知识库图谱失败：{KbId}", kbId); }
        _changes?.Notify();
        return true;
    }

    // ================= 文档 =================

    /// <summary>按附件 ID 添加知识文档：立即返回“处理中”文档记录，提取文本 → 切片 → 向量化在后台执行
    /// （向量化耗时较长，避免上传请求长时间阻塞；前端按 Status 轮询展示进度）。
    /// 向量存储 / embedding 不可用时同步返回错误说明。</summary>
    public async Task<(KbDocument? Doc, string? Error)> AddDocumentAsync(string kbId, string attachmentId, CancellationToken ct = default)
    {
        var kb = GetKb(kbId);
        if (kb is null) return (null, "知识库不存在");
        var store = _services.GetService<IMessageMemoryStore>();
        var embedding = _services.GetService<IEmbeddingProvider>();
        var attachments = _services.GetService<AttachmentStore>();
        if (store is null || embedding is null || attachments is null)
            return (null, "知识库不可用：需要启用语义记忆（Storage:Provider=postgres/sqlite 且 Agents:Memory:Enabled=true）");
        if (string.IsNullOrWhiteSpace(attachmentId)) return (null, "附件 ID 不能为空");

        var info = attachments.ResolvePath(attachmentId.Trim());
        if (info is null) return (null, "附件不存在或无法提取文本（仅支持 txt/md/json/csv 与 docx/xlsx/pptx/pdf）");
        var fileName = Path.GetFileName(info);
        var docId = "doc_" + IdGenerator.NewId();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var doc = new KbDocument
        {
            DocId = docId,
            FileName = fileName,
            AttachmentId = attachmentId.Trim(),
            Status = "processing",
            AddedAtMs = now,
        };
        kb.Documents.Add(doc);
        kb.UpdatedAtMs = now;
        _changes?.Notify();

        var task = Task.Run(() => ProcessDocumentAsync(kb, doc, store, embedding, attachments));
        _processing[docId] = task;
        _ = task.ContinueWith(t => _processing.TryRemove(docId, out _), TaskScheduler.Default);
        return (doc, null);
    }

    /// <summary>后台执行：提取文本 → 切片 → 向量化入库；任何失败把文档标记为 error。</summary>
    private async Task ProcessDocumentAsync(KnowledgeBase kb, KbDocument doc, IMessageMemoryStore store, IEmbeddingProvider embedding, AttachmentStore attachments)
    {
        try
        {
            if (!await ProcessingGate.WaitAsync(TimeSpan.FromSeconds(30)))
            {
                MarkError(doc, "排队超时，请稍后重试");
                return;
            }
            try
            {
                var text = await attachments.TryReadTextAsync(doc.AttachmentId, CancellationToken.None);
                if (text is null)
                {
                    MarkError(doc, "无法提取文档文本（仅支持 txt/md/json/csv 与 docx/xlsx/pptx/pdf）");
                    return;
                }
                await VectorizeAndStoreAsync(kb, doc, text, store, embedding);
            }
            finally
            {
                ProcessingGate.Release();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "知识库文档处理失败：{KbId}/{DocId}", kb.KbId, doc.DocId);
            MarkError(doc, $"处理失败：{ex.Message}");
        }
    }

    /// <summary>把一段原始文本异步入库为知识文档（1.3 记忆/结论沉淀）：切片 → 向量化 → 写入知识库向量表。</summary>
    public async Task<(KbDocument? Doc, string? Error)> AddTextDocumentAsync(string kbId, string fileName, string? text, CancellationToken ct = default)
    {
        var kb = GetKb(kbId);
        if (kb is null) return (null, "知识库不存在");
        var store = _services.GetService<IMessageMemoryStore>();
        var embedding = _services.GetService<IEmbeddingProvider>();
        var attachments = _services.GetService<AttachmentStore>();
        if (store is null || embedding is null || attachments is null)
            return (null, "知识库不可用：需要启用语义记忆（Storage:Provider=postgres/sqlite 且 Agents:Memory:Enabled=true）");
        if (string.IsNullOrWhiteSpace(text))
            return (null, "文本内容为空");

        var safeName = string.IsNullOrWhiteSpace(fileName) ? "consolidated.md" : Path.GetFileName(fileName);
        var docId = "doc_" + IdGenerator.NewId();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var doc = new KbDocument
        {
            DocId = docId,
            FileName = safeName,
            Status = "processing",
            AddedAtMs = now,
        };
        kb.Documents.Add(doc);
        kb.UpdatedAtMs = now;
        _changes?.Notify();

        var task = Task.Run(async () =>
        {
            if (!await ProcessingGate.WaitAsync(TimeSpan.FromSeconds(30)))
            {
                MarkError(doc, "排队超时，请稍后重试");
                return;
            }
            try { await VectorizeAndStoreAsync(kb, doc, text!, store, embedding); }
            finally { ProcessingGate.Release(); }
        });
        _processing[docId] = task;
        _ = task.ContinueWith(t => _processing.TryRemove(docId, out _), TaskScheduler.Default);
        _ = task.ContinueWith(t =>
        {
            if (t.IsFaulted) { _logger.LogError(t.Exception, "知识库文本文档处理失败：{KbId}/{DocId}", kb.KbId, docId); MarkError(doc, "处理失败"); }
        }, TaskScheduler.Default);
        return (doc, null);
    }

    /// <summary>切片 → 向量化 → 写入知识库向量表（attachment 与原始文本两条路径共用）。</summary>
    private async Task VectorizeAndStoreAsync(KnowledgeBase kb, KbDocument doc, string text, IMessageMemoryStore store, IEmbeddingProvider embedding)
    {
        var chunks = Chunk(text, ConfigChunkSize, ConfigChunkOverlap);
        if (chunks.Count == 0)
        {
            MarkError(doc, "文档内容为空");
            return;
        }
        if (chunks.Count > MaxChunksPerDoc)
        {
            MarkError(doc, $"文档过大（超过 {MaxChunksPerDoc} 个切片，约 {MaxChunksPerDoc * ConfigChunkSize} 字符），请拆分后上传");
            return;
        }

        var vectors = new List<float[]>(chunks.Count);
        for (var i = 0; i < chunks.Count; i++)
        {
            var vec = await embedding.EmbedAsync(chunks[i], CancellationToken.None);
            if (vec is null || vec.Length == 0)
            {
                MarkError(doc, "embedding 不可用（本地模型未加载或端点不可达），文档未入库");
                return;
            }
            vectors.Add(vec);
        }

        // 向量化期间文档可能已被用户移除，写入前再确认（避免孤儿向量）
        if (!_kbs.TryGetValue(kb.KbId, out var current) || !current.Documents.Contains(doc))
        {
            _logger.LogInformation("知识库 {KbId} 文档 {DocId} 处理期间已被移除，丢弃向量", kb.KbId, doc.DocId);
            return;
        }

        var groupId = KbGroupPrefix + kb.KbId;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        for (var i = 0; i < chunks.Count; i++)
        {
            store.Upsert(new MessageMemoryRecord(
                MessageId: doc.DocId + ":" + i,
                GroupId: groupId,
                TopicId: "kb",
                SenderId: doc.FileName,
                SenderType: "kb",
                Content: chunks[i],
                Embedding: vectors[i],
                Timestamp: now));
        }

        // 图谱 RAG（启用时）：对文档文本抽实体/关系，建入隔离域 kb:{KbId} 的图谱（与向量切片并列）
        await MaybeStoreKbGraphAsync(kb, text, embedding);

        doc.ChunkCount = chunks.Count;
        doc.Status = "ready";
        doc.Error = null;
        kb.UpdatedAtMs = now;
        _changes?.Notify();
        _logger.LogInformation("知识库 {KbId} 文档 {File} 入库完成：{Chunks} 个切片", kb.KbId, doc.FileName, chunks.Count);
    }

    /// <summary>
    /// 知识库图谱抽取（图谱 RAG，启用时）：对文档文本抽「实体-关系-实体」建入隔离域 <c>kb:{KbId}</c> 的图谱，
    /// 供 <see cref="SearchGraphAsync"/> 在知识库检索时做种子召回 + 图遍历补强。
    /// 图存储 / 解析器不可用（未启用图谱）时静默跳过，不影响向量切片入库；任何失败仅记日志。</summary>
    private async Task MaybeStoreKbGraphAsync(KnowledgeBase kb, string text, IEmbeddingProvider embedding)
    {
        if (!_options.Memory.GraphEnabled) return;
        var graphStore = _services.GetService<IGraphMemoryStore>();
        var extractor = _services.GetService<GraphEntityExtractor>();
        if (graphStore is null || extractor is null) return; // 图谱未启用 / 图存储不可用
        try
        {
            var content = text ?? "";
            var maxChars = Math.Max(1, _options.Memory.GraphMaxChars);
            if (content.Length > maxChars) content = content[..maxChars];
            var extraction = await extractor.ExtractAsync(content, CancellationToken.None);
            if (extraction.Entities.Count == 0 && extraction.Edges.Count == 0) return;

            var domain = KbGroupPrefix + kb.KbId;
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var idByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in extraction.Entities)
            {
                var id = GraphMemory.NormalizeEntityId(e.Name);
                idByName[e.Name] = id;
                var vec = await embedding.EmbedAsync(e.Name);
                if (vec is null || vec.Length == 0) continue;
                graphStore.UpsertEntity(new GraphEntityRecord(id, e.Name, e.Type ?? "Concept", domain, e.Description, vec, now));
            }
            foreach (var ed in extraction.Edges)
            {
                var srcId = idByName.TryGetValue(ed.Source, out var s) ? s : GraphMemory.NormalizeEntityId(ed.Source);
                var dstId = idByName.TryGetValue(ed.Target, out var d) ? d : GraphMemory.NormalizeEntityId(ed.Target);
                if (srcId == dstId) continue;
                graphStore.UpsertEdge(new GraphEdgeRecord(srcId, ed.Relation, dstId, domain, ed.Source, ed.Target));
            }
            _logger.LogInformation("知识库 {KbId} 图谱入库：{Entities} 实体 / {Edges} 关系（domain={Domain}）",
                kb.KbId, extraction.Entities.Count, extraction.Edges.Count, domain);
        }
        catch (Exception ex)
        {
            // 图谱失败不影响文档向量入库（主流程继续）
            _logger.LogWarning(ex, "知识库图谱抽取失败：{KbId}/{File}", kb.KbId, kb.Name);
        }
    }

    /// <summary>把文档标记为失败并刷新快照。</summary>
    private void MarkError(KbDocument doc, string message)
    {
        doc.Status = "error";
        doc.Error = message;
        _changes?.Notify();
        _logger.LogWarning("知识库文档 {File} 处理失败：{Msg}", doc.FileName, message);
    }

    /// <summary>等待某文档处理完成（返回后文档为 ready 或 error）。供测试与启动恢复同步使用。</summary>
    public async Task WaitForDocumentAsync(string docId, CancellationToken ct = default)
    {
        if (_processing.TryGetValue(docId, out var task))
            await task.WaitAsync(ct); // ct 取消时抛 OperationCanceledException，由调用方处理
    }

    /// <summary>等待所有处理中文档完成（供测试）。</summary>
    public async Task WaitForAllPendingAsync(CancellationToken ct = default)
    {
        while (_processing.Count > 0 && !ct.IsCancellationRequested)
        {
            var tasks = _processing.Values.ToArray();
            if (tasks.Length == 0) break;
            await Task.WhenAll(tasks).WaitAsync(ct);
        }
    }

    /// <summary>移除知识文档并删除其向量。</summary>
    public bool RemoveDocument(string kbId, string docId)
    {
        var kb = GetKb(kbId);
        if (kb is null) return false;
        var doc = kb.Documents.FirstOrDefault(d => d.DocId == docId);
        if (doc is null) return false;
        var store = _services.GetService<IMessageMemoryStore>();
        if (store is not null)
        {
            try
            {
                for (var i = 0; i < doc.ChunkCount; i++)
                    store.Remove(KbGroupPrefix + kbId, docId + ":" + i);
            }
            catch (Exception ex) { _logger.LogWarning(ex, "删除文档向量失败：{DocId}", docId); }
        }
        kb.Documents.Remove(doc);
        kb.UpdatedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _changes?.Notify();
        return true;
    }

    // ================= 记忆 / 结论沉淀（1.3） =================

    /// <summary>
    /// 把某群标记为「关键」（importance≥Critical）的记忆聚合为一份知识文档写入知识库
    /// （让群内重要结论随对话自动沉淀为长期知识，供智能体绑定检索）。
    /// 聚合为确定性文本（按话题分组、按时间排序），不调用模型；「经模型润色」留作后续增强。
    /// 返回 (文档, 错误, 沉淀的记忆条数)。
    /// </summary>
    public async Task<(KbDocument? Doc, string? Error, int MemoryCount)> ConsolidateGroupMemoriesAsync(
        string groupId, string kbId, IMessageMemoryStore store, CancellationToken ct = default)
    {
        if (GetKb(kbId) is null) return (null, "知识库不存在", 0);

        // 收集该群全部记忆，筛选出「关键」级别（importance>=2）
        var all = new List<MessageMemoryItem>();
        var offset = 0;
        while (!ct.IsCancellationRequested)
        {
            var page = store.ListMessages(groupId, null, null, 200, offset);
            if (page.Count == 0) break;
            all.AddRange(page);
            offset += page.Count;
            if (page.Count < 200) break;
        }
        var critical = all
            .Where(m => m.Importance >= MemoryImportance.Critical)
            .OrderBy(m => m.Timestamp)
            .ToList();
        if (critical.Count == 0)
            return (null, "该群暂无标记为「关键」的记忆（可先在记忆管理中把重要结论设为「关键」后重试）", 0);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"# 群关键结论沉淀");
        sb.AppendLine($"群：{groupId}；生成时间：{DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm}（UTC）。");
        sb.AppendLine();
        foreach (var topic in critical.GroupBy(m => string.IsNullOrEmpty(m.TopicId) ? "main" : m.TopicId))
        {
            sb.AppendLine($"## 话题 {topic.Key}");
            foreach (var m in topic)
            {
                var ts = DateTimeOffset.FromUnixTimeMilliseconds(m.Timestamp).ToString("yyyy-MM-dd HH:mm");
                sb.AppendLine($"- [{ts}] {m.SenderId}：{m.Content}");
            }
            sb.AppendLine();
        }

        var name = $"群关键结论-{DateTime.Now:yyyyMMdd-HHmm}.md";
        var (doc, error) = await AddTextDocumentAsync(kbId, name, sb.ToString(), ct);
        return (doc, error, critical.Count);
    }

    // ================= 检索 =================

    /// <summary>检索结果条目。</summary>
    public sealed record KbHit(string KbId, string KbName, string FileName, string Content, double Score);

    /// <summary>在指定知识库集合中按语义检索 top-k 片段（每个知识库各取 TopK 条，按相似度合并排序）。</summary>
    public async Task<IReadOnlyList<KbHit>> SearchAsync(IReadOnlyList<string> kbIds, string query, int topK, double minScore, CancellationToken ct = default)
    {
        if (kbIds.Count == 0 || string.IsNullOrWhiteSpace(query)) return [];
        var store = _services.GetService<IMessageMemoryStore>();
        var embedding = _services.GetService<IEmbeddingProvider>();
        if (store is null || embedding is null) return [];
        float[]? vec;
        try { vec = await embedding.EmbedAsync(query, ct); }
        catch (Exception ex) { _logger.LogDebug(ex, "知识库检索 embedding 失败"); return []; }
        if (vec is null || vec.Length == 0) return [];

        var hits = new List<KbHit>();
        foreach (var kbId in kbIds)
        {
            var kb = GetKb(kbId);
            if (kb is null) continue;
            try
            {
                foreach (var hit in store.Search(KbGroupPrefix + kbId, "kb", vec, Math.Max(1, topK), minScore, "group"))
                {
                    hits.Add(new KbHit(kbId, kb.Name, hit.SenderId, hit.Content, hit.Score));
                }
            }
            catch (Exception ex) { _logger.LogDebug(ex, "知识库 {KbId} 检索失败", kbId); }
        }

        // 关键词召回兜底（防止向量相似度低于阈值就丢命中的高频特征词）
        // 纯语义检索对“专享福利假”之类的稀有词 / 长目录文本容易低于 minScore 而被过滤；
        // 这里用 BM25 对知识库切片做一次关键词评分，把词面命中但向量漏掉的片段补回来。
        try { hits.AddRange(KeywordRecall(store, kbIds, query, topK)); }
        catch (Exception ex) { _logger.LogDebug(ex, "知识库关键词召回失败"); }

        // 按内容去重（向量命中与关键词命中可能落在同一片段），合并后统一按融合分排序
        return hits
            .GroupBy(h => h.Content, StringComparer.Ordinal)
            .Select(g => g.OrderByDescending(h => h.Score).First())
            .OrderByDescending(h => h.Score)
            .Take(topK)
            .ToList();
    }

    /// <summary>
    /// 知识库图谱检索（图谱 RAG，启用时）：在指定知识库集合的<b>图谱隔离域</b> <c>kb:{KbId}</c> 内
    /// 做「语义召回种子实体 → n 跳图遍历」，返回可达子图（实体 + 关系边）供注入——与向量切片检索
    /// (<see cref="SearchAsync"/>) 并列，补强知识文档中的关系型知识。图存储未启用/不可用返回空。</summary>
    public async Task<GraphSubgraph> SearchGraphAsync(IReadOnlyList<string> kbIds, string query, int topK, double minScore, int hops, int maxNodes, CancellationToken ct = default)
    {
        var empty = new GraphSubgraph([], []);
        if (!_options.Memory.GraphEnabled || kbIds.Count == 0 || string.IsNullOrWhiteSpace(query)) return empty;
        var graphStore = _services.GetService<IGraphMemoryStore>();
        var embedding = _services.GetService<IEmbeddingProvider>();
        if (graphStore is null || embedding is null) return empty;
        try
        {
            var vec = await embedding.EmbedAsync(query, ct);
            if (vec is null || vec.Length == 0) return empty;

            var entities = new Dictionary<string, GraphEntityHit>(StringComparer.Ordinal);
            var edges = new List<GraphEdgeHit>();
            var seedK = Math.Max(1, topK);
            foreach (var kbId in kbIds)
            {
                if (GetKb(kbId) is null) continue;
                var domain = KbGroupPrefix + kbId;
                var seeds = graphStore.SearchEntities(vec, seedK, minScore, domain);
                foreach (var seed in seeds.Take(seedK))
                {
                    var sub = graphStore.ExpandSubgraph(seed.EntityId, Math.Clamp(hops, 1, 4), Math.Clamp(maxNodes, 1, 200));
                    foreach (var e in sub.Entities)
                        entities.TryAdd(e.EntityId, e with { Score = e.Score != 0 ? e.Score : seed.Score, Hop = e.Hop });
                    foreach (var ed in sub.Edges)
                        if (!edges.Any(x => x.SourceId == ed.SourceId && x.Relation == ed.Relation && x.TargetId == ed.TargetId))
                            edges.Add(ed);
                    if (entities.Count >= maxNodes) break;
                }
                if (entities.Count >= maxNodes) break;
            }
            var ordered = entities.Values.OrderBy(e => e.Hop).ThenByDescending(e => e.Score).Take(maxNodes).ToList();
            if (ordered.Count == 0) return empty;
            _logger.LogDebug("知识库图谱检索命中 {Entities} 实体 / {Edges} 边（kbs={Kbs}）", ordered.Count, edges.Count, string.Join(",", kbIds));
            return new GraphSubgraph(ordered, edges);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "知识库图谱检索失败（已跳过，退回纯向量）");
            return empty;
        }
    }

    /// <summary>关键词召回：用 BM25 在候选切片集合内检索与查询高度词面相关的片段；用于兜底纯语义检索丢命中的场景。</summary>
    private List<KbHit> KeywordRecall(IMessageMemoryStore store, IReadOnlyList<string> kbIds, string query, int topK)
    {
        // 候选切片：限制扫描规模（每库取最近 N 片，覆盖绝大多数知识提问；超大库由纯语义路径兜底）
        var candidateCap = Math.Max(topK, 120);
        var scored = new List<KbHit>();
        foreach (var kbId in kbIds)
        {
            var kb = GetKb(kbId);
            if (kb is null) continue;
            var items = store.ListMessages(KbGroupPrefix + kbId, null, null, candidateCap, 0);
            foreach (var it in items)
            {
                var bm25 = Bm25Ranker.Score(query, it.Content);
                if (bm25 <= 0.0) continue; // 无任何查询词命中，跳过（避免大量弱相关噪音）
                scored.Add(new KbHit(kbId, kb.Name, it.SenderId, it.Content, bm25));
            }
        }
        return scored.OrderByDescending(h => h.Score).Take(topK).ToList();
    }

    /// <summary>长文本智能切片：优先沿换行 / 句末标点收尾，避免在句子中间硬切切断语义；
    /// 相邻切片携带重叠尾部（降低边界信息丢失）。窗口与重叠可传参（生产经配置 <c>KnowledgeChunkSize</c> / <c>KnowledgeChunkOverlap</c> 传入）。</summary>
    internal static List<string> Chunk(string text, int chunkSize = ChunkSize, int overlap = ChunkOverlap)
    {
        text = text.Trim();
        if (string.IsNullOrEmpty(text)) return [];
        var win = chunkSize > 0 ? chunkSize : ChunkSize;
        var ov = overlap >= 0 ? overlap : ChunkOverlap;
        if (text.Length <= win) return [text];

        // 切点回找下限：切割位置至少覆盖窗口的一半，避免切出过碎、失去边界的切片。
        var minCut = Math.Min(win, Math.Max(200, win / 2));

        var chunks = new List<string>();
        var pos = 0;
        while (pos < text.Length)
        {
            var winLen = Math.Min(win, text.Length - pos);
            var windowEnd = pos + winLen;
            var cut = windowEnd;
            if (windowEnd < text.Length)
            {
                var window = text.Substring(pos, winLen);
                // 1) 优先在换行符之后收尾（按自然段落切，尽量不断句）
                var nl = window.LastIndexOf('\n');
                if (nl >= 0 && (nl + 1) >= minCut)
                {
                    cut = pos + nl + 1;
                }
                else
                {
                    // 2) 无合适换行时，回找句末标点收尾
                    var se = FindSentenceEnd(window);
                    if (se >= 0 && (se + 1) >= minCut)
                        cut = pos + se + 1;
                }
                // 找不到合适边界则保持硬切（cut = windowEnd），保证不产生碎片
            }
            chunks.Add(text.Substring(pos, cut - pos));
            if (cut >= text.Length) break;
            // 下一片起点 = 切点 - 重叠（携带上一片尾部）；重叠比本片还长时保守回退到切点，避免后退/死循环
            var next = cut - ov;
            pos = next <= pos ? cut : next;
        }
        return chunks;
    }

    /// <summary>在切片窗口内从尾部回找最后一个句子结束标点（找不到返回 -1）。</summary>
    private static int FindSentenceEnd(string window)
    {
        for (var i = window.Length - 1; i >= 0; i--)
        {
            var c = window[i];
            if (c == '。' || c == '！' || c == '？' || c == '；' || c == '.' || c == '!' || c == '?' || c == ';')
                return i;
        }
        return -1;
    }
}
