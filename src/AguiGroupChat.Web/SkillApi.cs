using AguiGroupChat.Agents;
using AguiGroupChat.Hub.Infra;
using AguiGroupChat.Hub.Models;
using AguiGroupChat.Hub.Options;
using AguiGroupChat.Hub.Storage;
using AguiGroupChat.Hub.Users;
using Microsoft.Extensions.Logging;

namespace AguiGroupChat.Web;

/// <summary>
/// 技能库管理 HTTP API（OpenClaw 风格可复用技能）：技能定义（shell / http / prompt）的
/// 新增 / 更新 / 删除 / 试运行，供前端「技能库」管理弹窗与「数字员工自建技能」共用。
/// 技能库是全局可复用的技能目录：任何数字员工经 <see cref="AgentUpsertHttpRequest.SkillDefIds"/> 挂载引用。
/// </summary>
public static class SkillApi
{
    public static void MapSkillApi(this WebApplication app)
    {
        var root = app.MapGroup("/ag-ui/skills");

        // ---- 技能库列表（需登录；技能正文 / 解释器 / HTTP 配置仅归属者或管理员可见，避免脚本/密钥泄露）----
        root.MapGet("/", (HttpContext ctx, AuthService auth, AgentSkillCatalog catalog, AguiGroupChat.Agents.UserGroups.UserGroupStore groups) =>
        {
            var user = WebIdentity.User(ctx, auth);
            if (user is null) return Unauthorized();
            var isAdmin = auth.IsAdmin(user.UserId);
            var visible = catalog.ListAll()
                // 细粒度授权：设了用户组白名单的技能，非归属者/管理员需命中任一组才可见
                .Where(s => s.AllowedUserGroupIds is not { Count: > 0 } allow
                    || s.OwnerId == user.UserId || isAdmin || groups.UserInAny(user.UserId, allow));
            return Results.Ok(visible.Select(s => ToDto(s, s.OwnerId == user.UserId || isAdmin)));
        }).AddEndpointFilter(new WebIdentity.RequireTokenFilter());

        // ---- 用自然语言生成技能配置（无需手填各字段）：输入需求，由大模型产出结构化技能定义，前端据此填入表单 ----
        root.MapPost("/generate", async (SkillGenerateRequest req, HttpContext ctx, AuthService auth, AgentOptions options, AgentCatalog catalog, ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            var user = WebIdentity.User(ctx, auth);
            if (user is null) return Unauthorized();
            if (string.IsNullOrWhiteSpace(req.Request)) return Results.BadRequest(new AguiError(ErrorCodes.BadRequest, "需求描述不能为空"));
            try
            {
                var isAdmin = auth.IsAdmin(user.UserId);
                var preferClient = req.PreferClient == true;
                var runEnv = preferClient
                    ? DescribeClientEnv(req.ClientOs)
                    : DescribeServerEnv();
                var gen = await SkillDefinitionGenerator.GenerateAsync(
                    options, req.Request, preferClient, isAdmin, loggerFactory.CreateLogger("SkillApi.Generate"), ct, runEnv);

                var name = gen.Name;
                var description = gen.Description;
                var body = gen.Body;
                object? selfTest = null;

                // dotnet（C#）技能生成后自测：服务端只编译不运行，编译不过（或缺少 Run 入口）时让模型自动修正并复测，
                // 前端拿到的正文已是可编译版本，保存后即可用（避免“生成完保存了却不能编译”）。
                if (string.Equals(gen.Kind, "dotnet", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(options.Provider, "mock", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var fixer = new SkillAutoFixer(options, catalog, loggerFactory);
                        var probeDef = new AgentSkillDefinition
                        {
                            SkillId = gen.SkillId ?? "generated",
                            Name = gen.Name,
                            Description = gen.Description,
                            Kind = AgentSkillKind.Dotnet,
                            Body = gen.Body,
                            ExecutionLocation = gen.ExecutionLocation == "client"
                                ? AgentSkillExecutionLocation.Client
                                : AgentSkillExecutionLocation.Server,
                            RequiresApproval = gen.RequiresApproval,
                        };
                        var smoke = await fixer.VerifyOrRepairAsync(probeDef, maxAttempts: 3, ct).ConfigureAwait(false);
                        selfTest = new { skipped = smoke.Skipped, ok = smoke.Ok, attempts = smoke.Attempts, repaired = smoke.CorrectedBody != null, lastError = smoke.LastError };
                        if (smoke.Ok && smoke.CorrectedBody is { Length: > 0 } fixedBody)
                        {
                            body = fixedBody;
                            if (!string.IsNullOrWhiteSpace(smoke.CorrectedDescription)) description = smoke.CorrectedDescription!;
                        }
                    }
                    catch (Exception ex)
                    {
                        // 自测/修复失败不阻断返回草稿（正文仍可手动编辑）
                        loggerFactory.CreateLogger("SkillApi.Generate").LogWarning(ex, "dotnet 技能生成后自测失败：{SkillId}", gen.SkillId);
                        selfTest = new { skipped = true, ok = false, attempts = 0, repaired = false, lastError = "自测异常：" + ex.Message };
                    }
                }

                return Results.Ok(new
                {
                    generated = true,
                    skillId = gen.SkillId,
                    name,
                    kind = gen.Kind,
                    description,
                    body,
                    executionLocation = gen.ExecutionLocation,
                    clientRunner = gen.ClientRunner,
                    requiresApproval = gen.RequiresApproval,
                    targetEnv = runEnv,
                    selfTest,
                });
            }
            catch (OperationCanceledException) { return Results.Json(new AguiError(ErrorCodes.BadRequest, "生成已取消或超时"), statusCode: StatusCodes.Status408RequestTimeout); }
            catch (Exception ex) { return Results.Json(new AguiError(ErrorCodes.BadRequest, "生成失败：" + ex.Message), statusCode: StatusCodes.Status400BadRequest); }
        }).AddEndpointFilter(new WebIdentity.RequireTokenFilter());

        // ---- 新增技能（需登录；Shell / HTTP 技能的创建仅限管理员——它们的执行可能触发任意命令 / 外部请求，
        //      普通用户只能创建纯提示词技能，避免「自建 shell 技能自 run」的任意命令执行面）----
        root.MapPost("/", (SkillDefHttpRequest req, HttpContext ctx, AuthService auth, AgentSkillCatalog catalog, AguiGroupChat.Agents.UserGroups.UserGroupStore ugStore) =>
        {
            var user = WebIdentity.User(ctx, auth);
            if (user is null) return Unauthorized();
            var isAdmin = auth.IsAdmin(user.UserId);
            if (RequiresPrivilegedKind(req.Kind) && !isAdmin)
                return Results.Json(new AguiError(ErrorCodes.SkillPermissionDenied, $"仅管理员可创建 {req.Kind} 技能"), statusCode: StatusCodes.Status403Forbidden);
            var (def, err) = BuildDef(req, ownerId: user.UserId, authorAdmin: isAdmin, groups: ugStore,
                libs: ctx.RequestServices.GetService<AguiGroupChat.Agents.ImageLibraryCatalog>());
            if (err is not null) return err;
            var skill = def!; // BuildDef 保证 err 非空时 def 为空、err 为空时 def 非空
            if (catalog.Contains(skill.SkillId))
                return Results.Json(new AguiError(ErrorCodes.SkillExists, $"技能「{skill.SkillId}」已存在"), statusCode: StatusCodes.Status409Conflict);
            catalog.Upsert(skill);
            return Results.Ok(new { created = true, skillId = skill.SkillId });
        }).AddEndpointFilter(new WebIdentity.RequireTokenFilter());

        // ---- 更新技能（技能归属者或系统管理员）；Shell / HTTP 技能非管理员不可改（含把 Prompt 改成 Shell 提权）----
        root.MapPut("/{skillId}", (string skillId, SkillDefHttpRequest req, HttpContext ctx, AuthService auth, AgentSkillCatalog catalog, AguiGroupChat.Agents.UserGroups.UserGroupStore ugStore) =>
        {
            var user = WebIdentity.User(ctx, auth);
            if (user is null) return Unauthorized();
            var isAdmin = auth.IsAdmin(user.UserId);
            var existing = catalog.Get(skillId);
            if (existing is null)
                return Results.NotFound(new AguiError(ErrorCodes.SkillNotFound, "技能不存在"));
            if (existing.OwnerId is null)
                return Results.Json(new AguiError(ErrorCodes.SkillPermissionDenied, "系统内置技能只读，请导出后另建"), statusCode: StatusCodes.Status403Forbidden);
            if (!isAdmin && existing.OwnerId != user.UserId)
                return Results.Json(new AguiError(ErrorCodes.SkillPermissionDenied, "仅创建者或系统管理员可编辑该技能"), statusCode: StatusCodes.Status403Forbidden);
            if (RequiresPrivilegedKind(req.Kind) && !isAdmin)
                return Results.Json(new AguiError(ErrorCodes.SkillPermissionDenied, $"仅管理员可把技能改为 {req.Kind} 类型"), statusCode: StatusCodes.Status403Forbidden);
            var (def, err) = BuildDef(req, ownerId: existing.OwnerId, authorAdmin: isAdmin, groups: ugStore,
                libs: ctx.RequestServices.GetService<AguiGroupChat.Agents.ImageLibraryCatalog>());
            if (err is not null) return err;
            var skill = def!; // BuildDef 保证 err 非空时 def 为空、err 为空时 def 非空
            skill.SkillId = skillId; // ID 用 URL 的，不允许改名
            // 图库写入是管理员授予的能力：非管理员编辑技能时**保留**原授权，不能因表单未提交而静默丢失
            if (!isAdmin)
            {
                skill.ImageWrite = existing.ImageWrite;
                skill.ImageWriteLibraryIds = existing.ImageWriteLibraryIds;
                skill.ImageWriteRequiresApproval = existing.ImageWriteRequiresApproval;
                skill.ImageWriteMaxLibraries = existing.ImageWriteMaxLibraries;
                skill.ImageWriteMaxImages = existing.ImageWriteMaxImages;
            }
            // 用户经界面改过 → 不再是“原样内置版”，清掉版本标记：升级时不再用新正文覆盖用户改动
            skill.BuiltinVersion = null;
            // 暂停状态由独立的 suspend / resume 端点维护：编辑技能（修正文）不等于恢复上线，
            // 若在这里丢掉标志，用户一保存就把管理员刚停掉的技能重新挂回岗位。
            skill.Suspended = existing.Suspended;
            skill.SuspendedReason = existing.SuspendedReason;
            skill.SuspendedAtMs = existing.SuspendedAtMs;
            catalog.Upsert(skill);
            return Results.Ok(new { updated = true, skillId = skill.SkillId });
        }).AddEndpointFilter(new WebIdentity.RequireTokenFilter());

        // ---- 技能经验（1.0.349）：查看该技能沉淀的岗位失败 / 修复经验（归属者或管理员）----
        root.MapGet("/{skillId}/lessons", (string skillId, HttpContext ctx, AuthService auth, AgentSkillCatalog catalog) =>
        {
            var user = WebIdentity.User(ctx, auth);
            if (user is null) return Unauthorized();
            var skill = catalog.Get(skillId);
            if (skill is null) return Results.NotFound(new AguiError(ErrorCodes.SkillNotFound, "技能不存在"));
            var isAdmin = auth.IsAdmin(user.UserId);
            if (skill.OwnerId is not null && !isAdmin && skill.OwnerId != user.UserId)
                return Results.Json(new AguiError(ErrorCodes.SkillPermissionDenied, "仅创建者或系统管理员可查看该技能经验"), statusCode: StatusCodes.Status403Forbidden);
            if (skill.OwnerId is null && !isAdmin)
                return Results.Json(new AguiError(ErrorCodes.SkillPermissionDenied, "系统内置技能仅管理员可查看经验"), statusCode: StatusCodes.Status403Forbidden);
            var lessons = ctx.RequestServices.GetService<SkillLessonStore>()?.ForSkill(skillId) ?? Array.Empty<SkillLesson>();
            // 有效性回看（1.0.352）：用该技能最近 60 天的产物质量事件，按每条经验的时间切前后两段。
            var events = ctx.RequestServices.GetService<ArtifactQualityStore>()
                ?.Query(DateTimeOffset.UtcNow.AddDays(-60).ToUnixTimeMilliseconds())
                .Where(e => string.Equals(e.SkillId, skillId, StringComparison.Ordinal))
                .ToList() ?? [];
            object? Effectiveness(SkillLesson l)
            {
                var e = LessonEffectiveness.Evaluate(events, l.TimestampMs);
                return e is null ? null : new
                {
                    beforeRuns = e.BeforeRuns,
                    beforeRate = e.BeforeRate,
                    beforeAvg = e.BeforeAvg,
                    afterRuns = e.AfterRuns,
                    afterRate = e.AfterRate,
                    afterAvg = e.AfterAvg,
                    e.Delta,
                    e.Status,
                };
            }
            return Results.Ok(new
            {
                skillId,
                lessons = lessons.Select(l => new
                {
                    l.Id, l.AgentId, l.Kind, l.Summary, l.Missing, l.Fix, l.GroupId,
                    atMs = l.TimestampMs, l.Resolved,
                    effectiveness = Effectiveness(l),
                }),
            });
        }).AddEndpointFilter(new WebIdentity.RequireTokenFilter());

        // ---- 手动补写经验（1.0.351）：归属者或管理员可为该技能添加一条经验笔记 ----
        root.MapPost("/{skillId}/lessons", (string skillId, SkillLessonCreateRequest req, HttpContext ctx, AuthService auth,
            AgentSkillCatalog catalog, AgentCatalog agentCatalog) =>
        {
            var user = WebIdentity.User(ctx, auth);
            if (user is null) return Unauthorized();
            var skill = catalog.Get(skillId);
            if (skill is null) return Results.NotFound(new AguiError(ErrorCodes.SkillNotFound, "技能不存在"));
            var isAdmin = auth.IsAdmin(user.UserId);
            if (skill.OwnerId is not null && !isAdmin && skill.OwnerId != user.UserId)
                return Results.Json(new AguiError(ErrorCodes.SkillPermissionDenied, "仅创建者或系统管理员可编辑该技能经验"), statusCode: StatusCodes.Status403Forbidden);
            if (skill.OwnerId is null && !isAdmin)
                return Results.Json(new AguiError(ErrorCodes.SkillPermissionDenied, "系统内置技能仅管理员可编辑经验"), statusCode: StatusCodes.Status403Forbidden);
            if (string.IsNullOrWhiteSpace(req.Summary))
                return Results.BadRequest(new AguiError(ErrorCodes.BadRequest, "经验内容不能为空"));
            var agentId = (req.AgentId ?? "").Trim();
            if (agentId.Length > 0 && agentCatalog.GetDefinition(agentId) is null)
                return Results.BadRequest(new AguiError(ErrorCodes.BadRequest, $"岗位不存在：{agentId}"));
            var lessons = ctx.RequestServices.GetService<SkillLessonStore>();
            if (lessons is null) return Results.Json(new AguiError(ErrorCodes.BadRequest, "经验库不可用"), statusCode: StatusCodes.Status503ServiceUnavailable);
            var lesson = lessons.AddManual(skillId, agentId.Length == 0 ? null : agentId, req.Summary, req.Missing, req.Fix);
            if (lesson is null) return Results.BadRequest(new AguiError(ErrorCodes.BadRequest, "经验内容不能为空"));
            return Results.Ok(new { created = true, lessonId = lesson.Id });
        }).AddEndpointFilter(new WebIdentity.RequireTokenFilter());

        // ---- 删除一条经验（1.0.351）：归属者或管理员；仅限属于该技能的经验 ----
        root.MapDelete("/{skillId}/lessons/{lessonId}", (string skillId, string lessonId, HttpContext ctx, AuthService auth,
            AgentSkillCatalog catalog) =>
        {
            var user = WebIdentity.User(ctx, auth);
            if (user is null) return Unauthorized();
            var skill = catalog.Get(skillId);
            if (skill is null) return Results.NotFound(new AguiError(ErrorCodes.SkillNotFound, "技能不存在"));
            var isAdmin = auth.IsAdmin(user.UserId);
            if (skill.OwnerId is not null && !isAdmin && skill.OwnerId != user.UserId)
                return Results.Json(new AguiError(ErrorCodes.SkillPermissionDenied, "仅创建者或系统管理员可编辑该技能经验"), statusCode: StatusCodes.Status403Forbidden);
            if (skill.OwnerId is null && !isAdmin)
                return Results.Json(new AguiError(ErrorCodes.SkillPermissionDenied, "系统内置技能仅管理员可编辑经验"), statusCode: StatusCodes.Status403Forbidden);
            var removed = ctx.RequestServices.GetService<SkillLessonStore>()?.Remove(skillId, lessonId) == true;
            if (!removed) return Results.NotFound(new AguiError(ErrorCodes.SkillNotFound, "经验不存在"));
            return Results.Ok(new { deleted = true, lessonId });
        }).AddEndpointFilter(new WebIdentity.RequireTokenFilter());

        // ---- 清理无效经验（1.0.354）：把回看为“反而下降”的经验批量停用（默认）或删除 ----
        root.MapPost("/{skillId}/lessons/cleanup", (string skillId, string? mode, HttpContext ctx, AuthService auth,
            AgentSkillCatalog catalog) =>
        {
            var user = WebIdentity.User(ctx, auth);
            if (user is null) return Unauthorized();
            var skill = catalog.Get(skillId);
            if (skill is null) return Results.NotFound(new AguiError(ErrorCodes.SkillNotFound, "技能不存在"));
            var isAdmin = auth.IsAdmin(user.UserId);
            if (skill.OwnerId is not null && !isAdmin && skill.OwnerId != user.UserId)
                return Results.Json(new AguiError(ErrorCodes.SkillPermissionDenied, "仅创建者或系统管理员可清理该技能经验"), statusCode: StatusCodes.Status403Forbidden);
            if (skill.OwnerId is null && !isAdmin)
                return Results.Json(new AguiError(ErrorCodes.SkillPermissionDenied, "系统内置技能仅管理员可清理经验"), statusCode: StatusCodes.Status403Forbidden);

            var store = ctx.RequestServices.GetService<SkillLessonStore>();
            if (store is null) return Results.Json(new AguiError(ErrorCodes.BadRequest, "经验库不可用"), statusCode: StatusCodes.Status503ServiceUnavailable);
            var events = ctx.RequestServices.GetService<ArtifactQualityStore>()
                ?.Query(DateTimeOffset.UtcNow.AddDays(-60).ToUnixTimeMilliseconds())
                .Where(e => string.Equals(e.SkillId, skillId, StringComparison.Ordinal))
                .ToList() ?? [];
            var ids = LessonCleanup.SelectDeclined(store.ForSkill(skillId), events);
            var delete = string.Equals(mode, "delete", StringComparison.OrdinalIgnoreCase);
            var cleaned = 0;
            if (delete)
            {
                foreach (var id in ids) if (store.Remove(skillId, id)) cleaned++;
            }
            else
            {
                cleaned = store.Resolve(skillId, ids);
            }
            return Results.Ok(new { cleaned, mode = delete ? "delete" : "resolve", ids });
        }).AddEndpointFilter(new WebIdentity.RequireTokenFilter());

        // ---- 技能回归用例（1.0.355）：真实失败触发，用标准样例重跑，防修复后退化 ----
        root.MapGet("/{skillId}/regression", (string skillId, HttpContext ctx, AuthService auth, AgentSkillCatalog catalog) =>
        {
            var user = WebIdentity.User(ctx, auth);
            if (user is null) return Unauthorized();
            var skill = catalog.Get(skillId);
            if (skill is null) return Results.NotFound(new AguiError(ErrorCodes.SkillNotFound, "技能不存在"));
            var isAdmin = auth.IsAdmin(user.UserId);
            if (skill.OwnerId is not null && !isAdmin && skill.OwnerId != user.UserId)
                return Results.Json(new AguiError(ErrorCodes.SkillPermissionDenied, "仅创建者或系统管理员可查看该技能回归用例"), statusCode: StatusCodes.Status403Forbidden);
            if (skill.OwnerId is null && !isAdmin)
                return Results.Json(new AguiError(ErrorCodes.SkillPermissionDenied, "系统内置技能仅管理员可查看回归用例"), statusCode: StatusCodes.Status403Forbidden);
            var cases = ctx.RequestServices.GetService<SkillRegressionCaseStore>()?.List(skillId) ?? Array.Empty<SkillRegressionCase>();
            return Results.Ok(new
            {
                skillId,
                cases = cases.Select(c => new
                {
                    c.Id, c.Kind, c.MinScore, c.Source,
                    createdAtMs = c.CreatedAtMs,
                    c.LastPassed, c.LastScore, c.LastRunAtMs,
                }),
            });
        }).AddEndpointFilter(new WebIdentity.RequireTokenFilter());

        // ---- 跑回归（1.0.355）：逐个用例用标准样例真实执行并校验收产物契约，最多 3 条 ----
        root.MapPost("/{skillId}/regression/run", async (string skillId, HttpContext ctx, AuthService auth,
            AgentSkillCatalog catalog, AgentCatalog agents, CancellationToken ct) =>
        {
            var user = WebIdentity.User(ctx, auth);
            if (user is null) return Unauthorized();
            var skill = catalog.Get(skillId);
            if (skill is null) return Results.NotFound(new AguiError(ErrorCodes.SkillNotFound, "技能不存在"));
            var isAdmin = auth.IsAdmin(user.UserId);
            if (skill.OwnerId is not null && !isAdmin && skill.OwnerId != user.UserId)
                return Results.Json(new AguiError(ErrorCodes.SkillPermissionDenied, "仅创建者或系统管理员可跑该技能回归"), statusCode: StatusCodes.Status403Forbidden);
            if (skill.OwnerId is null && !isAdmin)
                return Results.Json(new AguiError(ErrorCodes.SkillPermissionDenied, "系统内置技能仅管理员可跑回归"), statusCode: StatusCodes.Status403Forbidden);
            var store = ctx.RequestServices.GetService<SkillRegressionCaseStore>();
            if (store is null) return Results.Json(new AguiError(ErrorCodes.BadRequest, "回归用例库不可用"), statusCode: StatusCodes.Status503ServiceUnavailable);

            var run = await SkillRegressionRunner.RunAsync(skill, store.List(skillId), agents, store, ct);
            return Results.Ok(new { skillId, ran = run.Ran, allPassed = run.AllPassed, results = run.Results });
        }).AddEndpointFilter(new WebIdentity.RequireTokenFilter());

        // ---- 删除技能（技能归属者或系统管理员）；已被数字员工引用的技能一并解除（按引用的 agent 引用清理在下文做，暂无外键）----
        root.MapDelete("/{skillId}", (string skillId, HttpContext ctx, AuthService auth, AgentSkillCatalog catalog) =>
        {
            var user = WebIdentity.User(ctx, auth);
            if (user is null) return Unauthorized();
            var existing = catalog.Get(skillId);
            if (existing is null)
                return Results.NotFound(new AguiError(ErrorCodes.SkillNotFound, "技能不存在"));
            if (existing.OwnerId is null)
                return Results.Json(new AguiError(ErrorCodes.SkillPermissionDenied, "系统内置技能只读，请导出后另建"), statusCode: StatusCodes.Status403Forbidden);
            if (!auth.IsAdmin(user.UserId) && existing.OwnerId != user.UserId)
                return Results.Json(new AguiError(ErrorCodes.SkillPermissionDenied, "仅创建者或系统管理员可删除该技能"), statusCode: StatusCodes.Status403Forbidden);
            catalog.Remove(skillId);
            // 技能没了，其岗位经验也一并清空（1.0.349）。
            ctx.RequestServices.GetService<SkillLessonStore>()?.RemoveSkill(skillId);
            ctx.RequestServices.GetService<SkillRegressionCaseStore>()?.RemoveSkill(skillId);
            return Results.Ok(new { deleted = true, skillId });
        }).AddEndpointFilter(new WebIdentity.RequireTokenFilter());

        // ---- 暂停 / 恢复技能挂载（1.0.345）：产物质量连续低分时自动隔离，管理员可手工操作 ----
        //      暂停后：不再挂载到数字员工（模型看不到），直接执行会被拦截；恢复后立即重新挂载。
        //      权限与编辑一致：归属者或系统管理员（系统内置技能仅管理员）。
        root.MapPost("/{skillId}/suspend", (string skillId, string? reason, HttpContext ctx, AuthService auth,
            AgentSkillCatalog catalog, AgentCatalog agents) =>
        {
            var user = WebIdentity.User(ctx, auth);
            if (user is null) return Unauthorized();
            var skill = catalog.Get(skillId);
            if (skill is null) return Results.NotFound(new AguiError(ErrorCodes.SkillNotFound, "技能不存在"));
            var isAdmin = auth.IsAdmin(user.UserId);
            if (skill.OwnerId is null && !isAdmin)
                return Results.Json(new AguiError(ErrorCodes.SkillPermissionDenied, "系统内置技能仅管理员可操作"), statusCode: StatusCodes.Status403Forbidden);
            if (skill.OwnerId is not null && !isAdmin && skill.OwnerId != user.UserId)
                return Results.Json(new AguiError(ErrorCodes.SkillPermissionDenied, "仅创建者或系统管理员可操作该技能"), statusCode: StatusCodes.Status403Forbidden);
            if (skill.Suspended)
                return Results.Ok(new { suspended = true, skillId, already = true, reason = skill.SuspendedReason, suspendedAtMs = skill.SuspendedAtMs });
            var why = string.IsNullOrWhiteSpace(reason) ? "管理员手工暂停" : reason.Trim();
            if (why.Length > 200) why = why[..200];
            var atMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            catalog.Suspend(skillId, why, atMs);
            agents.InvalidateAgentsUsingSkill(skillId);
            return Results.Ok(new { suspended = true, skillId, already = false, reason = why, suspendedAtMs = atMs });
        }).AddEndpointFilter(new WebIdentity.RequireTokenFilter());

        root.MapPost("/{skillId}/resume", (string skillId, HttpContext ctx, AuthService auth,
            AgentSkillCatalog catalog, AgentCatalog agents) =>
        {
            var user = WebIdentity.User(ctx, auth);
            if (user is null) return Unauthorized();
            var skill = catalog.Get(skillId);
            if (skill is null) return Results.NotFound(new AguiError(ErrorCodes.SkillNotFound, "技能不存在"));
            var isAdmin = auth.IsAdmin(user.UserId);
            if (skill.OwnerId is null && !isAdmin)
                return Results.Json(new AguiError(ErrorCodes.SkillPermissionDenied, "系统内置技能仅管理员可操作"), statusCode: StatusCodes.Status403Forbidden);
            if (skill.OwnerId is not null && !isAdmin && skill.OwnerId != user.UserId)
                return Results.Json(new AguiError(ErrorCodes.SkillPermissionDenied, "仅创建者或系统管理员可操作该技能"), statusCode: StatusCodes.Status403Forbidden);
            if (!skill.Suspended)
                return Results.Ok(new { suspended = false, skillId, already = true });
            catalog.Resume(skillId);
            agents.InvalidateAgentsUsingSkill(skillId);
            return Results.Ok(new { suspended = false, skillId, already = false });
        }).AddEndpointFilter(new WebIdentity.RequireTokenFilter());

        // ---- 诊断修复（1.0.347）：产物质量连续低分 / 被暂停后，让模型按最近失败项改进正文并自测；??apply=true 时落库并恢复 ----
        //      权限与 suspend / resume 一致；本机(client)执行技能服务端无法自测，直接返回指引。
        root.MapPost("/{skillId}/repair", async (string skillId, bool? apply, bool? resume, HttpContext ctx, AuthService auth,
            AgentSkillCatalog catalog, AgentCatalog agents, AgentOptions options, ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            var user = WebIdentity.User(ctx, auth);
            if (user is null) return Unauthorized();
            var skill = catalog.Get(skillId);
            if (skill is null) return Results.NotFound(new AguiError(ErrorCodes.SkillNotFound, "技能不存在"));
            var isAdmin = auth.IsAdmin(user.UserId);
            if (skill.OwnerId is null && !isAdmin)
                return Results.Json(new AguiError(ErrorCodes.SkillPermissionDenied, "系统内置技能仅管理员可操作"), statusCode: StatusCodes.Status403Forbidden);
            if (skill.OwnerId is not null && !isAdmin && skill.OwnerId != user.UserId)
                return Results.Json(new AguiError(ErrorCodes.SkillPermissionDenied, "仅创建者或系统管理员可操作该技能"), statusCode: StatusCodes.Status403Forbidden);

            // 最近 7 天的产物质量记录（给界面看“为什么判它坏了”，不必再拉绩效看板）
            var quality = ctx.RequestServices.GetService<ArtifactQualityStore>();
            var since = DateTimeOffset.UtcNow.AddDays(-7).ToUnixTimeMilliseconds();
            var events = (quality?.Query(since) ?? (IReadOnlyList<ArtifactQualityEvent>)[])
                .Where(e => string.Equals(e.SkillId, skillId, StringComparison.Ordinal))
                .ToList();
            var lows = events.Where(e => !e.Satisfied || e.Score < ArtifactAlertPolicy.DefaultLowScoreThreshold).ToList();
            var diagnosis = new
            {
                runs = events.Count,
                lowRuns = lows.Count,
                missing = lows.SelectMany(e => e.Missing ?? []).Where(m => !string.IsNullOrWhiteSpace(m))
                    .Distinct(StringComparer.Ordinal).Take(8).ToArray(),
            };

            if (skill.ExecutionLocation == AgentSkillExecutionLocation.Client)
            {
                return Results.Ok(new
                {
                    skillId, skipped = true, ok = false, attempts = 0,
                    lastError = "本机(client)执行技能无法在服务端自测 / 修复；请编辑正文后在本机对话或试运行中验证。",
                    correctedBody = (string?)null, correctedDescription = (string?)null,
                    applied = false, resumed = false, diagnosis,
                });
            }

            var loopWatch = System.Diagnostics.Stopwatch.StartNew();
            SkillAutoFixResult fix;
            try
            {
                var fixer = new SkillAutoFixer(options, agents, loggerFactory);
                fix = await fixer.VerifyOrRepairAsync(skill, maxAttempts: 3, ct);
            }
            catch (Exception ex)
            {
                loggerFactory.CreateLogger("SkillApi.Repair").LogWarning(ex, "技能诊断修复异常：{SkillId}", skillId);
                return Results.Ok(new
                {
                    skillId, skipped = false, ok = false, attempts = 0,
                    lastError = "修复过程异常：" + ex.Message,
                    correctedBody = (string?)null, correctedDescription = (string?)null,
                    applied = false, resumed = catalog.Get(skillId)?.Suspended == false, diagnosis,
                });
            }

            var applied = false;
            // 回滚保护（1.0.357）：先记住修复前的正文，回归不通过时恢复。
            var previousBody = skill.Body;
            var previousDescription = skill.Description;
            var previousBuiltinVersion = skill.BuiltinVersion;
            // 先只写正文、不立即恢复：回归不通过时必须保持暂停（1.0.356）。
            if (apply == true && fix.Ok && !fix.Skipped && fix.CorrectedBody is { Length: > 0 } body)
                applied = catalog.ApplyRepair(skillId, body, fix.CorrectedDescription, resume: false);

            // 修复后自动回归（1.0.356）：有回归用例就真实跑一遍标准样例，结果回写用例。
            var regressions = ctx.RequestServices.GetService<SkillRegressionCaseStore>();
            SkillRegressionRunResult? regression = null;
            if (apply == true && fix.Ok && !fix.Skipped && regressions is not null && catalog.Get(skillId) is { } live)
            {
                var cases = regressions.List(skillId);
                if (cases.Count > 0)
                    regression = await SkillRegressionRunner.RunAsync(live, cases, agents, regressions, ct);
            }
            var regressionBlocked = SkillRegressionRunner.BlocksResume(regression);

            // 回归未通过：把技能恢复到修复前的正文（1.0.357），避免留下“可能更差”的版本。
            var rolledBack = false;
            if (applied && regressionBlocked)
            {
                rolledBack = catalog.RestoreBody(skillId, previousBody, previousDescription, previousBuiltinVersion);
                if (rolledBack) applied = false;
            }

            // 自测通过且回归通过（或没有可跑的回归）：才恢复挂载并失效缓存。
            if (apply == true && fix.Ok && !fix.Skipped && !regressionBlocked)
            {
                if (resume != false) catalog.Resume(skillId);
                agents.InvalidateAgentsUsingSkill(skillId);
            }
            else if (apply == true && fix.Ok && !fix.Skipped)
            {
                // 回归未通过：保持（或继续）暂停；新正文已写入，仍要让缓存重建。
                agents.InvalidateAgentsUsingSkill(skillId);
            }

            // 修复成功、真的改了正文且回归通过：旧失败经验标为已解决，并沉淀正向修复经验（1.0.349）。
            var lessonSaved = false;
            if (applied && !regressionBlocked)
            {
                var lessons = ctx.RequestServices.GetService<SkillLessonStore>();
                if (lessons is not null)
                {
                    lessons.ResolveFailures(skillId);
                    lessons.Add(new SkillLesson(
                        "sl_" + Guid.NewGuid().ToString("N")[..12],
                        skillId, null, SkillLessonKinds.Repair,
                        $"模型修正后自测通过（第 {fix.Attempts} 次）",
                        [],
                        "按新正文执行；产出后仍确认交付文件齐全",
                        "", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Resolved: false));
                    lessonSaved = true;
                }
            }

            // 回归未通过：记一条失败经验（含“已回滚”），避免下次再试同一种改法。
            if (regressionBlocked)
            {
                var lessonsFail = ctx.RequestServices.GetService<SkillLessonStore>();
                if (lessonsFail is not null)
                {
                    var failed = regression!.Results.Where(r => !r.Skipped && !r.Passed).ToList();
                    var detail = string.Join("、", failed.Select(r => $"{r.Kind} {r.Score} 分"));
                    var missing = failed.SelectMany(r => r.Missing)
                        .Where(m => !string.IsNullOrWhiteSpace(m))
                        .Distinct(StringComparer.Ordinal)
                        .Take(3)
                        .ToArray();
                    lessonsFail.Add(new SkillLesson(
                        "sl_" + Guid.NewGuid().ToString("N")[..12],
                        skillId, null, SkillLessonKinds.Failure,
                        $"修复后回归未通过（{detail}）" + (rolledBack ? "，已回滚到修复前正文" : ""),
                        missing,
                        "不要沿用这次改法；先复现回归失败再重新修复，或改用更可靠的执行方案",
                        "", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Resolved: false));
                }
            }

            // 自愈闭环事件（1.0.358）：记录这次修复尝试的结局与耗时，供质量看板统计。
            if (apply == true)
            {
                var outcome = fix.Skipped
                    ? SkillLoopOutcomes.Skipped
                    : !fix.Ok ? SkillLoopOutcomes.Failed
                    : rolledBack ? SkillLoopOutcomes.RolledBack
                    : applied ? SkillLoopOutcomes.Applied
                    : SkillLoopOutcomes.Resumed;
                ctx.RequestServices.GetService<SkillLoopEventStore>()?.Record(
                    skillId, outcome, fix.Attempts, loopWatch.ElapsedMilliseconds,
                    regression?.Ran ?? 0, !regressionBlocked);
                // 自愈闭环告警（1.0.359）：反复失败 / 回滚时补回归用例并通知运营。
                SkillLoopAlertBridge.OnOutcome(ctx.RequestServices, skillId, outcome);
            }

            return Results.Ok(new
            {
                skillId,
                fix.Skipped,
                fix.Ok,
                fix.Attempts,
                fix.LastError,
                fix.CorrectedBody,
                fix.CorrectedDescription,
                applied,
                resumed = catalog.Get(skillId)?.Suspended == false,
                lessonSaved,
                rolledBack,
                regressionRan = regression?.Ran ?? 0,
                regressionPassed = !regressionBlocked,
                regression,
                diagnosis,
            });
        }).AddEndpointFilter(new WebIdentity.RequireTokenFilter());

        // ---- 试运行技能（仅归属者或管理员；系统技能仅管理员）----
        //      /run 是无审批通道的手动执行，不能让它被任意登录用户触发 shell / HTTP；
        //      归属者运行自己建的 prompt 技能用于调试验证，shell / HTTP 则限定管理员与归属者。
        //      【图库】服务器直跑的文档技能（docx/pdf/pptx）也走图库注入：否则界面里手动试运行
        //      一份带 imageQuery 的稿子会一张图都配不到（而聊天路径是好的，很难想到差别在入口）。
        root.MapPost("/{skillId}/run", async (string skillId, SkillRunHttpRequest req, HttpContext ctx, AuthService auth, AgentSkillCatalog catalog, AgentCatalog agents, AgentOptions options, ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            var user = WebIdentity.User(ctx, auth);
            if (user is null) return Unauthorized();
            // 【试运行产物】内置文档类技能（docx / pptx / xlsx / pdf）把文件写到服务端磁盘，
            // 但试运行结果原本只有一段文本 —— 用户既看不到也拿不到产出的稿子。
            // 这里把结果文本里的 produce_file 标记入库为附件（与聊天路径**同一实现**），
            // 登记归属（产出者本人可读）后随响应回给前端，前端就能给下载 / 在线查看入口。
            var runAttachmentStore = ctx.RequestServices.GetService<AttachmentStore>();
            var runArtifacts = ctx.RequestServices.GetService<SkillRunArtifactStore>();
            var artifactLogger = loggerFactory.CreateLogger("SkillRunArtifacts");
            // 只扫**服务端执行**的路径：本机桥执行的产物落在用户自己机器上，服务端读不到
            // （扫了也只会 File.Exists 落空，却可能把服务端上的同名无关文件当产物）。
            object[] CollectArtifacts(string text)
            {
                if (runAttachmentStore is null) return [];
                var files = ProducedFileMarker.SaveAll(text, runAttachmentStore, artifactLogger);
                foreach (var f in files) runArtifacts?.Register(f.AttachmentId, user.UserId);
                return files.Cast<object>().ToArray();
            }
            // 本机桥 / 桌面自托管标记：经 DI 取得（测试宿主可为空）。
            // IsHostLocal=true 表示“宿主即用户本机”（桌面版），Client 技能可在 Web 宿主直接执行、无需独立本机桥。
            var nativeTunnel = ctx.RequestServices.GetService<AguiGroupChat.Agents.NativeTunnelService>();
            var hostLocal = ctx.RequestServices.GetService<ClientToolOptions>()?.IsHostLocal == true;
            var hostEnv = ctx.RequestServices.GetService<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>();
            var existing = catalog.Get(skillId);
            if (existing is null)
                return Results.NotFound(new AguiError(ErrorCodes.SkillNotFound, "技能不存在"));
            // 试运行失败时若调用者可编辑该技能，则允许“一键应用自动修复后的正文”
            var canEditThis = existing.OwnerId is not null
                ? (auth.IsAdmin(user.UserId) || existing.OwnerId == user.UserId)
                : auth.IsAdmin(user.UserId);
            // dotnet 技能：（建立限管理员，运行面向任意登录用户）。
            // server → 服务端 Roslyn 编译执行；client → 本机执行：优先按请求上报的 client（当前浏览器机器），
            // 否则落到平台级桥（一座桥的机器即本机）在本机编译运行；无可用桥才给出说明。
            if (existing.Kind == AgentSkillKind.Dotnet)
            {
                if (existing.ExecutionLocation == AgentSkillExecutionLocation.Client)
                {
                    var source = existing.Body ?? "";
                    var query = req.Query ?? "";
                    // 桌面版/自托管（宿主=用户本机）：直接在 Web 宿主上进程内跑 Roslyn，无需独立本机桥。
                    if (hostLocal && hostEnv is not null)
                    {
                        var hostDr = await agents.RunSkillAsync(existing, agents.PrepareDocumentSkillInput(existing, query, user.UserId), ct);
                        var (txtA, fixA) = await TryDotnetAutoFixAsync(existing, hostDr, canEditThis, options, agents, loggerFactory, ct);
                        var textA = "【本机 dotnet · 在桌面宿主机直接执行】\n" + txtA;
                        return Results.Ok(new { skillId, result = textA, localOnly = true, autoFix = fixA, attachments = CollectArtifacts(textA) });
                    }
                    var clientId = (req.ClientId ?? "").Trim();
                    if (nativeTunnel is not null && clientId.Length > 0 && nativeTunnel.HasClient(clientId))
                    {
                        // 客户端本机 dotnet 必须经“发起请求的这台浏览器所在机器”的本机桥执行
                        var localResult = await nativeTunnel.ExecuteDotnetForClientAsync(
                            clientId, source, query, TimeSpan.FromSeconds(160), ct);
                        if (!string.IsNullOrWhiteSpace(localResult))
                        {
                            var (txtB, fixB) = await TryDotnetAutoFixAsync(existing, localResult, canEditThis, options, agents, loggerFactory, ct);
                            return Results.Ok(new { skillId, result = ("【本机 dotnet · 经本机桥执行】client=" + clientId + " 结果：\n" + txtB), localOnly = true, autoFix = fixB });
                        }
                    }
                    // 没有为该请求机器找到桥：明确报告，而非回退到可能并非请求机器的其它桥
                    return Results.Ok(new { skillId, result = ("执行失败，没有安装桥：本客户端技能需在发起请求的浏览器所在机器执行，\n"
                        + "但该机器未连接本机桥（找不到 client=" + (clientId.Length == 0 ? "（未上报）" : clientId) + "）。请在本机安装/启动 AguiGroupChat.NativeBridge 后重试。"), localOnly = true });
                }
                var dr = await agents.RunSkillAsync(existing, agents.PrepareDocumentSkillInput(existing, req.Query ?? "", user.UserId), ct);
                var (txtC, fixC) = await TryDotnetAutoFixAsync(existing, dr, canEditThis, options, agents, loggerFactory, ct);
                return Results.Ok(new { skillId, result = txtC, autoFix = fixC, attachments = CollectArtifacts(txtC) });
            }
            // shell + client（本机执行技能）：试运行应经本机桥在本机（当前机器）执行，落到服务端 bash 会因缺 PowerShell 命令而 127。
            if (existing.Kind == AgentSkillKind.Shell && existing.ExecutionLocation == AgentSkillExecutionLocation.Client)
            {
                bool isAdminShell = auth.IsAdmin(user.UserId);
                bool byThisUser = existing.OwnerId is not null && (existing.OwnerId == user.UserId || isAdminShell);
                bool systemByAdmin = existing.OwnerId is null && isAdminShell;
                if (!byThisUser && !systemByAdmin)
                    return Results.Json(new AguiError(ErrorCodes.SkillPermissionDenied, "仅技能创建者或系统管理员可试运行该本机技能"), statusCode: StatusCodes.Status403Forbidden);
                var command = existing.Body ?? "";
                var query = req.Query ?? "";
                // 桌面版/自托管（宿主=用户本机 Win）：直接在宿主上用 PowerShell 执行，无需独立本机桥。
                if (hostLocal && hostEnv is not null)
                {
                    var root = Path.Combine(hostEnv.ContentRootPath, "data", "clienttoolruns", HostShell.SanitizeSegment(user.UserId));
                    var hostOut = await HostShell.RunAsync(root, command, null, 60, query, ct);
                    var textH = "【本机 shell · 在桌面宿主机直接执行】\n" + hostOut;
                    return Results.Ok(new { skillId, result = textH, localOnly = true, attachments = CollectArtifacts(textH) });
                }
                var clientIdShell = (req.ClientId ?? "").Trim();
                if (nativeTunnel is not null && clientIdShell.Length > 0 && nativeTunnel.HasClient(clientIdShell))
                {
                    // 客户端本机 shell 必须经“发起请求的这台浏览器所在机器”的本机桥执行
                    var shellLocal = await nativeTunnel.ExecuteForClientAsync(
                        clientIdShell, command, null, 60, query, TimeSpan.FromSeconds(120), ct);
                    if (!string.IsNullOrWhiteSpace(shellLocal))
                        return Results.Ok(new { skillId, result = ("【本机 shell · 经本机桥执行】client=" + clientIdShell + " 结果：\n" + shellLocal), localOnly = true });
                }
                // 该请求机器没有桥：明确报告，而非回退到可能并非请求机器的其它桥
                return Results.Ok(new { skillId, result = ("执行失败，没有安装桥：本客户端 shell 技能需在发起请求的浏览器所在机器执行，\n"
                    + "但该机器未连接本机桥（找不到 client=" + (clientIdShell.Length == 0 ? "（未上报）" : clientIdShell) + "）。请在本机安装/启动 AguiGroupChat.NativeBridge 后重试。"), localOnly = true });
            }
            // 任何仍带 ExecutionLocation=Client 的技能（http / prompt 等）试运行都不应落服务端跑——
            // 它们应在其“本机/前端或桥所在机器”执行。这里既没有浏览器可驱动 http，也没有 dotnet/shell 的桥执行入口，
            // 因此给明确指引而非静默用服务端 Runner（会与本机语境错位）。
            if (existing.ExecutionLocation == AgentSkillExecutionLocation.Client)
            {
                var ownerGateOk = existing.OwnerId is not null ? (existing.OwnerId == user.UserId || auth.IsAdmin(user.UserId))
                    : auth.IsAdmin(user.UserId);
                if (!ownerGateOk)
                    return Results.Json(new AguiError(ErrorCodes.SkillPermissionDenied, "仅创建者或系统管理员可试运行该技能"), statusCode: StatusCodes.Status403Forbidden);
                var hintClient = "该技能标注为“本机(client)执行”，但 kind=" + existing.Kind + " 需要在其本机/前端或桥所在机器执行，\n"
                    + "技能库试运行无法在当前宿主代为驱动。请在挂载了本技能、且其机器装有/连接本机桥的数字员工对话中触发，\n"
                    + "由系统在你本机执行并回传；或把 executionLocation 改为 server 后在此试运行。\n\n正文：\n" + (existing.Body ?? "") + "\n\n请求：" + (req.Query ?? "");
                return Results.Ok(new { skillId, result = hintClient, localOnly = true });
            }
            // 其余为 Server 执行技能：系统技能无归属者 → 仅管理员可试运行；归属技能 → 归属者或管理员可试运行
            if (existing.OwnerId is not null && existing.OwnerId != user.UserId && !auth.IsAdmin(user.UserId))
                return Results.Json(new AguiError(ErrorCodes.SkillPermissionDenied, "仅技能创建者或系统管理员可试运行"), statusCode: StatusCodes.Status403Forbidden);
            if (existing.OwnerId is null && !auth.IsAdmin(user.UserId))
                return Results.Json(new AguiError(ErrorCodes.SkillPermissionDenied, "仅系统管理员可试运行系统技能"), statusCode: StatusCodes.Status403Forbidden);
            var result = await agents.RunSkillAsync(existing, req.Query ?? "", ct);
            return Results.Ok(new { skillId, result, attachments = CollectArtifacts(result) });
        }).AddEndpointFilter(new WebIdentity.RequireTokenFilter());

        // ---- 试运行参数建议：按技能的说明 / 正文 / 类型，让大模型给一个典型示例 query，供前端试运行时预填（权限同 /run）----
        root.MapPost("/{skillId}/suggest", async (string skillId, HttpContext ctx, AuthService auth, AgentSkillCatalog catalog, AgentOptions options, ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            var user = WebIdentity.User(ctx, auth);
            if (user is null) return Unauthorized();
            var existing = catalog.Get(skillId);
            if (existing is null)
                return Results.NotFound(new AguiError(ErrorCodes.SkillNotFound, "技能不存在"));
            if (existing.OwnerId is not null && existing.OwnerId != user.UserId && !auth.IsAdmin(user.UserId))
                return Results.Json(new AguiError(ErrorCodes.SkillPermissionDenied, "仅技能创建者或系统管理员可试运行"), statusCode: StatusCodes.Status403Forbidden);
            if (existing.OwnerId is null && !auth.IsAdmin(user.UserId))
                return Results.Json(new AguiError(ErrorCodes.SkillPermissionDenied, "仅系统管理员可试运行系统技能"), statusCode: StatusCodes.Status403Forbidden);
            var suggestion = await SkillQuerySuggester.SuggestAsync(options, existing, loggerFactory.CreateLogger("SkillApi.Suggest"), ct);
            return Results.Ok(new { skillId, suggestion });
        }).AddEndpointFilter(new WebIdentity.RequireTokenFilter());
    }

    /// <summary>Shell / HTTP / .NET 技能属特权类型：创建 / 修改 / 运行仅限管理员（归属者试运行 prompt 由 /run 单独管控）。</summary>
    private static bool RequiresPrivilegedKind(string? kind)
        => Enum.TryParse<AgentSkillKind>(kind, true, out var k) && k is AgentSkillKind.Shell or AgentSkillKind.Http or AgentSkillKind.Dotnet or AgentSkillKind.Org_deploy;

    private static object ToDto(AgentSkillDefinition s, bool canReadBody)
    {
        var dto = new Dictionary<string, object?>
        {
            ["skillId"] = s.SkillId,
            ["name"] = s.Name,
            ["kind"] = s.Kind.ToString().ToLowerInvariant(),
            ["requiresApproval"] = s.RequiresApproval,
            ["executionLocation"] = s.ExecutionLocation.ToString().ToLowerInvariant(),
            ["ownerId"] = s.OwnerId,
            // 细粒度访问授权：允许访问的技能用户组白名单（null = 不限制）
            ["allowedUserGroupIds"] = s.AllowedUserGroupIds is { Count: > 0 } ? s.AllowedUserGroupIds : null,
            // 图库写入能力（管理员开启；仅服务端非 prompt 技能）
            ["imageWrite"] = s.ImageWrite,
            ["imageWriteLibraryIds"] = s.ImageWriteLibraryIds is { Count: > 0 } ? s.ImageWriteLibraryIds : null,
            ["imageWriteRequiresApproval"] = s.ImageWriteRequiresApproval,
            ["imageWriteMaxLibraries"] = s.ImageWriteMaxLibraries,
            ["imageWriteMaxImages"] = s.ImageWriteMaxImages,
            // 暂停挂载（1.0.345）：连续低分被自动隔离或管理员手工暂停
            ["suspended"] = s.Suspended,
            ["suspendedReason"] = s.SuspendedReason,
            ["suspendedAtMs"] = s.SuspendedAtMs,
        };
        if (canReadBody)
        {
            dto["description"] = s.Description;
            dto["body"] = s.Body;
            dto["parametersJson"] = s.ParametersJson;
            dto["interpreter"] = s.Interpreter;
            dto["httpTimeoutSeconds"] = s.HttpTimeoutSeconds;
            dto["clientRunner"] = s.ClientRunner;
        }
        return dto;
    }

    private static (AgentSkillDefinition? Def, IResult? Err) BuildDef(SkillDefHttpRequest req, string? ownerId, bool authorAdmin,
        AguiGroupChat.Agents.UserGroups.UserGroupStore? groups = null,
        AguiGroupChat.Agents.ImageLibraryCatalog? libs = null)
    {
        var rawId = (req.SkillId ?? "").Trim();
        var id = AgentSkillDefinition.IsValidAsciiToolId(rawId)
            ? rawId
            : AgentSkillDefinition.ToAsciiToolId(rawId, new HashSet<string>(StringComparer.Ordinal));
        if (string.IsNullOrWhiteSpace(id))
            return (null, Results.BadRequest(new AguiError(ErrorCodes.BadRequest, "技能标识不能为空")));
        if (string.IsNullOrWhiteSpace(req.Name))
            return (null, Results.BadRequest(new AguiError(ErrorCodes.BadRequest, "技能名称不能为空")));
        if (string.IsNullOrWhiteSpace(req.Description))
            return (null, Results.BadRequest(new AguiError(ErrorCodes.BadRequest, "技能描述不能为空（模型据此决定是否调用）")));
        var kind = Enum.TryParse<AgentSkillKind>(req.Kind, true, out var k) ? k : AgentSkillKind.Prompt;
        // Shell 技能强制需审批（任意本机命令执行面最大）；HTTP / 提示词技能按创建者的 RequiresApproval 决定
        //（是否允许调用本机 / 内网另由 Agents:AllowPrivateSkillEndpoints 控制，安全兜底与其解耦）
        var requiresApproval = kind is AgentSkillKind.Shell ? true : (req.RequiresApproval ?? true);
        // 客户端执行位置：默认服务端（现状不变）；Shell / HTTP 客户端技能一律强制需审批（本机执行 / 外部请求安全兜底）
        var executionLocation = Enum.TryParse<AgentSkillExecutionLocation>(req.ExecutionLocation, true, out var loc)
            && req.ExecutionLocation is not null
            ? loc
            : AgentSkillExecutionLocation.Server;
        if (kind == AgentSkillKind.Dotnet)
        {
            // .NET（C# 动态编译）技能：动态代码面最高 → 一律强制审批（安全兜底）。
            // 执行位置遵循用户选择：server = 服务端 Roslyn 编译执行；client = 经内网本机桥在该用户机器/内网机编译执行（浏览器本身不能跑通用 C#）。
            requiresApproval = true;
        }
        if (executionLocation == AgentSkillExecutionLocation.Client)
            requiresApproval = true; // 客户端执行（尤其 shell / dotnet）属本机/外部副作用，一律需人工批准

        // 图库写入能力（1.0.277）：管理员显式开启；仅服务端非 prompt 技能可持有
        //（客户端技能在用户机器上跑，拿不到平台自令牌；prompt 技能没有执行面）。
        var imageWrite = authorAdmin
            && req.ImageWrite == true
            && kind is not AgentSkillKind.Prompt
            && executionLocation == AgentSkillExecutionLocation.Server;
        if (req.ImageWriteMaxLibraries is { } ql && ql is not (>= 1 and <= 10))
            return (null, Results.BadRequest(new AguiError(ErrorCodes.BadRequest, "imageWriteMaxLibraries 需在 1..10")));
        if (req.ImageWriteMaxImages is { } qi && qi is not (>= 1 and <= 200))
            return (null, Results.BadRequest(new AguiError(ErrorCodes.BadRequest, "imageWriteMaxImages 需在 1..200")));
        // 可写入的已有图库（1.0.278）：仅 imageWrite 生效时保留；每个 ID 必须真实存在
        var writeLibs = imageWrite
            ? req.ImageWriteLibraryIds?.Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id.Trim()).Distinct().ToList()
            : null;
        if (writeLibs is { Count: > 0 })
        {
            if (libs is null)
                return (null, Results.BadRequest(new AguiError(ErrorCodes.BadRequest, "图库服务不可用，无法校验授权图库")));
            foreach (var libId in writeLibs)
                if (libs.GetLibrary(libId) is null)
                    return (null, Results.BadRequest(new AguiError(ErrorCodes.BadRequest, $"引用的图库不存在：{libId}")));
        }
        if ((kind == AgentSkillKind.Shell || kind == AgentSkillKind.Dotnet) && string.IsNullOrWhiteSpace(req.Body))
            return (null, Results.BadRequest(new AguiError(ErrorCodes.BadRequest, "该技能正文（shell 命令/脚本，或 C# 源码）不能为空")));
        if (kind == AgentSkillKind.Http && string.IsNullOrWhiteSpace(req.Body))
            return (null, Results.BadRequest(new AguiError(ErrorCodes.BadRequest, "HTTP 技能的正文（JSON 配置）不能为空")));
        // 允许挂载的用户组白名单：id 必须真实存在（防手写错 id 导致技能静默对所有人不可挂载）
        if (groups is not null && req.AllowedUserGroupIds is { Count: > 0 } rawGroups)
        {
            foreach (var raw in rawGroups)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                var gid = raw.Trim();
                if (!gid.StartsWith("ug_", StringComparison.OrdinalIgnoreCase))
                    return (null, Results.BadRequest(new AguiError(ErrorCodes.BadRequest, $"用户分组 ID 需以 ug_ 开头：{gid}")));
                if (groups.Get(gid) is null)
                    return (null, Results.BadRequest(new AguiError(ErrorCodes.BadRequest, $"引用的用户分组不存在：{gid}")));
            }
        }

