// 界面人性化第五轮验证（Playwright，真实浏览器）：
//   1. 搜索结果跳转后：消息内命中词高亮 + 左下角导航条（上/下一条 / 关闭）；
//   2. 图片灯箱：点消息内图片全屏放大（缩放 / 同条消息内左右切换 / Esc 关闭），
//      且 ctrl/⌘/shift 点击仍走「新标签打开」。
//
// 用法：cd tools && HEADLESS=1 node ui-ux-round5.mjs
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

// 1x1 有效 PNG（红色 / 蓝色），避免依赖外部样例文件
const PNG_RED = Buffer.from("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==", "base64");
const PNG_BLUE = Buffer.from("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPj/HwADAgH/p+6eXwAAAABJRU5ErkJggg==", "base64");

const stamp = Date.now().toString(36).slice(-5);
const gName = "R5-" + stamp;
const term = "zqhit" + stamp; // 含专属后缀的唯一关键词

const browser = await chromium.launch({ headless: HEADLESS });
const page = await browser.newPage({ viewport: { width: 1366, height: 800 } });
const errors = [];
page.on("pageerror", (e) => errors.push(String(e.message)));
page.on("console", (m) => { if (m.type() === "error") errors.push("console: " + m.text()); });
page.on("popup", (p) => { p.close().catch(() => { /* 忽略 */ }); }); // ctrl+点击图片会开新标签，直接关掉

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

  // 8 条文本消息，其中第 3、6 条含关键词（供搜索命中两条，触发导航条）
  for (let i = 1; i <= 8; i++) {
    const content = (i === 3 || i === 6) ? `第 ${i} 条：命中关键词 ${term} 在此` : `R5 消息 ${i}`;
    await api("POST", "/ag-ui/group/message/send", { groupId: gid, userId, content });
    await sleep(20);
  }

  // 一条含 2 张图片的消息（灯箱需要同条消息内多图才能左右切换）
  const upload = async (name, buffer) => {
    const up = await page.request.post(BASE_URL + "/ag-ui/upload", { headers: { Authorization: "Bearer " + token }, multipart: { file: { name, mimeType: "image/png", buffer } } });
    return (await up.json()).attachments?.[0];
  };
  const att1 = await upload("r5-a.png", PNG_RED);
  const att2 = await upload("r5-b.png", PNG_BLUE);
  const imgSent = await api("POST", "/ag-ui/group/message/send", { groupId: gid, userId, content: "R5 双图消息", attachments: [att1, att2] });
  check("发送含 2 张图片的消息", imgSent.status === 200 && att1?.kind === "image" && att2?.kind === "image", JSON.stringify({ a: att1?.kind, b: att2?.kind }));

  await page.reload({ waitUntil: "domcontentloaded" });
  await page.waitForSelector(".group-item", { timeout: 20000 });
  await page.locator(".group-item", { hasText: gName }).first().click();
  await page.waitForFunction(() => document.querySelectorAll("#messages .msg").length >= 9, { timeout: 20000 });
  await sleep(400);

  /* ============ 1) 搜索结果命中词高亮 + 导航条 ============ */
  check("进入知聚后「🔍 搜索」按钮可用", !(await page.locator("#searchBtn").isDisabled()));
  await page.locator("#searchBtn").click();
  await page.waitForSelector("#searchModal:not(.hidden)", { timeout: 8000 });
  await page.fill("#searchInput", term);
  await page.locator("#searchGo").click();
  await page.waitForFunction(() => document.querySelectorAll("#searchResults .search-hit").length >= 2, null, { timeout: 15000 });
  const hitCount = await page.locator("#searchResults .search-hit").count();
  check("搜索命中 2 条含关键词的消息", hitCount >= 2, hitCount + " 条");

  await page.locator("#searchResults .search-hit").first().click();
  await page.locator("#searchNav").waitFor({ state: "visible", timeout: 8000 });
  await page.waitForFunction(() => document.querySelectorAll("#messages mark.hl-term").length >= 1, null, { timeout: 8000 });
  const s1 = await page.evaluate(() => {
    const mark = document.querySelector("#messages mark.hl-term");
    return {
      pos: searchPos,
      modalHidden: document.getElementById("searchModal").classList.contains("hidden"),
      navVisible: !document.getElementById("searchNav").classList.contains("hidden"),
      navText: document.getElementById("searchNavPos").textContent,
      marks: [...document.querySelectorAll("#messages mark.hl-term")].map((m) => m.textContent),
      mid: mark?.closest(".msg")?.dataset.mid || null,
      hits: searchHits.map((h) => h.messageId),
    };
  });
  check("跳转后搜索弹窗自动收起", s1.modalHidden);
  check("左下角导航条出现，计数形如「1 / N」", s1.navVisible && /^1 \/ \d+$/.test(s1.navText.trim()), s1.navText);
  check("命中词在消息内以 <mark> 高亮（文本=关键词）", s1.marks.length >= 1 && s1.marks.every((x) => x.toLowerCase() === term.toLowerCase()), s1.marks.join(","));
  check("高亮落在命中的那条消息上", s1.mid === s1.hits[0], `${s1.mid} vs ${s1.hits[0]}`);

  await page.locator("#searchNavNext").click();
  await page.waitForFunction(() => searchPos === 1, null, { timeout: 8000 });
  await page.waitForFunction(() => document.querySelectorAll("#messages mark.hl-term").length >= 1, null, { timeout: 8000 });
  const s2 = await page.evaluate(() => ({
    pos: searchPos,
    navText: document.getElementById("searchNavPos").textContent,
    mid: document.querySelector("#messages mark.hl-term")?.closest(".msg")?.dataset.mid || null,
    hits: searchHits.map((h) => h.messageId),
  }));
  check("「下一条」前进到第 2 条命中（计数 2 / N）", s2.pos === 1 && /^2 \/ /.test(s2.navText.trim()), `${s2.pos} · ${s2.navText}`);
  check("高亮转移到下一条消息", s2.mid === s2.hits[1], `${s2.mid} vs ${s2.hits[1]}`);

  await page.locator("#searchNavPrev").click();
  await page.waitForFunction(() => searchPos === 0, null, { timeout: 8000 });
  check("「上一条」可回退到第 1 条命中", await page.evaluate(() => searchPos === 0));

  await page.locator("#searchNavClose").click();
  await sleep(200);
  const s3 = await page.evaluate(() => ({
    marks: document.querySelectorAll("#messages mark.hl-term").length,
    navHidden: document.getElementById("searchNav").classList.contains("hidden"),
  }));
  check("「关闭」后高亮清空、导航条隐藏", s3.marks === 0 && s3.navHidden, JSON.stringify(s3));

  /* ============ 2) 图片灯箱 ============ */
  // 滚到底部确保双图消息在虚拟窗口内
  await page.evaluate(() => { const el = document.getElementById("messages"); el.scrollTop = el.scrollHeight; });
  await page.waitForSelector("#messages .att-img-wrap", { timeout: 10000 });
  await page.evaluate(() => {
    // 定位到含 2 张图片的那条消息，滚到可见
    const msg = [...document.querySelectorAll("#messages .msg")].find((m) => m.querySelectorAll(".att-img-wrap").length >= 2);
    msg?.scrollIntoView({ block: "center" });
  });
  await sleep(400);
  const hrefs = await page.evaluate(() => {
    const msg = [...document.querySelectorAll("#messages .msg")].find((m) => m.querySelectorAll(".att-img-wrap").length >= 2);
    return [...msg.querySelectorAll(".att-img-wrap")].map((a) => a.getAttribute("href"));
  });
  check("双图消息渲染出 2 个图片容器", hrefs.length >= 2, hrefs.length + " 个");

  // 双图消息（最后一条）中的图片容器
  const dualMsg = page.locator("#messages .msg").filter({ has: page.locator(".att-img-wrap") }).last();
  await dualMsg.locator(".att-img-wrap").nth(0).click();
  await page.waitForSelector("#lightbox:not(.hidden)", { timeout: 8000 });
  const lb1 = await page.evaluate(() => ({
    pos: document.getElementById("lbPos").textContent.trim(),
    src: document.getElementById("lbImg").getAttribute("src"),
  }));
  check("点图片打开灯箱，显示「1 / 2」", lb1.pos === "1 / 2", lb1.pos);
  check("灯箱展示被点击的那张图", lb1.src === hrefs[0], `${(lb1.src || "").slice(0, 40)}… vs ${(hrefs[0] || "").slice(0, 40)}…`);

  await page.locator("#lbNext").click();
  await sleep(200);
  const lb2 = await page.evaluate(() => ({
    pos: document.getElementById("lbPos").textContent.trim(),
    src: document.getElementById("lbImg").getAttribute("src"),
  }));
  check("「下一张」切到「2 / 2」", lb2.pos === "2 / 2", lb2.pos);
  check("图片已切换", lb2.src === hrefs[1] && lb2.src !== lb1.src);

  await page.locator("#lbZoomIn").click();
  await sleep(150);
  const zoom = await page.evaluate(() => ({
    transform: document.getElementById("lbImg").style.transform,
    zoomed: document.getElementById("lbImg").classList.contains("zoomed"),
    z: typeof lbZoom === "number" ? lbZoom : null,
  }));
  check("「放大」后应用 scale 变换（进入缩放态）", /scale\(/.test(zoom.transform) && zoom.zoomed && (zoom.z === null || zoom.z > 1), JSON.stringify(zoom));

  await page.locator("#lbZoomReset").click();
  await sleep(150);
  check("「重置」回到 1 倍", await page.evaluate(() => lbZoom === 1 && !document.getElementById("lbImg").classList.contains("zoomed")));

  await page.keyboard.press("Escape");
  await sleep(200);
  check("Esc 关闭灯箱", await page.evaluate(() => document.getElementById("lightbox").classList.contains("hidden")));

  // ctrl+点击：保留「新标签打开」手势，不应打开灯箱
  await dualMsg.locator(".att-img-wrap").nth(0).click({ modifiers: ["Control"] });
  await sleep(300);
  check("ctrl+点击图片不打开灯箱（走新标签）", await page.evaluate(() => document.getElementById("lightbox").classList.contains("hidden")));

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
