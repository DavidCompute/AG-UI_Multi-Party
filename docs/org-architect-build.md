# 「组织架构构建师」的组织打造算法
# How the “Org Architect” builds an organization

> 依据当前代码实现的算法说明。角色：**数字员工 `org_architect`（组织架构构建师）**，是一位“组织角色/部署员”。
> 关键对象与缩写：
> - `org_design` —— 组织角色挂载的设计稿技能（prompt）。
> - `org_deploy` —— 库中一条 `kind=org_deploy` 的受控动作技能（挂到 org_architect 的 `SkillDefIds`）。
> - `org_commit` / `org_plan_draft` —— 组织角色由系统在代码层自动加挂的两个原生工具（`AgentCatalog.Create`）。
> - `OrgApplyEngine` —— **唯一的官方落库引擎**（一键编排 apply 与 org_architect 共用）。

Based on the actual code. `org_architect` is an “organization builder / deployment” digital employee mounted with org skills; it has two auto-added native tools (`org_commit`, `org_plan_draft`) plus the prompt skill `org_design`, and writes only through the single shared `OrgApplyEngine`.

---

## 1. 角色是谁、身上挂了什么（Who is it / what it carries)

`org_architect` 由管理员在库里建成并挂：
- `skillDefIds = ["org_design", "org_deploy"]`（数据态）。
- 代码层自动加挂两个原生工具（只要挂 `org_design` 的组织角色都会）：
  1. `org_commit`：把“最终组织稿”整支落库 / 按 key 覆盖；仅平台管理员能真写，普通用户只拿“请管理员放行”说明。
  2. `org_plan_draft`：一键式 / 结构化整支初稿（复用「一键组织编排」`AgentOrchestrator`），只生成不落库。
- `org_design` 的**描述**在构建期会额外注入一段「当前平台可用运行能力概览」（`OrgDesignRunCapabilityNote`），提示如何按岗位职责挑 kind / executionLocation（含 server/client 与需管理员建/放行的类型），避免一律写 pure prompt。

### 1. Ingredients

Built in store with `skillDefIds = ["org_design","org_deploy"]`. At build time (`AgentCatalog.Create`) any org role gets two more native tools: `org_commit` (commit/overwrite a whole team, admin-gated) and `org_plan_draft` (one-shot structured first draft via the same `AgentOrchestrator` as web one-click orchestration; draft-only). `org_design`’s description is enriched with a runtime “available run capabilities” note so the model picks real kinds (shell/http/prompt/dotnet × server/client) instead of pure-prompt soft skills.

---

## 2. 总体算法（自上而下）

```
用户@org_architect 一句需求 / 一句“就按这版落库”
  │
  ├(1) 路由判定——整支建还是局部改
  │     · 要“整支新的组织/一整套能力/更完整架构” → 走 org_plan_draft（一键式）；
  │     · 只是“对已有支做局部小改（个别岗位/技能）” → 走 org_design 逐条精致。
  │
  ├(2) 出稿
  │     · org_plan_draft: AgentOrchestrator.GenerateAsync(一句话) →
  │         一次产出结构化方案 JSON{title, agents[{agentId,… skillIds, assignmentIds, escalationAgentId, relayToAgentId}], skills[{kind, body, executionLocation, requiresApproval}]}
  │     · org_design: 结合注入的“运行能力概览”，对话式逐条设计并攒成同一结构的“最终稿 JSON”。
  │
  ├(3) 呈现 + 确认
  │     · 以人类可读的概览逐岗位贴给用户核对；
  │     · 未获用户明确认可前绝不落库。
  │
  ├(4) 管理员放行触发 org_commit(teamKey, planJson)
  │
  └(5) OrgApplyEngine 整支落库（见§4），成功→可选建“客服知聚”并把数字员工加入
     失败→回吐原因、修正后用同一 teamKey 重试（覆盖语义）
```

### 2. Overall flow

1. **Route** the request: whole new team/complete capability set → `org_plan_draft` (one-shot structured); small tweak to an existing team → `org_design` (conversational, refined per item).
2. **Draft**: `org_plan_draft` calls `AgentOrchestrator.GenerateAsync(brief)` and yields `{title, agents[…], skills[…]}` in one pass; `org_design` iteratively drafts and accumulates the same JSON structure.
3. **Show & confirm** a human-readable per-role preview; **never** commit before the user explicitly agrees.
4. An admin authorizes → `org_commit(teamKey, planJson)`.
5. `OrgApplyEngine` writes the whole batch (see §4); optional “客服知聚”; on failure the agent fixes per error and retries under the same `teamKey` (overwrite semantics).

