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
