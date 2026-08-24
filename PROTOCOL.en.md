# AG-UI Group Chat Extension Protocol Standard v1.0

**English** | [简体中文](AG-UI%20群聊扩展协议标准%20v1.0.md)

This standard is an extension of the native AG-UI (Agent-User Interface) protocol. While remaining fully backward compatible, it adds real-time group chat capabilities for multi-user, multi-agent collaboration, and stays consistent with the native event system, wire format, and naming conventions.

## 1. Overview

### 1.1 Extension Goals

- Support real-time interaction among multiple people + multiple agents within the same group

- Remain compatible with all native one-on-one chat events and fields; legacy clients can parse base content without modification

- Cover the complete group chat lifecycle, including group management, member status, message interaction, and permission control

- User account / session token management (Hub extension): a registered user is a group member and can join a group directly to participate in group chat (see §5.3)

- Support both SSE and WebSocket transport modes

> **Official reference implementation**: The official .NET client SDK for this protocol is located at `src/AguiGroupChat.Sdk` (`AguiClient` HTTP upstream + `AguiRealtimeClient` WS/SSE downstream + strongly typed Models consistent with the wire format). Third-party applications can reference and integrate it directly (example `samples/AguiGroupChat.Client`, see [SDK documentation](src/AguiGroupChat.Sdk/README.md)).

### 1.2 Compatibility Principles

- All group-chat-specific fields are optional; native AG-UI clients can simply ignore the added fields

- All native event types are preserved; group chat context is carried only through extension fields

- Newly added events are uniformly named with the `GROUP_` prefix and do not conflict with native events

- In the group chat scenario, `threadId` and `groupId` correspond one-to-one, so one-on-one chat logic can be reused seamlessly

### 1.3 Applicable Scenarios

- Multi-person collaborative AI deliberation and requirements review groups

- Multi-agent collaboration work groups (product assistant + code assistant + data assistant)

- Community-style AI customer service and Q&A groups

- Multi-party approval and human-machine co-creation Agent scenarios

## 2. Core Data Model

### 2.1 Group Model

|Field|Type|Required|Description|
|---|---|---|---|
|groupId|string|Yes|Unique group identifier; naming recommendation: `group_xxx`|
|groupName|string|Yes|Display name of the group|
|groupAvatar|string|No|Group avatar URL|
|ownerId|string|Yes|Owner member ID|
|memberCount|number|Yes|Current total number of members|
|createTime|number|Yes|Creation timestamp (milliseconds)|
|isPrivate|boolean|No|Whether the group is private (default false). For private groups, the semantic memory is **only retrievable within the group**: when agents are triggered in other groups (scope=agent/all) content of private groups is excluded, whereas triggering within the private group itself is unaffected|
|extra|object|No|Custom business extension fields|

### 2.2 Group Member Model (GroupMember)

|Field|Type|Required|Description|
|---|---|---|---|
|memberId|string|Yes|Unique member identifier; users use `user_xxx`, agents use `agent_xxx`|
|memberType|enum|Yes|Member type: `user` / `agent`|
|nickname|string|Yes|Display nickname within the group|
|avatar|string|No|Member avatar URL|
|role|enum|Yes|Group role: `owner` / `admin` / `normal`|
|onlineStatus|enum|No|Online status: `online` / `offline` / `busy`|
|joinTime|number|Yes|Join timestamp|
|extra|object|No|Business extension fields|

### 2.3 Group Message Extension Model

Group chat attributes are added on top of the native message fields; all fields are optional:

|Field|Type|Default|Description|
|---|---|---|---|
|groupId|string|-|The group ID to which the message belongs|
|senderId|string|-|Sender member ID|
|senderType|enum|-|Sender type: `user` / `agent`|
|senderNickname|string|-|Sender's group nickname, for direct rendering by the frontend|
|replyToMessageId|string|-|Target message ID of the quoted reply|
|mentions|string[]|[]|List of @-mentioned member IDs|
|mentionAll|boolean|false|Whether everyone in the group is @-mentioned|
|visibility|enum|`all`|Visibility: `all` visible to entire group / `mentioned` visible only to the mentioned members / `private` visible only to the specified members|
|visibleMemberIds|string[]|[]|Targeted visible member list, used with `private`|
|topicId|string|`main`|The topic ID the message belongs to (default `main` is the main topic; may be migrated by "create a new topic from this message", see §4.8)|
|attachments|AttachmentInfo[]|[]|Message attachment list (model in §2.5, upload in §5.6)|

### 2.4 Topic Model (GroupTopic)

An independent discussion thread within the group. The default topic `main` (the main topic) always exists and is not persisted; new topics are created by group members, and messages can be assigned / migrated to the corresponding topic.

|Field|Type|Required|Description|
|---|---|---|---|
|topicId|string|Yes|Unique topic identifier, named `topic_xxx`|
|groupId|string|Yes|The group ID to which the topic belongs|
|name|string|Yes|Topic name (≤30 characters)|
|creatorId|string|Yes|Creator member ID|
|createdAt|number|Yes|Creation timestamp (milliseconds)|

### 2.5 Attachment Model (AttachmentInfo)

Messages can carry attachments (images / documents / binary). `text` and `document` type attachments (plain text such as txt, md, source code, as well as office documents such as docx / xlsx / pptx / pdf) have their full text extracted by the server and injected into the agent context (truncated when too long); `image` / `binary` types only carry metadata for the model to perceive.

|Field|Type|Required|Description|
|---|---|---|---|
|attachmentId|string|Yes|Unique attachment identifier (att_xxx), corresponds one-to-one to the upload directory|
|name|string|Yes|Original file name (sanitized; used for frontend display and download)|
|contentType|string|Yes|MIME type|
|size|number|Yes|Size in bytes|
|url|string|Yes|Download URL `GET /ag-ui/files/{attachmentId}/{name}`|
|kind|enum|Yes|Category: `image` / `audio` / `text` / `document` / `binary` (audio is a voice message, carries metadata only for frontend playback)|

## 3. Transport Layer Specification

### 3.1 Transport Methods

- **Downstream push**: WebSocket full-duplex transport is preferred; SSE one-way downstream is also supported, with each client establishing an independent connection to receive group messages

- **Upstream requests**: HTTP POST or WebSocket upstream, format consistent with native AG-UI

- **Encoding format**: Unified UTF-8 JSON serialization; the event `data: ` prefix rule is consistent with native

- **Connection keepalive**: WebSocket uses server Ping frames and SSE uses heartbeat comment lines to keep the connection alive; the interval is configured on the server (`GroupChat:HeartbeatIntervalSeconds`, default 15 seconds)

### 3.2 Connection and Subscription

1. When establishing a connection, the client must carry identity credentials (token authentication convention in §5.3 User Management APIs); the server validates group member permissions

2. A single connection can subscribe to multiple groups; the receive scope is managed through subscription events

3. Non-group members cannot receive any event pushes for the corresponding group

4. After the connection is established, the server pushes the `GROUP_CONNECTED` handshake event (carrying connectionId). In the SSE scenario, the subscription scope can be adjusted dynamically via `POST /ag-ui/group/subscribe` / `POST /ag-ui/group/unsubscribe`; in the WebSocket scenario, subscriptions are managed via the `GROUP_SUBSCRIBE` / `GROUP_UNSUBSCRIBE` events

