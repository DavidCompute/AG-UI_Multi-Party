// KnowGath 运行期 API 回归扫描（可复用）
//
// 用途：对正在运行的服务（本机 Docker / 桌面版 / 任意部署）做一轮**只读**主流面回归，
//       把「路由挂了 / 鉴权错位 / 序列化炸了 / 指标缺失」这类问题一次性端出来。
//
// 用法：
//   node tools/api-smoke.mjs                       # 默认 http://127.0.0.1:5200，账号 david/lingtong
//   node tools/api-smoke.mjs --base http://host:5200 --user X --pass Y
//   环境变量同名：AGUI_BASE / AGUI_USER / AGUI_PASS
//
// 退出码：0 = 全部通过；1 = 有失败项（脚本会逐项打印实际状态码与响应片段）。
// 说明：全部为 GET 只读探测，不改数据；`/ag-ui/metrics` 这类需要令牌的端点会带上管理员令牌。

const args = process.argv.slice(2);
const opt = (name, def) => {
  const i = args.indexOf("--" + name);
  return i >= 0 && args[i + 1] ? args[i + 1] : def;
};
const BASE = (opt("base", process.env.AGUI_BASE) || "http://127.0.0.1:5200").replace(/\/+$/, "");
const USER = opt("user", process.env.AGUI_USER) || "david";
const PASS = opt("pass", process.env.AGUI_PASS) || "lingtong";

const results = [];

async function call(method, path, token) {
  const url = BASE + path + (token ? (path.includes("?") ? "&" : "?") + "token=" + encodeURIComponent(token) : "");
  const res = await fetch(url, {
    method,
    headers: Object.assign({ Accept: "application/json" }, token ? { Authorization: "Bearer " + token } : {}),
  });
  const text = await res.text();
  return { status: res.status, text };
}

/** 一个检查项：候选路径里任一命中期望状态即通过（兼容“有无尾斜杠”等写法差异）。 */
async function check(name, paths, { token = "admin", expect = [200] } = {}) {
  const tried = [];
  for (const p of [].concat(paths)) {
    let r;
    try {
      r = await call("GET", p, token === "none" ? null : token === "user" ? userToken : adminToken);
    } catch (e) {
      tried.push(`${p} → ERR ${e.message}`);
      continue;
    }
    if (expect.includes(r.status)) { results.push({ name, ok: true, path: p, status: r.status }); return r; }
    tried.push(`${p} → ${r.status}`);
  }
  results.push({ name, ok: false, detail: tried.join(" ; ") });
  return null;
}

let adminToken = null;
let userToken = null;

