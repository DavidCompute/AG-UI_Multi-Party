/**
 * 把 docx 场景技能导入平台技能库。
 * ---------------------------------------------------------------------------
 * 用法：
 *   node tools/docx-skills/import.mjs --base http://localhost:5200 --user <管理员> --pass <密码>
 *   node tools/docx-skills/import.mjs --base http://localhost:5200 --token <令牌>
 *
 * 可选：
 *   --only docx_gongwen,docx_report   只导入部分
 *   --dry-run                         只打印将要导入的内容，不调用接口
 *   --force                           已存在则改为更新（PUT）
 *
 * 说明：dotnet 技能仅管理员可建，且平台强制需人工审批（安全策略）。
 * 导入后即可在「AI 角色管理 → 技能库」看到，并可挂载到数字员工。
 */

import { readFileSync, existsSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
const outDir = join(here, "out");

// ---- 技能元信息：Description 是模型据以判断「何时调用」的唯一依据（Body 不进提示词），必须写清楚 ----
const SKILLS = [
  {
    file: "docx_gongwen.cs",
    skillId: "docx_gongwen",
    name: "公文生成（Word）",
    description:
      "生成规范排版的党政机关公文 Word 文档（通知 / 通报 / 请示 / 批复 / 报告）。" +
      "当用户要求「拟一份通知」「起草公文」「写个请示/批复」，或明确要求产出 .docx 公文时调用。" +
      "三号仿宋正文、黑体层次标题、22pt 小标宋大标题、固定行距 28pt、A4 公文页边距、页脚页码。" +
      "参数为 JSON：title(标题)、subtitle/author/date(可选)、outputPath(可选，.docx 落盘路径)、" +
      "sections 数组，每项可以是 heading(小节标题,level 1-3) / paragraph(段落) / numbered(编号列表) / " +
      "bullets(项目符号) / table({headers,rows}) / image({path,widthCm,caption,alt}) / " +
      "chart({type:bar|line|pie,categories,series|values,title,caption}) / toc(目录) / pageBreak。" +
      "返回 JSON 含生成的 docx 文件路径，请据实告知用户文件位置，不要编造正文内容。",
  },
  {
    file: "docx_notice.cs",
    skillId: "docx_notice",
    name: "通知公告生成（Word）",
    description:
      "生成简洁的通知 / 公告 / 事项说明 / 操作指引类 Word 文档，版式清爽、优先单页呈现。" +
      "当用户要求「写个通知」「出个公告」「说明一下并给我文档」，且不需要公文体例时调用。" +
      "微软雅黑标题与正文、不缩进、行距紧凑、无页码。" +
      "参数为 JSON：title、subtitle/author/date(可选)、outputPath(可选)、sections 数组，" +
      "每项可为 heading / paragraph / bullets / numbered / quote(引用强调) / table / image / chart / toc / pageBreak。" +
      "返回 JSON 含生成的 docx 文件路径。",
  },
  {
    file: "docx_report.cs",
    skillId: "docx_report",
    name: "工作报告生成（Word）",
    description:
      "生成工作报告 / 工作总结 / 调研报告 / 实施方案类 Word 文档，支持分章节与数据图表。" +
      "当用户要求「写份工作总结」「出个报告/方案」「把数据整理成报告」，或内容需要分章节、含表格或图表时调用。" +
      "黑体标题 + 宋体正文、首行缩进 2 字符、行距 20pt、页脚页码。" +
      "参数为 JSON：title、subtitle/author/date(可选)、outputPath(可选)、sections 数组，" +
      "每项可为 heading(level 1-3) / paragraph / bullets / numbered / quote / table({headers,rows}) / " +
      "image({path,widthCm,caption}) / chart({type:bar|line|pie,title,categories,series,values,caption}) / toc(目录) / pageBreak。" +
      "返回 JSON 含生成的 docx 文件路径。",
  },
];

function parseArgs(argv) {
  const o = { base: "http://localhost:5200", dryRun: false, force: false, only: null };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a === "--base") o.base = argv[++i];
    else if (a === "--user") o.user = argv[++i];
    else if (a === "--pass") o.pass = argv[++i];
    else if (a === "--token") o.token = argv[++i];
    else if (a === "--only") o.only = argv[++i].split(",").map((s) => s.trim()).filter(Boolean);
    else if (a === "--dry-run") o.dryRun = true;
    else if (a === "--force") o.force = true;
    else if (a === "--help" || a === "-h") o.help = true;
  }
  return o;
}

