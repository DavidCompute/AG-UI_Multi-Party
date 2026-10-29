// 「记忆质量 / 技能自愈」两个新界面链路的浏览器回归（Playwright）
//
// 目的：这两条链路是最近多个版本新增的（记忆冲突 → 取代；技能修复闭环 → 健康巡检），
//       自动化只能守住后端逻辑，**界面入口 / DOM id / i18n 键 / 渲染**得靠这里守。
//
// 覆盖：
//   1. 记忆管理：「📊 质量概览」真的算出数字（不是空 / 不是 key 泄漏）；
//   2. 记忆管理：「🔍 一致性体检」弹窗能打开、给出结果或空态、能关闭；
//   3. 管理控制台 → 员工绩效 → 「🧪 产物质量」：「🔁 自愈闭环」有 KPI 卡与自动修复状态行。
//
// 用法：
//   cd tools && node ui-quality-chains.mjs            # 默认无头
//   HEADLESS=0 node ui-quality-chains.mjs             # 有头
//   BASE_URL=… USERNAME=… PASSWORD=… node ui-quality-chains.mjs
//
// 退出码：0 全部通过；1 有失败；2 环境 / 登录失败。
import { chromium } from "playwright";

const BASE_URL = (process.env.BASE_URL || "http://127.0.0.1:5200").replace(/\/+$/, "");
const USERNAME = process.env.USERNAME || "david";
const PASSWORD = process.env.PASSWORD || "lingtong";
const HEADLESS = process.env.HEADLESS !== "0";

const failed = [];
let total = 0;
const ok = (m) => console.log(`  [OK] ${m}`);
const check = (name, cond, extra) => {
  total++;
  if (cond) ok(name);
  else { failed.push(name); console.log(`  [FAIL] ${name}${extra ? ` — ${extra}` : ""}`); }
};
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
/** 等待某个条件成立（页面内轮询），返回是否成立。 */
const waitHidden = (sel) => page.waitForFunction((s) => document.querySelector(s)?.classList.contains("hidden"), sel, { timeout: 8000 });

async function waitFor(fn, ms = 8000) {
  const end = Date.now() + ms;
  while (Date.now() < end) {
    try { if (await fn()) return true; } catch { /* 继续等 */ }
    await sleep(200);
  }
  return false;
}

console.log(`# 记忆质量 / 技能自愈界面回归 → ${BASE_URL}`);

const browser = await chromium.launch({ headless: HEADLESS });
const ctx = await browser.newContext({ viewport: { width: 1440, height: 900 } });
const page = await ctx.newPage();
const pageErrors = [];
page.on("pageerror", (e) => pageErrors.push(String(e)));
page.on("console", (m) => {
  if (m.type() !== "error") return;
  const t = m.text();
  // 可选服务未启用导致的 fetch 失败不算缺陷
  if (/favicon|net::ERR_|Failed to load resource|404/i.test(t)) return;
  if (/ReferenceError|TypeError|is not defined|Cannot read/i.test(t)) pageErrors.push(t);
});

