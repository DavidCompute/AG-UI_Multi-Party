// 「显示自动沉淀的知识库」开关（两处：知识库管理 + 数字员工表单的「绑定知识库」选取弹窗）
//
// 验什么：
//   1. 知识库管理：开关默认关 → 自动沉淀库默认不出现在列表；勾选后出现，取消后再隐；开关旁标出已隐藏数量。
//   2. 绑定知识库弹窗：同样默认隐藏、勾选可见；**只在「知识库」种类时出现**（切到技能就应收起）。
//
// 识别不放名字上：接口对名字带 `自动沉淀·` 前缀的库报 autoConsolidated=true（打标前老库的兜底口径），
// 所以这里用一个临时「自动沉淀·…」库来复现这一类库。
//
// 用法：cd tools && HEADLESS=1 node ui-kb-auto-toggle.mjs
import { chromium } from "playwright";
import path from "node:path";
import { fileURLToPath } from "node:url";

const __dirname = path.dirname(fileURLToPath(import.meta.url));

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
const plainName = "UI开关验证-" + stamp;
const autoName = "自动沉淀·UI开关验证-" + stamp;
let plainId = null, autoId = null;

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
  const H = { Authorization: "Bearer " + token, "Content-Type": "application/json" };
  const api = async (method, p, data) => {
    const r = await page.request.fetch(BASE_URL + p, {
      method, headers: H, data: data === undefined ? undefined : JSON.stringify(data),
    });
    return { status: r.status(), body: await r.json().catch(() => null) };
  };
  console.log("已登录 " + USERNAME);

  const created = await api("POST", "/ag-ui/kb", { name: plainName, description: "开关验证（用完即删）" });
  plainId = created.body?.kbId;
  const autoCreated = await api("POST", "/ag-ui/kb", { name: autoName, description: "开关验证（用完即删）" });
  autoId = autoCreated.body?.kbId;
  check("建临时普通库 + 自动沉淀库", created.status === 200 && !!plainId && autoCreated.status === 200 && !!autoId);
  if (!plainId || !autoId) throw new Error("临时库未创建");

  // 接口口径：名字带前缀的库应被标记为自动沉淀
  const listed = (await api("GET", "/ag-ui/kb")).body || [];
  const autoRow = listed.find((k) => k.kbId === autoId);
  const plainRow = listed.find((k) => k.kbId === plainId);
  check("接口把「自动沉淀·…」标为 autoConsolidated", !!autoRow?.autoConsolidated && !plainRow?.autoConsolidated,
    JSON.stringify({ auto: autoRow?.autoConsolidated, plain: plainRow?.autoConsolidated }));

  // ============ 1. 知识库管理 ============
  await page.evaluate(() => openKbModal());
  await page.waitForSelector("#kbModal:not(.hidden)", { timeout: 10000 });
  await page.waitForSelector("#kbListWrap .kb-list-item", { timeout: 10000 });
  const mgItem = (name) => page.locator("#kbListWrap .kb-list-item").filter({ hasText: name });
  check("管理列表：普通库可见", await mgItem(plainName).count() === 1);
  check("管理列表：开关默认关", !(await page.locator("#kbShowAuto").isChecked()));
  check("管理列表：自动沉淀库默认隐藏", await mgItem(autoName).count() === 0);
  check("管理列表：开关旁标出数量", /[0-9]/.test(((await page.locator("#kbShowAutoCount").textContent()) || "")));
  await page.locator("#kbShowAuto").check();
  await sleep(200);
  check("管理列表：勾选后自动沉淀库出现", await mgItem(autoName).count() === 1);
  await page.locator("#kbShowAuto").uncheck();
  await sleep(200);
  check("管理列表：取消后又隐", await mgItem(autoName).count() === 0);
  await page.evaluate(() => document.getElementById("kbModal").classList.add("hidden"));

  // ============ 2. 「绑定知识库」选取弹窗 ============
  const agentId = await page.evaluate(async () => {
    await openAgentModal();
    await loadAgents();
    return (agentList && agentList.length) ? agentList[0].agentId : null;
  });
  check("打开数字员工表单（有可编辑角色）", !!agentId, String(agentId));
  if (!agentId) throw new Error("无可编辑的数字员工");
  await page.evaluate((id) => {
    openAgentForm(id);
    // 「＋ 绑定知识库」在「技能与知识」分区（#afSectSkill）内，该分区默认收起 → 展开它
    document.getElementById("afSectSkill")?.classList.remove("hidden-section");
  }, agentId);
  await page.click("#afKbAddBtn");
  await page.waitForSelector("#agentPickModal:not(.hidden)", { timeout: 10000 });
  await page.waitForSelector("#agentPickList .kb-pick-item", { timeout: 10000 });

  const pkItem = (name) => page.locator("#agentPickList .kb-pick-item").filter({ hasText: name });
  check("选取弹窗：知识库种类显示开关", await page.locator("#agentPickShowAutoWrap.hidden").count() === 0);
  check("选取弹窗：开关默认关（与上一处同状态——管理里已取消勾选）", !(await page.locator("#agentPickShowAuto").isChecked()));
  check("选取弹窗：普通库可见", await pkItem(plainName).count() === 1);
  check("选取弹窗：自动沉淀库默认隐藏", await pkItem(autoName).count() === 0);
  await page.locator("#agentPickShowAuto").check();
  await sleep(200);
  check("选取弹窗：勾选后出现", await pkItem(autoName).count() === 1);
  await page.locator("#agentPickShowAuto").uncheck();
  await sleep(200);
  check("选取弹窗：取消后又隐", await pkItem(autoName).count() === 0);
  try {
    const b = await page.locator("#agentPickModal .modal").boundingBox();
    await page.screenshot({ path: path.join(__dirname, "screenshots", "kb-auto-toggle-picker.png"), clip: { x: b.x, y: b.y, width: b.width, height: Math.min(b.height, 420) } });
  } catch { /* 截图仅供人工核对 */ }

  // 状态是共享的：在选取弹窗里打开 → 回管理弹窗应仍是「开」
  await page.locator("#agentPickShowAuto").check();
  await sleep(150);
  await page.evaluate(() => document.getElementById("agentPickModal").classList.add("hidden"));
  await page.evaluate(() => openKbModal());
  await page.waitForSelector("#kbListWrap .kb-list-item", { timeout: 10000 });
  check("两处开关共享同一状态（选取弹窗打开 → 管理弹窗也是开）",
    await page.locator("#kbShowAuto").isChecked() && await mgItem(autoName).count() === 1);
  await page.locator("#kbShowAuto").uncheck();

  // 切到其它种类：开关应收起（只对「绑定知识库」有意义）
  await page.evaluate(() => openAgentPick("skill"));
  await page.waitForSelector("#agentPickModal:not(.hidden)", { timeout: 10000 });
  check("非知识库种类时开关收起", await page.locator("#agentPickShowAutoWrap.hidden").count() === 1);
  await page.evaluate(() => document.getElementById("agentPickModal").classList.add("hidden"));
} catch (e) {
  ok = false;
  console.error("  异常：" + e.message);
} finally {
  if (plainId || autoId) {
    try {
      const t = (await (await page.request.post(BASE_URL + "/ag-ui/user/login",
        { data: { username: USERNAME, password: PASSWORD } })).json()).token;
      for (const id of [plainId, autoId]) {
        if (!id) continue;
        const del = await page.request.fetch(BASE_URL + "/ag-ui/kb/" + id, { method: "DELETE", headers: { Authorization: "Bearer " + t } });
        console.log((del.status() === 200 ? "清理：临时库已删除 " : "清理：删除失败 " + del.status() + " ") + id);
      }
    } catch { /* 清理失败不影响结论 */ }
  }
  await browser.close();
}
console.log("\n结果：" + (ok ? "全部通过" : "存在失败项"));
process.exit(ok ? 0 : 1);
