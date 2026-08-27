using AguiGroupChat.Agents;
using AguiGroupChat.Hub.Agents;
using AguiGroupChat.Hub.Models;
using AguiGroupChat.Hub.Persistence;
using AguiGroupChat.Hub.Persistence.Relational;
using AguiGroupChat.Hub.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>图谱记忆（Graph RAG）：SQLite 图存储 + 规则抽取 + 编排（写入→递归 CTE 图遍历检索）。</summary>
public sealed class GraphMemoryStoreTests
{
    private static (SqliteStore Store, RelationalGraphMemoryStore Graph) CreateGraph(int dims = 8)
    {
        // cache=shared 内存库：保证建表 / 写入 / 检索同库；库名唯一防并行污染
        var store = new SqliteStore($"Data Source=file:graph-{Guid.NewGuid():N}?mode=memory&cache=shared");
        store.EnsureSchema();
        var graph = new RelationalGraphMemoryStore(store, dims, NullLogger<RelationalGraphMemoryStore>.Instance);
        graph.EnsureSchema();
        return (store, graph);
    }

    private static GraphEntityRecord Entity(string id, string name, string type, string group, float[] v, string? desc = null)
        => new(id, name, type, group, desc, v, 1000);

    private static float[] OneHot(int idx, int dims = 8)
    {
        var v = new float[dims];
        v[idx] = 1f;
        return v;
    }

    [Fact]
    public void UpsertEntityEdge_ExpandSubgraph_ReturnsReachableNeighborsViaRecursiveCte()
    {
        var (_, graph) = CreateGraph();
        // 构图：A -[knows]-> B -[likes]-> C，A -[works]-> D
        graph.UpsertEntity(Entity("a", "Alice", "Person", "g1", OneHot(0)));
        graph.UpsertEntity(Entity("b", "Bob", "Person", "g1", OneHot(1)));
        graph.UpsertEntity(Entity("c", "CBD", "Technology", "g1", OneHot(2)));
        graph.UpsertEntity(Entity("d", "DeepSeek", "Organization", "g1", OneHot(3)));
        graph.UpsertEdge(new GraphEdgeRecord("a", "knows", "b", "g1", "Alice", "Bob"));
        graph.UpsertEdge(new GraphEdgeRecord("b", "likes", "c", "g1", "Bob", "CBD"));
        graph.UpsertEdge(new GraphEdgeRecord("a", "works", "d", "g1", "Alice", "DeepSeek"));

        // 从 A 做 2 跳遍历：命中 B、C、D 全部，且抽出 A-B、B-C、A-D 三条边
        var sub = graph.ExpandSubgraph("a", hops: 2, maxNodes: 10);
        var names = sub.Entities.Select(e => e.Name).ToHashSet();
        Assert.Contains("Alice", names);
        Assert.Contains("Bob", names);
        Assert.Contains("CBD", names);
        Assert.Contains("DeepSeek", names);
        Assert.Equal(3, sub.Edges.Count);

        // 从 C（叶子）1 跳：只到 B
        var leaf = graph.ExpandSubgraph("c", hops: 1, maxNodes: 10);
        Assert.Contains(leaf.Entities, e => e.Name == "CBD");
        Assert.Contains(leaf.Entities, e => e.Name == "Bob");
    }

    [Fact]
    public void SearchEntities_ReturnsTopSimilar_SeedForTraversal()
    {
        var (_, graph) = CreateGraph();
        graph.UpsertEntity(Entity("tech", "DeepSeek", "Organization", "g1", OneHot(0)));
        graph.UpsertEntity(Entity("other", "天气", "Concept", "g1", OneHot(1)));

        var seeds = graph.SearchEntities(OneHot(0), topK: 3, minScore: 0.5, groupId: null);
        var hit = Assert.Single(seeds);
        Assert.Equal("DeepSeek", hit.Name);
    }

    [Fact]
    public void RemoveGroup_And_Stats_Reflect_Deletion()
    {
        var (_, graph) = CreateGraph();
        graph.UpsertEntity(Entity("a", "A", "Concept", "g1", OneHot(0)));
        graph.UpsertEdge(new GraphEdgeRecord("a", "r", "b", "g1", "A", "B"));
        graph.UpsertEntity(Entity("x", "X", "Concept", "g2", OneHot(1)));

        Assert.True(graph.Stats().EntityCount >= 2);
        graph.RemoveGroup("g1");
        var stats = graph.Stats();
        Assert.Equal(1, stats.EntityCount); // 只剩 g2 的 X
    }

