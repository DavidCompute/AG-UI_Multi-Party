# 数字员工执行配置：可热改项 / 生效范围 / 与固定逻辑边界
# Digital-Employee Execution Configuration: hot-tunable knobs, scope, and what stays hard-coded

> 平台为数字员工（泛指被触发一条消息后从“判定触发→分派→产出回复”的全流程）开放了**两层**执行期覆盖：**平台级（P）**与**角色级（R）**——平台级让整个平台统一改一套时序/重试/TTL 与阶段开关；角色级针对单个数字员工覆盖其桥接/交接/组织路由是否需要关闭。下面列出**每一条当前到底能配什么、默认是什么、配了去哪儿生效、还有哪些改不了（码内固定）**。
> 只读你的环境变量/Docker 时：下面 P 层全部可用（Docker 容器 `docker compose up --build` 后经浏览器「管理员 → 执行参数」热调，也可直接写 `Agents:Execution`）；生效由共享单例承载——**保存立即对后续调用生效，不重启**。

## 代码锚 Code anchors
- 平台级可热改载体：`src/AguiGroupChat.Agents/ExecutionOptions.cs`（`Agents:Execution`）
- 管理员热改端点：`src/AguiGroupChat.Web/ExecutionRuntimeApi.cs`（`GET / POST /ag-ui/admin/execution`）
- 网关消费：`src/AguiGroupChat.Agents/AgentGateway.cs`（时序、分派、角色级 Disable）
- 角色级覆盖存储：`AgentDefinition` → `AgentApi` 读写（`DisableBridge/DisableRelay/DisableOrgRoute`）

---

## 摘要一页看（One-page summary）

| 配置面 | 谁能改 | 生效时机 | 入口 |
|---|---|---|---|
| P：执行参数（时序/重试/TTL/阶段开关/顺序/复杂度自适应超时） | 仅系统管理员 | 立即 + 持久化 | `管理员控制台 → 执行参数` 或 `appsettings Agents:Execution` |
| R：本角色禁用某执行阶段 | 创建者/系统管理员 | 角色保存后下次触发生效 | `数字员工管理 → 编辑 → 执行阶段` |

> P 与 R 的并语义：平台 `EnableXxx=false` **或** 该角色 `DisableXxx=true`，二者任一成立即跳过该阶段。

---

# A · 平台级（Platform-level）运行参数
# A · Platform runtime knobs

以下每一项都对应 `ExecutionOptions` 的一个成员；大写字段名即 `ExecutionRuntimeApi` 返回/接收的 JSON 小驼峰键（如 `streamTimeoutMinutes`）。**输入 ≤0 / 拼错 / 越界在保存时会按“回退默认 + warn 日志”夹紧**，绝不把废值带进运行。

