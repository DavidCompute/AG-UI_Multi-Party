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
    /// <summary>切片大小（字符）。</summary>
    public const int ChunkSize = 800;

    /// <summary>切片重叠（字符），避免跨片语义截断。</summary>
    public const int ChunkOverlap = 100;

    /// <summary>单文档切片上限（防超大文档打爆 embedding / 存储）。</summary>
    public const int MaxChunksPerDoc = 500;

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

    /// <summary>删除知识库：移除其全部向量 + 目录项。</summary>
    public bool RemoveKb(string kbId)
    {
        if (!_kbs.TryRemove(kbId, out _)) return false;
        try { _services.GetService<IMessageMemoryStore>()?.RemoveGroup(KbGroupPrefix + kbId); }
        catch (Exception ex) { _logger.LogWarning(ex, "删除知识库向量失败：{KbId}", kbId); }
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
        var chunks = Chunk(text);
        if (chunks.Count == 0)
        {
            MarkError(doc, "文档内容为空");
            return;
        }
        if (chunks.Count > MaxChunksPerDoc)
        {
            MarkError(doc, $"文档过大（超过 {MaxChunksPerDoc} 个切片，约 {MaxChunksPerDoc * ChunkSize} 字符），请拆分后上传");
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

        doc.ChunkCount = chunks.Count;
        doc.Status = "ready";
        doc.Error = null;
        kb.UpdatedAtMs = now;
        _changes?.Notify();
        _logger.LogInformation("知识库 {KbId} 文档 {File} 入库完成：{Chunks} 个切片", kb.KbId, doc.FileName, chunks.Count);
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
        return hits.OrderByDescending(h => h.Score).Take(topK).ToList();
    }

    /// <summary>长文本切片（按字符固定长度 + 重叠）。</summary>
    internal static List<string> Chunk(string text, int chunkSize = ChunkSize, int overlap = ChunkOverlap)
    {
        text = text.Trim();
        if (string.IsNullOrEmpty(text)) return [];
        if (text.Length <= chunkSize) return [text];
        var chunks = new List<string>();
        var pos = 0;
        while (pos < text.Length)
        {
            var len = Math.Min(chunkSize, text.Length - pos);
            chunks.Add(text.Substring(pos, len));
            if (pos + len >= text.Length) break;
            pos += len - overlap;
        }
        return chunks;
    }
}
