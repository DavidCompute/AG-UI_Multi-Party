/**
 * 把 pdf 技能同步到平台内置副本（嵌入资源）。
 * ---------------------------------------------------------------------------
 * 与 tools/pptx-skills/sync-builtin.mjs 同一职责：内置技能是仓库里技能正文的
 * 一份拷贝，作为嵌入资源随程序集分发。改完 pdf_doc.cs 必须同步，
 * 否则「界面上的技能」与「新部署自带的技能」会不一致。
 *
 * 用法：node tools/pdf-skills/sync-builtin.mjs
 *
 * 命名说明：内置目录下必须叫 *.skill.txt —— MSBuild 会把 *.cs.txt 里的 "cs"
 * 当作文化区后缀（Culture=cs），嵌入名被改写、运行时按名字找不到。
 */

import { readFileSync, writeFileSync, existsSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
const repoRoot = join(here, "..", "..");
const builtinDir = join(repoRoot, "src", "AguiGroupChat.Agents", "BuiltinSkills");

/** 仓库里的技能正文 → 内置同名技能 id */
const FILES = [["pdf_doc.cs", "pdf_doc"]];

if (!existsSync(builtinDir)) {
  console.error(`找不到内置目录：${builtinDir}`);
  process.exit(1);
}

let changed = 0;
for (const [file, id] of FILES) {
  const src = join(here, file);
  const dst = join(builtinDir, `${id}.skill.txt`);
  if (!existsSync(src)) {
    console.error(`缺少技能源文件 ${file}`);
    process.exit(1);
  }

  // 统一 LF：嵌入正文不应带 CRLF
  const body = readFileSync(src, "utf8").replace(/\r\n/g, "\n");
  const prev = existsSync(dst) ? readFileSync(dst, "utf8") : null;

  if (prev === body) {
    console.log(`= ${id}.skill.txt 已是最新`);
    continue;
  }
  writeFileSync(dst, body, "utf8");
  changed++;
  console.log(`✓ ${id}.skill.txt 已同步（${body.length} 字符）`);
}

console.log(
  changed === 0
    ? "\n内置副本已是最新，无需改动。"
    : `\n已同步 ${changed} 个内置技能。记得提交 src/AguiGroupChat.Agents/BuiltinSkills/ 下的改动。`
);
