using System.Text.Json;
using AguiGroupChat.Agents;
using AguiGroupChat.Hub.Infra;
using AguiGroupChat.Hub.Persistence;
using AguiGroupChat.Hub.Persistence.Relational;
using AguiGroupChat.Hub.Users;
using Microsoft.Extensions.Logging;

namespace AguiGroupChat.Web;

/// <summary>
/// 管理员「执行配置（运行时覆盖）」API：
///   - 默认值来自 <c>Agents:Execution</c>；
///   - 本端点把管理员写回的覆盖<b>直接改到进程内共享的单例 <see cref="ExecutionOptions"/></b>（数字员工网关即持它），
///     因此无需重启/重建即可让后续调用使用新值；
///   - 并把当前生效值持久化到扩展区「executionRuntime」（memory JSON 快照 / postgres 等 agui_sections），重启后自动恢复。
///   角色级覆盖（DisableBridge/DisableRelay/DisableOrgRoute）在“数字员工”编辑表单维护，不属于本端点。
/// </summary>
public static class ExecutionRuntimeApi
{
    public static void MapExecutionRuntimeApi(this WebApplication app)
    {
        var root = app.MapGroup("/ag-ui/admin/execution");

        // 读取当前生效执行参数（含归一化后的默认，供前端回填）。
        root.MapGet("/", (ExecutionOptions exec) => Results.Ok(ToDto(exec)))
            .AddEndpointFilter(new WebIdentity.RequireAdminFilter());

        // 管理员写入运行时覆盖：字段可选，未给的原有效值保留；非法值会经 Normalize 回退默认，
        // 返回体即“修正后生效值”。改的是共享单例 ExecutionOptions → 网关下次调用即生效。
        root.MapPost("/", (ExecutionPatchReq req, ExecutionOptions exec, ChangeHub changes,
            AuthService auth, AuditLogService audit, ILoggerFactory lf, HttpContext ctx) =>
        {
            Merge(req, exec);
            exec.Normalize(lf.CreateLogger("ExecutionRuntime"));
            changes?.Notify(); // 触发持久化 section（若 register），重启可恢复
            var me = WebIdentity.UserId(ctx) ?? "?";
            audit.Record("execution.patch", me, auth.GetUser(me)?.Username, detail: ToAudit(exec));
            return Results.Ok(new { ok = true, execution = ToDto(exec) });
        })
            .AddEndpointFilter(new WebIdentity.RequireAdminFilter());
    }

    /// <summary>按 PUT 部分字段合并（其余保持现有效值）。</summary>
    private static void Merge(ExecutionPatchReq req, ExecutionOptions e)
    {
        if (req.StreamTimeoutMinutes is { } v) e.StreamTimeoutMinutes = v;
        if (req.MaxModelAttempts is { } a) e.MaxModelAttempts = a;
        if (req.InteractionTtlMinutes is { } i) e.InteractionTtlMinutes = i;
        if (req.SessionLockTtlMinutes is { } ls) e.SessionLockTtlMinutes = ls;
        if (req.ApprovedSkillTtlMinutes is { } ak) e.ApprovedSkillTtlMinutes = ak;
        if (req.SessionLockMaxEntries is { } me2) e.SessionLockMaxEntries = me2;
        if (req.CoordinatorPlanMaxItems is { } ci) e.CoordinatorPlanMaxItems = ci;
        if (req.CoordinatorPlanMaxSteps is { } cs) e.CoordinatorPlanMaxSteps = cs;
        if (req.MaxRecursiveRounds is { } rr) e.MaxRecursiveRounds = rr;
        if (req.MaxRouteDepth is { } rd) e.MaxRouteDepth = rd;
        if (req.MaxInteractionRounds is { } ir) e.MaxInteractionRounds = ir;
        if (req.ExecutionOrder is { } order) e.ExecutionOrder = order;
        if (req.EnableBridge is { } eb) e.EnableBridge = eb;
        if (req.EnablePipeline is { } ep) e.EnablePipeline = ep;
        if (req.EnableRelay is { } er) e.EnableRelay = er;
        if (req.EnableOrgRoute is { } eo) e.EnableOrgRoute = eo;
    }

