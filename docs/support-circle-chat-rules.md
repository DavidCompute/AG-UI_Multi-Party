# 客服知聚（Support Circle）的聊天规则
# Chat Rules for a Support Circle

> 本文依据当前代码实现整理（`.NET 10`），说明“客服知聚（`GroupKind.Support`，俗称知聚主/客服组）”下人与数字员工聊天、触发、审批、可见性的规则。
> 代码依据统一缩略为：`GroupHub`=`src/AguiGroupChat.Hub/Messaging/GroupHub.cs`、`AgentGateway`=`src/AguiGroupChat.Agents/AgentGateway.cs`、`Http`=`src/AguiGroupChat.Hub/Transport/HttpGroupApi.cs`、`Group`=`src/AguiGroupChat.Hub/Models/Group.cs`、`SupportCircleTests`=`tests/AguiGroupChat.Hub.Tests/SupportCircleTests.cs`。

This document captures the chat rules of a Support Circle (`GroupKind.Support`) as *actually implemented* in the current `.NET 10` code, covering membership, two participant classes, visibility/scope, agent triggering, directed replies, approvals and member-vs-customer read rules.

---

## 摘要一页看（Summary）

| 维度 | 规则 |
|---|---|
| 身份 | 客服 = 群成员且 `Role != Normal`（创建即被置为 Admin/客服）；顾客 = 非成员“参与者” |
| 客服看到 | 群内全部会话（staff sees all） |
| 顾客看到 | 仅自己与客服团队的会话；顾客之间互不可见 |
| 会话隔离机制 | 每条消息强制 `Private` + `VisibleMemberIds` 定向，不再分独立 topic |
| 顾客可否触发 | 可；因无成员行，主群组 RBAC `CanInvokeAgents` 不拦截顾客 |
| 审批 | 顾客触发客服技能需**顾客本人**确认；服务端仅放行触发者 |
| 客服回复 | 继承触发消息的可见性，定向写回该顾客，不再带 @ 回显 |
| 顾客生命周期 | 非持久成员；进入登记到 `_supportCustomers`，30 分钟无活动由 TTL 回收 |
| 解散 | 仅群主能解散 |

| Aspect | Rule |
|---|---|
| Roles | Staff = group member with `Role != Normal` (made Admin/customer-service at creation); Customer = non-member “participant” |
| What staff see | every conversation in the circle (staff sees all) |
| What a customer sees | only his/her own conversation with the team; customers never see each other |
| Isolation mechanism | every message forced `Private` + `VisibleMemberIds`; no per-customer subtopic |
| Can a customer trigger? | Yes — a customer has no member row, so the group’s `CanInvokeAgents` RBAC does not block him/her |
| Approvals | a customer must approve his/her own triggered agent skills; the server only lets the triggerer decide |
| Agent replies | inherit the triggering message’s visibility, go back only to that customer, and no longer echo the `@` |
| Customer lifecycle | non-persistent participant; registered in `_supportCustomers`, reclaimed after 30 min idle |
| Disband | owner only |

---

## 0. 底层：kind 与 IsSupportCircle

- **`Group.Kind` 判定**：存储以 `Extra["kind"] := "support"/"normal"` 表示组类型；`Kind = support ? GroupKind.Support : GroupKind.Normal`（`Group`）。
- **`bool IsSupportCircle => Kind == GroupKind.Support;`**（`Group.cs`）。
- 一句话：凡是 `IsSupportCircle == true` 的群都走下面这套“客服 vs 顾客”规则；普通群全员对等（`Role` 全为 `Normal`，无“客服/顾客”之分）。

### 0. Backing `kind`/`IsSupportCircle`

The group type is stored as `Extra["kind"] := "support"/"normal"`; `Kind = support ? GroupKind.Support : GroupKind.Normal`, and `IsSupportCircle => Kind == GroupKind.Support`. **Every rule below only applies when the group is a support circle; ordinary groups are fully symmetric members.**

---

## 1. 创建与成员（Creation & membership）

- 客服知聚由 `OwnerId` 创建，创建者角色为 `Owner`。
- 创建时一并拉入的团队（真人 + 数字员工，`MemberSeed.MemberType = User/Agent`）**服务端覆盖成 `Role=Admin`**：
  `if (req.Kind == GroupKind.Support && id != req.OwnerId) role = GroupRole.Admin;`（`GroupHub`）。
  ——即“客服成员 admin 化”，“客服=非 Normal”是从这里来的。
- 创建成客服知聚必需；客服知聚**强制非私密**：`IsPrivate = isSupport ? false : req.IsPrivate`。
- 普通用户并不被加进成员表；他们以“顾客参与者（非成员）”身份进入（见 §2、§8）。