---

## 3. 角色路由与权限为何这样（Why it routes this way / privileges)

- 挂 `org_deploy`/`org_design` 的组织角色**不进协调计划/按查批量执行**：`HasMountedOrgDeploy(...)` 使 `isSkillPlanner` 关闭，落到**普通带工具流式 run**——这样 `org_commit` / `org_plan_draft` 才是“真能由模型当 function-call 调用的原生工具”，而不是被拿去当一次性排查技能。
- `org_plan_draft`/`org_design` 只**出稿**：是无副作用、仅一次模型生成的只读动作，任何被挂的用户都能触发来产稿。
- `org_commit` 写库前按 **生效平台角色 ≥ Admin（含 SuperAdmin）** 收口：客服/普通用户只能拿到“需管理员放行”的预览，绝不写库；库里始终按 `teamKey` 只保留最新一版。

### 3. Routing & privilege

Org roles don’t enter the coordinated-plan/batch executor (a mounted `org_deploy` turns `isSkillPlanner` off), so the two native tools stay callable as real function calls. Draft tools are read-only (any user may ask for a draft). Only `org_commit` writes, gated by effective platform role ≥ Admin (incl. SuperAdmin); ordinary users get a preview + “needs admin approval”, never a write.

---

## 4. OrgApplyEngine 落库算法（唯一引擎语义）

**输入`ownerId`、`isAdmin`、`skills[]`、`agents[]`、`createSupportCircle?`。步骤：**

1. **技能归一（去重 + 重命名）**：把每个技能先规范 id，与库内现有 id 冲突则自动追加后缀；记录 `原→新` 映射供数字员工引用重映射；名称/描述/正文不能为空；`shell/http/dotnet` 类型需 `isAdmin`，否则抛“仅管理员可建”，置为 forbidden。
2. **executionLocation 规约 + 审批**：`client` → Client 且强制需批准；`shell` 一律需批准；生成 `ClientRunner`（客户端 shell 缺 runner 时自动由正文构造）。
3. **服务端技能冒烟自检 + 自动修复**（`SkillAutoFixer`，至多 3 次）：先校验再落库，失败不至于部分写入；自测通过才保存技能。
4. **数字员工归一**：agent id 冲突自动改名（追加后缀），`twin_` 前缀冲突时再加保护前缀；昵称不能为空；校验每个 `skillIds` 引用的技能都出现在本批 skills（引用一致性）。
5. **校验连接目标**：`assignmentIds / escalationAgentId / relayToAgentId` 必须指向本批数字员工（不能悬空/错指）。
6. **写库**：先 `skillCatalog.Upsert` 技能、再按映射 Upsert 数字员工（挂载重映射后的 skillDefIds，连接目标也重映射）。
7. **可选建“客服知聚”**：把本批数字员工一次性 `CreateGroupAsync(GroupKind.Support)` 建客服群并在组内注册它们的触发规则（mention）、建好后把员工加到群。
8. **返回**：建出的员工 id / 技能 id /（若有）客服群 id / 每个技能的冒烟结果 `smoke[]`。

> **覆盖/keep-latest 语义**：`org_commit` 一侧会先在 `OrgTeamStore` 按 `teamKey` 查到上一版，`Retire`（清掉上一批登记的数字员工/技能），再以同 key 重建——因此“用同一 teamKey 反复落库 = 覆盖，库里只留最新一版”。（同引擎也被网页“一键编排 apply”复用，官方语义一致。）

### 4. OrgApplyEngine write algorithm (single engine)

Given `ownerId,isAdmin,skills[],agents[],createSupportCircle?`:

