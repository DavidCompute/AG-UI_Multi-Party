# 智能体（数字员工）执行算法
# How a digital employee runs: execution algorithm

> 依据当前代码（`.NET 10`）。总入口：群消息触发 `GroupHub`/`AgentTriggerService` 判定 → `AgentGateway.InvokeAsync` → `InvokeCoreAsync`；分派到若干执行路径后，结果以群内流式事件回灌。
> Back code anchors: `src/AguiGroupChat.Agents/AgentGateway.cs`, `src/AguiGroupChat.Agents/AgentCatalog.cs`, `src/AguiGroupChat.Hub/Messaging/GroupHub.cs`, `src/AguiGroupChat.Hub/Agents/AgentTriggerService.cs`.
> 想“改执行行为而不改代码”的旋钮清单与哪些仍码内固定见姊妹篇：[执行配置：可热改项 / 有效范围 / 能力矩阵](execution-configuration.md)。

---

## 1) 主流程图（Overall flowchart）

```mermaid
flowchart TD
    msg["群内一条消息 / @ 某数字员工"] --> eval{"AgentTriggerService 判定触发方式"}

    eval -- "提及 / 召唤 / 关键词 / 全量 / 语境 OK" --> hub["GroupHub 触发\n(查该角色定义与"谁是触发者)"] --> g[AgentGateway.InvokeAsync]
    eval -- "语境且判定应沉默" --> silent["AGENT_DECIDED_SILENT·静默跳过"]

    g --> core[InvokeCoreAsync: 读角色定义]
    core --> b{配桥接?}
    b -- 是 --> bridge[InvokeBridgeAsync：经 AG-UI 转发外部专家，流式回灌, 外部审批]
    b -- 否 --> p{配编排流水线 Pipeline?}
    p -- 是 --> pipe[InvokePipelineAsync：按步骤依次调子数字员工聚合答复] --> emit(结束·EndAgentMessage)
    p -- 否 --> r{配置角色交接 RelayToAgentId?}
    r -- 是 --> relay[InvokeRelayAsync：整轮转交, 由对方以别名代答]
    r -- 否 --> m{触发语义=提及?}
    m -- 提及 & (有下级指派/提升目标 或 策划可用) --> route[InvokeAssignmentEscalationAsync：组织化路由]
    m -- 其他 --> run[普通带工具流式 run] 

    route --> p1{"先试确定性编排计划\nCoordinatorPlanning && !挂 org_deploy"}
    p1 -- 有计划 --> plan[ExecuteCoordinatedPlanAsync]
    p1 -- 无计划 --> rec[递归指派/提升路由, 深度/环路防护]
    rec -- 全链路无解 --> refuse["(该问题我不在可解决范围) 并保留原始@宿主语义"] --> emit
    rec -- 有解人员→由对方代答自答 --> emit

    plan --> ex{逐项激活 dispatch / server技能 / 客户端技能}
    ex -- dispatch | 服务端技能 --> actSeq[依次执行、结果级联]
    ex -- 客户端技能集中 --> card["合并成「本机一键执行全部」/ 审批卡，本机或经桥执行并回传"]
    plan --> synth["最终一步递归综合(ExecuteRecursiveAnswerAsync)"]

    run --> stream[RunStreamingAsync 流式输出+推理+工具]
    stream --> tool?{模型请求工具?}
    tool? -- 免审批工具 --> fn[执行并注入结果 → 继续流]
    tool? -- 需审批/客户端工具 --> hitl[人机交互卡, 仅触发者可批准/拒绝 → 结果回灌 → 继续或结束]
    tool? -- 无需工具 --> ok["完成流 → 写库/广播(正文+计划卡+思考+调用链)"]
    synth --> emit
    ok --> emit
```

---

## 2) 关键路径说明（Method branch map)

- 桥接（`def.BridgeEndpoint` 或全局 `AguiBridge.Endpoint`）先于一切：本角色不再调本地大模型，`InvokeBridgeAsync` 经 AG-UI（standard/hub 方言）建立外部会话，外部流式回复逐段回灌；外部也带人机交互。
- 编排流水线 `def.Pipeline`：本角色**不自己跑模型**，而是按 `Pipeline` 步骤依次调一个子数字员工一次性 run，把各步结果聚合成本角色对群的回复。
- 角色交接 `def.RelayToAgentId`：整轮委托给被交接方（以本角色昵称/别名代它回复），并阻止 A→B→A 环回。
- 语境触发：`AgentTriggerMode.Contextual` 且 `ShouldSpeak` 判沉默 → 不发任何事件（`AGENT_DECIDED_SILENT`）。
- **带图轮次只换模型、不换能力**：消息（或本话题最近窗口）含图且启用了视觉时，`BuildVisionUserMessageAsync` 把图片像素一并组装成多模态消息，并把 agent 换成**视觉模型**的同一岗位（`AgentCatalog.GetOrCreateVision`）——工具 / 记忆注入 / 技能链 / 审批包装全部保留。
  反例（已修）：曾经换成“裸视觉体”（`Tools = null`），于是请求里根本没有 tools；DeepSeek 这类模型不会报错，而是**把工具调用当正文写出来**（DSML 标记），结果技能从未执行、也拿不到文件。
