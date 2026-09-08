// NativeBridge browser E2E (Playwright/Chromium): login auto-connect + profile status + logout disconnect.
// Requires the temporary e2e instance on http://localhost:5201 (e2e_user registered, MSI uploaded)
// and a local NativeBridge running in waiting mode on 127.0.0.1:17321.
// Usage: node tools/e2e-nativebridge-browser.mjs
import { chromium } from "playwright";

const BASE = "http://localhost:5201";
const BRIDGE = "http://127.0.0.1:17321";
const USER = { username: "e2e_user", password: "E2eUser#123" };

let pass = 0, fail = 0;
function check(name, ok, extra) {
  if (ok) { pass++; console.log(`  PASS  ${name}${extra ? "  " + extra : ""}`); }
  else { fail++; console.log(`  FAIL  ${name}${extra ? "  " + extra : ""}`); }
}
const sleep = (ms) => new Promise((s) => setTimeout(s, ms));
async function bridgeInfo() {
  try { const r = await fetch(`${BRIDGE}/ag-ui/bridge/info`, { cache: "no-store" }); return r.ok ? await r.json() : null; }
  catch { return null; }
}
async function poll(fn, ms, step) {
  const end = Date.now() + ms;
  let v = null;
  while (Date.now() < end) { v = await fn(); if (v) return v; await sleep(step); }
  return v;
}

console.log(`Browser E2E NativeBridge  base=${BASE}`);

// 0. reset bridge to waiting state (clear any leftover config from earlier tests)
try { await fetch(`${BRIDGE}/ag-ui/bridge/teardown`, { method: "POST" }); } catch { /* ignore */ }
await sleep(1000);
const pre = await bridgeInfo();
check("bridge waiting before login", pre && pre.configured === false, `configured=${pre?.configured}`);

const browser = await chromium.launch();
try {
  const page = await (await browser.newContext()).newPage();
  const errs = [];
  const httpErrs = [];
  page.on("pageerror", (e) => errs.push(String(e)));
  page.on("console", (m) => {
    if (m.type() === "error") {
      const loc = m.location();
      errs.push((loc && loc.url ? loc.url + " " : "") + m.text());
    }
  });
  page.on("response", (r) => { if (r.status() >= 400) httpErrs.push(`${r.status()} ${r.url()}`); });

  // 1. login (login tab is the default)
  await page.goto(BASE, { waitUntil: "domcontentloaded" });
  await page.fill("#authUsername", USER.username);
  await page.fill("#authPassword", USER.password);
  await page.click("#authSubmit");
  await page.waitForSelector("#meChip:not(.hidden)", { timeout: 15000 });
  check("login succeeds", true);

  // mock 模型的 e2e 实例登录后会自动弹出“模型配置”窗：先关掉它，避免拦截后续点击
  const closeModelModal = async () => {
    for (let i = 0; i < 10; i++) {
      if (await page.isVisible("#modelConfigModal")) { await page.click("#mcCancel").catch(() => {}); await sleep(300); }
      else break;
    }
  };
  await closeModelModal();

  // 2. login auto-connects the local bridge to this platform (browser CORS/loopback path)
  const conn = await poll(async () => {
    const info = await bridgeInfo();
    return info && info.configured === true && info.server === BASE ? info : null;
  }, 20000, 800);
  check("login auto-configured bridge (browser)", !!conn, `server=${conn?.server} client=${conn?.client}`);

  // 3. profile modal shows connected state + MSI download
  await closeModelModal();
  await page.click("#meChip");
  await page.waitForSelector("#meMenuProfile:visible");
  await page.click("#meMenuProfile");
  await page.waitForSelector("#profileModal:not(.hidden)", { timeout: 5000 });
  await page.waitForFunction(() => {
    const el = document.getElementById("bridgeLocalState");
    return el && el.textContent.trim().length > 0;
  }, { timeout: 8000 });
  const stateText = (await page.textContent("#bridgeLocalState"))?.trim() || "";
  check("profile shows bridge connected", stateText.includes("已连接本平台"), stateText);
  await page.waitForSelector("#bridgePkgDownload:visible", { timeout: 8000 }).catch(() => {});
  const dlText = (await page.textContent("#bridgePkgDownload"))?.trim() || "";
  const dlMeta = (await page.textContent("#bridgePkgMeta"))?.trim() || "";
  check("profile shows MSI download", dlText.includes("下载本机桥") && dlMeta.includes(".msi"), dlMeta);
  await page.click("#pfCancel");

  // 4. logout disconnects the bridge
  await closeModelModal();
  await page.click("#meChip");
  await page.waitForSelector("#meMenuLogout:visible");
  await page.click("#meMenuLogout");
  await page.waitForSelector("#authForm:visible", { timeout: 8000 }).catch(() => {});
  const off = await poll(async () => {
    const info = await bridgeInfo();
    return info && info.configured === false ? info : null;
  }, 12000, 600);
  check("logout disconnected & cleared bridge", !!off, `configured=${off?.configured}`);
  // 已知良性噪音：登出过渡期一次既有的 /ag-ui/user/me 轮询会命中已失效令牌返回 401（会话过期处理路径）
  const unexpected = errs.filter((e) => !e.includes("/ag-ui/user/me"));
  check("no unexpected console/page errors during flow", unexpected.length === 0, unexpected.slice(0, 3).join(" | "));
} finally {
  await browser.close();
}

console.log(`\nRESULT: ${pass} passed, ${fail} failed`);
process.exit(fail ? 1 : 0);
