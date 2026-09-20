// 技能库「试运行」产物的界面验证（Playwright，真实浏览器 + 真实内置 docx 技能）
//
// 验什么：
//   1. 技能库点 ▶ 试运行后，结果弹窗里出现「📦 本次产出」区，列出生出的文件；
//   2. 产出行有「⬇ 下载」直链（带会话令牌）与「👁 在线查看」按钮；
//   3. 点「👁 在线查看」→ 复用文档预览弹窗，iframe 载入 blob: PDF（即服务端转好的 PDF）；
//   4. 再次试运行时会先清掉上一次的产出（不残留混排）。
//
// 为什么这层要单独验：产出区是**新增界面**，而它是"服务端把 produce_file 入库为附件 + 登记归属"这条链路的
//   唯一用户可见出口。后端契约已由 tools/verify_doc_preview.mjs 断言，这里只验界面把它接上了。
//
// 用法：cd tools && HEADLESS=1 node ui-skill-run-artifacts.mjs
import { chromium } from "playwright";
import path from "node:path";
import { fileURLToPath } from "node:url";

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const BASE_URL = process.env.BASE_URL || "http://localhost:5200";
const USERNAME = process.env.USERNAME || "david";
const PASSWORD = process.env.PASSWORD || "lingtong";
const HEADLESS = process.env.HEADLESS === "1";
const SKILL_ID = process.env.SKILL_ID || "docx_report";

let pass = 0;
let fail = 0;
const check = (label, cond, extra = "") => {
  if (cond) { pass++; console.log("  PASS  " + label + (extra ? "  — " + extra : "")); }
  else { fail++; console.log("  FAIL  " + label + (extra ? "  — " + extra : "")); }
};
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

const browser = await chromium.launch({ headless: HEADLESS });
const page = await browser.newPage();

/** 走一遍「点 ▶ → 填试运行参数 → 等结果弹窗」。 */
async function runTrial(skillId, query) {
  const row = page.locator(".skill-row", { hasText: skillId }).first();
  await row.waitFor({ timeout: 20000 });
  await row.locator('[data-skill-act="test"]').click();

  // uiPrompt 对话框：填入试运行参数并确认
  await page.waitForSelector("#uiDialog:not(.hidden)", { timeout: 15000 });
  await page.fill("#uiDialogInput", query);
  await page.click("#uiDialogOk");

  // 结果弹窗（先显示“运行中”，随后被真实结果替换）
  await page.waitForSelector("#skillRunResultModal:not(.hidden)", { timeout: 20000 });
  await page.waitForFunction(
    (id) => {
      const el = document.getElementById("skillRunResultBody");
      return el && el.textContent && !el.textContent.includes("⚙️");
    },
    skillId,
    { timeout: 180000 },
  );
}

