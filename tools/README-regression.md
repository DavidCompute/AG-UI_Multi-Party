# 回归与守卫（运行期扫描 + 配置键静态校验）

五个**可复用**的仓库级守卫，用来在一次运行中对「正在跑的服务」与「配置/文档 一致性」做自动体检。
它们不替代单元 / 集成测试（`dotnet test`），而是覆盖测试之外的三类真实缺陷：

| 脚本 | 类别 | 抓什么 | CI |
|---|---|---|---|
| `api-smoke.mjs` | 运行期 | 后端 HTTP 只读主流面（46 项）：路由 404/500、鉴权错位、序列化异常、Prometheus 指标缺失 | 手动（需起服务） |
| `ui-smoke.mjs` | 运行期 | 前端主要界面（Playwright）：**未捕获运行时错误**（`ReferenceError` 等）、**i18n 原文键泄漏** | 手动（需浏览器） |
| `ui-quota-alerts.mjs` | 运行期 | 配额水位 / 覆写同步（Playwright）：用量页三张表（红/琥珀/`⚙`/豁免）、侧栏角标、水位**白名单**、`GROUP_UPDATED` 同步 `quotaPerGroup`/`model` | 手动；也可在 Actions 手动触发（`ui-regression.yml`，mock + 内存，无需本地起服务） |
| `e2e-memory-conflict.mjs` | 运行期 | **记忆冲突链路（API 级、确定性）**：发两条“同一件事改了一个值”的消息 → 体检列出 → 取代 → 列表/质量概览反映 → 体检标已处理 → 撤销取代 → 解散测试知聚 | 手动（需起服务；不调模型） |
| `ui-quality-chains.mjs` | 运行期 | **记忆质量 / 技能自愈界面（Playwright）**：记忆「📊 质量概览」有数字、「🔍 一致性体检」可开可关、产物质量弹窗「🔁 自愈闭环」有 KPI 与自动修复状态行、全程无前端运行时错误 | 手动（需浏览器） |
| `check-config-keys.mjs` | **静态** | **配置键 ↔ C# 属性漂移**：compose / appsettings 里配了、代码里没属性 → **静默忽略、不报错也不生效**（文档 §9.9 专列此坑） | **已在 CI** |
| `check-docs-links.mjs` | **静态** | **文档引用失效**：markdown 链接 + 反引号文件引用（含 `:行号` 越界）指向不存在 / 已移动的文件 | **已在 CI** |

> 三个运行期脚本都是**只读**的：API 扫描全走 GET；UI 扫描只「打开界面 + 切换页签」，新增类弹窗只开一次即关（不保存）。`ui-quota-alerts.mjs` 同样**只读**：服务端数据用 fetch 桩替换、事件直接投递，不写真实库。两个静态脚本秒级、无需起服务，已接入 `.github/workflows/ci.yml`。

## 依赖

- `api-smoke.mjs`：仅需 Node 内建 `fetch`，**零依赖**。
- `ui-smoke.mjs`：需要 Playwright。
  ```bash
  cd tools
  npm install
  npx playwright install chromium   # 首次安装浏览器内核
  ```
- `ui-quota-alerts.mjs`：同样需要 Playwright。

## 运行

```bash
# ---- API 扫描 ----
node tools/api-smoke.mjs
node tools/api-smoke.mjs --base http://127.0.0.1:5200 --user david --pass 'lingtong'
# 或环境变量：AGUI_BASE / AGUI_USER / AGUI_PASS

# ---- UI 控制台扫描 ----
cd tools && node ui-smoke.mjs                 # 默认无头
cd tools && HEADLESS=0 node ui-smoke.mjs      # 有头（可亲眼看它走一遍）
BASE_URL=http://127.0.0.1:5200 USERNAME=david PASSWORD='lingtong' node tools/ui-smoke.mjs
```

`ui-smoke.mjs` 可配 `TIMEOUT_S`（默认 240；总时长看门狗，防止任何一步挂死）；截图落在 `tools/screenshots/smoke/`。

```bash
# ---- 配额水位 / 覆写同步 回归（只读） ----
cd tools && HEADLESS=1 node ui-quota-alerts.mjs
cd tools && npm run ui:quota
```

`ui-quota-alerts.mjs` 允许跳过项（如当前账号没有知聚），此时退出码仍为 `0`。

```bash
# ---- 记忆冲突链路（API 级、确定性；不调模型） ----
node tools/e2e-memory-conflict.mjs
KEEP=1 node tools/e2e-memory-conflict.mjs     # 保留测试知聚（便于手动看界面）

# ---- 记忆质量 / 技能自愈界面 ----
cd tools && node ui-quality-chains.mjs
cd tools && HEADLESS=0 node ui-quality-chains.mjs   # 有头
```

`e2e-memory-conflict.mjs` 需服务端已启用语义记忆与 embedding（Docker 栈默认满足）；它会在测试知聚里发两条消息、验证“体检 → 取代 → 反映 → 撤销”整条闭环，结束时默认解散该知聚并遗忘记忆（`KEEP=1` 保留）。记忆未启用（mock + 内存存储）时脚本会打印 `[SKIP]` 并以退出码 `0` 退出，便于接入任意环境。

