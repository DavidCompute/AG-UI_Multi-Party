# AG-UI 群聊桌面版

纯桌面应用（Windows）：复用 Web 版全部功能（群聊、智能体、人机交互审批、语义记忆 RAG、
个人记忆、AI 分身、附件、话题、触发方式等），但**数据与模型全部在本机**：

- **数据库**：SQLite（`data/agui.sqlite`），语义记忆用 **sqlite-vec**（`vec0` 向量虚拟表，`libs/vec0.dll` 已随附）
- **向量模型**：**LLamaSharp**（llama.cpp 的 .NET 实现）本地加载 GGUF embedding 模型，全程离线
- **UI**：WPF + WebView2 窗口，内嵌 Kestrel 复用现有 Web 前端（不依赖外部浏览器）

## 快速开始

```bash
# 1.（无需准备）已捆绑本地 embedding 模型 bge-m3（1024 维，models/embedding.gguf），开箱即用、全程离线。
#    如需自行更换模型：把任一 GGUF embedding 模型重命名为 embedding.gguf 放入 models/ 目录，
#    并同步改 appsettings 的 EmbeddingDimensions（nomic-embed-text=768，bge-m3=1024）

# 2. 构建并运行
dotnet build src/AguiGroupChat.Desktop/AguiGroupChat.Desktop.csproj
dotnet run --project src/AguiGroupChat.Desktop/AguiGroupChat.Desktop.csproj
```

启动后弹出桌面窗口，多实例共享同一后端进程：本地服务**固定运行在 `http://127.0.0.1:5200`**（第一个实例自动拉起 `--backend` 子进程）。若 5200 被其他程序占用，首次启动会提示「请关闭占用端口的程序后重试」。
首次使用注册一个账号即可。

> **模型已捆绑**：bge-m3-Q8_0（约 605MB，1024 维）随安装包一并分发，MSI 因此较大（数百 MB）。
> 如需瘦身：删除 models/ 目录并配置 `Agents:Memory:ModelDownloadUrl`（直链），首次启动自动下载。
> MSI 为 perUser 安装（`%LocalAppData%\AguiGroupChat`，免管理员、目录可写），数据与模型直接落在安装目录。

## 配置（appsettings.json）

| 配置 | 默认 | 说明 |
|---|---|---|
| `Storage:Provider` | `sqlite` | 固定 SQLite；`ConnectionString` 可改路径 |
| `Agents:Provider` | `deepseek` | 对话模型：`mock`（无密钥）/ `deepseek` / `openai`（兼容端点） |
| `Agents:ApiKey` | - | 对话模型密钥（`mock` 不需要）；也读环境变量 `DEEPSEEK_API_KEY` |
| `Agents:EnableTools` | `true` | 工具调用：calculator 计算 / unit_converter 换算 / group_memory_search 记忆检索 / read_attachment 读附件 / publish_announcement 公告（需审批） |
| `Agents:EnableWebTools` | `false` | 网络工具：web_search（搜索网页）/ read_url（读网页，防内网 SSRF）；开启需外网 |
| `Agents:Memory:Enabled` | `true` | 语义记忆开关 |
| `Agents:Memory:Provider` | `llama` | embedding 提供方：`llama`（本地 LLamaSharp）/ `http`（OpenAI 兼容端点） |
| `Agents:Memory:LlamaModelPath` | `models/embedding.gguf` | 本地 GGUF 模型路径；缺失时也自动探测 `%LocalAppData%\AguiGroupChat\models\embedding.gguf`（老版本 perMachine 安装场景的兼容回退） |
| `Agents:Memory:ModelDownloadUrl` | 空 | 模型直链（如 Hugging Face / ModelScope 的 gguf 文件）；配置后首次启动自动下载（安装目录不可写时落到 %LocalAppData%\AguiGroupChat\models） |
| `Agents:Memory:EmbeddingDimensions` | `1024` | **须与模型一致**（捆绑模型 bge-m3=1024；nomic-embed-text=768 需改为 768） |
| `Agents:Memory:LlamaThreads` | `4` | 本地推理线程数（CPU 桌面推荐 4~8） |
| `GroupChat:SeedSampleData` | `true` | 首次启动播种示例群 / 成员 / 智能体 |

> 对话模型（DeepSeek 等）仍需联网；**语义记忆 / 群聊 / 智能体管理**等核心功能离线可用。

## 语义记忆（RAG）工作原理

1. 消息落库 → `AgentMessageMemory` 经 `LlamaEmbeddingProvider`（LLamaSharp 本地加载模型）向量化
2. 向量写入 sqlite-vec `vec0` 虚拟表（`agui_message_memory_vec`），元数据写入 `agui_message_memory`
3. 智能体回复前检索相似记忆（余弦相似度，`TopK=5`，`MinScore=0.25`），经
   `MemoryContextProvider` 注入提示词；范围 `agent` = 该智能体在所有群里的记忆
4. 个人记忆：用户 / 智能体开启后，回复时检索其本人历史发言（私密群隔离）

**降级路径**：`libs/vec0.dll` 缺失或加载失败时，自动降级为「向量存 BLOB + .NET 内存余弦检索」，
功能等价（大数据量下性能低于向量索引）。日志会提示当前模式。

## 目录结构

```
src/AguiGroupChat.Desktop/
├── Program.cs            # 组合根：内嵌 Kestrel（复用 Hub + 网关 + API + 前端）+ WPF 窗口
├── MainWindow.xaml(.cs)  # WebView2 窗口
├── appsettings.json      # 桌面配置（sqlite + 本地 llama embedding）
├── models/               # 捆绑的本地 embedding 模型：embedding.gguf（bge-m3-Q8_0，1024 维，约 605MB）
├── libs/vec0.dll         # sqlite-vec 原生扩展（Windows x86_64）
└── data/                 # 运行时生成：agui.sqlite + uploads/
```

## 说明

- 桌面版与 Web 版共用同一套协议 / Hub / 网关 / 前端代码，功能一致；
- 智能体触发、人机交互审批卡片、分身、附件、话题、私密群等全部可用；
- **技能（Skills）**：智能体管理里可为角色配置技能，把其他智能体（含 AG-UI 桥接外部专家）挂为可调用子代理，
  模型需要该领域信息时自动调起子智能体并引用其答复；智能体也可经 `create_skill` 工具（需审批）自建技能；
- **知识库（Knowledge Base）**：智能体管理里可创建知识库并上传文档（Word / Excel / PPT / PDF / 文本），
  回复前自动检索知识文档相关内容注入（RAG），让智能体基于您的资料作答；文档向量与语义记忆共用一套存储（sqlite-vec + 本地 bge-m3）；
- **VC++ 运行库已捆绑**（`vcruntime140.dll` / `vcruntime140_1.dll` / `msvcp140.dll` 随 MSI 装在 exe 旁，
  app-local 部署，目标机无需预装）。若个别机器 LLamaSharp 原生库仍加载失败（如缺失的旧系统库），
  应用会自动降级禁用语义记忆并记日志，**不会崩溃**，群聊等其余功能不受影响；
- WebView2 运行库：Windows 10/11 通常已内置；缺失时从
  [Microsoft Edge WebView2](https://developer.microsoft.com/microsoft-edge/webview2/) 安装。
