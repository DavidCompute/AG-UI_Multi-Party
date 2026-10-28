using System.Collections.Concurrent;
using AguiGroupChat.Agents;
using AguiGroupChat.Hub.Models;
using Microsoft.Extensions.Logging;

namespace AguiGroupChat.Web;

/// <summary>
/// 产物质量 → 告警 / 自动隔离桥接（1.0.345）。
///
/// <para>
/// <see cref="ArtifactQualityStore"/> 每记录一次带产物契约的执行，这里判定「同一数字员工 + 技能
/// 最近 N 次里有 M 次低分」。命中后：
/// </para>
/// <list type="number">
///   <item>写一条 <see cref="ArtifactQualityAlert"/>（供前端通知中心轮询）；</item>
///   <item>经 <see cref="WebhookDispatcher"/> 推送 <c>ARTIFACT_QUALITY_ALERT</c>（复用订阅过滤 / HMAC / 重试）；</item>
///   <item>若开启 <c>Agents:ArtifactQuality:AutoSuspendEnabled</c>，暂停该技能并推送
///         <c>ARTIFACT_SKILL_SUSPENDED</c>。</item>
/// </list>
///
/// <para>
/// 只对<b>用户 / 编排创建</b>的技能（<c>OwnerId != null</c>）自动暂停：平台内置技能与配置种子
/// 由发布流程维护，自动停掉会直接砍掉核心交付能力；它们仍会告警。
/// </para>
/// </summary>
public static class ArtifactAlertBridge
{
    /// <summary>只看最近 24 小时的执行来判定“连续低分”，避免久远历史把技能一直判死。</summary>
    private const long LookbackMs = 24 * 60 * 60 * 1000;

