using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AguiGroupChat.Agents;
using AguiGroupChat.Hub.Storage;
using AguiGroupChat.Web;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>员工绩效看板聚合（纯函数）：按数字员工汇总用量 + 反馈。</summary>
public sealed class AgentPerformanceTests
{
    private static readonly long Now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    [Fact]
    public void Aggregate_ByAgent_SumsUsageAndFeedback()
    {
        var usage = new[]
        {
            new UsageAggregate("2026-10-01", "a1", "u1", 10, 20, 0, 2),
            new UsageAggregate("2026-10-02", "a1", "u2", 5, 5, 0, 1),
            new UsageAggregate("2026-10-02", "a2", "u1", 100, 100, 0, 4),
        };
        var fb = new[]
        {
            new MessageFeedbackEntry { MessageId = "m1", GroupId = "g", AgentId = "a1", UserId = "u1", Value = 1, CreatedAtMs = Now },
            new MessageFeedbackEntry { MessageId = "m2", GroupId = "g", AgentId = "a1", UserId = "u2", Value = -1, CreatedAtMs = Now },
            new MessageFeedbackEntry { MessageId = "m3", GroupId = "g", AgentId = "a1", UserId = "u3", Value = 1, CreatedAtMs = Now },
        };
        var names = new Dictionary<string, string> { ["a1"] = "助理", ["a2"] = "代码帮" };

        var stats = AgentPerformance.Aggregate(usage, fb, names, 30, Now);

        Assert.Equal(2, stats.Count);
        // a2 调用次数更多 → 排前，且无反馈时好评率为 null
        Assert.Equal("a2", stats[0].AgentId);
        Assert.Equal("代码帮", stats[0].Name);
        Assert.Null(stats[0].PositiveRate);

        var a1 = stats.Single(s => s.AgentId == "a1");
        Assert.Equal("助理", a1.Name);
        Assert.Equal(3, a1.Calls);
        Assert.Equal(15, a1.PromptTokens);
        Assert.Equal(25, a1.CompletionTokens);
        Assert.Equal(0, a1.ReasoningTokens);
        Assert.Equal(40, a1.TotalTokens);
        Assert.Equal(2, a1.Up);
        Assert.Equal(1, a1.Down);
        Assert.Equal(2.0 / 3, a1.PositiveRate!.Value, 3);
        Assert.Equal("2026-10-02", a1.LastDate);
    }

    [Fact]
    public void Aggregate_ExcludesFeedbackOutsideWindow_AndFallsBackToId()
    {
        var fb = new[]
        {
            new MessageFeedbackEntry { MessageId = "m1", GroupId = "g", AgentId = "aX", UserId = "u1", Value = 1, CreatedAtMs = Now - 40L * 24 * 3600 * 1000 },
            new MessageFeedbackEntry { MessageId = "m2", GroupId = "g", AgentId = "aX", UserId = "u2", Value = -1, CreatedAtMs = Now },
        };

        var stats = AgentPerformance.Aggregate([], fb, new Dictionary<string, string>(), 30, Now);

        var a = Assert.Single(stats);
        Assert.Equal("aX", a.Name); // 无名字 → 回退 id
        Assert.Equal(0, a.Up);      // 窗口外的 👍 不计
        Assert.Equal(1, a.Down);
        Assert.Equal(0.0, a.PositiveRate!.Value, 3);
    }

    [Fact]
    public void Aggregate_EmptyInputs_YieldEmpty()
    {
        var stats = AgentPerformance.Aggregate([], [], new Dictionary<string, string>(), 7, Now);
        Assert.Empty(stats);
    }

