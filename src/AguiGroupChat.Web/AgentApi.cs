using AguiGroupChat.Agents;
using AguiGroupChat.Hub.Agents;
using AguiGroupChat.Hub.Infra;
using AguiGroupChat.Hub.Messaging;
using AguiGroupChat.Hub.Models;
using AguiGroupChat.Hub.Options;
using AguiGroupChat.Hub.Users;
using Microsoft.Extensions.Logging;

namespace AguiGroupChat.Web;

/// <summary>
/// 智能体管理 HTTP API（Web 组合根扩展）：目录查询（公开）+ 新增 / 更新 / 删除（需登录）。
/// 运行时可动态创建 AI 角色并配置人设 / 触发规则 / 模型，替代仅靠 appsettings 静态声明。
/// </summary>
public static class AgentApi
{
    public static void MapAgentApi(this WebApplication app)
    {
        var root = app.MapGroup("/ag-ui/agents");

        // ---- 目录（登录可见自己创建的私密智能体；匿名 / 他人不可见私密智能体；技能目标与 AI 分身不暴露）----
        root.MapGet("/", (HttpContext ctx, AuthService auth, AgentCatalog catalog) =>
        {
            var user = RequireUser(ctx, auth);
            var defs = catalog.ListDefinitions()
                .Where(d => !d.IsSkillTarget)
                // AI 分身（twin_*）由用户经「修改资料 → AI 分身」自我管理，不出现在智能体管理 / 成员勾选目录
                .Where(d => !d.AgentId.StartsWith(TwinService.AgentIdPrefix, StringComparison.Ordinal))
                .Where(d => !d.IsPrivate || (user is not null && d.OwnerId == user.UserId))
                .ToList();
            return Results.Ok(ToDtos(defs));
        });

        // ---- 新增智能体（需登录）----
        root.MapPost("/", (AgentUpsertHttpRequest req, HttpContext ctx, AuthService auth, AgentCatalog catalog, KnowledgeBaseCatalog kbs, GroupHub hub) =>
        {
            var user = RequireUser(ctx, auth);
            if (user is null) return Unauthorized();
            var skillError = ValidateSkills(req.Skills);
            if (skillError is not null) return skillError;
            var scheduleError = ValidateSchedule(req.Schedule);
            if (scheduleError is not null) return scheduleError;
            var pipelineError = ValidatePipeline(req.Pipeline, catalog);
            if (pipelineError is not null) return pipelineError;
            // 知识库归属校验：只能绑定系统级/自己/所属共享群/管理员可读的知识库（防跨用户检索他人私密知识库）
            var kbError = ValidateKbAccess(req, kbs, user.UserId, MemberGroupIds(hub, user.UserId), auth.IsAdmin(user.UserId));
            if (kbError is not null) return kbError;
            // 桥接端点 SSRF 防护：创建时即校验 scheme / 内网地址（空 = 不用桥接；网关调用时还会二次校验）；
            // 桥接端点会把服务端作为内网代理并携带令牌，仅系统管理员可配置
            if (!string.IsNullOrWhiteSpace(req.BridgeEndpoint))
            {
                if (!auth.IsAdmin(user.UserId))
                    return Results.Json(new AguiError(ErrorCodes.AgentPermissionDenied, "外部 AG-UI 桥接端点仅系统管理员可配置"),
                        statusCode: StatusCodes.Status403Forbidden);
                if (AguiGroupChat.Agents.BridgeEndpointValidator.GetError(req.BridgeEndpoint) is { } bridgeErr)
                    return Results.BadRequest(new AguiError(ErrorCodes.BadRequest, $"桥接端点不合法：{bridgeErr}"));
            }

            var agentId = string.IsNullOrWhiteSpace(req.AgentId) ? "agent_" + IdGenerator.NewId() : req.AgentId.Trim();
            if (agentId.StartsWith(TwinService.AgentIdPrefix, StringComparison.Ordinal))
                return Results.BadRequest(new AguiError(ErrorCodes.BadRequest,
                    "twin_ 前缀为系统保留（AI 分身），请更换 Agent ID"));
            if (string.IsNullOrWhiteSpace(req.Nickname))
                return Results.BadRequest(new AguiError(ErrorCodes.BadRequest, "nickname 不能为空"));
            if (catalog.GetDefinition(agentId) is not null)
                return Results.Json(new AguiError(ErrorCodes.AgentExists, $"智能体 {agentId} 已存在"), statusCode: StatusCodes.Status409Conflict);

            var def = BuildDefinition(agentId, req, ownerId: user.UserId);
            catalog.Upsert(def);
            return Results.Ok(new { created = true, agentId, nickname = def.Nickname });
        });

        // ---- 更新智能体（需登录）：同步已注册群内的触发规则（保留群内显式覆盖）----
        root.MapPut("/{agentId}", async (string agentId, AgentUpsertHttpRequest req, HttpContext ctx, AuthService auth, AgentCatalog catalog, KnowledgeBaseCatalog kbs, GroupHub hub, AgentRegistry registry, CancellationToken ct) =>
        {
            var user = RequireUser(ctx, auth);
            if (user is null) return Unauthorized();
            if (agentId.StartsWith(TwinService.AgentIdPrefix, StringComparison.Ordinal))
                return Results.Json(new AguiError(ErrorCodes.AgentPermissionDenied,
                    "AI 分身请通过「修改资料 → AI 分身」管理，不支持在此编辑"), statusCode: StatusCodes.Status403Forbidden);
            var skillError = ValidateSkills(req.Skills);
            if (skillError is not null) return skillError;
            var scheduleError = ValidateSchedule(req.Schedule);
            if (scheduleError is not null) return scheduleError;
            var pipelineError = ValidatePipeline(req.Pipeline, catalog);
            if (pipelineError is not null) return pipelineError;
            // 知识库归属校验：只能绑定系统级/自己/所属共享群/管理员可读的知识库
            var kbError = ValidateKbAccess(req, kbs, user.UserId, MemberGroupIds(hub, user.UserId), auth.IsAdmin(user.UserId));
            if (kbError is not null) return kbError;
            // 桥接端点 SSRF 防护：编辑时同样校验（空 = 沿用 / 清除；网关调用时还会二次校验）；
            // 桥接端点仅系统管理员可配置（新增或变更端点时校验，纯清除不限制）
            if (!string.IsNullOrWhiteSpace(req.BridgeEndpoint))
            {
                if (!auth.IsAdmin(user.UserId))
                    return Results.Json(new AguiError(ErrorCodes.AgentPermissionDenied, "外部 AG-UI 桥接端点仅系统管理员可配置"),
                        statusCode: StatusCodes.Status403Forbidden);
                if (AguiGroupChat.Agents.BridgeEndpointValidator.GetError(req.BridgeEndpoint) is { } bridgeErr)
                    return Results.BadRequest(new AguiError(ErrorCodes.BadRequest, $"桥接端点不合法：{bridgeErr}"));
            }
            if (catalog.GetDefinition(agentId) is null)
                return Results.NotFound(new AguiError(ErrorCodes.AgentNotFound, "智能体不存在"));
            if (catalog.GetDefinition(agentId)!.IsSkillTarget)
                return Results.Json(new AguiError(ErrorCodes.AgentPermissionDenied, "技能目标智能体不可编辑"), statusCode: StatusCodes.Status403Forbidden);
            if (string.IsNullOrWhiteSpace(req.Nickname))
                return Results.BadRequest(new AguiError(ErrorCodes.BadRequest, "nickname 不能为空"));
            var existing = catalog.GetDefinition(agentId)!;
            if (existing.OwnerId is null)
                return Results.Json(new AguiError(ErrorCodes.AgentPermissionDenied, "系统内置智能体只读，请导出后另建"),
                    statusCode: StatusCodes.Status403Forbidden);
            if (existing.OwnerId != user.UserId)
                return Results.Json(new AguiError(ErrorCodes.AgentPermissionDenied, "仅创建者可编辑该智能体"),
                    statusCode: StatusCodes.Status403Forbidden);

            // 更新时令牌留空表示沿用原值（令牌不回显给前端，避免公开目录泄露）
            var def = BuildDefinition(agentId, req, existingToken: existing.BridgeToken, ownerId: existing.OwnerId);
            catalog.Upsert(def);

            // 同步已加入群内的成员资料（昵称 / 头像），并广播 GROUP_MEMBER_UPDATED
            await hub.SyncAgentProfileAsync(agentId, def.Nickname, def.Avatar, ct);
            registry.UpdateNickname(agentId, def.Nickname); // 触发规则注册（含已覆盖的群）同步昵称

            // 把新的触发模式 / 关键词同步到该智能体已加入的群；
            // 群内已显式覆盖触发方式的注册保持不变（IsOverridden=true），未覆盖的跟随新角色默认。
            var groupIds = hub.Store.AllGroups()
                .Where(g => hub.Store.IsMember(g.GroupId, agentId))
                .Select(g => g.GroupId)
                .ToList();
            foreach (var groupId in groupIds)
            {
                if (registry.ForGroupAgent(groupId, agentId)?.IsOverridden == true) continue;
                hub.RegisterAgent(new AgentRegisterRequest
                {
                    AgentId = agentId,
                    Nickname = def.Nickname,
                    GroupIds = [groupId],
                    TriggerMode = def.TriggerMode,
                    Keywords = def.Keywords,
                });
            }
            return Results.Ok(new { updated = true, agentId, nickname = def.Nickname });
        });

        // ---- 删除智能体（需登录）：移除目录、触发规则，并从所有群退出；系统内置只读，仅创建者可删 ----
        root.MapDelete("/{agentId}", async (string agentId, HttpContext ctx, AuthService auth, AgentCatalog catalog, AgentRegistry registry, GroupHub hub, CancellationToken ct) =>
        {
            var user = RequireUser(ctx, auth);
            if (user is null) return Unauthorized();
            if (agentId.StartsWith(TwinService.AgentIdPrefix, StringComparison.Ordinal))
                return Results.Json(new AguiError(ErrorCodes.AgentPermissionDenied,
                    "AI 分身请通过「修改资料 → AI 分身」管理，不支持在此删除"), statusCode: StatusCodes.Status403Forbidden);
            var def = catalog.GetDefinition(agentId);
            if (def is null)
                return Results.NotFound(new AguiError(ErrorCodes.AgentNotFound, "智能体不存在"));
            if (def.IsSkillTarget)
                return Results.Json(new AguiError(ErrorCodes.AgentPermissionDenied, "技能目标智能体不可删除"), statusCode: StatusCodes.Status403Forbidden);
            if (def.OwnerId is null)
                return Results.Json(new AguiError(ErrorCodes.AgentPermissionDenied, "系统内置智能体只读，请导出后另建"),
                    statusCode: StatusCodes.Status403Forbidden);
            if (def.OwnerId != user.UserId)
                return Results.Json(new AguiError(ErrorCodes.AgentPermissionDenied, "仅创建者可删除该智能体"),
                    statusCode: StatusCodes.Status403Forbidden);

            catalog.Remove(agentId);
            registry.Unregister(agentId, null);

            foreach (var g in hub.Store.AllGroups().Where(g => hub.Store.IsMember(g.GroupId, agentId)).ToList())
            {
                hub.Store.RemoveMember(g.GroupId, agentId);
                g.MemberCount = hub.Store.MemberCount(g.GroupId);
                await hub.BroadcastAsync(g.GroupId, new GroupMemberLeftEvent
                {
                    GroupId = g.GroupId,
                    MemberIds = [agentId],
                    LeaveType = LeaveType.Kick,
                    OperatorId = user.UserId,
                    Timestamp = hub.NowMs,
                }, ct: ct);
            }
            return Results.Ok(new { deleted = true, agentId });
        });

        // ---- 根据一句话简介生成角色设定（需登录）：身份定位 / 职责范围 / 回复风格，填充 Instructions ----
        root.MapPost("/generate-instructions", async (GenerateInstructionsHttpRequest req, HttpContext ctx, AuthService auth, AgentOptions agentOptions, ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            var user = RequireUser(ctx, auth);
            if (user is null) return Unauthorized();
            var description = (req.Description ?? "").Trim();
            if (description.Length < 2)
                return Results.BadRequest(new AguiError(ErrorCodes.BadRequest, "请先填写一句话简介（至少 2 个字符）"));
            if (description.Length > 200)
                return Results.BadRequest(new AguiError(ErrorCodes.BadRequest, "一句话简介最长 200 字符"));

            try
            {
                var instructions = await AgentInstructionsGenerator.GenerateAsync(
                    agentOptions, description, loggerFactory.CreateLogger("AgentInstructions"), ct);
                return Results.Ok(new { instructions });
            }
            catch (Exception ex)
            {
                return Results.Json(new AguiError(ErrorCodes.BadRequest, "角色设定生成失败：" + ex.Message),
                    statusCode: StatusCodes.Status502BadGateway);
            }
        });

        // ---- 为群注册触发规则（协议 §6，前端建群 / 加成员 / 群内覆盖触发方式时调用）----
        root.MapPost("/register", (AgentRegisterHttpRequest req, HttpContext ctx, AuthService auth, AuthOptions authOptions, GroupHub hub) =>
        {
            // 身份校验（与群接口一致）：有 token 校验令牌；无 token 回退 ?memberId=（演示模式）；
            // Auth:RequireTokenOnRealTime=true 时一律 401。注册的是 agentId → 群的触发关系，无操作者字段可覆盖。
            var (identity, error) = RequireIdentity(ctx, auth, authOptions);
            if (identity is null) return error!;
            // 群存在校验（先 404 后 403）
            if (hub.Store.GetGroup(req.GroupId) is null)
                return Results.NotFound(new AguiError(ErrorCodes.GroupNotFound, "群组不存在"));
            // 调用者必须是该群成员（防任意用户向他人群注册触发规则）
            if (!hub.Store.IsMember(req.GroupId, identity))
                return Results.Json(new AguiError(ErrorCodes.GroupPermissionDenied, "仅群成员可注册触发规则"),
                    statusCode: StatusCodes.Status403Forbidden);
            // 智能体必须是该群成员（前端流程为先加成员再 register，见 createGroup / addMembers）
            if (!hub.Store.IsMember(req.GroupId, req.AgentId))
                return Results.Json(new AguiError(ErrorCodes.AgentPermissionDenied, "智能体不在该群，无法注册触发规则"),
                    statusCode: StatusCodes.Status403Forbidden);
            var mode = Enum.TryParse<AgentTriggerMode>(req.TriggerMode, true, out var m)
                ? m
                : AgentTriggerMode.Mentioned;
            hub.RegisterAgent(new AgentRegisterRequest
            {
                AgentId = req.AgentId,
                Nickname = req.Nickname ?? "",
                GroupIds = [req.GroupId],
                TriggerMode = mode,
                Keywords = req.Keywords,
                Override = req.Override,
            });
            return Results.Ok(new { registered = true, agentId = req.AgentId, groupId = req.GroupId });
        });
    }