```json
{
  "type": "GROUP_CONNECTED",
  "connectionId": "conn_8f3a2b9c",
  "memberId": "user_1001",
  "transport": "websocket",
  "timestamp": 1750000010000
}
```

`transport` values: `websocket` / `sse`.

## 4. Complete Event Type Specification

### 4.1 Naming Rules

- Group-specific events start with the `GROUP_` prefix

- Native events keep their original names; only optional group fields are added

- Event names uniformly use uppercase underscore format (consistent with the native style)

Event overview (direction: downstream = server push; upstream = client request):

|Event|Direction|Triggering party|Section|
|---|---|---|---|
|GROUP_CONNECTED|Downstream (handshake)|On connection establishment|§3.2|
|GROUP_CREATED / GROUP_UPDATED / GROUP_DISBANDED|Downstream|Group operations|§4.2|
|GROUP_MEMBER_JOINED / LEFT / UPDATED|Downstream|Member changes|§4.3|
|TEXT_MESSAGE_START / CONTENT / END|Downstream|Message send/receive / agent streaming replies|§4.4|
|GROUP_MESSAGE_RECALLED|Downstream|Message recall|§4.4|
|GROUP_TYPING|Downstream|Typing status|§4.4|
|GROUP_MESSAGE_READ|Downstream|Read receipt|§4.4|
|TOOL_CALL_START|Downstream|Tool call start|§4.5|
|AGENT_INTERACTION_REQUEST / AGENT_INTERACTION_RESOLVE|Downstream / Upstream|Human-machine interaction (tool approval, only the trigger may decide)|§4.5|
|AGENT_INTERACTION_RESOLVED|Downstream|Human-machine interaction decision result broadcast (syncs card state to the whole group)|§4.5|
|GROUP_SUBSCRIBE / GROUP_UNSUBSCRIBE|Upstream|Client subscribe / unsubscribe|§4.6 / §5.8|
|GROUP_SUBSCRIBE_ACK|Downstream|Subscription result|§4.6|
|GROUP_TOPIC_CREATED|Downstream|Topic creation|§4.8|
|GROUP_TOPIC_DELETED|Downstream|Topic deletion (its chat records and memory are also cleared)|§4.8|
|GROUP_TOPIC_CLEARED|Downstream|Topic chat records cleared (topic retained; messages and memory cleared together)|§4.8|
|GROUP_MESSAGE_TOPIC_MOVED|Downstream|Message migrated to a topic|§4.8|
|GROUP_STATE_SNAPSHOT|Downstream|Join / subscription success|§4.7|
|RUN_ERROR|Downstream|Run error|§7|
|GROUP_MESSAGE_SEND / GROUP_MESSAGE_RECALL|Upstream (WS)|Send / recall message|§5.8|

---

### 4.2 Group Lifecycle Events

#### GROUP_CREATED

Sent to all initial members when a group is created successfully.

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

#### GROUP_UPDATED

Broadcast to the whole group when the base information of the group changes.

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

#### GROUP_DISBANDED

Pushed to the whole group when the group is disbanded; after the push, the server terminates all events for that group.

```json
{
  "type": "GROUP_DISBANDED",
  "groupId": "group_001",
  "operatorId": "user_1001",
  "timestamp": 1750000200000
}
```

---

### 4.3 Group Member Events

#### GROUP_MEMBER_JOINED

Broadcast to the whole group when new members join; supports batch joining.

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

#### GROUP_MEMBER_LEFT

Broadcast to the whole group when a member voluntarily leaves or is removed.

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

`leaveType` values: `voluntary` voluntarily left / `kick` removed.

#### GROUP_MEMBER_UPDATED

Pushed when a member's role, nickname, or online status changes.

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

### 4.4 Group Message Events (Native Event Extensions)

All native `TEXT_MESSAGE_*` events are preserved, with new optional group chat fields added; legacy clients can simply ignore them.

#### TEXT_MESSAGE_START (Group Chat Extension)

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

> Actual clients may receive **extra optional fields** (legacy clients may ignore these, and they appear only in the corresponding scenarios): `topicId` (the topic the message belongs to, defaulting to `main`), `visibleMemberIds` (targeted visible members when `visibility=private`); user messages also carry `attachments` (attachment object array, §2.5).

#### TEXT_MESSAGE_CONTENT

The native format is completely unchanged; the group context is associated via `messageId` with the START event. In addition, the delta event may actually also carry the optional `groupId` (so the group can be located without correlating with START; legacy clients may ignore it).

```json
{
  "type": "TEXT_MESSAGE_CONTENT",
  "messageId": "msg_789",
  "delta": "针对这个需求，我建议从三个方向拆解..."
}
```

#### TEXT_MESSAGE_REASONING (Hub Extension, AG-UI Thinking Mode)

Incremental output of the agent's reasoning process, streamed back independently of the main body; the frontend renders it as a collapsible "thinking process" block (expanded and visible live during streaming, collapsed by default when finished), displayed separately from the main body. When the message ends, `TEXT_MESSAGE_END` carries the `reasoning` snapshot for replay.

```json
{
  "type": "TEXT_MESSAGE_REASONING",
  "messageId": "msg_789",
  "delta": "先拆解需求，再对比方案..."
}
```

> Like CONTENT, REASONING deltas may also carry the optional `groupId` (legacy clients may ignore it).

#### TEXT_MESSAGE_END (Group Chat Extension)

```json
{
  "type": "TEXT_MESSAGE_END",
  "messageId": "msg_789",
  "groupId": "group_001",
  "reasoning": "先拆解需求，再对比方案...",
  "timestamp": 1750000610000
}
```

#### GROUP_MESSAGE_RECALLED

Message recall event, broadcast to the whole group.

```json
{
  "type": "GROUP_MESSAGE_RECALLED",
  "groupId": "group_001",
  "messageId": "msg_789",
  "operatorId": "user_1001",
  "timestamp": 1750000700000
}
```

#### GROUP_TYPING

A member's typing status notification, which can be used to render "typing...".

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

#### GROUP_MESSAGE_READ

Message read receipt, optional to implement.

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

### 4.5 In-Group Tool Call Events (Native Extensions)

The native `TOOL_CALL_*` events are extended with group fields to support controlling the visibility of tool call results.

#### TOOL_CALL_START (Group Chat Extension)

```json
{
  "type": "TOOL_CALL_START",
  "toolCallId": "tool_001",
  "toolCallName": "search_prd_doc",
  "toolArguments": "{\"query\":\"发布公告\"}",  // Optional: tool arguments (JSON text); when external bridge arguments arrive in frames, they are resent via TOOL_CALL_ARGS
  "parentMessageId": "msg_789",
  "groupId": "group_001",
  "triggerUserId": "user_1001",
  "visibility": "mentioned",
  "visibleMemberIds": ["user_1001"],
  "timestamp": 1750000620000
}
```

#### TOOL_CALL_ARGS (Group Chat Extension, Hub Extension)

The **complete text** of the tool call arguments (in the bridge scenario, resent after TOOL_CALL_ARGS frames accumulate to completion; local tools already carry arguments in TOOL_CALL_START and no resend occurs):

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

#### TOOL_CALL_RESULT (Group Chat Extension, Hub Extension)

Tool execution result fed back (local tool `FunctionResultContent` / external AG-UI `TOOL_CALL_RESULT`), displayed by the frontend in association with the invocation line:

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

#### AGENT_INTERACTION_REQUEST (Human-Machine Interaction, Hub Extension)

