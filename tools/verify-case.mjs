// AG-UI 群聊 —— 8.4 切入故事自动化（基于现有 Docker 环境 http://localhost:5200）
// 目标：创建数字员工 → 建知聚「技术讨论」→ @触发自动对话 → 发起「多位数字员工讨论」（循序接力）
// 依赖：Docker web 容器健康；Provider=deepseek 且已配置 API Key（真实 AI 回复）。
// 运行：node tools/verify-case.mjs
const base = "http://localhost:5200";
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

const username = "case_demo_user";
const AGENTS = [
  { agentId: "agent_case_prd", nickname: "产品助理", description: "产品需求分析助手",
    instructions: "你是「产品助理」，负责需求拆解与用户故事、产品方案。答复简洁，先给结论再展开。" },
  { agentId: "agent_case_code", nickname: "代码帮", description: "代码助手",
    instructions: "你是「代码帮」，负责代码评审与方案实现。给出代码前先讲思路，答复偏工程。" },
];

async function raw(url, opts = {}) {
  const res = await fetch(url, opts);
  const text = await res.text();
  let json = null; try { json = text ? JSON.parse(text) : null; } catch {}
  return { res, json, text };
}
async function must(url, opts = {}) {
  const { res, json, text } = await raw(url, opts);
  if (!res.ok) throw new Error(`请求失败 ${res.status} ${url} :: ${text}`);
  return json;
}
const auth = (token) => ({ "Content-Type": "application/json", Authorization: `Bearer ${token}` });
const post = (path, token, payload) => must(base + path, { method: "POST", headers: auth(token), body: JSON.stringify(payload) });
const get = (path, token) => must(base + path, { method: "GET", headers: auth(token) });

/// 拉取群快照中指定发送者的消息文本（返回 [{senderId, senderNickname, content} ...]，按出现顺序）
async function snapshotAgentMessages(token, gid, agentIds) {
  const snap = await get(`/ag-ui/group/${gid}`, token);
  const msgs = ((snap.latestMessages || [])).filter((m) => agentIds.includes(m.senderId) && m.content && m.content.trim());
  return msgs.map((m) => ({ senderId: m.senderId, senderNickname: m.senderNickname, content: m.content }));
}

/// 等待指定发送者都有非空、且内容已稳定（不再增长）的回复，打印最终文本，返回发送者集合。
async function waitAgentReplies(token, gid, agentIds, timeoutMs, label) {
  const deadline = Date.now() + timeoutMs;
  const done = new Set();
  const lastSeen = new Map();   // senderId -> { text, stableSince }
  let lastLog = Date.now();
  while (Date.now() < deadline) {
    const msgs = await snapshotAgentMessages(token, gid, agentIds);
    for (const m of msgs) {
      const st = lastSeen.get(m.senderId) || { text: "", stableSince: null };
      if (m.content !== st.text) { lastSeen.set(m.senderId, { text: m.content, stableSince: Date.now() }); }
      else if (!st.stableSince) { lastSeen.set(m.senderId, { text: m.content, stableSince: Date.now() }); }
    }
    // 打印：内容已稳定（连续约 3 秒未变化）且尚未打印过
    for (const [sid, st] of lastSeen) {
      if (st.text && st.stableSince && Date.now() - st.stableSince >= 3000 && !done.has(sid) && agentIds.includes(sid)) {
        done.add(sid);
        console.log(`\n── ${label} · ${sid} ──\n${st.text.trim().slice(0, 1800)}${st.text.trim().length > 1800 ? "\n…(已截断)" : ""}`);
      }
    }
    if (agentIds.every((a) => done.has(a))) return done;
    if (Date.now() - lastLog > 15000) { console.log(`  · 等待 ${label}…（已 ${Math.round((Date.now() - deadline + timeoutMs) / 1000)}s，收到 ${done.size}/${agentIds.length}）`); lastLog = Date.now(); }
    await sleep(1000);
  }
  // 超时：仍把当前内容打印出来，便于排查
  for (const [sid, st] of lastSeen) {
    if (st.text && !done.has(sid) && agentIds.includes(sid)) {
      done.add(sid);
      console.log(`\n── ${label} · ${sid}（超时快照）──\n${st.text.trim().slice(0, 1800)}`);
    }
  }
  return done;
}

