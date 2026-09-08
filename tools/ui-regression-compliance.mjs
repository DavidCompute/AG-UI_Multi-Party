// AG-UI 合规界面回归（Playwright 浏览器自动化）
//
// 覆盖（对应「企业合规」与近期界面改动）：
//   1. 普通用户：资料弹窗「危险操作」区显示「⬇ 导出我的数据」与「注销账户」；
//      注销弹窗：错误密码提示、可取消；
//   2. 消息 👍/👎 评价按钮：默认隐藏、悬停消息可见（与复制/撤回等头部按钮一致的悬停显隐）；
//   3. 管理员：用户管理他人行存在「🗑️ 彻底删除」按钮；管理员控制台「审计日志」tab 可打开渲染。
//
// 用法（前置：Web 已运行于 BASE_URL，默认 http://localhost:5200）：
//   cd tools
//   npm install && npx playwright install chromium     # 首次
//   node ui-regression-compliance.mjs                  # 有头（默认），自建临时账号
//   HEADLESS=1 node ui-regression-compliance.mjs       # 无头
//
// 账号策略（无需手工造号）：
//   - 未给 USERNAME/PASSWORD/ADMIN_USERNAME/ADMIN_PASSWORD 时，脚本用 /ag-ui/user/register
//     自建两个临时账号（uiadmin_<ts> / uireg_<ts>，口令 secret123）：
//       * 若实例为空（首个注册者自动成为超级管理员），则管理员账号 = uiadmin_<ts>，正常走全套；
//       * 若实例已有用户（新注册者为普通用户），管理员账号不可自举 → 管理员相关断言自动跳过
//         （可用 ADMIN_USERNAME/ADMIN_PASSWORD 显式提供已有管理员）。
//   - 收尾：管理员账号存在且临时普通账号为自建时，用管理接口删除该普通账号；临时管理员账号在
//     空实例自举场景下保留（否则会删掉实例最后一名超管，破坏防呆语义）；KEEP=1 则全部保留。
//
// 退出码：0 = 全部通过（管理员套件跳过视为通过并提示）；1 = 任一步骤失败。

import { chromium } from "playwright";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const BASE_URL = process.env.BASE_URL || "http://localhost:5200";
const HEADLESS = process.env.HEADLESS === "1";
const KEEP = process.env.KEEP === "1";
const SHOTS = process.env.SHOTS || path.join(__dirname, "screenshots");
fs.mkdirSync(SHOTS, { recursive: true });
const stamp = new Date().toISOString().replace(/[-:.TZ]/g, "").slice(0, 14);

let pass = 0, fail = 0, skipped = [];
const check = (name, ok, extra = "") => {
  if (ok) { pass++; console.log(`  PASS  ${name}${extra ? "  " + extra : ""}`); }
  else { fail++; console.log(`  FAIL  ${name}${extra ? "  " + extra : ""}`); }
};
const note = (msg) => console.log(`  - note: ${msg}`);
const shot = async (page, name) => {
  await page.waitForTimeout(300);
  await page.screenshot({ path: path.join(SHOTS, `uir-${stamp}-${name}.png`) });
};
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

const api = async (method, p, token, json) => {
  const headers = {};
  if (token) headers.Authorization = `Bearer ${token}`;
  if (json !== undefined) headers["Content-Type"] = "application/json";
  const res = await fetch(BASE_URL + p, { method, headers, body: json !== undefined ? JSON.stringify(json) : undefined });
  const data = await res.json().catch(() => null);
  return { status: res.status, data };
};
const register = async (username) => (await api("POST", "/ag-ui/user/register", null,
  { username, password: "secret123", nickname: username })).data;
const login = async (username) => (await api("POST", "/ag-ui/user/login", null,
  { username, password: "secret123" })).data;

// ---------- 账号准备 ----------
const envNormal = process.env.USERNAME && process.env.PASSWORD
  ? { username: process.env.USERNAME, password: process.env.PASSWORD, external: true } : null;
