// 管理员控制台界面（布局）回归（Playwright，真实浏览器）：
//   1. 账户菜单的管理入口文案为「管理控制台」（不再是语义不符的「用户管理」）；
//   2. 控制台固定大小（≈视口高 - 48），内容区 `.admin-body` 内部滚动、标题/页签/按钮固定；
//   3. 页签过多时自动换行、不横向溢出（11 个页签至少两行，且不撑宽弹窗）。
// 用法：cd tools && HEADLESS=1 node ui-admin-console-layout.mjs
import { chromium } from "playwright";

const BASE_URL = process.env.BASE_URL || "http://localhost:5200";
const ADMIN_USER = process.env.USERNAME || "david";
const ADMIN_PASS = process.env.PASSWORD || "lingtong";
const HEADLESS = process.env.HEADLESS === "1";

let pass = 0, fail = 0;
const check = (label, cond, extra = "") => {
  if (cond) { pass++; console.log("  PASS  " + label + (extra ? "  — " + extra : "")); }
  else { fail++; console.log("  FAIL  " + label + (extra ? "  — " + extra : "")); }
};

const browser = await chromium.launch({ headless: HEADLESS });
const ctx = await browser.newContext({ viewport: { width: 1280, height: 800 } });
const page = await ctx.newPage();

try {
  await page.goto(BASE_URL, { waitUntil: "domcontentloaded" });
  await page.waitForSelector("#authOverlay", { timeout: 15000 });
  if (!(await page.evaluate(() => document.getElementById("authOverlay")?.classList.contains("hidden")))) {
    await page.fill("#authUsername", ADMIN_USER);
    await page.fill("#authPassword", ADMIN_PASS);
    await page.click("#authSubmit");
    await page.waitForFunction(() => document.getElementById("authOverlay")?.classList.contains("hidden"), { timeout: 20000 });
  }

  // 1) 菜单文案
  await page.click("#meChip");
  const label = (await page.locator("#meMenuAdmin").innerText()).trim();
  check("菜单文案为「管理控制台」", label.includes("管理控制台"), label);
  check("菜单文案不是「用户管理」", !label.includes("用户管理"), label);
  check("data-i18n 为 menu.console", (await page.locator("#meMenuAdmin").getAttribute("data-i18n")) === "menu.console");

  // 打开控制台
  await page.locator("#meMenuAdmin").click();
  await page.waitForSelector("#adminModal:not(.hidden)", { timeout: 5000 });

  // 2) 固定大小 + 内部滚动
  const vh = await page.evaluate(() => window.innerHeight);
  await page.waitForTimeout(500); // 等首个页签的数据渲染稳定后再测（避免拿到布局中间态）
  const box0 = await page.locator(".admin-modal").boundingBox();
  check("控制台高度接近满高且不超出视口", box0 && box0.height >= vh - 80 && box0.height <= vh + 1,
    box0 ? `${Math.round(box0.height)} / ${vh}` : "no box");
  // 切到内容最长的「运行指标」页：高度必须不变（固定大小），内容在 .admin-body 内滚动
  await page.locator("#adminTabMetrics").click();
  await page.waitForTimeout(500);
  const box1 = await page.locator(".admin-modal").boundingBox();
  check("切换页签后高度不变（固定大小）", box1 && box0 && Math.abs(box1.height - box0.height) <= 1,
    box0 && box1 ? `${Math.round(box0.height)} -> ${Math.round(box1.height)}` : "");
  const bodyOverflow = await page.evaluate(() => getComputedStyle(document.querySelector(".admin-body")).overflowY);
  check(".admin-body 内部滚动（overflow-y auto）", bodyOverflow === "auto", bodyOverflow);
  // 固定头/脚：标题与底部按钮在内容滚动后仍在弹窗内（不随内容滚走）
  const titleTop0 = (await page.locator(".admin-modal > .modal-title").boundingBox()).y;
  await page.evaluate(() => { document.querySelector(".admin-body").scrollTop = 400; });
  const titleTop1 = (await page.locator(".admin-modal > .modal-title").boundingBox()).y;
  check("标题在内容滚动后固定不动", Math.abs(titleTop1 - titleTop0) <= 1, `${titleTop0} -> ${titleTop1}`);

  // 3) 页签换行、不横向溢出
  const tabs = await page.evaluate(() => {
    const bar = document.querySelector(".admin-tabs");
    const btns = [...bar.querySelectorAll(".admin-tab")];
    const rows = new Set(btns.map((b) => Math.round(b.getBoundingClientRect().top)));
    return { count: btns.length, rows: rows.size, scrollW: bar.scrollWidth, clientW: bar.clientWidth };
  });
  check("页签数量 ≥ 10", tabs.count >= 10, String(tabs.count));
  check("宽屏下页签条不横向溢出", tabs.scrollW <= tabs.clientW + 2, `${tabs.scrollW} <= ${tabs.clientW}`);
  const modalScrollW = await page.evaluate(() => { const m = document.querySelector(".admin-modal"); return { s: m.scrollWidth, c: m.clientWidth }; });
  check("弹窗不横向溢出", modalScrollW.s <= modalScrollW.c + 2, `${modalScrollW.s} <= ${modalScrollW.c}`);

  // 窄屏：页签应自动换行、仍不溢出（flex-wrap 生效）
  await page.setViewportSize({ width: 820, height: 800 });
  const narrow = await page.evaluate(() => {
    const bar = document.querySelector(".admin-tabs");
    const btns = [...bar.querySelectorAll(".admin-tab")];
    const rows = new Set(btns.map((b) => Math.round(b.getBoundingClientRect().top)));
    const modal = document.querySelector(".admin-modal");
    return { rows: rows.size, barS: bar.scrollWidth, barC: bar.clientWidth, mS: modal.scrollWidth, mC: modal.clientWidth };
  });
  check("窄屏下页签自动换行（≥2 行）", narrow.rows >= 2, `${narrow.rows} 行`);
  check("窄屏下页签条不横向溢出", narrow.barS <= narrow.barC + 2, `${narrow.barS} <= ${narrow.barC}`);
  check("窄屏下弹窗不横向溢出", narrow.mS <= narrow.mC + 2, `${narrow.mS} <= ${narrow.mC}`);
} catch (e) {
  fail++;
  console.log("  FAIL  异常：" + (e && e.message));
  await page.screenshot({ path: "screenshots/admin-console-layout-fail.png" }).catch(() => {});
} finally {
  await browser.close();
}
console.log(`\n管理员控制台布局回归：${pass} 通过 / ${fail} 失败`);
process.exit(fail ? 1 : 0);
