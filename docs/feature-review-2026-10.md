# 近期新增功能评审记录（2026-10）

> 范围：**1.0.220 → 1.0.231** 期间新增/改动的「运营闭环」相关代码与文档，以及随之进行的**「实现—文档偏差」清单清理**。
> 目的：留下一份可追溯的评审结论（审了什么、发现了什么、怎么修的、还差什么），供后续维护与上线前复核。
> 评审方式：逐文件读代码 + 针对新面补回归用例 + 运行栈实测；结论以「代码为实证」，未验证处**如实标注**。

## 一、本次新增能力清单（按版本）

| 版本 | 能力 | 主要落点 |
| --- | --- | --- |
| 1.0.220 | **数字员工绩效看板** | `Web/AgentPerformance.cs`、`Web/AdminApi.cs`、`wwwroot/{index.html,app.js}` |
| 1.0.221 | **成本估算与预算**（`Agents:Pricing`） | `Agents/AgentOptions.cs`（`PricingOptions`）、`appsettings.json`、`docker-compose.yml` |
| 1.0.222 | **数字员工评测集 / 回归** | `Agents/{EvalCase,AgentEvalRunner}.cs`、`Web/EvalApi.cs` |
| 1.0.223 | **绩效反哺（关注标记）** + **按复杂度选模型**（`Agents:ModelRouting`） | `Web/AgentPerformance.cs`、`Agents/{AgentOptions,AgentCatalog,AgentGateway}.cs` |
| 1.0.224 | **组织体检报告**（Markdown 导出） | `Web/OrgHealthReport.cs`、`Web/AdminApi.cs` |
| 1.0.225 | **评测基线持久化**（并入报告「六、评测回归」） | `Agents/EvalCase.cs`、`Web/{EvalApi,OrgHealthReport}.cs` |
| 1.0.226 | **评分趋势折线**（`evalRuns` 历史） | `Agents/EvalCase.cs`、`Web/EvalApi.cs`、`wwwroot/app.js` |
| 1.0.227 | **评测期望交付格式**（`expectDeliverable`） | `Agents/{EvalCase,AgentEvalRunner}.cs`、`Web/EvalApi.cs` |
| 1.0.228 | **绩效反哺编排**（`Agents:PerformanceFeedback`，默认关） | `Agents/{AgentOptions,AgentGateway,PerformanceRoutingHint}.cs` |
| 1.0.229 | **本轮评审修复**（见下） | `Agents/{AgentCatalog,AgentGateway,PerformanceRoutingHint}.cs` |
| 1.0.230 | **偏差清理 A 组**（管理员控制台可见性：用户搜索 / 桥接能力协商 / 立即探测 / 只读快照 / 配置键补齐；修 `?refresh=1`→`?refresh=true`） | `wwwroot/{index.html,app.js,i18n/*.js}` |
| 1.0.231 | **偏差清理 B 组**（见下） | `wwwroot/{index.html,app.js,i18n/*.js}`、`Agents/AgentCatalog.cs`、`tests/**` |

## 二、发现并修复的缺陷（1.0.229）

### D1（真实缺陷）· 模型变体缓存未失效
- **现象**：`AgentCatalog.Upsert` / `Remove` 原先只删主缓存键 `_agents[agentId]`，**不删按模型分键的变体**（键形如 `agentId\u001Fmodel`）。
- **后果**：改了数字员工人设后，**走「视觉」或「按复杂度路由」的运行仍命中旧变体 → 继续用旧指令**。属既有隐患（视觉变体），被本次 `ModelRouting` 放大。
- **修复**：新增 `EvictAgent(agentId)`——主键 + 全部 `agentId\u001F*` 变体一并失效；`Upsert` / `Remove` 改用它。
- **回归**：`AgentCatalogDynamicTests.Upsert_EvictsModelKeyedVariants_Too`。

