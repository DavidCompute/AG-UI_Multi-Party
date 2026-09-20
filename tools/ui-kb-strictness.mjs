// 知识库「检索严格度」界面验证（Playwright）
//
// 验什么：知识库行上的 ⚙️ 能开设置弹窗、档位是知识库自己的三档（0.15/0.25/0.40）、
//         保存后接口回读、重新打开按已保存值回显、弹窗浮在管理弹窗之上。
//
// 数据安全：真实知识库只读；写操作全在临时知识库里做，结束删库（finally 兜底）。
//
// 用法：cd tools && HEADLESS=1 node ui-kb-strictness.mjs
import { chromium } from "playwright";

const BASE_URL = process.env.BASE_URL || "http://localhost:5200";
const USERNAME = process.env.USERNAME || "david";
const PASSWORD = process.env.PASSWORD || "lingtong";
const HEADLESS = process.env.HEADLESS === "1";

let ok = true;
const check = (label, cond, extra = "") => {
  ok = ok && !!cond;
  console.log((cond ? "  OK   " : "  FAIL ") + label + (cond || !extra ? "" : "  " + extra));
};
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

const browser = await chromium.launch({ headless: HEADLESS });
const page = await browser.newPage();
const stamp = Date.now().toString(36).slice(-5);
const probeKbName = "UI严格度验证-" + stamp;
let probeKbId = null;

