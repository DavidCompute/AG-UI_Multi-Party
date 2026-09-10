/**
 * 把生成的技能同步到平台内置副本（嵌入资源）。
 * ---------------------------------------------------------------------------
 * 为什么要这一步：内置技能是 out/ 的一份拷贝，作为嵌入资源随程序集分发。
 * 改了 generate.mjs 后如果不同步，就会出现「界面上的技能」与「新部署自带的技能」不一致。
 *
 * 用法：node tools/docx-skills/sync-builtin.mjs
 *
 * 命名说明：内置目录下必须叫 *.skill.txt —— MSBuild 会把 *.cs.txt 里的 "cs"
 * 当作文化区后缀（Culture=cs），嵌入名被改写成 ...docx_gongwen.txt 并归入卫星资源，
 * 运行时按名字找不到。
 */

import { copyFileSync, readFileSync, writeFileSync, existsSync, mkdirSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
const repoRoot = join(here, "..", "..");
const outDir = join(here, "out");
const builtinDir = join(repoRoot, "src", "AguiGroupChat.Agents", "BuiltinSkills");

const FILES = ["docx_gongwen", "docx_notice", "docx_report"];

if (!existsSync(builtinDir)) {
  console.error(`找不到内置目录：${builtinDir}`);
  process.exit(1);
}

let changed = 0;
for (const name of FILES) {
  const src = join(outDir, `${name}.cs`);
  const dst = join(builtinDir, `${name}.skill.txt`);
  if (!existsSync(src)) {
    console.error(`缺少生成物 ${name}.cs，请先运行：node tools/docx-skills/generate.mjs`);
    process.exit(1);
  }

  // 统一 LF：嵌入正文不应带 CRLF（技能编译对换行不敏感，但保持一致便于比对）
  const body = readFileSync(src, "utf8").replace(/\r\n/g, "\n");
  const prev = existsSync(dst) ? readFileSync(dst, "utf8") : null;

  if (prev === body) {
    console.log(`= ${name}.skill.txt 已是最新`);
    continue;
  }
  writeFileSync(dst, body, "utf8");
  changed++;
  console.log(`✓ ${name}.skill.txt 已同步（${body.length} 字符）`);
}

console.log(
  changed === 0
    ? "\n内置副本已是最新，无需改动。"
    : `\n已同步 ${changed} 个内置技能。记得提交 src/AguiGroupChat.Agents/BuiltinSkills/ 下的改动。`
);
