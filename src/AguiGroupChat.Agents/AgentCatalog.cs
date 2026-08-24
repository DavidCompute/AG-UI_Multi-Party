using System.ClientModel;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text;
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

    private readonly AgentOptions _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IServiceProvider _services;
    private readonly ChangeHub? _changes;
    private readonly MemoryContextProvider? _memoryContext;
    private readonly ILogger<AgentCatalog> _logger;
    // 模型用量统计（可选：注册了 AgentUsageService 才包装 usage 捕获）
    private readonly Lazy<AguiGroupChat.Hub.Agents.AgentUsageService?> _usage;
    private readonly string _workSpaceRoot; // 工作型智能体工作区根（data/workspaces）
    private readonly ConcurrentDictionary<string, AgentWorkSpace> _workSpaces = new(StringComparer.Ordinal); // 按 agentId 缓存
    private readonly ConcurrentDictionary<string, AgentDefinition> _definitions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ChatClientAgent> _agents = new(StringComparer.Ordinal);
    // agentId → 已挂载工具名（创建时填充；测试 / 调试 / 前端展示用）
    private readonly ConcurrentDictionary<string, IReadOnlyList<string>> _agentToolNames = new(StringComparer.Ordinal);
    // agentId → 已挂载且需要人机交互审批的工具名（创建时填充；差异化审批策略测试 / 调试用）
    private readonly ConcurrentDictionary<string, IReadOnlyList<string>> _agentApprovalToolNames = new(StringComparer.Ordinal);

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
        // 工作区根：优先宿主环境内容根（Web/桌面），回退当前工作目录（测试 / 独立运行）
        var contentRoot = _services.GetService(typeof(Microsoft.AspNetCore.Hosting.IWebHostEnvironment)) is Microsoft.AspNetCore.Hosting.IWebHostEnvironment env
            ? env.ContentRootPath
            : Directory.GetCurrentDirectory();
        _workSpaceRoot = Path.IsPathRooted(_options.WorkSpaceRoot)
            ? Path.GetFullPath(_options.WorkSpaceRoot)
            : Path.GetFullPath(Path.Combine(contentRoot, _options.WorkSpaceRoot));
        foreach (var def in options.Agents) _definitions.TryAdd(def.AgentId, def);
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

    public IReadOnlyList<string> AgentIds => _definitions.Keys.ToList();

    /// <summary>取（或创建）某智能体的专属工作区（data/workspaces/&lt;agentId&gt;/）。</summary>
    private AgentWorkSpace CreateWorkSpace(string agentId) => _workSpaces.GetOrAdd(agentId, id =>
    {
        // 目录名净化：仅保留字母/数字/下划线/连字符（其余替换为 _），杜绝路径穿越；空则退化为 agent
        var cleaned = new StringBuilder(id.Length);
        foreach (var ch in id)
            cleaned.Append(char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' ? ch : '_');
        var safeId = cleaned.ToString().Trim('_');
        if (string.IsNullOrEmpty(safeId)) safeId = "agent";
        return new AgentWorkSpace(Path.Combine(_workSpaceRoot, safeId));
    });

    /// <summary>解析工作区 PLAN.md 为结构化步骤计划（消息可视化用）：返回 (标题, 步骤)。
    /// 非工作型智能体 / 无 PLAN.md 返回 (null, 空)。只读，不触发任何写操作。</summary>
    public (string? Title, IReadOnlyList<AguiGroupChat.Hub.Models.PlanStepInfo> Steps) ReadPlan(string agentId)
    {
        if (!_definitions.TryGetValue(agentId, out var def) || !def.EnableWorkTools)
            return (null, []);
        try
        {
            var space = CreateWorkSpace(agentId);
            var plan = space.ContainsResolve("PLAN.md");
            if (plan is null || !File.Exists(plan)) return (null, []);
            string? title = null;
            var steps = new List<AguiGroupChat.Hub.Models.PlanStepInfo>();
            var id = 0;
            foreach (var raw in System.IO.File.ReadAllLines(plan))
            {
                var line = raw.Trim();
                if (title is null && line.StartsWith("# ", StringComparison.Ordinal))
                {
                    title = line[2..].Trim();
                    continue;
                }
                if (!line.StartsWith("- [ ", StringComparison.Ordinal)
                    && !line.StartsWith("- [x]", StringComparison.Ordinal)
                    && !line.StartsWith("- [X]", StringComparison.Ordinal) && !line.StartsWith("- [O]", StringComparison.Ordinal))
                    continue;
                var done = line.StartsWith("- [x]", StringComparison.Ordinal) || line.StartsWith("- [X]", StringComparison.Ordinal) || line.StartsWith("- [O]", StringComparison.Ordinal);
                var text = line[5..].Trim(); // 去掉 "- [ ] " / "- [x] " 前缀
                if (text.Length == 0) continue;
                // 兼容 "1. 步骤" 序号前缀：展示时去掉
                var dot = text.IndexOf('.');
                if (dot > 0 && int.TryParse(text[..dot], out _)) text = text[(dot + 1)..].Trim();
                steps.Add(new AguiGroupChat.Hub.Models.PlanStepInfo { Id = ++id, Text = text, Done = done });
            }
            return (title, steps);
        }
        catch
        {
            return (null, []); // 读取失败静默：计划可视化是增强，不阻断消息主流程
        }
    }

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
        // 工作型智能体（EnableWorkTools + 全局 WorkToolsEnabled）：额外挂载文件/命令工具。
        // 只能在专属工作区（data/workspaces/<agentId>/）内操作；命令/写操作有白名单与审批边界。
        if (_options.WorkToolsEnabled && def.EnableWorkTools)
        {
            tools ??= [];
            var workSpace = CreateWorkSpace(def.AgentId);
            var workTools = new AgentWorkTools(workSpace, _services, _loggerFactory);
            workSpace.EnsureRoot();
            tools.Add(AIFunctionFactory.Create(workTools.ListDir, "list_dir",
                "列出工作区目录内容（只读）。参数：relPath 相对路径（留空 = 工作区根目录）"));
            tools.Add(AIFunctionFactory.Create(workTools.ReadFile, "read_file",
                "读取工作区内文本文件（只读，UTF-8）。参数：path 工作区内的相对路径"));
            tools.Add(new ApprovalRequiredAIFunction(AIFunctionFactory.Create(workTools.WriteFile, "write_file",
                "写或追加工作区内文件（影响工作区，需用户批准）。参数：path 相对路径、content 完整内容、append 是否追加（默认 false 覆盖）")));
            tools.Add(new ApprovalRequiredAIFunction(AIFunctionFactory.Create(workTools.PublishFile, "publish_file",
                "把工作区内的文件发布为群可下载附件（产物回传，需用户批准）。参数：path 工作区内的相对路径")));
            // 网页采集落盘：工作区文件（写操作，需批准）
            tools.Add(new ApprovalRequiredAIFunction(AIFunctionFactory.Create(workTools.FetchUrl, "fetch_url",
                "抓取指定 URL 网页正文并保存为工作区内 Markdown 文件（采集外部资料，需用户批准）。参数：url 完整 http/https 链接、saveAs 保存的相对路径（如 doc.md）")));
            // 文件整理：安全封装的复制 / 重命名（免 shell 转义，写操作需批准）
            tools.Add(new ApprovalRequiredAIFunction(AIFunctionFactory.Create(workTools.CopyFile, "copy_file",
                "工作区内复制文件（整理归档，需用户批准）。参数：source 源相对路径、target 目标相对路径")));
            tools.Add(new ApprovalRequiredAIFunction(AIFunctionFactory.Create(workTools.RenameFile, "rename_file",
                "工作区内重命名 / 移动文件（整理归档，需用户批准）。参数：source 源相对路径、target 目标相对路径")));
            // 记忆延续：NOTES.md 备忘（跨对话。remember 写需批准；read_notes 只读免审批）
            tools.Add(new ApprovalRequiredAIFunction(AIFunctionFactory.Create(workTools.Remember, "remember",
                "把一条工作备忘写入工作区 NOTES.md（中间结论 / 待办 / 进度，跨对话延续，需用户批准）。参数：note 备忘内容")));
            tools.Add(AIFunctionFactory.Create(workTools.ReadNotes, "read_notes",
                "读取工作区 NOTES.md 备忘（跨对话回忆之前的进度 / 待办）。只读"));
            // 批量 / 编排工具（复杂任务）：只读免审批；写类需批准
            tools.Add(AIFunctionFactory.Create(workTools.ListTree, "list_tree",
                "递归列出工作区内全部文件（含子目录与大小）。只读，不需批准"));
            tools.Add(AIFunctionFactory.Create(workTools.ReadBatch, "read_batch",
                "一次读取多个工作区文件（逗号分隔的相对路径，最多 20 个），便于批量查看产物。只读，不需批准"));
            tools.Add(new ApprovalRequiredAIFunction(AIFunctionFactory.Create(workTools.BatchRename, "batch_rename",
                "批量把符合扩展名（如 md / txt / json）的文件迁移到目标目录并可选加后缀（整理归档，需用户批准）。参数：extension 无点扩展名、sourceDir 源目录、targetDir 目标目录、suffix 可选后缀")));
            tools.Add(new ApprovalRequiredAIFunction(AIFunctionFactory.Create(workTools.Archive, "archive",
                "把工作区内的文件或目录打包为 zip（批量归档 / 发布，需用户批准）。参数：path 源相对路径、archiveName 归档文件名（.zip）")));
            tools.Add(new ApprovalRequiredAIFunction(AIFunctionFactory.Create(workTools.Remove, "remove",
                "安全删除工作区内的文件或目录（防删根保护，需用户批准）。参数：path 相对路径")));
            // 任务计划器：复杂任务先写步骤计划（PLAN.md），逐步骤执行并用 plan_mark 打勾，跨对话 plan_read 接着干
            tools.Add(new ApprovalRequiredAIFunction(AIFunctionFactory.Create(workTools.PlanWrite, "plan_write",
                "为复杂任务写一份步骤计划到 PLAN.md（先规划再执行，需用户批准）。参数：title 任务标题、steps 用换行或逗号分隔的步骤列表")));
            tools.Add(AIFunctionFactory.Create(workTools.PlanRead, "plan_read",
                "读取 PLAN.md 计划（各步完成状态）。只读，不需批准"));
            tools.Add(new ApprovalRequiredAIFunction(AIFunctionFactory.Create(workTools.PlanMarkDone, "plan_mark",
                "把 PLAN.md 中的某一步标记为完成（打勾，需用户批准）。参数：step 步骤序号（从 1 开始）")));
            // shell：一律需审批（写/删除/组合命令尤其敏感，统一由用户确认后再执行）；工具内再做白名单与越界拦截
            tools.Add(new ApprovalRequiredAIFunction(AIFunctionFactory.Create(workTools.ShellAsync, "shell",
                "在工作区内执行终端命令（只能访问你的工作区，执行前需用户批准）。" +
                $"允许命令：{string.Join("/", AgentWorkTools.AllowedCommands)}")));
        }
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
                        new AgentSkillCall(target, skill.TargetAgentId, targetNick, _loggerFactory).InvokeAsync,
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

        if (string.Equals(_options.Provider, "mock", StringComparison.OrdinalIgnoreCase))
        {
            return new ChatClientAgent(new MockChatClient(def, enableTools: _options.EnableTools, skills: def.Skills), chatOptions, _loggerFactory, _services);
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

    /// <summary>构建 OpenAI 兼容 ChatClient（真实模型路径；Provider=mock 走 <see cref="MockChatClient"/>）。供分身人设生成等复用。</summary>
    internal static ChatClient BuildOpenAIChatClient(AgentOptions options, AgentDefinition def, bool isDeepSeek)
    {
        // 思考模式（默认开启）：优先用推理模型（DeepSeek 官方 deepseek-reasoner；可经 Agents:ThinkingModel 覆盖）；
        // 关闭时回退常规模型（智能体单独 Model → 全局 Model → DeepSeek 默认）。
        var model = ResolveModelName(options, def, isDeepSeek);
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

        // 智能体自我生成技能：模型定义子智能体（技能名 + 人设 + 调用说明），需用户批准后创建并挂载。
        // 强制审批（不随 RequireApprovalToolNames 名单调整）：技能创建属于敏感配置变更
        tools.Add(new ApprovalRequiredAIFunction(AIFunctionFactory.Create(CreateSkillTool, "create_skill",
            "创建 / 更新一个可复用技能（子智能体）：当前智能体在回复中需要特定领域的专长时，可用此工具定义技能名称、" +
            "子智能体人设与调用说明，经用户批准后自动创建并挂载（下一条消息生效）。" +
            "参数：skillName 技能名（字母/数字/下划线/连字符）、instructions 子智能体人设与职责、description 调用说明")));

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

    /// <summary>单个智能体最多可自建技能数（防滥用）。</summary>
    private const int MaxSelfCreatedSkills = 10;

    /// <summary>
    /// create_skill 工具实现（强制审批后执行）：创建 / 更新技能目标智能体（agentId = skill_&lt;skillName&gt;，
    /// 标记 IsSkillTarget 对目录 / API 隐藏），并把 AgentSkillConfig 挂到当前智能体（快照持久化，重启不丢）。
    /// 当前 run 仍用旧 agent 实例，下一条消息重建后生效。
    /// </summary>
    [Description("创建 / 更新一个可复用技能（子智能体），需用户批准后生效")]
    internal string CreateSkillTool(
        [Description("技能名（字母/数字/下划线/连字符，≤40）")] string skillName,
        [Description("子智能体人设与职责（定义它擅长什么、如何回答）")] string instructions,
        [Description("调用说明（何时调用这个技能、能获得什么）")] string description)
    {
        var ctx = AgentGateway.AmbientContext.Value;
        if (ctx is null) return "当前不在智能体运行上下文，无法创建技能。";

        skillName = (skillName ?? "").Trim();
        if (!SkillNamePattern.IsMatch(skillName))
            return "技能名不合法：仅允许字母/数字/下划线/连字符，最长 40 字符（如 risk_analyzer）。";
        if (string.IsNullOrWhiteSpace(instructions))
            return "请提供子智能体人设（instructions）。";
        if (instructions.Length > 4000)
            return "人设过长（最多 4000 字符），请精简。";
        var desc = (description ?? "").Trim();
        if (desc.Length > 200) desc = desc[..200];

        var host = GetDefinition(ctx.AgentId);
        if (host is null) return "宿主智能体不存在。";
        if (host.IsSkillTarget) return "技能目标智能体不能再创建技能。";

        var skills = host.Skills ??= [];
        // 宿主技能列表的读改写共用同一把锁（并发创建技能时防止丢失更新；本方法无 await，锁内安全）
        lock (skills)
        {
            var existingCount = skills.Count;
            var already = skills.Any(s => string.Equals(s.SkillId, skillName, StringComparison.OrdinalIgnoreCase));
            if (!already && existingCount >= MaxSelfCreatedSkills)
                return $"技能数量已达上限（{MaxSelfCreatedSkills} 个），请先删除旧技能。";
        }

        var targetId = "skill_" + skillName;
        // 防覆盖：不允许用户自建技能静默覆盖同名系统 / 用户智能体（IsSkillTarget 的技能目标允许更新，现状）
        var existing = GetDefinition(targetId);
        if (existing is not null && !existing.IsSkillTarget)
            return $"技能名「{skillName}」与现有智能体冲突，请换一个名字";

        // 创建 / 更新技能目标智能体（同 skillName 复用，人设覆盖更新）
        Upsert(new AgentDefinition
        {
            AgentId = targetId,
            Nickname = skillName,
            Description = desc,
            Instructions = instructions.Trim(),
            IsSkillTarget = true,
            OwnerId = host.OwnerId,
        });

        // 挂载到宿主智能体（同 SkillId 更新描述；新增则追加），Upsert 失效 agent 缓存 → 下一条消息生效
        lock (skills)
        {
            var skill = skills.FirstOrDefault(s => string.Equals(s.SkillId, skillName, StringComparison.OrdinalIgnoreCase));
            if (skill is null)
                skills.Add(new AgentSkillConfig { SkillId = skillName, Description = desc, TargetAgentId = targetId });
            else
                skill.Description = desc;
        }
        Upsert(host);

        _logger.LogInformation("智能体 {AgentId} 自建技能 {SkillId}（目标 {Target}，操作者 {Operator}）",
            ctx.AgentId, skillName, targetId, ctx.TriggerUserId);
        return $"技能「{skillName}」已创建并挂载到当前智能体，下一条消息起生效（可回复技能相关问题时调用）。";
    }
}
