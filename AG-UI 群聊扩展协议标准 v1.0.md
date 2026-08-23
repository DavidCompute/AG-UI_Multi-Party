# AG\-UI 群聊扩展协议标准 v1\.0

本标准基于原生 AG\-UI（Agent\-User Interface）协议扩展，在完全向后兼容的前提下，增加多用户群组、多智能体协同的实时群聊能力，保持原生事件体系、传输格式与命名风格一致。

## 1\. 概述

### 1\.1 扩展目标

- 支持多人 \+ 多智能体在同一群组内实时交互

- 兼容原生单聊全部事件与字段，旧客户端可无差别解析基础内容

- 覆盖群组管理、成员状态、消息交互、权限控制等完整群聊生命周期

- 用户账号 / 会话令牌管理（Hub 扩展）：注册用户即群成员，可直接入群参与群聊（详见 §5\.3）

- 适配 SSE 与 WebSocket 两种传输模式

### 1\.2 兼容性原则

- 所有群聊专属字段均为可选，原生 AG\-UI 客户端可直接忽略新增字段

- 原生事件类型全部保留，仅通过扩展字段承载群聊上下文

- 新增事件统一以 `GROUP_` 前缀命名，不与原生事件冲突

- 群聊场景下 `threadId` 与 `groupId` 一一对应，单聊会话逻辑可无缝复用

### 1\.3 适用场景

- 多人协作式 AI 会商、需求评审群

- 多智能体协同工作群（产品助手 \+ 代码助手 \+ 数据助手）

- 社群式 AI 客服、答疑群

- 多人审批、人机共创类 Agent 场景

## 2\. 核心数据模型

### 2\.1 群组模型（Group）

|字段名|类型|必填|说明|
|---|---|---|---|
|groupId|string|是|群组唯一标识，命名建议：`group_xxx`|
|groupName|string|是|群组显示名称|
|groupAvatar|string|否|群组头像 URL|
|ownerId|string|是|群主成员 ID|
|memberCount|number|是|当前成员总数|
|createTime|number|是|创建时间戳（毫秒级）|
|isPrivate|boolean|否|是否私密群（默认 false）。私密群的语义记忆**仅允许在群内被检索到**：智能体在其他群触发（scope=agent/all）时排除私密群内容，本群内触发不受影响|
|extra|object|否|业务自定义扩展字段|

### 2\.2 群成员模型（GroupMember）

|字段名|类型|必填|说明|
|---|---|---|---|
|memberId|string|是|成员唯一标识，用户为 `user_xxx`，智能体为 `agent_xxx`|
|memberType|enum|是|成员类型：`user` / `agent`|
|nickname|string|是|群内显示昵称|
|avatar|string|否|成员头像 URL|
|role|enum|是|群角色：`owner`（群主）/ `admin`（管理员）/ `normal`（普通成员）|
|onlineStatus|enum|否|在线状态：`online` / `offline` / `busy`|
|joinTime|number|是|入群时间戳|
|extra|object|否|业务扩展字段|

### 2\.3 群消息扩展模型

在原生消息字段基础上新增群聊属性，所有字段均为可选：

|字段名|类型|默认值|说明|
|---|---|---|---|
|groupId|string|\-|所属群组 ID|
|senderId|string|\-|发送者成员 ID|
|senderType|enum|\-|发送者类型：`user` / `agent`|
|senderNickname|string|\-|发送者群昵称，便于前端直接渲染|
|replyToMessageId|string|\-|引用回复的目标消息 ID|
|mentions|string\[\]|\[\]|@ 提及的成员 ID 列表|
|mentionAll|boolean|false|是否 @ 全体成员|
|visibility|enum|`all`|可见范围：`all` 全群可见 / `mentioned` 仅被提及者可见 / `private` 仅指定成员可见|
|visibleMemberIds|string\[\]|\[\]|定向可见成员列表，配合 `private` 使用|
|topicId|string|`main`|所属话题 ID（默认 `main` 为主话题；可因「以此消息新建话题」迁移，见 §4.8）|
|attachments|AttachmentInfo\[\]|\[\]|消息附件列表（模型见 §2.5，上传见 §5.6）|

### 2\.4 话题模型（GroupTopic）

群内独立讨论线。默认话题 `main`（主话题）始终存在，不落库；新话题由群成员创建，消息可归属 / 迁移到对应话题。

|字段名|类型|必填|说明|
|---|---|---|---|
|topicId|string|是|话题唯一标识，命名 `topic_xxx`|
|groupId|string|是|所属群组 ID|
|name|string|是|话题名称（≤30 字符）|
|creatorId|string|是|创建者成员 ID|
|createdAt|number|是|创建时间戳（毫秒级）|

### 2\.5 附件模型（AttachmentInfo）

消息可携带附件（图片 / 文档 / 二进制）。`text` 与 `document` 类附件（txt、md、源码等纯文本，以及 docx / xlsx / pptx / pdf 办公文档）由服务端提取全文注入智能体上下文（超长截断），`image` / `binary` 类仅携带元数据供模型感知。

|字段名|类型|必填|说明|
|---|---|---|---|
|attachmentId|string|是|附件唯一标识（att_xxx），与上传目录一一对应|
|name|string|是|原始文件名（已消毒，前端展示与下载用）|
|contentType|string|是|MIME 类型|
|size|number|是|字节大小|
|url|string|是|下载地址 `GET /ag-ui/files/{attachmentId}/{name}`|
|kind|enum|是|类别：`image` / `audio` / `text` / `document` / `binary`（audio 为语音消息，仅携元数据供前端播放）|

## 3\. 传输层规范

### 3\.1 传输方式

- **下行推送**：优先使用 WebSocket 全双工传输；兼容 SSE 单向下行，每个客户端独立建立连接接收群消息

- **上行请求**：HTTP POST 或 WebSocket 上行，格式与原生 AG\-UI 保持一致

- **编码格式**：统一 UTF\-8 JSON 序列化，事件前缀 `data: ` 规则与原生一致

- **连接保活**：WebSocket 以服务端 Ping 帧、SSE 以心跳注释行维持连接，间隔由服务端配置（`GroupChat:HeartbeatIntervalSeconds`，默认 15 秒）

### 3\.2 连接与订阅

1. 客户端建立连接时需携带身份凭证（令牌鉴权约定见 §5\.3 用户管理接口），服务端校验群成员权限

2. 单连接支持订阅多个群组，通过订阅事件管理接收范围

3. 非群成员无法接收对应群组的任何事件推送

4. 连接建立后服务端推送 `GROUP_CONNECTED` 握手事件（携带 connectionId）。SSE 场景可据此经 `POST /ag-ui/group/subscribe` / `POST /ag-ui/group/unsubscribe` 动态调整订阅范围；WebSocket 场景经 `GROUP_SUBSCRIBE` / `GROUP_UNSUBSCRIBE` 事件管理订阅

```json
{
  "type": "GROUP_CONNECTED",
  "connectionId": "conn_8f3a2b9c",
  "memberId": "user_1001",
  "transport": "websocket",
  "timestamp": 1750000010000
}
```

`transport` 取值：`websocket` / `sse`。

## 4\. 事件类型完整规范

### 4\.1 命名规则

- 群专属事件以 `GROUP_` 前缀开头

- 原生事件保留原名，仅新增可选群字段

- 事件名统一为大写下划线格式（与原生风格一致）

事件总览（方向：下行 = 服务端推送；上行 = 客户端请求）：

