// 复现真实桥接流程：POST RunAgentInput（SSE 保持连接）→ 读到 RUN_FINISHED 中断（不断开）
// → 在第一个连接仍打开时 POST resume → 观察恢复流是否返回内容
import http from "node:http";

const endpoint = "http://localhost:62572";
const threadId = "thread_keepalive_" + Date.now();
const runIdFirst = "run_first_" + Date.now();

function post(body) {
  return new Promise((resolve, reject) => {
    const data = JSON.stringify(body);
    const req = http.request(endpoint + "/", {
      method: "POST",
      headers: { "Content-Type": "application/json", "Content-Length": Buffer.byteLength(data) },
    }, (res) => {
      let buf = "";
      res.on("data", (c) => buf += c);
      res.on("end", () => resolve(buf));
      res.on("error", reject);
    });
    req.on("error", reject);
    req.write(data);
    req.end();
  });
}

async function main() {
  // 1. 首条请求：SSE 流（不断开，模拟桥接客户端读完中断后仍保持 _reader 打开）
  const firstBody = JSON.stringify({
    threadId, runId: runIdFirst,
    messages: [{ id: "msg_keepalive_1", role: "user", content: "发邮件给david@lingtong.com，主题：hello，内容：hello again." }],
    context: [],
  });
  const firstRes = await new Promise((resolve, reject) => {
    const req = http.request(endpoint + "/", {
      method: "POST",
      headers: { "Content-Type": "application/json", "Content-Length": Buffer.byteLength(firstBody) },
    }, resolve);
    req.on("error", reject);
    req.write(firstBody);
    req.end();
  });

  // 读 SSE 直到 RUN_FINISHED 中断，但保持连接打开（不发 req.destroy）
  let acc = "";
  const interruptId = await new Promise((resolve, reject) => {
    firstRes.on("data", (c) => {
      acc += c;
      const lines = acc.split("\n");
      for (const line of lines) {
        if (!line.startsWith("data:")) continue;
        try {
          const evt = JSON.parse(line.slice(5).trim());
          if (evt.type === "RUN_FINISHED" && evt.outcome?.type === "interrupt") {
            resolve(evt.outcome.interrupts[0].id);
            return;
          }
        } catch {}
      }
    });
    firstRes.on("error", reject);
    setTimeout(() => reject(new Error("首条流 60s 无中断")), 60000);
  });
  console.log("[OK] 首条流读到中断（连接保持打开）: " + interruptId);

  // 2. 在第一条连接仍打开时 POST resume
  const t0 = Date.now();
  const resumeBody = {
    threadId, runId: "run_different",
    messages: [],
    resume: [{ interruptId, status: "resolved", payload: { accepted: true } }],
  };
  const resumeRes = await new Promise((resolve, reject) => {
    const req = http.request(endpoint + "/", {
      method: "POST",
      headers: { "Content-Type": "application/json", "Content-Length": Buffer.byteLength(JSON.stringify(resumeBody)) },
    }, resolve);
    req.on("error", reject);
    req.write(JSON.stringify(resumeBody));
    req.end();
  });

  let gotContent = false, gotFinish = false;
  const content = await new Promise((resolve, reject) => {
    let buf = "";
    const timer = setTimeout(() => reject(new Error("恢复流 60s 无内容（时间戳基线 " + new Date(t0).toISOString() + "）")), 60000);
    resumeRes.on("data", (c) => {
      buf += c;
      const lines = buf.split("\n");
      for (const line of lines) {
        if (!line.startsWith("data:")) continue;
        try {
          const evt = JSON.parse(line.slice(5).trim());
          if (evt.type === "TEXT_MESSAGE_CONTENT") { gotContent = true; }
          if (evt.type === "RUN_FINISHED") {
            gotFinish = true;
            clearTimeout(timer);
            resolve(JSON.stringify(evt));
            return;
          }
        } catch {}
      }
    });
    resumeRes.on("error", (e) => { clearTimeout(timer); reject(e); });
  });

  console.log("[恢复流] 有内容: " + gotContent + " | 结束: " + gotFinish);
  console.log("[恢复流] RUN_FINISHED: " + content);
  // 释放首条连接
  firstRes.destroy();
  process.exit(0);
}

main().catch((e) => { console.error("[失败] " + e.message); process.exit(1); });
