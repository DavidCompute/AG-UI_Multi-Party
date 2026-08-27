# Contributing to AG-UI Group Chat Extension Protocol Hub (KnowGath / 知聚)

Thank you for your interest in contributing! This project is a **multi-user / multi-agent real-time group chat protocol hub** that implements the *AG-UI Group Chat Extension Protocol Standard v1.0*. The official product name is **KnowGath (知聚)**.

We welcome contributions of all kinds: bug reports, feature requests, documentation, translations, code, tests, and discussion. Please take a moment to read this guide before getting started.

## Code of Conduct

By participating in this project, you agree to abide by our [Code of Conduct](CODE_OF_CONDUCT.md). Please be kind, constructive, and respectful in all interactions — this is a global, multilingual community.

## Tech Stack

- **Backend:** C# on .NET 10, ASP.NET Core Minimal API (`Program.cs` / `HubApp.cs`)
- **Real-time transport:** WebSocket (full-duplex) and SSE (one-way downstream), with heartbeat keep-alive
- **Agents:** Microsoft Agent Framework (MSAGENT) gateway (`AguiGroupChat.Agents`), pluggable model providers (DeepSeek / Ollama / vLLM / Azure OpenAI / mock)
- **Frontend:** Native JavaScript (no framework) static pages in `src/AguiGroupChat.Web/wwwroot`
- **Desktop clients:** WPF + WebView2 (Windows) and Avalonia 12 (cross-platform) sharing one in-process backend host
- **Storage / persistence:** in-memory (JSON snapshot), PostgreSQL, MySQL, SQLite, and Redis (shared multi-replica) + optional pgvector semantic memory (RAG)

## What You'll Find in the Repository

| Path | Purpose |
|------|---------|
| `src/AguiGroupChat.Hub/` | Protocol Hub: entry, models, storage, messages, transport, options, user management, persistence |
| `src/AguiGroupChat.Agents/` | MSAGENT agent gateway, agent catalog, knowledge base, twins, skills, built-in tools |
| `src/AguiGroupChat.Web/` | Web demo: composition root + static frontend (`index.html` / `app.js`) + management APIs |
| `src/AguiGroupChat.Sdk/` | Third-party integration SDK (`AguiClient` HTTP + `AguiRealtimeClient` WS/SSE) |
| `src/AguiGroupChat.Desktop/` | Windows desktop (WPF + WebView2), SQLite + sqlite-vec + local embedding |
| `src/AguiGroupChat.Desktop.Core/` | Shared cross-platform desktop host |
| `src/AguiGroupChat.Desktop.Cross/` | Cross-platform desktop shell (Avalonia 12) |
| `tests/` | Unit / integration / end-to-end tests |
| `samples/` | Sample clients built on the SDK |
| `docs` in root | `README.en.md`, `PROTOCOL.en.md`, `ROADMAP.en.md`, etc. |

## Development Environment

Before you contribute, set up your environment:

