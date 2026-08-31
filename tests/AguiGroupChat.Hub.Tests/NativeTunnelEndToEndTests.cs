using AguiGroupChat.Agents;
using AguiGroupChat.Hub;
using AguiGroupChat.Hub.Agents;
using AguiGroupChat.Hub.Models;
using AguiGroupChat.NativeBridge;
using AguiGroupChat.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
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
    private const string AgentHost = "agent_host";
    private const string Token = "test-tunnel-token";
    private const string AgentPerAgent = "agent_special";
    private const string AgentPerToken = "per-agent-secret";

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
        builder.Services.AddSingleton(sp => new NativeTunnelRateLimitBag(sp.GetRequiredService<NativeTunnelOptions>()));
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
        builder.Services.AddSingleton(sp => new NativeTunnelRateLimitBag(sp.GetRequiredService<NativeTunnelOptions>()));
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

    /// <summary>
    /// 完整链路端到端验证：真实桥经 SSE 反向隧道连入同一 <see cref="NativeTunnelService"/>，
    /// 数字员工（mock 模型）被 @ 后调用其挂载的客户端 shell 技能 → 网关识别到该员工已有隧道在线，
    /// 直接把执行推给内网桥（而非前端浏览器）→ 桥在本机执行 hostname → 结果回灌 → 模型基于结果作答。
    /// 这覆盖了「网关路由 → 隧道 → 桥执行 → 结果回灌模型」此前未被自动化覆盖的一段。
    /// </summary>
    [Fact]
    public async Task Gateway_RoutesClientSkill_ThroughReverseTunnel_AndAnswers()
    {
        // —— 1) 启动真实宿主（用于桥 SSE 注册），取出共享 NativeTunnelService 实例 ——
        var envBuilder = HubApp.CreateBuilder([]);
        envBuilder.Environment.EnvironmentName = "Testing";
        envBuilder.WebHost.UseUrls("http://127.0.0.1:0");
        envBuilder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["GroupChat:SeedSampleData"] = "false",
            ["Agents:Provider"] = "mock",
            ["Persistence:Enabled"] = "false",
            ["Auth:RequireTokenOnRealTime"] = "false",
            ["NativeTunnel:Token"] = Token,
        });
        HubApp.ConfigureServices(envBuilder);
        envBuilder.Services.AddAgentFramework(envBuilder.Configuration);
        envBuilder.Services.AddSingleton<NativeTunnelService>();
        envBuilder.Services.AddSingleton(envBuilder.Configuration.GetSection("NativeTunnel").Get<NativeTunnelOptions>() ?? new NativeTunnelOptions());
        envBuilder.Services.AddSingleton(sp => new NativeTunnelRateLimitBag(sp.GetRequiredService<NativeTunnelOptions>()));
        envBuilder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));
        await using var app = envBuilder.Build();
        HubApp.MapEndpoints(app);
        app.MapNativeTunnelApi();
        await app.StartAsync();
        var nativeTunnel = app.Services.GetRequiredService<NativeTunnelService>();

        // —— 2) 真实桥连入该宿主（绑定 agent_host）——
        using var quit = new CancellationTokenSource();
        var bridge = new NativeTunnelClient(app.Urls.First(), AgentHost, Token);
        var bridgeTask = Task.Run(() => bridge.RunAsync(quit.Token));
        await AssertEventuallyAsync(() => nativeTunnel.HasTunnel(AgentHost), TimeSpan.FromSeconds(20), "桥未能在超时内经隧道注册");

        // —— 3) 群 + 技能库 + 数字员工（mock）——
        var f = new HubFixture();
        var group = await f.Hub.CreateGroupAsync(new GroupCreateRequest
        {
            GroupName = "g", OwnerId = "user_1", MemberIds = [AgentHost],
            Members = [new MemberSeed { MemberId = AgentHost, MemberType = MemberType.Agent, Nickname = "主机名助手" }],
        });
        var (conn, inbox) = f.NewConnection("user_1");
        await f.Hub.SubscribeAsync(conn, [group.GroupId]);
        f.Drain(inbox);

        var skill = new AgentSkillDefinition
        {
            SkillId = "sk_hostname", Name = "查询主机名", Description = "在本机查询主机名",
            Kind = AgentSkillKind.Shell, ExecutionLocation = AgentSkillExecutionLocation.Client,
            ClientRunner = "{\"kind\":\"shell\",\"command\":\"hostname\",\"cwd\":\".\",\"timeoutSec\":30}",
        };
        var options = new AgentOptions
        {
            Provider = "mock",
            EnableTools = true,
            CoordinatorPlanning = false,
            Skills = [skill],
            Agents =
            [
                new AgentDefinition
                {
                    AgentId = AgentHost, Nickname = "主机名助手", Description = "查询主机名", Instructions = "你是主机名助手",
                    TriggerMode = AgentTriggerMode.Mentioned, SkillDefIds = ["sk_hostname"],
                },
            ],
        };
        var skillCatalog = new AgentSkillCatalog(NullLoggerFactory.Instance, options);
        var catalog = new AgentCatalog(options, NullLoggerFactory.Instance,
            new ServiceCollection().AddSingleton(skillCatalog).BuildServiceProvider());
        // 网关的 _nativeTunnel 从 services 惰性解析 → 必须与宿主共享同一 NativeTunnelService 实例
        var svcs = new ServiceCollection()
            .AddSingleton(f.Hub)
            .AddSingleton<NativeTunnelService>(nativeTunnel)
            .AddSingleton(skillCatalog)
            .BuildServiceProvider();
        var gateway = new AgentGateway(catalog, svcs, options, attachmentStore: null,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AgentGateway>.Instance);

        // —— 4) 触发 @，期待 mock 调用 sk_hostname → 网关经隧道让本机桥执行 hostname ——
        var result = await gateway.InvokeAsync(new AgentInvocationContext(
            GroupId: group.GroupId, ThreadId: "thread_" + group.GroupId,
            AgentId: AgentHost, AgentNickname: "主机名助手", TriggerMessageId: "msg_trigger",
            TriggerUserId: "user_1", Content: "请问我电脑的主机名（hostname）是什么？请在本机执行技能查询。",
            Mentions: [], MentionAll: false), CancellationToken.None);

        Assert.True(result.Accepted, "网关运行失败: " + result.ErrorCode);

        // —— 5) 校验：完整回复落库，且其正文引用了桥在本机执行的真实主机名（证明结果经隧道回灌模型作答）——
        var events = f.Drain(inbox).Select(HubFixture.Parse).ToList();
        var start = events.First(e => e.GetProperty("type").GetString() == EventTypes.TextMessageStart);
        var messageId = start.GetProperty("messageId").GetString()!;
        var stored = f.Store.GetMessage(group.GroupId, messageId);
        Assert.NotNull(stored);
        Assert.Contains(Environment.MachineName, stored!.Content, StringComparison.OrdinalIgnoreCase);

        quit.Cancel();
        await Task.WhenAny(bridgeTask, Task.Delay(2000));
    }

    /// <summary>逐 agent 专属令牌：为该 agent 配置专有令牌后，持该令牌的桥能注册、持全局令牌反而被拒。</summary>
    [Fact]
    public async Task PerAgentToken_OverridesGlobal_ForThatAgent()
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
            ["NativeTunnel:Token"] = "global-token",
            ["NativeTunnel:AgentTokens:" + AgentPerAgent] = AgentPerToken, // 逐 agent 专有令牌
        });
        HubApp.ConfigureServices(builder);
        builder.Services.AddAgentFramework(builder.Configuration);
        builder.Services.AddSingleton<NativeTunnelService>();
        builder.Services.AddSingleton(builder.Configuration.GetSection("NativeTunnel").Get<NativeTunnelOptions>() ?? new NativeTunnelOptions());
        builder.Services.AddSingleton(sp => new NativeTunnelRateLimitBag(sp.GetRequiredService<NativeTunnelOptions>()));
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));
        await using var app = builder.Build();
        HubApp.MapEndpoints(app);
        app.MapNativeTunnelApi();
        await app.StartAsync();
        var svc = app.Services.GetRequiredService<NativeTunnelService>();

        // 用全局令牌连该 agent → 配置了逐 agent 专属令牌后，全局令牌对该 agent 无效 → 被拒
        using var quitGlobal = new CancellationTokenSource();
        var badBridge = new NativeTunnelClient(app.Urls.First(), AgentPerAgent, "global-token");
        _ = Task.Run(() => badBridge.RunAsync(quitGlobal.Token));
        await Task.Delay(1200);
        Assert.False(svc.HasTunnel(AgentPerAgent));
        quitGlobal.Cancel();

        // 用该 agent 的专属令牌连 → 成功注册
        using var quit = new CancellationTokenSource();
        var goodBridge = new NativeTunnelClient(app.Urls.First(), AgentPerAgent, AgentPerToken);
        _ = Task.Run(() => goodBridge.RunAsync(quit.Token));
        await AssertEventuallyAsync(() => svc.HasTunnel(AgentPerAgent), TimeSpan.FromSeconds(20), "持专属令牌的桥未能在超时内经隧道注册");
        quit.Cancel();
        await Task.Delay(500);
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
