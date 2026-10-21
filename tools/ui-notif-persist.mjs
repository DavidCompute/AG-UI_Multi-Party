// 通知本地持久化回归（Playwright，真实浏览器）：
//   1. 新通知写入 localStorage（按用户隔离），刷新后徽标 / 列表恢复；
//   2. 「清空」同时删除本地副本，再次刷新仍为空；
//   3. 超过 7 天的条目在恢复时被丢弃；
//   4. 无页面 JS 异常。
// 用法：cd tools && HEADLESS=1 node ui-notif-persist.mjs
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

const stored = () => page.evaluate(() => {
  try { return JSON.parse(localStorage.getItem("agui.notifications." + state.memberId) || "null"); } catch { return null; }
});

try {
  await page.goto(BASE_URL, { waitUntil: "domcontentloaded" });
  await waitLoggedIn();
  await page.evaluate(() => clearNotifications()); // 干净起点（同时清掉本地副本）

  // 1) 注入两条并落盘
  await page.evaluate(() => {
    addNotification("mention", "持久-提及", "p1");
    addNotification("error", "持久-错误", "p2");
    saveNotificationsNow();
  });
  check("徽标 = 2", (await page.locator("#notifBadge").innerText()).trim() === "2");
  const data1 = await stored();
  check("localStorage 写入两条", !!data1 && data1.items.length === 2, "items=" + (data1?.items?.length ?? 0));

  // 2) 刷新恢复
  await page.reload({ waitUntil: "domcontentloaded" });
  await waitLoggedIn();
  await page.waitForFunction(() => state.notifications.length >= 2, { timeout: 10000 });
  check("刷新后通知恢复（徽标 2）", (await page.locator("#notifBadge").innerText()).trim() === "2");
  await page.click("#notifBtn");
  await page.waitForSelector("#notifPanel:not(.hidden)", { timeout: 5000 });
  check("刷新后列表 2 条", (await page.locator("#notifList .notif-item").count()) === 2);
  const text = await page.locator("#notifList").innerText();
  check("刷新后内容一致", text.includes("持久-提及") && text.includes("持久-错误"));

  // 3) 清空 → 本地副本删除 → 再刷新仍为空
  await page.click("#notifClear");
  check("清空后 localStorage 副本已删除", (await stored()) === null);
  await page.reload({ waitUntil: "domcontentloaded" });
  await waitLoggedIn();
  await page.waitForTimeout(800);
  check("再次刷新仍为空", (await page.evaluate(() => state.notifications.length)) === 0);
  check("空态徽标隐藏", await page.locator("#notifBadge").evaluate((el) => el.classList.contains("hidden")));

  // 4) 超过 7 天的条目恢复时被丢弃
  await page.evaluate(() => {
    const key = "agui.notifications." + state.memberId;
    const now = Date.now();
    localStorage.setItem(key, JSON.stringify({ seq: 20, items: [
      { id: "n20", type: "info", title: "旧的（8 天前）", body: "", ts: now - 8 * 24 * 3600 * 1000, read: false, icon: "ℹ️" },
      { id: "n21", type: "info", title: "新的（刚发生）", body: "", ts: now - 1000, read: false, icon: "ℹ️" },
    ] }));
  });
  await page.reload({ waitUntil: "domcontentloaded" });
  await waitLoggedIn();
  await page.waitForFunction(() => state.notifications.length === 1, { timeout: 10000 });
  const kept = await page.evaluate(() => state.notifications.map((n) => n.title));
  check("超过 7 天被丢弃", kept.length === 1 && kept[0].includes("新的"), kept.join(","));
  check("徽标只计保留的 1 条", (await page.locator("#notifBadge").innerText()).trim() === "1");

  await page.evaluate(() => clearNotifications()); // 收尾
  check("无页面 JS 异常", errors.length === 0, errors.slice(0, 3).join(" ; "));
} catch (e) {
  fail++;
  console.log("  FAIL  异常：" + (e && e.message));
  await page.screenshot({ path: "screenshots/notif-persist-fail.png" }).catch(() => {});
} finally {
  await browser.close();
}
console.log(`\n通知本地持久化验证：${pass} 通过 / ${fail} 失败`);
process.exit(fail ? 1 : 0);
