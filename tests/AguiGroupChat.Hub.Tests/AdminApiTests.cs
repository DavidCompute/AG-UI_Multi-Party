using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AguiGroupChat.Agents;
using AguiGroupChat.Hub;
using AguiGroupChat.Hub.Models;
using AguiGroupChat.Hub.Persistence;
using AguiGroupChat.Hub.Users;
using AguiGroupChat.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json.Serialization;
using Xunit;

namespace AguiGroupChat.Hub.Tests;

/// <summary>自托管 Kestrel 集成测试夹具：Hub + 智能体网关（mock）+ 管理员控制台 API。</summary>
public sealed class AdminApiServerFixture : IAsyncLifetime
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
            // 固定管理员账号（避免依赖注册顺序）；并关闭 FirstUserIsAdmin，否则首个注册的普通用户
            // 会被自举为管理员，使各「非管理员 → 403」用例在单独运行时随机失败（与其他夹具一致）。
            ["Auth:AdminUserIds"] = "admin_chief",
            ["Auth:FirstUserIsAdmin"] = "false",
        });
        HubApp.ConfigureServices(builder);
        builder.Services.AddAgentFramework(builder.Configuration);
        builder.Services.AddSingleton(new ConfigGovernanceState()); // 配置治理（6.3）
        builder.Services.AddSingleton<SkillRunArtifactStore>(); // 附件回收的引用源之一（技能试运行产物）
        builder.Services.AddAttachmentGovernance(); // 附件回收（存储治理）
        builder.Services.AddSingleton(new StorageGovernanceOptions()); // 默认关闭回收
        // 文档在线转换能力：注册一个「已探测到转换器」的假执行器 → /admin/status 应报「就绪」
        builder.Services.AddSingleton<ISofficeRunner>(new StubAvailableSofficeRunner());
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));

        App = builder.Build();
        HubApp.MapEndpoints(App);
        App.MapAgentApi();
        App.MapAdminApi();
        App.MapConfigGovernanceApi();
        App.MapStorageAdminApi();
        App.MapExecutionRuntimeApi();
        App.MapEvalApi();
        await App.StartAsync();
        HttpBase = App.Urls.First();
    }

    public async Task DisposeAsync()
    {
        if (App is not null) await App.DisposeAsync();
    }
}

public sealed class AdminApiIntegrationTests : IClassFixture<AdminApiServerFixture>
{
    private readonly AdminApiServerFixture _fixture;
    private readonly HttpClient _client;

    public AdminApiIntegrationTests(AdminApiServerFixture fixture)
    {
        _fixture = fixture;
        _client = new HttpClient { BaseAddress = new Uri(fixture.HttpBase) };
    }

