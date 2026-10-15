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
- `DocPreviewApiTests` **17/17**（含新增 `Preview_PlayQuery_WithNumericOne_IsRejectedWith400`）；Playwright + curl 实测 A 组各页无 JS 报错、端点 200。

### 7.4 仍未关闭项（如实标注）
- 第 7 章 §7.15-2：`/ag-ui/preview/{id}/play` 清单端点**仍无独立端点测试**（仅 `PresentationPlaybackTests.TryBuild` 结构测试），待补。
- 第 7 章 §7.15-4/5：播放 PDF 浏览器缓存、桌面版 WebView2 内播放的端到端，仍属**推断 / 手动 E2E**。
- 第 5 章 §5.13、第 9 章 §9.10 的部署相关口径（`MinScore` 实配值、`SnapshotSigningKey` 配置键口径等）为**部署相关**，未在代码层面化。
