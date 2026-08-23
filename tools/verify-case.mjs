// AG-UI 群聊 —— 8.4 切入故事「多真人群 + 数字员工 · 多轮推进会」自动化（基于 Docker 环境 http://localhost:5200）
//
// 与上一版的关键差异：
//  1) 真人有多个（不再是一个演示账号贴不同角色标签）：注册 产品小梦 / 后端阿凯 / 前端小叶 / 测试小迪 四个真实账号，
//     各自作为独立群成员用自己的身份发言（端到端验证“多人真人 + 数字员工”同场协作）。
//  2) 修复“再次 @ 数字员工无回复”可见性：
//     - 每轮都从【新】@ 消息出发，等待一个【新产生】的数字员工回复 messageId；
//     - 用 WebSocket 订阅该轮，实时记录 RUN_ERROR / AGENT_QUOTA_EXCEEDED 等失败事件；
//     - 若某轮超时未拿到新回复，直接报错退出（绝不静默跳过），把 WS 观察到的错误一并打印。
//
// 机制：所有真人发言与数字员工回复都进入知聚群历史；每轮触发数字员工时会读取最近历史，
// 因此在后一轮能看到前几轮内容，形成“有来有回、逐步收敛”的过程。
//
// 依赖：Docker web 容器健康；Provider=deepseek 且已配置 API Key。
// 运行：node tools/verify-case.mjs   （幂等可重跑：各账号均先尝试登录，已存在则复用，避免撞注册限流）
const base = "http://localhost:5200";
const wsBase = "ws://localhost:5200";
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const PASSWORD = "secret123";

const HUMANS = [
  { username: "p_xiaomeng", nickname: "产品小梦", role: "产品" },
  { username: "d_akai", nickname: "后端阿凯", role: "后端" },
  { username: "f_xiaoye", nickname: "前端小叶", role: "前端" },
  { username: "q_xiaodi", nickname: "测试小迪", role: "测试" },
  { username: "o_xiaolin", nickname: "运营小林", role: "运营" },
];
const AGENTS = [
  { agentId: "agent_sw_prd", nickname: "产品助理", description: "产品需求分析助手",
    instructions: "你是「产品助理」，负责需求澄清、用户故事与产品方案。先给结论再展开；结尾可反问推进讨论。",
    skills: [ { skillId: "skill_tech", description: "当需要工程技术可行性、数据模型或权限设计的工程评审意见时，调用此技能获取代码帮的工程视角。", targetAgentId: "agent_sw_code" } ] },
  { agentId: "agent_sw_code", nickname: "代码帮", description: "代码/架构评审助手",
    instructions: "你是「代码帮」，负责架构评审与工程实现。讲思路优先，关注约束/风险/验收，可回应前序发言。",
    skills: [ { skillId: "skill_prd", description: "当需要产品需求视角、MVP 边界或用户故事验证时，调用此技能获取产品助理的产品意见。", targetAgentId: "agent_sw_prd" } ] },
];

async function raw(url, opts = {}) { const r = await fetch(url, opts); const t = await r.text(); let j = null; try { j = t ? JSON.parse(t) : null; } catch {} return { r, j, t }; }
async function must(url, opts = {}) { const { r, j, t } = await raw(url, opts); if (!r.ok) throw new Error(`请求失败 ${r.status} ${url} :: ${t}`); return j; }
const auth = (tok) => ({ "Content-Type": "application/json", Authorization: `Bearer ${tok}` });
const post = (path, tok, payload) => must(base + path, { method: "POST", headers: auth(tok), body: JSON.stringify(payload) });
const get = (path, tok) => must(base + path, { method: "GET", headers: auth(tok) });
const nickOf = (sid) => AGENTS.find((x) => x.agentId === sid)?.nickname || HUMANS.find((x) => x.username === sid)?.nickname || sid;

/// 登录，不存在则注册；返回 { token, userId, nickname }
async function ensureUser(u) {
  const lg = await raw(`${base}/ag-ui/user/login`, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ username: u.username, password: PASSWORD }) });
  if (lg.r.ok) return { token: lg.j.token, userId: lg.j.userId, nickname: u.nickname };
  const rg = await raw(`${base}/ag-ui/user/register`, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ username: u.username, password: PASSWORD, nickname: u.nickname }) });
  if (!rg.r.ok) throw new Error(`账号 ${u.username} 登录/注册均失败: login=${lg.r.status} register=${rg.r.status} ${rg.t}`);
  return { token: rg.j.token, userId: rg.j.userId, nickname: u.nickname };
}