    private async Task<JsonElement> RegisterAsync(string username, string password = "secret1")
    {
        var res = await _client.PostAsJsonAsync("/ag-ui/user/register", new { username, password, nickname = username });
        if (res.StatusCode == HttpStatusCode.Conflict)
        {
            // 共享 fixture：同名用户可能已被其他测试注册 → 回退登录拿令牌
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

    // ================= 组织体检报告：结构化数据（供前端图表视图） =================

    [Fact]
    public async Task OrgHealth_ReturnsStructuredDataForCharts()
    {
        var admin = await RegisterAsync("admin_chief");
        var token = admin.GetProperty("token").GetString()!;

        using var req = Authed(HttpMethod.Get, "/ag-ui/admin/org-health?days=30", token);
        var res = await _client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var d = await res.Content.ReadFromJsonAsync<JsonElement>();

        // Markdown 仍在（下载路径不变）
        Assert.True(d.TryGetProperty("markdown", out var md) && md.GetString()!.Length > 0);
        Assert.False(string.IsNullOrWhiteSpace(d.GetProperty("filename").GetString()));

        // 结构化数据（图表视图依赖）
        var summary = d.GetProperty("summary");
        foreach (var k in new[] { "agents", "totalAgents", "calls", "totalTokens", "up", "down", "cost", "flagged" })
            Assert.True(summary.TryGetProperty(k, out _), $"summary 缺字段 {k}");
        Assert.Equal(JsonValueKind.Array, d.GetProperty("agents").ValueKind);
        Assert.Equal(JsonValueKind.Array, d.GetProperty("users").ValueKind);
        Assert.Equal(JsonValueKind.Array, d.GetProperty("groups").ValueKind);
        // 环比：上一窗口的 summary 随包下发
        Assert.True(d.TryGetProperty("prev", out var prev));
        Assert.True(prev.TryGetProperty("summary", out var prevSummary));
        foreach (var k in new[] { "agents", "calls", "totalTokens", "cost", "flagged" })
            Assert.True(prevSummary.TryGetProperty(k, out _), $"prev.summary 缺字段 {k}");
        Assert.False(string.IsNullOrWhiteSpace(prev.GetProperty("from").GetString()));
        var pricing = d.GetProperty("pricing");
        foreach (var k in new[] { "configured", "currency", "inputPerMillion", "outputPerMillion" })
            Assert.True(pricing.TryGetProperty(k, out _), $"pricing 缺字段 {k}");
    }

    [Fact]
    public async Task OrgHealth_IncludesPerUserAggregation()
    {
        var admin = await RegisterAsync("admin_chief");
        var token = admin.GetProperty("token").GetString()!;
        var usage = _fixture.App.Services.GetRequiredService<AguiGroupChat.Hub.Agents.AgentUsageService>();
        usage.RecordUsage("agent_u", "user_u1", 10, 20, 0);
        usage.RecordUsage("agent_u", "user_u1", 5, 5, 0);
        usage.RecordUsage("agent_u", "user_u2", 1, 1, 0);

        using var req = Authed(HttpMethod.Get, "/ag-ui/admin/org-health?days=7", token);
        var res = await _client.SendAsync(req);
        res.EnsureSuccessStatusCode();
        var d = await res.Content.ReadFromJsonAsync<JsonElement>();

        var users = d.GetProperty("users").EnumerateArray()
            .ToDictionary(u => u.GetProperty("userId").GetString()!, u => u, StringComparer.Ordinal);
        Assert.True(users.ContainsKey("user_u1"), "users 应含 user_u1");
        Assert.Equal(2, users["user_u1"].GetProperty("calls").GetInt64());
        Assert.Equal(40, users["user_u1"].GetProperty("totalTokens").GetInt64());
        // 无对应账号时名字回退为 userId
        Assert.Equal("user_u1", users["user_u1"].GetProperty("name").GetString());
        Assert.Equal(1, users["user_u2"].GetProperty("calls").GetInt64());
    }

    [Fact]
    public async Task OrgHealth_IncludesPerGroupAggregation()
    {
        var admin = await RegisterAsync("admin_chief");
        var token = admin.GetProperty("token").GetString()!;
        var usage = _fixture.App.Services.GetRequiredService<AguiGroupChat.Hub.Agents.AgentUsageService>();
        usage.RecordUsage("agent_g", "user_g", "group_k1", 100, 50, 0);
        usage.RecordUsage("agent_g", "user_g", "group_k1", 10, 10, 0); // 同键累加
        usage.RecordUsage("agent_g", "user_g", "group_k2", 1, 1, 0);

        using var req = Authed(HttpMethod.Get, "/ag-ui/admin/org-health?days=7", token);
        var res = await _client.SendAsync(req);
        res.EnsureSuccessStatusCode();
        var d = await res.Content.ReadFromJsonAsync<JsonElement>();

        var groups = d.GetProperty("groups").EnumerateArray()
            .ToDictionary(g => g.GetProperty("groupId").GetString()!, g => g, StringComparer.Ordinal);
        Assert.True(groups.ContainsKey("group_k1"), "groups 应含 group_k1");
        Assert.Equal(2, groups["group_k1"].GetProperty("calls").GetInt64());
        Assert.Equal(170, groups["group_k1"].GetProperty("totalTokens").GetInt64());
        // 群不存在时名字回退为 groupId
        Assert.Equal("group_k1", groups["group_k1"].GetProperty("name").GetString());
        Assert.Equal(1, groups["group_k2"].GetProperty("calls").GetInt64());
    }

    [Fact]
    public async Task AgentPerformance_ScopesByGroup()
    {
        var admin = await RegisterAsync("admin_chief");
        var token = admin.GetProperty("token").GetString()!;
        var usage = _fixture.App.Services.GetRequiredService<AguiGroupChat.Hub.Agents.AgentUsageService>();
        usage.RecordUsage("agent_p", "user_p", "group_p1", 100, 50, 0);
        usage.RecordUsage("agent_p", "user_p", "group_p2", 10, 10, 0);

        // 全量：agent_p 跨群共 2 次调用
        using (var all = Authed(HttpMethod.Get, "/ag-ui/admin/agent-performance?days=7", token))
        {
            var d = await (await _client.SendAsync(all)).Content.ReadFromJsonAsync<JsonElement>();
            var row = d.GetProperty("agents").EnumerateArray().First(a => a.GetProperty("agentId").GetString() == "agent_p");
            Assert.Equal(2, row.GetProperty("calls").GetInt64());
        }

        // 知聚范围：只算该群的用量，且反馈列不可用（null）
        using (var scoped = Authed(HttpMethod.Get, "/ag-ui/admin/agent-performance?days=7&groupId=group_p1", token))
        {
            var res = await _client.SendAsync(scoped);
            res.EnsureSuccessStatusCode();
            var d = await res.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("group_p1", d.GetProperty("scope").GetProperty("groupId").GetString());
            var rows = d.GetProperty("agents").EnumerateArray().ToList();
            var row = rows.First(a => a.GetProperty("agentId").GetString() == "agent_p");
            Assert.Equal(1, row.GetProperty("calls").GetInt64());
            Assert.Equal(150, row.GetProperty("totalTokens").GetInt64());
            Assert.Equal(JsonValueKind.Null, row.GetProperty("positiveRate").ValueKind);
            // seedAllAgents=false：不列未在该群使用过的员工（无 0 调用行）
            Assert.DoesNotContain(rows, a => a.GetProperty("calls").GetInt64() == 0);
        }
    }

    [Fact]
    public async Task OrgHealth_RequiresOperator()
    {
        var plain = await RegisterAsync("oh_plain");
        var token = plain.GetProperty("token").GetString()!;
        using var req = Authed(HttpMethod.Get, "/ag-ui/admin/org-health", token);
        Assert.False((await _client.SendAsync(req)).IsSuccessStatusCode);
    }

    // ================= 执行参数：复杂度自适应超时（热改 + 持久化契约） =================

    [Fact]
    public async Task ExecutionRuntime_ComplexityTimeouts_AreExposedAndPatchable()
    {
        var admin = await RegisterAsync("admin_chief");
        var token = admin.GetProperty("token").GetString()!;

        // 1) 读：新嵌套节点必须出现在 DTO 里（否则前端无法回填、也无法热改）
        using var get = Authed(HttpMethod.Get, "/ag-ui/admin/execution", token);
        var before = (await (await _client.SendAsync(get)).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("complexityTimeouts");
        var original = before.GetRawText();
        Assert.True(before.GetProperty("enabled").GetBoolean());
        Assert.Equal(1.5, before.GetProperty("standardMultiplier").GetDouble());
        Assert.Equal(30, before.GetProperty("maxRunTimeoutMinutes").GetInt32());

        try
        {
            // 2) 写：只给一个字段 —— 其余字段必须保持不变（逐字段合并，不能整块重置）
            using var set = Authed(HttpMethod.Post, "/ag-ui/admin/execution", token);
            set.Content = JsonContent.Create(new { complexityTimeouts = new { heavyMultiplier = 4.5 } });
            var patched = (await (await _client.SendAsync(set)).Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("execution").GetProperty("complexityTimeouts");
            Assert.Equal(4.5, patched.GetProperty("heavyMultiplier").GetDouble());
            Assert.Equal(before.GetProperty("standardMultiplier").GetDouble(), patched.GetProperty("standardMultiplier").GetDouble());
            Assert.Equal(before.GetProperty("maxRunTimeoutMinutes").GetInt32(), patched.GetProperty("maxRunTimeoutMinutes").GetInt32());

            // 3) 非法的单字段 → 只回退该项，而不是拒整表
            using var bad = Authed(HttpMethod.Post, "/ag-ui/admin/execution", token);
            bad.Content = JsonContent.Create(new { complexityTimeouts = new { complexMultiplier = 99 } }); // 上界 6
            var clamped = (await (await _client.SendAsync(bad)).Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("execution").GetProperty("complexityTimeouts");
            Assert.Equal(2.0, clamped.GetProperty("complexMultiplier").GetDouble()); // 回退出厂默认 2
            Assert.Equal(4.5, clamped.GetProperty("heavyMultiplier").GetDouble());    // 其余项不受影响
        }
        finally
        {
            // 4) 还原：fixture 内其它用例共用同一份 ExecutionOptions 单例，不留污染
            using var restore = Authed(HttpMethod.Post, "/ag-ui/admin/execution", token);
            restore.Content = new StringContent("{\"complexityTimeouts\":" + original + "}", Encoding.UTF8, "application/json");
            (await _client.SendAsync(restore)).EnsureSuccessStatusCode();
        }
    }

    [Fact]
    public async Task ExecutionRuntime_ComplexityTimeouts_ForbidsNonAdmin()
    {
        var normal = await RegisterAsync("exec_normal_user");
        using var req = Authed(HttpMethod.Post, "/ag-ui/admin/execution", normal.GetProperty("token").GetString()!);
        req.Content = JsonContent.Create(new { complexityTimeouts = new { enabled = false } });
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(req)).StatusCode);
    }

    [Fact]
    public void ExecutionRuntime_LegacySnapshot_DoesNotClobberComplexityTimeouts()
    {
        // 升级安全：本次改动之前落盘的 executionRuntime 快照里根本没有 complexityTimeouts 键。
        // 若无条件赋值，会把 appsettings 里配好的值冲成默认（升级即丢配置）——这是真踩过的性能陷阱，钉住它。
        var exec = new ExecutionOptions
        {
            ComplexityTimeouts = new ComplexityTimeoutOptions { HeavyMultiplier = 4.5, MaxRunTimeoutMinutes = 45 },
        };
        var sections = new CaptureSectionStore();
        var services = new ServiceCollection();
        services.AddSingleton(exec);
        services.AddSingleton<ISectionStore>(sections);
        services.BuildServiceProvider().RegisterExecutionRuntimePersistence();

        var restore = Assert.Single(sections.Restores, r => r.Key == "executionRuntime").Value;

        // 旧快照（无该键）→ 只恢复旧字段，appsettings 里的复杂度配置必须原样保留
        restore(JsonDocument.Parse("{\"streamTimeoutMinutes\":8,\"enableRelay\":false}").RootElement);
        Assert.Equal(8, exec.StreamTimeoutMinutes);
        Assert.False(exec.EnableRelay);
        Assert.Equal(4.5, exec.ComplexityTimeouts.HeavyMultiplier);
        Assert.Equal(45, exec.ComplexityTimeouts.MaxRunTimeoutMinutes);

        // 新快照（带该键）→ 整个节点按快照恢复（快照总是写全字段，故不会丢）
        restore(JsonDocument.Parse("{\"complexityTimeouts\":{\"heavyMultiplier\":6}}").RootElement);
        Assert.Equal(6, exec.ComplexityTimeouts.HeavyMultiplier);
        Assert.Equal(30, exec.ComplexityTimeouts.MaxRunTimeoutMinutes);
    }

    /// <summary>捕获各扩展区注册的读写回调，供无持久化环境下的恢复语义测试。</summary>
    private sealed class CaptureSectionStore : ISectionStore
    {
        public Dictionary<string, Action<JsonElement>> Restores { get; } = new(StringComparer.Ordinal);
        public void AddSection(string name, Func<object?> snapshot, Action<JsonElement> restore) => Restores[name] = restore;
        public void LoadSections() { }
        public void Flush() { }
    }

    [Fact]
    public async Task AdminUsers_ListDisableEnableResetPassword()
    {
        // 管理员（配置名单）与目标用户
        var admin = await RegisterAsync("admin_chief");
        var adminToken = admin.GetProperty("token").GetString()!;
        var target = await RegisterAsync("target_user");
        var targetId = target.GetProperty("userId").GetString()!;
        var targetToken = target.GetProperty("token").GetString()!;

        // 用户列表（管理员）包含禁用状态
        using var listReq = Authed(HttpMethod.Get, "/ag-ui/admin/users", adminToken);
        var list = (await (await _client.SendAsync(listReq)).Content.ReadFromJsonAsync<JsonElement[]>() ?? []);
        var targetEntry = Assert.Single(list, u => u.GetProperty("username").GetString() == "target_user");
        Assert.False(targetEntry.GetProperty("isDisabled").GetBoolean());

        // 非管理员访问 → 403
        var outsider = await RegisterAsync("plain_user");
        var outsiderToken = outsider.GetProperty("token").GetString()!;
        using var denied = Authed(HttpMethod.Get, "/ag-ui/admin/users", outsiderToken);
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(denied)).StatusCode);

        // 禁用目标：旧令牌立即失效 + 无法再登录
        using var disableReq = Authed(HttpMethod.Post, $"/ag-ui/admin/users/{targetId}/disabled", adminToken);
        disableReq.Content = JsonContent.Create(new { disabled = true });
        (await _client.SendAsync(disableReq)).EnsureSuccessStatusCode();
        using var staleMe = Authed(HttpMethod.Get, "/ag-ui/user/me", targetToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.SendAsync(staleMe)).StatusCode);
        var deniedLogin = await _client.PostAsJsonAsync("/ag-ui/user/login", new { username = "target_user", password = "secret1" });
        Assert.Equal(HttpStatusCode.Unauthorized, deniedLogin.StatusCode);

        // 管理员不能禁用自己
        using var selfDisable = Authed(HttpMethod.Post, $"/ag-ui/admin/users/{admin.GetProperty("userId").GetString()}/disabled", adminToken);
        selfDisable.Content = JsonContent.Create(new { disabled = true });
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.SendAsync(selfDisable)).StatusCode);

        // 启用：可再次登录
        using var enableReq = Authed(HttpMethod.Post, $"/ag-ui/admin/users/{targetId}/disabled", adminToken);
        enableReq.Content = JsonContent.Create(new { disabled = false });
        (await _client.SendAsync(enableReq)).EnsureSuccessStatusCode();
        var relogin = await _client.PostAsJsonAsync("/ag-ui/user/login", new { username = "target_user", password = "secret1" });
        relogin.EnsureSuccessStatusCode();

        // 重置密码：旧密码失效、新密码可登录
        using var pwReq = Authed(HttpMethod.Post, $"/ag-ui/admin/users/{targetId}/password", adminToken);
        pwReq.Content = JsonContent.Create(new { newPassword = "brand-new-pass" });
        (await _client.SendAsync(pwReq)).EnsureSuccessStatusCode();
        var oldPw = await _client.PostAsJsonAsync("/ag-ui/user/login", new { username = "target_user", password = "secret1" });
        Assert.Equal(HttpStatusCode.Unauthorized, oldPw.StatusCode);
        var newPw = await _client.PostAsJsonAsync("/ag-ui/user/login", new { username = "target_user", password = "brand-new-pass" });
        newPw.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task AdminUsage_ReturnsDailySummary()
    {
        var admin = await RegisterAsync("admin_chief");
        var token = admin.GetProperty("token").GetString()!;
        // 直接记录几条用量（真实调用由 AgentGateway 在流式结束时写入）
        var usage = _fixture.App.Services.GetRequiredService<AguiGroupChat.Hub.Agents.AgentUsageService>();
        // 用量 store 是本夹具共享的单例，其它用例也会写入 → 断言用**增量**而非绝对值（否则依赖执行顺序）
        var todayBefore = usage.GetDailySummary(7).FirstOrDefault(x => x.Date == AguiGroupChat.Hub.Agents.AgentUsageService.Today());
        var tokenBefore = todayBefore?.TotalTokens ?? 0;
        var callsBefore = todayBefore?.Calls ?? 0;
        usage.RecordUsage("agent_x", "user_a", 100, 50, 10);
        usage.RecordUsage("agent_y", "user_a", 20, 30, 0);

        using var req = Authed(HttpMethod.Get, "/ag-ui/admin/usage?days=7", token);
        var res = await _client.SendAsync(req);
        res.EnsureSuccessStatusCode();
        var d = await res.Content.ReadFromJsonAsync<JsonElement>();
        var days = d.GetProperty("days").EnumerateArray().ToList();
        var today = Assert.Single(days, x => x.GetProperty("date").GetString() == AguiGroupChat.Hub.Agents.AgentUsageService.Today());
        Assert.Equal(tokenBefore + 210, today.GetProperty("totalTokens").GetInt64());
        Assert.Equal(callsBefore + 2, today.GetProperty("calls").GetInt64());

        // 非管理员 → 403
        var outsider = await RegisterAsync("usage_outsider");
        using var denied = Authed(HttpMethod.Get, "/ag-ui/admin/usage", outsider.GetProperty("token").GetString()!);
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(denied)).StatusCode);
    }

    [Fact]
    public async Task QuotaStatus_ReportsWarnAndOverLevelsForGroups()
    {
        var admin = await RegisterAsync("admin_chief");
        var token = admin.GetProperty("token").GetString()!;
        var usage = _fixture.App.Services.GetRequiredService<AguiGroupChat.Hub.Agents.AgentUsageService>();

        // 启用知聚配额（100）→ 80 为 warn、100 为 over；完事还原为 0，避免影响同类其它用例
        using (var set = Authed(HttpMethod.Post, "/ag-ui/admin/config", token))
        {
            set.Content = JsonContent.Create(new { dailyTokenQuotaPerGroup = 100 });
            (await _client.SendAsync(set)).EnsureSuccessStatusCode();
        }
        try
        {
            usage.RecordUsage("agent_q", "user_q", "group_qw", 60, 20, 0); // 80 → warn（阈值含）
            usage.RecordUsage("agent_q", "user_q", "group_qo", 60, 40, 0); // 100 → over
            usage.RecordUsage("agent_q", "user_q", "group_qk", 10, 10, 0); // 20 → ok

            using var req = Authed(HttpMethod.Get, "/ag-ui/admin/quota-status", token);
            var res = await _client.SendAsync(req);
            res.EnsureSuccessStatusCode();
            var d = await res.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(100, d.GetProperty("quotaPerGroup").GetInt64());
            Assert.Equal(0.8, d.GetProperty("warnRatio").GetDouble(), 3);

            var levels = d.GetProperty("groups").EnumerateArray()
                .ToDictionary(x => x.GetProperty("groupId").GetString()!, x => x.GetProperty("level").GetString()!, StringComparer.Ordinal);
            Assert.Equal("warn", levels["group_qw"]);
            Assert.Equal("over", levels["group_qo"]);
            Assert.Equal("ok", levels["group_qk"]);

            // /usage 的 groupsToday 同口径带 level（单一阈值来源）
            using var u = Authed(HttpMethod.Get, "/ag-ui/admin/usage?days=7", token);
            var ud = await (await _client.SendAsync(u)).Content.ReadFromJsonAsync<JsonElement>();
            var row = ud.GetProperty("groupsToday").EnumerateArray()
                .Single(x => x.GetProperty("groupId").GetString() == "group_qo");
            Assert.Equal("over", row.GetProperty("level").GetString());

            // 非管理员 → 403（配额 / 成本属运维信息）
            var outsider = await RegisterAsync("quota_outsider");
            using var denied = Authed(HttpMethod.Get, "/ag-ui/admin/quota-status", outsider.GetProperty("token").GetString()!);
            Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(denied)).StatusCode);
        }
        finally
        {
            using var restore = Authed(HttpMethod.Post, "/ag-ui/admin/config", token);
            restore.Content = JsonContent.Create(new { dailyTokenQuotaPerGroup = 0 });
            await _client.SendAsync(restore);
        }
    }

    [Fact]
    public async Task GroupQuotaOverride_WinsInUsageAndQuotaStatus()
    {
        var admin = await RegisterAsync("admin_chief");
        var token = admin.GetProperty("token").GetString()!;
        var userId = admin.GetProperty("userId").GetString()!;
        var usage = _fixture.App.Services.GetRequiredService<AguiGroupChat.Hub.Agents.AgentUsageService>();

        using var create = Authed(HttpMethod.Post, "/ag-ui/group/create", token);
        create.Content = JsonContent.Create(new { groupName = "配额覆写知聚", ownerId = userId, memberIds = new[] { userId } });
        var gid = (await (await _client.SendAsync(create)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("groupId").GetString()!;
        usage.RecordUsage("agent_o", "user_o", gid, 40, 20, 0); // 60 tokens

        // 管理员给该知聚设覆写 50 → 今日 60 已超；全局值（默认 0）不变
        using (var set = Authed(HttpMethod.Post, "/ag-ui/group/update", token))
        {
            set.Content = JsonContent.Create(new
            {
                groupId = gid,
                operatorId = userId,
                updateFields = new[] { "quotaPerGroup" },
                groupInfo = new Dictionary<string, object?> { ["quotaPerGroup"] = 50 },
            });
            (await _client.SendAsync(set)).EnsureSuccessStatusCode();
        }

        // 群列表回读覆写（供设置弹窗回显）
        using (var list = Authed(HttpMethod.Get, $"/ag-ui/member/{userId}/groups", token))
        {
            var gs = await (await _client.SendAsync(list)).Content.ReadFromJsonAsync<JsonElement>();
            var row = gs.EnumerateArray().Single(x => x.GetProperty("groupId").GetString() == gid);
            Assert.Equal(50, row.GetProperty("quotaOverride").GetInt64());
        }

        // /usage 的 groupsToday 该行：分母用**本知聚生效配额** 50（而非全局 0），水位 over
        using (var u = Authed(HttpMethod.Get, "/ag-ui/admin/usage?days=7", token))
        {
            var ud = await (await _client.SendAsync(u)).Content.ReadFromJsonAsync<JsonElement>();
            var row = ud.GetProperty("groupsToday").EnumerateArray().Single(x => x.GetProperty("groupId").GetString() == gid);
            Assert.Equal(50, row.GetProperty("quota").GetInt64());
            Assert.Equal("over", row.GetProperty("level").GetString());
        }

        // /quota-status 同口径（侧边栏角标来源）
        using (var q = Authed(HttpMethod.Get, "/ag-ui/admin/quota-status", token))
        {
            var qd = await (await _client.SendAsync(q)).Content.ReadFromJsonAsync<JsonElement>();
            var row = qd.GetProperty("groups").EnumerateArray().Single(x => x.GetProperty("groupId").GetString() == gid);
            Assert.Equal("over", row.GetProperty("level").GetString());
        }

        // 成本控制类覆写必须留痕（审计页可按 action 过滤）
        using (var aud = Authed(HttpMethod.Get, "/ag-ui/admin/audit?action=group.quotaOverride&limit=20", token))
        {
            var ad = await (await _client.SendAsync(aud)).Content.ReadFromJsonAsync<JsonElement>();
            Assert.Contains(ad.GetProperty("entries").EnumerateArray(), e =>
                e.GetProperty("action").GetString() == "group.quotaOverride"
                && e.GetProperty("targetId").GetString() == gid);
        }

        // 普通用户即群主也不能设（成本控制属平台管理员）→ 403
        var owner = await RegisterAsync("grp_quota_owner");
        var oToken = owner.GetProperty("token").GetString()!;
        var oId = owner.GetProperty("userId").GetString()!;
        using (var create2 = Authed(HttpMethod.Post, "/ag-ui/group/create", oToken))
        {
            create2.Content = JsonContent.Create(new { groupName = "配额覆写-普通", ownerId = oId });
            var gid2 = (await (await _client.SendAsync(create2)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("groupId").GetString()!;
            using var deny = Authed(HttpMethod.Post, "/ag-ui/group/update", oToken);
            deny.Content = JsonContent.Create(new
            {
                groupId = gid2,
                operatorId = oId,
                updateFields = new[] { "quotaPerGroup" },
                groupInfo = new Dictionary<string, object?> { ["quotaPerGroup"] = 999999 },
            });
            Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(deny)).StatusCode);
        }

        // 还原：清覆写（空串）
        using var clear = Authed(HttpMethod.Post, "/ag-ui/group/update", token);
        clear.Content = JsonContent.Create(new
        {
            groupId = gid,
            operatorId = userId,
            updateFields = new[] { "quotaPerGroup" },
            groupInfo = new Dictionary<string, object?> { ["quotaPerGroup"] = "" },
        });
        (await _client.SendAsync(clear)).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task GroupQuotaOverride_WithExpiry_ReturnsUntilThenFallsBackOnExpiry()
    {
        var admin = await RegisterAsync("admin_chief");
        var token = admin.GetProperty("token").GetString()!;
        var userId = admin.GetProperty("userId").GetString()!;
        var usage = _fixture.App.Services.GetRequiredService<AguiGroupChat.Hub.Agents.AgentUsageService>();

        using var create = Authed(HttpMethod.Post, "/ag-ui/group/create", token);
        create.Content = JsonContent.Create(new { groupName = "临时放额知聚", ownerId = userId, memberIds = new[] { userId } });
        var gid = (await (await _client.SendAsync(create)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("groupId").GetString()!;
        usage.RecordUsage("agent_t", "user_t", gid, 40, 20, 0); // 60 tokens

        var future = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds();
        using (var set = Authed(HttpMethod.Post, "/ag-ui/group/update", token))
        {
            set.Content = JsonContent.Create(new
            {
                groupId = gid,
                operatorId = userId,
                updateFields = new[] { "quotaPerGroup", "quotaPerGroupUntil" },
                groupInfo = new Dictionary<string, object?> { ["quotaPerGroup"] = 50, ["quotaPerGroupUntil"] = future },
            });
            (await _client.SendAsync(set)).EnsureSuccessStatusCode();
        }

        // 生效中：群列表回读到到期时间；/usage 行带 quotaUntil，分母用覆写值 50 → over
        using (var list = Authed(HttpMethod.Get, $"/ag-ui/member/{userId}/groups", token))
        {
            var gs = await (await _client.SendAsync(list)).Content.ReadFromJsonAsync<JsonElement>();
            var row = gs.EnumerateArray().Single(x => x.GetProperty("groupId").GetString() == gid);
            Assert.Equal(50, row.GetProperty("quotaOverride").GetInt64());
            Assert.Equal(future, row.GetProperty("quotaOverrideUntil").GetInt64());
        }
        using (var u = Authed(HttpMethod.Get, "/ag-ui/admin/usage?days=7", token))
        {
            var ud = await (await _client.SendAsync(u)).Content.ReadFromJsonAsync<JsonElement>();
            var row = ud.GetProperty("groupsToday").EnumerateArray().Single(x => x.GetProperty("groupId").GetString() == gid);
            Assert.Equal(50, row.GetProperty("quota").GetInt64());
            Assert.Equal(future, row.GetProperty("quotaUntil").GetInt64());
            Assert.Equal("over", row.GetProperty("level").GetString());
        }

        // 把到期时间改到过去 → 生效判定立刻失效（惰性，无后台任务）：回退全局配额，行上不再给 quotaUntil
        var past = DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeMilliseconds();
        using (var expire = Authed(HttpMethod.Post, "/ag-ui/group/update", token))
        {
            expire.Content = JsonContent.Create(new
            {
                groupId = gid,
                operatorId = userId,
                updateFields = new[] { "quotaPerGroupUntil" },
                groupInfo = new Dictionary<string, object?> { ["quotaPerGroupUntil"] = past },
            });
            (await _client.SendAsync(expire)).EnsureSuccessStatusCode();
        }
        using (var u2 = Authed(HttpMethod.Get, "/ag-ui/admin/usage?days=7", token))
        {
            var ud = await (await _client.SendAsync(u2)).Content.ReadFromJsonAsync<JsonElement>();
            var row = ud.GetProperty("groupsToday").EnumerateArray().Single(x => x.GetProperty("groupId").GetString() == gid);
            Assert.Equal(0, row.GetProperty("quota").GetInt64()); // 全局默认 0 = 不限
            Assert.Equal(JsonValueKind.Null, row.GetProperty("quotaUntil").ValueKind);
            Assert.Equal("ok", row.GetProperty("level").GetString());
        }
        // 存储里的旧值保留：管理员仍能看到当时设了多少（额度与到期时间都还在）
        using (var list2 = Authed(HttpMethod.Get, $"/ag-ui/member/{userId}/groups", token))
        {
            var gs = await (await _client.SendAsync(list2)).Content.ReadFromJsonAsync<JsonElement>();
            var row = gs.EnumerateArray().Single(x => x.GetProperty("groupId").GetString() == gid);
            Assert.Equal(50, row.GetProperty("quotaOverride").GetInt64());
            Assert.Equal(past, row.GetProperty("quotaOverrideUntil").GetInt64());
        }
        // 审计留痕含到期时间（运维复盘「谁在何时放了多大的额、何时到期」）
        using (var aud = Authed(HttpMethod.Get, "/ag-ui/admin/audit?action=group.quotaOverride&limit=20", token))
        {
            var ad = await (await _client.SendAsync(aud)).Content.ReadFromJsonAsync<JsonElement>();
            Assert.Contains(ad.GetProperty("entries").EnumerateArray(), e =>
                e.GetProperty("targetId").GetString() == gid
                && (e.GetProperty("detail").GetString() ?? "").Contains("有效期至"));
        }
        // detail 子串过滤：审计页「⏰ 临时放额」快捷筛选的服务端能力（返回行必须全部命中）
        using (var aud2 = Authed(HttpMethod.Get,
                   $"/ag-ui/admin/audit?action=group.quotaOverride&detail={Uri.EscapeDataString("有效期至")}&limit=20", token))
        {
            var ad = await (await _client.SendAsync(aud2)).Content.ReadFromJsonAsync<JsonElement>();
            var rows = ad.GetProperty("entries").EnumerateArray().ToList();
            Assert.NotEmpty(rows);
            Assert.All(rows, e => Assert.Contains("有效期至", e.GetProperty("detail").GetString() ?? ""));
            Assert.Contains(rows, e => e.GetProperty("targetId").GetString() == gid);
        }

        // 还原：清覆写
        using var clear = Authed(HttpMethod.Post, "/ag-ui/group/update", token);
        clear.Content = JsonContent.Create(new
        {
            groupId = gid,
            operatorId = userId,
            updateFields = new[] { "quotaPerGroup" },
            groupInfo = new Dictionary<string, object?> { ["quotaPerGroup"] = "" },
        });
        (await _client.SendAsync(clear)).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task AdminUsage_UsersToday_ReportsLevelsAndExemptsSystem()
    {
        var admin = await RegisterAsync("admin_chief");
        var token = admin.GetProperty("token").GetString()!;
        var usage = _fixture.App.Services.GetRequiredService<AguiGroupChat.Hub.Agents.AgentUsageService>();
        const string uid = "user_quota_wave"; // 唯一，避免与其它用例串
        var sid = AguiGroupChat.Hub.Agents.AgentUsageService.SystemUserId;

        // 个人配额 50 → uid 用量 60 应 over；system 用量 200 但**豁免** → 恒 ok
        using (var set = Authed(HttpMethod.Post, "/ag-ui/admin/config", token))
        {
            set.Content = JsonContent.Create(new { dailyTokenQuotaPerUser = 50 });
            (await _client.SendAsync(set)).EnsureSuccessStatusCode();
        }
        try
        {
            usage.RecordUsage("agent_w", uid, 40, 20, 0);   // 60
            usage.RecordUsage("agent_w", sid, 100, 100, 0); // 200，但 system 不受个人配额约束

            using var req = Authed(HttpMethod.Get, "/ag-ui/admin/usage?days=7", token);
            var d = await (await _client.SendAsync(req)).Content.ReadFromJsonAsync<JsonElement>();
            var rows = d.GetProperty("usersToday").EnumerateArray()
                .ToDictionary(x => x.GetProperty("userId").GetString()!, x => x, StringComparer.Ordinal);

            Assert.True(rows.ContainsKey(uid), "usersToday 应含该触发者");
            Assert.Equal(50, rows[uid].GetProperty("quota").GetInt64());
            Assert.Equal("over", rows[uid].GetProperty("level").GetString());
            Assert.False(rows[uid].GetProperty("exempt").GetBoolean());
            // 无对应账号时名字回退为 userId
            Assert.Equal(uid, rows[uid].GetProperty("name").GetString());

            Assert.True(rows.ContainsKey(sid), "usersToday 应含 system（供看到定时任务消耗）");
            Assert.True(rows[sid].GetProperty("exempt").GetBoolean());
            Assert.Equal("ok", rows[sid].GetProperty("level").GetString()); // 豁免：不报「已超限」
        }
        finally
        {
            using var restore = Authed(HttpMethod.Post, "/ag-ui/admin/config", token);
            restore.Content = JsonContent.Create(new { dailyTokenQuotaPerUser = 0 });
            await _client.SendAsync(restore);
        }
    }

    [Fact]
    public async Task AdminUsers_IncludesTodayQuotaLevel()
    {
        var admin = await RegisterAsync("admin_chief");
        var token = admin.GetProperty("token").GetString()!;
        var uid = admin.GetProperty("userId").GetString()!;
        var usage = _fixture.App.Services.GetRequiredService<AguiGroupChat.Hub.Agents.AgentUsageService>();

        // 个人配额 50 → 该用户再记 60 tokens 应 over；用**增量**断言，避免与其它用例共享 store 时依赖顺序
        using (var set = Authed(HttpMethod.Post, "/ag-ui/admin/config", token))
        {
            set.Content = JsonContent.Create(new { dailyTokenQuotaPerUser = 50 });
            (await _client.SendAsync(set)).EnsureSuccessStatusCode();
        }
        try
        {
            using (var before = Authed(HttpMethod.Get, "/ag-ui/admin/users", token))
            {
                var rows = await (await _client.SendAsync(before)).Content.ReadFromJsonAsync<JsonElement>();
                var me = rows.EnumerateArray().Single(x => x.GetProperty("userId").GetString() == uid);
                var beforeTokens = me.GetProperty("todayTokens").GetInt64();

                usage.RecordUsage("agent_uq", uid, 40, 20, 0); // +60

                using var after = Authed(HttpMethod.Get, "/ag-ui/admin/users", token);
                var rows2 = await (await _client.SendAsync(after)).Content.ReadFromJsonAsync<JsonElement>();
                var me2 = rows2.EnumerateArray().Single(x => x.GetProperty("userId").GetString() == uid);
                Assert.Equal(beforeTokens + 60, me2.GetProperty("todayTokens").GetInt64());
                Assert.Equal(50, me2.GetProperty("quota").GetInt64());
                // 总量 >= 50 → over（该用户当天无法触发）
                Assert.Equal("over", me2.GetProperty("quotaLevel").GetString());
            }
        }
        finally
        {
            using var restore = Authed(HttpMethod.Post, "/ag-ui/admin/config", token);
            restore.Content = JsonContent.Create(new { dailyTokenQuotaPerUser = 0 });
            await _client.SendAsync(restore);
        }
    }

    [Fact]
    public async Task AdminUsage_IncludesGroupsTodayWithNames()
    {
        var admin = await RegisterAsync("admin_chief");
        var token = admin.GetProperty("token").GetString()!;
        var userId = admin.GetProperty("userId").GetString()!;

        // 建一个真实知聚 → 记录该知的用量，验证「今日各知聚明细」能解出名字（而非回退 groupId）
        using var create = Authed(HttpMethod.Post, "/ag-ui/group/create", token);
        create.Content = JsonContent.Create(new { groupName = "用量明细知聚", ownerId = userId, memberIds = new[] { userId } });
        var groupId = (await (await _client.SendAsync(create)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("groupId").GetString()!;

        var usage = _fixture.App.Services.GetRequiredService<AguiGroupChat.Hub.Agents.AgentUsageService>();
        usage.RecordUsage("agent_gt", "user_gt", groupId, 100, 50, 10);

        using var req = Authed(HttpMethod.Get, "/ag-ui/admin/usage?days=7", token);
        var res = await _client.SendAsync(req);
        res.EnsureSuccessStatusCode();
        var d = await res.Content.ReadFromJsonAsync<JsonElement>();
        var row = d.GetProperty("groupsToday").EnumerateArray().Single(x => x.GetProperty("groupId").GetString() == groupId);
        Assert.Equal("用量明细知聚", row.GetProperty("name").GetString());
        Assert.Equal(160, row.GetProperty("tokens").GetInt64());
        Assert.Equal(1, row.GetProperty("calls").GetInt64());
    }

    [Fact]
    public async Task AdminStatus_ReturnsCounters()
    {
        var admin = await RegisterAsync("admin_chief");
        var token = admin.GetProperty("token").GetString()!;
        using var req = Authed(HttpMethod.Get, "/ag-ui/admin/status", token);
        var res = await _client.SendAsync(req);
        res.EnsureSuccessStatusCode();
        var d = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ok", d.GetProperty("status").GetString());
        Assert.True(d.GetProperty("users").GetInt32() >= 1);
        Assert.True(d.TryGetProperty("connections", out _));
        Assert.True(d.TryGetProperty("messages", out _));
        Assert.True(d.TryGetProperty("dotnetVersion", out _));
        // 文档在线转换能力：本宿主注册了「可用」的转换器 → 应报就绪（`converter` 为具体实现名）
        Assert.True(d.TryGetProperty("docPreview", out var dp));
        Assert.True(dp.GetProperty("available").GetBoolean());
        Assert.Equal("libreoffice", dp.GetProperty("converter").GetString());
    }

    [Fact]
    public async Task AdminConfig_ReturnsReadOnlySnapshot_OnlyForAdmin()
    {
        var admin = await RegisterAsync("admin_chief");
        var token = admin.GetProperty("token").GetString()!;

        using var req = Authed(HttpMethod.Get, "/ag-ui/admin/config", token);
        var res = await _client.SendAsync(req);
        res.EnsureSuccessStatusCode();
        var d = await res.Content.ReadFromJsonAsync<JsonElement>();
        // 关键运维配置均可见
        Assert.True(d.TryGetProperty("auth", out var auth) && auth.TryGetProperty("requireTokenOnRealTime", out _));
        Assert.Equal("memory", d.GetProperty("storage").GetProperty("provider").GetString());
        Assert.True(d.TryGetProperty("agents", out var agents) && agents.TryGetProperty("provider", out _));
        Assert.True(d.TryGetProperty("agents", out var ag) && ag.TryGetProperty("memory", out _));
        Assert.True(d.GetProperty("auth").GetProperty("hasAdminUserIds").GetBoolean());

        // 非管理员 → 403
        var outsider = await RegisterAsync("config_outsider");
        using var denied = Authed(HttpMethod.Get, "/ag-ui/admin/config", outsider.GetProperty("token").GetString()!);
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(denied)).StatusCode);
    }

    [Fact]
    public async Task MessagesAround_ReturnsTargetWithContext_OnlyForMembers()
    {
        var user = await RegisterAsync("around_user");
        var token = user.GetProperty("token").GetString()!;
        var userId = user.GetProperty("userId").GetString()!;

        // 建群 + 发三条消息
        var create = await _client.PostAsJsonAsync("/ag-ui/group/create", new { groupName = "定位群", ownerId = userId, memberIds = new[] { userId } });
        create.EnsureSuccessStatusCode();
        var groupId = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("groupId").GetString()!;
        await _client.PostAsJsonAsync("/ag-ui/group/message/send", new { groupId, userId, content = "第一条", timestamp = 1000L });
        var targetRes = await _client.PostAsJsonAsync("/ag-ui/group/message/send", new { groupId, userId, content = "目标消息", timestamp = 2000L });
        var targetId = (await targetRes.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("messageId").GetString()!;
        await _client.PostAsJsonAsync("/ag-ui/group/message/send", new { groupId, userId, content = "第三条", timestamp = 3000L });

        // around：目标消息前后各 1 条 → 返回 3 条（含目标），按时间序
        using var around = Authed(HttpMethod.Get, $"/ag-ui/group/{groupId}/messages/around?messageId={Uri.EscapeDataString(targetId)}&count=4", token);
        var res = await _client.SendAsync(around);
        res.EnsureSuccessStatusCode();
        var msgs = (await res.Content.ReadFromJsonAsync<JsonElement[]>() ?? []);
        Assert.Equal(3, msgs.Length);
        Assert.Equal(targetId, msgs[1].GetProperty("messageId").GetString());

        // 消息不存在 → 404
        using var missing = Authed(HttpMethod.Get, $"/ag-ui/group/{groupId}/messages/around?messageId=nope", token);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.SendAsync(missing)).StatusCode);

        // 非成员 → 403
        var outsider = await RegisterAsync("around_outsider");
        using var denied = Authed(HttpMethod.Get, $"/ag-ui/group/{groupId}/messages/around?messageId={Uri.EscapeDataString(targetId)}", outsider.GetProperty("token").GetString()!);
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(denied)).StatusCode);
    }

    [Fact]
    public async Task TopicsRelated_ReturnsRelatedBySharedKeywords_OnlyForMembers()
    {
        var user = await RegisterAsync("related_user");
        var token = user.GetProperty("token").GetString()!;
        var userId = user.GetProperty("userId").GetString()!;

        var create = await _client.PostAsJsonAsync("/ag-ui/group/create", new { groupName = "关联群", ownerId = userId, memberIds = new[] { userId } });
        create.EnsureSuccessStatusCode();
        var groupId = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("groupId").GetString()!;

        // 主话题（main）与新建话题 t2 都讨论「数据库选型」，含共享关键词
        await _client.PostAsJsonAsync("/ag-ui/group/message/send", new { groupId, userId, topicId = "main", content = "讨论数据库选型，考虑 Postgres 与 MySQL" });
        var tRes = await _client.PostAsJsonAsync("/ag-ui/group/topic/create", new { groupId, operatorId = userId, name = "存储" });
        tRes.EnsureSuccessStatusCode();
        var t2Id = (await tRes.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("topicId").GetString()!;
        await _client.PostAsJsonAsync("/ag-ui/group/message/send", new { groupId, userId, topicId = t2Id, content = "数据库选型：Postgres 更稳定，缓存用 Redis" });
        await _client.PostAsJsonAsync("/ag-ui/group/message/send", new { groupId, userId, topicId = "main", content = "今天天气不错" });

        using var req = Authed(HttpMethod.Get, $"/ag-ui/group/{groupId}/topics/related?topicId={Uri.EscapeDataString("main")}", token);
        var res = await _client.SendAsync(req);
        res.EnsureSuccessStatusCode();
        var d = await res.Content.ReadFromJsonAsync<JsonElement>();
        var related = d.GetProperty("related");
        Assert.True(related.GetArrayLength() >= 1, "应返回至少一个相关话题");
        Assert.Contains(related.EnumerateArray(), r => r.GetProperty("topicId").GetString() == t2Id);

        // 非成员 → 403
        var outsider = await RegisterAsync("related_outsider");
        using var denied = Authed(HttpMethod.Get, $"/ag-ui/group/{groupId}/topics/related?topicId=main", outsider.GetProperty("token").GetString()!);
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(denied)).StatusCode);
    }

    [Fact]
    public async Task GroupModelOverride_RequiresPlatformAdmin()
    {
        // 普通用户即使是群主也不能设置知聚模型覆写（成本 / 容量控制属平台管理员）→ 403
        var owner = await RegisterAsync("grp_model_owner");
        var oToken = owner.GetProperty("token").GetString()!;
        var oId = owner.GetProperty("userId").GetString()!;

        using var create = Authed(HttpMethod.Post, "/ag-ui/group/create", oToken);
        create.Content = JsonContent.Create(new { groupName = "模型覆写-普通", ownerId = oId });
        var gid = (await (await _client.SendAsync(create)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("groupId").GetString()!;

        using var deny = Authed(HttpMethod.Post, "/ag-ui/group/update", oToken);
        deny.Content = JsonContent.Create(new
        {
            groupId = gid,
            operatorId = oId,
            updateFields = new[] { "model" },
            groupInfo = new Dictionary<string, object?> { ["model"] = "deepseek-v4-pro" },
        });
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(deny)).StatusCode);

        // 平台管理员（且为群主）设置成功，并在响应里回读 modelOverride
        var admin = await RegisterAsync("admin_chief");
        var aToken = admin.GetProperty("token").GetString()!;
        var aId = admin.GetProperty("userId").GetString()!;
        using var create2 = Authed(HttpMethod.Post, "/ag-ui/group/create", aToken);
        create2.Content = JsonContent.Create(new { groupName = "模型覆写-管理", ownerId = aId });
        var gid2 = (await (await _client.SendAsync(create2)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("groupId").GetString()!;

        using var ok = Authed(HttpMethod.Post, "/ag-ui/group/update", aToken);
        ok.Content = JsonContent.Create(new
        {
            groupId = gid2,
            operatorId = aId,
            updateFields = new[] { "model" },
            groupInfo = new Dictionary<string, object?> { ["model"] = "deepseek-v4-pro" },
        });
        var okRes = await _client.SendAsync(ok);
        okRes.EnsureSuccessStatusCode();
        var g = await okRes.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("deepseek-v4-pro", g.GetProperty("modelOverride").GetString());
    }

    [Fact]
    public async Task ConfigGovernance_Post_AdminUpdatesAndPersists()
    {
        var admin = await RegisterAsync("admin_chief"); // 配置名单管理员
        var token = admin.GetProperty("token").GetString()!;

        using var set = Authed(HttpMethod.Post, "/ag-ui/admin/config", token);
        set.Content = JsonContent.Create(new
        {
            sessionTtlHours = 48,
            messageHistoryLimit = 2000,
            maxGroupMembers = 300,
            enableWebTools = true,
            requireApprovalToolNames = new[] { "publish_announcement", "deploy" },
            allowedFrameOrigins = new[] { "https://portal.example.com" },
            modelRoutingEnabled = true,
            modelRoutingSimpleModel = "deepseek-flash",
            modelRoutingStandardModel = "deepseek-flash",
            modelRoutingHeavyModel = "deepseek-v4-pro",
            // 记忆与向量化（运行时热改，保存即生效）
            memoryMaxQueryChars = 640,
            memoryMaxWriteChars = 960,
            memoryInteractiveEmbeddingWaitSeconds = 20,
            memoryBackgroundEmbeddingWaitSeconds = 90,
            memoryWriteEmbeddingCacheSize = 256,
            // token 配额（热改）：按用户与按知聚
            dailyTokenQuotaPerUser = 12345,
            dailyTokenQuotaPerGroup = 6789,
        });
        var setRes = await _client.SendAsync(set);
        setRes.EnsureSuccessStatusCode();

        using var get = Authed(HttpMethod.Get, "/ag-ui/admin/config/governance", token);
        var d = await (await _client.SendAsync(get)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(48, d.GetProperty("sessionTtlHours").GetInt32());
        Assert.Equal(2000, d.GetProperty("messageHistoryLimit").GetInt32());
        Assert.Equal(300, d.GetProperty("maxGroupMembers").GetInt32());
        Assert.Contains(d.GetProperty("requireApprovalToolNames").EnumerateArray(), x => x.GetString() == "deploy");
        Assert.Contains(d.GetProperty("allowedFrameOrigins").EnumerateArray(), x => x.GetString() == "https://portal.example.com");
        // 模型路由（6.7）：启用 + 各档模型名可在线保存与回读
        Assert.True(d.GetProperty("modelRoutingEnabled").GetBoolean());
        Assert.Equal("deepseek-flash", d.GetProperty("modelRoutingStandardModel").GetString());
        Assert.Equal("deepseek-v4-pro", d.GetProperty("modelRoutingHeavyModel").GetString());
        // 记忆与向量化：回读一致，且真的落到运行时单例 AgentOptions.Memory（保存即生效）
        Assert.Equal(640, d.GetProperty("memoryMaxQueryChars").GetInt32());
        Assert.Equal(960, d.GetProperty("memoryMaxWriteChars").GetInt32());
        Assert.Equal(256, d.GetProperty("memoryWriteEmbeddingCacheSize").GetInt32());
        var agents = _fixture.App.Services.GetRequiredService<AgentOptions>();
        Assert.Equal(640, agents.Memory.MaxQueryChars);
        Assert.Equal(960, agents.Memory.MaxWriteChars);
        Assert.Equal(20, agents.Memory.InteractiveEmbeddingWaitSeconds);
        Assert.Equal(90, agents.Memory.BackgroundEmbeddingWaitSeconds);
        Assert.Equal(256, agents.Memory.WriteEmbeddingCacheSize);

        // token 配额：回读一致 + 落到运行时 AgentOptions
        Assert.Equal(12345, d.GetProperty("dailyTokenQuotaPerUser").GetInt64());
        Assert.Equal(6789, d.GetProperty("dailyTokenQuotaPerGroup").GetInt64());
        Assert.Equal(12345, agents.DailyTokenQuotaPerUser);
        Assert.Equal(6789, agents.DailyTokenQuotaPerGroup);
        // 关键回归：AgentUsageService 必须**实时**读到新配额，而不是构造时拷贝的旧值
        // （旧实现把配额拷贝成字段 → 热改只重启后才生效，文档却写「热改持久」）。
        var usageSvc = _fixture.App.Services.GetRequiredService<AguiGroupChat.Hub.Agents.AgentUsageService>();
        Assert.Equal(12345, usageSvc.DailyQuotaPerUser);
        Assert.Equal(6789, usageSvc.DailyQuotaPerGroup);
    }

    [Fact]
    public async Task ConfigGovernance_Post_RejectsInvalidAndForbidsNonAdmin()
    {
        var admin = await RegisterAsync("admin_chief");
        var token = admin.GetProperty("token").GetString()!;

        // 非法边界 → 400
        using var bad = Authed(HttpMethod.Post, "/ag-ui/admin/config", token);
        bad.Content = JsonContent.Create(new { messageHistoryLimit = -1 });
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.SendAsync(bad)).StatusCode);

        // 记忆旋钮越界 → 400（0 = 关闭仅在去重缓存上合法）
        using var badMem = Authed(HttpMethod.Post, "/ag-ui/admin/config", token);
        badMem.Content = JsonContent.Create(new { memoryMaxQueryChars = 0 });
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.SendAsync(badMem)).StatusCode);
        using var badCache = Authed(HttpMethod.Post, "/ag-ui/admin/config", token);
        badCache.Content = JsonContent.Create(new { memoryWriteEmbeddingCacheSize = -1 });
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.SendAsync(badCache)).StatusCode);

        // token 配额不能为负 → 400
        using var badQuotaUser = Authed(HttpMethod.Post, "/ag-ui/admin/config", token);
        badQuotaUser.Content = JsonContent.Create(new { dailyTokenQuotaPerUser = -1 });
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.SendAsync(badQuotaUser)).StatusCode);
        using var badQuotaGroup = Authed(HttpMethod.Post, "/ag-ui/admin/config", token);
        badQuotaGroup.Content = JsonContent.Create(new { dailyTokenQuotaPerGroup = -1 });
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.SendAsync(badQuotaGroup)).StatusCode);

        // 非管理员 → 403
        var normal = await RegisterAsync("cfg_normal_admin");
        using var denied = Authed(HttpMethod.Post, "/ag-ui/admin/config", normal.GetProperty("token").GetString()!);
        denied.Content = JsonContent.Create(new { sessionTtlHours = 24 });
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(denied)).StatusCode);
    }
}

