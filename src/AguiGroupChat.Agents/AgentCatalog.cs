using System.ClientModel;
using System.Collections.Concurrent;
using System.ComponentModel;
using AguiGroupChat.Agents.Tools;
using AguiGroupChat.Hub.Persistence;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OpenAI;
using OpenAI.Chat;
using OpenAI.Internal;

namespace AguiGroupChat.Agents;

/// <summary>
/// 智能体目录：启动时以 appsettings（AgentOptions.Agents）为种子，
/// 运行时可经 Upsert / Remove 动态新增、修改、删除智能体（Web 管理界面），
/// 变更通知持久化，启动时可由持久化快照整体恢复。
/// 按 agentId 索引，构建 Microsoft Agent Framework 的 ChatClientAgent。
/// Provider=mock 使用内置模拟客户端；否则走 OpenAI 兼容端点。
/// </summary>
public sealed class AgentCatalog
{
    /// <summary>DeepSeek 官方 OpenAI 兼容端点与默认模型。</summary>
    internal const string DeepSeekEndpoint = "https://api.deepseek.com";
    internal const string DeepSeekDefaultModel = "deepseek-chat";
    /// <summary>思考模式下 DeepSeek 使用的推理模型。</summary>
    internal const string DeepSeekReasonerModel = "deepseek-reasoner";
    /// <summary>DeepSeek 视觉（图片理解）模型（需显式指定；deepseek-chat 不支持图片）。</summary>
    internal const string DeepSeekVisionModel = "deepseek-v4-flash-vision-exp";

    /// <summary>“结构化/格式型”生成任务的模型选择：是否需要“思考”（用推理模型）。
    /// 任务输出若会被机器按固定 JSON 或长度截取的短 token 解析，属格式任务——
    /// 始终用常规对话模型（deepseek-chat/schema-fast）。与 <see cref="GenComplexity"/> 配合：
    /// 复杂度仅决定是否在提示词里让它“先想后给”（见 <see cref="DeliberateFirstLine"/>），
    /// 而不是真的切开 reasoner（reasoner 在严格 JSON/截短输出上又慢又易空/超时）。
    /// 返回 null 表示“跟随全局思考模式”，供需要开放推理/创造的生成（如角色人设）不传 override。</summary>
    internal static string? StructuredFastModel(bool isDeepSeek)
        => isDeepSeek ? DeepSeekDefaultModel : null;

    /// <summary>生成任务的复杂度分档（决定是否“先想后给”再产出最终结构）。
    /// Simple：一句话/单一短 token（群名、示例入参、图谱抽等）——直接给格式答案，不做多余心理铺陈；
    /// Medium：单一技能/代码等稍依赖设计权衡；
    /// Complex：方案性强的 JSON（一键编排：岗位+技能+连接分工）——要求先把取舍/思路简述再给最终 JSON。
    /// 分档只影响“提示词措辞”，不影响模型选择（结构化一律 fast，避免 reasoner 的慢/空）。</summary>
    internal enum GenComplexity { Simple, Medium, Complex }

    /// <summary>对复杂(需要先权衡）的结构化生成，附加的“先想后给”要求：在正文开头简述取舍，最终才给 JSON。</summary>
    internal const string DeliberateFirstLine =
        "请先看到需求后，用一到两句中文简述你在方案上的关键取舍（仅供我参考，不会写入保存结果）；" +
        "随后在最后单独用一段输出唯一一份最终 JSON。你的最终 JSON 是最后一个以 { 开头、} 结尾的代码块。\n\n";

    private readonly AgentOptions _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IServiceProvider _services;
    private readonly ChangeHub? _changes;
    private readonly MemoryContextProvider? _memoryContext;
    private readonly ILogger<AgentCatalog> _logger;
    // 模型用量统计（可选：注册了 AgentUsageService 才包装 usage 捕获）
    private readonly Lazy<AguiGroupChat.Hub.Agents.AgentUsageService?> _usage;
    private readonly ConcurrentDictionary<string, AgentDefinition> _definitions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ChatClientAgent> _agents = new(StringComparer.Ordinal);
    // agentId → 已挂载工具名（创建时填充；测试 / 调试 / 前端展示用）
    private readonly ConcurrentDictionary<string, IReadOnlyList<string>> _agentToolNames = new(StringComparer.Ordinal);
    // agentId → 已挂载且需要人机交互审批的工具名（创建时填充；差异化审批策略测试 / 调试用）
    private readonly ConcurrentDictionary<string, IReadOnlyList<string>> _agentApprovalToolNames = new(StringComparer.Ordinal);
    // agentId → 已挂载的「客户端执行」技能名（ExecutionLocation=Client；网关识别后中断下发前端执行，不在服务端跑）
    private readonly ConcurrentDictionary<string, IReadOnlyList<string>> _agentClientToolNames = new(StringComparer.Ordinal);
    // 技能库与技能执行器（惰性解析：技能提供者为可选项）
    private readonly Lazy<AgentSkillCatalog?> _skillCatalog;
    private readonly Lazy<SkillRunner?> _skillRunner;

