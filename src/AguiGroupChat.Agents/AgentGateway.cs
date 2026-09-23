using System.Collections.Concurrent;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using AguiGroupChat.Hub.Agents;
using AguiGroupChat.Hub.Infra;
using AguiGroupChat.Hub.Messaging;
using AguiGroupChat.Hub.Models;
using AguiGroupChat.Hub.Options;
using AguiGroupChat.Hub.Persistence;
using AguiGroupChat.Hub.Storage;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.AGUI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AguiGroupChat.Agents;

/// <summary>
/// 基于 Microsoft Agent Framework 的真实 AG-UI 智能体网关（实现 Hub 预留的 IAgentGateway）。
///
/// 触发后流程：
///   1. 广播 GROUP_TYPING（智能体开始回复）；
///   2. 取得该群共享的 AgentSession，把触发消息作为用户消息送入 ChatClientAgent 流式运行；
///   3. 文本增量经 GroupHub.PublishAgentMessageStartAsync / AppendAgentContentAsync /
///      EndAgentMessageAsync 落库并扇出 TEXT_MESSAGE_* 事件；
///   4. 模型产生的函数调用经 BroadcastAsync 扇出 TOOL_CALL_START（协议 4.5）；
///   5. 结束 / 异常时广播 RUN_ERROR 或静默收尾，并撤销 typing。
/// </summary>
public sealed class AgentGateway : IAgentGateway, IDisposable
{
    /// <summary>注入模型上下文的群历史消息条数（滑动窗口，来自 PromptBudget:HistoryWindowMessages）。</summary>
    private readonly int ContextWindowMessages;

    /// <summary>历史单条消息文本截断长度（来自 PromptBudget:MaxCharsPerHistoryMessage）。</summary>
    private readonly int MaxContextCharsPerMessage;

    /// <summary>多轮上下文：把历史消息里可提取文本的附件重新内联给模型的总字符预算（来自 PromptBudget）。</summary>
    private readonly int MaxHistoryInlineTextChars;

    /// <summary>多轮视觉上下文：单轮最多喂入的当前附图数（来自 PromptBudget:MaxContextImages）。</summary>
    private readonly int MaxContextImages;

    /// <summary>多轮视觉上下文：跟随提问时一并回喂的历史图片上限（来自 PromptBudget:MaxHistoryImages）。</summary>
    private readonly int MaxHistoryImages;

    /// <summary>外部 AG-UI 会话首次建立时发送的话题历史条数上限（全量，存储层上限 5000）。</summary>
    private const int BridgeFullHistoryMax = 5000;

    /// <summary>外部 AG-UI 会话建立后单次增量发送条数上限（上次节点之后的新消息）。</summary>
    private const int BridgeIncrementMax = 100;

    /// <summary>话题滚动小结单次扫描消息数上限（从游标之后 / 首次话题尾部取数）。</summary>
    private const int TopicSummaryScanLimit = 400;

    /// <summary>思考过程总量截断（推理模型 reasoning_content 可能很长：防消息 / 前端 / 存储被撑爆）。</summary>
    private const int MaxReasoningTotalChars = 12000;

    /// <summary>桥接流式正文累计上限（standard 方言是累计文本，防外部服务流式下发无限长正文撑爆内存；截断到前缀不影响增量计算）。</summary>
    private const int MaxBridgeAccumulatedChars = 50000;

    /// <summary>
    /// “这一轮没产出可展示正文”的统一兜底文案。
    ///
    /// <para>
    /// 为什么必须有：正文为空的运行会留下一条**只有前缀**（“（X 代为处理）”）甚至**完全空白**的消息 ——
    /// 用户既看不到结论、也看不到失败原因，更不知道下一步该做什么（实测跏到多次：与「ppt生成助手」的单聊里
    /// 只有一句「（ppt生成助手 代为处理）」，之后再无任何内容、连错误也没有）。
    /// </para>
    ///
    /// <para>
    /// 普通流式 / 指派提升 / 交互恢复三处收尾都读它，口径务必一致；
    /// 判空一定要用 <c>IsNullOrWhiteSpace</c>：<c>??=</c> 兜不住**空串**（实测就是这么漏的）。
    /// </para>
    /// </summary>
    private const string EmptyReplyFallback =
        "（本轮没有产出可展示的正文。可以换一种更明确的问法，或把任务拆成更小的步骤让我重试。）";

    /// <summary>
    /// 当前 run 的业务上下文（AsyncLocal ambient，与 MSAGENT 内部 AgentRunContext 机制同构）。
    /// <see cref="MemoryContextProvider"/>（AIContextProvider）在 InvokingAsync 中读取它完成记忆检索注入。
    /// </summary>
    public static readonly AsyncLocal<AgentInvocationContext?> AmbientContext = new();

    private readonly AgentCatalog _catalog;
    private readonly Lazy<GroupHub> _hub;
    private readonly AgentOptions _options;
    // 规范化（夹紧回退默认后）的执行期时序 / 重试 / TTL 覆盖（来源：Agents:Execution，默认与既有常量一致）
    private readonly ExecutionOptions _execution;
    // 提示词装配预算（PromptBudget，可配）：总闸门 / 历史窗口 / 单条截断 / 历史附件 / 附图数量
    private readonly PromptBudgetOptions _prompt;
    private readonly AttachmentStore? _attachmentStore;
    private readonly ILogger<AgentGateway> _logger;
    // 模型 token 用量统计与配额（可选：未注册用量存储时不统计）
    private readonly Lazy<AguiGroupChat.Hub.Agents.AgentUsageService?> _usage;
    // 技能库（可复用技能：shell/http/prompt）——供确定性编排计划枚举与按计划激活技能
    private readonly Lazy<AgentSkillCatalog?> _skillCatalog;
    // 内网本机桥反向隧道（HTTP/SSE）：数字员工客户端技能由内网机隧道桥承载时，经隧道执行而非前端浏览器
    private readonly Lazy<NativeTunnelService?> _nativeTunnel;
    // 轻量运行指标（可选，6.1 可观测性）
    private readonly Lazy<MetricsService?> _metrics;
    // 话题滚动小结（长话题接续记忆）：可选（未注册服务时不注入）
    private readonly Lazy<TopicSummaryStore?> _topicSummary;
    // 消息反馈（👍/👎 偏好画像）：可选
    private readonly Lazy<MessageFeedbackStore?> _feedback;
    // 编排计划暂停/继续控制：可选（未注册服务时计划照常一口气执行）
    private readonly Lazy<CoordinatedPlanControlStore?> _planControl;
    // 桥接断线自动重连退避（3.1）：连续失败后短时抑制重连（防断线风暴）
    private readonly BridgeCircuitBreaker _bridgeCircuit = new();
    // 每个线程（群）一个会话锁：并发流式写入同一群消息时串行化。
    // 存储 (锁, 上次使用毫秒时间戳)，超时未用（30 分钟，可配置 SessionLockTtlMinutes）自动清理，避免群解散后残留泄漏。
    // 条目上限（SessionLockMaxEntries，默认 512）与锁 TTL 均取自 _execution。
    private readonly ConcurrentDictionary<string, (SemaphoreSlim Lock, long LastUsedMs)> _sessionLocks = new(StringComparer.Ordinal);

    // 单次模型 / 桥接流式调用的最长运行时间（分钟）：模型挂起时防止 Task 永久占用（见 _execution.StreamTimeoutMinutes）。

    // 待决策的人机交互（协议 4.5）：运行中断后保存会话与审批请求，等触发者决策后恢复
    // （超时见 _execution.InteractionTtlMinutes，超时由周期定时器清理）。
    private readonly ConcurrentDictionary<string, PendingInteraction> _pendingInteractions = new(StringComparer.Ordinal);

    /// <summary>批量批准运行集：key = runId。用户对某运行选择「批准本次运行后续全部操作」后，
    /// 该运行后续的审批工具自动放行（不再打断），直到运行结束清除。</summary>
    private readonly ConcurrentDictionary<string, byte> _autoApprovedRuns = new(StringComparer.Ordinal);

    /// <summary>对话内已批准执行的客户端技能：key = threadId|agentId，value = 已批准技能 id 集合 + 过期时间。
    /// 同一问题（同一对话）里用户已同意过的客户端技能，后续再次需要时不再弹确认卡，直接按已批准执行（隧道在线时）。</summary>
    // 已批准技能过期时长见 _execution.ApprovedSkillTtlMinutes（默认 30 分钟）。
    private readonly ConcurrentDictionary<string, (long ExpiresAtMs, HashSet<string> Skills)> _approvedClientSkills = new(StringComparer.Ordinal);

    private static string ApprovedSkillKey(string threadId, string agentId) => threadId + "|" + agentId;

    private HashSet<string> GetApprovedSkills(string threadId, string agentId)
    {
        var key = ApprovedSkillKey(threadId, agentId);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (_approvedClientSkills.TryGetValue(key, out var entry) && entry.ExpiresAtMs > now)
            return entry.Skills;
        _approvedClientSkills.TryRemove(key, out _);
        return new HashSet<string>(StringComparer.Ordinal);
    }