try {
  await page.goto(BASE_URL, { waitUntil: "domcontentloaded" });
  await page.waitForSelector("#authOverlay", { timeout: 20000 });
  if (!(await page.evaluate(() => document.getElementById("authOverlay")?.classList.contains("hidden")))) {
    await page.fill("#authUsername", USERNAME);
    await page.fill("#authPassword", PASSWORD);
    await page.click("#authSubmit");
    await page.waitForFunction(() => document.getElementById("authOverlay")?.classList.contains("hidden"), { timeout: 25000 });
  }
  ok("登录成功");

  // ---- 1) 记忆管理：质量概览 ----
  await page.click("#meChip");
  await page.click("#meMenuMemory");
  await page.waitForSelector("#memoryModal:not(.hidden)", { timeout: 8000 });

  const qualityOk = await waitFor(async () => {
    const t = (await page.locator("#memQualityText").innerText()).trim();
    return t.length > 0 && /知聚/.test(t) && !/^memory\./.test(t);
  }, 15000);
  const qualityText = (await page.locator("#memQualityText").innerText()).trim();
  check("质量概览渲染出统计（非空、非 i18n 键泄漏）", qualityOk, qualityText.slice(0, 120));
  check("质量概览包含「已被取代」口径", /已被取代/.test(qualityText), qualityText.slice(0, 120));

  // ---- 2) 一致性体检弹窗 ----
  // 先选一个知聚（体检必须按知聚跑）；从下拉里取第一个非空值
  const picked = await page.evaluate(() => {
    const sel = document.getElementById("memGroupSelect");
    const opt = Array.from(sel.options).find((o) => o.value);
    if (!opt) return "";
    sel.value = opt.value;
    sel.dispatchEvent(new Event("change"));
    return opt.value;
  });
  if (!picked) {
    check("存在可选知聚（体检按知聚运行）", false, "记忆管理里没有任何知聚可体检");
  } else {
    await sleep(600); // 等 change 触发的列表 / 质量刷新
    await page.click("#memConflictBtn");
    await page.waitForSelector("#memConflictModal:not(.hidden)", { timeout: 8000 });
    ok("一致性体检弹窗已打开");

    const ready = await waitFor(async () => {
      const t = (await page.locator("#memConflictList").innerText()).trim();
      return t.length > 0 && !/统计中|loading/i.test(t);
    }, 20000);
    const listText = (await page.locator("#memConflictList").innerText()).trim();
    check("体检给出结果或明确的空态", ready, listText.slice(0, 120));
    check("体检内容不是 i18n 键泄漏", !/^memory\./.test(listText), listText.slice(0, 80));

    await page.click("#memConflictClose");
    await waitHidden("#memConflictModal");
    ok("体检弹窗可关闭");
  }

  await page.click("#memoryClose");
  await waitHidden("#memoryModal");

  // ---- 3) 管理控制台 → 员工绩效 → 产物质量 → 自愈闭环 ----
  await page.click("#meChip");
  await page.click("#meMenuAdmin");
  await page.waitForSelector("#adminModal:not(.hidden)", { timeout: 8000 });
  await page.click("#adminTabPerf");
  await sleep(800);
  await page.click("#perfArtifactBtn");
  await page.waitForSelector("#artifactQualityModal:not(.hidden)", { timeout: 10000 });

  const loopCards = await waitFor(async () =>
    (await page.locator("#aqLoopCards .oh-card").count()) >= 3, 15000);
  const cardCount = await page.locator("#aqLoopCards .oh-card").count();
  check("「🔁 自愈闭环」渲染出 KPI 卡片（≥3）", loopCards, `卡片数 ${cardCount}`);

  const metaOk = await waitFor(async () => {
    const t = (await page.locator("#aqLoopMeta").innerText()).trim();
    return t.length > 0 && /自动修复/.test(t);
  }, 10000);
  const metaText = (await page.locator("#aqLoopMeta").innerText()).trim();
  check("自愈闭环显示「自动修复」状态行", metaOk, metaText.slice(0, 120));

  await page.click("#aqClose");
  await waitHidden("#artifactQualityModal");
  ok("产物质量弹窗可关闭");

  check("全程无前端运行时错误", pageErrors.length === 0, pageErrors.slice(0, 3).join(" | "));
} catch (e) {
  failed.push(`异常：${e.message}`);
  console.log(`  [FAIL] 异常：${e.message}`);
  try { await page.screenshot({ path: "screenshots/quality-chains-fail.png" }); } catch { /* ignore */ }
} finally {
  await browser.close();
}

console.log("");
if (failed.length) {
  console.log(`结果：${failed.length} 项失败 / 共 ${total} 项断言`);
  failed.forEach((f) => console.log(`  - ${f}`));
  process.exit(1);
}
console.log(`结果：全部通过（${total} 项断言）`);
process.exit(0);