| # | 字段 Field | 平台默认 Default | 含义 Meaning | 涉及代码路径 Code path |
|---|---|---|---|---|
| 1 | `streamTimeoutMinutes` | 5 | 单次模型/桥接流式的最长运行（防挂起占住 Task） | 每次流式、桥接 |
| 2 | `maxModelAttempts` | 2 | 本地模型可重试上限（429/5xx/连接重置） | 本地 run 重试 |
| 3 | `interactionTtlMinutes` | 10 | 待“人机交互审批”的超时，到点由周期清理 | 审批中断表 |
| 4 | `sessionLockTtlMinutes` | 30 | 会话锁空闲自动释放阈值 | 同一消息/会话串行锁 |
| 5 | `approvedSkillTtlMinutes` | 30 | “已批准客户端技能”授权记忆有效时长 | 客户端/桥执行的批准状态 |
| 6 | `sessionLockMaxEntries` | 512 | 会话锁表条目上限（兜底即时清理） | 锁表 |
| 7 | `coordinatorPlanMaxItems` | 12 | 确定性协调计划最多纳入清单项 | `ExecuteCoordinatedPlanAsync` |
| 8 | `coordinatorPlanMaxSteps` | 8 | 协调计划单次最多步骤 | 同上 |
| 9 | `maxRecursiveRounds` | 5 | 递归综合补查最多轮次（防死循环） | `ExecuteRecursiveAnswerAsync` |
| 10 | `maxRouteDepth` | 4 | 指派/提升路由最大层数（防病态深链） | `InvokeAssignmentEscalationAsync` |
| 11 | `maxInteractionRounds` | 15 | 同一消息最多允许的**人工审批**轮数（只数“真的打断了用户”的那一轮；已同意技能 / 批量批准这类自动放行不计入） | 审批 / 恢复循环 |
| 11b | `maxAutoApprovedRounds` | 30 | 同一运行最多允许的**自动放行**工具调用次数（两道独立防线，避免真正失控循环） | 审批 / 恢复循环 |
| 12 | `executionOrder[]` | `bridge,pipeline,relay,org_route,streaming` | 分派阶段判定顺序；白名单，`streaming` 恒置末 | `InvokeCoreAsync` |
| 13 | `enableBridge` | true | 平台是否启用“AG-UI 桥接”阶段 | 网关 switch(bridge) → `InvokeBridgeAsync` |
| 14 | `enablePipeline` | true | 平台是否启用“编排流水线”阶段 | case pipeline → `InvokePipelineAsync` |
| 15 | `enableRelay` | true | 平台是否启用“整轮交接”阶段 | case relay → `InvokeRelayAsync` |
| 16 | `enableOrgRoute` | true | 平台是否启用“组织化路由”阶段 | case org_route → 指派/提升/协调 |
| 17 | `complexityTimeouts` | 见 §A.1 | **复杂度自适应超时**：按触发消息估出的任务量级，在 `streamTimeoutMinutes` 之上乘倍率并夹上界 | `RunTimeoutPolicy` / `RunComplexityEstimator`（所有运行入口） |

真实生效顺序 = `ExecutionOrder`（过滤去重后）；`streaming` 无条件下沉为兜底：若前面阶段都不命中/被关，就以普通带工具流式 run 收尾。

---

## A.1 复杂度自适应超时（`complexityTimeouts`）

固定 5 分钟对“寒暄”过宽、对“41 页带插图的 PPT”过窄（实测撞到过「一次提交 41 页 + 长备注，服务端处理超限」）。
因此运行主预算改为 **`streamTimeoutMinutes` × 档位倍率**，并以 `maxRunTimeoutMinutes` 兜住上界。

**档位怎么估**（`RunComplexityEstimator`：纯函数 + 确定性；只吃触发消息正文**前 2000 字**与附件元数据，不读正文内容、不做 IO）：

| 信号 Signal | 权重 Weight |
|---|---|
| 交付物格式（docx/pptx/xlsx/pdf/ppt/word/excel/幻灯片/演示文稿/文档/报告/方案/白皮书/说明书/手册/纪要/周报…） | +2 |
| 页数 `N 页` | ≥20 → +3、≥8 → +2、≥3 → +1 |
| 字数 `N 字` / `Nk 字` | ≥5000 → +3、≥1500 → +2、≥500 → +1 |
| 条目数 `N 个/条/项/张/篇/款/章节/岗位…` | ≥10 → +2、≥4 → +1 |
| 多步措辞（首先/然后/接着/最后/分别/逐个/逐条/逐项/依次/每页/每个…） | 每处 +1，上限 +3 |
| 穷尽性措辞（完整/详细/详尽/全面/深入/全套/尽可能/丰富…） | +1 |
| 文档类附件（kind = text / document） | 每个 +1，上限 +3 |
| 附件总字节 ≥2MB | +1 |
| 图片附件 ≥5 张 | +1 |
| 该角色会向下指派（`AssignmentIds` 非空）或有编排流水线（`Pipeline` 非空） | +1 |

档位分界：`0–1` **简单**、`2–3` **常规**、`4–6` **复杂**、`≥7` **繁重**。

> 为何每条规则都要求“数字 + 量词”或明确的多步措辞：普通用词（如随口提一句“报告”）不该直接把预算抬到十几分钟。先把量级信号做硬，再谈放宽。

**倍率与上界**（这些字段本身也在同一页可热改，键名为小驼峰）：