> 运行时（非建群首批）后加入的成员仍一律 `Role=Normal`（`AddMembersCoreAsync`），故不被视为客服；客服 Admin 化只在“创建首批”发生。(见文末存疑②)

### 1. Creation & membership

The circle is created by `OwnerId` (role `Owner`); at creation the team pulled in (humans + digital employees, `MemberType=User/Agent`) are **overwritten server-side to `Role=Admin`** — hence “staff = non-Normal”. A support circle cannot be private (`IsPrivate=false`). Ordinary users are **not** added to the member table; they enter as “customer participants” (§2, §8).

> Members added later at runtime still default to `Role=Normal`, so they are **not** treated as staff; the Admin-isation only happens for the initial creation batch. (see note ② at the end)

---

## 2. 客服 vs 顾客 —— 两种身份的判定（Two identities)

- **客服（staff/客服组成员）** ＝ 该组 **成员列表里 `Role != Normal`** 的人：
  `SupportStaffIds(groupId) = ListMembers(...).Where(m => m.Role != GroupRole.Normal).Select(MemberId)`（`GroupHub`）。天然包含数字员工成员。
- **顾客（customer/参与者）** ＝ **非成员**、登记于 `GroupHub._supportCustomers`(key=groupId → customerId 集合) 的参与者。不是群成员、不出现在成员清单、不占名额、不持久。
- **判定函数**：`IsSupportCustomer`、`CanParticipate`、`Enter`（进入时若已是成员直接当作客服进入，否则写 `_supportCustomers`）。
- 顾客消息表：走 `SenderId == 顾客自身`；“会话”靠每条消息的 `Private`+`VisibleMemberIds` 维持，不按用户分队 topic（见文末存疑①）。

### 2. Two identities

- **Staff** = group members whose `Role != Normal` (`SupportStaffIds`); includes agent members.
- **Customer** = a **non-member “participant”**, tracked in `GroupHub._supportCustomers`; not on the member list, not persistent, does not consume a slot.
- A customer’s “own conversation” is achieved by `Private` + `VisibleMemberIds` on each message, **not** by a separate subtopic (see note ①).

---

## 3. 可见性与范围（Visibility & scoping)

**总原则：服务端在消息“读”与“写”两端都强制范围，顾客恒隔离自己，员工恒见全部。**

- **写端（发消息/客服答复）**由 `ApplySupportCircleScoping`（`GroupHub`）强制成定向：
  - 客服回复顾客 → `Private` + `VisibleMemberIds=[顾客自身]`
  - 客服之间相互回复 → `Private` + `VisibleMemberIds=[全体客服]`
  - 客服发的无目标通知 → `Private` + `[全体客服]`（不对顾客广播）
  - 顾客发的消息 → `Private` + `VisibleMemberIds=[发送者本人]`
- **读端**：历史/快照/分页/搜索都调 `CanSeeMessageAware`/`CanSeeMessageCore`：
  - 客服（staff）恒 `true` 全可见；
  - 普通群走全等可见（`All`）分支。
- **扇出**：`ResolveRecipients` 对每条消息追加“全体客服 + 命中会话的顾客”，保证不广播出副作用到无关顾客。
- **网关侧注入给数字员工的上下文**也做隔离（见 §7）。

### 3. Visibility & scoping

The server enforces scope at **both** write and read:

- **Write** (`ApplySupportCircleScoping`): staff→customer reply = `Private`+`[that customer]`; staff↔staff reply = `Private`+`[all staff]`; uncontrolled staff notice = `Private`+`[all staff]`; a customer’s message = `Private`+`[sender]`.
- **Read**: history/snapshot/paging/search all go through `CanSeeMessageAware`/`CanSeeMessageCore` — staff always sees everything; customers only see their own.
- **Fan-out**: `ResolveRecipients` adds all staff plus the matched customer, never leaking to unrelated customers.

---

## 4. 触发与 @ 提及（Agent triggering）

- 客服知聚不设全局“谁能触发”拦：`AgentTriggerService` 在消息上按提及/keyword/全员/语境判定是否触发。
- 若发送者是**成员**，先过 RBAC `CanInvokeAgents` 拦；**顾客没有成员行 → 该 RBAC 不拦 → 顾客可 @ 触发客服组的数字员工**。
- 显式提及/召唤 → 语义 `mentioned`；关闭(AI 分身)召唤需判断关键词；具名触发最终走 `InvokeAgentFor`。
- 群成员列表(成员视角)里可见的可触发数字员工 = 该组成员的 `Agent`。
- > 说明：客服知聚本身不在这里做“谁能触发/谁算有权”更高层授权——那些（例如让组织落库需管理员放行、普通用户只出稿不写库）属上层 org 角色/工具闸，不是客服知聚的“核心聊天规则”。

### 4. Agent triggering

