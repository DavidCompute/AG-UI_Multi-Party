using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AguiGroupChat.Agents;
using AguiGroupChat.Hub.Agents;
using AguiGroupChat.Hub.Messaging;
using AguiGroupChat.Hub.Models;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>AI 分身：基于公开群发言生成人设，加入公开群按设定触发方式回复；停用即移除。</summary>
public sealed class TwinApiTests : IClassFixture<AgentApiServerFixture>
{
    private readonly AgentApiServerFixture _fixture;
    private readonly HttpClient _client;

    public TwinApiTests(AgentApiServerFixture fixture)
    {
        _fixture = fixture;
        _client = new HttpClient { BaseAddress = new Uri(fixture.HttpBase) };
    }

    private async Task<(string Token, string UserId)> RegisterAsync(string username)
    {
        var res = await _client.PostAsJsonAsync("/ag-ui/user/register", new { username, password = "secret1", nickname = username });
        res.EnsureSuccessStatusCode();
        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        return (json.GetProperty("token").GetString()!, json.GetProperty("userId").GetString()!);
    }

    [Fact]
    public async Task Enable_JoinsPublicGroupsOnly_Disable_RemovesAll()
    {
        var (token, userId) = await RegisterAsync("twin_alice");
        var twinId = TwinService.AgentIdOf(userId);

        // 建公开群 + 私密群，alice 均为成员；公开群发几条发言作为人设语料
        var pub = await _client.PostAsJsonAsync("/ag-ui/group/create", new { groupName = "公开群", ownerId = userId });
        pub.EnsureSuccessStatusCode();
        var pubId = (await pub.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("groupId").GetString()!;

        var priv = await _client.PostAsJsonAsync("/ag-ui/group/create", new { groupName = "私密群", ownerId = userId, isPrivate = true });
        priv.EnsureSuccessStatusCode();
        var privId = (await priv.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("groupId").GetString()!;

        await _client.PostAsJsonAsync("/ag-ui/group/message/send", new { groupId = pubId, userId, content = "我偏好简洁的方案" });
        await _client.PostAsJsonAsync("/ag-ui/group/message/send", new { groupId = pubId, userId, content = "先做 MVP 再迭代" });

        // 启用分身（语境触发）
        using var enableReq = new HttpRequestMessage(HttpMethod.Post, "/ag-ui/twin/enable")
        {
            Content = JsonContent.Create(new { triggerMode = "contextual" }),
        };
        enableReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var enable = await _client.SendAsync(enableReq);
        enable.EnsureSuccessStatusCode();
        var status = await enable.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(status.GetProperty("enabled").GetBoolean());
        Assert.Equal(twinId, status.GetProperty("twinAgentId").GetString());

        // 分身：加入公开群（成员 + 触发规则）、不进私密群、目录对 alice 可见
        var hub = _fixture.App.Services.GetRequiredService<GroupHub>();
        Assert.True(hub.Store.IsMember(pubId, twinId));
        Assert.False(hub.Store.IsMember(privId, twinId), "分身不应加入私密群");
        Assert.NotNull(hub.Store.GetMember(pubId, twinId));
        Assert.NotNull(_fixture.App.Services.GetRequiredService<AgentRegistry>().ForGroupAgent(pubId, twinId));
        Assert.NotNull(_fixture.App.Services.GetRequiredService<AgentCatalog>().GetDefinition(twinId));
        Assert.True(_fixture.App.Services.GetRequiredService<AgentCatalog>().GetDefinition(twinId)!.IsPrivate, "分身应为私密智能体");

        // GET 状态
        using var get = new HttpRequestMessage(HttpMethod.Get, "/ag-ui/twin");
        get.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var getResp = await _client.SendAsync(get);
        Assert.True(getResp.IsSuccessStatusCode, $"GET /ag-ui/twin 状态 {getResp.StatusCode}：{await getResp.Content.ReadAsStringAsync()}");
        var got = await getResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(got.GetProperty("enabled").GetBoolean());

        // 分身可在公开群按规则被触发（目录存在 → 触发网关可用）
        var gateway = _fixture.App.Services.GetRequiredService<IAgentGateway>();
        Assert.True(await gateway.IsAvailableAsync(twinId, CancellationToken.None));

        // 停用分身：退出全部群 + 目录删除 + 触发规则注销
        using var disableReq = new HttpRequestMessage(HttpMethod.Post, "/ag-ui/twin/disable");
        disableReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var disable = await _client.SendAsync(disableReq);
        disable.EnsureSuccessStatusCode();
        var disabled = await disable.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(disabled.GetProperty("disabled").GetBoolean());

        Assert.False(hub.Store.IsMember(pubId, twinId));
        Assert.False(hub.Store.IsMember(privId, twinId));
        Assert.Null(_fixture.App.Services.GetRequiredService<AgentCatalog>().GetDefinition(twinId));
        Assert.Null(_fixture.App.Services.GetRequiredService<AgentRegistry>().ForGroupAgent(pubId, twinId));
    }

    [Fact]
    public async Task UpdateTrigger_PersistsAndSyncsGroupRegistration()
    {
        var (token, userId) = await RegisterAsync("twin_carol");
        var twinId = TwinService.AgentIdOf(userId);

        var pub = await _client.PostAsJsonAsync("/ag-ui/group/create", new { groupName = "公开群2", ownerId = userId });
        pub.EnsureSuccessStatusCode();
        var pubId = (await pub.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("groupId").GetString()!;

        // 启用（语境触发）
        using var enableReq = new HttpRequestMessage(HttpMethod.Post, "/ag-ui/twin/enable")
        { Content = JsonContent.Create(new { triggerMode = "contextual" }) };
        enableReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        (await _client.SendAsync(enableReq)).EnsureSuccessStatusCode();

        // 修改触发方式 → keyword
        using var trigReq = new HttpRequestMessage(HttpMethod.Post, "/ag-ui/twin/trigger")
        { Content = JsonContent.Create(new { triggerMode = "keyword" }) };
        trigReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var trig = await _client.SendAsync(trigReq);
        trig.EnsureSuccessStatusCode();
        var trigJson = await trig.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("keyword", trigJson.GetProperty("triggerMode").GetString()); // 小写，前端 select 可直接回显

        // 群注册同步为新触发方式
        var reg = _fixture.App.Services.GetRequiredService<AgentRegistry>().ForGroupAgent(pubId, twinId);
        Assert.NotNull(reg);
        Assert.Equal(AgentTriggerMode.Keyword, reg!.TriggerMode);

        // 重新查看状态：triggerMode 仍为小写 keyword（修复「修改后再查看空白」）
        using var getReq = new HttpRequestMessage(HttpMethod.Get, "/ag-ui/twin");
        getReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var got = await (await _client.SendAsync(getReq)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("keyword", got.GetProperty("triggerMode").GetString());

        // 清理
        using var disableReq = new HttpRequestMessage(HttpMethod.Post, "/ag-ui/twin/disable");
        disableReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        (await _client.SendAsync(disableReq)).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Twin_FollowsUser_IntoNewPublicGroupsOnly()
    {
        var (token, userId) = await RegisterAsync("twin_dave");
        var twinId = TwinService.AgentIdOf(userId);

        // 先启用分身（此时尚无群）
        using var enableReq = new HttpRequestMessage(HttpMethod.Post, "/ag-ui/twin/enable")
        { Content = JsonContent.Create(new { triggerMode = "mentioned" }) };
        enableReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        (await _client.SendAsync(enableReq)).EnsureSuccessStatusCode();

        // 启用后新建公开群 → 分身自动加入；新建私密群 → 分身不加入
        var pub = await _client.PostAsJsonAsync("/ag-ui/group/create", new { groupName = "新公开群", ownerId = userId });
        pub.EnsureSuccessStatusCode();
        var pubId = (await pub.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("groupId").GetString()!;

        var priv = await _client.PostAsJsonAsync("/ag-ui/group/create", new { groupName = "新私密群", ownerId = userId, isPrivate = true });
        priv.EnsureSuccessStatusCode();
        var privId = (await priv.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("groupId").GetString()!;

        var hub = _fixture.App.Services.GetRequiredService<GroupHub>();
        Assert.True(hub.Store.IsMember(pubId, twinId), "分身应自动加入启用后新建的公开群");
        Assert.False(hub.Store.IsMember(privId, twinId), "分身不应加入私密群");

        // 其他用户加入公开群（其无分身）不影响
        var (otherToken, otherId) = await RegisterAsync("twin_eve");
        var add = await _client.PostAsJsonAsync("/ag-ui/group/member/add", new { groupId = pubId, memberIds = new[] { otherId }, operatorId = userId });
        add.EnsureSuccessStatusCode();

        // 清理
        using var disableReq = new HttpRequestMessage(HttpMethod.Post, "/ag-ui/twin/disable");
        disableReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        (await _client.SendAsync(disableReq)).EnsureSuccessStatusCode();
        Assert.False(hub.Store.IsMember(pubId, twinId));
    }

    [Fact]
    public async Task Disable_WithoutTwin_ReturnsFalse_AndEndpointsRequireAuth()
    {
        // 未登录 → 401
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync("/ag-ui/twin")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.PostAsync("/ag-ui/twin/disable", null)).StatusCode);

        // 已登录但未启用 → disabled=false
        var (token, _) = await RegisterAsync("twin_bob");
        using var req = new HttpRequestMessage(HttpMethod.Post, "/ag-ui/twin/disable");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var res = await _client.SendAsync(req);
        res.EnsureSuccessStatusCode();
        Assert.False((await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("disabled").GetBoolean());
    }
}
