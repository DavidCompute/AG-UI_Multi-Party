// 桌面通知免打扰 / 按类型静音回归（Playwright，真实浏览器）：
//   1. 免打扰窗口解析："HH:mm" 读取、非法值忽略；
//   2. isQuietNow 判定：同日窗口 / 跨午夜窗口 / 起止相等（= 不启用）/ 未设（= 不启用）；
//   3. 静音类型：从 /ag-ui/notify-quiet 读取，未知类型被过滤，isTypeMuted 只对桌面通知生效；
//   4. 配置页包含开始 / 结束与各类型勾选框；
//   5. 无页面 JS 异常。
// 用法：cd tools && HEADLESS=1 node ui-notify-quiet.mjs
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

// 全局免打扰窗口桩：22:00 → 07:00（跨午夜）；静音 error（另带一个未知类型，应被过滤）
await page.route("**/ag-ui/notify-quiet", (route) => route.fulfill({ json: { start: "22:00", end: "07:00", mutedTypes: ["error", "bogus"] } }));

try {
  await page.goto(BASE_URL, { waitUntil: "domcontentloaded" });
  await page.waitForSelector("#authOverlay", { timeout: 15000 });
  if (!(await page.evaluate(() => document.getElementById("authOverlay")?.classList.contains("hidden")))) {
    await page.fill("#authUsername", USERNAME);
    await page.fill("#authPassword", PASSWORD);
    await page.click("#authSubmit");
    await page.waitForFunction(() => document.getElementById("authOverlay")?.classList.contains("hidden"), { timeout: 20000 });
  }

  // 1) 登录后读取全局窗口（22*60 / 7*60）
  await page.waitForFunction(() => notifyQuiet.start === 22 * 60 && notifyQuiet.end === 7 * 60, { timeout: 5000 });
  check("登录后读取免打扰窗口", true, "22:00 → 07:00");

  // 1.1) 静音类型（未知值被过滤）
  check("登录后读取静音类型", await page.evaluate(() => notifyMuted.has("error") && notifyMuted.size === 1), await page.evaluate(() => [...notifyMuted].join(",")));
  check("isTypeMuted 只命中已静音类型", await page.evaluate(() => isTypeMuted("error") && !isTypeMuted("mention") && !isTypeMuted(undefined)));

  // 2) 解析规则
  check("parseHhMm 合法值", await page.evaluate(() => parseHhMm("09:30") === 9 * 60 + 30));
  check("parseHhMm 非法值 → null", await page.evaluate(() => parseHhMm("25:00") === null && parseHhMm("7:00") === null && parseHhMm("") === null));

  // 3) 跨午夜窗口
  const crossMidnight = await page.evaluate(() => {
    const at = (h, m) => new Date(2026, 0, 1, h, m);
    return {
      late: isQuietNow(at(23, 30)),
      beforeEnd: isQuietNow(at(6, 59)),
      atEnd: isQuietNow(at(7, 0)),
      noon: isQuietNow(at(12, 0)),
      atStart: isQuietNow(at(22, 0)),
    };
  });
  check("跨午夜：23:30 / 06:59 / 22:00 安静", crossMidnight.late && crossMidnight.beforeEnd && crossMidnight.atStart);
  check("跨午夜：07:00 / 12:00 不安静", !crossMidnight.atEnd && !crossMidnight.noon);

  // 4) 同日窗口 + 边界（含起止）
  const sameDay = await page.evaluate(() => {
    notifyQuiet.start = 9 * 60;
    notifyQuiet.end = 17 * 60;
    const at = (h, m) => new Date(2026, 0, 1, h, m);
    return { in: isQuietNow(at(12, 0)), start: isQuietNow(at(9, 0)), end: isQuietNow(at(17, 0)), out: isQuietNow(at(18, 0)) };
  });
  check("同日窗口：09:00–17:00 命中 12:00 / 09:00，不命中 17:00 / 18:00", sameDay.in && sameDay.start && !sameDay.end && !sameDay.out);

  // 5) 起止相等 / 未设 → 不启用
  const disabled = await page.evaluate(() => {
    notifyQuiet.start = 10 * 60; notifyQuiet.end = 10 * 60;
    const same = isQuietNow(new Date(2026, 0, 1, 10, 0));
    notifyQuiet.start = null; notifyQuiet.end = null;
    const unset = isQuietNow(new Date(2026, 0, 1, 10, 0));
    return { same, unset };
  });
  check("起止相等 / 未设 → 不启用", !disabled.same && !disabled.unset);

  // 6) 配置页字段存在
  check("配置页含免打扰开始 / 结束", (await page.locator("#cfgQuietStart").count()) === 1 && (await page.locator("#cfgQuietEnd").count()) === 1);
  const muteIds = ["#cfgMuteMention", "#cfgMuteApproval", "#cfgMuteError", "#cfgMuteInfo", "#cfgMuteMessage", "#cfgMuteReconnect"];
  let muteCount = 0;
  for (const id of muteIds) muteCount += await page.locator(id).count();
  check("配置页含 6 个类型勾选框", muteCount === 6, "count=" + muteCount);

  // 7) 无页面 JS 异常
  check("无页面 JS 异常", errors.length === 0, errors.slice(0, 3).join(" ; "));
} catch (e) {
  fail++;
  console.log("  FAIL  异常：" + (e && e.message));
  await page.screenshot({ path: "screenshots/notify-quiet-fail.png" }).catch(() => {});
} finally {
  await browser.close();
}
console.log(`\n桌面通知免打扰 / 静音验证：${pass} 通过 / ${fail} 失败`);
process.exit(fail ? 1 : 0);