- 组织化路由：当触发为“提及”且（角色配了 `AssignmentIds`/`EscalationAgentId` 或启用了策划）时进入 `InvokeAssignmentEscalationAsync`：
  1. `CoordinatorPlanning` 且非组织-落库部署员（未挂 `org_deploy`）→ 先试 `BuildCoordinatedPlanAsync` 拿到结构化计划；
  2. 有计划 → `ExecuteCoordinatedPlanAsync` 逐项激活并把计划卡点亮；
  3. 无计划 → 回退递归路由（多候选指派/提升，深度上限 `MaxRouteDepth`、环路去重）；全部无解 → 明确复用“无法解决”引导；有解 → 由下游数字员工自答/代答，回复以原 @ 宿主身份发出。
- 递归综合 `ExecuteRecursiveAnswerAsync`：在计划收集结果后让模型综合，若信息不够就继续补调技能/下属（“体检式”一问到底），直到 `needsMore=false` 给最终答复；该处以 JSON 容错避免 `{needsMore,…}` 泄漏。
- 普通流式 run：无上述分支时，直接由模型带工具做一轮流式（`Run/rerun Stream`）、支持工具调用、审批中断与恢复。
- 组织角色（挂 `org_design`/`org_deploy`，如 org_architect）不属于策划批量路径，走“普通带工具 run”，让 `org_plan_draft`/`org_commit` 是真可被模型 function-call 的工具。

---

## 2.1) 小决策（该不该发言 / 派给谁）：模型与阈值

数字员工的模型调用分两类，**必须分开看待**：

| 类别 | 调用点 | 输出预算 | 走哪个模型 |
|---|---|---|---|
| 正式回复 | 普通流式 / 桥接 / 计划各步 / 递归综合 | 大（受 `StreamTimeoutMinutes` 等约束） | 受思考模式影响（思考开则走推理模型） |
| **小决策** | `ShouldSpeakAsync`（语境触发）、`RankAssignTargetsAsync`（指派路由） | **只有几个 token**（判定 8 / 路由 64） | **故意无视思考模式**，固定非推理模型 |

为什么必须解耦（实测教训）：推理模型会把这点预算**全花在思维链上，正文为空**——而这类失败**没有任何异常、没有错误日志**：

- 发言判定旧实现用 `StartsWith("YES")` 看正文 → 空正文恒为假 → **语境触发的数字员工永远不发言**；
- 指派路由旧实现把空输出当“模型回 NONE” → 解析出 0 个下游 → 退化成“只有问题提升、没有任务指派”。

实测（同一判定提示，生产实际使用的模型）：`deepseek-flash` 预算 8 → 正文空；预算 64 → 正文空（推理恰好吃满 64 被截断，这就是“有时空有时不空”的间歇性来源）；换 `deepseek-chat` 后预算 8 → 正文 `YES`，只花 1 个 token。

因此小决策现在：

1. **模型解析与思考模式解耦**（`AgentOptions.DecisionModel` → 智能体 `Model` → 全局 `Model`）；
2. **用概率而非文本前缀**：直调 OpenAI 兼容端点拿 `logprobs`，归一化成 P(是)，再与 `Agents:DecisionMinProbability`（默认 0.3）比；拿不到概率才退回文本（只认第一个词，认不出就**不猜**，返回 null 而不是默默当“否”）；
3. **空输出 ≠ NONE**：指派路由对空正文重试一次（更大预算）并记 `warn`，而不是静默当成“没人合适”；
4. 判定调用固定 `temperature=0`（判定不该采样），用量按同一口径记入库（判定提示很长，不记会低估配额消耗）。

**配置取值口径：留空/空白 = “未设置”。** 配置绑定会把**空字符串照绑**（Docker 里 `Agents__DecisionModel: ${AGENTS_DECISION_MODEL:-}` 在用户没配时就是空串），因此模型名解析一律按“空白即未设置”处理（`AgentCatalog.FirstNonBlank`），不能直接用 `??`。否则空串会被当成已设置的模型名一路传下去，`ChatClient` 构造直接抛 `ArgumentException: Value cannot be an empty string. (Parameter 'model')` —— 实测就是 1.0.154 上线后每次语境判定都失败（日志只有一句“语境判定调用失败”）。

---

## 3) 普通带工具 run 的内部循环（Local streaming + HITL）