- **.NET 10 SDK** — required to build and run the Hub, Web, agents, and desktop clients. Download from <https://dotnet.microsoft.com/download/dotnet/10.0>.
- **Node.js 20+** — required to run the frontend i18n validation scripts (see [Internationalization](#internationalization) below). Node is *not* required to run the Web frontend itself.
- A code editor of your choice (Visual Studio, JetBrains Rider, VS Code, etc.). Anything that supports the C# / .NET toolchain will work.

Optional, depending on the task:

- A local **PostgreSQL** or **MySQL** server if you work on database persistence tests (cases auto-skip when a database is not configured).
- **Redis** if you work on the shared multi-replica storage mode.

Verify your toolchain:

```bash
dotnet --version   # should report 10.x
node --version     # should report 20.x or newer
```

## Building and Running

### Build everything

```bash
dotnet build AguiGroupChat.slnx
```

### Run the components

The solutions file is `AguiGroupChat.slnx`. Individual components can be launched with `dotnet run --project <path>`:

```bash
# Web demo — Hub + MSAGENT agent gateway + static frontend, open http://localhost:5200 in a browser
# Default Provider=deepseek, so configure an API Key first (see "Connecting DeepSeek" in README.en.md)
dotnet run --project src/AguiGroupChat.Web

# Protocol Hub only (no frontend, no agent replies)
dotnet run --project src/AguiGroupChat.Hub

# Windows desktop (WPF + WebView2, SQLite + local embedding model)
dotnet run --project src/AguiGroupChat.Desktop

# Cross-platform desktop (Avalonia 12, macOS / Linux / Windows)
dotnet run --project src/AguiGroupChat.Desktop.Cross

# Sample SDK client
dotnet run --project samples/AguiGroupChat.Client -- --login zhangsan 123456 --groupIds group_001
```

### Run the tests

```bash
dotnet test AguiGroupChat.slnx
```

603 test cases cover group lifecycle, permission control, subscriptions, visibility fan-out, recall, agent trigger rules, streaming feedback, human-in-the-loop, user management, persistence (JSON / PostgreSQL / MySQL / SQLite), semantic memory, and full HTTP + WebSocket end-to-end integration tests on a real Kestrel.

When tests need a database, override the connection strings via environment variables (`AGUI_PG_TEST_CONN`, `AGUI_MYSQL_TEST_CONN`); otherwise those cases are auto-skipped.

## How to Submit a Pull Request

Please keep changes **small, focused, and relevant to one concern**. This makes review faster and history cleaner.

1. **Fork** the repository to your GitHub account.
2. **Clone** your fork and add the upstream remote:
   ```bash
   git clone https://github.com/<your-account>/AG-UI_Multi-Party.git
   cd AG-UI_Multi-Party
   git remote add upstream https://github.com/DavidCompute/AG-UI_Multi-Party.git
   ```
3. **Create a branch** with a descriptive name:
   ```bash
   git checkout -b feat/add-x          # or fix/..., docs/..., chore/...
   ```
4. **Make your changes**, following the [Code Style](#code-style) and [Internationalization](#internationalization) guidelines below.
5. **Commit** with a clear message. We use short, imperative subject lines (e.g. `Add recall event to SDK`, `Fix empty-group subscription crash`). Reference the issue number when applicable (e.g. `Fixes #123`).
6. **Push** your branch:
   ```bash
   git push origin feat/add-x
   ```
7. Open a **Pull Request** against the default branch (`main`), describing what you changed and why, and how you validated it.

### PR Checklist

Before you submit, verify all of the following:

- [ ] The change is scoped and does not bundle unrelated fixes or refactors.
- [ ] The build passes locally: `dotnet build AguiGroupChat.slnx`
- [ ] Relevant tests pass (or were added for new behavior): `dotnet test AguiGroupChat.slnx`
- [ ] For frontend changes: the page text uses `data-i18n` / `t("key")` and any **new keys were added to both `en.js` and `zh.js`** (see [Internationalization](#internationalization)).
- [ ] Frontend i18n validation passes — run both checks:
      ```bash
      node .github/workflows/check-i18n-keys.js
      node .github/workflows/check-orphan-keys.js
      ```
      (These run automatically in CI on PRs that touch the frontend, so they must be green.)
- [ ] No large generated artifacts are committed (see [Code Style](#code-style)).
- [ ] Documentation is updated if the change affects behavior or public API.
- [ ] Commit message is clear and imperative; no trailing punctuation on the subject line.

## Internationalization

The Web frontend is fully localized. **This is important** — the i18n CI check fails if the dictionaries become asymmetric.

- **Markup:** use the `data-i18n` attribute for static text in `index.html`, and the `t("key")` helper for strings generated in `app.js`.
- **Dictionaries:** live in `src/AguiGroupChat.Web/wwwroot/i18n/`:
  - `en.js` — **the source of truth**; every key is defined here with its English string.
  - `zh.js` — Simplified Chinese translations; it must mirror `en.js` **key-for-key** (no extra, no missing).
- **Adding a new string:** you **must** add the key to both `en.js` and `zh.js`. Keys must be symmetric across the two files.
- **Naming keys:** use dot- or section-style snake_case / camelCase keys grouped by feature (e.g. `login.title`, `chat.send`). Keep them stable and descriptive so they read like schema.
- **Validation:** run both scripts before pushing (same as CI):
  ```bash
  node .github/workflows/check-i18n-keys.js   # en/zh key symmetry
  node .github/workflows/check-orphan-keys.js # no unused / orphan keys
  ```
- **Translation courtesy:** a contribution in English is always welcome already; if you are able, please also provide the simplified Chinese translation for any new or changed keys. This is appreciated but **not** a blocker — a correct English submission with Chinese translation is the ideal, whereas English-only is perfectly acceptable.

## Code Style

- **Keep it simple and minimal.** Prefer the smallest, clearest change that solves the problem. Do not refactor unrelated code.
- **Follow existing conventions.** Match the surrounding code's naming, formatting, and structure — consistency matters more than personal preference.
- **Do not commit large generated artifacts.** No `bin/`, `obj/`, build output, databases, model binaries, or similar generated files. Refer to `.gitignore`; if you must update it, do so deliberately and minimally.
- **Comments** explain *why*, not *what*. Remove dead code rather than commenting it out.
- **Threading / performance:** the Hub uses a lock-free event fan-out model (`ConcurrentDictionary` + one send queue per connection). Preserve this model; do not introduce global locks in hot paths without strong justification.
- **Logging & errors:** prefer descriptive, actionable messages. Library errors in the SDK should be thrown as `AguiException` (protocol + HTTP status code) where applicable.

## Tests

- Add or update tests for the behavior you change. The project has a strong test culture — new features without tests are unlikely to be merged.
- Prefer the existing test structure: unit tests for logic, integration tests on a real Kestrel + `ClientWebSocket` for end-to-end paths.
- Run the full suite with `dotnet test AguiGroupChat.slnx` and make sure you don't regress existing cases.

## Issues and Discussion

### Reporting a bug

Please use the **Bug Report** issue template (it will be offered automatically when you click "New Issue"). A great bug report includes:

- Clear title, environment (OS, .NET version), and steps to reproduce.
- Expected vs. actual behavior.
- Relevant logs, stack traces, or the `appsettings.json` snippet (omitting any secrets).
- Which component is affected (Hub / Web / SDK / Desktop).

### Requesting a feature

Use the **Feature Request** template and describe the problem you want to solve, not just the proposed solution. The maintainers and community can then discuss design together.

### Asking questions

For usage questions, "how do I..." and general architecture discussion, prefer **GitHub Discussions** over issues so that bug/feature trackers stay clean.

### Etiquette

- Search existing issues and discussions before opening a new one to avoid duplicates.
- Be patient and constructive in threads; maintainers are volunteers and may respond on their own schedule.
- Lend a hand: if you can reproduce someone else's bug or have insight, jump in.

## Thank You

Every contribution — a typo fix in the docs, a translation, a test case, a bug report, or a full feature — makes this project better for everyone. We're glad you're here.
