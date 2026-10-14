using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AguiGroupChat.Agents;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>评测判定（纯函数）：期望要点的命中判定。</summary>
public sealed class EvalScoreTests
{
    [Fact]
    public void AnyMode_PassesWhenOneMatches()
    {
        var s = AgentEvalRunner.Score("这是建议与方案", ["建议", "不存在"], "any");
        Assert.True(s.Passed);
        Assert.Contains("建议", s.Matched);
        Assert.Contains("不存在", s.Missed);
    }

    [Fact]
    public void AllMode_FailsWhenOneMissing_CaseInsensitive()
    {
        var s = AgentEvalRunner.Score("Hello World", ["hello", "world", "missing"], "all");
        Assert.False(s.Passed);
        Assert.Equal(2, s.Matched.Count);   // 大小写不敏感
        Assert.Single(s.Missed);
    }

    [Fact]
    public void EmptyExpected_PassesOnlyWhenReplyNonEmpty()
    {
        Assert.True(AgentEvalRunner.Score("有内容", [], "any").Passed);
        Assert.False(AgentEvalRunner.Score("   ", [], "any").Passed);
    }
}

/// <summary>评测集端点：CRUD + 一键跑分（mock 网关，确定性）。</summary>
public sealed class EvalApiTests : IClassFixture<AdminApiServerFixture>
{
    private readonly HttpClient _client;

    public EvalApiTests(AdminApiServerFixture fixture)
        => _client = new HttpClient { BaseAddress = new Uri(fixture.HttpBase) };

    private async Task<string> AdminTokenAsync()
    {
        var res = await _client.PostAsJsonAsync("/ag-ui/user/register",
            new { username = "admin_chief", password = "secret123", nickname = "admin_chief" });
        if (!res.IsSuccessStatusCode)
            res = await _client.PostAsJsonAsync("/ag-ui/user/login", new { username = "admin_chief", password = "secret123" });
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
    }

    private HttpRequestMessage Authed(HttpMethod m, string path, string token)
    {
        var req = new HttpRequestMessage(m, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return req;
    }

    [Fact]
    public async Task Crud_And_Run_Deterministic()
    {
        var token = await AdminTokenAsync();

        // 建一个被测数字员工（mock 回复含「建议」与昵称）
        var agentId = "eval_agent_" + Guid.NewGuid().ToString("N")[..8];
        using (var create = Authed(HttpMethod.Post, "/ag-ui/agents", token))
        {
            create.Content = JsonContent.Create(new
            {
                agentId, nickname = "评测助手", description = "评测用例员工",
                instructions = "你是评测助手，请给出建议。", triggerMode = "mentioned", keywords = new string[0],
            });
            var res = await _client.SendAsync(create);
            Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
        }

        // 新建「应通过」用例
        using (var add = Authed(HttpMethod.Post, "/ag-ui/admin/eval-cases", token))
        {
            add.Content = JsonContent.Create(new { name = "建议类问题", agentId, prompt = "请给我一些建议", expected = new[] { "建议" }, matchMode = "any", enabled = true });
            var res = await _client.SendAsync(add);
            Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
        }
        // 新建「应失败」用例
        using (var add = Authed(HttpMethod.Post, "/ag-ui/admin/eval-cases", token))
        {
            add.Content = JsonContent.Create(new { name = "不可能命中", agentId, prompt = "随便问问", expected = new[] { "zzz_never_appears_zzz" }, matchMode = "any", enabled = true });
            var res = await _client.SendAsync(add);
            Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
        }

        // 列出（含被测员工候选）
        using (var list = Authed(HttpMethod.Get, "/ag-ui/admin/eval-cases", token))
        {
            var res = await _client.SendAsync(list);
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            var d = await res.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(JsonValueKind.Array, d.GetProperty("cases").ValueKind);
            Assert.Equal(JsonValueKind.Array, d.GetProperty("agents").ValueKind);
            Assert.True(d.GetProperty("cases").GetArrayLength() >= 2);
        }

        // 跑分：应得 1 通过 / 2 总数
        using (var run = Authed(HttpMethod.Post, "/ag-ui/admin/eval-cases/run", token))
        {
            run.Content = JsonContent.Create(new { caseIds = (string[]?)null });
            var res = await _client.SendAsync(run);
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            var d = await res.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(2, d.GetProperty("total").GetInt32());
            Assert.Equal(1, d.GetProperty("passed").GetInt32());
            var items = d.GetProperty("items").EnumerateArray().ToList();
            Assert.Contains(items, (i) => i.GetProperty("passed").GetBoolean());
            Assert.Contains(items, (i) => !i.GetProperty("passed").GetBoolean());
        }

        // 非管理员 → 403
        var plain = await _client.PostAsJsonAsync("/ag-ui/user/register", new { username = "eval_plain_" + Guid.NewGuid().ToString("N")[..6], password = "secret1", nickname = "plain" });
        var plainToken = (await plain.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
        using (var denied = Authed(HttpMethod.Get, "/ag-ui/admin/eval-cases", plainToken))
        {
            var res = await _client.SendAsync(denied);
            Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        }
    }
}
