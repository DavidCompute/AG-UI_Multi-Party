using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AguiGroupChat.Agents;
using AguiGroupChat.Hub;
using AguiGroupChat.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>
/// 运行指标导出（Prometheus 文本格式）集成测试：鉴权（无令牌 401 / 抓取令牌 200 / 会话令牌 200）
/// 与「关闭时 404」；并校验返回的是 Prometheus 文本且含核心指标。
/// </summary>
public sealed class MetricsApiServerFixture : IAsyncLifetime
{
    public const string ScrapeToken = "scrape_secret_123";

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
        });
        HubApp.ConfigureServices(builder);
        builder.Services.AddAgentFramework(builder.Configuration);
        builder.Services.AddSingleton(new ObservabilityOptions
        {
            PrometheusEnabled = true,
            PrometheusRequireAuth = true,
            PrometheusScrapeToken = ScrapeToken,
        });
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));

        App = builder.Build();
        HubApp.MapEndpoints(App);
        App.MapMetricsApi();
        await App.StartAsync();
        HttpBase = App.Urls.First();
    }

    public async Task DisposeAsync() => await App.DisposeAsync();
}

public sealed class MetricsApiTests : IClassFixture<MetricsApiServerFixture>
{
    private readonly MetricsApiServerFixture _fx;
    private readonly HttpClient _client;

    public MetricsApiTests(MetricsApiServerFixture fx)
    {
        _fx = fx;
        _client = new HttpClient { BaseAddress = new Uri(fx.HttpBase) };
    }

    [Fact]
    public async Task NoToken_IsUnauthorized()
    {
        var res = await _client.GetAsync("/ag-ui/metrics");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task ScrapeToken_ReturnsPrometheusText()
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/ag-ui/metrics");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", MetricsApiServerFixture.ScrapeToken);
        var res = await _client.SendAsync(req);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("text/plain", res.Content.Headers.ContentType?.MediaType);
        var body = await res.Content.ReadAsStringAsync();
        Assert.Contains("knowgath_up 1", body);
        Assert.Contains("# TYPE knowgath_agent_invocations_total counter", body);
        Assert.Contains("knowgath_process_working_set_bytes", body);
    }

    [Fact]
    public async Task ScrapeTokenViaQuery_AlsoWorks()
    {
        var res = await _client.GetAsync("/ag-ui/metrics?token=" + MetricsApiServerFixture.ScrapeToken);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task SessionToken_Works()
    {
        var reg = await _client.PostAsJsonAsync("/ag-ui/user/register", new { username = "metrics_viewer", password = "secret1", nickname = "m" });
        if (!reg.IsSuccessStatusCode)
            reg = await _client.PostAsJsonAsync("/ag-ui/user/login", new { username = "metrics_viewer", password = "secret1" });
        reg.EnsureSuccessStatusCode();
        var token = (await reg.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;

        using var req = new HttpRequestMessage(HttpMethod.Get, "/ag-ui/metrics");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Assert.Equal(HttpStatusCode.OK, (await _client.SendAsync(req)).StatusCode);
    }

    [Fact]
    public async Task WrongToken_IsUnauthorized()
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/ag-ui/metrics");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-token");
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.SendAsync(req)).StatusCode);
    }

    [Fact]
    public async Task Disabled_Returns404()
    {
        var options = _fx.App.Services.GetRequiredService<ObservabilityOptions>();
        options.PrometheusEnabled = false;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "/ag-ui/metrics");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", MetricsApiServerFixture.ScrapeToken);
            Assert.Equal(HttpStatusCode.NotFound, (await _client.SendAsync(req)).StatusCode);
        }
        finally { options.PrometheusEnabled = true; }
    }
}
