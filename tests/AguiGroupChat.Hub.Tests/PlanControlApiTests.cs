using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AguiGroupChat.Agents;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>编排计划「暂停 / 继续」HTTP 端点集成测试（复用 AgentApiServerFixture）。</summary>
public sealed class PlanControlApiTests : IClassFixture<AgentApiServerFixture>
{
    private readonly AgentApiServerFixture _fixture;
    private readonly HttpClient _client;

    public PlanControlApiTests(AgentApiServerFixture fixture)
    {
        _fixture = fixture;
        _client = new HttpClient { BaseAddress = new Uri(fixture.HttpBase) };
    }

    [Fact]
    public async Task Pause_WithoutToken_Returns401()
    {
        var res = await _client.PostAsJsonAsync("/ag-ui/plan/pause", new { groupId = "g", messageId = "m" });
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task PauseResume_ActiveGate_FlipsState()
    {
        var (token, userId) = await RegisterUserAsync("plan_" + Guid.NewGuid().ToString("N")[..6]);
        var (agentId, groupId) = await CreateDirectChatAsync(token, userId);

        var controls = _fixture.App.Services.GetRequiredService<CoordinatedPlanControlStore>();
        var gate = controls.Begin("msg_plan_1", groupId, agentId, userId);
        Assert.NotNull(gate);

        // 触发者暂停 → 闸门置为暂停
        var pause = await PostControlAsync(token, "pause", groupId, "msg_plan_1");
        pause.EnsureSuccessStatusCode();
        Assert.True(gate!.IsPaused);
        // 重复暂停幂等
        (await PostControlAsync(token, "pause", groupId, "msg_plan_1")).EnsureSuccessStatusCode();
        Assert.True(gate.IsPaused);

        // 继续 → 闸门恢复运行
        var resume = await PostControlAsync(token, "resume", groupId, "msg_plan_1");
        resume.EnsureSuccessStatusCode();
        Assert.False(gate.IsPaused);

        // 计划结束后闸门移除 → 后续控制 404
        controls.End("msg_plan_1");
        Assert.Equal(HttpStatusCode.NotFound,
            (await PostControlAsync(token, "resume", groupId, "msg_plan_1")).StatusCode);
    }

    [Fact]
    public async Task Pause_NoActivePlan_Returns404()
    {
        var (token, userId) = await RegisterUserAsync("plan_" + Guid.NewGuid().ToString("N")[..6]);
        var (_, groupId) = await CreateDirectChatAsync(token, userId);
        var res = await PostControlAsync(token, "pause", groupId, "msg_nonexistent");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task Pause_NotGroupMember_Returns403()
    {
        var (tokenOwner, userIdOwner) = await RegisterUserAsync("plan_" + Guid.NewGuid().ToString("N")[..6]);
        var (_, groupId) = await CreateDirectChatAsync(tokenOwner, userIdOwner);
        var (tokenOther, _) = await RegisterUserAsync("plan_" + Guid.NewGuid().ToString("N")[..6]);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await PostControlAsync(tokenOther, "pause", groupId, "msg_x")).StatusCode);
    }

    [Fact]
    public async Task Pause_TriggerOnlyButOwnerAdminAllowed()
    {
        // 群主（非触发者）也可以暂停：验证 Owner 放行
        var (token, userId) = await RegisterUserAsync("plan_" + Guid.NewGuid().ToString("N")[..6]);
        var (_, groupId) = await CreateDirectChatAsync(token, userId);
        var controls = _fixture.App.Services.GetRequiredService<CoordinatedPlanControlStore>();
        var gate = controls.Begin("msg_owner_pause", groupId, "agent", "other_trigger_user");
        Assert.NotNull(gate);
        // 群主本人请求暂停（owner 在单聊群里）→ 放行
        (await PostControlAsync(token, "pause", groupId, "msg_owner_pause")).EnsureSuccessStatusCode();
        Assert.True(gate!.IsPaused);
        controls.End("msg_owner_pause");
    }

    // ================= 辅助 =================

    private async Task<(string Token, string UserId)> RegisterUserAsync(string username)
    {
        var res = await _client.PostAsJsonAsync("/ag-ui/user/register", new { username, password = "secret1", nickname = username });
        res.EnsureSuccessStatusCode();
        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        return (json.GetProperty("token").GetString()!, json.GetProperty("userId").GetString()!);
    }

    private async Task<HttpResponseMessage> PostControlAsync(string token, string action, string groupId, string messageId)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"/ag-ui/plan/{action}")
        {
            Content = JsonContent.Create(new { groupId, messageId }),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _client.SendAsync(req);
    }

    /// <summary>注册数字员工并开启 1:1 单聊 → 返回 (agentId, groupId)。群主即该用户。</summary>
    private async Task<(string AgentId, string GroupId)> CreateDirectChatAsync(string token, string ownerId)
    {
        var agentId = "plan_agent_" + Guid.NewGuid().ToString("N")[..6];
        using var createAgent = new HttpRequestMessage(HttpMethod.Post, "/ag-ui/agents")
        {
            Content = JsonContent.Create(new { agentId, nickname = "计划助手", instructions = "你是计划助手", triggerMode = "mentioned" }),
        };
        createAgent.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        (await _client.SendAsync(createAgent)).EnsureSuccessStatusCode();

        using var start = new HttpRequestMessage(HttpMethod.Post, "/ag-ui/agents/direct")
        {
            Content = JsonContent.Create(new { agentId }),
        };
        start.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var startRes = await _client.SendAsync(start);
        startRes.EnsureSuccessStatusCode();
        var groupId = (await startRes.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("groupId").GetString()!;
        return (agentId, groupId);
    }
}
