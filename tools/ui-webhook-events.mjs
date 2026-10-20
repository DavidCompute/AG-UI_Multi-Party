// Webhook 事件目录分组回归（Playwright，真实浏览器）：
//   1. 管理控制台 → Webhook → 新建订阅：事件按类别分组渲染（知聚 / 成员 / 消息 / 协作与工具 / 话题 / 运行 / 配额 / 图库）；
//   2. 事件目录里的每个类型在界面上恰好出现一次（无遗漏、无重复）；
//   3. 组头「全选 / 清空」可整组勾选与取消，计数同步（0/3 → 3/3 → 0/3）；
//   4. 「最近投递」可按事件类型 / 成败筛选，计数「显示 X / Y」与无匹配提示正确；
//   5. 订阅表「最近投递」列按 subscriptionId 关联，显示最近一次 ✅/❌ 与时间；
//   6. 无页面 JS 异常；不创建任何真实订阅。
// 用法：cd tools && HEADLESS=1 node ui-webhook-events.mjs
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

const DELIVERIES = [
  { atMs: Date.now() - 1_000, subscriptionId: "wh_a", eventType: "GROUP_CREATED", ok: false, statusCode: 500, error: "HTTP 500", url: "https://example.com/a", attempt: 3, durationMs: 30 },
  { atMs: Date.now() - 2_000, subscriptionId: "wh_a", eventType: "GROUP_CREATED", ok: true, statusCode: 200, url: "https://example.com/a", attempt: 1, durationMs: 12 },
  { atMs: Date.now() - 3_000, subscriptionId: "wh_b", eventType: "IMAGE_WRITE_PENDING", ok: true, statusCode: 200, url: "https://example.com/b", attempt: 1, durationMs: 15 },
  { atMs: Date.now() - 4_000, subscriptionId: "wh_a", eventType: "PING", ok: true, statusCode: 200, url: "https://example.com/a", attempt: 1, durationMs: 9 },
];

const SUBS = [
  { id: "wh_a", name: "告警 A", url: "https://example.com/a", eventTypes: ["GROUP_CREATED"], groupId: null, enabled: true, createdAt: 1, createdBy: "u_admin", lastDeliveredAt: null, hasSecret: true },
  { id: "wh_b", name: "告警 B", url: "https://example.com/b", eventTypes: [], groupId: null, enabled: false, createdAt: 2, createdBy: "u_admin", lastDeliveredAt: null, hasSecret: false },
];

const browser = await chromium.launch({ headless: HEADLESS });
const page = await browser.newPage({ viewport: { width: 1400, height: 900 } });
const errors = [];
page.on("pageerror", (e) => errors.push("pageerror: " + e.message));
page.on("console", (m) => { if (m.type() === "error") errors.push("console: " + m.text()); });

// 投递记录与订阅列表用路由桩（只用于渲染 / 筛选 / 最近投递列验证）
await page.route("**/ag-ui/admin/webhooks/deliveries", (route) => route.fulfill({ json: DELIVERIES }));
await page.route("**/ag-ui/admin/webhooks", (route) =>
  route.request().method() === "GET" ? route.fulfill({ json: SUBS }) : route.fallback());

