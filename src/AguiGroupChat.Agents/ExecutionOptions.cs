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

    /// <summary>合法阶段 token 白名单（ExecutionOrder 只允许这些；语义见各入口方法注释）。</summary>
    private static readonly string[] LegalTokens = [StageBridge, StagePipeline, StageRelay, StageOrgRoute, StageStreaming];

    private const string StageBridge = "bridge";
    private const string StagePipeline = "pipeline";
    private const string StageRelay = "relay";
    private const string StageOrgRoute = "org_route";
    private const string StageStreaming = "streaming";

    /// <summary>
    /// 执行阶段分派顺序（网关 InvokeCoreAsync 的路由判定次序）。只允许白名单 token
    /// （bridge / pipeline / relay / org_route / streaming）；规范化时剔除未知 token、去重、保留相对顺序，
    /// 且恒含 streaming 并置最末（普通流式兜底）。缺省与既有硬编码 if 顺序完全一致。
    /// </summary>
    public string[] ExecutionOrder { get; set; } =
        [StageBridge, StagePipeline, StageRelay, StageOrgRoute, StageStreaming];

    /// <summary>平台级执行阶段开关（默认全开）。与角色的 <see cref="AgentDefinition.DisableBridge"/> 等
    /// 角色级覆盖求并：任一侧关闭 ⇒ 跳过该阶段。</summary>
    public bool EnableBridge { get; set; } = true;

    /// <summary>平台级执行阶段开关（默认全开）。见 <see cref="EnableBridge"/>。</summary>
    public bool EnablePipeline { get; set; } = true;

    /// <summary>平台级执行阶段开关（默认全开）。见 <see cref="EnableBridge"/>。</summary>
    public bool EnableRelay { get; set; } = true;

    /// <summary>平台级执行阶段开关（默认全开；org_route 还须语义满足 Mentioned&amp;&amp;指派/提升/协调）。见 <see cref="EnableBridge"/>。</summary>
    public bool EnableOrgRoute { get; set; } = true;

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
        ExecutionOrder = NormalizeExecutionOrder(ExecutionOrder, Default.ExecutionOrder, logger);
        return this;
    }

    /// <summary>
    /// 规范化执行阶段顺序：按白名单过滤（剔除未知 / 空 token）、去重且保留相对顺序、恒置 streaming 最末作兜底；
    /// 拼错 / 重复 / 整表非法（过滤后无任何合法非流式阶段）均回退默认顺序并按需 warn。
    /// </summary>
    private static string[] NormalizeExecutionOrder(IEnumerable<string>? order, string[] fallback, ILogger? logger)
    {
        var raw = order as string[] ?? order?.ToArray();
        if (raw is not { Length: > 0 })
        {
            logger?.LogWarning("Agents:Execution:ExecutionOrder 缺失或为空，已回退默认顺序");
            return (string[])fallback.Clone();
        }

        var kept = new List<string>(raw.Length);
        var sawStreaming = false;
        foreach (var item in raw)
        {
            var token = item?.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(token))
            {
                logger?.LogWarning("Agents:Execution:ExecutionOrder 含空项，已剔除");
                continue;
            }
            if (!LegalTokens.Contains(token))
            {
                logger?.LogWarning("Agents:Execution:ExecutionOrder 含未知阶段 token「{Token}」（应为 bridge/pipeline/relay/org_route/streaming 之一），已剔除", token);
                continue;
            }
            if (token == StageStreaming)
            {
                if (sawStreaming)
                {
                    logger?.LogWarning("Agents:Execution:ExecutionOrder 重复的 streaming 阶段，已去重");
                }
                sawStreaming = true;
                continue; // streaming 统一置末处理；这里先跳过去重（避免中途重复保留）
            }
            if (!kept.Contains(token))
            {
                kept.Add(token);
            }
            else
            {
                logger?.LogWarning("Agents:Execution:ExecutionOrder 重复的阶段 token「{Token}」，已去重", token);
            }
        }

        // 非流式阶段为空：整表非法，回退全默认顺序（杜绝只有 streaming 一个阶段的“跳过一切”误配置静默生效）。
        if (kept.Count == 0)
        {
            logger?.LogWarning("Agents:Execution:ExecutionOrder 未含任何合法路由阶段，已回退默认顺序");
            return (string[])fallback.Clone();
        }

        if (!sawStreaming) logger?.LogWarning("Agents:Execution:ExecutionOrder 未含 streaming 阶段，已自动补至末尾（普通流式恒为兜底）");
        kept.Add(StageStreaming);
        return kept.ToArray();
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
