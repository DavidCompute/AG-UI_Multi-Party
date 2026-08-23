# AG-UI Group Chat Desktop (Cross-Platform: Avalonia)

**English** | [简体中文](README.md)

Another UI shell for the same desktop app: **Avalonia 12** (cross-platform XAML) + the official **WebView control**
(`Avalonia.Controls.WebView` / `NativeWebView`)—Windows uses WebView2, **macOS uses WKWebView**,
Linux uses WebKitGTK. Features match the Windows version (`src/AguiGroupChat.Desktop`); both share
`src/AguiGroupChat.Desktop.Core` (SQLite + sqlite-vec memory, LLamaSharp local embedding). Note the **different process model**: this version uses a
**single-instance in-process** host (`DesktopApp.Start` starts Kestrel directly in-process); the Windows version is a multi-instance shared architecture of "UI instance + `--backend` backend child process".

## Run

```bash
# 1. (Nothing to prepare) The local embedding model bge-m3 (models/embedding.gguf, 1024 dimensions) is already bundled, ready to use;
#    swapping the model requires updating EmbeddingDimensions in appsettings as well
# 2. Build and run (same command on Windows / macOS / Linux)
dotnet run --project src/AguiGroupChat.Desktop.Cross
```

Once launched, an Avalonia window opens and the local service runs on `http://127.0.0.1:5200` (if that port is occupied, the system assigns a free one at random).
Register an account on first use. Features are fully identical to the Windows version: group chat, agents, human-in-the-loop approval,
semantic memory RAG, personal memory, AI twin, attachments, topics, trigger modes, AG-UI bridging, etc.

## macOS Notes

| Component | Status | Description |
|---|---|---|
| UI shell | ✅ WKWebView | Official Avalonia WebView control macOS backend (system WebKit, no extra install) |
| Host / frontend | ✅ | Fully managed, cross-platform |
| LLamaSharp | ✅ | `LLamaSharp.Backend.Cpu` supports macOS (incl. Apple Silicon); swap to `LLamaSharp.Backend.Metal` for GPU |
| SQLite relational storage | ✅ | `Microsoft.Data.Sqlite` (SQLitePCLRaw e_sqlite3), cross-platform |
| sqlite-vec vectors | ⚠️ Optional | Official build does not ship a macOS extension; download `libvec0.dylib` yourself (sqlite-vec releases → loadable-macos-aarch64) and place it in the app directory; when missing it automatically degrades to "store vectors as BLOB + in-memory cosine retrieval", functionally equivalent |
| Chat model | Needs network | Default `deepseek` (configure `DEEPSEEK_API_KEY`); `mock` works fully offline (no AI reply) |

## Directory Structure

```
src/AguiGroupChat.Desktop.Core/   # Shared host (fully managed, no UI): DesktopApp.Start assembles Kestrel
src/AguiGroupChat.Desktop.Cross/  # This shell: Program.cs + App.axaml + MainWindow.axaml (NativeWebView)
```

## Platform Selection (Important)

| Platform | Recommended version | Embedding | Description |
|---|---|---|---|
| **Windows** | `src/AguiGroupChat.Desktop` (WPF) | WebView2 | Mature and stable; recommended for Windows users |
| **macOS / Linux** | This version (Avalonia) | WKWebView / WebKitGTK | System WebView components, official control support |
| Windows running this version | Available (auto-degrade) | WebView2 adapter | The official WebView control's Windows WebView2 adapter may fail to initialize in some environments (`E_ACCESSDENIED`, etc.), **and then it automatically opens in the system browser** |

> Avalonia 11 (including mature controls like ChisterWu WebView.Avalonia / OutSystems WebViewControl) fails to start
> `Dispatcher.MainLoop` under this project's .NET 10 runtime (`PlatformNotSupportedException`), so the cross-platform shell uses
> Avalonia 12 + the official WebView control; for Windows embedding please use the WPF version.

## Known Limitations

- **Embedded WebView auto-degrade**: on Windows, if the official Avalonia WebView control (12.1) WebView2 adapter fails to initialize
  (WebView2 Runtime missing / enterprise policy / restricted permissions), the app **automatically opens the local address in the system browser** (hinted in the top status bar),
  with identical functionality; macOS / Linux use system WKWebView / WebKitGTK and have no such issue. You can switch manually at any time with "🌐 Open in browser".
- macOS / Linux WebView behavior is based on system components and differs subtly from Windows WebView2 (file upload dialogs,
  clipboard permissions); if fine-grained handling is needed, adapt per platform in `MainWindow`;
- This repository was compiled and run-verified on Windows; on macOS / Linux run `dotnet build` and then run on the corresponding system
  (the backend and managed layers have no platform dependency; risk lies mainly in differences among system WebView components).
