using System.Net.Http.Json;
using System.Text.Json.Serialization;
using AguiGroupChat.Agents;
using AguiGroupChat.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>桌面式宿主（ClientTool:IsHostLocal=true）下，Client 技能试运行应直接在宿主执行、无需本机桥。</summary>
public sealed class SkillHostLocalFixture : IAsyncLifetime
{
    public Microsoft.AspNetCore.Builder.WebApplication App { get; private set; } = null!;
    public string HttpBase { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder([]);
        builder.Environment.EnvironmentName = "Testing";
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["GroupChat:SeedSampleData"] = "false",
            ["Agents:Provider"] = "mock",
            ["Persistence:Enabled"] = "false",
            ["Auth:AdminUserIds"] = "desktop_admin",
        });
        HubApp.ConfigureServices(builder);
        builder.Services.AddAgentFramework(builder.Configuration);
        // 桌面自托管标记：宿主即用户本机 → Client 技能在宿主直接跑
        builder.Services.AddSingleton(new ClientToolOptions { IsHostLocal = true });
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase)));

        App = builder.Build();
        HubApp.MapEndpoints(App);
        App.MapSkillApi();
        await App.StartAsync();
        HttpBase = App.Urls.First();
    }

    public async Task DisposeAsync()
    {
        if (App is not null) await App.DisposeAsync();
    }
}

public sealed class SkillHostLocalTests : IClassFixture<SkillHostLocalFixture>
{
    private readonly SkillHostLocalFixture _fx;
    private readonly HttpClient _c;
    public SkillHostLocalTests(SkillHostLocalFixture fx) { _fx = fx; _c = new HttpClient { BaseAddress = new Uri(fx.HttpBase) }; }

    private async Task<string> TokenAsync(string username)
    {
        var r = await _c.PostAsJsonAsync("/ag-ui/user/register", new { username, password = "secret1", nickname = username });
        r.EnsureSuccessStatusCode();
        return (await r.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("token").GetString()!;
    }

    [Fact]
    public async Task ClientDotnet_TrialRun_UsesHostDirectly_WithoutBridge()
    {
        var tok = await TokenAsync("desktop_admin");
        _c.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", tok);
        // 建一个 dotnet + client 技能（仅管理员可建；desktop_admin 是管理员）
        var sid = "dn_host_" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var body = "using System; public class S{ public static string Run(string i){ return \"HOSTHIT \" + Environment.MachineName; } }";
        var create = await _c.PostAsJsonAsync("/ag-ui/skills", new
        {
            skillId = sid, name = "host dotnet", description = "test", kind = "dotnet",
            body, executionLocation = "client", requiresApproval = true
        });
        Assert.True(create.IsSuccessStatusCode, "create " + await create.Content.ReadAsStringAsync());

        var run = await _c.PostAsJsonAsync("/ag-ui/skills/" + sid + "/run", new { query = "x" });
        run.EnsureSuccessStatusCode();
        var txt = (await run.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("result").GetString()!;
        Assert.StartsWith("【本机 dotnet · 在桌面宿主机直接执行】", txt);
        Assert.Contains("HOSTHIT", txt);

        await _c.DeleteAsync("/ag-ui/skills/" + sid);
    }
}