|事件|方向|触发方|章节|
|---|---|---|---|
|GROUP_CONNECTED|下行（握手）|连接建立时|§3.2|
|GROUP_CREATED / GROUP_UPDATED / GROUP_DISBANDED|下行|群组操作|§4.2|
|GROUP_MEMBER_JOINED / LEFT / UPDATED|下行|成员变更|§4.3|
|TEXT_MESSAGE_START / CONTENT / END|下行|消息收发 / 智能体流式应答|§4.4|
|GROUP_MESSAGE_RECALLED|下行|消息撤回|§4.4|
|GROUP_TYPING|下行|输入状态|§4.4|
|GROUP_MESSAGE_READ|下行|已读回执|§4.4|
|TOOL_CALL_START|下行|工具调用开始|§4.5|
|AGENT_INTERACTION_REQUEST / AGENT_INTERACTION_RESOLVE|下行 / 上行|人机交互（工具审批，仅触发者可决策）|§4.5|
|AGENT_INTERACTION_RESOLVED|下行|人机交互决策结果广播（全群同步卡片状态）|§4.5|
|GROUP_SUBSCRIBE / GROUP_UNSUBSCRIBE|上行|客户端订阅 / 退订|§4.6 / §5.8|
|GROUP_SUBSCRIBE_ACK|下行|订阅结果|§4.6|
|GROUP_TOPIC_CREATED|下行|话题创建|§4.8|
|GROUP_TOPIC_DELETED|下行|话题删除（其下聊天记录与记忆一并清除）|§4.8|
|GROUP_TOPIC_CLEARED|下行|话题聊天记录清空（话题保留，消息与记忆一并清除）|§4.8|
|GROUP_MESSAGE_TOPIC_MOVED|下行|消息迁移话题|§4.8|
|GROUP_STATE_SNAPSHOT|下行|入群 / 订阅成功|§4.7|
|RUN_ERROR|下行|运行错误|§7|
|GROUP_MESSAGE_SEND / GROUP_MESSAGE_RECALL|上行（WS）|发送 / 撤回消息|§5.8|

---

### 4\.2 群组生命周期事件

#### GROUP\_CREATED

群组创建成功时推送给所有初始成员。

```json
{
  "type": "GROUP_CREATED",
  "groupId": "group_001",
  "groupInfo": {
    "groupName": "产品需求评审群",
    "groupAvatar": "https://xxx/group.png",
    "ownerId": "user_1001",
    "memberCount": 3,
    "createTime": 1750000000000
  },
  "members": [
    {"memberId": "user_1001", "memberType": "user", "role": "owner", "nickname": "张三"},
    {"memberId": "agent_prd", "memberType": "agent", "role": "normal", "nickname": "需求助手"}
  ],
  "timestamp": 1750000000000
}
```

#### GROUP\_UPDATED

群组基础信息变更时全群广播。

```json
{
  "type": "GROUP_UPDATED",
  "groupId": "group_001",
  "updateFields": ["groupName", "groupAvatar"],
  "groupInfo": {
    "groupName": "产品需求定稿群",
    "groupAvatar": "https://xxx/new.png"
  },
  "operatorId": "user_1001",
  "timestamp": 1750000100000
}
```

#### GROUP\_DISBANDED

群组解散时全群推送，推送后服务端终止该群所有事件。

```json
{
  "type": "GROUP_DISBANDED",
  "groupId": "group_001",
  "operatorId": "user_1001",
  "timestamp": 1750000200000
}
```

---

### 4\.3 群成员事件

#### GROUP\_MEMBER\_JOINED

新成员入群时全群广播，支持批量加入。

```json
{
  "type": "GROUP_MEMBER_JOINED",
  "groupId": "group_001",
  "members": [
    {"memberId": "user_1002", "memberType": "user", "role": "normal", "nickname": "李四", "joinTime": 1750000300000}
  ],
  "operatorId": "user_1001",
  "timestamp": 1750000300000
}
```

#### GROUP\_MEMBER\_LEFT

成员主动退群或被移出时全群广播。

```json
{
  "type": "GROUP_MEMBER_LEFT",
  "groupId": "group_001",
  "memberIds": ["user_1002"],
  "leaveType": "kick",
  "operatorId": "user_1001",
  "timestamp": 1750000400000
}
```

`leaveType` 取值：`voluntary` 主动退群 / `kick` 被移出。

#### GROUP\_MEMBER\_UPDATED

成员角色、昵称、在线状态变更时推送。

```json
{
  "type": "GROUP_MEMBER_UPDATED",
  "groupId": "group_001",
  "memberId": "user_1002",
  "updateFields": ["role", "onlineStatus"],
  "memberInfo": {
    "role": "admin",
    "onlineStatus": "online"
  },
  "operatorId": "user_1001",
  "timestamp": 1750000500000
}
```

---

### 4\.4 群消息事件（原生事件扩展）

原生 `TEXT_MESSAGE_*` 系列事件全部保留，新增群聊可选字段，旧客户端可直接忽略。

#### TEXT\_MESSAGE\_START（群聊扩展）

```json
{
  "type": "TEXT_MESSAGE_START",
  "messageId": "msg_789",
  "role": "assistant",
  "threadId": "thread_group_001",
  "runId": "run_456",
  "groupId": "group_001",
  "senderId": "agent_prd",
  "senderType": "agent",
  "senderNickname": "需求助手",
  "replyToMessageId": "msg_700",
  "mentions": ["user_1001"],
  "mentionAll": false,
  "visibility": "all",
  "timestamp": 1750000600000
}
```

> 实际客户端可能收到的**额外可选字段**（旧客户端可忽略，且仅在相应场景出现）：`topicId`（消息所属话题，缺省 `main`）、`visibleMemberIds`（`visibility=private` 时的定向可见成员）；用户消息还会带 `attachments`（附件对象数组，§2.5）。

#### TEXT\_MESSAGE\_CONTENT

原生格式完全不变，通过 `messageId` 与 START 事件关联群信息。除此之外，增量事件实际还可能携带可选 `groupId`（便于无需关联 START 即可定位群，旧客户端可忽略）。

```json
{
  "type": "TEXT_MESSAGE_CONTENT",
  "messageId": "msg_789",
  "delta": "针对这个需求，我建议从三个方向拆解..."
}
```

#### TEXT\_MESSAGE\_REASONING（Hub 扩展，AG-UI 思考模式）

智能体思考过程增量，独立于正文流式回灌；前端渲染为可折叠的「思考过程」块（流式中展开实时可见，结束后默认收起），与正文分离展示。消息结束时 `TEXT_MESSAGE_END` 携带 `reasoning` 完整快照供回放。

```json
{
  "type": "TEXT_MESSAGE_REASONING",
  "messageId": "msg_789",
  "delta": "先拆解需求，再对比方案..."
}
```

> 与 CONTENT 一样，REASONING 增量也可能携带可选 `groupId`（旧客户端可忽略）。

#### TEXT\_MESSAGE\_END（群聊扩展）

```json
{
  "type": "TEXT_MESSAGE_END",
  "messageId": "msg_789",
  "groupId": "group_001",
  "reasoning": "先拆解需求，再对比方案...",
  "timestamp": 1750000610000
}
```

#### GROUP\_MESSAGE\_RECALLED

消息撤回事件，全群广播。

```json
{
  "type": "GROUP_MESSAGE_RECALLED",
  "groupId": "group_001",
  "messageId": "msg_789",
  "operatorId": "user_1001",
  "timestamp": 1750000700000
}
```

#### GROUP\_TYPING

成员正在输入状态提示，可用于渲染 "对方正在输入"。

```json
{
  "type": "GROUP_TYPING",
  "groupId": "group_001",
  "memberId": "agent_prd",
  "memberType": "agent",
  "isTyping": true,
  "timestamp": 1750000605000
}
```

#### GROUP\_MESSAGE\_READ

消息已读回执，可选实现。

```json
{
  "type": "GROUP_MESSAGE_READ",
  "groupId": "group_001",
  "memberId": "user_1002",
  "readMessageId": "msg_789",
  "timestamp": 1750000650000
}
```

---

### 4\.5 群内工具调用事件（原生扩展）

原生 `TOOL_CALL_*` 系列事件扩展群字段，支持控制工具调用结果的可见范围。

#### TOOL\_CALL\_START（群聊扩展）

```json
{
  "type": "TOOL_CALL_START",
  "toolCallId": "tool_001",
  "toolCallName": "search_prd_doc",
  "toolArguments": "{\"query\":\"发布公告\"}",  // 可选：工具参数（JSON 文本）；外部桥接参数分帧到达时由 TOOL_CALL_ARGS 补发
  "parentMessageId": "msg_789",
  "groupId": "group_001",
  "triggerUserId": "user_1001",
  "visibility": "mentioned",
  "visibleMemberIds": ["user_1001"],
  "timestamp": 1750000620000
}
```

