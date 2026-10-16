// KnowGath 前端「控制台报错」回归扫描（Playwright）
//
// 目的：把历史上那类**前端运行时错误**（如 `Uncaught ReferenceError: syncMemoryTypeUi is not defined`）在
//       一次遍历中全部抓出来——登录后依次打开主要界面（账号菜单各弹窗、记忆管理、管理控制台全部页签、
//       数字员工、帮助、全局搜索、通知），全程收集：
//         · pageerror（未捕获异常）→ 一律**失败**
//         · console error（含 ReferenceError / TypeError / is not defined / Cannot read …）→ **失败**
//         · 其它 console error（多为可选服务未开导致的 fetch 失败）→ 仅**提示**
//
// 用法：
//   node tools/ui-smoke.mjs                 # 默认无头，http://127.0.0.1:5200，david/lingtong
//   HEADLESS=0 node tools/ui-smoke.mjs      # 有头（可亲眼看它走一遍）
//   BASE_URL=… USERNAME=… PASSWORD=… node tools/ui-smoke.mjs
//
// 退出码：0 = 无失败项；1 = 有失败项；2 = 环境/登录失败。
import { chromium } from "playwright";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const shots = path.join(__dirname, "screenshots", "smoke");
fs.mkdirSync(shots, { recursive: true });

const BASE_URL = process.env.BASE_URL || "http://127.0.0.1:5200";
const USERNAME = process.env.USERNAME || "david";
const PASSWORD = process.env.PASSWORD || "lingtong";
const HEADLESS = process.env.HEADLESS !== "0";

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
/** 给任意 Promise 加超时（Playwright 的 evaluate / screenshot 不受 setDefaultTimeout 约束，必须自兜）。 */
const withTimeout = (p, ms, label) => Promise.race([
  p,
  new Promise((_, rej) => setTimeout(() => rej(new Error(`timeout(${ms}ms): ${label}`)), ms)),
]);
/** 用 JS 直接 click（不受可操作性等待影响），带超时。 */
const jsClick = (page, sel) =>
  withTimeout(page.evaluate((s) => document.querySelector(s)?.click(), sel), 6000, `jsClick ${sel}`).catch(() => {});

// 判定为“真缺陷”的控制台错误特征
const HARD = [/ReferenceError/i, /TypeError/i, /is not defined/i, /is not a function/i, /Cannot read/i, /SyntaxError/i, /Unhandled/i];
// 已知可忽略（可选服务未启用 / 静态资源 / 第三方渲染库内部）
const IGNORE = [/favicon/i, /mermaid/i, /net::ERR_/i, /Failed to load resource/i, /404 \(Not Found\)/i, /音色|voice|tts|speech/i];

// i18n「原文键泄漏」启发式：可见文本整段长得像我们的键（前缀白名单 + 点号路径）
// 用于抓「common.save common.cancel」这类未翻译键被直接渲染到界面的问题。
const I18N_KEY_RE = /^(common|admin|memory|chat|agent|skill|help|menu|notif|plan|msg|create|gs|fb|search|topbar|settings|topic|task|kb|image|market|eval|perf|wh|ak|ug|voice|twin|profile|auth|brand|account|export|import|doc|org|cite|usage|exec|cfg|perm|role)\.[a-zA-Z0-9_]+(\.[a-zA-Z0-9_]+)*$/;
const i18nLeaks = new Set();

/** 扫描当前可见文本，收集像「未翻译键」的片段（去重、限量）。 */
async function scanI18nLeaks(page) {
  try {
    const found = await withTimeout(page.evaluate((src) => {
      const re = new RegExp(src);
      const out = [];
      const w = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
      while (w.nextNode()) {
        const n = w.currentNode;
        const p = n.parentElement;
        if (!p || p.closest("script,style,.hidden")) continue;
        if (p.getAttribute && p.getAttribute("aria-hidden") === "true") continue;
        // 按空白分词：一个文本节点里可能同时泄漏多个键（如 `common.save common.cancel`）
        for (const tok of (n.nodeValue || "").trim().split(/\s+/)) {
          if (tok && re.test(tok)) out.push(tok);
        }
      }
      return [...new Set(out)].slice(0, 30);
    }, I18N_KEY_RE.source), 8000, "i18n scan");
    for (const s of found) i18nLeaks.add(s);
  } catch { /* 扫描失败不作为失败项 */ }
}

let step = 0;
const hardErrors = [];
const softErrors = [];
let browser = null;