    [Fact]
    public async Task Extract_RuleFallback_ProducesEntitiesAndEdges_OnMockProvider()
    {
        var options = new AgentOptions { Provider = "mock" };
        var extractor = new GraphEntityExtractor(options, NullLogger<GraphEntityExtractor>.Instance);

        var ex = await extractor.ExtractAsync("张三负责「需求评审」项目，李四使用 PostgreSQL", CancellationToken.None);

        Assert.NotEmpty(ex.Entities);
        // 书名号内容与专有名词实体都应被抽到
        Assert.Contains(ex.Entities, e => e.Name == "需求评审");
    }

    /// <summary>测试用固定向量 embedding（按首字符散列到 one-hot 附近），驱动 GraphMemory 编排。</summary>
    private sealed class StubEmbedding : IEmbeddingProvider
    {
        public Task<float[]?> EmbedAsync(string text, CancellationToken ct = default)
        {
            var idx = text.Length % 8;
            var v = new float[8];
            v[idx] = 1f;
            return Task.FromResult<float[]?>(v);
        }
        public void Dispose() { }
    }

    [Fact]
    public async Task GraphMemory_WritesThenSearches_ReturnsConnectedSubgraph()
    {
        var (_, store) = CreateGraph();
        var options = new AgentOptions
        {
            Provider = "mock",
            Memory = new MemoryOptions { GraphEnabled = true, GraphTopK = 3, GraphMinScore = 0.1, GraphHops = 2, GraphMaxNodes = 20 },
        };
        var extractor = new GraphEntityExtractor(options, NullLogger<GraphEntityExtractor>.Instance);
        var graph = new GraphMemory(store, new StubEmbedding(), extractor, options, NullLogger<GraphMemory>.Instance);

        // 写入两条含书名号实体的消息（同一 group）
        graph.Remember(new GraphMessageEntry("g1", "u1", "DeepSeek「V3模型」发布", 1000));
        graph.Remember(new GraphMessageEntry("g1", "u2", "V3模型 使用 MoE架构", 2000));
        await Task.Delay(300); // 等待异步抽取落盘

        // 检索：query 命中任一实体，图遍历应带回相连实体与边
        var sub = await graph.SearchAsync("g1", "V3模型", CancellationToken.None);
        Assert.False(sub.IsEmpty);
        Assert.Contains(sub.Entities, e => e.Name == "V3模型");
        Assert.Contains(sub.Entities, e => e.Name == "MoE架构"); // BFS 相连
        Assert.Contains(sub.Edges, e => e.Relation is "使用");

        graph.Dispose();
    }

