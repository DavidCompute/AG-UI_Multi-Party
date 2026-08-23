#!/usr/bin/env node
/* CI：校验 i18n 字典 en/zh key 完全对称（英文为 source of truth） */
"use strict";
const fs = require("fs");
const path = require("path");

const ROOT = path.resolve(__dirname, "..", "..");
const WWW = path.join(ROOT, "src", "AguiGroupChat.Web", "wwwroot");
const i18nDir = path.join(WWW, "i18n");

function loadDict(file) {
  global.window = global; // 让字典的 window.I18N_DICTS 可用
  eval(fs.readFileSync(path.join(i18nDir, file), "utf8"));
  const lang = file.endsWith("en.js") ? "en" : "zh";
  return window.I18N_DICTS[lang];
}

function main() {
  const en = loadDict("en.js");
  const zh = loadDict("zh.js");
  const enKeys = Object.keys(en).sort();
  const zhKeys = Object.keys(zh).sort();
  let failed = false;

  const onlyZh = zhKeys.filter((k) => !(k in en));
  if (onlyZh.length) {
    failed = true;
    console.error(`✗ 仅存在于 zh (缺 en): ${onlyZh.length} 个`);
    onlyZh.forEach((k) => console.error("    " + k));
  }
  const onlyEn = enKeys.filter((k) => !(k in zh));
  if (onlyEn.length) {
    failed = true;
    console.error(`✗ 仅存在于 en (缺 zh): ${onlyEn.length} 个`);
    onlyEn.forEach((k) => console.error("    " + k));
  }

  // 空值检查
  const emptyErr = [];
  Object.keys(en).forEach((k) => { if (!String(en[k]).trim()) emptyErr.push(`en:${k}`); });
  Object.keys(zh).forEach((k) => { if (!String(zh[k]).trim()) emptyErr.push(`zh:${k}`); });
  if (emptyErr.length) {
    failed = true;
    console.error(`✗ 空值: ${emptyErr.join(", ")}`);
  }

  if (failed) { console.error(`\n共 ${enKeys.length} 个 key（en）/${zhKeys.length} 个 key（zh），存在不一致。`); process.exit(1); }
  console.log(`✓ en/zh key 对称，共 ${enKeys.length} 个 key。`);
}

main();
