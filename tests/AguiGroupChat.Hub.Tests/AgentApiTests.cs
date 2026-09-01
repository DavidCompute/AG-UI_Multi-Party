using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AguiGroupChat.Agents;
using AguiGroupChat.Hub.Agents;
using AguiGroupChat.Hub.Models;
using AguiGroupChat.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

public sealed class AgentCatalogDynamicTests
{
    private static AgentCatalog CreateCatalog(AgentOptions? options = null)
    {
        options ??= new AgentOptions { Provider = "mock" };
        return new AgentCatalog(options, NullLoggerFactory.Instance, new ServiceCollection().BuildServiceProvider());
    }

    [Fact]
    public void Catalog_SeededFromOptions_AtConstruction()
    {
        var catalog = CreateCatalog(new AgentOptions
        {
            Provider = "mock",
            Agents = { new AgentDefinition { AgentId = "agent_a", Nickname = "助手A" } },
        });

        Assert.NotNull(catalog.GetDefinition("agent_a"));
        Assert.Equal("助手A", catalog.GetDefinition("agent_a")!.Nickname);
        Assert.Single(catalog.ListDefinitions());
    }

    [Fact]
    public void Catalog_Upsert_AddsNewAgent_AndReplacesDefinition()
    {
        var catalog = CreateCatalog();

        catalog.Upsert(new AgentDefinition { AgentId = "agent_b", Nickname = "旧名", Instructions = "v1" });
        Assert.Equal("旧名", catalog.GetDefinition("agent_b")!.Nickname);

        catalog.Upsert(new AgentDefinition { AgentId = "agent_b", Nickname = "新名", Instructions = "v2" });
        Assert.Equal("新名", catalog.GetDefinition("agent_b")!.Nickname);
        Assert.Equal("v2", catalog.GetDefinition("agent_b")!.Instructions);
        Assert.Single(catalog.ListDefinitions());
    }

    [Fact]
    public void Catalog_Remove_DeletesAgent()
    {
        var catalog = CreateCatalog();
        catalog.Upsert(new AgentDefinition { AgentId = "agent_c", Nickname = "C" });

        Assert.True(catalog.Remove("agent_c"));
        Assert.Null(catalog.GetDefinition("agent_c"));
        Assert.False(catalog.Remove("agent_c"));
    }
}

/// <summary>自托管 Kestrel 集成测试夹具：Hub + MSAGENT 网关（mock）+ 智能体管理 API。</summary>
public sealed class AgentApiServerFixture : IAsyncLifetime
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
            // 建群 / 加成员等协议写接口测试走请求体身份回退（默认已改为强制令牌，这里显式关闭）
            ["Auth:RequireTokenOnRealTime"] = "false",
            // 桥接端点仅系统管理员可配置：固定 heidi / grace 为管理员（首个注册用户在其他测试先注册时不一定是管理员）
            ["Auth:AdminUserIds"] = "heidi,grace",
        });
        HubApp.ConfigureServices(builder);
        builder.Services.AddAgentFramework(builder.Configuration);
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));

        App = builder.Build();
        HubApp.MapEndpoints(App);
        App.MapAgentApi();
        App.MapTwinApi(); // AI 分身 API
        App.MapKnowledgeBaseApi(); // 知识库 API
        App.MapAttachmentApi(); // 附件上传 / 下载（头像等）
        App.MapGroupNameApi(); // 群名自动生成
        await App.StartAsync();
        HttpBase = App.Urls.First();
    }

    public async Task DisposeAsync()
    {
        if (App is not null) await App.DisposeAsync();
    }
}

public sealed class AgentApiIntegrationTests : IClassFixture<AgentApiServerFixture>
{
    private readonly AgentApiServerFixture _fixture;
    private readonly HttpClient _client;

    public AgentApiIntegrationTests(AgentApiServerFixture fixture)
    {
        _fixture = fixture;
        _client = new HttpClient { BaseAddress = new Uri(fixture.HttpBase) };
    }

