// 「输出物」验证（Playwright，真实浏览器）：
//   1. 聊天头部有「📁 输出物」按钮；进入知聚后可用；
//   2. 点开后列出本知聚全部附件（含上传的 docx / zip / pptx），按最新在前；
//   3. 办公文档行有「⬇ 下载」直链 + 「👁」（复用在线查看）；zip 只有下载、无预览；
//   4. 搜索框按文件名过滤；
//   5. 点「👁」能唤起文档预览弹窗；Esc 只收预览、输出物弹窗仍在。
//
// 用法：cd tools && HEADLESS=1 node ui-group-files.mjs
//   样例目录：默认 <repo>/artifacts/e2e（含 sample.docx / sample-notes.pptx）。
import { chromium } from "playwright";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const BASE_URL = process.env.BASE_URL || "http://localhost:5200";
const USERNAME = process.env.USERNAME || "david";
const PASSWORD = process.env.PASSWORD || "lingtong";
const HEADLESS = process.env.HEADLESS === "1";
const SAMPLES = process.env.SAMPLES || path.resolve(__dirname, "..", "artifacts", "e2e");

let pass = 0, fail = 0;
const check = (label, cond, extra = "") => {
  if (cond) { pass++; console.log("  PASS  " + label + (extra ? "  — " + extra : "")); }
  else { fail++; console.log("  FAIL  " + label + (extra ? "  — " + extra : "")); }
};
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

const stamp = Date.now().toString(36).slice(-5);
const groupName = "输出物验证-" + stamp;

const browser = await chromium.launch({ headless: HEADLESS });
const page = await browser.newPage();
let groupId = null, token = null;

