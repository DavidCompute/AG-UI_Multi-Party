// 待审批通知「点击直达」回归（Playwright，真实浏览器）：
//   1. 管理员登录后，待审批轮询把新项送进通知中心（徽标 +1，不依赖人工刷新）；
//   2. 通知文案提示「点击打开图库管理」；
//   3. 点击通知 → 图库管理弹窗打开、待审批块可见、批量按钮可用、通知面板收起；
//   4. 「最近处理」块渲染已处理回执（状态 / 文件 / 处理人）；
//   5. 无页面 JS 异常。
// 待审批列表与最近处理都用路由桩（不落任何真实数据）：只为验证「感知 → 点击 → 直达」这一段链路。
// 用法：cd tools && HEADLESS=1 node ui-imglib-pending-click.mjs
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

const PENDING = {
  items: [{
    id: "iwp_ui_" + Date.now().toString(36),
    ownerUserId: "u_ui",
    skillId: "skill_ui",
    agentId: "agent_ui",
    libId: "img_ui",
    libName: "UI 素材库",
    fileName: "ui-pending.png",
    contentType: "image/png",
    sizeBytes: 2048,
    atMs: Date.now(),
    expiresAtMs: Date.now() + 3600_000,
  }],
};

const HISTORY = {
  items: [{
    pendingId: "iwp_ui_old",
    status: "approved",
    libId: "img_ui",
    libName: "UI 素材库",
    fileName: "ui-approved.png",
    skillId: "skill_ui",
    agentId: "agent_ui",
    assetId: "asset_ui",
    duplicated: false,
    decidedBy: "david",
    atMs: Date.now() - 60_000,
  }],
};

const browser = await chromium.launch({ headless: HEADLESS });
const page = await browser.newPage({ viewport: { width: 1400, height: 900 } });
const errors = [];
page.on("pageerror", (e) => errors.push("pageerror: " + e.message));
page.on("console", (m) => { if (m.type() === "error") errors.push("console: " + m.text()); });

try {
  // 登录前挂路由桩：poll 在 enterApp 里立即 tick 一次，必须提前就位
  await page.route("**/ag-ui/admin/image-write-pending/history", (route) => route.fulfill({ json: HISTORY }));
  await page.route("**/ag-ui/admin/image-write-pending", (route) => route.fulfill({ json: PENDING }));

  await page.goto(BASE_URL, { waitUntil: "domcontentloaded" });
  await page.waitForSelector("#authOverlay", { timeout: 15000 });
  if (!(await page.evaluate(() => document.getElementById("authOverlay")?.classList.contains("hidden")))) {
    await page.fill("#authUsername", USERNAME);
    await page.fill("#authPassword", PASSWORD);
    await page.click("#authSubmit");
    await page.waitForFunction(() => document.getElementById("authOverlay")?.classList.contains("hidden"), { timeout: 20000 });
  }

  // 1) 轮询把新项送进通知中心（徽标显示未读数）
  await page.waitForFunction(() => {
    const b = document.getElementById("notifBadge");
    return b && !b.classList.contains("hidden") && Number(b.textContent) >= 1;
  }, { timeout: 15000 });
  const badge = (await page.locator("#notifBadge").innerText()).trim();
  check("新待审批项进入通知中心（未读徽标 ≥1）", Number(badge) >= 1, "badge=" + badge);

  // 2) 通知文案
  await page.click("#notifBtn");
  await page.waitForSelector("#notifPanel:not(.hidden)", { timeout: 5000 });
  const item = page.locator("#notifList .notif-item").filter({ hasText: "ui-pending.png" }).first();
  await item.waitFor({ timeout: 5000 });
  const text = (await item.innerText()).replace(/\s+/g, " ");
  check("通知标题为待审批图库写入", text.includes("待审批的图库写入"), text.slice(0, 60));
  check("通知文案提示点击打开图库管理", text.includes("点击打开图库管理"), text.slice(0, 120));

  // 3) 点击 → 直达图库管理
  await item.click();
  await page.waitForSelector("#imgLibModal:not(.hidden)", { timeout: 5000 });
  check("点击后图库管理弹窗打开", true);
  check("通知面板已收起", await page.evaluate(() => document.getElementById("notifPanel")?.classList.contains("hidden")));
  await page.waitForSelector("#imgLibPending:not(.hidden)", { timeout: 5000 });
  check("待审批块可见", true);
  const pendingText = (await page.locator("#imgLibPending").innerText()).replace(/\s+/g, " ");
  check("待审批块列出该文件与来源", pendingText.includes("ui-pending.png") && pendingText.includes("skill_ui"), pendingText.slice(0, 120));
  check("批量按钮可用", await page.locator("#imgLibPendingApproveAll").isVisible() && await page.locator("#imgLibPendingRejectAll").isVisible());

  // 4) 最近处理（已处理回执）
  await page.waitForSelector("#imgLibHistory:not(.hidden)", { timeout: 5000 });
  const histText = (await page.locator("#imgLibHistory").innerText()).replace(/\s+/g, " ");
  check("最近处理块可见且展示已批准", histText.includes("最近处理") && histText.includes("已批准"), histText.slice(0, 100));
  check("最近处理含文件与处理人", histText.includes("ui-approved.png") && histText.includes("david"), histText.slice(0, 160));

  // 4) 无页面 JS 异常
  check("无页面 JS 异常", errors.length === 0, errors.slice(0, 3).join(" ; "));
} catch (e) {
  fail++;
  console.log("  FAIL  异常：" + (e && e.message));
  await page.screenshot({ path: "screenshots/imglib-pending-click-fail.png" }).catch(() => {});
} finally {
  await browser.close();
}
console.log(`\n待审批通知点击直达验证：${pass} 通过 / ${fail} 失败`);
process.exit(fail ? 1 : 0);
