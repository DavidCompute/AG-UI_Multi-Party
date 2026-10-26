# AG-UI 群聊扩展平台 — 产品路线图（Roadmap）

[English](ROADMAP.en.md) | **简体中文**

> ✅ 标记 = 已实现；🟡 = 部分实现。当前已完成：**1.1–1.4 / 2.1–2.5 / 3.1–3.3 / 4.1–4.4 / 5.1–5.4 / 6.1–6.4**；
> 路线图全部条目均已实现 🎉


> 本文档规划项目的**未来功能增长与完善方向**，并给出优先级建议，供排期与资源决策参考。
> 设计原则：只列**尚未实现 / 尚不完善**的能力；与已有能力严格区分，避免重复。
> 「目标模块」列出落地时主要涉及的代码位置，便于拆任务。

**图例**：★ 优先级（★★★ 最高）；每个条目含「现状 → 目标 → 落点模块」。

---

## 一、多智能体协作层

已有基础：触发规则（提及/全量/关键词/语境）、群内覆盖、技能（智能体间调用）、AG-UI 桥接、AI 分身、知识库。

### 1.1 智能体编排 / 多步工作流（★★★ 差异化最强） ✅已实现
- **现状**：技能是「单层子代理调用」——一次 run 带回一段答复，无多步协作。
- **目标**：规划 → 拆解子任务 → 子智能体并行/顺序执行 → 聚合 → 输出最终回复，形成类似编码助手的 plan/agent 循环。一个复杂需求可由代码 + 文档 + 测试三个助手协作完成。
- **落点模块**：`src/AguiGroupChat.Agents/`（会话与技能调用）、`AgentCatalog`（新事件：子任务状态）。
- **增强（已实现）**：确定性编排计划（`CoordinatorPlanning`）支持「问题 → 定计划 → 依次激活」；计划内多个<b>客户端执行技能</b>合并成「本机一键执行全部」卡一次确认；综合阶段<b>递归补查</b>——发现信息不足时继续调技能/派下属补齐，直到信息充分才给结论；纯技能型数字员工也能进入计划编排。
- **一键组织编排（已实现）**：`POST /ag-ui/agents/orchestrate` 根据一句话需求生成「数字员工组织架构 + 每岗技能 + 岗位连接」的<b>不落库预览</b>；`/orchestrate/stream` 以 SSE <b>逐 token 流式展示生成过程</b>并实时统计已见岗位/技能（事件 `token`/`progress`/`done`/`error`）；`/orchestrate/apply` 校验后<b>原子落库</b>技能 + 数字员工 + 连接，且可 `createSupportCircle=true` 把方案<b>一键组建为客服知聚</b>直接上线服务顾客。
- **自动编排重名去重（已实现）**：生成的数字员工 / 技能 id 与原库同名时自动追加 `_2/_3` 改名继续保存——不再整体失败、不覆盖已有资产，方案内引用（技能挂载、上下级连接、客服知聚成员、返回 id）同步映射到最终 id。
- **组织架构可编辑可视化（已实现）**：组织架构画布**双击数字员工节点直接打开编辑表单**；同一对端点间的多条关系连线**横向错开**避免完全重叠；编辑返回上下文优化（从架构进入则退出回架构，从列表进入则回列表）。
- **“组织架构构建师”走一键式出稿（已实现）**：挂 `org_design` 的组织角色（如 `org_architect`）另挂 `org_plan_draft`，复用「一键组织编排」同一引擎一次结构化产整支成稿（多 kind 技能、非纯 prompt），用户显式认可后再经 `org_commit` 落库。
- **组织构建自动配记忆人格（已实现）**：「一键组织编排」与内置「组织架构构建师」（`org_plan_draft`）共用同一生成引擎；从一句话需求起草团队时，模型按岗位职责为每个角色自动挑选记忆拟人预设（`memoryProfile`：broad/deep/slowToLearn/cueDependent/fastForgetting），如主管/组长→deep、一线高量/客服→broad、售后/客户成功/顾问→cueDependent、值班/快速查档→fastForgetting、重复例行→slowToLearn；解析容错，缺失时启发式兜底，否则 null（平台全局，向后兼容）。预览中逐员工显示「记忆:」标签，经 `apply`/`org_commit` 落库后新员工随带匹配的记忆类型。
- **组织连接双向归一（已实现）**：生成/提交的计划若只设单向链，系统会在解析侧（`AgentOrchestrator.NormalizeConnections`，原 `InferAssignments`）与提交侧（`OrgApplyEngine.NormalizeConnections`，原 `EnsureAssignmentsForLeaders`）**两个方向**都补——由「向上提升」（`escalationAgentId`）反推直接下级并入其 `assignmentIds`（任务指派，原有）；再由「向下指派」（`assignmentIds`）反推**唯一**直接上级补进其 `escalationAgentId`（问题提升，新增）。去重、保序、只增不减，带自环 / 二环保护，被多个上级指派的下级不猜上级——生成团队始终双向连通，不会退化成单向链。
- **编排落库前自检清单（已实现）**：`AgentOrchestrator.SelfCheck(plan, requirement)` 把「结构 / 连接 / 技能 / 交付闭环」的确定性判断汇总成一组条目（`OrgPlanCheck{code, level, message}`），由 `/orchestrate`（含流式 `done`）作为 `selfCheck` 下发给前端，预览里逐条渲染（❌ / ⚠️ / ✅）。覆盖：`no_agents`、`dangling_link`（连接指向不存在的岗位——落库会被拒）、`no_connections`（既无指派也无提升）、`no_assignment`（只有提升没有指派）、`no_escalation`（只有指派没有提升）、`self_escalation`、`agent_without_skill`、`delivery_gap` / `hollow_delivery` / `delivery_gatekeeper`，全通过时给 `ok` 摘要——把问题在**确认落库之前**逐条暴露（此前最多只显示一条 `deliveryWarning`）。
- **编排交付闭环适配内置产出技能（已实现）**：编排提示词与可复用技能小节都会点名内置产出技能——要 PPT → 引 `pptx_deck`；要 Excel / 表格 / 报表 → 引 `xlsx_book`；要 PDF → 引 `pdf_doc`；要 Word → 引 `docx_report`/`docx_gongwen`/`docx_notice`，并明令<b>不要</b>为这些已有能力另造只能写字的 prompt 空壳；预览侧 `DetectDeliveryGap` / `DetectHollowDeliverySkill` / `DetectDeliveryGatekeeper` 会提前给出 `deliveryWarning`。交付物判定口径统一为**明确格式词优先、中文泛称靠后**，因此「做份 PPT，含一张对比表格」判为演示文稿交付（而非 Excel），不会再因先撞上「表格」而误报缺 `xlsx_` 技能；交付“厚度”也按类型分别度量（pptx 看页数、xlsx 看数据行数、docx/pdf 看内容块数），不会拿错字段误判。
- **客户技能不误跑服务端 bash（已实现）**：`ExecutionLocation=Client` 技能与服务端/非 Windows 宿主下明显 PowerShell 正文得到“需本机/需 PowerShell 环境”的明确指引，不再出现 `Not running in PowerShell / command not found / 退出码2` 假报错。
- **内置产出技能矩阵已齐备（已实现）**：Word（`docx_gongwen` / `docx_notice` / `docx_report`）、Excel（`xlsx_book`：多工作表、公式优先、数字格式、合计行、冻结窗格与自动筛选、财务配色惯例，另有 `analyze` 模式读取已有工作簿并回报结构摘要）、PPT（`pptx_deck`）、PDF（`pdf_doc`：设计令牌驱动多文档类型与封面版式、内容块体系、Markdown 重排路由、中文字体自动探测并子集嵌入）。均为随程序集分发的 `dotnet` 内置技能，开箱即用；可分别用 `Agents:BuiltinDocxSkills` / `BuiltinPptxSkills` / `BuiltinXlsxSkills` / `BuiltinPdfSkills` 关闭播种。
- **交付物在线查看（已实现）**：消息里带 docx / xlsx / pptx 等办公文档附件的，卡片旁多一个「👁」图标（仅图标、悬浮提示「在线查看」）→ 服务端 LibreOffice 转 PDF 后**内联**返回（`GET /ag-ui/preview/{attachmentId}`），前端宽幅弹窗 iframe 直接阅读，不用下载、不用装本地 Office（PDF 附件直通不转换）。权限与下载**同一套校验**（401 / 403 / 已撤回不可读 / 404），转换产物按「附件 ID + 源文件指纹」缓存 7 天（实测首次 docx≈2.5s / xlsx≈1.7s / 4.4MB-16 页 pptx≈5.3s，二次≈15ms），并发串行 + 120s 超时；服务端未装 LibreOffice 时降级 503 并提示可下载后用本地应用打开。**PPT 有专门的幻灯片查看器**：PDF.js 逐页渲染，**备注跟随当前页显示在下方**（服务端从 `notesSlides` 抽取），并支持**全屏播放模式**（方向键翻页）。
- **计划步骤空产出重试与缺口清单（1.0.339，已实现）**：编排计划内的指班子步骤与服务端技能步骤若返回空正文，网关用同一会话 / 同一入参自动重试（默认 1 次，`planStepRetryOnEmpty` / `planStepEmptyRetryMax` 可热调）；仍为空则计划卡与调用链标「（未产出）」、不把占位符传给下游，并在最终回复末尾如实列出「哪些步骤没产出」，最终汇总也为空时改报缺口清单而不是返回空白——把“空回复”收敛成“部分交付 + 明确缺口”。
- **技能库试运行同样回档产物（已实现）**：`POST /ag-ui/skills/{skillId}/run` 返回 `attachments[]`（本次生出的文件），产物按与聊天回档**同一实现**校验入库并登记归属（产出者本人可读 / 可下载 / 可在线查看，他人 403）；产物不属于任何知聚消息，因此另设 `SkillRunArtifactStore`（持久化到扩展区，7 天 / 每人 50 条裁剪）。本机桥 / 客户端执行的试运行不回档（产物在用户自己机器上）。

