/* 知聚(KnowGath)前端：连接 Hub WebSocket，渲染协议事件 */
"use strict";

/** 后端错误本地化：优先按返回的 errorCode 查字典（err.<CODE>），未覆盖时回退后端 message，再回退未知。
 *  用法：errMsg(res); 或 errMsg(data?.code, data?.message);
 */
function errMsg(codeOrRes, fallback) {
  let code = codeOrRes;
  let msg = fallback;
  if (codeOrRes && typeof codeOrRes === "object") {
    code = codeOrRes.code ?? codeOrRes.errorCode;
    msg = codeOrRes.message || codeOrRes.detail;
  }
  // 优先展示后端具体原因（例如「引用的技能不存在于技能库：xxx」），避免被通用错误码文案掩盖
  if (msg) return msg;
  if (code) {
    const localized = t("err." + code);
    // t 找不到时会回显 key 本身（以 err. 开头），据此区分是否成功命中
    if (localized !== "err." + code) return localized;
  }
  return t("err.unknown", { code: code || "?" });
}

const state = {
  memberId: null,   // 当前身份（登录用户的 userId 或示例身份）
  token: null,      // 会话令牌（登录 / 注册后签发；示例身份为空）
  avatar: null,     // 当前用户头像 URL（顶栏头像 / 资料弹窗回显）
  personalMemoryEnabled: false, // 当前用户是否开启个人记忆（资料弹窗可改）
  isAdmin: false,   // 是否系统管理员（数据备份 / 模型配置等管理菜单仅管理员可见）
  replyTo: null,    // 引用回复目标 { id, sender, content }（输入框上方引用条）
  ws: null,
  reconnectDelay: 1000,
  groups: [],
  activeGroupId: null,
  activeTopicId: "main", // 当前知聚内话题（默认主话题）
  // groupId -> { messages: [], members: [], typing: Set<memberId> }
  rooms: new Map(),
  // messageId -> 消息对象（TEXT_MESSAGE_CONTENT 事件不含 groupId，需跨 room 按 id 定位）
  msgIndex: new Map(),
  subscribedGroups: new Set(), // 已订阅知聚 ID（重连后自动恢复订阅）
  mentions: new Set(),   // 当前输入选中（提及 / 私聊对象）
  mentionAll: false,
  // 按知聚未读信息（groupId → { lastMessageAt, unreadCount, byTopic: { topicId: n } }）：来自知聚列表 API，实时事件增量维护
  groupUnread: new Map(),
  // 按知聚记忆的 @ 选择（groupId → {ids, all}）：切知聚恢复、发送后保留，避免每次重新 @
  mentionMemory: new Map(),
  // 按知聚记忆的话题：groupId → 话题 ID（持久化 localStorage，切知聚/再登录自动恢复）
  topicMemory: new Map(),
  visibility: "all",
  // 应用内通知中心（5.4）：{ id, type, icon, title, body, groupId?, topicId?, ts, read }[]
  notifications: [],
  notifSeq: 0,
  notifPanelOpen: false,
  reconnectTimer: null, // 断线重连定时器（登出时需取消，防止登出后无限重连）
};

/* 全局 fetch 包装：已登录时自动携带 Authorization: Bearer（服务端强制令牌鉴权后，遗漏会 401） */
const _aguiFetch = window.fetch.bind(window);
window.fetch = (url, opts = {}) => {
  opts = opts || {};
  const headers = Object.assign({}, opts.headers || {});
  const hasAuth = Object.keys(headers).some((k) => k.toLowerCase() === "authorization");
  if (state && state.token && !hasAuth) headers["Authorization"] = `Bearer ${state.token}`;
  return _aguiFetch(url, Object.assign({}, opts, { headers }));
};

/** 顶栏用户头像：有头像显示图片，否则隐藏（保留昵称）。 */
function renderMeAvatar() {
  const el = $("meAvatar");
  if (!state.avatar) { el.classList.add("hidden"); return; }
  el.classList.remove("hidden");
  const img = el.querySelector("img");
  img.src = authedAssetUrl(state.avatar);
  img.onerror = () => { el.classList.add("hidden"); img.onerror = null; };
}

const $ = (id) => document.getElementById(id);

/** CSS 选择器转义（老环境缺失 CSS.escape 时兜底；消息 ID 为服务端生成的安全字符）。 */
function cssEsc(id) {
  return window.CSS?.escape ? CSS.escape(id) : String(id);
}

/* ============ 连接 ============ */

function connect() {
  if (!state.memberId) return; // 登出后不再重连
  const proto = location.protocol === "https:" ? "wss" : "ws";
  const query = `memberId=${encodeURIComponent(state.memberId)}${state.token ? `&token=${encodeURIComponent(state.token)}` : ""}`;
  const ws = new WebSocket(`${proto}://${location.host}/ws?${query}`);
  state.ws = ws;
  setStatus(false, "status.connecting");
  const firstConnect = !state.hadConnection;

  ws.onopen = () => { setStatus(true, "status.connected"); state.reconnectDelay = 1000; state.hadConnection = true; if (!firstConnect) addNotification("reconnect", t("notif.reconnected"), t("notif.reconnected.body")); };
  ws.onmessage = (e) => { try { handleEvent(JSON.parse(e.data)); } catch (err) { console.error("事件解析失败：长度 " + e.data.length + "，原因 " + (err && err.message)); } };
  ws.onclose = () => {
    setStatus(false, "status.disconnected");
    if (state.memberId && state.hadConnection) addNotification("reconnect", t("notif.disconnected"), t("notif.disconnected.body"));
    if (state.ws === ws) {
      state.reconnectTimer = setTimeout(connect, state.reconnectDelay);
      checkSession(); // 令牌可能已失效（如服务端重启），失效则引导重新登录
    }
    state.reconnectDelay = Math.min(state.reconnectDelay * 2, 10000);
  };
  ws.onerror = () => ws.close();
}

function send(payload) {
  if (state.ws && state.ws.readyState === WebSocket.OPEN) state.ws.send(JSON.stringify(payload));
}

/* ============ 成员目录 ============ */

// 回退目录：/ag-ui/users 无注册用户时（如示例身份模式）展示内置示例用户；数字员工从 /ag-ui/agents 拉取
const USER_DIRECTORY = [
  { memberId: "user_1001", nickname: "张三", memberType: "user", triggerMode: null, keywords: null },
  { memberId: "user_1002", nickname: "李四", memberType: "user", triggerMode: null, keywords: null },
];
let userDirectory = [];
let agentDirectory = [];
let selectedMembers = new Set();
// 待发送附件：{ file, uploading }，发送时先上传到 /ag-ui/upload 再随消息携带
let pendingAttachments = [];

async function loadUserDirectory() {
  try {
    const res = await fetch("/ag-ui/users");
    if (!res.ok) { userDirectory = []; return; }
    userDirectory = (await res.json()).map((u) => ({
      memberId: u.userId, nickname: u.nickname || u.username, memberType: "user",
      triggerMode: null, keywords: null,
    }));
  } catch { userDirectory = []; }
}

async function loadAgentDirectory() {
  try {
    const res = await fetch("/ag-ui/agents");
    if (!res.ok) return;
    agentDirectory = (await res.json()).map((a) => ({
      memberId: a.agentId, nickname: a.nickname, memberType: "agent",
      triggerMode: a.triggerMode || "mentioned", keywords: a.keywords || [],
      avatar: a.avatar || null,
      ownerId: a.ownerId || null,
    }));
  } catch { /* 目录加载失败不阻塞创建知聚 */ }
}

function memberDirectory() {
  const users = userDirectory.length ? userDirectory : USER_DIRECTORY;
  return [...users, ...agentDirectory];
}

/* ============ 界面风格（深色 / 浅色） ============ */

const THEME_KEY = "agui.theme";

/** 应用界面风格：data-theme 写在 <html> 上，两套 CSS 变量见 style.css；选择持久化 localStorage。 */
function applyTheme(theme) {
  const t = theme === "light" ? "light" : "dark";
  document.documentElement.dataset.theme = t;
  const btn = document.getElementById("themeBtn");
  if (btn) btn.textContent = t === "light" ? "🌙" : "☀️"; // 图标提示可切换到的模式
}
applyTheme(localStorage.getItem(THEME_KEY));

/* ============ 白标 / 品牌化（6.4）：应用名 + Logo + 主色 + 嵌入模式 ============ */

/** 品牌配置缓存（从 /ag-ui/settings/branding 拉取）。 */
let branding = { appName: "知聚(KnowGath)", logoUrl: null, primaryColor: "", forceDark: null, tagline: null };

/** 是否处于 iframe 嵌入 / 显式嵌入模式：压缩顶栏、隐藏无关操作。 */
const isEmbedMode = (() => {
  try {
    const explicit = new URLSearchParams(location.search).get("embed") === "1";
    return explicit || (window.self !== window.top);
  } catch { return false; }
})();
if (isEmbedMode) document.documentElement.classList.add("embed-mode");

/** 从十六进制主色派生强调色（亮 / 暗两套），经 CSS 变量覆盖默认主题。 */
function applyAccentFromHex(hex) {
  const root = document.documentElement;
  if (!hex || !/^#[0-9a-fA-F]{6}$/.test(hex)) {
    root.style.removeProperty("--accent");
    root.style.removeProperty("--accent-text");
    root.style.removeProperty("--agent");
    return;
  }
  const r = parseInt(hex.slice(1, 3), 16), g = parseInt(hex.slice(3, 5), 16), b = parseInt(hex.slice(5, 7), 16);
  // 由单一主色生成：深色模式用原色，浅色模式加深；派生浅色文字标 / 紫色数字员工色
  const light = document.documentElement.dataset.theme !== "light";
  const base = light
    ? `rgb(${r},${g},${b})`
    : `rgb(${Math.max(0, r - 40)},${Math.max(0, g - 40)},${Math.max(0, b - 40)})`;
  root.style.setProperty("--accent", base);
  root.style.setProperty("--accent-text", light
    ? `rgb(${Math.min(255, r + 130)},${Math.min(255, g + 130)},${Math.min(255, b + 130)})`
    : `rgb(${Math.max(0, r - 130)},${Math.max(0, g - 130)},${Math.max(0, b - 130)})`);
  root.style.setProperty("--agent", light
    ? `rgb(${Math.min(255, r + 60)},${Math.max(0, g - 40)},${Math.min(255, b + 40)})`
    : `rgb(${Math.max(0, r - 30)},${Math.max(0, g - 70)},${Math.max(0, b - 20)})`);
}

/** 应用品牌配置到页面：名称 / Logo / 主色 / 强制深色 / 副标语。 */
function applyBranding(br) {
  if (!br) br = { appName: null };
  branding = { appName: br.appName || t("brand.name"), logoUrl: br.logoUrl || null, primaryColor: br.primaryColor || "", forceDark: br.forceDark ?? null, tagline: br.tagline || null };
  const name = branding.appName;
  const setBrand = (id, sub) => {
    const nameEl = document.getElementById(id);
    if (nameEl) nameEl.textContent = name;
  };
  setBrand("brandName");
  setBrand("authBrandName");
  // Logo：安全 URL 才渲染（复用 safeUrl），否则回退文字图标
  const logoEl = document.getElementById("brandLogo");
  const authLogoEl = document.getElementById("authBrandLogo");
  const safeLogo = safeUrl(branding.logoUrl, true);
  if (safeLogo) {
    const src = escapeHtml(authedAssetUrl(safeLogo));
    logoEl.innerHTML = `<img src="${src}" alt="${escapeHtml(name)}" />`;
    authLogoEl.innerHTML = `<img src="${src}" alt="" />`;
  } else {
    logoEl.innerHTML = "💬 ";
    authLogoEl.innerHTML = "💬 ";
  }
  // 副标语 / 登录页 tagline
  const sub = document.getElementById("brandSub");
  if (sub) sub.textContent = branding.tagline || t("brand.sub");
  // 强制深色：嵌入 / 门户白标时锁定深色，隐藏主题切换
  if (branding.forceDark) {
    applyTheme("dark");
    const t = document.getElementById("themeBtn");
    if (t) t.classList.add("hidden");
  }
  applyAccentFromHex(branding.primaryColor);
}

/** 拉取并应用品牌配置（页面加载与登录后调用；失败静默用默认）。 */
async function loadBranding() {
  try {
    const res = await fetch("/ag-ui/settings/branding");
    if (res.ok) applyBranding(await res.json());
  } catch { /* 品牌拉取失败不影响主功能 */ }
}

/* ============ 登录 / 注册 / 登出 ============ */

const AUTH_KEY = "agui.auth";
let authMode = "login";

/** 会话持久化：始终写入 sessionStorage（标签页级）与 localStorage（跨刷新 / 重启的会话兜底）。
 * 注意：localStorage 也始终写入，是为避免某些环境（WebView / 隐私模式）刷新时 sessionStorage 被清
 * 导致「刷新就自动退出登录」。登出时 clearAuth() 会同时清掉两者。 */
function storeAuth(auth) {
  try { sessionStorage.setItem(AUTH_KEY, JSON.stringify(auth)); } catch { /* 存储不可用忽略 */ }
  try { localStorage.setItem(AUTH_KEY, JSON.stringify(auth)); } catch { /* 存储不可用忽略 */ }
}

/** 读取会话：优先 sessionStorage（当前标签页），其次 localStorage（保持登录）。 */
function readAuth() {
  try { const s = sessionStorage.getItem(AUTH_KEY); if (s) return JSON.parse(s); } catch {}
  try { const l = localStorage.getItem(AUTH_KEY); if (l) return JSON.parse(l); } catch {}
  return null;
}

function clearAuth() { sessionStorage.removeItem(AUTH_KEY); localStorage.removeItem(AUTH_KEY); }

/** 昵称变更后同步到两处存储的会话快照。 */
function updateAuthNickname(nickname) {
  for (const store of [sessionStorage, localStorage]) {
    try {
      const saved = JSON.parse(store.getItem(AUTH_KEY) || "{}");
      if (saved.memberId) store.setItem(AUTH_KEY, JSON.stringify({ ...saved, nickname }));
    } catch {}
  }
}

function showAuth() { $("authOverlay").classList.remove("hidden"); }
function hideAuth() { $("authOverlay").classList.add("hidden"); }

function setAuthMode(mode) {
  authMode = mode;
  $("authTabLogin").classList.toggle("on", mode === "login");
  $("authTabRegister").classList.toggle("on", mode === "register");
  $("authNickname").classList.toggle("hidden", mode === "login");
  $("authSubmit").textContent = mode === "login" ? t("auth.login") : t("auth.register");
  $("authError").textContent = "";
}

async function submitAuth(e) {
  e.preventDefault();
  const username = $("authUsername").value.trim();
  const password = $("authPassword").value;
  if (!username || !password) { $("authError").textContent = t("auth.err.required"); return; }
  const body = authMode === "register"
    ? { username, password, nickname: $("authNickname").value.trim() || null }
    : { username, password };
  try {
    const res = await fetch(`/ag-ui/user/${authMode}`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(body),
    });
    const data = await res.json().catch(() => null);
    if (!res.ok) { $("authError").textContent = errMsg(data?.code, data?.message); return; }
    enterApp(data);
  } catch (ex) {
    $("authError").textContent = t("auth.err.network"); // 固定文案，异常细节只进 console 不暴露到界面
    console.warn("登录/注册请求异常：", ex && ex.message);
  }
}

function enterApp(data) {
  state.memberId = data.userId;
  state.token = data.token;
  state.avatar = data.avatar || null;
  state.personalMemoryEnabled = !!data.personalMemoryEnabled;
  state.isAdmin = !!data.isAdmin; // 系统管理员：数据备份 / 模型配置等管理菜单仅管理员可见
  storeAuth({ memberId: data.userId, token: data.token, nickname: data.nickname });
  state.topicMemory = loadTopicMemory(data.userId); // 恢复该用户的话题记忆
  pendingAutoEnterGroup = true; // 知聚列表加载完成后自动进入上次选择的知聚
  hideAuth();
  // 尽早建立 WebSocket：不依赖下方任何渲染，避免渲染异常阻断连接导致在线状态停留在 Offline
  connect();
  checkModelConfig(); // 未配置过 DeepSeek 模型 → 自动弹出配置界面
  try {
    $("meChip").classList.remove("hidden");
    $("meNickname").textContent = data.nickname || data.userId;
    // 管理菜单（数据备份 / 模型配置）仅管理员显示
    $("meMenuBackup").classList.toggle("hidden", !state.isAdmin);
    $("meMenuModelConfig").classList.toggle("hidden", !state.isAdmin);
    $("meMenuAdmin").classList.toggle("hidden", !state.isAdmin);   // 用户管理（仅管理员）
    $("meMenuStatus").classList.toggle("hidden", !state.isAdmin);  // 系统状态（仅管理员）
    $("meMenuBranding").classList.toggle("hidden", !state.isAdmin); // 白标品牌（仅管理员）
    renderMeAvatar();
    applyChatResizer(); // 恢复该用户上次拖拽的聊天区高度
    resetChatState();
  } catch (e) { console.error("进入会话后的界面渲染出错（不影响连接）", e); }
  loadUserDirectory();
  loadGroups();
}

function resetChatState() {
  state.rooms.clear();
  state.msgIndex.clear();
  state.subscribedGroups.clear();
  state.groups = [];
  state.activeGroupId = null;
  state.activeTopicId = "main";
  state.replyTo = null; // 切知聚 / 重置时清除引用
  renderReplyBar();
  $("addMemberBtn").disabled = true;
  $("groupSettingsBtn").disabled = true;
  $("searchBtn").disabled = true;
  $("discussBtn").disabled = true;
  resetVScroll(); renderGroupList(); renderMembers(); renderTopicBar();
  // 登出 / 切换身份：取消断线重连定时器；清理输入区残留（@ 选择 / @ 全体 / 草稿 / 待发送附件），防跨账号残留
  if (state.reconnectTimer) { clearTimeout(state.reconnectTimer); state.reconnectTimer = null; }
  state.mentions = new Set();
  state.mentionAll = false;
  state.mentionMemory = new Map();
  state.groupUnread.clear();
  pendingAttachments = [];
  state.visibility = "all";
  // 通知中心（5.4）：登出清空通知，避免跨账号残留
  state.notifications = [];
  state.hadConnection = false;
  hideNotifPanel();
  if ($("notifBadge")) renderNotifications();
  const input = $("input"); if (input) input.value = "";
  // 富媒体（5.2）：登出 / 切换身份时停止录音、关闭画布，避免残留麦克风占用
  stopVoiceRecording();
  closeCanvasModal();
  hideMentionPicker();
  renderMentionChips();
  renderAttachList();
}

async function logout() {
  if (state.token) {
    try {
      await fetch("/ag-ui/user/logout", { method: "POST", headers: { Authorization: `Bearer ${state.token}` } });
    } catch { /* 登出失败不阻塞本地清理 */ }
  }
  if (state.reconnectTimer) { clearTimeout(state.reconnectTimer); state.reconnectTimer = null; } // 登出后禁止继续重连
  stopKbPolling(); // 知识库轮询随登出停止，避免跨账号残留定时器
  if (state.ws) state.ws.close();
  state.ws = null;
  state.token = null;
  state.memberId = null;
  state.avatar = null;
  state.isAdmin = false;
  pendingAutoEnterGroup = false;
  clearAuth(); // 同时清除 sessionStorage 与 localStorage（保持登录状态随退出登录失效）
  try {
    resetChatState();
  } catch (e) { console.error("登出清理出错（已罗到登录页）", e); }
  $("meChip") && $("meChip").classList.add("hidden");
  state.visibility = "all";
  state.mentionAll = false;
  state.mentions = new Set();
  showAuth();
}

// 刷新页面后恢复会话：先校验令牌有效性，无效则回到登录页；保持登录状态下跨浏览器重启仍可恢复
async function tryRestoreSession() {
  const auth = readAuth();
  if (!auth?.memberId || !auth?.token) { showAuth(); return; }
  try {
    const res = await fetch("/ag-ui/user/me", { headers: { Authorization: `Bearer ${auth.token}` } });
    if (!res.ok) { clearAuth(); showAuth(); return; } // 401（令牌失效）才清登录态
    const me = await res.json();
    // 恢复会话时必须带上 isAdmin（/me 返回该字段）：否则管理菜单（备份 / 模型配置 / 用户管理 / 系统状态）会被隐藏
    enterApp({ userId: me.userId, token: auth.token, nickname: me.nickname || me.username, avatar: me.avatar || null, personalMemoryEnabled: !!me.personalMemoryEnabled, isAdmin: !!me.isAdmin });
  } catch {
    // 网络异常（断网 / 后端未就绪 / WebView 刚加载）：自动重试一次，避免启动或瞬时抖动导致误退回登录页；
    // 重试后仍失败才回到登录页（保留 token：keep-login 不销毁，恢复后刷新仍可自动登录）
    try {
      await new Promise((r) => setTimeout(r, 1200));
      const retry = await fetch("/ag-ui/user/me", { headers: { Authorization: `Bearer ${auth.token}` } });
      if (!retry.ok) { clearAuth(); showAuth(); return; }
      const me = await retry.json();
      enterApp({ userId: me.userId, token: auth.token, nickname: me.nickname || me.username, avatar: me.avatar || null, personalMemoryEnabled: !!me.personalMemoryEnabled, isAdmin: !!me.isAdmin });
    } catch {
      showAuth();
      $("authError").textContent = t("auth.err.restoreNetwork");
    }
  }
}

// ============ 模型配置（DeepSeek endpoint / apiKey）============

/** 打开模型配置弹窗：预填当前 endpoint；apiKey 不回显（仅提示是否已配置）。 */
async function openModelConfigModal() {
  try {
    const res = await fetch("/ag-ui/settings/model", { headers: { Authorization: "Bearer " + (state.token || "") } });
    const data = await res.json().catch(() => null);
    if (res.ok && data) modelConfigData = data;
  } catch { /* 网络异常忽略，使用缓存值 */ }
  $("mcEndpoint").value = modelConfigData?.endpoint || "";
  $("mcApiKey").value = "";
  $("mcApiKeyState").textContent = modelConfigData?.apiKeyConfigured ? t("mc.apiKeyConfigured") : "";
  $("mcThinking").checked = modelConfigData?.thinkingMode !== false; // 默认开启
  $("modelConfigModal").classList.remove("hidden");
}

let modelConfigData = null;

/** 登录后检查：未配置过模型 → 自动弹出配置界面（endpoint / apiKey 可留空，留空用默认与环境变量）。 */
async function checkModelConfig() {
  if (!state.token) return;
  try {
    const res = await fetch("/ag-ui/settings/model", { headers: { Authorization: "Bearer " + state.token } });
    const data = await res.json().catch(() => null);
    if (res.ok && data && !data.configured) {
      modelConfigData = data;
      openModelConfigModal();
    }
  } catch { /* 检查失败不阻塞进入 */ }
}

// ============ 记忆管理（分知聚分级 / 自动遗忘 / 可视化） ============

let memOffset = 0;
const MEM_PAGE = 30;

/** 发送者显示名：优先服务端解析的昵称（m.senderNickname），回退本地知聚成员昵称 / 原始 ID。 */
function memorySenderName(m) {
  if (m.senderNickname) return m.senderNickname;
  const id = m.senderId ?? m;
  for (const g of state.groups || []) {
    const r = room(g.groupId);
    const mm = r?.members.find((x) => x.memberId === id);
    if (mm?.nickname) return mm.nickname;
  }
  return id;
}

/** 打开记忆管理弹窗：加载知聚统计 + 首屏条目。 */
async function openMemoryModal() {
  $("memGroupSelect").value = "";
  $("memKeyword").value = "";
  $("memForgetPanel").classList.add("hidden");
  $("memForgetHint").classList.toggle("hidden", !!state.isAdmin); // 非管理员提示遗忘仅作用于自己的记忆
  $("memoryModal").classList.remove("hidden");
  await loadMemoryGroups();
  await loadMemoryList(0);
}

/* ============ 白标 / 品牌化（6.4）弹窗：应用名 / Logo / 主色 / 强制深色 ============ */

function openBrandingModal() {
  $("brName").value = branding.appName === "知聚(KnowGath)" ? "" : branding.appName;
  $("brLogo").value = branding.logoUrl || "";
  $("brColor").value = branding.primaryColor || "#4f8cff";
  $("brTagline").value = branding.tagline || "";
  $("brForceDark").checked = !!branding.forceDark;
  $("brandModal").classList.remove("hidden");
}

async function saveBranding() {
  const body = {
    appName: $("brName").value.trim(),
    logoUrl: $("brLogo").value.trim() || null,
    primaryColor: $("brColor").value || null,
    tagline: $("brTagline").value.trim() || null,
    forceDark: $("brForceDark").checked || null,
  };
  const btn = $("brSave");
  const orig = btn.textContent;
  btn.disabled = true; btn.textContent = t("common.saving");
  try {
    const res = await fetch("/ag-ui/settings/branding", {
      method: "POST",
      headers: { "Content-Type": "application/json", Authorization: "Bearer " + (state.token || "") },
      body: JSON.stringify(body),
    });
    const data = await res.json().catch(() => null);
    if (!res.ok) { toast(errMsg(data, t("common.saveFail", { err: res.status }))); return; }
    applyBranding(await (await fetch("/ag-ui/settings/branding")).json());
    toast(t("brand.saved"));
    $("brandModal").classList.add("hidden");
  } catch (ex) {
    toast(t("common.saveFail", { err: ex.message }));
  } finally {
    btn.disabled = false; btn.textContent = orig;
  }
}

/** 各知聚记忆统计 → 知聚选择器 + 总条数。 */
async function loadMemoryGroups() {
  try {
    const res = await fetch("/ag-ui/memory/groups", { headers: { Authorization: "Bearer " + (state.token || "") } });
    const data = await res.json().catch(() => null);
    if (!res.ok || !Array.isArray(data)) { $("memTotal").textContent = ""; return; }
    const total = data.reduce((s, g) => s + (Number(g.count) || 0), 0); // 数值归一，防字符串拼接 / 注入
    $("memTotal").textContent = t("memory.totalCount", { count: total });
    const sel = $("memGroupSelect");
    const current = sel.value;
    sel.innerHTML = `<option value="">${t("memory.allGroupsCount", { count: total })}</option>`
      + data.map((g) => {
          const count = Number(g.count) || 0; // 服务端数值先 Number 归一再入 HTML
          const expiredCount = Number(g.expiredCount) || 0;
          const expired = expiredCount ? t("memory.expiredCount", { count: escapeHtml(expiredCount) }) : "";
          return `<option value="${escapeHtml(g.groupId)}" ${g.groupId === current ? "selected" : ""}>${t("memory.groupOption", { name: escapeHtml(g.groupName), count: escapeHtml(count), expired })}</option>`;
        }).join("");
  } catch { /* 忽略 */ }
}

/** 加载记忆条目列表（按当前知聚 / 关键词 / 分页）。 */
async function loadMemoryList(offset = memOffset) {
  memOffset = offset;
  const groupId = $("memGroupSelect").value;
  const keyword = $("memKeyword").value.trim();
  const params = new URLSearchParams({ limit: String(MEM_PAGE), offset: String(offset) });
  if (groupId) params.set("groupId", groupId);
  if (keyword) params.set("keyword", keyword);
  const list = $("memList");
  list.innerHTML = `<div class="mem-empty">${t("memory.loading")}</div>`;
  try {
    const res = await fetch("/ag-ui/memory?" + params.toString(), { headers: { Authorization: "Bearer " + (state.token || "") } });
    const data = await res.json().catch(() => null);
    if (!res.ok || !data) { list.innerHTML = `<div class="mem-empty">${t("memory.loadFail")}</div>`; return; }
    const items = data.items || [];
    if (!items.length) { list.innerHTML = `<div class="mem-empty">${keyword || groupId ? t("memory.noMatch") : t("memory.empty")}</div>`; $("memPager").innerHTML = ""; return; }
    list.innerHTML = items.map((m) => {
      const impN = Number(m.importance); // 服务端数值强制转整型，防 class 属性注入
      const impKey = Number.isInteger(impN) && impN >= 0 ? impN : 0;
      const imp = [t("memory.level0"), t("memory.level1"), t("memory.level2")][impKey] || t("memory.level0");
      const expired = m.expiresAt && m.expiresAt <= Date.now();
      return `<div class="memory-item ${expired ? "expired" : ""}">
        <div class="mem-head">
          <span class="mem-badge imp-${impKey}">${imp}</span>
          <span class="mem-sender">${escapeHtml(memorySenderName(m))}</span>
          <span class="mem-time">${new Date(m.timestamp).toLocaleString()}</span>
          ${expired ? `<span class="mem-expired">${t("memory.expired")}</span>` : ""}
        </div>
        <div class="mem-content">${escapeHtml(m.content)}</div>
        ${m.canManage ? `<div class="mem-ops">
          <select class="mem-imp" data-mid="${escapeHtml(m.messageId)}" title="${t("memory.levelTitle")}">
            <option value="0" ${impN === 0 ? "selected" : ""}>${t("memory.level0")}</option>
            <option value="1" ${impN === 1 ? "selected" : ""}>${t("memory.level1")}</option>
            <option value="2" ${impN === 2 ? "selected" : ""}>${t("memory.level2")}</option>
          </select>
          <button class="mem-del" data-mid="${escapeHtml(m.messageId)}" title="${t("memory.delTitle")}">${t("memory.delBtn")}</button>
        </div>` : '<div class="mem-ops muted">' + t("memory.onlyOwner") + '</div>'}
      </div>`;
    }).join("");
    // 分页
    const total = Number(data.total) || 0;
    const pages = Math.max(1, Math.ceil(total / MEM_PAGE));
    const cur = Math.floor(offset / MEM_PAGE) + 1;
    $("memPager").innerHTML = `${t("memory.pager", { total, cur, pages })}
      <button class="chip-btn" id="memPrev" ${offset <= 0 ? "disabled" : ""}>${t("memory.prevPage")}</button>
      <button class="chip-btn" id="memNext" ${offset + MEM_PAGE >= total ? "disabled" : ""}>${t("memory.nextPage")}</button>`;
    const prev = $("memPrev");
    const next = $("memNext");
    if (prev) prev.onclick = () => loadMemoryList(Math.max(0, memOffset - MEM_PAGE));
    if (next) next.onclick = () => loadMemoryList(memOffset + MEM_PAGE);
    // 条目操作
    list.querySelectorAll(".mem-imp").forEach((sel) => {
      sel.onchange = async () => {
        const res = await fetch(`/ag-ui/memory/${encodeURIComponent(sel.dataset.mid)}/importance`, {
          method: "POST",
          headers: { "Content-Type": "application/json", Authorization: "Bearer " + (state.token || "") },
          body: JSON.stringify({ importance: Number(sel.value) }),
        });
        toast(res.ok ? t("memory.levelUpdated") : t("memory.updateFail"));
        loadMemoryList(memOffset);
      };
    });
    list.querySelectorAll(".mem-del").forEach((btn) => {
      btn.onclick = async () => {
        if (!confirm(t("memory.delConfirm"))) return;
        const res = await fetch(`/ag-ui/memory/${encodeURIComponent(btn.dataset.mid)}`, {
          method: "DELETE",
          headers: { Authorization: "Bearer " + (state.token || "") },
        });
        toast(res.ok ? t("memory.deleted") : t("memory.delFail"));
        loadMemoryList(memOffset);
        loadMemoryGroups();
      };
    });
  } catch { list.innerHTML = `<div class="mem-empty">${t("memory.loadFail")}</div>`; }
}

let lastAuthCheck = 0;

// 令牌失效兜底：会话为服务端内存态（重启即失效），重连失败时校验 /me，401 则回到登录页
async function checkSession() {
  if (!state.token) return;
  const now = Date.now();
  if (now - lastAuthCheck < 5000) return; // 节流：避免每个重连周期都打请求
  lastAuthCheck = now;
  try {
    const res = await fetch("/ag-ui/user/me", { headers: { Authorization: `Bearer ${state.token}` } });
    if (!res.ok) { toast(t("auth.err.sessionExpired")); logout(); }
  } catch { /* 网络异常时静默，继续重连 */ }
}

/* ============ 知聚 / 话题记忆（本地持久化，按用户隔离） ============ */

let pendingAutoEnterGroup = false; // 登录后知聚列表加载完成时自动进入上次选择的知聚（一次性）

const LastGroupKey = (uid) => "agui.lastGroup." + uid;
const TopicMemKey = (uid) => "agui.topicMem." + uid;

/** 读取该用户的话题记忆（groupId → topicId）。 */
function loadTopicMemory(uid) {
  const map = new Map();
  try {
    const raw = localStorage.getItem(TopicMemKey(uid));
    if (raw) {
      for (const [gid, topicId] of Object.entries(JSON.parse(raw))) {
        if (gid && topicId) map.set(gid, topicId);
      }
    }
  } catch {}
  return map;
}

function saveTopicMemory(uid) {
  try { localStorage.setItem(TopicMemKey(uid), JSON.stringify(Object.fromEntries(state.topicMemory))); } catch {}
}

/* ============ 数字员工管理（运行时可新增 / 编辑 / 删除 AI 角色） ============ */

const TRIGGER_LABELS = {
  mentioned: t("agent.form.trigger.mentioned"), allMessages: t("agent.form.trigger.allMessages"), keyword: t("agent.form.trigger.keyword"), contextual: t("agent.form.trigger.contextual"),
};
// 成员列表用紧凑图标代替文字标签；inherit = 跟随角色默认
const TRIGGER_ICONS = {
  inherit: "◎", mentioned: "@", allMessages: "👁", keyword: "#", contextual: "🧠",
};
const TRIGGER_HINTS = {
  mentioned: t("agent.form.trigger.mentioned.hint"),
  allMessages: t("agent.form.trigger.allMessages.hint"),
  keyword: t("agent.form.trigger.keyword.hint"),
  contextual: t("agent.form.trigger.contextual.hint"),
};
let agentList = [];
let editingAgentId = null;

async function openAgentModal() {
  if (!state.token) { toast(t("agent.err.loginRequired")); return; }
  // 一并刷新用户目录：创建者列优先显示昵称（别名），避免因目录未加载 / 已过期而回退到原始 ID
  await Promise.all([loadAgents(), loadKbs(), loadUserDirectory()]);
  $("agentModal").classList.remove("hidden");
  showAgentListView();
}

function showAgentListView() {
  $("agentListView").classList.remove("hidden");
  $("agentFormView").classList.add("hidden");
}

async function loadAgents() {
  try {
    const res = await fetch("/ag-ui/agents");
    if (!res.ok) { agentList = []; return; }
    agentList = (await res.json()).map((a) => ({ ...a, ownerId: a.ownerId || null }));
    renderAgentList();
  } catch { agentList = []; }
}

/** 数字员工创建者显示名：本人显示「我」，否则从用户目录取昵称，查不到则显示 ID 前段。内置数字员工（无 ownerId）显示「系统」。 */
function agentOwnerName(ownerId) {
  if (!ownerId) return `<span class="agent-owner sys">${escapeHtml(t("agent.ownerSystem"))}</span>`;
  if (ownerId === state.memberId) return `<span class="agent-owner me">${escapeHtml(t("agent.ownerMe"))}</span>`;
  const u = userDirectory.find((x) => x.memberId === ownerId);
  if (u?.nickname) return `<span class="agent-owner">${escapeHtml(u.nickname)}</span>`;
  return `<span class="agent-owner" title="${escapeHtml(ownerId)}">${escapeHtml(ownerId.length > 14 ? ownerId.slice(0, 14) + "…" : ownerId)}</span>`;
}

function renderAgentList() {
  const el = $("agentList");
  const keyword = ($("agentSearch").value || "").trim().toLowerCase();
  const filtered = agentList.filter((a) =>
    !keyword
    || (a.nickname || "").toLowerCase().includes(keyword)
    || (a.agentId || "").toLowerCase().includes(keyword)
    || (a.description || "").toLowerCase().includes(keyword));

  $("agentCount").textContent = agentList.length ? t("agent.count", { count: agentList.length }) : "";
  el.innerHTML = "";

  if (!agentList.length) {
    const empty = document.createElement("div");
    empty.className = "agent-empty";
    empty.innerHTML = `<div class="agent-empty-icon">🤖</div>
      <div>${t("agent.empty")}</div>`;
    el.appendChild(empty);
    return;
  }
  if (!filtered.length) {
    el.innerHTML = `<div class="agent-empty"><div class="agent-empty-icon">🔍</div><div>${t("agent.noMatch", { kw: escapeHtml(keyword) })}</div></div>`;
    return;
  }

  for (const a of filtered) {
    const row = document.createElement("div");
    row.className = "agent-row";
    const kw = (a.keywords || []).join("、");
    // 编辑 / 删除：创建者本人 或 系统管理员（内置数字员工 ownerId 为 null，不可改）
    const canManage = !!a.ownerId && (state.isAdmin || a.ownerId === state.memberId);
    const avatarImg = a.avatar
      ? `<img class="agent-avatar" src="${escapeHtml(authedAssetUrl(a.avatar))}" alt="" onerror="this.remove()" />`
      : "";
    row.innerHTML = `
      <div class="agent-cell">
        <div class="agent-name">${avatarImg}<b>${escapeHtml(a.nickname)}</b><span class="tag-agent">AI</span>${a.isPrivate ? `<span class="tag-lock" title="${escapeHtml(t("agent.privateTip"))}">🔒</span>` : ""}${(a.skills || []).length ? `<span class="tag-skill" title="${escapeHtml(t("agent.skillsTip", { ids: (a.skills || []).map((s) => s.skillId).join(", ") }))}">🧩 ${a.skills.length}</span>` : ""}${(a.knowledgeBaseIds || []).length ? `<span class="tag-skill" title="${escapeHtml(t("agent.kbTip", { ids: a.knowledgeBaseIds.join(", ") }))}">📚 ${a.knowledgeBaseIds.length}</span>` : ""}</div>
        <div class="agent-desc">${escapeHtml(a.description || "—")}</div>
      </div>
      <div class="agent-cell agent-cell-id"><code>${escapeHtml(a.agentId)}</code></div>
      <div class="agent-cell">
        <span class="tag-mode">${escapeHtml(TRIGGER_LABELS[a.triggerMode] || a.triggerMode)}</span>
        ${a.bridgeEndpoint ? `<span class="tag-bridge" title="${escapeHtml(a.bridgeEndpoint)}">${escapeHtml(t("agent.bridgeTag"))}</span>` : ""}
        ${kw ? `<div class="agent-desc">${escapeHtml(t("agent.keywordsLabel", { kw }))}</div>` : ""}
      </div>
      <div class="agent-cell agent-cell-model">${a.model ? escapeHtml(a.model) : `<span class="muted">${escapeHtml(t("agent.defaultModel"))}</span>`}</div>
      <div class="agent-cell agent-cell-owner">${agentOwnerName(a.ownerId)}</div>
      <div class="agent-cell agent-op-col">
        <button class="icon-btn" data-act="export" title="${escapeHtml(t("agent.exportTip"))}">📤</button>
        ${canManage ? `<button class="icon-btn" data-act="edit" title="${escapeHtml(t("agent.edit"))}">✏️</button><button class="icon-btn danger" data-act="del" title="${escapeHtml(t("agent.del"))}">🗑️</button>` : ""}
      </div>`;
    row.querySelector('[data-act="export"]').onclick = () => exportAgents([a], `${a.agentId}.json`);
    if (canManage) {
      row.querySelector('[data-act="edit"]').onclick = () => openAgentForm(a.agentId);
      row.querySelector('[data-act="del"]').onclick = (e) => confirmDeleteAgent(a, e.currentTarget);
    }
    el.appendChild(row);
  }
}

/* ============ 数字员工组织架构图（图形化编辑任务指派 / 问题提升；端口拖拽连线） ============ */

/** 组织架构画布状态。 */
let orgState = null;

/** 组织架构节点布局持久化键（按用户隔离：节点横纵坐标是查看者偏好，存浏览器，不写服务端）。 */
const ORG_LAYOUT_KEY = "agui.orgLayout";
function orgLayoutKey() { return ORG_LAYOUT_KEY + "." + (state.memberId || ""); }
/** 读取上次保存的节点坐标（仅当前用户）；无则返回空对象。 */
function loadOrgPositions() {
  try {
    const raw = localStorage.getItem(orgLayoutKey());
    if (raw) return JSON.parse(raw);
  } catch { /* 存储不可用忽略 */ }
  return {};
}
/** 保存当前所有节点坐标到浏览器（仅当前用户）。 */
function saveOrgPositions() {
  if (!orgState || !orgState.nodes) return;
  const map = {};
  orgState.nodes.forEach((n) => { if (typeof n.x === "number" && typeof n.y === "number") map[n.agentId] = { x: n.x, y: n.y }; });
  try { localStorage.setItem(orgLayoutKey(), JSON.stringify(map)); } catch { /* 存储不可用忽略 */ }
}

/** 事件坐标 → 画布本地坐标（与节点 x/y 同坐标系）。 */
function orgCanvasPoint(e) {
  const rect = $("orgCanvas").getBoundingClientRect();
  return { x: e.clientX - rect.left, y: e.clientY - rect.top };
}

/** 自动布局：纵向层级树（调度/提升关系自上而下），一对多时多个子节点横向并排。
 *  上级边 = 其他指向自己的指派 + 自己的提升目标；父子相邻（子在下并排），父节点居于子聚中间。
 *  纯树/森林走 tidy 布局；遇多父共享或环（非树）则兑为按行分组横向排列。 */
function orgAutoLayout(nodes, byId) {
  const ids = new Set(nodes.map((n) => n.agentId));

  // 有向边（上级→下级）：指派（源=上级） + 提升（提升目标=上级）
  const parents = {}, children = {};
  const addEdge = (p, c) => {
    if (!ids.has(p) || !ids.has(c) || p === c) return;
    (parents[c] || (parents[c] = new Set())).add(p);
    (children[p] || (children[p] = new Set())).add(c);
  };
  nodes.forEach((n) => {
    (n.assignmentIds || []).forEach((t) => addEdge(n.agentId, t));
    if (n.escalationAgentId && n.escalationAgentId !== n.agentId) addEdge(n.escalationAgentId, n.agentId);
  });

  // 是否为树/森林（每个节点至多一个父）
  let isForest = true;
  nodes.forEach((n) => { const ps = parents[n.agentId]; if (ps && ps.size > 1) isForest = false; });

  const X_GAP = 210, Y_GAP = 130, PAD = 40;

  // ---- 兑底：按“到根节点的末端深度”分组；同组横向依次排开 ----
  function layoutByDepth() {
    const depth = {};
    const visiting = new Set();
    const depOf = (id) => {
      if (id in depth) return depth[id];
      if (visiting.has(id)) { depth[id] = 0; return 0; }   // 环保护
      visiting.add(id);
      const ps = parents[id];
      let d = 0;
      if (ps) ps.forEach((p) => { d = Math.max(d, 1 + depOf(p)); });
      visiting.delete(id);
      depth[id] = d;
      return d;
    };
    nodes.forEach((n) => depOf(n.agentId));
    const rows = new Map();
    nodes.forEach((n) => {
      const d = depth[n.agentId]; if (!rows.has(d)) rows.set(d, []); rows.get(d).push(n.agentId);
    });
    const pos = {};
    // 同组内按父节点所在列居中聚类（父多的靠左，父列近似则按昵称）
    const colIndex = {}; nodes.forEach((n, i) => { colIndex[n.agentId] = i; });
    [...rows.entries()].sort((p, q) => p[0] - q[0]).forEach(([d, list]) => {
      list.sort((a, b) => {
        const ap = [...(parents[a] || [])].map((p) => colIndex[p] ?? 1e9);
        const bp = [...(parents[b] || [])].map((p) => colIndex[p] ?? 1e9);
        const minA = Math.min(...ap), minB = Math.min(...bp);
        return (minA - minB) || byId[a].nickname.localeCompare(byId[b].nickname);
      });
      list.forEach((id, i) => { pos[id] = { x: PAD + i * X_GAP, y: PAD + d * Y_GAP }; });
    });
    return pos;
  }

  if (!isForest) return layoutByDepth();

  // ---- tidy 树 / 森林：先序遍历分配列，父节点居于其子聚中点，同层子节点横向并排 ----
  const width = {};
  const widthOf = (id) => {
    if (id in width) return width[id];
    const cs = children[id];
    let w = 0;
    if (cs && cs.size) cs.forEach((c) => { w += widthOf(c); });
    else w = 1;
    width[id] = w;
    return w;
  };

  const pos = {};
  let cursor = 0;
  const place = (id, row) => {
    const w = widthOf(id);
    const cs = children[id];
    if (!cs || cs.size === 0) {
      pos[id] = { x: PAD + cursor * X_GAP, y: PAD + row * Y_GAP };
      cursor += 1;
      return;
    }
    const start = cursor;
    [...cs].forEach((c) => place(c, row + 1));
    // 父节点列 = 其子聚的中间列格子（偶数子时取中间空隙，仍不与不同行冲突）
    const mid = start + (w - 1) / 2;
    pos[id] = { x: PAD + mid * X_GAP, y: PAD + row * Y_GAP };
  };

  const roots = nodes.filter((n) => !parents[n.agentId] || parents[n.agentId].size === 0);
  if (roots.length === 0) return layoutByDepth();          // 全有父（成环）
  roots.forEach((r) => place(r.agentId, 0));
  return pos;
}

function openOrgChart() {
  const agents = (agentList || []).filter((a) => !a.isSkillTarget);
  const el = $("orgCanvas");
  el.innerHTML = "";
  const svg = $("orgSvg");
  svg.innerHTML = "";
  const NS = "http://www.w3.org/2000/svg";

  // 节点数据（不含 DOM，先建模型再做布局与 DOM）
  const nodes = agents.map((a) => ({
    agentId: a.agentId,
    nickname: a.nickname,
    assignmentIds: [...(a.assignmentIds || [])],
    escalationAgentId: a.escalationAgentId || "",
  }));
  const byId = Object.fromEntries(nodes.map((n) => [n.agentId, n]));

  orgState = { nodes, byId, base: {} };
  nodes.forEach((n) => { orgState.base[n.agentId] = { assignmentIds: [...n.assignmentIds], escalationAgentId: n.escalationAgentId }; });

  const positions = orgAutoLayout(nodes, byId);
  // 恢复该用户上次保存的布局：有记录的节点用之，其余落到自动布局；没有记录时整体用自动布局
  const saved = loadOrgPositions();
  nodes.forEach((n) => { if (saved[n.agentId]) { positions[n.agentId] = { x: saved[n.agentId].x, y: saved[n.agentId].y }; } });

  // 复位为自动布局（重新计算并覆盖保存为当前布局）
  const applyAuto = () => {
    const pos = orgAutoLayout(nodes, byId);
    nodes.forEach((n) => { n.x = pos[n.agentId].x; n.y = pos[n.agentId].y; n.el.style.left = n.x + "px"; n.el.style.top = n.y + "px"; });
    retrofitCanvas();
    draw();
    saveOrgPositions();   // 自动布局即成为下次打开记住的布局
  };

  // 依据节点位置推算画布尺寸（列数随数字员工数量变化，不再写死 920）
  function retrofitCanvas() {
    let maxX = 0, maxY = 0;
    nodes.forEach((n) => { maxX = Math.max(maxX, n.x + (n.el.offsetWidth || 150)); maxY = Math.max(maxY, n.y + (n.el.offsetHeight || 60)); });
    const w = Math.max(380, maxX + 80);
    const h = Math.max(320, maxY + 40);
    el.style.width = w + "px";
    svg.style.width = w + "px";
    el.style.height = h + "px";
    svg.style.height = h + "px";
    svg.setAttribute("width", w); svg.setAttribute("height", h);
    $("orgCanvasWrap").style.height = h + "px";
  }

  // 建节点 DOM（含两个连接端口：下方=指派，顶部=提升）
  nodes.forEach((n) => {
    n.x = positions[n.agentId].x;
    n.y = positions[n.agentId].y;
    n.el = document.createElement("div");
    n.el.className = "org-node";
    n.el.dataset.agentId = n.agentId;
    n.el.style.left = n.x + "px";
    n.el.style.top = n.y + "px";
    n.el.innerHTML = `
      <div class="org-port org-port-assign" data-port="assign" title="${escapeHtml(t("org.portAssign"))}"></div>
      <div class="org-port org-port-esc" data-port="esc" title="${escapeHtml(t("org.portEsc"))}"></div>
      <button class="org-opt-btn" type="button" title="${escapeHtml(t("org.optimizeTip"))}" aria-label="${escapeHtml(t("org.optimize"))}"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><polyline points="16 3 21 8 8 21 3 21 3 16 16 3"/></svg></button>
      <b>${escapeHtml(n.nickname)}</b><code>${escapeHtml(n.agentId)}</code>`;
    el.appendChild(n.el);
    // 「优化指派」：为该数字员工生成/管理下一层任务指派提示词；阻止按钮事件冒泡以免触发节点拖拽
    n.el.querySelector(".org-opt-btn").addEventListener("pointerdown", (e) => e.stopPropagation());
    n.el.querySelector(".org-opt-btn").addEventListener("click", (e) => { e.stopPropagation(); openOrgOptimize(n.agentId); });
  });

  // ---- 连线绘制（纵向布线：指派=源底→目标顶；提升=源顶→目标底） ----
  function edgeParams(n, t, type) {
    if (type === "assign") {
      const x1 = n.x + n.el.offsetWidth / 2, y1 = n.y + n.el.offsetHeight, x2 = t.x + t.el.offsetWidth / 2, y2 = t.y;
      const gap = Math.max(30, (y2 - y1) / 2);
      const d = `M${x1} ${y1} C${x1} ${y1 + gap} ${x2} ${y2 - gap} ${x2} ${y2}`;
      return { d, x1, y1, x2, y2 };
    }
    const x1 = n.x + n.el.offsetWidth / 2, y1 = n.y, x2 = t.x + t.el.offsetWidth / 2, y2 = t.y + t.el.offsetHeight;
    const gap = Math.max(30, (y1 - y2) / 2);
    const d = `M${x1} ${y1} C${x1} ${y1 - gap} ${x2} ${y2 + gap} ${x2} ${y2}`;
    return { d, x1, y1, x2, y2 };
  }
  function drawEdge(src, tgt, type) {
    const p = edgeParams(byId[src], byId[tgt], type);
    // 透明宽命中路径：点击即删除该连线
    const hit = document.createElementNS(NS, "path");
    hit.setAttribute("d", p.d);
    hit.setAttribute("class", "org-edge org-edge-hit");
    hit.setAttribute("stroke-width", 14);
    hit.dataset.edge = src + "|" + tgt + "|" + type;
    svg.appendChild(hit);
    const vis = document.createElementNS(NS, "path");
    vis.setAttribute("d", p.d);
    vis.setAttribute("class", "org-edge " + (type === "assign" ? "org-edge-assign" : "org-edge-esc"));
    vis.setAttribute("marker-end", "url(#orgArrow" + (type === "assign" ? "Assign" : "Esc") + ")");
    svg.appendChild(vis);
  }
  function draw() {
    svg.innerHTML = "";
    const defs = document.createElementNS(NS, "defs");
    defs.innerHTML = `<marker id="orgArrowAssign" markerWidth="10" markerHeight="10" refX="8" refY="3" orient="auto"><path d="M0,0 L0,6 L8,3 z" fill="#a98bff"/></marker>`
      + `<marker id="orgArrowEsc" markerWidth="10" markerHeight="10" refX="8" refY="3" orient="auto"><path d="M0,0 L0,6 L8,3 z" fill="#ff9b85"/></marker>`;
    svg.appendChild(defs);
    nodes.forEach((n) => {
      n.assignmentIds.forEach((tgt) => { if (byId[tgt]) drawEdge(n.agentId, tgt, "assign"); });
      if (n.escalationAgentId && byId[n.escalationAgentId]) drawEdge(n.agentId, n.escalationAgentId, "esc");
    });
  }

  // 点击连线删除
  svg.addEventListener("click", (e) => {
    const hit = e.target && e.target.closest ? e.target.closest(".org-edge-hit") : null;
    if (!hit) return;
    const [src, tgt, type] = hit.dataset.edge.split("|");
    const s = byId[src];
    if (type === "assign") { s.assignmentIds = s.assignmentIds.filter((id) => id !== tgt); $("orgStatus").textContent = t("org.assignRemoved"); }
    else { s.escalationAgentId = ""; $("orgStatus").textContent = t("org.escRemoved"); }
    draw();
  });

  // ---- 端口拖拽：创建 / 重设 / 移除连线；拖到空白 = 清空该源的该类全部连线 ----
  nodes.forEach((n) => {
    const portByType = (type) => (type === "assign" ? n.el.querySelector(".org-port-assign") : n.el.querySelector(".org-port-esc"));
    const anchor = (type) => (type === "assign"
      ? { x: n.x + n.el.offsetWidth / 2, y: n.y + n.el.offsetHeight }
      : { x: n.x + n.el.offsetWidth / 2, y: n.y });

    const onPortDown = (e, type) => {
      e.preventDefault();
      e.stopPropagation();
      const a = anchor(type);
      const tmp = document.createElementNS(NS, "path");
      tmp.setAttribute("class", "org-edge org-edge-preview");
      tmp.setAttribute("d", `M${a.x} ${a.y} L${a.x} ${a.y}`);
      svg.appendChild(tmp);
      const move = (me) => {
        const p = orgCanvasPoint(me);
        const gap = Math.max(30, Math.abs(p.y - a.y) / 2);
        const d = type === "assign"
          ? `M${a.x} ${a.y} C${a.x} ${a.y + gap} ${p.x} ${p.y - gap} ${p.x} ${p.y}`
          : `M${a.x} ${a.y} C${a.x} ${a.y - gap} ${p.x} ${p.y + gap} ${p.x} ${p.y}`;
        tmp.setAttribute("d", d);
      };
      const up = (ue) => {
        window.removeEventListener("pointermove", move);
        window.removeEventListener("pointerup", up);
        tmp.remove();
        const hit = document.elementFromPoint(ue.clientX, ue.clientY);
        const nodeEl = hit && hit.closest ? hit.closest(".org-node") : null;
        const tgtId = nodeEl && nodeEl.dataset.agentId !== n.agentId ? nodeEl.dataset.agentId : null;
        if (tgtId) {
          if (type === "assign") {
            const i = n.assignmentIds.indexOf(tgtId);
            if (i >= 0) n.assignmentIds.splice(i, 1); else n.assignmentIds.push(tgtId);
            $("orgStatus").textContent = i >= 0 ? t("org.assignRemoved") : t("org.assignSet");
          } else {
            if (tgtId === n.escalationAgentId) n.escalationAgentId = "";
            else n.escalationAgentId = tgtId;
            $("orgStatus").textContent = n.escalationAgentId ? t("org.escSet") : t("org.escRemoved");
          }
        } else {
          // 拖到空白：清空该源的该类全部连线
          if (type === "assign") { n.assignmentIds = []; $("orgStatus").textContent = t("org.assignCleared"); }
          else { n.escalationAgentId = ""; $("orgStatus").textContent = t("org.escCleared"); }
        }
        draw();
      };
      window.addEventListener("pointermove", move);
      window.addEventListener("pointerup", up);
    };
    n.el.querySelector(".org-port-assign").addEventListener("pointerdown", (e) => onPortDown(e, "assign"));
    n.el.querySelector(".org-port-esc").addEventListener("pointerdown", (e) => onPortDown(e, "esc"));

    // 节点主体拖拽：移动位置
    let sx = 0, sy = 0, startX = 0, startY = 0;
    n.el.addEventListener("pointerdown", (e) => {
      sx = e.clientX; sy = e.clientY; startX = n.x; startY = n.y;
      n.el.setPointerCapture(e.pointerId);
    });
    n.el.addEventListener("pointermove", (e) => {
      if (n.el.hasPointerCapture(e.pointerId)) {
        n.x = Math.max(0, startX + (e.clientX - sx));
        n.y = Math.max(0, startY + (e.clientY - sy));
        n.el.style.left = n.x + "px"; n.el.style.top = n.y + "px";
        draw();
      }
    });
    n.el.addEventListener("pointerup", (e) => { if (n.el.hasPointerCapture(e.pointerId)) { n.el.releasePointerCapture(e.pointerId); saveOrgPositions(); } });
  });

  // 画布复位与打开：先显示弹窗再计算尺寸与绘制，否则隐藏态的 offsetWidth/Height 为 0，连线会画在错误位置
  $("orgReset").onclick = applyAuto;
  $("orgStatus").textContent = "";
  $("orgModal").classList.remove("hidden");
  $("orgSave").onclick = saveOrgChart;
  $("orgCancel").onclick = () => $("orgModal").classList.add("hidden");
  $("orgOptCancel").onclick = () => $("orgOptModal").classList.add("hidden");
  $("orgOptAppend").onclick = applyOrgOptimize;
  retrofitCanvas();
  draw();
}

async function saveOrgChart() {
  if (!orgState) return;
  saveOrgPositions();   // 无论连线是否变更，都记录当前布局（拖拽/自动布局已实时记录，此处兜底）
  const changes = [];
  orgState.nodes.forEach((n) => {
    const b = orgState.base[n.agentId];
    const changed = JSON.stringify(n.assignmentIds.slice().sort()) !== JSON.stringify((b.assignmentIds || []).slice().sort())
      || (n.escalationAgentId || "") !== (b.escalationAgentId || "");
    if (changed) changes.push({ agentId: n.agentId, assignmentIds: [...n.assignmentIds], escalationAgentId: n.escalationAgentId || null });
  });
  if (changes.length === 0) { toast(t("org.noChange")); $("orgModal").classList.add("hidden"); return; }
  for (const c of changes) {
    try {
      const src = agentList.find((x) => x.agentId === c.agentId);
      const body = serializeAgent(src);
      body.assignmentIds = c.assignmentIds;
      body.escalationAgentId = c.escalationAgentId;
      const res = await fetch(`/ag-ui/agents/${encodeURIComponent(c.agentId)}`, {
        method: "PUT", headers: { "Content-Type": "application/json", Authorization: `Bearer ${state.token}` },
        body: JSON.stringify(body),
      });
      const data = await res.json().catch(() => null);
      if (!res.ok) { toast(t("common.saveFail", { err: errMsg(data, res.status) })); return; }
    } catch (ex) { toast(t("common.saveFail", { err: ex.message })); return; }
  }
  toast(t("org.saved"));
  $("orgModal").classList.add("hidden");
  await loadAgents();
}

/** 组织架构是否存在未保存改动（指派 / 提升连线与“打开时基准”不一致）。 */
function orgHasUncommittedChanges() {
  if (!orgState || !orgState.nodes) return false;
  const cmpArr = (a, b) => JSON.stringify((a || []).slice().sort()) !== JSON.stringify((b || []).slice().sort());
  return orgState.nodes.some((n) => {
    const b = orgState.base && orgState.base[n.agentId];
    if (!b) return true;
    return cmpArr(n.assignmentIds, b.assignmentIds) || ((n.escalationAgentId || "") !== (b.escalationAgentId || ""));
  });
}

/** 组织架构节点「优化指派」：生成该数字员工管理下一层指派提示词，预览后可追加到其 Instructions。
 * 若组织架构有<b>未保存</b>的指派/提升改动，先提示保存，避免基于过时后端数据生成。 */
let orgOptAgentId = "";
async function openOrgOptimize(agentId) {
  if (!state.token) { toast(t("agent.err.loginRequired")); return; }
  // 有未保存改动（含本节点或其它节点新增/删除的指派连线）→ 先提示保存，不生成
  if (orgHasUncommittedChanges()) { toast(t("org.optimizeNeedSave")); return; }
  orgOptAgentId = agentId;
  const src = (agentList || []).find((x) => x.agentId === agentId);
  $("orgOptAgent").textContent = src ? `${src.nickname || agentId}（${agentId}）` : agentId;
  $("orgOptText").value = t("org.optimizeGen");
  $("orgOptAppend").disabled = true;
  $("orgOptModal").classList.remove("hidden");
  // 异步生成：期间弹窗可见；失败回退提示
  try {
    const res = await fetch(`/ag-ui/agents/${encodeURIComponent(agentId)}/optimize-assignment`, {
      method: "POST", headers: { "Content-Type": "application/json" },
    });
    const data = await res.json().catch(() => null);
    if (!res.ok || !data || !data.assignmentGuidance) {
      $("orgOptText").value = "";
      toast(t("org.optimizeGenFail", { err: errMsg(data, res.status) }));
      return;
    }
    $("orgOptText").value = data.assignmentGuidance;
    $("orgOptAppend").disabled = false;
  } catch (ex) {
    $("orgOptText").value = "";
    toast(t("org.optimizeGenFail", { err: ex.message }));
  }
}

/** 应用生成结果：把下一层指派指引追加到该数字员工的 Instructions（保留原指令，去重）。 */
async function applyOrgOptimize() {
  const guidance = ($("orgOptText").value || "").trim();
  if (!guidance) return;
  const src = (agentList || []).find((x) => x.agentId === orgOptAgentId);
  if (!src) return;
  const body = serializeAgent(src);
  const cur = (src.instructions || "").trim();
  body.instructions = cur ? cur + "\n\n" + guidance : guidance;
  try {
    const res = await fetch(`/ag-ui/agents/${encodeURIComponent(orgOptAgentId)}`, {
      method: "PUT", headers: { "Content-Type": "application/json", Authorization: `Bearer ${state.token}` },
      body: JSON.stringify(body),
    });
    const data = await res.json().catch(() => null);
    if (!res.ok) { toast(t("common.saveFail", { err: errMsg(data, res.status) })); return; }
    $("orgOptModal").classList.add("hidden");
    toast(t("org.optimizeApplied"));
    await loadAgents();
  } catch (ex) { toast(t("common.saveFail", { err: ex.message })); }
}

/** 序列化数字员工配置：排除敏感字段 bridgeToken（不导出）；ownerId 不导出（导入后归属当前用户）。 */
function serializeAgent(a) {
  return {
    agentId: a.agentId || null,
    nickname: a.nickname,
    description: a.description || null,
    instructions: a.instructions || null,
    avatar: a.avatar || null,
    triggerMode: a.triggerMode || "mentioned",
    keywords: a.keywords || [],
    model: a.model || null,
    bridgeEndpoint: a.bridgeEndpoint || null,
    bridgeMode: a.bridgeMode || null,
    personalMemoryEnabled: !!a.personalMemoryEnabled,
    isPrivate: !!a.isPrivate,
    knowledgeBaseIds: (a.knowledgeBaseIds || []),
    skills: (a.skills || []).map((s) => ({ skillId: s.skillId || null, description: s.description || null, targetAgentId: s.targetAgentId || null })),
    assignmentIds: (a.assignmentIds || []),
    escalationAgentId: a.escalationAgentId || null,
    skillDefIds: (a.skillDefIds || []),
  };
}

/** 下载 JSON 文件（浏览器 Blob）。 */
function downloadJson(filename, data) {
  const blob = new Blob([JSON.stringify(data, null, 2)], { type: "application/json" });
  const url = URL.createObjectURL(blob);
  const a = document.createElement("a");
  a.href = url;
  a.download = filename;
  document.body.appendChild(a);
  a.click();
  a.remove();
  URL.revokeObjectURL(url);
}

/** 系统数据备份：导入预览渲染（账号 / 数字员工存在性摘要 + 知聚勾选列表）。 */
function renderBackupPreview(data) {
  const missingAccounts = data.accounts.filter((a) => !a.exists).length;
  const missingAgents = data.agents.filter((a) => !a.exists).length;
  $("backupImportSummary").textContent =
    t("backup.previewSummary", {
      accounts: data.accounts.length,
      agents: data.agents.length,
      groups: data.groups.length,
      acctNotes: missingAccounts ? t("backup.previewAcctNew", { n: missingAccounts }) : t("backup.previewAcctAllThere"),
      agentNotes: missingAgents ? t("backup.previewAgentNew", { n: missingAgents }) : t("backup.previewAgentAllThere"),
    });
  const list = $("backupImportGroups");
  list.innerHTML = data.groups.map((g) => `
    <label class="backup-group-item">
      <input type="checkbox" value="${escapeHtml(g.groupId)}" checked />
      <span class="bg-name">${escapeHtml(g.groupName)}</span>
      <span class="bg-meta">${t("backup.previewGroupItem", { members: Number(g.memberCount) || 0, messages: Number(g.messageCount) || 0 })}</span>
    </label>`).join("");
}

/** 系统数据备份：导入结果报告渲染。 */
function renderBackupResult(r) {
  const groups = (r.groupsImported || []).map((g) =>
    `<li>${t("backup.resultGroupItem", { name: escapeHtml(g.groupName), memberCount: g.memberCount, messageCount: g.messageCount, id: escapeHtml(g.newGroupId) })}</li>`).join("");
  $("backupResult").innerHTML = `
    <div class="br-title">${t("backup.resultTitle")}</div>
    <ul>
      <li>${t("backup.resultAccounts", { created: r.accountsCreated, updated: r.accountsUpdated })}</li>
      <li>${t("backup.resultAgents", { created: r.agentsCreated, skipped: r.agentsSkipped })}</li>
      <li>${t("backup.resultAttachments", { restored: r.attachmentsRestored, skipped: r.attachmentsSkipped })}</li>
      ${groups ? `<li>${t("backup.resultGroups")}</li>${groups}` : `<li>${t("backup.resultNoGroups")}</li>`}
    </ul>`;
}

/** 导出单个 / 多个数字员工为 JSON 文件（格式：{ version, agents: [...] }）。 */
function exportAgents(agents, filename = `agents-${Date.now()}.json`) {
  if (!agents.length) { toast(t("agent.exportNone")); return; }
  downloadJson(filename, { version: 1, agents: agents.map(serializeAgent) });
  toast(t("agent.exported", { count: agents.length }));
}

/** 解析导入文件：支持 {version, agents:[...]} 与裸数组两种格式；返回数字员工列表或抛错。 */
function parseAgentImport(text) {
  let data = JSON.parse(text);
  if (Array.isArray(data)) return data;
  if (data && Array.isArray(data.agents)) return data.agents;
  if (data && typeof data === "object" && data.nickname) return [data];
  throw new Error("无法识别的导入文件格式（应为 {version, agents:[…]} 或数字员工数组）");
}

/** 从文件导入：逐条调用 POST /ag-ui/agents 创建（agentId 冲突时自动生成新 ID；bridgeToken 不导入）。 */
async function importAgentsFromFile(file) {
  let agents;
  try {
    agents = parseAgentImport(await file.text());
  } catch (ex) {
    toast(t("agent.importFail", { err: ex.message }));
    return;
  }
  if (!Array.isArray(agents) || agents.length === 0) { toast(t("agent.importEmpty")); return; }
  if (!state.token) { toast(t("agent.err.loginRequired")); return; }

  let ok = 0, conflict = 0, failed = 0;
  for (const a of agents) {
    const body = {
      agentId: a.agentId || null,
      nickname: (a.nickname || "").trim(),
      description: a.description || null,
      instructions: a.instructions || null,
      avatar: a.avatar || null,
      triggerMode: a.triggerMode || "mentioned",
      keywords: a.keywords || [],
      model: a.model || null,
      bridgeEndpoint: a.bridgeEndpoint || null,
      bridgeMode: a.bridgeMode || null,
      // 敏感令牌不导入（导出时已排除），由导入者重新配置
      bridgeToken: null,
      personalMemoryEnabled: !!a.personalMemoryEnabled,
      isPrivate: !!a.isPrivate,
      knowledgeBaseIds: (a.knowledgeBaseIds || []),
      skills: (a.skills || []).map((s) => ({ skillId: s.skillId || null, description: s.description || null, targetAgentId: s.targetAgentId || null })),
      assignmentIds: (a.assignmentIds || []),
      escalationAgentId: a.escalationAgentId || null,
      skillDefIds: (a.skillDefIds || []),
    };
    if (!body.nickname) { failed++; continue; }
    try {
      let res = await fetch("/ag-ui/agents", {
        method: "POST",
        headers: { "Content-Type": "application/json", Authorization: `Bearer ${state.token}` },
        body: JSON.stringify(body),
      });
      if (res.status === 409 && body.agentId) {
        // agentId 冲突：自动生成新 ID 重试，避免覆盖现有数字员工
        conflict++;
        body.agentId = null;
        res = await fetch("/ag-ui/agents", {
          method: "POST",
          headers: { "Content-Type": "application/json", Authorization: `Bearer ${state.token}` },
          body: JSON.stringify(body),
        });
      }
      if (res.ok) ok++; else failed++;
    } catch { failed++; }
  }
  await Promise.all([loadAgents(), loadAgentDirectory()]);
  toast(t("agent.importDone", { ok, failed }) + (conflict ? t("agent.importConflict", { n: conflict }) : ""));
}

/** 删除二次确认：首次点击进入确认态（✅ 确认 / ❌ 取消图标），再次点击确认执行删除。
 * 进入确认态前先复位其它行已进入的确认态（避免多行残留取消按钮）。 */
function confirmDeleteAgent(agent, btn) {
  if (btn.dataset.confirming !== "1") {
    // 复位其它行的确认态：同一时间只允许一行处于确认状态
    document.querySelectorAll('.agent-row button[data-act="del"].danger-solid').forEach((b) => {
      if (b !== btn) restoreDeleteBtn(b);
    });
    btn.dataset.confirming = "1";
    btn.classList.add("danger-solid");
    btn.textContent = "✅";
    btn.title = t("agent.delConfirm");
    const cancel = document.createElement("button");
    cancel.className = "icon-btn";
    cancel.textContent = "❌";
    cancel.title = t("agent.delCancel");
    cancel.onclick = (e) => { e.stopPropagation(); restoreDeleteBtn(btn); };
    btn.parentElement.appendChild(cancel);
    return;
  }
  deleteAgent(agent);
}

function restoreDeleteBtn(btn) {
  delete btn.dataset.confirming;
  btn.classList.remove("danger-solid", "confirming");
  btn.textContent = "🗑️";
  btn.title = t("agent.del");
  const cancel = btn.parentElement.querySelector(".icon-btn:not([data-act])");
  if (cancel) cancel.remove();
}


/* 数字员工表单：可折叠分组。绑定折叠点击，并按 data-collapse-default / 用户记忆恢复开合。 */
const AF_SECTION_KEY = "agui.agentFormSections";
function loadAgentSectionState() {
  try { return JSON.parse(localStorage.getItem(AF_SECTION_KEY) || "{}") || {}; } catch { return {}; }
}
function initCollapsibleSections(bindOnly = false) {
  document.querySelectorAll("#agentFormView .form-section[data-collapse-key]").forEach((sec) => {
    // 静态区块（data-static-section）：不可折叠，始终展开
    if (sec.dataset.staticSection) return;
    const key = sec.dataset.collapseKey;
    const body = document.getElementById(key);
    if (!body) return;
    if (!bindOnly && !sec.dataset.bound) {
      sec.dataset.bound = "1";
      sec.addEventListener("click", () => {
        const collapsed = !sec.classList.contains("collapsed");
        sec.classList.toggle("collapsed", collapsed);
        body.classList.toggle("hidden-section", collapsed);
        addAgentSectionState(key, collapsed ? "closed" : "open");
      });
    }
    // 用户记忆优先，其次 data-collapse-default
    const saved = loadAgentSectionState()[key];
    const def = sec.dataset.collapseDefault || "open";
    const collapsed = (saved ? saved === "closed" : def === "closed");
    sec.classList.toggle("collapsed", collapsed);
    body.classList.toggle("hidden-section", collapsed);
  });
}
function addAgentSectionState(key, state) {
  const s = loadAgentSectionState();
  s[key] = state;
  try { localStorage.setItem(AF_SECTION_KEY, JSON.stringify(s)); } catch { /* 存储不可用忽略 */ }
}

function openAgentForm(agentId) {
  editingAgentId = agentId || null;
  initCollapsibleSections(false);
  const a = editingAgentId ? agentList.find((x) => x.agentId === editingAgentId) : null;
  $("agentFormTitle").textContent = a ? t("agent.form.editTitle", { name: a.nickname }) : t("agent.form.add");
  $("afAgentId").value = a?.agentId || "";
  $("afAgentId").disabled = !!a;
  $("afNickname").value = a?.nickname || "";
  $("afDescription").value = a?.description || "";
  $("afInstructions").value = a?.instructions || "";
  agentAvatar = a?.avatar || null; // null = 未改动（沿用原头像 / 无头像）
  afAvatarPicker.render(a?.avatar || "");
  $("afTriggerMode").value = a?.triggerMode || "mentioned";
  $("afKeywords").value = (a?.keywords || []).join(", ");
  $("afSchedule").value = a?.schedule || "";
  $("afModel").value = a?.model || "";
  // AG-UI 桥接：端点 / 方言回显；令牌不回显（留空 = 编辑时沿用原值）
  $("afBridgeEndpoint").value = a?.bridgeEndpoint || "";
  $("afBridgeMode").value = a?.bridgeMode || "standard";
  $("afBridgeToken").value = "";
  $("afPersonalMemory").checked = !!a?.personalMemoryEnabled;
  $("afIsPrivate").checked = !!a?.isPrivate;
  // 可复用技能（技能库）：回显挂载 + 异步加载技能库选项
  agentSkillDefIds = [...(a?.skillDefIds || [])];
  if (state.token && !skillList.length) loadSkills().then(() => renderAgentSkillDefPicks());
  else renderAgentSkillDefPicks();
  // 可调用子数字员工（Skills）：回显 + 渲染选择器
  agentSkillPicks = [...(a?.skills || [])].filter((s) => s && s.targetAgentId)
    .map((s) => ({ skillId: s.skillId || "", description: s.description || "", targetAgentId: s.targetAgentId }));
  renderAgentSkillPicks();
  // 知识库：回显绑定
  agentKbIds = [...(a?.knowledgeBaseIds || [])];
  renderKbPicks();
  // 私密数字员工仅创建者或系统管理员可编辑（种子数字员工无 ownerId，登录即可编辑）
  const canEditPrivate = !a?.isPrivate || !a?.ownerId || state.isAdmin || a.ownerId === state.memberId;
  $("afIsPrivate").disabled = !canEditPrivate;
  syncTriggerForm();
  $("agentListView").classList.add("hidden");
  $("agentFormView").classList.remove("hidden");
  $("afNickname").focus();
}

/** 触发方式联动：关键词输入显隐 + 说明文案。 */
function syncTriggerForm() {
  const mode = $("afTriggerMode").value;
  $("afKeywordGroup").classList.toggle("hidden", mode !== "keyword");
  $("afTriggerHint").textContent = TRIGGER_HINTS[mode] || "";
}

async function saveAgent() {
  const body = {
    agentId: $("afAgentId").value.trim() || null,
    nickname: $("afNickname").value.trim(),
    description: $("afDescription").value.trim() || null,
    instructions: $("afInstructions").value.trim() || null,
    avatar: agentAvatar, // 编辑时沿用原头像；"" 或 null 表示无头像
    triggerMode: $("afTriggerMode").value,
    keywords: $("afKeywords").value.split(/[,，]/).map((s) => s.trim()).filter(Boolean),
    schedule: $("afSchedule").value.trim() || null,
    model: $("afModel").value.trim() || null,
    bridgeEndpoint: $("afBridgeEndpoint").value.trim() || null,
    bridgeMode: $("afBridgeMode").value,
    bridgeToken: $("afBridgeToken").value.trim() || null, // 编辑时留空 → 后端沿用原令牌
    personalMemoryEnabled: $("afPersonalMemory").checked,
    isPrivate: $("afIsPrivate").checked,
    knowledgeBaseIds: [...agentKbIds],
    // 可调用子数字员工（Skills）：由表单「可调用子数字员工」勾选维护，skillId 留空后端自动生成
    skills: [...agentSkillPicks].filter((s) => s && s.targetAgentId)
      .map((s) => ({ skillId: s.skillId || null, description: s.description || null, targetAgentId: s.targetAgentId })),
    // 任务指派 / 问题提升由「组织架构」入口维护，此处仅保留原值（编辑时沿用，新增为空）
    assignmentIds: [...((agentList.find((x) => x.agentId === editingAgentId)?.assignmentIds) || [])],
    escalationAgentId: (agentList.find((x) => x.agentId === editingAgentId)?.escalationAgentId) || null,
    // 可复用技能：技能库引用（SkillDefIds）
    skillDefIds: [...agentSkillDefIds],
  };
  if (!body.nickname) { toast(t("agent.err.nicknameRequired")); return; }
  // 定时任务 cron 表达式：5 段（分 时 日 月 周），非法拒绝（后端同样校验）
  if (body.schedule && !/^(\*|[0-9]+|\*\/[0-9]+|[0-9]+-[0-9]+)(,(\*|[0-9]+|\*\/[0-9]+|[0-9]+-[0-9]+))* (\*|[0-9]+|\*\/[0-9]+|[0-9]+-[0-9]+)(,(\*|[0-9]+|\*\/[0-9]+|[0-9]+-[0-9]+))* (\*|[0-9]+|\*\/[0-9]+|[0-9]+-[0-9]+)(,(\*|[0-9]+|\*\/[0-9]+|[0-9]+-[0-9]+))* (\*|[0-9]+|\*\/[0-9]+|[0-9]+-[0-9]+)(,(\*|[0-9]+|\*\/[0-9]+|[0-9]+-[0-9]+))* (\*|[0-9]+|\*\/[0-9]+|[0-9]+-[0-9]+)(,(\*|[0-9]+|\*\/[0-9]+|[0-9]+-[0-9]+))*$/.test(body.schedule)) {
    toast(t("agent.err.scheduleInvalid")); return;
  }
  // 填了技能标识的必须合法（OpenAI 工具名规范）；留空的由后端自动生成 skill_<目标ID>
  const url = editingAgentId ? `/ag-ui/agents/${encodeURIComponent(editingAgentId)}` : "/ag-ui/agents";
  try {
    const res = await fetch(url, {
      method: editingAgentId ? "PUT" : "POST",
      headers: { "Content-Type": "application/json", Authorization: `Bearer ${state.token}` },
      body: JSON.stringify(body),
    });
    const data = await res.json().catch(() => null);
    if (!res.ok) { toast(t("common.saveFail", { err: errMsg(data, `保存失败（${res.status}）`) })); return; }
    toast(editingAgentId ? t("agent.updated") : t("agent.created"));
    await loadAgents();
    await loadAgentDirectory();
    showAgentListView();
  } catch (ex) { toast(t("common.saveFail", { err: ex.message })); }
}

let agentSkillDefIds = [];   // 数字员工表单：从技能库挂载的可复用技能 ID
let agentSkillPicks = [];    // 数字员工表单：可调用子数字员工（Skills）[{skillId,description,targetAgentId}]

/* ============ 技能库（可复用技能：shell / http / prompt） ============ */

let skillList = [];      // 技能库 [{skillId,name,description,kind,body,parametersJson,interpreter,httpTimeoutSeconds,requiresApproval,ownerId}]
let editingSkillId = null;

/** 加载技能库列表。返回是否成功（登录态）。 */
async function loadSkills() {
  if (!state.token) return false;
  try {
    const res = await fetch("/ag-ui/skills", { headers: { Authorization: "Bearer " + state.token } });
    if (!res.ok) return false;
    skillList = await res.json();
    return true;
  } catch { return false; }
}

/** 渲染技能库列表。 */
function renderSkillList() {
  const el = $("skillList");
  el.innerHTML = "";
  if (!skillList.length) {
    el.innerHTML = `<div class="kb-empty" data-i18n="skill.empty">技能库为空，点「新增技能」创建第一个可复用技能。</div>`;
    return;
  }
  const kindLabel = { shell: t("skill.kind.shellShort"), http: t("skill.kind.httpShort"), prompt: t("skill.kind.promptShort") };
  skillList.forEach((s) => {
    const row = document.createElement("div");
    row.className = "skill-row skill-list-item";
    const canManage = !s.ownerId || state.isAdmin || s.ownerId === state.memberId;
    row.innerHTML = `
      <span class="skill-name"><b>${escapeHtml(s.name)}</b><code>${escapeHtml(s.skillId)}</code></span>
      <span class="skill-kind tag-skill">${escapeHtml(kindLabel[s.kind] || s.kind)}</span>
      <span class="skill-desc">${escapeHtml(s.description || "—")}</span>
      <span class="skill-op-col">
        <button class="icon-btn" data-skill-act="test" title="${escapeHtml(t("skill.testRun"))}">▶</button>
        ${canManage ? `<button class="icon-btn" data-skill-act="edit" title="${escapeHtml(t("skill.edit"))}">✏️</button><button class="icon-btn danger" data-skill-act="del" title="${escapeHtml(t("skill.del"))}">🗑️</button>` : ""}
      </span>`;
    row.querySelector('[data-skill-act="test"]').onclick = () => testSkill(s.skillId);
    if (canManage) {
      row.querySelector('[data-skill-act="edit"]').onclick = () => openSkillForm(s.skillId);
      row.querySelector('[data-skill-act="del"]').onclick = () => deleteSkill(s.skillId);
    }
    el.appendChild(row);
  });
}

/** 打开技能库弹窗。 */
async function openSkillModal() {
  if (!state.token) { toast(t("agent.err.loginRequired")); return; }
  showSkillListView();
  await loadSkills();
  renderSkillList();
  $("skillModal").classList.remove("hidden");
}

function showSkillListView() { $("skillListView").classList.remove("hidden"); $("skillFormView").classList.add("hidden"); }
function showSkillFormView() { $("skillListView").classList.add("hidden"); $("skillFormView").classList.remove("hidden"); }

/** 打开技能表单编辑（skillId 为空 = 新建）。 */
function openSkillForm(skillId) {
  editingSkillId = skillId || null;
  const s = skillId ? skillList.find((x) => x.skillId === skillId) : null;
  const sf = (id) => $(id);
  sf("sfName").value = s?.name || "";
  sf("sfSkillId").value = s?.skillId || "";
  sf("sfSkillId").disabled = !!s; // 已存在技能 ID 不可改（工具名稳定）
  sf("sfKind").value = s?.kind || "prompt";
  sf("sfDescription").value = s?.description || "";
  sf("sfInterpreter").value = s?.interpreter || "";
  sf("sfBody").value = s?.body || "";
  sf("sfRequiresApproval").checked = s?.requiresApproval !== false;
  syncSkillKind();
  sf("sfTestResult").textContent = "";
  showSkillFormView();
}

/** 类型切换时联动：shell/ http 显示解释器 / 强制审批。 */
function syncSkillKind() {
  const kind = $("sfKind").value;
  const showInterp = kind === "shell";
  $("sfInterpreterGroup").style.display = showInterp ? "" : "none";
  // 仅 shell 技能强制需审批（任意本机命令执行面最大）；HTTP / 提示词技能允许关闭以自动调用
  $("sfRequiresApproval").disabled = (kind === "shell");
  $("sfBodyLabel").dataset.i18n = kind === "http" ? "skill.form.bodyHttp" : (kind === "prompt" ? "skill.form.bodyPrompt" : "skill.form.bodyShell");
  const label = t(kind === "http" ? "skill.form.bodyHttp" : (kind === "prompt" ? "skill.form.bodyPrompt" : "skill.form.bodyShell"));
  $("sfBodyLabel").textContent = label;
}

/** 保存技能（新建 POST / 更新 PUT）。 */
async function saveSkill() {
  const name = $("sfName").value.trim();
  if (!name) { toast(t("skill.err.nameRequired")); return; }
  const desc = $("sfDescription").value.trim();
  if (!desc) { toast(t("skill.err.descRequired")); return; }
  const kind = $("sfKind").value;
  const body = $("sfBody").value;
  if (kind !== "prompt" && !body.trim()) { toast(t("skill.err.bodyRequired")); return; }
  const payload = {
    skillId: editingSkillId || $("sfSkillId").value.trim() || null,
    name, description: desc, kind, body,
    parametersJson: "",
    interpreter: $("sfInterpreter").value.trim() || null,
    httpTimeoutSeconds: 30,
    requiresApproval: $("sfRequiresApproval").checked,
  };
  const url = editingSkillId ? `/ag-ui/skills/${encodeURIComponent(editingSkillId)}` : "/ag-ui/skills";
  const method = editingSkillId ? "PUT" : "POST";
  try {
    const res = await fetch(url, { method, headers: { "Content-Type": "application/json", Authorization: "Bearer " + state.token }, body: JSON.stringify(payload) });
    const data = await res.json().catch(() => null);
    if (!res.ok) { toast(t("common.saveFail", { err: errMsg(data, res.status) })); return; }
    toast(editingSkillId ? t("skill.updated") : t("skill.created"));
    await loadSkills(); renderSkillList();
    showSkillListView();
  } catch (ex) { toast(t("common.saveFail", { err: ex.message })); }
}

/** 试运行技能（用当前表单定义或已存定义跑一次）。 */
async function testSkill(skillId) {
  const id = skillId || editingSkillId;
  if (!id) { toast(t("skill.err.saveFirst")); return; }
  const query = prompt(t("skill.testQuery"), "你好");
  if (query === null) return;
  try {
    const res = await fetch(`/ag-ui/skills/${encodeURIComponent(id)}/run`, {
      method: "POST", headers: { "Content-Type": "application/json", Authorization: "Bearer " + state.token },
      body: JSON.stringify({ query }),
    });
    const data = await res.json().catch(() => null);
    if (!res.ok) { toast(t("common.saveFail", { err: errMsg(data, res.status) })); return; }
    $("sfTestResult").textContent = `▶ ${t("skill.testResult")}\n${data.result || ""}`;
  } catch (ex) { toast(t("common.saveFail", { err: ex.message })); }
}

/** 删除技能。 */
async function deleteSkill(skillId) {
  if (!confirm(t("skill.delConfirm", { name: skillId }))) return;
  try {
    const res = await fetch(`/ag-ui/skills/${encodeURIComponent(skillId)}`, { method: "DELETE", headers: { Authorization: "Bearer " + state.token } });
    if (!res.ok) { const d = await res.json().catch(() => null); toast(t("common.saveFail", { err: errMsg(d, res.status) })); return; }
    toast(t("skill.deleted"));
    await loadSkills(); renderSkillList();
  } catch (ex) { toast(t("common.saveFail", { err: ex.message })); }
}

/** 数字员工表单：可复用技能（技能库）多选回显渲染。 */
function renderAgentSkillDefPicks() {
  const el = $("afSkillDefList");
  el.innerHTML = "";
  (skillList || []).forEach((s) => {
    const on = agentSkillDefIds.includes(s.skillId);
    const label = document.createElement("label");
    label.className = "kb-pick-item" + (on ? " on" : "");
    label.innerHTML = `<input type="checkbox" value="${escapeHtml(s.skillId)}" ${on ? "checked" : ""} /> <span class="skill-kind tag-skill">${escapeHtml(s.kind)}</span> <b>${escapeHtml(s.name)}</b> <code>${escapeHtml(s.skillId)}</code> <span class="kb-meta">${escapeHtml(s.description || "")}</span>`;
    label.querySelector("input").addEventListener("change", (e) => {
      const id = e.target.value, check = e.target.checked;
      const i = agentSkillDefIds.indexOf(id);
      if (check && i < 0) agentSkillDefIds.push(id);
      if (!check && i >= 0) agentSkillDefIds.splice(i, 1);
      renderAgentSkillDefPicks();
    });
    el.appendChild(label);
  });
}

/** 数字员工表单：可调用子数字员工（Skills）多选渲染 + 每项调用说明。
 *  选中某数字员工 = 把它作为本角色可调用技能（模型需要其能力时自动调起），
 *  skillId 留空由后端自动生成 skill_<目标ID>。 */
function renderAgentSkillPicks() {
  const el = $("afSkillAgentList");
  if (!el) return;
  el.innerHTML = "";
  const candidates = (agentList || []).filter((x) =>
    x.agentId && x.agentId !== editingAgentId && !/^skill_/.test(x.agentId) && x.agentId !== "agent_" + editingAgentId);
  if (!candidates.length) {
    el.innerHTML = '<span class="form-hint">暂无可调用的数字员工（先新建其他数字员工）。</span>';
    return;
  }
  candidates.forEach((ag) => {
    const existing = agentSkillPicks.find((p) => p.targetAgentId === ag.agentId);
    const on = !!existing;
    const label = document.createElement("label");
    label.className = "kb-pick-item" + (on ? " on" : "");
    label.style.cursor = "pointer";
    label.innerHTML = `<input type="checkbox" value="${escapeHtml(ag.agentId)}" ${on ? "checked" : ""} /> <span class="skill-kind tag-agent">AI</span> <b>${escapeHtml(ag.nickname || ag.agentId)}</b> <code>${escapeHtml(ag.agentId)}</code> <span class="kb-meta">${escapeHtml(ag.description || "")}</span>`;
    label.querySelector("input").addEventListener("change", (e) => {
      const id = e.target.value, check = e.target.checked;
      const i = agentSkillPicks.findIndex((p) => p.targetAgentId === id);
      if (check && i < 0) {
        const ag2 = (agentList || []).find((x) => x.agentId === id);
        const desc = ag2?.description || ag2?.nickname
          ? `调用数字员工「${ag2?.nickname || id}」${ag2?.description ? "（" + ag2.description + "）" : ""}处理相关事务。`
          : `调用数字员工「${id}」处理相关事务。`;
        agentSkillPicks.push({ skillId: "", description: desc, targetAgentId: id });
      } else if (!check && i >= 0) {
        agentSkillPicks.splice(i, 1);
      }
      renderAgentSkillPicks();
    });
    el.appendChild(label);
    if (on && existing) {
      const descBox = document.createElement("div");
      descBox.style.cssText = "margin:-2px 0 6px 22px";
      const ta = document.createElement("textarea");
      ta.className = "modal-input";
      ta.rows = 2;
      ta.placeholder = t("agent.form.subAgentDescPh");
      ta.value = existing.description || "";
      ta.addEventListener("input", () => { existing.description = ta.value; });
      descBox.appendChild(ta);
      el.appendChild(descBox);
    }
  });
}

let kbList = [];       // 可见知识库 [{kbId,name,description,ownerId,documents}]
let agentKbIds = [];   // 数字员工表单当前选中的知识库 ID

/** 加载可见知识库列表并刷新表单多选 / 管理弹窗。 */
async function loadKbs() {
  try {
    const res = await fetch("/ag-ui/kb", { headers: { Authorization: `Bearer ${state.token}` } });
    if (res.ok) {
      kbList = (await res.json()) || [];
      renderKbPicks();
      if (!$("kbModal").classList.contains("hidden")) renderKbModal();
    }
  } catch { /* 知识库不可用时表单不阻塞 */ }
}

/** 数字员工表单：知识库多选（选中项写入 agentKbIds）。 */
function renderKbPicks() {
  const el = $("afKbList");
  el.innerHTML = "";
  if (!kbList.length) {
    el.innerHTML = '<span class="form-hint">暂无可用知识库，点「📚 管理知识库」创建后即可绑定</span>';
    return;
  }
  kbList.forEach((kb) => {
    const label = document.createElement("label");
    label.style.cssText = "display:flex;align-items:center;gap:6px;margin:4px 0;cursor:pointer";
    const cb = document.createElement("input");
    cb.type = "checkbox";
    cb.checked = agentKbIds.includes(kb.kbId);
    cb.style.cssText = "width:15px;height:15px;accent-color:#4f8cff";
    cb.onchange = () => {
      if (cb.checked) { if (!agentKbIds.includes(kb.kbId)) agentKbIds.push(kb.kbId); }
      else agentKbIds = agentKbIds.filter((x) => x !== kb.kbId);
    };
    const info = document.createElement("span");
    info.style.cssText = "color:#e6e6e6;font-size:13px";
    info.textContent = `📚 ${kb.name}${kb.description ? " — " + kb.description : ""}（${(kb.documents || []).length} 篇文档）`;
    label.appendChild(cb);
    label.appendChild(info);
    el.appendChild(label);
  });
}

/** 知识库管理弹窗：列表 + 上传文档 / 删除（仅自己创建的可管理）。 */
function renderKbModal() {
  const wrap = $("kbListWrap");
  wrap.innerHTML = "";
  if (!kbList.length) {
    wrap.innerHTML = `<div class="form-hint" style="padding:8px 0">${t("kb.noKb")}</div>`;
    return;
  }
  kbList.forEach((kb) => {
    const mine = kb.ownerId === state.memberId;
    const docs = (kb.documents || []).map((d) => {
      const st = d.status === "processing"
        ? `<span class="kb-status kb-status-proc">${t("kb.processing")}</span>`
        : d.status === "error"
          ? `<span class="kb-status kb-status-err" title="${escapeHtml(d.error || "")}">${t("kb.failed")}</span>`
          : `<span class="kb-status kb-status-ok">${t("kb.chunks", { count: Number(d.chunkCount) || 0 })}</span>`;
      return `
      <div class="kb-doc" style="display:flex;align-items:center;gap:6px;margin-top:4px">
        <span style="flex:1;font-size:12px;color:#c9cdd6;overflow:hidden;text-overflow:ellipsis;white-space:nowrap">📄 ${escapeHtml(d.fileName)}</span>${st}
        ${mine ? `<button class="icon-btn kb-doc-del" data-kb="${escapeHtml(kb.kbId)}" data-doc="${escapeHtml(d.docId)}" title="${t("kb.removeDocTitle")}">🗑️</button>` : ""}
      </div>`;
    }).join("") || `<span class="form-hint">${t("kb.noDocs")}</span>`;
    const box = document.createElement("div");
    box.className = "kb-card";
    box.style.cssText = "border:1px solid #3a3f4b;border-radius:8px;padding:10px;margin-bottom:10px";
    box.innerHTML = `
      <div style="display:flex;align-items:center;gap:6px">
        <b style="flex:1">📚 ${escapeHtml(kb.name)}</b>
        ${mine
          ? `<button class="chip-btn kb-upload" data-kb="${escapeHtml(kb.kbId)}" type="button">${t("kb.uploadDoc")}</button>
             <button class="icon-btn kb-del" data-kb="${escapeHtml(kb.kbId)}" title="${t("kb.delTitle")}">🗑️</button>`
          : `<span class="form-hint">${t("kb.systemReadonly")}</span>`}
      </div>
      ${kb.description ? `<div class="form-hint">${escapeHtml(kb.description)}</div>` : ""}
      <div class="kb-docs">${docs}</div>
      ${mine ? `<input type="file" class="hidden kb-file" data-kb="${escapeHtml(kb.kbId)}" accept=".txt,.md,.docx,.xlsx,.pptx,.pdf,.json,.csv" />` : ""}`;
    box.querySelectorAll(".kb-doc-del").forEach((btn) => {
      btn.onclick = async () => {
        if (!confirm(t("kb.docDelConfirm"))) return;
        const res = await fetch(`/ag-ui/kb/${btn.dataset.kb}/documents/${btn.dataset.doc}`, {
          method: "DELETE", headers: { Authorization: `Bearer ${state.token}` },
        });
        const data = await res.json().catch(() => null);
        if (!res.ok) { toast(errMsg(data, t("kb.docRemoveFail", { err: res.status }))); return; }
        toast(t("kb.docRemoved"));
        await loadKbs();
      };
    });
    const upBtn = box.querySelector(".kb-upload");
    if (upBtn) upBtn.onclick = () => box.querySelector(".kb-file").click();
    const fileInput = box.querySelector(".kb-file");
    if (fileInput) fileInput.onchange = async (e) => {
      const file = e.target.files?.[0];
      e.target.value = "";
      if (file) await addKbDocument(fileInput.dataset.kb, file);
    };
    const delBtn = box.querySelector(".kb-del");
    if (delBtn) delBtn.onclick = async () => {
      if (!confirm(t("kb.delConfirm"))) return;
      const res = await fetch(`/ag-ui/kb/${delBtn.dataset.kb}`, {
        method: "DELETE", headers: { Authorization: `Bearer ${state.token}` },
      });
      const data = await res.json().catch(() => null);
      if (!res.ok) { toast(errMsg(data, t("kb.delFail", { err: res.status }))); return; }
      toast(t("kb.deleted"));
      agentKbIds = agentKbIds.filter((x) => x !== delBtn.dataset.kb);
      renderKbPicks();
      await loadKbs();
    };
    wrap.appendChild(box);
  });
}

/** 打开知识库管理弹窗并刷新列表。 */
async function openKbModal() {
  $("kbModal").classList.remove("hidden");
  await loadKbs();
  startKbPolling();
}

/* 文档处理状态轮询：有 processing 文档时每 2s 刷新一次列表，全部完成自动停止。 */
let kbPollTimer = null;

function stopKbPolling() {
  if (kbPollTimer) { clearInterval(kbPollTimer); kbPollTimer = null; }
}

function startKbPolling() {
  stopKbPolling();
  kbPollTimer = setInterval(async () => {
    try {
      const hasProcessing = kbList.some((kb) => (kb.documents || []).some((d) => d.status === "processing"));
      if (!hasProcessing) { stopKbPolling(); return; }
      await loadKbs();
    } catch { /* 轮询失败下次继续 */ }
  }, 2000);
}

/** 上传文档到知识库：先经 /ag-ui/upload 传附件，再调 kb documents API（后台提取文本 + 向量化，返回后轮询状态）。 */
async function addKbDocument(kbId, file) {
  toast(t("kb.uploading", { name: file.name }));
  try {
    const form = new FormData();
    form.append("file", file, file.name);
    const url = state.token ? "/ag-ui/upload" : `/ag-ui/upload?memberId=${encodeURIComponent(state.memberId)}`;
    const up = await fetch(url, { method: "POST", body: form });
    const ups = await up.json().catch(() => null);
    // 上传接口返回 { attachments: [...] }（对象包裹数组），与 uploadAvatarFile / uploadAttachments 一致
    const atts = Array.isArray(ups?.attachments) ? ups.attachments : null;
    if (!up.ok || !atts || !atts.length) { toast(t("kb.uploadFail")); return; }
    const res = await fetch(`/ag-ui/kb/${kbId}/documents`, {
      method: "POST",
      headers: { "Content-Type": "application/json", Authorization: `Bearer ${state.token}` },
      body: JSON.stringify({ attachmentId: atts[0].attachmentId }),
    });
    const data = await res.json().catch(() => null);
    if (!res.ok) { toast(errMsg(data, t("kb.docAddFail", { err: res.status }))); return; }
    toast(t("kb.uploaded", { name: data.fileName }));
    await loadKbs();
    startKbPolling();
  } catch (ex) { toast(t("kb.docProcessFail", { err: ex.message })); }
}

async function deleteAgent(agent) {
  // 确认由行内两步确认（confirmDeleteAgent）负责，此处直接执行删除
  try {
    const res = await fetch(`/ag-ui/agents/${encodeURIComponent(agent.agentId)}`, {
      method: "DELETE",
      headers: { Authorization: `Bearer ${state.token}` },
    });
    const data = await res.json().catch(() => null);
    if (!res.ok) { toast(errMsg(data, t("agent.delFail", { err: res.status }))); return; }
    toast(t("agent.deleted"));
    await loadAgents();
    await loadAgentDirectory();
  } catch (ex) { toast(t("agent.delFail", { err: ex.message })); }
}

/* ============ 修改密码 / 资料 ============ */

async function submitChangePassword() {
  const oldPassword = $("pwOld").value;
  const newPassword = $("pwNew").value;
  if (!oldPassword || !newPassword) { toast(t("pw.fillAll")); return; }
  try {
    const res = await fetch("/ag-ui/user/password", {
      method: "POST",
      headers: { "Content-Type": "application/json", Authorization: `Bearer ${state.token}` },
      body: JSON.stringify({ oldPassword, newPassword }),
    });
    const data = await res.json().catch(() => null);
    if (!res.ok) { toast(errMsg(data, t("pw.changeFail", { err: res.status }))); return; }
    $("pwModal").classList.add("hidden");
    toast(t("pw.changed"));
    logout();
  } catch (ex) { toast(t("pw.changeFail", { err: ex.message })); }
}

function openProfileModal() {
  $("pfNickname").value = $("meNickname").textContent;
  $("pfPersonalMemory").checked = !!state.personalMemoryEnabled;
  profileAvatar = state.avatar || null; // null = 未改动
  pfAvatarPicker.render(state.avatar || "");
  refreshTwinUi(null); // 默认未启用，随后异步查询
  $("profileModal").classList.remove("hidden");
  if (state.token) loadTwinStatus();
}

/* ============ AI 分身 ============ */

let twinStatus = null; // { enabled, twinAgentId, nickname, triggerMode }

/** 查询当前用户分身状态并刷新 UI。 */
async function loadTwinStatus() {
  try {
    const res = await fetch("/ag-ui/twin", { headers: { Authorization: `Bearer ${state.token}` } });
    if (!res.ok) { refreshTwinUi(null); return; }
    const data = await res.json();
    twinStatus = data?.enabled ? data : null;
    refreshTwinUi(twinStatus);
  } catch { refreshTwinUi(null); }
}

/** 刷新分身设置区 UI。 */
function refreshTwinUi(status) {
  twinStatus = status;
  const on = !!status?.enabled;
  $("pfTwinEnable").style.display = on ? "none" : "";
  $("pfTwinDisable").style.display = on ? "" : "none";
  $("pfTwinSync").style.display = on ? "" : "none";
  $("pfTwinStatus").textContent = on
    ? t("profile.twinStatusEnabled", {
        name: status.nickname || "",
        mode: TRIGGER_LABELS[(status.triggerMode || "").toLowerCase()] || status.triggerMode || "",
      })
    : (state.token ? t("profile.twinNotEnabled") : t("common.loginFirst"));
  if (status?.triggerMode) $("pfTwinTrigger").value = status.triggerMode.toLowerCase();
}

/** 同步分身到当前全部公开知聚（补齐启用后新建 / 加入的知聚）。 */
async function syncTwinGroups() {
  if (!state.token || !twinStatus?.enabled) return;
  $("pfTwinSync").disabled = true;
  try {
    const res = await fetch("/ag-ui/twin/sync", {
      method: "POST",
      headers: { Authorization: `Bearer ${state.token}` },
    });
    const data = await res.json().catch(() => null);
    if (!res.ok) { toast(errMsg(data, t("profile.twinSyncFail", { err: res.status }))); return; }
    refreshTwinUi(data);
    toast(t("profile.twinSynced"));
    loadGroups();
    refreshActiveGroup();
  } catch (ex) { toast(t("profile.twinSyncFail", { err: ex.message })); }
  finally { $("pfTwinSync").disabled = false; }
}

/** 修改分身触发方式（分身已启用时即时保存并同步各公开知聚）。 */
async function updateTwinTrigger() {
  if (!state.token || !twinStatus?.enabled) return;
  const mode = $("pfTwinTrigger").value;
  try {
    const res = await fetch("/ag-ui/twin/trigger", {
      method: "POST",
      headers: { "Content-Type": "application/json", Authorization: `Bearer ${state.token}` },
      body: JSON.stringify({ triggerMode: mode }),
    });
    const data = await res.json().catch(() => null);
    if (!res.ok) { toast(errMsg(data, t("profile.twinTriggerFail", { err: res.status }))); loadTwinStatus(); return; }
    refreshTwinUi(data);
    toast(t("profile.twinTriggerUpdated"));
  } catch (ex) { toast(t("profile.twinTriggerFail", { err: ex.message })); loadTwinStatus(); }
}

/** 重新拉取当前知聚快照并应用（成员 / 话题 / 消息刷新，不依赖事件时序）。 */
async function refreshActiveGroup() {
  const gid = state.activeGroupId;
  if (!gid) return;
  try {
    const res = await fetch(`/ag-ui/group/${gid}`);
    if (!res.ok) return;
    applySnapshot(await res.json());
  } catch { /* 刷新失败不阻塞 */ }
}

/** 启用分身：服务端基于公开知聚发言生成人设并加入全部公开知聚。 */
async function enableTwin() {
  if (!state.token) { toast(t("common.loginFirst")); return; }
  $("pfTwinEnable").disabled = true;
  $("pfTwinStatus").textContent = t("profile.twinGenerating");
  try {
    const res = await fetch("/ag-ui/twin/enable", {
      method: "POST",
      headers: { "Content-Type": "application/json", Authorization: `Bearer ${state.token}` },
      body: JSON.stringify({ triggerMode: $("pfTwinTrigger").value }),
    });
    const data = await res.json().catch(() => null);
    if (!res.ok) { toast(errMsg(data, t("profile.twinEnableFail", { err: res.status }))); refreshTwinUi(null); return; }
    refreshTwinUi(data);
    toast(t("profile.twinEnabledToast"));
    loadGroups(); // 刷新知聚列表（分身已加入公开知聚，成员数变化）
    refreshActiveGroup(); // 立即刷新当前知聚成员列表（显示 🪞 分身）
  } catch (ex) { toast(t("profile.twinEnableFail", { err: ex.message })); refreshTwinUi(null); }
  finally { $("pfTwinEnable").disabled = false; }
}

/** 停用分身：删除分身并退出全部知聚。 */
async function disableTwin() {
  if (!state.token) return;
  if (!confirm(t("profile.twinDisableConfirm"))) return;
  try {
    const res = await fetch("/ag-ui/twin/disable", {
      method: "POST",
      headers: { Authorization: `Bearer ${state.token}` },
    });
    const data = await res.json().catch(() => null);
    if (!res.ok) { toast(errMsg(data, t("profile.twinDisableFail", { err: res.status }))); return; }
    refreshTwinUi(null);
    toast(t("profile.twinDisableToast"));
    loadGroups();
    refreshActiveGroup(); // 立即刷新当前知聚成员列表（移除 🪞 分身）
  } catch (ex) { toast(t("profile.twinDisableFail", { err: ex.message })); }
}

async function submitProfile() {
  const nickname = $("pfNickname").value.trim();
  const body = { nickname, personalMemoryEnabled: $("pfPersonalMemory").checked };
  // profileAvatar：null=未改动不发送；""=移除；否则=新头像 URL
  if (profileAvatar !== null) body.avatar = profileAvatar;
  try {
    const res = await fetch("/ag-ui/user/profile", {
      method: "PUT",
      headers: { "Content-Type": "application/json", Authorization: `Bearer ${state.token}` },
      body: JSON.stringify(body),
    });
    const data = await res.json().catch(() => null);
    if (!res.ok) { toast(errMsg(data, `保存失败（${res.status}）`)); return; }
    $("profileModal").classList.add("hidden");
    $("meNickname").textContent = data.nickname || state.memberId;
    state.avatar = data.avatar || null;
    state.personalMemoryEnabled = !!data.personalMemoryEnabled;
    updateAuthNickname(data.nickname); // 同步会话快照昵称（sessionStorage + localStorage）
    renderMeAvatar();
    loadUserDirectory();
    loadGroups();
    toast(t("profile.saved")); // 单例 toast：只提示一次（loadUserDirectory/loadGroups 为异步刷新，不重复提示）
  } catch (ex) { toast(t("common.saveFail", { err: ex.message })); }
}

/* ============ 创建知聚 / 添加成员：成员选择弹窗（头像 + 搜索） ============ */

let createPickOptions = []; // 创建知聚弹窗的可选成员（打开时快照，供搜索过滤）
let addPickOptions = [];    // 添加成员弹窗的可选成员

/** 成员选择项 HTML：头像（状态图标叠加）+ 名称 + 副标题 + AI 标签。 */
function pickItemHtml(m) {
  const isTwin = (m.memberId || "").startsWith("twin_");
  const statusIcon = memberStatusIconHtml(m);
  const avatar = m.avatar
    ? `<span class="member-avatar"><img src="${escapeHtml(authedAssetUrl(m.avatar))}" alt="" onerror="this.remove()" />${statusIcon}</span>`
    : statusIcon;
  const sub = m.memberType === "agent"
    ? (isTwin ? t("member.twinTip") : `${t("agent.pickPrefix")} · ${TRIGGER_LABELS[m.triggerMode] || t("agent.form.trigger.mentioned")}`)
    : t("agent.pickUser");
  return `${avatar}<span class="pick-info"><span class="pick-name">${escapeHtml(m.nickname || m.memberId)}</span><span class="pick-sub">${sub}</span></span>` +
    (!isTwin && m.memberType === "agent" ? '<span class="tag-agent">AI</span>' : "");
}

/** 渲染成员勾选列表（搜索过滤已由调用方完成）：checkbox + 头像 + 信息；onChange 在选中集变化时回调。 */
function renderMemberPick(listEl, members, selected, onChange) {
  listEl.innerHTML = "";
  if (members.length === 0) { listEl.innerHTML = `<div class="pick-empty">${escapeHtml(t("member.noMatch"))}</div>`; return; }
  for (const m of members) {
    const div = document.createElement("div");
    div.className = "pick-item" + (selected.has(m.memberId) ? " checked" : "");
    div.innerHTML = `<input type="checkbox" ${selected.has(m.memberId) ? "checked" : ""} />${pickItemHtml(m)}`;
    const setChecked = (checked) => {
      if (checked) selected.add(m.memberId); else selected.delete(m.memberId);
      div.classList.toggle("checked", checked);
      div.querySelector("input").checked = checked;
      onChange?.();
    };
    div.onclick = (e) => { if (e.target.tagName !== "INPUT") setChecked(!div.querySelector("input").checked); };
    div.querySelector("input").onchange = (e) => setChecked(e.target.checked);
    listEl.appendChild(div);
  }
}

/** 按搜索词过滤成员列表（昵称 / ID 忽略大小写）。 */
function filterPickOptions(options, q) {
  if (!q) return options;
  return options.filter((m) => (m.nickname || "").toLowerCase().includes(q) || m.memberId.toLowerCase().includes(q));
}

function renderCreatePick() {
  const q = $("createMemberSearch").value.trim().toLowerCase();
  renderMemberPick($("createMemberList"), filterPickOptions(createPickOptions, q), selectedMembers);
}

function openCreateModal() {
  selectedMembers.clear();
  createPickOptions = memberDirectory().filter((m) => m.memberId !== state.memberId);
  renderCreatePick();
  $("createGroupName").value = "";
  $("createGroupPrivate").checked = false;
  $("createMemberSearch").value = "";
  $("createConfirm").disabled = false; // 允许创建仅含知聚主的知聚
  $("createModal").classList.remove("hidden");
  $("createGroupName").focus();
}

/* ============ 知聚设置（知聚名 / 头像 / 私密） ============ */

let groupSettingsAvatar = null; // null=未改动；""=移除；url=新头像
let gsAvatarPicker = null;

function openGroupSettings() {
  const gid = state.activeGroupId;
  const g = state.groups.find((x) => x.groupId === gid);
  if (!g) return;
  // 权限：仅知聚主 / 管理员（服务端同样校验）
  const me = room(gid)?.members.find((m) => m.memberId === state.memberId);
  if (me && me.role !== "owner" && me.role !== "admin") { toast(t("gs.permEdit")); return; }
  // 解散仅知聚主可执行
  $("gsDisbandBtn").style.display = me?.role === "owner" || g.ownerId === state.memberId ? "" : "none";
  $("gsGroupName").value = g.groupName || "";
  $("gsIsPrivate").checked = !!g.isPrivate;
  groupSettingsAvatar = null; // 未改动
  gsAvatarPicker.render(g.groupAvatar || "");
  $("groupSettingsModal").classList.remove("hidden");
}

async function saveGroupSettings() {
  const gid = state.activeGroupId;
  const g = state.groups.find((x) => x.groupId === gid);
  if (!g) return;
  const groupName = $("gsGroupName").value.trim();
  if (!groupName) { toast(t("gs.nameRequired")); return; }
  const updateFields = [];
  const groupInfo = {};
  if (groupName !== g.groupName) { updateFields.push("groupName"); groupInfo.groupName = groupName; }
  const avatar = groupSettingsAvatar === null ? g.groupAvatar : (groupSettingsAvatar || null);
  if (avatar !== (g.groupAvatar || null)) { updateFields.push("groupAvatar"); groupInfo.groupAvatar = avatar; }
  const isPrivate = $("gsIsPrivate").checked;
  if (isPrivate !== !!g.isPrivate) { updateFields.push("isPrivate"); groupInfo.isPrivate = isPrivate; }
  if (updateFields.length === 0) { $("groupSettingsModal").classList.add("hidden"); return; }
  try {
    const res = await fetch("/ag-ui/group/update", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ groupId: gid, operatorId: state.memberId, updateFields, groupInfo }),
    });
    const data = await res.json().catch(() => null);
    if (!res.ok) { toast(errMsg(data, `保存失败（${res.status}）`)); return; }
    $("groupSettingsModal").classList.add("hidden");
    // 本地立即生效（GROUP_UPDATED 事件也会同步，双保险）
    if (groupInfo.groupName !== undefined) g.groupName = groupInfo.groupName;
    if (groupInfo.groupAvatar !== undefined) g.groupAvatar = groupInfo.groupAvatar;
    if (groupInfo.isPrivate !== undefined) g.isPrivate = groupInfo.isPrivate;
    renderGroupList();
    $("chatGroupName").textContent = (g.isPrivate ? "🔒 " : "") + (g.groupName || "");
    toast(t("gs.saved"));
  } catch (ex) { toast(t("common.saveFail", { err: ex.message })); }
}

/** 解散知聚（仅知聚主）：二次确认后调用 /ag-ui/group/disband，本地立即清理（GROUP_DISBANDED 事件到达后幂等处理）。 */
async function disbandGroup() {
  const gid = state.activeGroupId;
  const g = state.groups.find((x) => x.groupId === gid);
  if (!g) return;
  if (!confirm(t("gs.disbandConfirm", { name: g.groupName }))) return;
  try {
    const res = await fetch("/ag-ui/group/disband", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ groupId: gid, operatorId: state.memberId }),
    });
    const data = await res.json().catch(() => null);
    if (!res.ok) { toast(errMsg(data, t("gs.disbandFail", { err: res.status }))); return; }
    $("groupSettingsModal").classList.add("hidden");
    onDisbanded({ groupId: gid }); // 本地立即清理（事件到达后再执行一次无副作用）
    toast(t("gs.disbanded"));
  } catch (ex) { toast(t("gs.disbandFail", { err: ex.message })); }
}

async function createGroup() {
  let groupName = $("createGroupName").value.trim();
  const picked = memberDirectory().filter((m) => selectedMembers.has(m.memberId));
  // 用户不填知聚名：由 AI 按所选成员自动生成 6-12 字知聚名（需登录；演示模式无令牌则提示手动填写）
  if (!groupName) {
    if (picked.length === 0) { toast(t("create.needMemberOrName")); return; }
    if (!state.token) { toast(t("create.loginManualName")); return; }
    const btn = $("createConfirm");
    const oldText = btn.textContent;
    btn.disabled = true;
    btn.textContent = t("create.genName");
    try {
      const res = await fetch("/ag-ui/group/generate-name", {
        method: "POST",
        headers: { "Content-Type": "application/json", Authorization: `Bearer ${state.token}` },
        body: JSON.stringify({ memberNames: picked.map((m) => m.nickname) }),
      });
      const data = await res.json().catch(() => null);
      if (!res.ok) { toast(errMsg(data, t("create.genNameFail", { err: res.status }))); return; }
      groupName = data.groupName;
      $("createGroupName").value = groupName; // 回填展示
    } catch (ex) {
      toast(t("create.genNameFail2", { err: ex.message }));
      return;
    } finally {
      btn.disabled = false;
      btn.textContent = oldText;
    }
  }
  const body = {
    groupName,
    ownerId: state.memberId,
    isPrivate: $("createGroupPrivate").checked,
    memberIds: picked.map((m) => m.memberId),
    members: picked.map((m) => ({
      memberId: m.memberId, memberType: m.memberType, nickname: m.nickname,
      avatar: m.memberType === "agent" ? (m.avatar || null) : null,
    })),
  };
  try {
    const res = await fetch("/ag-ui/group/create", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(body),
    });
    if (!res.ok) {
      const err = await res.json().catch(() => null);
      toast(errMsg(err, t("create.createFail2", { err: res.status })));
      return;
    }
    const group = await res.json();

    // 为勾选的数字员工注册新知聚触发规则（协议 §6），使其在新知聚内可被触发
    for (const a of picked.filter((m) => m.memberType === "agent")) {
      try {
        await fetch(`/ag-ui/agents/register?memberId=${encodeURIComponent(state.memberId)}`, {
          method: "POST",
          headers: state.token
            ? { "Content-Type": "application/json", Authorization: `Bearer ${state.token}` }
            : { "Content-Type": "application/json" },
          body: JSON.stringify({
            agentId: a.memberId, nickname: a.nickname, groupId: group.groupId,
            triggerMode: a.triggerMode, keywords: a.keywords,
          }),
        });
      } catch { /* 单个数字员工注册失败不阻塞创建 */ }
    }

    $("createModal").classList.add("hidden");
    toast(t("create.created", { name: group.groupName }));
    await loadGroups();
    selectGroup(group.groupId);
  } catch (ex) {
    toast(t("create.createFail", { err: ex.message }));
  }
}

/* 连接状态徽标：记录当前状态 key，语言切换时据此按新语言重渲染。 */
let _connOnline = false;
let _connKey = "status.offline";
function setStatus(online, key) {
  _connOnline = online;
  _connKey = key;
  const el = $("connStatus");
  el.className = "badge " + (online ? "online" : "offline");
  el.textContent = t(key || "status.offline");
}

/* ============ 知聚与成员加载 ============ */

async function loadGroups() {
  const res = await fetch(`/ag-ui/member/${state.memberId}/groups`);
  if (!res.ok) return;
  state.groups = await res.json();
  // 未读信息（活跃度 / 未读徽标）：服务端计算为准，实时事件在此基础上增量维护
  state.groupUnread.clear();
  for (const g of state.groups) {
    state.groupUnread.set(g.groupId, {
      lastMessageAt: Number(g.lastMessageAt) || 0,
      unreadCount: Number(g.unreadCount) || 0,
      byTopic: g.unreadByTopic || {},
    });
  }
  renderGroupList();
  // 刷新完成：当前知聚话题栏同步最新未读（话题红点 / 主话题红点）
  if (state.activeGroupId) renderTopicBar();
  // 当前打开的知聚若已被解散 / 不再是成员，清空聊天区（刷新列表场景）
  if (state.activeGroupId && !state.groups.some((g) => g.groupId === state.activeGroupId)) {
    state.activeGroupId = null;
    state.activeTopicId = "main";
    resetVScroll();
    renderMembers();
    renderTopicBar();
    $("chatGroupName").textContent = t("chat.selectGroup");
    $("addMemberBtn").disabled = true;
    $("groupSettingsBtn").disabled = true;
    $("searchBtn").disabled = true;
  }
  // 登录后自动进入上次选择的知聚（一次性，手动刷新知聚列表不触发）
  if (pendingAutoEnterGroup && state.memberId && !state.activeGroupId) {
    pendingAutoEnterGroup = false;
    const last = localStorage.getItem(LastGroupKey(state.memberId));
    const g = last && state.groups.find((x) => x.groupId === last);
    if (g) selectGroup(g.groupId);
  }
}

function room(gid) {
  if (!state.rooms.has(gid)) state.rooms.set(gid, { messages: [], members: [], typing: new Set(), typingTs: new Map(), allLoaded: false, topics: [] });
  return state.rooms.get(gid);
}

/** 单知聚消息内存上限：超过 1200 条时裁剪最旧消息（含全局索引同步）。 */
const MAX_MESSAGES = 1200;
/** 单条消息流式内容累计上限（正文 / 思考各 2MB）：超限忽略后续增量并标记截断，防异常超长内容拖垮前端。 */
const STREAM_MAX_LENGTH = 2 * 1024 * 1024;

/** 消息裁剪：超限时移除最旧 excess 条并同步 msgIndex；裁剪后游标取当前首条即可继续翻页，不置 allLoaded。 */
function trimMessages(r) {
  if (!r || r.messages.length <= MAX_MESSAGES) return;
  const excess = r.messages.splice(0, r.messages.length - MAX_MESSAGES);
  for (const m of excess) state.msgIndex.delete(m.id);
}

/** 当前话题的消息（虚拟滚动 / 分页按话题隔离；系统行只在主话题显示，旧消息无 topicId 归主话题）。 */
function activeTopicMessages(r) {
  const t = state.activeTopicId || "main";
  return r.messages.filter((m) => {
    const isSys = m.sys || String(m.id || "").startsWith("sys_");
    if (isSys) return t === "main"; // 系统行无话题归属，仅主话题视图可见
    return !m.topicId || m.topicId === t; // 旧消息无 topicId 归主话题
  });
}

/* ============ 事件分发 ============ */

function handleEvent(evt) {
  switch (evt.type) {
    case "GROUP_CONNECTED":
      loadGroups();
      // 重连后自动恢复此前已订阅的知聚，避免流式事件（TEXT_MESSAGE_CONTENT 等）丢失
      if (state.subscribedGroups.size > 0) {
        send({ type: "GROUP_SUBSCRIBE", groupIds: [...state.subscribedGroups], timestamp: Date.now() });
      }
      break;
    case "GROUP_SUBSCRIBE_ACK":
      for (const gid of evt.failedGroupIds || []) state.subscribedGroups.delete(gid);
      if (evt.failedGroupIds?.length) toast(`订阅失败：${evt.failedGroupIds.join(",")}（${evt.failReason || ""}）`);
      break;
    case "GROUP_STATE_SNAPSHOT": applySnapshot(evt); break;
    case "TEXT_MESSAGE_START": onMessageStart(evt); break;
    case "TEXT_MESSAGE_CONTENT": onMessageContent(evt); break;
    case "TEXT_MESSAGE_REASONING": onMessageReasoning(evt); break;
    case "TEXT_MESSAGE_END": onMessageEnd(evt); break;
    case "TEXT_MESSAGE_RESET": onMessageReset(evt); break;
    case "TEXT_MESSAGE_ATTACHMENTS": onMessageAttachments(evt); break;
    case "TEXT_MESSAGE_PLAN": onMessagePlan(evt); break;
    case "GROUP_TYPING": onTyping(evt); break;
    case "GROUP_MESSAGE_RECALLED": onRecalled(evt); break;
    case "TOOL_CALL_START": onToolCall(evt); break;
    case "TOOL_CALL_RESULT": onToolCallResult(evt); break;
    case "AGENT_INTERACTION_REQUEST": onInteractionRequest(evt); break;
    case "AGENT_INTERACTION_RESOLVED": onInteractionResolved(evt); break;
    case "GROUP_MEMBER_JOINED":
      addSystemLine(evt.groupId, t("msg.memberJoined", { names: evt.members.map((m) => m.nickname).join("、") }));
      {
        const r = room(evt.groupId);
        for (const m of evt.members || []) {
          if (!r.members.some((x) => x.memberId === m.memberId)) r.members.push(m);
        }
        if (state.activeGroupId === evt.groupId) renderMembers();
      }
      loadGroups();
      break;
    case "GROUP_MEMBER_LEFT":
      addSystemLine(evt.groupId, evt.leaveType === "kick"
        ? t("msg.memberKicked", { names: evt.memberIds.join("、") })
        : t("msg.memberLeft", { names: evt.memberIds.join("、") }));
      if (evt.memberIds.includes(state.memberId)) {
        // 自己被移出 / 退出知聚：与解散同等清理（清空该知聚消息与索引、移除订阅与知聚列表），本地不再保留该知聚
        cleanupRoom(evt.groupId);
        break;
      }
      {
        const r = room(evt.groupId);
        r.members = r.members.filter((x) => !evt.memberIds.includes(x.memberId));
        if (state.activeGroupId === evt.groupId) renderMembers();
      }
      loadGroups();
      break;
    case "GROUP_MEMBER_UPDATED": onMemberUpdated(evt); break;
    case "GROUP_TOPIC_CREATED": onTopicCreated(evt); break;
    case "GROUP_TOPIC_DELETED": onTopicDeleted(evt); break;
    case "GROUP_TOPIC_CLEARED": onTopicCleared(evt); break;
    case "GROUP_MESSAGE_TOPIC_MOVED": onMessageTopicMoved(evt); break;
    case "GROUP_DISBANDED": onDisbanded(evt); break;
    case "GROUP_UPDATED": onGroupUpdated(evt); break;
    case "GROUP_CREATED": if (evt.members?.some((m) => m.memberId === state.memberId)) loadGroups(); break;
    case "RUN_ERROR": toast(`错误 [${evt.errorCode}]：${evt.message}`); break;
    default: break;
  }
}

function applySnapshot(evt) {
  const gid = evt.groupId;
  const r = room(gid);
  state.subscribedGroups.add(gid); // 快照到达 = 订阅已生效，同步本地订阅状态
  r.members = evt.members || [];
  r.topics = evt.topics || [];
  // 话题记忆：切知聚时 topics 尚未加载 → 快照到达后校验记忆话题仍存在则自动选中（auto：不视为主动查看，保留未读徽标）
  if (r._pendingTopic && state.activeGroupId === gid) {
    const t = r._pendingTopic;
    r._pendingTopic = null;
    if (t !== "main" && r.topics.some((x) => x.topicId === t)) selectTopic(t, { auto: true });
  }
  // 快照历史合并（按 messageId 去重；新增按时间序插入——本地已有流式中的新消息时，
  // 直接尾部追加会把旧快照消息压到新消息之后，导致消息乱序）
  const seen = new Set(r.messages.map((m) => m.id));
  const added = [];
  for (const sm of evt.latestMessages || []) {
    if (seen.has(sm.messageId)) continue;
    const m = {
      id: sm.messageId, senderId: sm.senderId, senderNickname: sm.senderNickname,
      senderType: sm.senderId?.startsWith("agent_") ? "agent" : "user",
      content: sm.content, reasoning: sm.reasoning || "", attachments: sm.attachments || [], mentions: sm.mentions || [], mentionAll: !!sm.mentionAll, agentChain: sm.agentChain || null,
      topicId: sm.topicId || "main",
      timestamp: Number(sm.timestamp) || 0,
      time: fmtTime(sm.timestamp), recalled: false, streaming: false, plan: null,
    };
    state.msgIndex.set(sm.messageId, m);
    seen.add(sm.messageId);
    added.push(m);
  }
  if (added.length > 0) {
    // 按时间戳（毫秒）比序插入：快照新增消息插到本地第一条 timestamp >= t0 的真实消息之前，
    // 没有则追加末尾。系统行（sys）无时间戳概念，不参与定位，插入后保持原有相对位置。
    // （用时间戳而非 HH:MM 显示串比序，避免跨天边界 00:01 与 23:59 字典序倒置造成乱序）
    const t0 = Number(added[0].timestamp) || 0;
    const idx = r.messages.findIndex((m) => !m.sys && (Number(m.timestamp) || 0) >= t0);
    r.messages.splice(idx < 0 ? r.messages.length : idx, 0, ...added);
    trimMessages(r); // 内存上限：超限裁剪最旧消息
  }
  // 快照固定截取最近 N 条：不足一页说明服务端已返回全部历史，否则可能还有更早消息可翻页
  r.allLoaded = (evt.latestMessages?.length ?? 0) < 50;
  if (state.activeGroupId === gid) {
    renderMembers();
    renderChatMeta(); // 成员列表就绪后刷新知聚主昵称显示
    renderTopicBar(); // 话题列表随快照到达刷新（未读徽标保留展示，进入知聚不标记已读）
    // 快照可能晚于首次渲染到达（新消息沉在视口下方）：贴近底部时重新跟随到最新
    const el = $("messages");
    if (el.scrollHeight - el.scrollTop - el.clientHeight < 120) vscroll.stickBottom = true;
    renderMessages();
  }
}

function onMessageStart(evt) {
  const r = room(evt.groupId);
  if (r.messages.some((m) => m.id === evt.messageId)) return;
  const m = {
    id: evt.messageId, senderId: evt.senderId, senderNickname: evt.senderNickname || evt.senderId,
    senderType: evt.senderType, role: evt.role, content: "", reasoning: "", attachments: evt.attachments || [],
    mentions: evt.mentions || [], mentionAll: !!evt.mentionAll, topicId: evt.topicId || "main",
    runId: evt.runId || null, // 数字员工运行 ID（「停止生成」需要）
    timestamp: Number(evt.timestamp) || Date.now(),
    time: fmtTime(evt.timestamp),
    replyTo: evt.replyToMessageId, streaming: true, recalled: false, plan: null,
  };
  r.messages.push(m);
  state.msgIndex.set(m.id, m);
  trimMessages(r); // 内存上限：超限裁剪最旧消息
  // 新消息到达：之前的“最后一条数字员工消息”不再满足刷新条件，清除其刷新按钮
  // （虚拟窗口重建时由 isLastAgentMsg 重新评估；此处覆盖 PLAIN 局部更新不重建头部的情况）
  if (state.activeGroupId === evt.groupId) {
    const el = $("messages");
    if (el) el.querySelectorAll(".regenerate-btn").forEach((b) => b.remove());
  }
  // 未读 / 已读维护：当前知聚内，当前话题的新消息视为已读（发回执），其他话题计入未读；
  // 同时刷新该知聚活跃度（lastMessageAt）→ 知聚列表按最新发言动态重排
  if (evt.groupId === state.activeGroupId && state.subscribedGroups.has(evt.groupId)) {
    const info = state.groupUnread.get(evt.groupId);
    if (info) {
      const ts = Number(evt.timestamp) || Date.now();
      if (ts > (info.lastMessageAt || 0)) {
        info.lastMessageAt = ts;
        renderGroupList();
      }
    }
    const t = m.topicId || "main";
    if (t === (state.activeTopicId || "main")) markTopicRead(evt.groupId, t, m.id);
    else bumpTopicUnread(evt.groupId, t);
  } else if (evt.groupId !== state.activeGroupId || (m.topicId || "main") !== (state.activeTopicId || "main")) {
    // 非当前视图的新消息：页面不可见时桌面通知（可见时站内徽标已提示）
    notifyNewMessage(evt, m);
  }
  // 应用内通知中心（5.4）：非当前视图的加入知聚消息 + 提及我 / 审批（在下方钩子单独处理）
  if (evt.senderId !== state.memberId) {
    const isMeMentioned = (evt.mentionAll) || (Array.isArray(evt.mentions) && evt.mentions.includes(state.memberId));
    const isCurrentView = evt.groupId === state.activeGroupId && (m.topicId || "main") === (state.activeTopicId || "main");
    const gnameStr = (state.groups.find((x) => x.groupId === evt.groupId)?.groupName) || evt.groupId;
    if (isMeMentioned) {
      addNotification("mention", window.t("notif.mentionedYou", { name: evt.senderNickname || evt.senderId }), window.t("notif.mentionBody", { group: gnameStr, content: String(m.content || "").slice(0, 80) }), { groupId: evt.groupId, topicId: m.topicId });
    } else if (!isCurrentView && evt.senderType !== "sys") {
      const label = evt.senderType === "agent" ? window.t("notif.agentSent") : window.t("notif.newMessage");
      addNotification("message", window.t("notif.messageTitle", { group: gnameStr, sender: evt.senderNickname || evt.senderId }), String(m.content || window.t("notif.bodyAttachment")).slice(0, 80), { groupId: evt.groupId, topicId: m.topicId });
    }
  }
  if (state.activeGroupId !== evt.groupId) return; // 非当前知聚不渲染
  // 是否跟随（滚动到底）由 stickBottom 决定：滚动监听只在用户停靠最底部时置位，
  // 任何上滑立即取消——避免滚轮大幅滚动时视口被反复拉回底部。
  scheduleVirtualRender();
}

function onMessageContent(evt) {
  // TEXT_MESSAGE_CONTENT 事件不含 groupId（协议 4.4），按 messageId 从全局索引定位消息
  let m = state.msgIndex.get(evt.messageId);
  const created = !m;
  if (!m) {
    // 兜底：START 事件可能因断线等丢失，收到 CONTENT 时自动创建消息，避免回复内容丢失；
    // 知聚定位优先取事件携带的 GroupId（Hub 扩展字段），缺省才回退当前活动知聚（防快速切知聚时写入错误 room）
    const r = evt.groupId ? room(evt.groupId) : room(state.activeGroupId);
    m = { id: evt.messageId, senderId: "", senderNickname: "数字员工", senderType: "agent", role: "assistant",
          content: "", reasoning: "", timestamp: null, time: fmtTime(Date.now()), replyTo: null, streaming: true, recalled: false };
    r.messages.push(m);
    state.msgIndex.set(m.id, m);
    trimMessages(r); // 内存上限：超限裁剪最旧消息
  }
  m.waiting = false; // 恢复后的首个增量到达：结束“等待确认”占位
  // 流式累计上限：正文超 2MB 后忽略后续增量并标记截断（首次截断时重建一次以显示提示）
  if (m.content.length + String(evt.delta || "").length > STREAM_MAX_LENGTH) {
    if (!m.truncated) { m.truncated = true; m._html = undefined; scheduleVirtualRender(); }
    return;
  }
  m.content += evt.delta;
  m._html = undefined; // 内容变化，渲染缓存失效
  m._bridgeParse = null; // 结构化响应解析缓存同样失效

  // 流式增量：窗口内的消息只局部更新文本节点（避免整窗重建闪烁 + 重复 Markdown 解析），
  // 行高变化由 ResizeObserver 测量刷新占位——大知聚“吐字卡顿”的根源也在这条路径上消除。
  const el = $("messages");
  const msgEl = el.querySelector(`[data-mid="${cssEsc(m.id)}"]`);
  const contentEl = msgEl ? msgEl.querySelector(".content") : null;
  if (msgEl && contentEl && !m.recalled && m.streaming) {
    contentEl.textContent = m.content;
    contentEl.classList.remove("waiting");
    // 跟随流式输出：合并到 rAF（每帧最多一次布局/滚动），避免高频增量每帧多次强制布局
    if (vscroll.stickBottom) scheduleFollow();
    return;
  }
  // END 已到仍收到迟到增量（协议乱序 / 断线补传）：流式局部更新路径已不可用，
  // 强制重建窗口使 DOM 与状态一致（重建按非流式 Markdown 渲染）
  if (!m.streaming) {
    vscroll.force = true;
    scheduleVirtualRender();
    return;
  }
  // 窗口之外：只更新状态；兜底创建的新消息安排一次渲染（是否跟随同样交给 stickBottom）
  if (created) scheduleVirtualRender();
}

/**
 * TEXT_MESSAGE_REASONING：数字员工思考过程增量（AG-UI 思考模式，独立于正文）——
 * 追加到消息的 reasoning 字段，窗口内局部更新折叠的「思考中…」块，窗口外标记重建。
 */
function onMessageReasoning(evt) {
  // 与 TEXT_MESSAGE_CONTENT 一致：事件不含 groupId，按 messageId 从全局索引定位
  let m = state.msgIndex.get(evt.messageId);
  const created = !m;
  if (!m) {
    // 兜底：START 可能因断线丢失，收到 REASONING 时自动创建消息（思考往往先于正文到达）；
    // 知聚定位优先取事件携带的 GroupId，缺省才回退当前活动知聚
    const r = evt.groupId ? room(evt.groupId) : room(state.activeGroupId);
    m = { id: evt.messageId, senderId: "", senderNickname: "数字员工", senderType: "agent", role: "assistant",
          content: "", reasoning: "", timestamp: null, time: fmtTime(Date.now()), replyTo: null, streaming: true, recalled: false };
    r.messages.push(m);
    state.msgIndex.set(m.id, m);
    trimMessages(r);
  }
  // 流式累计上限：思考内容同样限 2MB，超限忽略后续增量并标记截断
  if ((m.reasoning || "").length + String(evt.delta || "").length > STREAM_MAX_LENGTH) {
    if (!m.truncated) { m.truncated = true; m._html = undefined; scheduleVirtualRender(); }
    return;
  }
  m.reasoning = (m.reasoning || "") + evt.delta;
  m._html = undefined; // 思考变化 → 渲染缓存失效

  const el = $("messages");
  const msgEl = el.querySelector(`[data-mid="${cssEsc(m.id)}"]`);
  const th = msgEl ? msgEl.querySelector(".thinking") : null;
  if (msgEl && th && !m.recalled && m.streaming) {
    // 流式局部更新思考块：追加文本，跟随滚动交给 stickBottom
    th.querySelector(".thinking-body").textContent = m.reasoning;
    if (vscroll.stickBottom) scheduleFollow();
    return;
  }
  if (msgEl && !th && !m.recalled && m.streaming) {
    // 思考块尚未创建（思考先于正文/渲染）：插入到正文前
    const content = msgEl.querySelector(".content");
    const box = document.createElement("details");
    box.className = "thinking";
    box.open = true;
    box.innerHTML = `<summary>${t("msg.thinkingStreaming")}</summary><div class='thinking-body'></div>`;
    box.querySelector(".thinking-body").textContent = m.reasoning;
    (content ? content.parentNode : msgEl.querySelector(".body")).insertBefore(box, content);
    if (vscroll.stickBottom) scheduleFollow();
    return;
  }
  // 窗口之外 / 已结束：状态已更新，重建渲染（虚拟窗口或强制刷新）
  if (created || !m.streaming) {
    vscroll.force = true;
    scheduleVirtualRender();
  }
}

/**
 * TEXT_MESSAGE_RESET：人机交互中断时服务端清空了已回灌的中间内容——
 * 数字员工等用户反馈、运行继续结束后，最终结果再一次性回灌到这条消息。
 * 本地立即清空显示，并展示“等待确认”占位（恢复后流式内容会直接覆盖它）。
 */
function onMessageReset(evt) {
  const r = room(evt.groupId);
  const m = r.messages.find((x) => x.id === evt.messageId);
  if (!m) return;
  m.content = "";
  m.reasoning = ""; // 思考过程同样清空（中断恢复后重新思考 / 直接产出正文）
  m.waiting = true; // 状态标记：窗口外消息重建（虚拟滚动）时也显示占位
  m._html = undefined; // 内容清空，渲染缓存失效
  m._bridgeParse = null;
  if (state.activeGroupId !== evt.groupId) return;
  const msgEl = $("messages").querySelector(`[data-mid="${cssEsc(m.id)}"]`);
  if (msgEl) {
    const th = msgEl.querySelector(".thinking");
    if (th) th.remove(); // 思考块随内容一起清空
    const contentEl = msgEl.querySelector(".content");
    if (contentEl && !m.recalled && m.streaming) {
      contentEl.textContent = t("msg.waitingConfirm");
      contentEl.classList.add("waiting");
    }
  }
}

/**
 * TEXT_MESSAGE_ATTACHMENTS：数字员工消息运行中追加外部附件（AG-UI 桥接回灌）——
 * 按 URL 去重合并到消息附件，强制重渲染显示附件卡片 / 图片；快照 / 历史恢复自带持久化附件。
 */
function onMessageAttachments(evt) {
  const r = room(evt.groupId);
  const m = r.messages.find((x) => x.id === evt.messageId);
  if (!m) return;
  const known = new Set((m.attachments || []).map((a) => a.url));
  const added = (evt.attachments || []).filter((a) => a && a.url && !known.has(a.url));
  if (added.length === 0) return;
  m.attachments = [...(m.attachments || []), ...added];
  m._html = undefined; // 附件变化 → 渲染缓存失效
  if (state.activeGroupId !== evt.groupId) return;
  vscroll.force = true; // 附件追加 → 消息高度可能变化，强制重建窗口
  scheduleVirtualRender();
}

/**
 * TEXT_MESSAGE_PLAN：工作型数字员工消息结束时的任务计划回填（任务规划可视化）——
 * 把工作区 PLAN.md 的结构化步骤挂到消息上，前端渲染「计划清单 + 进度条」。
 */
function onMessagePlan(evt) {
  const r = room(evt.groupId);
  const m = r.messages.find((x) => x.id === evt.messageId);
  if (!m || !Array.isArray(evt.steps) || evt.steps.length === 0) return;
  m.plan = { title: evt.title || "", steps: evt.steps };
  m._html = undefined; // 计划变化 → 渲染缓存失效
  if (state.activeGroupId !== evt.groupId) return;
  vscroll.force = true; // 计划追加 → 消息高度可能变化，强制重建窗口
  scheduleVirtualRender();
}


function onMessageEnd(evt) {
  const r = room(evt.groupId);
  const m = r.messages.find((x) => x.id === evt.messageId);
  if (!m) return;
  m.streaming = false;
  // 思考内容以结束事件携带的完整快照为准（防抖 / 乱序场景下保证回放完整）
  m.reasoning = evt.reasoning ?? m.reasoning;
  m.agentChain = evt.agentChain ?? m.agentChain; // 技能调用链（链路可视化）
  m._html = undefined; // 结束 → 切 Markdown 渲染
  if (state.activeGroupId !== evt.groupId) return;
  // 窗口内消息立即局部更新（切 Markdown + 折叠 + 挂按钮）。
  // 注意：必须先加 clamp3 再挂按钮——流式期间渲染的 content 没有折叠类，
  // 此时测量会把 clientHeight=完整高度 误判为“不溢出”，m.long 被污染为 false 后按钮永不出现。
  const msgEl = $("messages").querySelector(`[data-mid="${cssEsc(m.id)}"]`);
  if (msgEl) {
    // 思考块：流式期间展开，结束后收起为「思考过程」并切换 Markdown 渲染（流式期间为纯文本）
    const th = msgEl.querySelector(".thinking");
    if (th) {
      th.open = false;
      const s = th.querySelector("summary");
      if (s) s.textContent = t("msg.thinkingDone");
      const thBody = th.querySelector(".thinking-body");
      if (thBody) {
        thBody.classList.add("md");
        thBody.innerHTML = renderMarkdown(m.reasoning);
      }
    }
    const content = msgEl.querySelector(".content");
    if (content) {
      content.classList.remove("streaming");
      content.classList.add("md");
      if (!m.expanded) content.classList.add("clamp3");
      // 与重建渲染一致：agent 消息先剥离结构化 JSON 附件信息（复用缓存，避免重复解析）
      if (m.senderType === "agent" && !m._bridgeParse) m._bridgeParse = parseBridgeResponse(m.content);
      const displayText = m.senderType === "agent" ? m._bridgeParse.text : m.content;
      content.innerHTML = renderMessageContent(displayText, m);
      m._html = content.innerHTML; // 缓存渲染结果
      attachToggleButton(msgEl, m);
      renderMermaidBlocks(content); // Mermaid 代码块 → 图表（异步，失败保留代码块）
    }
    // 补挂头部操作按钮（复制 / 重新回答 / 撤回）：流式期间渲染头部时这些按钮不显示（streaming=true），
    // END 后才有资格显示；PLAIN 局部更新不重建头部，这里补挂（滚动 / 刷新整表重建后由 msgDom 正常渲染）
    attachHeadActions(msgEl, m, r);
    // 流式结束：移除「停止生成」按钮（运行已结束；整表重建后 msgDom 也不会再渲染）
    const stopBtn = msgEl.querySelector(".head .stop-btn");
    if (stopBtn) stopBtn.remove();
  }
  // PLAIN 模式（整表渲染）局部更新已足够，无需整表重建（高度变化由 ResizeObserver 更新）；
  // 虚拟窗口模式才强制重建，统一处理窗口外消息与窗口跟随。
  if (activeTopicMessages(r).length > PLAIN_LIMIT) {
    vscroll.force = true;
    scheduleVirtualRender();
  }
}

const TYPING_TTL = 5000; // typing 状态超时：5 秒未刷新视为已结束

/** typing 超时清理：超过 TYPING_TTL 未刷新的成员标记为已结束（无定时器，事件 / 渲染时顺带收敛）。 */
function pruneTyping(r) {
  if (!r.typingTs || r.typingTs.size === 0) return;
  const now = Date.now();
  for (const [id, ts] of r.typingTs) {
    if (now - ts > TYPING_TTL) { r.typingTs.delete(id); r.typing.delete(id); }
  }
}

function onTyping(evt) {
  const r = room(evt.groupId);
  pruneTyping(r); // 顺带清理过期项，避免成员列表残留“正在输入”
  if (evt.isTyping) {
    r.typing.add(evt.memberId);
    r.typingTs.set(evt.memberId, Date.now()); // 记录时间戳：渲染时按 5 秒超时过滤
  } else {
    r.typing.delete(evt.memberId);
    r.typingTs.delete(evt.memberId);
  }
  renderTyping();
}

/** 撤回的本地状态与 DOM 更新（撤回者 HTTP 成功后立即调用 + GROUP_MESSAGE_RECALLED 事件到达时调用，幂等）。 */
function applyRecallLocal(groupId, messageId) {
  const r = room(groupId);
  const m = r.messages.find((x) => x.id === messageId);
  if (m) { m.recalled = true; m.streaming = false; m._html = undefined; m._bridgeParse = null; }
  if (state.activeGroupId !== groupId || !m) return;
  // PLAIN 模式（整表渲染）：局部标记撤回（显示「已撤回」占位），不整表重建；虚拟窗口模式统一重建
  if (activeTopicMessages(r).length <= PLAIN_LIMIT) {
    const msgEl = $("messages").querySelector(`[data-mid="${cssEsc(m.id)}"]`);
    if (msgEl) {
      const content = msgEl.querySelector(".content");
      if (content) {
        content.classList.add("recalled");
        content.classList.remove("streaming", "md");
        content.innerHTML = renderMessageContent(m.content, m);
      }
      const btn = msgEl.querySelector(".toggle-btn");
      if (btn) btn.remove();
      const ib = msgEl.querySelector(".interaction-block");
      if (ib) ib.remove(); // 撤回：嵌入的审批卡片一并移除
      // 撤回后头部操作按钮（复制 / 重新回答 / 撤回）一并隐藏
      msgEl.querySelectorAll(".head .copy-btn, .head .regenerate-btn, .head .recall-btn")
        .forEach((b) => b.remove());
    }
    return; // addSystemLine 已调度增量渲染（系统行追加）
  }
  vscroll.force = true;
  scheduleVirtualRender();
}

function onRecalled(evt) {
  applyRecallLocal(evt.groupId, evt.messageId);
  // 重新生成场景：旧回答被撤回是预期内的中间步骤，不打扰用户（不显示“消息已撤回”系统行）
  if (suppressedRecallMessageIds.delete(evt.messageId)) return;
  addSystemLine(evt.groupId, t("msg.recalledNotice"));
}

/** 重新生成时被抑制的“撤回”提示：regenerate 发起时记录旧回答 id，onRecalled 命中则不显示系统撤回行。 */
const suppressedRecallMessageIds = new Set();

/** 工具调用行 DOM（msgDom 与 onToolCall 局部更新共用）；兼容旧数据（toolCalls 为字符串数组）。
 * 简洁展示：工具开始「🔧 xxx 调用中…」，结果到达后整行隐藏（含工具名）。 */
function toolCallElement(tc) {
  const t = typeof tc === "string" ? { id: null, name: tc, done: false } : tc;
  const div = document.createElement("div");
  div.className = "tool-call";
  if (t.id) div.dataset.toolcallId = t.id;
  div.textContent = `🔧 ${window.t("msg.toolCalling", { name: t.name || "tool" })}`;
  return div;
}

function createToolCallsWrap(msgEl) {
  const wrap = document.createElement("div");
  wrap.className = "tool-calls";
  msgEl.querySelector(".body").appendChild(wrap);
  return wrap;
}

function onToolCall(evt) {
  const r = room(evt.groupId);
  const m = r.messages.find((x) => x.id === evt.parentMessageId);
  if (!m) { addSystemLine(evt.groupId, t("msg.toolCallNotice", { name: evt.toolCallName })); return; }
  m.toolCalls = m.toolCalls || [];
  m.toolCalls.push({ id: evt.toolCallId, name: evt.toolCallName || "tool", done: false });
  if (state.activeGroupId !== evt.groupId) return;
  // PLAIN 模式（整表渲染）：工具行局部插入，增量更新不重建整表；虚拟窗口模式统一重建
  if (activeTopicMessages(r).length <= PLAIN_LIMIT) {
    const msgEl = $("messages").querySelector(`[data-mid="${cssEsc(m.id)}"]`);
    if (msgEl) {
      const wrap = msgEl.querySelector(".body > .tool-calls") || createToolCallsWrap(msgEl);
      wrap.appendChild(toolCallElement(m.toolCalls[m.toolCalls.length - 1]));
      return;
    }
  }
  vscroll.force = true;
  scheduleVirtualRender();
}

/** 工具执行结果（TOOL_CALL_RESULT）：调用行整行隐藏（工具名也不保留）。 */
function onToolCallResult(evt) {
  const r = room(evt.groupId);
  const m = r.messages.find((x) => x.id === evt.parentMessageId);
  if (!m) return;
  const tc = (m.toolCalls || []).find((t) => t.id === evt.toolCallId);
  if (tc) tc.done = true;
  if (state.activeGroupId !== evt.groupId || !tc) return;
  const msgEl = $("messages").querySelector(`[data-mid="${cssEsc(m.id)}"]`);
  const el = msgEl ? msgEl.querySelector(`.tool-call[data-toolcall-id="${cssEsc(evt.toolCallId)}"]`) : null;
  if (msgEl && el) {
    el.remove();
    // 工具行清空后移除空容器，避免残留占位
    const wrap = msgEl.querySelector(".body > .tool-calls");
    if (wrap && !wrap.children.length) wrap.remove();
    return;
  }
  vscroll.force = true;
  scheduleVirtualRender();
}

/**
 * AGENT_INTERACTION_REQUEST：数字员工运行中断，请求人机交互（工具审批）。
 * 审批卡片嵌入数字员工回复消息内部（interaction-block）：仅触发者（targetMemberId）能看到批准 / 拒绝按钮，
 * 其他成员只读等待；决策后卡片就地更新状态，运行恢复后内容继续追加到同一消息。
 */
function onInteractionRequest(evt) {
  const r = room(evt.groupId);
  const m = r.messages.find((x) => x.id === evt.messageId);
  if (!m) { addSystemLine(evt.groupId, t("msg.interactionRequest", { name: evt.toolName || t("msg.toolCall") })); return; } // 兜底：消息未找到
  if (m.interaction && m.interaction.interruptId === evt.interruptId) return; // 幂等
  m.interaction = {
    interruptId: evt.interruptId,
    toolName: evt.toolName,
    toolArguments: evt.toolArguments || null,
    message: evt.message || "",
    kind: evt.kind || "approval", // approval（工具审批）/ input（请求用户输入）
    inputField: evt.inputField || null,
    responseSchema: evt.responseSchema || null, // input 型：完整 JSON Schema（渲染单选 / 多选 / 数字 / 多字段表单）
    questions: evt.questions || null, // 外部 question 工具结构化问题（逐题渲染选项，答案按问题顺序回传）
    targetMemberId: evt.targetMemberId,
    canDecide: evt.targetMemberId === state.memberId,
    resolved: false,
    decision: null,
  };
  m.waiting = false; // 卡片替代“等待确认”占位
  m._html = undefined; // 内容区无变化，但渲染缓存与卡片块独立，无需清（保守清一次）
  // 应用内通知中心（5.4）：当前用户待处理的人机交互（审批 / 输入）入通知，含系统兜底
  if (m.interaction.canDecide) {
    const actKey = m.interaction.kind === "input" ? "itx.notif.input" : "itx.notif.approve";
    const toolName = m.interaction.toolName || t("msg.toolCall");
    const groupName = (state.groups.find((x) => x.groupId === evt.groupId)?.groupName) || evt.groupId;
    addNotification("approval",
      t("itx.notif.title", { act: t(actKey), tool: toolName }),
      t("itx.notif.body", { group: groupName }),
      { groupId: evt.groupId });
  }
  if (state.activeGroupId !== evt.groupId) return;
  const msgEl = $("messages").querySelector(`[data-mid="${cssEsc(m.id)}"]`);
  if (!msgEl) { scheduleVirtualRender(); return; } // 窗口外：状态已挂，滚动到时由 msgDom 渲染卡片块
  const contentEl = msgEl.querySelector(".content");
  if (contentEl) { contentEl.classList.remove("waiting"); contentEl.textContent = ""; }
  const block = msgEl.querySelector(".interaction-block");
  if (block) { replaceInteractionBlock(block, m); return; }
  const div = document.createElement("div");
  div.className = "interaction-block";
  div.innerHTML = renderInteractionCard(m);
  bindInteractionButtons(div, m);
  if (contentEl) contentEl.after(div);
  else msgEl.querySelector(".body").appendChild(div);
}

/** 以最新卡片状态替换消息内的卡片块（局部更新，需重新绑定按钮点击）。 */
function replaceInteractionBlock(block, m) {
  const div = document.createElement("div");
  div.className = "interaction-block";
  div.innerHTML = renderInteractionCard(m);
  bindInteractionButtons(div, m);
  block.replaceWith(div);
}

/** 绑定审批卡片操作按钮（msgDom 重建与局部插入/替换共用）：input 型 → 提交输入；approval 型 → 批准 / 批量批准 / 拒绝。 */
function bindInteractionButtons(container, m) {
  container.querySelectorAll(".itx-btn").forEach((btn) => {
    if (btn.dataset.act === "submit") { btn.onclick = () => submitInteractionInput(container, m); return; }
    if (btn.dataset.act === "approveAll") { btn.onclick = () => resolveInteraction(m, true, null, null, true); return; }
    btn.onclick = () => resolveInteraction(m, btn.dataset.act === "approve");
  });
}

/** 归一化交互 responseSchema：顶层单字段（单选 enum / 多选 array / 纯 string / number / boolean，无 properties）
 * 包装为 object properties[inputField]，统一走 schemaFieldHtml 渲染；多字段对象保持原样。 */
function interactionSchema(itx) {
  const s = itx.responseSchema;
  if (!s || typeof s !== "object") return null;
  if (s.type !== "object" || !s.properties || Object.keys(s.properties).length === 0) {
    const f = itx.inputField || "answer";
    return { type: "object", properties: { [f]: s }, required: [f] };
  }
  return s;
}

/** 提交用户输入型交互（kind=input/choice/multi_choice）：按 responseSchema 收集表单字段值（单选 / 多选 / 数字 / 多字段），无 schema 时取单文本输入框。 */
function submitInteractionInput(container, m) {
  const itx = m.interaction;
  // 结构化问题（外部 question 工具，如 OpenCode）：逐题收集答案 → { inputField: [[q1答案], [q2a, q2b], [q3答案]] }
  // 格式与 OpenCode question API 一致：answers = list[list[str]]，每道题对应一个字符串数组（单选 = 单元素数组，多选 = 多元素）
  if (itx.questions && itx.questions.length) {
    const answers = [];
    for (let qi = 0; qi < itx.questions.length; qi++) {
      const q = itx.questions[qi];
      if (q.multiple) {
        const picked = [...container.querySelectorAll(`.itx-q-opt:checked[data-q="${qi}"]`)].map((c) => c.value);
        if (picked.length === 0) { toast(t("itx.answerQuestionMulti", { n: qi + 1 })); return; }
        answers.push(picked); // 多选：多个 label 的数组
      } else {
        const opt = container.querySelector(`.itx-q-opt:checked[data-q="${qi}"]`);
        const text = container.querySelector(`.itx-q-text[data-q="${qi}"]`);
        const val = opt ? opt.value : (text ? text.value.trim() : "");
        if (!val) { toast(t("itx.answerQuestion", { n: qi + 1 })); return; }
        answers.push([val]); // 单选：单元素数组
      }
    }
    resolveInteraction(m, true, undefined, { [itx.inputField || "answers"]: answers });
    return;
  }
  const schema = interactionSchema(itx);
  const hasForm = !!(schema && schema.type === "object" && schema.properties && Object.keys(schema.properties).length > 0);
  let value = "";
  let payload = null;
  if (hasForm) {
    payload = collectSchemaPayload(container);
    // 必填校验：schema.required 中的字段必须非空
    const required = Array.isArray(schema.required) ? schema.required : [];
    for (const f of required) {
      const v = payload[f];
      if (v === undefined || v === null || v === "") { toast(t("itx.requiredFields")); return; }
    }
    if (Object.keys(payload).length === 0) { toast(t("itx.enterContent")); return; }
  } else {
    value = (container.querySelector(".itx-text")?.value || "").trim();
    if (!value) { toast(t("itx.enterText")); inputElFocus(container); return; }
  }
  resolveInteraction(m, true, value, payload);
}

function inputElFocus(container) {
  const el = container.querySelector(".itx-text");
  if (el) el.focus();
}

/** 按 responseSchema 渲染输入表单字段控件：boolean → 是/否；string+enum → 下拉单选；array+items.enum → 勾选多选；
 * integer/number → 数字输入；其余 → 文本输入。 */
function schemaFieldHtml(key, def, required) {
  const type = def?.type || "string";
  const title = def?.title || key;
  const label = `<span class="itx-label">${escapeHtml(title)}${required ? ' <b style="color:#f44336">*</b>' : ""}</span>`;
  if (type === "boolean") {
    return `<label class="itx-field">${label}<select class="itx-schema-field" data-field="${escapeHtml(key)}">` +
      `<option value="true">${t("itx.booleanYes")}</option><option value="false">${t("itx.booleanNo")}</option></select></label>`;
  }
  if (type === "array" && Array.isArray(def.items?.enum)) {
    const opts = def.items.enum.map((v) =>
      `<label class="itx-check"><input type="checkbox" class="itx-schema-check" value="${escapeHtml(String(v))}" /> ${escapeHtml(String(v))}</label>`).join("");
    return `<div class="itx-field">${label}<span class="itx-hint">${t("itx.multiSelect")}</span><div class="itx-checks" data-field="${escapeHtml(key)}">${opts}</div></div>`;
  }
  if (type === "string" && Array.isArray(def.enum)) {
    const opts = def.enum.map((v) => `<option value="${escapeHtml(String(v))}">${escapeHtml(String(v))}</option>`).join("");
    return `<label class="itx-field">${label}<select class="itx-schema-field" data-field="${escapeHtml(key)}">${opts}</select></label>`;
  }
  const inputType = type === "integer" || type === "number" ? "number" : "text";
  return `<label class="itx-field">${label}<input class="itx-schema-field" type="${inputType}" data-field="${escapeHtml(key)}" placeholder="${escapeHtml(def?.description || "")}" /></label>`;
}

/** 收集表单字段值 → payload 对象（值统一为字符串：多选以逗号分隔，后端按 schema 拆数组 / 转数值）。 */
function collectSchemaPayload(container) {
  const payload = {};
  container.querySelectorAll(".itx-schema-field").forEach((el) => {
    payload[el.dataset.field] = el.value;
  });
  container.querySelectorAll(".itx-checks").forEach((wrap) => {
    const vals = [...wrap.querySelectorAll("input:checked")].map((c) => c.value);
    payload[wrap.dataset.field] = vals.join(",");
  });
  return payload;
}

/** 任务计划可视化卡片：工作型数字员工消息结束时，把其工作区 PLAN.md 的步骤渲染为带勾选清单 + 进度条的计划卡。 */
function renderPlanCard(plan) {
  const steps = plan.steps || [];
  const done = steps.filter((s) => s.done).length;
  const pct = steps.length ? Math.round((done / steps.length) * 100) : 0;
  return `<div class="plan-card-head">📋 ${plan.title ? `<b>${escapeHtml(plan.title)}</b>` : t("itx.planCardTitle")}<span class="plan-progress-txt">${done}/${steps.length}（${pct}%）</span></div>`
    + `<div class="plan-progress"><div class="plan-progress-bar" style="width:${pct}%"></div></div>`
    + `<ul class="plan-steps">${steps.map((s) =>
        `<li class="${s.done ? "done" : ""}"><span class="plan-check">${s.done ? "✅" : "⬜"}</span><span class="plan-step-text">${escapeHtml(s.text || "")}</span></li>`).join("")}</ul>`;
}

/**
 * 技能调用链（链路可视化）卡片：把嵌套的 ChainNode 树渲染为逐层缩进的“智能体链”，
 * 每层显示：触发技能名 + 目标数字员工名 + 传入请求；结果在可折叠条目内展示（点开查看上下文）。
 * 输入为后端序列化的 JSON 文本（camelCase：agentId/agentNickname/skillId/query/result/children）。
 */
function renderChainCard(chainJson) {
  let root;
  try { root = typeof chainJson === "string" ? JSON.parse(chainJson) : chainJson; } catch { return ""; }
  if (!root) return "";
  const renderNode = (node, depth) => {
    if (!node) return "";
    const pad = depth * 16;
    const hasSub = node.children && node.children.length > 0;
    const isRoute = node.kind === "assignment" || node.kind === "escalation";
    const kindTag = node.kind === "assignment"
      ? `<span class="chain-kind chain-kind-assign">${t("msg.chainAssign")}</span>`
      : (node.kind === "escalation"
        ? `<span class="chain-kind chain-kind-esc">${t("msg.chainEscalate")}</span>`
        : (node.skillId ? `<span class="chain-kind">${t("msg.chainSkill")}</span>` : ""));
    const label = node.skillId
      ? `${escapeHtml(node.skillId)} <span class="chain-arrow">→</span> ${escapeHtml(node.agentNickname || node.agentId)}`
      : `${escapeHtml(node.agentNickname || node.agentId)}`;
    const q = node.query ? `<div class="chain-query">💬 ${escapeHtml(node.query)}</div>` : "";
    const result = node.result ? `<div class="chain-result"><b>${t("msg.chainResult")}</b> ${escapeHtml(node.result)}</div>` : "";
    const sub = hasSub ? `<ul class="chain-children">${node.children.map((c) => renderNode(c, depth + 1)).join("")}</ul>` : "";
    return `<li class="chain-node" style="margin-left:${pad}px">`
      + `<div class="chain-row">${hasSub ? "▸" : "•"} ${kindTag} <span class="chain-label">${label}</span></div>`
      + (q ? q : "") + (result ? result : "") + sub + `</li>`;
  };
  // 顶层调用：若根是宿主智能体，其 children 才是实际技能调用（根自身行已由消息作者体现，这里从技能开始）
  const kids = root.children && root.children.length ? root.children : [root];
  if (kids.length === 0) return "";
  return `<details class="chain-card" open><summary>🧩 ${t("msg.chainTitle")} <span class="chain-count">${kids.length}</span></summary><ul class="chain-tree">${kids.map((n) => renderNode(n, 0)).join("")}</ul></details>`;
}

/** 渲染人机交互卡片：approval（工具审批）→ 批准 / 拒绝按钮；input / choice / multi_choice（请求输入 / 单选 / 多选）→ 按 responseSchema 渲染表单或单输入框。 */
function renderInteractionCard(m) {
  const itx = m.interaction;
  const isInput = itx.kind !== "approval"; // input / choice / multi_choice 均为输入型
  // 工具参数：approval / input 型均显示（外部 question 工具的问题与选项常在参数里，供用户判断要输入什么）；空对象不显示
  const argsHtml = itx.toolArguments && Object.keys(itx.toolArguments).length
    ? `<pre class="itx-args">${escapeHtml(JSON.stringify(itx.toolArguments, null, 2))}</pre>`
    : "";
  const schema = interactionSchema(itx);
  const hasForm = !!(schema && schema.type === "object" && schema.properties && Object.keys(schema.properties).length > 0);
  const formHtml = isInput && hasForm
    ? `<div class="itx-form">${Object.entries(schema.properties).map(([k, d]) =>
        schemaFieldHtml(k, d, Array.isArray(schema.required) && schema.required.includes(k))).join("")}</div>`
    : "";
  // 结构化问题（外部 question 工具，如 OpenCode）：逐题渲染选项（multiple → 勾选多选；否则单选）或无选项时的文本输入，优先于 schema 表单
  // radio/checkbox name 以消息 ID 做前缀：多个卡片共存时避免文档级同名互斥串扰
  const qCtlName = (qi) => `itxq_${m.id}_${qi}`;
  const qHtml = isInput && itx.questions && itx.questions.length
    ? `<div class="itx-questions">${itx.questions.map((q, qi) => {
        const ctlType = q.multiple ? "checkbox" : "radio";
        const opts = (q.options || []).map((o) =>
          `<label class="itx-check itx-q-opt-label"><input type="${ctlType}" class="itx-q-opt" name="${qCtlName(qi)}" data-q="${qi}" value="${escapeHtml(o.label)}" /> ${escapeHtml(o.label)}${o.description ? `<span class="itx-hint">— ${escapeHtml(o.description)}</span>` : ""}</label>`).join("");
        return `<div class="itx-q"><div class="itx-q-title">${qi + 1}. ${q.header ? `<span class="itx-q-header">[${escapeHtml(q.header)}]</span> ` : ""}${escapeHtml(q.question)}${q.multiple ? `<span class="itx-hint">${t("itx.multiSelect")}</span>` : ""}</div>` +
          (opts ? `<div class="itx-q-opts">${opts}</div>` : `<input class="itx-q-text" data-q="${qi}" type="text" placeholder="${escapeHtml(t("itx.questionAnswerPh", { n: qi + 1 }))}" maxlength="500" />`) +
          `</div>`;
      }).join("")}</div>`
    : "";
  // 工具行：approval 恒显示；input 型在外部服务提供工具名（非 unknown）时同样显示，明确向哪个工具输入（如 question）
  const toolName = itx.toolName && itx.toolName !== "unknown" ? itx.toolName : "";
  const toolLine = toolName ? `<div class="itx-tool">${t("itx.toolDesc")}<code>${escapeHtml(toolName)}</code></div>` : "";
  let actions = "";
  if (itx.resolved) {
    actions = `<div class="itx-status ${itx.decision ? "ok" : "no"}">${
      isInput
        ? t("itx.submittedInput")
        : (itx.decision ? t("itx.approved") : t("itx.rejected"))
    }</div>`;
  } else if (itx.canDecide) {
    if (isInput) {
      // 结构化问题卡片优先，其次 schema 表单；两者都没有时用单输入框（占位符带上输入字段名）
      const hint = itx.inputField ? t("itx.inputFieldPh", { field: itx.inputField }) : t("itx.inputPh");
      actions = `<div class="itx-input">${qHtml || formHtml}` +
        ((qHtml || hasForm) ? "" : `<input class="itx-text" type="text" placeholder="${escapeHtml(hint)}" maxlength="500" />`) +
        `<div class="itx-submit-row"><button class="itx-btn submit" data-act="submit">${t("itx.submit")}</button></div></div>`;
    } else {
      actions = `<div class="itx-actions">
        <button class="itx-btn approve" data-act="approve">${t("itx.approve")}</button>
        <button class="itx-btn approve-all" data-act="approveAll" title="${escapeHtml(t("itx.approveAllTip"))}">${t("itx.approveAll")}</button>
        <button class="itx-btn reject" data-act="reject">${t("itx.reject")}</button>
      </div>`;
    }
  } else {
    const verb = isInput ? t("itx.inputVerb") : t("itx.confirmVerb");
    actions = `<div class="itx-status">${t("itx.waiting", { name: escapeHtml(memberName(itx.targetMemberId)), text: verb })}</div>`;
  }
  return `<div class="interaction-card">
    <div class="itx-title">${isInput ? t("itx.cardAskInput") : t("itx.cardAskConfirm")}</div>
    ${itx.questions && itx.questions.length ? "" : `<div class="itx-message">${escapeHtml(itx.message || "")}</div>`}
    ${toolLine}
    ${argsHtml}
    ${actions}
  </div>`;
}

/** 触发者决策：approval 型批准 / 拒绝（approveAll=true 表示批准本次运行后续全部操作），input 型提交用户输入（inputText）或按 schema 提交 payload；经 WS 上行恢复被中断的数字员工运行。 */
function resolveInteraction(m, approved, inputText, payload, approveAll) {
  const itx = m.interaction;
  if (!itx || itx.resolved) return;
  // 断网检测：未连接时仅提示并保留卡片（服务端交互仍在悬挂，等待重连后再决策）
  if (!state.ws || state.ws.readyState !== WebSocket.OPEN) {
    toast(t("msg.connLostDecision"));
    return;
  }
  send({
    type: "AGENT_INTERACTION_RESOLVE",
    groupId: state.activeGroupId,
    interruptId: itx.interruptId,
    approved,
    input: inputText || undefined,
    payload: payload || undefined,
    approveAll: !!approveAll,
    memberId: state.memberId,
  });
  // 发送成功后才本地置为已决策并隐藏卡片，避免断网时卡片消失而服务端交互悬挂
  itx.resolved = true;
  itx.decision = approved;
  itx.inputText = inputText || null;
  itx.approveAll = !!approveAll;
  m._html = undefined; // 卡片状态变化，强制重渲染
  const el = $("messages").querySelector(`[data-mid="${cssEsc(m.id)}"]`);
  if (el) {
    const block = el.querySelector(".interaction-block");
    if (block) block.remove(); // 做出选择后隐藏审批块（恢复内容继续流式显示）
  }
}

/**
 * AGENT_INTERACTION_RESOLVED：触发者已决策，服务端全知聚广播——
 * 嵌入在数字员工回复内的卡片同步更新为「已批准 / 已拒绝」（决策者本人由本地回显 + 本事件双保险，幂等）。
 */
function onInteractionResolved(evt) {
  const r = room(evt.groupId);
  const m = r.messages.find((x) => x.interaction && x.interaction.interruptId === evt.interruptId);
  if (!m || !m.interaction || m.interaction.resolved) return;
  m.interaction.resolved = true;
  m.interaction.decision = evt.approved;
  m.interaction.decidedBy = evt.memberId;
  m.interaction.inputText = evt.input || m.interaction.inputText; // input 型：其他成员同步看到已提交内容
  m._html = undefined; // 强制重渲染卡片状态
  if (state.activeGroupId !== evt.groupId) return;
  const el = $("messages").querySelector(`[data-mid="${cssEsc(m.id)}"]`);
  if (el) {
    const block = el.querySelector(".interaction-block");
    if (block) block.remove(); // 决策后隐藏审批块（全知聚同步）
  }
}

function onMemberUpdated(evt) {
  const r = room(evt.groupId);
  const m = r.members.find((x) => x.memberId === evt.memberId);
  if (m) Object.assign(m, evt.memberInfo);
  if (evt.updateFields?.includes("onlineStatus")) {
    addSystemLine(evt.groupId, evt.memberInfo.onlineStatus === "online"
      ? t("msg.memberOnline", { name: m?.nickname || evt.memberId })
      : t("msg.memberOffline", { name: m?.nickname || evt.memberId }));
  }
  if (state.activeGroupId === evt.groupId) {
    renderMembers();
    if (evt.memberId === state.groups.find((g) => g.groupId === evt.groupId)?.ownerId) renderChatMeta();
  }
}

/** 知聚内新建话题：本地登记并刷新话题栏（消息走事件流无需额外处理）。 */
function onTopicCreated(evt) {
  const r = room(evt.groupId);
  if (evt.topic && !r.topics.some((t) => t.topicId === evt.topic.topicId)) r.topics.push(evt.topic);
  if (state.activeGroupId === evt.groupId) renderTopicBar();
}

/** 删除某话题下消息并同步全局索引（话题删除事件与本地删除成功后共用，幂等）。 */
function dropTopicMessages(r, topicId) {
  r.messages = r.messages.filter((m) => {
    if (m.topicId === topicId) { state.msgIndex.delete(m.id); return false; }
    return true;
  });
}

/** GROUP_TOPIC_DELETED：移除本地话题及其下消息；当前正在该话题时切回主话题。 */
function onTopicDeleted(evt) {
  const r = room(evt.groupId);
  r.topics = (r.topics || []).filter((t) => t.topicId !== evt.topicId);
  dropTopicMessages(r, evt.topicId); // 话题下聊天记录一并清除（含全局索引，避免孤儿对象与 CONTENT 兜底误命中）
  // 话题记忆同步清理：被删话题不再是记忆目标（下次加入知聚回主话题）
  if (state.topicMemory.get(evt.groupId) === evt.topicId) {
    state.topicMemory.delete(evt.groupId);
    saveTopicMemory(state.memberId);
  }
  if (state.activeGroupId === evt.groupId) {
    if (state.activeTopicId === evt.topicId) {
      state.activeTopicId = "main";
      r.allLoaded = false;
      vscroll.force = true;
      scheduleVirtualRender();
    }
    renderTopicBar();
  }
}

/** GROUP_TOPIC_CLEARED：某话题聊天记录被清空（话题保留）——移除本地该话题消息并刷新视图。 */
function onTopicCleared(evt) {
  const r = room(evt.groupId);
  dropTopicMessages(r, evt.topicId); // 含全局索引，避免孤儿对象与 CONTENT 兜底误命中
  if (state.activeGroupId === evt.groupId) {
    r.allLoaded = false; // 消息清空后可重新加载更早（服务端已无历史）
    vscroll.force = true;
    scheduleVirtualRender();
  }
}

/** 知聚已解散 / 自己被移出或退出知聚：清理该知聚本地状态（消息与索引、房间、订阅、知聚列表），并退出当前视图。 */
function cleanupRoom(gid) {
  const r = state.rooms.get(gid);
  if (r) for (const m of r.messages) state.msgIndex.delete(m.id);
  state.rooms.delete(gid);
  state.subscribedGroups.delete(gid);
  state.groups = state.groups.filter((g) => g.groupId !== gid);
  if (state.activeGroupId === gid) {
    state.activeGroupId = null;
    resetVScroll();
    renderGroupList();
    renderMembers();
    renderTopicBar();
    $("chatGroupName").textContent = t("chat.selectGroup");
    $("addMemberBtn").disabled = true;
    $("groupSettingsBtn").disabled = true;
    $("searchBtn").disabled = true;
    $("discussBtn").disabled = true;
  }
}

function onDisbanded(evt) {
  addSystemLine(evt.groupId, t("msg.groupDisbanded"));
  cleanupRoom(evt.groupId);
}

function onGroupUpdated(evt) {
  const g = state.groups.find((x) => x.groupId === evt.groupId);
  if (g) {
    if (evt.groupInfo?.groupName) g.groupName = evt.groupInfo.groupName;
    if (evt.groupInfo?.groupAvatar !== undefined) g.groupAvatar = evt.groupInfo.groupAvatar || null;
    if (evt.groupInfo?.isPrivate !== undefined) g.isPrivate = evt.groupInfo.isPrivate;
  }
  renderGroupList();
  if (state.activeGroupId === evt.groupId)
    $("chatGroupName").textContent = (g?.isPrivate ? "🔒 " : "") + (evt.groupInfo?.groupName || g?.groupName || "");
}

function addSystemLine(gid, text) {
  const r = room(gid);
  const m = { id: "sys_" + Math.random().toString(36).slice(2), sys: text, time: fmtTime(Date.now()) };
  r.messages.push(m);
  trimMessages(r); // 内存上限：超限裁剪最旧消息
  if (state.activeGroupId !== gid) return;
  // 是否跟随交给 stickBottom（用户是否停靠底部），见 onMessageStart 注释
  scheduleVirtualRender();
}

/* ============ 渲染 ============ */

function renderGroupList() {
  const el = $("groupList");
  el.innerHTML = "";
  // 按活跃度排序：最后发言（lastMessageAt）最新的知聚排在最前，无消息的排最后。
  // 实时值取自 state.groupUnread（loadGroups 初始化，onMessageStart 收到消息时增量更新），排序随新消息动态变化
  const sorted = [...state.groups].sort((a, b) =>
    (state.groupUnread.get(b.groupId)?.lastMessageAt || 0) - (state.groupUnread.get(a.groupId)?.lastMessageAt || 0));
  for (const g of sorted) {
    const div = document.createElement("div");
    div.className = "group-item" + (g.groupId === state.activeGroupId ? " active" : "");
    const unread = state.groupUnread.get(g.groupId)?.unreadCount || 0;
    const avatar = g.groupAvatar
      ? `<span class="group-avatar"><img src="${escapeHtml(authedAssetUrl(g.groupAvatar))}" alt="" onerror="this.remove()" /></span>`
      : `<span class="icon">👥</span>`;
    div.innerHTML = avatar + `<span>${g.isPrivate ? "🔒 " : ""}${escapeHtml(g.groupName)}</span>` +
      (unread > 0 ? `<span class="unread-badge" title="${escapeHtml(t("list.unread", { count: unread }))}">${unread > 99 ? "99+" : unread}</span>` : "") +
      `<span class="count">${Number(g.memberCount) || 0}</span>`;
    div.onclick = () => selectGroup(g.groupId);
    el.appendChild(div);
  }
}

/* ============ 未读提示与已读回执 ============ */

/** 未读徽标元素（知聚列表 / 话题栏共用）。 */
function unreadBadgeEl(count, tip) {
  const b = document.createElement("span");
  b.className = "unread-badge";
  b.textContent = count > 99 ? "99+" : String(count);
  b.title = t("list.unreadTitle", { count, label: tip || t("list.unreadLabel") });
  return b;
}

/** 话题未读红点：未读时显示醒目小圆点（悬停提示条数），已读后随渲染消失。 */
function unreadDotEl(count, tip) {
  const d = document.createElement("span");
  d.className = "unread-dot";
  d.title = t("list.unreadTitle", { count, label: tip || t("list.unreadLabel") });
  return d;
}

/** 发送已读回执（服务端落读位点，供知聚列表 / 话题未读计算）；仅需登录态，失败静默。 */
function sendReadReceipt(gid, topicId, messageId) {
  if (!gid || !messageId || !state.token) return;
  fetch("/ag-ui/group/message/read", {
    method: "POST",
    headers: { "Content-Type": "application/json", Authorization: `Bearer ${state.token}` },
    body: JSON.stringify({ groupId: gid, memberId: state.memberId, readMessageId: messageId }),
  }).catch(() => {});
}

/** 全部知聚未读总数（供浏览器标签页标题提示）。 */
function totalUnreadCount() {
  let n = 0;
  for (const info of state.groupUnread.values()) n += info.unreadCount || 0;
  return n;
}

/** 未读数写入 document.title：离开页面也能在标签页看到新消息提示。 */
function updateDocTitle() {
  const n = totalUnreadCount();
  document.title = n > 0 ? `(${n}) ${t("brand.name")}` : t("brand.name");
}

/** 浏览器桌面通知：页面不可见且非当前视图的新消息才通知（站内徽标已覆盖可见场景）；自己发的消息不通知。 */
function notifyNewMessage(evt, m) {
  try {
    if (!document.hidden) return; // 页面可见：站内未读徽标已提示，避免打扰
    if (evt.senderId === state.memberId) return;
    if (!("Notification" in window)) return;
    if (Notification.permission === "granted") {
      const n = new Notification(t("notif.messageFrom", { name: evt.senderNickname || evt.senderId }), {
        body: String(m.content || "").slice(0, 100),
        tag: evt.groupId,
      });
      n.onclick = () => { window.focus(); n.close(); };
    } else if (Notification.permission === "default") {
      Notification.requestPermission().catch(() => {}); // 浏览器可能要求用户手势，被忽略不影响功能
    }
  } catch { /* 通知失败忽略 */ }
}

/* ============ 应用内通知中心（5.4） ============ */

/** 新增应用内通知；页面隐藏时同步发系统桌面通知。 */
function addNotification(type, title, body, opts = {}) {
  const n = {
    id: "n" + (++state.notifSeq),
    type, // mention / approval / message / reconnect / info
    title, body,
    groupId: opts.groupId || null,
    topicId: opts.topicId || null,
    ts: Date.now(),
    read: false,
    icon: opts.icon || notifIcon(type),
  };
  state.notifications.unshift(n);
  if (state.notifications.length > 100) state.notifications.pop(); // 上限：仅保留最近 100 条
  renderNotifications();
  // 页面隐藏时用系统桌面通知兜底（站内面板可见时不打扰）
  if (document.hidden) showSystemNotification(title, body, opts.groupId);
}

function notifIcon(type) {
  return ({ mention: "📣", approval: "🔐", message: "💬", reconnect: "🔌", info: "ℹ️" })[type] || "🔔";
}

/** 系统桌面通知（页面隐藏时的兜底）。 */
function showSystemNotification(title, body, tag) {
  try {
    if (!("Notification" in window) || Notification.permission !== "granted") return;
    const n = new Notification(title, { body: String(body || "").slice(0, 100), tag: tag || "agui" });
    n.onclick = () => { window.focus(); n.close(); };
  } catch { /* 忽略 */ }
}

/** 未读通知条数。 */
function unreadNotifCount() {
  return state.notifications.filter((n) => !n.read).length;
}

/** 渲染通知面板列表 + 徽标。 */
function renderNotifications() {
  const list = $("notifList");
  if (!list) return;
  const badge = $("notifBadge");
  const unread = unreadNotifCount();
  badge.classList.toggle("hidden", unread === 0);
  badge.textContent = unread > 99 ? "99+" : String(unread);
  $("notifEmpty").classList.toggle("hidden", state.notifications.length > 0);
  list.innerHTML = state.notifications.length === 0 ? "" : state.notifications.map((n) => `
    <div class="notif-item${n.read ? "" : " unread"}" data-nid="${n.id}" role="listitem" tabindex="0">
      <span class="notif-ico">${n.icon}</span>
      <span class="notif-body">
        <span class="notif-text"><b>${escapeHtml(n.title)}</b><br/>${escapeHtml(n.body)}</span>
        <span class="notif-time">${escapeHtml(fmtTime(n.ts))}</span>
      </span>
    </div>`).join("");
  // 点击跳转 / 键盘 Enter 触发
  for (const el of list.querySelectorAll(".notif-item")) {
    const go = () => {
      const n = state.notifications.find((x) => String(x.id) === el.dataset.nid);
      if (n) { n.read = true; renderNotifications(); }
      if (n?.groupId) {
        hideNotifPanel();
        selectGroup(n.groupId); // 跳转到来源知聚（话题跟随知聚内记忆恢复，可靠且无竞态）
      }
    };
    el.onclick = go;
    el.onkeydown = (e) => { if (e.key === "Enter" || e.key === " ") { e.preventDefault(); go(); } };
  }
}

function toggleNotifPanel() {
  state.notifPanelOpen = !state.notifPanelOpen;
  $("notifPanel").classList.toggle("hidden", !state.notifPanelOpen);
  $("notifBtn").setAttribute("aria-expanded", String(state.notifPanelOpen));
  if (state.notifPanelOpen) {
    // 打开即清空未读徽标（列表项保留，点击跳转时才标已读）——徽标只反映“新”数量
    state.notifications.forEach((n) => { n.read = true; });
    renderNotifications();
  }
}

function hideNotifPanel() {
  state.notifPanelOpen = false;
  $("notifPanel").classList.add("hidden");
  $("notifBtn").setAttribute("aria-expanded", "false");
}

function clearNotifications() {
  state.notifications = [];
  renderNotifications();
}

/** 本地把某话题未读清零（进入知聚 / 切话题 / 当前话题收到新消息时），并重渲染列表与话题栏。 */
function markTopicRead(gid, topicId, messageId) {
  const info = state.groupUnread.get(gid);
  if (info) {
    const n = info.byTopic[topicId] || 0;
    if (n > 0) {
      info.byTopic[topicId] = 0;
      info.unreadCount = Math.max(0, info.unreadCount - n);
    }
    if (messageId) sendReadReceipt(gid, topicId, messageId);
  }
  if (state.activeGroupId === gid) { renderTopicBar(); renderGroupList(); }
  updateDocTitle(); // 未读变化同步标签页标题
}

/** 本地把某话题未读 +1（当前知聚其他话题收到新消息时），并重渲染话题栏与知聚列表。 */
function bumpTopicUnread(gid, topicId) {
  const info = state.groupUnread.get(gid);
  if (!info) return;
  info.byTopic[topicId] = (info.byTopic[topicId] || 0) + 1;
  info.unreadCount = (info.unreadCount || 0) + 1;
  if (state.activeGroupId === gid) { renderTopicBar(); renderGroupList(); }
  updateDocTitle(); // 未读变化同步标签页标题
}

/** 当前知聚某话题的最后一条真实消息 ID（无则 null）；供进入知聚 / 切话题时发已读回执。 */
function lastMessageIdOf(gid, topicId) {
  const r = room(gid);
  const msgs = (r?.messages || []).filter((m) => !m.sys && (m.topicId || "main") === (topicId || "main"));
  return msgs.length ? msgs[msgs.length - 1].id : null;
}

/** 知聚头部元信息：知聚主显示昵称（无昵称则用户名 / ID），附我的身份。 */
function renderChatMeta() {
  const g = state.groups.find((x) => x.groupId === state.activeGroupId);
  if (!g) { $("chatGroupMeta").textContent = ""; return; }
  const owner = room(g.groupId)?.members.find((m) => m.memberId === g.ownerId)
    || userDirectory.find((u) => u.memberId === g.ownerId);
  const ownerName = owner?.nickname || g.ownerId || "";
  $("chatGroupMeta").textContent = `知聚主 ${ownerName} · 我的身份 ${g.myRole || ""}`;
}

function selectGroup(gid) {
  if (state.activeGroupId === gid) return;
  clearReplyTo(); // 切知聚时清除引用目标（引用属于原知聚上下文）
  // 按知聚记忆 @ 选择：保存当前知聚，恢复目标知聚（无记忆则空）
  if (state.activeGroupId) {
    state.mentionMemory.set(state.activeGroupId, { ids: [...state.mentions], all: state.mentionAll });
  }
  state.activeGroupId = gid;
  state.activeTopicId = "main"; // 先主话题，随后按话题记忆恢复
  // 记住用户最后选择的知聚（再次登录自动进入）
  try { localStorage.setItem(LastGroupKey(state.memberId), gid); } catch {}
  const mem = state.mentionMemory.get(gid);
  state.mentions = new Set(mem?.ids || []);
  state.mentionAll = !!mem?.all;
  // 话题记忆：目标知聚 topics 已加载（此前进过）直接选中；未加载则等快照（_pendingTopic）
  const memTopic = state.topicMemory.get(gid);
  const r = room(gid);
  if (memTopic && memTopic !== "main") {
    if (r.topics.some((t) => t.topicId === memTopic)) {
      selectTopic(memTopic, { auto: true }); // 自动恢复不算“主动查看”：保留其未读徽标提示
    } else {
      r._pendingTopic = memTopic; // 等 GROUP_STATE_SNAPSHOT 到达后校验并选中
    }
  }
  resetVScroll(); // 清空上一知聚的虚拟滚动状态与消息 DOM
  vscroll.stickBottom = true; // 切知聚后定位到最新消息（快照到达后由 renderMessages 生效）
  hideMentionPicker();
  // 进入知聚不标记任何话题已读：话题栏先展示全部未读徽标，用户点击对应话题（或该话题收到新消息）后才已读
  $("mentionAllBtn").classList.toggle("on", state.mentionAll);
  renderMentionChips();
  renderMembers(); // 恢复被 @ 成员的高亮
  renderGroupList();
  $("addMemberBtn").disabled = false;
  $("groupSettingsBtn").disabled = false;
  $("searchBtn").disabled = false; // 知聚内消息全文搜索（进入知聚后可用）
  $("discussBtn").disabled = false; // 多位数字员工讨论（进入知聚后可用）
  const g = state.groups.find((x) => x.groupId === gid);
  $("chatGroupName").textContent = (g?.isPrivate ? "🔒 " : "") + (g?.groupName || "");
  renderChatMeta();
  state.subscribedGroups.add(gid);
  send({ type: "GROUP_SUBSCRIBE", groupIds: [gid], timestamp: Date.now() });
  // 进入知聚前刷新未读数据（未读可能产生于未订阅期间，事件收不到）：loadGroups 完成后话题栏红点 / 知聚列表徽标同步最新
  loadGroups();
  // 订阅前先显示旧状态，快照到达后刷新
  renderTopicBar();
  renderMembers();
  renderMessages();
  renderTyping();
}

/* ============ 知聚话题（知聚扩展） ============ */

/** 当前“以此消息新建话题”的来源消息 ID（null = 普通新建话题）。 */
let topicSourceMessageId = null;

/** 渲染话题栏：主话题 + 自定义话题 chip + 新建按钮。 */
function renderTopicBar() {
  const el = $("topicBar");
  const r = state.activeGroupId ? room(state.activeGroupId) : null;
  el.innerHTML = "";
  if (!r) { el.classList.add("hidden"); return; }
  el.classList.remove("hidden");

  const main = document.createElement("span");
  main.className = "topic-chip" + (state.activeTopicId === "main" ? " active" : "");
  main.textContent = t("topic.mainText");
  main.title = t("topic.mainTitle");
  const mainUnread = state.groupUnread.get(state.activeGroupId)?.byTopic["main"] || 0;
  if (mainUnread > 0) main.appendChild(unreadDotEl(mainUnread, t("topic.unreadLabel")));
  main.onclick = () => selectTopic("main");
  el.appendChild(main);

  // 话题管理权限：清空聊天记录（知聚主 / 管理员）；删除话题（知聚主 / 管理员或话题创建者，服务端同样校验）
  const me = r.members?.find((x) => x.memberId === state.memberId);
  const canManage = me?.role === "owner" || me?.role === "admin";

  /** 清空话题聊天记录（含主话题）：调 /ag-ui/group/topic/clear，本地移除该话题消息与全局索引。 */
  const clearTopic = async (topicId, name) => {
    if (!confirm(t("topic.clearConfirm", { name }))) return;
    try {
      const res = await fetch("/ag-ui/group/topic/clear", {
        method: "POST",
        headers: { "Content-Type": "application/json", Authorization: `Bearer ${state.token}` },
        body: JSON.stringify({ groupId: state.activeGroupId, topicId, operatorId: state.memberId }),
      });
      const data = await res.json().catch(() => null);
      if (!res.ok) { toast(errMsg(data, t("topic.clearFail", { err: res.status }))); return; }
      toast(t("topic.clearSuccess", { name, count: data.removedCount || 0 }));
      dropTopicMessages(r, topicId);
      scheduleVirtualRender();
      await refreshActiveGroup();
    } catch (ex) { toast(t("topic.clearFail", { err: ex.message })); }
  };

  // 主话题：知聚主 / 管理员可清空聊天记录
  if (canManage) {
    const clear = document.createElement("button");
    clear.className = "topic-del-btn";
    clear.textContent = "🗑";
    clear.title = t("topic.clearMainTitle");
    clear.onclick = (e) => { e.stopPropagation(); clearTopic("main", t("topic.mainText")); };
    main.appendChild(clear);
  }

  for (const tpc of r.topics || []) {
    const chip = document.createElement("span");
    chip.className = "topic-chip" + (state.activeTopicId === tpc.topicId ? " active" : "");
    chip.textContent = "# " + tpc.name;
    chip.title = t("topic.creatorTitle", { name: tpc.name, creator: tpc.creatorId });
    const tUnread = state.groupUnread.get(state.activeGroupId)?.byTopic[tpc.topicId] || 0;
    if (tUnread > 0) chip.appendChild(unreadDotEl(tUnread, t("topic.unreadLabel")));
    chip.onclick = () => selectTopic(tpc.topicId);
    if (canManage || tpc.creatorId === state.memberId) {
      if (canManage) {
        const clear = document.createElement("button");
        clear.className = "topic-del-btn";
        clear.textContent = "🗑";
        clear.title = t("topic.clearTitle");
        clear.onclick = (e) => { e.stopPropagation(); clearTopic(tpc.topicId, `# ${tpc.name}`); };
        chip.appendChild(clear);
      }
      const del = document.createElement("button");
      del.className = "topic-del-btn";
      del.textContent = "✕";
      del.title = t("topic.deleteTitle");
      del.onclick = async (e) => {
        e.stopPropagation();
        if (!confirm(t("topic.deleteConfirm", { name: tpc.name }))) return;
        try {
          const res = await fetch("/ag-ui/group/topic/delete", {
            method: "POST",
            headers: { "Content-Type": "application/json", Authorization: `Bearer ${state.token}` },
            body: JSON.stringify({ groupId: state.activeGroupId, topicId: tpc.topicId, operatorId: state.memberId }),
          });
          const data = await res.json().catch(() => null);
          if (!res.ok) { toast(errMsg(data, t("topic.deleteFail", { err: res.status }))); return; }
          toast(t("topic.deleteSuccess", { name: tpc.name }));
          // 本地同步清理该话题消息与全局索引（事件丢失时快照合并不会移除旧消息，需显式清理）
          dropTopicMessages(r, tpc.topicId);
          if (state.activeTopicId === tpc.topicId) selectTopic("main");
          await refreshActiveGroup();
        } catch (ex) { toast(t("topic.deleteFail", { err: ex.message })); }
      };
      chip.appendChild(del);
    }
    el.appendChild(chip);
  }

  const btn = document.createElement("button");
  btn.className = "topic-new-btn";
  btn.textContent = t("topic.newBtn");
  btn.title = t("topic.newTitle");
  btn.onclick = openTopicModal;
  el.appendChild(btn);
}

/** 切换当前话题：重置视图按话题过滤显示；该话题本地无消息时拉取最近一页历史。
 * opts.auto=true 表示记忆自动恢复（非用户主动点击）：不标记该话题已读，保留未读徽标提示。 */
async function selectTopic(topicId, opts) {
  const auto = opts?.auto === true;
  const gid = state.activeGroupId;
  const r = gid ? room(gid) : null;
  if (!r) return;
  // 点击当前话题标签 = 主动确认已读（其未读徽标清零并落读位点）
  if (state.activeTopicId === topicId) {
    if (!auto) markTopicRead(gid, topicId, lastMessageIdOf(gid, topicId));
    return;
  }
  state.activeTopicId = topicId;
  r.allLoaded = false; // 切换话题后允许加载更早（实际是否有更早由分页返回纠正）
  // 记住话题选择：切知聚 / 再次进入该知聚时自动恢复（localStorage 按用户持久化）
  state.topicMemory.set(gid, topicId);
  saveTopicMemory(state.memberId);
  renderTopicBar();
  resetVScroll();
  vscroll.stickBottom = true;
  renderMessages();
  // 用户主动点击话题 → 视为已读（清零该话题未读并落读位点）；自动恢复不标记
  if (!auto) markTopicRead(gid, topicId, lastMessageIdOf(gid, topicId));
  // 快照只覆盖最近若干条（跨话题），目标话题本地无消息时补拉历史
  if (!activeTopicMessages(r).some((m) => !m.sys)) {
    try {
      const res = await fetch(`/ag-ui/group/${encodeURIComponent(gid)}/topics/${encodeURIComponent(topicId)}/messages?count=50`);
      if (res.ok) {
        const older = await res.json();
        if (Array.isArray(older) && older.length > 0) {
          const known = new Set(r.messages.map((m) => m.id));
          const added = older.filter((m) => !known.has(m.messageId)).map(snapshotToMessage);
          if (added.length > 0) {
            r.messages.push(...added);
            for (const m of added) state.msgIndex.set(m.id, m);
            trimMessages(r); // 内存上限：超限裁剪最旧消息
            r.allLoaded = older.length < 50;
            renderMessages();
            if (!auto) markTopicRead(gid, topicId, lastMessageIdOf(gid, topicId));
          }
        }
      }
    } catch { /* 拉取失败静默，保持空话题视图 */ }
  }
}

/* ============ 消息搜索（知聚内全文） ============ */

/* ============ 管理员控制台：用户管理 + 系统状态 ============ */

async function openAdminModal() {
  if (!state.isAdmin) { toast(t("admin.adminOnly")); return; }
  $("adminModal").classList.remove("hidden");
  switchAdminTab("users");
}

/** 管理员弹窗 tab 切换：用户管理 / 用量统计。 */
function switchAdminTab(tab) {
  const users = tab === "users";
  const usage = tab === "usage";
  const conf = tab === "config";
  $("adminTabUsers").classList.toggle("on", users);
  $("adminTabUsage").classList.toggle("on", usage);
  $("adminTabConfig").classList.toggle("on", conf);
  $("adminUsersView").classList.toggle("hidden", !users);
  $("adminUsageView").classList.toggle("hidden", !usage);
  $("adminConfigView").classList.toggle("hidden", !conf);
  if (users) {
    $("adminUserRows").innerHTML = `<tr><td colspan="7" class="admin-empty">${t("admin.loading")}</td></tr>`;
    loadAdminUsers();
  } else if (usage) {
    $("adminUsageRows").innerHTML = `<tr><td colspan="6" class="admin-empty">${t("admin.loading")}</td></tr>`;
    loadAdminUsage();
  } else {
    loadConfigGovernance();
  }
}

/** 用量统计：最近 7 天按日汇总 + 配额配置。 */
async function loadAdminUsage() {
  try {
    const res = await fetch("/ag-ui/admin/usage?days=7", { headers: { Authorization: "Bearer " + state.token } });
    const data = await res.json().catch(() => null);
    if (!res.ok || !data) { $("adminUsageRows").innerHTML = `<tr><td colspan="6" class="admin-empty">${t("admin.loadFail", { err: escapeHtml(errMsg(data, "HTTP " + res.status)) })}</td></tr>`; return; }
    const quota = Number(data.dailyQuotaPerUser) || 0;
    $("adminUsageMeta").textContent = quota > 0
      ? t("admin.quotaEnabled", { quota: quota.toLocaleString() })
      : t("admin.quotaDisabled");
    const days = data.days || [];
    $("adminUsageRows").innerHTML = days.length
      ? days.map((d) => `<tr>
          <td>${escapeHtml(d.date)}</td>
          <td>${Number(d.totalTokens).toLocaleString()}</td>
          <td>${Number(d.promptTokens).toLocaleString()}</td>
          <td>${Number(d.completionTokens).toLocaleString()}</td>
          <td>${Number(d.reasoningTokens).toLocaleString()}</td>
          <td>${Number(d.calls).toLocaleString()}</td>
        </tr>`).join("")
      : `<tr><td colspan="6" class="admin-empty">${t("admin.usageEmpty")}</td></tr>`;
  } catch { $("adminUsageRows").innerHTML = `<tr><td colspan="6" class="admin-empty">${t("admin.sysNetErr")}</td></tr>`; }
}

/* ============ 配置治理（6.3）：管理员在线调参 ============ */

/** 读取治理状态并回填表单（值 undefined → 留空表示沿用配置默认；布尔以三态呈现：未设置不勾也不禁用）。 */
async function loadConfigGovernance() {
  try {
    const res = await fetch("/ag-ui/admin/config/governance", { headers: { Authorization: "Bearer " + state.token } });
    const d = await res.json().catch(() => null);
    if (!res.ok || !d) { toast(errMsg(d, `配置读取失败（HTTP ${res.status}）`)); return; }
    setCfg("cfgSessionTtlHours", d.sessionTtlHours);
    setCfg("cfgMessageHistoryLimit", d.messageHistoryLimit);
    setCfg("cfgMaxGroupMembers", d.maxGroupMembers);
    setCfg("cfgMaxMessageChars", d.maxMessageChars);
    setCfg("cfgMessageRetentionDays", d.messageRetentionDays);
    setCfg("cfgDailyTokenQuota", d.dailyTokenQuotaPerUser);
    setCfgBool("cfgRequireToken", d.requireTokenOnRealTime);
    setCfgBool("cfgEnableTools", d.enableTools);
    setCfgBool("cfgEnableWebTools", d.enableWebTools);
    setCfgBool("cfgThinking", d.thinkingMode);
    $("cfgApprovalTools").value = (d.requireApprovalToolNames || []).join(", ");
    $("cfgFrameOrigins").value = (d.allowedFrameOrigins || []).join(", ");
  } catch { toast(t("admin.configLoadFail")); }
}

function setCfg(id, v) { const el = $(id); if (el) el.value = v === null || v === undefined ? "" : String(v); }
function setCfgBool(id, v) {
  const el = $(id);
  if (!el) return;
  if (v === null || v === undefined) { el.checked = false; el.indeterminate = true; } // 三态：沿用配置默认
  else { el.checked = !!v; el.indeterminate = false; }
}
/** 把表单值收集为请求体：空值 / 三态布尔（indeterminate=沿用）不发送 → 服务端保留原值。 */
function collectCfg() {
  const body = {};
  body.sessionTtlHours = numOrNull("cfgSessionTtlHours");
  body.messageHistoryLimit = numOrNull("cfgMessageHistoryLimit");
  body.maxGroupMembers = numOrNull("cfgMaxGroupMembers");
  body.maxMessageChars = numOrNull("cfgMaxMessageChars");
  body.messageRetentionDays = numOrNull("cfgMessageRetentionDays");
  body.dailyTokenQuotaPerUser = numOrNull("cfgDailyTokenQuota");
  body.requireTokenOnRealTime = boolOrNull("cfgRequireToken");
  body.enableTools = boolOrNull("cfgEnableTools");
  body.enableWebTools = boolOrNull("cfgEnableWebTools");
  body.thinkingMode = boolOrNull("cfgThinking");
  body.requireApprovalToolNames = listOrUndefined($("cfgApprovalTools").value);
  body.allowedFrameOrigins = listOrUndefined($("cfgFrameOrigins").value);
  // 去掉 undefined / null 字段（保持后端语义：未传不修改）
  for (const k of Object.keys(body)) if (body[k] === undefined || body[k] === null) delete body[k];
  return body;
}
function numOrNull(id) { const v = $(id).value.trim(); return v === "" ? null : Number(v); }
function boolOrNull(id) { const el = $(id); return el.indeterminate ? null : el.checked; }
function listOrUndefined(str) {
  const items = (str || "").split(/[,\s]+/).map((s) => s.trim()).filter(Boolean);
  if (!str || !items.length) return undefined; // 空 → 不修改审批名单/嵌入来源
  return items;
}

async function saveConfigGovernance() {
  const btn = $("cfgSave");
  const orig = btn.textContent;
  btn.disabled = true; btn.textContent = t("common.saving");
  try {
    const res = await fetch("/ag-ui/admin/config", {
      method: "POST",
      headers: { "Content-Type": "application/json", Authorization: "Bearer " + state.token },
      body: JSON.stringify(collectCfg()),
    });
    const data = await res.json().catch(() => null);
    if (!res.ok) { toast(errMsg(data, t("common.saveFail", { err: res.status }))); return; }
    toast(t("admin.configSaved"));
  } catch (ex) { toast(t("common.saveFail", { err: ex.message })); }
  finally { btn.disabled = false; btn.textContent = orig; }
}

async function loadAdminUsers() {
  try {
    const res = await fetch("/ag-ui/admin/users", { headers: { Authorization: "Bearer " + state.token } });
    const data = await res.json().catch(() => null);
    if (!res.ok) { $("adminUserRows").innerHTML = `<tr><td colspan="7" class="admin-empty">${t("admin.loadFail", { err: escapeHtml(errMsg(data, "HTTP " + res.status)) })}</td></tr>`; return; }
    const meId = state.memberId;
    $("adminUserRows").innerHTML = (data || []).map((u) => {
      const role = u.isAdmin ? `<span class="tag-admin">${t("admin.role")}</span>` : `<span class="tag-user">${t("admin.user")}</span>`;
      const status = u.isDisabled ? `<span class="tag-disabled">${t("admin.disabled")}</span>` : `<span class="tag-active">${t("admin.active")}</span>`;
      const self = u.userId === meId ? t("admin.me") : "";
      const actions = u.userId === meId
        ? `<span class="muted">${t("admin.currentAccount")}</span>`
        : `<button class="chip-btn icon-btn" data-op="disable" data-uid="${cssEsc(u.userId)}" data-name="${cssEsc(u.username)}" title="${u.isDisabled ? t("admin.enableTitle") : t("admin.disableTitle")}">${u.isDisabled ? "🔓" : "🔒"}</button>`
          + `<button class="chip-btn icon-btn" data-op="resetpw" data-uid="${cssEsc(u.userId)}" data-name="${cssEsc(u.username)}" title="${t("admin.resetPwTitle")}">🔑</button>`;
      return `<tr>
        <td>${escapeHtml(u.username)}${self}</td>
        <td>${escapeHtml(u.nickname || "")}</td>
        <td>${role}</td>
        <td>${status}</td>
        <td>${Number(u.groupCount) || 0}</td>
        <td>${fmtDateTime(Number(u.createdAt) || 0)}</td>
        <td>${actions}</td>
      </tr>`;
    }).join("") || `<tr><td colspan="7" class="admin-empty">${t("admin.noUsers")}</td></tr>`;
  } catch { $("adminUserRows").innerHTML = `<tr><td colspan="7" class="admin-empty">${t("admin.sysNetErr")}</td></tr>`; }
}

/** 管理员操作：禁用 / 启用 / 重置密码（重置密码弹输入框）。 */
async function adminUserAction(op, uid, name) {
  if (op === "disable") {
    // 先重新拉取列表确认当前状态，再提示目标动作（禁用 / 启用）
    const list = await (await fetch("/ag-ui/admin/users", { headers: { Authorization: "Bearer " + state.token } })).json().catch(() => null);
    const u = (list || []).find((x) => x.userId === uid);
    if (!u) { toast(t("admin.userNotFound")); return; }
    const disabled = !u.isDisabled;
    const actionText = disabled ? t("admin.userDisableConfirm", { name }) : t("admin.userEnableConfirm", { name });
    if (!confirm(actionText)) return;
    const res = await fetch(`/ag-ui/admin/users/${encodeURIComponent(uid)}/disabled`, {
      method: "POST",
      headers: { "Content-Type": "application/json", Authorization: "Bearer " + state.token },
      body: JSON.stringify({ disabled }),
    });
    const data = await res.json().catch(() => null);
    if (!res.ok) { toast(errMsg(data, t("admin.opFail", { err: res.status }))); return; }
    toast(disabled ? t("admin.userDisabled", { name }) : t("admin.userEnabled", { name }));
    await loadAdminUsers();
  } else if (op === "resetpw") {
    const pw = prompt(t("admin.resetPwPrompt", { name }));
    if (pw === null) return;
    if (pw.length < 6) { toast(t("admin.pwMin")); return; }
    const res = await fetch(`/ag-ui/admin/users/${encodeURIComponent(uid)}/password`, {
      method: "POST",
      headers: { "Content-Type": "application/json", Authorization: "Bearer " + state.token },
      body: JSON.stringify({ newPassword: pw }),
    });
    const data = await res.json().catch(() => null);
    if (!res.ok) { toast(errMsg(data, t("admin.resetPwFail", { err: res.status }))); return; }
    toast(t("admin.pwReset", { name }));
  }
}

async function openStatusModal() {
  if (!state.isAdmin) { toast(t("admin.adminOnly")); return; }
  $("statusModal").classList.remove("hidden");
  $("statusBody").innerHTML = t("status.loading");
  try {
    const res = await fetch("/ag-ui/admin/status", { headers: { Authorization: "Bearer " + state.token } });
    const d = await res.json().catch(() => null);
    if (!res.ok || !d) { $("statusBody").innerHTML = t("admin.loadFail", { err: escapeHtml(errMsg(d, "HTTP " + res.status)) }); return; }
    // 每项 [标签 key, 值]：标签翻译，数值作为数据原样显示（数字 / 时长 / 版本字符串）
    const items = [
      [t("status.uptime"), fmtDuration(Number(d.uptimeSeconds) || 0)],
      [t("status.connections"), Number(d.connections) || 0],
      [t("status.groups"), Number(d.groups) || 0],
      [t("status.users"), Number(d.users) || 0],
      [t("status.agents"), Number(d.agents) || 0],
      [t("status.messages"), Number(d.messages) || 0],
      [t("status.memory"), (Number(d.memoryMb) || 0) + " MB"],
      [t("status.threads"), Number(d.threadCount) || 0],
      [t("status.dotnet"), escapeHtml(String(d.dotnetVersion || ""))],
      // RAG 检索方式（向量语义 / 图谱遍历）
      [t("status.ragVector"), (d.rag && d.rag.vectorEnabled) ? t("status.on") : t("status.off")],
    ];
    // 图谱命中时展示其规模；未命中（未启用）显示“未启用”
    if (d.rag && d.rag.graphInUse) {
      items.push([t("status.ragGraph"), t("status.graphActive", { e: Number(d.rag.graphEntities) || 0, r: Number(d.rag.graphEdges) || 0 })]);
    } else {
      items.push([t("status.ragGraph"), t("status.ragGraphOff")]);
    }
    $("statusBody").innerHTML = `<div class="status-grid">` + items.map(([k, v]) =>
      `<div class="status-cell"><div class="status-key">${k}</div><div class="status-val">${v}</div></div>`).join("") + `</div>`;
  } catch { $("statusBody").innerHTML = t("admin.sysNetErr"); }
}

/** 运行时长格式化（秒 → “X天X小时X分”）。 */
function fmtDuration(totalSeconds) {
  const s = Math.max(0, Number(totalSeconds) || 0);
  const d = Math.floor(s / 86400), h = Math.floor((s % 86400) / 3600), m = Math.floor((s % 3600) / 60);
  const parts = [];
  if (d) parts.push(d + " 天");
  if (h || d) parts.push(h + " 小时");
  parts.push(m + " 分");
  return parts.join(" ");
}

/* ============ 多位数字员工讨论 ============ */

function openDiscussModal() {
  const gid = state.activeGroupId;
  if (!gid) return;
  const g = state.groups.find((x) => x.groupId === gid);
  $("discussGroupName").textContent = g?.groupName || gid;
  const r = room(gid);
  const agents = (r.members || []).filter((m) => m.memberType === "agent" && m.memberId !== state.memberId);
  $("discussAgentPicks").innerHTML = agents.length
    ? agents.map((a) => `<label class="discuss-pick"><input type="checkbox" value="${escapeHtml(a.memberId)}" checked /> ${escapeHtml(a.nickname || a.memberId)}</label>`).join("")
    : `<div class="search-empty">${t("discuss.noAgents")}</div>`;
  $("discussInput").value = "";
  $("discussGo").disabled = agents.length === 0;
  $("discussModal").classList.remove("hidden");
  $("discussInput").focus();
}

async function startDiscussion() {
  const gid = state.activeGroupId;
  const theme = $("discussInput").value.trim();
  if (!gid) return;
  if (!theme) { toast(t("discuss.needTopic")); return; }
  const agentIds = [...$("discussAgentPicks").querySelectorAll("input:checked")].map((c) => c.value);
  if (agentIds.length === 0) { toast(t("discuss.needAgent")); return; }
  const go = $("discussGo");
  go.disabled = true;
  const old = go.textContent;
  go.textContent = t("discuss.going");
  try {
    const res = await fetch(`/ag-ui/group/${encodeURIComponent(gid)}/discussion`, {
      method: "POST",
      headers: { "Content-Type": "application/json", Authorization: "Bearer " + state.token },
      body: JSON.stringify({ content: theme, agentIds, topicId: state.activeTopicId || "main" }),
    });
    const data = await res.json().catch(() => null);
    if (!res.ok) { toast(errMsg(data, t("discuss.fail", { err: res.status }))); return; }
    $("discussModal").classList.add("hidden");
    toast(t("discuss.started", { count: agentIds.length }));
  } catch (ex) { toast(t("discuss.fail", { err: ex.message })); }
  finally { go.disabled = false; go.textContent = old; }
}

function openSearchModal() {
  const gid = state.activeGroupId;
  if (!gid) return;
  const g = state.groups.find((x) => x.groupId === gid);
  $("searchGroupName").textContent = g?.groupName || gid;
  $("searchInput").value = "";
  $("searchResults").innerHTML = `<div class="search-empty">${t("search.emptyHint")}</div>`;
  $("searchModal").classList.remove("hidden");
  $("searchInput").focus();
}

async function doSearch() {
  const gid = state.activeGroupId;
  const q = $("searchInput").value.trim();
  if (!gid) return;
  if (!q) { toast(t("search.needKeyword")); return; }
  $("searchResults").innerHTML = `<div class="search-empty">${t("search.searching")}</div>`;
  try {
    const res = await fetch(`/ag-ui/group/${encodeURIComponent(gid)}/messages/search?q=${encodeURIComponent(q)}&count=50`,
      { headers: { Authorization: "Bearer " + (state.token || "") } });
    const data = await res.json().catch(() => null);
    if (!res.ok) { $("searchResults").innerHTML = `<div class="search-empty">${escapeHtml(errMsg(data, t("search.fail", { err: "HTTP " + res.status })))}</div>`; return; }
    renderSearchResults(data || [], q);
  } catch {
    $("searchResults").innerHTML = `<div class="search-empty">${t("search.networkErr")}</div>`;
  }
}

function renderSearchResults(results, q) {
  const wrap = $("searchResults");
  if (!results.length) { wrap.innerHTML = `<div class="search-empty">${t("search.noMatch")}</div>`; return; }
  const r = room(state.activeGroupId);
  const topicName = (tid) => {
    const tp = r?.topics?.find((x) => x.topicId === tid);
    return tp ? tp.name : (tid === "main" ? t("topic.mainShort") : tid);
  };
  wrap.innerHTML = results.map((m) => {
    const sender = m.senderId === state.memberId ? t("search.me") : (memberName(m.senderId) || m.senderNickname || m.senderId);
    const snippet = String(m.content || "").slice(0, 120);
    return `<div class="search-hit" data-mid="${escapeHtml(m.messageId)}" data-topic="${escapeHtml(m.topicId || "main")}">
      <div class="search-hit-head"><span class="search-hit-sender">${escapeHtml(sender)}</span><span class="search-hit-topic"># ${escapeHtml(topicName(m.topicId || "main"))}</span><span class="search-hit-time">${escapeHtml(fmtTime(m.timestamp))}</span></div>
      <div class="search-hit-text">${escapeHtml(snippet)}</div>
    </div>`;
  }).join("");
  wrap.querySelectorAll(".search-hit").forEach((el) => {
    el.onclick = () => jumpToSearchHit(el.dataset.mid, el.dataset.topic);
  });
}

/** 跳转到搜索结果：切到对应话题并滚动 / 高亮该消息；
 * 目标不在已加载窗口时，拉取其前后各 count/2 条重建当前话题窗口再定位（深度定位）。 */
async function jumpToSearchHit(messageId, topicId) {
  $("searchModal").classList.add("hidden");
  const gid = state.activeGroupId;
  if (!gid) return;
  const r = room(gid);
  if (state.activeTopicId !== topicId) await selectTopic(topicId);
  // 双 rAF 确保虚拟窗口重建 / 增量渲染完成后再检查目标是否已在 DOM
  const inDom = await new Promise((resolve) => {
    requestAnimationFrame(() => requestAnimationFrame(() => resolve(!!document.querySelector(`[data-mid="${cssEsc(messageId)}"]`))));
  });
  if (inDom) { scrollToMessage(messageId); return; }
  // 深度定位：拉取目标消息前后各 count/2 条，重建当前话题窗口
  try {
    const res = await fetch(`/ag-ui/group/${encodeURIComponent(gid)}/messages/around?messageId=${encodeURIComponent(messageId)}&topicId=${encodeURIComponent(topicId || "main")}&count=60`,
      { headers: { Authorization: "Bearer " + (state.token || "") } });
    const data = await res.json().catch(() => null);
    if (!res.ok || !Array.isArray(data) || data.length === 0) { toast(t("search.locateFail")); return; }
    const known = new Set(r.messages.map((m) => m.id));
    // 重建窗口：保留流式进行中的消息与系统行（避免正在输出的回复丢失），其余以定位窗口为准
    const live = r.messages.filter((m) => m.streaming || m.sys);
    const added = data.map(snapshotToMessage).filter((m) => !known.has(m.id));
    r.messages = [...live, ...added];
    r.messages.sort((a, b) => (a.timestamp || 0) - (b.timestamp || 0));
    r.allLoaded = false; // around 不是完整历史，保留「加载更早」入口
    vscroll.force = true;
    scheduleVirtualRender();
    requestAnimationFrame(() => requestAnimationFrame(() => scrollToMessage(messageId)));
  } catch { toast(t("search.locateNetErr")); }
}

/** 滚动到指定消息并高亮（2.2s 闪烁）。 */
function scrollToMessage(messageId) {
  const el = document.querySelector(`[data-mid="${cssEsc(messageId)}"]`);
  if (!el) { toast(t("search.msgNotInRange")); return; }
  el.scrollIntoView({ block: "center", behavior: "smooth" });
  el.classList.add("flash-highlight");
  setTimeout(() => el.classList.remove("flash-highlight"), 2200);
}

function openTopicModal() {
  $("topicModalTitle").textContent = topicSourceMessageId ? t("topic.modalTitleFromMsg") : t("topic.title");
  $("topicModalHint").classList.toggle("hidden", !topicSourceMessageId);
  $("topicName").value = "";
  $("topicModal").classList.remove("hidden");
  $("topicName").focus();
}

/** 从某条发言新建话题：记录来源消息并弹出话题命名弹窗。 */
function openTopicModalFromMessage(messageId) {
  topicSourceMessageId = messageId;
  openTopicModal();
}

function closeTopicModal() {
  topicSourceMessageId = null;
  $("topicModal").classList.add("hidden");
}

/** 新建话题：HTTP 创建成功后本地生效并切换到新话题（广播事件会同步给其他成员）。
 * 携带 sourceMessageId 时，该消息被服务端迁移为新话题的起点。 */
async function createTopic() {
  const gid = state.activeGroupId;
  if (!gid) return;
  const name = $("topicName").value.trim();
  if (!name) { toast(t("topic.needName")); return; }
  const sourceMessageId = topicSourceMessageId;
  try {
    const res = await fetch("/ag-ui/group/topic/create", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ groupId: gid, name, operatorId: state.memberId, sourceMessageId }),
    });
    if (!res.ok) throw new Error("HTTP " + res.status);
    const topic = await res.json();
    closeTopicModal();
    const r = room(gid);
    if (!r.topics.some((t) => t.topicId === topic.topicId)) r.topics.push(topic);
    renderTopicBar();
    await selectTopic(topic.topicId);
    toast(t("topic.created", { name: topic.name }) + (sourceMessageId ? t("topic.migratedSuffix") : ""));
  } catch (ex) {
    toast(t("topic.createFail", { err: ex.message }));
  }
}

/** 消息被迁移到其他话题（“以此消息新建话题”）：更新本地归属并按当前话题过滤刷新。 */
function onMessageTopicMoved(evt) {
  const r = room(evt.groupId);
  const m = r.messages.find((x) => x.id === evt.messageId);
  if (m) m.topicId = evt.topicId;
  if (state.activeGroupId === evt.groupId) {
    vscroll.force = true; // 归属变化 → 重建按当前话题过滤显示
    scheduleVirtualRender();
  }
}

/** 知聚成员可见列表（分身与本人互斥）：用户在线 → 隐藏分身；用户离线且已启用分身 → 隐藏用户本人（由分身代班）。 */
function visibleGroupMembers() {
  const r = state.activeGroupId ? room(state.activeGroupId) : null;
  if (!r) return [];
  return r.members.filter((m) => {
    const isTwin = (m.memberId || "").startsWith("twin_");
    if (isTwin) {
      const owner = r.members.find((x) => x.memberId === m.memberId.slice(5));
      if (owner?.onlineStatus === "online") return false;
    } else {
      const twin = r.members.find((x) => x.memberId === "twin_" + m.memberId);
      if (twin && m.onlineStatus !== "online") return false;
    }
    return true;
  });
}

/** 成员状态图标 HTML（与成员列表一致）：分身 🪞；数字员工按触发方式图标；用户在线状态点。 */
function memberStatusIconHtml(m) {
  const isTwin = (m.memberId || "").startsWith("twin_");
  if (isTwin) return `<span class="twin-status-icon" title="${escapeHtml(t("member.twinTip"))}">🪞</span>`;
  if (m.memberType === "agent") {
    // 本知聚已覆盖 → 显示本知聚触发方式；跟随角色默认 → 解析角色默认的具体触发方式图标（agentDirectory 当前默认，回退成员快照值）
    const overridden = m.isTriggerOverridden;
    const mode = overridden
      ? m.triggerMode
      : (agentDirectory.find((a) => a.memberId === m.memberId)?.triggerMode || m.triggerMode || "mentioned");
    const label = TRIGGER_LABELS[mode] || mode || "mentioned";
    return overridden
      ? `<span class="trigger-mode-icon overridden" title="${escapeHtml(t("member.triggerOverridden", { mode: label }))}">${TRIGGER_ICONS[mode] || "⚙"}</span>`
      : `<span class="trigger-mode-icon" title="${escapeHtml(t("member.triggerInheritTip", { mode: label }))}">${TRIGGER_ICONS[mode] || TRIGGER_ICONS.inherit}</span>`;
  }
  // 用户在线状态点（有头像时叠加到头像右下角，无头像时独立显示）
  return `<span class="dot ${m.onlineStatus || "offline"}"></span>`;
}

function renderMembers() {
  const el = $("memberList");
  const r = state.activeGroupId ? room(state.activeGroupId) : null;
  el.innerHTML = "";
  $("memberCount").textContent = r ? `(${r.members.length})` : "";
  if (!r) return;
  const gid = state.activeGroupId;

  // 触发方式图例：仅当知聚内有普通数字员工成员时显示，悬停图标可看具体说明
  if (r.members.some((m) => m.memberType === "agent" && !(m.memberId || "").startsWith("twin_"))) {
    const legend = document.createElement("div");
    legend.className = "trigger-legend";
    legend.innerHTML =
      `<span>${TRIGGER_ICONS.mentioned} ${escapeHtml(t("agent.form.trigger.mentioned"))}</span>` +
      `<span>${TRIGGER_ICONS.allMessages} ${escapeHtml(t("agent.form.trigger.allMessages"))}</span>` +
      `<span>${TRIGGER_ICONS.keyword} ${escapeHtml(t("agent.form.trigger.keyword"))}</span>` +
      `<span>${TRIGGER_ICONS.contextual} ${escapeHtml(t("agent.form.trigger.contextual"))}</span>`;
    el.appendChild(legend);
  }

  for (const m of visibleGroupMembers()) {
    const isTwin = (m.memberId || "").startsWith("twin_");

    const div = document.createElement("div");
    div.className = "member-item";
    const role = m.role === "owner" ? t("member.roleOwner") : m.role === "admin" ? t("member.roleAdmin") : "";
    // 头像（有则显示）：状态图标叠加到头像右下角；无头像时状态图标独立显示（16px 圆形）
    const statusIcon = memberStatusIconHtml(m);
    const avatarHtml = m.avatar
      ? `<span class="member-avatar"><img src="${escapeHtml(authedAssetUrl(m.avatar))}" alt="" onerror="this.remove()" />${statusIcon}</span>`
      : statusIcon;
    div.innerHTML = `
      ${avatarHtml}
      <span>${escapeHtml(m.nickname || m.memberId)}</span>
      ${!isTwin && m.memberType === "agent" ? '<span class="tag-agent">AI</span>' : ""}
      <span class="role">${role}</span>`;
    // 双击成员行 = @（选中为提及 / 私聊对象），再次双击取消；被 @ 的行高亮
    div.title = t("member.dblClickMention");
    div.classList.toggle("mentioned", state.mentions.has(m.memberId));
    div.ondblclick = () => toggleMention(m.memberId);

    // 数字员工成员：⚙ 按钮弹出本知聚触发方式设置（可覆盖角色默认；分身不提供知聚内覆盖）
    if (m.memberType === "agent" && !isTwin) {
      const btn = document.createElement("button");
      btn.className = "trigger-btn";
      btn.textContent = "⚙";
      btn.title = t("member.setTrigger");
      btn.onclick = () => openGroupTriggerModal(gid, m);
      div.appendChild(btn);
    }

    // 成员移除（知聚主 / 管理员可见）：不能移除知聚主和自己；数字员工、分身同样可移除
    const me = r.members.find((x) => x.memberId === state.memberId);
    const canManage = me?.role === "owner" || me?.role === "admin";
    const ownerId = state.groups.find((g) => g.groupId === gid)?.ownerId;
    if (canManage && m.memberId !== ownerId && m.memberId !== state.memberId) {
      const rm = document.createElement("button");
      rm.className = "trigger-btn rm-btn";
      rm.textContent = "✕";
      rm.title = t("member.remove");
      rm.onclick = async (e) => {
        e.stopPropagation();
        if (!confirm(t("member.removeConfirm", { name: m.nickname || m.memberId }))) return;
        try {
          const res = await fetch("/ag-ui/group/member/remove", {
            method: "POST",
            headers: { "Content-Type": "application/json", Authorization: `Bearer ${state.token}` },
            body: JSON.stringify({ groupId: gid, memberIds: [m.memberId], operatorId: state.memberId }),
          });
          const data = await res.json().catch(() => null);
          if (!res.ok) { toast(t("member.removeFail", { err: errMsg(data, `删除失败（${res.status}）`) })); return; }
          toast(t("member.removed", { name: m.nickname || m.memberId }));
          await refreshActiveGroup();
        } catch (ex) { toast(t("member.removeFail", { err: ex.message })); }
      };
      div.appendChild(rm);
    }

    el.appendChild(div);
  }
}

/* ============ 知聚内触发方式设置（弹窗） ============ */

/** 当前弹窗编辑对象：{ gid, member }。 */
let gtEditing = null;

function openGroupTriggerModal(gid, m) {
  gtEditing = { gid, member: m };
  $("gtAgentName").textContent = m.nickname || m.memberId;
  $("gtTriggerMode").value = m.isTriggerOverridden ? (m.triggerMode || "mentioned") : "inherit";
  $("gtKeywords").value = (m.keywords || []).join(", ");
  syncGroupTriggerForm();
  $("groupTriggerModal").classList.remove("hidden");
}

function syncGroupTriggerForm() {
  $("gtKeywords").classList.toggle("hidden", $("gtTriggerMode").value !== "keyword");
}

function closeGroupTriggerModal() {
  gtEditing = null;
  $("groupTriggerModal").classList.add("hidden");
}

/**
 * 保存某数字员工在本知聚的触发方式：非「跟随角色默认」即显式覆盖
 * （override=true，角色编辑不再覆写本知聚）；「跟随角色默认」以角色当前默认值注册。
 */
async function saveGroupTrigger() {
  const editing = gtEditing;
  if (!editing) return;
  const { gid, member: m } = editing;
  const inherit = $("gtTriggerMode").value === "inherit";
  let triggerMode = $("gtTriggerMode").value;
  let keywords = [];
  if (inherit) {
    // 跟随角色默认：以角色当前默认值注册（override=false），角色后续编辑会自动同步
    const a = agentDirectory.find((x) => x.memberId === m.memberId);
    triggerMode = a?.triggerMode || "mentioned";
    keywords = a?.keywords || [];
  } else if (triggerMode === "keyword") {
    keywords = $("gtKeywords").value.split(/[,，]/).map((s) => s.trim()).filter(Boolean);
  }
  try {
    const res = await fetch(`/ag-ui/agents/register?memberId=${encodeURIComponent(state.memberId)}`, {
      method: "POST",
      headers: state.token
        ? { "Content-Type": "application/json", Authorization: `Bearer ${state.token}` }
        : { "Content-Type": "application/json" },
      body: JSON.stringify({
        agentId: m.memberId, nickname: m.nickname, groupId: gid,
        triggerMode, keywords, override: !inherit,
      }),
    });
    if (!res.ok) throw new Error("HTTP " + res.status);
    // 本地回显，无需等下一次快照
    m.triggerMode = triggerMode;
    m.keywords = keywords;
    m.isTriggerOverridden = !inherit;
    closeGroupTriggerModal();
    renderMembers();
    toast(inherit
      ? t("member.triggerRestored", { name: m.nickname })
      : t("member.triggerSaved", { name: m.nickname }));
  } catch (ex) {
    toast(t("common.saveFail", { err: ex.message }));
  }
}

/* ============ 消息区虚拟滚动 ============ */

/**
 * 虚拟滚动：消息区只渲染视口附近窗口内的消息（+上下缓冲），其余高度由 .vtop / .vbottom 占位；
 * 配合「加载更早消息」服务端分页，大知聚历史再多 DOM 节点也保持恒定、滚动流畅。
 */
const VIRTUAL_WINDOW = 40;   // 视口窗口内渲染的消息条数
const VIRTUAL_BUFFER = 25;   // 窗口上下额外缓冲（提前渲染，滚动不白屏；大滚轮/动量滚动吸收用）
const EST_MSG_HEIGHT = 88;   // 未测量消息的估算行高（含间距；虚拟窗口模式才用到）
const MSG_GAP = 10;          // .messages 的 flex gap
const LOAD_MORE_HEIGHT = 42; // 「加载更早消息」行高
const PLAIN_LIMIT = 200;     // 消息数 ≤ 该值时整表渲染（与服务端 MessageHistoryLimit 一致：零占位/零估算/零伪影），超出才启用虚拟窗口
const RECALL_WINDOW_MS = 3 * 60 * 1000; // 撤回时限：仅允许撤回发送 3 分钟内的消息（与服务端强校验一致，按钮按时间隐藏）

/** 虚拟滚动运行时状态（仅当前知聚；切知聚 / 重置时清空）。
 * stickBottom = 用户“停靠底部”意图：滚动监听在贴底时置位、一上滑即清除；渲染不消耗。
 * avgH = 实测行高的滑动平均，用于未测量消息的估算（比固定 88px 更贴近真实，减少滚动条漂移）。 */
let vscroll = { start: 0, end: 0, heights: null, raf: 0, force: false, stickBottom: false, avgH: 0 };
let vscrollRO = null; // ResizeObserver：测量已渲染消息高度（图片加载 / 展开 / 流式增长）
let followRaf = 0;    // 流式跟随跳转的 rAF 合并：每帧最多一次布局/滚动，避免高频增量逐条强制布局

function rowHeight(m) { return (m._h || vscroll.avgH || EST_MSG_HEIGHT) + MSG_GAP; }

/** 累计高度表 heights[i] = 前 i 条消息的总高度（含间距）。 */
function computeHeights(msgs) {
  const h = new Float64Array(msgs.length + 1);
  for (let i = 0; i < msgs.length; i++) h[i + 1] = h[i] + rowHeight(msgs[i]);
  return h;
}

/** 二分：返回包含 target 的那一行下标（heights[i] <= target < heights[i+1]）。 */
function lowerBound(h, target) {
  let lo = 0, hi = h.length;
  while (lo < hi) { const mid = (lo + hi) >> 1; if (h[mid] <= target) lo = mid + 1; else hi = mid; }
  return Math.max(0, lo - 1);
}

/** 渲染 / 刷新顶部「加载更早消息」行（未加载完时始终存在，滚动到顶部即可点击翻页）。 */
function renderLoadMoreRow(el, r) {
  let row = el.querySelector(".load-more-row");
  if (!r.allLoaded) {
    if (!row) {
      row = document.createElement("div");
      row.className = "load-more-row";
      row.innerHTML = `<button class="load-more-btn">${escapeHtml(t("msg.loadEarlier"))}</button>`;
      row.querySelector(".load-more-btn").onclick = loadEarlierMessages;
      el.insertBefore(row, el.querySelector(".vtop") || el.firstChild); // 位于 .vtop 之上，避免抢首位置
    }
    const btn = row.querySelector(".load-more-btn");
    btn.textContent = r.loadingEarlier ? t("msg.loading") : t("msg.loadEarlier");
    btn.disabled = !!r.loadingEarlier;
  } else if (row) {
    row.remove();
  }
}

/** 只更新占位高度（窗口不变时；布局测量变化走此轻量路径，避免整窗重建）。 */
function updateSpacers() {
  const el = $("messages");
  const r = state.activeGroupId ? room(state.activeGroupId) : null;
  if (!r || !vscroll.heights) return;
  const h = computeHeights(activeTopicMessages(r));
  vscroll.heights = h;
  const top = el.querySelector(".vtop");
  const bot = el.querySelector(".vbottom");
  if (top) top.style.height = h[vscroll.start] + "px";
  if (bot) bot.style.height = (h[h.length - 1] - h[vscroll.end]) + "px";
}

/** 核心渲染：按当前滚动位置计算窗口，重建窗口内消息节点，占位其余高度。 */
function virtualRender() {
  vscroll.raf = 0;
  const el = $("messages");
  const scrollTopBefore = el.scrollTop; // 重建前捕获：用于高度修正（估算→实测）后的滚动锚定补偿
  const r = state.activeGroupId ? room(state.activeGroupId) : null;
  const msgs = activeTopicMessages(r);
  const n = msgs.length;
  if (!r || n === 0) {
    // 已选中知聚但当前话题无消息：注入空态提示（避免误显示「选择一个群开始对话」的 CSS 占位，那样语义误导为尚未选群）；
    // 未选中任何知聚（activeGroupId 为空）才让元素置空，交给 #messages:empty::before 显示「选择群」引导。
    el.innerHTML = state.activeGroupId
      ? `<div class="msg-empty-hint">${escapeHtml(t("msg.noMessages"))}</div>`
      : "";
    vscroll.start = vscroll.end = 0;
    vscroll.heights = null;
    return;
  }

  // 之前若有空态提示（n 由 0 变 >0 的残留，如快照比首次渲染晚到），清理掉再正常渲染
  const emptyHint = el.querySelector(".msg-empty-hint");
  if (emptyHint) emptyHint.remove();

  // 结构：可选 loadMore 行 + .vtop 占位 + 窗口消息 + .vbottom 占位
  let top = el.querySelector(".vtop");
  if (!top) { top = document.createElement("div"); top.className = "vtop"; el.prepend(top); }
  let bot = el.querySelector(".vbottom");
  if (!bot) { bot = document.createElement("div"); bot.className = "vbottom"; el.append(bot); }
  renderLoadMoreRow(el, r);

  vscroll.heights = computeHeights(msgs);
  const h = vscroll.heights;
  const loadH = r.allLoaded ? 0 : LOAD_MORE_HEIGHT; // loadMore 行占据的顶部高度

  const anchor = Math.min(lowerBound(h, Math.max(0, el.scrollTop - loadH)), n - 1);
  // 小知聚（≤ 历史上限）整表渲染：无占位 / 无估算 / 无伪影；大知聚才启用虚拟窗口
  const start = n <= PLAIN_LIMIT ? 0 : Math.max(0, anchor - VIRTUAL_BUFFER);
  const end = n <= PLAIN_LIMIT ? n : Math.min(n, anchor + VIRTUAL_WINDOW + VIRTUAL_BUFFER);

  const stick = () => {
    if (!vscroll.stickBottom) return;
    // 仅在未贴底时跳转（贴底时 no-op），并保留 stickBottom——由滚动监听负责清除。
    const maxScroll = el.scrollHeight - el.clientHeight;
    if (el.scrollTop < maxScroll) {
      el.scrollTop = maxScroll;
      // 整表渲染（PLAIN）模式滚动无需重建窗口；虚拟窗口模式才需强制重建跟随新位置。
      // 流式回复时每次跳转都设 force 会导致每帧整表重建，占满主线程（输入框/成员列表卡顿根源）。
      if (n > PLAIN_LIMIT) vscroll.force = true;
      scheduleVirtualRender();
    }
  };

  if (vscroll.start === start && vscroll.end === end && !vscroll.force) {
    top.style.height = h[start] + "px";
    bot.style.height = (h[n] - h[end]) + "px";
    // 早退路径不调用 stick()：流式跟随由 scheduleFollow 负责，新消息跳转由增量/重建路径处理——
    // 避免每帧读 scrollHeight 强制布局（流式回复时输入框/成员列表卡顿的次要来源）
    return;
  }
  vscroll.force = false;
  const prevEnd = vscroll.end;
  vscroll.start = start;
  vscroll.end = end;

  // PLAIN 模式尾部增量：新消息只追加渲染，不重建已有 DOM（发送消息 / 数字员工回复 / 快照合并不卡）。
  // 头部插入（loadEarlierMessages）已强制 force 走整表重建；撤回/工具行/结束等也走整表。
  if (start === 0 && prevEnd > 0 && prevEnd < end) {
    const frag = document.createDocumentFragment();
    for (let i = prevEnd; i < end; i++) frag.appendChild(msgDom(msgs[i], r));
    const inserted = [...frag.children]; // insertBefore 会把 frag 子节点移入 DOM，之后 frag.children 为空
    el.insertBefore(frag, bot);
    for (const n of inserted) renderMermaidBlocks(n); // 新增消息的 Mermaid 图表渲染
    const nodes = el.querySelectorAll(".vmsg");
    const from = nodes.length - (end - prevEnd);
    let sumH = 0, cntH = 0;
    for (let i = from; i < nodes.length; i++) {
      const m = msgs[prevEnd + (i - from)];
      if (!m.sys && !m.streaming && !m.recalled) attachToggleButton(nodes[i], m);
      m._h = nodes[i].offsetHeight;
      if (m._h > 0) { sumH += m._h; cntH++; }
      vscrollRO?.observe(nodes[i]);
    }
    if (cntH > 0) {
      const avg = sumH / cntH;
      vscroll.avgH = vscroll.avgH ? vscroll.avgH * 0.7 + avg * 0.3 : avg;
      vscroll.avgH = Math.min(400, Math.max(30, vscroll.avgH));
    }
    vscroll.heights = computeHeights(msgs);
    top.style.height = "0px";
    bot.style.height = "0px";
    stick();
    return;
  }

  // 重建窗口节点
  for (const child of [...el.children]) {
    if (child.classList.contains("vmsg")) child.remove();
  }
  const frag = document.createDocumentFragment();
  for (let i = start; i < end; i++) frag.appendChild(msgDom(msgs[i], r));
  const inserted = [...frag.children]; // insertBefore 会把 frag 子节点移入 DOM，之后 frag.children 为空
  el.insertBefore(frag, bot);
  for (const n of inserted) renderMermaidBlocks(n); // 重建窗口后渲染 Mermaid 图表

  // 测量已渲染消息高度（缓存到 m._h）并挂载折叠按钮；观察尺寸变化。
  // ResizeObserver 负责流式增长 / 图片加载 / 展开收起后的行高重测：只刷新占位，窗口索引不变。
  vscrollRO ??= new ResizeObserver((entries) => {
    const r = state.activeGroupId ? room(state.activeGroupId) : null;
    let changed = false;
    for (const entry of entries) {
      const m = r?.messages.find((x) => x.id === entry.target.getAttribute("data-mid"));
      if (!m) continue;
      if (m._h !== entry.target.offsetHeight) { m._h = entry.target.offsetHeight; changed = true; }
  // 高度变化（图片/字体延迟加载等）后重新评估折叠：此前误判“不溢出”的补测并挂按钮
      if (!m.sys && !m.streaming && !m.expanded && m.long === false) {
        const content = entry.target.querySelector(".content");
        if (content && measureLong(content)) {
          m.long = true;
          attachToggleButton(entry.target, m);
        }
      }
    }
    if (changed) updateSpacers();
  });
  vscrollRO.disconnect();
  const vmsgs = el.querySelectorAll(".vmsg");
  let sumH = 0, cntH = 0;
  for (let i = 0; i < vmsgs.length; i++) {
    const m = msgs[start + i];
    if (!m.sys && !m.streaming && !m.recalled) attachToggleButton(vmsgs[i], m);
    m._h = vmsgs[i].offsetHeight;
    if (m._h > 0) { sumH += m._h; cntH++; }
    vscrollRO.observe(vmsgs[i]);
  }
  // 滑动平均实测行高 → 未测量消息的估算贴近真实（大幅滚动时滚动条总长漂移更小）
  if (cntH > 0) {
    const avg = sumH / cntH;
    vscroll.avgH = vscroll.avgH ? vscroll.avgH * 0.7 + avg * 0.3 : avg;
    vscroll.avgH = Math.min(400, Math.max(30, vscroll.avgH));
  }
  // 用实测高度重算占位，保证滚动条总长与内容一致
  const newH = computeHeights(msgs);
  vscroll.heights = newH;
  // 手动 scroll anchoring：估算高度（EST→实测）修正后坐标漂移，补偿 scrollTop 使视口内容不跳；
  // 贴底跟随（stickBottom）时交给 stick() 直接定位到新底部，不做补偿。
  const drift = h[anchor] - newH[anchor];
  if (!vscroll.stickBottom && drift !== 0) {
    el.scrollTop = Math.max(0, scrollTopBefore - drift);
  }
  top.style.height = newH[start] + "px";
  bot.style.height = (newH[n] - newH[end]) + "px";
  stick();
}

/** rAF 合并的渲染调度（消息追加 / 滚动 / 高度变化等高频场景只排一帧）。 */
function scheduleVirtualRender() {
  if (vscroll.raf) return;
  vscroll.raf = requestAnimationFrame(virtualRender);
}

/** 流式跟随：停靠底部时在下一帧滚动到底部（合并高频增量，每帧一次布局）。 */
function scheduleFollow() {
  if (followRaf) return;
  followRaf = requestAnimationFrame(() => {
    followRaf = 0;
    const el = $("messages");
    if (vscroll.stickBottom) el.scrollTop = el.scrollHeight;
  });
}

/** 切知聚 / 重置时清空虚拟滚动状态与消息 DOM。 */
function resetVScroll() {
  vscrollRO?.disconnect();
  vscroll = { start: 0, end: 0, heights: null, raf: 0, force: false, stickBottom: false, avgH: 0 };
  $("messages").innerHTML = "";
}

/** 兼容入口：整表重建场景（快照 / 解散 / 切知聚）统一走虚拟滚动调度。 */
function renderMessages() { scheduleVirtualRender(); }

/** 快照 / 分页消息 → 前端消息对象。 */
function snapshotToMessage(sm) {
  return {
    id: sm.messageId, senderId: sm.senderId, senderNickname: sm.senderNickname,
    senderType: sm.senderId?.startsWith("agent_") ? "agent" : "user",
    content: sm.content || "", attachments: sm.attachments || [], mentions: sm.mentions || [], mentionAll: !!sm.mentionAll,
    topicId: sm.topicId || "main", replyTo: sm.replyToMessageId || null,
    timestamp: Number(sm.timestamp) || 0,
    time: fmtTime(sm.timestamp), recalled: false, streaming: false, plan: null,
  };
}

/** 分页加载更早消息（服务端游标 before=当前首条），前插并保持顶部可见。 */
async function loadEarlierMessages() {
  const gid = state.activeGroupId;
  const r = gid ? room(gid) : null;
  if (!r || r.loadingEarlier || r.allLoaded) return;
  const topicMsgs = activeTopicMessages(r);
  if (topicMsgs.length === 0) return;
  r.loadingEarlier = true;
  scheduleVirtualRender(); // 立即显示「加载中…」
  const el = $("messages");
  const scrollBefore = el.scrollTop;
  const topicId = state.activeTopicId || "main";
  try {
    // 游标取当前话题第一条非系统行消息（系统行 id 为本地生成的 sys_xxx，服务端无此消息，作游标会导致翻页永久失效）
    const firstReal = topicMsgs.find((m) => !m.sys && !String(m.id || "").startsWith("sys_"));
    if (!firstReal) { r.allLoaded = true; return; }
    const before = firstReal.id;
    const res = await fetch(`/ag-ui/group/${encodeURIComponent(gid)}/topics/${encodeURIComponent(topicId)}/messages?before=${encodeURIComponent(before)}&count=50`);
    if (!res.ok) throw new Error("HTTP " + res.status);
    const older = await res.json();
    if (!Array.isArray(older) || older.length === 0) { r.allLoaded = true; return; }
    const known = new Set(r.messages.map((m) => m.id));
    const added = older.filter((m) => !known.has(m.messageId)).map(snapshotToMessage);
    if (added.length > 0) {
      // 插入到当前话题首条消息之前（保持话题内时间序；splice 而非 unshift，避免排到其他话题消息前面）
      const insertIdx = Math.max(0, r.messages.indexOf(topicMsgs[0]));
      r.messages.splice(insertIdx, 0, ...added);
      for (const m of added) state.msgIndex.set(m.id, m);
      trimMessages(r); // 内存上限：超限裁剪最旧消息（游标取当前首条，翻页继续可用）
      vscroll.force = true; // 头部插入：整表重建（PLAIN 增量路径仅适用尾部新增，避免索引错位）
      // 顶部锚定：翻页后视口应停在刚加载的页首（scrollTop 越界时浏览器自动钳制）；已切知聚则不动当前视图
      if (state.activeGroupId === gid && scrollBefore > 0) el.scrollTop = scrollBefore + added.length * (vscroll.avgH || EST_MSG_HEIGHT);
    }
    if (older.length < 50) r.allLoaded = true; // 一页不足 → 已到最早
  } catch (ex) {
    toast(t("msg.loadEarlierFail", { err: ex.message }));
  } finally {
    r.loadingEarlier = false;
    scheduleVirtualRender();
  }
}

/** 统一线条图标（lucide 风格内联 SVG，stroke=currentColor 跟随主题，避免 emoji 跨平台风格不一）。 */
const ICONS = {
  topic: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M21 15a2 2 0 0 1-2 2H7l-4 4V5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2z"/></svg>',
  copy: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="9" y="9" width="13" height="13" rx="2"/><path d="M5 15H4a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h9a2 2 0 0 1 2 2v1"/></svg>',
  refresh: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M21 12a9 9 0 1 1-2.64-6.36"/><polyline points="21 3 21 9 15 9"/></svg>',
  recall: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M3 6h18"/><path d="M8 6V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2"/><path d="M19 6l-1 14a2 2 0 0 1-2 2H8a2 2 0 0 1-2-2L5 6"/><line x1="10" y1="11" x2="10" y2="17"/><line x1="14" y1="11" x2="14" y2="17"/></svg>',
  reply: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><polyline points="9 17 4 12 9 7"/><path d="M20 18v-2a4 4 0 0 0-4-4H4"/></svg>',
  code: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><polyline points="16 18 22 12 16 6"/><polyline points="8 6 2 12 8 18"/></svg>',
  stop: '<svg viewBox="0 0 24 24" fill="currentColor" stroke="none"><rect x="6" y="6" width="12" height="12" rx="2"/></svg>',
};
function icon(name) { return ICONS[name] || ""; }

/** 构建单条消息的 DOM 节点（vmsg 标记类供虚拟窗口重建/测量定位；折叠按钮由 virtualRender 挂载）。 */
function msgDom(m, r) {
  if (m.sys) {
    const el = sysLine(m.sys, false);
    el.classList.add("vmsg");
    el.setAttribute("data-mid", m.id); // 供 ResizeObserver 高度测量定位
    return el;
  }
  const isMine = m.senderId === state.memberId;
  const div = document.createElement("div");
  div.className = "msg" + (isMine ? " mine" : "") + (m.senderType === "agent" ? " agent" : "") + " vmsg";
  div.setAttribute("data-mid", m.id); // 供展开/收起、流式增量、撤回等局部更新定位
  // ARIA（5.3）：每条消息作为列表项，附发件人 + 时间 + 内容摘要标签供读屏朗读
  div.setAttribute("role", "listitem");
  const labelText = (m.sys ? "" : (m.senderNickname || m.senderId) + ", " + m.time + ", ") + String(m.content || "").replace(/\s+/g, " ").slice(0, 120);
  div.setAttribute("aria-label", m.sys ? t("msg.systemMessage", { text: m.sys }) : labelText);

  // 撤回按钮：本人消息或当前用户是知聚主 / 管理员（服务端同样校验）+ 发送 3 分钟内；已撤回 / 流式中 / 系统行不显示
  const meRole = r.members?.find((x) => x.memberId === state.memberId)?.role;
  const canRecall = !m.sys && !m.recalled && !m.streaming
    && (m.senderId === state.memberId || meRole === "owner" || meRole === "admin")
    && (Number(m.timestamp) > 0 && Date.now() - Number(m.timestamp) <= RECALL_WINDOW_MS);
  // 复制按钮：非系统行 / 未撤回 / 内容非空（数字员工消息复制剥离结构化附件后的显示文本）
  const canCopy = !m.sys && !m.recalled && !!displayTextOf(m);
  // 回复按钮：非系统行 / 未撤回即可引用（流式进行中也允许，发送时按当时状态取内容）
  const canReply = !m.sys && !m.recalled;
  // 重新回答按钮：仅当前话题最后一条消息且为数字员工消息（流式中不显示，END 后补挂）
  const canRegenerate = isLastAgentMsg(m, r);
  // 停止生成按钮：数字员工消息流式进行中显示（点击后调停止 API，END 后随重建消失）
  const canStop = !m.sys && !m.recalled && m.streaming && m.senderType === "agent" && !!m.runId;
  const replyRef = m.replyTo ? `<div class="reply-ref">${t("msg.replyRef", { sender: escapeHtml(quoteOf(m.replyTo)) })}</div>` : "";
  // @ 提及回显：chips 选择的成员（或 @全体）在消息内以标签形式展示
  const mentionTags = (() => {
    if (m.mentionAll) return `<span class="mention-tag">${escapeHtml(t("msg.mentionAllTag"))}</span>`;
    if (!m.mentions || m.mentions.length === 0) return "";
    return m.mentions.map((id) => `<span class="mention-tag">@${escapeHtml(memberName(id))}</span>`).join(" ");
  })();
  const toolCalls = (() => {
    // 已完成（done=true）的工具行不渲染；字符串旧数据无状态信息，按调用中展示
    const rows = (m.toolCalls || []).filter((tc) => typeof tc === "string" || !tc.done);
    return rows.length
      ? `<div class="tool-calls">${rows.map((tc) => {
          const t = typeof tc === "string" ? { id: null, name: tc, done: false } : tc;
          return `<div class="tool-call"${t.id ? ` data-toolcall-id="${escapeHtml(t.id)}"` : ""}>🔧 ${window.t("msg.toolCalling", { name: escapeHtml(t.name || "tool") })}</div>`;
        }).join("")}</div>`
      : "";
  })();
  // 外部 AG-UI 结构化响应（agent 消息）：剥离 JSON 附件信息，转为显示文本 + 附件卡片。
  // 解析结果缓存到 m._bridgeParse（内容不变时避免每次渲染重复 JSON 解析）。
  let displayText = m.waiting ? t("msg.waitingConfirm") : m.content;
  let bridgeAttachments = [];
  if (!m.streaming && !m.recalled && m.senderType === "agent") {
    let parsed = m._bridgeParse;
    if (!parsed) { parsed = parseBridgeResponse(m.content); m._bridgeParse = parsed; }
    displayText = parsed.text;
    bridgeAttachments = parsed.attachments;
  }
  // 思考过程块（AG-UI 思考模式）：流式中展开实时可见，结束后默认收起；与正文分离的灰底折叠块。
  // 与正文一致：流式中纯文本（避免每增量解析），结束后 Markdown 渲染
  const thinking = (m.reasoning && !m.recalled && !m.sys)
    ? `<details class="thinking" ${m.streaming ? "open" : ""}><summary>${m.streaming ? t("msg.thinkingStreaming") : t("msg.thinkingDone")}</summary><div class="thinking-body ${m.streaming ? "" : "md"}">${m.streaming ? escapeHtml(m.reasoning) : renderMarkdown(m.reasoning)}</div></details>`
    : "";
  // 附件 URL 过 scheme 白名单：外部桥接 / 服务端数据可能含 javascript: 等危险协议，非法则跳过该附件
  const allAtts = [...(m.attachments || []), ...bridgeAttachments]
    .filter((att) => safeUrl(att.url, att.kind === "image" || att.kind === "audio"));
  // 富媒体（5.2）：全部为图片时使用平铺网格；否则回退流式横向布局
  const imgGrid = allAtts.length > 0 && allAtts.every((att) => att.kind === "image");
  const attachments = allAtts.map((att) => {
    const href = escapeHtml(authedAssetUrl(att.url));
    const name = escapeHtml(att.name);
    if (att.kind === "image") {
      return `<a class="att-img-wrap" href="${href}" target="_blank" rel="noopener" title="${name}"><img class="att-img" src="${href}" alt="${name}" loading="lazy" /></a>`;
    }
    if (att.kind === "audio") {
      const meta = att.size > 0 ? fmtBytes(att.size) : t("msg.attachmentAudio");
      return `<div class="att-audio"><span title="${escapeHtml(t("msg.attachmentAudio"))}">🎤</span><audio controls preload="none" src="${href}"></audio><span class="att-meta">${meta}</span></div>`;
    }
    const icon = att.kind === "text" || att.kind === "document" ? "📄" : "📎";
    const meta = att.size > 0 ? fmtBytes(att.size) : t("msg.attachmentDownload");
    return `<a class="att-file" href="${href}" target="_blank" rel="noopener" title="${escapeHtml(t("msg.attachmentTitle", { kind: att.kind }))}">${icon} ${name}<span class="att-meta">${meta}</span></a>`;
  }).join("");
  const avatar = (() => {
    const sender = r.members.find((x) => x.memberId === m.senderId);
    // 头像 URL 过 scheme 白名单（图片场景放行 data:image/），非法回退默认图标；站内头像追加会话令牌
    const avatarUrl = authedAssetUrl(safeUrl(sender?.avatar, true));
    return avatarUrl
      ? `<img class="avatar-img" src="${escapeHtml(avatarUrl)}" alt="" onerror="this.style.display='none'" />`
      : (m.senderType === "agent" ? "🤖" : "🧑");
  })();
  // 长内容折叠：流式结束且未展开时限制 3 行（是否溢出由 attachToggleButton 一次性检测）
  const clamp = !m.streaming && !m.expanded ? " clamp3" : "";
  // 流式结束且未撤回 → 以 Markdown 渲染；流式过程中用纯文本避免反复解析闪烁
  const md = !m.streaming && !m.recalled ? " md" : "";
  // 渲染缓存：已结束的消息只解析一次 Markdown，历史多时避免每次全量重渲染都重复解析
  let contentHtml = m._html;
  if (contentHtml === undefined) {
    contentHtml = renderMessageContent(displayText, m);
    if (!m.streaming && !m.recalled) m._html = contentHtml;
  }
  // 流式截断提示：内容超限（正文 / 思考）时在消息头部提示，避免误以为回复不完整
  const truncatedHint = m.truncated ? `<div class="msg-truncated">${escapeHtml(t("msg.truncated"))}</div>` : "";
  // 审批卡片嵌入数字员工回复内部：独立块（interaction-block），紧跟内容区，不受流式内容局部更新影响；
  // 触发者做出选择后隐藏（resolved）——历史重建时也不再渲染
  const interactionBlock = m.interaction && !m.interaction.resolved && !m.recalled
    ? `<div class="interaction-block">${renderInteractionCard(m)}</div>`
    : "";
  // 技能调用链（链路可视化）：仅 agent 消息、非撤回、有链数据时渲染嵌套调用卡片
  const chainCard = (m.senderType === "agent" && !m.recalled && !m.sys && m.agentChain)
    ? renderChainCard(m.agentChain)
    : "";
  div.innerHTML = `
    <div class="avatar">${avatar}</div>
    <div class="body">
      <div class="head"><span class="nick">${escapeHtml(m.senderNickname)}</span><span class="time">${m.time}</span>${m.sys ? "" : `<button class="topic-start-btn" title="${escapeHtml(t("msg.startTopic"))}">` + icon("topic") + "</button>"}${canStop ? `<button class="stop-btn" title="${escapeHtml(t("msg.stopGenerating"))}">` + icon("stop") + "</button>" : ""}${canReply ? `<button class="reply-btn" title="${escapeHtml(t("msg.reply"))}">` + icon("reply") + "</button>" : ""}${canCopy ? `<button class="copy-btn" title="${escapeHtml(t("msg.copy"))}">` + icon("copy") + "</button>" : ""}${canRegenerate ? `<button class="regenerate-btn" title="${escapeHtml(t("msg.regenTitle"))}">` + icon("refresh") + "</button>" : ""}${canRecall ? `<button class="recall-btn" title="${escapeHtml(t("msg.recallTitle"))}">` + icon("recall") + "</button>" : ""}</div>
      ${replyRef}
      ${mentionTags ? `<div class="mention-line">${mentionTags}</div>` : ""}
      ${thinking}
      <div class="content ${m.recalled ? "recalled" : ""} ${m.streaming ? "streaming" : ""} ${m.waiting ? "waiting" : ""}${clamp}${md}">${truncatedHint}${contentHtml}</div>
      ${interactionBlock}
      ${attachments && !m.recalled ? `<div class="attachments${imgGrid ? " img-grid" : ""}">${attachments}</div>` : ""}
      ${m.plan && m.plan.steps && m.plan.steps.length && !m.recalled ? `<div class="plan-card">${renderPlanCard(m.plan)}</div>` : ""}
      ${chainCard}
      ${toolCalls}
    </div>`;
  const topicStartBtn = div.querySelector(".topic-start-btn");
  if (topicStartBtn) topicStartBtn.onclick = (e) => { e.stopPropagation(); openTopicModalFromMessage(m.id); };
  const stopBtn = div.querySelector(".stop-btn");
  if (stopBtn) bindStopButton(stopBtn, m);
  const replyBtn = div.querySelector(".reply-btn");
  if (replyBtn) replyBtn.onclick = (e) => { e.stopPropagation(); setReplyTo(m); };
  const replyRefEl = div.querySelector(".reply-ref");
  if (replyRefEl) replyRefEl.onclick = (e) => { e.stopPropagation(); scrollToMessage(m.replyTo); };
  const copyBtn = div.querySelector(".copy-btn");
  if (copyBtn) bindCopyButton(copyBtn, m);
  const regenBtn = div.querySelector(".regenerate-btn");
  if (regenBtn) bindRegenerateButton(regenBtn, m);
  const recallBtn = div.querySelector(".recall-btn");
  if (recallBtn) bindRecallButton(recallBtn, m);
  // 人机交互卡片的批准 / 拒绝按钮
  bindInteractionButtons(div, m);
  return div;
}

/** 消息显示文本：数字员工消息剥离结构化 JSON 附件信息后的正文（解析缓存到 m._bridgeParse）；其余消息为原始内容。 */
function displayTextOf(m) {
  if (!m || m.recalled) return "";
  if (m.senderType === "agent") {
    if (!m._bridgeParse) m._bridgeParse = parseBridgeResponse(m.content);
    return m._bridgeParse.text || "";
  }
  return m.content || "";
}

/** 是否「当前话题最后一条消息且为数字员工消息」（重新回答按钮的显示条件）。 */
function isLastAgentMsg(m, r) {
  if (!m || m.sys || m.recalled || m.streaming || m.senderType !== "agent") return false;
  if (!r) return false;
  const list = activeTopicMessages(r); // 消息均属于当前话题（渲染视图）
  for (let i = list.length - 1; i >= 0; i--) {
    const x = list[i];
    if (x.sys) continue;
    return x.id === m.id && x.senderType === "agent" && !x.recalled && !x.streaming;
  }
  return false;
}

/** 复制文本：优先异步剪贴板 API，失败回退隐藏 textarea + execCommand（老内核 / 非安全上下文）。 */
function copyText(text) {
  if (navigator.clipboard?.writeText) {
    return navigator.clipboard.writeText(text).then(() => true, () => fallbackCopyText(text));
  }
  return Promise.resolve(fallbackCopyText(text));
}
function fallbackCopyText(text) {
  const ta = document.createElement("textarea");
  ta.value = text;
  ta.style.position = "fixed";
  ta.style.opacity = "0";
  document.body.appendChild(ta);
  ta.select();
  let ok = false;
  try { ok = document.execCommand("copy"); } catch { ok = false; }
  ta.remove();
  return ok;
}

/** 绑定停止生成按钮：调 /ag-ui/group/agent/stop 取消当前流式运行（触发者本人或管理员可执行，服务端校验）。 */
function bindStopButton(btn, m) {
  btn.onclick = async (e) => {
    e.stopPropagation();
    if (!m.runId) { toast(t("msg.runEnded")); return; }
    try {
      const res = await fetch("/ag-ui/group/agent/stop", {
        method: "POST",
        headers: { "Content-Type": "application/json", Authorization: `Bearer ${state.token}` },
        body: JSON.stringify({ runId: m.runId, groupId: state.activeGroupId, operatorId: state.memberId }),
      });
      const data = await res.json().catch(() => null);
      if (!res.ok) { toast(t("msg.stopFail", { err: errMsg(data, `停止失败（${res.status}）`) })); return; }
      toast(data?.stopped ? t("msg.stoppedGenerating") : t("msg.runEnded"));
      btn.disabled = true; // 已请求停止：避免重复点击
    } catch (ex) { toast(t("msg.stopFail", { err: ex.message })); }
  };
}

/** 绑定复制按钮：复制消息显示文本（数字员工消息为剥离附件后的正文）。 */
function bindCopyButton(btn, m) {
  btn.onclick = async (e) => {
    e.stopPropagation();
    const text = displayTextOf(m);
    if (!text) { toast(t("msg.noCopyContent")); return; }
    const ok = await copyText(text);
    toast(ok ? t("msg.copied") : t("msg.copyFail"));
  };
}

/** 绑定重新回答按钮：调 /ag-ui/group/message/regenerate（服务端校验最后一条数字员工消息 + 触发者 / 管理员权限）。 */
function bindRegenerateButton(btn, m) {
  btn.onclick = async (e) => {
    e.stopPropagation();
    if (!confirm(t("msg.regenConfirm"))) return;
    try {
      const res = await fetch("/ag-ui/group/message/regenerate", {
        method: "POST",
        headers: { "Content-Type": "application/json", Authorization: `Bearer ${state.token}` },
        body: JSON.stringify({ groupId: state.activeGroupId, topicId: state.activeTopicId || "main", messageId: m.id, operatorId: state.memberId }),
      });
      const data = await res.json().catch(() => null);
      if (!res.ok) { toast(t("msg.regenFail", { err: errMsg(data, `重新生成失败（${res.status}）`) })); return; }
      toast(t("msg.regenQueued"));
      suppressedRecallMessageIds.add(m.id); // 旧回答被撤回属预期中间步骤：抑制“消息已撤回”系统提示行
      applyRecallLocal(state.activeGroupId, m.id); // 旧回答立即标记撤回（GROUP_MESSAGE_RECALLED 事件到达时幂等）
    } catch (ex) { toast(t("msg.regenFail", { err: ex.message })); }
  };
}

/**
 * 补挂头部操作按钮（复制 / 重新回答 / 撤回）：流式期间渲染头部时这些按钮不显示，
 * END（或局部更新）后由这里补挂；滚动 / 刷新整表重建后由 msgDom 正常渲染。
 */
function attachHeadActions(msgEl, m, r) {
  const head = msgEl?.querySelector(".head");
  if (!head || m.sys) return;
  const meRole = r?.members?.find((x) => x.memberId === state.memberId)?.role;
  const canCopy = !m.recalled && !!displayTextOf(m);
  const canRegenerate = isLastAgentMsg(m, r);
  const canRecall = !m.recalled
    && (m.senderId === state.memberId || meRole === "owner" || meRole === "admin")
    && (Number(m.timestamp) > 0 && Date.now() - Number(m.timestamp) <= RECALL_WINDOW_MS);
  if (canCopy && !head.querySelector(".copy-btn")) {
    const btn = document.createElement("button");
    btn.className = "copy-btn"; btn.type = "button"; btn.title = t("msg.copy");
    btn.innerHTML = icon("copy");
    bindCopyButton(btn, m);
    head.appendChild(btn);
  }
  if (canRegenerate && !head.querySelector(".regenerate-btn")) {
    const btn = document.createElement("button");
    btn.className = "regenerate-btn"; btn.type = "button"; btn.title = t("msg.regenTitle");
    btn.innerHTML = icon("refresh");
    bindRegenerateButton(btn, m);
    head.appendChild(btn);
  }
  if (canRecall && !head.querySelector(".recall-btn")) {
    const btn = document.createElement("button");
    btn.className = "recall-btn"; btn.type = "button"; btn.title = t("msg.recallTitle");
    btn.innerHTML = icon("recall");
    bindRecallButton(btn, m);
    head.appendChild(btn);
  }
}

/** 绑定撤回按钮：确认后调 /ag-ui/group/message/recall（服务端校验本人 / 知聚主 / 管理员 + 3 分钟时限；撤回后内容隐藏并清除记忆）。 */
function bindRecallButton(btn, m) {
  btn.onclick = async (e) => {
    e.stopPropagation();
    if (!confirm(t("msg.recallConfirm"))) return;
    try {
      const res = await fetch("/ag-ui/group/message/recall", {
        method: "POST",
        headers: { "Content-Type": "application/json", Authorization: `Bearer ${state.token}` },
        body: JSON.stringify({ groupId: state.activeGroupId, messageId: m.id, operatorId: state.memberId }),
      });
      const data = await res.json().catch(() => null);
      if (!res.ok) { toast(t("msg.recallFail", { err: errMsg(data, `撤回失败（${res.status}）`) })); return; }
      toast(t("msg.recalled"));
      applyRecallLocal(state.activeGroupId, m.id); // 立即本地更新（不等事件广播），事件到达时幂等
    } catch (ex) { toast(t("msg.recallFail", { err: ex.message })); }
  };
}

/** 测量 .content 是否溢出 3 行。调用前提：折叠态（clamp3 已应用）——
 * Blink/WebKit/Gecko 的 -webkit-line-clamp 元素 scrollHeight 均为完整内容高度，
 * 直接对比即可；不做类切换（那会逐条强制整页布局，大知聚首次渲染卡顿的根源）。 */
function measureLong(content) {
  return content.scrollHeight > content.clientHeight + 2;
}

/**
 * 长消息折叠：仅对单条消息处理；溢出判定（scrollHeight > clientHeight）只计算一次并缓存到
 * m.long——避免旧实现每次全量渲染都对所有消息触发布局读取（layout thrash）。
 */
function attachToggleButton(msgEl, m) {
  const content = msgEl.querySelector(".content");
  if (!content) return;
  if (m.long === undefined && !m.expanded) {
    // 非折叠态不测量（异常路径，避免把完整高度当 3 行高度误判）
    if (!content.classList.contains("clamp3")) return;
    m.long = measureLong(content);
  }
  const body = content.parentElement;
  const collapsed = !m.expanded && m.long;
  const expanded = m.expanded && m.long;
  if (!collapsed && !expanded) return;
  const text = collapsed ? t("msg.more") : t("msg.collapse");
  const old = body.querySelector(".toggle-btn");
  if (old) {
    if (old.textContent === text) return; // 按钮已就位，无需重建
    old.remove();
  }
  const btn = document.createElement("button");
  btn.className = "toggle-btn";
  btn.textContent = text;
  btn.onclick = () => toggleExpandMessage(m);
  body.appendChild(btn);
}

/**
 * 展开 / 收起单条消息：只更新该消息的 DOM（clamp 类与按钮），
 * 不重渲染整个列表，因此不会把滚动位置带到底部；
 * 消息已移出视口时按就近原则滚动到它（visible 则保持当前位置）。
 */
function toggleExpandMessage(m) {
  m.expanded = !m.expanded;
  const el = $("messages");
  const msgEl = el.querySelector(`[data-mid="${cssEsc(m.id)}"]`);
  if (!msgEl) return;
  const content = msgEl.querySelector(".content");
  content.classList.toggle("clamp3", !m.expanded);
  const body = content.parentElement;
  const old = body.querySelector(".toggle-btn");
  if (old) old.remove();
  if (m.expanded) {
    const btn = document.createElement("button");
    btn.className = "toggle-btn";
    btn.textContent = t("msg.collapse");
    btn.onclick = () => toggleExpandMessage(m);
    body.appendChild(btn);
  } else if (m.long) {
    const btn = document.createElement("button");
    btn.className = "toggle-btn";
    btn.textContent = t("msg.more");
    btn.onclick = () => toggleExpandMessage(m);
    body.appendChild(btn);
  }
  // 内容不在视口内时才就近滚动（可见则保持当前位置）
  msgEl.scrollIntoView({ block: "nearest" });
}

function renderTyping() {
  const el = $("typingRow");
  const r = state.activeGroupId ? room(state.activeGroupId) : null;
  if (!r || r.typing.size === 0) { el.textContent = ""; return; }
  pruneTyping(r); // 渲染前过滤超过 5 秒未刷新的成员（无需定时器，渲染时自然收敛）
  if (r.typing.size === 0) { el.textContent = ""; return; }
  const names = [...r.typing].map((id) => memberName(id));
  el.textContent = t("msg.typing", { names: names.join("、") });
}

function renderMentionChips() {
  const el = $("mentionChips");
  el.innerHTML = "";
  for (const id of state.mentions) {
    const span = document.createElement("span");
    span.className = "chip-btn on";
    span.textContent = "@" + memberName(id) + " ✕";
    span.onclick = () => { state.mentions.delete(id); renderMentionChips(); };
    el.appendChild(span);
  }
}

function sysLine(text, isError) {
  const div = document.createElement("div");
  div.className = "sys-line" + (isError ? " error" : "");
  div.textContent = text;
  return div;
}

function memberName(id) {
  const r = state.activeGroupId ? room(state.activeGroupId) : null;
  return r?.members.find((m) => m.memberId === id)?.nickname || id;
}

/* ============ 引用回复（Reply） ============ */

/** 设置引用目标并展示输入框上方的引用条。 */
function setReplyTo(m) {
  if (!m || m.sys || m.recalled) return;
  state.replyTo = { id: m.id, sender: m.senderNickname || m.senderId, content: displayTextOf(m) };
  renderReplyBar();
  $("input").focus();
}

function clearReplyTo() {
  state.replyTo = null;
  renderReplyBar();
}

/** 渲染输入框上方的引用条（含原文摘要 + 取消按钮）。 */
function renderReplyBar() {
  const bar = $("replyBar");
  if (!bar) return;
  const rt = state.replyTo;
  bar.classList.toggle("hidden", !rt);
  if (!rt) return;
  const preview = String(rt.content || "").replace(/\s+/g, " ").slice(0, 60);
  bar.innerHTML = `<span class="reply-bar-text">${t("msg.replyBarText", { sender: escapeHtml(rt.sender), preview: escapeHtml(preview || t("msg.replyBarEmpty")) })}</span>`
    + `<button class="reply-bar-cancel" title="${escapeHtml(t("msg.cancelQuote"))}">✕</button>`;
  bar.querySelector(".reply-bar-cancel").onclick = clearReplyTo;
}

function quoteOf(messageId) {
  const r = state.activeGroupId ? room(state.activeGroupId) : null;
  const src = r?.messages.find((x) => x.id === messageId);
  if (src && src.content) {
    let t = src.content;
    if (src.senderType === "agent") {
      if (!src._bridgeParse) src._bridgeParse = parseBridgeResponse(src.content);
      t = src._bridgeParse.text;
    }
    return (t.slice(0, 30) + (t.length > 30 ? "…" : ""));
  }
  return messageId;
}

function toggleMention(id) {
  if (state.mentions.has(id)) state.mentions.delete(id);
  else state.mentions.add(id);
  renderMentionChips();
  renderMembers(); // 刷新被 @ 成员的高亮
}

/* ============ 输入框输入 @ 弹出知聚成员选择 ============ */
let composing = false;      // 中文输入法组合中（避免输入法输入 @ 时误弹）
let mentionPickerIndex = 0; // 键盘上下选择时的高亮索引
let mentionSession = null;  // 浮层打开时 @ 的位置与查询串快照 { index, query }，选择/取消时据此移除 @ 及后续输入

function hideMentionPicker() {
  const el = $("mentionPicker");
  if (!el || el.hidden) return;
  el.hidden = true;
  el.innerHTML = "";
  mentionPickerIndex = 0;
  mentionSession = null;
}

/** 光标前最后一个 @ 的查询串；无有效 @ 返回 null。 */
function mentionQueryAtCaret() {
  const input = $("input");
  const pos = input.selectionStart ?? input.value.length;
  const before = input.value.slice(0, pos);
  const m = before.match(/@([^\s@]*)$/);
  return m ? { query: m[1], index: m.index } : null;
}

/** 移除输入框中 @ 及其后的查询串（以浮层打开时的快照定位，选中或取消后调用）。 */
function removeMentionQuery() {
  const input = $("input");
  const s = mentionSession;
  if (!s) return;
  if (input.value.slice(s.index, s.index + 1) !== "@") return; // 位置已变（如文本被外部改动），跳过避免误删
  const before = input.value.slice(0, s.index);
  const after = input.value.slice(s.index + 1 + s.query.length);
  input.value = before + after;
  input.setSelectionRange(before.length, before.length);
}

function showMentionPicker(query) {
  const el = $("mentionPicker");
  const q = query.toLowerCase();
  const list = visibleGroupMembers().filter((m) => {
    if (state.mentions.has(m.memberId)) return false; // 已 @ 的成员不再重复选择
    return (m.nickname || "").toLowerCase().includes(q) || m.memberId.toLowerCase().includes(q);
  });
  if (mentionPickerIndex > list.length - 1) mentionPickerIndex = Math.max(0, list.length - 1);
  if (list.length === 0) {
    el.innerHTML = `<div class="mention-pick-empty">${escapeHtml(t("member.noMatch"))}</div>`;
  } else {
    el.innerHTML = list.map((m, i) => {
      const isTwin = (m.memberId || "").startsWith("twin_");
      const statusIcon = memberStatusIconHtml(m);
      const avatarHtml = m.avatar
        ? `<span class="member-avatar"><img src="${escapeHtml(authedAssetUrl(m.avatar))}" alt="" onerror="this.remove()" />${statusIcon}</span>`
        : statusIcon;
      return `<div class="mention-pick-item ${i === mentionPickerIndex ? "active" : ""}" data-id="${escapeHtml(m.memberId)}">` +
        avatarHtml +
        `<span class="pick-name">${escapeHtml(m.nickname || m.memberId)}</span>` +
        (!isTwin && m.memberType === "agent" ? '<span class="tag-agent">AI</span>' : "") +
        `</div>`;
    }).join("");
    el.querySelectorAll(".mention-pick-item").forEach((item, i) => {
      item.onclick = () => pickMentionFromInput(list[i]);
    });
  }
  el.hidden = false;
}

/** 选择成员：移除输入框中的 @ 查询串（成员引用以 chips 表达），并加入 mentions（必加，不用 toggle 以免误取消）。 */
function pickMentionFromInput(member) {
  removeMentionQuery();
  state.mentions.add(member.memberId);
  renderMentionChips();
  renderMembers();
  hideMentionPicker();
  $("input").focus();
}

/** 取消选择：与选择一致，把 @ 及其后的输入移除。 */
function cancelMentionPicker() {
  removeMentionQuery();
  hideMentionPicker();
}

function updateMentionPicker() {
  const input = $("input");
  if (input.value === "") { hideMentionPicker(); return; }
  const hit = mentionQueryAtCaret();
  if (!hit) { hideMentionPicker(); return; }
  // 查询串未变（仅移动光标）：保持现有浮层，只同步快照位置
  if (mentionSession && mentionSession.query === hit.query && !$("mentionPicker").hidden) {
    mentionSession.index = hit.index;
    return;
  }
  mentionSession = { index: hit.index, query: hit.query };
  showMentionPicker(hit.query);
}

function moveMentionPicker(delta) {
  const el = $("mentionPicker");
  if (el.hidden) return;
  const items = el.querySelectorAll(".mention-pick-item");
  if (items.length === 0) return;
  mentionPickerIndex = (mentionPickerIndex + delta + items.length) % items.length;
  items.forEach((it, i) => it.classList.toggle("active", i === mentionPickerIndex));
}

/* ============ 添加成员 ============ */

let selectedAddMembers = new Set();

function renderAddPick() {
  if (addPickOptions.length === 0) {
    $("addMemberList").innerHTML = `<div class="pick-empty">${escapeHtml(t("member.noAddable"))}</div>`;
    return;
  }
  const q = $("addMemberSearch").value.trim().toLowerCase();
  renderMemberPick($("addMemberList"), filterPickOptions(addPickOptions, q), selectedAddMembers, () => {
    $("addMemberConfirm").disabled = selectedAddMembers.size === 0;
  });
}

function openAddMemberModal() {
  const gid = state.activeGroupId;
  if (!gid) return;
  const r = room(gid);
  const existing = new Set(r.members.map((m) => m.memberId));
  addPickOptions = memberDirectory().filter((m) => !existing.has(m.memberId));
  selectedAddMembers.clear();
  $("addMemberSearch").value = "";
  renderAddPick();
  const g = state.groups.find((x) => x.groupId === gid);
  $("addMemberGroupName").textContent = g?.groupName || "";
  $("addMemberConfirm").disabled = true;
  $("addMemberModal").classList.remove("hidden");
}

async function addMembers() {
  const gid = state.activeGroupId;
  if (!gid) return;
  const picked = memberDirectory().filter((m) => selectedAddMembers.has(m.memberId));
  if (picked.length === 0) return;
  const body = {
    groupId: gid,
    operatorId: state.memberId,
    memberIds: picked.map((m) => m.memberId),
    memberDetails: picked.map((m) => ({
      memberId: m.memberId, memberType: m.memberType, nickname: m.nickname,
      avatar: m.memberType === "agent" ? (m.avatar || null) : null,
    })),
  };
  try {
    const res = await fetch("/ag-ui/group/member/add", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(body),
    });
    if (!res.ok) {
      const err = await res.json().catch(() => null);
      toast(t("member.addFail", { err: errMsg(err, `添加失败（${res.status}）`) }));
      return;
    }
    // 为添加的数字员工注册该知聚触发规则，使其加入知聚后即可被触发
    for (const a of picked.filter((m) => m.memberType === "agent")) {
      try {
        await fetch(`/ag-ui/agents/register?memberId=${encodeURIComponent(state.memberId)}`, {
          method: "POST",
          headers: state.token
            ? { "Content-Type": "application/json", Authorization: `Bearer ${state.token}` }
            : { "Content-Type": "application/json" },
          body: JSON.stringify({
            agentId: a.memberId, nickname: a.nickname, groupId: gid,
            triggerMode: a.triggerMode, keywords: a.keywords,
          }),
        });
      } catch { /* 单个数字员工注册失败不阻塞添加 */ }
    }
    $("addMemberModal").classList.add("hidden");
    toast(t("member.added", { names: picked.map((m) => m.nickname).join("、") }));
  } catch (ex) {
    toast(t("member.addFail", { err: ex.message }));
  }
}

/* ============ 聊天区 / 输入区可拖动分割线 ============ */

const CHAT_RESIZER_KEY = "agui.chatResizerH"; // composer 高度 px，按用户持久化

/** 恢复该用户上次拖拽的 composer 高度（登录后调用）。 */
function applyChatResizer() {
  const composer = document.querySelector(".composer");
  if (!composer) return;
  const saved = Number(localStorage.getItem(CHAT_RESIZER_KEY + "." + (state.memberId || "")));
  if (saved >= 96 && saved <= 700) composer.style.height = saved + "px";
}

/** 绑定分割线拖拽：上下拖动改变 composer 高度（聊天记录区随 flex 自动伸缩），高度持久化到 localStorage。 */
function initChatResizer() {
  const resizer = $("chatResizer");
  const composer = document.querySelector(".composer");
  if (!resizer || !composer) return;
  let dragging = false;
  const onMove = (e) => {
    if (!dragging) return;
    const chat = resizer.closest(".chat");
    if (!chat) return;
    const rect = chat.getBoundingClientRect();
    const maxH = rect.height * 0.55;
    let h = rect.bottom - e.clientY; // 鼠标位置 → composer 高度（底边固定）
    h = Math.max(96, Math.min(maxH, h));
    composer.style.height = h + "px";
  };
  const onUp = () => {
    if (!dragging) return;
    dragging = false;
    resizer.classList.remove("dragging");
    document.body.classList.remove("resizing");
    window.removeEventListener("pointermove", onMove);
    window.removeEventListener("pointerup", onUp);
    try { localStorage.setItem(CHAT_RESIZER_KEY + "." + (state.memberId || ""), composer.style.height); } catch { /* 存储不可用忽略 */ }
    // 高度变化后若用户停靠在底部，保持跟随最新消息
    if (vscroll.stickBottom) scheduleFollow();
  };
  resizer.addEventListener("pointerdown", (e) => {
    e.preventDefault();
    dragging = true;
    resizer.classList.add("dragging");
    document.body.classList.add("resizing");
    window.addEventListener("pointermove", onMove);
    window.addEventListener("pointerup", onUp);
  });
}

/* ============ 发送 ============ */

let sending = false; // Enter 发送锁：防快速连按重复发送（发送完成或失败后延时释放）

async function sendMessage() {
  const input = $("input");
  const content = input.value.trim();
  const gid = state.activeGroupId;
  if (!gid) return;
  if (!content && pendingAttachments.length === 0) return;
  // 断线 / 未连接：明确提示而不是静默丢弃（内容保留在输入框，重连后可直接重发）
  if (!state.ws || state.ws.readyState !== WebSocket.OPEN) {
    toast(t("msg.sendDisconnected"));
    return;
  }
  if (sending) return; // 防连按：上一次发送尚未结束时忽略本次

  // 兜底：若当前知聚未订阅（如断线重连后恢复失败），先补发订阅，保证消息回显与后续事件可达
  if (!state.subscribedGroups.has(gid)) {
    state.subscribedGroups.add(gid);
    send({ type: "GROUP_SUBSCRIBE", groupIds: [gid], timestamp: Date.now() });
  }
  sending = true;
  try {
    // 有待发送附件：先上传，成功后随消息携带附件元信息
    let attachments = [];
    if (pendingAttachments.length > 0) {
      if (pendingAttachments.some((a) => a.uploading)) { toast(t("msg.attachUploading")); return; }
      setComposerBusy(true, t("msg.uploading"));
      try {
        attachments = await uploadAttachments();
      } catch (ex) {
        toast(t("msg.attachUploadFail", { err: ex.message || t("msg.retry") }));
        setComposerBusy(false);
        return;
      }
      setComposerBusy(false);
      if (attachments.length === 0) { toast(t("msg.noAttachToSend")); return; }
    }

    const payload = {
      type: "GROUP_MESSAGE_SEND",
      groupId: gid,
      topicId: state.activeTopicId || "main",
      userId: state.memberId,
      content,
      mentions: [...state.mentions],
      mentionAll: state.mentionAll,
      visibility: state.visibility,
    };
    if (state.replyTo?.id) payload.replyToMessageId = state.replyTo.id;
    if (state.visibility === "private") payload.visibleMemberIds = [...state.mentions];
    if (attachments.length > 0) payload.attachments = attachments;

    send(payload);
    input.value = "";
    clearReplyTo(); // 引用一次性消费：发送后清除引用条
    pendingAttachments = [];
    renderAttachList();
    // @ 选择保留（按知聚记忆）：连续对话无需每次重新 @；点输入框上方的 chips ✕ 可随时取消
    renderMentionChips();
    renderMembers();
  } finally {
    // 发送结束（含失败路径）后短暂释放，避免锁死后续输入
    setTimeout(() => { sending = false; }, 300);
  }
}

/* ============ 头像上传（用户资料 / 数字员工表单共用） ============ */

/** 资料表单待保存的头像 URL；null = 未改动，"" = 移除。 */
let profileAvatar = null;
let agentAvatar = null;
let pfAvatarPicker = null;
let afAvatarPicker = null;

/** 上传单个图片文件，返回可作头像的 URL（复用 /ag-ui/upload）。 */
async function uploadAvatarFile(file) {
  const form = new FormData();
  form.append("file", file, file.name);
  const url = state.token
    ? "/ag-ui/upload"
    : `/ag-ui/upload?memberId=${encodeURIComponent(state.memberId)}`;
  const headers = state.token ? { Authorization: `Bearer ${state.token}` } : {};
  const res = await fetch(url, { method: "POST", headers, body: form });
  const data = await res.json().catch(() => null);
  if (!res.ok) throw new Error(errMsg(data, t("upload.fail", { err: "HTTP " + res.status })));
  const att = (data?.attachments || [])[0];
  if (!att) throw new Error(t("upload.noResult"));
  return att.url;
}

/**
 * 绑定头像选择控件：预览（默认 emoji）+ 上传按钮 + 移除按钮 + 隐藏文件输入。
 * onChanged(url) 在选定 / 移除时回调（url 为 "" 表示移除）。
 * 返回 { render(url) } 供打开表单时回显当前头像。
 */
function bindAvatarPicker(previewId, fileId, uploadId, clearId, defaultEmoji, onChanged) {
  const preview = $(previewId);
  const fileInput = $(fileId);
  const render = (url) => {
    preview.textContent = "";
    preview.classList.toggle("has-img", !!url);
    if (url) {
      const img = document.createElement("img");
      let retried = false;
      img.src = authedAssetUrl(url);
      img.alt = "";
      img.onerror = () => {
        // 首次失败延时重试一次（附件落盘时序 / 网络抖动），仍失败才回退默认头像
        if (!retried) {
          retried = true;
          setTimeout(() => { img.src = authedAssetUrl(url); }, 800);
        } else {
          render("");
        }
      };
      preview.appendChild(img);
    } else {
      preview.textContent = defaultEmoji;
    }
  };
  $(uploadId).onclick = () => fileInput.click();
  fileInput.onchange = async (e) => {
    const file = e.target.files?.[0];
    e.target.value = "";
    if (!file) return;
    try {
      const url = await uploadAvatarFile(file);
      // 预览用本地对象 URL：服务端附件鉴权在资料保存（Avatar 关联）前不认新附件，直接加载会 403
      render(URL.createObjectURL(file));
      onChanged(url);
      toast(t("profile.avatarUploaded"));
    } catch (ex) { toast(t("profile.avatarUploadFail", { err: ex.message })); }
  };
  $(clearId).onclick = () => { render(""); onChanged(""); };
  return { render };
}

/* ============ 附件 ============ */

/** 批量上传待发送附件，返回附件元信息列表（失败抛异常由调用方提示）。 */
async function uploadAttachments() {
  const form = new FormData();
  for (const a of pendingAttachments) {
    a.uploading = true;
    form.append("file", a.file, a.file.name);
  }
  renderAttachList();
  const url = state.token
    ? "/ag-ui/upload"
    : `/ag-ui/upload?memberId=${encodeURIComponent(state.memberId)}`;
  const headers = state.token ? { Authorization: `Bearer ${state.token}` } : {};
  try {
    const res = await fetch(url, { method: "POST", headers, body: form });
    const data = await res.json().catch(() => null);
    if (!res.ok) throw new Error(errMsg(data, t("upload.fail", { err: "HTTP " + res.status })));
    return data?.attachments || [];
  } finally {
    for (const a of pendingAttachments) a.uploading = false;
    renderAttachList();
  }
}

function renderAttachList() {
  const el = $("attachList");
  el.innerHTML = "";
  if (pendingAttachments.length === 0) { el.classList.remove("show"); return; }
  el.classList.add("show");
  pendingAttachments.forEach((a, i) => {
    const div = document.createElement("div");
    div.className = "attach-chip";
    // 富媒体（5.2）：图片显示缩略图，音频显示麦克风图标，其余显示纸夹
    const isImg = a.file.type.startsWith("image/") || /image\//i.test(a.file.type || "");
    if (isImg) {
      const img = document.createElement("img");
      img.className = "att-thumb";
      img.src = URL.createObjectURL(a.file);
      img.title = a.file.name;
      div.appendChild(img);
    }
    const name = document.createElement("span");
    name.className = "att-name";
    const icon = isImg ? "" : (a.file.type.startsWith("audio/") ? "🎤 " : "📎 ");
    name.textContent = icon + a.file.name;
    const meta = document.createElement("span");
    meta.className = "att-meta";
    meta.textContent = fmtBytes(a.file.size) + (a.file.type.startsWith("audio/") ? t("attach.audio") : "");
    div.appendChild(name);
    div.appendChild(meta);
    if (a.uploading) {
      const spin = document.createElement("span");
      spin.className = "att-uploading";
      spin.textContent = t("attach.uploading");
      div.appendChild(spin);
    } else {
      const rm = document.createElement("span");
      rm.className = "att-remove";
      rm.textContent = "✕";
      rm.title = t("attach.remove");
      rm.onclick = () => { pendingAttachments.splice(i, 1); renderAttachList(); };
      div.appendChild(rm);
    }
    el.appendChild(div);
  });
}

/* ============ 语音消息（富媒体 5.2）：MediaRecorder 录音 → 音频附件 ============ */
let voiceRecorder = null;
let voiceStream = null;
let voiceChunks = [];
let voiceTimerId = null;
let voiceStartAt = 0;

function fmtDuration(sec) {
  const s = Math.floor(sec);
  return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, "0")}`;
}

/** 开始录音：请求麦克风权限 + MediaRecorder（webm/opus）。失败（无权限 / 无硬件）时 toast 提示。 */
async function startVoiceRecording() {
  if (voiceRecorder) return;
  try {
    voiceStream = await navigator.mediaDevices.getUserMedia({ audio: true });
  } catch (err) {
    toast(err?.name === "NotAllowedError" ? t("voice.micDenied") : t("voice.micNotFound"));
    return;
  }
  voiceChunks = [];
  const mime = (() => {
    const c = ["audio/webm;codecs=opus", "audio/webm", "audio/ogg;codecs=opus"];
    for (const m of c) { if (typeof MediaRecorder !== "undefined" && MediaRecorder.isTypeSupported(m)) return m; }
    return "";
  })();
  try {
    voiceRecorder = new MediaRecorder(voiceStream, mime ? { mimeType: mime } : undefined);
  } catch (err) {
    toast(t("voice.unsupported"));
    voiceStream.getTracks().forEach((t) => t.stop());
    voiceStream = null;
    return;
  }
  voiceRecorder.ondataavailable = (e) => { if (e.data && e.data.size > 0) voiceChunks.push(e.data); };
  voiceRecorder.onstop = () => {
    const blob = new Blob(voiceChunks, { type: voiceRecorder.mimeType || "audio/webm" });
    const ext = (voiceRecorder.mimeType || "audio/webm").includes("ogg") ? ".ogg" : ".webm";
    const name = `语音-${new Date().toISOString().replace(/[:.]/g, "-").slice(0, 19)}${ext}`;
    // 空录音（< ~0.5s 仅几字节）不入列；正常则加入待发送附件
    if (blob.size > 1024) pendingAttachments.push({ file: new File([blob], name, { type: voiceRecorder.mimeType || "audio/webm" }), uploading: false });
    renderAttachList();
  };
  voiceStartAt = Date.now();
  voiceRecorder.start(250);
  $("voiceBtn").classList.add("recording");
  $("voiceBtn").title = t("voice.stopTip");
  $("voiceStatus").classList.remove("hidden");
  voiceTimerId = setInterval(() => {
    $("voiceTimer").textContent = fmtDuration((Date.now() - voiceStartAt) / 1000);
  }, 500);
}

/** 停止录音：仍在录音则停止；否则进入录音。Toggle 兼顾「点击开始 / 再点停止」。 */
function stopVoiceRecording() {
  if (!voiceRecorder) return;
  const r = voiceRecorder;
  try { r.state !== "inactive" && r.stop(); } catch { /* 已停 */ }
  r.stream?.getTracks()?.forEach((t) => t.stop());
  voiceStream?.getTracks()?.forEach((t) => t.stop());
  clearInterval(voiceTimerId);
  voiceRecorder = null;
  voiceStream = null;
  voiceChunks = [];
  $("voiceBtn").classList.remove("recording");
  $("voiceBtn").title = t("chat.voice");
  $("voiceStatus").classList.add("hidden");
}

/** 取消录音：丢弃音频片段。 */
function cancelVoiceRecording() {
  if (voiceRecorder) {
    const r = voiceRecorder;
    // 直接 stop 会触发 onstop 入列；用空标记配合阻止入列
    voiceChunks = [];
    try { r.state !== "inactive" && r.stop(); } catch { /* 已停 */ }
    r.stream?.getTracks()?.forEach((t) => t.stop());
    voiceStream?.getTracks()?.forEach((t) => t.stop());
    clearInterval(voiceTimerId);
    voiceRecorder = null;
    voiceStream = null;
  }
  $("voiceBtn").classList.remove("recording");
  $("voiceBtn").title = t("chat.voice");
  $("voiceStatus").classList.add("hidden");
}

/* ============ 画布标注（富媒体 5.2）：canvas 绘制 → PNG 图片附件 ============ */
let cvTool = "brush";
let cvDrawing = false;
let cvLast = null;
let cvFocusReturn = null; // 打开画布前的焦点元素（ARIA：关闭后回移）

function openCanvasModal() {
  if (!state.activeGroupId) { toast(t("chat.selectGroup")); return; }
  const c = $("cvCanvas");
  const ctx = c.getContext("2d");
  ctx.fillStyle = "#fff";
  ctx.fillRect(0, 0, c.width, c.height);
  cvTool = "brush";
  $("cvToolBrush").classList.add("on");
  $("cvToolEraser").classList.remove("on");
  $("canvasModal").classList.remove("hidden");
  // ARIA（5.3）：打开聚焦画布，焦点回移到画布按钮
  cvFocusReturn = document.activeElement;
  c.focus();
}

function closeCanvasModal() {
  $("canvasModal").classList.add("hidden");
  cvDrawing = false;
  cvLast = null;
  if (cvFocusReturn && cvFocusReturn.focus) { try { cvFocusReturn.focus(); } catch { /* 忽略 */ } }
  cvFocusReturn = null;
}

function cvDown(e) {
  cvDrawing = true;
  const p = cvPoint(e);
  cvLast = p;
  cvStroke(p, p);
}
function cvMove(e) {
  if (!cvDrawing || !cvLast) return;
  const p = cvPoint(e);
  cvStroke(cvLast, p);
  cvLast = p;
}
function cvUp() {
  cvDrawing = false;
  cvLast = null;
}
function cvPoint(e) {
  const r = $("cvCanvas").getBoundingClientRect();
  const scaleX = $("cvCanvas").width / r.width;
  const scaleY = $("cvCanvas").height / r.height;
  return { x: (e.clientX - r.left) * scaleX, y: (e.clientY - r.top) * scaleY };
}
function cvStroke(a, b) {
  const c = $("cvCanvas");
  const ctx = c.getContext("2d");
  ctx.strokeStyle = cvTool === "eraser" ? "#ffffff" : $("cvColor").value;
  ctx.lineWidth = cvTool === "eraser" ? 22 : 4;
  ctx.lineCap = "round";
  ctx.beginPath();
  ctx.moveTo(a.x, a.y);
  ctx.lineTo(b.x, b.y);
  ctx.stroke();
}
function insertCanvas() {
  const c = $("cvCanvas");
  c.toBlob((blob) => {
    if (!blob) { toast(t("canvas.exportFail")); return; }
    pendingAttachments.push({ file: new File([blob], `画布-${Date.now()}.png`, { type: "image/png" }), uploading: false });
    renderAttachList();
    closeCanvasModal();
  }, "image/png");
}

const SEND_ICON = '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><line x1="22" y1="2" x2="11" y2="13"/><polygon points="22 2 15 22 11 13 2 9 22 2"/></svg>';
const BUSY_ICON = '<svg class="spinner" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><path d="M12 2a10 10 0 0 1 10 10"/></svg>';

function setComposerBusy(busy, text) {
  const btn = $("sendBtn");
  btn.disabled = busy;
  // 图标按钮：忙时旋转 spinner 图标并更新 title，空闲恢复发送图标（不写文本，保持图标风格统一）
  btn.innerHTML = busy ? BUSY_ICON : SEND_ICON;
  btn.title = busy ? (text || t("msg.processing")) : t("msg.sendTooltip");
}

function fmtBytes(n) {
  if (n >= 1024 * 1024) return (n / 1024 / 1024).toFixed(1) + " MB";
  if (n >= 1024) return (n / 1024).toFixed(1) + " KB";
  return n + " B";
}

/* ============ Markdown 渲染 ============ */

/** 消息内容渲染：撤回 → 「已撤回」占位；流式 → 纯文本（escapeHtml）；否则 → Markdown（marked 解析 + DOMPurify 消毒）。
 * 兼容两种调用：renderMessageContent(m)（旧）或 renderMessageContent(text, m)。 */
function renderMessageContent(text, m) {
  if (typeof text === "object" && text !== null) { m = text; text = m.content || ""; }
  const s = String(text ?? "");
  if (m?.recalled) return escapeHtml(t("msg.recalledHint")); // 撤回后不显示原文，仅占位提示
  if (m?.streaming) return escapeHtml(s);
  return renderMarkdown(s);
}

/**
 * 解析外部 AG-UI 服务返回的结构化 JSON 响应（文本尾部为 {resultText, attachUrls, ...}）：
 * 把 JSON 从显示文本中剥离，attachUrls 转为附件卡片数据；无法识别时原样返回。
 * 性能：仅当文本含“attachUrls”键时才尝试 JSON.parse，其余快速返回。
 */
function parseBridgeResponse(content) {
  const text = String(content || "");
  const trimmed = text.trimEnd();
  const start = trimmed.lastIndexOf("{");
  if (start < 0 || !trimmed.includes("attachUrls", start)) return { text, attachments: [] };
  let obj = null;
  try { obj = JSON.parse(trimmed.slice(start)); } catch { /* 非 JSON → 原样显示 */ }
  if (!obj || typeof obj !== "object" || Array.isArray(obj)) return { text, attachments: [] };
  const urls = (Array.isArray(obj.attachUrls) ? obj.attachUrls : []).filter((u) => typeof u === "string" && u);
  if (urls.length === 0) return { text, attachments: [] };
  const resultText = typeof obj.resultText === "string" ? obj.resultText : "";
  const names = Array.isArray(obj.fileNames) ? obj.fileNames : (Array.isArray(obj.names) ? obj.names : []);
  // 附件 URL 过 scheme 白名单：外部桥接服务可能返回 javascript: 等危险协议，非法丢弃（图片按 kind 放行 data:image/）
  const attachments = urls.map((url, i) => {
    const name = bridgeFileName(url, i, names[i], resultText);
    return { url, name, kind: bridgeFileKind(url, name), size: 0 };
  }).filter((att) => safeUrl(att.url, att.kind === "image"));
  // 显示文本：JSON 之前的部分；为空时回退到 resultText（外部服务也常把摘要放在 JSON 里）
  const display = text.slice(0, start).trimEnd() || resultText;
  return { text: display, attachments };
}

/** 附件文件名：优先显式名称 → URL 最后路径段（含扩展名）→ resultText 中的“文件名.扩展名” → 兜底“附件 N”。 */
function bridgeFileName(url, index, explicitName, resultText) {
  if (typeof explicitName === "string" && explicitName.trim()) return explicitName.trim();
  try {
    const seg = decodeURIComponent(url.split("?")[0].split("/").filter(Boolean).pop() || "");
    if (/\.[A-Za-z0-9]{1,10}$/.test(seg)) return seg;
  } catch { /* 解码失败忽略 */ }
  const m = /([\w\u4e00-\u9fa5（）()\-_.]+?\.(?:pptx?|docx?|xlsx?|pdf|txt|md|json|csv|zip|rar|7z|png|jpe?g|gif|webp|bmp|xml|html?|log))\b/i.exec(resultText || "");
  if (m) return m[1];
  return `附件 ${index + 1}`;
}

/** 由文件名/URL 推断附件类别（与上传附件的 kind 语义一致）。 */
function bridgeFileKind(url, name) {
  const ext = ((name || url).split("?")[0].split(".").pop() || "").toLowerCase();
  if (/^(png|jpe?g|gif|webp|bmp|svg|ico)$/.test(ext)) return "image";
  if (/^(txt|md|json|csv|xml|html?|log|yaml|yml)$/.test(ext)) return "text";
  if (/^(pdf|docx?|xlsx?|pptx?)$/.test(ext)) return "document";
  return "binary";
}

/**
 * Markdown → 安全 HTML：marked 解析（GFM 表格 / breaks 换行），
 * DOMPurify 消毒防 XSS；外链统一加 target=_blank rel=noopener，
 * 且 http/https 链接重写为本站代理地址（/ag-ui/proxy），由 Hub 代访后返回内容——
 * 外部 AG-UI 返回的内网 / 混合内容链接浏览器端无法直连，需经 Hub 侧访问。
 */
function renderMarkdown(text) {
  if (!text) return "";
  try {
    const html = marked.parse(text, { gfm: true, breaks: true });
    const clean = DOMPurify.sanitize(html, { FORBID_ATTR: ["id", "name"], FORBID_TAGS: ["style"] }); // 禁 id/name 防 DOM Clobbering；禁 style 标签防 CSS 注入
    const div = document.createElement("div");
    div.innerHTML = clean;
    div.querySelectorAll("a").forEach((a) => {
      const href = a.getAttribute("href");
      if (href) {
        const proxied = proxyLinkUrl(href);
        if (proxied !== href) { a.setAttribute("href", proxied); a.title = href; } // 原链接存入 title，便于查看真实地址
      }
      a.setAttribute("target", "_blank");
      a.setAttribute("rel", "noopener noreferrer");
    });
    return div.innerHTML;
  } catch (e) {
    // 解析异常回退纯文本，保证消息始终可见
    return escapeHtml(text);
  }
}

/** 把绝对 http/https 链接重写为 Hub 代理地址；站内相对路径 / 其它 scheme 保持原样。 */
function proxyLinkUrl(href) {
  if (!/^https?:\/\//i.test(href)) return href;
  const params = new URLSearchParams({ url: href });
  if (state && state.token) params.set("token", state.token);
  return `/ag-ui/proxy?${params.toString()}`;
}

let mermaidSeq = 0;

/**
 * Mermaid SVG 消毒：mermaid 节点/边标签默认用 <foreignObject> 内嵌 HTML 承载，
 * DOMPurify 按命名空间规则会强制清空 foreignObject 内部 → 图表文字全丢。
 * 因此这里改为：HTML parser 解析（保留 foreignObject 内文字）→ 黑名单元素/危险属性
 * 白名单式清理（保留 <style> 主题块，仅滤除 CSS 危险指令）→ foreignObject 内部 HTML
 * 再交给 DOMPurify（HTML 上下文）二次消毒。
 * 安全性：mermaid securityLevel=strict 已转义 label 文本，配合本层清理 + CSP 双保险。
 */
function sanitizeMermaidSvg(svg) {
  const div = document.createElement("div");
  div.innerHTML = svg;
  div.querySelectorAll("script, iframe, object, embed, link, meta, base, form, input, button, textarea, select, option, template")
    .forEach((el) => el.remove());
  // 保留 <style>：mermaid 主题色（节点背景/描边/文字）定义于此，删除会导致整图变黑；
  // 仅清除 CSS 中的危险指令（外链 url/@import/expression 等）
  div.querySelectorAll("style").forEach((el) => {
    el.textContent = (el.textContent || "")
      .replace(/url\s*\([^)]*\)/gi, "url()")
      .replace(/@import[^;]*;?/gi, "")
      .replace(/expression\s*\(/gi, "")
      .replace(/javascript\s*:/gi, "")
      .replace(/behavior\s*:/gi, "")
      .replace(/-moz-binding\s*:/gi, "");
  });
  div.querySelectorAll("*").forEach((el) => {
    for (const attr of [...el.attributes]) {
      const n = attr.name.toLowerCase();
      if (n.startsWith("on")) { el.removeAttribute(attr.name); continue; }
      if (n === "style") {
        // style 属性与 <style> 文本同样过滤危险 CSS 指令；滤后无有效内容则整个移除该属性
        const v = String(attr.value)
          .replace(/url\s*\([^)]*\)/gi, "url()")
          .replace(/@import[^;]*;?/gi, "")
          .replace(/expression\s*\(/gi, "")
          .replace(/javascript\s*:/gi, "")
          .replace(/behavior\s*:/gi, "")
          .replace(/-moz-binding\s*:/gi, "");
        if (!v.trim()) el.removeAttribute(attr.name);
        else el.setAttribute(attr.name, v);
        continue;
      }
      if (n === "href" || n === "xlink:href" || n === "src" || n === "xlink:src") {
        const v = String(attr.value).trim().toLowerCase();
        if (v.startsWith("javascript:") || (v.startsWith("data:") && !v.startsWith("data:image/"))) {
          el.removeAttribute(attr.name);
        }
      }
    }
  });
  div.querySelectorAll("foreignObject").forEach((fo) => {
    fo.innerHTML = DOMPurify.sanitize(fo.innerHTML, {
      ADD_TAGS: ["div", "span", "p", "br", "b", "i", "em", "strong"],
      ADD_ATTR: ["style", "class"],
      FORBID_ATTR: ["id", "name"], // 防 DOM Clobbering
    });
  });
  return div;
}

/**
 * Mermaid 图表渲染：把消息内容里的 <pre><code class="language-mermaid"> 解析为 SVG 图表。
 * 安全：securityLevel=strict（默认防 XSS）+ sanitizeMermaidSvg 白名单清理；未加载库 / 解析失败时保留原代码块。
 * 通过 data-mermaid-done 标记防重入；内容变更（data 更新）后消息整体重建，标记随节点重建自动重置。
 */
async function renderMermaidBlocks(scope) {
  if (typeof mermaid === "undefined" || !scope) return;
  const blocks = scope.querySelectorAll("pre > code.language-mermaid");
  if (!blocks.length) return;
  try {
    const isLight = document.documentElement.dataset.theme === "light";
    mermaid.initialize({
      startOnLoad: false,
      securityLevel: "strict",
      theme: isLight ? "default" : "dark",
      // 中文友好字体：优先系统中文字体，避免图表文字显示为方块 / 乱码
      themeVariables: { fontFamily: '"Microsoft YaHei", "PingFang SC", "Noto Sans CJK SC", "Source Han Sans SC", sans-serif' },
    });
  } catch { /* 初始化失败则跳过 */ }
  for (const code of blocks) {
    const pre = code.closest("pre");
    if (!pre || pre.dataset.mermaidDone) continue;
    pre.dataset.mermaidDone = "1";
    const source = code.textContent;
    try {
      const { svg } = await mermaid.render("mmd" + (++mermaidSeq), source);
      const div = sanitizeMermaidSvg(svg);
      div.classList.add("mermaid");
      // 图表外层包一层：下方工具条提供「查看源码 / 复制源码」；防重入标记随 pre 替换迁移到 wrap
      const wrap = document.createElement("div");
      wrap.className = "mermaid-wrap";
      wrap.dataset.mermaidDone = "1";
      const bar = document.createElement("div");
      bar.className = "mermaid-bar";
      const viewBtn = document.createElement("button");
      viewBtn.type = "button";
      viewBtn.className = "mermaid-btn";
      viewBtn.title = t("mmd.viewSource");
      viewBtn.innerHTML = icon("code");
      viewBtn.onclick = (e) => { e.stopPropagation(); openMermaidSource(source); };
      const copyBtn = document.createElement("button");
      copyBtn.type = "button";
      copyBtn.className = "mermaid-btn";
      copyBtn.title = t("mmd.copySource");
      copyBtn.innerHTML = icon("copy");
      copyBtn.onclick = async (e) => {
        e.stopPropagation();
        const ok = await copyText(source);
        toast(ok ? t("mmd.copied") : t("mmd.copyFail"));
      };
      bar.appendChild(viewBtn);
      bar.appendChild(copyBtn);
      wrap.appendChild(div);
      wrap.appendChild(bar);
      pre.replaceWith(wrap);
    } catch {
      delete pre.dataset.mermaidDone; // 解析失败：保留原代码块，允许后续重试
    }
  }
}

/** 打开 Mermaid 源码查看弹窗（源码以纯文本展示，可选中复制）。 */
function openMermaidSource(source) {
  $("mmdSourceCode").textContent = source;
  $("mmdSourceModal").classList.remove("hidden");
}

/* ============ 工具 ============ */

function fmtTime(ts) {
  const d = new Date(ts);
  return `${String(d.getHours()).padStart(2, "0")}:${String(d.getMinutes()).padStart(2, "0")}`;
}

/** 完整日期时间（YYYY-MM-DD HH:mm），用于管理界面。 */
function fmtDateTime(ts) {
  if (!ts) return "-";
  const d = new Date(ts);
  if (Number.isNaN(d.getTime())) return "-";
  const p = (n) => String(n).padStart(2, "0");
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())} ${p(d.getHours())}:${p(d.getMinutes())}`;
}

function escapeHtml(s) {
  return String(s ?? "").replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
}

/** URL scheme 白名单：仅放行 http/https 与站内相对路径；data: 仅放行 base64 图片（附件 / 头像预览）。非法返回 null。 */
function safeUrl(url, forImage = false) {
  if (!url) return null;
  const u = String(url).trim();
  if ((u.startsWith("/") && !u.startsWith("//")) || u.startsWith("./") || u.startsWith("../")) return u; // 站内相对路径；// 开头（协议相对 URL）不放行，落入后续检查返回 null
  if (/^https?:\/\//i.test(u)) return u;
  if (/^data:image\//i.test(u)) return u; // base64 图片（img 预览 / 下载均安全，浏览器不执行）
  if (forImage && /^data:image\//i.test(u)) return u;
  return null; // 其余 data:（html/script 等）一律拒绝
}

/** 站内附件 / 头像 URL 自动追加会话令牌：<img>/<a> 无法带 Authorization 头，服务端下载接口支持 ?token= 查询参数鉴权。 */
function authedAssetUrl(url) {
  if (!url) return null;
  const token = state && state.token;
  if (!token) return url; // 演示模式（无令牌）：按原样，服务端回退模式放行
  if (/^https?:\/\//i.test(url)) return url; // 外部 URL（桥接附件）无需本站令牌
  if (/^data:/i.test(url)) return url; // data URL（base64 附件）：无需令牌，且追加参数会破坏格式
  if (/^blob:/i.test(url)) return url; // 本地 object URL（上传后即时预览）：追加参数会破坏 blob 匹配，预览直接失效
  // 仅站内单斜杠路径追加令牌：//evil.com 等协议相对 URL / 其它相对路径一律不追加，避免令牌外泄
  if (!(url.startsWith("/") && !url.startsWith("//"))) return url;
  const sep = url.includes("?") ? "&" : "?";
  return url + sep + "token=" + encodeURIComponent(token);
}

let toastTimer = null;
function toast(text) {
  const el = $("toast");
  el.textContent = text;
  el.classList.add("show");
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => el.classList.remove("show"), 3200);
}

/* ============ 初始化 ============ */

function init() {
  // 连接状态徽标初始为「未连接」（登录前 connect() 尚未建立 WebSocket，先按当前语言渲染）
  setStatus(false, "status.offline");

  // 加载品牌配置（应用名 / Logo / 主色 / 嵌入），登录页与顶栏据此渲染
  loadBranding();

  // ---- 界面风格切换（深色 / 浅色，选择持久化）----
  $("themeBtn").onclick = () => {
    const next = document.documentElement.dataset.theme === "light" ? "dark" : "light";
    localStorage.setItem(THEME_KEY, next);
    applyTheme(next);
    toast(next === "light" ? t("theme.light") : t("theme.dark"));
  };

  // ---- 认证 UI ----
  $("authTabLogin").onclick = () => setAuthMode("login");
  $("authTabRegister").onclick = () => setAuthMode("register");
  $("authForm").addEventListener("submit", submitAuth);

  // ---- 用户菜单（资料 / 密码 / 登出）----
  $("meChip").onclick = (e) => { e.stopPropagation(); $("meMenu").classList.toggle("hidden"); };
  document.addEventListener("click", () => $("meMenu").classList.add("hidden"));
  $("meMenuProfile").onclick = () => { $("meMenu").classList.add("hidden"); openProfileModal(); };
  $("meMenuPassword").onclick = () => {
    $("meMenu").classList.add("hidden");
    $("pwOld").value = ""; $("pwNew").value = "";
    $("pwModal").classList.remove("hidden");
  };
  $("meMenuBackup").onclick = () => {
    $("meMenu").classList.add("hidden");
    hideBackupImport();
    $("backupModal").classList.remove("hidden");
  };
  $("backupClose").onclick = () => $("backupModal").classList.add("hidden");
  // 模型配置（DeepSeek endpoint / apiKey；留空用默认端点与环境变量）
  $("meMenuModelConfig").onclick = () => { $("meMenu").classList.add("hidden"); openModelConfigModal(); };
  $("mcCancel").onclick = () => $("modelConfigModal").classList.add("hidden");
  // 记忆管理（分知聚分级 / 自动遗忘 / 可视化）
  $("meMenuMemory").onclick = () => { $("meMenu").classList.add("hidden"); openMemoryModal(); };
  $("memoryClose").onclick = () => $("memoryModal").classList.add("hidden");
  // 白标 / 品牌化（6.4，仅管理页）
  $("meMenuBranding").onclick = (e) => { e.stopPropagation(); $("meMenu").classList.add("hidden"); openBrandingModal(); };
  $("brSave").onclick = saveBranding;
  $("brCancel").onclick = () => $("brandModal").classList.add("hidden");
  $("brReset").onclick = async () => {
    $("brName").value = ""; $("brLogo").value = ""; $("brColor").value = "#4f8cff"; $("brTagline").value = ""; $("brForceDark").checked = false;
    $("brandModal").classList.add("hidden");
    applyBranding({ appName: t("brand.name"), primaryColor: "", forceDark: null }); // 本地立即恢复默认（不落库）
    toast(t("brand.resetDone"));
  };
  // 管理员控制台：用户管理 + 系统状态
  $("meMenuAdmin").onclick = () => { $("meMenu").classList.add("hidden"); openAdminModal(); };
  $("adminClose").onclick = () => $("adminModal").classList.add("hidden");
  $("adminUserRows").addEventListener("click", (e) => {
    const btn = e.target.closest("button[data-op]");
    if (btn) adminUserAction(btn.dataset.op, btn.dataset.uid, btn.dataset.name);
  });
  $("adminTabUsers").onclick = () => switchAdminTab("users");
  $("adminTabUsage").onclick = () => switchAdminTab("usage");
  $("adminTabConfig").onclick = () => switchAdminTab("config");
  $("cfgSave").onclick = saveConfigGovernance;
  $("cfgReload").onclick = loadConfigGovernance;
  $("meMenuStatus").onclick = () => { $("meMenu").classList.add("hidden"); openStatusModal(); };
  $("statusClose").onclick = () => $("statusModal").classList.add("hidden");
  $("memSearchBtn").onclick = () => loadMemoryList(0);
  $("memKeyword").addEventListener("keydown", (e) => { if (e.key === "Enter") loadMemoryList(0); });
  $("memGroupSelect").addEventListener("change", () => loadMemoryList(0));
  $("memForgetBtn").onclick = () => $("memForgetPanel").classList.toggle("hidden");
  $("memForgetCancel").onclick = () => $("memForgetPanel").classList.add("hidden");
  $("memForgetConfirm").onclick = async () => {
    const groupId = $("memGroupSelect").value;
    const hours = Number($("memForgetRange").value) * 24;
    const scope = groupId ? t("memory.scopeThis") : t("memory.scopeAll");
    const retain = hours ? t("memory.forgetRetain", { n: hours / 24 }) : t("memory.forgetImmediate");
    if (!confirm(t("memory.forgetConfirm", { scope, retain }))) return;
    const btn = $("memForgetConfirm");
    const orig = btn.textContent;
    btn.disabled = true; btn.textContent = "⏳…";
    try {
      const res = await fetch("/ag-ui/memory/forget", {
        method: "POST",
        headers: { "Content-Type": "application/json", Authorization: "Bearer " + (state.token || "") },
        body: JSON.stringify({ groupId: groupId || null, retentionHours: hours || null }),
      });
      const data = await res.json().catch(() => null);
      if (!res.ok) { toast(errMsg(data, t("memory.forgetFail", { err: res.status }))); return; }
      toast(t("memory.forgetSet", { count: data.affected || 0 }));
      $("memForgetPanel").classList.add("hidden");
      loadMemoryList(0);
      loadMemoryGroups();
    } catch (ex) { toast(t("memory.forgetFail", { err: ex.message })); }
    finally { btn.disabled = false; btn.textContent = orig; }
  };
  $("mcSave").onclick = async () => {
    const endpoint = $("mcEndpoint").value.trim();
    const apiKey = $("mcApiKey").value.trim();
    const thinkingMode = $("mcThinking").checked;
    const btn = $("mcSave");
    const orig = btn.textContent;
    btn.disabled = true; btn.textContent = t("common.saving");
    try {
      const res = await fetch("/ag-ui/settings/model", {
        method: "POST",
        headers: { "Content-Type": "application/json", Authorization: "Bearer " + (state.token || "") },
        body: JSON.stringify({ endpoint, apiKey, thinkingMode }),
      });
      const data = await res.json().catch(() => null);
      if (!res.ok) { toast(errMsg(data, t("common.saveFail", { err: res.status }))); return; }
      $("modelConfigModal").classList.add("hidden");
      toast(data.endpoint ? t("mc.savedWithEp", { endpoint: data.endpoint }) : t("mc.saved"));
    } catch (ex) { toast(t("common.saveFail", { err: ex.message })); }
    finally { btn.disabled = false; btn.textContent = orig; }
  };
  // 初始化（清空一切）：位于「数据备份」弹窗的危险操作区；删除全部数据 + 清浏览器缓存，回到登录页
  $("backupInitBtn").onclick = () => { $("backupInitText").value = ""; $("backupInitPanel").classList.remove("hidden"); };
  $("backupInitCancel").onclick = () => $("backupInitPanel").classList.add("hidden");
  $("backupInitConfirm").onclick = async () => {
    if ($("backupInitText").value.trim() !== "确认") { toast(t("backup.initTypeConfirm")); return; }
    const btn = $("backupInitConfirm");
    const orig = btn.textContent;
    btn.disabled = true; btn.textContent = "⏳ " + t("backup.initProgress");
    try {
      const res = await fetch("/ag-ui/reset", { method: "POST", headers: { Authorization: "Bearer " + (state.token || "") } });
      const data = await res.json().catch(() => null);
      if (!res.ok) { toast(errMsg(data, t("backup.initFail", { err: res.status }))); return; }
      // 清空浏览器缓存（登录态 / 主题 / 话题记忆 / 上次知聚等全部本地存储）并回登录页
      try { localStorage.clear(); sessionStorage.clear(); } catch { /* 存储不可用忽略 */ }
      location.reload();
    } catch (ex) { toast(t("backup.initFail", { err: ex.message })); }
    finally { btn.disabled = false; btn.textContent = orig; }
  };
  $("meMenuLogout").onclick = () => { $("meMenu").classList.add("hidden"); logout(); };
  $("pwCancel").onclick = () => $("pwModal").classList.add("hidden");
  $("pwConfirm").onclick = submitChangePassword;

  // ---- 应用内通知中心（5.4）----
  $("notifBtn").onclick = (e) => { e.stopPropagation(); toggleNotifPanel(); $("meMenu").classList.add("hidden"); };
  $("notifClear").onclick = (e) => { e.stopPropagation(); clearNotifications(); };
  document.addEventListener("click", (e) => {
    if (state.notifPanelOpen && !$("notifPanel").contains(e.target) && e.target !== $("notifBtn") && !$("notifBtn").contains(e.target)) hideNotifPanel();
  });
  document.addEventListener("keydown", (e) => {
    if (e.key === "Escape" && state.notifPanelOpen) hideNotifPanel();
    if (e.key === "Escape" && !$("canvasModal").classList.contains("hidden")) closeCanvasModal();
  });
  $("pwNew").addEventListener("keydown", (e) => { if (e.key === "Enter") { e.preventDefault(); submitChangePassword(); } });
  $("pfCancel").onclick = () => $("profileModal").classList.add("hidden");
  $("pfConfirm").onclick = submitProfile;
  $("pfTwinEnable").onclick = enableTwin;
  $("pfTwinDisable").onclick = disableTwin;
  $("pfTwinSync").onclick = syncTwinGroups;
  $("pfTwinTrigger").addEventListener("change", updateTwinTrigger);
  // 资料 / 数字员工头像选择控件
  pfAvatarPicker = bindAvatarPicker("pfAvatarPreview", "pfAvatarFile", "pfAvatarUploadBtn", "pfAvatarClearBtn", "🧑", (url) => { profileAvatar = url; });
  afAvatarPicker = bindAvatarPicker("afAvatarPreview", "afAvatarFile", "afAvatarUploadBtn", "afAvatarClearBtn", "🤖", (url) => { agentAvatar = url; });

  // ---- 数字员工管理 ----
  $("agentManageBtn").onclick = openAgentModal;
  $("agentClose").onclick = () => $("agentModal").classList.add("hidden");
  $("agentAddBtn").onclick = () => openAgentForm(null);
  initCollapsibleSections(false); // 绑定数字员工表单可折叠分组的开合
  // 数字员工组织架构（全局入口，一个图标即可）：确保目录已加载后打开
  $("agentOrgBtn").onclick = async () => { if (!state.token) { toast(t("agent.err.loginRequired")); return; } if (!agentList?.length) await loadAgents(); openOrgChart(); };
  // 技能库：工具条入口 + 弹窗内部动作
  $("agentSkillLibBtn").onclick = async () => { if (!state.token) { toast(t("agent.err.loginRequired")); return; } await loadSkills(); openSkillModal(); };
  $("skillCloseBtn").onclick = () => $("skillModal").classList.add("hidden");
  $("skillAddBtn").onclick = () => openSkillForm(null);
  $("sfBack").onclick = showSkillListView;
  $("sfKind").addEventListener("change", syncSkillKind);
  $("sfSave").onclick = saveSkill;
  $("sfTest").onclick = () => testSkill(editingSkillId);
  $("afSkillLibManageBtn").onclick = openSkillModal;
  // 数字员工导出 / 导入
  $("agentExportAllBtn").onclick = () => exportAgents(agentList);
  $("agentImportBtn").onclick = () => $("agentImportFile").click();
  $("agentImportFile").onchange = async (e) => {
    const file = e.target.files?.[0];
    e.target.value = "";
    if (file) await importAgentsFromFile(file);
  };

  // ---- 系统数据备份：导出 / 导入（账号 + 数字员工 + 聊天记录 + 附件）----
  let backupFile = null;
  let backupPreview = null;
  const hideBackupImport = () => { $("backupImportPanel").classList.add("hidden"); $("backupResult").classList.add("hidden"); backupFile = null; backupPreview = null; };

  $("backupExportBtn").onclick = async () => {
    const btn = $("backupExportBtn");
    const orig = btn.textContent;
    btn.disabled = true;
    btn.textContent = "⏳ " + t("backup.exporting");
    try {
      const res = await fetch("/ag-ui/export?token=" + encodeURIComponent(state.token || ""), { headers: { Authorization: "Bearer " + (state.token || "") } });
      if (!res.ok) { const err = await res.json().catch(() => null); toast(errMsg(err, t("backup.exportFail", { err: res.status }))); return; }
      const blob = await res.blob();
      const url = URL.createObjectURL(blob);
      const a = document.createElement("a");
      a.href = url;
      a.download = "agui-data-export.zip";
      document.body.appendChild(a); a.click(); a.remove();
      setTimeout(() => URL.revokeObjectURL(url), 2000);
      toast(t("backup.exported"));
    } catch (ex) { toast(t("backup.exportFail", { err: ex.message })); }
    finally { btn.disabled = false; btn.textContent = orig; }
  };

  $("backupImportBtn").onclick = () => $("backupImportFile").click();
  $("backupImportFile").onchange = async (e) => {
    const file = e.target.files?.[0];
    e.target.value = "";
    if (!file) return;
    $("backupResult").classList.add("hidden");
    toast(t("backup.parsing"));
    try {
      const form = new FormData();
      form.append("file", file);
      const res = await fetch("/ag-ui/import/preview", { method: "POST", headers: { Authorization: "Bearer " + (state.token || "") }, body: form });
      const data = await res.json().catch(() => null);
      if (!res.ok || !data) { toast(errMsg(data, t("backup.parseFail", { err: res.status }))); return; }
      backupFile = file;
      backupPreview = data;
      renderBackupPreview(data);
      $("backupImportPanel").classList.remove("hidden");
      toast(t("backup.selectGroupsToRestore"));
    } catch (ex) { toast(t("backup.parseFail", { err: ex.message })); }
  };

  $("backupImportCancel").onclick = hideBackupImport;
  $("backupImportConfirm").onclick = async () => {
    if (!backupFile || !backupPreview) return;
    const selected = [...$("backupImportGroups").querySelectorAll(".backup-group-item input:checked")].map((cb) => cb.value);
    if (selected.length === 0) { toast(t("backup.needSelectGroup")); return; }
    const btn = $("backupImportConfirm");
    const orig = btn.textContent;
    btn.disabled = true;
    btn.textContent = "⏳ " + t("backup.importing");
    try {
      const form = new FormData();
      form.append("file", backupFile);
      form.append("selectedGroupIds", JSON.stringify(selected));
      const res = await fetch("/ag-ui/import", { method: "POST", headers: { Authorization: "Bearer " + (state.token || "") }, body: form });
      const data = await res.json().catch(() => null);
      if (!res.ok || !data) { toast(errMsg(data, t("backup.importFail", { err: res.status }))); return; }
      renderBackupResult(data);
      $("backupImportPanel").classList.add("hidden");
      $("backupResult").classList.remove("hidden");
      backupFile = null; backupPreview = null;
      toast(t("backup.importDone"));
      loadGroups(); // 新知聚立即可见
    } catch (ex) { toast(t("backup.importFail", { err: ex.message })); }
    finally { btn.disabled = false; btn.textContent = orig; }
  };
  $("afCancel").onclick = () => showAgentListView();
  $("afSave").onclick = saveAgent;
  // 根据一句话简介生成角色设定（身份定位 / 职责范围 / 回复风格），填充 Instructions
  $("afGenInstructionsBtn").onclick = async () => {
    const desc = $("afDescription").value.trim();
    if (desc.length < 2) { toast(t("agent.form.descTooShort")); $("afDescription").focus(); return; }
    if (!state.token) { toast(t("common.loginFirst")); return; }
    const btn = $("afGenInstructionsBtn");
    const orig = btn.textContent;
    btn.disabled = true;
    btn.textContent = t("agent.form.generating");
    try {
      const res = await fetch("/ag-ui/agents/generate-instructions", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ description: desc }),
      });
      const data = await res.json().catch(() => null);
      if (!res.ok || !data?.instructions) { toast(errMsg(data, t("agent.form.genFail", { err: res.status }))); return; }
      $("afInstructions").value = data.instructions;
      toast(t("agent.form.generated"));
    } catch (ex) { toast(t("agent.form.genFail", { err: ex.message })); }
    finally { btn.disabled = false; btn.textContent = orig; }
  };
  // 知识库：管理弹窗 + 创建
  $("afKbManageBtn").onclick = openKbModal;
  $("kbCloseBtn").onclick = () => { $("kbModal").classList.add("hidden"); stopKbPolling(); renderKbPicks(); };
  $("kbCreateBtn").onclick = async () => {
    const name = $("kbName").value.trim();
    if (!name) { toast(t("kb.needName")); return; }
    const res = await fetch("/ag-ui/kb", {
      method: "POST",
      headers: { "Content-Type": "application/json", Authorization: `Bearer ${state.token}` },
      body: JSON.stringify({ name, description: $("kbDesc").value.trim() || null }),
    });
    const data = await res.json().catch(() => null);
    if (!res.ok) { toast(errMsg(data, t("kb.createFail", { err: res.status }))); return; }
    toast(t("kb.created", { name: data.name }));
    $("kbName").value = "";
    $("kbDesc").value = "";
    await loadKbs();
  };
  $("agentSearch").addEventListener("input", renderAgentList);
  $("afTriggerMode").addEventListener("change", syncTriggerForm);
  $("afInstructions").addEventListener("keydown", (e) => {
    if (e.key === "Enter" && (e.metaKey || e.ctrlKey)) { e.preventDefault(); saveAgent(); }
  });

  // ---- 使用帮助 ----
  $("helpBtn").onclick = () => $("helpModal").classList.remove("hidden");
  $("helpClose").onclick = () => $("helpModal").classList.add("hidden");
  $("helpModal").addEventListener("click", (e) => { if (e.target === $("helpModal")) $("helpModal").classList.add("hidden"); });
  document.addEventListener("keydown", (e) => { if (e.key === "Escape") $("helpModal").classList.add("hidden"); });

  $("sendBtn").onclick = sendMessage;
  // 输入框 @ 成员选择：输入时检测 @ 弹出选择器，↑/↓ 移动高亮，Enter 选中（浮层未开时 Enter 发送）
  const msgInput = $("input");
  // 浮层内按下鼠标不转移焦点（否则 textarea blur 会把浮层清掉，导致点击失效）
  $("mentionPicker").addEventListener("mousedown", (e) => e.preventDefault());
  msgInput.addEventListener("compositionstart", () => { composing = true; });
  msgInput.addEventListener("compositionend", () => { composing = false; updateMentionPicker(); });
  msgInput.addEventListener("input", (e) => { if (!composing && !e.isComposing) updateMentionPicker(); });
  // 点击输入框外部 = 取消：移除 @ 及后续输入（延迟等点击浮层项先于 blur 回调执行；浮层内 mousedown 已阻止 blur）
  msgInput.addEventListener("blur", () => setTimeout(cancelMentionPicker, 150));
  // 光标在输入框内移动（不触发 input）时同步浮层与快照位置
  document.addEventListener("selectionchange", () => {
    if (document.activeElement === msgInput && !$("mentionPicker").hidden) updateMentionPicker();
  });
  msgInput.addEventListener("keydown", (e) => {
    const pickerOpen = !$("mentionPicker").hidden;
    if (pickerOpen) {
      if (e.key === "ArrowDown") { e.preventDefault(); moveMentionPicker(1); return; }
      if (e.key === "ArrowUp") { e.preventDefault(); moveMentionPicker(-1); return; }
      if (e.key === "Enter" && !e.shiftKey) {
        e.preventDefault();
        const item = $("mentionPicker").querySelector(".mention-pick-item.active");
        if (item) item.click();
        return;
      }
      if (e.key === "Escape") { e.preventDefault(); cancelMentionPicker(); return; }
    }
    if (e.key === "Enter" && !e.shiftKey) { e.preventDefault(); sendMessage(); }
  });

  // ---- 聊天区 / 输入区可拖动分割线 ----
  initChatResizer();
  applyChatResizer();

  // ---- 附件上传（富媒体 5.2：图片多选 / 语音 / 画布标注）----
  $("attachBtn").disabled = false;
  $("attachBtn").onclick = () => $("attachInput").click();
  $("attachInput").onchange = (e) => {
    for (const f of e.target.files || []) {
      if (pendingAttachments.length >= 9) { toast(t("attach.maxCount", { count: 9 })); break; }
      if (f.size > 20 * 1024 * 1024) { toast(t("attach.overSize", { name: f.name, mb: 20 })); continue; }
      pendingAttachments.push({ file: f, uploading: false });
    }
    e.target.value = "";
    renderAttachList();
  };
  // 语音消息：点击开始录音，再点停止并加入附件（取消走状态条按钮）
  $("voiceBtn").disabled = false;
  $("voiceBtn").onclick = () => { if (voiceRecorder) stopVoiceRecording(); else startVoiceRecording(); };
  $("voiceCancel").onclick = cancelVoiceRecording;
  // 画布标注
  $("canvasBtn").disabled = false;
  $("canvasBtn").onclick = openCanvasModal;
  $("cvCancel").onclick = closeCanvasModal;
  $("cvInsert").onclick = insertCanvas;
  $("cvClear").onclick = () => {
    const c = $("cvCanvas");
    c.getContext("2d").fillStyle = "#fff";
    c.getContext("2d").fillRect(0, 0, c.width, c.height);
  };
  $("cvToolBrush").onclick = () => { cvTool = "brush"; $("cvToolBrush").classList.add("on"); $("cvToolEraser").classList.remove("on"); };
  $("cvToolEraser").onclick = () => { cvTool = "eraser"; $("cvToolEraser").classList.add("on"); $("cvToolBrush").classList.remove("on"); };
  const cvEl = $("cvCanvas");
  cvEl.addEventListener("pointerdown", cvDown);
  cvEl.addEventListener("pointermove", cvMove);
  cvEl.addEventListener("pointerup", cvUp);
  cvEl.addEventListener("pointerleave", cvUp);
  $("createGroupBtn").onclick = () => { Promise.all([loadAgentDirectory(), loadUserDirectory()]).then(openCreateModal); };
  $("refreshGroupsBtn").onclick = () => { loadGroups(); toast(t("groups.refreshed")); };
  $("refreshMembersBtn").onclick = async () => {
    if (!state.activeGroupId) { toast(t("chat.selectGroup")); return; }
    await refreshActiveGroup();
    toast(t("members.refreshed"));
  };
  $("createCancel").onclick = () => $("createModal").classList.add("hidden");
  $("createConfirm").onclick = createGroup;
  $("createMemberSearch").addEventListener("input", renderCreatePick); // 创建知聚：按昵称 / ID 过滤可选成员
  // 知聚设置
  gsAvatarPicker = bindAvatarPicker("gsAvatarPreview", "gsAvatarFile", "gsAvatarUploadBtn", "gsAvatarClearBtn", "👥", (url) => { groupSettingsAvatar = url; });
  $("groupSettingsBtn").onclick = openGroupSettings;
  $("gsCancel").onclick = () => $("groupSettingsModal").classList.add("hidden");
  $("gsConfirm").onclick = saveGroupSettings;
  $("gsDisbandBtn").onclick = disbandGroup;
  $("gsGroupName").addEventListener("keydown", (e) => { if (e.key === "Enter") { e.preventDefault(); saveGroupSettings(); } });
  $("createGroupName").addEventListener("keydown", (e) => {
    if (e.key === "Enter") { e.preventDefault(); createGroup(); }
  });
  // ---- 知聚话题 ----
  $("topicCancel").onclick = closeTopicModal;
  $("topicConfirm").onclick = createTopic;
  $("topicName").addEventListener("keydown", (e) => {
    if (e.key === "Enter") { e.preventDefault(); createTopic(); }
  });
  // ---- Mermaid 源码查看 ----
  $("mmdSourceCopy").onclick = async () => {
    const ok = await copyText($("mmdSourceCode").textContent);
    toast(ok ? t("mmd.copied") : t("mmd.copyFail"));
  };
  $("mmdSourceClose").onclick = () => $("mmdSourceModal").classList.add("hidden");
  $("addMemberBtn").onclick = () => { Promise.all([loadAgentDirectory(), loadUserDirectory()]).then(openAddMemberModal); };
  $("addMemberCancel").onclick = () => $("addMemberModal").classList.add("hidden");
  $("addMemberConfirm").onclick = addMembers;
  $("addMemberSearch").addEventListener("input", renderAddPick); // 添加成员：按昵称 / ID 过滤可选成员
  // 消息搜索（知聚内全文）
  $("searchBtn").onclick = openSearchModal;
  $("searchClose").onclick = () => $("searchModal").classList.add("hidden");
  $("searchGo").onclick = doSearch;
  $("searchInput").addEventListener("keydown", (e) => { if (e.key === "Enter") { e.preventDefault(); doSearch(); } });
  // 多位数字员工讨论
  $("discussBtn").onclick = openDiscussModal;
  $("discussCancel").onclick = () => $("discussModal").classList.add("hidden");
  $("discussGo").onclick = startDiscussion;
  // 知聚内触发方式设置弹窗
  $("gtTriggerMode").addEventListener("change", syncGroupTriggerForm);
  $("gtCancel").onclick = closeGroupTriggerModal;
  $("gtSave").onclick = saveGroupTrigger;
  $("mentionAllBtn").onclick = () => {
    state.mentionAll = !state.mentionAll;
    $("mentionAllBtn").classList.toggle("on", state.mentionAll);
  };
  $("visibilitySelect").onchange = (e) => { state.visibility = e.target.value; };

  // 消息区滚动跟踪：仅当停靠最底部时保持“跟随新消息”，任何上滑立即取消跟随——
  // 否则滚轮大幅滚动（进入 120px 贴底区）会被 stick() 反复拉回底部，表现为滚动异常。
  // 大幅跳跃（甩动滚轮 / 拖滚动条）时若视口已滑出已渲染窗口，立即同步重建，避免白屏一帧。
  $("messages").addEventListener("scroll", () => {
    const el = $("messages");
    vscroll.stickBottom = el.scrollHeight - el.scrollTop - el.clientHeight < 2;
    const r = state.activeGroupId ? room(state.activeGroupId) : null;
    const h = vscroll.heights;
    if (r && h && r.messages.length > 0 && vscroll.start < vscroll.end) {
      const loadH = r.allLoaded ? 0 : LOAD_MORE_HEIGHT;
      const anchor = lowerBound(h, Math.max(0, el.scrollTop - loadH));
      if (anchor < vscroll.start || anchor >= vscroll.end) {
        virtualRender(); // 同步重建窗口覆盖当前视口（scrollTop 每帧最多被纠正一次，不会级联）
        return;
      }
    }
    scheduleVirtualRender();
  });

  // 语言切换后刷新品牌、标签标题与连接状态（其余动态文案随 app.js 逐个迁移接入 t()）
  document.addEventListener("i18nchanged", () => {
    applyBranding(branding);
    if (typeof updateDocTitle === "function") updateDocTitle();
    if (typeof setStatus === "function") setStatus(_connOnline, _connKey);
  });

  // 恢复上次会话（校验令牌），否则显示登录页
  tryRestoreSession();
}

init();
