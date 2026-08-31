# AG-UI 群聊桌面版 —— 新增：内网穿透（反向隧道）
# AG-UI Group Chat Desktop — New: NAT traversal (reverse tunnel)

## 新增（中文）
- **新增：反向隧道（HTTP/SSE）让「无公网 IP」的内网机本机桥被公网 Hub 调用**。内网机上的本机桥主动出站连公网 Hub 并注册（`--agent` 绑定数字员工）；Hub 把对该员工的客户端技能任务沿隧道下行推给内网桥执行，结果回传模型继续作答——无需入站公网端口、无需第三方隧道。
- **用法**：`NativeBridge --tunnel <hub> [--agent <id>] --tunnel-token <token>`；Hub 侧 `NativeTunnel__Token`（或 appsettings `NativeTunnel:Token`）校验。前端桥地址仍填 Hub 自身即可，网关经隧道转发。
- **平台级桥（信任整个平台）**：不再强制指定数字员工 id——`--tunnel` 时不填 `--agent` 即注册为平台级桥（`*`），一座桥服务任意数字员工的客户端技能；某员工有专属桥时优先用专属桥，否则回落到平台级桥。
- **安全加固**：逐 agent 专属隧道令牌（`NativeTunnel:AgentTokens__<agentId>`，优先于全局 `NativeTunnel:Token`）；`POST /ag-ui/native-tunnel/result` 回传需携带该 agent 有效令牌（本机桥自动带 `--agent/--tunnel-token`），防伪造结果 / 无令牌刷接口；`connect` 与 `result` 端点带内存滑动窗口限流（默认 120 / 600 次每 IP 每分钟，可配）。

## New (English)
- **New: reverse tunnel (HTTP/SSE) so an intranet local bridge with no public IP can be called by the public Hub**. The bridge on the intranet host dials out to the public Hub and registers (binding a digital employee via `--agent`); the Hub pushes that employee's client-skill task down the tunnel for the bridge to execute, posting the result back so the model can continue—no inbound public port, no third-party tunnel. Usage: `NativeBridge --tunnel <hub> [--agent <id>] --tunnel-token <token>`; the Hub validates via `NativeTunnel__Token` (or appsettings `NativeTunnel:Token`). The frontend bridge URL still points at the Hub itself; the gateway forwards over the tunnel.
- **Platform-wide bridge (trust the whole platform)**: `--agent` is now optional under `--tunnel`—omit it to register as a platform-wide bridge (`*`) that serves any employee's client skills; an employee-specific bridge takes precedence when present, otherwise execution falls back to the platform-wide bridge.
- **Security hardening**: per-employee tokens (`NativeTunnel:AgentTokens__<agentId>`, takes precedence over global `NativeTunnel:Token`); the `POST /result` endpoint now requires a valid token for that agent (the bridge sends `--agent`/`--tunnel-token` automatically); in-memory sliding-window rate limiting on both `connect` and `result` (default 120 / 600 per IP per minute, configurable).

---

# AG-UI 群聊桌面版 1.0.102 发布说明
# AG-UI Group Chat Desktop 1.0.102 Release Notes