    [Fact]
    public async Task Create_WithoutToken_Returns401()
    {
        var res = await _client.PostAsJsonAsync("/ag-ui/agents", new { nickname = "助手", triggerMode = "mentioned" });
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Create_ThenList_ShowsAgent()
    {
        var token = await RegisterAsync("alice");

        var created = await PostAgentAsync(token, new
        {
            agentId = "agent_qa",
            nickname = "QA 助手",
            description = "测试助手",
            instructions = "你是 QA 助手",
            triggerMode = "keyword",
            keywords = new[] { "bug", "测试" },
            model = (string?)null,
        });
        created.EnsureSuccessStatusCode();
        Assert.Equal("agent_qa", (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("agentId").GetString());

        var list = await _client.GetFromJsonAsync<JsonElement[]>("/ag-ui/agents") ?? [];
        var agent = Assert.Single(list, x => x.GetProperty("agentId").GetString() == "agent_qa");
        Assert.Equal("QA 助手", agent.GetProperty("nickname").GetString());
        Assert.Equal("keyword", agent.GetProperty("triggerMode").GetString());
        Assert.Equal("bug", agent.GetProperty("keywords")[0].GetString());
    }

    [Fact]
    public async Task Create_AutoAgentId_AndDuplicateIdConflict()
    {
        var token = await RegisterAsync("bob");

        // 不传 agentId → 自动生成 agent_xxx
        var auto = await PostAgentAsync(token, new { agentId = (string?)null, nickname = "自动ID", triggerMode = "mentioned" });
        auto.EnsureSuccessStatusCode();
        var autoJson = await auto.Content.ReadFromJsonAsync<JsonElement>();
        Assert.StartsWith("agent_", autoJson.GetProperty("agentId").GetString());

        // 重复 ID → 409 AGENT_EXISTS
        var dup = await PostAgentAsync(token, new { agentId = "agent_dup", nickname = "重复", triggerMode = "mentioned" });
        dup.EnsureSuccessStatusCode();
        var dup2 = await PostAgentAsync(token, new { agentId = "agent_dup", nickname = "重复2", triggerMode = "mentioned" });
        Assert.Equal(HttpStatusCode.Conflict, dup2.StatusCode);
    }

    [Fact]
    public async Task Update_ChangesDefinition()
    {
        var token = await RegisterAsync("carol");
        await PostAgentAsync(token, new { agentId = "agent_ops", nickname = "运维", triggerMode = "mentioned", keywords = (string[]?)null });
        await PostAgentAsync(token, new { agentId = "agent_prd", nickname = "需求", triggerMode = "mentioned", keywords = (string[]?)null });

        // 更新 agent_ops
        using var req = new HttpRequestMessage(HttpMethod.Put, "/ag-ui/agents/agent_ops")
        {
            Content = JsonContent.Create(new
            {
                agentId = "agent_ops",
                nickname = "运维助手",
                description = "运维专家",
                instructions = "你是运维助手",
                triggerMode = "contextual",
                keywords = (string[]?)null,
                model = "deepseek-chat",
            }),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        (await _client.SendAsync(req)).EnsureSuccessStatusCode();

        var list = await _client.GetFromJsonAsync<JsonElement[]>("/ag-ui/agents") ?? [];
        var updated = Assert.Single(list, x => x.GetProperty("agentId").GetString() == "agent_ops");
        Assert.Equal("运维助手", updated.GetProperty("nickname").GetString());
        Assert.Equal("contextual", updated.GetProperty("triggerMode").GetString());
        Assert.Equal("deepseek-chat", updated.GetProperty("model").GetString());
    }

    [Fact]
    public async Task Create_BridgeAgent_ListShowsEndpointWithoutToken()
    {
        var token = await RegisterAsync("grace");

        var created = await PostAgentAsync(token, new
        {
            agentId = "agent_ext",
            nickname = "外部专家",
            triggerMode = "mentioned",
            bridgeEndpoint = "ws://agui-external:8080/ws",
            bridgeMode = "hub",
            bridgeToken = "secret-token",
        });
        created.EnsureSuccessStatusCode();

        var list = await _client.GetFromJsonAsync<JsonElement[]>("/ag-ui/agents") ?? [];
        var agent = Assert.Single(list, x => x.GetProperty("agentId").GetString() == "agent_ext");
        Assert.Equal("ws://agui-external:8080/ws", agent.GetProperty("bridgeEndpoint").GetString());
        // 令牌不回显（公开只读目录）
        Assert.False(agent.TryGetProperty("bridgeToken", out _), "bridgeToken 不应出现在列表响应中");
    }

    [Fact]
    public async Task Update_BridgeAgent_EndpointChanges_TokenBlankKeepsExisting()
    {
        var token = await RegisterAsync("heidi");
        await PostAgentAsync(token, new
        {
            agentId = "agent_ext2",
            nickname = "外部专家",
            triggerMode = "mentioned",
            bridgeEndpoint = "ws://old:8080/ws",
            bridgeMode = "standard",
            bridgeToken = "keep-me",
        });

        // 编辑：只改端点，令牌留空（null）→ 应沿用原令牌，端点更新
        using var req = new HttpRequestMessage(HttpMethod.Put, "/ag-ui/agents/agent_ext2")
        {
            Content = JsonContent.Create(new
            {
                agentId = "agent_ext2",
                nickname = "外部专家v2",
                triggerMode = "mentioned",
                bridgeEndpoint = "ws://new:8080/ws",
                bridgeMode = (string?)null,
                bridgeToken = (string?)null,
            }),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        (await _client.SendAsync(req)).EnsureSuccessStatusCode();

        var list = await _client.GetFromJsonAsync<JsonElement[]>("/ag-ui/agents") ?? [];
        var agent = Assert.Single(list, x => x.GetProperty("agentId").GetString() == "agent_ext2");
        Assert.Equal("ws://new:8080/ws", agent.GetProperty("bridgeEndpoint").GetString());
        Assert.Equal("外部专家v2", agent.GetProperty("nickname").GetString());
        Assert.False(agent.TryGetProperty("bridgeToken", out _), "bridgeToken 不应出现在列表响应中");
    }

    [Fact]
    public async Task Delete_RemovesFromCatalogAndAllGroups()
    {
        var token = await RegisterAsync("dave");
        await PostAgentAsync(token, new { agentId = "agent_legacy", nickname = "旧助手", triggerMode = "mentioned" });

        // 创建包含该智能体的群
        var create = await _client.PostAsJsonAsync("/ag-ui/group/create", new
        {
            groupName = "含智能体的群",
            ownerId = "user_x",
            memberIds = new[] { "agent_legacy" },
            members = new[] { new { memberId = "agent_legacy", memberType = "agent", nickname = "旧助手" } },
        });
        create.EnsureSuccessStatusCode();
        var groupId = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("groupId").GetString()!;

        // 删除智能体
        using var del = new HttpRequestMessage(HttpMethod.Delete, "/ag-ui/agents/agent_legacy");
        del.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        (await _client.SendAsync(del)).EnsureSuccessStatusCode();

        // 目录中不再存在
        var list = await _client.GetFromJsonAsync<JsonElement[]>("/ag-ui/agents") ?? [];
        Assert.DoesNotContain(list, x => x.GetProperty("agentId").GetString() == "agent_legacy");

        // 已从群成员中移除
        var members = await _client.GetFromJsonAsync<JsonElement[]>($"/ag-ui/group/{groupId}/members?memberId=user_x") ?? [];
        Assert.DoesNotContain(members, x => x.GetProperty("memberId").GetString() == "agent_legacy");
    }

    [Fact]
    public async Task Delete_UnknownAgent_Returns404()
    {
        var token = await RegisterAsync("erin");
        using var del = new HttpRequestMessage(HttpMethod.Delete, "/ag-ui/agents/agent_ghost");
        del.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.SendAsync(del)).StatusCode);
    }

    /// <summary>技能（Skills）配置随创建 / 列表往返回显。</summary>
    [Fact]
    public async Task Create_WithSkills_ListRoundTrips()
    {
        var token = await RegisterAsync("skilluser");
        // 先创建目标智能体
        await PostAgentAsync(token, new { agentId = "agent_docs", nickname = "文档专家", triggerMode = "mentioned", keywords = (string[]?)null });
        // 再创建带技能的智能体
        var res = await PostAgentAsync(token, new
        {
            agentId = "agent_host",
            nickname = "主助手",
            triggerMode = "mentioned",
            keywords = (string[]?)null,
            skills = new[]
            {
                new { skillId = "skill_docs", description = "查文档时调用", targetAgentId = "agent_docs" },
            },
        });
        res.EnsureSuccessStatusCode();

        var list = await _client.GetFromJsonAsync<JsonElement[]>("/ag-ui/agents") ?? [];
        var host = Assert.Single(list, x => x.GetProperty("agentId").GetString() == "agent_host");
        var skill = Assert.Single(host.GetProperty("skills").EnumerateArray());
        Assert.Equal("skill_docs", skill.GetProperty("skillId").GetString());
        Assert.Equal("查文档时调用", skill.GetProperty("description").GetString());
        Assert.Equal("agent_docs", skill.GetProperty("targetAgentId").GetString());
    }

    [Theory]
    [InlineData("技能分析")]      // 中文：OpenAI 工具名不允许
    [InlineData("skill docs")]    // 空格
    [InlineData("skill.docs")]    // 点号
    [InlineData("skill/1")]       // 斜杠
    public async Task Create_WithInvalidSkillId_Returns400(string skillId)
    {
        var token = await RegisterAsync("badskill" + skillId.GetHashCode());
        await PostAgentAsync(token, new { agentId = "agent_skillbase", nickname = "目标", triggerMode = "mentioned", keywords = (string[]?)null });

        var res = await PostAgentAsync(token, new
        {
            agentId = "agent_bad",
            nickname = "非法技能",
            triggerMode = "mentioned",
            keywords = (string[]?)null,
            skills = new[]
            {
                new { skillId, description = "x", targetAgentId = "agent_skillbase" },
            },
        });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);

        // 未被创建
        var list = await _client.GetFromJsonAsync<JsonElement[]>("/ag-ui/agents") ?? [];
        Assert.DoesNotContain(list, x => x.GetProperty("agentId").GetString() == "agent_bad");
    }

    /// <summary>技能标识留空 → 后端按目标智能体自动生成（skill_&lt;agentId&gt;），列表回显生成后的名称。</summary>
    [Fact]
    public async Task Create_WithEmptySkillId_AutoGeneratesName()
    {
        var token = await RegisterAsync("autoskill" + Guid.NewGuid().ToString("N")[..6]);
        await PostAgentAsync(token, new { agentId = "agent_docs2", nickname = "文档专家", triggerMode = "mentioned", keywords = (string[]?)null });

        var res = await PostAgentAsync(token, new
        {
            agentId = "agent_host2",
            nickname = "主助手",
            triggerMode = "mentioned",
            keywords = (string[]?)null,
            skills = new[]
            {
                new { skillId = (string?)null, description = "查文档时调用", targetAgentId = "agent_docs2" },
            },
        });
        res.EnsureSuccessStatusCode();

        var list = await _client.GetFromJsonAsync<JsonElement[]>("/ag-ui/agents") ?? [];
        var host = Assert.Single(list, x => x.GetProperty("agentId").GetString() == "agent_host2");
        var skill = Assert.Single(host.GetProperty("skills").EnumerateArray());
        var skillId = skill.GetProperty("skillId").GetString();
        Assert.Equal("skill_agent_docs2", skillId);
        Assert.Equal("查文档时调用", skill.GetProperty("description").GetString());
        Assert.Equal("agent_docs2", skill.GetProperty("targetAgentId").GetString());
    }

    /// <summary>多个技能留空且目标相同 → 自动生成不同名（skill_&lt;id&gt;、skill_&lt;id&gt;_2）。</summary>
    [Fact]
    public async Task Create_EmptySkillIds_GenerateDistinctNames()
    {
        var token = await RegisterAsync("multiskill" + Guid.NewGuid().ToString("N")[..6]);
        await PostAgentAsync(token, new { agentId = "agent_tgt", nickname = "目标", triggerMode = "mentioned", keywords = (string[]?)null });

        var res = await PostAgentAsync(token, new
        {
            agentId = "agent_host3",
            nickname = "主助手",
            triggerMode = "mentioned",
            keywords = (string[]?)null,
            skills = new[]
            {
                new { skillId = (string?)null, description = "一", targetAgentId = "agent_tgt" },
                new { skillId = (string?)null, description = "二", targetAgentId = "agent_tgt" },
            },
        });
        res.EnsureSuccessStatusCode();

        var list = await _client.GetFromJsonAsync<JsonElement[]>("/ag-ui/agents") ?? [];
        var host = Assert.Single(list, x => x.GetProperty("agentId").GetString() == "agent_host3");
        var names = host.GetProperty("skills").EnumerateArray()
            .Select(x => x.GetProperty("skillId").GetString())
            .ToList();
        Assert.Equal(2, names.Count);
        Assert.Equal(2, names.Distinct().Count());
        Assert.Contains("skill_agent_tgt", names);
        Assert.Contains("skill_agent_tgt_2", names);
    }

    [Fact]
    public async Task GroupTriggerOverride_SurvivesRoleUpdate_UnoverriddenFollowsDefault()
    {
        var token = await RegisterAsync("frank");
        await PostAgentAsync(token, new { agentId = "agent_grp", nickname = "群助手", triggerMode = "mentioned", keywords = (string[]?)null });

        // 建群并加入智能体
        var groupId = await CreateGroupWithAgentAsync("agent_grp", "群助手", "覆盖群");

        // 群内显式覆盖为「全量监听」
        var reg = await _client.PostAsJsonAsync("/ag-ui/agents/register?memberId=user_x", new Dictionary<string, object?>
        {
            ["agentId"] = "agent_grp", ["nickname"] = "群助手", ["groupId"] = groupId,
            ["triggerMode"] = "allMessages", ["keywords"] = null, ["override"] = true,
        });
        reg.EnsureSuccessStatusCode();

        // 第二个群：按角色默认注册（未覆盖，模拟前端建群默认行为）
        var groupId2 = await CreateGroupWithAgentAsync("agent_grp", "群助手", "跟随群");
        var reg2 = await _client.PostAsJsonAsync("/ag-ui/agents/register?memberId=user_x", new Dictionary<string, object?>
        {
            ["agentId"] = "agent_grp", ["nickname"] = "群助手", ["groupId"] = groupId2,
            ["triggerMode"] = "mentioned", ["keywords"] = null, ["override"] = false,
        });
        reg2.EnsureSuccessStatusCode();

        // 角色编辑改为「语境触发」
        using var put = new HttpRequestMessage(HttpMethod.Put, "/ag-ui/agents/agent_grp")
        {
            Content = JsonContent.Create(new
            {
                agentId = "agent_grp", nickname = "群助手v2", triggerMode = "contextual", keywords = (string[]?)null,
            }),
        };
        put.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        (await _client.SendAsync(put)).EnsureSuccessStatusCode();

        var registry = _fixture.App.Services.GetRequiredService<AgentRegistry>();
        // 显式覆盖的群保留群内设定
        var overridden = registry.ForGroupAgent(groupId, "agent_grp");
        Assert.NotNull(overridden);
        Assert.Equal(AgentTriggerMode.AllMessages, overridden!.TriggerMode);
        Assert.True(overridden.IsOverridden);
        // 未覆盖的群跟随新角色默认
        var followed = registry.ForGroupAgent(groupId2, "agent_grp");
        Assert.NotNull(followed);
        Assert.Equal(AgentTriggerMode.Contextual, followed!.TriggerMode);
        Assert.False(followed.IsOverridden);
    }

    [Fact]
    public async Task CreateAndUpdateAgent_Avatar_PropagatesToGroupMembers()
    {
        var token = await RegisterAsync("grace2");
        // 创建带头像的智能体
        await PostAgentAsync(token, new
        {
            agentId = "agent_av", nickname = "头像助手", triggerMode = "mentioned",
            avatar = "/ag-ui/files/att_1/a.png", keywords = (string[]?)null,
        });

        // 目录回显头像
        var list = await _client.GetFromJsonAsync<JsonElement[]>("/ag-ui/agents") ?? [];
        var created = Assert.Single(list, x => x.GetProperty("agentId").GetString() == "agent_av");
        Assert.Equal("/ag-ui/files/att_1/a.png", created.GetProperty("avatar").GetString());

        // 建群加入智能体（携带头像）→ 群成员头像生效
        var groupId = await CreateGroupWithAgentAsync("agent_av", "头像助手", "头像群", "/ag-ui/files/att_1/a.png");
        var members = await _client.GetFromJsonAsync<JsonElement[]>($"/ag-ui/group/{groupId}/members?memberId=user_x") ?? [];
        var member = Assert.Single(members, m => m.GetProperty("memberId").GetString() == "agent_av");
        Assert.Equal("/ag-ui/files/att_1/a.png", member.GetProperty("avatar").GetString());

        // 编辑智能体（换头像 + 改昵称）→ 群成员资料同步
        using var put = new HttpRequestMessage(HttpMethod.Put, "/ag-ui/agents/agent_av")
        {
            Content = JsonContent.Create(new
            {
                agentId = "agent_av", nickname = "头像助手v2", triggerMode = "mentioned",
                avatar = "/ag-ui/files/att_2/b.png", keywords = (string[]?)null,
            }),
        };
        put.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        (await _client.SendAsync(put)).EnsureSuccessStatusCode();

        var members2 = await _client.GetFromJsonAsync<JsonElement[]>($"/ag-ui/group/{groupId}/members?memberId=user_x") ?? [];
        var member2 = Assert.Single(members2, m => m.GetProperty("memberId").GetString() == "agent_av");
        Assert.Equal("/ag-ui/files/att_2/b.png", member2.GetProperty("avatar").GetString());
        Assert.Equal("头像助手v2", member2.GetProperty("nickname").GetString());
    }

    [Fact]
    public async Task GroupTriggerOverride_RegisterInherit_ResetsToRoleDefault()
    {
        var token = await RegisterAsync("gloria");
        await PostAgentAsync(token, new { agentId = "agent_grp2", nickname = "群助手", triggerMode = "mentioned", keywords = (string[]?)null });
        var groupId = await CreateGroupWithAgentAsync("agent_grp2", "群助手", "覆盖群");

        // 先覆盖为全量监听
        var reg = await _client.PostAsJsonAsync("/ag-ui/agents/register?memberId=user_x", new Dictionary<string, object?>
        {
            ["agentId"] = "agent_grp2", ["nickname"] = "群助手", ["groupId"] = groupId,
            ["triggerMode"] = "allMessages", ["keywords"] = null, ["override"] = true,
        });
        reg.EnsureSuccessStatusCode();

        // 再恢复为「跟随角色默认」（override=false）
        var inherit = await _client.PostAsJsonAsync("/ag-ui/agents/register?memberId=user_x", new Dictionary<string, object?>
        {
            ["agentId"] = "agent_grp2", ["nickname"] = "群助手", ["groupId"] = groupId,
            ["triggerMode"] = "mentioned", ["keywords"] = null, ["override"] = false,
        });
        inherit.EnsureSuccessStatusCode();

        var r = _fixture.App.Services.GetRequiredService<AgentRegistry>().ForGroupAgent(groupId, "agent_grp2");
        Assert.NotNull(r);
        Assert.Equal(AgentTriggerMode.Mentioned, r!.TriggerMode);
        Assert.False(r.IsOverridden, "恢复跟随默认后不应再标记为群内覆盖");
    }

    // ================= 知识库 API =================

    [Fact]
    public async Task Kb_CreateAndList_RoundTrips()
    {
        var token = await RegisterAsync("kbuser" + Guid.NewGuid().ToString("N")[..6]);
        var res = await _client.SendAsync(AuthMessage(HttpMethod.Post, "/ag-ui/kb", token, new { name = "公司制度", description = "内部制度文档" }));
        res.EnsureSuccessStatusCode();
        var created = await res.Content.ReadFromJsonAsync<JsonElement>();
        var kbId = created.GetProperty("kbId").GetString()!;
        Assert.StartsWith("kb_", kbId);

        var list = await _client.SendAsync(AuthMessage(HttpMethod.Get, "/ag-ui/kb", token));
        var kbs = await list.Content.ReadFromJsonAsync<JsonElement[]>();
        var kb = Assert.Single(kbs!, x => x.GetProperty("kbId").GetString() == kbId);
        Assert.Equal("公司制度", kb.GetProperty("name").GetString());
        Assert.Empty(kb.GetProperty("documents").EnumerateArray());
    }

    [Fact]
    public async Task Kb_CreateWithoutToken_Returns401()
    {
        var res = await _client.PostAsJsonAsync("/ag-ui/kb", new { name = "x" });
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Kb_DeleteByNonOwner_Returns403()
    {
        var owner = await RegisterAsync("kbowner" + Guid.NewGuid().ToString("N")[..6]);
        var created = await _client.SendAsync(AuthMessage(HttpMethod.Post, "/ag-ui/kb", owner, new { name = "我的库" }));
        created.EnsureSuccessStatusCode();
        var kbId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("kbId").GetString()!;

        var other = await RegisterAsync("kbother" + Guid.NewGuid().ToString("N")[..6]);
        var del = await _client.SendAsync(AuthMessage(HttpMethod.Delete, $"/ag-ui/kb/{kbId}", other));
        Assert.Equal(HttpStatusCode.Forbidden, del.StatusCode);

        // 创建者可以删除
        var del2 = await _client.SendAsync(AuthMessage(HttpMethod.Delete, $"/ag-ui/kb/{kbId}", owner));
        Assert.Equal(HttpStatusCode.OK, del2.StatusCode);
        var list = await _client.SendAsync(AuthMessage(HttpMethod.Get, "/ag-ui/kb", owner));
        var kbs = await list.Content.ReadFromJsonAsync<JsonElement[]>();
        Assert.DoesNotContain(kbs!, x => x.GetProperty("kbId").GetString() == kbId);
    }

    [Fact]
    public async Task Agent_BindKnowledgeBase_RoundTrips()
    {
        var token = await RegisterAsync("kbagent" + Guid.NewGuid().ToString("N")[..6]);
        var created = await _client.SendAsync(AuthMessage(HttpMethod.Post, "/ag-ui/kb", token, new { name = "资料库" }));
        created.EnsureSuccessStatusCode();
        var kbId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("kbId").GetString()!;

        var res = await PostAgentAsync(token, new
        {
            agentId = "agent_kbhost",
            nickname = "知识助手",
            triggerMode = "mentioned",
            keywords = (string[]?)null,
            knowledgeBaseIds = new[] { kbId },
        });
        res.EnsureSuccessStatusCode();

        var list = await _client.GetFromJsonAsync<JsonElement[]>("/ag-ui/agents") ?? [];
        var agent = Assert.Single(list, x => x.GetProperty("agentId").GetString() == "agent_kbhost");
        Assert.Equal(kbId, Assert.Single(agent.GetProperty("knowledgeBaseIds").EnumerateArray()).GetString());
    }

    /// <summary>fixture 为 memory 存储（无向量存储）：文档入库应返回明确的不可用错误而非崩溃。</summary>
    [Fact]
    public async Task Kb_AddDocument_WithoutVectorStore_ReturnsError()
    {
        var token = await RegisterAsync("kbdoc" + Guid.NewGuid().ToString("N")[..6]);
        var created = await _client.SendAsync(AuthMessage(HttpMethod.Post, "/ag-ui/kb", token, new { name = "临时库" }));
        created.EnsureSuccessStatusCode();
        var kbId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("kbId").GetString()!;

        var res = await _client.SendAsync(AuthMessage(HttpMethod.Post, $"/ag-ui/kb/{kbId}/documents", token, new { attachmentId = "att_none" }));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("语义记忆", body.GetProperty("message").GetString());
    }

    private static HttpRequestMessage AuthMessage(HttpMethod method, string url, string token, object? body = null)
    {
        var msg = new HttpRequestMessage(method, url);
        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) msg.Content = JsonContent.Create(body);
        return msg;
    }

    // ================= 技能目标智能体（自建技能） =================

    /// <summary>自建技能的目标智能体（IsSkillTarget）不出现在智能体目录，且拒绝 HTTP 编辑 / 删除。</summary>
    [Fact]
    public async Task Agent_SkillTarget_HiddenFromListAndProtected()
    {
        var token = await RegisterAsync("skilltgt" + Guid.NewGuid().ToString("N")[..6]);
        // 直接经 AgentCatalog 注入一个技能目标智能体（等价于 create_skill 工具的效果）
        var catalog = _fixture.App.Services.GetRequiredService<AgentCatalog>();
        catalog.Upsert(new AgentDefinition
        {
            AgentId = "skill_secret", Nickname = "secret", Description = "", Instructions = "人设",
            IsSkillTarget = true, OwnerId = null,
        });

        // 目录不暴露
        var list = await _client.GetFromJsonAsync<JsonElement[]>("/ag-ui/agents") ?? [];
        Assert.DoesNotContain(list, x => x.GetProperty("agentId").GetString() == "skill_secret");

        // 删除被拒绝
        var del = await _client.SendAsync(AuthMessage(HttpMethod.Delete, "/ag-ui/agents/skill_secret", token));
        Assert.Equal(HttpStatusCode.Forbidden, del.StatusCode);
        // 编辑被拒绝
        var put = await _client.SendAsync(AuthMessage(HttpMethod.Put, "/ag-ui/agents/skill_secret", token,
            new { agentId = "skill_secret", nickname = "改名", triggerMode = "mentioned", keywords = (string[]?)null }));
        Assert.Equal(HttpStatusCode.Forbidden, put.StatusCode);
    }

    /// <summary>私密智能体：仅创建者可看到、拉入群、编辑与删除。</summary>
    [Fact]
    public async Task PrivateAgent_OnlyOwnerCanSeeAndAddToGroup()
    {
        var (aliceToken, aliceId) = await RegisterUserAsync("alice_p");
        var (bobToken, bobId) = await RegisterUserAsync("bob_p");

        // alice 创建私密智能体
        (await PostAgentAsync(aliceToken, new
        {
            agentId = "agent_priv", nickname = "私密助手", triggerMode = "mentioned",
            isPrivate = true, keywords = (string[]?)null,
        })).EnsureSuccessStatusCode();

        // 目录可见性：匿名与 bob（他人）看不到；alice（创建者）看得到
        var anonList = await _client.GetFromJsonAsync<JsonElement[]>("/ag-ui/agents");
        Assert.DoesNotContain(anonList ?? [], a => a.GetProperty("agentId").GetString() == "agent_priv");

        using var bobReq = new HttpRequestMessage(HttpMethod.Get, "/ag-ui/agents");
        bobReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bobToken);
        var bobList = await (await _client.SendAsync(bobReq)).Content.ReadFromJsonAsync<JsonElement[]>();
        Assert.DoesNotContain(bobList ?? [], a => a.GetProperty("agentId").GetString() == "agent_priv");

        using var aliceReq = new HttpRequestMessage(HttpMethod.Get, "/ag-ui/agents");
        aliceReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", aliceToken);
        var aliceList = await (await _client.SendAsync(aliceReq)).Content.ReadFromJsonAsync<JsonElement[]>();
        Assert.Contains(aliceList ?? [], a => a.GetProperty("agentId").GetString() == "agent_priv");

        // bob 建群拉入私密智能体 → 403 AGENT_PERMISSION_DENIED
        var bobCreate = await _client.PostAsJsonAsync("/ag-ui/group/create", new
        {
            groupName = "bob 的群", ownerId = bobId,
            memberIds = new[] { "agent_priv" },
            members = new[] { new { memberId = "agent_priv", memberType = "agent", nickname = "私密助手" } },
        });
        Assert.Equal(HttpStatusCode.Forbidden, bobCreate.StatusCode);

        // alice 建群拉入 → 成功
        var aliceCreate = await _client.PostAsJsonAsync("/ag-ui/group/create", new
        {
            groupName = "alice 的群", ownerId = aliceId,
            memberIds = new[] { "agent_priv" },
            members = new[] { new { memberId = "agent_priv", memberType = "agent", nickname = "私密助手" } },
        });
        aliceCreate.EnsureSuccessStatusCode();

        // bob 编辑 / 删除 → 403
        using var put = new HttpRequestMessage(HttpMethod.Put, "/ag-ui/agents/agent_priv")
        {
            Content = JsonContent.Create(new { agentId = "agent_priv", nickname = "改名", triggerMode = "mentioned" }),
        };
        put.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bobToken);
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(put)).StatusCode);

        using var del = new HttpRequestMessage(HttpMethod.Delete, "/ag-ui/agents/agent_priv");
        del.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bobToken);
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(del)).StatusCode);
    }

    // ================= 一句话简介 → 角色设定生成 =================

    [Fact]
    public async Task GenerateInstructions_WithDescription_ReturnsStructuredPrompt()
    {
        var token = await RegisterAsync("prompt_gen");
        using var req = new HttpRequestMessage(HttpMethod.Post, "/ag-ui/agents/generate-instructions")
        {
            Content = JsonContent.Create(new { description = "产品需求分析助手" }),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var res = await _client.SendAsync(req);
        res.EnsureSuccessStatusCode();
        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        var instructions = json.GetProperty("instructions").GetString()!;
        Assert.Contains("身份定位", instructions);
        Assert.Contains("职责范围", instructions);
        Assert.Contains("回复风格", instructions);
    }

    [Fact]
    public async Task GenerateInstructions_TooShortDescription_Returns400()
    {
        var token = await RegisterAsync("prompt_gen2");
        using var req = new HttpRequestMessage(HttpMethod.Post, "/ag-ui/agents/generate-instructions")
        { Content = JsonContent.Create(new { description = "x" }) };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.SendAsync(req)).StatusCode);
    }

    [Fact]
    public async Task GenerateInstructions_WithoutToken_Returns401()
    {
        var res = await _client.PostAsJsonAsync("/ag-ui/agents/generate-instructions", new { description = "测试" });
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    // ================= AI 分身不在智能体管理目录 =================

    [Fact]
    public async Task TwinAgents_NotExposedInAgentManagement_AndNotEditable()
    {
        var (token, userId) = await RegisterUserAsync("twin_hide");

        // 启用分身
        using var enableReq = new HttpRequestMessage(HttpMethod.Post, "/ag-ui/twin/enable")
        { Content = JsonContent.Create(new { triggerMode = "mentioned" }) };
        enableReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        (await _client.SendAsync(enableReq)).EnsureSuccessStatusCode();
        var twinId = "twin_" + userId;

        // 1) 目录不返回分身（分身只经「修改资料 → AI 分身」管理）
        var list = await _client.GetFromJsonAsync<JsonElement[]>("/ag-ui/agents") ?? [];
        Assert.DoesNotContain(list, x => x.GetProperty("agentId").GetString() == twinId);

        // 2) PUT / DELETE 分身 → 403（防止绕过目录直接操作）
        using var put = new HttpRequestMessage(HttpMethod.Put, "/ag-ui/agents/" + twinId)
        { Content = JsonContent.Create(new { nickname = "改名", triggerMode = "mentioned" }) };
        put.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(put)).StatusCode);

        using var del = new HttpRequestMessage(HttpMethod.Delete, "/ag-ui/agents/" + twinId);
        del.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(del)).StatusCode);

        // 3) 创建时保留 twin_ 前缀 → 400
        using var create = new HttpRequestMessage(HttpMethod.Post, "/ag-ui/agents")
        { Content = JsonContent.Create(new { agentId = "twin_hijack", nickname = "x", triggerMode = "mentioned" }) };
        create.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.SendAsync(create)).StatusCode);

        // 清理
        using var disableReq = new HttpRequestMessage(HttpMethod.Post, "/ag-ui/twin/disable");
        disableReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        (await _client.SendAsync(disableReq)).EnsureSuccessStatusCode();
    }

    // ================= 辅助 =================

    /// <summary>编排 apply 请求：以 HTTP JSON（camelCase）序列化 record，与服务端约定一致。</summary>
    private HttpRequestMessage ApplyRequest(string token, OrchestrateApplyRequest body)
    {
        var msg = new HttpRequestMessage(HttpMethod.Post, "/ag-ui/agents/orchestrate/apply");
        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        msg.Content = JsonContent.Create(body, options: new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return msg;
    }

    private async Task<string> RegisterAsync(string username)
    {
        var res = await _client.PostAsJsonAsync("/ag-ui/user/register", new { username, password = "secret1", nickname = username });
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
    }

    private async Task<(string Token, string UserId)> RegisterUserAsync(string username)
    {
        var res = await _client.PostAsJsonAsync("/ag-ui/user/register", new { username, password = "secret1", nickname = username });
        res.EnsureSuccessStatusCode();
        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        return (json.GetProperty("token").GetString()!, json.GetProperty("userId").GetString()!);
    }

    private async Task<HttpResponseMessage> PostAgentAsync(string token, object body)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/ag-ui/agents") { Content = JsonContent.Create(body) };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _client.SendAsync(req);
    }

    private async Task<string> CreateGroupWithAgentAsync(string agentId, string nickname, string groupName, string? avatar = null)
    {
        var create = await _client.PostAsJsonAsync("/ag-ui/group/create", new
        {
            groupName,
            ownerId = "user_x",
            memberIds = new[] { agentId },
            members = new[] { new { memberId = agentId, memberType = "agent", nickname, avatar } },
        });
        create.EnsureSuccessStatusCode();
        return (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("groupId").GetString()!;
    }

    // ================= 一键组织编排 =================

    [Fact]
    public async Task Orchestrate_Preview_ReturnsPlanWithoutPersisting()
    {
        var token = await RegisterAsync("orch_preview");
        using var req = await _client.SendAsync(AuthMessage(HttpMethod.Post, "/ag-ui/agents/orchestrate", token,
            new { requirement = "组建一个客户服务团队" }));
        req.EnsureSuccessStatusCode();
        var d = await req.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(d.GetProperty("orchestrated").GetBoolean());
        var agents = d.GetProperty("agents");
        Assert.True(agents.GetArrayLength() >= 1);
        Assert.True(d.GetProperty("skills").GetArrayLength() >= 1);

        // 未落库：生成后库里不应出现这些 agent / skill。取预览里第一个 agentId 校验不存在。
        var firstAgent = agents[0].GetProperty("agentId").GetString()!;
        var catalog = _fixture.App.Services.GetRequiredService<AgentCatalog>();
        Assert.Null(catalog.GetDefinition(firstAgent));
    }

    [Fact]
    public async Task Orchestrate_Apply_CreatesAgentsSkillsAndConnections()
    {
        var token = await RegisterAsync("orch_apply");
        // mock 模板生成的固定方案：mgr + execA + execB + 两个技能
        var preview = await AgentOrchestrator.GenerateAsync(
            new AgentOptions { Provider = "mock" }, "客户服务团队", NullLoggerFactory.Instance.CreateLogger("t"), CancellationToken.None);
        var reqBody = new OrchestrateApplyRequest(
            preview.Title,
            preview.Agents.Select(a => new OrchestratedAgentHttp(a.AgentId, a.Nickname, a.Description, a.Instructions,
                a.TriggerMode, a.SkillIds, a.AssignmentIds, a.EscalationAgentId, a.RelayToAgentId)).ToList(),
            preview.Skills.Select(s => new OrchestratedSkillHttp(s.SkillId, s.Name, s.Description, s.Kind,
                s.Body, s.ExecutionLocation, s.RequiresApproval)).ToList());

        var res = await _client.SendAsync(ApplyRequest(token, reqBody));
        if (!res.IsSuccessStatusCode)
        {
            var errb = await res.Content.ReadAsStringAsync();
            throw new Xunit.Sdk.XunitException($"apply 失败 HTTP {res.StatusCode}: {errb}");
        }

        var catalog = _fixture.App.Services.GetRequiredService<AgentCatalog>();
        var skills = _fixture.App.Services.GetRequiredService<AgentSkillCatalog>();
        // 技能落库
        foreach (var s in preview.Skills) Assert.NotNull(skills.Get(s.SkillId!));
        // 数字员工落库 + 连接（mock 模板：Agents[0]=主管(assignment 两个执行岗)，Agents[1]=执行岗A(escalation=主管)）
        var mgrId = preview.Agents[0].AgentId!;
        var execId = preview.Agents[1].AgentId!;
        Assert.NotNull(catalog.GetDefinition(mgrId));
        var mgrDef = catalog.GetDefinition(mgrId)!;
        Assert.Equal(execId, mgrDef.AssignmentIds[0]);
        Assert.Equal(mgrId, catalog.GetDefinition(execId)!.EscalationAgentId);
        Assert.Contains(mgrDef.SkillDefIds, sid => preview.Skills.Any(s => s.SkillId == sid));
    }
}


