using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AguiGroupChat.Agents;
using AguiGroupChat.Hub;
using AguiGroupChat.Hub.Agents;
using AguiGroupChat.Hub.Models;
using AguiGroupChat.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json.Serialization;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>讨论 / 引用回复 / 消息保留 集成测试夹具：Hub + 智能体网关（mock）+ 管理 API。</summary>
public sealed class DiscussionServerFixture : IAsyncLifetime
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
            ["Auth:RequireTokenOnRealTime"] = "false",
            ["Auth:AdminUserIds"] = "discuss_admin",
            // 每用户每日配额 500 token：配额拦截测试使用（mock 无用量记录，不影响其他用例）
            ["Agents:DailyTokenQuotaPerUser"] = "500",
        });
        HubApp.ConfigureServices(builder);
        builder.Services.AddAgentFramework(builder.Configuration);
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));

        App = builder.Build();
        HubApp.MapEndpoints(App);
        App.MapAgentApi();
        await App.StartAsync();
        HttpBase = App.Urls.First();
    }

    public async Task DisposeAsync()
    {
        if (App is not null) await App.DisposeAsync();
    }
}

public sealed class DiscussionIntegrationTests : IClassFixture<DiscussionServerFixture>
{
    private readonly DiscussionServerFixture _fixture;
    private readonly HttpClient _client;

    public DiscussionIntegrationTests(DiscussionServerFixture fixture)
    {
        _fixture = fixture;
        _client = new HttpClient { BaseAddress = new Uri(fixture.HttpBase) };
    }

