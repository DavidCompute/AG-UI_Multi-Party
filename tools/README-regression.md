# 运行期回归扫描（API + UI 控制台）

两个**可复用**的回归脚本，用来在一次运行中对「正在跑的服务」（本机 Docker / 桌面版 / 任意部署）做一轮自动体检。
它们不替代单元 / 集成测试（`dotnet test`），而是覆盖测试之外的两类真实缺陷：

| 脚本 | 覆盖 | 抓什么 |
|---|---|---|
| `api-smoke.mjs` | 后端 HTTP 只读主流面（46 项） | 路由 404/500、鉴权错位、序列化异常、Prometheus 指标缺失 |
| `ui-smoke.mjs` | 前端主要界面（Playwright） | **未捕获运行时错误**（`ReferenceError` 等）、**i18n 原文键泄漏**（如界面直接显示 `common.save common.cancel`） |

> 二者都是**只读**的：API 扫描全部走 GET；UI 扫描只「打开界面 + 切换页签」，新增类弹窗只开一次即关（不保存）。

## 依赖

- `api-smoke.mjs`：仅需 Node 内建 `fetch`，**零依赖**。
- `ui-smoke.mjs`：需要 Playwright。
  ```bash
  cd tools
  npm install
  npx playwright install chromium   # 首次安装浏览器内核
  ```

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

## 建议的使用时机

- 每次改动前端（`index.html` / `app.js` / i18n）后跑一遍 `ui-smoke.mjs`——i18n 与运行时错误在浏览器里才现形。
- 每次后端改动 / 重建镜像后跑一遍 `api-smoke.mjs`。
- 发布前两者都跑一遍，作为单元 / 集成测试之外的「运行期体检」。
