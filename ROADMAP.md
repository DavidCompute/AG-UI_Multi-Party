# AG-UI 群聊扩展平台 — 产品路线图（Roadmap）

[English](ROADMAP.en.md) | **简体中文**

> ✅ 标记 = 已实现；🟡 = 部分实现。当前已完成：**1.1–1.4 / 2.1–2.4 / 3.1–3.3 / 4.1–4.4 / 5.1–5.4 / 6.1–6.4**；
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
- **客户技能不误跑服务端 bash（已实现）**：`ExecutionLocation=Client` 技能与服务端/非 Windows 宿主下明显 PowerShell 正文得到“需本机/需 PowerShell 环境”的明确指引，不再出现 `Not running in PowerShell / command not found / 退出码2` 假报错。

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

### 3.3 智能体 / 技能市场（★★☆） ✅已实现（内置目录一键导入）
- **现状**：角色 / 技能靠 JSON 文件手动导入（`tools/agents-starter.json`）。
- **目标**：内置「行业角色 / 技能 / 知识库模板」下载市场，一键分发。
- **落点模块**：`AgentApi`、前端「智能体管理」、复用 starter JSON 打包结构。
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

### 5.2 富媒体消息（★☆☆） ✅已实现（图片多选 + 语音 + 画布标注）
- **现状**：附件支持办公文档已完善。
- **目标**：语音消息、图片多图上传、画布标注，贴近即时通讯习惯。
- **落点模块**：`AttachmentInfo`、`/ag-ui/upload`、前端渲染。
- **已实现**：
  - 后端附件新增 `audio` 类别（`audio/mpeg`/`wav`/`ogg`/`webm` 等），上传白名单放行，仅携元数据供前端播放、不注入模型文本上下文；下载端点返回正确音频 MIME（range 支持）。
  - 输入端支持图片多选 / 拖拽；新增**语音消息**（MediaRecorder 录音 → 音频附件）与**画布标注**（canvas 绘制 → PNG 图片附件）；图片附件在消息内以平铺网格展示、输入区显示缩略图，音频以 `<audio>` 内联播放条展示。

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

### 6.1 结构化运行指标 / OpenTelemetry（★☆☆） ✅已实现（进程内指标）
- **现状**：`/ag-ui/health` 仅连接 / 群计数；已有 `agui_usage` 表。
- **目标**：模型调用量、token 消耗、延迟、桥接故障率、记忆命中率的指标与可视化。
- **落点模块**：`/ag-ui/health`、`agui_usage` 写入点、`AgentGateway` 埋点。

### 6.2 Web 端多副本横向扩展（★☆☆） ✅已实现（Redis 共享存储）
- **现状**：单进程 Kestrel。
- **目标**：Docker 场景多副本 + Redis 共享会话 / 存储（README 已预留 `IGroupStore` / `IUserStore` 可替换为 Redis / DB）。
- **实现**：新增 `Storage:Provider=redis`。`RedisContext`（连接复用与 key 约定）+ `RedisGroupStore` / `RedisUserStore` / `RedisTaskStore` / `RedisUsageStore` / `RedisAgentRegistryStore` / `RedisSectionStore`；登录会话经 `ISessionStore` 抽象（`RedisSessionStore`）跨副本共享，一台副本登录即可其余副本校验。多副本读写同一批 `agui:*` key 保持一致。
- **落点模块**：`src/AguiGroupChat.Hub/Persistence/Redis/`、`src/AguiGroupChat.Hub/Users/ISessionStore.cs`、`HubApp.ConfigureServices` 的 redis 分支。

### 6.3 配置治理 UI（★☆☆） ✅已实现
- **现状**：运维参数在 `.env` / appsettings。
- **目标**：管理面 UI 统一查看 / 调整 / 持久化（`AllowedOrigins`、`LinkProxy`、`WorkToolsEnabled`、数据库连接）。
- **落点模块**：`AdminApi`、`SystemApi`（`settings/model` 已有，可扩展）、前端管理面板。
- **已实现**：
  - 后端：`GET /ag-ui/admin/config`（现有只读快照，涵盖存储 / 模型 / 记忆等需重启项）+ `POST /ag-ui/admin/config`（写入运行时安全可改旋钮：会话有效期、群消息上限 / 群成员上限 / 单消息字符、消息保留天数、强制令牌、工具开关 / 工作型工具 / 思考模式 / 每日 Token 配额、审批名单、iframe 嵌入来源），非法值 400，持久化到扩展区「configGovernance」，重启自动应用覆盖；`GET /ag-ui/admin/config/governance` 读回当前覆盖值。
  - 前端：管理员控制台新增「配置治理」页签（参数网格 + 开关 + 审批/嵌入来源），加载回填（未设置项三态沿用默认）、保存即时生效、刷新。
  - 已含 2 个集成测试（管理员更新持久化 + 非法值 400 / 非管理员 403）。

