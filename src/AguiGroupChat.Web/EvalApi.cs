using System.Diagnostics;
using AguiGroupChat.Agents;
using AguiGroupChat.Hub.Infra;
using AguiGroupChat.Hub.Models;
using AguiGroupChat.Hub.Persistence;
using AguiGroupChat.Hub.Users;

namespace AguiGroupChat.Web;

/// <summary>
/// 数字员工评测集 / 回归（管理员，8.20 之外的运营能力）：
///   GET    /ag-ui/admin/eval-cases            —— 列出用例 + 可选数字员工（供新建下拉）
///   POST   /ag-ui/admin/eval-cases            —— 新建 / 更新用例
///   DELETE /ag-ui/admin/eval-cases/{id}       —— 删除用例
///   POST   /ag-ui/admin/eval-cases/run        —— 一键跑分（跑全部启用项，或指定 ids）
/// 评分只按「期望要点是否出现」的确定性判据（不用 LLM 当裁判），便宜可复现。
/// </summary>
public static class EvalApi
{
    public sealed record EvalCaseRequest(string? CaseId, string? Name, string? AgentId, string? Prompt,
        string[]? Expected, string? MatchMode, bool? Enabled, string? ExpectDeliverable);

    public sealed record EvalRunRequest(string[]? CaseIds);

    public static void MapEvalApi(this WebApplication app)
    {
        var root = app.MapGroup("/ag-ui/admin/eval-cases");

        // ---- 列出用例 + 被测员工候选（新建下拉）----
        root.MapGet("/", (EvalCaseStore store, AgentCatalog catalog) =>
        {
            var agents = catalog.ListDefinitions()
                .Select(d => new { agentId = d.AgentId, name = string.IsNullOrWhiteSpace(d.Nickname) ? d.AgentId : d.Nickname })
                .OrderBy(a => a.name, StringComparer.CurrentCulture)
                .ToList();
            return Results.Ok(new { cases = store.List(), agents, runs = store.Runs() });
        }).AddEndpointFilter(new WebIdentity.RequireAdminFilter());

        // ---- 新建 / 更新 ----
        root.MapPost("/", (EvalCaseRequest req, HttpContext ctx, AuthService auth,
            EvalCaseStore store, AgentCatalog catalog, ChangeHub changes, AuditLogService audit) =>
        {
            var me = WebIdentity.UserId(ctx)!;
            var agentId = (req.AgentId ?? "").Trim();
            if (agentId.Length == 0)
                return Results.BadRequest(new AguiError(ErrorCodes.BadRequest, "必须选择被测数字员工"));
            if (catalog.GetDefinition(agentId) is null)
                return Results.BadRequest(new AguiError(ErrorCodes.BadRequest, $"数字员工不存在：{agentId}"));
            var prompt = (req.Prompt ?? "").Trim();
            if (prompt.Length == 0)
                return Results.BadRequest(new AguiError(ErrorCodes.BadRequest, "提问内容不能为空"));
            if (prompt.Length > 2000)
                return Results.BadRequest(new AguiError(ErrorCodes.BadRequest, "提问内容最长 2000 字"));

            var existing = !string.IsNullOrWhiteSpace(req.CaseId) ? store.Get(req.CaseId!.Trim()) : null;
            var isNew = existing is null;
            var @case = existing ?? new EvalCase { CaseId = "eval_" + IdGenerator.NewId(), AgentId = agentId };

            @case.Name = (req.Name ?? "").Trim();
            @case.AgentId = agentId;
            @case.Prompt = prompt;
            @case.Expected = (req.Expected ?? [])
                .Select(e => (e ?? "").Trim())
                .Where(e => e.Length > 0)
                .Take(50) // 上限：避免误粘超长文本
                .ToArray();
            @case.MatchMode = string.Equals(req.MatchMode, "all", StringComparison.OrdinalIgnoreCase) ? "all" : "any";
            @case.ExpectDeliverable = AgentEvalRunner.NormalizeDeliverable(req.ExpectDeliverable);
            @case.Enabled = req.Enabled ?? true;
            if (isNew)
            {
                @case.CreatedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                @case.CreatedBy = me;
            }
            else
            {
                // 用例内容改了 → 旧跑分基线失效（否则报告/页签会显示过期的通过结果）
                @case.LastPassed = null;
                @case.LastRunAtMs = 0;
                @case.LastReply = "";
                @case.LastError = "";
            }

            store.Put(@case);
            changes.Notify();
            audit.Record(isNew ? "evalcase.create" : "evalcase.update", me, auth.GetUser(me)?.Username,
                targetType: "evalcase", targetId: @case.CaseId, detail: $"评测用例「{@case.Name}」→ {@case.AgentId}");
            return Results.Ok(new { caseItem = @case });
        }).AddEndpointFilter(new WebIdentity.RequireAdminFilter());

        // ---- 删除 ----
        root.MapDelete("/{id}", (string id, HttpContext ctx, AuthService auth,
            EvalCaseStore store, ChangeHub changes, AuditLogService audit) =>
        {
            var me = WebIdentity.UserId(ctx)!;
            if (!store.Remove(id)) return Results.NotFound(new AguiError(ErrorCodes.BadRequest, "用例不存在"));
            changes.Notify();
            audit.Record("evalcase.delete", me, auth.GetUser(me)?.Username,
                targetType: "evalcase", targetId: id, detail: "删除评测用例");
            return Results.Ok(new { ok = true });
        }).AddEndpointFilter(new WebIdentity.RequireAdminFilter());

        // ---- 一键跑分 ----
        root.MapPost("/run", async (EvalRunRequest req, EvalCaseStore store, AgentEvalRunner runner, ChangeHub changes, CancellationToken ct) =>
        {
            var ids = req.CaseIds is { Length: > 0 } ? req.CaseIds.ToHashSet(StringComparer.Ordinal) : null;
            var cases = store.List().Where(c => ids is null ? c.Enabled : ids.Contains(c.CaseId)).ToList();
            var items = new List<EvalRunItem>(cases.Count);
            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var sw = Stopwatch.StartNew();
            foreach (var c in cases)
            {
                ct.ThrowIfCancellationRequested();
                var item = await runner.RunAsync(c, ct);
                // 持久化跑分基线（随用例保存）：供页签回显与组织体检报告的「回归通过率」
                c.LastPassed = item.Passed;
                c.LastRunAtMs = nowMs;
                c.LastReply = item.Reply;
                c.LastError = item.Error;
                store.Put(c);
                items.Add(item);
            }
            sw.Stop();
            if (cases.Count > 0) changes.Notify(); // 让其它客户端的「评测回归」页同步刷新
            if (items.Count > 0) store.RecordRun(nowMs, items.Count, items.Count(i => i.Passed)); // 评分趋势历史
            return Results.Ok(new
            {
                total = items.Count,
                passed = items.Count(i => i.Passed),
                durationMs = sw.ElapsedMilliseconds,
                items,
            });
        }).AddEndpointFilter(new WebIdentity.RequireAdminFilter());
    }
}