### D2（健壮性）· 候选重复键抛异常
- **现象**：绩效反哺下 `AgentGateway.RankAssignTargetsAsync` 曾用 `candidates.ToDictionary(...)` 构建名字表。
- **后果**：`AssignmentIds` **未去重**，候选含重复 agentId 时抛「重复键」→ 指派路由异常。
- **修复**：`PerformanceRoutingHint.BuildLines` 改为接收**名称查找委托** `Func<string,string>`，从根上消除重复键风险（并省一次分配）。
- **回归**：`PerformanceRoutingHintTests.BuildLines_DuplicateCandidates_DoNotThrow`。

## 三、已确认无问题的点（清单）

- **预算提前安装**：`InstallRunBudget` 移到取 agent 之前；`def` 彼时已可用；同一运行内后续阶段（流水线 / 指派链 / 交付兑底）重算结果一致，无语义变化。
- **默认关 = 行为不变**：`ModelRouting` / `PerformanceFeedback` 关闭时返回 null / 不注入，与旧版完全一致；两者都**只换模型或只加提示**，**不改工具 / 记忆 / 审批包装**。
- **评测判定自洽**：`AgentEvalRunner.Score` 覆盖 any / all / 空期望 / 交付格式四类分支；未指定或未知格式不卡。
- **跑分基线生命周期**：跑分写入 `lastPassed/lastRunAtMs/回复摘录`；**编辑用例即清空基线**（避免过期结果进入报告 / 页签）；`runs` 历史有界（200）。
- **组织体检报告**：各小节数据源正确；未配单价降级为「—」；无用例时给出创建提示。
- **前端**：绩效 9 列 / 评测 6 列与 colspan 一致；i18n 无缺失、无孤儿键。

## 四、验证（1.0.229）

- 新增 2 个回归用例；连同 `AgentGatewayTests` / `AgentCatalogDynamicTests` / `AgentPerformanceTests` / `EvalApiTests` / `EvalScoreTests` / `OrgHealthReportTests` / `AdminApiTests` / `DesktopCompositionTests` 合跑 **86/86**。
- `node --check`（app.js / zh.js / en.js）、i18n 审计（**zh=en=1813，missing 0**）、`appsettings.json` JSON 校验、`docker compose config` 均通过。
- 运行栈实测：重建 `web` 后健康 200；绩效 / 组织体检 / 评测端点默认返回符合预期。

## 五、设计取舍（为何这两项默认关）

`Agents:ModelRouting` 与 `Agents:PerformanceFeedback` **均默认关**：
- 绩效是**行为信号**——好评**滞后、稀疏、带主观性**；直接改写路由可能引入抖动（如因一两条差评被长期冷落）。故采取**最小干预**：`ModelRouting` 只换模型、`PerformanceFeedback` 只把数据给模型由它综合职责与绩效判断。
- 默认关保证**不改变既有行为**，可按部署环境验证后逐项启用。

## 六、未能端到端验证 / 后续

- **`ModelRouting` / `PerformanceFeedback` 的开关路径未做真实端到端**（需第二模型、带指派关系的组织、观察提示词注入）；逻辑已单测，注入点已回归。**上线前建议在测试环境开启后跑一次真实场景确认。**
- 后续（已记入 `ROADMAP.md`）：
  - 在评测中**真正生成文件**（需带工具 / 技能运行 + 审批自动放行，较重，需先定安全口径）；
  - `ModelRouting` 的 Standard 档细分 / 按租户覆写；
  - 进程内 OTLP 直推；
  - `PerformanceFeedback` 验证稳定后考虑更强的排序权重（需防抖动）。

---

> 结论：本轮运营闭环（**绩效 → 成本/预算 → 关注/建议 → 组织体检报告 → 反哺编排**）已闭合；两处缺陷已修并留回归；未验证项已在上面如实标注。

## 七、偏差清理（1.0.230 / 1.0.231）

在两轮评审之后，逐章清理了 `docs/user-guide.md` / `en.md` 各章「实现与文档的偏差 / 需复核点」里**可关闭**的剩余项。

