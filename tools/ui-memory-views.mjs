// 记忆可视化（A3）验证（Playwright，真实浏览器）：
//   1. 记忆弹窗新增「列表 / 时间线 / 图谱」视图切换，默认列表；
//   2. 时间线可加载（有内容或空态，不报错）；
//   3. 图谱：未选知聚提示选知聚、无主题词提示输入；已启用/未启用分别给出对应状态；
//   4. buildMermaidGraph 子图 → Mermaid 源码正确（含 node/edge、去危险字符）；
//   5. 后端 /ag-ui/memory/graph 契约：缺 groupId → 400；带 groupId → 200 且含 enabled/needQuery。
// 用法：cd tools && HEADLESS=1 node ui-memory-views.mjs
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
const stamp = Date.now().toString(36).slice(-6);

const browser = await chromium.launch({ headless: HEADLESS });
const page = await browser.newPage({ viewport: { width: 1400, height: 900 } });
let token = null, userId = null, groupId = null;
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
  userId = (await api("GET", "/ag-ui/user/me")).body.userId;
  groupId = (await api("POST", "/ag-ui/group/create", { groupName: "MEMV-" + stamp, ownerId: userId, memberIds: [], members: [] })).body.groupId;
  check("建临时知聚", !!groupId, groupId);

  // ---- 后端契约 ----
  check("缺 groupId → 400", (await api("GET", "/ag-ui/memory/graph")).status === 400);
  const g1 = await api("GET", `/ag-ui/memory/graph?groupId=${encodeURIComponent(groupId)}`);
  check("带 groupId 无 q → 200 且含 enabled/needQuery", g1.status === 200 && typeof g1.body.enabled === "boolean" && typeof g1.body.needQuery === "boolean", JSON.stringify(g1.body).slice(0, 120));

  // ---- 打开记忆弹窗 ----
  await page.click("#meChip");
  await page.click("#meMenuMemory");
  await page.waitForSelector("#memoryModal:not(.hidden)", { timeout: 5000 });
  check("视图切换条可见", await page.locator(".memory-viewbar").isVisible());
  check("默认列表视图", await page.locator("#memList").isVisible() && await page.locator("#memTimeline").isHidden() && await page.locator("#memGraph").isHidden());

  // 时间线
  await page.click("#memViewTimeline");
  await page.waitForSelector("#memTimeline:not(.hidden)", { timeout: 5000 });
  check("时间线视图可见", await page.locator("#memTimeline").isVisible());
  check("时间线有内容或空态", (await page.locator("#memTimeline .mem-empty, #memTimeline .mem-tl-topic").count()) > 0);
  check("列表视图已隐藏", await page.locator("#memList").isHidden());

  // 图谱：未选知聚
  await page.click("#memViewGraph");
  await page.waitForSelector("#memGraph:not(.hidden)", { timeout: 5000 });
  await page.selectOption("#memGroupSelect", "");
  await page.click("#memGraphLoadBtn");
  await sleep(200);
  check("未选知聚 → 提示选知聚", (await page.locator("#memGraphCanvas").innerText()).includes("选择一个知聚"));

  // 注入临时知聚并选择（其可能未出现在记忆分组下拉里）
  await page.evaluate((gid) => {
    const s = document.getElementById("memGroupSelect");
    const o = document.createElement("option"); o.value = gid; o.textContent = "TMP";
    s.appendChild(o); s.value = gid;
  }, groupId);
  await page.click("#memGraphLoadBtn");
  await sleep(200);
  check("选中知聚但无主题词 → 提示输入", (await page.locator("#memGraphCanvas").innerText()).includes("主题词"));

  // 有主题词 → 图谱未启用时给出「未启用」状态
  await page.fill("#memGraphQuery", "项目");
  await page.click("#memGraphLoadBtn");
  await sleep(600);
  const graphText = await page.locator("#memGraphCanvas").innerText();
  check("图谱给出状态（未启用 / 未命中 / 图）", graphText.length > 0 && !errors.some((e) => e.includes("pageerror")), graphText.slice(0, 60));

  // ---- buildMermaidGraph 单元行为（合成数据） ----
  const mmd = await page.evaluate(() => buildMermaidGraph(
    [{ entityId: "e1", name: "DeepSeek" }, { entityId: "e2", name: '需求"评审]' }],
    [{ sourceId: "e1", targetId: "e2", relation: "负责|推荐" }]
  ));
  check("buildMermaidGraph 头为 graph LR", mmd.startsWith("graph LR"));
  check("含两个节点", (mmd.match(/\bn\d\[/g) || []).length === 2);
  check("含一条带标签的边", /n0 -->\|[^|]*\| n1/.test(mmd));
  const labels = [...mmd.matchAll(/\["([^"]*)"\]/g)].map((m) => m[1]);
  const edgeLabels = [...mmd.matchAll(/-->\|([^|]*)\|/g)].map((m) => m[1]);
  check("节点标签已清理危险字符", labels.length === 2 && labels.every((l) => !/["\[\]{}|<>]/.test(l)), labels.join(" / "));
  check("边标签已清理危险字符", edgeLabels.length === 1 && edgeLabels.every((l) => !/["\[\]{}|<>]/.test(l)), edgeLabels.join(" / "));

  check("无页面 JS 异常", errors.length === 0, errors.slice(0, 3).join(" ; "));
} catch (e) {
  fail++;
  console.log("  FAIL  异常：" + (e && e.message));
  await page.screenshot({ path: "screenshots/memory-views-fail.png" }).catch(() => {});
} finally {
  if (groupId) await api("POST", "/ag-ui/group/disband", { groupId, operatorId: userId }).catch(() => {});
  await browser.close();
}
console.log(`\n记忆可视化验证：${pass} 通过 / ${fail} 失败`);
process.exit(fail ? 1 : 0);
