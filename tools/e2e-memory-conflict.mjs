// 记忆冲突链路端到端（API 级，确定性）
//
// 覆盖：写入 → 冲突检测 → 一致性体检列出 → 人工取代 → 检索/质量反映 → 撤销取代。
// 全程不调用模型：只发两条用户消息，靠语义记忆的写入侧检测 + 体检扫描完成。
//
// 用法：
//   node tools/e2e-memory-conflict.mjs
//   BASE_URL=http://127.0.0.1:5200 USERNAME=david PASSWORD=lingtong node tools/e2e-memory-conflict.mjs
//   KEEP=1 …  # 保留测试知聚（默认结束后解散并删记忆）
//
// 退出码：0 = 全部通过；1 = 有失败；2 = 环境 / 登录失败。

const BASE = (process.env.BASE_URL || "http://127.0.0.1:5200").replace(/\/+$/, "");
const USER = process.env.USERNAME || "david";
const PASS = process.env.PASSWORD || "lingtong";
const KEEP = process.env.KEEP === "1";

const stamp = new Date().toISOString().replace(/[-:.TZ]/g, "").slice(0, 14);
const failed = [];
let total = 0;

const ok = (msg) => console.log(`  [OK] ${msg}`);
const bad = (msg, extra) => {
  failed.push(msg);
  console.log(`  [FAIL] ${msg}${extra ? ` — ${extra}` : ""}`);
};
const check = (name, cond, extra) => { total++; return cond ? ok(name) : bad(name, extra); };
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

let token = "";

async function api(method, path, body) {
  const res = await fetch(BASE + path, {
    method,
    headers: Object.assign(
      { Accept: "application/json" },
      body ? { "Content-Type": "application/json" } : {},
      token ? { Authorization: "Bearer " + token } : {}),
    body: body ? JSON.stringify(body) : undefined,
  });
  const text = await res.text();
  let json = null;
  try { json = JSON.parse(text); } catch { /* 非 JSON 保持原文 */ }
  return { status: res.status, json, text };
}

/** 轮询直到 fn 返回真值；超时返回 null。 */
async function until(fn, ms = 30000, every = 1000) {
  const deadline = Date.now() + ms;
  while (Date.now() < deadline) {
    const v = await fn();
    if (v) return v;
    await sleep(every);
  }
  return null;
}

console.log(`# 记忆冲突链路 E2E → ${BASE}`);

// 1) 登录
const login = await api("POST", "/ag-ui/user/login", { username: USER, password: PASS });
if (!login.json || !login.json.token) {
  console.error("登录失败：", login.status, login.text.slice(0, 200));
  process.exit(2);
}
token = login.json.token;
const me = await api("GET", "/ag-ui/user/me");
const userId = me.json?.userId || me.json?.UserId;
if (!userId) {
  console.error("取不到用户 ID：", me.status, me.text.slice(0, 200));
  process.exit(2);
}
ok(`登录成功（${USER} / ${userId}）`);

// 2) 建一个私密测试知聚
const created = await api("POST", "/ag-ui/group/create", {
  groupName: `E2E 冲突-${stamp}`,
  ownerId: userId,
  isPrivate: true,
});
const groupId = created.json?.groupId || created.json?.GroupId || created.json?.group?.groupId;
check("创建测试知聚", !!groupId, `HTTP ${created.status} ${created.text.slice(0, 200)}`);
if (!groupId) process.exit(1);
const cleanup = async () => {
  if (KEEP) { console.log(`  [KEEP] 保留知聚 ${groupId}`); return; }
  await api("POST", "/ag-ui/memory/forget", { groupId, retentionHours: null });
  const r = await api("POST", "/ag-ui/group/disband", { groupId, operatorId: userId });
  ok(`清理：知聚已解散（HTTP ${r.status}）`);
};

