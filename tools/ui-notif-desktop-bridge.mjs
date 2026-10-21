// 桌面版系统通知桥回归（Playwright）：模拟 WebView2 宿主注入的 window.chrome.webview。
//   1. 桌面宿主下 isDesktopHost() 为真、通知权限视为 granted（不显示权限引导）；
//   2. showSystemNotification → postMessage({type:"notify",title,body})，body 截断 200；
//   3. 免打扰时段 / 按类型静音仍然拦截（不上送宿主）；
//   4. notifyNewMessage 走桥；本人消息不弹；
//   5. 未读计数（1.0.305）：消息 + 非 message 通知合并上送，通知已读后同步，负数归零；
//   6. 任务栏进度（1.0.306/307）：计划 done/total + 当前步骤 → normal / paused，技能调用推进百分比，结束后 none；
//   7. 跳转列表（1.0.306）：最近非私密知聚按活跃时间倒序；
//   8. 非桌面宿主（无 chrome.webview）不误判、仍走浏览器 Notification、不上送任何计数；
//   9. 无页面 JS 异常。
// 用法：cd tools && HEADLESS=1 node ui-notif-desktop-bridge.mjs
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

const browser = await chromium.launch({ headless: HEADLESS });
const ctx = await browser.newContext({ viewport: { width: 1400, height: 900 } });
const errors = [];

async function openPage({ desktop }) {
  const page = await ctx.newPage();
  page.on("pageerror", (e) => errors.push("pageerror: " + e.message));
  page.on("console", (m) => { if (m.type() === "error") errors.push("console: " + m.text()); });
  await page.addInitScript((isDesktop) => {
    // 浏览器里 Notification 明确设为 denied：桌面宿主应当绕过它。
    class FakeNotification {
      static permission = "denied";
      static requestPermission = async () => "denied";
      constructor() { this.onclick = null; }
      close() {}
    }
    window.Notification = FakeNotification;
    window.__posted = [];
    if (isDesktop) {
      const host = window.chrome || {};
      host.webview = { postMessage: (msg) => { window.__posted.push(msg); } };
      window.chrome = host;
    }
  }, desktop);
  await page.goto(BASE_URL, { waitUntil: "domcontentloaded" });
  await page.waitForSelector("#authOverlay", { state: "attached", timeout: 15000 });
  if (!(await page.evaluate(() => document.getElementById("authOverlay")?.classList.contains("hidden")))) {
    await page.fill("#authUsername", USERNAME);
    await page.fill("#authPassword", PASSWORD);
    await page.click("#authSubmit");
    await page.waitForFunction(() => document.getElementById("authOverlay")?.classList.contains("hidden"), { timeout: 20000 });
  }
  return page;
}

/** 读取并清空宿主收到的消息。 */
async function drain(page) {
  return page.evaluate(() => { const v = window.__posted.slice(); window.__posted.length = 0; return v; });
}

