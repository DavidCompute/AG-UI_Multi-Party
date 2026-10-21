// 通知中心按类型筛选回归（Playwright，真实浏览器）：
//   1. 无筛选时展示全部；徽标未读数不受筛选影响；
//   2. 提及 / 审批 / 错误 / 消息 / 其他 各自过滤正确（其他 = 未归类类型，如 reconnect）；
//   3. 无匹配时显示「没有该类型的通知」而非「暂无通知」；
//   4. 选中 chip 有 .on 态；回到「全部」恢复；
//   5. 无页面 JS 异常。
// 通知用 addNotification 直接注入（不依赖服务端事件）。
// 用法：cd tools && HEADLESS=1 node ui-notif-filter.mjs
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

const items = () => page.locator("#notifList .notif-item");
const pick = async (nf) => { await page.click(`#notifFilters button[data-nf="${nf}"]`); };

try {
  await page.goto(BASE_URL, { waitUntil: "domcontentloaded" });
  await page.waitForSelector("#authOverlay", { timeout: 15000 });
  if (!(await page.evaluate(() => document.getElementById("authOverlay")?.classList.contains("hidden")))) {
    await page.fill("#authUsername", USERNAME);
    await page.fill("#authPassword", PASSWORD);
    await page.click("#authSubmit");
    await page.waitForFunction(() => document.getElementById("authOverlay")?.classList.contains("hidden"), { timeout: 20000 });
  }

  // 注入 4 条不同类型的通知
  await page.evaluate(() => {
    addNotification("mention", "被提及", "m1");
    addNotification("error", "出错了", "e1");
    addNotification("message", "新消息", "m2");
    addNotification("reconnect", "连接恢复", "r1");
  });
  check("徽标未读数 = 4（不受筛选影响）", (await page.locator("#notifBadge").innerText()).trim() === "4");

  await page.click("#notifBtn");
  await page.waitForSelector("#notifPanel:not(.hidden)", { timeout: 5000 });
  check("默认「全部」4 条", (await items().count()) === 4, "count=" + (await items().count()));
  check("「全部」chip 选中", await page.locator('#notifFilters button[data-nf=""]').evaluate((el) => el.classList.contains("on")));

  await pick("mention");
  check("提及 → 1 条", (await items().count()) === 1 && (await page.locator("#notifList").innerText()).includes("被提及"));
  await pick("error");
  check("错误 → 1 条", (await items().count()) === 1 && (await page.locator("#notifList").innerText()).includes("出错了"));
  await pick("message");
  check("消息 → 1 条", (await items().count()) === 1 && (await page.locator("#notifList").innerText()).includes("新消息"));
  await pick("other");
  check("其他 → 1 条（reconnect 归入其他）", (await items().count()) === 1 && (await page.locator("#notifList").innerText()).includes("连接恢复"));

  await pick("approval");
  check("审批（无数据）→ 0 条", (await items().count()) === 0);
  check("无匹配显示提示而非「暂无通知」", await page.locator("#notifNoMatch").isVisible() && !(await page.locator("#notifEmpty").isVisible()));
  check("筛选 chip 选中态跟随", await page.locator('#notifFilters button[data-nf="approval"]').evaluate((el) => el.classList.contains("on")));

  await pick("");
  check("回到「全部」恢复 4 条", (await items().count()) === 4);
  check("无页面 JS 异常", errors.length === 0, errors.slice(0, 3).join(" ; "));
} catch (e) {
  fail++;
  console.log("  FAIL  异常：" + (e && e.message));
  await page.screenshot({ path: "screenshots/notif-filter-fail.png" }).catch(() => {});
} finally {
  await browser.close();
}
console.log(`\n通知中心类型筛选验证：${pass} 通过 / ${fail} 失败`);
process.exit(fail ? 1 : 0);