### 7.1 A 组（1.0.230）· 管理员控制台可见性
- 管理员用户列表加搜索（`renderAdminUsers`/`adminUserRow`）；桥接能力协商接入前端（`loadBridgeCaps`）+ 「立即探测」（`#bridgeProbeBtn`）；只读参数快照（`#cfgSnapshotBtn`/`loadConfigSnapshot`）；配置治理补 `messageWriteDebounceMs`（UI 键 12 → 13）。
- **修真实缺陷**：`GET /ag-ui/admin/bridge-health|bridge-capabilities` 的 `refresh` 为 `bool?`，**传 `1` 会 400**；前端与文档统一为 `?refresh=true`。

### 7.2 B 组（1.0.231）· 跨章「需复核点」收尾
- **`#authRemember` 生效**（第 1 章）：`storeAuth()` 按复选框决定写 `localStorage`（勾选，默认）还是仅 `sessionStorage`（未勾选）。
- **帮助面板 `help.s11`/`help.s12` 补内联兜底**（第 1 章）。
- **移除两处孤儿 i18n 键**：`menu.tasks`、`docPreview.openFailed`（第 1、7 章）。
- **`applyAccentFromHex` 变量 `light` → `isDark`**（第 1 章）。
- **`subAgentCandidates` 排除条件加注释**、**组织落库工具挂载口径收紧**（`IsSystemOrgDesignSkill`，第 3 章）。
- **`BuiltinPptxSkills.Version` 核实为 `2026-09-22.2`**（第 4 章）；**`docs/memory-personality.md` 的 `TopK` / `PersonalTopK` / 组织配型措辞校正**（第 5、6 章）；**新增 `?play=1` → 400 回归用例**（第 7 章）。

### 7.3 验证（1.0.230 / 1.0.231）
- i18n 审计 **zh=en=1822，missing 0**；`node --check` / `check-i18n-keys` / `check-orphan-keys` 全过；`dotnet build` 0 错误。
- `DocPreviewApiTests` **19/19**（含新增 `Preview_PlayQuery_WithNumericOne_IsRejectedWith400` / `Play_Endpoint_ReturnsManifest` / `Preview_And_PlayPdf_SetCacheHeaders`）；Playwright + curl 实测 A 组各页无 JS 报错、端点 200。

### 7.4 仍未关闭项（如实标注）
- 第 7 章 §7.15-2 / §7.15-4：**已由 1.0.231 的两个端点用例关闭**（`Play_Endpoint_ReturnsManifest`、`Preview_And_PlayPdf_SetCacheHeaders`）。
- 第 7 章 §7.15-5：桌面版 WebView2 内播放的端到端仍属**手动 E2E**（渲染层）。
- 第 5 章 §5.13、第 9 章 §9.10 的部署相关口径（`MinScore` 实配值、`SnapshotSigningKey` 配置键口径等）为**部署相关**，未在代码层面化。

## 八、各章「偏差 / 需复核点」收尾总表（截至 1.0.231）

把 `docs/user-guide.md` / `en.md` 各章「实现与文档的偏差」「需复核点」逐条登记与结项。图例：**✅ 已关闭**＝已在 1.0.230/1.0.231 落地（代码或测试）；**📄 已按代码修正**＝文档/文案已与代码对齐；**ℹ️ 非偏差**＝经核实与代码一致或无代码依据，仅记录；**🖐 人工/手动**＝需人工确认或手动 E2E，或属「术语/文案」层面保留；**⚙️ 部署相关**＝取决于部署配置，非代码缺陷。

### 8.1 第 1 章（§1.12）

| # | 事项 | 状态 |
|---|---|---|
| 1 | help.s11/s12 的 `<li>` 缺内联兜底 | ✅ 1.0.231 |
| 2 | 登录页 `#authRemember` 复选框无效 | ✅ 1.0.231 |
| 3 | 账户菜单孤儿键 `menu.tasks` | ✅ 1.0.231（删键） |
| 4 | 注册无邮箱字段 | ℹ️ 非偏差 |
| 5 | `applyAccentFromHex` 变量 `light` 易误导 | ✅ 1.0.231（`isDark`） |

### 8.2 第 2 章（§2.15）