When an agent tool requires **manual approval** (the server wraps it with `ApprovalRequiredAIFunction`, e.g., the `Agents:RequireApprovalToolNames` list),
the run in which the model invoked that tool ends with an **interruption** (the tool is not executed); the gateway saves the run state and broadcasts this event to the whole group.
**The interaction object is limited to the trigger (`targetMemberId`)**: other members can see the card but have no authority to decide.

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

**Input-type extension fields** (`kind=input/choice/multi_choice`, external question tool):

| Field | Description |
|---|---|
| `kind` | `input` (text input) / `choice` (single choice) / `multi_choice` (multiple choice) / `approval` (tool approval, default) |
| `inputField` | The response field name (default `answer`); when resuming, user input is returned using this name as the key |
| `responseSchema` | Complete JSON Schema, based on which the frontend renders a generic form (single-choice enum / multi-choice array / number / multi-field) |
| `options` | List of options for kind=`choice`/`multi_choice` (from the `responseSchema` enum) |
| `questions` | Structured question list for the external question tool (e.g., OpenCode `metadata.questions`), rendered option by option by the frontend |

`questions` array element structure: `{ header?, question, options?: [{ label, description? }], multiple? }`.
The frontend renders each question (single-choice radio / `multiple:true` multi-choice checkbox / text input when no options), and the answers are returned in question order.

**Answer format convention for input-type resumption (AGENT_INTERACTION_RESOLVE → bridge resume)**: a two-dimensional array is not used; the resume payload is a **single-key JSON object** keyed by `inputField` (a sub-field name of `responseSchema`, default `answer`) — text / single choice are strings, multiple choice is a JSON string array under that key; if the frontend submits according to the complete `responseSchema`, then the `payload` object is passed back as-is (each field occupying one key):

```json
{ "answer": "行业研究报告" }          // text / single choice
{ "answer": ["市场规模", "技术趋势"] } // multiple choice (single-key JSON array)
{ "topic": "行业研究", "slides": 16 } // multi-field: full payload according to responseSchema
```

#### AGENT_INTERACTION_RESOLVE (Human-Machine Interaction Decision, Hub Extension)

The trigger approves / rejects the interaction request: the server validates that the decider **must be the `targetMemberId` of the `AGENT_INTERACTION_REQUEST`** (the trigger),
then feeds "approve / reject" back into the **same AgentSession** as a User message to resume the run (approve → execute the tool and continue replying; reject → skip the tool and continue).

WS upstream example (decider identity is taken from the connection; equivalent HTTP `POST /ag-ui/group/interaction/resolve`, `memberId` is taken from the token identity):

```json
{
  "type": "AGENT_INTERACTION_RESOLVE",
  "groupId": "group_001",
  "interruptId": "interrupt_abc",
  "approved": true
}
```

- `approved`: true = approve (execute the tool); false = reject (skip the tool). Always true when submitting kind=input/choice/multi_choice interactions
- `input`: the user's input text for kind=input interactions (empty for tool approval type)
- `payload`: the complete JSON submitted per `responseSchema` for kind=input interactions (single-choice / multi-choice / number / multi-field object)
- `approveAll`: true = enable batch approval for the current run (subsequent approvals of the same kind auto-approve without interrupting one by one); takes effect only when `approved=true`

- Interruption by a non-trigger / request already expired (10 minutes) → rejected and an error is returned (HTTP 400, `BAD_REQUEST`; WS sends `RUN_ERROR`)
- The resumed agent reply is streamed back as new `TEXT_MESSAGE_START/CONTENT/END` (new runId)
- The server may interrupt again during resumption (tool chain), re-broadcasting `AGENT_INTERACTION_REQUEST` (same `targetMemberId`)

#### AGENT_INTERACTION_RESOLVED (Decision Result Broadcast, Hub Extension)

After the trigger's decision takes effect, the server **broadcasts the decision result to the whole group**, and the interaction cards of other members update synchronously to "approved / rejected" (no longer remaining in the waiting state):

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

- For the decider him/herself, both the local echo and this event update the card (idempotent)
- Broadcast only when the trigger's decision succeeds; no broadcast if the interaction does not exist or the broadcaster is not the trigger

**Human-machine interaction of the AG-UI bridge role**: when an agent is configured with `bridgeEndpoint` (§5.7 / §6.3) to go through an external AG-UI service, all three transport forms also support approval interruption:

- **standard + HTTP(S)**: a self-hosted SSE client parses the external service's standard AG-UI event stream — `TEXT_MESSAGE_END` only marks the end of a message;
  the authoritative termination event is `RUN_FINISHED` (an interruption is produced when `outcome.type="interrupt"`, with arguments accumulated incrementally from `TOOL_CALL_ARGS` and refilled);
  the Hub gateway broadcasts `AGENT_INTERACTION_REQUEST` and saves the bridge connection; after the trigger's decision, it sends `RunAgentInput` + the `resume` array upstream to resume
