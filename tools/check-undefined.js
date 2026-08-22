// 静态检查：找出 app.js 顶层函数中引用了但未声明的自由变量（启发式，排除常见误报）
const fs = require("fs");
const src = fs.readFileSync("src/AguiGroupChat.Web/wwwroot/app.js", "utf8");

// ---------- 全局声明 ----------
const globalDecl = new Set();
for (const m of src.matchAll(/^(?:const|let|var)\s+([A-Za-z_$][\w$]*)/gm)) globalDecl.add(m[1]);
for (const m of src.matchAll(/^(?:async\s+)?function\s+([A-Za-z_$][\w$]*)/gm)) globalDecl.add(m[1]);
// 顶层箭头函数赋值：const f = (a, b) => { ... } / async (a) => ...
for (const m of src.matchAll(/^(?:const|let|var)\s+([A-Za-z_$][\w$]*)\s*=\s*(?:async\s*)?\(/gm)) globalDecl.add(m[1]);

const builtins = new Set(`
window document console requestAnimationFrame cancelAnimationFrame ResizeObserver
Float64Array Map Set String Number Math Date JSON Promise Object Array
encodeURIComponent decodeURIComponent setTimeout clearTimeout URL
CSS isNaN parseInt parseFloat fetch FormData WebSocket
marked DOMPurify navigator location localStorage Error
undefined null true false NaN Infinity
`.trim().split(/\s+/));

const keywords = new Set(`
break case catch class const continue debugger default delete do else export extends
finally for function if import in instanceof let new return super switch this throw
try typeof var void while with yield async await of get set static
`.trim().split(/\s+/));

// ---------- 顶层函数边界（含 async function 与顶层箭头函数） ----------
const funcs = [];
for (const m of src.matchAll(/^(?:async\s+)?function\s+([A-Za-z_$][\w$]*)\s*\(([^)]*)\)\s*\{/gm)) {
  funcs.push({ name: m[1], params: new Set((m[2] || "").split(",").map((s) => s.trim()).filter(Boolean)), start: m.index });
}
for (const m of src.matchAll(/^(?:const|let|var)\s+([A-Za-z_$][\w$]*)\s*=\s*(?:async\s*)?\(([^)]*)\)\s*=>\s*\{/gm)) {
  funcs.push({ name: m[1], params: new Set((m[2] || "").split(",").map((s) => s.trim()).filter(Boolean)), start: m.index });
}
// 按起始位置排序
funcs.sort((a, b) => a.start - b.start);
for (let i = 0; i < funcs.length; i++) {
  const f = funcs[i];
  f.end = i + 1 < funcs.length ? funcs[i + 1].start : src.length;
  f.body = src.slice(f.start, f.end);
}
const endIdx = src.lastIndexOf("\ninit();");
const topFuncs = funcs.filter((f) => f.start < endIdx);

// 全局箭头参数（用于识别 (x) => ... 的参数名）
const arrowParams = new Set();
for (const m of src.matchAll(/\(([^()]*)\)\s*=>/g))
  for (const p of m[1].split(",")) {
    const n = p.trim().split(/[=:]/)[0].trim();
    if (/^[A-Za-z_$][\w$]*$/.test(n)) arrowParams.add(n);
  }
for (const m of src.matchAll(/\b([A-Za-z_$][\w$]*)\s*=>/g)) arrowParams.add(m[1]);

// 函数体内本地声明收集
function collectLocals(body, params) {
  const local = new Set(params);
  // const/let/var 单条语句（可能逗号分隔多个声明符）
  for (const m of body.matchAll(/\b(?:const|let|var)\s+([^;=\n]+?)(?==|=|;)/g)) {
    const seg = m[1];
    // 去掉可能的解构 {..} / [..] 后按逗号拆分
    const cleaned = seg.replace(/\{[^}]*\}/g, "").replace(/\[[^\]]*\]/g, "");
    for (const part of cleaned.split(",")) {
      const n = part.trim().split(/[=:]/)[0].trim();
      if (/^[A-Za-z_$][\w$]*$/.test(n)) local.add(n);
    }
  }
  // function 声明
  for (const m of body.matchAll(/^\s*(?:async\s+)?function\s+([A-Za-z_$][\w$]*)/gm)) local.add(m[1]);
  // for(let x ...) 与 for(const {a, b} of ...)
  for (const m of body.matchAll(/\bfor\s*\(\s*(?:const|let|var)\s+([A-Za-z_$][\w$]*)/g)) local.add(m[1]);
  for (const m of body.matchAll(/\bfor\s*\(\s*(?:const|let|var)\s*\{([^}]*)\}\s+of/g))
    for (const p of m[1].split(",")) {
      const n = p.trim().split(/[:=]/)[0].trim();
      if (/^[A-Za-z_$][\w$]*$/.test(n)) local.add(n);
    }
  // 解构声明 {a, b} = ...
  for (const m of body.matchAll(/\b(?:const|let|var)\s*\{([^}]*)\}\s*=/g))
    for (const p of m[1].split(",")) {
      const n = p.trim().split(/[:=]/)[0].trim();
      if (/^[A-Za-z_$][\w$]*$/.test(n)) local.add(n);
    }
  for (const m of body.matchAll(/\b(?:const|let|var)\s*\[([^\]]*)\]\s*=/g))
    for (const p of m[1].split(",")) {
      const n = p.trim().split(/[=]/)[0].trim();
      if (/^[A-Za-z_$][\w$]*$/.test(n)) local.add(n);
    }
  return local;
}

let issues = 0;
for (const f of topFuncs) {
  const local = collectLocals(f.body, f.params);
  // 引用：剔除字符串/注释；排除属性访问（.x ?.x）、对象键（x:）、关键字后标识符
  const refs = new Set();
  const bodyNoStr = f.body.replace(/`[^`]*`|"[^"]*"|'[^']*'/g, " ");
  const bodyNoComment = bodyNoStr.replace(/\/\*[\s\S]*?\*\//g, " ").replace(/\/\/[^\n]*/g, " ");
  for (const m of bodyNoComment.matchAll(/(?<![.\w$])\b([A-Za-z_$][\w$]*)\b(?!\s*:)/g)) refs.add(m[1]);
  const missing = [...refs].filter(
    (r) => !local.has(r) && !globalDecl.has(r) && !builtins.has(r) && !keywords.has(r)
      && !arrowParams.has(r) && r !== f.name
  );
  if (missing.length) {
    issues++;
    console.log(`[${f.name}] 疑似未定义: ${missing.join(", ")}`);
  }
}
console.log(issues === 0 ? "未发现疑似未定义的自由变量" : `共 ${issues} 个函数存在疑似未定义自由变量`);
