using System.Text.RegularExpressions;
using AguiGroupChat.Hub.Models;

namespace AguiGroupChat.Agents;

/// <summary>智能体网关配置（appsettings.json 的 Agents 节点）。</summary>
public sealed class AgentOptions
{
    /// <summary>
    /// 模型提供方：<c>mock</c>（内置模拟客户端，无需密钥，开箱即用）、
    /// <c>openai</c>（OpenAI 官方 / 任何 OpenAI 兼容端点，如 Ollama、vLLM、Azure OpenAI）或
    /// <c>deepseek</c>（DeepSeek 官方 API，自动使用 https://api.deepseek.com 与 deepseek-chat）。
    /// </summary>
    public string Provider { get; set; } = "mock";

    /// <summary>
    /// API Key。未显式配置时依次回退环境变量 <c>DEEPSEEK_API_KEY</c>、<c>OPENAI_API_KEY</c>。
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>OpenAI 兼容端点（如 http://localhost:11434/v1）。留空时：deepseek 提供方自动用官方端点，其余走 OpenAI 官方端点。</summary>
    public string? Endpoint { get; set; }

    /// <summary>默认模型名（智能体可单独覆盖）。</summary>
    public string? Model { get; set; }

    /// <summary>
    /// 思考模式（默认开启）：智能体调用模型时使用推理模型（DeepSeek 自动用 <c>deepseek-reasoner</c>，
    /// 可经 <see cref="ThinkingModel"/> 覆盖），回复前先思考、回答质量更高，但更慢 / 更贵。
    /// 可在「模型配置」弹窗按需开关（运行时保存后立即生效）。
    /// </summary>
    public bool ThinkingMode { get; set; } = true;

    /// <summary>思考模式使用的模型名（留空时：DeepSeek 自动用 deepseek-reasoner，其余提供方回退默认模型）。</summary>
    public string? ThinkingModel { get; set; }

    /// <summary>
    /// 每个用户每日 token 用量配额（默认 0 = 不限）。超过配额的触发请求被拒绝（AGENT_QUOTA_EXCEEDED），
    /// 次日 0 点（UTC）自动恢复；定时任务（system 触发）与桥接调用不计入个人配额。
    /// 用量统计见管理员控制台「用量统计」。
    /// </summary>
    public long DailyTokenQuotaPerUser { get; set; }

    /// <summary>是否启用工具调用（内置本地工具：get_current_time / calculator / unit_converter /
    /// group_memory_search / read_attachment，以及需 EnableWebTools 的网络工具）。供 TOOL_CALL_START / 人机交互演示。</summary>
    public bool EnableTools { get; set; }

    /// <summary>是否启用网络类工具（web_search / read_url）。默认 false：本地工具零依赖、离线可用；
    /// 开启后工具可访问外网（搜索端点可配置）。</summary>
    public bool EnableWebTools { get; set; }

    /// <summary>
    /// <summary>是否启用工作型智能体的文件 / 命令工具（<c>list_dir</c> / <c>read_file</c> / <c>write_file</c> / <c>shell</c>）。
    /// 默认 false：普通智能体不受影响。仅在智能体自身 <see cref="AgentDefinition.EnableWorkTools"/> 开启时挂载
    /// 这些工具，且命令 / 写操作被限制在 <c>data/workspaces/&lt;agentId&gt;/</c> 工作区内、写操作用例子需经审批。
    /// </summary>
    public bool WorkToolsEnabled { get; set; }

    /// <summary>工作型智能体工作区根目录（相对路径基于内容根解析；绝对路径直接使用）。
    /// 每个启用工作工具的智能体拥有 <see cref="WorkSpaceRoot"/>/&lt;agentId&gt;/ 独立子目录。</summary>
    public string WorkSpaceRoot { get; set; } = "data/workspaces";

    /// <summary>web_search 工具端点（默认 DuckDuckGo Instant Answer API，免费无密钥，返回摘要式结果）。</summary>
    public string WebSearchEndpoint { get; set; } = "https://api.duckduckgo.com/";

    /// <summary>
    /// 需要人机交互审批的工具名列表（默认 <c>publish_announcement</c>）：命中后用 ApprovalRequiredAIFunction 包装，
    /// 模型调用时运行中断，等待触发者在前端批准 / 拒绝后恢复（协议 4.5）。
    /// </summary>
    public List<string> RequireApprovalToolNames { get; set; } = ["publish_announcement"];

