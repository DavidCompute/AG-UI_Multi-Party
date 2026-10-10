// 「执行计划」计划卡的收缩 / 展开（Playwright，真实浏览器，直接驱动前端函数）。
//
// 为什么直接调 page.evaluate 而不是真跑一次编排：一次编排要拉起协调员 + 多步激活，慢且脆；
// 而本次改动是**纯前端渲染**（renderPlanCard / bindPlanCardButtons），把它们放到真实浏览器里
// 驱动一遍即可覆盖：初始展开、点箭头收起、再点展开、以及“重渲染（计划推进）后状态保持”。
//
// 用法：cd tools && HEADLESS=1 node ui-plan-collapse.mjs
import { chromium } from "playwright";

const BASE_URL = process.env.BASE_URL || "http://localhost:5200";
const USERNAME = process.env.USERNAME || "david";
const PASSWORD = process.env.PASSWORD || "lingtong";
const HEADLESS = process.env.HEADLESS === "1";

let pass = 0, fail = 0;
const check = (label, cond, extra = "") => {
  if (cond) { pass++; console.log("  PASS  " + label + (extra ? "  — " + extra : "")); }
  else { fail++; console.log("  FAIL  " + label + (extra ? "  — " + extra : "")); }
};

const browser = await chromium.launch({ headless: HEADLESS });
const page = await browser.newPage();

try {
  await page.goto(BASE_URL, { waitUntil: "domcontentloaded" });
  await page.waitForSelector("#authOverlay", { timeout: 15000 });
  if (!(await page.evaluate(() => document.getElementById("authOverlay").classList.contains("hidden")))) {
    await page.fill("#authUsername", USERNAME);
    await page.fill("#authPassword", PASSWORD);
    await page.click("#authSubmit");
    await page.waitForFunction(() => document.getElementById("authOverlay")?.classList.contains("hidden"), { timeout: 20000 });
  }
  console.log("已登录 " + USERNAME + "\n");

  const out = await page.evaluate(() => {
    const host = document.createElement("div");
    document.body.appendChild(host);
    const steps = (doneCount) => [
      { text: "指派「配置管理员」", done: true },
      { text: "调用技能「连接测试」", done: doneCount >= 2 },
    ];
    const unfinished = { title: "执行计划", steps: steps(1) }; // 1/2 完成
    const finished = { title: "执行计划", steps: steps(2) };   // 2/2 完成
    const render = (plan, id) => {
      host.innerHTML = `<div class="plan-card">${renderPlanCard(plan, { id }, null)}</div>`;
      bindPlanCardButtons(host, { id });
      return host.querySelector(".plan-toggle");
    };
    const isCollapsed = () => host.querySelector(".plan-body").classList.contains("hidden");
    const r = {};

    // 默认联动完成度：未完→展开；全部完成→收起
    render(unfinished, "u1"); r.unfinishedDefaultExpanded = !isCollapsed();
    render(finished, "f1");   r.finishedDefaultCollapsed = isCollapsed();
    r.hasToggle = !!host.querySelector(".plan-toggle");

    // 手工覆盖：把“完成的（默认收起）”展开 → 重渲染仍展开
    let t = render(finished, "f2");
    t.click();
    r.manualExpanded = !isCollapsed();
    r.caretExpanded = t.textContent.trim();
    render(finished, "f2");
    r.overridePersistsExpanded = !isCollapsed();

    // 手工覆盖：把“未完成的（默认展开）”收起 → 重渲染仍收起 + aria=false + 箭头 ›
    t = render(unfinished, "u2");
    t.click();
    r.manualCollapsed = isCollapsed();
    r.caretCollapsed = t.textContent.trim();
    r.ariaCollapsed = t.getAttribute("aria-expanded");
    render(unfinished, "u2");
    r.overridePersistsCollapsed = isCollapsed();

    // 覆盖优先于自动默认：f2 已手工展开，即便重渲染成“未完成”也保持展开（未变）；
    // 再把它重渲染成“完成”更关键：不因完成度变化而被自动收起
    t = render(unfinished, "f2");
    r.overrideSurvivesCompletionChange = !isCollapsed();
    render(finished, "f2");
    r.manualExpandHeldWhenDone = !isCollapsed();

    host.remove();
    return { node: typeof renderPlanCard, ...r };
  });

  check("renderPlanCard 在前端可用", out.node === "function");
  check("计划卡头部有收缩 / 展开按钮", out.hasToggle);
  check("默认：未执行完 → 展开", out.unfinishedDefaultExpanded);
  check("默认：全部完成 → 收起", out.finishedDefaultCollapsed);
  check("可手工展开（完成的计划）", out.manualExpanded);
  check("收起 / 展开时箭头方向正确", out.caretExpanded === "⌄" && out.caretCollapsed === "›",
    `${out.caretExpanded} / ${out.caretCollapsed}`);
  check("收起时 aria-expanded=false", out.ariaCollapsed === "false", String(out.ariaCollapsed));
  check("可手工收起（未完成的计划）", out.manualCollapsed);
  check("手工展开后重渲染仍展开（覆盖生效）", out.overridePersistsExpanded);
  check("手工收起后重渲染仍收起（覆盖生效）", out.overridePersistsCollapsed);
  check("覆盖优先于完成度默认值", out.overrideSurvivesCompletionChange && out.manualExpandHeldWhenDone);

  console.log(`\n=== ${pass} 通过 / ${fail} 失败 ===`);
} catch (e) {
  fail++;
  console.error("\n界面验证异常：", e.message);
} finally {
  await browser.close();
  process.exit(fail === 0 ? 0 : 1);
}
