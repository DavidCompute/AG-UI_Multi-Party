#!/usr/bin/env node
// AG-UI 团队级整批替换（管理员）：先删旧批数字员工，再把“构建师磨好的最终方案”经一键编排 apply 整批落成新实体。
//
// 用法（管理员 token / david 默认）：
//   node tools/team-replace.mjs \
//     --base http://localhost:5200 \
//     --user david --pass 123456 \
//     --old sales_agent,support_agent \
//     --agent-file ./plan.json        # 与 /ag-ui/agents/orchestrate/apply 同结构：{ title, agents[], skills[], createSupportCircle }
//
// 全程复用已验证能力：DELETE /ag-ui/agents/{id}（含系统内置/分身/技能目标保护）＋ POST /ag-ui/agents/orchestrate/apply（原子+去重+技能自测）。
// 不会自动触发的禁用场景：跨对象校验不过在删之前就整体返回错误，不落任何库。
const base = (arg("--base") ?? "http://localhost:5200").replace(/\/$/, "");
const user = arg("--user") ?? "david";
const pass = arg("--pass") ?? "123456";
const oldList = (arg("--old") ?? "").split(",").map(s => s.trim()).filter(Boolean);
const planFile = arg("--agent-file");

if (oldList.length === 0 && !planFile) {
  usage();
  process.exit(2);
}

async function api(method, path, token, body) {
  const headers = { "Content-Type": "application/json" };
  if (token) headers.Authorization = "Bearer " + token;
  const res = await fetch(base + path, { method, headers, body: body ? JSON.stringify(body) : undefined });
  let data = null; try { data = await res.json(); } catch {}
  return { status: res.status, ok: res.ok, data };
}

function arg(name) {
  const i = process.argv.indexOf(name);
  return i >= 0 && process.argv[i + 1] ? process.argv[i + 1] : null;
}
function usage() {
  console.log("用法: node tools/team-replace.mjs --base <url> --user <u> --pass <p> --old <ids,…> [--agent-file <plan.json>]");
}

// --- 1. 登录 ---
const login = await api("POST", "/ag-ui/user/login", null, { username: user, password: pass });
if (!login.ok) { console.error("登录失败:", login.data); process.exit(1); }
const token = login.data.token;

// --- 2. 校验并删除旧批（逐个：复用既有 DELETE 的保护） ---
const deleted = [];
for (const id of oldList) {
  const r = await api("DELETE", "/ag-ui/agents/" + encodeURIComponent(id), token);
  if (r.ok) { deleted.push(id); console.log("已删除旧数字员工:", id, r.data); }
  else { console.error(`删除「${id}」失败(${r.status}):`, r.data); }
}
if (oldList.length > 0 && deleted.length !== oldList.length) {
  console.error("存在删除失败，为保数据完整已中止后续 apply，请核对旧批清单后重试。");
  process.exit(3);
}

// --- 3. 用一键编排 apply 把最终稿整批落成新实体 ---
if (planFile) {
  const fs = await import("node:fs");
  const plan = JSON.parse(fs.readFileSync(planFile, "utf8"));
  const apply = await api("POST", "/ag-ui/agents/orchestrate/apply", token, plan);
  if (apply.ok) {
    console.log("apply 成功: applied=true",
      "agents=" + (apply.data?.agents ?? []).join(","),
      "skills=" + (apply.data?.skills ?? []).join(","),
      "supportCircleGroupId=" + (apply.data?.supportCircleGroupId ?? "-"));
    console.log("smoke:", JSON.stringify(apply.data?.smoke ?? []));
  } else {
    console.error("apply 失败(" + apply.status + "):", apply.data);
    process.exit(4);
  }
}
console.log("整批替换完成。");