### 1.2 角色间消息传递 / 交接（★★☆） ✅已实现（整轮角色交接）
- **现状**：智能体把另一智能体当「工具」单次取用，无双向协作语义。
- **目标**：智能体可直接「给某智能体发消息 / 请求接力回复」，支持协作接力而非单向调用。
- **落点模块**：`AgentGateway.cs`、`AgentDefinition`（协作字段）、事件目录新增协作事件。

### 1.3 重要记忆自动沉淀为知识库（★★☆ 强化「越用越懂」） ✅已实现
- **现状**：知识库需手动创建上传。
- **目标**：被反复引用或标记「关键」的群内结论，经模型聚合后自动/半自动写入知识库；让知识沉淀随对话自动发生。
- **落点模块**：`KnowledgeBaseCatalog.cs`、`MessageMemory`（importance 分级已有，作为沉淀触发源）、`TwinService`（复用聚合逻辑）。

### 1.4 定时 / 计划任务编排（★★☆） ✅已实现
- **现状**：`Agents:Schedule`（5 段 cron）已存在，但为单次定时汇报。
- **目标**：重复任务编排（每日周报 / 定时核对 / 到点催办）+ 任务化界面，形成「值班智能体」。
- **落点模块**：`Schedule` 扩展、`TaskApi.cs` / `Tasks`（已有任务表 `agui_tasks`）、前端任务面板。

---

## 二、记忆与知识层

已有基础：RAG（pgvector / sqlite-vec）、个人记忆、记忆分级 / 自动遗忘 / 可视化、知识库（异步入库）、**图谱记忆（Graph RAG，实体-关系子图注入）**。

### 2.1 混合检索（稀疏 BM25 + 稠密向量）（★★☆） ✅已实现
- **现状**：单一 embedding 模型。
- **目标**：引入 BM25 稀疏检索与稠密向量融合，提升中文 / 代码场景召回率；支持按需切换 embedding 提供方。
- **落点模块**：`MessageMemory`、`IMessageMemory`（检索聚合）、`SqliteVecMessageMemoryStore.cs` / `PgMessageMemoryStore.cs`。
- **图谱记忆（Graph RAG，已实现）**：`Memory.GraphEnabled` 开启后，从群消息抽取「实体-关系-实体」建图（PostgreSQL：`agui_graph_entities`/`agui_graph_edges` + pgvector 实体向量；SQLite：同表 + BLOB 向量 + 内存余弦），回复前语义召回种子实体 + 逐层 BFS 双向 n 跳遍历，子图与向量记忆并列注入 prompt（补强关系型知识）。<b>知识库同样建图</b>：上传文档时同步抽取实体/关系建入隔离域 `kb:{KbId}`，检索时对绑定知识库做图谱种子召回 + n 跳遍历，子图与向量切片并列注入。`IGraphMemory` / `IGraphMemoryStore` 接口 + `PgGraphMemoryStore` / `RelationalGraphMemoryStore` / `GraphMemory` / `GraphEntityExtractor`；仅在 `GroupChat.Memory.GraphEnabled=true` 且存储为 postgres/sqlite 时启用。

### 2.2 记忆时间线 / 版本化（★★☆） ✅已实现（时间线回放）
- **现状**：记忆仅记「最新」+ 过期删除，无演进回放。
- **目标**：按时间线回放「某主题结论如何演进」，服务复盘与审计。
- **落点模块**：`MessageMemory`（时间维度）、`MemoryMaintenanceService`。

### 2.3 跨实例记忆同步（★★☆ 打通桌面/Web 孤岛） ✅已实现（记忆即数据包 / 增量同步）
- **现状**：桌面版与 Web 各自本地记忆，互不相通。
- **目标**：记忆导出 / 增量同步到中心 Hub，或「记忆即数据包」便携迁移，复用现有 export/import 骨架。
- **落点模块**：`ExportImportApi.cs`、`IMessageMemory`、桌面 `DesktopApp` 同步钩子。
- **已实现**：
  - `IMessageMemory` 新增导出 / 导入（`ExportMemories` / `CountMemories` / `ImportMemoriesAsync`）：<b>记忆即数据包</b>——只导出文本元数据（messageId / 群 / 话题 / 发送者 / 内容 / 时间 / 分级 / 过期），向量在目标实例按各自 embedding 模型重算（支持跨实例不同向量维度）。
  - HTTP：`GET /ag-ui/memory/export?groupId=&since=&limit=&offset=`（可按群 / 时间下限增量导出，仅成员可见自己群；管理员可导任意）；`POST /ag-ui/memory/import`（批量导入，按 messageId 去重）。
  - 已含 3 个跨实例导出/导入测试（round-trip 迁移、幂等去重、sinceMs 增量过滤）。

### 2.4 知识库细化权限与溯源（★★☆） ✅已实现（群级共享 + 溯源已具备）
- **现状**：知识库为「创建者专属 + 系统级」，作答不标注来源。
- **目标**：群 / 成员级共享的集体知识库；回答中标注引用来源文档，增强可追溯性。
- **落点模块**：`KnowledgeBaseApi.cs`、`KnowledgeBaseCatalog`（检索回带 docId 引用）。

### 2.5 数字员工记忆拟人类型 / 按类型抽取（★☆☆ 拟人化体验） ✅已实现（1.0.121+）
- **现状**：数字员工共享同一份知聚语义记忆，所有员工“记性”一样（同一全局 TopK / 阈值）。
- **目标**：不同数字员工有不同记忆特征，且调取记忆时真的按特征抽取，更拟人。
- **已实现**：`AgentDefinition.MemoryProfile`（五档预设 + 口吻 + 人设 + TopK/阈值微调，null=兼容旧行为）；
  读取侧 `MemoryProfileTuning.Resolve` → `MemoryRetrievalTuning` 单次覆盖传给 store 检索，含存得住想不起型的回忆提示分支与快速遗忘型的 21 天近期窗口；写入侧 `MemoryProfileWritePolicy`
  仅对该员工本人发言生效（深记型自动刻为「重要」记忆、快速遗忘型在开启自动遗忘时保留更短）；编辑 UI 见
  「记忆与权限 → 🧠 记忆类型」。机制与调试详见 `docs/memory-personality.md`。另：组织编排 / 组织架构构建师起草团队时已会按岗位自动配好合适预设，缺失 / 非法预设安全回退（null=全局，向后兼容），机制详见 `docs/memory-personality.md` §4.4。
  - **周期巩固 / 遗忘（1.0.340，已实现）**：新增 `MemoryLifecycleService`——按记忆拟人类型扫描存量记忆并调整元数据：深记型旧普通记忆补巩固为重要并清除过期；难录入型在同内容重复达到阈值后巩固；快速遗忘型按缩短保留期收紧；其余按全局保留期补齐。只改 `importance`/`expires_at`，不改写原文、不直接删除；重要级不会被自动过期。管理端记忆管理新增状态与「立即巩固 / 遗忘」，并提供 `GET/POST /ag-ui/memory/lifecycle*`。配置：`LifecycleEnabled` / `LifecycleIntervalHours` / `LifecycleMaxPerRun` / `DeepConsolidateAfterHours` / `SlowToLearnRepeatThreshold`。

### 2.6 记忆写入去重（重复编码）（★☆☆ 成本 / 延迟） ✅已实现（1.0.262）
- **现状**：同一段文本每次写入都要重新向量化一遍（组织编排 / 定时汇报里反复出现的短句与模板文案、导入包里的重复内容），CPU 上单条可达数秒，且占着后台池那唯一的槽位。
- **目标**：写入侧对「完全相同的文本」只算一次向量，且**不改变召回结果**。
- **已实现**：新增 `Agents:Memory:WriteEmbeddingCacheSize`（默认 512，**0 = 关闭**）——写入向量化前先用**实际入库文本**（净化 + `MaxWriteChars` 截断后）查进程内 LRU 缓存 `MemoryEmbeddingCache`，命中即**复用向量、跳过 embedding**。相同文本在同一模型下向量恒等 → **语义等价**（召回结果一字不差、每条消息仍按自己的 messageId 落库可检索）；LRU 有界（默认约 2MB），只作用于写入 / 导入路径（检索 query 通常唯一、命中率低，不缓存）。新增指标 `knowgath_memory_embedding_cache_hits_total` / `_misses_total`，控制台「运行指标」显示命中占比。落点：`AguiGroupChat.Agents/MemoryEmbeddingCache.cs`、`AgentMessageMemory.ProcessWriteQueueAsync` / `ImportMemoriesAsync`、`MetricsService.RecordEmbeddingCache`。

---

## 三、AG-UI 桥接与生态开放

已有基础：standard / hub 双方言、HTTP / WS 双传输、审批中断回传、附件回灌。

