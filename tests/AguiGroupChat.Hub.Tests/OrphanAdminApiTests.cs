using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AguiGroupChat.Agents;
using AguiGroupChat.Hub.Messaging;
using AguiGroupChat.Hub.Models;
using AguiGroupChat.Hub.Users;
using AguiGroupChat.Web;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>孤儿定义运营盘点：OwnerId 指向已注销账号的数字员工 / 技能的查询、接管、删除（含安全闸）。</summary>
public sealed class OrphanAdminApiTests : IClassFixture<AdminApiServerFixture>
{
    private readonly AdminApiServerFixture _fixture;
    private readonly HttpClient _client;

    public OrphanAdminApiTests(AdminApiServerFixture fixture)
    {
        _fixture = fixture;
        _client = new HttpClient { BaseAddress = new Uri(fixture.HttpBase) };
    }

    private async Task<string> AdminTokenAsync()
    {
        var res = await _client.PostAsJsonAsync("/ag-ui/user/login",
            new { username = "admin_chief", password = "secret123" });
        if (res.StatusCode == HttpStatusCode.Unauthorized)
        {
            var reg = await _client.PostAsJsonAsync("/ag-ui/user/register",
                new { username = "admin_chief", password = "secret123", nickname = "admin_chief" });
            reg.EnsureSuccessStatusCode();
            res = await _client.PostAsJsonAsync("/ag-ui/user/login",
                new { username = "admin_chief", password = "secret123" });
        }
        res.EnsureSuccessStatusCode();
        var d = await res.Content.ReadFromJsonAsync<JsonElement>();
        return d.GetProperty("token").GetString()!;
    }

    private static HttpRequestMessage Authed(HttpMethod method, string path, string token)
    {
        var req = new HttpRequestMessage(method, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return req;
    }

    [Fact]
    public async Task Orphans_List_Adopt_Delete()
    {
        var token = await AdminTokenAsync();
        var catalog = _fixture.App.Services.GetRequiredService<AgentCatalog>();
        var skills = _fixture.App.Services.GetRequiredService<AgentSkillCatalog>();
        var users = _fixture.App.Services.GetRequiredService<IUserStore>();
        var adminId = users.ListUsers().First(u => u.Username == "admin_chief").UserId;

        // 种子：两个孤儿（OwnerId 指向不存在账号）+ 一个技能
        catalog.Upsert(new AgentDefinition
        {
            AgentId = "orphan_gone_a", Nickname = "孤儿甲", Description = "已注销用户的员工",
            OwnerId = "user_gone_owner", IsPrivate = true,
        });
        catalog.Upsert(new AgentDefinition { AgentId = "orphan_gone_b", Nickname = "孤儿乙", OwnerId = "user_gone_owner" });
        skills.Upsert(new AgentSkillDefinition { SkillId = "orphan_skill", Name = "孤儿技能", OwnerId = "user_gone_owner", Kind = AgentSkillKind.Prompt });

        // 盘点：三个都在列
        var list = await (await _client.SendAsync(Authed(HttpMethod.Get, "/ag-ui/admin/orphans", token)))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains(list.GetProperty("agents").EnumerateArray(),
            a => a.GetProperty("agentId").GetString() == "orphan_gone_a");
        Assert.Contains(list.GetProperty("skills").EnumerateArray(),
            s => s.GetProperty("skillId").GetString() == "orphan_skill");

        // 删除孤儿技能（未被挂载 → 允许）
        var delSkill = await _client.SendAsync(Authed(HttpMethod.Delete, "/ag-ui/admin/orphans/skills/orphan_skill", token));
        delSkill.EnsureSuccessStatusCode();
        Assert.Null(skills.Get("orphan_skill"));

        // 孤儿甲被某现存知聚引用为成员 → 删除被安全闸拒绝；接管后归属当前管理员
        var hub = _fixture.App.Services.GetRequiredService<GroupHub>();
        var g = await hub.CreateGroupAsync(new GroupCreateRequest { GroupName = "引用孤儿甲的知聚", OwnerId = adminId });
        hub.Store.AddMember(g.GroupId, new GroupMember
        {
            MemberId = "orphan_gone_a", MemberType = MemberType.Agent, Nickname = "孤儿甲",
            Role = GroupRole.Normal, OnlineStatus = OnlineStatus.Offline,
            JoinTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });

        var denied = await _client.SendAsync(Authed(HttpMethod.Delete, "/ag-ui/admin/orphans/agents/orphan_gone_a", token));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        var adopt = await _client.SendAsync(Authed(HttpMethod.Post, "/ag-ui/admin/orphans/agents/orphan_gone_a/adopt", token));
        adopt.EnsureSuccessStatusCode();
        Assert.Equal(adminId, catalog.GetDefinition("orphan_gone_a")!.OwnerId); // 已被当前管理员接管

        // 盘点中不再出现已接管的孤儿；孤儿乙未被引用 → 可删除
        var list2 = await (await _client.SendAsync(Authed(HttpMethod.Get, "/ag-ui/admin/orphans", token)))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.DoesNotContain(list2.GetProperty("agents").EnumerateArray(),
            a => a.GetProperty("agentId").GetString() == "orphan_gone_a");
        var delAgent = await _client.SendAsync(Authed(HttpMethod.Delete, "/ag-ui/admin/orphans/agents/orphan_gone_b", token));
        delAgent.EnsureSuccessStatusCode();
        Assert.Null(catalog.GetDefinition("orphan_gone_b"));
    }
}