#### TOOL\_CALL\_ARGS（群聊扩展，Hub 扩展）

工具调用参数**完整文本**（桥接场景 TOOL_CALL_ARGS 分帧累积完成后补发；本地工具在 TOOL_CALL_START 已带参数，不补发）：

```json
{
  "type": "TOOL_CALL_ARGS",
  "toolCallId": "tool_001",
  "parentMessageId": "msg_789",
  "groupId": "group_001",
  "args": "{\"query\":\"发布公告\"}",
  "timestamp": 1750000620000
}
```

#### TOOL\_CALL\_RESULT（群聊扩展，Hub 扩展）

工具执行结果回灌（本地工具 `FunctionResultContent` / 外部 AG-UI `TOOL_CALL_RESULT`），前端与调用行关联展示：

```json
{
  "type": "TOOL_CALL_RESULT",
  "toolCallId": "tool_001",
  "parentMessageId": "msg_789",
  "groupId": "group_001",
  "result": "公告已发布至全群",
  "timestamp": 1750000620000
}
```

#### AGENT\_INTERACTION\_REQUEST（人机交互，Hub 扩展）

智能体工具需要**人工审批**（服务端用 `ApprovalRequiredAIFunction` 包装，如 `Agents:RequireApprovalToolNames` 名单）时，
模型调用该工具的运行以**中断**结束（工具不执行），网关保存运行现场并全群广播本事件。
**交互对象仅限触发者（`targetMemberId`）**：其他成员可看到卡片但无权决策。

```json
{
  "type": "AGENT_INTERACTION_REQUEST",
  "groupId": "group_001",
  "messageId": "msg_789",
  "threadId": "thread_group_001",
  "runId": "run_123",
  "interruptId": "interrupt_abc",
  "toolCallId": "call_001",
  "toolName": "publish_announcement",
  "toolArguments": { "announcement": "放假通知" },
  "message": "智能体「公告助手」请求你确认：是否执行操作「publish_announcement」？",
  "targetMemberId": "user_1001",
  "timestamp": 1750000620000
}
```

**输入型扩展字段**（`kind=input/choice/multi_choice`，外部 question 工具）：

| 字段 | 说明 |
|---|---|
| `kind` | `input`（文本输入）/ `choice`（单选）/ `multi_choice`（多选）/ `approval`（工具审批，默认） |
| `inputField` | 响应字段名（缺省 `answer`），恢复时以其为键回传用户输入 |
| `responseSchema` | 完整 JSON Schema，前端据此渲染通用表单（单选 enum / 多选 array / 数字 / 多字段） |
| `options` | kind=`choice`/`multi_choice` 的可选项列表（来自 `responseSchema` 的 enum） |
| `questions` | 外部 question 工具的结构化问题列表（如 OpenCode `metadata.questions`），前端逐题渲染选项 |

`questions` 数组元素结构：`{ header?, question, options?: [{ label, description? }], multiple? }`。
前端逐题渲染（单选 radio / `multiple:true` 多选 checkbox / 无选项文本输入），答案按问题顺序回传。

**输入型恢复（AGENT_INTERACTION_RESOLVE → 桥接 resume）的答案格式约定**：不采用二维数组；恢复载荷为以 `inputField`（`responseSchema` 子段名，缺省 `answer`）为键的**单键 JSON 对象**——文本 / 单选为字符串，多选为该键下的 JSON 字符串数组；若前端按完整 `responseSchema` 提交，则以 `payload` 对象原样回传（多个字段各占一个键）：

```json
{ "answer": "行业研究报告" }          // 文本 / 单选
{ "answer": ["市场规模", "技术趋势"] } // 多选（单键 JSON 数组）
{ "topic": "行业研究", "slides": 16 } // 多字段：按 responseSchema 的完整 payload
```

#### AGENT\_INTERACTION\_RESOLVE（人机交互决策，Hub 扩展）

触发者对交互请求作出批准 / 拒绝：服务端校验决策者**必须是 `AGENT_INTERACTION_REQUEST` 的 `targetMemberId`**（触发者），
然后以「批准 / 拒绝」作为 User 消息回灌**同一 AgentSession** 恢复运行（批准 → 执行工具并继续回复；拒绝 → 跳过工具继续）。

WS 上行示例（决策者身份取连接身份；等效 HTTP `POST /ag-ui/group/interaction/resolve`，`memberId` 取令牌身份）：

```json
{
  "type": "AGENT_INTERACTION_RESOLVE",
  "groupId": "group_001",
  "interruptId": "interrupt_abc",
  "approved": true
}
```

- `approved`: true = 批准（执行工具）；false = 拒绝（跳过工具）。`kind=input/choice/multi_choice` 交互提交时恒为 true
- `input`: kind=input 交互的用户输入文本（工具审批类型为空）
- `payload`: kind=input 交互按 `responseSchema` 提交的完整 JSON（单选 / 多选 / 数字 / 多字段对象）
- `approveAll`: true = 对本次运行启用批量批准（后续同类审批自动放行，不再逐个打断），仅 `approved=true` 时生效

- 非触发者决策 / 请求已过期（10 分钟）→ 拒绝并返回错误（HTTP 400，`BAD_REQUEST`；WS 发 `RUN_ERROR`）
- 恢复后的智能体回复以新的 `TEXT_MESSAGE_START/CONTENT/END` 流式回灌（新 runId）
- 服务端也可在恢复流程中再次中断（工具链），重复广播 `AGENT_INTERACTION_REQUEST`（同一 `targetMemberId`）

#### AGENT\_INTERACTION\_RESOLVED（决策结果广播，Hub 扩展）

触发者决策生效后，服务端**全群广播**决策结果，其他成员的交互卡片同步更新为「已批准 / 已拒绝」（不再停留在等待状态）：

```json
{
  "type": "AGENT_INTERACTION_RESOLVED",
  "groupId": "group_001",
  "interruptId": "interrupt_abc",
  "memberId": "user_1001",
  "approved": true,
  "timestamp": 1750000720000
}
```

- 决策者本人由本地回显 + 本事件双重更新（幂等）
- 仅触发者决策成功时广播；决策不存在 / 非触发者不广播

**AG-UI 桥接角色的人机交互**：智能体配置 `bridgeEndpoint`（§5.7 / §6.3）走外部 AG-UI 服务时，三种传输形态同样支持审批中断：

- **standard + HTTP(S)**：自建 SSE 客户端解析外部服务的标准 AG-UI 事件流——`TEXT_MESSAGE_END` 只是消息结束，
  权威终止事件是 `RUN_FINISHED`（`outcome.type="interrupt"` 时产出中断，参数从 `TOOL_CALL_ARGS` 增量累积回填）；
  本 Hub 网关广播 `AGENT_INTERACTION_REQUEST` 并保存桥接连接，触发者决策后上行 `RunAgentInput` + `resume` 数组恢复
- **standard + WebSocket**：网关解析外部服务 `RUN_FINISHED` + `outcome.interrupts[0]`（`id` / `reason` / `message` / `toolCallId` / `metadata`），
  恢复时上行 `RunAgentInput` 结构并携带 `resume: [{interruptId, status:"resolved", payload:{approved, toolCall:{callId, name, arguments}}}]`
  （`AGUIToolApprovalResumePayload`：字段为 `approved` 且需回传被批准工具的 `toolCall`，`status:"cancelled"` 取消）
- **hub 方言**：识别外部 Hub 广播的 `AGENT_INTERACTION_REQUEST`（字段直通），恢复时向外部 Hub 发送
  `AGENT_INTERACTION_RESOLVE`（WS 上行或 HTTP `POST /ag-ui/group/interaction/resolve`，成员身份 = 桥接 agent）——支持 Hub 级联审批

无论哪种形态，交互对象都**仅限本 Hub 内触发者**（`targetMemberId`），外部服务无权决定谁可交互。

---

### 4\.6 群订阅事件

用于单连接订阅多群组的场景。

#### GROUP\_SUBSCRIBE（客户端上行）

客户端请求订阅指定群组。

```json
{
  "type": "GROUP_SUBSCRIBE",
  "groupIds": ["group_001", "group_002"],
  "timestamp": 1750000800000
}
```

