# 浏览器自动化：一键组织编排 → 创建客服知聚

`ui-orchestrate-flow.mjs` 用 Playwright 驱动真实浏览器，自动走一遍完整 UI 链路并截图：

1. 打开 `http://localhost:5200` 并登录（默认 david / secret123）。
2. 点「一键组织编排」→ 输入客服团队需求 → 生成方案（真实调用 DeepSeek）。
3. 勾选「同时创建客服知聚」、填名称 → 确认创建。
4. 用 API 复核：客服知聚可被发现、方案里的数字员工均作为客服（role=admin）入目录；
   （数字员工数量 / ID 由模型决定，脚本按 apply 响应动态取清单，不依赖固定前缀。）
5. 收尾清理（解散客服知聚 + 删除新建的数字员工 + 删除新建的技能；设 `KEEP=1` 可保留）。
   - 技能删除带保护：仅删「不再被任何现存数字员工引用」的技能，避免误删用户团队在不同部署里同名的真实技能。
6. 每个关键步骤截图到 `tools/screenshots/`。

## 首次安装（只需一次）

```bash
cd tools
npm install                 # 安装 playwright 包（不含浏览器内核）
npx playwright install chromium    # 下载 Chromium（约 150MB）
```

## 运行

```bash
# 有头（弹浏览器，可亲眼观看）—— 默认
cd tools && node ui-orchestrate-flow.mjs

# 无头
cd tools && HEADLESS=1 node ui-orchestrate-flow.mjs

# 自定义账号 / 站点 / 保留产物
BASE_URL=http://localhost:5200 USERNAME=david PASSWORD='secret123' KEEP=1 node ui-orchestrate-flow.mjs
```

## 退出码

- `0` = 链路全部通过（界面点选 + API 核验）。
- `1` = 任一步失败（控制台会打印失败原因，`screenshots/` 留有到失败前一步的截图）。

> 前置：应用已用 Docker 部署在 `http://localhost:5200`，且账号密码有效（脚本里的 PASSWORD 与环境变量一致）。

---

# 配额水位 / 覆写同步回归（`ui-quota-alerts.mjs`）

覆盖 1.0.265～1.0.281 的前端渲染与同步路径，**只读、不写真实数据**（服务端数据用 fetch 桩替换，事件直接投递）。

覆盖点：

1. `GET /ag-ui/admin/usage` 含 `groupsToday[]` / `usersToday[]`，分知聚行含 `quota` / `level`；`/quota-status` 含 `quotaPerGroup` / `warnRatio` / `groups`。
2. 用量页三张表：分知聚（红 / 琥珀 / `⚙` 覆写标）、分触发者（红 / 琥珀 / `system` 豁免）、meta 汇总。
3. 水位值**白名单**：注入非法 `level` 不得进入 `state`，页面不得出现注入属性；合法 `warn` 正常打标。
4. `GROUP_UPDATED` 携带 `quotaPerGroup` / `quotaPerGroupUntil` / `model` 时**同步本地缓存**（否则重开设置弹窗会回填旧值），空串 → `null`；到期时间可回填并**无损回转**，取消覆写时额度与到期时间一并清空。

```bash
cd tools && HEADLESS=1 node ui-quota-alerts.mjs
# 或
cd tools && npm run ui:quota
```

退出码：`0` = 通过（允许有跳过项，例如当前账号没有知聚）；`1` = 有失败项。

## 在 GitHub Actions 里跑（无需本地起服务）

`.github/workflows/ui-regression.yml`（**手工触发**：Actions → “UI regression (quota alerts)” → Run workflow，或 `gh workflow run ui-regression.yml`）：

1. 在干净的 Ubuntu runner 上用 **mock 模型 + 内存存储** 起 Web（`dotnet run`，不用 docker compose——后者依赖 605MB 的 embedding 模型）；
2. 注册 `david`（首用户自动成为管理员）并建一个知聚（避免角标 / `GROUP_UPDATED` 两组断言被跳过）；
3. 安装 Playwright 并运行本脚本；失败时附带 Web 日志尾部。

