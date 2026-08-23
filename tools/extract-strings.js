#!/usr/bin/env node
/**
 * 知聚(KnowGath) 国际化辅助工具：抽取前端源码中的中文字符串。
 *
 * 用途：
 *   1. 扫描 wwwroot/app.js 与 wwwroot/index.html，提取所有硬编码中文（面向用户的 UI 文案）。
 *   2. 输出 JSON 清单（文件，行号，片段，待提取结果），供人工分组迁移到英文/中文字典。
 *
 * 注意：
 *   - 该脚本仅辅助定位待翻译字符串，不会自动修改源码。
 *   - 业务数据（如默认 agent 名字、协议字段值）与代码日志需人工判断是否翻译。
 *
 * 用法（任意目录执行）：
 *   node tools/extract-strings.js [--file=src/AguiGroupChat.Web/wwwroot/app.js]
 *   默认扫描 app.js 与 index.html，输出到构建目录并打印摘要。
 */
"use strict";

const fs = require("fs");
const path = require("path");

const ROOT = path.resolve(__dirname, "..");
const WWW = path.join(ROOT, "src", "AguiGroupChat.Web", "wwwroot");

// 匹配一段连续中文字符（>=2 个汉字），可含少量中文字符范围
const CJK = /[\u4e00-\u9fff]/;
// 匹配中文串（含标点、占位），用于初步抽取
const CHINESE_RUN = /[\u4e00-\u9fff][\u4e00-\u9fff\uff00-\uffef0-9a-zA-Z\s，。、：；！？（）《》“”‘’…·×≥≤—～@?%/.-]{1,}/g;

function extract(file) {
  const abs = path.join(WWW, file);
  if (!fs.existsSync(abs)) {
    console.error(`[skip] 文件不存在: ${file}`);
    return [];
  }
  const lines = fs.readFileSync(abs, "utf8").split("\n");
  const hits = [];
  lines.forEach((line, idx) => {
    if (!CJK.test(line)) return;
    const m = line.match(CHINESE_RUN);
    if (!m) { hits.push({ line: idx + 1, s: line.trim().slice(0, 120) }); return; }
    m.forEach((run) => hits.push({ line: idx + 1, s: run.trim().slice(0, 120) }));
  });
  return hits;
}

function main() {
  const args = process.argv.slice(2);
  const only = args.find((a) => a.startsWith("--file="));
  const files = only ? [only.replace("--file=", "")] : ["app.js", "index.html"];
  const out = {};
  files.forEach((f) => { out[f] = extract(f); });
  const total = Object.values(out).reduce((n, arr) => n + arr.length, 0);
  console.log(`抽取完成，命中 ${total} 条中文字符串：`);
  files.forEach((f) => console.log(`  ${f}: ${out[f].length} 条`));
  const outPath = path.join(ROOT, "tools", "extract-report.json");
  fs.writeFileSync(outPath, JSON.stringify(out, null, 2), "utf8");
  console.log(`报告已写入: ${outPath}`);
}

main();
