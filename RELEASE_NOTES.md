# AG-UI 群聊桌面版 1.0.290 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.290 Release Notes (current Windows desktop release)

**版本说明**：**测试投递结果留在行内**——点订阅行的「测试」后先显示「⏳ 测试中…」，完成后把 ✅ 状态码 + 时间 + 耗时（或 ❌ 错误 + 时间）直接写回该行，不再只靠一闪而过的 toast；同时刷新「最近投递」列。
**Version note**: **Test results stay in the row** — clicking **Test** on a subscription first shows "⏳ Testing…", then writes ✅ status + time + duration (or ❌ error + time) back into that row instead of relying on a fleeting toast; the **Last delivery** column refreshes as well.

## 变更
# Changes

中文：
- **内联结果**：新增会话内缓存（订阅 id → 最近测试结果），操作列下方渲染「⏳ 测试中… / ✅ 200 · 时间 · 12ms / ❌ HTTP 500 · 时间」；悬浮提示说明来自本次测试。
- **状态流**：点击后立即置「测试中」并重渲染，请求结束再写最终结果；网络异常也落到该行（❌ + 错误信息）。
- **联动**：测试投递会进投递日志，因此完成后自动重拉投递记录，「最近投递」列与投递表一并更新。
- **失效清理**：修改或删除订阅时清掉该行的旧测试结果，避免拿旧配置的结果误导。
- **无后端变更**：仍用 `POST /ag-ui/admin/webhooks/{id}/test`（返回投递记录）。
- **回归测试**：`ui-webhook-events.mjs` 扩到 **35 项**——测试失败行内 ❌ HTTP 500、测试成功行内 ✅ 200 + 耗时、且不影响「最近投递」列。

English:
- **Inline result**: a session cache (subscription id → latest test result) renders under the action buttons as "⏳ Testing… / ✅ 200 · time · 12ms / ❌ HTTP 500 · time", with a tooltip saying it is this test's result.
- **State flow**: clicking immediately shows "Testing…" and re-renders; the final result is written when the request finishes, and network errors also land in the row (❌ + message).
- **Linked refresh**: test deliveries enter the delivery log, so the delivery records are re-fetched and both the Last delivery column and the table update.
- **Stale cleanup**: editing or deleting a subscription clears its old test result so a stale config's outcome cannot mislead.
- **No backend change**: still `POST /ag-ui/admin/webhooks/{id}/test` (returns the delivery record).
- **Regression test**: `ui-webhook-events.mjs` grows to **35 checks** — a failed test shows ❌ HTTP 500 inline, a successful one shows ✅ 200 + duration, and the Last delivery column is unaffected.

## 文档
# Docs

中文：`docs/user-guide.md` / `en.md` §8.19（内联测试结果与刷新语义）；`tools/README-playwright.md`；`ROADMAP.md` / `en.md`；版本号与「适用版本」同步到 1.0.290。
English: `docs/user-guide.md` / `en.md` §8.19 (inline test result and refresh semantics); `tools/README-playwright.md`; `ROADMAP.md` / `en.md`; version numbers and "applies to version" bumped to 1.0.290.

## 测试
# Tests

中文：本轮**无服务端逻辑变更**（沿用 1.0.289 的全量结果 **1864 通过 / 0 失败**）；`node --check` 前端脚本；i18n 审计 **zh=en=2037，missing 0**；四个守卫脚本全过；Playwright **35/35**（Webhook）与 **21/21**（待审批 + 重试）；Docker 重建并部署（`/health` 200）。
English: **no server-side logic changed** this round (carrying over 1.0.289's full run of **1864 passed / 0 failed**); `node --check` passes for the frontend scripts; the i18n audit is **zh=en=2037, missing 0**; all four guard scripts pass; Playwright **35/35** (webhooks) and **21/21** (pending approvals + retry); Docker was rebuilt and redeployed (`/health` 200).

---

# AG-UI 群聊桌面版 1.0.289 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.289 Release Notes (current Windows desktop release)

**版本说明**：**失败可重试**——批准失败（目标图库已删 / 落盘报错）不再把待审批项吃掉：项与字节留在队列里，接口返回 404/400 并提示「仍待审批」；「最近处理」的失败行会标注「仍在待审批」并给出行内「重试」。只有写成功（含重复命中）或管理员拒绝才会移除项。
**Version note**: **Failures are retryable** — a failed approval (target library deleted / write error) no longer consumes the pending item: the item and its bytes stay queued, the endpoint returns 404/400 with a "still pending" hint, and the failed receipt is labelled "still pending" with an inline **Retry**. Only a successful write (including a duplicate hit) or an explicit rejection removes the item.

## 变更
# Changes

中文：
- **队列语义（`ImageWriteApprovalQueue`）**：新增 `Peek(id)`（查看不移除）与 `Remove(id)`（成功 / 重复命中后移除并删除字节）；拒绝仍用 `TryTake`。
- **批准端点**：改为「先试写、成功才移除」；失败分支保留项，并在错误消息后追加「（该项仍待审批，可稍后重试）」。
- **前端**：跟踪当前待审批 ID 集合；失败回执若仍在待审批，标「仍在待审批」并显示「重试」（直接调批准端点）；待审批集合一变就重渲染历史（不重复拉取）。
- **仍然是内存 / 磁盘双清**：成功与拒绝都会删除 `{id}.bin`；失败保留以便重试，24h TTL 到期仍会自动清理。
- **回归测试**：新增 2 例——队列 `Peek/Remove` 语义（含字节保留与删除）；集成用例「删库 → 批准 404 → 项仍在待审批 → 历史 `failed/library_missing` → 拒绝收尾」；Playwright 扩到 **21 项**（重试按钮可见、标注、点击调批准端点）。

English:
- **Queue semantics (`ImageWriteApprovalQueue`)**: adds `Peek(id)` (inspect without removing) and `Remove(id)` (drop item and bytes after success / duplicate hit); rejection still uses `TryTake`.
- **Approve endpoint**: now "try to write, remove only on success"; failure branches keep the item and append "(still pending, retry later)" to the error message.
- **Frontend**: tracks the current pending IDs; a failed receipt that is still pending is labelled "still pending" and shows **Retry** (calls approve again); the history re-renders whenever the pending set changes, without an extra fetch.
- **Still cleans up both stores**: success and rejection delete `{id}.bin`; failures keep it for retry, and the 24h TTL still expires it automatically.
- **Regression tests**: 2 new cases — queue `Peek/Remove` semantics (bytes kept then deleted) and an integration case (delete library → approve 404 → item still pending → history `failed/library_missing` → reject to clean up); Playwright grows to **21 checks** (retry button visible, label, click hits the approve endpoint).

## 文档
# Docs

中文：`docs/user-guide.md` / `en.md` §5.4.6（失败保留与重试语义）；`tools/README-playwright.md`；`ROADMAP.md` / `en.md`；版本号与「适用版本」同步到 1.0.289。
English: `docs/user-guide.md` / `en.md` §5.4.6 (failure retention and retry semantics); `tools/README-playwright.md`; `ROADMAP.md` / `en.md`; version numbers and "applies to version" bumped to 1.0.289.

## 测试
# Tests

中文：`dotnet test` 全量通过（1862 → **1864**）；`dotnet build` **0 错误**；i18n 审计 **zh=en=2035，missing 0**；四个守卫脚本全过；Playwright **21/21**（待审批直达 + 历史 + 重试）与 **32/32**（Webhook 分组 / 筛选 / 最近投递）；Docker 重建并部署（`/health` 200）。
English: full `dotnet test` passes (1862 → **1864**); `dotnet build` reports **0 errors**; the i18n audit is **zh=en=2035, missing 0**; all four guard scripts pass; Playwright **21/21** (pending click-through + history + retry) and **32/32** (webhook grouping / filters / last delivery); Docker was rebuilt and redeployed (`/health` 200).

---

# AG-UI 群聊桌面版 1.0.288 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.288 Release Notes (current Windows desktop release)

**版本说明**：**订阅列表直接显示最近投递**——每一行多出一列，按 `subscriptionId` 关联最近 100 条投递记录，直接看到最近一次 ✅/❌ 与时间；悬浮提示带错误原因与尝试次数。多订阅场景下不必再到下方投递表里按 URL 翻找。
**Version note**: **Each subscription now shows its last delivery** — a new column joins `subscriptionId` against the latest 100 deliveries to display the most recent ✅/❌ and time, with the error and attempt count in the tooltip. With multiple subscriptions there is no need to hunt for the URL in the delivery table below.

## 变更
# Changes

中文：
- **订阅表新增列**：位置在「事件」与「状态」之间；无记录时显示「—」。
- **关联规则**：投递表（新→旧）里每个订阅的第一条即最近一次；记录已滚出最近 100 条时，回落到订阅的 `lastDeliveredAt` 以 🕒 显示时间（状态见下方投递表）。
- **联动刷新**：订阅列表与投递记录无论哪个先加载完成，都会重新渲染该列；点「↻ 刷新」两者一起更新。
- **无后端变更**：仅使用既有 `GET /ag-ui/admin/webhooks`（含 `lastDeliveredAt`）与 `GET …/deliveries`（含 `subscriptionId`）。
- **回归测试**：`ui-webhook-events.mjs` 扩到 **32 项**——新增「表头含该列」「订阅 A 最近为 ❌」「订阅 B 最近为 ✅」；订阅 / 投递均用路由桩，不写真实数据。

English:
- **New subscription column**: placed between Events and Status; shows "—" when there is no record.
- **Join rule**: the first entry per subscription in the (newest-first) delivery table is the most recent one; once it scrolls past the latest 100, the column falls back to the subscription's `lastDeliveredAt` with 🕒 (status remains in the delivery table).
- **Linked refresh**: whichever of the subscription list or delivery log finishes loading, the column is rendered again; the ↻ Refresh button updates both.
- **No backend change**: only the existing `GET /ag-ui/admin/webhooks` (with `lastDeliveredAt`) and `GET …/deliveries` (with `subscriptionId`) are used.
- **Regression test**: `ui-webhook-events.mjs` grows to **32 checks** — the header contains the column, subscription A's last delivery is ❌, and subscription B's is ✅; subscriptions and deliveries are route-stubbed and no real data is written.

## 文档
# Docs

中文：`docs/user-guide.md` / `en.md` §8.19（订阅表最近投递列与关联规则）；`tools/README-playwright.md`；`ROADMAP.md` / `en.md`；版本号与「适用版本」同步到 1.0.288。
English: `docs/user-guide.md` / `en.md` §8.19 (last-delivery column and join rule); `tools/README-playwright.md`; `ROADMAP.md` / `en.md`; version numbers and "applies to version" bumped to 1.0.288.

## 测试
# Tests

中文：本轮**无服务端逻辑变更**（沿用 1.0.286 的全量结果 **1862 通过 / 0 失败**）；`node --check` 前端脚本 + UI 脚本；i18n 审计 **zh=en=2033，missing 0**；四个守卫脚本全过；Playwright **32/32**（事件分组 + 投递筛选 + 最近投递列）与 **18/18**（待审批直达 + 历史筛选）；Docker 重建并部署（`/health` 200）。
English: **no server-side logic changed** this round (carrying over 1.0.286's full run of **1862 passed / 0 failed**); `node --check` passes for the frontend and UI scripts; the i18n audit is **zh=en=2033, missing 0**; all four guard scripts pass; Playwright **32/32** (grouping + delivery filters + last-delivery column) and **18/18** (pending click-through + history filters); Docker was rebuilt and redeployed (`/health` 200).

---

# AG-UI 群聊桌面版 1.0.287 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.287 Release Notes (current Windows desktop release)

**版本说明**：**Webhook 排查更直接**——控制台的「最近投递」支持按事件类型与结果（成功 / 失败）筛选，并显示「显示 X / Y 条」；事件下拉来自实际投递（含测试 `PING`），集成出问题时可秒定位到哪类事件、哪个方向在失败。
**Version note**: **Faster webhook triage** — the console's **Recent deliveries** table filters by event type and result (succeeded / failed) and shows "showing X / Y"; the event dropdown is built from actual deliveries (including the `PING` test), so a failing integration can be narrowed to a specific event type and direction in seconds.

## 变更
# Changes

中文：
- **筛选**：新增事件类型下拉（全部 + 实际出现过的类型）与结果下拉（全部 / 成功 / 失败），两者叠加生效；计数「显示 X / Y 条」，无匹配时提示「没有匹配的投递记录」。
- **数据来源不变**：仍用 `GET /ag-ui/admin/webhooks/deliveries`（最近 100 条）；一次拉取后**前端筛选**，最多渲染 50 条（原为 30）。
- **保留选择**：刷新或重新打开页签后，事件下拉会尽量保留原选项（类型不存在时回落到「全部」）。
- **无后端变更**：路由、响应格式与权限均未动。
- **回归测试**：`ui-webhook-events.mjs` 扩到 **29 项**——投递桩 4 条、事件筛选 2 条、叠加失败 1 条、无匹配组合提示、清空筛选恢复；已随原有 CI 步骤自动执行。

English:
- **Filters**: a new event-type dropdown (All + types actually seen) and a result dropdown (All / Succeeded / Failed) combine; the counter reads "showing X / Y" and an empty combination shows "No matching deliveries".
- **Same data source**: still `GET /ag-ui/admin/webhooks/deliveries` (latest 100); filtering runs in the browser after one fetch, rendering at most 50 rows (previously 30).
- **Selection is preserved**: after a refresh or reopening the tab, the event dropdown keeps the previous choice when that type still exists (otherwise falls back to All).
- **No backend change**: routes, response shape and permissions are untouched.
- **Regression test**: `ui-webhook-events.mjs` grows to **29 checks** — four stubbed deliveries, event filter → 2, add failed → 1, an empty combination hint, and clearing filters restores 4; it runs automatically in the existing CI step.

## 文档
# Docs

中文：`docs/user-guide.md` / `en.md` §8.19（投递筛选）；`tools/README-playwright.md`；`ROADMAP.md` / `en.md`；版本号与「适用版本」同步到 1.0.287。
English: `docs/user-guide.md` / `en.md` §8.19 (delivery filtering); `tools/README-playwright.md`; `ROADMAP.md` / `en.md`; version numbers and "applies to version" bumped to 1.0.287.

## 测试
# Tests

中文：本轮**无服务端逻辑变更**（沿用 1.0.286 的全量结果 **1862 通过 / 0 失败**）；`node --check` 前端脚本 + UI 脚本；i18n 审计 **zh=en=2029，missing 0**；四个守卫脚本全过；Playwright **29/29**（事件分组 + 投递筛选）与 **18/18**（待审批直达 + 历史筛选）；Docker 重建并部署（`/health` 200）。
English: **no server-side logic changed** this round (carrying over 1.0.286's full run of **1862 passed / 0 failed**); `node --check` passes for the frontend and UI scripts; the i18n audit is **zh=en=2029, missing 0**; all four guard scripts pass; Playwright **29/29** (grouping + delivery filters) and **18/18** (pending click-through + history filters); Docker was rebuilt and redeployed (`/health` 200).

---

# AG-UI 群聊桌面版 1.0.286 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.286 Release Notes (current Windows desktop release)

**版本说明**：**「最近处理」可筛可选清**——按状态（全部 / 已批准 / 已拒绝 / 失败）与关键词（文件 / 技能 / 图库 / 处理人）快速定位回执；新增「清空历史」，二次确认后只清回执，待审批项、已落盘图片与审计日志均不受影响。
**Version note**: **Recently processed is now filterable and clearable** — find receipts by outcome (all / approved / rejected / failed) or keyword (file / skill / library / decided-by); the new **Clear history** action confirms first and removes only the receipts, leaving pending items, written images and the audit log untouched.

## 变更
# Changes

中文：
- **接口**：`GET /ag-ui/admin/image-write-pending/history` 新增 `limit`（1..50，默认 20）；新增 `DELETE` 同路径（返回 `{ cleared }`，审计 `skill.imageLibrary.historyClear`）。
- **队列**：`ClearHistory()` 返回清空条数并通知持久化（下个冲刷周期同步到 `imageWriteHistory` 扩展区）。
- **前端**：筛选栏含状态下拉、关键词输入（带 × 清除）与「清空历史」；最多拉 50 条后**前端就地过滤**，输入即时生效；无匹配时显示提示；确认框复用现有 `uiConfirm`。
- **不误伤**：清空只作用于回执列表，与待审批队列、图库图片、审计日志互不影响（确认文案已明示）。
- **测试**：队列单测 +1（清空返回条数且幂等）；集成用例追加 DELETE → GET 为空；Playwright 脚本扩到 **18 项**（三态回执、状态筛选、关键词筛选、无匹配提示、× 清除、确认框与清空）。

English:
- **API**: `GET /ag-ui/admin/image-write-pending/history` gains `limit` (1..50, default 20); new `DELETE` on the same path (returns `{ cleared }`, audited as `skill.imageLibrary.historyClear`).
- **Queue**: `ClearHistory()` returns the number cleared and notifies persistence (synced to the `imageWriteHistory` section on the next flush).
- **Frontend**: the filter row has an outcome dropdown, a keyword input (with × clear) and Clear history; up to 50 rows are fetched and **filtered in place**, so typing is instant; a hint appears when nothing matches; confirmation reuses the existing `uiConfirm`.
- **No collateral damage**: clearing only affects receipts — pending items, library images and the audit log are unaffected (stated in the confirmation text).
- **Tests**: one new queue unit test (clear returns the count and is idempotent); the integration case now also does DELETE → GET empty; the Playwright script grows to **18 checks** (three-state receipts, outcome filter, keyword filter, no-match hint, × clear, confirmation and clear).

## 文档
# Docs

中文：`docs/user-guide.md` / `en.md` §5.4.6（筛选 / 清空语义）与 API 表（`limit` + `DELETE`）；`ROADMAP.md` / `en.md`；版本号与「适用版本」同步到 1.0.286。
English: `docs/user-guide.md` / `en.md` §5.4.6 (filter / clear semantics) and the API table (`limit` + `DELETE`); `ROADMAP.md` / `en.md`; version numbers and "applies to version" bumped to 1.0.286.

## 测试
# Tests

中文：`dotnet test` 全量通过（1861 → **1862**）；`dotnet build` **0 错误**；`node --check` 前端脚本；i18n 审计 **zh=en=2022，missing 0**；四个守卫脚本全过；Playwright **18/18**；Docker 重建并部署（`/health` 200）。
English: full `dotnet test` passes (1861 → **1862**); `dotnet build` reports **0 errors**; `node --check` passes for the frontend scripts; the i18n audit is **zh=en=2022, missing 0**; all four guard scripts pass; Playwright **18/18**; Docker was rebuilt and redeployed (`/health` 200).

---

# AG-UI 群聊桌面版 1.0.285 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.285 Release Notes (current Windows desktop release)

**版本说明**：**Webhook 事件目录不再一长串平铺**——订阅弹窗按 8 个领域分组（🏠 知聚 / 👥 成员 / 💬 消息 / 🤝 协作与工具 / 🧵 话题 / ⚙️ 运行 / 📊 配额 / 🖼️ 图库），每组头部显示「已选 / 总数」并提供「全选 / 清空」；目录里将来新增但客户端尚未登记分组的类型自动落到「📦 其他」。
**Version note**: **The webhook event catalog is no longer one long flat list** — the subscription dialog groups events into 8 domains (🏠 Circles / 👥 Members / 💬 Messages / 🤝 Collaboration & tools / 🧵 Topics / ⚙️ Runs / 📊 Quota / 🖼️ Image libraries), each header showing "selected / total" with Select all / Clear; a future catalog type the client does not know yet lands in **📦 Other**.

## 变更
# Changes

中文：
- **分组渲染**：`WEBHOOK_EVENT_GROUPS` 声明「分组 → 事件类型」映射，`webhookEventGroups()` 按服务端目录过滤，未知类型归入 `other`；每组标题本地化（`whGroup.*`）。
- **整组操作**：组头按钮在「全选 / 清空」间切换，计数实时更新（如 0/3 → 3/3）；单条勾选同样更新计数与按钮文案。
- **布局**：`#whEventList` 改为专用滚动容器，组内选项两列自适应栅格（宽屏更紧凑），组间分隔线区分。
- **无需改后端**：仍使用 `GET /ag-ui/admin/webhooks/events` 的字符串数组（向后兼容）；保存时仍收集所有勾选项，请求体不变。
- **回归测试**：新增 Playwright 脚本 `ui-webhook-events.mjs`（**21 项**）——8 组齐全且无「其他」、23 个类型恰好各出现一次、组内全选/清空与计数联动、无页面 JS 异常；已接入手工 UI 回归工作流。

English:
- **Grouped rendering**: `WEBHOOK_EVENT_GROUPS` declares the "group → event types" mapping; `webhookEventGroups()` filters it against the server catalog and routes unknown types to `other`; group titles are localized (`whGroup.*`).
- **Whole-group actions**: the header button toggles between Select all / Clear and the counter updates live (e.g. 0/3 → 3/3); individual checkboxes update the counter and button label too.
- **Layout**: `#whEventList` becomes a dedicated scroll container; options inside a group use a responsive two-column grid (denser on wide screens), with separators between groups.
- **No backend change**: still consumes the `GET /ag-ui/admin/webhooks/events` string array (backwards compatible); saving still collects every checked item, so the request body is unchanged.
- **Regression test**: new Playwright script `ui-webhook-events.mjs` (**21 checks**) — all 8 groups present with no "Other", the 23 types appear exactly once, group select-all / clear and counters stay in sync, and no page JS errors; wired into the manual UI regression workflow.

## 文档
# Docs

中文：`docs/user-guide.md` / `en.md` §8.19（事件分组与整组操作）；`tools/README-playwright.md`（新脚本与 CI 步骤）；`ROADMAP.md` / `en.md`；版本号与「适用版本」同步到 1.0.285。
English: `docs/user-guide.md` / `en.md` §8.19 (grouping and whole-group actions); `tools/README-playwright.md` (new script and CI step); `ROADMAP.md` / `en.md`; version numbers and "applies to version" bumped to 1.0.285.

## 测试
# Tests

中文：本轮**无服务端逻辑变更**（沿用 1.0.284 的全量结果 **1861 通过 / 0 失败**）；`dotnet build` **0 错误**；`node --check` 三份前端脚本 + 新 UI 脚本；i18n 审计 **zh=en=2013，missing 0**；四个守卫脚本全过；Playwright **21/21**；Docker 重建并部署（`/health` 200，事件目录 **23 类**）。
English: **no server-side logic changed** this round (carrying over 1.0.284's full run of **1861 passed / 0 failed**); `dotnet build` reports **0 errors**; `node --check` passes for the three frontend scripts plus the new UI script; the i18n audit is **zh=en=2013, missing 0**; all four guard scripts pass; Playwright **21/21**; Docker was rebuilt and redeployed (`/health` 200, catalog **23 types**).

---

# AG-UI 群聊桌面版 1.0.284 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.284 Release Notes (current Windows desktop release)

**版本说明**：**审批历史跨重启保持**——「最近处理」不再随重启清空；回执（批准 / 拒绝 / 失败）写入持久化扩展区 `imageWriteHistory`，重启后图库管理里仍能回看，审计链路更完整。持久化关闭时自动退化为进程内，不影响审批本身。
**Version note**: **Approval history survives restarts** — "Recently processed" no longer resets on restart; receipts (approved / rejected / failed) are persisted in the `imageWriteHistory` section, so the table is still there after a restart. With persistence disabled it degrades to in-process without affecting approvals.

## 变更
# Changes

中文：
- **持久化 DTO**：`ResolvedImageWriteDto`（不含图片字节）；`SnapshotHistory()` / `RestoreHistory()` 保持新→旧顺序，恢复时跳过缺主键 / 状态的行、超过 50 条截断。
- **扩展区注册**：`RegisterImageWriteApprovalPersistence()` 现在注册两段—— `imageWritePending`（待审批）+ `imageWriteHistory`（最近处理）；Web 与桌面组合根无需改动。
- **及时落盘**：`RecordResolved` 调用 `ChangeHub.Notify()`，回执在下一个冲刷周期（默认 5s）写入快照，无需等下一次审批或关闭。
- **容错**：历史恢复包 try/catch——旧版快照 / 损坏数据不会阻断启动；无持久化时保持原进程内行为。
- **测试**：新增 2 例——快照 → 恢复完整闭环（含扩展区注册与字段保真）、恢复时截断与非法行跳过。

English:
- **Persistence DTO**: `ResolvedImageWriteDto` (no image bytes); `SnapshotHistory()` / `RestoreHistory()` keep newest-first order, skip rows missing id / status and truncate beyond 50 on restore.
- **Section registration**: `RegisterImageWriteApprovalPersistence()` now registers two sections — `imageWritePending` (pending) + `imageWriteHistory` (recently processed); no changes needed in the web or desktop composition roots.
- **Prompt flush**: `RecordResolved` calls `ChangeHub.Notify()`, so receipts land in the next flush cycle (5s by default) without waiting for another approval or shutdown.
- **Tolerant restore**: the history restore is wrapped in try/catch — old snapshots / corrupt data never block startup; without persistence the original in-process behaviour is unchanged.
- **Tests**: 2 new cases — full snapshot → restore round trip (including section registration and field fidelity), and truncation / invalid-row skipping on restore.

## 文档
# Docs

中文：`docs/user-guide.md` / `en.md` §5.4.6（「最近处理」持久化）与 API 表；`ROADMAP.md` / `en.md`；版本号与「适用版本」同步到 1.0.284。
English: `docs/user-guide.md` / `en.md` §5.4.6 (persisted recently-processed) and the API table; `ROADMAP.md` / `en.md`; version numbers and "applies to version" bumped to 1.0.284.

## 测试
# Tests

中文：`dotnet test` 全量通过（1859 → **1861**）；`dotnet build` **0 错误**；i18n 审计 **zh=en=2002，missing 0**；四个守卫脚本全过；Docker 重建并部署（`/health` 200，持久化文件中可见 `imageWriteHistory` 扩展区）。
English: full `dotnet test` passes (1859 → **1861**); `dotnet build` reports **0 errors**; the i18n audit is **zh=en=2002, missing 0**; all four guard scripts pass; Docker was rebuilt and redeployed (`/health` 200, the `imageWriteHistory` section is visible in the persisted state file).

---

# AG-UI 群聊桌面版 1.0.283 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.283 Release Notes (current Windows desktop release)

**版本说明**：**审批结果在站内可追溯**——图库管理新增「🧾 最近处理」，列出最近 20 条批准 / 拒绝 / 失败回执（状态、文件 → 图库、来源技能 / 岗位、处理人、时间）；失败带原因、内容重复会标注。此前只能去审计日志逐条翻。
**Version note**: **Approval outcomes are now traceable in the UI** — Image Library Management gains **🧾 Recently processed**, listing the latest 20 approved / rejected / failed receipts (status, file → library, source skill / agent, decided-by, time); failures carry a reason and duplicates are marked. Previously this required digging through the audit log.

## 变更
# Changes

中文：
- **队列历史**：`ImageWriteApprovalQueue` 新增 `RecordResolved` / `RecentResolved`，进程内保留**最近 50 条**（新→旧），不参与持久化（重启清空）。
- **新端点**：`GET /ag-ui/admin/image-write-pending/history`（仅管理员，默认返回 20 条）。字段：`pendingId / status / reason / libId / libName / fileName / skillId / agentId / assetId / duplicated / decidedBy / atMs`。
- **回执记录时机**：批准（落盘 / 命中重复）、拒绝、失败（图库已不存在 / 落盘报错）都会记录；`decidedBy` 取当前管理员用户名（取不到回落到 userId）。Webhook 与站内历史共用同一个 `EmitWriteResolved`，不会两套口径。
- **前端**：「🧾 最近处理」块（仅管理员、无数据时隐藏）；打开弹窗、单条 / 批量处理、以及弹窗打开期间的轮询都会刷新。
- **回归测试**：队列环形历史 1 例（顺序 / 上限 / 淘汰最旧）；集成用例追加校验——批准后 `GET /history` 能看到该条且 `decidedBy=iwp_admin`；Playwright 脚本扩到 **11 项**（新增最近处理块渲染）。

English:
- **Queue history**: `ImageWriteApprovalQueue` gains `RecordResolved` / `RecentResolved`, keeping the **latest 50** in process (newest first), not persisted (cleared on restart).
- **New endpoint**: `GET /ag-ui/admin/image-write-pending/history` (admin only, 20 by default). Fields: `pendingId / status / reason / libId / libName / fileName / skillId / agentId / assetId / duplicated / decidedBy / atMs`.
- **When receipts are recorded**: approve (written / duplicate hit), reject, and failure (library gone / write error); `decidedBy` is the acting admin's username (falls back to userId). Webhooks and the in-app history share one `EmitWriteResolved`, so they cannot drift.
- **Frontend**: the **🧾 Recently processed** block (admin only; hidden when empty) refreshes on dialog open, single / batch decisions, and while the dialog is open during polling.
- **Regression tests**: one ring-buffer case (order / cap / oldest evicted); the integration case now also checks that `GET /history` contains the entry with `decidedBy=iwp_admin`; the Playwright script grows to **11 checks** (recently-processed rendering).

## 文档
# Docs

中文：`docs/user-guide.md` / `en.md` §5.4.6（最近处理视图与数据来源）、§8.5 API 表（`GET …/history`）；`ROADMAP.md` / `en.md`；版本号与「适用版本」同步到 1.0.283。
English: `docs/user-guide.md` / `en.md` §5.4.6 (recently-processed view and data source) and the §8.5 API table (`GET …/history`); `ROADMAP.md` / `en.md`; version numbers and "applies to version" bumped to 1.0.283.

## 测试
# Tests

中文：`dotnet test` 全量通过（1858 → **1859**）；`dotnet build` **0 错误**；`node --check` 三份前端脚本；i18n 审计 **zh=en=2002，missing 0**；四个守卫脚本全过；Playwright UI 回归 **11/11**；Docker 重建并部署（`/health` 200，`GET …/history` 200，事件目录 **23 类**）。
English: full `dotnet test` passes (1858 → **1859**); `dotnet build` reports **0 errors**; `node --check` passes for all three frontend scripts; the i18n audit is **zh=en=2002, missing 0**; all four guard scripts pass; the Playwright UI regression is **11/11**; Docker was rebuilt and redeployed (`/health` 200, `GET …/history` 200, catalog **23 types**).

---

# AG-UI 群聊桌面版 1.0.282 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.282 Release Notes (current Windows desktop release)

**版本说明**：**审批闭环补齐**——管理员批准 / 拒绝后推出站事件 `IMAGE_WRITE_RESOLVED`（含批准、拒绝、失败三态与落盘结果），外部系统不必再轮询审批列表；同时待审批通知**可点击直达**「图库管理」，弹窗打开期间轮询会顺带刷新列表。
**Version note**: **The approval loop is now closed** — after an admin approves / rejects, an `IMAGE_WRITE_RESOLVED` outbound event is emitted (approved / rejected / failed, with the write outcome), so external systems no longer need to poll the approval list; pending notifications are also **clickable** and open Image Library Management, whose list refreshes on each poll while the dialog is open.

## 变更
# Changes

中文：
- **新事件 `IMAGE_WRITE_RESOLVED`**（`EventTypes.ImageWriteResolved` / `ImageWriteResolvedEvent`）：与 `IMAGE_WRITE_PENDING` 配对，**只经出站 Webhook**、不绑定知聚（`groupId` 为空串）。事件目录 **22 → 23**。载荷含 `pendingId / ownerUserId / skillId / agentId / libId / libName / fileName / bytes / status / duplicated / assetId / reason / timestamp`。
- **三态语义**：`status=approved` 正常落盘并带 `assetId`；内容已在目标图库时 `duplicated=true`（返回已存在的 `assetId`，不重复落盘）；`status=rejected` 表示管理员拒绝；`status=failed` 带 `reason`（`library_missing` / `write_failed`，与 4xx 响应一致）。
- **前端直达**：通知中心的待审批项带 `action`，点击直接打开图库管理（待审批块在顶部）；弹窗打开时每轮轮询顺手刷新待审批列表，批量 / 单条处理完不用重新打开。
- **回归测试**：新增 2 例——`WebhookTests.DeliverEventAsync_ImageWriteResolvedEnvelope`（信封 / 签名 / `data` 字段）与 `ImageWritePendingWebhookTests.ApprovingPendingWrite_PushesResolvedWebhook`（真实 HTTP：入队 → 批准 → 本地接收端收到 `IMAGE_WRITE_RESOLVED`，`status=approved`、`assetId` 非空）。

English:
- **New event `IMAGE_WRITE_RESOLVED`** (`EventTypes.ImageWriteResolved` / `ImageWriteResolvedEvent`): pairs with `IMAGE_WRITE_PENDING`; **webhook-only** and not bound to a circle (empty `groupId`). Catalog **22 → 23**. Payload carries `pendingId / ownerUserId / skillId / agentId / libId / libName / fileName / bytes / status / duplicated / assetId / reason / timestamp`.
- **Three-state semantics**: `status=approved` written normally with `assetId`; identical content already present sets `duplicated=true` (returns the existing `assetId`, no rewrite); `status=rejected` means an admin declined; `status=failed` carries `reason` (`library_missing` / `write_failed`, matching the 4xx response).
- **Frontend deep link**: pending notifications carry an `action`; clicking opens Image Library Management (pending block at the top). While the dialog is open each poll also refreshes the pending list, so batch / single decisions need no reopen.
- **Regression tests**: 2 new cases — `WebhookTests.DeliverEventAsync_ImageWriteResolvedEnvelope` (envelope / signature / `data` fields) and `ImageWritePendingWebhookTests.ApprovingPendingWrite_PushesResolvedWebhook` (real HTTP: enqueue → approve → local receiver gets `IMAGE_WRITE_RESOLVED` with `status=approved` and a non-empty `assetId`).

## 文档
# Docs

中文：`docs/user-guide.md` / `en.md` §5.4.6（审批回执 + 点击直达）、§8.19（事件目录 **23 类** + `IMAGE_WRITE_RESOLVED` 信封与三态语义）；`ROADMAP.md` / `en.md`；版本号与「适用版本」同步到 1.0.282。
English: `docs/user-guide.md` / `en.md` §5.4.6 (resolution receipts + click-through) and §8.19 (catalog **23 types** + `IMAGE_WRITE_RESOLVED` envelope and three-state semantics); `ROADMAP.md` / `en.md`; version numbers and "applies to version" bumped to 1.0.282.

## 测试
# Tests

中文：`dotnet test` 全量通过（1856 → **1858**）；`dotnet build` **0 错误**；`node --check` 三份前端脚本；i18n 审计 **zh=en=1995，missing 0**；四个守卫脚本全过；Playwright UI 回归 `ui-imglib-pending-click.mjs` **9/9**（通知进中心 → 点击直达 → 待审批块与批量按钮可见，列表用路由桩不写真实数据）；Docker 重建并部署（`/health` 200，事件目录实测 **23 类**）。
English: full `dotnet test` passes (1856 → **1858**); `dotnet build` reports **0 errors**; `node --check` passes for all three frontend scripts; the i18n audit is **zh=en=1995, missing 0**; all four guard scripts pass; the Playwright UI regression `ui-imglib-pending-click.mjs` passes **9/9** (notification lands in the center → click-through → pending block and batch buttons visible; the list is route-stubbed and writes no real data); Docker was rebuilt and redeployed (`/health` 200, event catalog observed at **23 types**).

---

# AG-UI 群聊桌面版 1.0.281 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.281 Release Notes (current Windows desktop release)

**版本说明**：**待审批的图库写入变得「主动可感知」**——技能请求写已有/授权图库、入队的那一刻就推出站事件 `IMAGE_WRITE_PENDING`；管理员端每 60 秒轮询，新待审批项自动进通知中心；图库管理新增「全部批准 / 全部拒绝」，不必逐条点。
**Version note**: **Pending image-library writes are now actively observable** — the moment a skill request is queued for an existing/authorized library, an `IMAGE_WRITE_PENDING` outbound event fires; the admin UI polls every 60 seconds and routes new items into the notification center; Image Library Management gains **Approve all / Reject all** instead of clicking item by item.

## 变更
# Changes

中文：
- **新事件 `IMAGE_WRITE_PENDING`**（`EventTypes.ImageWritePending` / `ImageWritePendingEvent`）：入队成功后、返回 202 之前推送；**不绑定知聚**（`groupId` 为空串），只有未限定知聚的订阅会收到；载荷 `pendingId / ownerUserId / skillId / agentId / libId / libName / fileName / bytes / timestamp`。事件目录 **21 → 22**。
- **通知中心主动提醒**：管理员登录后每 **60 秒**拉取一次待审批列表，**未见过**的项各生成一条 `approval` 通知（页面在后台时同时弹系统通知）；登出即停；去重集合按当前待审批项裁剪，长时间在线不会无限增长。
- **批量审批**：图库管理顶部标题行新增「全部批准 / 全部拒绝」；服务端逐条调用单条端点，**单条失败不阻断**其余项，完成后提示「已处理 X / N 项」。
- **回归测试**：新增 3 例——`WebhookTests.DeliverEventAsync_ImageWritePendingEnvelope`（信封 + 签名 + 载荷字段）与 `ImageWritePendingWebhookTests`（2 例，真实 HTTP 全链路：建库 → 平台签发审批型写入句柄 → 技能自令牌上传 → **202 pendingId** → 本地接收端收到带完整信封与 `data` 字段的 `IMAGE_WRITE_PENDING`；以及事件目录包含该类型）。

English:
- **New event `IMAGE_WRITE_PENDING`** (`EventTypes.ImageWritePending` / `ImageWritePendingEvent`): pushed after a successful enqueue and before the 202 response; **not bound to a circle** (empty `groupId`), so only subscriptions without a circle filter receive it; payload `pendingId / ownerUserId / skillId / agentId / libId / libName / fileName / bytes / timestamp`. Event catalog **21 → 22**.
- **Notification-center awareness**: after login an admin polls the pending list every **60 seconds** and creates one `approval` notification per **unseen** item (plus a system notification when the page is in the background); polling stops on logout; the dedup set is pruned to the current items so a long session does not grow unbounded.
- **Batch approval**: the header row gains **Approve all / Reject all**; each item goes through the single-item endpoint, **one failure does not block the rest**, and a final toast reports "processed X / N".
- **Regression tests**: 3 new cases — `WebhookTests.DeliverEventAsync_ImageWritePendingEnvelope` (envelope + signature + payload fields) and `ImageWritePendingWebhookTests` (2 cases; full HTTP path: create a library → mint an approval-required write handle → upload with the skill self token → **202 pendingId** → a local receiver gets `IMAGE_WRITE_PENDING` with the full envelope and `data` fields; plus the event catalog contains the type).

## 文档
# Docs

中文：`docs/user-guide.md` / `en.md` §5.4.6（主动感知 + 批量审批）、§8.19（事件目录 **22 类** + `IMAGE_WRITE_PENDING` 信封示例与投递语义）；`ROADMAP.md` / `en.md`；版本号与「适用版本」同步到 1.0.281。
English: `docs/user-guide.md` / `en.md` §5.4.6 (active awareness + batch approval) and §8.19 (catalog **22 types** + `IMAGE_WRITE_PENDING` envelope example and delivery semantics); `ROADMAP.md` / `en.md`; version numbers and "applies to version" bumped to 1.0.281.

## 测试
# Tests

中文：`dotnet test` 全量通过（1853 → **1856**）；`dotnet build` **0 错误**；`node --check` 三份前端脚本；i18n 审计 **zh=en=1995，missing 0**；`check-i18n-keys` / `check-orphan-keys` / `check-config-keys` / `check-docs-links` 全过；Docker 重建并部署（`/health` 200），端到端由新增集成测试在真实 HTTP 路径上覆盖。
English: full `dotnet test` passes (1853 → **1856**); `dotnet build` reports **0 errors**; `node --check` passes for all three frontend scripts; the i18n audit is **zh=en=1995, missing 0**; `check-i18n-keys` / `check-orphan-keys` / `check-config-keys` / `check-docs-links` all pass; Docker was rebuilt and redeployed (`/health` 200), and the end-to-end path is covered by the new integration test over real HTTP.

---

# AG-UI 群聊桌面版 1.0.280 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.280 Release Notes (current Windows desktop release)

**版本说明**：**待审批写入不再一重启就消失**——审批队列的元数据落到持久化扩展区 `imageWritePending`，图片字节落到 `data/pending-writes/{id}.bin`（与 Web 数据同卷），重启后自动恢复，管理员仍可继续批准 / 拒绝。持久化关闭时退化为纯内存，并在启动时清理超 TTL 的孤儿文件。
**Version note**: **Pending writes no longer vanish on restart** — approval metadata is persisted in the `imageWritePending` section and image bytes in `data/pending-writes/{id}.bin` (same data volume as the web host), so entries are restored after a restart and can still be approved / rejected. With persistence disabled it falls back to memory and cleans orphan files past the TTL at startup.

## 变更
# Changes

中文：
- **`ImageWriteApprovalQueue`**：新增可选的字节根目录与 `ChangeHub`；入队时写 `{id}.bin`，取出 / 拒绝 / 过期时删除。
- **持久化**：`Snapshot()` 只含元数据（不含字节），`Restore()` 按元数据回读磁盘文件；缺文件 / 已过期 / 超限项自动跳过并清理，保留最新。
- **注册**：Web 与桌面组合根都注册 `RegisterImageWriteApprovalPersistence()`（扩展区 `imageWritePending`），字节目录为 `{ContentRoot}/data/pending-writes`。
- **无持久化时**：启动清理超过 24h 的孤儿 `.bin`，避免磁盘泄漏。

English:
- **`ImageWriteApprovalQueue`**: optional bytes root and `ChangeHub`; writes `{id}.bin` on enqueue and deletes it on take / reject / expiry.
- **Persistence**: `Snapshot()` contains metadata only (no bytes), `Restore()` reads the files back by metadata; missing files / expired / over-limit items are skipped and cleaned, keeping the newest.
- **Registration**: both the web and desktop composition roots call `RegisterImageWriteApprovalPersistence()` (section `imageWritePending`), with the bytes directory at `{ContentRoot}/data/pending-writes`.
- **Without persistence**: startup removes orphan `.bin` files older than 24 hours to avoid disk leaks.

## 文档
# Docs

中文：`docs/user-guide.md` / `en.md` §5.4.6（队列跨重启、磁盘布局、持久化关闭时的退化行为）；`ROADMAP.md` / `en.md`；版本号与「适用版本」同步到 1.0.280。
English: `docs/user-guide.md` / `en.md` §5.4.6 (cross-restart queue, disk layout, degraded behaviour without persistence); `ROADMAP.md` / `en.md`; version numbers and "applies to version" bumped to 1.0.280.

## 测试
# Tests

中文：新增 2 例——`ImageWriteApprovalQueueTests`（同目录重启后元数据 + 字节均恢复、取出后字节文件被删；恢复时跳过过期项并删除其字节）。**全量测试通过**（1851 → **1853**）；`dotnet build` **0 错误**；`node --check` 三份前端脚本；i18n 审计 **zh=en=1990，missing 0**；守卫脚本均过。Docker 真实端到端 **6/6**：技能入队（202 + pendingId）→ 重启 web → 待审批项仍在 → 批准后图片落盘（字节从磁盘恢复）。
English: 2 new cases — `ImageWriteApprovalQueueTests` (after a restart on the same directory both metadata and bytes are restored, and taking an item deletes its bytes; restore skips expired items and deletes their bytes). **All tests pass** (1851 → **1853**); `dotnet build` reports **0 errors**; `node --check` passes for all three frontend scripts; the i18n audit is **zh=en=1990, missing 0**; guard scripts pass. Real Docker end-to-end **6/6**: a skill enqueues a write (202 + pendingId) → restart web → the pending item is still there → after approval the image lands (bytes restored from disk).


**版本说明**：**图库写入的二期能力**——① 逐次写入审批：技能写已有/授权库可要求管理员批准（先入队、返回 202 + pendingId，在「图库管理」顶部批准/拒绝）；② 写入来源标记：图片记录来源技能/岗位，图库支持「全部 / 技能写入 / 手动上传」筛选；③ 按技能配额：每轮最多新建 1..10 个库、上传 1..200 张图。新建库仍立即生效（隔离、风险低）。
**Version note**: **Phase 2 of image-library writes** — ① per-write approval: a skill can require admin approval for writes to existing/authorized libraries (queued, 202 + pendingId, approved/rejected at the top of Image Library Management); ② write-source marking: images record the source skill/agent, and the library can filter All / Skill writes / Manual uploads; ③ per-skill quotas: 1..10 libraries and 1..200 images per run. Libraries created in the run still apply immediately (isolated, low risk).

## 变更
# Changes

中文：
- **技能模型**：`ImageWriteRequiresApproval` / `ImageWriteMaxLibraries`（1..10）/ `ImageWriteMaxImages`（1..200）；仅 `ImageWrite` 生效时保留，非管理员更新时一并保留管理员授予值。
- **能力句柄**：`ImageWriteScope` 新增 `RequiresApproval` 与 `CreatedLibIds`（区分「本轮新建」与「预置授权」）；`RegisterWriteScope` 接受配额与审批参数。
- **审批队列 `ImageWriteApprovalQueue`**：内存 + 24h TTL；最多 50 项 / 每人 20 项 / 总字节 ≤ 64MB；记录 owner、skill、agent、lib、文件名/类型/字节/描述/标签。
- **写入端点**：当 `RequiresApproval` 且目标库不是本轮新建时，消耗本轮配额后入队并返回 **202** `{ pendingId, status: "pending" }`；新建库走原立即写入路径。
- **管理员端点**：`GET /ag-ui/admin/image-write-pending`、`POST /{id}/approve`（写盘 + 来源标记 + 审计）、`POST /{id}/reject`（审计 `skill.imageLibrary.rejected`）。
- **来源标记**：`ImageAsset.SourceSkillId` / `SourceAgentId`；技能写入（含审批通过）时填充；图库卡片显示 `🤖 skillId`，工具栏新增来源筛选。
- **审计**：新增 `skill.imageLibrary.pending` / `.rejected`；审批通过仍记 `skill.imageLibrary.upload`（detail 含审批号）。

English:
- **Skill model**: `ImageWriteRequiresApproval` / `ImageWriteMaxLibraries` (1..10) / `ImageWriteMaxImages` (1..200); kept only when `ImageWrite` is effective, and admin-granted values are preserved on non-admin edits.
- **Capability handle**: `ImageWriteScope` gains `RequiresApproval` and `CreatedLibIds` (distinguishing "created in this run" from "pre-authorized"); `RegisterWriteScope` accepts quota and approval parameters.
- **Approval queue `ImageWriteApprovalQueue`**: in-memory with a 24h TTL; at most 50 items / 20 per owner / 64MB total; stores owner, skill, agent, lib, filename/type/bytes/caption/tags.
- **Write endpoint**: when `RequiresApproval` is on and the target library was not created in this run, it consumes the per-run quota, enqueues and returns **202** `{ pendingId, status: "pending" }`; new libraries keep the immediate path.
- **Admin endpoints**: `GET /ag-ui/admin/image-write-pending`, `POST /{id}/approve` (write + source marking + audit), `POST /{id}/reject` (audits `skill.imageLibrary.rejected`).
- **Source marking**: `ImageAsset.SourceSkillId` / `SourceAgentId`, filled for skill writes (including approved ones); the image card shows `🤖 skillId` and the toolbar gains a source filter.
- **Audit**: new `skill.imageLibrary.pending` / `.rejected`; approvals still record `skill.imageLibrary.upload` (detail includes the approval id).

## 文档
# Docs

中文：`docs/user-guide.md` / `en.md` §4.3.4（配额与审批字段）、§5.4.6（审批队列、来源标记、审计）、§8.7（审计 action）、§9.x（管理员待审批端点）；`ROADMAP.md` / `en.md`；版本号与「适用版本」同步到 1.0.279。
English: `docs/user-guide.md` / `en.md` §4.3.4 (quota and approval fields), §5.4.6 (approval queue, source marking, audit), §8.7 (audit actions), §9.x (admin pending endpoints); `ROADMAP.md` / `en.md`; version numbers and "applies to version" bumped to 1.0.279.

## 测试
# Tests

中文：新增 4 例——`ImageWriteApprovalQueueTests` ×3（入队/列表/取出；每人上限；全局限额）、`AgentImageScopeBindingTests` ×1（自定义配额与审批参数、预置库不算本轮新建）。**全量测试通过**（1847 → **1851**）；`dotnet build` **0 错误**；`node --check` 三份前端脚本；i18n 审计 **zh=en=1990，missing 0**；守卫脚本均过。Docker 真实端到端 **13/13**：带审批的技能首次写入返回 202 + pendingId → 管理员列表可见 → 批准后落盘且带 `sourceSkillId` → 审计含 pending/upload → 同图再次写入被去重、不再产生待审批。浏览器校验 **6/6**：来源下拉三档、技能徽标、两种筛选、待审批块与批准/拒绝按钮、无页面脚本错误。
English: 4 new cases — `ImageWriteApprovalQueueTests` ×3 (add/list/take; per-owner cap; global cap), `AgentImageScopeBindingTests` ×1 (custom quotas and approval parameters; a seeded library is not "created in this run"). **All tests pass** (1847 → **1851**); `dotnet build` reports **0 errors**; `node --check` passes for all three frontend scripts; the i18n audit is **zh=en=1990, missing 0**; guard scripts pass. Real Docker end-to-end **13/13**: a skill with approval returns 202 + pendingId on the first write → the admin list shows it → after approval it is written with `sourceSkillId` → the audit contains pending/upload → writing the same image again is deduped and creates no new pending item. Browser checks **6/6**: three source options, the skill badge, both filters, the pending block with approve/reject buttons, and no page script errors.


**版本说明**：**技能写图库从「只能新建」扩到「可写授权库」，并加上内容去重**——① 管理员可在技能上选定可写入的**已有图库**（支持跨归属），非管理员后续编辑不会丢失这项授权；② 上传前按字节 SHA-256 去重，同图直接返回已有记录、不占配额；③ 「写入前人工确认」采用**配置层预审批**（默认关闭 + 逐库授权 + 配额 + 审计），逐次写入 HITL 明确留作二期。
**Version note**: **Skill image writes now cover authorized existing libraries, plus content dedup** — ① an admin can select **existing libraries** a skill may write to (cross-ownership allowed), and later edits by non-admins preserve that grant; ② uploads dedupe by byte-level SHA-256 (a hit returns the existing record without consuming quota); ③ "human confirmation before writing" is enforced as **configuration-level pre-approval** (off by default + per-library grant + quotas + audit), with per-write HITL explicitly deferred to phase 2.

## 变更
# Changes

中文：
- **技能模型**：`AgentSkillDefinition.ImageWriteLibraryIds`（仅 `ImageWrite` 生效时保留）；`SkillDefHttpRequest` / DTO 同步；服务端逐个校验图库存在（否则 400）。非管理员更新技能时保留管理员已授予的 `ImageWrite` 与白名单。
- **能力句柄**：`RegisterWriteScope(..., seedLibraryIds)` 把授权库预置进句柄；`IsWriteLibraryAllowed` 同时接受「本轮新建」与「预置授权」；上传端点去掉「图库归属必须等于触发者」的限制（改由管理员授权白名单控权）。
- **内容去重**：`ImageAsset.ContentHash` + `FindDuplicate(libId, bytes)`；`AddAsset` 写入时记录哈希；旧数据无哈希时首次比对懒补。技能上传命中返回 `{ duplicated: true, asset }`；手动上传命中直接返回已有记录。
- **前端**：技能表单新增「可写入的已有图库」多选清单（`#sfImageWriteLibs`，勾选 `#sfImageWrite` 后显示，随图库列表加载）；保存提交 `imageWriteLibraryIds`；AI 生成结果默认清空。

English:
- **Skill model**: `AgentSkillDefinition.ImageWriteLibraryIds` (kept only when `ImageWrite` is effective); `SkillDefHttpRequest` / DTO updated; the server validates every referenced library exists (otherwise 400). Non-admin updates preserve the admin-granted `ImageWrite` flag and allow-list.
- **Capability handle**: `RegisterWriteScope(..., seedLibraryIds)` seeds authorized libraries into the handle; `IsWriteLibraryAllowed` accepts both "created in this run" and "pre-authorized"; the upload endpoint drops the "library owner must equal trigger user" rule (the admin-granted allow-list is authoritative).
- **Content dedup**: `ImageAsset.ContentHash` + `FindDuplicate(libId, bytes)`; `AddAsset` records the hash; legacy assets without one are lazily backfilled on first comparison. Skill uploads return `{ duplicated: true, asset }` on a hit; manual uploads return the existing record directly.
- **Frontend**: the skill form gains an "existing libraries it may write to" multi-select (`#sfImageWriteLibs`, shown once `#sfImageWrite` is ticked, loaded from the library list); saving submits `imageWriteLibraryIds`; AI-generated results clear the selection by default.

## 文档
# Docs

中文：`docs/user-guide.md` / `en.md` §4.3.4（`#sfImageWriteLibs`）、§5.4.3（上传去重）、§5.4.6（已有图库授权、去重、预审批模型与二期说明）；`ROADMAP.md` / `en.md`；版本号与「适用版本」同步到 1.0.278。
English: `docs/user-guide.md` / `en.md` §4.3.4 (`#sfImageWriteLibs`), §5.4.3 (upload dedup), §5.4.6 (existing-library grants, dedup, the pre-approval model and the phase-2 note); `ROADMAP.md` / `en.md`; version numbers and "applies to version" bumped to 1.0.278.

## 测试
# Tests

中文：新增 2 例——`AgentImageScopeBindingTests`（授权库被预置进写句柄且可跨归属；`AddAsset` 后 `FindDuplicate` 返回同一张图、不同字节返回 null）；扩展 `ClientToolBridgeApiTests`（`imageWriteLibraryIds` 写入 / 回读一致，引用不存在的图库返回 400）。**全量测试通过**（1845 → **1847**）；`dotnet build` **0 错误**；`node --check` 三份前端脚本；i18n 审计 **zh=en=1976，missing 0**；守卫脚本均过。Docker 真实端到端 **10/10**：管理员建图库 + 授权技能 → 技能写入该库 → 两次上传同一张图只保留 1 张（第二次 `duplicated`）→ 审计有上传记录；未授权技能写同一库返回 403。
English: 2 new cases — `AgentImageScopeBindingTests` (authorized libraries are seeded into the write handle and may cross ownership; after `AddAsset`, `FindDuplicate` returns the same image and null for different bytes); extended `ClientToolBridgeApiTests` (`imageWriteLibraryIds` round-trips and a missing library returns 400). **All tests pass** (1845 → **1847**); `dotnet build` reports **0 errors**; `node --check` passes for all three frontend scripts; the i18n audit is **zh=en=1976, missing 0**; guard scripts pass. Real Docker end-to-end **10/10**: an admin creates a library and authorizes a skill → the skill writes into it → uploading the same image twice keeps only one (the second returns `duplicated`) → the audit has the upload record; an unauthorized skill writing the same library returns 403.


**版本说明**：**技能可以受控写入图库**——管理员可在单个技能上开启「图库写入」，平台在技能入参 JSON 中注入 10 分钟能力句柄 `imageWriteScopeId`，技能据此新建图库 / 上传图片（每轮 1 个库 / 20 张图 / 单图 10MB，只能写本轮新建的库，全程审计）。同时修复一个影响全平台审计的潜伏缺陷：审计 ID 用进程内自增序号，重启后与库中已有主键冲突并被静默丢弃。
**Version note**: **Skills can write to image libraries under control** — an admin can enable "image library write" per skill; the platform injects a 10-minute capability handle `imageWriteScopeId` into the skill's JSON input, which the skill uses to create a library / upload images (1 library / 20 images / 10 MB per image per run; only the library created in this run; fully audited). Also fixes a latent platform-wide audit defect: IDs used a process-local counter that collided with existing primary keys after a restart and were silently dropped.

## 变更
# Changes

中文：
- **技能级能力开关**：`AgentSkillDefinition.ImageWrite`（默认 `false`）；仅管理员可开，且仅对**服务端执行的非 prompt** 技能生效（客户端技能拿不到平台自令牌，prompt 无执行面）；服务端对非法组合强制 `false`。技能表单新增 `#sfImageWrite`（仅当时条件满足时可见）。
- **能力句柄**：`ImageLibraryCatalog.RegisterWriteScope` / `ResolveWriteScope`（TTL 10 分钟）、`TryConsumeLibraryCreate` / `TryConsumeImageUpload`（配额）、`AllowWriteLibrary` / `IsWriteLibraryAllowed`（本轮新建库白名单）。
- **注入**：`AgentCatalog.WithImageScope` 在原有 `imageScopeId` 之外，按 `ImageWrite` 注入 `imageWriteScopeId`；读/写可同时注入（如带配图的 Word 技能）。
- **自调用端点**（`SelfApi.IsSelf` + 句柄三重校验）：`POST /ag-ui/images/libraries`（建库，owner = 触发者）、`POST /ag-ui/images/libraries/{libId}/assets`（`attachmentId` 或 `base64`）；超配额返回 429；写入非本轮新建库返回 403。
- **审计**：`skill.imageLibrary.create` / `skill.imageLibrary.upload`（带 skillId + agentId + 触发者）；审计页可按 action 过滤。
- **生成器提示词**：补「平台图库写入能力」段落（端点、请求头、配额、只能写本轮新建库），自动生成的技能知道该能力。
- **审计 ID 修复（重要）**：`AuditEntry.Id` 从 `aud_<自增>` 改为 `aud_<随机 16 hex>`。此前独立表模式下重启后自增从 1 开始，新记录与库中已有 `aud_1/aud_2/…` 撞主键，被 `ON CONFLICT DO NOTHING` 静默忽略——表现为「重启后审计页不再新增记录」。

English:
- **Per-skill capability flag**: `AgentSkillDefinition.ImageWrite` (default `false`); only admins can enable it, and only for **server-side non-prompt** skills (client skills cannot reach the platform self token; `prompt` has no execution surface); illegal combinations are forced to `false` by the server. The skill form gains `#sfImageWrite` (visible only when those conditions hold).
- **Capability handle**: `ImageLibraryCatalog.RegisterWriteScope` / `ResolveWriteScope` (10-minute TTL), `TryConsumeLibraryCreate` / `TryConsumeImageUpload` (quotas), `AllowWriteLibrary` / `IsWriteLibraryAllowed` (allow-list of libraries created in this run).
- **Injection**: `AgentCatalog.WithImageScope` now injects `imageWriteScopeId` when `ImageWrite` is on, alongside the existing `imageScopeId`; read and write can be injected together (e.g. a Word skill with illustrations).
- **Self-call endpoints** (`SelfApi.IsSelf` plus the handle checks): `POST /ag-ui/images/libraries` (create; owner = trigger user), `POST /ag-ui/images/libraries/{libId}/assets` (`attachmentId` or `base64`); over quota returns 429; writing to a library not created in this run returns 403.
- **Audit**: `skill.imageLibrary.create` / `skill.imageLibrary.upload` (with skillId + agentId + trigger user); filterable by action on the audit page.
- **Generator prompt**: adds a "platform image-library write capability" section (endpoints, header, quotas, only the library created in this run), so auto-generated skills know about it.
- **Audit ID fix (important)**: `AuditEntry.Id` changes from `aud_<counter>` to `aud_<random 16 hex>`. In dedicated-table mode the counter previously restarted at 1 after every restart, so new rows collided with existing `aud_1/aud_2/…` primary keys and were silently dropped by `ON CONFLICT DO NOTHING` — observable as "the audit page stops gaining records after a restart".

## 文档
# Docs

中文：`docs/user-guide.md` / `en.md` §4.3.4（技能表单 `#sfImageWrite`）、§5.4.6（技能写入图库：开启、句柄、端点、安全模型、C# 示例）、§8.7（审计 ID 修复）、§9.x（两个自调用端点）；`ROADMAP.md` / `en.md`；版本号与「适用版本」同步到 1.0.277。
English: `docs/user-guide.md` / `en.md` §4.3.4 (`#sfImageWrite` in the skill form), §5.4.6 (skill writes: enabling, handle, endpoints, security model, C# example), §8.7 (audit ID fix), §9.x (the two self-call endpoints); `ROADMAP.md` / `en.md`; version numbers and "applies to version" bumped to 1.0.277.

## 测试
# Tests

中文：新增 7 例——`AgentImageScopeBindingTests` ×5（写能力句柄绑定触发者 / 岗位 / 技能；文档技能读写同时注入；未开启不注入；客户端 / prompt 永不注入；配额与本轮新建库白名单）、`ClientToolBridgeApiTests` ×1（`imageWrite` 只对管理员 + 服务端非 prompt 保留）、`AuditStoreTests` ×1（重启后新实例写审计不再撞 ID）。**全量测试通过**（1838 → **1845**）；`dotnet build` **0 错误**；`node --check` 三份前端脚本；i18n 审计 **zh=en=1974，missing 0**；守卫脚本均过。Docker 真实端到端 **9/9**：管理员创建开启 `imageWrite` 的 dotnet 技能 → 技能试运行经 `imageWriteScopeId` 建库 + 传图 → 图库归属触发者且含图片 → 审计含 `skill.imageLibrary.create` / `.upload` → 无自令牌 / 伪造令牌均 403；测试后已清理。
English: 7 new cases — `AgentImageScopeBindingTests` ×5 (the write handle is bound to the trigger user / agent / skill; document skills get both read and write; disabled skills get neither; client / prompt skills never get write; quota and the created-this-run allow-list), `ClientToolBridgeApiTests` ×1 (`imageWrite` is kept only for admin + server-side non-prompt), `AuditStoreTests` ×1 (a restarted instance no longer collides on audit IDs). **All tests pass** (1838 → **1845**); `dotnet build` reports **0 errors**; `node --check` passes for all three frontend scripts; the i18n audit is **zh=en=1974, missing 0**; guard scripts pass. Real Docker end-to-end **9/9**: an admin creates a dotnet skill with `imageWrite` → a trial run creates a library and uploads an image through `imageWriteScopeId` → the library is owned by the trigger user and contains the image → the audit contains `skill.imageLibrary.create` / `.upload` → no self token / a forged token both return 403; test data was cleaned up.


**版本说明**：**提醒从「能推」到「不重不漏、不接也能看」**——① 提醒去重键**落盘**（扩展区 `quotaReminderState`），重启后不再重复推送同一条 `EXPIRING` / `EXPIRED`；② 新增**站内提醒**：管理员打开「用量统计」页即可看到即将到期 / 已回退，无需接 Webhook；③ 知聚设置新增 `+2 小时 / +1 天 / +3 天 / +7 天 / 清空` 到期模板。
**Version note**: **From "can push" to "exactly once, and visible without an integration"** — ① reminder dedup keys are now **persisted** (extension section `quotaReminderState`), so a restart no longer re-delivers the same `EXPIRING` / `EXPIRED`; ② a new **in-app reminder** shows expiring / reverted allowances on the admin Usage Stats page without any webhook; ③ the group settings dialog gains `+2 hours / +1 day / +3 days / +7 days / Clear` expiry presets.

## 变更
# Changes

中文：
- **`QuotaReminderStateStore`**：两类事件各维护一份键集合（`groupId|untilMs`）；`MarkExpiring` / `MarkExpired` 返回是否首次，`Prune` 清理失效键，`Snapshot` / `Restore` 供持久化。注册到扩展区 `quotaReminderState`（Web 与桌面组合根都注册）；服务在扫描产生变更时 `ChangeHub.Notify()` 驱动落盘。持久化关闭时退化为进程内去重（行为同 1.0.275）。
- **站内提醒接口** `GET /ag-ui/admin/quota-reminders`（Operator+）：返回 `{ leadHours, graceHours, items[] }`，`items` 为「生效中且进入提醒窗口」与「已到期但仍在 24h 宽限期」的覆写，含 `groupId / name / quota / until / state / seconds`；窗口取自配置治理的提前量。
- **用量页站内提醒块**：`#adminQuotaReminders` 在页面顶部显示知聚 / 额度 / 到期时间 / 状态，无数据时隐藏；随 `loadAdminUsage()` 并行加载，不影响主表。
- **到期模板**：`#gsQuotaGroup` 内新增 5 个 chip，只填 `#gsQuotaUntil`（不改额度），用 `msToLocalInput` 保持本地时区一致；`+0 小时` 即清空。

English:
- **`QuotaReminderStateStore`**: one key set per event kind (`groupId|untilMs`); `MarkExpiring` / `MarkExpired` return whether it was the first time, `Prune` drops stale keys, and `Snapshot` / `Restore` back persistence. Registered under extension section `quotaReminderState` (both web and desktop composition roots); the service calls `ChangeHub.Notify()` when a scan changes state. With persistence disabled it degrades to in-process dedup (same as 1.0.275).
- **In-app endpoint** `GET /ag-ui/admin/quota-reminders` (Operator+): returns `{ leadHours, graceHours, items[] }`, where `items` are overrides that are effective and inside the reminder window, or already expired but within the 24-hour grace period, with `groupId / name / quota / until / state / seconds`; the window comes from Config Governance.
- **Usage-page block**: `#adminQuotaReminders` shows circle / quota / expires at / state at the top, hidden when empty; it loads alongside `loadAdminUsage()` without blocking the main tables.
- **Expiry presets**: five chips inside `#gsQuotaGroup` fill `#gsQuotaUntil` only (never the quota) via `msToLocalInput`, keeping the local-timezone round trip; `+0 hours` clears it.

## 文档
# Docs

中文：`docs/user-guide.md` / `en.md` §2.2.2.2（去重持久化 + 站内提醒 + 模板）、§8.6（用量页站内提醒块与接口形状）、§8.19（跨重启去重语义）；`ROADMAP.md` / `en.md`；版本号与「适用版本」同步到 1.0.276。
English: `docs/user-guide.md` / `en.md` §2.2.2.2 (persistent dedup + in-app reminder + presets), §8.6 (the usage-page block and response shape), §8.19 (cross-restart dedup semantics); `ROADMAP.md` / `en.md`; version numbers and "applies to version" bumped to 1.0.276.

## 测试
# Tests

中文：新增 3 例——`QuotaExpiryReminderTests` ×2（同一去重 store 跨两个服务实例模拟重启：`EXPIRING` / `EXPIRED` 均不重复；`QuotaReminderStateStore` 的标记 / 修剪 / 快照恢复）、`AdminApiTests` ×1（`/quota-reminders` 分别返回 `expiring` 与 `expired` 行及正确额度 / 剩余秒）。**全量测试通过**（1835 → **1838**）；`dotnet build` **0 错误**；`node --check` 三份前端脚本；i18n 审计 **zh=en=1972，missing 0**；守卫脚本均过。Docker 重建后真实端到端 **9/9**：首轮重启收到 `EXPIRING` + `EXPIRED`，站内接口返回 `expired` 行；**第二轮重启后 80 秒内无重复投递**（`before=2 after=2`）；用量页显示站内提醒块；`+2 小时` 模板回填误差 < 2 分钟、「清空」生效；无页面脚本错误。
English: 3 new cases — `QuotaExpiryReminderTests` ×2 (sharing one dedup store across two service instances simulates a restart: neither `EXPIRING` nor `EXPIRED` repeats; the store's mark / prune / snapshot-restore behaviour), `AdminApiTests` ×1 (`/quota-reminders` returns an `expiring` and an `expired` row with the right quota / remaining seconds). **All tests pass** (1835 → **1838**); `dotnet build` reports **0 errors**; `node --check` passes for all three frontend scripts; the i18n audit is **zh=en=1972, missing 0**; guard scripts pass. After a Docker rebuild, the real end-to-end run is **9/9**: the first restart delivers `EXPIRING` + `EXPIRED` and the in-app endpoint returns an `expired` row; after a **second restart there are no duplicate deliveries within 80 seconds** (`before=2 after=2`); the usage page shows the reminder block; the `+2 hours` preset lands within 2 minutes and `Clear` works; no page script errors.


**版本说明**：**临时放额提醒的完整闭环**——① 提醒提前量与扫描间隔可在「配置治理」热改（默认 24 小时 / 15 分钟）；② 到期后 **24 小时宽限期**内补发 `QUOTA_OVERRIDE_EXPIRED`，让告警平台确定性看到「已回退全局」；③ 审计页新增**知聚下拉筛选**，可与「⏰ 临时放额」组合直看某知聚历次放额。
**Version note**: **A complete loop for temporary-allowance reminders** — ① the lead time and scan interval are hot-changeable in Config Governance (default 24 hours / 15 minutes); ② within the **24-hour grace period** after expiry a `QUOTA_OVERRIDE_EXPIRED` event confirms the fallback to the global quota; ③ the audit page gains a **circle dropdown** that combines with "⏰ Temporary allowances" to review a circle's allowance history.

## 变更
# Changes

中文：
- **配置治理新增 2 个旋钮**：`quotaReminderLeadHours`（1..720）与 `quotaReminderScanMinutes`（1..1440），对应 `#cfgQuotaReminderLeadHours` / `#cfgQuotaReminderScanMinutes`；未设时回退 24 小时 / 15 分钟。提前量在**每轮扫描时**读取；扫描间隔保存时通过 `Reschedule()` 重排下一轮（最长等一个旧周期），无需重启。
- **新事件 `QUOTA_OVERRIDE_EXPIRED`**：`{ groupId, groupName, quota, until, expiredSeconds, timestamp }`；到期后 24 小时宽限期内只发一次（升级 / 重启不会为历史数据洪水式补发），事件目录 **20 → 21**。
- **提醒服务重构**：每轮扫描返回 `{ Expiring, Expired }` 两组事件；去重表分开维护，额度取消 / 改期 / 知聚解散时一并清理；定时器改为「每轮结束后按当前配置重排」，并加 `Interlocked` 防重入。
- **审计页知聚下拉**：`#auditGroupPick` 首次进入审计页时从 `GET /ag-ui/admin/groups` 加载全部知聚；选中后以 `targetId=<groupId>` 查询（优先于手填目标），切换即重查；CSV 导出同口径。

English:
- **Two new Config Governance knobs**: `quotaReminderLeadHours` (1..720) and `quotaReminderScanMinutes` (1..1440), bound to `#cfgQuotaReminderLeadHours` / `#cfgQuotaReminderScanMinutes`; unset falls back to 24 hours / 15 minutes. The lead time is read **on every scan**; saving the scan interval calls `Reschedule()` so the next run is re-armed (at most one old interval of delay) with no restart.
- **New event `QUOTA_OVERRIDE_EXPIRED`**: `{ groupId, groupName, quota, until, expiredSeconds, timestamp }`; emitted once within the 24-hour grace period after expiry (upgrades / restarts do not flood historical data); catalog **20 → 21**.
- **Reminder service rework**: each scan returns `{ Expiring, Expired }`; the two dedup maps are maintained separately and pruned when the allowance is cleared / re-dated / the circle is disbanded; the timer now re-arms with the current config after each run, with an `Interlocked` re-entrancy guard.
- **Audit circle dropdown**: `#auditGroupPick` loads all circles from `GET /ag-ui/admin/groups` on first entry; selecting one queries by `targetId=<groupId>` (taking precedence over the manual target) and re-queries on change; CSV export uses the same conditions.

## 文档
# Docs

中文：`docs/user-guide.md` / `en.md` §2.2.2.2（两类提醒事件）、§8.7（知聚下拉）、§8.10（可热改键 23 → 25 + 新旋钮说明）、§8.19（事件目录 **21 类** + `QUOTA_OVERRIDE_EXPIRED` 小节）；`ROADMAP.md` / `en.md`；版本号与「适用版本」同步到 1.0.275。
English: `docs/user-guide.md` / `en.md` §2.2.2.2 (both reminder events), §8.7 (circle dropdown), §8.10 (hot keys 23 → 25 + the new knobs), §8.19 (catalog **21 types** + the `QUOTA_OVERRIDE_EXPIRED` subsection); `ROADMAP.md` / `en.md`; version numbers and "applies to version" bumped to 1.0.275.

## 测试
# Tests

中文：新增 5 例——`QuotaExpiryReminderTests` ×4（提前量可配为 48h、到期后宽限期内发一次 `QUOTA_OVERRIDE_EXPIRED`、超过 24h 宽限期的历史数据不补发、取消覆写后两类事件均停止；既有 4 例适配新返回结构）；`WebhookTests` ×1（`QUOTA_OVERRIDE_EXPIRED` 在目录内 + 签名信封与载荷字段）。**全量测试通过**（1830 → **1835**）；`dotnet build` **0 错误**；`node --check` 三份前端脚本；i18n 审计 **zh=en=1958，missing 0**；`check-i18n-keys` / `check-orphan-keys` / `check-config-keys` / `check-docs-links` 均过。Docker 重建后实测：Webhook 事件目录返回 **21 类**；配置治理写入两个新旋钮 → 回读一致（非法值 400）；浏览器自动化：知聚下拉加载、选择后请求带 `targetId`、与临时放额 chip 组合生效、无页面脚本错误。
English: 5 new cases — `QuotaExpiryReminderTests` ×4 (a 48-hour configurable lead, one `QUOTA_OVERRIDE_EXPIRED` within the grace period, no back-fill for history older than 24 hours, both event kinds stop after the allowance is cleared; the existing 4 were adapted to the new result shape); `WebhookTests` ×1 (`QUOTA_OVERRIDE_EXPIRED` is in the catalog + the signed envelope and payload fields). **All tests pass** (1830 → **1835**); `dotnet build` reports **0 errors**; `node --check` passes for all three frontend scripts; the i18n audit is **zh=en=1958, missing 0**; `check-i18n-keys` / `check-orphan-keys` / `check-config-keys` / `check-docs-links` all pass. After a Docker rebuild: the webhook catalog returns **21 types**; the two new governance knobs round-trip (invalid values 400); browser automation confirms the circle dropdown loads, the request carries `targetId`, it combines with the temporary-allowance chip, and there are no page script errors.


**版本说明**：**临时放额的闭合管理**——① 到期前 **24 小时**通过出站 Webhook 推一条 `QUOTA_OVERRIDE_EXPIRING`（同一额度只发一次），避免管理员设了临时放额后忘记续期；② 审计页新增 **「⏰ 临时放额」快捷筛选**，一键只看带到期时间的覆写变更（`/audit` 新增 `detail` 子串过滤，CSV 导出同口径）。
**Version note**: **Closing the loop on temporary allowances** — ① a `QUOTA_OVERRIDE_EXPIRING` event is pushed to outbound webhooks **24 hours before expiry** (once per allowance), so admins no longer forget to renew; ② the audit page gains a **"⏰ Temporary allowances" quick filter** that shows only override changes carrying an expiry (`/audit` gains a `detail` substring filter, with CSV export using the same conditions).

## 变更
# Changes

中文：
- **新事件 `QUOTA_OVERRIDE_EXPIRING`**：载荷 `{ groupId, groupName, quota, until, remainingSeconds, timestamp }`；只经出站 Webhook 推送（不走群广播），事件目录 **19 → 20**。
- **`QuotaExpiryReminderService`**：宿主启动后 **1 分钟**首次扫描、此后每 **15 分钟**一次；只提醒**仍生效**（`IsQuotaOverrideActive`）且 `until ∈ (now, now+24h]` 的覆写。同一额度（`groupId + until`）**进程内只发一次**；额度取消 / 改期 / 到期 / 知聚解散后去重键在下一轮扫描清理（改回同样的到期时间会重新提醒）。Web 与桌面两个组合根都注册并从 `ApplicationStarted` 启动。
- **审计 `detail` 过滤**：`GET /ag-ui/admin/audit` / `/audit.csv` 新增 `detail`（大小写不敏感子串）；`IAuditStore` 与 Postgres / Relational 两套存储同步实现（SQL `detail LIKE`），内存回退模式在 `Matches` 中过滤。
- **审计页快捷筛选**：`#auditExpiringBtn`（开关式 chip，`on` / `aria-pressed`）：点击自动填入 `action=group.quotaOverride` + `detail=有效期至` 并立即查询；再次点击清空。导出 CSV 沿用同一条件。

English:
- **New event `QUOTA_OVERRIDE_EXPIRING`**: payload `{ groupId, groupName, quota, until, remainingSeconds, timestamp }`; delivered **only via outbound webhooks** (not a group broadcast); event catalog **19 → 20**.
- **`QuotaExpiryReminderService`**: first scan **1 minute** after host start, then every **15 minutes**; only overrides that are **still effective** (`IsQuotaOverrideActive`) with `until ∈ (now, now+24h]` are reminded. The same allowance (`groupId + until`) fires **once per process**; when it is cleared / re-dated / expired / the circle is disbanded, the dedup key is pruned on the next scan (setting the same deadline again later reminds again). Registered by both the web and desktop composition roots and started from `ApplicationStarted`.
- **Audit `detail` filter**: `GET /ag-ui/admin/audit` / `/audit.csv` gain `detail` (case-insensitive substring); implemented in `IAuditStore` and both Postgres / Relational stores (SQL `detail LIKE`), with the in-memory fallback filtering in `Matches`.
- **Audit quick filter**: `#auditExpiringBtn` (toggle chip with `on` / `aria-pressed`): clicking fills `action=group.quotaOverride` + `detail=有效期至` and queries immediately; clicking again clears both. The CSV export reuses the same conditions.

## 文档
# Docs

中文：`docs/user-guide.md` / `en.md` §2.2.2.2（到期提醒）、§8.7（快捷筛选 + `detail` 过滤）、§8.19（事件目录 **20 类** + `QUOTA_OVERRIDE_EXPIRING` 小节）、§9.x（审计接口新参数）；`ROADMAP.md` / `en.md`；版本号与「适用版本」同步到 1.0.274。
English: `docs/user-guide.md` / `en.md` §2.2.2.2 (expiry reminder), §8.7 (quick filter + `detail` filtering), §8.19 (event catalog **20 types** + the `QUOTA_OVERRIDE_EXPIRING` subsection), §9.x (the new audit parameter); `ROADMAP.md` / `en.md`; version numbers and "applies to version" bumped to 1.0.274.

## 测试
# Tests

中文：新增 7 例——`QuotaExpiryReminderTests` ×4（窗口内只发一次 / 窗口外不发直到进入 / 长期放额不发 / 改期后对新到期时间重新提醒）；`AuditStoreTests` ×2（SQLite 与内存回退的 `detail` 子串过滤）；`WebhookTests` ×1（`QUOTA_OVERRIDE_EXPIRING` 在目录内 + 签名信封与载荷字段）。**全量测试通过**（1823 → **1830**）；`dotnet build` **0 错误**；`node --check` 三份前端脚本；i18n 审计 **zh=en=1954，missing 0**；`check-i18n-keys` / `check-orphan-keys` / `check-config-keys` / `check-docs-links` 均过。Docker 重建后实测：① Webhook 事件目录返回 **20 类且含新事件**；② 浏览器自动化 **9/9**（快捷筛选激活 / 取消、请求带 `action` + `detail`、真实 `/audit?detail=` 200 且命中行均含标记、无页面脚本错误）；③ **真实端到端**：本地 HTTP 接收器 + 订阅 + 建知聚（1 小时后到期）→ 重启 web → 首次扫描后收到带 HMAC 签名的 `QUOTA_OVERRIDE_EXPIRING`，`groupId` / `groupName` / `quota=4321` / `until` / `remainingSeconds` 全部正确；测试后已清理订阅与知聚。
English: 7 new cases — `QuotaExpiryReminderTests` ×4 (fires once inside the window / does not fire until it enters / long-term overrides do not fire / a re-dated expiry fires for the new deadline); `AuditStoreTests` ×2 (`detail` substring filtering for SQLite and the in-memory fallback); `WebhookTests` ×1 (`QUOTA_OVERRIDE_EXPIRING` is in the catalog + the signed envelope and payload fields). **All tests pass** (1823 → **1830**); `dotnet build` reports **0 errors**; `node --check` passes for all three frontend scripts; the i18n audit is **zh=en=1954, missing 0**; `check-i18n-keys` / `check-orphan-keys` / `check-config-keys` / `check-docs-links` all pass. After a Docker rebuild: ① the webhook event catalog returns **20 types including the new one**; ② browser automation **9/9** (the quick filter activates / clears, requests carry `action` + `detail`, the real `/audit?detail=` returns 200 with every matched row containing the marker, no page script errors); ③ **real end-to-end**: a local HTTP receiver + subscription + a new circle (expiring in 1 hour) → restart web → after the first scan a signed `QUOTA_OVERRIDE_EXPIRING` arrived with correct `groupId` / `groupName` / `quota=4321` / `until` / `remainingSeconds`; the subscription and circle were cleaned up afterwards.


**版本说明**：**知聚配额「临时放额」**——管理员给某个知聚设配额覆写时，可以再给一个**到期时间**；到期后**自动回退全局配额**，不需要人工记得改回来。失效是**惰性**的（读时判定，无后台任务），存储里的旧值仍然保留，管理员能看到当时设了多少、原定何时到期。
**Version note**: **Temporary per-circle quota allowances** — when an admin sets a quota override for a circle, it can now carry an **expiry time**; once it lapses the circle **automatically falls back to the global quota**, so nobody has to remember to undo it. Expiry is **lazy** (evaluated at read time, no background job) and the stored values are kept, so an admin can still see what was set and when it was due.

## 变更
# Changes

中文：
- **模型（`Group`）**：新增 `QuotaOverrideUntil`（存 `Extra["quotaPerGroupUntil"]`，UTC 毫秒；`<= 0` 视为清除）与 `IsQuotaOverrideActive(nowMs)`（有值且无到期或未到期）。`QuotaOverride` 置 `null` / 负数时**同时移除**额度与到期时间两个键（不留孤儿）。
- **Hub（`GroupHub`）**：`UpdateGroupAsync` 的 `updateFields` 支持 `quotaPerGroupUntil`；`GroupQuotaOverride(groupId)` **到期即返回 `null`**（惰性失效）；新增 `GroupQuotaOverrideUntil(groupId)` 返回存储值（不判到期）。`OnGroupUpdated` 侧：`updateFields` 含 `quotaPerGroupUntil` 时也会刷新侧边栏水位角标。
- **接口（`HttpGroupApi`）**：`/update` 在 `updateFields` 含 `quotaPerGroup` **或 `quotaPerGroupUntil`** 时要求平台管理员（否则 403）；群列表投影新增 `quotaOverrideUntil`；审计 detail 在设了到期时间时追加「；有效期至 yyyy-MM-dd HH:mmZ」。
- **管理接口（`AdminApi`）**：`EffectiveQuota` 改为同时返回生效额度与到期时间；`/usage` 的 `groupsToday` 每行新增 `quotaUntil`（**仅当覆写仍生效时**给出，便于提示「临时至 X」）。
- **前端**：知聚设置弹窗新增 `#gsQuotaUntil`（`datetime-local`，仅管理员可见）；新增 `msToLocalInput` / `localInputToMs` 做本地时间 ↔ UTC 毫秒换算；回填 / 保存 / `GROUP_UPDATED` 同步均覆盖到期时间，取消覆写时本地一并清空；用量页 `⚙` 悬浮提示追加「临时放额至 {time}」。
- **语义不变的前提**：`0` 仍是**有效覆写**（本知聚不限），不是「未设」；到期失效**只影响是否生效**，不改写存储。

English:
- **Model (`Group`)**: adds `QuotaOverrideUntil` (stored in `Extra["quotaPerGroupUntil"]`, UTC milliseconds; `<= 0` clears) and `IsQuotaOverrideActive(nowMs)` (has a value and either no expiry or not yet expired). Setting `QuotaOverride` to `null` / a negative value now **removes both** the quota and the expiry key (no orphans).
- **Hub (`GroupHub`)**: `UpdateGroupAsync` accepts `quotaPerGroupUntil` in `updateFields`; `GroupQuotaOverride(groupId)` **returns `null` once expired** (lazy expiry); new `GroupQuotaOverrideUntil(groupId)` returns the stored value regardless of expiry. On the event side, `updateFields` containing `quotaPerGroupUntil` also refreshes the sidebar level badges.
- **API (`HttpGroupApi`)**: `/update` requires a platform admin (otherwise 403) when `updateFields` contains `quotaPerGroup` **or `quotaPerGroupUntil`**; the group-list projection gains `quotaOverrideUntil`; the audit `detail` appends "; effective until yyyy-MM-dd HH:mmZ" when an expiry is set.
- **Admin API (`AdminApi`)**: `EffectiveQuota` now returns both the effective quota and the expiry; each `groupsToday` row in `/usage` gains `quotaUntil` (**only when the override is still effective**) to drive the "temporary until X" hint.
- **Frontend**: the group settings dialog gains `#gsQuotaUntil` (`datetime-local`, admin only); new `msToLocalInput` / `localInputToMs` convert between local time and UTC milliseconds; prefill / save / `GROUP_UPDATED` sync all cover the expiry, and clearing the override also clears it locally; the usage page's `⚙` hover hint appends "temporary until {time}".
- **Semantics unchanged otherwise**: `0` is still a **valid override** (unlimited for that circle), not "unset"; expiry **only affects effectiveness** and never rewrites storage.

## 文档
# Docs

中文：`docs/user-guide.md` / `en.md` §2.2.2.2（临时放额：惰性失效 / 存储保留 / 取消清孤儿 / 时区换算 / 审计）、§8.6（`groupsToday` 新增 `quotaUntil`；到期回退说明）、§8.7（审计 detail 追加有效期）、§9.2.2（`/update` 权限字段扩为 `quotaPerGroup` / `quotaPerGroupUntil`）；版本号与「适用版本」同步到 1.0.273。
English: `docs/user-guide.md` / `en.md` §2.2.2.2 (temporary allowance: lazy expiry / stored values kept / clearing removes orphans / timezone handling / audit), §8.6 (`groupsToday` gains `quotaUntil`; fallback on expiry), §8.7 (audit detail appends the expiry), §9.2.2 (`/update` permission fields extended to `quotaPerGroup` / `quotaPerGroupUntil`); version numbers and "applies to version" bumped to 1.0.273.

## 测试
# Tests

中文：新增 4 例——`GroupHubTests` ×3（`UpdateGroup_QuotaOverrideUntil_ExpiresLazily`：设额度 + 到期 → 生效；推进时钟 → 生效判定回退但存储保留；`UpdateGroup_QuotaOverrideUntil_InThePastIsImmediatelyInactive`：过去时间立即不生效；`UpdateGroup_QuotaOverride_ClearedAlsoClearsUntil`：取消覆写同时清两个键且 `Extra` 无残留）；`AdminApiTests` ×1（`GroupQuotaOverride_WithExpiry_ReturnsUntilThenFallsBackOnExpiry`：群列表回读 `quotaOverrideUntil`、`/usage` 行带 `quotaUntil` 且水位 `over`、改成过去时间后立刻回退全局 / `quotaUntil=null`、存储旧值保留、审计含「有效期至」）。**定向用例 5/5 通过**；`dotnet build` **0 错误**；`node --check` 三份前端脚本；i18n 审计 **zh=en=1953，missing 0**；`check-i18n-keys` / `check-orphan-keys` / `check-config-keys` / `check-docs-links` 均过；`tools/ui-quota-alerts.mjs` 扩展后实跑 **19 通过 / 0 失败 / 1 跳过**（新增到期时间同步、回填可无损回转、取消清空断言）；Docker 重建后真实 API 端到端：设 1234 + 1 小时到期 → 群列表回读一致 → 改到过去 → 生效判定立即回退（`quotaOverride` 仍在）→ 取消覆写两个字段均 `null`。
English: 4 new cases — `GroupHubTests` ×3 (`UpdateGroup_QuotaOverrideUntil_ExpiresLazily`: set quota + expiry → effective; advance the clock → the effective check falls back while storage is kept; `UpdateGroup_QuotaOverrideUntil_InThePastIsImmediatelyInactive`: a past time is immediately inactive; `UpdateGroup_QuotaOverride_ClearedAlsoClearsUntil`: clearing removes both keys with no `Extra` leftovers); `AdminApiTests` ×1 (`GroupQuotaOverride_WithExpiry_ReturnsUntilThenFallsBackOnExpiry`: the group list returns `quotaOverrideUntil`, the `/usage` row carries `quotaUntil` with level `over`, moving the expiry into the past immediately falls back to global with `quotaUntil=null`, stored values remain, and the audit contains "effective until"). **Targeted cases 5/5 pass**; `dotnet build` reports **0 errors**; `node --check` passes for all three frontend scripts; the i18n audit is **zh=en=1953, missing 0**; `check-i18n-keys` / `check-orphan-keys` / `check-config-keys` / `check-docs-links` all pass; after extending it, `tools/ui-quota-alerts.mjs` runs **19 passed / 0 failed / 1 skipped** (new assertions for expiry sync, lossless prefill round-trip, and clearing); after a Docker rebuild, a real-API end-to-end run: set 1234 with a 1-hour expiry → the group list reads back consistently → move it into the past → the effective check immediately falls back (while `quotaOverride` is still stored) → clearing the override nulls both fields.


**版本说明**：**前端严格审核（1.0.265～1.0.270 渲染路径）后的三项修复**——① 修复一个**缓存不同步**缺陷：`GROUP_UPDATED` 只同步了群名 / 头像 / 私密，没同步 `model` / `quotaPerGroup`，导致**另一位管理员（或另一个标签页）改完覆写后，本端再打开设置弹窗会回填旧值**（再保存还可能把新值覆盖回去）。② 给水位值加**白名单**（只接受 `warn` / `over`）。③ 用量页拉取失败时**三张表都清空**（之前只清按日表，另两张会残留旧数据，容易被当成当前数据）。
**Version note**: **Three fixes after a strict frontend review (1.0.265–1.0.270 rendering paths)** — ① a **cache-sync** defect: `GROUP_UPDATED` synced only the name / avatar / private flag, not `model` / `quotaPerGroup`, so **after another admin (or another tab) changed an override, reopening the settings dialog here prefilled the old value** (and saving again could write it back). ② Quota levels are now **whitelisted** (only `warn` / `over`). ③ When the usage fetch fails, all **three tables are cleared** (previously only the daily table was, leaving stale rows that read as current).

## 变更
# Changes

中文：
- **`onGroupUpdated()` 同步覆写字段**：新增 `model` / `quotaPerGroup` 的本地同步（空串 / 非数字 / 负数 → `null`，与服务端「取消覆写」语义一致）；并在 `updateFields` 含 `quotaPerGroup` 时**异步刷新侧边栏水位角标**（覆写变了，快满 / 已满也会变）。
- **水位值白名单**：新增 `quotaLevelOf()`（只返回 `warn` / `over`），用于 `loadQuotaStatus`、`renderGroupList`、`adminUserRow`——避免把服务端返回的任意字符串拼进 `class` 属性（防御性加固，非已存在漏洞）。
- **失败时清空三张表**：`loadAdminUsage()` 在 `!res.ok || !data` 时同时填充按日 / 分知聚 / 分触发者三张表的错误占位，避免旧数据残留。
- **审核确认无误**（未改动）：`saveGroupSettings()` 虽未手写 `Authorization`，但全局 `window.fetch` 包装会自动注入令牌（已验证）；`loadQuotaStatus` 仅 `Operator+` 调用且失败静默；所有插值均经 `escapeHtml` / 固定字面量。

English:
- **`onGroupUpdated()` syncs the override fields**: adds local sync for `model` / `quotaPerGroup` (empty / non-numeric / negative → `null`, matching the server's "clear override" semantics); when `updateFields` contains `quotaPerGroup` it also **refreshes the sidebar level badges asynchronously** (an override change can move a circle in or out of the warning band).
- **Quota level whitelist**: new `quotaLevelOf()` (returns only `warn` / `over`), used by `loadQuotaStatus`, `renderGroupList` and `adminUserRow` — never interpolate an arbitrary server string into a `class` attribute (defence in depth, not a known exploit).
- **Clear all three tables on failure**: `loadAdminUsage()` now fills the error placeholder into the daily / per-circle / per-trigger tables when `!res.ok || !data`, so stale rows cannot linger.
- **Confirmed correct by the review** (unchanged): although `saveGroupSettings()` does not hand-write `Authorization`, the global `window.fetch` wrapper injects the token (verified); `loadQuotaStatus` is Operator+ only and fails silently; every interpolation goes through `escapeHtml` or a fixed literal.

## 文档
# Docs

无需改文档（无用户可见的文案 / 接口 / 配置变更）。
No doc changes (no user-visible copy, API or configuration change).

## 测试
# Tests

中文：无新增 .NET 用例（本轮**无 C# 变更**）；新增临时 **Playwright 真浏览器**验证 **9/9** 并已删除：`GROUP_UPDATED`（`updateFields` + `groupInfo`）→ 本地 `quotaOverride` / `modelOverride` 同步、重开设置弹窗回填新值、空串→ `null`；注入非法 `level` 不进入 `state` 且页面无注入属性；合法 `warn` 正常收录；无页面脚本错误。`dotnet build` **0 错误**；`node --check` 通过；i18n 审计 **zh=en=1951，missing 0**；守卫脚本均过。
English: no new .NET cases (this round has **no C# changes**); a temporary **real-browser Playwright** check passed **9/9** and was deleted: a `GROUP_UPDATED` (`updateFields` + `groupInfo`) syncs `quotaOverride` / `modelOverride`, reopening the dialog prefills the new values, an empty string becomes `null`; an injected illegal `level` stays out of `state` and leaves no injected attribute; a valid `warn` is recorded; no page script errors. `dotnet build` reports **0 errors**; `node --check` passes; the i18n audit is **zh=en=1951, missing 0**; the guard scripts pass.

# AG-UI Group Chat Desktop 1.0.271 Release Notes (current Windows desktop release)

**版本说明**：**严格代码审核后的两项修复**（覆盖 1.0.264～1.0.270 的配额链路）——① 修复一个**静默失效**类缺陷：`Group.Extra` 是 `init` 且可为 `null`，当旧数据库行 / 导入包的 `extra` 缺省时，`QuotaOverride` / `ModelOverride` / `Kind` 的 setter 会**直接 return**，接口返回 200 但实际没改；现改为**惰性非空属性**。② 配额阈值检查从「用量记录」的 `try/catch` 里拆出，避免它的异常被误报成「用量记录失败」。
**Version note**: **Two fixes after a strict code review** (covering the 1.0.264–1.0.270 quota chain) — ① a **silent-failure** class defect: `Group.Extra` was `init` and nullable, so when an old DB row or import pack had no `extra`, the `QuotaOverride` / `ModelOverride` / `Kind` setters **returned early** and the API still answered 200 without changing anything; it is now a **lazily non-null** property. ② The quota threshold check is lifted out of the usage-recording `try/catch`, so its exceptions are no longer misreported as "usage recording failed".

## 变更
# Changes

中文：
- **`Group.Extra` 改为惰性非空**：`get => _extra ??= new(StringComparer.Ordinal)`、`set => _extra = value ?? new(...)`。带来的效果：旧数据 / 导入包缺省 `extra` 的群，`Kind` / `ModelOverride` / `QuotaOverride` 现在能正常写入（之前是**静默 no-op**）；同时 `Extra = null` 也会被归一化为空字典，不会再次回到静默失败。序列化上无迁移代价（群存储本就透传 extra JSON，现在只会多一个 `"extra": {}`）。
- **阈值检查独立 catch**：`RecordUsage` 先写两张聚合表（失败则记警告并 `return`，不再对不可信的读数做阈值判定），再单独 try 调用 `CheckThresholds`，异常记 `Debug`——诊断日志不再混淆两件事。
- **审核确认无误的点**（未改动，仅记录）：阈值去重键 `日期|作用域|ID` 与跨日清理、`system` 个人豁免、知聚分母走 `EffectiveGroupQuota`（覆写优先）、未配配额 / 已 over 时零额外查询、个人作用域传空 `groupId` 避免错进限定知聚的订阅、Webhook 侧复用既有签名 / 重试 / 投递日志。

English:
- **`Group.Extra` is now lazily non-null**: `get => _extra ??= new(StringComparer.Ordinal)`, `set => _extra = value ?? new(...)`. Effect: groups whose `extra` was absent (old DB rows / import packs) can now write `Kind` / `ModelOverride` / `QuotaOverride` normally (it used to be a **silent no-op**); assigning `Extra = null` is also normalised to an empty dictionary, so it cannot regress into silent failure. No migration cost: the stores already pass extra JSON through, and serialization now just includes `"extra": {}`.
- **Separate catch for the threshold check**: `RecordUsage` first writes both aggregate tables (on failure it logs a warning and `return`s, so no threshold decision is made on untrustworthy readings), then calls `CheckThresholds` in its own try, logging exceptions at `Debug` — the diagnostics no longer conflate the two.
- **Points confirmed correct by the review** (unchanged, noted only): the dedup key `date|scope|id` with cross-day clearing, the `system` per-user exemption, the circle denominator going through `EffectiveGroupQuota` (override first), zero extra queries when no quota is set or the scope is already over, the empty `groupId` for the user scope keeping group-scoped subscriptions out, and the webhook side reusing the existing signing / retry / delivery log.

## 文档
# Docs

中文：`docs/user-guide.md` / `en.md` §2.2.2.2（补一句「旧数据 / 导入包缺省 `extra` 也能写入」）。
English: `docs/user-guide.md` / `en.md` §2.2.2.2 (adds that legacy / imported groups without `extra` can still store the override).

## 测试
# Tests

中文：新增 `GroupHubTests.Extra_IsLazy_LegacyGroupsCanStillStoreOverrides`（未预置 `Extra` 的群 → `QuotaOverride` / `ModelOverride` / `Kind` 均能写入；`Extra = null` 归一化为空字典后仍可写）。**全量测试通过**（1817 → 1818）；`dotnet build` **0 错误**；i18n 审计 **zh=en=1951，missing 0**，守卫脚本均过。
English: new `GroupHubTests.Extra_IsLazy_LegacyGroupsCanStillStoreOverrides` (a group without a pre-set `Extra` can write `QuotaOverride` / `ModelOverride` / `Kind`; after `Extra = null` is normalised to an empty dictionary, writes still work). **All tests pass** (1817 → 1818); `dotnet build` reports **0 errors**; the i18n audit is **zh=en=1951, missing 0**, and the guard scripts pass.

# AG-UI Group Chat Desktop 1.0.270 Release Notes (current Windows desktop release)

**版本说明**：**配额预警从「拉」变成「推」**——当日用量**首次**跨过配额的 80%（`warn`）/ 100%（`over`）时，除既有的页面角标外，现在还会发一条 **`QUOTA_THRESHOLD`** 事件到**出站 Webhook**（照旧带 HMAC 签名 / 重试 / 投递记录），可直接推到 Slack / 告警平台 / n8n。个人与知聚两个作用域均支持，**每作用域每级每日只发一次**（不刷屏；去重状态是**进程内**的，多副本部署时每个副本各发一次——告警平台可按 `scopeId + level + date` 去重）。
**Version note**: **Quota warnings go from "pull" to "push"** — when daily usage **first** crosses 80% (`warn`) or 100% (`over`) of the quota, besides the existing page badges it now emits a **`QUOTA_THRESHOLD`** event to **outbound webhooks** (still HMAC-signed, with retries and delivery records), so it can go straight to Slack / an alerting platform / n8n. Both the user and circle scopes are supported, with **one event per scope per level per day** (no flooding; the dedup state is **per process**, so in a multi-replica deployment each replica emits once — an alerting platform can dedupe by `scopeId + level + date`).

## 变更
# Changes

中文：
- **阈值检测（`AgentUsageService`）**：记录用量后检查两个作用域，**只在水平上升时**回调（`ok → warn → over`），状态键为 `日期|作用域|ID`，跨 UTC 日自动清空。
  - **成本控制**：未配配额（0）或已报过 `over` 的作用域**直接跳过**，不产生 store 查询——默认配置下零额外开销。
  - **分母用生效配额**：知聚维度走 `EffectiveGroupQuota`（**知聚级覆写优先**）；为此 `AgentUsageService` 新增可选的 `Func<string,long?>` 覆写解析器（由 `AgentHosting` 注入 `GroupHub.GroupQuotaOverride`），`CheckGroupQuota` 也随之统一到这一个解析点。
  - **`system` 豁免个人告警**（但仍计入知聚，可触发知聚告警）。
- **事件与桥接**：新增 `EventTypes.QuotaThreshold` 与 `QuotaThresholdEvent`（`scope` / `scopeId` / `level` / `used` / `quota` / `date` / `timestamp`）；新增 `QuotaAlertBridge.WireQuotaAlerts()` 把回调接到 `WebhookDispatcher`，**Web 与桌面组合根均调用**（行为一致）；无 dispatcher 的精简宿主静默跳过。
- **事件目录 18 → 19**：`QUOTA_THRESHOLD` 可选；**个人作用域 groupId 为空串**，因此限定了知聚的订阅收不到个人告警（避免错位）。

English:
- **Threshold detection (`AgentUsageService`)**: after recording usage it checks both scopes and calls back **only when the level rises** (`ok → warn → over`), with state keyed by `date|scope|id` and cleared on a new UTC day.
  - **Cost control**: scopes with no quota (0) or already reported `over` are **skipped outright**, so no store queries happen — zero overhead under the default configuration.
  - **The denominator is the effective quota**: the circle dimension goes through `EffectiveGroupQuota` (**per-circle override first**); to that end `AgentUsageService` gains an optional `Func<string,long?>` override resolver (injected by `AgentHosting` as `GroupHub.GroupQuotaOverride`), and `CheckGroupQuota` now shares that single resolution point.
  - **`system` is exempt from per-user alerts** (but still counts toward the circle, so a circle alert can fire).
- **Event and bridge**: new `EventTypes.QuotaThreshold` and `QuotaThresholdEvent` (`scope` / `scopeId` / `level` / `used` / `quota` / `date` / `timestamp`); new `QuotaAlertBridge.WireQuotaAlerts()` connects the callback to `WebhookDispatcher`, called by **both the web and desktop composition roots** (consistent behaviour); a minimal host without a dispatcher skips silently.
- **Event catalog 18 → 19**: `QUOTA_THRESHOLD` is selectable; the **user scope passes an empty groupId**, so group-scoped subscriptions do not receive per-user alerts (avoiding misrouting).

## 文档
# Docs

中文：`docs/user-guide.md` / `en.md` §8.19（事件目录 **18 → 19**，新增 `QUOTA_THRESHOLD` 小节：载荷、不刷屏 / 生效分母 / system 豁免 / 订阅过滤 / 未配配额零开销）、§8.6（在配额水位说明里补「推送型预警」）。
English: `docs/user-guide.md` / `en.md` §8.19 (event catalog **18 → 19** with a new `QUOTA_THRESHOLD` subsection: payload, no-flooding / effective denominator / system exemption / subscription filtering / zero overhead with no quota), §8.6 (the quota-level notes gain the "push-style warning").

## 测试
# Tests

中文：新增 8 例——`AgentUsageServiceTests` ×4（一次 warn 再一次 over 且不重复、`system` 豁免个人但触发知聚、未配配额零事件、知聚覆写生效）；`WebhookTests` ×2（`QUOTA_THRESHOLD` 在目录内 + 个人作用域跳过限定知聚的订阅 + 载荷/签名投递）；`QuotaAlertBridgeTests` ×2（**真实桥接端到端**：设置订阅 → `RecordUsage` 跨阈值 → 后台工作线程投递并在 body 里看到 `QUOTA_THRESHOLD` / `warn` / `user` / `u1`；无 dispatcher 时保持 `OnQuotaThreshold = null`）。**全量测试通过**（1809 → 1817）；`dotnet build` **0 错误**；`node --check` 三份前端脚本；i18n 审计 **zh=en=1951，missing 0**（含重复键守卫），4 个守卫脚本均过；Docker 重建后实测 `/ag-ui/admin/webhooks/events` 返回 **19 类且含 `QUOTA_THRESHOLD`**，`api-smoke` 46/46。
English: 8 new cases — `AgentUsageServiceTests` ×4 (one warn then one over without repeats, `system` exempt from the user scope but still alerting the circle, no events when no quota is configured, the circle override taking effect); `WebhookTests` ×2 (`QUOTA_THRESHOLD` is in the catalog + the user scope skips group-scoped subscriptions + payload/signature delivery); `QuotaAlertBridgeTests` ×2 (**real bridge end-to-end**: subscription → `RecordUsage` crossing the threshold → the background worker delivers, and the body contains `QUOTA_THRESHOLD` / `warn` / `user` / `u1`; without a dispatcher `OnQuotaThreshold` stays `null`). **All tests pass** (1809 → 1817); `dotnet build` reports **0 errors**; `node --check` passes for all three frontend scripts; the i18n audit is **zh=en=1951, missing 0** (with the duplicate-key guard), and all 4 guard scripts pass; after a Docker rebuild, `/ag-ui/admin/webhooks/events` returns **19 types including `QUOTA_THRESHOLD`**, and `api-smoke` is 46/46.

# AG-UI Group Chat Desktop 1.0.269 Release Notes (current Windows desktop release)

**版本说明**：**配额水位预警补齐最后一个可见面**——用户管理列表现在也在用户名后显示个人配额角标（`⚠` 接近 / `🔴` 已满），悬停给出「今日已用 X / Y token」。至此同一个水位口径在**三处**可见：侧边栏知聚项（按知聚配额）、用量页分知聚 / 分触发者明细、用户管理列表（按个人配额）——管理员无论从哪个入口进来，都能一眼看到谁 / 哪个知聚快到顶。
**Version note**: **The quota-level warning now covers its last visible surface** — the User Management list also shows a per-user quota badge after the username (`⚠` approaching / `🔴` reached), with a tooltip giving "used X / Y tokens today". The same level source is now visible in **three places**: the sidebar group items (per-circle quota), the usage page's per-circle / per-trigger breakdowns, and the User Management list (per-user quota) — so whichever entry point an admin uses, they can see at a glance who or which circle is about to hit its cap.

## 变更
# Changes

中文：
- **`GET /ag-ui/admin/users` 新增 3 字段**：`todayTokens`（今日 token）、`quota`（个人配额，即 `Agents:DailyTokenQuotaPerUser`）、`quotaLevel`（`ok` / `warn` / `over`，由 `AgentUsageService.QuotaLevel` 判定）。实现上先一次 `GetUserTodaySummary()` 建字典，再逐用户查——**不会 N+1 查询**（与用量页 / 侧边栏同源）。
- **用户管理列表角标**：`adminUserRow()` 在用户名后追加 `.quota-tag`：`warn` → `⚠`（琥珀）、`over` → `🔴`（红）、`ok` 不显示；悬停提示 `admin.userQuotaWarn` / `admin.userQuotaOver`（含今日用量与配额数字）。
- **样式复用**：新增 `.admin-table .quota-tag` 系列规则，与侧边栏 `.group-item .quota-tag` 共用同一套语义（琥珀 / 红）。

English:
- **`GET /ag-ui/admin/users` gains 3 fields**: `todayTokens`, `quota` (the per-user quota, i.e. `Agents:DailyTokenQuotaPerUser`) and `quotaLevel` (`ok` / `warn` / `over`, decided by `AgentUsageService.QuotaLevel`). It builds one dictionary from `GetUserTodaySummary()` and then looks each user up — **no N+1 queries** (same source as the usage page / sidebar).
- **User Management list badge**: `adminUserRow()` appends `.quota-tag` after the username: `warn` → `⚠` (amber), `over` → `🔴` (red), `ok` is hidden; the tooltip is `admin.userQuotaWarn` / `admin.userQuotaOver` (including today's usage and the quota).
- **Style reuse**: new `.admin-table .quota-tag` rules sharing the same semantics (amber / red) as the sidebar's `.group-item .quota-tag`.

## 文档
# Docs

中文：`docs/user-guide.md` / `en.md` §8.3.1（用户列表字段与角标说明）、§8.2（tab 接口列表）、§9.2.8（`/ag-ui/admin/users` 端点描述）。
English: `docs/user-guide.md` / `en.md` §8.3.1 (user-list fields and the badge), §8.2 (the tab's endpoint list), §9.2.8 (the `/ag-ui/admin/users` endpoint description).

## 测试
# Tests

中文：新增 `AdminApiIntegrationTests.AdminUsers_IncludesTodayQuotaLevel`（设个人配额 50 → 该用户再记 60 tokens，断言 `todayTokens` **增量**正确、`quota=50`、`quotaLevel=over`；结束还原配额；用增量避免共享 store 的顺序依赖）。**全量测试通过**（1808 → 1809）；`dotnet build` **0 错误**；`node --check` 三份前端脚本；i18n 审计 **zh=en=1951，missing 0**（含重复键守卫），4 个守卫脚本均过；Docker 重建后**真实浏览器**端到端（Playwright）**6/6**：真实接口返回数组且每行含三个新字段；受控数据下 warn / over / ok 三行渲染正确（1 个 ⚠ + 1 个 🔴，ok 无标），悬停提示含「850 / 1,000」与「1,500 / 1,000」数字，无页面脚本错误。
English: new `AdminApiIntegrationTests.AdminUsers_IncludesTodayQuotaLevel` (quota 50 → record 60 more tokens for that user, then assert `todayTokens` **delta** is correct, `quota=50` and `quotaLevel=over`; quota restored afterwards; using a delta avoids depending on the shared store's execution order). **All tests pass** (1808 → 1809); `dotnet build` reports **0 errors**; `node --check` passes for all three frontend scripts; the i18n audit is **zh=en=1951, missing 0** (including the duplicate-key guard), and all 4 guard scripts pass; after a Docker rebuild, a **real-browser** end-to-end (Playwright) **6/6**: the real endpoint returns an array with the three new fields on every row; with controlled data the warn / over / ok rows render correctly (one ⚠ plus one 🔴, none on ok), the tooltips contain "850 / 1,000" and "1,500 / 1,000", and there are no page script errors.

# AG-UI Group Chat Desktop 1.0.268 Release Notes (current Windows desktop release)

**版本说明**：**把配额水位预警补齐到「人」这一维度**——用量统计页新增「今日各触发者用量」明细（按个人配额算水位，`⚠` / `🔴` + meta 汇总），并把**知聚 / 模型覆写变更写进审计日志**（成本控制类配置谁来改、改成什么，现在有迹可查）。同时修掉 3 处**重复 i18n 键**（后者静默覆盖前者），其中一处导致「全局搜索」弹窗标题实际显示成「搜索消息 · 」；并给 CI 加上**重复键守卫**，以后不会再悄悄发生。
**Version note**: **Completing the quota-level warning for the "person" dimension** — the Usage Stats page gains a "today's usage per trigger" breakdown (judged against the per-user quota, with `⚠` / `🔴` and a meta summary), and **circle / model override changes now go into the audit log** (who changed a cost-control setting and to what is now traceable). Also fixes 3 **duplicate i18n keys** (the later silently overrode the earlier), one of which made the "Global Search" dialog title actually read "Search Messages · "; and adds a **duplicate-key guard** to CI so it cannot happen silently again.

## 变更
# Changes

中文：
- **用量页新增「今日各触发者用量」**（`GET /ag-ui/admin/usage` 出参新增 `usersToday[]`）：由 `AgentUsageService.GetUserTodaySummary()` 从按日聚合表一次聚合（按 token 降序取前 50），名字取 昵称→用户名→userId。每行带 `quota`（个人配额）与 `level`（同一套 `QuotaLevel`），配额使用列按水位配色（琥珀 / 红）；meta 追加「个人接近上限 N 人 ｜ 已超限 M 人」。
- **`system` 标注为「豁免」**：定时任务以虚拟触发者 `system` 发起，**刻意不受个人配额约束**。它仍会出现在表里（便于看到系统任务消耗），但 `exempt=true`、`level` 恒 `ok`，配额列显示「系统任务（豁免）」而非「已超限」——避免把豁免误读为超限。
- **知聚 / 模型覆写变更加审计**（`HttpGroupApi` `/update`）：`updateFields` 含 `model` 记 `group.modelOverride`，含 `quotaPerGroup` 记 `group.quotaOverride`，detail 为**改后的实际值**（含「（取消）」）。成本 / 容量控制类变更从此可在「审计日志」按 action 检索 / 导出。
- **修复 3 处重复 i18n 键**（`tools/i18n-audit.mjs` 新守卫抓出）：`search.title`（前者是老键，导致**全局搜索弹窗标题错误显示为「🔍 搜索消息 · 」**）、`admin.thDetail`（zh 值相同，en 为 `Details` / `Detail`）、`imgLib.newTitle`（en 两条相邻重复）。保留语义正确的一条，删掉残余。
- **新增 CI 守卫**：`check-i18n-keys.js` 与 `tools/i18n-audit.mjs` 均增加**行首 `"key":` 计数**的重复键检测（必须在 `eval` / `Map` 载入**之前**做，因为 JS 对象字面量会静默覆盖）。

English:
- **Usage page gains "today's usage per trigger"** (the `GET /ag-ui/admin/usage` response adds `usersToday[]`): aggregated in one pass by `AgentUsageService.GetUserTodaySummary()` from the daily aggregate table (top 50 by tokens descending), with names from nickname → username → userId. Each row carries `quota` (per-user) and `level` (the same `QuotaLevel`), and the quota-use column is colour-coded by level (amber / red); the meta line appends "{near} users approaching limit ｜ {over} over limit".
- **`system` is marked "exempt"**: scheduled tasks run as the virtual trigger `system`, which **deliberately bypasses the per-user quota**. It still appears in the table (so system-task consumption is visible), but with `exempt=true` and `level` always `ok`, and the quota column reads "System task (exempt)" instead of "over" — so an exemption is not misread as a breach.
- **Circle / model override changes are audited** (`HttpGroupApi` `/update`): `model` in `updateFields` records `group.modelOverride`, `quotaPerGroup` records `group.quotaOverride`, with the **resulting value** in `detail` (including "(cleared)"). Cost / capacity-control changes are now searchable / exportable by action in the Audit Log.
- **3 duplicate i18n keys fixed** (caught by the new guard in `tools/i18n-audit.mjs`): `search.title` (the earlier stale key made the **Global Search dialog title render as "🔍 Search Messages · "**), `admin.thDetail` (same zh value; en had `Details` / `Detail`), and `imgLib.newTitle` (two adjacent en entries). Kept the semantically correct one and removed the residue.
- **New CI guard**: both `check-i18n-keys.js` and `tools/i18n-audit.mjs` now detect duplicate keys by counting line-leading `"key":` occurrences — necessarily **before** `eval` / `Map` loading, because a JS object literal silently overwrites.

## 文档
# Docs

中文：`docs/user-guide.md` / `en.md` §8.6（`/usage` 出参新增 `usersToday[]` 与 `exempt` 语义 + 4 列表格）、§8.7（审计 action 清单新增 `group.modelOverride` / `group.quotaOverride`）、§2.2.2.1 / §2.2.2.2（覆写变更审计）、§9.2.8（`/usage` 描述）。
English: `docs/user-guide.md` / `en.md` §8.6 (the `/usage` response gains `usersToday[]` with the `exempt` semantics and the 4-column table), §8.7 (the audit action list gains `group.modelOverride` / `group.quotaOverride`), §2.2.2.1 / §2.2.2.2 (override change auditing), §9.2.8 (the `/usage` description).

## 测试
# Tests

中文：新增 2 例并扩展现有 1 例——`AgentUsageServiceTests.GetUserTodaySummary_GroupsByUser_SortedDesc`（跨智能体合并 / 降序 / 包含 `system`）；`AdminApiIntegrationTests.AdminUsage_UsersToday_ReportsLevelsAndExemptsSystem`（设个人配额 50 → 60 tokens 的触发者 `level=over`、无账号时名字回退 userId；`system` 用 200 仍 `exempt=true` 且 `level=ok`；结束还原配额）；扩展 `GroupQuotaOverride_...` 断言审计含 `group.quotaOverride` + 该知聚 `targetId`。**全量测试通过**（1806 → 1808）；`dotnet build` **0 错误**；`node --check` 三份前端脚本；i18n 审计 **zh=en=1949，missing 0**（新增重复键守卫后仍通过），`check-i18n-keys` / `check-orphan-keys` / `check-config-keys` / `check-docs-links` 均过；守卫**反向自测**（注入一条重复键 → 能检出）；Docker 重建后**真实浏览器**端到端（Playwright）**8/8**：`/usage` 出参含 `usersToday` 数组、表头为「触发者」、受控数据下 over/warn/exempt 三类行与配色正确、meta 为「个人接近上限 1 人 ｜ 已超限 1 人」、无页面脚本错误。
English: 2 new cases plus one extended — `AgentUsageServiceTests.GetUserTodaySummary_GroupsByUser_SortedDesc` (cross-agent merge / descending / includes `system`); `AdminApiIntegrationTests.AdminUsage_UsersToday_ReportsLevelsAndExemptsSystem` (quota 50 → a trigger with 60 tokens is `level=over`; the name falls back to userId when there is no account; `system` with 200 tokens is still `exempt=true` with `level=ok`; quota restored afterwards); extended `GroupQuotaOverride_...` to assert the audit contains `group.quotaOverride` with that circle's `targetId`. **All tests pass** (1806 → 1808); `dotnet build` reports **0 errors**; `node --check` passes for all three frontend scripts; the i18n audit is **zh=en=1949, missing 0** (and still passes with the new duplicate-key guard), and `check-i18n-keys` / `check-orphan-keys` / `check-config-keys` / `check-docs-links` all pass; the guard was **reverse-tested** (injecting a duplicate key is detected); after a Docker rebuild, a **real-browser** end-to-end (Playwright) **8/8**: `/usage` returns a `usersToday` array, the header is "Trigger", the over / warn / exempt rows and their colours render correctly with controlled data, the meta reads "1 user approaching limit ｜ 1 over limit", and there are no page script errors.

# AG-UI Group Chat Desktop 1.0.267 Release Notes (current Windows desktop release)

**版本说明**：**知聚级配额覆写**——全局配额 `Agents:DailyTokenQuotaPerGroup` 是**一刀切**，但知聚之间开销差异极大（客服知聚接待顾客 vs 内部小群）。现在管理员可在「知聚设置」给**单个知聚**单独指定每日 Token 额度（含 `0` = 本知聚不限），**不必抬高全平台上限**。水位预警相应变成**每知聚各用自己的分母**。
**Version note**: **Per-circle quota override** — the global `Agents:DailyTokenQuotaPerGroup` is **one-size-fits-all**, but circles differ hugely in cost (a support circle serving customers vs a small internal circle). An admin can now give **a single circle** its own daily token allowance in the group settings (including `0` = unlimited for that circle), **without raising the platform-wide cap**. The level warning now judges **each circle against its own quota**.

## 变更
# Changes

中文：
- **存储（零迁移）**：`Group.QuotaOverride`（`long?`）存于 `Extra["quotaPerGroup"]`（同 `ModelOverride` / `Kind`），既有所有群存储透传 extra JSON，无需新增列；`null` = 用全局，`0` = 本知聚不限。
- **更新与权限**：`GroupHub.UpdateGroupAsync` 支持 `updateFields=["quotaPerGroup"]`（缺省 / 空串 / 负数 / 非数字 = 取消覆写）；`HttpGroupApi` 的 `/update` 在含 `quotaPerGroup` 时要求生效角色 ≥ `PlatformRole.Admin`，否则 **403**（与模型覆写同口径）。
- **生效**：`AgentGateway` 取 `GroupHub.GroupQuotaOverride(groupId)` 后传入 `CheckGroupQuota(groupId, override)`；新增单一口径 `AgentUsageService.EffectiveGroupQuota(groupId, override)`（覆写优先，否则全局）——校验与展示共用，避免两处各写一遍回退逻辑。
- **用量页**：`groupsToday[]` 每行新增 `quota`（**本知聚生效配额**）；配额使用列改用该行自己的分母；与全局值不同时在知聚名后标 `⚙`（悬停显示具体额度与「优先于全局值」）。
- **群设置弹窗**：新增 `#gsQuotaGroup` / `#gsQuotaOverride`（**仅平台管理员可见**，与模型覆写并列）；群列表接口下发 `quotaOverride` 供回显；保存后异步刷新侧边栏水位角标。
- **顺带修复**：`AdminApiIntegrationTests.AdminUsage_ReturnsDailySummary` 原先断言当日**绝对** token 总数（210），而用量 store 是测试夹具共享单例——其它用例写入后断言依赖执行顺序（隐藏很久的脆弱性，本次被新用例暴露）；已改为断言**增量**。

English:
- **Storage (zero migration)**: `Group.QuotaOverride` (`long?`) is kept in `Extra["quotaPerGroup"]` (like `ModelOverride` / `Kind`); every group store passes extra JSON through, so no new column is needed; `null` = use the global value, `0` = unlimited for that circle.
- **Update & permission**: `GroupHub.UpdateGroupAsync` accepts `updateFields=["quotaPerGroup"]` (absent / empty / negative / non-numeric = clear the override); `HttpGroupApi`'s `/update` requires an effective role ≥ `PlatformRole.Admin` when `quotaPerGroup` is present, otherwise **403** (same rule as the model override).
- **Effect**: `AgentGateway` reads `GroupHub.GroupQuotaOverride(groupId)` and passes it to `CheckGroupQuota(groupId, override)`; a new single source `AgentUsageService.EffectiveGroupQuota(groupId, override)` (override first, else global) is shared by enforcement and display, so the fallback logic is written once.
- **Usage page**: each `groupsToday[]` row gains `quota` (**that circle's effective quota**); the quota-use column uses the row's own denominator; when it differs from the global value a `⚙` marker follows the circle name (hovering shows the value and "takes precedence over the global value").
- **Group settings dialog**: gains `#gsQuotaGroup` / `#gsQuotaOverride` (**platform admins only**, alongside the model override); the group-list endpoint returns `quotaOverride` for rendering; saving asynchronously refreshes the sidebar level badges.
- **Incidental fix**: `AdminApiIntegrationTests.AdminUsage_ReturnsDailySummary` asserted an **absolute** daily token total (210), but the usage store is a fixture-wide singleton — once other tests wrote to it, the assertion depended on execution order (a long-hidden fragility that the new cases exposed); it now asserts a **delta**.

## 文档
# Docs

中文：`docs/user-guide.md` / `en.md` §2.2.2（字段表新增 `#gsQuotaOverride`）、新增 §2.2.2.2（知聚级配额覆写）、§8.6（`groupsToday` 新增 `quota` 字段、每行自己的分母与 `⚙` 覆写标）、§9.2.2（`/ag-ui/group/update` 权限补 `quotaPerGroup`）、§9.2.8（`/quota-status` 补「每知聚自己的分母」）。
English: `docs/user-guide.md` / `en.md` §2.2.2 (the field table gains `#gsQuotaOverride`), a new §2.2.2.2 (per-circle quota override), §8.6 (the `quota` field on `groupsToday`, per-row denominator and the `⚙` override marker), §9.2.2 (`/ag-ui/group/update` permission note for `quotaPerGroup`), §9.2.8 (`/quota-status` gains "each circle's own denominator").

## 测试
# Tests

中文：新增 3 例——`GroupHubTests.UpdateGroup_QuotaOverride_AppliedAndCleared`（JSON number / 纯数字字符串两种形态；`0` 是有效覆写；负数 / 非数字 = 清除）；`AgentUsageServiceTests.GroupQuota_OverrideWinsAndZeroMeansUnlimitedForThatGroup`（覆写放行 / 覆写 0 不限 / 覆写更小则卡死 / `null` 与负数回退全局）；`AdminApiIntegrationTests.GroupQuotaOverride_WinsInUsageAndQuotaStatus`（管理员设覆写 → 群列表回读 → `/usage` 行 `quota` 用覆写值且 `level=over` → `/quota-status` 同口径 → 普通用户即群主 403 → 清除）。**全量测试通过**（1803 → 1806）；`dotnet build` **0 错误**；`node --check` 三份前端脚本；i18n 审计 **zh=en=1945，missing 0**，4 个守卫脚本均过；Docker 重建后**真实浏览器**端到端（Playwright）**12/12**：对今日有用量的真实知聚设覆写 1000（全局 0）→ 群列表回读 1000、`/usage` 该行 `quota=1000` 且 `level=over`、`/quota-status` `level=over`、设置弹窗对管理员可见并回填 1000、无页面脚本错误、清除后回 `null`。
English: 3 new cases — `GroupHubTests.UpdateGroup_QuotaOverride_AppliedAndCleared` (both JSON-number and numeric-string forms; `0` is a valid override; negative / non-numeric clears it); `AgentUsageServiceTests.GroupQuota_OverrideWinsAndZeroMeansUnlimitedForThatGroup` (override unblocks / override 0 = unlimited / a smaller override still blocks / `null` and negatives fall back to the global); `AdminApiIntegrationTests.GroupQuotaOverride_WinsInUsageAndQuotaStatus` (admin sets the override → group list reads it back → the `/usage` row uses the override as its `quota` with `level=over` → `/quota-status` matches → a normal user who owns the group gets 403 → cleared). **All tests pass** (1803 → 1806); `dotnet build` reports **0 errors**; `node --check` passes for all three frontend scripts; the i18n audit is **zh=en=1945, missing 0**, and all 4 guard scripts pass; after a Docker rebuild, a **real-browser** end-to-end (Playwright) **12/12**: setting an override of 1000 on a circle that has today's usage (global 0) → the group list reads back 1000, the `/usage` row has `quota=1000` with `level=over`, `/quota-status` reports `level=over`, the settings dialog is visible to the admin and prefilled with 1000, there are no page script errors, and clearing returns it to `null`.

# AG-UI Group Chat Desktop 1.0.266 Release Notes (current Windows desktop release)

**版本说明**：**知聚配额从「事后可查」变成「事前可见」**——新增**配额水位预警**：当日用量达知聚配额 80% 即标 `⚠`、达 100% 标 `🔴`，**不打开控制台就能在侧边栏知聚列表上看到哪个知聚快满了**（仅 Operator+；配额 / 成本属运维信息，不下发普通用户）。同一水位口径同时用在用量统计页：表格按水位配色（蕾珀 / 红），meta 行多一句「接近上限 N 个 ｜ 已超限 M 个」。
**Version note**: **The per-circle quota goes from "queryable after the fact" to "visible in advance"** — a new **quota-level warning**: at 80% of a circle's daily quota it shows `⚠`, at 100% `🔴`, so **you can see which circle is nearly full right in the sidebar circle list without opening the console** (Operator+ only; quota / cost is ops information and is not served to regular users). The same level source drives the Usage Stats page: the table is colour-coded by level (amber / red) and the meta line gains "{near} approaching limit ｜ {over} over limit".

## 变更
# Changes

中文：
- **单一阈值来源**：新增 `AgentUsageService.QuotaLevel(tokens, quota)` 与 `QuotaWarnRatio = 0.8`（`AgentUsageService`）——`ok` / `warn`（≥80%）/ `over`（≥配额），配额 0（不限）恒 `ok`。前后端**不再各写一份 80%**。
- **新增 `GET /ag-ui/admin/quota-status`**（Operator+）：返回 `{ quotaPerGroup, warnRatio, groups: [{ groupId, level }] }`（今日有用量且非 `ok` 的知聚），供侧边栏打标。
- **侧边栏知聚项预警角标**：`loadQuotaStatus()`（仅 Operator+）与知聚列表同刷新；`renderGroupList()` 给 `warn` / `over` 的知聚加 `.quota-tag`（`⚠` / `🔴`，`title` 解释原因）。登出即清（不残留）。
- **用量页**：`groupsToday[]` 每行新增 `level`；配额使用列按水位配色（`warn` 蕾珀 `.usage-quota-warn` / `over` 红标 `.usage-quota-hit`）；meta 尾部追加「接近上限 N 个 ｜ 已超限 M 个」。`/usage` 与 `/quota-status` 共用同一个行构造（`GroupTodayRows`），口径一致。
- **降级安全**：配额查询失败 / 非 Operator+ 一律静默（清空角标），绝不拖累知聚列表渲染。

English:
- **Single threshold source**: new `AgentUsageService.QuotaLevel(tokens, quota)` and `QuotaWarnRatio = 0.8` (`AgentUsageService`) — `ok` / `warn` (≥80%) / `over` (≥ quota), with quota 0 (unlimited) always `ok`. The frontend and backend **no longer each hard-code 80%**.
- **New `GET /ag-ui/admin/quota-status`** (Operator+): returns `{ quotaPerGroup, warnRatio, groups: [{ groupId, level }] }` (circles with today's usage that are not `ok`), feeding the sidebar badges.
- **Sidebar warning badges**: `loadQuotaStatus()` (Operator+ only) refreshes alongside the circle list; `renderGroupList()` adds `.quota-tag` (`⚠` / `🔴`, with a `title` explaining why) to `warn` / `over` circles. Cleared on logout (no residue).
- **Usage page**: each `groupsToday[]` row gains `level`; the quota-use column is colour-coded by level (`warn` amber `.usage-quota-warn` / `over` red `.usage-quota-hit`); the meta line appends "{near} approaching limit ｜ {over} over limit". `/usage` and `/quota-status` share one row builder (`GroupTodayRows`), keeping the two consistent.
- **Graceful degradation**: a failed quota query or a non-Operator+ user silently clears the badges and never drags down the circle-list rendering.

## 文档
# Docs

中文：`docs/user-guide.md` / `en.md` §2.1.1（知聚列表元素表新增 `.quota-tag` 配额水位角标）、§8.6（`groupsToday` 补 `level` 字段与配色 / meta 汇总，并把行号校正到当前实现）、§9.2.8（新增 `/ag-ui/admin/quota-status`）。
English: `docs/user-guide.md` / `en.md` §2.1.1 (the circle-list element table gains the `.quota-tag` quota-level badge), §8.6 (`groupsToday` gains the `level` field with the colour coding / meta summary, and line numbers are corrected to the current implementation), §9.2.8 (new `/ag-ui/admin/quota-status`).

## 测试
# Tests

中文：新增 2 例——`AgentUsageServiceTests.QuotaLevel_ThresholdsWarnAt80AndOverAt100`（边界：79 ok / 80 warn / 99 warn / 100 over / 150 over，且配额 0 恒 ok）；`AdminApiIntegrationTests.QuotaStatus_ReportsWarnAndOverLevelsForGroups`（设知聚配额 100 → 80 warn、100 over、20 ok；`/usage` 的 `groupsToday` 同口径带 `level`；非管理员 403；结束还原配额）。**全量测试通过**（1801 → 1803）；`dotnet build` **0 错误**；`node --check` 三份前端脚本；i18n 审计 **zh=en=1942，missing 0**，4 个守卫脚本均过；Docker 重建后**真实浏览器**端到端（Playwright）**7/7**——把配额临时设为 1000 使真实历史用量越限：侧边栏出现 `🔴` `over` 角标、用量页 meta「接近上限 0 个 ｜ 已超限 1 个」、配额列红标高亮、**无页面脚本错误**，结束时还原配额。
English: 2 new cases — `AgentUsageServiceTests.QuotaLevel_ThresholdsWarnAt80AndOverAt100` (boundaries: 79 ok / 80 warn / 99 warn / 100 over / 150 over, and quota 0 always ok); `AdminApiIntegrationTests.QuotaStatus_ReportsWarnAndOverLevelsForGroups` (quota 100 → 80 warn, 100 over, 20 ok; `/usage`'s `groupsToday` carries the same `level`; non-admin 403; quota restored afterwards). **All tests pass** (1801 → 1803); `dotnet build` reports **0 errors**; `node --check` passes for all three frontend scripts; the i18n audit is **zh=en=1942, missing 0**, and all 4 guard scripts pass; after a Docker rebuild, a **real-browser** end-to-end (Playwright) **7/7** — with the quota temporarily set to 1000 so real historical usage crosses the threshold: the sidebar shows a `🔴` `over` badge, the usage page meta reads "0 approaching limit ｜ 1 over limit", the quota column is highlighted red, and there are **no page script errors**; the quota is restored at the end.

# AG-UI Group Chat Desktop 1.0.265 Release Notes (current Windows desktop release)

**版本说明**：**把「谁在烧 token」摆到台面上**——用量统计页新增「**今日各知聚用量**」明细（含**配额使用占比**，达/超配额标红），并给两处配额拦截补上**可观测**（Prometheus 计数器 + 运行指标页卡片）。上一版刚把「按知聚配额」加上，如果看不见「哪个知聚快烧到了」，配额就只能靠报警发现；这一版补上可见性。
**Version note**: **Putting "who is burning tokens" on the table** — the Usage Stats page gains a **today's usage per circle** breakdown (with **quota-utilization share**, red at/over quota), and both quota rejections become **observable** (a Prometheus counter + a Runtime Metrics card). The previous release added the per-circle quota; without visibility into which circle is about to hit its cap, a quota can only be discovered by surprise — this release adds that visibility.

## 变更
# Changes

中文：
- **用量统计新增「今日各知聚明细」**（`GET /ag-ui/admin/usage` 出参新增 `groupsToday[]`）：一次聚合（`AgentUsageService.GetGroupTodaySummary`，复用既有「按知聚」聚合表的当日行，**不扩接口**、4 个存储通用），按 token 降序取前 50；名称由 `GroupHub.Store.AllGroups()` 解析（取不到回退 `groupId`，单聊知聚也能显示「与 X 的单聊」）。前端在按日表下方新增一张表：知聚 / 今日 Token / 调用次数 / **配额使用**（未设知聚配额时显「—」，达到或超出配额时标红）。
- **配额拦截可观测**（`MetricsService.RecordQuotaRejection`，在 `AgentGateway` 两处拦截点调用）：新增计数器 `knowgath_quota_rejections_total{scope="user|group"}`（`/ag-ui/metrics`，按用户 / 按知聚分开），并加进 `Snapshot`；运行指标页新增**「配额拦截」卡片**（仅被挡过才显示，标红，副标题「用户 N ｜ 知聚 M」，一眼分辨是「知聚烧超」还是「个人超」）。
- **为何不用审计日志**：配额拦截是**高频事件**（一旦超限，每个触发都会拦），写进审计日志会冲掉真正的关键操作记录（审计环形缓冲仅 5000 条）；指标是廉价累加、不落库，更适合这类计数。

English:
- **Usage Stats gains "today's usage per circle"** (the `GET /ag-ui/admin/usage` response adds `groupsToday[]`): aggregated in one pass (`AgentUsageService.GetGroupTodaySummary`, reusing the existing per-circle aggregate table's today rows, **without extending the interface**, so all 4 stores work), top 50 by tokens descending; names resolved from `GroupHub.Store.AllGroups()` (falling back to the `groupId`, so direct-chat circles show "Chat with X" too). The frontend adds a second table below the daily one: circle / today's tokens / calls / **quota use** ("—" when no per-circle quota is set, red at or over quota).
- **Quota rejections are observable** (`MetricsService.RecordQuotaRejection`, called at both `AgentGateway` check points): a new counter `knowgath_quota_rejections_total{scope="user|group"}` (`/ag-ui/metrics`, split per user / per circle), also added to `Snapshot`; the Runtime Metrics page gains a **"Quota rejections" card** (shown only once something has been blocked, in red, with a "user N ｜ circle M" subtitle so you can instantly tell a per-circle burn from a personal one).
- **Why not the audit log**: quota rejections are a **high-frequency event** (once over quota, every trigger is blocked), and writing them to the audit log would push out the genuinely critical operations (the audit ring buffer holds only 5000 entries); a cheap, non-persisted counter is the right fit.

## 文档
# Docs

中文：`docs/user-guide.md` / `en.md` §8.6（`/usage` 出参文档补 `groupsToday` 与 4 列表格说明，并把入参行号校正到当前实现）、§8.8.2（`Snapshot` 字段补 `embeddingCacheHits/Misses` 与 `quotaUser/GroupRejections`、卡片说明补两张条件卡片）、§9.2.8（`/usage` 端点描述）、§9.4 Prometheus 指标表（新增 `knowgath_quota_rejections_total`）。
English: `docs/user-guide.md` / `en.md` §8.6 (the `/usage` response doc gains `groupsToday` and the 4-column table, with line numbers corrected to the current implementation), §8.8.2 (`Snapshot` fields gain `embeddingCacheHits/Misses` and `quotaUser/GroupRejections`; the card notes gain the two conditional cards), §9.2.8 (the `/usage` endpoint description), §9.4 Prometheus metrics table (new `knowgath_quota_rejections_total`).

## 测试
# Tests

中文：新增 3 例——`AgentUsageServiceTests.GetGroupTodaySummary_GroupsByGroup_SortedDesc`（按知聚聚合 / 降序 / 无群上下文不入表）；`MetricsTests.RecordQuotaRejection_CountedByScope`（快照两字段 + Prometheus 两个 scope 标签）；`AdminApiIntegrationTests.AdminUsage_IncludesGroupsTodayWithNames`（建真实知聚 → 记录用量 → 断言 `groupsToday` **解出知聚名**而非回退 ID、tokens/calls 正确）。**全量测试通过**（1798 → 1801）；`dotnet build AguiGroupChat.slnx` **0 错误**；`node --check` 三份前端脚本通过；i18n 审计 **zh=en=1939，missing 0**，`check-i18n-keys` / `check-orphan-keys` / `check-config-keys` / `check-docs-links` 均过；Docker 重建后端到端实测（`/usage` 出参含 `groupsToday` 且名称正确解析为「与 字段校验岗 的单聊」；`/admin/metrics` 含两个配额字段；`/ag-ui/metrics` 含 `knowgath_quota_rejections_total{scope=...}`）**7/7**、`tools/api-smoke.mjs` **46/46**。
English: 3 new cases — `AgentUsageServiceTests.GetGroupTodaySummary_GroupsByGroup_SortedDesc` (aggregation per circle / descending / no-group context excluded); `MetricsTests.RecordQuotaRejection_CountedByScope` (both snapshot fields + both Prometheus `scope` labels); `AdminApiIntegrationTests.AdminUsage_IncludesGroupsTodayWithNames` (create a real circle → record usage → assert `groupsToday` **resolves the circle name** rather than falling back to the id, with correct tokens/calls). **All tests pass** (1798 → 1801); `dotnet build AguiGroupChat.slnx` reports **0 errors**; `node --check` passes for all three frontend scripts; the i18n audit is **zh=en=1939, missing 0**, and `check-i18n-keys` / `check-orphan-keys` / `check-config-keys` / `check-docs-links` all pass; end-to-end against the rebuilt Docker stack (the `/usage` response contains `groupsToday` with names correctly resolved to "与 字段校验岗 的单聊"; `/admin/metrics` contains both quota fields; `/ag-ui/metrics` contains `knowgath_quota_rejections_total{scope=...}`) **7/7**, and `tools/api-smoke.mjs` **46/46**.

# AG-UI Group Chat Desktop 1.0.264 Release Notes (current Windows desktop release)

**版本说明**：**修复配额「热改静默失效」+ 新增「按知聚每日 Token 配额」**——前者是长期潜伏的缺陷：管理员在「配置治理」把每日 Token 配额改完、界面回读也对，但**实际不生效**（服务在构造时把配额拷成了一个字段，之后永远读那个旧值）；现已改为**委托实时读取**，保存即生效。后者补上一处「无人可管」的消耗：定时 / 广播任务以虚拟触发者 `system` 发起，**刻意豁免个人配额**，于是这一路只增不减；新增的知聚配额正好卡住它。
**Version note**: **Fixes "hot-change silently ineffective" for quotas + adds "daily token quota per circle"** — the former was a long-latent defect: after an admin changed the daily token quota in Config Governance, the UI read back correctly but it **did not actually apply** (the service had copied the quota into a field at construction and kept reading that stale value); it now reads via a **delegate in real time**, taking effect on save. The latter closes a "nobody can manage it" consumption path: scheduled / broadcast tasks run as the virtual trigger `system`, which is **deliberately exempt** from the per-user quota, so that path only grew; the new per-circle quota caps it.

## 变更
# Changes

中文：
- **修复：配额热改即刻生效**（`AgentUsageService`）。原因：``DailyQuotaPerUser`` 在**构造时**从 `AgentOptions` 拷贝成字段，而「配置治理」改的是 `AgentOptions` 单例属性——服务读的永远是构造那一刻的旧值，**只有重启后才生效**（而 `docs/execution-configuration.md` 却写着「配额 → 热改持久」，属文档与实现不符）。现在改为注入 ``Func<long>``（由 `AgentHosting` 传 ``() => options.DailyTokenQuotaPerUser`` / ``() => options.DailyTokenQuotaPerGroup``），**每次读取都取当前值**。
- **新增：按知聚每日 Token 配额**（`Agents:DailyTokenQuotaPerGroup`，`0` = 不限）。复用既有的「按知聚」用量聚合表（`GetGroupUsageBetween`），**不扩 `IUsageStore` 接口**，4 个存储实现通用。超限时在网关 `InvokeCoreAsync` 广播 `RunErrorEvent { ErrorCode = "GROUP_QUOTA_EXCEEDED" }` 并拒绝本次触发。
- **它与按用户配额互补**：个人配额**豁免** `system` 触发者（系统任务不该被某个人的配额误伤），所以定时 / 广播任务的消耗此前无人可管；**知聚配额不豁免 `system`**——这一路消耗由它管住。
- **兼容**：保留旧构造 ``(IUsageStore, long, ILogger)``（等价于「固定个人配额 + 知聚不限」），既有测试 / 调用点不受影响。
- **配置治理新增 1 键**（22 → **23**）：`dailyTokenQuotaPerGroup`（≥ 0；越界 / 为负返回 **400**）。`GET /ag-ui/admin/usage` 出参新增 `dailyQuotaPerGroup`；用量统计页在个人配额提示后追加一行「每知聚每日配额」。
- **Docker**：`docker-compose.yml` 新增 `Agents__DailyTokenQuotaPerGroup`（`AGENTS_DAILY_TOKEN_QUOTA_PER_GROUP`，默认 0）。

English:
- **Fix: quota hot-change now applies at once** (`AgentUsageService`). Cause: `DailyQuotaPerUser` was copied from `AgentOptions` into a field **at construction**, while Config Governance mutates the `AgentOptions` singleton property — so the service forever read the value from construction time and it **only took effect after a restart** (while `docs/execution-configuration.md` claimed "quota → hot-change persistent", i.e. docs did not match the implementation). It now takes a `Func<long>` (injected by `AgentHosting` as `() => options.DailyTokenQuotaPerUser` / `() => options.DailyTokenQuotaPerGroup`), **reading the current value on every access**.
- **New: daily token quota per circle** (`Agents:DailyTokenQuotaPerGroup`, `0` = unlimited). It reuses the existing per-circle usage aggregate table (`GetGroupUsageBetween`) and **does not extend the `IUsageStore` interface**, so all 4 store implementations work. On overflow the gateway's `InvokeCoreAsync` broadcasts `RunErrorEvent { ErrorCode = "GROUP_QUOTA_EXCEEDED" }` and rejects the trigger.
- **It complements the per-user quota**: the personal quota **exempts** the `system` trigger (system tasks should not be hurt by one person's quota), so scheduled / broadcast consumption was previously unmanageable; the **per-circle quota does not exempt `system`** — that path is exactly what it caps.
- **Compatibility**: the old constructor `(IUsageStore, long, ILogger)` is kept (equivalent to "fixed per-user quota + no per-circle limit"), so existing tests / call sites are unaffected.
- **Config Governance gains 1 key** (22 → **23**): `dailyTokenQuotaPerGroup` (≥ 0; out-of-range / negative returns **400**). `GET /ag-ui/admin/usage` gains `dailyQuotaPerGroup`; the Usage Stats page appends a "daily quota per circle" line after the per-user quota hint.
- **Docker**: `docker-compose.yml` gains `Agents__DailyTokenQuotaPerGroup` (`AGENTS_DAILY_TOKEN_QUOTA_PER_GROUP`, default 0).

## 文档
# Docs

中文：`docs/user-guide.md` / `en.md` §8.6（用量统计新增知聚配额说明）、§8.10（配置治理 **22 → 23 键**，新增 `#cfgDailyTokenQuotaGroup` 行与边界校验）、§9.3.3（`Agents` 配置键表新增 `DailyTokenQuotaPerGroup`）、§9.5.2（env 表新增 `AGENTS_DAILY_TOKEN_QUOTA_PER_GROUP`）；`docs/execution-configuration.md`（配额热改说明现与实现一致）。
English: `docs/user-guide.md` / `en.md` §8.6 (Usage Stats gains the per-circle quota note), §8.10 (Config Governance **22 → 23 keys**, new `#cfgDailyTokenQuotaGroup` row and bounds), §9.3.3 (`Agents` config key table gains `DailyTokenQuotaPerGroup`), §9.5.2 (env table gains `AGENTS_DAILY_TOKEN_QUOTA_PER_GROUP`); `docs/execution-configuration.md` (the quota hot-change claim now matches the implementation).

## 测试
# Tests

中文：`AgentUsageServiceTests` 新增 4 例——①配额热改实时生效（回归：构造后改被委托读取的值，`DailyQuotaPerUser` 跟着变、`CheckUserQuota` 随之放行 / 拦截）；②旧构造行为不变（固定个人配额、知聚不限）；③知聚配额执行且**计入 `system` 触发**；④`GetGroupTodayTokens` 跨知聚不串、空 ID 返回 0。扩展 `AdminApiIntegrationTests.ConfigGovernance_Post_AdminUpdatesAndPersists`（保存两个配额 → 回读一致 + 断言落到运行时 `AgentOptions`，并**断言 `AgentUsageService` 单例读到的是新值**——即本缺陷的回归检查）；`ConfigGovernance_Post_RejectsInvalidAndForbidsNonAdmin` 新增两条负值 400。**全量 1798 测试通过**（1794 → 1798）；`dotnet build AguiGroupChat.slnx` **0 错误**；`node --check` 三份前端脚本通过；i18n 审计 **zh=en=1933，missing 0**，`check-i18n-keys` / `check-orphan-keys` / `check-config-keys` / `check-docs-links` 均过。
English: `AgentUsageServiceTests` gains 4 cases — ①quota hot-change applies live (regression: after construction, changing the value read by the delegate makes `DailyQuotaPerUser` follow and `CheckUserQuota` allow / block accordingly); ②the old constructor keeps its behaviour (fixed per-user quota, no per-circle limit); ③per-circle quota enforcement that **counts `system` triggers**; ④`GetGroupTodayTokens` does not cross circles and returns 0 for an empty id. Extended `AdminApiIntegrationTests.ConfigGovernance_Post_AdminUpdatesAndPersists` (save both quotas → read back consistently + assert they land in the runtime `AgentOptions`, and **assert the `AgentUsageService` singleton reads the new value** — i.e. the regression check for this defect); `ConfigGovernance_Post_RejectsInvalidAndForbidsNonAdmin` gains two negative-value 400 checks. **All 1798 tests pass** (1794 → 1798); `dotnet build AguiGroupChat.slnx` reports **0 errors**; `node --check` passes for all three frontend scripts; the i18n audit is **zh=en=1933, missing 0**, and `check-i18n-keys` / `check-orphan-keys` / `check-config-keys` / `check-docs-links` all pass.

# AG-UI Group Chat Desktop 1.0.263 Release Notes (current Windows desktop release)

**版本说明**：**记忆与向量化旋钮上线「配置治理」**——把「语义记忆检索慢 / 超时」的主旋钮（检索 query 截断、写入侧截断、交互 / 后台等待上限）与新增的「写入去重缓存条数」搬到管理员控制台在线热改，保存即生效、无需改 appsettings / 重启。
**Version note**: **Memory & vectorization knobs land in Config Governance** — the main knobs for “semantic-memory retrieval is slow / times out” (retrieval-query truncation, write-side truncation, interactive / background slot-wait ceilings) plus the new “write-dedup cache entries” are now editable online in the admin console, taking effect on save with no appsettings edit or restart.

## 变更
# Changes

中文：
- **配置治理新增 5 个记忆旋钮**（`ConfigGovernanceState` / `ConfigGovernanceHttpRequest` / `Apply` / 持久化恢复 / 边界校验全链）：`memoryMaxQueryChars`（1..10000）、`memoryMaxWriteChars`（1..10000）、`memoryInteractiveEmbeddingWaitSeconds`（1..600）、`memoryBackgroundEmbeddingWaitSeconds`（1..3600）、`memoryWriteEmbeddingCacheSize`（≥0；**0 = 关闭去重**），写入 `AgentOptions.Memory` 对应字段。
- **保存即生效**：这五项在**每次写入 / 检索时重新读取**（`MemoryContextProvider` / `AgentMessageMemory.EnterEmbeddingAsync` / 写队列），所以保存对后续调用立即生效。
- **去重缓存容量可热改**：`AgentMessageMemory` 改为**按需读取** `WriteEmbeddingCacheSize`（容量变了就重建 / 热改），`MemoryEmbeddingCache` 新增 `SetCapacity`（调小时按 LRU 立即淘汰多余条目）；改 0 = 立即关闭去重（行为回到旧版：每次写入都真正向量化）。
- **前端**：配置治理页新增「记忆与向量化」提示行 `admin.memHint` + 5 个数字框（`#cfgMemMaxQueryChars` / `#cfgMemMaxWriteChars` / `#cfgMemInteractiveWait` / `#cfgMemBackgroundWait` / `#cfgMemDedupCache`），`loadConfigGovernance` / `collectCfg` 同步；i18n 新增 6 键（zh/en）。
- **顺带修复**：1.0.263 开发中一次 fuzzy 编辑曾误删写队列消费者启动行（`_ = Task.Run(ProcessWriteQueueAsync)`）——已发现并修复，并由新增 / 既有记忆用例回归覆盖。

English:
- **Config Governance gains 5 memory knobs** (full chain: `ConfigGovernanceState` / `ConfigGovernanceHttpRequest` / `Apply` / persistence restore / bounds validation): `memoryMaxQueryChars` (1..10000), `memoryMaxWriteChars` (1..10000), `memoryInteractiveEmbeddingWaitSeconds` (1..600), `memoryBackgroundEmbeddingWaitSeconds` (1..3600), `memoryWriteEmbeddingCacheSize` (≥0; **0 = dedup off**), written into the corresponding `AgentOptions.Memory` fields.
- **Save takes effect immediately**: these five are re-read on **every write / retrieval** (`MemoryContextProvider` / `AgentMessageMemory.EnterEmbeddingAsync` / the write queue), so saving applies to subsequent calls at once.
- **Dedup cache capacity is hot-changeable**: `AgentMessageMemory` now reads `WriteEmbeddingCacheSize` **on demand** (rebuilds / resizes when it changes) and `MemoryEmbeddingCache` gains `SetCapacity` (shrinking evicts the excess by LRU at once); setting it to 0 disables dedup immediately (back to the old behaviour of embedding every write).
- **Frontend**: the Config Governance page gains a “Memory & vectorization” hint `admin.memHint` plus 5 number boxes (`#cfgMemMaxQueryChars` / `#cfgMemMaxWriteChars` / `#cfgMemInteractiveWait` / `#cfgMemBackgroundWait` / `#cfgMemDedupCache`), kept in sync by `loadConfigGovernance` / `collectCfg`; i18n adds 6 keys (zh/en).
- **Incidental fix**: during 1.0.263 development a fuzzy edit accidentally dropped the write-queue consumer startup line (`_ = Task.Run(ProcessWriteQueueAsync)`); it was caught and fixed, covered by the memory test regression.

## 文档
# Docs

中文：`docs/user-guide.md` / `en.md` §8.10（配置治理 **17 → 22 键**：新增 8.10.1 表行 #18–#22 与记忆旋钮说明、边界校验）；§9.2.8（`POST /ag-ui/admin/config` 描述补「记忆与向量化」）；`ROADMAP.md` §6.3。
English: `docs/user-guide.md` / `en.md` §8.10 (Config Governance **17 → 22 keys**: new table rows #18–#22 with the memory-knob notes and bounds); §9.2.8 (`POST /ag-ui/admin/config` description gains “memory & vectorization”); `ROADMAP.md` §6.3.

## 测试
# Tests

中文：扩展 `AdminApiIntegrationTests.ConfigGovernance_Post_AdminUpdatesAndPersists`（保存 5 个记忆旋钮 → 回读一致 + 断言真的落到运行时单例 `AgentOptions.Memory`）；`ConfigGovernance_Post_RejectsInvalidAndForbidsNonAdmin` 新增两条 400 边界（`memoryMaxQueryChars=0`、`memoryWriteEmbeddingCacheSize=-1`）。**全量 1794 测试通过**；`dotnet build AguiGroupChat.slnx` **0 错误**；i18n 审计 **zh=en=1931，missing 0**，`check-i18n-keys` / `check-orphan-keys` 均过。
English: extended `AdminApiIntegrationTests.ConfigGovernance_Post_AdminUpdatesAndPersists` (save the 5 memory knobs → read back consistently + assert they truly land in the runtime `AgentOptions.Memory`); `ConfigGovernance_Post_RejectsInvalidAndForbidsNonAdmin` gains two 400 bounds (`memoryMaxQueryChars=0`, `memoryWriteEmbeddingCacheSize=-1`). **All 1794 tests pass**; `dotnet build AguiGroupChat.slnx` reports **0 errors**; the i18n audit is **zh=en=1931, missing 0**, and `check-i18n-keys` / `check-orphan-keys` both pass.

# AG-UI 群聊桌面版 1.0.262 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.262 Release Notes (current Windows desktop release)

**版本说明**：**记忆写入「重复编码」去重**——写入向量化前先用**实际入库文本**查一个进程内 LRU 缓存，命中即复用向量、跳过 embedding（相同文本向量恒等 → **语义等价**，召回结果不变，只省重复推理）；新增可观测指标与控制台命中占比卡片。
**Version note**: **Write-side "repeated encoding" dedup** — before embedding, the **actual stored text** is looked up in an in-process LRU cache; a hit reuses the vector and skips embedding (identical text yields an identical vector, so it is **semantics-preserving** — recall is unchanged and only the repeated inference is saved); with new metrics and a hit-share card on the admin console.

## 变更
# Changes

中文：
- **写入去重（新）**：新增 `MemoryEmbeddingCache`（进程内 LRU，容量 `Agents:Memory:WriteEmbeddingCacheSize`，默认 **512**、**0 = 关闭**）。`AgentMessageMemory` 在原「净化 + 截断」之后、embedding 之前先查缓存：命中则复用向量、**跳过 embedding 调用**（也省掉一次后台池槽位占用）；未命中才向量化并写回缓存。
- **为什么语义等价**：向量只由「文本 + 模型」决定——相同文本在同一模型下向量恒等，复用结果与重算**逐位相同**；且**每条消息仍各自落库、按自己的 messageId 可被检索**（去重的是“要不要再算向量”，不是“要不要存记忆”），所以召回集合与排序一字不差。
- **范围**：只作用于**写入 / 导入**路径（`ProcessWriteQueueAsync` / `ImportMemoriesAsync`）；检索 query 通常唯一、命中率低，不缓存。有界 LRU（512 条约 2MB），无 TTL。
- **可观测**：`MetricsService.RecordEmbeddingCache` + 计数器 `knowgath_memory_embedding_cache_hits_total` / `knowgath_memory_embedding_cache_misses_total`（`/ag-ui/metrics` 与 `Snapshot`）；控制台「运行指标」新增**「记忆去重命中 / 写入编码」**卡片（无写入时不显示），i18n `admin.metricDedup`。
- **Docker**：`docker-compose.yml` 新增 `Agents__Memory__WriteEmbeddingCacheSize`（`MEMORY_WRITE_EMBEDDING_CACHE_SIZE`，默认 512）。

English:
- **Write dedup (new)**: added `MemoryEmbeddingCache` (in-process LRU, capacity `Agents:Memory:WriteEmbeddingCacheSize`, default **512**, **0 = off**). After the existing sanitize + truncate step and before embedding, `AgentMessageMemory` looks up the cache: a hit reuses the vector and skips the embedding call (also freeing a background-pool slot); a miss embeds as usual and writes the result back.
- **Why it is semantics-preserving**: a vector is determined only by "text + model" — identical text yields an identical vector on the same model, so a reused result is bit-for-bit the same as recomputing; and **every message is still stored under its own `messageId` and remains retrievable** (dedup decides whether to recompute a vector, not whether to keep a memory), so the recalled set and ordering are unchanged.
- **Scope**: only the **write / import** paths (`ProcessWriteQueueAsync` / `ImportMemoriesAsync`); retrieval queries are usually unique and rarely hit, so they are not cached. Bounded LRU (512 entries ≈ 2 MB), no TTL.
- **Observability**: `MetricsService.RecordEmbeddingCache` plus counters `knowgath_memory_embedding_cache_hits_total` / `knowgath_memory_embedding_cache_misses_total` (`/ag-ui/metrics` and `Snapshot`); the console "Runtime Metrics" page gains a **"Memory dedup hits / writes"** card (hidden when there have been no writes), with i18n `admin.metricDedup`.
- **Docker**: `docker-compose.yml` gains `Agents__Memory__WriteEmbeddingCacheSize` (`MEMORY_WRITE_EMBEDDING_CACHE_SIZE`, default 512).

## 文档
# Docs

中文：`README.md` / `README.en.md`（功能清单新增去重条目 + `MEMORY_WRITE_EMBEDDING_CACHE_SIZE` 环境变量表）；`docs/agent-execution-algorithm.md` §2.4（新增子节 + 配置行 + 代码锚点）；`docs/user-guide.md` / `en.md`（§6.1.1 写入链路图加缓存判定、新增 §6.1.3、§6.2 / §9.3.4 配置表、§6.11 排障）；`ROADMAP.md` 新增 §2.6。
English: `README.md` / `README.en.md` (feature bullet + `MEMORY_WRITE_EMBEDDING_CACHE_SIZE` in the env-var table); `docs/agent-execution-algorithm.md` §2.4 (new subsection + config row + code anchors); `docs/user-guide.md` / `en.md` (§6.1.1 write-chain diagram gains the cache decision, new §6.1.3, config tables §6.2 / §9.3.4, troubleshooting §6.11); `ROADMAP.md` gains §2.6.

## 测试
# Tests

中文：新增 `MemoryDedupCacheTests`（9 例）——缓存本体（存取 / LRU 淘汰 / 空向量忽略 / 默认容量）、写入路径（同内容只编码一次但两条消息都落库且向量一致、异内容各编码、关闭缓存行为不变、截断后同文本命中）、导入路径复现缓存复用，并断言 Prometheus 命中 / 未命中计数。`dotnet build AguiGroupChat.slnx` 0 错误；`node --check` 三份前端脚本通过；i18n 审计 **zh=en=1925，missing 0**，`check-i18n-keys` / `check-orphan-keys` 均过。
English: new `MemoryDedupCacheTests` (9 cases) — the cache itself (get/set, LRU eviction, empty-vector ignored, default capacity), the write path (identical content embedded once while both messages are stored with the same vector, different content embedded separately, cache-off behaves as before, truncated-to-equal text hits), and import reuse of the cached vector, with assertions on the Prometheus hit/miss counters. `dotnet build AguiGroupChat.slnx` 0 errors; `node --check` passes for all three frontend scripts; the i18n audit is **zh=en=1925, missing 0**, and `check-i18n-keys` / `check-orphan-keys` both pass.

# AG-UI 群聊桌面版 1.0.261 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.261 Release Notes (current Windows desktop release)

**版本说明**：**知聚级模型覆写**——管理员可在「知聚设置」给某个知聚**钉一个模型**（如高消耗知聚钉 `deepseek-v4-pro`，或钉 `deepseek-flash` 省成本），优先级**高于**按复杂度自动选模型与本知聚内智能体的默认模型；顺带修复了一个存储缺陷（群更新未写 `extra` 列）。
**Version note**: **Per-circle model override** — an admin can pin a model for a specific group in “Group Settings” (e.g. pin `deepseek-v4-pro` for a heavy group, or `deepseek-flash` to save cost); it takes precedence over complexity-based routing and the default model of digital employees in that group. A store defect (group update did not write the `extra` column) was fixed alongside.

## 变更
# Changes

中文：
- **知聚级模型覆写（新）**：`Group.ModelOverride` 存于 `Extra["model"]`（同 `Kind`，零迁移）；`GroupHub.UpdateGroupAsync` 支持 `updateFields=["model"]`（空串 = 取消覆写）；`GroupHub.GroupModelOverride` 供网关读取。
- **优先级**：`AgentGateway` 先取知聚覆写，再回退按复杂度路由——`effectiveModel = ResolveGroupModel(groupId) ?? routedModel`（只换模型，工具 / 记忆 / 审批包装照旧）。
- **权限**：知聚级模型属成本 / 容量控制，**仅平台管理员**可设——`HttpGroupApi` `/update` 在含 `model` 时要求 `ResolveRole ≥ Admin`，否则 **403**；群更新本身仍要群主 / 群管理员（实际需“知聚主/群管理员 且 平台管理员”）。
- **前端**：知聚设置弹窗新增 `#gsModelGroup` / `#gsModelOverride`（**仅管理员可见**，i18n `gs.model` / `gs.modelHint`）；群列表接口下发 `modelOverride` 供回显。
- **修复存储缺陷**：`PostgresGroupStore.UpdateGroup` / `RelationalGroupStore.UpdateGroup` 的 UPDATE 语句**未写 `extra` 列**——导致 `kind` / `model` 等 extra 支撑字段在创建后无法修改 / 清除（仅内存 / Redis 正常）。已补齐 `extra = @extra`，并加回归。

English:
- **Per-circle model override (new)**: `Group.ModelOverride` is stored in `Extra["model"]` (like `Kind`, zero migration); `GroupHub.UpdateGroupAsync` accepts `updateFields=["model"]` (empty string clears it); `GroupHub.GroupModelOverride` exposes it to the gateway.
- **Precedence**: `AgentGateway` takes the group override first, then falls back to complexity routing — `effectiveModel = ResolveGroupModel(groupId) ?? routedModel` (only the model changes; tools / memory / approval wrapping are unchanged).
- **Permission**: a per-group model is a cost / capacity control, so **platform admins only** — `HttpGroupApi`'s `/update` requires `ResolveRole ≥ Admin` when `model` is present, otherwise **403**; group updates themselves still require owner / group-admin (so it effectively needs owner/group-admin AND platform admin).
- **Frontend**: the group settings dialog gains `#gsModelGroup` / `#gsModelOverride` (**admins only**, i18n `gs.model` / `gs.modelHint`); the group-list endpoint now returns `modelOverride` for rendering.
- **Store defect fixed**: `PostgresGroupStore.UpdateGroup` / `RelationalGroupStore.UpdateGroup` did not write the `extra` column — so extra-backed fields such as `kind` / `model` could not be changed or cleared after creation (only in-memory / Redis were fine). Added `extra = @extra`, with a regression test.

## 文档
# Docs

中文：`docs/user-guide.md` / `en.md` §2.2.2（新增 2.2.2.1 知聚级模型覆写：入口 / 语义 / 存储 / 权限 / 生效 + 存储缺陷说明）；`ROADMAP.md` §6.7（按知聚覆写落地）。
English: `docs/user-guide.md` / `en.md` §2.2.2 (new 2.2.2.1 for the group model override: entry / meaning / storage / permission / effect, plus the store-defect note); `ROADMAP.md` §6.7 (per-group override shipped).

## 测试
# Tests

中文：新增 `GroupHubTests.UpdateGroup_ModelOverride_AppliedAndCleared`（设置 → 读回 → 空串清除）；`AdminApiIntegrationTests.GroupModelOverride_RequiresPlatformAdmin`（普通用户即群主 → 403；管理员 → 200 并回读 `modelOverride`）；扩展 `Postgres/Relational/Sqlite` 的群存储往返用例（`extra` 透传 + 更新后清除）。**全量 1785 测试通过**；`dotnet build AguiGroupChat.slnx` **0 错误**；i18n 审计 **zh=en=1924，missing 0**；本机 Docker（Postgres）实测：登录后对真实知聚设置 `model=deepseek-v4-pro` → 200、群列表回读一致，随后清除（此过程暴露并验证了上述存储缺陷修复）。
English: new `GroupHubTests.UpdateGroup_ModelOverride_AppliedAndCleared` (set → read back → clear with an empty string); `AdminApiIntegrationTests.GroupModelOverride_RequiresPlatformAdmin` (a normal user who owns the group → 403; an admin → 200 and reads back `modelOverride`); extended the Postgres / Relational / SQLite group-store round-trip cases (`extra` passthrough plus clear-after-update). **All 1785 tests pass**; `dotnet build AguiGroupChat.slnx` reports **0 errors**; the i18n audit is **zh=en=1924, missing 0**; verified against local Docker (Postgres): after login, setting `model=deepseek-v4-pro` on a real group returned 200 and read back consistently from the group list, then was cleared (this exposed and validated the store fix above).

# AG-UI 群聊桌面版 1.0.260 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.260 Release Notes (current Windows desktop release)

**版本说明**：**模型路由补全 Standard 档 + 可在「配置治理」页在线开关**——新增 `ModelRoutingOptions.StandardModel`（Standard 档不再固定不换，留空 = 不换），并把 `ModelRouting` 四个旋钮（启用 + 简单/标准/复杂三档模型名）暴露到管理员控制台「配置治理」页，保存即时生效、重启后仍生效，无需改 appsettings / env。
**Version note**: **Model routing gains the Standard tier and is now editable in Config Governance** — added `ModelRoutingOptions.StandardModel` (the Standard tier no longer never-switches; blank = don't switch), and exposed the four `ModelRouting` knobs (enable + simple/standard/heavy model names) on the admin Config Governance page: saving takes effect immediately and persists across restarts, with no appsettings / env edits.

## 变更
# Changes

中文：
- **Standard 档（新）**：`ModelRoutingOptions.StandardModel`；`ResolveForTier` 的 Standard 档由「固定不换」改为「配了就换、留空不换」（向后兼容：默认留空 → 行为与旧版一致）。
- **在线配置**：`ConfigGovernanceState` / `ConfigGovernanceHttpRequest` / `Apply` / 持久化新增 4 个字段（`modelRoutingEnabled` / `modelRoutingSimpleModel` / `modelRoutingStandardModel` / `modelRoutingHeavyModel`）；`Apply` 写入 `AgentOptions.ModelRouting`。校验：模型名 >200 字符返回 400。
- **前端**：「配置治理」页新增一个三态复选框 `#cfgModelRouting` + 三个模型名输入框 `#cfgRoutingSimple/Standard/Heavy`（`collectCfg` / `loadConfigGovernance` 同步）；i18n 新增 `admin.modelRouting` / `admin.routingSimple` / `admin.routingStandard` / `admin.routingHeavy`（zh/en）。示例：Simple/Standard 用 `deepseek-flash`、Heavy 用 `deepseek-v4-pro`。
- **Docker**：`docker-compose.yml` 补 `Agents__ModelRouting__StandardModel`（`AGENTS_MODELROUTING_STANDARD_MODEL`）。

English:
- **Standard tier (new)**: `ModelRoutingOptions.StandardModel`; the Standard arm of `ResolveForTier` changed from "never switch" to "switch if configured, blank = don't switch" (backward compatible: blank by default → same as before).
- **Online config**: `ConfigGovernanceState` / `ConfigGovernanceHttpRequest` / `Apply` / persistence gain 4 fields (`modelRoutingEnabled` / `modelRoutingSimpleModel` / `modelRoutingStandardModel` / `modelRoutingHeavyModel`); `Apply` writes into `AgentOptions.ModelRouting`. Validation: a model name over 200 chars returns 400.
- **Frontend**: the Config Governance page gains one tri-state checkbox `#cfgModelRouting` plus three model-name inputs `#cfgRoutingSimple/Standard/Heavy` (kept in sync by `collectCfg` / `loadConfigGovernance`); i18n adds `admin.modelRouting` / `admin.routingSimple` / `admin.routingStandard` / `admin.routingHeavy` (zh/en). Example: `deepseek-flash` for Simple/Standard and `deepseek-v4-pro` for Heavy.
- **Docker**: `docker-compose.yml` gains `Agents__ModelRouting__StandardModel` (`AGENTS_MODELROUTING_STANDARD_MODEL`).

## 文档
# Docs

中文：`docs/user-guide.md` / `en.md` §8.10（配置治理 13 → **17** 键，含 8.10.1 表与说明）；`ROADMAP.md` §6.7；`README.md` / `en.md`（`AGENTS_MODELROUTING_*` 补 `STANDARD_MODEL` 与在线配置说明）。
English: `docs/user-guide.md` / `en.md` §8.10 (Config Governance 13 → **17** keys, incl. the 8.10.1 table and notes); `ROADMAP.md` §6.7; `README.md` / `en.md` (`AGENTS_MODELROUTING_*` gains `STANDARD_MODEL` plus the online-config note).

## 测试
# Tests

中文：新增 / 扩展 `AgentPerformanceTests.ModelRouting_ResolveForTier_OnlyWhenEnabled`（Standard 有值时换、留空时不换）；`AdminApiIntegrationTests.ConfigGovernance_Post_AdminUpdatesAndPersists` 增加路由四字段（保存 → 回读 `heavy=deepseek-v4-pro` 等断言）。相关测试类 **22 通过**；`dotnet build AguiGroupChat.slnx` **0 错误**；i18n 审计 **zh=en=1922，missing 0**；本机 Docker 实测：登录后 `POST /ag-ui/admin/config` 保存路由 → `GET /config/governance` 回读到 `enabled=true, simple/standard=deepseek-flash, heavy=deepseek-v4-pro`，随后恢复默认。
English: new/extended `AgentPerformanceTests.ModelRouting_ResolveForTier_OnlyWhenEnabled` (Standard switches when set, stays null when blank); `AdminApiIntegrationTests.ConfigGovernance_Post_AdminUpdatesAndPersists` now covers the four routing fields (save → read-back asserts incl. `heavy=deepseek-v4-pro`). The related test classes passed **22**; `dotnet build AguiGroupChat.slnx` reports **0 errors**; the i18n audit is **zh=en=1922, missing 0**; verified against local Docker: after login, `POST /ag-ui/admin/config` saved the routing config → `GET /config/governance` read back `enabled=true, simple/standard=deepseek-flash, heavy=deepseek-v4-pro`, then restored to defaults.

# AG-UI 群聊桌面版 1.0.259 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.259 Release Notes (current Windows desktop release)

**版本说明**：**搜索框“×”清除按钮全覆盖 + 过期文案清理**——补齐最后几个缺失的筛选框（转发目标 / 记忆图谱 / 审计筛选），并把 index.html 中残留的旧模型名兜底文案对齐到 `deepseek-flash`。
**Version note**: **Full coverage of the “×” clear buttons + stale-copy cleanup** — the last few filter boxes missing it (forward target / memory graph / audit filters) now have it, and leftover old-model fallback copy in index.html is aligned to `deepseek-flash`.

## 变更
# Changes

中文：
- **清除按钮补齐**：「转发到知聚」目标筛选 `#forwardSearch`、记忆图谱主题词 `#memGraphQuery`、管理员控制台「审计日志」筛选行 `#auditActor`/`#auditAction`/`#auditTarget` 均补上右侧 “×”（`.search-clear-wrap` + `button.search-clear[data-clear-for]`）。`initSearchClears()` 已通用接线，**无需额外 JS**。至此全部搜索 / 筛选文本框都有 “×”。
- **过期兜底文案**：`#mc.thinking`（模型配置弹窗）与 `help.s5.4`（帮助）的 index.html 兜底文本仍写 `deepseek-reasoner` / `deepseek-chat`，与 1.0.258 统一后的 `deepseek-flash` 对齐。

English:
- **Clear-button coverage**: the forward-target filter `#forwardSearch`, the memory-graph topic box `#memGraphQuery`, and the admin **audit-log** filter row `#auditActor`/`#auditAction`/`#auditTarget` now have the “×” clear button (`.search-clear-wrap` + `button.search-clear[data-clear-for]`). `initSearchClears()` already wired them generically — **no extra JS**. Every search / filter box now has one.
- **Stale fallback copy**: the index.html fallback text for `#mc.thinking` (model-config dialog) and `help.s5.4` still named `deepseek-reasoner` / `deepseek-chat`; aligned to `deepseek-flash` as unified in 1.0.258.

## 文档
# Docs

中文：`docs/ui-design.md` 新增 §2.17，并更新全局「搜索框 ×」约定为全覆盖。
English: `docs/ui-design.md` gains §2.17 and the global “search-box ×” convention is updated to full coverage.

## 测试
# Tests

中文：纯前端改动。Playwright 实测（登录后）：5 个新增清除按钮均存在且结构正确（都在 `.search-clear-wrap` 内）、填入后 “×” 出现、点击清空；审计筛选行无横向溢出（`scrollWidth - clientWidth = 0`）；无控制台报错。i18n 审计 **zh=en=1918，missing 0**、`check-i18n-keys`（884）、`check-orphan-keys`（931）均过。另做了一次运行期扫描：登录后逐个调用 20 个弹窗打开函数并遍历账户菜单项，**无 JS 报错**（仅本机桥 127.0.0.1:17321 探测失败，属预期）。
English: front-end only. Verified with Playwright (after login): all 5 new clear buttons exist with the correct structure (inside `.search-clear-wrap`), the “×” appears once filled and clears on click; the audit filter row does not overflow (`scrollWidth - clientWidth = 0`); no console errors. The i18n audit passes **zh=en=1918, missing 0**, `check-i18n-keys` (884) and `check-orphan-keys` (931). A runtime sweep was also run: after login, invoking 20 modal-opener functions and clicking every account-menu item produced **no JS errors** (only the local-bridge probe at 127.0.0.1:17321 failed, as expected).

# AG-UI 群聊桌面版 1.0.258 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.258 Release Notes (current Windows desktop release)

**版本说明**：**DeepSeek 全部统一到 `deepseek-flash`**——按官方文档（api-docs.deepseek.com），现网只有一个 Flash 模型名；“是否思考”改为**请求参数** `thinking.type` 控制（传输层注入），不再靠换模型名。旧的 `deepseek-chat` / `deepseek-reasoner` / `deepseek-v4-flash-vision-exp` 引用全部清理。
**Version note**: **All DeepSeek calls now use `deepseek-flash`** — per the official docs (api-docs.deepseek.com) there is a single Flash model name; "whether to think" is now a **request field** (`thinking.type`, injected at the transport layer) instead of swapping model names. All references to the legacy `deepseek-chat` / `deepseek-reasoner` / `deepseek-v4-flash-vision-exp` are gone.

## 变更
# Changes

中文：
- **统一模型名**：`AgentCatalog.DeepSeekModel = "deepseek-flash"`（`DeepSeekDefaultModel` / `DeepSeekReasonerModel` / `DeepSeekVisionModel` 均为该值别名）；`StructuredFastModel` / `ResolveDecisionModelName` / `ResolveModelName` 全部收敛到该名；Docker `AGENTS_MODEL` 默认与 `Web/appsettings.json` 的 `Agents:Model` 同步改为 `deepseek-flash`。
- **思考开关改为请求参数**：新增 `AgentCatalog.DeepSeekThinkingPolicy`（`System.ClientModel` 的 `PipelinePolicy`，`PipelinePosition.BeforeTransport`）——在 `chat/completions` 的 JSON 顶层注入 `"thinking":{"type":"enabled|disabled"}`（已存在则不覆盖，并补回 `Content-Type: application/json`）。`BuildOpenAIChatClient(..., DeepSeekThinking thinking = Default)`：`Default` 跟随 `Agents:ThinkingMode`；判定 / 路由 / 严格 JSON / 视觉 / 技能生成与修复等**固定 `Disabled`**，正式回复跟随思考模式。
- **为何必须注入**：实测（真实端点）`deepseek-flash` **默认开思考**，且不抛异常——8/64 token 的判定调用正文会为空（“永不发言 / 指派 0 个下游”），而带 `thinking.type=disabled` 或 `reasoning_effort=none` 时正文正常。

English:
- **Single model name**: `AgentCatalog.DeepSeekModel = "deepseek-flash"` (`DeepSeekDefaultModel` / `DeepSeekReasonerModel` / `DeepSeekVisionModel` alias it); `StructuredFastModel` / `ResolveDecisionModelName` / `ResolveModelName` all resolve to it; the Docker `AGENTS_MODEL` default and `Web/appsettings.json`'s `Agents:Model` are now `deepseek-flash`.
- **Thinking is a request field**: added `AgentCatalog.DeepSeekThinkingPolicy` (a `System.ClientModel` `PipelinePolicy` at `PipelinePosition.BeforeTransport`) that injects `"thinking":{"type":"enabled|disabled"}` into the top level of the `chat/completions` JSON body (skipped if already present; re-sets `Content-Type: application/json`). `BuildOpenAIChatClient(..., DeepSeekThinking thinking = Default)`: `Default` follows `Agents:ThinkingMode`; decisions / routing / strict JSON / vision / skill generation & repair are **always `Disabled`**, while formal replies follow thinking mode.
- **Why injection is required**: measured against the real endpoint, `deepseek-flash` **defaults to thinking on** and raises no exception — an 8/64-token decision call comes back with empty text ("never speak / 0 downstream"), while `thinking.type=disabled` (or `reasoning_effort=none`) yields normal text.

## 文档
# Docs

中文：`README.md` / `en.md`（DeepSeek 提供方 / `AGENTS_MODEL` / `AGENTS_DECISION_MODEL` / 视觉默认模型 / 小决策段落）；`docs/user-guide.md` / `en.md`（默认模型、视觉模型、配置默认值表）；`docs/agent-execution-algorithm.md` §2.1（小决策改为“固定关思考”）；`MARKETING.md` / `en.md`；`ROADMAP.md` §6.5。
English: `README.md` / `en.md` (DeepSeek provider / `AGENTS_MODEL` / `AGENTS_DECISION_MODEL` / default vision model / small-decision paragraph); `docs/user-guide.md` / `en.md` (default model, vision model, config-default tables); `docs/agent-execution-algorithm.md` §2.1 (decisions now "thinking off"); `MARKETING.md` / `en.md`; `ROADMAP.md` §6.5.

## 测试
# Tests

中文：新增 `tests/AguiGroupChat.Hub.Tests/DeepSeekThinkingPolicyTests.cs`（用自建 Transport 离线抓包：`thinking.type` 确实进了 `chat/completions` 请求体、Content-Type 保持 JSON、恰好注入一次、body 仍是合法 JSON）；`DesktopDecisionPathTests` 更新为“单一模型名 + 请求级思考”。真实端点实测：`deepseek-flash`＋`thinking.type=disabled` 正文正常、不关思考时空正文（curl 探针）；本机 Docker 登录后 `POST /ag-ui/agents/orchestrate` 真实模型调用 **HTTP 200**，返回 7 岗位方案且 `selfCheck` 全绿。`dotnet build AguiGroupChat.slnx` **0 错误**；相关测试类 **32 通过**。
English: new `tests/AguiGroupChat.Hub.Tests/DeepSeekThinkingPolicyTests.cs` (offline capture via a custom Transport: the `thinking.type` flag really lands in the `chat/completions` body, Content-Type stays JSON, injected exactly once, body remains valid JSON); `DesktopDecisionPathTests` updated to "single model name + request-level thinking". Verified against the real endpoint: `deepseek-flash` with `thinking.type=disabled` returns text, while thinking-on returns empty text (curl probe); against the local Docker build, after login `POST /ag-ui/agents/orchestrate` (a real model call) returned **HTTP 200** with a 7-role plan and an all-green `selfCheck`. `dotnet build AguiGroupChat.slnx` reports **0 errors**, and the related test classes passed **32**.

# AG-UI 群聊桌面版 1.0.257 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.257 Release Notes (current Windows desktop release)

**版本说明**：**组织编排落库前自检清单 + 连接双向归一**——一键编排 / 组织架构构建师出稿后，预览里逐条列出**自检清单**（结构 / 连接 / 技能 / 交付闭环），把“只有问题提升没有任务指派”“没人能产出你要的文件”“交付岗被写成等定稿才出文件的门卫”等问题在**确认落库之前**暴露出来；同时把「向下任务指派」与「向上问题提升」两条链**双向补全**。
**Version note**: **Pre-commit self-check list for org orchestration + two-way link normalization** — after one-click orchestration / the org architect produces a plan, the preview now lists a **self-check checklist** item by item (structure / links / skills / delivery closed loop), surfacing problems such as “escalation only, no task assignment”, “nobody can produce the file you asked for” and “the delivery role was written as a gatekeeper that waits for a final draft” **before you confirm**, and it now **backfills both directions** of the assignment / escalation links.

## 变更
# Changes

中文：
- **自检清单（新）**：`AgentOrchestrator.SelfCheck(plan, requirement)` → `IReadOnlyList<OrgPlanCheck>`（每项 `{code, level(error/warn/info), message}`）。`/orchestrate` 与 `/orchestrate/stream`（`done` 事件）都新增 `selfCheck` 字段（`deliveryWarning` 保留不变，向后兼容）。
- **逐条而不是只报第一条**：此前预览最多只显示一条 `deliveryWarning`（三选一 `??`）；现在结构、连接、技能、交付四类问题**全部列清**。检查项：`no_agents` / `dangling_link`（连接指向不存在的岗位，落库会被拒）/ `no_connections`（既无指派也无提升）/ `no_assignment`（只有提升没有指派）/ `no_escalation`（只有指派没有提升）/ `self_escalation` / `agent_without_skill` / `delivery_gap` / `hollow_delivery` / `delivery_gatekeeper`；全通过时给出 `ok` 摘要。
- **连接双向归一**：`AgentOrchestrator.NormalizeConnections`（原 `InferAssignments`）与 `OrgApplyEngine.NormalizeConnections`（原 `EnsureAssignmentsForLeaders`）现在**两个方向**都补：由「提升目标」反推「直接下级」补进 `assignmentIds`（原有），再由「指派目标」反推**唯一**直接上级补进 `escalationAgentId`（新增）。带自环 / 二环保护；被多个上级指派的下级不猜上级；已有显式连接一律保留（只增不改）。
- **前端预览渲染清单**：`formatOrchestrationPlan` 追加「落库前自检清单」（❌ error / ⚠️ warn / ✅ info 分行），状态栏显示 error 计数（`org.orchSelfCheckErr`）。

English:
- **Self-check checklist (new)**: `AgentOrchestrator.SelfCheck(plan, requirement)` → `IReadOnlyList<OrgPlanCheck>` (each `{code, level(error/warn/info), message}`). Both `/orchestrate` and `/orchestrate/stream` (the `done` event) now carry a `selfCheck` field (`deliveryWarning` is unchanged for backward compatibility).
- **List every problem, not just the first**: the preview previously showed at most one `deliveryWarning` (a three-way `??`); now structural, link, skill and delivery problems are **all listed**. Codes: `no_agents` / `dangling_link` (a link points at a role that does not exist — apply would reject it) / `no_connections` (neither assignment nor escalation) / `no_assignment` (escalation only) / `no_escalation` (assignment only) / `self_escalation` / `agent_without_skill` / `delivery_gap` / `hollow_delivery` / `delivery_gatekeeper`; an `ok` summary when all pass.
- **Two-way link normalization**: `AgentOrchestrator.NormalizeConnections` (was `InferAssignments`) and `OrgApplyEngine.NormalizeConnections` (was `EnsureAssignmentsForLeaders`) now fill **both** directions — from an escalation target derive its direct subordinates into `assignmentIds` (as before), and from an assignment target derive its **unique** direct superior into `escalationAgentId` (new). Self-loop / 2-cycle guards; a subordinate assigned by several managers is left alone; existing explicit links are always preserved (additive only).
- **Front-end renders the list**: `formatOrchestrationPlan` appends a “pre-commit self-check” section (❌ error / ⚠️ warn / ✅ info per line) and the status line shows the error count (`org.orchSelfCheckErr`).

## 文档
# Docs

中文：`docs/user-guide.md` / `en.md` §3.7 补充「落库前自检清单」与「连接双向归一」；`docs/customer-service-and-org-builder.md` §B.1/§B.5 更新锚点（`NormalizeConnections`）；`ROADMAP.md` / `ROADMAP.en.md` §1.1 / §6.5 更新。
English: `docs/user-guide.md` / `en.md` §3.7 adds the “pre-commit self-check” and “two-way normalization”; `docs/customer-service-and-org-builder.md` §B.1/§B.5 anchor update (`NormalizeConnections`); `ROADMAP.md` / `ROADMAP.en.md` §1.1 / §6.5 updated.

## 测试
# Tests

中文：新增 `tests/AguiGroupChat.Hub.Tests/OrgPlanSelfCheckTests.cs`（14 例：扁平无连接→`no_connections`；只有提升→`no_assignment`；只有指派→`no_escalation`(warn)；成对→`ok`；悬空连接→error；自环→warn；无技能→warn；要 Word 无产出→`delivery_gap` 与结构问题**并列**列出；`no_agents` 短路；`Parse` 由提升补指派、由指派补提升、保留显式提升、多上级不猜、归一后自检无 error）。回归：`OrchestrationDeliveryLoopTests` **135 通过**、`AgentApiIntegrationTests` **40 通过**（含既有 `Orchestrate_Apply_AddsAssignmentsForLeaders_WhenOnlyEscalationGiven`）；`dotnet build` 0 错误；`node --check`、i18n 审计 **zh=en=1918，missing 0**、`check-i18n-keys`（884）、`check-orphan-keys`（931）均过。
English: new `tests/AguiGroupChat.Hub.Tests/OrgPlanSelfCheckTests.cs` (14 cases: flat/no links → `no_connections`; escalation only → `no_assignment`; assignment only → `no_escalation` (warn); paired → `ok`; dangling link → error; self-loop → warn; no skill → warn; Word wanted with no producer → `delivery_gap` listed **alongside** the structural problem; `no_agents` short-circuits; `Parse` fills assignment from escalation, escalation from assignment, keeps an explicit escalation, does not guess for a shared subordinate, and a normalized plan passes with no error). Regression: `OrchestrationDeliveryLoopTests` **135 passed**, `AgentApiIntegrationTests` **40 passed** (including the existing `Orchestrate_Apply_AddsAssignmentsForLeaders_WhenOnlyEscalationGiven`); `dotnet build` 0 errors; `node --check`, the i18n audit **zh=en=1918, missing 0**, `check-i18n-keys` (884) and `check-orphan-keys` (931) all pass.

# AG-UI 群聊桌面版 1.0.256 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.256 Release Notes (current Windows desktop release)

**版本说明**：**会话导出可选范围与格式**——点 📥 弹出选择：**当前话题 / 整个知聚（所有话题）** × **Markdown / HTML**；HTML 为单文件、内联样式、可直接打印为 PDF。
**Version note**: **Conversation export gains scope and format options** — clicking 📥 opens a chooser: **current topic / whole Circle (all topics)** × **Markdown / HTML**; the HTML is a single file with inline styles, printable straight to PDF.

## 变更
# Changes

中文：
- **入口改为弹窗**：📥 → `openExportChatModal()` → `#exportChatModal`（范围单选 + 格式单选 + 导出 / 取消；Esc / 点遮罩关闭），点导出 → `runChatExport()`。
- **范围**：当前话题（单节） / 整个知聚（按话题分节；MD 用 `## {话题}`，HTML 用 `<h2>`；最多 12 个话题，每话题至多 10 页）。
- **HTML 导出（新）**：**单文件 + 内联样式 + 无外部引用**，离线可开、可打印为 PDF（`@media print` + `print-color-adjust: exact`）；正文用已渲染并消毒的 `renderMarkdown`，数字员工消息带浅蓝左边线。
- **代码整理**：抽出 `fetchTopicMessages`（分页拉全）、`collectConversation`（按范围）、`topicLabel` / `fmtTs` / `msgFootnotes` / `mdMessageLines`（Markdown 与 HTML 共用脚注），`buildConversationMarkdown`（单话题）/ `buildGroupMarkdown` / `buildGroupHtml`（整知聚）。纯前端改动，**无新增接口**。
English:
- **Entry point is now a dialog**: 📥 → `openExportChatModal()` shows `#exportChatModal` (scope radios + format radios + Export / Cancel; Esc / overlay click closes), and Export calls `runChatExport()`.
- **Scope**: current topic (one section) / whole Circle (sectioned by topic; `## {topic}` in Markdown, `<h2>` in HTML; up to 12 topics, up to 10 pages each).
- **HTML export (new)**: a **single file with inline styles and no external references**, openable offline and printable to PDF (`@media print` + `print-color-adjust: exact`); bodies use already-rendered, sanitized `renderMarkdown`, and digital-employee messages get a light-blue left border.
- **Refactor**: extracted `fetchTopicMessages` (paging), `collectConversation` (by scope), `topicLabel` / `fmtTs` / `msgFootnotes` / `mdMessageLines` (footnotes shared by Markdown and HTML), `buildConversationMarkdown` (single topic) / `buildGroupMarkdown` / `buildGroupHtml` (whole Circle). Front-end only, **no new endpoint**.

## 文档
# Docs

中文：`docs/user-guide.md` / `en.md` §2.9.6 重写（弹窗选择、范围 / 格式、HTML 单文件）。
English: `docs/user-guide.md` / `en.md` §2.9.6 rewritten (the chooser, scope / format, single-file HTML).

## 测试
# Tests

中文：本轮**无 C# 改动**（纯前端 + 文案），`dotnet build` 与 1.0.255 一致（0 错误；MSI 步骤的完整 publish 也 0 错误）。`node --check`、i18n 审计 **zh=en=1916，missing 0**、`check-i18n-keys`（884 处引用）、`check-orphan-keys`（929 处 `t()` 引用）均过。浏览器实测（重建 web 后，管理员，真实数据）：📥 弹出选择；选「整个知聚 + Markdown」→ `chat-group_direct_5f….md`（23371 字节，**3 个话题节**、42 条）；选「整个知聚 + HTML」→ `chat-group_direct_5f….html`（35850 字节，`<!doctype html>` ✓、内联 `<style>` ✓、**无外部引用** ✓、42 个 `.msg` ✓、3 个 `<h2>` ✓）；将该 HTML 单独渲染到空页 → h1「与 组织架构构建师 的单聊」、42 条消息、**0 渲染错误**；**无页面报错**。
English: **no C# changes** this round (frontend + copy only), so `dotnet build` matches 1.0.255 (0 errors; the MSI step's full publish also reports 0 errors). `node --check`, the i18n audit **zh=en=1916, missing 0**, `check-i18n-keys` (884 references) and `check-orphan-keys` (929 `t()` references) all pass. Browser-verified (after rebuilding web, as admin, real data): 📥 opens the chooser; "whole Circle + Markdown" → `chat-group_direct_5f….md` (23371 bytes, **3 topic sections**, 42 messages); "whole Circle + HTML" → `chat-group_direct_5f….html` (35850 bytes, `<!doctype html>` ✓, inline `<style>` ✓, **no external references** ✓, 42 `.msg` ✓, 3 `<h2>` ✓); rendering that HTML alone in a blank page → h1 "与 组织架构构建师 的单聊", 42 messages, **0 render errors**; **no page errors**.

# AG-UI 群聊桌面版 1.0.255 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.255 Release Notes (current Windows desktop release)

**版本说明**：**会话导出（Markdown）**——聊天头部新增「📥 导出本话题会话」，把当前知聚的当前话题**全量**导出为单个 `.md`（分页拉全历史，绕过客户端内存裁剪），并在报告里带上数字员工回复的 💰 用量与 📎 引用来源。
**Version note**: **Conversation export (Markdown)** — the chat header gains "📥 Export this topic", dumping the current Circle's current topic **in full** to a single `.md` (paging through history to bypass the client's memory trimming) and including each digital-employee reply's 💰 usage and 📎 sources.

## 变更
# Changes

中文：
- **入口**：`.chat-header-ops` 新增 `#exportChatBtn`（`chat.exportTip`，📥）——与其它头部按钮一致，进入知聚后可用（未进入时 `setDisabledWithReason(..., "chat.needGroup")`，四处状态切换均已同步）；点击 → `exportCurrentTopic()`。
- **拉全历史（关键）**：客户端消息是**分页加载且有内存裁剪**（`MAX_MESSAGES`），直接导出内存列表会**不完整**；改为按既有 `GET /ag-ui/group/{groupId}/topics/{topicId}/messages?count=100`（`count` 上限 100）以 `before` 游标向前翻页拉全（至多 20 页 ≈ 2000 条，超出在报告头标注 `chat.exportTruncated`），去重 + 按时间排序；服务端过滤（已撤回 / 可见性）与界面一致。**无需新增接口**。
- **文档结构**：`# {知聚名} · {话题名}`（主话题用 `topic.mainShort`）+ meta 行（范围 / 条数 / 导出时间）；每条消息 `### {昵称} · {时间}`（数字员工 🤖），正文原样 Markdown，回复标 `> ↩ {消息 ID}`，附件列 `📁 \`文件名\``；单聊同路径（只有一个主话题）。
- **透明度延续**：有 `usageJson` 时附 `> 💰 输入/输出/思考 token · 成本`；有 `citationsJson` 时附 `> 📎 引用来源：库名/文档名、…`——与消息内「执行过程」同源（1.0.253 / 1.0.254）。
English:
- **Entry point**: `.chat-header-ops` gains `#exportChatBtn` (`chat.exportTip`, 📥) — consistent with the other header buttons, enabled after entering a Circle (greyed out with `setDisabledWithReason(..., "chat.needGroup")` otherwise; all four state transitions updated); clicking it calls `exportCurrentTopic()`.
- **Full history (the key part)**: client messages are **paginated and memory-trimmed** (`MAX_MESSAGES`), so exporting the in-memory list would be **incomplete**; instead the export pages **backwards** through the existing `GET /ag-ui/group/{groupId}/topics/{topicId}/messages?count=100` (`count` capped at 100) using the `before` cursor (up to 20 pages ≈ 2000 messages, beyond which the report header notes `chat.exportTruncated`), then dedupes and sorts by time; server-side filtering (recalled / visibility) matches the UI. **No new endpoint needed.**
- **Document structure**: `# {Circle name} · {topic name}` (the main topic uses `topic.mainShort`) plus a meta line (range / count / exported time); each message is `### {nickname} · {time}` (digital employees 🤖), the body is Markdown as-is, replies are marked `> ↩ {message ID}`, and attachments are listed as `📁 \`filename\``; direct chats follow the same path (only the main topic).
- **Transparency carried over**: when `usageJson` exists, a `> 💰 in/out/reasoning tokens · cost` line is added; when `citationsJson` exists, a `> 📎 Sources: KB/file, …` line is added — the same data as the in-message Execution process (1.0.253 / 1.0.254).

## 文档
# Docs

中文：`docs/user-guide.md` / `en.md` 新增 §2.9.6「会话导出（📥 Markdown）」；§1.2.3（头部操作区）与 §2.1.2（进入知聚后启用）同步补上 `#exportChatBtn`。
English: `docs/user-guide.md` / `en.md` add §2.9.6 "Conversation export (📥 Markdown)"; §1.2.3 (header operations) and §2.1.2 (enabled after entering) both mention `#exportChatBtn`.

## 测试
# Tests

中文：本轮**无 C# 改动**（纯前端 + 文案），`dotnet build` 与 1.0.254 一致（0 错误）。`node --check`、i18n 审计 **zh=en=1907，missing 0**、`check-i18n-keys`（876 处引用）、`check-orphan-keys`（928 处 `t()` 引用）均过。浏览器实测（重建 web 后，管理员，真实数据）：未进入知聚时📥**置灰**、进入后**启用**；点导出 → 下载 `chat-group_direct_5f...-main.md`（4697 字节），头部为「# 与 组织架构构建师 的单聊 · 主话题」+ meta「消息范围：2026-09-23 16:08 ~ 2026-10-14 11:51（共 16 条）」，正文含 `### David · …` 与 `### 🤖 组织架构构建师 · …`；另以合成消息直接测 `buildConversationMarkdown()` → 附件 `📁 \`brief.pdf\``、回复 `↩ m1`、`💰 输入 1,234 · 输出 567 · 思考 89 · 成本 $0.0125`、`📎 引用来源：产品手册/manual.pdf、David` 四项断言全部通过；**无页面报错**。
English: **no C# changes** this round (frontend + copy only), so `dotnet build` matches 1.0.254 (0 errors). `node --check`, the i18n audit **zh=en=1907, missing 0**, `check-i18n-keys` (876 references) and `check-orphan-keys` (928 `t()` references) all pass. Browser-verified (after rebuilding web, as admin, real data): 📥 is **greyed out** before entering a Circle and **enabled** after; clicking export downloads `chat-group_direct_5f...-main.md` (4697 bytes) with the header "# 与 组织架构构建师 的单聊 · 主话题" and meta "消息范围：2026-09-23 16:08 ~ 2026-10-14 11:51（共 16 条）", the body containing `### David · …` and `### 🤖 组织架构构建师 · …`; separately, calling `buildConversationMarkdown()` with synthetic messages passes all four assertions (attachment `📁 \`brief.pdf\``, reply `↩ m1`, `💰 输入 1,234 · 输出 567 · 思考 89 · 成本 $0.0125`, `📎 引用来源：产品手册/manual.pdf、David`); **no page errors**.

# AG-UI 群聊桌面版 1.0.254 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.254 Release Notes (current Windows desktop release)

**版本说明**：**每条回答带「📎 引用来源」**——数字员工回复的「执行过程」里列出本次**实际注入**的知识库片段 / 群记忆 / 个人记忆命中（可展开查看摘录片段）。
**Version note**: **Every reply now shows its "📎 Sources"** — the digital employee's "Execution process" lists the knowledge-base snippets / group memories / personal memories **actually injected** for this reply (expandable to read the excerpts).

## 变更
# Changes

中文：
- **采集**：新增 `CitationCollector`（AsyncLocal，同 `UsageAccumulator` 作用域约定）——在**唯一**的记忆 / 知识库检索注入处 `MemoryContextProvider.ProvideAIContextAsync` 埋点：只有**确实被注入**的命中才记录（群记忆 `memory` / 个人记忆 `person` / 知识库 `kb`），同来源去重、单条摘录截 400 字、至多 12 条。
- **回档**：网关最外层运行结束时 `AttachCitationsAsync` → `GroupHub.AttachCitationsAsync` → 消息 `citations_json`（`{items:[{kind,title,detail,score,snippet}]}`）；无来源不写。
- **存储**：`GroupMessage.CitationsJson` 继续沿用 `usage_json` 同款模式（SQLite / MySQL `EnsureColumn` + Postgres `ADD COLUMN IF NOT EXISTS`，**老库自动补列、零迁移**；两种关系库读写改 + Redis 匿名化 + 内存快照同步）。
- **前端**：`msgDom()` 的「执行过程」内新增 `.cite-card`（在调用链之后、用量之前）——折叠标题「📎 引用来源 + 条数」，每项为类型胶囊（知识库 / 群记忆 / 个人记忆）+ 标题（库名 / 发送者）+ 副标题（文档名）+ 相似度，下方为**摘录片段**；随快照与 `TEXT_MESSAGE_END` 两路解析（`parseCitationsJson`），空 / 缺 / 非法不渲染。
- **价值**：回答可**核对来源**（点开看片段）；存储 / 记忆没被用上时一眼可见（解释了「为什么没用到知识库」类疑问），与「本次用量」一起补全单次回答的透明度。
English:
- **Capture**: a new `CitationCollector` (AsyncLocal, same scoping convention as `UsageAccumulator`) — instrumented at the **single** memory / KB retrieval-and-injection site `MemoryContextProvider.ProvideAIContextAsync`: only hits **actually injected** are recorded (group memory `memory` / personal memory `person` / knowledge base `kb`), deduped by source, excerpts clamped to 400 chars, at most 12 items.
- **Archive**: at the end of the outermost run, `AttachCitationsAsync` → `GroupHub.AttachCitationsAsync` → the message's `citations_json` (`{items:[{kind,title,detail,score,snippet}]}`); nothing is written when there are no sources.
- **Storage**: `GroupMessage.CitationsJson` follows the same `usage_json` pattern (SQLite / MySQL `EnsureColumn` plus Postgres `ADD COLUMN IF NOT EXISTS`, **auto-added on old databases, zero migration**; the two relational stores read/write/update, Redis anonymization and the in-memory snapshot are all in sync).
- **Front end**: `msgDom()`'s "Execution process" gains a `.cite-card` (after the call chain, before the usage row) — the summary reads "📎 Sources + count", each item is a kind pill (knowledge base / group memory / personal memory) + title (KB name / sender) + subtitle (file name) + score, with the **excerpt** below; parsed from both the snapshot and `TEXT_MESSAGE_END` (`parseCitationsJson`), empty / missing / invalid renders nothing.
- **Value**: replies can be **verified against their sources** (open to read the excerpt); when storage / memory was not used it becomes immediately visible (explaining "why didn't it use the knowledge base?"), completing single-reply transparency together with the usage row.

## 文档
# Docs

中文：`docs/user-guide.md` / `en.md` §2.10.1（过程体新增「引用来源卡」）。
English: `docs/user-guide.md` / `en.md` §2.10.1 (the process body gains the "sources card").

## 测试
# Tests

中文：新增 `CitationCollectorTests`（去重 / 摘录截断 / 条数上限）。相关用例合跑 **19/19**。`node --check`、i18n 审计 **zh=en=1902，missing 0**、`check-i18n-keys`（875 处引用）、`check-orphan-keys`（923 处 `t()` 引用）、`dotnet build` **0 错误**均过。浏览器实测（重建 web 后）：`parseCitationsJson`（三项 / 坏 JSON → null）；直接调 `msgDom()` 渲一条带引用的探针消息 → `.cite-card` **存在且在 `.process` 内**，类型胶囊为「知识库 / 群记忆 / 个人记忆」、计数徽标「3」、摘录片段已渲染；无 / 空引用均不渲染；**无页面报错**。数据库侧：Postgres `agui_messages` 已自动出现 `citations_json` 列（与 `plan_json` / `usage_json` 并列）。
English: adds `CitationCollectorTests` (dedupe / excerpt clamping / item cap). Related tests run **19/19**. `node --check`, the i18n audit **zh=en=1902, missing 0**, `check-i18n-keys` (875 references), `check-orphan-keys` (923 `t()` references) and `dotnet build` **0 errors** all pass. Browser-verified (after rebuilding web): `parseCitationsJson` (three items / bad JSON → null); calling `msgDom()` directly on a probe message with citations → the `.cite-card` **exists and sits inside `.process`**, kind pills read "Knowledge base / Group memory / Personal memory", the count badge is "3" and the excerpts render; none / empty render nothing; **no page errors**. Database side: Postgres `agui_messages` now has the `citations_json` column (alongside `plan_json` / `usage_json`).

# AG-UI 群聊桌面版 1.0.253 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.253 Release Notes (current Windows desktop release)

**版本说明**：**每条回答带「本次用量」**——数字员工回复的「执行过程」里新增一行 `💰 本次用量 输入/输出/思考 token · 成本`，按本次运行（含子代理）汇总，随消息落库、刷新后仍在。
**Version note**: **Every reply now carries its own usage** — the digital employee's "Execution process" gains a `💰 Usage for this reply` row (input/output/reasoning tokens · cost), totalled over the run (including sub-agents), persisted with the message so it survives a refresh.

## 变更
# Changes

中文：
- **采集**：新增 `UsageAccumulator`（AsyncLocal）——模型调用处 `UsageCaptureChatClient.Record` 在落统计库的同时累加到「本次回答」；网关 `InvokeCoreAsync` **最外层运行创建、嵌套（子代理 / 交付兜底）沿用同一实例**，故总量含全部子调用；结束 / finally 正确恢复外层。
- **回档**：运行结束（与技能链同道）调 `AttachUsageAsync` → `GroupHub.AttachUsageAsync` → 消息 `usage_json`（`{inputTokens,outputTokens,reasoningTokens,cost,currency}`；成本按 `Agents:Pricing` 估算，未配单价则 cost/currency 为 null；全零不写）。
- **存储**：`GroupMessage.UsageJson` 随 `plan_json` 同款落库——SQLite / MySQL `EnsureColumn` + Postgres `ADD COLUMN IF NOT EXISTS` 老库自动补列（**无需迁移**），Relational / Postgres 读改写、Redis（整对象 JSON）与内存快照（`PersistedMessage`）同步。
- **前端**：`msgDom()` 的「执行过程」内新增 `.usage-card`（在调用链之后）——`💰 本次用量 输入 N · 输出 N · 思考 N · 成本 X`；`usageJson` 由快照与 `TEXT_MESSAGE_END` 两路解析（`parseUsageJson`），全零 / 缺失 / 非法不渲染。
English:
- **Capture**: a new `UsageAccumulator` (AsyncLocal) — `UsageCaptureChatClient.Record` accumulates into "this reply" while also writing the usage store; `AgentGateway.InvokeCoreAsync` **creates it for the outermost run and nested runs (sub-agents / delivery fallback) reuse the same instance**, so the total includes every sub-call; the outer value is restored at the end / in `finally`.
- **Archive**: at the end of the run (same path as the skill chain) `AttachUsageAsync` → `GroupHub.AttachUsageAsync` → the message's `usage_json` (`{inputTokens,outputTokens,reasoningTokens,cost,currency}`; cost is estimated from `Agents:Pricing`, with cost/currency null when no pricing is configured; all-zero is not written).
- **Storage**: `GroupMessage.UsageJson` is persisted like `plan_json` — SQLite / MySQL `EnsureColumn` plus Postgres `ADD COLUMN IF NOT EXISTS` auto-add the column on old databases (**no migration**), with Relational / Postgres read-write-update, Redis (whole-object JSON) and the in-memory snapshot (`PersistedMessage`) all in sync.
- **Front end**: `msgDom()`'s "Execution process" gains a `.usage-card` (after the call chain) — `💰 Usage for this reply in N · out N · reasoning N · cost X`; `usageJson` is parsed from both the snapshot and `TEXT_MESSAGE_END` (`parseUsageJson`), and all-zero / missing / invalid renders nothing.

## 文档
# Docs

中文：`docs/user-guide.md` / `en.md` §2.10.1（过程体新增「本次用量行」）。
English: `docs/user-guide.md` / `en.md` §2.10.1 (the process body gains the "usage row").

## 测试
# Tests

中文：新增 `UsageAccumulatorTests`（累加 + 非正数忽略 + AsyncLocal 默认空 / 可恢复）。相关/邻近用例合跑 **40/40**。`node --check`、i18n 审计 **zh=en=1898，missing 0**、`check-i18n-keys`（875 处引用）、`check-orphan-keys`（919 处 `t()` 引用）、`dotnet build` **0 错误**均过。浏览器实测（重建 web 后）：`parseUsageJson`（正/空/零/坏 JSON）与 `renderUsageCard` 输出正确；直接调用 `msgDom()` 渲一条带 usage 的探针消息 → `.usage-card` **存在且在 `.process` 内**，文案「💰 本次用量 输入 1,234 · 输出 567 · 思考 89 · 成本 $0.0125」；无 usage / 零 usage 均不渲染；**无页面报错**。数据库侧：Postgres `agui_messages` 已自动出现 `usage_json` 列。
English: adds `UsageAccumulatorTests` (accumulation + non-positive ignored + AsyncLocal null by default / restorable). Related and neighbouring tests run **40/40**. `node --check`, the i18n audit **zh=en=1898, missing 0**, `check-i18n-keys` (875 references), `check-orphan-keys` (919 `t()` references) and `dotnet build` **0 errors** all pass. Browser-verified (after rebuilding web): `parseUsageJson` (valid / empty / zero / bad JSON) and `renderUsageCard` output are correct; calling `msgDom()` directly on a probe message with usage → the `.usage-card` **exists and sits inside `.process`**, reading "💰 Usage for this reply in 1,234 · out 567 · reasoning 89 · cost $0.0125"; no usage / zero usage render nothing; **no page errors**. Database side: Postgres `agui_messages` now has the `usage_json` column automatically.

# AG-UI 群聊桌面版 1.0.252 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.252 Release Notes (current Windows desktop release)

**版本说明**：**绩效页新增「知聚范围」**——选一个知聚即可看该知聚内的用量（调用 / token / 成本）；👍👎 / 好评率不按知聚记录，显示「—」。
**Version note**: **The performance tab gains a "group scope"** — pick a group to see usage (calls / tokens / cost) inside it; 👍👎 / positive rate are not recorded per group and show "—".

## 变更
# Changes

中文：
- **后端**：`GET /ag-ui/admin/agent-performance` 新增可选 **`?groupId=`**——非空时用量取自**按知聚聚合表**（`agui_usage_group`），只列该知聚内有用量的员工（`AgentPerformance.Aggregate` 新增 `seedAllAgents=false`，避免「该群闲置」噪声），且**不注入反馈**，故 `up`/`down` 恒为 0、`positiveRate` 为 `null`；响应新增 **`scope`**（`{groupId,name}`，空即全部）。**向后兼容**（不传 `groupId` 行为不变）。
- **后端**：新增 `GET /ag-ui/admin/groups`（Operator+）→ `{groups:[{groupId,name}]}`（按名称排序），供下拉选项。
- **前端**：绩效页头部新增**知聚下拉**（懒加载一次；默认「全部知聚」）；选中后重新拉取带 `groupId` 的数据，表格 **👍 / 👎 / 好评率 / 关注 列均显示「—」**，下方提示「知聚范围「X」：用量取自该知聚；👍👎 / 好评率不按知聚记录，显示「—」」，且**「仅看需关注」自动置灰**（该范围下无反馈）。
English:
- **Backend**: `GET /ag-ui/admin/agent-performance` gains an optional **`?groupId=`** — when set, usage is taken from the **per-group aggregate table** (`agui_usage_group`), listing only employees with usage in that group (`AgentPerformance.Aggregate` gains `seedAllAgents=false` to avoid "idle in this group" noise) and **injecting no feedback**, so `up`/`down` are always 0 and `positiveRate` is `null`; the response gains **`scope`** (`{groupId,name}`; empty means all). **Backward compatible** (omitting `groupId` behaves as before).
- **Backend**: a new `GET /ag-ui/admin/groups` (Operator+) returns `{groups:[{groupId,name}]}` (sorted by name) for the dropdown.
- **Front end**: the perf tab header gains a **group dropdown** (lazily populated once; defaults to "All groups"); selecting one refetches with `groupId`, the table shows **"—" for 👍 / 👎 / positive rate / attention**, a hint is shown below ("Group scope \"X\": usage is taken from this group; 👍👎 / positive rate are not recorded per group and show \"—\""), and **"Flagged only" is greyed out** (no feedback in this scope).

## 文档
# Docs

中文：`docs/user-guide.md` / `en.md` §8.1.5（知聚范围）与 §9.2.8（`agent-performance` 补 `?groupId` / `scope`，新增 `/groups` 行）。
English: `docs/user-guide.md` / `en.md` §8.1.5 (group scope) and §9.2.8 (the `agent-performance` row notes `?groupId` / `scope`; new `/groups` row).

## 测试
# Tests

中文：`AdminApiTests` 新增 `AgentPerformance_ScopesByGroup`（记录 `group_p1` / `group_p2` 两次用量：全量 `agent_p` calls=2；`?groupId=group_p1` 只算 1 次 / 150 token、`positiveRate` 为 null、无 0 调用行）。相关类合跑 **26/26**。`node --check`、i18n 审计 **zh=en=1893，missing 0**、`check-i18n-keys`（875 处引用）、`check-orphan-keys`（914 处 `t()` 引用）、`dotnet build` **0 错误**均过。浏览器实测（重建 web 后，管理员，真实数据）：下拉 **10 个选项**（首项「全部知聚」，含 hello / test 等）；选一个知聚 → 提示范围文案、**「仅看需关注」置灰**、请求带上了 `scope.groupId`；注入合成范围数据 → 行单元格为 **「甲 / 3 / 300 / — / — / — / — / 2026-10-15 / —」**（成本 / 👍 / 👎 / 好评率 / 关注 均「—」）；切回「全部知聚」→ **17 行**、开关恢复可用；**无页面报错**。
English: `AdminApiTests` gains `AgentPerformance_ScopesByGroup` (recording two usages for `group_p1` / `group_p2`: all-scope `agent_p` has calls=2; `?groupId=group_p1` counts 1 call / 150 tokens, `positiveRate` null, and no zero-call rows). The related classes run **26/26**. `node --check`, the i18n audit **zh=en=1893, missing 0**, `check-i18n-keys` (875 references), `check-orphan-keys` (914 `t()` references) and `dotnet build` **0 errors** all pass. Browser-verified (after rebuilding web, as admin, real data): the dropdown has **10 options** (first "All groups", including hello / test); selecting one shows the scope hint, **greys out "Flagged only"** and sends `scope.groupId`; injecting synthetic scope data renders the cells **"甲 / 3 / 300 / — / — / — / — / 2026-10-15 / —"** (cost / 👍 / 👎 / positive rate / attention all "—"); back to "All groups" → **17 rows** with the toggle re-enabled; **no page errors**.

# AG-UI 群聊桌面版 1.0.251 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.251 Release Notes (current Windows desktop release)

**版本说明**：**组织体检带「环比」 + 绩效页可筛选**——KPI / 报告对比上一同长度窗口（▲/▼ + %），绩效页新增名称搜索与「仅看需关注」。
**Version note**: **Period-over-period in org health + filters on the perf tab** — KPIs / reports compare against the previous window of the same length (▲/▼ + %), and the perf tab gains a name search and a "flagged only" toggle.

## 变更
# Changes

中文：
- **环比（后端）**：`GET /ag-ui/admin/org-health` 额外计算**上一同长度窗口**（`from-1` 往前 `days` 天）的员工绩效，Markdown 报告「一、总览」新增一条**环比行**（调用 / Token / 成本 / 好评率），结构化数据新增 **`prev`**（`from` / `to` / `summary{agents,calls,totalTokens,cost,positiveRate,flagged}`），**向后兼容**。空值/上期为 0 时标「—」或「持平」/「新增」。
- **环比（前端）**：图表弹窗的 KPI 卡片在数值下显示**环比**（▲/▼/≈ + 百分比；上期为 0 或无从可比则不显示；成本卡未配单价时不显示），meta 尾部标注「环比上一 N 天」；**HTML 报告**同步（卡片 + 标题行）。
- **绩效页筛选**：`#adminPerfView` 头部新增**名称 / ID 搜索框**（带 × 清除）与 **「⚠️ 仅看需关注」开关**——**纯前端过滤**（`perfData` 缓存，不重拉数），筛选生效时 meta 尾部显示「显示 N / 总数」，无匹配时给空态文案；原「统计范围 / 刷新」逻辑不变。为此把 `loadAgentPerformance` 拆为**拉取** + `renderPerfRows`（按筛选重绘）。
English:
- **Period-over-period (backend)**: `GET /ag-ui/admin/org-health` additionally computes the **previous same-length window** (`days` ending the day before `from`); the Markdown report's "Overview" gains a **period-over-period line** (calls / tokens / cost / positive rate), and the structured data gains **`prev`** (`from` / `to` / `summary{agents,calls,totalTokens,cost,positiveRate,flagged}`), **backward compatible**. Nulls / a zero previous period show "—" or "持平" / "新增".
- **Period-over-period (front end)**: the chart dialog's KPI cards show a **delta** under each value (▲/▼/≈ + %; omitted when the previous period is 0 or not comparable, and on the cost card when no pricing is configured), with the meta line noting "vs previous N days"; the **HTML report** mirrors it (cards + header line).
- **Perf-tab filters**: the `#adminPerfView` header gains a **name / ID search box** (with an × clear) and a **"⚠️ Flagged only" toggle** — **front-end filtering** (`perfData` cached, no refetch); when active the meta line appends "Showing N / total", and an empty state is shown when nothing matches. The existing range / refresh logic is unchanged. To support this, `loadAgentPerformance` was split into **fetch** + `renderPerfRows` (re-render by filter).

## 文档
# Docs

中文：`docs/user-guide.md` / `en.md` §8.1.5（环比 + 绩效页筛选）与 §9.2.8（`org-health` 行补 `prev`）；`ROADMAP.md` §6.5 补环比与筛选。
English: `docs/user-guide.md` / `en.md` §8.1.5 (period-over-period + perf-tab filters) and §9.2.8 (the `org-health` row notes `prev`); `ROADMAP.md` §6.5 records both.

## 测试
# Tests

中文：`OrgHealthReportTests` 新增 `BuildMarkdown_WithPrevStats_ShowsPeriodOverPeriodLine`（断言「环比上一 30 天」「调用 ▲100%」「好评率 ▲」）；`OrgHealth_ReturnsStructuredDataForCharts` 补断言 `prev.summary` 含 `agents` / `calls` / `totalTokens` / `cost` / `flagged` 与 `prev.from` 非空。相关类合跑 **19/19**。`node --check`、i18n 审计 **zh=en=1891，missing 0**、`check-i18n-keys`（875 处引用）、`check-orphan-keys`（912 处 `t()` 引用）、`dotnet build` **0 错误**均过。浏览器实测（重建 web 后，管理员，真实数据）：图表弹窗 KPI 出现 **4 个环比**（如 ▲25% / ▲707% / ▼83%；成本/好评率因未配单价与无反馈不出），meta「2026-09-17 ~ 2026-10-16 · 环比上一 30 天」；HTML 报告含 **4 个 `card-d`** 与环比提示；绩效页 17 行 → 搜「ppt」**8 行**（meta「显示 8 / 17」）→ 开「仅看需关注」**2 行**（meta「显示 2 / 17」）→ 关闭恢复 **17 行**；**无页面报错**。
English: `OrgHealthReportTests` gains `BuildMarkdown_WithPrevStats_ShowsPeriodOverPeriodLine` (asserting "环比上一 30 天", "调用 ▲100%" and "好评率 ▲"); `OrgHealth_ReturnsStructuredDataForCharts` now asserts `prev.summary` carries `agents` / `calls` / `totalTokens` / `cost` / `flagged` and that `prev.from` is non-empty. The related classes run **19/19**. `node --check`, the i18n audit **zh=en=1891, missing 0**, `check-i18n-keys` (875 references), `check-orphan-keys` (912 `t()` references) and `dotnet build` **0 errors** all pass. Browser-verified (after rebuilding web, as admin, real data): the chart KPIs show **4 deltas** (e.g. ▲25% / ▲707% / ▼83%; cost and positive rate are omitted with no pricing and no feedback), meta "2026-09-17 ~ 2026-10-16 · vs previous 30 days"; the HTML report has **4 `card-d`** plus the period hint; the perf tab goes 17 rows → searching "ppt" gives **8 rows** (meta "Showing 8 / 17") → enabling "Flagged only" gives **2 rows** (meta "Showing 2 / 17") → turning it off restores **17 rows**; **no page errors**.

# AG-UI 群聊桌面版 1.0.250 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.250 Release Notes (current Windows desktop release)

**版本说明**：**AI 角色管理新增「📊 看绩效」入口**——从角色列表一键跳到管理控制台的「员工绩效」并**自动定位 + 高亮**该员工，打通「发现→归因→编辑」闭环。
**Version note**: **AI Role Management gains a "📊 View performance" action** — jump from the role list straight to the admin console's "Employee performance" tab with that employee's row **located and highlighted**, closing the "spot → attribute → edit" loop.

## 变更
# Changes

中文：
- **角色列表操作列**（`#agentList` 每行）：在 📤 导出 / 💬 单聊 之后新增 **📊 `data-act="perf"`**（title＝`agent.perfTip`＝「看绩效」）——**仅生效角色 ≥ Operator 时渲染**（`isOperatorPlus()`）；无权限时点击提示 `admin.adminOnly`。
- **跳转与定位** `openAgentPerformance(agentId)`：关掉角色管理窗 → 按角色刷新页签可见性 → 打开控制台 `#adminModal` → `switchAdminTab("perf")`（同步置入「加载中」占位并异步拉取）→ `waitForPerfRow` 轮询（≤ 4s）等目标行出现 → `scrollIntoView` 居中 + `.perf-row-hit` 高亮动画（2.4s 后自动移除）；未找到则提示 `admin.perfNotFound`。
- **绩效行可定位**：`#adminPerfRows` 每行新增 `data-agent-id`（便于定位 / 高亮 / 未来扩展）。
- **样式**：新增 `.perf-row-hit` + `@keyframes perfHit`（主题色渐隐高亮）。纯前端改动，**无新增接口**。
English:
- **Role list action column** (each row in `#agentList`): after 📤 export / 💬 direct chat, a new **📊 `data-act="perf"`** (title = `agent.perfTip` = "View performance") — **rendered only when the effective role is Operator or above** (`isOperatorPlus()`); without permission a click shows `admin.adminOnly`.
- **Jump and locate** `openAgentPerformance(agentId)`: closes the role-management dialog → refreshes tab visibility by role → opens the console `#adminModal` → `switchAdminTab("perf")` (synchronously puts in the "loading" placeholder and fetches asynchronously) → `waitForPerfRow` polls (≤ 4s) until the target row appears → `scrollIntoView` centred + a `.perf-row-hit` highlight animation (auto-removed after 2.4s); if not found it shows `admin.perfNotFound`.
- **Locatable performance rows**: each row in `#adminPerfRows` now carries `data-agent-id` (for locating / highlighting and future use).
- **Styles**: added `.perf-row-hit` + `@keyframes perfHit` (accent-colour fade highlight). Front-end only, **no new endpoint**.

## 文档
# Docs

中文：`docs/user-guide.md` / `en.md` §3.1.4（行内操作新增 📊 看绩效）。
English: `docs/user-guide.md` / `en.md` §3.1.4 (the inline actions now include 📊 View performance).

## 测试
# Tests

中文：本轮**无 C# 改动**（仅前端 + 文案），`dotnet build` 与 1.0.249 一致（0 错误）。`node --check`（`app.js` / `zh.js` / `en.js`）、i18n 审计 **zh=en=1886，missing 0**、`check-i18n-keys`（873 处引用）、`check-orphan-keys`（909 处 `t()` 引用）均过。浏览器实测（重建 web 后，管理员，真实数据）：角色列表每行存在 📊 按钮；点首行（`word_delivery`）→ 控制台打开、角色窗关闭、「员工绩效」页签 `on`、绩效视图可见、**目标行已定位**（`Word交付专员 110 / 267 / — / 0 / 0 / — / 2026-09-24`，全表 17 行）且**高亮已触发**；**无页面报错**。
English: **no C# changes** this round (frontend + copy only), so `dotnet build` matches 1.0.249 (0 errors). `node --check` (`app.js` / `zh.js` / `en.js`), the i18n audit **zh=en=1886, missing 0**, `check-i18n-keys` (873 references) and `check-orphan-keys` (909 `t()` references) all pass. Browser-verified (after rebuilding web, as admin, real data): every role-list row has the 📊 button; clicking the first row (`word_delivery`) → the console opens, the role dialog closes, the "Employee performance" tab is `on`, the performance view is visible, the **target row is located** (`Word交付专员 110 / 267 / — / 0 / 0 / — / 2026-09-24`, 17 rows in all) and the **highlight fired**; **no page errors**.

# AG-UI 群聊桌面版 1.0.249 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.249 Release Notes (current Windows desktop release)

**版本说明**：**图表视图可导出单文件 HTML 报告**——把 KPI / 柱状 / 环形 / 按日趋势 / 需关注项 / 明细表打包成一个**自带样式与图表**的 HTML 文件，**离线可开、可直接打印成 PDF**。
**Version note**: **The chart view exports a single-file HTML report** — packaging the KPIs / bars / donut / daily trend / attention list / detail table into one HTML file with **styles and charts inlined**, which **opens offline and prints straight to PDF**.

## 变更
# Changes

中文：
- **前端**：`#orgHealthModal` 新增 **「🖨️ 导出 HTML」**（与「📄 组织体检报告」「📥 导出 CSV」「关闭」并列）→ 下载 `org-health-report-<yyyymmdd>.html`。
- **报告内容**：标题 + 窗口 + 生成时间；**KPI 卡片**；**Top 12 柱状**（反射当前维度与指标）；**好评率环形** + **按日趋势折线**（独立内联 SVG，写死颜色，不依赖页面 CSS 变量）；**需关注项**；**当前维度明细表**。
- **自包含**：样式内联在 `<style>`、图表为内联 SVG，**无任何外部引用**（无 `src`/`href` 指向网络）；`<html lang>` 跟随当前语言；带打印样式（`@media print` + `print-color-adjust: exact`）。
- **重构**：抽取 `orgHealthTable()`（当前维度表头 / 行）供 **CSV 与 HTML 报告共用**，保证两者口径一致；新增 `reportDonutSvg` / `reportTrendSvg`（报告用独立 SVG）。纯前端改动，**无新增接口**。
English:
- **Front end**: `#orgHealthModal` gains **"🖨️ Export HTML"** (alongside "📄 Org health report", "📥 Export CSV", "Close") → downloads `org-health-report-<yyyymmdd>.html`.
- **Report content**: title + window + generated time; **KPI cards**; the **Top-12 bars** (reflecting the current dimension and metric); a **positive-rate donut** + **daily trend line** (standalone inline SVGs with hardcoded colours, not depending on the page's CSS variables); the **needs-attention list**; and the **current dimension's detail table**.
- **Self-contained**: styles are inlined in `<style>`, charts are inline SVGs, with **no external references** (no `src`/`href` pointing to the network); `<html lang>` follows the current language; print styles are included (`@media print` + `print-color-adjust: exact`).
- **Refactor**: extracted `orgHealthTable()` (current dimension's header / rows) **shared by the CSV and the HTML report** so both stay consistent; added `reportDonutSvg` / `reportTrendSvg` (standalone SVGs for the report). Front-end only, **no new endpoint**.

## 文档
# Docs

中文：`docs/user-guide.md` / `en.md` §8.1.5（新增 HTML 报告导出说明）；`ROADMAP.md` §6.5 补一句。
English: `docs/user-guide.md` / `en.md` §8.1.5 (documents the new HTML report export); `ROADMAP.md` §6.5 records it.

## 测试
# Tests

中文：本轮**无 C# 改动**（仅前端 + 文案），`dotnet build` 与 1.0.248 一致（0 错误）。`node --check`、i18n 审计 **zh=en=1884，missing 0**、`check-i18n-keys`（873 处引用）、`check-orphan-keys`（907 处 `t()` 引用）均过。浏览器实测（重建 web 后，管理员，真实数据）：点「🖨️ 导出 HTML」→ `org-health-report-20261015.html`（~9.9 KB）：`<!doctype html>` ✓、内联 `<style>` ✓、1 个内联 SVG（当前无 👍👎，环形降级为「—」，故仅趋势图）、`<table>`（1 表头 + 17 行）✓、6 张卡片 / 12 条柱 ✓、**无外部引用** ✓、标题本地化 ✓；把该 HTML 单独渲染到空白页 → h1「📊 组织体检报告」/ 6 卡 / 12 柱 / 1 SVG / 17 行表，**0 渲染错误**；toast「已导出 HTML 报告」；**无页面报错**。
English: **no C# changes** this round (frontend + copy only), so `dotnet build` matches 1.0.248 (0 errors). `node --check`, the i18n audit **zh=en=1884, missing 0**, `check-i18n-keys` (873 references) and `check-orphan-keys` (907 `t()` references) all pass. Browser-verified (after rebuilding web, as admin, real data): clicking "🖨️ Export HTML" → `org-health-report-20261015.html` (~9.9 KB): `<!doctype html>` ✓, inline `<style>` ✓, 1 inline SVG (no 👍👎 yet, so the donut degrades to "—" and only the trend chart remains), a `<table>` (1 header + 17 rows) ✓, 6 cards / 12 bars ✓, **no external references** ✓, localized title ✓; rendering that HTML alone in a blank page → h1 "📊 Org health report" / 6 cards / 12 bars / 1 SVG / 17 table rows, **0 render errors**; toast "HTML report exported"; **no page errors**.

# AG-UI 群聊桌面版 1.0.248 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.248 Release Notes (current Windows desktop release)

**版本说明**：**CSV 导出跟随当前维度**——图表视图切到「触发者 / 知聚」后，导出的 CSV 就是该维度的明细（不再恒为员工表）。
**Version note**: **The CSV export follows the current dimension** — after switching the chart to "triggered by / groups", the exported CSV is that dimension's table (no longer always employees).

## 变更
# Changes

中文：`#orgHealthModal` 的「📥 导出 CSV」现按**当前维度**导出：
- **数字员工**：员工 / 数字员工 ID / 调用次数 / 总 Token / 👍 / 👎 / 好评率 / 成本 / 最近活跃 / 关注（与之前一致）；
- **触发者 / 知聚**：名称 / ID / 调用次数 / 总 Token / 成本（这两维度无 👍👎 / 关注）。

文件名与维度对齐（`org-health-<dim>-<yyyymmdd>.csv`）；依然前置 UTF-8 BOM、字段全引号转义；维度为空时给出**维度化**的空态提示（不再用员工文案）。纯前端改动，**无新增接口**。
English: the "📥 Export CSV" button in `#orgHealthModal` now exports by the **current dimension**:
- **employees**: employee / agent ID / calls / total tokens / 👍 / 👎 / positive rate / cost / last active / attention (unchanged);
- **triggered by / groups**: name / ID / calls / total tokens / cost (these two dimensions have no 👍👎 / attention).

The filename matches the dimension (`org-health-<dim>-<yyyymmdd>.csv`); the UTF-8 BOM and full field quoting are kept; an empty dimension now shows a **dimension-aware** empty hint (no longer the employee copy). Front-end only, **no new endpoint**.

## 文档
# Docs

中文：`docs/user-guide.md` / `en.md` §8.1.5（CSV 导出注明跟随维度）；`ROADMAP.md` §6.5 补一句。
English: `docs/user-guide.md` / `en.md` §8.1.5 (the CSV export now notes it follows the dimension); `ROADMAP.md` §6.5 records it.

## 测试
# Tests

中文：本轮**无 C# 改动**（仅前端 + 文案），`dotnet build` 与 1.0.247 一致（0 错误）。`node --check`、i18n 审计 **zh=en=1879，missing 0**、`check-i18n-keys`（872 处引用）、`check-orphan-keys`（899 处 `t()` 引用）均过。浏览器实测（重建 web 后，管理员，真实数据）：数字员工维度 → `org-health-agents-20261015.csv`（BOM，10 列，17 行）；注入合成知聚并切到知聚维度 → `org-health-groups-20261015.csv`（BOM，5 列：名称 / ID / 调用次数 / 总 Token / 成本，2 行，首行「产品推广知聚, group_k1, 120, 3200000」）；**无页面报错**。
English: **no C# changes** this round (frontend + copy only), so `dotnet build` matches 1.0.247 (0 errors). `node --check`, the i18n audit **zh=en=1879, missing 0**, `check-i18n-keys` (872 references) and `check-orphan-keys` (899 `t()` references) all pass. Browser-verified (after rebuilding web, as admin, real data): employees dimension → `org-health-agents-20261015.csv` (BOM, 10 columns, 17 rows); after injecting synthetic groups and switching to the groups dimension → `org-health-groups-20261015.csv` (BOM, 5 columns: name / ID / calls / total tokens / cost, 2 rows, first "产品推广知聚, group_k1, 120, 3200000"); **no page errors**.

# AG-UI 群聊桌面版 1.0.247 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.247 Release Notes (current Windows desktop release)

**版本说明**：**图表视图维度再加「知聚」**——回答「**哪个知聚最耗资源**」。为此新增按知聚聚合的用量表（**零迁移风险**）。
**Version note**: **The chart view gains a "groups" dimension** — answering "**which group consumes the most**". This adds a per-group usage aggregate table (**zero migration risk**).

## 变更
# Changes

中文：
- **存储**：新增聚合表 `agui_usage_group(usage_date, group_id, agent_id, user_id, prompt/completion/reasoning/calls)`，主键 `(日期, 知聚, 员工, 触发者)`，仅用 `CREATE TABLE IF NOT EXISTS` 建表——**不修改既有 `agui_usage` 表、无需任何迁移**，旧库启动自动建表。SQLite / MySQL / Postgres / Redis / 内存五套实现同步支持。
- **接口**：`IUsageStore` 新增 `RecordGroupUsage` 与 `GetGroupUsageBetween`；`AgentUsageService.RecordUsage` 新增带 `groupId` 的重载（**同时写两张表**；无群上下文时只写原表，旧 5 参重载保留 → 既有调用/测试不受影响）；用量记录点（`AgentCatalog` / `UsageCaptureChatClient`）改为传入 `AmbientContext.GroupId`。
- **接口出参**：`GET /ag-ui/admin/org-health` 结构化数据**新增 `groups[]`**（按知聚聚合调用量 / token / 成本；名字从群库解析，取不到回退 groupId；取调用次数前 50），**向后兼容**。
- **前端**：柱状图**维度切换**在「数字员工 / 触发者」基础上**新增「知聚」**（三档）；空态文案改为**维度化**（非员工维度显示「该维度暂无用量（自本版起按维度记录）」）。
- **注意**：群维度**自本版起**录入——**历史用量无群信息故不计入**（这是无法回填的）。
English:
- **Storage**: a new aggregate table `agui_usage_group(usage_date, group_id, agent_id, user_id, prompt/completion/reasoning/calls)` with primary key `(date, group, employee, trigger)`, created with `CREATE TABLE IF NOT EXISTS` only — **the existing `agui_usage` table is untouched and no migration is needed**; existing databases create the table on startup. All five implementations (SQLite / MySQL / Postgres / Redis / in-memory) support it.
- **Interface**: `IUsageStore` gains `RecordGroupUsage` and `GetGroupUsageBetween`; `AgentUsageService.RecordUsage` gains a `groupId` overload (**writing both tables**; without a group context only the original table is written, and the old 5-arg overload is kept so existing call sites / tests are unaffected); usage recording points (`AgentCatalog` / `UsageCaptureChatClient`) now pass `AmbientContext.GroupId`.
- **Endpoint output**: the structured data of `GET /ag-ui/admin/org-health` **adds `groups[]`** (calls / tokens / cost aggregated per group; name resolved from the group store, falling back to groupId; top 50 by calls), **backward compatible**.
- **Front end**: the bar chart's **dimension switch** gains **"groups"** alongside "employees / triggered by" (three options); the empty state is now **dimension-aware** (non-employee dimensions show "No usage for this dimension yet (recorded since this version)").
- **Note**: the group dimension is recorded **from this version on** — **historical usage has no group info and is therefore not counted** (impossible to backfill).

## 文档
# Docs

中文：`docs/user-guide.md` / `en.md` §8.1.5（维度切换加「知聚」）与 §9.2.8（`org-health` 行补 `groups`）；`ROADMAP.md` §6.5 补知聚维度。
English: `docs/user-guide.md` / `en.md` §8.1.5 (the dimension switch gains "groups") and §9.2.8 (the `org-health` row notes `groups`); `ROADMAP.md` §6.5 records the group dimension.

## 测试
# Tests

中文：`AdminApiTests` 新增 `OrgHealth_IncludesPerGroupAggregation`（`group_k1` → calls=2 / tokens=170、`group_k2` → calls=1，且**群不存在时名字回退为 groupId**）；`OrgHealth_ReturnsStructuredDataForCharts` 补断言 `groups` 为数组。`AdminApiIntegrationTests` + `AgentUsageServiceTests` + `OrgHealthReportTests` 合跑 **21/21**；`Usage|RedisStore` 筛选 **18/18**。`node --check`、i18n 审计 **zh=en=1877，missing 0**、`check-i18n-keys`（872 处引用）、`check-orphan-keys`（897 处 `t()` 引用）、`dotnet build` **0 错误**均过。浏览器实测（重建 web 后，管理员）：接口 `groups` 为数组（真实历史为空，因无群维度）；维度切换三档正常；切「知聚」→ 空态文案「该维度暂无用量（自本版起按维度记录）」；注入合成知聚后渲 2 条（首条「产品推广知聚 120」）；**无页面报错**。
English: `AdminApiTests` gains `OrgHealth_IncludesPerGroupAggregation` (`group_k1` → calls=2 / tokens=170, `group_k2` → calls=1, and **the name falls back to the groupId when the group does not exist**); `OrgHealth_ReturnsStructuredDataForCharts` now asserts `groups` is an array. `AdminApiIntegrationTests` + `AgentUsageServiceTests` + `OrgHealthReportTests` run **21/21**; the `Usage|RedisStore` filter **18/18**. `node --check`, the i18n audit **zh=en=1877, missing 0**, `check-i18n-keys` (872 references), `check-orphan-keys` (897 `t()` references) and `dotnet build` **0 errors** all pass. Browser-verified (after rebuilding web, as admin): the endpoint's `groups` is an array (empty for real history, since there is no group dimension); the three-way dimension switch works; switching to "groups" shows the empty state "No usage for this dimension yet (recorded since this version)"; injecting synthetic groups renders 2 bars (first "产品推广知聚 120"); **no page errors**.

# AG-UI 群聊桌面版 1.0.246 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.246 Release Notes (current Windows desktop release)

**版本说明**：**图表视图新增「维度」切换**——组织体检图表的 Top 12 横向柱状可在**数字员工 / 触发者**之间切换，后者回答「谁在消耗资源」，与「按用户配额」互补。
**Version note**: **The chart view gains a dimension switch** — the org health Top-12 bar chart can now switch between **employees / triggered by**, the latter answering "who consumes resources", complementing the per-user quota.

## 变更
# Changes

中文：
- **后端**：`GET /ag-ui/admin/org-health?days=N` 的结构化数据**新增 `users[]`**——按 `UserId` 聚合调用量 / token / 成本（名字解析：昵称 → 用户名 → userId；取调用次数前 50）。**向后兼容**（仅新增字段）。
- **前端**：`#orgHealthModal` 的 Top 12 柱状标题行新增**维度分段按钮**（**数字员工 / 触发者**，与既有的指标分段按钮并列于新的 `.oh-switches` 容器），与指标按钮**各自独立同步高亮**。「触发者」维度无 👍👎 / 关注（这些是员工专属），仅展示调用量 / token / 成本；柱状仍按当前指标降序。导出 CSV 仍为**员工明细**（不随维度变）。
- **无新增接口**：`users` 由已在拉取的 `org-health` 响应随包下发（服务端从同一批 `GetUsageBetween` 行聚合）。
English:
- **Backend**: the structured data of `GET /ag-ui/admin/org-health?days=N` **adds `users[]`** — calls / tokens / cost aggregated by `UserId` (name resolved: nickname → username → userId; top 50 by calls). **Backward compatible** (additive only).
- **Front end**: the Top-12 bar header in `#orgHealthModal` gains a **dimension segmented control** (**employees / triggered by**) alongside the existing metric control, both inside a new `.oh-switches` container, each**independently syncing its highlight**. The "triggered by" dimension has no 👍👎 / attention (those are employee-specific) and shows calls / tokens / cost only; bars still sort by the current metric. The CSV export remains the **employee table** (does not follow the dimension).
- **No new endpoint**: `users` rides along in the already-fetched `org-health` response (aggregated server-side from the same `GetUsageBetween` rows).

## 文档
# Docs

中文：`docs/user-guide.md` / `en.md` §8.1.5（图表视图：新增维度切换）与 §9.2.8（`org-health` 行补 `users`）；`ROADMAP.md` §6.5 补维度切换。
English: `docs/user-guide.md` / `en.md` §8.1.5 (chart view: the new dimension switch) and §9.2.8 (the `org-health` row notes `users`); `ROADMAP.md` §6.5 records the dimension switch.

## 测试
# Tests

中文：`AdminApiTests` 新增 `OrgHealth_IncludesPerUserAggregation`（直接记录用量：`user_u1` → calls=2 / tokens=40、`user_u2` → calls=1，且**无对应账号时名字回退为 userId**）；`OrgHealth_ReturnsStructuredDataForCharts` 补断言 `users` 为数组。`AdminApiIntegrationTests` + `OrgHealthReportTests` 合跑 **16/16**。`node --check`、i18n 审计 **zh=en=1875，missing 0**、`check-i18n-keys`（871 处引用）、`check-orphan-keys`（897 处 `t()` 引用）、`dotnet build` **0 错误**均过。浏览器实测（重建 web 后，管理员，真实数据）：默认「数字员工 + 调用次数」→ 12 条柱（首条「质检审核 243」）；切「触发者」→ 1 条（「David 960」，名字由账号昵称解析）；再切「Token」→「David 14,878,030」；两组切换器均正确同步；**无页面报错**。
English: `AdminApiTests` gains `OrgHealth_IncludesPerUserAggregation` (recording usage directly: `user_u1` → calls=2 / tokens=40, `user_u2` → calls=1, and **the name falls back to the userId when no account exists**); `OrgHealth_ReturnsStructuredDataForCharts` now asserts `users` is an array. `AdminApiIntegrationTests` + `OrgHealthReportTests` run **16/16**. `node --check`, the i18n audit **zh=en=1875, missing 0**, `check-i18n-keys` (871 references), `check-orphan-keys` (897 `t()` references) and `dotnet build` **0 errors** all pass. Browser-verified (after rebuilding web, as admin, real data): default "employees + calls" → 12 bars (first "质检审核 243"); switch to "triggered by" → 1 bar ("David 960", name resolved from the account nickname); then "Tokens" → "David 14,878,030"; both switches stay in sync correctly; **no page errors**.

# AG-UI 群聊桌面版 1.0.245 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.245 Release Notes (current Windows desktop release)

**版本说明**：**图表视图支持导出 CSV**——组织体检图表的全部员工明细可一键导出为 CSV（本地化表头、带 BOM、UTF-8），直接进 Excel 做汇报。
**Version note**: **The chart view exports CSV** — the full employee table of the org health chart can be exported to CSV with one click (localized headers, BOM, UTF-8), ready for Excel reporting.

## 变更
# Changes

中文：
- **前端**：`#orgHealthModal` 底部新增 **「📥 导出 CSV」**（与「📄 组织体检报告」「关闭」并列）——把 `org-health` 返回的**全部员工**（不限于柱状图 Top 12）导出为 CSV，列为员工 / 数字员工 ID / 调用次数 / 总 Token / 👍 / 👎 / 好评率 / 成本 / 最近活跃 / 关注，**按调用次数降序**。
- **健壮性**：字段全部加引号并转义内嵌引号（逗号 / 引号 / 换行安全）；文件前置 **UTF-8 BOM**，避免 Excel 打开中文乱码；文件名 `org-health-agents-<yyyymmdd>.csv`（日期取响应窗口 `to`）。未配置单价时成本列留空；表头与关注标签随语言。
- **无新增接口**：数据取自已拉取的 `org-health` 响应（与图表同源），导出**与当前指标切换无关**（CSV 总是含全部列）。
English:
- **Front end**: `#orgHealthModal` gains an **"📥 Export CSV"** button (next to "📄 Org health report" and "Close") — it exports **every employee** returned by `org-health` (not just the chart's Top 12) as CSV, with columns employee / agent ID / calls / total tokens / 👍 / 👎 / positive rate / cost / last active / attention, **sorted by calls descending**.
- **Robustness**: all fields are quoted with embedded quotes escaped (comma / quote / newline safe); the file starts with a **UTF-8 BOM** so Excel opens Chinese correctly; the filename is `org-health-agents-<yyyymmdd>.csv` (date from the response's `to`). The cost column is blank when no pricing is configured; headers and attention labels follow the language.
- **No new endpoint**: the data comes from the already-fetched `org-health` response (same source as the charts), and the export is **independent of the metric switch** (CSV always carries all columns).

## 文档
# Docs

中文：`docs/user-guide.md` / `en.md` §8.1.5（图表视图：新增 CSV 导出与文件名 / BOM 说明）；`ROADMAP.md` §6.5 补 CSV 导出。
English: `docs/user-guide.md` / `en.md` §8.1.5 (chart view: the new CSV export with filename / BOM notes); `ROADMAP.md` §6.5 records the CSV export.

## 测试
# Tests

中文：本轮**无 C# 改动**（仅前端 + 文案），`dotnet build` 结果与 1.0.244 一致（0 错误）。`node --check`、i18n 审计 **zh=en=1873，missing 0**、`check-i18n-keys`（869 处引用）、`check-orphan-keys`（897 处 `t()` 引用）均过。浏览器实测（重建 web 后，管理员，真实数据）：点「📥 导出 CSV」→ 下载 `org-health-agents-20261015.csv`，**BOM=true**，**18 行**（1 表头 + 17 员工），表头「数字员工,数字员工 ID,调用次数,总 Token,👍,👎,好评率,成本,最近活跃,关注」，首两行按调用降序（质检审核 243 / 3,135,219、PPT制作排版工程师 157 / 2,873,070），未配置单价时成本列为空；toast「已导出 CSV」；**无页面报错**。
English: **no C# changes** this round (frontend + copy only), so `dotnet build` matches 1.0.244 (0 errors). `node --check`, the i18n audit **zh=en=1873, missing 0**, `check-i18n-keys` (869 references) and `check-orphan-keys` (897 `t()` references) all pass. Browser-verified (after rebuilding web, as admin, real data): clicking "📥 Export CSV" downloads `org-health-agents-20261015.csv` with **BOM=true**, **18 lines** (1 header + 17 employees), header "employee, agent ID, calls, total tokens, 👍, 👎, positive rate, cost, last active, attention", the first two rows sorted by calls descending (质检审核 243 / 3,135,219; PPT制作排版工程师 157 / 2,873,070), and a blank cost column with no pricing; toast "CSV exported"; **no page errors**.

# AG-UI 群聊桌面版 1.0.244 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.244 Release Notes (current Windows desktop release)

**版本说明**：**柱状图也支持指标切换**——组织体检图表的 Top 12 横向柱状与按日趋势折线**共用一组指标**（调用次数 / Token / 成本）；并**修复一处集成测试的隔离脆弱性**（非管理员用例在单独运行时随机失败）。
**Version note**: **The bar chart switches metrics too** — the org health Top-12 bar chart and the daily trend line now **share one metric** (calls / tokens / cost); also **fixes an integration-test isolation fragility** (non-admin cases used to fail randomly when run in isolation).

## 变更
# Changes

中文：
- **前端**：`#orgHealthModal` 的「Top 12」柱状与「按日趋势」折线**共用同一指标状态**（`orgHealthMetric`）——两组分段按钮（各 3 档：调用次数 / Token / 成本）始终同步高亮；切换**只重绘**（不重新拉数据），柱状按当前指标**降序重排**并换值（成本带货币、其余用千分位），趋势折线与 meta 同步换单位。指标切换绑定改为在弹窗上**事件委派**（`.oh-metric-btn`），无需各自 id。
- **降级不变**：未配置单价时所有「成本」按钮**统一置灰 + 提示**，当前维度若为成本则回退调用次数。柱状图数据取自 `org-health` 的 `agents[].cost` / `totalTokens` / `calls`，趋势图取自 `/admin/usage` 按日汇总，**无新增接口**。
English:
- **Front end**: the "Top 12" bar chart and the "Daily trend" line in `#orgHealthModal` now **share one metric state** (`orgHealthMetric`) — the two segmented controls (3 options each: calls / tokens / cost) always highlight in sync; switching **only re-renders** (no refetch), the bars **re-sort descending** by the current metric and change their values (cost with currency, others thousands-separated), and the trend line and meta line change units together. The metric binding now uses **event delegation** on the dialog (`.oh-metric-btn`), so no per-group id is needed.
- **Degradation unchanged**: when no pricing is configured every **Cost** button is **greyed out with a hint** in unison, and the current metric falls back to calls. Bar values come from `org-health`'s `agents[].cost` / `totalTokens` / `calls`; the trend comes from `/admin/usage`'s daily summary — **no new endpoint**.

## 修复
# Fixes

中文：**`AdminApiServerFixture` 未关闭 `Auth:FirstUserIsAdmin`（真实缺陷）**：该夹具被 `AdminApiIntegrationTests` 等 5 个测试类共用，却没像其它夹具那样显著关闭「首个注册用户自动提升为管理员」。于是多处「非管理员 → 403」用例（如 `OrgHealth_RequiresOperator`）在**单独运行**时，会因为自己的普通用户恰好是首个注册者而被当作管理员，导致断言失败（整类串跑时碰巧由 `admin_chief` 先注册而通过）。现按既有约定补上 `["Auth:FirstUserIsAdmin"] = "false"`，管理员只由 `Auth:AdminUserIds` 决定，与注册顺序解耦。
English: **`AdminApiServerFixture` did not disable `Auth:FirstUserIsAdmin` (real defect)**: this fixture, shared by 5 test classes including `AdminApiIntegrationTests`, unlike the other fixtures did not turn off "the first registered user auto-promotes to admin". So several "non-admin → 403" cases (e.g. `OrgHealth_RequiresOperator`) failed when **run in isolation**, because their plain user happened to be the first registrant and got treated as admin (when the whole class runs, `admin_chief` is registered first by luck and it passes). It now sets `["Auth:FirstUserIsAdmin"] = "false"`, matching the existing convention, so admin status is decided only by `Auth:AdminUserIds`, decoupled from registration order.

## 文档
# Docs

中文：`docs/user-guide.md` / `en.md` §8.1.5（图表视图：柱状与折线共用指标切换）；`ROADMAP.md` §6.5 补图表视图进展。
English: `docs/user-guide.md` / `en.md` §8.1.5 (chart view: bars and line share the metric switch); `ROADMAP.md` §6.5 records the chart-view progress.

## 测试
# Tests

中文：`AdminApiIntegrationTests` + `AgentPerformanceApiTests` + `OrphanAdminApiTests` + `StorageAdminApiTests` + `EvalApiTests` + `OrgHealthReportTests` 合跑 **21/21**；`OrgHealth_RequiresOperator` **单独运行也通过**（修复验证）。`node --check`、i18n 审计 **zh=en=1870，missing 0**、`check-i18n-keys`（868 处引用）、`check-orphan-keys`（890 处 `t()` 引用）、`dotnet build` **0 错误**均过。浏览器实测（重建 web 后，管理员，真实数据）：两组切换器始终同步；柱状 12 条：调用次数首条「质检审核 243」、切 Token「3,135,219」、切成本（注入单价/成本）「$3135.22」；趋势 meta 对应「共 960 次 / 共 14,878,030 Token / 共 $57.19」；未配置单价时成本按钮**两组均置灰**；**无页面报错**。
English: `AdminApiIntegrationTests` + `AgentPerformanceApiTests` + `OrphanAdminApiTests` + `StorageAdminApiTests` + `EvalApiTests` + `OrgHealthReportTests` run **21/21**; `OrgHealth_RequiresOperator` **passes in isolation too** (fix verification). `node --check`, the i18n audit **zh=en=1870, missing 0**, `check-i18n-keys` (868 references), `check-orphan-keys` (890 `t()` references) and `dotnet build` **0 errors** all pass. Browser-verified (after rebuilding web, as admin, real data): the two switches always stay in sync; 12 bars: calls → first "质检审核 243", tokens → "3,135,219", cost (with injected pricing/cost) → "$3135.22"; trend meta matches ("960 calls / 14,878,030 tokens / $57.19"); with no pricing both Cost buttons are **greyed out**; **no page errors**.

# AG-UI 群聊桌面版 1.0.243 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.243 Release Notes (current Windows desktop release)

**版本说明**：**组织体检图表的「按日趋势」支持指标切换**——可在**调用次数 / Token / 成本**三种维度间一键切换，复盘用量与花销的形状。
**Version note**: **The org health chart's daily trend now switches metrics** — flip between **calls / tokens / cost** with one click to review the shape of usage and spend.

## 变更
# Changes

中文：
- **前端**：`#orgHealthModal` 的「按日趋势」标题旁新增**指标切换控件**（`#orgHealthMetric`，分段按钮：调用次数 / Token / 成本）——切换只**重绘折线**（不重新拉数据），折线与下方 meta（区间 · 共 N · 单日峰值 M）同步换单位：执行次数用「次」、用量用「Token」、花销用配置货币（如 `$`）。
- **数据**：沿用既有 `GET /ag-ui/admin/usage?days=N` 的按日汇总（已含 `calls` / `totalTokens` / `promptTokens` / `completionTokens` / `reasoningTokens`），**无新增接口**。
- **后端**：`GET /ag-ui/admin/org-health` 的 `pricing` 对象**补下两个单价字段** `inputPerMillion` / `outputPerMillion`（与 `/agent-performance` 一致），供前端在浏览器内按日估算成本；**向后兼容**（仅新增字段）。
- **降级**：未配置单价（`pricing.configured=false`）时「成本」按钮**置灰并带提示**（`admin.orgHealthTrendNoPricing`），且若当前维度为成本则自动回退到调用次数；有效天数 `<2` 时仍显示提示文案而非空图。
English:
- **Front end**: next to the "Daily trend" title in `#orgHealthModal`, a **metric switch** (`#orgHealthMetric`, segmented buttons: calls / tokens / cost) is added — switching only **re-renders the line** (no refetch), and both the polyline and the meta line (range · N total · peak M) change units accordingly: calls show "calls", usage shows "tokens", spend shows the configured currency (e.g. `$`).
- **Data**: reuses the existing `GET /ag-ui/admin/usage?days=N` daily summary (already carries `calls` / `totalTokens` / `promptTokens` / `completionTokens` / `reasoningTokens`) — **no new endpoint**.
- **Backend**: the `pricing` object of `GET /ag-ui/admin/org-health` now **includes the two unit prices** `inputPerMillion` / `outputPerMillion` (matching `/agent-performance`), so the front end can estimate per-day cost in the browser; **backward compatible** (additive only).
- **Degradation**: when no pricing is configured (`pricing.configured=false`) the **Cost** button is **greyed out with a hint** (`admin.orgHealthTrendNoPricing`), and if the current metric is cost it falls back to calls; with fewer than 2 effective days the hint text is still shown instead of an empty chart.

## 文档
# Docs

中文：`docs/user-guide.md` / `en.md` §8.1.5（图表视图的按日趋势补指标切换）与 §9.2.8（`org-health` 行注明 `pricing` 含单价字段）。
English: `docs/user-guide.md` / `en.md` §8.1.5 (the chart view's daily trend now documents the metric switch) and §9.2.8 (the `org-health` row notes `pricing` carries the unit-price fields).

## 测试
# Tests

中文：`AdminApiTests.OrgHealth_ReturnsStructuredDataForCharts` 扩展为断言 `pricing` 含 `configured` / `currency` / `inputPerMillion` / `outputPerMillion`；`AdminApiIntegrationTests` 合跑 **13/13**。`node --check`（`app.js` / `zh.js` / `en.js`）、i18n 审计 **zh=en=1870，missing 0**、`check-i18n-keys`（868 处引用）、`check-orphan-keys`（890 处 `t()` 引用）、`dotnet build` **0 错误**均过。浏览器实测（重建 web 后，管理员，真实数据）：图表弹窗 **6 张 KPI 卡 / 12 条柱**；折线 12 个点；默认「调用次数」→ meta「2026-09-17 ~ 2026-10-14 · 共 960 次 · 单日峰值 219 次」；切到「Token」→ meta「共 14,878,030 Token · 单日峰值 3,598,713 Token」（折线仍 12 点）；「成本」按钮因未配置单价**正确置灰**，注入单价（2/8 每百万）后切到成本 → meta「共 $57.19 · 单日峰值 $14.16」；环形无反馈正确显示「—」；**无页面报错**。
English: `AdminApiTests.OrgHealth_ReturnsStructuredDataForCharts` is extended to assert `pricing` carries `configured` / `currency` / `inputPerMillion` / `outputPerMillion`; `AdminApiIntegrationTests` runs **13/13**. `node --check` (`app.js` / `zh.js` / `en.js`), the i18n audit **zh=en=1870, missing 0**, `check-i18n-keys` (868 references), `check-orphan-keys` (890 `t()` references) and `dotnet build` **0 errors** all pass. Browser-verified (after rebuilding web, as admin, real data): the chart dialog shows **6 KPI cards / 12 bars**; the line has 12 points; default "calls" → meta "2026-09-17 ~ 2026-10-14 · 960 calls total · peak 219 calls"; switching to "Tokens" → meta "14,878,030 tokens total · peak 3,598,713 tokens" (line still 12 points); the **Cost** button is correctly **greyed out** with no pricing, and after injecting unit prices (2/8 per million) switching to cost → meta "$57.19 total · peak $14.16"; the donut correctly shows "—" with no feedback; **no page errors**.

# AG-UI 群聊桌面版 1.0.242 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.242 Release Notes (current Windows desktop release)

**版本说明**：**组织体检图表视图再进一步**——在既有 KPI 与 Top 柱状之上，新增**按日趋势折线**（调用次数/天）与**好评率环形**。
**Version note**: **The org health chart view goes one step further** — on top of the existing KPIs and top-calls bars, it now adds a **daily trend line** (calls per day) and a **positive-rate donut**.

## 变更
# Changes

中文：
- **前端**：`#orgHealthModal` 在柱状图与需关注项之间新增一行：左侧**好评率环形**（复用既有 `renderDonut`，有 👍/👎 反馈时按 优/中/差 三档配色，无反馈时优雅降级显示「—」），右侧**按日趋势折线**（`viewBox="0 0 800 90"` 网格 + 面积 + `polyline`，`preserveAspectRatio="none"` + `vector-effect="non-scaling-stroke"` 防拉伸变形），下方 meta 显示「区间 · 共 N 次调用 · 单日峰值 M」。
- **数据来源**：图表弹窗现并行发起两个请求——`GET /ag-ui/admin/org-health?days=N`（KPI / 柱状 / 关注项）与 `GET /ag-ui/admin/usage?days=N`（按日趋势）；`/usage` **失败非致命**，仅降级折线，不影响其余渲染。
- **降级**：有效天数 `< 2` 时折线区显示提示文案（`admin.orgHealthTrendShort`）而非空图；无评价数据时环形显示「—」。
English:
- **Front end**: `#orgHealthModal` gains a new row between the bars and the attention list: a **positive-rate donut** on the left (reusing the existing `renderDonut`; the 优/中/差 colour tiers when 👍/👎 feedback exists, a graceful "—" when it does not) and a **daily trend line** on the right (`viewBox="0 0 800 90"` grid + area + `polyline`, `preserveAspectRatio="none"` + `vector-effect="non-scaling-stroke"` to avoid stretching), with a meta line below reading "range · N calls total · peak M per day".
- **Data source**: the chart dialog now fires two requests in parallel — `GET /ag-ui/admin/org-health?days=N` (KPIs / bars / attention) and `GET /ag-ui/admin/usage?days=N` (daily trend); a `/usage` **failure is non-fatal** and only degrades the trend line, leaving the rest intact.
- **Degradation**: with fewer than 2 effective days the trend area shows a hint (`admin.orgHealthTrendShort`) instead of an empty chart; the donut shows "—" when there is no rating data.

## 文档
# Docs

中文：`docs/user-guide.md` / `en.md` §8.1.5（图表视图补「好评率环形（无反馈显示 —）」与「按日趋势折线（1.0.242，复用 `/admin/usage`）」）。
English: `docs/user-guide.md` / `en.md` §8.1.5 (the chart view now documents the "positive-rate donut (— when no feedback)" and the "daily trend line (1.0.242, reusing `/admin/usage`)").

## 测试
# Tests

中文：`node --check`（`app.js` / `zh.js` / `en.js`）与 i18n 审计 **zh=en=1864，missing 0**、`check-i18n-keys`（865 处引用）、`check-orphan-keys`（889 处 `t()` 引用）全过；`dotnet build AguiGroupChat.slnx` **0 错误**（本轮无 C# 改动）。浏览器实测（重建 web 后，管理员，真实数据）：图表弹窗 **6 张 KPI 卡**、**12 条柱**、折线 **`polyline` 存在且 12 个点**、meta「2026-09-17 ~ 2026-10-14 · 共 960 次调用 · 单日峰值 219」；环形因当前库无 👍/👎 反馈正确降级显示「—」（手动 `renderDonut("orgHealthRate", 0.93)` 验证渲出 2 个 `<circle>`）；**无页面报错**。
English: `node --check` (`app.js` / `zh.js` / `en.js`) and the i18n audit **zh=en=1864, missing 0**, `check-i18n-keys` (865 references) and `check-orphan-keys` (889 `t()` references) all pass; `dotnet build AguiGroupChat.slnx` **0 errors** (no C# changes this round). Browser-verified (after rebuilding web, as admin, real data): the chart dialog renders **6 KPI cards**, **12 bars**, a trend **`polyline` with 12 points**, meta "2026-09-17 ~ 2026-10-14 · 960 calls total · peak 219 per day"; the donut correctly degrades to "—" because the current store has no 👍/👎 feedback (manually calling `renderDonut("orgHealthRate", 0.93)` renders 2 `<circle>`s); **no page errors**.

# AG-UI 群聊桌面版 1.0.241 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.241 Release Notes (current Windows desktop release)

**版本说明**：**组织体检报告新增「图表视图」**——除下载 Markdown 外，可在应用内直接看到 KPI、调用量 Top 柱状与需关注项。
**Version note**: **The org health report gains a chart view** — beyond downloading the Markdown, you can now see KPIs, a top-calls bar chart and the attention list right in the app.

## 变更
# Changes

中文：
- **接口** `GET /ag-ui/admin/org-health?days=N` 除 `filename` / `markdown` 外，**同时下发结构化数据**：`summary`（`agents` 有调用员工数 / `totalAgents` / `calls` / `totalTokens` / `up` / `down` / `positiveRate` / `cost` / `flagged`）、`agents[]`（每员工 `calls` / `totalTokens` / `up` / `down` / `positiveRate` / `lastDate` / `flags` / `cost`）、`pricing`（`configured` / `currency`）。**向后兼容**（Markdown 下载路径不变）。
- **前端**：绩效页新增「📊 图表」按钮 → `#orgHealthModal`：KPI 卡片（员工 有调用/总数、调用次数、总 Token、成本、好评率、关注项）、**调用次数 Top 12 横向柱状**（好评率偏低标红 `low`、闲置标灰 `idle`）、**需关注项列表**（`low-praise` / `idle`，标签随语言）；弹窗内可直接下载 Markdown。Esc / 取消 / 点遮罩关闭。
English:
- **Endpoint** `GET /ag-ui/admin/org-health?days=N` now also returns **structured data** alongside `filename` / `markdown`: `summary` (`agents` active / `totalAgents` / `calls` / `totalTokens` / `up` / `down` / `positiveRate` / `cost` / `flagged`), `agents[]` (per-employee `calls` / `totalTokens` / `up` / `down` / `positiveRate` / `lastDate` / `flags` / `cost`) and `pricing` (`configured` / `currency`). **Backward compatible** (the Markdown download path is unchanged).
- **Front end**: the performance tab gains a "📊 Charts" button → `#orgHealthModal`: KPI cards (employees active/total, calls, total tokens, cost, positive rate, flagged), a **top-12 calls horizontal bar chart** (low-praise bars red `low`, idle grey `idle`) and a **needs-attention list** (`low-praise` / `idle`, labelled per language); the dialog can also download the Markdown. Esc / Cancel / overlay click closes it.

## 文档
# Docs

中文：`docs/user-guide.md` / `en.md` §8.1.5（绩效段落补图表视图）与 §9.2.8（`org-health` 行注明同时下发结构化数据）。
English: `docs/user-guide.md` / `en.md` §8.1.5 (chart view added to the performance paragraph) and §9.2.8 (the `org-health` row notes it also returns structured data).

## 测试
# Tests

中文：`AdminApiTests` 新增 `OrgHealth_ReturnsStructuredDataForCharts`（200 且含 `markdown` / `filename` 与 `summary` 全部字段、`agents` 为数组、`pricing.configured`）与 `OrgHealth_RequiresOperator`（非 Operator 非成功）；连同 `OrgHealthReportTests` 合跑 **15/15**。浏览器实测（重建 web 后，管理员）：图表弹窗渲 **6 张 KPI 卡**（员工 15/17 · 调用 960 · tokens 14,878,030 · 成本 — · 好评率 — · 关注项 2）、**12 条柱**（首条“质检审核 243”）、需关注列出 2 位闲置员工（标签本地化）；无页面报错。`node --check`、i18n 审计 **zh=en=1861，missing 0**、`check-orphan-keys`、`dotnet build` 0 错误均过；重建 web 后健康 200。
English: `AdminApiTests` gains `OrgHealth_ReturnsStructuredDataForCharts` (200 with `markdown` / `filename` and every `summary` field, `agents` an array, `pricing.configured`) and `OrgHealth_RequiresOperator` (a non-Operator is not successful); **15/15** with `OrgHealthReportTests`. Browser-verified (after rebuilding web, as admin): the chart dialog renders **6 KPI cards** (employees 15/17 · calls 960 · tokens 14,878,030 · cost — · positive rate — · flagged 2), **12 bars** (first "质检审核 243") and lists 2 idle employees (labels localized); no page errors. `node --check`, the i18n audit **zh=en=1861, missing 0**, `check-orphan-keys` and `dotnet build` 0 errors all pass; health 200 after rebuilding web.

# AG-UI 群聊桌面版 1.0.240 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.240 Release Notes (current Windows desktop release)

**版本说明**：**修复「预览说有的、装完却没有」的偏差**——市场导入原来在克隆数字员工时**丢掉了 `skillDefIds`（可复用技能引用）与 `memoryProfile`（记忆拟人类型）**，导致包内这两项配置永远带不进来。现一并携带（深拷贝），预览与安装结果一致。
**Version note**: **Fixes a "the preview says it, but install drops it" mismatch** — importing from the marketplace used to clone digital employees **without `skillDefIds` (reusable skill references) or `memoryProfile` (memory personality)**, so those two would never come with a pack. They are now carried too (deep-copied), so the preview matches the install result.

## 修复
# Fixes

中文：
1. **`MarketplaceService.Clone` 漏拷两字段**（真实缺陷）：`Clone` 复制了人设 / 触发 / 关键词 / 桥接 / 知识库 / 图库 / 流水线 / 指派等，但**没有复制 `SkillDefIds` 与 `MemoryProfile`**——于是任何经市场（内置或远程）安装的数字员工，其挂载的可复用技能与记忆拟人类型全部丢失。现补上，并为 `MemoryProfile` 做**深拷贝**（新增 `CloneMemoryProfile`），不共享原对象。
2. **预览改为展示技能名**：预览接口新增 `skillNames`（按目标实例的技能库解析 `SkillDefIds` → 名称；未知 id 回退为 id），前端优先展示「技能：A、B」（>5 个截断），否则回退「技能 n」；与安装后实际一致。
English:
1. **`MarketplaceService.Clone` dropped two fields (real defect)**: `Clone` copied persona / trigger / keywords / bridge / knowledge bases / image libraries / pipeline / assignments, but **not `SkillDefIds` or `MemoryProfile`** — so any digital employee installed from the marketplace (built-in or remote) silently lost its mounted reusable skills and its memory personality. Both are now copied, with `MemoryProfile` **deep-copied** (new `CloneMemoryProfile`) so nothing is shared with the source object.
2. **The preview now shows skill names**: the preview endpoint gains `skillNames` (resolving `SkillDefIds` against the target instance's skill library → names; unknown ids fall back to the id), and the front end prefers "Skills: A, B" (truncated past 5), else the "Skills n" count — matching what actually gets installed.

## 文档
# Docs

中文：`docs/user-guide.md` / `en.md` §3.12（新增「随包携带的配置」与预览的技能名）。
English: `docs/user-guide.md` / `en.md` §3.12 (the new "configuration carried with the pack" note and skill names in the preview).

同轮文档修正（follow-up，无代码变更）：`docs/user-guide.md` / `en.md` §3.10 与 §3.11-4 —— 原文声称「导出/导入不覆盖 `schedule`/`relayToAgentId`/`pipeline`/`requireApprovalToolNames`/`disable*`，导入体不含 `schedule`/`allowedGroupIds`」，与代码不符：这些字段**早已随导出/导入无损往返**（两轮导入、`allowedGroupIds` 先按目标实例已有分组过滤），仅 `bridgeToken`/`ownerId` 不导出。已按实际行为修正；`docs/feature-review-2026-10.md` §8.3 第 4 项由「已知边界」改为「文档已修正」。
Same-round doc correction (follow-up, no code change): `docs/user-guide.md` / `en.md` §3.10 and §3.11-4 — they claimed export/import "does not cover `schedule` / `relayToAgentId` / `pipeline` / `requireApprovalToolNames` / `disable*`" and that the import body omits `schedule` / `allowedGroupIds`, which does not match the code: those fields have long been **lossless on export/import** (two-pass import, `allowedGroupIds` filtered against existing groups) and only `bridgeToken` / `ownerId` are not exported. Corrected to match the actual behaviour; `docs/feature-review-2026-10.md` §8.3 item 4 moved from "known boundary" to "docs corrected".

## 测试
# Tests

中文：`MarketplaceTests` 新增 `ImportDefinitions_CarriesSkillRefsAndMemoryProfile`（导入后 `SkillDefIds` 与 `MemoryProfile` 各字段保留；改动导入结果**不影响源定义**，验证深拷贝）；`MarketplaceApiTests` 的远程 demo 包加 `skillDefIds: ["demo_skill"]` 并断言预览 `skillNames` 回退为 `demo_skill`。Marketplace 相关合跑 **20/20**。浏览器实测（重建 web 后）：预览仍正常渲染 2 行 / 2 个勾选框，开市场与预览期间 **0 控制台报错**。`node --check`、i18n 审计 **zh=en=1853，missing 0**、`check-orphan-keys`、`dotnet build` 0 错误均过；重建 web 后健康 200。
English: `MarketplaceTests` gains `ImportDefinitions_CarriesSkillRefsAndMemoryProfile` (after import, `SkillDefIds` and every `MemoryProfile` field survive; mutating the import result **does not affect the source definition**, proving the deep copy); the remote demo pack in `MarketplaceApiTests` now carries `skillDefIds: ["demo_skill"]` and asserts the preview's `skillNames` falls back to `demo_skill`. Marketplace tests **20/20**. Browser-verified (after rebuilding web): the preview still renders 2 rows / 2 checkboxes, with **0 console errors** while opening the market and previewing. `node --check`, the i18n audit **zh=en=1853, missing 0**, `check-orphan-keys` and `dotnet build` 0 errors all pass; health 200 after rebuilding web.

# AG-UI 群聊桌面版 1.0.239 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.239 Release Notes (current Windows desktop release)

**版本说明**：**模板市场可按需勾选子集安装**——预览里每行可勾选（默认全选），只把需要的数字员工装进自己的目录。
**Version note**: **The marketplace can install a checked subset** — each preview row is checkable (all checked by default) so you install only the agents you need.

## 变更
# Changes

中文：
- **服务端**：`POST /ag-ui/marketplace/import/{packId}` 与 `/install` 接受可选 `agentIds`（空 / 缺省 = 整包）；新增 `FilterAgents` 统一过滤，显式给了名单但一个都不匹配时返回 400「所选数字员工为空」。过滤后仍走原 `ImportDefinitions` 两趟导入（先定新 ID，再重映射包内引用）；**被排除成员的引用按“包外”保留原值**。
- **前端**：预览弹窗每行加勾选框 + 顶部「全选」开关；「安装」只提交勾选的 `agentIds`；一个都不选时提示 `market.previewNoneSelected` 且不发请求。
- i18n 新增 `market.previewNoneSelected`。
English:
- **Server**: `POST /ag-ui/marketplace/import/{packId}` and `/install` accept an optional `agentIds` (empty / omitted = whole pack); a shared `FilterAgents` does the filtering, returning 400 "selected agents are empty" when an explicit list matches nothing. The filtered set still flows through `ImportDefinitions`'s two-pass import (assign new IDs, then remap intra-pack references); **references to excluded members are kept as-is (treated as outside the pack)**.
- **Front end**: preview rows gain checkboxes plus a "Select all" toggle at the top; "Install" submits only the checked `agentIds`; selecting none prompts `market.previewNoneSelected` and issues no request.
- New i18n key `market.previewNoneSelected`.

## 文档
# Docs

中文：`docs/user-guide.md` / `en.md` §3.12（预览勾选子集安装与接口 `agentIds`）；协议 `AG-UI 群聊扩展协议标准 v1.0.md` / `PROTOCOL.en.md` §5.9 表格。
English: `docs/user-guide.md` / `en.md` §3.12 (subset install from the preview and the `agentIds` parameters); the protocol `AG-UI 群聊扩展协议标准 v1.0.md` / `PROTOCOL.en.md` §5.9 table.

## 测试
# Tests

中文：`MarketplaceApiTests` 新增 `Import_Subset_AndValidation`（内置整包 2 → 只装 `agent_finance` 得 1；名单全不匹配 400；远程 `demo` 只装 `mp_member` 得 1），Marketplace 相关合跑 **19/19**。浏览器实测（重建 web 后）：预览列出 2 行且**默认全选**；「全选」取消 → 全不选；此时点「安装」**弹窗不关、不发请求**（守卫生效）；再全选 → 逐行回勾；无页面报错。`node --check`、i18n 审计 **zh=en=1852，missing 0**、`check-orphan-keys`、`dotnet build` 0 错误均过；重建 web 后健康 200。
English: `MarketplaceApiTests` gains `Import_Subset_AndValidation` (whole built-in pack 2 → only `agent_finance` gives 1; a fully non-matching list is 400; remote `demo` with only `mp_member` gives 1); Marketplace tests **19/19**. Browser-verified (after rebuilding web): the preview lists 2 rows **all checked by default**; toggling "Select all" off unchecks all; clicking "Install" then **keeps the dialog open and issues no request** (the guard works); toggling back re-checks every row; no page errors. `node --check`, the i18n audit **zh=en=1852, missing 0**, `check-orphan-keys` and `dotnet build` 0 errors all pass; health 200 after rebuilding web.

# AG-UI 群聊桌面版 1.0.238 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.238 Release Notes (current Windows desktop release)

**版本说明**：**模板市场新增「安装前预览」**——安装前先看清包内数字员工（昵称 / 描述 / 技能数 / 知识库数 / 记忆类型），预览本身不落库。
**Version note**: **The template marketplace gains a pre-install preview** — inspect a pack's agents (nickname / description / skill count / knowledge-base count / memory type) before installing; the preview itself writes nothing.

## 变更
# Changes

中文：
- **服务端** `GET /ag-ui/marketplace/preview?packId=&sourceId=`：内置包从 `MarketplaceService.Packs()` 直取；远程包经 `MarketplaceRemoteService.FetchPackAsync` 按需拉取（同样受 http/https、2MB、10s、不开重定向防护）；`sourceId` 空 = 内置；只回清单、**不落库**。
- **前端**：每个包旁新增「预览」按钮 → `#marketPreviewModal` 列出数字员工（`AI` 标签 + 昵称 + `agentId` + 描述 + `技能 n · 知识库 n · 🧠 记忆类型`）；弹窗内可直接「安装」。Esc / 取消 / 点遮罩关闭。
- i18n 新增 `market.preview` / `previewTitle` / `previewMeta` / `previewEmpty` / `previewFail` / `previewSkills` / `previewKb`。
English:
- **Server** `GET /ag-ui/marketplace/preview?packId=&sourceId=`: built-in packs are taken directly from `MarketplaceService.Packs()`; remote packs are fetched on demand via `MarketplaceRemoteService.FetchPackAsync` (same http/https, 2MB, 10s, no-redirect guards); empty `sourceId` = built-in; returns a listing only, **never writes**.
- **Front end**: a "Preview" button per pack → `#marketPreviewModal` lists the agents (`AI` tag + nickname + `agentId` + description + `Skills n · Knowledge n · 🧠 memory type`); "Install" is available right in the dialog. Esc / Cancel / overlay click closes it.
- New i18n keys `market.preview` / `previewTitle` / `previewMeta` / `previewEmpty` / `previewFail` / `previewSkills` / `previewKb`.

## 文档
# Docs

中文：`docs/user-guide.md` / `en.md` §3.12（新增「安装前预览」与接口）；`README.md` 市场条目；协议 `AG-UI 群聊扩展协议标准 v1.0.md` / `PROTOCOL.en.md` §5.9 表格。
English: `docs/user-guide.md` / `en.md` §3.12 (the new "pre-install preview" and the endpoint); `README.md` marketplace entry; the protocol `AG-UI 群聊扩展协议标准 v1.0.md` / `PROTOCOL.en.md` §5.9 table.

## 测试
# Tests

中文：`MarketplaceApiTests` 新增 `Preview_BuiltinAndRemote_AndValidation`（内置预览 200 且含 agent / 远程预览 200 且 2 个 agent、首个 `agentId=mp_lead` / 缺 `packId` 400），合跑 **4/4**。浏览器实测（重建 web 后）：管理员登录 → 开市场 → 内置 3 个包均有「预览」；点首个 → 弹窗标题「法务协作包」、元信息「2 个数字员工…」、列出 2 行、「安装」可用；**开市场与预览期间零控制台报错**（登录阶段的预存噪声与本次无关）。`node --check`、i18n 审计 **zh=en=1851，missing 0**、`check-orphan-keys`、`dotnet build` 0 错误均过；重建 web 后健康 200。
English: `MarketplaceApiTests` gains `Preview_BuiltinAndRemote_AndValidation` (built-in preview 200 with agents / remote preview 200 with 2 agents, first `agentId=mp_lead` / missing `packId` 400), **4/4**. Browser-verified (after rebuilding web): admin login → open market → all 3 built-in packs show "Preview"; clicking the first → dialog title "法务协作包", meta "2 个数字员工…", 2 rows, "Install" enabled; **zero console errors while opening the market and previewing** (login-phase noise is unrelated). `node --check`, the i18n audit **zh=en=1851, missing 0**, `check-orphan-keys` and `dotnet build` 0 errors all pass; health 200 after rebuilding web.

# AG-UI 群聊桌面版 1.0.237 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.237 Release Notes (current Windows desktop release)

**版本说明**：**输出物可一键「定位到该消息」**——在「输出物」里看到某个附件后，点「📍」即关弹窗并跳到携带它的那条消息（对话区闪烁高亮）。
**Version note**: **Outputs items can jump straight to their message** — spot an attachment in Outputs, click "📍" to close the dialog and jump to the message that carries it (with a flash highlight in the chat).

## 变更
# Changes

中文：
- **「📍 定位」**（`.gf-locate`）：仅当附件来自某条消息（`a.messageId`）时出现；点击 → `locateGroupFile(messageId, topicId)` 关弹窗并定位。
- **抽取复用**：把原搜索跳转的深层定位逻辑抽为通用 `locateMessage(messageId, topicId)`——切话题、若不在虚拟窗口则拉 `messages/around` 前后文重建窗口、再 `scrollToMessage` 闪烁高亮；`jumpToSearchHit` 改为调用它（并保留搜索命中词高亮）。
- i18n 新增 `gf.locate`。
English:
- **"📍 Locate"** (`.gf-locate`): appears only when the attachment belongs to a message (`a.messageId`); clicking it calls `locateGroupFile(messageId, topicId)` to close the dialog and locate.
- **Extraction/reuse**: the search jump's deep-locate logic is extracted into a generic `locateMessage(messageId, topicId)` — switches topic, fetches the `messages/around` window when the target is outside the virtual window, then `scrollToMessage` flash-highlights; `jumpToSearchHit` now calls it (keeping the search-term highlight).
- New i18n key `gf.locate`.

## 文档
# Docs

中文：`docs/user-guide.md` / `en.md` §7.12.3（行内新增「📍 定位」说明）。
English: `docs/user-guide.md` / `en.md` §7.12.3 (the new "📍 Locate" row action).

## 测试
# Tests

中文：浏览器实测（重建 web 后）：合成两条附件（一条带 `messageId`、一条不带）→ 「📍」只出现 1 个、`title` 已本地化（“定位到该消息”）；`locateGroupFile` 存在；点击后输出物弹窗被关闭（`hidden`）；页面无报错。`node --check` 与 i18n 审计（**zh=en=1844，missing 0**）、`check-orphan-keys` 全过；重建 web 后健康 200。
English: browser-verified (after rebuilding web): with two synthetic attachments (one with `messageId`, one without) exactly one "📍" renders and its `title` is localized ("定位到该消息"); `locateGroupFile` exists; clicking closes the Outputs dialog (`hidden`); no page errors. `node --check` and the i18n audit (**zh=en=1844, missing 0**), `check-orphan-keys` all pass; health 200 after rebuilding web.

# AG-UI 群聊桌面版 1.0.236 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.236 Release Notes (current Windows desktop release)

**版本说明**：**消息多选支持「按发送者勾选」**——多选工具条新增下拉，一键选入当前话题里某位发送者的全部（可批量复制 / 转发 / 删除）。
**Version note**: **Message multi-select gains "select by sender"** — the multi-select toolbar has a new dropdown that selects all of one sender's messages in the current topic in one go (for batch copy / forward / delete).

## 变更
# Changes

中文：
- **`#selectSender` 下拉**（多选工具条内）：列出**当前话题内**出现过的发送者（`renderSelectSenderOptions` 按出现顺序去重，首项 `sel.senderAll`「全部发送者」），选项文案本地化。
- **`selectBySender(senderId)`**：选入该发送者在本话题内**非系统 / 未撤回**的消息（替换现有选择；空值 = 全部），与「全选」同源地强制重建列表并刷新工具条。
- 随 `enterSelectMode` 与语言切换（`i18nchanged`）刷新下拉。i18n 新增 `sel.senderAll` / `sel.senderTip`。
English:
- **`#selectSender` dropdown** (inside the multi-select toolbar): lists the senders that appear **in the current topic** (`renderSelectSenderOptions`, deduped in appearance order; the first item is `sel.senderAll` "All senders"), with localized options.
- **`selectBySender(senderId)`**: selects that sender's **non-system / non-recalled** messages in the topic (replacing the current selection; empty = all), forcing a list rebuild and refreshing the toolbar just like "Select all".
- The dropdown is refreshed on `enterSelectMode` and on language change (`i18nchanged`). New i18n keys `sel.senderAll` / `sel.senderTip`.

## 文档
# Docs

中文：`docs/user-guide.md` / `en.md` §1.2.3（工具条控件列表）与 §2.7.7（新增「按发送者勾选」说明）。
English: `docs/user-guide.md` / `en.md` §1.2.3 (toolbar control list) and §2.7.7 (the new "select by sender" note).

## 测试
# Tests

中文：浏览器实测（重建 web 后）：合成当前话题消息（张三 ×2、李四 ×1、张三系统 ×1、李四已撤回 ×1、另一话题张三 ×1）后，下拉选项为 `全部发送者 / 张三 / 李四`；`selectBySender("u1")` 选 2 条、`("")` 选 3 条、`("u2")` 选 1 条（正确排除系统 / 已撤回 / 跨话题）；下拉在多选条内、`title` 已本地化；页面无报错。`node --check` 与 i18n 审计（**zh=en=1843，missing 0**）、`check-orphan-keys` 全过；重建 web 后健康 200。
English: browser-verified (after rebuilding web): with synthetic topic messages (Zhang ×2, Li ×1, Zhang system ×1, Li recalled ×1, Zhang in another topic ×1), the dropdown options are `All senders / 张三 / 李四`; `selectBySender("u1")` selects 2, `("")` selects 3, `("u2")` selects 1 (correctly excluding system / recalled / cross-topic); the dropdown sits in the select bar and its `title` is localized; no page errors. `node --check` and the i18n audit (**zh=en=1843, missing 0**), `check-orphan-keys` all pass; health 200 after rebuilding web.

# AG-UI 群聊桌面版 1.0.235 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.235 Release Notes (current Windows desktop release)

**版本说明**：**输出物弹窗新增按类型 / 时间筛选与批量下载**——知聚的附件越积越多时，原来只能按文件名筛、逐个下载。
**Version note**: **The Outputs dialog gains type/time filtering and batch download** — as a circle's attachments pile up, previously you could only filter by file name and download one at a time.

## 变更
# Changes

中文：
- **类型筛选** `#groupFilesKindFilter`：全部类型 / 文档 / 表格 / 演示 / PDF / 图片 / 音频 / 其它（`gfCategory` 依扩展名与 `kind` 判定）。
- **时间筛选** `#groupFilesTimeFilter`：全部时间 / 今天 / 近 7 天 / 近 30 天（`gfTimeCutoff`）。
- **行首勾选 + 全选 + 批量下载**：`#groupFilesSelectAll`（含 `indeterminate` 半选态）、`#groupFilesSelCount` 已选计数、`#groupFilesDownloadSel`「下载所选」（逐个建临时 `<a download>`，间隔 350ms）。
- 过滤统一在 `filteredGroupFiles()`；`renderGroupFiles` 把已选集合裁剪到当前可见项（切换筛选不留“隐形已选”）。
English:
- **Type filter** `#groupFilesKindFilter`: All types / Document / Spreadsheet / Presentation / PDF / Image / Audio / Other (`gfCategory` by extension and `kind`).
- **Time filter** `#groupFilesTimeFilter`: Any time / Today / Last 7 days / Last 30 days (`gfTimeCutoff`).
- **Row checkbox + select-all + batch download**: `#groupFilesSelectAll` (with an `indeterminate` half-state), `#groupFilesSelCount` selected count, `#groupFilesDownloadSel` "Download selected" (one temporary `<a download>` per item, 350 ms apart).
- Filtering is unified in `filteredGroupFiles()`; `renderGroupFiles` prunes the selected set to the visible items (no "hidden selection" after switching filters).

## 文档
# Docs

中文：`docs/user-guide.md` / `en.md` §7.12.3 重写（工具栏 + 行首勾选）并新增 §7.12.4「筛选与批量下载」（原 Esc 小节顺延为 7.12.5）。
English: `docs/user-guide.md` / `en.md` §7.12.3 rewritten (toolbar + row checkbox) and a new §7.12.4 "Filtering and batch download" (the Esc subsection becomes 7.12.5).

## 测试
# Tests

中文：浏览器实测（重建 web 后）：`gfCategory` 对 docx/xlsx/pptx/pdf/png/mp3/zip 分别得 `doc/sheet/slide/pdf/image/audio/other`；无筛选 7 行、类型=表格 1 行、近 7 天排除 8 天前项得 6 行；全选 → 已选 7、按钮可用、计数 7；取消一个 → 6 且全选半选态；切到「图片」已选裁剪为 1；选项文案已本地化（“文档”）；页面无报错。`node --check` 与 i18n 审计（**zh=en=1841，missing 0**）、`check-orphan-keys`（index 857 引用）全过；重建 web 后健康 200。
English: browser-verified (after rebuilding web): `gfCategory` maps docx/xlsx/pptx/pdf/png/mp3/zip to `doc/sheet/slide/pdf/image/audio/other`; no filter 7 rows, type=Spreadsheet 1 row, last-7-days excludes the 8-day-old item (6 rows); select all → 7 selected, button enabled, count 7; uncheck one → 6 with an indeterminate select-all; switching to Image prunes the selection to 1; option labels are localized ("文档"); no page errors. `node --check` and the i18n audit (**zh=en=1841, missing 0**), `check-orphan-keys` (857 index refs) all pass; health 200 after rebuilding web.

# AG-UI 群聊桌面版 1.0.234 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.234 Release Notes (current Windows desktop release)

**版本说明**：**PPT 在线播放支持拖拽进度条跳页**——播放控制条新增进度条（range），拖动即跳到任意页（此前只能按钮 / 键盘逐页）。
**Version note**: **PPT online playback now supports seeking by dragging a progress bar** — the play control bar gains a seek slider (range); dragging jumps to any page (previously only buttons / keyboard page-by-page).

## 变更
# Changes

中文：
- **进度条 `#docPlaySeek`**：控制条最左侧新增 range；`updateDocPlaySeek` 在每次 `renderDocPlay` 后同步 `max = 页数-1` 与当前页；单页时 `disabled` 置灰。i18n `docPreview.seek`（悬浮提示 / aria）。
- **拖动跳页 `docSeekGo(idx)`**：重置精灵步、渲染目标页、播该页转场（跨幻灯片时），并重设自动前进；输入带 80ms 防抖（`scheduleDocSeek`），拖动中多次重渲染由 `renderPageToCanvas` 的 `docRenderSeq` 丢弃过期帧。退出播放时清除防抖定时器。
English:
- **Progress bar `#docPlaySeek`**: a range at the left of the control bar; `updateDocPlaySeek` syncs `max = pages-1` and the current page after every `renderDocPlay`; greyed out (`disabled`) for a single page. i18n `docPreview.seek` (tooltip / aria).
- **Seek `docSeekGo(idx)`**: resets the sprite step, renders the target page, plays that page's transition (on a slide change) and re-arms auto-advance; input is debounced 80 ms (`scheduleDocSeek`), and stale frames during a drag are dropped by `renderPageToCanvas`'s `docRenderSeq`. The debounce timer is cleared on exiting playback.

## 文档
# Docs

中文：`docs/user-guide.md` / `en.md` §7.8.6 控制条小节补充进度条与拖动跳转说明。
English: `docs/user-guide.md` / `en.md` §7.8.6 (control bar) documents the progress bar and drag-to-seek.

## 测试
# Tests

中文：浏览器实测（重建 web 后）：`#docPlaySeek` 在控制条内、`title` 已本地化；无 PDF → `disabled`；5 页 → `max=4`/`value=2`/可用；单页 → `disabled`；播放态跟随 `docPlayPage`（9 页 → `max=8`/`value=6`）；`docSeekGo` 在非播放态安全早退；页面无报错。`node --check` 与 i18n 审计（**zh=en=1823，missing 0**）、`check-orphan-keys`（index 841 引用）全过；重建 web 后健康 200。
English: browser-verified (after rebuilding web): `#docPlaySeek` is inside the control bar and its `title` is localized; no PDF → `disabled`; 5 pages → `max=4`/`value=2`/enabled; single page → `disabled`; in playback it follows `docPlayPage` (9 pages → `max=8`/`value=6`); `docSeekGo` safely early-returns outside playback; no page errors. `node --check` and the i18n audit (**zh=en=1823, missing 0**), `check-orphan-keys` (841 index refs) all pass; health 200 after rebuilding web.

# AG-UI 群聊桌面版 1.0.233 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.233 Release Notes (current Windows desktop release)

**版本说明**：**PPT 在线播放转场遵循 `spd` 速度**——此前清单里每页转场的 `speed`（`spd`：`slow`/`med`/`fast`）被服务端解析、下发，但前端未用（CSS 时长写死）。现按 `speed` 调节转场时长。
**Version note**: **PPT online playback transitions honor `spd` speed** — the per-slide transition `speed` (`spd`: `slow`/`med`/`fast`) had been parsed and sent down in the manifest, but the front end ignored it (CSS durations were fixed). The duration now follows `speed`.

## 变更
# Changes

中文：
- **`speed` → 转场时长**：CSS 转场时长改为 `基准 × --doc-tr-scale`（`style.css`）；`runCanvasAnimation(cls, speed)` 按该页转场的 `speed` 设置倍率：`slow` = 1.8、`fast` = 0.55、其它（含 `med` / 缺省）= 1。即淡入 / 缩放：默认 0.45s、`slow` ≈0.81s、`fast` ≈0.25s；推入类默认 0.5s、`slow` 0.9s。
- **清理计时器**：动画类的移除延时改为 `≈基准 × 倍率 + 150ms`，与真实时长对齐（此前固定 600ms）。
- 页内分步仍用短淡入（`playStepFlash` 显式传 `fast`）。
English:
- **`speed` → transition duration**: the CSS transition duration is now `base × --doc-tr-scale` (`style.css`); `runCanvasAnimation(cls, speed)` sets the scale from that page's transition `speed`: `slow` = 1.8, `fast` = 0.55, otherwise (including `med` / absent) = 1. So fade / zoom: 0.45s by default, `slow` ≈0.81s, `fast` ≈0.25s; push-style: 0.5s by default, 0.9s when `slow`.
- **Timer cleanup**: the class-removal delay is now `≈base × scale + 150ms`, matching the real duration (it used to be a fixed 600ms).
- In-page steps still play a short fade-in (`playStepFlash` explicitly passes `fast`).

## 文档
# Docs

中文：`docs/user-guide.md` / `en.md` §7.6.3（转场映射补充 `speed` → 时长）与 §7.14（`speed` 标记为「部分已修复 1.0.233」）；`docs/feature-review-2026-10.md` §8.7/§8.10 状态更新。
English: `docs/user-guide.md` / `en.md` §7.6.3 (transition mapping now notes `speed` → duration) and §7.14 (`speed` marked "partly fixed in 1.0.233"); `docs/feature-review-2026-10.md` §8.7/§8.10 statuses updated.

## 测试
# Tests

中文：浏览器实测（重建 web 后）：`runCanvasAnimation` 后舞台 `animationDuration` 为 `slow` = `0.81s`、`fast` = `0.2475s`、`med` / 缺省 = `0.45s`、push + `slow` = `0.9s`，页面无报错。`node --check` 通过；`dotnet build` 0 错误；重建 web 后健康 200。
English: browser-verified (after rebuilding web): after `runCanvasAnimation`, the stage's `animationDuration` is `slow` = `0.81s`, `fast` = `0.2475s`, `med` / absent = `0.45s`, push + `slow` = `0.9s`, with no page errors. `node --check` passes; `dotnet build` 0 errors; health 200 after rebuilding web.

# AG-UI 群聊桌面版 1.0.232 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.232 Release Notes (current Windows desktop release)

**版本说明**：**PPT 在线播放支持「点击画面推进」并遵循幻灯片自身的 `advClick`**——此前该清单字段被服务端解析、下发，但前端从未读取（清单里的「已核实：解析但未使用」项）。现播放态点击舞台即前进（精灵模式先播下一步形状，否则翻页）。
**Version note**: **PPT online playback now supports click-to-advance and honors the slide's own `advClick`** — the server had been parsing and sending this manifest field down, but the frontend never read it (a "verified: parsed but unused" item in the checklist). Now, during playback, clicking the stage advances (in sprite mode it plays the next shape step, otherwise it turns the page).

## 变更
# Changes

中文：
- **点击推进**：`#docPlayStage` 新增 click 处理——播放态点击画面调用 `docSlideGo(1)`（精灵模式推进下一步形状，否则翻到下一页）。
- **遵循 `advanceOnClick`**：`slideAdvanceOnClick(slide)` 读清单 `transitions[slide].advanceOnClick`；为 `false` 时点击**不推进**（仅自动前进 / 控制条按钮 / 键盘可推进）；无转场元素（`null`）或缺省字段视为可点击推进。控制条按钮在舞台之外，不受影响。
- 与既有「自动前进（`advanceAfterMs`）」互补：自动前进到点仍由 `scheduleAutoAdvance` 触发。
English:
- **Click-to-advance**: a click handler on `#docPlayStage` — during playback, clicking the stage calls `docSlideGo(1)` (sprite mode steps the next shape, otherwise it turns the page).
- **Honors `advanceOnClick`**: `slideAdvanceOnClick(slide)` reads `transitions[slide].advanceOnClick` from the manifest; when `false`, clicking does **not** advance (only auto-advance / control-bar buttons / the keyboard can); no transition element (`null`) or a missing field is treated as click-to-advance. The control-bar buttons are outside the stage and unaffected.
- Complements the existing auto-advance (`advanceAfterMs`), which still fires via `scheduleAutoAdvance`.

## 文档
# Docs

中文：`docs/user-guide.md` / `en.md` 的 §7.6.3、§7.8.2（重写为「`advanceOnClick` 与点击推进」）、§7.8.4（改为「键盘与鼠标操作」）、§7.14、§9.9-4 同步；`docs/feature-review-2026-10.md` §8.7/§8.9/§8.10 状态更新（§8.7、§9.9-4 → 已关闭）。
English: `docs/user-guide.md` / `en.md` §7.6.3, §7.8.2 (rewritten as "`advanceOnClick` and click-to-advance"), §7.8.4 (now "Keyboard and mouse operations"), §7.14 and §9.9-4 updated; `docs/feature-review-2026-10.md` §8.7/§8.9/§8.10 statuses updated (§8.7, §9.9-4 → closed).

## 测试
# Tests

中文：新增 `PresentationPlaybackTests.Transitions_ParseAdvanceOnClickAndSpeed`（最小 pptx：无转场 → `null`；`advClick="0"` + `spd="slow"` + `advTm="3000"` → `AdvanceOnClick=false` / `Speed=slow` / `AdvanceAfterMs=3000`）。浏览器实测：新版 `app.js` 加载无报错；`slideAdvanceOnClick` 对 `[null, {advanceOnClick:false}, {advanceOnClick:true}]` 返回 `[true,false,true]`。`node --check` 通过；重建 web 后健康 200。
English: new `PresentationPlaybackTests.Transitions_ParseAdvanceOnClickAndSpeed` (minimal pptx: no transition → `null`; `advClick="0"` + `spd="slow"` + `advTm="3000"` → `AdvanceOnClick=false` / `Speed=slow` / `AdvanceAfterMs=3000`). Browser-verified: the new `app.js` loads with no errors; `slideAdvanceOnClick` returns `[true,false,true]` for `[null, {advanceOnClick:false}, {advanceOnClick:true}]`. `node --check` passes; health 200 after rebuilding web.

# AG-UI 群聊桌面版 1.0.231 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.231 Release Notes (current Windows desktop release)

**版本说明**：**「需复核点」收尾（B 组）**——逐章清理各章「实现与文档偏差 / 需复核点」里**可关闭**的剩余项：把几处真实现象（登录页复选框无效、帮助面板缺内联兜底、两处孤儿 i18n 键、易误导命名）**真正修掉**，并把组织落库工具挂载的口径收紧。
**Version note**: **Closing the "points to verify" backlog (group B)** — each chapter's remaining closable "implementation vs. documentation deviations / to-verify" items were resolved, fixing several real issues (a no-op login checkbox, missing inline fallbacks in the help panel, two orphan i18n keys, a misleading name) and tightening how the org-commit tool is mounted.

## 修复
# Fixes

中文：
1. **登录页「保持登录状态」复选框此前是无效项（真实缺陷）**：`#authRemember` 无人读取，`storeAuth()` 无条件同时写 `sessionStorage` 与 `localStorage`。现改为按复选框决定：**勾选**（HTML 默认 `checked`）写 `localStorage`（跨浏览器重启保持）；**未勾选**仅写 `sessionStorage` 并清除残留的 `localStorage` 会话（关标签页 / 浏览器即失效）。刷新页面按既有持久会话推断默认值。
2. **帮助面板 `help.s11` / `help.s12` 两组的 `<li>` 缺内联兜底**：其余分组都带内联文案，只有这两组靠 i18n 运行时注入——若 i18n 资源加载失败会显示为空。现已在 `index.html` 补齐这两组的内联兜底文案。
3. **组织落库工具挂载口径收紧**：`AgentCatalog.Create` 原仅按 `SkillDefIds.Contains("org_design")` 判定是否追加 `org_commit` / `org_plan_draft`；现**追加「该技能为系统技能（`OwnerId` 为空）」**条件（`IsSystemOrgDesignSkill`），用户自建同名技能不再误触发（`org_commit` 本身仍在运行期强制校验管理员权限）。
English:
1. **The login page's "Keep me signed in" checkbox was a no-op (real defect)**: `#authRemember` was read by nothing, and `storeAuth()` unconditionally wrote both `sessionStorage` and `localStorage`. It now follows the checkbox: **ticked** (the HTML default is `checked`) writes `localStorage` (persists across browser restarts); **unticked** writes only `sessionStorage` and clears any leftover `localStorage` session (expires when the tab / browser closes). On refresh the default is inferred from an existing persistent session.
2. **The help panel's `help.s11` / `help.s12` groups lacked inline fallbacks**: every other group carries inline copy; these two relied on i18n injection and would render empty if i18n resources failed to load. `index.html` now carries fallback copy for both groups.
3. **Tighter org-commit mounting**: `AgentCatalog.Create` used to decide whether to append `org_commit` / `org_plan_draft` from `SkillDefIds.Contains("org_design")` alone; it now **also requires the skill to be a system skill (`OwnerId` is null)** (`IsSystemOrgDesignSkill`), so a user-created skill with the same name no longer falsely triggers it (`org_commit` still enforces admin rights at runtime).

## 清理
# Cleanup

中文：
- **移除孤儿 i18n 键**：`menu.tasks`（无对应菜单元素）与 `docPreview.openFailed`（`app.js` 未引用，实际用 `docPreview.failed`）已从 `zh.js` / `en.js` 删除（字典键 1824 → 1822）。
- **`applyAccentFromHex` 变量重命名**：`light` → `isDark`（原命名与实际含义「深色模式」相反），逻辑不变。
- **`subAgentCandidates` 排除条件加注释**：说明 `x.agentId !== "agent_" + editingAgentId` 用于排除旧数据里的同源副本。
English:
- **Removed orphan i18n keys**: `menu.tasks` (no matching menu element) and `docPreview.openFailed` (not referenced by `app.js`, which uses `docPreview.failed`) were deleted from `zh.js` / `en.js` (dictionary keys 1824 → 1822).
- **Renamed a variable in `applyAccentFromHex`**: `light` → `isDark` (the old name was the opposite of its meaning, "dark mode"); no logic change.
- **Commented the `subAgentCandidates` exclusion**: explains that `x.agentId !== "agent_" + editingAgentId` excludes legacy same-origin copies.

## 文档
# Docs

中文：
- `docs/memory-personality.md`：`TopK` 默认改为代码默认 `6`（桌面覆盖 5 / Docker 6）；换算示例按 `TopK=6` 基准重算（广记型 11 / 深记型 3 / 存得住想不起型 4→10）；组织配型措辞改为「需反复培训带教才上手的岗位」并列出代码关键词表。
- `docs/user-guide.md` / `docs/user-guide.en.md`：§1.1.1、§1.1.5、§1.12、§3.11、§4.2、§4.11、§6.12、§7.15 同步更新；`BuiltinPptxSkills.Version` 核实为 `2026-09-22.2`。
English:
- `docs/memory-personality.md`: `TopK` default corrected to the code default `6` (desktop override 5 / Docker 6); the worked example is recomputed on a `TopK=6` base (broad 11 / deep 3 / cue-dependent 4→10); the org-assignment wording became "roles that need repeated training and mentoring" plus the code keyword table.
- `docs/user-guide.md` / `docs/user-guide.en.md`: §1.1.1, §1.1.5, §1.12, §3.11, §4.2, §4.11, §6.12, §7.15 updated; `BuiltinPptxSkills.Version` verified as `2026-09-22.2`.

## 部署
# Deployment

中文：`docker-compose.yml` 现暴露客户端技能桥部署开关 `ClientTool__RequireAdmin`（`${CLIENTTOOL_REQUIRE_ADMIN:-false}`，默认 `false` = 单机本机执行）。共享多用户 Web 部署置 `CLIENTTOOL_REQUIRE_ADMIN=true`，本机技能桥仅系统管理员可用，封堵宿主机任意 shell 的 RCE 面（见 `docs/RBAC.md` §4）。
English: `docker-compose.yml` now exposes `ClientTool__RequireAdmin` (`${CLIENTTOOL_REQUIRE_ADMIN:-false}`, default `false` = single-machine local execution). A shared multi-user Web deployment can set `CLIENTTOOL_REQUIRE_ADMIN=true` so only platform admins may use the local skill bridge, closing off the host's arbitrary-shell RCE surface (see `docs/RBAC.md` §4).

## 测试
# Tests

中文：新增回归用例 `Preview_PlayQuery_WithNumericOne_IsRejectedWith400`（固化 `?play=1` → 400 的 `bool?` 绑定行为）、`Play_Endpoint_ReturnsManifest`（`/ag-ui/preview/{id}/play` 清单端点 200 与字段）与 `Preview_And_PlayPdf_SetCacheHeaders`（`/preview` 与 `?play=true` 的 `nosniff` + `private, max-age=300`）；`DocPreviewApiTests` 合跑 **19/19**。i18n 审计 **zh=en=1822，missing 0**；`node --check`、`check-i18n-keys`、`check-orphan-keys` 全过；`dotnet build` 0 错误。
English: new regression tests `Preview_PlayQuery_WithNumericOne_IsRejectedWith400` (pinning the `bool?`-binding behavior of `?play=1` → 400), `Play_Endpoint_ReturnsManifest` (the `/ag-ui/preview/{id}/play` manifest endpoint's 200 and fields) and `Preview_And_PlayPdf_SetCacheHeaders` (`nosniff` + `private, max-age=300` on `/preview` and `?play=true`); `DocPreviewApiTests` **19/19**. The i18n audit is **zh=en=1822, missing 0**; `node --check`, `check-i18n-keys` and `check-orphan-keys` all pass; `dotnet build` 0 errors.

# AG-UI 群聊桌面版 1.0.230 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.230 Release Notes (current Windows desktop release)

**版本说明**：**文档与实现偏差清理（A 组）**——逐一核对 `docs/user-guide.md` §8.17/§8.18 记录的「实现与文档偏差 / 需复核点」，把其中**已可补齐的项**在代码与文档两侧对齐（用户搜索、桥接能力协商接入前端、实时探测、只读参数快照、配置治理键补齐）。**新增/变更均为管理员控制台侧的可见性增强，不改变既有业务行为。**
**Version note**: **A-group deviation cleanup (docs vs. implementation)** — each item recorded in `docs/user-guide.md` §8.17/§8.18 as an "implementation vs. documentation deviation / to-verify" that could be closed was aligned on both the code and doc sides (user search, bridge capability negotiation wired into the frontend, live probe, read-only config snapshot, config-governance key completion). **All additions are admin-console visibility improvements and do not change existing business behaviour.**

## 偏差清理
# Deviation cleanup

中文：
- **A1 管理员用户列表支持搜索**：新增 `#adminUsersSearch` 输入框；`loadAdminUsers()` 拉全量后走 `renderAdminUsers()` / `adminUserRow()` 按「用户名 / 显示名 / 角色」过滤（与其它列表的「x」清除按钮一致）。
- **A2 桥接能力协商接入前端**：新增 `loadBridgeCaps()`（消费 `GET /ag-ui/admin/bridge-capabilities`），「运行指标」页渲染「桥接能力协商」表；`loadAdminMetrics()` 末尾统一刷新桥接健康 / 能力协商 / 副本。
- **A3「立即探测」按钮**：`#bridgeProbeBtn` 通过 `loadBridgeHealth(true)` + `loadBridgeCaps(true)` 触发 `?refresh=true` 实时探测。
- **A4 只读参数快照**：新增 `#cfgSnapshotBtn` / `#cfgSnapshot`，消费 `GET /ag-ui/admin/config`，展开 JSON（配置治理页可直接核对当前生效值）。
- **A5 配置治理补 `messageWriteDebounceMs`**：`#cfgMessageWriteDebounceMs` 加入 `setCfg` / `collectCfg`（UI 键 12 → 13，与后端一致）。
- **A6 `executionOrder` 白名单表述（仅文档）**：查证后 i18n `admin.execOrder` 与 placeholder **均已含 `streaming`**，与代码白名单一致，故只需修正文档描述（`streaming` 恒置末）。
English:
- **A1 Admin user list search**: added `#adminUsersSearch`; `loadAdminUsers()` fetches all then `renderAdminUsers()` / `adminUserRow()` filters by username / display name / role (with the same "x" clear button as other lists).
- **A2 Bridge capability negotiation wired into the frontend**: added `loadBridgeCaps()` (consumes `GET /ag-ui/admin/bridge-capabilities`); the Runtime Metrics tab renders a "Bridge capability negotiation" table; `loadAdminMetrics()` refreshes bridge health / caps / replicas together.
- **A3 "Probe now" button**: `#bridgeProbeBtn` triggers a live `?refresh=true` probe via `loadBridgeHealth(true)` + `loadBridgeCaps(true)`.
- **A4 Read-only config snapshot**: added `#cfgSnapshotBtn` / `#cfgSnapshot`, consuming `GET /ag-ui/admin/config` and expanding the JSON (verify effective values from the Config Governance tab).
- **A5 Config Governance gains `messageWriteDebounceMs`**: `#cfgMessageWriteDebounceMs` added to `setCfg` / `collectCfg` (UI keys 12 → 13, matching the backend).
- **A6 `executionOrder` whitelist wording (docs only)**: verified that i18n `admin.execOrder` and the placeholder **already include `streaming`**, matching the code whitelist, so only the doc description needed fixing (`streaming` always last).

## 修复
# Fixes

中文：
1. **`?refresh=1` 会 400（本轮暴露的真实缺陷）**：`GET /ag-ui/admin/bridge-health` / `bridge-capabilities` 的 `refresh` 为 `bool?` 绑定，**不接受 `1`**；前端与文档已统一改为 **`?refresh=true`**。
English:
1. **`?refresh=1` returned 400 (real defect exposed this round)**: `refresh` on `GET /ag-ui/admin/bridge-health` / `bridge-capabilities` binds as `bool?` and **does not accept `1`**; the frontend and docs now consistently use **`?refresh=true`**.

## 测试
# Tests

中文：i18n 审计 **zh=en=1824，missing 0**；`node --check`（app.js / zh.js / en.js）、`check-i18n-keys`、`check-orphan-keys` 全过。运行栈实测（Playwright + curl）：A1 搜索全量 1 / 过滤 1、A2/A3 能力协商与健康各 1 行、A5 字段存在、A4 快照显示 JSON，**页面无 JS 报错**；`bridge-health?refresh=true` / `bridge-capabilities?refresh=true` / `GET /admin/config` 均 200；重建 web 后健康 200。
English: the i18n audit is **zh=en=1824, missing 0**; `node --check` (app.js / zh.js / en.js), `check-i18n-keys` and `check-orphan-keys` all pass. Verified live (Playwright + curl): A1 search all=1 / filtered=1, A2/A3 caps and health one row each, A5 field present, A4 snapshot shows JSON, **no page JS errors**; `bridge-health?refresh=true` / `bridge-capabilities?refresh=true` / `GET /admin/config` all 200; health 200 after rebuilding web.

# AG-UI 群聊桌面版 1.0.229 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.229 Release Notes (current Windows desktop release)

**版本说明**：**新一轮新增功能的严格代码审核与修复**——审到两处真实缺陷并修复（均与本次新增的「按复杂度选模型 / 绩效反哺」同源的缓存与健壮性问题）。
**Version note**: **Strict code review of the newly added features, with fixes** — two real defects found and fixed (both cache/robustness issues exposed by the new "complexity model routing / performance-feedback" work).

## 修复
# Fixes

中文：
1. **模型变体缓存未失效（真实缺陷）**：`AgentCatalog.Upsert`/`Remove` 原先只删主缓存键 `_agents[agentId]`，**不删按模型分键的变体**（`agentId\u001Fmodel`，即视觉 / 按复杂度路由的变体）——于是**改了数字员工的人设后，走视觉或模型路由的运行仍用旧指令**。现新增 `EvictAgent`：连主键带全部 `agentId\u001F*` 变体一并失效；并加回归用例 `Upsert_EvictsModelKeyedVariants_Too`。
2. **候选重复键会抛异常（健壮性）**：绩效反哺下 `RankAssignTargetsAsync` 曾用 `candidates.ToDictionary(...)` 建名字表——**候选列表含重复 agentId 时抛「重复键」**（`AssignmentIds` 未去重）。现改为传**名称查找委托**（`Func<string,string>`），从根上消除重复键风险（也少一次分配）；加回归用例 `BuildLines_DuplicateCandidates_DoNotThrow`。
English:
1. **Model-variant cache not invalidated (real defect)**: `AgentCatalog.Upsert`/`Remove` only removed the base key `_agents[agentId]`, **not the model-keyed variants** (`agentId\u001Fmodel`, i.e. vision / model-routed variants) — so after editing an employee's persona, vision or model-routed runs still used the **old instructions**. Added `EvictAgent` to evict the base key plus every `agentId\u001F*` variant; regression test `Upsert_EvictsModelKeyedVariants_Too`.
2. **Duplicate candidate keys threw (robustness)**: under performance feedback, `RankAssignTargetsAsync` used `candidates.ToDictionary(...)` for a name map — a **duplicate candidate agentId threw a duplicate-key error** (`AssignmentIds` isn't de-duplicated). Now it passes a **name lookup delegate** (`Func<string,string>`), removing the risk at the root (and one allocation); regression test `BuildLines_DuplicateCandidates_DoNotThrow`.

## 测试
# Tests

中文：新增 2 个回归用例；连同 AgentGateway / AgentCatalogDynamic / AgentPerformance / EvalApi / EvalScore / OrgHealthReport / AdminApi / DesktopComposition 合跑 **86/86**。i18n 审计 **zh=en=1813，missing 0**；`node --check` 通过。运行栈实测：重建 web 后健康 200。
English: 2 new regression tests; **86/86** with AgentGateway / AgentCatalogDynamic / AgentPerformance / EvalApi / EvalScore / OrgHealthReport / AdminApi / DesktopComposition. The i18n audit is **zh=en=1813, missing 0**; `node --check` passes. Verified live: health 200 after rebuilding web.

# AG-UI 群聊桌面版 1.0.228 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.228 Release Notes (current Windows desktop release)

**版本说明**：**绩效反哺编排（候选复用排序，默认关）**——开启后，任务指派路由会把候选数字员工的**近期好评率**（👍👎）作为参考注入提示词，让指派更偏向「实测靠谱」的员工（数据驱动，而非仅静态职责）。**默认关 = 行为与旧版完全一致。**
**Version note**: **Performance-feedback routing (candidate ranking, off by default)** — when enabled, task-assignment routing injects candidates' **recent positive rate** (👍👎) into the prompt, biasing assignment toward employees that have proven reliable (data-driven rather than role-description-only). **Off by default = identical to before.**

## 绩效反哺编排
# Performance-feedback routing

中文：新增 `Agents:PerformanceFeedback`（`Enabled` / `WindowDays` 默认 30 / `MinRatings` 默认 2；Docker `AGENTS_PERFORMANCEFEEDBACK_*`，**默认关**）：
- **注入点**：`AgentGateway.RankAssignTargetsAsync`（任务指派目标判定）——在候选职责之后追加「候选近期绩效」参考行（如 `a1（助理）：近期好评率 67%（2👍/1👎，30 天内）`），并声明「仅供参考；同等匹配时优先好评率高者、避开明显偏低者」。
- **口径**：仅统计**窗口内**反馈；候选反馈样本**不足 `MinRatings` 不列**（样本太少不可信）。各候选统计相互独立，**不会跨员工抹平**。
- **实现**：纯函数 `PerformanceRoutingHint.BuildLines`（便于单测）；数据来自既有消息反馈存储，无新增存储。
- **设计取舍（为何默认关）**：绩效是**行为信号**，若直接改写路由结果可能引入抖动（好评是滞后、稀疏、带主观性的）。因此采取「**只把数据给模型、由模型综合职责与绩效判断**」的最小干预方式，并默认关闭、可随开关启用。
English: new `Agents:PerformanceFeedback` (`Enabled` / `WindowDays` default 30 / `MinRatings` default 2; Docker `AGENTS_PERFORMANCEFEEDBACK_*`, **off by default**):
- **Injection point**: `AgentGateway.RankAssignTargetsAsync` (assignment target decision) — after candidate duties, append "candidate recent performance" reference lines (e.g. `a1（助理）：近期好评率 67%（2👍/1👎，30 天内）`) plus a note: "reference only; prefer higher positive rate on a tie, avoid clearly low ones".
- **Scope**: counts only feedback **within the window**; candidates with **fewer than `MinRatings`** samples are omitted (too few to trust). Each candidate is counted independently — **no cross-employee smoothing**.
- **Implementation**: a pure `PerformanceRoutingHint.BuildLines` (unit-tested); data from the existing message-feedback store — no new storage.
- **Design trade-off (why off by default)**: performance is a **behavioural signal**; rewriting routing outright could introduce churn (praise is lagging, sparse and subjective). So it takes the minimal-intervention form of **giving the data to the model and letting it weigh duties vs. performance**, off by default and toggleable.

## 测试
# Tests

中文：新增 `PerformanceRoutingHintTests`（仅列足够近期反馈者 / 降 `MinRatings` 后纳入稀疏候选 / 无反馈为空）；连同 AgentGateway / AgentPerformance / AdminApi / DesktopComposition / EvalApi 合跑 **75/75**。i18n 审计 **zh=en=1813，missing 0**；`node --check` 与 appsettings JSON 校验通过。运行栈实测：健康 200、绩效端点 200（默认关，无行为变化）。
English: new `PerformanceRoutingHintTests` (only candidates with enough recent feedback / lower `MinRatings` includes sparse ones / no feedback yields empty); **75/75** with AgentGateway / AgentPerformance / AdminApi / DesktopComposition / EvalApi. The i18n audit is **zh=en=1813, missing 0**; `node --check` and appsettings JSON validation pass. Verified live: health 200 and the performance endpoint 200 (off by default, no behaviour change).

# AG-UI 群聊桌面版 1.0.227 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.227 Release Notes (current Windows desktop release)

**版本说明**：**评测用例支持「期望交付格式」**——每条用例可选一个交付格式（docx / pptx / xlsx / pdf / text / image）；跑分时除「期望要点」外，还要求**回复中体现该格式**（同义词命中），用于检查数字员工的**格式意图**。
**Version note**: **Eval cases can expect a deliverable format** — each case may pick a format (docx / pptx / xlsx / pdf / text / image); a run then also requires the **reply to mention that format** (synonym hit), checking the employee's **format intent**.

## 期望交付格式
# Expected deliverable format

中文：
- **数据**：`EvalCase.ExpectDeliverable`（`docx`/`pptx`/`xlsx`/`pdf`/`text`/`image`，空 = 不限）；服务端规范化（大写/带空格→小写键，未知格式存空）。
- **判定**：`AgentEvalRunner.Score` 新增 `expectDeliverable` 参数——在「期望要点」判定之外，还要求回复命中该格式的同义词（如 docx → docx/word/文档；pptx → pptx/ppt/powerpoint/幻灯片/演示文稿）；未指定或未知格式不卡。结果新增 `deliverableOk`。
- **前端**：新建/编辑用例弹窗新增「期望交付格式」下拉；列表中「判定」列显示已选格式标签，未命中时在结果里标「（未体现 {格式}）」。
- **口径说明（重要）**：评测跑的是**裸模型（不带工具 / 技能）**以保证确定性，因此这里校验的是「回复中是否体现该交付格式」的**格式意图**，**不是**真实产出文件；真实文件生成由平台既有技能回归与端到端用例覆盖。此限制已在代码注释 / 手册中如实标注。
English:
- **Data**: `EvalCase.ExpectDeliverable` (`docx`/`pptx`/`xlsx`/`pdf`/`text`/`image`; empty = any); normalized server-side (uppercase/whitespace → lowercase key, unknown → empty).
- **Judging**: `AgentEvalRunner.Score` gains an `expectDeliverable` parameter — beyond the "expected points" rule, the reply must also hit that format's synonyms (e.g. docx → docx/word/文档; pptx → pptx/ppt/powerpoint/幻灯片/演示文稿); unspecified or unknown formats don't gate. The result gains `deliverableOk`.
- **Front end**: the create/edit dialog gains an "expected deliverable format" dropdown; the list's "match" column shows the chosen format tag, and on a miss the result is annotated "(did not mention {fmt})".
- **Scope note (important)**: the eval runs a **bare model (no tools / skills)** for determinism, so this checks the **format intent** of the reply — **not** real file production; actual file generation is covered by the platform's existing skill regression and end-to-end cases. This limitation is stated honestly in code comments and the manual.

## 测试
# Tests

中文：`EvalScoreTests` 新增交付格式用例（未提及不通过 / word·文档通过 / 未指定与未知不卡 / 规范化）；`EvalApiTests` 断言 `expectDeliverable` 规范化落库（` PPTX ` → `pptx`）；连同 OrgHealthReport / AgentPerformance / AdminApi / DesktopComposition 合跑 **30/30**。i18n 审计 **zh=en=1813，missing 0**；`node --check` 通过。运行栈实测：建带 `expectDeliverable=PPTX` 的用例→落库为 `pptx`，跑分返回 `deliverableOk=false`。
English: `EvalScoreTests` gains deliverable cases (not-mentioned fails / word·文档 passes / unspecified and unknown don't gate / normalization); `EvalApiTests` asserts `expectDeliverable` is normalized on write (` PPTX ` → `pptx`); **30/30** with OrgHealthReport / AgentPerformance / AdminApi / DesktopComposition. The i18n audit is **zh=en=1813, missing 0**; `node --check` passes. Verified live: create a case with `expectDeliverable=PPTX` → stored as `pptx`, and a run returns `deliverableOk=false`.

# AG-UI 群聊桌面版 1.0.226 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.226 Release Notes (current Windows desktop release)

**版本说明**：**评分趋势（多次跑分折线）**——「评测回归」页新增一条**通过率折线**：每次跑分记录 `{时间, 用例数, 通过数}` 并持久化（扩展区「evalRuns」，保留最近 200 次），页内以固定 0–100% 纵轴展示最近若干次跑分的通过率趋势，一眼看出「改提示词/换模型后有没有变差」。
**Version note**: **Pass-rate trend (a line over runs)** — the Eval & regression tab gains a **pass-rate line**: each run records `{time, cases, passed}` and persists it ("evalRuns" extension area, last 200 kept), drawn with a fixed 0–100% y-axis so you can see at a glance whether prompt/model changes made things worse.

## 评分趋势
# Pass-rate trend

中文：
- **记录与持久化**：`POST /ag-ui/admin/eval-cases/run` 跑完全部用例后记一条历史 `{atMs, total, passed}`（保留最近 200 条，扩展区「evalRuns」独立一段，不动「evalCases」的持久化形状、向后兼容）。
- **接口**：`GET /ag-ui/admin/eval-cases` 新增 `runs` 数组（按时间升序）。
- **前端**：「🧪 评测回归」页在工具栏下方新增**趋势折线**（≥ 2 次跑分才显示）：纵轴固定 0/50/100%，横轴为历次跑分；标题行显示「最近 N 次跑分 · 最新通过率 P%（时间）」。刚跑完会立即把本次追加到折线上（无需刷新）。
English:
- **Record & persist**: after running all cases, `POST /ag-ui/admin/eval-cases/run` appends a history entry `{atMs, total, passed}` (last 200 kept; a separate "evalRuns" extension section that leaves the "evalCases" persisted shape untouched and backward-compatible).
- **API**: `GET /ag-ui/admin/eval-cases` now returns a `runs` array (ascending by time).
- **Front end**: the "🧪 Eval & regression" tab gains a **trend line** below the toolbar (shown once ≥ 2 runs exist): fixed 0/50/100% y-axis, x-axis over runs; the caption reads "last N runs · latest pass rate P% (time)". A just-finished run is appended immediately without a refresh.

## 测试
# Tests

中文：`EvalApiTests` 新增 `runs` 形状与历史累积断言（跑分后 `runs` 长度 ≥ 1）；连同 EvalScore / OrgHealthReport / AgentPerformance / AdminApi / DesktopComposition 合跑 **29/29**。i18n 审计 **zh=en=1810，missing 0**；`node --check` 通过。运行栈实测：跑 2 次后 `runs` 返回 2 条记录；Playwright 实测趋势折线与标题渲染正常、无前端报错。
English: `EvalApiTests` gains `runs` shape and history-accumulation assertions (after a run, `runs` length ≥ 1); **29/29** with EvalScore / OrgHealthReport / AgentPerformance / AdminApi / DesktopComposition. The i18n audit is **zh=en=1810, missing 0**; `node --check` passes. Verified live: after 2 runs, `runs` returns 2 entries; Playwright confirms the trend line and caption render with no front-end errors.

# AG-UI 群聊桌面版 1.0.225 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.225 Release Notes (current Windows desktop release)

**版本说明**：**评测基线持久化**——每次跑分结果（通过与否 / 时间 / 回复摘录）随用例落库（扩展区「evalCases」）。好处：① 「评测回归」页刷新 / 重开后仍能看到**最近结果**；② **组织体检报告新增「六、评测回归」**（用例数 / 已跑过 / 通过率 + 明细表）。
**Version note**: **Persisted eval baselines** — each run's result (pass/fail / time / reply excerpt) is saved with the case (in the "evalCases" extension area). Benefits: (1) the Eval & regression tab keeps showing **last results** after reload; (2) the **org health report gains a "6. Eval regression" section** (case count / run count / pass rate + detail table).

## 评测基线持久化
# Persisted eval baselines

中文：`EvalCase` 新增 `LastPassed` / `LastRunAtMs` / `LastReply` / `LastError`：
- **跑分时写入**：`POST /ag-ui/admin/eval-cases/run` 对每条用例跑完即把上述字段写回用例并 `store.Put`（随「evalCases」一起持久化、重启不丢），并广播变更让其它客户端页签刷新。
- **编辑即失效**：`POST /ag-ui/admin/eval-cases`（更新）会清空该用例的跑分基线，避免报告 / 页签显示过期结果。
- **页签回显**：「测评回归」页加载时从 `lastRunAtMs` / `lastPassed` 重建「最近结果」列（不再只存于内存、刷新即丢）。
- **报告集成**：`GET /ag-ui/admin/org-health` 新增 **「## 六、评测回归」**——用例数 / 已跑过数 / **通过率** + （用例 × 数字员工 × 结果 × 最近跑分）明细表；无用例时提示去创建。
- 实现：`OrgHealthReport.BuildMarkdown` 新增可选 `evalCases` 参数（默认 null → 不出该节）；纯函数仍便于单测。
English: `EvalCase` gains `LastPassed` / `LastRunAtMs` / `LastReply` / `LastError`:
- **Written on run**: `POST /ag-ui/admin/eval-cases/run` writes these back per case and `store.Put`s them (persisted with "evalCases", surviving restart) and broadcasts a change so other clients' tabs refresh.
- **Invalidated on edit**: `POST /ag-ui/admin/eval-cases` (update) clears the case's baseline to avoid stale results in the report / tab.
- **Tab rehydration**: the Eval & regression tab rebuilds the "last result" column from `lastRunAtMs` / `lastPassed` on load (no longer memory-only, so a reload keeps them).
- **Report integration**: `GET /ag-ui/admin/org-health` gains a **"## 6. Eval regression"** section — case count / run count / **pass rate** + a table (case × agent × result × last run); a hint to create cases when there are none.
- Implementation: `OrgHealthReport.BuildMarkdown` gains an optional `evalCases` parameter (default null → section omitted); still a pure function for unit testing.

## 测试
# Tests

中文：`OrgHealthReportTests` 新增评测回归节断言（含「通过率 50%」）；`EvalApiTests` 新增基线持久化断言（跑分后重列用例带 `lastRunAtMs > 0` / `lastPassed`）；连同 AdminApi / DesktopComposition / AgentPerformance 合跑 **29/29**。i18n 审计 **zh=en=1809，missing 0**；`node --check` 通过。运行栈实测：建用例 → 跑分（`passed 0`）→ 重列用例 `lastRunAtMs=…/lastPassed=false`；`org-health` 报告含「## 六、评测回归」与通过率表。
English: `OrgHealthReportTests` gains an eval-section assertion (incl. "pass rate 50%"); `EvalApiTests` gains a baseline-persistence assertion (after a run, re-listing cases carries `lastRunAtMs > 0` / `lastPassed`); **29/29** with AdminApi / DesktopComposition / AgentPerformance. The i18n audit is **zh=en=1809, missing 0**; `node --check` passes. Verified live: create a case → run (`passed 0`) → re-list shows `lastRunAtMs=…/lastPassed=false`; the `org-health` report contains "## 6. Eval regression" with a pass-rate table.

# AG-UI 群聊桌面版 1.0.224 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.224 Release Notes (current Windows desktop release)

**版本说明**：**组织体检报告（可导出）**——把「数字员工绩效（调用 / token / 成本 / 好评率 / 关注标记）」与「组织连接（指派 / 提升 / 流水线）」汇成一份 **Markdown 报告**，控制台「员工绩效」页一键下载，便于运营复盘与对外汇报。
**Version note**: **Org health report (exportable)** — combines **digital-employee performance** (calls / tokens / cost / positive rate / attention flags) with **org connections** (assignment / escalation / pipeline) into a **Markdown report**, downloadable in one click from the console's "Agent performance" tab, for operational reviews and reporting.

## 组织体检报告
# Org health report

中文：新增端点 `GET /ag-ui/admin/org-health?days=N`（Operator+，只读）→ `{ filename, markdown }`：
- **总览**：员工数（窗口内有调用数）/ 调用次数 / Token / 成本（未配单价则注明）/ 综合好评率 / 组织连接数（指派·提升·流水线）。
- **需要关注**：带 `low-praise` / `idle` 标记的员工及原因。
- **绩效明细表**：数字员工 × （调用 / Token / 成本 / 👍 / 👎 / 好评率 / 最近活跃 / 关注），按调用降序。
- **组织架构**：每位员工的「指派 → …；提升 → …；流水线 → …」。
- **建议**：由标记与成本推导的确定性建议（复核低分岗位 / 归档闲置岗位 / 评估复杂度模型路由）。
- **前端**：「📈 员工绩效」页新增 **「📄 组织体检报告」** 按钮，按当前窗口生成并下载 `org-health-YYYYMMDD.md`。
- 实现：纯函数 `OrgHealthReport.BuildMarkdown`（便于单测）；数据复用既有绩效聚合 + 智能体目录，无新增存储。
English: new endpoint `GET /ag-ui/admin/org-health?days=N` (Operator+, read-only) → `{ filename, markdown }`:
- **Overview**: employee count (with calls in window) / calls / tokens / cost (annotated when prices are unset) / overall positive rate / connection counts (assignment · escalation · pipeline).
- **Attention**: employees flagged `low-praise` / `idle` with reasons.
- **Detail table**: employee × (calls / tokens / cost / 👍 / 👎 / positive rate / last active / attention), sorted by calls descending.
- **Org structure**: each employee's “assignment → …; escalation → …; pipeline → …”.
- **Advice**: deterministic suggestions derived from the flags and cost (review low-scoring roles / archive idle ones / evaluate complexity model routing).
- **Front end**: the "📈 Agent performance" tab gains a **"📄 Org health report"** button that generates and downloads `org-health-YYYYMMDD.md` for the current window.
- Implementation: a pure `OrgHealthReport.BuildMarkdown` (unit-tested); data reuses the existing performance aggregation + agent catalog — no new storage.

## 测试
# Tests

中文：新增 `OrgHealthReportTests`（总览 / 关注项 / 层级关系 / 成本 / 无单价降级 / 健康建议），连同 AdminApi / DesktopComposition / AgentPerformance 合跑 **25/25**。i18n 审计 **zh=en=1809，missing 0**；`node --check` 通过。运行栈实测：无令牌 401；管理员 200，生成 1932 字符的 Markdown（17 位员工 / 960 次调用 / 14.88M tokens / 12 条指派、 12 条提升）。
English: new `OrgHealthReportTests` (overview / attention items / hierarchy / cost / no-pricing fallback / healthy advice), **25/25** with AdminApi / DesktopComposition / AgentPerformance. The i18n audit is **zh=en=1809, missing 0**; `node --check` passes. Verified live: 401 without a token; 200 for an admin, producing a 1,932-char Markdown (17 employees / 960 calls / 14.88M tokens / 12 assignments, 12 escalations).

# AG-UI 群聊桌面版 1.0.223 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.223 Release Notes (current Windows desktop release)

**版本说明**：**绩效反哺 + 按复杂度选模型（P0 收官之后的两项）**——员工绩效看板新增**「关注」列与建议栏**（低好评率 / 闲置，确定性判据），把绩效变成可行动的运营信号；另新增**按任务复杂度自动选模型**（`Agents:ModelRouting`，**默认关**）：简单任务用经济模型、繁重任务用强模型，复用「复杂度自适应超时」同一估计器。
**Version note**: **Performance feedback + complexity-based model routing (the two items after the P0 finale)** — the performance dashboard gains an **"Attention" column and a hints strip** (low praise rate / idle, deterministic), turning performance into actionable signals; plus **complexity-based model routing** (`Agents:ModelRouting`, **off by default**): cheap model for simple tasks, strong model for heavy ones, reusing the same estimator as adaptive timeouts.

## 绩效反哺（读侧建议）
# Performance feedback (read-side hints)

中文：`GET /ag-ui/admin/agent-performance` 的 `agents[]` 新增 `flags`（确定性、可解释）：
- `low-praise`：反馈数 ≥ 3 且好评率 < 60% → 建议复核提示词 / 技能。
- `idle`：窗口内无调用（窗口内目录中的全部数字员工都会被列出，便于发现闲置岗位）。
- 前端：「📈 员工绩效」页新增**「关注」列**与**建议栏**（列出需关注员工），按 i18n 渲染。
English: `GET /ag-ui/admin/agent-performance`'s `agents[]` gains `flags` (deterministic, explainable):
- `low-praise`: ≥ 3 ratings and a positive rate < 60% → review the prompt / skills.
- `idle`: no calls in the window (every employee in the catalog is listed, so idle roles surface).
- Front end: the "📈 Agent performance" tab gains an **"Attention" column** and a **hints strip** (listing employees needing attention), rendered per i18n.

## 按任务复杂度自动选模型
# Complexity-based model routing

中文：新增 `Agents:ModelRouting`（`Enabled` / `SimpleModel` / `HeavyModel`；Docker：`AGENTS_MODELROUTING_*`，**默认关**）：
- 复用「复杂度自适应超时」同一份估计器（`RunComplexityEstimator`）：**Simple** 档路由到 `SimpleModel`、**Complex/Heavy** 档路由到 `HeavyModel`、**Standard** 档不换；某档留空 = 不换。
- 实现：网关在运行开始时先安装复杂度预算，据此经 `AgentCatalog.GetOrCreateWithModel`（与视觉变体同机制：按 `agentId + 模型` 分键缓存）取得对应模型变体（**只换模型、工具/记忆/审批包装照旧**）；关闭时返回 null → 走正常模型，行为与旧版完全一致。
- 纯函数 `ModelRoutingOptions.ResolveForTier` 便于单测。
English: new `Agents:ModelRouting` (`Enabled` / `SimpleModel` / `HeavyModel`; Docker: `AGENTS_MODELROUTING_*`, **off by default**):
- Reuses the same estimator as adaptive timeouts (`RunComplexityEstimator`): **Simple** → `SimpleModel`, **Complex/Heavy** → `HeavyModel`, **Standard** unchanged; a blank tier model means no switch.
- Implementation: the gateway installs the complexity budget at run start and, based on it, gets the matching model variant via `AgentCatalog.GetOrCreateWithModel` (same mechanism as the vision variant: cached per `agentId + model`) — **only the model changes; tools / memory / approval wrapping stay**; when off it returns null and the normal model is used, identical to before.
- Pure `ModelRoutingOptions.ResolveForTier` is unit-tested.

## 测试
# Tests

中文：`AgentPerformanceTests` 新增标记用例（`low-praise` / `idle` / 无标记）与 `ModelRoutingOptions.ResolveForTier` 用例；合跑 **44/44**（含 AdminApi / DesktopComposition / EvalApi / SpeechSynthesis）。i18n 审计 **zh=en=1807，missing 0**；`node --check`、appsettings JSON 校验通过。运行栈实测：绩效端点返回 `flags`（30 天窗口 17 位员工中 2 位 `idle`）。
English: `AgentPerformanceTests` gains flag cases (`low-praise` / `idle` / none) and a `ModelRoutingOptions.ResolveForTier` case; **44/44** (incl. AdminApi / DesktopComposition / EvalApi / SpeechSynthesis). The i18n audit is **zh=en=1807, missing 0**; `node --check` and appsettings JSON validation pass. Verified live: the performance endpoint returns `flags` (2 of 17 employees `idle` over 30 days).

# AG-UI 群聊桌面版 1.0.222 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.222 Release Notes (current Windows desktop release)

**版本说明**：**数字员工评测集 / 回归（P0 第三项，收官）**——管理员控制台新增「🧪 评测回归」页签：为数字员工建**金标准用例**（提问 → 期望要点），改提示词 / 换模型后**一键跑分**，按确定性判据给出每条**通过 / 未通过**与回复摘录，防能力退化。
**Version note**: **Digital-employee eval cases / regression (P0, item 3 — the finale)** — the admin console gains a "🧪 Eval & regression" tab: define **golden cases** for an employee (prompt → expected points), and after changing prompts or models **run all** to score each case **pass/fail** by a deterministic rule, with a reply excerpt — guarding against regressions.

## 数字员工评测集 / 回归
# Digital-employee eval cases / regression

中文：新增端点 `/ag-ui/admin/eval-cases`（仅管理员）：
- `GET /` —— 列出用例 + 被测员工候选（下拉用）。
- `POST /` —— 新建 / 更新用例（`name` / `agentId` / `prompt` / `expected[]` / `matchMode`=any|all / `enabled`）；校验员工存在、提问非空且 ≤2000 字。
- `DELETE /{id}` —— 删除用例。
- `POST /run` —— 一键跑分：对每条启用用例（或指定 `caseIds`）用目标员工的**提示词 + 正式回复模型**跑一次，按「期望要点是否出现」判定；返回逐条 `passed` / `matched` / `missed` / `reply` 摘录 / `durationMs` 与总计。
- **判定**：确定性关键词判据（大小写不敏感；`any` 命中任一即通过 / `all` 全部命中）；**不用 LLM 当裁判**（便宜、可复现）。
- **实现**：`AgentCatalog.CreateForEval`（只挂 Instructions、走正式回复模型，**不带工具 / 记忆 / 技能**，排除干扰）；`AgentEvalRunner`（`Score` 为纯函数，便于单测）；`EvalCaseStore` 内存持有 + 扩展区「evalCases」持久化（重启不丢，与消息反馈同机制）。
- **前端**：控制台新增「🧪 评测回归」页签（Admin+）：用例表（名称 / 员工 / 判定 / 启用 / 最近结果 / 操作）+ 逐条「▶ 跑此用例」/「编辑」/「删除」+ 顶部「▶ 跑全部」与汇总（通过数 / 用时）；用「＋ 新建用例」弹窗录入（**每次打开先清空再回填**，避免残留）。
English: new endpoints under `/ag-ui/admin/eval-cases` (admin only):
- `GET /` — list cases + target-employee options (for the dropdown).
- `POST /` — create / update a case (`name` / `agentId` / `prompt` / `expected[]` / `matchMode`=any|all / `enabled`); validates the employee exists and the prompt is non-empty and ≤2000 chars.
- `DELETE /{id}` — delete a case.
- `POST /run` — run all: for each enabled case (or given `caseIds`) run the target employee's **instructions + normal reply model** once and judge by whether the **expected points appear**; returns per-case `passed` / `matched` / `missed` / `reply` excerpt / `durationMs` plus totals.
- **Judging**: a deterministic keyword rule (case-insensitive; `any` = at least one hit / `all` = every hit); **no LLM as judge** (cheap, reproducible).
- **Implementation**: `AgentCatalog.CreateForEval` (instructions only, normal reply model, **no tools / memory / skills** — removing noise); `AgentEvalRunner` (with a pure `Score`, unit-tested); `EvalCaseStore` held in memory + persisted in the "evalCases" extension area (survives restart, same mechanism as message feedback).
- **Front end**: a "🧪 Eval & regression" tab (Admin+): a case table (name / agent / match / enabled / last result / actions) with per-row "▶ Run this case" / "Edit" / "Delete", plus a top "▶ Run all" with a summary (passed / duration); the "+ New case" dialog **clears then refills** so no stale data leaks between edits.

## 测试
# Tests

中文：新增 `EvalScoreTests`（any / all / 大小写不敏感 / 空期望）与 `EvalApiTests`（建员工→建用例→列出→跑分 `total=2 passed=1`→非管理员 403），合跑 **13/13**（连同 AdminApi / AgentPerformance）。i18n 审计 **zh=en=1802，missing 0**；`node --check` 通过。运行栈实测：无令牌 401、管理员 200（17 个候选员工）；对真实模型跑一条用例得 reply 与判定（2.9s）；Playwright 实测页签与新建弹窗渲染正常、无前端报错。
English: new `EvalScoreTests` (any / all / case-insensitive / empty expected) and `EvalApiTests` (create agent → create case → list → run `total=2 passed=1` → non-admin 403), **13/13** (with AdminApi / AgentPerformance). The i18n audit is **zh=en=1802, missing 0**; `node --check` passes. Verified live: 401 without a token, 200 for an admin (17 candidate employees); a real-model run of one case returned a reply and a verdict (2.9s); Playwright confirms the tab and the new-case dialog render with no front-end errors.

# AG-UI 群聊桌面版 1.0.221 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.221 Release Notes (current Windows desktop release)

**版本说明**：**成本估算与预算提示（P0 第二项）**——员工绩效看板新增**成本列**与合计：按可配的 token 单价（`Agents:Pricing`）把用量换算成金额，并可选**每日成本预算**，在看板顶部显示「已用 X%」、超预算时标 ⚠。单价未配置时成本显示「—」。
**Version note**: **Cost estimation and budget hint (P0, item 2)** — the agent performance dashboard gains a **cost column** and total: token usage is converted to money via configurable per-token prices (`Agents:Pricing`), with an optional **daily cost budget** showing "{pct}% used" at the top (⚠ when over). Cost shows "—" when prices are unconfigured.

## 成本估算与预算
# Cost estimation & budget

中文：扩展 `GET /ag-ui/admin/agent-performance`（同一端点，向后兼容）：
- **可配单价**（`Agents:Pricing`，也可用 `AGENTS_PRICING_*`）：`InputPerMillion`（输入每百万 token 单价）、`OutputPerMillion`（输出 = completion + reasoning 每百万 token 单价）、`Currency`（币种符号，仅展示）、`DailyBudget`（每日成本预算，0 = 不限）。单价皆为 0 视为未配置。
- **返回新增**：`pricing`（configured / currency / inputPerMillion / outputPerMillion / dailyBudget）；`summary` 新增 `cost`（窗口内总成本）、`dailyAvgCost`、`budget`、`budgetPct`（日均成本 / 日预算 ×100）、`overBudget`；每个 `agents[]` 新增 `cost`。
- **前端**：「📈 员工绩效」页签新增**成本列**；顶部 meta 行附加「成本 {金额}」与（配了预算时）「预算 {币}{额}/天 · 已用 {pct}%」（超预算追加 ⚠）。
- 实现：`PricingOptions`（`AgentOptions.cs`，含 `EstimateCost`）+ 端点计算；纯函数不涉新存储。成本按「输入单价 × prompt + 输出单价 × (completion + reasoning)」估算。
- **未做（下一步）**：按复杂度**自动选模型**（经济/强模型路由）——需先定口径与防抖动，本轮只做「可配 + 可观测」。
English: extends `GET /ag-ui/admin/agent-performance` (same endpoint, backward-compatible):
- **Configurable prices** (`Agents:Pricing`, also `AGENTS_PRICING_*`): `InputPerMillion`, `OutputPerMillion` (output = completion + reasoning, per million tokens), `Currency` (display only), `DailyBudget` (0 = unlimited). Both prices 0 means unconfigured.
- **New response fields**: `pricing` (configured / currency / inputPerMillion / outputPerMillion / dailyBudget); `summary` gains `cost`, `dailyAvgCost`, `budget`, `budgetPct` (daily-average cost / daily budget ×100) and `overBudget`; each `agents[]` gains `cost`.
- **Front end**: the "📈 Agent performance" tab gains a **cost column**; the meta line appends "Cost {amount}" and (when a budget is set) "Budget {cur}{amt}/day · {pct}% used" (⚠ when over).
- Implementation: `PricingOptions` (`AgentOptions.cs`, with `EstimateCost`) + endpoint math; no new storage. Cost = input price × prompt + output price × (completion + reasoning).
- **Not done (next step)**: complexity-based **automatic model routing** (economy/strong) — needs a stable rule first; this round is "configurable + observable" only.

## 测试
# Tests

中文：`AgentPerformanceTests` 新增 `PricingOptions.EstimateCost` 用例（输入/输出加权 + 未配置判定 + 零用量），端点集成测试新增 `pricing` / `summary.cost` 形状断言，合跑 **21/21**（含 AdminApi / DesktopComposition）。`node --check`、i18n 审计（zh=en=1773，missing 0）与 appsettings JSON 校验通过。运行栈实测：未配单价 `pricing.configured=false`、成本显示「—」；临时设 `AGENTS_PRICING_INPUT_PER_MILLION=2 / OUTPUT=8 / DAILY_BUDGET=5` 后返回总成本 **61.59**、日均 **2.053**、预算已用 **41.1%**（未超）、首位员工成本 **14.66**。
English: `AgentPerformanceTests` gains a `PricingOptions.EstimateCost` case (input/output weighting + unconfigured check + zero usage); the endpoint integration test asserts the `pricing` / `summary.cost` shape; **21/21** (incl. AdminApi / DesktopComposition). `node --check`, the i18n audit (zh=en=1773, missing 0) and appsettings JSON validation pass. Verified live: unconfigured prices → `pricing.configured=false` and cost shows "—"; with `AGENTS_PRICING_INPUT_PER_MILLION=2 / OUTPUT=8 / DAILY_BUDGET=5` it returns total cost **61.59**, daily average **2.053**, budget **41.1% used** (not over), top agent **14.66**.

# AG-UI 群聊桌面版 1.0.220 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.220 Release Notes (current Windows desktop release)

**版本说明**：**数字员工绩效看板（P0）**——管理员控制台新增「📈 员工绩效」页签：按数字员工聚合**调用次数 / 总 token / 👍👎 好评率 / 最近活跃**，支持 7 / 30 / 90 天窗口，一眼看出哪位置得用、哪位置需改进（为“一键编排选人”与组织体检提供实测依据）。
**Version note**: **Digital-employee performance dashboard (P0)** — the admin console gains an "📈 Agent performance" tab aggregating **calls / total tokens / 👍👎 positive rate / last active** per employee, with 7 / 30 / 90-day windows, so you can see at a glance who delivers and who needs work (a data foundation for "pick people when orchestrating" and org health checks).

## 员工绩效看板
# Agent performance dashboard

中文：新增端点 `GET /ag-ui/admin/agent-performance?days=N`（仅运维及以上，只读）：
- **数据源**：`agui_usage` 按「日期 + 员工 + 触发者」的聚合行（调用次数 / 输入·输出·思考 token）与消息反馈（👍/👎）。
- **窗口**：`days` 限 1–365（默认 30），只用于过滤反馈（用量行按日期区间取）。
- **返回**：`agents[]`（agentId / name / calls / prompt·completion·reasoning·totalTokens / up / down / positiveRate / lastDate）与 `summary`（员工数 / 总调用 / 总 token / 综合好评率）。名字取数字员工昵称，取不到时回退 agentId。
- **前端**：管理员控制台新增「📈 员工绩效」页签（Operator+ 可见），范围选择 7 / 30 / 90 天 + 一键刷新，按调用次数降序列表；无反馈时好评率显示「—」。
- 实现：聚合逻辑抽为纯函数 `AgentPerformance.Aggregate`（便于单测）；后端复用既有 `IUsageStore` / `MessageFeedbackStore` / `AgentCatalog`，无新增存储。
English: new endpoint `GET /ag-ui/admin/agent-performance?days=N` (Operator+, read-only):
- **Sources**: `agui_usage`'s per-date+agent+trigger aggregate rows (calls / prompt·completion·reasoning tokens) and message feedback (👍/👎).
- **Window**: `days` clamped to 1–365 (default 30), used only to filter feedback (usage is fetched by date range).
- **Returns**: `agents[]` (agentId / name / calls / prompt·completion·reasoning·totalTokens / up / down / positiveRate / lastDate) plus `summary` (agents / total calls / total tokens / overall positive rate). Names use the employee's nickname, falling back to the agentId.
- **Front end**: the admin console gains an "📈 Agent performance" tab (Operator+), with 7/30/90-day range and refresh, listed by calls descending; positive rate shows "—" when there is no feedback.
- Implementation: the aggregation is a pure function `AgentPerformance.Aggregate` (unit-tested); the backend reuses the existing `IUsageStore` / `MessageFeedbackStore` / `AgentCatalog` — no new storage.

## 测试
# Tests

中文：新增 `AgentPerformanceTests`（按员工汇总 / 窗口外反馈不计 / 名回退 id / 排序 / 空输入）与端点鉴权/形状集成测试，合跑 **4/4**；连同管理接口与桌面组合根回归全通过。`node --check` 与 i18n 审计（zh=en=1770，missing 0）通过。运行栈实测：无令牌 401；带令牌 200（近 30 天 22 位数字员工 / 1079 次调用 / 16.35M tokens）；Playwright 实测页签渲染正确。
English: new `AgentPerformanceTests` (per-agent aggregation / out-of-window feedback excluded / name falls back to id / ordering / empty input) plus an endpoint auth/shape integration test, running **4/4**; the admin-API and desktop-composition suites all pass. `node --check` and the i18n audit pass (zh=en=1770, missing 0). Verified live: 401 without a token; 200 with one (22 agents / 1079 calls / 16.35M tokens over 30 days); Playwright confirms the tab renders correctly.

# AG-UI 群聊桌面版 1.0.219 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.219 Release Notes (current Windows desktop release)

**版本说明**：**语音服务面板打磨**——账号菜单图标风格统一（不再只有语音一项带图标）、弹窗改为固定尺寸并带内部滚动条、音色试听改用 fetch+blob 播放（试听期间按钮显示「…」防重复点击，错误提示不再误导）。
**Version note**: **Voice-service panel polish** — the account menu is now visually uniform (voice was the only item with an icon), the modal has a fixed size with an internal scrollbar, and voice preview now plays via fetch+blob (the button shows “…” while synthesizing to block double-clicks, and error messages are no longer misleading).

## 三项修正
# Three fixes

中文：
1. **账号菜单图标统一**：去掉「语音服务」菜单项前的 🎙️，与其它菜单项（修改资料 / 模型配置 / 管理控制台 …）保持纯文字风格一致（弹窗标题仍保留图标，与「📊 系统状态」等一致）。
2. **弹窗固定尺寸 + 滚动条**：`#voiceServiceModal` 新增 `voice-service-modal` 类（640px 宽、`min(80vh,760px)` 高、内部 `overflow-y:auto`），音色再多也在弹窗内滚动，不再撑高页面 / 忽大忽小。
3. **音色试听不再报错**：原先把带 `token` 的 URL 直接丢给隐藏 `<audio>` 播放，合成一句样例需数秒，期间无反馈、易重复点击（二次改 `src` 触发 `AbortError`），且失败一律提示「获取音色列表失败」（误导）。现改为：合成期间按钮变 **「…」并禁用重复点击**，先 `fetch` 取音频字节再以 **blob URL** 播放（令牌不再出现在 URL 里，且能拿到真实 HTTP 状态给出准确提示）；关闭弹窗 / 重检时释放 blob URL。CSP 相应放行 `media-src 'self' blob:`。
English:
1. **Unified menu icons**: dropped the 🎙️ before the “Voice service” menu item so it matches the plain-text style of every other item (the modal title keeps its icon, like “📊 System Status”).
2. **Fixed-size modal with a scrollbar**: `#voiceServiceModal` gains a `voice-service-modal` class (640px wide, `min(80vh,760px)` tall, internal `overflow-y:auto`), so any number of voices scrolls inside the modal instead of stretching the page or resizing.
3. **Preview no longer errors**: previously the token-bearing URL was handed straight to a hidden `<audio>`; synthesis takes seconds with no feedback and invites double-clicks (changing `src` again aborts with `AbortError`), and any failure showed the misleading “failed to get the voice list”. Now the button shows **“…” and blocks repeat clicks** while synthesizing, the audio is `fetch`ed and played via a **blob URL** (the token never appears in the URL, and the real HTTP status yields an accurate message), and the blob URL is released on close / re-check. CSP accordingly allows `media-src 'self' blob:`.

## 测试
# Tests

中文：`node --check`（`app.js` / `zh.js` / `en.js`）通过；i18n 审计 **zh=en=1755，missing 0**。`SpeechSynthesisTests` / `AdminApiTests` / `DesktopCompositionTests` / `BrandingApiTests` 合跑 **39/39**。Playwright 实测（headless）：账号菜单 10 项均无图标；弹窗 640×576（`80vh`）且 `#voiceServiceBody` 可滚动；点 ▶ 后按钮变「…」，合成完成 `audio.src` 为 `blob:`、`readyState=4`、`paused=false`、`play()` 兑现——不再有错误 toast。
English: `node --check` (`app.js` / `zh.js` / `en.js`) passes; the i18n audit is **zh=en=1755, missing 0**. `SpeechSynthesisTests` / `AdminApiTests` / `DesktopCompositionTests` / `BrandingApiTests` run **39/39**. Verified with Playwright (headless): all 10 account-menu items are icon-free; the modal is 640×576 (`80vh`) with a scrollable `#voiceServiceBody`; after clicking ▶ the button shows “…”, then on completion `audio.src` is `blob:`, `readyState=4`, `paused=false`, and `play()` resolves — no error toast.

# AG-UI 群聊桌面版 1.0.218 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.218 Release Notes (current Windows desktop release)

**版本说明**：**语音服务独立运维入口**——账号菜单新增 **「🎙️ 语音服务」**：把语音问答（ASR）/ 语音回复（TTS）的可达性、端点、**音色试听**与**一键重检**集中到一个独立面板；「系统状态」只保留一行指向新入口的提示，职责更清晰。
**Version note**: **A dedicated Voice Service entry** — the account menu gains **“🎙️ Voice service”**, consolidating voice Q&A (ASR) / voice reply (TTS) reachability, endpoints, **voice preview** and a **one-click re-check** into its own panel; System Status keeps just a one-line pointer to it, so responsibilities are cleaner.

## 语音服务独立入口
# Dedicated Voice Service panel

中文：账号菜单（运维及以上可见）新增 **「🎙️ 语音服务」**，打开即**并行拉取** `/ag-ui/admin/status` 与 `/ag-ui/admin/voice/voices`：
- **ASR / TTS 状态**：就绪 / 未启用 / 不可达三态，附端点提示（TTS 另标「（仅语音回合）」）。
- **音色试听**：TTS 启用时列出合成服务的全部音色，逐个 **「▶」** 试听（复用单个 `<audio>`，避免叠加播放）。
- **一键重检**：面板底部「🔄 重新检测」重新拉取，无需关窗重开。

「系统状态」移除了语音行与「试听音色」按钮，改为一行提示「语音服务已移至账号菜单 → 🎙️ 语音服务」。后端**无改动**（复用既有 `/ag-ui/admin/status`、`/ag-ui/admin/voice/voices`、`/ag-ui/admin/voice/sample` 端点）。
English: the account menu (visible to Operator+) gains **“🎙️ Voice service”**, which on open **fetches in parallel** `/ag-ui/admin/status` and `/ag-ui/admin/voice/voices`:
- **ASR / TTS status**: ready / off / unreachable, with endpoint hints (TTS also marked “(voice turns only)”).
- **Voice preview**: when TTS is on, lists every voice the synthesis service offers, each previewable with **“▶”** (a single reused `<audio>` avoids overlapping playback).
- **One-click re-check**: the **“🔄 Re-check”** button at the bottom re-fetches, no need to close and reopen.

System Status drops the voice rows and the “Preview voices” button, replaced by a one-line pointer: “Voice service has moved to the account menu → 🎙️ Voice service”. The backend is **unchanged** (it reuses the existing `/ag-ui/admin/status`, `/ag-ui/admin/voice/voices`, `/ag-ui/admin/voice/sample` endpoints).

## 测试
# Tests

中文：`node --check`（`app.js` / `zh.js` / `en.js`）通过；i18n 审计 **zh=en=1754，missing 0**，`index.html` / `app.js` 引用键全部存在。桌面组合根 / 存储 / 管理接口 / 语音合成合跑 **44/44**。运行栈实测：`/index.html` 含 `voiceServiceModal`；`/app.js` 含 `openVoiceServiceModal`、**不含** `voicePickerModal`；`/i18n/zh.js` 含 `menu.voice`。
English: `node --check` (`app.js` / `zh.js` / `en.js`) passes; the i18n audit is **zh=en=1754, missing 0**, with all referenced keys present in `index.html` / `app.js`. The desktop-composition / store / admin-API / speech-synthesis suites run **44/44**. Verified live: `/index.html` contains `voiceServiceModal`; `/app.js` contains `openVoiceServiceModal` and **no** `voicePickerModal`; `/i18n/zh.js` contains `menu.voice`.

# AG-UI 群聊桌面版 1.0.217 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.217 Release Notes (current Windows desktop release)

**版本说明**：**转写可修正并一键重问 + TTS 语速**——ASR 听错时，可在语音消息下就地修正转写并一键「按修正文本重问」（引用原语音、@ 上一轮回复的数字员工）；语音合成新增语速参数。
**Version note**: **Editable transcript with one-click re-ask + TTS speed** — when ASR mishears, you can now correct the transcript in place under the voice message and re-ask with the correction (quoting the original voice message and @-mentioning the employee who answered); voice synthesis gains a speed parameter.

## 转写可修正 + 一键重问
# Editable transcript + one-click re-ask

中文：转写文本旁新增 **「✏️ 修正」**：就地编辑为正确文本，再点 **「↻ 按修正文本重问」** → 以修正后的文本发一条**新消息**（引用原语音消息，并 **@ 上一轮回复的那个数字员工**，确保重问能触发同一位）；「取消」放弃。这是 ASR 听错时的**一次性纠错入口**，**不修改**原始转写与历史。重问走正常发送通道（与手动发消息一致）。
English: next to the transcript is a **"✏️ Fix"** button: edit it in place, then **"↻ Re-ask with correction"** sends the corrected text as a **new message** (quoting the original voice message and **@-mentioning the employee who answered**, so the same one is triggered); "Cancel" discards. It is a **one-shot correction entry** for ASR mishears and **does not modify** the original transcript or history. The re-ask goes through the normal send path.

## 语音合成语速
# TTS speed

中文：新增 `Agents:Audio:SpeechSpeed`（默认 `1.0`），随合成请求发送 `speed` 参数（Kokoro / OpenAI 兼容，夹紧到 0.25–4.0）。Docker 对应 `AGENTS_AUDIO_SPEECH_SPEED`。
English: new `Agents:Audio:SpeechSpeed` (default `1.0`), sent as the `speed` field in the synthesis request (Kokoro / OpenAI-compatible, clamped to 0.25–4.0). Docker: `AGENTS_AUDIO_SPEECH_SPEED`.

## 测试
# Tests

中文：`SpeechSynthesisTests` 新增 1 例（语速发送与上限夹紧）；连同存储 / 管理接口 / 桌面组合根合跑 **44/44**；`node --check` 与 i18n 审计（zh=en=1751，missing 0）通过。运行栈实测：试听端点带 `speed` 仍 200 `audio/mpeg`；前端已上线「修正 / 重问」入口。
English: `SpeechSynthesisTests` gains 1 case (speed sent and upper-clamped); **44/44** together with the store / admin-API / desktop-composition suites; `node --check` and the i18n audit pass (zh=en=1751, missing 0). Verified live: the preview endpoint still returns 200 `audio/mpeg` with `speed`; the front end serves the Fix / Re-ask entries.

# AG-UI 群聊桌面版 1.0.216 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.216 Release Notes (current Windows desktop release)

**版本说明**：**管理端「语音音色试听」**——在「系统状态」的语音行旁新增「🎧 试听音色」：列出声合成服务提供的全部音色（代理其 `/v1/audio/voices`），逐个「▶」试听一段样句，不用改配置重启就能选定中文音色。
**Version note**: **Voice preview in the admin console** — a "🎧 Preview voices" entry next to the voice rows in System Status: lists every voice the synthesis service offers (proxying its `/v1/audio/voices`) and previews each with a ▶ sample, so a Chinese voice can be chosen without editing config and restarting.

## 音色列表与试听
# Voice list & preview

中文：
- **新端点（仅运维及以上）**：`GET /ag-ui/admin/voice/voices` —— 代理合成服务的音色列表，返回 `{ voices: string[] }`（未配置 / 不支持 404，服务不可达 503）；`GET /ag-ui/admin/voice/sample?voice=<id>` —— 用配置的 TTS 端点合成一句短样例并**内联返回音频**（便于 `<audio src>`，可带 `?token=`；失败 503）。
- **端点推导**：音色列表端点由合成端点推导（`…/v1/audio/speech` → `…/v1/audio/voices`）；自定义非标准形状的服务无法推导时返回 404（不报错，前端提示）。
- **服务层**：`SpeechSynthesisService` 抽出 `TrySynthesizeBytesAsync(text, voiceOverride)`（不落盘，供试听）与 `VoicesEndpoint` / `SampleContentType`；回复合成路径复用同一实现（行为不变）。
- **另**：确认「输出物」弹窗**已自动包含** TTS 回复的音频附件（动画/语音统一在件入口），无需改动。
English:
- **New endpoints (Operator+)**: `GET /ag-ui/admin/voice/voices` — proxies the synthesis service's voice list, returning `{ voices: string[] }` (404 when unconfigured / unsupported, 503 when unreachable); `GET /ag-ui/admin/voice/sample?voice=<id>` — synthesizes a short sample via the configured TTS endpoint and returns the **audio inline** (for `<audio src>`, `?token=` accepted; 503 on failure).
- **Endpoint derivation**: the voices URL is derived from the synthesis URL (`…/v1/audio/speech` → `…/v1/audio/voices`); a custom, non-standard service yields 404 (a hint is shown rather than an error).
- **Service layer**: `SpeechSynthesisService` gains `TrySynthesizeBytesAsync(text, voiceOverride)` (no disk write, for previews) plus `VoicesEndpoint` / `SampleContentType`; the reply path reuses the same implementation (behaviour unchanged).
- **Also**: confirmed the Outputs dialog **already includes** TTS-reply audio attachments (one consolidated attachment entry), so no change was needed.

## 测试
# Tests

中文：`SpeechSynthesisTests` 新增 2 例（字节合成 + 音色覆盖生效、`VoicesEndpoint` 推导与自定义形状为 null），连同存储 / 管理接口 / 桌面组合根合跑 **43/43**；`node --check` 与 i18n 审计（zh=en=1745，missing 0）通过。运行栈实测：`GET /ag-ui/admin/voice/voices` → 200（72 个音色，含 8 个中文）；`GET …/voice/sample?voice=zf_xiaoyi` → 200 `audio/mpeg` 69KB；无令牌 401。
English: `SpeechSynthesisTests` gains 2 cases (byte synthesis with voice override taking effect; `VoicesEndpoint` derivation and null for custom shapes), **43/43** together with the store / admin-API / desktop-composition suites; `node --check` and the i18n audit pass (zh=en=1745, missing 0). Verified live: `GET /ag-ui/admin/voice/voices` → 200 (72 voices incl. 8 Chinese); `GET …/voice/sample?voice=zf_xiaoyi` → 200 `audio/mpeg` 69KB; 401 without a token.

# AG-UI 群聊桌面版 1.0.215 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.215 Release Notes (current Windows desktop release)

**版本说明**：**语音消息转写「播放即转写」+ 控制台语音服务状态**——语音消息的 ASR 转写**默认内联展示**（无需再点）；管理员「系统状态」新增语音能力行，一眼看出 ASR / TTS 是否配好、是否可达。
**Version note**: **Voice transcripts show inline + a voice-service row in the console** — a voice message's ASR transcript is now **shown inline by default** (no click), and the admin System Status gains voice capability rows so ASR / TTS configured-and-reachable status is visible at a glance.

## 语音消息转写默认内联（1.0.214 的「点击查看」→ 自动展示）
# Voice transcript inline by default

中文：语音消息下自动加载并展开转写（「🎤 转写中…」→ 文本），仍可点击收起 / 展开。客户端按 `attachmentId` 缓存（本会话）：虚拟滚动重渲染不再重复请求；因 ASR 可能仍在进行，空结果最多重试 **2 次（5s / 10s）**，仍无才置为「确定无转写」并移除入口（避免未启用 ASR 时的请求风暴）；登出 / 重置时清空缓存。
English: a voice message auto-loads and expands its transcript ("🎤 Transcribing…" → text), still collapsible/expandable on click. Cached per `attachmentId` per session so virtual-scroll re-renders do not re-request; since ASR may still be running, an empty result retries up to **2 times (5s / 10s)** before being marked definitively absent and the entry removed (preventing a request storm when ASR is off); the cache is cleared on logout / reset.

## 管理控制台：语音服务状态
# Admin console: voice-service status

中文：`GET /ag-ui/admin/status` 新增 `voice` 字段：`voice.asr` / `voice.tts` = `{ enabled, endpoint, reachable[, onlyForVoice] }`。可达性由**短超时（连接 1.2s / 总 1.8s）的 GET 探测**得出——**收到任意 HTTP 响应即算可达**（`/v1/audio/*` 对 GET 常回 405，也已证明服务在监听）；未配置 / 未启用为 `null` / `enabled=false`。控制台「系统状态」新增两行：**🎤 语音转写（ASR）**、**🔊 语音回复（TTS）**，值为「就绪 / 未启用 / 不可达」，并在下方列出实际端点（TTS 附带「（仅语音回合）」）。端点来自服务端配置，无 SSRF 面。
English: `GET /ag-ui/admin/status` gains a `voice` field: `voice.asr` / `voice.tts` = `{ enabled, endpoint, reachable[, onlyForVoice] }`. Reachability comes from a **short-timeout GET probe (1.2s connect / 1.8s total)** where **any HTTP response counts as reachable** (`/v1/audio/*` typically answers 405 to GET, which already proves the service is listening); unconfigured / disabled is `null` / `enabled=false`. The System Status modal adds two rows: **🎤 Voice transcription (ASR)** and **🔊 Voice reply (TTS)** with values "Ready / Off / Unreachable", plus a hint listing the actual endpoints (TTS notes "voice turns only"). Endpoints come from server config, so there is no SSRF surface.

## 测试
# Tests

中文：`AudioTranscriptStoreTests` / 网关转写用例 / `AdminApiTests`（含 `/admin/status`）/ 桌面组合根合跑 **65/65**；`node --check` 与 i18n 审计（zh=en=1739，missing 0）通过。运行栈实测：`GET /ag-ui/admin/status` 返回 `voice.asr{enabled:true,reachable:true}` / `voice.tts{enabled:true,reachable:true,onlyForVoice:true}`。
English: `AudioTranscriptStoreTests` / the gateway transcription case / `AdminApiTests` (incl. `/admin/status`) / desktop composition run **65/65**; `node --check` and the i18n audit pass (zh=en=1739, missing 0). Verified on the live stack: `GET /ag-ui/admin/status` returns `voice.asr{enabled:true,reachable:true}` / `voice.tts{enabled:true,reachable:true,onlyForVoice:true}`.

# AG-UI 群聊桌面版 1.0.214 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.214 Release Notes (current Windows desktop release)

**版本说明**：**语音消息可见「转写文本」**——以前用户发了一段语音，只看到录音与回复，不知道数字员工到底「听到」了什么（答非所问时无从核对）。现在语音消息下多一个「🎤 查看转写」，点击展示该语音的 ASR 转写文本。
**Version note**: **Voice messages now show their transcript** — previously a user sending a voice message saw only the recording and the reply, with no way to check what the digital employee actually "heard" (impossible to diagnose an off-target answer). A "🎤 Show transcript" entry now appears under a voice message and reveals its ASR transcript on click.

## 语音消息转写展示
# Voice-message transcript display

中文：
- **端点** `GET /ag-ui/attachments/{attachmentId}/transcript` → `{ transcript }`（无转写 / 附件不存在 404）。鉴权与附件**下载 / 在线查看完全同一段校验**（`ResolveAndAuthorize` → `CanAccessAttachment`），未登录 401 / 非成员 403，不存在越权读取口子。
- **存储**：新增 `AudioTranscriptStore`——网关转写成功后按 `attachmentId` 记录；经持久化扩展区 **`audioTranscripts`** 跨重启保持（memory 模式写 JSON 快照，数据库模式落 `agui_sections`；共享存储下多副本亦可查）。系统初始化（清空全部数据）时一并清除。
- **前端**：语音消息渲染时多一个「🎤 查看转写」按钮（**懒加载**，点击才请求）；无 / 取不到时提示「暂无转写文本」并移除入口，不干扰其它附件；再点可收起。
English:
- **Endpoint** `GET /ag-ui/attachments/{attachmentId}/transcript` → `{ transcript }` (404 when there is no transcript / the attachment is missing). Authorization is **exactly the same check as attachment download / online view** (`ResolveAndAuthorize` → `CanAccessAttachment`): 401 unauthenticated, 403 non-member — no privilege-escalation read path.
- **Storage**: new `AudioTranscriptStore` — the gateway records the transcript by `attachmentId` after a successful transcription, persisted across restarts via the extension section **`audioTranscripts`** (JSON snapshot in memory mode; `agui_sections` in database mode; available across replicas on shared storage). Cleared together with “wipe all data”.
- **Front end**: a voice message gains a "🎤 Show transcript" button (**lazy** — only requested on click); when none is available it shows "No transcript available" and removes the entry without affecting other attachments; click again to collapse.

## 测试
# Tests

中文：新增 `AudioTranscriptStoreTests` 6/6（读写 / 截断空白 / 覆盖 / 清空 / 快照↔恢复往返）与网关用例 `Gateway_Transcription_RecordsTranscriptForUi`；连同语音 / 桌面组合根合跑 **59/59**。运行栈实测：发语音 → 端点 200 返回正确中文转写；无令牌 401、未知附件 404；**重启 web 后仍可查**（持久化生效）。
English: new `AudioTranscriptStoreTests` 6/6 (read/write, blank trimming, overwrite, clear, snapshot↔restore round-trip) and gateway case `Gateway_Transcription_RecordsTranscriptForUi`; **59/59** together with the voice / desktop-composition suites. Verified on the live stack: send voice → the endpoint returns 200 with the correct Chinese transcript; 401 without a token, 404 for an unknown attachment; and it is **still retrievable after restarting web** (persistence works).

# AG-UI 群聊桌面版 1.0.213 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.213 Release Notes (current Windows desktop release)

**版本说明**：**语音回复默认「仅语音回合」**——`Agents:Audio:SpeechOnlyForVoice` 默认改为 `true`：开启语音回复后，只有触发消息带语音附件的回合才合成音频，文字回复不再拖音频（Kokoro 在 CPU 上约 0.2s/字，逐条合成本就重）。跨语言、非语音回合仍可按需点前端 🔊 朗读。
**Version note**: **Voice replies default to voice turns only** — `Agents:Audio:SpeechOnlyForVoice` now defaults to `true`: once voice replies are on, only turns whose trigger message carries a voice attachment synthesize audio, so text replies no longer drag an attachment (Kokoro is ~0.2s/char on CPU, so per-reply synthesis is heavy anyway). Non-voice turns remain readable on demand via the front-end 🔊.

（默认音色保持 `zf_xiaoxiao`——中文清晰通用；男声可选 `zm_yunjian` / `zm_yunxi`，其余女声 `zf_xiaoyi` / `zf_xiaobei` / `zf_xiaoni`，试听样音见 `artifacts/voice-samples/`。）
(The default voice stays `zf_xiaoxiao` — clear and general-purpose Chinese; male alternatives are `zm_yunjian` / `zm_yunxi`, other female ones `zf_xiaoyi` / `zf_xiaobei` / `zf_xiaoni`; samples in `artifacts/voice-samples/`.)

# AG-UI 群聊桌面版 1.0.212 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.212 Release Notes (current Windows desktop release)

**版本说明**：**语音回复新增「仅语音回合」开关 + 中文音色备选**——避免每条文字回复都拖一个音频附件，只在「说完听回复」（语音进→语音出）时出声；并确认 Kokoro 的 8 个中文音色均可用于对比选型。
**Version note**: **A "voice turns only" switch for voice replies + Chinese voice options** — avoid attaching audio to every text reply, speaking only on "talk, then hear the answer" turns; also confirmed all 8 Kokoro Chinese voices work for selection.

## 仅语音回合才合成
# Synthesize only on voice turns

中文：
- **新配置** `Agents:Audio:SpeechOnlyForVoice`（默认 `false`）。开 `true` 时，**只有触发消息带语音附件**的回合才合成音频（`SpeechSynthesisService.TrySynthesizeAsync` 新增 `triggerHadVoice` 参数，网关按 `TriggerHadVoice(context)` 传入）；`false` 则维持旧行为（开启语音回复后每条回复都带音频）。
- **原因**：服务端 TTS 开启后，每条文字回复都附一段音频会让对话变重（每条几十~几百 KB）。开这个开关就只在真正的「语音进→语音出」时出声，其余回复仍由用户按需点 🔊 朗读（前端 Web Speech，无附件）。
- **Docker**：`AGENTS_AUDIO_SPEECH_ONLY_FOR_VOICE`（默认 `false`）。
English:
- **New config** `Agents:Audio:SpeechOnlyForVoice` (default `false`). When `true`, only turns whose **trigger message carries a voice attachment** synthesize audio (`SpeechSynthesisService.TrySynthesizeAsync` gained a `triggerHadVoice` parameter, passed by the gateway as `TriggerHadVoice(context)`); `false` keeps the previous behaviour (every reply carries audio once voice replies are on).
- **Why**: with server-side TTS on, attaching audio to every text reply makes threads heavy (tens to hundreds of KB each). This switch speaks only on genuine "talk, then hear the answer" turns; other replies stay readable on demand via the front-end 🔊 (Web Speech, no attachment).
- **Docker**: `AGENTS_AUDIO_SPEECH_ONLY_FOR_VOICE` (default `false`).

## 后台异步合成（不阻塞回复）+ 超时/长度调优
# Async synthesis (non-blocking) + timeout/length tuning

中文：
- **实测缺陷（1.0.211 遗留）**：语音回复是在回帖路径里**同步**合成的，而 Kokoro 在 CPU 上很慢（实测 924 字 ≈ **180 秒**，约 0.2s/字）。长回复会超时（默认 60s）被取消→静默不挂；即使不超时，也会把“正文出现”拖到几分钟后。
- **改法**：改为**回复结束后后台异步合成**（`ScheduleSpeech`，fire-and-forget），合成完成时用**不要求流在线**的挂载通道补挂并广播；正文立即返回，不受合成耗时影响；不再用运行取消令牌（避免长任务被上一轮取消连带杀掉）。
- **配套调优**：新增 `Agents:Audio:SpeechTimeoutSeconds`（默认 **300**，与转写超时解耦；转写仍用 `TimeoutSeconds`）；`SpeechMaxChars` 默认 600（≈2 分钟）。长回复自动截断。
- **实测**：语音消息→回复（109 字）→不阻塞返回，约 75s 后该消息自动挂上 `语音回复.mp3`（283KB，audio/mpeg）。
English:
- **Measured defect (from 1.0.211)**: voice reply was synthesized **synchronously** on the reply path, and Kokoro on CPU is slow (measured 924 chars ≈ **180 seconds**, ~0.2s/char). Long replies timed out (default 60s), got cancelled and silently attached nothing; even without a timeout, it delayed the reply's appearance by minutes.
- **Fix**: synthesize **in the background after the reply ends** (`ScheduleSpeech`, fire-and-forget), attaching via the **stream-free** attach path and broadcasting when done; the reply returns immediately, unaffected by synthesis time, and no longer uses the run cancellation token (so a long job isn't killed by the previous turn's cancellation).
- **Tuning**: new `Agents:Audio:SpeechTimeoutSeconds` (default **300**, decoupled from the transcription timeout which keeps `TimeoutSeconds`); `SpeechMaxChars` default 600 (≈2 minutes). Long replies are truncated.
- **Verified**: voice message → reply (109 chars) returns without blocking, and ~75s later the message automatically gains `语音回复.mp3` (283KB, audio/mpeg).

## 中文音色备选（Kokoro）
# Chinese voice options (Kokoro)

中文：Kokoro 提供 8 个中文音色，均已实测可合成（`zf_xiaoxiao` / `zf_xiaoyi` / `zf_xiaobei` / `zf_xiaoni` / `zm_yunjian` / `zm_yunxi` / `zm_yunxia` / `zm_yunyang`）；同一句话的试听样音已生成（本地 `artifacts/voice-samples/*.mp3`）。换音色只需改 `Agents:Audio:SpeechVoice`（或 `AGENTS_AUDIO_SPEECH_VOICE`），无需重启服务代码，重启 web 即可。
English: Kokoro ships 8 Chinese voices, all verified to synthesize (`zf_xiaoxiao` / `zf_xiaoyi` / `zf_xiaobei` / `zf_xiaoni` / `zm_yunjian` / `zm_yunxi` / `zm_yunxia` / `zm_yunyang`); listening samples of the same sentence were generated (local `artifacts/voice-samples/*.mp3`). To switch, just change `Agents:Audio:SpeechVoice` (or `AGENTS_AUDIO_SPEECH_VOICE`) and restart web — no code change.

## 测试
# Tests

中文：`SpeechSynthesisTests` 新增 2 例（开关开时非语音回合跳过且不发请求、语音回合正常合成；默认关时不受触发影响），共 16/16。
English: `SpeechSynthesisTests` gains 2 cases (with the switch on, non-voice turns skip without a request while voice turns synthesize; with it off, behaviour is unaffected by the trigger), 16/16 total.

# AG-UI 群聊桌面版 1.0.211 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.211 Release Notes (current Windows desktop release)

**版本说明**：**服务端语音回复改用独立的 TTS 服务（Kokoro-FastAPI）**——内置 speaches 的中文 TTS 不可用（piper-zh 近乎静音、Kokoro-zh 缺音素化后端），改用专门的 OpenAI 兼容 TTS 服务；已验证中文合成→转写闭环。
**Version note**: **Server-side voice reply now uses a standalone TTS service (Kokoro-FastAPI)** — the bundled speaches' Chinese TTS is unusable (piper-zh near-silent, Kokoro-zh missing a phonemizer backend), so switch to a dedicated OpenAI-compatible TTS service; the Chinese synthesize → transcribe loop is verified.

## 独立 TTS 服务
# Standalone TTS service

中文：
- **服务**：新增 `tts` 容器（`ghcr.nju.edu.cn/remsky/kokoro-fastapi-cpu:latest`，南京大学镜像站加速），OpenAI 兼容 `/v1/audio/speech`，**模型与中文音色内置、CPU 可跑**（`zf_xiaoxiao` / `zf_xiaoyi` / `zm_yunjian` …）。与 `speaches` 同属 `voice` profile：`docker compose --profile voice up -d`。
- **接线（无需改代码）**：`SpeechSynthesisService` 本就是标准 OpenAI 兼容，仅改默认值——`Agents:Audio:SpeechEndpoint=http://tts:8880/v1/audio/speech`、`SpeechModel=kokoro`、`SpeechVoice=zf_xiaoxiao`（可用 `AGENTS_AUDIO_SPEECH_*` 覆盖）。web 的 `NO_PROXY` 已含 `tts`。
- **实测闭环**：智能体回复→Kokoro 合成→`语音回复.mp3`（audio/mpeg，268KB）挂到消息；下载后回喂 speaches ASR 能读出中文（“我是组织架构构建石…”，同音字属正常）。
- **为什么不用 edge-tts**：`travisvn/openai-edge-tts` 实测被 Microsoft 以 403 拒（其 edge-tts 客户端已过期），故改用全本地的 Kokoro。
English:
- **Service**: a new `tts` container (`ghcr.nju.edu.cn/remsky/kokoro-fastapi-cpu:latest`, accelerated via the Nanjing University mirror), OpenAI-compatible `/v1/audio/speech`, **model and Chinese voices bundled, runs on CPU** (`zf_xiaoxiao` / `zf_xiaoyi` / `zm_yunjian` …). Same `voice` profile as `speaches`: `docker compose --profile voice up -d`.
- **Wiring (no code change)**: `SpeechSynthesisService` is standard OpenAI-compatible, so only defaults change — `Agents:Audio:SpeechEndpoint=http://tts:8880/v1/audio/speech`, `SpeechModel=kokoro`, `SpeechVoice=zf_xiaoxiao` (overridable via `AGENTS_AUDIO_SPEECH_*`). The web container's `NO_PROXY` now includes `tts`.
- **Verified loop**: agent reply → Kokoro synthesis → `语音回复.mp3` (audio/mpeg, 268KB) attached to the message; after download, feeding it back to speaches ASR reads out Chinese (homophones like 石/师 are normal).
- **Why not edge-tts**: `travisvn/openai-edge-tts` was rejected with 403 by Microsoft (its edge-tts client is outdated), so a fully local Kokoro is used instead.

# AG-UI 群聊桌面版 1.0.210 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.210 Release Notes (current Windows desktop release)

**版本说明**：**服务端语音回复（TTS）+ 语音链路的真实端点集成测试**——在 1.0.209「语音进 → 语音出」基础上补齐服务端能力：数字员工回复可合成为**音频附件**挂在消息上（供无浏览器 / 需分发音频的场景），并新增以真实 OpenAI 兼容服务为对象的集成测试（默认跳过）。
**Version note**: **Server-side voice reply (TTS) + real-endpoint integration tests for the voice path** — complementing 1.0.209's "voice in → voice out": a digital employee's reply can now be synthesized into an **audio attachment** on the message (for browser-less / audio-sharing scenarios), plus new integration tests against a real OpenAI-compatible service (skipped by default).

## 服务端语音回复（TTS）
# Server-side voice reply (TTS)

中文：
- **做什么**：开启 `Agents:Audio:SpeechEnabled` 且能解析出语音合成端点时，数字员工**每次回复的正文**会经 `SpeechSynthesisService` 合成为音频，用与技能产物**同一条回挂路径**（`GroupHub.AttachMessageAttachmentsAsync`）挂到消息上（前端以 `<audio>` 内联播放条呈现，可下载）。覆盖主回复 / 流水线 / 交接代答 / 指派提升 / 审批恢复等收尾路径。
- **协议**：OpenAI 语音合成——`POST {endpoint}` JSON `{model, input, voice, response_format}` 取音频字节；端点留空时由 `Agents:Endpoint` 派生 `{base}/v1/audio/speech`（与内置 speaches 一致）。
- **鲁棒**：合成前把正文整理成适合朗读的纯文本（去代码块 / 链接 / 标题 / 强调符）并截到 `SpeechMaxChars`；失败 / 超时 / 空文本一律**静默不挂、绝不阻断回复**。默认关闭。
- **与前端朗读（TTS）的分工**：前端 🔊（Web Speech API，离线、系统语音）是**默认且零配置**的“语音出”；服务端 TTS 是**可选增强**（产物是文件，可供非浏览器客户端、可分享/归档，或接入中文 TTS 服务提升音质）。
English:
- **What it does**: with `Agents:Audio:SpeechEnabled` on and a speech endpoint resolvable, every digital-employee **reply body** is synthesized to audio by `SpeechSynthesisService` and attached to the message via **the same recall path as skill-produced files** (`GroupHub.AttachMessageAttachmentsAsync`) — rendered as an inline `<audio>` player and downloadable. Covers the main reply, pipeline, relay, escalation and approval-resume finalization paths.
- **Protocol**: OpenAI speech synthesis — `POST {endpoint}` JSON `{model, input, voice, response_format}` returning audio bytes; when the endpoint is blank it is derived from `Agents:Endpoint` as `{base}/v1/audio/speech` (matching the bundled speaches).
- **Robust**: before synthesis the reply is cleaned into speakable plain text (code fences / links / headings / emphasis stripped) and truncated to `SpeechMaxChars`; failure / timeout / empty text all **silently skip, never blocking the reply**. Off by default.
- **How it splits with front-end read-aloud**: the front-end 🔊 (Web Speech API, offline system voices) is the **default, zero-config** "voice out"; server-side TTS is an **optional enhancement** (the output is a file — usable by browser-less clients, shareable/archivable, or wired to a Chinese TTS service for better quality).

## 配置
# Configuration

中文：`Agents:Audio:SpeechEnabled`（默认 `false`）/ `SpeechEndpoint`（留空派生 `/v1/audio/speech`）/ `SpeechModel` / `SpeechVoice` / `SpeechFormat`（默认 `mp3`）/ `SpeechMaxChars`（默认 1200）。Docker 对应 `AGENTS_AUDIO_SPEECH_*`；桌面见 `appsettings.json`。
English: `Agents:Audio:SpeechEnabled` (default `false`) / `SpeechEndpoint` (blank → derived `/v1/audio/speech`) / `SpeechModel` / `SpeechVoice` / `SpeechFormat` (default `mp3`) / `SpeechMaxChars` (default 1200). Docker: `AGENTS_AUDIO_SPEECH_*`; desktop in `appsettings.json`.

## 测试
# Tests

中文：新增 `SpeechSynthesisTests` 14/14（默认关 / 端点派生 / 成功入库为 audio 附件且校验请求 JSON / 格式→扩展名与 MIME / HTTP 失败降级 / 空文本不发请求 / 超长截断 / `ForSpeech` 清理 markdown）。新增 `AudioIntegrationTests` 2 例（**真实端点，默认跳过**）：`AGUI_ASR_TEST_ENDPOINT`+`AGUI_ASR_TEST_WAV` 真实转写、`AGUI_TTS_TEST_ENDPOINT/MODEL/VOICE` 真实合成；本地对内置 speaches 实测 **2/2 通过**。另在运行栈上做了应用级 e2e：经 `/agui/v1` 发一条带语音附件的消息，speaches 日志确认应用发起了 `POST /v1/audio/transcriptions` 且 200。
English: new `SpeechSynthesisTests` 14/14 (off by default / endpoint derivation / success registered as an audio attachment with request-JSON assertions / format → extension and MIME / HTTP-failure degradation / empty text sends no request / over-long truncation / `ForSpeech` markdown cleanup). New `AudioIntegrationTests` 2 cases (**real endpoint, skipped by default**): real transcription via `AGUI_ASR_TEST_ENDPOINT`+`AGUI_ASR_TEST_WAV`, and real synthesis via `AGUI_TTS_TEST_ENDPOINT/MODEL/VOICE`; verified **2/2 locally** against the bundled speaches. Also ran an app-level e2e on the live stack: sent a message with a voice attachment through `/agui/v1`, and the speaches log confirms the app issued `POST /v1/audio/transcriptions` returning 200.

# AG-UI 群聊桌面版 1.0.209 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.209 Release Notes (current Windows desktop release)

**版本说明**：**语音进 → 语音出闭环**——在语音问答（ASR，1.0.208）基础上，新增：① Docker 一键拉起本地语音转写服务（`--profile voice`，OpenAI 兼容）；② 前端「朗读回复」（TTS，Web Speech API，离线可用）+ 发送语音消息时自动朗读该条回复，形成完整闭环；③ 桌面版配置入口与说明。
**Version note**: **A voice in → voice out loop** — building on voice Q&A (ASR, 1.0.208): ① a one-command local transcription service for Docker (`--profile voice`, OpenAI-compatible); ② front-end "read reply aloud" (TTS via Web Speech API, works offline) plus auto-reading of the reply to a voice message, closing the loop; ③ desktop config and docs.

## 语音进 → 语音出闭环
# Voice in → voice out loop

中文：
- **本地转写服务（Docker，可选）**：新增 `speaches` 服务（`docker compose --profile voice up -d`，镜像 `ghcr.io/speaches-ai/speaches:latest-cpu`，按 digest 钉住）。它是 OpenAI 兼容服务，同时提供 `/v1/audio/transcriptions`（faster-whisper 转写）与 `/v1/audio/speech`（TTS）；模型首次从 Hugging Face 下载（走 `HTTP(S)_PROXY`），缓存落在 `agui-speaches-data` 卷；`speaches-preload` 一次性任务预先拉取模型。web 默认转写端点已指向它，开开关即可用：`.env` 置 `AGENTS_AUDIO_ENABLED=true`。
- **朗读回复（TTS，前端）**：数字员工回复的头部新增 🔊 按钮，点击用**系统语音**朗读（Web Speech API，自动按内容选中/英文），再次点击停止；朗读中按钮高亮为 ⏹。新增「个人资料 → 🔊 自动朗读数字员工回复」开关（本地偏好，默认关）。
- **语音问答闭环**：当用户发送含**语音附件**的消息时，该消息对应的数字员工回复**无论开关如何都会自动朗读**（`state.voiceTurn`），即「对着说话 → 听到回答」。
- **为什么 TTS 走浏览器原生而不是服务端**：系统语音（Windows SAPI / WebView2、浏览器）离线、零部署、零成本，且中/英文通常均已内置；服务端 TTS（如 speaches 的 `/v1/audio/speech`）作为可选替代，适合无浏览器/需附件分发的场景（本次仅落地前端路径）。
- **桌面版**：桌面版为本地自托管（WebView2）。**朗读（TTS）开箱即用**（用 Windows 内置语音）；**转写（ASR）**需在 `appsettings.json` 的 `Agents:Audio` 配置一个可达的 OpenAI 兼容转写端点（如另一台机器上的 speaches，或云端 whisper），默认关闭。
English:
- **Local transcription service (Docker, optional)**: new `speaches` service (`docker compose --profile voice up -d`, image `ghcr.io/speaches-ai/speaches:latest-cpu`, digest-pinned). It is OpenAI-compatible and serves both `/v1/audio/transcriptions` (faster-whisper) and `/v1/audio/speech` (TTS); models download from Hugging Face on first use (via `HTTP(S)_PROXY`) and cache in the `agui-speaches-data` volume, with a one-shot `speaches-preload` task pulling them ahead of time. The web app's default transcription endpoint already points at it — just set `AGENTS_AUDIO_ENABLED=true` in `.env`.
- **Read reply aloud (TTS, front end)**: a new 🔊 button in a digital employee's message header reads the reply with a **system voice** (Web Speech API; auto zh/en by content), and toggles to ⏹ while speaking. New "Profile → 🔊 Auto-read digital employee replies" switch (local preference, off by default).
- **Voice Q&A loop**: when a user sends a message with a **voice attachment**, the corresponding digital employee reply is **read aloud regardless of the switch** (`state.voiceTurn`) — talk to it, hear the answer.
- **Why TTS is browser-native, not server-side**: system voices (Windows SAPI / WebView2, browsers) are offline, zero-deploy and zero-cost, and typically already ship zh/en; server-side TTS (e.g. speaches' `/v1/audio/speech`) is an optional alternative for browser-less or attachment-sharing scenarios (only the front-end path shipped here).
- **Desktop**: the desktop app is self-hosted locally (WebView2). **Read-aloud (TTS) works out of the box** (Windows built-in voices); **transcription (ASR)** requires configuring a reachable OpenAI-compatible endpoint under `Agents:Audio` in `appsettings.json` (e.g. speaches on another host, or a cloud whisper); off by default.

## 配置
# Configuration

中文：
- Docker：`--profile voice` 启 `speaches`；`.env` 的 `AGENTS_AUDIO_ENABLED` / `AGENTS_AUDIO_ENDPOINT` / `AGENTS_AUDIO_MODEL` / `AGENTS_AUDIO_LANGUAGE`、`SPEACHES_PORT` / `SPEACHES_COMPUTE_TYPE` / `SPEACHES_PRELOAD_MODEL`。`NO_PROXY` 已包含 `speaches`（开了代理也能走内网直连）。
- 桌面：`src/AguiGroupChat.Desktop/appsettings.json` 的 `Agents:Audio`（默认 `Enabled=false`；端点/模型/语言可配）。
- 前端：朗读为本地偏好（`localStorage`，按用户），无需服务端配置。
English:
- Docker: start `speaches` with `--profile voice`; `.env` keys `AGENTS_AUDIO_ENABLED` / `AGENTS_AUDIO_ENDPOINT` / `AGENTS_AUDIO_MODEL` / `AGENTS_AUDIO_LANGUAGE`, `SPEACHES_PORT` / `SPEACHES_COMPUTE_TYPE` / `SPEACHES_PRELOAD_MODEL`. `NO_PROXY` now includes `speaches` (internal direct calls even with a proxy on).
- Desktop: `Agents:Audio` in `src/AguiGroupChat.Desktop/appsettings.json` (`Enabled=false` by default; endpoint/model/language configurable).
- Front end: read-aloud is a local per-user preference (`localStorage`), no server config needed.

# AG-UI 群聊桌面版 1.0.208 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.208 Release Notes (current Windows desktop release)

**版本说明**：**多模态进阶 · 语音问答（ASR）**——用户发来的**语音附件**可先转写为文字再注入模型上下文，数字员工从此能「听懂」语音消息（此前语音仅携元数据供前端播放、完全不进上下文）。默认关闭，开启并配置端点后生效。
**Version note**: **Multimodality · voice Q&A (ASR)** — **voice attachments** are transcribed to text and injected into the model context, so digital employees can now "hear" voice messages (previously audio was metadata-only for playback and never reached the context). Off by default; active once enabled with an endpoint.

## 语音问答（ASR）
# Voice Q&A (ASR)

中文：
- **一句话**：语音附件 → 转写文字 → 当作该附件的「正文」，走**与文档附件同一条**注入 / 预算路径（网关组织附件区时完成）。
- **默认关闭**：`Agents:Audio:Enabled=false` 时行为与旧版**完全一致**（语音仅元数据）；`AttachmentStore.IsExtractable` 对 audio 仍返回 `false`（转写是网关层额外做的，不改该语义）。
- **端点**：`Agents:Audio:Endpoint`（留空时由 `Agents:Endpoint` 派生 `{base}/v1/audio/transcriptions`）；`ApiKey` 留空回退 `Agents:ApiKey`。协议 = OpenAI 音频转写（`multipart/form-data`：`file` + `model` [+ `language`]，响应 `{"text":"…"}`；非 JSON 纯文本亦兼容）。
- **配置**：`Agents:Audio`——`Enabled` / `Endpoint` / `ApiKey` / `Model`（默认 `whisper-1`）/ `Language` / `MaxBytes`（默认 25MB）/ `TimeoutSeconds`（默认 60）/ `MaxFilesPerMessage`（默认 3）。
- **降级**：不可用 / 非语音 / 读取失败 / 超限 / 请求失败 / 空文本一律**静默不注入**并记日志，**绝不抛出、绝不阻断对话**；附件清单如实标注「语音（已转写为文字，见下方）」「语音（本次未转写，内容未注入）」「语音（未启用语音转写，内容无法被模型理解）」。
- **缓存**：转写结果按 attachmentId 缓存（同一段语音多轮只转一次，避免重复计费；上限 1000 条，超出清空重建）。
English:
- **In one line**: voice attachment → transcript text → treated as that attachment's **body**, going through the **same** injection / budget path as document attachments (assembled by the gateway's attachment section).
- **Off by default**: with `Agents:Audio:Enabled=false` behaviour is **identical** to before (audio is metadata-only); `AttachmentStore.IsExtractable` still returns `false` for audio (transcription is an extra gateway-layer step and does not change that semantics).
- **Endpoint**: `Agents:Audio:Endpoint` (when blank, derived from `Agents:Endpoint` as `{base}/v1/audio/transcriptions`); a blank `ApiKey` falls back to `Agents:ApiKey`. Protocol = OpenAI audio transcription (`multipart/form-data`: `file` + `model` [+ `language`], response `{"text":"…"}`; non-JSON plain text tolerated).
- **Config**: `Agents:Audio` — `Enabled` / `Endpoint` / `ApiKey` / `Model` (default `whisper-1`) / `Language` / `MaxBytes` (default 25MB) / `TimeoutSeconds` (default 60) / `MaxFilesPerMessage` (default 3).
- **Degradation**: unavailable / non-audio / read failure / oversize / request failure / empty text all **silently skip injection** and log, **never throw and never block the conversation**; the attachment list states it faithfully ("voice (transcribed, see below)" / "voice (not transcribed this time, content not injected)" / "voice (transcription disabled, content not understood by the model)").
- **Cache**: transcripts are cached by attachmentId (the same clip is transcribed once across turns to avoid double billing; cap 1000 entries, cleared and rebuilt on overflow).

## 测试
# Tests

中文：新增 `AudioTranscriptionTests` 17/17——默认关闭 / 端点派生与显式优先 / 成功解析（含非 JSON 纯文本、自定义模型）/ HTTP 失败与空文本与无 `text` 字段（含纯文本）/ 非语音与超限跳过（不发请求）/ 按附件缓存（第二次与清缓存后行为）/ 单条消息转写数量上限，以及网关级「转写文本确实进入附件区并带『语音转写』标签」/「未启用时保持元数据提示」。
English: new `AudioTranscriptionTests` 17/17 — off-by-default / endpoint derivation and explicit precedence / successful parsing (incl. non-JSON plain text and a custom model) / HTTP failure, empty text and missing `text` field (incl. plain text) / non-audio and oversize skipped without a request / per-attachment caching (second call and after clearing) / per-message transcription cap, plus gateway-level "the transcript does enter the attachment section with the 'voice transcript' label" and "metadata-only note when disabled".

# AG-UI 群聊桌面版 1.0.207 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.207 Release Notes (current Windows desktop release)

**版本说明**：**跨副本诊断**——多副本（Redis 共享存储）部署下，各副本周期性发布自身指标快照，任一副本都能在控制台看到**全部活跃副本**与汇总，并一键判断 Redis 共享存储是否健康。
**Version note**: **cross-replica diagnostics** — in a multi-replica (Redis shared storage) deployment each replica periodically publishes its own metric snapshot, so any replica can show **all live replicas** and their totals in the console, plus whether the Redis shared storage is healthy.

## 跨副本诊断
# Cross-replica diagnostics

中文：
- **副本身份**：`ReplicaIdentity` = 配置 `Observability:ReplicaId`（K8s 建议用 pod 名 / 序号）或回退「主机名:进程号」；启动时间一并上报。
- **心跳注册表**：`IReplicaRegistry`——Redis 部署用 `RedisReplicaRegistry`（每个副本写 `agui:replica:{id}`，TTL 60s，并登记到集合 `agui:replicas`；读取时**顺带清理已过期的索引项**，无需额外 GC 任务）；其余存储（memory / sqlite / postgres / mysql）用 `LocalReplicaRegistry`（仅自身，单副本下界面也自洽）。心跳间隔 15s（`ReplicaMetricsPublisher`）。
- **诊断端点**：`GET /ag-ui/admin/replicas`（Operator+）返回 `{ selfId, count, replicas[{replicaId, host, pid, startedAtMs, lastSeenMs, ageMs, isSelf, invocations, bridgeFailures, workingSetBytes, …}], aggregate{…}, redis{connected, latencyMs, endpoints} }`。`redis` 在非 Redis 部署下为 `null`。
- **看板**：控制台「运行指标」页新增**副本**表（含「本机」/「心跳陈旧」标记、汇总调用数与 Redis 健康行）。
- **为什么不是把副本号做成 Prometheus 标签**：Prometheus 的正确模型是**每个目标单独抓取**（它自带 `instance` 标签），所以 `/ag-ui/metrics` 保持「本进程」语义不变；跨副本视图是给**人**看诊断用的。
English:
- **Replica identity**: `ReplicaIdentity` = `Observability:ReplicaId` (K8s: a pod name / ordinal) or falls back to `hostname:pid`; the start time is reported too.
- **Heartbeat registry**: `IReplicaRegistry` — Redis deployments use `RedisReplicaRegistry` (each replica writes `agui:replica:{id}` with a 60s TTL and registers in the set `agui:replicas`; reads **prune expired index entries**, so no separate GC task is needed); other stores (memory / sqlite / postgres / mysql) use `LocalReplicaRegistry` (self only, so the UI is coherent even with one replica). Heartbeat interval 15s (`ReplicaMetricsPublisher`).
- **Diagnostics endpoint**: `GET /ag-ui/admin/replicas` (Operator+) returns `{ selfId, count, replicas[{…}], aggregate{…}, redis{connected, latencyMs, endpoints} }`; `redis` is `null` outside Redis deployments.
- **Dashboard**: the console's Runtime Metrics tab gains a **Replicas** table (with `self` / `stale` markers, aggregate invocations and a Redis health line).
- **Why not a replica label in Prometheus**: Prometheus's correct model is **one target per scrape** (it adds its own `instance` label), so `/ag-ui/metrics` keeps its per-process semantics; the cross-replica view is for humans doing diagnostics.

## 测试
# Tests

中文：新增 `ReplicaRegistryTests` 4/4（身份默认/覆盖、单副本发布前后）与 `RedisReplicaRegistryTests` 3/3（跨读取者可见、清理死索引、PING；Redis 不可达时跳过）、`ReplicaApiTests` 3/3（需 Operator+、返回自身与汇总、反映已发布的其它副本快照）；连同指标 / 桌面对等合跑通过。
English: new `ReplicaRegistryTests` 4/4 (identity default/override, local before/after publish), `RedisReplicaRegistryTests` 3/3 (visible to other readers, dead-index pruning, PING; skipped when Redis is unreachable) and `ReplicaApiTests` 3/3 (Operator+, self and aggregate, reflects published other-replica snapshots); passing together with the metric and desktop-parity suites.

# AG-UI 群聊桌面版 1.0.206 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.206 Release Notes (current Windows desktop release)

**版本说明**：**OpenTelemetry / Prometheus 指标导出**——新增 `GET /ag-ui/metrics`（Prometheus 文本格式），把已有进程内指标暴露给外部监控系统；Prometheus 可直接抓取，OTel Collector 也可用 `prometheus` receiver 采集后转发到任意后端。
**Version note**: **OpenTelemetry / Prometheus metric export** — a new `GET /ag-ui/metrics` (Prometheus text format) exposes the existing in-process metrics to external monitoring; Prometheus can scrape it directly, and an OTel Collector can collect it via its `prometheus` receiver and forward to any backend.

## 指标导出（Prometheus / OTel）
# Metric export (Prometheus / OTel)

中文：
- **端点**：`GET /ag-ui/metrics` → `text/plain; version=0.0.4`。指标名前缀统一 `knowgath_`：业务计数器（`knowgath_agent_invocations_total` / `_accepted_total` / `_rejected_total` / `knowgath_bridge_calls_total` / `knowgath_bridge_failures_total` / `knowgath_memory_hits_total` / `knowgath_memory_empty_total` / `knowgath_output_chars_total`）、分员工调用量（`knowgath_agent_invocations_by_agent_total{agent_id="…"}`，标签值转义）、进程与 GC（`knowgath_process_working_set_bytes` / `_private_bytes` / `_threads` / `_handles` / `knowgath_processors` / `knowgath_gc_heap_bytes` / `knowgath_dotnet_allocated_bytes_total` / `knowgath_gc_collections_total{generation="0|1|2"}`）与 `knowgath_uptime_seconds` / `knowgath_start_time_seconds` / `knowgath_up`。
- **鉴权（默认开）**：指标含数字员工标识与调用量，默认**要求鉴权**——可用专用抓取令牌（`Observability:PrometheusScrapeToken`，`Authorization: Bearer` 或 `?token=`）或已有登录会话 / 托管 API 密钥；`Observability:PrometheusRequireAuth=false` 仅建议在「仅内网可达」部署。`Observability:PrometheusEnabled=false` 时端点 404。
- **零新增依赖**：不内嵌 OTel SDK，直接输出标准文本格式（OTel Collector 的 `prometheus` receiver 可采集）；避免为一个导出功能引入大依赖与后台导出线程。
English:
- **Endpoint**: `GET /ag-ui/metrics` → `text/plain; version=0.0.4`. All names are prefixed `knowgath_`: business counters (`knowgath_agent_invocations_total` / `_accepted_total` / `_rejected_total` / `knowgath_bridge_calls_total` / `knowgath_bridge_failures_total` / `knowgath_memory_hits_total` / `knowgath_memory_empty_total` / `knowgath_output_chars_total`), per-agent counts (`knowgath_agent_invocations_by_agent_total{agent_id="…"}`, label values escaped), process & GC (`knowgath_process_working_set_bytes` / `_private_bytes` / `_threads` / `_handles` / `knowgath_processors` / `knowgath_gc_heap_bytes` / `knowgath_dotnet_allocated_bytes_total` / `knowgath_gc_collections_total{generation="0|1|2"}`) and `knowgath_uptime_seconds` / `knowgath_start_time_seconds` / `knowgath_up`.
- **Auth (on by default)**: metrics leak digital-employee ids and call counts, so the endpoint **requires auth by default** — either a dedicated scrape token (`Observability:PrometheusScrapeToken`, via `Authorization: Bearer` or `?token=`) or an existing session / managed API key; setting `Observability:PrometheusRequireAuth=false` is only advised for internal-only deployments. With `Observability:PrometheusEnabled=false` the endpoint 404s.
- **No new dependency**: no embedded OTel SDK — it emits the standard text format (an OTel Collector `prometheus` receiver can scrape it), avoiding a large dependency and a background export thread for one export path.

## 测试
# Tests

中文：新增 `MetricsApiTests` 6/6（无令牌 401 / 抓取令牌 200 且为 Prometheus 文本 / `?token=` 200 / 会话令牌 200 / 错误令牌 401 / 关闭时 404）；`MetricsTests` 新增 2 例（文本含核心指标与标签 / 标签值转义），共 7/7；连同桌面组合根对等合跑 25/25。
English: new `MetricsApiTests` 6/6 (no token 401 / scrape token 200 returning Prometheus text / `?token=` 200 / session token 200 / wrong token 401 / 404 when disabled); `MetricsTests` gains 2 cases (text carries core metrics and labels / label escaping), 7/7 total; 25/25 together with desktop parity.

# AG-UI 群聊桌面版 1.0.205 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.205 Release Notes (current Windows desktop release)

**版本说明**：管理员控制台三处界面修正——菜单文案、固定大小 + 内滚、页签不再溢出。
**Version note**: three admin-console UI fixes — the menu label, a fixed size with internal scrolling, and tabs that no longer overflow.

## 界面修正
# UI fixes

中文：
1. **菜单文案**：账号菜单里那条打开**整个控制台**的入口原来叫「用户管理」（`menu.admin`），语义不符。现两个角色统一为「**管理控制台**」（`menu.console`）——角色只决定可见页签，不影响菜单名；与之重复的 `menu.admin` 键已从中英字典删除。
2. **固定大小 + 滚动条**：`.admin-modal` 宽 `min(1000px, 96vw)`、高 `calc(100vh - 48px)`（固定，此前只有 `max-height`、高度随内容跳动）；标题 / 页签 / 底部按钮 `flex:none` 固定，内容包进新的 `.admin-body`（`flex:1 1 auto; min-height:0; overflow-y:auto`）**内部滚动**，切页签弹窗高度不变（实测 752px 恒定）。
3. **页签不再溢出**：`div.admin-tabs` 加 `flex-wrap:wrap`。11 个页签在 1000px 宽下正好单行容纳（不再溢出），窄屏（实测 820px）自动换行成两行且仍不横向溢出（只作用于控制台弹窗）。
English:
1. **Menu label**: the account-menu entry that opens the **whole console** was labelled "User Management" (`menu.admin`), which is misleading. Both roles now use "**Admin Console**" (`menu.console`) — the role only determines visible tabs, not the menu name; the now-duplicate `menu.admin` key was removed from both dictionaries.
2. **Fixed size + scrollbar**: `.admin-modal` is `min(1000px, 96vw)` wide and `calc(100vh - 48px)` tall (fixed; previously only `max-height`, so its height jumped with content); the title / tabs / footer buttons are `flex:none`, and the content is wrapped in a new `.admin-body` (`flex:1 1 auto; min-height:0; overflow-y:auto`) that **scrolls internally**, so the modal height is constant across tabs (measured 752px).
3. **Tabs no longer overflow**: `div.admin-tabs` gained `flex-wrap:wrap`. The 11 tabs fit on one line at 1000px wide (no overflow), and on narrow viewports (measured 820px) they wrap to two rows while still not overflowing horizontally (scoped to the console modal).

## 测试
# Tests

中文：新增 Playwright 回归 `tools/ui-admin-console-layout.mjs` 13/13（菜单文案 / 高度固定且切页签不变 / `.admin-body` 内滚且标题固定 / 宽屏不溢出 / 窄屏换行不溢出）；既有 `ui-role-console.mjs` 26/26 仍通过。
English: new Playwright regression `tools/ui-admin-console-layout.mjs` 13/13 (menu label / fixed height across tabs / `.admin-body` internal scroll with a fixed title / no overflow on wide screens / wrapping without overflow on narrow screens); the existing `ui-role-console.mjs` still passes 26/26.

# AG-UI 群聊桌面版 1.0.204 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.204 Release Notes (current Windows desktop release)

**版本说明**：**可观测性看板**——管理员控制台「运行指标」从“6 张卡片 + 2 条趋势线”升级为看板：新增**进程健康**（工作集 / GC 堆 / 线程 / 分配）、**五张多序列图**（调用速率 / 成功-拒绝 / 桥接调用-失败 / 记忆命中-空检索 / 输出吞吐）、**成功率环**与**时间范围选择**（30 分 / 2 时 / 4 时）。
**Version note**: an **observability dashboard** — the admin console's "Runtime Metrics" grows from "6 cards + 2 trend lines" into a dashboard: **process health** (working set / GC heap / threads / allocated), **five multi-series charts** (invocation rate / accepted-rejected / bridge calls-failures / memory hits-empty / output throughput), a **success-rate ring**, and a **range selector** (30 min / 2 h / 4 h).

## 可观测性看板
# Observability dashboard

中文：
- **进程健康卡片**（`#adminProcessCards`）：工作集内存（+ 私有）、GC 堆（+ G0/G1/G2 次数）、线程数（+ 核心数）、累计分配（+ 句柄）——来自 `MetricsService.Snapshot` 新增的 `process` 节点（`Process.GetCurrentProcess()` + GC 计数器；取不到时置 0，不影响其它指标）。
- **多序列图表**（`renderSparkSvg`）：服务端每 60s 采一点、缓存最近 240 点（约 4 小时）；前端按所选窗口取最近 N 点**差分**（间隔即 60s → 差分值＝每分钟增量），画调用速率 / 成功-拒绝 / 桥接调用-失败 / 记忆命中-空检索 / 输出吞吐五条多序列折线（按全局最大值归一，避免各图比例不一致而误读）。
- **成功率环**（`renderDonut`）：按窗口内成功 / 拒绝之和画环（≥90% 绿 / ≥60% 黄 / 否则红）。
- **时间范围选择**（`#metricsRange`）：切换时用最近一次快照**即时重画**（不重新请求）；数据不足 2 点时图表区隐藏、环显示 `—`。
English:
- **Process-health cards** (`#adminProcessCards`): working set (+ private), GC heap (+ G0/G1/G2 collections), threads (+ CPU count), total allocated (+ handles) — from a new `process` node in `MetricsService.Snapshot` (`Process.GetCurrentProcess()` + GC counters; zeros on failure, never affecting other metrics).
- **Multi-series charts** (`renderSparkSvg`): the server samples one point every 60s and caches the latest 240 (about 4 hours); the frontend takes the last N points of the selected window and **diffs** them (a 60s interval means each delta is a "per-minute increment"), drawing five charts — invocation rate / accepted-rejected / bridge calls-failures / memory hits-empty / output throughput (normalised to a shared max so the panels are comparable).
- **Success-rate ring** (`renderDonut`): drawn from the window's accepted / rejected sums (≥90% green / ≥60% amber / else red).
- **Range selector** (`#metricsRange`): changing it **redraws immediately** from the last snapshot (no refetch); with fewer than 2 points the chart area is hidden and the ring shows `—`.

## 测试
# Tests

中文：`MetricsTests` 新增 2 例（快照含 `process` 字段且数值合理 / 首个趋势采样点落环），共 5/5；连同市场在线分发 / 桌面组合根对等合跑 34/34；i18n zh=en=1708、missing 0。
English: `MetricsTests` gains 2 cases (the snapshot carries a sane `process` node / the first trend sample lands in the ring), 5/5 total; 34/34 together with the marketplace online distribution and desktop parity; i18n zh=en=1708, missing 0.

# AG-UI 群聊桌面版 1.0.203 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.203 Release Notes (current Windows desktop release)

**版本说明**：新增**模板市场在线分发**——除了内置角色包，管理员可配置<b>在线目录</b>（任意静态 JSON 地址），用户一键安装目录里的数字员工包；也可把自己创建的数字员工<b>发布</b>为可分发包文件自行托管。首次提供模板市场的图形界面。
**Version note**: adds **template marketplace online distribution** — beyond built-in packs, admins can configure **online catalogs** (any static JSON URL) and users install agent packs from them in one click; you can also **publish** agents you created as a distributable pack file to host yourself. This is the first GUI for the marketplace.

## 模板市场在线分发
# Template marketplace online distribution

中文：
- **合同即 JSON**：目录索引 `{ "packs": [ { packId, name, description, url, agentCount } ] }`（顶层数组亦可；`url` 省略则按目录同目录 `<packId>.json`），分发包 `{ schema: "knowgath/marketplace-pack/v1", packId, name, description, agents: [AgentDefinition…] }`。可放在任意静态服务器 / 对象存储，无需专用后端。
- **两趟导入 + 引用重映射**：安装 / 导入时先为每个数字员工分配不与现有目录冲突的新 ID（并避免包内互撞），再把<b>包内</b>相互引用（指派 AssignmentIds / 提升 EscalationAgentId / 交接 RelayToAgentId / 流水线步骤 / 技能目标）重映射到新 ID——分发的团队包引用不会断。
- **安装时安全净化**：外部包不得带入桥接端点 / 令牌（BridgeEndpoint / BridgeToken / BridgeMode 清空）、他实例的用户组 id（AllowedGroupIds 清空）、私有 / 技能目标标记（置 false）。
- **鲁棒拉取**：目录 / 包均 http/https、单响应 2MB 上限、10s 超时、不开重定向、目录 60s 缓存；拉取失败**不影响**聚合接口（逐源回报 `ok=false` + 错误）。
- **图形界面**：「AI 角色管理」工具栏新增「🏪 模板市场」：内置 + 远程包列表 + 一键安装；管理员可管目录源（添加 / 删除）；「📤 发布分包」多选自建数字员工 → 生成分包 JSON 下载。
English:
- **Contract is plain JSON**: catalog index `{ "packs": [ { packId, name, description, url, agentCount } ] }` (a top-level array also works; omit `url` to resolve `<packId>.json` next to the catalog), pack `{ schema: "knowgath/marketplace-pack/v1", packId, name, description, agents: [AgentDefinition…] }`. Host it on any static server / object store — no dedicated backend required.
- **Two-pass import with reference remapping**: install / import first assigns each agent a new ID that avoids both the existing catalog and intra-pack collisions, then remaps **intra-pack** references (AssignmentIds / EscalationAgentId / RelayToAgentId / pipeline steps / skill targets) to the new IDs — a distributed team pack keeps its links intact.
- **Install-time sanitisation**: an external pack may not bring bridge endpoints / tokens (BridgeEndpoint / BridgeToken / BridgeMode cleared), another instance's user-group ids (AllowedGroupIds cleared), or private / skill-target flags (set to false).
- **Robust fetching**: catalogs / packs are http/https only, 2MB per response, 10s timeout, no redirects, 60s catalog cache; a failing source **never** breaks the aggregate listing (per-source `ok=false` + error).
- **GUI**: a new "🏪 Marketplace" button in the AI-role toolbar: built-in + remote pack lists with one-click install; admins manage catalog sources (add / remove); "📤 Publish pack" multi-selects your agents and downloads a pack JSON.

## 测试
# Tests

中文：新增 `MarketplaceRemoteTests` 10/10（目录 / 包解析、相对地址、坏源、未知包、错 schema、空包、非 http 拒绝、超大拒绝、包内引用重映射、改名后重映射）与 `MarketplaceApiTests` 3/3（本地目录站集成：聚合 → 安装 → 净化校验 → 发布 → 坏源不致命；需管理员）；连同 Webhook / 对外 API / 托管密钥 / 桌面组合根对等合跑 64/64。
English: new `MarketplaceRemoteTests` 10/10 (catalog / pack parsing, relative URL, bad source, unknown pack, wrong schema, empty pack, non-http rejected, oversized rejected, intra-pack remap, remap after rename) and `MarketplaceApiTests` 3/3 (local catalog-station integration: aggregate → install → sanitisation → publish → bad source tolerated; admin-gated); 64/64 together with webhook / external API / managed keys / desktop parity.

# AG-UI 群聊桌面版 1.0.202 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.202 Release Notes (current Windows desktop release)

**版本说明**：新增**托管式 API 密钥**——管理员在控制台创建 / 列出 / 吊销外部集成凭证（替代只能写在 `appsettings`、改一次要重启的 `Auth:ApiKeys`）；密钥明文仅创建时显示一次，服务端只存哈希。
**Version note**: adds **managed API keys** — admins create / list / revoke external-integration credentials in the console (replacing `Auth:ApiKeys`, which lived in `appsettings` and needed a restart to change); the plaintext key is shown only at creation and only its hash is stored.

## 托管式 API 密钥
# Managed API keys

中文：
- **控制台页签**：管理员控制台新增「API 密钥」页签（仅 `Admin` 及以上）——新建（选名称 + 绑定账号）、列出（只回前缀与最近使用时间）、吊销。
- **只存哈希**：密钥为 32 字节随机值（`ak_…`），服务端只存 SHA-256，明文**只在创建时返回一次**；列表接口永不回密钥。最近使用时间按 60 秒节流刷新（避免每请求都写库）。
- **鉴权融合**：`AuthService.ResolveApiKey` 先查配置密钥（`Auth:ApiKeys`），再查托管密钥——两者共存，托管密钥命中即以绑定账号身份访问（继承其知聚成员 / 权限），可用于 `/agui/v1` 对外接口与出站集成。
- **权限与留痕**：创建 / 吊销仅管理员，写操作审计（`apikey.create` / `apikey.revoke`）；绑定账号不存在则 400；跨重启持久化（扩展区 `apiKeys`）。
- **管理 API**：`GET /ag-ui/admin/api-keys`（列表，脱敏）、`POST /ag-ui/admin/api-keys`（创建，返回一次性明文）、`DELETE /ag-ui/admin/api-keys/{id}`（吊销）。
English:
- **Console tab**: a new "API keys" tab in the admin console (visible to `Admin` and above) — create (name + bound account), list (prefix and last-used only), revoke.
- **Hash-only storage**: keys are 32 random bytes (`ak_…`); the server stores only SHA-256 and returns the plaintext **once at creation**; the list endpoint never returns the key. Last-used is refreshed at a 60s throttle to avoid a write per request.
- **Unified auth**: `AuthService.ResolveApiKey` first checks configured keys (`Auth:ApiKeys`) and then managed keys — both coexist; a managed key hit acts as its bound account (inheriting that account's circle memberships and permissions) and works for the `/agui/v1` external API and outbound integration.
- **Permissions and audit**: create / revoke are admin-only and audited (`apikey.create` / `apikey.revoke`); an unknown bound account yields 400; managed keys persist across restarts (extension section `apiKeys`).
- **Management API**: `GET /ag-ui/admin/api-keys` (list, masked), `POST /ag-ui/admin/api-keys` (create, returns the plaintext once), `DELETE /ag-ui/admin/api-keys/{id}` (revoke).

## 测试
# Tests

中文：新增 `ManagedApiKeyTests` 7/7（生成 / 哈希 / 前缀 / 匹配与最近使用 / 删除 / AuthService 解析 / 绑定账号不存在）与 `ApiKeyApiTests` 3/3（端到端：创建→用密钥调 `/agui/v1/me` 成功→吊销后 401；需管理员；绑定账号校验）；连同 Webhook / 对外 API / 桌面组合根对等合跑 48/48。
English: new `ManagedApiKeyTests` 7/7 (generate / hash / prefix / match & last-used / remove / AuthService resolution / unknown bound account) and `ApiKeyApiTests` 3/3 (end-to-end: create → call `/agui/v1/me` with the key → 401 after revoke; admin-gated; bound-account validation); 48/48 together with webhook / external API / desktop parity.

# AG-UI 群聊桌面版 1.0.201 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.201 Release Notes (current Windows desktop release)

**版本说明**：新增**版本化对外 REST API（`/agui/v1`，入站集成）**——与 1.0.200 的出站 Webhook 构成双向：外部系统既能被推送事件，也能**主动发消息并同步等回数字员工的回复**。
**Version note**: adds a **versioned external REST API (`/agui/v1`, inbound integration)** — together with 1.0.200's outbound webhooks this makes integration bidirectional: external systems can both receive pushed events and **actively send a message and synchronously wait for the agent's reply**.

## 版本化对外 REST API（入站集成）
# Versioned external REST API (inbound integration)

中文：
- **发消息 + 同步等回复**：`POST /agui/v1/groups/{groupId}/messages` 接受 `{content, topicId?, mentions?, mentionAll?, replyToMessageId?, attachments?, waitMs?}`，返回 `{messageId, groupId, topicId, createdAt, reply?}`。带 `waitMs`（上限 45000）时服务端长轮询，等该次触发的数字员工回复**完整**（正文非空且流式灌入已结束，`GroupHub.IsAgentMessageStreaming`）再返回，回复挂在 `reply` 并标 `complete`；超时回半截并标 `complete:false`，无回复则为 `null`。不带 `waitMs` 即异步（用历史 / `replies` 或 Webhook 取结果）。
- **其余端点**：`GET /agui/v1/me`（身份）、`GET /agui/v1/groups`（我参与的知聚）、历史（`after` 时间游标 / `before` 消息游标）、单条消息、`GET /agui/v1/groups/{groupId}/replies`（支持 `waitMs` 长轮询）。
- **鉴权与权限**：只接受真实身份（`Authorization: Bearer <apiKey|token>`），并以该身份做成员 / 可见性校验（非成员 / 非客服参与者 403、不存在 404、未登录 401、参数非法 400）；发送者始终取服务端解析身份，防伪造。
English:
- **Send + synchronous wait**: `POST /agui/v1/groups/{groupId}/messages` takes `{content, topicId?, mentions?, mentionAll?, replyToMessageId?, attachments?, waitMs?}` and returns `{messageId, groupId, topicId, createdAt, reply?}`. With `waitMs` (capped at 45000) the server long-polls until the agent reply triggered by that send is **complete** (non-empty body and its stream has ended — `GroupHub.IsAgentMessageStreaming`), returning it under `reply` with `complete`; on timeout it returns the partial reply with `complete:false`, and `null` when there is none. Without `waitMs` it is asynchronous (fetch later via history / `replies` or a webhook).
- **Other endpoints**: `GET /agui/v1/me` (identity), `GET /agui/v1/groups` (circles I participate in), history (`after` time cursor / `before` message cursor), single message, and `GET /agui/v1/groups/{groupId}/replies` (with `waitMs` long-poll).
- **Auth and permissions**: only a real identity (`Authorization: Bearer <apiKey|token>`) is accepted, and membership / visibility are checked as that identity (non-member / non-participant 403, missing 404, unauthenticated 401, bad params 400); the sender is always the server-resolved identity, so it cannot be spoofed.

## 测试
# Tests

中文：新增 `ExternalApiTests` 7/7（鉴权 / 身份 / 知聚列表 / 发消息 / 历史 / 单条 / 非成员 403 / 不存在 404 / 回复长轮询 / `waitMs` 同步等回复完整）；与 `Webhook`（13）、`GroupEventObserver`（3）、`DesktopCompositionTests`（12）合跑 35/35。
English: new `ExternalApiTests` 7/7 (auth / identity / group list / send / history / single / non-member 403 / missing 404 / reply long-poll / `waitMs` synchronous complete reply); 35/35 together with `Webhook` (13), `GroupEventObserver` (3), `DesktopCompositionTests` (12).

# AG-UI Group Chat Desktop 1.0.200 Release Notes (current Windows desktop release)

**版本说明**：新增**出站 Webhook（对外集成）**——管理员在控制台配置回调地址与事件订阅，知聚里的事件会以带 HMAC-SHA256 签名的 HTTP POST 推送到外部系统（n8n / 自建服务 / 低代码平台），让「知聚」从「人用的网站」升级为可被系统集成的中枢。
**Version note**: adds **outbound webhooks (external integration)** — admins configure callback URLs and event subscriptions in the console, and circle events are pushed to external systems (n8n / your own service / low-code) as HMAC-SHA256-signed HTTP POSTs, turning a circle from "a site people use" into an integration hub systems can call.

## 出站 Webhook（对外集成）
# Outbound webhooks (external integration)

中文：
- **订阅配置**：管理员控制台新增「Webhook」页签——新建 / 编辑 / 删除订阅，可选**事件类型**（群 / 成员 / 消息 / 工具 / 人机交互 / 话题 / 运行错误共 18 类；不勾选＝除高频流式增量外的全部）、可用 `groupId` **限定单个知聚**、可随时**启用 / 停用**；签名密钥 `whsec_…` **仅在创建或轮换时显示一次**（列表脱敏），落盘经 `SecretVault` 加密。
- **投递语义**：请求体为信封 `{ deliveryId, event, groupId, sentAt, data }`（`data` 即既有事件对象）；请求头含 `X-KnowGath-Event` / `X-KnowGath-Delivery` / `X-KnowGath-Timestamp` / `X-KnowGath-Signature`（`sha256=hex(HMAC-SHA256(secret, "{timestamp}.{body}"))`）。失败**重试至多 3 次**（退避 1s / 5s / 25s），单次超时 10s，串行化在后台工作线程（不阻塞实时扇出）。
- **可观测**：「最近投递」记录最近 100 条（时间 / 事件 / 结果码 / 耗时 / 错误），并有「测试」按钮发出一条 `PING` 投递即时验证连通与验签。
- **与在线人数无关**：出站钩子挂在群事件扇出点、**先于**「无订阅者则提前返回」执行——即使该群当前没有浏览器连接，外部系统依然能收到事件。观察者抛异常不影响正常聊天。
English:
- **Subscriptions**: a new "Webhooks" tab in the admin console — create / edit / delete subscriptions, pick **event types** (18 categories across group / member / message / tool / human-in-the-loop / topic / run error; none selected = all except high-frequency streaming deltas), optionally scope to a single circle via `groupId`, enable / disable at will; the `whsec_…` signing secret is **shown only once** (at creation or rotation), while the list is masked, and it is encrypted at rest via `SecretVault`.
- **Delivery semantics**: the body is an envelope `{ deliveryId, event, groupId, sentAt, data }` (`data` being the existing event object); headers carry `X-KnowGath-Event` / `X-KnowGath-Delivery` / `X-KnowGath-Timestamp` / `X-KnowGath-Signature` (`sha256=hex(HMAC-SHA256(secret, "{timestamp}.{body}"))`). Failures **retry up to 3 times** (backoff 1s / 5s / 25s) with a 10s per-attempt timeout, on background workers that never block realtime fan-out.
- **Observability**: "Recent deliveries" keeps the last 100 (time / event / status / latency / error), and a **Test** button fires a `PING` to check connectivity and signature verification immediately.
- **Independent of who is online**: the hook sits on the event fan-out point and runs **before** the "no subscribers, return early" check — so an external system still receives events even when nobody has a browser open on that circle. A throwing observer never affects normal chat.

## 测试
# Tests

中文：新增 `WebhookTests` 10/10（签名 / 订阅过滤 / 噪声排除 / 群过滤 / 信封与验签 / 重试到位 / 重试耗尽 / 测试投递）；`GroupEventObserverTests` 3/3（事件到达 / 无订阅者也触发 / 观察者异常不影响发送）；`WebhookApiTests` 3/3（需管理员 / URL 校验 / **端到端**：建群发消息 → 调度器 → 本地接收端投递并 HMAC 验签）；既有 `GroupHub` 相关用例 50/50 无回归。
English: new `WebhookTests` 10/10 (signing / subscription filtering / noise exclusion / group filter / envelope & verification / retry-to-success / retry-exhaustion / test delivery); `GroupEventObserverTests` 3/3 (events arrive / fires without subscribers / a throwing observer never breaks sending); `WebhookApiTests` 3/3 (admin-gated / URL validation / **end-to-end**: create group and send → dispatcher → local receiver with HMAC verification); 50/50 existing `GroupHub`-related cases, no regressions.

# AG-UI 群聊桌面版 1.0.199 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.199 Release Notes (current Windows desktop release)

**版本说明**：清理 1.0.198 审核遗留的 5 条 nit——配图反馈改为「按查询」维度（不再一损俱损）、只读库隐藏 👎、图谱视图隐藏无关关键词框、新视图补 aria、角色序回退加固，并同步手册。
**Version note**: clears the 5 leftover nits from the 1.0.198 review — per-query illustration feedback (no more collateral down-weighting), 👎 hidden for read-only libraries, the shared keyword box hidden in the graph view, ARIA on the new views, a hardened role-rank fallback, and matching docs.

## 审核 nit 清理
# Review nits

中文：
- **配图反馈改为按查询维度（C）**：反馈记入 `MismatchQueries` / `RelevantQueries`（归一化；空 = `*` 对所有查询生效），检索只对**同一主题词**降分（`POST …/feedback {relevant, query?}`）——对一张图在某个主题词下点一次「不相关」，不会波及它在其它主题词下的匹配；旧版无明细的计数仍按「对所有查询生效」兼容。
- **只读库隐藏 👎（C）**：图库「试检索」的 👎 仅对可写者（创建者 / 管理员）显示，避免群共享（只读）库上点了得 403。
- **图谱视图隐藏共享关键词框（A3）**：图谱有自己的主题词输入，图谱视图下隐藏 `#memKeyword` 与搜索按钮，消除「改了没反应」。
- **新视图补 aria（A3）**：视图切换按钮加 `aria-pressed`，时间线 / 图谱容器加 `role=region` + `aria-live=polite` + `aria-label`；👎 加 `aria-label`。
- **角色序回退加固（A1）**：`roleRank()` 在缺 `platformRole` 时按 `isSuperAdmin` / `isAdmin` 回退，不依赖单一字段。
English:
- **Per-query illustration feedback (C)**: feedback is recorded in `MismatchQueries` / `RelevantQueries` (normalized; empty = `*` = applies to all queries) and retrieval lowers the score only for the **same topic keyword** (`POST …/feedback {relevant, query?}`) — marking an image not-relevant for one topic no longer affects its matches for others; legacy counts without detail still apply to all queries.
- **👎 hidden for read-only libraries (C)**: the thumbs-down in the library test retrieval shows only to writers (creator / admin), so it can't be clicked on a shared read-only library and 403.
- **Shared keyword box hidden in the graph view (A3)**: the graph has its own keyword input, so `#memKeyword` and the search button are hidden in the graph view, removing the "changed but nothing happened" confusion.
- **ARIA on the new views (A3)**: view-switch buttons carry `aria-pressed`; the timeline / graph containers carry `role=region` + `aria-live=polite` + `aria-label`; the 👎 carries `aria-label`.
- **Role-rank fallback hardened (A1)**: `roleRank()` now falls back via `isSuperAdmin` / `isAdmin` when `platformRole` is absent, rather than depending on a single field.

## 测试
# Tests

中文：`ui-imglib-feedback` 14/14（新增「按查询隔离」用例）；`ui-role-console` 26/26、`ui-memory-views` 17/17、`ui-imglib-vector-status` 全通过；`ui-regression-compliance` 12/12（含管理员）；`ImageLibrary` / `Bm25` 单测 30/30。
English: `ui-imglib-feedback` 14/14 (new "per-query isolation" case); `ui-role-console` 26/26, `ui-memory-views` 17/17, `ui-imglib-vector-status` all pass; `ui-regression-compliance` 12/12 (with admin); `ImageLibrary` / `Bm25` unit tests 30/30.

# AG-UI 群聊桌面版 1.0.198 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.198 Release Notes (current Windows desktop release)

**版本说明**：对 1.0.195–1.0.197 三项做代码审核后的修复——手册纠正配图降权描述（只降分、不重套门槛、扣至 0 才剔除）、登出复位生效角色、时间线 403 给出明确指引。
**Version note**: review fixes for 1.0.195–1.0.197 — correct the illustration down-weighting description (lower score only, no gate re-application, excluded only at 0), reset the effective role on logout, and give a clear hint for the timeline 403.

## 审核修复
# Review fixes

中文：
- **手册纠正（C）**：原写「降权后低于门槛即不再召回」有误——惩罚在门槛过滤**之后**应用，且**不重新套用**语义门槛（否则会破坏「词面兜底不套门槛」这条被测试钉住的不变量）。改为准确描述：**只降分 / 降位，扣至 0 分才剔除**；并注明计数是「按图全局」而非按查询。
- **登出复位角色（A1）**：`logout()` 现同时复位 `state.platformRole = "user"` 与 `isSuperAdmin`，避免登出后残留旧角色影响菜单 / 控制台判定。
- **时间线 403 指引（A3）**：非管理员在「全部知聚」下切时间线（服务端按设计返回 403）时，改为提示「需先选择一个知聚」而不是笼统「记忆加载失败」。
English:
- **Doc fix (C)**: the old wording "once it drops below the gate the image is no longer recalled" was wrong — the penalty is applied **after** gate filtering and does **not** re-apply the semantic gate (that would break the "keyword fallback bypasses the gate" invariant). Now accurate: **lower score / lower rank, excluded only at 0**; and the counts are noted as **per-image, not per-query**.
- **Role reset on logout (A1)**: `logout()` now also resets `state.platformRole = "user"` and `isSuperAdmin`, so a stale role can't affect the menu / console after logout.
- **Timeline 403 hint (A3)**: for a non-admin with "All groups", switching to Timeline (the server returns 403 by design) now says "select a Circle first" instead of a generic "memory load failed".

## 测试
# Tests

中文：三项既有套件复跑全绿（`ui-role-console` 26/26、`ui-memory-views` 17/17、`ui-imglib-feedback` 13/13）。
English: the three suites re-run green (`ui-role-console` 26/26, `ui-memory-views` 17/17, `ui-imglib-feedback` 13/13).

# AG-UI 群聊桌面版 1.0.197 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.197 Release Notes (current Windows desktop release)

**版本说明**：图库配图增强——多模态自动打标（描述 + 检索标签）、检索可解释（命中类型 / 标签 / 分数）、错配可纠正（👎 负反馈降权）。
**Version note**: image-library illustration improvements — multimodal auto-tagging (caption + retrieval tags), explainable retrieval (match kind / tags / score), and correctable mismatches (👎 negative-feedback down-weighting).

## 图库配图
# Image-library illustrations

中文：
- **多模态自动打标**：`CaptionAsync` 改为要求视觉模型输出严格 JSON `{caption, tags[]}`；`caption` 为检索描述（≤400 字），`tags` 为检索标签（≤20，仅在用户未手填时写入）；两者一并参与向量化。解析失败回退为整段文本当描述。
- **可解释**：`/ag-ui/images/search` 每张图返回 `score` / `matchKind`（vector / keyword）/ `tags` / `mismatchCount`；图库「试检索」直接展示，看清「为什么配这张图」。
- **可纠正（负反馈）**：新增 `POST /ag-ui/image-libs/{libId}/assets/{assetId}/feedback {relevant}`；检索按「不相关 − 相关」差额降权（每条 0.08，封顶 0.32），降权后低于门槛即不再召回。计数随图库快照持久化。UI 在「试检索」命中行内提供 👎「标记不相关」并即时重跑试检索。
English:
- **Multimodal auto-tagging**: `CaptionAsync` now asks the vision model for strict JSON `{caption, tags[]}`; `caption` is the retrieval description (≤400 chars) and `tags` are retrieval tags (≤20, written only when the user hasn't entered tags); both feed vectorization. Parse failure falls back to the whole text as the caption.
- **Explainable**: `/ag-ui/images/search` returns `score` / `matchKind` (vector / keyword) / `tags` / `mismatchCount` per image; the library's test retrieval shows them so it's clear why an image matched.
- **Correctable (negative feedback)**: new `POST /ag-ui/image-libs/{libId}/assets/{assetId}/feedback {relevant}`; retrieval down-weights by (not-relevant − relevant) (0.08 each, capped 0.32) and drops below the gate once penalized. Counts persist with the library snapshot. The UI offers 👎 "mark not relevant" on test-retrieval rows and re-runs the probe immediately.

## 测试
# Tests

中文：新增 `tools/ui-imglib-feedback.mjs`（真实浏览器，13 项：反馈计数、降权实测、解释字段、UI 👎 落库）；既有 `ImageLibrary` / `Bm25` 单测 30/30 通过。
English: added `tools/ui-imglib-feedback.mjs` (real browser; 13 checks: feedback counts, measured down-weighting, explain fields, UI 👎 write-back); existing `ImageLibrary` / `Bm25` unit tests pass 30/30.

# AG-UI 群聊桌面版 1.0.196 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.196 Release Notes (current Windows desktop release)

**版本说明**：记忆可视化——记忆管理弹窗新增「列表 / 时间线 / 图谱」视图；时间线接入既有接口，图谱新增只读端点 `GET /ag-ui/memory/graph` 并用应用内 mermaid 画关系图。
**Version note**: memory visualization — the memory dialog gains List / Timeline / Graph views; the timeline consumes the existing endpoint, and the graph adds a read-only `GET /ag-ui/memory/graph` rendered via the in-app mermaid.

## 记忆可视化
# Memory visualization

中文：
- **视图切换**：`#memoryModal` 顶部新增「📋 列表 / ⏱ 时间线 / 🕸 图谱」，默认列表；切换知聚 / 关键词按当前视图刷新。
- **时间线**：调 `GET /ag-ui/memory/timeline`（既有接口）按话题回放记忆演进（旧→新），显示时间、发言者、级别与内容。
- **图谱**：新增只读端点 `GET /ag-ui/memory/graph?groupId=&q=`，按 `groupId` + 主题词语义召回种子实体并 n 跳遍历返回子图（实体 + 关系边），前端 `buildMermaidGraph` 转 Mermaid `graph LR` 后用 `renderMermaidBlocks` 渲染；未启用 / 未命中 / 未选知聚 / 无主题词各有明确状态。需群成员（管理员可跨群）。
- **实现**：后端 `MemoryApi.cs` 新端点复用 `IGraphMemory.SearchAsync` / `Stats` 与 `MemoryGovernance` 范围判定；前端复用既有 mermaid 管线，未引入任何前端图库。
English:
- **View switch**: `#memoryModal` gains "📋 List / ⏱ Timeline / 🕸 Graph", defaulting to List; changing the Circle / keyword refreshes the active view.
- **Timeline**: calls `GET /ag-ui/memory/timeline` (existing) to replay memory evolution per topic (old→new), showing time, sender, level, and content.
- **Graph**: a new read-only `GET /ag-ui/memory/graph?groupId=&q=` seeds entities by `groupId` + topic semantics and does an n-hop walk, returning the subgraph (entities + relation edges); the frontend's `buildMermaidGraph` turns it into a Mermaid `graph LR` and `renderMermaidBlocks` draws it. Clear states for not-enabled / no match / no Circle / no keyword. Requires circle membership (admins may cross circles).
- **Implementation**: the new `MemoryApi.cs` endpoint reuses `IGraphMemory.SearchAsync` / `Stats` and `MemoryGovernance` scoping; the frontend reuses the existing mermaid pipeline with no new frontend library.

## 测试
# Tests

中文：新增 `tools/ui-memory-views.mjs`（真实浏览器，17 项：视图切换、时间线、图谱状态、`buildMermaidGraph`、端点契约）。
English: added `tools/ui-memory-views.mjs` (real browser; 17 checks: view switch, timeline, graph states, `buildMermaidGraph`, endpoint contract).

# AG-UI 群聊桌面版 1.0.195 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.195 Release Notes (current Windows desktop release)

**版本说明**：前端按生效平台角色收敛——Operator（运维）获得只读「管理控制台」与「系统状态」入口，Admin+ 保持全量。
**Version note**: frontend entries now follow the effective platform role — Operator gets the read-only admin console and System Status, Admin+ keeps the full set.

## 平台角色收敛
# Role-scoped entries

中文：
- **菜单按生效角色收敛**：`#meMenuAdmin`（管理控制台）与 `#meMenuStatus`（系统状态）现对 **Operator 及以上**可见；数据备份 / 模型配置 / 白标仍限 **Admin 及以上**。Operator 下「管理控制台」文案经 `data-i18n` 切到 `menu.console`（`管理控制台` / `Admin Console`），切换语言不会被重置。
- **控制台页签按角色收敛**：`applyAdminTabVisibility` 令 Operator 仅见只读页签（用量统计 / 审计日志 / 运行指标，对应后端 `RequireRoleFilter(Operator)`），默认落到「用量统计」；Admin / SuperAdmin 见全量 9 页签。
- 与 `docs/RBAC.md` §5 / `docs/ui-design.md` §7.6「按生效角色细分菜单」现已一致；两份用户手册 §8.1.5 / §8.17 / §8.18 已同步更新。
English:
- **Menus scoped by effective role**: `#meMenuAdmin` (admin console) and `#meMenuStatus` (System Status) are now visible to **Operator and above**; Data Backup / Model Config / Whitelabel stay **Admin and above**. For Operator the console label flips to `menu.console` (`管理控制台` / `Admin Console`) via `data-i18n`, so a language switch no longer resets it.
- **Console tabs scoped by role**: `applyAdminTabVisibility` gives Operator only the read-only tabs (Usage / Audit / Runtime Metrics, matching the backend `RequireRoleFilter(Operator)`) and defaults to Usage; Admin / SuperAdmin see all 9 tabs.
- Now consistent with `docs/RBAC.md` §5 / `docs/ui-design.md` §7.6; both user manuals' §8.1.5 / §8.17 / §8.18 were updated.

## 测试
# Tests

中文：新增 `tools/ui-role-console.mjs`（真实浏览器，26 项：菜单可见性、文案切换、控制台只读页签、系统状态可开）。
English: added `tools/ui-role-console.mjs` (real browser; 26 checks: menu visibility, label switching, read-only tabs, System Status opens).

# AG-UI 群聊桌面版 1.0.194 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.194 Release Notes (current Windows desktop release)

**版本说明**：数据正确性收口——数字员工导出/导入改为无损往返（补齐定时 / 审批 / 阶段开关 / 指派 / 交接 / 流水线），导入改为两轮写入、引用可整体成立；附件注入预算默认值统一为一处。
**Version note**: data-correctness hardening — digital-employee export/import is now lossless (schedule / approval names / stage switches / assignment / relay / pipeline), import is two-pass so cross-references resolve, and the attachment-injection budget defaults are unified into one source.

## 数据正确性
# Data correctness

中文：
- **数字员工导出 / 导入无损往返**：此前 `serializeAgent` 与导入请求体遗漏 `schedule`（定时任务）、`requireApprovalToolNames`（审批名单）、`disableBridge` / `disableRelay` / `disableOrgRoute`（阶段开关）、`relayToAgentId` / `pipeline`（交接 / 流水线），导入还会丢掉 `allowedGroupIds`（用户组白名单）——迁移时**静默丢配置**。现全部随导出往返。
- **导入改为两轮写入**：第一轮创建（剥离「引用型」字段），第二轮 `PUT` 补写指派 / 提升 / 交接 / 流水线——引用同批创建的其他数字员工（含「被引用者后创建」的顺序）也能整体成立；服务端对未注册目标的 400 不再导致整条失败（引用暂不可解析时保留无引用状态并计数提示）。`allowedGroupIds` 先按目标实例已有分组过滤，避免引用不存在分组。
- **附件注入预算默认值统一**：`AttachmentStore.DefaultTextCharsPerFile` / `DefaultTextCharsTotal` 现直接取自 `PromptBudgetOptions.Default`（40000 / 200000），不再与 PromptBudget 各写一份、发生漂移（运行期实际生效值仍以 PromptBudget 配置为准）。
English:
- **Lossless digital-employee export/import**: `serializeAgent` and the import body used to omit `schedule` (cron), `requireApprovalToolNames`, `disableBridge` / `disableRelay` / `disableOrgRoute` (stage switches), `relayToAgentId` / `pipeline` (relay / pipeline), and the import dropped `allowedGroupIds` (user-group whitelist) — silently losing configuration on migration. All now round-trip.
- **Import is now two-pass**: pass 1 creates (reference-type fields stripped), pass 2 `PUT`s assignment / escalation / relay / pipeline back — so references to other employees created in the same batch (including “referenced agent created later”) resolve as a whole; the server's 400 for unregistered targets no longer fails the whole employee (an unresolvable reference keeps the no-reference state and is reported). `allowedGroupIds` is filtered against the target instance's existing groups first.
- **Attachment-injection budget defaults unified**: `AttachmentStore.DefaultTextCharsPerFile` / `DefaultTextCharsTotal` now come directly from `PromptBudgetOptions.Default` (40000 / 200000) instead of being duplicated and drifting (runtime effective values still follow the PromptBudget config).

## 测试
# Tests

中文：新增 `tools/verify-agent-roundtrip.mjs`（真实浏览器，18 项：字段暴露、导出完整性、两轮写入、导入后引用成立）。
English: added `tools/verify-agent-roundtrip.mjs` (real browser; 18 checks: field exposure, export completeness, two-pass write, post-import references).

# AG-UI 群聊桌面版 1.0.193 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.193 Release Notes (current Windows desktop release)

**版本说明**：完整用户手册——应用内新增「📖 完整手册」入口（中英双语，按界面语言渲染、可搜索、目录跳转），并随本次发布产出 `docs/user-guide.md` / `docs/user-guide.en.md`。
**Version note**: a full user manual — a new in-app “📖 Full Manual” entry (bilingual, rendered in the current UI language, with search and table-of-contents jump), plus the `docs/user-guide.md` / `docs/user-guide.en.md` delivered with this release.

## 完整用户手册
# Full user manual

中文：
- **新增「📖 完整手册」入口**：帮助弹窗 `#helpModal` 底部新增按钮，打开 `#manualModal`，按**当前界面语言**拉取内置用户手册（`GET /ag-ui/manual?lang=zh|en`，**需登录**）并用与消息正文相同的 Markdown 管线渲染；含搜索框（按顶层块过滤 + 空态）、章节标题自动生成可跳转锚点，`Esc` / 点遮罩 / 关闭按钮均可收起。
- **手册随程序集嵌入**：`docs/user-guide.md`（中文，4,887 行）与 `docs/user-guide.en.md`（英文，4,895 行）作为嵌入资源编入 `AguiGroupChat.Web`，开发 / Docker / 桌面三处无路径差异（`Dockerfile` 发布前 `COPY docs/ docs/`）。英文那份必须 `WithCulture="false"` —— 否则 MSBuild 会把文件名里的 `.en.` 当作 culture（卫星资源）而从主程序集剔除。
- **手册内容**：按代码实现逐模块、逐字段、逐按钮描述，共 9 章（快速上手 / 知聚与消息 / 数字员工与组织架构 / 技能与本机桥 / 知识库与图库 / 记忆系统 / 办公文档查看与播放 / 管理控制台与账号 / 接口·配置·排障）；每章附「实现与文档的偏差」与「需复核点」，只写代码可证实的事实。
English:
- **New “📖 Full Manual” entry**: a new button at the bottom of the help dialog (`#helpModal`) opens `#manualModal`, which fetches the built-in user manual in the **current UI language** (`GET /ag-ui/manual?lang=zh|en`, **login required**) and renders it through the same Markdown pipeline as message bodies; it has a search box (top-level block filtering + empty state), auto-generated jump anchors on section headings, and closes via `Esc` / overlay click / the close button.
- **The manual is embedded in the assembly**: `docs/user-guide.md` (Chinese, 4,887 lines) and `docs/user-guide.en.md` (English, 4,895 lines) ship as embedded resources in `AguiGroupChat.Web`, so dev / Docker / desktop share one source with no path differences (`Dockerfile` does `COPY docs/ docs/` before publish). The English one requires `WithCulture="false"` — otherwise MSBuild treats the `.en.` in the file name as a culture (satellite resource) and drops it from the main assembly.
- **Manual scope**: every module, field, and button described against the code across 9 chapters (Quick Start / Circles & Messages / Digital Employees & Org Structure / Skills & Local Bridge / Knowledge Bases & Image Libraries / Memory System / Document Viewing & Playback / Admin Console & Account / API·Config·Troubleshooting); each chapter ends with “Implementation vs. documentation deviations” and “Points to verify”, stating only code-verifiable facts.

# AG-UI 群聊桌面版 1.0.192 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.192 Release Notes (current Windows desktop release)

**版本说明**：知识库管理——新增「显示自动沉淀的知识库」开关（默认隐藏，量多时不淹没自建库）；修复操作列过窄导致展开图标压住描述文字。
**Version note**: knowledge base management — a new “Show auto-consolidated KBs” toggle (hidden by default) and a fix for the action column being too narrow (the expand icon overlapped the description).

## 知识库管理
# Knowledge base management

中文：
- **新增开关「显示自动沉淀的知识库」（默认关）**：系统按周期为每个知聚生成的沉淀目标库（名 `自动沉淀·{群名}`，实例多时数百个）默认**隐藏**；开关旁标出已隐藏数量，列表为空时明确说明「只剩自动沉淀库（已隐藏 N 个）」，不会让人以为库丢了。识别靠服务端的 `autoConsolidated` 标记（不是名字），打标前的老库按名字前缀兜底。**同一开关也加在数字员工表单的「＋ 绑定知识库」选取弹窗里**（只在知识库种类时显示，两处共享同一状态）。
- **修复：操作列过窄导致展开图标压住描述**：操作列固定 150px 而内容需约 182px，`justify-content:flex-end` 把溢出**推向左侧** → 展开 `▸` 与描述文字重叠。列宽改为 190px，并让图标不收缩、上传按钮可省略号收缩（极端情况只挤按钮，不压邻列）。知识库与图库共用同一套类，一处修两处生效。
English:
- **New toggle “Show auto-consolidated KBs” (off by default)**: the per-group sediment target KBs the system creates periodically (named `自动沉淀·{group}`, hundreds on busy instances) are hidden by default; the toggle shows how many are hidden, and an empty list explains “only auto-consolidated KBs remain (N hidden)” so nothing looks lost. Detection uses a server-side `autoConsolidated` flag (not the name); pre-flag KBs fall back to the name prefix. **The same toggle is also in the “＋ Bind knowledge base” picker of the agent form** (shown only for the KB kind; both places share one state).
- **Fix: the action column was too narrow, so the expand icon overlapped the description**: the column was fixed at 150px while its content needs ~182px, and `justify-content:flex-end` pushed the overflow to the left — the `▸` sat on top of the description text. The column is now 190px, icons no longer shrink, and the upload button ellipsizes (only the button is squeezed, never the neighbour column). Shared by the KB and image-library modals, so one fix covers both.

# AG-UI 群聊桌面版 1.0.191 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.191 Release Notes (current Windows desktop release)

**版本说明**：代码审核修复——PPT 播放不再因残留的逐段标注丢文字；逐段切分只在可靠时进行；桌面首次提示不再可能重复弹；桌面共享目录与测试补齐。
**Version note**: review fixes — PPT playback no longer loses text due to stale paragraph-build markers; paragraph splitting only happens when reliable; the desktop first-launch notice can no longer double-fire; desktop shared paths and tests were tidied.

## 审核修复
# Review fixes

中文：
- **播放不再丢文字（重要）**：`bldLst` 里**残留**的 `build="p"` 标注（PowerPoint 删除动画后常见）此前会让对应文本框被**整段留白清空**、文字在播放里凭空消失。现在只把「真的出现在 timing 里」的逐段形状当作逐段，其余保持原样可见。回归用例 `PresentationPlaybackStaleBuildTests`（已验证：去掉修复即变红）。
- **逐段切分改为「可靠才切」**：由「取最宽 k-1 个间隔」改为**只有墨迹块数恰等于段数**时才切；有段落折行或挨着时一律回退为整形状精灵，不再可能出「半段先出现」的切错。
- **桌面首次提示不会重复弹**：`TryClaimOnce` 在跨进程独占锁内完成「查标记 + 写标记」，多实例并发启动只有一个会弹（新增并发用例）。
- **桌面共享目录集中一处**（`DesktopPaths.DataDir`），避免三处各自硬编码 `%LocalAppData%\AguiGroupChat`。
- **测试补齐**：`/admin/status` 的 `docPreview` 正向路径（已注册转换器 → 就绪）现在有断言。
English:
- **Playback no longer loses text (important)**: a stale `build="p"` marker in `bldLst` (common after deleting an animation in PowerPoint) used to blank that text box entirely, making its text vanish during playback. Only paragraph shapes actually targeted by `timing` are treated as paragraph builds now; others stay visible. Regression: `PresentationPlaybackStaleBuildTests` (verified red without the fix).
- **Paragraph splitting only when reliable**: replaced “take the k-1 widest gaps” with **only split when the number of ink blocks equals the number of paragraphs**; wrapped or adjacent paragraphs fall back to a whole-shape sprite, so a “half paragraph first” mis-split can no longer happen.
- **The desktop first-launch notice can't double-fire**: `TryClaimOnce` checks and writes the marker inside a cross-process exclusive lock, so only one instance shows it (new concurrency test).
- **Desktop shared path centralized** (`DesktopPaths.DataDir`) instead of three hardcoded copies of `%LocalAppData%\AguiGroupChat`.
- **Test coverage**: the positive `docPreview` path of `/admin/status` (converter registered → ready) is now asserted.

# AG-UI 群聊桌面版 1.0.190 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.190 Release Notes (current Windows desktop release)

**版本说明**：桌面版首次启动的「未装 LibreOffice」一次性提示——说明哪些功能受影响、如何启用，之后不再打扰。
**Version note**: a one-time first-launch notice in the desktop build when LibreOffice is missing — explains what is affected and how to enable it, and never nags again.

## 首次启动一次性提示（缺 LibreOffice）
# One-time first-launch notice (missing LibreOffice)

中文：
- **触发条件**：桌面版首次启动时若**未检测到 LibreOffice**（办公文档在线查看 / PPT 播放的唯一外部依赖），弹一次说明；**只弹一次**（在 `%LocalAppData%\AguiGroupChat\docpreview-hint-shown` 落标记），之后不再打扰。
- **提示内容**：PPT / Word / Excel 在线查看与播放不可用（附件仍可下载后用本地应用打开）；如何启用（装 LibreOffice 或用 `AGUI_SOFFICE` 指定 soffice 后重启）；安装后可到「管理员 → 系统状态 → 文档在线转换」确认。
- **探测同源**：与后端用同一套探测（环境变量 + `C:\Program Files\LibreOffice\…`），不占端口、不需登录；未装时才提示，已装则完全不打扰。
- Web 版（Docker 已内置 LibreOffice）不受影响。
English:
- **When**: on the desktop build's first launch, if **LibreOffice is not detected** (the only external dependency for office document online view / PPT playback), a one-time notice is shown; it appears **only once** (a marker at `%LocalAppData%\AguiGroupChat\docpreview-hint-shown`) and never nags again.
- **What it says**: PPT / Word / Excel online view and playback are unavailable (attachments can still be downloaded and opened with a local app); how to enable it (install LibreOffice, or point `AGUI_SOFFICE` at soffice and restart); and where to confirm it afterwards (Admin → System status → Document conversion).
- **Same detection as the backend** (env var + `C:\Program Files\LibreOffice\…`), no port or login needed; it only appears when LibreOffice is missing.
- The web build (LibreOffice bundled in Docker) is unaffected.

# AG-UI 群聊桌面版 1.0.189 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.189 Release Notes (current Windows desktop release)

**版本说明**：管理端「系统状态」显式展示文档在线转换能力（未就绪给修复指引）；「回到最新消息」改用内联 SVG 图标。
**Version note**: the admin “System status” panel now shows document-conversion availability (with a fix hint when missing); the “jump to latest” control now uses an inline SVG icon.

## 文档在线转换能力显式提示
# Explicit document-conversion availability

中文：
- **系统状态新增能力行**：`GET /ag-ui/admin/status` 新增 `docPreview.available`（探测本机 LibreOffice），管理端「系统状态」展示「文档在线转换：就绪 / 未就绪」；未就绪时值标警示色，并在下方给出修复指引（装 LibreOffice 或用 `AGUI_SOFFICE` 指定 soffice 路径后重启），不再等用户点「在线查看 / ▶ 播放」时才报 503。
- **桌面版尤其相关**：MSI 不捆绑 LibreOffice，缺它即 PPT / Word / Excel 在线查看与播放不可用（Docker / Web 版镜像已内置）。
- 合规回归新增 2 项检查；并修复该套件 `ADMIN_USERNAME/PASSWORD` 未生效的问题。
English:
- **Capability row in System status**: `GET /ag-ui/admin/status` now returns `docPreview.available` (detects local LibreOffice); the admin “System status” panel shows “Document conversion: Ready / Not ready” and, when not ready, a fix hint (install LibreOffice or point `AGUI_SOFFICE` at soffice and restart) — instead of only failing with 503 when the user clicks view/play.
- **Most relevant to the desktop build**: the MSI does not bundle LibreOffice, so without it PPT / Word / Excel online view/play is unavailable (the Docker/web image bundles it).
- The compliance suite gained 2 checks, and its `ADMIN_USERNAME/PASSWORD` env vars are now actually honored.

## 「回到最新消息」图标美化
# “Jump to latest” icon polish

中文：
- 图标由文本字符 `↓` 改为**内联 SVG 箭头**（跨字体渲染一致、垂直居中）；**无新消息时是 40×40 圆形图标钮，有新消息时展开成胶囊**并轻微上下浮动（`prefers-reduced-motion` 下不浮动）。
English:
- The icon is now an **inline SVG arrow** instead of the text glyph `↓` (consistent across fonts, vertically centered); a **40×40 circle when there are no unseen messages**, expanding into a pill with the count when there are, with a subtle bob (`prefers-reduced-motion` disables it).

# AG-UI 群聊桌面版 1.0.188 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.188 Release Notes (current Windows desktop release)

**版本说明**：演示文稿在线播放的动画平顺性优化——消除跨页撕裂、重复清屏闪烁与翻页卡顿。
**Version note**: smoother PPT playback animation — no more cross-slide tearing, re-rasterization flicker, or per-page jank.

## 播放平顺性
# Playback smoothness

中文：
- **转场带动精灵层**：转场动画从画布改挂到整个「舞台」，底图与形状精灵一起动，跨页不再撕裂。
- **不再重复清屏**：画布尺寸不变就不重设 backing store；进/出全屏连发的 `fullscreenchange`/`resize` 合并为一次渲染。
- **翻页更轻**：只在形状并集 bbox 内取差、精灵直接用 `<canvas>`（去掉 PNG 同步编码）、同页离屏渲染缓存复用——大幅减少主线程长任务；同一步的多个精灵只强制一次重排。
- **位移与曲线**：飞入改 `translate3d` 并按形状尺寸给出最小位移（短行带也看得清），缓动改为更平滑的 `cubic-bezier`；dpr 统一上限 2，避免高 DPR 下像素网格错位。
English:
- **Transition moves the sprite layer too**: the transition now animates the whole stage, so the base image and sprites move together — no cross-slide tearing.
- **No re-rasterization flicker**: the canvas backing store is kept when the size is unchanged; the `fullscreenchange`/`resize` burst on entering fullscreen is coalesced into one render.
- **Lighter page turns**: diff only inside the union bbox of animated shapes, use `<canvas>` sprites directly (no synchronous PNG encoding), and cache the offscreen renders per slide — far less main-thread work; sprites shown in the same step share a single forced reflow.
- **Motion & easing**: fly-in now uses `translate3d` with a minimum distance based on shape size (short row bands stay visible) and a smoother `cubic-bezier`; dpr is capped at 2 for consistent pixel grids on high-DPR displays.

# AG-UI 群聊桌面版 1.0.187 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.187 Release Notes (current Windows desktop release)

**版本说明**：演示文稿在线播放的「逐段构建」（要点逐条出现）现在也走「底图 + 形状精灵」真动画——按行带把文本框切成每段一个精灵，逐步显现。
**Version note**: per-paragraph builds (bullets revealed one by one) now also play as real animation via the “base raster + shape sprite” path — the text box is split into one sprite per paragraph by row bands and revealed step by step.

## 逐段构建也走真动画
# Per-paragraph builds as real animation

中文：
- **逐段切精灵**：`bldP build="p"` 的文本框在清单里是同一 spid 的多条目（`paraIndex` = 段序）。前端在取差掩码上按**行带切分**——取 k-1 条最宽的「内部空白行带」作为切分点，恰好切成 k 段，每段一个精灵、各自在自己的步显现（实测每步只多显现一段 `counts=1,2,3,4`）。
- **安全回退**：空白带不够或切出空组 → 回退整形状精灵（绝不产出错位精灵）；服务端先做适配性检查（文本框无填充、无边线），不满足的页仍走分步展开。
- **效果**：要点逐条出现的页不再「硬切」，随段位播飞入 / 淡入等真动画。
English:
- **Paragraph sprites**: a `bldP build="p"` text box appears as multiple entries sharing one spid (`paraIndex` = paragraph order). The viewer splits the diff mask by **row bands** — picking the k-1 widest internal blank bands as cut points — into exactly k sprites, each revealed on its own step (measured: each step reveals exactly one more, `counts=1,2,3,4`).
- **Safe fallback**: too few blank bands or an empty group ⇒ fall back to a whole-shape sprite (never a mis-placed one); the server pre-checks eligibility (no fill, no line), and ineligible slides keep the step expansion.
- **Result**: bullet-by-bullet slides no longer hard-cut — they play fly-in / fade etc. per paragraph.

# AG-UI 群聊桌面版 1.0.186 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.186 Release Notes (current Windows desktop release)

**版本说明**：演示文稿在线播放升级为「底图 + 形状精灵」混合渲染——整形状动画（飞入位移 / 强调 / 退出）现在能真播；逐段构建仍走分步展开。
**Version note**: presentation playback now uses a “base raster + shape sprite” hybrid — whole-shape animations (fly-in motion, emphasis, exit) really play, while per-paragraph builds keep the step expansion.

## 形状级动画（底图 + 精灵）
# Shape-level animation (base raster + sprites)

中文：
- **底图 + 精灵**：只有整形状动画的页，播放稿只输出 1 页「移除被动画形状」的底图；前端把普通页与底图页在形状框内取差得到**透明精灵**，再对精灵播真动画。因为是同一套渲染，**静止时与底图像素级对齐**，也不依赖前端字体。
- **真动画**：飞入（按方向位移）、淡入淡出、旋转、缩放、退出；`<p:transition>` 转场照旧。
- **逐段构建**（要点逐条出现）无法用整形状精灵表达 → 自动回退到已发布的分步展开。
- 实测移除被动画形状后，**形状框之外的版面 0 像素差异**，所以底图与精灵拼接干净。
English:
- **Base raster + sprites**: for slides with whole-shape animations only, the play deck emits a single “base” page with those shapes removed; the viewer diffs the normal page against the base inside each shape's box to build a **transparent sprite**, then plays the real animation on it. Same renderer source ⇒ **pixel-aligned at rest**, no font-metric dependency.
- **Real animations**: fly-in (directional motion), fade in/out, spin, pulse, exit; `<p:transition>` slide transitions unchanged.
- **Per-paragraph builds** can't be expressed as whole-shape sprites → they fall back to the previously shipped step expansion.
- Measured: outside the shape boxes, removing the animated shapes changes **0 pixels**, so base + sprite compositing is clean.

# AG-UI 群聊桌面版 1.0.185 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.185 Release Notes (current Windows desktop release)

**版本说明**：演示文稿在线播放支持转场与逐条动画（无需商业组件）——服务端把分步动画展开成多页 PDF，前端页间转场、页内分步，并支持自动前进。
**Version note**: presentation playback now supports slide transitions and per-step animations (no commercial components) — the server expands build steps into extra PDF pages; the viewer plays transitions between slides and steps within a slide, with auto-advance.

## 演示文稿在线播放：转场 + 逐条动画
# Presentation playback: transitions + step animations

中文：
- **转场**：读每页的 `<p:transition>`（fade / push / wipe / cover / dissolve / zoom… + 方向 / 速度）→ 播放时用 CSS 动画还原。
- **逐条动画**：读 `<p:timing>` 的入场效果，把一页展开成多页（第 j 页只显现前 j 步；逐段要点按段留白占位，版式不跳）；播放时“页内翻页”就是逐条出现。
- **自动前进**：若该页设了 `advanceAfter`，播完本页最后一步后自动翻到下一张。
- **进入即出画面**：点播放先立刻用普通 PDF 出画面，随后异步升级到展开版；全屏依旧生效。
- **如实说明**：位移（飞入轨迹）、退出、强调（旋转/放大/变色）无法用位图表达，退化为“直接出现”；非 .pptx（.ppt/.odp）退化为逐页播放。
English:
- **Transitions**: read each slide's `<p:transition>` (fade / push / wipe / cover / dissolve / zoom…, with direction/speed) and replay it with CSS animations.
- **Step animations**: read entrance effects from `<p:timing>` and expand a slide into several pages (page j shows only the first j steps; per-paragraph builds leave the later paragraphs blank but space-reserving so the layout doesn't shift); “next” inside a slide is the step-by-step reveal.
- **Auto-advance**: if a slide sets `advanceAfter`, playback moves to the next slide automatically after its last step.
- **Instant feedback**: entering play shows the plain PDF immediately, then upgrades to the expanded deck asynchronously; fullscreen still works.
- **Honest limits**: motion paths (fly-in), exits and emphasis (spin/scale/color) can't be expressed with raster pages — they degrade to “just appear”; non-`.pptx` (`.ppt/.odp`) falls back to page-by-page.

# AG-UI 群聊桌面版 1.0.184 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.184 Release Notes (current Windows desktop release)

**版本说明**：代码审核修复（消息多选 / 灯箱 / 表情）——切话题退出多选、按钮按可执行性启用、外部图片下载提示、表情浮层随场景收起。
**Version note**: Review fixes (multi-select / lightbox / emoji) — leaving multi-select on topic switch, eligibility-based buttons, external-image download hint, and dismissing the emoji panel on context changes.

## 审核修复
# Review fixes

中文：
- **切话题退出多选**：切换话题会自动退出多选，避免工具条计数（总数）与实际操作（仅当前话题）不一致。
- **按钮按可执行性启用**：未选中时复制 / 转发 / 删除禁用；删除仅在存在可撤回项（本人 / 管理员且 3 分钟内）时可用。
- **外部图片下载**：图片查看的「下载原图」对站外图片改为新标签打开并提示右键另存（浏览器不允许跨源直接下载）。
- **表情浮层**：切换知聚或发送消息后自动收起。
English:
- **Exit multi-select on topic switch**: switching topics now leaves selection mode, so the toolbar count can't drift from what the actions actually apply to.
- **Eligibility-based buttons**: copy / forward / delete are disabled with nothing selected, and delete is enabled only when a recallable item (your own within 3 minutes, or as admin) is selected.
- **External-image download**: “Download original” opens off-site images in a new tab with a right-click-to-save hint (browsers block cross-origin downloads).
- **Emoji panel**: now dismissed when switching circles or after sending.

# AG-UI 群聊桌面版 1.0.183 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.183 Release Notes (current Windows desktop release)

**版本说明**：界面人性化（第八轮）——消息多选：批量复制 / 转发 / 删除（撤回）。
**Version note**: UI polish (round 8) — message multi-select for batch copy, forward, and delete (recall).

## 消息多选：批量复制 / 转发 / 删除
# Message multi-select: batch copy / forward / delete

中文：
- **进入多选**：聊天页头「☑」进入选择模式，话题栏下方出现工具条；每条消息前出现勾选框，**点消息即选中 / 取消**。
- **批量复制**：把已选消息拼成「发送者: 内容」多行复制到剪贴板。
- **批量转发**：选目标知聚（可搜索）逐条转发文本内容（不含附件）到其主话题。
- **批量删除**：删除即**撤回**（仅限你有权限撤回的：本人发送且 3 分钟内），确认后逐条执行。
- **全选 / 全不选**：一键作用于当前话题已加载的消息；`Esc` 随时退出多选。
English:
- **Enter multi-select**: click “☑” in the chat header to enter selection mode; a toolbar appears under the topic bar and each message gets a checkbox — **click a message to toggle it**. 
- **Batch copy**: joins the selected messages as “sender: content” lines to the clipboard.
- **Batch forward**: pick a target circle (searchable) and forward the text (attachments excluded) into its main topic, one by one.
- **Batch delete**: deleting means **recall** (only for what you may recall: your own within 3 minutes); after confirmation they're recalled one by one.
- **Select all / none**: one click across the loaded messages in the current topic; `Esc` exits multi-select anytime.

# AG-UI 群聊桌面版 1.0.182 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.182 Release Notes (current Windows desktop release)

**版本说明**：界面人性化（第七轮）——搜索弹窗内可直接上下翻页并回车打开、输入区新增表情选择器。
**Version note**: UI polish (round 7) — page through search results inside the dialog and press Enter to open, plus an emoji picker in the composer.

## 搜索弹窗内翻页 + 表情选择器
# In-dialog search paging + emoji picker

中文：
- **弹窗内结果导航**：知聚内搜索后，结果上方出现「‹ i / N ›」，**不用关弹窗**即可逐条翻看（选中行高亮）；也可用 `↑` / `↓` 移动，按**回车**直接打开当前条（关闭弹窗并跳到该消息、标出关键词、出现左下角上下条导航）。
- **表情选择器**：输入区「＋」菜单里新增「😀 表情」，打开常用表情网格，点选即插入到输入框光标处（可连续点选多个），点外部 / `Esc` 收起。
English:
- **In-dialog result navigation**: after searching in a circle, a “‹ i / N ›” bar appears above the results so you can browse them **without closing the dialog** (the selected row is highlighted); `↑` / `↓` also move the selection, and **Enter** opens the current one (closes the dialog, jumps to the message, highlights the keyword, and shows the bottom-left prev/next bar).
- **Emoji picker**: the composer's “＋” menu now has “😀 Emoji”, opening a grid of common emoji; clicking one inserts it at the cursor (you can insert several in a row), and clicking outside or `Esc` dismisses it.

# AG-UI 群聊桌面版 1.0.181 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.181 Release Notes (current Windows desktop release)

**版本说明**：界面人性化（第六轮）——全局搜索跳转后同样高亮并带上下条导航、图片灯箱可旋转与下载原图、支持拖拽上传。
**Version note**: UI polish (round 6) — global search now highlights hits with prev/next nav after jumping, the image lightbox can rotate and download, and files can be drag-dropped to attach.

## 全局搜索一致性 + 灯箱增强 + 拖拽上传
# Consistent global search + lightbox extras + drag-and-drop upload

中文：
- **全局搜索也高亮**：跨知聚全局搜索（🔎）点结果跳转后，目标消息同样标出命中关键词并出现「i / N」上下条导航（与知聚内搜索一致）；修复了此前可能用上一次检索词误高亮的问题。
- **灯箱再增强**：图片放大后新增「旋转 90°」（快捷键 `r`）与「下载原图」（文件名即附件原名）。
- **拖拽上传**：把文件直接拖到聊天区即可加入待发送附件（拖入时显示提示层），与点 📎 选择文件共用同一套校验（最多 9 个、单个 20MB）。
English:
- **Global search highlights too**: after clicking a result from the cross-circle global search (🔎), the target message now highlights the keyword and shows the “i / N” prev/next nav (matching in-circle search); also fixes a case where a stale keyword could be highlighted.
- **Lightbox extras**: viewing an image full-screen now also offers “Rotate 90°” (`r` shortcut) and “Download original” (saved under the attachment's original name).
- **Drag-and-drop upload**: drop files straight onto the chat area to queue them as attachments (a hint layer appears while dragging); shares the same limits as picking files with 📎 (up to 9 files, 20MB each).

# AG-UI 群聊桌面版 1.0.180 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.180 Release Notes (current Windows desktop release)

**版本说明**：界面人性化（第五轮）——修复「更多操作」菜单位置、消息内搜索命中高亮 + 上/下一条导航条、图片灯箱放大。
**Version note**: UI polish (round 5) — fixed the “more actions” menu position, in-message search hit highlighting with a prev/next nav bar, and an image lightbox.

## 搜索命中高亮 + 上/下一条 + 图片灯箱
# Search hit highlighting + prev/next nav + image lightbox

中文：
- **搜索结果高亮**：在知聚内搜索后点某条结果，跳转到的消息里会把命中关键词用黄色标出并滚动到第一处。
- **上/下一条导航条**：命中后聊天区左下角出现「i / N」与上一条 / 下一条 / 关闭，可在多条结果间循环跳转；切知聚或重新搜索时自动收起。
- **图片灯箱**：点消息里的图片即可全屏放大，同一条消息内的图片可左右切换，支持放大 / 缩小 / 重置（滚轮亦可）与放大后拖拽平移，`Esc` 关闭；按住 Ctrl/⌘/Shift 或中键点击仍按原样在新标签打开。
- **修复「⋯」菜单位置**：此前「更多操作」弹出菜单会跑到气泡右缘、看似错位；现改为锚在「⋯」按钮下方（空间不足时向上翻转）。
English:
- **Search hit highlighting**: after searching within a circle and clicking a result, the matched keyword is highlighted in the target message and scrolled to its first occurrence.
- **Prev/next nav bar**: a “i / N” bar with prev / next / close appears at the bottom-left after a hit, cycling through all results; it auto-collapses when you switch circles or start a new search.
- **Image lightbox**: click an image in a message to view it full-screen; images within the same message can be flipped through, with zoom in/out/reset (wheel too) and drag-to-pan when zoomed, and `Esc` to close; Ctrl/⌘/Shift or middle-click still opens a new tab as before.
- **Fixed the “⋯” menu position**: the “more actions” popup used to appear at the bubble's right edge and looked misplaced; it now anchors under the “⋯” button (flipping upward when there's no room below).

# AG-UI 群聊桌面版 1.0.179 发布说明（当前 Windows 桌面版）
# AG-UI Group Chat Desktop 1.0.179 Release Notes (current Windows desktop release)

**版本说明**：界面人性化（第四轮）——消息操作收进「⋯」菜单、话题栏可收起、`?` 快捷键面板、知聚行悬停看跨话题未读、「上次离开后」分隔线。
**Version note**: UI polish (round 4) — message actions tucked into a “⋯” menu, a collapsible topic bar, a `?` shortcuts panel, per-topic unread on circle hover, and a “since you last left” divider.

## 更干净的消息操作 + 话题栏可收起
# Cleaner message actions + collapsible topic bar

中文：
- **「⋯」菜单**：悬停消息时头部只留「停止 / 引用」，新建话题 / 复制 / 重新回答 / 撤回 / 👍👎 收进「⋯」；同一时刻只开一个，点外部或滚动即收起。
- **话题栏收起**：话题栏左侧 ▾ / ▸ 一键收起，只保留当前话题（话题多时省空间），状态按用户记住。
English:
- **“⋯” menu**: hovering a message now shows only Stop / Quote; new-topic / copy / regenerate / recall / 👍👎 move into “⋯” — only one opens at a time, closing on outside click or scroll.
- **Collapsible topic bar**: the ▾ / ▸ toggle on its left collapses it to just the current topic (handy when there are many), remembered per user.

## ? 快捷键面板 + 未读汇总 + 上次离开分隔线
# ? shortcuts panel + unread summary + since-you-left divider

中文：
- **快捷键面板**：不在输入框时按 `?` 弹出，列出 Ctrl/⌘+K、Alt+↑/↓、/、Enter、Esc，以及双击消息引用、←/→ 调侧栏宽度；帮助弹窗底部也有入口。
- **跨话题未读**：悬停知聚行即可看到各话题未读数（如「# 综合：3」）。
- **「上次离开后」分隔线**：进入知聚时，在您上次离开后产生的新消息前显示一条淡色分隔线（与「以下是新消息」重合时不重复）。
English:
- **Shortcuts panel**: press `?` outside an input to see Ctrl/⌘+K, Alt+↑/↓, /, Enter, Esc, plus double-click-to-quote and ←/→ panel nudging; also reachable from the help dialog.
- **Per-topic unread**: hovering a circle row shows each topic's unread count (e.g. “# General: 3”).
- **“Since you last left” divider**: entering a circle shows a faint divider before the messages that arrived after you last left it (not duplicated when it coincides with “New messages below”).

# AG-UI 群聊桌面版 1.0.178 发布说明
# AG-UI Group Chat Desktop 1.0.178 Release Notes

**版本说明**：界面布局与操作人性化（第三轮）——未读分隔线、记住滚动位置、侧栏一键收起、双击引用回复、切群后自动聚焦输入框（可关）。
**Version note**: layout & interaction polish (round 3) — an unread divider, remembered scroll position per circle, one-click panel collapse, double-click to quote, and auto-focus the composer after switching (toggleable).

## 未读分隔线 + 记住滚动位置
# Unread divider + remembered scroll position

中文：
- **「以下是新消息」**：进入有未读的知聚时，在该话题首条未读前显示分隔线；读到即消失。分隔线与消息同处一行测量，不影响滚动定位。
- **记住到过的地方**：切走前若你正停在中间某处，切回来就回到原处（贴底时仍然直接到最新）。
English:
- **“New messages below”**: entering a circle with unread messages shows a divider before the first unread one; it disappears once read. It is measured with its message row, so scrolling stays accurate.
- **Remembered position**: if you were scrolled up when you left a circle, returning takes you back there (if you were at the bottom, you still land on the latest).

## 侧栏一键收起 + 双击引用 + 自动聚焦
# One-click panel collapse + double-click to quote + auto-focus

中文：
- **一键收起**：聊天页头 <code>«</code> / <code>»</code> 收起 / 展开左 / 右侧栏，收起后聊天区变宽，状态被记住。
- **双击引用**：双击消息（未选中文字时）即引用回复；双击选词不误触。
- **切群后聚焦输入框**：默认开启（「修改资料」里可关）；触屏设备不生效，避免自动弹键盘。
- **顺带修复**：点「↓ 到最新」在某些情况下没反应（虚拟滚动早退路径不执行贴底）——已修复。
English:
- **One-click collapse**: the <code>«</code> / <code>»</code> buttons in the chat header collapse / expand the left / right panel; the chat area widens and the state is remembered.
- **Double-click to quote**: double-click a message (with no text selected) to quote it; double-clicking to select a word won't misfire.
- **Focus the composer after switching**: on by default, toggleable in “Edit profile”; not applied on touch devices so the keyboard doesn't pop up.
- **Also fixed**: the “↓ latest” button could do nothing in some cases (the virtual-scroll early-return path skipped sticking to the bottom).

# AG-UI 群聊桌面版 1.0.177 发布说明
# AG-UI Group Chat Desktop 1.0.177 Release Notes

**版本说明**：界面布局人性化——左侧知聚列表与右侧成员列表的宽度现在可以拖拽（与输入区拖拽同一套交互），按用户记忆，长名称不再被截断。
**Version note**: layout usability — the left circle list and right member list are now **resizable by dragging** (same interaction as the composer splitter), remembered per user, so long names are no longer truncated.

## 侧栏宽度可拖拽
# Resizable side panels

中文：
- **拖动改宽**：左 / 右栏边缘各有一条拖拽手柄（悬停高亮），拖动即改宽，聊天区相应伸缩。
- **按用户记住**：宽度存到本机（每个账号独立），刷新 / 下次登录仍生效；登出后换账号不串。
- **不挤坏聊天区**：聊天区不低于 360px；窗口变窄时两侧栏自动各让一半。
- **键盘与复位**：手柄可 Tab 聚焦，`←/→` 微调 8px；**双击手柄复位**默认宽度。
- 窄屏（≤900px）侧栏为抽屉，手柄自动隐藏。
English:
- **Drag to resize**: each side panel edge has a drag handle (highlights on hover); dragging resizes it and the chat area follows.
- **Remembered per user**: the width is stored locally per account and restored on reload / next login; it does not leak across accounts after sign-out.
- **Chat never squeezed**: the chat area keeps at least 360px; on window shrink both panels give way automatically.
- **Keyboard & reset**: the handle is focusable, `← / →` nudges by 8px, and **double-click resets** to the default width.
- On narrow screens (≤900px) the panels become drawers and the handles hide.

# AG-UI 群聊桌面版 1.0.176 发布说明
# AG-UI Group Chat Desktop 1.0.176 Release Notes

**版本说明**：对「执行过程分层」做一次严格审核后的修复——撤回 / 中断重置时不再残留过时或空白的「执行过程」块，并清理折叠状态表避免长会话下无界增长。
**Version note**: bug fixes after a strict review of the process/final-answer layering — recalled or reset replies no longer leave a stale or empty “Execution process” block, and the collapse-state maps are pruned to avoid unbounded growth in long sessions.

## 过程分层：边界路径修复
# Process layering: edge-path fixes

中文：
- **撤回消息**：撤回后「执行过程」块（思考 / 计划 / 链 / 工具）一并隐藏，不再残留过时过程。
- **中断重置**（人机交互中断）：清空思考后若过程体已空，连同「执行过程」标题一并移除，不留空壳。
- **折叠状态表清理**：消息超出内存上限被裁剪、或登出 / 切换身份时，同步清理按消息记录的折叠覆盖，避免长时间会话 / 多次登录下无界增长。
English:
- **Recalled messages**: recalling a message now also hides its process block (thinking / plan / chain / tools) instead of leaving a stale one behind.
- **Reset on interruption** (human-in-the-loop): if clearing the thinking leaves the process body empty, the whole block is removed — no hollow header.
- **Collapse-state pruning**: the per-message collapse overrides are now cleared when old messages are trimmed or on sign-out / identity switch, avoiding unbounded growth across long sessions or repeated logins.

# AG-UI 群聊桌面版 1.0.175 发布说明
# AG-UI Group Chat Desktop 1.0.175 Release Notes

**版本说明**：微调 1.0.174 的分层默认行为——「执行过程」块改为**执行结束自动收起**（与计划卡一致），最终答复始终优先可见；流式中仍实时展开，手动展开过则保持。
**Version note**: a small follow-up to 1.0.174 — the “Execution process” block now **auto-collapses when the run finishes** (matching the plan card), so the final answer stays front and center; it still expands live while streaming and keeps your manual choice.

## 过程块默认收起
# Process block auto-collapses

中文：
- **结束即收起**：数字员工回复结束后，「执行过程」块默认收起，只留标题（如 `▶ 执行过程 · 3/5`）+ 最终答复；想回看过程点一下展开。
- **流式中仍展开**：思考 / 工具执行时实时可见，不遮挡过程。
- **手动优先**：只要您点过标题，就以您的选择为准（重渲染 / 滚动重绘不被程序性收起覆盖）。
English:
- **Collapse on finish**: once a reply ends, the block collapses to just its header (`▶ Execution process · 3/5`) plus the final answer; one click re-opens the process.
- **Still expands while streaming**: thinking and tool execution stay visible in real time.
- **Your choice wins**: once you click the header, your state is kept (programmatic collapse won't override it on re-render).

# AG-UI 群聊桌面版 1.0.174 发布说明
# AG-UI Group Chat Desktop 1.0.174 Release Notes

**版本说明**：本版把数字员工回复里的「过程」与「最终答复」分层——思考 / 执行计划 / 技能调用链 / 工具调用归入一个可折叠的「执行过程」块，置于最终答复之上，默认展开、可一键收起，读起来一眼分得清“过程”与“结论”。
**Version note**: this release layers a digital employee's reply into "process" and "final answer": thinking / plan / skill-call chain / tool calls are grouped into one collapsible “Execution process” block above the final answer — expanded by default, one click to collapse.

## 执行过程 / 最终答复分层
# Process / final-answer layering

中文：
- **过去的问题**：思考块在正文上方，计划卡 / 调用链 / 工具行在正文下方，最终答复被夹在中间，分不清哪句是结论。
- **现在**：四类过程块统一收进 `.process`（标题 `▶ 执行过程`，有计划时显示 `· 3/5` 步数进度），置于正文之上；左侧细竖线 + 缩进与正文区分；**默认展开**，点标题一键收起，手动选择按消息记住（滚动重绘 / 计划推进不被覆盖）。
- **无过程则不打扰**：没有思考 / 计划 / 链路 / 工具的回复不会多出这个块；已撤回消息不渲染。
- **流式也对齐**：工具调用比思考先到时，过程容器会即时补出并把已有的思考块收编进来，不会出现“思考在外、工具在内”的错层。
English:
- **The old problem**: thinking sat above the answer while the plan / chain / tool rows sat below it, sandwiching the final answer so it was hard to tell which line was the conclusion.
- **Now**: all four process blocks live in one `.process` (header `▶ Execution process`, with a `· 3/5` step counter when a plan exists) placed above the answer, set off by a thin left rule and indent; **expanded by default**, one click to collapse, with your choice remembered per message (kept across scroll repaints / plan progress).
- **No process, no clutter**: replies without thinking / plan / chain / tools don't get the block; recalled messages don't render it.
- **Streaming stays aligned**: when a tool call arrives before any process content, the container is created on the fly and adopts the existing thinking block, so you never see the thinking outside but the tools inside.

# AG-UI 群聊桌面版 1.0.173 发布说明
# AG-UI Group Chat Desktop 1.0.173 Release Notes

**版本说明**：本版继续**界面与操作体验的人性化优化**（第二批）：消息日期标签、禁用按钮给出原因、发送失败可重试、通知可按知聚静音、成员栏更清爽、帮助可搜索并新增「概念速查」。
**Version note**: a second batch of **UI/UX humanization**: message date labels, reasons on disabled buttons, retry on failed send, per-circle notification mute, a cleaner member bar, and a searchable help with a glossary.

## 消息更易读 + 失败可重试
# More readable messages + retry on failure

中文：
- **日期标签**：每天第一条消息的时间前带「今天 / 昨天 / 具体日期」，跨天回看不再需要猜时间。
- **发送失败重试**：断线或竞态导致消息没发出去时，输入内容与附件**保留**，底部出现警示条给出「重试 / 取消」——不再只靠一闪而过的提示。
- **禁用按钮给出原因**：未进入知聚时，置灰的按钮悬浮/读屏会说明「请先进入一个知聚」，不再“只灰不说”。
English:
- **Date labels**: the first message of each day shows “Today / Yesterday / date” before its time, so cross-day reading needs no guessing.
- **Retry on failure**: if a send is dropped (disconnect / race), the text and attachments are kept and a warning bar offers “Retry / Cancel” instead of a fleeting toast.
- **Reasons on disabled buttons**: buttons grayed out before entering a circle now explain “Enter a circle first” on hover and to screen readers.

## 通知静音 + 成员栏更清爽 + 帮助可搜索
# Per-circle mute + cleaner member bar + searchable help

中文：
- **按知聚静音**：知聚行 🔔/🔕 开关，被静音的知聚不再产生应用内通知与桌面通知（本地偏好，按用户保存）。
- **成员栏信息密度**：操作按钮（触发方式 / 移除）默认隐没，悬停或键盘聚焦才显现；触屏设备保持常显。
- **帮助可搜索 + 概念速查**：帮助弹窗新增搜索框（逐条过滤、无命中给提示）与「🔎 概念速查」——一次看懂知聚 / 数字员工 / 技能 / 记忆 / 记忆特征 / 组织架构 / 图库知识库 / 输出物 / 本机桥 / 客服知聚。
English:
- **Per-circle mute**: a 🔔/🔕 toggle on each circle row; muted circles stop producing in-app and desktop notifications (a local, per-user preference).
- **Member-bar density**: action buttons (trigger mode / remove) stay hidden until hover or keyboard focus; touch devices keep them always visible.
- **Searchable help + glossary**: the help dialog gains a search box (per-entry filtering with a no-match hint) and a “🔎 Glossary” covering circle / digital employee / skill / memory / memory trait / org structure / image & knowledge base / outputs / local bridge / support circle.

# AG-UI 群聊桌面版 1.0.172 发布说明
# AG-UI Group Chat Desktop 1.0.172 Release Notes

**版本说明**：本版是一次**界面与操作体验的人性化优化**（第一批）：修中文输入法回车误发、窄屏抽屉、输入区「＋」菜单、按群草稿、跳到底部按钮、弹窗焦点管理、全局快捷键、无障碍名与空态引导。
**Version note**: a first batch of **UI/UX humanization**: IME-safe Enter, a narrow-screen drawer, a composer “＋” menu, per-circle drafts, a jump-to-latest button, modal focus management, keyboard shortcuts, accessible names and empty-state hints.

## 中文输入法不再误发 + 窄屏可用
# IME-safe Enter + usable narrow screens

中文：
- **输入法回车误发（P0）**：`#input` 的 Enter 现在判断 `isComposing` / keyCode 229 —— 拼音选词回车不再把半句话发出去。
- **窄屏抽屉（P0）**：≤900px 时两侧栏不再直接 `display:none`（那样**根本无法切知聚**），改为聊天头 `☰`/`👥` 打开左/右抽屉，点遮罩 / 选群 / Esc 收起。
English:
- **IME Enter**: the composer checks `isComposing` / keyCode 229, so confirming a pinyin candidate no longer sends a half-finished line.
- **Narrow-screen drawer**: ≤900px no longer hides both sidebars (`display:none`, which made switching circles impossible) but opens them as ☰ / 👥 drawers with a backdrop; close on backdrop / picking a circle / Esc.

## 输入区「＋」菜单 / 按群草稿 / 跳到底部
# Composer “＋” menu / per-circle drafts / jump-to-latest

中文：
- **「＋」菜单**：附件 / 语音 / 画布收进 `＋` 弹出菜单，主行只留 ＋ / 讨论 / 发送，图标密度下降。
- **按群草稿**：输入实时存 `agui.draft.<用户>.<群>`，切群 / 刷新 / 重连不丢，发送即清。
- **「↓ 到最新 / N 条新消息」**：上滑离开底部时出现并累计新消息计数；点一下贴底归零。
English:
- **“＋” menu**: attachment / voice / canvas move into a `＋` popover; the main row keeps only ＋ / discuss / send.
- **Per-circle drafts**: input is persisted to `agui.draft.<user>.<circle>`, surviving circle switches / reloads / reconnects, cleared on send.
- **“↓ latest / N new messages”**: appears when you scroll away from the bottom and counts incoming messages; one click returns to the bottom.

## 键盘、焦点与无障碍
# Keyboard, focus and accessibility

中文：
- **弹窗焦点管理**：打开自动把焦点移入弹窗、关闭归还、`Tab` 困在最上层弹窗内。
- **全局快捷键**：`Ctrl/⌘+K` 搜索（群内 / 全局自适应）、`/` 聚焦输入框、`Alt+↑/↓` 快速切知聚。
- **无障碍名**：动态生成的消息操作按钮（回复 / 复制 / 重新回答 / 撤回 / 停止 / 新建话题）补齐 `aria-label`。
- **空态引导**：空知聚提示追加一句操作引导。
English:
- **Modal focus**: focus moves into the modal on open, is restored on close, and `Tab` is trapped inside the topmost modal.
- **Shortcuts**: `Ctrl/⌘+K` search (scoped adaptively), `/` focus the input, `Alt+↑/↓` switch circles.
- **Accessible names**: dynamically rendered message actions (reply / copy / regenerate / recall / stop / new-topic) now carry `aria-label`.
- **Empty state**: the empty-circle hint gains an actionable tip.

## 回归
# Tests

中文：新增浏览器 E2E `tools/ui-ux-polish.mjs` **16/16**（＋菜单 / 草稿 / 输入法 / 抽屉 / 弹窗焦点）；回归 `ui-group-files` 19/19、`ui-plan-collapse` 11/11、`ui-doc-slides` 23/23、`ui-doc-preview` 19/19；i18n 审计 `zh=en=1469，missing 0`。
English: new browser E2E `tools/ui-ux-polish.mjs` **16/16**; regressions `ui-group-files` 19/19, `ui-plan-collapse` 11/11, `ui-doc-slides` 23/23, `ui-doc-preview` 19/19; i18n audit `zh=en=1469, missing 0`.

---

# AG-UI 群聊桌面版 1.0.171 发布说明（上一版）
# AG-UI Group Chat Desktop 1.0.171 Release Notes (previous release)

**版本说明**：本版三件事——① 新增「📁 输出物」：集中查看本知聚里**所有附件**并下载 / 预览；② 「执行计划」卡片可**收缩 / 展开**（默认未完成展开、全部完成收起，手动选择优先）；③ **保留数字员工的“过程”**：审批 / 交互中断时不再清空已流式产出的正文。
**Version note**: three things — (1) a new **“📁 Outputs”** panel listing **every attachment in the circle** with download / preview; (2) the **plan card can collapse / expand** (default: expanded while running, collapsed once done; a manual choice wins); (3) **the agent's “process” is kept**: the streamed text is no longer wiped on an approval/interaction interrupt.

## 保留数字员工的「过程」（审批中断不再清空）
# Keep the agent's process (no more wipe on approval interrupt)

中文：
- **现场**：在「与 项目总监 的单聊」里，一轮编排跑完后只看到“最后一次的回复”，之前的自检 / 改稿 / 质检过程全不见了。
- **根因**：人机交互（审批卡）中断时网关调用 `ResetAgentContentAsync` **清空已流式产出的正文与思考**（库 + 前端一起清，注释写的意图是“保持决策前正文为空”），恢复后只把最终结果追加进去。
- **修复**：审批 / 交互中断**不再清空**——把“过程”留在消息里，决策后同一消息**继续追加**最终结果。仅模型**可重试错误重试**时仍清空半截输出（避免重试内容拼接重复）。另修**收尾剥壳**：整段是「过程叙述 + 协调 JSON」时不再被替换成只剩 `answer`（保留 JSON 之前的过程）。
- **实测**：对同一请求重跑（日志确认 `交付物兜底触发交互中断` 已发生），结束后消息从**过程第一句**开始（之前只剩中断后的片段）。

English:
- **Scene**: in the “与 项目总监” direct chat, a coordinator run showed only the last reply; the earlier self-check / rewrite / review process was gone.
- **Cause**: on an approval-card interrupt the gateway called `ResetAgentContentAsync`, which wiped the streamed text and reasoning (store + frontend), then only appended the final result.
- **Fix**: interrupts **no longer wipe** — the process stays on the message and the final result is appended to the same message after the decision. Only a **retryable model-error retry** still clears the half output (to avoid duplicated concatenation). Also fixed the close-out peel so “process + coordination JSON” keeps the process narration before the JSON.
- **Real run**: rerunning the same request (logs confirm an interrupt fired) leaves the message starting at the process's **first line**, not the post-interrupt tail.

## 📁 输出物：本知聚全部附件一览
# 📁 Outputs: every attachment in the circle

中文：聊天头部「📁」打开汇总弹窗，列出本知聚**所有可见消息里的附件**（服务端汇总 `GET /ag-ui/group/{id}/attachments`：按时间倒序、按附件 ID 去重、过滤已撤回 / 不可见），每行可**直接下载**或（办公文档 / PDF）**在线查看**（复用现有阅读器 / 幻灯片查看器），顶部可按文件名筛选。
English: the chat header “📁” opens a summary dialog listing **every attachment across the circle's visible messages** (server-aggregated `GET /ag-ui/group/{id}/attachments`: newest first, de-duplicated by attachment id, recalled / invisible messages filtered); each row is **directly downloadable** or (office docs / PDF) **viewable online** (reusing the existing reader / slide viewer), with a name filter on top.

## 执行计划卡片：收缩 / 展开
# Plan card: collapse / expand

中文：计划卡头部多一个 `⌄/›` 箭头：**默认未执行完展开、全部完成收起**；用户手动点过箭头则以其选择为准（重渲染后保持）。收起后只留标题一行 + 进度文本。
English: the plan card head gains a `⌄/›` caret: **expanded while running, collapsed once all steps are done**; a manual choice wins and sticks across re-renders. Collapsed keeps just the title line plus the progress text.

## 回归
# Tests

中文：C# 相关面 **386 通过 / 0 失败**（含新增 `EndMessage_CoordinationJson_KeepsProcessPrefix`、输出物端点「列出全部 + 撤回后不出现 + 匿名 401」）；浏览器 E2E：新增 `ui-group-files.mjs` **19/19**、`ui-plan-collapse.mjs` **11/11**，回归 `ui-doc-slides.mjs` **23/23**、`ui-doc-preview.mjs` **19/19**；i18n 审计 `zh=en=1460，missing 0`。
English: **386 passed / 0 failed** across the affected C# suites (including the new `EndMessage_CoordinationJson_KeepsProcessPrefix` and the outputs endpoint cases); browser E2E: new `ui-group-files.mjs` **19/19** and `ui-plan-collapse.mjs` **11/11**, regressions `ui-doc-slides.mjs` **23/23** and `ui-doc-preview.mjs` **19/19**; i18n audit `zh=en=1460, missing 0`.

---

# AG-UI 群聊桌面版 1.0.170 发布说明（上一版）
# AG-UI Group Chat Desktop 1.0.170 Release Notes (previous release)

**版本说明**：1.0.170 把演示文稿的「在线播放」做成**真·全屏**：幻灯片铺满整块屏幕（仅按比例留黑边），控制条静置自动隐没；以前它只是“铺满浏览器视口里的一个小框”（还受 `max-width/max-height` 限制），且从不请求浏览器全屏。
**Version note**: 1.0.170 makes presentation playback **truly fullscreen**: the slide fills the whole screen (letterboxed only for aspect mismatch) and the control bar auto-hides. Before, it merely filled a small box inside the viewport (clamped by `max-width/max-height`) and never requested browser fullscreen.

## 播放满屏（1.0.170）
# Fullscreen playback (1.0.170)

中文：
- **真全屏**：点「▶ 播放」时调用浏览器 Fullscreen API（桌面版 WebView2 同样支持）；请求失败也不影响——覆盖层本身 `position:fixed; inset:0` 已铺满视口，只是浏览器还留边框。
- **铺满屏幕**：删除 `.doc-play-canvas` 上的 `max-width:92vw / max-height:82vh` 限制（这正是“填不满”的直接原因），画布按 `window.innerWidth × window.innerHeight` 等比铺满；比例不符时另一方向留黑边属正常。
- **控制条自动隐没**：播放中控制条（◀ 页码 ▶ / 备注 / 退出）静置 3 秒后淡出、鼠标移动再现，让幻灯片真正占满屏幕；进出全屏导致的视口变化会重画一帧。`Esc`（或浏览器 F11/退出全屏）统一收尾：只退播放、不关弹窗。
- **回归**：浏览器 E2E `tools/ui-doc-slides.mjs`（`deviceScaleFactor=2`）新增“播放满屏（限制方向铺满视口）”“进入浏览器全屏”“控制条静置后自动隐没”三项，**23 项全过**；`ui-doc-preview.mjs` **19 项**仍全过。

English:
- **True fullscreen**: 「▶ Play」now calls the browser Fullscreen API (WebView2 supports it too); if the request fails it still works — the overlay is `position:fixed; inset:0`, so only browser chrome differs.
- **Fills the screen**: removed the `max-width:92vw / max-height:82vh` clamp on `.doc-play-canvas` (the direct cause of “not filling”), and the canvas is fitted to `window.innerWidth × window.innerHeight` preserving aspect; black bars on the other axis for aspect mismatch are expected.
- **Auto-hiding controls**: the play bar (◀ page ▶ / notes / exit) fades out after 3 s of no mouse movement and returns on move; viewport changes from entering/leaving fullscreen trigger a re-render. `Esc` (or the browser leaving fullscreen) exits playback only, never the modal.
- **Tests**: the browser E2E `tools/ui-doc-slides.mjs` (`deviceScaleFactor=2`) adds “playback fills the viewport”, “entered browser fullscreen”, and “control bar auto-hides after idle”, now **23/23**; `ui-doc-preview.mjs` still passes **19/19**.

---

# AG-UI 群聊桌面版 1.0.169 发布说明（上一版）
# AG-UI Group Chat Desktop 1.0.169 Release Notes (previous release)

**版本说明**：本版把**演示文稿的在线查看**从“只能看幻灯片”升级为**能看备注、能播放**：PPT / PPTX 改用自绘幻灯片查看器，**备注跟随当前页显示在下方**，并可点「▶ 播放」进入**全屏播放模式**（方向键 / 空格翻页、`N` 开关备注、`Esc` 退出）。顺带把「👁 在线查看」按钮改为仅图标，并修掉一个高分屏下“幻灯片只显示一半宽高”的缺陷。
**Version note**: this release turns **presentation online viewing** from “slides only” into **notes + playback**: PPT / PPTX now use a self-drawn slide viewer with **per-slide speaker notes shown below the current page**, plus a **fullscreen playback mode** (arrow / space to navigate, `N` for notes, `Esc` to exit). It also makes the “👁 view online” button icon-only and fixes a high-DPI bug where the slide rendered at half width and half height.

## 演示文稿在线查看：备注跟随当前页 + 全屏播放
# Presentation preview: per-slide notes + fullscreen playback

中文：
- **为什么改**：浏览器内置 PDF 阅读器是黑盒，拿不到“当前第几页”，所以既没法把备注对到当前页、也没法做播放。演示文稿因此改用**自绘幻灯片查看器**（内置 PDF.js 把服务端转好的 PDF 逐页画到 canvas）；docx / xlsx / pdf 等仍走原来的内置阅读器（零回归）。
- **备注跟随当前页**：新增 `GET /ag-ui/preview/{id}/notes`，服务端从 `.pptx` 的 `ppt/notesSlides` 按幻灯片顺序抽取备注正文（只取备注正文占位符，避开页码），前端在第 i 页下方显示第 i 页备注，翻页即切换。
- **在线播放模式**：弹窗底部「◀ 页码 ▶」翻页 + 「▶ 播放」进入全屏黑底幻灯片；方向键 / 空格 / PageUp·PageDown 翻页，`N` 切换备注浮层，`Esc` 只退播放、不关弹窗。
- **优雅降级**：`.ppt`（二进制）/ `.odp` 抽不到备注，仍可正常播放，只是不显示备注区；服务端未装 LibreOffice 时一如既往降级为 503。
- **依赖**：新增 vendored `PDF.js`（`wwwroot/vendor/pdfjs`，同源加载，符合现有 CSP）。另保留 `?notes=true`（LibreOffice `ExportNotesPages` 备注页 PDF）为 API 选项。
- **缺陷修复（高分屏）**：画布后备缓冲按 DPR 分配、但渲染变换误设为单位矩阵，导致 DPR=2 屏幕上幻灯片只画进左上角 1/4（看起来只有一半宽高）。已改为 `setTransform(dpr,0,0,dpr,0,0)`。
- **界面小改**：附件卡片的「👁 在线查看」按钮只保留 `👁` 图标（保留悬浮提示 + 补 `aria-label`）。

English:
- **Why**: the built-in PDF viewer is a black box with no “current page” signal, so it can neither line notes up with the current slide nor do playback. Presentations therefore use a **self-drawn slide viewer** (bundled PDF.js renders the server-converted PDF page by page onto a canvas); docx / xlsx / pdf keep the old built-in reader (no regression).
- **Per-slide notes**: new `GET /ag-ui/preview/{id}/notes` extracts speaker notes in slide order from the `.pptx` `notesSlides` (body placeholder only, skipping the page-number field); the viewer shows page *i*'s notes below page *i* and swaps them on navigation.
- **Playback mode**: a 「◀ page ▶」 bar plus 「▶ Play」 enters a fullscreen black slide view; arrow / space / PageUp·PageDown navigate, `N` toggles the notes overlay, `Esc` exits playback only (the modal stays).
- **Graceful degradation**: `.ppt` (binary) / `.odp` yield no notes but still play; the usual 503 fallback when LibreOffice is absent is unchanged.
- **Dependency**: vendored `PDF.js` (`wwwroot/vendor/pdfjs`, same-origin, fits the current CSP). `?notes=true` (LibreOffice `ExportNotesPages` notes-pages PDF) is kept as an API option.
- **High-DPI fix**: the canvas backing store was sized by DPR while the render transform was left as identity, so at DPR=2 the slide drew into the top-left quarter (appearing half width and half height). Now `setTransform(dpr,0,0,dpr,0,0)`.
- **UI tweak**: the attachment “👁 view online” button is icon-only now (tooltip kept, `aria-label` added).

## 回归
# Tests

中文：新增 `PresentationNotesTests`（备注按幻灯片顺序抽取 / 跳过页码占位符 / 无备注返回空串 / 非 pptx 与损坏包返回 null）；`DocPreview` 增加“`?notes=true` 走独立缓存”“`/notes` 端点返回逐页备注”“匿名 401”。C# 相关 **73 通过 / 0 失败**。新增浏览器 E2E `tools/ui-doc-slides.mjs`（在 **deviceScaleFactor=2** 下跑）：**20 项全过**，含“幻灯片铺满画布”回归（只画 1/4 会变红）；原有 `ui-doc-preview.mjs` **19 项**仍全过。
English: added `PresentationNotesTests` (order / placeholder skip / empty notes / non-pptx and corrupt → null), plus `DocPreview` cases for the `?notes=true` cache variant, the `/notes` endpoint, and anonymous 401. **73 passed / 0 failed** in C#. New browser E2E `tools/ui-doc-slides.mjs` (run at **deviceScaleFactor=2**): **20/20**, including the “slide fills the canvas” regression (fails if it draws only a quarter); the existing `ui-doc-preview.mjs` still passes **19/19**.

---

# AG-UI 群聊桌面版 1.0.168 发布说明（上一版）
# AG-UI Group Chat Desktop 1.0.168 Release Notes (previous release)

**版本说明**：1.0.168 是 1.0.167 的**收尾加固版**。1.0.167 用「心跳 + 抬高空闲阈值」把长任务从孤儿流兜底手里救了出来，但同一条根因链上还有几处没修干净：兜底文案在流被回收后必然写不进去、桥接与计划阶段仍无心跳、终止/交互超限两条收尾路径不挂产物、心跳本身还有并发“复活”隐患。本版把审核出的 P1/P2 全部收口，并把当时“假通过”的测试改成真正会红的回归。
**Version note**: 1.0.168 hardens 1.0.167. That release saved long runs from the orphan-stream reaper via a heartbeat and a higher idle threshold, but several links of the same root-cause chain were left open: the empty-reply fallback could never be written once the stream was reclaimed, the bridge and plan phases still had no heartbeat, the terminate and interaction-limit paths attached no products, and the heartbeat itself still had a concurrent “resurrect” hazard. This release closes every P1/P2 item from the audit and turns the previously “falsely-passing” tests into real regressions.

## 空正文兜底不再依赖“流还开着”（空气泡的最后一环）
# The empty-reply fallback no longer needs a live stream (the last link of the empty-bubble chain)

中文：
- **根因**：收尾路径（崩溃 / 取消 / 终止 / 交互 TTL 清理）明知自己可能跑在**流已被回收之后**，却一直用 `AppendAgentContentAsync` 补那句人话 —— 它硬性要求流在线，于是异常被 `catch → LogDebug` 吞掉，用户最终仍只看到一只空气泡（实测那条消息 `content` 就是 0，而不是兜底文案）。
- **修复**：新增 `GroupHub.StampMessageContentIfEmptyAsync` —— 把“判空（含防抖缓冲）+ 写入 + 丢弃空快照”合并到锁内原子完成，流在线时顺带广播 `TEXT_MESSAGE_CONTENT`，流已回收则只改库；`TryStampFallbackIfEmptyAsync` 改走它，写入失败从 **Debug 提到 Warning**。
- **同类收口**：`TerminateResumedRunAsync` 回挂产物时补上 `allowAfterEnd: true`（注释承诺了“产物仍附在本条消息上”，代码却漏了）；交互轮数超限终止路径**此前完全不挂产物**，现在也尽力回挂（靠正文里的 `att_` 引用找回；`produce_file` 类受 AsyncLocal 已结束所限仍可能找不回，属已记录局限）。

English:
- **Root cause**: the finalizers (crash / cancel / terminate / interaction-TTL sweep) knew they could run *after* the stream was reclaimed, yet still stamped their human-readable note via `AppendAgentContentAsync`, which hard-requires a live stream — so the exception was swallowed at `catch → LogDebug` and the user still saw a bare bubble (the measured message had `content = 0`, not the fallback note).
- **Fix**: new `GroupHub.StampMessageContentIfEmptyAsync` does “check-empty (including the debounce buffer) + write + drop the empty snapshot” atomically under the lock, broadcasting `TEXT_MESSAGE_CONTENT` when the stream is live and only touching the DB when it is gone; `TryStampFallbackIfEmptyAsync` now uses it, and a failed stamp is logged at **Warning** instead of Debug. The same sweep adds `allowAfterEnd: true` to `TerminateResumedRunAsync` (whose comment promised the products stay on the message) and makes the interaction-limit termination attempt a reattach at all (recovered via `att_` references in the body; `produce_file` products can still be unrecoverable because the AsyncLocal collector has ended — a recorded limitation).

## 长任务在每个阶段都有心跳（桥接 / 计划 / 批量本机执行）
# Heartbeats in every phase of a long run (bridge / plan / batch local exec)

中文：桥接两条接收循环（`InvokeBridgeAsync` / `ResumeBridgeStreamAsync`）、协调计划的逐步循环、以及等待用户在本机批量执行客户端技能的阶段，此前都不刷活跃度 —— 外部 AG-UI 服务或计划阶段可能大段时间不吐正文，仍会被孤儿流兜底误判为空闲。现在这几处统一调 `TouchAgentStream`；`BroadcastMessagePlanAsync` 命中存活流时也顺手刷新（计划卡本就是“运行仍在进行”的可见信号）。
English: the two bridge receive loops (`InvokeBridgeAsync` / `ResumeBridgeStreamAsync`), the coordinated-plan step loop, and the wait for the user to run batch client skills locally never refreshed activity — an external AG-UI service or plan phase can be silent for minutes and still be misjudged as idle. All of these now call `TouchAgentStream`, and `BroadcastMessagePlanAsync` refreshes too when it hits a live stream (a plan card is itself a visible “run is alive” signal).

## 心跳不再“复活”已结束的流（并发收口）
# A heartbeat never “resurrects” an ended stream (concurrency)

中文：`TouchAgentStream` 原先在锁外做「读-改-写」`_agentStreams`，与 `EndAgentMessageAsync` 的移除交错时，会把一条**已广播 END** 的流重新插回字典，随后的正文增量就追加到已结束的消息上。现在 `TouchAgentStream` 与 `End` 的移除共用同一把 `_pendingLock`，读-改-写原子完成。另：桥接附件回灌两处改走“不要求流在线”的挂载通道（流在时行为不变，流已回收也不丢文件）。
English: `TouchAgentStream` used to read-modify-write `_agentStreams` outside the lock; interleaving with `EndAgentMessageAsync`'s removal could reinsert a stream that had already broadcast END, so later content deltas landed on an ended message. `TouchAgentStream` and the `End` removal now share `_pendingLock`, making the read-modify-write atomic. The two bridge attachment reflow sites also move to the durable “no live stream required” attach channel (identical when live, lossless when reclaimed).

## 失败不再静默 + 计数不再虚报
# No more silent failures, no more inflated counts

中文：孤儿流收尾失败从 **Debug 提到 Warning**（这条以前完全看不到，正是“查不出来”的原因）；三处产物计数改为按挂载返回值累加（流式挂载无返回值、结束后挂载返回 `bool`，重复/消息不存在应为 false，不再把失败算成成功）。顺手修掉 `AgentGateway` 里一处改坏的 XML 文档（重复 `<param>` + 游离 `</summary>`）。
English: orphan-stream cleanup failures are logged at **Warning** (formerly invisible, the reason these were so hard to diagnose); three product counters now accumulate by the attach return value (the streaming attach returns void; the post-end attach returns `bool`), so duplicates/missing messages no longer count as successes. A broken XML doc block in `AgentGateway` (duplicate `<param>` + stray `</summary>`) is fixed too.

## 回归
# Tests

中文：把审核指出的“假通过”用例改成真正会红的回归 —— ①`OrphanReaper` 用例调整为先累计空闲、再扫描、后心跳的顺序，空闲阈值一旦被改回 60s 第一次扫描就会误杀、用例立刻变红；②`SnapshotApprovals` 用例改为**非空**快照（空表快照无论拷贝还是别名都会通过，是恒真的）；③新增 4 例 `StampMessageContentIfEmptyAsync`（流已回收仍写库 / 不覆盖已有正文 / 空文案不写 / 流在线时广播），④保留 `TouchAgentStream` 不复活、流已回收可挂附件等用例。相关测试类 **376 通过 / 0 失败**。
English: the audit's “falsely-passing” cases are now real regressions — (1) the `OrphanReaper` case advances idle, then sweeps, then heartbeats, so dropping the idle threshold back to 60s kills the stream on the first sweep and the test goes red; (2) `SnapshotApprovals` now snapshots a **non-empty** list (an empty one passes whether or not it aliases); (3) four new `StampMessageContentIfEmptyAsync` cases (writes after the stream is gone / does not overwrite existing content / no-ops on empty text / broadcasts while streaming); (4) the “heartbeat does not resurrect” and “attach after stream ended” cases are kept. **376 passed / 0 failed** across the affected classes.

---

# AG-UI 群聊桌面版 1.0.167 发布说明（上一版）
# AG-UI Group Chat Desktop 1.0.167 Release Notes (previous release)

**版本说明**：1.0.167 修「文件明明生成好了，用户却只看到一只空气泡」：长任务在“只调工具、不吐正文”的阶段被孤儿流兜底误杀，随后最终正文追加崩溃，产物又因“挂载要求流还开着”而全部丢失。顺带把挂载失败与兜底收尾两处**静默**失败改为可见。上一版 1.0.166 的缺陷（空需求、计划没排文件步骤、语境判定提示词臃肿）见下一节。
**Version note**: 1.0.167 fixes “the file was really generated, the user only saw an empty bubble”: a long run that spends minutes calling tools without emitting prose was reclaimed by the orphan-stream reaper, the final text append then crashed, and every produced file was lost because attaching required a live stream. Two silently-swallowed failures around attaching and reaping are now visible. The 1.0.166 defects (empty request, plan with no file step, bloated speak-gate prompt) are in the next section.

## 长任务不再被“孤儿流兜底”误杀（“空气泡 + 没有文件”的根因）
# Long runs are no longer killed by the orphan-stream reaper (the “empty bubble, no file” bug)

中文：现场：用户带 `MARKETING.md` 说“请根据附件撰写 ppt”，计划 7 步全跑完、交付岗也真的生成了 5 个 pptx（磁盘上都在），但消息**正文为空、附件为空**，用户在界面上只看到一只空气泡。
根因链（现已用日志逐环证实）：
1. 交付阶段模型连续调了 8 轮 `pptx_deck`，**全程不吐正文**；而孤儿流兜底的“活跃度”只在写正文/思考/重置时刷新 —— 于是消息创建满 10 分钟的那一秒（正好对上最后一个文件的时间）被判“空闲”强制 `End`。
2. 流被 End 后，模型那段最终正文追加直接抛 `消息不存在或未开启流式灌入`，整轮崩溃。
3. 崩溃兑底 `TryAttachProductsAfterFailureAsync` 也无效：挂附件同样要求“流还开着”，5 次全部失败且被吞在 **Debug**（生产日志里完全看不到）。

修复：
- **心跳**：三条流式循环（主路径 / 交付兑底 / 审批恢复）在每一帧都调 `GroupHub.TouchAgentStream`，让“只调工具、不吐正文”的长任务不再被当成孤儿。
- **空闲阈值 60s → 5 分钟**：单次文档生成工具调用本身就会连续 1–3 分钟不产生任何流式帧，1 分钟的空闲阈值无论如何都会误杀。真孤儿的回收延迟从 1 分钟变 5 分钟，防泄漏几乎不受影响。
- **产物回挂不再依赖“流在线”**：新增 `GroupHub.AttachMessageAttachmentsAsync`（恢复/崩溃兑底专用，流已回收也能落库；流还在时照旧广播），`TryAttachProductsAfterFailureAsync` 改走它；**挂载失败从 Debug 提到 Warning**。
- **兜底收尾留痕**：孤儿流收尾时记一条 Warning（含建流/空闲秒数）——这条以前是静默的，正是“查不出来”的原因。

**实测证据（修复前后对比，同一个请求 + 同一个 `MARKETING.md` 附件）**：
- 修复前：`孤儿流兜底` 无任何日志（静默），消息 `clen=0／附件 0`，磁盘上躺着 5 个 pptx 却一个也没挂上。
- 修复后：在复现现场先加固观测，拿到了直接证据 `孤儿流兜底收尾：建流 615s / 空闲 390s`（证实就是兜底误杀）；随后同一请求重跑：**未出现任何孤儿流/崩溃日志**，消息 **正文 2,115 字 + 4 个附件**（`智能体产物回档：4 个附件挂到消息`），磁盘上 4 个 pptx 全部可下载，而该消息已存在 13 分钟（远超 10 分钟阈值）。

English: the scene — the user attached `MARKETING.md` and asked for a deck; the 7-step plan finished and the delivery agent really produced 5 pptx files (all on disk), yet the message had **no text and no attachments**: an empty bubble. Cause chain, each link now proven from logs: (1) the delivery agent called `pptx_deck` eight times in a row **without emitting any prose**, and the reaper's “activity” stamp only moves on content/reasoning writes — so at exactly the 10-minute mark the stream was `End`ed as idle; (2) the model's final text then threw `消息不存在或未开启流式灌入` and the whole run died; (3) the crash recovery could not help because attaching files *also* required a live stream — all five attaches failed and were swallowed at **Debug**. Fix: a **heartbeat** on every frame of all three streaming loops; the **idle threshold raised 60s → 5 minutes** (a single document-generation call is silent for 1–3 minutes by nature); a **durable attach** (`GroupHub.AttachMessageAttachmentsAsync`) for the recovery path that works after the stream is gone, with attach failures logged at **Warning**; and a Warning line whenever the reaper does reclaim a stream.

## 回归
# Tests

中文：新增 3 例 `OrphanReaper` / `AttachMessageAttachments` / `TouchAgentStream`（心跳保住长任务 / 无心跳的对照组证实会被误杀 / 流已回收仍可挂附件 / 心跳不复活已结束的流），时间由可注入的 `FakeClock` 驱动；相关测试类 **453 通过 / 0 失败**。
English: added 3 `OrphanReaper` / `AttachMessageAttachments` / `TouchAgentStream` cases (a heartbeat keeps a long run alive, the control case proves it dies without one, a reclaimed stream still takes attachments, and a heartbeat never resurrects an ended stream), driven by an injectable `FakeClock`. **453 passed / 0 failed** across the affected test classes.

---

# AG-UI 群聊桌面版 1.0.166 发布说明（上一版）
# AG-UI Group Chat Desktop 1.0.166 Release Notes (previous release)

**版本说明**：1.0.166 修掉两类“数字员工答非所问 / 空手而回”的缺陷。用户反复反馈「我这边还没有收到具体需求（消息内容为空），然而按重新回答后又正常，已经出现好多回了」。两处根因都不在模型，而在网关拼提示词与恢复流拼审批决议的代码里。**本版 MSI 也是 1.0.164 / 1.0.165 两节内容的首次发版**——那两节此前只进了主干、没有出过安装包。
**Version note**: 1.0.166 fixes two defects behind “the agent answers the wrong thing / comes back empty-handed”. The user kept hitting “I haven't received the actual request (message content empty)”, which went away after pressing *Regenerate*. Neither root cause was in the model: both were in the gateway — the dispatch prompt and the resume message. **This MSI is also the first shipping build for the 1.0.164 / 1.0.165 sections**, which had landed on main without an installer.

## 根因一：恢复流把刚批准的审批决议清空了（用户感知最直接的那个）
# Cause 1: the resume loop wiped the approvals it had just granted (the one users actually see)

中文：
- **现场**：`data/clienttool-trace.log` 与容器日志对比后可见规律 —— 成功的那几次恢复是 `RESUME-MSG approvals=1 tools=pptx_deck` 之后直接 `RESUME-END`（正文“已按反馈出稿，文件已生成 ✅”）；失败的几次多出**第二轮** `RESUME-MSG approvals=0 tools=`（零决议），紧接着 `RESUME-END` 的正文就是「我这边还没有收到具体需求（消息内容为空）」。
- **根因**：`ResumeRunAsync` 的批量批准循环里 `lastApprovals = approvalsThisTurn;` 是**别名赋值**，而 `approvalsThisTurn` 是每轮的收集器，循环头会先 `approvalsThisTurn.Clear()`。于是下一轮把上一轮刚自动放行的审批决议**一起清空**，`BuildResumeMessage` 造出一条**零决议的空 User 消息**投给模型 —— 模型收到的就是“空消息”，回一句“消息内容为空”完全是如实反应。
- **为什么偶发**：只有「自动放行之后还要再跑一轮」才触发，所以时有时无；用户点「重新回答」重跑常常不复现 —— 与用户观察完全一致。
- **修复**：自动放行分支改为存**快照**（新增 `SnapshotApprovals`，把决议复制成新 List）；三处保存运行现场（`PendingInteraction.ApprovalRequests`）也一律存快照，杜绝同类别名隐患。另加一道防御：一轮既无审批决议也无客户端执行结果时**不再发空回合**，而是记 WARN 并结束本轮（`HasResumePayload`）。

English:
- **What the logs showed**: successful resumes read `RESUME-MSG approvals=1 tools=pptx_deck` followed straight by `RESUME-END` (“file generated ✅”), while every failure had an extra **second** round logging `RESUME-MSG approvals=0 tools=` (zero approvals) whose `RESUME-END` body was exactly “I haven't received the actual request (message content empty)”.
- **Root cause**: in the batch-approval loop of `ResumeRunAsync`, `lastApprovals = approvalsThisTurn;` **aliased** the per-round collector, whose `Clear()` sits at the top of the loop. The next round therefore wiped the approvals just auto-granted, `BuildResumeMessage` produced a **User message with zero content**, and the model — literally handed an empty turn — answered “message content empty”.
- **Why intermittent**: it needs “an auto-approved round followed by another round”, hence the flakiness and why *Regenerate* usually looks fine.
- **Fix**: the auto-approval branch now stores a **snapshot** (new `SnapshotApprovals`), all three sites that save the interaction state do the same, and a guard (`HasResumePayload`) refuses to send a content-free turn (WARN + end the round instead).

## 根因二：派发子岗位时把「用户原始请求」弄丢了
# Cause 2: dispatching a teammate dropped the *user's original request*

中文：
- **根因**：编排计划里 `working` 初值为 `plan.Input`，但**每跑完一步就 `working = stepOut`**，而派发提示词把整段“问题”写成 `working`。于是上一步一旦产出为空（退化成占位串「（未返回内容）」）或与需求无关，第二步起的子岗位就再也看不到用户到底要什么。计划步序由模型生成、每次都不同，因此表现为**偶发**；重新回答会重新规划，所以常常又正常。
- **修复**：新增 `BuildAssignmentPrompt`，把提示词拆成 **【用户原始请求】（永不缺席、永不被顶替）+【上一步产出（仅供参考）】+【前序各岗位已产出】**；占位串不再当“问题”投喂；三段各自限长（8000/4000/6000 字）防撑爆下游上下文。
- **顺带修正**：指派者文案原先写成**目标岗位自己**（“你正被「PPT 制作排版工程师」指派处理”），语义上成了“自己指派自己”，持续误导模型；现改为真正的上级（本计划的主管）。`InvokeSubordinateAsync` 同一处错误一并修正。

English:
- **Root cause**: `working` started as `plan.Input` but **was overwritten with each step's output**, and the dispatch prompt passed `working` as the whole “question”. Once a step returned nothing (degrading to the placeholder “（未返回内容）”) or something unrelated, every later teammate lost sight of what the user actually asked for. Plan step order is model-generated and differs each run — hence intermittent, and why *Regenerate* (which re-plans) often worked.
- **Fix**: new `BuildAssignmentPrompt` splits the prompt into **【user's original request】 (never missing, never replaced) + 【previous step output (reference only)】 + 【prior teammates' output】**; placeholders are no longer fed as the question; each part is length-capped (8000/4000/6000 chars).
- Also fixed: the dispatcher used to be named as **the target itself** (“you are being dispatched by «PPT layout engineer»”), i.e. “you assigned yourself”; it is now the real superior (the plan's owner), same fix applied in `InvokeSubordinateAsync`.

## 归档：交付没出文件时不再丢掉计划素材
# Also: the plan's material is no longer dropped when delivery fails to produce a file

中文：交付兑底两次都没真正调出文件时，原先只发一句“未能生成 X 文件”就把计划期间各岗位写好的正文全丢掉了。现在新增 `DeliveryOutcome.FileGenerationFailed`，这种情况**补发计划素材**；同时把“不得反问用户要材料/定稿、优先于任何岗位人设与流程规定”提到交付提示词最前（新开一段【最高优先级】），压住“仅接收定稿才交付”“需先对齐流程”这类人设挡箭牌。
English: when both delivery attempts fail to call the file tool, the run used to emit only “failed to generate X” and throw away everything the team wrote. A new `DeliveryOutcome.FileGenerationFailed` now **re-appends the plan's material**, and the delivery prompt leads with a 【highest priority】 block stating it outranks any persona or process (“only deliver from an approved draft”, “align on the flow first”) so the model can't hide behind them.

## 可观测性（这次能定位的关键）
# Observability that made this findable

中文：计划路径此前**没有**任何输入侧日志（`plan.Input`、每步 `working`、交付提示词都不落日志，`agentChain` 也常常为空），所以“子岗位说没收到需求”只能靠猜。新增四条 Information 日志：`编排计划开始`（步数 + 输入长度 + 片段）、`指派子岗位`（父/子岗位、原始请求长度、上一步产出长度、提示词长度、上一步片段）、`交付兑底提示词`（素材长度、提示词长度、开头片段）、以及交付两次均未产出文件时的 `prompt=` 片段。
English: the plan path previously logged **nothing** about its inputs (`plan.Input`, each step's `working`, the delivery prompt, and `agentChain` was often empty), leaving “teammate says it got no request” to guesswork. Four Information logs were added: plan start (steps + input length + excerpt), per-dispatch (parent/target, request length, previous-step length, prompt length, previous-step excerpt), delivery prompt (draft length, prompt length, head), and `prompt=` on the two-failed-attempts path.

## 归档：计划没排文件步骤时，用户要的文件也要出
# Also: a file the user asked for is now delivered even when the plan scheduled no file step

中文：交付兜底原先只在「计划点名了文档技能、又被跳过」时触发。若计划**压根没排文件步骤**（实测：只有「需求分析师 → 内容策划文案 → 综合答复」三步），`NeedsDelivery=false`，于是没有任何兜底——用户说了“我要最终形成 word 文档”，最后只拿到一段带〔待补〕的提纲、没有文件。现在计划路径也按 `WantedDeliverable(context.Content)` 判一次（新增 `ShouldTryPlanDelivery`），与非编排路径口径一致；计划自己已把该技能跑过时，兜底内部会直接跳过，不会重复出文件。计划的“补发正文”仍只在原场景（计划正文被有意抑制时）生效，避免重复一段说明。
English: the delivery fallback used to fire only when the plan named a document skill and the plan path skipped it. When the plan scheduled **no** file step at all (measured: “requirements analyst → content planner → synthesis”), `NeedsDelivery` stayed false, so nothing delivered — the user asked for a Word file and got an outline full of 〔待补〕 placeholders instead. The plan path now also consults `WantedDeliverable(context.Content)` (new `ShouldTryPlanDelivery`), matching the non-plan path; when the plan already ran that skill the fallback skips itself, so no duplicate file, and the “re-append the plan text” step still only applies to the original scenario.

## 语境判定提示词瘦身：同一条判定少花 ~80% 输入
# The speak-gate prompt is now ~80% cheaper

中文：语境触发（Contextual）的“要不要发言”判定**带着一份完整对话窗口，却只换回 1 bit**（`ContextMaxMessages=10` × 每条截 4000 字，
而输出上限只有 8 个 token）。在真实数据上重建过：典型 6.5k–11.3k 字符。现在按决策专档收紧，新增两个可配旋钮
（`PromptBudget:DecisionMaxCharsPerMessage` 默认 300、`PromptBudget:DecisionMaxCharsForLatestMessage` 默认 2000），
并把 `Agents:ContextMaxMessages` 默认 10 → **3**。判定提示词的构建抽成 `BuildSpeakDecisionPrompt`（测试钩子）。

- 实测（离线，用真实群消息重建）：**11,302 → ~1,263 字符**。
- 实测（在线，临时群 + 3 条 4,500 字长消息）：判定提示词 **3,283 字**且保持有界（每加一条长消息只增 ~286 字，即钳制生效）；
  旧口径同输入约 **16,000 字**。
- 同一处日志现在可观测了：`语境判定：模型=… 提示词 N 字（历史 M 条）P(发言)=… 阈值=… → …`。

English: the Contextual “should I speak” gate used to carry a **full conversation window to answer one bit** (`ContextMaxMessages=10`, each capped at 4000 chars, with an 8-token output cap) — measured at 6.5k–11.3k chars on real data. It now has its own budget (`PromptBudget:DecisionMaxCharsPerMessage` default 300, `PromptBudget:DecisionMaxCharsForLatestMessage` default 2000) and `Agents:ContextMaxMessages` defaults to **3**; the prompt is built by the testable `BuildSpeakDecisionPrompt`. Measured offline on real messages: **11,302 → ~1,263 chars**. Measured live (temp group, 3 × 4,500-char messages): **3,283 chars and bounded** (each extra long message adds only ~286 chars, i.e. the clamp works) versus ~**16,000** under the old rules. The same log line now reports `提示词 N 字（历史 M 条）`.

## 验证（真跑）
# Verification (actually run)

中文：本机 Docker 栈（`docker compose up -d --build web`）在「与 项目总监 的单聊」重放同一句请求「帮我写一份“知聚”市场推广文案，我要最终形成 pptx 演示文稿」。
- 修复前：`RESUME-MSG approvals=1` → `RESUME-MSG approvals=0` → 正文「我这边还没有收到具体需求（消息内容为空）」，`/app/docs` **无新产物**。
- 修复后：四轮恢复**全部** `approvals=1 tools=pptx_deck`，`RESUME-END` 正文为「## 交付结果 … **产物**：`/app/docs/知聚 KnowGath 市场推广｜把 AI 放进流程，而不是放进对话框_2.pptx` 21 页 · 约 196 KB」，附件已挂在消息上，磁盘上新出现 2 个 pptx。
- 日志同时证实根因二已修：6 个 `指派子岗位` 每一步都带 `原始请求 7289 字`，其中一步上一步产出为空时提示词只省略该段、**不省略原始需求**。
English: replayed the same request in the “direct chat with 项目总监” against the local Docker stack (`docker compose up -d --build web`). Before: `RESUME-MSG approvals=1` → `approvals=0` → “message content empty”, no new file in `/app/docs`. After: all four resume rounds logged `approvals=1 tools=pptx_deck` and `RESUME-END` carried the delivery report with the produced `.pptx` attached (2 new files on disk). The logs also show all six dispatches carrying `原始请求 7289 字`, including the one whose previous step came back empty.

## 回归
# Tests

中文：新增 `AssignmentPrompt` 4 例（占位串不当需求 / 上一步无关也保留需求 / 指派者是上级不是目标 / 限长）、`PlanText_` 2 例（交付未出文件时补发素材）、`ShouldTryPlanDelivery` 3 例（计划跳过文件技能 / 计划没排文件步骤但用户要文件 / 两者都不是）、`SnapshotApprovals` / `HasResumePayload` 6 例（别名回归 + 空回合防护）、`BuildSpeakDecisionPrompt` / `ClampForDecision` 7 例（人设与规则在位 / 历史与最新消息分别钳制 / 上界 4k）。相关测试类全部通过。
English: added 4 `AssignmentPrompt` cases, 2 `PlanText_` cases, 3 `ShouldTryPlanDelivery` cases, 6 `SnapshotApprovals` / `HasResumePayload` cases (alias regression + empty-turn guard) and 7 `BuildSpeakDecisionPrompt` / `ClampForDecision` cases. All affected test classes pass.

---

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
- 启动**维度自检**：HTTP embedding 端点（Ollama / llama.cpp / 任意 OpenAI 兼容）实际返回的维度与 `MEMORY_EMBEDDING_DIMENSIONS` 不一致时**禁用语义记忆**并给出修法——以前这种错配是「写入失败、检索恒空」的静默失效（用户只觉得数字员工「记不住事」，日志无显眼错误）；探测本身失败（端点暂不可用/超时、或端点不是 OpenAI 兼容）只记 WARN、**不**禁用。单测 `EmbeddingDimensionProbeTests`（7 例）钉住：一致→启用、不一致→禁用、换了模型但同步改配置（2560）→放行、端点不可用/无向量/本地 llama/记忆本来就关着→都不动状态。
- **升级动作**：`git pull` → 放好 `./models/embedding.gguf` → `docker compose up -d --build --remove-orphans`（旧的 `agui-ollama-data` 卷可删）。升级后的三条自查命令（宿主端口归谁 / 模型闸门 / 维度自检）见 README「升级后自查（容器 healthy ≠ 配置正确）」。
  ⚠️ **`--remove-orphans` 不能省**（已实测复现）：旧 `agui-group-chat-ollama` 容器仍占着宿主 11435。不加时首次 up 直接 `Bind for 0.0.0.0:11435 failed: port is already allocated`；**注意：再次 up 之后应用看起来是好的**（web 与 llama-embed 走 compose 内网，不经宿主端口），但**宿主机 11435 仍归旧 Ollama 容器**——llama-embed 的端口映射落不了地，于是宿主侧多出一套「看不见的旧 embedding 服务」，巡检/脚本都会打到旧引擎上。
- **回退**：只改 `MEMORY_EMBEDDING_ENDPOINT` 指回任意 OpenAI 兼容端点（含自建 Ollama `ollama serve`）即可，两端向量语义一致。
- **English**: new `llama-embed` service (official llama.cpp image, **pinned by digest** — upstream ships dev builds) with a health check gating `web`; the model is a host **directory** bind mount `./models:/models:ro` (a file-level mount makes Docker create a same-named directory when the file is missing, which the download script then mistakes for an existing model); the entrypoint now rejects a **missing or too-small** (<400MB) model with an actionable message instead of letting a dimension mismatch degrade into "RAG silently fails"; the HTTP embedding endpoint is **dimension-checked once at startup** (mismatch disables semantic memory with the fix spelled out; an unreachable endpoint only warns) with 7 unit tests; `OLLAMA_PORT` → `LLAMA_EMBED_PORT`, new `LLAMA_EMBED_THREADS` (default **4**) and `LLAMA_EMBED_IMAGE`, new default endpoint, `NO_PROXY` now includes `llama-embed` and `host.docker.internal`, `--no-webui` → `--no-ui`. **Upgrade**: `git pull`, place the GGUF, then `docker compose up -d --build --remove-orphans` — required because the old Ollama container still holds host port 11435 (measured: the first up fails with `port is already allocated`; after a retry the app looks fine because web reaches llama-embed over the compose network, yet host 11435 still belongs to the old Ollama container, leaving a second, invisible embedding service for host-side tooling). Rollback: repoint `MEMORY_EMBEDDING_ENDPOINT` at any OpenAI-compatible endpoint.

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
