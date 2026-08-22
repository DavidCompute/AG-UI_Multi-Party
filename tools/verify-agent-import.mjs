// 端到端验证：模拟前端 importAgentsFromFile 导入 tools/agents-starter.json
import { readFileSync } from "node:fs";
const base = "http://127.0.0.1:5301";

async function main() {
  // 1. 注册（注册即登录）
  const reg = await (await fetch(`${base}/ag-ui/user/register`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ username: "seed_test", password: "secret123", nickname: "导入验证" }),
  })).json();
  const token = reg.token;
  console.log("[OK] 注册 ->", reg.userId);

  // 2. 模拟前端导入逻辑：逐条 POST（agentId 冲突自动换新）
  const data = JSON.parse(readFileSync("tools/agents-starter.json", "utf8"));
  let ok = 0, failed = 0;
  for (const a of data.agents) {
    const body = {
      agentId: a.agentId || null,
      nickname: (a.nickname || "").trim(),
      description: a.description || null,
      instructions: a.instructions || null,
      avatar: a.avatar || null,
      triggerMode: a.triggerMode || "mentioned",
      keywords: a.keywords || [],
      model: a.model || null,
      bridgeEndpoint: a.bridgeEndpoint || null,
      bridgeMode: a.bridgeMode || null,
      bridgeToken: null,
      personalMemoryEnabled: !!a.personalMemoryEnabled,
      isPrivate: !!a.isPrivate,
    };
    let res = await fetch(`${base}/ag-ui/agents`, {
      method: "POST",
      headers: { "Content-Type": "application/json", Authorization: `Bearer ${token}` },
      body: JSON.stringify(body),
    });
    if (res.status === 409 && body.agentId) {
      body.agentId = null;
      res = await fetch(`${base}/ag-ui/agents`, {
        method: "POST",
        headers: { "Content-Type": "application/json", Authorization: `Bearer ${token}` },
        body: JSON.stringify(body),
      });
    }
    if (res.ok) ok++; else { failed++; console.log("[FAIL]", body.nickname, res.status, await res.text().catch(()=>"")); }
  }
  console.log(`[导入] 成功 ${ok}，失败 ${failed}`);

  // 3. 验证目录
  const list = await (await fetch(`${base}/ag-ui/agents`, {
    headers: { Authorization: `Bearer ${token}` },
  })).json();
  console.log("[目录] 智能体总数:", list.length);
  const names = list.slice(0, 5).map((a) => a.nickname);
  console.log("[目录] 示例:", names.join("、"));
  process.exit(ok === data.agents.length ? 0 : 1);
}
main().catch((e) => { console.error("[失败]", e.message); process.exit(1); });
