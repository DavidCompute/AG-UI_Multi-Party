using System.Net;
using System.Net.Sockets;
using AguiGroupChat.Agents;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>桥接端点健康度（3.1）测试：连通探测 up / down。</summary>
public sealed class BridgeHealthTests
{
    private static BridgeHealthService CreateService(params AgentDefinition[] bridgeAgents)
    {
        var options = new AgentOptions
        {
            Provider = "mock",
            Agents = bridgeAgents.ToList(),
        };
        var catalog = new AgentCatalog(options, NullLoggerFactory.Instance, new ServiceCollection().BuildServiceProvider());
        return new BridgeHealthService(catalog, options, NullLogger<BridgeHealthService>.Instance);
    }

    [Fact]
    public async Task ProbeReachableEndpoint_MarksUp()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        // 后台接受连接（Probe 的 TcpClient 构造函数会连上）
        _ = Task.Run(() => { try { listener.AcceptTcpClient(); } catch { /* 停止时忽略 */ } });

        var svc = CreateService(new AgentDefinition
        {
            AgentId = "ext",
            Nickname = "外部专家",
            Instructions = "",
            BridgeEndpoint = $"ws://127.0.0.1:{port}/ws",
        });

        var status = await svc.ProbeAllAsync(CancellationToken.None);
        var hit = Assert.Single(status);
        Assert.True(hit.Up);
        Assert.Equal("ext", hit.AgentId);
        Assert.True(hit.LatencyMs >= 0);
        listener.Stop();
    }

    [Fact]
    public async Task ProbeUnreachableEndpoint_MarksDown()
    {
        // 查找一个未监听的端口：先监听拿到端口，再关闭它
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var svc = CreateService(new AgentDefinition
        {
            AgentId = "ext",
            Nickname = "外部专家",
            Instructions = "",
            BridgeEndpoint = $"http://127.0.0.1:{port}/",
        });

        var status = await svc.ProbeAllAsync(CancellationToken.None);
        var hit = Assert.Single(status);
        Assert.False(hit.Up);
        Assert.False(string.IsNullOrWhiteSpace(hit.Detail));
    }

    [Fact]
    public async Task ConsecutiveFailuresAccumulateInDetail()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop(); // 端口不再监听 → 每次探测都失败

        var svc = CreateService(new AgentDefinition
        {
            AgentId = "flaky",
            Nickname = "抖动桥",
            Instructions = "",
            BridgeEndpoint = $"ws://127.0.0.1:{port}/ws",
        });

        await svc.ProbeAllAsync(CancellationToken.None); // 第 1 次失败
        var second = Assert.Single(await svc.ProbeAllAsync(CancellationToken.None)); // 第 2 次失败
        Assert.False(second.Up);
        Assert.Equal(2, second.ConsecutiveFailures);
        Assert.Contains("已连续失败 2 次", second.Detail);

        // 恢复成功探测后连续失败清零（另一个可达端点，Same Set 逻辑验证）
        var ok = new TcpListener(IPAddress.Loopback, 0);
        ok.Start();
        var okPort = ((IPEndPoint)ok.LocalEndpoint).Port;
        _ = Task.Run(() => { try { ok.AcceptTcpClient(); } catch { /* 停止时忽略 */ } });
        var svc2 = CreateService(new AgentDefinition
        {
            AgentId = "okagent",
            Nickname = "正常桥",
            Instructions = "",
            BridgeEndpoint = $"ws://127.0.0.1:{okPort}/ws",
        });
        var hit = Assert.Single(await svc2.ProbeAllAsync(CancellationToken.None));
        Assert.True(hit.Up);
        Assert.Equal(0, hit.ConsecutiveFailures);
        ok.Stop();
    }

    [Fact]
    public async Task Probe_GlobalEndpoint_PlusAgentEndpoints()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = Task.Run(() => { try { listener.AcceptTcpClient(); } catch { } });

        var options = new AgentOptions
        {
            Provider = "mock",
            AguiBridge = new AguiBridgeOptions { Endpoint = $"ws://127.0.0.1:{port}/ws" },
            Agents = [],
        };
        var catalog = new AgentCatalog(options, NullLoggerFactory.Instance, new ServiceCollection().BuildServiceProvider());
        var svc = new BridgeHealthService(catalog, options, NullLogger<BridgeHealthService>.Instance);

        var status = await svc.ProbeAllAsync(CancellationToken.None);
        var hit = Assert.Single(status);
        Assert.Equal("__global__", hit.AgentId);
        Assert.True(hit.Up);
        listener.Stop();
    }
}
