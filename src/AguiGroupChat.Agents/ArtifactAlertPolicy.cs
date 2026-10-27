namespace AguiGroupChat.Agents;

/// <summary>
/// 一次「连续低分」判定的结果（纯数据，便于单测与日志）。
/// </summary>
/// <param name="ShouldAlert">最近窗口内低分次数是否达到告警门槛。</param>
/// <param name="Runs">窗口内实际参与判定的执行次数（不足窗口时按实际条数）。</param>
/// <param name="LowRuns">窗口内低分次数。</param>
/// <param name="AvgScore">窗口内平均分（0~100，一位小数）。</param>
/// <param name="Missing">窗口内低分事件里出现过的缺失项（去重，最多 5 条）。</param>
public sealed record ArtifactAlertDecision(
    bool ShouldAlert,
    int Runs,
    int LowRuns,
    double AvgScore,
    string[] Missing);

/// <summary>
/// 产物质量告警判定（1.0.345，<b>纯函数</b>）。
///
/// <para>
/// 判定口径：同一「数字员工 + 技能」最近 <c>windowRuns</c> 次带产物契约的执行中，
/// 低分（<c>!Satisfied</c> 或 <c>Score &lt; threshold</c>）次数 ≥ <c>lowRunsToAlert</c> 时告警。
/// 单次偶发失败不告警，避免把模型偶发的空稿当技能缺陷；连续两次才说明技能本身有问题。
/// </para>
/// </summary>
public static class ArtifactAlertPolicy
{
    /// <summary>默认低分阈值（与绩效看板「🧪 产物质量」口径一致）。</summary>
    public const int DefaultLowScoreThreshold = 70;

    public static ArtifactAlertDecision Evaluate(
        IReadOnlyList<ArtifactQualityEvent> history,
        int lowScoreThreshold = DefaultLowScoreThreshold,
        int windowRuns = 3,
        int lowRunsToAlert = 2)
    {
        var window = Math.Max(1, windowRuns);
        var need = Math.Clamp(lowRunsToAlert, 1, window);
        var recent = (history ?? [])
            .OrderBy(e => e.TimestampMs)
            .TakeLast(window)
            .ToList();
        if (recent.Count == 0)
            return new ArtifactAlertDecision(false, 0, 0, 0, []);

        bool IsLow(ArtifactQualityEvent e) => !e.Satisfied || e.Score < lowScoreThreshold;
        var low = recent.Count(IsLow);
        var avg = Math.Round(recent.Average(e => Math.Clamp(e.Score, 0, 100)), 1);
        var missing = recent.Where(IsLow)
            .SelectMany(e => e.Missing ?? [])
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Distinct(StringComparer.Ordinal)
            .Take(5)
            .ToArray();
        return new ArtifactAlertDecision(low >= need, recent.Count, low, avg, missing);
    }
}

/// <summary>
/// 一条产物质量告警（1.0.345）：既经出站 Webhook 推送，也留在内存里供
/// 前端通知中心轮询（服务重启即清空 —— 告警是「近期需要看一眼」的运营信号，不做长期留档）。
/// </summary>
public sealed record ArtifactQualityAlert(
    string SkillId,
    string SkillName,
    string AgentId,
    string AgentName,
    string Kind,
    int Runs,
    int LowRuns,
    int Score,
    int Threshold,
    string[] Missing,
    bool Suspended,
    string GroupId,
    long TimestampMs);

/// <summary>
/// 产物质量告警存储（1.0.345）：有界内存（默认 200 条），供
/// <c>GET /ag-ui/admin/artifact-quality/alerts</c> 与通知中心使用。
/// </summary>
public sealed class ArtifactAlertStore
{
    /// <summary>保留的最近告警上限。</summary>
    public const int MaxAlerts = 200;

    private readonly object _gate = new();
    private readonly List<ArtifactQualityAlert> _alerts = new();

    public void Add(ArtifactQualityAlert alert)
    {
        if (string.IsNullOrWhiteSpace(alert.SkillId)) return;
        lock (_gate)
        {
            _alerts.Add(alert);
            if (_alerts.Count > MaxAlerts)
                _alerts.RemoveRange(0, _alerts.Count - MaxAlerts);
        }
    }

    public IReadOnlyList<ArtifactQualityAlert> Snapshot(long sinceMs = 0)
    {
        lock (_gate)
            return _alerts.Where(a => a.TimestampMs >= sinceMs).ToArray();
    }
}
