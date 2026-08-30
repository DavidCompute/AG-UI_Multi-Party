# AG-UI Group Chat Extension Platform — Product Roadmap

**English** | [简体中文](ROADMAP.md)

> ✅ mark = implemented; 🟡 = partially implemented. Currently completed: **1.1–1.4 / 2.1–2.4 / 3.1–3.3 / 4.1–4.4 / 5.1–5.4 / 6.1–6.4**;
> all roadmap items are now implemented 🎉

> This document plans the project's **future feature growth and refinement directions** and provides priority recommendations to inform scheduling and resource decisions.
> Design principle: only list capabilities that are **not yet implemented / not yet mature**; clearly distinguish them from existing capabilities to avoid duplication.
> The "Target Module" column lists the main code locations involved in implementation, making it easier to split tasks.

**Legend**: ★ priority (★★★ highest); each item follows the structure "Current State → Goal → Target Module".

---

## I. Multi-Agent Collaboration Layer

Existing foundation: trigger rules (mention / full / keyword / context), in-group coverage, skills (inter-agent invocation), AG-UI bridge, AI twins, knowledge base.

### 1.1 Agent Orchestration / Multi-Step Workflow (★★★ strongest differentiator) ✅ Implemented
- **Current state**: skills are "single-layer sub-agent invocations"—one run returns a single reply, with no multi-step collaboration.
- **Goal**: planning → decomposition into subtasks → sub-agents executed in parallel / sequentially → aggregation → final reply, forming a plan/agent loop similar to a coding assistant. A complex requirement can be completed collaboratively by three assistants handling code + documentation + tests.
- **Target module**: `src/AguiGroupChat.Agents/` (session and skill invocation), `AgentCatalog` (new event: subtask status).
- **Enhancement (implemented)**: deterministic orchestration plan (`CoordinatorPlanning`) supports "question → build a plan → activate in sequence"; multiple <b>client-execution skills</b> inside a plan merge into one "run all locally" card after a single confirmation; the synthesis stage <b>recursively gathers more</b> (invoking more skills / direct reports when the info is insufficient) until the answer is complete; skill-only digital employees also enter plan orchestration.

### 1.2 Inter-Role Message Passing / Handoff (★★☆) ✅ Implemented (full-round-role handoff)
- **Current state**: an agent uses another agent as a "tool" in a one-off call, with no bidirectional collaboration semantics.
- **Goal**: agents can directly "send a message to / request a relayed reply from another agent", supporting collaborative handoff rather than one-way invocation.
- **Target module**: `AgentGateway.cs`, `AgentDefinition` (collaboration fields), new collaboration events added to the event catalog.

### 1.3 Auto-Sinking of Important Memories into the Knowledge Base (★★☆ enhances "better the more it's used") ✅ Implemented
- **Current state**: knowledge base entries must be created and uploaded manually.
- **Goal**: in-group conclusions that are repeatedly referenced or marked as "key" are automatically / semi-automatically written to the knowledge base after model aggregation, letting knowledge accumulation happen automatically during conversation.
- **Target module**: `KnowledgeBaseCatalog.cs`, `MessageMemory` (importance tiering already exists as the sink trigger source), `TwinService` (reuses aggregation logic).

### 1.4 Scheduled / Cron Task Orchestration (★★☆) ✅ Implemented
- **Current state**: `Agents:Schedule` (5-field cron) already exists, but only for one-time scheduled reporting.
- **Goal**: recurring task orchestration (daily reports / scheduled checks / deadline reminders) + a task-based UI, forming an "on-duty agent".
- **Target module**: `Schedule` extension, `TaskApi.cs` / `Tasks` (existing task table `agui_tasks`), frontend task panel.

---

## II. Memory & Knowledge Layer

Existing foundation: RAG (pgvector / sqlite-vec), personal memory, memory tiering / auto-forgetting / visualization, knowledge base (asynchronous ingestion).

