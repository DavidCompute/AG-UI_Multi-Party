// 知识库「检索严格度」界面验证（Playwright）
//
// 验什么：知识库行上的 ⚙️ 能开设置弹窗、档位是知识库自己的三档（0.15/0.25/0.40）、
//         保存后接口回读、重新打开按已保存值回显、弹窗浮在管理弹窗之上。
//
// 数据安全：真实知识库只读；写操作全在临时知识库里做，结束删库（finally 兜底）。
//
// 用法：cd tools && HEADLESS=1 node ui-kb-strictness.mjs
import { chromium } from "playwright";

const BASE_URL = process.env.BASE_URL || "http://localhost:5200";
const USERNAME = process.env.USERNAME || "david";
const PASSWORD = process.env.PASSWORD || "lingtong";
const HEADLESS = process.env.HEADLESS === "1";

let ok = true;
const check = (label, cond, extra = "") => {
  ok = ok && !!cond;
  console.log((cond ? "  OK   " : "  FAIL ") + label + (cond || !extra ? "" : "  " + extra));
};
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

const browser = await chromium.launch({ headless: HEADLESS });
const page = await browser.newPage();
const stamp = Date.now().toString(36).slice(-5);
const probeKbName = "UI严格度验证-" + stamp;
let probeKbId = null;

try {
  await page.goto(BASE_URL, { waitUntil: "domcontentloaded" });
  await page.waitForSelector("#authOverlay", { timeout: 15000 });
  if (!await page.evaluate(() => document.getElementById("authOverlay").classList.contains("hidden"))) {
    await page.fill("#authUsername", USERNAME);
    await page.fill("#authPassword", PASSWORD);
    await page.click("#authSubmit");
    await page.waitForFunction(() => document.getElementById("authOverlay")?.classList.contains("hidden"), { timeout: 20000 });
  }
  const token = (await (await page.request.post(BASE_URL + "/ag-ui/user/login",
    { data: { username: USERNAME, password: PASSWORD } })).json()).token;
  const H = { Authorization: "Bearer " + token };
  const api = async (method, p, data) => {
    const r = await page.request.fetch(BASE_URL + p, {
      method, headers: { ...H, "Content-Type": "application/json" },
      data: data === undefined ? undefined : JSON.stringify(data),
    });
    return { status: r.status(), body: await r.json().catch(() => null) };
  };
  console.log("已登录 " + USERNAME);

  const created = await api("POST", "/ag-ui/kb", { name: probeKbName, description: "界面验证（用完即删）" });
  probeKbId = created.body?.kbId;
  check("建临时知识库", created.status === 200 && !!probeKbId, JSON.stringify(created).slice(0, 160));
  if (!probeKbId) throw new Error("临时库未创建");

  // 打开知识库管理（入口在「AI 角色管理」工具栏）
  await page.waitForSelector("#agentManageBtn", { state: "visible", timeout: 20000 });
  await page.click("#agentManageBtn");
  await page.click("#agentKbManageBtn");
  await page.waitForSelector("#kbModal:not(.hidden)", { timeout: 10000 });

  const item = page.locator("#kbListWrap .kb-list-item").filter({ hasText: probeKbName }).first();
  check("临时知识库出现在列表里", await item.count() > 0);
  const gear = item.locator(".kb-set").first();
  check("知识库行上有 ⚙️ 设置入口", await gear.count() > 0);

  if (await gear.count() > 0) {
    await gear.click();
    await page.waitForSelector("#libSetModal:not(.hidden)", { timeout: 5000 });
    check("⚙️ 打开共用设置弹窗", true);
    const sel = page.locator("#libSetStrictness");
    const values = await sel.locator("option").evaluateAll((os) => os.map((o) => o.value));
    console.log("   档位：" + JSON.stringify(values));
    check("档位是知识库自己的三档（0.15/0.25/0.40）",
      JSON.stringify(values) === JSON.stringify(["0.15", "0.25", "0.4"]), JSON.stringify(values));
    check("未设置时回显为「标准」（0.25）", await sel.inputValue() === "0.25", await sel.inputValue());
    const hint = (await page.locator("#libSetHint").textContent()) || "";
    check("弹窗里带了说明（提到实测分数范围）", hint.includes("0.31") || hint.includes("0.38"), hint.slice(0, 60));

    await page.selectOption("#libSetStrictness", "0.4");
    await page.click("#libSetOk");
    await page.waitForSelector("#libSetModal.hidden", { state: "hidden", timeout: 8000 });
    const after = await api("GET", "/ag-ui/kb");
    const mine = (after.body || []).find((k) => k.kbId === probeKbId);
    check("保存后接口回读为 0.40", Math.abs((mine?.minScore ?? 0) - 0.4) < 1e-9, String(mine?.minScore));

    await item.locator(".kb-set").first().click();
    await page.waitForSelector("#libSetModal:not(.hidden)", { timeout: 5000 });
    check("重新打开按已保存值回显 0.40", await page.locator("#libSetStrictness").inputValue() === "0.4",
      await page.locator("#libSetStrictness").inputValue());
    // Esc 关闭（且不应把下层管理弹窗一起关掉）
    await page.keyboard.press("Escape");
    await sleep(300);
    check("Esc 关闭设置弹窗", await page.locator("#libSetModal.hidden").count() > 0);
    check("下层管理弹窗仍开着（Esc 没穿透）", await page.locator("#kbModal.hidden").count() === 0);
  }
} finally {
  if (probeKbId) {
    try {
      const r = await page.request.post(BASE_URL + "/ag-ui/user/login", { data: { username: USERNAME, password: PASSWORD } });
      const t = (await r.json()).token;
      const del = await page.request.fetch(BASE_URL + "/ag-ui/kb/" + probeKbId, { method: "DELETE", headers: { Authorization: "Bearer " + t } });
      console.log((del.status() === 200 ? "清理：临时知识库已删除" : "清理：删除失败 " + del.status()));
    } catch { /* 清理失败不影响结论 */ }
  }
  await browser.close();
}
console.log("\n结果：" + (ok ? "全部通过" : "存在失败项"));
process.exit(ok ? 0 : 1);