## 退出码

| 码 | 含义 |
|---|---|
| `0` | 全部通过 |
| `1` | 有失败项（脚本逐条打印：实际状态码 / 报错文本 / 泄漏的键） |
| `2`（UI） | 环境或登录失败 |
| `3`（UI） | 看门狗超时（某一步卡住） |

## UI 扫描覆盖的界面

登录后依次打开：账号菜单各弹窗（修改资料 / 修改密码 / 模型配置 / **记忆管理** / 系统状态 / 语音服务）、顶栏（数字员工管理 / 使用帮助 / 全局搜索 / 通知 / 语言 / 主题）、记忆管理的三个视图（列表 / 时间线 / 图谱）、**管理控制台全部页签**（并在各页签内点「刷新 / 新增」按钮，子弹窗只开不存）、知聚会话的知聚设置 / 输出物 / 知聚内搜索。

## 「未翻译键泄漏」探针是怎么判的（含自检）

- 遍历可见文本节点，**按空白分词**逐词判定；命中「前缀白名单 + 点号路径」的词即报（如 `common.save`、`admin.tabMetrics`）。
- 按空白分词很关键：用户实际遇到的是**一个文本节点里两个键**（`common.save common.cancel`），整段锚定会漏。
- 误报防护：`docs/user-guide.md`（含 `/`）、`app.js:7079`（含 `:`）、`deepseek-flash`（含 `-`）、`1.0.263`（数字开头）、`e.g`（前缀不在白名单）均不命中；`.hidden` 子树跳过。
- 该探针经过**注入式自检**验证：往页面塞入 `common.save common.cancel` 与一个 `.hidden` 隐藏键，确认「该抓的抓到、该跳的跳过、正常文案不误报」。

## 配置键守卫（`check-config-keys.mjs`，静态、不需跑服务）

```bash
node tools/check-config-keys.mjs                 # 校验：退出码 0 = 无未登记漂移；1 = 有漂移
node tools/check-config-keys.mjs --no-allowlist  # 自证 / 审计：把白名单抑制的已知漂移全部打出
```

怎么判：从 `docker-compose.yml`（env `X__Y__Z`）与各 `appsettings.json`（`Section:Sub:Key`）收集配置键，按「节根 → 选项类文件」映射，检查末段属性名是否真在该文件（含同文件内嵌的嵌套 Options 类）声明。

- **白名单**登记「有意保留 / 不参与强类型绑定」的键，并写明原因（如 `Observability:ReplicaId` 是 HubApp 启动时 `Configuration[...]` **直读**，非属性绑定）。
- `--no-allowlist` 让白名单失效——既能**自证探针有牙**（当前恰好现形 3 个已文档化项），也能随时审计已知漂移清单（防止白名单里悄悄积压新问题）。
- 新增配置键时若忘了在 Options 里加属性，默认模式会直接失败并指出来源文件。

## 文档引用守卫（`check-docs-links.mjs`，静态、无需跑服务）

```bash
node tools/check-docs-links.mjs   # 退出码 0 = 全部可解析；1 = 有失效引用
```

怎么判：扫全仓 `*.md`（跳过 node_modules / bin / obj / artifacts 等，忽略围栏代码块——所以本文的示例放在围栏块里就不会被当成真实引用），校验两类引用：

```text
A. markdown 相对链接：  ](docs/user-guide.md)     或  href="src/AguiGroupChat.Web/AdminApi.cs"
B. 反引号文件引用：     `docs/user-guide.md`   `src/…/ConfigGovernanceApi.cs:18-38`
```

- **markdown 相对链接**：跳过 http(s) / mailto / `#` / `data:` 开头者；去锚点 / 查询后按文件所在目录解析（也接受仓库根相对）。
- **反引号文件引用**（本仓文档的主流写法）：去 `:行号` / `#L…` 后解析，允许**简写后缀**（`Web/AttachmentApi.cs` → `src/AguiGroupChat.Web/AttachmentApi.cs`，字符串级 `endsWith`），并排除容器 / 运行时路径（`/app/…`、`data/…`）；行号**超出该文件总行数**则报错。

> 自检：往任意 md 里写一条 `` `NoSuchDir/NoSuchFile.xyz` `` 与一个不存在的 md 链接，运行即应报错（已验证探针有牙且不误报同文件的正常引用）。
> （示例用非白名单扩展名 `.xyz`，以免检查器把文档里的示例本身当成真实引用。）

## 建议的使用时机

- 每次改动前端（`index.html` / `app.js` / i18n）后跑一遍 `ui-smoke.mjs`——i18n 与运行时错误在浏览器里才现形。
- 每次后端改动 / 重建镜像后跑一遍 `api-smoke.mjs`。
- 每次动 **compose / appsettings / 选项类**后跑一遍 `check-config-keys.mjs`（纯静态、秒级）。
- 每次动 **markdown 文档 / 改文件名**后跑一遍 `check-docs-links.mjs`（纯静态、秒级）。
- 发布前三者都跑一遍，作为单元 / 集成测试之外的「仓库级体检」。
- 两个静态守卫已在 CI（`.github/workflows/ci.yml`），推送 / PR 即自动执行；两个运行期扫描需起服务，保持手动。
