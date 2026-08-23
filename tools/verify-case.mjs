// AG-UI 群聊 —— 8.4 切入故事「多轮开发团队推进会」自动化（基于现有 Docker 环境 http://localhost:5200）
//
// 模拟一个开发团队（产品 / 后端 / 前端 / 测试 等成员轮流发言）与两个数字员工
//「产品助理」「代码帮」围绕一个需求（做「知聚」多角色协作平台的 MVP）进行 4 轮推进会：
//   Round1 需求澄清  产品 @产品助理 拆需求 → 产品助理给 MVP 用户故事
//   Round2 技术评审  后端 @代码帮 评方案 → 代码帮给工程方案/约束
//   Round3 多方对齐  「多位数字员工讨论」→ 产品助理 + 代码帮 互相回应、求同存异
//   Round4 收敛追问  团队抛验收标准/风险，@两位 收敛成结论 & 遗留项
//
// 关键机制：所有成员与数字员工的发言都会进入该知聚的群历史；每轮触发数字员工时，
// 其回复会读取最近群历史，因此在后一轮能看到前几轮的内容，形成“有来有回、逐步收敛”。
//
// 依赖：Docker web 容器健康；Provider=deepseek 且已配置 API Key（真实 AI 回复）。
// 运行：node tools/verify-case.mjs        （幂等可重跑：登录固定账号，数字员工已存在则跳过）
const base = "http://localhost:5200";
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const username = "case_demo_user";
const GROUP_NAME = "MVP 推进会-技术讨论";

