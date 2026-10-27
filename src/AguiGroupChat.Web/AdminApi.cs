using System.Diagnostics;
using System.Text.Json;
using AguiGroupChat.Agents;
using AguiGroupChat.Hub;
using AguiGroupChat.Hub.Agents;
using AguiGroupChat.Hub.Infra;
using AguiGroupChat.Hub.Messaging;
using AguiGroupChat.Hub.Models;
using AguiGroupChat.Hub.Options;
using AguiGroupChat.Hub.Persistence;
using AguiGroupChat.Hub.Persistence.Redis;
using AguiGroupChat.Hub.Storage;
using AguiGroupChat.Hub.Users;

namespace AguiGroupChat.Web;

/// <summary>
/// 管理员控制台 API（仅系统管理员）：
///   GET  /ag-ui/admin/users —— 用户列表（禁用状态 / 管理员标记 / 注册时间）
///   POST /ag-ui/admin/users/{userId}/disabled —— 禁用 / 启用账号（禁用即吊销全部会话）
///   POST /ag-ui/admin/users/{userId}/password —— 重置密码（吊销全部会话）
///   DELETE /ag-ui/admin/users/{userId} —— 彻底删除账号（数据擦除：群转让/解散 + 记忆 / 知识库清除）
///   GET  /ag-ui/admin/status —— 系统状态（连接数 / 群数 / 用户数 / 消息数 / 智能体数 / 进程信息）
/// </summary>
public static class AdminApi
{
    public static void MapAdminApi(this WebApplication app)
    {
        var root = app.MapGroup("/ag-ui/admin");
        root.MapGet("/users", (HttpContext ctx, AuthService auth, GroupHub hub, AguiGroupChat.Hub.Agents.AgentUsageService usage) =>
        {
            // 个人配额水位（1.0.269）：把与用量页 / 侧边栏同源的 `QuotaLevel` 直接落到用户列表，
            // 管理员在「用户管理」就能看到谁快到顶了；登限制用户不在列表里（它是定时任务虚拟触发者）。
            var quota = usage.DailyQuotaPerUser;
            var today = usage.GetUserTodaySummary().ToDictionary(u => u.UserId, u => u.Tokens, StringComparer.Ordinal);
            var users = auth.ListUsers().Select(u =>
            {
                var tokens = today.TryGetValue(u.UserId, out var tk) ? tk : 0;
                return new
                {
                    u.UserId,
                    u.Username,
                    u.Nickname,
                    u.Avatar,
                    u.IsAdmin,
                    platformRole = WebIdentity.RoleName(u.PlatformRole),
                    u.IsDisabled,
                    u.PersonalMemoryEnabled,
                    u.CreatedAt,
                    u.UpdatedAt,
                    // 所在群数量（管理视角：账号活跃度参考）
                    groupCount = hub.Store.GroupsOf(u.UserId).Count,
                    todayTokens = tokens,
                    quota,
                    quotaLevel = AguiGroupChat.Hub.Agents.AgentUsageService.QuotaLevel(tokens, quota),
                };
            });
            return Results.Ok(users);
        }).AddEndpointFilter(new WebIdentity.RequireAdminFilter());

        // ---- 平台角色（RBAC 分层）：仅超级管理员可查询 / 授予 / 回收他人平台角色 ----
        root.MapGet("/roles", (HttpContext ctx, AuthService auth, GroupHub hub) =>
        {
            // 展示每个账号的<b>生效</b>角色（显式角色与 IsAdmin/配置名单推导取较高者），供运营查看角色矩阵
            var list = auth.ListUsers().Select(u => new
            {
                u.UserId,
                u.Username,
                explicitRole = WebIdentity.RoleName(u.PlatformRole),
                effectiveRole = WebIdentity.RoleName(auth.ResolveRole(u.UserId)),
                u.IsAdmin,
                u.IsDisabled,
            });
            return Results.Ok(list);
        }).AddEndpointFilter(new WebIdentity.RequireRoleFilter(PlatformRole.SuperAdmin));

        root.MapPost("/roles/{userId}", (string userId, AdminRoleHttpRequest req, HttpContext ctx, AuthService auth,
            AguiGroupChat.Hub.Infra.AuditLogService audit) =>
        {
            var me = WebIdentity.UserId(ctx)!;
            if (!Enum.TryParse<PlatformRole>(req.Role, true, out var role))
                return Results.BadRequest(new AguiError(ErrorCodes.BadRequest, "role 须为 user / operator / admin / superadmin"));
            // 超级管理员不得用本接口把自己降级（自我降级应通过更高权限处理，避免最后一任致盲）；其余由 AuthService 防呆兜底
            if (me == userId)
                return Results.BadRequest(new AguiError(ErrorCodes.BadRequest, "不能通过本接口修改自己的平台角色（防止误伤最后一任管理员）"));
            var updated = auth.SetPlatformRole(userId, role);
            audit.Record("admin.user.role", me, auth.GetUser(me)?.Username, targetType: "user",
                targetId: userId, detail: $"平台角色 → {role}");
            return Results.Ok(new { ok = true, userId, explicitRole = WebIdentity.RoleName(updated.PlatformRole), role });
        }).AddEndpointFilter(new WebIdentity.RequireRoleFilter(PlatformRole.SuperAdmin));

        root.MapPost("/users/{userId}/disabled", (string userId, AdminDisabledHttpRequest req, HttpContext ctx, AuthService auth, AguiGroupChat.Hub.Infra.AuditLogService audit) =>
            Run(() =>
            {
                var me = WebIdentity.UserId(ctx)!;
                // 防止管理员误禁自己（把自己禁掉后控制台失联）
                if (req.Disabled && me == userId)
                    return Results.BadRequest(new AguiError(ErrorCodes.BadRequest, "不能禁用当前登录的管理员账号"));
                auth.SetUserDisabled(userId, req.Disabled);
                audit.Record("admin.user.disable", me, auth.GetUser(me)?.Username, targetType: "user",
                    targetId: userId, detail: req.Disabled ? "禁用账号" : "启用账号");
                return Results.Ok(new { ok = true, userId, disabled = req.Disabled });
            })).AddEndpointFilter(new WebIdentity.RequireAdminFilter());

        root.MapPost("/users/{userId}/password", (string userId, AdminPasswordHttpRequest req, HttpContext ctx, AuthService auth, AguiGroupChat.Hub.Infra.AuditLogService audit) =>
            Run(() =>
            {
                var me = WebIdentity.UserId(ctx)!;
                auth.AdminResetPassword(userId, req.NewPassword);
                audit.Record("admin.user.reset_password", me, auth.GetUser(me)?.Username, targetType: "user", targetId: userId);
                return Results.Ok(new { ok = true, userId });
            })).AddEndpointFilter(new WebIdentity.RequireAdminFilter());

        root.MapGet("/status", async (HttpContext ctx, AuthService auth, GroupHub hub, AgentCatalog catalog, ConnectionManager connections, IServiceProvider sp) =>
        {
            var store = hub.Store;
            var messageCount = store.AllGroups().Sum(g => store.AllMessages(g.GroupId).Count);
            var proc = Process.GetCurrentProcess();
            // RAG 配置与图存储：图谱记忆是否生效取决于配置开启（GraphEnabled）且 IGraphMemory 可用（非 null 占位）
            var agents = sp.GetRequiredService<AgentOptions>();
            var graph = sp.GetService<IGraphMemory>();
            var graphActive = graph is not null && agents.Memory.GraphEnabled;
            GraphStats? gs = null;
            if (graphActive) { try { gs = graph!.Stats(); } catch { gs = null; } }
            // 在线查看 / 播放依赖服务端 LibreOffice（桌面版需本机安装）；未注册（测试宿主）视为不可用
            var soffice = sp.GetService<ISofficeRunner>();
            // 语音（多模态进阶）：ASR 转写 / TTS 合成的配置与可达性（可选能力，未注册时为 null）
            var asr = sp.GetService<AguiGroupChat.Agents.AudioTranscriptionService>();
            var tts = sp.GetService<AguiGroupChat.Agents.SpeechSynthesisService>();
            var asrReach = asr is { IsEnabled: true } ? await ProbeAsync(asr.Endpoint) : (bool?)null;
            var ttsReach = tts is { IsEnabled: true } ? await ProbeAsync(tts.Endpoint) : (bool?)null;
            return Results.Ok(new
            {
                status = "ok",
                uptimeSeconds = (long)(DateTimeOffset.UtcNow - Process.GetCurrentProcess().StartTime.ToUniversalTime()).TotalSeconds,
                connections = connections.ConnectionCount,
                groups = store.AllGroups().Count,
                users = auth.ListUsers().Count,
                agents = catalog.AgentIds.Count,
                messages = messageCount,
                memoryMb = proc.WorkingSet64 / 1024 / 1024,
                threadCount = proc.Threads.Count,
                dotnetVersion = Environment.Version.ToString(),
                rag = new
                {
                    vectorEnabled = agents.Memory.Enabled,             // 向量 RAG（语义记忆）配置开关
                    graphEnabled = agents.Memory.GraphEnabled,         // 图谱 RAG 配置开关
                    graphInUse = graphActive,                          // 图谱 RAG 是否真正生效（配置开 + 图存储可用）
                    graphProvider = graphActive ? agents.Memory.Provider : null,
                    graphTopK = agents.Memory.GraphTopK,
                    graphHops = agents.Memory.GraphHops,
                    graphEntities = gs?.EntityCount ?? 0,              // 当前图谱实体数
                    graphEdges = gs?.EdgeCount ?? 0,                   // 当前图谱关系边数
                },
                docPreview = new
                {
                    available = soffice?.Available ?? false,          // 转换组件（LibreOffice）是否就绪
                    converter = soffice is null ? null : "libreoffice", // 转换器类型（预留其它实现）
                },
                voice = new
                {
                    asr = new { enabled = asr?.IsEnabled ?? false, endpoint = asr?.Endpoint, reachable = asrReach },
                    tts = new { enabled = tts?.IsEnabled ?? false, endpoint = tts?.Endpoint, onlyForVoice = tts?.OnlyWhenVoiceTurn ?? false, reachable = ttsReach },
                },
            });
        }).AddEndpointFilter(new WebIdentity.RequireRoleFilter(PlatformRole.Operator));

        // 语音合成音色列表（多模态进阶 · 语音问答）：代理合成服务的 /v1/audio/voices，供管理员选音色。
        // 未配置 / 不支持 → 404；服务不可达 → 503；成功 → { voices: string[] }。仅运维及以上。
        root.MapGet("/voice/voices", async (IServiceProvider sp) =>
        {
            var tts = sp.GetService<AguiGroupChat.Agents.SpeechSynthesisService>();
            var url = tts?.VoicesEndpoint;
            if (tts is null || !tts.IsEnabled || url is null)
                return Results.NotFound(new AguiError(ErrorCodes.BadRequest, "未配置独立的语音合成服务（或不支持音色列表）"));
            try
            {
                using var res = await VoiceHttp.GetAsync(url);
                if (!res.IsSuccessStatusCode)
                    return Results.Json(new AguiError(ErrorCodes.BadRequest, $"音色列表获取失败（HTTP {(int)res.StatusCode}）"),
                        statusCode: StatusCodes.Status503ServiceUnavailable);
                return Results.Ok(new { voices = ParseVoices(await res.Content.ReadAsStringAsync()) });
            }
            catch
            {
                return Results.Json(new AguiError(ErrorCodes.BadRequest, "语音合成服务不可达"),
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        }).AddEndpointFilter(new WebIdentity.RequireRoleFilter(PlatformRole.Operator));

        // 音色试听：用配置的 TTS 端点合成一句短样例并**内联返回音频**（GET，便于 <audio src>，可带 ?token=）。
        // 未启用 → 404；合成失败 → 503。仅运维及以上。
        root.MapGet("/voice/sample", async (string? voice, IServiceProvider sp) =>
        {
            var tts = sp.GetService<AguiGroupChat.Agents.SpeechSynthesisService>();
            if (tts is null || !tts.IsEnabled) return Results.NotFound();
            var bytes = await tts.TrySynthesizeBytesAsync("你好，我是知聚平台的数字员工。这是音色试听。", voice, CancellationToken.None);
            if (bytes is null)
                return Results.Json(new AguiError(ErrorCodes.BadRequest, "音色试听合成失败"),
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            return Results.File(bytes, tts.SampleContentType);
        }).AddEndpointFilter(new WebIdentity.RequireRoleFilter(PlatformRole.Operator));

        // 数字员工绩效看板（P0）：按数字员工聚合调用量 / token / 👍👎 好评率。仅运维及以上（只读）。
        // 数据源：agui_usage（按日期+员工+触发者聚合行）与消息反馈；窗口 days 只用于过滤反馈。
        root.MapGet("/agent-performance", (int? days, string? groupId, IServiceProvider sp) =>
        {
            var usage = sp.GetService<IUsageStore>();
            var catalog = sp.GetService<AgentCatalog>();
            if (usage is null || catalog is null)
                return Results.Ok(new { days = 0, agents = Array.Empty<object>() });
            var d = Math.Clamp(days ?? 30, 1, 365);
            var to = DateTimeOffset.UtcNow;
            var from = to.AddDays(-d + 1);
            var names = catalog.ListDefinitions()
                .ToDictionary(x => x.AgentId, x => string.IsNullOrWhiteSpace(x.Nickname) ? x.AgentId : x.Nickname, StringComparer.Ordinal);
            var scopeId = string.IsNullOrWhiteSpace(groupId) ? null : groupId.Trim();
            var artifactQuality = sp.GetService<ArtifactQualityStore>();
            IReadOnlyList<AgentPerformance.AgentStat> stats;
            string? scopeName = null;
            if (scopeId is not null)
            {
                // 知聚范围：用量取「按知聚」聚合表；👍👎 / 好评率不按知聚记录，故 Up/Down 恒为 0（前端显示「—」）；
                // seedAllAgents=false：只列该知聚内实际有用量的员工，避免「该群闲置」噪声。
                var grows = usage.GetGroupUsageBetween(from.ToString("yyyy-MM-dd"), to.ToString("yyyy-MM-dd"))
                    .Where(r => string.Equals(r.GroupId, scopeId, StringComparison.Ordinal));
                IReadOnlyList<ArtifactQualityEvent> gArtifacts = artifactQuality?.Query(from.ToUnixTimeMilliseconds(), groupId: scopeId) ?? [];
                stats = AgentPerformance.Aggregate(grows, (IReadOnlyList<MessageFeedbackEntry>)[], names, d, to.ToUnixTimeMilliseconds(), seedAllAgents: false, artifacts: gArtifacts);
                scopeName = sp.GetService<GroupHub>()?.Store.AllGroups().FirstOrDefault(g => g.GroupId == scopeId)?.GroupName;
            }
            else
            {
                var rows = usage.GetUsageBetween(from.ToString("yyyy-MM-dd"), to.ToString("yyyy-MM-dd"));
                var feedback = sp.GetService<MessageFeedbackStore>()?.Snapshot() ?? (IReadOnlyList<MessageFeedbackEntry>)[];
                IReadOnlyList<ArtifactQualityEvent> artifacts = artifactQuality?.Query(from.ToUnixTimeMilliseconds()) ?? [];
                stats = AgentPerformance.Aggregate(rows, feedback, names, d, to.ToUnixTimeMilliseconds(), artifacts: artifacts);
            }
            var pricing = sp.GetService<AgentOptions>()?.Pricing ?? new PricingOptions();
            var up = stats.Sum(s => s.Up);
            var down = stats.Sum(s => s.Down);
            var totalCost = stats.Sum(s => pricing.EstimateCost(s.PromptTokens, s.CompletionTokens, s.ReasoningTokens));
            var dailyAvgCost = totalCost / d;
            var artifactRuns = stats.Sum(s => s.ArtifactRuns);
            var artifactValid = stats.Sum(s => s.ArtifactValid);
            var artifactAvgScore = artifactRuns == 0 ? (double?)null
                : Math.Round(stats.Sum(s => s.ArtifactAvgScore * s.ArtifactRuns) / artifactRuns, 1);
            return Results.Ok(new
            {
                days = d,
                from = from.ToString("yyyy-MM-dd"),
                to = to.ToString("yyyy-MM-dd"),
                // 知聚范围（为空即全部）：前端据此提示「👍👎 / 好评率不按知聚记录」
                scope = scopeId is null ? null : new { groupId = scopeId, name = scopeName ?? scopeId },
                pricing = new
                {
                    configured = pricing.Configured,
                    currency = string.IsNullOrWhiteSpace(pricing.Currency) ? "$" : pricing.Currency,
                    inputPerMillion = pricing.InputPerMillion,
                    outputPerMillion = pricing.OutputPerMillion,
                    dailyBudget = pricing.DailyBudget,
                },
                summary = new
                {
                    agents = stats.Count,
                    calls = stats.Sum(s => s.Calls),
                    totalTokens = stats.Sum(s => s.TotalTokens),
                    up,
                    down,
                    positiveRate = (up + down) == 0 ? (double?)null : Math.Round((double)up / (up + down), 4),
                    cost = Math.Round(totalCost, 4),
                    dailyAvgCost = Math.Round(dailyAvgCost, 4),
                    budget = pricing.DailyBudget,
                    budgetPct = pricing.DailyBudget > 0 ? (double)Math.Round(dailyAvgCost / pricing.DailyBudget * 100, 1) : (double?)null,
                    overBudget = pricing.DailyBudget > 0 && dailyAvgCost > pricing.DailyBudget,
                    artifactRuns,
                    artifactValid,
                    artifactValidRate = artifactRuns == 0 ? (double?)null : Math.Round((double)artifactValid / artifactRuns, 4),
                    artifactAvgScore,
                    artifactLow = stats.Sum(s => s.ArtifactLow),
                },
                agents = stats.Select(s => new
                {
                    s.AgentId, s.Name, s.Calls, s.PromptTokens, s.CompletionTokens, s.ReasoningTokens, s.TotalTokens,
                    s.Up, s.Down, s.PositiveRate, s.LastDate, s.Flags,
                    s.ArtifactRuns, s.ArtifactValid, s.ArtifactValidRate, s.ArtifactAvgScore, s.ArtifactLow,
                    cost = Math.Round(pricing.EstimateCost(s.PromptTokens, s.CompletionTokens, s.ReasoningTokens), 4),
                }),
            });
        }).AddEndpointFilter(new WebIdentity.RequireRoleFilter(PlatformRole.Operator));

        // 知聚下拉选项（绩效页「按知聚」筛选用）：仅运维及以上（只读）。
        root.MapGet("/groups", (IServiceProvider sp) =>
        {
            var groups = (sp.GetService<GroupHub>()?.Store.AllGroups() ?? (IReadOnlyList<Group>)[])
                .Select(g => new { groupId = g.GroupId, name = string.IsNullOrWhiteSpace(g.GroupName) ? g.GroupId : g.GroupName })
                .OrderBy(x => x.name, StringComparer.CurrentCulture)
                .ToList();
            return Results.Ok(new { groups });
        }).AddEndpointFilter(new WebIdentity.RequireRoleFilter(PlatformRole.Operator));

        // 组织体检报告（P0）：把「员工绩效 + 组织连接」汇成可导出的 Markdown。仅运维及以上（只读）。
        root.MapGet("/org-health", (int? days, IServiceProvider sp) =>
        {
            var usage = sp.GetService<IUsageStore>();
            var catalog = sp.GetService<AgentCatalog>();
            if (usage is null || catalog is null)
                return Results.Ok(new { filename = "", markdown = "" });
            var d = Math.Clamp(days ?? 30, 1, 365);
            var to = DateTimeOffset.UtcNow;
            var from = to.AddDays(-d + 1);
            var rows = usage.GetUsageBetween(from.ToString("yyyy-MM-dd"), to.ToString("yyyy-MM-dd"));
            var feedback = sp.GetService<MessageFeedbackStore>()?.Snapshot() ?? (IReadOnlyList<MessageFeedbackEntry>)[];
            var artifactQuality = sp.GetService<ArtifactQualityStore>();
            var defs = catalog.ListDefinitions();
            var names = defs.ToDictionary(x => x.AgentId, x => string.IsNullOrWhiteSpace(x.Nickname) ? x.AgentId : x.Nickname, StringComparer.Ordinal);
            IReadOnlyList<ArtifactQualityEvent> artifacts = artifactQuality?.Query(from.ToUnixTimeMilliseconds()) ?? [];
            var stats = AgentPerformance.Aggregate(rows, feedback, names, d, to.ToUnixTimeMilliseconds(), artifacts: artifacts);
            // 上一窗口（同长度、紧邻当前窗口之前）：供环比
            var prevTo = from.AddDays(-1);
            var prevFrom = prevTo.AddDays(-d + 1);
            var prevRows = usage.GetUsageBetween(prevFrom.ToString("yyyy-MM-dd"), prevTo.ToString("yyyy-MM-dd"));
            IReadOnlyList<ArtifactQualityEvent> prevArtifacts = artifactQuality?.Query(prevTo.AddDays(-d + 1).ToUnixTimeMilliseconds()) ?? [];
            var prevStats = AgentPerformance.Aggregate(prevRows, feedback, names, d, prevTo.ToUnixTimeMilliseconds(), artifacts: prevArtifacts);
            var pricing = sp.GetService<AgentOptions>()?.Pricing ?? new PricingOptions();
            var byId = defs.ToDictionary(x => x.AgentId, x => x, StringComparer.Ordinal);
            var md = OrgHealthReport.BuildMarkdown(d, from.ToString("yyyy-MM-dd"), to.ToString("yyyy-MM-dd"),
                stats, byId, pricing, to.ToUnixTimeMilliseconds(), sp.GetService<EvalCaseStore>()?.List(), prevStats);
            // 除 Markdown 外，同时下发结构化数据（供前端图表视图：KPI / 调用量 Top / 关注项）。
            var up = stats.Sum(s => s.Up);
            var down = stats.Sum(s => s.Down);
            var totalCost = stats.Sum(s => pricing.EstimateCost(s.PromptTokens, s.CompletionTokens, s.ReasoningTokens));
            var prevUp = prevStats.Sum(s => s.Up);
            var prevDown = prevStats.Sum(s => s.Down);
            var prevTotalCost = pricing.EstimateCost(prevStats.Sum(s => s.PromptTokens), prevStats.Sum(s => s.CompletionTokens), prevStats.Sum(s => s.ReasoningTokens));
            // 按「触发者（用户）」聚合：谁在消耗资源（与「按用户配额」互补）；名字取昵称 → 用户名 → userId。
            var userNames = sp.GetService<AuthService>()?.ListUsers()
                .ToDictionary(x => x.UserId, x => string.IsNullOrWhiteSpace(x.Nickname) ? x.Username : x.Nickname, StringComparer.Ordinal)
                ?? new Dictionary<string, string>(StringComparer.Ordinal);
            var users = rows
                .GroupBy(r => r.UserId, StringComparer.Ordinal)
                .Select(g => new
                {
                    userId = g.Key,
                    name = userNames.TryGetValue(g.Key, out var un) ? un : g.Key,
                    calls = g.Sum(x => x.Calls),
                    totalTokens = g.Sum(x => x.TotalTokens),
                    cost = Math.Round(g.Sum(x => pricing.EstimateCost(x.PromptTokens, x.CompletionTokens, x.ReasoningTokens)), 4),
                })
                .OrderByDescending(x => x.calls)
                .Take(50)
                .ToList();
            // 按「知聚（群）」聚合：哪个知聚最耗资源。注：群维度自本版起录入，历史用量无群信息不会出现在此表。
            var groupNames = sp.GetService<GroupHub>()?.Store.AllGroups()
                .ToDictionary(g => g.GroupId, g => g.GroupName, StringComparer.Ordinal)
                ?? new Dictionary<string, string>(StringComparer.Ordinal);
            var groups = usage.GetGroupUsageBetween(from.ToString("yyyy-MM-dd"), to.ToString("yyyy-MM-dd"))
                .GroupBy(r => r.GroupId, StringComparer.Ordinal)
                .Select(g => new
                {
                    groupId = g.Key,
                    name = groupNames.TryGetValue(g.Key, out var gn) ? gn : g.Key,
                    calls = g.Sum(x => x.Calls),
                    totalTokens = g.Sum(x => x.TotalTokens),
                    cost = Math.Round(g.Sum(x => pricing.EstimateCost(x.PromptTokens, x.CompletionTokens, x.ReasoningTokens)), 4),
                })
                .OrderByDescending(x => x.calls)
                .Take(50)
                .ToList();
            return Results.Ok(new
            {
                filename = $"org-health-{to:yyyyMMdd}.md",
                markdown = md,
                days = d,
                from = from.ToString("yyyy-MM-dd"),
                to = to.ToString("yyyy-MM-dd"),
                pricing = new
                {
                    configured = pricing.Configured,
                    currency = string.IsNullOrWhiteSpace(pricing.Currency) ? "$" : pricing.Currency,
                    // 单价随包下发，供前端图表视图在「成本」维度按日估算（与 /agent-performance 一致）。
                    inputPerMillion = pricing.InputPerMillion,
                    outputPerMillion = pricing.OutputPerMillion,
                },
                summary = new
                {
                    agents = stats.Count(s => s.Calls > 0),
                    totalAgents = stats.Count,
                    calls = stats.Sum(s => s.Calls),
                    totalTokens = stats.Sum(s => s.TotalTokens),
                    up,
                    down,
                    positiveRate = (up + down) == 0 ? (double?)null : Math.Round((double)up / (up + down), 4),
                    cost = Math.Round(totalCost, 4),
                    flagged = stats.Count(s => s.Flags is { Count: > 0 }),
                    artifactRuns = stats.Sum(s => s.ArtifactRuns),
                    artifactValid = stats.Sum(s => s.ArtifactValid),
                    artifactValidRate = stats.Sum(s => s.ArtifactRuns) == 0
                        ? (double?)null
                        : Math.Round((double)stats.Sum(s => s.ArtifactValid) / stats.Sum(s => s.ArtifactRuns), 4),
                    artifactAvgScore = stats.Sum(s => s.ArtifactRuns) == 0
                        ? (double?)null
                        : Math.Round(stats.Sum(s => s.ArtifactAvgScore * s.ArtifactRuns) / stats.Sum(s => s.ArtifactRuns), 1),
                    artifactLow = stats.Sum(s => s.ArtifactLow),
                },
                // 上一窗口（同长度）：供前端 / 报告做环比
                prev = new
                {
                    from = prevFrom.ToString("yyyy-MM-dd"),
                    to = prevTo.ToString("yyyy-MM-dd"),
                    summary = new
                    {
                        agents = prevStats.Count(s => s.Calls > 0),
                        calls = prevStats.Sum(s => s.Calls),
                        totalTokens = prevStats.Sum(s => s.TotalTokens),
                        cost = Math.Round(prevTotalCost, 4),
                        positiveRate = (prevUp + prevDown) == 0 ? (double?)null : Math.Round((double)prevUp / (prevUp + prevDown), 4),
                        flagged = prevStats.Count(s => s.Flags is { Count: > 0 }),
                        artifactValidRate = prevStats.Sum(s => s.ArtifactRuns) == 0
                            ? (double?)null
                            : Math.Round((double)prevStats.Sum(s => s.ArtifactValid) / prevStats.Sum(s => s.ArtifactRuns), 4),
                        artifactAvgScore = prevStats.Sum(s => s.ArtifactRuns) == 0
                            ? (double?)null
                            : Math.Round(prevStats.Sum(s => s.ArtifactAvgScore * s.ArtifactRuns) / prevStats.Sum(s => s.ArtifactRuns), 1),
                    },
                },
                agents = stats.Select(s => new
                {
                    s.AgentId, s.Name, s.Calls, s.TotalTokens, s.Up, s.Down, s.PositiveRate, s.LastDate, s.Flags,
                    s.ArtifactRuns, s.ArtifactValid, s.ArtifactValidRate, s.ArtifactAvgScore, s.ArtifactLow,
                    cost = Math.Round(pricing.EstimateCost(s.PromptTokens, s.CompletionTokens, s.ReasoningTokens), 4),
                }),
                // 图表视图「触发者」维度：按调用次数降序的前 50 名（无反馈 / flags，故仅调用量 / token / 成本）。
                users = users,
                // 图表视图「知聚」维度：按调用次数降序的前 50 名知聚（同理无反馈 / flags）。
                groups = groups,
            });
        }).AddEndpointFilter(new WebIdentity.RequireRoleFilter(PlatformRole.Operator));

        // 模型用量统计（最近 N 天按日汇总 / 配额配置 / 今日各知聚明细）：仅管理员及以上（含运维）
        root.MapGet("/usage", (int? days, HttpContext ctx, IServiceProvider sp, AuthService auth, AguiGroupChat.Hub.Agents.AgentUsageService usage) =>
        {
            return Results.Ok(new
            {
                dailyQuotaPerUser = usage.DailyQuotaPerUser,
                dailyQuotaPerGroup = usage.DailyQuotaPerGroup,
                groupsToday = GroupTodayRows(sp, usage),
                usersToday = UserTodayRows(auth, usage),
                days = usage.GetDailySummary(days ?? 7),
            });
        }).AddEndpointFilter(new WebIdentity.RequireRoleFilter(PlatformRole.Operator));

        // 知聚配额水位（管理员侧主动预警）：供侧边栏知聚项打标（不打开控制台也能看到哪个知聚快满）
        // 与用量页汇总。仅 Operator+（配额 / 成本属运维信息，不下发给普通用户）。
        root.MapGet("/quota-status", (IServiceProvider sp, AguiGroupChat.Hub.Agents.AgentUsageService usage) =>
        {
            var global = usage.DailyQuotaPerGroup;
            var hub = sp.GetService<GroupHub>();
            var groups = usage.GetGroupTodaySummary()
                .Take(50)
                .Select(g => new
                {
                    groupId = g.GroupId,
                    level = AguiGroupChat.Hub.Agents.AgentUsageService.QuotaLevel(g.Tokens, EffectiveQuota(hub, usage, g.GroupId).Quota),
                })
                .ToList();
            return Results.Ok(new
            {
                quotaPerGroup = global,
                warnRatio = AguiGroupChat.Hub.Agents.AgentUsageService.QuotaWarnRatio,
                groups,
            });
        }).AddEndpointFilter(new WebIdentity.RequireRoleFilter(PlatformRole.Operator));

        // 临时放额提醒（1.0.276，站内通道）：不依赖 Webhook，管理员打开控制台就能看到
        // 「哪些知聚的临时额度即将到期 / 已回退」。窗口与提醒服务同源（配置治理的提前量）。
        root.MapGet("/quota-reminders", (IServiceProvider sp) =>
        {
            var cfg = sp.GetService<ConfigGovernanceState>();
            var leadHours = cfg?.QuotaReminderLeadHours is > 0 and <= 720
                ? cfg.QuotaReminderLeadHours.Value
                : QuotaExpiryReminderService.DefaultLeadHours;
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var leadMs = (long)TimeSpan.FromHours(leadHours).TotalMilliseconds;
            var graceMs = (long)QuotaExpiryReminderService.ExpiredGrace.TotalMilliseconds;
            var groups = sp.GetService<GroupHub>()?.Store.AllGroups() ?? (IReadOnlyList<Group>)[];
            var items = new List<QuotaReminderRow>();
            foreach (var g in groups)
            {
                if (g.QuotaOverride is not { } quota || g.QuotaOverrideUntil is not { } until) continue;
                var delta = until - now;
                if (delta > 0 && delta <= leadMs)
                    items.Add(new QuotaReminderRow(g.GroupId, g.GroupName, quota, until, "expiring", delta / 1000));
                else if (delta <= 0 && -delta <= graceMs)
                    items.Add(new QuotaReminderRow(g.GroupId, g.GroupName, quota, until, "expired", (-delta) / 1000));
            }
            var ordered = items
                .OrderBy(x => x.State == "expired" ? 1 : 0)
                .ThenBy(x => x.Until)
                .ToList();
            return Results.Ok(new { leadHours, graceHours = (int)QuotaExpiryReminderService.ExpiredGrace.TotalHours, items = ordered });
        }).AddEndpointFilter(new WebIdentity.RequireRoleFilter(PlatformRole.Operator));

        // 今日各知聚用量行（/usage 与 /quota-status 共用口径）：名字由群库解析，取不到回退 groupId；
        // 同时带上**本知聚生效配额** `quota` 与水位 `level`（ok / warn / over）——单一阈值来源，
        // 且支持知聚级配额覆写（每行自己的分母，前端直接用 `quota` 而非全局值）。
        static List<object> GroupTodayRows(IServiceProvider sp, AguiGroupChat.Hub.Agents.AgentUsageService usage)
        {
            var groupNames = sp.GetService<GroupHub>()?.Store.AllGroups()
                .ToDictionary(g => g.GroupId, g => g.GroupName, StringComparer.Ordinal)
                ?? new Dictionary<string, string>(StringComparer.Ordinal);
            var hub = sp.GetService<GroupHub>();
            return usage.GetGroupTodaySummary()
                .Take(50)
                .Select(g =>
                {
                    var (quota, until) = EffectiveQuota(hub, usage, g.GroupId);
                    return (object)new
                    {
                        groupId = g.GroupId,
                        name = groupNames.TryGetValue(g.GroupId, out var gn) ? gn : g.GroupId,
                        tokens = g.Tokens,
                        calls = g.Calls,
                        quota,
                        // 临时放额的到期时间（仅当覆写**仍生效**时给出，便于界面提示「临时至 X」）
                        quotaUntil = until,
                        level = AguiGroupChat.Hub.Agents.AgentUsageService.QuotaLevel(g.Tokens, quota),
                    };
                })
                .ToList();
        }

        // 生效配额 = 知聚级覆写（Group.QuotaOverride，且未到期）优先，否则全局 Agents:DailyTokenQuotaPerGroup。
        // 一次取覆写值同时拿到期时间，避免两次群库读取。
        static (long Quota, long? Until) EffectiveQuota(GroupHub? hub, AguiGroupChat.Hub.Agents.AgentUsageService usage, string groupId)
        {
            var ov = hub?.GroupQuotaOverride(groupId);
            return (usage.EffectiveGroupQuota(groupId, ov), ov is null ? null : hub?.GroupQuotaOverrideUntil(groupId));
        }

        // 今日各触发者用量行（1.0.268）：按用户个人配额算水位，供用量页「接近 / 已超个人配额」汇总。
        // system 是定时任务的虚拟触发者，**刻意豁免个人配额**——它也会出现（便于看到系统任务消耗），
        // 但 exempt=true、level 恒 ok，否则前端会显示「已超限」而实际从不拦截。
        static List<object> UserTodayRows(AuthService auth, AguiGroupChat.Hub.Agents.AgentUsageService usage)
        {
            var quota = usage.DailyQuotaPerUser;
            var names = auth.ListUsers().ToDictionary(
                u => u.UserId,
                u => !string.IsNullOrWhiteSpace(u.Nickname) ? u.Nickname
                    : !string.IsNullOrWhiteSpace(u.Username) ? u.Username : u.UserId,
                StringComparer.Ordinal);
            return usage.GetUserTodaySummary()
                .Take(50)
                .Select(u =>
                {
                    var exempt = string.Equals(u.UserId, AguiGroupChat.Hub.Agents.AgentUsageService.SystemUserId, StringComparison.Ordinal);
                    return (object)new
                    {
                        userId = u.UserId,
                        name = names.TryGetValue(u.UserId, out var n) ? n : u.UserId,
                        tokens = u.Tokens,
                        calls = u.Calls,
                        quota,
                        exempt,
                        level = exempt ? "ok" : AguiGroupChat.Hub.Agents.AgentUsageService.QuotaLevel(u.Tokens, quota),
                    };
                })
                .ToList();
        }

        // 操作审计日志（4.3 / 企业合规）：关键 / 敏感操作留痕，仅管理员及以上（含运维）。
        // 支持过滤：actor（操作者用户名 / ID 子串）、action（操作名子串）、targetId、fromMs / toMs（UTC 毫秒）。limit 最多 200。
        root.MapGet("/audit", (int? limit, string? actor, string? action, string? targetId, long? fromMs, long? toMs, string? detail,
            HttpContext ctx, AguiGroupChat.Hub.Infra.AuditLogService audit) =>
        {
            return Results.Ok(new
            {
                total = audit.Count,
                entries = audit.Query(limit ?? 100, actor, action, targetId, fromMs, toMs, detail),
            });
        }).AddEndpointFilter(new WebIdentity.RequireRoleFilter(PlatformRole.Operator));

        // 审计日志导出 CSV（RFC 4180 转义 + UTF-8 BOM，Excel 可直接打开）：同 /audit 过滤条件，按时间正序，无 200 条上限。
        root.MapGet("/audit.csv", (string? actor, string? action, string? targetId, long? fromMs, long? toMs, string? detail,
            HttpContext ctx, AguiGroupChat.Hub.Infra.AuditLogService audit) =>
        {
            var rows = audit.QueryAll(actor, action, targetId, fromMs, toMs, detail);
            var sb = new System.Text.StringBuilder(rows.Count * 160);
            sb.AppendLine("id,timeUtc,timeMs,action,actorId,actorUsername,groupId,targetType,targetId,detail,result");
            foreach (var e in rows) AppendCsvRow(sb, e);
            var body = System.Text.Encoding.UTF8.GetBytes(sb.ToString());
            var csv = new byte[body.Length + 3]; // 前置 UTF-8 BOM：Excel 打开中文不乱码
            csv[0] = 0xEF; csv[1] = 0xBB; csv[2] = 0xBF;
            Buffer.BlockCopy(body, 0, csv, 3, body.Length);
            return Results.File(csv, "text/csv; charset=utf-8",
                $"agui-audit-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.csv");
        }).AddEndpointFilter(new WebIdentity.RequireRoleFilter(PlatformRole.Operator));

        // 桥接端点健康度（3.1）：查看已配置外部 AG-UI 端点的实时/缓存连通状态，仅管理员及以上（含运维）。
        root.MapGet("/bridge-health", async (bool? refresh, HttpContext ctx,
            AguiGroupChat.Agents.BridgeHealthService bridgeHealth, CancellationToken ct) =>
        {
            if (refresh == true)
                return Results.Ok(await bridgeHealth.ProbeAllAsync(ct)); // 同步触发一次实时探测
            return Results.Ok(bridgeHealth.GetStatus());
        }).AddEndpointFilter(new WebIdentity.RequireRoleFilter(PlatformRole.Operator));

        // 桥接能力协商（3.2）：查看外部端点的能力（支持的工具 / 附件 / 审批类型），仅管理员及以上（含运维）。
        root.MapGet("/bridge-capabilities", async (bool? refresh, HttpContext ctx,
            AguiGroupChat.Agents.BridgeCapabilitiesService caps, CancellationToken ct) =>
        {
            if (refresh == true)
                return Results.Ok((await caps.ProbeAllAsync(ct)).Select(r => new { r.AgentId, r.Endpoint, supportsProtocol = r.Cap.Discovered, r.Cap.SupportsTools, r.Cap.SupportsAttachments, r.Cap.ApprovalTypes }));
            return Results.Ok(caps.GetCached().Select(r => new { r.AgentId, r.Endpoint, supportsProtocol = r.Cap.Discovered, r.Cap.SupportsTools, r.Cap.SupportsAttachments, r.Cap.ApprovalTypes }));
        }).AddEndpointFilter(new WebIdentity.RequireRoleFilter(PlatformRole.Operator));

        // 工作区可视化（1.0.312）：列出各数字员工工作区占用，支持按策略清理。仅运维及以上。
        //   GET  /ag-ui/admin/workspaces —— 根目录 / 配额 / 保留期 + 各工作区占用
        //   POST /ag-ui/admin/workspaces/cleanup?agentId=xxx —— 不传 agentId = 按全局策略清整根；传 = 仅清该工作区
        root.MapGet("/workspaces", (AgentOptions options, AgentCatalog catalog,
            Microsoft.Extensions.Hosting.IHostEnvironment env) =>
        {
            var root = WorkspaceMaintenance.ResolveRoot(options.WorkSpaceRoot, env.ContentRootPath);
            var items = WorkspaceMaintenance.Describe(root)
                .Select(w => new
                {
                    agentId = w.AgentId,
                    nickname = catalog.GetDefinition(w.AgentId)?.Nickname ?? "",
                    bytes = w.Bytes,
                    files = w.Files,
                    lastModifiedUtc = w.LastModifiedUtc,
                })
                .ToList();
            return Results.Ok(new
            {
                root,
                quotaMb = options.WorkSpaceQuotaMb,
                retentionDays = options.WorkSpaceRetentionDays,
                items,
            });
        }).AddEndpointFilter(new WebIdentity.RequireRoleFilter(PlatformRole.Operator));

        root.MapPost("/workspaces/cleanup", (string? agentId, HttpContext ctx, AgentOptions options,
            AuthService auth, Microsoft.Extensions.Hosting.IHostEnvironment env,
            ILoggerFactory loggerFactory, AguiGroupChat.Hub.Infra.AuditLogService audit) =>
        {
            var me = WebIdentity.UserId(ctx)!;
            var logger = loggerFactory.CreateLogger("AdminWorkspaces");
            var root = WorkspaceMaintenance.ResolveRoot(options.WorkSpaceRoot, env.ContentRootPath);
            var scope = string.IsNullOrWhiteSpace(agentId) ? "all" : "agent";
            var target = scope == "all" ? root : Path.GetFullPath(Path.Combine(root, agentId!));
            // 防越界：agentId 仅允许单层目录名（不接受路径符号）
            if (scope == "agent"
                && (agentId!.Contains('/') || agentId.Contains('\\') || agentId is "." or ".."
                    || !string.Equals(Path.GetDirectoryName(target), Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar),
                        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)))
                return Results.BadRequest(new { error = "invalid_agent_id" });

            var result = WorkspaceMaintenance.Sweep(target, options.WorkSpaceQuotaMb, options.WorkSpaceRetentionDays, logger);
            audit.Record("admin.workspaces.cleanup", me, auth.GetUser(me)?.Username, targetType: "workspace", targetId: agentId ?? "*",
                detail: $"删除 {result.DeletedFiles} 个文件（{Math.Round(result.DeletedBytes / 1024d / 1024d, 1)} MB）");
            return Results.Ok(new
            {
                ok = true,
                scope,
                agentId,
                deletedFiles = result.DeletedFiles,
                deletedBytes = result.DeletedBytes,
                totalBytes = result.TotalBytes,
            });
        }).AddEndpointFilter(new WebIdentity.RequireRoleFilter(PlatformRole.Operator));

        // 工作区文件浏览（1.0.313）：只读列出 / 下载工作区内的文件（路径严格限定在工作区目录内）。
        // 1.0.317：按目录逐层浏览（path 为空 = 根），返回子目录 + 文件，前端用面包屑导航。
        root.MapGet("/workspaces/{agentId}/files", (string agentId, string? path, string? q, string? scope, AgentOptions options,
            Microsoft.Extensions.Hosting.IHostEnvironment env) =>
        {
            var root = WorkspaceMaintenance.ResolveRoot(options.WorkSpaceRoot, env.ContentRootPath);
            var dir = WorkspaceMaintenance.ResolveWorkspaceDir(root, agentId);
            if (dir is null) return Results.BadRequest(new { error = "invalid_agent_id" });

            var rel = (path ?? "").Trim().Replace('\\', '/').Trim('/');
            if (rel is "." or "..") rel = "";
            string full;
            if (rel.Length == 0) full = dir;
            else if (!WorkspaceMaintenance.TryResolveTarget(dir, rel, out full))
                return Results.BadRequest(new { error = "invalid_path" });

            var exists = Directory.Exists(full);

            // 文件名搜索（1.0.318）/ 内容搜索（1.0.319）：跨整个工作区；内容搜索额外给行号与片段
            var needle = (q ?? "").Trim();
            var contentScope = string.Equals(scope, "content", StringComparison.OrdinalIgnoreCase);
            if (needle.Length > 0 && exists && contentScope)
            {
                var matches = WorkspaceMaintenance.SearchContent(dir, needle)
                    .Select(m => new
                    {
                        name = Path.GetFileName(m.Path),
                        path = m.Path,
                        isDir = false,
                        bytes = m.Bytes,
                        modifiedUtc = m.ModifiedUtc,
                        line = m.Line,
                        snippet = m.Snippet,
                    })
                    .ToList();
                return Results.Ok(new { agentId, dir = rel, exists, mode = "search", scope = "content", q = needle, truncated = false, entries = matches });
            }
            if (needle.Length > 0 && exists)
            {
                const int maxHits = 500;
                var all = WorkspaceMaintenance.ListFiles(dir, maxHits + 1);
                var hits = all.Where(f => f.Path.Contains(needle, StringComparison.OrdinalIgnoreCase))
                    .Take(maxHits)
                    .Select(f => new
                    {
                        name = Path.GetFileName(f.Path),
                        path = f.Path,
                        isDir = false,
                        bytes = f.Bytes,
                        modifiedUtc = f.ModifiedUtc,
                    })
                    .ToList();
                return Results.Ok(new { agentId, dir = rel, exists, mode = "search", q = needle, truncated = all.Count > maxHits, entries = hits });
            }

            var entries = exists
                ? WorkspaceMaintenance.ListEntries(dir, rel)
                    .Select(e => new { e.Name, e.Path, e.IsDir, e.Bytes, e.ModifiedUtc })
                    .ToList()
                : [];
            return Results.Ok(new { agentId, dir = rel, exists, mode = "list", entries });
        }).AddEndpointFilter(new WebIdentity.RequireRoleFilter(PlatformRole.Operator));

        root.MapGet("/workspaces/{agentId}/file", (string agentId, string? path, AgentOptions options,
            Microsoft.Extensions.Hosting.IHostEnvironment env) =>
        {
            var root = WorkspaceMaintenance.ResolveRoot(options.WorkSpaceRoot, env.ContentRootPath);
            var dir = WorkspaceMaintenance.ResolveWorkspaceDir(root, agentId);
            if (dir is null) return Results.BadRequest(new { error = "invalid_agent_id" });
            var full = WorkspaceMaintenance.ResolveFile(dir, path);
            if (full is null) return Results.BadRequest(new { error = "invalid_path" });
            return Results.File(full, "application/octet-stream", Path.GetFileName(full), enableRangeProcessing: true);
        }).AddEndpointFilter(new WebIdentity.RequireRoleFilter(PlatformRole.Operator));

        // 归档占用与手动清理（1.0.319）
        // 审计看板（1.0.321；1.0.322 增加按员工 / 知聚维度；1.0.326 加按工具；1.0.327 支持叠加员工）
        root.MapGet("/work-audits/timeline", (int? days, string? agentId, string? groupId, string? q, string? by, string? stack,
            GroupHub hub, AgentCatalog catalog, IServiceProvider sp) =>
        {
            var archive = sp.GetService<WorkAuditArchive>();
            var clamped = Math.Clamp(days ?? 7, 1, 90);
            var r = WorkAuditReport.Collect(hub.Store, id => catalog.GetDefinition(id)?.Nickname, archive,
                clamped, agentId, groupId, q, 5000);
            var dim = NormalizeTimelineDim(by);
            if (string.Equals(stack, "agent", StringComparison.OrdinalIgnoreCase) && dim != "tool" && dim != "agent")
            {
                return Results.Ok(new
                {
                    by = dim,
                    stack = "agent",
                    window = clamped,
                    buckets = WorkAuditReport.StackByAgent(r.Items, clamped, dim),
                });
            }
            return Results.Ok(new { by = dim, stack = (string?)null, window = clamped, buckets = TimelineBuckets(r.Items, clamped, dim) });
        }).AddEndpointFilter(new WebIdentity.RequireRoleFilter(PlatformRole.Operator));

        // 看板导出 CSV（1.0.323）：与当前维度 / 筛选同一口径，一行一个桶。
        root.MapGet("/work-audits/timeline.csv", (int? days, string? agentId, string? groupId, string? q, string? by,
            GroupHub hub, AgentCatalog catalog, IServiceProvider sp) =>
        {
            var archive = sp.GetService<WorkAuditArchive>();
            var clamped = Math.Clamp(days ?? 7, 1, 90);
            var r = WorkAuditReport.Collect(hub.Store, id => catalog.GetDefinition(id)?.Nickname, archive,
                clamped, agentId, groupId, q, 5000);
            var dim = NormalizeTimelineDim(by);
            var csv = WorkAuditReport.ToTimelineCsv(TimelineBuckets(r.Items, clamped, dim));
            return Results.File(System.Text.Encoding.UTF8.GetBytes(csv), "text/csv", $"work-audit-timeline-{dim}-{clamped}d.csv");
        }).AddEndpointFilter(new WebIdentity.RequireRoleFilter(PlatformRole.Operator));

        root.MapGet("/work-audits/storage", (AgentOptions options, IServiceProvider sp) =>
        {
            var archive = sp.GetService<WorkAuditArchive>();
            if (archive is null) return Results.Ok(new { available = false });
            var stats = archive.Stats();
            return Results.Ok(new
            {
                available = true,
                dir = archive.RootDir,
                files = stats.Files,
                bytes = stats.Bytes,
                oldestUtc = stats.Oldest,
                newestUtc = stats.Newest,
                entries = stats.Entries,
                failed = stats.Failed,
                tools = stats.Tools,
                retentionDays = options.WorkAuditRetentionDays,
            });
        }).AddEndpointFilter(new WebIdentity.RequireRoleFilter(PlatformRole.Operator));

        root.MapPost("/work-audits/sweep", (HttpContext ctx, AgentOptions options, AuthService auth,
            IServiceProvider sp, AguiGroupChat.Hub.Infra.AuditLogService audit) =>
        {
            var archive = sp.GetService<WorkAuditArchive>();
            if (archive is null) return Results.Ok(new { ok = true, deleted = 0 });
            var deleted = archive.Sweep(options.WorkAuditRetentionDays);
            var me = WebIdentity.UserId(ctx)!;
            audit.Record("admin.workaudit.sweep", me, auth.GetUser(me)?.Username,
                targetType: "workAudit", targetId: "*", detail: $"清理 {deleted} 个过期归档分片");
            return Results.Ok(new { ok = true, deleted, retentionDays = options.WorkAuditRetentionDays });
        }).AddEndpointFilter(new WebIdentity.RequireRoleFilter(PlatformRole.Operator));

        // 工作区批量下载（1.0.321；1.0.322 支持按目录整包）：按相对路径打包 ZIP（仅文件；单次 200 个 / 200 MB 上限）。
        root.MapPost("/workspaces/{agentId}/download", (string agentId, WorkspaceDownloadRequest req, AgentOptions options,
            Microsoft.Extensions.Hosting.IHostEnvironment env) =>
        {
            const int maxFiles = 200;
            var root = WorkspaceMaintenance.ResolveRoot(options.WorkSpaceRoot, env.ContentRootPath);
            var dir = WorkspaceMaintenance.ResolveWorkspaceDir(root, agentId);
            if (dir is null) return Results.BadRequest(new { error = "invalid_agent_id" });

            var paths = (req.Paths ?? []).Where((p) => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.Ordinal).ToList();
            var byDir = !string.IsNullOrWhiteSpace(req.Dir);
            if (!byDir && paths.Count == 0) return Results.BadRequest(new { error = "missing_paths" });
            if (!byDir && paths.Count > maxFiles) return Results.BadRequest(new { error = "too_many_files" });

            string? dirFull = null;
            if (byDir)
            {
                // 目录整包：允许工作区根（"."），越界 / 不存在 / 链接一律拒绝
                if (!WorkspaceMaintenance.TryResolveTarget(dir, req.Dir, out var resolved) || !Directory.Exists(resolved))
                    return Results.BadRequest(new { error = "invalid_path" });
                dirFull = resolved;
            }

            long total = 0;
            var added = 0;
            string? failure = null;
            using var ms = new MemoryStream();
            using (var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
            {
                if (dirFull is not null)
                {
                    List<string> files;
                    try
                    {
                        files = Directory.EnumerateFiles(dirFull, "*", SearchOption.AllDirectories)
                            .Where((f) => (File.GetAttributes(f) & FileAttributes.ReparsePoint) == 0)
                            .ToList();
                    }
                    catch
                    {
                        return Results.BadRequest(new { error = "invalid_path" });
                    }
                    if (files.Count > maxFiles) return Results.BadRequest(new { error = "too_many_files" });
                    foreach (var full in files)
                    {
                        var rel = Path.GetRelativePath(dir, full).Replace('\\', '/');
                        var err = TryAddZipEntry(zip, full, rel, ref total, ref added);
                        if (err is not null) { failure = err; break; }
                    }
                }
                else
                {
                    foreach (var rel in paths)
                    {
                        var full = WorkspaceMaintenance.ResolveFile(dir, rel);
                        if (full is null) continue; // 不存在 / 越界：跳过
                        var err = TryAddZipEntry(zip, full, Path.GetRelativePath(dir, full).Replace('\\', '/'), ref total, ref added);
                        if (err is not null) { failure = err; break; }
                    }
                }
            }
            if (failure is not null) return Results.BadRequest(new { error = failure });
            // 空 Zip 归档也会写出 22 字节的中央目录，所以按「实际加入的条目数」判断（1.0.321）。
            if (added == 0) return Results.BadRequest(new { error = "no_files" });
            return Results.File(ms.ToArray(), "application/zip", $"workspace-{agentId}.zip");
        }).AddEndpointFilter(new WebIdentity.RequireRoleFilter(PlatformRole.Operator));

        // 工作区文件操作（1.0.315）：删除单文件 / 新建目录；同样走路径校验 + 审计日志。
        root.MapDelete("/workspaces/{agentId}/file", (string agentId, string? path, HttpContext ctx, AgentOptions options,
            AuthService auth, Microsoft.Extensions.Hosting.IHostEnvironment env, AguiGroupChat.Hub.Infra.AuditLogService audit) =>
        {
            var root = WorkspaceMaintenance.ResolveRoot(options.WorkSpaceRoot, env.ContentRootPath);
            var dir = WorkspaceMaintenance.ResolveWorkspaceDir(root, agentId);
            if (dir is null) return Results.BadRequest(new { error = "invalid_agent_id" });
            if (!WorkspaceMaintenance.DeleteFile(dir, path)) return Results.BadRequest(new { error = "invalid_path" });
            var me = WebIdentity.UserId(ctx)!;
            audit.Record("admin.workspaces.delete", me, auth.GetUser(me)?.Username, targetType: "workspace", targetId: agentId,
                detail: $"删除文件 {path}");
            return Results.Ok(new { ok = true, agentId, path });
        }).AddEndpointFilter(new WebIdentity.RequireRoleFilter(PlatformRole.Operator));

        root.MapPost("/workspaces/{agentId}/dir", (string agentId, string? path, HttpContext ctx, AgentOptions options,
            AuthService auth, Microsoft.Extensions.Hosting.IHostEnvironment env, AguiGroupChat.Hub.Infra.AuditLogService audit) =>
        {
            var root = WorkspaceMaintenance.ResolveRoot(options.WorkSpaceRoot, env.ContentRootPath);
            var dir = WorkspaceMaintenance.ResolveWorkspaceDir(root, agentId);
            if (dir is null) return Results.BadRequest(new { error = "invalid_agent_id" });
            if (!WorkspaceMaintenance.CreateDirectory(dir, path)) return Results.BadRequest(new { error = "invalid_path" });
            var me = WebIdentity.UserId(ctx)!;
            audit.Record("admin.workspaces.mkdir", me, auth.GetUser(me)?.Username, targetType: "workspace", targetId: agentId,
                detail: $"新建目录 {path}");
            return Results.Ok(new { ok = true, agentId, path });
        }).AddEndpointFilter(new WebIdentity.RequireRoleFilter(PlatformRole.Operator));

        // 工作区文件上传（1.0.316）：multipart/form-data，可指定相对路径（默认用原文件名）；单文件上限 50 MB。
        root.MapPost("/workspaces/{agentId}/upload", async (string agentId, string? path, bool? overwrite, HttpContext ctx, AgentOptions options,
            AuthService auth, Microsoft.Extensions.Hosting.IHostEnvironment env, AguiGroupChat.Hub.Infra.AuditLogService audit) =>
        {
            var root = WorkspaceMaintenance.ResolveRoot(options.WorkSpaceRoot, env.ContentRootPath);
            var dir = WorkspaceMaintenance.ResolveWorkspaceDir(root, agentId);
            if (dir is null) return Results.BadRequest(new { error = "invalid_agent_id" });
            if (!ctx.Request.HasFormContentType || ctx.Request.Form.Files.Count == 0)
                return Results.BadRequest(new { error = "missing_file" });
            var file = ctx.Request.Form.Files[0];
            const long maxBytes = 50L * 1024 * 1024;
            if (file.Length <= 0) return Results.BadRequest(new { error = "empty_file" });
            if (file.Length > maxBytes) return Results.BadRequest(new { error = "file_too_large" });

            var rel = string.IsNullOrWhiteSpace(path) ? file.FileName : path!;
            if (!WorkspaceMaintenance.TryResolveTarget(dir, rel, out var full))
                return Results.BadRequest(new { error = "invalid_path" });
            if (Directory.Exists(full)) return Results.BadRequest(new { error = "path_is_directory" });

            var parent = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

            // 覆盖策略（1.0.323）：overwrite=false 时同名文件直接跳过，不改动已有内容。
            if (overwrite == false && File.Exists(full))
                return Results.Ok(new { ok = true, skipped = true, agentId, path = Path.GetRelativePath(dir, full).Replace('\\', '/') });
            await using (var stream = file.OpenReadStream())
            await using (var target = File.Create(full))
            {
                await stream.CopyToAsync(target, ctx.RequestAborted);
            }

            var me = WebIdentity.UserId(ctx)!;
            var saved = Path.GetRelativePath(dir, full).Replace('\\', '/');
            audit.Record("admin.workspaces.upload", me, auth.GetUser(me)?.Username, targetType: "workspace", targetId: agentId,
                detail: $"上传文件 {saved}（{file.Length} 字节）");
            return Results.Ok(new { ok = true, agentId, path = saved, bytes = file.Length });
        }).AddEndpointFilter(new WebIdentity.RequireRoleFilter(PlatformRole.Operator));

        // 断点续传（1.0.327）：先问已收到多少字节，再从偏移继续分片，满 total 后合并为正式文件。
        root.MapGet("/workspaces/{agentId}/upload-offset", (string agentId, string? path, long? size, AgentOptions options,
            Microsoft.Extensions.Hosting.IHostEnvironment env) =>
        {
            var root = WorkspaceMaintenance.ResolveRoot(options.WorkSpaceRoot, env.ContentRootPath);
            var dir = WorkspaceMaintenance.ResolveWorkspaceDir(root, agentId);
            if (dir is null) return Results.BadRequest(new { error = "invalid_agent_id" });
            if (string.IsNullOrWhiteSpace(path)) return Results.BadRequest(new { error = "missing_path" });
            if (!WorkspaceMaintenance.TryResolveTarget(dir, path, out var full))
                return Results.BadRequest(new { error = "invalid_path" });
            if (Directory.Exists(full)) return Results.BadRequest(new { error = "path_is_directory" });
            var expected = size ?? 0;
            if (expected <= 0) return Results.BadRequest(new { error = "missing_size" });
            if (expected > MaxUploadBytes) return Results.BadRequest(new { error = "file_too_large" });

            var part = UploadPartPath(env.ContentRootPath, agentId, path!);
            CleanupUploadParts(Path.GetDirectoryName(part), TimeSpan.FromDays(2));
            var complete = File.Exists(full) && new FileInfo(full).Length == expected;
            long offset = complete ? expected : (File.Exists(part) ? new FileInfo(part).Length : 0);
            if (offset > expected) // 残留的更大分片：丢弃重传
            {
                try { File.Delete(part); } catch { /* 忽略 */ }
                offset = 0;
            }
            return Results.Ok(new { offset, complete, bytes = expected });
        }).AddEndpointFilter(new WebIdentity.RequireRoleFilter(PlatformRole.Operator));

        root.MapPost("/workspaces/{agentId}/upload-chunk", async (string agentId, string? path, long? offset, long? total,
            bool? overwrite, HttpContext ctx, AgentOptions options, AuthService auth,
            Microsoft.Extensions.Hosting.IHostEnvironment env, AguiGroupChat.Hub.Infra.AuditLogService audit) =>
        {
            var root = WorkspaceMaintenance.ResolveRoot(options.WorkSpaceRoot, env.ContentRootPath);
            var dir = WorkspaceMaintenance.ResolveWorkspaceDir(root, agentId);
            if (dir is null) return Results.BadRequest(new { error = "invalid_agent_id" });
            if (string.IsNullOrWhiteSpace(path)) return Results.BadRequest(new { error = "missing_path" });
            if (!WorkspaceMaintenance.TryResolveTarget(dir, path, out var full))
                return Results.BadRequest(new { error = "invalid_path" });
            if (Directory.Exists(full)) return Results.BadRequest(new { error = "path_is_directory" });
            var expected = total ?? 0;
            if (expected <= 0) return Results.BadRequest(new { error = "missing_total" });
            if (expected > MaxUploadBytes) return Results.BadRequest(new { error = "file_too_large" });
            if (!ctx.Request.HasFormContentType || ctx.Request.Form.Files.Count == 0)
                return Results.BadRequest(new { error = "missing_file" });
            var file = ctx.Request.Form.Files[0];
            if (file.Length <= 0) return Results.BadRequest(new { error = "empty_file" });

            var part = UploadPartPath(env.ContentRootPath, agentId, path!);
            var partDir = Path.GetDirectoryName(part)!;
            Directory.CreateDirectory(partDir);
            CleanupUploadParts(partDir, TimeSpan.FromDays(2));

            var off = Math.Max(0, offset ?? 0);
            var existing = File.Exists(part) ? new FileInfo(part).Length : 0;
            if (off == 0)
            {
                try { File.Delete(part); } catch { /* 忽略 */ }
                existing = 0;
            }
            // 偏移对不上就让客户端按服务端真实偏移重传（避免静默拼错）
            if (existing != off) return Results.Conflict(new { error = "offset_mismatch", offset = existing });

            await using (var fs = new FileStream(part, FileMode.Append, FileAccess.Write, FileShare.None))
            await using (var src = file.OpenReadStream())
            {
                await src.CopyToAsync(fs, ctx.RequestAborted);
            }
            var newLen = new FileInfo(part).Length;
            if (newLen > expected)
            {
                try { File.Delete(part); } catch { /* 忽略 */ }
                return Results.BadRequest(new { error = "file_too_large" });
            }
            if (newLen < expected) return Results.Ok(new { ok = true, partial = true, offset = newLen });

            var savedRel = Path.GetRelativePath(dir, full).Replace('\\', '/');
            if (overwrite == false && File.Exists(full))
            {
                try { File.Delete(part); } catch { /* 忽略 */ }
                return Results.Ok(new { ok = true, skipped = true, agentId, path = savedRel });
            }
            var parent = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
            File.Move(part, full, overwrite: true);

            var me = WebIdentity.UserId(ctx)!;
            audit.Record("admin.workspaces.upload", me, auth.GetUser(me)?.Username, targetType: "workspace", targetId: agentId,
                detail: $"上传文件 {savedRel}（{expected} 字节，分片续传）");
            return Results.Ok(new { ok = true, agentId, path = savedRel, bytes = expected });
        }).AddEndpointFilter(new WebIdentity.RequireRoleFilter(PlatformRole.Operator));

        // 工作区操作审计汇总（1.0.314；1.0.315 起读按月归档）：按时间 / 员工 / 关键词查最近 N 天的 work_* 调用，可导出 CSV。
        // 归档服务用 GetService 可选解析：轻量宿主（测试 / 嵌入式）没注册时自动退回扫消息。
        root.MapGet("/work-audits", (int? days, string? agentId, string? groupId, string? q, int? limit, string? date, string? tool,
            GroupHub hub, AgentCatalog catalog, IServiceProvider sp) =>
        {
            var archive = sp.GetService<WorkAuditArchive>();
            var r = WorkAuditReport.Collect(hub.Store, id => catalog.GetDefinition(id)?.Nickname, archive,
                days ?? 7, agentId, groupId, q, limit ?? 500);
            // 看板钻取 / 工具筛选（1.0.324–1.0.325）：只筛展示行，facets 仍按完整窗口统计
            var items = WorkAuditReport.FilterByTool(WorkAuditReport.FilterByDate(r.Items, date), tool);
            return Results.Ok(new
            {
                items = items.Select(e => new
                {
                    e.GroupId, e.GroupName, e.AgentId, e.AgentName, e.MessageId, e.TopicId, e.At,
                    e.Tool, e.Ok, e.DurationMs, e.Text,
                }),
                r.Truncated,
                r.ScannedMessages,
                r.Source,
                groups = r.Groups,
                agents = r.Agents,
                days = Math.Clamp(days ?? 7, 1, 90),
                date = string.IsNullOrWhiteSpace(date) ? null : date,
                tool = string.IsNullOrWhiteSpace(tool) ? null : tool,
            });
        }).AddEndpointFilter(new WebIdentity.RequireRoleFilter(PlatformRole.Operator));

        // 导出（1.0.318）：group=message → 按消息聚合；split=month → 打成 ZIP，一月一个 CSV
        // 1.0.324/1.0.325 起跟随「日期 / 工具」筛选，导出口径与表格一致
        root.MapGet("/work-audits.csv", (int? days, string? agentId, string? groupId, string? q,
            string? group, string? split, string? date, string? tool, GroupHub hub, AgentCatalog catalog, IServiceProvider sp) =>
        {
            var archive = sp.GetService<WorkAuditArchive>();
            var collected = WorkAuditReport.Collect(hub.Store, id => catalog.GetDefinition(id)?.Nickname, archive,
                days ?? 7, agentId, groupId, q, 5000);
            var rowsFiltered = WorkAuditReport.FilterByTool(WorkAuditReport.FilterByDate(collected.Items, date), tool);
            var r = collected with { Items = rowsFiltered };
            var grouped = string.Equals(group, "message", StringComparison.OrdinalIgnoreCase);

            if (string.Equals(split, "month", StringComparison.OrdinalIgnoreCase))
            {
                using var ms = new MemoryStream();
                using (var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
                {
                    foreach (var month in r.Items
                                 .GroupBy((e) => DateTimeOffset.FromUnixTimeMilliseconds(e.At).UtcDateTime
                                     .ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture))
                                 .OrderBy((g) => g.Key, StringComparer.Ordinal))
                    {
                        var rows = month.ToList();
                        var csv = grouped ? WorkAuditReport.ToGroupedCsv(rows) : WorkAuditReport.ToCsv(rows);
                        var entry = zip.CreateEntry($"work-audits-{month.Key}.csv", System.IO.Compression.CompressionLevel.Fastest);
                        using var stream = entry.Open();
                        var bytes = System.Text.Encoding.UTF8.GetBytes(csv);
                        stream.Write(bytes, 0, bytes.Length);
                    }
                }
                return Results.File(ms.ToArray(), "application/zip", $"work-audits-{days ?? 7}d.zip");
            }

            var text = grouped ? WorkAuditReport.ToGroupedCsv(r.Items) : WorkAuditReport.ToCsv(r.Items);
            return Results.Text(text, "text/csv; charset=utf-8");
        }).AddEndpointFilter(new WebIdentity.RequireRoleFilter(PlatformRole.Operator));

        // 轻量运行指标（6.1）：智能体调用 / 桥接 / 记忆命中 / 输出长度 的进程内计数，仅管理员及以上（含运维）。
        root.MapGet("/metrics", (HttpContext ctx, AguiGroupChat.Agents.MetricsService metrics) =>
        {
            return Results.Ok(metrics.Snapshot());
        }).AddEndpointFilter(new WebIdentity.RequireRoleFilter(PlatformRole.Operator));

        // 跨副本诊断（6.1 / 6.2）：列出活跃副本 + 汇总 + Redis 健康（Redis 多副本时跨进程可见）。
        // 仅 Redis 多副本部署下副本数会 > 1；单副本时也返回自身（界面自洽）。
        root.MapGet("/replicas", (IReplicaRegistry registry, ReplicaIdentity identity,
            AguiGroupChat.Agents.MetricsService metrics, IServiceProvider sp) =>
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var list = registry.List().ToList();
            // 启动初期尚未发布心跳时用当前读数兑底，保证列表至少含自身
            if (!list.Any(r => r.ReplicaId == identity.Id))
            {
                var c = metrics.Counters();
                list.Add(new ReplicaSnapshot
                {
                    ReplicaId = identity.Id, Host = identity.Host, Pid = identity.Pid,
                    StartedAtMs = identity.StartedAtMs, LastSeenMs = now,
                    Invocations = c.Invocations, Accepted = c.Accepted, Rejected = c.Rejected,
                    BridgeCalls = c.BridgeCalls, BridgeFailures = c.BridgeFailures,
                    MemoryHits = c.MemoryHits, MemoryEmpty = c.MemoryEmpty, OutputChars = c.OutputChars,
                    WorkingSetBytes = c.WorkingSetBytes, GcHeapBytes = c.GcHeapBytes,
                });
            }

            var replicas = list.Select(r => new
            {
                r.ReplicaId,
                r.Host,
                r.Pid,
                r.StartedAtMs,
                r.LastSeenMs,
                ageMs = now - r.LastSeenMs,
                isSelf = r.ReplicaId == identity.Id,
                r.Invocations,
                r.Accepted,
                r.Rejected,
                r.BridgeCalls,
                r.BridgeFailures,
                r.MemoryHits,
                r.MemoryEmpty,
                r.OutputChars,
                r.WorkingSetBytes,
                r.GcHeapBytes,
            }).ToList();

            var redis = sp.GetService<RedisContext>();
            var health = redis?.Ping();
            return Results.Ok(new
            {
                selfId = identity.Id,
                count = replicas.Count,
                replicas,
                aggregate = new
                {
                    invocations = list.Sum(r => r.Invocations),
                    accepted = list.Sum(r => r.Accepted),
                    rejected = list.Sum(r => r.Rejected),
                    bridgeCalls = list.Sum(r => r.BridgeCalls),
                    bridgeFailures = list.Sum(r => r.BridgeFailures),
                    memoryHits = list.Sum(r => r.MemoryHits),
                    memoryEmpty = list.Sum(r => r.MemoryEmpty),
                    outputChars = list.Sum(r => r.OutputChars),
                    workingSetBytes = list.Sum(r => r.WorkingSetBytes),
                },
                redis = redis is null ? null : new
                {
                    connected = health!.Value.Connected,
                    latencyMs = health.Value.Connected ? Math.Round(health.Value.LatencyMs, 2) : (double?)null,
                    endpoints = redis.Endpoints,
                },
            });
        }).AddEndpointFilter(new WebIdentity.RequireRoleFilter(PlatformRole.Operator));

        // 运维配置只读快照（6.3 数据面，仅管理员）：集中展示散在各处 appsettings / .env 的关键参数，供治理与排障。
        root.MapGet("/config", (HttpContext ctx, AuthOptions authOptions, GroupChatOptions groupChat, StorageOptions storage,
            AgentOptions agents, IServiceProvider sp) =>
        {
            var persistence = sp.GetService<AguiGroupChat.Hub.Persistence.PersistenceOptions>() ?? new AguiGroupChat.Hub.Persistence.PersistenceOptions();
            return Results.Ok(new
            {
                auth = new
                {
                    authOptions.RequireTokenOnRealTime,
                    sessionTtlHours = authOptions.SessionTtlHours,
                    absoluteSessionTtlDays = authOptions.AbsoluteSessionTtlDays,
                    firstUserIsAdmin = authOptions.FirstUserIsAdmin,
                    hasAdminUserIds = !string.IsNullOrWhiteSpace(authOptions.AdminUserIds),
                    allowedOrigins = authOptions.AllowedOrigins,
                },
                groupChat = new
                {
                    groupChat.MessageHistoryLimit,
                    groupChat.MessageWriteDebounceMs,
                    groupChat.MaxMessageChars,
                    groupChat.MaxConcurrentAgentInvocations,
                    groupChat.MessageRetentionDays,
                },
                storage = new
                {
                    provider = storage.Provider,
                    autoCreateSchema = storage.AutoCreateSchema,
                    hasConnectionString = !string.IsNullOrWhiteSpace(storage.ConnectionString),
                },
                persistence = new
                {
                    persistence.Enabled,
                    filePath = persistence.FilePath,
                },
                agents = new
                {
                    provider = agents.Provider,
                    enableTools = agents.EnableTools,
                    enableWebTools = agents.EnableWebTools,
                    workToolsEnabled = agents.WorkToolsEnabled,
                    workToolCommands = agents.WorkToolCommands,
                    thinkingMode = agents.ThinkingMode,
                    dailyTokenQuotaPerUser = agents.DailyTokenQuotaPerUser,
                    dailyTokenQuotaPerGroup = agents.DailyTokenQuotaPerGroup,
                    requireApprovalToolNames = agents.RequireApprovalToolNames,
                    memory = new
                    {
                        enabled = agents.Memory.Enabled,
                        provider = agents.Memory.Provider,
                        embeddingModel = agents.Memory.EmbeddingModel,
                        embeddingDimensions = agents.Memory.EmbeddingDimensions,
                        retentionDays = agents.Memory.RetentionDays,
                        scope = agents.Memory.Scope,
                    },
                },
            });
        }).AddEndpointFilter(new WebIdentity.RequireAdminFilter());

        // ---- 账号彻底删除（企业合规 · 数据擦除）：管理员删除他人账号（不可删除自己——本人请走「注销账户」自助入口）----
        root.MapDelete("/users/{userId}", async (string userId, HttpContext ctx,
            [Microsoft.AspNetCore.Mvc.FromServices] AccountErasureService erasure,
            [Microsoft.AspNetCore.Mvc.FromServices] AuthService auth, CancellationToken ct) =>
        {
            try
            {
                var me = WebIdentity.UserId(ctx)!;
                if (me == userId)
                    return Results.BadRequest(new AguiError(ErrorCodes.BadRequest, "不能经管理接口删除自己，请在「我的资料」使用「注销账户」（需密码确认）"));
                var report = await erasure.EraseAsync(userId, me, ctx.Request.Query["reason"].ToString(), ct);
                return Results.Ok(new
                {
                    ok = true,
                    userId,
                    accountRemoved = report.AccountRemoved,
                    groupsHandled = report.GroupsHandled,
                    memoriesErased = report.MemoriesErased,
                    knowledgeBasesRemoved = report.KnowledgeBasesRemoved,
                    messagesAnonymized = report.MessagesAnonymized,
                    agentsRemoved = report.AgentsRemoved,
                    skillsRemoved = report.SkillsRemoved,
                });
            }
            catch (AguiProtocolException ex) { return MapErasureError(ex); }
        }).AddEndpointFilter(new WebIdentity.RequireAdminFilter());

        // ---- 孤儿定义盘点（企业合规运营）：OwnerId 指向已注销账号的数字员工 / 技能 ----------------
        root.MapGet("/orphans", (HttpContext ctx, AuthService auth, GroupHub hub,
            AgentCatalog catalog, AgentSkillCatalog skills,
            AguiGroupChat.Hub.Infra.AuditLogService audit) =>
        {
            var userIds = auth.ListUsers().Select(u => u.UserId).ToHashSet(StringComparer.Ordinal);
            var allDefs = catalog.ListDefinitions();
            var skillUsedBy = BuildSkillUsage(allDefs);
            var agentRefs = BuildAgentReferences(allDefs);

            // 现存群中以数字员工身份出现的成员（agentId → 所在群名列表）
            var memberGroups = hub.Store.AllGroups()
                .SelectMany(g => hub.Store.ListMembers(g.GroupId)
                    .Where(m => m.MemberType == MemberType.Agent)
                    .Select(m => new { m.MemberId, GroupName = g.GroupName }))
                .GroupBy(x => x.MemberId, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Select(x => x.GroupName).Distinct().ToList(), StringComparer.Ordinal);

            var agents = allDefs
                .Where(d => !userIds.Contains(d.OwnerId ?? ""))
                .Select(d => new
                {
                    d.AgentId,
                    d.Nickname,
                    d.Description,
                    ownerId = d.OwnerId,
                    d.IsPrivate,
                    memberGroups = memberGroups.TryGetValue(d.AgentId, out var mg) ? mg : [],
                    referencedBy = agentRefs.TryGetValue(d.AgentId, out var rb) ? rb : [],
                })
                .OrderBy(a => a.AgentId)
                .ToList();

            var ownedSkills = skills.ListAll()
                .Where(s => !userIds.Contains(s.OwnerId ?? ""))
                .Select(s => new
                {
                    s.SkillId,
                    s.Name,
                    kind = s.Kind.ToString(),
                    ownerId = s.OwnerId,
                    usedBy = skillUsedBy.TryGetValue(s.SkillId, out var ub) ? ub : [],
                })
                .OrderBy(s => s.SkillId)
                .ToList();

            return Results.Ok(new { agents, skills = ownedSkills });
        }).AddEndpointFilter(new WebIdentity.RequireAdminFilter());

        // 接管：把孤儿定义的 OwnerId 改为当前管理员（可继续编辑 / 挂载，不再指向已注销账号）
        root.MapPost("/orphans/agents/{agentId}/adopt", (string agentId, HttpContext ctx, AuthService auth,
            AgentCatalog catalog, AgentSkillCatalog _, AguiGroupChat.Hub.Infra.AuditLogService audit) =>
            OrphanRun(() =>
            {
                var me = WebIdentity.UserId(ctx)!;
                var userIds = auth.ListUsers().Select(u => u.UserId).ToHashSet(StringComparer.Ordinal);
                var def = catalog.GetDefinition(agentId)
                    ?? throw new AguiProtocolException(ErrorCodes.AgentNotFound, "数字员工不存在");
                if (userIds.Contains(def.OwnerId ?? ""))
                    throw new AguiProtocolException(ErrorCodes.AgentPermissionDenied, "该数字员工仍属现存账号，无需接管（请走常规管理）");
                var originalOwner = def.OwnerId;
                def.OwnerId = me;
                catalog.Upsert(def);
                audit.Record("admin.orphan.adopt", me, auth.GetUser(me)?.Username, targetType: "agent", targetId: agentId,
                    detail: $"接管孤儿数字员工（原 Owner {originalOwner}）");
                return Results.Ok(new { ok = true, agentId });
            })).AddEndpointFilter(new WebIdentity.RequireAdminFilter());

        root.MapPost("/orphans/skills/{skillId}/adopt", (string skillId, HttpContext ctx, AuthService auth,
            AgentSkillCatalog skills, AguiGroupChat.Hub.Infra.AuditLogService audit) =>
            OrphanRun(() =>
            {
                var me = WebIdentity.UserId(ctx)!;
                var userIds = auth.ListUsers().Select(u => u.UserId).ToHashSet(StringComparer.Ordinal);
                var def = skills.Get(skillId)
                    ?? throw new AguiProtocolException(ErrorCodes.AgentNotFound, "技能不存在");
                if (userIds.Contains(def.OwnerId ?? ""))
                    throw new AguiProtocolException(ErrorCodes.AgentPermissionDenied, "该技能仍属现存账号，无需接管");
                def.OwnerId = me;
                skills.Upsert(def);
                audit.Record("admin.orphan.adopt", me, auth.GetUser(me)?.Username, targetType: "skill", targetId: skillId);
                return Results.Ok(new { ok = true, skillId });
            })).AddEndpointFilter(new WebIdentity.RequireAdminFilter());

        // 删除孤儿定义（安全闸：仅当不再被任何现存知聚成员 / 保留定义引用）
        root.MapDelete("/orphans/agents/{agentId}", (string agentId, HttpContext ctx, AuthService auth,
            GroupHub hub, AgentCatalog catalog, AgentRegistry registry,
            AguiGroupChat.Hub.Infra.AuditLogService audit) =>
            OrphanRun(() =>
            {
                var me = WebIdentity.UserId(ctx)!;
                var userIds = auth.ListUsers().Select(u => u.UserId).ToHashSet(StringComparer.Ordinal);
                var def = catalog.GetDefinition(agentId)
                    ?? throw new AguiProtocolException(ErrorCodes.AgentNotFound, "数字员工不存在");
                if (userIds.Contains(def.OwnerId ?? ""))
                    throw new AguiProtocolException(ErrorCodes.AgentPermissionDenied, "该数字员工仍属现存账号，请走常规管理删除");
                var stillMember = hub.Store.AllGroups().Any(g => hub.Store.GetMember(g.GroupId, agentId)?.MemberType == MemberType.Agent);
                if (stillMember)
                    throw new AguiProtocolException(ErrorCodes.AgentPermissionDenied, "该数字员工仍是现存知聚的成员，删除会悬空知聚：请先接管并移除，或解散相关知聚");
                var referenced = BuildAgentReferences(catalog.ListDefinitions()).TryGetValue(agentId, out var rb) && rb.Count > 0;
                if (referenced)
                    throw new AguiProtocolException(ErrorCodes.AgentPermissionDenied, "该数字员工仍被其他数字员工引用（交接 / 升级），请先接管调整引用");
                catalog.Remove(agentId);
                registry.Unregister(agentId, null);
                audit.Record("admin.orphan.delete", me, auth.GetUser(me)?.Username, targetType: "agent", targetId: agentId);
                return Results.Ok(new { ok = true, agentId });
            })).AddEndpointFilter(new WebIdentity.RequireAdminFilter());

        root.MapDelete("/orphans/skills/{skillId}", (string skillId, HttpContext ctx, AuthService auth,
            AgentSkillCatalog skills, AgentCatalog catalog,
            AguiGroupChat.Hub.Infra.AuditLogService audit) =>
            OrphanRun(() =>
            {
                var me = WebIdentity.UserId(ctx)!;
                var userIds = auth.ListUsers().Select(u => u.UserId).ToHashSet(StringComparer.Ordinal);
                var def = skills.Get(skillId)
                    ?? throw new AguiProtocolException(ErrorCodes.AgentNotFound, "技能不存在");
                if (userIds.Contains(def.OwnerId ?? ""))
                    throw new AguiProtocolException(ErrorCodes.AgentPermissionDenied, "该技能仍属现存账号，请走常规管理删除");
                var used = BuildSkillUsage(catalog.ListDefinitions()).TryGetValue(skillId, out var ub) && ub.Count > 0;
                if (used)
                    throw new AguiProtocolException(ErrorCodes.AgentPermissionDenied, "该技能仍被数字员工挂载，请先接管并解除挂载");
                skills.Remove(skillId);
                audit.Record("admin.orphan.delete", me, auth.GetUser(me)?.Username, targetType: "skill", targetId: skillId);
                return Results.Ok(new { ok = true, skillId });
            })).AddEndpointFilter(new WebIdentity.RequireAdminFilter());
    }

    /// <summary>孤儿定义操作统一错误映射（异常 → 结构化响应，避免 500）。</summary>
    private static IResult OrphanRun(Func<IResult> action)
    {
        try { return action(); }
        catch (AguiProtocolException ex) { return MapErasureError(ex); }
    }

    /// <summary>探测用 HttpClient（懒建，短超时）：仅管理员打开「系统状态」时才用，不阻塞主路径。</summary>
    private static readonly HttpClient ProbeHttp = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromMilliseconds(1200),
    })
    { Timeout = TimeSpan.FromMilliseconds(1800) };

