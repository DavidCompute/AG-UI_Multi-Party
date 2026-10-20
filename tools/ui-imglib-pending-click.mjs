// 待审批通知「点击直达」回归（Playwright，真实浏览器）：
//   1. 管理员登录后，待审批轮询把新项送进通知中心（徽标 +1，不依赖人工刷新）；
//   2. 通知文案提示「点击打开图库管理」；
//   3. 点击通知 → 图库管理弹窗打开、待审批块可见、批量按钮可用、通知面板收起；
//   4. 「最近处理」块渲染 3 条回执（批准 / 拒绝 / 失败），状态筛选与关键词筛选生效；
//   5. 失败回执仍在待审批 → 标「仍在待审批」并可一键「重试」（重试调批准端点）；
//   6. 「清空历史」弹确认框 → 确认后列表块隐藏（DELETE 由路由桩承兑）；
//   7. 无页面 JS 异常。
// 待审批列表、最近处理与清空都用路由桩（不落任何真实数据）：只为验证「感知 → 点击 → 直达 → 筛选/清空」这一段链路。
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

const PENDING_ID = "iwp_ui_" + Date.now().toString(36);

const PENDING = {
  items: [{
    id: PENDING_ID,
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
  items: [
    {
      pendingId: "iwp_ui_a", status: "approved", libId: "img_ui", libName: "UI 素材库",
      fileName: "ui-approved.png", skillId: "skill_ui", agentId: "agent_ui", assetId: "asset_ui",
      duplicated: false, decidedBy: "david", atMs: Date.now() - 60_000,
    },
    {
      pendingId: "iwp_ui_r", status: "rejected", libId: "img_ui", libName: "UI 素材库",
      fileName: "ui-rejected.png", skillId: "skill_other", agentId: "agent_ui",
      duplicated: false, decidedBy: "david", atMs: Date.now() - 120_000,
    },
    {
      pendingId: PENDING_ID, status: "failed", reason: "library_missing", libId: "img_ui", libName: "UI 素材库",
      fileName: "ui-failed.png", skillId: "skill_ui", agentId: "agent_ui",
      duplicated: false, decidedBy: "david", atMs: Date.now() - 180_000,
    },
  ],
};

const browser = await chromium.launch({ headless: HEADLESS });
const page = await browser.newPage({ viewport: { width: 1400, height: 900 } });
const errors = [];
page.on("pageerror", (e) => errors.push("pageerror: " + e.message));
page.on("console", (m) => { if (m.type() === "error") errors.push("console: " + m.text()); });

try {
  // 登录前挂路由桩：poll 在 enterApp 里立即 tick 一次，必须提前就位
  await page.route("**/ag-ui/admin/image-write-pending/history*", (route) =>
    route.request().method() === "DELETE"
      ? route.fulfill({ json: { cleared: HISTORY.items.length } })
      : route.fulfill({ json: HISTORY }));
  await page.route("**/ag-ui/admin/image-write-pending", (route) => route.fulfill({ json: PENDING }));
  // 1.0.289：失败回执的「重试」→ 批准端点（只捕捉请求，不产生真实审批）
  let retriedUrl = null;
  await page.route("**/ag-ui/admin/image-write-pending/*/approve", (route) => {
    retriedUrl = route.request().url();
    return route.fulfill({ json: { applied: true } });
  });

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

  // 4) 最近处理（已处理回执）+ 筛选 + 清空
  await page.waitForSelector("#imgLibHistory:not(.hidden)", { timeout: 5000 });
  const histRows = page.locator("#imgLibHistoryList .kb-list-item");
  check("最近处理渲染 3 条回执", (await histRows.count()) === 3, "rows=" + (await histRows.count()));
  const histText = (await page.locator("#imgLibHistory").innerText()).replace(/\s+/g, " ");
  check("含已批准 / 已拒绝 / 失败三态", ["已批准", "已拒绝", "失败"].every((s) => histText.includes(s)), histText.slice(0, 120));
  check("含文件与处理人", histText.includes("ui-approved.png") && histText.includes("david"));

  // 1.0.289：失败的项仍在待审批 → 行内给「重试」
  const retryBtn = page.locator('#imgLibHistoryList [data-retry]');
  check("失败回执带「重试」按钮", (await retryBtn.count()) === 1, "count=" + (await retryBtn.count()));
  check("失败回执标注「仍在待审批」", histText.includes("仍在待审批"));
  await retryBtn.click();
  await page.waitForFunction(() => document.querySelector('#imgLibHistoryList [data-retry]') !== null);
  check("点重试 → 调用批准端点", !!retriedUrl && retriedUrl.includes(PENDING_ID), retriedUrl || "none");

  // 状态筛选：只看被拒绝
  await page.selectOption("#imgLibHistoryStatus", "rejected");
  await page.waitForFunction(() => document.querySelectorAll("#imgLibHistoryList .kb-list-item").length === 1);
  const rejText = (await page.locator("#imgLibHistoryList").innerText()).replace(/\s+/g, " ");
  check("按状态筛选 → 仅剩拒绝项", rejText.includes("ui-rejected.png") && !rejText.includes("ui-approved.png"), rejText.slice(0, 80));

  // 关键词筛选（回到全部状态后搜技能名）
  await page.selectOption("#imgLibHistoryStatus", "");
  await page.fill("#imgLibHistorySearch", "skill_other");
  await page.waitForFunction(() => document.querySelectorAll("#imgLibHistoryList .kb-list-item").length === 1);
  const kwText = (await page.locator("#imgLibHistoryList").innerText()).replace(/\s+/g, " ");
  check("按关键词筛选 → 命中技能", kwText.includes("ui-rejected.png"), kwText.slice(0, 80));

  // 无匹配提示 + 清空按钮（× 清空搜索）
  await page.fill("#imgLibHistorySearch", "no-such-file");
  await page.waitForSelector("#imgLibHistoryList .admin-empty", { timeout: 3000 });
  check("无匹配时给提示", (await page.locator("#imgLibHistoryList .admin-empty").innerText()).includes("没有匹配"));
  await page.click('button.search-clear[data-clear-for="imgLibHistorySearch"]');
  await page.waitForFunction(() => document.querySelectorAll("#imgLibHistoryList .kb-list-item").length === 3);
  check("× 清空搜索后恢复 3 条", true);

  // 清空历史：确认框 → DELETE（路由桩）→ 块隐藏
  await page.click("#imgLibHistoryClear");
  await page.waitForSelector("#uiDialog:not(.hidden)", { timeout: 3000 });
  check("清空前弹确认框", (await page.locator("#uiDialogMsg").innerText()).includes("不影响"));
  await page.click("#uiDialogOk");
  await page.waitForFunction(() => document.getElementById("imgLibHistory")?.classList.contains("hidden"), { timeout: 5000 });
  check("清空后「最近处理」块隐藏", true);

  // 5) 无页面 JS 异常
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
