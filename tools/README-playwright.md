# 浏览器自动化：一键组织编排 → 创建客服知聚

`ui-orchestrate-flow.mjs` 用 Playwright 驱动真实浏览器，自动走一遍完整 UI 链路并截图：

1. 打开 `http://localhost:5200` 并登录（默认 david / secret123）。
2. 点「一键组织编排」→ 输入客服团队需求 → 生成方案（真实调用 DeepSeek）。
3. 勾选「同时创建客服知聚」、填名称 → 确认创建。
4. 用 API 复核：客服知聚可被发现、4 位数字员工均为客服（role=admin）、cs_* 智能体已入目录。
5. 收尾清理（解散客服知聚 + 删除 cs_* 数字员工；设 `KEEP=1` 可保留）。
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
