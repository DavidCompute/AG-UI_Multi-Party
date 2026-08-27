using System.Text;
using AguiGroupChat.Hub.Agents;
using AguiGroupChat.Hub.Messaging;
using AguiGroupChat.Hub.Models;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;

namespace AguiGroupChat.Agents;

/// <summary>
/// MSAGENT 标准 <see cref="AIContextProvider"/>：在每次 agent run 前（InvokingAsync）
/// 按语义相似度检索群记忆（RAG）与触发者个人记忆，作为 Instructions 注入上下文。
///
/// 与 Microsoft Agent Framework 的「内存」抽象对齐（官方文档 Memory &amp; Persistence）：
/// <list type="bullet">
///   <item>长期记忆通过 ContextProvider 在 run 生命周期内读写（本实现为只读注入）；</item>
///   <item>写入侧保持 GroupHub 群消息钩子（用户消息不经过 agent run，无法在 after_run 捕获，
///         故写入必须发生在 run 之外——这是群聊场景的架构事实）；</item>
///   <item>当前 run 的业务上下文（群 / 触发者 / 触发消息）经 <see cref="AgentGateway.AmbientContext"/>
///         （AsyncLocal）传递，与 MSAGENT 内部 AgentRunContext 的 ambient 机制同构。</item>
/// </list>
/// </summary>
public sealed class MemoryContextProvider : AIContextProvider
{
    private readonly IMessageMemory? _memory;
    private readonly IGraphMemory? _graph;
    private readonly AgentOptions _options;
    private readonly ILogger<MemoryContextProvider> _logger;
    private readonly Lazy<GroupHub> _hub;
    private readonly Lazy<AgentCatalog> _catalog;
    private readonly Lazy<KnowledgeBaseCatalog?> _kbCatalog;

    public MemoryContextProvider(
        AgentOptions options,
        IServiceProvider services,
        ILogger<MemoryContextProvider> logger,
        IMessageMemory? memory = null,
        IGraphMemory? graph = null)
        : base(msgs => msgs, msgs => msgs, msgs => msgs) // 不做输入/存储消息过滤（记忆仅经 Instructions 注入）
    {
        _options = options;
        _logger = logger;
        _memory = memory;
        _graph = graph;
        _hub = new Lazy<GroupHub>(() => services.GetService(typeof(GroupHub)) as GroupHub
            ?? throw new InvalidOperationException("GroupHub 未注册（记忆检索需要群数据访问）"));
        _catalog = new Lazy<AgentCatalog>(() => services.GetService(typeof(AgentCatalog)) as AgentCatalog
            ?? throw new InvalidOperationException("AgentCatalog 未注册（个人记忆需要智能体设置）"));
        _kbCatalog = new Lazy<KnowledgeBaseCatalog?>(() => services.GetService(typeof(KnowledgeBaseCatalog)) as KnowledgeBaseCatalog);
    }

    public override IReadOnlyList<string> StateKeys => [];

