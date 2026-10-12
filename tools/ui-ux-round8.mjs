// 界面人性化第八轮验证（Playwright，真实浏览器）：
//   消息多选：进入选择模式、逐条点选、全选/全不选、批量复制、批量转发、批量删除（撤回）、Esc 退出。
//
// 用法：cd tools && HEADLESS=1 node ui-ux-round8.mjs
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
const gA = "R8A-" + stamp;
const gB = "R8B-" + stamp;
const token = "r8tok" + stamp;

const browser = await chromium.launch({ headless: HEADLESS });
// 授予剪贴板权限，便于校验「批量复制」
const context = await browser.newContext({ viewport: { width: 1366, height: 800 }, permissions: ["clipboard-read", "clipboard-write"] });
const page = await context.newPage();
const errors = [];
page.on("pageerror", (e) => errors.push(String(e.message)));
page.on("console", (m) => { if (m.type() === "error") errors.push("console: " + m.text()); });

let gidA = null, gidB = null, token2 = null, userId = null;

try {
  await page.goto(BASE_URL, { waitUntil: "domcontentloaded" });
  await page.waitForSelector("#authOverlay", { timeout: 15000 });
  if (!(await page.evaluate(() => document.getElementById("authOverlay").classList.contains("hidden")))) {
    await page.fill("#authUsername", USERNAME); await page.fill("#authPassword", PASSWORD); await page.click("#authSubmit");
    await page.waitForFunction(() => document.getElementById("authOverlay")?.classList.contains("hidden"), { timeout: 20000 });
  }
  token2 = (await (await page.request.post(BASE_URL + "/ag-ui/user/login", { data: { username: USERNAME, password: PASSWORD } })).json()).token;
  const H = { Authorization: "Bearer " + token2, "Content-Type": "application/json" };
  const api = async (method, p, data) => {
    const r = await page.request.fetch(BASE_URL + p, { method, headers: H, data: data === undefined ? undefined : JSON.stringify(data) });
    return { status: r.status(), body: await r.json().catch(() => null) };
  };
  userId = (await api("GET", "/ag-ui/user/me")).body.userId;

  gidA = (await api("POST", "/ag-ui/group/create", { groupName: gA, ownerId: userId, memberIds: [], members: [] })).body.groupId;
  gidB = (await api("POST", "/ag-ui/group/create", { groupName: gB, ownerId: userId, memberIds: [], members: [] })).body.groupId;
  if (!gidA || !gidB) throw new Error("临时知聚未创建");

  const contents = [`第一条 ${token} A`, `第二条 ${token} B`, "第三条 C", "第四条 D", "第五条 E"];
  for (const c of contents) { await api("POST", "/ag-ui/group/message/send", { groupId: gidA, userId, content: c }); await sleep(20); }
  const searchA = async () => (await api("GET", `/ag-ui/group/${gidA}/messages/search?q=${encodeURIComponent(token)}&count=50`)).body || [];
  const topicRes = await api("POST", "/ag-ui/group/topic/create", { groupId: gidA, name: "R8话题", operatorId: userId });
  check("准备：为知聚 A 建一个附加话题", topicRes.status === 200, String(topicRes.body?.topicId || topicRes.status));
  check("准备：知聚 A 有 5 条消息，其中 2 条含关键词", (await searchA()).length === 2);
  const searchB = async () => (await api("GET", `/ag-ui/group/${gidB}/messages/search?q=${encodeURIComponent(token)}&count=50`)).body || [];

  await page.reload({ waitUntil: "domcontentloaded" });
  await page.waitForSelector(".group-item", { timeout: 20000 });
  await page.locator(".group-item", { hasText: gA }).first().click();
  await page.waitForFunction(() => document.querySelectorAll("#messages .msg").length >= 5, { timeout: 20000 });
  await sleep(400);

  /* ============ 进入多选模式 ============ */
  check("进入知聚后「☑ 多选」按钮可用", !(await page.locator("#selectBtn").isDisabled()));
  await page.locator("#selectBtn").click();
  await page.waitForSelector("#messages .msg .select-check", { timeout: 8000 });
  await sleep(200);
  const entered = await page.evaluate(() => ({
    barVisible: !document.getElementById("selectBar").classList.contains("hidden"),
    btnOn: document.getElementById("selectBtn").classList.contains("on"),
    checkboxes: document.querySelectorAll("#messages .msg .select-check").length,
    selectable: document.querySelectorAll("#messages .msg.selectable").length,
    count: document.getElementById("selectCount").textContent.trim(),
  }));
  check("进入多选：工具条出现、按钮点亮", entered.barVisible && entered.btnOn);
  check("每条消息出现勾选框且可点选", entered.checkboxes >= 5 && entered.selectable >= 5, JSON.stringify({ cb: entered.checkboxes, sel: entered.selectable }));
  const disabled0 = await page.evaluate(() => ({
    copy: document.getElementById("selectCopyBtn").disabled,
    fwd: document.getElementById("selectForwardBtn").disabled,
    del: document.getElementById("selectDeleteBtn").disabled,
  }));
  check("未选中时复制 / 转发 / 删除均禁用", disabled0.copy && disabled0.fwd && disabled0.del, JSON.stringify(disabled0));

  /* ============ 逐条点选 ============ */
  await page.locator("#messages .msg").nth(0).click();
  await sleep(120);
  check("点第 1 条消息 → 选中 1 条", await page.evaluate(() => document.getElementById("selectCount").textContent.includes("1")));
  await page.locator("#messages .msg").nth(1).click();
  await sleep(120);
  const two = await page.evaluate(() => ({
    count: document.getElementById("selectCount").textContent.trim(),
    selected: document.querySelectorAll("#messages .msg.selected").length,
  }));
  check("点第 2 条 → 选中 2 条", two.count.includes("2") && two.selected === 2, JSON.stringify(two));

  /* ============ 批量转发 ============ */
  await page.locator("#selectForwardBtn").click();
  await page.waitForSelector("#forwardModal:not(.hidden)", { timeout: 8000 });
  check("打开「转发到知聚」弹窗", true);
  await page.locator("#forwardList .forward-item", { hasText: gB }).first().click();
  await sleep(100);
  await page.locator("#forwardConfirm").click();
  await page.locator("#forwardModal").waitFor({ state: "hidden", timeout: 8000 });
  await page.waitForFunction(() => document.getElementById("selectBar").classList.contains("hidden"), null, { timeout: 8000 });
  check("转发后弹窗关闭、退出多选模式", true);
  await sleep(300);
  const fwdHits = await searchB();
  check("转发的文本已到达目标知聚", fwdHits.length >= 2, fwdHits.length + " 条");

  /* ============ 全选 / 全不选 ============ */
  await page.locator("#selectBtn").click();
  await page.waitForSelector("#messages .msg .select-check", { timeout: 8000 });
  await sleep(150);
  await page.locator("#selectAllBtn").click();
  await sleep(150);
  const allOn = await page.evaluate(() => ({ count: document.getElementById("selectCount").textContent.trim(), sel: document.querySelectorAll("#messages .msg.selected").length }));
  check("「全选」选中当前话题全部消息", allOn.count.includes("5") && allOn.sel >= 5, JSON.stringify(allOn));
  await page.locator("#selectAllBtn").click();
  await sleep(150);
  check("再次「全选」（全部已选时）→ 清空选择", await page.evaluate(() => document.getElementById("selectCount").textContent.includes("0")));

  /* ============ 批量复制 ============ */
  await page.locator("#messages .msg").nth(0).click();
  await page.locator("#messages .msg").nth(1).click();
  await sleep(120);
  await page.locator("#selectCopyBtn").click();
  await sleep(300);
  const clip = await page.evaluate(() => navigator.clipboard.readText());
  check("「复制」把已选消息写入剪贴板", clip.includes(token), JSON.stringify(clip.slice(0, 60)));
  check("复制后退出多选模式", await page.evaluate(() => document.getElementById("selectBar").classList.contains("hidden")));

  /* ============ 批量删除（撤回） ============ */
  const before = (await searchA()).length;
  await page.locator("#selectBtn").click();
  await page.waitForSelector("#messages .msg .select-check", { timeout: 8000 });
  await sleep(150);
  await page.locator("#messages .msg").nth(0).click();
  await sleep(120);
  await page.locator("#selectDeleteBtn").click();
  await page.waitForSelector("#uiDialog:not(.hidden)", { timeout: 8000 });
  check("「删除」弹出确认框", true);
  await page.locator("#uiDialogOk").click();
  await sleep(500);
  const after = (await searchA()).length;
  check("删除即撤回：命中关键词的消息减少 1 条", after === before - 1, `${before} → ${after}`);
  check("删除后退出多选模式", await page.evaluate(() => document.getElementById("selectBar").classList.contains("hidden")));

  /* ============ Esc 退出 ============ */
  await page.locator("#selectBtn").click();
  await sleep(150);
  await page.keyboard.press("Escape");
  await sleep(200);
  check("Esc 退出多选模式", await page.evaluate(() => document.getElementById("selectBar").classList.contains("hidden")));

  /* ============ 切话题退出多选（避免计数与实际操作不一致） ============ */
  await page.locator("#selectBtn").click();
  await page.waitForSelector("#messages .msg .select-check", { timeout: 8000 });
  await sleep(150);
  await page.locator("#messages .msg").nth(0).click();
  await sleep(120);
  check("切话题前已选中 1 条", await page.evaluate(() => document.getElementById("selectCount").textContent.includes("1")));
  await page.locator("#topicBar .topic-chip", { hasText: "R8话题" }).first().click();
  await sleep(400);
  check("切换话题后退出多选（工具条收起、按钮灭）", await page.evaluate(() => document.getElementById("selectBar").classList.contains("hidden") && !document.getElementById("selectBtn").classList.contains("on")));

  check("无控制台报错", errors.length === 0, errors.slice(0, 3).join(" | "));
  console.log(`\n=== ${pass} 通过 / ${fail} 失败 ===`);
} catch (e) {
  fail++;
  console.error("\n界面验证异常：", e.message);
} finally {
  for (const gid of [gidA, gidB]) {
    try {
      if (gid && token2) await page.request.fetch(BASE_URL + "/ag-ui/group/disband", {
        method: "POST", headers: { Authorization: "Bearer " + token2, "Content-Type": "application/json" },
        data: JSON.stringify({ groupId: gid, operatorId: null }),
      });
    } catch { /* 忽略 */ }
  }
  await browser.close();
  process.exit(fail === 0 ? 0 : 1);
}