## 修复与改进（中文）
- **新增：客户端执行技能（`ExecutionLocation=Client`，本机执行）**。技能可配置为「客户端执行」：服务端不执行，而是由<b>前端/本机桥</b>在浏览器所在主机执行并把结果回传（shell 走本机 PowerShell/沙箱；http 走浏览器 fetch）。前端在卡的聊天历史内确认后执行，结果回灌模型继续。
- **新增：独立「本机工具桥（NativeBridge）」**。Docker + 浏览器在本机（如 aibook）时，shell 类客户端技能需在<b>浏览器所在主机</b>执行而非 Docker 容器。新增独立项目 `src/AguiGroupChat.NativeBridge`（回环监听、令牌鉴权、CORS 白名单、沙箱/超时/截断），并在「修改资料 → 本机工具桥」提供 **🔍 一键检测** 自动读地址+令牌。桌面版无需（桌面壳即本机）。
- **新增：编排计划内客户端技能「本机一键执行全部」**。数字员工定计划时若一次选中多个客户端技能，网关把它们合并成一张 `client_tool_batch` 卡，一次确认后前端逐个本机执行、逐条点亮计划卡、最后综合回归。
- **新增：递归补查闭环（方案 C）**。数字员工基于已收集结果作答时，若发现信息不足（缺磁盘/内存/日志等关键数据），会**主动继续调技能/派下属补齐**，直到信息充分才给最终结论——不再中途停下问「要不要继续」。
- **新增：用自然语言生成技能配置**。技能库「🤖 用自然语言生成技能」：输入需求（如「检查本机磁盘使用情况」），大模型产出名称/类型/命令/描述/执行位置/ClientRunner，自动填入表单供微调保存（可选「优先本机执行」）。端点 `POST /ag-ui/skills/generate`。
- **增强：技能型智能体也走编排计划**。开启 `CoordinatorPlanning` 后，仅挂了技能（无下属/提升目标）的数字员工被 @ 时也进入计划编排（多技能批量 + 递归补查），不再回落为普通单工具调用。Docker 默认 `CoordinatorPlanning=true`。
- **修复：审批/交互卡点击后即时隐藏**。已决策（resolved）或客户端技能执行中（running）的卡片即时消失且不随任何重渲染复活（同步移除 DOM + 渲染层空化）。
- **修复：编译/运行、多技能规划、计划步骤上限**。多技能组合体检规划（6→8 步上限）与规划 prompt 引导（全面检查时多选互补技能）。

## Fixed & Improved (English)
- **New: client-execution skills (`ExecutionLocation=Client`)**. A skill can be marked "client execution" so the server does not run it; the frontend/native bridge runs it on the browser's host machine and posts the result back (shell via local PowerShell sandbox; http via browser fetch). Confirmation happens inline on the chat card, then the result is fed back to the model.
- **New: standalone NativeBridge**. For Docker + a browser on the local machine (e.g. aibook), shell client skills must run on the <b>browser's host</b> rather than the Docker container. Added `src/AguiGroupChat.NativeBridge` (loopback, token auth, CORS allowlist, sandbox/timeout/truncation) plus a **one-click Detect** in Profile -> Native Tool Bridge to read address & token. Desktop needs none (the desktop shell is the local host).
- **New: batch "run all locally" for client skills inside a plan**. When a plan selects several client skills, the gateway merges them into one `client_tool_batch` card: confirm once, the frontend runs each locally, lights up each plan step, then synthesizes a combined answer.
- **New: recursive gather-and-answer loop (Plan C)**. While answering from gathered results, if the info is insufficient (missing disk/memory/logs etc.), the digital employee proactively keeps invoking skills/direct reports until the answer is complete—never stopping to ask "continue?" in the middle.
- **New: generate skill definitions from natural language**. In the skill library, "generate from plain text": describe a request (e.g. "check local disk usage"), and the LLM produces name/kind/command/description/execution location/ClientRunner, filled into the form for review (optionally "prefer local execution"). Endpoint `POST /ag-ui/skills/generate`.
- **Enhancement: skill-only agents also take the coordinator plan**. With `CoordinatorPlanning` on, an agent that only has skills (no subordinates/escalation) also routes through plan orchestration when @-mentioned (multi-skill batch + recursive gathering) instead of plain single-tool calls. Docker defaults `CoordinatorPlanning=true`.
- **Fix: interaction cards hide immediately on decision**. A resolved or running client-tool card disappears instantly and cannot reappear on any re-render (synchronous DOM removal + empty render).

## 使用提示（中文）
客户端技能：桌面版开箱即用（桌面壳本机执行）；Docker + 本机浏览器需在「修改资料 → 本机工具桥」一键检测填入地址与令牌。自然语言生成技能需已配置 DeepSeek 等模型 key。

## Usage Note (English)
Client skills work out of the box in the desktop edition (the desktop shell executes locally); for Docker + a local browser, use Profile -> Native Tool Bridge -> Detect to fill the address & token. Generating skills from text requires a configured model key (e.g. DeepSeek).

---
文件：`AguiGroupChat-Desktop-1.0.102.msi` / Docker（postgres+ollama+web）
File: `AguiGroupChat-Desktop-1.0.102.msi` / Docker (postgres+ollama+web)

---

# AG-UI 群聊桌面版 1.0.80 发布说明
# AG-UI Group Chat Desktop 1.0.80 Release Notes

