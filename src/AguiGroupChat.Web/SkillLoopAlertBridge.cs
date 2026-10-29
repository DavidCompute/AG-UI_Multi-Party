using System.Collections.Concurrent;
using AguiGroupChat.Agents;
using AguiGroupChat.Hub.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AguiGroupChat.Web;

/// <summary>
/// 把闭环事件接给告警桥（1.0.360）：人工修复与自动修复都经此入口，不必互相知道对方。
/// </summary>
public sealed class SkillLoopAlertObserver : ISkillLoopObserver
{
    private readonly IServiceProvider _services;

    public SkillLoopAlertObserver(IServiceProvider services) => _services = services;

    public void OnOutcomeRecorded(string skillId, string outcome)
        => SkillLoopAlertBridge.OnOutcome(_services, skillId, outcome);
}

/// <summary>Web 侧注册（1.0.360）：Program.cs 与 DesktopApp.cs 各调一次（组合根漂移守卫）。</summary>
public static class SkillLoopAlertRegistration
{
    public static IServiceCollection RegisterSkillLoopAlerts(this IServiceCollection services)
    {
        services.AddSingleton<ISkillLoopObserver, SkillLoopAlertObserver>();
        return services;
    }
}

/// <summary>
/// 自愈闭环 → 告警 / 回归用例补充桥接（1.0.359）。
///
/// <para>
/// 每次 <c>repair?apply=true</c> 记录结局后调用：按 <see cref="SkillLoopAlertPolicy"/> 判定该技能最近
/// 的修复是否「反复失败 / 反复回滚 / 成功率偏低」。命中后：
/// </para>
/// <list type="number">
///   <item>若该技能是可生成类产物技能且还没有回归用例，补一条标准样例用例（与 1.0.355 同口径）；</item>
///   <item>写一条 <see cref="SkillLoopAlert"/>（供前端通知中心轮询）；</item>
///   <item>经 <see cref="WebhookDispatcher"/> 推送 <c>SKILL_LOOP_ALERT</c>。</item>
/// </list>
///
/// <para>
/// 不自动暂停技能：修复失败 / 回滚本身已保持暂停，修复成功后再暂停没有意义；
/// 告警只负责把「这个技能需要人看」这件事送出去。
/// </para>
/// </summary>
public static class SkillLoopAlertBridge
{
    /// <summary>判定窗口：最近 7 天的闭环事件（修复是低频动作，窗口按时间而非计数）。</summary>
    private const long LookbackMs = 7 * 24 * 60 * 60 * 1000;

    /// <summary>同一技能两次告警的最小间隔，避免连续修复刷屏。</summary>
    private const long CooldownMs = 60 * 60 * 1000;

    private static readonly ConcurrentDictionary<string, long> Cooldown = new(StringComparer.Ordinal);

    /// <summary>记录完一条结局事件后调用。未注册对应服务（精简宿主）时静默跳过。</summary>
    public static void OnOutcome(IServiceProvider services, string skillId, string outcome)
    {
        var logger = (services.GetService(typeof(ILoggerFactory)) as ILoggerFactory)
            ?.CreateLogger("SkillLoopAlerts");
        try
        {
            if (string.IsNullOrWhiteSpace(skillId)) return;
            // 跳过不是一次真实尝试：既不进判定，也不该因为修复入口被调用而告警。
            if (outcome == SkillLoopOutcomes.Skipped) return;

            var store = services.GetService(typeof(SkillLoopEventStore)) as SkillLoopEventStore;
            if (store is null) return;

            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (Cooldown.TryGetValue(skillId, out var until) && now < until) return;

            var history = store.Query(now - LookbackMs)
                .Where(e => string.Equals(e.SkillId, skillId, StringComparison.Ordinal))
                .ToList();
            var decision = SkillLoopAlertPolicy.Evaluate(history);
            if (!decision.ShouldAlert) return;

            Cooldown[skillId] = now + CooldownMs;

            var skillCatalog = services.GetService(typeof(AgentSkillCatalog)) as AgentSkillCatalog;
            var skill = skillCatalog?.Get(skillId);
            var skillName = string.IsNullOrWhiteSpace(skill?.Name) ? skillId : skill!.Name;

            // 补充回归用例：修复反复失败时，最需要的是「修好后别再退化」的守门用例。
            var ensured = false;
            var regressions = services.GetService(typeof(SkillRegressionCaseStore)) as SkillRegressionCaseStore;
            if (regressions is not null && skill is not null)
            {
                var probeKind = SkillArtifactContract.ExpectedKind(skill);
                if (probeKind != SkillArtifactKind.None && SkillArtifactContract.SupportsGenerationProbe(probeKind))
                    ensured = regressions.Ensure(
                        skill.SkillId, probeKind.ToString(), ArtifactAlertPolicy.DefaultLowScoreThreshold) is not null;
            }

            var alerts = services.GetService(typeof(SkillLoopAlertStore)) as SkillLoopAlertStore;
            alerts?.Add(new SkillLoopAlert(
                skillId, skillName, decision.Attempts, decision.Successes, decision.RolledBack, decision.Failed,
                decision.SuccessRate, decision.AvgMs, decision.Reasons, ensured, now));

            // 自动修复（1.0.360）：告警之后先把最便宜的一步做掉——后台再试一次修复。
            // 只对非内置、服务端可自测的技能排队；真正的闸门在工作线程里（冷却 + 每日上限）。
            var options = (services.GetService(typeof(AgentOptions)) as AgentOptions)?.ArtifactQuality;
            if (options is { AutoRepairEnabled: true }
                && skill is { OwnerId: not null }
                && skill.ExecutionLocation != AgentSkillExecutionLocation.Client
                && SkillSuspension.MayAutoRepair(skill))
            {
                (services.GetService(typeof(SkillAutoRepairQueue)) as SkillAutoRepairQueue)
                    ?.TryEnqueue(skillId, string.Join(",", decision.Reasons));
            }

            var dispatcher = services.GetService(typeof(WebhookDispatcher)) as WebhookDispatcher;
            dispatcher?.OnEvent("", new SkillLoopAlertEvent
            {
                SkillId = skillId,
                SkillName = skillName,
                Attempts = decision.Attempts,
                Successes = decision.Successes,
                RolledBack = decision.RolledBack,
                Failed = decision.Failed,
                SuccessRate = decision.SuccessRate,
                AvgMs = decision.AvgMs,
                Reasons = decision.Reasons,
                RegressionEnsured = ensured,
                Hint = "可在技能库查看「💡 经验 / 🧪 回归用例」，必要时手工调整技能定义或产物契约。",
                Timestamp = now,
            });

            logger?.LogWarning(
                "自愈闭环告警：skill={SkillId} attempts={Attempts} ok={Successes} rollback={RolledBack} failed={Failed} rate={Rate} reasons={Reasons}",
                skillId, decision.Attempts, decision.Successes, decision.RolledBack, decision.Failed,
                decision.SuccessRate, string.Join(",", decision.Reasons));
        }
        catch (Exception ex)
        {
            // 告警是增强：绝不影响修复本身
            logger?.LogDebug(ex, "自愈闭环告警处理失败（忽略）");
        }
    }
}
