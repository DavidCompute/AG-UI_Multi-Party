// 办公文档「在线查看」界面验证（Playwright，真实浏览器）
//
// 验什么：
//   1. 带 docx 附件的消息上出现「👁 在线查看」按钮，下载链接仍保留；
//   2. 点击后弹窗打开 → 服务端转好的 PDF 以 blob: URL 喂给弹窗 iframe，转换提示消失；
//   3. 弹窗头部有「下载原件」直链；Esc 与「关闭」都能收起并回收 blob；
//   4. 不支持在线查看的附件（zip）**不**出现该按钮。
//
// 为什么断言 blob: 而不是「看着像 PDF」：iframe 里是浏览器内置 PDF 阅读器，脚本读不到其内容；
//   blob: 出现即证明「取到了 application/pdf 且状态码为 200」（前端只在两者都对时才建 blob）。
//
// 数据安全：写操作只在**新建的临时知聚**里做，结束解散（附件与预览缓存留盘，缓存 7 天过期）。
//
// 用法：cd tools && HEADLESS=1 node ui-doc-preview.mjs
import { chromium } from "playwright";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const BASE_URL = process.env.BASE_URL || "http://localhost:5200";
const USERNAME = process.env.USERNAME || "david";
const PASSWORD = process.env.PASSWORD || "lingtong";
const HEADLESS = process.env.HEADLESS === "1";
const SAMPLES = process.env.SAMPLES || path.join(process.env.TEMP || "/tmp", "agui-pv-e2e");

let pass = 0;
let fail = 0;
const check = (label, cond, extra = "") => {
  if (cond) { pass++; console.log("  PASS  " + label + (extra ? "  — " + extra : "")); }
  else { fail++; console.log("  FAIL  " + label + (extra ? "  — " + extra : "")); }
};
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

const stamp = Date.now().toString(36).slice(-5);
const groupName = "预览UI验证-" + stamp;

const browser = await chromium.launch({ headless: HEADLESS });
const page = await browser.newPage();
let groupId = null;
let token = null;