    private async Task<JsonElement> RegisterAsync(string username, string password = "secret1")
    {
        var res = await _client.PostAsJsonAsync("/ag-ui/user/register", new { username, password, nickname = username });
        if (res.StatusCode == HttpStatusCode.Conflict)
        {
            var login = await _client.PostAsJsonAsync("/ag-ui/user/login", new { username, password });
            login.EnsureSuccessStatusCode();
            return await login.Content.ReadFromJsonAsync<JsonElement>();
        }
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static HttpRequestMessage Authed(HttpMethod method, string path, string token)
    {
        var req = new HttpRequestMessage(method, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return req;
    }

    [Fact]
    public async Task Discussion_TriggersAgentsInOrder()
    {
        var user = await RegisterAsync("discuss_admin");
        var token = user.GetProperty("token").GetString()!;
        var userId = user.GetProperty("userId").GetString()!;

        // 创建两个智能体并加入群
        foreach (var (id, nick) in new[] { ("agent_discuss_a", "讨论甲"), ("agent_discuss_b", "讨论乙") })
        {
            using var createAgent = Authed(HttpMethod.Post, "/ag-ui/agents", token);
            createAgent.Content = JsonContent.Create(new
            {
                agentId = id, nickname = nick, description = "讨论成员",
                instructions = "你是讨论成员，请简短发表观点。", triggerMode = "mentioned", keywords = new string[0],
            });
            var created = await _client.SendAsync(createAgent);
            Assert.True(created.IsSuccessStatusCode, $"创建智能体失败: {await created.Content.ReadAsStringAsync()}");
        }
        var create = await _client.PostAsJsonAsync("/ag-ui/group/create", new
        {
            groupName = "讨论群", ownerId = userId,
            memberIds = new[] { userId, "agent_discuss_a", "agent_discuss_b" },
            members = new[]
            {
                new { memberId = "agent_discuss_a", memberType = "agent", nickname = "讨论甲" },
                new { memberId = "agent_discuss_b", memberType = "agent", nickname = "讨论乙" },
            },
        });
        create.EnsureSuccessStatusCode();
        var groupId = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("groupId").GetString()!;

        // 发起讨论（两个智能体）
        using var discuss = Authed(HttpMethod.Post, $"/ag-ui/group/{groupId}/discussion", token);
        discuss.Content = JsonContent.Create(new { content = "如何设计权限模型？", agentIds = new[] { "agent_discuss_a", "agent_discuss_b" } });
        var res = await _client.SendAsync(discuss);
        res.EnsureSuccessStatusCode();
        Assert.Equal(2, (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("agents").GetArrayLength());

        // mock 网关立即回复：轮询快照直到两个智能体都发言（快照消息无 senderType，按 senderId 前缀判断）
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        JsonElement snap = default;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var snapRes = await _client.SendAsync(Authed(HttpMethod.Get, $"/ag-ui/group/{groupId}", token));
            snapRes.EnsureSuccessStatusCode();
            snap = await snapRes.Content.ReadFromJsonAsync<JsonElement>();
            var agents = snap.GetProperty("latestMessages").EnumerateArray()
                .Where(m => (m.GetProperty("senderId").GetString() ?? "").StartsWith("agent_")).ToList();
            if (agents.Count >= 2) break;
            await Task.Delay(100);
        }
        var agentMsgs = snap.GetProperty("latestMessages").EnumerateArray()
            .Where(m => (m.GetProperty("senderId").GetString() ?? "").StartsWith("agent_")).Select(m => m.GetProperty("senderId").GetString()).ToList();
        Assert.Contains("agent_discuss_a", agentMsgs);
        Assert.Contains("agent_discuss_b", agentMsgs);

        // 非法：非成员智能体不能参与
        using var bad = Authed(HttpMethod.Post, $"/ag-ui/group/{groupId}/discussion", token);
        bad.Content = JsonContent.Create(new { content = "x", agentIds = new[] { "not_a_member" } });
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.SendAsync(bad)).StatusCode);

        // 非法：非群成员不能发起
        var outsider = await RegisterAsync("discuss_out");
        using var denied = Authed(HttpMethod.Post, $"/ag-ui/group/{groupId}/discussion", outsider.GetProperty("token").GetString());
        denied.Content = JsonContent.Create(new { content = "x", agentIds = new[] { "agent_discuss_a" } });
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(denied)).StatusCode);
    }

    [Fact]
    public async Task Discussion_QuotaBlocksExceededUser()
    {
        var user = await RegisterAsync("quota_user");
        var token = user.GetProperty("token").GetString()!;
        var userId = user.GetProperty("userId").GetString()!;

        // 创建智能体 + 建群
        using var createAgent = Authed(HttpMethod.Post, "/ag-ui/agents", token);
        createAgent.Content = JsonContent.Create(new
        {
            agentId = "agent_quota", nickname = "配额助手", description = "x",
            instructions = "简短回答", triggerMode = "mentioned", keywords = new string[0],
        });
        (await _client.SendAsync(createAgent)).EnsureSuccessStatusCode();
        var create = await _client.PostAsJsonAsync("/ag-ui/group/create", new
        {
            groupName = "配额群", ownerId = userId, memberIds = new[] { userId, "agent_quota" },
            members = new[] { new { memberId = "agent_quota", memberType = "agent", nickname = "配额助手" } },
        });
        create.EnsureSuccessStatusCode();
        var groupId = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("groupId").GetString()!;

        // 预置用量达到配额（500）：直接记录到用量存储
        var usage = _fixture.App.Services.GetRequiredService<AguiGroupChat.Hub.Agents.AgentUsageService>();
        usage.RecordUsage("agent_quota", userId, 300, 200, 0);

        // 触发智能体 → 配额拦截（AGENT_QUOTA_EXCEEDED），无智能体消息产生
        var send = await _client.PostAsJsonAsync("/ag-ui/group/message/send", new
        {
            groupId, userId, content = "@配额助手 你好", mentions = new[] { "agent_quota" }, timestamp = 1000L,
        });
        send.EnsureSuccessStatusCode();
        await Task.Delay(300);
        var snap = await (await _client.SendAsync(Authed(HttpMethod.Get, $"/ag-ui/group/{groupId}", token))).Content.ReadFromJsonAsync<JsonElement>();
        Assert.DoesNotContain(snap.GetProperty("latestMessages").EnumerateArray(),
            m => (m.GetProperty("senderId").GetString() ?? "").StartsWith("agent_"));

        // 其他用户不受影响
        var other = await RegisterAsync("quota_other");
        Assert.Null(usage.CheckUserQuota(other.GetProperty("userId").GetString()!));
    }

    [Fact]
    public async Task ReplyTo_IsPersistedInSnapshot_AndValidated()
    {
        var user = await RegisterAsync("reply_user");
        var token = user.GetProperty("token").GetString()!;
        var userId = user.GetProperty("userId").GetString()!;
        var create = await _client.PostAsJsonAsync("/ag-ui/group/create", new { groupName = "引用群", ownerId = userId, memberIds = new[] { userId } });
        create.EnsureSuccessStatusCode();
        var groupId = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("groupId").GetString()!;

        var first = await _client.PostAsJsonAsync("/ag-ui/group/message/send", new { groupId, userId, content = "原始消息", timestamp = 1000L });
        first.EnsureSuccessStatusCode();
        var firstId = (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("messageId").GetString()!;

        // 引用回复：带 replyToMessageId
        var reply = await _client.PostAsJsonAsync("/ag-ui/group/message/send", new { groupId, userId, content = "引用回复", replyToMessageId = firstId, timestamp = 2000L });
        reply.EnsureSuccessStatusCode();
        var replyId = (await reply.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("messageId").GetString()!;

        // 快照消息带 replyToMessageId（前端渲染引用行）
        var snap = await (await _client.SendAsync(Authed(HttpMethod.Get, $"/ag-ui/group/{groupId}", token))).Content.ReadFromJsonAsync<JsonElement>();
        var replyMsg = Assert.Single(snap.GetProperty("latestMessages").EnumerateArray(), m => m.GetProperty("messageId").GetString() == replyId);
        Assert.Equal(firstId, replyMsg.GetProperty("replyToMessageId").GetString());

        // 引用不存在的消息 → 404（GroupMessageNotFound）
        var bad = await _client.PostAsJsonAsync("/ag-ui/group/message/send", new { groupId, userId, content = "坏引用", replyToMessageId = "nope", timestamp = 3000L });
        Assert.Equal(HttpStatusCode.NotFound, bad.StatusCode);
    }
}

/// <summary>消息保留策略：DeleteMessagesBefore 按时间清理（内存 store）。</summary>
public sealed class MessageRetentionTests
{
    [Fact]
    public void DeleteMessagesBefore_RemovesOldKeepsNew()
    {
        var store = new Hub.Storage.InMemoryGroupStore();
        store.AddGroup(new Group { GroupId = "g1", GroupName = "g", OwnerId = "u1", MemberCount = 1, CreateTime = 1 });
        store.AddMessage(new GroupMessage
        {
            MessageId = "m_old", GroupId = "g1", ThreadId = "t", TopicId = "main", SenderId = "u1",
            SenderType = MemberType.User, SenderNickname = "u1", Content = "旧消息", Timestamp = 1000,
        });
        store.AddMessage(new GroupMessage
        {
            MessageId = "m_new", GroupId = "g1", ThreadId = "t", TopicId = "main", SenderId = "u1",
            SenderType = MemberType.User, SenderNickname = "u1", Content = "新消息", Timestamp = 2000,
        });

        Assert.Equal(1, store.DeleteMessagesBefore(1500)); // 删除 1000 的那条
        Assert.Single(store.AllMessages("g1"));
        Assert.Equal("m_new", store.AllMessages("g1")[0].MessageId);
    }

    [Fact]
    public void MessageRetentionService_RespectsConfig()
    {
        var store = new Hub.Storage.InMemoryGroupStore();
        store.AddGroup(new Group { GroupId = "g1", GroupName = "g", OwnerId = "u1", MemberCount = 1, CreateTime = 1 });
        store.AddMessage(new GroupMessage
        {
            MessageId = "m_old", GroupId = "g1", ThreadId = "t", TopicId = "main", SenderId = "u1",
            SenderType = MemberType.User, SenderNickname = "u1", Content = "旧", Timestamp = DateTimeOffset.UtcNow.AddDays(-10).ToUnixTimeMilliseconds(),
        });
        store.AddMessage(new GroupMessage
        {
            MessageId = "m_new", GroupId = "g1", ThreadId = "t", TopicId = "main", SenderId = "u1",
            SenderType = MemberType.User, SenderNickname = "u1", Content = "新", Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });

        var options = new Hub.Options.GroupChatOptions { MessageRetentionDays = 7 };
        var svc = new Hub.Persistence.MessageRetentionService(store, options, Microsoft.Extensions.Logging.Abstractions.NullLogger<Hub.Persistence.MessageRetentionService>.Instance);
        Assert.Equal(1, svc.RunOnce()); // 10 天前的被清，今天的保留
        Assert.Single(store.AllMessages("g1"));
        Assert.Equal("m_new", store.AllMessages("g1")[0].MessageId);
    }
}

/// <summary>记录 InvokeAsync 调用的网关替身（验证讨论把计划-执行提示传给工作型智能体）。</summary>
public sealed class RecordingDiscussionGateway : IAgentGateway
{
    public List<AgentInvocationContext> Invoked { get; } = [];
    public Task<AgentInvocationResult> InvokeAsync(AgentInvocationContext context, CancellationToken ct)
    { Invoked.Add(context); return Task.FromResult(new AgentInvocationResult(true, "run_" + Invoked.Count, null)); }
    public Task<bool> IsAvailableAsync(string agentId, CancellationToken ct) => Task.FromResult(true);
    public Task<bool> ResolveInteractionAsync(string interruptId, string memberId, bool approved, string? input, System.Text.Json.JsonElement? payload, CancellationToken ct, bool approveAll = false) => Task.FromResult(true);
    public bool StopRun(string runId, string operatorId, string groupId, bool isManager) => false;
}

/// <summary>讨论→计划执行：夹具用记录网关，验证工作型智能体收到的讨论内容含 plan_write 等计划-执行提示。</summary>
public sealed class DiscussionPlanFixture : IAsyncLifetime
{
    public WebApplication App { get; private set; } = null!;
    public string HttpBase { get; private set; } = null!;
    public RecordingDiscussionGateway Gateway { get; } = new();

    public async Task InitializeAsync()
    {
        var builder = HubApp.CreateBuilder([]);
        builder.Environment.EnvironmentName = "Testing";
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["GroupChat:SeedSampleData"] = "false",
            ["Agents:Provider"] = "mock",
            ["Agents:WorkToolsEnabled"] = "true",
            ["Persistence:Enabled"] = "false",
            ["Auth:RequireTokenOnRealTime"] = "false",
            ["Auth:AdminUserIds"] = "discuss_plan_admin",
        });
        HubApp.ConfigureServices(builder);
        builder.Services.AddAgentFramework(builder.Configuration);
        builder.Services.AddSingleton<IAgentGateway>(Gateway); // 覆盖为记录网关
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));

        App = builder.Build();
        HubApp.MapEndpoints(App);
        App.MapAgentApi();
        await App.StartAsync();
        HttpBase = App.Urls.First();
    }

    public async Task DisposeAsync()
    {
        if (App is not null) await App.DisposeAsync();
    }
}

