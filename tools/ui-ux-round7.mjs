// 界面人性化第七轮验证（Playwright，真实浏览器）：
//   1. 知聚内搜索弹窗内的结果导航：上/下一条（含 ↑↓）+ 「回车打开」，不关弹窗即可选行；
//   2. 表情选择器：输入区「＋」菜单里的表情，网格浮层点击插入光标处（选完 / 点外部 / Esc 收起）。
//
// 用法：cd tools && HEADLESS=1 node ui-ux-round7.mjs
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
const gName = "R7-" + stamp;
const term = "r7hit" + stamp;

const browser = await chromium.launch({ headless: HEADLESS });
const page = await browser.newPage({ viewport: { width: 1366, height: 800 } });
const errors = [];
page.on("pageerror", (e) => errors.push(String(e.message)));
page.on("console", (m) => { if (m.type() === "error") errors.push("console: " + m.text()); });

let gid = null, token = null, userId = null;

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

  gid = (await api("POST", "/ag-ui/group/create", { groupName: gName, ownerId: userId, memberIds: [], members: [] })).body.groupId;
  if (!gid) throw new Error("临时知聚未创建");
  for (let i = 1; i <= 6; i++) {
    const content = (i === 2 || i === 5) ? `第 ${i} 条：命中关键词 ${term} 在此` : `R7 消息 ${i}`;
    await api("POST", "/ag-ui/group/message/send", { groupId: gid, userId, content });
    await sleep(20);
  }

  await page.reload({ waitUntil: "domcontentloaded" });
  await page.waitForSelector(".group-item", { timeout: 20000 });
  await page.locator(".group-item", { hasText: gName }).first().click();
  await page.waitForFunction(() => document.querySelectorAll("#messages .msg").length >= 6, { timeout: 20000 });
  await sleep(400);

  /* ============ 1) 搜索弹窗内的结果导航 ============ */
  await page.locator("#searchBtn").click();
  await page.waitForSelector("#searchModal:not(.hidden)", { timeout: 8000 });
  check("打开搜索弹窗时结果导航隐藏", await page.evaluate(() => document.getElementById("searchModalNav").classList.contains("hidden")));
  await page.fill("#searchInput", term);
  await page.locator("#searchGo").click();
  await page.waitForFunction(() => document.querySelectorAll("#searchResults .search-hit").length >= 2, null, { timeout: 15000 });

  const nav0 = await page.evaluate(() => ({
    navVisible: !document.getElementById("searchModalNav").classList.contains("hidden"),
    pos: document.getElementById("searchResPos").textContent.trim(),
    activeIdx: [...document.querySelectorAll("#searchResults .search-hit")].findIndex((r) => r.classList.contains("active")),
    modalOpen: !document.getElementById("searchModal").classList.contains("hidden"),
  }));
  check("有结果时弹窗内出现导航，计数「1 / N」", nav0.navVisible && /^1 \/ \d+$/.test(nav0.pos), nav0.pos);
  check("默认选中第一条结果", nav0.activeIdx === 0, "idx=" + nav0.activeIdx);

  await page.locator("#searchResNext").click();
  await sleep(150);
  const nav1 = await page.evaluate(() => ({
    pos: document.getElementById("searchResPos").textContent.trim(),
    activeIdx: [...document.querySelectorAll("#searchResults .search-hit")].findIndex((r) => r.classList.contains("active")),
    modalOpen: !document.getElementById("searchModal").classList.contains("hidden"),
  }));
  check("「下一条」前进且弹窗不关", nav1.activeIdx === 1 && /^2 \/ /.test(nav1.pos) && nav1.modalOpen, `${nav1.activeIdx} · ${nav1.pos}`);

  await page.locator("#searchInput").press("ArrowUp");
  await sleep(150);
  check("↑ 回到上一条", await page.evaluate(() => [...document.querySelectorAll("#searchResults .search-hit")].findIndex((r) => r.classList.contains("active")) === 0));
  await page.locator("#searchInput").press("ArrowDown");
  await sleep(150);
  check("↓ 再前进到下一条", await page.evaluate(() => [...document.querySelectorAll("#searchResults .search-hit")].findIndex((r) => r.classList.contains("active")) === 1));

  // 回车打开当前选中行：关弹窗 + 命中词高亮
  await page.locator("#searchInput").press("Enter");
  await page.waitForFunction(() => document.getElementById("searchModal").classList.contains("hidden"), null, { timeout: 8000 });
  await page.waitForFunction(() => document.querySelectorAll("#messages mark.hl-term").length >= 1, null, { timeout: 8000 });
  const opened = await page.evaluate(() => ({
    modalHidden: document.getElementById("searchModal").classList.contains("hidden"),
    marks: [...document.querySelectorAll("#messages mark.hl-term")].map((m) => m.textContent),
    navVisible: !document.getElementById("searchNav").classList.contains("hidden"),
  }));
  check("「回车打开」关闭弹窗并跳转", opened.modalHidden);
  check("打开后命中词高亮 + 左下角导航条", opened.marks.length >= 1 && opened.marks.every((x) => x.toLowerCase() === term.toLowerCase()) && opened.navVisible, JSON.stringify(opened));
  await page.locator("#searchNavClose").click();
  await sleep(200);

  /* ============ 2) 表情选择器 ============ */
  await page.locator("#composerPlusBtn").click();
  await page.waitForSelector("#composerPlusMenu:not(.hidden)", { timeout: 5000 });
  check("「＋」菜单里有表情入口", (await page.locator("#composerPlusMenu #emojiBtn").count()) === 1);
  await page.locator("#emojiBtn").click();
  await sleep(200);
  const em1 = await page.evaluate(() => ({
    pickerVisible: !document.getElementById("emojiPicker").classList.contains("hidden"),
    plusHidden: document.getElementById("composerPlusMenu").classList.contains("hidden"),
    count: document.querySelectorAll("#emojiPicker .em").length,
  }));
  check("点表情打开网格浮层且「＋」菜单收起", em1.pickerVisible && em1.plusHidden, JSON.stringify(em1));
  check("表情面板含多个候选", em1.count >= 20, em1.count + " 个");

  const firstEmoji = await page.locator("#emojiPicker .em").first().textContent();
  await page.locator("#emojiPicker .em").first().click();
  await sleep(200);
  const inserted = await page.evaluate(() => ({
    value: document.getElementById("input").value,
    pickerOpen: !document.getElementById("emojiPicker").classList.contains("hidden"),
  }));
  check("点表情插入到输入框光标处", inserted.value.includes(firstEmoji), JSON.stringify(inserted.value));
  check("插入后面板保持打开（可连续插入）", inserted.pickerOpen);

  // 点外部收起
  await page.locator("#messages").click({ position: { x: 5, y: 5 } });
  await sleep(200);
  check("点外部收起浮层", await page.evaluate(() => document.getElementById("emojiPicker").classList.contains("hidden")));

  // 再从「＋」菜单打开，Esc 收起
  await page.locator("#composerPlusBtn").click();
  await page.waitForSelector("#composerPlusMenu:not(.hidden)", { timeout: 5000 });
  await page.locator("#emojiBtn").click();
  await sleep(150);
  check("可从「＋」菜单再次打开浮层", await page.evaluate(() => !document.getElementById("emojiPicker").classList.contains("hidden")));
  await page.keyboard.press("Escape");
  await sleep(200);
  check("Esc 收起浮层", await page.evaluate(() => document.getElementById("emojiPicker").classList.contains("hidden")));

  check("无控制台报错", errors.length === 0, errors.slice(0, 3).join(" | "));
  console.log(`\n=== ${pass} 通过 / ${fail} 失败 ===`);
} catch (e) {
  fail++;
  console.error("\n界面验证异常：", e.message);
} finally {
  try {
    if (gid && token) await page.request.fetch(BASE_URL + "/ag-ui/group/disband", {
      method: "POST", headers: { Authorization: "Bearer " + token, "Content-Type": "application/json" },
      data: JSON.stringify({ groupId: gid, operatorId: null }),
    });
  } catch { /* 忽略 */ }
  await browser.close();
  process.exit(fail === 0 ? 0 : 1);
}