- **standard + WebSocket**: the gateway parses the external service's `RUN_FINISHED` + `outcome.interrupts[0]` (`id` / `reason` / `message` / `toolCallId` / `metadata`),
  and when resuming sends the `RunAgentInput` structure upstream carrying `resume: [{interruptId, status:"resolved", payload:{approved, toolCall:{callId, name, arguments}}}]`
  (`AGUIToolApprovalResumePayload`: the field is `approved` and the approved tool's `toolCall` must be returned; `status:"cancelled"` cancels)
- **hub dialect**: recognizes the `AGENT_INTERACTION_REQUEST` broadcast by the external Hub (fields passed through), and when resuming sends
  `AGENT_INTERACTION_RESOLVE` to the external Hub (encoded upstream via WS or HTTP `POST /ag-ui/group/interaction/resolve`, member identity = bridge agent) — supports Hub cascading approval

Regardless of the form, the interaction object is **always the trigger within this Hub** (`targetMemberId`); the external service has no authority to decide who may interact.

---

### 4.6 Group Subscription Events

Used in the scenario of subscribing to multiple groups over a single connection.

#### GROUP_SUBSCRIBE (Client Upstream)

The client requests to subscribe to the specified groups.

```json
{
  "type": "GROUP_SUBSCRIBE",
  "groupIds": ["group_001", "group_002"],
  "timestamp": 1750000800000
}
```

#### GROUP_SUBSCRIBE_ACK (Server Downstream)

The server returns the subscription result.

```json
{
  "type": "GROUP_SUBSCRIBE_ACK",
  "successGroupIds": ["group_001"],
  "failedGroupIds": ["group_002"],
  "failReason": "无群组访问权限",
  "timestamp": 1750000801000
}
```

#### GROUP_UNSUBSCRIBE

Cancel subscription to the specified groups.

```json
{
  "type": "GROUP_UNSUBSCRIBE",
  "groupIds": ["group_001"],
  "timestamp": 1750000900000
}
```

---

### 4.7 Group State Synchronization Events

#### GROUP_STATE_SNAPSHOT

After a member joins / subscribes successfully, the server pushes the complete state snapshot of the group.

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

> `latestMessages` elements may actually also carry **optional fields** (legacy clients may ignore them): `senderNickname`, `replyToMessageId`, `attachments`, `reasoning`, `mentions`, `mentionAll`, `topicId`.

When a member is an agent, its effective in-group trigger rules are echoed back: `triggerMode` / `keywords` / `isTriggerOverridden` (see §6.2); `topics` is the list of topics within the group (excluding the default `main`, see §2.4). The snapshot can also be pulled proactively via `GET /ag-ui/group/{groupId}` (see §5.4).

---

### 4.8 Group Topic Events (Hub Extension)

#### GROUP_TOPIC_CREATED

Broadcast to the whole group when a group member successfully creates a new topic; if a starting message was specified at creation, it is accompanied by `GROUP_MESSAGE_TOPIC_MOVED` (the message is migrated to the new topic, see below).

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

#### GROUP_MESSAGE_TOPIC_MOVED

Broadcast to the whole group when a message is migrated to a new topic ("create a new topic from this message").

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

Broadcast to the whole group when a topic is deleted (only the group owner / admin or the topic creator may delete); **the topic's chat records and the corresponding semantic memory are deleted together** (not recoverable).

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

Broadcast to the whole group when **clearing a topic's chat records** (group owner / admin only): the topic itself is retained, while the messages under that topic (including the main topic `main`) and the corresponding semantic memory are physically deleted together (not recoverable).

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

## 5. Client Upstream Request Specification

### 5.1 Sending a Group Text Message

- Endpoint: `POST /ag-ui/group/message/send`; equivalent WS upstream event in §5.8

Request body fields:

|Field|Type|Required|Description|
|---|---|---|---|
|groupId|string|Yes|Target group ID|
|threadId|string|No|Defaults to `thread_ + groupId` (corresponds one-to-one with groupId in group chat)|
|userId|string|Yes|Sender member ID (for WS upstream, the connection identity resolved by the server is used instead)|
|content|string|Yes|Message text (may be empty if only attachments are sent; then only attachments are sent)|
|topicId|string|No|The topic the message belongs to, default `main` (must be an existing topic)|
|replyToMessageId|string|No|Target message ID of the quoted reply (rejected if the target does not exist or has been recalled)|
|mentions|string[]|No|List of @-mentioned member IDs|
|mentionAll|boolean|No|Whether @ everyone|
|visibility|enum|No|`all` / `mentioned` / `private`, default `all`|
|visibleMemberIds|string[]|No|Targeted visible member list, used with `private`|
|attachments|AttachmentInfo[]|No|Attachment object array (upload first via §5.6, then carry the returned attachment metadata unchanged)|

Example request body:

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

After a successful send, the message is echoed and broadcast as the `TEXT_MESSAGE_START` / `TEXT_MESSAGE_CONTENT` / `TEXT_MESSAGE_END` triad (the sender always receives it, see §4.4), and agents in the group are awakened per the §6 trigger rules.

### 5.2 Group Management APIs

|API|Path|Core parameters|
|---|---|---|
|Create group|`POST /ag-ui/group/create`|groupName、ownerId、isPrivate*、memberIds、members[] (may carry nickname / type / avatar)|
|Update group info|`POST /ag-ui/group/update`|groupId、updateFields、groupInfo、operatorId|
|Disband group|`POST /ag-ui/group/disband`|groupId、operatorId|
|Add member|`POST /ag-ui/group/member/add`|groupId、memberIds、operatorId、memberDetails[]|
|Remove member|`POST /ag-ui/group/member/remove`|groupId、memberIds、operatorId|
|Member leave|`POST /ag-ui/group/member/leave`|groupId、memberId|
|Update member|`POST /ag-ui/group/member/update`|groupId、memberId、updateFields、memberInfo、operatorId|
|Recall message|`POST /ag-ui/group/message/recall`|groupId、messageId、operatorId|
|Human-machine interaction decision|`POST /ag-ui/group/interaction/resolve`|groupId、interruptId、approved (only the trigger may decide; other members get 400)|
|Typing status|`POST /ag-ui/group/message/typing`|groupId、memberId、isTyping|
|Read receipt|`POST /ag-ui/group/message/read`|groupId、memberId、readMessageId|

`updateFields` / `memberInfo` use a "field whitelist + key-value pairs" update mode (same as the GROUP_UPDATED / GROUP_MEMBER_UPDATED events); supported fields: group info `groupName` / `groupAvatar` / `isPrivate`; member `role` / `nickname` / `avatar` / `onlineStatus`.

Permission matrix (role: owner / admin / normal):

|Operation|Owner|Admin|Normal member|
|---|---|---|---|
|Disband group|✅|—|—|
|Update group info|✅|✅|—|
|Add / remove normal members|✅|✅|—|
|Remove admin|✅|—|—|
|Change others' roles (the owner role cannot be changed)|✅|✅|—|
|Change own nickname / avatar|✅|✅|✅|
|Change others' nickname / avatar|✅|✅|—|
|Send messages / create topics|✅|✅|✅|
|Delete topic (creator / owner / admin)|✅|✅|Creator only|
|Recall messages|✅|✅|Own sent ones only|
|Leave group|✅|✅|✅ (owner cannot leave; the group must be disbanded)|

**Write operation authentication**: all **write APIs** for groups / members / messages / topics uniformly perform identity resolution (same as WS / SSE) —
when a valid session token is carried (`Authorization: Bearer <token>` or `?token=`), the **token identity takes precedence** and overrides `ownerId` / `operatorId` / `userId` / `memberId` in the request body (a logged-in user cannot impersonate another identity);
when no token is carried, it falls back to the identity in the request body (compatible with legacy clients / demo mode), unless the server is configured with `Auth:RequireTokenOnRealTime=true` (always 401).
GET **read API authentication** (snapshot / members / history pagination, §5.4) requires login and validates that **the caller is a member of the group** (401 if not logged in, 403 if not a member, 404 if the group does not exist); `GET /ag-ui/member/{memberId}/groups` can only be queried by the user him/herself (403 on unauthorized access).

### 5.3 User Management APIs (Hub Extension)

The account system is a Hub extension (outside the native protocol): the `userId` (`user_xxx`) of a registered user is directly reused as the group member `memberId`, and the user can be added to groups to participate in group chat. Except for registration / login, all other user APIs require a session token (`Authorization: Bearer <token>` or the `?token=` query parameter); the user directory `GET /ag-ui/users` also requires login.

#### User Account Model (UserAccount)

|Field|Type|Required|Description|
|---|---|---|---|
|userId|string|Yes|Unique user identifier, named `user_xxx`, i.e., the memberId in the group member system|
|username|string|Yes|Login name (globally unique, case-insensitive, immutable, ≥3 characters)|
|nickname|string|No|Display nickname (used as the default group nickname when creating a group; if empty, the username is used)|
|avatar|string|No|Avatar URL (may be an upload address like `/ag-ui/files/...`)|
|personalMemoryEnabled|boolean|No|Personal memory switch (default false): when enabled, the user's historical posts are retrieved and injected as "personal memory" (takes effect only when the agent has personal memory enabled, see §2.1 semantic memory / §5.7)|
|createdAt|number|Yes|Registration timestamp (milliseconds)|
|updatedAt|number|No|Timestamp of the latest profile / password change (milliseconds)|

Passwords are stored as PBKDF2 salted hashes (≥6 characters); the server does not store plaintext.

#### API Overview

|API|Path|Auth|Description|
|---|---|---|---|
|Register|`POST /ag-ui/user/register`|No|Registration is also login; a token is issued directly|
|Login|`POST /ag-ui/user/login`|No|Username + password login; issues a token|
|Logout|`POST /ag-ui/user/logout`|Token|Revokes the current token|
|Current user|`GET /ag-ui/user/me`|Token|Returns the current account profile|
|Change password|`POST /ag-ui/user/password`|Token|Validates the old password before updating, and revokes all of the user's sessions (requires re-login)|
|Update profile|`PUT /ag-ui/user/profile`|Token|Changes nickname / avatar / `personalMemoryEnabled` (personal memory switch, default off), and synchronizes to the display in all groups the user belongs to (broadcast GROUP_MEMBER_UPDATED)|
|User directory|`GET /ag-ui/users`|Token|All registered users (frontend group-creation member picker, visible with login; the DTO does not include `isAdmin` to avoid leaking admin identities)|

#### Request / Response Examples

Register (registration is also login; the response includes the token):

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

Login:

```json
POST /ag-ui/user/login
{ "username": "zhangsan", "password": "123456" }
```

Change password / update profile (requires `Authorization: Bearer <token>`):

```json
POST /ag-ui/user/password
{ "oldPassword": "123456", "newPassword": "newpass123" }
```

```json
PUT /ag-ui/user/profile
{ "nickname": "张总", "avatar": "/ag-ui/files/att_xxx/avatar.png", "personalMemoryEnabled": true }
```

#### Session Token

- The token is Base64URL (no padding) of a 32-byte random number, with validity `Auth:SessionTtlHours` (default 168 hours); it is automatically sliding-renewed on each successful validation
- Transport: HTTP request header `Authorization: Bearer <token>`; because browser WebSocket cannot customize request headers, the `?token=` query parameter is also supported
- Real-time channel (WS / SSE): when a valid token is carried, the token identity takes precedence (the memberId parameter is ignored); when absent, it falls back to direct memberId connection (compatible with legacy clients), unless `Auth:RequireTokenOnRealTime=true` (always 401 without a token)
- Sessions are server in-process state: after a server restart, login is required again (sessions are not persisted in PostgreSQL mode either)

#### Relationship with the Group System

- A registered user can directly join a group as a member: pass the user's `userId` in `memberIds` when creating a group / adding members
- After an account's nickname / avatar changes, the server automatically updates the member display name and avatar in all groups the user belongs to, and broadcasts `GROUP_MEMBER_UPDATED` (the frontend refreshes immediately based on this)

#### AI Twin (Twin, Hub Extension)

A user can enable a "twin": the server aggregates the user's post records in **all public groups** (maximum 120 posts / 8000 characters), calls the model to generate a persona (Instructions),
and, as a **private agent** `twin_{userId}` (owned by the current user, manageable only by the creator), automatically joins all public groups the user belongs to, replying on behalf of the user according to the trigger mode the user configured.
Disabling deletes the twin (directory + exit from all groups). Private group content does not participate in persona generation, and a twin does not enter private groups.

|API|Path|Auth|Description|
|---|---|---|---|
|Twin status|`GET /ag-ui/twin`|Token|Current user's twin status (returns `{enabled:false}` if not enabled)|
|Enable twin|`POST /ag-ui/twin/enable`|Token|body contains `triggerMode` (default mentioned); generates the persona and joins all public groups|
|Change trigger|`POST /ag-ui/twin/trigger`|Token|body contains `triggerMode`; updates the persona's trigger mode and synchronizes registration in all public groups|
|Sync to public groups|`POST /ag-ui/twin/sync`|Token|Adds public groups newly created / joined after enabling (idempotent; does not re-register)|
|Disable twin|`POST /ag-ui/twin/disable`|Token|Deletes the twin agent and exits all groups|

**Online / offline mutual exclusion**: when the user is **online** (an active WS/SSE connection exists) the twin automatically pauses and does not respond, and the member list shows the user him/herself;
when the user is **offline** the twin responds on behalf, and the member list shows the twin (🪞 icon). In-group trigger evaluation is determined by the number of connections.

### 5.4 Group Query and History Pagination APIs

|API|Path|Description|
|---|---|---|
|My group list|`GET /ag-ui/member/{memberId}/groups`|All groups a member belongs to (including myRole / myNickname)|
|Group state snapshot|`GET /ag-ui/group/{groupId}`|Same as GROUP_STATE_SNAPSHOT (§4.7), including members / topics / latest messages|
|Member list|`GET /ag-ui/group/{groupId}/members`|All members (agent members include trigger fields)|
|Message history pagination|`GET /ag-ui/group/{groupId}/messages?before=&count=`|Forward pagination by cursor (virtual scrolling "load earlier messages")|

History pagination cursor semantics: `before` is the cursor message ID (that message is excluded); `count` defaults to 50, max 100; returns a time-ordered (old → new) `SnapshotMessage` list (recalled messages filtered); when `before` is absent, returns the most recent `count` messages; when the cursor is the first message or does not exist, returns an empty array. Within the same group, ordering is by `(timestamp, messageId)` lexicographic order.

### 5.5 Topic APIs

|API|Path|Description|
|---|---|---|
|Create topic|`POST /ag-ui/group/topic/create`|groupId、name（≤30 characters）、operatorId；when `sourceMessageId` is non-empty, that message becomes the starting point of the new topic (the message is migrated to the new topic)|
|Delete topic|`POST /ag-ui/group/topic/delete`|groupId、topicId、operatorId；group owner / admin or topic creator only；**the topic's chat records and the corresponding memory are deleted together**，broadcast to the whole group as `GROUP_TOPIC_DELETED`；the main topic `main` cannot be deleted|
|Clear topic records|`POST /ag-ui/group/topic/clear`|groupId、topicId（may be `main`）、operatorId；group owner / admin only；**the topic is retained**，the messages under that topic and the corresponding semantic memory are physically deleted together，broadcast to the whole group as `GROUP_TOPIC_CLEARED`，returns `{cleared, topicId, removedCount}`|
|Topic list|`GET /ag-ui/group/{groupId}/topics`|All topics (excluding the default `main`)|
|Related topics|`GET /ag-ui/group/{groupId}/topics/related?topicId=`|**Cross-topic thematic association (5.1)**: tokenizes topic messages into shared keywords (Jaccard) and computes association scores; returns the other topics most related to this topic's content `{topicId, name, score}` (threshold >0.02, Top 6), for "where else has this theme been discussed"；group member required, 403 for non-members|
|Topic message pagination|`GET /ag-ui/group/{groupId}/topics/{topicId}/messages?before=&count=`|Forward pagination by cursor within the topic, semantics same as §5.4；`topicId=main` denotes the main topic|

A successful topic creation returns the topic object and broadcasts `GROUP_TOPIC_CREATED` to the whole group; when a starting message is specified, `GROUP_MESSAGE_TOPIC_MOVED` is also broadcast (message ownership is migrated; the starting message cannot be a recalled message).

### 5.6 Attachment Upload and Download

|API|Path|Description|
|---|---|---|
|Upload attachment|`POST /ag-ui/upload`|multipart/form-data，field name `file`；max 9 files per request、single file ≤20 MB。Identity required（login token，or in demo mode `?memberId=`，consistent with WS/SSE auth）。Images / audio / text / office documents are auto-categorized into `kind`|
|Download / preview|`GET /ag-ui/files/{attachmentId}/{fileName}`|Locates by attachment ID；the file name is used only for display (the original name is kept on download)|

Example upload response (the response body is `{ "attachments": [...] }`):

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

`kind` determination (the server categorizes by Content-Type and file extension): `image` (image types） / `text` (plain text such as txt、md、json、source code） / `document` (office documents such as docx / xlsx / pptx / pdf） / `binary` (others). Both `text` and `document` types can have their text extracted and injected into the agent context (§2.5). When sending a message, place the returned attachment object array in the `attachments` field (§5.1).

### 5.7 Agent Management APIs (Hub Extension)

The Agent Definition directory provides runtime management: it is seeded from the server configuration by default, and can be dynamically created / edited / deleted; the definition includes persona, trigger mode, model, and AG-UI bridge configuration (§6). All APIs except directory query require a login token.

|API|Path|Description|
|---|---|---|
|Directory list|`GET /ag-ui/agents`|Agent definitions (public read-only)；**private agents are visible only to their creator**（invisible to anonymous users / others）|
|Create agent|`POST /ag-ui/agents`|body below；when agentId is left empty, `agent_xxx` is auto-generated；records the creator ownerId|
|Edit agent|`PUT /ag-ui/agents/{agentId}`|Updates the definition；synchronizes trigger rules and group member profiles of non-overridden groups；**private agents can only be edited by their creator**（otherwise 403）|
|Delete agent|`DELETE /ag-ui/agents/{agentId}`|Removes the definition、all trigger rules、and exits all groups；**private agents can only be deleted by their creator**（otherwise 403）|
|Register in-group trigger|`POST /ag-ui/agents/register`|Registers trigger rules for a specified group（triggerMode / keywords / override）|

Agent definition fields:

|Field|Type|Description|
|---|---|---|
|agentId|string|Unique identifier（`agent_xxx`）|
|nickname|string|Display nickname（synced to group members）|
|description / instructions|string|One-line summary / system prompt（defines persona and reply style）|
|avatar|string?|Avatar URL|
|triggerMode|enum|Default trigger mode：`mentioned` / `allMessages` / `keyword` / `contextual`|
|keywords|string[]|Keywords when triggerMode=keyword|
|model|string?|Model specified separately（defaults to server default when absent）|
|schedule|string?|Scheduled reporting（5-field cron，optional）：independent of message trigger；scheduled and message triggers can take effect simultaneously|
|enableWorkTools|boolean|Whether to enable the file / command tools of a work-type agent（`list_dir` / `read_file` / `write_file` / `shell`），default false；commands and write operations are restricted to the `data/workspaces/<agentId>/` workspace；write operations examples require approval，and the global `Agents:WorkToolsEnabled` must be turned on|
|bridgeEndpoint|string?|AG-UI bridge endpoint `http(s)://` or `ws(s)://`（see §6.3 external experts）|
|bridgeMode / bridgeToken|string?|Bridge dialect（standard / hub）and auth token（when editing，an empty token means keeping the original value，not echoed back）|
|personalMemoryEnabled|boolean|Whether personal memory is enabled（default false）：when enabled，the replies retrieve and inject the triggering user's own historical posts（the triggering user must also have it enabled）|
|skills|object[]?|Skills（inter-agent invocation）：`[{skillId, description, targetAgentId}]`；skillId is given to the model as the tool name（only letters/digits/underscores/hyphens，**auto-generated `skill_<targetID>` when empty，appending `_2/_3` on conflict**），targetAgentId is a registered agent（including AG-UI bridge roles）；target agents are expanded one level only and cannot point to themselves|
|knowledgeBaseIds|string[]|Bound knowledge base IDs（see §5.8）：before replying，relevant document fragments are retrieved from these knowledge bases and injected into the context（RAG knowledge base）|
|relayToAgentId|string?|**Role handoff (1.2)**：when non-empty，the whole turn of this agent is delegated to the relay agent（`agent_xxx`），and to external callers it still appears as the original role；when the relay does not exist / forms a relay loop，it falls back to local processing|
|requireApprovalToolNames|string[]?|**Agent-level approval policy (4.1)**：when non-empty，this list decides which tools require approval；otherwise it falls back to the global `Agents:RequireApprovalToolNames`；`approveAll` can approve all subsequent pending tools of the current run at once|
|isPrivate|boolean|Whether it is a private agent（default false）：only the creator（ownerId）can pull it into a group / edit / delete，and the directory hides it from other users|
|ownerId|string?|Creator userId（appsettings seed null = system-level）|

**Export / import**: the "📤 Export All" / per-row "Export" of agent management exports the configuration as JSON (`{version: 1, agents:[…]}`，fields as above，sensitive tokens and ownerId are not exported)，and the frontend "📥 Import" reads the JSON and calls `POST /ag-ui/agents` one by one to create them（owned by the current logged-in user；on agentId conflict the ID is auto-renamed without overwriting）。Additionally there is a **full data package** API（administrator）：`GET /ag-ui/export` exports accounts（including password hash / salt）+ agent definitions and trigger rules + groups / topics / messages + attachments as a zip；`POST /ag-ui/import/preview` uploads a zip and returns an existence check of accounts / agents and a group manifest；`POST /ag-ui/import` executes per `selectedGroupIds`（accounts are auto-completed by username，agents by agentId；message senders / mentions / visible lists are rewritten according to the account mapping；attachments are restored）。

### 5.8 Knowledge Base Management APIs (Hub Extension, RAG Knowledge Documents)

Agents can bind knowledge bases (`AgentDefinition.knowledgeBaseIds`); before replying, relevant document fragments are retrieved from the bound list and injected into the context.
Knowledge bases are created by users who upload documents (txt/md/json/csv and docx/xlsx/pptx/pdf, reusing attachment text extraction); after slicing, documents are vectorized and stored in the semantic memory vector table (GroupId convention `kb:{KbId}`). Slicing defaults to an 800-char window with 100 chars of overlap, configurable via `Agents:Memory:KnowledgeChunkSize` / `KnowledgeChunkOverlap`; cuts are placed at line breaks or sentence-ending punctuation (avoiding splitting mid-sentence), and adjacent slices share an overlapping tail to reduce boundary information loss. It depends on vector storage and embedding (pgvector / sqlite-vec + llama / http); when unavailable, document ingestion returns 400 with a clear error.

**Document ingestion is asynchronous**: `POST /ag-ui/kb/{kbId}/documents` immediately returns a document record with `status=processing` (text extraction / slicing / vectorization run in the background, avoiding long blocking of the upload request); the frontend polls the knowledge base list to observe status changes — `processing`（in progress）→ `ready`（ingested，chunkCount>0）or `error`（failed，error field is the reason）。A document being processed can be removed at any time（background discards the not-yet-written vectors）；documents whose processing was interrupted by a server restart revert to `error`。

|API|Path|Description|
|---|---|---|
|Create knowledge base|`POST /ag-ui/kb`|body：`{name, description?}`；login required；returns kbId（`kb_xxx`）|
|Knowledge base list|`GET /ag-ui/kb`|System-level（ownerId=null）+ those created by the current user；document manifest includes `{docId, fileName, chunkCount, status, error, addedAtMs}`|
|Delete knowledge base|`DELETE /ag-ui/kb/{kbId}`|Creator only（403 on unauthorized）；clears the whole document vectors together|
|Add document|`POST /ag-ui/kb/{kbId}/documents`|body：`{attachmentId}`（upload first via `POST /ag-ui/upload`）；immediately returns `{docId, fileName, chunkCount, status, error}`（status=processing），with slicing and vectorization completing in the background|
|Remove document|`DELETE /ag-ui/kb/{kbId}/documents/{docId}`|Deletes the document's slice vectors（a document being processed can be removed directly）|

System-level knowledge bases（ownerId=null）are visible but read-only to all users（modification not exposed）。

Example in-group registration request:

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

`override=true` means explicitly overriding the role's default trigger mode within the group（role edits no longer overwrite this group）；`false` means following the role default（role edits sync automatically）。

### 5.9 Memory, Task, and Operations Management APIs (Hub Extension)

The following are all Hub extension management-type APIs, expanding the capability surface of the group chat protocol (memory governance, recurring tasks, external expert marketplace, audit and operations observability).

**Memory governance and timeline**（login required；only the memory of **groups one belongs to** can be governed）：

|API|Path|Description|
|---|---|---|
|Memory overview|`GET /ag-ui/memory/groups`|Memory statistics for each group the user belongs to（entry counts / distribution by level）|
|Memory list|`GET /ag-ui/memory`|Memory entry visualization（filtered by group `groupId` / sender `senderId` / keyword `keyword`，paginated with `limit`/`offset`）|
|Memory timeline|`GET /ag-ui/memory/timeline`|**2.2 Timeline replay**：replays the old → new evolution of memory by group / topic / keyword，for reviewing "how a conclusion evolved"|
|Adjust memory level|`POST /ag-ui/memory/{messageId}/importance`|Sets a single memory level `0 normal / 1 important / 2 critical`（at equal similarity，higher levels are retrieved and injected first）|
|Delete memory|`DELETE /ag-ui/memory/{messageId}`|Physically deletes a single memory|
|Manual forget|`POST /ag-ui/memory/forget`|Sets expiry by group（or all），optionally keeping the most recent N hours；the corresponding memory stops participating in retrieval and is cleaned up periodically in the background|
|Consolidate memory into knowledge base|`POST /ag-ui/memory/consolidate`|**1.3**：aggregates the "critical"-level memory of a group and writes it into a specified knowledge base（automatically / semi-automatically distills conclusions into documents），body contains `groupId` / `kbId`，etc.；reuses knowledge base slicing and vectorization |
|Memory export|`GET /ag-ui/memory/export?groupId=&since=&limit=&offset=`|**2.3 Cross-instance sync**：exports "memory as a data package"（text metadata：messageId/group/topic/sender/content/time/level/expiry），supports incremental by group and time lower bound `since`；non-admins can export only the groups they belong to，admins can export anything |
|Memory import|`POST /ag-ui/memory/import`|**2.3**：imports a memory array（or `{items:[...]}`），recomputes vectors per item in the target instance using its own embedding model，deduplicates by messageId；returns `{imported, provided}` |

**Recurring scheduled tasks (1.4，on-duty agents)**：

|API|Path|Description|
|---|---|---|
|Task list|`GET /ag-ui/scheduled-tasks`|Agent tasks of the groups I belong to；admins see all|
|Create task|`POST /ag-ui/scheduled-tasks`|`{agentId, name, cron, prompt?, groupId?, enabled?}`——at the scheduled time triggers the agent to generate a report / verification / reminder|
|Update task|`PUT /ag-ui/scheduled-tasks/{taskId}`|Changes name / cron / report instruction / target group / enabled status|
|Delete task|`DELETE /ag-ui/scheduled-tasks/{taskId}`|Deletes the task；create / edit must be a member of the relevant group（admins unrestricted）|

**Agent / skill marketplace (3.3)**：

|API|Path|Description|
|---|---|---|
|Marketplace catalog|`GET /ag-ui/marketplace`|Optional role / skill pack catalog（industry role packs，reusing the `agents-starter` packaging structure，login required）|
|One-click import|`POST /ag-ui/marketplace/import/{packId}`|Imports a marketplace pack as an agent of the current user（on agentId conflict the ID is auto-renamed without overwriting）|

**Bridge health and capabilities (3.1 / 3.2，admins only)**：

|API|Path|Description|
|---|---|---|
|Bridge health|`GET /ag-ui/admin/bridge-health?refresh=`|Real-time / cached connectivity status of configured external AG-UI endpoints（supports auto-reconnect backoff、offline re-transmission，`BridgeCircuitBreaker`）|
|Bridge capability negotiation|`GET /ag-ui/admin/bridge-capabilities?refresh=`|External endpoint capabilities（supported tools / attachments / approval types，Capability Discovery），reducing manual configuration|

**Audit and observability (4.3 / 6.1 / 6.3，admins only)**：

|API|Path|Description|
|---|---|---|
|Operation audit log|`GET /ag-ui/admin/audit?limit=`|Record of critical / sensitive operations（who / when / which tool was approved / import-export / resets，etc.），`limit` ≤200，exportable for compliance|
|Runtime status|`GET /ag-ui/admin/status`|Connection / group / user / agent / message counts，memory，threads，and other process information|
|Model usage|`GET /ag-ui/admin/usage?days=`|Model call volume summarized by day for the most recent N days + quota configuration|
|Runtime metrics|`GET /ag-ui/admin/metrics`|Agent invocation / bridge / memory hit / output length，and other in-process metric counters（6.1）|
|Configuration snapshot|`GET /ag-ui/admin/config`|Central read-only display of key appsettings / .env operations parameters（model，storage，permissions，allowed origins，etc.），for governance and troubleshooting（6.3） |
|Configuration write|`POST /ag-ui/admin/config`|**6.3 Configuration governance**：admins only；writes online and persists runtime-safely-mutable knobs（session TTL，max group messages / max group members / per-message characters，message retention days，required token，tool switch / work-type tools / thinking mode / daily token quota，approval list，iframe embed origins），invalid values return 400；persisted to `configGovernance`，auto-applied on restart |
|Configuration override read-back|`GET /ag-ui/admin/config/governance`|Returns the currently saved governance override values（unspecified ones fall back to configuration defaults） |

**Multi-device sessions and two-factor verification (4.4，token required)**：

|API|Path|Description|
|---|---|---|
|TOTP status|`GET /ag-ui/user/totp`|Whether two-factor verification is enabled |
|Enroll secret|`POST /ag-ui/user/totp/enroll`|Generates a TOTP secret（for binding）|
|Confirm enable|`POST /ag-ui/user/totp/confirm`|Enables TOTP after validating the dynamic code（two-factor verification required at login）|
|Disable TOTP|`POST /ag-ui/user/totp/disable`|Disables two-factor verification|
|Session list|`GET /ag-ui/user/sessions`|All logged-in device sessions of the current account|
|Revoke session|`POST /ag-ui/user/sessions/revoke`|Revokes the specified session |
|Revoke others|`POST /ag-ui/user/sessions/revoke-others`|Revokes all sessions except this one|

**Fine-grained RBAC (4.2，channel-level)**：group members carry `GroupMemberPermissions` via `extra["rbac"]`（`canInvokeAgents` who can @ agents / `canApprove` who can approve human-machine interactions / `canManageKnowledge` who can manage knowledge bases）；when a field is explicitly set to false it is restricted，null follows the default of role / admin allowing；`IsAdmin` / `AdminUserIds` determine system-level admins，who can access the §5.9 admin APIs。

**White-label branding and embedding (6.4)**：

|API|Path|Description|
|---|---|---|
|Query branding config|`GET /ag-ui/settings/branding`|Public：returns `{configured, appName, logoUrl, primaryColor, forceDark, tagline}`，for rendering the login / embedded pages |
|Save branding config|`POST /ag-ui/settings/branding`|System admin only：configures app name（≤40）/ Logo（in-site path or https / data:image）/ brand primary color（6-digit hex）/ force dark / tagline；persisted to an extension area；invalid color / dangerous Logo return 400 |
|iframe embedding|—|`GroupChatOptions.AllowedFrameOrigins` configures allowed embed origins（default empty = disallowed）；CSP `frame-ancestors` and `X-Frame-Options` are relaxed accordingly |
|External API key|—|`Auth:ApiKeys`：`Authorization: Bearer <apiKey>` authenticates without login to call all HTTP APIs with the bound account identity（inheriting its group membership / permissions / admin flags）|

### 5.8 WebSocket Upstream Events

On a WebSocket connection, the following events can be sent upstream directly (equivalent to the corresponding HTTP APIs), with fields identical to the HTTP request body:

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

|Upstream type|Equivalent HTTP|Description|
|---|---|---|
|GROUP_MESSAGE_SEND|POST /ag-ui/group/message/send|Send a group message（sender identity is the connection identity，ignoring `userId` in the request）|
|GROUP_MESSAGE_RECALL|POST /ag-ui/group/message/recall|Recall a message|
|AGENT_INTERACTION_RESOLVE|POST /ag-ui/group/interaction/resolve|Human-machine interaction decision（trigger only；decider identity is the connection identity）|
|GROUP_TYPING|POST /ag-ui/group/message/typing|Typing status|
|GROUP_MESSAGE_READ|POST /ag-ui/group/message/read|Read receipt|
|GROUP_SUBSCRIBE / GROUP_UNSUBSCRIBE|POST /ag-ui/group/subscribe|Subscribe / unsubscribe group（see §4.6）|

The server authenticates WS upstream by connection identity（token takes precedence over memberId，see §5.3 session token）；identity fields in the request are always overridden by the connection identity resolved by the server（and `userId` of GROUP_MESSAGE_SEND，`operatorId` of GROUP_MESSAGE_RECALL，`memberId` of GROUP_TYPING / GROUP_MESSAGE_READ），to prevent forgery。

## 6. Agent Group Chat Trigger Rules (Protocol Recommendation)

### 6.1 Trigger Modes

1. **Mention trigger（mentioned）**：when a message's `mentions` contains the corresponding `agentId`（or `mentionAll`），the agent starts processing and generates a `runId`

2. **Full monitoring（allMessages）**：an agent configured for full monitoring receives all group messages and decides for itself whether to respond

3. **Keyword trigger（keyword）**：the server matches message content against the configured keywords（case-insensitive）；on a hit，the corresponding agent is awakened

4. **Contextual trigger（contextual）**：the server decides based on the context of recent messages（`Agents:ContextMaxMessages` messages，default 10）；on a hit，the corresponding agent is awakened

5. Agent responses must carry `senderId` and `senderType`，based on which the frontend renders the identity；a message sent by an agent does not trigger itself
6. Agent replies **do not echo the triggering message's `mentions` / `mentionAll`**（mentions are used only for triggering，to avoid @ being echoed into the body）

### 6.2 In-Group Trigger Mode Override

An agent's effective trigger mode within a group can be independent of the role default:

- When creating a group / adding members，trigger rules can be registered per group（§5.7 `POST /ag-ui/agents/register`）
- `override=true`：explicitly overrides the role default within the group（role edits do not overwrite the group setting）
- `override=false`：follows the role default（role edits sync to the group automatically）
- In the snapshot and member list，agent members carry the three fields `triggerMode` / `keywords` / `isTriggerOverridden` to echo back the currently effective value（§2.2 / §4.7）

### 6.3 AG-UI Bridge (External Expert)

Once an agent definition configures `bridgeEndpoint`（§5.7），the role **does not use the local large model**，and instead connects to an external AG-UI service via the AG-UI protocol (standard AG-UI or this project's group chat extension，with `bridgeMode` set to `standard` / `hub`)：triggering messages are forwarded to the external service，and its streaming replies are fed back into the group chat via the `TEXT_MESSAGE_*` events；`bridgeToken` is used for connection authentication（when editing，leaving it empty keeps the original value，not echoed back）。

### 6.4 AI Twin

After a user enables the twin（§5.3），a **private agent** `twin_{userId}` is generated，with the following trigger rules:

- Created by the owning user，`isPrivate=true`，managed only by the creator（pull into group / edit / delete）
- Automatically joins **all public groups** that the owning user belongs to（does not join private groups），with the trigger mode set by the user and modifiable at any time（`POST /ag-ui/twin/trigger` syncs each group）
- When a public group adds / removes a user member，the server automatically follows by joining / leaving（`ITwinAgentSync` hook）
- **Offline-only trigger**：when the owning user has an active connection，the twin pauses and does not respond（online/offline mutual exclusion，with the member list correspondingly switching its display）
- On disable / online switching，the trigger rules and member identity of each group are automatically cleaned up / restored along the lifecycle

## 7. Error Code Extensions

On top of the native `RUN_ERROR` event，group-chat-specific error types are added:

|Error identifier|Description|
|---|---|
|GROUP\_NOT\_FOUND|The group does not exist|
|GROUP\_PERMISSION\_DENIED|No permission for the group operation|
|GROUP\_MEMBER\_NOT\_EXIST|The target member is not in the group|
|GROUP\_FULL|The number of group members has reached the limit|
|GROUP\_MESSAGE\_NOT\_FOUND|The message does not exist or has been recalled|
|GROUP\_SUBSCRIBE\_FAILED|Group subscription failed|

Hub extension error codes（user / agent management）：

|Error identifier|Description|
|---|---|
|BAD\_REQUEST|Malformed request（unparseable / missing fields）|
|USER\_NOT\_FOUND|The user does not exist|
|USER\_EXISTS|The username is already registered（registration conflict）|
|USER\_BAD\_CREDENTIALS|Wrong username or password（login failed）|
|USER\_PASSWORD\_INVALID|The old password is incorrect（when changing the password）|
|USER\_UNAUTHORIZED|Not logged in or the token is invalid / expired|
|AGENT_NOT_FOUND|The agent does not exist（not declared in the directory）|
|AGENT_EXISTS|The agent ID is already taken|
|AGENT_PERMISSION_DENIED|Private agents can be operated only by their creator（pull into group / edit / delete，returns 403）|

The error response body is uniformly `{"code": "...", "message": "..."}`（HTTP status codes are per the implementation of each API：401 / 403 / 404 / 409）。

## 8. Backward Compatibility Notes

1. When a native AG-UI client receives group chat events，it can ignore all group-related fields and normally parse the text, tool calls, and other core content

2. In group chat mode, `threadId` and `groupId` correspond one-to-one，so one-on-one context logic can be reused seamlessly

3. A server that has not implemented group chat functionality can simply ignore the group fields in upstream requests and degrade to one-on-one chat handling

4. All newly added events are optional to implement，and can be enabled as needed to satisfy different levels of group chat requirements

5. Topics / attachments / agent management / user management are all Hub extensions：servers not implementing them and legacy clients can ignore the related fields，events，and APIs without affecting basic group chat

6. `GROUP_CONNECTED`（handshake）、`GROUP_TOPIC_CREATED`、`GROUP_TOPIC_DELETED`、`GROUP_MESSAGE_TOPIC_MOVED` are optional events，which legacy clients ignore as unknown events

> (Note: some content may have been generated by AI)
