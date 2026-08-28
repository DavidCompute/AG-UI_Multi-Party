using System.Collections.Concurrent;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using AguiGroupChat.Hub.Agents;
using AguiGroupChat.Hub.Infra;
using AguiGroupChat.Hub.Messaging;
using AguiGroupChat.Hub.Models;
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
    /// <summary>注入模型上下文的群历史消息条数（滑动窗口，控制上下文规模）。</summary>
    private const int ContextWindowMessages = 12;

    /// <summary>外部 AG-UI 会话首次建立时发送的话题历史条数上限（全量，存储层上限 5000）。</summary>
    private const int BridgeFullHistoryMax = 5000;

    /// <summary>外部 AG-UI 会话建立后单次增量发送条数上限（上次节点之后的新消息）。</summary>
    private const int BridgeIncrementMax = 100;

    /// <summary>历史单条消息文本截断长度。</summary>
    private const int MaxContextCharsPerMessage = 500;

    /// <summary>思考过程总量截断（推理模型 reasoning_content 可能很长：防消息 / 前端 / 存储被撑爆）。</summary>
    private const int MaxReasoningTotalChars = 12000;

    /// <summary>桥接流式正文累计上限（standard 方言是累计文本，防外部服务流式下发无限长正文撑爆内存；截断到前缀不影响增量计算）。</summary>
    private const int MaxBridgeAccumulatedChars = 50000;

    /// <summary>
    /// 当前 run 的业务上下文（AsyncLocal ambient，与 MSAGENT 内部 AgentRunContext 机制同构）。
    /// <see cref="MemoryContextProvider"/>（AIContextProvider）在 InvokingAsync 中读取它完成记忆检索注入。
    /// </summary>
    public static readonly AsyncLocal<AgentInvocationContext?> AmbientContext = new();

    private readonly AgentCatalog _catalog;
    private readonly Lazy<GroupHub> _hub;
    private readonly AgentOptions _options;
    private readonly AttachmentStore? _attachmentStore;
    private readonly ILogger<AgentGateway> _logger;
    // 模型 token 用量统计与配额（可选：未注册用量存储时不统计）
    private readonly Lazy<AguiGroupChat.Hub.Agents.AgentUsageService?> _usage;
    // 技能库（可复用技能：shell/http/prompt）——供确定性编排计划枚举与按计划激活技能
    private readonly Lazy<AgentSkillCatalog?> _skillCatalog;
    // 轻量运行指标（可选，6.1 可观测性）
    private readonly Lazy<MetricsService?> _metrics;
    // 桥接断线自动重连退避（3.1）：连续失败后短时抑制重连（防断线风暴）
    private readonly BridgeCircuitBreaker _bridgeCircuit = new();
    // 每个线程（群）一个会话锁：并发流式写入同一群消息时串行化。
    // 存储 (锁, 上次使用毫秒时间戳)，超时未用（30 分钟）自动清理，避免群解散后残留泄漏。
    private const long SessionLockTtlMs = 30 * 60 * 1000;
    private const int SessionLockMaxEntries = 512;
    private readonly ConcurrentDictionary<string, (SemaphoreSlim Lock, long LastUsedMs)> _sessionLocks = new(StringComparer.Ordinal);

    /// <summary>单次模型 / 桥接流式调用的最长运行时间（分钟）：模型挂起时防止 Task 永久占用。</summary>
    private const int StreamTimeoutMinutes = 5;

    /// <summary>待决策的人机交互（协议 4.5）：运行中断后保存会话与审批请求，等触发者决策后恢复。</summary>
    private const long InteractionTtlMs = 10 * 60 * 1000;
    private readonly ConcurrentDictionary<string, PendingInteraction> _pendingInteractions = new(StringComparer.Ordinal);

    /// <summary>批量批准运行集：key = runId。用户对某运行选择「批准本次运行后续全部操作」后，
    /// 该运行后续的审批工具自动放行（不再打断），直到运行结束清除。</summary>
    private readonly ConcurrentDictionary<string, byte> _autoApprovedRuns = new(StringComparer.Ordinal);

    /// <summary>外部 AG-UI 桥接增量游标：key = agentId|外部threadId，value = 上次已发送的本话题最后消息 ID。
    /// 会话（首次触发）发送话题全部历史；会话建立后只发游标之后的本话题新消息（增量）。
    /// 经扩展区「bridgeCursors」持久化（组合根 RegisterBridgeCursorPersistence），网关重启后游标不丢。</summary>
    private readonly ConcurrentDictionary<string, string> _bridgeCursors = new(StringComparer.Ordinal);

    /// <summary>变更通知（驱动持久化落盘）：游标推进时 Notify，由持久化服务定时合并写入。</summary>
    private readonly ChangeHub? _changes;

    /// <summary>周期清理定时器（60s）：清理超时未决策的交互（HITL 悬挂）+ 超时未用的会话锁（残留泄漏）。</summary>
    private readonly Timer _purgeTimer;

    /// <summary>同一消息最多允许的审批轮数（防止外部服务异常导致恢复后反复中断的死循环）。</summary>
    private const int MaxInteractionRounds = 5;

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
        int ResumeCount = 0);                // 已恢复轮数（多轮审批防护：超过 MaxInteractionRounds 强制结束）

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
        _attachmentStore = attachmentStore;
        _logger = logger;
        _changes = services.GetService<ChangeHub>(); // 游标持久化脏位通知（可选：未注册持久化时不落盘）
        _usage = new Lazy<AguiGroupChat.Hub.Agents.AgentUsageService?>(() =>
            services.GetService(typeof(AguiGroupChat.Hub.Agents.AgentUsageService)) as AguiGroupChat.Hub.Agents.AgentUsageService);
        _skillCatalog = new Lazy<AgentSkillCatalog?>(() => services.GetService(typeof(AgentSkillCatalog)) as AgentSkillCatalog);
        // 轻量运行指标（可选，6.1）
        _metrics = new Lazy<MetricsService?>(() =>
            services.GetService(typeof(MetricsService)) as MetricsService);
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
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "定时清理任务异常（已忽略）");
        }
    }

    /// <summary>清理超时未用的会话锁（群解散后残留泄漏防护）：无条件遍历，不依赖条目数阈值（Count>=512 阈值仅作兜底）。</summary>
    private void PurgeExpiredSessionLocks()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var kv in _sessionLocks)
        {
            if (now - kv.Value.LastUsedMs > SessionLockTtlMs)
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

    private async Task<AgentInvocationResult> InvokeCoreAsync(AgentInvocationContext context, CancellationToken ct)
    {
        var def = _catalog.GetDefinition(context.AgentId);
        if (def is null)
            return new AgentInvocationResult(false, null, "AGENT_NOT_CONFIGURED");

        // AG-UI 桥接角色：不经本地大模型，以 AG-UI 协议对接外部 AG-UI 服务，
        // 外部服务的流式回复经群聊事件回灌。智能体端点与全局 AguiBridge:Endpoint 任一配置即走桥接。
        if (!string.IsNullOrWhiteSpace(def.BridgeEndpoint) || !string.IsNullOrWhiteSpace(_options.AguiBridge?.Endpoint))
            return await InvokeBridgeAsync(context, def, ct);

        // 编排流水线（1.1）：配置了 Pipeline 的智能体不直接调本地大模型，而是按步骤依次调用子智能体
        if (def.Pipeline is { Count: > 0 })
            return await InvokePipelineAsync(context, def, ct);

        // 角色交接（1.2）：配置了 RelayToAgentId 的智能体整轮委托给中继智能体（防止自环 / 接力环）
        var relayTarget = def.RelayToAgentId;
        if (!string.IsNullOrWhiteSpace(relayTarget) && relayTarget != def.AgentId
            && _catalog.GetDefinition(relayTarget) is { RelayToAgentId: null or "" })
            return await InvokeRelayAsync(context, def, relayTarget, ct);

        // 语境触发（Contextual）：群内触发模式优先（可覆盖角色默认），先结合群上下文判断是否应该发言，
        // 不发言则静默跳过（不发任何事件）
        var effectiveMode = context.TriggerMode ?? def.TriggerMode;
        if (effectiveMode == AgentTriggerMode.Contextual && !await ShouldSpeakAsync(context, def, ct))
        {
            _logger.LogInformation("智能体 {AgentId} 语境判断为保持沉默（group={GroupId}）", context.AgentId, context.GroupId);
            return new AgentInvocationResult(false, null, "AGENT_DECIDED_SILENT");
        }

        // 任务指派 / 问题提升（组织化路由）：被显式 @（Mentioned）触发，且配置了「指派白名单」或「提升目标」时，
        // 按本数字员工系统提示词推断：该我答 → 直接答；不该我答且白名单有合适 → 向下指派；
        // 无合适指派对象且配了提升目标 → 向上提升；再无可解 → 回答不能解决。
        if (effectiveMode == AgentTriggerMode.Mentioned
            && (def.AssignmentIds is { Count: > 0 } || !string.IsNullOrWhiteSpace(def.EscalationAgentId)))
        {
            return await InvokeAssignmentEscalationAsync(context, ct);
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

        string? messageId = null;
        ToolApprovalRequestContent? approval = null;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(StreamTimeoutMinutes)); // 模型挂起保护
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

            var accumulated = "";
            var reasoningAccumulated = 0; // 思考过程累计长度（防推理模型思考过长撑爆消息 / 前端）
            var userMessage = new ChatMessage(ChatRole.User, await BuildUserMessageAsync(context, runCt));
            var runOptions = new ChatClientAgentRunOptions();

            // 模型限流（429）/ 网关 5xx / 连接重置时指数退避重试：避免群聊中智能体偶发哑火。
            // 重试从头流式输出（清空累计跟踪与已广播的半截内容）；审批中断不触发重试（approval 置位后 break）。
            var attempt = 0;
            const int MaxModelAttempts = 2;
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

                    break; // 模型流正常完成（审批中断时 approval 已置位，由下方分支处理）
                }
                catch (Exception ex) when (AgentGatewayHelpers.IsRetryableModelError(ex) && attempt < MaxModelAttempts)
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
                await _hub.Value.ResetAgentContentAsync(context.GroupId, messageId, runCt);
                var interruptId = "interrupt_" + IdGenerator.NewId();
                var fc = approval.ToolCall as FunctionCallContent;
                // 客户端执行技能：toolName 命中则标记 kind=client_tool，下发给前端执行（复用 HITL 通道下发 + 回传）
                var isClientTool = fc is not null
                    && _catalog.GetAgentClientToolNames(context.AgentId).Contains(fc.Name, StringComparer.Ordinal);
                var clientRunner = isClientTool ? GetSkillById(fc!.Name)?.ClientRunner : null;
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

            await AttachPublishedProductsAsync(context.GroupId, messageId, accumulated, runCt);
            await AttachAgentChainAsync(context, messageId, runCt);
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
            await _hub.Value.BroadcastTypingAsync(new GroupTypingRequest
            {
                GroupId = context.GroupId,
                MemberId = context.AgentId,
                IsTyping = false,
            }, CancellationToken.None);
        }
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
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(StreamTimeoutMinutes));
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

            var finalText = sb.Length == 0 ? "（流水线未产出内容）" : sb.ToString().Trim();
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
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(StreamTimeoutMinutes));
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
            foreach (var chunk in AgentGatewayHelpers.ChunkReply(text, 160))
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
        var runId = "run_" + IdGenerator.NewId();
        _logger.LogInformation("智能体 {AgentId} 进入指派/提升路由（run={RunId}，group={GroupId}）", context.AgentId, runId, context.GroupId);
        await _hub.Value.BroadcastTypingAsync(new GroupTypingRequest { GroupId = context.GroupId, MemberId = context.AgentId, IsTyping = true }, ct);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(StreamTimeoutMinutes));
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
                await ExecuteCoordinatedPlanAsync(context, plan, messageId, runCt);
            }
            else
            {
                // 非编排路径：链路可视化 + 前缀 + 直接方案
                RecordStandinChain(context, hops);
                var prefixNames = hops.Where(h => !string.IsNullOrWhiteSpace(h.AgentId)).Select(h => h.AgentNickname).ToList();
                foreach (var name in prefixNames)
                    await _hub.Value.AppendAgentContentAsync(context.GroupId, messageId, $"（{name} 代为处理）\n", runCt);
                finalText ??= "（处理对象未返回内容）";
                foreach (var chunk in AgentGatewayHelpers.ChunkReply(finalText.Trim(), 160))
                    await _hub.Value.AppendAgentContentAsync(context.GroupId, messageId, chunk, runCt);
            }

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

    /// <summary>最多纳入计划的清单项 / 步骤数（防配置病态深链 / 打爆模型时长）。</summary>
    private const int CoordinatorPlanMaxItems = 12;
    private const int CoordinatorPlanMaxSteps = 6;

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
            while (queue.Count > 0 && reached.Count < CoordinatorPlanMaxItems)
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
                    if (catalog.Get(refId) is { } def && !skills.ContainsKey(def.SkillId)) skills[def.SkillId] = def;
            }
            Collect(root);
            foreach (var r in reached) Collect(r);

            if (reached.Count == 0 && skills.Count == 0)
                return null; // 无可指派 / 可调用

            var steps = await PlanCoordinatedAsync(context, root, input, reached, skills.Values.ToList(), ct);
            if (steps is null || steps.Count == 0) return null;
            return new CoordinatedPlan(steps.Take(CoordinatorPlanMaxSteps).ToList(), reached, skills, input);
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
    private async Task ExecuteCoordinatedPlanAsync(AgentInvocationContext context, CoordinatedPlan plan, string messageId, CancellationToken ct)
    {
        var root = _catalog.GetDefinition(context.AgentId);
        if (root is null) return;
        var gid = context.GroupId;

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

        // 2) 逐项执行 & 点亮
        var sb = new StringBuilder();
        var working = plan.Input;
        var hops = new List<ChainNode>();
        var di = 0;
        for (var si = 0; si < plan.Steps.Count; si++)
        {
            var step = plan.Steps[si];
            if (step.Action == "dispatch")
            {
                if (_catalog.GetDefinition(step.Target) is not { } target
                    || !plan.Reached.Any(r => r.AgentId == step.Target)) continue;
                var child = _catalog.GetOrCreate(step.Target);
                // 预判下游：若下一步是“需要 ${query} 输入的技能”，则让本员工<b>只输出该值本身</b>，供技能直接使用
                var feedsParameterizedSkill = si + 1 < plan.Steps.Count
                    && plan.Steps[si + 1].Action == "skill"
                    && plan.Skills.TryGetValue(plan.Steps[si + 1].Target, out var nextSkill)
                    && AgentGatewayHelpers.SkillRequiredInputs(nextSkill).Contains("query", StringComparer.Ordinal);
                var prompt = "你正被「" + (target.Nickname ?? step.Target) + "」指派处理，请就以下请求给出你的专业结论。\n\n问题：\n" + working
                    + (sb.Length > 0 ? "\n\n前序已产出（可参考）：\n" + sb : "")
                    + (feedsParameterizedSkill
                        ? "\n\n<b>请只输出所需的值本身</b>（如一个完整 URL / 地址 / 参数），不要添加解释或多余文字——它会被直接作为下一步技能的输入。"
                        : "")
                    + "\n\n只输出本步结论，不要复述前序内容。";
                var session = await child.CreateSessionAsync(ct);
                // 关键：把 ambient 上下文切到<b>子员工</b>（保留群/话题/触发者，只改 AgentId/Nickname）
                // ——MemoryContextProvider 据此注入<b>子员工自己的知识库/记忆</b>，否则它会按宿主(Exchange连接测试助手)
                // 的库检索（宿主没绑配置库 → 配置管理员丢上下文、答“数据不足”）。
                var prev = AgentGateway.AmbientContext.Value;
                AgentGateway.AmbientContext.Value = context with { AgentId = target.AgentId, AgentNickname = target.Nickname ?? target.AgentId };
                try
                {
                    var resp = await child.RunAsync([new ChatMessage(ChatRole.User, prompt)], session, null, ct);
                    var stepOut = string.IsNullOrWhiteSpace(resp.Text) ? "（未返回内容）" : resp.Text.Trim();
                    // 计划卡已展示“指派「配置管理员」代为处理”，正文不再叠加（X 代为处理）前缀，避免重复
                    if (!hops.Any(h => h.AgentId == step.Target))
                        hops.Add(new ChainNode { Kind = "assignment", AgentId = step.Target, AgentNickname = target.Nickname ?? step.Target, Query = AgentGatewayHelpers.TruncateForChain(working), Result = AgentGatewayHelpers.TruncateForChain(stepOut) });
                    sb.Clear().Append(stepOut);
                    working = stepOut;
                }
                finally
                {
                    AgentGateway.AmbientContext.Value = prev;
                }
                if (di < display.Count) { display[di] = new PlanStepInfo { Id = display[di].Id, Text = display[di].Text, Done = true }; di++; }
                await BroadcastPlanAsync(gid, messageId, display, ct);
            }
            else if (step.Action == "skill")
            {
                if (!plan.Skills.TryGetValue(step.Target, out var skill)) continue;
                // 参数化技能（body 含 ${query}）：从“上一步输出”中尽可能提取干净的 URL/值，再作为技能输入
                var skillQuery = working;
                if (AgentGatewayHelpers.SkillRequiredInputs(skill).Contains("query", StringComparer.Ordinal))
                {
                    var clean = AgentGatewayHelpers.ExtractCleanValueForSkill(skillQuery);
                    if (!string.IsNullOrWhiteSpace(clean)) skillQuery = clean;
                }
                var res = await _catalog.RunSkillAsync(skill, skillQuery, ct);
                _logger.LogInformation("编排计划激活技能：agent={AgentId} skill={SkillId} query={Q}", context.AgentId, skill.SkillId, AgentGatewayHelpers.TruncateForChain(skillQuery));
                if (!hops.Any(h => h.AgentId == skill.SkillId))
                    hops.Add(new ChainNode { Kind = "skill", AgentId = skill.SkillId, AgentNickname = skill.Name ?? skill.SkillId, Query = AgentGatewayHelpers.TruncateForChain(skillQuery), Result = AgentGatewayHelpers.TruncateForChain(res) });
                sb.Clear().Append(res);
                working = res;
                if (di < display.Count) { display[di] = new PlanStepInfo { Id = display[di].Id, Text = display[di].Text, Done = true }; di++; }
                await BroadcastPlanAsync(gid, messageId, display, ct);
            }
        }

        // 3) 综合答复制止
        display[^1] = new PlanStepInfo { Id = display[^1].Id, Text = display[^1].Text, Done = true };
        await BroadcastPlanAsync(gid, messageId, display, ct);

        // 链路可视化
        RecordStandinChain(context, hops);

        // 4) 综合答复
        var final = await SynthesizePlanAnswerAsync(context, root, plan.Input, sb.ToString(), ct);
        var text = string.IsNullOrWhiteSpace(final) ? sb.ToString() : final;
        if (string.IsNullOrWhiteSpace(text)) text = "（处理对象未返回内容）";
        foreach (var chunk in AgentGatewayHelpers.ChunkReply(text.Trim(), 160))
            await _hub.Value.AppendAgentContentAsync(gid, messageId, chunk, ct);
    }

    private async Task BroadcastPlanAsync(string groupId, string messageId, IReadOnlyList<PlanStepInfo> steps, CancellationToken ct)
    {
        try
        {
            await _hub.Value.BroadcastMessagePlanAsync(groupId, messageId, "执行计划", steps, ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "编排计划广播失败（已忽略）：group={GroupId}", groupId);
        }
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
            // 技能需要的外部输入（body 里的 ${query}/${xxx} 占位符）→ 提示计划先拿到该值再调用它
            var inputs = AgentGatewayHelpers.SkillRequiredInputs(s);
            if (inputs.Count > 0)
                sb.Append("【需要输入：").Append(string.Join("、", inputs)).Append("】");
            sb.AppendLine();
        }
        if (reached.Count == 0) sb.AppendLine("- （无可指派的数字员工）");
        if (skills.Count == 0) sb.AppendLine("- （无可调用的技能）");
        return sb.ToString();
    }

    private async Task<List<PlanStep>?> PlanCoordinatedAsync(AgentInvocationContext context, AgentDefinition root, string input,
        IReadOnlyList<AgentDefinition> reached, IReadOnlyList<AgentSkillDefinition> skills, CancellationToken ct)
    {
        var agent = _catalog.GetOrCreate(root.AgentId);
        var inventory = BuildPlanInventory(root, reached, skills);
        var prompt =
            "你是群聊的协调员「" + (root.Nickname ?? root.AgentId) + "」。\n\n"
            + "用户问题：\n" + input + "\n\n"
            + "你掌握的组织分工与技能如下（只能从中选，不能造）：\n" + inventory + "\n\n"
            + "请针对该问题制定一张<b>执行计划</b>：\n"
            + "- 若需要某数字员工提供信息/处理某部分 → {\"action\":\"dispatch\",\"target\":\"<该员工id>\"}\n"
            + "- 若需要调用某技能做检测/验证 → {\"action\":\"skill\",\"target\":\"<该技能id>\"}\n"
            + "- 最后用一步 {\"action\":\"answer\",\"note\":\"<你要怎么综合答复>\"} 汇总。\n"
            + "<b>依赖顺序很重要</b>：如果一个技能<b>需要某个输入</b>（见技能后的【需要输入：…】），而这个输入由某位员工掌握，\n"
            + "你必须<b>先用一步 dispatch 该员工拿到输入值</b>，<b>再</b>在后续步骤里调用该技能——技能步骤会自动收到它前一步的结果作为输入。\n"
            + "例如：要“测 Exchange 连接”需先知道 OWA 地址，而地址由配置管理员提供，则应安排 [dispatch→配置管理员, skill→连接测试技能, answer]。\n"
            + "只输出 JSON，不要任何其他文字：{\"steps\":[...]}，步骤 1~" + CoordinatorPlanMaxSteps + " 条。若问题与任何员工/技能都不相关，输出 {\"steps\":[]}。";
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

    /// <summary>指派/提升路由的最大层数（防配置病态深链 / 打爆模型时长的兑底）。</summary>
    private const int MaxRouteDepth = 4;

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
        if (def is null || depth > MaxRouteDepth || visited.Contains(agentId))
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
    /// 任务指派目标<b>排序</b>：在 <paramref name="candidates"/>（白名单）里按匹配度从高到低输出一个或多个
    /// 候选下游数字员工（可逗号分隔返回多个，供上层做递归探测回退）；都不合适输出 NONE。
    /// 返回候选 agentId 的已排序列表（保证都在 <paramref name="candidates"/> 内）。
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
        // 解析输出：逗号分隔的候选 id（兼容单个 / NONE / 混合文本），只保留在白名单内的
        var choices = (resp.Text ?? "NONE")
            .Split([',', '，'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return choices.Where(c => candidates.Contains(c)).ToList();
    }

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
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(StreamTimeoutMinutes)); // 外部服务挂起保护
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
            timeoutCts.CancelAfter(TimeSpan.FromMinutes(StreamTimeoutMinutes));
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
    /// 由于 MSAGENT 管道内部消化工具执行、不暴露 FunctionResultContent，只能靠正文引用回档附件。
    /// </summary>
    private async Task AttachPublishedProductsAsync(string groupId, string messageId, string content, CancellationToken ct)
    {
        if (_attachmentStore is null || string.IsNullOrEmpty(content)) return;
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
            if (added > 0)
                _logger.LogInformation("工作型智能体产物回档：{Count} 个附件挂到消息 {MessageId}", added, messageId);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "publish_file 产物回档扫描失败（已忽略）");
        }
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
    private async Task<string> BuildUserMessageAsync(AgentInvocationContext context, CancellationToken ct)
    {
        var sb = new StringBuilder();

        // 仅注入全群可见消息：定向 / 私密消息不进入智能体上下文窗口（防私密内容泄漏）；按话题过滤（会话历史以话题为单位）
        var history = _hub.Value.Store.RecentMessages(context.GroupId, ContextWindowMessages, context.TopicId)
            .Where(m => !m.Recalled && m.MessageId != context.TriggerMessageId && !string.IsNullOrWhiteSpace(m.Content)
                && m.Visibility == MessageVisibility.All)
            .ToList();
        if (history.Count > 0)
        {
            // 群历史消息是用户输入，可能含恶意指令（prompt injection）：整段包上不可信边界
            var block = new StringBuilder();
            block.AppendLine("以下是群最近对话：");
            foreach (var m in history)
            {
                var who = string.IsNullOrWhiteSpace(m.SenderNickname) ? m.SenderId : m.SenderNickname;
                var text = m.Content.Length > MaxContextCharsPerMessage ? m.Content[..MaxContextCharsPerMessage] : m.Content;
                block.AppendLine($"{who}：{text}");
            }
            sb.Append(UntrustedBoundary.Wrap(block.ToString())).AppendLine();
        }

        sb.Append(context.Content);
        await AppendAttachmentsAsync(sb, context, ct);
        return sb.ToString();
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
        var visible = history.Where(m => !m.Recalled && m.MessageId != context.TriggerMessageId
            && !string.IsNullOrWhiteSpace(m.Content) && m.Visibility == MessageVisibility.All).ToList();
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
        sb.Append(context.Content);
        await AppendAttachmentsAsync(sb, context, ct);
        return sb.ToString();
    }

    /// <summary>把可提取文本的附件内联到消息文本（docx/xlsx/pptx/pdf 等），其余携带元数据。</summary>
    private async Task AppendAttachmentsAsync(StringBuilder sb, AgentInvocationContext context, CancellationToken ct)
    {
        if (context.Attachments is not { Count: > 0 } attachments || _attachmentStore is null)
            return;
        var injected = 0;
        foreach (var att in attachments)
        {
            if (AttachmentStore.IsExtractable(att))
            {
                var text = await _attachmentStore.TryReadTextAsync(att.AttachmentId, ct);
                if (text is not null && injected < AttachmentStore.MaxTextCharsTotal)
                {
                    var take = Math.Min(text.Length, AttachmentStore.MaxTextCharsTotal - injected);
                    // 附件文本可能含恶意指令（prompt injection）：内联内容包上不可信边界
                    sb.Append($"\n\n【附件：{att.Name}】\n").Append(UntrustedBoundary.Wrap(text[..take]));
                    injected += take;
                    continue;
                }
            }
            sb.Append($"\n\n【附件：{att.Name}】（{att.Kind}，{AgentGatewayHelpers.FormatBytes(att.Size)}，{att.Url}）");
        }
    }

    /// <summary>
    /// 语境发言决策（Contextual 模式）：把群最近消息作为上下文交给模型，
    /// 让它判断是否需要由该智能体发言。不 @、无关键词也可按语境主动发言。
    /// </summary>
    private async Task<bool> ShouldSpeakAsync(AgentInvocationContext context, AgentDefinition def, CancellationToken ct)
    {
        // 轻量决策：用裸 ChatClientAgent（无工具 / 无记忆注入 / 无审批包装）——语境决策不需要业务能力，
        // 避免双重工具 / 记忆注入（决策轮与正式回复轮各注入一次记忆、重复挂载工具浪费上下文）
        // 按被评估的智能体（def）构建决策体，而非总是宿主：委派链上每层用各自的身份判断语境。
        var agent = _catalog.CreateBare(def.AgentId);
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

        var runOptions = new ChatClientAgentRunOptions
        {
            ChatOptions = new ChatOptions { MaxOutputTokens = 8 },
        };
        var response = await agent.RunAsync(prompt, session: null, runOptions, ct);
        var decision = response.Text?.Trim() ?? "";
        _logger.LogDebug("智能体 {AgentId} 语境决策：{Decision}", context.AgentId, decision);
        return decision.StartsWith("YES", StringComparison.OrdinalIgnoreCase);
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
        if (_sessionLocks.Count >= SessionLockMaxEntries)
        {
            // 兜底：条目超限时即时清理超时锁；常态清理由 60s 定时器完成（不依赖 Count）
            foreach (var kv in _sessionLocks)
            {
                if (now - kv.Value.LastUsedMs > SessionLockTtlMs)
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
    public async Task<bool> ResolveInteractionAsync(string interruptId, string memberId, bool approved, string? input, JsonElement? payload, CancellationToken ct, bool approveAll = false, string? toolResult = null)
    {
        // 入口先做一次周期清理：定时器兜底外，决策前把已超时的交互先清掉，避免继续处理过期请求
        await PurgeExpiredInteractions();

        // 先校验存在性与触发者身份（仅触发者可决策），确认后才移除——非触发者的调用不会消耗请求
        if (!_pendingInteractions.TryGetValue(interruptId, out var pending))
            return false; // 不存在 / 已过期
        if (!string.Equals(pending.TargetMemberId, memberId, StringComparison.Ordinal))
            return false; // 仅触发者可决策（群聊其他用户无权交互）
        if (!_pendingInteractions.TryRemove(interruptId, out pending))
            return false; // 并发下已被决策

        // 批量批准：用户对本次运行选择「批准本次运行后续全部操作」→ 记录 runId，恢复后后续审批自动放行
        if (approveAll && approved && !string.IsNullOrEmpty(pending.RunId))
        {
            _autoApprovedRuns[pending.RunId] = 0;
            _logger.LogInformation("启用批量批准：run={RunId} by={Member}", pending.RunId, memberId);
        }

        // 多轮审批防护：外部服务异常时可能恢复后反复中断——超过最大轮数则终止运行（结束消息 + 广播错误）
        if (pending.ResumeCount >= MaxInteractionRounds)
        {
            _logger.LogWarning("交互恢复超过最大轮数（{Max}），终止运行：interrupt={InterruptId}", MaxInteractionRounds, interruptId);
            _ = SafeEndAsync(pending.Context, pending.MessageId);
            await _hub.Value.BroadcastAsync(pending.GroupId, new RunErrorEvent
            {
                GroupId = pending.GroupId,
                ErrorCode = "AGENT_INTERACTION_LIMIT",
                Message = $"智能体审批交互超过最大轮数（{MaxInteractionRounds}），运行已终止，请重新发起消息",
                Timestamp = _hub.Value.NowMs,
            }, ct: CancellationToken.None);
            if (pending.BridgeClient is not null) await pending.BridgeClient.DisposeAsync();
            return true;
        }

        _logger.LogInformation("交互决策：interrupt={InterruptId} member={Member} approved={Approved} hasToolResult={HasToolResult} toolResultLen={ToolResultLen}",
            interruptId, memberId, approved, !string.IsNullOrEmpty(toolResult), toolResult?.Length ?? 0);
        ClientToolTrace.Write($"RESOLVE interrupt={interruptId} member={memberId} approved={approved} hasToolResult={!string.IsNullOrEmpty(toolResult)} toolResultLen={toolResult?.Length ?? 0} agent={pending.Context.AgentId}");
        // 恢复任务与 HTTP 请求生命周期解耦：独立 5 分钟超时 CTS（请求断开 / 前端超时不影响恢复执行，
        // 避免恢复任务在 WaitAsync / 流式消费中被请求取消令牌中断）。
        // 注意：不能在方法末尾 using 释放——后台任务仍在使用该令牌，须等任务结束后再释放。
        var resumeCts = new CancellationTokenSource(TimeSpan.FromMinutes(StreamTimeoutMinutes));
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
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(StreamTimeoutMinutes));
        var runCt = timeoutCts.Token;
        var sessionLock = GetSessionLock(pending.Context.ThreadId);
        var acquired = false;
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
                if (resumeRounds > MaxInteractionRounds)
                {
                    _logger.LogWarning("交互恢复超过最大轮数（{Max}），终止运行：run={RunId}", MaxInteractionRounds, runId);
                    await SafeEndAsync(pending.Context, messageId);
                    await _hub.Value.BroadcastAsync(pending.GroupId, new RunErrorEvent
                    {
                        GroupId = pending.GroupId,
                        ErrorCode = "AGENT_INTERACTION_LIMIT",
                        Message = $"智能体审批交互超过最大轮数（{MaxInteractionRounds}），运行已终止，请重新发起消息",
                        Timestamp = _hub.Value.NowMs,
                    }, ct: CancellationToken.None);
                    return;
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
                    BridgeClient: null, ResumeCount: pending.ResumeCount + resumeRounds);
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
            await AttachPublishedProductsAsync(pending.GroupId, messageId, accumulated, runCt);
            await _hub.Value.EndAgentMessageAsync(pending.GroupId, messageId, runCt);
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
            // 客户端执行技能：前端已在本地执行并回传结果 → 以一句明确的 User 消息注入模型，让其「拿到结果继续作答」
            _logger.LogInformation("客户端技能恢复：注入前端执行结果 tool={Tool} agent={Agent} resultLen={Len}", fc!.Name, pending.Context.AgentId, toolResult.Length);
            ClientToolTrace.Write($"INJECT-RESULT tool={fc!.Name} agent={pending.Context.AgentId} resultLen={toolResult.Length} first= {toolResult.Substring(0, Math.Min(80, toolResult.Length))}");
            msgs.Add(new ChatMessage(ChatRole.User,
                $"[前端工具] {fc.Name} 已在客户端执行完毕，请直接引用它的结果作答：\n{toolResult}\n（答完即可，无需再调用该工具）"));
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
                if (now - kv.Value.CreatedAtMs > InteractionTtlMs && _pendingInteractions.TryRemove(kv.Key, out var pending))
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
        try { await _hub.Value.EndAgentMessageAsync(context.GroupId, messageId, CancellationToken.None); }
        catch (Exception ex) { _logger.LogDebug(ex, "结束智能体消息失败：{MessageId}", messageId); }
    }

    /// <summary>
    /// 从流式文本帧中计算增量：
    /// 累计文本（后续帧以全部已输出文本为前缀）→ 取新增部分；
    /// 增量片段（各帧互不重叠）→ 整体作为 delta。
    /// </summary>
    internal static string ComputeTextDelta(string accumulated, string text)
        => text.StartsWith(accumulated, StringComparison.Ordinal) ? text[accumulated.Length..] : text;
}
