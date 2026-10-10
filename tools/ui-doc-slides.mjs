// 演示文稿「在线查看」增强验证（Playwright，真实浏览器）：
//   1. 打开带备注的 pptx → 走自绘幻灯片查看器（不是 iframe），首页幻灯片渲染出来；
//   2. 备注跟随当前页显示在下方（第 1 页显示第 1 页备注）；
//   3. 翻页 → 页码与备注同步更新；
//   4. 「播放」进入全屏播放，方向键翻页；
//   5. 播放中 Esc 只退出播放（弹窗仍在），再 Esc 收弹窗。
//
// 用法：cd tools && HEADLESS=1 node ui-doc-slides.mjs
//   样例：默认用 <repo>/artifacts/e2e/sample-notes.pptx（真实的 8 页带备注 pptx），可用 PPTX=/path 覆盖。
import { chromium } from "playwright";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const BASE_URL = process.env.BASE_URL || "http://localhost:5200";
const USERNAME = process.env.USERNAME || "david";
const PASSWORD = process.env.PASSWORD || "lingtong";
const HEADLESS = process.env.HEADLESS === "1";
const PPTX = process.env.PPTX || path.resolve(__dirname, "..", "artifacts", "e2e", "sample-notes.pptx");
const EXPECT_PAGES = Number(process.env.EXPECT_PAGES || 8);

let pass = 0, fail = 0;
const check = (label, cond, extra = "") => {
  if (cond) { pass++; console.log("  PASS  " + label + (extra ? "  — " + extra : "")); }
  else { fail++; console.log("  FAIL  " + label + (extra ? "  — " + extra : "")); }
};
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

const stamp = Date.now().toString(36).slice(-5);
const groupName = "幻灯片验证-" + stamp;

const browser = await chromium.launch({ headless: HEADLESS });
// deviceScaleFactor=2：高分屏（Windows 笔记本常见）下才照得出「只画进 1/4」的 DPR 缺陷。
const page = await browser.newPage({ deviceScaleFactor: 2 });
let groupId = null, token = null;