    /// <summary>音色列表 / 试听用 HttpClient（合成与列表可能稍慢，给宽松超时）。</summary>
    private static readonly HttpClient VoiceHttp = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromSeconds(3),
    })
    { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>解析音色列表响应：兼容 `{voices:[…]}` / 裸数组，元素为字符串或带 id / name 的对象。</summary>
    private static List<string> ParseVoices(string body)
    {
        var list = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(body);
            JsonElement arr;
            if (doc.RootElement.ValueKind == JsonValueKind.Array) arr = doc.RootElement;
            else if (doc.RootElement.TryGetProperty("voices", out var v) && v.ValueKind == JsonValueKind.Array) arr = v;
            else return list;
            foreach (var el in arr.EnumerateArray())
            {
                if (el.ValueKind == JsonValueKind.String)
                {
                    var s = el.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) list.Add(s!);
                }
                else if (el.ValueKind == JsonValueKind.Object)
                {
                    var id = el.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString()
                        : el.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(id)) list.Add(id!);
                }
            }
        }
        catch { /* 非 JSON / 结构不符：返回空列表（前端显示“无”） */ }
        return list;
    }

    /// <summary>
    /// 语音端点可达性探测（多模态进阶 · 语音问答）：发一个 GET，**只要收到任意 HTTP 响应即算可达**
    /// （`/v1/audio/*` 对 GET 通常回 405，那也已证明服务在监听）；连接失败 / 超时 / 未配置返回 null 或 false。
    /// 端点来自服务端配置（非用户输入），无 SSRF 面。
    /// </summary>
    private static async Task<bool?> ProbeAsync(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint)) return null;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, endpoint);
            using var res = await ProbeHttp.SendAsync(req).ConfigureAwait(false);
            return true;
        }
        catch { return false; }
    }

    /// <summary>账号删除 / 孤儿操作错误码 → HTTP 状态映射（404 目标不存在；403 权限不足 / 防呆；其余 400）。</summary>
    private static IResult MapErasureError(AguiProtocolException ex) => ex.ErrorCode switch
    {
        ErrorCodes.UserNotFound or ErrorCodes.AgentNotFound or ErrorCodes.GroupNotFound
            => Results.NotFound(new AguiError(ex.ErrorCode, ex.Message)),
        ErrorCodes.GroupPermissionDenied or ErrorCodes.UserUnauthorized or ErrorCodes.AgentPermissionDenied
            => Results.Json(new AguiError(ex.ErrorCode, ex.Message), statusCode: StatusCodes.Status403Forbidden),
        _ => Results.BadRequest(new AguiError(ex.ErrorCode, ex.Message)),
    };

    /// <summary>现存数字员工定义中「中继 / 升级」引用图：agentId → 引用它的数字员工 id 列表（孤儿盘点用）。</summary>
    private static Dictionary<string, List<string>> BuildAgentReferences(IReadOnlyList<AgentDefinition> defs)
    {
        var map = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var d in defs)
        {
            AddRef(map, d.RelayToAgentId, d.AgentId);
            AddRef(map, d.EscalationAgentId, d.AgentId);
        }
        return map;
    }

    private static void AddRef(Dictionary<string, List<string>> map, string? target, string source)
    {
        if (string.IsNullOrWhiteSpace(target) || target == source) return;
        if (!map.TryGetValue(target!, out var list)) map[target!] = list = new List<string>();
        list.Add(source);
    }

    /// <summary>现存数字员工挂载的技能使用图：skillId → 挂载它的数字员工 id 列表。</summary>
    private static Dictionary<string, List<string>> BuildSkillUsage(IReadOnlyList<AgentDefinition> defs)
    {
        var map = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var d in defs)
        {
            foreach (var sid in d.SkillDefIds ?? [])
                if (!string.IsNullOrWhiteSpace(sid)) AddRef(map, sid, d.AgentId);
            foreach (var s in d.Skills ?? [])
                if (!string.IsNullOrWhiteSpace(s.SkillId)) AddRef(map, s.SkillId, d.AgentId);
        }
        return map;
    }

    /// <summary>追加一条审计 CSV 行（RFC 4180：含分隔符 / 引号 / 换行的字段加引号包裹，内部引号双写）。</summary>
    private static void AppendCsvRow(System.Text.StringBuilder sb, AguiGroupChat.Hub.Infra.AuditEntry e)
    {
        sb.Append(CsvField(e.Id)).Append(',');
        sb.Append(CsvField(DateTimeOffset.FromUnixTimeMilliseconds(e.Timestamp).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"))).Append(',');
        sb.Append(e.Timestamp).Append(',');
        sb.Append(CsvField(e.Action)).Append(',');
        sb.Append(CsvField(e.ActorId)).Append(',');
        sb.Append(CsvField(e.ActorUsername)).Append(',');
        sb.Append(CsvField(e.GroupId)).Append(',');
        sb.Append(CsvField(e.TargetType)).Append(',');
        sb.Append(CsvField(e.TargetId)).Append(',');
        sb.Append(CsvField(e.Detail)).Append(',');
        sb.Append(CsvField(e.Result)).Append('\n');
    }

    private static string CsvField(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var needsQuote = value.IndexOfAny([',', '"', '\r', '\n']) >= 0;
        return needsQuote ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }

    private static IResult Run(Func<IResult> action)
    {
        try { return action(); }
        catch (AguiProtocolException ex) { return Results.BadRequest(new AguiError(ex.ErrorCode, ex.Message)); }
    }

    /// <summary>看板维度归一（1.0.323；1.0.326 加按工具）：非法值统一回落到按天。</summary>
    private static string NormalizeTimelineDim(string? by)
        => string.Equals(by, "agent", StringComparison.OrdinalIgnoreCase) ? "agent"
         : string.Equals(by, "group", StringComparison.OrdinalIgnoreCase) ? "group"
         : string.Equals(by, "tool", StringComparison.OrdinalIgnoreCase) ? "tool"
         : "day";

    /// <summary>按维度算看板桶（1.0.323）：日维度补零，员工 / 知聚 / 工具维度按总量排序。</summary>
    private static IReadOnlyList<WorkAuditReport.TimelineBucket> TimelineBuckets(
        IReadOnlyList<WorkAuditReport.Entry> items, int days, string dim)
        => dim == "day"
            ? WorkAuditReport.Timeline(items, days)
                .Select((d) => new WorkAuditReport.TimelineBucket(d.Date, d.Date.Length > 5 ? d.Date[5..] : d.Date,
                    d.Commands, d.Writes, d.Reads, d.Listings, d.Other, d.Failed))
                .ToList()
            : dim == "tool"
                ? WorkAuditReport.ToolTimeline(items)
                : WorkAuditReport.GroupTimeline(items, dim);

    /// <summary>上传总大小上限（单文件 / 分片累计，1.0.327）。</summary>
    private const long MaxUploadBytes = 200L * 1024 * 1024;

    /// <summary>分片临时文件路径（1.0.327）：按 agentId + 相对路径哈希存放，避免工作区里出现 .part。</summary>
    private static string UploadPartPath(string contentRoot, string agentId, string rel)
    {
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(rel))).ToLowerInvariant()[..32];
        return Path.Combine(contentRoot, "data", "upload-parts", agentId, hash + ".part");
    }

    /// <summary>清理过期的分片残留（1.0.327）：只在用到分片时顺手扫一遍该员工的目录。</summary>
    private static void CleanupUploadParts(string? partDir, TimeSpan maxAge)
    {
        if (string.IsNullOrWhiteSpace(partDir)) return;
        try
        {
            if (!Directory.Exists(partDir)) return;
            var cutoff = DateTime.UtcNow - maxAge;
            foreach (var f in Directory.EnumerateFiles(partDir, "*.part"))
            {
                try { if (File.GetLastWriteTimeUtc(f) < cutoff) File.Delete(f); } catch { /* 忽略单个 */ }
            }
        }
        catch { /* 目录不可读：忽略 */ }
    }

    /// <summary>往批量下载 ZIP 里写一个文件（1.0.322）：超出总上限时返回错误码，否则返回 null。</summary>
    private static string? TryAddZipEntry(System.IO.Compression.ZipArchive zip, string full, string rel,
        ref long total, ref int added)
    {
        const long maxTotalBytes = 200L * 1024 * 1024;
        var size = new FileInfo(full).Length;
        if (total + size > maxTotalBytes) return "too_large";
        total += size;
        var entry = zip.CreateEntry(rel, System.IO.Compression.CompressionLevel.Fastest);
        using var target = entry.Open();
        using var source = File.OpenRead(full);
        source.CopyTo(target);
        added++;
        return null;
    }
}

/// <summary>临时放额提醒行（站内通道，1.0.276）：<c>State</c> = expiring / expired。</summary>
public sealed record QuotaReminderRow(string GroupId, string Name, long Quota, long Until, string State, long Seconds);

/// <summary>管理员禁用 / 启用账号请求体。</summary>
public sealed record AdminDisabledHttpRequest(bool Disabled);

/// <summary>管理员重置密码请求体。</summary>
public sealed record AdminPasswordHttpRequest(string NewPassword);

/// <summary>平台角色设置请求体（RBAC 分层，仅超级管理员）：role 取 user / operator / admin / superadmin。</summary>
public sealed record AdminRoleHttpRequest(string Role);

/// <summary>工作区批量下载请求体（1.0.321；1.0.322 增加 dir）：paths = 相对路径列表，dir = 按目录整包。</summary>
public sealed record WorkspaceDownloadRequest(IReadOnlyList<string>? Paths, string? Dir = null);