### 3.1 桥接健康度与自动重连（★★☆） ✅已实现（健康度探测 + 能力协商 + 自动重连退避）
- **现状**：桥接失败仅广播 `RUN_ERROR` 并回灌。
- **目标**：端点健康探测、自动重连退避、断线补发，让「外部专家」更可靠。
- **落点模块**：`AgentGateway`（桥接分派）、`AguiBridgeClient` / `AguiBridgeHttpStandardClient.cs`。

### 3.2 桥接能力协商 Capability Discovery（★★☆） ✅已实现
- **现状**：方言 / 传输靠静态配置。
- **目标**：基于 AG-UI 协议做能力发现（支持哪些工具 / 附件 / 审批类型），减少人工配置。
- **落点模块**：`AgentGateway` 桥接链路、`AguiBridge*` 客户端。

### 3.3 智能体 / 技能市场（★★☆） ✅已实现（内置目录一键导入 + **在线分发**）
- **现状**：角色 / 技能靠 JSON 文件手动导入（`tools/agents-starter.json`）。
- **目标**：内置「行业角色 / 技能 / 知识库模板」下载市场，一键分发。
- **落点模块**：`AgentApi`、前端「智能体管理」、复用 starter JSON 打包结构。
- **在线分发（已实现）**：除内置角色包外，管理员可配置<b>在线目录源</b>（任意静态 JSON 地址），用户一键安装目录里的包；也可把自建数字员工<b>发布</b>为分包文件自行托管。合同为纯 JSON（目录索引 + 包 `schema=knowgath/marketplace-pack/v1`），无需专用后端。安装<b>两趟</b>处理（先分配新 ID，再把包内相互引用重映射），并做<b>安全净化</b>（清桥接端点 / 令牌、他实例用户组 id、私有标记）；拉取限制 http/https、2MB、10s、不开重定向、60s 缓存，坏源不影响聚合。前端：「AI 角色管理」工具栏「🏪 模板市场」。落点：`Agents/MarketplaceService.cs`（`ImportDefinitions`）、`Web/MarketplaceRemote.cs`、`Web/MarketplaceApi.cs`。
- **技能库搜索与批量删除（已实现）**：技能库新增 `prompt` / `shell` / `http` 三类可复用技能（`shell`/`http` 仅管理员创建），支持<b>按名称 / ID 搜索</b>（前端过滤）与<b>勾选批量删除</b>；`POST /ag-ui/skills/generate` 用自然语言生成技能定义（无需手写命令 / JSON）。

---

## 四、人机协同与治理（★★★ 企业落地硬门槛）

- **客服知聚顾客批准技能（已实现）**：在客服知聚（`kind=support`，见协议 §2.1.1）中，普通用户以<b>顾客参与者</b>身份进入（非成员），可批准<b>其本人触发</b>的客服技能执行——网关按 `targetMemberId`（触发者）强校验，顾客只能批自己那次交互（GroupHub `ResolveAgentInteractionAsync`），客服侧全员可见全部会话。
- **客服知聚 typing 与智能体上下文（已实现）**：客服 / 数字员工输入时顾客参与者能看到「客服正在输入」，顾客输入时客服可见，顾客之间互不可见（与消息隔离一致）；智能体上下文窗口包含本次触发顾客的隔离会话（顾客提问 + 定向回给该顾客的客服消息），客服能「记得」该顾客之前聊过、不再像新对话一样重答，其他顾客私聊不进上下文。
- **本机技能隧道执行（已实现）**：客户端（`executionLocation=client`）shell 技能当内网桥经隧道在线时直接沿<b>反向隧道</b>在本机执行而非下发前端（`NativeTunnel__Token` 配置令牌；`ClientToolTunnelRequireApproval` 默认 `true` 决定是否需触发者审批）。

### 4.1 审批策略差异化（★★★） ✅已实现
- **现状**：`Agents:RequireApprovalToolNames` 为全局名单（按工具名），粒度粗。
- **目标**：支持按智能体 / 按群 / 按金额或敏感度阈值的差异化审批策略；`approveAll` 已有，可细化控制粒度。
- **落点模块**：`ApprovalRequiredAIFunction` 包装逻辑、`AgentOptions`、HITL 决策流（`AgentGateway` / `HttpGroupApi` 的 interaction/resolve）。

### 4.2 细粒度 RBAC（★★★） ✅已实现
- **现状**：权限为 owner / admin / normal + 管理员标记（`IsAdmin` / `AdminUserIds`）。
- **目标**：频道级权限——谁能 @ 智能体、谁能审批、谁能管理知识库。
- **落点模块**：`AuthService`、`AuthOptions`、`GroupMember`（角色扩展）、各 API 鉴权检查。

### 4.3 操作审计日志（★★★） ✅已实现
- **现状**：HITL 卡有留痕，但无全局审计日志导出。
- **目标**：记录「谁 / 何时 / 批准了什么工具 / 导出导入 / 重置」等，可导出，满足涉密与合规。
- **落点模块**：事件广播通道、`agui_usage` 表扩展、`AdminApi` / 管理界面。

### 4.4 会话安全增强（★★☆） ✅已实现（多设备会话 + TOTP）
- **现状**：login 会话进程内 + 部分落库；无多设备管理。
- **目标**：多设备会话查看 / 吊销、可选的登录二次验证（TOTP）、`AllowedOrigins` / CSWSH 收紧的界面化配置。
- **落点模块**：`AuthService`、`AuthOptions`、`UserApi` / `AdminApi`、前端账户菜单。

---

## 五、话题、群与前端体验

### 5.1 跨话题主题关联（★☆☆） ✅已实现
- **现状**：话题各自独立。
- **目标**：「此主题还在哪个话题讨论过」的关联矩阵，帮助多人协作不漏上下文。
- **落点模块**：`GroupTopic`、`HttpGroupApi` 话题接口、前端话题栏。
- **已实现**：新增 `GET /ag-ui/group/{groupId}/topics/related?topicId=…`，按话题消息的分词共享关键词（Jaccard）计算关联分（>0.02、Top6），仅群成员可见（非成员 403）。

### 5.2 富媒体消息（★☆☆） ✅已实现（图片多选 + 语音 + 画布标注 + 语音问答 ASR）
- **现状**：附件支持办公文档已完善。
- **目标**：语音消息、图片多图上传、画布标注，贴近即时通讯习惯。
- **落点模块**：`AttachmentInfo`、`/ag-ui/upload`、前端渲染。
- **已实现**：
  - 后端附件新增 `audio` 类别（`audio/mpeg`/`wav`/`ogg`/`webm` 等），上传白名单放行，默认仅携元数据供前端播放（`AttachmentStore.IsExtractable` 对 audio 返回 false）；下载端点返回正确音频 MIME（range 支持）。
  - 输入端支持图片多选 / 拖拽；新增**语音消息**（MediaRecorder 录音 → 音频附件）与**画布标注**（canvas 绘制 → PNG 图片附件）；图片附件在消息内以平铺网格展示、输入区显示缩略图，音频以 `<audio>` 内联播放条展示。
  - **多模态进阶 · 语音问答（ASR，1.0.208）**：开启 `Agents:Audio:Enabled` 且能解析出转写端点时，服务端在组织附件区时先把语音转写为文字（OpenAI 兼容 `multipart/form-data`，响应 `{"text":"…"}`），再当作该附件「正文」走与文档附件同一条注入 / 预算路径；默认关闭，失败 / 超时 / 超限静默降级不阻断，按 attachmentId 缓存。对应实现 `AudioOptions` / `AudioTranscriptionService` 与 `AgentGateway.BuildAttachmentsSectionAsync`，回归 `AudioTranscriptionTests`。
  - **多模态进阶 · 语音进→语音出闭环（TTS，1.0.209）**：数字员工回复头部 🔊 用 Web Speech API（系统语音，离线）朗读，可开关自动朗读；发语音消息时其回复自动朗读。Docker 新增可选 `speaches` 服务（`--profile voice`，OpenAI 兼容转写 + 合成），前端默认转写端点已指向它。
  - **多模态进阶 · 服务端语音回复（TTS，1.0.210）**：开启 `Agents:Audio:SpeechEnabled` 后回复经 `SpeechSynthesisService` 合成为音频附件并挂到消息（复用技能产物回挂路径），覆盖主回复/流水线/交接/提升/审批恢复；默认关，失败静默。新增 `SpeechSynthesisTests` 与真实端点集成测试 `AudioIntegrationTests`。
  - **多模态进阶 · 独立 TTS 服务（1.0.211）**：服务端语音回复改用独立 Kokoro-FastAPI（`--profile voice` 一起拉起，镜像走南京大学镜像站，中文音色内置、CPU 可跑）；`SpeechSynthesisService` 仅改配置即接上（无需改代码）。实测中文合成→转写闭环通过。
  - **多模态进阶 · 语音服务独立运维入口（1.0.212–1.0.219）**：控制台把语音服务收敛为账号菜单 **「语音服务」** 独立面板（`#voiceServiceModal`）：并行拉取 `/ag-ui/admin/status` 与 `/ag-ui/admin/voice/voices`，展示 **ASR / TTS 就绪 / 未启用 / 不可达**三态 + 端点提示、逐音色 **「▶」试听**（复用单个 `<audio>`）、**「🔄 重新检测」**不关窗即重拉；语音消息**转写默认内联**并可**就地修正 + 一键按修正文本重问**；TTS 新增语速参数（`Agents:Audio:SpeechSpeed`）。「系统状态」只留一行跳转提示，后端端点无改动。