    /// <summary>语境触发（Contextual）发言决策时携带的最近消息条数。</summary>
    public int ContextMaxMessages { get; set; } = 10;

    /// <summary>AG-UI 桥接全局配置：智能体配置了 BridgeEndpoint（或使用默认端点）时，
    /// 不经本地大模型，改为以 AG-UI 协议对接外部 AG-UI 服务（标准 AG-UI 或本项目群聊扩展）。</summary>
    public AguiBridgeOptions? AguiBridge { get; set; }

    /// <summary>语义记忆（RAG）：群消息向量化存储，回复前按相似度检索注入上下文。仅 postgres 提供器 + pgvector 可用。</summary>
    public MemoryOptions Memory { get; set; } = new();

    public List<AgentDefinition> Agents { get; set; } = [];
}

/// <summary>
/// 语义记忆（RAG）配置：消息经 OpenAI 兼容 <c>/v1/embeddings</c> 向量化后写入
/// PostgreSQL + pgvector 表，智能体触发时按语义相似度检索 top-k 注入上下文（长期记忆，
/// 与「最近 N 条」滑动窗口互补）。写入为异步 fire-and-forget，失败不影响群聊主流程。
/// </summary>
public sealed class MemoryOptions
{
    /// <summary>是否启用语义记忆（默认关闭；sqlite 模式用 sqlite-vec，postgres 模式用 pgvector）。</summary>
    public bool Enabled { get; set; }

    /// <summary>embedding 提供方：<c>http</c>（默认，OpenAI 兼容端点）或 <c>llama</c>（LLamaSharp 本地 GGUF 模型）。</summary>
    public string Provider { get; set; } = "http";

    /// <summary>OpenAI 兼容 embedding 端点（如 Ollama <c>http://localhost:11434/v1</c>）。
    /// 缺省依次回退 Agents:Endpoint、官方 OpenAI 端点。仅 Provider=http 时生效。</summary>
    public string? EmbeddingEndpoint { get; set; }

    public string? EmbeddingApiKey { get; set; }

    /// <summary>embedding 模型名（Ollama 默认 nomic-embed-text）。仅 Provider=http 时生效。</summary>
    public string EmbeddingModel { get; set; } = "nomic-embed-text";

    /// <summary>本地 GGUF embedding 模型路径（如 nomic-embed-text-v1.5.Q8_0.gguf / bge-m3.Q8_0.gguf）。
    /// 仅 Provider=llama 时生效；未配置时回退到工作目录下 <c>models/embedding.gguf</c>。</summary>
    public string? LlamaModelPath { get; set; }

    /// <summary>本地模型推理上下文大小（tokens）。仅 Provider=llama 时生效。</summary>
    public int LlamaContextSize { get; set; } = 512;

    /// <summary>本地模型推理线程数（默认 4；桌面 CPU 推荐 4~8）。</summary>
    public int LlamaThreads { get; set; } = 4;

    /// <summary>向量维度（须与模型一致，决定建表的 vector(n) 维度）。</summary>
    public int EmbeddingDimensions { get; set; } = 768;

    /// <summary>每次回复检索注入的历史记忆条数。</summary>
    public int TopK { get; set; } = 5;

    /// <summary>相似度阈值（0..1，余弦相似度），低于此值不注入。</summary>
    public double MinScore { get; set; } = 0.25;

    /// <summary>检索范围：agent（默认，该智能体所在的所有群的记忆）/ group（仅当前触发群）/ all（全部群）。</summary>
    public string Scope { get; set; } = "agent";

    /// <summary>个人记忆：每次回复注入的「触发者本人历史发言」条数（默认 0 = 关闭；需用户与智能体都开启才注入）。</summary>
    public int PersonalTopK { get; set; }

    /// <summary>个人记忆相似度阈值（0..1），低于此值不注入。</summary>
    public double PersonalMinScore { get; set; } = 0.25;

    /// <summary>
    /// 混合检索（2.1）：默认开。在稠密向量检索的命中集合内，用 BM25 词项评分做<b>二次精排</b>——
    /// 同 cosine 相似度/重要级下，命中查询关键词的记忆排得更靠前。不改变返回条数与召回集合
    /// （只在既有命中内调序，避免引入假阳性），因此对既有索引行为无破坏。
    /// </summary>
    public bool HybridSearch { get; set; } = true;

