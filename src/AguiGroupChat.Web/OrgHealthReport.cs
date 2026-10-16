using System.Globalization;
using System.Text;
using AguiGroupChat.Agents;

namespace AguiGroupChat.Web;

/// <summary>
/// 组织体检报告（管理员导出）：把「员工绩效（调用 / token / 成本 / 好评率 / 关注标记）」与
/// 「组织连接（指派 / 提升 / 流水线）」汇成一份可导出的 Markdown 报告，便于运营复盘与汇报。
/// 纯函数，便于单测；数据全部来自既有端点 / 目录，不新增存储。
/// </summary>
public static class OrgHealthReport
{
    public static string BuildMarkdown(
        int days,
        string from,
        string to,
        IReadOnlyList<AgentPerformance.AgentStat> stats,
        IReadOnlyDictionary<string, AgentDefinition> defs,
        PricingOptions pricing,
        long nowMs,
        IReadOnlyList<EvalCase>? evalCases = null,
        IReadOnlyList<AgentPerformance.AgentStat>? prevStats = null)
    {
        var sb = new StringBuilder();
        var cur = string.IsNullOrWhiteSpace(pricing.Currency) ? "$" : pricing.Currency;
        var priced = pricing.Configured;

        string Name(string id) => defs.TryGetValue(id, out var d) && !string.IsNullOrWhiteSpace(d.Nickname) ? d.Nickname! : id;
        static string Pct(double? r) => r is { } v ? Math.Round(v * 100) + "%" : "—";
        string Cost(long p, long c, long r) => priced
            ? cur + pricing.EstimateCost(p, c, r).ToString("0.00", CultureInfo.InvariantCulture)
            : "—";
        static string N0(long n) => n.ToString("N0", CultureInfo.InvariantCulture);
        static string Delta(double? curIn, double? prevIn)
        {
            if (curIn is not { } cur || prevIn is not { } prev) return "—";
            if (prev == 0) return cur == 0 ? "持平" : "新增";
            var pct = (cur - prev) / prev;
            var arrow = pct > 0.005 ? "▲" : pct < -0.005 ? "▼" : "≈";
            return arrow + Math.Round(Math.Abs(pct) * 100) + "%";
        }

        var totalCalls = stats.Sum(s => s.Calls);
        var totalTokens = stats.Sum(s => s.TotalTokens);
        var up = stats.Sum(s => s.Up);
        var down = stats.Sum(s => s.Down);
        var active = stats.Count(s => s.Calls > 0);
        var totalCost = pricing.EstimateCost(stats.Sum(s => s.PromptTokens), stats.Sum(s => s.CompletionTokens), stats.Sum(s => s.ReasoningTokens));
        var flagged = stats.Where(s => s.Flags.Count > 0).OrderByDescending(s => s.Flags.Count).ThenBy(s => s.Name, StringComparer.CurrentCulture).ToList();
        var lowPraise = stats.Count(s => s.Flags.Contains("low-praise"));
        var idle = stats.Count(s => s.Flags.Contains("idle"));
        var assignmentEdges = defs.Values.Sum(d => d.AssignmentIds?.Count ?? 0);
        var escalationEdges = defs.Values.Count(d => !string.IsNullOrWhiteSpace(d.EscalationAgentId));
        var pipelineSteps = defs.Values.Where(d => d.Pipeline is { Count: > 0 }).Sum(d => d.Pipeline!.Count);

        sb.AppendLine("# 组织健康报告 · KnowGath");
        sb.AppendLine();
        sb.AppendLine($"- 生成时间：{DateTimeOffset.FromUnixTimeMilliseconds(nowMs).ToLocalTime():yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"- 统计窗口：{from} ~ {to}（{days} 天）");
        sb.AppendLine();
        sb.AppendLine("## 一、总览");
        sb.AppendLine($"- 数字员工：{stats.Count} 位（窗口内有调用 {active} 位）");
        sb.AppendLine($"- 调用次数：{N0(totalCalls)}；Token：{N0(totalTokens)}；成本：{(priced ? cur + totalCost.ToString("0.00", CultureInfo.InvariantCulture) : "未配置单价（Agents:Pricing）")}");
        sb.AppendLine($"- 综合好评率：{Pct((up + down) == 0 ? null : (double)up / (up + down))}（👍 {up} / 👎 {down}）");
        if (prevStats is not null)
        {
            // 环比：与紧邻的上一同长度窗口对比（涨跌箭头 + 百分比）
            var pCalls = prevStats.Sum(s => s.Calls);
            var pTokens = prevStats.Sum(s => s.TotalTokens);
            var pUp = prevStats.Sum(s => s.Up);
            var pDown = prevStats.Sum(s => s.Down);
            var pCost = pricing.EstimateCost(prevStats.Sum(s => s.PromptTokens), prevStats.Sum(s => s.CompletionTokens), prevStats.Sum(s => s.ReasoningTokens));
            double? curRate = (up + down) == 0 ? null : (double)up / (up + down);
            double? prevRate = (pUp + pDown) == 0 ? null : (double)pUp / (pUp + pDown);
            sb.AppendLine($"- 环比上一 {days} 天：调用 {Delta(totalCalls, pCalls)}；Token {Delta(totalTokens, pTokens)}；成本 {(priced ? Delta((double)totalCost, (double)pCost) : "—")}；好评率 {Delta(curRate, prevRate)}");
        }
        sb.AppendLine($"- 组织连接：指派 {assignmentEdges} 条 / 提升 {escalationEdges} 条 / 流水线 {pipelineSteps} 步");
        sb.AppendLine();
        sb.AppendLine($"## 二、需要关注（{flagged.Count}）");
        if (flagged.Count == 0)
        {
            sb.AppendLine("- 暂无");
        }
        else
        {
            foreach (var s in flagged)
            {
                var reasons = s.Flags.Select(f => f switch
                {
                    "low-praise" => $"好评率偏低（{Pct(s.PositiveRate)}，👍 {s.Up} / 👎 {s.Down}）",
                    "idle" => $"近 {days} 天无调用",
                    _ => f,
                });
                sb.AppendLine($"- 「{s.Name}」：{string.Join("；", reasons)}");
            }
        }
        sb.AppendLine();
        sb.AppendLine("## 三、数字员工绩效明细");
        sb.AppendLine();
        sb.AppendLine("| 数字员工 | 调用 | Token | 成本 | 👍 | 👎 | 好评率 | 最近活跃 | 关注 |");
        sb.AppendLine("| --- | ---: | ---: | ---: | ---: | ---: | ---: | --- | --- |");
        foreach (var s in stats.OrderByDescending(x => x.Calls).ThenBy(x => x.Name, StringComparer.CurrentCulture))
            sb.AppendLine($"| {s.Name} | {N0(s.Calls)} | {N0(s.TotalTokens)} | {Cost(s.PromptTokens, s.CompletionTokens, s.ReasoningTokens)} | {s.Up} | {s.Down} | {Pct(s.PositiveRate)} | {s.LastDate ?? "—"} | {(s.Flags.Count == 0 ? "—" : string.Join(" / ", s.Flags))} |");
        sb.AppendLine();
        sb.AppendLine("## 四、组织架构（指派 / 提升 / 流水线）");
        var anyRel = false;
        foreach (var d in defs.Values.OrderBy(x => x.Nickname ?? x.AgentId, StringComparer.CurrentCulture))
        {
            var parts = new List<string>();
            if (d.AssignmentIds is { Count: > 0 }) parts.Add("指派 → " + string.Join(", ", d.AssignmentIds.Select(Name)));
            if (!string.IsNullOrWhiteSpace(d.EscalationAgentId)) parts.Add("提升 → " + Name(d.EscalationAgentId!));
            if (d.Pipeline is { Count: > 0 }) parts.Add("流水线 → " + string.Join(" → ", d.Pipeline!.Select(p => Name(p.StepAgentId))));
            if (parts.Count == 0) continue;
            anyRel = true;
            sb.AppendLine($"- 「{d.Nickname ?? d.AgentId}」：{string.Join("；", parts)}");
        }
        if (!anyRel) sb.AppendLine("- 未配置指派 / 提升 / 流水线");
        sb.AppendLine();
        sb.AppendLine("## 五、建议");
        var advice = new List<string>();
        if (lowPraise > 0) advice.Add($"有 {lowPraise} 位数字员工好评率偏低，建议复核其提示词或技能配置（可用「评测回归」建金标准用例对照）。");
        if (idle > 0) advice.Add($"有 {idle} 位数字员工近 {days} 天无调用，建议归档，或检查其触发关键词 / 可见范围是否过窄。");
        if (priced && totalCalls > 0)
        {
            var top = stats.OrderByDescending(s => pricing.EstimateCost(s.PromptTokens, s.CompletionTokens, s.ReasoningTokens)).FirstOrDefault();
            if (top is not null && top.Calls > 0)
                advice.Add($"成本最高的是「{top.Name}」（{Cost(top.PromptTokens, top.CompletionTokens, top.ReasoningTokens)}）——可评估启用按复杂度模型路由（Agents:ModelRouting）或压缩上下文。");
        }
        if (advice.Count == 0) advice.Add("组织运行健康，无需特别处理。");
        foreach (var a in advice) sb.AppendLine($"- {a}");
        sb.AppendLine();

        // 评测回归（持久化基线）：让报告能带「回归通过率」
        if (evalCases is not null)
        {
            var run = evalCases.Where(c => c.LastRunAtMs > 0).ToList();
            var passedN = run.Count(c => c.LastPassed == true);
            sb.AppendLine("## 六、评测回归");
            if (evalCases.Count == 0)
            {
                sb.AppendLine("- 暂无评测用例（可在控制台「评测回归」创建）");
            }
            else
            {
                var rate = run.Count == 0 ? "—" : Math.Round((double)passedN / run.Count * 100) + "%";
                sb.AppendLine($"- 用例：{evalCases.Count} 条；已跑过：{run.Count} 条（通过 {passedN}，通过率 {rate}）");
                sb.AppendLine();
                sb.AppendLine("| 用例 | 数字员工 | 结果 | 最近跑分 |");
                sb.AppendLine("| --- | --- | --- | --- |");
                foreach (var c in evalCases.OrderByDescending(c => c.LastRunAtMs))
                {
                    var result = c.LastRunAtMs == 0 ? "未跑过" : (c.LastPassed == true ? "通过" : "未通过");
                    var at = c.LastRunAtMs == 0 ? "—" : DateTimeOffset.FromUnixTimeMilliseconds(c.LastRunAtMs).ToLocalTime().ToString("yyyy-MM-dd HH:mm");
                    sb.AppendLine($"| {c.Name} | {Name(c.AgentId)} | {result} | {at} |");
                }
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }
}