### 5.3 前端性能与无障碍（★☆☆） ✅已实现（虚拟滚动已具备 + ARIA 增强）
- **现状**：已做流式局部渲染 + 仅渲染最近 300 条。
- **目标**：虚拟滚动 + 消息懒加载应对超大规模群；ARIA 无障碍改进。
- **落点模块**：`wwwroot/app.js`。
- **已实现**：
  - 虚拟滚动与懒加载已成熟：消息窗口化渲染（`virtualRender` + 上下占位 spacer）、实测行高滚动锚定、≤200 条小表整渲染（`PLAIN_LIMIT`）、「加载更早消息」游标分页、单群 1200 条内存上限裁剪。
  - **ARIA 无障碍增强**：消息容器 `role="log" aria-live="polite" aria-relevant="additions"`、每条消息 `role="listitem"` + 发件人/时间/内容摘要标签；composer 图标按钮、输入框、画布绘图区补 `aria-label`；画布模态 `role="dialog" aria-modal="true"` + 打开聚焦 + Esc 关闭 + 关闭焦点回移；通知面板 `role="region"`。

### 5.4 应用内通知中心（★★☆） ✅已实现
- **现状**：无聚合通知。
- **目标**：WS 断线重连、被 @、审批待处理、定时任务结果 → 应用内通知 + 系统通知。
- **落点模块**：通知事件管道、前端通知中心。
- **已实现**：顶栏 🔔 通知按钮 + 下拉面板（含未读徽标）；聚合四类通知——**被 @ 提及**、**审批 / 输入待处理**、**WS 断线 / 重连**、**非当前视图新消息**（含定时任务的智能体广播消息）；点击通知跳转来源群，支持未读高亮、清空、Esc / 点外部关闭；页面隐藏时同步发系统桌面通知（复用 `Notification`）；登出清空。

---

## 六、可观测性与工程

### 6.1 结构化运行指标 / OpenTelemetry（★☆☆） ✅已实现（进程内指标 + **可视化看板**）
- **现状**：`/ag-ui/health` 仅连接 / 群计数；已有 `agui_usage` 表。
- **目标**：模型调用量、token 消耗、延迟、桥接故障率、记忆命中率的指标与可视化。
- **落点模块**：`/ag-ui/health`、`agui_usage` 写入点、`AgentGateway` 埋点。
- **可视化看板（已实现）**：`MetricsService` 除业务计数器外新增 `process` 节点（工作集 / GC 堆 / 线程 / 分配 / 句柄），控制台「运行指标」页升级为看板：**五张多序列图**（调用速率 / 成功-拒绝 / 桥接调用-失败 / 记忆命中-空检索 / 输出吞吐；按 60s 采样环做差分＝每分钟增量）、**成功率环**与**时间范围选择**（30 分 / 2 时 / 4 时）。落点：`Agents/MetricsService.cs`、`wwwroot/app.js`（`renderMetricsDashboard` / `renderSparkSvg` / `renderDonut`）。
- **Prometheus / OTel 导出（已实现）**：`GET /ag-ui/metrics`（`text/plain; version=0.0.4`，指标前缀 `knowgath_*`）——Prometheus 直接抓取，OTel Collector 可用 `prometheus` receiver 采集后转发；零新增依赖。默认需鉴权（`Observability:PrometheusScrapeToken` / 会话 / API 密钥），`PrometheusEnabled=false` → 404。落点：`Agents/MetricsService.RenderPrometheusText()`、`Web/MetricsApi.cs`。未完成：进程内 OTLP 直推（如需可后续加 SDK）。

### 6.2 Web 端多副本横向扩展（★☆☆） ✅已实现（Redis 共享存储 + **跨副本诊断**）
- **现状**：单进程 Kestrel。
- **目标**：Docker 场景多副本 + Redis 共享会话 / 存储（README 已预留 `IGroupStore` / `IUserStore` 可替换为 Redis / DB）。
- **实现**：新增 `Storage:Provider=redis`。`RedisContext`（连接复用与 key 约定）+ `RedisGroupStore` / `RedisUserStore` / `RedisTaskStore` / `RedisUsageStore` / `RedisAgentRegistryStore` / `RedisSectionStore`；登录会话经 `ISessionStore` 抽象（`RedisSessionStore`）跨副本共享，一台副本登录即可其余副本校验。多副本读写同一批 `agui:*` key 保持一致。
- **落点模块**：`src/AguiGroupChat.Hub/Persistence/Redis/`、`src/AguiGroupChat.Hub/Users/ISessionStore.cs`、`HubApp.ConfigureServices` 的 redis 分支。