> 后端语义（阈值触发、覆写写入与权限、配额拦截）已由 .NET 集成测试覆盖，本脚本只盯前端；两者互补。

---

## 企业合规界面回归（账号注销入口 / 评价按钮 / 管理员审计）

`ui-regression-compliance.mjs` 覆盖近期合规相关界面改动（无需手工造号）：

1. 普通用户：资料弹窗「危险操作」区显示「⬇ 导出我的数据」与「注销账户」；注销弹窗错误密码提示与取消；
2. 消息 👍/👎 评价按钮默认隐藏、悬停消息可见（与复制/重新回答等头部按钮一致的悬停显隐）；
3. 管理员：用户管理他人行出现「🗑️ 彻底删除」；控制台「审计日志」tab 打开渲染；「系统状态」显式展示**文档在线转换**（LibreOffice）能力行——就绪显示「就绪」，未就绪标警示色并给出修复指引（Docker 内置 LibreOffice，应显示「就绪」）。

```bash
cd tools && node ui-regression-compliance.mjs                 # 有头，自建临时账号
cd tools && HEADLESS=1 node ui-regression-compliance.mjs      # 无头
# 已有实例非空时管理员不能自举：显式提供管理员账号即可跑管理员套件
cd tools && ADMIN_USERNAME=admin ADMIN_PASSWORD=xxx node ui-regression-compliance.mjs
```

账号策略：新注册账号用临时唯一用户名（`uireg_<ts>` / `uiadmin_<ts>`，口令 `secret123`）；
实例为空（首个注册者自动超管）时管理员套件自动启用；实例已有用户且未提供管理员账号时管理员套件自动跳过。
收尾：管理员存在时删除自建普通账号；`KEEP=1` 保留全部临时产物。退出码 0 = 通过。截图输出 `tools/screenshots/uir-*`。

---

## 数字员工单聊（kind=direct）UI 验证

`direct-chat-flow.mjs` 验证“数字员工列表 → 💬 单聊 → 进入私有双人群并（可选）发普通消息即触发”这一新功能：

1. 登录并创建一个临时数字员工；
2. 在「数字员工管理」列表中该行点 💬（单聊），后端 `POST /ag-ui/agents/direct` 幂等建/复用彼此隔离的私有双人群；
3. 管理窗关闭、前端切到该单聊；
4. API 复核：该群出现在“我的知聚”、私密且成员数=2、且再次进入返回同一群（幂等）；
5. 在聊天输入框发一条普通（不 @）消息，轮询是否有新的回复（C1 直达触发的可视化观测，属尽力而为）；
6. 收尾解散单聊 + 删除临时数字员工（`KEEP=1` 可保留）。

```bash
cd tools && node direct-chat-flow.mjs                 # 有头（默认）
cd tools && HEADLESS=1 node direct-chat-flow.mjs      # 无头
```
环境变量同上一脚本（`BASE_URL`/`USERNAME`/`PASSWORD`/`HEADLESS`/`KEEP`）。截图输出到 `tools/screenshots/`。

> 前置：同样需要一个运行中的 Web 实例（如 `dotnet run --project src/AguiGroupChat.Web` 或对应 Docker）。若未配置 DeepSeek Key，请为单聊的对端用 `Agents:Provider=mock` 启动，便于第 5 步看到本地模拟回复。

---

## 界面人性化 UI 回归套件（Playwright）

对界面布局与操作体验的回归验证：每套脚本自建临时知聚 / 数据，跑完自动清理。默认账号 `david` / `lingtong`，
可用 `BASE_URL` / `USERNAME` / `PASSWORD` / `HEADLESS=1` 覆盖。