### 2.1 Hybrid Retrieval (Sparse BM25 + Dense Vectors) (★★☆) ✅ Implemented
- **Current state**: single embedding model.
- **Goal**: fuse BM25 sparse retrieval with dense vectors to improve recall in Chinese / code scenarios; support switching embedding providers on demand.
- **Target module**: `MessageMemory`, `IMessageMemory` (retrieval aggregation), `SqliteVecMessageMemoryStore.cs` / `PgMessageMemoryStore.cs`.

### 2.2 Memory Timeline / Versioning (★★☆) ✅ Implemented (timeline playback)
- **Current state**: memory only records "latest" plus expiry deletion, with no evolution playback.
- **Goal**: replay "how a conclusion on a topic evolved" along a timeline, serving retrospectives and auditing.
- **Target module**: `MessageMemory` (temporal dimension), `MemoryMaintenanceService`.

### 2.3 Cross-Instance Memory Sync (★★☆ bridges desktop/Web silos) ✅ Implemented (memory-as-packet / incremental sync)
- **Current state**: the desktop and Web builds each keep their own local memory, unaware of each other.
- **Goal**: export / incrementally sync memory to the central Hub, or migrate portably via "memory-as-packet", reusing the existing export/import scaffolding.
- **Target module**: `ExportImportApi.cs`, `IMessageMemory`, desktop `DesktopApp` sync hooks.
- **Implemented**:
  - `IMessageMemory` now supports export / import (`ExportMemories` / `CountMemories` / `ImportMemoriesAsync`): <b>memory-as-packet</b>—only text metadata is exported (messageId / group / topic / sender / content / timestamp / tier / expiry), with vectors recomputed on the target instance using its own embedding model (supports different vector dimensions across instances).
  - HTTP: `GET /ag-ui/memory/export?groupId=&since=&limit=&offset=` (per-group / time-floor incremental export; members can only see their own group; admins can export anything); `POST /ag-ui/memory/import` (bulk import, deduped by messageId).
  - Includes 3 cross-instance export/import tests (round-trip migration, idempotent dedup, sinceMs incremental filtering).

### 2.4 Knowledge Base Refined Permissions & Provenance (★★☆) ✅ Implemented (group-level sharing + provenance in place)
- **Current state**: the knowledge base is "owner-only + system-level", and answers don't cite sources.
- **Goal**: a collective knowledge base shared at the group / member level; answers cite referenced source documents for traceability.
- **Target module**: `KnowledgeBaseApi.cs`, `KnowledgeBaseCatalog` (retrieval returns docId references).

---

## III. AG-UI Bridging & Ecosystem Openness

Existing foundation: standard / hub dialects, HTTP / WS dual transport, approval interruption callbacks, attachment refeeding.

### 3.1 Bridge Health & Auto-Reconnect (★★☆) ✅ Implemented (health probing + capability negotiation + auto-reconnect backoff)
- **Current state**: on bridge failure, only `RUN_ERROR` is broadcast and refed.
- **Goal**: endpoint health probing, auto-reconnect with backoff, offline resend, making "external experts" more reliable.
- **Target module**: `AgentGateway` (bridge dispatch), `AguiBridgeClient` / `AguiBridgeHttpStandardClient.cs`.

### 3.2 Bridge Capability Negotiation / Capability Discovery (★★☆) ✅ Implemented
- **Current state**: dialect / transport relies on static configuration.
- **Goal**: capability discovery based on the AG-UI protocol (which tools / attachments / approval types are supported), reducing manual configuration.
- **Target module**: `AgentGateway` bridge chain, `AguiBridge*` clients.

### 3.3 Agent / Skill Marketplace (★★☆) ✅ Implemented (built-in catalog with one-click import)
- **Current state**: roles / skills are imported manually via JSON files (`tools/agents-starter.json`).
- **Goal**: a built-in "industry roles / skills / knowledge base template" download marketplace for one-click distribution.
- **Target module**: `AgentApi`, frontend "Agent Management", reusing the starter JSON packaging structure.

---

