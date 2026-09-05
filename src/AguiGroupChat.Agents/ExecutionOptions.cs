using Microsoft.Extensions.Logging;

namespace AguiGroupChat.Agents;

/// <summary>
/// 网关执行期及时序 / 重试 / TTL 的可配置覆盖（appsettings 的 <c>Agents:Execution</c> 节点）。
/// 默认值与 AgentGateway 原生时序常量一致；平台运营者可在不改代码的前提下按环境覆盖，
/// 超时（分钟）仍统一用分钟表达并按需 <c>TimeSpan.FromMinutes</c> 换算。
/// </summary>
public sealed class ExecutionOptions
{
    /// <summary>单次模型 / 桥接流式调用的最长运行时间（分钟）：模型挂起时防止 Task 永久占用。</summary>
    public int StreamTimeoutMinutes { get; set; } = 5;

    /// <summary>本地模型流式调用失败时的重试次数上限（可重试 429 / 5xx / 连接重置）。</summary>
    public int MaxModelAttempts { get; set; } = 2;

    /// <summary>待决策人机交互的超时（分钟）：超时由周期定时器清理。</summary>
    public int InteractionTtlMinutes { get; set; } = 10;

    /// <summary>会话锁超时未用自动清理阈值（分钟）。</summary>
    public int SessionLockTtlMinutes { get; set; } = 30;

    /// <summary>「已批准客户端技能」记忆的过期时长（分钟）。</summary>
    public int ApprovedSkillTtlMinutes { get; set; } = 30;

    /// <summary>会话锁内存表条目数上限（超限时即时清理超时锁的兜底阈值）。</summary>
    public int SessionLockMaxEntries { get; set; } = 512;

    /// <summary>确定性编排计划最多纳入清单的下游员工 / 技能项数。</summary>
    public int CoordinatorPlanMaxItems { get; set; } = 12;

    /// <summary>确定性编排计划单次最多步骤数。</summary>
    public int CoordinatorPlanMaxSteps { get; set; } = 8;

    /// <summary>递归综合补查最多轮次（防死循环 / 打爆时长）。</summary>
    public int MaxRecursiveRounds { get; set; } = 5;

    /// <summary>指派 / 提升路由的最大层数（防配置病态深链）。</summary>
    public int MaxRouteDepth { get; set; } = 4;

    /// <summary>同一消息最多允许的审批轮数（防外部服务异常导致反复中断的死循环）。</summary>
    public int MaxInteractionRounds { get; set; } = 5;

    /// <summary>出厂默认（与无关代码路径紧密绑定，回退必用同一值保证行为一致）。</summary>
    public static ExecutionOptions Default { get; } = new();

    /// <summary>
    /// 规范化并夹紧：把每个 ≤0 的非法（正）值回退到 <see cref="Default"/>（含整数语义的 0 上限本就不应出现），
    /// 并以 <paramref name="logger"/> 记录 warn——绝不把废值带到运行时各处跑。
    /// </summary>
    public ExecutionOptions Normalize(ILogger? logger = null)
    {
        // 逐个成员夹紧：非法（≤0，非 0 上限正整数）一律回退默认，避免范围崩溃 / 死循环等病态行为。
        StreamTimeoutMinutes = Positive(StreamTimeoutMinutes, Default.StreamTimeoutMinutes, nameof(StreamTimeoutMinutes), logger);
        MaxModelAttempts = Positive(MaxModelAttempts, Default.MaxModelAttempts, nameof(MaxModelAttempts), logger);
        InteractionTtlMinutes = Positive(InteractionTtlMinutes, Default.InteractionTtlMinutes, nameof(InteractionTtlMinutes), logger);
        SessionLockTtlMinutes = Positive(SessionLockTtlMinutes, Default.SessionLockTtlMinutes, nameof(SessionLockTtlMinutes), logger);
        ApprovedSkillTtlMinutes = Positive(ApprovedSkillTtlMinutes, Default.ApprovedSkillTtlMinutes, nameof(ApprovedSkillTtlMinutes), logger);
        SessionLockMaxEntries = Positive(SessionLockMaxEntries, Default.SessionLockMaxEntries, nameof(SessionLockMaxEntries), logger);
        CoordinatorPlanMaxItems = Positive(CoordinatorPlanMaxItems, Default.CoordinatorPlanMaxItems, nameof(CoordinatorPlanMaxItems), logger);
        CoordinatorPlanMaxSteps = Positive(CoordinatorPlanMaxSteps, Default.CoordinatorPlanMaxSteps, nameof(CoordinatorPlanMaxSteps), logger);
        MaxRecursiveRounds = Positive(MaxRecursiveRounds, Default.MaxRecursiveRounds, nameof(MaxRecursiveRounds), logger);
        MaxRouteDepth = Positive(MaxRouteDepth, Default.MaxRouteDepth, nameof(MaxRouteDepth), logger);
        MaxInteractionRounds = Positive(MaxInteractionRounds, Default.MaxInteractionRounds, nameof(MaxInteractionRounds), logger);
        return this;
    }

    /// <summary>夹紧单个 >0 正整数：非法（≤0）则回退默认值并按需告警。</summary>
    private static int Positive(int value, int fallback, string name, ILogger? logger)
    {
        if (value > 0) return value;
        logger?.LogWarning(
            "Agents:Execution:{Name} 配置非法（{Value}），已回退默认值 {Fallback}",
            name, value, fallback);
        return fallback;
    }
}
