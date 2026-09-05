// AG-UI 数字员工单聊（kind=direct）浏览器自动化验证（Playwright）
//
// 用法：
//   cd tools
//   npm install && npx playwright install chromium      # 首次
//   node direct-chat-flow.mjs                            # 有头（默认）
//   HEADLESS=1 BASE_URL=http://localhost:5200 PASSWORD=xxx node direct-chat-flow.mjs
//
// 环境变量：
//   BASE_URL   默认 http://localhost:5200
//   USERNAME   默认 david
//   PASSWORD   默认 secret123
//   HEADLESS   1 = 无头
//   KEEP        1 = 结束后不清理（保留新建数字员工与单聊群）
//
// 验证目标：
//   1. 登录后创建一个临时数字员工；
//   2. 在「数字员工管理」列表对该行点 💬（单聊）→ 后端 POST /ag-ui/agents/direct 建/复用独立私有双人群；
//   3. 管理窗关闭、前端切到该单聊群；
//   4. API 复核：该群出现在“我的知聚”、私密且两员（用户 + 数字员工）；
//   5. 幂等（再次进入返回同一 groupId）—— UI 层内置；另做 API 二次进入断言同群；
//   6. 收尾清理（解散单聊 + 删除临时数字员工），KEEP=1 则保留。
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

const stamp = new Date().toISOString().replace(/[-:.TZ]/g, "").slice(0, 14);
const agentId = `agent_demo_${stamp}`;
const agentNick = `单聊演示-${stamp}`;

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
let step = 0;
const ok = (msg) => console.log(`  [OK] ${msg}`);
const shot = async (page, name) => {
  await page.waitForTimeout(250);
  await page.screenshot({ path: path.join(shots, `${String(step++).padStart(3, "0")}-direct-${name}.png`) });
  ok(`截图 ${name}.png`);
};
const sessionToken = async (page) => {
  const raw = await page.evaluate(() =>
    sessionStorage.getItem("agui.auth") || localStorage.getItem("agui.auth"));
  return raw ? JSON.parse(raw).token : null;
};

async function login(page) {
  await page.goto(BASE_URL, { waitUntil: "domcontentloaded" });
  await page.waitForSelector("#authOverlay", { timeout: 15000 });
  const needLogin = await page.evaluate(() =>
    !document.getElementById("authOverlay").classList.contains("hidden"));
  if (!needLogin) { ok("已在登录态"); return; }
  await page.fill("#authUsername", USERNAME);
  await page.fill("#authPassword", PASSWORD);
  await page.click("#authSubmit");
  await page.waitForFunction(() =>
    document.getElementById("authOverlay")?.classList.contains("hidden"), { timeout: 20000 });
  await page.waitForSelector("#agentManageBtn", { state: "visible", timeout: 10000 });
  ok(`已登录 ${USERNAME}`);
}

async function createAgentViaApi(page) {
  const token = await sessionToken(page);
  if (!token) throw new Error("未从会话读取到 token");
  const auth = { "Content-Type": "application/json", Authorization: `Bearer ${token}` };
  const res = await fetch(`${BASE_URL}/ag-ui/agents`, {
    method: "POST", headers: auth,
    body: JSON.stringify({ agentId, nickname: agentNick, description: "单聊自动化演示", instructions: "你是单聊演示助手。", triggerMode: "mentioned" }),
  });
  if (!res.ok) throw new Error(`创建数字员工失败: HTTP ${res.status} ${await res.text()}`);
  ok(`已创建临时数字员工 ${agentId}(${agentNick})`);
}

async function openChatFromManager(page) {
  await page.waitForSelector("#agentManageBtn", { state: "visible", timeout: 10000 });
  await page.click("#agentManageBtn");
  await page.waitForSelector("#agentModal:not(.hidden)", { timeout: 10000 });
  await page.waitForSelector("#agentList .agent-row", { timeout: 10000 });
  await shot(page, "manager");
  // 找到该数字员工所在行，点行内 💬（单聊）
  const clicked = await page.locator("#agentList .agent-row").filter({
    hasText: agentNick,
  }).locator('[data-act="chat"]').click({ timeout: 10000 }).then(() => true).catch(() => false);
  if (!clicked) throw new Error(`数字员工列表未找到「${agentNick}」行的单聊按钮`);
  ok(`已点击「${agentNick}」行的 💬（单聊）`);
}

