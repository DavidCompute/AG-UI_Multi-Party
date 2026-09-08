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
///   DELETE /ag-ui/admin/users/{userId} —— 彻底删除账号（数据擦除：群转让/解散 + 记忆 / 知识库清除）
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

        // 操作审计日志（4.3 / 企业合规）：关键 / 敏感操作留痕，仅管理员及以上（含运维）。
        // 支持过滤：actor（操作者用户名 / ID 子串）、action（操作名子串）、targetId、fromMs / toMs（UTC 毫秒）。limit 最多 200。
        root.MapGet("/audit", (int? limit, string? actor, string? action, string? targetId, long? fromMs, long? toMs,
            HttpContext ctx, AguiGroupChat.Hub.Infra.AuditLogService audit) =>
        {
            return Results.Ok(new
            {
                total = audit.Count,
                entries = audit.Query(limit ?? 100, actor, action, targetId, fromMs, toMs),
            });
        }).AddEndpointFilter(new WebIdentity.RequireRoleFilter(PlatformRole.Operator));

        // 审计日志导出 CSV（RFC 4180 转义 + UTF-8 BOM，Excel 可直接打开）：同 /audit 过滤条件，按时间正序，无 200 条上限。
        root.MapGet("/audit.csv", (string? actor, string? action, string? targetId, long? fromMs, long? toMs,
            HttpContext ctx, AguiGroupChat.Hub.Infra.AuditLogService audit) =>
        {
            var rows = audit.QueryAll(actor, action, targetId, fromMs, toMs);
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
}

/// <summary>管理员禁用 / 启用账号请求体。</summary>
public sealed record AdminDisabledHttpRequest(bool Disabled);

/// <summary>管理员重置密码请求体。</summary>
public sealed record AdminPasswordHttpRequest(string NewPassword);

/// <summary>平台角色设置请求体（RBAC 分层，仅超级管理员）：role 取 user / operator / admin / superadmin。</summary>
public sealed record AdminRoleHttpRequest(string Role);
