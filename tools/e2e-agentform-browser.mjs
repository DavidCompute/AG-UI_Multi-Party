// Browser E2E for digital-employee edit form UI fixes:
//  1) skill-library pick rows expose a "view/edit" button that opens that skill
//  2) employees linked in the org structure (assignment/escalation) show checked
//     in the "callable sub employees" list when opening the edit form.
// Runs against a fresh e2e instance (http://localhost:5201) that this script first seeds.
// Usage: node tools/e2e-agentform-browser.mjs
import { chromium } from "playwright";

const BASE = "http://localhost:5201";
let pass = 0, fail = 0;
function check(name, ok, extra) {
  if (ok) { pass++; console.log(`  PASS  ${name}${extra ? "  " + extra : ""}`); }
  else { fail++; console.log(`  FAIL  ${name}${extra ? "  " + extra : ""}`); }
}
const sleep = (ms) => new Promise((s) => setTimeout(s, ms));

async function api(method, path, token, json) {
  const headers = {};
  if (token) headers.Authorization = `Bearer ${token}`;
  if (json !== undefined) headers["Content-Type"] = "application/json";
  const res = await fetch(BASE + path, { method, headers, body: json !== undefined ? JSON.stringify(json) : undefined });
  const data = await res.json().catch(() => null);
  return { status: res.status, data };
}

console.log(`Agent-form UI E2E  base=${BASE}`);
// ---------- seed data ----------
let r = await api("POST", "/ag-ui/user/login", null, { username: "e2e_admin", password: "E2eAdmin#123" });
if (r.status !== 200) r = await api("POST", "/ag-ui/user/register", null, { username: "e2e_admin", password: "E2eAdmin#123", nickname: "E2E管理员" });
check("admin ready", r.status === 200 && !!r.data?.token, `status=${r.status}`);
const token = r.data.token;
// cleanup leftovers (idempotent re-run)
for (const id of ["boss_a", "worker_b", "worker_c"]) await api("DELETE", `/ag-ui/agents/${id}`, token, undefined).catch(() => {});
await api("DELETE", "/ag-ui/skills/stock_query", token, undefined).catch(() => {});

r = await api("POST", "/ag-ui/skills", token, { skillId: "stock_query", name: "库存查询", kind: "prompt", description: "查询库存数据", body: "请返回库存清单。" });
check("create skill stock_query", r.status === 200, `status=${r.status} ${JSON.stringify(r.data || {}).slice(0, 100)}`);
for (const [id, nickname, desc] of [["boss_a", "仓库主管", "负责调度与汇总"], ["worker_b", "入库员", "负责入库登记"], ["worker_c", "出库员", "负责出库登记"]]) {
  r = await api("POST", "/ag-ui/agents", token, { agentId: id, nickname, description: desc, triggerMode: "mentioned" });
  check(`create agent ${id}`, r.status === 200, `status=${r.status}`);
}
r = await api("PUT", "/ag-ui/agents/boss_a", token, {
  agentId: "boss_a", nickname: "仓库主管", description: "负责调度与汇总", triggerMode: "mentioned",
  assignmentIds: ["worker_b", "worker_c"], escalationAgentId: null, relayToAgentId: null,
  skillDefIds: ["stock_query"], skills: [],
});
check("set boss assignment + skill mount", r.status === 200, `status=${r.status}`);
r = await api("PUT", "/ag-ui/agents/worker_b", token, {
  agentId: "worker_b", nickname: "入库员", description: "负责入库登记", triggerMode: "mentioned",
  assignmentIds: [], escalationAgentId: "boss_a", relayToAgentId: null, skillDefIds: [], skills: [],
});
check("set worker_b escalation to boss", r.status === 200, `status=${r.status}`);