    [Fact]
    public async Task KnowledgeBase_GraphIngest_ThenSearchReturnsSubgraph()
    {
        // 图谱 RAG 接入知识库：知识文档入库时抽实体/关系建入 kb:{id} 图谱，检索时种子召回 + 图遍历返回子图
        var store = new SqliteStore($"Data Source=file:kbgraph-{Guid.NewGuid():N}?mode=memory&cache=shared");
        store.EnsureSchema();
        var graphStore = new RelationalGraphMemoryStore(store, 8, NullLogger<RelationalGraphMemoryStore>.Instance);
        graphStore.EnsureSchema();
        var memoryStore = new SqliteVecMessageMemoryStore(store, 8, NullLogger<SqliteVecMessageMemoryStore>.Instance);
        memoryStore.EnsureSchema();

        var options = new AgentOptions
        {
            Provider = "mock",
            Memory = new MemoryOptions
            {
                GraphEnabled = true,
                GraphTopK = 3,
                GraphMinScore = 0.1,
                GraphHops = 2,
                GraphMaxNodes = 20,
                EmbeddingDimensions = 8,
            },
        };
        var tmp = Path.Combine(Path.GetTempPath(), $"agui-kbgraph-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            var services = new ServiceCollection();
            services.AddSingleton<IGraphMemoryStore>(graphStore);
            services.AddSingleton<IMessageMemoryStore>(memoryStore);
            services.AddSingleton<IEmbeddingProvider>(new StubEmbedding());
            services.AddSingleton(new GraphEntityExtractor(options, NullLogger<GraphEntityExtractor>.Instance));
            services.AddSingleton(new AttachmentStore(tmp));
            services.AddSingleton(options);
            var sp = services.BuildServiceProvider();

            var catalog = new KnowledgeBaseCatalog(options, sp, NullLoggerFactory.Instance, changes: null);
            var kb = catalog.CreateKb("Graph 知识库", "图测试", ownerId: null);
            var domain = "kb:" + kb.KbId;

            // 直接在该知识库域的图谱里种入实体与关系（隔离验证：检索/遍历应限定在 kb:{id} 域）
            graphStore.UpsertEntity(Entity("v3", "V3模型", "Technology", domain, OneHot(4)));
            graphStore.UpsertEntity(Entity("moe", "MoE架构", "Concept", domain, OneHot(4)));
            graphStore.UpsertEntity(Entity("ds", "DeepSeek", "Organization", domain, OneHot(0)));
            graphStore.UpsertEdge(new GraphEdgeRecord("v3", "应用", "moe", domain, "V3模型", "MoE架构"));
            graphStore.UpsertEdge(new GraphEdgeRecord("ds", "开发", "v3", domain, "DeepSeek", "V3模型"));

            // 图谱检索：query 命中种子实体，图遍历应带同域的相连实体与边
            var sub = await catalog.SearchGraphAsync([kb.KbId], "V3模型", topK: 3, minScore: 0.5, hops: 2, maxNodes: 20, CancellationToken.None);
            Assert.False(sub.IsEmpty);
            Assert.Contains(sub.Entities, e => e.Name == "V3模型");
            Assert.Contains(sub.Entities, e => e.Name == "MoE架构"); // 经图遍历相连
            Assert.Contains(sub.Edges, e => e.Relation == "应用" || e.Relation == "开发");

            // 其他知识库域不受污染（隔离）
            var sub2 = await catalog.SearchGraphAsync([kb.KbId + "_fake"], "V3模型", topK: 3, minScore: 0.5, hops: 2, maxNodes: 20, CancellationToken.None);
            Assert.True(sub2.IsEmpty);

            // 另：知识文档入库应触发图谱抽取（MaybeStoreKbGraphAsync 写入同域）：入库后实体/边应增长
            var before = graphStore.Stats().EntityCount;
            var (doc, err) = await catalog.AddTextDocumentAsync(kb.KbId, "graph.md", "DeepSeek 开发 V3模型，V3模型 应用 MoE架构", CancellationToken.None);
            Assert.Null(err);
            Assert.NotNull(doc);
            await catalog.WaitForDocumentAsync(doc!.DocId);
            await Task.Delay(200);
            Assert.True(graphStore.Stats().EntityCount >= before, "知识文档入库应抽取实体并入同域图谱");
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task KnowledgeBase_AddDocument_Attachment_Sqlite_GraphEnabled_EndsReady()
    {
        // 复现桌面版（SQLite + 图谱启用）经附件方式 AddDocumentAsync 上传知识文档：文档最终应 ready，而非 error
        var dbFile = Path.Combine(Path.GetTempPath(), $"agui-kbupload-{Guid.NewGuid():N}.sqlite");
        var store = new SqliteStore($"Data Source={dbFile}");
        store.EnsureSchema();
        var graphStore = new RelationalGraphMemoryStore(store, 8, NullLogger<RelationalGraphMemoryStore>.Instance);
        graphStore.EnsureSchema();
        var memoryStore = new SqliteVecMessageMemoryStore(store, 8, NullLogger<SqliteVecMessageMemoryStore>.Instance);
        memoryStore.EnsureSchema();
        var options = new AgentOptions
        {
            Provider = "mock",
            Memory = new MemoryOptions { GraphEnabled = true, EmbeddingDimensions = 8 },
        };
        var tmp = Path.Combine(Path.GetTempPath(), $"agui-kbupload-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            var attachments = new AttachmentStore(tmp);
            using var ms = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("报销制度：员工需在 7 个工作日内提交发票：DeepSeek 开发 V3模型，V3模型 应用 MoE架构"));
            var info = attachments.Save("制度.txt", "text/plain", ms, ms.Length);
            Assert.NotNull(info.AttachmentId);

            var services = new ServiceCollection();
            services.AddSingleton<IGraphMemoryStore>(graphStore);
            services.AddSingleton<IMessageMemoryStore>(memoryStore);
            services.AddSingleton<IEmbeddingProvider>(new StubEmbedding());
            services.AddSingleton(new GraphEntityExtractor(options, NullLogger<GraphEntityExtractor>.Instance));
            services.AddSingleton(attachments);
            services.AddSingleton(options);
            var sp = services.BuildServiceProvider();
            var catalog = new KnowledgeBaseCatalog(options, sp, NullLoggerFactory.Instance, changes: null);
            var kb = catalog.CreateKb("上传测试", "t", ownerId: null);

            var (doc, err) = await catalog.AddDocumentAsync(kb.KbId, info.AttachmentId, CancellationToken.None);
            Assert.Null(err);
            Assert.NotNull(doc);
            await catalog.WaitForDocumentAsync(doc!.DocId);
            await Task.Delay(200);
            // 关键断言：文档应处理完成（ready），而非 error（图/向量在 SQLite 上的写入异常会产生 error 状态）
            Assert.True(doc.Status == "ready", $"文档应为 ready，实际 Status={doc.Status} Error={doc.Error}");
            Assert.True(graphStore.Stats().EntityCount > 0, "SQLite 图应在文档入库后写入实体");
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
            try { File.Delete(dbFile); } catch { }
        }
    }
}