    private static object ToDtos(IEnumerable<AgentDefinition> defs) => defs.Select(d => new
    {
        d.AgentId,
        d.Nickname,
        d.Description,
        d.Instructions,
        d.Avatar,
        TriggerMode = d.TriggerMode.ToString().ToLowerInvariant(),
        d.Keywords,
        d.Schedule,
        d.Model,
        d.BridgeEndpoint,
        d.PersonalMemoryEnabled,
        d.EnableWorkTools,
        d.IsPrivate,
        d.OwnerId,
        d.Skills,
        d.KnowledgeBaseIds,
        d.RequireApprovalToolNames,
        d.Pipeline,
    });

    /// <summary>定时任务 cron 表达式校验：非法返回 400 错误（调度器每分钟空转会刷警告日志）。</summary>
    private static IResult? ValidateSchedule(string? schedule)
    {
        if (string.IsNullOrWhiteSpace(schedule)) return null;
        if (!CronSchedule.TryParse(schedule, out _, out var scheduleError))
            return Results.BadRequest(new AguiError(ErrorCodes.BadRequest, $"定时表达式不合法：{scheduleError}"));
        return null;
    }

    /// <summary>某用户所属的群 ID 集合（用于知识库群级共享可见性判定）。</summary>
    private static IReadOnlySet<string> MemberGroupIds(GroupHub hub, string userId)
        => hub.Store.GroupsOf(userId).Select(g => g.GroupId).ToHashSet(StringComparer.Ordinal);

