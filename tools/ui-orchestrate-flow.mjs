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
  // 等登录层挂载（无论在哪态）
  await page.waitForSelector("#authOverlay", { timeout: 15000 });
  const needLogin = await page.evaluate(() =>
    !document.getElementById("authOverlay").classList.contains("hidden"));
  if (!needLogin) { ok("已在登录态（无需重新登录）"); return; }
  await page.fill("#authUsername", USERNAME);
  await page.fill("#authPassword", PASSWORD);
  await page.click("#authSubmit");
  // 登录成功后 authOverlay 会隐藏，且顶部入口按钮出现
  await page.waitForFunction(() =>
    document.getElementById("authOverlay")?.classList.contains("hidden"), { timeout: 20000 });
  await page.waitForSelector("#agentManageBtn", { state: "visible", timeout: 10000 });
  // 首次登录可能自动弹出「模型配置」提示（未显式保存过配置）。它不影响用环境变量里的 key，
  // 但会遮挡其它弹窗，这里点取消关掉，避免拦截后续点击。
  const mcVisible = await page.evaluate(() => {
    const el = document.getElementById("modelConfigModal");
    return !!(el && !el.classList.contains("hidden"));
  });
  if (mcVisible) {
    await page.click("#mcCancel");
    ok("已关闭自动弹出的「模型配置」提示（使用环境变量中的 DeepSeek key）");
  }
  ok(`已登录 ${USERNAME}`);
  await shot(page, "logged-in");
}

async function runOrchestrate(page) {
  // 数字员工管理入口（含工具栏与「一键编排」按钮）在 agentModal 里，先打开它
  await page.waitForSelector("#agentManageBtn", { state: "visible", timeout: 10000 });
  await page.click("#agentManageBtn");
  await page.waitForSelector("#agentOrchBtn", { state: "visible", timeout: 15000 });
  await page.click("#agentOrchBtn");
  await page.waitForSelector("#orgOrchModal:not(.hidden)", { timeout: 10000 });
  ok("打开一键组织编排弹窗");
  await shot(page, "orchestrate-modal");

  // 输入需求并生成
  await page.fill("#orgOrchReq", req);
  await page.click("#orgOrchGen");
  await page.waitForFunction(
    () => (document.getElementById("orgOrchPreview")?.textContent || "").length > 50,
    undefined, { timeout: 120000 }, // 真实模型生成需要时间
  );
  ok("方案已生成（预览区有内容）");
  await shot(page, "plan-preview");

  // 勾选「创建客服知聚」并填名称
  await page.check("#orgOrchSupportCircle");
  await page.fill("#orgOrchSupportName", circleName);
  ok(`已勾选创建客服知聚，名称=${circleName}`);
  await shot(page, "support-circle-checked");

  // 确认创建（apply）。先等网络响应，成功则 modal 隐藏；失败则抛出明确错误（DeepSeek 生成偶发失败）。
  const applyRespP = page.waitForResponse(
    (r) => r.url().endsWith("/ag-ui/agents/orchestrate/apply") && r.request().method() === "POST",
    { timeout: 180000 },
  );
  await page.click("#orgOrchApply");
  const applyResp = await applyRespP;
  if (!applyResp.ok()) {
    let msg = `HTTP ${applyResp.status()}`;
    try { const j = await applyResp.json(); msg += " — " + (j.message || j.error || JSON.stringify(j)); } catch {}
    throw new Error(`编排 apply 失败：${msg}`);
  }
  const applyData = await applyResp.json().catch(() => null);
  await page.waitForFunction(
    () => document.getElementById("orgOrchModal")?.classList.contains("hidden"),
    undefined, { timeout: 20000 },
  );
  ok("创建成功（apply 200，弹窗已关闭）");
  await shot(page, "after-apply");
  // 从 apply 响应里拿权威的「新建产物」清单，供清理用（服务端返回它实际创建的数字员工 / 技能 / 客服知聚）。
  const createdAgentIds = applyData?.agents || [];
  const createdSkillIds = applyData?.skills || [];
  const gid = applyData?.supportCircleGroupId || null;
  return { gid, createdAgentIds, createdSkillIds };
}

