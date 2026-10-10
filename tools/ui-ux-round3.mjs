// 界面人性化第三轮验证（Playwright，真实浏览器）：
//   1. 未读分隔线：进入知聚时首条未读前显示「以下是新消息」，且不破坏虚拟行的计数；
//   2. 记住滚动位置：切走再回来停在原处（非贴底时）；
//   3. 侧栏一键收起 / 展开（页头 « / »），并持久化；
//   4. 双击消息 = 引用回复（选中文字时不触发）；
//   5. 切换知聚后自动聚焦输入框（修改资料里的偏好开关）。
//
// 用法：cd tools && HEADLESS=1 node ui-ux-round3.mjs
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
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

const stamp = Date.now().toString(36).slice(-5);
const g1Name = "R3-A-" + stamp, g2Name = "R3-B-" + stamp;

const browser = await chromium.launch({ headless: HEADLESS });
const page = await browser.newPage({ viewport: { width: 1366, height: 800 } });
const errors = [];
page.on("pageerror", (e) => errors.push(String(e.message)));
page.on("console", (m) => { if (m.type() === "error") errors.push("console: " + m.text()); });

let g1 = null, g2 = null, token = null, userId = null;

try {
  await page.goto(BASE_URL, { waitUntil: "domcontentloaded" });
  await page.waitForSelector("#authOverlay", { timeout: 15000 });
  if (!(await page.evaluate(() => document.getElementById("authOverlay").classList.contains("hidden")))) {
    await page.fill("#authUsername", USERNAME); await page.fill("#authPassword", PASSWORD); await page.click("#authSubmit");
    await page.waitForFunction(() => document.getElementById("authOverlay")?.classList.contains("hidden"), { timeout: 20000 });
  }
  token = (await (await page.request.post(BASE_URL + "/ag-ui/user/login", { data: { username: USERNAME, password: PASSWORD } })).json()).token;
  const H = { Authorization: "Bearer " + token, "Content-Type": "application/json" };
  const api = async (method, p, data) => {
    const r = await page.request.fetch(BASE_URL + p, { method, headers: H, data: data === undefined ? undefined : JSON.stringify(data) });
    return { status: r.status(), body: await r.json().catch(() => null) };
  };
  userId = (await api("GET", "/ag-ui/user/me")).body.userId;
  g1 = (await api("POST", "/ag-ui/group/create", { groupName: g1Name, ownerId: userId, memberIds: [], members: [] })).body.groupId;
  g2 = (await api("POST", "/ag-ui/group/create", { groupName: g2Name, ownerId: userId, memberIds: [], members: [] })).body.groupId;
  const sendMsg = (gid, text) => api("POST", "/ag-ui/group/message/send", { groupId: gid, userId, content: text });
  for (let i = 1; i <= 45; i++) await sendMsg(g1, `R3 消息 ${i} —— 用于滚动位置验证`);
  await sendMsg(g2, "B 群一条");

  await page.reload({ waitUntil: "domcontentloaded" });
  await page.waitForSelector(".group-item", { timeout: 20000 });
  await page.locator(".group-item", { hasText: g1Name }).first().click();
  await page.waitForSelector(".msg", { timeout: 15000 });
  await sleep(500);

  // ---- 1) 未读分隔线 ----
  const div = await page.evaluate(() => {
    window.getSelection()?.removeAllRanges();
    pendingUnreadDivider = { gid: state.activeGroupId, topicId: state.activeTopicId || "main", count: 3 };
    vscroll.force = true; virtualRender();
    const marker = document.querySelector("#messages .vmsg.unread-marker");
    const line = marker?.querySelector(".unread-divider");
    const msgs = [...document.querySelectorAll("#messages .vmsg")];
    const vmsgWithMsg = msgs.filter((v) => v.querySelector(":scope > .msg")).length;
    return {
      hasLine: !!line,
      text: line?.textContent || "",
      markers: document.querySelectorAll("#messages .unread-marker").length,
      // 每条消息恰好一个 .vmsg（wrapper 计数不重复）
      vmsgCount: msgs.length,
      msgCount: document.querySelectorAll("#messages .msg").length,
      vmsgWithMsg,
      lineAboveContent: !!marker && marker.querySelector(".msg") !== null,
    };
  });
  check("未读时显示「以下是新消息」分隔线", div.hasLine && div.text.trim().length > 0, div.text.trim());
  check("分隔线只有一个", div.markers === 1, String(div.markers));
  check("分隔线与消息同处一个 .vmsg（不重复计数）", div.vmsgCount === div.msgCount && div.vmsgWithMsg === 1, `vmsg=${div.vmsgCount} msg=${div.msgCount}`);
  // 清除分隔线
  await page.evaluate(() => { pendingUnreadDivider = null; unreadDividerId = null; vscroll.force = true; virtualRender(); });
  await sleep(150);
  check("清除后分隔线消失", (await page.locator("#messages .unread-divider").count()) === 0);

  // ---- 4) 双击 = 引用回复 ----
  await page.evaluate(() => clearReplyTo());
  const dblOkay = await page.evaluate(() => {
    const msg = document.querySelector("#messages .msg");
    window.getSelection()?.removeAllRanges();
    msg.dispatchEvent(new MouseEvent("dblclick", { bubbles: true }));
    return !document.getElementById("replyBar").classList.contains("hidden");
  });
  check("双击消息（未选中文字）触发引用", dblOkay);
  await page.evaluate(() => clearReplyTo());
  const dblBlocked = await page.evaluate(() => {
    const msg = document.querySelector("#messages .msg");
    const range = document.createRange(); range.selectNodeContents(msg.querySelector(".content"));
    window.getSelection().removeAllRanges(); window.getSelection().addRange(range);
    msg.dispatchEvent(new MouseEvent("dblclick", { bubbles: true }));
    const shown = !document.getElementById("replyBar").classList.contains("hidden");
    window.getSelection().removeAllRanges();
    return shown;
  });
  check("选中文字后双击不触发引用", dblBlocked === false);

  // ---- 3) 侧栏一键收起 / 展开 ----
  const before = await page.evaluate(() => Math.round(document.querySelector(".panel.groups").getBoundingClientRect().width));
  await page.locator("#collapseGroupsBtn").click();
  await sleep(250);
  const collapsed = await page.evaluate(() => ({
    cls: document.querySelector(".layout").classList.contains("collapse-groups"),
    w: Math.round(document.querySelector(".panel.groups").getBoundingClientRect().width),
    label: document.getElementById("collapseGroupsBtn").textContent.trim(),
    pressed: document.getElementById("collapseGroupsBtn").getAttribute("aria-pressed"),
  }));
  check("点 « 收起知聚栏（宽度归零）", collapsed.cls && collapsed.w === 0, `w=${collapsed.w}`);
  check("按钮变为展开态（» / aria-pressed=true）", collapsed.label === "»" && collapsed.pressed === "true", `${collapsed.label}/${collapsed.pressed}`);
  const persistCollapsed = await page.evaluate(() => {
    const k = Object.keys(localStorage).find((x) => x.startsWith("agui.panelW."));
    return k ? JSON.parse(localStorage.getItem(k)).groupsCollapsed : null;
  });
  check("收起状态已持久化", persistCollapsed === true, String(persistCollapsed));
  await page.locator("#collapseGroupsBtn").click();
  await sleep(250);
  const expanded = await page.evaluate(() => Math.round(document.querySelector(".panel.groups").getBoundingClientRect().width));
  check("再点 » 展开恢复原宽", Math.abs(expanded - before) <= 2, `${before} -> ${expanded}`);

  // ---- 2) 记住滚动位置 ----
  await page.evaluate(() => { const el = document.getElementById("messages"); el.scrollTop = 260; el.dispatchEvent(new Event("scroll")); });
  await sleep(400);
  const savedTop = await page.evaluate(() => document.getElementById("messages").scrollTop);
  const stick = await page.evaluate(() => vscroll.stickBottom);
  await page.locator(".group-item", { hasText: g2Name }).first().click();
  await page.waitForSelector(".msg", { timeout: 10000 });
  await sleep(400);
  await page.locator(".group-item", { hasText: g1Name }).first().click();
  await page.waitForFunction(() => document.querySelectorAll("#messages .msg").length >= 40, { timeout: 15000 });
  await sleep(600);
  const restoredTop = await page.evaluate(() => document.getElementById("messages").scrollTop);
  check("离开时非贴底（记录为中途位置）", stick === false, `stick=${stick}`);
  check("切走再回来恢复原滚动位置", Math.abs(restoredTop - savedTop) <= 40, `${savedTop} -> ${restoredTop}`);
  // 回到底部应正常贴底
  await page.evaluate(() => jumpToBottom());
  await sleep(300);
  const atBottom = await page.evaluate(() => { const el = document.getElementById("messages"); return el.scrollHeight - el.clientHeight - el.scrollTop < 8; });
  check("「↓ 到最新」回到底部", atBottom);

  // ---- 5) 切换知聚后自动聚焦 ----
  const hasPref = await page.evaluate(() => !!document.getElementById("pfFocusOnSwitch"));
  check("修改资料里有「切换后聚焦输入框」开关", hasPref);
  await page.evaluate(() => { setFocusOnSwitchPref(false); document.getElementById("input").blur(); });
  await page.locator(".group-item", { hasText: g2Name }).first().click();
  await sleep(400);
  const focusOff = await page.evaluate(() => document.activeElement?.id || "");
  check("关闭偏好后切群不聚焦输入框", focusOff !== "input", `active=${focusOff}`);
  await page.evaluate(() => setFocusOnSwitchPref(true));
  await page.locator(".group-item", { hasText: g1Name }).first().click();
  await sleep(500);
  const focusOn = await page.evaluate(() => document.activeElement?.id || "");
  check("开启偏好后切群聚焦输入框", focusOn === "input", `active=${focusOn}`);

  check("无控制台报错", errors.length === 0, errors.slice(0, 3).join(" | "));
  console.log(`\n=== ${pass} 通过 / ${fail} 失败 ===`);
} catch (e) {
  fail++;
  console.error("\n界面验证异常：", e.message);
} finally {
  for (const gid of [g1, g2]) {
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