    /// <summary>知识库绑定归属校验：每个 KnowledgeBaseId 必须存在，且调用者被允许读取（系统级 / 自己 / 管理员 / 所属共享群成员）；
    /// 否则返回错误结果（防把他人私密知识库绑到自己的智能体上检索注入）。</summary>
    private static IResult? ValidateKbAccess(AgentUpsertHttpRequest req, KnowledgeBaseCatalog kbs, string userId, IReadOnlySet<string>? memberGroupIds, bool isAdmin)
    {
        var ids = req.KnowledgeBaseIds
            ?.Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id.Trim()).Distinct().ToList();
        if (ids is null || ids.Count == 0) return null;
        foreach (var id in ids)
        {
            var kb = kbs.GetKb(id);
            if (kb is null)
                return Results.BadRequest(new AguiError(ErrorCodes.BadRequest, $"知识库不存在：{id}"));
            if (!kbs.CanRead(kb, userId, memberGroupIds, isAdmin))
                return Results.Json(new AguiError(ErrorCodes.AgentPermissionDenied, "不能绑定未共享给你的知识库（仅系统级 / 自己 / 所属共享群 / 管理员可绑定）"),
                    statusCode: StatusCodes.Status403Forbidden);
        }
        return null;
    }

    private static AgentDefinition BuildDefinition(string agentId, AgentUpsertHttpRequest req, string? existingToken = null, string? ownerId = null)
    {
        var mode = Enum.TryParse<AgentTriggerMode>(req.TriggerMode, true, out var m)
            ? m
            : AgentTriggerMode.Mentioned;
        return new AgentDefinition
        {
            AgentId = agentId,
            Nickname = req.Nickname.Trim(),
            Description = req.Description?.Trim() ?? "",
            Instructions = req.Instructions?.Trim() ?? "",
            Avatar = string.IsNullOrWhiteSpace(req.Avatar) ? null : req.Avatar.Trim(),
            TriggerMode = mode,
            Keywords = req.Keywords?.Where(k => !string.IsNullOrWhiteSpace(k)).Select(k => k.Trim()).ToList() ?? [],
            Schedule = string.IsNullOrWhiteSpace(req.Schedule) ? null : req.Schedule.Trim(),
            Model = string.IsNullOrWhiteSpace(req.Model) ? null : req.Model.Trim(),
            // AG-UI 桥接：端点非空即外部专家；令牌留空时沿用原值（编辑防覆盖）
            BridgeEndpoint = string.IsNullOrWhiteSpace(req.BridgeEndpoint) ? null : req.BridgeEndpoint.Trim(),
            BridgeMode = string.IsNullOrWhiteSpace(req.BridgeMode) ? null : req.BridgeMode.Trim().ToLowerInvariant(),
            BridgeToken = string.IsNullOrWhiteSpace(req.BridgeToken) ? existingToken : req.BridgeToken.Trim(),
            PersonalMemoryEnabled = req.PersonalMemoryEnabled ?? false,
            EnableWorkTools = req.EnableWorkTools ?? false,
            IsPrivate = req.IsPrivate ?? false,
            OwnerId = ownerId,
            Skills = BuildSkills(req.Skills),
            KnowledgeBaseIds = req.KnowledgeBaseIds?.Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id.Trim()).Distinct().ToList() ?? [],
            RequireApprovalToolNames = req.RequireApprovalToolNames
                ?.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).Distinct().ToList() ?? [],
            Pipeline = BuildPipeline(req.Pipeline),
        };
    }

    /// <summary>构建编排流水线步骤列表（去空步骤，去重步骤智能体）。</summary>
    private static List<AgentPipelineStep>? BuildPipeline(IReadOnlyList<AgentPipelineStepHttpRequest>? pipeline)
    {
        if (pipeline is null or { Count: 0 }) return null;
        var steps = new List<AgentPipelineStep>();
        foreach (var s in pipeline)
        {
            var stepAgentId = s.StepAgentId?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(stepAgentId)) continue;
            if (steps.Any(st => st.StepAgentId == stepAgentId)) continue; // 同一步骤智能体去重
            steps.Add(new AgentPipelineStep { StepAgentId = stepAgentId, Prompt = string.IsNullOrWhiteSpace(s.Prompt) ? null : s.Prompt.Trim() });
        }
        return steps.Count == 0 ? null : steps;
    }

    /// <summary>校验编排流水线：每个步骤智能体非空、且必须是已注册智能体。</summary>
    private static IResult? ValidatePipeline(IReadOnlyList<AgentPipelineStepHttpRequest>? pipeline, AgentCatalog catalog)
    {
        if (pipeline is null or { Count: 0 }) return null;
        foreach (var s in pipeline)
        {
            if (string.IsNullOrWhiteSpace(s.StepAgentId))
                return Results.BadRequest(new AguiError(ErrorCodes.BadRequest, "流水线步骤的子智能体不能为空"));
            if (catalog.GetDefinition(s.StepAgentId.Trim()) is null)
                return Results.BadRequest(new AguiError(ErrorCodes.BadRequest, $"流水线步骤智能体未注册：{s.StepAgentId.Trim()}"));
        }
        return null;
    }

    /// <summary>构建技能列表：SkillId 留空时按目标智能体自动生成（skill_&lt;agentId&gt;，同名冲突追加 _2/_3），并去重。</summary>
    private static List<AgentSkillConfig> BuildSkills(IReadOnlyList<AgentSkillHttpRequest>? skills)
    {
        if (skills is null) return [];
        var occupied = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<AgentSkillConfig>();
        foreach (var s in skills)
        {
            if (string.IsNullOrWhiteSpace(s.TargetAgentId)) continue;
            var skillId = s.SkillId?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(skillId))
                skillId = AgentSkillConfig.GenerateSkillId(s.TargetAgentId, occupied);
            else if (!occupied.Add(skillId))
                continue; // 显式重名（前端已拦，此处兜底跳过）
            result.Add(new AgentSkillConfig
            {
                SkillId = skillId,
                Description = s.Description?.Trim() ?? "",
                TargetAgentId = s.TargetAgentId.Trim(),
            });
        }
        return result;
    }

    internal static UserAccount? RequireUser(HttpContext ctx, AuthService auth)
        => auth.ValidateToken(ResolveToken(ctx));

    /// <summary>校验技能配置：非空 SkillId 须合法（OpenAI 工具名规范，否则模型调用直接 400），目标智能体非空。</summary>

    private static IResult? ValidateSkills(IReadOnlyList<AgentSkillHttpRequest>? skills)
    {
        if (skills is null) return null;
        foreach (var s in skills)
        {
            // 非空 SkillId 须合法（OpenAI 工具名规范，否则模型调用直接 400）；留空 = 后端自动生成
            if (!string.IsNullOrWhiteSpace(s.SkillId) && !AgentSkillConfig.IsValidSkillId(s.SkillId))
                return Results.BadRequest(new AguiError(ErrorCodes.BadRequest,
                    $"技能标识「{s.SkillId}」不合法：仅允许字母、数字、下划线、连字符（如 skill_docs，不能含中文/空格/点号）；留空则自动生成"));
            if (string.IsNullOrWhiteSpace(s.TargetAgentId))
                return Results.BadRequest(new AguiError(ErrorCodes.BadRequest, "技能的目标智能体不能为空"));
        }
        return null;
    }

    /// <summary>解析身份（同群接口 / WS / SSE）：有效令牌 → 令牌身份；无令牌 → `?memberId=` 回退
    /// （兼容旧客户端 / 演示模式）；<see cref="AuthOptions.RequireTokenOnRealTime"/> = true 时一律 401。</summary>
    private static (string? Identity, IResult? Error) RequireIdentity(HttpContext ctx, AuthService auth, AuthOptions authOptions)
    {
        var token = ResolveToken(ctx);
        if (!string.IsNullOrEmpty(token))
        {
            var user = auth.ValidateToken(token);
            if (user is null)
                return (null, Results.Json(new AguiError(ErrorCodes.UserUnauthorized, "未登录或令牌无效"),
                    statusCode: StatusCodes.Status401Unauthorized));
            return (user.UserId, null);
        }
        if (authOptions.RequireTokenOnRealTime)
            return (null, Results.Json(new AguiError(ErrorCodes.UserUnauthorized, "缺少身份令牌（Auth:RequireTokenOnRealTime=true）"),
                statusCode: StatusCodes.Status401Unauthorized));
        var memberId = ctx.Request.Query["memberId"].ToString();
        return string.IsNullOrWhiteSpace(memberId)
            ? (null, Results.Json(new AguiError(ErrorCodes.UserUnauthorized, "缺少身份（登录 token 或 memberId）"),
                statusCode: StatusCodes.Status401Unauthorized))
            : (memberId.Trim(), null);
    }

    internal static string? ResolveToken(HttpContext ctx)
    {
        var header = ctx.Request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return header["Bearer ".Length..].Trim();
        var query = ctx.Request.Query["token"].ToString();
        return string.IsNullOrEmpty(query) ? null : query;
    }

    internal static IResult Unauthorized(string message = "未登录或令牌无效")
        => Results.Json(new AguiError(ErrorCodes.UserUnauthorized, message), statusCode: StatusCodes.Status401Unauthorized);
}

