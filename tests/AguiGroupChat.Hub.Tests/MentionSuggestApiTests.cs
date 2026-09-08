using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>输入时「建议 @ 谁」HTTP 端点集成测试（复用 AgentApiServerFixture：真实 Kestrel + mock 网关）。</summary>
public sealed class MentionSuggestApiTests : IClassFixture<AgentApiServerFixture>
{
    private readonly AgentApiServerFixture _fixture;
    private readonly HttpClient _client;

    public MentionSuggestApiTests(AgentApiServerFixture fixture)
    {
        _fixture = fixture;
        _client = new HttpClient { BaseAddress = new Uri(fixture.HttpBase) };
    }

    [Fact]
    public async Task Suggest_WithoutToken_Returns401()
    {
        var res = await _client.PostAsJsonAsync("/ag-ui/mention-suggest", new { groupId = "g", text = "帮我看看磁盘" });
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Suggest_SharedGroup_ReturnsMatchingAgent()
    {
        var (token, userId) = await RegisterUserAsync("ms_" + Guid.NewGuid().ToString("N")[..6]);
        var diskId = "ms_disk_" + Guid.NewGuid().ToString("N")[..6];
        var netId = "ms_net_" + Guid.NewGuid().ToString("N")[..6];
        await PostAgentAsync(token, new
        {
            agentId = diskId,
            nickname = "磁盘专员",
            description = "负责服务器磁盘空间清理与性能优化",
            instructions = "你是磁盘专员",
            triggerMode = "mentioned",
            keywords = new[] { "磁盘" },
        });
        await PostAgentAsync(token, new
        {
            agentId = netId,
            nickname = "网络专员",
            description = "负责网络故障排查与专线维护",
            instructions = "你是网络专员",
            triggerMode = "mentioned",
            keywords = new[] { "网络" },
        });
        // 共享群：两位数字员工 + 群主（至少两名数字员工 → 非 1:1 单聊，才应提示）
        var groupId = await CreateGroupAsync(token, userId, "磁盘小组",
            (diskId, "磁盘专员"), (netId, "网络专员"));

        using var req = new HttpRequestMessage(HttpMethod.Post, "/ag-ui/mention-suggest")
        {
            Content = JsonContent.Create(new { groupId, text = "帮我看看磁盘空间是不是满了" }),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var res = await _client.SendAsync(req);
        res.EnsureSuccessStatusCode();
        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        var suggestions = json.GetProperty("suggestions").EnumerateArray().ToList();
        var sug = Assert.Single(suggestions); // 仅磁盘文本 → 只推荐磁盘专员
        Assert.Equal(diskId, sug.GetProperty("agentId").GetString());
        Assert.Equal("磁盘专员", sug.GetProperty("nickname").GetString());
    }

    [Fact]
    public async Task Suggest_NotGroupMember_Returns403()
    {
        var (token, _) = await RegisterUserAsync("ms_" + Guid.NewGuid().ToString("N")[..6]);
        // 别人建的群：本用户不是成员
        var (tokenOwner, userIdOwner) = await RegisterUserAsync("ms_" + Guid.NewGuid().ToString("N")[..6]);
        var netOther = "ms_net_" + Guid.NewGuid().ToString("N")[..6];
        await PostAgentAsync(tokenOwner, new
        {
            agentId = netOther,
            nickname = "网络专员",
            instructions = "你是网络专员",
            triggerMode = "mentioned",
            keywords = new[] { "网络" },
        });
        var groupId = await CreateGroupAsync(tokenOwner, userIdOwner, "别人的群", (netOther, "网络专员"));

        using var req = new HttpRequestMessage(HttpMethod.Post, "/ag-ui/mention-suggest")
        {
            Content = JsonContent.Create(new { groupId, text = "网络出问题了帮我查一下" }),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(req)).StatusCode);
    }

    [Fact]
    public async Task Suggest_OneToOneDirectWithAgent_ReturnsEmpty()
    {
        var (token, _) = await RegisterUserAsync("ms_" + Guid.NewGuid().ToString("N")[..6]);
        var agentId = "ms_direct_" + Guid.NewGuid().ToString("N")[..6];
        await PostAgentAsync(token, new
        {
            agentId,
            nickname = "单聊助手",
            instructions = "你是单聊助手",
            triggerMode = "mentioned",
            keywords = new[] { "磁盘" },
        });

        // 与唯一数字员工的 1:1 单聊：对象已明确，不提示
        using var start = new HttpRequestMessage(HttpMethod.Post, "/ag-ui/agents/direct")
        {
            Content = JsonContent.Create(new { agentId }),
        };
        start.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var startRes = await _client.SendAsync(start);
        startRes.EnsureSuccessStatusCode();
        var groupId = (await startRes.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("groupId").GetString()!;

        using var req = new HttpRequestMessage(HttpMethod.Post, "/ag-ui/mention-suggest")
        {
            Content = JsonContent.Create(new { groupId, text = "帮我看看磁盘空间是不是满了" }),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var res = await _client.SendAsync(req);
        res.EnsureSuccessStatusCode();
        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Empty(json.GetProperty("suggestions").EnumerateArray());
        Assert.Equal("direct", json.GetProperty("reason").GetString());
    }

    // ================= 辅助（与 AgentApiIntegrationTests 一致的最小化写法） =================

    private async Task<(string Token, string UserId)> RegisterUserAsync(string username)
    {
        var res = await _client.PostAsJsonAsync("/ag-ui/user/register", new { username, password = "secret1", nickname = username });
        res.EnsureSuccessStatusCode();
        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        return (json.GetProperty("token").GetString()!, json.GetProperty("userId").GetString()!);
    }

    private async Task PostAgentAsync(string token, object body)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/ag-ui/agents") { Content = JsonContent.Create(body) };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        (await _client.SendAsync(req)).EnsureSuccessStatusCode();
    }

    private async Task<string> CreateGroupAsync(string token, string ownerId, string groupName, params (string AgentId, string Nickname)[] agents)
    {
        var create = await _client.PostAsJsonAsync("/ag-ui/group/create", new
        {
            groupName,
            ownerId,
            memberIds = agents.Select(a => a.AgentId).ToArray(),
            members = agents.Select(a => new { memberId = a.AgentId, memberType = "agent", nickname = a.Nickname }).ToArray(),
        });
        create.EnsureSuccessStatusCode();
        return (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("groupId").GetString()!;
    }
}
