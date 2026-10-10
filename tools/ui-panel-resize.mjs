// 左 / 右侧栏宽度拖拽验证（Playwright，真实浏览器）：
//   1. 宽屏下两个拖拽手柄可见，左右栏有初始宽度；
//   2. 拖动左侧手柄 → 知聚栏变宽，聊天区相应变窄；
//   3. 松手持久化到 localStorage（agui.panelW.<uid>），刷新后仍生效；
//   4. 键盘 ←/→ 可微调；
//   5. 窄屏（≤900px）侧栏为抽屉时手柄隐藏。
//
// 用法：cd tools && HEADLESS=1 node ui-panel-resize.mjs
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
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

const browser = await chromium.launch({ headless: HEADLESS });
const page = await browser.newPage({ viewport: { width: 1366, height: 800 } });

const login = async () => {
  await page.goto(BASE_URL, { waitUntil: "domcontentloaded" });
  await page.waitForSelector("#authOverlay", { timeout: 15000 });
  if (!(await page.evaluate(() => document.getElementById("authOverlay").classList.contains("hidden")))) {
    await page.fill("#authUsername", USERNAME);
    await page.fill("#authPassword", PASSWORD);
    await page.click("#authSubmit");
    await page.waitForFunction(() => document.getElementById("authOverlay")?.classList.contains("hidden"), { timeout: 20000 });
  }
  await page.waitForSelector(".group-item", { timeout: 20000 });
};
const widthOf = (sel) => page.evaluate((s) => Math.round(document.querySelector(s).getBoundingClientRect().width), sel);
const panelW = () => page.evaluate(() => {
  const k = Object.keys(localStorage).find((x) => x.startsWith("agui.panelW."));
  return k ? JSON.parse(localStorage.getItem(k)) : null;
});

try {
  await login();
  // 清掉历史偏好，从默认宽度开始
  await page.evaluate(() => Object.keys(localStorage).filter((k) => k.startsWith("agui.panelW.")).forEach((k) => localStorage.removeItem(k)));
  await page.reload({ waitUntil: "domcontentloaded" });
  await page.waitForSelector(".group-item", { timeout: 20000 });

  check("宽屏显示 2 个侧栏拖拽手柄", (await page.locator(".panel-resizer").count()) === 2);
  check("左栏手柄可见", await page.locator("#groupsResizer").isVisible());
  check("右栏手柄可见", await page.locator("#membersResizer").isVisible());

  const g0 = await widthOf(".panel.groups");
  const chat0 = await widthOf("main.chat");
  check("知聚栏默认宽 220", g0 === 220, String(g0));

  // 拖动左栏手柄右移 120px
  const box = await page.locator("#groupsResizer").boundingBox();
  await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2);
  await page.mouse.down();
  await page.mouse.move(box.x + box.width / 2 + 120, box.y + box.height / 2, { steps: 8 });
  await page.mouse.up();
  await sleep(200);
  const g1 = await widthOf(".panel.groups");
  const chat1 = await widthOf("main.chat");
  check("拖动后知聚栏变宽（+110~130）", g1 - g0 >= 110 && g1 - g0 <= 130, `+${g1 - g0}`);
  check("聊天区相应变窄", chat0 - chat1 >= 110, `-${chat0 - chat1}`);

  const saved = await panelW();
  check("宽度已持久化到 localStorage", !!saved && saved.groups === g1, JSON.stringify(saved));

  // 刷新后仍生效
  await page.reload({ waitUntil: "domcontentloaded" });
  await page.waitForSelector(".group-item", { timeout: 20000 });
  await sleep(300);
  const g2 = await widthOf(".panel.groups");
  check("刷新后宽度保持", Math.abs(g2 - g1) <= 2, `${g1} -> ${g2}`);

  // 键盘微调：聚焦左栏手柄后按 →
  await page.locator("#groupsResizer").focus();
  await page.keyboard.press("ArrowRight");
  await sleep(150);
  const g3 = await widthOf(".panel.groups");
  check("键盘 → 微调 +8", g3 - g2 === 8, `${g2} -> ${g3}`);

  // 双击手柄复位默认宽度
  await page.locator("#groupsResizer").dblclick();
  await sleep(200);
  const g4 = await widthOf(".panel.groups");
  check("双击手柄复位为 220", g4 === 220, String(g4));

  // 右栏手柄拖动
  const mb = await page.locator("#membersResizer").boundingBox();
  const m0 = await widthOf(".panel.members");
  await page.mouse.move(mb.x + mb.width / 2, mb.y + mb.height / 2);
  await page.mouse.down();
  await page.mouse.move(mb.x + mb.width / 2 - 80, mb.y + mb.height / 2, { steps: 6 });
  await page.mouse.up();
  await sleep(200);
  const m1 = await widthOf(".panel.members");
  check("右栏拖宽（+70~90）", m1 - m0 >= 70 && m1 - m0 <= 90, `+${m1 - m0}`);

  // 窄屏：抽屉模式下手柄隐藏
  await page.setViewportSize({ width: 700, height: 800 });
  await sleep(300);
  check("窄屏隐藏拖拽手柄", (await page.locator(".panel-resizer:visible").count()) === 0);

  console.log(`\n=== ${pass} 通过 / ${fail} 失败 ===`);
} catch (e) {
  fail++;
  console.error("\n界面验证异常：", e.message);
} finally {
  await browser.close();
  process.exit(fail === 0 ? 0 : 1);
}
