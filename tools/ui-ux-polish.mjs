// 界面人性化改动验证（Playwright，真实浏览器）：
//   1. 输入区「＋」弹出菜单（附件/语音/画布），点外部收起；
//   2. 草稿按群持久：输入后切群再切回，内容仍在；
//   3. 中文输入法组字中回车不误发（isComposing / keyCode 229）；
//   4. 窄屏抽屉：☰ 打开知聚抽屉、👥 打开成员抽屉，点遮罩收起；
//   5. 弹窗焦点：打开后焦点进入弹窗内。
//
// 用法：cd tools && HEADLESS=1 node ui-ux-polish.mjs
import { chromium } from "playwright";
import path from "node:path";
import { fileURLToPath } from "node:url";

const __dirname = path.dirname(fileURLToPath(import.meta.url));

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
let gA = null, gB = null, token = null;

try {
  await page.goto(BASE_URL, { waitUntil: "domcontentloaded" });
  await page.waitForSelector("#authOverlay", { timeout: 15000 });
  if (!(await page.evaluate(() => document.getElementById("authOverlay").classList.contains("hidden")))) {
    await page.fill("#authUsername", USERNAME);
    await page.fill("#authPassword", PASSWORD);
    await page.click("#authSubmit");
    await page.waitForFunction(() => document.getElementById("authOverlay")?.classList.contains("hidden"), { timeout: 20000 });
  }
  token = (await (await page.request.post(BASE_URL + "/ag-ui/user/login", { data: { username: USERNAME, password: PASSWORD } })).json()).token;
  const H = { Authorization: "Bearer " + token };
  const api = async (method, p, data) => {
    const r = await page.request.fetch(BASE_URL + p, { method, headers: { ...H, "Content-Type": "application/json" }, data: data === undefined ? undefined : JSON.stringify(data) });
    return { status: r.status(), body: await r.json().catch(() => null) };
  };
  const userId = (await api("GET", "/ag-ui/user/me")).body.userId;
  console.log("已登录 " + USERNAME + "\n");
  const stamp = Date.now().toString(36).slice(-5);
  gA = (await api("POST", "/ag-ui/group/create", { groupName: "UX-A-" + stamp, ownerId: userId, memberIds: [], members: [] })).body.groupId;
  gB = (await api("POST", "/ag-ui/group/create", { groupName: "UX-B-" + stamp, ownerId: userId, memberIds: [], members: [] })).body.groupId;
  const sendMsg = (gid, text) => api("POST", "/ag-ui/group/message/send", { groupId: gid, userId, content: text });
  await sendMsg(gA, "A 群第一条");
  await sendMsg(gA, "A 群第二条");
  await sendMsg(gB, "B 群第一条");

  await page.reload({ waitUntil: "domcontentloaded" });
  await page.waitForSelector(".group-item", { timeout: 20000 });

  // ---- 1) 输入区「＋」菜单 ----
  await page.locator(".group-item", { hasText: "UX-A-" }).first().click();
  await page.waitForSelector("#input", { timeout: 10000 });
  check("输入区有「＋」按钮", await page.locator("#composerPlusBtn").isVisible());
  check("菜单默认隐藏", await page.locator("#composerPlusMenu").isHidden());
  await page.locator("#composerPlusBtn").click();
  await page.waitForSelector("#composerPlusMenu:not(.hidden)", { timeout: 5000 });
  check("点「＋」弹出菜单", true);
  check("菜单含 附件/语音/画布", (await page.locator("#composerPlusMenu .composer-plus-item").count()) === 3);
  await page.locator("#messages").click({ position: { x: 5, y: 5 } });
  await sleep(150);
  check("点外部收起菜单", await page.locator("#composerPlusMenu").isHidden());

  // 主行不再是四个图标：只剩 ＋ / 讨论 / 发送
  check("主行图标精简（附件/语音/画布移入菜单）", (await page.locator(".composer-row .composer-icon-btn").count()) === 2);

  // ---- 2) 草稿按群持久 ----
  await page.fill("#input", "这是一段未发送的草稿");
  await sleep(200);
  await page.locator(".group-item", { hasText: "UX-B-" }).first().click();
  await page.waitForSelector(".msg", { timeout: 10000 });
  await sleep(300);
  const inB = await page.locator("#input").inputValue();
  check("切到 B 群：输入框恢复 B 的草稿（空）", inB === "", JSON.stringify(inB));
  await page.fill("#input", "B 群草稿");
  await sleep(200);
  await page.locator(".group-item", { hasText: "UX-A-" }).first().click();
  await page.waitForSelector(".msg", { timeout: 10000 });
  await sleep(300);
  const backA = await page.locator("#input").inputValue();
  check("切回 A 群：A 的草稿被恢复", backA === "这是一段未发送的草稿", JSON.stringify(backA));

  // ---- 3) 输入法组字中回车不误发 ----
  const before = await page.locator(".msg").count();
  await page.evaluate(() => {
    const el = document.getElementById("input");
    el.focus();
    el.value = "组字中";
    el.dispatchEvent(new KeyboardEvent("compositionstart", { bubbles: true }));
    el.dispatchEvent(new KeyboardEvent("keydown", { key: "Enter", isComposing: true, bubbles: true, cancelable: true }));
    el.dispatchEvent(new KeyboardEvent("compositionend", { bubbles: true }));
  });
  await sleep(400);
  const after = await page.locator(".msg").count();
  check("组字中回车不发送（消息数不变、输入内容保留）", after === before && (await page.locator("#input").inputValue()) === "组字中",
    `msg ${before}->${after}`);

  // ---- 4) 窄屏抽屉 ----
  await page.setViewportSize({ width: 700, height: 800 });
  await sleep(300);
  check("窄屏显示 ☰ 按钮", await page.locator("#navToggle").isVisible());
  check("窄屏：知聚侧栏默认收起（抽屉未开）", !(await page.locator(".panel.groups").evaluate((el) => el.classList.contains("drawer-open"))));
  await page.locator("#navToggle").click();
  await sleep(250);
  check("点 ☰ 打开知聚抽屉", await page.locator(".panel.groups").evaluate((el) => el.classList.contains("drawer-open")));
  check("出现遮罩", await page.locator("#drawerBackdrop").isVisible());
  await page.locator("#drawerBackdrop").click();
  await sleep(250);
  check("点遮罩收起抽屉", !(await page.locator(".panel.groups").evaluate((el) => el.classList.contains("drawer-open"))));
  await page.locator("#membersToggle").click();
  await sleep(250);
  check("点 👥 打开成员抽屉", await page.locator(".panel.members").evaluate((el) => el.classList.contains("drawer-open")));
  await page.locator("#drawerBackdrop").click();
  await sleep(200);
  await page.setViewportSize({ width: 1280, height: 800 });
  await sleep(200);

  // ---- 5) 弹窗焦点入框 ----
  await page.locator("#searchBtn").click();
  await page.waitForSelector("#searchModal:not(.hidden)", { timeout: 5000 });
  await sleep(250);
  const focusedInside = await page.evaluate(() => {
    const ae = document.activeElement;
    const modal = document.querySelector(".modal-overlay:not(.hidden)");
    return !!(modal && ae && modal.contains(ae));
  });
  check("弹窗打开后焦点落在弹窗内", focusedInside);
  await page.keyboard.press("Escape");

  console.log(`\n=== ${pass} 通过 / ${fail} 失败 ===`);
} catch (e) {
  fail++;
  console.error("\n界面验证异常：", e.message);
  try { await page.screenshot({ path: path.join(__dirname, "screenshots", "ux-polish-fail.png") }); } catch { /* 忽略 */ }
} finally {
  for (const gid of [gA, gB]) {
    try {
      if (gid && token) await page.request.fetch(BASE_URL + "/ag-ui/group/disband", {
        method: "POST", headers: { Authorization: "Bearer " + token, "Content-Type": "application/json" },
        data: JSON.stringify({ groupId: gid, operatorId: null }),
      });
    } catch { /* 忽略 */ }
  }
  await browser.close();
  process.exit(fail === 0 ? 0 : 1);
}
