using System.Diagnostics;
using AguiGroupChat.Agents;
using AguiGroupChat.Hub;
using AguiGroupChat.Hub.Agents;
using AguiGroupChat.Hub.Infra;
using AguiGroupChat.Hub.Messaging;
using AguiGroupChat.Hub.Models;
using AguiGroupChat.Hub.Options;
using AguiGroupChat.Hub.Users;

namespace AguiGroupChat.Web;

/// <summary>
/// 管理员控制台 API（仅系统管理员）：
///   GET  /ag-ui/admin/users —— 用户列表（禁用状态 / 管理员标记 / 注册时间）
///   POST /ag-ui/admin/users/{userId}/disabled —— 禁用 / 启用账号（禁用即吊销全部会话）
///   POST /ag-ui/admin/users/{userId}/password —— 重置密码（吊销全部会话）
///   GET  /ag-ui/admin/status —— 系统状态（连接数 / 群数 / 用户数 / 消息数 / 智能体数 / 进程信息）
/// </summary>
public static class AdminApi
{
    public static void MapAdminApi(this WebApplication app)
    {
        var root = app.MapGroup("/ag-ui/admin");
        root.MapGet("/users", (HttpContext ctx, AuthService auth, GroupHub hub) =>
        {
            var users = auth.ListUsers().Select(u => new
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

        root.MapGet("/status", (HttpContext ctx, AuthService auth, GroupHub hub, AgentCatalog catalog, ConnectionManager connections, IServiceProvider sp) =>
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
            });
        }).AddEndpointFilter(new WebIdentity.RequireRoleFilter(PlatformRole.Operator));

        // 模型用量统计（最近 N 天按日汇总 + 配额配置）：仅管理员及以上（含运维）
        root.MapGet("/usage", (int? days, HttpContext ctx, AguiGroupChat.Hub.Agents.AgentUsageService usage) =>
        {
            return Results.Ok(new
            {
                dailyQuotaPerUser = usage.DailyQuotaPerUser,
                days = usage.GetDailySummary(days ?? 7),
            });
        }).AddEndpointFilter(new WebIdentity.RequireRoleFilter(PlatformRole.Operator));

        // 操作审计日志（4.3）：关键 / 敏感操作留痕，仅管理员及以上（含运维）。limit 最多 200。
        root.MapGet("/audit", (int? limit, HttpContext ctx, AguiGroupChat.Hub.Infra.AuditLogService audit) =>
        {
            return Results.Ok(new
            {
                total = audit.Count,
                entries = audit.Query(limit ?? 100),
            });
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

        // 轻量运行指标（6.1）：智能体调用 / 桥接 / 记忆命中 / 输出长度 的进程内计数，仅管理员及以上（含运维）。
        root.MapGet("/metrics", (HttpContext ctx, AguiGroupChat.Agents.MetricsService metrics) =>
        {
            return Results.Ok(metrics.Snapshot());
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
                    thinkingMode = agents.ThinkingMode,
                    dailyTokenQuotaPerUser = agents.DailyTokenQuotaPerUser,
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
    }

    private static IResult Run(Func<IResult> action)
    {
        try { return action(); }
        catch (AguiProtocolException ex) { return Results.BadRequest(new AguiError(ex.ErrorCode, ex.Message)); }
    }
}

/// <summary>管理员禁用 / 启用账号请求体。</summary>
public sealed record AdminDisabledHttpRequest(bool Disabled);

/// <summary>管理员重置密码请求体。</summary>
public sealed record AdminPasswordHttpRequest(string NewPassword);

/// <summary>平台角色设置请求体（RBAC 分层，仅超级管理员）：role 取 user / operator / admin / superadmin。</summary>
public sealed record AdminRoleHttpRequest(string Role);
