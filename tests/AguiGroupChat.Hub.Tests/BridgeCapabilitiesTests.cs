using System.Net;
using System.Text;
using System.Text.Json;
using AguiGroupChat.Agents;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>桥接能力协商（3.2）测试：http 能力端点探测 / ws 回退未知 / 客户端不可达。</summary>
public sealed class BridgeCapabilitiesTests
{
    private static BridgeCapabilitiesService Create(AgentDefinition? agent = null, string? global = null)
    {
        var options = new AgentOptions
        {
            Provider = "mock",
            AguiBridge = global is null ? null : new AguiBridgeOptions { Endpoint = global },
            Agents = agent is null ? [] : [agent],
        };
        var catalog = new AgentCatalog(options, NullLoggerFactory.Instance, new ServiceCollection().BuildServiceProvider());
        return new BridgeCapabilitiesService(catalog, options, NullLogger<BridgeCapabilitiesService>.Instance);
    }

    [Fact]
    public async Task ProbeHttpEndpoint_DiscoversCapabilities()
    {
        var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.Run(async ctx =>
        {
            if (ctx.Request.Path == "/capabilities")
            {
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsync(JsonSerializer.Serialize(new
                {
                    supportsTools = new[] { "read_attachment", "web_search" },
                    supportsAttachments = true,
                    approvalTypes = new[] { "approval", "input" },
                }));
            }
            else ctx.Response.StatusCode = 404;
        });
        await app.StartAsync();
        try
        {
            var baseUrl = app.Urls.First().TrimEnd('/');
            var svc = Create(global: baseUrl + "/");
            var results = await svc.ProbeAllAsync();
            var r = Assert.Single(results);
            Assert.True(r.Cap.Discovered);
            Assert.Contains("read_attachment", r.Cap.SupportsTools);
            Assert.True(r.Cap.SupportsAttachments);
            Assert.Contains("approval", r.Cap.ApprovalTypes);
        }
        finally { await app.DisposeAsync(); }
    }

    [Fact]
    public async Task ProbeWsEndpoint_ReturnsUnknown()
    {
        var svc = Create(agent: new AgentDefinition { AgentId = "ext", Nickname = "外部", Instructions = "", BridgeEndpoint = "ws://127.0.0.1:9/ws" });
        var results = await svc.ProbeAllAsync();
        var r = Assert.Single(results);
        Assert.False(r.Cap.Discovered); // ws/wss 无标准能力端点 → 未知
    }

    [Fact]
    public async Task ProbeUnreachableHttp_ReturnsUnknown_DoesNotThrow()
    {
        var svc = Create(agent: new AgentDefinition { AgentId = "ext", Nickname = "外部", Instructions = "", BridgeEndpoint = "http://127.0.0.1:9/" });
        var results = await svc.ProbeAllAsync(); // 不应抛出
        var r = Assert.Single(results);
        Assert.False(r.Cap.Discovered);
    }
}