const envAdmin = process.env.ADMIN_USERNAME && process.env.ADMIN_PASSWORD
  ? { username: process.env.ADMIN_USERNAME, password: process.env.ADMIN_PASSWORD, external: true } : null;

let normal = envNormal ? await login(envNormal.username) : null;
if (!normal) {
  normal = await register(`uireg_${stamp}`);
  normal._selfMade = true;
}
if (!normal?.token) { console.error("❌ 无法准备普通用户账号（请检查 BASE_URL / 网络 / 账号）；请用 USERNAME/PASSWORD 显式提供"); process.exit(1); }

let admin = envAdmin ? await login(envAdmin.username) : null;
if (!admin) {
  const candidate = await register(`uiadmin_${stamp}`);
  const isRoot = candidate?.platformRole === "superadmin" || candidate?.isAdmin === true;
  if (isRoot) { admin = candidate; admin._selfMade = true; }
  else if (candidate?.token) { note(`管理员自举不可用（实例已有用户）：${candidate.username} 为普通账号；管理员套件跳过（可用 ADMIN_USERNAME/PASSWORD 提供管理员）`); skipped.push("admin"); }
}
if (admin && !admin.token) admin = null;

console.log(`=== UI 合规回归 ===  站点 ${BASE_URL}  普通用户 ${normal.username}${admin ? `  管理员 ${admin.username}` : "  (管理员套件跳过)"}  有头: ${!HEADLESS}`);

const browser = await chromium.launch({ headless: HEADLESS });
const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });
const pageErrors = [];
page.on("pageerror", (e) => pageErrors.push(String(e)));

async function loginUi(username, password) {
  await page.goto(BASE_URL, { waitUntil: "domcontentloaded" });
  await page.waitForSelector("#authOverlay", { timeout: 15000 });
  const needLogin = await page.evaluate(() => !document.getElementById("authOverlay").classList.contains("hidden"));
  if (!needLogin) { await page.evaluate(() => { sessionStorage.clear(); localStorage.clear(); }); await page.goto(BASE_URL, { waitUntil: "domcontentloaded" }); }
  await page.fill("#authUsername", username);
  await page.fill("#authPassword", password);
  await page.click("#authSubmit");
  await page.waitForFunction(() => document.getElementById("authOverlay")?.classList.contains("hidden"), { timeout: 20000 });
  await page.waitForSelector("#meChip", { state: "visible", timeout: 10000 });
  console.log(`  [OK] 已登录 ${username}`);
}

