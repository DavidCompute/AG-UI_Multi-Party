// 验证 parseBridgeResponse / bridgeFileName / bridgeFileKind 的解析逻辑（从 app.js 提取的独立函数）
function bridgeFileName(url, index, explicitName, resultText) {
  if (typeof explicitName === "string" && explicitName.trim()) return explicitName.trim();
  try {
    const seg = decodeURIComponent(url.split("?")[0].split("/").filter(Boolean).pop() || "");
    if (/\.[A-Za-z0-9]{1,10}$/.test(seg)) return seg;
  } catch { }
  const m = /([\w\u4e00-\u9fa5（）()\-_.]+?\.(?:pptx?|docx?|xlsx?|pdf|txt|md|json|csv|zip|rar|7z|png|jpe?g|gif|webp|bmp|xml|html?|log))\b/i.exec(resultText || "");
  if (m) return m[1];
  return `附件 ${index + 1}`;
}
function bridgeFileKind(url, name) {
  const ext = ((name || url).split("?")[0].split(".").pop() || "").toLowerCase();
  if (/^(png|jpe?g|gif|webp|bmp|svg|ico)$/.test(ext)) return "image";
  if (/^(txt|md|json|csv|xml|html?|log|yaml|yml)$/.test(ext)) return "text";
  if (/^(pdf|docx?|xlsx?|pptx?)$/.test(ext)) return "document";
  return "binary";
}
function parseBridgeResponse(content) {
  const text = String(content || "");
  const trimmed = text.trimEnd();
  const start = trimmed.lastIndexOf("{");
  if (start < 0) return { text, attachments: [] };
  let obj = null;
  try { obj = JSON.parse(trimmed.slice(start)); } catch { }
  if (!obj || typeof obj !== "object" || Array.isArray(obj)) return { text, attachments: [] };
  const urls = (Array.isArray(obj.attachUrls) ? obj.attachUrls : []).filter((u) => typeof u === "string" && u);
  if (urls.length === 0) return { text, attachments: [] };
  const resultText = typeof obj.resultText === "string" ? obj.resultText : "";
  const names = Array.isArray(obj.fileNames) ? obj.fileNames : (Array.isArray(obj.names) ? obj.names : []);
  const attachments = urls.map((url, i) => {
    const name = bridgeFileName(url, i, names[i], resultText);
    return { url, name, kind: bridgeFileKind(url, name), size: 0 };
  });
  const display = text.slice(0, start).trimEnd() || resultText;
  return { text: display, attachments };
}

let pass = 0, fail = 0;
function check(name, cond) {
  if (cond) { pass++; console.log("✓ " + name); }
  else { fail++; console.log("✗ FAIL: " + name); }
}

// 1. 用户场景：文本 + 尾部 JSON（文件名在 resultText 中）
{
  const input = "PPT 演示文稿：ec8627bc8fb34740a95789b4ee47eca1_群聊摘要.pptx\n\n📎 下载：ec8627bc8fb34740a95789b4ee47eca1_群聊摘要.pptx" +
    JSON.stringify({ resultText: "已生成PPT 演示文稿：ec8627bc8fb34740a95789b4ee47eca1_群聊摘要.pptx", attachUrls: ["http://localhost:5088/api/file/download?path=abc%3D"], contextContinue: true });
  const r = parseBridgeResponse(input);
  check("场景1: JSON 从显示文本剥离", !r.text.includes("{") && !r.text.includes("resultText"));
  check("场景1: 保留 JSON 前文本", r.text.includes("PPT 演示文稿"));
  check("场景1: 提取 1 个附件", r.attachments.length === 1);
  check("场景1: 文件名从 resultText 提取", r.attachments[0].name === "ec8627bc8fb34740a95789b4ee47eca1_群聊摘要.pptx");
  check("场景1: kind=document", r.attachments[0].kind === "document");
}

// 2. 纯文本无 JSON → 原样
{
  const r = parseBridgeResponse("你好，这是普通回复");
  check("场景2: 无 JSON 原样返回", r.text === "你好，这是普通回复" && r.attachments.length === 0);
}

// 3. JSON 不完整（流式中）→ 原样
{
  const r = parseBridgeResponse("部分文本{\"resultText\":\"x\"");
  check("场景3: 不完整 JSON 原样返回", r.text === "部分文本{\"resultText\":\"x\"" && r.attachments.length === 0);
}

// 4. JSON 无 attachUrls → 原样
{
  const r = parseBridgeResponse("文本" + JSON.stringify({ resultText: "x" }));
  check("场景4: 无 attachUrls 原样返回", r.attachments.length === 0 && r.text.includes("resultText"));
}

// 5. URL 带文件名 + 显式 fileNames
{
  const r = parseBridgeResponse("看附件" + JSON.stringify({ attachUrls: ["http://host/files/报告.pdf"], fileNames: ["月度报告.pdf"] }));
  check("场景5: fileNames 优先", r.attachments[0].name === "月度报告.pdf");
  const r2 = parseBridgeResponse("看附件" + JSON.stringify({ attachUrls: ["http://host/files/图表.png"] }));
  check("场景5b: URL 文件名 + 图片 kind", r2.attachments[0].name === "图表.png" && r2.attachments[0].kind === "image");
}

// 6. JSON 前文本为空 → 回退 resultText
{
  const r = parseBridgeResponse(JSON.stringify({ resultText: "摘要文本", attachUrls: ["http://h/a.docx"] }));
  check("场景6: 空文本回退 resultText", r.text === "摘要文本" && r.attachments[0].kind === "document");
}

console.log(`\n通过 ${pass} / ${pass + fail}`);
process.exit(fail ? 1 : 0);
