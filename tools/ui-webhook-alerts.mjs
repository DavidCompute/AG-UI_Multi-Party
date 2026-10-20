// 投递失败主动告警回归（Playwright，真实浏览器）：
//   1. 登录后首次检查只建基线（历史失败不推）；
//   2. 新出现的「已结算」失败（距最近尝试 > 40s）→ 通知中心一条「Webhook 投递失败」；
//   3. 刚发生、可能还在重试的失败不告警；PING 测试投递不告警；成功不告警；
//   4. 同一订阅 10 分钟冷却：第二条失败不重复推；
//   5. 点击通知直达控制台 Webhook 页签；无页面 JS 异常。
// 投递记录全部用路由桩，不创建真实订阅 / 投递。
// 用法：cd tools && HEADLESS=1 node ui-webhook-alerts.mjs
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

// 投递记录桩（列表新→旧；脚本按需 push 新记录）
const deliveries = [];
const rec = (o) => ({
  deliveryId: o.id, subscriptionId: o.sub ?? "wh_a", eventType: o.event ?? "GROUP_CREATED",
  groupId: "", url: o.url ?? "https://example.com/a", attempt: o.attempt ?? 3,
  statusCode: o.ok ? 200 : 500, ok: !!o.ok, atMs: o.atMs ?? Date.now() - 90_000,
  durationMs: 12, error: o.ok ? undefined : (o.error ?? "HTTP 500"),
});

const browser = await chromium.launch({ headless: HEADLESS });
const ctx = await browser.newContext({ viewport: { width: 1400, height: 900 } });
const page = await ctx.newPage();
const errors = [];
page.on("pageerror", (e) => errors.push("pageerror: " + e.message));
page.on("console", (m) => { if (m.type() === "error") errors.push("console: " + m.text()); });

await page.route("**/ag-ui/admin/webhooks/deliveries", (route) => route.fulfill({ json: deliveries }));

const notifCount = () => page.evaluate(() => state.notifications.length);
const runCheck = () => page.evaluate(() => checkWebhookDeliveryAlarmsOnce());

try {
  await page.goto(BASE_URL, { waitUntil: "domcontentloaded" });
  await page.waitForSelector("#authOverlay", { timeout: 15000 });
  if (!(await page.evaluate(() => document.getElementById("authOverlay")?.classList.contains("hidden")))) {
    await page.fill("#authUsername", USERNAME);
    await page.fill("#authPassword", PASSWORD);
    await page.click("#authSubmit");
    await page.waitForFunction(() => document.getElementById("authOverlay")?.classList.contains("hidden"), { timeout: 20000 });
  }

  // 1) 首轮只建基线
  await page.waitForFunction(() => typeof webhookAlarmBaselineDone !== "undefined" && webhookAlarmBaselineDone === true, { timeout: 15000 });
  check("首轮建立基线", (await notifCount()) === 0, "notifications=" + (await notifCount()));

  // 2) 已结算的失败 → 一条告警
  deliveries.unshift(rec({ id: "wd_fail_1", event: "GROUP_CREATED" }));
  await runCheck();
  await page.waitForFunction(() => state.notifications.some((n) => n.title.includes("Webhook 投递失败")), { timeout: 5000 });
  const notif = await page.evaluate(() => state.notifications.find((n) => n.title.includes("Webhook 投递失败")));
  check("已结算失败 → 推送告警", !!notif, notif?.title);
  check("告警含地址 / 事件 / 错误", !!notif && notif.body.includes("https://example.com/a") && notif.body.includes("GROUP_CREATED") && notif.body.includes("HTTP 500"), notif?.body);

  const before = await notifCount();

  // 3) 刚发生（可能还在重试）→ 不推
  deliveries.unshift(rec({ id: "wd_fresh", atMs: Date.now() - 5_000 }));
  await runCheck();
  check("刚失败（<40s）不告警", (await notifCount()) === before, `${before} → ${await notifCount()}`);

  // PING 测试投递 → 不推
  deliveries.unshift(rec({ id: "wd_ping", event: "PING" }));
  await runCheck();
  check("PING 测试投递不告警", (await notifCount()) === before);

  // 成功 → 不推
  deliveries.unshift(rec({ id: "wd_ok", ok: true }));
  await runCheck();
  check("成功投递不告警", (await notifCount()) === before);

  // 4) 同一订阅 10 分钟冷却 → 第二条失败不推
  deliveries.unshift(rec({ id: "wd_fail_2", sub: "wh_a" }));
  await runCheck();
  check("同订阅 10 分钟冷却内不重复告警", (await notifCount()) === before, `${before} → ${await notifCount()}`);

  // 不同订阅 → 仍会告警（验证冷却按订阅隔离）
  deliveries.unshift(rec({ id: "wd_fail_3", sub: "wh_b", url: "https://example.com/b" }));
  await runCheck();
  check("另一订阅失败 → 新告警", (await notifCount()) === before + 1, `${before} → ${await notifCount()}`);

  // 5) 点击通知 → 控制台 Webhook 页签
  await page.click("#notifBtn");
  await page.waitForSelector("#notifPanel:not(.hidden)", { timeout: 5000 });
  await page.locator("#notifList .notif-item").filter({ hasText: "Webhook 投递失败" }).first().click();
  await page.waitForSelector("#adminModal:not(.hidden)", { timeout: 5000 });
  await page.waitForSelector("#adminWebhooksView:not(.hidden)", { timeout: 5000 });
  check("点击告警直达 Webhook 页签", await page.locator("#adminTabWebhooks").evaluate((el) => el.classList.contains("on")));

  check("无页面 JS 异常", errors.length === 0, errors.slice(0, 3).join(" ; "));
} catch (e) {
  fail++;
  console.log("  FAIL  异常：" + (e && e.message));
  await page.screenshot({ path: "screenshots/webhook-alerts-fail.png" }).catch(() => {});
} finally {
  await browser.close();
}
console.log(`\n投递失败告警验证：${pass} 通过 / ${fail} 失败`);
process.exit(fail ? 1 : 0);