### 6.3 配置治理 UI（★☆☆） ✅已实现
- **现状**：运维参数在 `.env` / appsettings。
- **目标**：管理面 UI 统一查看 / 调整 / 持久化（`AllowedOrigins`、`LinkProxy`、`WorkToolsEnabled`、数据库连接）。
- **落点模块**：`AdminApi`、`SystemApi`（`settings/model` 已有，可扩展）、前端管理面板。
- **已实现**：
  - 后端：`GET /ag-ui/admin/config`（现有只读快照，涵盖存储 / 模型 / 记忆等需重启项）+ `POST /ag-ui/admin/config`（写入运行时安全可改旋钮：会话有效期、群消息上限 / 群成员上限 / 单消息字符、消息保留天数、强制令牌、工具开关 / 工作型工具 / 思考模式 / 每日 Token 配额（按用户 / 按知聚）、审批名单、iframe 嵌入来源），非法值 400，持久化到扩展区「configGovernance」，重启自动应用覆盖；`GET /ag-ui/admin/config/governance` 读回当前覆盖值。
  - **配额修复与扩充（1.0.264）**：新增**按知聚每日 Token 配额**（`Agents:DailyTokenQuotaPerGroup`）——按用户配额刻意豁免 `system` 触发者，于是定时 / 广播那路消耗无人可管；按知聚配额不豁免，正好卡住它。并修复一个潜伏缺陷：两个配额原先在 `AgentUsageService` 构造时被拷贝成字段，导致「配置治理」热改配额**实际不生效**（仅重启生效）；现改为 `Func<long>` 实时读取，保存即生效。
  - **配额可见性（1.0.265）**：用量统计页新增「今日各知聚用量」明细（`/usage` 出参 `groupsToday[]`，含配额使用占比、达/超标红）；两处配额拦截补上可观测（`knowgath_quota_rejections_total{scope="user|group"}` + 运行指标页「配额拦截」卡片）。配额是「看得见才管得住」的典型。
  - **配额事前预警（1.0.266）**：`AgentUsageService.QuotaLevel` + `QuotaWarnRatio=0.8` 作为**单一阈值来源**；新增 `GET /ag-ui/admin/quota-status`，让**侧边栏知聚项**直接打标（`⚠` ≥80% / `🔴` ≥100%，仅 Operator+），用量页同时按水位配色并在 meta 汇总「接近上限 N 个 ｜ 已超限 M 个」——配额从「事后可查」进到「事前可见」。
  - **知聚级配额覆写（1.0.267）**：全局配额一刀切，但知聚开销差异大——`Group.Extra["quotaPerGroup"]`（零迁移，同模型覆写）让管理员只给**单个知聚**放额（含 `0` = 本知聚不限），`EffectiveGroupQuota` 作为单一口径供**校验与展示共用**；水位预警随之改成**每知聚各用自己的分母**。
  - **按用户水位 + 覆写审计（1.0.268）**：`/usage` 新增 `usersToday[]`，把同一套 `QuotaLevel` 用到**个人配额**（`system` 标为豁免）；`group.modelOverride` / `group.quotaOverride` 变更写审计（记改后实际值）。同版修复 3 处重复 i18n 键并给 CI 加**重复键守卫**（后者原本会静默覆盖前者）。
  - **用户列表水位角标（1.0.269）**：`GET /ag-ui/admin/users` 返回 `todayTokens` / `quota` / `quotaLevel`（一次聚合成字典，无 N+1），用户管理列表在用户名后直接打 `⚠` / `🔴`——至此同一水位口径在**侧边栏知聚项 / 用量页 / 用户列表**三处可见。
  - **推送型预警（1.0.270）**：首次跨过 80% / 100% 时发 `QUOTA_THRESHOLD` 事件到**出站 Webhook**（个人 / 知聚两作用域，每作用域每级每日一次；分母用生效配额、支持知聚覆写；未配配额零开销）。预警从「拉」变「推」，可直接接告警平台。
  - **审核修复（1.0.271）**：`Group.Extra` 改为**惰性非空**——修复旧数据 / 导入包缺省 `extra` 时覆写 setter 静默 no-op 的缺陷；配额阈值检查拆出独立 catch，避免与「用量记录失败」混淆。
  - **前端审核修复（1.0.272）**：`GROUP_UPDATED` 现在同步 `model` / `quotaPerGroup`（之前只同步群名 / 头像 / 私密，导致另一位管理员改完覆写后本端弹窗回填旧值）；水位值加白名单；用量页失败时三张表全清。
  - **临时放额（1.0.273）**：知聚配额覆写可带**到期时间**（`Group.Extra["quotaPerGroupUntil"]`），到期后**惰性回退全局**（无后台任务，读时判定；存储旧值保留供管理员查看）；取消覆写时额度与到期时间一并清除；`/usage` 行新增 `quotaUntil`，审计 detail 追加有效期。
  - **到期提醒 + 审计复盘（1.0.274）**：剩余 24 小时内每 15 分钟扫描一次，向出站 Webhook 发 `QUOTA_OVERRIDE_EXPIRING`（同一额度只发一次，事件目录 19 → 20）；审计页新增「⏰ 临时放额」快捷筛选（`/audit` 新增 `detail` 子串过滤，CSV 导出同口径）。
  - **提醒可配 + 到期回执 + 知聚筛选（1.0.275）**：提醒提前量 / 扫描间隔进配置治理（`quotaReminderLeadHours` / `quotaReminderScanMinutes`）；到期后 24 小时宽限期内补发 `QUOTA_OVERRIDE_EXPIRED`（事件目录 20 → 21）；审计页新增知聚下拉（`targetId`），可直看某知聚历次临时放额。
  - **跨重启去重 + 站内通道 + 到期模板（1.0.276）**：提醒去重键落盘（扩展区 `quotaReminderState`），重启不重复投递；用量页顶部新增站内提醒（`GET /ag-ui/admin/quota-reminders`，不依赖 Webhook）；知聚设置弹窗新增 `+2小时/+1天/+3天/+7天/清空` 到期模板。
  - **技能写入图库（1.0.277）**：技能级 `imageWrite`（默认关闭、仅管理员 + 服务端非 prompt）；平台注入 `imageWriteScopeId`（10 分钟、每轮 1 库 / 20 图 / 单图 10MB）；新增自调用端点 `/ag-ui/images/libraries[/{id}/assets]`（建库 + 传图，只能写本轮新建的库）；全程审计；生成器提示词已包含该能力。同版修复审计 ID 重启后撞主键导致新记录被静默丢弃的缺陷。
  - **授权已有图库 + 内容去重（1.0.278）**：技能可额外写入管理员在技能上选定的**已有图库**（`imageWriteLibraryIds`，支持跨归属；非管理员编辑时保留授权）；上传前按字节 SHA-256 去重（命中返回 `duplicated`，不占配额）；人工确认采用**配置层预审批**（能力默认关闭 + 逐库授权 + 配额 + 审计），逐次写入 HITL 留作二期。
  - **审批队列 + 来源标记 + 按技能配额（1.0.279）**：`imageWriteRequiresApproval` 开启后，写已有/授权库先入队（202 + pendingId），管理员在图库管理顶部批准/拒绝；新建库仍立即生效。技能写入的图片记录 `sourceSkillId` / `sourceAgentId`，图库支持来源筛选；每轮配额可按技能调（1..10 库 / 1..200 图）。
  - **审批队列跨重启（1.0.280）**：待审批元数据存扩展区 `imageWritePending`、图片字节存 `data/pending-writes/{id}.bin`（同一数据卷），重启后自动恢复并可继续批准；持久化关闭时退化为内存并清理超 TTL 孤儿文件。
  - **待审批写入主动感知（1.0.281）**：入队即推出站 Webhook `IMAGE_WRITE_PENDING`（事件目录 21 → 22，不绑定知聚）；管理员端每 60s 轮询把新项送进通知中心；图库管理新增「全部批准 / 全部拒绝」批量操作（逐条调用、单条失败不阻断）。
  - **审批回执 + 点击直达（1.0.282）**：批准 / 拒绝后推 `IMAGE_WRITE_RESOLVED`（事件目录 22 → 23，`status` = approved / rejected / failed，带 `assetId` / `duplicated` / `reason`）；待审批通知可点击直接打开图库管理，弹窗打开期间轮询顺带刷新列表。
  - **审批历史视图（1.0.283）**：图库管理显示「最近处理」20 条（状态 / 文件 / 来源 / 处理人 / 时间），新增 `GET /ag-ui/admin/image-write-pending/history`；队列进程内保留 50 条，重启清空（长期追溯走审计日志）。
  - **审批历史持久化（1.0.284）**：最近处理随持久化跨重启保持（扩展区 `imageWriteHistory`，与待审批队列共用注册入口）；持久化关闭时退化为进程内。
  - **Webhook 事件分组（1.0.285）**：订阅弹窗按 8 个领域分组（知聚 / 成员 / 消息 / 协作与工具 / 话题 / 运行 / 配额 / 图库），每组显示已选计数并支持全选 / 清空；未登记类型自动归入「其他」。
  - **最近处理筛选与清空（1.0.286）**：图库管理的「最近处理」支持按状态与关键词筛选（前端过滤，最多 50 条）；「清空历史」二次确认后仅清回执（`DELETE /ag-ui/admin/image-write-pending/history`，记审计），不影响待审批项与已落盘图片。
  - **投递记录筛选（1.0.287）**：控制台「最近投递」按事件类型与结果（成功 / 失败）筛选，显示「X / Y 条」；事件下拉来自实际投递（含 `PING`），前端筛选、最多展示 50 条。
  - **订阅表最近投递列（1.0.288）**：订阅行按 `subscriptionId` 关联最近 100 条投递，显示最近一次 ✅/❌ 与时间（悬浮带错误与尝试次数）；更早记录回落到 `lastDeliveredAt`（🕒）。
  - **失败可重试（1.0.289）**：批准失败时保留待审批项与字节（不再 TryTake），返回 404/400 并提示「仍待审批」；「最近处理」失败行标注并可一键「重试」。只有写成功（含重复命中）或拒绝才移除项。
  - **测试投递内联结果（1.0.290）**：点「测试」后行内先显「⏳ 测试中…」，完成后写回 ✅ 状态码 + 时间 + 耗时（或 ❌ 错误）；同时刷新「最近投递」列；修改 / 删除订阅时清除旧结果。
  - **投递记录 CSV 导出（1.0.291）**：按当前筛选导出时间 / 事件 / 结果 / HTTP 状态 / 尝试 / 耗时 / 地址 / 错误；UTF-8 BOM + RFC 4180，Excel 直接打开；无匹配时只提示不下载。
  - **投递记录持久化（1.0.292）**：最近 100 条投递写入 `webhookDeliveries` 扩展区（内存 / 文件 / PostgreSQL 同路径），重启后仍可查看 / 筛选 / 导出；每次投递标记脏位，下个冲刷周期落盘。
  - **投递失败主动告警（1.0.293）**：管理员每 5 分钟检查一次；已结算失败（> 40s）推通知中心，首次只建基线；同订阅 10 分钟冷却；点击直达 Webhook 页签；`PING` 不告警。
  - **告警参数可配（1.0.294）**：轮询间隔 / 结算窗口 / 同订阅冷却 / 总开关进「配置治理」，保存即重排；关闭后不轮询，重开重建基线（不补推关闭期间的失败）。
  - **桌面通知免打扰（1.0.295）**：配置治理新增 `HH:mm` 开始 / 结束（跨午夜支持），仅抑制桌面系统通知；站内通知中心与徽标照常；所有人可通过 `GET /ag-ui/notify-quiet` 读取。
  - **按类型静音（1.0.296）**：`notifyMutedTypes` 可分别静音提及 / 审批 / 错误·投递失败 / 提示 / 新消息 / 连接状态的桌面通知；未知类型 400；接口同时返回 `mutedTypes`。
  - **通知中心筛选（1.0.297）**：顶部「全部 / 提及 / 审批 / 错误 / 消息 / 其他」chip；筛选只影响列表，徽标仍为全部未读；无匹配时单独提示。
  - **桌面通知权限引导（1.0.298）**：未授权时通知面板提示（default 给「开启」、denied 给站点设置说明）；× 关闭后 7 天再提醒；不支持时隐藏。
  - **通知本地持久化（1.0.299）**：按用户写入 localStorage（100 条 / 7 天，含已读），刷新恢复；清空删副本；500ms 合并写入。
  - **通知单条操作（1.0.300）**：悬浮显示「标为未读 / 删除」；删除同步本地副本与技能错误备查；未读重新计入徽标。
  - **桌面版通知桥（1.0.301）**：WebView2 中 `Notification` 权限不可用 → 前端经 `chrome.webview.postMessage({type:"notify"})` 交给 WPF 宿主，用 `NotifyIcon` 托盘气泡展示（6s，点击/双击前置窗口）；免打扰与类型静音照常生效，普通浏览器行为不变。
  - **托盘右键菜单（1.0.302）**：托盘图标提供「打开知聚 / 静音 1 小时·取消静音 / 退出」；静音只关宿主气泡（站内通知中心与徽标照常），静音与恢复各弹一次确认；状态仅存进程内。
  - **托盘常驻入口（1.0.303）**：启动即建托盘；菜单增加「打开数据目录 / 重启本地服务 / 四档静音（30 分钟～到明天 08:00）/ 关闭窗口时最小化到托盘（持久化，默认关）」；重启保留多实例计数，失败可重试。
  - **未读徽标与开机启动（1.0.304）**：前端在写标签页标题时同步总未读消息数，托盘 tooltip / 菜单首项 / 图标红点（>99 显示 99+）实时更新；新增「开机时自动启动」（HKCU Run + `--autostart`，登录后静默进托盘）。
  - **任务栏集成与未读口径（1.0.305）**：未读 = 消息 + 通知中心（排除 message 类），tooltip 显示明细，任务栏图标叠加未读覆盖图；`ITaskbarList3` 缩略图按钮提供「打开 / 静音 1 小时·取消静音」，与托盘静音状态共享，不可用时静默降级。
  - **任务栏进度与跳转列表（1.0.306）**：执行计划 `done/total` → 任务栏进度（暂停色 / 无计划时不确定进度），结束清除；`ICustomDestinationList` 提供「最近知聚」分类（最多 7 个非私密，按活跃排序），点击以 `--group` 新窗口直达。
  - **跳转任务与进度细化（1.0.307）**：跳转列表增加「新建知聚 / 打开管理控制台」任务；进度 tooltip 显示当前计划步骤或运行中技能，无计划时按技能 / 工具调用完成比单调推进（封顶 95%）。
  - **托盘切换与窗口记忆（1.0.308）**：托盘「最近知聚」覆盖全部知聚（私密 🔒）并在当前窗口切换，新增新建知聚 / 管理控制台快捷项；窗口大小、位置与最大化状态持久化，显示器变化时安全回退。
  - **深链单窗口接管（1.0.309）**：第一个 UI 占用当前用户命名管道，跳转列表 / 快捷任务优先投递给已运行窗口（切知聚 / 开弹窗并前置），不再重复开窗与重复托盘图标；无窗口时照旧新开。
  - **工作型数字员工 + 未读聚合（1.0.310）**：`WorkToolsEnabled` + 智能体开关同时开启才挂载工作区工具（读 / 写 / 列目录 / 白名单命令），写与命令强制审批；桌面多窗口未读写入共享目录聚合到所有托盘 / 任务栏徽标。
  - **操作审计 + 容量治理 + 未读明细（1.0.311）**：`work_*` 每次调用记一条摘要（命令原文 / 路径 / 退出码 / 耗时），运行收尾追加到「执行计划」卡并随 `planJson` 落库；工作区按保留期与配额自动清理（写时增量校验）；托盘新增「未读窗口」子菜单，展开到窗口 → 知聚。
  - **审计可读 / 工作区可管 / 未读直达（1.0.312）**：审计从计划步骤拆出成 `audit[]`，按工具分组并默认只看异常；管理控制台新增「工作区」页（占用统计 + 按策略清理 + 写审计日志）；逐窗口深链管道让托盘点击其他窗口的知聚可直达。
  - **工作区可浏览 / 审计可导出 / 通知可定位（1.0.313）**：工作区页可列出并下载文件（路径越界一律 400）；计划卡审计一键导出 CSV；桌面通知点击经 `{group, topicId, messageId}` 深链定位到具体消息。
  - **工作区可预览 / 审计可汇总 / 通知可操作（1.0.314）**：文本与图片在工作区面板内直接看；最近 1–90 天的 `work_*` 操作跨消息汇总并可导出 CSV + 一键定位；通知改为带「打开 / 忽略」的浮窗。
  - **审计归档 / 工作区可治 / 通知定位（1.0.315）**：审计按月归档（`data/work-audit/audit-YYYY-MM.jsonl`，保留期可配），查询不再扫全量消息；工作区可删单文件 / 建目录（写审计日志）；通知中心每行可 `↗` 直接定位到消息。
  - **审计可筛可聚合 / 工作区可上传 / 浮窗可收敛（1.0.316）**：归档增量读取，知聚 / 员工 facets 筛选 + 按消息聚合；工作区面板可多选上传（单文件 50 MB）；通知面板可指定哪些类型弹浮窗。
  - **归档按天分片 / 工作区目录导航 / 通知批量操作（1.0.317）**：审计按天分片（保留期按天）、工作区逐层浏览（面包屑 + 当前目录内上传 / 新建 / 删除）、通知一键全部已读与按类型清空。
  - **工作区可搜 / 审计导出精细化 / 通知偏好跨设备（1.0.318）**：文件名搜索跨整个工作区；审计可按聚合口径导出或按月打包 ZIP；浮窗范围存服务端（`notifyPrefs`）不随设备变。
  - **内容搜索 / 归档可治理 / 通知规则化（1.0.319）**：工作区支持按内容搜索（行号 + 片段）；归档占用可见且可手动清理过期分片；通知可按知聚 / 发送者 / 关键词免打扰（仍进通知中心）。
  - **命中可定位 / 归档可知构成 / 通知模板化（1.0.320）**：内容搜索命中在预览中高亮并自动定位；归档统计给出工具分布与异常条数；通知范围可一键套用模板。
  - **工作区可批量 / 审计可看板 / 通知模板可自存（1.0.321）**：文件清单多选后可批量打包 ZIP 下载或批量删除（复用统一路径校验）；审计区新增按天堆叠柱图（含失败段，跟随筛选）；通知偏好可把当前设置存成个人模板（最多 10 个，随账号上云）。
  - **看板可换维度 / 整目录打包 / 模板可携带（1.0.322）**：柱图支持按天 / 按员工 / 按知聚切换；工作区可把当前目录（含子目录）整包下载；通知模板支持 JSON 导入导出，便于换机与团队共享。
  - **看失败率 / 拖拽上传 / 看板可导出（1.0.323）**：柱图叠加失败率折线（空桶断开）；文件与文件夹可直接拖进工作区，并可选择不覆盖同名；看板按当前维度导出 CSV。
  - **折线可关 / 柱体可下钻 / 上传有进度（1.0.324）**：失败率折线可隐藏并记住选择；点柱体直接筛出对应员工 / 知聚 / 日期的审计明细；文件夹上传显示进度条。
  - **下钻高亮 / 上传可取消 / 工具可筛（1.0.325）**：当前筛选柱体加描边高亮；上传中途可取消且不回滚已完成文件；审计可按命令 / 写入 / 读取 / 目录筛选，CSV 同步跟随。
  - **按工具维度 / 筛选视图 / 失败重试（1.0.326）**：看板新增第四种维度「按工具」并可点击下钻；天数 / 知聚 / 员工 / 工具 / 日期可存为命名视图（最多 20 个）；上传失败项可一键重传。
  - **断点续传 / 叠加员工 / 视图携带（1.0.327）**：大文件按 1 MB 分片续传（偏移自校正，2 天过期清理）；按天 / 按知聚的柱体可按员工拆段；筛选视图可导出 / 导入 JSON。
  - **员工段下钻 / 上传可暂停 / 视图上云（1.0.328）**：叠加看板点员工段直接筛表；上传支持暂停与继续（分片边界生效）；筛选视图存服务端（扩展区 `auditViews`），多设备一致。
  - **段内分布 / 暂停提示 / 默认视图（1.0.329）**：员工段悬停看工具构成；首次暂停弹提示；常用筛选可设为默认视图，打开即套用。
  - **一键还原 / 图表收敛 / 上传队列（1.0.330）**：筛选一键清回默认；下钻员工或知聚时看板只显示被选项；上传逐项显示排队与状态。
  - **选中重试 / 显示全部 / 视图搜索（1.0.331）**：失败项可勾选后只重传选中；看板可一键回到全量；视图下拉支持输入过滤。
  - **队列可搜可筛 / 视图可共享 / 看板可截图（1.0.332）**：上传队列按名称与状态过滤；个人视图可发布到共享池供其他运营复用；看板支持导出 PNG；并修复重启后通知偏好 / 视图丢失的持久化缺陷。
  - **团队默认 / PNG 完善 / 拖拽排序（1.0.333）**：共享视图可设为团队默认（个人默认优先）；PNG 带图例与失败率折线；上传队列可拖拽调整优先级。
  - **共享可导入 / PNG 带数值 / 队列按钮排序（1.0.334）**：视图 JSON 可直接发布到共享池；导出图写出失败率百分比；上传队列支持 ▲/▼ 按钮重排。
  - **共享用量 / PNG 适浅色 / 失败 CSV（1.0.335）**：共享视图显示套用次数（重启保留）；PNG 在浅色主题下换加深调色板；上传失败项可导出 CSV。
  - **冷门视图 / PNG 脚注 / 队列排序（1.0.336）**：共享视图按用量排序并提供冷门提示；导出图带时间与筛选脚注；上传队列可按名称 / 大小 / 状态排序。
  - **续传未完成 / PNG 备注 / 共享分组（1.0.337）**：失败项可看出已传进度并一键续传；导出图可写备注；共享视图按前缀自动分组。
  - **原因筛选 / 视图重命名 / PNG 图例合并（1.0.338）**：上传失败可按原因筛选；视图可重命名（团队默认跟随）；叠加图按前缀合并图例。
  - **模型路由（1.0.260）**与**记忆与向量化（1.0.263）**也已上页：前者 `ModelRouting` 四旋钮（启用 + 简单 / 标准 / 复杂档模型名），后者 `Agents:Memory` 的 `MaxQueryChars` / `MaxWriteChars` / `InteractiveEmbeddingWaitSeconds` / `BackgroundEmbeddingWaitSeconds` / `WriteEmbeddingCacheSize`——均为「检索慢 / 超时」的主旋钮，保存即生效（并发额度因闸门信号量启动定型，仍属需重启项）。
  - 前端：管理员控制台新增「配置治理」页签（参数网格 + 开关 + 审批/嵌入来源），加载回填（未设置项三态沿用默认）、保存即时生效、刷新。
  - **已含 2 个集成测试**（管理员更新持久化 + 非法值 400 / 非管理员 403）。
  - **配置键守卫（已实现）**：`tools/check-config-keys.mjs` 静态校验「compose / appsettings 的配置键 ↔ C# 选项属性」——把文档 §9.9 记录的「配了却没属性、被静默忽略」从**手工登记**变成**自动守卫**（白名单登记有意保留项，`--no-allowlist` 可自证 / 审计）。