// ================= 请求体 =================

/// <summary>根据一句话简介生成角色设定的请求体。</summary>
public sealed record GenerateInstructionsHttpRequest(string? Description);

/// <summary>创建 / 更新智能体的请求体（agentId 仅创建时可选，留空自动生成 agent_xxx）。</summary>
public sealed record AgentUpsertHttpRequest(
    string? AgentId,
    string Nickname,
    string? Description,
    string? Instructions,
    string? Avatar,
    string TriggerMode,
    IReadOnlyList<string>? Keywords,
    string? Model,
    string? BridgeEndpoint,
    string? BridgeMode,
    string? BridgeToken,
    string? Schedule = null,
    bool? PersonalMemoryEnabled = null,
    bool? IsPrivate = null,
    bool? EnableWorkTools = null,
    IReadOnlyList<AgentSkillHttpRequest>? Skills = null,
    IReadOnlyList<string>? KnowledgeBaseIds = null,
    IReadOnlyList<string>? RequireApprovalToolNames = null,
    IReadOnlyList<AgentPipelineStepHttpRequest>? Pipeline = null);

/// <summary>技能配置（把其他已注册智能体作为可调用子代理）。</summary>
/// <param name="SkillId">技能标识（给模型的工具名，同一智能体内唯一）。</param>
/// <param name="Description">技能描述（告诉模型何时调用）。</param>
/// <param name="TargetAgentId">被调用的智能体 ID。</param>
public sealed record AgentSkillHttpRequest(
    string SkillId,
    string? Description,
    string TargetAgentId);

/// <summary>编排流水线（1.1）中的一步：调用一个已注册子智能体。</summary>
/// <param name="StepAgentId">被调用的子智能体 ID。</param>
/// <param name="Prompt">给子智能体的额外指令（本步要专攻什么），可空/留空。</param>
public sealed record AgentPipelineStepHttpRequest(string StepAgentId, string? Prompt);

/// <summary>为指定群注册智能体触发规则（triggerMode 为字符串，便于前端直接传递）。</summary>
/// <param name="Override">true 表示在群内显式覆盖角色默认触发方式（角色编辑不覆写本群）；
/// false 表示跟随角色默认。</param>
public sealed record AgentRegisterHttpRequest(
    string AgentId,
    string? Nickname,
    string GroupId,
    string TriggerMode,
    IReadOnlyList<string>? Keywords,
    bool Override = false);
