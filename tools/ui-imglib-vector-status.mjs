// 图库「图片描述 → 向量化」状态提示：浏览器自动化验证（Playwright）
//
// 验什么（都是用户在界面上能看到的状态提示）：
//   1. 每张图都有状态徽标：⏳ 识别中… / ✅ 已向量化 / ⚠ 仅按文件名匹配 / ❌ 失败
//   2. 有未就绪图片时，图库行（收起态）提示「⚠ n 张未就绪」；全部就绪则不提示
//   3. 上传后的异步过程：先「识别中 / 未就绪」→ 最终「✅ 已向量化」
//   4. 保存描述：按钮显示「⏳ 向量化中…」，返回后徽标变「✅ 已向量化」（PUT 会等向量化完成）
//   5. 描述存空时提示「⚠ 仅按文件名匹配」（这种情况检索几乎找不到，必须说出来）
//
// 数据安全：**真实图库只读**（只用来看徽标）。所有写操作（建库 / 上传 / 改描述 / 删库）
// 都在一个临时图库里做，结束后把整个临时库删掉 —— 绝不触碰真实图片。
//
// 用法：
//   cd tools && HEADLESS=1 node ui-imglib-vector-status.mjs
// 环境变量：BASE_URL（默认 http://localhost:5200）、USERNAME、PASSWORD
import { chromium } from "playwright";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const shots = path.join(__dirname, "screenshots");
fs.mkdirSync(shots, { recursive: true });

const BASE_URL = process.env.BASE_URL || "http://localhost:5200";
const USERNAME = process.env.USERNAME || "david";
const PASSWORD = process.env.PASSWORD || "lingtong";
const HEADLESS = process.env.HEADLESS === "1";

const KNOWN_BADGES = ["✅ 已向量化", "⏳ 识别中…", "⚠ 仅按文件名匹配", "❌ 失败", "✅ Vectorized", "⏳ Analyzing…", "⚠ Filename only", "❌ Failed"];
const READY = /已向量化|Vectorized/;
const PENDING = /识别中|Analyzing/;
const FILENAME_ONLY = /仅按文件名|Filename only/;

let ok = true;
const check = (label, cond, extra = "") => {
  ok = ok && !!cond;
  console.log((cond ? "  OK   " : "  FAIL ") + label + (cond || !extra ? "" : "  " + extra));
};
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

const browser = await chromium.launch({ headless: HEADLESS });
const page = await browser.newPage();
const stamp = Date.now().toString(36).slice(-5);
const probeLibName = "UI状态验证-" + stamp;
let probeLibId = null;

