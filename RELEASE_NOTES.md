# 部署栈变更：语义记忆 embedding 换用 llama.cpp（Docker / Server 面，未随桌面版号发布）
# Deployment-stack change: embeddings now served by llama.cpp (Docker / Server side, outside the desktop release numbering)

**版本说明**：Docker 编排里提供语义记忆 embedding 的服务由 **Ollama 换成 llama.cpp 官方 server**（新服务名 `llama-embed`，容器内 `http://llama-embed:8080/v1`）。同机、同模型（bge-m3）、同核**配对实测**：短文本快 2~3.6 倍，检索/写入上限长度（500/800 字）快约 1.3 倍；两个引擎产出的向量**完全一致**（cosine=1.000000、1024 维、L2 归一化），**已入库记忆无需重算**。
**Version note**: the semantic-memory embedding service in the Docker orchestration moves from **Ollama to the official llama.cpp server** (new service `llama-embed`, reachable inside compose at `http://llama-embed:8080/v1`). Paired measurement on one machine, same model (bge-m3), same cores: 2–3.6x faster on short inputs and ~1.3x at the retrieval/write caps (500/800 chars); both engines produce **identical vectors** (cosine 1.000000, 1024 dims, L2-normalized), so **no stored memory needs re-embedding**.

## 为什么 / Why

- Ollama 0.32 的推理引擎**本身就是内置的 `llama-server` 子进程**（容器内 `ps` 可见），所以差距不在指令集，而在每个请求都要走 `HTTP → Go → HTTP` 中转：实测多约 **350~450ms 固定开销** + 约 **1.5x** 吞吐损耗。
- 中位延迟（输入 6/50/200/500/800 字）：**Ollama 382/635/1708/3973/6409ms → llama.cpp 107/288/978/2978/4869ms**。
- **English**: Ollama 0.32's inference engine **is an internal `llama-server` subprocess**, so the gap is not the instruction set but the extra `HTTP → Go → HTTP` hop per request (~350–450 ms fixed plus ~1.5x throughput). Median latency (6/50/200/500/800 chars): **382/635/1708/3973/6409 ms → 107/288/978/2978/4869 ms**.

## 三个必须显式给的参数 / Three flags that must be explicit

| 参数 Flag | 为什么 Why |
|---|---|
| `-b 2048 -ub 2048` | `--embeddings` 会把 `n_batch/n_ubatch` **强制降到 512**，≥500 字的输入直接 `HTTP 500 (input is too large to process)`。English: embeddings clamp the physical batch to 512, so 500+ char inputs fail with HTTP 500 |
| `-t N` | **并非越多越快**：4 核机器上 6 线程比 4 线程慢 30%~2 倍；12 核机器上 6 线程优于 12 线程。默认 6，4 核请设 `LLAMA_EMBED_THREADS=4`。English: threads must match cores — oversubscription measured 30%–2x slower |
| `--flash-attn off` | 非因果（BERT 类）编码器上关掉 flash attention，短文本快 2~3 倍（**对 Ollama 无效**，这也是先前把根因找错方向的原因）。English: 2–3x faster on short inputs for a non-causal encoder; setting it on Ollama changes nothing |

## 配置面变更 / Config surface

- 新服务 `llama-embed`（`ghcr.io/ggml-org/llama.cpp:server`，官方多变体 ggml 构建，实测加载 `libggml-cpu-alderlake.so`，与 Ollama 同款内核；**按 digest 钉死**，因为上游给的是 dev 构建）+ 健康检查；`web` 依赖它 `service_healthy`——模型不能时就**不会带着坏掉的记忆服务启动**。
- 模型改为挂载宿主机**目录** `./models:/models:ro`（不是单个文件：文件级 bind mount 在源文件缺失时会被 Docker 建出一个同名**目录**，而下载脚本会把那个目录当成“模型已存在”而什么都不下 → 无解死锁，实测踩到）。首次部署跑 `powershell -File tools/download-embedding-model.ps1 -OutDir models`（约 605MB，不入仓库，详见新增的 `models/README.md`）。
- 容器入口加了**模型闸门**：**缺失**或**过小**（<400MB，即不是 bge-m3/1024 维）都打印可照做的下一步后退出——把「换错模型 → 向量维度不匹配 → RAG 静默失效」挡在启动前（与 `tools/download-embedding-model.ps1` / CI 的尺寸护栏同口径）。
- `docker-compose.yml` / `.env.example`：`OLLAMA_PORT` → `LLAMA_EMBED_PORT`；新增 `LLAMA_EMBED_THREADS`（默认 **4**；4 核上 6 线程反而慢 30%~2 倍，≥8 核可提到 6）与 `LLAMA_EMBED_IMAGE`（跟进上游用）；`MEMORY_EMBEDDING_ENDPOINT` 默认 → `http://llama-embed:8080/v1`；`NO_PROXY` 增补 `llama-embed` 与 `host.docker.internal`（后者用于自备宿主机 Ollama）；启动参数 `--no-webui` → `--no-ui`（前者已被上游标记弃用）；删除 `agui-ollama-data` 卷声明。
- **升级动作**：`git pull` → 放好 `./models/embedding.gguf` → `docker compose up -d --build --remove-orphans`（旧的 `agui-ollama-data` 卷可删）。
  ⚠️ **`--remove-orphans` 不能省**：旧 `agui-group-chat-ollama` 容器仍占着宿主 11435，不清理会让 `llama-embed` 以 `port is already allocated` 起不来，而 `web` 依赖它健康 → **整套服务起不来**（表现为升级后 5200 打不开）。
- **回退**：只改 `MEMORY_EMBEDDING_ENDPOINT` 指回任意 OpenAI 兼容端点（含自建 Ollama `ollama serve`）即可，两端向量语义一致。
- **English**: new `llama-embed` service (official llama.cpp image, **pinned by digest** — upstream ships dev builds) with a health check gating `web`; the model is a host **directory** bind mount `./models:/models:ro` (a file-level mount makes Docker create a same-named directory when the file is missing, which the download script then mistakes for an existing model); the entrypoint now rejects a **missing or too-small** (<400MB) model with an actionable message instead of letting a dimension mismatch degrade into "RAG silently fails"; `OLLAMA_PORT` → `LLAMA_EMBED_PORT`, new `LLAMA_EMBED_THREADS` (default **4**) and `LLAMA_EMBED_IMAGE`, new default endpoint, `NO_PROXY` now includes `llama-embed` and `host.docker.internal`, `--no-webui` → `--no-ui`. **Upgrade**: `git pull`, place the GGUF, then `docker compose up -d --build --remove-orphans` — `--remove-orphans` is required because the old Ollama container still holds port 11435 and would otherwise break the whole stack. Rollback: repoint `MEMORY_EMBEDDING_ENDPOINT` at any OpenAI-compatible endpoint.

## 一并记录的否定结论 / A negative result worth recording

把 WSL（`.wslconfig`）从 4 核/8GB 放开到 12 核/12GB：embedding **没有变快**（500 字 4035 → 4676ms），同时宿主机可用内存从 9.7GB 降到 6.7GB、测量方差明显变大。embedding 是**内存带宽/同步受限**的短序列工作，不是核数受限，因此已回滚，不作为发布内容。
Enlarging WSL to 12 cores/12GB did **not** speed embeddings up (500 chars: 4035 → 4676 ms) while cutting host free memory from 9.7 GB to 6.7 GB and inflating variance; embeddings are bandwidth/synchronization-bound short-sequence work, not core-bound, so the change was reverted.

---

# AG-UI 群聊桌面版 1.0.165 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.165 Release Notes (current Windows desktop release)

**版本说明**：1.0.165 修“**说已出稿、却拿不到文件**”这类交付断链：① 恢复路径崩溃时**先回挂已产出的产物**；② 一轮多个审批请求**全部收下、全部回应**；③ 校验器不再把「读 / 自检 / 改既有稿」当成“缺 slides”拒掉；④ 被拒原因落日志。
**Version note**: 1.0.165 fixes delivery being lost while the reply claims success: (1) the resume path now **re-attaches already-produced artifacts before failing**; (2) multiple approval requests in one turn are **all collected and all answered**; (3) the validator no longer rejects “read / qa / edit an existing deck” as a missing `slides` payload; (4) rejection reasons are now logged.

## 交付不再断链（1.0.165）
# Delivery no longer breaks mid-flight (1.0.165)

中文：
- **现场**：用户说“领导不喜欢黑色背景”→ 模型确实生成了浅色版（`/app/docs` 里躺着 `…-浅底商务版-26页-骨架稿v1.pptx`、`…v9-浅底商务版-26页.pptx`），但那条回复**一个附件都没挂**；再往前一步，恢复请求被模型以 `HTTP 400 The reasoning_content in the thinking mode must be passed back to the API.` 拒掉，运行中断。
- **根因一（用户能感知的那个）**：恢复路径的 **catch 分支不收产物**——只“空正文兑底 + 结束消息”，而同方法的成功分支、以及其它终止路径（如审批轮数超限）都会先 `AttachPublishedProductsAsync`。于是“生成成功但没挂上”= 用户白跑。**现在 catch 分支先回挂产物再收尾**，与其它收尾路径同一原则：先保证用户能拿到东西。
- **根因二（触发那条 400 的那一步）**：一个模型轮次里提出了**两个**需审批的工具调用，而网关只弹一张卡、只回一条 `ToolApprovalResponseContent`，留下无人回应的审批请求。日志里**三次** 400（08:25、00:16、00:27）全部紧跟“一轮两条审批”的那一步，而单审批的恢复从未失败。**现在一轮的审批全部收下、全部回应**（仍是一张卡、一个决定作用于全组，卡文会写明“本轮共 N 项待确认”并把工具名列清楚）。
- **根因三（“读不了/改不了旧稿”）**：文档技能入参校验要求 `slides` 非空，对 `action=read` / `qa` / `edit` **也**照样拒绝（而 xlsx 对 `action=analyze` 是有豁免的）。用户要“改主题”时模型自然用 `edit`，被拒后收到“请把内容整理成 slides 数组”的误导提示，反复重试到自述“我没有文件读取能力”。**现在这三个 action 一律放行**（真正“从零生成”仍必须有 slides）。
- **可诊断性**：校验失败时日志补上 `reason=`（原先只有“校验未通过”，真因只在回给模型的提示里）；恢复崩溃后的产物回挂也会记一条 `恢复失败但已回挂产物：N 个附件`。
- **回归**：新增 `HitlMultiApprovalTests`（一轮两条审批：卡片写明 2 项、恢复后正常运行）与 pptx 校验的 action 豁免用例；全量 **1555 通过 / 0 失败**（新增用例后总数见下）。

English:
- **What happened**: after “the boss does not like dark backgrounds” the model really did produce a light deck (`…-浅底商务版-26页-骨架稿v1.pptx` and `…v9-浅底商务版-26页.pptx` were sitting in `/app/docs`), yet the reply carried **no attachment at all**; one step earlier the resume request had been rejected with `HTTP 400 The reasoning_content in the thinking mode must be passed back to the API.` and the run was torn down.
- **Cause 1 (the one users notice)**: the resume path's **catch branch never collected artifacts** — it only stamped an empty-body fallback and ended the message, while the success branch and every other teardown path (e.g. the approval-round limit) attach products first. “Generated but never attached” means the user lost the work. **The catch branch now re-attaches produced artifacts before ending.**
- **Cause 2 (the step that triggered the 400)**: one model turn asked for **two** approvals, but the gateway showed a single card and answered only one of them, leaving an approval request with no response. All **three** 400s in the log (08:25, 00:16, 00:27) immediately follow a two-approval turn, while every single-approval resume succeeded. **One turn's approvals are now all collected and all answered** (still one card, one decision applied to the whole group, and the card says “N items pending” and lists the tool names).
- **Cause 3 (“cannot read or change the old deck”)**: the document-skill input validator demanded a non-empty `slides` array and applied that to `action=read` / `qa` / `edit` too (while xlsx already exempts `action=analyze`). Asked to change the theme, the model naturally calls `edit`, gets rejected with a misleading “please put your content into a slides array”, retries and eventually tells the user it has no file access. **Those three actions are now allowed through** (real generation still requires slides).
- **Diagnosability**: validation failures now log `reason=` (previously only “validation failed”, with the real reason visible only to the model), and the crash-path re-attachment logs `恢复失败但已回挂产物：N 个附件`.
- **Tests**: new `HitlMultiApprovalTests` (one turn, two approvals: the card states 2 items and the resume completes) plus pptx action-exemption cases.

---

### 上一版 / Previous release

# AG-UI 群聊桌面版 1.0.164 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.164 Release Notes (current Windows desktop release)

**版本说明**：1.0.164 把**配图来源统一为「团队图库」这一个来源**——联网取图（Wikimedia Commons）**暂时下线**，默认彻底不出网。
**Version note**: 1.0.164 makes the **team image library the single image source** — web image search (Wikimedia Commons) is **temporarily retired** and the default is fully offline.

## 配图只走图库（1.0.164）
# Illustration goes library-only (1.0.164)

中文：
- **改了什么**：`pptx_deck` 的 `imageSource` 默认从 `auto` 改为 **`library`**（入参与环境变量 `AGUI_IMAGE_SOURCE` 都未给时生效）：只做团队图库语义检索，未命中就**降级为自动题图**并在 `warnings` 里说明，**不再回落网络**。整条网络链路（`auto` / `network`）与端点覆盖（入参 `imageSearchApi`、环境变量 `AGUI_PHOTO_API`）**全部保留**，恢复只需改一个值，不必改代码。
- **为什么**：联网取图带来三类成本——① **版权 / 合规**（外图必须署名，出稿会多一页「图片来源」）；② **可达性**（内网 / 部分网络下 Wikimedia 不可达，每次取图白等一轮超时，实测 4 张配图 45 秒）；③ **画不对题**（关键词置信度不够时会配上不相干的网图）。图库是**企业自有素材**：无需署名、不出网、语义命中更贴业务。
- **安全侧默认**：`AGUI_IMAGE_SOURCE` 写错成没见过的值时**也按 `library` 处理**——出网必须是显式开启的能力，不能靠拼写意外打开。
- **不静默**：因未开启联网而配不到图时，降级原因里会写「联网取图已下线（默认只查团队图库），未联网检索」，用户能从 `warnings` 看出原因。
- **模型提示词同步更新**：工具描述、技能正文（`tools/pptx-skills/pptx_deck.cs` 及其内置副本）与各技能 README 全部改成“只从图库配图”，并**明确要求模型不要向用户暗示会拿到网图 / 实景照片**。
- **配置面同步**：`docker-compose.yml` 与 `.env.example` 的默认值改为 `library`（注释里写清恢复方式）。
- **回归**：`PptxDeckSkillTests` **125 通过 / 0 失败**，其中新增一项**钉住“默认不出网”**：即便调用方把检索端点也给了，也不得发起任何网络请求（`Searches == 0`），且降级要如实说明原因；原本验证网络路径的用例改为**显式** `imageSource=auto`。

English:
- **What changed**: `pptx_deck`'s `imageSource` now defaults to **`library`** instead of `auto` (applies when neither the input field nor `AGUI_IMAGE_SOURCE` is set): only the team image library is searched, a miss **degrades to generated art** with an explanatory `warnings` entry, and **there is no network fallback**. The whole network path (`auto` / `network`) and the endpoint overrides (input `imageSearchApi`, env `AGUI_PHOTO_API`) are **kept intact**, so restoring it is a one-value change rather than a code change.
- **Why**: web photos carried three costs — (1) **licensing/compliance** (external photos must be credited, adding an “Image credits” page); (2) **reachability** (Wikimedia is unreachable on intranets and some networks, burning a wasted timeout per lookup — measured: 4 illustrations took 45 s); (3) **wrong subject** (a low-confidence keyword match can attach an irrelevant stock photo). The library holds **your own assets**: no attribution, no egress, and semantics that match the business.
- **Safe-by-default**: an unrecognised `AGUI_IMAGE_SOURCE` value is treated as `library` too — egress must be an explicit capability, never something a typo can switch on.
- **Never silent**: when a photo is missing because web search is off, the degradation reason says so (“联网取图已下线（默认只查团队图库），未联网检索”), visible in `warnings`.
- **Prompts updated**: the tool description, the skill body (`tools/pptx-skills/pptx_deck.cs` plus its built-in copy) and every skill README now say “library only”, and **explicitly tell the model not to imply to users that web/real-scene photos will be fetched**.
- **Config defaults aligned**: `docker-compose.yml` and `.env.example` now default to `library`, with the restore path documented in comments.
- **Tests**: `PptxDeckSkillTests` **125 passed / 0 failed**, including a new case that **pins “no egress by default”** — even when the caller supplies a search endpoint, no network request may happen (`Searches == 0`) and the degradation must explain itself; the cases that exercise the network path were switched to an **explicit** `imageSource=auto`.

---

### 上一版 / Previous release

# AG-UI 群聊桌面版 1.0.163 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.163 Release Notes (current Windows desktop release)

**版本说明**：1.0.163 只做一件事——把 1.0.162 新引入的「自动放行上限」（`MaxAutoApprovedRounds`）从一条**固定魔数**改成**按任务复杂度定档**，与运行超时预算**同源同档**。
**Version note**: 1.0.163 does one thing: the auto-approval limit (`MaxAutoApprovedRounds`) introduced in 1.0.162 moves from a fixed magic number to **tiering driven by the very same task-complexity score** as the run timeout budget.

## 自动放行上限按任务定档（1.0.163）
# Auto-approval limit tiered by task (1.0.163)

中文：
- **为何要改**：1.0.162 把「自动放行」从人工审批计数里分了出来，但给它的仍是一条固定值（30）。固定值只能按最大量级去配——小任务白得宽松额度，大任务又可能被误杀（实测踩到过“产物已落盘、却因轮数超限被终止”）。而「时间够了、轮数却先到」本质上是同一件事：预算与放行额度应当同档。
- **怎么定档**：复用已有的 `RunComplexityEstimator`（交付物格式 / 页数 / 字数 / 条目数 / 多步措辞 / 附件规模 / 是否向下指派）。`MaxAutoApprovedRounds`（默认 30）退化为**简单档基准值 / 关闭自适应时的兜底**；启用后实际上限 = `max(基准值, 档位值)`，档位值为常规 **40** / 复杂 **60** / 繁重 **100**。两条不变量与超时一致：**只放宽不收紧**（运营者把基准调高过档位值时以运营者的为准）、**确定性**（恢复运行重算得同一额度）。档位值**可以突破**基准值——否则“自适应”没有意义。
- **可观测**：预算摘要与放宽日志现在都带上额度（`… 5→15 分钟，自动放行≤100，依据：交付物格式、41 页…`），熔断时的 WARN 也会带上本次档位与依据，可直接回答“为什么这次允许这么多次 / 为什么被兜住”。
- **可热改**：「管理员 → 执行参数 → 复杂度自适应」下新增三个字段（小驼峰 `autoApprovedStandard` / `autoApprovedComplex` / `autoApprovedHeavy`），与超时字段同页热改并持久化；非法值回退默认（1–10000）。
- **回归**：全量 **1554 通过 / 0 失败**。

English:
- **Why**: 1.0.162 separated auto-releases from the human-approval counter but bounded them with a fixed value (30), which can only be sized for the worst case: small tasks get an unnecessarily loose limit while big ones can still be killed (observed: a product was already on disk when the run was stopped by the round limit). "Time is enough but rounds run out first" is the same problem — the budget and the limit should share a tier.
- **How**: reuses the existing `RunComplexityEstimator` (deliverable format / pages / words / items / multi-step wording / attachment size / whether the role fans out). `MaxAutoApprovedRounds` (30) becomes the **simplest-tier baseline / fallback when adaptive is off**; when enabled the effective limit is `max(baseline, tier value)` with tier values 40 / 60 / 100 (standard / complex / heavy). The same two invariants hold: **widen-only** and **deterministic on resume**. Tier values **may exceed** the baseline, otherwise "adaptive" would be meaningless.
- **Observable**: the budget summary, the widening log line and the circuit-breaker WARN all carry the limit together with the tier and its evidence.
- **Hot-tunable**: three new fields under Admin → Execution Parameters → complexity-adaptive (`autoApprovedStandard` / `autoApprovedComplex` / `autoApprovedHeavy`), persisted like the timeout fields; invalid values fall back to defaults (1–10000).
- **Tests**: **1554 passed / 0 failed**.

---

### 上一版 / Previous release

# AG-UI 群聊桌面版 1.0.162 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.162 Release Notes (current Windows desktop release)

**版本说明**：1.0.162 是一次围绕「**上下文预算**」的集中治理——把此前散落在各处、各自独立、且**全部静默**的长度 / 时间上限收拢成可配置预算，并把截断与慢调用变成可查的日志。共五块：① **运行超时按任务复杂度自适应**（寒暄与 41 页带图 PPT 不再共用同一条预算）；② **提示词装配预算**（分段 + 总闸门 + 分层降级 + 截断 WARN），直接治「文字被截断却查不出在哪一层截的」；③ **记忆向量化的输入预算**（实测 embedding 约 **8.5ms/字符**：检索 query 2000→**500**、写入侧原本整条原文不限长→**800**）；④ **审批轮数分两道线 + 终止前先保产物**，治「40 页 PPT 已生成到磁盘、却因轮次上限被杀且产物没挂上消息」；⑤ **向量化两池隔离**，治后台批量入库把交互检索饿死（日志表现为「语义记忆检索失败」，看着像服务挂了）。
**Version note**: 1.0.162 is a concentrated pass over **context budgets** — length/time limits that used to be scattered, independent of each other and entirely silent are now one configurable budget with observability. Five parts: (1) **run timeouts adapt to task complexity** (chit-chat and a 41-page illustrated deck no longer share one budget); (2) a **prompt assembly budget** (sections + total gate + layered downgrade + truncation warnings), fixing "text was truncated and I cannot tell which layer did it"; (3) an **input budget for memory embedding** (measured ~8.5 ms per character: retrieval query 2000 → **500**, writes from unlimited to **800**); (4) **two separate approval counters with products kept on termination**, fixing a 40-page deck that was already on disk but never attached because the run was killed by the round limit; (5) **isolated embedding pools**, fixing background ingestion starving interactive retrieval (which surfaces as "semantic memory retrieval failed").

## 上下文预算治理（1.0.162）
# Context budget governance (1.0.162)

中文：
- **运行超时按任务复杂度自适应**：按触发消息估出任务量级（交付物格式 / 页数 / 字数 / 条目数 / 多步措辞 / 附件规模 / 是否向下指派），在 `StreamTimeoutMinutes`（默认 5 分钟）之上乘倍率（常规 ×1.5、复杂 ×2、繁重 ×3），上界 `MaxRunTimeoutMinutes`（默认 30 分钟）。**三处连带放宽**：内置文档技能预算同倍率、客户端桥技能等待上界 180 秒×倍率、模型 HTTP 网络兜底改为 `MaxRunTimeoutMinutes+5`。两条不变量：**只放宽不收紧**、**恢复时重算得同一预算**。预算被放宽时日志写明档位与依据。
- **提示词装配预算（分段 + 总闸门 + 截断可观测）**：收拢为 appsettings 顶层 `PromptBudget` 节点。单项放宽（历史单条 500→**4000** 字符、历史附件回喂 24K→**96K**、附件单文件 12K→**40K**、合计 60K→**200K**），规模由**总闸门**（默认 200000 字符）兜住：超预算时按 `TruncationOrder`（默认 `history` → `history_attachments` → `attachments`）**从尾部**裁，**当前消息与系统提示永不截断**；**只要发生截断就记一条 WARN**（写明用了多少 / 上限多少 / 丢了什么）。为什么不贴窗口定？模型（DeepSeek-V4.1-Flash）官方推荐 `context_window = 1M tokens`，而实测本平台单次 prompt 仅 3.5K~17.5K tokens（窗口的 0.35%~1.75%），真正的约束是成本与延迟（每轮重建上下文 → 每次调用都重付 prefill）。
- **记忆向量化的输入预算**：embedding 耗时由输入长度主导（实测 4 核 CPU + bge-m3：6 字 0.3 秒、**1500 字 12.7 秒**）。检索 query 上限 2000→**500**；新增写入侧上限 **800**（**同时作用于入库文本与向量**，避免“命中却内容对不上”）；新增 `EmbeddingConnectTimeoutSeconds=5`（把「连不上」与「排队中」拆开）与 `SlowEmbeddingWarnSeconds=10`（单次超时记 WARN 并带字符数）。**为什么不靠并发/并行**：实测服务端并行槽 1→4 只快 **~9%**，批量对长文本**完全无效**（1500 字×4：49.6s vs 48.7s）——这是“算术量”问题。
- **审批轮数分两道线 + 终止前先保产物**：`MaxInteractionRounds`（5→**15**）只数“真的打断了用户”的那一轮；**自动放行**（已同意技能 / 批量批准）走新配置 `MaxAutoApprovedRounds`（默认 30）。旧实现把两者混计，导致一次正常的长生成在第 5 次调用就被判超限杀掉（实测：40 页 PPT 已生成到磁盘、却因“交互恢复超过最大轮数（5）”终止、产物未挂上消息，用户只看到空白兜底）。现在**终止前先回挂已产出产物**。
- **向量化两池隔离**：交互池（回复前记忆 / 知识库 / 图谱检索，默认 3 并发）与后台池（记忆写入 / 导入 / 知识库入库，默认 1 并发）互不抢占；两池之和 = 旧版总并发（4），**不增加**对 embedding 服务的压力。交互侧等不到槽位就**降级**（本次不注入这段可选上下文），后台侧跳过本条由下次补上。
- **回归**：全量 **1546 通过 / 0 失败**。可在「管理员 → 执行参数」热改（注意：已保存过执行参数的实例会以库中快照为准，升级后需把 `maxInteractionRounds` 改成 15 再保存）。

English:
- **Run timeouts adapt to task complexity**: the trigger message is scored for magnitude and the stream timeout is multiplied (standard ×1.5, complex ×2, heavy ×3) under `MaxRunTimeoutMinutes` (30 min default). Three budgets widen together (built-in document skills, client-bridge skill wait, and the model network fallback), with two invariants: **widen-only** and **the same budget on resume**. Widening is logged with tier and evidence.
- **Prompt assembly budget (sections + total gate + truncation observability)**: one `PromptBudget` node. Per-section caps stay generous (history message 500→**4000** chars, re-inlined historical attachments 24K→**96K**, attachment per file 12K→**40K**, total 60K→**200K**) while the **total gate** (200000 chars) bounds the size, trimming from the tail by `TruncationOrder`; **the current message and system prompt are never truncated**, and **any truncation emits a WARN** stating usage, cap and what was dropped. Sized by cost, not by the window: the model recommends a 1M-token window while measured prompts are 3.5K–17.5K tokens.
- **Memory embedding input budget**: embedding cost is dominated by input length (4-core CPU: 6 chars = 0.3 s, **1500 chars = 12.7 s**). Query cap 2000→**500**; a new write cap of **800** (**applied to both stored text and vector**); new `EmbeddingConnectTimeoutSeconds=5` (separating "cannot connect" from "queued") and `SlowEmbeddingWarnSeconds=10`. **Not solved by concurrency**: measured, raising server slots 1→4 buys ~**9%**, and batching is useless for long text.
- **Two separate approval counters, products kept on termination**: `MaxInteractionRounds` (5→**15**) counts only rounds that really interrupted a user; auto-releases (already-approved skills / batch approval) are bounded separately by `MaxAutoApprovedRounds` (30). Mixing them killed legitimate long generations at the fifth call — a 40-page deck was on disk when the run terminated and never got attached. Termination now re-attaches produced products first.
- **Isolated embedding pools**: interactive (pre-reply memory / knowledge / graph retrieval, 3 concurrent) and background (writes / imports / knowledge ingestion, 1 concurrent) never preempt each other; their sum equals the previous total (4), so pressure is unchanged. Interactive callers degrade instead of waiting; background callers retry on a later pass.
- **Tests**: **1546 passed / 0 failed**. Hot-tunable under Admin → Execution Parameters (note: instances that saved execution params keep the stored snapshot, so bump `maxInteractionRounds` to 15 and save once after upgrading).

---

### 上一版 / Previous release

# AG-UI 群聊桌面版 1.0.161 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.161 Release Notes (current Windows desktop release)

**版本说明**：1.0.161 修「生成超时」——**不是页数的问题，是配图把执行预算吃光了**。实测（容器里跑真流程）：**纯文本 41 页只要 4~5.5 秒**，但**4 张联网配图就要 45 秒、8 张正好撞在一次执行 60 秒的硬预算上**；而超时的后果是**整份稿子都没了**——用户看到的就是「生成超时了（一次提交 41 页 + 长备注）。我把页数收敛到 33 页、备注精简后重出一版」，明明页数不是原因，却靠砍页数解决，还白跑一轮。根因：取图预算只在“每次取图前”查一次，而单次取图内部可能是「库检索 10s + 网络检索 8s + 逐个候选下载 12s×N」，能**越过预算好几倍**；**重试那一下还拿着旧的超时值**。现在：每次取图网络调用的超时都**夹到剩余预算内**（剩余不到 3 秒就不开工），重试也重算，预算用尽立即收手；取图上限可用 `AGUI_PHOTO_BUDGET_SEC` 调。另外把实用规模写进了技能描述（单次 ≤ 25 页、联网配图 ≤ 6 张；更大的稿子用 `action:edit` + `op:append` 分批），超时文案不再只说“已中止”，而是直接给出“减少配图/页数或分批 append，不要原样重试”。
**Version note**: 1.0.161 fixes "generation timed out" — **the page count is not the problem, illustrations eating the execution budget are**. Measured against the real pipeline inside the container: **41 text-only pages take just 4–5.5 seconds**, while **4 web illustrations take 45 seconds and 8 land exactly on the 60-second hard limit** — and a timeout loses **the entire deck**. That is why users saw 「生成超时了（一次提交 41 页 + 长备注）。我把页数收敛到 33 页…重出一版」: page count was not the cause, yet shrinking it was the workaround, costing a wasted round trip. Root cause: the photo budget was only checked *before* each lookup, while one lookup can chain a library search (10s), a web search (8s) and a download per candidate (12s each) — several times the budget — and the retry reused the old timeout. Now every network call in the photo path clamps its timeout to what is left of the budget (giving up under 3s), retries re-clamp, and the phase stops the moment the budget is gone; the budget is tunable through `AGUI_PHOTO_BUDGET_SEC`. The skill description now states the practical limits (up to 25 pages, 6 web illustrations, use `action:edit` + `op:append` for larger decks), and the timeout message tells the model how to retry instead of just reporting that it was aborted.

## 超时的真因：配图吃光预算（1.0.161）
# The real cause of the timeouts: illustrations (1.0.161)

中文：
- **实测数据（容器里跑真流程）**：纯文本 41 页 **4.2 秒**；41 页 + 8 页配图 **44.3 秒**、41 页 + 16 页配图 **45.0 秒**（修后均 ok=true，不再撞 60 秒）。修前：4 张配图 45 秒、8 张 **60.2 秒**（= 钉在硬预算上）。
- **为何“越预算”**：预算只在取图前查一次；单次取图内部可能是「库检索 10s + 检索 8s + 逐个候选下载 12s × N」，而 429/超时的重试还拿着旧超时值再跑一轮。
- **修法**：`ClampToBudget` —— 每次网络调用的超时夹到剩余预算内（剩余 < 3 秒直接放弃），重试重新夹；预算用尽立刻收手并把其余页降级为题图（如实 Warn），**先保证稿子能出来**。
- **超时文案可操作了**：现在是「.NET 技能执行超时（…ms），已中止。（不要原样重试：这通常是单次入参过重——文档技能最常见的原因是页数或联网配图太多…请减少配图/页数，或分批执行：先出一部分，再用 action:edit 的 op:append 追加剩下的。）」
- **提前引导**：技能描述里写明“单次建议 ≤ 25 页、联网配图 ≤ 6 张；更大分批 append”。
- **回归**：`PhotoBudget_CapsEachNetworkCall_SoTheDeckStillCompletes`（桩每条请求故意挂 30 秒、预算压到 2 秒，断言整个技能几秒内收手并如实报预算用尽）；全量 **1478 通过 / 0 失败**。

English:
- **Measurements (real pipeline, container)**: 41 text-only pages **4.2s**; 41 pages with 8 illustrated pages **44.3s**; with 16 **45.0s** (all `ok=true` after the fix, none hitting 60s). Before the fix: 4 illustrations took 45s and 8 took **60.2s**, pinned to the hard limit.
- **Why it overran**: the budget was checked once before each lookup, while a single lookup may chain a library search (10s), a search (8s) and a download per candidate (12s × N), and a 429/timeout retry reused the previous timeout for another full round.
- **The fix**: `ClampToBudget` — every network call's timeout is clamped to the remaining budget (under 3s left means give up), retries re-clamp, and once the budget is gone the phase stops immediately, degrading the remaining pages to generated art with an honest warning. **The deck always comes out.**
- **The timeout message is now actionable**: “.NET skill execution timed out (…ms), aborted. (Do not simply retry: this is usually an over-heavy single call — for document skills the most common cause is too many pages or web illustrations… reduce them, or run in batches: emit part of it, then append the rest with action:edit + op:append.)”
- **Guide the model up front**: the skill description states the practical limits (≤ 25 pages, ≤ 6 web illustrations, append for anything larger).
- **Regression**: `PhotoBudget_CapsEachNetworkCall_SoTheDeckStillCompletes` (the stub deliberately hangs 30 seconds per request while the budget is 2 seconds; the skill must stop within seconds and report the exhausted budget); full suite **1478 pass / 0 fail**.

# AG-UI 群聊桌面版 1.0.160 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.160 Release Notes (current Windows desktop release)

**版本说明**：1.0.160 修一个“每轮都白跑一次”的交互缺陷。技能工具过去只声明一个**必填**的 `query`（JSON 字符串）参数，而模型常常把技能参数**摊平**直接传进来（`{title, slides}` 而不是 `{query:"{…}"}`）——绑定就会失败，模型收到一句“缺少 query”，下一轮再包进 `query` 重发。用户看到的就是「**参数需要放在 `query` 里，我重新提交：**」，代价是白跑一整轮、多花一次 token、节奏被打断。现在工具的 schema 改成 **query 可选 + 允许额外字段**，并在执行前把两种形状归一成同一个技能入参：写在 `query` 里照旧能用，直接摊平传也能用（与客户端技能那条路早就在用的取值口径一致）；客户端技能的工具也一并统一，避免同类重试；同时把“可以直接摊平传参”写进了技能工具描述，让模型第一次就传对。
**Version note**: 1.0.160 fixes an interaction defect that cost a wasted round trip every time. Skill tools declared a single **required** `query` (a JSON string), while models routinely pass the skill's parameters **flattened** (`{title, slides}` rather than `{query:"{…}"}`) — argument binding then failed, the model saw “missing query”, and it re-sent the whole call wrapped in `query`. That is the 「参数需要放在 `query` 里，我重新提交：」 users kept seeing, at the cost of a whole extra round trip and its tokens. The tool schema is now **query optional with extra properties allowed**, and both shapes are normalised into one skill input before execution: works as before when written into `query`, and works when flattened (the same argument convention the client-skill path already used). Client-skill tools were unified too, and the tool description now says flattened arguments are fine so the model gets it right the first time.

## 技能工具接受“摊平传参”（1.0.160）
# Skill tools accept flattened arguments (1.0.160)

中文：
- **症状**：聊天里出现「参数需要放在 `query` 里，我重新提交：」，然后同一件事重做一遍；生成类技能（PPT/Word/Excel）尤其容易碰到（参数多）。
- **成因**：工具 schema 是 `{query: string}` 且 `query` **必填**。模型摊平传参 → 绑定失败 → 模型自己“改为放 query 里”重发。
- **修法**：`SkillToolFunction`（自定 `AIFunction`）——schema 改为 `query` 可选 + `additionalProperties: true`；执行前归一：有 `query` 用 `query`（字符串或 JSON 对象都认），没有就把整个参数对象序列化成紧凑 JSON 交给技能（技能侧本来就容错解析 JSON）。旧写法完全兼容。
- **护栏**：`SkillToolArgumentTests`（7 条）钉住两种形状的归一、schema 不得再要求 `query`、以及**源码扫描护栏**（技能工具必须走 `SkillToolFunction`，不得退回必填 `query` 的写法）。
- **实测**：一份参数较多的 PPT 请求（主题/风格/动画/表格/仪表/图标行）一次调用直接出稿，**没有再出现“重新提交”**；产物核对：5 页、12 个动画效果节点、每页 `fade` 切换、悬空引用 0。

English:
- **Symptom**: chats showed 「参数需要放在 `query` 里，我重新提交：」 followed by the whole task being redone; generation skills (PPT/Word/Excel) hit it most because they take many parameters.
- **Cause**: the tool schema was `{query: string}` with `query` **required**. A flattened call failed binding, so the model wrapped everything in `query` and re-sent.
- **Fix**: a custom `AIFunction` (`SkillToolFunction`) — `query` optional plus `additionalProperties: true`; before execution the arguments are normalised: `query` wins when present (string or JSON object), otherwise the whole arguments object is serialised to compact JSON for the skill (skill bodies already parse JSON tolerantly). The old shape keeps working unchanged.
- **Guardrails**: `SkillToolArgumentTests` (7 tests) pin both normalisations, that the schema no longer requires `query`, and a source-scan guard (skill tools must go through `SkillToolFunction`, not back to the required-`query` form).
- **Verified**: a parameter-heavy PPT request (theme/style/animations/table/gauge/icon rows) produced the deck in a single call with **no “resubmitting”** message; the file checks out — 5 slides, 12 animation effect nodes, a `fade` transition on every slide, zero dangling references.

# AG-UI 群聊桌面版 1.0.159 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.159 Release Notes (current Windows desktop release)

**版本说明**：1.0.159 修「配图不正确 / 图库明明有对应的图片却没用上」。两个成因都查实并修掉：① **本页文字兜底把整段 120 字当一个查询丢过去，被检索“摊薄”**——同一个名字单查 0.76 命中本人照，含它的 47 字整段只有 0.5862，低于 0.60 的可用线，于是图库里明明有照片也落空、降级成网图/题图；现在把本页文字**切成短片段逐个试**（短片段优先、整段作最后兜底、命中够可信就收手，不抢全稿共享的 35 秒取图预算）。② **有 4 次调用根本没拿到图库**：模型给的入参带 ``` 围栏时，平台严格解析失败、异常被吞掉，整轮无图库；现在平台与技能同口径容错解析（取不到则**原样交回**，绝不用空对象顶替）。另外把“词面命中”的定义**收紧了一道**（这是保留该能力而不是削弱它）：查询里必须有**一段连续词项**命中——人名/型号照旧命中，长描述里蹭一个“团队”不再算命中（实测曾以 0.38 分把一张 AI 插画当成命中）。
**Version note**: 1.0.159 fixes “the illustration is wrong / the library clearly has the matching picture but it was not used”. Both causes were reproduced and fixed: (1) **the page-text fallback handed the whole 120-character page blob to one query and got diluted** — the bare name scores 0.76 against its caption while the 47-character page text containing it scores only 0.5862, under the 0.60 usability gate, so the photo that *was* in the library was missed and the page fell back to a web photo or a generated image; the page text is now **split into short fragments tried one by one** (shortest first, the whole text kept as the last candidate, stopping at the first confident hit so the deck-wide 35-second photo budget still holds). (2) **Four calls got no image library at all**: when the model's payload arrived wrapped in ``` fences the platform's strict parse failed and the exception was swallowed, so that run had no library; the platform now tolerates input the same way the skill does (and hands an unparseable payload back **unchanged**, never replaced by an empty object). The definition of a “word-form hit” was also **tightened** (preserving that capability rather than weakening it): the query must have a **contiguous run of terms** present in the text — names and part numbers still match, while one scattered common word like 团队 in a long description no longer counts (measured: an AI illustration was once accepted at 0.38).

## 配图：短片段兜底 + 注入容错 + 词面证据（1.0.159）
# Illustrations: fragment fallback, tolerant injection, phrase evidence (1.0.159)

中文：
- **成因 1（主因）**：本页文字兜底直接用整段（上限 120 字）查图库。检索会按查询词项数**摊薄**，实测同一页内容：`刘佳俊` 单查 **0.76**，含该名字的 47 字整段 **0.5862**（低于可用线）；`AI项目支持团队` 单查 **0.80**，含它的 72 字整段 0.6281（贴线）。修法：按标题分隔符（·｜—）与常见标点切成 2~20 字片段，最多 5 个，**短片段优先**，整段作最后一个兜底候选（不会比改之前更差）；某个片段达到可信分（0.78）就提前收手。
- **成因 2（并列）**：日志里 `注入图库检索范围失败（按无图库处理）：skill=pptx_deck`——`JsonDocument.Parse` 碰到 ``` 围栏就直接抛异常，被 catch 吞掉，**这一轮完全没有图库**。修法：平台侧与技能侧同口径容错（取首个 `{` 到末个 `}`），取不到就**原样交回**。
- **词面命中收紧（保留能力，只堵蹭词）**：新增 `Bm25Ranker.HasPhraseEvidence`——查询里必须有**一段连续词项**在文本里也都在；1~2 个词项的短查询不作此要求（避免误伤 `SKU-2026` 这类双词项专有号）。于是 `刘佳俊` / `SKU-2026` / `颁奖典礼合影` 照样命中，而“颁奖 团队 合影”不再因为长描述里恰好有“团队”二字而命中（实测 0.38 分）。
- **端到端实测**（本机容器，真实图库）：三个**关键词全部落空**的页面（“颁奖典礼 现场 合影”“员工 颁奖 舞台”“集体 合影 会场”），兜底分别配到了 `1刘佳俊.png`(0.762)、`MS.png`(0.762) 等**真实照片**，0 warnings（无网图、无题图降级）；而泛化词（集体/合影/现场）在图库里仍是 0 命中，不会硬塞。
- **回归**：新增 7 条单测（短片段兜底、围栏入参仍能注入、容错不了要原样交回、人名在严格门下仍被词面召回、长描述蹭词不算命中、词组证据边界、散落词不算命中）；全量 **1470 通过 / 0 失败**。

English:
- **Cause 1 (the main one)**: the page-text fallback queried the library with the whole blob (up to 120 characters). Retrieval is diluted by the number of query terms: measured on the same page, `刘佳俊` alone scores **0.76** while the 47-character text containing it scores **0.5862** (under the usability gate), and `AI项目支持团队` alone scores **0.80** against 0.6281 for the 72-character blob. The page text is now split on title separators (·｜—) and punctuation into 2–20 character fragments, at most five, shortest first, with the whole text kept as the final candidate (so it is never worse than before); the loop stops once a fragment reaches the confidence score (0.78).
- **Cause 2**: the log showed `injecting the image-library scope failed (treated as no library): skill=pptx_deck` — `JsonDocument.Parse` threw on ``` fences and the exception was swallowed, so that run had no library at all. The platform now tolerates the input the same way the skill does (first `{` to last `}`) and hands it back **unchanged** when it cannot.
- **Word-form hits tightened (capability preserved, free-riding closed)**: new `Bm25Ranker.HasPhraseEvidence` requires a **contiguous run of query terms** to appear in the text; queries with only one or two terms are exempt so part numbers like `SKU-2026` are not hurt. So `刘佳俊`, `SKU-2026` and `颁奖典礼合影` still match, while “颁奖 团队 合影” no longer matches merely because a long description happens to contain 团队 (measured at 0.38).
- **End-to-end on the real library** (local container): three pages whose keywords all missed (“颁奖典礼 现场 合影”, “员工 颁奖 舞台”, “集体 合影 会场”) were matched by the fallback to real photos — `1刘佳俊.png` (0.762), `MS.png` (0.762) — with zero warnings (no web photo, no generated fallback), while generic words (集体/合影/现场) still return nothing rather than forcing a wrong picture in.
- **Regression**: 7 new tests (fragment fallback, fenced input still gets a scope, unparseable input left untouched, a person name still recalled under a strict gate, a scattered word in a long description no longer counted, phrase-evidence boundaries, isolated terms not counted); full suite **1470 pass / 0 fail**.

# AG-UI 群聊桌面版 1.0.158 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.158 Release Notes (current Windows desktop release)

**版本说明**：1.0.158 让 PPT 技能**会写动画与翻页切换**。以前生成的 pptx 是静态稿：用户在「与 ppt生成助手 的单聊」里问“第一页能否将文字加入飞出来的效果”，数字员工只能答“工具不写入动画，请在 PowerPoint 里手动加”。现在支持入场（出现 / 淡入 / 飞入 / 擦除 / 溶解）、强调（旋转 / 放大回弹 / 变色）、退出（消失 / 淡出 / 飞出 / 擦除退出 / 溶解退出）与 **21 种页间切换**；顶层写一次就是全稿默认、页级可覆盖，而且**已经交付出去的旧稿也能直接加**（`action:edit` 新增 `op:animate` / `op:transition`）。动画 XML 不是凭记忆写的——结构逐项对齐**真实 PowerPoint 产物**（从 LibreOffice 回归库的 301 份 pptx 里统计得出），因为写错会被 PowerPoint 判“需要修复”；三层把关：OpenXmlValidator、悬空引用检查（动画指向不存在的形状）、LibreOffice 实际打开与回写。不请求动画时输出与从前**逐字节一致**。
**Version note**: 1.0.158 teaches the PPT skill to **write animations and slide transitions**. Decks used to be static: asked in *chat with ppt-assistant* whether the first slide's text could “fly in”, a digital employee could only answer “the tool does not write animations — add it by hand in PowerPoint”. It now supports entrances (appear / fade / fly in / wipe / dissolve), emphasis (spin / pulse / change colour) and exits (disappear / fade out / fly out / wipe out / dissolve out) plus **21 slide transitions**; write it once at the top level as a deck-wide default and override it per slide, and **decks already delivered can be given effects too** (the new `op:animate` / `op:transition` in `action:edit`). The AnimationML structure is not written from memory: every piece is aligned with **real PowerPoint output** (derived from 301 pptx files in LibreOffice's regression corpus), because malformed timing makes PowerPoint demand a repair. Three layers of checking: OpenXmlValidator, a dangling-reference check (animations pointing at shapes that do not exist) and an actual LibreOffice open plus write-back. With no animation requested the output stays **byte-identical** to before.

## PPT 技能支持动画与页间切换（1.0.158）
# PPT skills can now animate and transition (1.0.158)

中文：
- **怎么用**：顶层 `transition` / `animate` 作为全稿默认，页级同名字段覆盖（写 `false` 关掉该页）。例：封面主标题飞入 = 该页 `"animate": {"preset": "flyIn", "direction": "bottom"}`；要点逐条出现 = `{"preset": "fade", "byParagraph": true}`；全稿淡入 = 顶层 `"animate": "fade"`；逐页切换 = 顶层 `"transition": "fade"`；一页多个效果写数组（各占一次点击）。
- **既有稿加动画**：`action:"edit"` + `ops:[{op:"animate",slides:[1],animate:{…}}, {op:"transition",slides:[2,3],transition:{preset:"push"}}]`；可重复执行（每次都先删旧时间线，不叠加），且绠不改原件。
- **结构来源（关键）**：动画 XML 没有“大致对”——错一点 PowerPoint 就要修复。所以逐项对齐真实产物：Fly In 是 `p:anim` + `tavLst` 位移（**不是** `animEffect`）、Wipe 是 `animEffect filter="wipe(up)"`、退出是 `animEffect transition="out"` + 紧随的 `set hidden`（delay=dur−1）、强调是 `animRot`/`animClr`；元素次序按 `CT_Slide`（cSld → clrMapOvr → transition → timing → extLst）。
- **三层验证**：① `OpenXmlValidator` 过 schema；② **悬空引用检查**（每个 `spTgt/@spid` 必须在当页真实存在，`qa` 报 `animTarget`）；③ 容器内 LibreOffice 实际打开转 PDF，且回写 pptx 后动画**被理解并保留**（含逐段的每个节点）。
- **端到端实测**（本机容器）：对「与 ppt生成助手 的单聊」说“封面主标题要加飞出来的动画，每页翻页用淡入切换”，它直接交付出稿，自述“封面飞入 1 页 / 2 个效果，4 页淡入切换”；我们拆包核对，**与自述一致**（无悬空引用、每页 transition 就位）。
- **如实说明的限制**：只做经典（2007 schema）效果，**没有 morph（变形）与 3D**；切换的“任意毫秒时长”映射到 fast/med/slow 三档；`byParagraph`（逐段）默认关，播放观感需在 PowerPoint 里看一眼（自动化只能保证文件完好、不需修复）。
- **兼容承诺**：没请求动画时输出与从前逐字节一致；返回 JSON 新增 `animations`（pages / effects / transitions / presets）如实报出用量，0 就是没加；未知预设名一律报错，不静默回落。
- **回归**：新增 5 条单测（目标存在性 + schema、不写就不加、未知预设报错、页级关闭、既有稿加动画且不叠加）；全量 **1462 通过 / 0 失败**。

English:
- **How to use it**: `transition` / `animate` at the top level act as deck-wide defaults and a slide's same-named field overrides them (`false` disables that slide). Examples: a cover title flying in = `"animate": {"preset": "flyIn", "direction": "bottom"}` on that slide; bullets appearing one at a time = `{"preset": "fade", "byParagraph": true}`; deck-wide fade = top-level `"animate": "fade"`; per-slide transitions = top-level `"transition": "fade"`; an array requests several effects on one slide (each on its own click).
- **Adding effects to an existing deck**: `action:"edit"` with `ops:[{op:"animate",slides:[1],animate:{…}}, {op:"transition",slides:[2,3],transition:{preset:"push"}}]`; repeatable (the old timeline is removed first, so effects never stack) and the original file is never touched.
- **Where the structure comes from (the important part)**: animation XML has no “roughly right” — get it slightly wrong and PowerPoint demands a repair. So every piece is aligned with real output: Fly In is `p:anim` + a `tavLst` offset (**not** `animEffect`), Wipe is `animEffect filter="wipe(up)"`, an exit is `animEffect transition="out"` followed by `set hidden` (delay = dur−1), emphasis is `animRot`/`animClr`; element order follows `CT_Slide` (cSld → clrMapOvr → transition → timing → extLst).
- **Verified three ways**: (1) `OpenXmlValidator` against the schema; (2) a **dangling-reference check** (every `spTgt/@spid` must exist on that slide; `qa` reports `animTarget`); (3) an actual LibreOffice open to PDF inside the container, plus a pptx write-back showing the animations were **understood and preserved** (every per-paragraph node included).
- **End-to-end result** (local container): asked in *chat with ppt-assistant* for “a fly-in cover title and fade transitions between slides”, the assistant delivered a deck and reported “cover fly-in, 1 slide / 2 effects; fade transitions on all 4 slides”; unpacking the file confirmed the claim (no dangling references, transitions present on every slide).
- **Honest limits**: classic (2007 schema) effects only — **no morph, no 3D**; an arbitrary transition duration maps onto fast/med/slow; `byParagraph` is off by default and its playback should be eyeballed in PowerPoint (automation can only guarantee the file is intact and needs no repair).
- **Compatibility promise**: with no animation requested the output is byte-identical to before; the new `animations` field in the return JSON (pages / effects / transitions / presets) reports real usage — 0 means nothing was added; an unknown preset name is an error rather than a silent fallback.
- **Regression**: 5 new unit tests (target existence + schema, nothing added unless requested, unknown preset errors, per-slide opt-out, effects added to an existing deck without stacking); full suite **1462 pass / 0 fail**.

# AG-UI 群聊桌面版 1.0.157 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.157 Release Notes (current Windows desktop release)

**版本说明**：1.0.157 只修一处“账看不明白”的口径问题。上一版新增的「存储治理」页只报“可回收”，而附件是**先上传、后随消息发送**的，宽限期（默认 7 天）内的无引用文件按设计不能回收——而你的库里那 186 个无引用附件**恰好全部**落在宽限期内，于是页面显示「可回收 0」，看起来像“一点浪费都没有”，反而会误导管理员。现在口径拆成两档并分开展示：**无引用（含宽限期内）** 与 **现就可回收**。删除行为完全不变：仍然只删“无任何引用 **且** 超过宽限期”的文件，开关依旧默认关闭（`StorageGovernance:AllowReclaim=false`）。
**Version note**: 1.0.157 fixes one misleading number. The *Storage* page added in the previous release reported only the reclaimable total, but attachments are **uploaded first and attached to a message later**, so unreferenced files inside the grace period (7 days by default) are deliberately not reclaimable — and in your library all 186 unreferenced attachments happened to fall inside that window, so the page showed "reclaimable 0", which reads as "nothing is being wasted at all" and misleads the admin instead of informing them. The stats are now split into **unreferenced (grace period included)** and **reclaimable now**, shown as separate cards. Deletion behavior is completely unchanged: it still only removes files with no reference at all **and** past the grace period, and the switch remains off by default (`StorageGovernance:AllowReclaim=false`).

## 存储统计拆成「无引用」与「现就可回收」两档（1.0.157）
# Storage stats split into "unreferenced" and "reclaimable now" (1.0.157)

中文：
- **问题**：`AttachmentStorageStats` 只有一个 `OrphanFiles`/`OrphanBytes`，语义是“无引用 **且** 超过宽限期”。宽限期内的无引用文件因此**完全不可见**。实测该口径下页面显示「可回收 0」，而真实情况是 203 个附件里 180 个已无引用（257 MB）——管理员据此会判断“没有任何浪费”，结论完全相反。
- **修法**：`Inspect()` 先统计“无引用”数量，再判断是否超过宽限期，返回两组数：`UnreferencedFiles`/`UnreferencedBytes`（无引用，含宽限期内；对应“刚上传还没发送”“正在生成的产物”）与 `OrphanFiles`/`OrphanBytes`（现就可回收）。`GET /ag-ui/admin/storage` 同时返回两者，页面多加一张卡片。
- **不变的部分**：回收判定与删除集合一字未改（仍是“无引用 **且** 超宽限期”），`POST /reclaim` 在开关关闭时依旧 **403 + 原因**，勾选删除附件的默认值依旧为不勾。
- **回归**：`AttachmentLifecycleTests` / `TopicTests` 补齐 `UnreferencedFiles` 断言（子集 21 条全绿），全量 1457 通过 / 0 失败。

English:
- **The problem**: `AttachmentStorageStats` carried a single `OrphanFiles`/`OrphanBytes` pair meaning "no reference **and** past the grace period", which made unreferenced files inside the grace period **completely invisible**. Measured under that rule the page reported "reclaimable 0" while reality was 180 of 203 attachments having no reference at all (257 MB) — an admin would conclude "nothing is being wasted", the opposite of the truth.
- **The fix**: `Inspect()` now counts unreferenced files first and only then checks the grace period, returning two pairs: `UnreferencedFiles`/`UnreferencedBytes` (no reference, grace period included — i.e. just-uploaded-not-yet-sent and artifacts still being generated) and `OrphanFiles`/`OrphanBytes` (reclaimable now). `GET /ag-ui/admin/storage` returns both and the page shows an extra card.
- **What did not change**: the reclaim rule and the deleted set are untouched (still "no reference **and** past the grace period"), `POST /reclaim` still answers **403 with a reason** while the switch is off, and the "also delete attachment files" checkbox still defaults to unchecked.
- **Regression**: `AttachmentLifecycleTests` / `TopicTests` now assert `UnreferencedFiles` (21-test subset green); full suite 1457 pass / 0 fail.

# AG-UI 群聊桌面版 1.0.156 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.156 Release Notes (current Windows desktop release)

**版本说明**：1.0.156 修一个“看起来像丢数据”的语义缺陷，并把附件回收做成可控能力。起因是：你发现「与 ppt生成助手 的单聊」里**以前上传的附件都不见了**。查下来文件一个都没丢——真正发生的是：13:50 那次「清空主话题聊天记录」只删了消息与记忆（这是它声明过的），而附件卡片是挂在消息上的，消息没了入口也就没了；**文件本身仍在磁盘上**（当时 203 个附件里 186 个已无入口，245.6 MB）。三个改进：① **把话说清楚**：清空 / 删除话题的确认框现在明确写明“消息与记忆会删、附件文件默认保留（会从对话里移除，无法再打开）”，并多了一个「同时删除这批附件文件」勾选（默认不勾；勾了也会跳过仍被引用的那些）；② **把账算清**：管理员控制台新增「存储治理」页，展示附件总占用 / 仍被引用 / 可回收量与宽限期；③ **把动作做可控**：回收默认**关闭**（`StorageGovernance:AllowReclaim=false`），开启后管理员可一键回收“无任何引用且超过宽限期（默认 7 天）”的孤儿附件，每次回收写审计。已把你那 186 个孤儿附件完整导出到宿主机 `C:\Users\david\agui-attachments-backup\2026-09-21\`（含 manifest.csv 指出哪些已无入口）。
**Version note**: 1.0.156 fixes a semantic defect that *looks* like data loss, and turns attachment reclaim into a controlled capability. It started with your report that in *chat with ppt-assistant* **previously uploaded attachments had disappeared**. Nothing was actually lost: what happened is that the 13:50 “clear main-topic chat history” deleted only messages and their memories (as it always said it would), and attachment cards hang off messages — so with the message gone, the entry point was gone; **the files themselves were still on disk** (203 attachments, of which 186 had no reachable entry point, 245.6 MB). Three improvements: (1) **say what actually happens** — the clear/delete-topic dialogs now state that messages and memories are deleted while **attachment files are kept by default** (they are removed from the conversation and can no longer be opened there), plus a new “also delete these attachment files” checkbox (unchecked by default; even when ticked, files still referenced elsewhere are skipped); (2) **show the numbers** — a new *Storage* page in the admin console reports total attachment footprint, how much is still referenced, and how much is reclaimable; (3) **make the action controllable** — reclaim is **off by default** (`StorageGovernance:AllowReclaim=false`); once enabled an admin can reclaim orphans that have no reference at all and are older than the grace period (7 days by default), with every reclaim written to the audit log. Your 186 orphaned attachments have been exported in full to `C:\Users\david\agui-attachments-backup\2026-09-21\` (with manifest.csv marking which have no entry point).

## 附件语义说清 + 附件回收做成可控能力（1.0.156）
# Attachment semantics made explicit + reclaim becomes a controlled capability (1.0.156)

中文：
- **先说结论（本次不是缺陷）**：传过的附件**没有被删除**。全仓唯一会删附件文件的地方是「系统初始化」；清空 / 删除话题、撤回消息、消息保留策略、账号擦除都只动消息（与对应记忆）。所以“附件会消失”实际是“承载它的消息被删了”——附件卡片跟着消息走，这是设计如此。
- **① 把话说清楚**：`topic.clearConfirm` / `topic.deleteConfirm` 现在写明“消息与对应记忆将一并删除…已上传的附件文件默认保留（会从对话中移除，无法再从这里打开）”，并新增勾选项 `topic.alsoDeleteFiles`（默认不勾）。新增通用对话框能力 `uiConfirmWithCheck`，需要时可复用。
- **② 附件可以真的删（可选）**：勾选后服务端才会删，而且**不是无条件删**——新的 `IAttachmentLifecycle`（接口在 Hub、实现在 Web）会先算出“仍被引用”的集合：**消息附件 / 用户 / 群 / 群成员 / 数字员工头像 / 知识库文档 / 技能试运行产物**都在保护名单内（实测中差点漏掉知识库文档那一类）。未被引用才删，被引用的跳过去并记日志。
- **③ 存储治理页（新）**：管理员控制台 →「存储治理」。`GET /ag-ui/admin/storage`（管理员）返回附件总数 / 总占用 / 仍被引用数 / 可回收数与可回收字节 / 宽限期；回收按钮在开关未开时禁用并提示原因。
- **④ 回收默认关闭**：`StorageGovernance:AllowReclaim`（默认 `false`，环境变量 `STORAGE_ALLOW_RECLAIM`）+ `StorageGovernance:OrphanGraceHours`（默认 168 小时）。判定口径：“无任何引用 **且** 超过宽限期”。宽限期不可省：文件是“先上传、后随消息发送”的，没有宽限期会把刚上传还没发送的文件当孤儿删掉。回收经 `POST /ag-ui/admin/storage/reclaim`，每次都写审计（`storage.reclaim`）。
- **已帮你把数据拿回来**：186 个孤儿附件已完整导出到 `C:\Users\david\agui-attachments-backup\2026-09-21\uploads\`，并生成 `manifest.csv`（含 `stillInChat=yes|no`，筛 `no` 就是“已经找不到入口”的那批）。这是副本，容器内原始数据未动。
- **新增回归（共 13 条）**：`AttachmentLifecycleTests`（6 条：四类引用必须被算入、宽限期拦住新文件、只删孤儿、dry-run 不删、非法目录不计入）；`TopicTests`（+4 条：默认不碰附件、勾选后把该话题的附件交给回收、删话题同样、权限不变）；`StorageAdminApiTests`（3 条：统计管理员可读 / 非管理员 403、回收默认 fail-closed（未开启直接 403）、开启后只删孤儿而保留仍被引用的文件）。
- **护栏**：`DesktopCompositionTests` 的前端接口探测新增 `GET /ag-ui/admin/storage`；两个组合根的 `Map*Api()` 清单一致性仍受源码扫描保护。
- **全量 1457 通过 / 0 失败**。

English:
- **First, the conclusion (this was not a defect)**: the attachments you uploaded were **never deleted**. The only place in the whole repo that deletes attachment files is *system reset*; clearing or deleting a topic, recalling a message, the message-retention policy and account erasure all touch messages (and their memories) only. So “attachments disappear” really means “the message carrying them was deleted” — attachment cards belong to messages, by design.
- **(1) Say what actually happens**: `topic.clearConfirm` / `topic.deleteConfirm` now state that messages and their memories are deleted while **uploaded attachment files are kept by default** (removed from the conversation, no longer openable there), plus a new `topic.alsoDeleteFiles` checkbox (unchecked by default). A reusable `uiConfirmWithCheck` dialog helper was added.
- **(2) Attachments can actually be deleted (opt-in)**: only when ticked does the server delete anything — and even then not unconditionally. The new `IAttachmentLifecycle` (interface in Hub, implementation in Web) first computes the still-referenced set: **message attachments / user, group, group-member and digital-employee avatars / knowledge-base documents / skill trial-run artifacts** are all protected (the knowledge-base document class was nearly missed during implementation). Unreferenced files are deleted; referenced ones are skipped and logged.
- **(3) New Storage page**: admin console → *Storage*. `GET /ag-ui/admin/storage` (admin only) returns total files / footprint / still-referenced count / reclaimable count and bytes / grace period. The reclaim button is disabled with the reason shown while the switch is off.
- **(4) Reclaim is off by default**: `StorageGovernance:AllowReclaim` (default `false`, env `STORAGE_ALLOW_RECLAIM`) plus `StorageGovernance:OrphanGraceHours` (default 168 hours). The rule is “no reference at all **and** older than the grace period”. The grace period is not optional: files are uploaded *before* the message is sent, so without it a just-uploaded file would be treated as an orphan and deleted. Reclaim goes through `POST /ag-ui/admin/storage/reclaim` and is always audited (`storage.reclaim`).
- **Your data has been recovered for you**: the 186 orphaned attachments were exported in full to `C:\Users\david\agui-attachments-backup\2026-09-21\uploads\`, with a generated `manifest.csv` (including `stillInChat=yes|no`; filter on `no` for the ones with no entry point left). This is a copy — the original data inside the container is untouched.
- **New regression tests (13 total)**: `AttachmentLifecycleTests` (6: all four reference kinds must be counted, the grace period holds back fresh files, only orphans are deleted, dry-run deletes nothing, invalid directories are ignored); `TopicTests` (+4: attachments untouched by default, handing the topic's attachments to reclaim when ticked, same for topic deletion, permissions unchanged); `StorageAdminApiTests` (3: stats readable by admins / 403 for others, reclaim fail-closed by default (403 while disabled), and when enabled only orphans are deleted while referenced files survive).
- **Guardrails**: the desktop composition probe list now includes `GET /ag-ui/admin/storage`; the two roots' `Map*Api()` parity check is unchanged and still enforced by source scan.
- **Full suite 1457 pass / 0 fail**.

# AG-UI 群聊桌面版 1.0.155 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.155 Release Notes (current Windows desktop release)

**版本说明**：1.0.155 修两个会让“数字员工该干活却什么也没干”的缺陷。① **带图的消息会把工具全部摘掉**：本轮带图时网关把执行体换成了一个**不带工具**的裸视觉体，于是请求里根本没有 `tools`；而 DeepSeek 这类模型**不会报错**，而是把工具调用**当正文写出来**（DSML 标记：`tool_calls` / `invoke` / `parameter` 一整套）——结果技能从未被调用、文件从未生成，用户既看不到 pptx，还收到一大段标记文本（实测就是「与 ppt生成助手 的单聊」里“PPT 首页背景图换成附件的图”那条，以及后续“没有看见pptx”）。现在带图轮次**只换模型、不换能力**（工具 / 记忆 / 审批包装全保留），已实测：同一请求由“写出一段标记”变成“发起技能调用 → 批准 → 产出 348KB 的 pptx 附件”。② **空值配置被当成已配置**：Docker 透传的空值（`Agents__DecisionModel=`）会绑定成**空字符串**，而空串被 `??` 当成“已设置”并一路传成模型名，`ChatClient` 构造直接抛 `Value cannot be an empty string. (Parameter 'model')`——于是**每次语境判定都失败**（日志只有一句“语境判定调用失败，本次按不发言处理”）。现在模型名解析一律“**空白即未设置**”，留空就是默认行为。
**Version note**: 1.0.155 fixes two defects that made a digital employee do nothing when it should have worked. (1) **A message carrying an image stripped every tool**: on an image turn the gateway swapped the executor for a **tool-less** bare vision agent, so the request had no `tools` at all. DeepSeek does not error on that — it **writes the tool call into the message body** (its DSML markup: a whole `tool_calls` / `invoke` / `parameter` block) — so the skill never ran, no file was ever produced, and the user got a wall of markup instead of a pptx (exactly the “swap the PPT cover for my attached image” message in *chat with ppt-assistant*, and the “I don't see a pptx” that followed). An image turn now **swaps only the model, never the abilities** (tools, memory and the approval wrapper are all kept); measured end to end: the same request went from “emits a markup block” to “calls the skill → approve → delivers a 348 KB pptx attachment”. (2) **A blank config value was treated as configured**: an empty value passed through Docker (`Agents__DecisionModel=`) binds as an **empty string**, which `??` accepted as “set” and passed along as the model name, so constructing the `ChatClient` threw `Value cannot be an empty string. (Parameter 'model')` — and **every contextual decision failed** (the only trace being “contextual decision call failed, treating as no-speak”). Model-name resolution now treats **blank as unset**, so leaving it empty means the default behavior.

## 带图消息不再丢工具 + 空值配置不再当已配置（1.0.155）
# Image turns keep their tools + blank config no longer counts as set (1.0.155)

中文：
- **缺陷一：带图消息把工具摘掉，技能从此不会执行**
  - 路径：普通流式与交付兑底的“本轮带图 → 换视觉模型”都换成了 `CreateBareVision`——它是**裸体**（`Tools = null`、无记忆注入）。
  - 后果（实测）：发往模型的消息里没有 `tools`；实测真实端点（同一提示）——**带 tools → 返回结构化 `tool_calls`；不带 tools → 正文出现 DSML 标记**。于是技能从未执行、交付兑底两轮都产不出文件。
  - 修法：新增 `AgentCatalog.GetOrCreateVision(agentId, visionModel)`：走**同一套**完整装配（工具 / 记忆 / 技能链 / 审批包装），**只把模型换成视觉模型**（缓存键按模型区分）。两个调用点全部改用它；`CreateBareVision` 删除（避免再被误用）。
  - 为何不是模型的锅：实测视觉模型 `deepseek-v4-flash-vision-exp` **支持** tool calling（无图时返回结构化调用）。
  - 线上实测（本机 Docker，「与 ppt生成助手 的单聊」，带图）：`构建模型客户端：... model=deepseek-v4-flash-vision-exp` → `运行中断等待交互`（模型发起需审批的技能调用）→ 批准 → `技能产物入库为附件：年度颁奖典礼_3页_暖阳版.pptx（356408 字节）`，聊天里出现可下载附件。
- **缺陷二：`Agents__DecisionModel=`（空串）让每次语境判定失败**
  - 根因：配置绑定会把**空字符串照样绑上**，而空串不是 `null`——旧实现用 `??`，于是把空串当成“已配置的模型名”，`ChatClient` 构造抛 `ArgumentException: Value cannot be an empty string. (Parameter 'model')`。
  - 影响：语境判定、指派路由全部失败（日志只有“语境判定调用失败，本次按不发言处理”）；而 `Agents__DecisionModel` 的默认值就是空串，所以这是一个“默认配置就坏”的缺陷。
  - 修法：模型名解析统一改走 `FirstNonBlank`（**空白 = 未设置**），覆盖 `DecisionModel` / `ThinkingModel` / 智能体 `Model` / `BuildOpenAIChatClient` 的 `modelOverride`；与本仓库既有约定一致（`Agents__ApiKey` / `Agents__Endpoint` 本就按空白回退）。
  - 线上实测：修复后日志为 `语境判定：模型=deepseek-chat P(发言)=1.000 阈值=0.3 → 发言（原始：YES）`。
- **新增回归（共 9 条）**：
  - `DecisionModelAndParsingTests` +8：空白（`null` / `""` / `"   "`）的 `DecisionModel`、智能体 `Model`、`ThinkingModel` 必须回退到默认模型；并把生产配置形状直接喂给 `BuildOpenAIChatClient`（含空 `Endpoint` / 空 `modelOverride`）断言构造不抛。
  - `VisionTurnToolRetentionTests` +1：带图轮次仍能调工具（用“公告”这个需审批的内置工具当探针，mock 只在挂工具时才会发出该调用）。两条护栏都做了**反向验证**：把代码改回错误实现，它们确实会红。
  - 全量 **1444 通过 / 0 失败**。
- **运维提示（非缺陷）**：一条流式消息**创建超过 10 分钟且 60s 无活跃**会被孤儿流兜底强制收尾；所以审批卡放超过 10 分钟再点批准，恢复会报“消息不存在或未开启流式灌入”（该次回复拿不回来了）。及时点按正常。
- **已知小噪声（不影响产出）**：偶尔可见 `注入图库检索范围失败（按无图库处理）：skill=pptx_deck`——技能本次仍能执行，只是**不注入图库范围**（配图可能不命中团队图库）。出现在模型把工具入参传成非 JSON 文本时；待后续单独处理。

English:
- **Defect 1: an image turn dropped every tool, so skills could never run**
  - Path: both the plain streaming path and the delivery fallback swapped to `CreateBareVision` for “image this turn → use the vision model” — a **bare** agent (`Tools = null`, no memory injection).
  - Consequence (measured): the request sent to the model carried no `tools`; against the real endpoint with the same prompt, **with tools → structured `tool_calls`; without tools → DSML markup in the message body**. The skill therefore never executed and the delivery fallback produced no file in either of its two attempts.
  - Fix: added `AgentCatalog.GetOrCreateVision(agentId, visionModel)`, which goes through the **same** full assembly (tools / memory / skill chain / approval wrapper) and **only swaps the model** (cache key is per model). Both call sites now use it, and `CreateBareVision` is deleted so it cannot be misused again.
  - It was not the model's fault: the vision model `deepseek-v4-flash-vision-exp` **does** support tool calling (it returns structured calls when no image is involved).
  - Verified live (local Docker, *chat with ppt-assistant*, with an image): `构建模型客户端：... model=deepseek-v4-flash-vision-exp` → `运行中断等待交互` (the model raised an approval-gated skill call) → approve → `技能产物入库为附件：年度颁奖典礼_3页_暖阳版.pptx（356408 字节）`, with a downloadable attachment in the chat.
- **Defect 2: `Agents__DecisionModel=` (empty string) made every contextual decision fail**
  - Root cause: config binding happily binds an **empty string**, and an empty string is not `null` — the old code used `??`, so the blank was treated as a configured model name and the `ChatClient` constructor threw `ArgumentException: Value cannot be an empty string. (Parameter 'model')`.
  - Impact: every contextual decision and assignment routing call failed (the only trace: “contextual decision call failed, treating as no-speak”). Since the default value of `Agents__DecisionModel` *is* an empty string, this broke the default configuration.
  - Fix: model-name resolution now goes through `FirstNonBlank` (**blank = unset**) for `DecisionModel`, `ThinkingModel`, the per-agent `Model` and `BuildOpenAIChatClient`'s `modelOverride` — matching the repo's existing convention (`Agents__ApiKey` / `Agents__Endpoint` already fell back on blank).
  - Verified live: the log now reads `语境判定：模型=deepseek-chat P(发言)=1.000 阈值=0.3 → 发言（原始：YES）`.
- **New regression tests (9 total)**:
  - `DecisionModelAndParsingTests` +8: blank (`null` / `""` / `"   "`) `DecisionModel`, agent `Model` and `ThinkingModel` must fall back to the default model, and the production config shape is fed to `BuildOpenAIChatClient` (including a blank `Endpoint` and a blank `modelOverride`) asserting construction does not throw.
  - `VisionTurnToolRetentionTests` +1: an image turn must still be able to call tools (using the approval-gated built-in “announcement” tool as the probe — the mock only emits that call when tools are mounted). Both guards were **reverse-verified**: restoring the broken implementations does make them fail.
  - Full suite **1444 pass / 0 fail**.
- **Operations note (not a defect)**: a streaming message that is **older than 10 minutes and idle for 60s** is force-closed by the orphan-stream reaper; approving an interaction card left sitting for more than 10 minutes therefore fails to resume with “message does not exist or is not streaming” (that reply is lost). Approving promptly is unaffected.
- **Known minor noise (does not affect output)**: an occasional `注入图库检索范围失败（按无图库处理）：skill=pptx_deck` — the skill still runs, it just runs **without the image-library scope** (illustrations may therefore miss the team library). It appears when the model passes non-JSON tool arguments; to be handled separately.

# AG-UI 群聊桌面版 1.0.154 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.154 Release Notes (current Windows desktop release)

**版本说明**：1.0.154 修一个**没有任何报错**的静默缺陷：数字员工的「小决策」调用（语境触发时判**该不该发言**、组织化路由时判**派给谁**）在思考模式下走了推理模型，而这类调用的输出预算只有几个 token —— 推理模型把这几个 token **全花在思维链上、正文为空**。后果是：语境触发的数字员工**永远不发言**，指派路由解析出 **0 个下游**（这正是你反馈过的「只有问题提升、没有任务指派」）。因为既没有异常也没有错误日志，从表象极难定位。现在小决策**与思考模式解耦、固定走非推理模型**，并从 `logprobs` 读**概率**再按阈值判定（不再用 `StartsWith("YES")` 猜文本）；指派路由对空输出**重试并告警**，不再静默当成“没人合适”。顺带修掉一个同类的静默坑：**往知聚里加数字员工时只按 `agent_` 前缀判断“是不是智能体”**，导致组织编排产出的岗位（`bl_commander` 这类不带前缀的 ID）被记成真人成员——触发规则不注册，且它一发言就抛「发送者不是智能体成员」。
**Version note**: 1.0.154 fixes a **silent defect that raises no error at all**: a digital employee's “small decisions” (contextual triggering: *should I speak?*; org routing: *who should this go to?*) ran on the reasoning model in thinking mode, while those calls get only a handful of output tokens — the reasoning model spends them **entirely on its chain of thought, leaving the text empty**. The consequences: contextually-triggered employees **never speak**, and assignment routing parses **0 downstream targets** (exactly the “only escalations, no assignments” you reported). With no exception and no error log, this is very hard to pin down from the symptoms. Small decisions are now **decoupled from thinking mode and pinned to a non-reasoning model**, and they read a **probability** from `logprobs` compared against a threshold (no more guessing text with `StartsWith("YES")`); assignment routing now **retries and warns** on empty output instead of silently treating it as “nobody is suitable”. A second silent trap of the same family is fixed too: **adding a digital employee to a group decided "is this an agent?" purely from the `agent_` ID prefix**, so org-orchestration roles (IDs like `bl_commander` without that prefix) were recorded as *human* members — trigger rules were never registered, and the moment they spoke they threw “sender is not an agent member”.

## 小决策不再走推理模型 + 判定改用概率阈值（1.0.154）
# Small decisions off the reasoning model + probability-based verdicts (1.0.154)

中文：
- **现象**（两条，用户都报过）：① 语境触发（`Contextual`）的数字员工**从不发言**；② 组织化路由**只有问题提升、没有任务指派**。两者都**没有异常、没有错误日志**。
- **根因**：小决策调用的输出预算只有 `8`（发言判定）/ `64`（指派路由）个 token，而旧实现按思考模式选了**推理模型**。实测（生产实际使用的 `deepseek-flash`，同一判定提示）：

  | 模型 | 输出预算 | 正文 |
  |---|---|---|
  | `deepseek-flash` | 8 | **空**（8 个 token 全是推理） |
  | `deepseek-flash` | 32 | **空** |
  | `deepseek-flash` | 64 | **空**（推理恰好吃满 64 被截断） |
  | `deepseek-chat` | 8 | `YES`（completion 只花 1 个 token） |

  于是：旧发言判定 `decision.StartsWith("YES")` 对空正文**恒为假** → 永远沉默；旧指派路由 `resp.Text ?? "NONE"` 对**空字符串**不生效（`""` 不是 `null`）→ 解析出 0 个候选 → 静默走提升。预算 64 那档“有时空有时不空”正是**间歇性**失败的来源。
- **修法**：
  1. **模型解耦**：判定/路由固定走非推理模型（`Agents:DecisionModel` → 智能体 `Model` → 全局 `Model`），**故意无视 `Agents:ThinkingMode`**（`AgentCatalog.ResolveDecisionModelName`）。
  2. **概率代替文本前缀**：直调 OpenAI 兼容端点要回 `logprobs`，把候选 token 归一化成 **P(是)**，与 `Agents:DecisionMinProbability`（默认 `0.3`）比；判定调用固定 `temperature=0`（判定不该采样）。
  3. **灰区不猜**：拿不到概率才退回文本，且**只认第一个词**，认不出返回 `null`（调用方按“不发言”处理）——不再把 `MAYBE LATER` 里的 Y 当成“是”。
  4. **空输出 ≠ NONE**：指派路由遇空正文**重试一次**（更大预算）并记 `warn`，命中/未命中/重试都写日志（模型、P(发言)、阈值、原始输出）。
  5. 判定用量按同一口径**记入库**（判定提示很长，不记会低估配额消耗）。
- **阈值为何是 0.3 而不是 0.5**：实测同一提示只改“最新消息”，概率与“该不该发言”单调对应但整体偏低——直接点名 **0.827**、明说属于其职责 **0.334**、边缘相关 0.131、纯寒暄 0.048、与职责无关 0.012、明确无需发言 0.002。取 0.5 会把「属于其职责但未点名」这类**本该发言**的情形一并压掉。样本仅 6 档，上量后应用真实数据重调。
- **线上实测**（本机 Docker，真实 DeepSeek 端点，`ThinkingMode=true`）：临时把某岗注册为 `contextual` 后发两条消息，日志给出——
  - 无关消息：`语境判定：模型=deepseek-chat P(发言)=0.000 阈值=0.3 → 保持沉默（原始：NO）`
  - 点名消息：`语境判定：模型=deepseek-chat P(发言)=1.000 阈值=0.3 → 发言（原始：YES）`
  → 既证明**判定确实用了非推理模型**（思考模式开着，用的是 `deepseek-chat`），也证明阈值与日志可用。修复前同一路径会稳定判“沉默”（且日志只有一句「保持沉默」，看不出原因）。
- **顺带修（同类静默坑）**：群成员类型判定改为**先查智能体目录**、查不到才退回 `agent_` 前缀。实测：把 `bl_field_validator` 加进知聚后，它的回复直接抛「发送者不是智能体成员」——因为不带 `agent_` 前缀的岗位被记成了真人成员（触发规则也不注册）。
- **新增可配项**（都有安全默认，**不配不用改任何东西**）：`AGENTS_DECISION_MODEL`（留空=自动用非推理常规模型）、`AGENTS_DECISION_MIN_PROBABILITY`（默认 0.3）。已写入 `docker-compose.yml` 注释、`README` / `README.en.md` 与 `docs/execution-configuration.md`、`docs/agent-execution-algorithm.md`（新增 §2.1 小决策）。
- **新增回归**：`DecisionModelAndParsingTests`（9 条：判定模型不随思考模式漂移、实测 logprobs 形态解析、中文「是/否」与变体、无概率时文本兜底且 `MAYBE LATER` 必须不猜、空输出 ≠ NONE、白名单过滤/去重/中文逗号）、`GroupMemberTypeResolutionTests`（5 条：不带前缀的编排岗位按智能体记、真人不受影响、无目录时保持前缀兜底、显式 `MemberDetails` 优先）与 `Invoke_RouterHit_DoesNotRetryRouteCall`（成功命中时**不得**再跑一次路由调用）。最后这条是开发中真踩到的：重试分支漏了个 `return`，导致“命中”也一路落到重试里——每次成功指派都白烧一次模型调用、**第二次结果会覆盖第一次**、日志还把成功误报成“返回空输出”。因为 mock 两次回答相同、最终正文也正确，**只断言输出抓不住**，所以改为断言日志。护栏做了反向验证：把 `return` 去掉，它确实会红。
- **可观测**：提示缓存命中量进日志（实测同一提示二次调用 `2550` 中 `2304` 命中、延迟 249ms→139ms）；指派路由的每次决策（含未命中）都有 `info` 级日志。

English:
- **Symptoms** (both reported by the user): (1) contextually-triggered (`Contextual`) employees **never speak**; (2) org routing shows **only escalations, no assignments**. Neither raised an exception nor logged an error.
- **Root cause**: small-decision calls get only `8` (speak check) / `64` (assignment routing) output tokens, yet the old code picked the **reasoning** model whenever thinking mode was on. Measured on the real prompt with the production model `deepseek-flash`:

  | Model | Budget | Text |
  |---|---|---|
  | `deepseek-flash` | 8 | **empty** (all 8 tokens were reasoning) |
  | `deepseek-flash` | 32 | **empty** |
  | `deepseek-flash` | 64 | **empty** (reasoning exactly hit the 64 cap and was truncated) |
  | `deepseek-chat` | 8 | `YES` (1 completion token) |

  So the old speak check `decision.StartsWith("YES")` was **always false** on empty text → permanent silence; and the old router's `resp.Text ?? "NONE"` did not apply to an **empty string** (`""` is not `null`) → 0 candidates → silent escalation. The budget-64 row “sometimes empty, sometimes not” is exactly why the failure looked **intermittent**.
- **Fix**:
  1. **Model decoupled**: decision/routing calls are pinned to a non-reasoning model (`Agents:DecisionModel` → agent `Model` → global `Model`), **deliberately ignoring `Agents:ThinkingMode`** (`AgentCatalog.ResolveDecisionModelName`).
  2. **Probability instead of a text prefix**: the call goes straight to the OpenAI-compatible endpoint and asks for `logprobs`, normalizing candidate tokens into **P(yes)**, compared against `Agents:DecisionMinProbability` (default `0.3`); decision calls pin `temperature=0` (a verdict should not sample).
  3. **No guessing in the grey zone**: text fallback only when no probabilities are available, and it only reads **the first word** — anything unrecognized returns `null` (the caller treats that as “don't speak”) instead of reading the `Y` in `MAYBE LATER` as “yes”.
  4. **Empty output ≠ NONE**: assignment routing **retries once** with a larger budget and logs a `warn`; hits, misses and retries all log (model, P(speak), threshold, raw output).
  5. Decision usage is now **recorded** with the same accounting as everything else (decision prompts are long; skipping them understates quota usage).
- **Why the threshold is 0.3, not 0.5**: holding the prompt fixed and varying only the latest message, the probability tracks “should this agent speak?” monotonically but sits low overall — addressed by name **0.827**, explicitly in scope **0.334**, marginally related 0.131, pure small talk 0.048, unrelated to its role 0.012, explicitly not needed 0.002. A 0.5 threshold would suppress “in scope but not addressed by name”, which legitimately deserves speaking up. Only 6 samples so far; retune on real data once volume grows.
- **Verified live** (local Docker, real DeepSeek endpoint, `ThinkingMode=true`): after temporarily registering one role as `contextual`, two messages produced these log lines —
  - irrelevant message: `语境判定：模型=deepseek-chat P(发言)=0.000 阈值=0.3 → 保持沉默（原始：NO）`
  - addressed message: `语境判定：模型=deepseek-chat P(发言)=1.000 阈值=0.3 → 发言（原始：YES）`
  → proving both that the decision really used the **non-reasoning** model (thinking mode was on, yet `deepseek-chat` was used) and that the threshold plus logging work. Before the fix this same path reliably decided “silent” (with only a bare “staying silent” line, giving no clue why).
- **Also fixed (same family of silent traps)**: group member type resolution now **consults the agent catalog first** and only falls back to the `agent_` prefix. Measured: after adding `bl_field_validator` to a group, its reply threw “sender is not an agent member”, because a role whose ID lacks the `agent_` prefix had been recorded as a human member (and no trigger rule was registered either).
- **New knobs** (both have safe defaults — **no action needed if you don't set them**): `AGENTS_DECISION_MODEL` (empty = automatically the non-reasoning regular model), `AGENTS_DECISION_MIN_PROBABILITY` (default 0.3). Documented in `docker-compose.yml` comments, `README` / `README.en.md`, `docs/execution-configuration.md`, and `docs/agent-execution-algorithm.md` (new §2.1 on small decisions).
- **New regression tests**: `DecisionModelAndParsingTests` (9 cases: the decision model does not drift with thinking mode, parsing the measured `logprobs` shape, Chinese 是/否 and decorated variants, text fallback that must not guess on `MAYBE LATER`, empty output ≠ NONE, whitelist filtering/dedup/Chinese comma), `GroupMemberTypeResolutionTests` (5 cases: prefix-less orchestration roles recorded as agents, real users unaffected, prefix fallback when no catalog is injected, explicit `MemberDetails` wins), and `Invoke_RouterHit_DoesNotRetryRouteCall` (a successful hit must **not** trigger a second routing call). That last one was a real trap hit during development: the retry branch was missing a `return`, so a *hit* fell through into the retry — burning an extra model call on every successful assignment, **letting the second result overwrite the first**, and misreporting success as “empty output, retrying” in the logs. Since the mock answers identically both times and the final body is still correct, **output assertions cannot catch it** — hence asserting on logs. The guard was reverse-verified: removing the `return` does make it fail.
- **Observability**: prompt-cache hits are logged (measured on a repeated prompt: `2304` of `2550` tokens cached, latency 249ms → 139ms); every assignment-routing decision (including misses) now logs at `info`.

# AG-UI 群聊桌面版 1.0.153 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.153 Release Notes (current Windows desktop release)

**版本说明**：1.0.153 修一个**桌面版专属**的缺陷：**桌面版组合根漏挂了 7 个 HTTP API**，其中就包括**图库**——所以点「创建图库」根本不工作（持久化都注册了、前端界面也完整，就是路由没挂）。因为桌面根末尾有 `MapFallbackToFile("index.html")`，这些请求不会 404：`GET` 返回**首页 HTML**（前端 `res.ok` 为真、`res.json()` 解析失败）、`POST` 返回 **405**，表现就是“点了没反应”或莫名状态码，比 404 难查得多。本次补齐的是：图库、白标/品牌、配置治理、@ 建议、消息 👍/👎、编排计划暂停/继续、话题小结（另补两个状态单例与两项持久化注册）。同时加了两道护栏：① 拿**真实桌面宿主**逐条探测前端会调用的接口（不得退化成 HTML / 404 / 405）；② 比对两个组合根的 `app.Map*Api()` 清单，以后只在 Web 侧加 API 会直接红。**如果你是通过代理访问且看到 504，那不是本缺陷的表现**（应用自身不会返回 504），请把 `127.0.0.1` / `localhost` 加进代理绕过列表。
**Version note**: 1.0.153 fixes a **desktop-only** defect: the desktop composition root **failed to map 7 HTTP APIs**, including the **image library** — so “Create library” simply did not work (persistence was registered and the UI was complete; the routes were never wired). Because the desktop root ends with `MapFallbackToFile("index.html")`, those requests are not 404s: a `GET` returns the **SPA homepage** (the frontend sees `res.ok === true` and then fails to parse JSON) and a `POST` returns **405**, which surfaces as “I clicked and nothing happened” or a puzzling status code — far harder to diagnose than a 404. Now mapped: image library, branding, config governance, @-mention suggestions, message 👍/👎, plan pause/resume, and topic summaries (plus two state singletons and two persistence registrations). Two guardrails were added: (1) the **real desktop host** is booted and every frontend-facing route is probed, asserting none degrades to HTML / 404 / 405; (2) the two composition roots' `app.Map*Api()` lists are compared, so a future Web-only addition fails the suite immediately. **If you reach the app through a proxy and see a 504, that is not this defect** (the app itself never returns 504) — add `127.0.0.1` / `localhost` to the proxy bypass list.

## 桌面版漏挂 7 个 API（含图库）（1.0.153）
# Desktop host missed 7 API mappings (image library among them) (1.0.153)

中文：
- **现象**：桌面版点「创建图库」不工作；`@` 建议、消息 👍/👎、编排计划暂停/继续、话题小结、白标、配置治理同样无效。
- **根因**：项目有**两个组合根** —— `src/AguiGroupChat.Web/Program.cs`（Docker / 独立服务）与
  `src/AguiGroupChat.Desktop.Core/DesktopApp.cs`（桌面版进程内 Kestrel）。前端只有一份，两边各写一份
  `app.Map*Api()` 清单；新增 API 时只改了 Web 那份。图库就是这种：`RegisterImageLibraryPersistence()`
  在桌面早就注册了，唯独 `MapImageLibraryApi()` 没挂。
- **为何特别难查**：桌面根末尾有 `app.MapFallbackToFile("index.html")`，漏挂的路由不会 404 ——
  `GET` 命中 fallback 返回 **200 + text/html**（前端 `res.ok` 为真 → `res.json()` 失败 →
  界面报错或只静默不刷新）；`POST` 因同路径存在 GET 端点而返回 **405**。都不是“路由不存在”那种直观信号。
- **修法**：在 `DesktopApp.cs` 补齐 7 个 `Map*Api()` + `BrandingState` / `ConfigGovernanceState` 两个状态单例
  + `RegisterBrandingPersistence()` / `RegisterConfigGovernancePersistence()`；并写上清单同步的注释。
  **故意不挂**两个（已写明理由）：`MapNativeTunnelApi`（公网 Hub + 内网桥的反向隧道，本机回环无意义且多一个暴露面）、
  `MapNativeBridgeDownloadApi`（下载本机桥安装包，桌面自己就是宿主）。
- **两道护栏（新增回归）**：
  - `DesktopCompositionTests.FrontendApi_IsMappedOnDesktopHost`：启动**真实桌面宿主**，逐条探测前端会调用的接口，
    断言响应不得是 SPA 首页 / 404 / 405（401/403/400 都算“路由存在”）——修复前 **9/10 红**，修复后全绿；
  - `DesktopCompositionTests.DesktopCompositionRoot_MapsEveryApiTheWebRootMaps`：用行首锚定的正则比对两个组合根的
    `app.Map*Api()` 清单（注释掉的算没挂），以后只在 Web 加 API 会直接红。**这条护栏我做了反向验证**：
    把 `app.MapImageLibraryApi();` 注释掉，它确实会红（第一版正则没锚行首，注释掉也“通过”——是个假护栏，已修）。
- **与 504 的关系（实测结论）**：应用自身不会返回 504 —— 全仓只有链接代理一处 504（访问目标链接超时，与本路径无关）。
  漏挂 API 的实际响应是 **HTML(200) / 405**。若在桌面确看到 504，几乎可判定是**请求经过了代理**
  （WebView2 用系统代理；代理连不到你本机的 127.0.0.1:5200 时会回 502/504），请把 `127.0.0.1` / `localhost`
  加入代理绕过列表（或把代理指向本机地址时排除）。
- **实测验证**：桌面宿主 HTTP 探测 **11/11 通过**（含建库 + 列表端到端）；Web 版同一接口实测仍 `200 application/json`
  （确认只是桌面侧问题，未影响 Docker / Web 部署）；全量 **1416 通过**。

English:
- **Symptom**: on the desktop, “Create library” did nothing; so did @-mention suggestions, message 👍/👎, plan pause/resume,
  topic summaries, branding, and config governance.
- **Root cause**: the project has **two composition roots** — `src/AguiGroupChat.Web/Program.cs` (Docker / standalone server)
  and `src/AguiGroupChat.Desktop.Core/DesktopApp.cs` (the desktop app's in-process Kestrel). There is one frontend but each root
  lists its own `app.Map*Api()` calls, and new APIs were only added to the Web list. The image library was exactly that:
  `RegisterImageLibraryPersistence()` had long been registered on the desktop, but `MapImageLibraryApi()` was never mapped.
- **Why it was especially hard to catch**: the desktop root ends with `app.MapFallbackToFile("index.html")`, so an unmapped route
  is not a 404 — a `GET` hits the fallback and returns **200 + text/html** (the frontend sees `res.ok === true`, then `res.json()`
  fails, so the UI errors out or silently does nothing), while a `POST` returns **405** because a GET endpoint exists for that path.
  Neither is the unambiguous “route does not exist” signal.
- **Fix**: added the 7 missing `Map*Api()` calls plus the `BrandingState` / `ConfigGovernanceState` singletons and their two persistence
  registrations to `DesktopApp.cs`, with a comment about keeping the lists in sync. Two APIs stay **deliberately unmapped** (reason
  documented): `MapNativeTunnelApi` (reverse tunnel for “public Hub + intranet bridge” — meaningless on loopback and one more exposed
  surface) and `MapNativeBridgeDownloadApi` (downloading the bridge installer; the desktop *is* the host).
- **Two guardrails (new regression tests)**:
  - `DesktopCompositionTests.FrontendApi_IsMappedOnDesktopHost` boots the **real desktop host** and probes every frontend-facing route,
    asserting the response is never the SPA homepage / 404 / 405 (401/403/400 all count as “route exists”) — **9/10 red before the fix**, all
    green after;
  - `DesktopCompositionTests.DesktopCompositionRoot_MapsEveryApiTheWebRootMaps` compares the two roots' `app.Map*Api()` lists with a
    line-anchored regex (commented-out calls count as missing), so a future Web-only addition fails the suite. **I reverse-verified this guard**:
    commenting out `app.MapImageLibraryApi();` does make it fail (my first regex was not line-anchored, so it “passed” with the call commented
    out — a false guard, now fixed).
- **Relation to the 504 (measured)**: the app itself never returns 504 — the only 504 in the entire repository is in the link proxy
  (target fetch timeout, unrelated to this path). The actual response for an unmapped API is **HTML(200) / 405**. If a 504 really does
  appear on the desktop, the request is almost certainly **going through a proxy** (WebView2 uses the system proxy, and a proxy that cannot
  reach your local 127.0.0.1:5200 answers 502/504) — add `127.0.0.1` / `localhost` to the proxy bypass list, or exclude them where the
  proxy is configured.
- **Verified**: desktop-host HTTP probing **11/11 pass** (including a create-and-list end-to-end); the same API on the Web build still returns
  `200 application/json` (confirming the problem was desktop-only and did not affect Docker / Web deployments); full suite **1416 passing**.

# AG-UI 群聊桌面版 1.0.152 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.152 Release Notes (current Windows desktop release)

**版本说明**：1.0.152 把「在线查看」接到了**技能库试运行**上 —— 以前在技能库点 ▶ 试运行内置文档技能（`docx_report` / `pptx_deck` / `xlsx_book` / `pdf_doc`），结果弹窗里只有一段文本，**文件确实生成了却拿不到**（只看得见一行路径）。现在试运行结果多出「📦 本次产出」区：可**直接下载**、也可**在线查看**（复用 1.0.151 的文档阅读器）。服务端把结果里的 `produce_file` 标记按与聊天回档**同一实现**校验入库，并登记归属——**产出者本人**可读 / 可下载 / 可预览，他人仍 403。Web 与桌面共用同一套 Hub / 网关 / 前端。
**Version note**: 1.0.152 brings **online viewing to skill library trial runs** — before this, clicking ▶ on a built-in document skill (`docx_report` / `pptx_deck` / `xlsx_book` / `pdf_doc`) produced a result modal containing nothing but text, so **the file was generated but unreachable** (you could only see a path). Trial-run results now carry a **📦 Produced this run** section whose files are **directly downloadable** and **viewable online** (reusing the 1.0.151 document reader). The server validates the `produce_file` marker out of the result with **the same implementation the chat recall path uses**, registers the files, and records ownership — the **producer** can read / download / preview them while everyone else still gets 403. Web and desktop share the same Hub / gateway / frontend.

## 技能试运行产出回档（1.0.152）
# Trial runs now recall their output files (1.0.152)

中文：
- **问题**：技能库「试运行」的结果只有文本。内置 docx / pptx / xlsx / pdf 技能是把文件写到**服务端磁盘**的，
  返回里只有一行路径 —— 用户看得到路径、拿不到稿子，也无法预览（而同样一个技能在聊天里是能拿到下载卡片的，很难想到差别在入口）。
- **做法**：`POST /ag-ui/skills/{skillId}/run` 现在返回 `attachments[]`。服务端从结果文本里解析 `produce_file` 标记，
  按与聊天回档**同一实现**校验（扩展名白名单 / 非空 / 产物尺寸上限）后入库为附件。
- **收口成一处（顺便消重）**：解析 + 校验 + 入库抽到 `ProducedFileMarker`（聊天路径与试运行共用），
  网关原来的那份内联实现删掉 —— 两条路共用一套实现，才不会一边修好另一边漂移。既有源码扫描守卫（“回档路径必须带白名单”）
  同步改成断言“网关确实走那个收口 + 收口里有白名单与尺寸上限”，并且此它更严（多钉了一条不得绕过）。
- **权限：产出者本人**。试运行产物不属于任何知聚消息，而附件校验要求“命中你能访问的知聚里某条未撤回消息”，
  直接回给前端就是个 403（自己的东西自己看不了）。新增 `SkillRunArtifactStore` 只记「这个附件是哪个用户试运行产出的」，
  因此**产出者本人**可读 / 可下载 / 可预览，**他人仍 403**（不是所有人可读 —— 试运行稿件可能含未公开内容）。
  登记持久化到扩展区（重启后旧链接仍有效），按保留期 7 天（与预览缓存同口径）与每人 50 条裁剪，随「清空一切」一并清。
- **未收集的路径**：本机桥 / 客户端执行的试运行**不**收集产物 —— 文件在用户自己机器上，服务端读不到
  （强行扫只会把服务端上的同名无关文件当产物）。
- **界面**：结果弹窗新增产出区（`#skillRunResultArtifacts`），每行 = 文件名 + 「⬇ 下载」+（办公文档才有的）「👁 在线查看」；
  每次重跑先清空上一次的产出（否则两次结果会混排）。在线查看沿用同一个预览弹窗，
  因此它被抬到 `ui-dialog-overlay`（z-80）：要从技能结果弹窗之上弹出来，且 `Esc` 只收预览、不连下层一起关。
- **实测验证（真实部署 + 真实内置技能 + 真实浏览器）**：
  - `node tools/verify_doc_preview.mjs` —— **35 项全过**（含新增 7 项试运行项）：真实试运行 `docx_report` 返回 `attachments=1`，
    地址为站内可下载链接；预览 200 + `%PDF` + 1 页；产出者可下载（200）；未登录 401。
  - `HEADLESS=1 node tools/ui-skill-run-artifacts.mjs`（新增 Playwright 脚本）—— **14 项全过**：产出区列出文件、
    下载直链带令牌、点「👁 在线查看」子窗载入 `blob:` PDF（可视区 **1058×485**）、`Esc` 只收预览弹窗
    （技能结果弹窗仍在）、`Esc` 后 blob 已回收、重跑先清空上一次产出。
  - 回归测试 **新增 10 个**（产物归属 9：仅产出者可读 / 过期裁剪 / 每人超量裁剪 / 快照恢复 round-trip / 清空；
    预览端点 1：试运行产物仅产出者可预览可下载），全量 **1405 通过**。

English:
- **The problem**: the skill library's trial-run result was text only. Built-in docx / pptx / xlsx / pdf skills write their file to
  **server disk** and return just a path — so the user could see the path but not obtain the document, and could not preview it
  (while the very same skill does produce a downloadable card when run in chat, which makes the difference hard to guess).
- **The fix**: `POST /ag-ui/skills/{skillId}/run` now returns `attachments[]`. The server parses the `produce_file` marker out of the
  result text and registers the files as attachments using **the same implementation as the chat recall path**
  (extension allow-list / non-empty / produced-file size cap).
- **One shared implementation (removing duplication)**: parsing, validation and storage moved into `ProducedFileMarker` (used by both the
  chat path and trial runs) and the gateway's former inline copy was deleted — one implementation for both paths, so fixing one cannot leave
  the other behind. The existing source-scanning guard (“the recall path must carry the allow-list”) now asserts that the gateway really goes
  through that chokepoint **and** that the chokepoint holds the allow-list plus the size cap, which is strictly stronger (it also pins that the
  gateway cannot bypass it).
- **Permission: the producer only.** A trial-run artifact belongs to no group message, while attachment access requires “hits an unrecalled
  message in a group you can access” — so handing it back to the frontend would just 403 (you cannot open your own file). A new
  `SkillRunArtifactStore` records which user produced which attachment, so **only the producer** can read / download / preview it while
  **everyone else still gets 403** (this is not world-readable — trial-run drafts may contain non-public content). The mapping is persisted to
  the extension area (links survive a restart) and pruned at 7 days (same as the preview cache) and 50 entries per user, and it is cleared by
  “reset everything”.
- **Paths not collected**: trial runs executed over the native bridge / on the client are **not** recalled — the file lives on the user's own
  machine and the server cannot read it (scanning anyway would only risk picking up a same-named unrelated server file).
- **UI**: the result modal gained an artifacts section (`#skillRunResultArtifacts`) where each row is the file name plus “⬇ Download” and,
  for office documents, “👁 View online”; re-running clears the previous run's artifacts first (otherwise the two runs would interleave).
  Online viewing reuses the same preview modal, which is therefore raised to `ui-dialog-overlay` (z-80) so it can appear above the skill result
  modal, with `Esc` closing only the preview and not the modal underneath.
- **Verified live (real deployment, real built-in skill, real browser)**:
  - `node tools/verify_doc_preview.mjs` — **35/35 pass** (including 7 new trial-run assertions): a real `docx_report` run returns
    `attachments=1` with an in-app downloadable URL; preview gives 200 + `%PDF` + 1 page; the producer can download (200); unauthenticated 401.
  - `HEADLESS=1 node tools/ui-skill-run-artifacts.mjs` (new Playwright script) — **14/14 pass**: the artifacts section lists the file, the download
    link carries the session token, clicking “👁 View online” loads a `blob:` PDF in the child modal (**1058×485** visible area), `Esc` closes only the
    preview (the skill result modal stays), the blob is revoked afterwards, and re-running clears the previous artifacts.
  - **10 new regression tests** (9 for artifact ownership: producer-only read, expiry pruning, per-user overflow pruning, snapshot round-trip,
    clear-all; plus 1 for the preview endpoint: a trial-run artifact is previewable and downloadable only by its producer); full suite **1405 passing**.

# AG-UI 群聊桌面版 1.0.151 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.151 Release Notes (current Windows desktop release)

**版本说明**：1.0.151 给**数字员工产出的办公文档加上「在线查看」** —— 消息里带 docx / xlsx / pptx 附件的，下载卡片旁多一个「👁 在线查看」：服务端用 LibreOffice 转成 PDF 后**内联**返回，前端在宽幅弹窗 iframe 里直接阅读（`GET /ag-ui/preview/{attachmentId}`），不用下载、不用装本地 Office；权限与下载**共用同一段校验**（未登录 401 / 非成员 403 / 已撤回不可读 / 附件不存在 404），因此没有绕过知聚权限的口子。转换结果按「附件 ID + 源文件指纹」缓存 7 天（实测首次 docx ≈2.5s、xlsx ≈1.7s、4.4MB/16 页 pptx ≈5.3s；二次均 ≈15ms）。顺带修掉两个**在验证本功能时被暴露出来的既有界面缺陷**：① `loadGroups()` 清空消息 DOM 却不重建，导致任何一次知聚列表刷新（点「🔄 刷新」、成员加入/退出、重连）都可能把聊天区留成空白；② `virtualRender()` 在尚未选中知聚时会对 null 取 `.messages`，抛异常并让消息区空白。Web 与桌面共用同一套 Hub / 网关 / 前端。
**Version note**: 1.0.151 adds **online viewing for the office documents digital employees produce** — a message carrying a docx / xlsx / pptx attachment gains a **👁 View online** button next to the download card: the server converts the file to PDF with LibreOffice and returns it **inline**, and the frontend reads it in a wide modal iframe (`GET /ag-ui/preview/{attachmentId}`) with no download and no local Office install. Permissions are **the very same check download uses** (401 unauthenticated / 403 non-member / refused once recalled / 404 missing), so there is no way around group permissions. Results are cached for 7 days per “attachment ID + source fingerprint” (measured: first docx ≈2.5s, xlsx ≈1.7s, a 4.4MB/16-page pptx ≈5.3s; every cache hit ≈15ms). Building this also flushed out **two pre-existing UI defects**: (1) `loadGroups()` cleared the message DOM without rebuilding it, so any group-list refresh (the 🔄 button, members joining/leaving, a reconnect) could leave the chat area blank; (2) `virtualRender()` read `.messages` off a null room when no group was selected yet, throwing and blanking the message area. Web and desktop share the same Hub / gateway / frontend.

## 交付物在线查看 + 两处聊天区空白缺陷（1.0.151）
# Viewing deliverables online, plus two blank-chat defects (1.0.151)

中文：
- **👁 在线查看办公文档**：消息附件卡片旁多一个按钮，点击弹出宽幅阅读器（`min(1100px, 94vw)` × `min(88vh, 900px)`），
  服务端转好的 PDF 以 `blob:` URL 喂给弹窗 iframe；头部有附件名与「⬇ 下载原件」，`Esc` / 「关闭」/ 点遮罩都能收起并**回收 blob**。
  覆盖 docx / xlsx / pptx（另含 odt / ods / odp / rtf）；**PDF 附件直通不转换**（再进弹窗只是复用同一套阅读体验）。
- **端点 `GET /ag-ui/preview/{attachmentId}`**：`200` + `application/pdf`，**不设** `Content-Disposition: attachment`（这正是它与
  `/ag-ui/files` 的区别 —— 后者按原格式返回，浏览器拿到 `.docx` 只会去下载）；带 `nosniff` 与 `Cache-Control: private, max-age=300`，支持 Range。
  `?token=` 与 `Authorization: Bearer` 均可（iframe 拿不到请求头，前端走前者）。
- **权限与下载共用同一段代码**（`ResolveAndAuthorize`）：404（附件不存在）/ 403（非该附件所在知聚的成员）/ 已**撤回**消息的附件不可读。
  非成员请求**不会**触发转换（不为越权请求白干活）——单测直接断言转换器调用次数不变。
- **失败语义**：不支持的类型 `400`（`BAD_REQUEST`）；服务端未装 LibreOffice `503`；该文档转不出 PDF（损坏 / 120s 超时）`500`；
  后两者错误码均为 `DOCUMENT_PREVIEW_FAILED`，`message` 给出可读原因。桌面版 / 精简部署若没装 LibreOffice，只会让这一个按钮不可用（提示可下载后用本地应用打开），**不影响其它功能**。
- **性能与稳定性**：转换串行（LibreOffice 并发会互相踩）+ **每次调用一次性 profile**（复用只快约 1 秒，但进程被超时杀掉后残留的锁会让
  **后续所有**转换失败）；产物按「附件 ID + 源文件指纹（长度 + mtime）」缓存在 `data/preview`，替换源文件自动失效、保留 7 天；
  缓存清理与「清空一切」联动。
- **修掉两处既有缺陷（验证本功能时暴露）**：
  - `loadGroups()` 在刷新知聚列表时调 `resetVScroll()` 清空了消息 DOM，却没有重建 —— 而它被「🔄 刷新」、成员加入/退出、
    `GROUP_CONNECTED`（重连）、进入知聚等**十余处**调用，因此聊天区会在刷新后变空白，要等下一条实时事件才恢复。修法：清空后立即 `renderMessages()`。
  - `virtualRender()` 把 `activeTopicMessages(r)` 算在了 `if (!r || n === 0)` 守卫**之前**，`r` 为 null（已进入应用但尚未选中知聚）时直接
    `TypeError: Cannot read properties of null`，消息区空白。修法：先判空再取消息。
- **实测验证（真实部署 + 真实文件 + 真实浏览器，不是 mock）**：
  - 后端端到端 `node tools/verify_doc_preview.mjs` —— **28 项全过**：4 种格式（docx 6.8KB→4 页 / xlsx 4.3KB→6 页 / pptx 4.4MB→16 页 / pdf 直通）
    都返回 200 + `application/pdf` + `%PDF` 魔数 + 页数 ≥1 + 无 `Content-Disposition: attachment`；二次取用缓存命中
    **2494ms→18ms / 1691ms→18ms / 5311ms→12ms / 14ms→14ms**；未登录 401、不存在 404、非成员 403、zip 400。
  - 界面 `HEADLESS=1 node tools/ui-doc-preview.mjs`（Playwright）—— **19 项全过**：只有办公文档附件出现入口（zip 不出现）、弹窗打开、
    标题为附件名、iframe 载入 `blob:` PDF 且可视区 **1058×485**、转换提示消失、错误区未出现、`Esc` 收起并在关闭后
    `src` 清空（blob 已回收）、「关闭」按钮同样生效。
  - 回归测试 **26 个新增**（转换器缓存/失效/并发/失败分类 15 + 端点权限与状态码 10 + 「漏注册预览服务时其它路由照常工作」1），
    全量 **1395 通过**。

English:
- **👁 View office documents online**: attachments gain a button beside the download card that opens a wide reader
  (`min(1100px, 94vw)` × `min(88vh, 900px)`); the server-converted PDF is handed to the modal iframe as a `blob:` URL. The header
  carries the file name and “⬇ Download original”, and `Esc` / “Close” / clicking the scrim all dismiss it and **revoke the blob**.
  Covers docx / xlsx / pptx (plus odt / ods / odp / rtf); **PDF attachments pass straight through** (opening the modal just reuses the same reader).
- **Endpoint `GET /ag-ui/preview/{attachmentId}`**: `200` + `application/pdf` **without** `Content-Disposition: attachment` — exactly how it
  differs from `/ag-ui/files`, which returns the original format so the browser just downloads a `.docx`. It carries `nosniff` and
  `Cache-Control: private, max-age=300`, and supports Range. Both `?token=` and `Authorization: Bearer` work (an iframe cannot send headers, so the frontend uses the former).
- **Permissions share the download path's code** (`ResolveAndAuthorize`): 404 missing / 403 non-member / attachments of **recalled** messages are unreadable.
  An unauthorised request never triggers a conversion (no free work for trespassers) — a unit test asserts the converter call count is unchanged.
- **Failure semantics**: unsupported type `400` (`BAD_REQUEST`); LibreOffice missing `503`; this document cannot be converted (corrupt / 120s timeout) `500`.
  The latter two use `DOCUMENT_PREVIEW_FAILED` with a readable `message`. On a desktop or slim deployment without LibreOffice only this one button
  is unavailable (with a “download and open locally” hint) — **nothing else breaks**.
- **Performance and robustness**: conversions are serialized (LibreOffice trips over itself when run concurrently) and each call uses a
  **one-shot profile** (reuse saves only ~1s, but a lock left behind by a timed-out process breaks **every subsequent** conversion);
  artifacts are cached under `data/preview` per “attachment ID + source fingerprint (length + mtime)”, invalidated when the source changes, kept 7 days,
  and cleared by “reset everything”.
- **Two pre-existing defects fixed** (surfaced while validating this feature):
  - `loadGroups()` called `resetVScroll()` (which clears the message DOM) without rebuilding it, and it is invoked from a dozen places — the 🔄 refresh
    button, members joining/leaving, `GROUP_CONNECTED` on reconnect, entering a group — so the chat area could go blank until the next realtime event.
    Fix: rebuild with `renderMessages()` right after clearing.
  - `virtualRender()` computed `activeTopicMessages(r)` *before* the `if (!r || n === 0)` guard, so a null room (in the app but no group selected yet)
    threw `TypeError: Cannot read properties of null` and blanked the message area. Fix: null-check before reading messages.
- **Verified live (real deployment, real files, real browser — not mocked)**:
  - Backend end-to-end `node tools/verify_doc_preview.mjs` — **28/28 pass**: all four formats (docx 6.8KB → 4 pages, xlsx 4.3KB → 6 pages,
    pptx 4.4MB → 16 pages, pdf pass-through) return 200 + `application/pdf` + `%PDF` magic + ≥1 page + no `Content-Disposition: attachment`;
    second fetches hit the cache at **2494ms→18ms / 1691ms→18ms / 5311ms→12ms / 14ms→14ms**; 401 unauthenticated, 404 missing, 403 non-member, 400 for zip.
  - UI `HEADLESS=1 node tools/ui-doc-preview.mjs` (Playwright) — **19/19 pass**: only office attachments show the entry (a zip does not), the modal opens,
    the title is the attachment name, the iframe loads a `blob:` PDF with a **1058×485** viewport, the converting hint disappears, the error area stays hidden,
    `Esc` dismisses it and clears the iframe `src` (blob revoked), and the “Close” button works the same way.
  - **26 new regression tests** (15 for cache / invalidation / concurrency / failure classification, 10 for endpoint permissions and status codes, plus one
    proving other routes keep working when the preview service is not registered); full suite **1395 passing**.

# AG-UI 群聊桌面版 1.0.150 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.150 Release Notes (current Windows desktop release)

**版本说明**：1.0.150 两件事：① 库设置弹窗里加了 **🔍 试检索**（用当前档位当场试一次，看到会召回哪些 + 分数与片段预览，调严格度不再靠猜；知识库与图库都可用）；② 修了一个**在试用时被它自己暴露的真 bug** —— **词面兜底与语义门槛不同量纲**：BM25 的 sigmoid 分零重叠就给 0.5，于是“只与文档共用一个常用词”的提问也看着像 0.54，比向量路给无关内容的分（~0.41）还高，**能盖过任何档位**（实测：问“公司食堂今天中午吃什么”、文档里恰好有“公司”二字 → 0.537，连“严格”都拦不住）。Web 与桌面共用同一套 Hub / 网关 / 前端。
**Version note**: 1.0.150 does two things: (1) the library-settings dialog gains **🔍 Test retrieval**, which runs the currently selected preset on the spot so you can see what it recalls (scores plus snippet previews) instead of guessing; (2) it fixes a **real bug the feature itself exposed** — the **word-overlap channel and the semantic gate were on different scales**: a BM25 sigmoid score returns 0.5 even with zero term overlap, so a question that merely *shares one common word* with a document also looks like 0.54, outranking what the vector channel gives to unrelated content (~0.41) and thus **beating every preset** (measured: asking “公司食堂今天中午吃什么” against a document containing “公司” → 0.537, which even “strict” could not stop).

## 试检索 + 词面分与语义分对齐量纲（1.0.150）
# Test retrieval, and putting BM25 on the same scale (1.0.150)

中文：
- **🔍 试检索**（库设置弹窗内，图库 / 知识库共用）：输入一句提问 / 关键词，用**当前选中的档位**当场检索，
  列出命中项的**分数 + 文件名 + 片段预览**（片段截断 200 字），并回显“按当前档位（x）召回 n 条”；
  换档位自动重测；重新打开弹窗清掉上次结果。端点：图库复用 `/ag-ui/images/search`（限定本库），
  知识库新增 `POST /ag-ui/kb/{kbId}/search`（只回片段预览；门槛可临时指定＝“先试再存”；权限与“能不能读该库”一致）。
- **真 bug：词面兜底与语义门槛不同量纲**。BM25 是 sigmoid 归一化，**零词面重叠也给 0.5**，
  所以“只共用一个常用词”的无关提问会拿到 ~0.54，**看着比真实命中还高**，任何 `minScore` 都拦不住它。
  实测（真实文档 + bge-m3，同一个知识库）：
  | 查询 | 修正前接口分 | 纯向量分 |
  |---|---|---|
  | 真实提问（答案在文档里） | 0.7471 | 0.6987 |
  | **只共用一个常用词**（“公司食堂今天中午吃什么”） | **0.537** | 0.4090 |
  | 罕见号 ORION-7788 | 0.7087 | 0.4187 |
  | 零词面交集 | 0.2754 | 0.2754 |
- **修法**：词面命中先经 `Bm25Ranker.ToSimilarity`（零重叠 → 0，与余弦同量纲）再接一条**固定底线 0.35**
  （不随库的严格度变 —— 这条路的职责是笛住向量表示不好的**罕罕见词 / 专有号**，不该被语义门槛一票否决）：
  罕见号（换算后 0.417）仍能过 → 兜底没被修没；只共用一个常用词（0.074）→ 任何档位都不再算命中。
- **实测验证**（`tools/verify_kb_strictness.py` + `tools/ui-kb-strictness.mjs`，均在临时库里做、结束删库）：
  真实提问能召回；只共用常用词的提问在**严格档 0 条**（修正前是 1 条）；罕见号在**严格档仍召回**；
  零词面交集时**接口分与 psql 独立计算完全一致**（0.2924 = 0.2924，证明向量路量纲没被改坏）；
  界面：试检索能出结果、分数为 `\d.\d\d`、无关提问 0 条 + 解释文案、档位越严命中数不增、换档自动重测、
  重新打开清空结果、`Esc` 关闭不穿透。
- **修正一处旧说明**：1.0.148 / 1.0.149 的发布说明里写的“关键词词面命中不套这个门槛”**已被本版取代** ——
  现在词面命中要换算量纲并过固定底线 0.35（回归：`Bm25SimilarityScaleTests` 四条）。
- **测试**：新增 5 个（量纲换算 4 + 试检索端点 1），全量 **1369 通过**。

English:
- **🔍 Test retrieval** (inside the library-settings dialog, shared by image libraries and knowledge bases): type a question or keyword and it searches **with the currently selected preset**, listing each hit's **score + file name + snippet preview** (truncated to 200 chars) and reporting “at <preset>: n hit(s)”; changing the preset re-runs it and reopening the dialog clears stale results. Endpoints: image libraries reuse `/ag-ui/images/search` (scoped with `libraryIds`); knowledge bases gained `POST /ag-ui/kb/{kbId}/search` (snippet previews only; a temporary gate can be passed so you can try before saving; same read permission as the library).
- **A real bug: BM25 and the semantic gate were on different scales.** BM25 is sigmoid-normalised, so **zero term overlap still returns 0.5** — a question that merely shares one common word gets ~0.54, *looking better than a real hit* and beating every `minScore`. Measured on real data (bge-m3, one knowledge base): real question **0.7471** vs vector-only 0.6987; **one shared common word 0.537** vs 0.4090; rare code `ORION-7788` 0.7087 vs 0.4187; no overlap 0.2754 both ways.
- **Fix**: overlap hits are converted with `Bm25Ranker.ToSimilarity` (zero overlap → 0, same scale as cosine) and then must clear a **fixed floor of 0.35** (independent of the library's strictness, because that channel exists to rescue **rare terms / codes** the embedding handles poorly and should not be vetoed by a semantic gate): the rare code (0.417 after conversion) still passes — the rescue survives — while sharing a single common word (0.074) no longer counts at any preset.
- **Verified live** (`tools/verify_kb_strictness.py` + `tools/ui-kb-strictness.mjs`, both on a throwaway knowledge base): a real question is recalled; the common-word question returns **0 chunks at strict** (it returned 1 before); the rare code is still recalled **even at strict**; and with no word overlap the **API score matches an independent psql computation exactly** (0.2924 = 0.2924, proving the vector scale was not disturbed). In the UI: the probe returns rows with `\d.\d\d` scores, an unrelated question yields 0 rows plus the explanatory line, stricter presets never return more, changing the preset re-runs automatically, reopening clears results, and `Esc` closes without leaking.
- **One correction**: the “word-overlap hits are not gated” wording in the 1.0.148 / 1.0.149 notes is **superseded** — overlap hits are now converted and must clear the fixed 0.35 floor (regressions: four cases in `Bm25SimilarityScaleTests`).
- **Tests**: 5 new cases (four on the scale conversion plus one on the test-retrieval endpoint), **1369 passing** overall.

---

# AG-UI 群聊桌面版 1.0.149 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.149 Release Notes (current Windows desktop release)

**版本说明**：1.0.149 把上一版的**检索严格度**从图库扩到**知识库**：每个知识库也能单独设相似度门槛（界面在「📚 管理知识库」每行的 `⚙️`）。上一版图库的实测分尺是“无关词 ≤0.55、真实命中 0.62~0.88”，而知识库完全不同——实测**无关提问 0.31~0.38、真实命中 0.41~0.71**（分离度窄得多），所以档位也另配（宽松 0.15 / 标准 0.25 / 严格 0.40）。两个库类型共用同一个设置弹窗 `#libSetModal`。Web 与桌面共用同一套 Hub / 网关 / 前端。
**Version note**: 1.0.149 extends the **search strictness** from image libraries to **knowledge bases** (the `⚙️` on each row under “📚 Manage knowledge bases”). The image scale measured last time (noise ≤0.55, real hits 0.62-0.88) does not transfer: knowledge bases measure **0.31-0.38 for unrelated questions and 0.41-0.71 for real hits** — a much narrower margin, so they get their own presets (loose 0.15 / standard 0.25 / strict 0.40). Both library kinds share one settings dialog, `#libSetModal`.

## 知识库：按库可调的检索严格度（1.0.149）
# Knowledge bases: per-library search strictness (1.0.149)

中文：
- **为何要按库调**：文档是**短条目 / 目录型**（制度条款、产品参数、人名表）时，通用提问的相似度普遍偏高，门槛要**收紧**；
  是**长篇论述**时，一句话提问与其中某段的相似度普遍偏低，门槛要**放松**才召得回。
  全局 `Agents:Memory:MinScore`（默认 0.25）必然两头都不合适。
- **实测定档**（真实文档 + bge-m3，平台同一条公式 `1 - 余弦距离`）：
  | 提问 | 分数 |
  |---|---|
  | 知聚平台有什么创新点（答案在文档里） | **0.7181** |
  | 数字员工的组织架构是怎么协作的（语义相关） | **0.6071** |
  | 公司食堂今天中午吃什么（无关） | **0.3869** |
  | `qzxv-不存在-9987`（乱码） | **0.3140** |
  所以三档定为 **宽松 0.15 / 标准 0.25（= 平台默认）/ 严格 0.40**；
  对比图库（无关 ≤0.55、真实 0.62~0.88）可见两者分尺完全不同 —— 所以两个库类型各自一套档位，而不是照搬。
- **存在库上、库设了就以库为准**：`KnowledgeBaseCatalog.SearchAsync` 在**每个库**上分别解析生效门槛
  （库设了用库的，否则用调用方传的全局值）；值夹到 **0.10~0.80**；未设置的库**行为完全不变**（向后兼容）。
- **共用一套 UI**：知识库与图库用同一个 `#libSetModal`（交互完全一致，只有档位与说明不同）：
  未设置时回显“标准”；接口手工设的非预设值显示为“自定义”（否则一保存就被默默改掉）；`Esc` 关闭且不穿透下层管理弹窗。
- **边界**：仅创建者 / 管理员可改（`PUT /ag-ui/kb/{kbId}`）；
  **关键词词面命中（BM25 兜底）不套这个门槛** —— 词都对上了是另一种信号，分尺不同。
- **验证**（`tools/verify_kb_strictness.py` + `tools/ui-kb-strictness.mjs`，均在临时库里做、结束删库）：
  上表四个分数真实量出；同一无关提问在 **严格 0.40 下召回 0 条、宽松 0.15 下召回 1 条**（门槛确实在起作用）；
  接口回读 0.40、越界值夹到 0.80、其它知识库未被动过；界面：⚙️ 入口、三档取值、回显、保存、`Esc` 关闭且不穿透。
- **测试**：新增 6 个（库值覆盖全局 / 未设置时沿用 / 放松后召回了低于全局门槛的切片 / 按库隔离 / 夹紧与恢复 / 接口保存与越权 403 + 404），全量 **1364 通过**。

English:
- **Why per library**: short-item/catalogue-style documents (policy clauses, product specs, name lists) score high for any question, so **tighten** the gate; long prose scores low for a one-line question, so **loosen** it or nothing is recalled. A single global `Agents:Memory:MinScore` (0.25) can only be wrong for one of them.
- **Presets grounded in measurement** (real document + bge-m3, the platform's own formula `1 - cosine distance`): “知聚平台有什么创新点” (the answer is in the document) **0.7181**; a semantically related question **0.6071**; an unrelated one (“公司食堂今天中午吃什么”) **0.3869**; gibberish **0.3140**. Hence **loose 0.15 / standard 0.25 (= platform default) / strict 0.40**. Compare image libraries (noise ≤0.55, real hits 0.62-0.88): the two scales are entirely different, so each kind keeps its own presets rather than copying the other's.
- **Stored on the library, and the library wins when set**: `KnowledgeBaseCatalog.SearchAsync` resolves the gate **per library** (library value, else the caller's global value); values are clamped to **0.10-0.80**; unset libraries **behave exactly as before** (backwards compatible).
- **One shared dialog**: knowledge bases and image libraries use the same `#libSetModal` (identical interaction, different presets and copy): unset renders as “standard”, an API-set non-preset value shows as “custom” (otherwise saving would silently reset it), and `Esc` closes it without leaking to the management modal underneath.
- **Limits**: creator/admin only (`PUT /ag-ui/kb/{kbId}`); **word-overlap hits (the BM25 fallback) are not gated** — matching words is a different signal on a different scale.
- **Verified** (`tools/verify_kb_strictness.py` + `tools/ui-kb-strictness.mjs`, both on a throwaway library they delete afterwards): the four scores above measured live; the same unrelated question recalls **0 chunks at strict 0.40 and 1 chunk at loose 0.15**; the API round-trips 0.40, clamps out-of-range input to 0.80 and leaves other knowledge bases untouched; the UI check covers the ⚙️ entry, the three presets, the echo, saving, and `Esc` closing without leaking.
- **Tests**: 6 new cases, **1364 passing** overall.

---

# AG-UI 群聊桌面版 1.0.148 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.148 Release Notes (current Windows desktop release)

**版本说明**：1.0.148 给图库加了**检索严格度**（每个库一个相似度门槛，界面在「🖼️ 管理图库」每行的 `⚙️`）。上一版把 Word / PDF 的配图门槛统一到 0.6，但一个全局值对**所有**图库并不合适：描述是短人名 / 标签的库，通用关键词得分普遍偏高（0.6 也容易配错）；描述是长句的库，标题式查询得分偏低（实测 0.62，0.6 勉强过线）。现在**库设了就以库为准**（可更松或更紧），没设就沿用技能默认值。Web 与桌面共用同一套 Hub / 网关 / 前端。
**Version note**: 1.0.148 adds a **search strictness** setting to image libraries (one similarity gate per library; `⚙️` on each row under “🖼️ Manage image libraries”). The previous release moved Word/PDF illustration onto a 0.6 gate, but one global value fits no library well: captions that are short names/tags score high across the board (0.6 still picks wrong ones), while long-sentence captions score low for heading-style queries (0.62 measured — barely past 0.6). Now **the library wins when set** (looser or stricter), and unset libraries keep the skill default.

## 图库：按库可调的检索严格度（1.0.148）
# Image libraries: per-library search strictness (1.0.148)

中文：
- **为何要按库调**：图库之间描述风格差别很大 ——
  描述是**短人名 / 标签**（“刘佳俊”“背景”）时，通用关键词得分普遍偏高，门槛要**收紧**（否则容易配上不相干的图）；
  描述是**长句**时，标题式查询得分普遍偏低（实测 **0.62**），门槛要**放松**才配得上。一个全局常量两头都不合适。
- **存在图库上，库设了就以库为准**：`/ag-ui/images/search` 在**每个库**上分别解析生效门槛
  （库设了用库的，否则用调用方传的 `minScore`，再否则 0.25 兜底）。技能默认传 0.6，所以**未设置的库行为完全不变**（向后兼容）。
- **界面**：管理图库每行 `⚙️` → 三档下拉 **宽松 0.5 / 标准 0.6（推荐）/ 严格 0.72** + 一段说明；
  未设置时按“标准 0.6”回显；接口手工设的非预设值（如 0.65）会以“自定义”选项回显 —— 否则一保存会被默默改回 0.6。
- **边界**：值夹到 **0.30~0.95**（低于 0.3 等于不筛，高于 0.95 连本人照片都配不上）；
  仅创建者 / 管理员可改（`PUT /ag-ui/image-libs/{libId}`）；
  **关键词词面命中（BM25 兜底）不套这个门槛** —— 词都对上了是另一种信号，分尺不同。
- **实测**（真实图库 + 真实模型，`tools/verify_lib_strictness.py`，全程在临时库上做、结束删库）：
  先量出某查询的真实相似度 **0.6471**，然后：库=0.71、调用方传 0.5 → **不命中**；库=0.59、调用方传 0.6 → **命中**（证明以库为准，两个方向都成立）。
  走 `docx_report` 技能链路同样：库收紧时**不配图 + warning**（文档里位图数 0），库放松时**配上**（回显 score 0.647，文档里位图数 1）。
- **测试**：新增 6 个（库值覆盖调用方 / 未设置时沿用 / 放松后低分图能配上 / 按库隔离 / 夹紧与恢复 / 接口保存与越权 403 + 404），全量 **1358 通过**。

English:
- **Why per library**: caption styles vary a lot — captions that are **short names/tags** (“刘佳俊”, “背景”) score high for topical keywords, so **tighten** the gate or unrelated photos get picked; **long-sentence** captions score low for heading-style queries (**0.62** measured), so **loosen** it for those to match at all. A single global constant can only be wrong for one of them.
- **Stored on the library, and the library wins when set**: `/ag-ui/images/search` resolves the gate **per library** (library value, else the caller's `minScore`, else 0.25). Skills send 0.6, so **unset libraries behave exactly as before** (backwards compatible).
- **UI**: `⚙️` on each library row → a three-option dropdown (**loose 0.5 / standard 0.6 (recommended) / strict 0.72**) plus an explanation; unset renders as “standard 0.6”; a non-preset value set through the API is shown as “custom” — otherwise saving would silently reset it to 0.6.
- **Limits**: the value is clamped to **0.30-0.95** (below 0.3 is no filtering; above 0.95 not even the person's own photo matches); only the creator/admin can change it (`PUT /ag-ui/image-libs/{libId}`); **word-overlap hits (the BM25 fallback) are not gated** — matching words is a different signal on a different scale.
- **Verified live** against the real library and model (`tools/verify_lib_strictness.py`, everything on a throwaway library it deletes at the end): a query measured at **0.6471** is **blocked** with the library at 0.71 even though the caller passed 0.5, and **matches** with the library at 0.59 even though the caller passed 0.6 — the library wins in both directions. The same holds through `docx_report`: tightened → **no image + warning** (0 bitmaps in the document); loosened → **embedded** (echo shows score 0.647, 1 bitmap).
- **Tests**: 6 new cases (library overrides caller / unset follows the caller / loosening recovers a lower-scoring image / per-library isolation / clamping and reset / API save with 403 for non-owners and 404 for missing libraries), **1358 passing** overall.

---

# AG-UI 群聊桌面版 1.0.147 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.147 Release Notes (current Windows desktop release)

**版本说明**：1.0.147 把 **Word / PDF 的配图**提到 PPT 的水平 —— 以前它们“门槛没传（平台按 0.25 兜底，等于不筛）+ 没有二次尝试”，所以**更容易配错图、也配不上人名**。现在三者同口径：**显式门槛 0.6 + 上下文候选逐个查（图自己的 caption → 最近的标题 → 前面最近的文字块）+ 谁分高用谁**，并在返回里回显“用了哪条检索词、哪张图、多少分”。Web 与桌面共用同一套 Hub / 网关 / 前端。
**Version note**: 1.0.147 brings **Word/PDF illustration up to the PPT level**. They previously sent no `minScore` (the platform falls back to 0.25 — no filtering at all) and had no second attempt, so they were *more* likely to embed an unrelated photo and could not match a person by name. All three now share one mechanism: an explicit **0.6 gate**, **context candidates tried one by one** (the image's own caption → nearest heading → preceding text block) with **highest score wins**, and a response echo of *which query won, which file, what score*.

## Word / PDF 配图：门槛与上下文候选（1.0.147）
# Word/PDF illustration: a real gate plus context candidates (1.0.147)

中文：
- **原病**：两处技能发的检索请求是 `{"query":…,"scopeHandle":…,"topK":3}` —— **没传 `minScore`**，平台按 **0.25** 兜底，
  而实测无意义关键词能到 **0.44~0.55**、真实命中才是 0.62~0.88。于是“关键词不相关也能蹭过门槛”，
  把不相干的照片嵌进稿子；又因为没有“用本页/上下文文字再查一次”，**人名永远配不上**。
- **修法（与 PPT 1.0.145 同口径）**：
  1. **显式门槛**：请求里带 `minScore: 0.6`，阈值以下不静默（跳过该图 + `warnings` 写清原因）；
  2. **上下文候选**：关键词命中不够确定（< 0.78）或没命中时，依次用 **图自己的 caption/alt → 最近的标题 → 前面最近的文字块**
     各查一次，**取分数最高者**；一旦某个候选 ≥ 0.78 就停（最多再查 3 次）；
  3. **分开查，不拼串**（实测踩到）：把“caption + 标题 + 正文”拼成一句去查，关键的那个名字会被旁边的词稀释 ——
     人名直查 **0.88**，拼串只有 **0.63**，标题单独查 **0.62** 反而能用；
  4. **回显**：返回 JSON 多了 `images[{query,fileName,score}]`（图库**原始文件名**，不是服务器存储名 `asset_xxx.png`）；
     失败时 `warnings` 里**两条原因都写出来**（关键词 + 上下文），并保留“用的是哪条关键词”。
- **实测**（真实图库 + 真实模型，`tools/verify_docx_pdf_image_match.py`）：
  正文标题写“高效习惯优秀进步奖 · 刘佳俊”、模型只给“员工 颁奖 舞台” → 最终嵌入的**就是 `1刘佳俊.png`**
  （**按嵌入字节与图库文件逐字节比对**，不是只信它自己报的）；对照组（无关页）不配图且有告警。PDF 同理。
- **测试**：新增 3 个（低分命中输给上下文 / 已足够确定就不多查 / PDF 同场景），全量 **1352 通过**。

English:
- **The bug**: both skills sent `{"query":…,"scopeHandle":…,"topK":3}` with **no `minScore`**, so the platform fell back to **0.25** while measured nonsense keywords score **0.44–0.55** (real hits: 0.62–0.88). Irrelevant keywords slipped through the gate and unrelated photos got embedded — and with no second attempt, a page naming a person never matched.
- **The fix (same mechanism as PPT in 1.0.145)**: (1) an explicit **`minScore: 0.6`**, with nothing silent below it (the image is skipped and the reason names the keyword); (2) when the keyword hit is uncertain (below 0.78) or missing, **context candidates** — the image's own caption/alt, the nearest heading, the preceding text block — are queried in turn and the **highest score wins**, stopping as soon as one reaches 0.78 (at most 3 extra lookups); (3) candidates are queried **separately, never concatenated**: combining caption+heading+body dilutes the name that matters (0.88 for the bare name versus 0.63 combined, while the heading alone still scores 0.62); (4) responses now carry **`images[{query,fileName,score}]`** using the library's **original file name**, and a failed match reports both reasons (keyword and context).
- **Verified live** against the real library (`tools/verify_docx_pdf_image_match.py`): a heading “... · 刘佳俊” with the keyword “员工 颁奖 舞台” now embeds **`1刘佳俊.png`**, proven by **byte-comparing the embedded image against the library file**; the unrelated control case embeds nothing and warns. PDF behaves the same.
- **Tests**: 3 new cases, **1352 passing** overall.

---

# AG-UI 群聊桌面版 1.0.146 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.146 Release Notes (current Windows desktop release)

**版本说明**：1.0.146 把图库图片的**向量化状态**明明白白显示出来：以前只有“识别中/失败”才有徐标，一旦就绪就什么都不显示，用户无从判断“我改的描述到底生效了没、这张图能不能被匹配”。现在每张图都常驻一个状态：`⏳ 识别中…` / `✅ 已向量化` / `⚠ 仅按文件名匹配` / `❌ 失败`，图库行收起时也会提示「⚠ n 张未就绪」，保存描述时按钮显示「⏳ 向量化中…」（该请求要等重新向量化完成才返回，按钮恢复即可用）。Web 与桌面共用同一套 Hub / 网关 / 前端。
**Version note**: 1.0.146 makes an image's **vectorization state** visible. Previously a badge appeared only while processing or after a failure, so once an asset was ready nothing was shown and users could not tell whether a description edit had taken effect or whether the image could be matched at all. Every image now carries `⏳ Analyzing…` / `✅ Vectorized` / `⚠ Filename only` / `❌ Failed`; a collapsed library reports “n not ready”, and saving a description shows “⏳ Vectorizing…” (that request only returns once re-vectorization is done, so the button coming back is the completion signal). Web and desktop share the same Hub / gateway / frontend.

## 图库：向量化完成/未完成都有提示（1.0.146）
# Image library: vectorization status is now visible (1.0.146)

中文：
- **问题**：图片只有“⏳ 识别中…”与“❌ 失败”两种徐标，`ready` 时**什么都不显示**。
  于是“改完描述到底生效了没”“这张图到底能不能被语义匹配上”只能靠猜。
- **四种状态都常驻显示**（`assetBadge`，文案与悬浮说明走 i18n）：
  | 状态 | 徐标 | 含义 |
  |---|---|---|
  | `processing` | ⏳ 识别中… | 正在写描述并向量化；**这期间这张图暂时搜不到** |
  | `ready` 且有描述 | ✅ 已向量化 | 可被语义检索命中；改描述保存后立即重新向量化并覆盖旧向量 |
  | `ready` 但无描述 | ⚠ 仅按文件名匹配 | 向量只含文件名/标签，实际找不到（未配置视觉模型时上传即此态）—— 提醒补描述 |
  | `error` | ❌ 失败 | 悬浮看原因（如 embedding 不可用）；修好前不会被检索命中 |
- **图库行**（收起态）有未就绪图片时显示 `⚠ n 张未就绪`，全部就绪则不显示（处理中时显示 `⏳ 识别中…`）—— 不用逐库展开也能看出进度。
- **保存描述时按钮显示 `⏳ 向量化中…` 并禁用**：这个 PUT 在后端会 `await` 重新向量化（同一 assetId 覆盖写），所以按钮恢复＝新描述已生效。
- **验证**：新增浏览器端到端脚本 `tools/ui-imglib-vector-status.mjs`（Playwright），真实图库**只读**，
  所有写操作都在临时图库里做并在结束时删掉；实测覆盖：四种徐标均可见、上传后先「⏳ 识别中…」再「✅ 已向量化」、
  行内“未就绪”提示的出现与消失、保存时按钮忙态、描述存空→「⚠ 仅按文件名匹配」，全部通过。

English:
- **Problem**: only “⏳ Analyzing…” and “❌ Failed” badges existed — a `ready` image showed nothing, so there was no way to tell whether an edited description had taken effect or whether the image could be matched at all.
- **All four states are now always visible** (i18n text + tooltips): ⏳ Analyzing (and **unsearchable meanwhile**), ✅ Vectorized, ⚠ Filename only (ready but no description, so it will not be found; this is what an upload looks like with no vision model configured), ❌ Failed with the reason on hover.
- **Collapsed library rows** report `⚠ n not ready` (and `⏳ Analyzing…` while processing), so progress is visible without expanding each library.
- **Saving a description** shows a disabled “⏳ Vectorizing…” button: the PUT awaits re-vectorization server-side (upsert under the same assetId), so the button returning means the new description is live.
- **Verified** by a new Playwright end-to-end script, `tools/ui-imglib-vector-status.mjs`. Real libraries are read-only; every write happens in a throwaway library it deletes at the end. Covered: all four badges render, upload goes Analyzing → Vectorized, the row-level “not ready” hint appears and clears, the save-button busy state, and empty description → “Filename only”. All passing.

---

# AG-UI 群聊桌面版 1.0.145 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.145 Release Notes (current Windows desktop release)

**版本说明**：1.0.145 修的是**“图库里明明有这个人的照片，PPT 上也写着他的名字，却没配上”**。根因是模型看不到图库里有什么，只会按页面主题写关键词；现在配上图不再只看模型给的关键词：**图库命中不够确定时，会拿“本页文字”再查一次图库，谁分高用谁（自有素材优先于通用网图）**。顺带修掉两处会让人误判的静默行为：无外网时“离网熔断”把图库一起挡掉、以及配上图后仍报“已降级为题图”。
**Version note**: 1.0.145 fixes **“the library has this person's photo, the slide spells their name, and yet no photo matched”**. The model cannot see what is inside the library and only writes topical keywords; matching now also consults the **page's own text** and prefers the higher-scoring hit (your own assets beat generic web photos). Two misleading silent behaviours were fixed as well: an offline circuit-breaker that also blocked the *library*, and a downgrade warning that was reported even after a photo was successfully matched.

## 配图不认识人：拿“本页文字”再查一次图库（1.0.145）
# Illustrations did not know people by name (1.0.145)

中文：
- **现象**：图库里的描述就是人名（“刘佳俊”），幻灯片上也写着“高效习惯优秀进步奖 · 刘佳俊”，却没配上图 —— 用户的原话是“明明里面有一些图片的描述是有人名，并且 ppt 上也有人名，为什么不能自动配图？”。
- **根因**：模型**看不到图库内容**，只能按这一页的主题写关键词。实测（公司人员生活照库，bge-m3）：
  | 检索词 | 结果 |
  |---|---|
  | `刘佳俊`（名字直查） | `1刘佳俊.png` **0.8808** ✅ |
  | `员工 颁奖 舞台`（模型会写的那种） | 要么空手，要么靠那张合影描述里的“舞台/宴会厅”蹭到 **0.68** —— 配上的是**不相干的一家三口宴会合影** |
  也就是说：机制没问题，问题是“关键词里没有那个名字”。
- **修法（三步，都在 `ImagePathOf`）**：
  1. **本页文字兜底**：模型关键词完全没命中，或只配到 Wikimedia 的通用网图 → 拿**本页文字**再查一次图库。
  2. **分数择优**：图库命中**低于 0.78**（可信线，不是“能不能用”的底线 0.6）时，同样拿本页文字再比一次，**谁分高用谁** —— 0.68 那张输给 0.88 的本人照。
  3. **自有素材优先**：只要图库能配上，就不用通用网图。
- **边界（都是刻意定的）**：本页文字=标题 / 副标题 / 正文，**截断 120 字**（BM25 按查询词项数摊薄，太长反而糊掉关键的那个名字）；二次尝试**只查图库不出网**（中文页面文字丢给 Wikimedia 既没结果又白耗取图预算）；落选的候选会从 `images[]` 里去掉（清单不列没进稿子的图）；配上图**不报**降级警告。
- **顺带修掉两处“静默 / 假消息”**：
  1. 无外网环境中，第一页取图失败会置“离网熔断”，而那条短路写在**图库检索之前** → 从第二页起**连图库都不会再查**（内网部署反而一张自有图都用不上）。现在离网只跳过网络，图库照查。
  2. 降级警告改为由调用方在**确认两次都没配上**之后才报（之前二次尝试配上图了，仍会报“已改用自动生成的题图”）。
- **验证**：真实图库端到端（`tools/verify_name_illustration.py`，不硬编码名字，从库里挑一个“像人名”的描述来构造页面）：
  页面写“· 刘佳俊” + 关键词“员工 颁奖 舞台” → 命中 **`1刘佳俊.png`**（query = 本页文字）；对照组（页面与库无关）→ 不配图且有 warning。
  另一个老脚本 `tools/verify_image_recall_and_sizes.py` 三项也全过（无意义词不误配、正常词命中且产物 <6MB、改描述按新描述命中 0.9073）。
  新增 2 个回归（本页文字兜底 / 低分命中被顶掉），全量 **1349 通过**。

English:
- **Symptom**: library captions are literally people's names (“刘佳俊”), the slide says “高效习惯优秀进步奖 · 刘佳俊”, and still no photo matched.
- **Root cause**: the model **cannot see what is in the library** and only writes topical keywords. Measured on the real library (bge-m3): searching the bare name hits `1刘佳俊.png` at **0.8808** ✅, while `员工 颁奖 舞台` (what the model actually writes) either returns nothing or rides the words “舞台/宴会厅” in a *different* caption to **0.68** — embedding an unrelated family photo. The mechanism was fine; the keyword simply did not contain the name.
- **Fix (three steps, all in `ImagePathOf`)**: (1) if the model's keywords miss entirely — or only fetch a generic Wikimedia photo — search the library again with **the page's own text**; (2) if a library hit scores **below 0.78** (a *confidence* bar, distinct from the 0.6 usability bar), compare against the page-text hit and **keep the higher score** (the 0.68 family photo loses to the 0.88 portrait); (3) library hits always beat generic web photos.
- **Deliberate limits**: page text is title/subtitle/body **truncated to 120 chars** (BM25 divides by term count, so a long string blurs the one name that matters); the second attempt is **library-only, never the network** (Chinese page text returns nothing from Wikimedia and burns the photo budget); losing candidates are removed from `images[]` so the list never claims a photo that is not in the deck; no downgrade warning when a photo *was* matched.
- **Two misleading silent behaviours fixed**: (1) offline deployments set a circuit-breaker on the first failed fetch *before* the library was consulted, so from page two on **the library was never queried at all** — the breaker now only skips the network; (2) the downgrade warning is now emitted by the caller only after *both* attempts fail (it used to claim “fell back to generated art” even when the page-text retry had matched).
- **Verified**: live end-to-end against the real library (`tools/verify_name_illustration.py`, which hardcodes no names — it picks a person-like caption from the library itself): page text “· 刘佳俊” with keyword “员工 颁奖 舞台” now yields **`1刘佳俊.png`**; the control case (unrelated page) embeds nothing and warns. The earlier harness `tools/verify_image_recall_and_sizes.py` still passes all three checks. 2 new regression tests, **1349 passing** overall.

---

# AG-UI 群聊桌面版 1.0.144 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.144 Release Notes (current Windows desktop release)

**版本说明**：1.0.144 为当前 Windows 桌面版本。这一版修的是**“东西明明生成了，你却拿不到 / 配错了”**两类问题：带插图的稿子超过附件上限后**静默不挂到对话**（表现为没有下载入口），以及**关键词召回兜底实际上没在筛**（无意义关键词也能“命中”任意图片、任何提问都能把全库切片当成 RAG 命中）。同时把 PPT 嵌入图片瘦身，避免稿子再撞上体积上限。
**Version note**: 1.0.144 is the current Windows desktop release. It fixes two “it was produced but you never got it / got the wrong thing” problems: illustrated decks silently exceeding the attachment cap (so no download card appeared), and the keyword-recall fallback that never actually filtered (arbitrary images matched any keyword, and any question matched arbitrary knowledge-base chunks). Decks also slim embedded images now, so they stop hitting the size cap.

## 产物挂不上对话 / 附件上限（1.0.144）
# Attachments that never arrived (1.0.144)

中文：
- **现象**：单聊里数字员工说文件已生成，但对话里没有下载卡片 —— 有时有、有时没有。
- **根因**：带插图的稿子把图库照片**按原图嵌入**（一张 12MP 手机照 3~12MB），四张就把 .pptx 顶到
  **21.3MB / 31.2MB**，超过附件 20MB 上限 → 产物**不挂到对话**。而那条判定当时用 **Debug** 级日志 + 静默 `continue`，
  线上完全看不到原因（实测那份 31MB 的稿子就是这样“消失”的）。
- **修法（两头）**：
  1. **技能侧瘦身**：嵌入前把图最长边压到 1920px、照片重编 JPEG q85；
     **只有真的存在透明像素**才保 PNG（逐像素查 A&lt;255）—— 图库里很多“截图/照片型 PNG”，
     按容器格式一律保 PNG 的话 1920px 仍有 3MB，等于没瘦（实测：3.7MB 的 PNG 未被压小）；
     已经 ≤ 1.2MB 的图原样嵌（不重编、不丢像素）。
  2. **平台侧上限与可诊断性**：产物走独立上限 **64MB**（产物是我们自己生成的文件，不是不可信上传件），
     并且**超限/空文件/扩展名不在白名单一律打 Warning** —— 同样的错下次能一眼查到。
- **并已找回本机那两份产物**（文件本体一直在服务器上，只是没挂到对话）：`dist/recovered/` 下。

English:
- **Symptom**: the employee says the file is ready, but no download card shows up — sometimes it does, sometimes it does not.
- **Root cause**: illustrated decks embedded library photos **at original size** (one 12 MP phone shot is 3–12 MB), so four of them pushed a .pptx to **21.3 MB / 31.2 MB** — past the 20 MB attachment cap, so the file was never attached. That check logged at **Debug** and then silently `continue`d, making the cause invisible in production (that is how a 31 MB deck “vanished”).
- **Fixed on both ends**: (1) the skill slims images before embedding — longest side 1920 px, photos re-encoded as JPEG q85, PNG kept **only when the pixels actually use transparency** (a 1920 px screenshot-style PNG stays 3 MB otherwise, so format-based decisions do not slim anything), and images already ≤ 1.2 MB pass through untouched; (2) produced files get their own **64 MB** cap (they are our own output, not untrusted uploads) and every skip is now a **Warning**.
- **The two files from this machine were recovered** into `dist/recovered/` — they were always on the server, just never attached.

## 关键词召回兜底其实没在筛（1.0.144）
# The keyword fallback never filtered anything (1.0.144)

中文：
- **根因**：`Bm25Ranker.Score` 是 sigmoid 归一化，**零词面重叠也返回 0.5**（sigmoid(0)）；
  而图库与知识库的“关键词召回兜底”都写的是 `if (bm25 <= 0) continue;` —— 等于不筛。
- **影响**：任何查询都拿全库以 0.5 分召回，所以 ① `minScore` 形同虚设（0.5 &gt; 默认 0.25）；
  ② 无意义关键词也能“命中”任意照片，于是 PPT 会把不相干的人物照当“配图”嵌进去；
  ③ 知识库那边任何提问都能把全库最近 120 条切片当成“关键词命中”塞进 RAG 上下文。
- **修法**：新增 `Bm25Ranker.ZeroOverlapScore`（=0.5）并注明语义，两处兜底改为 `<= ZeroOverlapScore` 跳过。
  另外给 PPT 的图库检索显式设门槛 **minScore 0.6**（实测：无意义词 0.44~0.55，真实命中 0.62~0.84），
  阈值以下**不静默**：降级为题图并报 warnings（关键词写出来，用户可自己改词重试）。
- **关于“改了图片描述能不能按新描述匹配”**：能。已实测：把某图描述改成“紫罗兰色潜水艇在珊瑚礁间穿行”后，
  用该词组检索命中它 **0.9073**（排第一），而旧描述短语不再强命中它。改描述会**重新向量化并按同一 assetId 覆盖**旧向量。
- 测试：新增 4 个（大图瘦身 / 小图原样 / 图库与知识库的“零重叠不得召回”），全量 **1347 通过**；
  另外用真实图库与真实模型跑了三项端到端验证（脚本 `tools/verify_image_recall_and_sizes.py`）。

English:
- **Root cause**: `Bm25Ranker.Score` is sigmoid-normalised, so **zero term overlap still returns 0.5** (sigmoid(0)), while both the image-library and knowledge-base keyword fallbacks tested `if (bm25 <= 0) continue;` — i.e. no filtering at all.
- **Impact**: every query recalled the whole corpus at 0.5, so (1) `minScore` was meaningless (0.5 > the 0.25 default), (2) arbitrary photos “matched” nonsense keywords — a deck would paste an unrelated portrait as its illustration, and (3) in the knowledge base any question pulled the nearest 120 chunks into the RAG context as “keyword hits”.
- **Fixed** by adding `Bm25Ranker.ZeroOverlapScore` (= 0.5) with documented semantics and using it in both fallbacks, plus an explicit **minScore 0.6** for the deck skill's library search (measured: nonsense keywords 0.44–0.55, real matches 0.62–0.84). Below the threshold nothing is silent — it degrades to generated art and reports a warning naming the keyword.
- **On “does editing an image description affect matching?”** — yes. Verified live: after setting a caption to a nonsense-but-unique phrase, searching that phrase hits the image at **0.9073** (ranked first), and the old phrase no longer matches it. Editing re-vectorises and **overwrites** the vector under the same assetId.
- Tests: 4 new cases (big image slimmed, small image untouched, and “zero overlap must not recall” for both the image library and the knowledge base), **1347 passing** in total, plus a live three-part end-to-end check (`tools/verify_image_recall_and_sizes.py`).

---

# AG-UI 群聊桌面版 1.0.143 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.143 Release Notes (current Windows desktop release)

**版本说明**：1.0.143 为当前 Windows 桌面版本。这一版把 **PPT 的配色重做了一遍**：18 套命名调色板逐套重算角色（底色 / 标题 / 强调 / 卡片面），修掉了“中间调铺满卡片”“鲜艳色被洗成土色”“强调色与主色糊在一起”这三类观感问题；未指定 theme 时的默认也换成更好看的一套。Web 与桌面共用同一套 Hub / 网关 / 前端。
**Version note**: 1.0.143 is the current Windows desktop release. It reworks **deck colour**. All 18 named palettes have their roles (background / title / accent / card surface) derived again, fixing three things that made decks look cheap — saturated mid-tones tiling whole slides, vivid hues washed into mud, and accents indistinguishable from the title colour. The default when no `theme` is given also moves to a better-looking palette. Web and desktop share the same Hub / gateway / frontend.

## PPT 配色重做（1.0.143）
# Deck colour rework (1.0.143)

中文：
- **为什么改**：原来的角色推导用“往黑/白里混”（sRGB 线性混色）来满足可读性，代价是颜色失真：
  `vibrant-orange-mint` 的鲜明橙 FF9F1C 被洗成脏芥末 CC7F16、`modern-wellness` 的 E29578 被洗成灰褐 B57760。
  更明显的是**卡片面**：它直接取“调色板次亮色”，而那个色常常是个中间调饱和色
  （`education-charts` 的橙 F4A261、`art-food` 的琥珀 E09F3E、`nature-outdoors` 的茶色 DDA15E）——
  铺满卡片后整份稿子就是一片色块，观感很廉价。
- **现在（五条规则）**：
  1. **调色全在 HSL 空间做**：只改亮度、保色相与饱和，目标饱和从“感知彩度”推——鲜艳的仍鲜艳、柔和的仍柔和；
  2. **底线同时成立**：正文/主色 ≥ **7:1**（以前 4.5，投影下会发灰）、副色与强调 vs 底色 ≥ 3:1、
     徽标字 vs 强调 ≥ 4.5:1、强调与主色 ≥ 2.2:1（色相拉开 40° 时 ≥ 1.5:1）；
  3. **卡片面必须是“面”**：彩度 ≤ 0.30 且与底色能看出层次，不合格就按主色合成一层淡调（5~14% 饱和）；
  4. **强调色选“最鲜艳且分得开”的**，而不是“第一个能修合格的”——实测 `vintage-academic` 会挑到一个
     与深蓝主色一样暗的 990000，而不艳一点的 C1121F 明显更好看；
  5. **未写 `theme` 时默认 `business-authority`**（以前默认历史主题 `business`：白底+金强调，
     而金色在白底上只有 2.41:1，强调线/徽标本来就弱）。**显式写 `theme:"business"` 仍是原来那套**，老调用方不受影响。
- **效果举例（改前 → 改后）**：`modern-wellness` 强调色 B57760（灰褐）→ CC7756（陶土）；
  `coastal-coral` D8665D（暗砖）→ DF7067（珊瑚）；`nature-outdoors` E9BA90（浅杏，对底色只有 1.68）→ D86A09（烤橙）；
  `vintage-academic` 暗红 990000 → C1121F；`tech-night` 灰蓝 → 005AB8（深底上更亮、与黄主色互补）；
  `education-charts` 卡片从亮橙 F4A261 → 淡灰面；`art-food` 从琥珀 E09F3E → 淡米面。
- **钉住它的测试**：`NamedPalette_RendersAndKeepsTextReadable` 从“能读就行”扩到整套设计底线
  （7:1、卡片面彩度、主色不发灰、强调色得是点色、强调与主色分得开），18 套逐对跑。
- 测试：全量 **1343 通过**。

English:
- **Why**: role derivation satisfied readability by **mixing towards black or white in sRGB**, which distorts colour — `vibrant-orange-mint`'s vivid orange FF9F1C became a dingy mustard CC7F16, and `modern-wellness`'s E29578 turned into grey-brown B57760. Worse, **card surfaces** reused the palette's second-lightest colour, which is usually a saturated mid-tone (`education-charts` orange F4A261, `art-food` amber E09F3E, `nature-outdoors` tan DDA15E): across a deck that reads as slabs of colour and looks cheap.
- **Now (five rules)**: (1) all colour maths happens in **HSL** — only lightness moves, hue and saturation are kept, and target saturation comes from perceptual chroma, so vivid stays vivid and muted stays muted; (2) **all floors hold at once** — body and title text ≥ **7:1** (was 4.5, which greys out on a projector), secondary and accent vs background ≥ 3:1, badge text on the accent ≥ 4.5:1, accent vs primary ≥ 2.2:1 (or ≥ 1.5:1 when hues are 40°+ apart); (3) **card surfaces must look like surfaces** — chroma ≤ 0.30 and visibly stepped from the background, otherwise a pale tint of the primary is synthesised; (4) **the accent is the most vivid candidate that separates well from the primary**, not the first one that can be fixed (`vintage-academic` used to pick a maroon 990000 as dark as its navy title, where the slightly softer C1121F looks far better); (5) **with no `theme`, the default is now `business-authority`** (it used to be the legacy `business` theme whose gold on white only reached 2.41:1). **An explicit `theme:"business"` still yields the old look**, so existing callers are unaffected.
- **Concrete before → after**: `modern-wellness` accent B57760 (grey-brown) → CC7756 (terracotta); `coastal-coral` D8665D (dull brick) → DF7067 (coral); `nature-outdoors` E9BA90 (pale apricot at 1.68:1 on its background) → D86A09 (burnt orange); `vintage-academic` 990000 → C1121F; `tech-night` grey-blue → 005AB8 (brighter on the dark backdrop, complementary to the yellow title); `education-charts` cards F4A261 (orange) → a pale grey surface; `art-food` E09F3E (amber) → a pale sand surface.
- **Pinned by tests**: `NamedPalette_RendersAndKeepsTextReadable` grew from “readable is enough” to the full design floor (7:1, surface chroma, title not grey, accent must be a point colour, accent separates from primary), run across all 18 palettes.
- Tests: **1343 passing** in total.

---

# AG-UI 群聊桌面版 1.0.142 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.142 Release Notes (current Windows desktop release)

**版本说明**：1.0.142 为当前 Windows 桌面版本。这一版是两处界面打磨：**新建知识库 / 新建图库改为独立弹窗**（两处体验完全一致），以及**弹窗标题图标不再重复**（修前会看到「📚 📚 知识库管理」）。Web 与桌面共用同一套 Hub / 网关 / 前端。
**Version note**: 1.0.142 is the current Windows desktop release. Two UI refinements: **creating a knowledge base or an image library now uses a proper dialog** (identical in both places), and **dialog titles no longer repeat their icon** (they used to read “📚 📚 Knowledge Base Management”). Web and desktop share the same Hub / gateway / frontend.

## 新建库走弹窗（1.0.142）
# Library creation moves into a dialog (1.0.142)

中文：
- **为何改**：原本“＋ 创建知识库 / ＋ 创建图库”是在管理弹窗里**内联展开**一小块输入区，夹在搜索框与列表之间：
  不醒目，列表一长还会被顶出视野；而且与其它“新增类操作走弹窗”的口径不一致。
- **现在**：两处共用一个「新建」弹窗（`#libCreateModal`），由各自弹窗底部的 `＋ 创建…` 按钮打开。
  打开即聚焦名称输入框；`Enter` 提交；`Esc` / 取消 / 点遮罩关闭；名称为空时提示且**不关窗**；
  创建失败**保留已填内容**（改名重试不用重打）。
- **层级**：弹窗带 `ui-dialog-overlay`（`z-index:80`），高于管理弹窗的 `60` —— 从管理弹窗里唤起也在最上层、可正常输入（同类问题之前在一次“新建分组”上报过）。
  `Esc` 在**捕获阶段**处理，所以只关新建弹窗，不会把下层的管理弹窗一起关掉。
- **两处完全一致**：知识库与图库的标题、占位符、按钮文案随类型切换；说明文案分则告知创建后怎么用（上传文档 / 上传图片）。
  弹窗开着时切语言，标题/按钮/占位符**即时跟随**，且不动已填内容。

English:
- **Why**: “Create knowledge base / Create image library” used to expand an **inline input strip** inside the manager dialog, squeezed between the search box and the list — easy to miss, pushed out of view by a long list, and inconsistent with the “new item = dialog” convention used elsewhere.
- **Now**: both share one dialog (`#libCreateModal`) opened by the `＋ Create …` button at the bottom of each manager. Focus lands on the name field, `Enter` submits, `Esc` / Cancel / backdrop click closes; an empty name warns without closing, and a failed request **keeps what you typed** so a rename-and-retry is one keystroke.
- **Stacking**: the dialog carries `ui-dialog-overlay` (`z-index:80`), above the manager dialog's `60`, so it is operable on top when opened from inside it (the same class of bug was reported once for “new user group”). `Esc` is handled in the **capture phase**, so it closes only the create dialog and leaves the manager open.
- **Identical in both places**: title, placeholders and buttons switch with the kind, and the hint explains what to do after creating (upload documents / upload images). Switching language while the dialog is open updates title, buttons and placeholders **immediately** without touching your input.

## 弹窗标题图标不再重复（1.0.142）
# Dialog titles no longer repeat their icon (1.0.142)

中文：
- **现象**：图库管理标题显示为「🖼️ 🖼️ 图库管理」；知识库那处其实一直是「📚 📚 知识库管理」，只是一直没被注意到。
- **根因**：标题的图标写在了 HTML（译文字面）里，而 i18n 值里**又带了一个**。i18n 运行时是**整体替换**元素文本
  （fallback 会被替掉、不会叠加），所以规则应是「**图标放 HTML、i18n 只放文字**」—— 这也正是其它弹窗标题一贯的口径。
- **修法**：去掉 `kb.title` / `imgLib.title` 里的开头图标，图标留在标记里；并写脚本把“值以 emoji 开头且在元素前紧邻同一个字面 emoji”的用法全扫一遍，确认无其它重复点。
- 顺带删掉因内联面板下线而失效的 `.kb-create` 样式。

English:
- **Symptom**: the image-library title read “🖼️ 🖼️ Image Library”; the knowledge-base one had actually read “📚 📚 Knowledge Base Management” all along, just unnoticed.
- **Root cause**: the title's icon lives in the markup, and the translation value carried a second copy. The i18n runtime **replaces** an element's text (the fallback is overwritten, not appended), so the rule is **icon in the markup, plain text in i18n** — which is what every other dialog title already does.
- **Fix**: strip the leading icon from `kb.title` / `imgLib.title`, keep it in the markup, and audit every value that starts with an emoji while a literal copy of that emoji sits right before the element — no other duplicates remain.
- The now-dead `.kb-create` styles were removed along with the inline panels.

---

# AG-UI 群聊桌面版 1.0.141 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.141 Release Notes (current Windows desktop release)

**版本说明**：1.0.141 为当前 Windows 桌面版本。这一版新增**图库**：把公司自己的图片传上去，数字员工出稿（PPT / Word / PDF）时按语义**自动配图**，**不依赖外网**。Web 与桌面共用同一套 Hub / 网关 / 前端。
**Version note**: 1.0.141 is the current Windows desktop release. It adds an **image library**: upload your own pictures once, and digital employees then **illustrate PPT / Word / PDF deliverables by semantic search** — **with no internet dependency**. Web and desktop share the same Hub / gateway / frontend.

## 图库：自有图片，语义配图，不出网（1.0.141）
# Image library: your own pictures, semantic matching, fully offline (1.0.141)

中文：
- **要解决什么**：此前 PPT 的 `imageQuery` 只能去 Wikimedia 找照片。内网 / 合规场景里外网往往不可达，
  而且“公司自己有一批品牌图 / 产品图，数字员工配图时自己挑”才是真实需求。
- **怎么用**：`AI 角色管理 → 🖼️ 管理图库` 建库并上传图片（可多选）；上传后**视觉模型自动写中文描述**
  （描述里附检索要素，失败则退回文件名 + 手填描述），描述 + 标签 + 文件名一起向量化。
  出稿时在需要图的位置写 `imageQuery`（关键词）即可，库内命中就用本地图片直接嵌入 —— **不需要署名**，
  因为那是你自己上传的图。
- **三个内置文档技能都接通**，但口径按能力分开：
  - **PPT**（`pptx_deck`）：先查图库，未命中再回落 Wikimedia，都没有则降级为题图；
  - **Word**（`docx_*`）：只走图库（`{"image":{"imageQuery":"…"}}`），取不到就**跳过这张图**并在 `warnings` 说明；
  - **PDF**（`pdf_doc`）：只走图库，且只嵌 **PNG/JPEG**（PDFsharp 的限制）；只命中 WebP 时**宁可跳过并告知**
    “建议换成 PNG/JPEG”，也不硬塞 —— 硬塞会生成打不开的 PDF。
- **岗位可以绑定图库**（表单「图库（可选）」）：绑了 = 该岗位出稿**只用这几个库**（典型：对外宣讲岗只准用已审核的品牌图库）；
  不绑 = 用触发者本人可读的全部图库。**绑定的库全被删时不注入**（按“无图库”降级），而**不是**静默放宽成该用户全部图库。
- **安全设计（两条硬线）**：① 平台只往技能入参里注入一个**检索范围句柄**，技能**不自报图库 ID** ——
  入参是模型生成的，能自报就能让模型写个别人的 ID 把别人的图读出来；② `/ag-ui/images/search` 的
  **服务器本地路径只回给进程内自令牌**，登录用户只拿元数据与 `/raw` 地址。图库向量也**不参与群记忆检索**。
- **三个入口都注入**（聊天单聊/知聚/编排、技能库「试运行」、桌面宿主直跑）：
  顺手补掉一个口径缺口 —— 原先只有聊天路径注入，于是「聊天里能配图、界面上手动试运行却一张图都没有」。
- **顺手修掉一个真 bug**：图库允许上传 WebP，而 Word 只认 png/jpg/gif/bmp/tiff。
  现在按**文件头**判定并把认不出的重编成 PNG（旧实现按扩展名，遇到改过名的图会报“不支持的图片格式”并让整篇文档失败）。
- **界面**：管理弹窗与知识库同构（搜索 + 创建 + 展开缩略图网格 + 逐图改描述 + 多图上传 + 处理中状态轮询 + 删图/删库），
  数字员工表单新增「图库（可选）」绑定区（只罗列已选、新增走弹窗勾选），列表带 🖼️ 计数徽标；中英文案齐全。
- **实测（容器内真实链路）**：上传一张日落城市天际线图 → 视觉模型写出“11 栋建筑 / 日落 / 渐变天空 / 平涂矢量”等要素
  → 检索命中 **0.777** → 经平台跑 `docx_report`（带 `imageQuery`）→ 产物含嵌入图片、`warnings` 为空，
  日志可见 `已为文档技能注入图库检索范围：skill=docx_report libs=1`。
- 测试：新增 14 个（回环链路用假平台端点真跑一遍：句柄 + 自令牌 + 路径 + 嵌入 + 各类降级；绑定语义：收窄/不绑取全部/绑定失效不放宽/非文档技能不动/非 JSON 不动），
  全量 **1343 通过**。

English:
- **What it solves**: `imageQuery` on decks previously only searched Wikimedia. On intranets (and for compliance) the internet is often unreachable, and “we have our own brand/product shots — let the employee pick” is the real requirement.
- **How to use it**: create a library under `AI role management → 🖼️ Image library` and upload pictures (multi-select). A **vision model writes a Chinese description** (with search facets; on failure the filename plus your caption is used), and description + tags + filename are vectorised. Then just write `imageQuery` where a picture is wanted — a library hit embeds the local file and needs **no attribution**, because it is your own image.
- **All three built-in document skills are wired up**, with deliberately different policies: **PPT** tries the library first, then Wikimedia, then generated art; **Word** uses the library only (a miss **skips that image** and explains itself in `warnings`); **PDF** uses the library only and embeds **PNG/JPEG only**, skipping rather than force-fitting a WebP (which would produce an unopenable PDF) and telling you to convert it.
- **Positions can bind libraries** (the new “Image library (optional)” form section): bound = that role may illustrate from **those libraries only** (e.g. a public-facing role restricted to approved brand assets); unbound = every library the triggering user can read. If every bound library is deleted, **nothing is injected** rather than silently widening to all of the user's libraries.
- **Security (two hard rules)**: the platform injects only a **search-scope handle**, never library IDs — the skill input is model-generated, so self-declared IDs would let a model read someone else's pictures; and the **server-side file path is returned only to the in-process self token**, while logged-in users get metadata and `/raw` URLs. Library vectors are **excluded from group-memory retrieval**.
- **All three entry points inject the scope** (chat, the skill-library “Run” button and the desktop host), closing a gap where illustrations worked in chat but not when test-running the skill from the UI.
- **A real bug fixed along the way**: the library accepts WebP while Word only embeds png/jpg/gif/bmp/tiff. Detection is now by **file signature** with re-encoding to PNG for anything else (the old code trusted the extension, so a renamed file failed the whole document).
- **UI**: the management dialog mirrors the knowledge-base one (search, create, expandable thumbnail grids, per-image caption editing, multi-file upload, processing-state polling, delete image/library); the agent form gains an “Image library (optional)” binding section (lists only what is selected, adds via a picker dialog) and the list shows a 🖼️ count badge. Chinese and English strings are complete.
- **Verified live in the container**: upload a sunset-skyline picture → the vision model describes it (11 buildings, sunset, gradient sky, flat vector) → semantic search scores **0.777** → run `docx_report` through the platform with `imageQuery` → the output contains the embedded image with empty `warnings`, and the log shows `已为文档技能注入图库检索范围：skill=docx_report libs=1`.
- Tests: 14 new cases (the loopback chain exercised against a fake platform endpoint — handle, self token, path, embedding and every degradation path; plus binding semantics: narrowed to bound, all readable when unbound, no widening when bindings are gone, non-document skills untouched, non-JSON input untouched); **1343 passing** in total.

---

# AG-UI 群聊桌面版 1.0.140 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.140 Release Notes (current Windows desktop release)

**版本说明**：1.0.140 为当前 Windows 桌面版本。这一版修掉两个「内容凭空消失」的问题：PPT 里**文字压出自己的框**（PowerPoint 打开时才看得出来），以及某种路由下数字员工先回一句「（X 代为处理）」、**之后整条消息再无正文**。Web 与桌面共用同一套 Hub / 网关 / 前端。
**Version note**: 1.0.140 is the current Windows desktop release. It fixes two ways content silently went missing: **text spilling out of its own box** in decks (only visible once PowerPoint opens the file), and a routing path where an agent replied with a single “(handled by X)” line and **nothing else**. Web and desktop share the same Hub / gateway / frontend.

## PPT：文字不再压出框（1.0.140）
# PPT: text no longer spills out of its box (1.0.140)

中文：
- **根因不是「某处字号写大了」**，而是我们的「量高」从一开始就算不出真实高度：行高系数当成 1.0（真实是 1.27~1.45 em）、
  段前距的单位小了 100 倍、粗体 / 左缩进 / 中英混排的真实字宽都没算。三项叠加，估出来的高度常常不到真实值的一半——
  于是**「缩字号」几乎从不触发**。
- **以前为什么看不出来**：每个文本框都带着 `<a:normAutofit/>`，LibreOffice 打开时会**替我们缩**；
  但 **PowerPoint 打开时并不重算 autofit**，用户看到的就是溢出后的样子。
- **标定**（容器里渲染 + `pdftotext -bbox` 实测）：自然行高 = **1.455 em**；`spcBef` 是在行高之上叠加的磅值；
  汉字 advance 正好 **1.0 em**（所以汉字不留余量、拉丁保留 5%）。修正后 `FitScale` 用**二分**求「放得下的最大缩放」，
  下限 12pt、行距不低于 100%（行距低于 100% 时汉字墨迹会冒出行框）。
- **装不下就分页，不丢内容**：要点页 / 小结 / 目录按能放下的条数**自动拆页**（标题带「（n/m）」），表格按行切页；
  结构固定的框（卡片 / 示意图层 / 封面 / 图注）会截断，并在 `warnings` 里**说清楚截掉了多少字**。
- **出稿自检新增 `textOverflow`**：从写出的 XML 反推框与字号再复核一遍；`replaceText` 改长后单独复核并进 `warnings`。
- **独立复核工具 `tools/verify-pptx-textfit.py`（新）**：先把产物里的 `<a:normAutofit/>` **摘掉**再交给 LibreOffice 渲染
  （「渲染器替我们兜底」这条路被断掉），再用 `pdftotext -bbox` 逐词查越界。本机字体与容器字体（`Noto Sans CJK SC`）各跑一遍：
  **14 页、0 越界 / 0 页外文字**。
- 顺带修掉两个真 bug：四象限纵轴名（36pt 宽的框里塞 4 个字会折行溢出）、目录卡片序号行没计入高度。
- 测试：全量 **1303 通过**（新增分页 / 截断 / 测量 / 自检用例）。

English:
- **The root cause was not “a font size written too large”** — our height measurement could never produce the real height: the line-height factor was treated as 1.0 (it is really 1.27–1.45 em), the space-before value was off by a factor of 100, and bold text, left indent and mixed CJK/Latin advance widths were not accounted for. Together these made the estimate less than half the true height, so **shrink-to-fit almost never fired**.
- **Why it used to look fine**: every text box carried `<a:normAutofit/>`, so LibreOffice **shrank the text for us**; **PowerPoint does not recompute autofit when opening**, which is the version users saw — overflowing.
- **Calibration** (render in the container, measure with `pdftotext -bbox`): natural line height is **1.455 em**; `spcBef` adds on top of the line height; a CJK glyph advances exactly **1.0 em** (so CJK gets no slack, Latin keeps 5%). `FitScale` now **binary-searches** for the largest scale that fits, floored at 12pt with line spacing never below 100% (below that, glyph ink escapes the line box).
- **When it still does not fit, paginate instead of dropping content**: bullet / summary / TOC pages **split across slides** by how many items fit (titles carry “(n/m)”) and tables split by row; fixed boxes (cards, diagram layers, cover, captions) trim and **say in `warnings` how much was cut**.
- **QA now reports `textOverflow`**: the written XML is re-read to re-check boxes against font sizes; `replaceText` re-checks after lengthening a run.
- **Independent checker `tools/verify-pptx-textfit.py` (new)**: it strips `<a:normAutofit/>` from the product **before** rendering with LibreOffice — closing the “renderer covers for us” escape hatch — then flags every word whose `pdftotext -bbox` box escapes its own text box. Run against local fonts and the container font (`Noto Sans CJK SC`): **14 slides, 0 overflows, 0 words off-slide**.
- Two real bugs fixed along the way: the quadrant page’s vertical axis label (four characters in a 36pt-wide box wrapped and overflowed) and the TOC card’s index line not being counted towards its height.
- Tests: **1303 passing** overall (new pagination / trimming / measurement / QA cases).

## 「代为处理」之后没有正文（1.0.140）
# Nothing after “handled by X” (1.0.140)

中文：
- **现象**：与某位数字员工单聊，只收到一句「（X 代为处理）」，此后再无内容——无报错、无审批卡，技能也从未执行。
- **根因有两层**：① 路由判断见到「确定性计划」就直接走计划路径，理由写的是「计划能干活」；
  但**文档生成类技能在计划里是被刻意跳过的**（结构化 JSON 入参必须由模型当工具构造），
  只挂这类技能的叶子岗位因此落到**轻量自答路径**——那条路径**没有工具、也不处理审批**，
  模型返回的是一个审批请求而不是正文，于是正文为空。
  ② 兜底只兜 `null`（`??=`），**兜不住空串 / 空白串**，剥壳后变空也一样漏掉，整条消息就只剩前缀。
- **修法**：新增 `PlanPathCanDoTheWork(def)` —— 有可派下级、或本岗有「非文档生成 / 非落库」技能才算计划能干活；
  **只挂文档生成技能的叶子**回落到完整流式路径。轻量自答为空而本岗有可执行技能时，**在同一条消息、同一个 run 内补跑一次完整流式**（复用既有审批机制）。
  三处收尾统一按 `IsNullOrWhiteSpace` 判空并补上可展示的兜底文案。
- **验证**：新增 5 个用例（含两个反向保护：有计划技能 / 有下级时**不应**回落到流式），
  并用 `MockChatClient` 的触发词稳定复现「模型什么都不回」；改回旧逻辑确认用例失败后再恢复。
  实盘复测两条单聊：8 页图示版 PPT、16 条长要点 + 四象限 + 12 行表格的详版 PPT，均正常出稿（前者**以前就是空回复**）。

English:
- **Symptom**: in a direct chat with a digital employee the only content received was a single “(handled by X)” line — no error, no approval card, and the skill never ran.
- **Two root causes**: ① the routing check short-circuited to the plan path whenever deterministic planning was on, on the assumption that “the plan can do the work”; but **document-generation skills are deliberately skipped inside the plan** (their structured JSON arguments must be built by the model as a tool call), so a leaf agent holding only such skills landed on the **lightweight self-answer path** — which **has no tools and does not handle approvals**, so the model returned an approval request instead of prose and the body came back empty. ② The fallback only covered `null` (`??=`), **not empty or whitespace strings**, and unwrapping a coordinated answer could leave it empty too, so the message was reduced to its prefix.
- **Fix**: a new `PlanPathCanDoTheWork(def)` only lets the plan path run when the agent can delegate downward or holds a skill that is neither document generation nor record-keeping; **a leaf holding only document skills falls back to the full streaming path**. When the lightweight self-answer comes back empty but the agent has an executable skill, the full streaming path is **re-run inside the same message and the same run** (reusing the existing approval machinery). All three closing paths now test `IsNullOrWhiteSpace` and add a displayable fallback.
- **Verification**: 5 new cases (including two guards asserting the streaming fallback does **not** kick in when plan-eligible skills or subordinates exist), plus a `MockChatClient` trigger that deterministically reproduces “the model returns nothing”; reverting the fix was confirmed to fail the cases before restoring it. Two end-to-end chats re-ran cleanly: an 8-slide illustrated deck and a text-heavy deck (16 long bullets + quadrant + 12-row table) — the former **used to be an empty reply**.

# AG-UI 群聊桌面版 1.0.139 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.139 Release Notes (current Windows desktop release)

**版本说明**：1.0.139 为当前 Windows 桌面版本。PPT 技能新增**自动插图**：一类是**示意图**（金字塔 / 漏斗 / 四象限 / 循环闭环 / 层叠架构），用形状把关系画出来；另一类是**程序化题图**，图片缺失时自动按主题配色生成。**全程不需要用户提供任何图片素材、不联网、无版权问题**（产物里零图片文件）。Web 与桌面共用同一套 Hub/网关/前端。
**Version note**: 1.0.139 is the current Windows desktop release. The PPT skill can now **illustrate itself**: diagram pages (pyramid / funnel / quadrant / cycle / stack) draw relationships with shapes, and a procedural hero image is generated from the theme palette whenever an image is missing. **No user-supplied assets, no network, no licensing concerns** (the output contains zero image files). Web and desktop share the same Hub / gateway / frontend.

## PPT：自动插图（1.0.139）
# PPT: automatic illustrations (1.0.139)

中文：
- **示意图页型（新）**：`pyramid`（3~6 层，顶层最窄，斜边**连续**）/ `funnel`（顶层最宽 + 右侧数值列）/
  `matrix`（四象限 + 轴名与轴端标签）/ `cycle`（3~6 步环形闭环 + 切向箭头 + 中心标题）/ `stack`（纵向分层条）。
  全部用 DrawingML 预设几何（`trapezoid`/`rect`/`ellipse`/`triangle`/`parallelogram`）拼出来，
  **不写自定义几何、不依赖外部素材、不联网**。
- **程序化题图**：新增 `hero` 页型；且 `image` 页与封面在图片缺失/未提供时**自动降级为题图**
  ——以前只能画一块纯色，看着就是一页色块。题图用标题作种子（**确定性**，重导出不会变），
  只用实色（无渐变）、透明度用 `a:alpha`、颜色全部取自动调色板。
- **不假称有图**：降级时页面上写明“（图片不存在，已自动生成题图）”，返回 `warnings` 也如实报。
- **让模型主动用**：技能描述里写清了“当用户说要有插图/别只有文字时，就用这些页型”，
  并强调轮换版式。实测（真实单聊）：一句“用示意图表达分层推进/转化漏斗/优先级取舍/迭代闭环，
  开头一页纯视觉题图” → 产出 6 页（hero + 金字塔 + 漏斗 + 四象限 + 闭环 + 小结），
  **产物里图片文件数为 0**，且自检报“无占位符 / 空页 / 越界”。
- **两个实测踩到的坑（都已钉回归）**：
  ① 题图最初用“旋转矩形”做斜带，旋转后的包围盒超出给定矩形 → 形状画到画布外（自检报 overflow），
  现在改用 `parallelogram`（自带斜边、不靠旋转），圆/点阵全部限位；
  ② `cycle` 一度写成 `Math.Max(3, items.Count)`，只给 1~2 项时 `n` 被抬到 3 而 `items` 没那么多 →
  数组越界崩溃（被“每个页型都真的接通了”那个用例抳到，它只给 1 项）。
- 测试：新增 18 个用例（示意图形状特征 / 6 项上限不越界 / 题图确定性 / 缺图降级 / layout 别名），
  全量 **1294 通过**；实盘 35 页 × 3 套调色板/风格组合**形状全部在版面内** + OPC 结构完整。
- **局限（实话）**：这是**图形**而不是**照片**。要有照片级插图，必须接一个文生图服务
  （平台目前没有这个能力，默认 provider 也不提供）。

English:
- **Diagram pages (new)**: `pyramid` (3-6 layers, narrowest on top, **continuous slanted sides**), `funnel` (widest on top plus a value column), `matrix` (quadrants with axis labels), `cycle` (3-6 step ring with tangential arrows and a centre caption) and `stack` (vertical layers). All are built from DrawingML preset geometry (`trapezoid`/`rect`/`ellipse`/`triangle`/`parallelogram`) — **no custom geometry, no external assets, no network**.
- **Procedural hero image**: a new `hero` page type, and `image` pages / covers now **fall back to generated art** when the picture is missing or absent — previously they drew a flat colour block, which reads as exactly that. The art is seeded by the title (**deterministic**, so re-exporting a deck does not change the cover), uses solid colours only (no gradients), expresses transparency via `a:alpha`, and takes every colour from the active palette.
- **It does not pretend there is an image**: the slide says the picture was missing and generated art was used, and the response reports it in `warnings`.
- **The model reaches for them by default**: the tool description now says to use these page types when a user asks for illustrations or “not just text”, and to rotate layouts. Verified end to end in a real single chat: one request for diagrams covering layering, a funnel, prioritisation and a loop produced 6 pages (hero + pyramid + funnel + quadrant + cycle + summary) with **zero image files** in the output, and QA reported no placeholders, empty slides or overflow.
- **Two bugs the new tests caught**: ① the hero art originally used rotated rectangles for diagonal bands, whose rotated bounding box exceeded the given rect and drew shapes off-canvas (QA reported overflow); it now uses `parallelogram`, which is slanted without rotation, and circles/dot grids are clamped. ② `cycle` computed `Math.Max(3, items.Count)`, so 1-2 items lifted the count to 3 while the item list stayed short, crashing on an out-of-range index (caught by the “every page type is actually wired” case, which supplies a single item).
- Tests: 18 new cases (shape signatures per diagram, six-item upper bound staying inside the canvas, deterministic hero art, missing-image fallback, layout aliases); **1294 passing** in total. Live checks render 35 pages across 3 palette/style combos with every shape inside the canvas and a complete OPC structure.
- **Honest limitation**: these are **graphics, not photographs**. Photo-grade illustrations require a text-to-image service, which the platform does not have today.

---

# AG-UI 群聊桌面版 1.0.138 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.138 Release Notes (current Windows desktop release)

**版本说明**：1.0.138 为当前 Windows 桌面版本。PPT 技能全面对齐参考实现（MiniMax pptx-generator）的能力面：**版式变体、图文混排、散点/雷达图、进度与环形仪表页、字体配对、出稿后自检、原地编辑既有稿**。Web 与桌面共用同一套 Hub/网关/前端。
**Version note**: 1.0.138 is the current Windows desktop release. The PPT skill is now feature-aligned with the reference implementation (MiniMax pptx-generator): **layout variants, mixed text+image pages, scatter/radar charts, progress and ring gauges, font pairings, post-generation QA, and in-place editing of existing decks**. Web and desktop share the same Hub / gateway / frontend.

## PPT：版式、图文、图表、QA 与编辑（1.0.138）
# PPT: layouts, media, charts, QA and editing (1.0.138)

中文：
- **版式变体**（同一个页型多种排法，`variant`）：封面 `left/center/image/split`、目录 `list/grid/sidebar`、
  章节页 `number/bar/full`、小结 `list/cta/split`。变体是**枚举**实现的，因此单测逐个变体对比页面 XML——
  未实现的变体会**静默回落**成默认版式（以前 `twoCol` 就这么死过），光看“标题在不在”拦不住。
- **图文混排**：`image` 页新增 `left`(图左文右) / `right`(文左图右) / `bleed`(半出血+叠字) /
  `gallery`(2~4 张图廊)。图片按框**裁切（cover）**而不是拉伸，否则非等比框会变形；
  图片缺失不再静默空白，而是报 `warnings` 并在页上画占位块。
- **图表**：新增 `scatter`（散点，按 x/y 数值轴）与 `radar`（雷达，多维对比）；
  `doughnut/scatter/radar` 请求原生图表时**如实说明降级**（不静默降级）。
- **进度 / 仪表页（新页型 `progress`）**：`bar` 横向进度条 / `ring` 环形仪表。
  进度、完成度、占比这类表达用数字卡片（kpi）说不清“已走到哪”。
- **图标 12 → 45 个**：新增 `money` `target` `rocket` `shield` `layers` `globe` `network` `cloud`
  `database` `mail` `phone` `calendar` `flag` `search` `edit` `file` `pie` `link` `eye` `heart` `key`
  `crown` `map` `cpu` `package` `award` `briefcase` `users` `code` `gauge` `filter` `refresh` `download`。
- **字体**：`fontPair` 命名字体配对（georgia-calibri / cambria-calibri / trebuchet-calibri / …）+ `fontCjk`；
  **拉丁字面与中文字面分开写**（`a:latin` / `a:ea`）——拿 Georgia 去排汉字会整段落到 fallback。
- **去掉“标题下强调线”**：设计规范把它列为 AI 生成稿的典型特征，现在**默认不画**，
  需要旧观感的传 `titleRule: true`。
- **出稿后自检（QA）**：生成与编辑都会自动跑一遍（返回 `qa` 字段；也可 `action:"qa"` 单独跑）——
  查占位符 / 空页 / “只有标题” / 自动填充的空状态文案 / 形状越界。两个防误报细节：
  有图/图表的页不算“只有标题”；外部文件不知道页型时不做这项判定。
- **原地编辑既有稿（`action:"edit"`）**：删页 / 重排 / 复制页 / 替换文字 / 追加新页。
  **绝不改原件**（先复制到 `outputPath`），输出与原件相同时直接报错，不能删光、页号越界报可读错误；
  含图表的页**不支持复制**（需克隆 ChartPart 与内嵌工作簿，容易产出“需要修复”的文件——宁可报错也不破坏）。
- **两个实测踩到的坑**（都已钉回归）：
  ① 环形仪表被画成**实心饼**（用底色填内圆挖空 → 中心变成了不透明底色），改用开放折线描边画弧；
  ② `fontTitle:"宋体"` 被默认的正文字体反手盖掉，导致汉字不走宋体。
- 测试：新增 39 个用例，全量 **1276 通过**；实盘 27 页 × 3 套调色板/风格组合**形状全部在版面内** + OPC 结构完整；
  真实单聊 e2e：一次请求产出 8 页（居中封面 / 卡片目录 / 进度条 / 雷达图 / CTA 收尾），模型回报“自检通过：0 空页、0 占位符、0 越界”。
- 新增本地工具 `tools/run-skill.py`：在本地“编译 + 运行”一份技能（不依赖容器），改渲染代码时迭代快得多。

English:
- **Layout variants** (several排法 per page type, via `variant`): cover `left/center/image/split`, TOC `list/grid/sidebar`, section `number/bar/full`, summary `list/cta/split`. Variants are **enumerated in code**, so tests compare the generated page XML pairwise — an unimplemented variant **silently falls back** to the default layout (as `twoCol` once did), which a "is the title there?" assertion cannot catch.
- **Mixed text + image**: the `image` page gains `left`, `right`, `bleed` (half-bleed image with overlaid text) and `gallery` (2-4 images). Pictures are **cover-cropped** to the frame instead of stretched (a non-proportional frame would distort); a missing file no longer yields a silent blank — it reports a `warning` and draws a placeholder.
- **Charts**: adds `scatter` (numeric x/y axes) and `radar` (multi-dimension comparison); requesting native charts for `doughnut/scatter/radar` **states the fallback** rather than degrading silently.
- **Progress / gauge page (new `progress` type)**: `bar` or `ring`. Progress and completion ratios cannot be expressed by a KPI number card.
- **Icons: 12 → 45** (adds `money`, `rocket`, `shield`, `gauge`, `users`, `code`, …).
- **Fonts**: `fontPair` presets plus `fontCjk`; **Latin and East-Asian typefaces are now written separately** (`a:latin` / `a:ea`) — using Georgia for CJK dropped whole runs to fallback.
- **No more accent line under titles**: the design guide calls it a hallmark of AI-generated slides, so it is **off by default** (`titleRule: true` restores it).
- **Post-generation QA**: generation and editing both run it automatically (`qa` in the response; also available as `action:"qa"`) — placeholders, empty slides, title-only slides, auto-filled empty-state text and shapes outside the canvas. Two false-positive guards: slides containing images/charts don't count as title-only, and external files skip that check (page types unknown).
- **In-place editing (`action:"edit"`)**: delete / reorder / duplicate / replace text / append. It **never touches the original** (copies to `outputPath` first), refuses to write over the source, refuses to delete every slide, and reports out-of-range page numbers readably. Duplicating a slide that contains a chart is **not supported** (it would require cloning the ChartPart and its embedded workbook — an error beats a corrupt file).
- **Two bugs found by testing** (both pinned by regressions): ① the ring gauge rendered as a **solid pie** (carving the centre with the background colour made it opaque), now drawn as a stroked open arc; ② `fontTitle:"宋体"` was overwritten by the default body font, so CJK never used it.
- Tests: 39 new cases, **1276 passing** in total; live checks render 27 pages across 3 palette/style combos with **every shape inside the canvas** and a complete OPC structure; a real single-chat run produced an 8-page deck (centred cover / card TOC / progress bars / radar chart / CTA close) and the model reported "self-check passed: 0 empty, 0 placeholders, 0 overflow".
- New local tool `tools/run-skill.py`: compile and run a skill body locally (no container) for much faster iteration on rendering code.

---

# AG-UI 群聊桌面版 1.0.137 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.137 Release Notes (current Windows desktop release)

**版本说明**：1.0.137 为当前 Windows 桌面版本。修复**编排计划“空答复”缺陷**：计划里每一步都被跳过时（典型场景：单聊里接着说“希望有一些插图”），用户以前只会收到一句写死的“已按计划收集了各岗位的结果，但未汇总出可展示的最终文本”，既与事实不符、也拿不到文件。现在交付判断会参考计划点名的文件技能、并把计划内的产出当素材交给交付岗，直接出成品；万一交付确实没接手，兜底文案也会如实说明哪些步骤被跳过、为什么。Web 与桌面共用同一套 Hub/网关/前端。
**Version note**: 1.0.137 is the current Windows desktop release. It fixes the **"empty coordinated-plan reply" defect**. When every plan step was skipped (typically a single-chat follow-up such as "希望有一些插图"), users used to get only the hard-coded line "…collected each role's results but no final text could be assembled" — untrue, and no file. Delivery detection now consults the document skill the plan named, and the plan's own output is handed to the delivery agent as source material, so a real file is produced. If delivery genuinely does not take over, the fallback text honestly states which steps were skipped and why. Web and desktop share the same Hub / gateway / frontend.

## 编排计划空答复修复（1.0.137）
# Empty coordinated-plan reply fix (1.0.137)

中文：
- **累计缓冲不再被覆盖**：每一步的产出改为**追加**到“前序已产出”，先前是每步 `Clear()` 重写 ——
  最后一步没产出时，前面各岗位的成果会被整体丢掉，而兜底文案却声称“已收集各岗位结果”。
  拼进下游提示词时按 6000 字符截断，避免累计后撑大上下文。
- **交付物类型不再只看用户那一句**：实测单聊里“我希望ppt是绿色的”能出文件（句子里恰好有 `ppt`），
  紧接着的“希望有一些插图”同样意图却什么都没拿到 —— 因为交付判断只读当前这句话。
  现在用户没提格式词时，回退用**计划里点名的文件技能**（`pptx_` / `xlsx_` / `pdf_` / `docx_`）确定交付物。
- **交付岗拿到正文素材**：计划的各步产出随 `upstreamDraft` 一起交给交付兑底（上限 12000 字符，取尾部），
  不再只盯着用户那句原始请求从零重写（否则前面的产出等于白做）。
- **兜底文案改成说实话**：区分“一步都没跑”与“跑了但没有任何产出”，并列出被跳过的步骤与原因、
  给出可行的下一步；不再在什么都没跑时说“已收集各岗位结果”。
- **不再叠两条自相矛盾的说明**：交给交付收尾时，计划侧那段说明暂不发；
  只有交付**确实静默放弃**（没认出交付物 / 找不到能做的岗位 / 空异常）时才补发，避免用户看到空消息。
- **补上诊断日志**（原先该分支完全没有日志，线上无法定位）：
  `计划无任何可展示产出，进入如实兜底：steps=… ran=… needsDelivery=… skips=[…] input=…`。
- 单测：编排/交付相关新增 21 个用例，全量 **1237 通过**。
- 实盘复验（容器 `agui-group-chat-web`）：原先只回一句兜底文案的“希望有一些插图”，
  现在产出 26 页带插图的 PPT；继续“再加一页团队介绍”“改成蓝色科技风”均正常出稿（30 页 / 32 页）。

English:
- **The cumulative buffer no longer overwrites itself**: each step's output is **appended** to "prior output" instead of `Clear()`-ing the buffer every step. Previously, when the last step produced nothing, every earlier role's work was dropped — while the fallback text still claimed results had been collected. The buffer is capped at 6000 chars when fed into downstream prompts.
- **Deliverable type no longer depends only on the user's sentence**: in a single chat, "我希望ppt是绿色的" produced a file (it happens to contain `ppt`), while the follow-up "希望有一些插图" — same intent — produced nothing, because delivery detection only read the current message. When the user names no format, the **document skill the plan named** (`pptx_` / `xlsx_` / `pdf_` / `docx_`) now decides the deliverable.
- **The delivery agent receives source material**: the plan's per-step output is passed along as `upstreamDraft` (capped at 12000 chars, tail-kept) instead of the agent rewriting everything from the user's one-line request.
- **The fallback text tells the truth**: it distinguishes "no step ran at all" from "steps ran but produced nothing", lists the skipped steps with reasons, and offers a concrete next step — no more claiming results were collected when nothing ran.
- **No more two contradictory explanations in one message**: when handing off to delivery, the plan-side note is withheld and only emitted if delivery **genuinely gave up silently** (no deliverable recognised / no capable owner / empty exception), so users never see a blank message.
- **Diagnostics added** (this branch previously logged nothing, making it undiagnosable in production): `计划无任何可展示产出，进入如实兜底：steps=… ran=… needsDelivery=… skips=[…] input=…`.
- Tests: 21 new orchestration/delivery cases; **1237 passing** in total.
- Live re-verification (container `agui-group-chat-web`): the exact message that used to return only the fallback line ("希望有一些插图") now yields a 26-page illustrated PPT; follow-ups ("再加一页团队介绍", "改成蓝色科技风") produce 30- and 32-page decks.

---

# AG-UI 群聊桌面版 1.0.136 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.136 Release Notes (current Windows desktop release)

**版本说明**：1.0.136 为当前 Windows 桌面版本。PPT 新增** 12 个内置图标**：`iconRows` 的 `icon` 可以直接填图标名，画成真正的图标（不再只能是 1~2 个字）。Web 与桌面共用同一套 Hub/网关/前端。
**Version note**: 1.0.136 is the current Windows desktop release. It adds **12 built-in icons** to PPT: `iconRows` can now take an icon name and renders an actual icon instead of being limited to one or two characters. Web and desktop share the same Hub / gateway / frontend.

## 内置图标（1.0.136）
# Built-in icons (1.0.136)

中文：
- 可用：`check` `cross` `arrow` `star` `dot` `warn` `lock` `user` `chart` `clock` `gear` `bulb`。
- 画法：用 ImageSharp 直接画成 PNG（透明底），颜色取主题的 OnAccent，所以**自动跟主题配色走**；
  图标放在彩色圆内并留出内边距，不贴边。
- **为什么自己画而不是用图标字体**：技能是**单个编译单元**，既不能携带字体/素材文件，
  也不能假设宿主装了某个图标字体；而 ImageSharp 已经为图表引入了，画几个几何图形是顺手的事。
- **不认识的名字仍然回退成文字**（原来那个 1~2 字的行为保留），不会变成空白。
- 单测：`IconRows_RendersBuiltInIconsAsImages`（两个图标名→两张合法 PNG、第三个名字以文字回退、图标不出圆）。
  PPT 相关 65 个用例全绿。
- 踩到的小坑：本版本 ImageSharp.Drawing **没有** `DrawLines` / `DrawArc`，折线要拆成多次 `DrawLine`，
  锁梁改用圆环代替（视觉上仍是挂锁）。

English:
- Available: `check`, `cross`, `arrow`, `star`, `dot`, `warn`, `lock`, `user`, `chart`, `clock`, `gear`, `bulb`.
- Rendering: drawn directly as transparent PNGs via ImageSharp, coloured with the theme's OnAccent so they **follow the palette automatically**; each sits inside the coloured circle with padding.
- **Why draw them instead of using an icon font**: a skill is a **single compilation unit** — it cannot ship font/asset files, nor assume the host has a given icon font. ImageSharp is already pulled in for charts, so a few geometric shapes come for free.
- **Unknown names still fall back to text** (the previous one-or-two-character behaviour), never to a blank.
- Covered by `IconRows_RendersBuiltInIconsAsImages` (two icon names yield two valid PNGs, a third name falls back to text, icons stay within the circle); 65 PPT-related cases green.
- Minor snag: this version of ImageSharp.Drawing has **no** `DrawLines` or `DrawArc`, so polylines are split into repeated `DrawLine` calls and the padlock shackle became a ring (still reads as a padlock).

---

# AG-UI 群聊桌面版 1.0.135 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.135 Release Notes (current Windows desktop release)

**版本说明**：1.0.135 为当前 Windows 桌面版本。**设计系统下沉到 Excel 与 PDF**：两个技能原先没有任何主题概念（xlsx 只有一个写死的表头蓝，pdf 只有 8 个语义 role），现在与 PPT 共享同一套 **18 个命名调色板**。Web 与桌面共用同一套 Hub/网关/前端。
**Version note**: 1.0.135 is the current Windows desktop release. The **design system now reaches Excel and PDF**: neither skill had any theme concept (xlsx had a single hard-coded header blue, pdf had eight semantic roles), and both now share the same **18 named palettes** as PPT. Web and desktop share the same Hub / gateway / frontend.

## 18 套命名调色板：xlsx / pdf（1.0.135）
# 18 named palettes for xlsx and pdf (1.0.135)

中文：
- **xlsx**：新增 `theme`，可取 18 个名字之一，也可直接给十六进制主色（`"theme":"#2F6B4F"`）。
  品牌色只控**表头底 / 表头上的字 / 合计行底色与合计线**；**输入蓝与跨表引用绿刻意不变**——
  那是 Excel 多年的约定（表头用「最深色 + 对比度更高的黑白字」保证反白字始终看得清）。
- **pdf**：新增 `palette`，与既有的 `docType`（8 种版式）、`accentRole`（8 种语义角色）共存；
  `palette` 只给品牌三色（主色/底色/强调色），`AccentDark`/`AccentLight`/`Panel`/`Rule`/`Muted`
  继续由既有派生逻辑展开，不另造一套规则。
- **优先级**：`palette` → `accentRole` → `accent` → `colors`，显式单项始终覆盖，
  **不传则产出与改造前完全一致**（有单测钉住 xlsx 的 `FF1F3864` / `FFF2F2F2`）。
- **踩到的真缺陷**：xlsx 默认字色原是 `FF000000`（8 位 ARGB），我把主题色存成 6 位后直接写进去，
  变成 `rgb="000000"` —— SpreadsheetML 的 `rgb` 要求 ARGB，于是**schema 校验失败**（被既有测试当场抓住）。
  现统一补 `FF` 前缀，默认值仍逐字节等同改造前。
- 另一处沿用 pptx 的教训：pdf 的底色过一道 `Surface` 兜底，否则 `education-charts` 的最亮色是亮黄 `E9C46A`，
  会得到一张黄底文档。
- 单测：`PalettePortTests`（8 个用例）+ 全量 **1215 全绿**；回退取证：把 `theme` 参数短路掉，3 个用例立刻失败。

English:
- **xlsx** gained `theme`, accepting any of the 18 names or a bare hex brand colour (`"theme": "#2F6B4F"`). It drives only the **header fill / header text / total-row fill and rule**; the **input-blue and cross-sheet-green stay put**, being long-standing Excel conventions. The header pairs the palette's darkest colour with whichever of black/white contrasts more, so reversed text is always legible.
- **pdf** gained `palette`, coexisting with the existing `docType` (8 layouts) and `accentRole` (8 roles). It supplies only the brand three (primary / background / accent); `AccentDark`, `AccentLight`, `Panel`, `Rule` and `Muted` keep flowing from the existing derivation rather than a second set of rules.
- **Precedence**: `palette` → `accentRole` → `accent` → `colors`, so explicit single values always win, and **omitting it reproduces the previous output byte for byte** (a test pins xlsx's `FF1F3864` / `FFF2F2F2`).
- **A real defect surfaced**: xlsx's default font colour was `FF000000` (8-digit ARGB); storing theme colours as 6 digits and writing them straight through produced `rgb="000000"`, which SpreadsheetML rejects as ARGB — **schema validation failed** and the existing test caught it immediately. Colours are now normalised to ARGB, with defaults still byte-identical to before.
- The same lesson as pptx was applied to pdf: backgrounds pass through a `Surface` guard, otherwise `education-charts`' brightest colour (a bright yellow `E9C46A`) would produce a yellow-paged document.
- Tests: `PalettePortTests` (8 cases) plus a full **1215 green**; revert evidence: short-circuiting the `theme` parameter makes three cases fail at once.

---

# AG-UI 群聊桌面版 1.0.134 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.134 Release Notes (current Windows desktop release)

**版本说明**：1.0.134 为当前 Windows 桌面版本。PPT 长表格改为**自动分页**（不再“只显示前几行”）；Word 修复了**图片宽度不受正文区限制**（显式传 20/24cm 会画到页边距外）。Web 与桌面共用同一套 Hub/网关/前端。
**Version note**: 1.0.134 is the current Windows desktop release. Long PPT tables are now **split across pages** instead of showing only the first few rows, and Word no longer lets an **image exceed the text area** (an explicit 20/24 cm width used to run past the margins). Web and desktop share the same Hub / gateway / frontend.

## PPT：长表格自动分页
# PPT: long tables are split across pages

中文：
- **之前**：一页幻灯片只能放几行，超出的行不画，末行提示「… 另有 N 行未显示」。诚实，但用户拿不到全部数据。
- **现在**：在**渲染之前**先按“字号下限下一页能放几行”切块，每块出一页 table 页，标题带「（n/m）」；
  **每一行都在**，不再有截断提示（若某页仍装不下，截断提示仍作为兼底保留）。
- 为什么在渲染前拆：`RenderSlide` 一次只出一页，页型自己开不了新页；所以 `Build` 先把超长表格展开成多页再逐页渲染。
- **实测**（实盘容器）：30 行 × 5 列 × 60 字的表 → 自动分成 5 页；回归用例额外校验
  **30 行一行不少**、无截断提示、形状仍全在版面内。

English:
- **Before**: only a few rows fitted on one slide, the rest were omitted with an “…and N more rows” line. Honest, but the user never got the full data.
- **Now**: rows are chunked **before rendering** by how many fit at the font floor, and each chunk becomes its own table slide with a “(n/m)” title. **Every row is present** and the truncation notice is gone (it remains only as a safety net for pathological cells).
- Why split before rendering: `RenderSlide` produces exactly one slide, so a page type cannot open another page itself; `Build` therefore expands oversized tables into multiple pages first.
- **Measured** (live container): a 30-row × 5-column × 60-character table becomes 5 pages; the regression additionally checks that **all 30 rows survive**, that no truncation notice appears, and that every shape stays inside the canvas.

## Word：图片不再越出页边距
# Word: images no longer exceed the margins

中文：
- **根因**：图片宽度只被卡在 `≤24cm`，而 A4 正文宽约 **15.9cm** —— 显式传 `widthCm:20/24`
  （或传一张很宽的图配合 `widthPercent`）就会画到页边距外。实测：24cm = 8640000 EMU，正文宽仅 5731510 EMU。
- **修复**：图片等比缩到**正文区内**，上限由**当前页面的实际尺寸与页边距**算出（不写死 15.9cm），
  同时限制高度不超正文高。缩到正文宽即止，不会顺手缩得更小。
- **附带确认**：Word 侧**不需要**“正文缩字号”——Word 是流式排版（段落自然分页、表格行自动长高），
  不存在“撑出页面”。新增回归 `HugeBodyText_IsNotClipped` 用 300 条要点确认一字不少，
  把这个判断钉成可验证的事实（而不是拍脑袋不加）。
- 回退取证：去掉夹取后，该回归报「图片宽 8640000 EMU 超过了正文宽 5731510 EMU」。

English:
- **Root cause**: image width was only capped at `≤ 24 cm` while A4 text width is about **15.9 cm**, so an explicit `widthCm: 20/24` (or a wide image with `widthPercent`) drew past the margins. Measured: 24 cm = 8,640,000 EMU against a text width of 5,731,510 EMU.
- **Fix**: images scale down proportionally to fit the **text area**, with the limit derived from the **actual page size and margins** (no hard-coded 15.9 cm), and height capped to the text height too. Scaling stops as soon as the width fits, so images are never shrunk more than necessary.
- **Also confirmed**: Word does **not** need body-text shrinking — it reflows (paragraphs paginate, table rows grow), so nothing can “spill out of the page”. The new `HugeBodyText_IsNotClipped` regression asserts that all 300 bullet points survive, turning that judgement into a verifiable fact rather than an assumption.
- Revert evidence: removing the clamp makes that regression report “image width 8640000 EMU exceeds text width 5731510 EMU”.

---

# AG-UI 群聊桌面版 1.0.133 发布说明
# AG-UI Group Chat Desktop 1.0.133 Release Notes

**版本说明**：1.0.133 为当前 Windows 桌面版本。修复了 PPT 技能里三个**一直存在、且从不报错**的缺陷：① 「缩字号」实际上是死代码（内容一多就直接溢出正文区）；② **`twoCol` 页型从未生效**（每页都静默变成要点页）；③ 表格行高写死，长文本会撑出页面。Web 与桌面共用同一套 Hub/网关/前端。
**Version note**: 1.0.133 is the current Windows desktop release. It fixes three long-standing defects in the PPT skill that **never reported an error**: (1) the shrink-to-fit logic was effectively dead code (content overflowed the body area instead); (2) the **`twoCol` page type never took effect** (every such slide silently became a bullet page); (3) table row heights were fixed, so long cell text pushed the table off the page. Web and desktop share the same Hub / gateway / frontend.

## 修复一：「缩字号」是死代码（1.0.133）
# Fix 1: shrink-to-fit was dead code (1.0.133)

中文：
- **现象**：PPT 内容一多就溢出正文区/页面——这就是你最早报的「内容太多会越界」。
- **根因**：高度估算函数把 `lineSpacing` 当成**百分数**（`/100.0`），而所有调用方传的都是**倍率**（`1.25`）。
  于是估出来的高度只有真实值的约 1%，`need <= boxH` 永远成立、缩放系数永远是 `1.0`——
  **“缩字号”这一整套逻辑从来没执行过**。它不报错、不影响打开，只是默默什么都没做。
- **修复**：统一为倍率（并兼容 `>5` 视为百分数，防止后来人写 `125`）。
  同时**补全页型覆盖**：`toc` / `twoCol` / `table` / `kpi` 之前根本没有调用过缩字号，现已全部接上。
- **表格额外处理**：行高改为按“最长那一列折几行”计算；缩到字号下限（约 9pt）仍装不下的行
  不画出去，末行换成「… 另有 N 行未显示」——一张幻灯片本来装不下「30 行 × 每格折 4 行」，
  硬画出去只会得到看不见的表；宁可少显示并告诉你少了多少。
- **实测**（实盘、真容器）：30 行 × 5 列 × 60 字的表 → 显示 6 行 + 「… 另有 24 行未显示」；
  20 项目录 / 每栅15 条两栏 / 4 张超长标签指标卡 → **全部形状仍在版面内**。
- 回退取证：把估算公式改回 `/100.0`，新增回归确实失败。

English:
- **Symptom**: PPT content overflowed the body area and the page as soon as there was enough of it.
- **Root cause**: the height estimator treated `lineSpacing` as a **percentage** (`/100.0`) while every caller passes a **multiplier** (`1.25`). The estimated height came out at roughly 1% of reality, so `need <= boxH` always held and the scale was always `1.0` — **the entire shrink-to-fit path had never run**. No error, no corruption, it simply did nothing.
- **Fix**: settle on the multiplier convention (still tolerating `> 5` as a percentage, in case someone writes `125`). Slide-type coverage was also completed: `toc` / `twoCol` / `table` / `kpi` never even called the shrink logic and now do.
- **Table handling**: row height is now derived from how many lines the widest cell wraps to; rows that still do not fit at the font floor (~9pt) are omitted and the last row becomes “…and N more rows”. A slide cannot hold 30 rows of four-line cells; drawing them anyway just produces an unreadable table.
- **Measured** (live container): a 30-row × 5-column × 60-character table shows 6 rows plus “…and 24 more rows”; a 20-item TOC, two 15-bullet columns and four oversized KPI labels all keep **every shape inside the canvas**.
- Revert evidence: restoring the `/100.0` formula does make the new regression fail.

## 修复二：`twoCol` 页型从未生效（1.0.133）
# Fix 2: the `twoCol` page type never took effect (1.0.133)

中文：
- **现象**：文档里写着支持两栏，实际每一页都变成普通的要点页——不报错、不崩，就是静默给你错的东西。
- **根因**：分发前对 `type` 做了 `ToLowerInvariant()`，而 `case` 写成了驼峰 `"twoCol"`，
  于是**永远匹配不上**，落入 `default` 分支（默认要点页）。
- **修复**：改为全小写 `"twocol"`；并加了回归 `EveryDocumentedSlideType_IsActuallyWired`——
  把**每种对外声明的页型**与“不存在的页型”各出一份，逐页比 XML，一样就说明没接上（会直接点名是哪个页型）。
  回退取证：把 `case` 改回驼峰，该回归报「未接上：twoCol」。

English:
- **Symptom**: two-column slides were documented but every one of them came out as an ordinary bullet page — no error, no crash, just silently wrong output.
- **Root cause**: `type` is lower-cased before dispatch, while the `case` label was written as `"twoCol"`, so it never matched and fell through to `default` (the bullet page).
- **Fix**: use the lower-case `"twocol"`, and add the regression `EveryDocumentedSlideType_IsActuallyWired` — it renders **every documented page type** and the same content with a non-existent type, compares the slides pairwise, and names any type whose output is identical (i.e. never wired).
- Revert evidence: restoring the camel-case label makes that regression report “not wired: twoCol”.

---

# AG-UI 群聊桌面版 1.0.132 发布说明
# AG-UI Group Chat Desktop 1.0.132 Release Notes

**版本说明**：1.0.132 为当前 Windows 桌面版本。修复了套模板返回的页数不对（`keepTemplateSlides` 时只报新生成页数，3 页报成 1，看着像丢了页），并校正了 PPT 技能文档里已过时的能力描述。Web 与桌面共用同一套 Hub/网关/前端。
**Version note**: 1.0.132 is the current Windows desktop release. It fixes a wrong slide count returned when authoring from a template (`keepTemplateSlides` reported only the newly generated slides, so a 3-page result read as 1 and looked like the template pages had been dropped), and corrects capability statements in the PPT skill docs that had gone stale. Web and desktop share the same Hub / gateway / frontend.

## 修复：套模板返回的页数（1.0.132）
# Fix: slide count returned by template runs (1.0.132)

中文：
- **现象**：用 `keepTemplateSlides: true` 套模板时，返回的 `slides` 只是**本次新生成的页数**。
  一份 3 页的稿子（2 页模板 + 1 页新增）会报成 `1`，调用方/用户会以为模板原有页丢了。
- **修复**：改为返回**整份稿子的总页数**（`SlideIdLst` 里的实际条目数），并补了 `keepTemplateSlides` 这条路径的回归测试（之前它没有被任何用例覆盖）。
- 实测：`模板 2 页 + 追加 1 页` → 返回 `slides=3`，产物里 3 个 `slideN.xml`，模板页与新页都在。

English:
- **Symptom**: with `keepTemplateSlides: true` the returned `slides` was only the number of **newly generated** slides. A 3-page deck (2 template + 1 new) reported `1`, making it look like the template pages had been dropped.
- **Fix**: report the **total** number of slides in the deck (the actual `SlideIdLst` entries), and add regression coverage for the `keepTemplateSlides` path (it had none).
- **Measured**: a 2-page template plus 1 appended page now returns `slides=3`, with three `slideN.xml` parts in the package and both the template and new slides present.

## 文档校正：PPT 技能的能力描述
# Docs: corrected capability statements for the PPT skill

中文：把 `tools/pptx-skills/README.md` 的「已知边界」改成与实现一致：
- 原文写「**不支持从模板/既有 pptx 编辑**」——已不准确（现在支持 `action:read` 读取文本与 `template` 套模板重出）；
  改为明确“**不支持在 XML 层任意编辑既有页**”。
- 原文写「图表是图片，**不可**在 PowerPoint 内改数据」——补上原生图表选项。
- 顺带补齐之前没写清的边界：无图标素材、无图文混排版式、原生图表只支持 bar/line/pie、
  缩字号实际覆盖哪些页型、`action:read` 会把页码徽标当文本取出。

English: `tools/pptx-skills/README.md`'s "known limits" now match the implementation: "cannot edit from a template or an existing pptx" was no longer accurate (`action: read` and `template` exist) and now says arbitrary XML-level editing of existing slides is not supported; "charts are images, not editable in PowerPoint" now mentions the native chart option; and previously unstated limits were added (no icon assets, no mixed text/image layouts, native charts limited to bar/line/pie, which slide types actually shrink text, and that `action: read` surfaces the page-number badge as text).

---

# AG-UI 群聊桌面版 1.0.131 发布说明
# AG-UI Group Chat Desktop 1.0.131 Release Notes

**版本说明**：1.0.131 为当前 Windows 桌面版本。修复了 1.0.130 新引入的**套模板会丢掉模板底色**（深色模板产出变成白底）。Web 与桌面共用同一套 Hub/网关/前端。
**Version note**: 1.0.131 is the current Windows desktop release. It fixes a defect introduced in 1.0.130 where **authoring from a template lost the template's background colour** (a dark template came out light). Web and desktop share the same Hub / gateway / frontend.

## 修复：套模板丢掉模板底色（1.0.131）
# Fix: template authoring lost the template's background (1.0.131)

中文：
- **现象**：拿一份深色底模板出稿，产物却变成白底——配色看起来“只借了一半”。
- **两层原因，都是“读错了地方”**：
  1. 底色原本只读主题色板的 `lt1`，而 `lt1` 几乎总是 `sysClr(window)`，即**永远是白**。
     真正生效的底色写在 `<p:bg>` 里，得从那里读。
  2. 修正后仍不对：**母版也有一份 `<p:bg>`（白底）**，而代码写成
     `母版 ?? 幻灯片 ?? lt1`，于是在母版的白色上就短路了，根本轮不到幻灯片那一层。
     按 OOXML 的优先级应该是 **幻灯片 → 母版 → `lt1`**（页背景覆盖母版背景）。
- **修复**：按上述优先级取色，并在母版 / 首张幻灯片两处都读 `<p:bg>`。
- **实测**：`tech-night`（底 `000814`）作模板 → 产物 `bg=000814`、`primary=FFD60A`，与模板一致；
  之前是 `bg=FFFFFF`、`primary=806B05`（被守卫调暗过的橄榄绿）。
- 全量单测 **1201 全绿**；新增回归 `TemplateMode_KeepsDarkTemplateBackground`（外加母版白底时也会失败）。

English:
- **Symptom**: authoring from a dark template produced a light deck — the styling looked only half-adopted.
- **Two layers of the same mistake, both “reading the wrong place”**:
  1. The background was taken from the theme's `lt1`, which is almost always `sysClr(window)` — i.e. **always white**. The background that actually applies lives in `<p:bg>`.
  2. After that fix it was still wrong: the **master also carries a `<p:bg>` (white)**, and the code read `master ?? slide ?? lt1`, so it short-circuited on the master's white and never consulted the slide. OOXML precedence is **slide → master → `lt1`** (a slide's background overrides the master's).
- **Fix**: honour that precedence, reading `<p:bg>` from both the master and the first slide.
- **Measured**: a `tech-night` template (background `000814`) now yields `bg=000814`, `primary=FFD60A`, matching the template; previously `bg=FFFFFF`, `primary=806B05` (an olive that the readability guard had darkened).
- Full suite **1201 green**; new regression `TemplateMode_KeepsDarkTemplateBackground` (it also fails if the master's white background is preferred again).

---

# AG-UI 群聊桌面版 1.0.130 发布说明
# AG-UI Group Chat Desktop 1.0.130 Release Notes

**版本说明**：1.0.130 为当前 Windows 桌面版本。PPT 产出能力大改造：**18 套设计级调色板 + 4 种版式风格 + 4 种新页型**（设计系统移植），新增**原生可编辑图表**（可在 PowerPoint 里改数据）、**读取既有 pptx** 与**套用户模板出稿**。Web 与桌面共用同一套 Hub/网关/前端。
**Version note**: 1.0.130 is the current Windows desktop release. A major upgrade of the deck authoring skill: **18 design-grade palettes, 4 layout styles and 4 new slide types** (ported design system), plus **native editable charts** (editable in PowerPoint), **reading existing decks** and **authoring from a user-supplied template**. Web and desktop share the same Hub / gateway / frontend.

## 设计系统：18 套调色板 + 4 种版式风格 + 4 种新页型
# Design system: 18 palettes, 4 layout styles, 4 new slide types

中文：
- **18 套命名调色板**（医疗/年报/学术/母婴/科技发布/教育统计/ESG/时尚/美食/奢侈品/云AI/旅行/快消/金融科技…）。每套只给 5 个色值，**角色（主色/底色/强调/浅底）由亮度与彩度推导**，18 套共用一条规则——而不是逐套手调。
- **两个推导规则是实盘翻车倒逼出来的**：① 底色必须先保证是“面”——`education-charts` 的最亮色是亮黄 `E9C46A`，直接当底色就是一张黄底幻灯片，现在会把彩度压下来（`F6E7C3` 奶油底）；② 强调色必须与主色**色相拉开**——`forest-eco` 原本选中 `3A5A40`，与主色 `344E41` 色相差仅 4°，强调线等于白画。
- **WCAG 可读性兜底**：正文 ≥4.5:1、主/副/强调 ≥3.0:1，并保证强调色上至少黑或白有一个能读清（它是页码徽标/序号圆的底色）。
- **`style`（与主题正交）**：`sharp` / `soft` / `rounded` / `pill`，只动页边距、间距、圆角，可与任意主题自由组合（如 `tech-night` + `pill`）。
- **4 种新页型**：`stats`（大数字看板）、`grid`（2/3 列网格卡）、`timeline`（序号圆 + 连接线）、`iconRows`（彩色圆图标行）；`content` 页还可用 `"layout":"grid"` 这类别名。
- **验证**：把上面两条推导规则分别回退后，18 套里 **6 套失败**（包括我原本没发现的 `luxury-mysterious`/`coastal-coral`/`art-food`/`vibrant-tech`）。

English:
- **18 named palettes** (healthcare, annual reports, academic, mother-and-baby, tech launches, education/statistics, ESG, fashion, food, luxury, cloud/AI, travel, FMCG, fintech, ...). Each supplies only **five colours**; the roles (primary / background / accent / light surface) are **derived from luminance and chroma**, so a single rule has to hold for all 18 instead of being hand-tuned per palette.
- **Two of those rules exist because the live run failed**: (1) the background must read as a *surface* — `education-charts`' brightest colour is a bright yellow `E9C46A`, which as a slide background is simply a yellow page; chroma is now pushed down (`F6E7C3`). (2) The accent must be **hue-separated** from the primary — `forest-eco` had picked `3A5A40`, only 4° away from the primary `344E41`, making every accent rule invisible.
- **WCAG guards**: body text ≥ 4.5:1, primary/secondary/accent ≥ 3.0:1, and the accent is adjusted so that at least one of black/white is readable on it (it backs the page-number badge and timeline nodes).
- **`style`, orthogonal to the theme**: `sharp` / `soft` / `rounded` / `pill`, moving only margins, spacing and corner radii; freely combinable with any theme (e.g. `tech-night` + `pill`).
- **Four new slide types**: `stats` (large callouts), `grid` (2–3 column cards), `timeline` (numbered nodes on a connector) and `iconRows`; `content` also accepts a `"layout"` alias.
- **Verification**: reverting either derivation rule makes **6 of the 18 palettes fail** — including four I had not spotted while writing them.

## 原生可编辑图表（可选）
# Native editable charts (opt-in)

中文：
- 默认仍渲成 PNG（兼容性最好），但需要“拿回去继续改数据”时，把 `chartType` 写成 `bar-native` / `line-native` / `pie-native`（或 `chartData:"native"`）即可生成**真正的 DrawingML 图表**，并**嵌入一份数据工作簿**（否则“编辑数据”拿不到表格）。
- `doughnut` 暂不支持原生，会**降级为图片**，并在返回 JSON 的 `nativeChartFallback` 里如实说明（不静默降级）；`nativeCharts` 报出实际生成了几张。
- **实测踩到一个真缺陷**：图表调色板常量带 `#`（ImageSharp 接受），但 `srgbClr/@val` 是 `xsd:hexBinary`，带 `#` 就不合法——OpenXmlValidator 直接报错。现已在输出层统一去 `#`。
- 单测覆盖：部件存在、关系存在、嵌入工作簿可打开、ChartSpace 缓存值与输入一致、整包过 schema 校验。**仍建议发布前用 PowerPoint 真开一次**——这是本项目唯一无法靠自动化完全覆盖的风险点。

English:
- Charts stay rasterised by default (best compatibility). When the user needs to keep editing the data, `chartType: "bar-native" / "line-native" / "pie-native"` (or `chartData: "native"`) produces a **real DrawingML chart** with an **embedded data workbook** (without it, “Edit Data” has no table).
- `doughnut` is not supported natively and **falls back to an image**, reported honestly in `nativeChartFallback` (never silent); `nativeCharts` reports how many were produced.
- **A real defect surfaced here**: the chart palette constants carry a leading `#` (ImageSharp accepts it), but `srgbClr/@val` is `xsd:hexBinary` and rejects it — OpenXmlValidator flagged it immediately. Normalised at the output layer.
- Covered by tests: parts exist, relationships exist, the embedded workbook opens, the ChartSpace cache matches the input, and the whole package passes schema validation. **Still open a file in PowerPoint once before shipping** — it is the one risk this project cannot fully automate.

## 读取既有 pptx 与套用户模板出稿
# Reading existing decks and authoring from a template

中文：
- **读取**：`{ "action": "read", "path": "att_xxx" }` → 按放映顺序返回每页文本（含备注）。只取文本，不还原版式与图片。
- **套模板**：`template` 传入既有 .pptx → **先复制再改副本，绝不写原件**（`template` 与 `outputPath` 相同时直接报错）；保留模板的母版/版式，并从其主题读出配色与字体；默认清空模板原有页（`keepTemplateSlides:true` 则追加）。
- **清空要连部件一起删**：只删 `SlideId` 的话 `ppt/slides/slideN.xml` 还会留在包里（占体积、文本仍能被搜到），实测表现为“看着像清空失败”。
- **用户上传的文件怎么传给技能**：模型只看得到附件 ID（`att_xxx`）、看不到服务器路径，而技能吃的是路径。平台现在在调用 .NET 技能前把入参里的 `att_xxx` 换成真实路径（`SkillRunner.ResolveAttachments`）；解析不到的 ID 原样保留（技能会报「找不到文件」），解析器抛异常也不会把调用搞挂。
- **实盘已验**：真的 `POST /ag-ui/upload` 拿到 `att_63007e4ffb1348f5`，再用它做 `read`（读回 2 页）与 `template`（出 3 页、母版/主题各 1 套、模板配色 `2B2D42` 生效、模板旧页已清）。

English:
- **Read**: `{ "action": "read", "path": "att_xxx" }` returns each slide's text (plus notes) in show order. Text only — layouts and images are not reconstructed.
- **Template**: pass an existing `.pptx` as `template` → the file is **copied first and only the copy is modified**; using the same path for `template` and `outputPath` is rejected outright. The template's master/layouts are kept and its theme colours and fonts are read; template slides are cleared by default (`keepTemplateSlides: true` appends instead).
- **Clearing must delete the parts too**: removing only the `SlideId` leaves `ppt/slides/slideN.xml` in the package (dead weight, and the text is still findable), which in practice looks like “the clear didn't work”.
- **How an uploaded file reaches a skill**: the model only sees the attachment id (`att_xxx`), never a server path, while skills consume paths. The platform now rewrites `att_xxx` in the arguments before invoking a .NET skill (`SkillRunner.ResolveAttachments`). Unresolvable ids pass through unchanged (the skill reports “file not found”), and a throwing resolver cannot break the call.
- **Verified live**: a real `POST /ag-ui/upload` returned `att_63007e4ffb1348f5`, which was then used for `read` (2 slides back) and `template` (3 slides out, exactly one master and one theme, the template's `2B2D42` palette in effect, and the template's old slide cleared).

---

# AG-UI 群聊桌面版 1.0.129 发布说明
# AG-UI Group Chat Desktop 1.0.129 Release Notes

**版本说明**：1.0.129 为当前 Windows 桌面版本。修复了**内置产出技能（Word / PPT）的图表**两个缺陷：① **内容一多就画出画布**（长标题、多系列、多分类、大数值）；② **饼图其实一直没画出来**（扇区被填成了几乎没面积的“弓形”）。Web 与桌面共用同一套 Hub/网关/前端。
**Version note**: 1.0.129 is the current Windows desktop release. It fixes two defects in the **chart renderers** of the built-in authoring skills (Word / PPT): (1) **content spilling outside the canvas** with long titles, many series, many categories or large values; and (2) **pie charts that were effectively never drawn** (slices were filled as near-zero-area circular *segments*). Web and desktop share the same Hub / gateway / frontend.

## 修复一：图表内容过多会越界（1.0.129）
# Fix 1: charts spilling outside the canvas (1.0.129)

中文：
- **现象**：图表标题很长、系列/分类很多、数值位数很大时，文字与图例会被画出画布边界（ImageSharp 会静默裁到边框，于是贴着边就是最后的痕迹）。
- **根因**：渲染器全程使用**固定坐标**——标题直接画在 `x=8`、Y 轴刻度写死左边距、图例一行横向累加 x 坐标、分类标签按字符数而不按实际槽宽判断是否挤压。内容一多，锚点就跑到画布外。
- **修复（两侧一致）**：
  - 标题按可用宽度**先缩字号再裁剪**；
  - Y 轴刻度文案**先量宽**，据此推左边距（钳在 72–200px）并**右对齐**到轴线左侧；
  - 图例按宽度**换行**（最多 3 行），每项按宽裁剪，放不下的补“…等 N 项”，且**把图例占用的行数提前计入顶部留白**；
  - 分类标签按**槽宽**裁剪，过密时**隔位显示**（`stride = ceil(52 / slot)`），绘制起点用 `ClampX` 夹在绘图区内；
  - 饼图图例同样按宽裁剪、按高限行数并补“…等 N 项”。
  - 另外 PPT 的**页面正文**（`content` 页型）新增“内容太多整体缩字号”：先整理要点 → 估算高度定缩放（下限 0.6）→ 再生 XML。
- **实测（逐像素求墨迹包围盒，画布 1400×800 / 900×480）**：

  | 图 | 修复前 | 修复后（PPT） | 修复后（Word） |
  |---|---|---|---|
  | 柱状 | 右侧贴边 `R0` | `L20 T25 R35 B51` | `L18 T23 R27 B42` |
  | 折线 | 右侧贴边 `R0` | `L20 T25 R35 B51` | `L18 T23 R24 B42` |
  | 饼图 | 墨迹铺到 `(0,0)-(1399,799)` | `L40 T23 R40 B26` | `L24 T19 R23 B29` |

English:
- **Symptom**: with a very long title, many series/categories, or many-digit values, text and legends were drawn past the canvas edge (ImageSharp silently clips at the border, so “hugging the edge” is the tell-tale sign).
- **Root cause**: the renderer used **fixed coordinates throughout** — the title was drawn at a hard-coded `x=8`, the Y-axis left padding was constant, the legend accumulated x across a single row, and category labels decided “too crowded?” by character count rather than actual slot width.
- **Fix (identical on both sides)**: titles shrink-then-clip to the available width; Y-axis tick labels are measured first to derive the left padding (clamped to 72–200px) and are **right-aligned** against the axis; legends **wrap** by width (max 3 rows), clip per item, and fall back to “…and N more”, with their row count **folded into the top padding**; category labels are clipped to the **slot** width, **thinned** when crowded (`stride = ceil(52 / slot)`) and clamped into the plot area; pie legends clip by width and cap rows the same way. PowerPoint `content` slides also gained *shrink-to-fit body text* (estimate height → pick a scale, floor 0.6 → emit XML).
- **Measured** (per-pixel ink bounding box, canvases 1400×800 / 900×480): as in the table above; before the fix the bar and line charts hugged the right edge (`R0`) and the pie's ink covered `(0,0)-(1399,799)`.

## 修复二：饼图一直没被真正画出来（1.0.129）
# Fix 2: pie charts were never actually drawn (1.0.129)

中文：
- **现象**：饼图区域**整片留白**，只在圆盘外缘留一圈极细的弧线（远看就是“这张图没画”）。
- **根因**：`PathBuilder.AddArc` **只是往当前图形里追加一段弧**——它既不会先移到圆心，也不会自动补上两条半径。代码只做 `AddArc` + `Fill`，于是路径被当成「弧 + 弦」围成的**弓形**来填充，面积远小于扇形：40 项饼图只剩 **0.8%** 的彩色像素。
  - PPT 侧还有一种更糟的形态：误用了 `AddArc` 的 7 参重载（它的前两个参数同样是**圆心**，不是包围盒左上角），算出**半径翻倍的巨大圆**，墨迹直接铺满整张画布（连 `(0,0)` 都被填上）。
- **修复**：不再依赖 `AddArc` 的重载语义，改为显式围出扇形——`MoveTo(圆心)` → 沿弧 `LineTo` 走一圈 → `CloseFigure()`，用每 2° 一段的多边形逼近（弦高远小于 1px，肉眼不可见）。Word / PPT 两侧统一。
- **实测（彩色像素占比，真圆盘应占画布约 27%）**：PPT 饼图从 **0.8% → 28.8%**；Word 饼图从 **0.8% → 26.8%**。
- **回归测试**：新增 `ChartOverflowTests`，把 200 字标题 + 30 个 20 字分类 + 4 个长名系列 + 40 项饼图图例跑过**真实** `DotnetSkillHost`，从产物里抠出图表 PNG，用自带的纯 C# PNG 解码器**逐像素求墨迹包围盒**断言四周留空边；并另加两条**饼图实心度**断言（彩色像素占比 ≥ 20%）。
  仅断言“没越界”是不够的——扇区画坏时整张图仍全在画布内，越界断言一律通过。
  已实测：把两侧的修复分别回退后，对应测试**确实失败**（PPT 饼图墨迹回到 `(0,0)-(1399,799)`、柱/折线回到 `R0`；饼图实心度回到 0.8% / 17.3% / 13.7%）。
- **全量单测**：**1157 通过 / 1 失败**。唯一失败的是既有的 `TwinTriggerTests`（用 `Task.Delay(200)` 等异步触发派发，全量并行跑时会偶发超时；单独跑通过），**与本次改动无关**，未在本次修复。

English:
- **Symptom**: the pie area was **blank**, with only a hairline arc along the rim — effectively “the chart wasn't drawn”.
- **Root cause**: `PathBuilder.AddArc` only *appends an arc to the current figure* — it neither moves to the centre nor adds the two radii. The code did `AddArc` + `Fill`, so the path was filled as the circular **segment** cut off by the arc and its chord, whose area is far smaller than the wedge: a 40-item pie kept only **0.8%** coloured pixels. On the PowerPoint side there was an even worse variant: the 7-argument `AddArc` overload was used (its first two arguments are also the **centre**, not the bounding-box corner), producing a circle of **double radius** that flooded the entire canvas (even `(0,0)` was painted).
- **Fix**: stop relying on `AddArc` overload semantics and build the wedge explicitly — `MoveTo(centre)` → walk the arc with `LineTo` → `CloseFigure()`, using a polygon approximation at 2° per step (sagitta far below 1px). Applied identically to the Word and PPT renderers.
- **Measured** (share of coloured pixels; a real disk covers ≈27% of the canvas): the PPT pie went **0.8% → 28.8%** and the Word pie **0.8% → 26.8%**.
- **Regression tests**: the new `ChartOverflowTests` runs a stress payload (200-character title, 30 categories of 20 characters, 4 long-named series, a 40-item pie legend) through the **real** `DotnetSkillHost`, extracts the chart PNGs from the produced package and asserts, with a hand-written pure-C# PNG decoder, that the **per-pixel ink bounding box** still leaves margin on all four sides. Two further assertions cover **pie solidity** (coloured share ≥ 20%): checking “nothing is out of bounds” alone is not enough, because a broken slice still sits entirely inside the canvas. Verified by reverting each side's fix in turn — the corresponding tests **do** fail (PPT pie ink back to `(0,0)-(1399,799)`, bar/line back to `R0`; solidity back to 0.8% / 17.3% / 13.7%).
- **Full suite**: **1157 passed / 1 failed**. The single failure is the pre-existing `TwinTriggerTests` (it waits on async trigger dispatch via `Task.Delay(200)` and occasionally times out under full parallel load; it passes in isolation). It is **unrelated to this change** and was left untouched.

---

# AG-UI 群聊桌面版 1.0.128 发布说明
# AG-UI Group Chat Desktop 1.0.128 Release Notes

**版本说明**：1.0.128 为当前 Windows 桌面版本。修复了**图表里的标点符号方向错误**（破折号 `—` 变成竖线、`（）「」【】《》` 被旋转 90°）。Web 与桌面共用同一套 Hub/网关/前端。
**Version note**: 1.0.128 is the current Windows desktop release. It fixes **punctuation rendered with the wrong orientation in charts** (an em dash `—` came out as a vertical bar, `（）「」【】《》` were rotated 90°). Web and desktop share the same Hub / gateway / frontend.

## 修复：图表标点被竖排渲染（1.0.128 核心）
# Fix: chart punctuation rendered vertically (1.0.128 headline)

中文：
- **现象**：图表里的横线类与括号类标点方向错误——破折号 `—` `–` 被画成**竖线**，`（）「」『』【】《》`
  被**旋转 90°**；而汉字与 `、。：；！？` 一切正常，所以看起来像“部分符号方向错了”。
- **根因不在字体，也不在我们的排版代码**：图表代码里**没有任何旋转**（所有文字都水平绘制）。
  把容器里的字体文件取出来用 **FreeType/PIL** 渲染，同一份字体是**横排正确**的；
  而容器里**7 种中文字体全部**在 ImageSharp 下出错（Noto Sans/Serif CJK、WQY Micro Hei/Zen Hei、
  AR PL UMing/UKai、Droid Sans Fallback）—— 所以是库的问题：
  **`SixLabors.Fonts` 1.0.0 的 shaping 错误地对 CJK 字体套用了竖排（`vert`）字形替换。**
  而它与平台选的字体无关、与备注/页型无关：只要图表文字里出现这批标点就会出现。
- **为何一直命中**：技能只声明了 `SixLabors.ImageSharp 2.1.5`，它对 `SixLabors.Fonts` 的依赖是
  `>= 1.0.0`，而平台的 NuGet 解析器取**最低满足版** —— 于是每次都落到有缺陷的 1.0.0。
- **修复**：在技能里**显式钉住 `#r "nuget: SixLabors.Fonts, 1.0.1"`**（Word 侧写在生成器的共享 banner，
  会带进三份场景技能），并加了单测 `ChartSkill_PinsSixLaborsFontsAtLeast101` 拦住被当作“多余引用”而清理掉。
  1.0.1 仍为 **Apache-2.0**，不受 Six Labors 从 2.0 起改用 Split License 的影响（这也是没升到 ImageSharp 3.x 的原因）。
- **验证（逐层取数）**：
  - **独立渲染器对照**：同一份 `wqy-microhei.ttc`，PIL/FreeType 得到 `—` 46×4（横）、`（` 18×43（高瘦）
    → 证明字体本身正确；
  - **库版本受控对比**：同一份字体、同一段诊断代码，`SixLabors.Fonts` **1.0.0 得到 `—` 4×49（竖）**，
    **1.0.1 得到 44×4（横）**；`（` 从 42×12（扁宽）变为 17×43（高瘦）；`「` `【` `《` 同样恢复正常；
  - **端到端（真实 pptx 技能产物）**：把图表分类标签设为单个 `—`，量其墨迹包围盒为 **11×1（宽高比 11.0，横向 ✓）**；
    标签设为 `（）` 得到 **9×14（高>宽，方向正确 ✓）**；另设 `XX` 作对照；
  - 全量单测 **1154 全绿**；把修复行删掉后新单测确实失败（已实测）。

English:
- **Symptom**: dashes and brackets in charts came out with the wrong orientation — an em dash `—` `–` was drawn as a **vertical bar**, and `（）「」『』【】《》` were **rotated 90°** — while Chinese characters and `、。：；！？` looked fine, so it read as “some symbols have the wrong direction”.
- **The font is not at fault, and neither is our layout code**: there is **no rotation anywhere** in the chart renderer (every string is drawn horizontally). Rendering the container's own font files through **FreeType/PIL** gives the correct horizontal shapes, while **all seven** CJK fonts present (Noto Sans/Serif CJK, WQY Micro Hei/Zen Hei, AR PL UMing/UKai, Droid Sans Fallback) misbehave under ImageSharp — so the problem is in the library: **`SixLabors.Fonts` 1.0.0 wrongly applies the vertical (`vert`) glyph substitutions to CJK fonts.** It has nothing to do with which font is picked, nor with notes or slide type: any chart text containing this punctuation is affected.
- **Why it always bit us**: the skill declared only `SixLabors.ImageSharp 2.1.5`, whose dependency on `SixLabors.Fonts` is `>= 1.0.0`, and the platform's NuGet resolver takes the **lowest satisfying version** — so it landed on the defective 1.0.0 every time.
- **Fix**: explicitly pin `#r "nuget: SixLabors.Fonts, 1.0.1"` in the skills (on the Word side it lives in the generator's shared banner, which flows into all three scene skills), with a unit test, `ChartSkill_PinsSixLaborsFontsAtLeast101`, to stop it being cleaned up as a “redundant reference”. 1.0.1 is still **Apache-2.0**, unaffected by Six Labors moving to the Split License at 2.0 — which is also why this does not upgrade to ImageSharp 3.x.
- **Verification (measured layer by layer)**:
  - **independent renderer as control**: the same `wqy-microhei.ttc` through PIL/FreeType yields `—` 46×4 (horizontal) and `（` 18×43 (tall) — the font itself is correct;
  - **controlled library-version comparison**: same font, same diagnostic code — `SixLabors.Fonts` **1.0.0 gives `—` 4×49 (vertical)** while **1.0.1 gives 44×4 (horizontal)**; `（` goes from 42×12 (flat) to 17×43 (tall); `「` `【` `《` likewise return to normal;
  - **end to end, through the real pptx skill**: with a single `—` as the only chart category label, the label ink measures **11×1 (aspect 11.0, horizontal ✓)**; with `（）` it measures **9×14 (taller than wide, correct ✓)**; `XX` was measured as a control;
  - the full suite is **1154 green**, and deleting the fix line does make the new test fail (measured).

---

# AG-UI 群聊桌面版 1.0.127 发布说明
# AG-UI Group Chat Desktop 1.0.127 Release Notes

**版本说明**：1.0.127 为 Windows 桌面版本。修复了 **Word / PPT 图表里的中文显示为乱码**（实际是空心方框）的问题。Web 与桌面共用同一套 Hub/网关/前端。
**Version note**: 1.0.127 is a Windows desktop release. It fixes **Chinese text in Word / PPT charts rendering as garbled characters** (in fact hollow notdef boxes). Web and desktop share the same Hub / gateway / frontend.

## 修复：图表中文变乱码（1.0.127 核心）
# Fix: Chinese in charts rendered as garbage (1.0.127 headline)

中文：
- **根因：图表字体只按“族名命中”就选定，结果选到了纯拉丁字体**。图表是 ImageSharp 渲成的 PNG。
  字体候选名单原为 `Microsoft YaHei / SimHei / SimSun / Arial / DejaVu Sans`——在 Linux 容器里前四个
  都不存在，第一个命中的是 **`DejaVu Sans`（纯拉丁字体）**。字形缺失时 ImageSharp 会把每个汉字画成
  **空心方框（notdef）**，于是图表的中文标题 / 分类标签 / 系列名全变方框，而英文坐标数字正常——
  看上去很像“字体渲染错乱”，实际是选错了字体。
- **修复：命中候选后必须验字形覆盖**。现在会用一个常用汉字取样串逐个调 `Font.TryGetGlyphs`，
  只采用**真的含中文字形**的字体；候选名单也补上了 Linux / macOS 的常见中文字体族名
  （`Noto Sans CJK SC`、`Source Han Sans SC`、`WenQuanYi Micro Hei`、`Droid Sans Fallback`、`PingFang SC` 等）；
  第二步还会遍历系统全部字体族找中文字形（应对未知族名）。容器里现在正确选到 `Noto Sans CJK SC`。
  真的一个中文字体都没有时，不再静默出方框，而是把提示写进返回的 `message`。
- **可排障**：PPT / Word 技能的返回 JSON 新增 `chartFont` 与 `chartFontCjk`，一眼能看出图表用的是哪款字体、是否含中文字形。
- **影响范围**：内置 PPT（`pptx_deck`）与三个 Word 技能（`docx_gongwen` / `docx_notice` / `docx_report`）共用同一套图表渲染，三者的图表中文都受影响，已一并修复（Word 侧改在生成器共享内核 `tools/docx-skills/generate.mjs`，再重新生成三份技能）。
- **验证（改前 / 改后逐一取数）**：
  - 容器内实际选中字体：修复前 `DejaVu Sans` → 修复后 **`Noto Sans CJK SC`**（`chartFontCjk: true`）；
  - 把修复前实际产出的 pptx 取回，抽出图表 PNG 逐像素分析：标签带**每行恒为 24 个深色像素**——
    这是 **16 个空心方框**（4 个标签 × 4 个字，只有边框有墨）的特征；修复后同一区域逐行着墨为
    `111/85/116/52/70/48/56/67/38/68/66`，是真实汉字的笔画分布；
  - 把标签带降采样成文本图目视对比：修复前是 4 个空心方框轮廓，修复后是 4 簇互不相同的汉字笔画；
  - Word 产物同样确认：图表标签区为 3 簇真实汉字笔画（非方框）。
- **防回归**：新增单测 `ChartFont_MustHaveChineseGlyphs`——用 `SixLabors.Fonts` **独立**校验“所选字体真的含中文字形”（不只信技能自报）；环境里确实没有中文字体时跳过而非误报失败。

English:
- **Root cause: the chart font was chosen on family-name match alone, so a Latin-only font won.** Charts are PNGs rendered with ImageSharp. The candidate list was `Microsoft YaHei / SimHei / SimSun / Arial / DejaVu Sans` — none of the first four exists in the Linux container, so the first hit was **`DejaVu Sans`, a Latin-only font**. With no glyph for a character ImageSharp draws a **hollow notdef box**, so every Chinese chart title, category label and series name became a box while the Latin axis numbers looked fine — which reads like broken rendering but is really the wrong font being picked.
- **Fix: verify glyph coverage after a name match.** The picker now walks a sample string of common Chinese characters through `Font.TryGetGlyphs` and only accepts a face that **actually has them**; the candidate list gained the usual Linux / macOS CJK family names (`Noto Sans CJK SC`, `Source Han Sans SC`, `WenQuanYi Micro Hei`, `Droid Sans Fallback`, `PingFang SC`, …); a second pass scans every installed family for CJK coverage, covering unknown names. The container now picks `Noto Sans CJK SC`. When no CJK font exists at all it no longer silently emits boxes: the hint goes into the returned `message`.
- **Diagnosable**: the PPT and Word skills now return `chartFont` and `chartFontCjk`, so it is immediately visible which face the charts used and whether it carries Chinese glyphs.
- **Scope**: the built-in PPT skill (`pptx_deck`) and the three Word skills (`docx_gongwen` / `docx_notice` / `docx_report`) share one chart renderer, so all four were affected and all four are fixed (on the Word side in the generator's shared kernel, `tools/docx-skills/generate.mjs`, then regenerated).
- **Verification (measured before and after)**:
  - face actually selected in the container: `DejaVu Sans` before → **`Noto Sans CJK SC`** after (`chartFontCjk: true`);
  - the deck produced by the *old* build was fetched and its chart PNG analysed pixel by pixel: the label band held **a constant 24 dark pixels per row**, the signature of **16 hollow boxes** (4 labels × 4 characters, only the outlines inked); after the fix the same band reads `111/85/116/52/70/48/56/67/38/68/66` — the stroke distribution of real glyphs;
  - downsampling the label band into a text picture shows four hollow box outlines before and four distinct clusters of Chinese strokes after;
  - the Word output was confirmed the same way: three clusters of real strokes, not boxes.
- **Regression guard**: a new unit test, `ChartFont_MustHaveChineseGlyphs`, uses `SixLabors.Fonts` to **independently** check that the selected face really carries Chinese glyphs (rather than trusting the skill's own report); it skips instead of failing on an environment that genuinely has no CJK font.

---

# AG-UI 群聊桌面版 1.0.126 发布说明
# AG-UI Group Chat Desktop 1.0.126 Release Notes

**版本说明**：1.0.126 为 Windows 桌面版本。修复了一个严重缺陷：**内置 PPT 技能生成的 .pptx 会被 PowerPoint 判为“需要修复”才能打开**。Web 与桌面共用同一套 Hub/网关/前端。
**Version note**: 1.0.126 is a Windows desktop release. It fixes a serious defect: **the built-in PPT skill produced .pptx files that PowerPoint demanded to repair before opening**. Web and desktop share the same Hub / gateway / frontend.

## 修复：PPT 产物需要“修复”才能打开（1.0.126 核心）
# Fix: PPT output needed repairing before it opened (1.0.126 headline)

中文：
- **根因：OOXML 包缺了必备部件与关系**。生成的 .pptx 里：
  1. **幻灯片母版没有关联主题部件**（`p:sldMaster` 必须拥有主题—— 颜色/字体由它提供）；
  2. 只要有一页带演讲者备注，包里就会出现 `notesSlide`，**却没有对应的 `notesMaster`**，`presentation.xml` 也缺 `notesMasterIdLst`；
  3. 版式（`slideLayout`）没有回指其母版的关系。
  这三处都会让 PowerPoint 弹“需要修复”。修复方式：生成时补上主题部件（含完整的 `clrScheme` / `fontScheme` / `fmtScheme`，且 fmtScheme 四个子列表各至少 3 项）、按需创建备注母版并由 presentation 与每个备注页分别关联、版式回指母版；另补齐备注页回指幻灯片的反关系。
- **为什么以前没被发现（教训）**：`OpenXmlValidator` 只校验**单个部件内部的 schema**，**完全不校验跨部件的必备关系**。上述三处缺漏它全部放行（错误 0），所以单测一直是“绿的”。
  同时该缺陷**与备注无关的那部分一直都在**——也就是说**之前生成的所有 .pptx 都需要修复**，与是否使用备注、图表无关。
- **新增两层防回归**：
  - 单测新增 `Package_HasAllRequiredCrossPartRelationships`，直接用 OpenXML SDK 断言包的跨部件关系（母版有主题、版式回指母版、有备注页就有备注母版、`presentation.xml` 含 `notesMasterIdLst`、备注页回指幻灯片）；
  - 新增独立工具 `tools/verify_office_package.py`，不依赖 .NET，按 OOXML 规则逐个检查 pptx / docx / xlsx 的部件与关系完整性（并报了未声明 content type、悬空关系）。
- **验证**：上述两处修复**先临时关掉**确认新测试与结构检查都会失败（已实测），恢复后再跑——全量 **1153 个用例全绿**；产物另经**独立复验**：OpenXML schema 校验 0 错误、`tools/verify_office_package.py` 结构完整、`python-pptx`（另一套实现）能打开并正确读出 8 页与全部中文备注、各部件内 `cNvPr id` 无重复、无悬空关系。

English:
- **Root cause: the OOXML package was missing required parts and relationships.** In the generated .pptx:
  1. the **slide master had no theme part** (a `p:sldMaster` must own a theme — colours and fonts come from it);
  2. as soon as any slide carried speaker notes the package contained `notesSlide` parts **with no `notesMaster`**, and `presentation.xml` lacked `notesMasterIdLst`;
  3. the slide layout **did not point back at its master**.
  All three make PowerPoint offer to repair the file. The fix adds the theme part (complete `clrScheme` / `fontScheme` / `fmtScheme`, with at least three entries in each of the four format lists), creates the notes master on demand and relates it from both the presentation and every notes slide, points the layout back at its master, and adds the inverse notes-slide-to-slide relationship.
- **Why this went unnoticed (the lesson)**: `OpenXmlValidator` only validates **the schema inside a single part** — it does **not** check the required **cross-part** relationships. It passed all three omissions with zero errors, so the unit tests stayed green. Note also that the **non-notes part of the defect was always present**: every .pptx generated before this fix needed repairing, whether or not notes or charts were used.
- **Two new layers against regression**:
  - a unit test, `Package_HasAllRequiredCrossPartRelationships`, asserting the cross-part relations through the OpenXML SDK (master has a theme, layout points back at the master, notes slides imply a notes master, `presentation.xml` carries `notesMasterIdLst`, notes slides point back at their slide);
  - a standalone tool, `tools/verify_office_package.py`, which needs no .NET and checks the parts and relationships of pptx / docx / xlsx against the OOXML rules (it also reports undeclared content types and dangling relationship targets).
- **Verification**: both fixes were **temporarily disabled first** to confirm the new test and the structure check do fail (measured), then restored — the full suite is **1153 green**; the output was additionally **independently verified**: zero OpenXML schema errors, structurally complete per `tools/verify_office_package.py`, openable by `python-pptx` (a different implementation) with all 8 slides and every Chinese note read back correctly, no duplicate `cNvPr` ids within any part, and no dangling relationships.

---

# AG-UI 群聊桌面版 1.0.125 发布说明
# AG-UI Group Chat Desktop 1.0.125 Release Notes

**版本说明**：1.0.125 为 Windows 桌面版本。在 1.0.124 基础上新增**内置 Excel 生成技能**（`xlsx_book`）与**内置 PDF 生成技能**（`pdf_doc`），并把两者接入编排交付闭环；同时修正了 PDFsharp 字体解析器（进程级全局）被抢先占用导致内置 PDF 技能失效、以及测试间泄露该全局状态的问题。Web 与桌面共用同一套 Hub/网关/前端。
**Version note**: 1.0.125 is a Windows desktop release. On top of 1.0.124 it adds a **built-in Excel generation skill** (`xlsx_book`) and a **built-in PDF generation skill** (`pdf_doc`), wires both into the orchestration delivery loop, and fixes both the process-wide PDFsharp font-resolver conflict that could disable the built-in PDF skill and the test that leaked that global state. Web and desktop share the same Hub / gateway / frontend.

## 内置 Excel + PDF 生成，交付技能矩阵齐备（1.0.125 核心）
# Built-in Excel + PDF generation, completing the producer matrix (1.0.125 headline)

中文：
- **内置 `xlsx_book` Excel 技能（开箱即用）**：照 `pptx_deck` 的同一套工程约定做成嵌入资源，`DocumentFormat.OpenXml` 的 SpreadsheetML。支持多工作表、列定义（文字 / 数值 / 货币 / 百分比 / 日期）与数字格式、**公式优先**（算出来的列写真正的工作表公式，合计行自动生成 SUM 等，跨表引用用 `表名!`）、冻结窗格与自动筛选、合计行上框线（会计式），并遵循财务配色惯例（硬编码输入＝蓝、同表公式＝黑、跨表公式＝绿）；另提供 `analyze` 模式读取已有 .xlsx 并回报各表结构摘要。产物带 `produce_file` 标记 → 直接可下载，实测对话产出 `.xlsx`（2 个工作表 / 14 个公式 / 4.3 KB）。
- **内置 `pdf_doc` PDF 技能（开箱即用）**：借鉴 MiniMax-AI/skills 的 minimax-pdf 思路，由**设计令牌**驱动——多种文档类型（report / proposal / resume / academic / minimal / editorial / magazine / terminal）各带封面版式与语义强调色，内容块体系（标题 / 正文 / 富文本标记 / 列表 / callout / 引用 / 表格 / 图片 / 矢量图表 / 代码 / 分隔线 / 图注 / 目录 / 分页 / 留白 / 指标卡），既可直接给结构化 `blocks`，也可把整篇 **Markdown 重排**成 PDF（REFORMAT 路由）。A4 / Letter、页码页脚、目录页码回溯填充。
- **中文字体：自动探测 + 子集嵌入（关键工程点）**：PDF 必须把字体嵌进文件，而 PDFsharp 只对 **glyf（TrueType 轮廓）** 字体做子集化，对 **CFF/OTTO** 字体会把整份字体塞进去。实测：同一页中文文档，glyf 字体产出 **24–48 KB**，而容器已有的 `NotoSansCJK`（CFF）产出 **13.7 MB**。因此镜像新增 `fonts-droid-fallback`（Apache-2.0，glyf）供 PDF 技能使用，技能侧也会读字体头自行判别 glyf/CFF、并对 CFF 给出体积警示；可用 `AGUI_PDF_FONT` 或参数 `fontPath` 指定自己的字体（支持 .ttf/.ttc，TTC 会自动抽 face）。实测对话产出 4 页中文 PDF：**95 KB**，字体为子集 `/BIIYBT+Droid Sans Fallback`，中文可提取可搜索。
- **编排交付闭环覆盖四类格式**：要 Excel / 表格 / 报表 → 点名 `xlsx_book`；要 PDF → 点名 `pdf_doc`（与原有 PPT/Word 一致），并加入 `org_design` 技能正文的复用规则与“空洞交付技能”告警文案。
- **交付“厚度”按类型分别度量（修正）**：原先把各技能的字段混着取最大值，而各技能报的字段语义不同（pptx 报 `slides`、xlsx 报 `rows`、docx/pdf 报 `blocks`）——xlsx 的结果会因为没有 `blocks` 而被判成“0 内容”触发无谓重试。现按交付类型选度量：pptx 看页数（≥3）、xlsx 看数据行数（≥3）、docx/pdf 看内容块数（≥5）。
- **Excel / PDF 入参形状校验**：与 docx 的 `sections`、pptx 的 `slides` 同理，新增 xlsx 的 `sheets`（必须同时有列定义与数据行）与 pdf 的 `blocks`（每块要有 `type`，或直接给 `markdown`）校验，拦住“只有标题的空表 / 只有封面的 PDF”，并回送正确形状让模型自纠。
- **PDFsharp 字体解析器冲突（进程级全局，已修）**：PDFsharp 只允许安装一次字体解析器，且一旦有人渲染过 PDF 文字，该插槽就<b>永久冻结</b>。技能都跑在 Web 同一进程里，因此一个先渲染 PDF 文字的自建 dotnet 技能会连带内置 PDF 技能一起失效。现技能会先尽力安装自带解析器，失败时尝试复用已安装的那个（可解析中文字体时仍可出稿），否则给出<b>可执行的中文错误</b>（而不是一句莫名的异常）。
- **测试间泄露该全局状态（已修）**：附件存储用例原先用 PDFsharp 现场渲染一个 PDF 夹具，从而在测试进程里冻结了解析器插槽，导致全量跑时 PDF 技能用例连锁失败（实测 16 个）。现改为手写一份最小 PDF 夹具（base-14 Helvetica，不需字体解析器）——它只关心“能从 PDF 提取文本”，本不该占用这个全局；全量 1129 个用例恢复全绿。

English:
- **Built-in `xlsx_book` Excel skill (works out of the box)**: shipped as an embedded resource using the same engineering conventions as `pptx_deck`, implemented on `DocumentFormat.OpenXml` SpreadsheetML. It supports multiple worksheets, column definitions (text / number / currency / percent / date) and number formats, **formulas first** (computed columns carry real worksheet formulas, totals rows generate SUM and friends, cross-sheet references use `Sheet!`), frozen panes and autofilter, an accounting rule above totals, and the usual financial color convention (hard-coded input = blue, same-sheet formula = black, cross-sheet formula = green); an `analyze` mode reads an existing .xlsx and reports a structural summary per sheet. The output carries the `produce_file` marker → downloadable, and was verified live (2 worksheets / 14 formulas / 4.3 KB).
- **Built-in `pdf_doc` PDF skill (works out of the box)**: following the design-system idea of MiniMax-AI/skills' minimax-pdf, it is driven by **design tokens** — several document types (report / proposal / resume / academic / minimal / editorial / magazine / terminal) each with their own cover layout and semantic accent color, plus a content-block system (headings / paragraphs with inline markup / lists / callout / quote / table / image / vector charts / code / divider / caption / toc / page break / spacer / KPI). Feed it structured `blocks`, or hand it a whole **Markdown** document to re-typeset into PDF (the REFORMAT route). A4 / Letter, page numbers and footers, and a table of contents whose page numbers are resolved on a second pass.
- **Chinese fonts: auto-detection plus subset embedding (the key engineering point)**: a PDF must embed its fonts, and PDFsharp only subsets **glyf (TrueType outline)** fonts — for **CFF/OTTO** fonts it embeds the entire file. Measured on the same one-page Chinese document: a glyf font yields **24–48 KB** while the container's pre-existing `NotoSansCJK` (CFF) yields **13.7 MB**. The image therefore adds `fonts-droid-fallback` (Apache-2.0, glyf) for the PDF skill; the skill also inspects the font header itself to tell glyf from CFF and warns about the size cost of CFF. A custom font can be supplied via `AGUI_PDF_FONT` or the `fontPath` parameter (.ttf/.ttc supported, TTC faces are extracted automatically). Verified live: a 4-page Chinese PDF at **95 KB**, with a subset font `/BIIYBT+Droid Sans Fallback` and extractable, searchable Chinese text.
- **The orchestration delivery loop now covers all four formats**: Excel / spreadsheet / report → names `xlsx_book`; PDF → names `pdf_doc` (alongside the existing PPT / Word cases), with matching updates to the built-in `org_design` skill's reuse rules and to the hollow-delivery-skill warning.
- **Delivery “thickness” is measured per format (fix)**: previously the fields reported by different skills were pooled into a single maximum, but their meanings differ (pptx reports `slides`, xlsx reports `rows`, docx/pdf report `blocks`) — so an xlsx result, having no `blocks`, looked like “0 content” and triggered a pointless retry. The metric is now chosen by deliverable type: slides for pptx (≥3), data rows for xlsx (≥3), content blocks for docx / pdf (≥5).
- **Input-shape validation for Excel and PDF**: mirroring the docx `sections` and pptx `slides` checks, the gateway now validates xlsx `sheets` (each must carry both a column definition and data rows) and pdf `blocks` (each block needs a `type`, or supply `markdown` directly), which blocks an “empty table with only a header” or a “cover-only PDF” and feeds the correct shape back so the model can self-correct.
- **PDFsharp font-resolver conflict (process-wide global, fixed)**: PDFsharp allows its font resolver to be set only once, and once anything has rendered PDF text the slot is **permanently frozen**. Skills all run in the same web process, so a single self-authored dotnet skill that renders PDF text first would disable the built-in PDF skill too. The skill now tries to install its own resolver, and on failure attempts to reuse the installed one (which still works if that one can resolve a Chinese font); otherwise it returns an **actionable Chinese error** instead of an opaque exception.
- **A test leaked that global state (fixed)**: the attachment-storage test used to render a PDF fixture with PDFsharp, freezing the resolver slot inside the test process and making the PDF skill tests fail in bulk when the whole suite ran (16 of them, measured). It now writes a minimal PDF fixture by hand (base-14 Helvetica, no font resolver needed) — that test only cares about extracting text from a PDF and never needed this global; all 1129 tests are green again.

---

# AG-UI 群聊桌面版 1.0.124 发布说明
# AG-UI Group Chat Desktop 1.0.124 Release Notes

**版本说明**：1.0.124 为 Windows 桌面版本。在 1.0.123 基础上新增**内置 PowerPoint 生成技能**，并让**一键编排 / 组织架构构建师自动适配**它；同时修正了交付物判定的优先级错误。Web 与桌面共用同一套 Hub/网关/前端。
**Version note**: 1.0.124 is a Windows desktop release. On top of 1.0.123 it adds a **built-in PowerPoint generation skill** and makes **one-click orchestration / the org architect adapt to it automatically**, plus a fix to the deliverable-detection precedence. Web and desktop share the same Hub / gateway / frontend.

## 内置 PPT 生成 + 编排适配（1.0.124 核心）
# Built-in PPT generation + orchestration adaptation (1.0.124 headline)

中文：
- **内置 `pptx_deck` 演示文稿技能（开箱即用）**：与内置 Word 技能同一条 Roslyn 链路，**纯 .NET（DocumentFormat.OpenXml）**，不依赖 Node / Python / 外部解释器。按页型组织——封面 / 目录 / 章节分隔 / 内容 / 两栏 / 表格 / 指标卡 / 引言 / 图片 / 图表 / 小结 / 结束页，任意页可带演讲者备注；六套主题预设或自定义配色与字体、16:9 宽屏、除封面外每页页码徽标。图表（柱 / 折线 / 饼 / 环形）**渲成 PNG 再按图片嵌入**而非发 ChartPart（沿用内置 Word 技能的决策，避开 DrawingML 图表兼容性风险）。产物带 `produce_file` 标记 → 网关自动登记为附件，**前端可直接下载**。可由 `Agents:BuiltinPptxSkills` 关闭播种。
- **编排自动适配内置产出技能**：编排提示词与「可复用技能」小节现在都点名——要 PPT / 演示文稿就**直接引用 `pptx_deck`**，要 Word 就引 `docx_report`/`docx_gongwen`/`docx_notice`，并明令**不要**为这些已有能力另造只能写字的 prompt 空壳；内置 `org_design` 技能的复用规则同步更新。预览侧 `DetectDeliveryGap` 在点名该格式时也会**直接给出内置技能名**，模型与用户都有可引用的对象。
- **交付物判定改为「明确格式词优先、中文泛称靠后」（重要修正）**：此前先判中文泛称「表格」，于是「做份 PPT，含一张对比表格」被判成 **Excel 交付** —— 找不到 xlsx 技能后兜底**静默放弃**，用户拿不到文件。现明确格式词（pptx / xlsx / docx / pdf）优先，中文泛称（「文档」→ docx、「表格」→ xlsx）作兜底，且兜底内部仍保持「文档」优先，含糊表述（如「写份文档，里面含一张表格」）的判定与历史一致。
- **治理旧代码**：新造交付技能与内置技能同 id 时视为重复（应改为直接引用），该规则原本只对 docx 表述，现同时覆盖 pptx。

English:
- **Built-in `pptx_deck` presentation skill (works out of the box)**: same Roslyn path and **pure .NET (DocumentFormat.OpenXml)** as the built-in Word skills — no Node, Python or external interpreter. Organised by slide type: cover, toc, section, content, two-column, table, KPI, quote, image, chart, summary and end, each optionally carrying speaker notes; six theme presets or explicit colors and fonts, 16:9 widescreen, a page badge on every page but the cover. Charts (bar / line / pie / doughnut) are **rendered to PNG and embedded as pictures** rather than emitted as ChartParts — the same call the Word skills make to avoid DrawingML chart compatibility risk. The output carries the `produce_file` marker, so the gateway registers it as an attachment that **downloads straight from the frontend**. Can be turned off with `Agents:BuiltinPptxSkills`.
- **Orchestration adapts to the built-in producers**: both the orchestration prompt and its reusable-skills section now name them — for a deck, **reference `pptx_deck` directly**; for Word, `docx_report` / `docx_gongwen` / `docx_notice` — and explicitly forbid inventing text-only prompt shells for capabilities that already exist. The built-in `org_design` skill's reuse rules were updated to match, and the preview's `DetectDeliveryGap` **names the built-in skill** so both the model and the user have something concrete to reference.
- **Deliverable detection now prefers explicit format words over generic Chinese nouns (important fix)**: it tested the loose noun “表格” first, so “make a PPT with a comparison table” was classified as an **Excel** deliverable — the fallback then found no spreadsheet skill and **gave up silently**, leaving the user with no file. Explicit format words (pptx / xlsx / docx / pdf) now win, with generic nouns (“文档” → docx, “表格” → xlsx) as the fallback; inside that fallback “文档” still comes first so ambiguous phrasing such as “write a document containing a table” resolves exactly as before.
- **Old rule brought in line**: a newly invented delivery skill sharing an id with a built-in one is treated as a duplicate that should have been a direct reference; that rule previously only said docx, and now covers pptx too.

---

# AG-UI 群聊桌面版 1.0.123 发布说明
# AG-UI Group Chat Desktop 1.0.123 Release Notes

**版本说明**：1.0.123 为 Windows 桌面版本。在 1.0.122 基础上：**用户分组（按组细粒度授权）正式进入桌面安装包**，并修复了一轮严格代码审核发现的问题（弹窗层级导致操作卡死、用户分组创建时间持久化丢失、技能目标准入判定顺序、单聊复用路径绕过准入、白名单 id 无校验）。Web 与桌面共用同一套 Hub/网关/前端。
**Version note**: 1.0.123 is a Windows desktop release. On top of 1.0.122 it brings **user groups (group-based fine-grained authorization) into the desktop installer** and fixes a round of issues found in a strict code review (a stacking bug that hung dialogs, loss of a group's creation timestamp across restarts, the skill-target access check ordering, the direct-chat reuse path bypassing admission, and unvalidated allowlist ids). Web and desktop share the same Hub / gateway / frontend.

## 代码审核修复（1.0.123 核心）
# Code-review fixes (1.0.123 headline)

中文：
- **弹窗不再被遮住而卡死（回归修复）**：通用确认框 `uiConfirm` / 输入框 `uiPrompt` 没有自己的层级，从「知识库管理」里删除文档时确认框会被画到该弹窗**底下** —— 用户看不到、`await` 也永不返回，界面直接卡住。现为确认框单独指定层级（高于二级弹窗、低于 toast）。
- **用户分组的创建时间不再丢失**：该字段被标了 `[JsonIgnore]`，永远不会落盘，重启后一律归零；三种持久化模式（内存 / PostgreSQL / Redis）均受影响。已修正，并把原先“看似在测、实则测不到”的持久化用例改为走真实生产路径且断言该字段。
- **技能目标准入判定顺序**：访问策略把管理员放行排在“技能目标永不直接可见”之前，对技能子代理会返回“可访问”。虽然现有调用点都另外挡了一层、尚无可实际触发的越权，但这是安全原语里的 fail-open 隐患，已调整顺序并加回归测试。
- **单聊复用路径补上准入**：此前只在“创建”会话时校验用户组白名单，复用已存在会话时直接返回 —— 用户被移出白名单组后仍能继续单聊。现将准入校验前置，创建与复用两条路径同权。**行为变化**：被移出白名单组的用户，其已有单聊会立即失效。
- **白名单 id 增加校验**：数字员工 / 技能的“允许访问的用户组”此前接受任意字符串，写错一个 id（或漏 `ug_` 前缀）会让该资源**静默对所有人不可见**且保存不报错；现在保存即拦下。
- **目录可见性与单聊准入统一为同一份策略**：数字员工目录曾自行重写一遍可见性规则（与准入策略是两份实现，已经开始漂移）；现已统一调用同一策略，并加组合测试把两者钉死。
- **删除分组前给出影响提示**：新增影响预检（该分组被哪些数字员工 / 技能引用），删除确认会列出具体资源与成员数，避免运维误删后有人突然失去访问。

English:
- **Dialogs no longer render behind the caller and hang**: the generic confirm/prompt dialog had no z-index of its own, so confirming a document deletion inside the knowledge-base manager drew it underneath that dialog — invisible, and its promise never resolved, freezing the UI. It now has an explicit layer above second-level dialogs and below the toast.
- **A user group's creation timestamp survives restarts**: the field was marked `[JsonIgnore]`, so it was never persisted and always restored as zero, in memory, PostgreSQL and Redis alike. Fixed, and the persistence test that only appeared to cover it now drives the real production path and asserts the field.
- **Skill-target access check ordering**: the policy tested the admin bypass before the “skill targets are never directly visible” rule, answering true for a skill sub-agent. Every current caller also guards separately, so nothing was exploitable, but it was a fail-open ordering hazard in a security primitive; reordered with a regression test.
- **Direct chat validates on the reuse path too**: admission was only checked when creating the conversation, so a user dropped from an allowlist group kept it by reopening the existing one. The check now precedes both paths. **Behaviour change**: an existing direct chat stops working as soon as its user is removed from the group.
- **Allowlist ids are validated**: the “allowed user groups” list on employees and skills accepted any string, so a single typo could silently hide a resource from everyone while saving without error. Invalid ids are now rejected on save.
- **Catalog visibility and direct-chat admission share one policy**: the employee catalog re-implemented the visibility rule instead of using the shared policy (two implementations that had already drifted). It now delegates to the same policy, pinned by a combinatorial test.
- **Group deletion reports its impact**: a new impact check lists which employees and skills reference a group, so the delete confirmation shows exactly what and how many users would lose access before it happens.

---

# AG-UI 群聊桌面版 1.0.122 发布说明
# AG-UI Group Chat Desktop 1.0.122 Release Notes

**版本说明**：1.0.122 为当前 Windows 桌面点版本（已构建 Windows 1.0.122 MSI），在 1.0.121 基础之上修正了编排/长稿稳定性的三个问题，全部 Web 已推送（Web 与桌面共用同一套 Hub/网关/前端）。
**Version note**: 1.0.122 is the current Windows desktop point release (a Windows 1.0.122 MSI was built). On top of 1.0.121 it fixes three issues affecting orchestration & long-form stability, all already on the Web build (Web and desktop share the same Hub / gateway / frontend).

## 编排长稿稳定性修复（1.0.122 核心）
# Orchestration & long-form stability fixes (1.0.122 headline)

中文：
- **模型网络超时放大（不再“跑到一半被掐”）**：底层模型客户端默认仅在 100 秒内响应，思考模型（deepseek-reasoner）长思考/长稿生成时请求未完成就被中断，表现为“编排/指派路由进行中取消、出不来稿”。已把单次请求网络超时提到 15 分钟（真正的一次运行时限仍由执行配置的流式超时 `streamTimeoutMinutes` 控制）。
- **多附件问答可用（不再“只认第一个”）**：一条消息可带多个附件，模型上下文现在会给「附件清单」（每个带 attachmentId + 注入状态）；可提取文本附件按“每文件 12K + 总 60K”逐文件注入（此前共享 24K 且先到先得，首个大文件会挤掉后续）；`read_attachment` 支持分段续读（startIndex/maxChars），长文档可被完整引用；历史追问回放预算同步放大到 24K。
- **编排回复绝不空白（不再“只有计划卡、没有字”）**：综合答复拿不到可用文本时，回退到已收集的中间结果整理成一段可见回答，或给明确提示；递归补查每步都保证返回非空；取消/异常中断的收尾也会在上一条“正文为空”的消息上补一句说明。任何情况下都不再留下纯空白回复。

English:
- **Model network timeout enlarged**: the underlying model client only waited 100s, so a reasoning model (deepseek-reasoner) still thinking when long-form output is requested got cut off before the request finished — showing up as “orchestration / assignment routing gets cancelled, no draft”. The per-request network timeout is now 15 minutes (the true per-run limit is still governed by the execution stream timeout `streamTimeoutMinutes`).
- **Multi-attachment Q&A works**: a message may carry multiple files and the model context now gets an “attachment inventory” (each with its own attachmentId + inlined status). Extractable text files are inlined per file (12K each within a 60K total, replacing a shared 24K served first-come-first-served that let the first big file crowd out the rest); `read_attachment` supports paginated reads (`startIndex`/`maxChars`) so long documents can be fully referenced; the historical follow-up inline budget is raised to 24K.
- **Orchestration replies never arrive blank**: when the synthesized answer yields no usable text it now falls back to a readable summary of the collected intermediate results or a clear notice; the recursive gathering returns a non-empty value at every boundary; and cancelled / error teardowns stamp a brief note rather than leaving an empty-content bubble.

---

## 用户分组与按组授权（细粒度访问控制）（已包含于 1.0.123 桌面安装包）
# User groups & group-based authorization (fine-grained access) (included in the 1.0.123 desktop installer)

中文：
- **用户分组（用户组 / 组织单元）**：管理员控制台新增「用户分组」页签（`GET/POST/DELETE /ag-ui/usergroups`，另有 `/mine` 供登录用户查自己所属组）。把**平台账号**编组（与“群聊知聚”是不同概念），用于按组授权。持久化于扩展区 `userGroups`，重启不丢。
- **数字员工按组授权**：`AgentDefinition.AllowedGroupIds`——在数字员工编辑表单新增「🔐 允许访问的用户组」多选。未配置 = 全员可用（向后兼容）；配置后仅命中白名单的用户组成员可**看到 / 单聊 / 拉入/建群**（创建者与系统管理员始终放行）。服务端在目录列表、单聊、建群、加成员四个关口统一拦截（403 `AGENT_PERMISSION_DENIED`）。
- **技能库按组授权**：`AgentSkillDefinition.AllowedUserGroupIds`——受限技能对非归属者/非管理员隐藏，挂载到数字员工时非授权用户拒绝（403 `SKILL_PERMISSION_DENIED`）。知识库已有“群级共享”语义，与本机制互补。
- 详见 `docs/RBAC.md` §6。

English:
- **User groups (organizational units)**: a new “User Groups” tab in the admin console (`GET/POST/DELETE /ag-ui/usergroups`, plus `/mine` for a signed-in user's own groups) to organize **platform accounts** (a different concept from chat groups) for group-based authorization. Persisted in the `userGroups` section across restarts.
- **Group-based agent authorization**: `AgentDefinition.AllowedGroupIds` — the agent edit form gains an “🔐 Allowed user groups” multi-select. Empty = open to everyone (backward compatible); when set, only members of the listed groups may **see / direct-chat / add to a circle** (creator and system admins always pass). Enforced server-side at the catalog list, direct chat, group create and member add (403 `AGENT_PERMISSION_DENIED`).
- **Group-based skill authorization**: `AgentSkillDefinition.AllowedUserGroupIds` — restricted skills are hidden from non-owners/admins and cannot be mounted onto an agent by unauthorized users (403 `SKILL_PERMISSION_DENIED`). Knowledge bases already model group-based sharing, complementing this mechanism.
- Details in `docs/RBAC.md` §6.

---

# AG-UI 群聊桌面版 1.0.121 发布说明
# AG-UI Group Chat Desktop 1.0.121 Release Notes

**版本说明**：1.0.120 之后的上一桌面包为 1.0.121（已构建 Windows 1.0.121 MSI，含记忆拟人类型 / 组织构建自动配记忆人格 / 组织连接自动成对 / 记忆管理按角色分层 / 企业合规 / 单聊等等）。1.0.120 为再往上一版（单聊 kind=direct、实时会话吊销、SDK 上行串行化、200MB 上传/导入）。以下各节为此前的功能增量。
**Version note**: The previous desktop package after 1.0.120 was 1.0.121 (a Windows 1.0.121 MSI was built, adding memory personality types / org auto-assigned memory personas / auto-paired assignment+escalation links / role-tiered memory scope / enterprise compliance / direct chats etc.); before that 1.0.120 (direct chats `kind=direct`, instant session teardown, SDK send serialization, 200MB upload/import). The sections below are the earlier feature increments.

## 组织构建连接自动成对（指派 + 提升）（已随 1.0.121 桌面安装包发布）
# Org building auto-pairs assignment + escalation links (shipped in the 1.0.121 desktop installer)

中文：
- **组织构建连接自动成对（指派 + 提升）**：当编排方案 / 待落库最终稿只填了向上「问题提升」连接（`escalationAgentId`）、向下任务指派名册（`assignmentIds`）为空或不全时，系统会在**生成解析**（`AgentOrchestrator.Parse` → `InferAssignments`）与**共享落库引擎**（`OrgApplyEngine.EnsureAssignmentsForLeaders`）两处，自动把直接提升到该主管的下属并入其 `assignmentIds`——去重、保序、只增不改；生成器提示与内置 `org_design` 技能正文也已明确要求成对连接。净效果：无论经网页一键编排、内置组织架构构建师（`org_plan_draft`）还是 `org_commit` 建出的团队，都同时具备「主管向下指派 + 下属向上提升」双向连接，不会退化成单向提升链。**库中已有旧组织不回写**——重新生成 / 重新 apply 即生效（解析推断 / 并入既有名册 / 落库兜底的单元与集成测试已存在）。

English:
- **Org building auto-pairs assignment + escalation links**: when an orchestration plan / committed final draft only sets upward problem-escalation links (`escalationAgentId`) and leaves the downward task-assignment rosters (`assignmentIds`) empty or partial, the system auto-merges each leader’s direct subordinates (the agents that escalate to it) into that leader’s `assignmentIds` — deduplicated, order-preserving, additive-only — at two stages: generation parse (`AgentOrchestrator.Parse` → `InferAssignments`) and the shared persist engine (`OrgApplyEngine.EnsureAssignmentsForLeaders`). The generator prompt and the built-in `org_design` skill body now explicitly instruct paired connections. Net effect: any team created via one-click orchestration, the built-in org architect (`org_plan_draft`) or `org_commit` lands with both directions (managers assign downward AND subordinates escalate upward) and never degrades into a one-way escalation chain. Legacy orgs already in the catalog are untouched — regenerate / re-apply to benefit. Related unit/integration tests exist (parse inference, merge into existing rosters, apply-stage backstop).

---

## 组织构建自动按岗位适配「记忆拟人类型」（已随 1.0.121 桌面安装包发布）
# Org building auto-assigns per-role “memory personality” (shipped in the 1.0.121 desktop installer)

中文：
- **组织构建自动适配记忆拟人类型**：网页「一键组织编排」与内置组织架构构建师（`org_plan_draft`）产稿时，会按每个岗位的实际职责让模型挑选记忆拟人 preset（`memoryProfile`：broad/deep/slowToLearn/cueDependent/fastForgetting），随方案 JSON 一起预览，并在 apply / `org_commit` 落库后自动写入新创建的数字员工——统筹主管通常是 `deep` 深记型、高频客服/一线是 `broad` 广记型、需回想客户过往的顾问/售后是 `cueDependent` 存得住想不起型、值班/速查岗是 `fastForgetting`、重复套路岗是 `slowToLearn`。容错解析：模型写 preset key 字符串或 `{"memoryType":…}` 对象均可；未知/缺失不报错，缺失按岗位称呼/职责关键词启发式兜底，仍无把握则保持 `null`=沿用全局召回。**向后兼容**：历史方案/手写最终稿 JSON 没有该字段 → 解析为 null、原样落库。预览逐岗位回显「记忆: 🧠…」标签便于落库前核对。详见 `docs/memory-personality.md` §4.4。

English:
- **Org building now auto-assigns memory personalities**: one-click orchestration and the built-in org architect (`org_plan_draft`) ask the model to pick a memory-persona preset per role (`memoryProfile`: broad/deep/slowToLearn/cueDependent/fastForgetting) that is shown in the preview and written into each newly created employee on apply / `org_commit` — e.g. leads/deep, high-volume frontline/broad, account-success & after-sales/cueDependent, duty & quick-lookup/fastForgetting, repetitive routine/slowToLearn. Parsing is tolerant (preset-key string or `{"memoryType":…}` object); unknown/missing values never abort the build — missing falls back to role-keyword heuristics, otherwise stays `null` (platform-global recall, backward compatible; legacy plans / hand-written JSON without the field still parse and apply exactly as before). Preview rows show a “记忆:” tag per employee for verification before committing. See `docs/memory-personality.md` §4.4.

---

## 数字员工「记忆拟人类型」（已随 1.0.121 桌面安装包发布）
# Digital-employee memory personality types (shipped in the 1.0.121 desktop installer)

中文：
- **记忆拟人类型（按类型召回）**：编辑数字员工 →「记忆与权限」新增「🧠 记忆类型（拟人召回）」区——五档预设（广记型 / 深记型 / 难录入型 / 存得住想不起型 / 快速遗忘型）+ 口吻模式（平实引述 / 先概括要点）+ 人设口吻 + 高级微调（群/个人 TopK 与相似度阈值）。回复前按该员工类型执行抽取：调取群记忆 / 个人记忆的条数与阈值不同（经检索层真正生效），存得住想不起型在用户给回忆提示（「记得吗 / 上次 / 之前」）时临时放宽提取，快速遗忘型默认只见最近 21 天的记忆（不删落库数据）；注入记忆时附一句与类型相符的“召回口吻”软性说明（广记型提示用「我记得好像是…」式谨慎措辞，不凭空补全）。写入侧仅对本员工本人发言微调：<b>深记型</b>自动刻为「重要」记忆、<b>快速遗忘型</b>在开启自动遗忘时保留更短（其余写入无差别）。未配置 = 沿用平台全局，完全向后兼容。记忆特性专项说明：`docs/memory-personality.md`。

English:
- **Memory personality types (type-driven recall)**: editing an employee → “Memory & Permissions” now has a “🧠 Memory type” section — five presets (Broad / Deep / Slow-to-learn / Stored-but-cue-dependent / Fast-forgetting) plus a recall/digest speaking style, an optional persona tone, and advanced tuning (group/personal TopK and similarity thresholds). Recall runs per type before every reply: hit counts and thresholds differ for group/personal memory (enforced at the store layer); the cue-dependent type temporarily widens retrieval when the user gives recall cues (“remember? / last time / before”), and fast-forgetting only sees memories from the last 21 days by default (stored data is never deleted); a type-consistent soft “recall tone” note accompanies injected memories (broad types hedge with “I think it was…” instead of inventing). On the write side, only the employee's own posts are mildly typed: <b>Deep</b> posts are auto-marked “Important”, <b>Fast-forgetting</b> posts get shorter retention when auto-forget is on (everything else writes identically). Unset means platform-global behavior — fully backward compatible. Dedicated memory-feature spec: `docs/memory-personality.md`.

---

## 记忆管理按角色分层（已随 1.0.121 桌面安装包发布）
# Role-tiered memory-management scope (shipped in the 1.0.121 desktop installer)

中文：
- **记忆管理范围按角色划分**：平台管理员（Admin / SuperAdmin）跨全部知聚查看 / 治理任意记忆；知聚<b>群主 / 群管理员</b>可在其知聚内整群治理（对他人记忆分级 / 删除、整群遗忘、向该群导入记忆）；<b>普通成员</b>仅可查看所在知聚记忆，分级 / 删除 / 遗忘只作用于本人发言；Operator（只读运维）不因平台角色获得记忆治理特权。记忆列表逐条回传 `canManage`、`/memory/groups` 回传每群 `canManageAll`，记忆管理界面按所选知聚提示当前遗忘范围（整群 / 仅本人），并修复了非管理员“全部知聚”视图可能越权枚举其它知聚记忆的缺口（数据范围改为仅自己所在群）。

English:
- **Memory-management scope is now role-tiered**: platform admins (Admin / SuperAdmin) may view / govern memory across every group; a circle's <b>owner / admin</b> may govern the whole circle they manage (tier / delete others' memories, group-wide forgetting, importing into that group); <b>ordinary members</b> may only view memory of circles they belong to and may tier / delete / forget only their own posts; Operator (read-only ops) gains no memory-governance privilege. Each listed memory returns `canManage` and `/memory/groups` returns per-circle `canManageAll`; the memory UI hints the current forget scope per selected circle (whole circle / own posts only), and a former gap was closed where non-admins using the “all circles” view could enumerate other circles' memory (data scope is now restricted to their own circles).

---

## 企业合规（已随 1.0.121 桌面安装包发布）
# Enterprise compliance (shipped in the 1.0.121 desktop installer)

中文：
- **账号注销与数据擦除**：「我的资料 → 危险操作 → 注销账户」（需密码确认，`DELETE /ag-ui/account`）与管理员「用户管理 → 彻底删除」（`DELETE /ag-ui/admin/users/{userId}`）。删除语义：创建的知聚在存在其他用户成员时**转让群主后自行退群**、否则**解散**；加入的他人知聚自行退群；其发言**语义记忆**与**个人知识库**物理删除；**现存群中的发言匿名化**（正文 / 附件 / 提及 / 推理 / 链 / 计划清空、昵称改「已注销用户」，保留消息行与时间线）；**其创建的数字员工 / 技能孤儿清理**（不再被现存群引用的连同触发注册删除，仍被他人知聚引用的保留）；账号行 + 全部会话 + TOTP 清除（四种存储实现 `IUserStore.RemoveUser`）。防呆：最后一名超级管理员不可删；管理员不可经管理接口删除自己。
- **我的数据导出（可携权）**：资料弹窗新增「⬇ 导出我的数据」（`GET /ag-ui/account/export`）——任意登录用户可把账号资料、知聚清单、本人发言（含附件元信息）、本人语义记忆、个人知识库元数据、本人创建的数字员工 / 技能完整定义导成一个 JSON 下载，供注销前留存 / 迁移。**注销弹窗带「先导出」引导**：距上次导出超过 30 天（以服务端审计为准，跨设备一致）时提示并一键先导出。
- **孤儿定义运营盘点**：管理员控制台新增「孤儿盘点」页签（`GET /ag-ui/admin/orphans`）——列出 Owner 账号已注销的数字员工 / 技能及其引用上下文；未被引用可直接删除，仍被引用的可**接管**到自己名下再常规管理（删除有安全闸，防悬空知聚）。
- **审计日志检索 + CSV 导出 + 独立表持久化**：`AuditLogService` 支持操作者 / 操作名 / 目标 ID / 时间范围过滤；`GET /ag-ui/admin/audit`（过滤查询）、`/ag-ui/admin/audit.csv`（RFC 4180 + UTF-8 BOM 全量导出）；管理员控制台「审计日志」页签；**数据库模式（PG/MySQL/SQLite）为独立表 `agui_audit`**（写入 / 检索 / 导出 / 保留裁剪全走 SQL，保留策略 5000 条，重启保留；memory / Redis 模式为持久化扩展区）。
- **消息 👍/👎 按钮改版**：改用与复制 / 重新回答 / 撤回等头部按钮同款描边 SVG 图标（不再用 emoji 字符），并与其他头部按钮一致的「悬停消息才显示」，已评价态保留 👍绿 / 👎红高亮。
- **全局智能检索**：顶栏「🔎 全局搜索」跨知聚一次检索消息 / 语义记忆 / 知识库（`GET /ag-ui/search?q=`），结果严格按可见性过滤（成员 / 客服 staff / 顾客参与者各见其当见，不泄露他人会话与定向消息），点击消息 / 记忆可跳转到所在知聚。
- 详见 `docs/enterprise-compliance.md`。

English:
- **Account deletion & data erasure**: self-service “Delete account” in profile (password-confirmed, `DELETE /ag-ui/account`) and admin “permanently delete” in user management (`DELETE /ag-ui/admin/users/{userId}`). Semantics: groups the user owns are transferred (then they leave) when other user members exist, otherwise disbanded; groups they merely joined are left; their message **memories** and **personal knowledge bases** are physically erased; their **messages in surviving groups are anonymized** (content / attachments / mentions / reasoning / chains / plans cleared, nickname becomes “deleted user”, message rows and the timeline stay); their **owned employee / skill definitions are orphan-cleaned** (unreferenced ones are removed together with trigger registrations; ones still referenced by other people’s groups are kept); the account row, all sessions and TOTP are removed (four `IUserStore.RemoveUser` storage backends). Guards: the last super admin can never be deleted; admins cannot delete themselves via the admin API.
- **My-data export (portability)**: a new “⬇ Export my data” action in the profile (`GET /ag-ui/account/export`) lets any signed-in user download a JSON file with their profile, group memberships, their own messages (incl. attachment metadata), their semantic memories, knowledge-base metadata, and full definitions of the employees / skills they created — to keep or migrate before deletion. The **delete-account dialog now nudges you to export first** when the last export is more than 30 days old (server-side audit, consistent across devices) with a one-click export.
- **Orphan-definition inventory**: a new Admin Console “Orphan Inventory” tab (`GET /ag-ui/admin/orphans`) lists employees/skills whose owner account is gone, with their reference context; unreferenced ones can be deleted, referenced ones can be **adopted** into the current admin and managed normally (deletion is guarded to prevent orphaning groups).
- **Audit log search + CSV export + dedicated-table persistence**: `AuditLogService` filters by actor / action / targetId / time range; `GET /ag-ui/admin/audit` (filtered query), `/ag-ui/admin/audit.csv` (RFC 4180 + UTF-8 BOM full export); Admin Console “Audit Log” tab; **database storage (PG/MySQL/SQLite) uses a dedicated `agui_audit` table** (SQL-backed write/search/export/retention pruning capped at 5,000 and surviving restarts; memory/Redis fall back to the persisted section).
- **Message 👍/👎 restyle**: the like/dislike buttons now use the same outline SVG icon set as the copy / regenerate / recall head buttons (no longer raw emoji glyphs), keep the 👍 green / 👎 red selected states, and share the same hover-to-show behaviour.
- **Global intelligent search**: the top-bar “🔎 Global Search” searches messages / semantic memories / knowledge bases across all your groups at once (`GET /ag-ui/search?q=`), with strict visibility scoping (members, support-circle staff and customer participants each see only what they may — no leaking of other customers’ sessions or directed messages); clicking a message / memory jumps into its group.
- Details in `docs/enterprise-compliance.md`.

---

## 聊天多附件问答增强（已随 1.0.121 桌面安装包发布）
# Multi-attachment Q&A enhancement for chat (shipped in the 1.0.121 desktop installer)

中文：
- **聊天多附件问答增强（让每个附件都能被引用）**：向数字员工提问时一条消息可带多达 9 个附件；模型上下文现在会收到一份“附件清单”——每个附件带编号与 `attachmentId`、并注明“已注入正文 / 仅元数据”，可对任意一个单独调用 `read_attachment` 读取。可提取文本的附件（txt/md/code/pdf/docx/xlsx/pptx）按“每文件 12K 字符 + 总预算 60K”逐文件注入（此前为共享 24K 且先到先得，首个大文件会挤掉后续附件，表现成“只认第一个附件”）；`read_attachment` 工具支持**分段续读**（`startIndex`/`maxChars`，返回结束偏移供下一次继续），长文档与未自动注入的附件都能被完整引用；历史追问自动回放的文档正文预算同步放大到 24K。图片走独立视觉通道（每轮上限不变）。

English:
- **Multi-attachment Q&A now references every file**: a single chat message may carry up to 9 attachments; the model context now gets an “attachment inventory” — every file numbered with its own `attachmentId` and its status (inlined vs. metadata-only), and any file can be read on demand via `read_attachment`. Extractable text files (txt/md/code/pdf/docx/xlsx/pptx) are inlined per file (12K chars each within a 60K total, instead of a shared 24K served first-come-first-served that let the first big file crowd out later attachments and look like “only the first file was used”). `read_attachment` now supports **paginated reads** (`startIndex` / `maxChars`, returning the end offset for the next call), so long documents and non-inlined attachments can be fully referenced; the historical follow-up inline budget is also raised to 24K. Images keep their separate vision channel (per-turn cap unchanged).

---

## 新增（中文）
- **数字员工单聊（kind=direct）**：数字员工管理列表每行新增「💬 私聊」——对该数字员工点一下即开始/复用与之的一对一**私有双人群**（`POST /ag-ui/agents/direct`，幂等）；不同用户与同一数字员工的单聊各自独立、互不可见（会话隔离）；单聊默认私密（`isPrivate=true`，语义记忆仅在本私群可检索）。在单聊里发**普通（未 @）消息即视为对其直达触发**，无需手动 @。Web 与桌面一致可用（Playwright 用例 `tools/direct-chat-flow.mjs` 已全绿）。
- **实时会话吊销/禁用/改密即时断线**：登出、修改密码、管理员禁用或重置密码会**立即终止**该账号已建立的 WebSocket / SSE 实时连接（服务端主动关闭），不再等到断线才失效；会话在服务端吊销即刻生效。
- **SDK 实时通道健壮性**：断连回调在异常断连时只触发一次（此前 WS/SSE 会先带异常、再带 null 触发两次）；上行 WebSocket 发送真正串行化（SemaphoreSlim 覆盖发送本身），避免并发发送竞态。
- **上传 / 导入大包放行**：Kestrel 请求体上限与 FormOptions 同步放宽到 200MB（此前仅放宽表单解析，Kestrel 默认 30MB 仍会先拦大包），`/import`、`/upload` 大包可用。
- **组织架构构建师走“一键式”结构化出稿**：挂 `org_design` 的组织角色（如 org_architect）多挂载一个 `org_plan_draft` 工具，复用与网页「一键组织编排」同一生成引擎（`AgentOrchestrator`），一次产出「岗位 + 各岗 skillIds + 技能(kind 按 shell/http/prompt/dotnet、executionLocation 按 server/client) + 岗位连接」的整支成稿 JSON——避免整支被手写成只见 pure prompt 的软稿。只出稿不落库，须用户明确认可后再经既有 `org_commit`（仅管理员）落库。
- **协调 JSON 的 answer 真实换行也剥得干净**：模型在 `answer` 里放真实换行/未转义内容导致整包 `JsonDocument.Parse` 失败时，收尾（`UnwrapCoordinationAnswer` / 递归补查解析）会走容错提取把正文剥出来，不再把 `{"needsMore":…,"answer":…}` 整段 JSON 泄漏/截断给用户；真实换行保留成正文排版。
- **Client 技能绝不落到服务端当 bash 跑**：服务端执行器对 `ExecutionLocation=Client` 的技能一律拒跑并给指引（应经本机桥/该用户机器执行）；明显 Windows PowerShell 正文在非 Windows 宿主（Docker/Linux 服务器、非 Windows 本机宿主）直接报“需 PowerShell 环境”，而不是出现 `Not running in PowerShell / command not found / 退出码2` 这类误导性假报错。Windows 桌面自托管的 PowerShell 执行路径不受影响。
- **执行配置可在线热改（新增“执行阶段”角色级开关同理）**：管理员控制台新增「执行参数」页（`/ag-ui/admin/execution`），可在线调平台的时序/重试/TTL、执行顺序与阶段开关并一键「恢复默认」（`/defaults`），保存即对后续调用生效并经 `executionRuntime` 持久化、重启恢复；单个数字员工可在「编辑 → 执行阶段」为自身关闭桥接/交接/组织路由（`DisableBridge/DisableRelay/DisableOrgRoute`）。模型装配、记忆、协调计划总开关、网络安全收敛项与 RBAC 门槛仍属需改 appsettings/重启或固定不变——边界见 `docs/execution-configuration.md`。
- **普通编辑不再误清交接/流水线**：因角色编辑 PUT 为整表替换，普通编辑页现在会沿用在“一键编排/组织”等处配置的整轮交接（`RelayToAgentId`）、编排流水线（`Pipeline`）与差异化审批名单（`RequireApprovalToolNames`），避免随手保存时被静默清空。

## New (English)
- **Digital-employee direct chat (`kind=direct`)**: each digital employee row in the management list gains a “💬 chat” action — one click starts/reuses a one-to-one **private two-member group** with that employee (`POST /ag-ui/agents/direct`, idempotent); different users chatting with the same employee get isolated, mutually invisible sessions; direct chats are private by default (`isPrivate=true`, semantic memory only retrievable inside that private group). Inside the chat, **a plain (un-@) message means “talk to it” and triggers it directly** — no manual at-mention. Works identically in Web and desktop (Playwright flow `tools/direct-chat-flow.mjs` is green).
- **Immediate realtime teardown on logout / disable / password reset**: logging out, changing the password, or an admin disabling/resetting an account now **terminates that account’s established WebSocket / SSE connections right away** (server-initiated close), instead of waiting for the next disconnect; session revocation takes effect immediately.
- **SDK realtime robustness**: `Disconnected` now fires exactly once on an abnormal close (WS/SSE previously fired with the exception and then with `null`); WebSocket sends are fully serialized (a semaphore covers the actual `SendAsync`), removing concurrent-send races.
- **Large upload/import bodies allowed**: the Kestrel request-body limit is raised to 200MB together with `FormOptions` (previously only the form parser was relaxed, so Kestrel’s 30MB default still rejected big bodies); big `/import` and `/upload` now work.
- **Org architect drafts a whole organization the one-shot way**: an org role mounted on `org_design` (e.g., org_architect) gains an `org_plan_draft` tool that reuses the same generator as the web one-click organization orchestration (`AgentOrchestrator`) — one structured pass yields the full team JSON (roles + per-role `skillIds` + skills where `kind` is picked shell/http/prompt/dotnet and `executionLocation` server/client + connections), instead of free-chat producing mostly pure-prompt drafts. It only drafts (no write); commit still goes through `org_commit` (admin-gated) after explicit user agreement.
- **Tolerant unwrap when the coordination JSON has real newlines in answer**: when the `answer` contains genuine line breaks / unescaped content so whole-object `JsonDocument.Parse` fails, final-reply wrappers (`UnwrapCoordinationAnswer` / recursive parse) fall back to extracting the answer text instead of leaking or truncating `{"needsMore":…,"answer":…}`; real newlines are kept as line breaks.
- **Client skills are never mis-run as server bash**: the server executors refuse `ExecutionLocation=Client` with clear guidance (run on the originating machine via NativeBridge/browser); obviously PowerShell bodies on a non-Windows host (Docker/Linux server, non-Windows self-host) return a clear “PowerShell environment required” instead of the misleading `Not running in PowerShell / command not found / exit code 2`. Windows desktop self-host PowerShell execution is unchanged.
- **Execution config now tunable at runtime (plus role-level stage toggles)**: new Admin Console “Execution Params” page (`/ag-ui/admin/execution`) lets you tune platform timeouts / retries / TTLs / stage switches and stage order online and restore defaults in one click (`/defaults`); saving applies immediately to later calls and persists via `executionRuntime`. A single employee can disable bridge / relay / org-routing for itself under “Edit → Execution stages” (`DisableBridge/DisableRelay/DisableOrgRoute`). Model/provider wiring, memory, the coordinator-plan master switch, network-security toggles and RBAC gates remain appsettings/restart or fixed — the boundary is documented in `docs/execution-configuration.md`.
- **Plain editing no longer wipes relay / pipeline / per-role approvals**: because role PUT is full-replace, the ordinary edit page now carries forward the relay (`RelayToAgentId`), pipeline (`Pipeline`) and per-role approval list (`RequireApprovalToolNames`) set elsewhere — preventing a silent clear on casual save.

---

# AG-UI 群聊桌面版 1.0.119 发布说明（上一版本）
# AG-UI Group Chat Desktop 1.0.119 Release Notes (previous point release)

## 新增（中文）
- **内部协调 JSON 的“整段二次剥壳”**：智能体消息收尾（EndAgentMessage）时把整段正文再归一一次——若它就是 {\"needsMore\":…,\"answer\":…} 协调 JSON，落库与广播前统一替换为用户可读的 answer；即便此前被拆成多段流式发给用户，也会在完结前被纠正。
- 承上：Client（本机）技能只在该“发起请求的用户所在机器”执行（A 口径）、本机无桥报“执行失败，没有安装桥”；试运行结果独立弹窗、结果真正落到当前机器等系列改进持续有效。

## New (English)
- **Second-pass cleanup of coordination JSON at message end**: at agent-message End, if the whole content is an internal {\"needsMore\":…,\"answer\":…} object, it is rewritten to its user-facing answer before store/broadcast — even if earlier streamed in fragments.
- Carried: Client (local) skills run only on the originating user’s machine (policy A); no bridge → “execution failed: no bridge installed”; trial results in a dialog and truly landing on the current machine.

**版本说明**：1.0.119 为上一 Windows 桌面点版本（当前为 1.0.121），主题为「内部协调 JSON 整段二次剥壳（收尾归一）」，并包含 Client 技能 A 口径系列修复；已构建 Windows 1.0.119 MSI。本机桥日志写系统临时目录。
**Version note**: 1.0.119 is a previous Windows desktop point release (current: 1.0.121), themed “second-pass whole-message cleanup of coordination JSON”, including the Client-skill policy-A series; a Windows 1.0.119 MSI was built. Native-bridge logs live in the system temp directory.

---

# AG-UI 群聊桌面版 1.0.118 发布说明（上一版本）
# AG-UI Group Chat Desktop 1.0.118 Release Notes (previous point release)

## 新增（中文）
- **Client（本机）技能只在该“发起请求的用户所在机器”执行**：接受 A 口径——凡 `ExecutionLocation=Client` 的技能只能跑在提问者消息所附的本机桥那台机器（`msg.BridgeClient`）；该用户本机无桥 / 未上报一律返回“执行失败，没有安装桥”，不再回落任何 agent/平台级桥。桌面版（宿主即本机）直接执行，无桥限制。
- **修复“内部协调 JSON 泄漏到聊天”**：把 `{"needsMore":…,…,"answer":…}` 这类协调 JSON 的剥壳铺到各最终答复出口；若模型把内部决策 JSON 原样当正文，只把 `answer` 给用户看。
- 附带：本机/client 试运行结果独立弹窗显示、结果真落到当前机器（系列 1.0.117 已梳理的改进继续有效）。

## New (English)
- **Client (local) skills execute only on the requesting user’s machine**: under policy A, any `ExecutionLocation=Client` skill runs only on the bridge of the originator’s message (`msg.BridgeClient`); if that user has no bridge / didn’t report one, it returns “execution failed: no bridge installed” and never falls back to an agent/platform bridge. Desktop (host == the user machine) executes directly without a bridge.
- **Stop internal coordination JSON from leaking into the chat**: unwrap `{"needsMore":…,…,"answer":…}` to just `answer` at every final-reply funnel; won’t affect normal prose.
- Carried over: trial-run results shown in a dedicated dialog and really landing on the current machine (1.0.117 improvements).

**版本说明**：1.0.118 是其上一桌面点版本，主题为「Client 技能只跑在发起用户那台机器（A 口径）+ 收尾剥离内部协调 JSON」；本机桥日志写系统临时目录。
**Version note**: 1.0.118 is the previous point release, themed “Client skills run only on the originating user’s machine (policy A) + strip internal coordination JSON from final replies”.

---

# AG-UI 群聊桌面版 1.0.117 发布说明（上一版本）
# AG-UI Group Chat Desktop 1.0.117 Release Notes (previous point release)

## 新增（中文）
- **本机(client)技能的“试运行”真正落到当前机器**：技能库试运行 `/run` 里，`ExecutionLocation=Client` 的 **dotnet(C#)** 与本机 **shell** 技能（如系统内置的 `服务状态`）会经<br>本机桥（优先按浏览器上报的 `clientId` 路由；否则落在平台级桥，即当前机器）在本机**真实编译/执行并回传结果**——不再误把 PowerShell 正文交给服务端 Linux bash 而报 `Get-Service: command not found`。（前置：本机已启动 `AguiGroupChat.NativeBridge`。）
- **`/run` 对任何 Client 技能都不再回落服务端**：凡 `ExecutionLocation=Client`、但没有“进程内可直接执行”形态（http / prompt 标成 client 等）的边缘情形，试运行返回明确指引（请到挂载它的数字员工对话、由其本机桥/前端在本机执行，或改 return server 后再试），而不再静默用服务端 Runner 跑一个“名为客户端技能”的东西。
- **技能库“试运行”结果展示升级为独立弹窗**：在技能库列表（或编辑表单）点 ▶ 后，结果出现在专门“试运行结果”弹窗（列表场景）或表单下方（编辑场景），不再写进不可见角落。
- **生成工具的“要不要思考”改为按任务自动判定**：结构化/格式型生成（技能生成、一键编排、试运行建议入参、技能自测与修复、图谱抽取、群名等）一律用常规对话模型，不因全局“思考模式”切换到 reasoner（reasoner 对严格 JSON/短 token 又慢又易空/超时）；复杂方案型任务在常规模型上“先想后给”（先简述取舍再给最终 JSON）。角色人设、指派引导等开放型生成仍随全局思考模式。
- **长任务并发保护与可“停止”**：技能生成 / 组织编排预览在跑时，其它可触发长生成的按钮置灰、防并发；只读生成任务提供“停止生成”（AbortController 取消，不写库，安全）；写库的“确认并创建(apply)”刻意不暴露停止以保数据完整性。
- **修复“生成为空/很慢”**：技能/编排等 use `deepseek-reasoner`（思考模式开时）会慢慢至空——现在是仅对话等重要推理才走 reasoner，结构化工具用常规模型（实测技能生成数秒、一键编排 10s 内 done）。

## New (English)
- **Client-located skills now truly trial-run on the current machine**: in the skill-library trial run (`/run`), `ExecutionLocation=Client` **dotnet (C#)** and local **shell** skills (e.g. the built-in `服务状态`) are sent over the native bridge (prefer the reported `clientId`; else the platform bridge = the current machine) and really compile/execute locally and return results — no longer handing a PowerShell body to the server’s Linux bash (the `Get-Service: command not found` error). Prerequisite: run `AguiGroupChat.NativeBridge` on this machine.
- **`/run` never server-runs a Client skill**: any `ExecutionLocation=Client` skill lacking an in-process executable form (e.g. http/prompt mislabelled client) now returns clear guidance instead of silently running server-side.
- **Trial-run results shown in a dedicated result dialog** (list view) or under the edit form (editing view).
- **“Thinking vs not” is now auto-decided by task**: structured/formatting generators (skill generation, one-click orchestration, trial-input suggestion, self-test & repair, graph/entity extraction, group naming) always use the regular chat model — they no longer fall into `deepseek-reasoner` just because global “thinking mode” is on (reasoner is slow and often empty on rigid JSON / short tokens). Complex plan-shaped tasks do a “deliberate-first-then-JSON” pass on the fast model, while persona/role/assignment-guidance generators still follow global thinking.
- **Long-run concurrency guard + cancellable “Stop”**: while skill/orchestration previews generate, other long-trigger buttons are greyed out; read-only generation offers a Stop (AbortController; safe, writes nothing); the persisting “Confirm & create” is deliberately not cancellable to protect data integrity.
- **Fixed “generation returns empty / too slow”**: structured tools use the fast model (measured: skill generation seconds; orchestration done in <10s).

**版本说明**：1.0.117 是其上一桌面点版本，主题为「本机(client)技能的试运行真正落到当前机器 + 生成工具“按任务自动决定思考” + 长任务互斥/可取消 + 试运行结果独立弹窗」；已构建 Windows 1.0.117 MSI。本机桥桥日志改写入系统临时目录。
**Version note**: 1.0.117 is the previous point release themed “Client-located trial runs land on the real current machine via the bridge; generators decide thinking type by task; long-run tasks gray out & are cancellable; trial results shown in a dialog”. A Windows 1.0.117 MSI was built. Native-bridge logs moved to the system temp directory.

---

# AG-UI 群聊桌面版 1.0.116 发布说明（1.0.117 之前的点版本）
# AG-UI Group Chat Desktop 1.0.116 Release Notes (previous point release)

## 新增（中文）
- **新的技能类型 `dotnet`（C#）**：技能正文可写 C# 源码（含 `public static string Run(string input)` 入口），运行时经 Roslyn 编译后执行。服务端（`server`）执行的 dotnet 在 Hub 进程内的可回收 `AssemblyLoadContext` 中运行（受限引用白名单 + `AllowUnsafe=false` + 超时 / 输出上限）；独立桥 `AguiGroupChat.NativeBridge` 新增 `DotnetRunner`，能在本机执行 `kind=dotnet` 隧道任务（Source = C#），因此浏览器所在主机的客户端 dotnet 技能由本机桥运行——浏览器自身无法编译 C#，客户端 dotnet 需触发者批准后经桥执行。
- **dotnet 权限模型（管理员建、人人可跑）**：`dotnet` 与 `shell`/`http` 同属特权桶——**仅系统管理员可创建 / 修改 / 删除**；自然语言生成 `/generate` 也只为系统管理员产出 `dotnet`。但**对现有 dotnet 技能的运行对全体登录用户开放**：任何登录用户可试运行服务端 dotnet、或经数字员工 / 客户端执行由本机桥运行，无需管理员。
- **技能库“试运行”自动建议示例入参**：试运行前先经 `POST /ag-ui/skills/{skillId}/suggest` 由模型依据技能描述 / 正文自动推导并预填一段代表性的示例输入，再 `POST /run` 执行，降低试运行门槛。
- **一键组织编排 apply 冒烟自测 + 自修复**：落库前先对服务端执行的技能做冒烟：`prompt` 用样例跑一次、`http` 仅静态校验 method/url（不外呼）、server shell 仅当 `Agents.SkillAutoTestServerShell`（`AGENTS_SKILL_AUTOTEST_SHELL`，默认 true）开启才盲跑，client / 隧道类跳过；失败项由大模型自动修复（最多 3 次）。apply 返回 `smoke[]`（`{skillId,skipped,ok,attempts,repaired,lastError}`），UI 在创建后于通知中心展示。
- **Shell 脚本写盘 BOM 修复**：服务端 `SkillRunner` 曾以带 UTF-8 BOM 的方式写 shell 脚本，导致 bash 下即使是合法首条命令也会失败；现改为写入<b>无 BOM 的 UTF-8</b>。
- docker-compose 新增暴露 `AGENTS_SKILL_AUTOTEST_SHELL`（对应选项 `Agents.SkillAutoTestServerShell`）。

## New (English)
- **New skill kind `dotnet` (C#)**：a skill body can be C# source exposing `public static string Run(string input)`，compiled at run time with Roslyn and executed. Server-side (`server`) dotnet runs inside the Hub process in a collectible `AssemblyLoadContext`（constrained reference allowlist + `AllowUnsafe=false` + timeout / output caps）；the standalone bridge `AguiGroupChat.NativeBridge` gains a `DotnetRunner` that can execute `kind=dotnet` tunnel tasks (Source = C#) on the local host, so client dotnet skills on the browser's host run via the bridge——a browser itself cannot compile C#，and client dotnet runs over the bridge after the triggerer approves.
- **dotnet permission model（admin-create，everyone-can-run）**：`dotnet` shares the privileged bucket with `shell`/`http`——**only system admins can create / edit / delete**；natural-language `/generate` also yields `dotnet` only for system admins. But **running an existing dotnet skill is open to all logged-in users**：any user can trial-run a server dotnet skill，or have a client-executed one run over the native bridge，no admin needed just to run.
- **Skill library “trial run” auto-suggests an example input**：before running，`POST /ag-ui/skills/{skillId}/suggest` has the model derive and pre-fill a representative example input from the skill description / body，then `POST /run` executes it.
- **Orchestration apply now smoke-tests + self-repairs**：before persisting，server-executed skills are smoke-tested——`prompt` runs once against a sample，`http` is only config-linted (method / url validity，no outbound call)，server `shell` is blind-run only when `Agents.SkillAutoTestServerShell`（`AGENTS_SKILL_AUTOTEST_SHELL`，default true）is on，client / tunnel kinds are skipped; failures are auto-repaired by the model（up to 3 attempts）。Apply returns a `smoke[]`（`{skillId,skipped,ok,attempts,repaired,lastError}`）that the UI surfaces in the notification center after creation.
- **Shell script BOM fix**：the server-side `SkillRunner` used to write shell scripts with a UTF-8 BOM，breaking even a valid first command under bash；scripts are now written as BOM-less UTF-8.
- docker-compose now exposes `AGENTS_SKILL_AUTOTEST_SHELL`（option `Agents.SkillAutoTestServerShell`）。

**版本说明**：1.0.116 是其上一桌面点版本，主题为「dotnet（C#）技能 + 试运行建议与编排冒烟自检」并包含 shell BOM 修复。
**Version note**: 1.0.116 was the previous point release, recording dotnet (C#) skills, trial-run input suggestion & orchestration smoke/self-repair, plus the shell BOM fix.

---

# AG-UI 群聊桌面版 1.0.108 发布说明
# AG-UI Group Chat Desktop 1.0.108 Release Notes

## 新增（中文）
（提交 d14f210）
- **图片理解（视觉）**：群消息携带图片附件时，该轮数字员工回复自动路由到视觉模型、以多模态 base64 内联看图作答；纯文本消息仍走常规 / 思考模型。视觉默认模型 `deepseek-v4-flash-vision-exp`（可用 `Agents:VisionModel` 覆盖，`Agents:VisionEnabled` 默认开启为总开关；`mock` 提供方不支持视觉）。图片从附件库读取、无需另存文件，发图 / 审批协作流程不变。本期已构建 Windows 1.0.108 MSI。

## New (English)
(commit d14f210)
- **Image understanding (vision)**: when a group message carries an image attachment, that turn is auto-routed to a vision model that sees the image (fed inline as base64 multimodal content); plain text messages keep the normal / thinking model. Default vision model `deepseek-v4-flash-vision-exp` (override with `Agents:VisionModel`; `Agents:VisionEnabled` defaults on as the master switch; the `mock` provider has no vision). The image is read from the attachment store — no extra file — and the image-upload / approval workflow is unchanged. A Windows 1.0.108 MSI was built for this release.

**版本说明**：1.0.107 → 1.0.108 为点版本，主题为「图片理解（视觉）」，并已构建 Windows 1.0.108 MSI。
**Version note**: 1.0.107 → 1.0.108 is a point release adding image understanding (vision); a Windows 1.0.108 MSI was built.

---

# AG-UI 群聊桌面版 1.0.107 发布说明
# AG-UI Group Chat Desktop 1.0.107 Release Notes

## 新增（中文）
（提交 b60b9c7）
- **自动编排重名去重（自动改名避重）**：生成的数字员工 / 技能 id 与原库同名时，自动追加 `_2/_3` 改名继续保存，不再整体失败、不覆盖已有资产，并自动同步方案内引用（技能挂载、上下级连接、客服知聚成员、返回 id）。
- **组织架构交互**：双击数字员工节点可直接打开编辑表单；组织关系连线同一对端点间的多条线会**横向错开**避免完全重叠；编辑返回上下文优化——从组织架构进入编辑，退出后回组织架构，再关闭回数字员工管理列表（从列表进入则回列表）。

## New (English)
(commit b60b9c7)
- **Auto-rename on orchestration id collisions**: when a generated digital-employee / skill id collides with an existing library entry, it is auto-renamed with a `_2/_3` suffix and saved anyway — no more whole-apply failures, no overwriting existing assets — and all in-plan references (skill mounts, up/down connections, support-circle members, returned ids) are remapped to the final ids.
- **Org-chart interactions**: double-clicking a digital-employee node now opens its edit form; edges connecting the same pair of endpoints are **laterally offset** so they no longer overlap; edit-return context is optimized — editing from the org chart returns to the org chart after exit, then closes back to the digital-employee list (editing from the list returns to the list).

**版本说明**：1.0.106 → 1.0.107 为点版本，主题为「自动编排重名去重 + 组织架构交互」。全量 714 个单元 / 集成测试通过。
**Version note**: 1.0.106 → 1.0.107 is a point release focused on auto-rename collision handling during orchestration and org-chart editing/rendering interactions. All 714 unit / integration tests pass.

---

# AG-UI 群聊桌面版 1.0.106 发布说明
# AG-UI Group Chat Desktop 1.0.106 Release Notes

## 新增（中文）
（提交 386c7ee）
- **客服知聚打字指示（typing）**：客服 / 数字员工输入时，顾客参与者能看到「客服正在输入」；顾客输入时客服能看到；顾客之间互不可见，与消息隔离一致（此前客服知聚里 typing 双向都不达）。
- **智能体上下文作用域**：客服知聚的智能体上下文窗口现在会包含本次触发顾客的隔离会话（顾客自己的提问 + 定向回给该顾客的客服消息），客服能「记得」该顾客之前聊过的内容，不再像新对话一样重答；其他顾客的私聊不进上下文。

## New (English)
(commit 386c7ee)
- **Support-circle typing indicators**: while a staff member / digital employee is typing, customer participants see “staff is typing”; while a customer types, staff see it; customers never see each other's typing, consistent with message isolation (previously typing was not delivered in either direction in support circles).
- **Agent context scoping**: the support-circle agent context window now includes the triggering customer's isolated conversation (the customer's own questions plus staff replies directed to that customer), so staff can “remember” that customer's prior dialogue instead of answering as if it were a fresh chat; other customers' private chats stay out of context.

**版本说明**：1.0.105 → 1.0.106 为点版本，主题为「客服知聚 typing 与智能体上下文修复」。全量 714 个单元 / 集成测试通过。
**Version note**: 1.0.105 → 1.0.106 is a point release that fixes support-circle typing delivery and scopes the agent context to the triggering customer. All 714 unit / integration tests pass.

---

# AG-UI 群聊桌面版 1.0.105 发布说明
# AG-UI Group Chat Desktop 1.0.105 Release Notes

## 新增（中文）
- **客服知聚权限修复随本版首发**：普通顾客（参与者、非成员）可批准其触发的客服技能执行（此前会被「决策者不是群成员」拒绝）；网关仍强校验必须是触发者本人。本版桌面包首次实际包含该修复。（提交 4dd9e94）
- **自动化验证工具增强**：`tools/ui-orchestrate-flow.mjs` 现在会一并清理编排创建的数字员工与技能，避免留下测试残留；技能删除带保护（仅删不再被现存数字员工引用的，避免误删用户团队同名的真实技能）。

## New (English)
- **Support-circle permission fix ships in this build**: ordinary customers (participants, non-members) can now approve the customer-service skill execution they triggered (previously rejected as “decider is not a group member”); the gateway still requires the approver to be the triggerer. This is the first desktop build to actually include the fix. (commit 4dd9e94)
- **Automation tooling hardening**: `tools/ui-orchestrate-flow.mjs` now also deletes the digital employees and skills it creates during orchestration to avoid test residue; skill deletion is protected so it only removes skills no longer referenced by any remaining digital employee, avoiding accidental deletion of same-named real skills used by your teams.

**版本说明**：1.0.104 → 1.0.105 为点版本，主题为「随本版正式带上客服知聚权限修复 + 文档与验证工具同步」。全量 711 个单元 / 集成测试通过。
**Version note**: 1.0.104 → 1.0.105 is a point release that formally ships the support-circle permission fix along with doc and verification-tool updates. All 711 unit / integration tests pass.

---

# AG-UI 群聊桌面版 1.0.104 发布说明
# AG-UI Group Chat Desktop 1.0.104 Release Notes

## 新增（中文）
- **一键组织编排·生成过程可视化（方案 C）**：新增流式 SSE 端点 `POST /ag-ui/agents/orchestrate/stream`，DeepSeek 逐 token 流式吐出生成过程，前端实时展示原始生成文本，并基于已见 JSON 实时统计「已识别 N 名数字员工 / M 个技能」；生成结束下发完整结构化方案供确认。`AgentOrchestrator` 新增 `StreamTextAsync`（真实模型流式 / mock 分片模板）。
- **编排 apply 可勾选「同时创建客服知聚」**：落库组织方案时可把方案里的数字员工作为客服团队建群（`GroupKind.Support`），并逐个注册触发规则（`@` 即可应答），直接把方案上线服务顾客。
- **客服知聚权限修复**：普通顾客（参与者、非成员）现在可以批准其触发的客服技能执行——客服知聚的核心业务流程打通（此前会被「决策者不是群成员」拒绝）；网关仍强校验必须是触发者本人。

## New (English)
- **One-click org orchestration — generation process visualization (Plan C)**: new SSE endpoint `POST /ag-ui/agents/orchestrate/stream` streams DeepSeek's output token-by-token; the frontend shows the raw generation in real time and live counts of “N digital employees / M skills identified”; a complete structured plan is delivered when generation finishes for confirmation. `AgentOrchestrator` gains `StreamTextAsync` (streams for real models; chunked template for mock).
- **Orchestrate apply can opt in to “create a support circle”**: when persisting the plan, the plan's digital employees can be assembled into a support circle (`GroupKind.Support`) with trigger rules registered per employee (@ triggers a reply), putting the plan online to serve customers immediately.
- **Support-circle permission fix**: ordinary customers (participants, non-members) can now approve the customer-service skill execution they triggered — the core support-circle flow now works (it used to be rejected as “decider is not a group member”); the gateway still strictly requires the approver to be the triggerer themselves.

## 修复（中文）
- **本机 shell 技能经隧道执行修复**：通过编排 / 表单创建的本地（client 执行）shell 技能若未携带 `ClientRunner`，现在会自动从命令体生成（bfbce39），且网关执行时兜底从命令体合成——已有技能无需重建即可在本机经隧道执行（此前提示「该技能非本机 shell，无法经隧道执行」）。
- **编排方案兼容真实模型的 `JSON 对象` 技能体**：http/shell 技能 body 常被模型写成 JSON 对象，解析时经 `FlexibleBodyConverter` 归一化为字符串（9d6b9e5）。

## Fixed (English)
- **Local shell skills now execute over the tunnel**: local (`client`) shell skills created via orchestration / form without a `ClientRunner` are now auto-derived from the command body (bfbce39), and the gateway synthesizes one at execution time as a fallback — existing skills work over the tunnel without re-creation (previously “this skill is not a local shell type, can't run over the tunnel”).
- **Orchestration tolerates JSON-object skill bodies from real models**: http/shell skill bodies are often emitted as JSON objects; parsing now normalizes them to strings via `FlexibleBodyConverter` (9d6b9e5).

## 工具（中文）
- 新增 `tools/ui-orchestrate-flow.mjs`（Playwright）浏览器自动化：一键编排 → 建客服知聚 → API 核验 → 清理，见 `tools/README-playwright.md`。

## Tools (English)
- Added `tools/ui-orchestrate-flow.mjs` (Playwright) browser automation covering orchestrating → creating a support circle → API verification → cleanup; see `tools/README-playwright.md`.

**测试**：全量 711 个单元 / 集成测试通过。
**Tests**: all 711 unit / integration tests pass.

---

# AG-UI 群聊桌面版 —— 增补：RBAC 权限分层 + 安全加固
# AG-UI Group Chat Desktop — Addendum: RBAC layering + security hardening

## 新增（中文）
- **平台级 RBAC 分层**：账号增加 `PlatformRole`（`user / operator / admin / superadmin`）。首个注册账号自举为超级管理员；新增 Operator（只读运维）角色；`GET|POST /ag-ui/admin/roles` 由超级管理员管理角色矩阵；`/status`、`/usage`、`/audit`、`/bridge-health`、`/bridge-capabilities`、`/metrics` 降为 Operator 可读。管理员控制台「用户管理」新增角色下拉（仅超级管理员可见）。
- **群级 RBAC 收敛**：不允许把成员标为 Owner（Owner 仅群主转让得到）；授予/撤销群管理员仅群主可操作；新增 `POST /ag-ui/group/transfer-owner` 群主转让（转让后原群主降为 Admin）。
- **频道级 RBAC 保持**：`canInvokeAgents` / `canApprove` 由群主/管理员按成员管理，默认全允许。
- **安全加固**：HTTP API 移除 `?memberId=` 身份回退；客户端技能桥新增 `ClientTool:RequireAdmin` 部署开关（共享多用户部署建议开启）；模型 API Key / TOTP 密钥落盘加密（`SecretVault`）；快照可选 HMAC 签名并防静默清空。

## New (English)
- **Platform RBAC layering**: accounts gain a `PlatformRole` (`user / operator / admin / superadmin`). The first registered account self-bootstraps as Super Admin; a read-only `Operator` role is introduced; `GET|POST /ag-ui/admin/roles` lets a Super Admin manage the role matrix; `/status`, `/usage`, `/audit`, `/bridge-health`, `/bridge-capabilities`, `/metrics` are now readable by Operator+. The admin console's User Management gains a role dropdown (visible to Super Admin only).
- **Group RBAC tightening**: members can no longer be marked `Owner` via member update (`Owner` is only obtained through ownership transfer); only the Owner may grant/revoke group admin; add `POST /ag-ui/group/transfer-owner` (the previous owner becomes a group admin after transfer).
- **Channel RBAC preserved**: `canInvokeAgents` / `canApprove` per-member limits remain manageable by owner/admin, defaulting to allow.
- **Security hardening**: HTTP APIs no longer trust a `?memberId=` identity fallback; the client-skill bridge gains a `ClientTool:RequireAdmin` deployment switch (recommended for shared multi-user deployments); model API keys & TOTP secrets are encrypted at rest via `SecretVault`; snapshots support optional HMAC signing and are protected from silent data loss.

详情见 `docs/RBAC.md`。See `docs/RBAC.md` for details.

---

# AG-UI 群聊桌面版 1.0.103 发布说明
# AG-UI Group Chat Desktop 1.0.103 Release Notes

## 新增（中文）
- **新增：客服知聚（`kind=support`）**。在公有 / 私有知聚之上新增客服知聚：创建者建群时拉入的**客服团队**（真人用户或数字员工，`Role=Admin`）为知聚的全部成员，可看到**所有会话**；客服知聚对所有用户可见、可进入，无需邀请。
- **顾客非成员、会话隔离**：普通用户进入客服知聚时**不会加入成员表**（不占成员名额、不出现在成员清单），而是登记为一个带 30 分钟活动 TTL 的**顾客参与者**，获得与客服团队聊天的**独立会话**；每位顾客之间彼此隔离（A 看不到 B 的会话），客服回复某顾客定向到该顾客，客服之间的内部沟通仅客服可见。
- **接口**：`POST /ag-ui/group/create` 增加 `kind=normal|support`；`GET /ag-ui/group/discover` 发现全部客服知聚（对已登录用户可见，含 `isMember`/`hasEntered`）；`POST /ag-ui/group/{groupId}/enter` 进入（非成员登记为顾客参与者）。
- **前端**：创建弹窗增加「普通知聚 / 🛟 客服知聚」选择；侧栏自动展示可进入的客服知聚（蓝色标签 + 非成员「进入」标）；进入后作为参与者直接聊天。

## New (English)
- **New: support circles (`kind=support`)**. On top of public/private circles: the invited **support team** (humans and agent employees, `Role=Admin`) is the circle's entire membership and sees **every conversation**; a support circle is discoverable and enterable by all users without invitation.
- **Customers are non-members with isolated conversations**: entering a support circle does **not** add you to the member roster (no headcount, not listed); you register as a time-limited **customer participant** (30-min activity TTL) with your **own isolated conversation** with staff. Customers are isolated from each other; staff replies target the specific customer; internal staff chats stay staff-only.
- **APIs**: `POST /ag-ui/group/create` now takes `kind=normal|support`; `GET /ag-ui/group/discover` lists support circles (visible to any logged-in user, with `isMember`/`hasEntered`); `POST /ag-ui/group/{groupId}/enter` to enter (registers non-members as customer participants).
- **Frontend**: create dialog gains a "Normal / 🛟 Support circle" picker; the sidebar automatically shows enterable support circles (blue badge + an "Enter" chip for non-members); entered participants can chat directly.

---

# AG-UI 群聊桌面版 —— 新增：内网穿透（反向隧道）
# AG-UI Group Chat Desktop — New: NAT traversal (reverse tunnel)

## 新增（中文）
- **新增：反向隧道（HTTP/SSE）让「无公网 IP」的内网机本机桥被公网 Hub 调用**。内网机上的本机桥主动出站连公网 Hub 并注册（`--agent` 绑定数字员工）；Hub 把对该员工的客户端技能任务沿隧道下行推给内网桥执行，结果回传模型继续作答——无需入站公网端口、无需第三方隧道。
- **用法**：`NativeBridge --tunnel <hub> [--agent <id>] --tunnel-token <token>`；Hub 侧 `NativeTunnel__Token`（或 appsettings `NativeTunnel:Token`）校验。前端桥地址仍填 Hub 自身即可，网关经隧道转发。
- **平台级桥（信任整个平台）**：不再强制指定数字员工 id——`--tunnel` 时不填 `--agent` 即注册为平台级桥（`*`），一座桥服务任意数字员工的客户端技能；某员工有专属桥时优先用专属桥，否则回落到平台级桥。
- **安全加固**：逐 agent 专属隧道令牌（`NativeTunnel:AgentTokens__<agentId>`，优先于全局 `NativeTunnel:Token`）；`POST /ag-ui/native-tunnel/result` 回传需携带该 agent 有效令牌（本机桥自动带 `--agent/--tunnel-token`），防伪造结果 / 无令牌刷接口；`connect` 与 `result` 端点带内存滑动窗口限流（默认 120 / 600 次每 IP 每分钟，可配）。
- **简化：网页端取消「本机工具桥」手动配置**。移除「修改资料」里的桥地址 / 令牌输入与「一键检测」——本机 shell 执行统一经反向隧道自动路由（起桥 `--tunnel` 即生效，前端无需填任何桥配置）；无隧道桥时回落到服务器端 `/ag-ui/client-tool`。桌面版本就无需桥配置。

## New (English)
- **New: reverse tunnel (HTTP/SSE) so an intranet local bridge with no public IP can be called by the public Hub**. The bridge on the intranet host dials out to the public Hub and registers (binding a digital employee via `--agent`); the Hub pushes that employee's client-skill task down the tunnel for the bridge to execute, posting the result back so the model can continue—no inbound public port, no third-party tunnel. Usage: `NativeBridge --tunnel <hub> [--agent <id>] --tunnel-token <token>`; the Hub validates via `NativeTunnel__Token` (or appsettings `NativeTunnel:Token`). The frontend bridge URL still points at the Hub itself; the gateway forwards over the tunnel.
- **Platform-wide bridge (trust the whole platform)**: `--agent` is now optional under `--tunnel`—omit it to register as a platform-wide bridge (`*`) that serves any employee's client skills; an employee-specific bridge takes precedence when present, otherwise execution falls back to the platform-wide bridge.
- **Security hardening**: per-employee tokens (`NativeTunnel:AgentTokens__<agentId>`, takes precedence over global `NativeTunnel:Token`); the `POST /result` endpoint now requires a valid token for that agent (the bridge sends `--agent`/`--tunnel-token` automatically); in-memory sliding-window rate limiting on both `connect` and `result` (default 120 / 600 per IP per minute, configurable).
- **Simplified: the web UI's manual “Native Tool Bridge” config is gone**. Removed the bridge URL / token fields and one-click Detect from Profile; local shell execution now routes automatically through the reverse tunnel (just start the bridge with `--tunnel`), falling back to the server-side `/ag-ui/client-tool` when no tunnel bridge is connected.

---

# AG-UI 群聊桌面版 1.0.102 发布说明
# AG-UI Group Chat Desktop 1.0.102 Release Notes

## 修复与改进（中文）
- **新增：客户端执行技能（`ExecutionLocation=Client`，本机执行）**。技能可配置为「客户端执行」：服务端不执行，而是由<b>前端/本机桥</b>在浏览器所在主机执行并把结果回传（shell 走本机 PowerShell/沙箱；http 走浏览器 fetch）。前端在卡的聊天历史内确认后执行，结果回灌模型继续。
- **新增：独立「本机工具桥（NativeBridge）」**。Docker + 浏览器在本机（如 aibook）时，shell 类客户端技能需在<b>浏览器所在主机</b>执行而非 Docker 容器。新增独立项目 `src/AguiGroupChat.NativeBridge`（回环监听、令牌鉴权、CORS 白名单、沙箱/超时/截断），并在「修改资料 → 本机工具桥」提供 **🔍 一键检测** 自动读地址+令牌。桌面版无需（桌面壳即本机）。
- **新增：编排计划内客户端技能「本机一键执行全部」**。数字员工定计划时若一次选中多个客户端技能，网关把它们合并成一张 `client_tool_batch` 卡，一次确认后前端逐个本机执行、逐条点亮计划卡、最后综合回归。
- **新增：递归补查闭环（方案 C）**。数字员工基于已收集结果作答时，若发现信息不足（缺磁盘/内存/日志等关键数据），会**主动继续调技能/派下属补齐**，直到信息充分才给最终结论——不再中途停下问「要不要继续」。
- **新增：用自然语言生成技能配置**。技能库「🤖 用自然语言生成技能」：输入需求（如「检查本机磁盘使用情况」），大模型产出名称/类型/命令/描述/执行位置/ClientRunner，自动填入表单供微调保存（可选「优先本机执行」）。端点 `POST /ag-ui/skills/generate`。
- **增强：技能型智能体也走编排计划**。开启 `CoordinatorPlanning` 后，仅挂了技能（无下属/提升目标）的数字员工被 @ 时也进入计划编排（多技能批量 + 递归补查），不再回落为普通单工具调用。Docker 默认 `CoordinatorPlanning=true`。
- **修复：审批/交互卡点击后即时隐藏**。已决策（resolved）或客户端技能执行中（running）的卡片即时消失且不随任何重渲染复活（同步移除 DOM + 渲染层空化）。
- **修复：编译/运行、多技能规划、计划步骤上限**。多技能组合体检规划（6→8 步上限）与规划 prompt 引导（全面检查时多选互补技能）。

## Fixed & Improved (English)
- **New: client-execution skills (`ExecutionLocation=Client`)**. A skill can be marked "client execution" so the server does not run it; the frontend/native bridge runs it on the browser's host machine and posts the result back (shell via local PowerShell sandbox; http via browser fetch). Confirmation happens inline on the chat card, then the result is fed back to the model.
- **New: standalone NativeBridge**. For Docker + a browser on the local machine (e.g. aibook), shell client skills must run on the <b>browser's host</b> rather than the Docker container. Added `src/AguiGroupChat.NativeBridge` (loopback, token auth, CORS allowlist, sandbox/timeout/truncation) plus a **one-click Detect** in Profile -> Native Tool Bridge to read address & token. Desktop needs none (the desktop shell is the local host).
- **New: batch "run all locally" for client skills inside a plan**. When a plan selects several client skills, the gateway merges them into one `client_tool_batch` card: confirm once, the frontend runs each locally, lights up each plan step, then synthesizes a combined answer.
- **New: recursive gather-and-answer loop (Plan C)**. While answering from gathered results, if the info is insufficient (missing disk/memory/logs etc.), the digital employee proactively keeps invoking skills/direct reports until the answer is complete—never stopping to ask "continue?" in the middle.
- **New: generate skill definitions from natural language**. In the skill library, "generate from plain text": describe a request (e.g. "check local disk usage"), and the LLM produces name/kind/command/description/execution location/ClientRunner, filled into the form for review (optionally "prefer local execution"). Endpoint `POST /ag-ui/skills/generate`.
- **Enhancement: skill-only agents also take the coordinator plan**. With `CoordinatorPlanning` on, an agent that only has skills (no subordinates/escalation) also routes through plan orchestration when @-mentioned (multi-skill batch + recursive gathering) instead of plain single-tool calls. Docker defaults `CoordinatorPlanning=true`.
- **Fix: interaction cards hide immediately on decision**. A resolved or running client-tool card disappears instantly and cannot reappear on any re-render (synchronous DOM removal + empty render).

## 使用提示（中文）
客户端技能：桌面版开箱即用（桌面壳本机执行）；Docker + 本机浏览器需在「修改资料 → 本机工具桥」一键检测填入地址与令牌。自然语言生成技能需已配置 DeepSeek 等模型 key。

## Usage Note (English)
Client skills work out of the box in the desktop edition (the desktop shell executes locally); for Docker + a local browser, use Profile -> Native Tool Bridge -> Detect to fill the address & token. Generating skills from text requires a configured model key (e.g. DeepSeek).

---
文件：`AguiGroupChat-Desktop-1.0.102.msi` / Docker（postgres+ollama+web）
File: `AguiGroupChat-Desktop-1.0.102.msi` / Docker (postgres+ollama+web)

---

# AG-UI 群聊桌面版 1.0.80 发布说明
# AG-UI Group Chat Desktop 1.0.80 Release Notes

## 修复与改进（中文）
- **修复：桌面版技能库不持久化（重启丢失）**。桌面后端装配漏注册技能库持久化（`DesktopApp` 缺 `RegisterSkillPersistence()`，Web 版有）——导致桌面版里新建 / 编辑的技能不写盘、重启即丢，且数字员工经 `skillDefIds` 引用的技能在重启后会被当「不存在」跳过而不被调用。已补上 `RegisterSkillPersistence()`，桌面技能库跨重启保持。
- **新增：HTTP 技能访问本机 / 内网开关（`Agents:AllowPrivateSkillEndpoints`）**。默认关（保留 SSRF 防护，拒绝本机/内网）；确需调用本机 / 内网接口（本地 Ollama / 内网 API）时置 `true` 放行，放行后仍保留 http/https 白名单与重定向逐跳校验。桌面 `appsettings.json`，Docker `AGENTS_ALLOW_PRIVATE_SKILL_ENDPOINTS`。
- **修复：桌面版保存技能失败（405）**。桌面版后端装配漏注册技能库 API（`DesktopApp` 未调用 `MapSkillApi()`，Web 版有）；导致桌面版里技能库新增 / 编辑 / 试运行全部返回 405。已补上 `MapSkillApi()`，桌面与 Web 技能库功能一致。
- **修复：指派链路前缀末级对象重复显示**。多级下派到叶子作答时，最末端对象在「代为处理」前缀里会出现两次（如 `（Exchange连接测试助手 代为处理）` ×2）。修复 `AgentGateway` 路由链构建：末级自答时不再重复叠加末级关系节点，链路各层只出现一次。
- **修复：组织架构未保存改动时「优化指派」会出错**。新增未保存检测——新拖的指派/提升连线未点「保存」时，点「优化指派」会提示「请先保存」而不再基于过时后端数据生成。
- **指派判断只看下一层**：组织架构里的数字员工只依据<b>自己直接下一层</b>的提示词/职责判断是否指派（不向上钻、不引入更深层叶子），同时保留下层<b>多指派</b>（多候选排序 + 回退）与「收到指派后继续嵌套指派」（多级深钻链）。
- **新增：组织架构「优化指派」提示词生成**：组织架构图每个节点新增「优化指派」按钮，按该数字员工的<b>直接下一层</b>（AssignmentIds）自动生成一段「管理下一层任务指派」指引（只依据下一层挑下游、不越级、行不通则返回 NONE），预览后可追加到其 Instructions。端点 `POST /ag-ui/agents/{agentId}/optimize-assignment`（需登录，仅创建者/管理员）。
- **修复：任务指派无法按组织架构深钻到最后一层**。此前指派到达下一层时仅做「单候选贪心」：若该层语义分析返回 NONE 就提前终结，即使真正匹配的数字员工在更深层。现改为<b>多候选排序 + 递归探测回退</b>：指派持续向下钻取，某子分支无解时回退到下一候选，直到命中能作答的末端层；并向模型传入候选昵称+职责，语义匹配更准。
- **图谱 RAG 默认关闭（本次交付默认禁用，可手动开启）**：为排除图谱检索对问答/下派效果的干扰，本次 <b>Docker 与桌面版默认 `GraphEnabled=false`</b>（向量语义记忆不受影响）。需要图谱时再于 `.env`（`MEMORY_GRAPH_ENABLED=true`）或 `appsettings.json`（`Agents:Memory:GraphEnabled=true`）开启。
- **图谱 RAG 注入收敛（提升检索效果）**：图谱定位为补强而非并重——新增 `GraphMaxSectionChars=700` 注入预算，收紧默认 `GraphTopK=3→2`、`GraphMinScore=0.30→0.45`、`GraphHops=2→1`、`GraphMaxNodes=40→12`，避免图谱挤占、稀释向量切片。
- **修复：数字员工“未调用已关联的 HTTP 技能”**。根因是 HTTP 技能在创建/更新时被<b>强制置为需审批</b>（`SkillApi.BuildDef` 把 shell/http 一律写死 `RequiresApproval=true`）——技能虽已挂载，但模型一调用就进审批卡，自动化流程没有人工批，看起来就是“没被调用”。现将 `RequiresApproval` 改为：<b>仅 shell 技能强制需审批</b>，HTTP / 提示词技能跟随创建者勾选（可<b>关闭审批以自动调用</b>），需要本机/内网时另由 `Agents:AllowPrivateSkillEndpoints=true` 放行。前端技能表单相应放开 HTTP 的“需审批”开关。
- **修复：数字员工保存失败（请求格式错误 / 缺少字段）**。前端错误展示改为<b>优先显示后端返回的具体原因</b>（如“引用的技能不存在于技能库：xxx”），不再被通用错误码文案掩盖；同时排查并修正了数字员工 `skillDefIds` 引用不存在技能导致保存 400 的数据问题。
- **新增：数字员工表单「可调用子数字员工」**：把下层（或其他）数字员工挂为上层角色的可调用技能——模型需要其领域能力时可自动调起、引用其答复（即“上层正好需要下层能力时触发”）。此前后端与文档都已支持 `Skills（子代理）`，但前端表单不再暴露入口，导致无法配置；已在「技能与知识」段补回多选择器 + 每项调用说明，`skillId` 留空后端自动生成 `skill_<目标ID>`。
- **修复：所有桌面实例退出后后台进程残留**。根因是运行期 `instance-count`（记录多少个 UI 窗口在共享同一个后端）在异常退出后可能残留非零，导致后续每次启动把计数不断抬升、永远到不了 0，`/ag-ui/shutdown` 也就不会触发，后端进程残留。修复分三层：① 后端进程启动时把残留计数<b>归零</b>（新进程必然没有已存活的 UI）；② 后端自监视停机兜底——实例计数为 0 且无活动连接持续约 15s、或即使计数残留为正但无活动连接持续约 2 分钟时，后端自行优雅停机（显式 `Environment.Exit` 冲刷落盘）；③ 正常链路（计数归零 → HTTP shutdown）仍即时退出。已实测：无 UI 时后端自动退出、有 UI（计数=1）时后端保持在线。
- **新增：确定性编排计划（Coordinator Plan，`Agents:CoordinatorPlanning`）**：针对“技能/数字员工难以被可靠触发”的痛点，提供“问题 → 按<b>组织架构与技能配置</b>定计划 → <b>依次激活</b>对应数字员工与技能执行 → 聚合答复”的编排方式。开启后，带指派白名单/提升目标的路由型数字员工收到问题时，会把<b>可达的下游员工清单</b>与<b>可调用技能清单</b>显式列给模型，由其产出结构化执行计划（dispatch 派谁 / skill 调什么 / answer 汇总），再确定性逐项激活；任何环节失败自动回退到原有递归指派，不阻断主流程。桌面默认开启，Docker `AGENTS_COORDINATOR_PLANNING`；默认关（未启用时行为不变）。
- **增强：支撑<b>技能→技能 / 员工→技能的依赖链</b>**。编排计划会识别技能正文里的输入占位（`${query}` 等，见清单中技能的【需要输入：…】），并明确引导计划<b>先 dispatch 掌握该输入的员工拿值，再调用技能</b>（技能步骤自动收到上一步结果作输入）。这解决了“某技能的运行需要另一员工/前序步骤提供参数（如 Exchange 连接测试技能需要 OWA 地址，而地址由配置管理员提供）”的依赖场景；并补充了该依赖链的回归测试。
- **新增：编排计划可视化**。当确定性编排计划运行时，界面会把<b>执行计划步骤</b>广播为 `TEXT_MESSAGE_PLAN`（前端计划卡渲染）：如「指派「配置管理员」→ 调用技能「连接测试」→ 综合答复」逐项勾选展示，让用户看清“问题 → 按组织/技能定的计划 → 依次激活了谁”。
- **增强：编排计划<b>边执行边逐条点亮</b>**。计划拆分重构为「先规划 → 随消息流逐项执行」：消息开播即广播“全部待执行”的计划卡，每完成一步（派下属 / 调技能）动态把对应步骤勾选为完成并<b>实时刷新计划卡</b>，全部完成后综合答复——用户能实时看到每一步在现场点亮。测试断言：多次计划广播、首帧全未完成、末帧全部完成且含“调用技能”。
- **修复：技能需要的“值”喂不干净（如 Exchange 测试技能要 OWA 地址，却拿到带解释的文字）**。参数化技能（正文含 `${query}`）执行前，编排计划会：① 前置的取值步骤（派给配置管理员等）提示<b>只输出所需的值本身</b>；② 即便员工返回了带解释的文字，也会用 `ExtractCleanValueForSkill` <b>提取出干净的 URL/地址</b>再作为技能输入。新增 4 个取值提取单测 + 依赖链回归（断言先派后调、多次点亮）。
- **修复：协调计划派给子员工时丢上下文（配置管理员答“数据不足”）**。根因：协调计划经 `child.RunAsync` 直接调子员工时，`MemoryContextProvider` 仍按<b>宿主</b>的 `AmbientContext` 注入知识库/记忆——于是派给绑了配置库的“配置管理员”时它拿不到自己的配置库（`mail.lingtong.com`），只会答“数据不足/请提供地址”。修复：执行 dispatch 步骤时把 ambient 上下文<b>切换到子员工本人</b>（Group/Topic/触发者不变，仅 AgentId/Nickname 改为子员工），子员工据此检索自己的知识库。
- **相同修复顺带覆盖编排流水线（Pipeline）与角色交接（Relay）**：这两处直接 `child/relay.RunAsync` 调子员工/被交接方，同样会因宿主 ambient 上下文拿不到自己的知识库记忆；已做一致处理（Pipeline 每步、Relay 整轮都切到对方）。
- **“代为处理”表述对齐**：协调计划卡上被指派的步骤显示为「指派「X」代为处理」，与消息正文的前缀「（X 代为处理）」一致。
- **有计划卡时不再在正文重复“（X 代为处理）前缀”**：既然计划卡已把“指派给谁”表达清楚了，协调计划路径的消息正文不再叠加「（X 代为处理）」前缀，避免冗余；无计划卡的非编排路径仍保留前缀。
- **计划卡文字调整**：指派步骤由「指派「X」代为处理」改为「**为「X」分配工作**」。

## Fixed & Improved (English)
- **Fix: router agents no longer self-answer and swallow issues that should be dispatched (IT Service Desk → Exchange Expert scenario)**. Previously the "should I answer" semantic check ran before dispatch, so the IT Service Desk self-answered "outlook can't connect to exchange" instead of dispatching to a dedicated Exchange expert. Now <b>router nodes (with an assignment whitelist) dispatch first</b> - drill to the best-matching specialist layer before self-answering, and only fall back to self-serving when no specialist claims it; multi-level deep drilling to the last layer is also supported.
- **Assignment judgment only looks at the next layer**: an org-chart agent decides whether to assign based solely on its <b>direct subordinates'</b> prompts/roles (no up-drilling, no bringing in deeper specialist leaves), while keeping down-layer <b>multi-assignment</b> (multi-candidate ranking + fallback) and <b>nested assignment</b> after receiving a task (multi-level deep-drill chain).
- **New: org-chart "Optimize dispatch" prompt generator**: each org-chart node gains an "Optimize dispatch" button that auto-generates a "manage next-layer dispatch" guidance paragraph from the employee's <b>direct next layer</b> (AssignmentIds) - pick a subordinate based only on the next layer, no override, return NONE if none fits - previewable and appendable to its Instructions. Endpoint `POST /ag-ui/agents/{agentId}/optimize-assignment` (login required, owner/admin only).
- **Fix: task assignment cannot drill down the org tree to the last layer**. Assignment previously used a greedy single-candidate pick at the next layer: if that layer's semantic analysis returned NONE, the chain terminated early even though the real matching agent sat deeper. Now it is <b>multi-candidate ranking + recursive probe with fallback</b>: assignment keeps drilling downward, and when one branch fails it falls back to the next candidate until it reaches an answering leaf (supports inference down the org tree to the end); the model is also given each candidate's nickname + role so semantic matching is more accurate.
- **Tightened Graph RAG injection (better retrieval quality)**: the graph is now a supplement, not an equal-weight block - a `GraphMaxSectionChars=700` injection budget and tighter defaults (`GraphTopK=3→2`, `GraphMinScore=0.30→0.45`, `GraphHops=2→1`, `GraphMaxNodes=40→12`) keep it from crowding out or diluting the vector chunks.
- **Fix: digital employee "does not call its associated HTTP skill"**. Root cause: HTTP skills were hard-forced to `RequiresApproval=true` at create/update (`SkillApi.BuildDef` hard-coded shell/http), so although the skill was mounted, the model's call immediately hit an approval card that no human approved in the automated flow - looking like "not called". Now `RequiresApproval` follows the creator's choice for <b>HTTP / prompt</b> skills (can be <b>turned off to auto-invoke</b>), while <b>shell skills stay always-requiring-approval</b>; access to local/intranet targets is governed separately by `Agents:AllowPrivateSkillEndpoints=true`. The skill form's "requires approval" toggle is now enabled for HTTP.
- **Fix: digital employee save failure (request format error / missing fields)**. Error display now <b>prefers the backend's specific message</b> (e.g. "referenced skill not found in library: xxx") instead of being masked by a generic code, and a data issue where an agent's `skillDefIds` referenced a nonexistent skill causing a 400 on save was diagnosed and fixed.
- **New: "Callable sub employees" in the digital-employee form**: attach a lower (or any other) digital employee as a callable skill of an upper role - the model auto-invokes and cites it when it needs that employee's capability (i.e. "the upper employee triggering a lower employee's capability when needed"). The backend and docs already supported `Skills` (sub-agents) but the form no longer exposed an entry to configure it; a multi-selector plus a per-item call description is now restored under the "Skills & Knowledge" section, with `skillId` auto-generated (`skill_<targetAgentId>`) when left blank.
- **Fix: background process lingers after all desktop instances exit**. The runtime `instance-count` (which records how many UI windows share the one backend) could be left non-zero after an abnormal exit, so every subsequent launch kept inflating it and it never reached 0 - the `/ag-ui/shutdown` call never fired and the backend process lingered. The fix has three layers: ① the backend process <b>resets any stale count to 0 on startup</b> (a newly started backend has no live UI by definition); ② a backend <b>self-monitoring watchdog</b> gracefully stops the backend when the instance count is 0 with no active connections for ~15s, or even if the count is stale-positive but there are no active connections for ~2 minutes (`Environment.Exit` flushes persistence); ③ the normal path (count hits 0 -> HTTP shutdown) still exits immediately. Verified: the backend exits when no UI is attached and stays online when a UI (count=1) is present.
- **New: deterministic orchestration plan (Coordinator Plan, `Agents:CoordinatorPlanning`)**. To address the unreliable triggering of skills / digital employees, this adds a "question -> build a plan from the <b>org chart & skill config</b> -> <b>activate</b> the selected digital employees & skills in sequence -> aggregate the answer" orchestration. When enabled, a router-type digital employee (with an assignment whitelist / escalation target) receives a question, explicitly enumerates the <b>reachable subordinate employees</b> and <b>callable skills</b> to the model, has it produce a structured execution plan (dispatch who / invoke which skill / how to summarize), then activates each step deterministically; any failure falls back to the original recursive dispatch without blocking the flow. Enabled by default in the desktop; Docker `AGENTS_COORDINATOR_PLANNING`; default off (behavior unchanged when not enabled).
- **Enhanced: supports <b>skill->skill / employee->skill dependency chains</b>**. The orchestration plan now detects the input placeholders in a skill body (`${query}` etc., shown as 【需要输入：…】 in the inventory) and explicitly guides the plan to <b>first dispatch the employee that holds that input, then invoke the skill</b> (the skill step automatically receives the previous step's result as its input). This addresses cases where one skill's execution needs a parameter supplied by another employee / earlier step (e.g. the Exchange connectivity-test skill needs the OWA address, which the config admin provides); a regression test covers the chain.
- **New: orchestration-plan visualization**. When the deterministic orchestration plan runs, the UI now receives the <b>execution-plan steps</b> as a `TEXT_MESSAGE_PLAN` (rendered as a plan card): e.g. "dispatch to 配置管理员 -> invoke the connectivity-test skill -> synthesize the answer", shown step-by-step, so the user can see "question -> plan from the org/skills -> who was actually activated". Covered by a test that asserts the plan event contains a skill step in the dependency-chain scenario.
- **Enhanced: orchestration plan lights up step-by-step in real time**. The plan was split into "first plan, then execute step-by-step while the message streams": as soon as the message starts, the plan card is broadcast with all steps pending; each step (dispatch to an employee / invoke a skill) marks its own row complete and <b>refreshes the plan card live</b>, and after all steps the final synthesized answer streams. The test asserts multiple plan broadcasts, first frame all pending, last frame all done and containing an "invoke skill" step.
- **Fix: coordinator lost context when dispatching to a sub employee (config admin answering "insufficient data")**. Root cause: the coordinator invoked the sub employee via its own `child.RunAsync`, but `MemoryContextProvider` injected knowledge/memory based on the <b>host's</b> ambient context - so when it dispatched to a config admin bound to a config knowledge base, that admin could not see its own config base (`mail.lingtong.com`) and only answered "insufficient data / please provide the address". Fix: when executing a dispatch step, the ambient context is now <b>switched to the sub employee</b> (Group/Topic/triggerer stay the same, only AgentId/Nickname are the sub employee's) so it retrieves its own knowledge base.
- **The same fix also covers the orchestration pipeline (`Pipeline`) and role handoff (`Relay`)**: both invoke sub/passee agents via `child/relay.RunAsync` directly and would lose their own knowledge/memory under the host's ambient context; they are now handled consistently (each pipeline step and the whole relay run switch the ambient context to the counterpart).

## 使用提示（中文）
全新安装或想彻底重置：先完全退出桌面版，再删除 `%LocalAppData%\AguiGroupChat\data\` 目录后启动，第一个注册账号即为管理员。启用图谱记忆：`appsettings.json` → `Agents:Memory:GraphEnabled=true`。组织下派靠「智能体管理 → 组织架构」配置各角色的任务指派白名单（AssignmentIds）与问题提升目标。

## Usage Note (English)
For a clean start: fully quit the app, delete `%LocalAppData%\AguiGroupChat\data\`, then launch - the first account you register becomes the admin. To enable graph memory set `Agents:Memory:GraphEnabled=true` in `appsettings.json`. Configure per-role assignment whitelists (`AssignmentIds`) and escalation targets via "Agent Management → Org Chart".

---
文件：`AguiGroupChat-Desktop-1.0.80.msi` / Docker（postgres+ollama+web，`MEMORY_GRAPH_ENABLED=true`）
File: `AguiGroupChat-Desktop-1.0.80.msi` / Docker (postgres+ollama+web, `MEMORY_GRAPH_ENABLED=true`)

---

# AG-UI 群聊桌面版 1.0.79 发布说明
# AG-UI Group Chat Desktop 1.0.79 Release Notes

## 改进（中文）
- **图谱 RAG 注入收敛（提升检索效果）**：之前图谱子图（最多 40 实体 + 50 边）与向量切片平权强塞进 prompt，反而稀释了向量检索结果。本次把图谱定位为<b>补强而非并重</b>：段落强引导语「仅作参考、涉及具体事实以向量/知识库切片原文为准」，并新增 `GraphMaxSectionChars=700` 总字符预算——先保留种子/近层实体与其连接的高价值边，超出部分丢弃；同时收紧默认召回 `GraphTopK=3→2`、`GraphMinScore=0.30→0.45`、`GraphHops=2→1`、`GraphMaxNodes=40→12`，让图谱只在查询很贴近实体时少量补充，避免挤占、稀释向量切片。

## Improved (English)
- **Tightened Graph RAG injection (better retrieval quality)**: previously the graph subgraph (up to 40 entities + 50 edges) was injected with equal weight alongside the vector chunks, which diluted the vector results. The graph is now positioned as a <b>supplement</b>: the section carries explicit guidance "for reference only; specifics should defer to the vector/KB snippets", and a new `GraphMaxSectionChars=700` char budget keeps seed/nearby entities and the high-value edges connecting them first, dropping anything beyond; defaults are also tightened (`GraphTopK=3→2`, `GraphMinScore=0.30→0.45`, `GraphHops=2→1`, `GraphMaxNodes=40→12`) so the graph only adds a little when the query is very close to an entity, without crowding out the vector chunks.

## 使用提示（中文）
全新安装或想彻底重置：先完全退出桌面版，再删除 `%LocalAppData%\AguiGroupChat\data\` 目录后启动，第一个注册账号即为管理员。启用图谱记忆：`appsettings.json` → `Agents:Memory:GraphEnabled=true`。已启用用户若觉得图谱仍偏噪声，可进一步调低 `GraphMaxSectionChars` 或关闭 `GraphEnabled`。

## Usage Note (English)
For a clean start: fully quit the app, delete `%LocalAppData%\AguiGroupChat\data\`, then launch - the first account you register becomes the admin. To enable graph memory set `Agents:Memory:GraphEnabled=true` in `appsettings.json`. If you still find the graph noisy, lower `GraphMaxSectionChars` further or turn `GraphEnabled` off.

---
文件：`AguiGroupChat-Desktop-1.0.79.msi` / Docker（postgres+ollama+web，`MEMORY_GRAPH_ENABLED=true`）
File: `AguiGroupChat-Desktop-1.0.79.msi` / Docker (postgres+ollama+web, `MEMORY_GRAPH_ENABLED=true`)

---

# AG-UI 群聊桌面版 1.0.78 发布说明
# AG-UI Group Chat Desktop 1.0.78 Release Notes

## 修复（中文）
- **修复：长文档上传知识库向量化失败（返回「embedding 不可用」）**。本地 embedding（LLamaSharp / bge-m3）超长文本分段的字符/token 比值误设为 2.0，导致单次 embedding 的字数上限（context×2=4096）超过模型真实 context，凡正文切片约 2000+ 字符的文档（如员工手册类 docx）都会被整片交给模型、返回空向量而入库失败。已改为 0.9，长切片自动切成多段（每段 ≤ context×0.9 字）分别向量化、再取平均，长文档可正常入库。

## Fixed (English)
- **Fix: long documents fail vectorization on KB upload** (reported as "embedding unavailable"). The safe chars/token ratio for long-text segmenting in the local embedding (LLamaSharp / bge-m3) was mistakenly set to 2.0, so the single-shot character cap (context×2=4096) exceeded the model's real context; any document whose chunks exceed ~2000 characters (e.g. employee-handbook docx) was passed whole to the model, returned an empty vector, and failed to ingest. Changed to 0.9 - long chunks are now split into multiple segments (each ≤ context×0.9 chars), embedded separately, then averaged, so long documents ingest correctly.

## 使用提示（中文）
全新安装或想彻底重置：先完全退出桌面版，再删除 `%LocalAppData%\AguiGroupChat\data\` 目录后启动，第一个注册账号即为管理员。启用图谱记忆：`appsettings.json` → `Agents:Memory:GraphEnabled=true`。

## Usage Note (English)
For a clean start: fully quit the app, delete `%LocalAppData%\AguiGroupChat\data\`, then launch - the first account you register becomes the admin. To enable graph memory set `Agents:Memory:GraphEnabled=true` in `appsettings.json`.

---
文件：`AguiGroupChat-Desktop-1.0.78.msi`（约 584 MB，已内置本地 embedding 模型）
File: `AguiGroupChat-Desktop-1.0.78.msi` (~584 MB, bundles the local embedding model)

---

# 上一版：1.0.77
# Previous: 1.0.77

## 新增（中文）
- **知识库图谱 RAG（Graph RAG）**：上传到知识库的文档在入库时也会抽取「实体-关系-实体」建入隔离域 `kb:{KbId}` 的图谱；检索知识库时对绑定知识库做「语义召回种子实体 + n 跳图遍历」，把可达子图与向量切片并列注入 prompt，补强知识文档中的关系型知识。知识库图谱与群记忆图谱按域隔离、互不污染；删除知识库时同步清其图谱。
- **系统状态页：RAG 检索方式可视化**：管理员「系统状态」页新增两行——「向量语义记忆」（开/关）与「图谱方式（Graph RAG）」（已生效 · 实体 N / 关系 M，或未启用），直观展示当前 RAG 是否使用图谱方式。

## New (English)
- **Knowledge-base Graph RAG**: knowledge-base documents are now also entity/relation-extracted into the isolated `kb:{KbId}` graph on ingest; knowledge retrieval performs semantic seed recall + n-hop traversal over the bound knowledge bases and injects the reachable subgraph alongside the vector chunks, augmenting relational knowledge in documents. KB graphs and group-memory graphs are domain-isolated and cross-contamination-free; deleting a KB also removes its graph.
- **System-status RAG visualization**: the admin "System Status" page now shows two rows — "Vector semantic memory" (On/Off) and "Graph mode (Graph RAG)" (Active · N entities / M relations, or Not enabled), making it clear whether RAG is currently using the graph approach.

---
文件：`AguiGroupChat-Desktop-1.0.77.msi`
File: `AguiGroupChat-Desktop-1.0.77.msi`