try {
  await page.goto(BASE_URL, { waitUntil: "domcontentloaded" });
  await page.waitForSelector("#authOverlay", { timeout: 15000 });
  if (!await page.evaluate(() => document.getElementById("authOverlay").classList.contains("hidden"))) {
    await page.fill("#authUsername", USERNAME);
    await page.fill("#authPassword", PASSWORD);
    await page.click("#authSubmit");
    await page.waitForFunction(() => document.getElementById("authOverlay")?.classList.contains("hidden"), { timeout: 20000 });
  }
  const token = (await (await page.request.post(BASE_URL + "/ag-ui/user/login",
    { data: { username: USERNAME, password: PASSWORD } })).json()).token;
  const H = { Authorization: "Bearer " + token };
  const api = async (method, p, data) => {
    const r = await page.request.fetch(BASE_URL + p, {
      method, headers: { ...H, "Content-Type": "application/json" },
      data: data === undefined ? undefined : JSON.stringify(data),
    });
    return { status: r.status(), body: await r.json().catch(() => null) };
  };
  console.log("已登录 " + USERNAME);

  const created = await api("POST", "/ag-ui/kb", { name: probeKbName, description: "界面验证（用完即删）" });
  probeKbId = created.body?.kbId;
  check("建临时知识库", created.status === 200 && !!probeKbId, JSON.stringify(created).slice(0, 160));
  if (!probeKbId) throw new Error("临时库未创建");

  // 上传一份小文档，让试检索有东西可查（内容固定，方便断言）
  const docText = [
    "# 知聚平台内部说明（界面验证用）",
    "## 一、产品定位",
    "知聚是数字员工协作平台，核心是组织架构 + 技能库 + 知识库 + 图库。",
    "首创把公司组织架构搬进聊天：用户打造一个团队，平台自动生成对应的数字员工并建群。",
    "## 二、创新点",
    "组织即能力；数字员工可相互委派；记忆有拟人特征；交付物直出 Word/Excel/PPT/PDF。",
    "## 三、权限",
    "用户分组 + 分组授权；管理员控制台有审计与合规。",
    "## 四、项目代号",
    "本平台内部项目代号 ORION-7788，仅供内部文档引用。",
  ].join("\n");
  const up = await page.request.post(BASE_URL + "/ag-ui/upload", {
    headers: H,
    multipart: { file: { name: "知聚平台内部说明.md", mimeType: "text/markdown", buffer: Buffer.from(docText, "utf-8") } },
  });
  const att = (await up.json()).attachments?.[0]?.attachmentId;
  const added = await api("POST", `/ag-ui/kb/${probeKbId}/documents`, { attachmentId: att });
  check("上传示例文档", added.status === 200, JSON.stringify(added).slice(0, 160));
  let docReady = false;
  for (let i = 0; i < 90; i++) {
    const list = await api("GET", "/ag-ui/kb");
    const kb = (list.body || []).find((k) => k.kbId === probeKbId);
    const doc = (kb?.documents || [])[0];
    if (doc?.status === "ready") { docReady = true; console.log("   切片数：" + doc.chunkCount); break; }
    if (doc?.status === "error") break;
    await sleep(2000);
  }
  check("文档向量化完成（试检索才有东西可查）", docReady);

  // 打开知识库管理（入口在「AI 角色管理」工具栏）
  await page.waitForSelector("#agentManageBtn", { state: "visible", timeout: 20000 });
  await page.click("#agentManageBtn");
  await page.click("#agentKbManageBtn");
  await page.waitForSelector("#kbModal:not(.hidden)", { timeout: 10000 });

  const item = page.locator("#kbListWrap .kb-list-item").filter({ hasText: probeKbName }).first();
  check("临时知识库出现在列表里", await item.count() > 0);
  const gear = item.locator(".kb-set").first();
  check("知识库行上有 ⚙️ 设置入口", await gear.count() > 0);

  if (await gear.count() > 0) {
    await gear.click();
    await page.waitForSelector("#libSetModal:not(.hidden)", { timeout: 5000 });
    check("⚙️ 打开共用设置弹窗", true);
    const sel = page.locator("#libSetStrictness");
    const values = await sel.locator("option").evaluateAll((os) => os.map((o) => o.value));
    console.log("   档位：" + JSON.stringify(values));
    check("档位是知识库自己的三档（0.15/0.25/0.40）",
      JSON.stringify(values) === JSON.stringify(["0.15", "0.25", "0.4"]), JSON.stringify(values));
    check("未设置时回显为「标准」（0.25）", await sel.inputValue() === "0.25", await sel.inputValue());
    const hint = (await page.locator("#libSetHint").textContent()) || "";
    check("弹窗里带了说明（提到实测分数范围）", hint.includes("0.31") || hint.includes("0.38"), hint.slice(0, 60));

    await page.selectOption("#libSetStrictness", "0.4");
    await page.click("#libSetOk");
    await page.waitForSelector("#libSetModal.hidden", { state: "hidden", timeout: 8000 });
    const after = await api("GET", "/ag-ui/kb");
    const mine = (after.body || []).find((k) => k.kbId === probeKbId);
    check("保存后接口回读为 0.40", Math.abs((mine?.minScore ?? 0) - 0.4) < 1e-9, String(mine?.minScore));

    // 重新打开：应按已保存值回显，且清掉上次的试检索结果（后面的试检索要开着弹窗做）
    await item.locator(".kb-set").first().click();
    await page.waitForSelector("#libSetModal:not(.hidden)", { timeout: 5000 });
    check("重新打开按已保存值回显 0.40", await page.locator("#libSetStrictness").inputValue() === "0.4",
      await page.locator("#libSetStrictness").inputValue());
    check("重新打开清掉了上次的试检索结果", (await page.locator("#libSetProbeResult .lib-set-probe-hit").count()) === 0);

    // 先清空查询再切档：我的实现里“换档位自动重试”只在已有查询时才跑，
    // 否则这个自动请求会与下面的手动点击抢竞态（旧查询的结果后到会把新结果盖掉）。
    const probeAt = async (gateValue, query) => {
      await page.fill("#libSetProbeQuery", "");
      await page.selectOption("#libSetStrictness", gateValue);
      await page.fill("#libSetProbeQuery", query);
      await page.click("#libSetProbeBtn");
      // 等“本次档位”的结果（带上档位值，避免拿到上一次的残留文案）
      await page.waitForFunction((g) => {
        const t = document.getElementById("libSetProbeGate")?.textContent || "";
        return t.includes(`（${g}）`) && /召回|hit/.test(t);
      }, gateValue, { timeout: 30000 });
      return {
        rows: await page.locator("#libSetProbeResult .lib-set-probe-hit").count(),
        gate: ((await page.locator("#libSetProbeGate").textContent()) || "").trim(),
      };
    };

    // 正例：真实提问（答案在文档里）在标准档能召回
    let r = await probeAt("0.25", "知聚平台有什么创新点");
    console.log("   标准 0.25 + 真实提问：" + r.gate);
    check("标准档下真实提问能召回（>=1 条）", r.rows >= 1, r.gate);
    const scoreText = r.rows ? (await page.locator("#libSetProbeResult .lib-set-probe-hit .score").first().textContent()) || "" : "";
    check("命中行里有分数（0.00~1.00）", /^\d\.\d\d$/.test(scoreText.trim()), scoreText);

    // 反例：与文档**零词面交集且语义无关**的提问 → 严格档召不回（确定性，不依赖分数边界）
    r = await probeAt("0.4", "紫罗兰色潜水艇在珊瑚礁间穿行");
    console.log("   严格 0.40 + 无关提问：" + r.gate);
    check("无关提问：严格档 0 条 + 解释文案",
      r.rows === 0 && (await page.locator("#libSetProbeResult .kb-empty").count()) === 1, r.gate);

    // 单调性：同一个“只共用常用词”的提问，严格档的命中数不应多于标准档
    //（具体分数随文档长度浮动，所以只钉“越过严越少”这个方向）
    const stdCommon = await probeAt("0.25", "公司食堂今天中午吃什么");
    const strictCommon = await probeAt("0.4", "公司食堂今天中午吃什么");
    console.log(`   只共用常用词：标准 ${stdCommon.rows} 条 → 严格 ${strictCommon.rows} 条`);
    check("档位越严，命中数不增（严格 <= 标准）", strictCommon.rows <= stdCommon.rows,
      `${stdCommon.rows} → ${strictCommon.rows}`);

    // 罕见号：词面兜底的意义所在 —— 换档（含自动重试）都应能召回来
    r = await probeAt("0.25", "ORION-7788 是什么");
    console.log("   标准 0.25 + 罕见号：" + r.gate);
    check("罕见号在标准档能召回（兜底有效）", r.rows >= 1, r.gate);
    // 再验证“换档位自动重试”：不动查询，只切档 → 应重新跑一遍（此时查询非空）
    const before = ((await page.locator("#libSetProbeGate").textContent()) || "").trim();
    await page.selectOption("#libSetStrictness", "0.4");
    await page.waitForFunction((g) => {
      const t = document.getElementById("libSetProbeGate")?.textContent || "";
      return t.includes(`（${g}）`) && /召回|hit/.test(t);
    }, "0.4", { timeout: 30000 });
    r = { rows: await page.locator("#libSetProbeResult .lib-set-probe-hit").count(), gate: ((await page.locator("#libSetProbeGate").textContent()) || "").trim() };
    console.log("   切到严格 0.40 + 罕见号（自动重试）：" + r.gate + "（切档前：" + before + "）");
    check("换档位自动重试（严格档下罕见号仍能召回）", r.rows >= 1, r.gate);

    // Esc 关闭（且不应把下层管理弹窗一起关掉）
    await page.keyboard.press("Escape");
    await sleep(300);
    check("Esc 关闭设置弹窗", await page.locator("#libSetModal.hidden").count() > 0);
    check("下层管理弹窗仍开着（Esc 没穿透）", await page.locator("#kbModal.hidden").count() === 0);
  }
} finally {
  if (probeKbId) {
    try {
      const r = await page.request.post(BASE_URL + "/ag-ui/user/login", { data: { username: USERNAME, password: PASSWORD } });
      const t = (await r.json()).token;
      const del = await page.request.fetch(BASE_URL + "/ag-ui/kb/" + probeKbId, { method: "DELETE", headers: { Authorization: "Bearer " + t } });
      console.log((del.status() === 200 ? "清理：临时知识库已删除" : "清理：删除失败 " + del.status()));
    } catch { /* 清理失败不影响结论 */ }
  }
  await browser.close();
}
console.log("\n结果：" + (ok ? "全部通过" : "存在失败项"));
process.exit(ok ? 0 : 1);