try {
  if (!fs.existsSync(PPTX)) throw new Error("缺少样例 pptx：" + PPTX);

  await page.goto(BASE_URL, { waitUntil: "domcontentloaded" });
  await page.waitForSelector("#authOverlay", { timeout: 15000 });
  if (!(await page.evaluate(() => document.getElementById("authOverlay").classList.contains("hidden")))) {
    await page.fill("#authUsername", USERNAME);
    await page.fill("#authPassword", PASSWORD);
    await page.click("#authSubmit");
    await page.waitForFunction(() => document.getElementById("authOverlay")?.classList.contains("hidden"), { timeout: 20000 });
  }
  token = (await (await page.request.post(BASE_URL + "/ag-ui/user/login",
    { data: { username: USERNAME, password: PASSWORD } })).json()).token;
  const H = { Authorization: "Bearer " + token };
  const api = async (method, p, data) => {
    const r = await page.request.fetch(BASE_URL + p, {
      method, headers: { ...H, "Content-Type": "application/json" },
      data: data === undefined ? undefined : JSON.stringify(data),
    });
    return { status: r.status(), body: await r.json().catch(() => null) };
  };
  const userId = (await api("GET", "/ag-ui/user/me")).body.userId;
  console.log("已登录 " + USERNAME + "\n");

  const created = await api("POST", "/ag-ui/group/create", { groupName, ownerId: userId, memberIds: [], members: [] });
  groupId = created.body?.groupId;
  check("建临时知聚", created.status === 200 && !!groupId, groupName);
  if (!groupId) throw new Error("临时知聚未创建");

  const up = await page.request.post(BASE_URL + "/ag-ui/upload", {
    headers: H,
    multipart: { file: { name: "发布会.pptx", mimeType: "application/vnd.openxmlformats-officedocument.presentationml.presentation", buffer: fs.readFileSync(PPTX) } },
  });
  const att = (await up.json()).attachments?.[0];
  check("上传 pptx", !!att?.attachmentId);
  const sent = await api("POST", "/ag-ui/group/message/send", { groupId, userId, content: "幻灯片验证：pptx", attachments: [att] });
  check("发送带 pptx 的消息", sent.status === 200);

  // 打开知聚
  await page.reload({ waitUntil: "domcontentloaded" });
  await page.waitForSelector(".group-item", { timeout: 20000 });
  await page.locator(".group-item", { hasText: groupName }).first().click();
  await page.waitForSelector(".msg .att-preview", { timeout: 20000 });
  await sleep(500);

  // 打开预览
  await page.locator(".msg .att-preview").first().click();
  await page.waitForSelector("#docPreviewModal:not(.hidden)", { timeout: 10000 });

  // 断言 1：走自绘查看器（slide view 可见、iframe 不可见）
  await page.waitForSelector("#docSlideView:not(.hidden)", { timeout: 60000 });
  check("演示文稿走自绘查看器（slide view 可见）", true);
  check("iframe 未用于演示文稿", await page.locator("#docPreviewFrame").isHidden());
  check("转换提示已隐藏", await page.locator("#docPreviewLoading").isHidden());
  check("错误提示未出现", await page.locator("#docPreviewError").isHidden());

  // 断言 2：首页渲染 + 页码 + 备注
  await page.waitForFunction((n) => document.getElementById("docSlidePage")?.textContent === `1 / ${n}`, EXPECT_PAGES, { timeout: 30000 });
  check("页码显示 1 / " + EXPECT_PAGES, true, await page.locator("#docSlidePage").textContent());
  const box = await page.locator("#docSlideCanvas").boundingBox();
  check("画布有实际尺寸（幻灯片已渲染）", !!box && box.width > 200 && box.height > 150,
    box ? `${Math.round(box.width)}×${Math.round(box.height)}` : "无");
  // 断言“内容真的铺满画布”：只画进左上角 1/4 时，非透明像素的包围盒只有 ~50%（DPR 缺陷现场）。
  const span = await page.evaluate(() => {
    const c = document.getElementById("docSlideCanvas");
    const ctx = c.getContext("2d");
    const W = c.width, H = c.height;
    const d = ctx.getImageData(0, 0, W, H).data;
    let minX = W, minY = H, maxX = -1, maxY = -1;
    for (let y = 0; y < H; y += 2) for (let x = 0; x < W; x += 2) {
      if (d[(y * W + x) * 4 + 3] > 0) { if (x < minX) minX = x; if (x > maxX) maxX = x; if (y < minY) minY = y; if (y > maxY) maxY = y; }
    }
    return maxX < 0 ? null : { w: (maxX - minX + 1) / W, h: (maxY - minY + 1) / H, dpr: window.devicePixelRatio };
  });
  check("幻灯片铺满画布（DPR=2 下不会只画 1/4）", !!span && span.w > 0.85 && span.h > 0.85,
    span ? `${Math.round(span.w * 100)}%×${Math.round(span.h * 100)}%（dpr=${span.dpr}）` : "无内容");
  const notes1 = await page.locator("#docSlideNotes").textContent();
  check("第 1 页备注显示在下方", !!notes1 && notes1.length > 5, (notes1 || "").slice(0, 30) + "…");

  // 断言 3：翻页 → 页码与备注同步
  await page.locator("#docSlideNext").click();
  await page.waitForFunction((n) => document.getElementById("docSlidePage")?.textContent === `2 / ${n}`, EXPECT_PAGES, { timeout: 15000 });
  const notes2 = await page.locator("#docSlideNotes").textContent();
  check("翻到第 2 页（页码更新）", true, await page.locator("#docSlidePage").textContent());
  check("备注随当前页切换", notes2 !== notes1, (notes2 || "").slice(0, 30) + "…");

  // 断言 4：播放模式（真·全屏 + 铺满）
  await page.locator("#docSlidePlay").click();
  await page.waitForSelector("#docPlayOverlay:not(.hidden)", { timeout: 10000 });
  check("进入全屏播放模式", true);
  await page.waitForFunction((n) => document.getElementById("docPlayPage")?.textContent === `2 / ${n}`, EXPECT_PAGES, { timeout: 15000 });
  check("播放起始页沿用当前页 2 / " + EXPECT_PAGES, true);
  const playBox = await page.locator("#docPlayCanvas").boundingBox();
  check("播放画布已渲染", !!playBox && playBox.width > 200, playBox ? `${Math.round(playBox.width)}×${Math.round(playBox.height)}` : "无");
  // 满屏：画布在限制方向上铺满整个视口（比例不符时另一方向留黑边属正常）
  const inner = await page.evaluate(() => ({ w: window.innerWidth, h: window.innerHeight, fs: document.fullscreenElement && document.fullscreenElement.id }));
  const fillW = playBox ? playBox.width / inner.w : 0, fillH = playBox ? playBox.height / inner.h : 0;
  check("播放满屏（限制方向铺满视口）", Math.max(fillW, fillH) >= 0.99 && Math.min(fillW, fillH) >= 0.7,
    `${Math.round(playBox.width)}×${Math.round(playBox.height)} / 视口 ${inner.w}×${inner.h}`);
  check("进入浏览器全屏（requestFullscreen）", inner.fs === "docPlayOverlay", String(inner.fs));
  await page.keyboard.press("ArrowRight");
  await page.waitForFunction((n) => document.getElementById("docPlayPage")?.textContent === `3 / ${n}`, EXPECT_PAGES, { timeout: 15000 });
  check("方向键翻页（播放中 → 3 / " + EXPECT_PAGES + "）", true);
  // 控制条静置后自动隐没（幻灯片真正铺满）
  await sleep(3300);
  check("控制条静置后自动隐没", await page.locator("#docPlayBar").evaluate((el) => el.classList.contains("doc-play-hidden")));

  // 断言 5：Esc 只退出播放，再 Esc 收弹窗
  await page.keyboard.press("Escape");
  await page.locator("#docPlayOverlay").waitFor({ state: "hidden", timeout: 5000 });
  check("Esc 退出播放", true);
  check("退出播放后弹窗仍在", await page.locator("#docPreviewModal").isVisible());
  await page.keyboard.press("Escape");
  await page.locator("#docPreviewModal").waitFor({ state: "hidden", timeout: 5000 });
  check("再 Esc 收起弹窗", true);

  console.log(`\n=== ${pass} 通过 / ${fail} 失败 ===`);
} catch (e) {
  fail++;
  console.error("\n界面验证异常：", e.message);
  try { await page.screenshot({ path: path.join(__dirname, "screenshots", "doc-slides-fail.png") }); } catch { /* 忽略 */ }
} finally {
  try {
    if (groupId && token) {
      const r = await page.request.fetch(BASE_URL + "/ag-ui/group/disband", {
        method: "POST",
        headers: { Authorization: "Bearer " + token, "Content-Type": "application/json" },
        data: JSON.stringify({ groupId, operatorId: null }),
      });
      console.log("临时知聚已解散：" + r.status());
    }
  } catch { /* 忽略 */ }
  await browser.close();
  process.exit(fail === 0 ? 0 : 1);
}
