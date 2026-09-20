using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AguiGroupChat.Agents;
using AguiGroupChat.Hub.Models;
using AguiGroupChat.Hub.Persistence;
using AguiGroupChat.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>
/// 知识库的<b>检索严格度</b>（每个库自己的相似度门槛）。
///
/// <para>
/// 为何要按库可调：文档是<b>短条目 / 目录型</b>时，通用提问的相似度普遍偏高，门槛要收紧；
/// 文档是<b>长篇论述</b>时，一句话提问与其中某段的相似度普遍偏低，门槛要放松才召得回。
/// 全局 <c>Agents:Memory:MinScore</c>（默认 0.25）两头都不合适。
/// </para>
///
/// <para>
/// 收口点只有一处：<see cref="KnowledgeBaseCatalog.SearchAsync"/> 对**每个库**解析生效门槛
/// （库设了用库的，否则用调用方传的）。知识库的检索结果只进模型上下文、没有面向用户的读取接口，
/// 所以这里不去比“回答里提没提到”，而是用假 store 记下它**收到的 minScore** ——
/// 那才是“到底按谁的门槛在筛”的唯一真相。
/// </para>
/// </summary>
public sealed class KbStrictnessTests
{
    private sealed class RecordingStore : IMessageMemoryStore
    {
        public double HitScore = 0.30;
        public readonly Dictionary<string, double> Gates = new(StringComparer.Ordinal);
        public void EnsureSchema() { }
        public void Upsert(MessageMemoryRecord record) { }
        public void Remove(string groupId, string messageId) { }
        public void RemoveGroup(string groupId) { }
        public void ClearAll() { }
        public IReadOnlyList<MessageMemoryItem> ListMessages(string? groupId, string? senderId, string? keyword, int limit, int offset) => [];
        public long CountMessages(string? groupId, string? senderId, string? keyword) => 0;
        public IReadOnlyList<MessageMemoryGroupStat> GroupStats(long nowMs) => [];
        public bool DeleteByMessageId(string messageId) => false;
        public bool UpdateImportance(string messageId, int importance) => false;
        public int SetExpiry(string? groupId, long? expiresAt, long nowMs) => 0;
        public int PruneExpired(long nowMs) => 0;

        public IReadOnlyList<MessageMemoryHit> Search(string groupId, string? agentId, float[] embedding, int topK, double minScore, string scope)
        {
            Gates[groupId] = minScore;
            return HitScore >= minScore
                ? [new MessageMemoryHit("doc_a:0", "切片正文", "文档.md", 0, HitScore, MemoryImportance.Normal, groupId)]
                : [];
        }

        public IReadOnlyList<MessageMemoryHit> SearchPerson(string personId, string currentGroupId, float[] embedding, int topK, double minScore) => [];
    }

    private sealed class FakeEmbedding : IEmbeddingProvider
    {
        public Task<float[]?> EmbedAsync(string text, CancellationToken ct = default)
            => Task.FromResult<float[]?>(string.IsNullOrWhiteSpace(text) ? null : [0.3f, 0.4f]);
        public void Dispose() { }
    }

    private static (KnowledgeBaseCatalog Catalog, RecordingStore Store, KnowledgeBase Kb) NewCatalog(double? strictness = null)
    {
        var options = new AgentOptions { Provider = "mock", Memory = new MemoryOptions { Enabled = true } };
        var store = new RecordingStore();
        var services = new ServiceCollection();
        services.AddSingleton<IMessageMemoryStore>(store);
        services.AddSingleton<IEmbeddingProvider>(new FakeEmbedding());
        var catalog = new KnowledgeBaseCatalog(options, services.BuildServiceProvider(), NullLoggerFactory.Instance);
        var kb = catalog.CreateKb("制度库", "", "user_owner");
        if (strictness is { } s) Assert.Null(catalog.SetStrictness(kb.KbId, s));
        return (catalog, store, kb);
    }

    [Fact]
    public async Task LibraryStrictness_OverridesRequestedGate()
    {
        var (catalog, store, kb) = NewCatalog(strictness: 0.40);
        store.HitScore = 0.30;     // 过得了全局 0.25，过不了库的 0.40

        var hits = await catalog.SearchAsync([kb.KbId], "报销流程", 5, 0.25);

        Assert.Equal(0.40, store.Gates[KnowledgeBaseCatalog.KbGroupPrefix + kb.KbId], 3);
        Assert.Empty(hits);
    }

    [Fact]
    public async Task WithoutLibraryStrictness_RequestedGateApplies()
    {
        var (catalog, store, kb) = NewCatalog(strictness: null);
        store.HitScore = 0.30;

        var hits = await catalog.SearchAsync([kb.KbId], "报销流程", 5, 0.25);

        Assert.Equal(0.25, store.Gates[KnowledgeBaseCatalog.KbGroupPrefix + kb.KbId], 3);
        Assert.Single(hits);
    }

