// 配额水位 / 覆写同步 的前端回归（Playwright，真实浏览器）。
//
// 覆盖 1.0.265～1.0.272 的渲染与同步路径。**只读**：不写任何真实数据——
// 服务端返回的数据用 fetch 桩替换，事件用 onGroupUpdated() 直接投递。
// 后端语义（阈值、覆写写入、配额拦截）由 .NET 集成测试覆盖，这里只盯前端。
//
// 用法：
//   cd tools && HEADLESS=1 node ui-quota-alerts.mjs
//   BASE_URL=http://localhost:5200 USERNAME=david PASSWORD=lingtong node ui-quota-alerts.mjs
//
// 退出码：0 = 全部通过（或仅有跳过项）；1 = 有失败项。

import { chromium } from "playwright";

const BASE_URL = process.env.BASE_URL || "http://localhost:5200";
const USERNAME = process.env.USERNAME || "david";
const PASSWORD = process.env.PASSWORD || "lingtong";
const HEADLESS = process.env.HEADLESS === "1";

let pass = 0, fail = 0, skip = 0;
const check = (label, cond, extra = "") => {
  if (cond) { pass++; console.log("  PASS  " + label + (extra ? "  — " + extra : "")); }
  else { fail++; console.log("  FAIL  " + label + (extra ? "  — " + extra : "")); }
};
const skipped = (label, why) => { skip++; console.log("  SKIP  " + label + (why ? "  — " + why : "")); };

const browser = await chromium.launch({ headless: HEADLESS });
const page = await browser.newPage();
const errors = [];
page.on("pageerror", (e) => errors.push(String(e)));

