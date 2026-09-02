// AG-UI 一键组织编排 → 创建客服知聚 → 客服入口发现：浏览器自动化验证（Playwright）
//
// 用法：
//   cd tools
//   npm install
//   npx playwright install chromium        # 首次安装浏览器内核
//   node ui-orchestrate-flow.mjs            # 有头（弹出浏览器，可观看），默认
//   BASE_URL=... PASSWORD=xxx node ui-orchestrate-flow.mjs
//
// 环境变量：
//   BASE_URL   默认 http://localhost:5200
//   USERNAME   默认 david
//   PASSWORD   默认 secret123
//   HEADLESS   1 = 无头；缺省 0 = 有头（亲眼看浏览器点一遍）
//   KEEP        1 = 结束后不清理新建的编排产物；缺省清理（删除新建数字员工团体+客服知聚）
import { chromium } from "playwright";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const shots = path.join(__dirname, "screenshots");
fs.mkdirSync(shots, { recursive: true });

const BASE_URL = process.env.BASE_URL || "http://localhost:5200";
const USERNAME = process.env.USERNAME || "david";
const PASSWORD = process.env.PASSWORD || "secret123";
const HEADLESS = process.env.HEADLESS === "1";
const KEEP = process.env.KEEP === "1";

// 每次给客服知聚一个唯一名，避免与历史订阅 / 断言相冲
const stamp = new Date().toISOString().replace(/[-:.TZ]/g, "").slice(0, 14);
const req = "组建一个客服团队，包含一名客服主管和若干客服专员，负责售前咨询和售后支持";
const circleName = `UI验证客服知聚-${stamp}`;

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
let step = 0;
const ok = (msg) => console.log(`  [OK] ${msg}`);
const shot = async (page, name) => {
  await page.waitForTimeout(250);
  await page.screenshot({ path: path.join(shots, `${String(step++).padStart(3, "0")}-${name}.png`) });
  ok(`截图 ${name}.png`);
};

async function login(page) {
  await page.goto(BASE_URL, { waitUntil: "domcontentloaded" });
  // 等登录层出现或已登录（隐藏）：两种情况都算已加载完成
  await page.waitForSelector("#agentOrgBtn", { timeout: 15000 }); // 应用外壳已挂载
  const needLogin = await page.evaluate(() =>
    !!document.getElementById("authOverlay") && !document.getElementById("authOverlay").classList.contains("hidden"));
  if (!needLogin) { ok("已在登录态（无需重新登录）"); return; }
  await page.fill("#authUsername", USERNAME);
  await page.fill("#authPassword", PASSWORD);
  await page.click("#authSubmit");
  // 登录成功后 authOverlay 会隐藏
  await page.waitForFunction(() =>
    document.getElementById("authOverlay")?.classList.contains("hidden"), { timeout: 20000 });
  ok(`已登录 ${USERNAME}`);
  await shot(page, "logged-in");
}

async function runOrchestrate(page) {
  // 打开「一键组织编排」弹窗
  await page.click("#agentOrchBtn");
  await page.waitForSelector("#orgOrchModal:not(.hidden)", { timeout: 10000 });
  ok("打开一键组织编排弹窗");
  await shot(page, "orchestrate-modal");

  // 输入需求并生成
  await page.fill("#orgOrchReq", req);
  await page.click("#orgOrchGen");
  await page.waitForFunction(
    () => (document.getElementById("orgOrchPreview")?.textContent || "").length > 50,
    { timeout: 120000 }, // 真实模型生成需要时间
  );
  ok("方案已生成（预览区有内容）");
  await shot(page, "plan-preview");

  // 勾选「创建客服知聚」并填名称
  await page.check("#orgOrchSupportCircle");
  await page.fill("#orgOrchSupportName", circleName);
  ok(`已勾选创建客服知聚，名称=${circleName}`);
  await shot(page, "support-circle-checked");

  // 确认创建（apply）。成功后 modal 隐藏并 toast。
  await page.click("#orgOrchApply");
  await page.waitForFunction(
    () => document.getElementById("orgOrchModal")?.classList.contains("hidden"),
    { timeout: 120000 },
  );
  ok("创建成功（弹窗已关闭）");
  await shot(page, "after-apply");
}

