# AG-UI Group Chat Desktop

**English** | [简体中文](README.md)

A pure desktop application (Windows): it reuses all the features of the web version (group chat, agents, human-in-the-loop approval, semantic memory RAG,
personal memory, AI twin, attachments, topics, trigger modes, etc.), but with **all data and models stored locally**:

- **Database**: SQLite (`data/agui.sqlite`); semantic memory uses **sqlite-vec** (`vec0` vector virtual table, shipped with `libs/vec0.dll`)
- **Vector model**: **LLamaSharp** (the .NET implementation of llama.cpp) loads a GGUF embedding model locally, fully offline
- **UI**: WPF + WebView2 window that embeds Kestrel to reuse the existing web frontend (no external browser required)

## Quick Start

```bash
# 1. (Nothing to prepare) The local embedding model bge-m3 (1024 dimensions, models/embedding.gguf) is already bundled, ready to use and fully offline.
#    To swap the model yourself: rename any GGUF embedding model to embedding.gguf and place it in the models/ directory,
#    then update EmbeddingDimensions in appsettings accordingly (nomic-embed-text=768, bge-m3=1024)

# 2. Build and run
dotnet build src/AguiGroupChat.Desktop/AguiGroupChat.Desktop.csproj
dotnet run --project src/AguiGroupChat.Desktop/AguiGroupChat.Desktop.csproj
```

Once launched, a desktop window opens. Multiple instances share the same backend process: the local service **always runs on `http://127.0.0.1:5200`** (the first instance automatically starts a `--backend` child process). If port 5200 is occupied by another program, the first launch will prompt "Close the program occupying the port and try again."
On first use, just register an account.

> **Model bundled**: bge-m3-Q8_0 (about 605MB, 1024 dimensions) is distributed together with the installer, which is why the MSI is relatively large (several hundred MB).
> To slim it down: delete the models/ directory and configure `Agents:Memory:ModelDownloadUrl` (direct link); it will be downloaded automatically on first launch.
> The MSI is a per-user install (`%LocalAppData%\AguiGroupChat`, no admin required, writable directory); data and models are placed directly in the install directory.

## Configuration (appsettings.json)

| Configuration | Default | Description |
|---|---|---|
| `Storage:Provider` | `sqlite` | Fixed SQLite; `ConnectionString` can change the path |
| `Agents:Provider` | `deepseek` | Chat model: `mock` (no key) / `deepseek` / `openai` (compatible endpoint) |
| `Agents:ApiKey` | - | Chat model key (`mock` does not need one); also reads env var `DEEPSEEK_API_KEY` |
| `Agents:EnableTools` | `true` | Tool calls: calculator / unit_converter / group_memory_search memory retrieval / read_attachment / publish_announcement (requires approval) |
| `Agents:EnableWebTools` | `false` | Web tools: web_search / read_url (SSRF-protected); requires external network |
| `Agents:Memory:Enabled` | `true` | Semantic memory switch |
| `Agents:Memory:Provider` | `llama` | Embedding provider: `llama` (local LLamaSharp) / `http` (OpenAI-compatible endpoint) |
| `Agents:Memory:LlamaModelPath` | `models/embedding.gguf` | Local GGUF model path; when missing it also auto-detects `%LocalAppData%\AguiGroupChat\models\embedding.gguf` (compat fallback for older perMachine installs) |
| `Agents:Memory:ModelDownloadUrl` | empty | Direct model link (e.g. a gguf file on Hugging Face / ModelScope); once configured, downloaded automatically on first launch (falls back to %LocalAppData%\AguiGroupChat\models when the install dir is not writable) |
| `Agents:Memory:EmbeddingDimensions` | `1024` | **Must match the model** (bundled model bge-m3=1024; nomic-embed-text=768 must be changed to 768) |
| `Agents:Memory:LlamaThreads` | `4` | Number of local inference threads (4~8 recommended for CPU desktops) |
| `GroupChat:SeedSampleData` | `true` | Seeds sample groups / members / agents on first launch |

> Chat models (DeepSeek, etc.) still need network connectivity; **core features such as semantic memory / group chat / agent management work offline**.

## How Semantic Memory (RAG) Works

1. Messages are persisted → `AgentMessageMemory` is vectorized via `LlamaEmbeddingProvider` (LLamaSharp loads the model locally)
2. Vectors are written to the sqlite-vec `vec0` virtual table (`agui_message_memory_vec`); metadata is written to `agui_message_memory`
3. Before replying, the agent retrieves similar memories (cosine similarity, `TopK=5`, `MinScore=0.25`) through
   `MemoryContextProvider` and injects them into the prompt; scope `agent` = the agent's memories across all groups
4. Personal memory: when enabled for a user / agent, it retrieves their own past messages when replying (isolated within private groups)

**Degradation path**: if `libs/vec0.dll` is missing or fails to load, it automatically degrades to "store vectors as BLOB + in-memory cosine retrieval in .NET",
which is functionally equivalent (slower than vector indexing for large datasets). The log indicates the current mode.

## Directory Structure

```
src/AguiGroupChat.Desktop/
├── Program.cs            # Composition root: embedded Kestrel (reuses Hub + gateway + API + frontend) + WPF window
├── MainWindow.xaml(.cs)  # WebView2 window
├── appsettings.json      # Desktop configuration (sqlite + local llama embedding)
├── models/               # Bundled local embedding model: embedding.gguf (bge-m3-Q8_0, 1024 dims, ~605MB)
├── libs/vec0.dll         # sqlite-vec native extension (Windows x86_64)
└── data/                 # Generated at runtime: agui.sqlite + uploads/
```

## Notes

- The desktop version and web version share the same protocol / Hub / gateway / frontend code, so features are identical;
- Agent triggers, human-in-the-loop approval cards, twins, attachments, topics, private groups, etc. are all available;
- **Skills**: in agent management you can configure skills for a role, attaching other agents (including AG-UI bridged external experts) as callable sub-agents;
  when the model needs domain information it automatically invokes the sub-agent and cites its reply; agents can also build skills themselves via the `create_skill` tool (requires approval);
- **Knowledge Base**: in agent management you can create knowledge bases and upload documents (Word / Excel / PPT / PDF / text);
  before replying, relevant knowledge-document content is automatically retrieved and injected (RAG) so the agent answers based on your materials; document vectors and semantic memory share one storage backend (sqlite-vec + local bge-m3);
- **VC++ runtime is bundled** (`vcruntime140.dll` / `vcruntime140_1.dll` / `msvcp140.dll` are installed next to the exe with the MSI,
  app-local deployment, no need to preinstall on the target machine). If LLamaSharp's native library still fails to load on some machines (e.g. due to missing legacy system libraries),
  the app automatically degrades by disabling semantic memory and logs the error—**it will not crash**, and other features such as group chat remain unaffected;
- WebView2 runtime: usually built into Windows 10/11; if missing, install it from
  [Microsoft Edge WebView2](https://developer.microsoft.com/microsoft-edge/webview2/).