```mermaid
sequenceDiagram
    autonumber
    participant M as 模型（deepseek 等）
    participant G as AgentGateway
    participant T as 工具/审批
    participant U as 用户（触发者）

    G->>M: user message（群历史按可见性注入 + 记忆）
    loop 直到结束
        M-->>G: 增量正文 / 推理文本 / 工具调用
        alt 需审批或客户端工具
            G-->>T: 触发生成交互卡
            T-->>U: 仅触发者可 批准/拒绝（含批量“本机一键执行全部”）
            U-->>G: 决策
            G->>M: 结果回灌 → 继续
        else 免审批工具（时间/换算/记忆检索等）
            G->>M: 执行并把结果注入
        end
    end
    G-->>U: 完成：正文/计划卡/思考/技能调用链回灌 + EndAgentMessage 写库
```

---

## 4) 组织化路由 → 计划 → 递归综合（分层细图 / Org routing → plan → recursive synthesize）

```mermaid
flowchart TD
    cell["触发语义=提及 且 (有 AssignmentIds / EscalationAgentId 或策划可用)"]
    cell --> cap{CoordinatorPlanning && 非组织落库员(未挂 org_deploy)?}
    cap -- 否 --> fallback["回退：递归指派 / 提升 (ResolveRoute)"]
    cap -- 是 --> build["BuildCoordinatedPlanAsync：列可达下属 + 可调技能 → 路由模型产出结构化 steps (最多 N 步)"]
    build --> hasPlan{拿到计划?}
    hasPlan -- 空/失败 --> fallback
    hasPlan -- 有计划 --> startStream["广播计划卡(点亮待执行)"]

    startStream --> step1{逐条 step.Action}
    step1 -- dispatch --> subRun["子数字员工一次性 run，结果级联 return(不递归下钻，防无限深)"]
    step1 -- 服务端技能 --> skRun["SkillRunner 执行(server 沙箱)；含 SkillAutoFixer 自修（至多 3 次）"]
    step1 -- 客户端技能 --> batch["合并成「本机一键执行全部 / 浏览器或桥」→ 回传结果逐个点亮"]
    subRun --> cascade(级联为下一 step 输入 working)
    skRun --> cascade
    batch --> cascade
    cascade --> more{还有 step?}
    more -- 是 --> step1
    more -- 否 --> nd{计划里跳过了文档生成类技能?<br/>needsDelivery}
    nd -- 否 --> rs["最终·递归综合 ExecuteRecursiveAnswerAsync"]
    nd -- 是 --> deliver["交付兑底 TrySatisfyDeliveryAsync：找挂了匹配文件技能的同事，完整流式让它自己调技能并回档产物"]
    deliver --> dhandled{交付接手了吗?}
    dhandled -- 已出文件 / 已挂审批卡 --> done
    dhandled -- 静默放弃 --> planNote["补发计划说明（如实告知跳过了哪些步及原因）"]
    planNote --> done

    rs --> enough{信息足够 needsMore=false?}
    enough -- 否 --> pick{还需补查 skill / dispatch?}
    pick -- 有 --> runMore["已执行集合去重 → 补查一轮"]
    enough -- 是 --> done["给最终答复（正文 + 计划卡 + 思考 + 技能链 → EndAgentMessage）"]
    fallback -- 解答/代答/无解:见回退提示 --> done
```

> 说明：递归补查有轮次上限（`MaxRecursiveRounds`）；“已执行技能/已带回结果的下属”会去重，避免同一技能被重复调用两次。客户端技能在本机执行也必须走批准/桥，服务端绝不当 bash 误跑此类 PowerShell 技能。
>
> 交付分支（1.0.137 修正）：文档生成技能的入参是结构化 JSON，必须由模型当工具调用构造，计划路径按纯文本直接调它只会塑出空壳文档，因此计划里跳过它并标记 `needsDelivery`，改由交付兑底完整流式产出。交付物类型优先取用户那句里的格式词，用户没提格式词时回退用**计划点名的文件技能**（否则“希望有一些插图”这类迭代请求会因判不出交付物而直接放弃）；计划内各步产出会作为正文素材（`upstreamDraft`）一并交给交付岗。计划侧那段“说明”在交付收尾时暂不发，只有交付**静默放弃**（没认出交付物 / 找不到能做的岗位）时才补发，避免空消息，也避免两条自相矛盾的说明叠在同一条消息里。

---

## 5) 驳回 / 超时 / 失败重试 分支（Reject · timeouts · retry branches）

