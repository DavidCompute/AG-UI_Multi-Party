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
/// 图库的<b>检索严格度</b>（每个库自己的相似度门槛）。
///
/// <para>
/// 为何要按库可调：图库描述风格差别很大 —— 描述是<b>短人名 / 标签</b>时，通用关键词的得分普遍偏高，
/// 门槛要收紧（否则容易把不相干的图配上）；描述是<b>长句</b>时，标题式查询得分普遍偏低（实测 0.62），
/// 门槛要放松才配得上。一个全局常量必然两头都不合适。
/// </para>
///
/// <para>
/// 收口点只有一处：<see cref="ImageLibraryCatalog.SearchAsync"/> 对**每个库**解析生效门槛
/// （库设了用库的，否则用调用方传的）。所以这里不测“界面长什么样”，只测到底把哪个值交给了向量检索 ——
/// 用假 store 把它收到的 minScore 记下来，比“看结果猜”直接得多。
/// </para>
/// </summary>
public sealed class ImageLibraryStrictnessTests
{
    /// <summary>假向量存储：按给定分数决定是否召回，并记下每次收到的门槛（按库分开记）。</summary>
    private sealed class RecordingStore : IMessageMemoryStore
    {
        public string AssetId = "";
        public double AssetScore = 0.65;
        public readonly Dictionary<string, double> Gates = new(StringComparer.Ordinal);
        public readonly Dictionary<string, int> Calls = new(StringComparer.Ordinal);

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
            Calls[groupId] = Calls.TryGetValue(groupId, out var n) ? n + 1 : 1;
            // 召回条件是「门槛」，让“门槛到底是多少”直接决定结果
            return AssetScore >= minScore
                ? [new MessageMemoryHit(AssetId, "caption", "img", 0, AssetScore, MemoryImportance.Normal, groupId)]
                : [];
        }

        public IReadOnlyList<MessageMemoryHit> SearchPerson(string personId, string currentGroupId, float[] embedding, int topK, double minScore) => [];
    }

    private sealed class FakeEmbedding : IEmbeddingProvider
    {
        public Task<float[]?> EmbedAsync(string text, CancellationToken ct = default)
            => Task.FromResult<float[]?>(string.IsNullOrWhiteSpace(text) ? null : [0.1f, 0.2f]);
        public void Dispose() { }
    }

    private static readonly byte[] OnePng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAQAAAAECAYAAACp8Z5+AAAANElEQVR42hXIIQEAMAhFQZKQhBDoaUIsyc+EphB7E2fO/PYeCAPzIiBM/UgCwuSPICBM9D59ZymRzPwZugAAAABJRU5ErkJggg==");

    /// <summary>建一个图库 + 一张“已就绪”的图（文件真落盘，否则检索结果会被 BuildHit 丢掉）。</summary>
    private static (ImageLibraryCatalog Catalog, RecordingStore Store, ImageLibrary Lib) NewCatalog(double? strictness = null)
    {
        var options = new AgentOptions { Provider = "mock", Memory = new MemoryOptions { Enabled = true } };
        var root = Path.Combine(Path.GetTempPath(), "agui-imgstrict-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        var store = new RecordingStore();
        var services = new ServiceCollection();
        services.AddSingleton<IMessageMemoryStore>(store);
        services.AddSingleton<IEmbeddingProvider>(new FakeEmbedding());
        var catalog = new ImageLibraryCatalog(options, services.BuildServiceProvider(), NullLoggerFactory.Instance, root);
        var lib = catalog.CreateLibrary("品牌图库", "", "user_owner");
        var (asset, error) = catalog.AddAsset(lib.LibId, "logo.png", "image/png", OnePng, "user_owner");
        Assert.NotNull(asset);
        Assert.Null(error);
        asset!.Status = "ready";           // 跳过后台视觉/向量化（本用例只关心门槛）
        store.AssetId = asset.AssetId;
        if (strictness is { } s) Assert.Null(catalog.SetStrictness(lib.LibId, s));
        return (catalog, store, lib);
    }

    private static async Task<IReadOnlyList<ImageLibraryCatalog.ImageHit>> Search(ImageLibraryCatalog catalog, ImageLibrary lib, double requested)
        => await catalog.SearchAsync([lib.LibId], "团队合影", 3, requested);

    [Fact]
    public async Task LibraryStrictness_OverridesRequestedGate()
    {
        var (catalog, store, lib) = NewCatalog(strictness: 0.72);
        store.AssetScore = 0.65;           // 过得了调用方的 0.6，过不了库的 0.72

        var hits = await Search(catalog, lib, requested: 0.60);

        Assert.Equal(0.72, store.Gates[ImageLibraryCatalog.ImgGroupPrefix + lib.LibId], 3);
        Assert.Empty(hits);
    }

    [Fact]
    public async Task WithoutLibraryStrictness_RequestedGateApplies()
    {
        var (catalog, store, lib) = NewCatalog(strictness: null);
        store.AssetScore = 0.65;

        var hits = await Search(catalog, lib, requested: 0.60);

        Assert.Equal(0.60, store.Gates[ImageLibraryCatalog.ImgGroupPrefix + lib.LibId], 3);
        Assert.Single(hits);
    }

    [Fact]
    public async Task LooseStrictness_LetsLowerScoresThrough()
    {
        var (catalog, store, lib) = NewCatalog(strictness: 0.50);
        store.AssetScore = 0.55;           // 被调用方的 0.6 挡住，但库放宽到 0.5 后就该配上

        var hits = await Search(catalog, lib, requested: 0.60);

        Assert.Equal(0.50, store.Gates[ImageLibraryCatalog.ImgGroupPrefix + lib.LibId], 3);
        Assert.Single(hits);
        Assert.Equal(0.55, hits[0].Score, 3);
    }

    [Fact]
    public async Task Strictness_IsPerLibrary_NotGlobal()
    {
        var (catalog, store, libA) = NewCatalog(strictness: 0.72);
        var libB = catalog.CreateLibrary("生活照", "", "user_owner");
        var (assetB, _) = catalog.AddAsset(libB.LibId, "life.png", "image/png", OnePng, "user_owner");
        assetB!.Status = "ready";
        store.AssetId = assetB.AssetId;    // 只让 B 有可召回的图（A 的假 store 只认一个 assetId）
        store.AssetScore = 0.65;

        var hits = await catalog.SearchAsync([libA.LibId, libB.LibId], "团队合影", 3, 0.60);

        Assert.Equal(0.72, store.Gates[ImageLibraryCatalog.ImgGroupPrefix + libA.LibId], 3);   // A 用库的
        Assert.Equal(0.60, store.Gates[ImageLibraryCatalog.ImgGroupPrefix + libB.LibId], 3);   // B 没设 → 用请求的
        Assert.Single(hits);
        Assert.Equal(libB.LibId, hits[0].LibId);
    }

    [Fact]
    public void Strictness_IsClampedToSaneRange()
    {
        var (catalog, _, lib) = NewCatalog(strictness: null);

        Assert.Null(catalog.SetStrictness(lib.LibId, 0.05));           // 低于 0.3：等于不筛，夹住
        Assert.Equal(ImageLibraryCatalog.MinStrictness, lib.MinScore!.Value, 3);

        Assert.Null(catalog.SetStrictness(lib.LibId, 5));              // 高于 0.95：连本人照片都配不上
        Assert.Equal(ImageLibraryCatalog.MaxStrictness, lib.MinScore!.Value, 3);

        Assert.Null(catalog.SetStrictness(lib.LibId, null));           // 恢复“沿用调用方传的值”
        Assert.Null(lib.MinScore);

        Assert.Equal("图库不存在", catalog.SetStrictness("img_不存在", 0.6));
    }
}