## 修复与改进（中文）
- **修复：桌面版技能库不持久化（重启丢失）**。桌面后端装配漏注册技能库持久化（`DesktopApp` 缺 `RegisterSkillPersistence()`，Web 版有）——导致桌面版里新建 / 编辑的技能不写盘、重启即丢，且数字员工经 `skillDefIds` 引用的技能在重启后会被当「不存在」跳过而不被调用。已补上 `RegisterSkillPersistence()`，桌面技能库跨重启保持。
- **新增：HTTP 技能访问本机 / 内网开关（`Agents:AllowPrivateSkillEndpoints`）**。默认关（保留 SSRF 防护，拒绝本机/内网）；确需调用本机 / 内网接口（本地 Ollama / 内网 API）时置 `true` 放行，放行后仍保留 http/https 白名单与重定向逐跳校验。桌面 `appsettings.json`，Docker `AGENTS_ALLOW_PRIVATE_SKILL_ENDPOINTS`。
- **修复：桌面版保存技能失败（405）**。桌面版后端装配漏注册技能库 API（`DesktopApp` 未调用 `MapSkillApi()`，Web 版有）；导致桌面版里技能库新增 / 编辑 / 试运行全部返回 405。已补上 `MapSkillApi()`，桌面与 Web 技能库功能一致。
- **修复：指派链路前缀末级对象重复显示**。多级下派到叶子作答时，最末端对象在「代为处理」前缀里会出现两次（如 `（Exchange连接测试助手 代为处理）` ×2）。修复 `AgentGateway` 路由链构建：末级自答时不再重复叠加末级关系节点，链路各层只出现一次。
- **修复：组织架构未保存改动时「优化指派」会出错**。新增未保存检测——新拖的指派/提升连线未点「保存」时，点「优化指派」会提示「请先保存」而不再基于过时后端数据生成。
- **指派判断只看下一层**：组织架构里的数字员工只依据<b>自己直接下一层</b>的提示词/职责判断是否指派（不向上钻、不引入更深层叶子），同时保留下层<b>多指派</b>（多候选排序 + 回退）与「收到指派后继续嵌套指派」（多级深钻链）。
- **新增：组织架构「优化指派」提示词生成**：组织架构图每个节点新增「优化指派」按钮，按该数字员工的<b>直接下一层</b>（AssignmentIds）自动生成一段「管理下一层任务指派」指引（只依据下一层挑下游、不越级、行不通则返回 NONE），预览后可追加到其 Instructions。端点 `POST /ag-ui/agents/{agentId}/optimize-assignment`（需登录，仅创建者/管理员）。
- **修复：任务指派无法按组织架构深钻到最后一层**。此前指派到达下一层时仅做「单候选贪心」：若该层语义分析返回 NONE 就提前终结，即使真正匹配的数字员工在更深层。现改为<b>多候选排序 + 递归探测回退</b>：指派持续向下钻取，某子分支无解时回退到下一候选，直到命中能作答的末端层；并向模型传入候选昵称+职责，语义匹配更准。
- **图谱 RAG 默认关闭（本次交付默认禁用，可手动开启）**：为排除图谱检索对问答/下派效果的干扰，本次 <b>Docker 与桌面版默认 `GraphEnabled=false`</b>（向量语义记忆不受影响）。需要图谱时再于 `.env`（`MEMORY_GRAPH_ENABLED=true`）或 `appsettings.json`（`Agents:Memory:GraphEnabled=true`）开启。
- **图谱 RAG 注入收敛（提升检索效果）**：图谱定位为补强而非并重——新增 `GraphMaxSectionChars=700` 注入预算，收紧默认 `GraphTopK=3→2`、`GraphMinScore=0.30→0.45`、`GraphHops=2→1`、`GraphMaxNodes=40→12`，避免图谱挤占、稀释向量切片。
- **修复：数字员工“未调用已关联的 HTTP 技能”**。根因是 HTTP 技能在创建/更新时被<b>强制置为需审批</b>（`SkillApi.BuildDef` 把 shell/http 一律写死 `RequiresApproval=true`）——技能虽已挂载，但模型一调用就进审批卡，自动化流程没有人工批，看起来就是“没被调用”。现将 `RequiresApproval` 改为：<b>仅 shell 技能强制需审批</b>，HTTP / 提示词技能跟随创建者勾选（可<b>关闭审批以自动调用</b>），需要本机/内网时另由 `Agents:AllowPrivateSkillEndpoints=true` 放行。前端技能表单相应放开 HTTP 的“需审批”开关。
- **修复：数字员工保存失败（请求格式错误 / 缺少字段）**。前端错误展示改为<b>优先显示后端返回的具体原因</b>（如“引用的技能不存在于技能库：xxx”），不再被通用错误码文案掩盖；同时排查并修正了数字员工 `skillDefIds` 引用不存在技能导致保存 400 的数据问题。
- **新增：数字员工表单「可调用子数字员工」**：把下层（或其他）数字员工挂为上层角色的可调用技能——模型需要其领域能力时可自动调起、引用其答复（即“上层正好需要下层能力时触发”）。此前后端与文档都已支持 `Skills（子代理）`，但前端表单不再暴露入口，导致无法配置；已在「技能与知识」段补回多选择器 + 每项调用说明，`skillId` 留空后端自动生成 `skill_<目标ID>`。
- **修复：所有桌面实例退出后后台进程残留**。根因是运行期 `instance-count`（记录多少个 UI 窗口在共享同一个后端）在异常退出后可能残留非零，导致后续每次启动把计数不断抬升、永远到不了 0，`/ag-ui/shutdown` 也就不会触发，后端进程残留。修复分三层：① 后端进程启动时把残留计数<b>归零</b>（新进程必然没有已存活的 UI）；② 后端自监视停机兜底——实例计数为 0 且无活动连接持续约 15s、或即使计数残留为正但无活动连接持续约 2 分钟时，后端自行优雅停机（显式 `Environment.Exit` 冲刷落盘）；③ 正常链路（计数归零 → HTTP shutdown）仍即时退出。已实测：无 UI 时后端自动退出、有 UI（计数=1）时后端保持在线。
- **新增：确定性编排计划（Coordinator Plan，`Agents:CoordinatorPlanning`）**：针对“技能/数字员工难以被可靠触发”的痛点，提供“问题 → 按<b>组织架构与技能配置</b>定计划 → <b>依次激活</b>对应数字员工与技能执行 → 聚合答复”的编排方式。开启后，带指派白名单/提升目标的路由型数字员工收到问题时，会把<b>可达的下游员工清单</b>与<b>可调用技能清单</b>显式列给模型，由其产出结构化执行计划（dispatch 派谁 / skill 调什么 / answer 汇总），再确定性逐项激活；任何环节失败自动回退到原有递归指派，不阻断主流程。桌面默认开启，Docker `AGENTS_COORDINATOR_PLANNING`；默认关（未启用时行为不变）。
- **增强：支撑<b>技能→技能 / 员工→技能的依赖链</b>**。编排计划会识别技能正文里的输入占位（`${query}` 等，见清单中技能的【需要输入：…】），并明确引导计划<b>先 dispatch 掌握该输入的员工拿值，再调用技能</b>（技能步骤自动收到上一步结果作输入）。这解决了“某技能的运行需要另一员工/前序步骤提供参数（如 Exchange 连接测试技能需要 OWA 地址，而地址由配置管理员提供）”的依赖场景；并补充了该依赖链的回归测试。
- **新增：编排计划可视化**。当确定性编排计划运行时，界面会把<b>执行计划步骤</b>广播为 `TEXT_MESSAGE_PLAN`（前端计划卡渲染）：如「指派「配置管理员」→ 调用技能「连接测试」→ 综合答复」逐项勾选展示，让用户看清“问题 → 按组织/技能定的计划 → 依次激活了谁”。
- **增强：编排计划<b>边执行边逐条点亮</b>**。计划拆分重构为「先规划 → 随消息流逐项执行」：消息开播即广播“全部待执行”的计划卡，每完成一步（派下属 / 调技能）动态把对应步骤勾选为完成并<b>实时刷新计划卡</b>，全部完成后综合答复——用户能实时看到每一步在现场点亮。测试断言：多次计划广播、首帧全未完成、末帧全部完成且含“调用技能”。
- **修复：技能需要的“值”喂不干净（如 Exchange 测试技能要 OWA 地址，却拿到带解释的文字）**。参数化技能（正文含 `${query}`）执行前，编排计划会：① 前置的取值步骤（派给配置管理员等）提示<b>只输出所需的值本身</b>；② 即便员工返回了带解释的文字，也会用 `ExtractCleanValueForSkill` <b>提取出干净的 URL/地址</b>再作为技能输入。新增 4 个取值提取单测 + 依赖链回归（断言先派后调、多次点亮）。
- **修复：协调计划派给子员工时丢上下文（配置管理员答“数据不足”）**。根因：协调计划经 `child.RunAsync` 直接调子员工时，`MemoryContextProvider` 仍按<b>宿主</b>的 `AmbientContext` 注入知识库/记忆——于是派给绑了配置库的“配置管理员”时它拿不到自己的配置库（`mail.lingtong.com`），只会答“数据不足/请提供地址”。修复：执行 dispatch 步骤时把 ambient 上下文<b>切换到子员工本人</b>（Group/Topic/触发者不变，仅 AgentId/Nickname 改为子员工），子员工据此检索自己的知识库。
- **相同修复顺带覆盖编排流水线（Pipeline）与角色交接（Relay）**：这两处直接 `child/relay.RunAsync` 调子员工/被交接方，同样会因宿主 ambient 上下文拿不到自己的知识库记忆；已做一致处理（Pipeline 每步、Relay 整轮都切到对方）。
- **“代为处理”表述对齐**：协调计划卡上被指派的步骤显示为「指派「X」代为处理」，与消息正文的前缀「（X 代为处理）」一致。
- **有计划卡时不再在正文重复“（X 代为处理）前缀”**：既然计划卡已把“指派给谁”表达清楚了，协调计划路径的消息正文不再叠加「（X 代为处理）」前缀，避免冗余；无计划卡的非编排路径仍保留前缀。
- **计划卡文字调整**：指派步骤由「指派「X」代为处理」改为「**为「X」分配工作**」。