// ---------- browser assertions ----------
const browser = await chromium.launch();
try {
  const page = await (await browser.newContext()).newPage();
  await page.goto(BASE, { waitUntil: "domcontentloaded" });
  await page.fill("#authUsername", "e2e_admin");
  await page.fill("#authPassword", "E2eAdmin#123");
  await page.click("#authSubmit");
  await page.waitForSelector("#meChip:not(.hidden)", { timeout: 15000 });
  // mock 模型登录后会弹“模型配置”窗：先关闭
  for (let i = 0; i < 8 && await page.isVisible("#modelConfigModal"); i++) { await page.click("#mcCancel").catch(() => {}); await sleep(250); }
  await page.click("#agentManageBtn");
  await page.waitForSelector("#agentModal:not(.hidden)", { timeout: 8000 });

  const openAgent = async (agentId) => {
    const row = page.locator("#agentList > *").filter({ hasText: agentId }).first();
    check(`agent row ${agentId} exists`, (await row.count()) === 1);
    if ((await row.count()) === 0) return false;
    await row.locator('[data-act="edit"]').first().click();
    await page.waitForSelector("#agentFormView:not(.hidden)", { timeout: 6000 });
    return true;
  };

  // 展开“技能与知识”区（默认折叠）
  const ensureSkillSection = async () => {
    const sec = page.locator('[data-collapse-key="afSectSkill"]');
    if ((await sec.evaluate((el) => el.classList.contains("collapsed")).catch(() => false))) await sec.click();
    await page.waitForTimeout(150);
  };

  // --- scenario 1: boss shows mounted skill with view/edit button + org-linked callables checked ---
  await openAgent("boss_a");
  await ensureSkillSection();
  const defRows = await page.$$eval("#afSkillDefList .kb-pick-item", (els) =>
    els.map((el) => ({ text: el.textContent, checked: !!el.querySelector("input")?.checked, hasView: !!el.querySelector("button") })));
  check("skill row visible & checked", defRows.length === 1 && defRows[0].checked, JSON.stringify(defRows[0]?.text?.slice(0, 40)));
  check("skill row has view/edit button", defRows.length === 1 && defRows[0].hasView);
  const agentChecks = await page.$$eval("#afSkillAgentList .kb-pick-item", (els) =>
    els.map((el) => ({ text: el.textContent, checked: !!el.querySelector("input")?.checked })));
  const chk = (id) => agentChecks.find((c) => c.text.includes(id))?.checked;
  check("org assignment worker_b checked", chk("worker_b") === true);
  check("org assignment worker_c checked", chk("worker_c") === true);
  check("boss row itself excluded", chk("boss_a") === undefined || chk("boss_a") === false);
  await page.screenshot({ path: "artifacts/e2e-agentform-boss.png", fullPage: false });

  // --- open the skill editor from the row button, then back ---
  await page.click("#afSkillDefList .kb-pick-item button");
  await page.waitForSelector("#skillModal:not(.hidden)", { timeout: 6000 });
  const nameVal = await page.inputValue("#sfName").catch(() => "");
  check("skill editor opened with that skill", (await page.isVisible("#skillFormView")) && nameVal === "库存查询", `sfName=${nameVal}`);
  await page.click("#sfBack");
  await page.waitForFunction(() => document.getElementById("skillModal").classList.contains("hidden"), { timeout: 4000 });
  check("skill viewer returns directly to agent form", await page.isVisible("#agentFormView:not(.hidden)"));

  // --- scenario 2: leaf employee shows org escalation target checked ---
  await page.click("#afCancel");
  await page.waitForSelector("#agentListView:not(.hidden)", { timeout: 5000 });
  await openAgent("worker_b");
  await ensureSkillSection();
  const agentChecks2 = await page.$$eval("#afSkillAgentList .kb-pick-item", (els) =>
    els.map((el) => ({ text: el.textContent, checked: !!el.querySelector("input")?.checked })));
  const chk2 = (id) => agentChecks2.find((c) => c.text.includes(id))?.checked;
  check("org escalation boss_a checked for worker_b", chk2("boss_a") === true);
  check("unrelated worker_c not checked for worker_b", chk2("worker_c") === false);
  await page.screenshot({ path: "artifacts/e2e-agentform-worker.png", fullPage: false });
  await page.click("#afCancel");
} finally {
  await browser.close();
}

console.log(`\nRESULT: ${pass} passed, ${fail} failed`);
process.exit(fail ? 1 : 0);