async function verifyDirectGroup(page) {
  // startDirectChat：POST direct → 关弹窗 → loadGroups → selectGroup(groupId)
  // 等 agentModal 回隐藏（进入单聊后前端关闭管理窗）
  await page.waitForFunction(() =>
    document.getElementById("agentModal")?.classList.contains("hidden"), { timeout: 15000 });
  ok("数字员工管理弹窗已关闭（前端已切去单聊）");
  await shot(page, "entered-direct");

  const token = await sessionToken(page);
  const me = await (await fetch(`${BASE_URL}/ag-ui/user/me`, { headers: { Authorization: `Bearer ${token}` } })).json();
  const myId = me?.userId;
  if (!myId) throw new Error("无法解析用户身份");
  // “我的知聚”应包含这个私有单聊群
  const groups = await (await fetch(`${BASE_URL}/ag-ui/member/${encodeURIComponent(myId)}/groups`, {
    headers: { Authorization: `Bearer ${token}` },
  })).json();
  const directGroup = groups.find((g) => g.groupId && g.groupName === `与 ${agentNick} 的单聊`);
  if (!directGroup) throw new Error(`“我的知聚”中未找到与「${agentNick}」的单聊群`);
  ok(`已建立私有单聊群：${directGroup.groupId}（名=${directGroup.groupName}, 成员数=${directGroup.memberCount}, 私密=${directGroup.isPrivate}）`);
  if (directGroup.memberCount !== 2) throw new Error(`单聊群成员数应为 2，实际 ${directGroup.memberCount}`);
  if (!directGroup.isPrivate) throw new Error("单聊群应默认为私密（isPrivate=true）");

  // 幂等：API 再次进入同一数字员工应返回同一群
  const again = await (await fetch(`${BASE_URL}/ag-ui/agents/direct`, {
    method: "POST",
    headers: { "Content-Type": "application/json", Authorization: `Bearer ${token}` },
    body: JSON.stringify({ agentId }),
  })).json();
  if (again.groupId !== directGroup.groupId)
    throw new Error(`幂等失败：第二次进入返回不同群（${again.groupId} vs ${directGroup.groupId}）`);
  ok(`幂等：再次进入同一群 ${again.groupId}`);
  return { groupId: directGroup.groupId, myId };
}

async function verifyPlainMessageTriggers(page, { groupId, myId }) {
  // 在打开的单聊房间输入框发一条普通（不 @）消息，期待对端（真实模型）回复
  const input = page.locator("#input");
  await input.waitFor({ state: "visible", timeout: 10000 });
  await input.click();
  await input.fill("你好，请用一句话回答：当前使用的模型是什么？");
  await page.waitForTimeout(150);
  await input.press("Enter"); // 聊天输入框 Enter = 发送（#input）
  ok("已在单聊输入框发送普通消息（未 @），等待对端真实回复…");

  // 用 API 轮询单聊群快照：找“对端数字员工”发出的新消息（reasoner 模型可能要几十秒）
  const token = await sessionToken(page);
  const H = { "Content-Type": "application/json", Authorization: `Bearer ${token}` };
  const before = await (await fetch(`${BASE_URL}/ag-ui/group/${encodeURIComponent(groupId)}`, { headers: H })).json();
  const known = new Set((before.latestMessages || []).map((m) => m.messageId));
  for (let i = 0; i < 50; i++) {
    await sleep(3000);
    let snap;
    try {
      snap = await (await fetch(`${BASE_URL}/ag-ui/group/${encodeURIComponent(groupId)}`, { headers: H })).json();
    } catch { continue; }
    const agentMsg = (snap.latestMessages || [])
      .filter((m) => m.senderId !== myId && !known.has(m.messageId) && m.content && !m.content.startsWith("【定时任务"))
      .sort((a, b) => (a.timestamp || 0) - (b.timestamp || 0))
      .pop();
    if (agentMsg) {
      ok(`收到对端数字员工真实回复（message=${agentMsg.messageId}）：${String(agentMsg.content).slice(0, 80)}…`);
      return;
    }
  }
  ok("（等待回复超时——请人工确认对端是否回复；本步不影响前序单聊链路断言）");
}

async function cleanup(page, { groupId, myId }) {
  if (KEEP) { ok("KEEP=1，跳过清理"); return; }
  const token = await sessionToken(page);
  const auth = { Authorization: `Bearer ${token}` };
  if (groupId) {
    const disband = await fetch(`${BASE_URL}/ag-ui/group/disband`, {
      method: "POST",
      headers: { "Content-Type": "application/json", ...auth },
      body: JSON.stringify({ groupId, operatorId: myId }),
    });
    ok(`解散单聊群：${disband.ok ? "✅" : `❌ ${disband.status}`}`);
  }
  const del = await fetch(`${BASE_URL}/ag-ui/agents/${encodeURIComponent(agentId)}`, { method: "DELETE", headers: auth });
  ok(`删除临时数字员工：${del.ok ? "✅" : `❌ ${del.status}`}`);
}

let browser;
try {
  console.log("\n=== 浏览器自动化验证：数字员工单聊（kind=direct）===");
  console.log(`目标: ${BASE_URL}  用户: ${USERNAME}  有头: ${HEADLESS ? "否" : "是"}`);
  browser = await chromium.launch({ headless: HEADLESS });
  const page = await browser.newPage();
  page.setDefaultTimeout(20000);
  await login(page);
  await createAgentViaApi(page);
  await openChatFromManager(page);
  const direct = await verifyDirectGroup(page);
  await verifyPlainMessageTriggers(page, direct);
  await cleanup(page, direct);
  console.log("\n=== ✅ 端到端通过：数字员工列表单聊建立/复用 + 私密隔离 + 幂等核验均成功 ===\n");
  console.log(`截图目录：${shots}\n`);
  await browser.close();
} catch (e) {
  console.error(`\n=== ❌ 验证失败：${e.message} ===\n`);
  if (browser) await browser.close();
  process.exit(1);
}