async function verifyViaApi(page, applied = {}) {
  // 从会话里取 token，用 API 复核客服知聚是否可发现、成员是否客服
  const token = await page.evaluate(() => {
    const raw = sessionStorage.getItem("agui.auth") || localStorage.getItem("agui.auth");
    return raw ? JSON.parse(raw).token : null;
  });
  if (!token) throw new Error("未从会话读取到 token");

  // 优先用 apply 响应给出的客服知聚 groupId；否则从发现接口按名字找
  let gid = applied.gid || null;
  const discover = await fetch(`${BASE_URL}/ag-ui/group/discover`, {
    headers: { Authorization: `Bearer ${token}` },
  });
  const circles = await discover.json();
  const target = gid ? circles.find((c) => c.groupId === gid) : circles.find((c) => c.groupName === circleName);
  ok(`客服知聚发现接口返回 ${circles.length} 个客服知聚`);
  if (!target) throw new Error(`未发现客服知聚「${circleName || gid}」`);
  if (!gid) gid = target.groupId;
  ok(`客服知聚「${target.groupName || target.groupId}」可发现：groupId=${target.groupId}, isSupportCircle=${target.isSupportCircle}, kind=${target.kind}`);

  // 成员核验：数字员工应 Role=Admin（客服）+ 创建者为 Owner
  const members = await (await fetch(`${BASE_URL}/ag-ui/group/${target.groupId}/members`, {
    headers: { Authorization: `Bearer ${token}` },
  })).json();
  const agents = members.filter((m) => m.memberType === "agent");
  const owner = members.find((m) => m.role === "owner");
  ok(`客服知聚成员：数字员工 ${agents.length} 位（应含主管+专员）、Owner=${owner?.memberId || owner?.nickname}`);
  const allSupport = agents.length > 0 && agents.every((m) => m.role === "admin");
  ok(`全员客服(role=admin)：${allSupport ? "✅" : "❌"}`);

  // 数字员工目录应包含 apply 创建的（authoritative list）或客服知聚内的数字员工
  const createdAgentIds = applied.createdAgentIds && applied.createdAgentIds.length
    ? applied.createdAgentIds
    : agents.map((m) => m.memberId);
  const agentsAll = await (await fetch(`${BASE_URL}/ag-ui/agents`, {
    headers: { Authorization: `Bearer ${token}` },
  })).json();
  const present = createdAgentIds.filter((id) => agentsAll.some((a) => a.agentId === id));
  ok(`数字员工目录命中刚创建的数字员工：${present.length}/${createdAgentIds.length} -> ${present.join(", ") || "(无)"}`);
  if (present.length < createdAgentIds.length)
    throw new Error(`数字员工目录缺少刚创建的数字员工（${createdAgentIds.join(", ")}）`);

  return { gid, createdAgentIds };
}

async function cleanup(page, { gid, createdAgentIds = [], createdSkillIds = [] }) {
  if (KEEP) { ok("KEEP=1，跳过清理（保留新建产物）"); return; }
  const token = await page.evaluate(() => {
    const raw = sessionStorage.getItem("agui.auth") || localStorage.getItem("agui.auth");
    return raw ? JSON.parse(raw).token : null;
  });
  const auth = { Authorization: `Bearer ${token}` };
  // 解散客服知聚（服务端从 token 解析身份，operatorId 仍需按字段提供以满足绑定）
  if (gid) {
    const disband = await fetch(`${BASE_URL}/ag-ui/group/disband`, {
      method: "POST", headers: { "Content-Type": "application/json", ...auth },
      body: JSON.stringify({ groupId: gid, operatorId: USERNAME }),
    });
    if (!disband.ok) throw new Error(`解散客服知聚 ${gid} 失败: HTTP ${disband.status}（请手动清理残留）`);
    ok(`已解散客服知聚：✅`);
  }
  // 删除数字员工
  for (const id of createdAgentIds) {
    const del = await fetch(`${BASE_URL}/ag-ui/agents/${encodeURIComponent(id)}`, {
      method: "DELETE", headers: auth,
    });
    ok(`删除数字员工 ${id}：${del.ok ? "✅" : `❌ ${del.status}`}`);
  }
  // 删除本次新建的技能：仅删「不再被任何现存数字员工引用」的，避免误删用户团队在多部署里同名的真实技能。
  if (createdSkillIds.length) {
    const agentsAll = await (await fetch(`${BASE_URL}/ag-ui/agents`, {
      headers: auth,
    })).json().catch(() => []);
    const referenced = new Set();
    for (const a of agentsAll) for (const sid of (a.skillDefIds || a.skillIds || [])) referenced.add(sid);
    for (const sid of createdSkillIds) {
      if (referenced.has(sid)) { ok(`技能 ${sid} 仍被其它数字员工引用，跳过删除`); continue; }
      const del = await fetch(`${BASE_URL}/ag-ui/skills/${encodeURIComponent(sid)}`, { method: "DELETE", headers: auth });
      ok(`删除技能 ${sid}：${del.ok ? "✅" : `❌ ${del.status}`}`);
    }
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
  const applied = await runOrchestrate(page);
  const verified = await verifyViaApi(page, applied);
  await cleanup(page, { gid: verified.gid, createdAgentIds: verified.createdAgentIds, createdSkillIds: applied.createdSkillIds });

  console.log(`\n=== ✅ 端到端通过：界面点选 + API 核验均成功 ===\n`);
  console.log(`截图目录：${shots}\n`);
  await browser.close();
} catch (e) {
  console.error(`\n=== ❌ 验证失败：${e.message} ===\n`);
  if (browser) await browser.close();
  process.exit(1);
}