### 6.4 嵌入 / 白标 / 对外 API（★☆☆） ✅已实现
- **现状**：接入走网页会话 token；已提供官方 .NET SDK 供第三方应用程序化接入。
- **目标**：iframe 嵌入第三方站点、品牌定制（Logo / 主题）、面向实现的 REST API 密钥与官方客户端 SDK。
- **落点模块**：`SystemApi`、`AuthService`（API key）、前端主题化、`AguiGroupChat.Sdk`。
- **已实现**：
  - **官方 .NET SDK（第三方接入）**：`src/AguiGroupChat.Sdk`——`AguiClient`（HTTP 上行：认证 / 群组 / 成员 / 话题 / 消息 / 多智能体讨论 / 人机交互 / 智能体操 / 附件）+ `AguiRealtimeClient`（WS 全双工 / SSE 下行 + 强类型事件分发）+ `Models`（与协议线格式一致的 DTO / 事件），`net8.0` / `net10.0`、零外部依赖；错误统一抛 `AguiException`（协议错误码 + HTTP 状态码）。示例 `samples/AguiGroupChat.Client`，端到端测试 `tests/AguiGroupChat.Sdk.Tests`（自托管真实 Hub，含 WebSocket 全链路）。
  - **对外 API 密钥**：`Auth:ApiKeys`（`[{apiKey, username}]`），`Authorization: Bearer <apiKey>` 免登录以绑定账号身份调用 HTTP API，继承其权限 / 管理员标记。**另提供托管密钥**（管理员控制台「API 密钥」页签）：创建 / 列出 / 吊销，明文仅创建时返回一次、服务端只存 SHA-256，跨重启持久化（扩展区 `apiKeys`），避免改一次配置就重启。落点：`Hub/Users/ManagedApiKeyStore.cs`、`Web/ApiKeyApi.cs`。
  - **白标品牌**：`GET/POST /ag-ui/settings/branding`（公开读 / 管理员写），配置应用名 + Logo + 品牌主色 + 强制深色 + 副标语，持久化到扩展区「branding」；前端以 CSS 变量注入主色、渲染登录页 / 顶栏 Logo 与应用名，管理菜单「白标设置」可在线编辑。
  - **iframe 嵌入**：`GroupChatOptions.AllowedFrameOrigins` 配置允许嵌入来源（CSP `frame-ancestors` 与 X-Frame-Options 相应放行，默认禁止）；前端自动检测 iframe / `?embed=1` 进入紧凑嵌入模式（隐藏无关按钮 / 副标题）。
  - **出站 Webhook（对外集成）**：管理员控制台「Webhook」页签配置回调地址 + 事件订阅（18 类事件；可按 `groupId` 限定知聚；密钥 `whsec_…` 仅创建 / 轮换时显示一次，落盘加密），知聚事件以带 **HMAC-SHA256 签名**的 HTTP POST 推送（信封 `{deliveryId, event, groupId, sentAt, data}`，头部 `X-KnowGath-Signature = sha256(HMAC(secret, "{timestamp}.{body}"))`）；失败重试 3 次（退避 1s/5s/25s），后台线程不阻塞扇出，与在线人数无关；「最近投递」+「测试」便于集成调试。管理 API：`/ag-ui/admin/webhooks(+/events,/deliveries,/{id}/test)`。落点：`IGroupEventObserver`（Hub 钩子）+ `WebhookApi` / `WebhookDispatcher`（Web）。
  - **版本化对外 REST API（入站集成）**：`/agui/v1`——外部系统用 API 密钥（或会话）主动发消息 / 读历史 / 同步等回复。`POST /agui/v1/groups/{id}/messages` 可带 `waitMs`（≤45000）长轮询，等该次触发的数字员工回复**完整**（正文非空且流已结束，`GroupHub.IsAgentMessageStreaming`）再返回（超时回半截并标 `complete:false`）；`GET /agui/v1/{me,groups,groups/{id}/messages,groups/{id}/messages/{mid},groups/{id}/replies}`。鉴权只接受真实身份并以该身份做成员 / 可见性校验。落点：`Web/ExternalApi.cs`。

