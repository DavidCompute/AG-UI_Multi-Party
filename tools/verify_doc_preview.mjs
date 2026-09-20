// 办公文档「在线查看」端到端验证（对真实部署跑，不是 mock）：
//   登录 → 建临时知聚 → 上传真实 docx / xlsx / pptx / pdf → 发带附件的消息
//   → GET /ag-ui/preview/{attachmentId} → 断言状态码 / MIME / %PDF 魔数 / 页数，并核对缓存加速与各错误码。
//
// 用法（Windows / Linux 均可）：
//   node tools/verify_doc_preview.mjs
// 环境变量：
//   BASE     默认 http://localhost:5200
//   USERNAME / PASSWORD  默认 david / lingtong
//   SAMPLES  真实样例文件所在目录（需含 sample.docx / sample.xlsx / sample.pptx / sample.pdf）
//
// 数据安全：只在**新建的临时知聚**里发消息，结束即解散；上传的附件与预览缓存会留在磁盘上
// （缓存有 7 天保留期，会自然过期）。

import { readFileSync, existsSync } from "node:fs";
import { join } from "node:path";

const BASE = process.env.BASE || "http://localhost:5200";
const USERNAME = process.env.USERNAME || "david";
const PASSWORD = process.env.PASSWORD || "lingtong";
const SAMPLES = process.env.SAMPLES || join(process.env.TEMP || "/tmp", "agui-pv-e2e");

let pass = 0;
let fail = 0;

function check(name, ok, detail = "") {
  if (ok) { pass++; console.log(`  PASS  ${name}${detail ? "  — " + detail : ""}`); }
  else { fail++; console.log(`  FAIL  ${name}${detail ? "  — " + detail : ""}`); }
}

async function api(path, { method = "GET", token, body, form } = {}) {
  const headers = {};
  if (token) headers.Authorization = `Bearer ${token}`;
  let payload;
  if (form) payload = form;
  else if (body !== undefined) { headers["Content-Type"] = "application/json"; payload = JSON.stringify(body); }
  const res = await fetch(BASE + path, { method, headers, body: payload });
  return res;
}

/** PDF 页数：数 /MediaBox（LibreOffice 每页写一个）。足以区分「有内容」与「只有一页空壳」。 */
function pdfPageCount(buf) {
  const text = buf.toString("latin1");
  return (text.match(/\/MediaBox/g) || []).length;
}

