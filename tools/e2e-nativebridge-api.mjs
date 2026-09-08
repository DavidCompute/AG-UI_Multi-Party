// NativeBridge "login-to-connect / logout-to-disconnect" E2E (API level) against the
// temporary e2e instance http://localhost:5201 (fresh DB agui_e2e, mock agents).
// Usage: node tools/e2e-nativebridge-api.mjs
import { readFileSync } from "node:fs";
import { resolve } from "node:path";

const BASE = process.env.E2E_BASE || "http://localhost:5201";
const BRIDGE = "http://127.0.0.1:17321";
const MSI = resolve(process.env.E2E_MSI || "artifacts/nativebridge-wix/AguiGroupChat-NativeBridge-1.0.121-win-x64.msi");

let pass = 0, fail = 0;
function check(name, ok, extra) {
  if (ok) { pass++; console.log(`  PASS  ${name}${extra ? "  " + extra : ""}`); }
  else { fail++; console.log(`  FAIL  ${name}${extra ? "  " + extra : ""}`); }
}

async function api(method, path, { token, json, form } = {}) {
  const headers = {};
  if (token) headers.Authorization = `Bearer ${token}`;
  let body;
  if (form) body = form;
  else if (json !== undefined) { headers["Content-Type"] = "application/json"; body = JSON.stringify(json); }
  const res = await fetch(BASE + path, { method, headers, body });
  const data = await res.json().catch(() => null);
  return { status: res.status, data };
}
async function bridgeReq(method, path, json) {
  const headers = {};
  if (json !== undefined) headers["Content-Type"] = "application/json";
  const res = await fetch(BRIDGE + path, { method, headers, body: json !== undefined ? JSON.stringify(json) : undefined });
  const data = await res.json().catch(() => null);
  return { status: res.status, data };
}
// 先登录（已存在则复用），失败则注册（首次自举管理员）；返回整个响应体
async function obtainUser(username, password, nickname) {
  let r = await api("POST", "/ag-ui/user/login", { json: { username, password } });
  if (r.status === 200 && r.data?.token) return r.data;
  r = await api("POST", "/ag-ui/user/register", { json: { username, password, nickname } });
  if (r.status === 200 && r.data?.token) return r.data;
  throw new Error(`cannot login/register ${username}: ${r.status} ${JSON.stringify(r.data)}`);
}

console.log(`E2E NativeBridge login-connect flow  base=${BASE}`);
let r;

// ---------- 1. admin (first bootstrap) + normal user ----------
const admin = await obtainUser("e2e_admin", "E2eAdmin#123", "E2E管理员");
check("e2e_admin is admin", admin.platformRole === "superadmin" || admin.isAdmin === true, `role=${admin.platformRole}`);
const user = await obtainUser("e2e_user", "E2eUser#123", "E2E用户");
check("e2e_user is NOT admin", !(user.isAdmin === true) && user.platformRole !== "superadmin", `role=${user.platformRole}`);
const note = `setup:${user.userId}`;
const userToken = user.token;
const adminToken = admin.token;

// ---------- 2. upload MSI (admin) ----------
r = await api("GET", "/ag-ui/native-bridge/download/info", { token: adminToken });
check("info initially unavailable", r.status === 200 && r.data?.available === false, `available=${r.data?.available}`);
const form = new FormData();
form.append("file", new Blob([readFileSync(MSI)]), "AguiGroupChat-NativeBridge-1.0.121-win-x64.msi");
r = await api("POST", "/ag-ui/native-bridge/download/upload", { token: adminToken, form });
check("upload MSI", r.status === 200 && r.data?.uploaded === true, `status=${r.status}`);
r = await api("GET", "/ag-ui/native-bridge/download/info", { token: adminToken });
check("info shows package", r.status === 200 && r.data?.available === true && r.data?.kind === "msi", `file=${r.data?.fileName}`);

// ---------- 3. login-to-connect (browser-equivalent calls) ----------
r = await api("POST", "/ag-ui/native-bridge/download/setup-token", { token: userToken });
check("user claims setup-token", r.status === 200 && !!r.data?.setupToken, `status=${r.status}`);
check("setup server equals platform origin", r.data?.server === BASE, `server=${r.data?.server}`);
const setupToken = r.data.setupToken;

const b = await bridgeReq("GET", "/ag-ui/bridge/info");
check("bridge reachable & waiting", b.status === 200 && b.data?.configured === false, `configured=${b.data?.configured}`);

r = await bridgeReq("POST", "/ag-ui/bridge/setup", { server: BASE, setupToken });
check("bridge accepts setup", r.status === 200 && r.data?.accepted === true, `status=${r.status}`);
await new Promise((s) => setTimeout(s, 2500));
const b2 = await bridgeReq("GET", "/ag-ui/bridge/info");
check("bridge configured to platform", b2.data?.configured === true && b2.data?.server === BASE, `server=${b2.data?.server}`);
const bridgeClient = b2.data?.client;

r = await api("GET", "/ag-ui/native-bridge/download/tokens", { token: adminToken });
const mine = (r.data?.entries || []).find((e) => e.note === note);
check("admin sees user setup token bound", !!mine && mine.bound === true && !!mine.client, `note=${note} bound=${mine?.bound} client=${mine?.client}`);

// ---------- 4. logout-to-disconnect ----------
r = await api("POST", "/ag-ui/native-bridge/download/setup-token/revoke", { token: userToken });
check("user revoke on logout", r.status === 200 && r.data?.revoked >= 1, `revoked=${r.data?.revoked}`);
const t = await bridgeReq("POST", "/ag-ui/bridge/teardown");
check("bridge teardown accepted", t.status === 200 && t.data?.disconnected === true, `status=${t.status}`);
const b3 = await bridgeReq("GET", "/ag-ui/bridge/info");
check("bridge cleared after logout", b3.data?.configured === false && b3.data?.connected === false, `configured=${b3.data?.configured}`);

// 吊销后的旧令牌不能再连入隧道（HTTP 直达验证，401 立即返回不会挂长连）
const rev = await fetch(`${BASE}/ag-ui/native-tunnel/connect?agent=${encodeURIComponent("*")}&token=${encodeURIComponent(setupToken)}&client=${encodeURIComponent(bridgeClient || "x")}`);
check("revoked token refused by tunnel", rev.status === 401, `http=${rev.status}`);

// ---------- 5. re-login re-connects with a fresh token (同机复用 client) ----------
r = await api("POST", "/ag-ui/native-bridge/download/setup-token", { token: userToken });
check("re-login claims fresh setup-token", r.status === 200 && !!r.data?.setupToken, `status=${r.status}`);
const t2 = await bridgeReq("POST", "/ag-ui/bridge/setup", { server: BASE, setupToken: r.data.setupToken });
check("bridge accepts fresh setup", t2.status === 200 && t2.data?.accepted === true, `status=${t2.status}`);
await new Promise((s) => setTimeout(s, 2500));
r = await api("GET", "/ag-ui/native-bridge/download/tokens", { token: adminToken });
const mine2 = (r.data?.entries || []).find((e) => e.note === note);
check("fresh token re-bound to same machine", mine2?.bound === true && mine2?.client === bridgeClient, `client=${mine2?.client}`);

console.log(`\nRESULT: ${pass} passed, ${fail} failed`);
process.exit(fail ? 1 : 0);
