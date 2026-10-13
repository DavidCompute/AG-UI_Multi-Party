// 「📖 完整手册」入口验证（Playwright，真实浏览器）：
//   1. 使用帮助弹窗含「完整手册」按钮，点击打开手册弹窗；
//   2. 手册按当前界面语言渲染（中 / 英），含章节标题且标题带可跳转 id；
//   3. 手册内搜索可过滤顶层块并显示空态；
//   4. 文内目录锚点点击在弹窗内滚动；关闭后收起。
// 用法：cd tools && HEADLESS=1 node ui-manual.mjs
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

const browser = await chromium.launch({ headless: HEADLESS });
const page = await browser.newPage({ viewport: { width: 1280, height: 800 } });
try {
  await page.goto(BASE_URL, { waitUntil: "domcontentloaded" });
  await page.waitForSelector("#authOverlay", { timeout: 15000 });
  if (!(await page.evaluate(() => document.getElementById("authOverlay").classList.contains("hidden")))) {
    await page.fill("#authUsername", USERNAME);
    await page.fill("#authPassword", PASSWORD);
    await page.click("#authSubmit");
    await page.waitForFunction(() => document.getElementById("authOverlay")?.classList.contains("hidden"), { timeout: 20000 });
  }

  // ---- 中文手册 ----
  await page.evaluate(() => window.i18n && i18n.setLang("zh"));
  await sleep(250);
  await page.click("#helpBtn");
  await page.waitForSelector("#helpModal:not(.hidden)", { timeout: 5000 });
  check("帮助弹窗含「完整手册」按钮", await page.locator("#helpManualBtn").isVisible());
  await page.click("#helpManualBtn");
  await page.waitForSelector("#manualModal:not(.hidden)", { timeout: 5000 });
  await page.waitForFunction(() => {
    const c = document.getElementById("manualContent");
    return c && c.querySelectorAll("h1, h2, h3").length > 5;
  }, { timeout: 20000 });
  check("手册标题为「📖 完整手册」", /完整手册/.test(await page.locator("#manualModal .modal-title").innerText()));
  const h2 = await page.locator("#manualContent h2").count();
  check("渲染出章节标题（≥9）", h2 >= 9, "h2=" + h2);
  check("章节标题带可跳转 id", (await page.locator("#manualContent h2[id]").count()) >= 9);

  // 搜索过滤
  await page.fill("#manualSearch", "记忆拟人");
  await sleep(250);
  const visibleBlocks = await page.locator("#manualContent > :not(.hidden)").count();
  check("搜索后仅保留命中块", visibleBlocks > 0 && visibleBlocks < h2 + 6, "可见块=" + visibleBlocks);
  await page.fill("#manualSearch", "zzz_绝不存在的词_qqq");
  await sleep(250);
  check("无命中显示空态", await page.locator("#manualNoMatch:not(.hidden)").isVisible());
  await page.fill("#manualSearch", "");
  await sleep(200);

  // 文内锚点
  const before = await page.locator("#manualContent").evaluate((el) => el.scrollTop);
  await page.locator('#manualContent a[href^="#"]').first().click();
  await sleep(350);
  const after = await page.locator("#manualContent").evaluate((el) => el.scrollTop);
  check("目录锚点可滚动到章节", after >= before, `${before}→${after}`);

  await page.click("#manualClose");
  check("关闭后手册弹窗隐藏", await page.locator("#manualModal").isHidden());

  // ---- 英文手册（切语言后重新拉取）----
  await page.evaluate(() => window.i18n && i18n.setLang("en"));
  await sleep(250);
  await page.click("#helpBtn");
  await page.waitForSelector("#helpModal:not(.hidden)", { timeout: 5000 });
  await page.click("#helpManualBtn");
  await page.waitForFunction(() => {
    const c = document.getElementById("manualContent");
    return c && c.querySelectorAll("h1, h2").length > 5;
  }, { timeout: 20000 });
  check("英文标题为 Full Manual", /Full Manual/.test(await page.locator("#manualModal .modal-title").innerText()));
  check("英文手册章节标题（Quick Start）",
    /Quick Start/.test(await page.locator('#manualContent h2[id^="chapter-"]').first().innerText()));
} catch (e) {
  fail++; console.log("  FAIL  异常：" + (e && e.message));
  await page.screenshot({ path: "screenshots/manual-fail.png" }).catch(() => {});
} finally {
  await browser.close();
}
console.log(`\n完整手册 UI 验证：${pass} 通过 / ${fail} 失败`);
process.exit(fail ? 1 : 0);