| 字段 Field | 默认 Default | 含义 Meaning |
|---|---|---|
| `enabled` | true | 关掉后所有运行一律用 `streamTimeoutMinutes` 原值（行为等价旧版） |
| `standardMultiplier` | 1.5 | 常规档倍率（允许 1–4） |
| `complexMultiplier` | 2 | 复杂档倍率（1–6） |
| `heavyMultiplier` | 3 | 繁重档倍率（1–10） |
| `maxRunTimeoutMinutes` | 30 | 单次运行主预算绝对上界（1–1440） |
| `maxSkillTimeoutMs` | 300000 | 内置文档类技能预算上界（毫秒，1000–3600000） |
| `maxClientSkillTimeoutSec` | 600 | 客户端（本机桥）技能单次调用等待上界（秒，10–3600） |

**连带放宽的三处**（避开“主预算放开了、某一层先掐断”的短板）：

1. **内置文档类技能**（docx/pptx/xlsx/pdf）的单技能预算：`builtinSkillTimeoutMs` × 同倍率，夹在「既有值」与 `maxSkillTimeoutMs` 之间。用户自建技能（`dotnetSkillTimeoutMs`）**不**放宽——正文不受控，且宿主是同步等待，拖长只会白占线程。
2. **客户端（本机桥）技能**的隧道等待上界：原本硬编码 180 秒，现改为「180 × 倍率」，夹在 `maxClientSkillTimeoutSec` 内。
3. **模型 HTTP 网络兜底**：`OpenAIClientOptions.NetworkTimeout = maxRunTimeoutMinutes + 5`（不再固定 15 分钟），否则外层放宽了、这一层先掐断，症状仍是“运行中取消、不出稿”。

**指派链 ×3**：计划 → 逐步执行 → 递归补查 → 交付兑底 是一条多阶段链，在自适应预算之上再乘 3（同样夹上界）；交付兑底内部再开一份自己的预算。

**两条不变量**（有测试钉住，改前先看 `RunTimeoutPolicyTests` / `SkillTimeoutBudgetTests`）：

- **只放宽、不收紧**：任何档位、任何夹紧都不会低于你原本配的 `streamTimeoutMinutes` 与技能预算（倍率下限 1；上界小于基准时以基准为准）。
- **确定性**：同一份触发上下文重算必得同一预算——审批恢复 / 桥接恢复用 `PendingInteraction` 里保留的同一份上下文，不会出现“恢复后预算莫名其妙变了”（否则会在恢复时被突然掐断）。

---

# B · 角色级（Role-level）禁用开关
# B · Role-level disable toggles

三类“本角色禁用”，存在 `AgentDefinition`，经 Agent HTTP 读写（默认/不勾 false → 跟随平台）：

| 开关 Switch | 关掉该角色后 Skip this stage for that role | 语义同平台哪项 P |
|---|---|---|
| `DisableBridge` | 本角色即使配了桥接端点/外部专家也不转发，改走本地 | `enableBridge=false` |
| `DisableRelay` | 本角色不把整轮交给 `RelayToAgentId` | `enableRelay=false` |
| `DisableOrgRoute` | 本角色即使配了“指派白名单/提升/协调”也不做组织化分发，只落普通流式兜底 | `enableOrgRoute=false` |

只在 `数字员工管理 → 编辑 → 执行阶段`里维护；生效判定在网关各 case 前：`def.DisableX is not true` 才继续。

---

# C · 可热改 vs 需改用配置/重启（Code-fixed boundary）能力矩阵
# C · tunable now vs config-file/restart / code-fixed matrix

能热改的以 P/R 表示（P=平台级执行参数页，R=角色级开关；另见既有「管理员→配置治理」）。标 **fixed** = 目前仍写死/仅 appsettings 读取，改需发版或重启，本节把它们显式列出来，避免误以为超支持面去热调。

