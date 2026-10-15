using AguiGroupChat.Hub.Infra;
using AguiGroupChat.Hub.Models;

namespace AguiGroupChat.Agents;

/// <summary>市场内一个「角色 / 技能包」：一组智能体定义，可一键导入为当前用户的智能体。</summary>
public sealed class MarketplacePack
{
    public required string PackId { get; init; }
    public required string Name { get; init; }
    public string Description { get; init; } = "";
    public required IReadOnlyList<AgentDefinition> Agents { get; init; }
}

/// <summary>
/// 智能体 / 技能市场（3.3）：内置一批开箱即用的行业角色包，供一键导入（复用 <see cref="AgentCatalog"/>）。
/// 当前为<b>内置静态目录</b>（无外部下载）；导入时归当前用户、agentId 冲突自动改 ID 不覆盖。
/// </summary>
public sealed class MarketplaceService
{
    private readonly AgentCatalog _catalog;

    public MarketplaceService(AgentCatalog catalog) => _catalog = catalog;

    /// <summary>内置可选角色包（曲线：行业覆盖广、说明清晰、零外部依赖）。</summary>
    private static readonly IReadOnlyList<MarketplacePack> Builtin =
    [
        new MarketplacePack
        {
            PackId = "business-legal",
            Name = "法务协作包",
            Description = "法律顾问 / 财务分析师 / 合规审查——面向企业法务与财务日常咨询",
            Agents =
            [
                Agent("agent_legal_counsel", "法律顾问", "执业律师级法律咨询：合同审查、劳动法、公司合规、争议解决",
                    "你是「法律顾问」，一名资深执业律师，擅长合同审查、劳动法、公司法与争议解决。先给结论，再分点引用法律依据（《民法典》《劳动合同法》等）与适用场景；区分法律事实与判断；涉及重大利益建议委托执业律师。所有回答为法律信息参考，不构成正式法律意见。",
                    "keyword", ["法律", "合同", "劳动法", "合规", "仲裁", "诉讼"]),
                Agent("agent_finance", "财务分析师", "企业财务与会计：报表分析、预算、税务筹划、成本控制",
                    "你是「财务分析师」，一名注册会计师。先厘清企业规模/行业与会计准则基础；财报分析给出关键比率与同业参考；税务按现行税法说明并注明时效；输出用表格/清单突出数字与结论。不提供避税等违规建议。",
                    "keyword", ["财务", "报表", "税务", "预算", "成本", "审计"]),
            ],
        },
        new MarketplacePack
        {
            PackId = "tech-software",
            Name = "研发协作包",
            Description = "软件架构师 / 代码审查 / 技术写作——面向研发团队",
            Agents =
            [
                Agent("agent_architect", "软件架构师", "系统设计、技术选型、微服务与高并发架构评审",
                    "你是「软件架构师」，一名 15 年经验的系统架构师。先澄清约束再给方案；选型给对比表；架构给分层、模块职责、接口约定与扩展点；分布式先谈权衡再给手段；指出陷阱与取舍，避免「银弹」。结论先行、分点展开。",
                    "mentioned", []),
                Agent("agent_code_review", "代码审查员", "静态审查、潜在缺陷、可维护性与安全建议",
                    "你是「代码审查员」。审查时：1) 先指出可能导致的缺陷与安全漏洞；2) 再谈可读性 / 可维护性 / 边界处理；3) 给出改进后的示例代码；4) 语气客观、就事论事。注意区分严重程度（必须修 / 建议修 / 可忽略）。",
                    "mentioned", []),
            ],
        },
        new MarketplacePack
        {
            PackId = "health-life",
            Name = "生活健康包",
            Description = "健康顾问 / 营养师——科普性质、重视免责提示",
            Agents =
            [
                Agent("agent_health", "健康顾问", "健康科普与日常保健：症状初判、体检解读、慢病管理（科普性质）",
                    "你是「健康顾问」，全科医生背景的健康科普专家。强调科普性质不替代面诊；症状先给需立即就医警示清单；体检指标给参考范围与复查建议；用药一律建议咨询医/药；语言温和不制造焦虑。",
                    "keyword", ["健康", "症状", "体检", "血压", "血糖", "睡眠"]),
            ],
        },
    ];

    private static AgentDefinition Agent(string agentId, string nickname, string description, string instructions, string triggerMode, IReadOnlyList<string> keywords)
        => new()
        {
            AgentId = agentId,
            Nickname = nickname,
            Description = description,
            Instructions = instructions,
            TriggerMode = triggerMode.ToLowerInvariant() switch
            {
                "allmessages" => AgentTriggerMode.AllMessages,
                "keyword" => AgentTriggerMode.Keyword,
                "contextual" => AgentTriggerMode.Contextual,
                _ => AgentTriggerMode.Mentioned,
            },
            Keywords = keywords.ToList(),
        };

    /// <summary>全部可选包（不含导入态标注，仅目录）。</summary>
    public IReadOnlyList<MarketplacePack> Packs() => Builtin;

    public MarketplacePack? Get(string packId) => Builtin.FirstOrDefault(p => p.PackId == packId);

    /// <summary>把某包导入为当前用户的智能体；agentId 冲突自动改 ID 不覆盖。返回导入结果。</summary>
    public ImportResult ImportPack(string packId, string userId)
    {
        var pack = Get(packId) ?? throw new AguiProtocolException(ErrorCodes.BadRequest, "角色包不存在");
        return ImportDefinitions(pack.PackId, pack.Name, pack.Agents, userId);
    }

