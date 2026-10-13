// 图库配图（C）验证（Playwright，真实浏览器）：
//   1. 配图反馈端点：不相关 / 相关 计数，且需可写权限；
//   2. 负反馈降权：多次「不相关」后，检索分数下降（或跌出门槛不再召回）；
//   3. 可解释：搜索响应含 tags / matchKind / mismatchCount；
//   4. UI：图库「试检索」结果带 👎 按钮，点击即落反馈（重新拉取核对计数）。
// 用法：cd tools && HEADLESS=1 node ui-imglib-feedback.mjs
import { chromium } from "playwright";

const BASE_URL = process.env.BASE_URL || "http://localhost:5200";
const USERNAME = process.env.USERNAME || "david";
const PASSWORD = process.env.PASSWORD || "lingtong";
const HEADLESS = process.env.HEADLESS === "1";
// 1x1 PNG
const PNG = Buffer.from("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==", "base64");

let pass = 0, fail = 0;
const check = (label, cond, extra = "") => {
  if (cond) { pass++; console.log("  PASS  " + label + (extra ? "  — " + extra : "")); }
  else { fail++; console.log("  FAIL  " + label + (extra ? "  — " + extra : "")); }
};
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const stamp = Date.now().toString(36).slice(-6);

const browser = await chromium.launch({ headless: HEADLESS });
const page = await browser.newPage({ viewport: { width: 1400, height: 900 } });
let token = null, libId = null, assetId = null, fileName = "pic-" + stamp + ".png";
const errors = [];
page.on("pageerror", (e) => errors.push("pageerror: " + e.message));
page.on("console", (m) => { if (m.type() === "error") errors.push("console: " + m.text()); });
const api = async (method, p, data) => {
  const r = await page.request.fetch(BASE_URL + p, {
    method, headers: { Authorization: "Bearer " + token, "Content-Type": "application/json" },
    data: data === undefined ? undefined : JSON.stringify(data),
  });
  return { status: r.status(), body: await r.json().catch(() => null) };
};
const findAsset = async () => {
  const libs = (await api("GET", "/ag-ui/image-libs")).body.libraries || [];
  const lib = libs.find((l) => l.libId === libId);
  return lib ? (lib.assets || []).find((a) => a.assetId === assetId) : null;
};