#### GROUP\_SUBSCRIBE\_ACK（服务端下行）

服务端返回订阅结果。

```json
{
  "type": "GROUP_SUBSCRIBE_ACK",
  "successGroupIds": ["group_001"],
  "failedGroupIds": ["group_002"],
  "failReason": "无群组访问权限",
  "timestamp": 1750000801000
}
```

#### GROUP\_UNSUBSCRIBE

取消订阅指定群组。

```json
{
  "type": "GROUP_UNSUBSCRIBE",
  "groupIds": ["group_001"],
  "timestamp": 1750000900000
}
```

---

### 4\.7 群状态同步事件

#### GROUP\_STATE\_SNAPSHOT

成员入群 / 订阅成功后，服务端推送群组完整状态快照。

```json
{
  "type": "GROUP_STATE_SNAPSHOT",
  "groupId": "group_001",
  "groupInfo": {
    "groupName": "产品需求评审群",
    "ownerId": "user_1001",
    "memberCount": 4
  },
  "members": [
    {"memberId": "user_1001", "memberType": "user", "role": "owner", "nickname": "张三", "onlineStatus": "online"},
    {"memberId": "agent_prd", "memberType": "agent", "role": "normal", "nickname": "需求助手", "onlineStatus": "online", "triggerMode": "mentioned", "keywords": [], "isTriggerOverridden": false}
  ],
  "topics": [
    {"topicId": "topic_001", "groupId": "group_001", "name": "V2 需求评审", "creatorId": "user_1001", "createdAt": 1750000900000}
  ],
  "latestMessages": [
    {"messageId": "msg_700", "senderId": "user_1001", "content": "大家看下本周新需求", "timestamp": 1750000050000}
  ],
  "timestamp": 1750001000000
}
```

> `latestMessages` 元素实际还可能携带**可选字段**（旧客户端可忽略）：`senderNickname`、`replyToMessageId`、`attachments`、`reasoning`、`mentions`、`mentionAll`、`topicId`。

成员为智能体时附带回显其群内生效触发规则：`triggerMode` / `keywords` / `isTriggerOverridden`（见 §6.2）；`topics` 为群内话题列表（不含默认 `main`，见 §2.4）。快照亦可经 `GET /ag-ui/group/{groupId}` 主动拉取（见 §5.4）。

---

### 4\.8 群话题事件（Hub 扩展）

#### GROUP\_TOPIC\_CREATED

群成员新建话题成功时全群广播；若创建时指定了起点消息，会伴随 `GROUP_MESSAGE_TOPIC_MOVED`（消息迁移到新话题，见下）。

```json
{
  "type": "GROUP_TOPIC_CREATED",
  "groupId": "group_001",
  "topic": {
    "topicId": "topic_001",
    "groupId": "group_001",
    "name": "V2 需求评审",
    "creatorId": "user_1001",
    "createdAt": 1750000900000
  },
  "timestamp": 1750000900000
}
```

#### GROUP\_MESSAGE\_TOPIC\_MOVED

消息被迁移到新话题（「以此消息新建话题」）时全群广播。

```json
{
  "type": "GROUP_MESSAGE_TOPIC_MOVED",
  "groupId": "group_001",
  "messageId": "msg_700",
  "topicId": "topic_001",
  "operatorId": "user_1001",
  "timestamp": 1750000901000
}
```

#### GROUP_TOPIC_DELETED

话题被删除（仅群主 / 管理员或话题创建者可删）时全群广播；**话题下聊天记录与对应语义记忆一并删除**（不可恢复）。

```json
{
  "type": "GROUP_TOPIC_DELETED",
  "groupId": "group_001",
  "topicId": "topic_001",
  "operatorId": "user_1001",
  "timestamp": 1750000902000
}
```

#### GROUP_TOPIC_CLEARED

**清空话题聊天记录**（仅群主 / 管理员）时全群广播：话题本身保留，该话题（含主话题 `main`）下消息与对应语义记忆一并物理删除（不可恢复）。

```json
{
  "type": "GROUP_TOPIC_CLEARED",
  "groupId": "group_001",
  "topicId": "main",
  "operatorId": "user_1001",
  "removedCount": 42,
  "timestamp": 1750000902000
}
```

## 5\. 客户端上行请求规范

### 5\.1 发送群文本消息

- 端点：`POST /ag-ui/group/message/send`；WS 上行等效事件见 §5.8

请求体字段：

|字段名|类型|必填|说明|
|---|---|---|---|
|groupId|string|是|目标群组 ID|
|threadId|string|否|缺省取 `thread_ + groupId`（群聊下与 groupId 一一对应）|
|userId|string|是|发送者成员 ID（WS 上行时以服务端解析的连接身份为准）|
|content|string|是|消息文本（可与附件同时为空时仅发附件）|
|topicId|string|否|所属话题，缺省 `main`（须为已存在话题）|
|replyToMessageId|string|否|引用回复的目标消息 ID（目标不存在或已撤回则拒绝）|
|mentions|string\[\]|否|@ 提及的成员 ID 列表|
|mentionAll|boolean|否|是否 @ 全体|
|visibility|enum|否|`all` / `mentioned` / `private`，缺省 `all`|
|visibleMemberIds|string\[\]|否|定向可见成员列表，配合 `private`|
|attachments|AttachmentInfo\[\]|否|附件对象数组（先经 §5.6 上传，把返回的附件元信息原样携带）|

请求体示例：

```json
{
  "groupId": "group_001",
  "threadId": "thread_group_001",
  "userId": "user_1001",
  "content": "@需求助手 帮我生成 V2 版本需求大纲",
  "mentions": ["agent_prd"],
  "replyToMessageId": "msg_700",
  "topicId": "main",
  "visibility": "all",
  "attachments": [
    { "attachmentId": "att_001", "name": "需求文档.docx", "contentType": "application/vnd.openxmlformats-officedocument.wordprocessingml.document", "size": 20480, "url": "/ag-ui/files/att_001/%E9%9C%80%E6%B1%82%E6%96%87%E6%A1%A3.docx", "kind": "document" }
  ]
}
```

发送成功后以 `TEXT_MESSAGE_START` / `TEXT_MESSAGE_CONTENT` / `TEXT_MESSAGE_END` 三元组回显与广播（发送者恒收到，见 §4.4），并按 §6 触发规则唤醒群内智能体。

### 5\.2 群组管理接口

|接口|路径|核心参数|
|---|---|---|
|创建群组|`POST /ag-ui/group/create`|groupName、ownerId、isPrivate*、memberIds、members\[\]（可携带昵称 / 类型 / 头像）|
|更新群信息|`POST /ag-ui/group/update`|groupId、updateFields、groupInfo、operatorId|
|解散群组|`POST /ag-ui/group/disband`|groupId、operatorId|
|添加成员|`POST /ag-ui/group/member/add`|groupId、memberIds、operatorId、memberDetails\[\]|
|移除成员|`POST /ag-ui/group/member/remove`|groupId、memberIds、operatorId|
|成员退群|`POST /ag-ui/group/member/leave`|groupId、memberId|
|更新成员|`POST /ag-ui/group/member/update`|groupId、memberId、updateFields、memberInfo、operatorId|
|撤回消息|`POST /ag-ui/group/message/recall`|groupId、messageId、operatorId|
|人机交互决策|`POST /ag-ui/group/interaction/resolve`|groupId、interruptId、approved（仅触发者可决策，其他成员 400）|
|输入状态|`POST /ag-ui/group/message/typing`|groupId、memberId、isTyping|
|已读回执|`POST /ag-ui/group/message/read`|groupId、memberId、readMessageId|

`updateFields` / `memberInfo` 采用「字段白名单 + 键值对」更新模式（同 GROUP_UPDATED / GROUP_MEMBER_UPDATED 事件），支持字段：群信息 `groupName` / `groupAvatar` / `isPrivate`；成员 `role` / `nickname` / `avatar` / `onlineStatus`。

权限矩阵（role：owner 群主 / admin 管理员 / normal 普通成员）：