| # | 事项 | 状态 |
|---|---|---|
| 1–3 | 消息头动作 / 语音 toggle / 撤回权限 | 📄 已按代码修正 |
| 4–5 | 撤回图标、话题删除图标的帮助文案不精确 | 🖐 保留（帮助文案） |
| 6–8 | 右键菜单 / 知聚列表可搜索 / 语音转文字 | ℹ️ 代码无依据（任务书设想） |
| 9 | i18n 术语（owner/admin）统一 | 🖐 保留（术语层面） |
| 10 | 输出物数据源 | ℹ️ 无偏差 |
| 11 | 前端内存上限 vs 服务端历史上限 | 📄 已按代码区分 |
| 12 | 撤回后占位（服务端快照过滤） | 🖐 保留（需补充说明） |
| 复核 | 撤回广播语义 / 私聊与 @ 共用 mentions / 发言迁移话题事件 | 🖐 保留（需在手册补充） |
| 复核 | 多附件问答预算默认已统一 | ✅ 已统一 |

### 8.3 第 3 章（§3.11）

| # | 事项 | 状态 |
|---|---|---|
| 1 | 表单无「工作能力」分区（i18n 有键无控件） | ℹ️ 已知边界 |
| 2 | `AssignmentIds` / `EscalationAgentId` 由组织架构维护 | 📄 已按代码 |
| 3 | `RelayToAgentId` / `Pipeline` / `RequireApprovalToolNames` 无控件 | ℹ️ 已知边界 |
| 4 | 导出/导入不覆盖部分字段 | ℹ️ 已知边界 |
| 5 | 技能库弹窗无导入/导出按钮 | ℹ️ 已知边界 |
| 6 | 分区数量表述 | 📄 已按代码 |
| 7 | `IsSkillTarget` 表述 | 📄 已按代码 |
| 复核 1 | `subAgentCandidates` 排除条件意图 | ✅ 1.0.231（加注释） |
| 复核 2 | `org_design` 自动挂载误触发风险 | ✅ 1.0.231（限系统技能） |
| 复核 3 | 组织架构坐标按 `memberId` / 匿名访问 | 🖐 运行验证 |

### 8.4 第 4 章（§4.10 / §4.11）

| # | 事项 | 状态 |
|---|---|---|
| 4.10-1 | i18n 前缀（`itx.*` / `profile.bridge*`） | 📄 已按代码 |
| 4.10-2 | 技能库弹窗无导入导出 vs 只读错误文案「请导出」 | ℹ️ 已知边界 |
| 4.10-3 | 自然语言生成技能非 SSE 流式 | 📄 已按代码 |
| 4.10-4 | `bridge-capabilities` 未被前端消费 | ✅ 1.0.230（§4.9.3/§4.10-4 残留表述 1.0.231 补修） |
| 4.10-5 | 安装包「动态重打包 / 注入令牌」与代码不符 | 🖐 保留（README 表述待对齐） |
| 4.10-6 | zip / msi 双脚本 | 📄 已按代码 |
| 4.10-7 | 强制审批集合 = shell ∪ dotnet ∪ client | 📄 已按代码 |
| 4.10-8 | 技能表单缺 `parametersJson` / `httpTimeoutSeconds` 控件 | ℹ️ 已知边界 |
| 4.10-9 | forwarding 专门处理无依据 | ℹ️ 代码无依据 |
| 4.11 | `BuiltinPptxSkills.Version` | ✅ 1.0.230（`2026-09-22.2`） |
| 4.11 | `*.skill.txt` 完整字段集与校验细节 | 🖐 需读正文 |
| 4.11 | 本机桥静态连接 `LoadAllowedOrigin` 时机 | 🖐 未逐行验证 |

### 8.5 第 5 章（§5.12 / §5.13）

