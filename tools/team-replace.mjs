#!/usr/bin/env node
// AG-UI 团队级整批替换 / 反复覆盖（管理员）：
// 先删该团队上一版数字员工（旧批），再把“构建师磨好的最终方案”经一键编排 apply 整批落库。
// 语义：同一 team（用同一 --name）可反复改、反复覆盖落库，库里始终只留“最新一版”。
//
// 用法（管理员登录）：
//   ① 首次建一支（并把新批 ID 记到本地 state，供下次覆盖）：
//      node tools/team-replace.mjs --base http://localhost:5200 --user david --pass 123456 \
//        --name my-team --agent-file ./v1.json
//   ② 再改一版用同一 --name：自动把上一版当旧批删掉，再 apply 新稿：
//      node tools/team-replace.mjs --base http://localhost:5200 --user david --pass 123456 \
//        --name my-team --agent-file ./v2.json
//   ③ 也可不记 state、显式指定旧批：--old a,b --agent-file ./plan.json
//
// 全程复用系统已验证能力：DELETE /ag-ui/agents/{id}（含内置/分身/技能目标护栏）＋
// POST /ag-ui/agents/orchestrate/apply（原子、自动去重重映射、技能自测）。先删后建，任一旧对象删除失败即整体中止，不部分写入。
import path from "node:path";
import fs from "node:fs";
import { fileURLToPath } from "node:url";

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const base = (arg("--base") ?? "http://localhost:5200").replace(/\/$/, "");
const user = arg("--user") ?? "david";
const pass = arg("--pass") ?? "123456";
const teamKey = (arg("--name") ?? "").trim();
const explicitOld = (arg("--old") ?? "").split(",").map(s => s.trim()).filter(Boolean);
const planFile = arg("--agent-file");

const stateDir = path.join(__dirname, ".team-state");
function stateFile() { return path.join(stateDir, (teamKey || "_unnamed").replace(/[^\w-]/g, "_") + ".json"); }
function readState() {
  try { return teamKey ? JSON.parse(fs.readFileSync(stateFile(), "utf8")) : null; } catch { return null; }
}
function writeState(o) {
  if (!teamKey || !o || !o.agents?.length) return;
  // 用集合累积该 team 曾建过的 id（agents + skills 分开记），使每次覆盖都把历史上建过的对象一并回收，
  // 避免技能因跨版本遗留而累积成 triage_2 / _3（对象早已删时循环回收遇 404 自然跳过）。
  const prev = readState() || {};
  const agents = Array.from(new Set([...(prev.agents || []), ...(o.agents || [])].filter(Boolean)));
  const skills = Array.from(new Set([...(prev.skills || []), ...(o.skills || [])].filter(Boolean)));
  fs.mkdirSync(stateDir, { recursive: true });
  fs.writeFileSync(stateFile(), JSON.stringify({
    name: teamKey,
    title: o.title ?? null,
    agents,
    skills,
    supportCircleGroupId: o.supportCircleGroupId ?? null,
    updatedAt: new Date().toISOString(),
  }, null, 2));
}

function arg(name) {
  const i = process.argv.indexOf(name);
  return i >= 0 && process.argv[i + 1] ? process.argv[i + 1] : null;
}
function usage() {
  console.log("用法: node tools/team-replace.mjs --base <url> --user <u> --pass <p> [--name <team> | --old <ids,…>] [--agent-file <plan.json>]");
}

async function api(method, p, token, body) {
  const headers = { "Content-Type": "application/json" };
  if (token) headers.Authorization = "Bearer " + token;
  const res = await fetch(base + p, { method, headers, body: body ? JSON.stringify(body) : undefined });
  let data = null; try { data = await res.json(); } catch {}
  return { status: res.status, ok: res.ok, data };
}

// --- 1. 登录（管理员） ---
const login = await api("POST", "/ag-ui/user/login", null, { username: user, password: pass });
if (!login.ok) { console.error("登录失败:", login.data); process.exit(1); }
const token = login.data.token;

// --- 2. 删除本团队上一版数字员工（按显式 --old 或 team state 中的旧批） ---
const prevState = readState();
const oldList = explicitOld.length ? explicitOld : (prevState?.agents ?? []);
if (oldList.length === 0 && !planFile) { usage(); process.exit(2); }
const deleted = [];
for (const id of oldList) {
  const r = await api("DELETE", "/ag-ui/agents/" + encodeURIComponent(id), token);
  if (r.ok) { deleted.push(id); console.log("已删除上一版数字员工:", id, r.data); }
  else console.error(`删除「${id}」失败(${r.status}):`, r.data);
}
if (oldList.length > 0 && deleted.length !== oldList.length) {
  console.error("存在删除失败，为保数据完整已中止后续 apply，请核对后重试。");
  process.exit(3);
}
// 3. 对同一 team 上一版自建的技能也退役（仅删该 team 上一轮记录的技能），
//    让 build 下一版时能用清爽的原始 skillId（而不累积 triage_2 之类重复产物）。
//    非 state 模式（显式 --old 且无 --name）不做技能回收。
if (teamKey && !explicitOld.length && prevState?.skills?.length) {
  for (const sid of prevState.skills) {
    const r = await api("DELETE", "/ag-ui/skills/" + encodeURIComponent(sid), token);
    if (r.ok) console.log("已退役上一版技能:", sid);
    else if (r.status === 404) console.log("上一版技能已不存在，跳过:", sid);
    else console.error(`技能「${sid}」回收失败(${r.status}):`, r.data);
  }
}

// --- 3. 用一键编排 apply 把最终稿整批落库，并记录新批 ID（供同 name 下次覆盖） ---
if (planFile) {
  const plan = JSON.parse(fs.readFileSync(planFile, "utf8"));
  const apply = await api("POST", "/ag-ui/agents/orchestrate/apply", token, plan);
  if (!apply.ok) {
    console.error("apply 失败(" + apply.status + "):", apply.data);
    process.exit(4);
  }
  console.log("apply 成功: applied=true",
    "agents=" + (apply.data?.agents ?? []).join(","),
    "skills=" + (apply.data?.skills ?? []).join(","),
    "supportCircleGroupId=" + (apply.data?.supportCircleGroupId ?? "-"));
  console.log("smoke:", JSON.stringify(apply.data?.smoke ?? []));
  writeState({
    name: teamKey,
    title: plan?.title ?? null,
    agents: apply.data?.agents ?? [],
    skills: apply.data?.skills ?? [],
    supportCircleGroupId: apply.data?.supportCircleGroupId ?? null,
    updatedAt: new Date().toISOString(),
  });
  if (teamKey) console.log(`已将这套记为 team 状态：${stateFile()}（下次用同一 --name 会自动清旧再落新版）`);
}
console.log("完成。库里当前只保留本套最新版本。");