|操作|群主|管理员|普通成员|
|---|---|---|---|
|解散群组|✅|—|—|
|更新群信息|✅|✅|—|
|添加 / 移除普通成员|✅|✅|—|
|移除管理员|✅|—|—|
|修改他人角色（群主角色不可改）|✅|✅|—|
|修改本人昵称 / 头像|✅|✅|✅|
|修改他人昵称 / 头像|✅|✅|—|
|发送消息 / 创建话题|✅|✅|✅|
|删除话题（创建者 / 群主 / 管理员）|✅|✅|仅创建者|
|撤回消息|✅|✅|仅本人发送的|
|退群|✅|✅|✅（群主不可退群，须解散群组）|

**写操作鉴权**：群组 / 成员 / 消息 / 话题等全部**写接口**统一做身份解析（与 WS / SSE 一致）——
携带有效会话令牌（`Authorization: Bearer <token>` 或 `?token=`）时以**令牌身份为准**，覆盖请求体中的 `ownerId` / `operatorId` / `userId` / `memberId`（登录用户无法伪造他人身份）；
未携带令牌时回退到请求体身份（兼容旧客户端 / 演示模式），除非服务端配置 `Auth:RequireTokenOnRealTime=true`（一律 401）。
GET **读接口鉴权**（快照 / 成员 / 历史分页，§5.4）要求登录，且校验**调用者是该群成员**（未登录 401、非成员 403，群不存在 404）；`GET /ag-ui/member/{memberId}/groups` 仅本人可查（越权 403）。

### 5\.3 用户管理接口（Hub 扩展）

账号体系为 Hub 扩展（不在原生协议内）：注册用户的 `userId`（`user_xxx`）直接复用为群成员 `memberId`，可被添加进群组参与群聊。除注册 / 登录外，其余用户接口均需携带会话令牌（`Authorization: Bearer <token>` 或 `?token=` 查询参数）；用户目录 `GET /ag-ui/users` 同样需登录。

#### 用户账号模型（UserAccount）

|字段名|类型|必填|说明|
|---|---|---|---|
|userId|string|是|用户唯一标识，命名 `user_xxx`，即群成员体系中的 memberId|
|username|string|是|登录名（全局唯一、大小写不敏感、不可变，≥3 字符）|
|nickname|string|否|显示昵称（建群时作为群内默认昵称，空则取用户名）|
|avatar|string|否|头像 URL（可为 `/ag-ui/files/...` 上传地址）|
|personalMemoryEnabled|boolean|否|个人记忆开关（默认 false）：开启后该用户的历史发言作为「个人记忆」参与检索注入（智能体开启个人记忆时才生效，见 §2.1 语义记忆 / §5.7）|
|createdAt|number|是|注册时间戳（毫秒级）|
|updatedAt|number|否|最近资料 / 密码变更时间戳（毫秒级）|

密码以 PBKDF2 加盐哈希存储（≥6 位），服务端不保存明文。

#### 接口一览

|接口|路径|鉴权|说明|
|---|---|---|---|
|注册|`POST /ag-ui/user/register`|否|注册即登录，直接签发令牌|
|登录|`POST /ag-ui/user/login`|否|用户名 + 密码登录，签发令牌|
|登出|`POST /ag-ui/user/logout`|令牌|吊销当前令牌|
|当前用户|`GET /ag-ui/user/me`|令牌|返回当前账号资料|
|修改密码|`POST /ag-ui/user/password`|令牌|校验旧密码后更新，并吊销该用户全部会话（需重新登录）|
|更新资料|`PUT /ag-ui/user/profile`|令牌|修改昵称 / 头像 / `personalMemoryEnabled`（个人记忆开关，默认关），并同步到其所在所有群的成员显示（广播 GROUP_MEMBER_UPDATED）|
|用户目录|`GET /ag-ui/users`|令牌|全部注册用户（前端建群成员选择器，需登录可见；DTO 不含 `isAdmin` 防泄露管理员身份）|

#### 请求 / 响应示例

注册（注册即登录，响应含令牌）：

```json
POST /ag-ui/user/register
{
  "username": "zhangsan",
  "password": "123456",
  "nickname": "张三",
  "avatar": null
}
```

```json
{
  "userId": "user_1001",
  "username": "zhangsan",
  "nickname": "张三",
  "avatar": null,
  "token": "MDEyM2FiY2RlZjQ1Njc4OWFiY2RlZjQ1Njc4OWFiY2Rl",
  "expiresAt": 1750086400000
}
```

登录：

```json
POST /ag-ui/user/login
{ "username": "zhangsan", "password": "123456" }
```

修改密码 / 更新资料（需 `Authorization: Bearer <token>`）：

```json
POST /ag-ui/user/password
{ "oldPassword": "123456", "newPassword": "newpass123" }
```

```json
PUT /ag-ui/user/profile
{ "nickname": "张总", "avatar": "/ag-ui/files/att_xxx/avatar.png", "personalMemoryEnabled": true }
```

#### 会话令牌

- 令牌为 32 字节随机数的 Base64URL（无填充），有效期 `Auth:SessionTtlHours`（默认 168 小时），每次校验成功自动滑动续期
- 传输方式：HTTP 请求头 `Authorization: Bearer <token>`；浏览器 WebSocket 无法自定义请求头，故同时支持 `?token=` 查询参数
- 实时通道（WS / SSE）：携带有效令牌时以令牌身份为准（忽略 memberId 参数）；未携带时回退到 memberId 直连（兼容旧客户端），除非 `Auth:RequireTokenOnRealTime=true`（缺令牌一律 401）
- 会话为服务端进程内状态：服务端重启后需重新登录（PostgreSQL 模式亦不持久化会话）

#### 与群体系的关系

- 注册用户可直接作为成员加入群组：建群 / 加成员时 `memberIds` 传其 `userId`
- 账号昵称 / 头像变更后，服务端自动把其所在各群的成员显示名与头像同步为新值，并广播 `GROUP_MEMBER_UPDATED`（前端据此即时刷新）

#### AI 分身（Twin，Hub 扩展）

用户可自行启用「分身」：服务端聚合该用户在**所有公开群**的发言记录（上限 120 条 / 8000 字符），调用模型生成人设（Instructions），
以**私密智能体** `twin_{userId}`（归属当前用户，仅创建者可管理）自动加入该用户所在全部公开群，按用户设定的触发方式代班回复。
停用即删除分身（目录 + 退出全部群）。私密群内容不参与人设生成，分身也不进入私密群。

|接口|路径|鉴权|说明|
|---|---|---|---|
|分身状态|`GET /ag-ui/twin`|令牌|当前用户分身状态（未启用返回 `{enabled:false}`）|
|启用分身|`POST /ag-ui/twin/enable`|令牌|body 含 `triggerMode`（默认 mentioned）；生成人设并加入全部公开群|
|修改触发|`POST /ag-ui/twin/trigger`|令牌|body 含 `triggerMode`；更新人设触发方式并同步全部公开群注册|
|同步到公开群|`POST /ag-ui/twin/sync`|令牌|补齐启用后新建 / 加入的公开群（幂等，不重复注册）|
|停用分身|`POST /ag-ui/twin/disable`|令牌|删除分身智能体并退出全部群|

**在线 / 离线互斥**：用户**在线**（存在活跃 WS/SSE 连接）时分身自动暂停不响应，成员列表显示用户本人；
用户**离线**时分身代为响应，成员列表显示分身（🪞 图标）。群内触发评估按连接数判定。

### 5\.4 群查询与历史分页接口

|接口|路径|说明|
|---|---|---|
|我的群列表|`GET /ag-ui/member/{memberId}/groups`|成员加入的全部群（含 myRole / myNickname）|
|群状态快照|`GET /ag-ui/group/{groupId}`|同 GROUP_STATE_SNAPSHOT（§4.7），含成员 / 话题 / 最近消息|
|成员列表|`GET /ag-ui/group/{groupId}/members`|全部成员（智能体成员含触发字段）|
|消息历史分页|`GET /ag-ui/group/{groupId}/messages?before=&count=`|按游标向前分页（虚拟滚动「加载更早消息」）|