        return (new AgentSkillDefinition
        {
            SkillId = id,
            Name = req.Name.Trim(),
            Description = req.Description.Trim(),
            Kind = kind,
            Body = req.Body ?? "",
            ParametersJson = req.ParametersJson ?? "",
            Interpreter = string.IsNullOrWhiteSpace(req.Interpreter) ? null : req.Interpreter.Trim(),
            HttpTimeoutSeconds = Math.Clamp(req.HttpTimeoutSeconds.GetValueOrDefault(30), 5, 120),
            RequiresApproval = requiresApproval,
            OwnerId = ownerId,
            ExecutionLocation = executionLocation,
            ClientRunner = AgentApi.BuildClientRunner(kind, executionLocation, req.Body ?? "", req.ClientRunner),
            // 细粒度访问授权：允许挂载/可见的技能用户组白名单（空 = 不限制，向后兼容）
            AllowedUserGroupIds = req.AllowedUserGroupIds?.Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id.Trim()).Distinct().ToList() is { Count: > 0 } glist ? glist : null,
            ImageWrite = imageWrite,
            ImageWriteLibraryIds = writeLibs is { Count: > 0 } ? writeLibs : null,
            ImageWriteRequiresApproval = imageWrite && req.ImageWriteRequiresApproval == true,
            ImageWriteMaxLibraries = imageWrite ? Math.Clamp(req.ImageWriteMaxLibraries ?? 1, 1, 10) : null,
            ImageWriteMaxImages = imageWrite ? Math.Clamp(req.ImageWriteMaxImages ?? 20, 1, 200) : null,
        }, null);
    }

    private static UserAccount? RequireUser(HttpContext ctx, AuthService auth) => AgentApi.RequireUser(ctx, auth);

    // ---- 生成技能时的「目标运行环境」描述：优先按用户偏好/上报的浏览器系统；否则回退为服务端宿主系统。----
    private static string NormalizeOs(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "未知";
        var v = s.Trim().ToLowerInvariant();
        return v switch
        {
            "win" or "win32" or "windows" => "Windows",
            "mac" or "macos" or "darwin" or "osx" => "macOS",
            "linux" or "unix" => "Linux",
            _ => v,
        };
    }

    private static string DescribeServerEnv()
        => "目标运行于服务端（宿主系统：" + (OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : "Linux") + "）";

    private static string DescribeClientEnv(string? clientOs)
    {
        var os = NormalizeOs(clientOs);
        return os switch
        {
            "Windows" => "目标运行于本机（浏览器所在系统：Windows，用 PowerShell 脚本）",
            "macOS" => "目标运行于本机（浏览器所在系统：macOS，用 bash 脚本）",
            "Linux" => "目标运行于本机（浏览器所在系统：Linux，用 bash 脚本）",
            _ => "目标运行于本机（浏览器所在系统：未上报" + (string.IsNullOrWhiteSpace(clientOs) ? "" : "（" + clientOs + "）") + "）",
        };
    }

    private static IResult Unauthorized()
        => Results.Json(new AguiError(ErrorCodes.UserUnauthorized, "未登录或令牌无效"), statusCode: StatusCodes.Status401Unauthorized);

    /// <summary>C#（dotnet）技能试运行遇编译类失败时，自动让大模型修复正文并回传“一键应用”候选（不自动保存）。</summary>
    private static async Task<(string Result, object? AutoFix)> TryDotnetAutoFixAsync(
        AgentSkillDefinition skill, string runResult, bool canEdit,
        AgentOptions options, AgentCatalog agents, ILoggerFactory loggerFactory, CancellationToken ct)
    {
        if (!canEdit) return (runResult, null);
        var isCompileFailure = runResult.Contains(".NET 技能编译失败", StringComparison.Ordinal)
            || runResult.Contains("NuGet 引用还原失败", StringComparison.Ordinal)
            || runResult.Contains("缺少入口", StringComparison.Ordinal)
            || runResult.Contains("编译校验失败", StringComparison.Ordinal)
            || runResult.Contains(AguiGroupChat.Agents.SkillArtifactContract.FailurePrefix, StringComparison.Ordinal);
        if (!isCompileFailure) return (runResult, null);
        if (string.Equals(options.Provider, "mock", StringComparison.OrdinalIgnoreCase)) return (runResult, null);
        try
        {
            var fixer = new SkillAutoFixer(options, agents, loggerFactory);
            var smoke = await fixer.VerifyOrRepairAsync(skill, maxAttempts: 3, ct).ConfigureAwait(false);
            if (smoke.CorrectedBody is { Length: > 0 } && !string.Equals(smoke.CorrectedBody, skill.Body, StringComparison.Ordinal))
                return (runResult, new
                {
                    ok = smoke.Ok,
                    attempts = smoke.Attempts,
                    correctedBody = smoke.CorrectedBody,
                    lastError = smoke.LastError,
                });
        }
        catch (Exception ex)
        {
            loggerFactory.CreateLogger("SkillApi.Run").LogWarning(ex, "dotnet 技能试运行自动修复失败：{SkillId}", skill.SkillId);
        }
        return (runResult, null);
    }
}