- **Playwright 自动化验证（已实现）**：`tools/ui-orchestrate-flow.mjs` 用 Playwright 自动跑「一键组织编排 → 建客服知聚」全链路并截图（`tools/README-playwright.md`），覆盖 SSE 流式生成、apply 落库、客服知聚进入与会话隔离的前端验证。
- **运行期回归扫描（已实现）**：`tools/api-smoke.mjs`（后端 HTTP 只读主流面 46 项：路由 / 鉴权 / 序列化 / Prometheus 指标）与 `tools/ui-smoke.mjs`（前端控制台：未捕获运行时错误 + i18n 原文键泄漏；带总时长看门狗与注入式自检），均为只读、可重复跑，见 `tools/README-regression.md`。
- **静态守卫（已实现）**：`tools/check-config-keys.mjs`（配置键 ↔ C# 属性漂移）与 `tools/check-docs-links.mjs`（文档引用完整性：markdown 链接 + 反引号文件引用 + 行号越界）——**两者已接入 CI**（`.github/workflows/ci.yml`，秒级、无需起服务），推送 / PR 即自动阻止漂移。

### 6.5 数字员工绩效看板（★★☆ 运营洞察 / 反哺编排） ✅已实现（1.0.220）
- **现状**：`agui_usage` 已在记录按「日期 + 员工 + 触发者」的调用与 token；消息 👍👎 反馈也已在存；但**没有按员工的汇总视图**，编排选人仍靠静态角色描述。
- **目标**：按数字员工聚合绩效（调用量 / token / 好评率 / 最近活跃），为「一键编排选人」与组织体检提供实测依据。
- **已实现**：端点 `GET /ag-ui/admin/agent-performance?days=N`（Operator+，只读；名字取昵称、取不到回退 agentId）；控制台新增「员工绩效」页签（7/30/90 天窗口 + 刷新，按调用次数降序）；聚合为纯函数 `AgentPerformance.Aggregate`。**成本估算（1.0.221）**：按可配单价 `Agents:Pricing`（输入/输出每百万 token 单价 + 币种 + 每日预算）算成本列与预算已用（单价为 0 时显示「—」）。落点：`Web/AgentPerformance.cs`、`AgentOptions.PricingOptions`、`Web/AdminApi.cs`、`wwwroot/{index.html,app.js}`。
- **已实现（1.0.223 反哺）**：端点返回每人 `flags`（`low-praise` 反馈≥ 3 且好评率 < 60% / `idle` 窗口内无调用，确定性、可解释），控制台新增「关注」列与建议栏——把绩效变成可行动的运营信号。
- **已实现（1.0.224 组织体检报告）**：`GET /ag-ui/admin/org-health?days=N` 导出 Markdown（总览 / 关注项 / 绩效明细 / 组织架构 / 建议），纯函数 `OrgHealthReport.BuildMarkdown`。
- **已实现（1.0.228 反哺编排）**：`Agents:PerformanceFeedback`（默认关）——任务指派路由把候选的**近期好评率**作为参考注入提示词（`PerformanceRoutingHint.BuildLines`，仅列窗口内样本足够者）。采取「只给数据、不硬改写路由」的最小干预方式。
- **已实现（1.0.241–1.0.244 图表视图）**：控制台「员工绩效」页新增「📊 图表」→ `#orgHealthModal`，用 `org-health` 的结构化数据画 KPI 卡片 / Top 12 横向柱状 / 好评率环形 / 需关注项；**1.0.242** 补**按日趋势折线**（复用 `/admin/usage` 按日汇总）；**1.0.243** 折线支持**指标切换（调用次数 / Token / 成本）**——成本由 `org-health` 下发的单价（`inputPerMillion` / `outputPerMillion`）在浏览器内按日估算，未配置单价时按钮置灰；**1.0.244** 柱状与折线**共用该指标切换**（柱状按指标降序重排）；**1.0.245** 支持**导出 CSV**（全部员工明细，带 BOM 便于 Excel）；**1.0.246** 柱状图支持**维度切换（数字员工 / 触发者）**——「触发者」按用户聚合（`org-health` 新增 `users[]`），看「谁在消耗资源」；**1.0.247** 维度再加**知聚**（新增聚合表 `agui_usage_group`，主键含 group_id，**零迁移**）——看「哪个知聚最耗资源」（群维度自本版起记录）；**1.0.248** **CSV 导出跟随当前维度**；**1.0.249** **导出单文件 HTML 报告**（内联样式 + SVG，离线可开 / 打印为 PDF）。
- **已实现（1.0.250 反向入口）**：AI 角色管理每行新增「📊 看绩效」→ 跳到控制台「员工绩效」并定位 + 高亮该员工行（`data-agent-id` + `.perf-row-hit`），打通「发现→归因→编辑」。
- **已实现（1.0.251 环比 + 筛选）**：`org-health` 额外计算上一同长度窗口（下发布 `prev`），KPI 卡片 / Markdown 报告 / HTML 报告均带**环比**（▲/▼ + %）；绩效页新增**名称 / ID 搜索**与**「仅看需关注」**前端筛选（meta 显示「显示 N / 总数」）。
- **已实现（1.0.252 知聚范围）**：绩效页新增**知聚下拉**（`GET /ag-ui/admin/groups`）→ `/agent-performance?groupId=` 用量取自按知聚聚合表（仅列该群有用量的员工）；👍👎 / 好评率不按知聚记录故显示「—」。
- **已实现（1.0.253 单次回答用量）**：数字员工回复的「执行过程」新增「💰 本次用量」（输入/输出/思考 token + 估算成本），按本次运行（含子代理）汇总并随消息落库（`agui_messages.usage_json`，零迁移）。与「答案溯源」同属对话粒度可观测性（待续：RAG 检索来源引用）。
- **已实现（1.0.254 引用来源）**：回复的「执行过程」再加「📎 引用来源」（知识库片段 / 群记忆 / 个人记忆命中，可展开看摘录），在唯一检索注入处采样并随消息落库（`agui_messages.citations_json`，零迁移）；单次回答的透明度闭环（思考 / 计划 / 链路 / 来源 / 用量）。
- **已实现（1.0.255 会话导出）**：聊天头部「📥 导出本话题会话（Markdown）」——按分页接口向前翻页拉全当前话题历史（绕过客户端内存裁剪），导出含附件名 / 回复关系 / 数字员工回复的用量与引用来源；纯前端改动，无新增接口。
- **已实现（1.0.256 导出可选范围 / 格式）**：📥 改为弹窗——范围（当前话题 / 整个知聚）× 格式（Markdown / HTML）；HTML 为单文件、内联样式、无外部引用，可直接打印为 PDF。
- **已实现（1.0.257 编排自检清单 + 连接双向归一）**：`AgentOrchestrator.SelfCheck` 把结构 / 连接 / 技能 / 交付闭环逐条列出（`selfCheck` 随 `/orchestrate` 与流式 `done` 下发，预览逐条渲染 ❌/⚠️/✅），把“只有问题提升没有指派 / 没人能产出文件 / 交付岗是门卫”等在确认落库前暴露（不再是只显示一条 `deliveryWarning`）；`NormalizeConnections`（orchestrator + apply 两侧）把「指派 ⇄ 提升」双向补全（自环 / 二环保护、多上级不猜、只增不改）。
- **已实现（1.0.258 DeepSeek 全部统一 `deepseek-flash`）**：按官方文档口径，现网只有一个 Flash 模型名——“是否思考”改为**请求参数** `thinking.type`，由新增的 `AgentCatalog.DeepSeekThinkingPolicy`（`System.ClientModel` 的 `PipelinePolicy`）在 `chat/completions` 请求体内注入；`StructuredFastModel` / `ResolveDecisionModelName` / `ResolveModelName` 与 Docker/`appsettings` 默认值全部收敛到 `deepseek-flash`；判定 / 路由 / 严格 JSON / 视觉固定关思考，正式回复跟随 `Agents:ThinkingMode`。旧的 `deepseek-chat` / `deepseek-reasoner` / `deepseek-v4-flash-vision-exp` 引用全部清理。
- **待续**：若验证下来稳定，可考虑更强的候选排序权重（需防抖动，避免好评稀疏 / 滞后带来的误判）。