历史分页游标语义：`before` 为游标消息 ID（不含该条），`count` 默认 50、上限 100；返回按时间序（旧 → 新）的 `SnapshotMessage` 列表（过滤已撤回）；`before` 缺省返回最近 count 条；游标为首条或不存在时返回空数组。同一群内按 `(timestamp, messageId)` 字典序排序。

### 5\.5 话题接口

|接口|路径|说明|
|---|---|---|
|新建话题|`POST /ag-ui/group/topic/create`|groupId、name（≤30 字符）、operatorId；`sourceMessageId` 非空时以该消息为新话题起点（消息迁移到新话题）|
|删除话题|`POST /ag-ui/group/topic/delete`|groupId、topicId、operatorId；仅群主 / 管理员或话题创建者；**话题下聊天记录与对应记忆一并删除**，全群广播 `GROUP_TOPIC_DELETED`；主话题 `main` 不可删除|
|清空话题记录|`POST /ag-ui/group/topic/clear`|groupId、topicId（可为 `main`）、operatorId；仅群主 / 管理员；**话题保留**，该话题下消息与对应语义记忆一并物理删除，全群广播 `GROUP_TOPIC_CLEARED`，返回 `{cleared, topicId, removedCount}`|
|话题列表|`GET /ag-ui/group/{groupId}/topics`|全部话题（不含默认 `main`）|
|关联话题|`GET /ag-ui/group/{groupId}/topics/related?topicId=`|**跨话题主题关联（5.1）**：按话题消息分词共享关键词（Jaccard）计算关联分，返回与该话题内容最相关的其它话题 `{topicId, name, score}`（阈值 >0.02、Top6），便于「此主题还在哪讨论过」；需群成员，非成员 403|
|话题消息分页|`GET /ag-ui/group/{groupId}/topics/{topicId}/messages?before=&count=`|话题内按游标向前分页，语义同 §5.4；`topicId=main` 表示主话题|

新建话题成功返回话题对象，并全群广播 `GROUP_TOPIC_CREATED`；指定起点消息时同步广播 `GROUP_MESSAGE_TOPIC_MOVED`（消息归属迁移，起点消息不能是已撤回消息）。

### 5\.6 附件上传与下载

|接口|路径|说明|
|---|---|---|
|上传附件|`POST /ag-ui/upload`|multipart/form-data，字段名 `file`；单请求最多 9 个、单文件 ≤20 MB。需身份（登录令牌，或演示模式 `?memberId=`，与 WS/SSE 鉴权一致）。图片 / 音频 / 文本 / 办公文档自动归类 `kind`|
|下载 / 预览|`GET /ag-ui/files/{attachmentId}/{fileName}`|按附件 ID 定位，文件名仅用于展示（下载保留原名）|

上传返回示例（响应体为 `{ "attachments": [...] }`）：

```json
{
  "attachments": [
    {
      "attachmentId": "att_001",
      "name": "需求文档.docx",
      "contentType": "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
      "size": 24576,
      "url": "/ag-ui/files/att_001/%E9%9C%80%E6%B1%82%E6%96%87%E6%A1%A3.docx",
      "kind": "document"
    }
  ]
}
```

`kind` 判定（服务端按 Content-Type 与扩展名归类）：`image`（图片类）/ `text`（txt、md、json、源码等纯文本）/ `document`（docx / xlsx / pptx / pdf 办公文档）/ `binary`（其它）。`text` 与 `document` 类均可提取文本注入智能体上下文（§2.5）。发送消息时把返回的附件对象数组放入 `attachments` 字段（§5.1）。

### 5\.7 智能体管理接口（Hub 扩展）

智能体目录（AgentDefinition）提供运行时管理：默认以服务端配置为种子，可动态新增 / 编辑 / 删除；定义含人设、触发方式、模型与 AG-UI 桥接配置（§6）。除目录查询外均需登录令牌。

|接口|路径|说明|
|---|---|---|
|目录列表|`GET /ag-ui/agents`|智能体定义（公开只读）；**私密智能体仅创建者可见**（匿名 / 他人不可见）|
|新增智能体|`POST /ag-ui/agents`|body 见下；agentId 留空自动生成 `agent_xxx`；记录创建者 ownerId|
|编辑智能体|`PUT /ag-ui/agents/{agentId}`|更新定义；同步未覆盖群的触发规则与群成员资料；**私密智能体仅创建者可编辑**（否则 403）|
|删除智能体|`DELETE /ag-ui/agents/{agentId}`|移除定义、全部触发规则并从所有群退出；**私密智能体仅创建者可删除**（否则 403）|
|群内注册触发|`POST /ag-ui/agents/register`|为指定群注册触发规则（triggerMode / keywords / override）|

智能体定义字段：

|字段名|类型|说明|
|---|---|---|
|agentId|string|唯一标识（`agent_xxx`）|
|nickname|string|显示昵称（同步到群成员）|
|description / instructions|string|一句话简介 / 系统提示词（定义人设与回复风格）|
|avatar|string?|头像 URL|
|triggerMode|enum|默认触发方式：`mentioned` / `allMessages` / `keyword` / `contextual`|
|keywords|string\[\]|triggerMode=keyword 时的关键词|
|model|string?|单独指定模型（缺省用服务端默认）|
|schedule|string?|定时汇报（5 段 cron，可选）：与消息触发独立，定时触发与消息触发可同时生效|
|enableWorkTools|boolean|是否启用工作型智能体的文件 / 命令工具（`list_dir` / `read_file` / `write_file` / `shell`），默认 false；命令与写操作限制在 `data/workspaces/<agentId>/` 工作区内，写操作用例子需经审批，且需全局 `Agents:WorkToolsEnabled` 开启|
|bridgeEndpoint|string?|AG-UI 桥接端点 `http(s)://` 或 `ws(s)://`（见 §6.3 外部专家）|
|bridgeMode / bridgeToken|string?|桥接方言（standard / hub）与认证令牌（编辑时令牌留空表示沿用原值，不回显）|
|personalMemoryEnabled|boolean|是否开启个人记忆（默认 false）：开启后回复时检索触发者本人历史发言注入（还需触发者用户开启）|
|skills|object\[\]?|技能（智能体间调用）：`[{skillId, description, targetAgentId}]`；skillId 给模型作工具名（仅字母/数字/下划线/连字符，**留空自动生成 `skill_<目标ID>`，冲突追加 `_2/_3`**），targetAgentId 为已注册智能体（含 AG-UI 桥接角色）；目标智能体单层展开、不能指向自身|
|knowledgeBaseIds|string\[\]|绑定的知识库 ID 列表（见 §5.8）：回复前按这些知识库检索相关文档片段注入上下文（RAG 知识库）|
|relayToAgentId|string?|**角色交接（1.2）**：非空时该智能体整轮委托给中继智能体（`agent_xxx`），外部调用者视角仍是原角色；中继不存在 / 形成接力环时回退本地处理|
|requireApprovalToolNames|string\[\]?|**智能体级审批策略（4.1）**：非空则用本名单决定哪些工具需审批，否则回退全局 `Agents:RequireApprovalToolNames`；`approveAll` 可一次性批准当前 run 后续全部待审批工具|
|isPrivate|boolean|是否私密智能体（默认 false）：仅创建者（ownerId）可拉入群 / 编辑 / 删除，目录对其他用户隐藏|
|ownerId|string?|创建者 userId（appsettings 种子为 null = 系统级）|

**导出 / 导入**：智能体管理的「📤 导出全部」/ 每行「导出」把配置导出为 JSON（`{version: 1, agents:[…]}`，字段同上，敏感令牌与 ownerId 不导出），前端「📥 导入」读取 JSON 后逐条调用 `POST /ag-ui/agents` 创建（归属当前登录用户，agentId 冲突自动改 ID 不覆盖）。另提供**全量数据包**接口（管理员）：`GET /ag-ui/export` 导出账号（含密码哈希 / 盐）+ 智能体定义与触发规则 + 群 / 话题 / 消息 + 附件为 zip；`POST /ag-ui/import/preview` 上传 zip 返回账号 / 智能体存在性检查与群清单，`POST /ag-ui/import` 按 `selectedGroupIds` 执行（账号按 username、智能体按 agentId 自动补齐，消息发送者 / 提及 / 可见列表按账号映射重写，附件还原）。

