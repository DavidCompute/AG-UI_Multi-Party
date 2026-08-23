/* 知聚(KnowGath) 前端国际化运行时 · i18n.js
 *
 * 零依赖轻量方案（配合内联字典 en.js / zh.js 同步加载）。
 *
 * 功能：
 *   1. 语言探测：localStorage('agui.lang') -> navigator.language -> en（兜底）
 *   2. 静态文案：扫描 data-i18n / data-i18n-html / data-i18n-title / data-i18n-placeholder
 *      属性，替换为当前语言的文本
 *   3. 动态文案：暴露 i18n.t('key', params) 给 app.js 使用（别名 window.t）
 *   4. 语言切换：i18n.setLang('en'|'zh')，持久化到 localStorage，同步 <html lang>，
 *      并派发 'i18nchanged' 事件供界面对话补充渲染
 *
 * 约定：英文为 source of truth，中文 zh 为翻译。
 */
(function (global) {
  "use strict";

  var SUPPORTED = ["en", "zh"];
  var STORAGE_KEY = "agui.lang";
  var DEFAULT_LANG = "en";

  var current = detect();
  var dicts = global.I18N_DICTS || {};

  function detect() {
    try {
      var saved = global.localStorage && global.localStorage.getItem(STORAGE_KEY);
      if (saved && SUPPORTED.indexOf(saved) >= 0) return saved;
    } catch (e) { /* 隐私模式下可能抛异常，忽略 */ }
    var nav = String(
      (global.navigator && (global.navigator.language || global.navigator.userLanguage)) || ""
    ).toLowerCase();
    return nav.indexOf("zh") === 0 ? "zh" : "en";
  }

  function normalize(k) {
    return String(k == null ? "" : k).trim();
  }

  /* 查找文本：优先当前语言，缺失回退英文，再缺失返回 key 本身 */
  function t(key, params) {
    var k = normalize(key);
    var langDict = dicts[current] || {};
    var enDict = dicts.en || {};
    var s = langDict[k];
    if (s == null) s = enDict[k];
    if (s == null) return k;
    s = String(s);
    if (params) {
      s = s.replace(/\{(\w+)\}/g, function (_, name) {
        return params[name] != null ? String(params[name]) : "";
      });
    }
    return s;
  }

  /* 应用当前语言到静态 DOM */
  function applyDom(root) {
    var scope = root || global.document;
    if (!scope || !scope.querySelectorAll) return;

    // 纯文本
    scope.querySelectorAll("[data-i18n]").forEach(function (el) {
      el.textContent = t(el.getAttribute("data-i18n"));
    });
    // 含 HTML 的文案（谨慎使用，仅限受信任 key）
    scope.querySelectorAll("[data-i18n-html]").forEach(function (el) {
      el.innerHTML = t(el.getAttribute("data-i18n-html"));
    });
    // 标题 / 占位符
    scope.querySelectorAll("[data-i18n-title]").forEach(function (el) {
      el.title = t(el.getAttribute("data-i18n-title"));
    });
    scope.querySelectorAll("[data-i18n-placeholder]").forEach(function (el) {
      el.placeholder = t(el.getAttribute("data-i18n-placeholder"));
    });
    // aria 标签
    scope.querySelectorAll("[data-i18n-label]").forEach(function (el) {
      el.setAttribute("aria-label", t(el.getAttribute("data-i18n-label")));
    });
  }

  function syncHtmlLang() {
    var html = global.document && global.document.documentElement;
    if (html) html.setAttribute("lang", current === "zh" ? "zh-CN" : "en");
  }

  function setLang(lang) {
    if (SUPPORTED.indexOf(lang) < 0) lang = DEFAULT_LANG;
    current = lang;
    try {
      if (global.localStorage) global.localStorage.setItem(STORAGE_KEY, lang);
    } catch (e) { /* 忽略 */ }
    syncHtmlLang();
    if (global.document) {
      applyDom(global.document);
      global.document.dispatchEvent(new CustomEvent("i18nchanged", { detail: { lang: current } }));
    }
  }

  function lang() {
    return current;
  }

  function init() {
    syncHtmlLang();
    if (global.document) {
      if (global.document.readyState === "loading") {
        global.document.addEventListener("DOMContentLoaded", function () { applyDom(global.document); });
      } else {
        applyDom(global.document);
      }
      bindLangButton();
    }
  }

  /* 绑定顶栏语言切换按钮（#langBtn）：中英互切。
   * 用 document 事件委托：即使脚本在 DOM 解析前绑定，或用动态替换的按钮也能命中，
   * 避免按钮存在却因绑定时序问题导致点击无效。 */
  function bindLangButton() {
    var doc = global.document;
    if (!doc || !doc.addEventListener) return;
    doc.addEventListener("click", function (e) {
      var el = e && e.target;
      // 命中语言切换按钮（顶栏 #langBtn 或登录页 #authLangBtn）或其子节点
      while (el && el !== doc) {
        if (el.id === "langBtn" || el.id === "authLangBtn") {
          e.preventDefault();
          e.stopPropagation();
          setLang(current === "zh" ? "en" : "zh");
          return;
        }
        el = el.parentNode;
      }
    });
  }

  var i18n = {
    t: t,
    lang: lang,
    setLang: setLang,
    applyDom: applyDom,
    supported: SUPPORTED.slice()
  };

  global.i18n = i18n;
  // 快捷方式：app.js 动态文案用 t('key') / t('key', {..})
  global.t = t;

  init();
})(window);
