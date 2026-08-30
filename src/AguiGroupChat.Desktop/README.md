# AG-UI 群聊桌面版

[English](README.en.md) | **简体中文**

纯桌面应用（Windows）：复用 Web 版全部功能（群聊、智能体、人机交互审批、语义记忆 RAG、
个人记忆、AI 分身、附件、话题、触发方式等），但**数据与模型全部在本机**：

- **数据库**：SQLite（`data/agui.sqlite`），语义记忆用 **sqlite-vec**（`vec0` 向量虚拟表，`libs/vec0.dll` 已随附）
- **向量模型**：**LLamaSharp**（llama.cpp 的 .NET 实现）本地加载 GGUF embedding 模型，全程离线
- **UI**：WPF + WebView2 窗口，内嵌 Kestrel 复用现有 Web 前端（不依赖外部浏览器）

## 快速开始

```bash
# 1.（可选）准备本地 embedding 模型 bge-m3（1024 维，models/embedding.gguf，约 605MB）：
#    - 模型文件**不随源码仓库分发**（体积大、超 GitHub 单文件限制），首次启动若缺失会自动提示；
#    - 先用脚本下载：powershell -ExecutionPolicy Bypass -File tools/download-embedding-model.ps1
#      或配置 `Agents:Memory:ModelDownloadUrl`（gguf 直链），首次启动自动下载；
#    - 也可自行放置任一 GGUF embedding 模型为 models/embedding.gguf，并同步改
#      appsettings 的 EmbeddingDimensions（nomic-embed-text=768，bge-m3=1024）

# 2. 构建并运行
#    说明：发行版 MSI 已含模型；从源码构建则需先完成第 1 步下载模型
#    避免自行下载时：可先把 `Agents:Memory:Provider` 改为 http（外部 embedding 端点）
dotnet build src/AguiGroupChat.Desktop/AguiGroupChat.Desktop.csproj
dotnet run --project src/AguiGroupChat.Desktop/AguiGroupChat.Desktop.csproj
```

启动后弹出桌面窗口，多实例共享同一后端进程：本地服务**固定运行在 `http://127.0.0.1:5200`**（第一个实例自动拉起 `--backend` 子进程）。若 5200 被其他程序占用，首次启动会提示「请关闭占用端口的程序后重试」。
首次使用注册一个账号即可。

> **关于模型（重要）**：本地 embedding 模型 `bge-m3-Q8_0`（约 605MB，1024 维）**不随源码仓库分发**（体积超过 GitHub 单文件 100MB 限制，已从 git 中排除）。
> 发行版 **MSI 安装包仍已内置该模型**（安装后即用、全程离线）；**从源码 clone 自行构建**时需先下载模型：
> - 运行 `tools/download-embedding-model.ps1`，或
> - 配置 `Agents:Memory:ModelDownloadUrl`（gguf 直链，Hugging Face / ModelScope），首次启动自动下载到 `models/`（安装目录不可写时落到 `%LocalAppData%\AguiGroupChat\models`）。
> MSI 为 perUser 安装（`%LocalAppData%\AguiGroupChat`，免管理员、目录可写），数据与模型直接落在安装目录。
> 模型缺失时应用会**自动降级禁用语义记忆**并记日志，群聊等其余功能不受影响（LLamaSharp 加载失败的兜底行为）。

## 配置（appsettings.json）

| 配置 | 默认 | 说明 |
|---|---|---|
| `Storage:Provider` | `sqlite` | 固定 SQLite；`ConnectionString` 可改路径 |
| `Agents:Provider` | `deepseek` | 对话模型：`mock`（无密钥）/ `deepseek` / `openai`（兼容端点） |
| `Agents:ApiKey` | - | 对话模型密钥（`mock` 不需要）；也读环境变量 `DEEPSEEK_API_KEY` |
| `Agents:EnableTools` | `true` | 工具调用：calculator 计算 / unit_converter 换算 / group_memory_search 记忆检索 / read_attachment 读附件 / publish_announcement 公告（需审批） |
| `Agents:EnableWebTools` | `false` | 网络工具：web_search（搜索网页）/ read_url（读网页，防内网 SSRF）；开启需外网 |
| `Agents:AllowPrivateSkillEndpoints` | `false` | 技能库 HTTP 是否放行<b>本机 / 内网 / 私网</b>地址（默认关=保留 SSRF 防护）；确需调用本机 / 内网接口（如本地 Ollama / 内网 API）时置 `true` |
| `Agents:CoordinatorPlanning` | `false` | 确定性编排计划：开启后路由型数字员工（配了指派白名单/提升目标，或<b>挂了技能</b>）收到问题时，先按<b>组织架构与技能配置</b>生成一张执行计划，再<b>依次激活</b>对应员工/技能并聚合答复；计划内的<b>客户端执行技能会合并成一张「本机一键执行全部」卡</b>，综合阶段<b>可递归补查</b>直到信息充分（不会中途问“要不要继续”）；计划失败自动回退到原有递归指派 |
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