    public AgentCatalog(AgentOptions options, ILoggerFactory loggerFactory, IServiceProvider services, ChangeHub? changes = null, MemoryContextProvider? memoryContext = null)
    {
        _options = options;
        _loggerFactory = loggerFactory;
        _services = services;
        _changes = changes;
        _memoryContext = memoryContext;
        _logger = loggerFactory.CreateLogger<AgentCatalog>();
        _usage = new Lazy<AguiGroupChat.Hub.Agents.AgentUsageService?>(() =>
            services.GetService(typeof(AguiGroupChat.Hub.Agents.AgentUsageService)) as AguiGroupChat.Hub.Agents.AgentUsageService);
        // 优先宿主环境内容根（Web/桌面），回退当前工作目录（测试 / 独立运行）
        var contentRoot = _services.GetService(typeof(Microsoft.AspNetCore.Hosting.IWebHostEnvironment)) is Microsoft.AspNetCore.Hosting.IWebHostEnvironment env
            ? env.ContentRootPath
            : Directory.GetCurrentDirectory();
        foreach (var def in options.Agents) _definitions.TryAdd(def.AgentId, def);
        // 技能库 + 技能执行沙箱（data/skillruns）：沿用工作区根解析（data/workspaces 之上一级 /skillruns）
        _skillCatalog = new Lazy<AgentSkillCatalog?>(() => _services.GetService(typeof(AgentSkillCatalog)) as AgentSkillCatalog);
        var skillRunRoot = Directory.GetParent(
            Path.GetFullPath(Path.IsPathRooted(_options.WorkSpaceRoot) ? _options.WorkSpaceRoot : Path.Combine(contentRoot, _options.WorkSpaceRoot)))
            is { } p
            ? Path.Combine(p.FullName, "skillruns")
            : Path.Combine(contentRoot, "data", "skillruns");
        _skillRunner = new Lazy<SkillRunner?>(() => new SkillRunner(skillRunRoot, _loggerFactory, allowPrivateEndpoints: _options.AllowPrivateSkillEndpoints));
    }

    public AgentDefinition? GetDefinition(string agentId)
        => _definitions.TryGetValue(agentId, out var def) ? def : null;

    /// <summary>新增或更新智能体定义；变更会失效已缓存的 ChatClientAgent，下次触发按新人设重建。</summary>
    public void Upsert(AgentDefinition def)
    {
        _definitions[def.AgentId] = def;
        _agents.TryRemove(def.AgentId, out _);
        _agentToolNames.TryRemove(def.AgentId, out _);
        _agentApprovalToolNames.TryRemove(def.AgentId, out _);
        _changes?.Notify();
    }

    /// <summary>删除智能体定义并失效缓存。返回是否存在。</summary>
    public bool Remove(string agentId)
    {
        _agents.TryRemove(agentId, out _);
        _agentToolNames.TryRemove(agentId, out _);
        _agentApprovalToolNames.TryRemove(agentId, out _);
        var ok = _definitions.TryRemove(agentId, out _);
        if (ok) _changes?.Notify();
        return ok;
    }

    /// <summary>
    /// 技能库 API 测试运行：按技能定义执行一次（shell / http / prompt），返回结果文本。
    /// 供管理界面试运行 / 调试验证技能定义；失败返回错误文本不抛（与技能运行时一致）。
    /// </summary>
    public Task<string> RunSkillAsync(AgentSkillDefinition skill, string query, CancellationToken ct = default)
        => _skillRunner.Value is { } runner ? runner.InvokeAsync(skill, query, ct) : Task.FromResult("技能执行器不可用。");

    /// <summary>清空并整体恢复智能体定义（启动恢复用）：<b>常驻配置智能体（appsettings Agents:Agents）始终保留</b>，
    /// 持久化快照中的运行时定义按 agentId 覆盖（同 ID 以运行时的为准），不触发脏标记。</summary>
    public void RestoreAll(IEnumerable<AgentDefinition> definitions)
    {
        _agents.Clear();
        _agentToolNames.Clear();
        _agentApprovalToolNames.Clear();
        // 配置声明的智能体是应用基线，不能被持久化快照整体替换（快照缺失时配置智能体不应丢失）
        foreach (var def in _options.Agents) _definitions.TryAdd(def.AgentId, def);
        foreach (var def in definitions) _definitions[def.AgentId] = def;
    }

    public IReadOnlyList<AgentDefinition> ListDefinitions()
        => _definitions.Values.ToList();

    public ChatClientAgent GetOrCreate(string agentId)
        => _agents.GetOrAdd(agentId, Create);

    /// <summary>失效全部已缓存 ChatClientAgent（系统初始化 / 全局模型配置变更后调用：下次触发按新配置重建）。</summary>
    public void InvalidateAll()
    {
        _agents.Clear();
        _agentToolNames.Clear();
        _agentApprovalToolNames.Clear();
        _changes?.Notify();
    }