    [Fact]
    public async Task LooseStrictness_RescuesASliceBelowTheGlobalGate()
    {
        var (catalog, store, kb) = NewCatalog(strictness: 0.15);
        store.HitScore = 0.20;     // 低于全局 0.25（会被丢掉），库放宽到 0.15 后应召得回

        var hits = await catalog.SearchAsync([kb.KbId], "长假怎么申请", 5, 0.25);

        Assert.Equal(0.15, store.Gates[KnowledgeBaseCatalog.KbGroupPrefix + kb.KbId], 3);
        Assert.Single(hits);
    }

    [Fact]
    public async Task Strictness_IsPerKb_NotGlobal()
    {
        var (catalog, store, kbA) = NewCatalog(strictness: 0.40);
        var kbB = catalog.CreateKb("产品库", "", "user_owner");
        store.HitScore = 0.30;

        var hits = await catalog.SearchAsync([kbA.KbId, kbB.KbId], "报销流程", 5, 0.25);

        Assert.Equal(0.40, store.Gates[KnowledgeBaseCatalog.KbGroupPrefix + kbA.KbId], 3);   // A 用库的
        Assert.Equal(0.25, store.Gates[KnowledgeBaseCatalog.KbGroupPrefix + kbB.KbId], 3);   // B 没设 → 用调用的
        Assert.Single(hits);
        Assert.Equal(kbB.KbId, hits[0].KbId);
    }

    [Fact]
    public void Strictness_IsClampedToSaneRange()
    {
        var (catalog, _, kb) = NewCatalog(strictness: null);

        Assert.Null(catalog.SetStrictness(kb.KbId, 0.02));                    // 低于下限：等于不筛，夹住
        Assert.Equal(KnowledgeBaseCatalog.MinStrictness, kb.MinScore!.Value, 3);

        Assert.Null(catalog.SetStrictness(kb.KbId, 0.99));                    // 高于上限：连原文都召不回
        Assert.Equal(KnowledgeBaseCatalog.MaxStrictness, kb.MinScore!.Value, 3);

        Assert.Null(catalog.SetStrictness(kb.KbId, null));                    // 恢复“沿用调用方传的值”
        Assert.Null(kb.MinScore);

        Assert.Equal("知识库不存在", catalog.SetStrictness("kb_不存在", 0.4));
    }
}

/// <summary>知识库设置接口（<c>PUT /ag-ui/kb/{kbId}</c>）：保存严格度 + 只有创建者/管理员能改。</summary>
public sealed class KbStrictnessApiFixture : IAsyncLifetime
{
    public WebApplication App { get; private set; } = null!;
    public string HttpBase { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var builder = HubApp.CreateBuilder([]);
        builder.Environment.EnvironmentName = "Testing";
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["GroupChat:SeedSampleData"] = "false",
            ["Agents:Provider"] = "mock",
            ["Persistence:Enabled"] = "false",
            ["Auth:RequireTokenOnRealTime"] = "false",
            ["Auth:AdminUserIds"] = "kb_admin",
        });
        HubApp.ConfigureServices(builder);
        builder.Services.AddAgentFramework(builder.Configuration);
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));
        App = builder.Build();
        HubApp.MapEndpoints(App);
        App.MapKnowledgeBaseApi();
        await App.StartAsync();
        HttpBase = App.Urls.First();
    }

    public async Task DisposeAsync()
    {
        if (App is not null) await App.DisposeAsync();
    }
}

public sealed class KbStrictnessApiTests : IClassFixture<KbStrictnessApiFixture>
{
    private readonly HttpClient _client;

    public KbStrictnessApiTests(KbStrictnessApiFixture fixture)
        => _client = new HttpClient { BaseAddress = new Uri(fixture.HttpBase) };