try {
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
  const me = await api("GET", "/ag-ui/user/me");
  const userId = me.body.userId;
  console.log("已登录 " + USERNAME + "\n");

  // ---- 造数据：临时知聚 + docx 附件消息 + zip 附件消息（zip 应无预览入口）----
  const created = await api("POST", "/ag-ui/group/create", { groupName, ownerId: userId, memberIds: [], members: [] });
  groupId = created.body?.groupId;
  check("建临时知聚", created.status === 200 && !!groupId, groupName);
  if (!groupId) throw new Error("临时知聚未创建");

  const docxPath = path.join(SAMPLES, "sample.docx");
  if (!fs.existsSync(docxPath)) throw new Error("缺少真实样例：" + docxPath + "（见 verify_doc_preview.mjs 头部说明）");

  const send = async (fileName, mimeType, buffer, content) => {
    const up = await page.request.post(BASE_URL + "/ag-ui/upload", {
      headers: H,
      multipart: { file: { name: fileName, mimeType, buffer } },
    });
    const att = (await up.json()).attachments?.[0];
    check(`上传 ${fileName}`, !!att?.attachmentId);
    const sent = await api("POST", "/ag-ui/group/message/send", { groupId, userId, content, attachments: [att] });
    check(`发送带 ${fileName} 的消息`, sent.status === 200);
    return att;
  };

  const docxAtt = await send("季度报告.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
    fs.readFileSync(docxPath), "界面验证：Word 稿子");
  const zipAtt = await send("归档.zip", "application/zip", Buffer.from("PK\u0003\u0004fake"), "界面验证：压缩包");

  // ---- 打开该知聚 ----
  await page.reload({ waitUntil: "domcontentloaded" });
  await page.waitForSelector(".group-item", { timeout: 20000 });
  const row = page.locator(".group-item", { hasText: groupName }).first();
  await row.waitFor({ timeout: 20000 });
  await row.click();
  await page.waitForSelector(".msg .att-file", { timeout: 20000 });
  await sleep(600); // 让消息区完成一次渲染
  console.log("已进入临时知聚\n");

  // ---- 断言 1：docx 有预览入口、zip 没有 ----
  const previewBtns = page.locator(".msg .att-preview");
  const n = await previewBtns.count();
  check("只有办公文档附件出现「在线查看」入口（zip 不出现）", n === 1, `实际 ${n} 个`);

  const zipMsg = page.locator(".msg", { hasText: "归档.zip" }).first();
  check("zip 消息内的附件卡片无预览按钮",
    (await zipMsg.locator(".att-preview").count()) === 0);

  const docxMsg = page.locator(".msg", { hasText: "季度报告.docx" }).first();
  check("docx 消息仍保留下载卡片", (await docxMsg.locator("a.att-file").count()) === 1);
  const dlHref = await docxMsg.locator("a.att-file").first().getAttribute("href");
  check("下载卡片指向 /ag-ui/files/ 且带会话令牌",
    !!dlHref && dlHref.includes("/ag-ui/files/") && dlHref.includes("token="),
    (dlHref || "").slice(0, 80) + "…");

  // ---- 断言 2：点开预览 → 弹窗 + blob PDF ----
  await previewBtns.first().click();
  await page.waitForSelector("#docPreviewModal:not(.hidden)", { timeout: 10000 });
  check("点击后弹窗打开", true);
  check("弹窗标题为附件名",
    (await page.locator("#docPreviewName").textContent()) === "季度报告.docx",
    await page.locator("#docPreviewName").textContent());
  const modalDl = await page.locator("#docPreviewDownload").getAttribute("href");
  check("弹窗内「下载原件」指向 /ag-ui/files/", !!modalDl && modalDl.includes("/ag-ui/files/"),
    (modalDl || "").slice(0, 80) + "…");

  await page.waitForSelector("#docPreviewFrame:not(.hidden)", { timeout: 60000 });
  const frameSrc = await page.locator("#docPreviewFrame").getAttribute("src");
  check("iframe 已加载转换后的 PDF（blob: URL）", !!frameSrc && frameSrc.startsWith("blob:"),
    (frameSrc || "").slice(0, 40) + "…");
  check("转换中提示已隐藏",
    await page.locator("#docPreviewLoading").isHidden());
  check("错误提示未出现", await page.locator("#docPreviewError").isHidden());
  const frameBox = await page.locator("#docPreviewFrame").boundingBox();
  check("iframe 有实际可视区域", !!frameBox && frameBox.width > 600 && frameBox.height > 300,
    frameBox ? `${Math.round(frameBox.width)}×${Math.round(frameBox.height)}` : "无");

  // ---- 断言 3：Esc 关闭并回收 blob ----
  // 注意：Playwright 不能用 waitForSelector("#x.hidden") 等“隐藏”状态——隐藏元素永远不可能是 visible；
  // 要用 waitFor({ state: "hidden" })。
  await page.keyboard.press("Escape");
  await page.locator("#docPreviewModal").waitFor({ state: "hidden", timeout: 5000 });
  check("Esc 收起弹窗", true);
  await sleep(200);
  const srcAfter = await page.locator("#docPreviewFrame").getAttribute("src");
  check("关闭后 iframe 的 blob 已回收（src 清空）", !srcAfter, String(srcAfter));

  // ---- 断言 4：再开一次，用「关闭」按钮收 ----
  await previewBtns.first().click();
  await page.waitForSelector("#docPreviewModal:not(.hidden)", { timeout: 10000 });
  await page.waitForSelector("#docPreviewFrame:not(.hidden)", { timeout: 60000 });
  await page.locator("#docPreviewClose").click();
  await page.locator("#docPreviewModal").waitFor({ state: "hidden", timeout: 5000 });
  check("「关闭」按钮收起弹窗", true);

  console.log(`\n=== ${pass} 通过 / ${fail} 失败 ===`);
} catch (e) {
  fail++;
  console.error("\n界面验证异常：", e.message);
  try { await page.screenshot({ path: path.join(__dirname, "screenshots", "doc-preview-fail.png") }); } catch { /* 忽略 */ }
} finally {
  // 清理：解散临时知聚（附件与预览缓存留盘，缓存 7 天自然过期）
  try {
    if (groupId && token) {
      const r = await page.request.fetch(BASE_URL + "/ag-ui/group/disband", {
        method: "POST",
        headers: { Authorization: "Bearer " + token, "Content-Type": "application/json" },
        data: JSON.stringify({ groupId, operatorId: null }),
      });
      console.log("临时知聚已解散：" + r.status());
    }
  } catch { /* 忽略 */ }
  await browser.close();
  process.exit(fail === 0 ? 0 : 1);
}