    /// <summary>
    /// 创建裸 ChatClientAgent（不缓存）：只挂 Instructions / Description，不带工具、不带 AIContextProviders（记忆注入）、
    /// 不带技能与审批包装。用于轻量决策（如 Contextual 模式 <c>__AGUI_DECIDE__</c> 发言判断）——
    /// 决策轮不需要业务能力，避免双重工具 / 记忆注入浪费上下文。
    /// </summary>
    public ChatClientAgent CreateBare(string agentId)
    {
        var def = GetDefinition(agentId)
            ?? throw new InvalidOperationException($"智能体 {agentId} 未在 Agents 配置中声明");
        var chatOptions = new ChatClientAgentOptions
        {
            Name = def.Nickname,
            Description = def.Description,
            ChatOptions = new Microsoft.Extensions.AI.ChatOptions
            {
                Instructions = def.Instructions,
                Tools = null,
            },
            AIContextProviders = [],
        };

        if (string.Equals(_options.Provider, "mock", StringComparison.OrdinalIgnoreCase))
        {
            return new ChatClientAgent(new MockChatClient(def, enableTools: false), chatOptions, _loggerFactory, _services);
        }

        var isDeepSeek = string.Equals(_options.Provider, "deepseek", StringComparison.OrdinalIgnoreCase);
        var client = BuildOpenAIChatClient(_options, def, isDeepSeek);
        return client.AsAIAgent(chatOptions, clientFactory: null, _loggerFactory, _services);
    }

    /// <summary>创建“视觉”裸 ChatClientAgent（不缓存）：用指定视觉模型 + 智能体人设，不带工具/记忆注入，
    /// 用于对含图片的消息做一次看图理解（多模态 user message 走 <c>RunStreamingAsync</c>）。
    /// mock 提供方不支持视觉，返回 null = 走普通文本。</summary>
    public ChatClientAgent? CreateBareVision(string agentId, string visionModel)
    {
        if (string.Equals(_options.Provider, "mock", StringComparison.OrdinalIgnoreCase)) return null;
        var def = GetDefinition(agentId)
            ?? throw new InvalidOperationException($"智能体 {agentId} 未在 Agents 配置中声明");
        var chatOptions = new ChatClientAgentOptions
        {
            Name = def.Nickname,
            Description = def.Description,
            ChatOptions = new Microsoft.Extensions.AI.ChatOptions
            {
                Instructions = def.Instructions,
                Tools = null,
            },
            AIContextProviders = [],
        };
        var isDeepSeek = string.Equals(_options.Provider, "deepseek", StringComparison.OrdinalIgnoreCase);
        var client = BuildOpenAIChatClient(_options, def, isDeepSeek, visionModel);
        return client.AsAIAgent(chatOptions, clientFactory: null, _loggerFactory, _services);
    }

    /// <summary>返回某智能体当前挂载的工具名列表（空 = 工具未启用）。</summary>
    public IReadOnlyList<string> GetAgentToolNames(string agentId)
    {
        GetOrCreate(agentId); // 确保缓存已填充
        return _agentToolNames.TryGetValue(agentId, out var names) ? names : [];
    }

    /// <summary>返回已填充缓存中的工具名（不强制重建智能体）。用于验证技能目标在多跳构建中
    /// 已挂载自身技能：顶层智能体构建时会递归填充其技能目标的工具名缓存。</summary>
    internal IReadOnlyList<string> GetCachedToolNames(string agentId)
        => _agentToolNames.TryGetValue(agentId, out var names) ? names : [];

    /// <summary>返回某个智能体已挂载且**需要人机交互审批**的工具名（差异化审批策略下每个智能体可不同）。
    /// 若智能体未启用工具则返回空集。</summary>
    public IReadOnlyList<string> GetAgentApprovalToolNames(string agentId)
    {
        GetOrCreate(agentId); // 确保缓存已填充
        return _agentApprovalToolNames.TryGetValue(agentId, out var names) ? names : [];
    }

    /// <summary>返回某个智能体已挂载且为**客户端执行**的技能工具名（网关据此把调用中断下发前端执行）。</summary>
    public IReadOnlyList<string> GetAgentClientToolNames(string agentId)
    {
        GetOrCreate(agentId); // 确保缓存已填充
        return _agentClientToolNames.TryGetValue(agentId, out var names) ? names : [];
    }

    public IReadOnlyList<string> AgentIds => _definitions.Keys.ToList();

    private ChatClientAgent Create(string agentId) => Create(agentId, includeSkills: true);

    /// <summary>技能链最大递归深度（防配置病态深链打爆构建 / 运行）。</summary>
    private const int MaxSkillDepth = 6;