(async () => {
  console.log(`KnowGath API smoke → ${BASE}\n`);

  // ---- 登录（先注册兜底：桌面版全新库可能没有 david）----
  let login = await (async () => {
    const res = await fetch(BASE + "/ag-ui/user/login", {
      method: "POST", headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ username: USER, password: PASS }),
    });
    return { status: res.status, data: await res.json().catch(() => null) };
  })();
  if (login.status !== 200) {
    const reg = await fetch(BASE + "/ag-ui/user/register", {
      method: "POST", headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ username: USER, password: PASS }),
    });
    login = { status: reg.status, data: await reg.json().catch(() => null) };
  }
  if (login.status !== 200 || !login.data?.token) {
    console.error(`登录失败（HTTP ${login.status}）：${JSON.stringify(login.data)}`);
    process.exit(1);
  }
  adminToken = login.data.token;
  userToken = adminToken;
  const me = login.data.userId;
  console.log(`登录通过：${login.data.username}（${me}，isAdmin=${!!login.data.isAdmin}）\n`);

  // ---- 健康与身份 ----
  await check("健康检查", "/ag-ui/health", { token: "none" });
  await check("当前用户", "/ag-ui/user/me");
  await check("用户目录", "/ag-ui/users");
  await check("我的知聚列表", `/ag-ui/member/${me}/groups`);
  await check("客服知聚发现", "/ag-ui/group/discover");

  // ---- 目录类（数字员工 / 技能 / 知识库 / 图库 / 市场 / 计划任务 / 语音）----
  await check("数字员工目录", ["/ag-ui/agents", "/ag-ui/agents/"]);
  await check("技能库", ["/ag-ui/skills", "/ag-ui/skills/"]);
  await check("知识库", ["/ag-ui/kb", "/ag-ui/kb/"]);
  await check("图库", ["/ag-ui/image-libs", "/ag-ui/image-libs/"]);
  await check("模板市场目录", ["/ag-ui/marketplace", "/ag-ui/marketplace/"]);
  await check("计划任务", ["/ag-ui/scheduled-tasks", "/ag-ui/scheduled-tasks/"]);
  await check("AI 分身状态", ["/ag-ui/twin", "/ag-ui/twin/"]);
  await check("用户分组", ["/ag-ui/usergroups", "/ag-ui/usergroups/"]);
  await check("品牌配置（公开）", "/ag-ui/settings/branding", { token: "none" });

  // ---- 记忆 ----
  await check("记忆总览", ["/ag-ui/memory/groups"]);
  await check("记忆条目", ["/ag-ui/memory", "/ag-ui/memory/"]);
  await check("记忆导出", ["/ag-ui/memory/export"]);

  // ---- 管理面（只读）----
  await check("运行指标快照", "/ag-ui/admin/metrics");
  await check("Prometheus 指标", "/ag-ui/metrics", { token: "admin", expect: [200] });
  await check("系统状态", "/ag-ui/admin/status");
  await check("用量统计", "/ag-ui/admin/usage");
  await check("审计日志", "/ag-ui/admin/audit");
  await check("只读参数快照", "/ag-ui/admin/config");
  await check("配置治理", "/ag-ui/admin/config/governance");
  await check("执行参数", ["/ag-ui/admin/execution", "/ag-ui/admin/execution/"]);
  await check("执行参数出厂默认", "/ag-ui/admin/execution/defaults");
  await check("员工绩效", "/ag-ui/admin/agent-performance");
  await check("组织体检", "/ag-ui/admin/org-health");
  await check("绩效知聚下拉", "/ag-ui/admin/groups");
  await check("跨副本诊断", "/ag-ui/admin/replicas");
  await check("桥接健康", "/ag-ui/admin/bridge-health");
  await check("桥接能力", "/ag-ui/admin/bridge-capabilities");
  await check("语音音色", "/ag-ui/admin/voice/voices");
  await check("孤儿盘点", "/ag-ui/admin/orphans");
  await check("平台角色", "/ag-ui/admin/roles");
  await check("用户管理", "/ag-ui/admin/users");
  await check("存储治理", ["/ag-ui/admin/storage", "/ag-ui/admin/storage/"]);
  await check("托管 API 密钥", ["/ag-ui/admin/api-keys", "/ag-ui/admin/api-keys/"]);
  await check("评测回归", ["/ag-ui/admin/eval-cases", "/ag-ui/admin/eval-cases/"]);
  await check("市场源", ["/ag-ui/admin/marketplace/sources", "/ag-ui/admin/marketplace/sources/"]);
  await check("Webhook", ["/ag-ui/admin/webhooks", "/ag-ui/admin/webhooks/"]);
  await check("原生桥安装包信息", "/ag-ui/native-bridge/download/info");

  // ---- 对外 REST API（/agui/v1）----
  await check("对外 API · me", "/agui/v1/me");
  await check("对外 API · groups", "/agui/v1/groups");

  // ---- 文档手册 ----
  await check("用户手册", "/ag-ui/manual?lang=zh");

  // ---- 内容断言：Prometheus 文本必须含核心指标（含 1.0.262 新增的去重缓存计数）----
  {
    const r = await call("GET", "/ag-ui/metrics", adminToken);
    const must = [
      "knowgath_agent_invocations_total",
      "knowgath_memory_hits_total",
      "knowgath_memory_embedding_cache_hits_total",
      "knowgath_memory_embedding_cache_misses_total",
      "knowgath_up",
    ];
    const missing = must.filter((m) => !r.text.includes(m));
    results.push(missing.length === 0
      ? { name: "Prometheus 指标内容", ok: true, detail: `${must.length} 项齐全` }
      : { name: "Prometheus 指标内容", ok: false, detail: "缺 " + missing.join(", ") });
  }

  // ---- 报告 ----
  const passed = results.filter((r) => r.ok).length;
  const failed = results.filter((r) => !r.ok);
  console.log("检查项：");
  for (const r of results) {
    console.log(`  ${r.ok ? "✓" : "✗"} ${r.name}${r.ok ? `（${r.status}${r.path ? " " + r.path : ""}${r.detail ? " " + r.detail : ""}）` : `　${r.detail}`}`);
  }
  console.log(`\n通过 ${passed}/${results.length}`);
  if (failed.length) {
    console.log("\n失败项：");
    for (const f of failed) console.log(`  - ${f.name}: ${f.detail}`);
    process.exit(1);
  }
  console.log("全部通过。");
})().catch((e) => { console.error("扫描异常：", e); process.exit(2); });
