using System.Collections.Concurrent;

namespace AguiGroupChat.Agents;

/// <summary>
/// 一条数字员工评测用例（金标准）：向目标数字员工提问，按「期望要点」（判定关键词）是否出现判定通过。
/// 用于改提示词 / 换模型后一键跑分，防止能力退化。判定方式：<see cref="MatchMode"/> = any（命中任一即通过）/ all（全部命中）。
/// </summary>
public sealed class EvalCase
{
    public required string CaseId { get; init; }

    /// <summary>用例名称（便于识别）。</summary>
    public string Name { get; set; } = "";

    /// <summary>被测数字员工。</summary>
    public required string AgentId { get; set; }

    /// <summary>提问内容。</summary>
    public string Prompt { get; set; } = "";

    /// <summary>期望要点（判定关键词，回复中应出现其一 / 全部）。</summary>
    public string[] Expected { get; set; } = [];

    /// <summary>判定模式：<c>any</c>（默认，命中任一即通过）/ <c>all</c>（全部命中才通过）。</summary>
    public string MatchMode { get; set; } = "any";

    /// <summary>
    /// 期望交付格式（可空）：<c>docx</c> / <c>pptx</c> / <c>xlsx</c> / <c>pdf</c> / <c>text</c> / <c>image</c>。
    /// 设置后，除「期望要点」外还要求回复中**体现该格式**（同义词命中）。
    /// 注意：评测跑的是裸模型（不带工具 / 技能），这里校验的是「格式意图」而非真实产出文件
    /// ——真实文件生成由平台的技能回归与端到端用例覆盖。
    /// </summary>
    public string ExpectDeliverable { get; set; } = "";

    /// <summary>是否参与「跑全部」。</summary>
    public bool Enabled { get; set; } = true;

    public long CreatedAtMs { get; set; }
    public string CreatedBy { get; set; } = "";

    /// <summary>最近一次跑分的通过与否（null = 从未跑过）；随用例一起持久化，供报告与页签回显，也让报告能带「回归通过率」。</summary>
    public bool? LastPassed { get; set; }

    /// <summary>最近一次跑分时间（ms；0 = 未跑过）。</summary>
    public long LastRunAtMs { get; set; }

    /// <summary>最近一次跑分的回复摘录（截断保存，控体积）。</summary>
    public string LastReply { get; set; } = "";

    /// <summary>最近一次跑分的错误（若有）。</summary>
    public string LastError { get; set; } = "";
}

/// <summary>一次跑分的历史记录（用于「评分趋势」折线）。</summary>
public sealed record EvalRunRecord(long AtMs, int Total, int Passed);

/// <summary>
/// 评测用例服务：内存持有 + 扩展区持久化（重启后不丢）。运行结果不落库（每次跑分即时返回）。
/// </summary>
public sealed class EvalCaseStore
{
    private const int MaxRuns = 200;
    private readonly ConcurrentDictionary<string, EvalCase> _cases = new(StringComparer.Ordinal);
    private readonly object _runGate = new();
    private readonly List<EvalRunRecord> _runs = [];

    public IReadOnlyList<EvalCase> List() => _cases.Values.OrderBy(c => c.CreatedAtMs).ToList();

    public EvalCase? Get(string caseId) => _cases.TryGetValue(caseId, out var c) ? c : null;

    public void Put(EvalCase c)
    {
        if (string.IsNullOrWhiteSpace(c.CaseId)) return;
        _cases[c.CaseId] = c;
    }

    public bool Remove(string caseId) => _cases.TryRemove(caseId, out _);

    public IReadOnlyList<EvalCase> Snapshot() => _cases.Values.ToList();

    public void Restore(IEnumerable<EvalCase> cases)
    {
        _cases.Clear();
        foreach (var c in cases)
        {
            if (string.IsNullOrWhiteSpace(c.CaseId)) continue;
            _cases[c.CaseId] = c;
        }
    }

    /// <summary>记录一次跑分（保留最近 <see cref="MaxRuns"/> 次）——用于「评分趋势」。</summary>
    public void RecordRun(long atMs, int total, int passed)
    {
        lock (_runGate)
        {
            _runs.Add(new EvalRunRecord(atMs, total, passed));
            if (_runs.Count > MaxRuns) _runs.RemoveRange(0, _runs.Count - MaxRuns);
        }
    }

    /// <summary>跑分历史（按时间升序）。</summary>
    public IReadOnlyList<EvalRunRecord> Runs()
    {
        lock (_runGate) return _runs.ToList();
    }

    public void RestoreRuns(IEnumerable<EvalRunRecord> runs)
    {
        lock (_runGate)
        {
            _runs.Clear();
            foreach (var r in runs) _runs.Add(r);
            if (_runs.Count > MaxRuns) _runs.RemoveRange(0, _runs.Count - MaxRuns);
        }
    }
}