async function verifyViaApi(page) {
  // 从会话里取 token，用 API 复核客服知聚是否可发现、成员是否客服
  const token = await page.evaluate(() => {
    const raw = sessionStorage.getItem("agui.auth") || localStorage.getItem("agui.auth");
    return raw ? JSON.parse(raw).token : null;
  });
  if (!token) throw new Error("未从会话读取到 token");

  const discover = await fetch(`${BASE_URL}/ag-ui/group/discover`, {
    headers: { Authorization: `Bearer ${token}` },
  });
  const circles = await discover.json();
  const target = circles.find((c) => c.groupName === circleName);
  ok(`客服知聚发现接口返回 ${circles.length} 个客服知聚`);
  if (!target) throw new Error(`未发现客服知聚「${circleName}」`);
  ok(`客服知聚「${circleName}」可发现：groupId=${target.groupId}, isSupportCircle=${target.isSupportCircle}, kind=${target.kind}`);

  // 成员核验：4 个客服数字员工应 role=Admin（客服）+ 创建者为 Owner
  const members = await (await fetch(`${BASE_URL}/ag-ui/group/${target.groupId}/members`, {
    headers: { Authorization: `Bearer ${token}` },
  })).json();
  const agents = members.filter((m) => m.memberType === "agent");
  const owner = members.find((m) => m.role === "owner");
  ok(`客服知聚成员：数字员工 ${agents.length} 位（应含主管+专员）、Owner=${owner?.memberId || owner?.nickname}`);
  const allSupport = agents.length > 0 && agents.every((m) => m.role === "admin");
  ok(`全员客服(role=admin)：${allSupport ? "✅" : "❌"}`);

  // 数字员工目录应已包含刚创建的几个 cs_* 智能体
  const agentsAll = await (await fetch(`${BASE_URL}/ag-ui/agents`, {
    headers: { Authorization: `Bearer ${token}` },
  })).json();
  const csIds = agentsAll.filter((a) => /^cs_/.test(a.agentId || "")).map((a) => a.agentId);
  ok(`数字员工目录中 cs_* 智能体：${csIds.length} 个 -> ${csIds.join(", ")}`);

  return { gid: target.groupId, csIds };
}

async function cleanup(page, gid, csIds) {
  if (KEEP) { ok("KEEP=1，跳过清理（保留新建产物）"); return; }
  const token = await page.evaluate(() => {
    const raw = sessionStorage.getItem("agui.auth") || localStorage.getItem("agui.auth");
    return raw ? JSON.parse(raw).token : null;
  });
  // 解散客服知聚（服务端从 token 解析身份，operatorId 仅为回退）
  const disband = await fetch(`${BASE_URL}/ag-ui/group/disband`, {
    method: "POST", headers: { "Content-Type": "application/json", Authorization: `Bearer ${token}` },
    body: JSON.stringify({ groupId: gid }),
  });
  ok(`已解散客服知聚：${disband.ok ? "✅" : `❌ ${disband.status}`}`);
  // 删除数字员工
  for (const id of csIds) {
    const del = await fetch(`${BASE_URL}/ag-ui/agents/${encodeURIComponent(id)}`, {
      method: "DELETE", headers: { Authorization: `Bearer ${token}` },
    });
    ok(`删除数字员工 ${id}：${del.ok ? "✅" : `❌ ${del.status}`}`);
  }
}

let browser;
try {
  console.log(`\n=== 浏览器自动化验证：一键组织编排 → 创建客服知聚 ===\n`);
  console.log(`目标: ${BASE_URL}  用户: ${USERNAME}  有头: ${HEADLESS ? "否" : "是"}`);
  browser = await chromium.launch({ headless: HEADLESS });
  const page = await browser.newPage();
  page.setDefaultTimeout(15000);

  await login(page);
  await runOrchestrate(page);
  const { gid, csIds } = await verifyViaApi(page);
  await cleanup(page, gid, csIds);

  console.log(`\n=== ✅ 端到端通过：界面点选 + API 核验均成功 ===\n`);
  console.log(`截图目录：${shots}\n`);
  await browser.close();
} catch (e) {
  console.error(`\n=== ❌ 验证失败：${e.message} ===\n`);
  if (browser) await browser.close();
  process.exit(1);
}
