using System.Text;
using AguiGroupChat.Agents;
using AguiGroupChat.Hub.Agents;
using AguiGroupChat.Hub.Models;
using AguiGroupChat.Hub.Persistence;
using AguiGroupChat.Hub.Storage;
using Microsoft.Agents.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>知识库（RAG 知识文档）测试：切片、文档入库（向量）、检索、删除、注入。</summary>
public sealed class KnowledgeBaseTests
{
    // ================= 切片 =================

    [Fact]
    public void Chunk_ShortText_SingleChunk()
        => Assert.Single(KnowledgeBaseCatalog.Chunk("短文"));

    [Fact]
    public void Chunk_LongText_SplitsWithOverlap()
    {
        var text = new string('中', 2000);
        var chunks = KnowledgeBaseCatalog.Chunk(text);
        Assert.True(chunks.Count >= 3);
        Assert.All(chunks, c => Assert.True(c.Length <= KnowledgeBaseCatalog.ChunkSize));
        // 相邻切片有重叠（后一片开头 10 字符应出现在前一片中）
        var tail = chunks[1][..10];
        Assert.True(chunks[0].Contains(tail, StringComparison.Ordinal));
    }

    [Fact]
    public void Chunk_Empty_ReturnsEmpty()
        => Assert.Empty(KnowledgeBaseCatalog.Chunk("   "));

    // ================= 目录 / 文档 / 检索 =================

    private sealed class FakeKbStore : IMessageMemoryStore
    {
        public List<MessageMemoryRecord> Records { get; } = [];
        public List<string> RemovedGroups { get; } = [];
        public void EnsureSchema() { }
        public void Upsert(MessageMemoryRecord record) => Records.Add(record);
        public void Remove(string groupId, string messageId) => Records.RemoveAll(r => r.GroupId == groupId && r.MessageId == messageId);
        public void RemoveGroup(string groupId)
        {
            RemovedGroups.Add(groupId);
            Records.RemoveAll(r => r.GroupId == groupId);
        }
        public void ClearAll() => Records.Clear();
        public IReadOnlyList<MessageMemoryItem> ListMessages(string? groupId, string? senderId, string? keyword, int limit, int offset) => [];
        public long CountMessages(string? groupId, string? senderId, string? keyword) => 0;
        public IReadOnlyList<MessageMemoryGroupStat> GroupStats(long nowMs) => [];
        public bool DeleteByMessageId(string messageId) => false;
        public bool UpdateImportance(string messageId, int importance) => false;
        public int SetExpiry(string? groupId, long? expiresAt, long nowMs) => 0;
        public int PruneExpired(long nowMs) => 0;
        public IReadOnlyList<MessageMemoryHit> Search(string groupId, string? agentId, float[] embedding, int topK, double minScore, string scope)
            => Records.Where(r => r.GroupId == groupId && r.SenderType == "kb")
                .Select(r => new MessageMemoryHit(r.MessageId, r.Content, r.SenderId, r.Timestamp, 0.9))
                .Take(topK).ToList();
        public IReadOnlyList<MessageMemoryHit> SearchPerson(string personId, string currentGroupId, float[] embedding, int topK, double minScore) => [];
    }

    private sealed class FakeKbEmbedding : IEmbeddingProvider
    {
        public Task<float[]?> EmbedAsync(string text, CancellationToken ct = default)
            => Task.FromResult<float[]?>(string.IsNullOrWhiteSpace(text) ? null : [0.1f, 0.2f, 0.3f]);
        public void Dispose() { }
    }

    /// <summary>慢速 embedding（每个切片延迟 200ms），用于验证处理中状态的时序测试。</summary>
    private sealed class SlowKbEmbedding : IEmbeddingProvider
    {
        public async Task<float[]?> EmbedAsync(string text, CancellationToken ct = default)
        {
            await Task.Delay(200, ct);
            return string.IsNullOrWhiteSpace(text) ? null : [0.1f, 0.2f, 0.3f];
        }
        public void Dispose() { }
    }

    private static (KnowledgeBaseCatalog Catalog, FakeKbStore Store, string KbId, string AttId) SetupKb(string docText = "公司制度：报销需在 7 个工作日内提交发票 SKY-2026")
    {
        var dir = Path.Combine(Path.GetTempPath(), "agui-kb-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var store = new AttachmentStore(dir);
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(docText));
        var info = store.Save("制度.txt", "text/plain", ms, ms.Length);

        var options = new AgentOptions { Provider = "mock" };
        var services = new ServiceCollection();
        services.AddSingleton(store);
        services.AddSingleton<IMessageMemoryStore>(new FakeKbStore());
        services.AddSingleton<IEmbeddingProvider>(new FakeKbEmbedding());
        var sp = services.BuildServiceProvider();
        var catalog = new KnowledgeBaseCatalog(options, sp, NullLoggerFactory.Instance);
        var kb = catalog.CreateKb("制度库", "公司制度", ownerId: "user_1");
        return (catalog, (FakeKbStore)sp.GetRequiredService<IMessageMemoryStore>(), kb.KbId, info.AttachmentId);
    }