async function main() {
  console.log(`\n=== 办公文档在线查看 E2E（${BASE}）===\n`);

  const login = await api("/ag-ui/user/login", { method: "POST", body: { username: USERNAME, password: PASSWORD } });
  if (!login.ok) throw new Error(`登录失败：${login.status} ${await login.text()}`);
  const auth = await login.json();
  const token = auth.token;
  const userId = auth.userId;
  console.log(`登录成功：${USERNAME}（${userId}）\n`);

  const created = await api("/ag-ui/group/create", {
    method: "POST", token,
    body: { groupName: "预览E2E-临时知聚", ownerId: userId, memberIds: [], members: [] },
  });
  if (!created.ok) throw new Error(`建群失败：${created.status} ${await created.text()}`);
  const groupId = (await created.json()).groupId;
  console.log(`临时知聚：${groupId}\n`);

  const cases = [
    { file: "sample.docx", label: "docx 文稿" },
    { file: "sample.xlsx", label: "xlsx 表格" },
    { file: "sample.pptx", label: "pptx 演示文稿" },
    { file: "sample.pdf", label: "pdf 原件（免转换直通）" },
  ];

  const uploads = [];
  for (const c of cases) {
    const path = join(SAMPLES, c.file);
    if (!existsSync(path)) { check(`${c.label}：样例文件存在`, false, path); continue; }
    const bytes = readFileSync(path);
    const form = new FormData();
    form.append("file", new Blob([bytes], { type: "application/octet-stream" }), c.file);
    const up = await api("/ag-ui/upload", { method: "POST", token, form });
    if (!up.ok) { check(`${c.label}：上传`, false, `${up.status} ${await up.text()}`); continue; }
    const att = (await up.json()).attachments[0];
    const send = await api("/ag-ui/group/message/send", {
      method: "POST", token,
      body: { groupId, userId, content: `E2E 预览：${c.file}`, attachments: [att] },
    });
    if (!send.ok) { check(`${c.label}：发消息`, false, `${send.status} ${await send.text()}`); continue; }
    uploads.push({ ...c, att, bytes: bytes.length });
  }
  console.log(`已上传并入会话：${uploads.length} 个附件\n`);

  console.log("—— 转换与内联渲染 ——");
  for (const u of uploads) {
    const t0 = Date.now();
    const res = await api(`/ag-ui/preview/${u.att.attachmentId}`, { token });
    const first = Date.now() - t0;
    const buf = Buffer.from(await res.arrayBuffer());
    const ctype = res.headers.get("content-type") || "";
    const okMagic = buf.subarray(0, 4).toString("latin1") === "%PDF";
    const pages = okMagic ? pdfPageCount(buf) : 0;

    check(`${u.label}：HTTP 200`, res.status === 200, `状态 ${res.status}`);
    check(`${u.label}：Content-Type=application/pdf`, ctype.startsWith("application/pdf"), ctype);
    check(`${u.label}：响应体是 PDF`, okMagic, `${buf.length} 字节`);
    check(`${u.label}：含可见页（页数 ≥1）`, pages >= 1, `${pages} 页`);
    check(`${u.label}：内联（无 Content-Disposition: attachment）`,
      !/attachment/i.test(res.headers.get("content-disposition") || ""),
      res.headers.get("content-disposition") || "(none)");

    const t1 = Date.now();
    const again = await api(`/ag-ui/preview/${u.att.attachmentId}`, { token });
    const second = Date.now() - t1;
    check(`${u.label}：二次取用命中缓存（不慢于首次）`, again.status === 200 && second <= first + 50,
      `首次 ${first}ms（${u.bytes} 字节源文件）→ 二次 ${second}ms`);
  }

  console.log("\n—— 鉴权与错误码 ——");
  const first = uploads[0];
  const noToken = await api(`/ag-ui/preview/${first.att.attachmentId}`);
  check("未登录 → 401", noToken.status === 401, `状态 ${noToken.status}`);

  const missing = await api("/ag-ui/preview/att_notexist", { token });
  check("不存在的附件 → 404", missing.status === 404, `状态 ${missing.status}`);

  // 非成员：另注册一个临时账号（不在上面的知聚里）
  const tag = "pv" + Date.now().toString(36);
  const reg = await api("/ag-ui/user/register", {
    method: "POST", body: { username: tag, password: "secret1", nickname: tag },
  });
  if (reg.ok) {
    const outsiderToken = (await reg.json()).token;
    const denied = await api(`/ag-ui/preview/${first.att.attachmentId}`, { token: outsiderToken });
    check("非本知聚成员 → 403", denied.status === 403, `状态 ${denied.status}`);
  } else {
    check("非成员 403 用例（临时账号注册）", false, `注册失败 ${reg.status}`);
  }

  // 不支持的类型：上传一个 .zip
  const zipForm = new FormData();
  zipForm.append("file", new Blob([Buffer.from("PK\u0003\u0004fake")], { type: "application/zip" }), "归档.zip");
  const zipUp = await api("/ag-ui/upload", { method: "POST", token, form: zipForm });
  if (zipUp.ok) {
    const zipAtt = (await zipUp.json()).attachments[0];
    await api("/ag-ui/group/message/send", {
      method: "POST", token,
      body: { groupId, userId, content: "E2E：zip", attachments: [zipAtt] },
    });
    const bad = await api(`/ag-ui/preview/${zipAtt.attachmentId}`, { token });
    const badBody = await bad.json().catch(() => ({}));
    check("不支持的类型 → 400 + BAD_REQUEST",
      bad.status === 400 && badBody.code === "BAD_REQUEST", `状态 ${bad.status}，code=${badBody.code}`);
  } else {
    check("不支持类型 400 用例（zip 上传）", false, `上传失败 ${zipUp.status}`);
  }

  // ---- 技能库试运行产物：产出文件应可下载 + 可在线查看（产出者本人）----
  console.log("\n—— 技能试运行产物（真实内置 docx 技能）——");
  const skills = await (await api("/ag-ui/skills/", { token })).json();
  const docxSkill = (skills || []).find((s) => s.skillId === "docx_report") || (skills || []).find((s) => /^docx_/.test(s.skillId || ""));
  if (!docxSkill) {
    check("找到内置 docx 技能", false, "技能库中没有 docx_* 技能");
  } else {
    const run = await api(`/ag-ui/skills/${encodeURIComponent(docxSkill.skillId)}/run`, {
      method: "POST", token, body: { query: "写一份 2026 年第一季度工作总结，小标题两级，包含三个要点" },
    });
    const runBody = await run.json().catch(() => ({}));
    const atts = Array.isArray(runBody.attachments) ? runBody.attachments : [];
    check(`试运行 ${docxSkill.skillId} 成功`, run.status === 200, `状态 ${run.status}`);
    check("试运行响应带回产出附件", atts.length > 0, `attachments=${atts.length}`);
    check("产出附件是可下载的站内地址",
      atts.length > 0 && /^\/ag-ui\/files\/att_[A-Za-z0-9_-]+\//.test(atts[0].url || ""), atts[0]?.url || "(无)");

    if (atts.length > 0) {
      const attId = atts[0].attachmentId;
      // 产物不属于任何知聚消息，因此这条 200 验的是「试运行产物归属」放行规则
      const pv = await api(`/ag-ui/preview/${attId}`, { token });
      const pvBuf = Buffer.from(await pv.arrayBuffer());
      const pvOk = pvBuf.subarray(0, 4).toString("latin1") === "%PDF";
      check("试运行产物可在线查看（200 + PDF）",
        pv.status === 200 && pv.headers.get("content-type")?.startsWith("application/pdf") && pvOk,
        `状态 ${pv.status}，${pvBuf.length} 字节，${pvOk ? "%PDF ✓" : "非 PDF"}`);
      check("试运行产物含可见页（页数 ≥1）", pdfPageCount(pvBuf) >= 1, `${pdfPageCount(pvBuf)} 页`);

      const dl = await api(atts[0].url, { token });
      check("试运行产物可下载（产出者本人）", dl.status === 200, `状态 ${dl.status}`);
      const anon = await api(`/ag-ui/preview/${attId}`);
      check("未登录不能看试运行产物 → 401", anon.status === 401, `状态 ${anon.status}`);
    }
  }

  // 清理：解散临时知聚（附件与缓存留盘，缓存 7 天自然过期）
  const disband = await api("/ag-ui/group/disband", {
    method: "POST", token, body: { groupId, operatorId: userId },
  });
  console.log(`\n临时知聚已解散：${disband.status}`);
  console.log(`\n=== 结果：${pass} 通过 / ${fail} 失败 ===\n`);
  process.exit(fail === 0 ? 0 : 1);
}

main().catch((e) => { console.error("E2E 异常：", e); process.exit(2); });