### 5.8 知识库管理接口（Hub 扩展，RAG 知识文档）

智能体可绑定知识库（`AgentDefinition.knowledgeBaseIds`），回复前按绑定列表检索知识文档相关片段注入上下文。
知识库由用户创建并上传文档（txt/md/json/csv 与 docx/xlsx/pptx/pdf，复用附件文本提取）：文档切片（800 字符/片 + 100 重叠）后向量化存入语义记忆向量表（GroupId 约定 `kb:{KbId}`）。依赖向量存储与 embedding（pgvector / sqlite-vec + llama / http），不可用时文档入库返回 400 明确错误。

**文档入库为异步处理**：`POST /ag-ui/kb/{kbId}/documents` 立即返回 `status=processing` 的文档记录（提取文本 / 切片 / 向量化在后台执行，避免上传请求长时间阻塞）；前端轮询知识库列表观察状态变化——`processing`（处理中）→ `ready`（已入库，chunkCount>0）或 `error`（失败，error 字段为原因）。处理中文档可随时移除（后台丢弃未写入的向量）；服务重启导致处理中断的文档恢复为 `error`。

|接口|路径|说明|
|---|---|---|
|创建知识库|`POST /ag-ui/kb`|body：`{name, description?}`；需登录；返回 kbId（`kb_xxx`）|
|知识库列表|`GET /ag-ui/kb`|系统级（ownerId=null）+ 当前用户创建的；文档清单含 `{docId, fileName, chunkCount, status, error, addedAtMs}`|
|删除知识库|`DELETE /ag-ui/kb/{kbId}`|仅创建者（403 越权）；连同全部文档向量一并清除|
|添加文档|`POST /ag-ui/kb/{kbId}/documents`|body：`{attachmentId}`（先经 `POST /ag-ui/upload` 上传）；立即返回 `{docId, fileName, chunkCount, status, error}`（status=processing），后台完成切片向量化|
|移除文档|`DELETE /ag-ui/kb/{kbId}/documents/{docId}`|删除文档切片向量（处理中的文档可直接移除）|

系统级知识库（ownerId=null）对所有用户可见但只读（不开放修改）。

群内注册请求示例：

```json
{
  "agentId": "agent_prd",
  "nickname": "需求助手",
  "groupId": "group_001",
  "triggerMode": "mentioned",
  "keywords": [],
  "override": true
}
```

`override=true` 表示在群内显式覆盖角色默认触发方式（角色编辑不再覆写本群）；`false` 表示跟随角色默认（角色编辑自动同步）。

### 5.9 记忆、任务与运维管理接口（Hub 扩展）

以下均为 Hub 扩展管理类接口，扩展群聊协议的能力面（记忆治理、重复任务、外部专家市场、审计与运维观测）。

**记忆治理与时间线**（需登录，仅可治理**自己所在群**的记忆）：

|接口|路径|说明|
|---|---|---|
|记忆总览|`GET /ag-ui/memory/groups`|用户所在各群的记忆统计（条目数 / 各级别分布）|
|记忆列表|`GET /ag-ui/memory`|记忆条目可视化（按群 `groupId` / 发送者 `senderId` / 关键词 `keyword` 筛选，`limit`/`offset` 分页）|
|记忆时间线|`GET /ag-ui/memory/timeline`|**2.2 时间线回放**：按群 / 话题 / 关键词回放记忆的旧→新演进，用于复盘「某结论如何演化」|
|调整记忆级别|`POST /ag-ui/memory/{messageId}/importance`|设置单条记忆级别 `0 普通 / 1 重要 / 2 关键`（同相似度下高级别优先被检索注入）|
|删除记忆|`DELETE /ag-ui/memory/{messageId}`|物理删除单条记忆|
|手动遗忘|`POST /ag-ui/memory/forget`|按群（或全部）设过期，可保留最近 N 小时；对应记忆停止参与检索并由后台定时清理|
|记忆沉淀知识库|`POST /ag-ui/memory/consolidate`|**1.3**：把某群「关键」级别的记忆聚合写入指定知识库（自动/半自动沉淀结论为文档），body 含 `groupId` / `kbId` 等；复用知识库切片向量化|

**重复性定时任务（1.4，值班智能体）**：

|接口|路径|说明|
|---|---|---|
|任务列表|`GET /ag-ui/scheduled-tasks`|我所在群的智能体任务；管理员看全部|
|创建任务|`POST /ag-ui/scheduled-tasks`|`{agentId, name, cron, prompt?, groupId?, enabled?}`——到点触发智能体生成汇报 / 核对 / 催办|
|更新任务|`PUT /ag-ui/scheduled-tasks/{taskId}`|改名称 / cron / 汇报指令 / 目标群 / 启用状态|
|删除任务|`DELETE /ag-ui/scheduled-tasks/{taskId}`|删除任务；创建 / 编辑须为相关群成员（管理员任意）|

**智能体 / 技能市场（3.3）**：

|接口|路径|说明|
|---|---|---|
|市场目录|`GET /ag-ui/marketplace`|可选角色 / 技能包目录（行业角色包，复用 `agents-starter` 打包结构，需登录）|
|一键导入|`POST /ag-ui/marketplace/import/{packId}`|把市场包导入为当前用户智能体（agentId 冲突自动改 ID 不覆盖）|

**桥接健康度与能力（3.1 / 3.2，仅管理员）**：

|接口|路径|说明|
|---|---|---|
|桥接健康度|`GET /ag-ui/admin/bridge-health?refresh=`|已配置外部 AG-UI 端点的实时/缓存连通状态（支持自动重连退避、断线补发，`BridgeCircuitBreaker`）|
|桥接能力协商|`GET /ag-ui/admin/bridge-capabilities?refresh=`|外部端点能力（支持的工具 / 附件 / 审批类型，Capability Discovery），减少人工配置|

**审计与观测（4.3 / 6.1 / 6.3，仅管理员）**：

|接口|路径|说明|
|---|---|---|
|操作审计日志|`GET /ag-ui/admin/audit?limit=`|关键 / 敏感操作留痕（谁 / 何时 / 批准了什么工具 / 导入导出 / 重置等），`limit` ≤200，可导出满足合规|
|运行状态|`GET /ag-ui/admin/status`|连接 / 群 / 用户 / 智能体 / 消息计数、内存、线程等进程信息|
|模型用量|`GET /ag-ui/admin/usage?days=`|最近 N 天模型调用量按日汇总 + 配额配置|
|运行指标|`GET /ag-ui/admin/metrics`|智能体调用 / 桥接 / 记忆命中 / 输出长度等进程内指标计数（6.1）|
|配置快照|`GET /ag-ui/admin/config`|集中只读展示 appsettings / .env 关键运维参数（模型、存储、权限、允许来源等），供治理与排障（6.3）|

**多设备会话与二次验证（4.4，需令牌）**：

|接口|路径|说明|
|---|---|---|
|TOTP 状态|`GET /ag-ui/user/totp`|是否已启用二次验证 |
|签发密钥|`POST /ag-ui/user/totp/enroll`|生成 TOTP 密钥（供绑定）|
|确认启用|`POST /ag-ui/user/totp/confirm`|校验动态码后启用 TOTP（登录时需二次验证）|
|停用 TOTP|`POST /ag-ui/user/totp/disable`|关闭二次验证|
|会话列表|`GET /ag-ui/user/sessions`|当前账号全部登录设备会话|
|吊销会话|`POST /ag-ui/user/sessions/revoke`|吊销指定会话 |
|吊销其它|`POST /ag-ui/user/sessions/revoke-others`|吊销除本会话外的全部会话|

**细粒度 RBAC（4.2，频道级）**：群成员经 `extra["rbac"]` 携带 `GroupMemberPermissions`（`canInvokeAgents` 谁能 @ 智能体 / `canApprove` 谁能批准人机交互 / `canManageKnowledge` 谁能管理知识库）；字段显式置 false 时限制，null 跟随角色 / 管理员默认允许；`IsAdmin` / `AdminUserIds` 决定系统级管理员，可访问 §5.9 管理员接口。

**白标品牌与嵌入（6.4）**：

