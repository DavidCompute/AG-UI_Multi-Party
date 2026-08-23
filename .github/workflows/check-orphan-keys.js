#!/usr/bin/env node
/* CI：检测前端文案引用是否都存在于 i18n 字典（en）中
 *   - index.html 的 data-i18n* 属性
 *   - app.js 里的 t("key") 动态引用
 */
"use strict";
const fs = require("fs");
const path = require("path");

const ROOT = path.resolve(__dirname, "..", "..");
const WWW = path.join(ROOT, "src", "AguiGroupChat.Web", "wwwroot");

// 收集 en 字典里所有 key
global.window = global;
eval(fs.readFileSync(path.join(WWW, "i18n", "en.js"), "utf8"));
const enKeys = window.I18N_DICTS.en;
const known = new Set(Object.keys(enKeys));

let failed = false;

// 1) index.html 的 data-i18n 引用
const indexHtml = path.join(WWW, "index.html");
const src = fs.readFileSync(indexHtml, "utf8");
const attrRe = /data-i18n(?:-html|-title|-placeholder|-label)?="([^"]+)"/g;
const used = new Set();
let m;
while ((m = attrRe.exec(src)) !== null) used.add(m[1]);
const orphanAttr = [...used].filter((k) => !known.has(k));
if (orphanAttr.length) {
  failed = true;
  console.error(`✗ index.html 引用了 ${orphanAttr.length} 个字典不存在的 key：`);
  orphanAttr.forEach((k) => console.error("    " + k));
} else {
  console.log(`✓ index.html 中 ${used.size} 个 data-i18n* 引用全部存在于字典。`);
}

// 2) app.js 的 t("key") 引用
const appJs = path.join(WWW, "app.js");
const appSrc = fs.readFileSync(appJs, "utf8");
const tRe = /\bt\(\s*"([^"]+)"/g;
const tKeys = new Set();
while ((m = tRe.exec(appSrc)) !== null) tKeys.add(m[1]);
// 忽略以 . 结尾的动态拼接 key 前缀（如 t("err." + code)，key 在运行时拼接），无法静态校验
const dynamicPrefix = [...tKeys].filter((k) => k.endsWith("."));
const tKeysFinal = [...tKeys].filter((k) => !k.endsWith("."));
const orphanT = tKeysFinal.filter((k) => !known.has(k));
if (orphanT.length) {
  failed = true;
  console.error(`✗ app.js 引用了 ${orphanT.length} 个字典不存在的 key：`);
  orphanT.forEach((k) => console.error("    " + k));
} else {
  console.log(`✓ app.js 中 ${tKeysFinal.length} 个 t() 引用全部存在于字典${dynamicPrefix.length ? `（另跳过 ${dynamicPrefix.length} 个动态拼接 key 前缀）` : ""}。`);
}

if (failed) process.exit(1);