public sealed class DiscussionPlanTests : IClassFixture<DiscussionPlanFixture>
{
    private readonly DiscussionPlanFixture _fixture;
    private readonly HttpClient _client;

    public DiscussionPlanTests(DiscussionPlanFixture fixture)
    {
        _fixture = fixture;
        _client = new HttpClient { BaseAddress = new Uri(fixture.HttpBase) };
    }

    private async Task<JsonElement> RegisterAsync(string username)
    {
        var res = await _client.PostAsJsonAsync("/ag-ui/user/register", new { username, password = "secret1", nickname = username });
        if (res.StatusCode == HttpStatusCode.Conflict)
        {
            var login = await _client.PostAsJsonAsync("/ag-ui/user/login", new { username, password = "secret1" });
            login.EnsureSuccessStatusCode();
            return await login.Content.ReadFromJsonAsync<JsonElement>();
        }
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static HttpRequestMessage Authed(HttpMethod method, string path, string token)
    {
        var req = new HttpRequestMessage(method, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return req;
    }

    /// <summary>工作型智能体参与讨论：收到的提示转为「计划→执行」（plan_write / plan_mark / publish_file）。</summary>
    [Fact]
    public async Task Discussion_WorkAgent_GetsPlanThenExecutePrompt()
    {
        var user = await RegisterAsync("discuss_plan_user");
        var token = user.GetProperty("token").GetString()!;

        // 工作型智能体 + 普通智能体
        using var createWork = Authed(HttpMethod.Post, "/ag-ui/agents", token);
        createWork.Content = JsonContent.Create(new
        {
            agentId = "agent_dplan_work", nickname = "工作甲", description = "工作",
            instructions = "你是工作型智能体", triggerMode = "mentioned", keywords = new string[0], enableWorkTools = true,
        });
        var cw = await _client.SendAsync(createWork);
        Assert.True(cw.IsSuccessStatusCode, $"创建工作智能体失败: {await cw.Content.ReadAsStringAsync()}");

        using var createNorm = Authed(HttpMethod.Post, "/ag-ui/agents", token);
        createNorm.Content = JsonContent.Create(new
        {
            agentId = "agent_dplan_norm", nickname = "讨论乙", description = "讨论",
            instructions = "你是讨论成员", triggerMode = "mentioned", keywords = new string[0],
        });
        var cn = await _client.SendAsync(createNorm);
        Assert.True(cn.IsSuccessStatusCode, $"创建普通智能体失败: {await cn.Content.ReadAsStringAsync()}");

        var create = await _client.PostAsJsonAsync("/ag-ui/group/create", new
        {
            groupName = "计划讨论群", ownerId = user.GetProperty("userId").GetString(),
            memberIds = new[] { user.GetProperty("userId").GetString(), "agent_dplan_work", "agent_dplan_norm" },
            members = new[]
            {
                new { memberId = "agent_dplan_work", memberType = "agent", nickname = "工作甲" },
                new { memberId = "agent_dplan_norm", memberType = "agent", nickname = "讨论乙" },
            },
        });
        create.EnsureSuccessStatusCode();
        var groupId = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("groupId").GetString()!;

        using var discuss = Authed(HttpMethod.Post, $"/ag-ui/group/{groupId}/discussion", token);
        discuss.Content = JsonContent.Create(new { content = "整理一份权限设计文档", agentIds = new[] { "agent_dplan_work", "agent_dplan_norm" } });
        var res = await _client.SendAsync(discuss);
        res.EnsureSuccessStatusCode();

        // 等待后台讨论触发完成（记录网关同步返回）
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline && _fixture.Gateway.Invoked.Count < 2)
            await Task.Delay(50);

        Assert.Equal(2, _fixture.Gateway.Invoked.Count);
        // 工作型智能体 → 计划-执行提示
        var work = _fixture.Gateway.Invoked.First(c => c.AgentId == "agent_dplan_work");
        Assert.Contains("plan_write", work.Content);
        Assert.Contains("plan_mark", work.Content);
        Assert.Contains("publish_file", work.Content);
        // 普通智能体 → 普通讨论提示
        var norm = _fixture.Gateway.Invoked.First(c => c.AgentId == "agent_dplan_norm");
        Assert.Contains("群讨论", norm.Content);
        Assert.DoesNotContain("plan_write", norm.Content);
    }
}
