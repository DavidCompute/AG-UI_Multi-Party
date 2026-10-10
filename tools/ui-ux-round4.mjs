// 界面人性化第四轮验证（Playwright，真实浏览器）：
//   1. 消息「⋯」更多操作菜单（复制 / 撤回等收入其中；点外部 / 滚动收起）；
//   2. 话题栏可收起 / 展开并持久化；
//   3. 快捷键面板（? 唤起 / Esc 关闭 / 帮助里入口）；
//   4. 知聚行悬停显示跨话题未读汇总；
//   5. 「上次离开后」分隔线。
//
// 用法：cd tools && HEADLESS=1 node ui-ux-round4.mjs
import { chromium } from "playwright";

const BASE_URL = process.env.BASE_URL || "http://localhost:5200";
const USERNAME = process.env.USERNAME || "david";
const PASSWORD = process.env.PASSWORD || "lingtong";
const HEADLESS = process.env.HEADLESS === "1";

let pass = 0, fail = 0;
const check = (label, cond, extra = "") => {
  if (cond) { pass++; console.log("  PASS  " + label + (extra ? "  — " + extra : "")); }
  else { fail++; console.log("  FAIL  " + label + (extra ? "  — " + extra : "")); }
};
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

const stamp = Date.now().toString(36).slice(-5);
const gName = "R4-" + stamp;
const browser = await chromium.launch({ headless: HEADLESS });
const page = await browser.newPage({ viewport: { width: 1366, height: 800 } });
const errors = [];
page.on("pageerror", (e) => errors.push(String(e.message)));
page.on("console", (m) => { if (m.type() === "error") errors.push("console: " + m.text()); });

let gid = null, token = null, userId = null;

