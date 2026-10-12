// 在线播放（转场 + 分步）端到端核对：登录 → 建群 → 上传带动画的 pptx → 发消息 →
// 取播放清单 / 展开版 PDF，打印页数比对。用法：node e2e-play-check.mjs <pptxPath>
import fs from "node:fs";
import path from "node:path";

const BASE = process.env.BASE_URL || "http://127.0.0.1:5200";
const USER = process.env.USERNAME || "david";
const PASS = process.env.PASSWORD || "lingtong";
const pptx = process.argv[2] || "artifacts/pptx-play/deck.pptx";

const j = async (r) => { const t = await r.text(); try { return JSON.parse(t); } catch { return t; } };
const login = await j(await fetch(BASE + "/ag-ui/user/login", {
  method: "POST", headers: { "Content-Type": "application/json" },
  body: JSON.stringify({ username: USER, password: PASS }),
}));
const token = login.token, userId = login.userId;
const H = { Authorization: "Bearer " + token };
console.log("login:", !!token, "user:", userId);

const group = await j(await fetch(BASE + "/ag-ui/group/create", {
  method: "POST", headers: { ...H, "Content-Type": "application/json" },
  body: JSON.stringify({ groupName: "播放验证-" + Date.now().toString(36).slice(-4), ownerId: userId, memberIds: [], members: [] }),
}));
const gid = group.groupId;
console.log("group:", gid);

const fd = new FormData();
fd.append("file", new Blob([fs.readFileSync(pptx)], { type: "application/vnd.openxmlformats-officedocument.presentationml.presentation" }), path.basename(pptx));
const up = await j(await fetch(BASE + "/ag-ui/upload", { method: "POST", headers: H, body: fd }));
const att = up.attachments[0];
console.log("upload:", att.attachmentId, att.name, att.kind);

await fetch(BASE + "/ag-ui/group/message/send", {
  method: "POST", headers: { ...H, "Content-Type": "application/json" },
  body: JSON.stringify({ groupId: gid, userId, content: "播放验证", attachments: [att] }),
});

const manifest = await j(await fetch(BASE + `/ag-ui/preview/${att.attachmentId}/play`, { headers: H }));
console.log("manifest.expanded:", manifest.expanded, "slideCount:", manifest.slideCount, "pages:", manifest.pages?.length);
console.log("transitions:", (manifest.transitions || []).map((t) => (t ? t.preset + (t.dir || t.direction ? "/" + (t.dir || t.direction) : "") : "-")).join(" "));
const byslide = {};
for (const p of manifest.pages || []) byslide[p.slide] = (byslide[p.slide] || 0) + 1;
console.log("pagesPerSlide:", JSON.stringify(byslide));

const pdfRes = await fetch(BASE + `/ag-ui/preview/${att.attachmentId}?play=true`, { headers: H });
if (!pdfRes.ok) {
  console.log("play pdf error:", pdfRes.status, JSON.stringify(await pdfRes.text()));
} else {
  const buf = Buffer.from(await pdfRes.arrayBuffer());
  console.log("play pdf:", pdfRes.status, pdfRes.headers.get("content-type"), buf.length + " bytes, header:", buf.subarray(0, 5).toString("latin1"));
  fs.writeFileSync(path.join("..", "artifacts", "pptx-play", "play.pdf"), buf);
}

try {
  await fetch(BASE + "/ag-ui/group/disband", {
    method: "POST", headers: { ...H, "Content-Type": "application/json" },
    body: JSON.stringify({ groupId: gid, operatorId: null }),
  });
  console.log("disbanded");
} catch { /* 忽略 */ }