    [Fact]
    public async Task AddDocument_ReturnsProcessingImmediately_ThenReady()
    {
        var (catalog, store, kbId, attId) = SetupKb();
        var (doc, error) = await catalog.AddDocumentAsync(kbId, attId);
        Assert.Null(error);
        Assert.NotNull(doc);
        // 立即返回“处理中”记录（向量化在后台执行）
        Assert.Equal("processing", doc!.Status);
        Assert.Equal(0, doc.ChunkCount);
        // 等待后台处理完成后变为 ready，向量已写入
        await catalog.WaitForDocumentAsync(doc.DocId);
        Assert.Equal("ready", doc.Status);
        Assert.True(doc.ChunkCount >= 1);
        var vectors = store.Records.Where(r => r.GroupId == KnowledgeBaseCatalog.KbGroupPrefix + kbId).ToList();
        Assert.Equal(doc.ChunkCount, vectors.Count);
        Assert.All(vectors, v => Assert.Equal("kb", v.SenderType));
    }

    [Fact]
    public async Task AddDocument_VectorizesAndStoresChunks()
    {
        var (catalog, store, kbId, attId) = SetupKb();
        var (doc, error) = await catalog.AddDocumentAsync(kbId, attId);
        Assert.Null(error);
        Assert.NotNull(doc);
        await catalog.WaitForDocumentAsync(doc!.DocId);
        Assert.Equal("制度.txt", doc.FileName);
        Assert.True(doc.ChunkCount >= 1);
        // 向量写入记忆存储（GroupId=kb:{KbId}）
        var vectors = store.Records.Where(r => r.GroupId == KnowledgeBaseCatalog.KbGroupPrefix + kbId).ToList();
        Assert.Equal(doc.ChunkCount, vectors.Count);
        Assert.All(vectors, v => Assert.Equal("kb", v.SenderType));
        // 文档元数据登记
        var kb = catalog.GetKb(kbId)!;
        Assert.Single(kb.Documents);
    }

    [Fact]
    public async Task AddDocument_InvalidAttachment_ReturnsSyncError()
    {
        var (catalog, store, kbId, _) = SetupKb();
        // 伪造一个不存在的附件 ID：快速失败仍同步返回 error
        var (doc, error) = await catalog.AddDocumentAsync(kbId, "att_does_not_exist");
        Assert.Null(doc);
        Assert.Contains("无法提取文本", error);
    }

    [Fact]
    public async Task Search_ReturnsKbHits()
    {
        var (catalog, _, kbId, attId) = SetupKb("报销流程：先提交发票，再审批 SKY-2026");
        var (doc, _) = await catalog.AddDocumentAsync(kbId, attId);
        await catalog.WaitForDocumentAsync(doc!.DocId);
        var hits = await catalog.SearchAsync([kbId], "报销要什么流程", topK: 3, minScore: 0.1);
        var hit = Assert.Single(hits);
        Assert.Equal("制度库", hit.KbName);
        Assert.Contains("报销", hit.Content);
    }

    [Fact]
    public async Task RemoveDocument_DeletesVectors()
    {
        var (catalog, store, kbId, attId) = SetupKb();
        var (doc, _) = await catalog.AddDocumentAsync(kbId, attId);
        await catalog.WaitForDocumentAsync(doc!.DocId);
        Assert.True(catalog.RemoveDocument(kbId, doc.DocId));
        Assert.Empty(store.Records.Where(r => r.GroupId == KnowledgeBaseCatalog.KbGroupPrefix + kbId));
        Assert.Empty(catalog.GetKb(kbId)!.Documents);
    }