    /// <summary>混合检索中 BM25 分数的权重（与余弦相似度线性融合：score = cosine×(1-w) + bm25×w）。</summary>
    public double HybridBm25Weight { get; set; } = 0.35;

    /// <summary>注入的每条记忆文本截断长度。</summary>
    public int MaxCharsPerMemory { get; set; } = 600;

    /// <summary>检索 query 文本（触发消息）截断长度。</summary>
    public int MaxQueryChars { get; set; } = 2000;

    /// <summary>知识库文档切片大小（字符）：长文本按此窗口切片后向量化，窗口偏小/过长都会影响检索命中。</summary>
    public int KnowledgeChunkSize { get; set; } = 4096;

    /// <summary>知识库文档切片重叠（字符）：相邻切片共享的重叠文本，降低边界信息丢失风险。一般取切片大小的 1/8~1/5。</summary>
    public int KnowledgeChunkOverlap { get; set; } = 512;

    /// <summary>embedding 调用超时（秒）。默认 60：CPU 环境首次加载模型（如 bge-m3 约 1.1GB）可能超过 15 秒。</summary>
    public int EmbeddingTimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// 自动遗忘：记忆默认保留天数（0 = 永不过期，默认）。&gt;0 时新写入的记忆自动带过期时间
    /// （写入时间 + 保留天数），检索不再命中，并由后台定时任务物理清理。重要（importance≥1）记忆
    /// 不受此限制（保留更久，防止重要决策被误清理）。
    /// </summary>
    public int RetentionDays { get; set; }
}

/// <summary>
/// AG-UI 桥接配置：连接外部 AG-UI 服务的默认参数，可被单个智能体的 Bridge 字段覆盖。
/// </summary>
public sealed class AguiBridgeOptions
{
    /// <summary>外部 AG-UI 服务 WebSocket 端点（如 ws://agui-ext:8080/ws），智能体未单独配置时生效。</summary>
    public string? Endpoint { get; set; }

    /// <summary>桥接方言：standard（标准 AG-UI 事件 USER_MESSAGE / ASSISTANT_MESSAGE / RUN_*）
    /// 或 hub（本项目群聊扩展协议 GROUP_MESSAGE_SEND / TEXT_MESSAGE_*），默认 standard。</summary>
    public string Mode { get; set; } = "standard";

    /// <summary>认证令牌：连接时携带 Authorization: Bearer 头。</summary>
    public string? Token { get; set; }

    /// <summary>
    /// 是否允许桥接端点指向私网 / 环回地址（默认 true：本机 / 内网 AG-UI 服务是常见部署形态，
    /// 且桥接端点仅系统管理员可配置）。公网部署建议设 false：启用域名 DNS 解析逐 IP 校验，
    /// 拦截 localhost / 127.x / 内网域名（收紧 SSRF 面）。
    /// </summary>
    public bool AllowPrivateEndpoints { get; set; } = true;

    /// <summary>连接超时（秒），默认 10。</summary>
    public int ConnectTimeoutSeconds { get; set; } = 10;
}

/// <summary>单个智能体的定义（协议 §6 触发规则 + MSAGENT 人设）。</summary>
public sealed class AgentDefinition
{
    public required string AgentId { get; set; }

    public required string Nickname { get; set; }

    public string Description { get; set; } = "";

    /// <summary>MSAGENT 系统提示（Instructions）。</summary>
    public string Instructions { get; set; } = "";

    /// <summary>头像 URL（可为 /ag-ui/files/... 上传地址）。</summary>
    public string? Avatar { get; set; }

    public AgentTriggerMode TriggerMode { get; set; } = AgentTriggerMode.Mentioned;

    /// <summary>TriggerMode=Keyword 时的触发关键词。</summary>
    public List<string>? Keywords { get; set; }

    /// <summary>
    /// 定时任务表达式（5 段 cron：分 时 日 月 周，UTC）。非空时该智能体按表达式定时向
    /// 其加入的每个群发一条汇报消息（如 <c>0 9 * * *</c> 每天 9 点）；留空不启用。
    /// 与 <see cref="TriggerMode"/> 独立：消息触发与定时触发可同时生效。
    /// </summary>
    public string? Schedule { get; set; }

