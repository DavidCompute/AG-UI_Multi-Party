// 数字员工「导出 / 导入」字段完整性验证（Playwright，真实浏览器）：
//   1. 服务端 upsert 接受 schedule / 审批名单 / 阶段开关 / 指派 / 交接 / 流水线；
//   2. 目录 DTO 暴露这些字段，serializeAgent（导出）完整携带；
//   3. buildAgentUpsertBody 第一轮剥离「引用型」字段、保留其余；
//   4. 真实导入路径（#agentImportFile → importAgentsFromFile 两轮写入）后，
//      引用型字段在同批数字员工之间可整体成立（含「被引用者后创建」的顺序）。
// 用法：cd tools && HEADLESS=1 node verify-agent-roundtrip.mjs
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

const browser = await chromium.launch({ headless: HEADLESS });
const page = await browser.newPage({ viewport: { width: 1280, height: 800 } });
let token = null;
const api = async (method, p, data) => {
  const r = await page.request.fetch(BASE_URL + p, {
    method,
    headers: { Authorization: "Bearer " + token, "Content-Type": "application/json" },
    data: data === undefined ? undefined : JSON.stringify(data),
  });
  return { status: r.status(), body: await r.json().catch(() => null) };
};
const aId = "agent_rt_a_" + stamp;
const bId = "agent_rt_b_" + stamp;
const cleanup = async () => { await api("DELETE", `/ag-ui/agents/${encodeURIComponent(bId)}`); await api("DELETE", `/ag-ui/agents/${encodeURIComponent(aId)}`); };

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

  // A：被引用目标；B：携带全部新增字段的角色
  let r = await api("POST", "/ag-ui/agents", { agentId: aId, nickname: "RT-目标-" + stamp, triggerMode: "mentioned" });
  check("创建目标数字员工 A", r.status === 200, String(r.status));
  r = await api("POST", "/ag-ui/agents", {
    agentId: bId, nickname: "RT-主-" + stamp, triggerMode: "mentioned",
    schedule: "*/5 * * * *",
    requireApprovalToolNames: ["publish_announcement"],
    disableRelay: true, disableOrgRoute: false,
    assignmentIds: [aId], relayToAgentId: aId,
    pipeline: [{ stepAgentId: aId, prompt: "专攻" }],
  });
  check("创建带完整字段的数字员工 B", r.status === 200, String(r.status) + " " + JSON.stringify(r.body));

  const list = (await api("GET", "/ag-ui/agents")).body || [];
  const aDto = list.find((x) => x.agentId === aId);
  const bDto = list.find((x) => x.agentId === bId);
  check("目录 DTO 含 schedule/pipeline/relayToAgentId/disableRelay",
    !!(bDto && bDto.schedule && bDto.pipeline && bDto.relayToAgentId && bDto.disableRelay === true));

  const exported = await page.evaluate((b) => (typeof serializeAgent === "function" ? serializeAgent(b) : null), bDto);
  check("serializeAgent 含 schedule", !!(exported && exported.schedule));
  check("serializeAgent 含 requireApprovalToolNames", !!(exported && Array.isArray(exported.requireApprovalToolNames) && exported.requireApprovalToolNames.length === 1));
  check("serializeAgent 含 pipeline", !!(exported && Array.isArray(exported.pipeline) && exported.pipeline.length === 1 && exported.pipeline[0].stepAgentId === aId));
  check("serializeAgent 含 relayToAgentId", !!(exported && exported.relayToAgentId === aId));
  check("serializeAgent 含 disableRelay", !!(exported && exported.disableRelay === true));

  const noRefs = await page.evaluate((b) => buildAgentUpsertBody(b, { includeRefs: false }), bDto);
  const withRefs = await page.evaluate((b) => buildAgentUpsertBody(b, { includeRefs: true }), bDto);
  check("第一轮剥离引用（pipeline/assignmentIds/relayToAgentId 均缺省）", !noRefs.pipeline && !noRefs.assignmentIds && !noRefs.relayToAgentId);
  check("第一轮保留 schedule/审批/阶段开关", !!noRefs.schedule && noRefs.requireApprovalToolNames.length === 1 && noRefs.disableRelay === true);
  check("第二轮带引用", !!withRefs.pipeline && withRefs.assignmentIds.length === 1 && withRefs.relayToAgentId === aId);

  // 删除后走真实导入（B 在数组中排在 A 之前，验证「被引用者后创建」也能成立）
  const aExported = await page.evaluate((d) => (typeof serializeAgent === "function" ? serializeAgent(d) : null), aDto);
  await cleanup();
  const removed = ((await api("GET", "/ag-ui/agents")).body || []).find((x) => x.agentId === bId);
  check("删除后 B 不在目录", !removed);

  const fileObj = { version: 1, agents: [exported, aExported] };
  await page.setInputFiles("#agentImportFile", { name: "rt.json", mimeType: "application/json", buffer: Buffer.from(JSON.stringify(fileObj)) });

  let b2 = null;
  for (let i = 0; i < 20 && !b2; i++) { await sleep(500); b2 = ((await api("GET", "/ag-ui/agents")).body || []).find((x) => x.agentId === bId) || null; }
  check("导入后 B 重新存在", !!b2);
  check("导入后 B 保留 schedule", !!(b2 && b2.schedule));
  check("导入后 B 保留 pipeline 引用", !!(b2 && b2.pipeline && b2.pipeline.length === 1 && b2.pipeline[0].stepAgentId === aId));
  check("导入后 B 保留 assignmentIds 引用", !!(b2 && b2.assignmentIds && b2.assignmentIds.includes(aId)));
  check("导入后 B 保留 relayToAgentId", !!(b2 && b2.relayToAgentId === aId));
  check("导入后 B 保留 disableRelay", !!(b2 && b2.disableRelay === true));

  await cleanup();
} catch (e) {
  fail++;
  console.log("  FAIL  异常：" + (e && e.message));
  await page.screenshot({ path: "screenshots/agent-roundtrip-fail.png" }).catch(() => {});
  await cleanup().catch(() => {});
} finally {
  await browser.close();
}
console.log(`\n数字员工导出/导入字段完整性验证：${pass} 通过 / ${fail} 失败`);
process.exit(fail ? 1 : 0);