/// <summary>图库设置接口（<c>PUT /ag-ui/image-libs/{libId}</c>）：保存严格度 + 只有创建者/管理员能改。</summary>
public sealed class ImageLibraryStrictnessApiFixture : IAsyncLifetime
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
            ["Auth:AdminUserIds"] = "strict_admin",
        });
        HubApp.ConfigureServices(builder);
        builder.Services.AddAgentFramework(builder.Configuration);
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));
        App = builder.Build();
        HubApp.MapEndpoints(App);
        App.MapImageLibraryApi();
        await App.StartAsync();
        HttpBase = App.Urls.First();
    }

    public async Task DisposeAsync()
    {
        if (App is not null) await App.DisposeAsync();
    }
}

public sealed class ImageLibraryStrictnessApiTests : IClassFixture<ImageLibraryStrictnessApiFixture>
{
    private readonly HttpClient _client;

    public ImageLibraryStrictnessApiTests(ImageLibraryStrictnessApiFixture fixture)
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
        var owner = await RegisterAsync("strict_owner_" + stamp);
        var other = await RegisterAsync("strict_other_" + stamp);

        // 创建（可带上初始严格度）
        using var create = Authed(HttpMethod.Post, "/ag-ui/image-libs", owner);
        create.Content = JsonContent.Create(new { name = "严格度验证库-" + stamp, description = "x", minScore = 0.72 });
        var createRes = await _client.SendAsync(create);
        createRes.EnsureSuccessStatusCode();
        var lib = await createRes.Content.ReadFromJsonAsync<JsonElement>();
        var libId = lib.GetProperty("libId").GetString()!;
        Assert.Equal(0.72, lib.GetProperty("minScore").GetDouble(), 3);

        // 改成宽松
        using (var put = Authed(HttpMethod.Put, $"/ag-ui/image-libs/{libId}", owner))
        {
            put.Content = JsonContent.Create(new { minScore = 0.5 });
            var putRes = await _client.SendAsync(put);
            putRes.EnsureSuccessStatusCode();
            Assert.Equal(0.5, (await putRes.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("minScore").GetDouble(), 3);
        }

        // 列表里能读到（界面回显靠它）
        using (var list = Authed(HttpMethod.Get, "/ag-ui/image-libs", owner))
        {
            var listRes = await _client.SendAsync(list);
            listRes.EnsureSuccessStatusCode();
            var libs = (await listRes.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("libraries");
            var mine = libs.EnumerateArray().First(x => x.GetProperty("libId").GetString() == libId);
            Assert.Equal(0.5, mine.GetProperty("minScore").GetDouble(), 3);
        }

        // 别人改不动
        using (var put = Authed(HttpMethod.Put, $"/ag-ui/image-libs/{libId}", other))
        {
            put.Content = JsonContent.Create(new { minScore = 0.9 });
            var putRes = await _client.SendAsync(put);
            Assert.Equal(HttpStatusCode.Forbidden, putRes.StatusCode);
        }

        // 不存在的库 → 404（别把“改了个不存在的库”当成成功）
        using (var put = Authed(HttpMethod.Put, "/ag-ui/image-libs/img_不存在", owner))
        {
            put.Content = JsonContent.Create(new { minScore = 0.6 });
            var putRes = await _client.SendAsync(put);
            Assert.Equal(HttpStatusCode.NotFound, putRes.StatusCode);
        }
    }
}
