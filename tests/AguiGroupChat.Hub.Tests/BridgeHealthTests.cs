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