try {
  // 3) 两条“同一件事、改了一个值”的用户消息（第 2 条带更正线索）
  const m1 = await api("POST", "/ag-ui/group/message/send", { groupId, content: "项目负责人是李四" });
  const m2 = await api("POST", "/ag-ui/group/message/send", { groupId, content: "项目负责人改成张三了" });
  check("发送两条消息", m1.status === 200 && m2.status === 200, `HTTP ${m1.status}/${m2.status}`);

  // 4) 等语义记忆写入 + 写入侧冲突检测（embedding 异步）
  const pair = await until(async () => {
    const r = await api("GET", `/ag-ui/memory/conflicts?groupId=${encodeURIComponent(groupId)}`);
    const pairs = (r.json && r.json.pairs) || [];
    return pairs.length ? pairs[0] : null;
  }, 45000, 1500);

  if (!pair) {
    // 写入侧没判出来（embedding 阈值 / 归一化差异）属于“可接受但需知情”：体检扫描也应该能给出
    bad("冲突对未出现（写入侧检测或体检均未命中）");
  } else {
    ok(`体检列出疑似冲突（older=${pair.older.messageId} newer=${pair.newer.messageId}，重合度 ${pair.score}）`);
    check("冲突对按旧→新给出", pair.older.timestamp < pair.newer.timestamp,
      `${pair.older.timestamp} / ${pair.newer.timestamp}`);

    // 5) 人工裁决：旧说法作废
    const resolved = await api("POST", "/ag-ui/memory/conflicts/resolve", {
      olderId: pair.older.messageId,
      newerId: pair.newer.messageId,
      action: "supersede",
    });
    check("取代请求成功", resolved.status === 200 && resolved.json?.ok === true,
      `HTTP ${resolved.status} ${resolved.text.slice(0, 160)}`);

    // 6) 列表带 supersededBy；质量概览 superseded ≥ 1；体检把该对标记为已处理
    const list = await api("GET", `/ag-ui/memory?groupId=${encodeURIComponent(groupId)}&limit=50`);
    const older = ((list.json && list.json.items) || []).find((i) => i.messageId === pair.older.messageId);
    check("列表显示「已被取代」", !!older && older.supersededBy === pair.newer.messageId,
      older ? JSON.stringify({ supersededBy: older.supersededBy }) : "未找到该条");

    const quality = await api("GET", `/ag-ui/memory/quality?groupId=${encodeURIComponent(groupId)}`);
    check("质量概览统计到 1 条已被取代", quality.json?.summary?.superseded >= 1,
      JSON.stringify(quality.json?.summary));

    const again = await api("GET", `/ag-ui/memory/conflicts?groupId=${encodeURIComponent(groupId)}`);
    const same = ((again.json && again.json.pairs) || []).find(
      (p) => p.older.messageId === pair.older.messageId && p.newer.messageId === pair.newer.messageId);
    check("体检把该对标为已处理", !!same && same.resolved === true,
      same ? JSON.stringify({ resolved: same.resolved }) : "未找到该对");

    // 7) 撤销取代 → 标记清除、质量回落
    const revert = await api("POST", `/ag-ui/memory/${encodeURIComponent(pair.older.messageId)}/unsupersede`);
    check("撤销取代成功", revert.status === 200 && revert.json?.ok === true,
      `HTTP ${revert.status} ${revert.text.slice(0, 160)}`);

    const after = await api("GET", `/ag-ui/memory?groupId=${encodeURIComponent(groupId)}&limit=50`);
    const older2 = ((after.json && after.json.items) || []).find((i) => i.messageId === pair.older.messageId);
    check("撤销后标记已清除", !!older2 && !older2.supersededBy,
      older2 ? JSON.stringify({ supersededBy: older2.supersededBy }) : "未找到该条");
  }
} finally {
  try { await cleanup(); } catch (e) { console.log(`  [WARN] 清理失败：${e.message}`); }
}

console.log("");
if (failed.length) {
  console.log(`结果：${failed.length} 项失败 / 共 ${total} 项断言`);
  failed.forEach((f) => console.log(`  - ${f}`));
  process.exit(1);
}
console.log("结果：全部通过");
process.exit(0);
