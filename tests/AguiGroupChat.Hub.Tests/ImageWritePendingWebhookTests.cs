using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AguiGroupChat.Agents;
using AguiGroupChat.Hub;
using AguiGroupChat.Hub.Messaging;
using AguiGroupChat.Hub.Models;
using AguiGroupChat.Hub.Persistence;
using AguiGroupChat.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>
/// 待审批图库写入的「主动感知」端到端（1.0.281）：技能写已有图库 → 202 入队 →
/// 出站 Webhook 收到 <c>IMAGE_WRITE_PENDING</c>（未绑定知聚的订阅也能收到）。
///
/// <para>
/// 走的是真实 HTTP 路径：图库创建 → 平台签发写入能力句柄 → 技能自令牌上传 → 入队 →
/// Webhook 调度器后台投递到本地接收端。这样才能覆盖「事件目录登记 / 触发器里的 groupId = "" /
/// 信封里的 data 字段」这几处容易只在联调时才暴露的问题。
/// </para>
/// </summary>
public sealed class ImageWritePendingWebhookFixture : IAsyncLifetime
{
    public sealed record ReceiverHit(string EventType, string Body);

    public WebApplication App { get; private set; } = null!;
    public string HttpBase { get; private set; } = null!;
    public string ReceiverBase { get; private set; } = null!;
    public ConcurrentQueue<ReceiverHit> Hits { get; } = new();
    public string PendingRoot { get; } = Path.Combine(Path.GetTempPath(), "agui-iwp-" + Guid.NewGuid().ToString("N")[..8]);

    /// <summary>图库落盘需要存储 + 向量提供者；测试宿主用内存存储，这里补一个最小假件。</summary>
    private sealed class FakeMemoryStore : IMessageMemoryStore
    {
        public List<MessageMemoryRecord> Records { get; } = [];
        public void EnsureSchema() { }
        public void Upsert(MessageMemoryRecord record)
        {
            Records.RemoveAll(r => r.MessageId == record.MessageId);
            Records.Add(record);
        }
        public void Remove(string groupId, string messageId) => Records.RemoveAll(r => r.GroupId == groupId && r.MessageId == messageId);
        public void RemoveGroup(string groupId) => Records.RemoveAll(r => r.GroupId == groupId);
        public void ClearAll() => Records.Clear();
        public IReadOnlyList<MessageMemoryHit> Search(string groupId, string? agentId, float[] embedding, int topK, double minScore, string scope) => [];
        public IReadOnlyList<MessageMemoryHit> SearchPerson(string personId, string currentGroupId, float[] embedding, int topK, double minScore) => [];
        public IReadOnlyList<MessageMemoryItem> ListMessages(string? groupId, string? senderId, string? keyword, int limit, int offset) => [];
        public long CountMessages(string? groupId, string? senderId, string? keyword) => 0;
        public IReadOnlyList<MessageMemoryGroupStat> GroupStats(long nowMs) => [];
        public bool DeleteByMessageId(string messageId) => false;
        public bool UpdateImportance(string messageId, int importance) => false;
        public int SetExpiry(string? groupId, long? expiresAt, long nowMs) => 0;
        public int PruneExpired(long nowMs) => 0;
    }

    private sealed class FakeEmbedding : IEmbeddingProvider
    {
        public Task<float[]?> EmbedAsync(string text, CancellationToken ct = default)
            => Task.FromResult<float[]?>(string.IsNullOrWhiteSpace(text) ? null : [0.1f, 0.2f, 0.3f]);
        public void Dispose() { }
    }

    private WebApplication _receiver = null!;

    public async Task InitializeAsync()
    {
        // ---- 接收端：模拟外部告警系统 ----
        var rb = WebApplication.CreateBuilder();
        rb.WebHost.UseUrls("http://127.0.0.1:0");
        rb.Logging.ClearProviders();
        _receiver = rb.Build();
        _receiver.MapPost("/hook", async ctx =>
        {
            using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8);
            var body = await reader.ReadToEndAsync();
            Hits.Enqueue(new ReceiverHit(ctx.Request.Headers["X-KnowGath-Event"].ToString(), body));
            ctx.Response.StatusCode = 200;
        });
        await _receiver.StartAsync();
        ReceiverBase = _receiver.Urls.First();