/// <summary>技能库请求体：技能定义字段（与 <see cref="AgentSkillDefinition"/> 对齐，SkillId 为可选新标识，更新时用 URL）。</summary>
public sealed record SkillDefHttpRequest(
    string? SkillId,
    string Name,
    string Description,
    string Kind,
    string? Body,
    string? ParametersJson,
    string? Interpreter,
    int? HttpTimeoutSeconds,
    bool? RequiresApproval,
    string? ExecutionLocation = null,
    string? ClientRunner = null,
    IReadOnlyList<string>? AllowedUserGroupIds = null,
    // 图库写入能力（1.0.277）：仅管理员可开，且仅对服务端非 prompt 技能生效
    bool? ImageWrite = null,
    // 可写入的已有图库（1.0.278）：imageWrite 开启时生效；null/空 = 只能写本轮新建的库
    IReadOnlyList<string>? ImageWriteLibraryIds = null,
    // 写入审批与按技能配额（1.0.279）
    bool? ImageWriteRequiresApproval = null,
    int? ImageWriteMaxLibraries = null,
    int? ImageWriteMaxImages = null);

/// <summary>技能试运行请求体。ClientId：本机执行的机器标识（浏览器本机桥 /ag-ui/bridge/info 读取），用于把 client dotnet 送到当前机器执行。</summary>
public sealed record SkillRunHttpRequest(string Query = "", string? ClientId = null);

/// <summary>新增技能经验请求（1.0.351）：AgentId 为空 = 技能级经验。</summary>
public sealed record SkillLessonCreateRequest(string? AgentId, string Summary, string[]? Missing = null, string? Fix = null);

/// <summary>用自然语言生成技能配置的请求体。ClientOs：客户端/本机执行时的浏览器所在系统（windows/macos/linux/other），供生成对应脚本；服务端执行时后端自行推断。</summary>
public sealed record SkillGenerateRequest(string Request, bool? PreferClient = null, string? ClientOs = null);