    private async Task<string> RegisterAsync(string username)
    {
        var res = await _client.PostAsJsonAsync("/ag-ui/user/register", new { username, password = "secret1", nickname = username });
        if (res.StatusCode == HttpStatusCode.Conflict)
        {
            var login = await _client.PostAsJsonAsync("/ag-ui/user/login", new { username, password = "secret1" });
            login.EnsureSuccessStatusCode();
            return (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
        }
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
    }

    private static HttpRequestMessage Authed(HttpMethod method, string path, string token)
    {
        var req = new HttpRequestMessage(method, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return req;
    }

    [Fact]
    public async Task Strictness_CanBeSavedAndIsReflectedInTheList()
    {
        var stamp = Guid.NewGuid().ToString("N")[..6];
        var owner = await RegisterAsync("kb_owner_" + stamp);
        var other = await RegisterAsync("kb_other_" + stamp);

        using var create = Authed(HttpMethod.Post, "/ag-ui/kb", owner);
        create.Content = JsonContent.Create(new { name = "严格度验证库-" + stamp, description = "x", minScore = 0.4 });
        var createRes = await _client.SendAsync(create);
        createRes.EnsureSuccessStatusCode();
        var kb = await createRes.Content.ReadFromJsonAsync<JsonElement>();
        var kbId = kb.GetProperty("kbId").GetString()!;
        Assert.Equal(0.4, kb.GetProperty("minScore").GetDouble(), 3);

        using (var put = Authed(HttpMethod.Put, $"/ag-ui/kb/{kbId}", owner))
        {
            put.Content = JsonContent.Create(new { minScore = 0.15 });
            var putRes = await _client.SendAsync(put);
            putRes.EnsureSuccessStatusCode();
            Assert.Equal(0.15, (await putRes.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("minScore").GetDouble(), 3);
        }

        using (var list = Authed(HttpMethod.Get, "/ag-ui/kb", owner))
        {
            var listRes = await _client.SendAsync(list);
            listRes.EnsureSuccessStatusCode();
            var kbs = await listRes.Content.ReadFromJsonAsync<JsonElement>();
            var mine = kbs.EnumerateArray().First(x => x.GetProperty("kbId").GetString() == kbId);
            Assert.Equal(0.15, mine.GetProperty("minScore").GetDouble(), 3);
        }

        // 别人改不动
        using (var put = Authed(HttpMethod.Put, $"/ag-ui/kb/{kbId}", other))
        {
            put.Content = JsonContent.Create(new { minScore = 0.7 });
            var putRes = await _client.SendAsync(put);
            Assert.Equal(HttpStatusCode.Forbidden, putRes.StatusCode);
        }

        // 不存在的库 → 404
        using (var put = Authed(HttpMethod.Put, "/ag-ui/kb/kb_不存在", owner))
        {
            put.Content = JsonContent.Create(new { minScore = 0.25 });
            var putRes = await _client.SendAsync(put);
            Assert.Equal(HttpStatusCode.NotFound, putRes.StatusCode);
        }
    }

    /// <summary>
    /// 「试检索」端点（库设置里调严格度用）：返回命中片段与分数，且**按请求里临时给的门槛**算
    /// —— 这样界面能“先试不同档位、再决定保存”。
    /// </summary>
    [Fact]
    public async Task ProbeSearch_ReturnsHits_AndHonoursTheTemporaryGate()
    {
        var stamp = Guid.NewGuid().ToString("N")[..6];
        var owner = await RegisterAsync("kb_probe_" + stamp);

        using var create = Authed(HttpMethod.Post, "/ag-ui/kb", owner);
        create.Content = JsonContent.Create(new { name = "试检索验证库-" + stamp, description = "x" });
        var createRes = await _client.SendAsync(create);
        createRes.EnsureSuccessStatusCode();
        var kbId = (await createRes.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("kbId").GetString()!;

        // 空库：能检索（不报错），但 0 条（空库不该被当成错误）
        using (var probe = Authed(HttpMethod.Post, $"/ag-ui/kb/{kbId}/search", owner))
        {
            probe.Content = JsonContent.Create(new { query = "报销流程", topK = 5, minScore = 0.25 });
            var res = await _client.SendAsync(probe);
            res.EnsureSuccessStatusCode();
            var d = await res.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(0, d.GetProperty("count").GetInt32());
            Assert.Equal(0.25, d.GetProperty("minScore").GetDouble(), 3);
        }

        // 门槛：请求里给的优先（界面试档位靠它）
        using (var probe = Authed(HttpMethod.Post, $"/ag-ui/kb/{kbId}/search", owner))
        {
            probe.Content = JsonContent.Create(new { query = "报销流程", minScore = 0.4 });
            var res = await _client.SendAsync(probe);
            res.EnsureSuccessStatusCode();
            Assert.Equal(0.4, (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("minScore").GetDouble(), 3);
        }

        // 没给就用本库自己的（隔了层解析，别只看请求回显）
        using (var put = Authed(HttpMethod.Put, $"/ag-ui/kb/{kbId}", owner))
        {
            put.Content = JsonContent.Create(new { minScore = 0.55 });
            (await _client.SendAsync(put)).EnsureSuccessStatusCode();
        }
        using (var probe = Authed(HttpMethod.Post, $"/ag-ui/kb/{kbId}/search", owner))
        {
            probe.Content = JsonContent.Create(new { query = "报销流程" });
            var res = await _client.SendAsync(probe);
            res.EnsureSuccessStatusCode();
            Assert.Equal(0.55, (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("minScore").GetDouble(), 3);
        }

        // 空查询 → 400；未登录 → 401
        using (var probe = Authed(HttpMethod.Post, $"/ag-ui/kb/{kbId}/search", owner))
        {
            probe.Content = JsonContent.Create(new { query = "  " });
            Assert.Equal(HttpStatusCode.BadRequest, (await _client.SendAsync(probe)).StatusCode);
        }
        using (var probe = new HttpRequestMessage(HttpMethod.Post, $"/ag-ui/kb/{kbId}/search"))
        {
            probe.Content = JsonContent.Create(new { query = "报销流程" });
            Assert.Equal(HttpStatusCode.Unauthorized, (await _client.SendAsync(probe)).StatusCode);
        }
    }
}