        // ---- 主应用：Hub + 图库 API + 出站 Webhook ----
        var builder = HubApp.CreateBuilder([]);
        builder.Environment.EnvironmentName = "Testing";
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["GroupChat:SeedSampleData"] = "false",
            ["Agents:Provider"] = "mock",
            ["Persistence:Enabled"] = "false",
            ["Auth:AdminUserIds"] = "iwp_admin",
            ["Auth:FirstUserIsAdmin"] = "false",
        });
        HubApp.ConfigureServices(builder);
        builder.Services.AddAgentFramework(builder.Configuration);
        builder.Services.AddSingleton(new WebhookState());
        builder.Services.AddSingleton<WebhookDispatcher>();
        builder.Services.AddSingleton<IGroupEventObserver>(sp => sp.GetRequiredService<WebhookDispatcher>());
        builder.Services.AddSingleton(new ImageWriteApprovalQueue(PendingRoot));
        // 图库落盘要求「语义记忆可用」（存储 + 向量）；测试宿主用内存存储，这里补假件
        builder.Services.AddSingleton<IMessageMemoryStore>(new FakeMemoryStore());
        builder.Services.AddSingleton<IEmbeddingProvider>(new FakeEmbedding());
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));

        App = builder.Build();
        HubApp.MapEndpoints(App);
        App.MapWebhookApi();
        App.MapImageLibraryApi();
        await App.StartAsync();
        HttpBase = App.Urls.First();
        App.Services.GetRequiredService<WebhookDispatcher>().Start();
    }

    public async Task DisposeAsync()
    {
        await App.DisposeAsync();
        await _receiver.DisposeAsync();
        try { if (Directory.Exists(PendingRoot)) Directory.Delete(PendingRoot, recursive: true); } catch { /* 清理失败不影响断言 */ }
    }
}

public sealed class ImageWritePendingWebhookTests : IClassFixture<ImageWritePendingWebhookFixture>
{
    private readonly ImageWritePendingWebhookFixture _fx;
    private readonly HttpClient _client;

    public ImageWritePendingWebhookTests(ImageWritePendingWebhookFixture fx)
    {
        _fx = fx;
        _client = new HttpClient { BaseAddress = new Uri(fx.HttpBase) };
    }

    private async Task<(string Token, string UserId)> AdminAsync()
    {
        var res = await _client.PostAsJsonAsync("/ag-ui/user/register",
            new { username = "iwp_admin", password = "secret1", nickname = "iwp" });
        if (!res.IsSuccessStatusCode)
            res = await _client.PostAsJsonAsync("/ag-ui/user/login",
                new { username = "iwp_admin", password = "secret1" });
        res.EnsureSuccessStatusCode();
        var doc = await res.Content.ReadFromJsonAsync<JsonElement>();
        return (doc.GetProperty("token").GetString()!, doc.GetProperty("userId").GetString()!);
    }

    private static HttpRequestMessage Authed(HttpMethod method, string path, string token, HttpContent? body = null)
    {
        var req = new HttpRequestMessage(method, path) { Content = body };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return req;
    }