## IV. Human-Machine Collaboration & Governance (★★★ hard gate for enterprise adoption)

### 4.1 Differentiated Approval Policies (★★★) ✅ Implemented
- **Current state**: `Agents:RequireApprovalToolNames` is a global list (by tool name), too coarse-grained.
- **Goal**: differentiated approval policies by agent / by group / by amount or sensitivity thresholds; `approveAll` already exists and can be refined for finer-grained control.
- **Target module**: `ApprovalRequiredAIFunction` wrapper logic, `AgentOptions`, HITL decision flow (`AgentGateway` / `HttpGroupApi` interaction/resolve).

### 4.2 Fine-Grained RBAC (★★★) ✅ Implemented
- **Current state**: permissions are owner / admin / normal plus admin flags (`IsAdmin` / `AdminUserIds`).
- **Goal**: channel-level permissions—who can @ an agent, who can approve, who can manage the knowledge base.
- **Target module**: `AuthService`, `AuthOptions`, `GroupMember` (role extension), auth checks in the various APIs.

### 4.3 Operation Audit Logs (★★★) ✅ Implemented
- **Current state**: HITL cards leave traces, but there are no global audit logs to export.
- **Goal**: record "who / when / which tool was approved / export-import / reset" etc., exportable, satisfying classified-data and compliance requirements.
- **Target module**: event broadcast channel, `agui_usage` table extension, `AdminApi` / admin UI.

### 4.4 Session Security Enhancements (★★☆) ✅ Implemented (multi-device sessions + TOTP)
- **Current state**: login sessions are in-process, partially persisted; no multi-device management.
- **Goal**: multi-device session viewing / revocation, optional second-factor login (TOTP), UI-based configuration to tighten `AllowedOrigins` / CSWSH.
- **Target module**: `AuthService`, `AuthOptions`, `UserApi` / `AdminApi`, frontend account menu.

---

## V. Topics, Groups & Frontend Experience

### 5.1 Cross-Topic Theme Association (★☆☆) ✅ Implemented
- **Current state**: topics are independent of one another.
- **Goal**: a relevance matrix of "which other topics this theme has been discussed in", helping multi-person collaboration not to miss context.
- **Target module**: `GroupTopic`, `HttpGroupApi` topic endpoints, frontend topic bar.
- **Implemented**: added `GET /ag-ui/group/{groupId}/topics/related?topicId=…`, computing association scores via shared keywords (Jaccard) across topic messages' tokenization (>0.02, Top6), visible only to group members (403 for non-members).

### 5.2 Rich Media Messages (★☆☆) ✅ Implemented (multi-image selection + voice + canvas annotation)
- **Current state**: attachment support for office documents is complete.
- **Goal**: voice messages, multi-image upload, canvas annotation, closer to instant-messaging conventions.
- **Target module**: `AttachmentInfo`, `/ag-ui/upload`, frontend rendering.
- **Implemented**:
  - Backend attachments added an `audio` category (`audio/mpeg`/`wav`/`ogg`/`webm` etc.), passed by the upload whitelist, only carrying metadata for frontend playback without being injected into the model's text context; the download endpoint returns the correct audio MIME (with range support).
  - Input supports multi-image selection / drag-and-drop; added **voice messages** (MediaRecorder recording → audio attachment) and **canvas annotation** (canvas drawing → PNG image attachment); images render in a tiled grid in the message, thumbnails show in the input area, and audio shows as an inline `<audio>` playback bar.

### 5.3 Frontend Performance & Accessibility (★☆☆) ✅ Implemented (virtual scrolling in place + ARIA enhancements)
- **Current state**: incremental/streaming partial rendering plus a cap of the most recent 300 messages.
- **Goal**: virtual scrolling + lazy message loading for very large groups; ARIA accessibility improvements.
- **Target module**: `wwwroot/app.js`.
- **Implemented**:
  - Virtual scrolling and lazy loading are mature: windowed message rendering (`virtualRender` + upper/lower placeholder spacers), scroll anchoring based on measured row heights, full rendering for small tables of ≤200 rows (`PLAIN_LIMIT`), cursor pagination for "load earlier messages", and per-group in-memory capping at 1200 messages.
  - **ARIA accessibility enhancements**: message container `role="log" aria-live="polite" aria-relevant="additions"`, each message `role="listitem"` + sender/time/content summary labels; the composer icon buttons, input box, and canvas drawing area get `aria-label`; the canvas modal is `role="dialog" aria-modal="true"` + focus-on-open + Esc to close + focus return on close; the notification panel is `role="region"`.