| 维度 Aspect | 当前支持面 Support | 生效方式 | 备注 / 代码锚 |
|---|---|---|---|
| 触发时序（流式超时/重试/TTL/锁） | **P** | 热改持久 | `ExecutionOptions` |
| 复杂度自适应超时（倍率 / 上界 / 开关） | **P** | 热改持久 | `ExecutionOptions.ComplexityTimeouts`（§A.1） |
| 执行阶段顺序与整体开/关 | **P** | 热改持久 | `executionOrder` / Enable* |
| 单角色禁用 桥接/交接/组织路由 | **R** | 保存即对下次生效 | `AgentDefinition.Disable*` |
| 定时任务 cron 的“到点自动汇报/触发” | 每个数字员工的定时设置 | 角色保存即生效 | 调度器轮询（非本次） |
| 会话/消息/成员上限、保留天数、配额 | 管理员 `配置治理` 页 | 热改持久 | `ConfigGovernanceApi` |
| 工具开关（EnableTools / EnableWebTools / 思考 / 需审批工具表 / iframe 来源） | 管理员 `配置治理` 页 | 热改持久 | 同上（已存在，非本次新增） |
| 数字员工模型名 / 触发方式 / 关键词 / 私密 | 角色编辑表单 | 保存即生效 | AgentApi |
| 各角色“可调用子员工/技能”挂载、可复用技能库引用 | 角色编辑表单 / 技能库 | 保存即生效 | AgentApi / SkillApi |
| 一键组织/客服编排与受控落库（OrgDeploy 权限） | 组织用例 | 触发即按库内外状态 | OrgApplyEngine（管理员闸） |
| 模型 provider / apiKey / endpoint / 思考模型名 / 视觉模型 | **fixed → 需改 appsettings 并重启** | 启动装配 | `AgentOptions`：Provider/ApiKey/Endpoint/ThinkingModel/Vision* |
| 「小决策」模型名（判定发言 / 指派路由） | **fixed → appsettings `Agents:DecisionModel`**（默认留空 = 非推理常规模型） | 启动装配 | `AgentOptions.DecisionModel`；**故意不受思考模式影响**，详见 README「「小决策」模型」一节 |
| 「小决策」判定阈值（P(发言) 下限） | **fixed → appsettings `Agents:DecisionMinProbability`**（默认 0.3） | 启动装配 | `AgentOptions.DecisionMinProbability`；变更只影响后续调用，无需重启前端 |
| 记忆（RAG）是否启、embedding provider、向量维度、TopK、相似度阈值、上下文 | **fixed → 需改 appsettings 并重启** | 启动装配 | `Agents:Memory` |
| 提示词装配预算（总闸门 / 历史窗口与截断 / 历史附件回喂 / 附件注入长度 / 附图数量） | **fixed → appsettings 顶层 `PromptBudget` 节点**（不重启不生效） | 启动装配 | `PromptBudgetOptions`；分层降级与可观测见 `docs/agent-execution-algorithm.md` §2.3 |
| 正式回复的输出预算与推理力度 | **fixed → appsettings `Agents:MaxOutputTokens` / `Agents:ReasoningEffort`** | 启动装配 | 只作用于正式回复（`Create`）；小决策不受影响 |
| `CoordinatorPlanning`（确定性协调计划总开关） | **fixed → 需改 appsettings 并重启**（本页 P 只能调“计划条目/步骤上限”，开关仍码内固定） | — | `AgentOptions.CoordinatorPlanning` |
| Skill 自动盲跑 server shell 风险开关 / 允许内网技能端点放行 | **fixed → appsettings**（运维安全收敛项） | — | `SkillAutoTestServerShell`、`AllowPrivateSkillEndpoints` |
| 全局 AG-UI 桥默认端点（AguiBridge.Endpoint） | **fixed → appsettings**，角色侧桥接端点可经角色表单配置 | 启动装配 | `Agents:AguiBridge` |
| 审批工具名默认表 | `配置治理` 可改 | 热改 | （已存在） |
| 客户端 shell 隧道是否需先确认 | **fixed → appsettings `ClientToolTunnelRequireApproval`** | — | `AgentOptions` |
| 组织架构算法内部选择策略、指派/提升判定、@宿主代答语义 | **fixed（算法设计）** | 不受 P/R 控制 | `AgentOrchestrator` / `AgentGateway（组织化路由段）` |
| RBAC/权限门槛（如 org 落库仅系统管理员 / superadmin 会话可写） | **fixed** | 代码强制执行 | 权限过滤器 |

