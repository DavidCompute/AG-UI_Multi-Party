#!/usr/bin/env node
// Playwright 冒烟：内置“托管团队”整支覆盖/删除 UI（对照受控 org-teams API）
// 先经 API 预置一支（避免依赖后端目录里已有团队），再在 UI 上：列出 -> 整支覆盖 v2 -> 删除，任一步失败 exit 1。
import { chromium } from "playwright";
import { mkdirSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const BASE = (process.env.BASE_URL || "http://localhost:5200").replace(/\/$/, "");
const USER = process.env.USERNAME || "david";
const PASS = process.env.PASSWORD || "123456";
const KEY = process.env.SMOKE_KEY || "ui_smoke_team";
const __dir = path.dirname(fileURLToPath(import.meta.url));
mkdirSync(path.join(__dir, "screenshots"), { recursive: true });

async function api(method, p, token, body) {
  const h = body !== undefined ? { "Content-Type": "application/json", ...(token ? { Authorization: "Bearer " + token } : {}) }
    : (token ? { Authorization: "Bearer " + token } : {});
  const r = await fetch(BASE + p, { method, headers: h, body: body !== undefined ? (typeof body === "string" ? body : JSON.stringify(body)) : undefined });
  let d = null; try { d = await r.json(); } catch {}
  return { ok: r.ok, status: r.status, data: d };
}
function plan(mid, midNick, v) {
  return {
    title: "UI冒烟 " + v,
    skills: [{ skillId: "smoketicket", name: "冒烟工单", description: "冒烟用技能", kind: "prompt", body: "请结合模板与请求综合作答。", executionLocation: "server", requiresApproval: false }],
    agents: [
      { agentId: "ui_front", nickname: "一线(ui" + v + ")", description: "一线", instructions: "接单初判", triggerMode: "mentioned", skillIds: ["smoketicket"], assignmentIds: [], escalationAgentId: mid, relayToAgentId: null },
      { agentId: mid, nickname: midNick, description: "二线", instructions: "二线处理", triggerMode: "mentioned", skillIds: [], assignmentIds: ["ui_front"], escalationAgentId: null, relayToAgentId: null },
    ],
    createSupportCircle: false,
  };
}

const login = await api("POST", "/ag-ui/user/login", null, { username: USER, password: PASS });
if (!login.ok) { console.error("登录失败", login.data); process.exit(1); }
const token = login.data.token;

// 预置一支(v1)
await api("POST", `/ag-ui/agents/org-teams/${KEY}/apply`, token, JSON.stringify(plan("ui_fq", "资深(v1)", 1)));

const browser = await chromium.launch({ headless: process.env.HEADLESS !== "0" });
const page = await browser.newPage();
let failed = null;
page.on("dialog", (dlg) => dlg.accept()); // 接受 UI 删除确认
try {
  await page.goto(BASE, { waitUntil: "networkidle" });
  // 登录（已有会话则跳过）
  if (await page.isVisible("#authSubmit:not(.hidden),#authSubmit").catch(() => false) && await page.isVisible("#authUsername").catch(() => false)) {
    await page.fill("#authUsername", USER);
    await page.fill("#authPassword", PASS);
    await page.click("#authSubmit");
    await page.waitForSelector("#agentManageBtn", { timeout: 15000 });
  } else {
    await page.waitForSelector("#agentManageBtn", { timeout: 15000 }).catch(() => { throw new Error("未见顶栏数字员工按钮（可能未登录）"); });
  }
  // 打开数字员工弹窗，再开“托管团队”
  await page.click("#agentManageBtn");
  await page.waitForSelector("#omgManageBtn", { timeout: 10000 });
  await page.click("#omgManageBtn");
  await page.waitForSelector("#omgOverlay[style*='flex']", { timeout: 10000 }).catch(async () => { await page.waitForTimeout(1500); });
  await page.waitForSelector("#omgRows", { timeout: 10000 });
  // 断言列表出现预置行并含 v1 角色 sr(-fq)
  await page.waitForFunction((key) => document.querySelector("#omgRows")?.innerText?.includes(key), KEY, { timeout: 10000 });
  const before = await page.locator("#omgRows").innerText();
  if (!before.includes("ui_fq") || before.includes("ui_esc")) { throw new Error("预置行列内容不符(v1未见于列表)：" + before); }

  // 整支覆盖 -> v2（把 v2 写到输入区点“覆盖”）
  await page.fill("#omgPlan", JSON.stringify(plan("ui_esc", "专家(v2)", 2)));
  await page.click(`#omgOverlay button[data-omg="apply"][data-key="${KEY}"]`);
  // 覆盖成功后列表被刷新：该行 agents 应含新角色
  await page.waitForFunction((key) => document.querySelector("#omgRows")?.innerText?.includes("ui_esc"), KEY, { timeout: 15000 });
  const afterTxt = await page.locator("#omgRows").innerText();
  if (afterTxt.includes("ui_fq") || !afterTxt.includes("ui_esc")) { throw new Error("整支覆盖后未剩最新版：\n" + afterTxt); }

  // 删除
  await page.click(`#omgOverlay button[data-omg="del"][data-key="${KEY}"]`);
  await page.waitForTimeout(1200);
  const still = await page.locator("#omgRows").innerText();
  if (still.includes(KEY)) { throw new Error("删除后行仍存在"); }
  await page.screenshot({ path: path.join(__dir, "screenshots", "smoke-managed-teams.png") });
  console.log("UI 冒烟通过：预置→覆盖(v2)→删除 全链路 OK");
} catch (e) {
  failed = e;
  try { await page.screenshot({ path: path.join(__dir, "screenshots", "smoke-managed-teams-fail.png") }); } catch {}
  console.error("UI 冒烟失败：", e && e.message ? e.message : e);
} finally {
  // 清理后端残留（防污染）
  await api("DELETE", `/ag-ui/agents/org-teams/${KEY}`, token);
  await browser.close();
}
process.exit(failed ? 1 : 0);