    [Fact]
    public async Task RemoveDocument_WhileProcessing_DiscardsVectors()
    {
        var dir = Path.Combine(Path.GetTempPath(), "agui-kb-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(dir);
            var store = new AttachmentStore(dir);
            var docText = new string('长', 4000); // 多切片，配合慢 embedding 保证处理中时序
            using var ms = new MemoryStream(Encoding.UTF8.GetBytes(docText));
            var info = store.Save("长文.txt", "text/plain", ms, ms.Length);

            var options = new AgentOptions { Provider = "mock" };
            var services = new ServiceCollection();
            services.AddSingleton(store);
            services.AddSingleton<IMessageMemoryStore>(new FakeKbStore());
            services.AddSingleton<IEmbeddingProvider>(new SlowKbEmbedding());
            var sp = services.BuildServiceProvider();
            var catalog = new KnowledgeBaseCatalog(options, sp, NullLoggerFactory.Instance);
            var kb = catalog.CreateKb("制度库", "", ownerId: "user_1");
            var fakeStore = (FakeKbStore)sp.GetRequiredService<IMessageMemoryStore>();

            var (doc, error) = await catalog.AddDocumentAsync(kb.KbId, info.AttachmentId);
            Assert.Null(error);
            Assert.Equal("processing", doc!.Status); // 慢 embedding 下必然仍在处理
            Assert.True(catalog.RemoveDocument(kb.KbId, doc.DocId)); // 处理中移除
            await catalog.WaitForDocumentAsync(doc.DocId);
            // 后台任务检测到文档已被移除，不得写入孤儿向量
            Assert.Empty(fakeStore.Records.Where(r => r.GroupId == KnowledgeBaseCatalog.KbGroupPrefix + kb.KbId));
            Assert.Empty(catalog.GetKb(kb.KbId)!.Documents);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async Task RemoveKb_DeletesVectorsAndEntry()
    {
        var (catalog, store, kbId, attId) = SetupKb();
        var (doc, _) = await catalog.AddDocumentAsync(kbId, attId);
        await catalog.WaitForDocumentAsync(doc!.DocId);
        Assert.True(catalog.RemoveKb(kbId));
        Assert.Contains(KnowledgeBaseCatalog.KbGroupPrefix + kbId, store.RemovedGroups);
        Assert.Null(catalog.GetKb(kbId));
    }

    [Fact]
    public async Task AddDocument_WithoutStore_ReturnsError()
    {
        var options = new AgentOptions();
        var sp = new ServiceCollection().BuildServiceProvider();
        var catalog = new KnowledgeBaseCatalog(options, sp, NullLoggerFactory.Instance);
        var kb = catalog.CreateKb("x", "", "user_1");
        var (doc, error) = await catalog.AddDocumentAsync(kb.KbId, "att_xxx");
        Assert.Null(doc);
        Assert.Contains("语义记忆", error);
    }

    [Fact]
    public void ListKbs_Visibility_SystemAndOwned()
    {
        var options = new AgentOptions();
        var sp = new ServiceCollection().BuildServiceProvider();
        var catalog = new KnowledgeBaseCatalog(options, sp, NullLoggerFactory.Instance);
        var sys = catalog.CreateKb("系统库", "", ownerId: null);
        var mine = catalog.CreateKb("我的库", "", ownerId: "user_1");
        var other = catalog.CreateKb("别人的库", "", ownerId: "user_2");
        var visible = catalog.ListKbs("user_1");
        Assert.Contains(visible, k => k.KbId == sys.KbId);
        Assert.Contains(visible, k => k.KbId == mine.KbId);
        Assert.DoesNotContain(visible, k => k.KbId == other.KbId);
    }

    // ================= 群级共享（2.4） =================

    [Fact]
    public void ListKbs_GroupShared_VisibleToMember_NotToOutsider()
    {
        var options = new AgentOptions();
        var sp = new ServiceCollection().BuildServiceProvider();
        var catalog = new KnowledgeBaseCatalog(options, sp, NullLoggerFactory.Instance);
        var kb = catalog.CreateKb("团队库", "", ownerId: "user_1");
        kb.SharedGroupIds = ["group_a"];

        var memberGroups = new HashSet<string>(StringComparer.Ordinal) { "group_a" };
        var outsiderGroups = new HashSet<string>(StringComparer.Ordinal) { "group_b" };

        // 成员可见（只读）；无关群成员不可见
        Assert.Contains(catalog.ListKbs("user_x", memberGroups, isAdmin: false), k => k.KbId == kb.KbId);
        Assert.DoesNotContain(catalog.ListKbs("user_x", outsiderGroups, isAdmin: false), k => k.KbId == kb.KbId);
        // 管理员全可见
        Assert.Contains(catalog.ListKbs("user_x", outsiderGroups, isAdmin: true), k => k.KbId == kb.KbId);
    }

    [Fact]
    public void CanRead_GroupShared_AllowsMember_CanWrite_OnlyCreatorAdmin()
    {
        var options = new AgentOptions();
        var sp = new ServiceCollection().BuildServiceProvider();
        var catalog = new KnowledgeBaseCatalog(options, sp, NullLoggerFactory.Instance);
        var kb = catalog.CreateKb("团队库", "", ownerId: "user_1");
        kb.SharedGroupIds = ["group_a"];
        var memberGroups = new HashSet<string>(StringComparer.Ordinal) { "group_a" };

        // 共享群成员可读但不可写
        Assert.True(catalog.CanRead(kb, "user_x", memberGroups, isAdmin: false));
        Assert.False(catalog.CanWrite(kb, "user_x", isAdmin: false));
        // 创建者可读写；管理员可写
        Assert.True(catalog.CanWrite(kb, "user_1", isAdmin: false));
        Assert.True(catalog.CanWrite(kb, "user_x", isAdmin: true));
    }

    // ================= 注入（MemoryContextProvider） =================

    [Fact]
    public async Task Provider_InjectsKbSection_WhenAgentBound()
    {
        var dir = Path.Combine(Path.GetTempPath(), "agui-kb-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(dir);
            var attachmentStore = new AttachmentStore(dir);
            using var ms = new MemoryStream(Encoding.UTF8.GetBytes("项目代号 SKY-2026：核心是 WebSocket 实时推送"));
            var info = attachmentStore.Save("项目.txt", "text/plain", ms, ms.Length);

            var options = new AgentOptions
            {
                Provider = "mock",
                Memory = new MemoryOptions { Enabled = true, TopK = 3, MinScore = 0.1 },
                Agents =
                [
                    new AgentDefinition
                    {
                        AgentId = "agent_qa", Nickname = "问答", Description = "", Instructions = "你是问答助手",
                        KnowledgeBaseIds = [], // 稍后通过 catalog 绑定
                    },
                ],
            };
            var services = new ServiceCollection();
            services.AddSingleton(attachmentStore);
            services.AddSingleton<IMessageMemoryStore>(new FakeKbStore());
            services.AddSingleton<IEmbeddingProvider>(new FakeKbEmbedding());
            services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
            services.AddSingleton(options);
            services.AddSingleton<AgentCatalog>();
            services.AddSingleton<KnowledgeBaseCatalog>();
            var sp = services.BuildServiceProvider();

            var catalog = sp.GetRequiredService<KnowledgeBaseCatalog>();
            var kb = catalog.CreateKb("知识库A", "", ownerId: null);
            var (doc, error) = await catalog.AddDocumentAsync(kb.KbId, info.AttachmentId);
            Assert.Null(error);
            await catalog.WaitForDocumentAsync(doc!.DocId);
            options.Agents[0].KnowledgeBaseIds.Add(kb.KbId);

            var provider = new MemoryContextProvider(options, sp, NullLogger<MemoryContextProvider>.Instance);
            var run = new AgentInvocationContext("g1", "t1", "agent_qa", "问答", "msg1", "user_1", "项目代号是什么？", [], false);
            var prev = AgentGateway.AmbientContext.Value;
            AgentGateway.AmbientContext.Value = run;
            try
            {
#pragma warning disable MAAI001
                var aiContext = await provider.InvokingAsync(new AIContextProvider.InvokingContext(
                    new ChatClientAgent(new MockChatClient(options.Agents[0]), options.Agents[0].Instructions,
                        options.Agents[0].Nickname, null, null, NullLoggerFactory.Instance, sp),
                    null, new AIContext()), CancellationToken.None);
#pragma warning restore MAAI001
                Assert.Contains("知识库检索结果", aiContext.Instructions);
                Assert.Contains("SKY-2026", aiContext.Instructions);
            }
            finally { AgentGateway.AmbientContext.Value = prev; }
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Provider_SkipsKb_WhenNotBound()
    {
        var options = new AgentOptions
        {
            Provider = "mock",
            Agents = [new AgentDefinition { AgentId = "agent_x", Nickname = "X", Description = "", Instructions = "" }],
        };
        var sp = new ServiceCollection()
            .AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance)
            .AddSingleton(options)
            .AddSingleton<AgentCatalog>()
            .AddSingleton<KnowledgeBaseCatalog>()
            .BuildServiceProvider();
        var provider = new MemoryContextProvider(options, sp, NullLogger<MemoryContextProvider>.Instance);
        var run = new AgentInvocationContext("g1", "t1", "agent_x", "X", "msg1", "user_1", "hi", [], false);
        var prev = AgentGateway.AmbientContext.Value;
        AgentGateway.AmbientContext.Value = run;
        try
        {
#pragma warning disable MAAI001
            var aiContext = await provider.InvokingAsync(new AIContextProvider.InvokingContext(
                new ChatClientAgent(new MockChatClient(options.Agents[0]), options.Agents[0].Instructions,
                    options.Agents[0].Nickname, null, null, NullLoggerFactory.Instance, sp),
                null, new AIContext()), CancellationToken.None);
#pragma warning restore MAAI001
            Assert.DoesNotContain("知识库", aiContext.Instructions ?? "");
        }
        finally { AgentGateway.AmbientContext.Value = prev; }
    }
}