    /// <summary>该智能体单独使用的模型（缺省用 AgentOptions.Model）。</summary>
    public string? Model { get; set; }

    /// <summary>AG-UI 桥接端点（ws://...）。非空时该角色不经本地大模型，
    /// 改为以 AG-UI 协议对接外部服务，外部回复流式回灌群聊。</summary>
    public string? BridgeEndpoint { get; set; }

    /// <summary>桥接方言（覆盖全局 AguiBridge.Mode）：standard / hub。</summary>
    public string? BridgeMode { get; set; }

    /// <summary>桥接认证令牌（覆盖全局 AguiBridge.Token）。</summary>
    public string? BridgeToken { get; set; }

    /// <summary>
    /// 是否开启个人记忆（默认关闭）。开启后，该智能体回复时会检索触发者本人的历史发言并注入上下文。
    /// </summary>
    public bool PersonalMemoryEnabled { get; set; }

    /// <summary>
    /// 是否为工作型智能体（默认关闭）：开启后挂载文件 / 命令工具（<c>list_dir</c> / <c>read_file</c> /
    /// <c>write_file</c> / <c>shell</c>），只能在该智能体专属工作区（<c>data/workspaces/&lt;agentId&gt;/</c>）内
    /// 执行只读操作或（经 HITL 审批的）写操作。文件 / 命令操作有安全边界（路径越界 / 危险命令拒绝），
    /// 普通聊天智能体不受影响。需全局 <see cref="AgentOptions.WorkToolsEnabled"/> 开启。
    /// </summary>
    public bool EnableWorkTools { get; set; }

    /// <summary>
    /// 是否私密智能体（默认关闭）。私密智能体仅创建者（<see cref="OwnerId"/>）可将其加入群。
    /// 创建者以外的用户看不到 / 拉不进私密智能体。
    /// </summary>
    public bool IsPrivate { get; set; }

    /// <summary>
    /// 智能体级**差异化审批策略**（人机交互 HITL）：本智能体需要审批的工具名列表
    /// （覆盖全局 <see cref="AgentOptions.RequireApprovalToolNames"/>）。
    /// 空 = 跟随全局名单；非空 = 仅对本智能体这套名单生效（可替换全局，例如本智能体的
    /// <c>publish_announcement</c> 不需要审批、或额外把其他敏感工具纳入审批）。
    /// 强制审批的极敏感工具（<c>create_skill</c>、工作型智能体的写/命令类工具）不受此名单影响，
    /// 始终需要审批。
    /// </summary>
    public List<string> RequireApprovalToolNames { get; set; } = [];

    /// <summary>
    /// **角色交接（1.2，整轮委托）**：非空时，本智能体收到触发后<b>整轮委托</b>给该中继智能体，
    /// 中继的回复即作为本智能体对群的答复（「由 X 代答 / 交接」）。与技能（模型按需调用子代理）
    /// 和编排流水线（多步 + 聚合）不同：本字段是<b>确定性、整轮</b>的角色别名/交接。
    /// 不得指向自身（且中继目标自身不再接力，防止循环链）。
    /// </summary>
    public string? RelayToAgentId { get; set; }

    /// <summary>
    /// **任务指派白名单（向下）**：本数字员工被 @ 时，若按自身系统提示词判定语境不属于自己，
    /// 可在<b>白名单内自动指派</b>给更合适的下游数字员工（由模型在该名单里推断目标）。
    /// 留空 = 不做向下指派。指派方向由「白名单 + 系统提示词语境推断」自动决定。
    /// </summary>
    public List<string> AssignmentIds { get; set; } = [];

    /// <summary>
    /// **问题提升目标（向上，手工配置）**：本数字员工达不到语境且白名单也无合适指派对象时，
    /// <b>提升</b>给该数字员工（通常是其上级/主管）。由人工配置。若为空，且自身也无解，则回答「不能解决」。
    /// </summary>
    public string? EscalationAgentId { get; set; }

    /// <summary>创建者 userId（运行时创建时记录；appsettings 种子为 null = 系统级智能体）。</summary>
    public string? OwnerId { get; set; }