function usage() {
  console.log(`用法:
  node tools/docx-skills/import.mjs --base http://localhost:5200 --user <管理员> --pass <密码>
  node tools/docx-skills/import.mjs --base http://localhost:5200 --token <令牌>
可选: --only docx_gongwen,docx_report  --dry-run  --force`);
}

async function login(base, user, pass) {
  const res = await fetch(`${base}/ag-ui/user/login`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ username: user, password: pass }),
  });
  const text = await res.text();
  if (!res.ok) throw new Error(`登录失败（HTTP ${res.status}）：${text.slice(0, 200)}`);
  const json = JSON.parse(text);
  if (!json.token) throw new Error("登录响应缺少 token：" + text.slice(0, 200));
  return json.token;
}

async function api(base, token, method, path, body) {
  const res = await fetch(`${base}${path}`, {
    method,
    headers: { Authorization: `Bearer ${token}`, "Content-Type": "application/json" },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  const text = await res.text();
  let json = null;
  try { json = text ? JSON.parse(text) : null; } catch { /* 非 JSON */ }
  return { ok: res.ok, status: res.status, json, text };
}

const args = parseArgs(process.argv.slice(2));
if (args.help) { usage(); process.exit(0); }

const picked = SKILLS.filter((s) => !args.only || args.only.includes(s.skillId));
if (picked.length === 0) {
  console.error(`没有匹配的技能。可选：${SKILLS.map((s) => s.skillId).join(", ")}`);
  process.exit(1);
}

// 读取并以「按行拼接」确保换行统一（避免 CRLF 混入技能正文）
const payloads = [];
for (const s of picked) {
  const p = join(outDir, s.file);
  if (!existsSync(p)) {
    console.error(`缺少生成物 ${s.file}，请先运行：node tools/docx-skills/generate.mjs`);
    process.exit(1);
  }
  const body = readFileSync(p, "utf8").replace(/\r\n/g, "\n");
  payloads.push({
    skillId: s.skillId,
    name: s.name,
    description: s.description,
    kind: "dotnet",
    executionLocation: "server",
    body,
  });
}

if (args.dryRun) {
  for (const p of payloads) {
    console.log(`[dry-run] ${p.skillId}: kind=${p.kind} loc=${p.executionLocation} body=${p.body.length} 字符`);
    console.log(`          description=${p.description.slice(0, 60)}…`);
  }
  console.log(`共 ${payloads.length} 个技能；未调用接口。`);
  process.exit(0);
}

if (!args.token && !(args.user && args.pass)) {
  console.error("需要认证：--token <令牌>，或 --user/--pass。");
  usage();
  process.exit(1);
}

let token = args.token;
if (!token) {
  console.log(`以 ${args.user} 登录 ${args.base} …`);
  token = await login(args.base, args.user, args.pass);
}

console.log(`导入 ${payloads.length} 个技能到 ${args.base} …\n`);
let okCount = 0, failCount = 0;

for (const p of payloads) {
  // 先探测是否已存在（列表接口）
  const list = await api(args.base, token, "GET", "/ag-ui/skills");
  const exists = list.ok && Array.isArray(list.json) && list.json.some((x) => (x.skillId || x.SkillId) === p.skillId);

  let r;
  if (exists && args.force) {
    r = await api(args.base, token, "PUT", `/ag-ui/skills/${encodeURIComponent(p.skillId)}`, p);
  } else if (exists) {
    console.log(`⊙ ${p.skillId} 已存在，跳过（要覆盖请加 --force）`);
    continue;
  } else {
    r = await api(args.base, token, "POST", "/ag-ui/skills", p);
  }

  if (r.ok) {
    okCount++;
    console.log(`✓ ${p.skillId}（${exists ? "已更新" : "已创建"}）`);
  } else {
    failCount++;
    const msg = (r.json && (r.json.message || r.json.Message)) || r.text?.slice(0, 200) || r.status;
    console.log(`✗ ${p.skillId} 失败（HTTP ${r.status}）：${msg}`);
    if (r.status === 403) console.log("   提示：dotnet 技能仅系统管理员可创建，请用管理员账号或 --token。");
  }
}

console.log(`\n完成：成功 ${okCount}，失败 ${failCount}。`);
if (okCount > 0) {
  console.log("可在「AI 角色管理 → 🎯 技能库」查看；挂载到数字员工后即可使用。");
  console.log("注意：dotnet 技能执行时会弹出人工审批，属平台安全策略。");
}
process.exit(failCount > 0 ? 1 : 0);