| # | 事项 | 状态 |
|---|---|---|
| 5.12-1 | 图库「库默认 0.6」vs「端点兜底 0.25」 | 📄 已按代码区分 |
| 5.12-2 | 知识库严格度与图库不同值 | 📄 已按代码 |
| 5.12-3 | 知识库图谱 RAG 默认关 | 📄 已按代码 |
| 5.12-4 | 知识库 `CanWrite` 系统级返回 true | ℹ️ 已知有意差异（UI 与端点一致） |
| 5.12-5 | `docs/memory-personality.md` 的 `TopK` 默认不符 | ✅ 1.0.231 |
| 5.12-6 | 联网取图「默认停用」而非「已删除」 | 📄 已按代码 |
| 5.13 | `MinScore` 实配值 / embedding 提供方 / 图库体量 / 沉淀群规模 / PDF 无 OCR | ⚙️ 部署相关 |

### 8.6 第 6 章（§6.12）

| # | 事项 | 状态 |
|---|---|---|
| 1 | `TopK` 默认值 | ✅ 1.0.231 |
| 2 | `PersonalTopK` 举例值 | ✅ 1.0.231 |
| 3 | i18n 前缀 `memType.*` | ℹ️ 已核实无需改 |
| 4 | 管理员「看不到任何信息」 | ℹ️ 非权限问题（查 `Memory:Enabled` / `Storage:Provider`） |
| 5 | 组织配型措辞 | ✅ 1.0.231 |
| 6 | `Scope` 默认 | ℹ️ 无偏差 |
| 7 | 图谱可视化 / 时间线回放 | ✅ 已提供（1.0.196） |
| 7 | 手动整群遗忘对重要记忆的处理 | 🖐 保留（文档未描述该细节） |
| 7 | 个人记忆程序性默认关闭 | ℹ️ 无偏差 |

### 8.7 第 7 章（§7.14 / §7.15）

| # | 事项 | 状态 |
|---|---|---|
| 7.14 | `advanceOnClick` 解析但前端未用 | ✅ 1.0.232（前端「点击画面推进」并遵循该字段） |
| 7.14 | 转场 `speed` / `orientation` / `spokes` 解析但未用 | ✅ 1.0.233（`speed` 决定转场时长）/ 🖐 保留（`orientation`、`spokes`） |
| 7.14 | 播放层媒体（视频/音频）不支持 | ℹ️ 边界 |
| 7.14 | 在线查看入口数量笔误（两个→三个） | 📄 已改正 |
| 7.14 | 「👁」按钮图标与提示 | ℹ️ 无偏差 |
| 7.15-1 | `?play=1` 返回 400 | ✅ 1.0.231（测试固化） |
| 7.15-2 | `/play` 清单端点无独立测试 | ✅ 1.0.231（`Play_Endpoint_ReturnsManifest`） |
| 7.15-3 | 孤儿键 `docPreview.openFailed` | ✅ 1.0.231（删键） |
| 7.15-4 | 播放 PDF 浏览器缓存（`private, max-age=300`） | ✅ 1.0.231（测试固化） |
| 7.15-5 | 桌面版 WebView2 内播放端到端 | 🖐 手动 E2E |

### 8.8 第 8 章（§8.17 / §8.18）

| # | 事项 | 状态 |
|---|---|---|
| 8.17-1 | 前端管理端按 `platformRole` 细分 | ℹ️ 已一致（1.0.194） |
| 8.17-2 | `GET /admin/config` 未被消费 | ✅ 1.0.230 |
| 8.17-3 | 桥健康在「运行指标」tab + 能力协商表 | ✅ 1.0.230 |
| 8.17-4 | 用户管理无搜索 | ✅ 1.0.230 |
| 8.17-5 | `execution-configuration` / `enterprise-compliance` 一致 | ℹ️ 已核对 |
| 8.18-1 | `executionOrder` 白名单含 `streaming` | 📄 文档已更正 |
| 8.18-2 | 配置治理 12 vs 13 键 | ✅ 1.0.230 |
| 8.18-3 | `bridge-capabilities` 接入前端 | ✅ 1.0.230 |
| 8.18-4 | `bridge-health` 实时探测 | ✅ 1.0.230 |
| 8.18-5 | 导入「群 id 重生成 / 密码不覆盖」语义 | 🖐 保留（迁移场景需向用户说明） |
| 8.18-6 | 审计保留 5000 条 vs 前端 `limit=200` | ℹ️ 已知边界（用 CSV 导出） |