try {
  // ---------- 登录（同时拿一个 API token，用于建/删临时库与直接登记图片） ----------
  await page.goto(BASE_URL, { waitUntil: "domcontentloaded" });
  await page.waitForSelector("#authOverlay", { timeout: 15000 });
  const needLogin = !await page.evaluate(() => document.getElementById("authOverlay").classList.contains("hidden"));
  if (needLogin) {
    await page.fill("#authUsername", USERNAME);
    await page.fill("#authPassword", PASSWORD);
    await page.click("#authSubmit");
    await page.waitForFunction(() => document.getElementById("authOverlay")?.classList.contains("hidden"), { timeout: 20000 });
  }
  const loginRes = await page.request.post(BASE_URL + "/ag-ui/user/login", { data: { username: USERNAME, password: PASSWORD } });
  const token = (await loginRes.json()).token;
  const H = { Authorization: "Bearer " + token };
  const apiJson = async (method, p, data) => {
    const r = await page.request.fetch(BASE_URL + p, {
      method, headers: { ...H, "Content-Type": "application/json" },
      data: data === undefined ? undefined : JSON.stringify(data),
    });
    return { status: r.status(), body: await r.json().catch(() => null) };
  };
  console.log("已登录 " + USERNAME);

  const waitListRendered = () => page.waitForFunction(() => {
    const w = document.getElementById("imgLibListWrap");
    // 列表区要么有库行，要么已渲染出空态文案（否则可能只是还没 load 完）
    return !!w && (!!w.querySelector(".kb-list-item") || w.textContent.trim().length > 0);
  }, { timeout: 20000 });
  const openLibModal = async () => {
    if (await page.locator("#imgLibModal.hidden").count()) {
      await page.waitForSelector("#agentManageBtn", { state: "visible", timeout: 20000 });
      await page.click("#agentManageBtn");
      await page.click("#agentImgLibManageBtn");
      await page.waitForSelector("#imgLibModal:not(.hidden)", { timeout: 10000 });
    } else {
      // 已打开：关掉再打开走同一条加载路径，保证拿到最新状态
      await page.click("#imgLibCloseBtn");
      await page.waitForSelector("#imgLibModal.hidden", { state: "hidden", timeout: 5000 });
      await page.click("#agentImgLibManageBtn");
      await page.waitForSelector("#imgLibModal:not(.hidden)", { timeout: 10000 });
    }
    await waitListRendered();
  };
  const itemOf = (name) => page.locator("#imgLibListWrap .kb-list-item").filter({ hasText: name }).first();
  const expand = async (item) => {
    if (await item.locator(".imglib-grid").count() === 0) {
      await item.locator(".kb-row").click();
      await sleep(250);
    }
  };
  const badgesOf = async (item) => (await item.locator(".imglib-meta .kb-status").allTextContents()).map((x) => x.trim());

  // ---------- 1) 真实图库（只读）：每张图都要有状态徐标 ----------
  await openLibModal();
  const libCount = await page.locator("#imgLibListWrap .kb-list-item").count();
  for (let i = 0; i < libCount; i++) {
    const item = page.locator("#imgLibListWrap .kb-list-item").nth(i);
    await expand(item);
  }
  if (libCount > 0) await sleep(300);   // 展开后等缩略图网格渲染
  const cards = await page.locator(".imglib-card").count();
  const badges = await page.locator(".imglib-card .imglib-meta .kb-status").allTextContents();
  console.log("真实图库 " + libCount + " 个，图片卡片 " + cards + " 张");
  check("看到至少一张图（真实图库有数据）", cards > 0);
  check("每张图都有状态徽标", badges.length === cards, `卡片 ${cards}，徽标 ${badges.length}`);
  const unknown = badges.map((b) => b.trim()).filter((b) => !KNOWN_BADGES.includes(b));
  check("徽标都是已知状态", unknown.length === 0, JSON.stringify(unknown));
  console.log("  徽标分布：" + JSON.stringify(badges.reduce((m, b) => (m[b.trim()] = (m[b.trim()] || 0) + 1, m), {})));
  check("至少有一张显示「✅ 已向量化」（完成要有提示）", badges.some((b) => READY.test(b)));
  // 全部就绪的库，行内不该报“未就绪”
  for (let i = 0; i < libCount; i++) {
    const item = page.locator("#imgLibListWrap .kb-list-item").nth(i);
    if (await item.locator(".imglib-grid").count() === 0) continue;
    const b = await badgesOf(item);
    if (!b.length || !b.every((x) => READY.test(x))) continue;
    await item.locator(".kb-row").click();          // 收起
    await sleep(250);
    const stat = ((await item.locator(".kb-stat").textContent()) || "").replace(/\s+/g, " ").trim();
    await item.locator(".kb-row").click();          // 再展开
    await sleep(250);
    check("全部就绪时行内不报“未就绪”", !/未就绪|not ready/.test(stat), stat);
    break;
  }
  await page.screenshot({ path: path.join(shots, "imglib-status-real.png") });

  // ---------- 2) 临时图库：走完整的“未完成 → 完成”过程 ----------
  const created = await apiJson("POST", "/ag-ui/image-libs", { name: probeLibName, description: "UI 状态提示自动化验证（用完即删）" });
  probeLibId = created.body?.libId;
  check("建临时图库成功", created.status === 200 && !!probeLibId, JSON.stringify(created).slice(0, 200));
  if (!probeLibId) throw new Error("临时图库未创建，后续步骤跳过");

  // 用一张**真实截图**当探针图（视觉模型能描述出内容；不用 4×4 纯色图，那描述不出东西）
  const probePath = path.join(shots, "vector-status-probe.png");
  await page.screenshot({ path: probePath, clip: { x: 0, y: 0, width: 720, height: 480 } });
  const up = await page.request.post(BASE_URL + "/ag-ui/upload", {
    headers: H,
    multipart: { file: { name: "probe.png", mimeType: "image/png", buffer: fs.readFileSync(probePath) } },
  });
  const atts = (await up.json()).attachments || [];
  const added = await apiJson("POST", `/ag-ui/image-libs/${probeLibId}/assets`, { attachmentId: atts[0]?.attachmentId, fileName: "probe.png" });
  check("探针图已登记（后台开始识别 + 向量化）", added.status === 200 && added.body?.status === "processing", JSON.stringify(added).slice(0, 200));

  await openLibModal();
  const probeItem = itemOf(probeLibName);
  await expand(probeItem);
  // 抓“未完成”提示：卡片徽标「⏳ 识别中…」或行内「⚠ 1 张未就绪」
  // （行与卡片都是常驻可见，不用展开收起，免得把状态切乱）
  let sawPending = false, pendingSeen = "";
  for (let i = 0; i < 200; i++) {
    const b = await badgesOf(probeItem);
    const stat = ((await probeItem.locator(".kb-stat").textContent()) || "").replace(/\s+/g, " ").trim();
    if (b.some((x) => PENDING.test(x))) { sawPending = true; pendingSeen = "卡片徽标 " + JSON.stringify(b); break; }
    if (/未就绪|not ready/.test(stat)) { sawPending = true; pendingSeen = "行提示 " + stat; break; }
    if (b.length) break;   // 已经有最终徽标了 → 没捕捉到中间态
    await sleep(120);
  }
  console.log("  中间态：" + (sawPending ? pendingSeen : "未捕捉到（识别过快）"));
  check("处理中有“未完成”提示（识别中 / 未就绪）", sawPending);

  // 等最终就绪，并确认徽标变「✅ 已向量化」
  let finalBadge = "";
  for (let i = 0; i < 600; i++) {
    const b = await badgesOf(probeItem);
    finalBadge = b[0] || "";
    if (finalBadge && !PENDING.test(finalBadge)) break;
    await sleep(200);
    if (i % 25 === 24) { await openLibModal(); await expand(itemOf(probeLibName)); }   // 轮询兜底刷新
  }
  console.log("  最终徽标：" + finalBadge);
  check("处理完成给出“完成”提示（✅ 已向量化）", READY.test(finalBadge), finalBadge);
  await page.screenshot({ path: path.join(shots, "imglib-status-probe.png") });

  // 行内提示：全就绪后不该再报未就绪
  await probeItem.locator(".kb-row").click();
  await sleep(250);
  const statReady = ((await probeItem.locator(".kb-stat").textContent()) || "").replace(/\s+/g, " ").trim();
  await probeItem.locator(".kb-row").click();
  await sleep(250);
  check("全部就绪后行内不再报“未就绪”", !/未就绪|not ready/.test(statReady), statReady);

  // ---------- 3) 保存描述：按钮「⏳ 向量化中…」→ 徐标「✅ 已向量化」 ----------
  // 注意：列表每 2s 会重渲染一次（状态轮询），所以**每次操作前重新取 locator 并校验写入结果**，
  // 否则可能填到已被替换掉的旧输入框上（表现为“保存了但值没变”，曾经误判为功能 bug）。
  const fillCaption = async (text) => {
    for (let i = 0; i < 3; i++) {
      const inp = probeItem.locator(".imglib-card").first().locator(".imglib-cap");
      await inp.fill(text);
      if ((await inp.inputValue()) === text) return true;
      await sleep(300);
    }
    return false;
  };
  const saveCaption = () => probeItem.locator(".imglib-card").first().locator(".imglib-save").first();
  const waitBadge = async (re, timeoutMs = 20000) => {
    const deadline = Date.now() + timeoutMs;
    while (Date.now() < deadline) {
      const b = (await badgesOf(probeItem))[0] || "";
      if (re.test(b)) return b;
      await sleep(200);
    }
    return (await badgesOf(probeItem))[0] || "";
  };
  // 填充 + 点击 + 等徐标，作为一个**整体**重试：
  // 上一次保存的响应回来时会把列表重渲染，可能让刚填的值被服务器旧值覆盖，
  // 于是这一个“保存”实际发的是旧值（徐标就不会变）——重试一次就好了。
  const saveAndWait = async (text, re, tries = 3) => {
    for (let i = 0; i < tries; i++) {
      await fillCaption(text);
      await saveCaption().click();
      const b = await waitBadge(re, 8000);
      if (re.test(b)) return b;
      await sleep(500);
    }
    return (await badgesOf(probeItem))[0] || "";
  };

  const probeCard = probeItem.locator(".imglib-card").first();
  if (!await probeCard.locator(".imglib-save").count()) {
    check("临时库可写（保存按钮可见）", false, "当前账号对临时库无写权限");
  } else {
    check("写入描述成功（填完回读一致）", await fillCaption("自动化验证：蓝色潜水艇在珊瑚礁间穿行"));
    const btn = saveCaption();
    const clicking = btn.click();
    let sawBusy = false;
    for (let i = 0; i < 100; i++) {
      if (/向量化中|Vectorizing/.test((await btn.textContent()) || "")) { sawBusy = true; break; }
      await sleep(80);
    }
    await clicking;
    check("保存时按钮提示「⏳ 向量化中…」（未完成有提示）", sawBusy);
    const afterBadge = await waitBadge(READY);
    check("保存返回后徐标为「✅ 已向量化」（完成有提示）", READY.test(afterBadge), afterBadge);

    // ---------- 4) 描述存空 → 「⚠ 仅按文件名匹配」 ----------
    const emptyBadge = await saveAndWait("", FILENAME_ONLY);
    check("描述存空时提示「⚠ 仅按文件名匹配」", FILENAME_ONLY.test(emptyBadge), emptyBadge);
  }

  // ---------- 6) 图库设置（⚙️ 检索严格度）----------
  // 在临时库上操作（不碰真实数据）：打开 → 选严格 → 保存 → API 回读确认。
  await openLibModal();
  const setItem = itemOf(probeLibName);
  await setItem.locator(".kb-row").click();   // 展开（⚙️ 在行操作区，展开不影响）
  await sleep(200);
  const gear = setItem.locator(".imglib-set").first();
  check("图库行上有 ⚙️ 设置入口", await gear.count() > 0);
  if (await gear.count() > 0) {
    await gear.click();
    await page.waitForSelector("#libSetModal:not(.hidden)", { timeout: 5000 });
    check("⚙️ 打开设置弹窗（浮在管理弹窗之上）", true);
    check("弹窗标题带图库名", ((await page.locator("#libSetTitle").textContent()) || "").includes(probeLibName));
    check("未设置时回显为「标准（0.6）」（技能默认值）",
          await page.locator("#libSetStrictness").inputValue() === "0.6",
          await page.locator("#libSetStrictness").inputValue());
    await page.selectOption("#libSetStrictness", "0.72");
    await page.click("#libSetOk");
    await page.waitForSelector("#libSetModal.hidden", { state: "hidden", timeout: 8000 });
    const saved = await apiJson("GET", "/ag-ui/image-libs");
    const mine = (saved.body?.libraries || []).find((l) => l.libId === probeLibId);
    check("保存后接口回读为 0.72", Math.abs((mine?.minScore ?? 0) - 0.72) < 1e-9, String(mine?.minScore));
    // 再打开：应按已保存值回显（否则一保存就被默默改回 0.6）
    await setItem.locator(".imglib-set").first().click();
    await page.waitForSelector("#libSetModal:not(.hidden)", { timeout: 5000 });
    check("重新打开时回显已保存的 0.72",
          await page.locator("#libSetStrictness").inputValue() === "0.72",
          await page.locator("#libSetStrictness").inputValue());
    await page.click("#libSetCancel");
    await page.waitForSelector("#libSetModal.hidden", { state: "hidden", timeout: 5000 });
  }

  // ---------- 5) 清理：删掉临时图库（不碰真实数据）----------
  const del = await apiJson("DELETE", `/ag-ui/image-libs/${probeLibId}`);
  check("临时图库已清理", del.status === 200, JSON.stringify(del).slice(0, 120));
  const after = await apiJson("GET", "/ag-ui/image-libs");
  const stillThere = (after.body?.libraries || []).some((l) => l.libId === probeLibId);
  check("临时图库确实不存在了", !stillThere);
  probeLibId = null;

  console.log("\n结果：" + (ok ? "全部通过" : "存在失败项"));
} finally {
  // 兜底清理：万一中途抛错，也别把临时图库留在真实数据里
  if (probeLibId) {
    try {
      const r = await page.request.post(BASE_URL + "/ag-ui/user/login", { data: { username: USERNAME, password: PASSWORD } });
      const t = (await r.json()).token;
      await page.request.fetch(BASE_URL + "/ag-ui/image-libs/" + probeLibId, { method: "DELETE", headers: { Authorization: "Bearer " + t } });
      console.log("（兜底）已删除临时图库 " + probeLibId);
    } catch { /* 清理失败不影响结论 */ }
  }
  await browser.close();
}
process.exit(ok ? 0 : 1);