/// <summary>
/// 独立宿主（专属 fixture）：首个注册用户默认自举为管理员 + 超级管理员，用于确定性验证平台角色 HTTP 分层。
/// </summary>
public sealed class PlatformRoleApiServerFixture : IAsyncLifetime
{
    public WebApplication App { get; private set; } = null!;
    public string HttpBase { get; private set; } = null!;
    public string SuperAdminToken { get; private set; } = null!;

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
            ["Auth:AdminUserIds"] = "",
        });
        HubApp.ConfigureServices(builder);
        builder.Services.AddAgentFramework(builder.Configuration);
        builder.Services.AddSingleton(new ConfigGovernanceState());
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));

        App = builder.Build();
        HubApp.MapEndpoints(App);
        App.MapAdminApi();
        App.MapConfigGovernanceApi();
        await App.StartAsync();
        HttpBase = App.Urls.First();

        // 确定性自举：平台_root 为本应用首个注册账号 → 自动成为管理员 + 超级管理员（其它测试先行注册也不会影响本 fixture 的专属实例）
        var auth = App.Services.GetRequiredService<AuthService>();
        var super = auth.Register("platform_root", "secret1", "超级管理员", null);
        Assert.Equal(PlatformRole.SuperAdmin, super.PlatformRole);
        SuperAdminToken = auth.Login("platform_root", "secret1").Token;
    }

    public async Task DisposeAsync()
    {
        if (App is not null) await App.DisposeAsync();
    }
}

