// 方案 C 验证（Playwright，真实浏览器）：带转场/动画的 pptx 在线播放时，每张幻灯片都走
//   「底图 + 形状精灵」并播真动画（flyIn 等）；逐段构建（byParagraph）的形状同 spid 多条目，
//   前端按「行带」切成 k 段精灵，各自在自己的步显现。
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

  // 播放清单：3 张幻灯片全部走精灵模式（逐段构建也纳入）
  const manifest = (await api("GET", `/ag-ui/preview/${att.attachmentId}/play`)).body;
  check("清单含播放稿标记", !!manifest && manifest.expanded === true, JSON.stringify({ expanded: manifest?.expanded, pages: manifest?.pages?.length }));
  const spriteSlides = (manifest.animations || []).filter(Boolean).length;
  const fallbackSlides = (manifest.animations || []).filter((a) => !a).length;
  check("每张幻灯片都走精灵模式", spriteSlides === 3 && fallbackSlides === 0, `sprite=${spriteSlides} fallback=${fallbackSlides}`);
  const fly = (manifest.animations || []).flat().find((s) => s.effect === "entr");
  check("精灵含入场动画（flyIn/fade 等）", !!fly, fly ? `${fly.kind}/${fly.preset} step=${fly.revealStep} box=${fly.x.toFixed(2)},${fly.y.toFixed(2)}` : "无");
  const paras = ((manifest.animations || [])[1] || []).filter((s) => s.paraIndex > 0);
  check("逐段构建进精灵模式（正文 3 段带段序）", paras.length === 3 && paras.map((s) => s.paraIndex).join(",") === "1,2,3", "paraIndex=" + paras.map((s) => s.paraIndex).join(","));

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

  // 再下一步 → 进入要点页（也走精灵模式：标题 1 + 正文逐段 3 = 4 个精灵）
  await page.keyboard.press("ArrowRight");
  await sleep(700);
  const slide2 = await page.evaluate(() => ({
    text: document.getElementById("docPlayPage").textContent,
    sprites: [...document.querySelectorAll("#docSpriteLayer .doc-sprite")].map((el) => ({ top: parseFloat(el.style.top), display: getComputedStyle(el).display })),
  }));
  check("跨到要点页（4 个精灵：标题 + 逐段 3）", /2 \/ 3/.test(slide2.text) && slide2.sprites.length === 4, JSON.stringify({ text: slide2.text, n: slide2.sprites.length }));
  const tops = slide2.sprites.map((s) => s.top).sort((a, b) => a - b);
  check("逐段精灵按行带纵向分布（非重复）", new Set(tops).size === 4, "tops=" + tops.map((t) => t.toFixed(0)).join(","));
  check("要点页初始全部隐藏（步 0）", slide2.sprites.every((s) => s.display === "none"), JSON.stringify(slide2.sprites.map((s) => s.display)));
  check("页码显示 0/4 步", /0\/4/.test(slide2.text || ""), slide2.text);

  // 逐步显现：连按 4 次 → 标题 + 3 段逐段显现（每步只多显现一个）
  const counts = [];
  for (let i = 0; i < 4; i++) {
    await page.keyboard.press("ArrowRight");
    await sleep(250);
    counts.push(await page.evaluate(() => [...document.querySelectorAll("#docSpriteLayer .doc-sprite")].filter((el) => getComputedStyle(el).display !== "none").length));
  }
  check("逐段逐步显现（每步只多一段）", counts.join(",") === "1,2,3,4", "counts=" + counts.join(","));
  await sleep(900); // 等最后一段淡入完成
  const shown = await page.evaluate(() => [...document.querySelectorAll("#docSpriteLayer .doc-sprite")].filter((el) => getComputedStyle(el).display !== "none" && parseFloat(getComputedStyle(el).opacity) > 0.95).length);
  check("逐段全部可见（4 个）", shown === 4, "visible=" + shown);
  const label4 = await page.locator("#docPlayPage").textContent();
  check("页码显示 4/4 步", /4\/4/.test(label4 || ""), label4);
  try { fs.mkdirSync(path.join(__dirname, "screenshots"), { recursive: true }); await page.screenshot({ path: path.join(__dirname, "screenshots", "play-sprites-bands.png") }); } catch { /* 仅供人工核对 */ }

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