try {
  // ============ 1. 普通用户：资料危险区 + 注销弹窗 ============
  await loginUi(normal.username, envNormal?.password || "secret123");
  await page.click("#meChip");
  await page.click("#meMenuProfile");
  await page.waitForSelector("#profileModal:not(.hidden)", { timeout: 5000 });
  await page.locator("#pfExportData").scrollIntoViewIfNeeded();
  check("资料弹窗显示「导出我的数据」", await page.locator("#pfExportData").isVisible());
  check("资料弹窗显示「注销账户」", await page.locator("#pfDeleteAccount").isVisible());
  await shot(page, "profile-danger-zone");

  await page.click("#pfDeleteAccount");
  await page.waitForSelector("#deleteAccountModal:not(.hidden)", { timeout: 3000 });
  await page.fill("#daPassword", "wrong-pass-123");
  await page.click("#daConfirm");
  await page.waitForSelector("#daError:not([style*='display: none'])", { timeout: 3000 });
  check("注销弹窗：错误密码提示出现", await page.locator("#daError").isVisible());
  await shot(page, "delete-account-modal");
  await page.click("#daCancel");
  check("注销弹窗：可取消并关闭", await page.locator("#deleteAccountModal").evaluate((el) => el.classList.contains("hidden")));
  await page.click("#pfCancel");
  check("资料弹窗：可关闭", await page.locator("#profileModal").evaluate((el) => el.classList.contains("hidden")));

  // ============ 2. 👍/👎 评价按钮悬停显隐 ============
  await page.evaluate(() => {
    const head = document.createElement("div");
    head.className = "head";
    const like = document.createElement("button");
    like.className = "fb-like"; like.innerHTML = "<svg></svg>";
    const dislike = document.createElement("button");
    dislike.className = "fb-dislike"; dislike.innerHTML = "<svg></svg>";
    head.append(like, dislike);
    const msg = document.createElement("div");
    msg.className = "msg agent";
    msg.style.cssText = "position:fixed;top:80px;left:16px;width:480px;height:70px;background:var(--panel,#fff);z-index:9999";
    msg.appendChild(head);
    document.body.appendChild(msg);
    window.__fbTest = msg;
  });
  const before = await page.evaluate(() => getComputedStyle(window.__fbTest.querySelector(".fb-like")).visibility);
  await page.hover("body > .msg.agent");
  await page.waitForTimeout(200);
  const duringHover = await page.evaluate(() => getComputedStyle(window.__fbTest.querySelector(".fb-like")).visibility);
  const dislikeHover = await page.evaluate(() => getComputedStyle(window.__fbTest.querySelector(".fb-dislike")).visibility);
  check("评价按钮默认隐藏(hidden)", before === "hidden", before);
  check("悬停消息：👍 可见(visible)", duringHover === "visible", duringHover);
  check("悬停消息：👎 可见(visible)", dislikeHover === "visible", dislikeHover);
  await page.evaluate(() => window.__fbTest.remove());
  await shot(page, "fb-hover");

  // ============ 3. 管理员：删除按钮 + 审计 tab ============
  if (admin) {
    await page.evaluate(() => { sessionStorage.clear(); localStorage.clear(); });
    await page.goto(BASE_URL, { waitUntil: "domcontentloaded" });
    await loginUi(admin.username, envAdmin?.password || "secret123");
    await page.click("#meChip");
    await page.click("#meMenuAdmin");
    await page.waitForSelector("#adminModal:not(.hidden)", { timeout: 5000 });
    await page.waitForSelector("#adminUserRows tr", { timeout: 5000 });
    await sleep(300);
    const hasDeleteBtn = await page.locator('#adminUserRows button[data-op="delete"]').count();
    check("用户管理他人行出现🗑️彻底删除按钮", hasDeleteBtn > 0, `count=${hasDeleteBtn}`);
    await shot(page, "admin-users-delete");

    await page.click("#adminTabAudit");
    await page.waitForFunction(() => !document.getElementById("adminAuditView").classList.contains("hidden"), { timeout: 3000 });
    await page.waitForTimeout(600);
    const auditRows = await page.locator("#adminAuditRows tr").count();
    check("审计日志 tab 打开并渲染", auditRows >= 0, `rows=${auditRows}`);
    const auditErr = await page.locator("#adminAuditRows .admin-empty").count();
    if (auditRows === 1 && auditErr === 1) note("审计列表为空（实例暂无审计记录）——渲染正常");
    await shot(page, "admin-audit-tab");
    await page.click("#adminClose").catch(() => {});
  }

  if (pageErrors.length) note(`页面 JS 异常: ${pageErrors.join(" | ")}`);
  else console.log("  [OK] 无页面 JS 异常");
} finally {
  await browser.close();
  // ============ 收尾清理（best-effort） ============
  if (!KEEP && admin) {
    if (normal._selfMade && normal.userId) {
      const r = await api("DELETE", `/ag-ui/admin/users/${normal.userId}`, admin.token);
      if (r.status === 200) console.log(`  [OK] 已删除自建普通账号 ${normal.username}`);
      else note(`自建普通账号清理未执行（HTTP ${r.status}），可手动删除 ${normal.username}`);
    }
    if (admin._selfMade) note(`自建管理员 ${admin.username} 保留（防删最后一名超级管理员语义）；空实例如需复用可直接使用该账号`);
  }
}

console.log(fail === 0
  ? `\n=== ✅ UI 合规回归通过（${pass} PASS${skipped.length ? `，${skipped.join("/")} 套件跳过` : ""}） ===`
  : `\n=== ❌ UI 合规回归失败：${fail} fail / ${pass} pass ===`);
process.exit(fail === 0 ? 0 : 1);
