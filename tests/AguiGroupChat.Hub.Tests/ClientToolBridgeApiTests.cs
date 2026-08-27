using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AguiGroupChat.Agents;
using AguiGroupChat.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>自托管 Kestrel 集成测试夹具：Hub + MSAGENT 网关（mock）+ 技能库 + 客户端技能本机桥。</summary>
public sealed class ClientToolBridgeFixture : IAsyncLifetime
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
            ["Auth:RequireTokenOnRealTime"] = "true", // 本机桥强制令牌鉴权
            ["Auth:AdminUserIds"] = "admin_chief",
        });
        HubApp.ConfigureServices(builder);
        builder.Services.AddAgentFramework(builder.Configuration);
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));

        App = builder.Build();
        HubApp.MapEndpoints(App);
        App.MapSkillApi();
        App.MapClientToolBridgeApi(); // 客户端执行技能的 shell 本机桥
        await App.StartAsync();
        HttpBase = App.Urls.First();
    }

    public async Task DisposeAsync()
    {
        if (App is not null) await App.DisposeAsync();
    }
}

public sealed class ClientToolBridgeApiTests : IClassFixture<ClientToolBridgeFixture>
{
    private readonly ClientToolBridgeFixture _fixture;
    private readonly HttpClient _client;

    public ClientToolBridgeApiTests(ClientToolBridgeFixture fixture)
    {
        _fixture = fixture;
        _client = new HttpClient { BaseAddress = new Uri(fixture.HttpBase) };
    }

    private async Task<string> RegisterAsync(string username)
    {
        var res = await _client.PostAsJsonAsync("/ag-ui/user/register", new { username, password = "secret1", nickname = username });
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
    }

    private HttpRequestMessage Authed(HttpMethod method, string path, string token)
    {
        var req = new HttpRequestMessage(method, path);
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return req;
    }

    [Fact]
    public async Task Shell_WithoutToken_Returns401()
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/ag-ui/client-tool")
        {
            Content = JsonContent.Create(new { kind = "shell", command = "echo hi" }),
        };
        var res = await _client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Shell_WithToken_RunsCommandInSandbox()
    {
        var token = await RegisterAsync("bridge_demo");
        using var req = Authed(HttpMethod.Post, "/ag-ui/client-tool", token);
        req.Content = JsonContent.Create(new { kind = "shell", command = "echo hello-from-client" });

        var res = await _client.SendAsync(req);
        res.EnsureSuccessStatusCode();
        var d = await res.Content.ReadFromJsonAsync<JsonElement>();
        var output = d.GetProperty("output").GetString()!;
        Assert.Contains("hello-from-client", output);
        // 输出携带运行沙箱内的退出码信息（隔离工作目录）
        Assert.Contains("退出码 0", output);
    }

    [Fact]
    public async Task Shell_EmptyCommand_ReturnsBadRequest()
    {
        var token = await RegisterAsync("bridge_demo2");
        using var req = Authed(HttpMethod.Post, "/ag-ui/client-tool", token);
        req.Content = JsonContent.Create(new { kind = "shell", command = "   " });

        var res = await _client.SendAsync(req);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Shell_ExceedingTimeout_ReturnsGracefulMessage()
    {
        var token = await RegisterAsync("bridge_demo3");
        using var req = Authed(HttpMethod.Post, "/ag-ui/client-tool", token);
        // 命令耗时超 1 秒超时 → 应被强制终止，秒级返回软超时文案（结果回灌模型，不丢卡片）
        req.Content = JsonContent.Create(new
        {
            kind = "shell",
            command = OperatingSystem.IsWindows() ? "Start-Sleep -Seconds 5; Write-Output done" : "sleep 5 && echo done",
            timeoutSec = 1,
        });

        var res = await _client.SendAsync(req);
        res.EnsureSuccessStatusCode();
        var d = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("超时", d.GetProperty("output").GetString()!);
    }

    [Fact]
    public async Task Shell_QuerySubstitution_EmbedsArgv()
    {
        var token = await RegisterAsync("bridge_demo4");
        using var req = Authed(HttpMethod.Post, "/ag-ui/client-tool", token);
        // shell 脚本可通过环境变量 QUERY 读调用参数（Unix 用 $QUERY，Windows PowerShell 用 $env:QUERY）
        var cmd = OperatingSystem.IsWindows()
            ? "Write-Output \"QUERY=$env:QUERY\""
            : "echo \"QUERY=$QUERY\"";
        req.Content = JsonContent.Create(new { kind = "shell", command = cmd, query = "你好，世界" });

        var res = await _client.SendAsync(req);
        res.EnsureSuccessStatusCode();
        var d = await res.Content.ReadFromJsonAsync<JsonElement>();
        var output = d.GetProperty("output").GetString()!;
        Assert.Contains("你好，世界", output);
    }
}
