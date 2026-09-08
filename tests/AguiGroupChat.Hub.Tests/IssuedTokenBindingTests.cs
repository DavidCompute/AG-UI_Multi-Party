using System.Text.Json;
using System.Text.Json.Serialization;
using AguiGroupChat.Agents;
using AguiGroupChat.Hub;
using AguiGroupChat.NativeBridge;
using AguiGroupChat.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Hosting;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>
/// 网页安装包“绑定型”令牌（NativeBridgeIssuedTokenStore）端到端：
///   1) 签发一枚令牌 → 桥 A（client=a）首次连接成功并绑定；
///   2) 桥 A 重连（同 client）仍成功；
///   3) 桥 B（client=b，即“包被拷到另一台机器”）用同一包令牌 → 401 被拒；
///   4) 管理员吊销后，即便原 client 重连也被拒。
/// </summary>
public class IssuedTokenBindingTests
{
    private const string AgentScope = NativeTunnelService.PlatformWideScope;

    [Fact]
    public async Task IssuedToken_BindsFirstClient_RejectsCopiedPackage_AndRevokes()
    {
        // —— 1) 真实宿主：关闭全局令牌（只有签发令牌可入），注册签发器 ——
        var builder = HubApp.CreateBuilder([]);
        builder.Environment.EnvironmentName = "Testing";
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["GroupChat:SeedSampleData"] = "false",
            ["Agents:Provider"] = "mock",
            ["Persistence:Enabled"] = "false",
            ["Auth:RequireTokenOnRealTime"] = "false",
            ["NativeTunnel:Token"] = "", // 不配全局令牌：证明签发令牌独立可用
        });
        HubApp.ConfigureServices(builder);
        builder.Services.AddAgentFramework(builder.Configuration);
        builder.Services.AddSingleton<NativeTunnelService>();
        // 签发器用临时文件隔离（避免并发测试读写共享文件 / 读到历史令牌）
        var tmpStore = Path.Combine(Path.GetTempPath(), $"nb-issued-{Guid.NewGuid():N}.json");
        builder.Services.AddSingleton(new NativeBridgeIssuedTokenStore(tmpStore));
        builder.Services.AddSingleton(builder.Configuration.GetSection("NativeTunnel").Get<NativeTunnelOptions>() ?? new NativeTunnelOptions());
        builder.Services.AddSingleton(sp => new NativeTunnelRateLimitBag(sp.GetRequiredService<NativeTunnelOptions>()));
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));
        await using var app = builder.Build();
        HubApp.MapEndpoints(app);
        app.MapNativeTunnelApi();
        await app.StartAsync();
        var hubBase = app.Urls.First();
        var svc = app.Services.GetRequiredService<NativeTunnelService>();
        var store = app.Services.GetRequiredService<NativeBridgeIssuedTokenStore>();
        try
        {
            await RunScenarioAsync(hubBase, svc, store);
        }
        finally
        {
            try { File.Delete(tmpStore); } catch { /* 忽略清理失败 */ }
        }
    }

    private static async Task RunScenarioAsync(string hubBase, NativeTunnelService svc, NativeBridgeIssuedTokenStore store)
    {

        // —— 2) 模拟“管理员下载安装包”：签发一枚令牌 ——
        var issued = store.Issue("测试安装包");
        Assert.False(string.IsNullOrWhiteSpace(issued.Token));
        Assert.Single(store.List());

        // —— 3) 桥 A（client=a）用该包令牌连接 → 成功并绑定 ——
        using var quitA = new CancellationTokenSource();
        var bridgeA = new NativeTunnelClient(hubBase, AgentScope, issued.Token, clientId: "machine-a");
        var taskA = Task.Run(() => bridgeA.RunAsync(quitA.Token));
        await AssertEventuallyAsync(() => svc.HasTunnel(AgentScope), TimeSpan.FromSeconds(15), "桥 A（签发令牌）未能在超时内经隧道注册");
        var entry = Assert.Single(store.List());
        Assert.Equal("machine-a", entry.ClientId); // 已绑定到首次连接的机器

        // —— 4) 桥 A 重连（同 client）仍成功：模拟机器重启自启 ——
        quitA.Cancel();
        await Task.WhenAny(taskA, Task.Delay(2000));
        using var quitA2 = new CancellationTokenSource();
        var bridgeA2 = new NativeTunnelClient(hubBase, AgentScope, issued.Token, clientId: "machine-a");
        var taskA2 = Task.Run(() => bridgeA2.RunAsync(quitA2.Token));
        await AssertEventuallyAsync(() => svc.HasTunnel(AgentScope), TimeSpan.FromSeconds(15), "桥 A 重连失败");

        // —— 5) 桥 B（client=b）用同一包（拷贝到其它机器）→ 401 被拒、不得注册 ——
        using var quitB = new CancellationTokenSource();
        var bridgeB = new NativeTunnelClient(hubBase, AgentScope, issued.Token, clientId: "machine-b");
        var taskB = Task.Run(() => bridgeB.RunAsync(quitB.Token));
        await Task.Delay(1800);
        Assert.False(svc.HasClient("machine-b"), "拷贝到其它机器的安装包（不同 client）不应被接受");

        // —— 6) 管理员吊销该令牌 → 即使原 client 重连也被拒 ——
        var revoked = store.Revoke(issued.Hash);
        Assert.True(revoked);
        quitA2.Cancel();
        await Task.WhenAny(taskA2, Task.Delay(2000));
        using var quitA3 = new CancellationTokenSource();
        var bridgeA3 = new NativeTunnelClient(hubBase, AgentScope, issued.Token, clientId: "machine-a");
        var taskA3 = Task.Run(() => bridgeA3.RunAsync(quitA3.Token));
        await Task.Delay(1800);
        Assert.False(svc.HasTunnel(AgentScope), "吊销后原 client 重连也应被拒");

        quitB.Cancel(); quitA3.Cancel();
        await Task.WhenAny(taskB, taskA3, Task.Delay(2000));
    }

    private static async Task AssertEventuallyAsync(Func<bool> cond, TimeSpan timeout, string failMsg)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (cond()) return;
            await Task.Delay(150);
        }
        Assert.Fail(failMsg);
    }
}