**长文本自动分段**：本地 llama embedding 对超长文本（如大型知识库文档切片）会按模型 context 自动切成多段分别向量化再取平均（分段字符/token 比值取 0.9，留有安全余量），避免超 context 导致向量化失败（1.0.78 修复）。长文档上传知识库不受长度限制。

## 目录结构

```
src/AguiGroupChat.Desktop/
├── Program.cs            # 组合根：内嵌 Kestrel（复用 Hub + 网关 + API + 前端）+ WPF 窗口
├── MainWindow.xaml(.cs)  # WebView2 窗口
├── appsettings.json      # 桌面配置（sqlite + 本地 llama embedding）
├── models/               # 本地 embedding 模型：embedding.gguf（bge-m3-Q8_0，1024 维，约 605MB）
│                        # 不入源码仓库，见上方「关于模型」——下载脚本或 ModelDownloadUrl 获取
├── libs/vec0.dll         # sqlite-vec 原生扩展（Windows x86_64）
└── data/                 # 运行时生成：agui.sqlite + uploads/
```

## 说明

- 桌面版与 Web 版共用同一套协议 / Hub / 网关 / 前端代码，功能一致；
- 智能体触发、人机交互审批卡片、分身、附件、话题、私密群等全部可用；
- **技能（Skills，两层含义）**：① 把<b>其他数字员工</b>挂为可调用子代理（“技能与知识 → 可调用子数字员工”）——模型需要其领域能力时会自动调起子数字员工并引用其答复（含 AG-UI 桥接外部专家）；上下层数字员工如此接钩后，上层需要下层能力时即可触发调用。② 在「技能库」手动配置 shell / http / prompt 三类可复用技能，数字员工经 `skillDefIds` 挂载调用。HTTP / 提示词技能在表单里可<b>关闭“需审批”以自动调用</b>（需访问本机/内网时另置 `Agents:AllowPrivateSkillEndpoints=true`）；
  shell 技能因可执行任意命令而<b>始终强制需审批</b>；智能体也可经 `create_skill` 工具（需审批）自建技能。技能库还支持 <b>🤖 用自然语言生成技能</b>（输入需求即可让大模型填好命令/描述/执行位置等，再微调保存）；
- **客户端执行技能（`ExecutionLocation=Client`）**：技能可标记为<b>在本机执行</b>——桌面版由桌面壳（本机进程）在沙箱内执行 shell 或由 WebView 执行 http，结果回传模型继续作答（桌面版<b>无需额外配置本机桥/令牌</b>，开箱即用）。编排计划选中的多个客户端技能会合并成「本机一键执行全部」卡，一次确认后逐个执行。
- **知识库（Knowledge Base）**：智能体管理里可创建知识库并上传文档（Word / Excel / PPT / PDF / 文本），
  回复前自动检索知识文档相关内容注入（RAG），让智能体基于您的资料作答；文档向量与语义记忆共用一套存储（sqlite-vec + 本地 bge-m3）；
- **数字员工组织架构**：顶栏「🌐 组织架构」画布拖拽连线配置各角色的<b>任务指派（向下）/ 问题提升（向上）</b>；节点右上角「**优化指派**」图标按钮可按该角色的<b>直接下一层</b>自动生成「管理下一层任务指派」提示词（只看下一层挑下游、不越级），预览后可追加到其 Instructions；
- **VC++ 运行库已捆绑**（`vcruntime140.dll` / `vcruntime140_1.dll` / `msvcp140.dll` 随 MSI 装在 exe 旁，
  app-local 部署，目标机无需预装）。若个别机器 LLamaSharp 原生库仍加载失败（如缺失的旧系统库），
  应用会自动降级禁用语义记忆并记日志，**不会崩溃**，群聊等其余功能不受影响；
- WebView2 运行库：Windows 10/11 通常已内置；缺失时从
  [Microsoft Edge WebView2](https://developer.microsoft.com/microsoft-edge/webview2/) 安装。