    private static object ToDto(ExecutionOptions e) => new
    {
        e.StreamTimeoutMinutes, e.MaxModelAttempts, e.InteractionTtlMinutes,
        e.SessionLockTtlMinutes, e.ApprovedSkillTtlMinutes, e.SessionLockMaxEntries,
        e.CoordinatorPlanMaxItems, e.CoordinatorPlanMaxSteps, e.MaxRecursiveRounds,
        e.MaxRouteDepth, e.MaxInteractionRounds,
        executionOrder = e.ExecutionOrder,
        e.EnableBridge, e.EnablePipeline, e.EnableRelay, e.EnableOrgRoute,
    };

    private static string ToAudit(ExecutionOptions e)
        => $"order={string.Join(",", e.ExecutionOrder)};" +
           $"stream={e.StreamTimeoutMinutes};attempts={e.MaxModelAttempts};" +
           $"bridge={e.EnableBridge};pipeline={e.EnablePipeline};relay={e.EnableRelay};org={e.EnableOrgRoute}";

    /// <summary>
    /// 注册「executionRuntime」到持久化：memory 写 JSON 快照，postgres/mysql/sqlite 落 agui_sections。
    /// 须在状态恢复 InitializePersistence 之前（Web/Desktop 组合根）与其它 Register*Persistence 并排调用。
    /// </summary>
    public static void RegisterExecutionRuntimePersistence(this IServiceProvider services)
    {
        var exec = services.GetRequiredService<AguiGroupChat.Agents.ExecutionOptions>();
        Func<object?> snapshot = () => exec;
        Action<JsonElement> restore = element =>
        {
            var saved = element.Deserialize<AguiGroupChat.Agents.ExecutionOptions>(AguiJson.Options);
            if (saved is null) return;
            exec.StreamTimeoutMinutes = saved.StreamTimeoutMinutes;
            exec.MaxModelAttempts = saved.MaxModelAttempts;
            exec.InteractionTtlMinutes = saved.InteractionTtlMinutes;
            exec.SessionLockTtlMinutes = saved.SessionLockTtlMinutes;
            exec.ApprovedSkillTtlMinutes = saved.ApprovedSkillTtlMinutes;
            exec.SessionLockMaxEntries = saved.SessionLockMaxEntries;
            exec.CoordinatorPlanMaxItems = saved.CoordinatorPlanMaxItems;
            exec.CoordinatorPlanMaxSteps = saved.CoordinatorPlanMaxSteps;
            exec.MaxRecursiveRounds = saved.MaxRecursiveRounds;
            exec.MaxRouteDepth = saved.MaxRouteDepth;
            exec.MaxInteractionRounds = saved.MaxInteractionRounds;
            exec.ExecutionOrder = saved.ExecutionOrder;
            exec.EnableBridge = saved.EnableBridge;
            exec.EnablePipeline = saved.EnablePipeline;
            exec.EnableRelay = saved.EnableRelay;
            exec.EnableOrgRoute = saved.EnableOrgRoute;
            exec.Normalize();
        };
        var persistence = services.GetService<PersistenceService>();
        if (persistence is not null) persistence.AddSection("executionRuntime", snapshot, restore);
        else services.GetService<ISectionStore>()?.AddSection("executionRuntime", snapshot, restore);
    }
}

/// <summary>管理员写入请求：字段可选，未给的原有效值保留。</summary>
public sealed record ExecutionPatchReq(
    int? StreamTimeoutMinutes, int? MaxModelAttempts, int? InteractionTtlMinutes,
    int? SessionLockTtlMinutes, int? ApprovedSkillTtlMinutes, int? SessionLockMaxEntries,
    int? CoordinatorPlanMaxItems, int? CoordinatorPlanMaxSteps, int? MaxRecursiveRounds,
    int? MaxRouteDepth, int? MaxInteractionRounds,
    string[]? ExecutionOrder,
    bool? EnableBridge, bool? EnablePipeline, bool? EnableRelay, bool? EnableOrgRoute);