const AGENTS = [
  { agentId: "agent_case_prd", nickname: "产品助理", description: "产品需求分析助手",
    instructions: "你是「产品助理」，负责需求澄清、用户故事与产品方案。先给结论再展开，回复可用结构列表，结尾可提一个反问推进讨论。" },
  { agentId: "agent_case_code", nickname: "代码帮", description: "代码/架构评审助手",
    instructions: "你是「代码帮」，负责架构评审与工程实现。给代码或方案前先讲思路，关注约束/风险/验收，可回应前序发言。" },
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

const _seenMsgIds = new Set(); // 全局：已打印过的消息，保证每轮只打印新增的数字员工回复

async function snapshotAgentMessages(token, gid, agentIds) {
  const snap = await get(`/ag-ui/group/${gid}`, token);
  return ((snap.latestMessages || [])).filter((m) => agentIds.includes(m.senderId) && m.content && m.content.trim());
}

const nickOf = (sid) => (AGENTS.find((x) => x.agentId === sid)?.nickname || sid);

/// 等待指定发送者在本轮产生'新增且稳定'的回复；有则打印新内容。返回本轮收到的新消息（senderId -> {text}）
async function waitReplies(token, gid, agentIds, timeoutMs, label) {
  const deadline = Date.now() + timeoutMs;
  const stable = new Map(); // messageId -> { text, stableSince }
  const gotThisRound = new Map(); // senderId -> { text, messageId }
  let lastLog = Date.now();
  while (Date.now() < deadline) {
    for (const m of await snapshotAgentMessages(token, gid, agentIds)) {
      const isNew = !_seenMsgIds.has(m.messageId);
      if (!isNew) continue; // 己打印过的旧消息跳过
      const st = stable.get(m.messageId) || { text: "", stableSince: null };
      if (m.content !== st.text) stable.set(m.messageId, { text: m.content, stableSince: Date.now() });
      else if (!st.stableSince) stable.set(m.messageId, { text: m.content, stableSince: Date.now() });
    }
    // 内容稳定（连续约 3s 未变化）且本轮尚未打印 → 打印并标记
    const toPrint = [...stable.entries()]
      .filter(([, st]) => st.text && st.stableSince && (Date.now() - st.stableSince) >= 3000)
      .filter(([mid]) => !_seenMsgIds.has(mid));
    for (const [mid, st] of toPrint) {
      _seenMsgIds.add(mid);
      const senderId = (await snapshotAgentMessages(token, gid, agentIds)).find((m) => m.messageId === mid)?.senderId;
      gotThisRound.set(senderId, { text: st.text, messageId: mid });
      console.log(`\n── 🤖 ${nickOf(senderId)} 回复 ──\n${st.text.trim().replace(/^/gm, "  ")}`);
    }
    // 本轮目标数字员工是否都己回复新消息
    const okIds = new Set([...gotThisRound.keys()].filter((sid) => agentIds.includes(sid)));
    if (agentIds.every((a) => okIds.has(a))) return gotThisRound;
    if (Date.now() - lastLog > 12000) { console.log(`  · 等待 ${label}…（${okIds.size}/${agentIds.length} 已到位）`); lastLog = Date.now(); }
    await sleep(900);
  }
  return gotThisRound;
}

/// 发一条“团队成员”发言并等待指定数字员工回复（带人类角色标签）
async function roundHumanAsk(token, gid, speaker, text, agents, label, timeoutMs) {
  console.log(`\n👤 ${speaker}：「${text}」`);
  await post("/ag-ui/group/message/send", token, {
    groupId: gid, userId: username, content: text.replace(/\n/g, " "),
    mentions: agents, timestamp: Date.now(),
  });
  const got = await waitReplies(token, gid, agents, timeoutMs, label);
  if (!agents.every((a) => got.has(a))) throw new Error(`超时：${label} 未收到全部数字员工本轮的回复`);
}

async function main() {
  console.log("═══ 知聚 8.4 案例 · 多轮开发团队推进会（真实 DeepSeek 模型） ═══\n");

  // 0. 登录固定演示账号（不存在则注册）
  let user;
  const loginRes = await raw(`${base}/ag-ui/user/login`, {
    method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ username, password: "secret123" }),
  });
  if (loginRes.res.ok) user = loginRes.json;
  else {
    const reg = await raw(`${base}/ag-ui/user/register`, {
      method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ username, password: "secret123", nickname: "演示者" }),
    });
    if (!reg.res.ok) throw new Error(`登录/注册失败 login=${loginRes.res.status} register=${reg.res.status}`);
    user = reg.json;
  }
  const token = user.token, userId = user.userId;
  console.log(`[OK] 演示者已登录: ${userId}\n`);

  // 1. 确保两个数字员工存在
  for (const a of AGENTS) {
    const existing = await get("/ag-ui/agents", token);
    if (existing.some((x) => x.agentId === a.agentId)) continue;
    await post("/ag-ui/agents", token, { agentId: a.agentId, nickname: a.nickname, description: a.description, instructions: a.instructions, triggerMode: "mentioned", keywords: [] });
  }
  console.log("[OK] 数字员工作备就绪：产品助理 / 代码帮\n");

  // 2. 建知聚（每次运行新建一个，保证群历史是最新一轮）
  const group = await post("/ag-ui/group/create", token, {
    groupName: GROUP_NAME, ownerId: userId,
    memberIds: [userId, "agent_case_prd", "agent_case_code"],
    members: AGENTS.map((a) => ({ memberId: a.agentId, memberType: "agent", nickname: a.nickname })),
  });
  const gid = group.groupId;
  // 触发规则
  for (const a of AGENTS)
    await post(`/ag-ui/agents/register?memberId=${encodeURIComponent(userId)}`, token, { agentId: a.agentId, nickname: a.nickname, groupId: gid, triggerMode: "mentioned" });
  console.log(`[OK] 已创建知聚「${GROUP_NAME}」：${gid}（群历史将累积全部轮次发言）\n`);

  // ============ 四轮推进会 ============
  // R1 需求澄清
  console.log("───────────────────── Round 1 · 需求澄清 ─────────────────────");
  await roundHumanAsk(token, gid, "产品(小梦)", "@产品助理 我们想把「知聚」做成一个多角色协作平台，目标团队 3-10 人。先帮我们把最小可用版本的边界和用户故事理清楚，别贪多。", ["agent_case_prd"], "产品助理回复", 90000);

  // R2 技术评审
  console.log("\n───────────────────── Round 2 · 技术评审 ─────────────────────");
  await roundHumanAsk(token, gid, "后端(阿凯)", "@代码帮 产品助理刚给了 MVP 范围，你从工程角度评审一下：数据模型、权限怎么做，有没有明显坑？不要太发散，先给最关键的约束。", ["agent_case_code"], "代码帮回复", 90000);

  // R3 多方对齐：两位数字员工直接对话、互相回应
  console.log("\n───────────────────── Round 3 · 多位数字员工对齐 ─────────────────────");
  console.log("👤 前端(小叶)：「你们俩角度不一样，直接在群里对一对，别各说各话。」");
  const discuss = await post(`/ag-ui/group/${gid}/discussion`, token, {
    content: `基于前序：产品给了 MVP 用户故事，代码帮给了工程约束。请你俩把「权限模型」和「MVP 验收标准」对齐成一份结论，并各自明确指出对方的提案里需要补或改的一点。`,
    agentIds: ["agent_case_prd", "agent_case_code"],
  });
  console.log(`[发起讨论] 参与: ${(discuss.agents || []).join(", ")}\n`);
  const got3 = await waitReplies(token, gid, ["agent_case_prd", "agent_case_code"], 150000, "数字员工对齐");
  if (got3.size < 2) throw new Error("Round3 数字员工对齐全员未到位");

  // R4 收敛追问：团队抛验收标准/风险，@两位收口
  console.log("\n───────────────────── Round 4 · 收敛与遗留项 ─────────────────────");
  await roundHumanAsk(token, gid, "测试(小迪)", "@产品助理 @代码帮 你们对齐后的结论里，那条最关键、最容易返工的验收标准是什么？上线的两件事先做哪两件？顺便把一时没结论的遗留项列出来。", ["agent_case_prd", "agent_case_code"], "数字员工收口", 120000);

  // 3. 汇总：回读群历史，按时间序列出完整对话骨架（人 + 数字员工交替）
  console.log("\n\n═══════════ 完整推进会记录（按时间序） ═══════════");
  const snap = await get(`/ag-ui/group/${gid}`, token);
  const all = (snap.latestMessages || []).filter((m) => !m.recalled);
  const who = (m) => (m.senderId === userId ? "👤 演示者/团队" : (AGENTS.find((x) => x.agentId === m.senderId)?.nickname || m.senderId));
  for (const m of all) {
    const tag = m.senderId.startsWith("agent_") ? "🤖" : "👤";
    console.log(`\n${tag} [${who(m)}] ${m.content.trim().slice(0, 220)}${m.content.trim().length > 220 ? " …" : ""}`);
  }
  console.log(`\n[完成] 推进会可登录 http://localhost:5200 查看完整对话（知聚 ${gid}）`);
  process.exit(0);
}

main().catch((e) => { console.error(`[失败] ${e.message}`); process.exit(1); });