async function forceClose(page) {
  await withTimeout(page.evaluate(() => {
    document.querySelectorAll(".modal-overlay").forEach((e) => e.classList.add("hidden"));
    document.getElementById("meMenu")?.classList.add("hidden");
    document.getElementById("notifPanel")?.classList.add("hidden");
  }), 6000, "forceClose").catch(() => {});
}

/** 点击某入口打开界面 → 等渲染 → 截图 → 关闭（保证后续步骤不被遮挡）。找不到入口就跳过并记录。 */
async function probe(page, name, selector) {
  process.stdout.write(`  · ${name} … `);
  const el = await page.$(selector);
  if (!el) { softErrors.push(`跳过「${name}」：找不到入口 ${selector}`); console.log("跳过"); return; }
  try {
    await el.click({ timeout: 4000 });
  } catch {
    // 被遮挡等：用 JS 触发点击
    await withTimeout(page.evaluate((s) => document.querySelector(s)?.click(), selector), 8000, `click ${selector}`).catch(() => {});
  }
  await sleep(700);
  await withTimeout(page.screenshot({ path: path.join(shots, `${String(step++).padStart(3, "0")}-${name.replace(/[^\w\u4e00-\u9fa5-]/g, "_")}.png`) }), 15000, `shot ${name}`).catch((e) => softErrors.push(String(e.message)));
  await scanI18nLeaks(page); // 弹窗打开时扫描未翻译键
  await forceClose(page);
  await sleep(150);
  console.log("ok");
}