try {
  // ---------------- 登录 ----------------
  await page.goto(BASE_URL, { waitUntil: "domcontentloaded" });
  await page.waitForSelector("#authOverlay", { timeout: 15000 });
  if (!(await page.evaluate(() => document.getElementById("authOverlay").classList.contains("hidden")))) {
    await page.fill("#authUsername", USERNAME);
    await page.fill("#authPassword", PASSWORD);
    await page.click("#authSubmit");
    await page.waitForFunction(() => document.getElementById("authOverlay")?.classList.contains("hidden"), { timeout: 20000 });
  }
  console.log(`已登录 ${USERNAME} → ${BASE_URL}\n`);

  // ---------------- 1) 真实接口形状 ----------------
  const shape = await page.evaluate(async () => {
    const u = await fetch("/ag-ui/admin/usage?days=7", { headers: { Authorization: "Bearer " + state.token } });
    const ud = u.ok ? await u.json() : null;
    const q = await fetch("/ag-ui/admin/quota-status", { headers: { Authorization: "Bearer " + state.token } });
    const qd = q.ok ? await q.json() : null;
    return {
      usageStatus: u.status,
      hasGroups: Array.isArray(ud?.groupsToday),
      hasUsers: Array.isArray(ud?.usersToday),
      groupRowKeys: (ud?.groupsToday?.[0] ? Object.keys(ud.groupsToday[0]) : []),
      quotaStatus: q.status,
      quotaKeys: qd ? Object.keys(qd) : [],
    };
  });
  check("/usage → 200 且含 groupsToday[] / usersToday[]", shape.usageStatus === 200 && shape.hasGroups && shape.hasUsers, JSON.stringify({ s: shape.usageStatus, g: shape.hasGroups, u: shape.hasUsers }));
  if (shape.groupRowKeys.length) {
    check("/usage 分知聚行含 quota / level", ["quota", "level"].every((k) => shape.groupRowKeys.includes(k)), shape.groupRowKeys.join(","));
  } else {
    skipped("/usage 分知聚行字段", "今日暂无分知聚用量");
  }
  check("/quota-status → 200 且含 quotaPerGroup / warnRatio / groups", shape.quotaStatus === 200 && ["quotaPerGroup", "warnRatio", "groups"].every((k) => shape.quotaKeys.includes(k)), shape.quotaKeys.join(","));

  // ---------------- 2) 用量页三张表渲染（受控数据） ----------------
  const usageDom = await page.evaluate(async () => {
    const orig = window.fetch;
    const synthetic = {
      dailyQuotaPerUser: 1000,
      dailyQuotaPerGroup: 1000,
      groupsToday: [
        { groupId: "g_over", name: "超限知聚", tokens: 1500, calls: 3, quota: 1000, level: "over" },
        { groupId: "g_warn", name: "接近知聚", tokens: 850, calls: 2, quota: 1000, level: "warn" },
        { groupId: "g_ovr", name: "覆写知聚", tokens: 100, calls: 1, quota: 50000, level: "ok" },
      ],
      usersToday: [
        { userId: "u_over", name: "超限用户", tokens: 1500, calls: 3, quota: 1000, exempt: false, level: "over" },
        { userId: "u_warn", name: "接近用户", tokens: 850, calls: 2, quota: 1000, exempt: false, level: "warn" },
        { userId: "u_ok", name: "正常用户", tokens: 10, calls: 1, quota: 1000, exempt: false, level: "ok" },
        { userId: "system", name: "系统任务", tokens: 99999, calls: 9, quota: 1000, exempt: true, level: "ok" },
      ],
      days: [{ date: "2026-01-01", totalTokens: 1, promptTokens: 1, completionTokens: 0, reasoningTokens: 0, calls: 1 }],
    };
    window.fetch = async (url, opts) => {
      if (String(url).startsWith("/ag-ui/admin/usage")) {
        return { ok: true, status: 200, json: async () => synthetic };
      }
      return orig(url, opts);
    };
    try {
      await loadAdminUsage();
    } finally {
      window.fetch = orig;
    }
    return {
      groupRows: document.querySelectorAll("#adminUsageGroupRows tr").length,
      groupHit: document.querySelectorAll("#adminUsageGroupRows .usage-quota-hit").length,
      groupWarn: document.querySelectorAll("#adminUsageGroupRows .usage-quota-warn").length,
      overrideMark: document.querySelectorAll("#adminUsageGroupRows .usage-quota-ovr").length,
      userRows: document.querySelectorAll("#adminUsageUserRows tr").length,
      userHit: document.querySelectorAll("#adminUsageUserRows .usage-quota-hit").length,
      userWarn: document.querySelectorAll("#adminUsageUserRows .usage-quota-warn").length,
      userExempt: document.querySelectorAll("#adminUsageUserRows .usage-quota-exempt").length,
      meta: document.getElementById("adminUsageMeta").textContent || "",
      header: document.querySelector("#adminUsageUserRows")?.closest("table")?.querySelector("thead")?.textContent?.trim() || "",
    };
  });
  check("分知聚表 3 行", usageDom.groupRows === 3, "rows=" + usageDom.groupRows);
  check("分知聚：红标 1 / 琥珀 1", usageDom.groupHit === 1 && usageDom.groupWarn === 1, `hit=${usageDom.groupHit} warn=${usageDom.groupWarn}`);
  check("分知聚：配额覆写标 ⚙ 出现 1 次", usageDom.overrideMark === 1, "marks=" + usageDom.overrideMark);
  check("分触发者表 4 行", usageDom.userRows === 4, "rows=" + usageDom.userRows);
  check("分触发者：红标 1 / 琥珀 1 / 豁免 1", usageDom.userHit === 1 && usageDom.userWarn === 1 && usageDom.userExempt === 1, `hit=${usageDom.userHit} warn=${usageDom.userWarn} exempt=${usageDom.userExempt}`);
  check("表头为「触发者」", usageDom.header.includes("触发者"), usageDom.header);
  check("meta 汇总知聚 + 个人水位", /接近上限 1 个/.test(usageDom.meta) && /已超限 1 个/.test(usageDom.meta) && /个人接近上限 1 人/.test(usageDom.meta) && /已超限 1 人/.test(usageDom.meta), usageDom.meta);

  // ---------------- 3) 水位白名单 + 侧栏角标 ----------------
  const badge = await page.evaluate(async () => {
    await loadGroups();
    const gid = state.groups[0]?.groupId || null;
    const orig = window.fetch;
    window.fetch = async (url, opts) => {
      if (String(url).startsWith("/ag-ui/admin/quota-status")) {
        return {
          ok: true, status: 200, json: async () => ({
            quotaPerGroup: 1000, warnRatio: 0.8,
            groups: [
              { groupId: gid || "g_ok", level: "warn" },
              { groupId: "g_evil", level: 'x" onmouseover="alert(1)' },
            ],
          }),
        };
      }
      return orig(url, opts);
    };
    try {
      await loadQuotaStatus();
      renderGroupList();
      return {
        hasGroup: !!gid,
        realLevel: gid ? (state.quotaLevels.get(gid) || null) : null,
        evil: state.quotaLevels.get("g_evil") ?? null,
        warnTags: document.querySelectorAll("#groupList .quota-tag.warn").length,
        injectedAttr: document.querySelector("#groupList [onmouseover]") !== null,
        tags: document.querySelectorAll("#groupList .quota-tag").length,
      };
    } finally { window.fetch = orig; }
  });
  check("非法 level 不入 state", badge.evil === null, String(badge.evil));
  check("页面无注入属性", badge.injectedAttr === false, `tags=${badge.tags}`);
  if (badge.hasGroup) {
    check("合法 warn 收录并渲染角标", badge.realLevel === "warn" && badge.warnTags >= 1, `level=${badge.realLevel} tags=${badge.warnTags}`);
  } else {
    skipped("侧栏角标渲染", "当前账号没有知聚");
  }

  // ---------------- 4) GROUP_UPDATED 同步覆写字段 ----------------
  if (badge.hasGroup) {
    const sync = await page.evaluate(() => {
      const gid = state.groups[0].groupId;
      const until = Date.UTC(2027, 0, 2, 3, 4);
      state.activeGroupId = gid;
      onGroupUpdated({
        groupId: gid,
        updateFields: ["quotaPerGroup", "quotaPerGroupUntil", "model"],
        groupInfo: { quotaPerGroup: 1234, quotaPerGroupUntil: until, model: "deepseek-flash" },
      });
      const g = state.groups.find((x) => x.groupId === gid);
      openGroupSettings();
      const out = {
        quota: g.quotaOverride,
        until: g.quotaOverrideUntil,
        model: g.modelOverride,
        prefilledQuota: document.getElementById("gsQuotaOverride").value,
        prefilledUntil: document.getElementById("gsQuotaUntil").value,
        prefilledUntilBack: localInputToMs(document.getElementById("gsQuotaUntil").value),
        prefilledModel: document.getElementById("gsModelOverride").value,
      };
      document.getElementById("groupSettingsModal").classList.add("hidden");
      onGroupUpdated({ groupId: gid, updateFields: ["quotaPerGroup"], groupInfo: { quotaPerGroup: "" } });
      const cleared = state.groups.find((x) => x.groupId === gid);
      out.cleared = cleared.quotaOverride;
      out.clearedUntil = cleared.quotaOverrideUntil;
      return out;
    });
    check("GROUP_UPDATED 同步 quotaOverride=1234", sync.quota === 1234, String(sync.quota));
    check("GROUP_UPDATED 同步 quotaOverrideUntil", sync.until === Date.UTC(2027, 0, 2, 3, 4), String(sync.until));
    check("GROUP_UPDATED 同步 modelOverride", sync.model === "deepseek-flash", String(sync.model));
    check("重开设置弹窗回填新值", sync.prefilledQuota === "1234" && sync.prefilledModel === "deepseek-flash", JSON.stringify({ q: sync.prefilledQuota, m: sync.prefilledModel }));
    check("到期时间回填且可无损回转", sync.prefilledUntil.length === 16 && sync.prefilledUntilBack === Date.UTC(2027, 0, 2, 3, 4), JSON.stringify({ v: sync.prefilledUntil, back: sync.prefilledUntilBack }));
    check("空串覆写 → 本地归 null（额度与到期时间）", sync.cleared === null && sync.clearedUntil === null, JSON.stringify({ q: sync.cleared, u: sync.clearedUntil }));
  } else {
    skipped("GROUP_UPDATED 同步", "当前账号没有知聚");
  }

  // ---------------- 5) 无页面脚本错误 ----------------
  check("无页面脚本错误", errors.length === 0, errors.slice(0, 2).join(" | "));
} finally {
  await browser.close();
}

const summary = `通过 ${pass} / 失败 ${fail}${skip ? ` / 跳过 ${skip}` : ""}`;
console.log("\n" + (fail === 0 ? `全部通过 ✅ (${summary})` : `失败 ❌ (${summary})`));
process.exit(fail === 0 ? 0 : 1);
