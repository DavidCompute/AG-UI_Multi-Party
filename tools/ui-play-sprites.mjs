// 方案 C 验证（Playwright，真实浏览器）：带转场/动画的 pptx 在线播放时，
//   「整形状动画」页走「底图 + 形状精灵」并播真动画（flyIn 等）；「逐段构建」页回退到分步展开。
//
// 用法：cd tools && HEADLESS=1 node ui-play-sprites.mjs
//   样例：默认 <repo>/artifacts/pptx-play/deck.pptx（由 PresentationPlaybackTests 生成，3 页：封面 flyIn / 要点逐段 / 结束 fadeOut）
import { chromium } from "playwright";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const BASE_URL = process.env.BASE_URL || "http://localhost:5200";
const USERNAME = process.env.USERNAME || "david";
const PASSWORD = process.env.PASSWORD || "lingtong";
const HEADLESS = process.env.HEADLESS === "1";
const PPTX = process.env.PPTX || path.resolve(__dirname, "..", "artifacts", "pptx-play", "deck.pptx");

let pass = 0, fail = 0;
const check = (label, cond, extra = "") => {
  if (cond) { pass++; console.log("  PASS  " + label + (extra ? "  — " + extra : "")); }
  else { fail++; console.log("  FAIL  " + label + (extra ? "  — " + extra : "")); }
};
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const stamp = Date.now().toString(36).slice(-5);
const groupName = "播放精灵-" + stamp;

const browser = await chromium.launch({ headless: HEADLESS });
const page = await browser.newPage();
let groupId = null, token = null;

try {
  if (!fs.existsSync(PPTX)) throw new Error("缺少样例 pptx：" + PPTX);
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
  const userId = (await api("GET", "/ag-ui/user/me")).body.userId;

  groupId = (await api("POST", "/ag-ui/group/create", { groupName, ownerId: userId, memberIds: [], members: [] })).body.groupId;
  const up = await page.request.post(BASE_URL + "/ag-ui/upload", {
    headers: { Authorization: "Bearer " + token },
    multipart: { file: { name: "deck.pptx", mimeType: "application/vnd.openxmlformats-officedocument.presentationml.presentation", buffer: fs.readFileSync(PPTX) } },
  });
  const att = (await up.json()).attachments?.[0];
  if (!att) console.log("upload status=" + up.status() + " body=" + JSON.stringify(await up.text()));
  check("上传带动画的 pptx", !!att?.attachmentId);
  await api("POST", "/ag-ui/group/message/send", { groupId, userId, content: "播放精灵验证", attachments: [att] });

  // 播放清单：应有精灵模式的页（animations 非空）与回退页（null）
  const manifest = (await api("GET", `/ag-ui/preview/${att.attachmentId}/play`)).body;
  check("清单含展开稿标记", !!manifest && manifest.expanded === true, JSON.stringify({ expanded: manifest?.expanded, pages: manifest?.pages?.length }));
  const spriteSlides = (manifest.animations || []).filter(Boolean).length;
  const fallbackSlides = (manifest.animations || []).filter((a) => !a).length;
  check("既有精灵模式页也有回退页", spriteSlides >= 1 && fallbackSlides >= 1, `sprite=${spriteSlides} fallback=${fallbackSlides}`);
  const fly = (manifest.animations || []).flat().find((s) => s.effect === "entr");
  check("精灵含入场动画（flyIn/fade 等）", !!fly, fly ? `${fly.kind}/${fly.preset} step=${fly.revealStep} box=${fly.x.toFixed(2)},${fly.y.toFixed(2)}` : "无");

  // 打开预览 → 进入播放
  await page.reload({ waitUntil: "domcontentloaded" });
  await page.waitForSelector(".group-item", { timeout: 20000 });
  await page.locator(".group-item", { hasText: groupName }).first().click();
  await page.waitForSelector(".msg .att-preview", { timeout: 20000 });
  await sleep(400);
  await page.locator(".msg .att-preview").first().click();
  await page.waitForSelector("#docSlideView:not(.hidden)", { timeout: 60000 });
  await page.locator("#docSlidePlay").click();
  await page.waitForSelector("#docPlayOverlay:not(.hidden)", { timeout: 10000 });
  check("进入播放模式", true);

  // 等「展开版」升级完成：精灵层出现精灵
  await page.waitForSelector("#docSpriteLayer .doc-sprite", { state: "attached", timeout: 60000 });
  const sprites = await page.locator("#docSpriteLayer .doc-sprite").count();
  check("底图之上出现形状精灵", sprites >= 1, sprites + " 个");
  const box = await page.locator("#docSpriteLayer .doc-sprite").first().evaluate((el) => ({ w: parseFloat(el.style.width), h: parseFloat(el.style.height), left: parseFloat(el.style.left), top: parseFloat(el.style.top) }));
  check("精灵已按 bbox 定位且有尺寸", box.w > 4 && box.h > 4 && box.left >= 0 && box.top >= 0, JSON.stringify(box));

  // 入场精灵在步 0 时隐藏
  const before = await page.locator("#docSpriteLayer .doc-sprite").first().evaluate((el) => getComputedStyle(el).display);
  check("入场精灵初始隐藏（未到该步）", before === "none", before);
  const label0 = await page.locator("#docPlayPage").textContent();
  check("播放页码显示幻灯片与步", /1 \/ 3/.test(label0 || ""), label0);

  // 下一步 → 精灵显现并播动画
  await page.keyboard.press("ArrowRight");
  await sleep(250);
  const after = await page.locator("#docSpriteLayer .doc-sprite").first().evaluate((el) => ({ display: getComputedStyle(el).display, cls: el.className, op: getComputedStyle(el).opacity }));
  check("下一步后入场精灵显现", after.display !== "none", JSON.stringify(after));
  check("精灵播放了动画类", /sp-in-/.test(after.cls), after.cls);
  // 等动画播完：精灵完全可见（不透明度回到 1）
  await sleep(800);
  const settled = await page.locator("#docSpriteLayer .doc-sprite").first().evaluate((el) => ({ op: getComputedStyle(el).opacity, display: getComputedStyle(el).display }));
  check("动画结束后精灵完全可见", settled.display !== "none" && parseFloat(settled.op) > 0.95, JSON.stringify(settled));

  // 再下一步 → 进入下一张（要点页 = 回退分步模式，精灵层清空）
  await page.keyboard.press("ArrowRight");
  await sleep(600);
  const afterSlide2 = await page.evaluate(() => ({
    text: document.getElementById("docPlayPage").textContent,
    sprites: document.querySelectorAll("#docSpriteLayer .doc-sprite").length,
  }));
  check("跨到下一张（回退分步页无精灵）", /2 \/ 3/.test(afterSlide2.text) && afterSlide2.sprites === 0, JSON.stringify(afterSlide2));

  // Esc 退出播放
  await page.keyboard.press("Escape");
  await page.locator("#docPlayOverlay").waitFor({ state: "hidden", timeout: 5000 });
  check("Esc 退出播放", true);

  console.log(`\n=== ${pass} 通过 / ${fail} 失败 ===`);
} catch (e) {
  fail++;
  console.error("\n界面验证异常：", e.message);
} finally {
  try {
    if (groupId && token) await page.request.fetch(BASE_URL + "/ag-ui/group/disband", {
      method: "POST", headers: { Authorization: "Bearer " + token, "Content-Type": "application/json" },
      data: JSON.stringify({ groupId, operatorId: null }),
    });
  } catch { /* 忽略 */ }
  await browser.close();
  process.exit(fail === 0 ? 0 : 1);
}
