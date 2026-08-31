using AguiGroupChat.Agents;
using AguiGroupChat.Hub;
using AguiGroupChat.NativeBridge;
using AguiGroupChat.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>
/// 内网穿透反向隧道<b>端到端</b>验证：真实 Kestrel 宿主（含 NativeTunnelApi）+ 真实
/// <see cref="NativeTunnelClient"/>（桥侧）经 SSE 隧道连入 → 服务器 ExecuteAsync 下行任务 → 桥在
/// 本机真实执行 shell → 回传结果 → 服务器完成等待并返回。全程走真实 HTTP/SSE，不 mock 传输层。
/// </summary>
public sealed class NativeTunnelEndToEndTests
{
    private const string AgentId = "agent_ops";
    private const string Token = "test-tunnel-token";

    [Fact]
    public async Task RealBridge_ExecutesShellOverSseTunnel_AndReturnsResult()
    {
        // 1) 启动真实 Kestrel 宿主（mock 模型，关闭持久化，配置隧道令牌），映射隧道端点
        var builder = HubApp.CreateBuilder([]);
        builder.Environment.EnvironmentName = "Testing";
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["GroupChat:SeedSampleData"] = "false",
            ["Agents:Provider"] = "mock",
            ["Persistence:Enabled"] = "false",
            ["Auth:RequireTokenOnRealTime"] = "false",
            ["NativeTunnel:Token"] = Token,
        });
        HubApp.ConfigureServices(builder);
        builder.Services.AddAgentFramework(builder.Configuration);
        builder.Services.AddSingleton<NativeTunnelService>();
        builder.Services.AddSingleton(builder.Configuration.GetSection("NativeTunnel").Get<NativeTunnelOptions>() ?? new NativeTunnelOptions());
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));
        await using var app = builder.Build();
        HubApp.MapEndpoints(app);
        app.MapNativeTunnelApi();
        await app.StartAsync();
        var hubBase = app.Urls.First();

        var svc = app.Services.GetRequiredService<NativeTunnelService>();

        // 2) 启动真实桥客户端（与生产同款 NativeTunnelClient）在后台连入
        using var quit = new CancellationTokenSource();
        var bridge = new NativeTunnelClient(hubBase, AgentId, Token);
        var bridgeTask = Task.Run(() => bridge.RunAsync(quit.Token));

        // 3) 等桥经 SSE 注册成功
        await AssertEventuallyAsync(() => svc.HasTunnel(AgentId), TimeSpan.FromSeconds(20),
            "内网桥未能在超时内经 SSE 隧道连入 Hub 并注册");

        // 4) 服务器下发一个真实的 shell 任务（hostname），期待桥在本机执行并回传真实主机名
        var result = await svc.ExecuteAsync(AgentId, "hostname", null, 30, null,
            TimeSpan.FromSeconds(20), CancellationToken.None);

        // 5) 校验：桥真实执行了命令，结果非空且包含本机主机名（区分于“未执行返回空/超时”）。
        //    Windows 下 hostname 与该阶段 netbios 名的原始大小写可能不同（AiBook vs AIBOOK），故用忽略大小写比较。
        Assert.False(string.IsNullOrWhiteSpace(result), "隧道应返回桥在本机执行的 hostname 结果，而非空/超时");
        Assert.Contains(Environment.MachineName, result, StringComparison.OrdinalIgnoreCase);

        quit.Cancel();
        await Task.WhenAny(bridgeTask, Task.Delay(2000));
    }

    [Fact]
    public async Task RealBridge_WrongToken_IsRejected()
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
            ["NativeTunnel:Token"] = Token,
        });
        HubApp.ConfigureServices(builder);
        builder.Services.AddAgentFramework(builder.Configuration);
        builder.Services.AddSingleton<NativeTunnelService>();
        builder.Services.AddSingleton(builder.Configuration.GetSection("NativeTunnel").Get<NativeTunnelOptions>() ?? new NativeTunnelOptions());
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));
        await using var app = builder.Build();
        HubApp.MapEndpoints(app);
        app.MapNativeTunnelApi();
        await app.StartAsync();
        var hubBase = app.Urls.First();

        var svc = app.Services.GetRequiredService<NativeTunnelService>();

        using var quit = new CancellationTokenSource();
        var bridge = new NativeTunnelClient(hubBase, AgentId, "wrong-token");
        var bridgeTask = Task.Run(() => bridge.RunAsync(quit.Token));

        // 错误令牌：桥反复尝试 401，不应注册成功 → HasTunnel 保持 false
        await Task.Delay(1500);
        Assert.False(svc.HasTunnel(AgentId));

        quit.Cancel();
        await Task.WhenAny(bridgeTask, Task.Delay(2000));
    }

    private static async Task AssertEventuallyAsync(Func<bool> condition, TimeSpan timeout, string failMsg)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(100);
        }
        Assert.Fail(failMsg);
    }
}