/// <summary>平台角色分层 HTTP 端到端验证：首账号自举为 SuperAdmin，可授予/回收；Operator / Admin / User 差异化访问。</summary>
public sealed class PlatformRoleApiTests : IClassFixture<PlatformRoleApiServerFixture>
{
    private readonly PlatformRoleApiServerFixture _fixture;
    private readonly HttpClient _client;

    public PlatformRoleApiTests(PlatformRoleApiServerFixture fixture)
    {
        _fixture = fixture;
        _client = new HttpClient { BaseAddress = new Uri(fixture.HttpBase) };
    }

    private async Task<JsonElement> RegisterAsync(string username, string password = "secret1")
    {
        var res = await _client.PostAsJsonAsync("/ag-ui/user/register", new { username, password, nickname = username });
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
    public async Task FirstUser_IsSuperAdmin_AndCanManageRoles()
    {
        var superToken = _fixture.SuperAdminToken; // 平台_root（自举为 SuperAdmin）

        // 注册一个普通用户
        var staff = await RegisterAsync("staff_member");
        var staffId = staff.GetProperty("userId").GetString()!;
        Assert.Equal("user", staff.GetProperty("platformRole").GetString());

        // 普通用户访问 `/roles` → 403
        using (var denied = Authed(HttpMethod.Get, "/ag-ui/admin/roles", staff.GetProperty("token").GetString()!))
            Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(denied)).StatusCode);

