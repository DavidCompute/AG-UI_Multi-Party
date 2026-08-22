// AG-UI HITL 部署验证：注册新用户 → 建群（含需求助手）→ WS 订阅 → 请求发布公告 → 观察审批卡片事件
const base = "http://localhost:5200";
const wsBase = "ws://localhost:5200";

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const username = "hitl_test_" + Date.now().toString(36);

async function main() {
  // 1. 注册（注册即登录）
  const regRes = await fetch(`${base}/ag-ui/user/register`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ username, password: "secret123", nickname: "HITL 验证" }),
  });
  if (!regRes.ok) throw new Error(`注册失败: ${regRes.status}`);
  const reg = await regRes.json();
  const token = reg.token;
  const userId = reg.userId;
  console.log(`[OK] 注册 ${username} -> ${userId}`);

  // 2. 从目录中选智能体（优先选 AG-UI 桥接角色，复现邮件助手场景）
  const agents = await (await fetch(`${base}/ag-ui/agents`)).json();
  const target = agents.find((a) => a.bridgeEndpoint) || agents.find((a) => a.triggerMode === "mentioned") || agents[0];
  if (!target) throw new Error("目录中没有智能体");
  const agentId = target.agentId;
  const agentName = target.nickname;
  console.log(`[OK] 目标智能体: ${agentId}（${agentName}，桥接=${target.bridgeEndpoint || "无"}，触发 ${target.triggerMode}）`);

  // 3. 建群（含该智能体）
  const createRes = await fetch(`${base}/ag-ui/group/create`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({
      groupName: "HITL 验证群",
      ownerId: userId,
      memberIds: [agentId],
      members: [{ memberId: agentId, memberType: "agent", nickname: agentName }],
    }),
  });
  if (!createRes.ok) throw new Error(`建群失败: ${createRes.status}`);
  const group = await createRes.json();
  const gid = group.groupId;
  console.log(`[OK] 建群: ${gid}（成员 ${group.memberCount}）`);

  // 4. 注册触发规则（提及触发）
  const regRes2 = await fetch(`${base}/ag-ui/agents/register?memberId=${encodeURIComponent(userId)}`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ agentId, nickname: agentName, groupId: gid, triggerMode: "mentioned" }),
  });
  if (!regRes2.ok) throw new Error(`注册触发规则失败: ${regRes2.status}`);
  console.log("[OK] 触发规则已注册（mentioned）");

  // 4. WS 连接 + 订阅
  const ws = new WebSocket(`${wsBase}/ws?memberId=${encodeURIComponent(userId)}&token=${encodeURIComponent(token)}`);
  await new Promise((res, rej) => { ws.onopen = res; ws.onerror = rej; });
  console.log("[OK] WebSocket 已连接");

  const observed = [];
  let sent = false;
  ws.onmessage = (e) => {
    const evt = JSON.parse(e.data);
    observed.push(evt.type);
    switch (evt.type) {
      case "GROUP_CONNECTED":
        ws.send(JSON.stringify({ type: "GROUP_SUBSCRIBE", groupIds: [gid], timestamp: Date.now() }));
        break;
      case "RUN_ERROR":
        console.log(`>>> RUN_ERROR: code=${evt.errorCode} message=${evt.message}`);
        break;
      case "TEXT_MESSAGE_START":
        console.log(`>>> 消息开始: sender=${evt.senderId} role=${evt.role}`);
        break;
      case "TOOL_CALL_START":
        console.log(`>>> TOOL_CALL_START: name=${evt.toolCallName} id=${evt.toolCallId}`);
        break;
      case "TEXT_MESSAGE_END":
        console.log(`>>> 消息结束: ${evt.messageId}`);
        break;
      case "TEXT_MESSAGE_RESET":
        console.log(`>>> 消息内容已清空（智能体等待确认）: ${evt.messageId}`);
        break;
      case "AGENT_INTERACTION_REQUEST":
        console.log(`\n>>> 收到审批卡片事件:`);
        console.log(`    工具: ${evt.toolName} | 参数: ${JSON.stringify(evt.toolArguments)}`);
        console.log(`    targetMemberId(触发者): ${evt.targetMemberId} | 当前用户: ${userId} → 可决策: ${evt.targetMemberId === userId}`);
        if (evt.targetMemberId === userId) {
          console.log(`    >>> 自动批准（验证决策广播）`);
          ws.send(JSON.stringify({
            type: "AGENT_INTERACTION_RESOLVE",
            groupId: evt.groupId,
            interruptId: evt.interruptId,
            approved: true,
            memberId: userId,
          }));
        }
        break;
      case "AGENT_INTERACTION_RESOLVED":
        console.log(`>>> 决策广播事件: interrupt=${evt.interruptId} member=${evt.memberId} approved=${evt.approved}（全群同步）`);
        break;
      case "GROUP_SUBSCRIBE_ACK":
        if (!sent) {
          sent = true;
          console.log(`[OK] 已订阅，发送消息: 发邮件给david@lingtong.com…`);
          ws.send(JSON.stringify({
            type: "GROUP_MESSAGE_SEND",
            groupId: gid,
            topicId: "main",
            userId,
            content: "发邮件给david@lingtong.com，主题：hello，内容：hello again.",
            mentions: [agentId],
            mentionAll: false,
            visibility: "all",
          }));
        }
        break;
      default:
        break;
    }
  };

  // 5. 观察 90 秒（真实模型响应需要时间）：出现审批请求 → 自动批准 → 等待决策广播与恢复完成
  //    新行为：恢复复用同一消息（无新 TEXT_MESSAGE_START），决策广播后同一条消息 END 即恢复完成；
  //    外部服务可能多轮审批（工具链多次中断），循环直到决策后消息结束
  const deadline = Date.now() + 90000;
  let resolvedIdx = -1;
  while (Date.now() < deadline) {
    resolvedIdx = observed.indexOf("AGENT_INTERACTION_RESOLVED");
    if (resolvedIdx >= 0 && observed.indexOf("TEXT_MESSAGE_END", resolvedIdx + 1) > resolvedIdx) break;
    await sleep(500);
  }

  const hasInteraction = observed.includes("AGENT_INTERACTION_REQUEST");
  const hasReset = observed.includes("TEXT_MESSAGE_RESET");
  const hasResolved = observed.includes("AGENT_INTERACTION_RESOLVED");
  const resolvedIdxFinal = observed.indexOf("AGENT_INTERACTION_RESOLVED");
  // 新行为：恢复复用同一消息（不再新发 TEXT_MESSAGE_START），决策广播后同一条消息有内容回灌即成功
  const hasResumedContent = resolvedIdxFinal >= 0 && observed.indexOf("TEXT_MESSAGE_CONTENT", resolvedIdxFinal + 1) > resolvedIdxFinal;
  const hasResumedEnd = resolvedIdxFinal >= 0 && observed.indexOf("TEXT_MESSAGE_END", resolvedIdxFinal + 1) > resolvedIdxFinal;
  console.log(`\n[事件序列] ${observed.join(" -> ") || "(无)"}`);
  console.log(`\n[结论] 审批卡片事件: ${hasInteraction ? "✅" : "❌"} | 中断时内容重置: ${hasReset ? "✅" : "❌"} | 决策广播(AGENT_INTERACTION_RESOLVED): ${hasResolved ? "✅ 全群同步" : "❌"} | 恢复后内容回灌(同一消息): ${hasResumedContent ? "✅" : "❌"} | 恢复后消息结束: ${hasResumedEnd ? "✅" : "❌"}`);
  ws.close();
  process.exit(hasInteraction && hasReset && hasResolved && hasResumedContent && hasResumedEnd ? 0 : 1);
}

main().catch((e) => { console.error(`[失败] ${e.message}`); process.exit(1); });
