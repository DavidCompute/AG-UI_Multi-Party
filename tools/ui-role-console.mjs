// 平台角色前端收敛（A1）验证（Playwright，真实浏览器）：
//   1. Operator 登录后可见「管理控制台」「系统状态」，隐藏数据备份 / 模型配置 / 白标；
//   2. 控制台仅只读页签（用量 / 审计 / 运行指标），默认落到「用量统计」；
//   3. 菜单文案随语言切换在「管理控制台 / Admin Console」与「Admin Console」间正确切换（两种角色统一用 menu.console）；
//   4. 系统状态弹窗对 Operator 可打开。
// 用法：cd tools && HEADLESS=1 node ui-role-console.mjs
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
const stamp = Date.now().toString(36).slice(-6);
const opUser = "op_" + stamp;
const opPass = "optest123";

const browser = await chromium.launch({ headless: HEADLESS });
const adminCtx = await browser.newContext({ viewport: { width: 1280, height: 800 } });
const adminPage = await adminCtx.newPage();
let adminToken = null, opUserId = null;

const adminApi = async (method, p, data) => {
  const r = await adminPage.request.fetch(BASE_URL + p, {
    method, headers: { Authorization: "Bearer " + adminToken, "Content-Type": "application/json" },
    data: data === undefined ? undefined : JSON.stringify(data),
  });
  return { status: r.status(), body: await r.json().catch(() => null) };
};

try {
  // 管理员登录 + 拿 token
  await adminPage.goto(BASE_URL, { waitUntil: "domcontentloaded" });
  await adminPage.waitForSelector("#authOverlay", { timeout: 15000 });
  if (!(await adminPage.evaluate(() => document.getElementById("authOverlay")?.classList.contains("hidden")))) {
    await adminPage.fill("#authUsername", ADMIN_USER);
    await adminPage.fill("#authPassword", ADMIN_PASS);
    await adminPage.click("#authSubmit");
    await adminPage.waitForFunction(() => document.getElementById("authOverlay")?.classList.contains("hidden"), { timeout: 20000 });
  }
  adminToken = (await (await adminPage.request.post(BASE_URL + "/ag-ui/user/login", { data: { username: ADMIN_USER, password: ADMIN_PASS } })).json()).token;

  // 建临时用户并授予 Operator
  const reg = await (await adminPage.request.post(BASE_URL + "/ag-ui/user/register", { data: { username: opUser, password: opPass, nickname: "运维" + stamp } })).json();
  opUserId = reg.userId;
  check("注册临时用户", !!opUserId, opUserId);
  const grant = await adminApi("POST", `/ag-ui/admin/roles/${encodeURIComponent(opUserId)}`, { role: "operator" });
  check("授予 Operator 角色", grant.status === 200, String(grant.status));

  // 新上下文以 Operator 登录
  const opCtx = await browser.newContext({ viewport: { width: 1280, height: 800 } });
  const opPage = await opCtx.newPage();
  await opPage.goto(BASE_URL, { waitUntil: "domcontentloaded" });
  await opPage.waitForSelector("#authOverlay", { timeout: 15000 });
  await opPage.fill("#authUsername", opUser);
  await opPage.fill("#authPassword", opPass);
  await opPage.click("#authSubmit");
  await opPage.waitForFunction(() => document.getElementById("authOverlay")?.classList.contains("hidden"), { timeout: 20000 });
  check("Operator 能登录", true);
  check("生效角色=operator", (await opPage.evaluate(() => (typeof state !== "undefined" ? state.platformRole : null))) === "operator");

  // 菜单可见性（Operator）
  await opPage.click("#meChip");
  check("#meMenuAdmin（管理控制台）可见", await opPage.locator("#meMenuAdmin").isVisible());
  check("#meMenuStatus（系统状态）可见", await opPage.locator("#meMenuStatus").isVisible());
  check("#meMenuBackup 隐藏", await opPage.locator("#meMenuBackup").isHidden());
  check("#meMenuModelConfig 隐藏", await opPage.locator("#meMenuModelConfig").isHidden());
  check("#meMenuBranding 隐藏", await opPage.locator("#meMenuBranding").isHidden());

  // 文案随语言正确切换（改 data-i18n 键，切语言不被重置）
  await opPage.evaluate(() => window.i18n && i18n.setLang("zh"));
  check("zh 文案为「管理控制台」", (await opPage.locator("#meMenuAdmin").innerText()).includes("管理控制台"));
  await opPage.evaluate(() => window.i18n && i18n.setLang("en"));
  check("en 文案为 Admin Console", (await opPage.locator("#meMenuAdmin").innerText()).includes("Admin Console"));
  check("en 下 data-i18n 切到 menu.console", (await opPage.locator("#meMenuAdmin").getAttribute("data-i18n")) === "menu.console");

  // 打开控制台：仅只读页签
  await opPage.locator("#meMenuAdmin").click();
  await opPage.waitForSelector("#adminModal:not(.hidden)", { timeout: 5000 });
  for (const id of ["#adminTabUsage", "#adminTabAudit", "#adminTabMetrics"]) check(`${id} 可见`, await opPage.locator(id).isVisible());
  for (const id of ["#adminTabUsers", "#adminTabConfig", "#adminTabExec", "#adminTabOrphans", "#adminTabUserGroups", "#adminTabStorage"]) check(`${id} 隐藏`, await opPage.locator(id).isHidden());
  check("默认落在「用量统计」", (await opPage.locator("#adminTabUsage").getAttribute("class")).includes("on"));
  check("用量视图可见", await opPage.locator("#adminUsageView").isVisible());
  check("用户管理视图隐藏", await opPage.locator("#adminUsersView").isHidden());
  await opPage.evaluate(() => document.getElementById("adminModal").classList.add("hidden"));

  // 系统状态弹窗对 Operator 可打开
  await opPage.click("#meChip");
  await opPage.locator("#meMenuStatus").click();
  await opPage.waitForSelector("#statusModal:not(.hidden)", { timeout: 5000 });
  check("Operator 可打开系统状态", await opPage.locator("#statusModal").isVisible());
  await opCtx.close();

  // 清理：删除临时账号
  const del = await adminApi("DELETE", `/ag-ui/admin/users/${encodeURIComponent(opUserId)}`);
  check("清理临时账号", del.status === 200, String(del.status));
} catch (e) {
  fail++;
  console.log("  FAIL  异常：" + (e && e.message));
  await adminPage.screenshot({ path: "screenshots/role-console-fail.png" }).catch(() => {});
  if (opUserId && adminToken) await adminApi("DELETE", `/ag-ui/admin/users/${encodeURIComponent(opUserId)}`).catch(() => {});
} finally {
  await browser.close();
}
console.log(`\n平台角色前端收敛验证：${pass} 通过 / ${fail} 失败`);
process.exit(fail ? 1 : 0);