A support circle sets no extra global “who may trigger” gate. The `AgentTriggerService` evaluates on the message (mention / keyword / all / context). Staff (members) additionally pass RBAC `CanInvokeAgents`; **customers have no member row, so that RBAC doesn’t apply — a customer can @-trigger the circle’s agents**. An explicit mention/summon yields `mentioned`; concrete invocation goes through `InvokeAgentFor`. Higher-level gates (e.g. org apply needing an admin) live in the corresponding tool/org-role, not in circle chat rules.

---

## 5. 数字员工（客服）如何定向答复顾客（Directed agent replies)

- 构建 `AgentInvocationContext` 时**继承触发消息的 `Visibility`+`VisibleMemberIds`**（`GroupHub.InvokeAgentFor`）。
- 各派生命令 `PublishAgentMessageStartAsync`（本地/编排/relay/指派升级/桥接）都带上 `Visibility=context.Visibility; VisibleMemberIds=context.VisibleMemberIds ?? []`。
- Hub 在 `PublishAgentMessageStartAsync` 用它建立**只含该批订阅者的流（`ResolveRecipients`）**；`Append/End/Reset/Attach` 只朝流接收集写出 → 客服在客服组的答复天然定向给触发它的那位顾客，绝不广播到其它顾客。
- 回复正文**不再回显 @**（提及仅用于触发）。

### 5. Directed agent replies

Building `AgentInvocationContext` carries the **triggering message’s `Visibility`+`VisibleMemberIds`**; all derived `PublishAgentMessageStart*` open a stream whose recipients = `ResolveRecipients(...)`, and every `Append/End/Reset/Attach` writes only to that set. Thus the agent’s reply reaches back exactly to the customer who triggered it and not to other customers. The reply body no longer echoes the `@`.

---

## 6. 审批 / 人机交互（Approvals — customer approves their skill)

- 顾客触发客服技能 → 数字员工请求执行 → 需顾客确认。
- **服务端决策权双层收口到“仅触发者”**：
  - Hub 层放行**顾客参与者（非成员）**作为决策者身份进入（`ResolveInteraction`/`Can*` 里 `isSupportCustomer` 分支；真实成员额外过 RBAC `CanApproveInteractions`，顾客无成员行故只由网关兜底）；
  - 网关按 `pending.TargetMemberId == 决策者` 强校验，非触发者一律拒绝（含批量客户端技能分支按 `batch.TargetMemberId`）。
- 审批通过后结果经事件/卡片回灌模型继续。

### 6. Approvals

When a customer triggers a skill the agent executes **only after that customer approves**. The server confines decision-making to the triggerer in two layers: the Hub admits a customer participant as a decision-maker (real member additionally must satisfy RBAC `CanApproveInteractions`); the gateway requires `pending.TargetMemberId == the decider` and rejects anyone else (the client-skill batch path checks `batch.TargetMemberId` too).

---

## 7. 客服看全部 vs 顾客只看自己（Read/unread/typing/subscribe）

- **存储层消息是“原样”**（`RecentMessages/Messages*/AllMessages` 不带 viewer）；可见性过滤在**消费端**：群快照/HTTP 历史/搜索都 `.Where(CanSeeMessageAware(m, viewerId))`；普通群走全等分支。
- **顾客**不在 `GroupsOf`（非成员）→ 不会出现在“我的群/成员群列表”与成员维度 unread 里。
- **typing**：
  - 客服在输入 → 广播给其他客服 + 全部已进入顾客（顾客之间互不可见）；
  - 顾客在输入 → 其他(成员)客服收到；**其它顾客不在广播目标**（隔离）。
- **订阅**：`TrySubscribeOne`用 `CanParticipate` 放行顾客参与者；顾客订阅后只拿到**自己会话**的快照。

### 7. Staff-sees-all vs customer-sees-only-own

Message history is stored literally; filtering happens at consumers (snapshot/HTTP/search use `CanSeeMessageAware(m, viewerId)`). Customers are **not members**, so they are absent from “my groups” and member-unread. Typing: a staff member’s keystrokes go to other staff + all entered customers; a customer’s keystrokes go to staff only (customers never see each other). Subscription uses `CanParticipate` and delivers a customer only his/her own snapshot.

---

## 8. 顾客的进入 / 离开 / 会话恢复（Customer lifecycle)

- **进入**：消息/订阅时按非成员判定写 `_supportCustomers`，进入即生成一趟“该顾客↔客服团队”的隔离会话（语义上的）。
- **离开 / 回收**：顾客非成员，走不了成员 `LeaveGroupAsync`；超过 **30 分钟**无活动由 `PurgeExpiredSupportCustomers` 通过 TTL 回收（`SupportCustomerTtlMs = 30*60*1000`）。
- **会话恢复**：再进入重新登记即可；不保留独立 topic——隔离靠消息 `Private`+定向（代码未为顾客建独立 topic，见存疑①）。
- 成员删除 / 群解散按既有规则（解散仅群主）；`_supportCustomers` 主要靠 TTL 收，解散不显式清。

