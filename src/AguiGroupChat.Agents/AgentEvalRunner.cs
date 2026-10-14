using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;

namespace AguiGroupChat.Agents;

/// <summary>一条用例的判定结果。</summary>
public sealed record EvalScore(bool Passed, IReadOnlyList<string> Matched, IReadOnlyList<string> Missed);

/// <summary>一次用例运行的结果（不落库，跑分即时返回）。</summary>
public sealed record EvalRunItem(
    string CaseId,
    string Name,
    string AgentId,
    bool Passed,
    string Reply,
    string Error,
    IReadOnlyList<string> Matched,
    IReadOnlyList<string> Missed,
    long DurationMs);

/// <summary>
/// 数字员工评测运行器：对每条用例，用目标数字员工的「提示词 + 正式回复模型」跑一次（不带工具 / 记忆），
/// 再按「期望要点」判定通过与否。用于改提示词 / 换模型后的回归对照。
/// </summary>
public sealed class AgentEvalRunner(AgentCatalog catalog, ILogger<AgentEvalRunner> logger)
{
    private const int ReplyExcerpt = 1200;

    public async Task<EvalRunItem> RunAsync(EvalCase c, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var agent = catalog.CreateForEval(c.AgentId);
            var resp = await agent.RunAsync(c.Prompt ?? "", session: null, new ChatClientAgentRunOptions(), ct)
                .ConfigureAwait(false);
            var reply = (resp?.Text ?? "").Trim();
            var score = Score(reply, c.Expected, c.MatchMode);
            sw.Stop();
            if (!score.Passed)
                logger.LogInformation("评测未通过：{Case}（agent={Agent}）", c.Name, c.AgentId);
            return new EvalRunItem(c.CaseId, c.Name, c.AgentId, score.Passed, Excerpt(reply), "",
                score.Matched, score.Missed, sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            logger.LogWarning(ex, "评测运行失败：{Case}（agent={Agent}）", c.Name, c.AgentId);
            return new EvalRunItem(c.CaseId, c.Name, c.AgentId, false, "", ex.Message,
                [], c.Expected ?? [], sw.ElapsedMilliseconds);
        }
    }

    /// <summary>判定：回复（大小写不敏感）是否包含期望要点。expected 为空时以「回复非空」为通过。
    /// 纯函数，便于单测。</summary>
    public static EvalScore Score(string reply, IReadOnlyList<string>? expected, string? matchMode)
    {
        var text = reply ?? "";
        var exp = (expected ?? [])
            .Where(e => !string.IsNullOrWhiteSpace(e))
            .Select(e => e.Trim())
            .ToList();
        if (exp.Count == 0)
            return new EvalScore(!string.IsNullOrWhiteSpace(text), [], []);

        var matched = exp.Where(e => text.Contains(e, StringComparison.OrdinalIgnoreCase)).ToList();
        var missed = exp.Where(e => !matched.Contains(e, StringComparer.OrdinalIgnoreCase)).ToList();
        var all = string.Equals(matchMode, "all", StringComparison.OrdinalIgnoreCase);
        var passed = all ? missed.Count == 0 : matched.Count > 0;
        return new EvalScore(passed, matched, missed);
    }

    private static string Excerpt(string s) => s.Length <= ReplyExcerpt ? s : s[..ReplyExcerpt] + "…";
}
