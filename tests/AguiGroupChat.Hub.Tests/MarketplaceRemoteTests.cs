using System.Net;
using System.Text;
using AguiGroupChat.Agents;
using AguiGroupChat.Hub.Infra;
using AguiGroupChat.Hub.Models;
using AguiGroupChat.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>远程市场（在线分发）单测：目录 / 包解析、地址解析、校验、尺寸 / 协议防护。</summary>
public sealed class MarketplaceRemoteTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(responder(request));
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK)
        => new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private const string CatalogJson =
        """{"packs":[{"packId":"demo","name":"演示包","description":"d","agentCount":2}]}""";

    private const string PackJson =
        """{"schema":"knowgath/marketplace-pack/v1","packId":"demo","name":"演示包","agents":[{"agentId":"a1","nickname":"甲"},{"agentId":"a2","nickname":"乙"}]}""";

    private static MarketplaceRemoteService Create(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => new(NullLogger<MarketplaceRemoteService>.Instance, new HttpClient(new StubHandler(responder)));

    private static MarketplaceSource Source(string url = "https://catalog.example/catalog.json")
        => new() { Id = "ms_1", Name = "官方目录", Url = url };

    [Fact]
    public async Task FetchCatalog_ParsesPacks()
    {
        using var svc = Create(_ => Json(CatalogJson));
        var res = await svc.FetchCatalogAsync(Source(), CancellationToken.None);
        Assert.True(res.Ok);
        var p = Assert.Single(res.Packs);
        Assert.Equal("demo", p.PackId);
        Assert.Equal(2, p.AgentCount);
    }

    [Fact]
    public async Task FetchPack_ResolvesRelativeUrl_AndParses()
    {
        var seen = new List<string>();
        using var svc = Create(req =>
        {
            seen.Add(req.RequestUri!.ToString());
            return req.RequestUri!.AbsolutePath.EndsWith("catalog.json", StringComparison.Ordinal)
                ? Json(CatalogJson)
                : Json(PackJson);
        });

        var doc = await svc.FetchPackAsync(Source(), "demo", CancellationToken.None);

        Assert.Equal("demo", doc.PackId);
        Assert.Equal(2, doc.Agents.Count);
        Assert.Contains("https://catalog.example/demo.json", seen); // 相对地址按目录同目录解析
    }

    [Fact]
    public async Task FetchCatalog_FailureIsReportedNotThrown()
    {
        using var svc = Create(_ => Json("nope", HttpStatusCode.InternalServerError));
        var res = await svc.FetchCatalogAsync(Source(), CancellationToken.None);
        Assert.False(res.Ok);
        Assert.Contains("500", res.Error);
    }

    [Fact]
    public async Task FetchPack_UnknownPack_Throws()
    {
        using var svc = Create(_ => Json(CatalogJson));
        await Assert.ThrowsAsync<AguiProtocolException>(() => svc.FetchPackAsync(Source(), "nope", CancellationToken.None));
    }

    [Fact]
    public async Task FetchPack_WrongSchema_Throws()
    {
        using var svc = Create(req => req.RequestUri!.AbsolutePath.EndsWith("catalog.json", StringComparison.Ordinal)
            ? Json(CatalogJson)
            : Json("""{"schema":"other/v9","packId":"demo","agents":[{"agentId":"a","nickname":"n"}]}"""));
        await Assert.ThrowsAsync<AguiProtocolException>(() => svc.FetchPackAsync(Source(), "demo", CancellationToken.None));
    }

    [Fact]
    public async Task FetchPack_EmptyAgents_Throws()
    {
        using var svc = Create(req => req.RequestUri!.AbsolutePath.EndsWith("catalog.json", StringComparison.Ordinal)
            ? Json(CatalogJson)
            : Json("""{"schema":"knowgath/marketplace-pack/v1","packId":"demo","name":"x","agents":[]}"""));
        await Assert.ThrowsAsync<AguiProtocolException>(() => svc.FetchPackAsync(Source(), "demo", CancellationToken.None));
    }

    [Fact]
    public async Task FetchCatalog_RejectsNonHttpScheme()
    {
        using var svc = Create(_ => Json(CatalogJson));
        var res = await svc.FetchCatalogAsync(Source("file:///etc/passwd"), CancellationToken.None);
        Assert.False(res.Ok);
        Assert.Contains("http", res.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FetchCatalog_RejectsOversizedBody()
    {
        var big = new string('x', 3 * 1024 * 1024); // > 2MB
        using var svc = Create(_ => Json("{\"packs\":[],\"pad\":\"" + big + "\"}"));
        var res = await svc.FetchCatalogAsync(Source(), CancellationToken.None);
        Assert.False(res.Ok);
        Assert.Contains("过大", res.Error);
    }

    [Fact]
    public void ImportDefinitions_RemapsIntraPackReferences()
    {
        var options = new AgentOptions { Provider = "mock" };
        var catalog = new AgentCatalog(options, NullLoggerFactory.Instance, new ServiceCollection().BuildServiceProvider());
        var market = new MarketplaceService(catalog);

        var agents = new List<AgentDefinition>
        {
            new() { AgentId = "lead", Nickname = "主管", Instructions = "" },
            new() { AgentId = "worker", Nickname = "执行", Instructions = "", AssignmentIds = ["lead"], EscalationAgentId = "lead" },
        };
        var result = market.ImportDefinitions("demo", "演示包", agents, "user_1");

        Assert.Equal(2, result.AgentsCreated);
        var worker = catalog.GetDefinition("worker")!;
        Assert.Equal("user_1", worker.OwnerId);
        Assert.Equal(["lead"], worker.AssignmentIds);   // 包内引用仍指向新 ID（此处同名）
        Assert.Equal("lead", worker.EscalationAgentId);
    }

    [Fact]
    public void ImportDefinitions_RenamedId_RemapsReferencesToRenamedTarget()
    {
        var options = new AgentOptions { Provider = "mock" };
        var catalog = new AgentCatalog(options, NullLoggerFactory.Instance, new ServiceCollection().BuildServiceProvider());
        var market = new MarketplaceService(catalog);
        catalog.Upsert(new AgentDefinition { AgentId = "lead", Nickname = "已有同名", Instructions = "" }); // 占位导致改名

        market.ImportDefinitions("demo", "演示包", [
            new AgentDefinition { AgentId = "lead", Nickname = "新主管", Instructions = "" },
            new AgentDefinition { AgentId = "worker", Nickname = "执行", Instructions = "", AssignmentIds = ["lead"] },
        ], "user_1");

        var worker = catalog.GetDefinition("worker")!;
        Assert.Equal(["lead_2"], worker.AssignmentIds);           // 重映射到改名后的 lead_2
        Assert.Equal("已有同名", catalog.GetDefinition("lead")!.Nickname); // 未覆盖已有
        Assert.Equal("新主管", catalog.GetDefinition("lead_2")!.Nickname);
    }
}
