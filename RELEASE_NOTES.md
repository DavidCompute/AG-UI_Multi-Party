# AG-UI 群聊桌面版 1.0.119 发布说明（当前桌面版）
# AG-UI Group Chat Desktop 1.0.119 Release Notes (current desktop release)

## 新增（中文）
- **内部协调 JSON 的“整段二次剥壳”**：智能体消息收尾（EndAgentMessage）时把整段正文再归一一次——若它就是 {\"needsMore\":…,\"answer\":…} 协调 JSON，落库与广播前统一替换为用户可读的 answer；即便此前被拆成多段流式发给用户，也会在完结前被纠正。
- 承上：Client（本机）技能只在该“发起请求的用户所在机器”执行（A 口径）、本机无桥报“执行失败，没有安装桥”；试运行结果独立弹窗、结果真正落到当前机器等系列改进持续有效。

## New (English)
- **Second-pass cleanup of coordination JSON at message end**: at agent-message End, if the whole content is an internal {\"needsMore\":…,\"answer\":…} object, it is rewritten to its user-facing answer before store/broadcast — even if earlier streamed in fragments.
- Carried: Client (local) skills run only on the originating user’s machine (policy A); no bridge → “execution failed: no bridge installed”; trial results in a dialog and truly landing on the current machine.

**版本说明**：1.0.119 为当前 Windows 桌面点版本，主题为「内部协调 JSON 整段二次剥壳（收尾归一）」，并包含 Client 技能 A 口径系列修复；已构建 Windows 1.0.119 MSI。本机桥日志写系统临时目录。
**Version note**: 1.0.119 is the current Windows desktop point release, themed “second-pass whole-message cleanup of coordination JSON”, including the Client-skill policy-A series; a Windows 1.0.119 MSI was built.

---

# AG-UI 群聊桌面版 1.0.118 发布说明（上一版本）
# AG-UI Group Chat Desktop 1.0.118 Release Notes (previous point release)

## 新增（中文）
- **Client（本机）技能只在该“发起请求的用户所在机器”执行**：接受 A 口径——凡 `ExecutionLocation=Client` 的技能只能跑在提问者消息所附的本机桥那台机器（`msg.BridgeClient`）；该用户本机无桥 / 未上报一律返回“执行失败，没有安装桥”，不再回落任何 agent/平台级桥。桌面版（宿主即本机）直接执行，无桥限制。
- **修复“内部协调 JSON 泄漏到聊天”**：把 `{"needsMore":…,…,"answer":…}` 这类协调 JSON 的剥壳铺到各最终答复出口；若模型把内部决策 JSON 原样当正文，只把 `answer` 给用户看。
- 附带：本机/client 试运行结果独立弹窗显示、结果真落到当前机器（系列 1.0.117 已梳理的改进继续有效）。

## New (English)
- **Client (local) skills execute only on the requesting user’s machine**: under policy A, any `ExecutionLocation=Client` skill runs only on the bridge of the originator’s message (`msg.BridgeClient`); if that user has no bridge / didn’t report one, it returns “execution failed: no bridge installed” and never falls back to an agent/platform bridge. Desktop (host == the user machine) executes directly without a bridge.
- **Stop internal coordination JSON from leaking into the chat**: unwrap `{"needsMore":…,…,"answer":…}` to just `answer` at every final-reply funnel; won’t affect normal prose.
- Carried over: trial-run results shown in a dedicated dialog and really landing on the current machine (1.0.117 improvements).

**版本说明**：1.0.118 是其上一桌面点版本，主题为「Client 技能只跑在发起用户那台机器（A 口径）+ 收尾剥离内部协调 JSON」；本机桥日志写系统临时目录。
**Version note**: 1.0.118 is the previous point release, themed “Client skills run only on the originating user’s machine (policy A) + strip internal coordination JSON from final replies”.

---

# AG-UI 群聊桌面版 1.0.117 发布说明（上一版本）
# AG-UI Group Chat Desktop 1.0.117 Release Notes (previous point release)

## 新增（中文）
- **本机(client)技能的“试运行”真正落到当前机器**：技能库试运行 `/run` 里，`ExecutionLocation=Client` 的 **dotnet(C#)** 与本机 **shell** 技能（如系统内置的 `服务状态`）会经<br>本机桥（优先按浏览器上报的 `clientId` 路由；否则落在平台级桥，即当前机器）在本机**真实编译/执行并回传结果**——不再误把 PowerShell 正文交给服务端 Linux bash 而报 `Get-Service: command not found`。（前置：本机已启动 `AguiGroupChat.NativeBridge`。）
- **`/run` 对任何 Client 技能都不再回落服务端**：凡 `ExecutionLocation=Client`、但没有“进程内可直接执行”形态（http / prompt 标成 client 等）的边缘情形，试运行返回明确指引（请到挂载它的数字员工对话、由其本机桥/前端在本机执行，或改 return server 后再试），而不再静默用服务端 Runner 跑一个“名为客户端技能”的东西。
- **技能库“试运行”结果展示升级为独立弹窗**：在技能库列表（或编辑表单）点 ▶ 后，结果出现在专门“试运行结果”弹窗（列表场景）或表单下方（编辑场景），不再写进不可见角落。
- **生成工具的“要不要思考”改为按任务自动判定**：结构化/格式型生成（技能生成、一键编排、试运行建议入参、技能自测与修复、图谱抽取、群名等）一律用常规对话模型，不因全局“思考模式”切换到 reasoner（reasoner 对严格 JSON/短 token 又慢又易空/超时）；复杂方案型任务在常规模型上“先想后给”（先简述取舍再给最终 JSON）。角色人设、指派引导等开放型生成仍随全局思考模式。
- **长任务并发保护与可“停止”**：技能生成 / 组织编排预览在跑时，其它可触发长生成的按钮置灰、防并发；只读生成任务提供“停止生成”（AbortController 取消，不写库，安全）；写库的“确认并创建(apply)”刻意不暴露停止以保数据完整性。
- **修复“生成为空/很慢”**：技能/编排等 use `deepseek-reasoner`（思考模式开时）会慢慢至空——现在是仅对话等重要推理才走 reasoner，结构化工具用常规模型（实测技能生成数秒、一键编排 10s 内 done）。

## New (English)
- **Client-located skills now truly trial-run on the current machine**: in the skill-library trial run (`/run`), `ExecutionLocation=Client` **dotnet (C#)** and local **shell** skills (e.g. the built-in `服务状态`) are sent over the native bridge (prefer the reported `clientId`; else the platform bridge = the current machine) and really compile/execute locally and return results — no longer handing a PowerShell body to the server’s Linux bash (the `Get-Service: command not found` error). Prerequisite: run `AguiGroupChat.NativeBridge` on this machine.
- **`/run` never server-runs a Client skill**: any `ExecutionLocation=Client` skill lacking an in-process executable form (e.g. http/prompt mislabelled client) now returns clear guidance instead of silently running server-side.
- **Trial-run results shown in a dedicated result dialog** (list view) or under the edit form (editing view).
- **“Thinking vs not” is now auto-decided by task**: structured/formatting generators (skill generation, one-click orchestration, trial-input suggestion, self-test & repair, graph/entity extraction, group naming) always use the regular chat model — they no longer fall into `deepseek-reasoner` just because global “thinking mode” is on (reasoner is slow and often empty on rigid JSON / short tokens). Complex plan-shaped tasks do a “deliberate-first-then-JSON” pass on the fast model, while persona/role/assignment-guidance generators still follow global thinking.
- **Long-run concurrency guard + cancellable “Stop”**: while skill/orchestration previews generate, other long-trigger buttons are greyed out; read-only generation offers a Stop (AbortController; safe, writes nothing); the persisting “Confirm & create” is deliberately not cancellable to protect data integrity.
- **Fixed “generation returns empty / too slow”**: structured tools use the fast model (measured: skill generation seconds; orchestration done in <10s).

**版本说明**：1.0.117 是其上一桌面点版本，主题为「本机(client)技能的试运行真正落到当前机器 + 生成工具“按任务自动决定思考” + 长任务互斥/可取消 + 试运行结果独立弹窗」；已构建 Windows 1.0.117 MSI。本机桥桥日志改写入系统临时目录。
**Version note**: 1.0.117 is the previous point release themed “Client-located trial runs land on the real current machine via the bridge; generators decide thinking type by task; long-run tasks gray out & are cancellable; trial results shown in a dialog”. A Windows 1.0.117 MSI was built. Native-bridge logs moved to the system temp directory.

---

# AG-UI 群聊桌面版 1.0.116 发布说明（1.0.117 之前的点版本）
# AG-UI Group Chat Desktop 1.0.116 Release Notes (previous point release)

## 新增（中文）
- **新的技能类型 `dotnet`（C#）**：技能正文可写 C# 源码（含 `public static string Run(string input)` 入口），运行时经 Roslyn 编译后执行。服务端（`server`）执行的 dotnet 在 Hub 进程内的可回收 `AssemblyLoadContext` 中运行（受限引用白名单 + `AllowUnsafe=false` + 超时 / 输出上限）；独立桥 `AguiGroupChat.NativeBridge` 新增 `DotnetRunner`，能在本机执行 `kind=dotnet` 隧道任务（Source = C#），因此浏览器所在主机的客户端 dotnet 技能由本机桥运行——浏览器自身无法编译 C#，客户端 dotnet 需触发者批准后经桥执行。
- **dotnet 权限模型（管理员建、人人可跑）**：`dotnet` 与 `shell`/`http` 同属特权桶——**仅系统管理员可创建 / 修改 / 删除**；自然语言生成 `/generate` 也只为系统管理员产出 `dotnet`。但**对现有 dotnet 技能的运行对全体登录用户开放**：任何登录用户可试运行服务端 dotnet、或经数字员工 / 客户端执行由本机桥运行，无需管理员。
- **技能库“试运行”自动建议示例入参**：试运行前先经 `POST /ag-ui/skills/{skillId}/suggest` 由模型依据技能描述 / 正文自动推导并预填一段代表性的示例输入，再 `POST /run` 执行，降低试运行门槛。
- **一键组织编排 apply 冒烟自测 + 自修复**：落库前先对服务端执行的技能做冒烟：`prompt` 用样例跑一次、`http` 仅静态校验 method/url（不外呼）、server shell 仅当 `Agents.SkillAutoTestServerShell`（`AGENTS_SKILL_AUTOTEST_SHELL`，默认 true）开启才盲跑，client / 隧道类跳过；失败项由大模型自动修复（最多 3 次）。apply 返回 `smoke[]`（`{skillId,skipped,ok,attempts,repaired,lastError}`），UI 在创建后于通知中心展示。
- **Shell 脚本写盘 BOM 修复**：服务端 `SkillRunner` 曾以带 UTF-8 BOM 的方式写 shell 脚本，导致 bash 下即使是合法首条命令也会失败；现改为写入<b>无 BOM 的 UTF-8</b>。
- docker-compose 新增暴露 `AGENTS_SKILL_AUTOTEST_SHELL`（对应选项 `Agents.SkillAutoTestServerShell`）。

## New (English)
- **New skill kind `dotnet` (C#)**：a skill body can be C# source exposing `public static string Run(string input)`，compiled at run time with Roslyn and executed. Server-side (`server`) dotnet runs inside the Hub process in a collectible `AssemblyLoadContext`（constrained reference allowlist + `AllowUnsafe=false` + timeout / output caps）；the standalone bridge `AguiGroupChat.NativeBridge` gains a `DotnetRunner` that can execute `kind=dotnet` tunnel tasks (Source = C#) on the local host, so client dotnet skills on the browser's host run via the bridge——a browser itself cannot compile C#，and client dotnet runs over the bridge after the triggerer approves.
- **dotnet permission model（admin-create，everyone-can-run）**：`dotnet` shares the privileged bucket with `shell`/`http`——**only system admins can create / edit / delete**；natural-language `/generate` also yields `dotnet` only for system admins. But **running an existing dotnet skill is open to all logged-in users**：any user can trial-run a server dotnet skill，or have a client-executed one run over the native bridge，no admin needed just to run.
- **Skill library “trial run” auto-suggests an example input**：before running，`POST /ag-ui/skills/{skillId}/suggest` has the model derive and pre-fill a representative example input from the skill description / body，then `POST /run` executes it.
- **Orchestration apply now smoke-tests + self-repairs**：before persisting，server-executed skills are smoke-tested——`prompt` runs once against a sample，`http` is only config-linted (method / url validity，no outbound call)，server `shell` is blind-run only when `Agents.SkillAutoTestServerShell`（`AGENTS_SKILL_AUTOTEST_SHELL`，default true）is on，client / tunnel kinds are skipped; failures are auto-repaired by the model（up to 3 attempts）。Apply returns a `smoke[]`（`{skillId,skipped,ok,attempts,repaired,lastError}`）that the UI surfaces in the notification center after creation.
- **Shell script BOM fix**：the server-side `SkillRunner` used to write shell scripts with a UTF-8 BOM，breaking even a valid first command under bash；scripts are now written as BOM-less UTF-8.
- docker-compose now exposes `AGENTS_SKILL_AUTOTEST_SHELL`（option `Agents.SkillAutoTestServerShell`）。

**版本说明**：1.0.116 是其上一桌面点版本，主题为「dotnet（C#）技能 + 试运行建议与编排冒烟自检」并包含 shell BOM 修复。
**Version note**: 1.0.116 was the previous point release, recording dotnet (C#) skills, trial-run input suggestion & orchestration smoke/self-repair, plus the shell BOM fix.

---

# AG-UI 群聊桌面版 1.0.108 发布说明
# AG-UI Group Chat Desktop 1.0.108 Release Notes

## 新增（中文）
（提交 d14f210）
- **图片理解（视觉）**：群消息携带图片附件时，该轮数字员工回复自动路由到视觉模型、以多模态 base64 内联看图作答；纯文本消息仍走常规 / 思考模型。视觉默认模型 `deepseek-v4-flash-vision-exp`（可用 `Agents:VisionModel` 覆盖，`Agents:VisionEnabled` 默认开启为总开关；`mock` 提供方不支持视觉）。图片从附件库读取、无需另存文件，发图 / 审批协作流程不变。本期已构建 Windows 1.0.108 MSI。

## New (English)
(commit d14f210)
- **Image understanding (vision)**: when a group message carries an image attachment, that turn is auto-routed to a vision model that sees the image (fed inline as base64 multimodal content); plain text messages keep the normal / thinking model. Default vision model `deepseek-v4-flash-vision-exp` (override with `Agents:VisionModel`; `Agents:VisionEnabled` defaults on as the master switch; the `mock` provider has no vision). The image is read from the attachment store — no extra file — and the image-upload / approval workflow is unchanged. A Windows 1.0.108 MSI was built for this release.

**版本说明**：1.0.107 → 1.0.108 为点版本，主题为「图片理解（视觉）」，并已构建 Windows 1.0.108 MSI。
**Version note**: 1.0.107 → 1.0.108 is a point release adding image understanding (vision); a Windows 1.0.108 MSI was built.

---

# AG-UI 群聊桌面版 1.0.107 发布说明
# AG-UI Group Chat Desktop 1.0.107 Release Notes

## 新增（中文）
（提交 b60b9c7）
- **自动编排重名去重（自动改名避重）**：生成的数字员工 / 技能 id 与原库同名时，自动追加 `_2/_3` 改名继续保存，不再整体失败、不覆盖已有资产，并自动同步方案内引用（技能挂载、上下级连接、客服知聚成员、返回 id）。
- **组织架构交互**：双击数字员工节点可直接打开编辑表单；组织关系连线同一对端点间的多条线会**横向错开**避免完全重叠；编辑返回上下文优化——从组织架构进入编辑，退出后回组织架构，再关闭回数字员工管理列表（从列表进入则回列表）。

## New (English)
(commit b60b9c7)
- **Auto-rename on orchestration id collisions**: when a generated digital-employee / skill id collides with an existing library entry, it is auto-renamed with a `_2/_3` suffix and saved anyway — no more whole-apply failures, no overwriting existing assets — and all in-plan references (skill mounts, up/down connections, support-circle members, returned ids) are remapped to the final ids.
- **Org-chart interactions**: double-clicking a digital-employee node now opens its edit form; edges connecting the same pair of endpoints are **laterally offset** so they no longer overlap; edit-return context is optimized — editing from the org chart returns to the org chart after exit, then closes back to the digital-employee list (editing from the list returns to the list).

**版本说明**：1.0.106 → 1.0.107 为点版本，主题为「自动编排重名去重 + 组织架构交互」。全量 714 个单元 / 集成测试通过。
**Version note**: 1.0.106 → 1.0.107 is a point release focused on auto-rename collision handling during orchestration and org-chart editing/rendering interactions. All 714 unit / integration tests pass.

---

# AG-UI 群聊桌面版 1.0.106 发布说明
# AG-UI Group Chat Desktop 1.0.106 Release Notes

## 新增（中文）
（提交 386c7ee）
- **客服知聚打字指示（typing）**：客服 / 数字员工输入时，顾客参与者能看到「客服正在输入」；顾客输入时客服能看到；顾客之间互不可见，与消息隔离一致（此前客服知聚里 typing 双向都不达）。
- **智能体上下文作用域**：客服知聚的智能体上下文窗口现在会包含本次触发顾客的隔离会话（顾客自己的提问 + 定向回给该顾客的客服消息），客服能「记得」该顾客之前聊过的内容，不再像新对话一样重答；其他顾客的私聊不进上下文。

## New (English)
(commit 386c7ee)
- **Support-circle typing indicators**: while a staff member / digital employee is typing, customer participants see “staff is typing”; while a customer types, staff see it; customers never see each other's typing, consistent with message isolation (previously typing was not delivered in either direction in support circles).
- **Agent context scoping**: the support-circle agent context window now includes the triggering customer's isolated conversation (the customer's own questions plus staff replies directed to that customer), so staff can “remember” that customer's prior dialogue instead of answering as if it were a fresh chat; other customers' private chats stay out of context.

**版本说明**：1.0.105 → 1.0.106 为点版本，主题为「客服知聚 typing 与智能体上下文修复」。全量 714 个单元 / 集成测试通过。
**Version note**: 1.0.105 → 1.0.106 is a point release that fixes support-circle typing delivery and scopes the agent context to the triggering customer. All 714 unit / integration tests pass.

---

# AG-UI 群聊桌面版 1.0.105 发布说明
# AG-UI Group Chat Desktop 1.0.105 Release Notes

## 新增（中文）
- **客服知聚权限修复随本版首发**：普通顾客（参与者、非成员）可批准其触发的客服技能执行（此前会被「决策者不是群成员」拒绝）；网关仍强校验必须是触发者本人。本版桌面包首次实际包含该修复。（提交 4dd9e94）
- **自动化验证工具增强**：`tools/ui-orchestrate-flow.mjs` 现在会一并清理编排创建的数字员工与技能，避免留下测试残留；技能删除带保护（仅删不再被现存数字员工引用的，避免误删用户团队同名的真实技能）。

## New (English)
- **Support-circle permission fix ships in this build**: ordinary customers (participants, non-members) can now approve the customer-service skill execution they triggered (previously rejected as “decider is not a group member”); the gateway still requires the approver to be the triggerer. This is the first desktop build to actually include the fix. (commit 4dd9e94)
- **Automation tooling hardening**: `tools/ui-orchestrate-flow.mjs` now also deletes the digital employees and skills it creates during orchestration to avoid test residue; skill deletion is protected so it only removes skills no longer referenced by any remaining digital employee, avoiding accidental deletion of same-named real skills used by your teams.

**版本说明**：1.0.104 → 1.0.105 为点版本，主题为「随本版正式带上客服知聚权限修复 + 文档与验证工具同步」。全量 711 个单元 / 集成测试通过。
**Version note**: 1.0.104 → 1.0.105 is a point release that formally ships the support-circle permission fix along with doc and verification-tool updates. All 711 unit / integration tests pass.

---

# AG-UI 群聊桌面版 1.0.104 发布说明
# AG-UI Group Chat Desktop 1.0.104 Release Notes

## 新增（中文）
- **一键组织编排·生成过程可视化（方案 C）**：新增流式 SSE 端点 `POST /ag-ui/agents/orchestrate/stream`，DeepSeek 逐 token 流式吐出生成过程，前端实时展示原始生成文本，并基于已见 JSON 实时统计「已识别 N 名数字员工 / M 个技能」；生成结束下发完整结构化方案供确认。`AgentOrchestrator` 新增 `StreamTextAsync`（真实模型流式 / mock 分片模板）。
- **编排 apply 可勾选「同时创建客服知聚」**：落库组织方案时可把方案里的数字员工作为客服团队建群（`GroupKind.Support`），并逐个注册触发规则（`@` 即可应答），直接把方案上线服务顾客。
- **客服知聚权限修复**：普通顾客（参与者、非成员）现在可以批准其触发的客服技能执行——客服知聚的核心业务流程打通（此前会被「决策者不是群成员」拒绝）；网关仍强校验必须是触发者本人。

## New (English)
- **One-click org orchestration — generation process visualization (Plan C)**: new SSE endpoint `POST /ag-ui/agents/orchestrate/stream` streams DeepSeek's output token-by-token; the frontend shows the raw generation in real time and live counts of “N digital employees / M skills identified”; a complete structured plan is delivered when generation finishes for confirmation. `AgentOrchestrator` gains `StreamTextAsync` (streams for real models; chunked template for mock).
- **Orchestrate apply can opt in to “create a support circle”**: when persisting the plan, the plan's digital employees can be assembled into a support circle (`GroupKind.Support`) with trigger rules registered per employee (@ triggers a reply), putting the plan online to serve customers immediately.
- **Support-circle permission fix**: ordinary customers (participants, non-members) can now approve the customer-service skill execution they triggered — the core support-circle flow now works (it used to be rejected as “decider is not a group member”); the gateway still strictly requires the approver to be the triggerer themselves.

## 修复（中文）
- **本机 shell 技能经隧道执行修复**：通过编排 / 表单创建的本地（client 执行）shell 技能若未携带 `ClientRunner`，现在会自动从命令体生成（bfbce39），且网关执行时兜底从命令体合成——已有技能无需重建即可在本机经隧道执行（此前提示「该技能非本机 shell，无法经隧道执行」）。
- **编排方案兼容真实模型的 `JSON 对象` 技能体**：http/shell 技能 body 常被模型写成 JSON 对象，解析时经 `FlexibleBodyConverter` 归一化为字符串（9d6b9e5）。

## Fixed (English)
- **Local shell skills now execute over the tunnel**: local (`client`) shell skills created via orchestration / form without a `ClientRunner` are now auto-derived from the command body (bfbce39), and the gateway synthesizes one at execution time as a fallback — existing skills work over the tunnel without re-creation (previously “this skill is not a local shell type, can't run over the tunnel”).
- **Orchestration tolerates JSON-object skill bodies from real models**: http/shell skill bodies are often emitted as JSON objects; parsing now normalizes them to strings via `FlexibleBodyConverter` (9d6b9e5).

## 工具（中文）
- 新增 `tools/ui-orchestrate-flow.mjs`（Playwright）浏览器自动化：一键编排 → 建客服知聚 → API 核验 → 清理，见 `tools/README-playwright.md`。

## Tools (English)
- Added `tools/ui-orchestrate-flow.mjs` (Playwright) browser automation covering orchestrating → creating a support circle → API verification → cleanup; see `tools/README-playwright.md`.

**测试**：全量 711 个单元 / 集成测试通过。
**Tests**: all 711 unit / integration tests pass.

---

# AG-UI 群聊桌面版 —— 增补：RBAC 权限分层 + 安全加固
# AG-UI Group Chat Desktop — Addendum: RBAC layering + security hardening

## 新增（中文）
- **平台级 RBAC 分层**：账号增加 `PlatformRole`（`user / operator / admin / superadmin`）。首个注册账号自举为超级管理员；新增 Operator（只读运维）角色；`GET|POST /ag-ui/admin/roles` 由超级管理员管理角色矩阵；`/status`、`/usage`、`/audit`、`/bridge-health`、`/bridge-capabilities`、`/metrics` 降为 Operator 可读。管理员控制台「用户管理」新增角色下拉（仅超级管理员可见）。
- **群级 RBAC 收敛**：不允许把成员标为 Owner（Owner 仅群主转让得到）；授予/撤销群管理员仅群主可操作；新增 `POST /ag-ui/group/transfer-owner` 群主转让（转让后原群主降为 Admin）。
- **频道级 RBAC 保持**：`canInvokeAgents` / `canApprove` 由群主/管理员按成员管理，默认全允许。
- **安全加固**：HTTP API 移除 `?memberId=` 身份回退；客户端技能桥新增 `ClientTool:RequireAdmin` 部署开关（共享多用户部署建议开启）；模型 API Key / TOTP 密钥落盘加密（`SecretVault`）；快照可选 HMAC 签名并防静默清空。

## New (English)
- **Platform RBAC layering**: accounts gain a `PlatformRole` (`user / operator / admin / superadmin`). The first registered account self-bootstraps as Super Admin; a read-only `Operator` role is introduced; `GET|POST /ag-ui/admin/roles` lets a Super Admin manage the role matrix; `/status`, `/usage`, `/audit`, `/bridge-health`, `/bridge-capabilities`, `/metrics` are now readable by Operator+. The admin console's User Management gains a role dropdown (visible to Super Admin only).
- **Group RBAC tightening**: members can no longer be marked `Owner` via member update (`Owner` is only obtained through ownership transfer); only the Owner may grant/revoke group admin; add `POST /ag-ui/group/transfer-owner` (the previous owner becomes a group admin after transfer).
- **Channel RBAC preserved**: `canInvokeAgents` / `canApprove` per-member limits remain manageable by owner/admin, defaulting to allow.
- **Security hardening**: HTTP APIs no longer trust a `?memberId=` identity fallback; the client-skill bridge gains a `ClientTool:RequireAdmin` deployment switch (recommended for shared multi-user deployments); model API keys & TOTP secrets are encrypted at rest via `SecretVault`; snapshots support optional HMAC signing and are protected from silent data loss.

详情见 `docs/RBAC.md`。See `docs/RBAC.md` for details.

---

# AG-UI 群聊桌面版 1.0.103 发布说明
# AG-UI Group Chat Desktop 1.0.103 Release Notes

## 新增（中文）
- **新增：客服知聚（`kind=support`）**。在公有 / 私有知聚之上新增客服知聚：创建者建群时拉入的**客服团队**（真人用户或数字员工，`Role=Admin`）为知聚的全部成员，可看到**所有会话**；客服知聚对所有用户可见、可进入，无需邀请。
- **顾客非成员、会话隔离**：普通用户进入客服知聚时**不会加入成员表**（不占成员名额、不出现在成员清单），而是登记为一个带 30 分钟活动 TTL 的**顾客参与者**，获得与客服团队聊天的**独立会话**；每位顾客之间彼此隔离（A 看不到 B 的会话），客服回复某顾客定向到该顾客，客服之间的内部沟通仅客服可见。
- **接口**：`POST /ag-ui/group/create` 增加 `kind=normal|support`；`GET /ag-ui/group/discover` 发现全部客服知聚（对已登录用户可见，含 `isMember`/`hasEntered`）；`POST /ag-ui/group/{groupId}/enter` 进入（非成员登记为顾客参与者）。
- **前端**：创建弹窗增加「普通知聚 / 🛟 客服知聚」选择；侧栏自动展示可进入的客服知聚（蓝色标签 + 非成员「进入」标）；进入后作为参与者直接聊天。

## New (English)
- **New: support circles (`kind=support`)**. On top of public/private circles: the invited **support team** (humans and agent employees, `Role=Admin`) is the circle's entire membership and sees **every conversation**; a support circle is discoverable and enterable by all users without invitation.
- **Customers are non-members with isolated conversations**: entering a support circle does **not** add you to the member roster (no headcount, not listed); you register as a time-limited **customer participant** (30-min activity TTL) with your **own isolated conversation** with staff. Customers are isolated from each other; staff replies target the specific customer; internal staff chats stay staff-only.
- **APIs**: `POST /ag-ui/group/create` now takes `kind=normal|support`; `GET /ag-ui/group/discover` lists support circles (visible to any logged-in user, with `isMember`/`hasEntered`); `POST /ag-ui/group/{groupId}/enter` to enter (registers non-members as customer participants).
- **Frontend**: create dialog gains a "Normal / 🛟 Support circle" picker; the sidebar automatically shows enterable support circles (blue badge + an "Enter" chip for non-members); entered participants can chat directly.

---

# AG-UI 群聊桌面版 —— 新增：内网穿透（反向隧道）
# AG-UI Group Chat Desktop — New: NAT traversal (reverse tunnel)

## 新增（中文）
- **新增：反向隧道（HTTP/SSE）让「无公网 IP」的内网机本机桥被公网 Hub 调用**。内网机上的本机桥主动出站连公网 Hub 并注册（`--agent` 绑定数字员工）；Hub 把对该员工的客户端技能任务沿隧道下行推给内网桥执行，结果回传模型继续作答——无需入站公网端口、无需第三方隧道。
- **用法**：`NativeBridge --tunnel <hub> [--agent <id>] --tunnel-token <token>`；Hub 侧 `NativeTunnel__Token`（或 appsettings `NativeTunnel:Token`）校验。前端桥地址仍填 Hub 自身即可，网关经隧道转发。
- **平台级桥（信任整个平台）**：不再强制指定数字员工 id——`--tunnel` 时不填 `--agent` 即注册为平台级桥（`*`），一座桥服务任意数字员工的客户端技能；某员工有专属桥时优先用专属桥，否则回落到平台级桥。
- **安全加固**：逐 agent 专属隧道令牌（`NativeTunnel:AgentTokens__<agentId>`，优先于全局 `NativeTunnel:Token`）；`POST /ag-ui/native-tunnel/result` 回传需携带该 agent 有效令牌（本机桥自动带 `--agent/--tunnel-token`），防伪造结果 / 无令牌刷接口；`connect` 与 `result` 端点带内存滑动窗口限流（默认 120 / 600 次每 IP 每分钟，可配）。
- **简化：网页端取消「本机工具桥」手动配置**。移除「修改资料」里的桥地址 / 令牌输入与「一键检测」——本机 shell 执行统一经反向隧道自动路由（起桥 `--tunnel` 即生效，前端无需填任何桥配置）；无隧道桥时回落到服务器端 `/ag-ui/client-tool`。桌面版本就无需桥配置。

## New (English)
- **New: reverse tunnel (HTTP/SSE) so an intranet local bridge with no public IP can be called by the public Hub**. The bridge on the intranet host dials out to the public Hub and registers (binding a digital employee via `--agent`); the Hub pushes that employee's client-skill task down the tunnel for the bridge to execute, posting the result back so the model can continue—no inbound public port, no third-party tunnel. Usage: `NativeBridge --tunnel <hub> [--agent <id>] --tunnel-token <token>`; the Hub validates via `NativeTunnel__Token` (or appsettings `NativeTunnel:Token`). The frontend bridge URL still points at the Hub itself; the gateway forwards over the tunnel.
- **Platform-wide bridge (trust the whole platform)**: `--agent` is now optional under `--tunnel`—omit it to register as a platform-wide bridge (`*`) that serves any employee's client skills; an employee-specific bridge takes precedence when present, otherwise execution falls back to the platform-wide bridge.
- **Security hardening**: per-employee tokens (`NativeTunnel:AgentTokens__<agentId>`, takes precedence over global `NativeTunnel:Token`); the `POST /result` endpoint now requires a valid token for that agent (the bridge sends `--agent`/`--tunnel-token` automatically); in-memory sliding-window rate limiting on both `connect` and `result` (default 120 / 600 per IP per minute, configurable).
- **Simplified: the web UI's manual “Native Tool Bridge” config is gone**. Removed the bridge URL / token fields and one-click Detect from Profile; local shell execution now routes automatically through the reverse tunnel (just start the bridge with `--tunnel`), falling back to the server-side `/ag-ui/client-tool` when no tunnel bridge is connected.

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
