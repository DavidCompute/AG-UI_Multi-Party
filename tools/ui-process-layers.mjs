// 「执行过程 / 最终答复分层」验证（Playwright，真实浏览器，直接驱动 msgDom）。
//
// 为什么直接调 page.evaluate 而不是真跑一次编排：一次编排要拉起协调员 + 多步激活，慢且脆；
// 而本次改动是**纯前端渲染**（msgDom 把思考 / 计划 / 技能链 / 工具调用归入 .process 块），
// 把它放进真实浏览器驱动一遍即可覆盖：分组、位于正文之前、默认展开、折叠后重渲染保持、无过程不渲染。
//
// 用法：cd tools && HEADLESS=1 node ui-process-layers.mjs
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

const browser = await chromium.launch({ headless: HEADLESS });
const page = await browser.newPage();

try {
  await page.goto(BASE_URL, { waitUntil: "domcontentloaded" });
  await page.waitForSelector("#authOverlay", { timeout: 15000 });
  if (!(await page.evaluate(() => document.getElementById("authOverlay").classList.contains("hidden")))) {
    await page.fill("#authUsername", USERNAME);
    await page.fill("#authPassword", PASSWORD);
    await page.click("#authSubmit");
    await page.waitForFunction(() => document.getElementById("authOverlay")?.classList.contains("hidden"), { timeout: 20000 });
  }
  console.log("已登录 " + USERNAME + "\n");

  const out = await page.evaluate(() => {
    const r = { members: [], messages: [] };
    const base = {
      senderId: "agent_x", senderNickname: "测试员工", senderType: "agent",
      content: "这是最终答复", reasoning: "这是思考过程",
      timestamp: Date.now(), time: "12:00",
      streaming: false, recalled: false, mentions: [],
      plan: { title: "执行计划", steps: [{ text: "步骤一", done: true }, { text: "步骤二", done: false }] },
      agentChain: JSON.stringify({ name: "根节点", children: [] }),
      toolCalls: [{ id: "t1", name: "shell", done: false }],
    };
    const render = (m) => {
      const host = document.createElement("div");
      document.body.appendChild(host);
      host.appendChild(msgDom(m, r, null));
      return host;
    };
    const res = {};

    const host = render({ ...base, id: "m_proc" });
    const proc = host.querySelector(".process");
    const content = host.querySelector(".content");
    res.hasProcess = !!proc;
    res.processCollapsedByDefault = proc ? !proc.open : false; // 执行结束 → 默认收起（跟随计划卡）
    // 过程块应位于正文（最终答复）之前
    res.processBeforeContent = !!(proc && content
      && (proc.compareDocumentPosition(content) & Node.DOCUMENT_POSITION_FOLLOWING));
    res.thinkingInside = !!host.querySelector(".process-body .thinking");
    res.planInside = !!host.querySelector(".process-body .plan-card");
    res.chainInside = !!host.querySelector(".process-body .chain-card");
    res.toolsInside = !!host.querySelector(".process-body .tool-calls");
    res.summaryHasTitle = !!(host.querySelector(".process > .process-summary")?.textContent || "").trim();
    res.contentOutsideProcess = !!content && !proc.contains(content);

    // 流式中 → 默认展开
    const hostS = render({ ...base, id: "m_live", streaming: true });
    res.processOpenWhileStreaming = !!hostS.querySelector(".process")?.open;

    // 手动展开（已完成的消息）→ 重渲染仍展开（手动覆盖生效）
    proc.open = true;
    proc.dispatchEvent(new Event("toggle"));
    const host2 = render({ ...base, id: "m_proc" });
    res.expandedPersists = !!host2.querySelector(".process")?.open;

    // 无过程内容（无思考 / 计划 / 链 / 工具）时不渲染过程块
    const host3 = render({ ...base, id: "m_plain", reasoning: "", plan: null, agentChain: null, toolCalls: [] });
    res.noProcessWhenEmpty = !host3.querySelector(".process");
    res.contentStillThere = !!host3.querySelector(".content");

    // 流式局部插入路径：已有“独立”思考块 + 正文，工具调用到达时应补出过程容器并收入思考块
    const msgEl = document.createElement("div");
    msgEl.className = "msg";
    msgEl.dataset.mid = "m_stream";
    msgEl.innerHTML = `<div class="body">`
      + `<details class="thinking" open><summary>x</summary><div class="thinking-body">t</div></details>`
      + `<div class="content">答</div></div>`;
    document.body.appendChild(msgEl);
    const wrap = createToolCallsWrap(msgEl);
    wrap.appendChild(toolCallElement({ id: "t9", name: "shell", done: false }));
    res.streamProcessBeforeContent = !!msgEl.querySelector(".body > .process + .content");
    res.streamAdoptsThinking = !!msgEl.querySelector(".process-body .thinking");
    res.streamToolsInside = !!msgEl.querySelector(".process-body .tool-calls");
    res.streamNoOrphanThinking = msgEl.querySelectorAll(".body > .thinking").length === 0;
    // 撤回的消息不渲染过程块
    const hostRec = render({ ...base, id: "m_rec", recalled: true });
    res.recalledNoProcess = !hostRec.querySelector(".process");

    // pruneEmptyProcess：空过程体 → 移除容器；非空 → 保留
    const hostP = document.createElement("div");
    hostP.innerHTML = `<div class="msg"><div class="body"><div class="process"><div class="process-body"></div></div><div class="content">x</div></div></div>`;
    pruneEmptyProcess(hostP.querySelector(".msg"));
    res.pruneEmptied = !hostP.querySelector(".process");
    const hostP2 = document.createElement("div");
    hostP2.innerHTML = `<div class="msg"><div class="body"><div class="process"><div class="process-body"><div class="tool-calls"></div></div></div><div class="content">x</div></div></div>`;
    pruneEmptyProcess(hostP2.querySelector(".msg"));
    res.pruneKeptWhenNonEmpty = !!hostP2.querySelector(".process");

    msgEl.remove();

    host.remove(); hostS.remove(); host2.remove(); host3.remove(); hostRec.remove(); hostP.remove(); hostP2.remove();
    return { node: typeof msgDom, ...res };
  });

  check("msgDom 在前端可用", out.node === "function");
  check("有过程内容时渲染「执行过程」块", out.hasProcess);
  check("执行结束 → 过程块默认收起", out.processCollapsedByDefault);
  check("流式中 → 过程块默认展开", out.processOpenWhileStreaming);
  check("过程块位于正文（最终答复）之前", out.processBeforeContent);
  check("思考块归入过程容器", out.thinkingInside);
  check("计划卡归入过程容器", out.planInside);
  check("技能调用链归入过程容器", out.chainInside);
  check("工具调用归入过程容器", out.toolsInside);
  check("过程块标题非空", out.summaryHasTitle);
  check("正文在过程容器之外", out.contentOutsideProcess);
  check("手动展开后重渲染仍展开（覆盖生效）", out.expandedPersists);
  check("无过程内容时不渲染过程块", out.noProcessWhenEmpty);
  check("无过程内容时正文照常渲染", out.contentStillThere);
  check("流式：工具调用补出过程容器且置于正文之前", out.streamProcessBeforeContent);
  check("流式：已有思考块被收入过程容器", out.streamAdoptsThinking);
  check("流式：工具调用落在过程容器内", out.streamToolsInside);
  check("流式：不残留容器外的思考块", out.streamNoOrphanThinking);
  check("撤回的消息不渲染过程块", out.recalledNoProcess);
  check("pruneEmptyProcess：空过程容器被移除", out.pruneEmptied);
  check("pruneEmptyProcess：非空过程容器保留", out.pruneKeptWhenNonEmpty);

  console.log(`\n=== ${pass} 通过 / ${fail} 失败 ===`);
} catch (e) {
  fail++;
  console.error("\n界面验证异常：", e.message);
} finally {
  await browser.close();
  process.exit(fail === 0 ? 0 : 1);
}