async function main() {
  console.log("═══ 知聚 8.4 案例 · 自动对话验证（真实 DeepSeek 模型，Docker 环境） ═══\n");

  // 1. 登录（固定演示账号）；不存在则注册后登录。用固定账号可重跑，避免重复注册撞 IP 限流
  let user;
  const loginRes = await raw(`${base}/ag-ui/user/login`, {
    method: "POST", headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ username, password: "secret123" }),
  });
  if (loginRes.res.ok) {
    user = loginRes.json;
  } else {
    const reg = await raw(`${base}/ag-ui/user/register`, {
      method: "POST", headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ username, password: "secret123", nickname: "案例演示者" }),
    });
    if (!reg.res.ok) throw new Error(`注册与登录均失败: register=${reg.res.status} login=${loginRes.res.status}`);
    user = reg.json;
  }
  const token = user.token, userId = user.userId;
  console.log(`[OK] 注册/登录 ${username} -> ${userId}\n`);

  // 2. 创建两个数字员工（幂等）
  for (const a of AGENTS) {
    const existing = await get("/ag-ui/agents", token);
    if (existing.some((x) => x.agentId === a.agentId)) { console.log(`[OK] 数字员工已存在: ${a.agentId}（${a.nickname}）`); continue; }
    const r = await post("/ag-ui/agents", token, {
      agentId: a.agentId, nickname: a.nickname, description: a.description,
      instructions: a.instructions, triggerMode: "mentioned", keywords: [],
    });
    console.log(`[OK] 创建数字员工: ${r.agentId || a.agentId}（${a.nickname}）`);
  }

  // 2b. 「✨ 生成角色设定」演示
  try {
    const gi = await post("/ag-ui/agents/generate-instructions", token, { description: "产品需求分析助手" });
    console.log(`[OK] ✨ 生成角色设定已返回，instructions 长度=${(gi.instructions || "").length}`);
  } catch (e) { console.log(`[warn] 生成角色设定: ${e.message}`); }
  console.log("");

  // 3. 建知聚「技术讨论」
  const group = await post("/ag-ui/group/create", token, {
    groupName: "技术讨论", ownerId: userId,
    memberIds: [userId, "agent_case_prd", "agent_case_code"],
    members: AGENTS.map((a) => ({ memberId: a.agentId, memberType: "agent", nickname: a.nickname })),
  });
  const gid = group.groupId;
  console.log(`[OK] 建知聚: ${gid}（成员 ${group.memberCount ?? "?"}）`);

  // 4. 注册触发规则（提及触发）
  for (const a of AGENTS) {
    await post(`/ag-ui/agents/register?memberId=${encodeURIComponent(userId)}`, token,
      { agentId: a.agentId, nickname: a.nickname, groupId: gid, triggerMode: "mentioned" });
  }
  console.log("[OK] 触发规则已注册（提及触发）\n");

  // 5. @触发「产品助理」（HTTP 发送 + 快照轮询拿回复）
  console.log("════ 步骤①：成员 @产品助理 —— 发起第一段真实对话 ════");
  await post("/ag-ui/group/message/send", token, {
    groupId: gid, userId, content: "@产品助理 帮我把「知聚多角色协作」拆解成待交付的用户故事，简洁输出 3-5 条。",
    mentions: ["agent_case_prd"], timestamp: Date.now(),
  });
  await waitAgentReplies(token, gid, ["agent_case_prd"], 120000, "产品助理回复");

  // 6. 发起「多位数字员工讨论」（产品助理 + 代码帮 按序接力）
  console.log("\n════ 步骤②：发起「多位数字员工讨论」——产品助理 + 代码帮 按序接力 ════");
  const discuss = await post(`/ag-ui/group/${gid}/discussion`, token, {
    content: "我们团队要做「知聚多角色协作」，请两位各抒己见：产品上怎么定义最小可用版本？技术上权限模型怎么设计？",
    agentIds: ["agent_case_prd", "agent_case_code"],
  });
  console.log(`[OK] 讨论已发起，参与数字员工: ${(discuss.agents || []).join(", ")}`);
  await waitAgentReplies(token, gid, ["agent_case_prd", "agent_case_code"], 180000, "多数字员工讨论");

  // 7. 汇总
  const prd = await snapshotAgentMessages(token, gid, ["agent_case_prd"]);
  const code = await snapshotAgentMessages(token, gid, ["agent_case_code"]);
  const gotPrd = prd.some((m) => m.content.trim());
  const gotCode = code.some((m) => m.content.trim());
  console.log(`\n══════════════════════════════════════`);
  console.log(`[完成] 产品助理已回复: ${gotPrd ? "✅" : "❌"} ｜ 代码帮已回复: ${gotCode ? "✅" : "❌"}`);
  console.log(`完整对话可在浏览器 http://localhost:5200 以该账号登录「技术讨论」知聚查看（群 ${gid}）`);
  process.exit(gotPrd && gotCode ? 0 : 1);
}

main().catch((e) => { console.error(`[失败] ${e.message}`); process.exit(1); });