### 5.4 In-App Notification Center (★★☆) ✅ Implemented
- **Current state**: no aggregated notifications.
- **Goal**: WS disconnect/reconnect, being @mentioned, pending approvals, scheduled task results → in-app notifications + system notifications.
- **Target module**: notification event pipeline, frontend notification center.
- **Implemented**: top-bar 🔔 notification button + dropdown panel (with unread badge); aggregates four notification types—**@mentions**, **pending approvals / input requests**, **WS disconnect / reconnect**, **new messages in non-current views** (including agent broadcast messages from scheduled tasks); clicking a notification jumps to the source group, with unread highlighting, clear-all, Esc / outside-click to close; system desktop notifications are sent when the page is hidden (reusing `Notification`); cleared on logout.

---

## VI. Observability & Engineering

### 6.1 Structured Runtime Metrics / OpenTelemetry (★☆☆) ✅ Implemented (in-process metrics)
- **Current state**: `/ag-ui/health` only reports connection / group counts; an `agui_usage` table already exists.
- **Goal**: metrics and dashboards for model call volume, token consumption, latency, bridge failure rate, and memory hit rate.
- **Target module**: `/ag-ui/health`, `agui_usage` write points, `AgentGateway` instrumentation.

### 6.2 Multi-Replica Horizontal Scaling for the Web Build (★☆☆) ✅ Implemented (Redis shared storage)
- **Current state**: single-process Kestrel.
- **Goal**: multiple replicas in Docker scenarios with Redis-shared sessions / storage (README already reserves `IGroupStore` / `IUserStore` for replacement with Redis / DB).
- **Implementation**: added `Storage:Provider=redis`. `RedisContext` (connection reuse and key conventions) + `RedisGroupStore` / `RedisUserStore` / `RedisTaskStore` / `RedisUsageStore` / `RedisAgentRegistryStore` / `RedisSectionStore`; login sessions are shared across replicas via the `ISessionStore` abstraction (`RedisSessionStore`)—log in on one replica and any other replica can validate. Replicas reading/writing the same `agui:*` keys stay consistent.
- **Target module**: `src/AguiGroupChat.Hub/Persistence/Redis/`, `src/AguiGroupChat.Hub/Users/ISessionStore.cs`, the redis branch in `HubApp.ConfigureServices`.

### 6.3 Configuration Governance UI (★☆☆) ✅ Implemented
- **Current state**: operational parameters live in `.env` / appsettings.
- **Goal**: an admin UI to uniformly view / adjust / persist (`AllowedOrigins`, `LinkProxy`, `WorkToolsEnabled`, database connection).
- **Target module**: `AdminApi`, `SystemApi` (`settings/model` already exists and can be extended), frontend admin panel.
- **Implemented**:
  - Backend: `GET /ag-ui/admin/config` (existing read-only snapshot, covering storage / model / memory items that require a restart) + `POST /ag-ui/admin/config` (writes runtime-safe adjustable knobs: session validity, group message limits / group member limits / per-message character limits, message retention days, forced token, tool toggles / work-type tools / thinking mode / daily token quota, approval list, iframe embed origins); invalid values return 400, persisted to the "configGovernance" extension area, applied as an override automatically on restart; `GET /ag-ui/admin/config/governance` reads back the current override values.
  - Frontend: the admin console gained a "Configuration Governance" tab (parameter grid + toggles + approval / embed origins), with load-and-backfill (unset items are tri-state, falling back to defaults), save with immediate effect, and refresh.
  - Includes 2 integration tests (admin update persists + invalid value 400 / non-admin 403).

