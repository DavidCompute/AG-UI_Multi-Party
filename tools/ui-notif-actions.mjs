// 通知单条操作回归（Playwright，真实浏览器）：
//   1. 悬浮显示「标为未读 / 删除」按钮；点击不触发条目跳转；
//   2. 标为未读 → 徽标 +1、条目带 unread 类、本地副本同步；
//   3. 删除 → 列表与本地副本同时移除；刷新后不复现；
//   4. 技能错误通知删除时同步清理「备查」存储；
//   5. 无页面 JS 异常。
// 用法：cd tools && HEADLESS=1 node ui-notif-actions.mjs
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

const browser = await chromium.launch({ headless: HEADLESS });
const ctx = await browser.newContext({ viewport: { width: 1400, height: 900 } });
const page = await ctx.newPage();
const errors = [];
page.on("pageerror", (e) => errors.push("pageerror: " + e.message));
page.on("console", (m) => { if (m.type() === "error") errors.push("console: " + m.text()); });

async function waitLoggedIn() {
  await page.waitForSelector("#authOverlay", { state: "attached", timeout: 15000 });
  if (!(await page.evaluate(() => document.getElementById("authOverlay")?.classList.contains("hidden")))) {
    await page.fill("#authUsername", USERNAME);
    await page.fill("#authPassword", PASSWORD);
    await page.click("#authSubmit");
    await page.waitForFunction(() => document.getElementById("authOverlay")?.classList.contains("hidden"), { timeout: 20000 });
  }
  await page.waitForFunction(() => !!state.memberId, { timeout: 15000 });
}

const storedItems = () => page.evaluate(() => {
  try { return (JSON.parse(localStorage.getItem("agui.notifications." + state.memberId) || "null")?.items) || []; }
  catch { return []; }
});
const badge = () => page.locator("#notifBadge").innerText();

try {
  await page.goto(BASE_URL, { waitUntil: "domcontentloaded" });
  await waitLoggedIn();
  await page.evaluate(() => clearNotifications());

  await page.evaluate(() => {
    addNotification("mention", "操作-提及", "a1");
    addNotification("message", "操作-消息", "a2");
    saveNotificationsNow();
  });
  await page.click("#notifBtn");
  await page.waitForSelector("#notifPanel:not(.hidden)", { timeout: 5000 });

  const first = page.locator("#notifList .notif-item").first();
  check("默认不显示单条操作", !(await first.locator(".notif-actions").isVisible()));
  await first.hover();
  check("悬浮显示操作按钮", await first.locator(".notif-actions").isVisible());
  check("含「标为未读」与「删除」", (await first.locator('[data-nact="unread"]').count()) === 1 && (await first.locator('[data-nact="del"]').count()) === 1);

  // 标为未读：徽标 +1，本地副本 read=false
  await first.locator('[data-nact="unread"]').click();
  check("标为未读 → 徽标 1", (await badge()).trim() === "1", (await badge()).trim());
  check("条目带 unread 类", await page.locator("#notifList .notif-item").first().evaluate((el) => el.classList.contains("unread")));
  await page.waitForTimeout(700); // 等 500ms 合并写入
  const afterUnread = await storedItems();
  check("本地副本标记已读状态同步", afterUnread.some((n) => n.title === "操作-消息" && n.read === false));

  // 删除第二条：列表与本地副本同时移除
  const second = page.locator("#notifList .notif-item").nth(1);
  await second.hover();
  await second.locator('[data-nact="del"]').click();
  check("删除后列表剩 1 条", (await page.locator("#notifList .notif-item").count()) === 1);
  await page.waitForTimeout(700);
  const afterDel = await storedItems();
  check("删除后本地副本剩 1 条（保留未被删的）", afterDel.length === 1 && afterDel[0].title === "操作-消息", "len=" + afterDel.length);

  // 刷新后不复现
  await page.reload({ waitUntil: "domcontentloaded" });
  await waitLoggedIn();
  await page.waitForFunction(() => state.notifications.length === 1, { timeout: 10000 });
  check("刷新后被删条目不复现", (await page.evaluate(() => state.notifications.map((n) => n.title)))[0] === "操作-消息");

  // 技能错误删除同步清理备查存储
  await page.evaluate(() => { notifySkillError("g_test", "skill_demo", "boom"); });
  await page.click("#notifBtn");
  await page.waitForSelector("#notifPanel:not(.hidden)", { timeout: 5000 });
  const errItem = page.locator("#notifList .notif-item").filter({ hasText: "技能执行失败" }).first();
  await errItem.hover();
  await errItem.locator('[data-nact="del"]').click();
  const skillLeft = await page.evaluate(() => {
    const raw = localStorage.getItem("agui.skillError." + state.memberId);
    return raw ? JSON.parse(raw).length : 0;
  });
  check("技能错误删除后备查存储清空", skillLeft === 0, "left=" + skillLeft);

  await page.evaluate(() => clearNotifications()); // 收尾
  check("无页面 JS 异常", errors.length === 0, errors.slice(0, 3).join(" ; "));
} catch (e) {
  fail++;
  console.log("  FAIL  异常：" + (e && e.message));
  await page.screenshot({ path: "screenshots/notif-actions-fail.png" }).catch(() => {});
} finally {
  await browser.close();
}
console.log(`\n通知单条操作验证：${pass} 通过 / ${fail} 失败`);
process.exit(fail ? 1 : 0);