/// <summary>一键组织编排生成器（AgentOrchestrator）单元测试：mock 模式确定性模板与解析。</summary>
public sealed class AgentOrchestratorTests
{
    [Fact]
    public async Task Mock_GeneratesDeterministicOrgWithSkillsAndConnections()
    {
        var plan = await AgentOrchestrator.GenerateAsync(
            new AgentOptions { Provider = "mock" }, "财务报销流程",
            NullLoggerFactory.Instance.CreateLogger("orch"), CancellationToken.None);

        Assert.False(string.IsNullOrWhiteSpace(plan.Title));
        Assert.NotEmpty(plan.Agents);
        Assert.NotEmpty(plan.Skills);
        // 主管有向下指派两个执行岗；两个执行岗都向上提升到主管；技能引用都存在
        var mgr = Assert.Single(plan.Agents, a => (a.AssignmentIds ?? []).Count > 0);
        var execs = plan.Agents.Where(a => !string.IsNullOrEmpty(a.EscalationAgentId)).ToList();
        Assert.NotEmpty(execs);
        Assert.All(execs, e => Assert.Equal(mgr.AgentId, e.EscalationAgentId));
        foreach (var a in plan.Agents)
            foreach (var sid in a.SkillIds ?? [])
                Assert.Contains(plan.Skills, s => s.SkillId == sid);
    }

    [Theory]
    [InlineData("  ")]
    [InlineData("x")]
    public async Task ShortRequirement_Throws(string requirement)
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => AgentOrchestrator.GenerateAsync(
            new AgentOptions { Provider = "mock" }, requirement, NullLoggerFactory.Instance.CreateLogger("t"), CancellationToken.None));
        Assert.Contains("至少 2 个字符", ex.Message);
    }
}