```mermaid
flowchart TB
    stream["模型流式循环 attempt loop (MaxModelAttempts 重试)"]
    stream --> tr{流式抛异常?}
    tr -- 否 --> done2["正常结束 / 审批中断 → 结束本 run 收尾"]
    tr -- "可重试 429 / 5xx / 连接重置" --> backoff["ResetAgentContent 清半截 + 指数退避(1.5*attempt) 重试"]
    tr -- 其它异常 --> describe["脱敏 DescribeModelError 写群错误事件(不改库)"]
    backoff --> stream

    done2 --> hitl{"遇需审批/客户端工具?"}
    hitl -- 否 --> finish[完成广播：正文/计划/思考/链]
    hitl -- 是 --> card["下发人机交互卡（仅触发者可决策）"]
    card --> dec{谁、如何决策?}
    dec -- 非触发者决策 --> reject0["一律拒绝：仅触发者可决"]
    dec -- 触发者 批准 --> resume["恢复继续 ResumeRunAsync"]
    dec -- 触发者 拒绝 --> reject["拒绝该工具：告诉模型并给出结果，执行类不改库；视内容结束或继续"]
    dec -- 超时无决策(约 10 分钟 TTL) --> purge["周期清理 + 安全结束该消息(SafeEnd)"]

    card -- 客户端批量 --> batchT{"批量执行"}
    batchT -- 桥在线且免确认 --> tun["ExecuteForClientAsync 执行"]
    batchT -- 其他 --> cf{等待前端回传}
    cf -- 批准回传 --> tun
    cf -- 超时/取消/未回传 --> timedOut["视为未执行：以“本机未执行/已取消”文本注入→仍可做综合"]
    tun -- 桥超时/失败 --> tunfail["以‘未返回/超时’文案注入模型继续"]

    subgraph extraRetry["其它失败上的自重试"]
        orgCommit["org_commit 落库失败 → 按报错修 JSON 并用同一 teamKey 重试(至成功或明确原因)"]
        skillAuto["服务端技能自检/运行失败 → SkillAutoFixer 至多 3 次"]
        bridgeCC["桥接外部端点连续失败 → 熔断开短暂退避(AGENT_BRIDGE_BACKOFF)，不做无休止重连"]
    end
    finish --> extraRetry
```

> 关键常量/语义：模型流式 `StreamTimeoutMinutes=5` 挂起保护；人机交互 `InteractionTtlMs≈10 分钟`，超时由周期定时器清理；模型流可重试错误按 `MaxModelAttempts` 次指数退避；桥接失败走熔断退避；单次消息审批轮数有上限防死循环。驳回/超时都不会静默改库：执行类技能一律“已获批准才执行”，拒绝即不执行。
>
> 孤儿流兜底：一条流式消息**创建超过 10 分钟且最近 60s 无活跃**时被强制收尾（防状态泄漏）。因此审批卡放太久（>10 分钟）再点批准，恢复会报“消息不存在或未开启流式灌入”（该次回复拿不到了）；及时点按不受影响。

---

## 一句话理解

群消息被判定触达某数字员工 → 按“桥接 > 流水线 > 交接 > 语境沉默 > 组织化路由(计划→逐项→递归综合) > 普通流式(带工具+人机审批)”的顺序择一路径，最终把该角色的一次答复（正文 + 计划卡 + 思考 + 技能链）定向/全群地回灌本群。

In one sentence: once a message touches a digital employee, the runtime picks a single pipeline in priority order — bridge > pipeline > relay > contextual silence > org routing (plan → execute → recursive synthesize) > plain streaming (tools + human-in-the-loop) — and delivers that role’s reply (body, plan card, thinking, skill chain) back into the group.

---

## 附：代码锚点

- 触发判定：`AgentTriggerService`（提及/关键词/全量/语境）。
- 总入口与 ambient/桥退避：`AgentGateway.InvokeAsync`。
- 分派(s开关)：`InvokeCoreAsync`（桥 `InvokeBridgeAsync` / 流水线 `InvokePipelineAsync` / 交接 `InvokeRelayAsync` / 语言沉默 / 策划指派 `InvokeAssignmentEscalationAsync` / 普通流式）。
- 组织化路由/计划/递归综合/计划卡广播：`BuildCoordinatedPlanAsync` / `ExecuteCoordinatedPlanAsync` / `ExecuteRecursiveAnswerAsync` / `RecordStandinChain`。
- 交付兑底与交付物类型判定：`TrySatisfyDeliveryAsync` / `RunDeliveryStreamAsync` / `WantedDeliverable` / `DeliverableFromSkillId` / `BuildDeliveryPrompt` / `BuildNoOutputFallback`。
- 普通 run：`agent.RunStreamingAsync`、审批 (`HITL`) 恢复 `ResumeRunAsync`、暂停清理 `ResolveInteractionAsync`、批量客户端 `AwaitBatchClientExecAsync`。
- 小决策（§2.1）：`AgentCatalog.ResolveDecisionModelName` / `DecideYesNoAsync` / `ParseYesNo` / `ParseAssignTargets`、`AgentGateway.ShouldSpeakAsync` / `RankAssignTargetsAsync`。