try {
  // ---- A. 桌面宿主 ----
  const a = await openPage({ desktop: true });
  check("桌面宿主被识别", await a.evaluate(() => isDesktopHost() === true));
  check("桌面宿主下权限视为 granted", (await a.evaluate(() => notifPermission())) === "granted");
  check("桌面宿主下浏览器真权限仍为 denied（说明未依赖它）", (await a.evaluate(() => Notification.permission)) === "denied");

  await a.click("#notifBtn");
  await a.waitForSelector("#notifPanel:not(.hidden)", { timeout: 5000 });
  check("桌面宿主不显示权限引导", await a.locator("#notifPermHint").evaluate((el) => el.classList.contains("hidden")));
  await a.keyboard.press("Escape").catch(() => {});

  // 基础投递
  await drain(a);
  await a.evaluate(() => showSystemNotification("标题甲", "正文乙", "group_g1", "info"));
  let posted = await drain(a);
  const firstNotify = posted.filter((m) => m.type === "notify");
  check("showSystemNotification 上送宿主", firstNotify.length === 1, JSON.stringify(firstNotify[0] || null));
  check("payload.type = notify", firstNotify[0]?.type === "notify");
  check("payload 携带 title", firstNotify[0]?.title === "标题甲");
  check("payload 携带 body", firstNotify[0]?.body === "正文乙");

  // body 截断 200
  await a.evaluate(() => showSystemNotification("长文", "x".repeat(300), "agui", "info"));
  posted = await drain(a);
  const longNotify = posted.find((m) => m.type === "notify");
  check("body 截断到 200 字", longNotify?.body?.length === 200, String(longNotify?.body?.length));

  // 未读计数桥（1.0.305）：未读消息 + 非 message 类未读通知（避免与消息未读重复计数）
  await a.evaluate(() => {
    state.groupUnread.clear();
    state.groupUnread.set("gtest", { unreadCount: 3, byTopic: {} });
    state.notifications = [
      { id: "n1", type: "approval", read: false, icon: "🔐", title: "审批", body: "待处理", ts: Date.now() },
      { id: "n2", type: "error", read: false, icon: "⚠️", title: "错误", body: "失败", ts: Date.now() },
      { id: "n3", type: "message", read: false, icon: "💬", title: "消息", body: "重复", ts: Date.now() },
      { id: "n4", type: "info", read: true, icon: "ℹ️", title: "已读", body: "忽略", ts: Date.now() },
    ];
    updateDocTitle();
  });
  posted = await drain(a);
  check("未读计数上送宿主", posted.some((m) => m.type === "unread" && m.count === 5 && m.messages === 3 && m.notifications === 2), JSON.stringify(posted.filter((m) => m.type === "unread")));

  // 通知标为已读 → renderNotifications 自动同步（1.0.305）
  await a.evaluate(() => { state.notifications[0].read = true; renderNotifications(); });
  posted = await drain(a);
  check("通知已读后同步未读", posted.some((m) => m.type === "unread" && m.count === 4 && m.notifications === 1), JSON.stringify(posted.filter((m) => m.type === "unread")));

  // 负数归零
  await a.evaluate(() => {
    state.groupUnread.clear();
    state.groupUnread.set("gneg", { unreadCount: -3, byTopic: {} });
    state.notifications = [];
    updateDocTitle();
  });
  posted = await drain(a);
  const clamped = posted.find((m) => m.type === "unread");
  check("负数未读归零", clamped?.count === 0, JSON.stringify(posted.filter((m) => m.type === "unread")));

  await a.evaluate(() => { state.groupUnread.clear(); state.notifications = []; renderNotifications(); updateDocTitle(); });
  await drain(a);

  // 任务栏进度（1.0.306）：计划 done/total / paused / none
  await a.evaluate(() => {
    state.rooms.clear();
    const r = room("gprog");
    r.messages.push({
      id: "mprog", senderType: "agent", streaming: true,
      plan: { title: "计划", paused: false, steps: [{ text: "a", done: true }, { text: "b", done: false }, { text: "c", done: false }] },
    });
    syncDesktopTaskbarProgress();
  });
  posted = await drain(a);
  check("任务栏进度按计划上送", posted.some((m) => m.type === "progress" && m.state === "normal" && m.value === 1 && m.total === 3 && m.label === "b"), JSON.stringify(posted.filter((m) => m.type === "progress")));

  await a.evaluate(() => {
    state.rooms.get("gprog").messages[0].plan.paused = true;
    syncDesktopTaskbarProgress();
  });
  posted = await drain(a);
  check("计划暂停 → 任务栏 paused", posted.some((m) => m.type === "progress" && m.state === "paused"), JSON.stringify(posted.filter((m) => m.type === "progress")));

  await a.evaluate(() => {
    state.rooms.get("gprog").messages[0].streaming = false;
    syncDesktopTaskbarProgress();
    state.rooms.clear();
  });
  posted = await drain(a);
  check("任务结束后清除进度", posted.some((m) => m.type === "progress" && m.state === "none"), JSON.stringify(posted.filter((m) => m.type === "progress")));

  // 无计划时按技能 / 工具调用推进（1.0.307）
  await a.evaluate(() => {
    state.rooms.clear();
    const r = room("gtool");
    r.messages.push({
      id: "mtool", senderType: "agent", streaming: true,
      toolCalls: [{ id: "t1", name: "docx_report", done: true }, { id: "t2", name: "pptx_deck", done: false }],
    });
    syncDesktopTaskbarProgress();
  });
  posted = await drain(a);
  check("技能步骤进度按调用推进", posted.some((m) => m.type === "progress" && m.state === "normal" && m.value === 50 && m.total === 100 && m.label === "pptx_deck"), JSON.stringify(posted.filter((m) => m.type === "progress")));
  await a.evaluate(() => { state.rooms.clear(); syncDesktopTaskbarProgress(); });
  await drain(a);

  // 跳转列表（1.0.306）：最近非私密知聚，按活跃时间倒序
  await a.evaluate(() => {
    state.groups = [
      { groupId: "g1", groupName: "最近公开", isPrivate: false, lastMessageAt: 100 },
      { groupId: "g2", groupName: "更近公开", isPrivate: false, lastMessageAt: 300 },
      { groupId: "g3", groupName: "私密", isPrivate: true, lastMessageAt: 500 },
    ];
    syncDesktopRecents();
  });
  posted = await drain(a);
  const recentsOk = posted.some((m) => m.type === "recents" && (m.items || []).map((x) => x.id).join(",") === "g2,g1");
  check("跳转列表按最近排序并排除私密", recentsOk, JSON.stringify(posted.filter((m) => m.type === "recents")));
  await a.evaluate(() => { state.groups = []; syncDesktopRecents(); });
  await drain(a);

  // 免打扰：构造覆盖当前时刻的窗口
  await a.evaluate(() => {
    const d = new Date();
    const now = d.getHours() * 60 + d.getMinutes();
    notifyQuiet.start = (now + 1440 - 1) % 1440;
    notifyQuiet.end = (now + 2) % 1440;
  });
  await a.evaluate(() => showSystemNotification("静音中", "不应上送", "agui", "info"));
  posted = await drain(a);
  check("免打扰时段拦截桥消息", posted.every((m) => m.type !== "notify"), JSON.stringify(posted));
  await a.evaluate(() => { notifyQuiet.start = null; notifyQuiet.end = null; });

  // 按类型静音
  await a.evaluate(() => notifyMuted.add("message"));
  await a.evaluate(() => showSystemNotification("被静音", "不应上送", "agui", "message"));
  posted = await drain(a);
  check("类型静音拦截桥消息", posted.every((m) => m.type !== "notify"), JSON.stringify(posted));
  await a.evaluate(() => notifyMuted.delete("message"));

  // notifyNewMessage 走桥 + 本人消息不弹
  await a.evaluate(() => notifyNewMessage({ senderId: "someone", senderNickname: "张三", groupId: "g9" }, { content: "  你好\n世界  " }));
  posted = await drain(a);
  const msgNotify = posted.filter((m) => m.type === "notify");
  check("新消息通知走宿主桥", msgNotify.length === 1, JSON.stringify(msgNotify));
  check("新消息标题含发送者", String(msgNotify[0]?.title || "").includes("张三"), String(msgNotify[0]?.title || ""));
  check("新消息正文压缩空白", msgNotify[0]?.body === "你好 世界", String(msgNotify[0]?.body || ""));

  const selfId = await a.evaluate(() => state.memberId);
  await a.evaluate((id) => notifyNewMessage({ senderId: id, senderNickname: "我", groupId: "g9" }, { content: "自己发的" }), selfId);
  posted = await drain(a);
  check("本人消息不上送", posted.filter((m) => m.type === "notify").length === 0, JSON.stringify(posted));

  // ---- B. 非桌面宿主：不误判，仍走浏览器 Notification ----
  const b = await openPage({ desktop: false });
  check("无 chrome.webview 时不识别为桌面宿主", await b.evaluate(() => isDesktopHost() === false));
  check("浏览器宿主仍按真实权限判定", (await b.evaluate(() => notifPermission())) === "denied");
  await b.evaluate(() => showSystemNotification("浏览器", "denied 不应弹", "agui", "info"));
  check("浏览器 denied 时无宿主消息", (await drain(b)).every((m) => m.type !== "notify"));
  await b.evaluate(() => syncDesktopUnread());
  check("浏览器不上送未读计数", (await drain(b)).length === 0);
  await b.evaluate(() => { syncDesktopTaskbarProgress(); syncDesktopRecents(); });
  check("浏览器不上送进度/最近知聚", (await drain(b)).length === 0);

  // ---- C. 跳转列表常用任务：?action=admin 自动打开控制台（david 为管理员）----
  const c = await ctx.newPage();
  c.on("pageerror", (e) => errors.push("pageerror: " + e.message));
  c.on("console", (m) => { if (m.type() === "error") errors.push("console: " + m.text()); });
  await c.goto(BASE_URL + "/?action=admin", { waitUntil: "domcontentloaded" });
  await c.waitForSelector("#authOverlay", { state: "attached", timeout: 15000 });
  await c.waitForFunction(() => !document.getElementById("adminModal")?.classList.contains("hidden"), { timeout: 20000 }).catch(() => {});
  check("跳转任务 ?action=admin 打开控制台", await c.locator("#adminModal").evaluate((el) => !el.classList.contains("hidden")));
  await c.close();

  check("无页面 JS 异常", errors.length === 0, errors.slice(0, 3).join(" ; "));
} catch (e) {
  fail++;
  console.log("  FAIL  异常：" + (e && e.message));
} finally {
  await browser.close();
}
console.log(`\n桌面版系统通知桥验证：${pass} 通过 / ${fail} 失败`);
process.exit(fail ? 1 : 0);