    /// <summary>创建 ChatClientAgent。支持<b>多跳技能链</b>：技能目标（isSkillTarget=true）会继续挂载自身的技能，
    /// 使 A→B→C 逐层激活成为可能；通过 <c>building</c> 访问链在构建期破坏循环引用（A→B→A 只在首次出现处
    /// 注册目标，之后不再递归展开该目标，避免无限递归），并受 <see cref="MaxSkillDepth"/> 深度上限兜底。
    /// 技能目标同时做<b>工具隔离</b>：不挂网络 / 文件读取类工具（web_search / read_url / read_attachment）——
    /// 技能链会把宿主的人设指令交由子代理执行，若子代理可联网 / 读附件，宿主被人设注入时会把
    /// SSRF / 文件读取等能力带进技能执行（攻击面放大）；基础工具（时间 / 计算 / 换算 / 记忆检索）与审批包装保留。</summary>
    private ChatClientAgent Create(string agentId, bool includeSkills, bool isSkillTarget = false, IReadOnlySet<string>? building = null)
    {
        var def = GetDefinition(agentId)
            ?? throw new InvalidOperationException($"智能体 {agentId} 未在 Agents 配置中声明");
        // 智能体级差异化审批策略：非空则用本智能体名单，否则回退全局（AgentOptions.RequireApprovalToolNames）
        var approvalNames = def.RequireApprovalToolNames is { Count: > 0 }
            ? def.RequireApprovalToolNames
            : _options.RequireApprovalToolNames;
        var tools = _options.EnableTools ? BuildTools(approvalNames) : null;
        if (isSkillTarget && tools is not null)
            tools = tools.Where(t => t.Name is not ("web_search" or "read_url" or "read_attachment")).ToList();
        _agentToolNames[agentId] = tools?.Select(t => t.Name).ToList() ?? [];
        _agentApprovalToolNames[agentId] = tools?.OfType<ApprovalRequiredAIFunction>().Select(t => t.Name).ToList() ?? [];

        // MSAGENT 标准记忆：把记忆上下文提供者挂到 agent 管道（每次 run 前经 AIContextProvider 注入）
        var chatOptions = new ChatClientAgentOptions
        {
            Name = def.Nickname,
            Description = def.Description,
            ChatOptions = new Microsoft.Extensions.AI.ChatOptions
            {
                Instructions = def.Instructions,
                Tools = tools,
            },
            AIContextProviders = _memoryContext is null ? [] : [_memoryContext],
        };

        // MSAGENT 技能（智能体间调用）：把其他已注册智能体封装为可调用子代理（AIFunction 形式），
        // 模型需要该领域信息时经框架 AgentSession 调起目标智能体并取回其回复。
        // 支持多跳技能链：技能目标（isSkillTarget）会继续挂载自身技能，因此 A→B→C 能逐层激活。
        // 用 building 访问链在构建期破坏循环（A→B→A 在目标已出现于当前链时不再次展开），并以深度上限兑底。
        if (includeSkills && def.Skills is { Count: > 0 })
        {
            var skillTools = new List<AITool>();
            var occupied = new HashSet<string>(StringComparer.Ordinal);
            foreach (var skill in def.Skills)
            {
                if (string.IsNullOrWhiteSpace(skill.TargetAgentId)) continue;
                // 工具名须符合 OpenAI 规范 ^[a-zA-Z0-9_-]+$：中文 / 空格 / 点号等会让模型调用直接 400。
                // 留空则自动生成（skill_<目标ID>，冲突追加 _2/_3）并写回定义：快照 / 列表回显 / mock 一致
                if (string.IsNullOrWhiteSpace(skill.SkillId))
                {
                    skill.SkillId = AgentSkillConfig.GenerateSkillId(skill.TargetAgentId, occupied);
                }
                else if (!AgentSkillConfig.IsValidSkillId(skill.SkillId))
                {
                    _logger.LogWarning("智能体 {AgentId} 的技能 {SkillId} 名称不合法（仅允许字母/数字/下划线/连字符），已跳过", agentId, skill.SkillId);
                    continue;
                }
                else
                {
                    occupied.Add(skill.SkillId);
                }
                if (string.Equals(skill.TargetAgentId, agentId, StringComparison.Ordinal))
                {
                    _logger.LogWarning("智能体 {AgentId} 的技能 {SkillId} 指向自己，已跳过", agentId, skill.SkillId);
                    continue;
                }
                if (!_definitions.ContainsKey(skill.TargetAgentId))
                {
                    _logger.LogWarning("智能体 {AgentId} 的技能 {SkillId} 目标智能体不存在：{Target}（已跳过）",
                        agentId, skill.SkillId, skill.TargetAgentId);
                    continue;
                }
                try
                {
                    // 访问链：当前链（祖先） + 本智能体，作为构建期防循环与深度限制的依据
                    var childChain = new HashSet<string>(StringComparer.Ordinal);
                    if (building is not null) childChain.UnionWith(building);
                    childChain.Add(agentId);
                    // 循环引用（目标已在本链）：注册一个不带自身技能的目标，避免无限递归
                    var isCycle = childChain.Contains(skill.TargetAgentId);
                    var expandTarget = !isCycle && childChain.Count < MaxSkillDepth;
                    var target = Create(skill.TargetAgentId,
                        includeSkills: expandTarget,
                        isSkillTarget: true,
                        building: childChain);
                    var targetNick = _definitions.TryGetValue(skill.TargetAgentId, out var tDef) ? tDef.Nickname ?? "" : skill.TargetAgentId;
                    // 技能返回子智能体的 Markdown 答复：提示宿主模型原样保留（含 mermaid 代码块），
                    // 否则模型常把代码块转义 / 改写，前端无法渲染成图表
                    var desc = (skill.Description ?? "").Trim();
                    if (desc.Length > 0) desc += "；";
                    desc += "该技能返回 Markdown 文本，若其中包含以 ``` 包裹的 mermaid 代码块（如 ```mermaid ... ```），"
                        + "请在你的最终回复中原样保留该代码块（不要转义、不要省略反引号），系统会自动将其渲染为图表";
                    skillTools.Add(AIFunctionFactory.Create(
                        new AgentSkillCall(target, skill.TargetAgentId, targetNick, skill.SkillId, _loggerFactory).InvokeAsync,
                        skill.SkillId, desc));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "智能体 {AgentId} 挂载技能 {SkillId} 失败（目标 {Target}）",
                        agentId, skill.SkillId, skill.TargetAgentId);
                }
            }
            if (skillTools.Count > 0)
            {
                var chatTools = (IList<AITool>)(chatOptions.ChatOptions.Tools ??= []);
                foreach (var t in skillTools) chatTools.Add(t);
                _agentToolNames[agentId] = (_agentToolNames.TryGetValue(agentId, out var names) ? names.ToList() : [])
                    .Concat(skillTools.Select(s => s.Name)).Distinct().ToList();
            }
        }

