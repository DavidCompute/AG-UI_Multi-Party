// i18n 一致性审计：校验 zh / en 词典对齐，并检查 index.html / app.js 中引用的键是否都已定义。
// 用法：node tools/i18n-audit.mjs   （退出码 1 表示存在缺失，可用于 CI）
import fs from 'node:fs';

const webRoot = new URL('../src/AguiGroupChat.Web/wwwroot/', import.meta.url);
const read = (rel) => fs.readFileSync(new URL(rel, webRoot), 'utf8');

function loadDict(file) {
  const s = read('i18n/' + file);
  const m = new Map();
  const re = /"([A-Za-z0-9_.\-]+)"\s*:\s*"([^"]*)"/g;
  let g;
  while ((g = re.exec(s)) !== null) m.set(g[1], g[2]);
  return m;
}

const zh = loadDict('zh.js');
const en = loadDict('en.js');

let failed = false;

// 1) 双语对齐
const enOnly = [...en.keys()].filter((k) => !zh.has(k));
const zhOnly = [...zh.keys()].filter((k) => !en.has(k));
console.log(`dictionary keys: zh=${zh.size} en=${en.size}`);
if (enOnly.length) { failed = true; console.log('en-only:', enOnly.join(', ')); }
if (zhOnly.length) { failed = true; console.log('zh-only:', zhOnly.join(', ')); }

// 2) 使用键是否都有定义（data-i18n* 属性 + t("…") 调用）
const used = new Set();
for (const file of ['index.html', 'app.js']) {
  const s = read(file);
  for (const m of s.matchAll(/data-i18n(?:-html|-placeholder)?="([^"]+)"/g)) used.add(m[1]);
  for (const m of s.matchAll(/\bt\(\s*"([A-Za-z0-9_.\-]+)"/g)) used.add(m[1]);
}
// 动态拼接前缀（代码里 "prefix" + value）无法静态解析，这些以 . 结尾的视为动态键跳过
const isDynamicPrefix = (k) => k.endsWith('.');
const missing = [...used].filter((k) => !isDynamicPrefix(k) && (!zh.has(k) || !en.has(k)));
console.log(`used keys: ${used.size}; missing: ${missing.length}`);
if (missing.length) { failed = true; console.log(missing.sort().join('\n')); }

console.log(failed ? 'i18n audit: FAILED' : 'i18n audit: OK');
process.exit(failed ? 1 : 0);
