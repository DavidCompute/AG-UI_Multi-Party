# AG-UI 群聊桌面版（跨平台：Avalonia）

[English](README.en.md) | **简体中文**

同一套桌面应用的另一 UI 壳：**Avalonia 12**（跨平台 XAML）+ 官方 **WebView 控件**
（`Avalonia.Controls.WebView` / `NativeWebView`）——Windows 走 WebView2、**macOS 走 WKWebView**、
Linux 走 WebKitGTK。当前版本 **1.0.104**（见 `src/AguiGroupChat.Desktop.Cross/AguiGroupChat.Desktop.Cross.csproj`）。功能与 Windows 版（`src/AguiGroupChat.Desktop`）一致，共享
`src/AguiGroupChat.Desktop.Core`（SQLite + sqlite-vec 记忆、LLamaSharp 本地 embedding）。注意**进程模型不同**：本版为
**单实例进程内**宿主（`DesktopApp.Start` 直接进程内起 Kestrel）；Windows 版是「UI 实例 + `--backend` 后端子进程」的多实例共享架构。

## 运行

```bash
# 1.（可选）准备本地 embedding 模型 bge-m3（models/embedding.gguf，1024 维，约 605MB）：
#    模型文件不随源码仓库分发（超 GitHub 单文件限制）；从源码构建需先下载：
#      powershell -ExecutionPolicy Bypass -File tools/download-embedding-model.ps1
#    或配置 Agents:Memory:ModelDownloadUrl 首次启动自动下载；更换模型需同步改 appsettings 的 EmbeddingDimensions
# 2. 构建并运行（Windows / macOS / Linux 同一命令）
dotnet run --project src/AguiGroupChat.Desktop.Cross
```

启动后弹出 Avalonia 窗口，本地服务运行在 `http://127.0.0.1:5200`（该端口被占用时由系统随机分配一个空闲端口），
首次使用注册账号即可。功能与 Windows 版完全一致：群聊、智能体、人机交互审批、
语义记忆 RAG、个人记忆、AI 分身、附件、话题、触发方式、AG-UI 桥接等。

## macOS 说明

| 组件 | 状态 | 说明 |
|---|---|---|
| UI 壳 | ✅ WKWebView | Avalonia 官方 WebView 控件 macOS 后端（系统 WebKit，无需额外安装） |
| 宿主 / 前端 | ✅ | 纯托管，跨平台 |
| LLamaSharp | ✅ | `LLamaSharp.Backend.Cpu` 支持 macOS（含 Apple Silicon）；想用 GPU 可换 `LLamaSharp.Backend.Metal` |
| SQLite 关系存储 | ✅ | `Microsoft.Data.Sqlite`（SQLitePCLRaw e_sqlite3）跨平台 |
| sqlite-vec 向量 | ⚠️ 可选 | 官方未随附 macOS 扩展，可自行下载 `libvec0.dylib`（sqlite-vec releases → loadable-macos-aarch64）放入应用目录；缺失时自动降级为「向量存 BLOB + 内存余弦检索」，功能等价 |
| 对话模型 | 需联网 | 默认 `deepseek`（配 `DEEPSEEK_API_KEY`）；`mock` 可完全离线（无 AI 回复） |

## 目录结构

```
src/AguiGroupChat.Desktop.Core/   # 共享宿主（纯托管，无 UI）：DesktopApp.Start 组装 Kestrel
src/AguiGroupChat.Desktop.Cross/  # 本壳：Program.cs + App.axaml + MainWindow.axaml（NativeWebView）
```

## 平台选型（重要）

| 平台 | 推荐版本 | 内嵌方式 | 说明 |
|---|---|---|---|
| **Windows** | `src/AguiGroupChat.Desktop`（WPF） | WebView2 | 成熟稳定，推荐 Windows 用户使用 |
| **macOS / Linux** | 本版（Avalonia） | WKWebView / WebKitGTK | 系统 WebView 组件，官方控件支持 |
| Windows 跑本版 | 可用（自动降级） | WebView2 适配器 | 官方 WebView 控件的 Windows WebView2 适配器在部分环境初始化失败（`E_ACCESSDENIED` 等），**会自动用系统浏览器打开** |

> Avalonia 11（含 ChisterWu WebView.Avalonia / OutSystems WebViewControl 等成熟控件）在本项目
> .NET 10 运行时下 `Dispatcher.MainLoop` 启动失败（`PlatformNotSupportedException`），故跨平台壳采用
> Avalonia 12 + 官方 WebView 控件；Windows 内嵌请用 WPF 版。

## 已知限制

- **内嵌 WebView 自动降级**：Windows 上若 Avalonia 官方 WebView 控件（12.1）的 WebView2 适配器初始化失败
  （WebView2 Runtime 缺失 / 企业策略 / 权限受限），应用会**自动用系统浏览器打开本地地址**（顶部状态栏提示），
  功能完全一致；macOS / Linux 走系统 WKWebView / WebKitGTK，无此问题。可用「🌐 浏览器打开」随时手动切换。
- macOS / Linux 的 WebView 行为基于系统组件，与 Windows WebView2 存在细微差异（文件上传对话框、
  剪贴板权限），如需精细化处理可在 `MainWindow` 中按平台适配；
- 本仓库在 Windows 上完成编译与运行验证，macOS / Linux 需在对应系统 `dotnet build` 后运行
  （后端与托管层无平台依赖，风险主要在系统 WebView 组件的差异）。