    private void MarkSkillsApproved(string threadId, string agentId, IEnumerable<string> skillIds)
    {
        if (string.IsNullOrWhiteSpace(threadId) || string.IsNullOrWhiteSpace(agentId)) return;
        var key = ApprovedSkillKey(threadId, agentId);
        var set = GetApprovedSkills(threadId, agentId);
        foreach (var s in skillIds)
            if (!string.IsNullOrWhiteSpace(s)) set.Add(s);
        _approvedClientSkills[key] = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + (long)_execution.ApprovedSkillTtlMinutes * 60 * 1000, set);
    }

    private bool IsSkillApproved(string threadId, string agentId, string skillId)
        => GetApprovedSkills(threadId, agentId).Contains(skillId, StringComparer.Ordinal);

    /// <summary>编排计划内「客户端技能」批量执行的等待器：key = interruptId。
    /// 计划在执行到多个需在本机执行的客户端技能时（ExecutionLocation=Client），把它们合并成一张
    /// 「本机一键执行全部」交互卡下发给前端；前端逐个执行并回传结果后，由 <see cref="ResolveInteractionAsync"/>
    /// 写入此处 TCS，计划方法据此点亮各步骤并继续综合。</summary>
    private sealed record BatchClientItem(string SkillId, string Name, string ClientRunner, string Query, int PlanIndex);
    private sealed record BatchClientExec(
        string GroupId, string MessageId, string AgentId, string? ClientId, string TargetMemberId,
        IReadOnlyList<BatchClientItem> Items,
        int DisplayCount, // 展示步骤总数（用于索引对齐）
        TaskCompletionSource<(bool Ok, Dictionary<string, string>? Results)> Completion);
    private readonly ConcurrentDictionary<string, BatchClientExec> _batchClientExecWaits = new(StringComparer.Ordinal);

    /// <summary>外部 AG-UI 桥接增量游标：key = agentId|外部threadId，value = 上次已发送的本话题最后消息 ID。
    /// 会话（首次触发）发送话题全部历史；会话建立后只发游标之后的本话题新消息（增量）。
    /// 经扩展区「bridgeCursors」持久化（组合根 RegisterBridgeCursorPersistence），网关重启后游标不丢。</summary>
    private readonly ConcurrentDictionary<string, string> _bridgeCursors = new(StringComparer.Ordinal);

    /// <summary>变更通知（驱动持久化落盘）：游标推进时 Notify，由持久化服务定时合并写入。</summary>
    private readonly ChangeHub? _changes;

    /// <summary>周期清理定时器（60s）：清理超时未决策的交互（HITL 悬挂）+ 超时未用的会话锁（残留泄漏）。</summary>
    private readonly Timer _purgeTimer;

    // 同一消息最多允许的审批轮数（见 _execution.MaxInteractionRounds，防止外部服务异常导致恢复后反复中断的死循环）。

    /// <summary>活跃运行注册表：runId → 取消令牌与归属（供「停止生成」中断当前流式调用；
    /// 触发者本人或同群管理员可停止）。</summary>
    private readonly ConcurrentDictionary<string, ActiveRun> _activeRuns = new(StringComparer.Ordinal);
    private sealed record ActiveRun(CancellationTokenSource Cts, string GroupId, string AgentId, string TriggerUserId);

    /// <summary>一次待决策的交互：保存被中断的运行现场（会话 / 审批请求 / 业务上下文 / 桥接恢复所需引用）。</summary>
    private sealed record PendingInteraction(
        string InterruptId,        // 本地中断 ID（字典 key，广播给前端）
        string GroupId,
        string AgentId,
        string RunId,
        string MessageId,
        string TargetMemberId,     // 唯一可决策者（触发者）
        string TopicId,
        long CreatedAtMs,
        AgentInvocationContext Context,      // 本地与桥接通用（AmbientContext / 记忆注入）
        string? ExternalInterruptId,         // 外部 AG-UI 服务的 interrupt id（桥接恢复时回传）
        string? ExternalToolCallId,          // 外部被批准工具：toolCallId（standard 方言恢复回传 toolCall）
        string? ExternalToolName,            // 外部被批准工具：工具名
        JsonElement? ExternalToolArguments,  // 外部被批准工具：参数（TOOL_CALL_ARGS 累积）
        ChatClientAgent? Agent,              // 本地 run：ChatClientAgent
        AgentSession? Session,               // 本地 run：AgentSession
        ToolApprovalRequestContent? ApprovalRequest, // 本地 / standard+HTTP：审批请求（CreateResponse 恢复）
        IAguiBridgeClient? BridgeClient,     // 桥接（WS / HTTP standard / hub）：恢复指令 + 继续事件流
        string? InputField = null,           // kind=input 型中断：外部服务 responseSchema 的输入字段名（恢复时以其为键回传用户输入）
        JsonElement? ResponseSchema = null,  // kind=input 型中断：完整 responseSchema（前端渲染表单 / 恢复时规范化 payload）
        IReadOnlyList<BridgeQuestion>? Questions = null, // 外部 question 工具的结构化问题（前端逐题渲染选项）
        int ResumeCount = 0,                 // 已恢复轮数（多轮审批防护：超过 MaxInteractionRounds 强制结束）
        bool SuppressMessage = false);       // 交付物兜底：本 run 的消息由外层统一落定，恢复/结束时不再重复开消息

    /// <summary>
    /// 以 IServiceProvider 惰性解析 GroupHub，避免 DI 循环依赖
    /// （GroupHub → IAgentGateway → GroupHub）。InvokeAsync 触发时 Hub 必然已构造完成。
    /// attachmentStore 可空：未注册附件存储时消息仅携带文本。
    /// 记忆检索注入已按 MSAGENT 标准迁移至 <see cref="MemoryContextProvider"/>（AIContextProvider），
    /// 本网关不再直接注入记忆段落。
    /// </summary>
    public AgentGateway(AgentCatalog catalog, IServiceProvider services, AgentOptions options, AttachmentStore? attachmentStore, ILogger<AgentGateway> logger)
    {
        _catalog = catalog;
        _hub = new Lazy<GroupHub>(() => services.GetRequiredService<GroupHub>());
        _options = options;
        // 执行期覆盖：优先用进程共享的 ExecutionOptions 单例（管理侧热改同一对象即生效），未注册时用配置归一化副本。生
        // 效值已由注册处/此处 Normalize 夹紧，保证后续各处取到的是已夹紧值，绝不用废值。

        _execution = services.GetService(typeof(ExecutionOptions)) as ExecutionOptions
            ?? (options.Execution ?? ExecutionOptions.Default).Normalize(_logger);
        // 提示词装配预算（Hub 注册的单例；未注册时用出厂默认）：历史窗口 / 单条截断 / 历史附件 / 附图数量在构造时取定，
        // 因为它们在热路径上被反复读，且与 PromptBudget 其它字段一致“改配置需重启”。
        _prompt = services.GetService(typeof(PromptBudgetOptions)) as PromptBudgetOptions ?? PromptBudgetOptions.Default;
        ContextWindowMessages = _prompt.HistoryWindowMessages;
        MaxContextCharsPerMessage = _prompt.MaxCharsPerHistoryMessage;
        MaxHistoryInlineTextChars = _prompt.MaxHistoryInlineTextChars;
        MaxContextImages = _prompt.MaxContextImages;
        MaxHistoryImages = _prompt.MaxHistoryImages;
        _attachmentStore = attachmentStore;
        _logger = logger;
        _changes = services.GetService<ChangeHub>(); // 游标持久化脏位通知（可选：未注册持久化时不落盘）
        _usage = new Lazy<AguiGroupChat.Hub.Agents.AgentUsageService?>(() =>
            services.GetService(typeof(AguiGroupChat.Hub.Agents.AgentUsageService)) as AguiGroupChat.Hub.Agents.AgentUsageService);
        _skillCatalog = new Lazy<AgentSkillCatalog?>(() => services.GetService(typeof(AgentSkillCatalog)) as AgentSkillCatalog);
        // 内网本机桥反向隧道：数字员工调由内网桥承载的客户端技能时，优先经隧道让那台内网机执行（而非前端浏览器）
        _nativeTunnel = new Lazy<NativeTunnelService?>(() => services.GetService(typeof(NativeTunnelService)) as NativeTunnelService);
        // 轻量运行指标（可选，6.1）
        _metrics = new Lazy<MetricsService?>(() =>
            services.GetService(typeof(MetricsService)) as MetricsService);
        _topicSummary = new Lazy<TopicSummaryStore?>(() =>
            services.GetService(typeof(TopicSummaryStore)) as TopicSummaryStore);
        _feedback = new Lazy<MessageFeedbackStore?>(() =>
            services.GetService(typeof(MessageFeedbackStore)) as MessageFeedbackStore);
        _planControl = new Lazy<CoordinatedPlanControlStore?>(() =>
            services.GetService(typeof(CoordinatedPlanControlStore)) as CoordinatedPlanControlStore);
        // HITL 悬挂清理与会话锁 TTL 清理改为独立定时器定期执行（不再依赖「新增交互时顺带清理」），
        // 保证即使没有新交互产生，超时未决策的交互 / 已解散群的残留会话锁也能被回收。
        _purgeTimer = new Timer(_ => PurgePeriodicCleanup(), null, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));
    }

    /// <summary>外部会话增量游标快照（持久化扩展区「bridgeCursors」的 snapshot 回调）。</summary>
    internal object SnapshotBridgeCursors() => new Dictionary<string, string>(_bridgeCursors);

    /// <summary>清空全部外部会话增量游标（系统初始化用）。</summary>
    public void ClearBridgeCursors()
    {
        _bridgeCursors.Clear();
        _changes?.Notify();
    }

    /// <summary>从持久化恢复外部会话增量游标（扩展区 restore 回调，启动时调用）。</summary>
    internal void RestoreBridgeCursors(JsonElement element)
    {
        var restored = element.Deserialize<Dictionary<string, string>>(AguiJson.Options) ?? [];
        foreach (var kv in restored)
            _bridgeCursors[kv.Key] = kv.Value;
        _logger.LogInformation("恢复外部 AG-UI 桥接增量游标 {Count} 条（按话题增量会话跨重启保持）", restored.Count);
    }

    public void Dispose() => _purgeTimer.Dispose();

    /// <summary>周期清理回调（60s）：交互 TTL + 会话锁 TTL 一并清理。Timer 回调不抛未捕获异常（防御性 try/catch）。</summary>
    private void PurgePeriodicCleanup()
    {
        try
        {
            _ = PurgeExpiredInteractions(); // 交互清理含异步桥接连接释放；定时器回调不等待
            PurgeExpiredSessionLocks();
            PurgeExpiredApprovedSkills();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "定时清理任务异常（已忽略）");
        }
    }

    /// <summary>清理超时的“已批准客户端技能”记忆（同一问题内免重复同意的内存缓存）。</summary>
    private void PurgeExpiredApprovedSkills()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var kv in _approvedClientSkills)
            if (kv.Value.ExpiresAtMs <= now) _approvedClientSkills.TryRemove(kv.Key, out _);
    }

    /// <summary>清理超时未用的会话锁（群解散后残留泄漏防护）：无条件遍历，不依赖条目数阈值（Count>=512 阈值仅作兜底）。</summary>
    private void PurgeExpiredSessionLocks()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var kv in _sessionLocks)
        {
            if (now - kv.Value.LastUsedMs > (long)_execution.SessionLockTtlMinutes * 60 * 1000)
                _sessionLocks.TryRemove(kv.Key, out _);
        }
    }

    public Task<bool> IsAvailableAsync(string agentId, CancellationToken ct)
        => Task.FromResult(_catalog.GetDefinition(agentId) is not null);

    public async Task<AgentInvocationResult> InvokeAsync(AgentInvocationContext context, CancellationToken ct)
    {
        // 设置 ambient run 上下文（MemoryContextProvider 经 AsyncLocal 读取），方法结束即清理
        var prev = AmbientContext.Value;
        AmbientContext.Value = context;
        try
        {
            var isBridge = IsBridgeAgent(context.AgentId);
            // 桥接退避（3.1）：外部端点刚连续失败 → 短时抑制重连，避免频繁重试打爆端点/本地
            if (isBridge && _bridgeCircuit.IsOpen(context.AgentId, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()))
                return new AgentInvocationResult(false, null, "AGENT_BRIDGE_BACKOFF");

            var result = await InvokeCoreAsync(context, ct);
            _bridgeCircuit.Record(context.AgentId,
                isFailure: result.ErrorCode is "AGENT_BRIDGE_ERROR" or "AGENT_BRIDGE_DISCONNECTED",
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            _metrics.Value?.RecordInvocation(context.AgentId, result.Accepted,
                isBridge: isBridge,
                isBridgeFailure: result.ErrorCode is "AGENT_BRIDGE_ERROR" or "AGENT_BRIDGE_DISCONNECTED",
                outputChars: 0); // 输出字数由流式累加；此处仅记是否接受。细粒度 token 计费另属用量服务
            return result;
        }
        finally { AmbientContext.Value = prev; }
    }

    /// <summary>该智能体是否为外部 AG-UI 桥接角色（配置了端点或全局默认端点）。</summary>
    private bool IsBridgeAgent(string agentId)
        => !string.IsNullOrWhiteSpace(_options.AguiBridge?.Endpoint)
           || _catalog.GetDefinition(agentId)?.BridgeEndpoint is { Length: > 0 };

    /// <summary>按工具名（技能库 SkillId）反查技能定义；客户端执行技能在其 <see cref="AgentSkillDefinition.ClientRunner"/> 中携带前端运行配置。</summary>
    private AgentSkillDefinition? GetSkillById(string toolName)
    {
        var catalog = _skillCatalog.Value;
        if (catalog is null) return null;
        var d = catalog.Get(toolName);
        if (d is not null) return d;
        return catalog.ListAll().FirstOrDefault(s => s.SkillId == toolName);
    }

    /// <summary>该数字员工是否挂载了“受控组织落库（OrgDeploy）”类技能：此类技能是对话驱动的部署动作（先出稿 → 管理员确认 → function-call 落库），
    /// 而非可在“编排计划/按查”里批量投给 SkillRunner 的执行技能，故有此挂载的数字员工应走普通带工具 run 而非计划路由。</summary>
    private bool HasMountedOrgDeploy(AgentDefinition def)
    {
        var catalog = _skillCatalog.Value;
        if (catalog is null || def.SkillDefIds is not { Count: > 0 }) return false;
        foreach (var id in def.SkillDefIds)
        {
            if (catalog.Get(id) is { Kind: AgentSkillKind.Org_deploy })
                return true;
        }
        return false;
    }

    /// <summary>该工具（客户端技能）是不是本机 dotnet（C#）类型：由桥在本机编译执行，浏览器无法直接运行任意 C#。</summary>
    private bool IsClientDotnetSkill(string toolName)
    {
        var skill = GetSkillById(toolName);
        return skill is not null
            && skill.Kind == AgentSkillKind.Dotnet
            && skill.ExecutionLocation == AgentSkillExecutionLocation.Client;
    }

    /// <summary>本机 dotnet 技能的 C# 源码（= 技能正文）。</summary>
    private string? ClientDotnetSource(string toolName)
    {
        var skill = GetSkillById(toolName);
        return skill?.Kind == AgentSkillKind.Dotnet ? (skill.Body ?? "") : null;
    }

    /// <summary>执行本机 dotnet（C#）技能：只在发起请求的 clientId 那台上跑；该 client 不在线/未上报返回 null（无可用桥）。
    /// 强调：绝不回落 agent/平台作用域桥，避免在“非发起用户所在机器”上执行。</summary>
    private async Task<string?> ExecuteTunnelDotnetAsync(
        string agentId, string? clientId, string source, string? query,
        TimeSpan waitTimeout, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(clientId) && _nativeTunnel.Value is { } t1 && t1.HasClient(clientId))
            return await t1.ExecuteDotnetForClientAsync(clientId, source, query, waitTimeout, ct);
        return null;
    }

    /// <summary>从客户端执行技能的 <see cref="AgentSkillDefinition.ClientRunner"/>（JSON）解析 shell 命令 / 工作目录 / 超时，供内网隧道执行。
    /// 仅支持单技能对象（非批量数组）；解析失败返回 false。</summary>
    private bool TryParseClientShell(string toolName, out string? command, out string? cwd, out int? timeoutSec)
    {
        var skill = GetSkillById(toolName);
        if (skill is null)
        {
            command = null; cwd = null; timeoutSec = null;
            return false;
        }
        var runner = EffectiveClientRunner(skill);
        if (string.IsNullOrWhiteSpace(runner))
        {
            command = null; cwd = null; timeoutSec = null;
            return false;
        }
        return TryParseRunnerShell(runner, out command, out cwd, out timeoutSec);
    }

    /// <summary>从一段 <c>ClientRunner</c> JSON 解析 shell 命令 / 工作目录 / 超时（供内网隧道对单技能 / 批量项执行）。
    /// 仅支持单技能对象（非批量数组）；要求 <c>kind=shell</c> 且命令非空；返回 false 表示不能经隧道执行。</summary>
    private static bool TryParseRunnerShell(string clientRunner, out string? command, out string? cwd, out int? timeoutSec)
    {
        command = null; cwd = null; timeoutSec = null;
        if (string.IsNullOrWhiteSpace(clientRunner)) return false;
        try
        {
            using var doc = JsonDocument.Parse(clientRunner);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false; // 批量数组不在此路径处理
            var kind = root.TryGetProperty("kind", out var k) ? k.GetString() : null;
            if (!string.Equals(kind, "shell", StringComparison.OrdinalIgnoreCase)) return false;
            command = root.TryGetProperty("command", out var c) ? c.GetString() : null;
            cwd = root.TryGetProperty("cwd", out var w) ? w.GetString() : null;
            timeoutSec = root.TryGetProperty("timeoutSec", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt32() : (int?)null;
            return !string.IsNullOrWhiteSpace(command);
        }
        catch { return false; }
    }

    /// <summary>客户端执行的 shell 技能实际要用的 <c>ClientRunner</c>：带显式 runner 用显式；
    /// 否则（早期 / 编排创建的技能可能未写 runner）从技能正文（PowerShell）自动构造可经隧道 / 前端解析的 runner。
    /// 避免「<c>executionLocation=client</c> 但缺 runner」导致本机无法执行。</summary>
    private static string? EffectiveClientRunner(AgentSkillDefinition skill)
    {
        if (!string.IsNullOrWhiteSpace(skill.ClientRunner)) return skill.ClientRunner;
        if (skill.Kind == AgentSkillKind.Shell
            && skill.ExecutionLocation == AgentSkillExecutionLocation.Client
            && !string.IsNullOrWhiteSpace(skill.Body))
            return "{\"kind\":\"shell\",\"command\":" + JsonSerializer.Serialize(skill.Body) + ",\"cwd\":\".\",\"timeoutSec\":30}";
        return null;
    }

    /// <summary>是否能执行该客户端技能。按 A 口径：绝不用 agent/平台桥兜底，<b>只能在发起请求的 clientId 那台上跑</b>——
    /// clientId 非空且其桥在线才 true；若为空（发起它的浏览器未连接本机桥）一律 false（不再回落 agent/平台作用域）。</summary>
    private bool TunnelAvailable(string agentId, string? clientId)
        => !string.IsNullOrWhiteSpace(clientId) && _nativeTunnel.Value?.HasClient(clientId) == true;

    /// <summary>执行客户端 shell：只在发起请求的 clientId 那台上跑；该 client 不在线/未上报返回 null（无可用桥）。
    /// 强调：绝不回落 agent/平台作用域桥。</summary>
    private async Task<string?> ExecuteTunnelAsync(
        string agentId, string? clientId,
        string command, string? cwd, int? timeoutSec, string? query,
        TimeSpan waitTimeout, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(clientId) && _nativeTunnel.Value is { } t && t.HasClient(clientId))
            return await t.ExecuteForClientAsync(clientId, command, cwd, timeoutSec, query, waitTimeout, ct);
        return null;
    }

    private bool IsSupportGroup(string groupId)
    {
        try { return _hub.Value.IsSupportCircleGroup(groupId); } catch { return false; }
    }

    /// <summary>客服知聚双保险：本机(client)技能只允许在“发起请求的顾客”浏览器所在机器执行。
    /// 该顾客 client 缺失/离线时记录告警——执行端按 A 口径不会在客服/服务器/其它机器跑（不回落）。</summary>
    private void GuardSupportCircleClient(string groupId, string? clientId, string what)
    {
        if (!IsSupportGroup(groupId)) return;
        bool ok = !string.IsNullOrWhiteSpace(clientId) && _nativeTunnel.Value?.HasClient(clientId) == true;
        if (!ok)
            _logger.LogWarning("客服知聚【本机技能只在请求顾客的机器执行】: {What} group={G} 未找到该顾客本机桥 client={C}（不会在客服/服务器/其它机器执行）",
                what, groupId, clientId ?? "(空)");
    }

    /// <summary>
    /// 估算本次任务复杂度并安装“运行超时预算”（复杂度自适应超时）：
    /// 主预算 = StreamTimeoutMinutes × 档位倍率，受 ComplexityTimeouts:MaxRunTimeoutMinutes 夹紧；
    /// 同时写入 <see cref="RunTimeoutPolicy.Ambient"/>，让技能宿主按同一倍率放宽单技能预算。
    /// 各运行入口（首轮 / 桥接 / 审批恢复）都要调用，且恢复路径用同一份触发上下文 → 结果与首轮一致。
    /// </summary>
    private RunTimeoutBudget InstallRunBudget(AgentInvocationContext context, AgentDefinition? def)
    {
        var previous = RunTimeoutPolicy.Ambient;
        var budget = RunTimeoutPolicy.Install(context.Content, context.Attachments, IsFanOutRole(def), _execution, _options);
        // 只在“本次异步流的首次安装”且真的被放宽时记 Information：
        // 同一运行的后续阶段（流水线 / 指派链 / 交付兑底）重算结果相同，重复记录只会刷屏；
        // 这一行可直接回答“为什么这次跑这么久”。
        if (previous is null && budget.Run > TimeSpan.FromMinutes(_execution.StreamTimeoutMinutes))
            _logger.LogInformation("运行超时预算按任务复杂度放宽：agent={AgentId} {Budget}", context.AgentId, budget.Describe());
        else if (previous is null)
            _logger.LogDebug("运行超时预算：agent={AgentId} {Budget}", context.AgentId, budget.Describe());
        return budget;
    }

    /// <summary>该角色是否会把任务向下摊开（编排流水线 / 向下指派）：这类运行的链条天然更长（计划 + 递归补查 + 交付）。</summary>
    private static bool IsFanOutRole(AgentDefinition? def)
        => def is not null && ((def.Pipeline?.Count ?? 0) > 0 || (def.AssignmentIds?.Count ?? 0) > 0);

    /// <summary>
    /// 客户端技能经内网隧道执行的等待上界（秒）。模型自报的 shell 超时决定基准，
    /// 原先上界硬编码 180 秒——对“复杂度高的本地批处理”是短板；这里改用本次运行的复杂度预算作为上界。
    /// 注意：始终不小于原本的 10..180 区间，所以对普通任务行为完全不变。
    /// </summary>
    private static TimeSpan ClientSkillTimeout(int? declaredSeconds)
    {
        var sec = Math.Clamp(declaredSeconds.GetValueOrDefault(30) + 20, 10, 180);
        if (RunTimeoutPolicy.Ambient is { ClientSkillTimeoutSec: > 0 } b) sec = Math.Max(sec, b.ClientSkillTimeoutSec);
        return TimeSpan.FromSeconds(sec);
    }

    /// <summary>顾客机不可达时给模型的明确失败文案（客服知聚强调“只在请求顾客的机器执行”）。</summary>
    private string SupportClientUnavailableText(AgentInvocationContext ctx, string toolName)
    {
        if (!IsSupportGroup(ctx.GroupId))
            return "（未能执行：发起请求/决策的电脑未连接本机桥 NativeBridge，无法路由到该机器执行该客户端技能。请在该电脑启动 AguiGroupChat.NativeBridge 后重新发起该操作。）";
        return $"（未能执行：客服知聚的本机技能只能在发起请求的顾客电脑上执行，但该顾客电脑未连接本机桥 NativeBridge（client 缺失/离线）。请顾客在其电脑启动 AguiGroupChat.NativeBridge 后重新发起该操作。技能：{toolName}）";
    }

    /// <summary>取函数调用参数里的 query 文本（供经隧道的 shell 技能做 ${query} 占位替换）；
    /// 参数里没有 query 键时回退为紧凑 JSON（与前端 clientToolSubstitute 的取值口径一致）。</summary>
    private static string? ApprovalArgsQuery(FunctionCallContent fc)
    {
        if (fc.Arguments is { Count: > 0 } args)
        {
            if (args.TryGetValue("query", out var q) && q is not null)
                return q.ToString();
            try { return System.Text.Json.JsonSerializer.Serialize(args, AguiJson.Options); } catch { /* 序列化失败回退 null */ }
        }
        return null;
    }

    private async Task<AgentInvocationResult> InvokeCoreAsync(AgentInvocationContext context, CancellationToken ct)
    {
        var def = _catalog.GetDefinition(context.AgentId);
        if (def is null)
            return new AgentInvocationResult(false, null, "AGENT_NOT_CONFIGURED");

        // 顺序可配的执行阶段分派（Step B）：默认 bridge→pipeline→relay→org_route→streaming，
        // 由 _execution.ExecutionOrder 决定、可经平台开关 + 角色级覆盖禁用单个非兜底阶段。
        // 命中阶段即返回其结果；全部阶段语义未命中（fallthrough）时落回下方普通流式兜底。
        var effectiveMode = context.TriggerMode ?? def.TriggerMode;
        // 交付物兜底会以“只跑流式”标记调用，避免再次命中 org_route 形成递归。
        if (!_streamingOnly.Value
            && await DispatchRoutedStagesAsync(context, def, effectiveMode, ct) is { } routedResult)
            return routedResult;

        // 语境触发（Contextual）：下方普通流式 / 模型消费前先结合群上下文判断是否应发言，不发言则静默跳过
        // （不发任何事件）。语境沉默只作用于最终落回模型这条路径——确定性阶段（bridge/pipeline/relay）
        // 与组织化路由（需 Mentioned，非 Contextual）均不受影响，语义与原实现一致。
        if (effectiveMode == AgentTriggerMode.Contextual && !await ShouldSpeakAsync(context, def, ct))
        {
            _logger.LogInformation("智能体 {AgentId} 语境判断为保持沉默（group={GroupId}）", context.AgentId, context.GroupId);
            return new AgentInvocationResult(false, null, "AGENT_DECIDED_SILENT");
        }

        var agent = _catalog.GetOrCreate(context.AgentId);
        var runId = "run_" + IdGenerator.NewId();
        _logger.LogInformation("智能体 {AgentId} 开始运行：run={RunId} group={GroupId} 触发消息={MessageId}",
            context.AgentId, runId, context.GroupId, context.TriggerMessageId);

        // 配额校验（Agents:DailyTokenQuotaPerUser）：超限拒绝触发（定时任务 system 不受限）
        if (_usage.Value?.CheckUserQuota(context.TriggerUserId) is { } quota)
        {
            _logger.LogWarning("智能体触发被配额拦截：user={User} used={Used}/{Quota} agent={AgentId}",
                context.TriggerUserId, quota.Used, quota.Quota, context.AgentId);
            await _hub.Value.BroadcastAsync(context.GroupId, new RunErrorEvent
            {
                GroupId = context.GroupId,
                ErrorCode = "AGENT_QUOTA_EXCEEDED",
                Message = $"今日模型用量已达配额上限（{quota.Quota} token），请明日再试或联系管理员",
                Timestamp = _hub.Value.NowMs,
            }, ct: CancellationToken.None);
            return new AgentInvocationResult(false, runId, "AGENT_QUOTA_EXCEEDED");
        }

        await _hub.Value.BroadcastTypingAsync(new GroupTypingRequest
        {
            GroupId = context.GroupId,
            MemberId = context.AgentId,
            IsTyping = true,
        }, ct);

        // 链路可视化：为本次运行建立技能调用链（skill 调用经 AgentSkillCall 嵌套填充），运行结束写库并广播
        var prevChain = SkillChainBuilder.Ambient.Value;
        SkillChainBuilder.Ambient.Value = new SkillChainBuilder();
        SkillChainBuilder.Ambient.Value.EnsureRoot(context.AgentId, def.Nickname ?? context.AgentId);
        // 工具返回收集：技能产物（produce_file 标记）以工具真实返回为准，
        // 不依赖模型在正文里原样复述 JSON（模型常改写成人话而丢掉标记）。
        var prevToolResults = ToolResultCollector.Ambient.Value;
        ToolResultCollector.Ambient.Value = new ToolResultCollector();

        string? messageId = null;
        ToolApprovalRequestContent? approval = null;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(InstallRunBudget(context, def).Run); // 模型挂起保护（按任务复杂度放宽）
        var runCt = timeoutCts.Token;
        _activeRuns[runId] = new ActiveRun(timeoutCts, context.GroupId, context.AgentId, context.TriggerUserId); // 注册：支持「停止生成」
        var sessionLock = GetSessionLock(context.ThreadId);
        var acquired = false;
        try
        {
            // WaitAsync 移入 try：未获锁就取消/超时时走 catch + finally，保证 typing=false 一定广播（避免 typing 卡死）
            await sessionLock.WaitAsync(runCt);
            acquired = true;
            var session = await GetOrCreateSessionAsync(context, agent, runCt);

            var started = await _hub.Value.PublishAgentMessageStartAsync(new AgentMessageStartInput
            {
                GroupId = context.GroupId,
                AgentId = context.AgentId,
                RunId = runId,
                TopicId = context.TopicId,
                ReplyToMessageId = context.TriggerMessageId,
                // 回复不再携带 @ 信息（触发消息的提及仅用于触发，不回显到智能体回复）
                Mentions = [],
                MentionAll = false,
                // 回复继承触发消息的可见性：私密 / 定向内容不向全群广播
                Visibility = context.Visibility,
                VisibleMemberIds = context.VisibleMemberIds ?? [],
            }, runCt);

            messageId = started.MessageId;

            // 图片理解（视觉）：消息或本话题最近带图历史里含图片、且启用视觉时，切到视觉模型多模态（文本 + 图 byte）喂模型。
            // 多轮场景（先发图、后追问纯文本）：BuildVisionUserMessageAsync 会把最近窗口里带图的历史消息图片一并回喂，
            // 使后续提问仍能“看到”先前那张图，而不是只能见图片的文本元数据。
            var visionModel = _options.VisionEnabled
                ? AgentCatalog.ResolveVisionModelName(_options, string.Equals(_options.Provider, "deepseek", StringComparison.OrdinalIgnoreCase))
                : null;

            var accumulated = "";
            var reasoningAccumulated = 0; // 思考过程累计长度（防推理模型思考过长撑爆消息 / 前端）
            ChatMessage userMessage;
            // 视觉模型可用时才尝试多模态组装；不可用（未配视觉）一律纯文本，行为与旧版一致。
            if (!string.IsNullOrWhiteSpace(visionModel))
            {
                (userMessage, var visionTurn) = await BuildVisionUserMessageAsync(context, runCt);
                // 本轮真的带图 → 换用视觉模型。
                // 注意：换的**只是模型**——工具 / 记忆 / 审批包装照旧（见 GetOrCreateVision 的说明）：
                // 曾经这里换成了不带工具的裸视觉体，结果模型把工具调用当正文写出来、技能从未执行。
                if (visionTurn) agent = _catalog.GetOrCreateVision(context.AgentId, visionModel);
            }
            else
            {
                userMessage = new ChatMessage(ChatRole.User, await BuildUserMessageAsync(context, runCt));
            }
            var runOptions = new ChatClientAgentRunOptions();
            // 模型限流（429）/ 网关 5xx / 连接重置时指数退避重试：避免群聊中智能体偶发哑火。
            // 重试从头流式输出（清空累计跟踪与已广播的半截内容）；审批中断不触发重试（approval 置位后 break）。
            var attempt = 0;
            // 模型重试次数上限取自 _execution.MaxModelAttempts（默认 2）
            while (true)
            {
                try
                {
                    await foreach (var update in agent.RunStreamingAsync(userMessage, session, runOptions, runCt))
                    {
                // AgentResponseUpdate.Text 的形态取决于客户端：MockChatClient 为累计文本，
                // 真实 OpenAI 兼容客户端（如 DeepSeek）为增量片段。统一按累计文本跟踪：
                // 累计文本 → 取相对上一帧的新增部分；增量片段 → 整体作为 delta。
                if (update.Text is { Length: > 0 } text)
                {
                    var delta = ComputeTextDelta(accumulated, text);
                    if (delta.Length > 0)
                    {
                        await _hub.Value.AppendAgentContentAsync(context.GroupId, messageId, delta, runCt);
                        accumulated += delta;
                    }
                }

                // 思考过程（deepseek-reasoner 等推理模型的 reasoning_content → TextReasoningContent）：
                // 转发到独立的 TEXT_MESSAGE_REASONING 通道，前端以折叠「思考过程」块展示，与正文分离
                foreach (var rc in update.Contents.OfType<TextReasoningContent>())
                {
                    if (rc.Text is not { Length: > 0 } r) continue;
                    if (reasoningAccumulated >= MaxReasoningTotalChars) continue; // 总量截断
                    var remaining = MaxReasoningTotalChars - reasoningAccumulated;
                    var rd = r.Length > remaining ? r[..remaining] : r;
                    reasoningAccumulated += rd.Length;
                    await AppendReasoningAsync(context.GroupId, messageId, rd, runCt);
                }

                // 函数调用 → TOOL_CALL_START（协议 4.5）：携带参数，前端展示工具调用详情
                foreach (var fc in update.Contents.OfType<FunctionCallContent>())
                {
                    await _hub.Value.BroadcastAsync(context.GroupId, new ToolCallStartEvent
                    {
                        ToolCallId = fc.CallId ?? "tool_" + IdGenerator.NewId(),
                        ToolCallName = fc.Name,
                        ToolArguments = fc.Arguments is { Count: > 0 } ? JsonSerializer.Serialize(fc.Arguments) : null,
                        ParentMessageId = messageId,
                        GroupId = context.GroupId,
                        TriggerUserId = context.TriggerUserId,
                        Timestamp = _hub.Value.NowMs,
                    }, ct: runCt);
                }

                // 工具执行结果 → TOOL_CALL_RESULT（Hub 扩展）：与工具调用行关联展示
                foreach (var fr in update.Contents.OfType<FunctionResultContent>())
                {
                    if (fr.Result is null) continue;
                    // 同时收集原始工具返回：技能产物的 produce_file 标记以它为准，
                    // 因为模型可能在正文里把 JSON 改写成自然语言而丢掉标记。
                    ToolResultCollector.Ambient.Value?.Add(AgentGatewayHelpers.DescribeToolResult(fr.Result));
                    await _hub.Value.BroadcastAsync(context.GroupId, new ToolCallResultEvent
                    {
                        ToolCallId = fr.CallId ?? "tool_" + IdGenerator.NewId(),
                        ParentMessageId = messageId,
                        GroupId = context.GroupId,
                        Result = AgentGatewayHelpers.DescribeToolResult(fr.Result),
                        Timestamp = _hub.Value.NowMs,
                    }, ct: runCt);
                }

                // 人机交互（协议 4.5）：工具需要审批 → 运行中断，等待触发者决策
                foreach (var apr in update.Contents.OfType<ToolApprovalRequestContent>())
                {
                    approval = apr;
                    break;
                }
                if (approval is not null) break;
                    }

                    // —— 反向隧道（内网穿透）：客户端技能且该数字员工的客户端技能由内网机隧道桥承载、且不要求确认时，
                    //     经隧道让那台内网机执行（不依赖前端浏览器），结果回灌模型继续，而非下发给前端。 ——
                    //     若配置要求确认（ClientToolTunnelRequireApproval=true，默认），则不在此自动执行，交给下方
                    //     「审批中断 → 下交互卡 → 触发者批准后由 ResumeRunAsync 经隧道执行」。
                    if (approval is not null
                        && approval.ToolCall is FunctionCallContent tfc
                        && _catalog.GetAgentClientToolNames(context.AgentId).Contains(tfc.Name, StringComparer.Ordinal)
                        && TunnelAvailable(context.AgentId, context.PreferredBridgeClient)
                        && !_options.ClientToolTunnelRequireApproval
                        && TryParseClientShell(tfc.Name, out var shellCmd, out var shellCwd, out var shellTimeoutSec))
                    {
                        await _hub.Value.ResetAgentContentAsync(context.GroupId, messageId, runCt);
                        var tunnelResult = await ExecuteTunnelAsync(
                            context.AgentId, context.PreferredBridgeClient, shellCmd!, shellCwd, shellTimeoutSec, ApprovalArgsQuery(tfc),
                            ClientSkillTimeout(shellTimeoutSec), runCt);
                        var resultText = string.IsNullOrWhiteSpace(tunnelResult)
                            ? (tunnelResult is null ? "（内网本机桥执行未返回结果 / 超时）" : "（内网本机执行无输出）")
                            : tunnelResult;
                        // 关键：把隧道结果写入 ClientToolResultStore——approval.CreateResponse(true) 会让 MSAGENT 重放并执行
                        // 该客户端技能的占位函数，占位函数从该 Store 读取真实结果；不写入则读到 null 回落为占位文本，覆盖掉隧道结果。
                        ClientToolResultStore.Put(tfc.Name, resultText);
                        _logger.LogInformation("客户端技能经内网隧道执行：agent={AgentId} tool={Tool}", context.AgentId, tfc.Name);
                        accumulated = ""; reasoningAccumulated = 0;
                        userMessage = new ChatMessage(ChatRole.User, new AIContent[]
                        {
                            approval.CreateResponse(true),
                            new TextContent($"[前端工具] {tfc.Name} 已由内网本机桥执行完毕，请直接引用它的结果作答：\n{resultText}\n（答完即可，无需再调用该工具）"),
                        });
                        approval = null; // 复位，重新进入主循环以注入结果的用户消息继续流式作答
                        continue;
                    }

                    break; // 模型流正常完成（审批中断时 approval 已置位，由下方分支处理）
                }
                catch (Exception ex) when (AgentGatewayHelpers.IsRetryableModelError(ex) && attempt < _execution.MaxModelAttempts)
                {
                    attempt++;
                    _logger.LogWarning(ex, "模型调用返回可重试错误，第 {Attempt} 次退避重试（agent={AgentId}）", attempt, context.AgentId);
                    await Task.Delay(TimeSpan.FromSeconds(1.5 * attempt), runCt); // 指数退避
                    // 清空已广播的半截内容（避免重试输出与失败内容拼接重复），重置累计跟踪
                    try { await _hub.Value.ResetAgentContentAsync(context.GroupId, messageId, runCt); } catch { /* 消息已结束则忽略 */ }
                    accumulated = "";
                    reasoningAccumulated = 0;
                }
            }

            // 审批中断：清空已回灌的中间内容（避免显示半截回复），保存运行现场 + 广播交互请求（仅触发者可决策）。
            // 消息保持开启（不 End）：用户反馈后同一 AgentSession 继续运行，最终结果在运行结束时一次性返回。
            if (approval is not null)
            {
                // 诊断：客户端技能需要在本机执行，但发起请求的 client 缺失 / 其桥不在线——这种情形下无人能真机执行，
                // 审批后也只会落到服务端兜底/失败。记录原因便于定位“执行环境没对上用户电脑”的问题。
                if (approval.ToolCall is FunctionCallContent diagFc
                    && _catalog.GetAgentClientToolNames(context.AgentId).Contains(diagFc.Name, StringComparer.Ordinal)
                    && !TunnelAvailable(context.AgentId, context.PreferredBridgeClient))
                {
                    _logger.LogWarning(
                        "客户端技能 {Tool} 无法路由到发起客户端：PreferredBridgeClient={Client}（空=前端未发现本机桥；非空=该桥不在线/未注册）",
                        diagFc.Name, context.PreferredBridgeClient ?? "(空)");
                }
                await _hub.Value.ResetAgentContentAsync(context.GroupId, messageId, runCt);
                var fc = approval.ToolCall as FunctionCallContent;
                // 客户端执行技能：toolName 命中则标记 kind=client_tool，下发给前端执行（复用 HITL 通道下发 + 回传）；
                // 若该数字员工已有内网隧道桥，则在 while 内经隧道执行（见上），不会到达这里走前端下发。
                var isClientTool = fc is not null
                    && _catalog.GetAgentClientToolNames(context.AgentId).Contains(fc.Name, StringComparer.Ordinal);

                var interruptId = "interrupt_" + IdGenerator.NewId();
                var clientSkill = isClientTool ? GetSkillById(fc!.Name) : null;
                var clientRunner = clientSkill is null ? null : EffectiveClientRunner(clientSkill);
                _pendingInteractions[interruptId] = new PendingInteraction(
                    interruptId, context.GroupId, context.AgentId, runId, messageId,
                    context.TriggerUserId, context.TopicId, _hub.Value.NowMs, context,
                    ExternalInterruptId: null,
                    ExternalToolCallId: null, ExternalToolName: null, ExternalToolArguments: null,
                    Agent: agent, Session: session, ApprovalRequest: approval,
                    BridgeClient: null);
                await PurgeExpiredInteractions();
                await _hub.Value.BroadcastAsync(context.GroupId, new AgentInteractionRequestEvent
                {
                    GroupId = context.GroupId,
                    MessageId = messageId,
                    ThreadId = context.ThreadId,
                    RunId = runId,
                    InterruptId = interruptId,
                    ToolCallId = fc?.CallId ?? "tool_" + IdGenerator.NewId(),
                    ToolName = fc?.Name ?? "unknown",
                    ToolArguments = fc?.Arguments is { } args ? JsonSerializer.SerializeToElement(args) : null,
                    Message = isClientTool
                        ? $"智能体「{def.Nickname}」请求你在本机执行客户端技能「{fc?.Name}」"
                        : $"智能体「{def.Nickname}」请求你确认：是否执行操作「{fc?.Name}」？",
                    Kind = isClientTool ? "client_tool" : "approval",
                    ClientRunner = clientRunner,
                    TargetMemberId = context.TriggerUserId,
                    Timestamp = _hub.Value.NowMs,
                }, ct: runCt);

                _logger.LogInformation("智能体 {AgentId} 运行中断等待交互：run={RunId} interrupt={InterruptId} target={Target}",
                    context.AgentId, runId, interruptId, context.TriggerUserId);
                return new AgentInvocationResult(false, runId, "AGENT_AWAITING_INTERACTION");
            }

            var attached = await AttachPublishedProductsAsync(context.GroupId, messageId, accumulated, runCt);
            await AttachAgentChainAsync(context, messageId, runCt);
            // 空正文兜底：模型这一轮既没给正文、也没产出文件时，不要留一条**完全空白**的消息
            // （前端就是一只空气泡，用户不知道发生了什么）。与指派/提升、计划两条路径同一口径。
            if (string.IsNullOrWhiteSpace(accumulated) && attached == 0)
                await _hub.Value.AppendAgentContentAsync(context.GroupId, messageId, EmptyReplyFallback, runCt);
            await _hub.Value.EndAgentMessageAsync(context.GroupId, messageId, runCt);
            return new AgentInvocationResult(true, runId, null);
        }
        catch (OperationCanceledException)
        {
            await SafeEndAsync(context, messageId);
            return new AgentInvocationResult(false, runId, "AGENT_RUN_CANCELLED");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "智能体 {AgentId} 运行失败：run={RunId}", context.AgentId, runId);
            await SafeEndAsync(context, messageId);
            await _hub.Value.BroadcastAsync(context.GroupId, new RunErrorEvent
            {
                GroupId = context.GroupId,
                ErrorCode = "AGENT_RUN_ERROR",
                Message = AgentGatewayHelpers.DescribeModelError(ex),
                Timestamp = _hub.Value.NowMs,
            });
            return new AgentInvocationResult(false, runId, "AGENT_RUN_ERROR");
        }
        finally
        {
            if (acquired) sessionLock.Release(); // 未获得锁时不 Release（避免 SemaphoreFullException）
            _activeRuns.TryRemove(runId, out _); // 运行结束 / 取消：注销停止能力
            SkillChainBuilder.Ambient.Value = prevChain; // 清理链构造器（恢复外层）
            ToolResultCollector.Ambient.Value = prevToolResults; // 恢复外层工具返回收集器
            await _hub.Value.BroadcastTypingAsync(new GroupTypingRequest
            {
                GroupId = context.GroupId,
                MemberId = context.AgentId,
                IsTyping = false,
            }, CancellationToken.None);
        }
    }

    /// <summary>
    /// 顺序可配的非兜底路由阶段分派（Step B）。按 <see cref="ExecutionOptions.ExecutionOrder"/> 遍历阶段，
    /// 每阶段在「平台开关开启 ∧ 未被子角色覆盖禁用 ∧ 语义就绪」时才处理，命中即走原分派方法返回其结果；
    /// 其余情况该阶段返回 null 交由下一阶段。全部阶段都未命中时返回 null，由 <see cref="InvokeCoreAsync"/>
    /// 落回普通流式兜底（恒在 <see cref="ExecutionOptions.ExecutionOrder"/> 最末）。
    /// 各阶段的语义判定条件与原硬编码 if 完全一致；默认顺序与开关值即现状。<br/>
    /// 语境沉默（Contextual &amp;&amp; ShouldSpeak false）不在此列：它不属于可编排阶段，仍由 <see cref="InvokeCoreAsync"/>
    /// 在兜底前统一判断（此处只分派语义要求 Mentioned 的组织化路由，天然非 Contextual）。
    /// </summary>
    private async Task<AgentInvocationResult?> DispatchRoutedStagesAsync(
        AgentInvocationContext context, AgentDefinition def, AgentTriggerMode effectiveMode, CancellationToken ct)
    {
        foreach (var token in _execution.ExecutionOrder)
        {
            switch (token)
            {
                // ① 桥接角色：不经本地大模型，以 AG-UI 协议对接外部 AG-UI 服务。
                case "bridge":
                    if (_execution.EnableBridge && def.DisableBridge is not true)
                    {
                        if (!string.IsNullOrWhiteSpace(def.BridgeEndpoint)
                            || !string.IsNullOrWhiteSpace(_options.AguiBridge?.Endpoint))
                            return await InvokeBridgeAsync(context, def, ct);
                    }
                    break;

                // ② 编排流水线（1.1）：配置了 Pipeline 即依次调用子智能体。
                case "pipeline":
                    if (_execution.EnablePipeline && def.Pipeline is { Count: > 0 })
                        return await InvokePipelineAsync(context, def, ct);
                    break;

                // ③ 角色交接（1.2）：RelayToAgentId 整轮委托（防自环 / 接力环，条件与原实现一致）。
                case "relay":
                    if (_execution.EnableRelay && def.DisableRelay is not true)
                    {
                        var relayTarget = def.RelayToAgentId;
                        if (!string.IsNullOrWhiteSpace(relayTarget) && relayTarget != def.AgentId
                            && _catalog.GetDefinition(relayTarget) is { RelayToAgentId: null or "" })
                            return await InvokeRelayAsync(context, def, relayTarget, ct);
                    }
                    break;

                // ④ 组织化路由（指派 / 提升 / 技能型计划编排）：仅 Mentioned 且存在指派 / 提升 / 协调才触发；
                //    否则 fallthrough 交由下一阶段（默认落到普通流式）。协调开关照旧读 _options.CoordinatorPlanning，
                //    isSkillPlanner 需配合本阶段开关才生效。
                case "org_route":
                    if (_execution.EnableOrgRoute && def.DisableOrgRoute is not true)
                    {
                        var isSkillPlanner = _options.CoordinatorPlanning
                            && def.SkillDefIds is { Count: > 0 } && !HasMountedOrgDeploy(def);
                        if (effectiveMode == AgentTriggerMode.Mentioned
                            && (def.AssignmentIds is { Count: > 0 }
                                || !string.IsNullOrWhiteSpace(def.EscalationAgentId)
                                || isSkillPlanner))
                            return await InvokeAssignmentEscalationAsync(context, ct);
                    }
                    break;

                // ⑤ 普通流式（streaming）：规范化保证其恒置最末，由 InvokeCoreAsync 兜底处理，此处从不命中。
                default:
                    break;
            }
        }
        return null;
    }

    /// <summary>
    /// 编排流水线（1.1）：按 <see cref="AgentDefinition.Pipeline"/> 的步骤<b>依次</b>调用子智能体，
    /// 把最终聚合文本作为本智能体对群的回复。确定性执行（不经本智能体模型规划），步骤输入自动级联。
    /// 各步骤由子智能体一次性 run 完成；最终结果聚合为群消息正文。
    /// </summary>
    private async Task<AgentInvocationResult> InvokePipelineAsync(AgentInvocationContext context, AgentDefinition def, CancellationToken ct)
    {
        var runId = "run_" + IdGenerator.NewId();
        _logger.LogInformation("智能体 {AgentId} 编排流水线运行：run={RunId} steps={Steps}", context.AgentId, runId, def.Pipeline!.Count);
        await _hub.Value.BroadcastTypingAsync(new GroupTypingRequest { GroupId = context.GroupId, MemberId = context.AgentId, IsTyping = true }, ct);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(InstallRunBudget(context, def).Run);
        var runCt = timeoutCts.Token;
        _activeRuns[runId] = new ActiveRun(timeoutCts, context.GroupId, context.AgentId, context.TriggerUserId);

        string? messageId = null;
        try
        {
            var started = await _hub.Value.PublishAgentMessageStartAsync(new AgentMessageStartInput
            {
                GroupId = context.GroupId,
                AgentId = context.AgentId,
                RunId = runId,
                TopicId = context.TopicId,
                ReplyToMessageId = context.TriggerMessageId,
                Mentions = [],
                MentionAll = false,
                Visibility = context.Visibility,
                VisibleMemberIds = context.VisibleMemberIds ?? [],
            }, runCt);
            messageId = started.MessageId;

            var input = await BuildUserMessageAsync(context, runCt); // 触发消息 + 群上下文（与普通路径一致）
            var sb = new System.Text.StringBuilder();
            foreach (var step in def.Pipeline!)
            {
                if (string.IsNullOrWhiteSpace(step.StepAgentId)) continue;
                var stepDef = _catalog.GetDefinition(step.StepAgentId);
                if (stepDef is null)
                    throw new AguiProtocolException(ErrorCodes.BadRequest, $"流水线步骤智能体未配置：{step.StepAgentId}");

                var child = _catalog.GetOrCreate(step.StepAgentId);
                var prompt = "你是步骤 " + (stepDef.Nickname ?? step.StepAgentId) + "，请就以下请求给出你的专业答复。\n\n"
                    + (string.IsNullOrWhiteSpace(step.Prompt) ? "" : "本步要求：" + step.Prompt + "\n\n")
                    + "用户请求：\n" + input + "\n\n"
                    + (sb.Length > 0 ? "前序步骤已产出（可参考）：\n" + sb + "\n\n" : "")
                    + "只输出本步结论，不要复述前序内容。";
                // 子智能体在干净会话上一次 run（不继承本群模型会话），产出该步文本。
                // 关键：把 ambient 上下文切到<b>本步骤子智能体</b>——MemoryContextProvider 据此注入
                // 它自己的知识库/记忆（否则按宿主检索，绑知识库的子智能体会丢上下文）。
                var childSession = await child.CreateSessionAsync(runCt);
                var prevAmbient = AgentGateway.AmbientContext.Value;
                AgentGateway.AmbientContext.Value = context with { AgentId = step.StepAgentId, AgentNickname = stepDef.Nickname ?? step.StepAgentId };
                string stepOut;
                try
                {
                    var resp = await child.RunAsync([new ChatMessage(ChatRole.User, prompt)], childSession, null, runCt);
                    stepOut = string.IsNullOrWhiteSpace(resp.Text) ? "（子智能体未返回内容）" : resp.Text.Trim();
                }
                finally
                {
                    AgentGateway.AmbientContext.Value = prevAmbient;
                }
                sb.Append("【").Append(stepDef.Nickname ?? step.StepAgentId).Append("】").AppendLine(stepOut).AppendLine();
                _logger.LogInformation("流水线步骤完成：agent={AgentId} step={StepAgent} run={RunId}", context.AgentId, step.StepAgentId, runId);

                // 下一步以本步输出为输入（前序结果级联）
                input = stepOut;
            }

            // 空正文兜底：`sb.Length == 0` 拦不住**空白串**（下游 Trim 之后就是空消息）
            var finalText = sb.ToString().Trim();
            if (finalText.Length == 0) finalText = "（流水线未产出内容）";
            finalText = UnwrapCoordinationAnswer(finalText); // 防内部协调 JSON 泄漏到用户
            if (string.IsNullOrWhiteSpace(finalText)) finalText = EmptyReplyFallback;
            foreach (var chunk in AgentGatewayHelpers.ChunkReply(finalText, 160)) // 分块广播，前端可像流式一样渐进渲染
                await _hub.Value.AppendAgentContentAsync(context.GroupId, messageId, chunk, runCt);

            await _hub.Value.EndAgentMessageAsync(context.GroupId, messageId, runCt);
            return new AgentInvocationResult(true, runId, null);
        }
        catch (OperationCanceledException)
        {
            await SafeEndAsync(context, messageId);
            return new AgentInvocationResult(false, runId, "AGENT_RUN_CANCELLED");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "编排流水线运行失败：agent={AgentId} run={RunId}", context.AgentId, runId);
            await SafeEndAsync(context, messageId);
            try
            {
                await _hub.Value.BroadcastAsync(context.GroupId, new RunErrorEvent
                {
                    GroupId = context.GroupId,
                    ErrorCode = "AGENT_RUN_ERROR",
                    Message = "流水线执行失败：" + ex.Message,
                    Timestamp = _hub.Value.NowMs,
                }, ct: CancellationToken.None);
            }
            catch { /* 广播失败不影响返回 */ }
            return new AgentInvocationResult(false, runId, "AGENT_RUN_ERROR");
        }
        finally
        {
            _activeRuns.TryRemove(runId, out _);
            await _hub.Value.BroadcastTypingAsync(new GroupTypingRequest
            {
                GroupId = context.GroupId,
                MemberId = context.AgentId,
                IsTyping = false,
            }, CancellationToken.None);
        }
    }

    /// <summary>
    /// 角色交接（1.2）：整轮把触发委托给 <paramref name="relayAgentId"/>，中继智能体运行一次，
    /// 其回复即作为本智能体对群的答复流式回灌（「由 X 代答」的角色别名）。
    /// </summary>
    private async Task<AgentInvocationResult> InvokeRelayAsync(AgentInvocationContext context, AgentDefinition def, string relayAgentId, CancellationToken ct)
    {
        var relayDef = _catalog.GetDefinition(relayAgentId);
        if (relayDef is null)
            throw new AguiProtocolException(ErrorCodes.BadRequest, $"交接目标智能体未配置：{relayAgentId}");
        var runId = "run_" + IdGenerator.NewId();
        _logger.LogInformation("智能体 {AgentId} 角色交接：整轮委托给 {Relay}（run={RunId}）", context.AgentId, relayAgentId, runId);
        await _hub.Value.BroadcastTypingAsync(new GroupTypingRequest { GroupId = context.GroupId, MemberId = context.AgentId, IsTyping = true }, ct);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(InstallRunBudget(context, def).Run);
        var runCt = timeoutCts.Token;
        _activeRuns[runId] = new ActiveRun(timeoutCts, context.GroupId, context.AgentId, context.TriggerUserId);
        string? messageId = null;
        try
        {
            var started = await _hub.Value.PublishAgentMessageStartAsync(new AgentMessageStartInput
            {
                GroupId = context.GroupId,
                AgentId = context.AgentId,
                RunId = runId,
                TopicId = context.TopicId,
                ReplyToMessageId = context.TriggerMessageId,
                Mentions = [], MentionAll = false,
                Visibility = context.Visibility,
                VisibleMemberIds = context.VisibleMemberIds ?? [],
            }, runCt);
            messageId = started.MessageId;

            var input = await BuildUserMessageAsync(context, runCt);
            var relay = _catalog.GetOrCreate(relayAgentId);
            var prompt = "你正被「" + (def.Nickname ?? context.AgentId) + "」整轮交接代答。请就以下用户请求直接给出你的专业答复：\n\n" + input;
            var session = await relay.CreateSessionAsync(runCt);
            // 关键：交接代答时把 ambient 上下文切到<b>被交接方</b>，使其能检索自己的知识库/记忆
            var prevAmbient = AgentGateway.AmbientContext.Value;
            AgentGateway.AmbientContext.Value = context with { AgentId = relayAgentId, AgentNickname = relayDef.Nickname ?? relayAgentId };
            string text;
            try
            {
                var resp = await relay.RunAsync([new ChatMessage(ChatRole.User, prompt)], session, null, runCt);
                text = string.IsNullOrWhiteSpace(resp.Text) ? "（交接对象未返回内容）" : resp.Text.Trim();
            }
            finally
            {
                AgentGateway.AmbientContext.Value = prevAmbient;
            }
            foreach (var chunk in AgentGatewayHelpers.ChunkReply(UnwrapCoordinationAnswer(text), 160)) // 防内部协调 JSON 泄漏到用户
                await _hub.Value.AppendAgentContentAsync(context.GroupId, messageId, chunk, runCt);

            await _hub.Value.EndAgentMessageAsync(context.GroupId, messageId, runCt);
            return new AgentInvocationResult(true, runId, null);
        }
        catch (OperationCanceledException)
        {
            await SafeEndAsync(context, messageId);
            return new AgentInvocationResult(false, runId, "AGENT_RUN_CANCELLED");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "角色交接运行失败：agent={AgentId} relay={Relay} run={RunId}", context.AgentId, relayAgentId, runId);
            await SafeEndAsync(context, messageId);
            try
            {
                await _hub.Value.BroadcastAsync(context.GroupId, new RunErrorEvent
                {
                    GroupId = context.GroupId, ErrorCode = "AGENT_RUN_ERROR",
                    Message = "角色交接失败：" + ex.Message, Timestamp = _hub.Value.NowMs,
                }, ct: CancellationToken.None);
            }
            catch { /* 广播失败不影响返回 */ }
            return new AgentInvocationResult(false, runId, "AGENT_RUN_ERROR");
        }
        finally
        {
            _activeRuns.TryRemove(runId, out _);
            await _hub.Value.BroadcastTypingAsync(new GroupTypingRequest { GroupId = context.GroupId, MemberId = context.AgentId, IsTyping = false }, CancellationToken.None);
        }
    }

    /// <summary>
    /// 任务指派 / 问题提升（组织化路由）：被显式 @ 的宿主（或其下游被指派/提升）进入该路由。
    /// 优先按本数字员工系统提示词推断「该不该我答」：该我答 → 直接答；不该我答且白名单有合适 → 向下<b>任务指派</b>；
    /// 无合适指派对象且配了提升目标 → 向上<b>问题提升</b>；再无解 → 回答「不能解决」。
    /// 回复统一以原始 @ 宿主身份发出；含深度上限与环路保护（A→B→A 不环回）。
    /// </summary>
    private async Task<AgentInvocationResult> InvokeAssignmentEscalationAsync(AgentInvocationContext context, CancellationToken ct)
    {
        // 先做纯路由决策（不产生任何事件）。若决策结果是「本级该自答、但它挂了可执行技能」，
        // 不能在这里用轻量路径草草回答：必须 return null 落回普通流式，让它拿到工具 / 审批 / 产物回档。
        // 实测踩到：编排出的团队里「文案交付排版员」挂了 docx_report，用户带附件要 Word，
        // 轻量路径只回了一句表态、技能从未被调用、产物也拿不到下载。
        if (await ShouldDelegateRouteToStreamingAsync(context, ct))
        {
            _logger.LogInformation("组织化路由交由完整执行（岗位挂有可执行技能且判定应自答）：agent={AgentId}", context.AgentId);
            return null!;
        }

        var runId = "run_" + IdGenerator.NewId();
        _logger.LogInformation("智能体 {AgentId} 进入指派/提升路由（run={RunId}，group={GroupId}）", context.AgentId, runId, context.GroupId);
        await _hub.Value.BroadcastTypingAsync(new GroupTypingRequest { GroupId = context.GroupId, MemberId = context.AgentId, IsTyping = true }, ct);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // 指派/提升是一条多阶段流水线：计划 → 逐步执行（多次模型调用）→ 递归补查 → 交付兑底。
        // 用单次流式的预算盖整条链会中途截断：实测走主管时，计划+递归已经把基准预算用完，
        // 轮到“真正出文件”的那一步刚好耗尽。这里在“复杂度自适应预算”之上再给整条链 ×3 的总预算
        // （交付兑底内部还会再开一份自己的预算），仍受 ComplexityTimeouts:MaxRunTimeoutMinutes 夹紧。
        timeoutCts.CancelAfter(InstallRunBudget(context, _catalog.GetDefinition(context.AgentId)).Scale(3));
        var runCt = timeoutCts.Token;
        _activeRuns[runId] = new ActiveRun(timeoutCts, context.GroupId, context.AgentId, context.TriggerUserId);
        string? messageId = null;
        try
        {
            var input = await BuildUserMessageAsync(context, runCt);
            var visited = new HashSet<string>(StringComparer.Ordinal);
            string? finalText = null;
            var hops = new List<ChainNode>();
            var outcome = RouteOutcome.CannotSolve;

            // 先尝试构建编排计划（只规划、不执行）；拿到计划则进入「随消息流逐项激活」；否则回退到递归指派
            CoordinatedPlan? plan = null;
            if (_options.CoordinatorPlanning && _catalog.GetDefinition(context.AgentId) is { } coordDef)
                plan = await BuildCoordinatedPlanAsync(context, coordDef, input, runCt);
            if (plan is null)
            {
                (outcome, finalText, hops) = await ResolveRouteAsync(context, context.AgentId, input, visited, depth: 1, runCt);
            }
            else
            {
                outcome = RouteOutcome.Answer;
            }
            if (outcome == RouteOutcome.CannotSolve)
                finalText = "（该问题不在我可解决的范围内，且没有可指派的同事或可提升的上级，暂时无法解决。请直接联系处理该问题的负责人。）";

            // 交付物兜底：用户明确要文件（Word/excel/...），而整条指派/提升链只产出了文本、没生成文件时，
            // 找一个挂了对应技能的同事真正做一次。
            //
            // 为什么需要：实测踩到 —— 用户带附件要 Word，链路把活派来派去（排版员→总监→组长→写手），
            // 最后写手只把稿子**当文本贴了出来**，没人调 docx_* 技能，用户拿不到文件。
            // “任务被派来派去却没人负责最终交付物”是组织协作的典型断点。
            var wantDelivery = plan is null && finalText is { Length: > 0 } && !string.IsNullOrWhiteSpace(context.Content)
                && WantedDeliverable(context.Content) is not null;

            var started = await _hub.Value.PublishAgentMessageStartAsync(new AgentMessageStartInput
            {
                GroupId = context.GroupId,
                AgentId = context.AgentId,
                RunId = runId,
                TopicId = context.TopicId,
                ReplyToMessageId = context.TriggerMessageId,
                Mentions = [], MentionAll = false,
                Visibility = context.Visibility,
                VisibleMemberIds = context.VisibleMemberIds ?? [],
            }, runCt);
            messageId = started.MessageId;

            if (plan is not null)
            {
                // 编排计划：随消息流逐项激活 & 逐条点亮计划卡（TEXT_MESSAGE_PLAN 前端渲染）
                var planOutcome = await ExecuteCoordinatedPlanAsync(context, plan, messageId, runCt);
                // 计划跳过了文档生成技能（它的入参是结构化 JSON，必须由模型当工具构造）：
                // 这里补一次交付兑底 —— 完整流式，让模型自己调技能并回档产物。
                // 不这么做就会退化为“计划跑了、用户仍拿不到文件”。
                if (planOutcome.NeedsDelivery)
                {
                    // 传外层原始 ct（而非 runCt）：runCt 的时间预算可能已被计划/递归消耗待尽，
                    // 交付兑底内部会基于它另开一份新预算。
                    // 同时把计划内已产出的内容一并传过去：交付岗直接拿它当正文素材，
                    // 不必从用户那句原始请求从零重写（否则前面各岗位的产出全白做）。
                    // 交付物类型优先听用户那句话；用户没提格式词时（如“希望有一些插图”）
                    // 回退用计划点名的文件技能，否则会直接放弃交付 —— 实测就是“什么都没给”。
                    var delivery = await TrySatisfyDeliveryAsync(context, input, hops, ct, runId, messageId,
                        planOutcome.Collected, planOutcome.DeliverySkillId);
                    if (delivery.MessageId is { } pmid) messageId = pmid;
                    if (delivery.AwaitingInteraction)
                    {
                        _logger.LogInformation("编排计划因交付兑底中断等待交互：run={RunId} interruptTarget={Target}", runId, context.TriggerUserId);
                        return new AgentInvocationResult(false, runId, "AGENT_AWAITING_INTERACTION");
                    }
                    // 交付根本没发生（没认出交付物 / 找不到能做的岗位 / 空异常）：
                    // 这时才把计划侧那段“为什么没内容”的说明补上，否则用户只看到一条空消息。
                    if (ShouldAppendPlanText(delivery.AwaitingInteraction, delivery.Handled, planOutcome.PlanText))
                    {
                        _logger.LogWarning("交付兑底未接手，补发计划说明以免空消息：run={RunId} len={Len}",
                            runId, planOutcome.PlanText.Length);
                        foreach (var chunk in AgentGatewayHelpers.ChunkReply(planOutcome.PlanText, 160))
                            await _hub.Value.AppendAgentContentAsync(context.GroupId, messageId!, chunk, runCt);
                    }
                }
            }
            else
            {
                // 非编排路径：链路可视化 + 前缀 + 直接方案
                RecordStandinChain(context, hops);
                var prefixNames = hops.Where(h => !string.IsNullOrWhiteSpace(h.AgentId)).Select(h => h.AgentNickname).ToList();
                foreach (var name in prefixNames)
                    await _hub.Value.AppendAgentContentAsync(context.GroupId, messageId, $"（{name} 代为处理）\n", runCt);

                var handled = false;
                if (wantDelivery)
                {
                    // 交付物兜底：交付岗直接产出文件（正文 + 下载卡片），不再贴一遍链路里的过程稿，
                    // 避免用户看到两段互相矛盾的内容。兜底失败则回退到原文本回答。
                    // 同样传外层原始 ct（见上），让交付拿到自己的完整时间预算。
                    var delivery = await TrySatisfyDeliveryAsync(context, input, hops, ct, runId, messageId);
                    if (delivery.MessageId is { } mid) messageId = mid;
                    handled = delivery.Handled;
                    if (delivery.AwaitingInteraction)
                    {
                        // 已下交互卡：消息保持开启，等用户决策后由 ResumeRunAsync 继续追加最终结果
                        _logger.LogInformation("指派/提升路由因交付物兜底中断等待交互：run={RunId} interruptTarget={Target}", runId, context.TriggerUserId);
                        return new AgentInvocationResult(false, runId, "AGENT_AWAITING_INTERACTION");
                    }
                }

                // 轻量自答没拿到正文、而本岗挂着可执行技能 → 极可能是“这一步本该调工具/技能”
                // （需审批的技能在非流式 run 里只会返回一个审批请求，而不是正文）。
                // 轻量路径不处理审批，继续下去只会给用户一句「（X 代为处理）」；别把不可能完成的工作
                // 留在没工具的路径上，改走一次**在途完整流式**（与交付兜底同一条管道：
                // 工具调用 / 审批卡 / 产物回档都齐），必要时交回 ResumeRunAsync 继续。
                if (!handled && string.IsNullOrWhiteSpace(finalText)
                    && _catalog.GetDefinition(context.AgentId) is { } selfDef && HasExecutableSkill(selfDef))
                {
                    _logger.LogWarning("指派/提升轻量自答未产出正文，改走完整流式：agent={AgentId} run={RunId}", context.AgentId, runId);
                    var (interrupt, streamed) = await RunDeliveryStreamAsync(context, selfDef, runId, messageId, runCt);
                    if (interrupt is not null)
                    {
                        _logger.LogInformation("轻量自答补跑因审批中断等待交互：run={RunId} interruptTarget={Target}", runId, context.TriggerUserId);
                        return new AgentInvocationResult(false, runId, "AGENT_AWAITING_INTERACTION");
                    }
                    finalText = streamed;
                }

                // 空正文兜底（两道）：① `??=` 只兜 null，**兜不住空串/空白**；
                // ② UnwrapCoordinationAnswer 把内部 JSON 包壳剥完后也可能变空。
                // 两道都不做就会出现“只有（X 代为处理）的静默消息”——实测踩到过。
                if (string.IsNullOrWhiteSpace(finalText)) finalText = EmptyReplyFallback;
                finalText = UnwrapCoordinationAnswer(finalText); // 防内部协调 JSON 泄漏到用户
                if (string.IsNullOrWhiteSpace(finalText)) finalText = EmptyReplyFallback;
                var replyId = messageId!;
                foreach (var chunk in AgentGatewayHelpers.ChunkReply(finalText.Trim(), 160))
                    await _hub.Value.AppendAgentContentAsync(context.GroupId, replyId, chunk, runCt);
            }

            // 运行完成：产物回档挂在外层这条消息上（交付物兜底也已在其上追加）。
            // 收尾用独立短超时：计划 + 递归 + 交付可能已把 runCt 的预算用尽，
            // 不能因为“预算到点”就把已经生成好的产物丢掉（那样用户拿不到文件）。
            using var finalCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await AttachPublishedProductsAsync(context.GroupId, messageId!, finalText ?? "", finalCts.Token);
            await _hub.Value.EndAgentMessageAsync(context.GroupId, messageId!, finalCts.Token);
            return new AgentInvocationResult(true, runId, null);
        }
        catch (OperationCanceledException)
        {
            await SafeEndAsync(context, messageId);
            return new AgentInvocationResult(false, runId, "AGENT_RUN_CANCELLED");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "指派/提升路由运行失败：agent={AgentId} run={RunId}", context.AgentId, runId);
            await SafeEndAsync(context, messageId);
            try
            {
                await _hub.Value.BroadcastAsync(context.GroupId, new RunErrorEvent
                {
                    GroupId = context.GroupId, ErrorCode = "AGENT_RUN_ERROR",
                    Message = "指派/提升失败：" + ex.Message, Timestamp = _hub.Value.NowMs,
                }, ct: CancellationToken.None);
            }
            catch { /* 广播失败不影响返回 */ }
            return new AgentInvocationResult(false, runId, "AGENT_RUN_ERROR");
        }
        finally
        {
            _activeRuns.TryRemove(runId, out _);
            await _hub.Value.BroadcastTypingAsync(new GroupTypingRequest { GroupId = context.GroupId, MemberId = context.AgentId, IsTyping = false }, CancellationToken.None);
        }
    }

    // ---------- 确定性编排计划（Coordinator Plan）：问题 → 按组织架构/技能配置定计划 → 激活对应员工与能力执行 ----------

    // 最多纳入计划的清单项 / 步骤数（防配置病态深链 / 打爆模型时长）；
    // 运行时取自 _execution.CoordinatorPlanMaxItems / _execution.CoordinatorPlanMaxSteps。

    /// <summary>
    /// 构建一张编排计划（只规划、不执行）：把问题、可指派的组织下属、可调用技能显式列给路由模型，
    /// 由它产出结构化步骤（谁先、调什么、如何汇总）；返回 null = 无需/无法编排 → 调用方回退到递归指派。
    /// 计划随后由 <see cref="ExecuteCoordinatedPlanAsync"/> 随消息流逐项激活（并逐条点亮计划卡）。
    /// </summary>
    private async Task<CoordinatedPlan?> BuildCoordinatedPlanAsync(
        AgentInvocationContext context, AgentDefinition root, string input, CancellationToken ct)
    {
        try
        {
            // 清单：可指派的组织下属（AssignmentIds 递归 BFS + 子代理 Skills）+ 可调用技能（SkillDefIds 技能库）
            var reached = new List<AgentDefinition>();
            var seen = new HashSet<string>(StringComparer.Ordinal) { root.AgentId };
            var queue = new Queue<AgentDefinition>();
            foreach (var id in root.AssignmentIds ?? [])
                if (_catalog.GetDefinition(id) is { } d) queue.Enqueue(d);
            foreach (var s in root.Skills ?? [])
                if (_catalog.GetDefinition(s.TargetAgentId) is { } d) queue.Enqueue(d);
            while (queue.Count > 0 && reached.Count < _execution.CoordinatorPlanMaxItems)
            {
                var d = queue.Dequeue();
                if (!seen.Add(d.AgentId)) continue;
                if (d.IsSkillTarget) continue;
                reached.Add(d);
                foreach (var id in d.AssignmentIds ?? [])
                    if (_catalog.GetDefinition(id) is { } sub) queue.Enqueue(sub);
            }
            var catalog = _skillCatalog.Value;
            var skills = new Dictionary<string, AgentSkillDefinition>(StringComparer.Ordinal);
            void Collect(AgentDefinition? d)
            {
                if (d is null || catalog is null) return;
                foreach (var refId in d.SkillDefIds ?? [])
                    // OrgDeploy（受控组织落库）是可对话 function-call 的部署动作，不是可批跑的排查技能：不进计划 inventory、不投给 SkillRunner。
                    if (catalog.Get(refId) is { } def && def.Kind != AgentSkillKind.Org_deploy && !skills.ContainsKey(def.SkillId)) skills[def.SkillId] = def;
            }
            Collect(root);
            foreach (var r in reached) Collect(r);

            if (reached.Count == 0 && skills.Count == 0)
                return null; // 无可指派 / 可调用

            var steps = await PlanCoordinatedAsync(context, root, input, reached, skills.Values.ToList(), ct);
            if (steps is null || steps.Count == 0) return null;
            return new CoordinatedPlan(steps.Take(_execution.CoordinatorPlanMaxSteps).ToList(), reached, skills, input);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "构建编排计划失败（已回退到递归指派）：agent={AgentId}", root.AgentId);
            return null;
        }
    }

    /// <summary>
    /// 随消息流逐项激活编排计划：先广播“全部待执行”的计划卡，再逐条执行（派下属 / 调技能），
    /// 每完成一条即把该步标记完成并<b>重新广播计划卡</b>（前端逐条点亮），中间步骤产出作为下一步输入；
    /// 最后综合各步给最终答复并广播计划完成。任一异常都优雅收尾（不再阻断消息）。
    /// </summary>
    /// <returns>
    /// 本次计划的执行情况：是否需要外层再跑一次交付兑底，以及计划内实际产出的内容。
    /// 后者供交付兑底当正文素材（否则它只能从用户原始请求从零重写，等于把前面各岗位的产出全丢掉）。
    /// “需要兑底”指的是计划跳过了文档生成类技能——这类技能的入参是结构化 JSON，必须由模型当工具调用
    /// 才能构造，计划路径按纯文本直接调它只会塑出空壳文档（实测：Word 只有标题）。
    /// </returns>
    private async Task<CoordinatedPlanOutcome> ExecuteCoordinatedPlanAsync(AgentInvocationContext context, CoordinatedPlan plan, string messageId, CancellationToken ct)
    {
        var root = _catalog.GetDefinition(context.AgentId);
        if (root is null) return new CoordinatedPlanOutcome(false, "", null, "");
        var gid = context.GroupId;
        var needsDelivery = false; // 计划里跳过了交付类技能 → 交给外层交付兑底
        var collectedOut = "";     // 计划内已产出的内容（交付兑底的素材）
        var planTextOut = "";       // 本计划本该展示的正文（见下方：需交付时暂不发）
        // 计划里被跳过的文档生成技能 id：外层交付兑底据此确定要出哪种文件。
        // 必要性（实测）：用户接着说“希望有一些插图”这类话时，句子里没有任何格式词，
        // 而交付判断原先只看当前这句 → 判不出交付物 → 直接放弃；上一步说“我希望ppt是绿色的”能成功，
        // 只因句子里恰好有“ppt”。计划已经点名了要用哪个文件技能，就该听计划的。
        string? skippedDeliverySkill = null;

        // 计划暂停/继续闸门：先登记（端点收到暂停请求时能定位到本计划），随执行结束/异常移除
        var planGate = _planControl.Value?.Begin(messageId, gid, context.AgentId, context.TriggerUserId);
        try
        {
        // 1) 构造展示步骤（即时生效步 + 最终综合步），全部“待执行”
        var display = new List<PlanStepInfo>();
        foreach (var step in plan.Steps)
        {
            if (step.Action == "dispatch")
            {
                var nick = _catalog.GetDefinition(step.Target)?.Nickname ?? step.Target;
                display.Add(new PlanStepInfo { Id = display.Count + 1, Text = "为「" + nick + "」分配工作" + (string.IsNullOrWhiteSpace(step.Note) ? "" : "：" + step.Note), Done = false });
            }
            else if (step.Action == "skill")
            {
                var name = plan.Skills.TryGetValue(step.Target, out var sk) ? (sk.Name ?? sk.SkillId) : step.Target;
                display.Add(new PlanStepInfo { Id = display.Count + 1, Text = "调用技能「" + name + "」" + (string.IsNullOrWhiteSpace(step.Note) ? "" : "：" + step.Note), Done = false });
            }
        }
        var finalStep = new PlanStepInfo { Id = display.Count + 1, Text = "综合各步结果并给出最终答复", Done = false };
        display.Add(finalStep);

        await BroadcastPlanAsync(gid, messageId, display, ct);

        // 2) 分拣：客户端执行技能（ExecutionLocation=Client，需本机执行）与非客户端步骤（dispatch / 服务端技能）。
        //    客户端技能统一合并成「本机一键执行全部」批处理（一次确认，逐个执行、逐条点亮），
        //    其余步骤照旧循序执行、结果级联。
        // 累计缓冲：每一步产出的摘要。**千万不要 Clear**——它要供后续步骤参考（“前序已产出”），
        // 也是“未汇总出最终文本”时的兜底展示内容。
        // 实测踩到：原先是单槽（每步 Clear 后重写），于是最后一步没产出时，
        // 先前各岗位的成果全被丢掉，兜底文案宣称“已收集各岗位结果”却一个字都没有。
        var sb = new StringBuilder();
        // 被跳过的步骤与原因：兜底时要如实说明跳了什么、为什么，不要再笼统道歉
        var skipNotes = new List<string>();
        // 真正跑过（指派 / 技能 / 批量客户端技能）的步骤数：用于兜底时区分
        // “一步都没跑”与“跑了但没有任何可展示产出”，不再写死“已收集各岗位结果”
        var stepsRan = 0;
        var working = plan.Input;
        var hops = new List<ChainNode>();

        void Remember(string label, string? content)
        {
            if (string.IsNullOrWhiteSpace(content)) return;
            if (sb.Length > 0) sb.Append("\n\n");
            sb.Append("【").Append(label).Append("】\n").Append(content!.Trim());
        }

        // 拼进 prompt 的“前序”要有上限（累计后会变长，不能无限撑大下游提示）
        static string Tail(string s, int max) => s.Length <= max ? s : "…（前文从前略）\n" + s.Substring(s.Length - max);
        // 本问内已执行能力（计划阶段）全局去重：同一技能/员工在一答里只真正执行一次
        var capExecuted = new HashSet<string>(StringComparer.Ordinal);
        var clientSteps = new SortedDictionary<int, BatchClientItem>(); // planIndex(展示索) → 批量项
        for (var i = 0; i < plan.Steps.Count; i++)
        {
            var st = plan.Steps[i];
            if (st.Action != "skill" || !plan.Skills.TryGetValue(st.Target, out var csk)
                || csk.ExecutionLocation != AgentSkillExecutionLocation.Client) continue;
            // 客户端执行的文档生成技能同样不在此直接跑（同样是结构化 JSON 入参）：
            // 留给交付兑底走完整流式，让模型构造参数后经本机桥执行。
            if (AgentGatewayHelpers.IsDocumentGenerator(csk))
            {
                needsDelivery = true;
                skippedDeliverySkill ??= csk.SkillId;
                skipNotes.Add($"第{i + 1}步「{csk.Name ?? csk.SkillId}」：文档生成技能不在此阶段执行，改由交付环节直接生成文件");
                continue;
            }
            // 同一技能在计划里出现多次 → 只保留第一次，避免重复执行
            if (!capExecuted.Add(csk.SkillId)) continue;
            var cq = working;
            if (AgentGatewayHelpers.SkillRequiredInputs(csk).Contains("query", StringComparer.Ordinal))
            {
                var clean = AgentGatewayHelpers.ExtractCleanValueForSkill(working);
                if (!string.IsNullOrWhiteSpace(clean)) cq = clean;
            }
            clientSteps[i] = new BatchClientItem(csk.SkillId, csk.Name ?? csk.SkillId, EffectiveClientRunner(csk) ?? "", cq, i);
        }

        // 3) 逐项执行 dispatch / 服务端技能（跳过客户端技能，留到批量阶段）& 按原顺序点亮
        for (var si = 0; si < plan.Steps.Count; si++)
        {
            // 步骤边界：用户暂停过则挂起等待「继续」，恢复后接着执行剩余步骤
            await PausePlanIfRequestedAsync(planGate, gid, messageId, display, ct);
            var step = plan.Steps[si];
            if (clientSteps.ContainsKey(si)) continue; // 客户端技能统一在批量阶段执行
            if (step.Action == "dispatch")
            {
                if (_catalog.GetDefinition(step.Target) is not { } target
                    || !plan.Reached.Any(r => r.AgentId == step.Target))
                {
                    skipNotes.Add($"第{si + 1}步 指派：目标员工不可用，或不在本计划可指派范围内");
                    continue;
                }
                // 同一员工在计划里出现多次 → 只指派一次，后续复用其结果
                if (!capExecuted.Add("agent:" + step.Target))
                {
                    skipNotes.Add($"第{si + 1}步 指派「{target.Nickname ?? step.Target}」：同一员工本轮已指派过，直接复用其前序结果");
                    continue;
                }
                var child = _catalog.GetOrCreate(step.Target);
                var prompt = "你正被「" + (target.Nickname ?? step.Target) + "」指派处理，请就以下请求给出你的专业结论。\n\n问题：\n" + working
                    + (sb.Length > 0 ? "\n\n前序已产出（可参考）：\n" + Tail(sb.ToString(), 6000) : "")
                    + "\n\n只输出本步结论，不要复述前序内容。";
                var session = await child.CreateSessionAsync(ct);
                var prev = AgentGateway.AmbientContext.Value;
                AgentGateway.AmbientContext.Value = context with { AgentId = target.AgentId, AgentNickname = target.Nickname ?? target.AgentId };
                try
                {
                    var resp = await child.RunAsync([new ChatMessage(ChatRole.User, prompt)], session, null, ct);
                    stepsRan++;
                    var stepOut = string.IsNullOrWhiteSpace(resp.Text) ? "（未返回内容）" : resp.Text.Trim();
                    if (!hops.Any(h => h.AgentId == step.Target))
                        hops.Add(new ChainNode { Kind = "assignment", AgentId = step.Target, AgentNickname = target.Nickname ?? step.Target, Query = AgentGatewayHelpers.TruncateForChain(working), Result = AgentGatewayHelpers.TruncateForChain(stepOut) });
                    Remember(target.Nickname ?? step.Target, stepOut);
                    working = stepOut;
                }
                finally { AgentGateway.AmbientContext.Value = prev; }
                if (si < display.Count) display[si] = new PlanStepInfo { Id = display[si].Id, Text = display[si].Text, Done = true };
                await BroadcastPlanAsync(gid, messageId, display, ct);
            }
            else if (step.Action == "skill")
            {
                if (!plan.Skills.TryGetValue(step.Target, out var skill))
                {
                    skipNotes.Add($"第{si + 1}步 技能调用：计划里没有该技能的定义");
                    continue;
                }
                // 重拾：OrgDeploy（受控落库）不是可批跑技能 —— 万一命中也不投给 SkillRunner（防御性跳过）
                if (skill.Kind == AgentSkillKind.Org_deploy)
                {
                    skipNotes.Add($"第{si + 1}步「{skill.Name ?? skill.SkillId}」：落库类技能不在本阶段执行");
                    continue;
                }
                // 文档生成类技能（docx_* / md_to_docx / xlsx_* …）：**不能在计划路径里直接调**。
                // 它们的入参是结构化 JSON（{title, sections:[…]}），必须由模型当工具调用构造；
                // 计划路径只会把一段纯文本（群上下文 / 平台前言）塑给它 → JsonReaderException 或空壳文档
                // （实测：用户拿到只有标题的 Word）。这里跳过，标记为“需交付兑底”，
                // 由外层走完整流式让模型自己构造 JSON 调技能。
                if (AgentGatewayHelpers.IsDocumentGenerator(skill))
                {
                    needsDelivery = true;
                    skippedDeliverySkill ??= skill.SkillId;
                    skipNotes.Add($"第{si + 1}步「{skill.Name ?? skill.SkillId}」：文档生成技能不在此阶段执行，改由交付环节直接生成文件");
                    _logger.LogInformation("计划步骤为文档生成技能，改走交付兑底（避免纯文本塑入）：skill={SkillId}", skill.SkillId);
                    if (si < display.Count) display[si] = new PlanStepInfo { Id = display[si].Id, Text = display[si].Text, Done = true };
                    await BroadcastPlanAsync(gid, messageId, display, ct);
                    continue;
                }
                // 同一服务端技能在计划里出现多次 → 只执行一次，后续复用其结果
                if (!capExecuted.Add(skill.SkillId))
                {
                    skipNotes.Add($"第{si + 1}步「{skill.Name ?? skill.SkillId}」：同一技能本轮已执行过，直接复用其前序结果");
                    continue;
                }
                var skillQuery = working;
                if (AgentGatewayHelpers.SkillRequiredInputs(skill).Contains("query", StringComparer.Ordinal))
                {
                    var clean = AgentGatewayHelpers.ExtractCleanValueForSkill(skillQuery);
                    if (!string.IsNullOrWhiteSpace(clean)) skillQuery = clean;
                }
                // 声明了 cleanInput 的技能（转换 / 排版类）：只要用户本次那句原文，
                // 不要把整段群上下文（历史对话 + 不可信边界包装）投给它 ——
                // 否则会把聊天记录当正文写进交付文档（实测踩到：导出 303 段的「文档」全是历史消息）。
                if (AgentGatewayHelpers.WantsCleanInput(skill))
                {
                    var raw = AgentGatewayHelpers.ExtractLatestUserUtterance(plan.Input);
                    if (!string.IsNullOrWhiteSpace(raw)) skillQuery = raw;
                }

                // 输入兑底（交付闭环）：转换 / 导出类技能拿到空输入 / 过薄输入时，绝不能就此生成空壳文件。
                //
                // 实测踩到两次：
                //   ① 计划把 docx_report 排在执笔岗之前，上游返回空，网关把字面量“（未返回内容）”传下去；
                //   ② docx_gongwen 收到的是“（请用中文回复，提问者消息以中文为主。）”——平台语言提示，
                //      它非空且超过 2 字，旧阈值放过了，结果技能报 JsonReaderException。
                // 这里统一用“过薄”判定；不达标就先试“从本群已产出内容里取最像正文的一段”，
                // 取不到则<b>不调技能</b>、如实播报原因（宁可没文件，也不给用户一个空壳）。
                if (AgentGatewayHelpers.RequiredUpstreamInputs(skill).Count > 0
                    && AgentGatewayHelpers.LooksTooThinForDelivery(skillQuery))
                {
                    var salvaged = FindUpstreamContent(context);
                    if (!string.IsNullOrWhiteSpace(salvaged))
                    {
                        skillQuery = salvaged;
                        _logger.LogInformation("交付技能收到空输入，已从群内上游产出兑底：skill={SkillId} len={Len}", skill.SkillId, salvaged.Length);
                    }
                    else
                    {
                        var why = $"（未能生成文件：没有可供转换的正文内容（上游未产出定稿），技能 {skill.SkillId} 未执行。请先在群里要素材/定稿，或把要排版的内容直接发给我。）";
                        _logger.LogWarning("交付技能无可用输入且群内无上游产出，已跳过执行：skill={SkillId}", skill.SkillId);
                        Remember(skill.Name ?? skill.SkillId, why);
                        working = why;
                        if (si < display.Count) display[si] = new PlanStepInfo { Id = display[si].Id, Text = display[si].Text, Done = true };
                        await BroadcastPlanAsync(gid, messageId, display, ct);
                        continue;
                    }
                }

                var res = await _catalog.RunSkillAsync(skill, skillQuery, ct);
                stepsRan++;
                _logger.LogInformation("编排计划激活技能：agent={AgentId} skill={SkillId} query={Q}", context.AgentId, skill.SkillId, AgentGatewayHelpers.TruncateForChain(skillQuery));
                // 编排路径的技能产物同样需要回档：技能返回值里的 produce_file 标记要入库为附件，
                // 否则「计划里调了 docx 技能、用户却拿不到文件」——这条路径不经过模型正文，
                // 产物只存在于 res 里，不处理就彻底丢了。
                await AttachSkillProducedFilesAsync(gid, messageId, res, ct);
                if (!hops.Any(h => h.AgentId == skill.SkillId))
                    hops.Add(new ChainNode { Kind = "skill", AgentId = skill.SkillId, AgentNickname = skill.Name ?? skill.SkillId, Query = AgentGatewayHelpers.TruncateForChain(skillQuery), Result = AgentGatewayHelpers.TruncateForChain(res) });
                Remember(skill.Name ?? skill.SkillId, res);
                working = res;
                if (si < display.Count) display[si] = new PlanStepInfo { Id = display[si].Id, Text = display[si].Text, Done = true };
                await BroadcastPlanAsync(gid, messageId, display, ct);
            }
        }

        // 4) 批量执行客户端技能（若有）
        // 4) 批量执行客户端技能（若有）：合并下发一张「本机一键执行全部」交互卡，前端逐个执行、逐条回传、逐条点亮
        await PausePlanIfRequestedAsync(planGate, gid, messageId, display, ct);
        if (clientSteps.Count > 0)
        {
            var results = await AwaitBatchClientExecAsync(context, gid, messageId, display, clientSteps.Values.ToList(), ct);
            foreach (var kv in clientSteps)
            {
                var idx = kv.Key;
                var item = kv.Value;
                var outText = (results is not null && results.TryGetValue(item.SkillId, out var r) && !string.IsNullOrWhiteSpace(r))
                    ? r : "（本机执行未返回结果 / 已取消）";
                if (results is not null && results.ContainsKey(item.SkillId)) stepsRan++;
                if (!hops.Any(h => h.AgentId == item.SkillId))
                    hops.Add(new ChainNode { Kind = "skill", AgentId = item.SkillId, AgentNickname = item.Name, Query = AgentGatewayHelpers.TruncateForChain(item.Query), Result = AgentGatewayHelpers.TruncateForChain(outText) });
                Remember(item.Name, outText);
                working = outText;
                if (idx < display.Count) display[idx] = new PlanStepInfo { Id = display[idx].Id, Text = display[idx].Text, Done = true };
                await BroadcastPlanAsync(gid, messageId, display, ct);
            }
        }

        // 5) 综合答复制止（计划卡步骤全部点亮，先标记完成）；用户可在此前暂停，避免计划一口气冲到最终答复
        await PausePlanIfRequestedAsync(planGate, gid, messageId, display, ct);
        display[^1] = new PlanStepInfo { Id = display[^1].Id, Text = display[^1].Text, Done = true };
        await BroadcastPlanAsync(gid, messageId, display, ct);

        // 链路可视化（已收集的计划展开阶段的调用链保留）
        RecordStandinChain(context, hops);

        // 6) 递归综合答复：模型基于已收集结果作答；若发现不足，主动补查（客户端技能批量确认 / 服务端技能 / 指派下属），
        //    循环直到信息充分才给最终结论，不会中途停下问用户要不要继续。
        //    已执行能力集合以计划里实际激活过的所有技能（含服务端技能）与已指派的员工 id 为种子，
        //    避免递归阶段再次拿同一技能/同一员工补查（“同一能力被调用两次”）。
        var ranSkills = new HashSet<string>(StringComparer.Ordinal);
        foreach (var ck in capExecuted)
        {
            if (ck.StartsWith("agent:", StringComparison.Ordinal)) ranSkills.Add(ck["agent:".Length..]);
            else ranSkills.Add(ck);
        }
        // 计划里跳过了文档生成技能（且这正是用户要的交付物）时，不再跑递归补查：
        //   ① 交付兑底才是真正产出文件的那一步，递归补查只会先耗掉时间预算（实测：轮到交付时预算已尽）；
        //   ② 递归阶段会把“已完成导出”之类的话写进群历史，反而让交付岗以为自己已经做完了、不再调工具。
        // 直接交外层走交付兑底，更确定、也更快。
        string final;
        if (needsDelivery)
        {
            _logger.LogInformation("计划已跳过文档生成技能且用户要文件，跳过递归补查、直接进入交付兑底");
            final = "";
        }
        else
        {
            final = await ExecuteRecursiveAnswerAsync(context, root, gid, messageId, plan.Input, sb.ToString(),
                ranSkills, ct) ?? "";
        }
        // 空正文兜底：若综合答复也没产出可展示文本，绝不留下"只有计划卡、正文空白"的消息。
        // 三级降级，且每级都要说实话：
        //   ① 有汇总文本 → 直接用；
        //   ② 无汇总但有计划内产出 → 把已收集的中间结果摊给用户；
        //   ③ 两者皆空 → 按实际执行情况说明（原先这里写死“已按计划收集了各岗位的结果”，
        //      而恰恰在一步都没跑时它也这么说，用户拿到一句空话却无从判断下一步怎么做）。
        var finalTrimmed = string.IsNullOrWhiteSpace(final) ? "" : final.Trim();
        var collected = sb.ToString().Trim();
        collectedOut = collected; // 交给交付兑底当素材（即使 final 为空也不能丢）
        string text;
        if (finalTrimmed.Length > 0)
        {
            text = finalTrimmed;
        }
        else if (collected.Length > 0)
        {
            _logger.LogInformation("无最终汇总文本，回退展示计划内产出：steps={Steps} ran={Ran} collectedLen={Len} needsDelivery={Needs}",
                plan.Steps.Count, stepsRan, collected.Length, needsDelivery);
            text = collected;
        }
        else
        {
            // 走到这里说明：既没有汇总文本，计划内也没留下任何产出。触发原因通常是每步都被跳过，
            // 或全部步骤执行后返回了空。原实现此处无任何日志，线上根本无法定位（已踩过）。
            _logger.LogWarning(
                "计划无任何可展示产出，进入如实兜底：steps={Steps} ran={Ran} needsDelivery={Needs} skips=[{Skips}] input={Input}",
                plan.Steps.Count, stepsRan, needsDelivery, string.Join(" | ", skipNotes),
                AgentGatewayHelpers.TruncateForChain(plan.Input));
            text = BuildNoOutputFallback(stepsRan, skipNotes);
        }
        if (string.IsNullOrWhiteSpace(text)) text = "（处理对象未返回内容）";
        text = UnwrapCoordinationAnswer(text); // 防御：若模型把内部 JSON 决策原样当回复，剥出 user-facing answer
        if (string.IsNullOrWhiteSpace(text.Trim()))
            text = "（本轮协作已执行，但未产出可直接展示的正文。可让我按计划分步重试，或换一种更明确的问法。当前不存在可作答的遗漏步骤。）";
        text = text.Trim();
        planTextOut = text;
        // 交给交付兑底收尾时不在这里发正文：交付成了就由它给正文＋下载卡片，
        // 而计划这段“什么都没跑”的说明叠在交付结果前面只会让用户困惑（实测就这么出现过）。
        // 因此把文本回传给调用方，由它在“交付真没发生”时再补上。
        if (!needsDelivery)
            foreach (var chunk in AgentGatewayHelpers.ChunkReply(text, 160))
                await _hub.Value.AppendAgentContentAsync(gid, messageId, chunk, ct);
        }
        finally
        {
            _planControl.Value?.End(messageId);
        }
        return new CoordinatedPlanOutcome(needsDelivery, collectedOut, skippedDeliverySkill, planTextOut);
    }

    /// <summary>编排计划执行结果：<paramref name="NeedsDelivery"/> 是否需要外层交付兑底；
    /// <paramref name="Collected"/> 计划内各步实际产出（带岗位/技能标签），供交付兑底当正文素材；
    /// <paramref name="DeliverySkillId"/> 计划里被跳过的文档生成技能（用于确定交付物类型）；
    /// <paramref name="PlanText"/> 本计划本该展示的正文（需交付时暂不发，由调用方在交付未发生时补发）。</summary>
    private readonly record struct CoordinatedPlanOutcome(
        bool NeedsDelivery, string Collected, string? DeliverySkillId, string PlanText);

    /// <summary>计划阶段既无汇总文本、又无任何计划内产出时的如实兜底文案。
    /// 区分“一步都没跑”与“跑了但没产出”，并列出被跳过的步骤及原因，
    /// 避免旧实现那句写死的“已按计划收集了各岗位的结果”在什么都没跑时误导用户。
    /// 测试钩子（internal）。</summary>
    internal static string BuildNoOutputFallback(int stepsRan, IReadOnlyList<string> skipNotes)
    {
        var sb = new StringBuilder();
        if (stepsRan == 0)
            sb.Append("（本轮计划里的步骤没有一步真正执行，因此没有可汇总的结果。）");
        else
            sb.Append("（本轮实际执行了 ").Append(stepsRan).Append(" 个步骤，但它们都没有返回可展示的内容。）");
        if (skipNotes.Count > 0)
        {
            sb.Append("\n\n跳过的步骤：");
            foreach (var n in skipNotes) sb.Append("\n- ").Append(n);
        }
        sb.Append("\n\n可以告诉我需要生成的具体内容（标题 / 要点 / 目标文档类型），或换个更明确的问法，我直接出成稿。");
        return sb.ToString();
    }

    /// <summary>交付兑底未能接手时，是否需要把计划侧那段说明补发给用户（否则消息是空的）。
    /// 断言这条不变式：交付已经给了用户可见结果 / 正在等审批 → 不补（避免两条自相矛盾的说明叠在一起）；
    /// 交付静默放过了 → 必须补。测试钩子（internal）。</summary>
    internal static bool ShouldAppendPlanText(bool awaitingInteraction, bool handled, string? planText)
        => !awaitingInteraction && !handled && !string.IsNullOrWhiteSpace(planText);

    /// <summary>步骤边界暂停闸门：网关在每步（含批量执行与综合答复）之前检查一次；
    /// 用户已暂停 → 广播带「已暂停」状态的计划卡并挂起，直到用户点「继续」才恢复后续步骤。</summary>
    private async Task PausePlanIfRequestedAsync(PlanGate? gate, string gid, string messageId,
        IReadOnlyList<PlanStepInfo> display, CancellationToken ct)
    {
        if (gate is null || !gate.IsPaused) return;
        try
        {
            await BroadcastPlanAsync(gid, messageId, display, ct,
                paused: true, triggerMemberId: gate.TriggerUserId);
            await gate.WaitWhilePausedAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // 用户停止生成 / 会话取消：不再继续步骤，交由上层收尾（闸门由 finally End 移除）
        }
    }

    private async Task BroadcastPlanAsync(string groupId, string messageId, IReadOnlyList<PlanStepInfo> steps, CancellationToken ct,
        bool paused = false, string? triggerMemberId = null)
    {
        try
        {
            await _hub.Value.BroadcastMessagePlanAsync(groupId, messageId, "执行计划", steps, ct,
                paused: paused, triggerMemberId: triggerMemberId);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "编排计划广播失败（已忽略）：group={GroupId}", groupId);
        }
    }

    /// <summary>
    /// 编排计划内「客户端技能」的批量执行：把多个需在本机执行的技能合并成一张「本机一键执行全部」交互卡
    /// （Kind=client_tool，ClientRunner 为要执行的技能数组 JSON），下发给触发者一次确认；前端逐个通过本机桥
    /// 执行并回传结果（<see cref="ResolveInteractionAsync"/> 写入 TCS）。
    /// 返回 skillId → 输出 的映射；取消 / 超时 / 未执行返回 null（调用方标记相应步骤为未返回）。
    /// 本方法阻塞等待前端回传（计划引擎在指派路径内运行，不持会话锁，可安全等待）。
    /// </summary>
    private async Task<Dictionary<string, string>?> AwaitBatchClientExecAsync(
        AgentInvocationContext context, string gid, string messageId,
        IReadOnlyList<PlanStepInfo> display, IReadOnlyList<BatchClientItem> items, CancellationToken ct)
    {
        // 内网隧道在线（平台级或逐员工桥）且<b>不需要确认</b>时，跳过前端「本机一键执行全部」交互卡，直接经隧道在桥所在主机逐个执行
        // 这些客户端 shell 技能并收集结果——与本机桥在真机上执行等价，前端无需点确认。
        // 需要确认（默认）时走下方交互卡：触发者批准后由 <see cref="ResolveInteractionAsync"/> 再经隧道执行。
        if (TunnelAvailable(context.AgentId, context.PreferredBridgeClient) && !_options.ClientToolTunnelRequireApproval)
        {
            var tunneled = new Dictionary<string, string>();
            var approvedIds = new List<string>();
            foreach (var it in items)
            {
                if (TryParseRunnerShell(it.ClientRunner, out var cmd, out var cwd, out var timeoutSec))
                {
                    var r = await ExecuteTunnelAsync(
                        context.AgentId, context.PreferredBridgeClient, cmd!, cwd, timeoutSec, it.Query,
                        ClientSkillTimeout(timeoutSec), ct);
                    tunneled[it.SkillId] = string.IsNullOrWhiteSpace(r) ? "（本机执行未返回结果 / 超时）" : r;
                }
                else
                {
                    tunneled[it.SkillId] = "（该技能非本机 shell，无法经隧道执行）";
                }
                approvedIds.Add(it.SkillId);
            }
            MarkSkillsApproved(context.ThreadId, context.AgentId, approvedIds);
            _logger.LogInformation("客户端技能批量经内网隧道执行（免确认）：agent={AgentId} count={Count}", context.AgentId, items.Count);
            return tunneled;
        }

        // 同一对话里用户已同意过的客户端技能：无需再次弹确认卡。内网隧道在线时直接经隧道执行取得结果并合并返回；
        // 只把“尚未同意过”的技能下发给前端卡片确认（减少重复确认次数）。
        var autoResults = new Dictionary<string, string>();
        List<BatchClientItem>? cardItems = null;
        if (context.ThreadId is { Length: > 0 } && TunnelAvailable(context.AgentId, context.PreferredBridgeClient))
        {
            foreach (var it in items)
            {
                if (IsSkillApproved(context.ThreadId, context.AgentId, it.SkillId)
                    && TryParseRunnerShell(it.ClientRunner, out var aCmd, out var aCwd, out var aTimeoutSec))
                {
                    var r = await ExecuteTunnelAsync(
                        context.AgentId, context.PreferredBridgeClient, aCmd!, aCwd, aTimeoutSec, it.Query,
                        ClientSkillTimeout(aTimeoutSec), ct);
                    autoResults[it.SkillId] = string.IsNullOrWhiteSpace(r) ? "（本机执行未返回结果 / 超时）" : r;
                }
                else
                {
                    (cardItems ??= new List<BatchClientItem>()).Add(it);
                }
            }
        }
        else
        {
            cardItems = items.ToList();
        }
        if (cardItems is null || cardItems.Count == 0)
            return autoResults; // 全部技能已在此前同意过且已在本机执行，无需卡片

        var interruptId = "interrupt_" + IdGenerator.NewId();
        var runId = "run_" + IdGenerator.NewId();
        var root = _catalog.GetDefinition(context.AgentId);
        var tcs = new TaskCompletionSource<(bool Ok, Dictionary<string, string>? Results)>(TaskCreationOptions.RunContinuationsAsynchronously);
        _batchClientExecWaits[interruptId] = new BatchClientExec(gid, messageId, context.AgentId, context.PreferredBridgeClient, context.TriggerUserId, cardItems, display.Count, tcs);

        // 批量交互卡要执行的全部技能（前端据此逐个执行；clientRunner 复用技能的 ClientRunner JSON）
        var payload = cardItems.Select(it => new
        {
            skillId = it.SkillId,
            name = it.Name,
            query = it.Query,
            runner = it.ClientRunner,
        }).ToList();
        try
        {
            await _hub.Value.BroadcastAsync(gid, new AgentInteractionRequestEvent
            {
                GroupId = gid,
                MessageId = messageId,
                ThreadId = context.ThreadId,
                RunId = runId,
                InterruptId = interruptId,
                ToolCallId = "batch_" + interruptId,
                ToolName = "本机一键执行全部",
                ToolArguments = null,
                Message = $"智能体「{root?.Nickname ?? context.AgentId}」请求你在本机执行 {cardItems.Count} 个客户端技能（可一次全部执行）。",
                Kind = "client_tool_batch",
                ClientRunner = JsonSerializer.Serialize(payload),
                TargetMemberId = context.TriggerUserId,
                Timestamp = _hub.Value.NowMs,
            }, ct: ct);
            ClientToolTrace.Write($"BATCH-INVOKE interrupt={interruptId} count={cardItems.Count} skills={string.Join(",", cardItems.Select(i => i.SkillId))}");
        }
        catch (Exception ex)
        {
            _batchClientExecWaits.TryRemove(interruptId, out _);
            _logger.LogWarning(ex, "批量客户端技能交互卡下发失败：group={GroupId}", gid);
            return autoResults.Count > 0 ? autoResults : null;
        }

        // 阻塞等待前端回传（带交互 TTL 上限兜底，超时视为未执行）
        try
        {
            var tcsDone = await tcs.Task.WaitAsync(TimeSpan.FromMilliseconds((long)_execution.InteractionTtlMinutes * 60_000), ct);
            _batchClientExecWaits.TryRemove(interruptId, out _);
            if (tcsDone.Ok && tcsDone.Results is not null)
            {
                // 记录本批已同意执行的客户端技能：同一对话里后续再次需要时免确认（经隧道直跑）
                MarkSkillsApproved(context.ThreadId, context.AgentId, cardItems.Select(c => c.SkillId));
                foreach (var kv in autoResults) tcsDone.Results.TryAdd(kv.Key, kv.Value);
                return tcsDone.Results;
            }
            return autoResults;
        }
        catch (TimeoutException)
        {
            _batchClientExecWaits.TryRemove(interruptId, out _);
            _logger.LogWarning("批量客户端技能执行超时未回传：interrupt={InterruptId}", interruptId);
            return null;
        }
        catch (OperationCanceledException)
        {
            _batchClientExecWaits.TryRemove(interruptId, out _);
            return null;
        }
    }

    /// <summary>
    /// 从本群已有消息里找“最像正文”的上游产出，用于交付技能的空输入兑底。
    ///
    /// <para>
    /// 场景：计划把导出技能排在了产出岗之前，或产出岗本轮返回为空，
    /// 但群里其实已经有内容（如上一轮“内容负责人”已经写好的定稿）。
    /// 这时宁可拿它继续交付，也不应给用户一个空白 Word 或一句“无法生成”。
    /// </para>
    ///
    /// 选取口径（宁缺勿滥）：取最近 N 条消息中最长的一条，且长度需达下限；
    /// 排除本管道自己播报的进度文案 / 兜底提示，避免把“未返回内容”又取回来。
    /// </summary>
    private string? FindUpstreamContent(AgentInvocationContext context)
    {
        const int MinChars = 120; // 低于此长度基本不是可交付的正稿
        try
        {
            var msgs = _hub.Value.Store.RecentMessages(context.GroupId, 30, context.TopicId);
            var best = "";
            foreach (var m in msgs)
            {
                if (m.Recalled) continue;
                if (m.MessageId == context.TriggerMessageId) continue; // 不要拿用户本次提问当正文
                var body = m.Content;
                if (string.IsNullOrWhiteSpace(body)) continue;
                if (AgentGatewayHelpers.LooksTooThinForDelivery(body)) continue;
                if (body.Contains("未能生成文件", StringComparison.Ordinal)
                    || body.Contains("没有可供转换", StringComparison.Ordinal)) continue;
                if (body.Length > best.Length) best = body;
            }
            if (best.Length >= MinChars) return best;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "交付空输入兑底：读取群消息失败");
        }
        return null;
    }

    private string BuildPlanInventory(AgentDefinition root, IReadOnlyList<AgentDefinition> reached, IReadOnlyList<AgentSkillDefinition> skills)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# 可用的数字员工（组织分工）");
        foreach (var d in reached)
            sb.Append("- [员工] ").Append(d.Nickname ?? d.AgentId).Append(" (id=").Append(d.AgentId).Append(")")
              .AppendLine(string.IsNullOrWhiteSpace(d.Description) ? "" : "｜" + d.Description.ReplaceLineEndings(" "));
        sb.AppendLine("# 可调用的技能（技能库）");
        foreach (var s in skills)
        {
            sb.Append("- [技能] ").Append(s.SkillId).Append("｜").Append(s.Name ?? s.SkillId).Append("：")
              .Append((s.Description ?? "").ReplaceLineEndings(" "));
            // 技能需要的外部输入 → 提示计划先拿到该值再调用它。
            // 用 RequiredUpstreamInputs 而非 SkillRequiredInputs：后者只看正文里的 ${xxx} 占位符，
            // 而 dotnet / shell 类技能（如 docx_report / md_to_docx）的入参在函数签名里、正文无占位符，
            // 用旧口径会把它们误判为“无需输入”，于是规划器把它们排在产出岗之前，导出只有标题的空文档（实测踩到）。
            var inputs = AgentGatewayHelpers.RequiredUpstreamInputs(s);
            if (inputs.Count > 0)
                sb.Append("【需要输入：").Append(string.Join("、", inputs)).Append("】");
            sb.AppendLine();
        }
        if (reached.Count == 0) sb.AppendLine("- （无可指派的数字员工）");
        if (skills.Count == 0) sb.AppendLine("- （无可调用的技能）");
        return sb.ToString();
    }

    /// <summary>测试钩子：拼装规划器提示词的正文部分（不依赖真实目录）。勿在生产路径调用。</summary>
    internal static string BuildPlannerPromptText(string rootName, string input, string inventory)
    {
        return "你是群聊的协调员「" + rootName + "」。\n\n"
            + "用户问题：\n" + input + "\n\n"
            + "你掌握的组织分工与技能如下（只能从中选，不能造）：\n" + inventory + "\n\n"
            + "请针对该问题制定一张<b>执行计划</b>：\n"
            + "- 若需要某数字员工提供信息/处理某部分 → {\"action\":\"dispatch\",\"target\":\"<该员工id>\"}\n"
            + "- 若需要调用某技能做检测/验证 → {\"action\":\"skill\",\"target\":\"<该技能id>\"}\n"
            + "- 最后用一步 {\"action\":\"answer\",\"note\":\"<你要怎么综合答复>\"} 汇总。\n"
            + "<b>多技能组合优先</b>：当用户想排查/全面检查（如“电脑/系统有没有问题、是不是异常、检查一下”）时，\n"
            + "请在可用技能中挑选<b>多个相互补充</b>的检测项（如系统信息、磁盘、内存/CPU、进程、网络、服务、事件日志）组合成连续步骤，\n"
            + "一起跑完后再综合判断——不要只挑一个就把结论下死。技能若无需特定输入（技能描述未标【需要输入：…】），可直接作为互不依赖的连续步骤。\n"
            + "<b>依赖顺序很重要</b>：如果一个技能<b>需要某个输入</b>（见技能后的【需要输入：…】），而这个输入由某位员工掌握，\n"
            + "你必须<b>先用一步 dispatch 该员工拿到输入值</b>，<b>再</b>在后续步骤里调用该技能——技能步骤会自动收到它前一步的结果作为输入。\n"
            + "例如：要“测 Exchange 连接”需先知道 OWA 地址，而地址由配置管理员提供，则应安排 [dispatch→配置管理员, skill→连接测试技能, answer]。\n"
            // 交付顺序：导出 / 排版类是“把已有内容转成文件”，必须先有内容。
            // 实测踩到：docx_report 被排在执笔岗之前执行，输入为空 → 导出的 Word 只有一个标题、正文全空。
            + "<b>交付类技能必须排在产出内容之后（很重要）</b>：\n"
            + "- “导出 / 排版 / 转换 / 生成 Word・Excel・PDF・PPT”类技能（如 docx_report、md_to_docx），\n"
            + "  它的活是<b>把已有内容转成文件</b>，所以<b>必须排在能产出该内容的员工之后</b>；\n"
            + "  绝不能把它排在第一个、也不能排在任何产出内容的 dispatch 之前。\n"
            + "- 正确顺序：先 dispatch 产出岗位（写作 / 分析 / 整理 / 规划）→ 再把它的产出交给交付技能转成文件 → answer。\n"
            + "- 若用户既要内容又要文件，且组织里只有一个岗位能干这两件事，则规划顺序上也必须先产出、后导出。\n"
            + "只输出 JSON，不要任何其他文字：{\"steps\":[…]}}，步骤 1~" + PlanMaxStepsPlaceholder + " 条。若问题与任何员工/技能都不相关，输出 {\"steps\":[]}。";
    }

    /// <summary>规划器提示词里的最大步数（测试钩子用固定值，生产路径由执行配置覆盖）。</summary>
    private const int PlanMaxStepsPlaceholder = 8;

    private async Task<List<PlanStep>?> PlanCoordinatedAsync(AgentInvocationContext context, AgentDefinition root, string input,
        IReadOnlyList<AgentDefinition> reached, IReadOnlyList<AgentSkillDefinition> skills, CancellationToken ct)
    {
        var agent = _catalog.GetOrCreate(root.AgentId);
        var inventory = BuildPlanInventory(root, reached, skills);
        // 提示词统一由 BuildPlannerPromptText 拼装（测试可断言“交付顺序”硬规则在不在）
        var prompt = BuildPlannerPromptText(root.Nickname ?? root.AgentId, input, inventory)
            .Replace(PlanMaxStepsPlaceholder.ToString(), _execution.CoordinatorPlanMaxSteps.ToString());
        var session = await agent.CreateSessionAsync(ct);
        var resp = await agent.RunAsync([new ChatMessage(ChatRole.User, prompt)], session, null, ct);
        return AgentGatewayHelpers.ParsePlan(resp.Text);
    }

    private async Task<string> SynthesizePlanAnswerAsync(AgentInvocationContext context, AgentDefinition root, string input, string resultText, CancellationToken ct)
    {
        var agent = _catalog.GetOrCreate(root.AgentId);
        var prompt = "你是「" + (root.Nickname ?? root.AgentId) + "」。用户问题：\n" + input
            + "\n\n你已按计划调用下属/技能，得到以下处理结果：\n" + (resultText.Length == 0 ? "（无）" : resultText)
            + "\n\n请基于这些结果，给用户一个完整、连贯的最终答复（不要在开头重复“已按计划…实现”之类话术，直接作答；若结果不足以回答，如实说明并给出下一步建议）。";
        var session = await agent.CreateSessionAsync(ct);
        var resp = await agent.RunAsync([new ChatMessage(ChatRole.User, prompt)], session, null, ct);
        return string.IsNullOrWhiteSpace(resp.Text) ? resultText : resp.Text.Trim();
    }

    /// <summary>
    /// 模型驱动的<b>递归补查闭环</b>（方案 C）：数字员工基于已收集的检查结果作答，
    /// 每轮让模型判断“是否已有足够信息回答用户的完整问题”；若不足，则输出下一步要补查的
    /// 技能（<c>kind=skill</c>）或指派的数字员工（<c>kind=dispatch</c>），网关据此执行
    /// （客户端技能合并成「本机一键执行全部」批量确认；服务端技能 / 子员工直接执行），
    /// 结果回灌后进入下一轮，直到模型认为信息充分才给出最终答复。<b>不会中途停下问用户要不要继续。</b>
    /// </summary>
    private async Task<string> ExecuteRecursiveAnswerAsync(
        AgentInvocationContext context, AgentDefinition root, string groupId, string messageId,
        string input, string priorResults, IEnumerable<string>? alreadyRanSkills, CancellationToken ct)
    {
        var db = _skillCatalog.Value;
        var agent = _catalog.GetOrCreate(root.AgentId);
        var session = await agent.CreateSessionAsync(ct);
        var facts = new StringBuilder(string.IsNullOrWhiteSpace(priorResults) ? "（暂无可用的检查结果）" : priorResults.Trim());
        var lastAnswer = "";
        var rounds = 0;
        // 递归补查轮次上限取自 _execution.MaxRecursiveRounds（默认 5，防死循环 / 打爆时长）
        // 已执行过的技能 id（含计划里已跑过的所有技能，客户端 + 服务端）：避免下一轮又拿同一技能补查，导致“同一技能被调用两次”
        var executedSkills = new HashSet<string>(alreadyRanSkills ?? [], StringComparer.Ordinal);
        // 已带回结果的能力（技能 / 分派员工都记录），补查时同样跳过，防止重复调用
        var answeredTargets = new HashSet<string>(executedSkills, StringComparer.Ordinal);

        // 可用技能清单 + 可指派的直属下属，供模型判断“还能补查什么”
        var skillList = new List<string>();
        var dispatchList = new List<string>();
        foreach (var sk in (root.SkillDefIds ?? []))
            if (db?.Get(sk) is { } sd && sd.Kind != AgentSkillKind.Org_deploy)
            {
                // 文档生成类技能不进补查清单：它们的入参是结构化 JSON，
                // 递归补查只会把“已掌握的检查结果”一段纯文本塑进去（JsonReaderException / 空壳文档）。
                // 文件交付由计划后的交付兑底（完整流式）负责。
                if (AgentGatewayHelpers.IsDocumentGenerator(sd)) continue;
                skillList.Add($"{sd.SkillId}（{sd.Name ?? sd.SkillId}）：{(sd.Description ?? "").Replace("\n", " ")}");
            }
        foreach (var id in (root.AssignmentIds ?? []))
            if (_catalog.GetDefinition(id) is { } sub) dispatchList.Add($"{sub.Nickname ?? id}（id={id}）：{(sub.Description ?? "").Replace("\n", " ")}");

        while (rounds++ < _execution.MaxRecursiveRounds)
        {
            var prompt = "你是「" + (root.Nickname ?? root.AgentId) + "」，正在回答用户的问题。\n\n"
                + "用户问题：\n" + input + "\n\n"
                + "已掌握的检查结果：\n" + facts + "\n\n"
                + "你当前可补查的能力：\n- 客户端/服务端技能：\n" + (skillList.Count == 0 ? "  （无）" : string.Join("\n", skillList.Select(x => "  - " + x)))
                + "\n- 可指派的数字员工：\n" + (dispatchList.Count == 0 ? "  （无）" : string.Join("\n", dispatchList.Select(x => "  - " + x)))
                + "\n\n请判断：现有的检查结果<b>是否已足以</b>完整回答用户的问题。\n"
                + "- 若还缺关键信息/有疑问需要进一步排查 → 输出 JSON，`needsMore` 为 true，并在 `gather` 里列出<b>要补查的能力</b>（只能从上面列出的技能 id 或员工 id 中选）：\n"
                + "  {\"needsMore\":true,\"gather\":[{\"kind\":\"skill\",\"target\":\"<技能id>\"},{\"kind\":\"dispatch\",\"target\":\"<员工id>\"}],\"answer\":\"\"}\n"
                + "- 若已有信息<b>足以回答</b> → 输出 JSON，`needsMore` 为 false，并在 `answer` 里直接给出面向用户的<b>完整、连贯的最终答复</b>：\n"
                + "  {\"needsMore\":false,\"gather\":[],\"answer\":\"<最终答复>\"}\n"
                + "只输出这一行 JSON，不要任何其他文字。请<b>综合判断</b>，不要为答而反复补查；能回答就回答。";
            var resp = await agent.RunAsync([new ChatMessage(ChatRole.User, prompt)], session, null, ct);
            var text = (resp.Text ?? "").Trim();

            // 宽松解析模型输出
            var parsed = ParseRecursiveResponse(text);
            if (parsed is null)
            {
                // 解析失败：把模型原文当作最终答复，结束递归（退化，避免卡死）
                return EnsureNonEmpty(string.IsNullOrWhiteSpace(text) ? facts.ToString() : UnwrapCoordinationAnswer(text),
                    facts.ToString());
            }
            if (!string.IsNullOrWhiteSpace(parsed.Answer))
                lastAnswer = parsed.Answer.Trim();
            if (!parsed.NeedsMore || parsed.Gather.Count == 0)
            {
                // 信息充分：用模型给出的答案给最终答复；即便 answer 为空也绝不回空串（用已收集 facts 兜底）
                return EnsureNonEmpty(lastAnswer, facts.ToString());
            }

            // 执行本轮要补查的能力：客户端技能→批量；服务端技能→直接执行；分派→子员工。已执行过的技能直接跳过（去重，防同一技能重复调用）。
            var gathered = new StringBuilder();
            var clientItems = new List<BatchClientItem>();
            foreach (var req in parsed.Gather)
            {
                // 已在计划/上一轮带回结果的能力（技能或分派员工）直接跳过：防止“同一能力被调用两次”
                if (!answeredTargets.Add(req.Target))
                {
                    var resolved = string.Equals(req.Kind, "skill", StringComparison.OrdinalIgnoreCase)
                        ? (db?.Get(req.Target)?.Name ?? req.Target) : req.Target;
                    gathered.AppendLine($"「{resolved}」已在上轮执行，直接复用其结果。");
                    continue;
                }
                if (string.Equals(req.Kind, "skill", StringComparison.OrdinalIgnoreCase))
                {
                    var skill = db?.Get(req.Target);
                    if (skill is null) { gathered.AppendLine($"技能「{req.Target}」不可用，已跳过。"); continue; }
                    if (skill.Kind == AgentSkillKind.Org_deploy) { gathered.AppendLine($"「{req.Target}」是受控落库动作，不走补查批量执行。"); continue; } // 防御：不投给 SkillRunner
                    executedSkills.Add(req.Target);
                    if (skill.ExecutionLocation == AgentSkillExecutionLocation.Client)
                        clientItems.Add(new BatchClientItem(skill.SkillId, skill.Name ?? skill.SkillId, EffectiveClientRunner(skill) ?? "", req.Input ?? "", 0));
                    else
                        gathered.AppendLine($"【{skill.Name ?? skill.SkillId}】\n" + (await _catalog.RunSkillAsync(skill, req.Input ?? "", ct)));
                }
                else if (string.Equals(req.Kind, "dispatch", StringComparison.OrdinalIgnoreCase))
                {
                    var outText = await InvokeSubordinateAsync(context, req.Target, req.Input ?? input, ct);
                    gathered.AppendLine($"【{req.Target} 协助】\n" + outText);
                }
            }
            if (clientItems.Count > 0)
            {
                var map = await AwaitBatchClientExecAsync(context, groupId, messageId, [], clientItems, ct);
                if (map is not null)
                    foreach (var it in clientItems)
                        gathered.AppendLine($"【{it.Name}】\n" + (map.TryGetValue(it.SkillId, out var o) ? o : "（未返回结果）"));
                else
                    gathered.AppendLine("（本机补查未执行 / 被取消）");
            }
            facts.Append("\n\n【本轮追加补查结果】\n").Append(gathered.ToString().Trim());
            _logger.LogInformation("递归补查第 {Round} 轮：技能={Skills} 分派={Dispatches} agent={AgentId}",
                rounds, string.Join(",", parsed.Gather.Where(g => g.Kind == "skill").Select(g => g.Target)),
                string.Join(",", parsed.Gather.Where(g => g.Kind == "dispatch").Select(g => g.Target)), root.AgentId);
        }

        // 达到最大轮数仍未明确“信息充分”：用最近一次答案兜底
        return EnsureNonEmpty(lastAnswer, facts.ToString());
    }

    /// <summary>优先级回退：若主文本为空则回退到二号文本；两者都空则给“无可用内容”占位，绝不向调用方回空（防空正文消息）。</summary>
    private static string EnsureNonEmpty(string primary, string fallback)
    {
        var p = string.IsNullOrWhiteSpace(primary) ? "" : primary.Trim();
        if (p.Length > 0) return p;
        var f = string.IsNullOrWhiteSpace(fallback) ? "" : fallback.Trim();
        return f.Length > 0 ? f : "（本次未能从已收集结果中汇总出可展示内容。请允许我基于现有结果重新组织一次，或换一种更明确的问法。）";
    }

    /// <summary>递归补查时指派的目标解析。</summary>
    private sealed record RecursiveGatherItem(string Kind, string Target, string? Input);
    private sealed record RecursiveResponse(bool NeedsMore, List<RecursiveGatherItem> Gather, string? Answer);

    private static RecursiveResponse? ParseRecursiveResponse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        JsonDocument? doc = null;
        try { doc = JsonDocument.Parse(text.Substring(start, end - start + 1)); }
        catch { /* 模型常在 answer 里放了真实换行/未转义 → 整包 JSON 解码失败；走容错提取正文，避免把决策 JSON 泄漏给用户 */ }
        if (doc is null) return ExtractRecursiveAnswerFallback(text);
        using (doc)
        {
            var rootEl = doc.RootElement;
            var needsMore = rootEl.TryGetProperty("needsMore", out var nm) && nm.ValueKind == JsonValueKind.True;
            var answer = rootEl.TryGetProperty("answer", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() : null;
            var gather = new List<RecursiveGatherItem>();
            if (rootEl.TryGetProperty("gather", out var g) && g.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in g.EnumerateArray())
                {
                    var kind = item.TryGetProperty("kind", out var k) ? k.GetString() : null;
                    var target = item.TryGetProperty("target", out var t) ? t.GetString() : null;
                    var input = item.TryGetProperty("input", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(kind) && !string.IsNullOrWhiteSpace(target))
                        gather.Add(new RecursiveGatherItem(kind.Trim(), target.Trim(), input));
                }
            }
            return new RecursiveResponse(needsMore, gather, answer);
        }
    }

    /// <summary>容错回退：整包 JSON 解码失败（模型常把 answer 写成含真实换行/未转义 的纯文本）时，
    /// 手工从文本里剥出 needsMore 与 answer 正文，避免把决策 JSON 原样泄漏给用户。
    /// 提取结尾引号时跳过 \" 转义，避免在正文含双引号处被截断。</summary>
    private static RecursiveResponse? ExtractRecursiveAnswerFallback(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var needsMore = false;
        // needsMore：紧跟在冒号后的词是 true/false；找不到按 false（信息充分）处理，让答案直接可用
        var nmIdx = text.IndexOf("needsMore", StringComparison.OrdinalIgnoreCase);
        if (nmIdx >= 0)
        {
            var tail = text.Substring(nmIdx + "needsMore".Length);
            var colon = tail.IndexOf(':');
            if (colon >= 0)
            {
                var rest = tail.Substring(colon + 1).TrimStart();
                if (rest.StartsWith("true", StringComparison.OrdinalIgnoreCase)) needsMore = true;
                else if (rest.StartsWith("false", StringComparison.OrdinalIgnoreCase)) needsMore = false;
            }
        }

        // answer：定位 "answer" 后的首个 :
        var ansKey = "answer";
        var keyIdx = text.IndexOf(ansKey, StringComparison.OrdinalIgnoreCase);
        if (keyIdx < 0) return null;
        var afterKey = text.Substring(keyIdx + ansKey.Length);
        var ansColon = afterKey.IndexOf(':');
        if (ansColon < 0) return null;
        var valueStart = ansColon + 1;
        // 跳过空白跳到 `"`
        var str = afterKey.Substring(valueStart).TrimStart();
        if (str.Length == 0 || str[0] != '"') return null;

        // 从字符串末尾的方向找正文的结束引号：双引号若紧跟 \ 前缀则视为转义（跳开）；
        // 正文结束引号即为最右侧那个未转义的 `"`。
        var openQuote = 1; // 跳开头的 "
        var closeQuote = -1;
        for (int i = str.Length - 1; i >= openQuote; i--)
        {
            if (str[i] == '"' && !IsEscaped(str, i))
            {
                closeQuote = i;
                break;
            }
        }
        if (closeQuote < 0) return null;
        var answer = str.Substring(openQuote, closeQuote - openQuote);
        answer = answer.Replace("\\\"", "\"").Replace("\\\\", "\\").Replace("\\n", "\n").Replace("\\t", "\t");
        return new RecursiveResponse(needsMore, [], answer.Trim());
    }

    /// <summary>判断 text[i] 是否是(被 \ 转义过的)引号 —— 即 text[i]=='"' 且往前数反斜杠个数为奇数。</summary>
    private static bool IsEscaped(string s, int i)
    {
        if (i <= 0 || s[i] != '"') return false;
        int backslashes = 0;
        for (int j = i - 1; j >= 0 && s[j] == '\\'; j--) backslashes++;
        return backslashes % 2 == 1;
    }

    /// <summary>防御：若模型的“最终归答文本”实际是内部协调 JSON（{"needsMore":…,"gather":…,"answer":…}）
    /// 或其它仅含 answer 的包壳，剥出面向用户的 answer，避免把内部 JSON 原样回给用户。</summary>
    private static string UnwrapCoordinationAnswer(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start) return text;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(text.Substring(start, end - start + 1));
            var r = doc.RootElement;
            if (!r.ValueKind.Equals(System.Text.Json.JsonValueKind.Object)) return text;
            var isCoord = r.TryGetProperty("needsMore", out var nm) && (nm.ValueKind == System.Text.Json.JsonValueKind.True || nm.ValueKind == System.Text.Json.JsonValueKind.False);
            if (isCoord && r.TryGetProperty("answer", out var a) && a.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                var ans = a.GetString();
                if (!string.IsNullOrWhiteSpace(ans)) return ans.Trim();
            }
        }
        catch
        {
            // 模型常在 answer 里放了真实换行/未转义 → 整包解码失败：若文本仍像是协调 JSON 包壳，
            // 用容错提取把 answer 正文剥出来，避免把 {needsMore,…} 整段 JSON 泄漏给用户。
            if (LooksLikeCoordinationObject(text)
                && ExtractRecursiveAnswerFallback(text) is { Answer: { Length: > 0 } ans2 }
                && !string.IsNullOrWhiteSpace(ans2))
                return ans2.Trim();
        }
        return text;
    }

    /// <summary>粗略判断一段文本是否是“协调决策”样式的对象包壳（开头是 { 且含 answer/needsMore），用于解码失败时的容错回退。</summary>
    private static bool LooksLikeCoordinationObject(string text)
    {
        var t = text?.TrimStart() ?? "";
        if (!t.StartsWith('{')) return false;
        return t.Contains("answer", System.StringComparison.OrdinalIgnoreCase)
            || t.Contains("needsMore", System.StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>递归补查时让某个直属下属就问题给结论（一次性 RunAsync，不递归下钻，避免无限深）。</summary>
    private async Task<string> InvokeSubordinateAsync(AgentInvocationContext context, string agentId, string input, CancellationToken ct)
    {
        if (_catalog.GetDefinition(agentId) is not { } sub) return "（指派对象不存在）";
        var child = _catalog.GetOrCreate(agentId);
        var prompt = "你被「" + (sub.Nickname ?? sub.AgentId) + "」指派处理，请就以下请求给出你的专业结论。\n\n问题：" + input
            + "\n\n只输出本步结论，不要复述前序内容。";
        var session = await child.CreateSessionAsync(ct);
        try { return (await child.RunAsync([new ChatMessage(ChatRole.User, prompt)], session, null, ct)).Text?.Trim() ?? "（子员工未返回内容）"; }
        catch (Exception ex) { return "（指派执行失败：" + ex.Message + "）"; }
    }

    // 指派/提升路由的最大层数（防配置病态深链 / 打爆模型时长的兑底），见 _execution.MaxRouteDepth。

    /// <summary>路由结局。</summary>
    private enum RouteOutcome { Answer, CannotSolve }

    /// <summary>
    /// 递归路由：本级先判是否应答，否则尝试<b>任务指派</b>（白名单内推断目标），
    /// 再否则尝试<b>问题提升</b>（配置的提升目标）；全部无解 → <see cref="RouteOutcome.CannotSolve"/>。
    /// 返回 (结局, 最终答复, 路由路径[ChainNode])。
    /// </summary>
    private async Task<(RouteOutcome Outcome, string Text, List<ChainNode> Hops)> ResolveRouteAsync(
        AgentInvocationContext context, string agentId, string input, HashSet<string> visited, int depth, CancellationToken ct)
    {
        var def = _catalog.GetDefinition(agentId);
        if (def is null || depth > _execution.MaxRouteDepth || visited.Contains(agentId))
            return (RouteOutcome.CannotSolve, "", []);
        visited.Add(agentId);
        var hops = new List<ChainNode>();

        // 1) 任务指派白名单（向下）：对<b>路由器</b>节点（配了白名单）先尝试向下钻取。
        //    即便本节点语义（ShouldSpeak）也认定该由系统处理，也优先路由到更专业的下游——因为组织里
        //    专门负责该问题的数字员工更有权威；只有下游无解（没有专业层认领）时才回退到本节点自答。
        //    多候选排序 + 递归探测回退：召回层只排序，逐候选递归，某子分支无解回退下一候选，
        //    支持推断到最后一层。召回为空（NONE）表示「本层不派”：根层（depth==1）尊重它；
        //    处于上层下派链（depth>1）、本层为无法解决的管理者时按白名单顺序继续下钻，避免深层漏解。
        var candidates = (def.AssignmentIds ?? [])
            .Where(id => !string.IsNullOrWhiteSpace(id)
                && !string.Equals(id, agentId, StringComparison.Ordinal)
                && !visited.Contains(id)
                && _catalog.GetDefinition(id) is not null)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (candidates.Count > 0)
        {
            var ranked = await RankAssignTargetsAsync(context, def, candidates, input, ct);
            var probeOrder = ranked.Count > 0 || depth <= 1 ? ranked : candidates;
            foreach (var target in probeOrder)
            {
                if (string.IsNullOrWhiteSpace(target) || visited.Contains(target)) continue;
                var (subOutcome, subText, subHops) = await ResolveRouteAsync(context, target, input, visited, depth + 1, ct);
                if (subOutcome == RouteOutcome.Answer)
                {
                    // 末级自答：subHops 首节点即 target（叶子），避免「target 关系节点 + subHops 作答节点」重复
                    var targetSelfAnswer = subHops.Count > 0 && string.Equals(subHops[0].AgentId, target, StringComparison.Ordinal);
                    if (!targetSelfAnswer)
                        hops.Add(new ChainNode { Kind = "assignment", AgentId = target, AgentNickname = _catalog.GetDefinition(target)?.Nickname ?? target, Query = AgentGatewayHelpers.TruncateForChain(input) });
                    hops.AddRange(subHops);
                    return (RouteOutcome.Answer, subText, hops);
                }
            }
        }

        // 2) 本节点语义（ShouldSpeak）：下游无解时才轮到本节点自答
        if (await ShouldSpeakAsync(context, def, ct))
        {
            var text = await RunRouteAnswerAsync(context, agentId, input, ct);
            hops.Add(new ChainNode { Kind = "assignment", AgentId = agentId, AgentNickname = def.Nickname ?? agentId, Query = AgentGatewayHelpers.TruncateForChain(input), Result = AgentGatewayHelpers.TruncateForChain(text) });
            return (RouteOutcome.Answer, text, hops);
        }

        // 3) 问题提升（配置的提升目标）
        var esc = def.EscalationAgentId;
        if (!string.IsNullOrWhiteSpace(esc)
            && !string.Equals(esc, agentId, StringComparison.Ordinal)
            && !visited.Contains(esc)
            && _catalog.GetDefinition(esc) is not null)
        {
            var (subOutcome, subText, subHops) = await ResolveRouteAsync(context, esc, input, visited, depth + 1, ct);
            if (subOutcome == RouteOutcome.Answer)
            {
                // 末级自答同理去重：subHops 首节点即 esc（叶子）时不再重复叠加
                var escSelfAnswer = subHops.Count > 0 && string.Equals(subHops[0].AgentId, esc, StringComparison.Ordinal);
                if (!escSelfAnswer)
                    hops.Add(new ChainNode { Kind = "escalation", AgentId = esc, AgentNickname = _catalog.GetDefinition(esc)?.Nickname ?? esc, Query = AgentGatewayHelpers.TruncateForChain(input) });
                hops.AddRange(subHops);
                return (RouteOutcome.Answer, subText, hops);
            }
        }

        // 4) 无解
        return (RouteOutcome.CannotSolve, "", hops);
    }

    /// <summary>
    /// 该岗位是否挂了<b>可执行</b>技能（需要真实执行、可能产出文件 / 副作用）。
    ///
    /// <para>
    /// 用于决定路由自答走轻量回答还是完整流式：
    /// prompt / org_deploy 不算可执行（前者是一段模板，后者是受控落库动作）；
    /// dotnet / shell / http 均算 —— 它们必须经完整路径才能拿到工具调用、审批与产物回档。
    /// </para>
    /// </summary>
    private bool HasExecutableSkill(AgentDefinition def)
    {
        if (def.SkillDefIds is not { Count: > 0 }) return false;
        var catalog = _skillCatalog.Value;
        if (catalog is null) return false;
        foreach (var id in def.SkillDefIds)
        {
            if (string.IsNullOrWhiteSpace(id)) continue;
            if (catalog.Get(id) is not { } sk) continue;
            if (sk.Kind is AgentSkillKind.Dotnet or AgentSkillKind.Shell or AgentSkillKind.Http) return true;
        }
        return false;
    }

    /// <summary>
    /// 任务指派目标<b>排序</b>：在 <paramref name="candidates"/>（白名单）里按匹配度从高到低输出一个或多个
    /// 候选下游数字员工（可逗号分隔返回多个，供上层做递归探测回退）；都不合适输出 NONE。
    /// 返回候选 agentId 的已排序列表（保证都在 <paramref name="candidates"/> 内）。
    /// 只依据<b>直接下级</b>的昵称与职责做语义匹配——组织架构的指派判断只看下一层，不向上钻、不引入更深层叶子。
    /// </summary>
    private async Task<List<string>> RankAssignTargetsAsync(AgentInvocationContext context, AgentDefinition def, List<string> candidates, string input, CancellationToken ct)
    {
        var agent = _catalog.CreateBare(def.AgentId);
        var sb = new StringBuilder();
        sb.AppendLine("以下是一个待处理请求，请判断该把它交给哪个下游数字员工（任务指派）。");
        sb.AppendLine("你只输出匹配结果，不要附加说明。");
        sb.AppendLine("候选（agentId 列表）：" + string.Join(", ", candidates));
        sb.AppendLine("候选职责：");
        foreach (var cid in candidates)
        {
            var cdef = _catalog.GetDefinition(cid);
            sb.AppendLine($"  - {cid}：{cdef?.Nickname ?? cid} - {cdef?.Description ?? ""}");
        }
        sb.AppendLine("按匹配度从高到低输出一个或多个候选 agentId，多个用英文逗号分隔；若都不适合只输出 NONE。");
        var prompt = "__AGUI_ROUTE__\n" + sb + "\n请求：\n" + UntrustedBoundary.Wrap(input);
        var resp = await agent.RunAsync(prompt, session: null, new ChatClientAgentRunOptions { ChatOptions = new ChatOptions { MaxOutputTokens = 64 } }, ct);
        var ranked = ParseAssignTargets(resp.Text, candidates);
        if (ranked.Count > 0)
        {
            _logger.LogInformation("智能体 {AgentId} 指派路由：候选 {Candidates} 个 → 命中 {Hits}",
                def.AgentId, candidates.Count, ranked.Count);
            return ranked;
        }
        if (!string.IsNullOrWhiteSpace(resp.Text))
        {
            // 模型明确回 NONE（或全在白名单外）：尊重它的判断，不再重试
            _logger.LogInformation("智能体 {AgentId} 指派路由：候选 {Candidates} 个 → 未命中（模型回 {Raw}）",
                def.AgentId, candidates.Count, AgentGatewayHelpers.TruncateForChain(resp.Text));
            return ranked;
        }

        // 空输出 = 判定失败（不是“没人合适”）：实测思考模型会把 64 个预算全花在思维链上、正文为空，
        // 而旧代码把它当成“无候选”，于是**静默**退化成“只有问题提升、没有任务指派”。
        // 这里重试一次（瞬时失败居多），仍为空则记警告并保留原语义（不发明指派）。
        // 注意：命中（ranked.Count > 0）必须在上面就 return —— 否则每次成功指派都要白跑一次路由调用，
        // 而且第二次的结果会覆盖第一次（曾经就是漏了这个 return，靠护栏才没流出）。
        _logger.LogWarning("智能体 {AgentId} 指派路由返回空输出（模型={Model}），重试一次",
            def.AgentId, _catalog.DecisionModelName(def.AgentId));
        var retry = await agent.RunAsync(prompt, session: null, new ChatClientAgentRunOptions { ChatOptions = new ChatOptions { MaxOutputTokens = 256 } }, ct);
        ranked = ParseAssignTargets(retry.Text, candidates);
        if (ranked.Count == 0)
            _logger.LogWarning("智能体 {AgentId} 指派路由重试后仍无候选（原始：{Raw}）——本次不指派", def.AgentId, AgentGatewayHelpers.TruncateForChain(retry.Text));
        else
            _logger.LogInformation("智能体 {AgentId} 指派路由：候选 {Candidates} 个 → 命中 {Hits}（重试后）", def.AgentId, candidates.Count, ranked.Count);
        return ranked;
    }

    /// <summary>
    /// 解析指派路由的输出：逗号分隔的候选 agentId（兼容单个 / NONE / 混合文本），只保留在白名单内的。
    /// 注意空输入返回空列表——“模型说了 NONE”与“模型什么都没说”对调用方意义不同，
    /// 因此判定“是否失败”应由调用方看原始文本（见 RankAssignTargetsAsync）。
    /// </summary>
    internal static List<string> ParseAssignTargets(string? text, IReadOnlyList<string> candidates)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        return text.Split([',', '，'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(c => candidates.Contains(c))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// 编排计划路径对本岗位是否“干得了活”：有可派下级 → 能（计划会派单）；
    /// 自己挂了**计划真会执行**的技能（非文档生成 / 非落库类） → 能（计划会调它）。
    ///
    /// <para>
    /// 两者都不成立 = 只挂文档生成技能的叶子岗位：计划注定产不出东西，应回落完整流式。
    /// 技能库拿不到时返回 true（保守：保持原行为，不放宽路由）。
    /// </para>
    /// </summary>
    private bool PlanPathCanDoTheWork(AgentDefinition def)
    {
        if ((def.AssignmentIds ?? []).Any(id => !string.IsNullOrWhiteSpace(id)
                && !string.Equals(id, def.AgentId, StringComparison.Ordinal)
                && _catalog.GetDefinition(id) is not null))
            return true;

        var catalog = _skillCatalog.Value;
        if (catalog is null) return true;
        foreach (var id in def.SkillDefIds ?? [])
        {
            if (string.IsNullOrWhiteSpace(id)) continue;
            if (catalog.Get(id) is not { } s) continue;
            // 落库类技能不进计划 inventory（与 isSkillPlanner 的口径一致）。
            if (s.Kind == AgentSkillKind.Org_deploy) continue;
            // 文档生成技能在计划里只会被跳过（改交交付兜底），不算“计划干得了的活”。
            if (!AgentGatewayHelpers.IsDocumentGenerator(s)) return true;
        }
        return false;
    }

    /// <summary>
    /// 预判：组织化路由是否应当「交由完整流式路径」处理。
    ///
    /// <para>
    /// 条件：① 本岗位挂了可执行技能（dotnet / shell / http）；② 路由最终会把任务落在本岗位自答（而非派给下级）。
    /// 二者同时成立时，轻量路由回答（无工具、无审批、无产物回档）干不了活，应落回普通流式。
    /// </para>
    ///
    /// <para>
    /// 实现上只做「决策」不做「执行」：不广播计划卡、不建消息、不改上下文，
    /// 因此重复一次路由决策是安全的（多花一次轻量模型调用，换取叶子执行岗能真正干活）。
    /// 只在「本岗无下级可派」或「有下级但下级无人接单且本岗该自答」时才转为委派。
    /// </para>
    /// </summary>
    private async Task<bool> ShouldDelegateRouteToStreamingAsync(AgentInvocationContext context, CancellationToken ct)
    {
        var def = _catalog.GetDefinition(context.AgentId);
        if (def is null) return false;
        if (!HasExecutableSkill(def)) return false;

        // 配了编排计划时，正常情况下走 ExecuteCoordinatedPlanAsync（那条路径已含技能执行与产物回档），不需转。
        //
        // 但“配了计划”≠“计划干得了这个岗位的活”：若本岗是**没有可派下级的叶子**，且自己的技能
        // **全是文档生成类**（docx_* / pptx_* / md_to_docx …）—— 这类技能被计划路径**刻意跳过**
        // （它们的入参是结构化 JSON，必须由模型当工具构造），计划就产不出任何东西；
        // 若就此返回 false，任务会落到轻量自答路径：那条路径没有工具、也不处理审批，
        // 模型这一轮返回的是**审批请求**而不是正文 → 正文为空。
        // 实测就是「与 ppt生成助手 的单聊」里那条只回「（ppt生成助手 代为处理）」、
        // 技能从未执行、也没有审批卡与任何错误的请求。这种情况应当交回完整流式（工具 / 审批 / 产物回档）。
        if (_options.CoordinatorPlanning && PlanPathCanDoTheWork(def)) return false;

        // 快速通道：配了可派下级时，先不做任何模型调用。
        // 只有「本岗是叶子（无下级）或下级全不行」才值得花一次预判；
        // 绝大多数有下级的场景会返回 false，从而保持原路由行为与原开销。
        var candidates = (def.AssignmentIds ?? [])
            .Where(id => !string.IsNullOrWhiteSpace(id)
                && !string.Equals(id, def.AgentId, StringComparison.Ordinal)
                && _catalog.GetDefinition(id) is not null)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        try
        {
            var input = await BuildUserMessageAsync(context, ct);
            if (candidates.Count > 0)
            {
                var ranked = await RankAssignTargetsAsync(context, def, candidates, input, ct);
                if (ranked.Count > 0) return false; // 派得出去，走原路由
            }

            // 无下级可派（或下级都不接）：轮到自己。该自答就转完整执行。
            return await ShouldSpeakAsync(context, def, ct);
        }
        catch (Exception ex)
        {
            // 预判失败不阻断主流程：退回原路由行为（宁可轻量回答，也不能把消息卡死）
            _logger.LogDebug(ex, "路由转委派预判失败（回退原路由）：agent={AgentId}", context.AgentId);
            return false;
        }
    }

    /// <summary>
    /// 交付物兜底：用户明确要文件，而指派 / 提升链只回了文本时，找一位挂了对应技能的同事真正生成一次。
    ///
    /// <para>
    /// 设计取舍：不改变原有“找对人”的路由语义（该派还是派、该提升还是提升），
    /// 只在链尾发现“**用户要文件、却没人产出文件**”时补一次真实交付。
    /// 选人口径：在可触达的组织范围内（本岗 + 下级 + 提升链）找挂了匹配技能的岗位；
    /// 找不到就不插手（宁可没文件，也不能乱调技能）。
    /// </para>
    ///
    /// <para>
    /// 副作用：在<b>当前这条消息</b>上继续追加内容（不另开消息），因此产物回档会自然挂到本条消息上，
    /// 前端直接出下载卡片。
    ///
    /// <para>
    /// 实现要点（实测踩坑）：这里<b>不能</b>再走一次 <see cref="InvokeCoreAsync"/>。兜底发生时外层
    /// <c>InvokeAssignmentEscalationAsync</c> 正持有本会话的 <c>sessionLock</c>，而 <see cref="InvokeCoreAsync"/>
    /// 会再取同一把锁 → 直接死锁（表现就是“没有工具调用、没有审批、没有结束、没有错误”的彻底卡死）。
    /// 同时外层尚未 <c>PublishAgentMessageStartAsync</c>，若内层自行开消息，产物附件也挂不到用户看到的那条消息上。
    /// 因此改为<b>在本方法内联跑一次流式</b>：复用外层 <paramref name="messageId"/> 追加正文，
    /// 审批则复用既有 <c>_pendingInteractions</c> 机制挂在外层同一个 run 上，由 <see cref="ResumeRunAsync"/> 继续。
    /// </para>
    ///
    /// <para>
    /// <paramref name="upstreamDraft"/>：组织内其他岗位已产出的内容。交付类技能只需要“正文素材”，
    /// 而计划阶段的各岗位产出就在手边；不传进去的话，交付岗只能照着用户那句原始请求从零重新构思，
    /// 这就是“计划跑了半小时、终稿却与前面成果无关”的根因。
    /// </para>
    ///
    /// <para>
    /// <paramref name="deliverableSkillHint"/>：计划里已点名的文件生成技能。用户那句没带格式词
    /// （如“希望有一些插图”）时，靠它才能知道要出什么文件；否则交付得直接放弃、用户什么都拿不到。
    /// </para>
    /// </summary>
    private async Task<DeliveryOutcome> TrySatisfyDeliveryAsync(
        AgentInvocationContext context, string input, List<ChainNode> hops, CancellationToken outerCt,
        string runId, string? messageId, string? upstreamDraft = null, string? deliverableSkillHint = null)
    {
        // 交付兑底必须有自己的时间预算：外层（计划 + 递归补查）常常已经把那份预算用得差不多，
        // 若共用同一个 token，轮到真正要出文件时它已被取消 → 模型调用瞬时被取消、静默失败。
        // 实测踩到：直接找交付岗 30s 就出文件；走主管（计划+递归补查多轮）却总是拿不到文件。
        using var deliveryCts = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
        deliveryCts.CancelAfter(InstallRunBudget(context, _catalog.GetDefinition(context.AgentId)).Run);
        var ct = deliveryCts.Token;
        try
        {
            // 1) 用户是否要文件？优先看原始消息；没格式词时退回看计划点名的文件技能。
            //   （后者覆盖“接着改一下 / 再加点插图”这类不带格式词的迭代请求）
            var want = WantedDeliverable(context.Content) ?? DeliverableFromSkillId(deliverableSkillHint);
            if (want is null) return new DeliveryOutcome(messageId, false);

            // 2) 链路上已经调过这个技能 / 已有产物 → 不重复做
            var usedSkillIds = hops.Where(h => h.Kind == "skill").Select(h => h.AgentId).ToHashSet(StringComparer.Ordinal);
            if (usedSkillIds.Any(id => AgentGatewayHelpers.SkillMatchesDeliverablePrefix(id, want.Value.SkillPrefix)))
                return new DeliveryOutcome(messageId, false);

            // 3) 在可达组织范围内找一位挂了匹配技能的同事
            var candidate = FindDeliverableOwner(context, want.Value.SkillPrefix);
            if (candidate is null)
            {
                _logger.LogDebug("交付物兜底：组织内无匹配技能的岗位（需要 {Prefix}*）", want.Value.SkillPrefix);
                return new DeliveryOutcome(messageId, false);
            }

            _logger.LogInformation("交付物兜底：用户要求 {Kind}，由 {AgentId} 产出交付文件", want.Value.Label, candidate.AgentId);

            // 4) 让该同事真实跑一次（完整流式路径：工具调用 + 审批 + 产物回档均在官方管道内完成）。
            //    这里的“派单”只是把交付要求显式告知，不伪造对话历史。
            //    提示词必须把“先出文件”放在最前，并把“不得因流程拒交”写进去：
            //      · 否则模型会先去调自己挂的规划/文案类技能（实测：先调 copy_plan 弹审批，Word 一直没生成）；
            //      · 若岗位人设被写成“仅接收定稿才出文件”，它还会反问用户要定稿、空手而回
            //        （实测：word_delivery 回“我需要先跟你对齐交付流程…否则我不会出文件”）。
            var wantSkill = DeliverableSkillFor(candidate, want.Value.SkillPrefix);
            var deliver = BuildDeliveryPrompt(want.Value.Label, wantSkill, context.Content, upstreamDraft: upstreamDraft);
            var prev = AmbientContext.Value;
            var prevChain = SkillChainBuilder.Ambient.Value;
            var prevToolResults = ToolResultCollector.Ambient.Value;
            try
            {
                // 用 with 派生：保留群 / 话题 / 触发者 / 可见性 / 附件等全部上下文，只换执行者与内容
                var sub = context with
                {
                    AgentId = candidate.AgentId,
                    AgentNickname = candidate.Nickname,
                    Content = deliver,
                    // 直接走流式：置 AllMessages 并配合 _streamingOnly，避免又回到“找对人”循环
                    TriggerMode = AgentTriggerMode.AllMessages,
                };
                AmbientContext.Value = sub;
                // 兜底执行也要参与链路可视化与产物收集（与外层作用域隔离，避免污染原链）
                SkillChainBuilder.Ambient.Value = new SkillChainBuilder();
                SkillChainBuilder.Ambient.Value.EnsureRoot(candidate.AgentId, candidate.Nickname ?? candidate.AgentId);
                ToolResultCollector.Ambient.Value = new ToolResultCollector();

                // 最多试两次：模型可能“只回文字、没调工具”或“只出了一张封面”就宣称交付完成。
                // 常见诱因：群历史里已经有它自己“已完成导出”的发言（尤其计划阶段的产物），
                // 它据此以为已经做完了（实测踩到：主管链路下交付岗只回“已完成 Word 导出”，用户没有文件）。
                // 第二次在提示词里明确指出“上一次没有真正调用工具 / 内容不完整”。
                var keptFile = false;
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    if (attempt > 0)
                    {
                        var retryPrompt = BuildDeliveryPrompt(want.Value.Label, wantSkill, context.Content, retryNoToolCall: true, upstreamDraft: upstreamDraft);
                        sub = sub with { Content = retryPrompt };
                        AmbientContext.Value = sub;
                        ToolResultCollector.Ambient.Value = new ToolResultCollector();
                        _logger.LogWarning("交付兑底未达标（无文件或内容过薄），第二次尝试：agent={AgentId}", candidate.AgentId);
                    }

                    var interrupt = (await RunDeliveryStreamAsync(sub, candidate, runId, messageId, ct)).Interrupt;
                    if (interrupt is not null)
                    {
                        // 已挂交互卡：正文待用户决策后由 ResumeRunAsync 继续；产物回档也在那边完成
                        return new DeliveryOutcome(messageId, true, true);
                    }

                    var (hasFile, blocks) = DeliveryResult(want.Value.SkillPrefix);
                    // 阈值按交付物类型取：ppt 看页数、excel 看数据行数、pdf/docx 看内容块数
                    var minContent = MinContentFor(want.Value.SkillPrefix);
                    if (hasFile && blocks >= minContent)
                    {
                        hops.Add(new ChainNode { Kind = "skill", AgentId = candidate.AgentId, AgentNickname = candidate.Nickname, Query = AgentGatewayHelpers.TruncateForChain(deliver) });
                        return new DeliveryOutcome(messageId, false, true);
                    }
                    // 有文件但内容过薄（如只有封面）：宁可留一份薄文件，也别让用户什么都没有
                    if (hasFile) keptFile = true;
                    _logger.LogWarning("交付兑底未达标：agent={AgentId} hasFile={HasFile} 内容量={Blocks}（阈值 {Min}）",
                        candidate.AgentId, hasFile, blocks, minContent);
                }
                if (keptFile)
                {
                    // 两次都偏薄，但至少有文件：接受它（产物已回档），不再给用户“什么都没有”
                    hops.Add(new ChainNode { Kind = "skill", AgentId = candidate.AgentId, AgentNickname = candidate.Nickname, Query = AgentGatewayHelpers.TruncateForChain(deliver) });
                    return new DeliveryOutcome(messageId, false, true);
                }
                // 两次都没产出文件：不谎报，明确告知用户（产物缺失才是真问题）
                _logger.LogWarning("交付兑底两次均未产出文件：agent={AgentId} skill={Skill}", candidate.AgentId, wantSkill);
                if (messageId is not null)
                    await _hub.Value.AppendAgentContentAsync(context.GroupId, messageId,
                        $"（未能生成 {want.Value.Label} 文件：交付环节没有真正调用文件生成技能。请再说一次，或把要写的内容直接发给我。）", ct);
                return new DeliveryOutcome(messageId, false, true);
            }
            finally
            {
                AmbientContext.Value = prev;
                SkillChainBuilder.Ambient.Value = prevChain;
                ToolResultCollector.Ambient.Value = prevToolResults;
            }
        }
        catch (Exception ex)
        {
            // 兜底失败不影响已给出的回答。但必须记到 Warning：此前用 Debug，
            // “交付岗不是本群成员”这类真问题被完全淹没，表现为“用户就是拿不到文件”且毫无线索。
            _logger.LogWarning(ex, "交付物兑底失败（已忽略，用户可能拿不到文件）：agent={AgentId}", context.AgentId);
        }
        return new DeliveryOutcome(messageId, false);
    }

    /// <summary>交付物兜底结果：可能保持原消息，也可能因审批中断而由恢复流接管。
    /// <paramref name="Handled"/> = 本次兜底已经给出用户可见的结果（出文件 / 已下交互卡 / 已明确报失败）；
    /// 为 false 表示它静默放过了（没认出交付物 / 找不到能做的岗位 / 空异常），
    /// 此时调用方需要把计划侧原本不该发的说明补上，否则用户看到一条空消息。</summary>
    private readonly record struct DeliveryOutcome(string? MessageId, bool AwaitingInteraction, bool Handled = false);

    /// <summary>
    /// 交付物兜底的“内联流式”：在<b>外层已开启的那条消息</b>上让交付岗真实跑一次模型循环
    /// （工具调用 / 技能产物 / 审批全部走官方管道）。
    ///
    /// <para>
    /// 返回 <c>Interrupt</c> 非 null 表示期间触发了审批中断，调用方应停止追加正文并把本次 run
    /// 交还给 <see cref="ResumeRunAsync"/>；<c>Text</c> 是本轮累计写进消息的正文
    /// （空 = 本轮什么都没产出，调用方需自行兜底一句人话）。
    /// </para>
    ///
    /// <para>与外层共享 <c>sessionLock</c> 与 <c>_activeRuns[runId]</c>，因此不再单独建锁 / 注册 run。</para>
    /// </summary>
    private async Task<(ToolApprovalRequestContent? Interrupt, string Text)> RunDeliveryStreamAsync(
        AgentInvocationContext context, AgentDefinition def, string runId, string? messageId, CancellationToken ct)
    {
        var visionModel = _options.VisionEnabled
            ? AgentCatalog.ResolveVisionModelName(_options, string.Equals(_options.Provider, "deepseek", StringComparison.OrdinalIgnoreCase))
            : null;
        var agent = _catalog.GetOrCreate(context.AgentId);
        var session = await GetOrCreateSessionAsync(context, agent, ct);
        // typing 是纯展示性的：交付兑底换了一位执行岗，而它不一定是本单聊群的成员
        // （实测踩到：群是为主管建的，代表交付岗广播 typing 会抛 GroupMemberNotExist，
        //  异常被外层 catch 吞掉，整个交付静默不执行、用户拿不到文件）。尽力而为。
        try
        {
            await _hub.Value.BroadcastTypingAsync(new GroupTypingRequest { GroupId = context.GroupId, MemberId = context.AgentId, IsTyping = true }, ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "交付物流：typing 广播失败（忽略）agent={AgentId}", context.AgentId);
        }

        var accumulated = "";
        var reasoningAccumulated = 0;
        ChatMessage userMessage;
        if (!string.IsNullOrWhiteSpace(visionModel))
        {
            (userMessage, var visionTurn) = await BuildVisionUserMessageAsync(context, ct);
            // 与普通流式同一原则：换模型，不换工具（见 GetOrCreateVision 的说明）。
            // 交付环节特别容易撞上：用户往往正是“把这张图的某处改一下并重新出文件”。
            if (visionTurn) agent = _catalog.GetOrCreateVision(context.AgentId, visionModel);
        }
        else
        {
            userMessage = new ChatMessage(ChatRole.User, await BuildUserMessageAsync(context, ct));
        }

        ToolApprovalRequestContent? approval = null;
        await foreach (var update in agent.RunStreamingAsync(userMessage, session, new ChatClientAgentRunOptions(), ct))
        {
            if (update.Text is { Length: > 0 } text)
            {
                var delta = ComputeTextDelta(accumulated, text);
                if (delta.Length > 0)
                {
                    if (messageId is not null)
                        await _hub.Value.AppendAgentContentAsync(context.GroupId, messageId, delta, ct);
                    accumulated += delta;
                }
            }
            foreach (var rc in update.Contents.OfType<TextReasoningContent>())
            {
                if (rc.Text is not { Length: > 0 } r) continue;
                if (reasoningAccumulated >= MaxReasoningTotalChars) continue;
                var remaining = MaxReasoningTotalChars - reasoningAccumulated;
                var rd = r.Length > remaining ? r[..remaining] : r;
                reasoningAccumulated += rd.Length;
                if (messageId is not null)
                    await AppendReasoningAsync(context.GroupId, messageId, rd, ct);
            }
            foreach (var callFc in update.Contents.OfType<FunctionCallContent>())
            {
                await _hub.Value.BroadcastAsync(context.GroupId, new ToolCallStartEvent
                {
                    ToolCallId = callFc.CallId ?? "tool_" + IdGenerator.NewId(),
                    ToolCallName = callFc.Name,
                    ToolArguments = callFc.Arguments is { Count: > 0 } ? JsonSerializer.Serialize(callFc.Arguments) : null,
                    ParentMessageId = messageId,
                    GroupId = context.GroupId,
                    TriggerUserId = context.TriggerUserId,
                    Timestamp = _hub.Value.NowMs,
                }, ct: ct);
            }
            foreach (var fr in update.Contents.OfType<FunctionResultContent>())
            {
                if (fr.Result is null) continue;
                ToolResultCollector.Ambient.Value?.Add(AgentGatewayHelpers.DescribeToolResult(fr.Result));
                await _hub.Value.BroadcastAsync(context.GroupId, new ToolCallResultEvent
                {
                    ToolCallId = fr.CallId ?? "tool_" + IdGenerator.NewId(),
                    ParentMessageId = messageId,
                    GroupId = context.GroupId,
                    Result = AgentGatewayHelpers.DescribeToolResult(fr.Result),
                    Timestamp = _hub.Value.NowMs,
                }, ct: ct);
            }
            foreach (var apr in update.Contents.OfType<ToolApprovalRequestContent>())
            {
                approval = apr;
                break;
            }
            if (approval is not null) break;
        }

        if (approval is null) return (null, accumulated);

        // 审批中断：复用外层 runId（不另开 run），先把这次兜底已追加的正文清掉，保持“决策前正文为空”的一致体验
        if (messageId is not null)
            await _hub.Value.ResetAgentContentAsync(context.GroupId, messageId, ct);
        var fc = approval.ToolCall as FunctionCallContent;
        var isClientTool = fc is not null
            && _catalog.GetAgentClientToolNames(context.AgentId).Contains(fc.Name, StringComparer.Ordinal);
        var interruptId = "interrupt_" + IdGenerator.NewId();
        var clientSkill = isClientTool ? GetSkillById(fc!.Name) : null;
        var clientRunner = clientSkill is null ? null : EffectiveClientRunner(clientSkill);
        _pendingInteractions[interruptId] = new PendingInteraction(
            interruptId, context.GroupId, context.AgentId, runId,
            messageId ?? "", context.TriggerUserId, context.TopicId, _hub.Value.NowMs, context,
            ExternalInterruptId: null,
            ExternalToolCallId: null, ExternalToolName: null, ExternalToolArguments: null,
            Agent: agent, Session: session, ApprovalRequest: approval,
            BridgeClient: null, SuppressMessage: true);
        await PurgeExpiredInteractions();
        await _hub.Value.BroadcastAsync(context.GroupId, new AgentInteractionRequestEvent
        {
            GroupId = context.GroupId,
            MessageId = messageId ?? "",
            ThreadId = context.ThreadId,
            RunId = runId,
            InterruptId = interruptId,
            ToolCallId = fc?.CallId ?? "tool_" + IdGenerator.NewId(),
            ToolName = fc?.Name ?? "unknown",
            ToolArguments = fc?.Arguments is { } args ? JsonSerializer.SerializeToElement(args) : null,
            Message = isClientTool
                ? $"智能体「{def.Nickname}」请求你在本机执行客户端技能「{fc?.Name}」"
                : $"智能体「{def.Nickname}」请求你确认：是否执行操作「{fc?.Name}」？",
            Kind = isClientTool ? "client_tool" : "approval",
            ClientRunner = clientRunner,
            TargetMemberId = context.TriggerUserId,
            Timestamp = _hub.Value.NowMs,
        }, ct: ct);
        _logger.LogInformation("交付物兜底触发交互中断：agent={AgentId} interrupt={InterruptId}", context.AgentId, interruptId);
        return (approval, accumulated);
    }

    /// <summary>
    /// 本次兜底运行产出的文件及其<b>内容块数</b>（技能返回值里的 <c>produce_file</c> 与 <c>blocks</c>）。
    ///
    /// <para>
    /// 为什么不能只看模型正文：模型常把“已完成导出”写进正文而根本没调工具 ——
    /// 用户拿着这句话去找文件却什么也没有（实测踩到）。以工具真实返回为准。
    /// </para>
    ///
    /// <para>
    /// 为什么还要看 blocks：文件确实生了、但只有标题与副标题（blocks = 4）也是失败交付 ——
    /// 用 blocks 才能区分“真出了文档”与“只出了一张封面”（实测踩到多次）。
    /// </para>
    /// </summary>
    private static (bool HasFile, int Blocks) DeliveryResult(string? skillPrefix)
    {
        return ParseDeliveryResult(ToolResultCollector.Ambient.Value?.Text, skillPrefix);
    }

    /// <summary>测试钩子：从技能返回文本解析“是否有产物 / 内容量”。</summary>
    internal static (bool HasFile, int Blocks) ParseDeliveryResult(string? text, string? skillPrefix = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return (false, 0);
        var hasFile = text.Contains("produce_file", StringComparison.OrdinalIgnoreCase);
        // 度量口径<b>按交付物类型分别选</b>，不能混着取最大值：各技能报的字段语义不同，
        // 混取会让“一张三行的表”因为 sheets=1 而被当成内容丰富（或反之误报很薄）。
        var amount = MetricFor(skillPrefix) switch
        {
            "slides" => MaxJsonInt(text, "slides"), // pptx：页数
            "rows" => MaxJsonInt(text, "rows"),     // xlsx：数据行数（一张表通常只有 1~3 张，页数口径会永远不达标）
            "blocks" => MaxJsonInt(text, "blocks"), // docx / pdf：内容块数（pdf 另报 pages）
            // 未知类型（旧调用点 / 测试钩子）：取各类度量最大，保持历史口径
            _ => new[] { "blocks", "slides", "pages", "rows" }.Max(f => MaxJsonInt(text, f)),
        };
        return (hasFile, amount);
    }

    /// <summary>某交付前缀对应的“内容量”度量字段；空前缀 = 未知类型，返回 <c>auto</c>。</summary>
    private static string MetricFor(string? skillPrefix)
    {
        var p = skillPrefix ?? "";
        if (p.Length == 0) return "auto";
        if (p.StartsWith("pptx", StringComparison.OrdinalIgnoreCase)) return "slides";
        if (p.StartsWith("xlsx", StringComparison.OrdinalIgnoreCase)) return "rows";
        return "blocks";
    }

    /// <summary>某交付前缀的最低内容量：低于它视为“只有空壳”，会重试一次。</summary>
    private static int MinContentFor(string? skillPrefix) => MetricFor(skillPrefix) switch
    {
        "slides" => MinDeliverySlides,
        "rows" => MinDeliveryRows,
        _ => MinDeliveryBlocks,
    };

    /// <summary>从文本里取某个数字字段的最大值（技能返回 JSON 可能有多个/多份）。</summary>
    private static int MaxJsonInt(string text, string field)
    {
        var max = 0;
        var pattern = "\"" + field + "\"\\s*:\\s*(\\d+)";
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text, pattern))
            if (int.TryParse(m.Groups[1].Value, out var v) && v > max) max = v;
        return max;
    }

    /// <summary>交付文档的最少内容量：低于它视为“只有封面、没有正文”，会重试一次。
    /// docx / pdf 按内容块数（低于 5 基本只有封面）；ppt 按页数（低于 3 撑不起一场演示）；
    /// excel 按数据行数（低于 3 行基本只有表头）。</summary>
    private const int MinDeliveryBlocks = 5;
    private const int MinDeliverySlides = 3;
    private const int MinDeliveryRows = 3;

    /// <summary>交付兑底提示词里“上游已产出素材”的上限。计划各步产出可能很长，
    /// 取尾部（靠后的步通常是汇总/定稿），避免把下游上下文撑爆。</summary>
    internal const int MaxUpstreamDraftChars = 12000;

    /// <summary>测试钩子：交付兑底提示词（含“不得流程拒交”与“内容必须完整”两条硬要求）。
    /// <paramref name="upstreamDraft"/> 为组织内已产出的内容：交付岗应当直接拿它当正文素材。</summary>
    internal static string BuildDeliveryPrompt(string label, string? skillId, string userContent,
        bool retryNoToolCall = false, string? upstreamDraft = null)
    {
        // 第二次尝试：明确告诉模型“你上一次只说了话、没调工具”，把“历史里那句话说过了”的错觉打掉。
        var retryLead = retryNoToolCall
            ? "【重要】你上一次只回复了文字、**并没有真正调用工具**，用户因此没有拿到文件。"
              + "不要再说“已完成”，也不要相信对话历史里任何“已完成导出”的说法——那些都不是真的。"
              + "现在必须真正调用工具。\n\n"
            : "";
        var hardRule = "\n\n【硬性要求】用户是直接向你要这份文件的，你必须现在就产出文件："
            + "不要反问用户要定稿/合规结论/审批结果，不要以“流程未走完”为由拒交；"
            + "材料不完整也先出稿，把不确定的地方在文件里列为待确认项。";
        // 内容完整性：交付类技能的入参是结构化 sections，模型很容易只给“标题 + 副标题 + 作者”就收工
        // （实测踩到多次：Word 只有标题 / 只有 3 段，sections 是空的）。
        // 因此提示词必须把“先把正文写出来、再逐节填进 sections”写成硬要求。
        var contentRule = "\n\n【内容必须完整（很重要）】产出后用户拿到的应该是一份可直接交付的完整成稿，不是提纲或骨架："
            + "先把你应该写的内容完整组织好，再逐节写进技能的 sections 参数——"
            + "它们接收 heading(level 1-3) / paragraph / bullets / numbered / quote / table({headers,rows}) / toc 等结构；"
            + "**每一节都要有实质正文（段落 / 列表 / 表格），绝不允许只传标题、副标题、作者就收工**（那样文档里会没有任何内容）。"
            + "若组织里同时有能直接吃 Markdown 正文的导出技能，优先用它（把完整 Markdown 正文交给它转换）。";
        // 组织内已产出的内容：直接当正文素材，不要让模型重新构思一遍。
        // 实测踩到：计划阶段各岗位已写好稿子，交付岗却只盯着用户那句原始请求重写，
        // 于是终稿与前面成果对不上，用户还白等了整个计划的时间。
        var draft = string.IsNullOrWhiteSpace(upstreamDraft)
            ? ""
            : "\n\n【组织内已产出的内容（这是你的正文素材，直接据此编写并补全，不要重新构思一遍）】\n"
              + (upstreamDraft.Trim().Length <= MaxUpstreamDraftChars
                  ? upstreamDraft.Trim()
                  : "…（前文从前略）\n" + upstreamDraft.Trim()[^MaxUpstreamDraftChars..]);
        return retryLead + (skillId is null
            ? $"用户要求交付 {label} 文件。请直接调用你的文件生成技能，把完整内容生成为文件后简短回报。" + hardRule + contentRule
              + $"\n\n【用户原始请求】\n{userContent}" + draft
            : $"用户要求交付 {label} 文件。请调用文档生成技能 {skillId}，把完整内容生成为文件后简短回报"
              + $"（不要先反问用户要材料，也不要调用其它无关技能）。" + hardRule + contentRule
              + $"\n\n【用户原始请求】\n{userContent}" + draft);
    }

    /// <summary>
    /// 由文件生成技能 id 反推交付物类型（前缀 + 展示名）。
    /// 用于「用户那句话没带格式词、但计划已经点名要调哪个文件技能」的场景：
    /// 实测：上一步“我希望ppt是绿色的”能出文件（句子里恰好有 ppt），
    /// 下一步“希望有一些插图”同样意图却什么都没拿到 —— 只因交付判断只读当前那句话。
    /// 认不出来返回 null（不插手）。
    /// </summary>
    internal static (string SkillPrefix, string Label)? DeliverableFromSkillId(string? skillId)
    {
        if (string.IsNullOrWhiteSpace(skillId)) return null;
        if (AgentGatewayHelpers.SkillMatchesDeliverablePrefix(skillId, "pptx_")) return ("pptx_", "演示文稿");
        if (AgentGatewayHelpers.SkillMatchesDeliverablePrefix(skillId, "xlsx_")) return ("xlsx_", "Excel 表格");
        if (AgentGatewayHelpers.SkillMatchesDeliverablePrefix(skillId, "pdf_")) return ("pdf_", "PDF 文档");
        if (AgentGatewayHelpers.SkillMatchesDeliverablePrefix(skillId, "docx_")) return ("docx_", "Word 文档");
        return null;
    }

    /// <summary>用户要的交付物类型（技能前缀用于在组织里匹配）。识别不出来返回 null。测试钩子（internal）。</summary>
    internal static (string SkillPrefix, string Label)? WantedDeliverable(string? userText)
    {
        if (string.IsNullOrWhiteSpace(userText)) return null;
        var t = userText.ToLowerInvariant();
        bool Has(params string[] keys) => keys.Any(k => t.Contains(k, StringComparison.OrdinalIgnoreCase));

        // ① 明确的格式词（含扩展名与英文名）优先：它们指向唯一的交付物类型。
        //    pptx 必须排在“表格”之前 —— 一份 PPT 里的“表格”是<b>页内元素</b>，不是要交付 Excel。
        //    实测踩到：一句“做份 PPT…含一张对比表格”被判成 Excel 交付，
        //    于是去找 xlsx 技能、找不到就静默放弃，用户什么也没拿到。
        if (Has("pptx", "powerpoint", "ppt", "幻灯片", "演示文稿")) return ("pptx_", "演示文稿");
        if (Has("xlsx", "excel", "电子表格", "工作簿")) return ("xlsx_", "Excel 表格");
        if (Has("docx", "word", ".doc", "文稿")) return ("docx_", "Word 文档");
        if (Has("pdf")) return ("pdf_", "PDF 文档");

        // ② 中文泛称（容易与页内元素 / 普通名词混淆）放最后
        if (Has("表格")) return ("xlsx_", "Excel 表格");
        if (Has("文档")) return ("docx_", "Word 文档");
        return null;
    }

    /// <summary>
    /// 在可达组织范围内（本岗 → 下级（递归）→ 提升链（递归））找第一位挂了
    /// <paramref name="skillPrefix"/> 开头技能的同事。
    /// 只沿组织连接走，不全局搜库 —— 保持“团队自己的事自己干”的语义。
    /// </summary>
    private AgentDefinition? FindDeliverableOwner(AgentInvocationContext context, string skillPrefix)
    {
        var root = _catalog.GetDefinition(context.AgentId);
        if (root is null) return null;

        var visited = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<AgentDefinition>();
        queue.Enqueue(root);
        AgentDefinition? fallback = null;

        while (queue.Count > 0 && visited.Count < 60)
        {
            var d = queue.Dequeue();
            if (!visited.Add(d.AgentId)) continue;

            if (HasSkillWithPrefix(d, skillPrefix))
            {
                // 优先选“只有交付能力、不主管别人”的岗位（真正的执行岗）；否则记住作为备选
                if (d.AssignmentIds is not { Count: > 0 }) return d;
                fallback ??= d;
            }

            foreach (var sub in (d.AssignmentIds ?? []).Concat([d.EscalationAgentId]))
            {
                if (string.IsNullOrWhiteSpace(sub) || visited.Contains(sub)) continue;
                if (_catalog.GetDefinition(sub) is { } sd) queue.Enqueue(sd);
            }
        }
        return fallback;
    }

    /// <summary>取该岗位身上第一个匹配交付前缀的技能 ID（用于把兜底提示词直接指向它）。取不到返回 null。</summary>
    private string? DeliverableSkillFor(AgentDefinition def, string prefix)
    {
        if (def.SkillDefIds is not { Count: > 0 }) return null;
        foreach (var id in def.SkillDefIds)
            if (AgentGatewayHelpers.SkillMatchesDeliverablePrefix(id, prefix)) return id;
        return null;
    }

    private bool HasSkillWithPrefix(AgentDefinition def, string prefix)
    {
        if (def.SkillDefIds is not { Count: > 0 }) return false;
        var catalog = _skillCatalog.Value;
        if (catalog is null) return false;
        foreach (var id in def.SkillDefIds)
        {
            // 前缀式（docx_report）与后缀式（md_to_docx）都算：只判 StartsWith 会漏掉后者，
            // 于是“用户要 Word、团队里确实有人能产 Word”却找不到交付人（实测踩到）。
            if (AgentGatewayHelpers.SkillMatchesDeliverablePrefix(id, prefix)) return true;
        }
        return false;
    }

    // 交付物兜底专用：AsyncLocal 标记“本次调用只走流式阶段”（避免再次命中 org_route 造成递归）
    private static readonly AsyncLocal<bool> _streamingOnly = new();

    /// <summary>让单个数字员工就指派/提升请求实际作答（模型一次 run），返回最终文本。</summary>
    private async Task<string> RunRouteAnswerAsync(AgentInvocationContext context, string agentId, string input, CancellationToken ct)
    {
        var host = _catalog.GetDefinition(context.AgentId);
        var hostName = host?.Nickname ?? context.AgentId;
        var agent = _catalog.GetOrCreate(agentId);
        var prompt = "你正被「" + hostName + "」委派处理该请求。请结合你的职责直接给出专业答复。\n\n" + input;
        var session = await agent.CreateSessionAsync(ct);
        var resp = await agent.RunAsync([new ChatMessage(ChatRole.User, prompt)], session, null, ct);
        if (!string.IsNullOrWhiteSpace(resp.Text)) return resp.Text.Trim();
        foreach (var m in resp.Messages)
        {
            if (m.Role != ChatRole.Assistant) continue;
            if (!string.IsNullOrWhiteSpace(m.Text)) return m.Text.Trim();
            foreach (var c in m.Contents)
                if (c is TextContent tc && !string.IsNullOrWhiteSpace(tc.Text)) return tc.Text.Trim();
        }
        return "";
    }

    /// <summary>停止指定运行触发者本人或同群管理员可调；命中并已取消返回 true。</summary>
    public bool StopRun(string runId, string operatorId, string groupId, bool isManager)
    {
        if (!_activeRuns.TryGetValue(runId, out var run)) return false;
        if (run.GroupId != groupId) return false;
        if (run.TriggerUserId != operatorId && !isManager) return false;
        try { run.Cts.Cancel(); }
        catch { return false; }
        _activeRuns.TryRemove(runId, out _);
        _logger.LogInformation("用户 {Operator} 停止智能体运行：run={RunId} agent={AgentId}", operatorId, runId, run.AgentId);
        return true;
    }

    /// <summary>
    /// AG-UI 桥接路径：发送用户消息（含历史窗口与附件上下文）并订阅其流式回复，回灌群聊。
    /// 传输分派：ws/wss → 内置 WebSocket 客户端；http/https + hub → <see cref="AguiBridgeHttpHubClient"/>（本 Hub 的 HTTP 面）；
    /// http/https + standard → 官方 AGUIChatClient（Microsoft.Agents.AI.AGUI，与 Microsoft 参考示例一致，
    /// 自动处理 AGUI.AspNetCore 的 RunAgentInput 上行与事件流下行）。
    /// </summary>
    private async Task<AgentInvocationResult> InvokeBridgeAsync(AgentInvocationContext context, AgentDefinition def, CancellationToken ct)
    {
        var mode = def.BridgeMode ?? _options.AguiBridge?.Mode ?? "standard";
        var token = def.BridgeToken ?? _options.AguiBridge?.Token;
        var connectTimeout = _options.AguiBridge?.ConnectTimeoutSeconds ?? 10;
        // 端点优先级：智能体单独配置 > 全局 AguiBridge:Endpoint；两者都未配置时桥接角色无法运行（防御性抛错）
        var endpoint = def.BridgeEndpoint ?? _options.AguiBridge?.Endpoint;
        if (string.IsNullOrWhiteSpace(endpoint))
            throw new InvalidOperationException("桥接端点未配置：智能体 BridgeEndpoint 与全局 AguiBridge:Endpoint 均为空（桥接角色必须配置外部 AG-UI 服务端点）");
        var runId = "run_" + IdGenerator.NewId();
        // 外部 AG-UI 会话按话题隔离：main 话题沿用群级 threadId，非 main 话题追加话题后缀
        var externalThreadId = AgentGatewayHelpers.BuildExternalThreadId(context.ThreadId, context.TopicId);

        await _hub.Value.BroadcastTypingAsync(new GroupTypingRequest
        {
            GroupId = context.GroupId,
            MemberId = context.AgentId,
            IsTyping = true,
        }, ct);

        string? replyId = null;
        IAguiBridgeClient? bridgeClient = null;
        var interactionPending = false; // 中断时保留桥接连接供恢复，不随本方法结束释放
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(InstallRunBudget(context, def).Run); // 外部服务挂起保护（按任务复杂度放宽）
        var runCt = timeoutCts.Token;
        _activeRuns[runId] = new ActiveRun(timeoutCts, context.GroupId, context.AgentId, context.TriggerUserId); // 注册：支持「停止生成」
        // 与本地路径一致的会话锁：同一桥接智能体并发触发时串行化，防止同一桥接智能体多路连接外部服务
        var sessionLock = GetSessionLock(context.ThreadId);
        var acquired = false;
        try
        {
            // WaitAsync 移入 try：未获锁就取消/超时时走 catch + finally，保证 typing=false 一定广播（避免 typing 卡死）
            await sessionLock.WaitAsync(runCt);
            acquired = true;

            // 桥接端点合法性校验（H3 网关侧防御）：非法端点按连接失败路径处理（AGENT_BRIDGE_ERROR）
            var endpointError = BridgeEndpointValidator.GetError(endpoint);
            // 公网部署收紧模式：域名解析后逐 IP 拦截环回 / 私网 / 链路本地（防 localhost / 内网域名 / DNS rebinding SSRF）
            if (endpointError is null && _options.AguiBridge is { AllowPrivateEndpoints: false })
                endpointError = BridgeEndpointValidator.ValidateResolved(endpoint);
            if (endpointError is not null)
                throw new InvalidOperationException($"桥接端点配置非法：{endpointError}");

            if (AgentGatewayHelpers.IsWebSocketEndpoint(endpoint))
            {
                bridgeClient = new AguiBridgeClient(endpoint, mode, token, context.AgentId, _logger, connectTimeout);
                await bridgeClient.ConnectAsync(context.AgentId, runCt);
                var outboundId = "msg_" + IdGenerator.NewId();
                var userContent = await BuildBridgeUserMessageAsync(context, externalThreadId, runCt);
                await bridgeClient.SendUserMessageAsync(outboundId, externalThreadId, runId, userContent, context.GroupId, context.AgentId, runCt);
            }
            else
            {
                // HTTP(S)：standard → 自建 SSE 解析（与 WS 共用 AguiBridgeProtocol，含审批中断检测）；
                // hub → 本 Hub 的 HTTP 面（SSE 订阅 + POST 群消息）。
                bridgeClient = string.Equals(mode, "hub", StringComparison.OrdinalIgnoreCase)
                    ? new AguiBridgeHttpHubClient(endpoint, token, context.AgentId, _logger)
                    : new AguiBridgeHttpStandardClient(endpoint, token, _logger);
                await bridgeClient.ConnectAsync(context.AgentId, runCt);
                var outboundId = "msg_" + IdGenerator.NewId();
                var userContent = await BuildBridgeUserMessageAsync(context, externalThreadId, runCt);
                await bridgeClient.SendUserMessageAsync(outboundId, externalThreadId, runId, userContent, context.GroupId, context.AgentId, runCt);
            }

            // 群内应答 START（assistant）
            var started = await _hub.Value.PublishAgentMessageStartAsync(new AgentMessageStartInput
            {
                GroupId = context.GroupId,
                AgentId = context.AgentId,
                RunId = runId,
                TopicId = context.TopicId,
                ReplyToMessageId = context.TriggerMessageId,
                // 回复不再携带 @ 信息（触发消息的提及仅用于触发，不回显到智能体回复）
                Mentions = [],
                MentionAll = false,
                // 回复继承触发消息的可见性：私密 / 定向内容不向全群广播
                Visibility = context.Visibility,
                VisibleMemberIds = context.VisibleMemberIds ?? [],
            }, runCt);
            replyId = started.MessageId;

            // 订阅外部流式回复并回灌；standard 方言的 ASSISTANT_MESSAGE 可能是累计文本，统一按增量处理
            var accumulated = "";
            var finished = false;
            var receivedContent = false; // 本次流是否收到过实质内容（正文 / 思考 / 工具 / 附件）
            AguiBridgeEvent? interrupt = null;
            var bridgeAttachments = new List<BridgeAttachment>(); // 外部 AG-UI 服务附件（ATTACHMENT_* / START 附件）累积，消息结束时一次性回灌
            await foreach (var evt in bridgeClient.ReceiveAsync(runCt))
            {
                switch (evt.Type)
                {
                    case "content" when evt.Delta is { Length: > 0 }:
                    {
                        receivedContent = true;
                        var delta = ComputeTextDelta(accumulated, evt.Delta);
                        if (delta.Length > 0)
                        {
                            await _hub.Value.AppendAgentContentAsync(context.GroupId, replyId, delta, runCt);
                            accumulated += delta;
                            // 正文累计上限：standard 方言的 delta 是累计文本（前缀截断不影响增量计算），防无限长正文撑爆内存
                            if (accumulated.Length > MaxBridgeAccumulatedChars)
                            {
                                _logger.LogWarning("AG-UI 桥接回复正文累计超过 {Max} 字符，已截断：agent={AgentId} run={RunId}",
                                    MaxBridgeAccumulatedChars, context.AgentId, runId);
                                accumulated = accumulated[..MaxBridgeAccumulatedChars];
                            }
                        }
                        break;
                    }
                    case "reasoning" when evt.Delta is { Length: > 0 }:
                        receivedContent = true;
                        await AppendReasoningAsync(context.GroupId, replyId, evt.Delta, runCt);
                        break;
                    // 工具调用 / 动作开始（ACTION_STARTED）：统一以「🔧」过程行广播
                    case "tool" or "action":
                        receivedContent = true;
                        await BroadcastBridgeToolCallAsync(context, replyId, evt, runCt);
                        break;
                    // 工具参数（TOOL_CALL_END + 分帧累积）与执行结果（TOOL_CALL_RESULT）：前端展示调用详情
                    case "tool_end":
                        await BroadcastBridgeToolArgsAsync(context, replyId, evt, runCt);
                        break;
                    case "tool_result":
                        await BroadcastBridgeToolResultAsync(context, replyId, evt, runCt);
                        break;
                    // 任务进度快照（ACTIVITY_SNAPSHOT todo 流）：前端实时进度块
                    case "todo":
                        await BroadcastBridgeTodoAsync(context, replyId, evt, runCt);
                        break;
                    // 外部附件（ATTACHMENT_STARTED url 型 / TEXT_MESSAGE_START.attachments / hub 回复 START 附件）：累积到消息结束一并回灌
                    case "attachment" when evt.Attachments is { Count: > 0 }:
                        bridgeAttachments.AddRange(evt.Attachments);
                        break;
                    case "interrupt":
                        interrupt = evt;
                        break;
                    case "end":
                        finished = true;
                        break;
                    case "error":
                        await _hub.Value.BroadcastAsync(context.GroupId, new RunErrorEvent
                        {
                            GroupId = context.GroupId,
                            ErrorCode = evt.ErrorCode ?? "AGENT_BRIDGE_ERROR",
                            Message = evt.ErrorMessage ?? "外部 AG-UI 服务返回错误",
                            Timestamp = _hub.Value.NowMs,
                        }, ct: runCt);
                        finished = true;
                        break;
                }
                if (interrupt is not null || finished) break;
            }

            // 流在收到 end/error 前断开（连接中断）：已收到实质内容后关闭 → 视为正常完成（AG-UI 允许回复完成后
            // 直接关闭连接而不发 end 事件）；完全未收到内容即断开 → 按断线处理，提示回复可能不完整
            if (!finished && interrupt is null)
            {
                if (receivedContent)
                {
                    finished = true;
                    _logger.LogInformation("AG-UI 桥接流随连接关闭自然结束（已收到内容，未显式 end）：agent={AgentId} run={RunId}",
                        context.AgentId, runId);
                }
                else
                {
                    await _hub.Value.BroadcastAsync(context.GroupId, new RunErrorEvent
                    {
                        GroupId = context.GroupId,
                        ErrorCode = "AGENT_BRIDGE_DISCONNECTED",
                        Message = "外部 AG-UI 服务连接中断，回复可能不完整",
                        Timestamp = _hub.Value.NowMs,
                    }, ct: runCt);
                }
            }

            if (interrupt is not null)
            {
                // 人机交互中断（协议 4.5）：先清空已回灌的中间内容——等用户反馈、外部服务继续运行结束后，
                // 最终结果一次性回灌到同一消息；保存运行现场 + 广播交互请求（仅触发者可决策）。
                await _hub.Value.ResetAgentContentAsync(context.GroupId, replyId, runCt);
                var interruptId = "interrupt_" + IdGenerator.NewId();
                interactionPending = true;
                _pendingInteractions[interruptId] = new PendingInteraction(
                    interruptId, context.GroupId, context.AgentId, runId, replyId,
                    context.TriggerUserId, context.TopicId, _hub.Value.NowMs, context,
                    ExternalInterruptId: interrupt.InterruptId,
                    ExternalToolCallId: interrupt.ToolCallId,
                    ExternalToolName: interrupt.ToolName,
                    ExternalToolArguments: interrupt.ToolArguments,
                    Agent: null, Session: null, ApprovalRequest: null,
                    BridgeClient: bridgeClient,
                    InputField: interrupt.InputField,
                    ResponseSchema: interrupt.ResponseSchema,
                    Questions: interrupt.Questions);
                await PurgeExpiredInteractions();

                await _hub.Value.BroadcastAsync(context.GroupId, new AgentInteractionRequestEvent
                {
                    GroupId = context.GroupId,
                    MessageId = replyId,
                    ThreadId = context.ThreadId,
                    RunId = runId,
                    InterruptId = interruptId,
                    ToolCallId = interrupt.ToolCallId ?? "tool_" + IdGenerator.NewId(),
                    ToolName = interrupt.ToolName ?? "unknown",
                    ToolArguments = interrupt.ToolArguments,
                    Message = interrupt.InterruptMessage ?? $"智能体「{def.Nickname}」请求你确认：是否执行操作「{interrupt.ToolName}」？",
                    Kind = interrupt.InterruptKind ?? "approval",
                    InputField = interrupt.InputField,
                    Options = interrupt.InterruptOptions,
                    ResponseSchema = interrupt.ResponseSchema,
                    Questions = interrupt.Questions,
                    TargetMemberId = context.TriggerUserId,
                    Timestamp = _hub.Value.NowMs,
                }, ct: runCt);

                _logger.LogInformation("AG-UI 桥接运行中断等待交互：run={RunId} interrupt={InterruptId} target={Target}",
                    runId, interruptId, context.TriggerUserId);
                // 外部服务已处理触发消息（中断等待决策）：推进游标，恢复后从本次回复消息之后继续增量
                AdvanceBridgeCursor(context.AgentId, externalThreadId, replyId);
                return new AgentInvocationResult(false, runId, "AGENT_AWAITING_INTERACTION");
            }

            if (bridgeAttachments.Count > 0)
            {
                try { await _hub.Value.AppendAgentAttachmentsAsync(context.GroupId, replyId, AgentGatewayHelpers.ToAttachmentInfos(bridgeAttachments), runCt); }
                catch (Exception ex) { _logger.LogWarning(ex, "AG-UI 桥接附件回灌失败：agent={AgentId}", context.AgentId); }
            }
            await _hub.Value.EndAgentMessageAsync(context.GroupId, replyId, runCt);
            // 仅正常完成（收到 end 事件）才推进外部会话增量游标：断线 / 异常路径保持旧游标，
            // 下次触发会重发上次未确认的消息（避免外部服务实际未处理触发消息却丢上下文）
            AdvanceBridgeCursor(context.AgentId, externalThreadId, replyId);
            _logger.LogInformation("AG-UI 桥接完成：agent={AgentId} run={RunId} endpoint={Endpoint}", context.AgentId, runId, endpoint);
            return new AgentInvocationResult(true, runId, null);
        }
        catch (OperationCanceledException)
        {
            await SafeEndAsync(context, replyId);
            return new AgentInvocationResult(false, runId, "AGENT_BRIDGE_CANCELLED");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AG-UI 桥接运行失败：agent={AgentId} endpoint={Endpoint}", context.AgentId, endpoint);
            await SafeEndAsync(context, replyId);
            await _hub.Value.BroadcastAsync(context.GroupId, new RunErrorEvent
            {
                GroupId = context.GroupId,
                ErrorCode = "AGENT_BRIDGE_ERROR",
                Message = ex.Message,
                Timestamp = _hub.Value.NowMs,
            }, ct: CancellationToken.None);
            return new AgentInvocationResult(false, runId, "AGENT_BRIDGE_ERROR");
        }
        finally
        {
            if (acquired) sessionLock.Release(); // 未获得锁时不 Release（避免 SemaphoreFullException）
            _activeRuns.TryRemove(runId, out _); // 运行结束 / 取消：注销停止能力
            // 交互中断时保留桥接连接供恢复（恢复完成后在恢复流程释放）；否则立即释放
            if (bridgeClient is not null && !interactionPending) await bridgeClient.DisposeAsync();
            await _hub.Value.BroadcastTypingAsync(new GroupTypingRequest
            {
                GroupId = context.GroupId,
                MemberId = context.AgentId,
                IsTyping = false,
            }, CancellationToken.None);
        }
    }

    /// <summary>推进外部会话增量游标：以本次 agent 回复消息为「上次节点」，下次触发只发其后的本话题新消息；
    /// 通知持久化（ChangeHub 脏位 → 定时落盘），网关重启后游标不丢。仅成功 / 中断等待决策路径调用。</summary>
    private void AdvanceBridgeCursor(string agentId, string externalThreadId, string replyId)
    {
        if (string.IsNullOrEmpty(replyId)) return;
        _bridgeCursors[$"{agentId}|{externalThreadId}"] = replyId;
        _changes?.Notify();
    }

    /// <summary>桥接 WS / hub 方言恢复：外部服务已收到恢复指令（resume / AGENT_INTERACTION_RESOLVE），
    /// 继续消费其事件流，最终结果追加到中断时保留的同一消息；若再次中断则递归保存新的交互请求。
    /// 运行结束才结束消息（中间内容在中断时已清空）。恢复结束后释放桥接连接。</summary>
    private async Task ResumeBridgeStreamAsync(PendingInteraction pending, CancellationToken ct)
    {
        var bridgeClient = pending.BridgeClient!;
        var runId = pending.RunId; // 保持首轮 runId：外部服务按 threadId+runId 关联中断，多轮 resume 必须一致
        var messageId = pending.MessageId; // 复用中断时保留的消息（内容已清空，等待最终结果）
        var interactionPending = false; // 恢复流再次中断时保留桥接连接供下一轮决策
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(InstallRunBudget(pending.Context, _catalog.GetDefinition(pending.Context.AgentId)).Run);
            var runCt = timeoutCts.Token;
            var sessionLock = GetSessionLock(pending.Context.ThreadId);
            var acquired = false;
            try
            {
                // WaitAsync 移入 try：未获锁就取消/超时时走 catch，不会在 finally 里对未获取的锁 Release
                await sessionLock.WaitAsync(runCt);
                acquired = true;
                var accumulated = "";
                AguiBridgeEvent? interrupt = null;
                var finished = false;
                var bridgeAttachments = new List<BridgeAttachment>(); // 恢复流中外部附件累积，运行结束一并回灌
                await foreach (var evt in bridgeClient.ReceiveAsync(runCt))
                {
                    switch (evt.Type)
                    {
                        case "content" when evt.Delta is { Length: > 0 }:
                        {
                            var delta = ComputeTextDelta(accumulated, evt.Delta);
                            if (delta.Length > 0)
                            {
                                await _hub.Value.AppendAgentContentAsync(pending.GroupId, messageId, delta, runCt);
                                accumulated += delta;
                                // 正文累计上限：standard 方言的 delta 是累计文本（前缀截断不影响增量计算），防无限长正文撑爆内存
                                if (accumulated.Length > MaxBridgeAccumulatedChars)
                                {
                                    _logger.LogWarning("AG-UI 桥接恢复流正文累计超过 {Max} 字符，已截断：agent={AgentId} run={RunId}",
                                        MaxBridgeAccumulatedChars, pending.AgentId, runId);
                                    accumulated = accumulated[..MaxBridgeAccumulatedChars];
                                }
                            }
                            break;
                        }
                        case "reasoning" when evt.Delta is { Length: > 0 }:
                            await AppendReasoningAsync(pending.GroupId, messageId, evt.Delta, runCt);
                            break;
                        case "tool" or "action":
                            await BroadcastBridgeToolCallAsync(pending.Context, messageId, evt, runCt);
                            break;
                        case "tool_end":
                            await BroadcastBridgeToolArgsAsync(pending.Context, messageId, evt, runCt);
                            break;
                        case "tool_result":
                            await BroadcastBridgeToolResultAsync(pending.Context, messageId, evt, runCt);
                            break;
                        case "todo":
                            await BroadcastBridgeTodoAsync(pending.Context, messageId, evt, runCt);
                            break;
                        case "attachment" when evt.Attachments is { Count: > 0 }:
                            bridgeAttachments.AddRange(evt.Attachments);
                            break;
                        case "interrupt":
                            interrupt = evt;
                            break;
                        case "end":
                            finished = true;
                            break;
                        case "error":
                            await _hub.Value.BroadcastAsync(pending.GroupId, new RunErrorEvent
                            {
                                GroupId = pending.GroupId,
                                ErrorCode = evt.ErrorCode ?? "AGENT_BRIDGE_ERROR",
                                Message = evt.ErrorMessage ?? "外部 AG-UI 服务返回错误",
                                Timestamp = _hub.Value.NowMs,
                            }, ct: runCt);
                            finished = true;
                            break;
                    }
                    if (interrupt is not null || finished) break;
                }

                // 流在收到 end/error 前断开（连接中断）：按错误处理，提示回复可能不完整（避免静默截断回复）
                if (!finished && interrupt is null)
                {
                    await _hub.Value.BroadcastAsync(pending.GroupId, new RunErrorEvent
                    {
                        GroupId = pending.GroupId,
                        ErrorCode = "AGENT_BRIDGE_DISCONNECTED",
                        Message = "外部 AG-UI 服务连接中断，回复可能不完整",
                        Timestamp = _hub.Value.NowMs,
                    }, ct: runCt);
                }

                if (interrupt is not null)
                {
                    // 工具链再次需要审批：清空已回灌的中间内容，保存新的交互请求（同触发者，同一条消息），保留桥接连接供恢复
                    await _hub.Value.ResetAgentContentAsync(pending.GroupId, messageId, runCt);
                    interactionPending = true;
                    var interruptId = "interrupt_" + IdGenerator.NewId();
                    _pendingInteractions[interruptId] = new PendingInteraction(
                        interruptId, pending.GroupId, pending.AgentId, runId, messageId,
                        pending.TargetMemberId, pending.TopicId, _hub.Value.NowMs, pending.Context,
                        ExternalInterruptId: interrupt.InterruptId,
                        ExternalToolCallId: interrupt.ToolCallId,
                        ExternalToolName: interrupt.ToolName,
                        ExternalToolArguments: interrupt.ToolArguments,
                        Agent: null, Session: null, ApprovalRequest: null,
                        BridgeClient: bridgeClient, ResumeCount: pending.ResumeCount + 1,
                        InputField: interrupt.InputField,
                        ResponseSchema: interrupt.ResponseSchema,
                        Questions: interrupt.Questions);
                    await PurgeExpiredInteractions();
                    await _hub.Value.BroadcastAsync(pending.GroupId, new AgentInteractionRequestEvent
                    {
                        GroupId = pending.GroupId,
                        MessageId = messageId,
                        ThreadId = pending.Context.ThreadId,
                        RunId = runId,
                        InterruptId = interruptId,
                        ToolCallId = interrupt.ToolCallId ?? "tool_" + IdGenerator.NewId(),
                        ToolName = interrupt.ToolName ?? "unknown",
                        ToolArguments = interrupt.ToolArguments,
                        Message = interrupt.InterruptMessage ?? $"智能体请求你确认：是否执行操作「{interrupt.ToolName}」？",
                        Kind = interrupt.InterruptKind ?? "approval",
                        InputField = interrupt.InputField,
                        Options = interrupt.InterruptOptions,
                        ResponseSchema = interrupt.ResponseSchema,
                        Questions = interrupt.Questions,
                        TargetMemberId = pending.TargetMemberId,
                        Timestamp = _hub.Value.NowMs,
                    }, ct: runCt);
                    _logger.LogInformation("AG-UI 桥接恢复流再次中断：run={RunId} interrupt={InterruptId} target={Target}",
                        runId, interruptId, pending.TargetMemberId);
                    return; // 消息保持开启：等下一轮决策恢复后继续追加最终结果
                }

                if (bridgeAttachments.Count > 0)
                {
                    try { await _hub.Value.AppendAgentAttachmentsAsync(pending.GroupId, messageId, AgentGatewayHelpers.ToAttachmentInfos(bridgeAttachments), runCt); }
                    catch (Exception ex) { _logger.LogWarning(ex, "AG-UI 桥接恢复流附件回灌失败：agent={AgentId}", pending.AgentId); }
                }
                await _hub.Value.EndAgentMessageAsync(pending.GroupId, messageId, runCt);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "AG-UI 桥接交互恢复异常：interrupt={InterruptId}", pending.InterruptId);
                await SafeEndAsync(pending.Context, messageId);
            }
            finally
            {
                if (acquired) sessionLock.Release(); // 未获得锁时不 Release（避免 SemaphoreFullException）
            }
        }
        finally
        {
            // 恢复流再次中断时保留桥接连接供下一轮决策；否则恢复流程结束，释放桥接连接
            if (!interactionPending) await bridgeClient.DisposeAsync();
        }
    }

    /// <summary>
    /// 工作型智能体产物回档：从已生成正文中提取 <c>attach_xxx</c>（publish_file 发布的附件 ID），
    /// 反查附件存储并把它们追加到智能体消息（TEXT_MESSAGE_ATTACHMENTS，前端渲染可下载附件卡片）。
    /// <para>
    /// 双来源：正文引用 + <see cref="FunctionResultContent"/>（工具真实返回）。
    /// 为什么必须看工具返回：模型常把技能返回的 JSON <b>改写成人话</b>（如“已生成文档，位置 /tmp/x.docx”），
    /// 结果里的 <c>produce_file</c> 标记就这样丢了 —— 曾导致技能确实生成了文件，用户却看不到下载入口。
    /// 工具返回是权威且未经模型改写的，作为主来源；正文扫描保留以兼容 publish_file 的引用式产物。
    /// </para>
    /// </summary>
    private async Task<int> AttachPublishedProductsAsync(string groupId, string messageId, string content, CancellationToken ct)
    {
        if (_attachmentStore is null) return 0;
        try
        {
            var added = 0;
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(content, @"att_[a-z0-9]+", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                var att = _attachmentStore.GetAttachmentInfo(m.Value);
                if (att is null) continue;
                // 逐个追加：AppendAgentAttachmentsAsync 内部按 URL 去重，重复引用不重复挂
                try
                {
                    await _hub.Value.AppendAgentAttachmentsAsync(groupId, messageId, [att], ct);
                    added++;
                }
                catch (Exception ex) { _logger.LogDebug(ex, "publish_file 产物回档失败：{Att}", m.Value); }
            }
            // 技能产物（如内置 docx 技能）：结果里带 produce_file 标记的文件入库为附件并挂到本条消息。
            // 正文 + 本轮全部工具返回合并扫描（去重由下游按路径保证）。
            var toolResults = ToolResultCollector.Ambient.Value?.Text ?? "";
            added += await AttachSkillProducedFilesAsync(groupId, messageId, content + "\n" + toolResults, ct);
            if (added > 0)
                _logger.LogInformation("智能体产物回档：{Count} 个附件挂到消息 {MessageId}", added, messageId);
            return added;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "产物回档扫描失败（已忽略）");
        }
        return 0;
    }

    /// <summary>
    /// 把技能声明产出（结果文本里的 <c>produce_file</c> 标记）的文件登记为附件并挂到消息。
    /// 解析与入库都在 <see cref="ProducedFileMarker"/>（与技能库试运行共用同一实现）。
    /// </summary>
    private async Task<int> AttachSkillProducedFilesAsync(string groupId, string messageId, string content, CancellationToken ct)
    {
        if (_attachmentStore is null) return 0;
        var added = 0;
        foreach (var info in ProducedFileMarker.SaveAll(content, _attachmentStore, _logger))
        {
            try
            {
                await _hub.Value.AppendAgentAttachmentsAsync(groupId, messageId, [info], ct);
                added++;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "产物挂到消息失败（已忽略）");
            }
        }
        return added;
    }


    /// <summary>技能调用链可视化：运行结束时把 <see cref="SkillChainBuilder.Ambient"/> 中的多跳技能树
    /// 写入当前消息（JSON），供前端渲染链路。无技能调用（null）静默跳过，不阻断主流程。</summary>
    private async Task AttachAgentChainAsync(AgentInvocationContext context, string messageId, CancellationToken ct)
    {
        try
        {
            var chainJson = SkillChainBuilder.Ambient.Value?.ToJson();
            if (string.IsNullOrWhiteSpace(chainJson)) return;
            await _hub.Value.AttachAgentChainAsync(context.GroupId, messageId, chainJson, ct);
            _logger.LogDebug("技能调用链回档：消息 {MessageId}（{AgentId}）", messageId, context.AgentId);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "技能调用链回档失败（已忽略）");
        }
    }

    /// <summary>把任务指派 / 问题提升路径写入链构造器（根=宿主；指派/提升跳按嵌套结构记录），
    /// 供链路可视化与技能调用同屏展示。无构造器（非网关驱动）静默跳过。</summary>
    private void RecordStandinChain(AgentInvocationContext context, List<ChainNode> hops)
    {
        try
        {
            var builder = SkillChainBuilder.Ambient.Value;
            if (builder is null || hops.Count == 0) return;
            builder.EnsureRoot(context.AgentId, _catalog.GetDefinition(context.AgentId)?.Nickname ?? context.AgentId);
            foreach (var hop in hops)
            {
                if (string.IsNullOrWhiteSpace(hop.Kind)) hop.Kind = "assignment";
                builder.Push(hop); // 依序嵌套：root → B → C → …
            }
            // 回到根作用域（Pop 不越过根），不影响后续技能调用作用域
            while (builder.Root is not null) builder.Pop();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "委派链写入失败（已忽略）");
        }
    }

    /// <summary>思考过程写入独立思考通道（TEXT_MESSAGE_REASONING），前端以折叠的「思考过程」块展示，
    /// 与正文分离。本地推理模型（deepseek-reasoner 等）与外部 AG-UI 桥接共用；单段超长截断防刷屏。</summary>
    private async Task AppendReasoningAsync(string groupId, string messageId, string delta, CancellationToken ct)
    {
        const int MaxReasoningChars = 4000;
        var text = delta.Trim();
        if (text.Length == 0) return;
        if (text.Length > MaxReasoningChars) text = text[..MaxReasoningChars] + "…";
        await _hub.Value.AppendAgentReasoningAsync(groupId, messageId, text, ct);
    }

    /// <summary>外部 AG-UI 工具调用开始（TOOL_CALL_START）：广播 TOOL_CALL_START 群事件
    /// （前端渲染「🔧 调用工具：xxx」），可见性继承触发消息（定向 / 私聊回复的工具行不外泄）。</summary>
    private async Task BroadcastBridgeToolCallAsync(AgentInvocationContext context, string messageId, AguiBridgeEvent evt, CancellationToken ct)
    {
        await _hub.Value.BroadcastAsync(context.GroupId, new ToolCallStartEvent
        {
            ToolCallId = evt.ToolCallId ?? "tool_" + IdGenerator.NewId(),
            ToolCallName = evt.ToolName ?? "tool",
            ParentMessageId = messageId,
            GroupId = context.GroupId,
            TriggerUserId = context.TriggerUserId,
            Visibility = context.Visibility,
            VisibleMemberIds = context.VisibleMemberIds ?? [],
            Timestamp = _hub.Value.NowMs,
        }, ct: ct);
    }

    /// <summary>外部 AG-UI 工具参数（TOOL_CALL_END + 分帧累积回填）：广播 TOOL_CALL_ARGS 群事件，前端展示参数详情；空参数不广播。</summary>
    private async Task BroadcastBridgeToolArgsAsync(AgentInvocationContext context, string messageId, AguiBridgeEvent evt, CancellationToken ct)
    {
        if (evt.ToolArguments is not { ValueKind: JsonValueKind.Object } args
            || !args.EnumerateObject().Any()) return; // 无参数 / 空对象 {} 不显示
        await _hub.Value.BroadcastAsync(context.GroupId, new ToolCallArgsEvent
        {
            ToolCallId = evt.ToolCallId ?? "tool_" + IdGenerator.NewId(),
            ParentMessageId = messageId,
            GroupId = context.GroupId,
            Args = args.ToString(),
            Timestamp = _hub.Value.NowMs,
        }, ct: ct);
    }

    /// <summary>外部 AG-UI 工具执行结果（TOOL_CALL_RESULT）：广播 TOOL_CALL_RESULT 群事件，前端与调用行关联展示。</summary>
    private async Task BroadcastBridgeToolResultAsync(AgentInvocationContext context, string messageId, AguiBridgeEvent evt, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(evt.Delta)) return;
        var text = evt.Delta!.Length > AgentGatewayHelpers.MaxToolResultChars ? evt.Delta[..AgentGatewayHelpers.MaxToolResultChars] + "…" : evt.Delta;
        await _hub.Value.BroadcastAsync(context.GroupId, new ToolCallResultEvent
        {
            ToolCallId = evt.ToolCallId ?? "tool_" + IdGenerator.NewId(),
            ParentMessageId = messageId,
            GroupId = context.GroupId,
            Result = text,
            Timestamp = _hub.Value.NowMs,
        }, ct: ct);
    }

    /// <summary>外部 AG-UI 任务进度快照（ACTIVITY_SNAPSHOT todo 流）：广播 ACTIVITY_SNAPSHOT 群事件，前端实时更新进度块。</summary>
    private async Task BroadcastBridgeTodoAsync(AgentInvocationContext context, string messageId, AguiBridgeEvent evt, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(evt.Delta)) return;
        try
        {
            using var doc = JsonDocument.Parse(evt.Delta);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return;
            await _hub.Value.BroadcastAsync(context.GroupId, new ActivitySnapshotEvent
            {
                ParentMessageId = messageId,
                GroupId = context.GroupId,
                Todos = doc.RootElement.Clone(),
                Timestamp = _hub.Value.NowMs,
            }, ct: ct);
        }
        catch (JsonException) { /* 非 JSON 数组 → 忽略 */ }
    }

    /// <summary>
    /// 组装注入智能体上下文的消息文本（本地 run）：
    /// 1. 本话题最近消息滑动窗口（不含当前触发消息、过滤撤回、单条截断）——会话历史按话题隔离，
    ///    不同话题各有独立上下文；记忆体检索（RAG）才是全量/跨话题的（见 MemoryContextProvider）；
    /// 2. 当前消息文本；
    /// 3. 可提取文本的附件（text 类与 docx/xlsx/pptx/pdf 办公文档）由存储层读取文本并内联（截断后附上文件名）；
    ///    image / binary 类携带元数据（类别 / 大小 / 下载地址）供模型感知。
    /// 记忆检索注入（群记忆 RAG + 个人记忆）已按 MSAGENT 标准迁移至
    /// <see cref="MemoryContextProvider"/>（AIContextProvider，经 Instructions 注入），此处不再拼接。
    /// </summary>
    /// <summary>
    /// 智能体上下文可见性过滤：普通知聚只注入全群可见（All）消息；客服知聚存在顾客隔离会话（消息为 Private 定向），
    /// 需把<b>本次触发顾客自己的会话</b>纳入上下文（否则客服“忘了”该顾客之前的对话）。
    /// 严格限定在触发者本人：只含发送者是触发者、或定向可见含触发者的消息，绝不混入其它顾客的私聊。
    /// </summary>
    public static bool IsVisibleForAgentContext(GroupMessage m, string triggerId, bool supportCircle)
    {
        if (!supportCircle) return m.Visibility == MessageVisibility.All;
        return m.Visibility == MessageVisibility.All
            || m.SenderId == triggerId                       // 顾客自己的消息
            || (m.VisibleMemberIds?.Contains(triggerId) ?? false); // 定向回给该顾客的客服消息
    }

    private async Task<string> BuildUserMessageAsync(AgentInvocationContext context, CancellationToken ct)
    {
        var sb = new StringBuilder();

        // 智能体上下文窗口：普通知聚只注入全群可见消息（All）；客服知聚补入本次触发顾客的隔离会话消息
        // （含顾客自己的提问与客服定向回复，见 IsVisibleForAgentContext）。按话题过滤（会话历史以话题为单位）。
        var supportCircle = _hub.Value.Store.GetGroup(context.GroupId)?.IsSupportCircle == true;

        // 固定段（不受总闸门裁剪；都很短且属“上下文锚点”）：话题滚动小结 + 用户反馈画像。
        var head = new StringBuilder();
        // 长话题滚动小结：把“较早对话”的自动摘要先注入（客服知聚跳过——顾客会话彼此隔离），
        // 让滑动窗口之外的早期结论仍能进入模型视野。
        var topicSummary = await MaybeGetTopicSummaryAsync(context, supportCircle, ct);
        if (topicSummary is not null)
        {
            head.Append("【本话题历史小结（自动生成，供回顾更早对话，不必向用户复述）：】\n")
                .Append(topicSummary).AppendLine().AppendLine();
        }

        // 用户偏好画像：该用户近期对“本群/本数字员工”回复点过 👎 时，注入改进提示（客服知聚同样适用）。
        var feedbackHint = await MaybeBuildFeedbackHintAsync(context, ct);
        if (feedbackHint is not null)
        {
            head.Append("【该用户近期对回复的反馈（用于改进，请勿复述给用户）：】\n")
                .Append(feedbackHint).AppendLine().AppendLine();
        }

        var history = _hub.Value.Store.RecentMessages(context.GroupId, ContextWindowMessages, context.TopicId)
            .Where(m => !m.Recalled && m.MessageId != context.TriggerMessageId && !string.IsNullOrWhiteSpace(m.Content)
                && IsVisibleForAgentContext(m, context.TriggerUserId, supportCircle))
            .ToList();

        // 三段“可裁剪段”各自先按自己的单项预算成型，再由总闸门（PromptBudget:MaxTotalChars）按
        // TruncationOrder 统一裁剪。这么拆的目的：单项上限可以保持宽松（不因怕爆而把日常场景压得过紧），
        // 真正的规模上界由总闸门兜住。
        var historySection = BuildHistorySection(history, out var historyDropped, out var historyTruncatedMessages);
        var (historyAttSection, historyAttIncomplete) = await BuildHistoryAttachmentsSectionAsync(history, ct);
        var (attSection, attIncomplete) = await BuildAttachmentsSectionAsync(context, ct);

        // 当前消息（含语言提示）永不截断，先为它预留额度。
        var contentBlock = new StringBuilder();
        AppendLanguageHint(contentBlock, context.Content);
        contentBlock.Append(context.Content);

        var sections = new List<(string Name, StringBuilder Text)>
        {
            ("history", historySection),
            ("history_attachments", historyAttSection),
            ("attachments", attSection),
        };
        var totalChars = head.Length + contentBlock.Length + sections.Sum(s => s.Text.Length);
        var cut = TrimSectionsToBudget(sections, _prompt.TruncationOrder, totalChars - _prompt.MaxTotalChars);
        if (cut.Count > 0) totalChars = head.Length + contentBlock.Length + sections.Sum(s => s.Text.Length);

        sb.Append(head);
        if (historySection.Length > 0) sb.Append(historySection).AppendLine();
        if (historyAttSection.Length > 0) sb.Append(historyAttSection);
        sb.Append(contentBlock);
        if (attSection.Length > 0) sb.Append(attSection);

        ReportPromptAssembly(context, totalChars, historyDropped, historyTruncatedMessages, historyAttIncomplete, attIncomplete, cut);
        return sb.ToString();
    }

    /// <summary>
    /// 总闸门的实际裁剪：把各段按 <paramref name="order"/>（越靠前越先牺牲）裁到不超预算，
    /// 且一律从<b>尾部</b>裁（历史尾部 = 更早的消息）。就地修改 <paramref name="sections"/> 里的 StringBuilder。
    /// 抽成纯函数（与网关状态无关）是为了能被单测直接钉住降级顺序与裁剪量。
    /// </summary>
    /// <returns>被实际裁剪的段及字符数（未裁的不出现）。</returns>
    internal static List<string> TrimSectionsToBudget(
        IReadOnlyList<(string Name, StringBuilder Text)> sections, string[] order, int overflow)
    {
        var cut = new List<string>();
        if (overflow <= 0) return cut;
        foreach (var stage in order)
        {
            if (overflow <= 0) break;
            foreach (var section in sections)
            {
                if (!string.Equals(section.Name, stage, StringComparison.Ordinal) || section.Text.Length == 0) continue;
                var n = Math.Min(section.Text.Length, overflow);
                section.Text.Remove(section.Text.Length - n, n);
                overflow -= n;
                cut.Add($"{stage}:-{n}");
            }
        }
        return cut;
    }

    /// <summary>
    /// 把群历史排版为可注入段落：单条按 <c>PromptBudget:MaxCharsPerHistoryMessage</c> 截断，
    /// 整段按 <c>MaxHistoryChars</c> <b>从最新往回填</b>（填满即停，短消息不浪费额度），最后恢复时间顺序输出。
    /// 群历史是用户输入，可能含恶意指令（prompt injection）：整段包上不可信边界。
    /// </summary>
    private StringBuilder BuildHistorySection(IReadOnlyList<GroupMessage> history, out int droppedOldest, out int truncatedMessages)
    {
        droppedOldest = 0;
        truncatedMessages = 0;
        if (history.Count == 0) return new StringBuilder();

        const string header = "以下是群最近对话：";
        var used = header.Length;
        var lines = new List<string>(history.Count);
        for (var i = history.Count - 1; i >= 0; i--)
        {
            var m = history[i];
            var raw = m.Content;
            if (raw.Length > MaxContextCharsPerMessage)
            {
                raw = raw[..MaxContextCharsPerMessage];
                truncatedMessages++;
            }
            var who = string.IsNullOrWhiteSpace(m.SenderNickname) ? m.SenderId : m.SenderNickname;
            var line = $"{who}：{raw}";
            if (used + line.Length > _prompt.MaxHistoryChars)
            {
                droppedOldest = i + 1; // 0..i 放不下 → 这些更早的消息全部被丢弃
                break;
            }
            lines.Add(line);
            used += line.Length;
        }
        lines.Reverse(); // 恢复时间顺序
        var block = new StringBuilder();
        block.AppendLine(header);
        foreach (var line in lines) block.AppendLine(line);
        return new StringBuilder(UntrustedBoundary.Wrap(block.ToString()));
    }

    /// <summary>
    /// 把历史消息里“可提取文本”的附件（Word/Excel/PDF/txt…）跨轮重新内联，让后续追问仍能参考其内容
    /// （上下文按触发重建，无跨轮会话，因此需要重载回上下文）。预算：<c>PromptBudget:MaxHistoryInlineTextChars</c>。
    /// 全部包上不可信边界。返回段落与“未被完整注入”的附件数（供截断留痕）。
    /// </summary>
    private async Task<(StringBuilder Text, int Incomplete)> BuildHistoryAttachmentsSectionAsync(
        IReadOnlyList<GroupMessage> history, CancellationToken ct)
    {
        var sb = new StringBuilder();
        if (_attachmentStore is null || history.Count == 0) return (sb, 0);

        var injected = 0;
        var incomplete = 0;
        foreach (var m in history)
        {
            if (m.Attachments is not { Count: > 0 }) continue;
            foreach (var att in m.Attachments)
            {
                if (!AttachmentStore.IsExtractable(att)) continue;
                if (injected >= MaxHistoryInlineTextChars) { incomplete++; continue; }
                var extracted = await _attachmentStore.TryReadTextAsync(att.AttachmentId, ct);
                if (string.IsNullOrEmpty(extracted)) continue;
                var take = Math.Min(extracted.Length, MaxHistoryInlineTextChars - injected);
                if (take <= 0) { incomplete++; continue; }
                if (take < extracted.Length) incomplete++; // 只注入了截断版：同样如实计入
                var who = string.IsNullOrWhiteSpace(m.SenderNickname) ? m.SenderId : m.SenderNickname;
                sb.Append($"\n\n[{who} 上传的文档 {att.Name} 内容摘录]\n")
                  .Append(UntrustedBoundary.Wrap(extracted[..take]));
                injected += take;
            }
        }
        return (sb, incomplete);
    }

    /// <summary>
    /// 提示词装配留痕。此前各层截断<b>全部静默</b>，于是“看到文字被截断”却查不出是哪一层截的、截掉多少——
    /// 这正是本机制要解决的可观测性问题：只要有截断就记一条 WARN；接近闸门时也留一条 Information 便于观察趋势。
    /// 注意：字符数是估算，真实规模以模型返回的 prompt tokens 为准。
    /// </summary>
    private void ReportPromptAssembly(AgentInvocationContext context, int totalChars,
        int historyDropped, int historyTruncatedMessages, int historyAttIncomplete, int attIncomplete, List<string> trimmed)
    {
        var truncated = trimmed.Count > 0 || historyDropped > 0 || historyTruncatedMessages > 0
            || historyAttIncomplete > 0 || attIncomplete > 0;
        if (!truncated)
        {
            if (totalChars > _prompt.MaxTotalChars / 2)
                _logger.LogInformation("提示词装配：agent={AgentId} group={GroupId} 字符={Chars}/{Budget}（未截断）",
                    context.AgentId, context.GroupId, totalChars, _prompt.MaxTotalChars);
            return;
        }
        _logger.LogWarning("提示词装配发生截断：agent={AgentId} group={GroupId} 字符={Chars}/{Budget}；"
            + "历史丢弃最早 {HistoryDropped} 条、【历史单条截断 {HistoryTruncated} 条】、历史附件未完整注入 {HistoryAtt} 个、"
            + "当前附件未完整注入 {Att} 个、总闸门裁剪 [{Trimmed}]（当前消息与系统提示永不截断）",
            context.AgentId, context.GroupId, totalChars, _prompt.MaxTotalChars,
            historyDropped, historyTruncatedMessages, historyAttIncomplete, attIncomplete, string.Join(",", trimmed));
    }

    /// <summary>多语言自适应：按触发消息的主导语言给模型补一句“用该语言回复”的提示。
    /// 检测不到明确语种（纯数字 / 过短英文等）时不注入，避免误判。</summary>
    internal static string? DetectReplyLanguageHint(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;
        int cjk = 0, kana = 0, hangul = 0, latin = 0;
        foreach (var ch in content)
        {
            if (ch >= '\u4E00' && ch <= '\u9FFF') cjk++;
            else if (ch >= '\u3040' && ch <= '\u30FF') kana++;
            else if (ch >= '\uAC00' && ch <= '\uD7A3') hangul++;
            else if ((ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z')) latin++;
        }
        var letters = cjk + kana + hangul + latin;
        if (letters <= 0) return null;
        if (cjk > 0 && latin <= cjk * 3) return "（请用中文回复，提问者消息以中文为主。）";
        if (kana >= 3 && kana * 10 >= letters * 3) return "（質問は日本語です。日本語で回答してください。）";
        if (hangul >= 3 && hangul * 10 >= letters * 3) return "（질문이 한국어입니다. 한국어로 답변해 주세요.）";
        if (latin >= 6 && latin * 10 >= letters * 6) return "(Answer in English - the requester wrote in English.)";
        return null;
    }

    /// <summary>把语言提示拼到同一轮用户消息的正文前（先于 <paramref name="content"/>）。</summary>
    private static void AppendLanguageHint(StringBuilder sb, string? content)
    {
        var hint = DetectReplyLanguageHint(content);
        if (hint is null) return;
        sb.Append(hint).AppendLine().AppendLine();
    }

    /// <summary>话题滚动小结：返回可注入的小结文本（有则注入；无则 null）。
    /// 触发策略：距上次小结（watermark）新增消息达到阈值才生成/更新一次，否则仅回读既有小结。
    /// 同话题并发只允许一个生成任务（TryBeginGenerate 防抖），失败不阻塞回复。</summary>
    private async Task<string?> MaybeGetTopicSummaryAsync(AgentInvocationContext context, bool supportCircle, CancellationToken ct)
    {
        if (supportCircle || !_options.TopicSummaryEnabled) return null;
        var store = _topicSummary.Value;
        if (store is null) return null;
        var existing = store.Get(context.GroupId, context.TopicId);

        IReadOnlyList<GroupMessage> since;
        if (existing is { WatermarkMessageId: not null }
            && _hub.Value.Store.GetMessage(context.GroupId, existing.WatermarkMessageId) is not null)
        {
            since = _hub.Value.Store.MessagesAfter(context.GroupId, existing.WatermarkMessageId, TopicSummaryScanLimit, context.TopicId)
                .Where(m => !m.Recalled && m.MessageId != context.TriggerMessageId && !string.IsNullOrWhiteSpace(m.Content)
                    && m.Visibility == MessageVisibility.All)
                .ToList();
        }
        else
        {
            // 首次（或游标消息已被清空/删除）：取话题尾部最近一批作为本轮小结范围
            since = _hub.Value.Store.RecentMessages(context.GroupId, TopicSummaryScanLimit, context.TopicId)
                .Where(m => !m.Recalled && m.MessageId != context.TriggerMessageId && !string.IsNullOrWhiteSpace(m.Content)
                    && m.Visibility == MessageVisibility.All)
                .ToList();
        }
        since = since.OrderBy(m => m.Timestamp).ThenBy(m => m.MessageId).ToList();

        var threshold = Math.Max(6, _options.TopicSummaryTriggerCount);
        if (since.Count < threshold) return existing?.Summary;
        if (!store.TryBeginGenerate(context.GroupId, context.TopicId)) return existing?.Summary; // 已有同话题生成在跑

        try
        {
            var messages = since.Select(m => (Who: m.SenderNickname ?? m.SenderId, Text: m.Content)).ToList();
            var summary = await TopicSummaryGenerator.GenerateAsync(_options, existing?.Summary, messages, _logger, ct);
            var newest = since[^1];
            store.Put(new TopicSummaryRecord
            {
                GroupId = context.GroupId,
                TopicId = context.TopicId,
                Summary = summary,
                WatermarkMessageId = newest.MessageId,
                UpdatedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                MessageCount = (existing?.MessageCount ?? 0) + since.Count,
            });
            return summary;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "话题滚动小结生成失败（忽略，不影响回复）：group={GroupId} topic={TopicId}",
                context.GroupId, context.TopicId);
            return existing?.Summary;
        }
        finally
        {
            store.EndGenerate(context.GroupId, context.TopicId);
        }
    }

    /// <summary>用户偏好画像：汇总该用户近期负面反馈（同群或同数字员工）为一段改进提示。
    /// 纯函数便于单测：entries 为该用户全部反馈；只取 30 天内、命中本群或本数字员工的 👎。</summary>
    internal static string? BuildFeedbackHint(IReadOnlyList<MessageFeedbackEntry> entries, string agentId, string groupId, long nowMs)
    {
        const long WindowMs = 30L * 24 * 3600 * 1000;
        var relevant = (entries ?? [])
            .Where(e => e.Value < 0
                && (e.AgentId == agentId || e.GroupId == groupId)
                && nowMs - e.CreatedAtMs <= WindowMs)
            .OrderByDescending(e => e.CreatedAtMs)
            .Take(6)
            .ToList();
        if (relevant.Count == 0) return null;

        var tagCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var tag in relevant.SelectMany(e => e.Tags ?? []))
            if (!string.IsNullOrWhiteSpace(tag)) tagCounts[tag] = tagCounts.GetValueOrDefault(tag) + 1;
        var lines = new List<string>();
        var topTags = string.Join("、", tagCounts.OrderByDescending(kv => kv.Value).Take(4).Select(kv => kv.Key));
        if (topTags.Length > 0)
            lines.Add("该用户近期点赞为 👎 的回复主要反映：" + topTags + "。请尽量避免这些问题。");
        else
            lines.Add($"该用户近期对 {relevant.Count} 条回复点了 👎，请在回答时更贴合其需求。");
        foreach (var e in relevant.Take(2))
        {
            if (string.IsNullOrWhiteSpace(e.Snippet)) continue;
            var snippet = e.Snippet.Length > 80 ? e.Snippet[..80] + "…" : e.Snippet;
            lines.Add("· 曾被点 👎 的回复片段：" + snippet);
        }
        return string.Join("\n", lines);
    }

    private async Task<string?> MaybeBuildFeedbackHintAsync(AgentInvocationContext context, CancellationToken ct)
    {
        var store = _feedback.Value;
        if (store is null || context.TriggerUserId is null) return null;
        try
        {
            var entries = store.ByUser(context.TriggerUserId);
            return BuildFeedbackHint(entries, context.AgentId, context.GroupId, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "读取用户反馈失败（忽略）");
            return null;
        }
    }

    /// <summary>外部 AG-UI 桥接用户消息组装：会话首次建立（无增量游标）发送话题全部历史；
    /// 会话已建立后只发送上次节点（游标）之后的本话题增量 + 当前消息，避免每次全量重发。</summary>
    private async Task<string> BuildBridgeUserMessageAsync(AgentInvocationContext context, string externalThreadId, CancellationToken ct)
    {
        var cursorKey = $"{context.AgentId}|{externalThreadId}";
        var lastId = _bridgeCursors.TryGetValue(cursorKey, out var id) ? id : null;
        var history = lastId is null
            ? _hub.Value.Store.MessagesBefore(context.GroupId, null, BridgeFullHistoryMax, context.TopicId) // 首次：话题全量历史
            : _hub.Value.Store.MessagesAfter(context.GroupId, lastId, BridgeIncrementMax, context.TopicId);   // 已建立：增量
        // 游标失效防护：话题被「清空 / 删除」后游标指向已删消息，增量永远为空（外部会话上下文丢失）——
        // 增量结果为空且游标消息已不存在时，回退全量并重置游标（下次恢复增量模式）
        if (lastId is not null && history.Count == 0
            && _hub.Value.Store.GetMessage(context.GroupId, lastId) is null)
        {
            _logger.LogInformation("外部会话增量游标失效（消息已删除），回退话题全量：agent={AgentId} thread={ThreadId}",
                context.AgentId, externalThreadId);
            _bridgeCursors.TryRemove(cursorKey, out _);
            _changes?.Notify();
            history = _hub.Value.Store.MessagesBefore(context.GroupId, null, BridgeFullHistoryMax, context.TopicId);
        }
        var supportCircle = _hub.Value.Store.GetGroup(context.GroupId)?.IsSupportCircle == true;
        var visible = history.Where(m => !m.Recalled && m.MessageId != context.TriggerMessageId
            && !string.IsNullOrWhiteSpace(m.Content) && IsVisibleForAgentContext(m, context.TriggerUserId, supportCircle)).ToList();
        var sb = new StringBuilder();
        if (visible.Count > 0)
        {
            // 话题历史消息是用户输入，可能含恶意指令（prompt injection）：整段包上不可信边界
            var block = new StringBuilder();
            block.AppendLine(lastId is null ? "以下是话题对话历史：" : "以下是话题新增对话：");
            foreach (var m in visible)
            {
                var who = string.IsNullOrWhiteSpace(m.SenderNickname) ? m.SenderId : m.SenderNickname;
                var text = m.Content.Length > MaxContextCharsPerMessage ? m.Content[..MaxContextCharsPerMessage] : m.Content;
                block.AppendLine($"{who}：{text}");
            }
            sb.Append(UntrustedBoundary.Wrap(block.ToString())).AppendLine();
        }
        // 注意：桥接对外发送的是 AG-UI 协议原文（标准模式断言内容与触发消息完全一致），不做本地语言提示注入
        sb.Append(context.Content);
        await AppendAttachmentsAsync(sb, context, ct);
        return sb.ToString();
    }

    /// <summary>
    /// 把消息附件组织成模型可<b>逐个引用</b>的附件区：先给出全部附件清单（编号 + 名称 + attachmentId + 注入状态），
    /// 再把可提取文本按顺序内联。预算策略是“每文件各自的单文件上限 + 全局总预算”（参考主流聊天工具：多个文件都可被引用，
    /// 未自动注入全文的附件可随时经 read_attachment 按 ID 读取/分段续读），避免首个大文件挤掉后续附件。
    /// </summary>
    private async Task<(StringBuilder Text, int Incomplete)> BuildAttachmentsSectionAsync(AgentInvocationContext context, CancellationToken ct)
    {
        var sb = new StringBuilder();
        if (context.Attachments is not { Count: > 0 } attachments || _attachmentStore is null) return (sb, 0);

        // 1) 预读每个可提取附件的首段文本（单文件上限）与全文长度；不可提取/提取失败以 null 标记。
        var entries = new List<(AttachmentInfo Att, string? Text, int Total)>(attachments.Count);
        foreach (var att in attachments)
        {
            if (!AttachmentStore.IsExtractable(att))
            {
                entries.Add((att, null, 0));
                continue;
            }
            var seg = await _attachmentStore.TryReadTextRangeAsync(att.AttachmentId, 0, _attachmentStore.TextCharsPerFile, ct);
            entries.Add(seg is { } s ? (att, s.Segment, s.TotalLength) : (att, null, 0));
        }

        // 2) 全局预算内顺序分配（每文件已由单文件上限截断，只有总预算耗尽才会截得更短）。
        var remaining = _prompt.AttachmentMaxTextCharsTotal;
        var takes = new int[entries.Count];
        for (var i = 0; i < entries.Count; i++)
        {
            var len = entries[i].Text?.Length ?? 0;
            if (len == 0) { takes[i] = 0; continue; }
            var take = Math.Min(len, remaining);
            takes[i] = take;
            remaining -= take;
        }

        // 3) 附件清单：让模型清楚“本条共 N 个附件、各自 attachmentId、哪些已注入正文、哪些需读取”。
        sb.Append($"\n\n【本条消息共 {attachments.Count} 个附件（可对任意一个调用 read_attachment 读取正文）：】\n");
        var imageSeen = 0;
        for (var i = 0; i < entries.Count; i++)
        {
            var (att, text, total) = entries[i];
            var take = takes[i];
            string note;
            if (IsImage(att))
                note = ++imageSeen <= MaxContextImages + MaxHistoryImages
                    ? "图片（已随本轮视觉上下文提供）"
                    : "图片（本轮视觉数量已达上限，仅元数据）";
            else if (text is null || text.Length == 0)
                note = "无文本可提取（仅元数据）";
            else if (take >= total)
                note = "已注入全文";
            else if (take >= text.Length)
                note = $"已注入前 {take} 字符（全文 {total} 字符，如需其余内容请用 read_attachment 续读）";
            else
                note = $"已注入前 {take}/{total} 字符（如需完整内容请用 read_attachment 读取）";
            sb.Append("· 附件").Append(i + 1).Append(". ").Append(att.Name)
              .Append("（").Append(att.Kind).Append("，").Append(AgentGatewayHelpers.FormatBytes(att.Size))
              .Append("，attachmentId=").Append(att.AttachmentId).Append("）：").Append(note).AppendLine();
        }

        // 4) 正文段（清单在先，正文在后；正文可能是用户上传内容，包上不可信边界）。
        for (var i = 0; i < entries.Count; i++)
        {
            var take = takes[i];
            if (take <= 0 || entries[i].Text is not { Length: > 0 } text) continue;
            sb.Append($"\n【附件 {i + 1}. {entries[i].Att.Name} 正文】\n").Append(UntrustedBoundary.Wrap(text[..take]));
        }

        // “未完整注入”的附件（被单文件或总预算截短的、以及完全没注入的）如实上报，供“文字被截断”排查。
        var incomplete = 0;
        for (var i = 0; i < entries.Count; i++)
            if (entries[i].Total > takes[i]) incomplete++;
        return (sb, incomplete);
    }

    /// <summary>兼容入口：把附件段落直接追加到既有 sb（桥接路径沿用，行为不变）。</summary>
    private async Task AppendAttachmentsAsync(StringBuilder sb, AgentInvocationContext context, CancellationToken ct)
    {
        var (text, _) = await BuildAttachmentsSectionAsync(context, ct);
        sb.Append(text);
    }

    /// <summary>
    /// 组装“视觉（多模态）”轮次的用户消息：文本（含当前消息 + 本话题最近滑动窗口的对话文本，BuildUserMessageAsync）作基底；
    /// 然后把 <b>当前消息</b> 附图 与 <b>最近窗口里带图的历史消息</b> 的图片像素一并作为 DataContent 喂给视觉模型。
    /// 这样“先发图、隔一轮再追问”的多轮对话，模型仍能看到先前那张图，而不是只能看到图片的文本元数据。
    /// 返回 (message, hasImage)：hasImage=false 表示无任何可用图片（调用方回退普通文本模型）。
    /// </summary>
    private async Task<(ChatMessage Message, bool HasImage)> BuildVisionUserMessageAsync(AgentInvocationContext context, CancellationToken ct)
    {
        var contents = new List<AIContent>();
        var addedImageIds = new HashSet<string>(StringComparer.Ordinal);
        var addedImages = 0; // 总图片数上限（当前 + 历史）

        // 1) 基底文本：含当前消息正文 / 当前附件文本→内联或元数据、以及话题最近对话文本。
        var text = await BuildUserMessageAsync(context, ct);
        var sb = new StringBuilder(text);

        // 2) 当前消息附图：直接喂像素（BuildUserMessageAsync 已给过【附件：名】元数据行作指位）。
        if (_attachmentStore is not null)
            foreach (var att in context.Attachments ?? [])
            {
                if (!IsImage(att)) continue;
                if (!addedImageIds.Add(att.AttachmentId) || addedImages++ >= (MaxContextImages + MaxHistoryImages)) continue;
                var img = _attachmentStore.TryReadImageBytes(att.AttachmentId);
                if (img is { } cur) contents.Add(new DataContent(cur.Bytes, cur.ContentType));
            }

        // 3) 最近窗口里带图的历史消息：与 BuildUserMessageAsync 同一过滤（提及/隐私/话题），回喂其图片像素，并在文本里补一句指位。
        if (_attachmentStore is not null && addedImages < (MaxContextImages + MaxHistoryImages))
        {
            var supportCircle = _hub.Value.Store.GetGroup(context.GroupId)?.IsSupportCircle == true;
            var recent = _hub.Value.Store.RecentMessages(context.GroupId, ContextWindowMessages, context.TopicId).ToList();
            int historyImages = 0;
            foreach (var m in recent)
            {
                if (m.Recalled || m.MessageId == context.TriggerMessageId
                    || !IsVisibleForAgentContext(m, context.TriggerUserId, supportCircle)) continue;
                // 纯附图消息正文为空：历史文本窗口可能缺该行，这里为图片单补一行指位
                foreach (var att in m.Attachments ?? [])
                {
                    if (!IsImage(att) || !addedImageIds.Add(att.AttachmentId)) continue;
                    if (historyImages >= MaxHistoryImages || addedImages++ >= (MaxContextImages + MaxHistoryImages)) break;
                    var img = _attachmentStore.TryReadImageBytes(att.AttachmentId);
                    if (img is not { } gi) { addedImageIds.Remove(att.AttachmentId); addedImages--; continue; }
                    contents.Add(new DataContent(gi.Bytes, gi.ContentType));
                    historyImages++;
                    var who = string.IsNullOrWhiteSpace(m.SenderNickname) ? m.SenderId : m.SenderNickname;
                    sb.Append($"\n\n（补充上下文：{who} 此前发过图片【{att.Name}】，请结合该图片理解本轮提问）");
                }
                if (historyImages >= MaxHistoryImages) break;
            }
        }

        // 有图则文本作为第 0 段，后接各图片；无图则回落纯文本（调用方按 HasImage 决定用哪个模型）。
        var hasImage = contents.OfType<DataContent>().Any();
        contents.Insert(0, new TextContent(sb.ToString()));
        return (new ChatMessage(ChatRole.User, contents), hasImage);
    }

    private static bool IsImage(AttachmentInfo a)
        => a?.Kind == "image" || (a?.ContentType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ?? false);

    /// <summary>
    /// 语境发言决策（Contextual 模式）：把群最近消息作为上下文交给模型，
    /// 让它判断是否需要由该智能体发言。不 @、无关键词也可按语境主动发言。
    /// </summary>
    private async Task<bool> ShouldSpeakAsync(AgentInvocationContext context, AgentDefinition def, CancellationToken ct)
    {
        // 轻量决策：只要一个布尔与它的概率（不走工具 / 记忆 / 审批包装，也不需要推理模型）。
        // 实测教训：以前走 MAF 裸智能体 + 推理模型 + MaxOutputTokens=8，推理把预算吃光 → 正文为空
        // → StartsWith("YES") 恒为假 → “语境触发永远不发言”（且日志只是一句“保持沉默”，看不出原因）。
        // 语境判断同样按话题取最近对话（会话历史以话题为单位，与 BuildUserMessageAsync 一致）
        var history = _hub.Value.Store.RecentMessages(context.GroupId, _options.ContextMaxMessages, context.TopicId)
            .Where(m => !m.Recalled && m.Visibility == MessageVisibility.All)
            .ToList();

        var sb = new StringBuilder();
        foreach (var m in history)
        {
            var who = string.IsNullOrWhiteSpace(m.SenderNickname) ? m.SenderId : m.SenderNickname;
            var text = m.Content.Length > MaxContextCharsPerMessage ? m.Content[..MaxContextCharsPerMessage] : m.Content;
            sb.AppendLine($"{who}：{text}");
        }

        var prompt =
            $"__AGUI_DECIDE__\n" +
            $"你是「{def.Nickname}」，角色：{def.Description}\n行为准则：{def.Instructions}\n\n" +
            $"这是群「{context.GroupId}」最近的对话：\n{sb}" +
            $"最新消息：{context.Content}\n\n" +
            "请根据语境判断你是否应该发言：被直接提及/询问、或消息与你的职责相关且你有实质内容补充 → YES；" +
            "只是寒暄、与你职责无关、或你刚发言过且没有新的实质信息 → NO。\n只输出 YES 或 NO。";

        AgentCatalog.DecisionOutcome decision;
        try
        {
            decision = await _catalog.DecideYesNoAsync(def.AgentId, prompt, ct);
        }
        catch (Exception ex)
        {
            // 判定调用失败不应阻断整条链路：保守选择“不发言”，但必须留痕（以前这类失败是静默的）。
            _logger.LogWarning(ex, "智能体 {AgentId} 语境判定调用失败，本次按不发言处理", def.AgentId);
            return false;
        }

        // 用概率定阈值（可在 Agents:DecisionMinProbability 调）：拿不到概率时退回文本结论，两者都拿不到则不猜（不发言）。
        var pYes = decision.Probability;
        var speak = pYes is { } p
            ? p >= _options.DecisionMinProbability
            : decision.Answer == true;
        _logger.LogInformation("智能体 {AgentId} 语境判定：模型={Model} P(发言)={PYes} 阈值={Min} → {Verdict}（原始：{Raw}）",
            def.AgentId, decision.Model,
            pYes is { } v ? v.ToString("F3") : "n/a", _options.DecisionMinProbability,
            speak ? "发言" : "保持沉默", AgentGatewayHelpers.TruncateForChain(decision.Raw));
        return speak;
    }

    /// <summary>
    /// 每次触发重建会话：MSAGENT 默认内存历史无上限，群聊记录多时上下文无限增长，
    /// 模型 prefill/生成显著变慢（表现为回复吐字越来越慢）。
    /// 上下文改为由 <see cref="BuildUserMessageAsync"/> 从群存储注入的滑动窗口
    /// （最近 N 条 + 单条截断），会话仅承载单次调用。
    /// </summary>
    private ValueTask<AgentSession> GetOrCreateSessionAsync(AgentInvocationContext context, ChatClientAgent agent, CancellationToken ct)
        => agent.CreateSessionAsync(ct);

    /// <summary>
    /// 获取线程（群）的会话锁；条目超过上限时顺带清理超时未用的锁（定时清理兜底，见 <see cref="PurgePeriodicCleanup"/>）。
    /// 正在等待旧锁实例的调用不受影响（释放后队列继续），新调用创建新锁（短暂窗口内同群并发可接受）。
    /// </summary>
    private SemaphoreSlim GetSessionLock(string threadId)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (_sessionLocks.Count >= _execution.SessionLockMaxEntries)
        {
            // 兜底：条目超限时即时清理超时锁；常态清理由 60s 定时器完成（不依赖 Count）
            foreach (var kv in _sessionLocks)
            {
                if (now - kv.Value.LastUsedMs > (long)_execution.SessionLockTtlMinutes * 60 * 1000)
                    _sessionLocks.TryRemove(kv.Key, out _);
            }
        }
        // 原子创建 / 刷新（AddOrUpdate）：避免「GetOrAdd + 索引赋值」两段式在并发下丢失刷新，
        // 也避免刚创建的锁实例被旧条目覆盖导致双实例
        var entry = _sessionLocks.AddOrUpdate(threadId,
            _ => (new SemaphoreSlim(1, 1), now),
            (_, cur) => (cur.Lock, now)); // 刷新上次使用时间
        return entry.Lock;
    }

    // ================= 人机交互（协议 4.5）=================

    /// <summary>
    /// 触发者决策后恢复被中断的运行：校验决策者必须是交互请求的 TargetMemberId（触发者），
    /// 把「批准 / 拒绝」作为 User 消息回灌同一 AgentSession，工具随之执行（或跳过），流式回复继续回灌群聊。
    /// </summary>
    public async Task<bool> ResolveInteractionAsync(string interruptId, string memberId, bool approved, string? input, JsonElement? payload, CancellationToken ct, bool approveAll = false, string? toolResult = null, string? clientId = null)
    {
        // 入口先做一次周期清理：定时器兜底外，决策前把已超时的交互先清掉，避免继续处理过期请求
        await PurgeExpiredInteractions();

        // 编排计划「客户端技能批量执行」的交互：不通过 PendingInteraction/ResumeRunAsync，而是直接
        // 把执行结果写入 TCS，让正在等待的计划方法恢复执行（再次校验触发者身份）。
        if (_batchClientExecWaits.TryGetValue(interruptId, out var batch))
        {
            if (!string.Equals(batch.TargetMemberId, memberId, StringComparison.Ordinal))
                return false;
            if (!_batchClientExecWaits.TryRemove(interruptId, out batch))
                return false;
            // 决策时携带浏览器本机桥的 client → 以决策机器为准路由（发起该次运行的消息未带 bridgeClient 时在此补全）
            var resolveClient = string.IsNullOrWhiteSpace(clientId) ? null : clientId.Trim();
            if (!string.IsNullOrWhiteSpace(resolveClient)
                && !string.Equals(batch.ClientId, resolveClient, StringComparison.Ordinal))
            {
                _logger.LogInformation("批量客户端技能按决策机器路由：interrupt={InterruptId} client={Client}", interruptId, resolveClient);
                batch = batch with { ClientId = resolveClient };
            }
            GuardSupportCircleClient(batch.GroupId, batch.ClientId, "批量本机技能:" + batch.AgentId);
            Dictionary<string, string>? results = null;
            if (approved)
            {
                results = new Dictionary<string, string>(StringComparer.Ordinal);
                // 前端回传结果先并入（http 等浏览器可直接执行的项）：JSON 数组 [{"skillId":..,"output":..}]
                if (!string.IsNullOrWhiteSpace(toolResult))
                {
                    try
                    {
                        var arr = JsonSerializer.Deserialize<List<JsonElement>>(toolResult, AguiJson.Options) ?? [];
                        foreach (var item in arr)
                        {
                            var sid = item.TryGetProperty("skillId", out var p) ? p.GetString() : null;
                            var outp = item.TryGetProperty("output", out var op) ? op.GetString() : null;
                            if (!string.IsNullOrWhiteSpace(sid)) results[sid] = outp ?? "（本机执行无输出）";
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "批量客户端技能回传解析失败：interrupt={InterruptId}", interruptId);
                    }
                }
                // 内网隧道在线（按发起/决策客户端）且已批准 → 批量 shell 技能经隧道在桥所在主机逐个执行，优先于前端回传结果
                if (TunnelAvailable(batch.AgentId, batch.ClientId))
                {
                    foreach (var it in batch.Items)
                    {
                        if (TryParseRunnerShell(it.ClientRunner, out var bCmd, out var bCwd, out var bTimeoutSec))
                        {
                            var br = await ExecuteTunnelAsync(
                                batch.AgentId, batch.ClientId, bCmd!, bCwd, bTimeoutSec, it.Query,
                                ClientSkillTimeout(bTimeoutSec), ct);
                            results[it.SkillId] = string.IsNullOrWhiteSpace(br) ? "（本机执行未返回结果 / 超时）" : br;
                        }
                        else if (!results.ContainsKey(it.SkillId))
                        {
                            results[it.SkillId] = "（该技能非本机 shell，无法经隧道执行）";
                        }
                    }
                }
                else
                {
                    // 无可用隧道（发起/决策机器的本机桥未连接 / 未上报）：逐项补齐明确失败原因，避免静默留空让模型脑补成功
                    foreach (var it in batch.Items)
                    {
                        if (results.ContainsKey(it.SkillId)) continue;
                        results[it.SkillId] = TryParseRunnerShell(it.ClientRunner, out _, out _, out _)
                            ? "（未能执行：发起请求/决策的电脑未连接本机桥 NativeBridge，无法路由到该机器执行本机技能。请在该电脑启动 AguiGroupChat.NativeBridge 后重新发起。）"
                            : "（该技能非本机 shell，无法经隧道执行）";
                    }
                }
            }
            ClientToolTrace.Write($"BATCH-RESOLVE interrupt={interruptId} member={memberId} approved={approved} tunneled={TunnelAvailable(batch.AgentId, batch.ClientId)} results={results?.Count ?? 0}");
            batch.Completion.TrySetResult((approved && results is not null, results));
            return true;
        }

        // 先校验存在性与触发者身份（仅触发者可决策），确认后才移除——非触发者的调用不会消耗请求
        if (!_pendingInteractions.TryGetValue(interruptId, out var pending))
            return false; // 不存在 / 已过期
        if (!string.Equals(pending.TargetMemberId, memberId, StringComparison.Ordinal))
            return false; // 仅触发者可决策（群聊其他用户无权交互）
        if (!_pendingInteractions.TryRemove(interruptId, out pending))
            return false; // 并发下已被决策

        // 决策时携带浏览器本机桥的 client → 作为“本机(client)技能”的路由目标：发起消息阶段若未携带（如页面早于桥上线）
        // 或桥中途重启换了 client，触发者决策时仍可把执行路由到其浏览器所在电脑的本机桥。
        var decisionClient = string.IsNullOrWhiteSpace(clientId) ? null : clientId.Trim();
        if (!string.IsNullOrWhiteSpace(decisionClient)
            && _nativeTunnel.Value?.HasClient(decisionClient) == true
            && !string.Equals(pending.Context.PreferredBridgeClient, decisionClient, StringComparison.Ordinal))
        {
            pending = pending with { Context = pending.Context with { PreferredBridgeClient = decisionClient } };
            _logger.LogInformation("交互决策按决策机器路由：interrupt={InterruptId} client={Client}", interruptId, decisionClient);
        }
        // 客服知聚双保险：本机技能只允许在“发起请求的顾客”机器执行——client 缺失/离线时记录告警（A 口径不会跑别处）
        if (pending.ApprovalRequest?.ToolCall is FunctionCallContent supFc
            && _catalog.GetAgentClientToolNames(pending.Context.AgentId).Contains(supFc.Name, StringComparer.Ordinal))
            GuardSupportCircleClient(pending.Context.GroupId, pending.Context.PreferredBridgeClient, "审批后:" + supFc.Name);
        // 诊断：客户端技能已批准，但仍无已注册 client 可路由——审批后只会落失败/占位文本，记录便于定位“执行环境没对上用户电脑”的问题
        if (approved
            && pending.ApprovalRequest?.ToolCall is FunctionCallContent routeFc
            && _catalog.GetAgentClientToolNames(pending.Context.AgentId).Contains(routeFc.Name, StringComparer.Ordinal)
            && !TunnelAvailable(pending.Context.AgentId, pending.Context.PreferredBridgeClient))
        {
            _logger.LogWarning("客户端技能 {Tool} 审批后仍无法路由到本机桥：PreferredBridgeClient={Client}（空=决策浏览器未发现本机桥；非空=该桥不在线/未注册）",
                routeFc.Name, pending.Context.PreferredBridgeClient ?? "(空)");
        }

        // 批量批准：用户对本次运行选择「批准本次运行后续全部操作」→ 记录 runId，恢复后后续审批自动放行
        if (approveAll && approved && !string.IsNullOrEmpty(pending.RunId))
        {
            _autoApprovedRuns[pending.RunId] = 0;
            _logger.LogInformation("启用批量批准：run={RunId} by={Member}", pending.RunId, memberId);
        }

        // 多轮审批防护：外部服务异常时可能恢复后反复中断——超过最大轮数则终止运行（结束消息 + 广播错误）
        if (pending.ResumeCount >= _execution.MaxInteractionRounds)
        {
            _logger.LogWarning("交互恢复超过最大轮数（{Max}），终止运行：interrupt={InterruptId}", _execution.MaxInteractionRounds, interruptId);
            _ = SafeEndAsync(pending.Context, pending.MessageId);
            await _hub.Value.BroadcastAsync(pending.GroupId, new RunErrorEvent
            {
                GroupId = pending.GroupId,
                ErrorCode = "AGENT_INTERACTION_LIMIT",
                Message = $"智能体审批交互超过最大轮数（{_execution.MaxInteractionRounds}），运行已终止，请重新发起消息",
                Timestamp = _hub.Value.NowMs,
            }, ct: CancellationToken.None);
            if (pending.BridgeClient is not null) await pending.BridgeClient.DisposeAsync();
            return true;
        }

        _logger.LogInformation("交互决策：interrupt={InterruptId} member={Member} approved={Approved} hasToolResult={HasToolResult} toolResultLen={ToolResultLen}",
            interruptId, memberId, approved, !string.IsNullOrEmpty(toolResult), toolResult?.Length ?? 0);
        ClientToolTrace.Write($"RESOLVE interrupt={interruptId} member={memberId} approved={approved} hasToolResult={!string.IsNullOrEmpty(toolResult)} toolResultLen={toolResult?.Length ?? 0} agent={pending.Context.AgentId}");
        // 恢复任务与 HTTP 请求生命周期解耦：独立超时 CTS（请求断开 / 前端超时不影响恢复执行，
        // 避免恢复任务在 WaitAsync / 流式消费中被请求取消令牌中断）。预算同样按该任务复杂度放宽。
        // 注意：不能在方法末尾 using 释放——后台任务仍在使用该令牌，须等任务结束后再释放。
        var resumeCts = new CancellationTokenSource(InstallRunBudget(pending.Context, _catalog.GetDefinition(pending.Context.AgentId)).Run);
        _ = Task.Run(async () =>
        {
            var prev = AmbientContext.Value;
            AmbientContext.Value = pending.Context; // 记忆注入等沿用触发时的业务上下文
            try
            {
                // 本地 run：决策作为 User 消息恢复同一 AgentSession（客户端执行技能附带前端回传的 toolResult）
                if (pending.Agent is not null && pending.Session is not null && pending.ApprovalRequest is not null)
                {
                    await ResumeRunAsync(pending, approved, toolResult, resumeCts.Token);
                }
                // 桥接（WS / HTTP standard / hub）：向外部服务发送恢复指令，继续消费其事件流
                else if (pending.BridgeClient is not null)
                {
                    // 按 responseSchema 规范化前端提交的输入 payload（多选拆数组 / 数字转数值）
                    var normalized = AguiBridgeProtocol.NormalizeInputPayload(pending.ResponseSchema, payload);
                    // 恢复沿用话题级外部 threadId（与首发会话一致，外部服务据此关联中断运行）
                    var externalThreadId = AgentGatewayHelpers.BuildExternalThreadId(pending.Context.ThreadId, pending.Context.TopicId);
                    await pending.BridgeClient.ResumeInteractionAsync(
                        pending.ExternalInterruptId ?? pending.InterruptId,
                        externalThreadId, pending.RunId, pending.GroupId, approved, resumeCts.Token,
                        pending.ExternalToolCallId, pending.ExternalToolName, pending.ExternalToolArguments,
                        input, pending.InputField, normalized);
                    await ResumeBridgeStreamAsync(pending, resumeCts.Token);
                }
                else
                {
                    _logger.LogWarning("交互恢复缺少运行现场：interrupt={InterruptId}", interruptId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "交互恢复运行失败：interrupt={InterruptId}", interruptId);
                // 恢复失败补偿：交互已被消费（TryRemove）但运行未恢复——广播错误并结束挂起的消息，
                // 避免「交互已消费但消息永久悬挂」（前端看到错误卡片，而不是永远转圈）
                try
                {
                    await _hub.Value.BroadcastAsync(pending.GroupId, new RunErrorEvent
                    {
                        GroupId = pending.GroupId,
                        ErrorCode = "AGENT_RESUME_ERROR",
                        Message = AgentGatewayHelpers.DescribeModelError(ex),
                        Timestamp = _hub.Value.NowMs,
                    }, ct: CancellationToken.None);
                }
                catch (Exception broadcastEx)
                {
                    _logger.LogDebug(broadcastEx, "恢复失败错误广播失败（已忽略）：interrupt={InterruptId}", interruptId);
                }
                await SafeEndAsync(pending.Context, pending.MessageId);
            }
            finally
            {
                AmbientContext.Value = prev;
                resumeCts.Dispose(); // 任务结束才释放（任务期间不可释放令牌）
            }
        });
        return true;
    }

    /// <summary>恢复被中断的运行：同一 AgentSession 继续流式，最终结果追加到中断时保留的同一消息；
    /// 若再次中断且该运行处于「批量批准」态（用户曾对该 run 点过“批准并继续本次运行”），则自动批准后续同类操作（不打断用户）；
    /// 否则保存新的交互请求。运行结束才结束消息（中间内容在中断时已清空）。</summary>
    private async Task ResumeRunAsync(PendingInteraction pending, bool approved, string? toolResult, CancellationToken ct)
    {
        var agent = pending.Agent!;            // 调用方已保证非空（本地 run 分支）
        var session = pending.Session!;
        var runId = pending.RunId; // 保持首轮 runId：外部服务按 threadId+runId 关联中断，多轮 resume 必须一致
        var messageId = pending.MessageId; // 复用中断时保留的消息（内容已清空，等待最终结果）

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(InstallRunBudget(pending.Context, _catalog.GetDefinition(pending.Context.AgentId)).Run);
        var runCt = timeoutCts.Token;
        var sessionLock = GetSessionLock(pending.Context.ThreadId);
        var acquired = false;
        // 恢复运行是独立的异步流（由决策请求触发），需自建工具返回收集器：
        // 首轮那个作用域已随中断返回而结束，此处不重建则收集不到本轮技能产物。
        var prevToolResults = ToolResultCollector.Ambient.Value;
        ToolResultCollector.Ambient.Value = new ToolResultCollector();
        try
        {
            // WaitAsync 移入 try：未获锁就取消/超时时走 catch + finally，不会对未获取的锁 Release（避免 SemaphoreFullException）
            await sessionLock.WaitAsync(runCt);
            acquired = true;
            var accumulated = "";
            var reasoningAccumulated = 0; // 思考过程累计长度（与首轮一致，防推理模型思考过长）
            var resumeRounds = 0;
            var lastApproval = pending.ApprovalRequest!;
            var lastApproved = approved;

            // 客户端 shell 技能 + 内网隧道在线 + 已批准 → 由网关经隧道在桥所在主机执行以取得真实结果
            // （而非依赖前端回传），结果写入 ClientToolResultStore 并作为 toolResult 注入恢复消息。
            if (approved && lastApproval.ToolCall is FunctionCallContent pfc
                && _catalog.GetAgentClientToolNames(pending.Context.AgentId).Contains(pfc.Name, StringComparer.Ordinal)
                && TunnelAvailable(pending.Context.AgentId, pending.Context.PreferredBridgeClient)
                && TryParseClientShell(pfc.Name, out var rCmd, out var rCwd, out var rTimeoutSec))
            {
                var tr = await ExecuteTunnelAsync(
                    pending.Context.AgentId, pending.Context.PreferredBridgeClient, rCmd!, rCwd, rTimeoutSec, ApprovalArgsQuery(pfc),
                    ClientSkillTimeout(rTimeoutSec), runCt);
                if (!string.IsNullOrWhiteSpace(tr))
                {
                    toolResult = tr;
                    ClientToolResultStore.Put(pfc.Name, tr);
                }
                MarkSkillsApproved(pending.Context.ThreadId, pending.Context.AgentId, [pfc.Name]);
                _logger.LogInformation("客户端技能经内网隧道执行（审批后）：agent={AgentId} tool={Tool}", pending.Context.AgentId, pfc.Name);
            }
            else if (approved && lastApproval.ToolCall is FunctionCallContent dfc
                && _catalog.GetAgentClientToolNames(pending.Context.AgentId).Contains(dfc.Name, StringComparer.Ordinal)
                && TunnelAvailable(pending.Context.AgentId, pending.Context.PreferredBridgeClient)
                && IsClientDotnetSkill(dfc.Name)
                && ClientDotnetSource(dfc.Name) is { Length: > 0 } dnSource)
            {
                // 本机 dotnet（C#）技能：必须经本机桥（浏览器无法直接跑任意 C#）；批准后走隧道在桥所在主机编译执行
                var dn = await ExecuteTunnelDotnetAsync(
                    pending.Context.AgentId, pending.Context.PreferredBridgeClient, dnSource, null,
                    ClientSkillTimeout(null), runCt);
                var dnResult = string.IsNullOrWhiteSpace(dn)
                    ? "（本机 dotnet 经桥执行未返回结果 / 超时）" : dn;
                toolResult = dnResult;
                ClientToolResultStore.Put(dfc.Name, dnResult);
                MarkSkillsApproved(pending.Context.ThreadId, pending.Context.AgentId, [dfc.Name]);
                _logger.LogInformation("本机 dotnet 技能经隧道执行（审批后）：agent={AgentId} tool={Tool}", pending.Context.AgentId, dfc.Name);
            }
            else if (!approved && lastApproval.ToolCall is FunctionCallContent refc
                     && IsClientDotnetSkill(refc.Name)
                     && _catalog.GetAgentClientToolNames(pending.Context.AgentId).Contains(refc.Name, StringComparer.Ordinal))
            {
                // 用户拒绝运行本机 dotnet：给模型一个明确的拒绝结果，避免回退到前端试图“本机执行 C#”
                toolResult = "（用户已拒绝在本机执行该 .NET dotnet 技能）";
                ClientToolResultStore.Put(refc.Name, toolResult!);
                lastApproved = false;
            }
            else if (approved && lastApproval.ToolCall is FunctionCallContent acfc
                     && _catalog.GetAgentClientToolNames(pending.Context.AgentId).Contains(acfc.Name, StringComparer.Ordinal)
                     && string.IsNullOrWhiteSpace(toolResult))
            {
                // 客户端技能已批准，但既无前端回传结果、也未能经隧道路由到本机桥（无可用 client）——给模型明确失败原因，
                // 而不是回放占位文本（占位文本会让模型以为技能已在某处执行并脑补出“看似真实”的结果）。
                toolResult = SupportClientUnavailableText(pending.Context, acfc.Name);
                ClientToolResultStore.Put(acfc.Name, toolResult);
            }

            // 批量批准循环：同一 Session 连续流式；后续审批若命中“本次运行批量批准”自动批准，否则交还用户决策
            while (true)
            {
                var resumeMessages = BuildResumeMessage(pending, lastApproval, lastApproved, toolResult, runCt);
                ToolApprovalRequestContent? nextApproval = null;
                await foreach (var update in agent.RunStreamingAsync(resumeMessages, session, new ChatClientAgentRunOptions(), runCt))
                {
                    ClientToolTrace.Write($"RESUME-UPDATE textLen={(update.Text?.Length ?? 0)} cts=[{string.Join(",", update.Contents.Select(c => c.GetType().Name))}]");
                    if (update.Text is { Length: > 0 } text)
                    {
                        var delta = ComputeTextDelta(accumulated, text);
                        if (delta.Length > 0)
                        {
                            await _hub.Value.AppendAgentContentAsync(pending.GroupId, messageId, delta, runCt);
                            accumulated += delta;
                        }
                    }
                    // 恢复后的思考过程同样转发（重新思考 / 决策后继续推理）
                    foreach (var rc in update.Contents.OfType<TextReasoningContent>())
                    {
                        if (rc.Text is not { Length: > 0 } r) continue;
                        if (reasoningAccumulated >= MaxReasoningTotalChars) continue;
                        var remaining = MaxReasoningTotalChars - reasoningAccumulated;
                        var rd = r.Length > remaining ? r[..remaining] : r;
                        reasoningAccumulated += rd.Length;
                        await AppendReasoningAsync(pending.GroupId, messageId, rd, runCt);
                    }
                    // 工具返回收集：技能产物（produce_file 标记）以工具真实返回为准，
                    // 不经模型改写。（批准后技能在本轮才真正执行，故结果出现在恢复流里。）
                    foreach (var fr in update.Contents.OfType<FunctionResultContent>())
                    {
                        if (fr.Result is null) continue;
                        ToolResultCollector.Ambient.Value?.Add(AgentGatewayHelpers.DescribeToolResult(fr.Result));
                    }
                    foreach (var apr in update.Contents.OfType<ToolApprovalRequestContent>())
                    {
                        nextApproval = apr;
                        break;
                    }
                    if (nextApproval is not null) break;
                }

                if (nextApproval is null)
                {
                    ClientToolTrace.Write($"RESUME-END accumulatedLen={accumulated.Length} first= {accumulated.Substring(0, Math.Min(120, accumulated.Length)).Replace(Environment.NewLine, " ")}");
                    break; // 本轮流式正常结束 → 运行完成
                }

                // 又需审批
                resumeRounds++;
                if (resumeRounds > _execution.MaxInteractionRounds)
                {
                    _logger.LogWarning("交互恢复超过最大轮数（{Max}），终止运行：run={RunId}", _execution.MaxInteractionRounds, runId);
                    await SafeEndAsync(pending.Context, messageId);
                    await _hub.Value.BroadcastAsync(pending.GroupId, new RunErrorEvent
                    {
                        GroupId = pending.GroupId,
                        ErrorCode = "AGENT_INTERACTION_LIMIT",
                        Message = $"智能体审批交互超过最大轮数（{_execution.MaxInteractionRounds}），运行已终止，请重新发起消息",
                        Timestamp = _hub.Value.NowMs,
                    }, ct: CancellationToken.None);
                    return;
                }

                // 已批准客户端技能记忆：该客户端技能在此对话里已获用户同意 → 免确认、继续自动执行（同一问题内不再重复弹卡）
                if (nextApproval.ToolCall is FunctionCallContent nfc
                    && _catalog.GetAgentClientToolNames(pending.Context.AgentId).Contains(nfc.Name, StringComparer.Ordinal)
                    && IsSkillApproved(pending.Context.ThreadId, pending.Context.AgentId, nfc.Name))
                {
                    lastApproval = nextApproval;
                    lastApproved = true;
                    _logger.LogInformation("已同意技能自动放行：run={RunId} tool={Tool}", runId, nfc.Name);
                    continue;
                }

                // 批量批准生效：自动批准本次运行后续的审批操作，不打断用户
                if (_autoApprovedRuns.ContainsKey(runId))
                {
                    lastApproval = nextApproval;
                    lastApproved = true;
                    _logger.LogInformation("批量批准自动放行：run={RunId} tool={Tool}", runId, (nextApproval.ToolCall as FunctionCallContent)?.Name ?? "unknown");
                    continue;
                }

                // 非批量：清空已回灌的中间内容，保存新的交互请求（同触发者，同一条消息）
                await _hub.Value.ResetAgentContentAsync(pending.GroupId, messageId, runCt);
                var interruptId = "interrupt_" + IdGenerator.NewId();
                var fc = nextApproval.ToolCall as FunctionCallContent;
                _pendingInteractions[interruptId] = new PendingInteraction(
                    interruptId, pending.GroupId, pending.AgentId, runId, messageId,
                    pending.TargetMemberId, pending.Context.TopicId, _hub.Value.NowMs, pending.Context,
                    ExternalInterruptId: null,
                    ExternalToolCallId: null, ExternalToolName: null, ExternalToolArguments: null,
                    Agent: agent, Session: session, ApprovalRequest: nextApproval,
                    BridgeClient: null, ResumeCount: pending.ResumeCount + resumeRounds,
                    SuppressMessage: pending.SuppressMessage);
                await PurgeExpiredInteractions();
                await _hub.Value.BroadcastAsync(pending.GroupId, new AgentInteractionRequestEvent
                {
                    GroupId = pending.GroupId,
                    MessageId = messageId,
                    ThreadId = pending.Context.ThreadId,
                    RunId = runId,
                    InterruptId = interruptId,
                    ToolCallId = fc?.CallId ?? "tool_" + IdGenerator.NewId(),
                    ToolName = fc?.Name ?? "unknown",
                    ToolArguments = fc?.Arguments is { } args ? JsonSerializer.SerializeToElement(args) : null,
                    Message = $"智能体请求你确认：是否执行操作「{fc?.Name}」？",
                    TargetMemberId = pending.TargetMemberId,
                    Timestamp = _hub.Value.NowMs,
                }, ct: runCt);
                _logger.LogInformation("交互恢复流再次中断：run={RunId} interrupt={InterruptId} target={Target}",
                    runId, interruptId, pending.TargetMemberId);
                return; // 消息保持开启：等下一轮决策恢复后继续追加最终结果
            }

            // 运行完成
            _autoApprovedRuns.TryRemove(runId, out _); // 批量批准随运行结束失效
            var attached = await AttachPublishedProductsAsync(pending.GroupId, messageId, accumulated, runCt);
            // 空正文兜底：交互前通常已清空过正文（ResetAgentContentAsync），若恢复后又什么都没产出，
            // 消息会变成完全空白 —— 与其它路径同一口径，补一句人话。
            if (string.IsNullOrWhiteSpace(accumulated) && attached == 0)
                await _hub.Value.AppendAgentContentAsync(pending.GroupId, messageId, EmptyReplyFallback, runCt);
            // 交付物兜底：正文已由兜底流写入，这里只修正媒体/链路的挂载并把消息收尾
            if (!pending.SuppressMessage)
                await _hub.Value.EndAgentMessageAsync(pending.GroupId, messageId, runCt);
            else
                await EndSuppressedMessageAsync(pending.GroupId, messageId, runCt);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "智能体交互恢复运行失败：run={RunId}", pending.RunId);
            _logger.LogWarning(ex, "智能体交互恢复运行异常：interrupt={InterruptId}", pending.InterruptId);
            ClientToolTrace.Write($"RESUME-EX unless={ex.GetType().Name} msg={ex.Message}");
            _autoApprovedRuns.TryRemove(runId, out _);
            await SafeEndAsync(pending.Context, messageId);
        }
        finally
        {
            if (acquired) sessionLock.Release(); // 未获得锁时不 Release（避免 SemaphoreFullException）
            ToolResultCollector.Ambient.Value = prevToolResults; // 恢复外层工具返回收集器
        }
    }

    /// <summary>构造审批 / 客户端工具恢复时的回灌消息：
    /// 一律返回 <see cref="ToolApprovalRequestContent.CreateResponse"/>（批准 / 拒绝，满足 MSAGENT 审批决议；否则恢复抛「no matching ToolApprovalResponseContent」），
    /// 客户端执行技能（<see cref="AgentSkillDefinition.ExecutionLocation"/> = Client）批准且前端已回传 toolResult 时，
    /// <b>额外追加一条 User 消息，把「该工具已在客户端执行、结果为 …」直接注入模型上下文</b>——
    /// 规避 MSAGENT 的 `CreateResponse` 在真实 `AsAIAgent` 路径不执行占位函数、以及工具结果经 OpenAI 序列化可能到不了模型的问题。</summary>
    private IReadOnlyList<ChatMessage> BuildResumeMessage(PendingInteraction pending, ToolApprovalRequestContent approval, bool approved, string? toolResult, CancellationToken ct)
    {
        var fc = approval.ToolCall as FunctionCallContent;
        var isClientTool = fc is not null
            && _catalog.GetAgentClientToolNames(pending.Context.AgentId).Contains(fc.Name, StringComparer.Ordinal);
        var msgs = new List<ChatMessage> { new(ChatRole.User, [approval.CreateResponse(approved)]) };
        if (isClientTool && approved && !string.IsNullOrEmpty(toolResult))
        {
            // 客户端执行技能：前端已在本地执行并回传结果 → 以一句明确的 User 消息注入模型，
            // 并要求它先<b>回归校验</b>结果（是否正常 / 是否满足问题 / 有无风险），再做有洞察的结论，
            // 而非直接复述原始输出。
            _logger.LogInformation("客户端技能恢复：注入前端执行结果 tool={Tool} agent={Agent} resultLen={Len}", fc!.Name, pending.Context.AgentId, toolResult.Length);
            ClientToolTrace.Write($"INJECT-RESULT tool={fc!.Name} agent={pending.Context.AgentId} resultLen={toolResult.Length} first= {toolResult.Substring(0, Math.Min(80, toolResult.Length))}");
            msgs.Add(new ChatMessage(ChatRole.User,
                $"[前端工具] {fc.Name} 已在本机执行完毕，下面是它返回的数据：\n{toolResult}\n\n"
                + "请先对这份数据进行<b>回归校验</b>，再作答：\n"
                + "① 数据是否完整可读、命令是否正常返回（有无报错/异常）；\n"
                + "② 有没有值得关注的异常、风险或异常趋势（如磁盘将满、内存占用过高、连接异常、报错）；\n"
                + "③ 基于该校验给出精炼结论和可执行的建议或下一步排查方向。\n"
                + "不必复述原始字段，直接谈判断与建议；数据本身无法回答问题时如实说明。无需再调用该工具。"));
        }
        ClientToolTrace.Write($"RESUME-MSG tool={(fc?.Name ?? "?")} approved={approved} hasToolResult={!string.IsNullOrEmpty(toolResult)} isClientAgentTool={isClientTool} agent={pending.Context.AgentId} msgCount={msgs.Count}");
        return msgs;
    }

    /// <summary>清理超时未决策的交互请求
    /// 并释放超时交互保留的桥接连接（WS / HTTP standard / hub，防连接泄漏）。async：桥接连接释放为异步。</summary>
    private async Task PurgeExpiredInteractions()
    {
        try
        {
            var now = _hub.Value.NowMs;
            foreach (var kv in _pendingInteractions)
            {
                if (now - kv.Value.CreatedAtMs > (long)_execution.InteractionTtlMinutes * 60_000 && _pendingInteractions.TryRemove(kv.Key, out var pending))
                {
                    // 交互超时未决策：消息仍处于“等待确认”状态（内容已清空），安全结束它
                    await SafeEndAsync(pending.Context, pending.MessageId);
                    // 释放超时交互保留的桥接连接（防连接 / 线程泄漏）
                    if (pending.BridgeClient is not null)
                    {
                        try { await pending.BridgeClient.DisposeAsync(); }
                        catch { /* 忽略 */ }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "交互清理异常（已忽略）");
        }
    }

    private async Task SafeEndAsync(AgentInvocationContext context, string? messageId)
    {
        if (messageId is null) return;
        // 若这条消息跑完/中断后正文为空，绝不落成空白泡——先补一句“无法形成正文”的说明（仅当真没有内容时）。
        await TryStampFallbackIfEmptyAsync(context.GroupId, messageId,
            "（本轮回复未能生成可展示的正文，可能被中断或结果为空。请直接再说一次，或把要求拆细一点，我会重新给出成稿。）");
        try { await _hub.Value.EndAgentMessageAsync(context.GroupId, messageId, CancellationToken.None); }
        catch (Exception ex) { _logger.LogDebug(ex, "结束智能体消息失败：{MessageId}", messageId); }
    }

    /// <summary>交付物兜底的消息收尾：该消息由外层指派/提升路由开启（外层已返回），
    /// 所以这里只做空正文保护与结束，不重复发事件。</summary>
    private async Task EndSuppressedMessageAsync(string groupId, string messageId, CancellationToken ct)
    {
        await TryStampFallbackIfEmptyAsync(groupId, messageId,
            "（交付环节未能生成文件，请再说一次或把要求拆细一点。）");
        try { await _hub.Value.EndAgentMessageAsync(groupId, messageId, ct); }
        catch (Exception ex) { _logger.LogDebug(ex, "结束交付物兜底消息失败：{MessageId}", messageId); }
    }

    /// <summary>收尾前的空正文兜底：仅当流式消息的正文仍为空时，先补一句可见说明，避免“只有卡、没有字”的空白回复。</summary>
    private async Task TryStampFallbackIfEmptyAsync(string groupId, string messageId, string note)
    {
        try
        {
            var msg = _hub.Value.Store.GetMessage(groupId, messageId);
            if (msg is null || !string.IsNullOrWhiteSpace(msg.Content)) return;
            await _hub.Value.AppendAgentContentAsync(groupId, messageId, note, CancellationToken.None);
            _logger.LogInformation("空正文兜底：智能体消息 {MessageId} 未产出内容，已附提示（group={GroupId}）", messageId, groupId);
        }
        catch (Exception ex) { _logger.LogDebug(ex, "空正文兜底写入失败（忽略）：{MessageId}", messageId); }
    }

    /// <summary>
    /// 从流式文本帧中计算增量：
    /// 累计文本（后续帧以全部已输出文本为前缀）→ 取新增部分；
    /// 增量片段（各帧互不重叠）→ 整体作为 delta。
    /// </summary>
    internal static string ComputeTextDelta(string accumulated, string text)
        => text.StartsWith(accumulated, StringComparison.Ordinal) ? text[accumulated.Length..] : text;
}