    /// <summary>把产物质量记录回调接到告警 / 自动隔离。未注册对应服务（精简宿主）时静默跳过。</summary>
    public static void WireArtifactAlerts(this IServiceProvider services)
    {
        var quality = services.GetService(typeof(ArtifactQualityStore)) as ArtifactQualityStore;
        if (quality is null) return;

        var options = (services.GetService(typeof(AgentOptions)) as AgentOptions)?.ArtifactQuality
                      ?? new ArtifactQualityOptions();
        var skillCatalog = services.GetService(typeof(AgentSkillCatalog)) as AgentSkillCatalog;
        var agentCatalog = services.GetService(typeof(AgentCatalog)) as AgentCatalog;
        var alertStore = services.GetService(typeof(ArtifactAlertStore)) as ArtifactAlertStore;
        var lessons = services.GetService(typeof(SkillLessonStore)) as SkillLessonStore;
        var regressions = services.GetService(typeof(SkillRegressionCaseStore)) as SkillRegressionCaseStore;
        var dispatcher = services.GetService(typeof(WebhookDispatcher)) as WebhookDispatcher;
        var logger = (services.GetService(typeof(ILoggerFactory)) as ILoggerFactory)?.CreateLogger("ArtifactAlerts");

        // 冷却键：agentId|skillId（同技能被多岗位使用时，各自判定、各自告警）
        var cooldown = new ConcurrentDictionary<string, long>(StringComparer.Ordinal);

        quality.OnRecord = evt =>
        {
            try
            {
                if (string.IsNullOrWhiteSpace(evt.SkillId)) return;
                // 本次就是合格产物 → 无需判定（只有低分才可能触发告警）
                if (evt.Satisfied && evt.Score >= options.LowScoreThreshold) return;

                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var key = (evt.AgentId ?? "") + "|" + evt.SkillId;
                if (cooldown.TryGetValue(key, out var until) && now < until) return;

                var agentScope = string.IsNullOrWhiteSpace(evt.AgentId) ? null : evt.AgentId;
                var history = quality
                    .Query(now - LookbackMs, agentScope)
                    .Where(e => string.Equals(e.SkillId, evt.SkillId, StringComparison.Ordinal))
                    .ToList();
                var decision = ArtifactAlertPolicy.Evaluate(
                    history, options.LowScoreThreshold, options.WindowRuns, options.LowRunsToAlert);
                if (!decision.ShouldAlert) return;

                cooldown[key] = now + Math.Max(1, options.CooldownMinutes) * 60_000L;

                var skill = skillCatalog?.Get(evt.SkillId);
                var agentName = string.IsNullOrWhiteSpace(evt.AgentId)
                    ? ""
                    : (agentCatalog?.GetDefinition(evt.AgentId)?.Nickname ?? evt.AgentId);
                var skillName = string.IsNullOrWhiteSpace(skill?.Name) ? evt.SkillId : skill!.Name;
                var kind = skill?.Kind.ToString().ToLowerInvariant() ?? evt.Kind.ToLowerInvariant();

                var suspended = false;
                string? reason = null;
                if (options.AutoSuspendEnabled && skill is not null && !skill.Suspended && skill.OwnerId is not null)
                {
                    reason = $"产物质量连续 {decision.LowRuns}/{decision.Runs} 次未达标（阈值 {options.LowScoreThreshold}）";
                    skillCatalog!.Suspend(skill.SkillId, reason, now);
                    agentCatalog?.InvalidateAgentsUsingSkill(skill.SkillId);
                    suspended = true;
                }

                alertStore?.Add(new ArtifactQualityAlert(
                    evt.SkillId, skillName, evt.AgentId ?? "", agentName, kind,
                    decision.Runs, decision.LowRuns, Math.Clamp(evt.Score, 0, 100),
                    options.LowScoreThreshold, decision.Missing, suspended,
                    evt.GroupId ?? "", now));

                // 岗位经验（1.0.349）：把“为什么失败 / 下次怎么避免”沉淀下来，下次挂载该技能时注入工具说明。
                if (lessons is not null)
                {
                    lessons.Add(new SkillLesson(
                        "sl_" + Guid.NewGuid().ToString("N")[..12],
                        evt.SkillId,
                        string.IsNullOrWhiteSpace(evt.AgentId) ? null : evt.AgentId,
                        SkillLessonKinds.Failure,
                        $"连续 {decision.LowRuns}/{decision.Runs} 次产物未达标（最近一次 {Math.Clamp(evt.Score, 0, 100)} 分）",
                        decision.Missing,
                        suspended
                            ? "该技能已自动暂停；管理员在技能库点 🩺 修复后恢复"
                            : "调用前确认已提供完整正文 / 页数 / 数据，产出后检查交付文件是否齐全",
                        evt.GroupId ?? "", now, Resolved: false));
                    // 新经验要能被已在缓存里的智能体看见：失效引用该技能的缓存，下次触发时重建工具说明。
                    agentCatalog?.InvalidateAgentsUsingSkill(evt.SkillId);
                }

                // 回归用例（1.0.355）：真实失败触发一次标准样例重跑用例（幂等），防止修复后退化。
                if (regressions is not null && skill is not null)
                {
                    var probeKind = SkillArtifactContract.ExpectedKind(skill);
                    if (probeKind != SkillArtifactKind.None && SkillArtifactContract.SupportsGenerationProbe(probeKind))
                        regressions.Ensure(skill.SkillId, probeKind.ToString(), options.LowScoreThreshold);
                }

                dispatcher?.OnEvent(evt.GroupId ?? "", new ArtifactQualityAlertEvent
                {
                    SkillId = evt.SkillId,
                    SkillName = skillName,
                    AgentId = evt.AgentId ?? "",
                    AgentName = agentName,
                    Kind = kind,
                    Runs = decision.Runs,
                    LowRuns = decision.LowRuns,
                    Score = Math.Clamp(evt.Score, 0, 100),
                    Threshold = options.LowScoreThreshold,
                    Missing = decision.Missing,
                    Suspended = suspended,
                    GroupId = evt.GroupId ?? "",
                    Hint = suspended
                        ? "技能已被自动暂停，修复后请在技能库点「恢复」。"
                        : "可在技能库检查技能定义与产物契约，必要时手工暂停。",
                    Timestamp = now,
                });

                if (suspended && reason is not null)
                {
                    dispatcher?.OnEvent(evt.GroupId ?? "", new ArtifactSkillSuspendedEvent
                    {
                        SkillId = evt.SkillId,
                        SkillName = skillName,
                        Reason = reason,
                        AgentId = evt.AgentId ?? "",
                        GroupId = evt.GroupId ?? "",
                        Timestamp = now,
                    });
                }

                logger?.LogWarning(
                    "产物质量告警：skill={SkillId} agent={AgentId} low={Low}/{Runs} avg={Avg} suspended={Suspended}",
                    evt.SkillId, evt.AgentId, decision.LowRuns, decision.Runs, decision.AvgScore, suspended);
            }
            catch (Exception ex)
            {
                // 告警是增强：绝不影响技能执行与产物回收
                logger?.LogDebug(ex, "产物质量告警处理失败（忽略）");
            }
        };
    }
}
