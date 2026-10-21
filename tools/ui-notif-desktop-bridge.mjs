// 桌面版系统通知桥回归（Playwright）：模拟 WebView2 宿主注入的 window.chrome.webview。
//   1. 桌面宿主下 isDesktopHost() 为真、通知权限视为 granted（不显示权限引导）；
//   2. showSystemNotification → postMessage({type:"notify",title,body})，body 截断 200；
//   3. 免打扰时段 / 按类型静音仍然拦截（不上送宿主）；
//   4. notifyNewMessage 走桥；本人消息不弹；
//   5. 未读计数（1.0.304）：syncDesktopUnread / updateDocTitle 上送 {type:"unread",count}，负数归零；
//   6. 非桌面宿主（无 chrome.webview）不误判、仍走浏览器 Notification、不上送未读；
//   7. 无页面 JS 异常。
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

  // 未读计数桥（1.0.304）
  await a.evaluate(() => syncDesktopUnread(7));
  posted = await drain(a);
  check("未读计数上送宿主", posted.some((m) => m.type === "unread" && m.count === 7), JSON.stringify(posted));

  await a.evaluate(() => {
    state.groupUnread.clear();
    state.groupUnread.set("gtest", { unreadCount: 4, byTopic: {} });
    updateDocTitle();
  });
  posted = await drain(a);
  check("标题更新同步未读计数", posted.some((m) => m.type === "unread" && m.count === 4), JSON.stringify(posted));
  await a.evaluate(() => { state.groupUnread.delete("gtest"); updateDocTitle(); });
  await drain(a);

  await a.evaluate(() => syncDesktopUnread(-3));
  posted = await drain(a);
  check("负数未读归零", posted.some((m) => m.type === "unread" && m.count === 0), JSON.stringify(posted));

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
  await b.evaluate(() => syncDesktopUnread(5));
  check("浏览器不上送未读计数", (await drain(b)).length === 0);

  check("无页面 JS 异常", errors.length === 0, errors.slice(0, 3).join(" ; "));
} catch (e) {
  fail++;
  console.log("  FAIL  异常：" + (e && e.message));
} finally {
  await browser.close();
}
console.log(`\n桌面版系统通知桥验证：${pass} 通过 / ${fail} 失败`);
process.exit(fail ? 1 : 0);
