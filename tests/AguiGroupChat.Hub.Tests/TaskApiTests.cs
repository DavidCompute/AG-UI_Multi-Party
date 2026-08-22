using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AguiGroupChat.Agents;
using AguiGroupChat.Hub.Transport;
using AguiGroupChat.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>工作任务编排 API 集成测试夹具：Hub + WSAGENT 网关(mock) + 任务编排端点。</summary>
public sealed class TaskApiServerFixture : IAsyncLifetime
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
            ["Agents:WorkToolsEnabled"] = "true",
            ["Persistence:Enabled"] = "false",
            ["Auth:RequireTokenOnRealTime"] = "false",
            ["Auth:AdminUserIds"] = "admin_task",
        });
        HubApp.ConfigureServices(builder);
        builder.Services.AddAgentFramework(builder.Configuration);
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));

        App = builder.Build();
        HubApp.MapEndpoints(App);
        App.MapAgentApi();
        App.MapTaskApi();
        App.MapAttachmentApi();
        await App.StartAsync();
        HttpBase = App.Urls.First();
    }

    public async Task DisposeAsync()
    {
        if (App is not null) await App.DisposeAsync();
    }
}

public sealed class TaskApiTests : IClassFixture<TaskApiServerFixture>
{
    private readonly TaskApiServerFixture _fixture;
    private readonly HttpClient _client;

    public TaskApiTests(TaskApiServerFixture fixture)
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

    private static HttpRequestMessage AuthMessage(HttpMethod method, string url, string token, object? body = null)
    {
        var msg = new HttpRequestMessage(method, url);
        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) msg.Content = JsonContent.Create(body);
        return msg;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, string token, object? body = null)
        => await _client.SendAsync(AuthMessage(method, url, token, body));

    private async Task<HttpResponseMessage> PostAgentAsync(string token, object body)
        => await SendAsync(HttpMethod.Post, "/ag-ui/agents", token, body);

    /// <summary>以 token 用户身份建群并加入一个智能体（用户自动成为群主/成员）。</summary>
    private async Task<string> CreateGroupWithAgentAsync(string token, string agentId, string nickname, string groupName)
    {
        var create = await SendAsync(HttpMethod.Post, "/ag-ui/group/create", token, new
        {
            groupName,
            ownerId = "",
            memberIds = new[] { agentId },
            members = new[] { new { memberId = agentId, memberType = "agent", nickname } },
        });
        create.EnsureSuccessStatusCode();
        return (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("groupId").GetString()!;
    }

    [Fact]
    public async Task CreateTask_WithoutToken_Returns401()
    {
        var res = await _client.PostAsJsonAsync("/ag-ui/tasks", new { groupId = "g", agentId = "a", content = "整理文档" });
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task CreateTask_ThenListAndGet_ShowsTask()
    {
        var token = await RegisterAsync("taskuser" + Guid.NewGuid().ToString("N")[..6]);
        (await PostAgentAsync(token, new { agentId = "agent_task", nickname = "工作助手", triggerMode = "mentioned", keywords = (string[]?)null })).EnsureSuccessStatusCode();
        var groupId = await CreateGroupWithAgentAsync(token, "agent_task", "工作助手", "任务群");

        // 创建任务
        var created = await SendAsync(HttpMethod.Post, "/ag-ui/tasks/", token,
            new { groupId, agentId = "agent_task", content = "抓取文档并整理成报告", title = "整理报告" });
        created.EnsureSuccessStatusCode();
        var json = await created.Content.ReadFromJsonAsync<JsonElement>();
        var taskId = json.GetProperty("taskId").GetString()!;
        Assert.StartsWith("task_", taskId);
        // 后台任务可能已立即被 mock 网关推进（queue→running→finished），断言状态为合法枚举值即可
        var status = json.GetProperty("status").GetString();
        Assert.Contains(status, new[] { "queue", "running", "finished", "failed", "cancelled" });

        // 我的任务列表
        var list = await SendAsync(HttpMethod.Get, "/ag-ui/tasks", token);
        list.EnsureSuccessStatusCode();
        var tasks = await list.Content.ReadFromJsonAsync<JsonElement[]>();
        Assert.Contains(tasks!, x => x.GetProperty("taskId").GetString() == taskId);

        // 群任务列表
        var group = await SendAsync(HttpMethod.Get, $"/ag-ui/tasks/{groupId}/group", token);
        group.EnsureSuccessStatusCode();
        var groupTasks = await group.Content.ReadFromJsonAsync<JsonElement[]>();
        Assert.Contains(groupTasks!, x => x.GetProperty("taskId").GetString() == taskId);

        // 任务详情
        var detail = await SendAsync(HttpMethod.Get, $"/ag-ui/tasks/{taskId}", token);
        detail.EnsureSuccessStatusCode();
        var detailJson = await detail.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("整理报告", detailJson.GetProperty("title").GetString());
    }

    [Fact]
    public async Task CreateTask_NonMember_Returns403()
    {
        var ownerToken = await RegisterAsync("taskowner" + Guid.NewGuid().ToString("N")[..6]);
        (await PostAgentAsync(ownerToken, new { agentId = "agent_task2", nickname = "助手", triggerMode = "mentioned", keywords = (string[]?)null })).EnsureSuccessStatusCode();
        // 拥有者建群
        var groupId = await CreateGroupWithAgentAsync(ownerToken, "agent_task2", "助手", "内群");

        // 另一用户（非成员）试图建任务 → 403
        var outsider = await RegisterAsync("taskoutsider" + Guid.NewGuid().ToString("N")[..6]);
        var res = await SendAsync(HttpMethod.Post, "/ag-ui/tasks/", outsider,
            new { groupId, agentId = "agent_task2", content = "x" });
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task CreateTask_AgentNotInGroup_Returns400()
    {
        var token = await RegisterAsync("taskbad" + Guid.NewGuid().ToString("N")[..6]);
        (await PostAgentAsync(token, new { agentId = "agent_task3", nickname = "助手", triggerMode = "mentioned", keywords = (string[]?)null })).EnsureSuccessStatusCode();
        // 建群但不加入 agent_task3
        var create = await SendAsync(HttpMethod.Post, "/ag-ui/group/create", token, new { groupName = "无智能体群", ownerId = "" });
        create.EnsureSuccessStatusCode();
        var groupId = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("groupId").GetString()!;

        // 指定不在群内的智能体 → 400
        var res = await SendAsync(HttpMethod.Post, "/ag-ui/tasks/", token,
            new { groupId, agentId = "agent_task3", content = "x" });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task GetTask_ByNonMember_Returns403()
    {
        var ownerToken = await RegisterAsync("taskowner2" + Guid.NewGuid().ToString("N")[..6]);
        (await PostAgentAsync(ownerToken, new { agentId = "agent_task4", nickname = "助手", triggerMode = "mentioned", keywords = (string[]?)null })).EnsureSuccessStatusCode();
        var groupId = await CreateGroupWithAgentAsync(ownerToken, "agent_task4", "助手", "任务群2");

        var created = await SendAsync(HttpMethod.Post, "/ag-ui/tasks/", ownerToken,
            new { groupId, agentId = "agent_task4", content = "处理数据" });
        created.EnsureSuccessStatusCode();
        var taskId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("taskId").GetString()!;

        // 另一用户（非群成员）查看详情 → 403
        var outsider = await RegisterAsync("taskstranger" + Guid.NewGuid().ToString("N")[..6]);
        var res = await SendAsync(HttpMethod.Get, $"/ag-ui/tasks/{taskId}", outsider);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }
}