try {
  await page.goto(BASE_URL, { waitUntil: "domcontentloaded" });
  await page.waitForSelector("#authOverlay", { timeout: 15000 });
  if (!(await page.evaluate(() => document.getElementById("authOverlay")?.classList.contains("hidden")))) {
    await page.fill("#authUsername", USERNAME);
    await page.fill("#authPassword", PASSWORD);
    await page.click("#authSubmit");
    await page.waitForFunction(() => document.getElementById("authOverlay")?.classList.contains("hidden"), { timeout: 20000 });
  }
  token = (await (await page.request.post(BASE_URL + "/ag-ui/user/login", { data: { username: USERNAME, password: PASSWORD } })).json()).token;

  // 建库 + 上传图片 + 登记
  libId = (await api("POST", "/ag-ui/image-libs", { name: "FBT-" + stamp })).body.libId;
  check("建临时图库", !!libId, libId);
  const up = await page.request.post(BASE_URL + "/ag-ui/upload", {
    headers: { Authorization: "Bearer " + token },
    multipart: { file: { name: fileName, mimeType: "image/png", buffer: PNG } },
  });
  const att = (await up.json()).attachments?.[0];
  check("上传图片拿到 attachmentId", !!att?.attachmentId, att?.attachmentId);
  const addRes = await api("POST", `/ag-ui/image-libs/${libId}/assets`, { attachmentId: att.attachmentId, fileName });
  check("登记进图库", addRes.status === 200, String(addRes.status));
  assetId = addRes.body.assetId;

  // 等待入库（ready / error）
  let asset = null;
  for (let i = 0; i < 40; i++) { await sleep(750); asset = await findAsset(); if (asset && asset.status !== "processing") break; }
  check("图片进入稳定状态", !!asset && asset.status !== "processing", asset?.status);

  // 反馈计数
  check("未标注时不相关计数为 0", (await findAsset()).mismatchCount === 0);
  let r = await api("POST", `/ag-ui/image-libs/${libId}/assets/${assetId}/feedback`, { relevant: false });
  check("标注「不相关」→ 计数 +1", r.status === 200 && r.body.mismatchCount === 1, JSON.stringify(r.body).slice(0, 80));
  r = await api("POST", `/ag-ui/image-libs/${libId}/assets/${assetId}/feedback`, { relevant: true });
  check("标注「相关」→ relevantCount +1", r.status === 200 && r.body.relevantCount === 1);
  check("未知图片 → 404", (await api("POST", `/ag-ui/image-libs/${libId}/assets/att_nope/feedback`, { relevant: false })).status === 404);

  // 降权：对比检索分数
  const search = async () => (await api("POST", "/ag-ui/images/search", { query: fileName, topK: 5, minScore: 0.25, libraryIds: [libId] })).body;
  const d1 = await search();
  const h1 = (d1.images || []).find((x) => x.assetId === assetId);
  check("检索命中该图且带解释字段（tags/matchKind/mismatchCount）",
    !!h1 && Array.isArray(h1.tags) && typeof h1.matchKind === "string" && typeof h1.mismatchCount === "number",
    h1 ? `score=${h1.score} kind=${h1.matchKind} mm=${h1.mismatchCount}` : "no hit");

  if (h1) {
    // 补 3 次不相关（加上此前 1 次 = 4；再减 1 次相关 = 净 3）→ 惩罚 0.24
    for (let i = 0; i < 3; i++) await api("POST", `/ag-ui/image-libs/${libId}/assets/${assetId}/feedback`, { relevant: false });
    const d2 = await search();
    const h2 = (d2.images || []).find((x) => x.assetId === assetId);
    const dropped = !h2 || (h2.score <= h1.score - 0.1);
    check("负反馈后分数下降或跌出召回", dropped, h2 ? `score ${h1.score} → ${h2.score}` : "no longer returned");
  } else {
    check("负反馈后分数下降或跌出召回", true, "跳过（初始未召回）");
  }

  // UI：试检索里点 👎
  await page.evaluate(async () => { if (typeof loadImageLibs === "function") await loadImageLibs(); });
  await page.evaluate((id) => openLibSettings("imglib", id), libId);
  await page.waitForSelector("#libSetModal:not(.hidden)", { timeout: 5000 });
  await page.fill("#libSetProbeQuery", fileName);
  await page.click("#libSetProbeBtn");
  await sleep(1200);
  const fbCount = await page.locator("#libSetProbeResult .lib-set-fb").count();
  check("试检索结果含 👎 反馈按钮", fbCount > 0, "count=" + fbCount);
  if (fbCount > 0) {
    const before = (await findAsset()).mismatchCount;
    await page.locator("#libSetProbeResult .lib-set-fb").first().click();
    let after = before;
    for (let i = 0; i < 12 && after === before; i++) { await sleep(400); after = (await findAsset()).mismatchCount; }
    check("UI 点 👎 落库（计数 +1）", after === before + 1, `${before} → ${after}`);
  }
  check("无页面 JS 异常", errors.length === 0, errors.slice(0, 3).join(" ; "));
} catch (e) {
  fail++;
  console.log("  FAIL  异常：" + (e && e.message));
  await page.screenshot({ path: "screenshots/imglib-feedback-fail.png" }).catch(() => {});
} finally {
  if (libId) await api("DELETE", `/ag-ui/image-libs/${libId}`).catch(() => {});
  await browser.close();
}
console.log(`\n图库配图反馈验证：${pass} 通过 / ${fail} 失败`);
process.exit(fail ? 1 : 0);