    private async Task WaitForHitAsync(Func<ImageWritePendingWebhookFixture.ReceiverHit, bool> predicate, int timeoutMs = 15000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (_fx.Hits.Any(predicate)) return;
            await Task.Delay(100);
        }
    }

    [Fact]
    public async Task PendingWrite_PushesImageWritePendingWebhook()
    {
        var (token, userId) = await AdminAsync();

        // 1) 建一个「已有图库」（技能只能通过授权写它）
        using var createLib = Authed(HttpMethod.Post, "/ag-ui/image-libs/", token,
            JsonContent.Create(new { name = "iwp-lib", description = "e2e" }));
        var libRes = await _client.SendAsync(createLib);
        libRes.EnsureSuccessStatusCode();
        var libDoc = await libRes.Content.ReadFromJsonAsync<JsonElement>();
        var libId = libDoc.GetProperty("libId").GetString()!;

        // 2) 平台侧签发写入能力句柄：要求审批，并授权写这个已有库
        var catalog = _fx.App.Services.GetRequiredService<ImageLibraryCatalog>();
        var scopeId = catalog.RegisterWriteScope(userId, agentId: "agent_iwp", skillId: "skill_iwp",
            seedLibraryIds: [libId], requiresApproval: true);

        // 3) 订阅（不绑定知聚 → 也应收到本次事件）
        using var subReq = Authed(HttpMethod.Post, "/ag-ui/admin/webhooks", token,
            JsonContent.Create(new { name = "iwp-hook", url = _fx.ReceiverBase + "/hook", eventTypes = new[] { EventTypes.ImageWritePending } }));
        (await _client.SendAsync(subReq)).EnsureSuccessStatusCode();

        // 4) 技能自令牌写图（1x1 PNG 头即可：内容不参与本次断言）
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        using var addReq = new HttpRequestMessage(HttpMethod.Post, $"/ag-ui/images/libraries/{libId}/assets")
        {
            Content = JsonContent.Create(new
            {
                writeScopeId = scopeId,
                fileName = "pending.png",
                base64 = Convert.ToBase64String(png),
                caption = "待审批写入",
            }),
        };
        addReq.Headers.Add(SelfApi.TokenEnvVar, SelfApi.Token);
        var addRes = await _client.SendAsync(addReq);
        Assert.Equal(System.Net.HttpStatusCode.Accepted, addRes.StatusCode);
        var pending = await addRes.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("pending", pending.GetProperty("status").GetString());
        var pendingId = pending.GetProperty("pendingId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(pendingId));

        // 5) Webhook 应带着完整信封到达（data 里能看到是谁、写哪、多大）
        await WaitForHitAsync(h => h.EventType == EventTypes.ImageWritePending);
        var hit = _fx.Hits.FirstOrDefault(h => h.EventType == EventTypes.ImageWritePending);
        Assert.NotNull(hit);
        var envelope = JsonDocument.Parse(hit!.Body).RootElement;
        Assert.Equal(EventTypes.ImageWritePending, envelope.GetProperty("event").GetString());
        Assert.Equal("", envelope.GetProperty("groupId").GetString());
        var data = envelope.GetProperty("data");
        Assert.Equal(pendingId, data.GetProperty("pendingId").GetString());
        Assert.Equal(userId, data.GetProperty("ownerUserId").GetString());
        Assert.Equal("skill_iwp", data.GetProperty("skillId").GetString());
        Assert.Equal(libId, data.GetProperty("libId").GetString());
        Assert.Equal("pending.png", data.GetProperty("fileName").GetString());
        Assert.Equal(png.LongLength, data.GetProperty("bytes").GetInt64());
    }

    [Fact]
    public async Task ApprovingPendingWrite_PushesResolvedWebhook()
    {
        var (token, userId) = await AdminAsync();

        using var createLib = Authed(HttpMethod.Post, "/ag-ui/image-libs/", token,
            JsonContent.Create(new { name = "iwp-resolved-lib", description = "e2e" }));
        var libRes = await _client.SendAsync(createLib);
        libRes.EnsureSuccessStatusCode();
        var libId = (await libRes.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("libId").GetString()!;

        var catalog = _fx.App.Services.GetRequiredService<ImageLibraryCatalog>();
        var scopeId = catalog.RegisterWriteScope(userId, agentId: "agent_iwr", skillId: "skill_iwr",
            seedLibraryIds: [libId], requiresApproval: true);

        // 同时订阅回执事件
        using var subReq = Authed(HttpMethod.Post, "/ag-ui/admin/webhooks", token,
            JsonContent.Create(new { name = "iwr-hook", url = _fx.ReceiverBase + "/hook", eventTypes = new[] { EventTypes.ImageWriteResolved } }));
        (await _client.SendAsync(subReq)).EnsureSuccessStatusCode();

        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0B };
        using var addReq = new HttpRequestMessage(HttpMethod.Post, $"/ag-ui/images/libraries/{libId}/assets")
        {
            Content = JsonContent.Create(new
            {
                writeScopeId = scopeId,
                fileName = "resolved.png",
                base64 = Convert.ToBase64String(png),
            }),
        };
        addReq.Headers.Add(SelfApi.TokenEnvVar, SelfApi.Token);
        var addRes = await _client.SendAsync(addReq);
        Assert.Equal(System.Net.HttpStatusCode.Accepted, addRes.StatusCode);
        var pendingId = (await addRes.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("pendingId").GetString()!;

        using var approve = Authed(HttpMethod.Post, $"/ag-ui/admin/image-write-pending/{pendingId}/approve", token);
        (await _client.SendAsync(approve)).EnsureSuccessStatusCode();

        await WaitForHitAsync(h => h.EventType == EventTypes.ImageWriteResolved && h.Body.Contains(pendingId));
        var hit = _fx.Hits.FirstOrDefault(h => h.EventType == EventTypes.ImageWriteResolved && h.Body.Contains(pendingId));
        Assert.NotNull(hit);
        var data = JsonDocument.Parse(hit!.Body).RootElement.GetProperty("data");
        Assert.Equal(pendingId, data.GetProperty("pendingId").GetString());
        Assert.Equal("approved", data.GetProperty("status").GetString());
        Assert.False(data.GetProperty("duplicated").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(data.GetProperty("assetId").GetString()));

        // 6) 图库管理「最近处理」也能看到这条回执（含处理人）
        using var histReq = Authed(HttpMethod.Get, "/ag-ui/admin/image-write-pending/history", token);
        var histRes = await _client.SendAsync(histReq);
        histRes.EnsureSuccessStatusCode();
        var items = (await histRes.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items").EnumerateArray().ToList();
        var entry = items.FirstOrDefault(x => x.GetProperty("pendingId").GetString() == pendingId);
        Assert.True(entry.ValueKind == JsonValueKind.Object, "最近处理应包含该 pendingId）；实际：" + string.Join(",", items.Select(x => x.GetProperty("pendingId").GetString())));
        Assert.Equal("approved", entry.GetProperty("status").GetString());
        Assert.Equal("iwp_admin", entry.GetProperty("decidedBy").GetString());
        Assert.Equal(libId, entry.GetProperty("libId").GetString());
        Assert.Equal("resolved.png", entry.GetProperty("fileName").GetString());

        // 7) 清空历史：只清回执，接口返回条数；随后列表为空
        using var clearReq = Authed(HttpMethod.Delete, "/ag-ui/admin/image-write-pending/history", token);
        var clearRes = await _client.SendAsync(clearReq);
        clearRes.EnsureSuccessStatusCode();
        var cleared = (await clearRes.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("cleared").GetInt32();
        Assert.True(cleared >= 1, "清空条数应 ≥1，实际 " + cleared);

        using var afterReq = Authed(HttpMethod.Get, "/ag-ui/admin/image-write-pending/history", token);
        var after = (await (await _client.SendAsync(afterReq)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items");
        Assert.Equal(0, after.GetArrayLength());
    }

    [Fact]
    public async Task FailedApprove_KeepsItemPendingForRetry()
    {
        var (token, userId) = await AdminAsync();

        // 建库 + 入队一条待审批
        using var createLib = Authed(HttpMethod.Post, "/ag-ui/image-libs/", token,
            JsonContent.Create(new { name = "iwp-fail-lib", description = "e2e" }));
        var libRes = await _client.SendAsync(createLib);
        libRes.EnsureSuccessStatusCode();
        var libId = (await libRes.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("libId").GetString()!;

        var catalog = _fx.App.Services.GetRequiredService<ImageLibraryCatalog>();
        var scopeId = catalog.RegisterWriteScope(userId, agentId: "agent_fail", skillId: "skill_fail",
            seedLibraryIds: [libId], requiresApproval: true);

        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0C };
        using var addReq = new HttpRequestMessage(HttpMethod.Post, $"/ag-ui/images/libraries/{libId}/assets")
        {
            Content = JsonContent.Create(new { writeScopeId = scopeId, fileName = "retry.png", base64 = Convert.ToBase64String(png) }),
        };
        addReq.Headers.Add(SelfApi.TokenEnvVar, SelfApi.Token);
        var addRes = await _client.SendAsync(addReq);
        Assert.Equal(System.Net.HttpStatusCode.Accepted, addRes.StatusCode);
        var pendingId = (await addRes.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("pendingId").GetString()!;

        // 删掉目标图库 → 批准必然失败（1.0.289：失败不再把待审批项吃掉）
        using var delLib = Authed(HttpMethod.Delete, $"/ag-ui/image-libs/{libId}", token);
        (await _client.SendAsync(delLib)).EnsureSuccessStatusCode();

        using var approve = Authed(HttpMethod.Post, $"/ag-ui/admin/image-write-pending/{pendingId}/approve", token);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await _client.SendAsync(approve)).StatusCode);

        using var pendingReq = Authed(HttpMethod.Get, "/ag-ui/admin/image-write-pending", token);
        var pendingItems = (await (await _client.SendAsync(pendingReq)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items");
        Assert.Contains(pendingItems.EnumerateArray(), x => x.GetProperty("id").GetString() == pendingId);

        using var histReq = Authed(HttpMethod.Get, "/ag-ui/admin/image-write-pending/history", token);
        var hist = (await (await _client.SendAsync(histReq)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items");
        var failed = hist.EnumerateArray().FirstOrDefault(x => x.GetProperty("pendingId").GetString() == pendingId);
        Assert.Equal("failed", failed.GetProperty("status").GetString());
        Assert.Equal("library_missing", failed.GetProperty("reason").GetString());

        // 收尾：拒掉这一项（仍能正常处理）
        using var reject = Authed(HttpMethod.Post, $"/ag-ui/admin/image-write-pending/{pendingId}/reject", token);
        (await _client.SendAsync(reject)).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task EventCatalog_ContainsImageWritePending()
    {
        var (token, _) = await AdminAsync();
        using var req = Authed(HttpMethod.Get, "/ag-ui/admin/webhooks/events", token);
        var res = await _client.SendAsync(req);
        res.EnsureSuccessStatusCode();
        var types = await res.Content.ReadFromJsonAsync<string[]>();
        Assert.NotNull(types);
        Assert.Contains(EventTypes.ImageWritePending, types!);
    }
}
