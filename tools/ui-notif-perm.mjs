// 桌面通知权限引导回归（Playwright，真实浏览器）：
//   1. 未授权（default）→ 通知面板顶部显示提示与「开启」；
//   2. 手动关闭（×）→ 隐藏并写入 7 天重提醒标记；
//   3. 点「开启」→ 授权后提示消失并 toast「已开启」；
//   4. 已被拒绝（denied）→ 显示说明，不显示「开启」（再点也不会弹授权框）；
//   5. 不支持（无 Notification）→ 不显示提示；
//   6. 无页面 JS 异常。
// Notification 权限用 initScript 桩模拟（不依赖真实浏览器权限）。
// 用法：cd tools && HEADLESS=1 node ui-notif-perm.mjs
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
const errors = [];

async function openPage(mode) {
  const page = await ctx.newPage();
  page.on("pageerror", (e) => errors.push("pageerror: " + e.message));
  page.on("console", (m) => { if (m.type() === "error") errors.push("console: " + m.text()); });
  await page.addInitScript((m) => {
    if (m === "unsupported") { try { delete window.Notification; } catch { /* ignore */ } return; }
    class FakeNotification {
      static permission = m; // "default" | "denied"
      static requestPermission = async () => { FakeNotification.permission = "granted"; return "granted"; };
      constructor() { this.onclick = null; }
      close() {}
    }
    window.Notification = FakeNotification;
  }, mode);
  await page.goto(BASE_URL, { waitUntil: "domcontentloaded" });
  await page.waitForSelector("#authOverlay", { state: "attached", timeout: 15000 });
  if (!(await page.evaluate(() => document.getElementById("authOverlay")?.classList.contains("hidden")))) {
    await page.fill("#authUsername", USERNAME);
    await page.fill("#authPassword", PASSWORD);
    await page.click("#authSubmit");
    await page.waitForFunction(() => document.getElementById("authOverlay")?.classList.contains("hidden"), { timeout: 20000 });
  }
  return page;
}

try {
  // ---- A. default → 显示提示，可关闭 / 可开启 ----
  const a = await openPage("default");
  await a.click("#notifBtn");
  await a.waitForSelector("#notifPanel:not(.hidden)", { timeout: 5000 });
  await a.waitForSelector("#notifPermHint:not(.hidden)", { timeout: 5000 });
  const hintA = (await a.locator("#notifPermText").innerText()).trim();
  check("未授权时显示引导提示", hintA.includes("未开启"), hintA);
  check("未授权时显示「开启」", await a.locator("#notifPermBtn").isVisible());

  // 「×」关闭 → 隐藏并写入标记
  await a.click("#notifPermDismiss");
  check("点 × 后提示隐藏", await a.locator("#notifPermHint").evaluate((el) => el.classList.contains("hidden")));
  check("写入 7 天重提醒标记", await a.evaluate(() => Number(localStorage.getItem("agui.notifPermDismissedAt") || 0) > 0));

  // 清除标记后重新评估 → 提示回来；点「开启」→ 授权成功
  await a.evaluate(() => { localStorage.removeItem("agui.notifPermDismissedAt"); renderNotifPermHint(); });
  await a.waitForSelector("#notifPermHint:not(.hidden)", { timeout: 3000 });
  await a.click("#notifPermBtn");
  await a.waitForFunction(() => document.getElementById("notifPermHint")?.classList.contains("hidden"), { timeout: 5000 });
  check("授权后提示消失", true);
  check("授权后 toast 提示", (await a.locator("#toast").innerText()).includes("已开启"), (await a.locator("#toast").innerText()).trim());

  // ---- B. denied → 说明 + 无「开启」 ----
  const b = await openPage("denied");
  await b.click("#notifBtn");
  await b.waitForSelector("#notifPermHint:not(.hidden)", { timeout: 5000 });
  const hintB = (await b.locator("#notifPermText").innerText()).trim();
  check("已拒绝时显示站点设置说明", hintB.includes("拒绝"), hintB);
  check("已拒绝时不显示「开启」", !(await b.locator("#notifPermBtn").isVisible()));

  // ---- C. 不支持 → 不显示 ----
  const c = await openPage("unsupported");
  check("环境无 Notification 时检测为 unsupported", await c.evaluate(() => !("Notification" in window)));
  await c.click("#notifBtn");
  await c.waitForSelector("#notifPanel:not(.hidden)", { timeout: 5000 });
  check("不支持时不显示提示", await c.locator("#notifPermHint").evaluate((el) => el.classList.contains("hidden")));

  check("无页面 JS 异常", errors.length === 0, errors.slice(0, 3).join(" ; "));
} catch (e) {
  fail++;
  console.log("  FAIL  异常：" + (e && e.message));
} finally {
  await browser.close();
}
console.log(`\n桌面通知权限引导验证：${pass} 通过 / ${fail} 失败`);
process.exit(fail ? 1 : 0);