try {
  await page.goto(BASE_URL, { waitUntil: "domcontentloaded" });
  await page.waitForSelector("#authOverlay", { timeout: 15000 });
  if (!(await page.evaluate(() => document.getElementById("authOverlay")?.classList.contains("hidden")))) {
    await page.fill("#authUsername", USERNAME);
    await page.fill("#authPassword", PASSWORD);
    await page.click("#authSubmit");
    await page.waitForFunction(() => document.getElementById("authOverlay")?.classList.contains("hidden"), { timeout: 20000 });
  }

  // 服务端事件目录（用于核对「每个类型恰好出现一次」）
  const login = await page.request.post(BASE_URL + "/ag-ui/user/login", { data: { username: USERNAME, password: PASSWORD } });
  const token = (await login.json()).token;
  const catalogRes = await page.request.get(BASE_URL + "/ag-ui/admin/webhooks/events", { headers: { Authorization: "Bearer " + token } });
  const catalog = await catalogRes.json();
  check("事件目录可取（≥20 类）", Array.isArray(catalog) && catalog.length >= 20, "n=" + (catalog?.length ?? 0));

  // 打开控制台 → Webhook → 新建订阅
  await page.click("#meChip");
  await page.locator("#meMenuAdmin").click();
  await page.waitForSelector("#adminModal:not(.hidden)", { timeout: 5000 });
  await page.locator("#adminTabWebhooks").click();
  await page.waitForSelector("#adminWebhooksView:not(.hidden)", { timeout: 5000 });

  // 0) 订阅表「最近投递」列：按 subscriptionId 关联最近一次结果
  await page.waitForFunction(() => document.querySelectorAll("#adminWebhookRows tr").length === 2);
  const headText = (await page.locator("#adminWebhooksView thead").first().innerText()).replace(/\s+/g, " ");
  check("订阅表含「最近投递」列", headText.includes("最近投递"), headText.slice(0, 100));
  const rowA = (await page.locator("#adminWebhookRows tr", { hasText: "告警 A" }).innerText()).replace(/\s+/g, " ");
  const rowB = (await page.locator("#adminWebhookRows tr", { hasText: "告警 B" }).innerText()).replace(/\s+/g, " ");
  check("订阅 A 最近投递为失败 ❌", rowA.includes("❌"), rowA.slice(0, 80));
  check("订阅 B 最近投递为成功 ✅", rowB.includes("✅"), rowB.slice(0, 80));

  await page.locator("#whAddBtn").click();
  await page.waitForSelector("#webhookModal:not(.hidden)", { timeout: 5000 });
  await page.waitForSelector("#whEventList .wh-ev-group", { timeout: 5000 });

  // 1) 分组渲染
  const groupKeys = await page.$$eval("#whEventList .wh-ev-group", (els) => els.map((e) => e.dataset.group));
  for (const key of ["group", "members", "messages", "interactions", "topics", "runs", "quota", "images"]) {
    check("分组存在：" + key, groupKeys.includes(key));
  }
  check("无「其他」分组（类型均已归类）", !groupKeys.includes("other"), groupKeys.join(","));
  const titles = (await page.locator("#whEventList .wh-ev-title").allInnerTexts()).map((x) => x.trim());
  check("分组标题本地化", titles.some((x) => x.includes("配额")) && titles.some((x) => x.includes("图库")), titles.join(" | "));

  // 2) 每个类型恰好一次
  const rendered = await page.$$eval("#whEventList input[type=checkbox]", (els) => els.map((e) => e.value));
  const counts = new Map();
  rendered.forEach((v) => counts.set(v, (counts.get(v) || 0) + 1));
  const missing = catalog.filter((tp) => !counts.has(tp));
  const dup = [...counts.entries()].filter(([, n]) => n > 1).map(([k]) => k);
  check("目录类型全部渲染（无遗漏）", missing.length === 0, missing.join(","));
  check("无重复类型", dup.length === 0, dup.join(","));
  check("渲染数量与目录一致", rendered.length === catalog.length, `${rendered.length} / ${catalog.length}`);

  // 3) 组头全选 / 清空 + 计数
  const quota = page.locator('#whEventList .wh-ev-group[data-group="quota"]');
  const quotaBoxes = quota.locator('input[type=checkbox]');
  const quotaCount = quota.locator(".wh-ev-count");
  const quotaToggle = quota.locator(".wh-ev-toggle");
  check("配额组默认计数 0/3", (await quotaCount.innerText()).trim() === "0/3", (await quotaCount.innerText()).trim());

  await quotaToggle.click();
  const allChecked = await quotaBoxes.evaluateAll((els) => els.every((e) => e.checked));
  check("点「全选」→ 整组勾选", allChecked);
  check("勾选后计数 3/3", (await quotaCount.innerText()).trim() === "3/3", (await quotaCount.innerText()).trim());
  check("按钮变为「清空」", (await quotaToggle.innerText()).trim() === "清空", (await quotaToggle.innerText()).trim());

  await quotaToggle.click();
  const noneChecked = await quotaBoxes.evaluateAll((els) => els.every((e) => !e.checked));
  check("点「清空」→ 整组取消", noneChecked);
  check("取消后计数回到 0/3", (await quotaCount.innerText()).trim() === "0/3", (await quotaCount.innerText()).trim());

  // 4) 最近投递：事件类型 / 成败筛选
  const delivRows = page.locator("#adminWebhookDeliveryRows tr");
  await page.waitForFunction(() => document.querySelectorAll("#adminWebhookDeliveryRows tr").length === 4);
  check("最近投递渲染 4 条（桩）", (await delivRows.count()) === 4, "rows=" + (await delivRows.count()));
  check("计数显示 4 / 4", (await page.locator("#whDelivMeta").innerText()).includes("4 / 4"), (await page.locator("#whDelivMeta").innerText()).trim());

  await page.waitForFunction(() => !!document.querySelector('#whDelivEvent option[value="GROUP_CREATED"]'));
  await page.selectOption("#whDelivEvent", "GROUP_CREATED");
  await page.waitForFunction(() => document.querySelectorAll("#adminWebhookDeliveryRows tr").length === 2);
  check("按事件筛选 → 2 条", true);
  check("事件筛选后计数 2 / 4", (await page.locator("#whDelivMeta").innerText()).includes("2 / 4"));

  await page.selectOption("#whDelivStatus", "fail");
  await page.waitForFunction(() => document.querySelectorAll("#adminWebhookDeliveryRows tr").length === 1);
  const failText = (await page.locator("#adminWebhookDeliveryRows").innerText());
  check("叠加「失败」→ 仅失败项", failText.includes("HTTP 500"), failText.replace(/\s+/g, " ").slice(0, 80));

  await page.selectOption("#whDelivEvent", "PING");
  await page.waitForSelector("#adminWebhookDeliveryRows .admin-empty", { timeout: 3000 });
  check("无匹配组合 → 提示无匹配", (await page.locator("#adminWebhookDeliveryRows .admin-empty").innerText()).includes("没有匹配"));
  check("无匹配时计数 0 / 4", (await page.locator("#whDelivMeta").innerText()).includes("0 / 4"));

  // 复位，避免影响后续断言 / 眼观
  await page.selectOption("#whDelivStatus", "");
  await page.selectOption("#whDelivEvent", "");
  await page.waitForFunction(() => document.querySelectorAll("#adminWebhookDeliveryRows tr").length === 4);
  check("清空筛选 → 恢复 4 条", true);

  // 5) 无页面 JS 异常
  check("无页面 JS 异常", errors.length === 0, errors.slice(0, 3).join(" ; "));
} catch (e) {
  fail++;
  console.log("  FAIL  异常：" + (e && e.message));
  await page.screenshot({ path: "screenshots/webhook-events-fail.png" }).catch(() => {});
} finally {
  await browser.close();
}
console.log(`\nWebhook 事件分组验证：${pass} 通过 / ${fail} 失败`);
process.exit(fail ? 1 : 0);
