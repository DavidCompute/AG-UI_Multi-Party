// 文档引用完整性检查（静态、秒级）
//
// 背景：本仓库文档极其详细，且大量以**反引号路径**互相引用（如 `docs/user-guide.md`、
// `tools/README-regression.md`、`src/AguiGroupChat.Web/ConfigGovernanceApi.cs:18-38`）。
// 文件改名 / 移动 / 删除后这些引用会静默失效——读者点了才发现；行号也会随代码变动漂移。
// 本脚本把它变成自动守卫。
//
// 规则：
//   · 扫描全仓 `*.md`（跳过 node_modules / .git / artifacts / bin / obj / screenshots）；忽略围栏代码块；
//   · A. Markdown 相对链接 `](path)` / `href="path"`：跳过 http(s) / mailto / # / data:，去锚点后按文件目录解析；
//   · B. 反引号文件引用：形如 `a/b/c.ext` 或 `a/b/c.ext:12`（含 `/`，或为仓库根已知文件），
//        去 `:行号` / `#L…` 后按「文档目录 → 仓库根」解析；文件不存在 → **失败**，
//        行号超出该文件总行数 → **失败**（确定写错），行号在范围内但不精确 → 仅**提示**。
//
// 用法：node tools/check-docs-links.mjs   （退出码 0 = 通过；1 = 有失效引用；2 = 环境问题）

import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const ROOT = path.resolve(__dirname, "..");
const SKIP_DIRS = new Set(["node_modules", ".git", "artifacts", "bin", "obj", "screenshots", ".vs", "dist"]);

function walk(dir, out = [], ext = [".md"]) {
  for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
    if (e.isDirectory()) {
      if (SKIP_DIRS.has(e.name)) continue;
      if (e.name.startsWith(".") && e.name !== ".github") continue;
      walk(path.join(dir, e.name), out, ext);
    } else if (ext.some((x) => e.name.toLowerCase().endsWith(x))) out.push(path.join(dir, e.name));
  }
  return out;
}

// 全仓文件索引（用于“后缀式简写引用”的解析：`Web/AttachmentApi.cs` → src/…/AguiGroupChat.Web/AttachmentApi.cs）
const ALL_FILES = walk(ROOT, [], [""]).map((p) => path.relative(ROOT, p).replace(/\\/g, "/"));
// 后缀匹配（允许首段是片段：`Web/AttachmentApi.cs` → src/…/AguiGroupChat.Web/AttachmentApi.cs）
const suffixExists = (target) => ALL_FILES.some((f) => f === target || f.endsWith(target));
const findBySuffix = (target) => ALL_FILES.find((f) => f === target || f.endsWith(target));
// 容器内 / 运行时路径（不在仓库中）：不参与校验
const NOT_IN_REPO = /^\/|^data\/|^%LocalAppData%|^\.\//;

const EXTERNAL = /^(https?:|mailto:|tel:|data:|#|\/\/)/i;
const LINK_RE = /\]\(([^)\s]+)\)|href="([^"]+)"/g;
// 反引号文件引用：必须带 `/` 或为根级已知文件名；扩展名白名单
const ROOT_FILES = new Set(["README.md", "ROADMAP.md", "MARKETING.md", "PROTOCOL.md", "RELEASE_NOTES.md", "AGENTS.md", "LICENSE", "docker-compose.yml"]);
const CODE_RE = /`([A-Za-z0-9_./\\-]+\.(?:cs|md|mjs|js|json|yml|yaml|ps1|html|txt|wxs|sh|props|slnx|csproj|css)(?::\d+(?:-\d+)?|#L\d+(?:-L?\d+)?)?)`/g;

const failures = [];
const advisories = [];
let mdLinks = 0, codeRefs = 0, files = 0;

const lineCount = (p) => { try { return fs.readFileSync(p, "utf8").split(/\r?\n/).length; } catch { return 0; } };
function resolveExistence(fromDir, target) {
  const candidates = [path.resolve(fromDir, target), path.resolve(ROOT, target)];
  for (const c of candidates) if (fs.existsSync(c)) return c;
  return null;
}

for (const file of walk(ROOT)) {
  files++;
  const rel = path.relative(ROOT, file).replace(/\\/g, "/");
  const dir = path.dirname(file);
  const lines = fs.readFileSync(file, "utf8").split(/\r?\n/);
  let inFence = false;
  lines.forEach((line, i) => {
    if (/^\s*```/.test(line)) { inFence = !inFence; return; }
    if (inFence) return;

    // A. markdown 链接
    for (const m of line.matchAll(LINK_RE)) {
      let target = (m[1] || m[2] || "").trim();
      if (!target || EXTERNAL.test(target)) continue;
      target = target.split("#")[0].split("?")[0];
      if (!target) continue;
      mdLinks++;
      if (!resolveExistence(dir, decodeURIComponent(target))) failures.push(`${rel}:${i + 1} 链接失效 → ${target}`);
    }

    // B. 反引号文件引用
    for (const m of line.matchAll(CODE_RE)) {
      const raw = m[1];
      const hasSlash = raw.includes("/") || raw.includes("\\");
      const bare = raw.split(":")[0].split("#")[0];
      if (!hasSlash && !ROOT_FILES.has(bare)) continue;
      if (NOT_IN_REPO.test(bare)) continue;
      codeRefs++;
      if (!resolveExistence(dir, bare) && !suffixExists(bare)) { failures.push(`${rel}:${i + 1} 引用的文件不存在 → ${bare}`); continue; }
      // 行号越界（确定写错）
      const lm = raw.match(/[:#]L?(\d+)(?:-L?(\d+))?/);
      if (lm) {
        const resolved = resolveExistence(dir, bare) || findBySuffix(bare);
        const total = resolved ? lineCount(path.isAbsolute(resolved) ? resolved : path.join(ROOT, resolved)) : 0;
        const start = Number(lm[1]);
        const end = lm[2] ? Number(lm[2]) : start;
        if (total > 0 && end > total) failures.push(`${rel}:${i + 1} 行号越界 → ${bare}:${lm[1]}${lm[2] ? "-" + lm[2] : ""}（该文件仅 ${total} 行）`);
      }
    }
  });
}

console.log(`文档引用检查：扫描 ${files} 个 md；全仓索引 ${ALL_FILES.length} 个文件；markdown 链接 ${mdLinks} 条；反引号文件引用 ${codeRefs} 条`);
console.log(`失效引用：${failures.length}`);
for (const f of failures.slice(0, 80)) console.log(`  ✗ ${f}`);
if (failures.length > 80) console.log(`  …（还有 ${failures.length - 80} 条）`);
if (failures.length) { console.log("\n结果：失败（存在无法解析的文档引用）"); process.exit(1); }
console.log("\n结果：通过（所有文档引用均可解析，行号未越界）");