        // SuperAdmin 提升普通用户为 Operator（只读运维）
        using var promote = Authed(HttpMethod.Post, $"/ag-ui/admin/roles/{staffId}", superToken);
        promote.Content = JsonContent.Create(new { role = "operator" });
        (await _client.SendAsync(promote)).EnsureSuccessStatusCode();

        // Operator 可访问只读运维端点 /admin/usage，但不能访问管理写端点 /admin/users
        var staffToken = staff.GetProperty("token").GetString()!;
        using (var usage = Authed(HttpMethod.Get, "/ag-ui/admin/usage", staffToken))
            Assert.Equal(HttpStatusCode.OK, (await _client.SendAsync(usage)).StatusCode);
        using (var users = Authed(HttpMethod.Get, "/ag-ui/admin/users", staffToken))
            Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(users)).StatusCode);

        // SuperAdmin 查看角色矩阵
        using var roles = Authed(HttpMethod.Get, "/ag-ui/admin/roles", superToken);
        var roleList = (await (await _client.SendAsync(roles)).Content.ReadFromJsonAsync<JsonElement[]>() ?? []);
        var staffRow = Assert.Single(roleList, r => r.GetProperty("userId").GetString() == staffId);
        Assert.Equal("operator", staffRow.GetProperty("effectiveRole").GetString());

        // SuperAdmin 把 Operator 再提升为 Admin，随后可访问管理写端点
        using var promoteAdmin = Authed(HttpMethod.Post, $"/ag-ui/admin/roles/{staffId}", superToken);
        promoteAdmin.Content = JsonContent.Create(new { role = "admin" });
        (await _client.SendAsync(promoteAdmin)).EnsureSuccessStatusCode();
        using var usersNow = Authed(HttpMethod.Get, "/ag-ui/admin/users", staffToken);
        Assert.Equal(HttpStatusCode.OK, (await _client.SendAsync(usersNow)).StatusCode);
    }

    [Fact]
    public async Task Admin_CannotManageRoles()
    {
        var superToken = _fixture.SuperAdminToken;
        // 由 SuperAdmin 授予一个 Admin
        var admin = await RegisterAsync("admin_mgr");
        var adminId = admin.GetProperty("userId").GetString()!;
        using var promote = Authed(HttpMethod.Post, $"/ag-ui/admin/roles/{adminId}", superToken);
        promote.Content = JsonContent.Create(new { role = "admin" });
        (await _client.SendAsync(promote)).EnsureSuccessStatusCode();

        // Admin 试图再授予他人 → 403（角色管理仅 SuperAdmin）
        var normal = await RegisterAsync("normal_c");
        using var denied = Authed(HttpMethod.Post, $"/ag-ui/admin/roles/{normal.GetProperty("userId").GetString()}", admin.GetProperty("token").GetString()!);
        denied.Content = JsonContent.Create(new { role = "admin" });
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(denied)).StatusCode);
    }
}

/// <summary>测试用：「已探测到转换器」的假执行器——只服务于 /admin/status 文档转换能力（docPreview）的断言。</summary>
internal sealed class StubAvailableSofficeRunner : ISofficeRunner
{
    public bool Available => true;

    public Task<SofficeResult> ConvertToPdfAsync(string sourcePath, string outDir,
        bool exportNotes = false, CancellationToken ct = default)
        => Task.FromResult(new SofficeResult(true, "stub"));
}