try {
  const docxPath = path.join(SAMPLES, "sample.docx");
  const pptxPath = path.join(SAMPLES, "sample-notes.pptx");
  if (!fs.existsSync(docxPath) || !fs.existsSync(pptxPath)) throw new Error("缺少样例：" + SAMPLES);

  await page.goto(BASE_URL, { waitUntil: "domcontentloaded" });
  await page.waitForSelector("#authOverlay", { timeout: 15000 });
  if (!(await page.evaluate(() => document.getElementById("authOverlay").classList.contains("hidden")))) {
    await page.fill("#authUsername", USERNAME);
    await page.fill("#authPassword", PASSWORD);
    await page.click("#authSubmit");
    await page.waitForFunction(() => document.getElementById("authOverlay")?.classList.contains("hidden"), { timeout: 20000 });
  }
  token = (await (await page.request.post(BASE_URL + "/ag-ui/user/login",
    { data: { username: USERNAME, password: PASSWORD } })).json()).token;
  const H = { Authorization: "Bearer " + token };
  const api = async (method, p, data) => {
    const r = await page.request.fetch(BASE_URL + p, {
      method, headers: { ...H, "Content-Type": "application/json" },
      data: data === undefined ? undefined : JSON.stringify(data),
    });
    return { status: r.status(), body: await r.json().catch(() => null) };
  };
  const userId = (await api("GET", "/ag-ui/user/me")).body.userId;
  console.log("已登录 " + USERNAME + "\n");

  const created = await api("POST", "/ag-ui/group/create", { groupName, ownerId: userId, memberIds: [], members: [] });
  groupId = created.body?.groupId;
  check("建临时知聚", created.status === 200 && !!groupId, groupName);
  if (!groupId) throw new Error("临时知聚未创建");

  const send = async (fileName, mimeType, buffer, content) => {
    const up = await page.request.post(BASE_URL + "/ag-ui/upload", { headers: H, multipart: { file: { name: fileName, mimeType, buffer } } });
    const att = (await up.json()).attachments?.[0];
    const sent = await api("POST", "/ag-ui/group/message/send", { groupId, userId, content, attachments: [att] });
    check(`发送带 ${fileName} 的消息`, sent.status === 200);
    return att;
  };
  await send("季度报告.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document", fs.readFileSync(docxPath), "输出物：Word");
  await send("归档.zip", "application/zip", Buffer.from("PK\u0003\u0004fake"), "输出物：压缩包");
  await send("发布会.pptx", "application/vnd.openxmlformats-officedocument.presentationml.presentation", fs.readFileSync(pptxPath), "输出物：PPT");

  // 进入知聚
  await page.reload({ waitUntil: "domcontentloaded" });
  await page.waitForSelector(".group-item", { timeout: 20000 });
  await page.locator(".group-item", { hasText: groupName }).first().click();
  await page.waitForSelector(".msg .att-file", { timeout: 20000 });
  await sleep(500);

  // 断言 1：按钮可用 + 打开弹窗
  check("进入知聚后「📁 输出物」按钮可用", !(await page.locator("#groupFilesBtn").isDisabled()));
  await page.locator("#groupFilesBtn").click();
  await page.waitForSelector("#groupFilesModal:not(.hidden)", { timeout: 10000 });
  check("点开后「输出物」弹窗打开", true);

  // 断言 2：列出全部 3 个附件
  await page.waitForFunction(() => document.querySelectorAll("#groupFilesList .gf-row").length === 3, null, { timeout: 15000 });
  const rows = page.locator("#groupFilesList .gf-row");
  check("列出本知聚全部 3 个附件", (await rows.count()) === 3);
  const countTxt = await page.locator("#groupFilesCount").textContent();
  check("显示计数", /3/.test(countTxt || ""), countTxt);
  const names = await rows.locator(".gf-name").allTextContents();
  check("包含 docx / zip / pptx", ["季度报告.docx", "归档.zip", "发布会.pptx"].every((n) => names.includes(n)), names.join(", "));

  // 断言 3：办公文档有下载 + 预览；zip 只有下载
  const docxRow = rows.filter({ hasText: "季度报告.docx" }).first();
  const zipRow = rows.filter({ hasText: "归档.zip" }).first();
  const pptxRow = rows.filter({ hasText: "发布会.pptx" }).first();
  const dlHref = await docxRow.locator("a.gf-btn").first().getAttribute("href");
  check("docx 行有下载直链（带会话令牌）", !!dlHref && dlHref.includes("/ag-ui/files/") && dlHref.includes("token="), (dlHref || "").slice(0, 60) + "…");
  check("docx 行有「👁」预览", (await docxRow.locator(".att-preview").count()) === 1);
  check("pptx 行有「👁」预览", (await pptxRow.locator(".att-preview").count()) === 1);
  check("zip 行只有下载、无预览", (await zipRow.locator("a.gf-btn").count()) === 1 && (await zipRow.locator(".att-preview").count()) === 0);

  // 断言 4：搜索过滤
  await page.fill("#groupFilesSearch", "发布会");
  await sleep(150);
  check("按文件名筛选（只剩 pptx）", (await rows.count()) === 1 && (await rows.first().locator(".gf-name").textContent()) === "发布会.pptx");
  await page.fill("#groupFilesSearch", "");
  await sleep(150);
  check("清空筛选后恢复 3 行", (await rows.count()) === 3);

  // 断言 5：点「👁」唤起预览；Esc 只收预览
  await docxRow.locator(".att-preview").first().click();
  await page.waitForSelector("#docPreviewModal:not(.hidden)", { timeout: 10000 });
  await page.waitForSelector("#docPreviewFrame:not(.hidden)", { timeout: 60000 });
  check("点「👁」唤起在线查看（docx 走 iframe）", true);
  await page.keyboard.press("Escape");
  await page.locator("#docPreviewModal").waitFor({ state: "hidden", timeout: 5000 });
  check("Esc 只收预览弹窗", true);
  check("输出物弹窗仍在", await page.locator("#groupFilesModal").isVisible());

  await page.locator("#groupFilesClose").click();
  await page.locator("#groupFilesModal").waitFor({ state: "hidden", timeout: 5000 });
  check("「关闭」收起输出物弹窗", true);

  console.log(`\n=== ${pass} 通过 / ${fail} 失败 ===`);
} catch (e) {
  fail++;
  console.error("\n界面验证异常：", e.message);
  try { await page.screenshot({ path: path.join(__dirname, "screenshots", "group-files-fail.png") }); } catch { /* 忽略 */ }
} finally {
  try {
    if (groupId && token) {
      const r = await page.request.fetch(BASE_URL + "/ag-ui/group/disband", {
        method: "POST", headers: { Authorization: "Bearer " + token, "Content-Type": "application/json" },
        data: JSON.stringify({ groupId, operatorId: null }),
      });
      console.log("临时知聚已解散：" + r.status());
    }
  } catch { /* 忽略 */ }
  await browser.close();
  process.exit(fail === 0 ? 0 : 1);
}
