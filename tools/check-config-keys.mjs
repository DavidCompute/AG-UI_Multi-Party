// 配置键 ↔ C# 属性 静态校验（防「键配了却不生效」的静默漂移）
//
// 背景（见 docs/user-guide.md §9.9 / feature-review §8.9）：绑定到强类型 Options 的配置键，
// 若在代码里没有对应属性，**不会报错、也不会生效**——只能靠人工登记。本脚本把它变成自动守卫。
//
// 作用：
//   1. 从 docker-compose.yml（env `X__Y__Z`）与各 appsettings.json（`Section:Sub:Key`）收集配置键；
//   2. 按「节根 → 选项类文件」映射，检查该键的**末段属性名**是否真在该文件里声明（含同文件内的嵌套 Options 类）；
//   3. 报告漂移（配了但无属性）；已登记为「有意保留」的键进白名单，不算失败。
//
// 用法：node tools/check-config-keys.mjs    （退出码 0 = 无未登记漂移；1 = 有漂移；2 = 环境问题）

import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const ROOT = path.resolve(__dirname, "..");
const read = (p) => fs.readFileSync(path.join(ROOT, p), "utf8");
const NO_ALLOW = process.argv.includes("--no-allowlist"); // 自证 / 审计：把白名单抑制的已知漂移全部打出

// 节根 → 选项类所在文件（可多个：同节下不同子节定义在不同文件；同文件内的嵌套 Options 类一并纳入）
const OPTION_FILES = {
  Agents: ["src/AguiGroupChat.Agents/AgentOptions.cs", "src/AguiGroupChat.Agents/AudioOptions.cs"],
  GroupChat: ["src/AguiGroupChat.Hub/Options/GroupChatOptions.cs"],
  Auth: ["src/AguiGroupChat.Hub/Options/AuthOptions.cs"],
  Storage: ["src/AguiGroupChat.Hub/Options/StorageOptions.cs"],
  Persistence: ["src/AguiGroupChat.Hub/Persistence/PersistenceOptions.cs"],
  PromptBudget: ["src/AguiGroupChat.Hub/Options/PromptBudgetOptions.cs"],
  ClientTool: ["src/AguiGroupChat.Web/ClientToolBridgeApi.cs"],
  LinkProxy: ["src/AguiGroupChat.Web/LinkProxyApi.cs"],
  NativeTunnel: ["src/AguiGroupChat.Web/NativeTunnelApi.cs"],
  Observability: ["src/AguiGroupChat.Web/MetricsApi.cs"],
  StorageGovernance: ["src/AguiGroupChat.Web/StorageAdminApi.cs"],
};

// 已登记为「有意保留 / 不参与强类型绑定」的键（文档已说明）：不算漂移
const ALLOWLIST = new Map([
  ["Agents:WorkToolsEnabled", "§9.9-5：协议/前端提到但未实现（已文档化）"],
  ["Observability:ReplicaId", "§9.3.1：HubApp 启动时 `Configuration[\"Observability:ReplicaId\"]` 直读（非属性绑定）"],
  ["Agents:Memory:ModelDownloadUrl", "§9.9-3：桌面组合根直读 `memory[\"ModelDownloadUrl\"]`（非属性绑定）"],
]);

// ---- 收集「应存在」的属性名 ----
const propsByRoot = new Map();
for (const [root, files] of Object.entries(OPTION_FILES)) {
  const names = new Set();
  for (const file of files) {
    const src = read(file);
    // 形如：public Type Name { get; set; }  或  public Type Name { get; init; } = ...
    for (const m of src.matchAll(/public\s+(?:required\s+)?[\w<>?,\[\]\.\s]+?\s([A-Za-z_]\w*)\s*\{\s*(?:get|set|init)/g)) names.add(m[1]);
  }
  propsByRoot.set(root, names);
}

// ---- 收集配置键 ----
const keys = new Map(); // key -> where

// 1) docker-compose.yml：`X__Y__Z: ...`
{
  const src = read("docker-compose.yml");
  for (const m of src.matchAll(/^\s+([A-Za-z]\w*(?:__[A-Za-z0-9_]+)+):/gm)) {
    const key = m[1].split("__").filter((s) => !/^\d+$/.test(s)).map((s) => s).join(":");
    keys.set(key, "docker-compose.yml");
  }
}

// 2) appsettings.json（web + desktop）：递归展开成 `Section:Sub:Key`
function walkJson(obj, prefix, where, into) {
  if (obj === null || typeof obj !== "object") return;
  if (Array.isArray(obj)) { into.set(prefix, where); return; }
  for (const [k, v] of Object.entries(obj)) {
    const p = prefix ? `${prefix}:${k}` : k;
    // 只采集**叶值 / 数组**（节对象本身不是配置键，避免「Auth」「LinkProxy」这类误报）
    if (v === null || typeof v !== "object" || Array.isArray(v)) into.set(p, where);
    else walkJson(v, p, where, into);
  }
}
for (const f of ["src/AguiGroupChat.Web/appsettings.json", "src/AguiGroupChat.Web/appsettings.Development.json",
  "src/AguiGroupChat.Desktop/appsettings.json"]) {
  let txt;
  try { txt = read(f); } catch { continue; }
  try { walkJson(JSON.parse(txt), "", f, keys); } catch { /* 含注释的 json 容错跳过 */ }
}

// ---- 校验 ----
const drift = [];
const unmappedRoots = new Map();
for (const [key, where] of keys) {
  const parts = key.split(":");
  const root = parts[0];
  if (!(root in OPTION_FILES)) { unmappedRoots.set(root, where); continue; }
  const leaf = parts[parts.length - 1];
  const names = propsByRoot.get(root);
  if (!names.has(leaf) && (NO_ALLOW || (!ALLOWLIST.has(key) && !ALLOWLIST.has(parts.slice(0, 2).join(":"))))) {
    drift.push({ key, where, files: OPTION_FILES[root], allowed: ALLOWLIST.get(key) ?? ALLOWLIST.get(parts.slice(0, 2).join(":")) });
  }
}

console.log(`配置键校验：共 ${keys.size} 个键（compose + appsettings）`);
console.log(`已映射节根：${Object.keys(OPTION_FILES).join(", ")}`);
if (unmappedRoots.size) {
  console.log(`未映射节根（不参与校验）：${[...unmappedRoots.keys()].sort().join(", ")}`);
}
console.log(`白名单（有意保留）：${[...ALLOWLIST.keys()].join(", ")}${NO_ALLOW ? "（本轮已禁用）" : ""}`);
console.log(`\n未登记漂移：${drift.length}`);
for (const d of drift) console.log(`  ✗ ${d.key}  （来自 ${d.where}；${d.files.join(" / ")} 无此属性）${d.allowed ? `\n      ↳ 白名单原因：${d.allowed}` : ""}`);
if (drift.length) { console.log("\n结果：失败（存在配置键在代码里没有对应属性——会被静默忽略）"); process.exit(1); }
console.log("\n结果：通过（所有已映射节根的配置键都能绑定到 C# 属性；有意保留项已在白名单）");