    /// <summary>
    /// 是否为技能目标智能体（智能体自我生成技能时的子代理）：不在智能体目录 / 触发评估 / 群成员选择中暴露，
    /// 仅作为技能（AgentSkillCall）被宿主智能体调用；拒绝 HTTP 管理 API 的编辑 / 删除。
    /// </summary>
    public bool IsSkillTarget { get; set; }

    /// <summary>
    /// 技能（MSAGENT AgentSkill，智能体间调用）：把其他已注册智能体作为子代理技能挂载，
    /// 本智能体需要该领域信息时由模型决定调用。目标智能体不再挂载自身的技能（防循环引用）。
    /// </summary>
    public List<AgentSkillConfig>? Skills { get; set; }

    /// <summary>
    /// 绑定的知识库 ID 列表（<see cref="KnowledgeBaseCatalog"/> 管理）：回复前按这些知识库检索相关片段注入上下文，
    /// 让智能体基于用户上传的知识文档作答（RAG 知识库）。
    /// </summary>
    public List<string> KnowledgeBaseIds { get; set; } = [];

    /// <summary>
    /// **编排流水线（Pipeline，1.1）**：非空时本智能体不直接调用本地大模型，而是按步骤<b>依次</b>调用
    /// 指定的子智能体（<see cref="AgentPipelineStep.StepAgentId"/>），把上一步的输出作为下一步输入，
    /// 最后把各步输出聚合为最终回复（可再附一段总述话术）。用于「规划 → 拆解 → 顺序执行 → 聚合」
    /// 的确定性多角色协作，区别于可自由调用的技能（Skills）。留空不启用。
    /// </summary>
    public List<AgentPipelineStep>? Pipeline { get; set; }
}

/// <summary>编排流水线中的一步：调用一个子智能体，其输入为触发消息 + 前序步骤输出的累积。</summary>
public sealed class AgentPipelineStep
{
    /// <summary>被调用的子智能体 ID（须为已注册智能体；桥接角色亦可作为步骤目标）。</summary>
    public string StepAgentId { get; set; } = "";

    /// <summary>给子智能体的额外指令（人设补充 / 本步要专攻什么）。可空。</summary>
    public string? Prompt { get; set; }
}

/// <summary>智能体技能配置：把另一个已注册智能体作为可调用的子代理（MSAGENT AgentSkill）。</summary>
public sealed class AgentSkillConfig
{
    /// <summary>技能标识（给模型的工具名，如 <c>skill_docs</c>），同一智能体内唯一；留空时自动生成。</summary>
    public string SkillId { get; set; } = "";

    /// <summary>技能描述（给模型的调用说明，说明何时调用、能获得什么）。</summary>
    public string Description { get; set; } = "";

    /// <summary>被调用的智能体 ID（须为已注册智能体；桥接角色也可作为技能目标）。</summary>
    public string TargetAgentId { get; set; } = "";

    /// <summary>合法技能名白名单（OpenAI 工具名规范：字母数字下划线连字符）。</summary>
    private static readonly Regex SkillIdPattern = new("^[a-zA-Z0-9_-]+$", RegexOptions.Compiled);

    /// <summary>SkillId 是否合法（OpenAI 工具名规范；空视为待生成）。</summary>
    public static bool IsValidSkillId(string skillId)
        => !string.IsNullOrWhiteSpace(skillId) && SkillIdPattern.IsMatch(skillId);

    /// <summary>自动生成技能标识：<c>skill_</c> + 目标智能体 ID（非字母数字下划线连字符的字符替换为下划线），
    /// 与 occupied 已占用名冲突时追加 <c>_2</c>/<c>_3</c>…（占用集合会被更新）。</summary>
    public static string GenerateSkillId(string targetAgentId, ISet<string> occupied)
    {
        var safe = SkillIdPattern.IsMatch(targetAgentId ?? "")
            ? (targetAgentId ?? "").Trim()
            : Regex.Replace((targetAgentId ?? "").Trim(), "[^a-zA-Z0-9_-]", "_");
        if (string.IsNullOrWhiteSpace(safe)) safe = "agent";
        var baseName = "skill_" + safe;
        var name = baseName;
        for (var i = 2; !occupied.Add(name); i++) name = baseName + "_" + i;
        return name;
    }
}
