const fs = require("fs");
const s = fs.readFileSync("src/AguiGroupChat.Web/wwwroot/app.js", "utf8");
const lines = s.split("\n");
let depth = 0;
let state = "code"; // code / str1 / str2 / tmpl / regex / line / block
// 模板帧栈：tmpl=模板内容态，interp=${...} 插值，block=插值内的代码块。
// } 弹栈规则：block → 留在 code；interp → 回到 tmpl。
const frames = [];
const REGEX_AFTER = new Set(["(", "[", "{", ",", ";", ":", "=", "!", "&", "|", "?", "+", "-", "*", "%", "^", "~", "<", ">"]);
const REGEX_KEYWORDS = new Set(["return", "typeof", "instanceof", "in", "of", "case", "delete", "void", "new", "do", "else", "yield", "await"]);
for (let ln = 0; ln < lines.length; ln++) {
  const line = lines[ln];
  let j = 0;
  let prev = "";
  let prevWord = "";
  while (j < line.length) {
    const c = line[j];
    const n = line[j + 1];
    if (state === "line") { state = "code"; break; }
    if (state === "block") {
      if (c === "*" && n === "/") { state = "code"; j++; }
      j++; continue;
    }
    if (state === "str1") {
      if (c === "\\") { j += 2; continue; }
      if (c === "'") state = "code";
      j++; continue;
    }
    if (state === "str2") {
      if (c === "\\") { j += 2; continue; }
      if (c === '"') state = "code";
      j++; continue;
    }
    if (state === "tmpl") {
      if (c === "\\") { j += 2; continue; }
      if (c === "`") {
        // 模板结束：弹 tmpl 帧，回到 code（若外层仍是插值则继续处理其代码）
        if (frames.length > 0 && frames[frames.length - 1] === "tmpl") frames.pop();
        state = "code";
      } else if (c === "$" && n === "{") {
        // 插值开始：进入 code；${ 的 { 计入 depth（配对的 } 弹帧时同样减回）
        frames.push("interp");
        state = "code";
        depth++;
        j += 2; continue;
      }
      j++; continue;
    }
    if (state === "regex") {
      if (c === "\\") { j += 2; continue; }
      if (c === "/") state = "code";
      j++; continue;
    }
    if (c === "/" && n === "/") { state = "line"; j += 2; continue; }
    if (c === "/" && n === "*") { state = "block"; j += 2; continue; }
    if (c === "/" && n !== "'" && n !== '"'
        && (REGEX_AFTER.has(prev) || REGEX_KEYWORDS.has(prevWord))) {
      state = "regex"; j++; prev = ""; prevWord = ""; continue;
    }
    if (c === "'") { state = "str1"; prev = c; prevWord = ""; j++; continue; }
    if (c === '"') { state = "str2"; prev = c; prevWord = ""; j++; continue; }
    if (c === "`") {
      frames.push("tmpl");
      state = "tmpl";
      prev = c; prevWord = ""; j++; continue;
    }
    if (/[A-Za-z0-9_$]/.test(c)) {
      let k = j;
      while (k < line.length && /[A-Za-z0-9_$]/.test(line[k])) k++;
      prevWord = line.slice(j, k);
      prev = prevWord[prevWord.length - 1];
      j = k; continue;
    }
    if (c === "{") {
      depth++;
      const top = frames[frames.length - 1];
      if (top === "interp" || top === "block") frames.push("block"); // 插值内的代码块
    }
    if (c === "}") {
      depth--;
      const top = frames[frames.length - 1];
      if (top === "block") {
        frames.pop(); // 代码块结束：仍在插值代码内
      } else if (top === "interp") {
        frames.pop(); // 插值结束：回到模板内容态
        state = "tmpl";
      }
    }
    if (!/\s/.test(c)) { prev = c; prevWord = ""; }
    j++;
  }
  if (depth < 0) { console.log("NEG depth at line", ln + 1); process.exit(0); }
}
console.log("final depth:", depth);