    /// <summary>
    /// 按一组智能体定义导入（内置包 / 远程分发包共用）：**两趟**处理——
    ///   1) 先为每个智能体分配不与现有目录冲突的新 ID（并避免包内互撞）；
    ///   2) 再把<b>包内</b>相互引用（指派 / 提升 / 交接 / 流水线步骤 / 技能目标）重映射到新 ID。
    /// 指向包外的引用保留原值（可能指向本实例已有对象）。归当前用户。
    /// </summary>
    public ImportResult ImportDefinitions(string packId, string packName, IReadOnlyList<AgentDefinition> agents, string userId)
    {
        var idMap = new Dictionary<string, string>(StringComparer.Ordinal);
        var reserved = new HashSet<string>(StringComparer.Ordinal);
        var clones = new List<AgentDefinition>();

        // 第一趟：定新 ID（克隆后不再改动共享对象）
        foreach (var src in agents)
        {
            var def = Clone(src);
            var newId = UniqueAgentId(src.AgentId, reserved);
            idMap[src.AgentId] = newId;
            reserved.Add(newId);
            def.AgentId = newId;
            clones.Add(def);
        }

        // 第二趟：重映射包内引用
        foreach (var def in clones) RemapReferences(def, idMap);

        var created = new List<AgentDefinition>();
        foreach (var def in clones)
        {
            def.OwnerId = userId;
            _catalog.Upsert(def);
            created.Add(def);
        }
        return new ImportResult(packId, packName, created.Count);
    }

    /// <summary>把包内相互引用重映射到新 ID（包外引用原值保留）。</summary>
    private static void RemapReferences(AgentDefinition d, IReadOnlyDictionary<string, string> map)
    {
        static string? MapOne(IReadOnlyDictionary<string, string> m, string? id)
            => id is null ? null : (m.TryGetValue(id, out var n) ? n : id); // 包外保留原值

        d.RelayToAgentId = MapOne(map, d.RelayToAgentId);
        d.EscalationAgentId = MapOne(map, d.EscalationAgentId);
        if (d.AssignmentIds is { Count: > 0 }) d.AssignmentIds = d.AssignmentIds.Select(a => MapOne(map, a)!).ToList();
        if (d.Pipeline is not null)
            d.Pipeline = d.Pipeline.Select(p => new AgentPipelineStep
            {
                StepAgentId = MapOne(map, p.StepAgentId) ?? p.StepAgentId,
                Prompt = p.Prompt,
            }).ToList();
        if (d.Skills is not null)
            foreach (var s in d.Skills)
                if (s.TargetAgentId is { } t && map.TryGetValue(t, out var nid)) s.TargetAgentId = nid;
    }

    private string UniqueAgentId(string baseId, ISet<string> reserved)
    {
        bool Taken(string id) => _catalog.GetDefinition(id) is not null || reserved.Contains(id);
        if (!Taken(baseId)) return baseId;
        for (var i = 2; i < 1000; i++)
        {
            var candidate = $"{baseId}_{i}";
            if (!Taken(candidate)) return candidate;
        }
        return baseId + "_" + IdGenerator.NewId();
    }

    private static AgentDefinition Clone(AgentDefinition d) => new()
    {
        AgentId = d.AgentId,
        Nickname = d.Nickname,
        Description = d.Description,
        Instructions = d.Instructions,
        Avatar = d.Avatar,
        TriggerMode = d.TriggerMode,
        Keywords = d.Keywords?.ToList(),
        Schedule = d.Schedule,
        Model = d.Model,
        BridgeEndpoint = d.BridgeEndpoint,
        BridgeMode = d.BridgeMode,
        BridgeToken = d.BridgeToken,
        PersonalMemoryEnabled = d.PersonalMemoryEnabled,
        IsPrivate = d.IsPrivate,
        OwnerId = d.OwnerId,
        RequireApprovalToolNames = d.RequireApprovalToolNames?.ToList() ?? [],
        Skills = d.Skills?.Select(s => new AgentSkillConfig { SkillId = s.SkillId, Description = s.Description, TargetAgentId = s.TargetAgentId }).ToList(),
        KnowledgeBaseIds = d.KnowledgeBaseIds?.ToList() ?? [],
        ImageLibraryIds = d.ImageLibraryIds?.ToList() ?? [],
        Pipeline = d.Pipeline?.Select(p => new AgentPipelineStep { StepAgentId = p.StepAgentId, Prompt = p.Prompt }).ToList(),
        RelayToAgentId = d.RelayToAgentId,
        AssignmentIds = d.AssignmentIds?.ToList() ?? [],
        EscalationAgentId = d.EscalationAgentId,
        // 随包携带：可复用技能引用与记忆拟人类型（都是配置；引用不存在的技能 id 在挂载时会被跳过）
        SkillDefIds = d.SkillDefIds?.ToList() ?? [],
        MemoryProfile = CloneMemoryProfile(d.MemoryProfile),
    };

    /// <summary>记忆拟人配置的深拷贝（可空）。</summary>
    private static MemoryProfile? CloneMemoryProfile(MemoryProfile? p) => p is null ? null : new MemoryProfile
    {
        MemoryType = p.MemoryType,
        StyleMode = p.StyleMode,
        PersonaCard = p.PersonaCard,
        TopK = p.TopK,
        PersonalTopK = p.PersonalTopK,
        MinScore = p.MinScore,
        PersonalMinScore = p.PersonalMinScore,
    };
}

/// <summary>导入角色包的结果。</summary>
public sealed record ImportResult(string PackId, string PackName, int AgentsCreated);