const _seenMsgIds = new Set();
async function snapshotAgentMsgs(token, gid, agentIds) {
  const snap = await get(`/ag-ui/group/${gid}`, token);
  return (snap.latestMessages || []).filter((m) => agentIds.includes(m.senderId) && m.content && m.content.trim());
}

/// 开一条 WS 观察该轮失败事件（RUN_ERROR / 配额 / 交互等），返回一个收集器。不主动发消息。
function observeWS(user, gid) {
  const ws = new WebSocket(`${wsBase}/ws?memberId=${encodeURIComponent(user.userId)}&token=${encodeURIComponent(user.token)}`);
  const errors = [];
  const tools = []; // 观测到数字员工调用的工具/技能名（TOOL_CALL_START）
  ws.onopen = () => { };
  ws.onmessage = (e) => {
    const evt = JSON.parse(e.data);
    if (evt.type === "GROUP_CONNECTED") ws.send(JSON.stringify({ type: "GROUP_SUBSCRIBE", groupIds: [gid], timestamp: Date.now() }));
    if ([ "RUN_ERROR", "AGENT_QUOTA_EXCEEDED", "AGENT_INTERACTION_REQUEST", "TEXT_MESSAGE_RESET" ].includes(evt.type)) errors.push(evt.type + (evt.message ? ` :: ${evt.message}` : "") + (evt.errorCode ? ` [${evt.errorCode}]` : ""));
    if (evt.type === "TOOL_CALL_START") tools.push(evt.toolCallName || evt.toolName || "unknown");
  };
  return { ws, errors, tools, close: () => { try { ws.close(); } catch {} } };
}

/// 等目标数字员工产生『本轮新增』回复（内容稳定后打印），返回收到的新回复；带 WS 错误收集
async function waitNewReplies(token, gid, agentIds, timeoutMs, errors) {
  const deadline = Date.now() + timeoutMs;
  const stable = new Map(); // messageId -> { senderId, text, stableSince }
  const labelled = new Map(); // senderId/target -> messageId 已打印
  let lastLog = Date.now();
  while (Date.now() < deadline) {
    for (const m of await snapshotAgentMsgs(token, gid, agentIds)) {
      if (_seenMsgIds.has(m.messageId)) continue;
      const st = stable.get(m.messageId) || { senderId: m.senderId, text: "", stableSince: null };
      if (m.content !== st.text) stable.set(m.messageId, { senderId: m.senderId, text: m.content, stableSince: Date.now() });
      else if (!st.stableSince) stable.set(m.messageId, { senderId: m.senderId, text: m.content, stableSince: Date.now() });
    }
    // 打印任何内容已稳定（3s 未变）且尚未打印的新消息（每消息只打一次）
    for (const [mid, st] of [...stable.entries()]) {
      if (_seenMsgIds.has(mid)) continue;
      if (st.text && st.stableSince && Date.now() - st.stableSince >= 3000) {
        _seenMsgIds.add(mid);
        labelled.set(st.senderId, mid);
        console.log(`\n── 🤖 ${nickOf(st.senderId)} 回复 ──\n${st.text.trim().replace(/^/gm, "  ")}`);
      }
    }
    const okIds = new Set([...labelled.keys()].filter((s) => agentIds.includes(s)));
    if (agentIds.every((a) => okIds.has(a))) return labelled;
    if (Date.now() - lastLog > 12000) { console.log(`  · 等待回复…（${okIds.size}/${agentIds.length}；WS: ${errors.join(" | ") || "-"}）`); lastLog = Date.now(); }
    await sleep(900);
  }
  return labelled;
}

/// 由某个真人发言 @某数字员工，等待新回复；超时则抛出（含 WS 错误）
async function roundAsk(user, gid, text, agents, timeoutMs, label) {
  const obs = observeWS(user, gid);
  await sleep(300);
  console.log(`\n👤 ${user.nickname}：「${text}」`);
  await post("/ag-ui/group/message/send", user.token, { groupId: gid, userId: user.userId, content: text.replace(/\n/g, " "), mentions: agents, timestamp: Date.now() });
  const got = await waitNewReplies(user.token, gid, agents, timeoutMs, obs.errors);
  obs.close();
  const missing = agents.filter((a) => !got.has(a));
  if (missing.length) {
    throw new Error(`${label} 未收到新回复：${missing.join(",")}；WS 观察到: ${obs.errors.join(" | ") || "无明显错误"}`);
  }
  return { replies: got, tools: [...new Set(obs.tools)] };
}

