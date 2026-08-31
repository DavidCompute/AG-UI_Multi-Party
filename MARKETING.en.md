# KnowGath — Marketing & Positioning Document

**English** | [简体中文](MARKETING.md)

> **One-line positioning**: A one-stop multi-digital-employee collaboration platform that lets "humans + multiple AI experts + external AG-UI digital employees" collaborate in real time, share memory, and approve safely within a single group chat.

---

## 1. What It Is

KnowGath is a **multi-digital-employee group chat collaboration system built on the standard AG-UI protocol** (implemented in .NET 10). It upgrades the traditional "one-on-one chat" AI assistant into a **group-chat-style multi-role collaboration space**:

- A single group chat can host **multiple AI roles** (requirements assistant, coding assistant, financial advisor...). People @ the ones they want to respond, or let roles auto-speak via full-listen / keyword / context awareness;
- It can **integrate external AG-UI digital employees** (such as AG-UI-protocol-based services like OpenCode), pulling "external experts" into the group chat, with their streaming replies, reasoning traces, tool calls, approval requests, and attachments fed back verbatim;
- Chat history is **vectorized into long-term memory** (RAG) — digital employees automatically search relevant history before responding, so the more you chat, the better it understands your project and team; memory can be **tiered, auto-forgotten by retention days, and governed with visual tools** (data-minimization and audit friendly);
- Supports **topic isolation** (one group chat, multiple discussion threads, each with its own independent session and incremental context), **human-in-the-loop approval** (critical actions must be approved by a human), **AI twin** (takes over for you when you're offline), and **knowledge base** (upload documents; digital employees answer based on your materials) — full enterprise-grade collaboration capabilities.

It runs on **Docker (cloud / intranet server)**, **Windows desktop (standalone / multi-instance)**, and **cross-platform desktop (macOS / Linux)**. Data can live in **PostgreSQL / MySQL / SQLite / Redis / local JSON**, supports **Docker multi-replica + Redis shared-storage horizontal scaling**, and supports **one-click export / import** (accounts, digital employees, chat history, and attachments all packaged together) — **data sovereignty is entirely in your hands**.

---

## 2. Core Features & Highlights

### 1. Multi-Digital-Employee Group Chat Collaboration (Human + Machine Together)
- A single group chat can host any number of AI roles, with **four trigger modes**: @ mention, full listen, keyword, and context (the model decides autonomously whether to speak).
- **Roles can be per-group-chat overridden**: different group chats can configure the same role differently without interference.
- Each digital employee can have a **personal persona (Instructions), its own model, and avatar**; a one-sentence intro can **auto-generate the role definition** in a click; 25 industry role packs can be imported in one click.
- The group chat list sorts by activity + unread badges; joining a new group chat or being pulled into an existing one **refreshes in real time**, and online users are immediately visible.

### 2. External AG-UI Expert Ecosystem (Open Integration)
- Bridged via the standard AG-UI protocol: **any AG-UI service** (OpenCode, self-built agent services, etc.) can be pulled in as an "external expert".
- The external expert's **streaming replies, reasoning traces (collapsible), tool calls ("🔧 calling..." progress line, auto-collapsed on completion), approval cards, and attachments** all feed back verbatim, with the same experience as local digital employees.
- **Per-topic isolated sessions + incremental context**: external experts maintain an independent session per topic and, once established, only sync new messages incrementally — saving tokens while staying precise; the incremental cursor is persisted and survives restarts.
- **Hub cascade**: two Hub instances can connect to each other for cross-organization / cross-network digital employee collaboration and **cross-Hub approvals**.

### 3. Memory & Knowledge (The More You Use It, the Better It Knows You — and You Stay in Control)
- **Semantic memory (RAG)**: chat history is stored vectorized (pgvector / sqlite-vec). Digital employees retrieve relevant history by similarity before responding and inject it into context; supports three retrieval scopes: `agent / group / all`.
- **Personal memory**: a digital employee can reference the history of "the person who triggered it" to understand your preferences and stance (requires both sides to enable it).
- **Knowledge base (RAG knowledge documents)**: upload Word / Excel / PPT / PDF / text, which is chunked and vectorized so digital employees answer based on your materials.
- **Memory governance (per-group-chat / tiered)**: memory is strictly isolated per group chat (private group chats are only retrievable from within their own group chat); each memory can be tagged **normal / important / critical** — at the same similarity, important memories are **preferentially retrieved** and injected into replies, so critical decisions are less likely to be lost.
- **Memory governance (auto-forgetting)**: configurable **retention days**; expired memory automatically stops participating in retrieval and is physically cleaned up by a background task; supports **manual forgetting** (keep the recent 7 / 30 / 90 days per group chat, or forget immediately), meeting data-minimization and compliance requirements.
- **Memory visualization**: a built-in "Memory Management" UI — per-group-chat memory stats, browse by group chat / keyword, per-entry **tiering / deletion**, per-group-chat **forgetting**, fully transparent, controllable, and auditable; permission checks limit you to managing memory only for the group chats you belong to.
- **Memory cross-instance sync (2.3)**: export memory as a "**memory-as-data pack**" (text metadata, migratable across instances); `GET /ag-ui/memory/export` supports **incremental export** per group chat / time lower bound to a central Hub, and `POST /ag-ui/memory/import` de-duplicates by messageId and recomputes vectors on the target instance with its own model — bridging the desktop / Web memory silos.
- **Memory tools**: a digital employee can proactively call `group_memory_search` to recall background and `read_attachment` to read attachments.

### 4. Human-Machine Collaboration & Security (Critical Actions Require Human Approval)
- **Human-in-the-loop approval (HITL)**: tools requiring approval (e.g., posting announcements, creating skills) are **interrupted** after being triggered by the model; a 🔐 card appears in the chat area, and **only the user who made the request** can approve / reject; after the decision, the same session continues.
- **AI twin**: once enabled, the system generates a persona from your posts in public group chats; when you're online you reply yourself, and when offline your twin covers for you; you can also "@ yourself" to summon it anytime.
- Full account system (PBKDF2 password hashing, session tokens, sliding renewal), **strong server-side authentication** (all write operations are bound to the token identity, so no one can impersonate others), attachment upload whitelist, frontend XSS sanitization with CSP, link-proxy HTML sandbox, and SSRF protection.

### 5. Topic-Based Organization (Multiple Parallel Lines Without Chaos)
- A group chat can have multiple **topics** (e.g., "requirement review", "tech selection", "casual chat"), each with independent discussion lines, unread counts, sessions, and memory.
- "New topic from this message" migrates a single post into a discussion start point in one click; topics can be cleared / deleted (their corresponding memory is removed as well).
- Remembers the most recently used topic per group chat and auto-selects it when you re-enter.

### 6. Data Sovereignty & Portability (No Data Lock-In)
- Five storage options: **PostgreSQL (production / cloud), MySQL, SQLite (desktop standalone), Redis (multi-replica shared), JSON snapshots** — switchable in one click.
- **Redis multi-replica horizontal scaling (6.2)**: `Storage:Provider=redis` shares all storage and login sessions across Redis — when horizontally scaling Docker replicas, "a write on one replica is immediately readable on the others, and the same login validates on any replica", enabling high availability and elastic scaling.
- **One-click export / import**: accounts, digital employees, chat history, attachments, and **avatars** are packaged into a zip — migrate to any new environment (account passwords are fully restored; **existing accounts auto-update their profiles including avatars, while passwords are preserved**; digital employees are auto-completed; sender references are auto-rewritten).
- **Desktop multi-instance**: one backend process, multiple windows sharing data; the service only stops when the last window is closed.
- **Initialization (clear everything)**: return to a fresh state in one click (including browser cache) and reconfigure models.
- **Runtime model configuration**: fill in the DeepSeek endpoint / apiKey directly in the UI (leave blank to use the official endpoint and environment variables); takes effect immediately and survives restarts.

### 7. Link Proxy & Intranet Passthrough (Access Intranet Resources from External Networks)
- Links in digital employee replies (including intranet addresses) are **visited by the backend** and returned — mixed content / intranet services that browsers cannot reach directly can still be viewed and downloaded; HTML is sandboxed against scripts, and downloads automatically carry the correct filename (including Chinese names).

### 8. Digital Employee Orchestration & Collaboration (From "Each Replies on Its Own" to "Collaborative Problem-Solving")
- **Multi-step workflows / orchestration (1.1)**: break a complex requirement into subtasks handled by multiple assistants (code + docs + tests) via "plan → decompose → execute in parallel / sequence → aggregate → output", forming an agent loop similar to a coding assistant.
- **Role handoff (1.2)**: a digital employee can delegate a whole round to another role (`relayToAgentId`) for "relay replies / collaborative relays" rather than one-way calls.
- **Scheduled / cron tasks (1.4)**: "on-duty digital employees" report / verify / nag on a 5-field cron schedule, managed visually in a frontend task panel.
- **Automatic accumulation of important memory (1.3)**: one-click aggregation of "critical" conclusions in a group chat into the knowledge base — knowledge is accumulated as conversation happens, cutting manual organization cost.
- **Cross-topic theme linking (5.1)**: a relation matrix showing "which other topics this theme was also discussed in", so multi-person collaboration never misses context.
- **Recursive gather-and-answer loop (Plan C)**: while answering, if the gathered data is insufficient (e.g. still need disk / memory / services / logs), the digital employee <b>proactively keeps invoking skills / assigning subordinates</b> until the info is complete — <b>never stopping to ask "continue?"</b>; skill-only digital employees can also enter plan orchestration, ideal for multi-skill checks like "is there anything wrong with this PC?"

### 9. Governance & Compliance (The Hard Threshold for Enterprise Rollout)
- **Differentiated approval policies (4.1)**: in addition to the global tool list, each digital employee can have an independent approval tool list; `approveAll` approves all remaining pending tools for the current task in one go.
- **Fine-grained RBAC (4.2)**: channel-level permissions — who can @ a digital employee (`canInvokeAgents`), who can approve human-in-the-loop actions (`canApprove`), who can manage the knowledge base (`canManageKnowledge`) — layered on top of system admins (`IsAdmin` / `AdminUserIds`).
- **Operational audit logs (4.3)**: records key operations such as "who / when / approved which tool / import-export / reset"; exportable, meeting classified and compliance requirements.
- **Session security (4.4)**: multi-device session viewing / revoking, optional second-factor login (TOTP), and `AllowedOrigins` configured from the UI.
- **Configuration governance (6.3)**: admins adjust and persist runtime parameters online in the console's "Configuration Governance" tab (session validity, group chat / message policy, tool toggles / work tools / thinking mode / daily token quota, approval lists, iframe embed sources) without editing config or restarting.
- **External API keys (6.4)**: REST API Key authentication (`Auth:ApiKeys`) for programmatic access.

### 10. Reliability, Observability & Ecosystem
- **Web multi-replica horizontal scaling (6.2)**: Redis shares sessions / storage — in multi-replica deployments, group chats / users / tasks / usage / extension areas and login sessions are all written to Redis (`agui:*`), with consistent reads and writes across replicas; horizontal scaling linearly raises concurrent capacity.
- **Bridge health & auto-reconnect (3.1)**: health probing, backoff reconnection, and offline catch-up for external AG-UI endpoints make "external experts" more reliable.
- **Capability negotiation (3.2)**: automatically discovers which tools / attachments / approval types external endpoints support via the AG-UI protocol, reducing manual configuration.
- **Digital employee / skill marketplace (3.3)**: built-in download marketplace for "industry role / skill / knowledge-base templates", pushed out in one click.
- **Runtime metrics (6.1)**: model call volume, tokens, memory hit rate, bridge failure rate, and other metrics with visualization (`/ag-ui/admin/metrics`, `/ag-ui/admin/usage`).
- **Memory timeline (2.2)**: replay "how a conclusion evolved" per topic, serving retrospectives and audits.

### 11. Rich Media Messages (Close to Instant-Messaging Habits)
- **Multi-image upload**: up to 9 images per batch, displayed in a **tiled grid** within the message and clickable to enlarge; thumbnail previews in the input area.
- **Voice messages**: tap-and-hold the mic to **record** (MediaRecorder), producing audio attachments (mp3/wav/ogg/m4a/webm, etc.) played back inline via `<audio>` in the message — voice attachments carry metadata only and are not injected into the model's text.
- **Canvas annotation**: built-in drawing canvas (brush / eraser / color / clear); export to PNG image attachment and send in one click — great for hand-drawn diagrams and annotations.

### 12. Accessibility & Notification Center (Experience & Reachability)
- **In-app notification center (5.4)**: the top bar 🔔 aggregates notifications — @ mentions, pending approvals / inputs, WS disconnect / reconnect, new messages in non-visible views (including schedule-task broadcasts); click to jump to the source group chat; unread badges + clear; syncs system desktop notifications when the page is hidden.
- **Frontend performance**: message virtual scrolling + lazy loading (windowed rendering, measured row-height anchoring, paginated loading earlier), so very large group chats scroll smoothly.
- **Accessibility (ARIA, 5.3)**: the message list uses `role="log"` with screen-reader announcements; each message carries semantic labels for sender / time / content; icon buttons / drawing areas have complete `aria-label`; modals use `role="dialog"` with focus management and Esc-to-close; fully keyboard operable.

### 13. Whitelabel / Embedding / External API (Open & Integrative)
- **Whitelabel theme (6.4)**: admins configure the app name + logo + primary brand color + forced dark mode + tagline online ("Whitelabel Settings"); the login page and top bar take effect immediately, and the theme can be restored to default in one click; the primary color auto-derives accent and dark-mode variants.
- **iframe embedding (6.4)**: configure `AllowedFrameOrigins` to allow trusted sites to embed via iframe (CSP `frame-ancestors` allows it; disabled by default); after embedding, auto-enters compact mode (hides irrelevant buttons).
- **External API keys (6.4)**: `Auth:ApiKeys` config keys; `Authorization: Bearer <apiKey>` authenticates without login to call the HTTP API as a bound account — suitable for scripts and integration.
- **Official .NET SDK (third-party integration)**: `src/AguiGroupChat.Sdk` provides `AguiClient` (HTTP upstream) + `AguiRealtimeClient` (WS/SSE downstream) with strongly typed Models — a third-party app can log in, create group chats, send messages, subscribe in real time, and receive agent streaming replies with a single reference (see [SDK docs](src/AguiGroupChat.Sdk/README.md), examples in `samples/AguiGroupChat.Client`).

### 14. On-Device Smart Tools & Desktop Ops (Human-in-the-loop local execution)
- **Client-execution skills (`ExecutionLocation=Client`)**: a skill can be marked to <b>run on the local machine</b> — shell runs via the native bridge in a sandbox on the browser's host; http runs via the browser fetch; the result is fed back to the model. <b>Works out of the box in the desktop edition</b> (the desktop shell is the local host); for Docker + a local browser, use the standalone NativeBridge with one-click Detect (loopback + token auth + sandbox / timeout / truncation).
- **Built-in standard ops skill pack**: ships seven `ops_*` client skills — system info / disk / memory & CPU / top processes / network connections / service status / recent System error logs — letting digital employees <b>troubleshoot the local PC</b> (disk nearly full, high memory, suspicious outbound connections, stopped services, error / blue screen) and act on real local data.
- **Run all locally inside a plan**: when several local skills are needed at once, they merge into one "run all locally" confirmation card (approve once), the frontend runs each and lights up the execution-plan card, then synthesizes a combined answer.

### 15. Zero-Friction Skill Configuration (Generate Skills from Natural Language)
- **🤖 Generate a skill from plain text**: in the Skill Library, type a request (e.g. "check local disk usage and report free space per partition"), and the LLM produces name / kind / command / description / execution location / client-runner config, filled into the form for review then save — <b>no need to hand-write commands or JSON</b>, so even non-technical users can add capabilities to digital employees.

---

---

## 3. Technical Advantages

| Dimension | Description |
|---|---|
| Standard protocol | Built on AG-UI group chat extension protocol v1.0, aligned event / field by field with native AG-UI ecosystem (Microsoft.Agents.AI.AGUI) |
| Mature framework | Digital employee gateway built on Microsoft Agent Framework (MSAGENT), natively supporting streaming, tool calls, skills (digital-employee-to-digital-employee), approvals; client-execution skills / native tool bridge enable human-in-the-loop local execution, and plans support a recursive gather-and-answer loop |
| Multi-client coverage | Web (Docker), Windows desktop (WPF + WebView2, multi-instance), cross-platform desktop (Avalonia) |
| Extensible | Storage abstraction (IGroupStore / IUserStore) with built-in **memory / postgres / mysql / sqlite / redis** implementations, switchable in one click; `IAgentGateway` for custom gateways; digital employee directory / trigger rules / topics all managed at runtime |
| Data security | Password PBKDF2, token auth, attachment whitelist, strict ownership validation for private group chats / private digital employees, HTML sandbox, SSRF protection, memory tiering and auto-forgetting, local-execution sandbox + token auth |
| Quality assurance | **648 automated test cases** (group chat lifecycle / permissions / RBAC / audit / memory & timeline / memory cross-instance sync / Redis & three database storages / Redis shared sessions / bridge & reconnect / approvals / orchestration & role handoff / schedule tasks / rich media attachments / whitelabel / configuration governance / end-to-end) |

---

## 4. Use Cases (Detailed)

### Scenario 1: Enterprise Internal Team Collaboration & Knowledge Accumulation (General for R&D / Product / Ops)

**Scenario description**: A team of 5-20 people hosts multiple AI roles ("requirements assistant", "coding assistant", "data analyst", etc.) in a daily-work group chat. Regular members need no AI tool skills — they call multiple experts **within their existing chat habits**.

**Typical usage**:
- A product manager says "@requirements-assistant help me break down this requirement and output user stories" → the requirements assistant instantly produces a structured breakdown;
- When developers discuss architecture in the tech group chat, the "coding assistant" uses **context triggering** to **proactively** offer suggestions and code snippets;
- History from project discussions is vectorized into **semantic memory** — two weeks later, ask "why did we choose this solution back then?" and the digital employee retrieves history and auto-reviews the context;
- Mark key decisions as **important / critical** in "Memory Management" (preferentially retrieved at the same similarity); outdated discussions auto-forget per retention days, with a one-glance, always-deletable memory UI;
- Upload 《Project Weekly Report Template.docx》 to the **knowledge base**, bind it to the data analyst, and it will generate weekly reports by the template from then on.

**Value**: embeds AI into the team's existing collaboration flow with zero learning cost; memory keeps knowledge **in the organization rather than in individuals**, while tiering / forgetting / visualization make memory **controllable and auditable**; multi-role division means every problem has the right AI expert.

### Scenario 2: R&D Teams with External AI Expert Integration (AG-UI Bridging / OpenCode-class Services)

**Scenario description**: A team has built or purchased an AG-UI-protocol-based coding digital employee service (e.g., a local OpenCode coding agent) and wants it to both work standalone and be callable by team members in the group chat anytime.

**Typical usage**:
- Create an "external coding expert" in the digital employee management, fill in the bridge endpoint `http://192.168.x.x:8889/ag-ui/opencode`, and pull it into the R&D group chat;
- A member says "@external-coding-expert make me a PPT on the AI prospect outlook" → the external expert's **reasoning trace, tool calls, and execution progress** stream back in real time ("🔧 tool calling..."), and on completion the attachment (PPTX) returns into the group chat as a card;
- When the external expert performs a permission-gated operation (e.g., publishing an announcement / deploying), an **approval card** pops up in the group chat that only the requesting user can approve;
- Intranet links in the external expert's replies (e.g., a local preview service) go through the **link proxy** and are visited server-side, so members can click and view them directly (downloaded files automatically carry the correct filename);
- The external expert works on multiple topics in parallel, each with an independent session and incrementally synced context.

**Value**: upgrades "tool-type" AI into "collaborative-type" AI; humans, local digital employees, and external AG-UI experts collaborate in one space while **approval stays in human hands** — efficient and controlled.

### Scenario 3: Private / Classified-Environment Digital Employee Hub (Finance, Defense, Healthcare, Government)

**Scenario description**: Data-sensitive industries require **data to never leave the intranet** and cannot use public-cloud LLM APIs; meanwhile they need full multi-digital-employee collaboration, memory governance, and audit capability.

**Typical usage**:
- Docker deploy on an intranet server (PostgreSQL on disk); **all data is localized** with no external-network dependency (local Ollama provides embeddings; models can be private-deploy OpenAI-compatible endpoints);
- Use **private group chats** to host classified discussions: their chat memory is **only retrievable within that group chat**; other group chats triggering digital employees cannot read it, preventing lateral information leakage;
- **Memory governance meets data minimization & compliance**: configure retention days for auto-forgetting per confidentiality requirements, forget manually per group chat, and audit visually in the memory management UI — memory content, level, and expiry are all controllable and checkable;
- **Private digital employees**: only the creator can use / pull into group chats / edit — ideal for personal dedicated advisors;
- **Human-in-the-loop approvals**: classified operations (external publishing / data deletion) must be human-approved, fully traced;
- Regularly **one-click export** data packs (accounts + digital employees + chat history + attachments + avatars) for offsite backup; **initialization** clears everything in one click.

**Value**: full AI collaboration capability within compliance red lines; data sovereignty, permission boundaries, approval traces, memory forgetting, and backup migration — all five requirements satisfied.

### Scenario 4: Personal Knowledge Steward & AI Twin Assistant

**Scenario description**: Individual users want a "24×7" AI assistant that answers questions in work group chats, accumulates personal knowledge, and can even reply on their behalf when offline.

**Typical usage**:
- The desktop version (SQLite + local embedding) runs standalone on Windows / macOS / Linux across all three platforms, **multi-instance** (work window + home window share one backend; the service only stops when the last window closes);
- Create personal knowledge bases (personal documents, notes, resumes, contract templates) and bind them to the assistant role, so "help me write a self-intro based on my resume" is answered from your materials;
- Enable the **AI twin**: it auto-generates a persona from your posts in public group chats; you reply when online, and **the twin covers when offline**; in a group chat, @ yourself can also "summon" the twin anytime;
- Enable **personal memory** so the assistant increasingly understands your preferences; mark key conclusions as "important" in **Memory Management** so the assistant remembers your critical preferences long-term;
- Use **topics** to separate "work" / "life" / "study" lines of discussion without interference.

**Value**: a private assistant that "knows you", always available and on duty; all data stays local with worry-free privacy, and memory is under your control.

### Scenario 5: Education & Academic Discussion (Multi-Supervisor, Multi-Student Collaboration)

**Scenario description**: University research groups / training classes need multiple specialized AI teaching assistants (writing, coding, math, literature review) to join group discussions and provide personalized tutoring to students.

**Typical usage**:
- One course group chat hosts "writing TA", "coding TA", and "math TA", each bound to the corresponding knowledge base (textbooks, lecture notes, past exam questions);
- A student asks "@writing-TA proofread this thesis abstract and give revision suggestions" → the TA answers based on the knowledge base and course requirements;
- "@coding-TA" proactively points out common errors in student code via **context triggering** (full listen + contextual judgment);
- Academic discussions are split by **topic**: "course Q&A", "project meetings", "paper progress" don't interfere with each other;
- Teachers enable the **AI twin**: when offline, the twin first answers common questions, then the teacher takes over when back online;
- Mark key classroom knowledge points as "important" in Memory Management so TAs cite them preferentially.

**Value**: multi-TA parallel tutoring relieves teacher load; the knowledge base keeps answers **aligned to the course syllabus** rather than generic; topic organization keeps discussion orderly, and memory tiering makes key content stand out.

### Scenario 6: Cross-Organization / Cross-Region Multi-Party Collaboration (Hub Cascade & External Expert Networks)

**Scenario description**: Two companies or two departments jointly advance a project, each with internal digital employees and knowledge, needing to collaborate without fully opening up all their data.

**Typical usage**:
- Company A's Hub "external expert" role bridges to Company B's Hub via **hub dialect bridging** — the two systems cascade: trigger messages in A's group chat forward to B's digital employees, and B's streaming replies feed back into A's group chat;
- The external expert's **approval requests cascade across Hubs**: when B's digital employee needs to perform a sensitive operation, the approval card appears in A's group chat, decided by the A-side requester;
- **Topic** isolation separates multi-party collaboration lines; each party manages its own group chats and memory; **private group chats** ensure internal discussion isn't retrieved by external digital employees;
- Each party configures **memory retention days** per compliance requirements; after the project ends, **export** the collaboration group chat data for archiving, or **initialize** to clear external-access traces.

**Value**: makes "organizational boundaries" no longer block AI collaboration; cascaded approval keeps cross-organization operations **human-controllable**; memory governance and data sovereignty are each preserved.

---

## 5. Deployment Modes at a Glance

| Mode | Best for | Data storage | Highlights |
|---|---|---|---|
| **Docker Web** (recommended for production) | Intranet server / cloud host | PostgreSQL (+ pgvector) | Single `docker compose up -d` starts Web + Postgres + Ollama; RAG memory and memory governance work out of the box |
| **Docker Web + Redis multi-replica** | High concurrency / high availability | Redis + PostgreSQL (+ pgvector) | `docker compose up --scale web=N` scales horizontally; all storage and login sessions share Redis; multi-replica consistent reads/writes and one login valid everywhere |
| **Windows desktop** | Individuals / small-team standalone | SQLite (+ sqlite-vec) | WPF + WebView2, bundled local embedding model, works offline; **multi-instance sharing one backend** |
| **Cross-platform desktop** | macOS / Linux / Windows | SQLite | Avalonia shell, one host, consistent experience across all three platforms |
| **Protocol Hub** (protocol-only) | Secondary development / server-side | Any | Exposes only the AG-UI protocol endpoints; can be embedded in your own system |

---

## 6. Quick Start (5 Minutes)

1. **Docker**: `cp .env.example .env && docker compose up -d --build` → open `http://localhost:5200` in a browser;
2. **Desktop**: install the MSI (Windows) and double-click to launch; the local service starts automatically;
3. Register / log in (on first entry, configure the DeepSeek endpoint / apiKey as prompted; blank uses the official endpoint and environment variables);
4. In the top bar "🤖 Digital Employees", create an AI role (or one-click import 25 industry roles from `tools/agents-starter.json`);
5. Click "＋" on the left to create a group chat, check members and digital employees, and @ one to start chatting;
6. Need an external expert? Just fill in the AG-UI bridge endpoint in the digital employee form;
7. After chatting for a while, go to Account Menu → "Memory Management" to experience tiering, auto-forgetting, and visual governance.

---

## 7. Next Steps & Roadmap

- **Multimodal advancement**: image / voice Q&A, audio & video meeting integration (voice messages, multi-image, and canvas annotations are already in as attachments; two-way voice dialogue and image Q&A are to be deepened);
- **Deeper observability (6.1)**: metric visualization dashboards, OpenTelemetry integration, cross-replica diagnostics on Redis multi-replica;
- **Hybrid retrieval & RAG enhancement**: BM25 sparse retrieval blended with dense vectors, covering Chinese / code scenarios; switchable embedding providers on demand;
- **Deeper memory governance**: important memories auto-accumulated into the knowledge base, memory cross-instance incremental sync to a central Hub, memory multi-version / timeline replay;
- **Ecosystem & marketplace**: online distribution marketplace for digital employee / skill / knowledge-base templates, deeper whitelabel customization, and an enhanced external REST API.

---

## 8. Commercialization Direction & Competitive Positioning

### 8.1 Target Customers

| Customer segment | Pain points | Our entry point | Willingness to pay |
|---|---|---|---|
| **Mid/large-enterprise intranet AI hub** | General LLM SaaS can't keep data in-domain; multiple teams wiring AI separately leads to duplication and unmanageability | Private deployment + tiered memory governance + approval traces + audit: an integrated solution where data never leaves the intranet | High (compliance is a hard requirement) |
| **R&D / data teams** | External coding digital employees (OpenCode-class) hard to connect with team collaboration | AG-UI bridging turns "tool-type AI" into "collaborative-type AI"; humans + local digital employees + external experts under one roof | Medium-high |
| **Consulting / legal / finance** | Knowledge lives in individuals rather than the organization; key outputs can't be traced | Knowledge base + important-memory accumulation + topic organization + operational audit, turning experience into assets | High |
| **Education & training** | Multi-TA / multi-supervisor parallel tutoring is expensive and hard to personalize | Multi-digital-employee discussion + knowledge base aligned to the curriculum + AI twin standby | Medium |
| **Personal knowledge steward** | Existing assistants lack private memory and offline availability | Desktop SQLite + local embedding; all data local, privacy worry-free | Low → subscription |

### 8.2 Competitive Positioning (Differentiation)

| Dimension | General AI assistant / Chat | Enterprise RAG / knowledge platform | **KnowGath** |
|---|---|---|---|
| Collaboration form | One-on-one chat (Q&A) | Knowledge Q&A (document-driven) | **Group-chat-style multi-digital-employee collaboration** (human & machine on one thread) |
| Digital employee | Single assistant | Essentially none | **Multiple roles + external AG-UI expert bridging** |
| Memory | Session context | Document vector store | **Per-group-chat tiered memory + auto-forgetting + visual governance + cross-instance sync** |
| Security governance | Weak | Partial | **Differentiated approval policies + fine-grained RBAC + operational audit + TOTP + session management** |
| Data sovereignty | Cloud-hosted | Privatizable | **Local / private / multi-cloud storage switchable; no data lock-in** |
| Extensibility | None | Weak | **Docker multi-replica + Redis shared storage, horizontal scaling** |

**One-line positioning**: upgrades the "one-on-one chat AI assistant" into a **multi-person, multi-digital-employee collaboration platform that is governable, scalable, and knows your organization's memory** — with Chat's ease of use, enterprise RAG's rigor, and the **collaborative orchestration and security governance** both of them lack.

### 8.3 Commercialization Path (Phased)

1. **Open-source acquisition (current)**: core capabilities open for self-use; build trust and community reputation through code quality, documentation, and protocol openness (AG-UI standard), forming an open ecosystem of templates / roles / knowledge bases;
2. **Value-added subscription (enterprise)**: package the **governance suite** (fine-grained RBAC, operational audit, TOTP, configuration governance, Redis multi-replica) into a paid edition with whitelabel, branding, and support;
3. **Private / bespoke deployment (industry depth)**: deliver **industry template packs** (roles + knowledge bases + approval-policy templates) to classified industries (government / finance / defense) plus on-site deployment / training / compliance-certification services, priced by seat and deployment scale;
4. **Ecosystem marketplace (platform)**: build a "digital employee / skill / knowledge-base template" trading marketplace with commissions or copyright charges, creating a two-sided network effect; commercialize the external REST API: tiered billing by API call volume / seats / usage.

### 8.4 The Best Go-to-Market Story Right Now (with Real Hands-on Experience)
> A data-sensitive R&D team (about 12 people: product / frontend / backend / ops) needs **data to never leave the intranet** and a **shared AI hub for the whole team**. Here's the complete process of them pressing every button and seeing every screen.

**Step 1 · One-command intranet startup, with sample data out of the box**
Ops runs `cp .env.example .env && docker compose up -d --build` on an intranet server, pulling up three containers — Web + PostgreSQL (pgvector) + Ollama — with a single command. Because `STORAGE_PROVIDER=postgres`, all business data and vector memory are written to the intranet database; embeddings are provided by the bundled Ollama (auto-pulls the bge-m3 model on first run). Sample data is auto-seeded on startup (`GroupChat__SeedSampleData=true`). The first time a member opens `http://intranet-IP:5200` and registers, they become an admin and can already see sample group chats and several digital employees on the left — **they can click around and play without configuring anything first**.

**Step 2 · Create three digital employees ("Code / Requirements / Legal") + an external OpenCode expert**
1. Click "🤖 Digital Employees" in the top bar to open the management panel; first click "📥 Import" in the toolbar to batch-create 25 industry roles from `tools/agents-starter.json`, then search and edit to keep only what you want;
2. Open the "Product Assistant" form: nickname, avatar (local image upload ok), and a one-sentence intro "product requirements analysis assistant"; click "✨ Generate Role Definition" — the model auto-fills **identity / responsibility scope / reply style requirements** into the Instructions system prompt; review, fine-tune, and save;
3. Repeat the same steps to create "Code Buddy": its persona stresses "explain the approach before giving code"; trigger mode is set to **context trigger** (🧠), and the model can be individually overridden to `deepseek-reasoner` thinking mode;
4. To add the external coding expert, create a role and fill in the AG-UI bridge endpoint `http://192.168.6.18:8889/ag-ui/opencode` — after saving, this role **no longer calls the local LLM**; messages are forwarded to the external OpenCode via the AG-UI protocol, and its **reasoning trace, tool-calling progress, approval cards, and attachments** feed back into the group chat verbatim.

**Step 3 · Pull up a "Technical Discussion" group chat and use it daily**
1. On the left under "My Group Chats", click "＋" in the top-right to create "Technical Discussion", checking members + the three digital employees into it;
2. A member types `@Code-Buddy can you rewrite this into a SQL-injection-proof version` in the input box and gets a streaming reply; to watch multiple roles solve a problem together, click the "🗣 Multi-digital-employee discussion" icon on the left of the input area, check "Product Assistant + Code Buddy", and set the topic "How should we design our current API permission model?" — the two digital employees **speak in relay sequence**, forming a multi-angle discussion among multiple parties;
3. Semantic memory takes effect automatically: `@Product-Assistant we'll present this solution at next week's review — help me recap the decision background`; the assistant retrieves the vector memory from prior discussions and lays out the selection rationale and pitfalls in one go.

**Step 4 · Turn memory into "team assets"**
1. At a key point in the discussion, a member clicks the account menu in the top-right → "🧠 Memory Management" and sees the memory stats for the "Technical Discussion" group chat; mark "why we chose MySQL partitioning" as **important** and "must go through review before external publishing" as **critical** — from then on, among memories of similar similarity, these are **preferentially retrieved** and injected into replies;
2. For outdated topics, admins manually set **30-day auto-forgetting** per group chat, with background physical cleanup on expiry; memory is **isolated per group chat** throughout — "Classified Requirements" is a private group chat, and other group chats cannot read its content when triggering digital employees;
3. Upload《Interface Review Form.docx》 to the **knowledge base** and bind it to "Product Assistant"; it will then first retrieve the template and output review comments per the spec.

**Step 5 · Critical actions require human approval, fully auditable**
1. A member tells "Code Buddy" "post an announcement to the project group chat", hitting the approval-required tool `publish_announcement` — the digital employee **interrupts** at this step, a 🔐 approval card pops up in the chat area, and **only the member who made the request** sees the three buttons "✅ Approve / ✅🔄 Approve all this time / ❌ Reject"; everyone else only sees waiting. After the decision, the digital employee continues;
2. To delegate authority, click "approveAll" (approve everything remaining for this run), or the admin refines this tool's approval list down to an individual digital employee in "Configuration Governance"; to tighten things up, disable a member's `canApprove` (who can approve), `canInvokeAgents` (who can @ digital employees), or `canManageKnowledge` (who can manage the knowledge base) in channel-level permissions;
3. The admin opens "Account Menu → Operational Audit" and sees the full trail: who / when / approved which tool / what was imported or exported, exportable for compliance archiving.

**Step 6 · Work-type digital employees take on long tasks (optional advanced)**
Check "🛠️ Work-type digital employee" for "Code Buddy", giving it a dedicated workspace (`data/workspaces/code-bang/`). A member starts "organize this week's release list and generate a weekly report" in the group chat; it auto-creates a task to read / write files and run whitelisted commands, with the frontend "Task Center" showing **status / progress / result** in real time, and the message shows the **plan checklist + progress bar** written back from the workspace `PLAN.md`; read / delete operations go through in-group-chat approval too. Cross-dialogue continuity relies on the workspace `NOTES.md`, so nothing needs to be re-explained.

**Step 7 · Data sovereignty fallback & scaling**
1. The "Data Backup" panel one-click exports a zip (accounts + digital employees + chat history + attachments + avatars) for offsite backup; on a new environment, one-click import (account passwords fully restored, existing accounts auto-update profiles, digital employees auto-completed, sender references auto-rewritten), or "System Initialization" returns to a fresh state in one click;
2. For high availability, switch `Storage__Provider` to `redis` and `docker compose up --scale web=2`: multi-replica shared sessions and storage, **any login on any replica is valid**, and horizontal scaling linearly raises concurrency.

**Result**: from "one-command intranet deploy" to "multi-digital-employee daily collaboration", "governable organizational memory", "per-person approval with audit trails", "work-type digital employees running long tasks", and "portable, scalable data" — this complete **private, governable, collaborative, scalable** experience is exactly what a general SaaS assistant cannot deliver.