    [Fact]
    public void Aggregate_ComputesFlags_LowPraise_And_Idle()
    {
        var fb = new[]
        {
            new MessageFeedbackEntry { MessageId = "m1", GroupId = "g", AgentId = "aLow", UserId = "u1", Value = 1, CreatedAtMs = Now },
            new MessageFeedbackEntry { MessageId = "m2", GroupId = "g", AgentId = "aLow", UserId = "u2", Value = -1, CreatedAtMs = Now },
            new MessageFeedbackEntry { MessageId = "m3", GroupId = "g", AgentId = "aLow", UserId = "u3", Value = -1, CreatedAtMs = Now },
        };
        var names = new Dictionary<string, string> { ["aLow"] = "低分", ["aIdle"] = "闲置", ["aGood"] = "好评" };
        var usage = new[] { new UsageAggregate("2026-10-02", "aGood", "u1", 1, 1, 0, 3) };

        var stats = AgentPerformance.Aggregate(usage, fb, names, 30, Now);

        Assert.Contains("low-praise", stats.Single(s => s.AgentId == "aLow").Flags);  // 3 反馈、1/3 好评
        Assert.Contains("idle", stats.Single(s => s.AgentId == "aIdle").Flags);       // 目录中但无用量
        Assert.Empty(stats.Single(s => s.AgentId == "aGood").Flags);                  // 有调用、无反馈 → 不标记
    }

    [Fact]
    public void ModelRouting_ResolveForTier_OnlyWhenEnabled()
    {
        var off = new ModelRoutingOptions { Enabled = false, SimpleModel = "cheap", HeavyModel = "strong" };
        Assert.Null(off.ResolveForTier(RunComplexity.Simple)); // 未启用 → 不换

        var on = new ModelRoutingOptions { Enabled = true, SimpleModel = "cheap", HeavyModel = "strong" };
        Assert.Equal("cheap", on.ResolveForTier(RunComplexity.Simple));
        Assert.Null(on.ResolveForTier(RunComplexity.Standard));          // 常规档不换
        Assert.Equal("strong", on.ResolveForTier(RunComplexity.Complex));
        Assert.Equal("strong", on.ResolveForTier(RunComplexity.Heavy));

        var partial = new ModelRoutingOptions { Enabled = true, SimpleModel = "cheap" };
        Assert.Equal("cheap", partial.ResolveForTier(RunComplexity.Simple));
        Assert.Null(partial.ResolveForTier(RunComplexity.Heavy));        // 该档未配 → 不换
    }

    [Fact]
    public void PricingOptions_EstimateCost_WeightsInputAndOutput()
    {
        var p = new PricingOptions { InputPerMillion = 2m, OutputPerMillion = 8m };
        // 1e6 prompt ×2 + (0.6e6 + 0.4e6) output ×8 = 2 + 8 = 10
        Assert.Equal(10m, p.EstimateCost(1_000_000, 600_000, 400_000));
        Assert.True(p.Configured);
        Assert.False(new PricingOptions().Configured); // 单价皆为 0 → 未配置
        // 零用量 → 零成本
        Assert.Equal(0m, p.EstimateCost(0, 0, 0));
    }
}

/// <summary>端点鉴权与响应形状（挂在管理员控制台下，Operator+）。</summary>
public sealed class AgentPerformanceApiTests : IClassFixture<AdminApiServerFixture>
{
    private readonly HttpClient _client;

    public AgentPerformanceApiTests(AdminApiServerFixture fixture)
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

    [Fact]
    public async Task RequiresOperator_AndReturnsShape()
    {
        var token = await AdminTokenAsync();
        using var req = new HttpRequestMessage(HttpMethod.Get, "/ag-ui/admin/agent-performance?days=7");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var res = await _client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var d = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(7, d.GetProperty("days").GetInt32());
        Assert.Equal(JsonValueKind.Array, d.GetProperty("agents").ValueKind);
        Assert.True(d.TryGetProperty("pricing", out var pricing));
        Assert.True(pricing.TryGetProperty("configured", out _));
        Assert.True(d.GetProperty("summary").TryGetProperty("cost", out _));

        // 无令牌 → 401
        var anon = await _client.GetAsync("/ag-ui/admin/agent-performance");
        Assert.Equal(HttpStatusCode.Unauthorized, anon.StatusCode);
    }
}