```bash
cd tools
HEADLESS=1 node ui-ux-polish.mjs          # 23 项：基础人性化（抽屉 / 弹窗焦点 / 帮助搜索…）
HEADLESS=1 node ui-ux-round3.mjs          # 17 项：未读分隔线 / 滚动记忆 / 侧栏收起 / 双击引用
HEADLESS=1 node ui-ux-round4.mjs          # 19 项：「⋯」菜单 / 话题栏收起 / 快捷键面板 / 上次离开分隔线
HEADLESS=1 node ui-ux-round5.mjs          # 21 项：搜索命中高亮 + 上/下一条导航条 / 图片灯箱
HEADLESS=1 node ui-ux-round6.mjs          # 13 项：全局搜索一致性 / 灯箱旋转下载 / 拖拽上传
HEADLESS=1 node ui-ux-round7.mjs          # 17 项：搜索弹窗内翻页（↑↓/回车）/ 表情选择器
HEADLESS=1 node ui-ux-round8.mjs          # 22 项：消息多选（批量复制 / 转发 / 删除）
HEADLESS=1 node ui-panel-resize.mjs       # 12 项：侧栏 / 输入区拖拽宽度与持久化
HEADLESS=1 node ui-process-layers.mjs     # 21 项：执行过程 / 最终答复分层
HEADLESS=1 node ui-plan-collapse.mjs      # 11 项：计划卡默认展开 / 收起
SAMPLES=../artifacts/e2e HEADLESS=1 node ui-group-files.mjs   # 19 项：输出物（需样例文件）
SAMPLES=../artifacts/e2e HEADLESS=1 node ui-doc-slides.mjs    # 23 项：办公文档在线查看 / 幻灯片
HEADLESS=1 node ui-play-sprites.mjs       # 23 项：演示文稿形状级动画（底图 + 精灵，含逐段行带切分）
HEADLESS=1 node ui-kb-auto-toggle.mjs     # 17 项：「显示自动沉淀的知识库」开关（知识库管理 + 绑定知识库弹窗，两处共享状态）
HEADLESS=1 node ui-kb-strictness.mjs      # 知识库检索严格度 + 操作列几何 + 自动沉淀库开关（检索部分需 embedding 可用）
HEADLESS=1 node ui-kb-auto-toggle.mjs     # 17 项：「显示自动沉淀的知识库」开关（知识库管理 + 绑定知识库弹窗，两处共享状态）
```

- 退出码 `0` = 全部通过；`1` = 有失败（控制台打印失败项）。
- **不要并发跑**：多个脚本同时打同一后端会因时序争用出现偶发失败，请逐个串行执行。
- `ui-group-files` / `ui-doc-slides` 需要样例文件（`artifacts/e2e/sample.docx`、`sample-notes.pptx`；`artifacts/` 已 gitignore）。
- `ui-ux-round6.mjs` 的「下载原图」用 Playwright 下载事件校验，无需额外依赖。
- `ui-play-sprites.mjs` 需要一份**带转场/动画**的 pptx：默认用 `artifacts/pptx-play/deck.pptx`（由 `PresentationPlaybackTests` 生成），可用 `PPTX=...` 覆盖。
  该样稿的要点页是逐段构建，会验证「同 spid 多条目 → 按行带切成 4 个精灵、每步只多显现一段」。

---

## 演示文稿在线播放（转场 + 精灵）端到端核对

`e2e-play-check.mjs`：登录 → 建临时知聚 → 上传一份 pptx → 取**播放清单**与**播放稿 PDF**，打印
`expanded / slideCount / pages / transitions / 每张幻灯片页数`，并落一份 `artifacts/pptx-play/play.pdf`。

```bash
cd tools
node e2e-play-check.mjs ../artifacts/pptx-play/deck.pptx   # 需一份带转场/动画的 pptx
```

> 播放稿校对：把落盘的 `play.pdf` 拷进容器用 `pdftoppm` 数页，应等于清单的 `pages.length`
> （例：3 张幻灯片全部走精灵模式 → 播放稿 3 页，每页是移除被动画形状后的底图；前端在底图上叠精灵播真动画）。