    /// <summary>run 前检索并注入记忆（MSAGENT AIContextProvider 注入点：<c>ProvideAIContextAsync</c>）。
    /// 返回的 AIContext.Instructions 追加到系统提示（记忆位于 instructions 尾部、用户消息之前）。</summary>
    protected override async ValueTask<AIContext> ProvideAIContextAsync(InvokingContext context, CancellationToken ct)
    {
        var run = AgentGateway.AmbientContext.Value;
        var aiContext = new AIContext();

        // 无业务上下文（run 由外部直接驱动）时不注入，完全透明
        if (run is null) return aiContext;

        try
        {
            var sb = new StringBuilder();

            // 检索 query（触发消息）统一按 MaxQueryChars 截断：群记忆 / 个人记忆 / 知识库同一长度，
            // 超长文本先截断再向量化（与 AgentMessageMemory 内部的截断一致，避免超长输入打爆 embedding）
            var maxQueryChars = Math.Max(1, _options.Memory.MaxQueryChars);
            var query = run.Content.Length > maxQueryChars ? run.Content[..maxQueryChars] : run.Content;

            // 群记忆（RAG）与个人记忆：依赖 IMessageMemory（需启用语义记忆）
            if (_memory is not null)
            {
                // 群记忆（RAG）：按触发消息语义检索长期历史（默认覆盖该智能体所在的所有群）
                IReadOnlyList<MessageMemoryHit> memories = [];
                try { memories = await _memory.SearchAsync(run.GroupId, run.AgentId, query, ct); }
                catch (Exception ex) { _logger.LogDebug(ex, "语义记忆检索异常"); }
                memories = Dedupe(memories, run.TriggerMessageId, Math.Max(1, _options.Memory.TopK));
                var memorySection = BuildMemorySection(memories, _options.Memory.MaxCharsPerMemory);
                if (memorySection.Length > 0)
                {
                    _logger.LogInformation("智能体 {AgentId} 回复前注入 {Count} 条历史记忆（group={GroupId}）", run.AgentId, memories.Count, run.GroupId);
                    sb.Append(memorySection).AppendLine();
                }

                // 个人记忆：需全局能力（PersonalTopK>0）+ 智能体开启 + 触发者用户开启（隐私），三重条件
                if (_options.Memory.PersonalTopK > 0
                    && _catalog.Value.GetDefinition(run.AgentId)?.PersonalMemoryEnabled == true
                    && _hub.Value.IsPersonalMemoryEnabled(run.TriggerUserId))
                {
                    IReadOnlyList<MessageMemoryHit> personal = [];
                    try { personal = await _memory.SearchPersonAsync(run.TriggerUserId, run.GroupId, query, ct); }
                    catch (Exception ex) { _logger.LogDebug(ex, "个人记忆检索异常"); }
                    personal = Dedupe(personal, run.TriggerMessageId, Math.Max(1, _options.Memory.PersonalTopK));
                    var personSection = BuildPersonSection(run.TriggerUserId, personal, _options.Memory.MaxCharsPerMemory);
                    if (personSection.Length > 0)
                    {
                        _logger.LogInformation("智能体 {AgentId} 回复前注入 {Count} 条个人记忆（person={PersonId}）", run.AgentId, personal.Count, run.TriggerUserId);
                        sb.Append(personSection).AppendLine();
                    }
                }
            }

            // 知识库（RAG）：智能体绑定的知识文档，回复前按触发消息检索相关片段（独立于群记忆开关）
            if (_kbCatalog.Value is { } kbCatalog
                && _catalog.Value.GetDefinition(run.AgentId)?.KnowledgeBaseIds is { Count: > 0 } kbIds)
            {
                IReadOnlyList<KnowledgeBaseCatalog.KbHit> kbHits = [];
                try
                {
                    kbHits = await kbCatalog.SearchAsync(kbIds, query, _options.Memory.TopK, _options.Memory.MinScore, ct);
                }
                catch (Exception ex) { _logger.LogDebug(ex, "知识库检索异常"); }
                var kbSection = BuildKbSection(kbHits, _options.Memory.MaxCharsPerMemory);
                if (kbSection.Length > 0)
                {
                    _logger.LogInformation("智能体 {AgentId} 回复前注入 {Count} 条知识库片段（kbs={Kbs}）", run.AgentId, kbHits.Count, string.Join(",", kbIds));
                    sb.Append(kbSection).AppendLine();
                }

                // 知识库图谱（Graph RAG，启用时）：对绑定知识库的图谱隔离域做种子召回 + 图遍历，注入子图补强
                if (_options.Memory.GraphEnabled && _graph is not null)
                {
                    try
                    {
                        var kbSub = await kbCatalog.SearchGraphAsync(kbIds, query, _options.Memory.GraphTopK, _options.Memory.GraphMinScore, _options.Memory.GraphHops, _options.Memory.GraphMaxNodes, ct);
                        var kbGraphSection = BuildKbGraphSection(kbSub, _options.Memory.GraphMaxSectionChars);
                        if (kbGraphSection.Length > 0)
                        {
                            _logger.LogInformation("智能体 {AgentId} 回复前注入知识库图谱子图（kbs={Kbs}，实体{Entities} 边{Edges}）",
                                run.AgentId, string.Join(",", kbIds), kbSub.Entities.Count, kbSub.Edges.Count);
                            sb.Append(kbGraphSection).AppendLine();
                        }
                    }
                    catch (Exception ex) { _logger.LogDebug(ex, "知识库图谱检索注入异常（已跳过）"); }
                }
            }

            // 图谱记忆（Graph RAG）：按语义召回种子实体 + n 跳图遍历，把命中子图注入（补强关系型知识）
            if (_graph is not null && _options.Memory.GraphEnabled)
            {
                try
                {
                    var sub = await _graph.SearchAsync(run.GroupId, query, ct);
                    var graphSection = BuildGraphSection(sub, _options.Memory.GraphMaxSectionChars);
                    if (graphSection.Length > 0)
                    {
                        _logger.LogInformation("智能体 {AgentId} 回复前注入图谱子图（group={GroupId}，实体{Entities} 边{Edges}）",
                            run.AgentId, run.GroupId, sub.Entities.Count, sub.Edges.Count);
                        sb.Append(graphSection).AppendLine();
                    }
                }
                catch (Exception ex) { _logger.LogDebug(ex, "图谱检索注入异常（已跳过）"); }
            }

            if (sb.Length > 0) aiContext.Instructions = sb.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "记忆上下文注入失败（已跳过，不影响本轮回复）");
        }
        return aiContext;
    }

    /// <summary>排除触发消息自身 + 按内容去重（记忆嵌套 / 重复只保留最高分）+ TopK 硬限制。</summary>
    internal static List<MessageMemoryHit> Dedupe(IReadOnlyList<MessageMemoryHit> hits, string triggerMessageId, int topK)
        => hits
            .Where(m => m.MessageId != triggerMessageId)
            .GroupBy(m => m.Content.Trim(), StringComparer.Ordinal)
            .Select(g => g.OrderByDescending(m => m.Score).First())
            .OrderByDescending(m => m.Score)
            .Take(topK)
            .ToList();

    /// <summary>把检索命中的历史记忆排版为 prompt 段落（无命中返回空串）。供测试直接调用。
    /// 记忆内容来自历史消息（可能是用户输入，含 prompt injection 风险）：整段包上不可信边界。</summary>
    internal static string BuildMemorySection(IReadOnlyList<MessageMemoryHit> hits, int maxCharsPerMemory)
    {
        if (hits.Count == 0) return "";
        var sb = new StringBuilder();
        sb.AppendLine("以下是相关历史记忆（可按需引用，不要重复回答已经确认过的内容）：");
        foreach (var m in hits)
        {
            var text = m.Content.Length > maxCharsPerMemory ? m.Content[..maxCharsPerMemory] : m.Content;
            var time = DateTimeOffset.FromUnixTimeMilliseconds(m.Timestamp).ToLocalTime().ToString("MM-dd HH:mm");
            sb.AppendLine($"[{time} · {m.SenderId} · 相似度{m.Score:0.00}] {text}");
        }
        return UntrustedBoundary.Wrap(sb.ToString());
    }

    /// <summary>把检索命中的个人记忆排版为 prompt 段落（无命中返回空串）。供测试直接调用。
    /// 个人记忆来自用户历史发言（可能含恶意指令）：整段包上不可信边界。</summary>
    internal static string BuildPersonSection(string personId, IReadOnlyList<MessageMemoryHit> hits, int maxCharsPerMemory)
    {
        if (hits.Count == 0) return "";
        var sb = new StringBuilder();
        sb.AppendLine($"以下是 {personId} 的个人记忆（TA 在其他对话中说过的相关历史发言，可据此了解 TA 的偏好与立场，仅在相关时引用）：");
        foreach (var m in hits)
        {
            var text = m.Content.Length > maxCharsPerMemory ? m.Content[..maxCharsPerMemory] : m.Content;
            var time = DateTimeOffset.FromUnixTimeMilliseconds(m.Timestamp).ToLocalTime().ToString("MM-dd HH:mm");
            sb.AppendLine($"[{time} · 相似度{m.Score:0.00}] {text}");
        }
        return UntrustedBoundary.Wrap(sb.ToString());
    }

    /// <summary>把检索命中的知识库片段排版为 prompt 段落（无命中返回空串）。供测试直接调用。
    /// 知识库文档为上传内容（可能含恶意指令）：整段包上不可信边界。</summary>
    internal static string BuildKbSection(IReadOnlyList<KnowledgeBaseCatalog.KbHit> hits, int maxCharsPerKb)
    {
        if (hits.Count == 0) return "";
        var sb = new StringBuilder();
        sb.AppendLine("以下是知识库检索结果（来自用户上传的知识文档，回答时应优先基于这些资料并注明出处文档）：");
        foreach (var h in hits)
        {
            var text = h.Content.Length > maxCharsPerKb ? h.Content[..maxCharsPerKb] : h.Content;
            sb.AppendLine($"[知识库 {h.KbName} · 文档 {h.FileName} · 相似度{h.Score:0.00}] {text}");
        }
        return UntrustedBoundary.Wrap(sb.ToString());
    }

    /// <summary>把图谱检索命中的子图（实体 + 边）排版为 prompt 段落（无命中返回空串）。
    /// 图谱实体/关系来自历史消息（可能含恶意指令）：整段包上不可信边界。
    /// 图谱是补强：实体与边受 <paramref name="maxSectionChars"/> 总字符预算约束，先排置信度更高的种子/近层实体
    /// 与连接这些实体的边，超过预算的部分丢弃，避免挤占向量切片。</summary>
    internal static string BuildGraphSection(GraphSubgraph sub, int maxSectionChars)
    {
        if (sub.IsEmpty) return "";
        const string intro =
            "以下是相关实体知识图谱子图，仅作参考：用于补强主体间关系；涉及具体事实/数据时，以其他记忆或知识库原文为准。";
        var (entities, edges) = RenderWithinBudget(sub, maxSectionChars);
        var sb = new StringBuilder();
        sb.AppendLine(intro);
        sb.AppendLine($"[实体] " + string.Join("，", entities));
        if (edges.Length > 0)
        {
            sb.AppendLine("[关系（重点）]");
            foreach (var e in edges)
                sb.AppendLine("  " + e);
        }
        return UntrustedBoundary.Wrap(sb.ToString());
    }

    /// <summary>把知识库图谱子图排版为 prompt 段落（无命中返回空串）。
    /// 知识库图谱来自上传文档（可能含恶意指令）：整段包上不可信边界。
    /// 同样受总字符预算约束，且首行明示以切片原文为准。</summary>
    internal static string BuildKbGraphSection(GraphSubgraph sub, int maxSectionChars)
    {
        if (sub.IsEmpty) return "";
        const string intro =
            "以下是知识库文档中的实体关系图谱，仅作参考、用于理解文档主体间的关系；回答具体事实时，以知识库切片原文为准。";
        var (entities, edges) = RenderWithinBudget(sub, maxSectionChars);
        var sb = new StringBuilder();
        sb.AppendLine(intro);
        sb.AppendLine($"[实体] " + string.Join("，", entities));
        if (edges.Length > 0)
        {
            sb.AppendLine("[关系（重点）]");
            foreach (var e in edges)
                sb.AppendLine("  " + e);
        }
        return UntrustedBoundary.Wrap(sb.ToString());
    }

    /// <summary>在 <paramref name="maxChars"/> 预算内渲染子图：实体按相关性（种子/近层在前）排序且优先保留，
    /// 关系边只保留「起止点都在已保留实体集合内」的高价值边并优先；返回渲染后的实体名列表与关系行列表。
    /// 预算优先给实体，关系边占剩余预算（边信息密度低于实体名录，噪声也主要来自边）。</summary>
    private static (string[] Entities, string[] Edges) RenderWithinBudget(GraphSubgraph sub, int maxChars)
    {
        if (sub.IsEmpty || maxChars <= 0) return ([], []);
        // 实体排序：Hop 升序（种子=0 在前）→ 分数降序 → 名字稳定排序
        var orderedEntities = sub.Entities
            .OrderBy(e => e.Hop)
            .ThenByDescending(e => e.Score)
            .ThenBy(e => e.Name, StringComparer.Ordinal)
            .Take(Math.Max(1, sub.Entities.Count));

        // 关系边只保留两端都在已保留实体中的，且优先种子/near 实体间的边；按权重降序
        var keptIds = new HashSet<string>(orderedEntities.Select(e => e.EntityId), StringComparer.Ordinal);
        var orderedEdges = sub.Edges
            .Where(e => keptIds.Contains(e.SourceId) && keptIds.Contains(e.TargetId))
            .OrderByDescending(e => e.Weight)
            .ThenBy(e => e.Relation, StringComparer.Ordinal);

        var entities = new List<string>();
        var edges = new List<string>();
        var used = 0; // 预算针对可选内容（实体名录 + 关系边）；固定引导语不计入，因其很短且属必要框架
        foreach (var e in orderedEntities)
        {
            var label = "「" + e.Name + "」"
                + (e.Type is not ("" or null) && e.Type != "Concept" ? $"（{e.Type}）" : "");
            var cost = label.Length + 2; // 逗号/分隔
            if (used + cost > maxChars) continue;
            entities.Add(label);
            used += cost;
        }
        foreach (var edge in orderedEdges)
        {
            var line = $"  {edge.SourceName} ——[{edge.Relation}]→ {edge.TargetName}";
            var cost = line.Length + 1;
            if (used + cost > maxChars) break;
            edges.Add(line);
            used += cost;
        }
        return (entities.ToArray(), edges.ToArray());
    }
}