### 6.6 数字员工评测集 / 回归（★★☆ 质量守护） ✅已实现（1.0.222）
- **现状**：改提示词 / 换模型靠“凭感觉”，无金标准用例，能力退化无告警。
- **目标**：为数字员工建金标准用例（提问 → 期望要点），改动后一键跑分防退化。
- **已实现**：端点 `/ag-ui/admin/eval-cases`（CRUD + `run`，仅管理员）；跑分对每条用例用目标员工的「提示词 + 正式回复模型」跑一次（`AgentCatalog.CreateForEval`，不带工具 / 记忆 / 技能排除干扰），按**确定性关键词判据**（any/all，不用 LLM 当裁判）；控制台新增「评测回归」页签；用例经扩展区「evalCases」持久化。落点：`Agents/{EvalCase,AgentEvalRunner}.cs`、`Web/EvalApi.cs`、`wwwroot/{index.html,app.js}`。
- **跑分基线持久化（1.0.225）**：`EvalCase` 落库 `LastPassed`/`LastRunAtMs`/回复摘录（编辑用例即清空），页签刷新仍显「最近结果」，并并入组织体检报告「六、评测回归」（通过率 + 明细）。
- **评分趋势（1.0.226）**：历次跑分记录 `{atMs,total,passed}`（扩展区「evalRuns」，保留 200 次），「评测回归」页以固定 0–100% 纵轴绘通过率折线；`GET /ag-ui/admin/eval-cases` 返回 `runs`。
- **期望交付格式（1.0.227）**：`EvalCase.ExpectDeliverable`（docx/pptx/xlsx/pdf/text/image），判定除期望要点外还要求回复命中该格式同义词；**裸模型下校验「格式意图」，非真实出文件**。
- **待续**：在评测中**真正生成文件**（需带工具 / 技能运行，涉及审批自动放行）——较重，需先定安全口径。

### 6.7 按任务复杂度自动选模型（★★☆ 成本 / 速度） ✅已实现（1.0.223）
- **现状**：所有任务共用同一模型（思考模式决定推理/非推理），简单任务也用强模型，成本 / 速度浪费。
- **目标**：把已经很成熟的「复杂度自适应超时」同一套档位估计延伸到**模型选择**：简单任务用经济模型、繁重任务用强模型。
- **已实现**：`Agents:ModelRouting`（`Enabled` / `SimpleModel` / `HeavyModel`，**默认关**）；网关运行开始时安装复杂度预算，据此经 `AgentCatalog.GetOrCreateWithModel`（按 `agentId + 模型` 分键缓存，与视觉变体同机制）取得模型变体——**只换模型，工具 / 记忆 / 审批包装照旧**；关闭时行为与旧版完全一致。落点：`AgentOptions.ModelRoutingOptions`、`AgentCatalog.GetOrCreateWithModel`、`AgentGateway.ResolveRoutedModel`。
- **已实现（1.0.260 Standard 档 + 在线配置）**：新增 `ModelRoutingOptions.StandardModel`——Standard 档不再固定不换（留空 = 不换，向后兼容）；并在管理员控制台**「配置治理」页在线暴露** `ModelRouting` 四个旋钮（`#cfgModelRouting` + `#cfgRoutingSimple/Standard/Heavy`，`ConfigGovernanceState` / `HttpRequest` / `Apply` / 持久化全链），保存即时生效且重启后仍生效（无需改 appsettings / env）。
- **已实现（1.0.261 按知聚覆写）**：知聚级模型覆写 `Group.Extra["model"]`（同 `Kind`，零迁移）——管理员在「知聚设置」给某个知聚**钉一个模型**，优先级**高于**按复杂度路由与本知聚内智能体默认模型；服务端限平台管理员（`HttpGroupApi` `/update` 含 `model` 时 `ResolveRole < Admin` → 403）。落点：`Group.ModelOverride`、`GroupHub.GroupModelOverride`、`AgentGateway.ResolveGroupModel`。顺带修复 `Postgres/Relational.UpdateGroup` 未写 `extra` 列的存储缺陷。
- **待续**：按租户（多租户）覆写模型。

---

## 优先排期建议（★★★ 优先）

| 优先级 | 方向 | 理由 |
|---|---|---|
| ★★★ | 4.1 审批策略差异化 + 4.2 细粒度 RBAC | 企业落地的硬门槛，改动集中在既有权限 / 审批模块，性价比高 |
| ★★★ | 4.3 操作审计日志 | 政务 / 金融场景刚需，复用现有事件广播做留痕 |
| ★★☆ | 1.1 智能体编排 / Pipeline | 从「各专家各自回话」升级为「协同解题」，差异化最强 |
| ★★☆ | 1.3 重要记忆自动沉淀知识库 | 降低维护成本、强化「越用越懂」，技术底座已具备 |
| ★★☆ | 2.3 / 3.1 跨实例记忆同步 + 桥接重连 | 打通桌面/Web 孤岛，提升外部专家可靠性 |
| ★☆☆ | 6.1 可观测性 | 提升运维与调优能力，成本较低 |

> **里程碑建议**：路线图 1.1–6.4 已全部落地，近期又新增<b>一键组织编排（含 SSE 流式生成）</b>、<b>客服知聚一键创建</b>、<b>本机技能隧道执行</b>、<b>顾客在客服知聚可批准技能</b>、<b>技能库搜索 / 批量删除</b>、<b>客服知聚（kind=support）</b>、<b>客服知聚 typing 与智能体上下文</b>、<b>自动编排重名去重</b>、<b>组织架构双击编辑与连线防重</b>、<b>数字员工记忆拟人类型（2.5）</b>、<b>记忆写入去重・重复编码（2.6）</b>与<b>Playwright 自动化验证</b>；后续按运营反馈迭代优化（如 Redis 分片 / Redis 集群、可观测性增强、更多企业合规、每员工记忆副本），见主 README 与 MARKETING「下一步」展望。

---

## 说明与界限

- **近期新增功能的评审记录见 [`docs/feature-review-2026-10.md`](docs/feature-review-2026-10.md)**（1.0.220–1.0.231 的能力清单、审计发现与修复、偏差清理、未验证项）。

- 本文档**不重复**已实现能力；每条的「目标」均指向较当前版本的新增或补齐。
- 落地前请回到代码核对（各「落点模块」为入口），并以主 `README.md` 与协议标准文档为准。
- 若想深入某一条的设计（如 4.1 审批策略差异化的字段 / 数据模型 / 接口改动），可据此进入详细设计。