### 6.4 Embedding / Whitelabel / External API (★☆☆) ✅ Implemented
- **Current state**: integration uses webpage session tokens; an official .NET SDK is already provided for third-party programmatic integration.
- **Goal**: iframe embedding into third-party sites, brand customization (Logo / theme), implementation-facing REST API keys, and an official client SDK.
- **Target module**: `SystemApi`, `AuthService` (API key), frontend theming, `AguiGroupChat.Sdk`.
- **Implemented**:
  - **Official .NET SDK (third-party integration)**: `src/AguiGroupChat.Sdk`—`AguiClient` (HTTP upstream: auth / groups / members / topics / messages / multi-agent discussions / human-machine interaction / agent exercises / attachments) + `AguiRealtimeClient` (WS full-duplex / SSE downstream + strongly typed event dispatch) + `Models` (DTOs / events consistent with the protocol wire format); `net8.0` / `net10.0`, zero external dependencies; errors uniformly throw `AguiException` (protocol error codes + HTTP status codes). Sample `samples/AguiGroupChat.Client`, and end-to-end tests `tests/AguiGroupChat.Sdk.Tests` (self-hosted real Hub, including full WebSocket flows).
  - **External API keys**: `Auth:ApiKeys` (`[{apiKey, username}]`), `Authorization: Bearer <apiKey>` bypasses login to call the HTTP API as the bound account, inheriting its permissions / admin flags.
  - **Whitelabel branding**: `GET/POST /ag-ui/settings/branding` (public read / admin write), configures app name + Logo + brand primary color + forced dark mode + tagline, persisted to the "branding" extension area; the frontend injects the primary color via CSS variables and renders the login page / top-bar Logo and app name; the "Whitelabel Settings" entry in the admin menu allows online editing.
  - **iframe embedding**: `GroupChatOptions.AllowedFrameOrigins` configures allowed embed origins (CSP `frame-ancestors` and X-Frame-Options correspondingly relaxed, denied by default); the frontend auto-detects iframe / `?embed=1` to enter a compact embed mode (hiding irrelevant buttons / subtitle).

---

## Prioritized Scheduling Recommendations (★★★ priority first)

| Priority | Direction | Rationale |
|---|---|---|
| ★★★ | 4.1 Differentiated Approval Policies + 4.2 Fine-Grained RBAC | A hard gate for enterprise adoption; changes are concentrated in the existing permission / approval modules, high cost-effectiveness |
| ★★★ | 4.3 Operation Audit Logs | Hard requirement in government / finance scenarios; reuse the existing event broadcast for traceability |
| ★★☆ | 1.1 Agent Orchestration / Pipeline | Upgrades from "each expert answering independently" to "collaborative problem-solving", the strongest differentiator |
| ★★☆ | 1.3 Auto-Sinking Important Memories into the Knowledge Base | Lowers maintenance cost, reinforces "better the more it's used"; the technical foundation already exists |
| ★★☆ | 2.3 / 3.1 Cross-Instance Memory Sync + Bridge Reconnect | Bridges desktop/Web silos, improves external-expert reliability |
| ★☆☆ | 6.1 Observability | Improves operations and tuning capability at low cost |

> **Milestone note**: Roadmap items 1.1–6.4 are all delivered; subsequent iterations will refine based on operational feedback (e.g. Redis sharding / Redis cluster, observability enhancements, more enterprise compliance), see the main README and the "Next Steps" outlook in MARKETING.

---

## Notes & Boundaries

- This document **does not duplicate** already-implemented capabilities; each item's "Goal" points to additions or completions relative to the current version.
- Before rollout, verify against the code (each "Target Module" is the entry point), and defer to the main `README.md` and protocol specification documents.
- To dig deeper into the design of a particular item (e.g. the fields / data model / interface changes for 4.1 Differentiated Approval Policies), you can proceed to a detailed design from here.