        // 内置组织角色（挂载 org_design 方案技能的，如组织构建师/组织运营官）：追加受控落库工具 org_commit。
        // 仅平台管理员触发才真正写入；普通用户调用只会拿到“请管理员放行”的说明，绝不写库。
        if (tools is not null
            && def.SkillDefIds is { Count: > 0 }
            && def.SkillDefIds.Contains("org_design", StringComparer.OrdinalIgnoreCase))
        {
            var commitTool = new Tools.OrgCommitTool(_services, _loggerFactory);
            var commitFunc = AIFunctionFactory.Create(commitTool.Commit, "org_commit",
                "把已经磨好并得到认可的『最终组织稿』真正落库为这支组织（数字员工+技能+连接），并支持同一 teamKey 反复覆盖（库里始终只留最新一版）。" +
                "仅平台管理员（群主/超管）真正写入；普通用户调用只会收到需管理员放行的说明。" +
                "参数：teamKey（这支组织的稳定英文短钥匙）、planJson（最终稿 JSON，字段同 one-click apply：{ title, skills[], agents[], createSupportCircle? }）。" +
                "请只在方案全部得到用户一一同意之后才发起；发起前不要为无关字词频繁调用。");
            var chatT3 = (IList<AITool>)(chatOptions.ChatOptions.Tools ??= []);
            chatT3.Add(commitFunc);
            _agentToolNames[agentId] = (_agentToolNames.TryGetValue(agentId, out var names3) ? names3.ToList() : [])
                .Append(commitFunc.Name).Distinct().ToList();
        }

        // 可复用技能（OpenClaw 风格）：把技能库中本智能体引用的技能逐个封装为 AIFunction 挂上。
        // shell / http / prompt 三类都经 SkillRunner 执行；需审批的技能用 ApprovalRequiredAIFunction 包装。
        if (def.SkillDefIds is { Count: > 0 } && _skillCatalog.Value is { } skillCatalog && _skillRunner.Value is { } runner)
        {
            var defTools = new List<AITool>();
            // 排除已挂工具名，避免技能工具名与内置 / 子代理技能撞名
            var occupied = new HashSet<string>(StringComparer.Ordinal);
            if (chatOptions.ChatOptions.Tools is IEnumerable<AITool> existingTools)
                foreach (var t in existingTools) occupied.Add(t.Name);
            foreach (var refId in def.SkillDefIds)
            {
                var skill = skillCatalog.Get(refId);
                if (skill is null)
                {
                    _logger.LogWarning("智能体 {AgentId} 引用技能 {Ref} 不存在（已跳过）", agentId, refId);
                    continue;
                }
                // 工具名：库内 SkillId 本身即 ASCII 工具名；占位则在已占用名上规避
                var toolName = AgentSkillDefinition.IsValidAsciiToolId(skill.SkillId)
                    ? skill.SkillId
                    : AgentSkillDefinition.ToAsciiToolId(skill.SkillId, occupied, "skill");
                var desc = (skill.Description ?? "").Trim();
                if (desc.Length > 0) desc += "；";
                if (skill.Kind == AgentSkillKind.Shell)
                    desc += $"可执行命令/脚本（在专属沙箱运行，命令正文见技能定义，可用 $QUERY 变量读取请求）。需用户批准后执行。";
                else if (skill.Kind == AgentSkillKind.Http)
                    desc += $"调用外部 HTTP 接口（Body JSON 定义 method/url/headers/body，可用 ${{query}} 占位）。需用户批准后执行。";
                else if (skill.Kind == AgentSkillKind.Dotnet)
                    desc += (skill.ExecutionLocation == AgentSkillExecutionLocation.Client
                                ? "本机 C# 技能（由本机桥在用户机器/内网机编译执行，正文含 public static string Run(string input)）。"
                                : "服务端 C# 技能（Roslyn 动态编译受限执行，正文含 public static string Run(string input)）。")
                            + "需用户批准后执行。";
                else
                    desc += $"提示词/流程模板：无需外部执行，请结合模板与请求直接综合作答。";
                // 受控”组织落库“技能：它不是可跑命令/prompt——把它作为该数字员工的一个部署动作挂载（经唯一官方引擎落库、仅管理员写）。
                if (skill.Kind == AgentSkillKind.Org_deploy)
                {
                    var deploy = new OrgCommitTool(_services, _loggerFactory);
                    defTools.Add(AIFunctionFactory.Create(deploy.Commit, toolName, desc));
                    occupied.Add(toolName);
                    continue;
                }
                var isClientSkill = skill.ExecutionLocation == AgentSkillExecutionLocation.Client;
                // 客户端执行技能：服务端只在模型调用时中断、下发给前端执行；批准恢复时 MSAGENT 会执行这个占位函数，
                // 它从 <see cref="ClientToolResultStore"/> 读取前端回传的真实结果返回给模型（避免返回占位文本让模型在服务端跑 stub）
                var func = isClientSkill
                    ? AIFunctionFactory.Create(() =>
                    {
                        var v = ClientToolResultStore.ConsumeOrDefault(toolName);
                        ClientToolTrace.Write($"STUB-INVOKE tool={toolName} read={(v is null ? "NULL" : $"len={v.Length} first={v.Substring(0, Math.Min(60, v.Length))}")}");
                        return Task.FromResult(v ?? "客户端执行（本技能不在服务端运行，需前端执行并回传结果）");
                    }, toolName, desc)
                    : AIFunctionFactory.Create((string query, System.Threading.CancellationToken ct) => runner.InvokeAsync(skill, query, ct), toolName, desc);
                // 客户端执行技能一律审批包装：模型调用即中断，等待前端执行并回传结果（服务端不自动执行）
                var needsApproval = skill.RequiresApproval || isClientSkill;
                var wrapped = needsApproval ? new ApprovalRequiredAIFunction(func) : func;
                defTools.Add(wrapped);
                occupied.Add(toolName);
                if (needsApproval)
                    _agentApprovalToolNames[agentId] = (_agentApprovalToolNames.TryGetValue(agentId, out var a) ? a.ToList() : [])
                        .Append(toolName).Distinct().ToList();
                if (isClientSkill)
                    _agentClientToolNames[agentId] = (_agentClientToolNames.TryGetValue(agentId, out var c) ? c.ToList() : [])
                        .Append(toolName).Distinct().ToList();
            }
            if (defTools.Count > 0)
            {
                var chatTools = (IList<AITool>)(chatOptions.ChatOptions.Tools ??= []);
                foreach (var t in defTools) chatTools.Add(t);
                _agentToolNames[agentId] = (_agentToolNames.TryGetValue(agentId, out var names) ? names.ToList() : [])
                    .Concat(defTools.Select(t => t.Name)).Distinct().ToList();
            }
        }