try {
  await page.goto(BASE_URL, { waitUntil: "domcontentloaded" });
  await page.waitForSelector("#authOverlay", { timeout: 15000 });
  if (!(await page.evaluate(() => document.getElementById("authOverlay").classList.contains("hidden")))) {
    await page.fill("#authUsername", USERNAME);
    await page.fill("#authPassword", PASSWORD);
    await page.click("#authSubmit");
    await page.waitForFunction(() => document.getElementById("authOverlay")?.classList.contains("hidden"), { timeout: 20000 });
  }
  console.log(`已登录 ${USERNAME}\n`);

  // 打开技能库（AI 角色管理 → 🎯 技能库）
  await page.waitForSelector("#agentManageBtn", { state: "visible", timeout: 15000 });
  await page.click("#agentManageBtn");
  await page.waitForSelector("#agentSkillLibBtn", { state: "visible", timeout: 15000 });
  await page.click("#agentSkillLibBtn");
  await page.waitForSelector("#skillModal:not(.hidden)", { timeout: 15000 });
  await page.waitForSelector(".skill-row", { timeout: 20000 });
  console.log("技能库已打开\n");

  check("产出区初始为空（上一次的产出不残留）",
    await page.locator("#skillRunResultArtifacts").isHidden() || (await page.locator(".skill-run-artifact").count()) === 0);

  // ---- 第一次试运行：真实内置 docx 技能 ----
  await runTrial(SKILL_ID, "写一份 2026 年第一季度工作总结，包含三个要点");
  console.log("—— 试运行产出 ——");
  check("结果弹窗显示了试运行输出",
    !(await page.locator("#skillRunResultBody").textContent()).includes("⚙️"));

  await page.waitForSelector("#skillRunResultArtifacts:not(.hidden)", { timeout: 20000 });
  const rows = page.locator(".skill-run-artifact");
  const n = await rows.count();
  check("产出区列出生出的文件", n >= 1, `${n} 个`);

  const first = rows.first();
  const nameText = await first.locator(".skill-run-artifact-name").textContent();
  check("产出行显示文件名", /\.docx$/i.test(nameText || ""), (nameText || "").trim());

  const href = await first.locator("a.skill-run-artifact-btn").getAttribute("href");
  check("产出行有带令牌的下载直链",
    !!href && href.includes("/ag-ui/files/") && href.includes("token="), (href || "").slice(0, 70) + "…");

  const dlText = await first.locator("a.skill-run-artifact-btn").textContent();
  check("下载按钮文案正确", /下载|Download/.test(dlText || ""), (dlText || "").trim());

  // ---- 在线查看：复用文档预览弹窗 ----
  const pvBtn = first.locator("button.skill-run-artifact-btn");
  check("产出行有「在线查看」按钮", (await pvBtn.count()) === 1);
  await pvBtn.click();
  await page.waitForSelector("#docPreviewModal:not(.hidden)", { timeout: 15000 });
  await page.waitForSelector("#docPreviewFrame:not(.hidden)", { timeout: 120000 });
  const src = await page.locator("#docPreviewFrame").getAttribute("src");
  check("预览弹窗载入 blob: PDF（在技能弹窗之上）", !!src && src.startsWith("blob:"), (src || "").slice(0, 40) + "…");
  check("转换中提示已隐藏", await page.locator("#docPreviewLoading").isHidden());
  const box = await page.locator("#docPreviewFrame").boundingBox();
  check("预览可视区可用", !!box && box.width > 600 && box.height > 300,
    box ? `${Math.round(box.width)}×${Math.round(box.height)}` : "无");

  await page.keyboard.press("Escape");
  await page.locator("#docPreviewModal").waitFor({ state: "hidden", timeout: 5000 });
  check("Esc 只收起预览弹窗（技能结果弹窗仍在）",
    await page.locator("#skillRunResultModal").isVisible());
  check("Esc 后 blob 已回收", !(await page.locator("#docPreviewFrame").getAttribute("src")));

  // ---- 第二次试运行：上一次的产出应先被清掉 ----
  await page.click("#skillRunResultClose");
  await page.locator("#skillRunResultModal").waitFor({ state: "hidden", timeout: 5000 });
  const rowAgain = page.locator(".skill-row", { hasText: SKILL_ID }).first();
  await rowAgain.locator('[data-skill-act="test"]').click();
  await page.waitForSelector("#uiDialog:not(.hidden)", { timeout: 15000 });
  await page.fill("#uiDialogInput", "再写一份 2026 年第二季度工作总结");
  await page.click("#uiDialogOk");
  await page.waitForSelector("#skillRunResultModal:not(.hidden)", { timeout: 20000 });
  await sleep(400);
  check("重跑时先清空上一次产出（不残留混排）",
    (await page.locator(".skill-run-artifact").count()) === 0
      || /⚙️/.test(await page.locator("#skillRunResultBody").textContent()),
    `当前行数 ${await page.locator(".skill-run-artifact").count()}`);
  await page.waitForFunction(
    () => {
      const el = document.getElementById("skillRunResultBody");
      return el && el.textContent && !el.textContent.includes("⚙️");
    },
    null,
    { timeout: 180000 },
  );
  await page.waitForSelector("#skillRunResultArtifacts:not(.hidden)", { timeout: 20000 });
  check("第二次试运行同样给出产出（且只有本次的）",
    (await page.locator(".skill-run-artifact").count()) === 1,
    `${await page.locator(".skill-run-artifact").count()} 个`);

  console.log(`\n=== ${pass} 通过 / ${fail} 失败 ===`);
} catch (e) {
  fail++;
  console.error("\n界面验证异常：", e.message);
  try { await page.screenshot({ path: path.join(__dirname, "screenshots", "skill-run-artifacts-fail.png") }); } catch { /* 忽略 */ }
} finally {
  await browser.close();
  process.exit(fail === 0 ? 0 : 1);
}