try {
  await page.goto(BASE_URL, { waitUntil: "domcontentloaded" });
  await page.waitForSelector("#authOverlay", { timeout: 15000 });
  if (!(await page.evaluate(() => document.getElementById("authOverlay").classList.contains("hidden")))) {
    await page.fill("#authUsername", USERNAME); await page.fill("#authPassword", PASSWORD); await page.click("#authSubmit");
    await page.waitForFunction(() => document.getElementById("authOverlay")?.classList.contains("hidden"), { timeout: 20000 });
  }
  token = (await (await page.request.post(BASE_URL + "/ag-ui/user/login", { data: { username: USERNAME, password: PASSWORD } })).json()).token;
  const H = { Authorization: "Bearer " + token, "Content-Type": "application/json" };
  const api = async (method, p, data) => {
    const r = await page.request.fetch(BASE_URL + p, { method, headers: H, data: data === undefined ? undefined : JSON.stringify(data) });
    return { status: r.status(), body: await r.json().catch(() => null) };
  };
  userId = (await api("GET", "/ag-ui/user/me")).body.userId;
  gid = (await api("POST", "/ag-ui/group/create", { groupName: gName, ownerId: userId, memberIds: [], members: [] })).body.groupId;
  for (let i = 1; i <= 12; i++) {
    await api("POST", "/ag-ui/group/message/send", { groupId: gid, userId, content: `R4 消息 ${i}` });
    await sleep(30); // 拉开发送时间，便于「上次离开后」分隔线定位
  }

  await page.reload({ waitUntil: "domcontentloaded" });
  await page.waitForSelector(".group-item", { timeout: 20000 });
  // 清掉历史偏好
  await page.evaluate(() => Object.keys(localStorage).filter((k) => k.startsWith("agui.topicBarCollapsed.")).forEach((k) => localStorage.removeItem(k)));
  await page.locator(".group-item", { hasText: gName }).first().click();
  await page.waitForFunction(() => document.querySelectorAll("#messages .msg").length >= 12, { timeout: 15000 });
  await sleep(400);

  // ---- 1) 消息「⋯」更多操作菜单 ----
  const menuHidden = await page.evaluate(() => {
    const m = document.querySelector("#messages .msg");
    return { hasActions: !!m.querySelector(".msg-actions"), hasCopy: !!m.querySelector(".msg-actions .copy-btn"), hidden: m.querySelector(".msg-actions").classList.contains("hidden") };
  });
  check("消息头有「⋯」容器且含复制按钮", menuHidden.hasActions && menuHidden.hasCopy);
  check("菜单默认隐藏", menuHidden.hidden);
  await page.evaluate(() => { const m = document.querySelector("#messages .msg"); m.querySelector(".msg-more-btn").click(); });
  await sleep(150);
  const opened = await page.evaluate(() => {
    const m = document.querySelector("#messages .msg");
    return { open: !m.querySelector(".msg-actions").classList.contains("hidden"), expanded: m.querySelector(".msg-more-btn").getAttribute("aria-expanded"), copyVisible: m.querySelector(".msg-actions .copy-btn").checkVisibility?.() ?? true };
  });
  check("点「⋯」展开菜单", opened.open && opened.expanded === "true");
  check("菜单内按钮可见（不受悬停消隐影响）", opened.copyVisible);
  await page.locator("#messages").click({ position: { x: 5, y: 5 } });
  await sleep(150);
  check("点外部收起菜单", await page.evaluate(() => document.querySelector("#messages .msg .msg-actions").classList.contains("hidden")));

  // ---- 2) 话题栏收起 / 展开 ----
  const tb0 = await page.evaluate(() => !!document.querySelector("#topicBar .topic-collapse-btn"));
  check("话题栏有折叠按钮", tb0);
  await page.locator("#topicBar .topic-collapse-btn").click();
  await sleep(200);
  const tb1 = await page.evaluate(() => ({
    cls: document.getElementById("topicBar").classList.contains("collapsed"),
    newBtnVisible: !!document.querySelector("#topicBar .topic-new-btn:not([hidden])") && !!document.querySelector("#topicBar .topic-new-btn")?.checkVisibility?.(),
    mainVisible: !!document.querySelector("#topicBar .topic-chip.active")?.checkVisibility?.(),
    saved: localStorage.getItem(Object.keys(localStorage).find((k) => k.startsWith("agui.topicBarCollapsed."))) === "1",
  }));
  check("点折叠 → 话题栏进入收起态", tb1.cls);
  check("收起后当前话题仍可见、新建按钮隐藏", tb1.mainVisible && !tb1.newBtnVisible, JSON.stringify(tb1));
  check("收起状态已持久化", tb1.saved);
  await page.locator("#topicBar .topic-collapse-btn").click();
  await sleep(200);
  check("再点展开恢复", !(await page.evaluate(() => document.getElementById("topicBar").classList.contains("collapsed"))));

  // ---- 3) 快捷键面板 ----
  await page.evaluate(() => document.getElementById("input").blur());
  await page.keyboard.press("?");
  await sleep(200);
  check("按 ? 打开快捷键面板", await page.locator("#shortcutsModal").isVisible());
  check("面板列出多条快捷键", (await page.locator("#shortcutsModal .sc-row").count()) >= 6);
  await page.keyboard.press("Escape");
  await sleep(200);
  check("Esc 关闭快捷键面板", !(await page.locator("#shortcutsModal").isVisible()));
  await page.locator("#helpBtn").click();
  await page.waitForSelector("#helpModal:not(.hidden)", { timeout: 5000 });
  await page.locator("#helpShortcutsBtn").click();
  await sleep(200);
  check("帮助里可打开快捷键面板", await page.locator("#shortcutsModal").isVisible());
  await page.locator("#scClose").click();
  await sleep(150);

  // ---- 4) 跨话题未读汇总（悬停知聚行） ----
  const tip = await page.evaluate(() => {
    const g = state.groups.find((x) => x.groupId === state.activeGroupId);
    state.groupUnread.set(g.groupId, { lastMessageAt: Date.now(), unreadCount: 3, byTopic: { main: 3 } });
    renderGroupList();
    const row = document.querySelector(`#groupList .group-item.active`) || document.querySelector("#groupList .group-item");
    return row.title;
  });
  check("知聚行 title 含跨话题未读明细", /3/.test(tip) && tip.includes("\n"), JSON.stringify(tip));

  // ---- 5) 「上次离开后」分隔线 ----
  const leaveInfo = await page.evaluate(() => {
    const r = room(state.activeGroupId);
    const msgs = r.messages.filter((m) => !m.sys);
    const since = Number(msgs[5].timestamp) + 1; // 第 6 条之后
    pendingLeaveDivider = { gid: state.activeGroupId, since };
    unreadDividerId = null; leaveDividerId = null;
    vscroll.force = true; virtualRender();
    const marker = document.querySelector("#messages .unread-divider.leave");
    return { has: !!marker, text: marker?.textContent || "", targetId: leaveDividerId, expectId: msgs[6]?.id };
  });
  check("显示「上次离开后」分隔线", leaveInfo.has, leaveInfo.text);
  check("分隔线落在离开时间后的首条消息", leaveInfo.targetId === leaveInfo.expectId, `${leaveInfo.targetId} vs ${leaveInfo.expectId}`);

  check("无控制台报错", errors.length === 0, errors.slice(0, 3).join(" | "));
  console.log(`\n=== ${pass} 通过 / ${fail} 失败 ===`);
} catch (e) {
  fail++;
  console.error("\n界面验证异常：", e.message);
} finally {
  try {
    if (gid && token) await page.request.fetch(BASE_URL + "/ag-ui/group/disband", {
      method: "POST", headers: { Authorization: "Bearer " + token, "Content-Type": "application/json" },
      data: JSON.stringify({ groupId: gid, operatorId: null }),
    });
  } catch { /* 忽略 */ }
  await browser.close();
  process.exit(fail === 0 ? 0 : 1);
}