## Fixed & Improved (English)
- **Fix: router agents no longer self-answer and swallow issues that should be dispatched (IT Service Desk → Exchange Expert scenario)**. Previously the "should I answer" semantic check ran before dispatch, so the IT Service Desk self-answered "outlook can't connect to exchange" instead of dispatching to a dedicated Exchange expert. Now <b>router nodes (with an assignment whitelist) dispatch first</b> - drill to the best-matching specialist layer before self-answering, and only fall back to self-serving when no specialist claims it; multi-level deep drilling to the last layer is also supported.
- **Assignment judgment only looks at the next layer**: an org-chart agent decides whether to assign based solely on its <b>direct subordinates'</b> prompts/roles (no up-drilling, no bringing in deeper specialist leaves), while keeping down-layer <b>multi-assignment</b> (multi-candidate ranking + fallback) and <b>nested assignment</b> after receiving a task (multi-level deep-drill chain).
- **New: org-chart "Optimize dispatch" prompt generator**: each org-chart node gains an "Optimize dispatch" button that auto-generates a "manage next-layer dispatch" guidance paragraph from the employee's <b>direct next layer</b> (AssignmentIds) - pick a subordinate based only on the next layer, no override, return NONE if none fits - previewable and appendable to its Instructions. Endpoint `POST /ag-ui/agents/{agentId}/optimize-assignment` (login required, owner/admin only).
- **Fix: task assignment cannot drill down the org tree to the last layer**. Assignment previously used a greedy single-candidate pick at the next layer: if that layer's semantic analysis returned NONE, the chain terminated early even though the real matching agent sat deeper. Now it is <b>multi-candidate ranking + recursive probe with fallback</b>: assignment keeps drilling downward, and when one branch fails it falls back to the next candidate until it reaches an answering leaf (supports inference down the org tree to the end); the model is also given each candidate's nickname + role so semantic matching is more accurate.
- **Tightened Graph RAG injection (better retrieval quality)**: the graph is now a supplement, not an equal-weight block - a `GraphMaxSectionChars=700` injection budget and tighter defaults (`GraphTopK=3→2`, `GraphMinScore=0.30→0.45`, `GraphHops=2→1`, `GraphMaxNodes=40→12`) keep it from crowding out or diluting the vector chunks.
- **Fix: digital employee "does not call its associated HTTP skill"**. Root cause: HTTP skills were hard-forced to `RequiresApproval=true` at create/update (`SkillApi.BuildDef` hard-coded shell/http), so although the skill was mounted, the model's call immediately hit an approval card that no human approved in the automated flow - looking like "not called". Now `RequiresApproval` follows the creator's choice for <b>HTTP / prompt</b> skills (can be <b>turned off to auto-invoke</b>), while <b>shell skills stay always-requiring-approval</b>; access to local/intranet targets is governed separately by `Agents:AllowPrivateSkillEndpoints=true`. The skill form's "requires approval" toggle is now enabled for HTTP.
- **Fix: digital employee save failure (request format error / missing fields)**. Error display now <b>prefers the backend's specific message</b> (e.g. "referenced skill not found in library: xxx") instead of being masked by a generic code, and a data issue where an agent's `skillDefIds` referenced a nonexistent skill causing a 400 on save was diagnosed and fixed.
- **New: "Callable sub employees" in the digital-employee form**: attach a lower (or any other) digital employee as a callable skill of an upper role - the model auto-invokes and cites it when it needs that employee's capability (i.e. "the upper employee triggering a lower employee's capability when needed"). The backend and docs already supported `Skills` (sub-agents) but the form no longer exposed an entry to configure it; a multi-selector plus a per-item call description is now restored under the "Skills & Knowledge" section, with `skillId` auto-generated (`skill_<targetAgentId>`) when left blank.
- **Fix: background process lingers after all desktop instances exit**. The runtime `instance-count` (which records how many UI windows share the one backend) could be left non-zero after an abnormal exit, so every subsequent launch kept inflating it and it never reached 0 - the `/ag-ui/shutdown` call never fired and the backend process lingered. The fix has three layers: ① the backend process <b>resets any stale count to 0 on startup</b> (a newly started backend has no live UI by definition); ② a backend <b>self-monitoring watchdog</b> gracefully stops the backend when the instance count is 0 with no active connections for ~15s, or even if the count is stale-positive but there are no active connections for ~2 minutes (`Environment.Exit` flushes persistence); ③ the normal path (count hits 0 -> HTTP shutdown) still exits immediately. Verified: the backend exits when no UI is attached and stays online when a UI (count=1) is present.
- **New: deterministic orchestration plan (Coordinator Plan, `Agents:CoordinatorPlanning`)**. To address the unreliable triggering of skills / digital employees, this adds a "question -> build a plan from the <b>org chart & skill config</b> -> <b>activate</b> the selected digital employees & skills in sequence -> aggregate the answer" orchestration. When enabled, a router-type digital employee (with an assignment whitelist / escalation target) receives a question, explicitly enumerates the <b>reachable subordinate employees</b> and <b>callable skills</b> to the model, has it produce a structured execution plan (dispatch who / invoke which skill / how to summarize), then activates each step deterministically; any failure falls back to the original recursive dispatch without blocking the flow. Enabled by default in the desktop; Docker `AGENTS_COORDINATOR_PLANNING`; default off (behavior unchanged when not enabled).
- **Enhanced: supports <b>skill->skill / employee->skill dependency chains</b>**. The orchestration plan now detects the input placeholders in a skill body (`${query}` etc., shown as 【需要输入：…】 in the inventory) and explicitly guides the plan to <b>first dispatch the employee that holds that input, then invoke the skill</b> (the skill step automatically receives the previous step's result as its input). This addresses cases where one skill's execution needs a parameter supplied by another employee / earlier step (e.g. the Exchange connectivity-test skill needs the OWA address, which the config admin provides); a regression test covers the chain.
- **New: orchestration-plan visualization**. When the deterministic orchestration plan runs, the UI now receives the <b>execution-plan steps</b> as a `TEXT_MESSAGE_PLAN` (rendered as a plan card): e.g. "dispatch to 配置管理员 -> invoke the connectivity-test skill -> synthesize the answer", shown step-by-step, so the user can see "question -> plan from the org/skills -> who was actually activated". Covered by a test that asserts the plan event contains a skill step in the dependency-chain scenario.
- **Enhanced: orchestration plan lights up step-by-step in real time**. The plan was split into "first plan, then execute step-by-step while the message streams": as soon as the message starts, the plan card is broadcast with all steps pending; each step (dispatch to an employee / invoke a skill) marks its own row complete and <b>refreshes the plan card live</b>, and after all steps the final synthesized answer streams. The test asserts multiple plan broadcasts, first frame all pending, last frame all done and containing an "invoke skill" step.
- **Fix: coordinator lost context when dispatching to a sub employee (config admin answering "insufficient data")**. Root cause: the coordinator invoked the sub employee via its own `child.RunAsync`, but `MemoryContextProvider` injected knowledge/memory based on the <b>host's</b> ambient context - so when it dispatched to a config admin bound to a config knowledge base, that admin could not see its own config base (`mail.lingtong.com`) and only answered "insufficient data / please provide the address". Fix: when executing a dispatch step, the ambient context is now <b>switched to the sub employee</b> (Group/Topic/triggerer stay the same, only AgentId/Nickname are the sub employee's) so it retrieves its own knowledge base.
- **The same fix also covers the orchestration pipeline (`Pipeline`) and role handoff (`Relay`)**: both invoke sub/passee agents via `child/relay.RunAsync` directly and would lose their own knowledge/memory under the host's ambient context; they are now handled consistently (each pipeline step and the whole relay run switch the ambient context to the counterpart).