### 6.4 嵌入 / 白标 / 对外 API（★☆☆） ✅已实现
- **现状**：接入走网页会话 token；已提供官方 .NET SDK 供第三方应用程序化接入。
- **目标**：iframe 嵌入第三方站点、品牌定制（Logo / 主题）、面向实现的 REST API 密钥与官方客户端 SDK。
- **落点模块**：`SystemApi`、`AuthService`（API key）、前端主题化、`AguiGroupChat.Sdk`。
- **已实现**：
  - **官方 .NET SDK（第三方接入）**：`src/AguiGroupChat.Sdk`——`AguiClient`（HTTP 上行：认证 / 群组 / 成员 / 话题 / 消息 / 多智能体讨论 / 人机交互 / 智能体操 / 附件）+ `AguiRealtimeClient`（WS 全双工 / SSE 下行 + 强类型事件分发）+ `Models`（与协议线格式一致的 DTO / 事件），`net8.0` / `net10.0`、零外部依赖；错误统一抛 `AguiException`（协议错误码 + HTTP 状态码）。示例 `samples/AguiGroupChat.Client`，端到端测试 `tests/AguiGroupChat.Sdk.Tests`（自托管真实 Hub，含 WebSocket 全链路）。
  - **对外 API 密钥**：`Auth:ApiKeys`（`[{apiKey, username}]`），`Authorization: Bearer <apiKey>` 免登录以绑定账号身份调用 HTTP API，继承其权限 / 管理员标记。
  - **白标品牌**：`GET/POST /ag-ui/settings/branding`（公开读 / 管理员写），配置应用名 + Logo + 品牌主色 + 强制深色 + 副标语，持久化到扩展区「branding」；前端以 CSS 变量注入主色、渲染登录页 / 顶栏 Logo 与应用名，管理菜单「白标设置」可在线编辑。
  - **iframe 嵌入**：`GroupChatOptions.AllowedFrameOrigins` 配置允许嵌入来源（CSP `frame-ancestors` 与 X-Frame-Options 相应放行，默认禁止）；前端自动检测 iframe / `?embed=1` 进入紧凑嵌入模式（隐藏无关按钮 / 副标题）。

- **Playwright 自动化验证（已实现）**：`tools/ui-orchestrate-flow.mjs` 用 Playwright 自动跑「一键组织编排 → 建客服知聚」全链路并截图（`tools/README-playwright.md`），覆盖 SSE 流式生成、apply 落库、客服知聚进入与会话隔离的前端验证。

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

> **里程碑建议**：路线图 1.1–6.4 已全部落地，近期又新增<b>一键组织编排（含 SSE 流式生成）</b>、<b>客服知聚一键创建</b>、<b>本机技能隧道执行</b>、<b>顾客在客服知聚可批准技能</b>、<b>技能库搜索 / 批量删除</b>、<b>客服知聚（kind=support）</b>、<b>客服知聚 typing 与智能体上下文</b>、<b>自动编排重名去重</b>、<b>组织架构双击编辑与连线防重</b>与<b>Playwright 自动化验证</b>；后续按运营反馈迭代优化（如 Redis 分片 / Redis 集群、可观测性增强、更多企业合规），见主 README 与 MARKETING「下一步」展望。

---

## 说明与界限

- 本文档**不重复**已实现能力；每条的「目标」均指向较当前版本的新增或补齐。
- 落地前请回到代码核对（各「落点模块」为入口），并以主 `README.md` 与协议标准文档为准。
- 若想深入某一条的设计（如 4.1 审批策略差异化的字段 / 数据模型 / 接口改动），可据此进入详细设计。