### 8. Customer lifecycle

A customer is registered into `_supportCustomers` upon entering; idle > **30 minutes** is reclaimed by `PurgeExpiredSupportCustomers` (TTL). Customers do not leave via the member-`Leave` path (they have no member row); re-entering simply re-registers. No per-customer subtopic is allocated in code — isolation is through `Private` + directed visibility (see note ①). Disband is owner-only.

---

## 9. Support 与普通群的关键差异速查（Support circle vs ordinary group)

| 点 | 普通知聚 | 客服知聚 |
|---|---|---|
| 成员角色 | 全员 `Normal`（平等） | 创建者 Owner；首批团队 = Admin(客服)；后续加入者仍 `Normal`(不入客服) |
| 私密 | 可私密 | 强制 `IsPrivate=false` |
| 进入者 | 须是成员 | 可以是“顾客参与者”（非成员） |
| 消息可见 | `All` | 每条强制 `Private` + `VisibleMemberIds`；客服全可见 |
| 谁触发数字员工 | 成员（RBAC） | 客服成员 + 顾客(顾客无成员行→不受 RBAC) |
| 审批 | 成员批准的技能 | 触发者本人（顾客也能是触发者） |
| typing | 全体可见 | 客服↔客服、客服↔其顾客；顾客之间不可见 |
| 读上下文 | 网关注入 `All` | 网关注入“本顾客所属会话 + All”(`IsVisibleForAgentContext`) |
| 成员列表/“我的群” | 含全部成员 | 只含客服成员；顾客不在成员清单 |

| Aspect | Ordinary group | Support circle |
|---|---|---|
| Roles | all `Normal` (equal) | Owner + staff = Admin at creation; later joiners stay `Normal`(not staff) |
| Privacy | allowed | forced public (`IsPrivate=false`) |
| Who can be in | members only | plus “customer participant” (non-member) |
| Visibility | `All` | every message `Private`+`VisibleMemberIds`; staff sees all |
| Who triggers agents | members (RBAC) | staff + customers (customers bypass member RBAC) |
| Approvals | member approves | the triggerer (customer allowed as triggerer) |
| Typing | everyone sees | staff↔staff and staff↔own customer; customers never see each other |
| Agent context | gateway injects `All` | only the triggering customer’s thread + `All` (`IsVisibleForAgentContext`) |
| Member list / “my groups” | all members | staff only; customers not listed |

### Support vs ordinary — one-line

In an ordinary group everyone is an equal member; a support circle introduces **two classes** — a staff/admin cluster that sees everything, and non-member customers who each get a private one-to-one thread with the team, strictly isolated from other customers.

---

## 附：权威实现位置（Where implemented, code anchors)

- `GroupHub`: 建群 Admin 化/私密/进入/隔离/扇出/typing/审批顾客放行/disband —— `GroupHub.cs`（`SupportStaffIds`、`ApplySupportCircleScoping`、`CanSeeMessageAware/Core`、`ResolveRecipients`、`Enter/IsSupportCustomer/CanParticipate`、`PurgeExpiredSupportCustomers`、`PublishAgentMessageStartAsync`、`BroadcastTypingAsync`、`ResolveInteraction`）。
- `AgentGateway`: 触发回复可见性继承/答复只发向定向流；`IsVisibleForAgentContext` 与 support 历史注入 —— `AgentGateway.cs`。
- Models: `Group.cs`(kind/IsSupportCircle)、`Requests.cs`、`Enums.cs`(GroupKind);Transport `HttpGroupApi.cs`(discover/members/history/search);`Web/AttachmentApi.cs`(客服 vs 顾客下载校验)。
- Tests：`tests/.../SupportCircleTests.cs`、`HitlGatewayTests.cs`。

---

## 文末存疑 / 实现期注意（Caveats verified from code, worth knowing)

1. **“每顾客会话”并未落到独立 topic**：隔离靠逐条消息 `Private + VisibleMemberIds`，消息多落在默认 `main` 话题，不是为顾客建独立 topic。写文档 / 理解时，“独立会话”指**可见性隔离**而非数据集分区。
2. **运行时后加入的成员仍 `Role=Normal`，不被视作客服**：只有“创建首批”被 Admin 化；需要“后续加入自动成客服”需额外实现，当前代码没有。
3. **顾客登记表 `_supportCustomers` 回收靠 30 分钟 TTL**：未见断连/退出即立刻清理的分支；群解散也未显式清空（但不影响“顾客不可见别人会话”的边界）。