## 使用提示（中文）
全新安装或想彻底重置：先完全退出桌面版，再删除 `%LocalAppData%\AguiGroupChat\data\` 目录后启动，第一个注册账号即为管理员。启用图谱记忆：`appsettings.json` → `Agents:Memory:GraphEnabled=true`。组织下派靠「智能体管理 → 组织架构」配置各角色的任务指派白名单（AssignmentIds）与问题提升目标。

## Usage Note (English)
For a clean start: fully quit the app, delete `%LocalAppData%\AguiGroupChat\data\`, then launch - the first account you register becomes the admin. To enable graph memory set `Agents:Memory:GraphEnabled=true` in `appsettings.json`. Configure per-role assignment whitelists (`AssignmentIds`) and escalation targets via "Agent Management → Org Chart".

---
文件：`AguiGroupChat-Desktop-1.0.80.msi` / Docker（postgres+ollama+web，`MEMORY_GRAPH_ENABLED=true`）
File: `AguiGroupChat-Desktop-1.0.80.msi` / Docker (postgres+ollama+web, `MEMORY_GRAPH_ENABLED=true`)

---

# AG-UI 群聊桌面版 1.0.79 发布说明
# AG-UI Group Chat Desktop 1.0.79 Release Notes

## 改进（中文）
- **图谱 RAG 注入收敛（提升检索效果）**：之前图谱子图（最多 40 实体 + 50 边）与向量切片平权强塞进 prompt，反而稀释了向量检索结果。本次把图谱定位为<b>补强而非并重</b>：段落强引导语「仅作参考、涉及具体事实以向量/知识库切片原文为准」，并新增 `GraphMaxSectionChars=700` 总字符预算——先保留种子/近层实体与其连接的高价值边，超出部分丢弃；同时收紧默认召回 `GraphTopK=3→2`、`GraphMinScore=0.30→0.45`、`GraphHops=2→1`、`GraphMaxNodes=40→12`，让图谱只在查询很贴近实体时少量补充，避免挤占、稀释向量切片。

## Improved (English)
- **Tightened Graph RAG injection (better retrieval quality)**: previously the graph subgraph (up to 40 entities + 50 edges) was injected with equal weight alongside the vector chunks, which diluted the vector results. The graph is now positioned as a <b>supplement</b>: the section carries explicit guidance "for reference only; specifics should defer to the vector/KB snippets", and a new `GraphMaxSectionChars=700` char budget keeps seed/nearby entities and the high-value edges connecting them first, dropping anything beyond; defaults are also tightened (`GraphTopK=3→2`, `GraphMinScore=0.30→0.45`, `GraphHops=2→1`, `GraphMaxNodes=40→12`) so the graph only adds a little when the query is very close to an entity, without crowding out the vector chunks.

## 使用提示（中文）
全新安装或想彻底重置：先完全退出桌面版，再删除 `%LocalAppData%\AguiGroupChat\data\` 目录后启动，第一个注册账号即为管理员。启用图谱记忆：`appsettings.json` → `Agents:Memory:GraphEnabled=true`。已启用用户若觉得图谱仍偏噪声，可进一步调低 `GraphMaxSectionChars` 或关闭 `GraphEnabled`。

## Usage Note (English)
For a clean start: fully quit the app, delete `%LocalAppData%\AguiGroupChat\data\`, then launch - the first account you register becomes the admin. To enable graph memory set `Agents:Memory:GraphEnabled=true` in `appsettings.json`. If you still find the graph noisy, lower `GraphMaxSectionChars` further or turn `GraphEnabled` off.

---
文件：`AguiGroupChat-Desktop-1.0.79.msi` / Docker（postgres+ollama+web，`MEMORY_GRAPH_ENABLED=true`）
File: `AguiGroupChat-Desktop-1.0.79.msi` / Docker (postgres+ollama+web, `MEMORY_GRAPH_ENABLED=true`)

---

# AG-UI 群聊桌面版 1.0.78 发布说明
# AG-UI Group Chat Desktop 1.0.78 Release Notes

## 修复（中文）
- **修复：长文档上传知识库向量化失败（返回「embedding 不可用」）**。本地 embedding（LLamaSharp / bge-m3）超长文本分段的字符/token 比值误设为 2.0，导致单次 embedding 的字数上限（context×2=4096）超过模型真实 context，凡正文切片约 2000+ 字符的文档（如员工手册类 docx）都会被整片交给模型、返回空向量而入库失败。已改为 0.9，长切片自动切成多段（每段 ≤ context×0.9 字）分别向量化、再取平均，长文档可正常入库。

## Fixed (English)
- **Fix: long documents fail vectorization on KB upload** (reported as "embedding unavailable"). The safe chars/token ratio for long-text segmenting in the local embedding (LLamaSharp / bge-m3) was mistakenly set to 2.0, so the single-shot character cap (context×2=4096) exceeded the model's real context; any document whose chunks exceed ~2000 characters (e.g. employee-handbook docx) was passed whole to the model, returned an empty vector, and failed to ingest. Changed to 0.9 - long chunks are now split into multiple segments (each ≤ context×0.9 chars), embedded separately, then averaged, so long documents ingest correctly.

## 使用提示（中文）
全新安装或想彻底重置：先完全退出桌面版，再删除 `%LocalAppData%\AguiGroupChat\data\` 目录后启动，第一个注册账号即为管理员。启用图谱记忆：`appsettings.json` → `Agents:Memory:GraphEnabled=true`。

## Usage Note (English)
For a clean start: fully quit the app, delete `%LocalAppData%\AguiGroupChat\data\`, then launch - the first account you register becomes the admin. To enable graph memory set `Agents:Memory:GraphEnabled=true` in `appsettings.json`.

---
文件：`AguiGroupChat-Desktop-1.0.78.msi`（约 584 MB，已内置本地 embedding 模型）
File: `AguiGroupChat-Desktop-1.0.78.msi` (~584 MB, bundles the local embedding model)

---

# 上一版：1.0.77
# Previous: 1.0.77

## 新增（中文）
- **知识库图谱 RAG（Graph RAG）**：上传到知识库的文档在入库时也会抽取「实体-关系-实体」建入隔离域 `kb:{KbId}` 的图谱；检索知识库时对绑定知识库做「语义召回种子实体 + n 跳图遍历」，把可达子图与向量切片并列注入 prompt，补强知识文档中的关系型知识。知识库图谱与群记忆图谱按域隔离、互不污染；删除知识库时同步清其图谱。
- **系统状态页：RAG 检索方式可视化**：管理员「系统状态」页新增两行——「向量语义记忆」（开/关）与「图谱方式（Graph RAG）」（已生效 · 实体 N / 关系 M，或未启用），直观展示当前 RAG 是否使用图谱方式。

## New (English)
- **Knowledge-base Graph RAG**: knowledge-base documents are now also entity/relation-extracted into the isolated `kb:{KbId}` graph on ingest; knowledge retrieval performs semantic seed recall + n-hop traversal over the bound knowledge bases and injects the reachable subgraph alongside the vector chunks, augmenting relational knowledge in documents. KB graphs and group-memory graphs are domain-isolated and cross-contamination-free; deleting a KB also removes its graph.
- **System-status RAG visualization**: the admin "System Status" page now shows two rows — "Vector semantic memory" (On/Off) and "Graph mode (Graph RAG)" (Active · N entities / M relations, or Not enabled), making it clear whether RAG is currently using the graph approach.

---
文件：`AguiGroupChat-Desktop-1.0.77.msi`
File: `AguiGroupChat-Desktop-1.0.77.msi`