> 结论：**凡是涉及「一条消息从触发到被某段逻辑真正接走前”你能搬的旋钮」，我们尽量做成 P/R 热调**；而模型装配、记忆、是否开启“协调计划”、网络安全收敛项与 RBAC 门槛，目前仍属配置项或属于不可调的算法/权限设计——要改请走 appsettings/发版，不要期待在执行参数页弹出来。

---

# D · 运维速查（Ops quick-reference）
# D · Ops quick-reference

浏览器内（仅系统管理员）：
1. 打开**管理员控制台**（顶栏头像菜单）→ 切 `执行参数`页。
2. 改数字或顺序/开关 → `保存`。服务端返回**归一化后**实际生效值并自动回填（会把非法 token 剔除、`streaming` 补到最末），看到的就是已修正的真实值。
3. 想恢复出厂：点 `恢复默认`会把出厂快照回填到表单，再点 `保存`即整体归一化落库生效；也可只改其中想改的几项后保存（逐字段合并，未给的保留原值）。

持久化：
- 运行时会写入扩展区 `executionRuntime`：memory 快照 / postgres 等 `agui_sections`，重启自动恢复。
- 若你希望把默认固化到镜像里：把它写进 `Agents:Execution`（appsettings / Docker env）后重建。热改值优先于文件默认；重启后热改值仍在（覆盖持久化）。

CLI / 日志排查：
- 保存时值非法只回退该项并记一条 `Agents.Execution` / `ExecutionRuntime` `warn`，其余项保留；观察是否为“整表非法 → 全回默认（并 warn）”——这种情况执行参数页会显示默认值。
- 想看网关到底按什么跑：先在执行参数页 `保存`一次触发 `Normalize`，再看 `execution.patch` 审计（`execution.order/...`）确认落的值。

快速排障（常见误区）：
- 我把 `executionOrder` 删了？→ 服务端自动补回默认（缺失即整表空 → warn 回退默认顺序）。
- 写错拼写/含未知 token？→ 剔除该项；若合法路由阶段全空则整表回默认顺序。
- 为什么单角色还走 org_route？→ 该角色没开 `DisableOrgRoute`，或 `enableOrgRoute` 平台仍 true；二者任一关掉才跳过。
- 我不想让某角色经桥接？→ 编辑该数字员工 → 「执行阶段」勾 `🚫 禁用本数字员工的 AG-UI 桥接`（角色级），或全局关闭 `enableBridge`。
- 这次为什么跑了 15 分钟还没结束？→ 任务被估成“复杂/繁重”档，预算按倍率放宽了（见 §A.1）。日志会打一行 `运行超时预算按任务复杂度放宽：agent=… Heavy（分 8，×3，5→15 分钟，依据：交付物格式、41 页、2 处多步要求…）`，写明档位与依据；不想放宽就把 `complexityTimeouts.enabled` 关掉（其余参数照常）。
- 为什么某个数字员工这次回了 3 分钟就断，上次却等了 10 分钟？→ 看上面的预算日志：档位随触发消息内容变，同一角色不同请求的预算本来就不一样。

---

# E · 边界与设计取舍（Trade-offs）
# E · Boundary & design trade-offs

1. streamTimeout 是“单次流式调用的最大时长”的**基准值**，**不是**整条消息全链路的预算：实际预算按任务复杂度向上放宽（§A.1），且全链路仍由各阶段分别超时叠加 + 递归轮次上限兜底，因此极端慢的外部只拖长单段，不会让整条一票否决。
2. `coordinatorPlanMaxItems/Steps`、`maxRecursiveRounds`、`maxRouteDepth`、`maxInteractionRounds` 本质是“防病态”的上限，若当业务“想调多”往上加，收益有限而代价是指数风险——建议只在明确需求时微调，别当成性能大旋钮。
3. “谁在什么文件上写库”等权限硬约束不在本参数页可配范围（是安全线）；全平台统一降权请走 RBAC/平台角色的配置，而不是在单角色执行开关里凑。