|接口|路径|说明|
|---|---|---|
|查询品牌配置|`GET /ag-ui/settings/branding`|公开：返回 `{configured, appName, logoUrl, primaryColor, forceDark, tagline}`，供登录页 / 嵌入页渲染 |
|保存品牌配置|`POST /ag-ui/settings/branding`|仅系统管理员：配置应用名（≤40）/ Logo（站内路径或 https / data:image）/ 品牌主色（6 位 hex）/ 强制深色 / 副标语；持久化到扩展区；非法主色 / 危险 Logo 返回 400 |
|iframe 嵌入|—|`GroupChatOptions.AllowedFrameOrigins` 配置允许嵌入来源（默认空 = 禁止）；CSP `frame-ancestors` 与 `X-Frame-Options` 相应放行 |
|对外 API 密钥|—|`Auth:ApiKeys`：`Authorization: Bearer <apiKey>` 免登录以绑定账号身份调用全部 HTTP API（继承其群成员 / 权限 / 管理员标记）|

### 5\.8 WebSocket 上行事件

WebSocket 连接上可直接上行以下事件（等效对应 HTTP 接口），字段与 HTTP 请求体一致：

```json
{
  "type": "GROUP_MESSAGE_SEND",
  "groupId": "group_001",
  "topicId": "main",
  "content": "这是经 WS 发送的消息",
  "mentions": ["agent_prd"],
  "visibility": "all",
  "attachments": [
    { "attachmentId": "att_002", "name": "图表.png", "contentType": "image/png", "size": 4096, "url": "/ag-ui/files/att_002/%E5%9B%BE%E8%A1%A8.png", "kind": "image" }
  ]
}
```

|上行类型|等效 HTTP|说明|
|---|---|---|
|GROUP_MESSAGE_SEND|POST /ag-ui/group/message/send|发送群消息（发送者身份取连接身份，忽略请求内 userId）|
|GROUP_MESSAGE_RECALL|POST /ag-ui/group/message/recall|撤回消息|
|AGENT_INTERACTION_RESOLVE|POST /ag-ui/group/interaction/resolve|人机交互决策（仅触发者；决策者身份取连接身份）|
|GROUP_TYPING|POST /ag-ui/group/message/typing|输入状态|
|GROUP_MESSAGE_READ|POST /ag-ui/group/message/read|已读回执|
|GROUP_SUBSCRIBE / GROUP_UNSUBSCRIBE|POST /ag-ui/group/subscribe|订阅 / 退订群组（见 §4.6）|

服务端对 WS 上行按连接身份鉴权（令牌优先于 memberId，见 §5.3 会话令牌）；请求中的身份字段一律以服务端解析的连接身份覆盖（GROUP_MESSAGE_SEND 的 userId、GROUP_MESSAGE_RECALL 的 operatorId、GROUP_TYPING / GROUP_MESSAGE_READ 的 memberId），防伪造。

## 6\. 智能体群聊触发规则（协议建议）

### 6\.1 触发方式

1. **提及触发（mentioned）**：消息 `mentions` 包含对应 `agentId`（或 `mentionAll`）时，智能体启动处理并生成 `runId`

2. **全量监听（allMessages）**：配置为全量监听的智能体接收所有群消息，自行判断是否响应

3. **关键词触发（keyword）**：服务端按配置的关键词匹配消息内容（大小写不敏感），命中后唤醒对应智能体

4. **语境触发（contextual）**：服务端按最近消息上下文决策（`Agents:ContextMaxMessages` 条，默认 10），命中后唤醒对应智能体

5. 智能体响应必须携带 `senderId` 与 `senderType`，前端据此渲染身份标识；智能体自身发送的消息不触发自身
6. 智能体回复消息**不回显触发消息的 `mentions` / `mentionAll`**（提及仅用于触发，避免 @ 回显到正文）

### 6\.2 群内触发方式覆盖

智能体在某个群内的生效触发方式可独立于角色默认值：

- 建群 / 加成员时可按群注册触发规则（§5.7 `POST /ag-ui/agents/register`）
- `override=true`：群内显式覆盖角色默认（角色编辑不覆写本群设定）
- `override=false`：跟随角色默认（角色编辑自动同步该群）
- 快照与成员列表中，智能体成员携带 `triggerMode` / `keywords` / `isTriggerOverridden` 三字段回显当前生效值（§2.2 / §4.7）

### 6\.3 AG-UI 桥接（外部专家）

智能体定义配置 `bridgeEndpoint`（§5.7）后，该角色**不经本地大模型**，改以 AG-UI 协议对接外部 AG-UI 服务（标准 AG-UI 或本项目群聊扩展，`bridgeMode` 取 `standard` / `hub`）：触发消息转发给外部服务，其流式回复经 `TEXT_MESSAGE_*` 事件回灌群聊；`bridgeToken` 用于连接鉴权（编辑时留空表示沿用原值，不回显）。

### 6\\.4 AI 分身（Twin）

用户启用分身（§5.3）后生成**私密智能体** `twin_{userId}`，触发规则如下：

- 归属用户创建，`isPrivate=true`，仅创建者可管理（拉群 / 编辑 / 删除）
- 自动加入归属用户所在的**全部公开群**（私密群不加入），触发方式由用户设定并可随时修改（`POST /ag-ui/twin/trigger` 同步各群）
- 公开群新增 / 移除用户成员时，服务端自动跟随加入 / 退出（`ITwinAgentSync` 钩子）
- **仅离线触发**：归属用户存在活跃连接时分身暂停不响应（在线 / 离线互斥，成员列表相应切换显示）
- 停用 / 在线切换时，各群触发规则与成员身份随生命周期自动清理 / 恢复

## 7\. 错误码扩展

在原生 `RUN_ERROR` 事件基础上，新增群聊专属错误类型：

|错误标识|说明|
|---|---|
|GROUP\_NOT\_FOUND|群组不存在|
|GROUP\_PERMISSION\_DENIED|无群组操作权限|
|GROUP\_MEMBER\_NOT\_EXIST|目标成员不在群组内|
|GROUP\_FULL|群成员数量达上限|
|GROUP\_MESSAGE\_NOT\_FOUND|消息不存在或已撤回|
|GROUP\_SUBSCRIBE\_FAILED|群组订阅失败|

Hub 扩展错误码（用户 / 智能体管理）：

|错误标识|说明|
|---|---|
|BAD\_REQUEST|请求格式错误（无法解析 / 缺少字段）|
|USER\_NOT\_FOUND|用户不存在|
|USER\_EXISTS|用户名已被注册（注册冲突）|
|USER\_BAD\_CREDENTIALS|用户名或密码错误（登录失败）|
|USER\_PASSWORD\_INVALID|旧密码不正确（修改密码时）|
|USER\_UNAUTHORIZED|未登录或令牌无效 / 已过期|
|AGENT_NOT_FOUND|智能体不存在（未在目录中声明）|
|AGENT_EXISTS|智能体 ID 已被占用|
|AGENT_PERMISSION_DENIED|私密智能体仅创建者可操作（拉入群 / 编辑 / 删除，返回 403）|

错误响应体统一为 `{"code": "...", "message": "..."}`（HTTP 状态码见各接口实现：401 / 403 / 404 / 409）。

## 8\. 向后兼容性说明

1. 原生 AG\-UI 客户端接收群聊事件时，可忽略所有群相关字段，正常解析文本、工具调用等核心内容

2. 群聊模式下 `threadId` 与 `groupId` 一一对应，单聊上下文逻辑可无缝复用

3. 未实现群聊功能的服务端，可直接忽略上行请求中的群字段，降级为单聊处理

4. 所有新增事件均为可选实现，按需启用即可满足不同层级的群聊需求

5. 话题 / 附件 / 智能体管理 / 用户管理均为 Hub 扩展：未实现的服务端与旧客户端可忽略相关字段、事件与接口，不影响基础群聊

6. `GROUP_CONNECTED`（握手）、`GROUP_TOPIC_CREATED`、`GROUP_TOPIC_DELETED`、`GROUP_MESSAGE_TOPIC_MOVED` 为可选事件，旧客户端按未知事件忽略

> （注：部分内容可能由 AI 生成）