1. **Normalize skills** (ASCII id, auto-suffix collision → keep `orig→new` map), require name/description/body; `shell/http/dotnet` only if `isAdmin`.
2. **executionLocation + approval**: `client`→Client (forced approval), `shell`→approval; auto `ClientRunner` if missing.
3. **Server-skill smoke + auto-repair** (`SkillAutoFixer`, ≤3) before persisting — no partial writes.
4. **Normalize agents** (auto-suffix collisions, guard `twin_` prefix), require nickname, and ensure each `skillIds` reference resolves in this batch.
5. **Validate connection targets** (`assignmentIds/escalationAgentId/relayToAgentId`) point to batch agents.
6. **Persist**: upsert skills first, then agents with remapped skill ids and connections.
7. **Optional “客服知聚”**: create a `kind=support` group with the new agents and register their trigger rules (mention).
8. **Return**: created agent/skill ids, (optional) support-group id, and `smoke[]` per skill.

> Overwrite/keep-latest: `org_commit` looks up the prior version by `teamKey` in `OrgTeamStore`, retires the previous registered batch, then rebuilds under the same key — writing the same key repeatedly overwrites, keeping only the newest version. Web “one-click orchestration apply” shares this same engine.

---

## 5. kind / executionLocation 选择原则（算法要让模型这么挑）

- `prompt`：提示词/流程模板，无外部执行——最稳妥、任何岗位可用（但不要把什么都写成纯 prompt）。
- `http`：外部 HTTP(S)，由服务端执行（访问本机/内网受 `Agents:AllowPrivateSkillEndpoints` 约束）。
- `shell`：执行命令；`server`=服务端沙箱、`client`=发起用户本机/内网桥（需批准）。Windows 用 PowerShell；体系里会显式拒绝“把 client / PowerShell 技能误当服务端 bash 跑”并给“需在本机经 NativeBridge/PowerShell 执行”的引导。
- `dotnet`（C#）：`server`=服务端 Roslyn 受限沙箱；`client`=桌面/本机桥在本机编译执行。
- 连接原则：2~6 名数字员工，形成“主管 → 若干执行岗”的层次；执行岗 `assignmentIds` 留空、`escalationAgentId` 指向主管；技能 1~6 个、贴合岗位。

### 5. kind / executionLocation guidance

`prompt`(no exec), `http`(server, SSRF-guarded), `shell`(server sandbox or client via local bridge; approval; PowerShell on Windows — client/PowerShell must never be mis-run as server bash), `dotnet`(C#, server Roslyn or local bridge). Prefer 2–6 agents forming a manager → exec hierarchy, 1–6 skills per role matching real duties.

---

## 6. 一句话总结（TL;DR）

组织架构构建师 = **“路由 →（一键式或逐条）出稿 → 呈现等确认 → 管理员放行 → OrgApplyEngine 唯一引擎整支落库/覆盖 → 可选建客服知聚”**。写库唯一、重名去重、引用校验、服务端技能自测、连接目标校验都收敛在 `OrgApplyEngine`；`org_architect` 与网页“一键编排”走的是**同一套落库代码**，差异只在“怎么拿到初稿”（对话/结构化）与谁放行落库（管理员）。

The Org Architect = **route → draft (one-shot or incremental) → preview & wait for confirmation → admin authorizes → single `OrgApplyEngine` writes/overwrites the whole team → optionally create a support circle**. Dedup/rename, reference validation, server-skill smoke test and connection validation all live in `OrgApplyEngine`; `org_architect` and the web one-click orchestration share the **same write engine** — they differ only in how the draft is produced and who authorizes the write (admin).

---

## 附：权威实现锚点（Code anchors)

- `AgentCatalog.Create`：组织角色加挂 `org_commit` / `org_plan_draft`、给 `org_design` 注入运行能力注记 —— `src/AguiGroupChat.Agents/AgentCatalog.cs`。
- `AgentOrchestrator`：一键式整支出稿的生成引擎与 prompt —— `src/AguiGroupChat.Agents/AgentOrchestrator.cs`。
- `Tools/OrgOneShotDraftTool.cs`：`org_plan_draft` 动作（出稿文案强化“确认后才 commit”）。
- `Tools/OrgCommitTool.cs`：`org_commit`（管理员闸）。
- `OrgTeamManager.cs`(OrgTeamCommitter)：teamKey/覆盖 + 接 `OrgApplyEngine`；`OrgTeamStore` 登记最新账。
- `OrgApplyEngine.cs`：§4 落库算法。
- `AgentGateway` / `HasMountedOrgDeploy`：组织角色不参与者计划路由、走普通 run（工具可被模型调用）。
- 运行态 data：`org_architect` 与 `org_design` 正文在库（非 git）；`skills_backup.json` 为原备份。
