// 界面人性化第六轮验证（Playwright，真实浏览器）：
//   1. 全局搜索（🔎 跨知聚）：点结果跳转后同样有「命中词高亮 + 上/下一条导航条」
//      （与知聚内搜索一致——此前全局搜索跳转不高亮、无导航条）；
//   2. 图片灯箱：新增「旋转 90°」与「下载原图」；
//   3. 拖拽上传：把文件拖到聊天区即加入待发送附件（拖入时显示覆盖层）。
//
// 用法：cd tools && HEADLESS=1 node ui-ux-round6.mjs
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

const PNG_RED = Buffer.from("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==", "base64");
const PNG_BLUE = Buffer.from("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPj/HwADAgH/p+6eXwAAAABJRU5ErkJggg==", "base64");

const stamp = Date.now().toString(36).slice(-5);
const gName = "R6-" + stamp;
const term = "r6hit" + stamp;

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

  // 两条含关键词（供导航条翻页）+ 若干填充
  for (let i = 1; i <= 6; i++) {
    const content = (i === 2 || i === 5) ? `第 ${i} 条：命中关键词 ${term} 在此` : `R6 消息 ${i}`;
    await api("POST", "/ag-ui/group/message/send", { groupId: gid, userId, content });
    await sleep(20);
  }
  const upload = async (name, buffer) => {
    const up = await page.request.post(BASE_URL + "/ag-ui/upload", { headers: { Authorization: "Bearer " + token }, multipart: { file: { name, mimeType: "image/png", buffer } } });
    return (await up.json()).attachments?.[0];
  };
  const att1 = await upload("r6-a.png", PNG_RED);
  const att2 = await upload("r6-b.png", PNG_BLUE);
  await api("POST", "/ag-ui/group/message/send", { groupId: gid, userId, content: "R6 双图消息", attachments: [att1, att2] });

  await page.reload({ waitUntil: "domcontentloaded" });
  await page.waitForSelector(".group-item", { timeout: 20000 });
  await page.locator(".group-item", { hasText: gName }).first().click();
  await page.waitForFunction(() => document.querySelectorAll("#messages .msg").length >= 7, { timeout: 20000 });
  await sleep(400);

  /* ============ 1) 全局搜索：跳转后同样高亮 + 导航条 ============ */
  await page.locator("#globalSearchBtn").click();
  await page.waitForSelector("#globalSearchModal:not(.hidden)", { timeout: 8000 });
  await page.fill("#gsKeyword", term);
  await page.locator("#gsKeyword").press("Enter");
  await page.waitForFunction((g) => !!document.querySelector(`#gsResults .gs-row[data-gid="${g}"]`), gid, { timeout: 15000 });
  const gsRows = await page.locator(`#gsResults .gs-row[data-gid="${gid}"]`).count();
  check("全局搜索命中本知聚的消息", gsRows >= 1, gsRows + " 条");

  await page.locator(`#gsResults .gs-row[data-gid="${gid}"]`).first().click();
  await page.locator("#searchNav").waitFor({ state: "visible", timeout: 8000 });
  await page.waitForFunction(() => document.querySelectorAll("#messages mark.hl-term").length >= 1, null, { timeout: 8000 });
  const g1 = await page.evaluate(() => ({
    gsHidden: document.getElementById("globalSearchModal").classList.contains("hidden"),
    navText: document.getElementById("searchNavPos").textContent.trim(),
    marks: [...document.querySelectorAll("#messages mark.hl-term")].map((m) => m.textContent),
    mid: document.querySelector("#messages mark.hl-term")?.closest(".msg")?.dataset.mid || null,
    hitMids: searchHits.map((h) => h.messageId),
    pos: searchPos,
  }));
  check("全局搜索跳转后关闭检索弹窗", g1.gsHidden);
  check("全局搜索跳转后出现「上/下一条」导航条", /^\d+ \/ \d+$/.test(g1.navText), g1.navText);
  check("全局搜索跳转后命中词高亮（文本=关键词）", g1.marks.length >= 1 && g1.marks.every((x) => x.toLowerCase() === term.toLowerCase()), g1.marks.join(","));
  check("高亮落在被点击的那条消息上", g1.mid === g1.hitMids[g1.pos], `${g1.mid} vs ${g1.hitMids[g1.pos]}`);
  await page.locator("#searchNavClose").click();
  await sleep(200);

  /* ============ 2) 图片灯箱：旋转 + 下载 ============ */
  await page.evaluate(() => { const el = document.getElementById("messages"); el.scrollTop = el.scrollHeight; });
  await page.waitForSelector("#messages .att-img-wrap", { timeout: 10000 });
  const dualMsg = page.locator("#messages .msg").filter({ has: page.locator(".att-img-wrap") }).last();
  await dualMsg.locator(".att-img-wrap").nth(0).click();
  await page.waitForSelector("#lightbox:not(.hidden)", { timeout: 8000 });

  const t0 = await page.evaluate(() => document.getElementById("lbImg").style.transform);
  await page.locator("#lbRotate").click();
  await sleep(150);
  const t1 = await page.evaluate(() => document.getElementById("lbImg").style.transform);
  check("「旋转」在变换中加入 rotate(90deg)", /rotate\(90deg\)/.test(t1) && t1 !== t0, t1);
  await page.locator("#lbRotate").click();
  await sleep(120);
  check("再次旋转到 180°", /rotate\(180deg\)/.test(await page.evaluate(() => document.getElementById("lbImg").style.transform)));

  const [download] = await Promise.all([
    page.waitForEvent("download", { timeout: 8000 }),
    page.locator("#lbDownload").click(),
  ]);
  check("「下载原图」触发下载且文件名正确", download.suggestedFilename() === "r6-a.png", download.suggestedFilename());

  await page.keyboard.press("Escape");
  await sleep(200);
  check("Esc 关闭灯箱", await page.evaluate(() => document.getElementById("lightbox").classList.contains("hidden")));

  /* ============ 3) 拖拽上传 ============ */
  const dtHandle = await page.evaluateHandle(() => {
    const dt = new DataTransfer();
    dt.items.add(new File([new Uint8Array([104, 105])], "drop-test.txt", { type: "text/plain" }));
    return dt;
  });
  await page.dispatchEvent("main.chat", "dragenter", { dataTransfer: dtHandle });
  await sleep(150);
  check("拖入文件时显示覆盖层", await page.evaluate(() => !document.getElementById("dropOverlay").classList.contains("hidden")));
  await page.dispatchEvent("main.chat", "drop", { dataTransfer: dtHandle });
  await sleep(250);
  const drop = await page.evaluate(() => ({
    overlayHidden: document.getElementById("dropOverlay").classList.contains("hidden"),
    chips: [...document.querySelectorAll("#attachList .attach-chip")].map((c) => c.textContent || ""),
    hasList: document.getElementById("attachList").classList.contains("show"),
  }));
  check("拖拽松手后覆盖层收起", drop.overlayHidden);
  check("拖入的文件加入待发送附件", drop.hasList && drop.chips.some((c) => c.includes("drop-test.txt")), JSON.stringify(drop.chips));

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