(async () => {
  // 总时长看门狗：无论如何不让扫描进程挂死（默认 240s，可用 TIMEOUT_S 覆盖）
  const watchdog = setTimeout(() => { console.error("扫描超时，强制结束（看门狗）"); process.exit(3); }, Number(process.env.TIMEOUT_S || 240) * 1000);
  if (watchdog.unref) watchdog.unref();

  browser = await chromium.launch({ headless: HEADLESS });
  const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });
  page.setDefaultTimeout(8000);
  page.setDefaultNavigationTimeout(20000);

  page.on("pageerror", (err) => hardErrors.push(`pageerror: ${err.message}`));
  page.on("console", (msg) => {
    if (msg.type() !== "error") return;
    const text = msg.text();
    if (IGNORE.some((re) => re.test(text))) return;
    if (HARD.some((re) => re.test(text))) hardErrors.push(`console: ${text}`);
    else softErrors.push(`console(其它): ${text}`);
  });

  console.log(`UI smoke → ${BASE_URL}（${HEADLESS ? "无头" : "有头"}）\n`);

  await page.goto(BASE_URL, { waitUntil: "domcontentloaded" });
  await page.waitForSelector("#authOverlay", { timeout: 20000 });
  const needLogin = await page.evaluate(() => !document.getElementById("authOverlay").classList.contains("hidden"));
  if (needLogin) {
    await page.fill("#authUsername", USERNAME);
    await page.fill("#authPassword", PASSWORD);
    await page.click("#authSubmit");
    await page.waitForFunction(() => document.getElementById("authOverlay")?.classList.contains("hidden"), { timeout: 25000 });
  }
  await page.waitForSelector("#agentManageBtn", { state: "visible", timeout: 15000 });
  console.log(`登录通过：${USERNAME}`);

  // 首登可能自动弹「模型配置」提示，先关掉避免遮挡
  await jsClick(page, "#mcCancel");
  await sleep(300);

  // ---- 账号菜单各弹窗 ----
  for (const [name, id] of [
    ["修改资料", "#meMenuProfile"],
    ["修改密码", "#meMenuPassword"],
    ["模型配置", "#meMenuModelConfig"],
    ["记忆管理", "#meMenuMemory"],
    ["系统状态", "#meMenuStatus"],
    ["语音服务", "#meMenuVoice"],
  ]) {
    console.log(`账号菜单：${name}`);
    await jsClick(page, "#meChip");
    await sleep(200);
    await probe(page, name, id);
  }

  // ---- 顶栏入口 ----
  await probe(page, "数字员工管理", "#agentManageBtn");
  await probe(page, "使用帮助", "#helpBtn");
  await probe(page, "全局搜索", "#globalSearchBtn");
  await probe(page, "通知面板", "#notifBtn");
  await probe(page, "语言切换", "#langBtn");
  await probe(page, "主题切换", "#themeBtn");

  // ---- 记忆管理内的三个视图（记忆拟人类型 UI 曾在此报错）----
  await jsClick(page, "#meChip");
  await sleep(150);
  await jsClick(page, "#meMenuMemory");
  await sleep(600);
  for (const [name, id] of [["记忆-时间线", "#memViewTimeline"], ["记忆-图谱", "#memViewGraph"], ["记忆-列表", "#memViewList"]]) {
    const el = await page.$(id);
    if (el) { await el.click().catch(() => {}); await sleep(500); }
    else softErrors.push(`跳过「${name}」：找不到 ${id}`);
  }
  await withTimeout(page.screenshot({ path: path.join(shots, `${String(step++).padStart(3, "0")}-memory-views.png`) }), 15000, "shot memory-views").catch(() => {});
  await forceClose(page);

  // ---- 管理控制台：逐个页签渲染 ----
  await jsClick(page, "#meChip");
  await sleep(150);
  await jsClick(page, "#meMenuAdmin");
  await sleep(900);
  const tabs = ["#adminTabUsers", "#adminTabUsage", "#adminTabMetrics", "#adminTabConfig", "#adminTabExec",
    "#adminTabAudit", "#adminTabOrphans", "#adminTabUserGroups", "#adminTabWebhooks", "#adminTabApiKeys",
    "#adminTabStorage", "#adminTabPerf", "#adminTabEval"];
  // 每个页签渲染后，再点其「刷新 / 新增」按钮（子弹窗是 ReferenceError 高发区；新增仅开弹窗不保存）
  const afterTab = {
    "#adminTabMetrics": ["#metricsRefreshBtn"],
    "#adminTabPerf": ["#perfRefreshBtn"],
    "#adminTabConfig": ["#cfgReload"],
    "#adminTabExec": ["#efReload"],
    "#adminTabUserGroups": ["#ugAddBtn"],
    "#adminTabWebhooks": ["#whAddBtn"],
    "#adminTabApiKeys": ["#akAddBtn"],
  };
  for (const t of tabs) {
    process.stdout.write(`  · 页签 ${t} … `);
    const el = await page.$(t);
    if (!el) { softErrors.push(`跳过控制台页签 ${t}：不存在`); console.log("跳过"); continue; }
    await el.click().catch(() => {});
    await sleep(650);
    await withTimeout(page.screenshot({ path: path.join(shots, `${String(step++).padStart(3, "0")}-admin${t.replace("#adminTab", "")}.png`) }), 15000, `shot ${t}`).catch((e) => softErrors.push(String(e.message)));
    await scanI18nLeaks(page);
    for (const btn of afterTab[t] || []) {
      const b = await page.$(btn);
      if (!b) { softErrors.push(`跳过 ${btn}：不存在`); continue; }
      await b.click({ timeout: 4000 }).catch(() => {});
      await sleep(500);
      await forceClose(page); // 新增类子弹窗只是开一下即关（不保存）
      await sleep(150);
    }
    console.log("ok");
  }
  await forceClose(page);

  // ---- 知聚会话：选中第一个知聚 → 知聚设置 / 输出物弹窗（模型覆写等 UI）----
  // 直接点 DOM（不 await selectGroup 内部的网络流程，避免挂死）：点击左栏第一个知聚项
  await withTimeout(page.evaluate(() => {
    const list = document.getElementById("groupList");
    const first = list && list.querySelector("div");
    if (first) first.click();
  }), 6000, "click first group").catch(() => {});
  await sleep(1200);
  await page.waitForTimeout(1200);
  await probe(page, "知聚设置", "#groupSettingsBtn");
  await probe(page, "知聚输出物", "#groupFilesBtn");
  await probe(page, "知聚内搜索", "#searchBtn");

  await browser.close();
  browser = null;
  clearTimeout(watchdog);

  console.log(`\n未捕获 / 运行时错误：${hardErrors.length}`);
  for (const e of hardErrors) console.log("  ✗ " + e);
  console.log(`未翻译键泄漏：${i18nLeaks.size}`);
  for (const s of i18nLeaks) console.log("  ✗ " + s);
  if (softErrors.length) {
    console.log(`\n次要提示（多为可选服务未启用，非本次判据）：${softErrors.length}`);
    for (const e of softErrors.slice(0, 12)) console.log("  · " + e);
  }
  console.log(`\n截图目录：${shots}`);
  const failed = hardErrors.length + i18nLeaks.size;
  if (failed) { console.log(`\n结果：失败（${hardErrors.length} 个运行时错误，${i18nLeaks.size} 个未翻译键）`); process.exit(1); }
  console.log("\n结果：通过（无前端运行时错误，无未翻译键泄漏）");
})().catch(async (e) => {
  console.error("扫描异常：", e.message);
  if (browser) await browser.close().catch(() => {});
  process.exit(2);
});