### 8.9 第 9 章（§9.9 / §9.10）

| # | 事项 | 状态 |
|---|---|---|
| 9.9-1 | `GET /admin/config` 未被消费 | ✅ 1.0.230 |
| 9.9-2 | `bridge-capabilities` 未被消费 | ✅ 1.0.230 |
| 9.9-3 | `Memory:ModelDownloadUrl` 无强类型属性 | 🖐 保留（文档已说明，桌面组合根直读） |
| 9.9-4 | `advanceOnClick` 未用 | ✅ 1.0.232（同 7.14） |
| 9.9-5 | `Agents:WorkToolsEnabled` 无 C# 绑定 | 🖐 保留（协议/前端提到但未实现） |
| 9.9-6 | `artifacts/verify-msi-payload.py` 不存在 | ℹ️ 已核实 |
| 9.9-7 | 上传 20MB vs 产物 64MB | ℹ️ 已知边界 |
| 9.9-8 | 附件注入出厂默认已统一 | ✅ 已统一 |
| 9.10-1 | `SnapshotSigningKey` 配置键口径 | ✅ 1.0.231（修正注释为 `Persistence:...`） |
| 9.10-2 | `MaxZipBytes` vs 上传端点阈值 | ℹ️ 已知边界（`Math.Min(…, 200MB)`） |
| 9.10-3 | `ClientTool:RequireAdmin` 未在 compose 暴露 | ✅ 1.0.231（compose 暴露 `CLIENTTOOL_REQUIRE_ADMIN`，默认 false） |
| 9.10-4 | `StorageGovernanceOptions` 两个名字 | ✅ 已核实 |
| 9.10-5 | `POST /images/search` 无过滤器 | ℹ️ 安全设计说明（handler 内鉴权，自报 ID 不信） |
| 9.10-6 | `admin/config` postgres 下展示未用的 `filePath` | ℹ️ 已知边界 |
| 9.10-7 | `Agents:Memory:Enabled` 代码默认 `false` | ⚙️ 部署相关 |
| 9.10-8 | 判定/路由 `DecisionModel` 与 `ThinkingMode` 解耦 | ⚙️ 部署相关（自建自配须保留） |
| 9.10-9 | 桌面版需 LibreOffice（MSI 不捆绑） | ℹ️ 边界（首启一次性提示，`AGUI_SOFFICE` 可指定） |

### 8.10 小结

- **已关闭（✅）**：1.0.230 共 8 项（桥接能力协商、`bridge-health` 探测、`admin/config` 快照、配置治理 13 键、用户搜索、`?refresh=1` 缺陷、`BuiltinPptxSkills.Version`、`GET /admin/config` 消费）；1.0.231 共 11 项（`#authRemember`、help 内联兜底、`menu.tasks`/`docPreview.openFailed` 孤儿键、`applyAccentFromHex` 命名、`subAgentCandidates` 注释、`org_design` 加固、`TopK`/`PersonalTopK`/组织配型措辞、`SnapshotSigningKey` 注释、`§7.15-1/2/4` 三个测试、compose 暴露 `ClientTool__RequireAdmin`）；**1.0.232 共 1 项**（`advanceOnClick` 前端点击推进，见 7.14/9.9-4）；**1.0.233 共 1 项**（转场 `speed` 决定时长，见 7.6.3/7.14）。
- **文档已按代码修正（📄）**：若干（详见上表）。
- **非偏差 / 已知边界（ℹ️）**：若干。
- **保留的人工项（🖐）**：帮助文案图标不精确、i18n 术语统一、撤回广播 / 私聊 mentions / 发言迁移话题的服务端说明、`orientation`/`spokes`、`ModelDownloadUrl`、`WorkToolsEnabled`、`*.skill.txt` 正文细节、`LoadAllowedOrigin`、桌面 WebView2 播放 E2E、导入迁移语义说明。
- **部署相关（⚙️）**：§5.13 各项、`Memory:Enabled` 默认、`DecisionModel` 解耦。