async function main() {
  console.log("═══ 知聚 8.4 案例 · 多真人群 + 数字员工 · 4 轮推进会（真实 DeepSeek） ═══\n");

  // 1. 注册/登录多个真人 + 数字员工
  const people = {};
  for (const h of HUMANS) people[h.username] = { role: h.role, ...(await ensureUser(h)) };
  console.log(`[OK] 已就绪真人: ${HUMANS.map((h) => `${h.nickname}(@${h.username})`).join("、")}`);

  const owner = people[HUMANS[0].username];
  // 创建数字员工并配置技能（技能 = 数字员工间协作：宿主可调用目标子数字员工）；已存在则跳过（幂等）
  const skillOf = (agents) => agents.map((s) => ({ skillId: s.skillId, description: s.description, targetAgentId: s.targetAgentId }));
  for (const a of AGENTS) {
    const ex = await get("/ag-ui/agents", owner.token);
    const payload = { agentId: a.agentId, nickname: a.nickname, description: a.description, instructions: a.instructions, triggerMode: "mentioned", keywords: [], skills: skillOf(a.skills) };
    if (!ex.some((x) => x.agentId === a.agentId))
      await post("/ag-ui/agents", owner.token, payload);
  }
  console.log(`[OK] 数字员工作备：${AGENTS.map((a) => a.nickname).join("、")}\n`);

  // 2. 建知聚：所有真人都入群 + 两个数字员工
  const allHumenIds = HUMANS.map((h) => people[h.username].userId);
  const group = await post("/ag-ui/group/create", owner.token, {
    groupName: "MVP-协作平台推进会", ownerId: owner.userId,
    memberIds: [...allHumenIds, ...AGENTS.map((a) => a.agentId)],
    members: [
      ...HUMANS.map((h) => ({ memberId: people[h.username].userId, memberType: "user", nickname: h.nickname })),
      ...AGENTS.map((a) => ({ memberId: a.agentId, memberType: "agent", nickname: a.nickname })),
    ],
  });
  const gid = group.groupId;
  for (const a of AGENTS)
    await post(`/ag-ui/agents/register?memberId=${encodeURIComponent(owner.userId)}`, owner.token, { agentId: a.agentId, nickname: a.nickname, groupId: gid, triggerMode: "mentioned" });
  console.log(`[OK] 建知聚「MVP-协作平台推进会」：${gid}（真人 ${HUMANS.length} + 数字员工 2，群历史跨轮累积）\n`);

  // 3. 四轮推进会（每次由不同真人@数字员工，验证“再次@也能回复”）
  const p = people;

  console.log("────────── Round 1 · 需求澄清（产品小梦 @产品助理） ──────────");
  await roundAsk(p["p_xiaomeng"], gid, "@产品助理 我们团队 3-10 人想把「知聚」做成多角色协作平台。先帮我们把 MVP 边界和用户故事理清楚，别贪多。", ["agent_sw_prd"], 90000, "Round1 产品助理回复");

  console.log("\n────────── Round 2 · 技术评审（后端阿凯 @代码帮） ──────────");
  await roundAsk(p["d_akai"], gid, "@代码帮 产品助理刚给了 MVP 范围。你从工程角度评审数据模型和权限怎么做、有哪些坑？给最关键约束。", ["agent_sw_code"], 90000, "Round2 代码帮回复");

  // 运营小林：不 @ 数字员工，仅作为真人成员插一句，证明“多人真人同场”
  console.log("\n────────── Round 2.5 · 运营插话（运营小林，不 @ 数字员工） ──────────");
  console.log("\n👤 运营小林：「我先记一笔：这轮信息要同步到周报，大家接着聊，结论我最后汇总。」");
  await post("/ag-ui/group/message/send", p["o_xiaolin"].token, {
    groupId: gid, userId: p["o_xiaolin"].userId, content: "我先记一笔：这轮信息要同步到周报，大家接着聊，结论我最后汇总。", mentions: [], timestamp: Date.now(),
  });

  console.log("\n────────── Round 3 · 技能协作（前端小叶请代码帮调用「产品助理」技能） ──────────");
  console.log(`\n👤 前端小叶：「代码帮，请先调用你的「产品助理」技能核对一下需求边界的这条验收口径，再给我权限相关的工程结论。」`);
  const obs3 = observeWS(p["f_xiaoye"], gid); await sleep(300);
  await post("/ag-ui/group/message/send", p["f_xiaoye"].token, {
    groupId: gid, userId: p["f_xiaoye"].userId,
    content: "@代码帮 请先调用你的「skill_prd」产品助理技能，核对一下需求边界的验收口径，再给出权限相关的工程结论。",
    mentions: ["agent_sw_code"], timestamp: Date.now(),
  });
  const got3 = await waitNewReplies(p["f_xiaoye"].token, gid, ["agent_sw_code"], 120000, obs3.errors);
  const skillTools3 = [...new Set(obs3.tools.filter((t) => t.startsWith("skill")))];
  obs3.close();
  if (!got3.has("agent_sw_code")) throw new Error(`Round3 代码帮未回复；WS: ${obs3.errors.join(" | ") || "无明显错误"}`);
  console.log(`\n[技能协作观测] 本轮模型调用了技能/工具: ${obs3.tools.join(", ") || "（未观测到工具调用）"}${skillTools3.length ? ` → ✅ 数字员工间技能协作 ${skillTools3.join("、")} 已触发` : " → ⚠️ 未触发技能（模型直接作答）"}`);

  // 反向技能演示：产品助理调用「代码帮」技能做工程校核（可选，说明技能可双向）
  console.log(`\n────────── Round 3b · 技能协作（产品助理调用「代码帮」技能） ──────────`);
  console.log(`\n👤 产品小梦：「产品助理，请你调用你的「代码帮」技能，让代码帮看看这个权限约束在工程上有没有坑，再回我。」`);
  const obs3b = observeWS(p["p_xiaomeng"], gid); await sleep(300);
  await post("/ag-ui/group/message/send", p["p_xiaomeng"].token, {
    groupId: gid, userId: p["p_xiaomeng"].userId,
    content: "@产品助理 请调用你的「skill_tech」代码帮技能，让代码帮评审一下「ACL 先于 RBAC」这条约束在工程上的实现有没有坑，再给我你的结论。",
    mentions: ["agent_sw_prd"], timestamp: Date.now(),
  });
  const got3b = await waitNewReplies(p["p_xiaomeng"].token, gid, ["agent_sw_prd"], 120000, obs3b.errors);
  const skillTools3b = [...new Set(obs3b.tools.filter((t) => t.startsWith("skill")))];
  obs3b.close();
  if (!got3b.has("agent_sw_prd")) throw new Error(`Round3b 产品助理未回复；WS: ${obs3b.errors.join(" | ") || "无明显错误"}`);
  console.log(`\n[技能协作观测] 本轮模型调用了技能/工具: ${obs3b.tools.join(", ") || "（未观测到工具调用）"}${skillTools3b.length ? ` → ✅ 反向技能协作 ${skillTools3b.join("、")} 已触发` : " → ⚠️ 未触发技能（模型直接作答）"}`);

  console.log("\n────────── Round 4 · 收口（测试小迪 再次@代码帮） ──────────");
  await roundAsk(p["q_xiaodi"], gid, "@代码帮 结合刚才的情况，把「最易返工的一条验收标准 + 上线先做的两件事 + 遗留项」收口成一份简洁结论。", ["agent_sw_code"], 150000, "Round4 数字员工收口");

  // 4. 汇总：明确列出参与者（真人几人 + 数字员工几人），再按时间序回放
  console.log("\n\n═══════════ 完整推进会回放 ═══════════");
  const snap = await get(`/ag-ui/group/${gid}`, owner.token);
  const members = snap.members || [];
  const humans = members.filter((m) => m.memberType === "user");
  const agents = members.filter((m) => m.memberType === "agent");
  console.log(`\n👥 本知聚参与者：真人 ${humans.length} 位（${humans.map((m) => m.nickname).join("、")}） + 数字员工 ${agents.length} 位（${agents.map((m) => m.nickname).join("、")}）`);
  for (const m of (snap.latestMessages || []).filter((x) => !x.recalled)) {
    const isAgent = m.senderId.startsWith("agent_");
    const tag = isAgent ? "🤖" : "👤";
    const name = m.senderNickname || (isAgent ? nickOf(m.senderId) : "成员");
    console.log(`\n${tag} [${name}] ${m.content.trim().slice(0, 140)}${m.content.trim().length > 140 ? " …" : ""}`);
  }
  console.log(`\n[完成] 全面会可登录 http://localhost:5200 查看（知聚 ${gid}）；真人账号见上。`);
  process.exit(0);
}

main().catch((e) => { console.error(`\n[失败] ${e.message}`); process.exit(1); });