        if (string.Equals(_options.Provider, "mock", StringComparison.OrdinalIgnoreCase))
        {
            var clientToolNames = _agentClientToolNames.TryGetValue(agentId, out var c) ? c : null;
            return new ChatClientAgent(new MockChatClient(def, enableTools: _options.EnableTools, skills: def.Skills, clientToolNames: clientToolNames), chatOptions, _loggerFactory, _services);
        }

        var isDeepSeek = string.Equals(_options.Provider, "deepseek", StringComparison.OrdinalIgnoreCase);
        var client = BuildOpenAIChatClient(_options, def, isDeepSeek);
        _logger.LogInformation("智能体 {AgentId} 构建模型客户端：provider={Provider} thinkingMode={Thinking} model={Model}",
            agentId, _options.Provider, _options.ThinkingMode, ResolveModelName(_options, def, isDeepSeek));

        // Microsoft.Agents.AI.OpenAI：ChatClient → ChatClientAgent（Instructions/Name/Description 经 options）
        // clientFactory 挂用量捕获装饰器（最底层，usage 帧在此层仍为原始类型）；未注册用量服务时透传不包装
        return client.AsAIAgent(chatOptions,
            clientFactory: _usage.Value is null ? null : inner => new UsageCaptureChatClient(inner, _usage),
            _loggerFactory, _services);
    }

    /// <summary>解析实际使用的模型名（思考模式开启时优先推理模型；否则智能体 Model → 全局 Model → 提供方默认）。</summary>
    internal static string ResolveModelName(AgentOptions options, AgentDefinition def, bool isDeepSeek)
    {
        var model = options.ThinkingMode
            ? options.ThinkingModel ?? (isDeepSeek ? DeepSeekReasonerModel : null)
            : null;
        return model ?? def.Model ?? options.Model
            ?? (isDeepSeek ? DeepSeekDefaultModel : null)
            ?? throw new InvalidOperationException("未配置模型名（Agents:Model 或智能体 Model）");
    }

    /// <summary>构建 OpenAI 兼容 ChatClient（真实模型路径；Provider=mock 走 <see cref="MockChatClient"/>）。供分身人设生成等复用。
    /// <paramref name="modelOverride"/> 非空时强制用该模型（视觉等专用场景）。</summary>
    internal static ChatClient BuildOpenAIChatClient(AgentOptions options, AgentDefinition def, bool isDeepSeek, string? modelOverride = null)
    {
        // 思考模式（默认开启）：优先用推理模型（DeepSeek 官方 deepseek-reasoner；可经 Agents:ThinkingModel 覆盖）；
        // 关闭时回退常规模型（智能体单独 Model → 全局 Model → 提供方默认）。modelOverride 优先于这一切。
        var model = modelOverride ?? ResolveModelName(options, def, isDeepSeek);
        var apiKey = options.ApiKey
            ?? throw new InvalidOperationException(
                "Provider 非 mock 时必须配置 API Key（Agents:ApiKey / dotnet user-secrets / 环境变量 DEEPSEEK_API_KEY 或 OPENAI_API_KEY）");
        var credential = new ApiKeyCredential(apiKey);

        var endpoint = string.IsNullOrWhiteSpace(options.Endpoint)
            ? (isDeepSeek ? DeepSeekEndpoint : null)
            : options.Endpoint;

        // 禁用 W3C traceparent 自动注入（EnableDistributedTracing=false）：.NET 10 的 DiagnosticsHandler
        // 注入 traceparent 时在部分平台（Linux 容器实测）把前一个 header 的行结束符写坏（\n\r\n），
        // 导致 DeepSeek 等严格网关返回 400 invalid header。traceparent 对调用方无业务价值，直接关闭。
        var openAiOptions = new OpenAIClientOptions
        {
            EnableDistributedTracing = false,
        };
        if (endpoint is not null) openAiOptions.Endpoint = new Uri(endpoint);

        return new ChatClient(model, credential, openAiOptions);
    }

    /// <summary>解析视觉（图片理解）模型名：显式配置优先；DeepSeek 提供方自动用视觉模型；其余无则为 null（不支持图片）。</summary>
    internal static string? ResolveVisionModelName(AgentOptions options, bool isDeepSeek)
        => !string.IsNullOrWhiteSpace(options.VisionModel) ? options.VisionModel!.Trim() : (isDeepSeek ? DeepSeekVisionModel : null);

    private List<AITool> BuildTools(IEnumerable<string> requireApprovalNames)
    {
        var tools = new List<AITool>
        {
            AIFunctionFactory.Create(GetCurrentTime, "get_current_time", "返回当前服务器时间（UTC ISO 8601 字符串）"),
            AIFunctionFactory.Create(CalculatorTool.Evaluate, "calculator",
                "精确计算数学表达式，支持 + - * / % ^ 括号、函数（sqrt/abs/round/floor/ceil/min/max/pow/log/ln/exp/sin/cos/tan）与常量 pi/e。例：(1+2)*3^2、sqrt(144)、15%4、2^10"),
            AIFunctionFactory.Create(UnitConverterTool.Convert, "unit_converter",
                "单位换算，支持长度/质量/温度/时间/数据量/速度。参数：value 数值、from 原单位、to 目标单位。例：100 km 转 mile、37 c 转 f、2 t 转 kg、1 day 转 h"),
            AIFunctionFactory.Create(PublishAnnouncement, "publish_announcement", "发布一条群公告（所有群成员可见，需要用户批准后执行）"),
        };

        // 群聊上下文工具（本地能力：记忆语义检索 + 附件文本读取）
        var contextTools = new GroupContextTools(_services, _options, _loggerFactory);
        tools.Add(AIFunctionFactory.Create(contextTools.SearchMemory, "group_memory_search",
            "按语义检索该智能体的历史记忆（覆盖其所在的所有群）。**仅当记忆与当前问题高度相关时调用**；" +
            "结果已按 ≥0.40 相似度严格过滤、最多 3 条，返回为空即表示没有足够相关的历史记忆，切勿编造。参数：query 检索问题"));
        tools.Add(AIFunctionFactory.Create(contextTools.ReadAttachment, "read_attachment",
            "按附件 ID 读取上传文件内容（支持 txt/md/json/csv 与 docx/xlsx/pptx/pdf）。附件 ID 形如 att_xxx，来自消息中的附件信息。参数：attachmentId"));

        // 智能体自建可复用技能：模型用 create_skill 定义「能执行的功能 / 提示词模板」，存入技能库，当前智能体挂载引用。
        // 强制审批（不随 RequireApprovalToolNames 名单调整）：技能创建 / 更新属于敏感配置变更
        tools.Add(new ApprovalRequiredAIFunction(AIFunctionFactory.Create(CreateSkillToolImpl, "create_skill",
            "创建 / 更新一个可复用技能（OpenClaw 风格：能执行的功能 / 提示词模板），存入技能库并挂载到当前智能体，供本智能体与其他智能体复用。" +
            "技能类型 kind：shell（可执行命令/脚本，body 填脚本正文）、http（调用外部接口，body 填 JSON 配置 {method,url,headers,body}）、" +
            "prompt（提示词 / 流程模板，body 填模板正文）。" +
            "参数：skillName 技能名、kind 类型名（shell/http/prompt）、description 调用说明（何时调用、能获得什么）、" +
            "body 技能正文（按类型）、query 可选示例请求（用于首次试运行校验技能是否正确）")));

        if (_options.EnableWebTools)
        {
            var web = new WebTools(_services, _loggerFactory);
            tools.Add(AIFunctionFactory.Create(web.WebSearch, "web_search",
                "搜索互联网获取最新信息（默认 DuckDuckGo 免费端点）。回答时效性 / 外部知识问题前先调用。参数：query 搜索关键词"));
            tools.Add(AIFunctionFactory.Create(web.ReadUrl, "read_url",
                "读取网页链接的正文内容（html 转文本，拒绝内网地址）。参数：url 完整 http/https 链接"));
        }

        // 需要审批的工具用 ApprovalRequiredAIFunction 包装：模型调用时运行中断，等待触发者决策（协议 4.5 人机交互）
        // requireApprovalNames 为当前智能体生效的审批名单（全局或该智能体覆盖值）
        var requireApproval = new HashSet<string>(requireApprovalNames, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < tools.Count; i++)
        {
            if (requireApproval.Contains(tools[i].Name))
                tools[i] = new ApprovalRequiredAIFunction((AIFunction)tools[i]);
        }
        return tools;
    }

    private static string GetCurrentTime() => DateTimeOffset.UtcNow.ToString("O");

    /// <summary>演示用审批工具：发布群公告（真实业务可替换为发邮件 / 转账等敏感操作）。</summary>
    [Description("发布一条群公告（所有群成员可见，需用户批准后执行）")]
    private static string PublishAnnouncement([Description("公告内容")] string announcement)
        => $"公告已发布：{announcement}";

    /// <summary>技能名合法字符（OpenAI 工具名规范）。</summary>
    private static readonly System.Text.RegularExpressions.Regex SkillNamePattern = new(
        "^[a-zA-Z0-9_-]{1,40}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    // ================= 自建技能（create_skill，OpenClaw 风格） =================

    /// <summary>
    /// create_skill 工具实现（强制审批后执行，OpenClaw 风格）：创建一个可复用技能定义（shell / http / prompt）到技能库，
    /// 并把它的 SkillId 挂到当前智能体的 <see cref="AgentDefinition.SkillDefIds"/>（快照持久化，重启不丢）。
    /// 技能可被任意其他智能体挂载复用。当前 run 仍用旧 agent 实例，下一条消息重建后生效。
    /// </summary>
    internal string CreateSkillToolImpl(
        [Description("技能名（字母/数字/下划线/连字符，≤40）")] string skillName,
        [Description("技能类型：shell / http / prompt")] string kind,
        [Description("调用说明（何时调用这个技能、能获得什么）")] string description,
        [Description("技能正文（shell 填命令/脚本；http 填 JSON 配置 {method,url,headers,body}；prompt 填提示词/流程模板）")] string? body,
        [Description("可选：示例请求，用于创建后立即试运行校验技能是否正确")] string? query)
    {
        var ctx = AgentGateway.AmbientContext.Value;
        if (ctx is null) return "当前不在智能体运行上下文，无法创建技能。";

        skillName = (skillName ?? "").Trim();
        if (!SkillNamePattern.IsMatch(skillName))
            return "技能名不合法：仅允许字母/数字/下划线/连字符，最长 40 字符（如 risk_analyzer）。";
        if (!Enum.TryParse<AgentSkillKind>(kind, true, out var k) || kind is null or "")
            return "技能类型 kind 无效：仅支持 shell / http / prompt。";
        if (k == AgentSkillKind.Dotnet)
            return "dotnet（C# 动态编译）技能属于高危特权类型，需由系统管理员到技能库手动创建，智能体不能运行时自建。";
        if (k == AgentSkillKind.Org_deploy)
            return "org_deploy（组织落库）技能属受控特权类型，需由系统管理员在技能库手动创建，智能体不能运行时自建。";
        var desc = (description ?? "").Trim();
        if (desc.Length == 0)
            return "请提供技能描述（何时调用、能获得什么）。";
        if (desc.Length > 500) desc = desc[..500];
        var bodyTxt = (body ?? "").Trim();
        if (k != AgentSkillKind.Prompt && string.IsNullOrWhiteSpace(bodyTxt))
            return $"{k} 类型技能的正文（body）不能为空：shell 填脚本正文，http 填 JSON 配置（method/url/headers/body），prompt 填模板正文。";
        if (bodyTxt.Length > 16_000)
            return $"技能正文过长（最多 16000 字符），请精简：{bodyTxt.Length}。";

        var host = GetDefinition(ctx.AgentId);
        if (host is null) return "宿主智能体不存在。";
        if (host.IsSkillTarget) return "技能目标智能体不能再创建技能。";

        // 技能库写入口（若未注册技能库则拒绝）
        var catalog = _skillCatalog.Value;
        if (catalog is null) return "技能库（AgentSkillCatalog）不可用。";

        var ownerId = host.OwnerId;
        var def = new AgentSkillDefinition
        {
            SkillId = skillName,
            Name = skillName,
            Description = desc,
            Kind = k,
            Body = bodyTxt,
            ParametersJson = "",
            RequiresApproval = k != AgentSkillKind.Prompt, // 代码 / HTTP 一律需批准（安全兜底）
            OwnerId = ownerId,
        };
        catalog.Upsert(def);

        // 挂到宿主（去重）；Upsert 失效 agent 缓存 → 下一条消息生效
        var refs = host.SkillDefIds ??= [];
        if (!refs.Contains(def.SkillId, StringComparer.Ordinal)) refs.Add(def.SkillId);
        Upsert(host);

        _logger.LogInformation("智能体 {AgentId} 自建技能 {SkillId}（{Kind}，操作者 {Operator}）",
            ctx.AgentId, def.SkillId, def.Kind, ctx.TriggerUserId);

        // 可选试运行校验：示例请求跑一次，方便发现定义错误
        var validated = "未试运行";
        if (!string.IsNullOrWhiteSpace(query) && _skillRunner.Value is { } runner)
        {
            var runResult = runner.InvokeAsync(def, query, CancellationToken.None).GetAwaiter().GetResult();
            validated = string.IsNullOrWhiteSpace(runResult) ? "（空输出）" : (runResult.Length > 200 ? runResult[..200] + "…" : runResult);
        }
        return $"技能「{def.SkillId}」已创建并挂载到当前智能体（类型 {def.Kind}），其他智能体也可复用；下一条消息起生效。"
            + (string.Equals(validated, "未试运行", StringComparison.Ordinal) ? "" : $"\n试运行结果：\n{validated}");
    }
}
