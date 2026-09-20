"""Word / PDF 配图的「上下文二次尝试 + 显式门槛」真实验证（临时脚本）。

验什么（都是 PPT 侧 1.0.145 修过、这次补齐到 Word/PDF 的同一类问题）：
  A) Word：正文写着人名、模型只给通用词 → 最终嵌进去的**必须是那个人的照片**（按字节比对）；
  B) Word：关键词已经高分命中 → 不该多发一次检索；
  C) Word/PDF：上下文也配不上 → 不配图 + warning（不静默），且不给不相干图片；
  D) PDF：同样能靠上下文配上（用 images 回显 + PDF 里确有位图判定）。

依赖：本机 docker（产物读回靠 docker cp）。图库与模型都是真实数据。
用法：python tools/verify_docx_pdf_image_match.py
"""
import io
import json
import re
import subprocess
import sys
import tempfile
import urllib.error
import urllib.request
import zipfile

BASE = "http://127.0.0.1:5200"
CONTAINER = "agui-group-chat-web"
try:
    sys.stdout.reconfigure(encoding="utf-8")
except Exception:
    pass

ok = True


def check(label, cond, extra=""):
    global ok
    print(("  OK   " if cond else "  FAIL ") + label + (("  " + extra) if extra and not cond else ""))
    if not cond:
        ok = False


def call(m, p, b=None, t=None, raw=False):
    d = json.dumps(b, ensure_ascii=False).encode() if b is not None else None
    h = {"Content-Type": "application/json"} if d else {}
    if t:
        h["Authorization"] = "Bearer " + t
    r = urllib.request.Request(BASE + p, data=d, headers=h, method=m)
    try:
        with urllib.request.urlopen(r, timeout=900) as x:
            payload = x.read()
            return payload if raw else json.loads(payload or b"{}")
    except urllib.error.HTTPError as e:
        return {"_http": e.code, "_body": e.read().decode("utf-8", "replace")[:300]}


def run_skill(skill_id, payload, token):
    r = call("POST", f"/ag-ui/skills/{skill_id}/run", {"query": json.dumps(payload, ensure_ascii=False)}, t=token)
    return json.loads(r["result"])


def docker_cp(container_path):
    tmp = tempfile.mkdtemp()
    dest = tmp + "/out.bin"
    subprocess.run(["docker", "cp", f"{CONTAINER}:{container_path}", dest],
                   check=True, capture_output=True)
    with open(dest, "rb") as f:
        return f.read()


token = call("POST", "/ag-ui/user/login", {"username": "david", "password": "lingtong"})["token"]

# ── 从真实图库挑一个“像人名”的描述（不硬编码任何名字）──
libs = call("GET", "/ag-ui/image-libs", t=token)["libraries"]
cand = None
for l in libs:
    for a in (l.get("assets") or []):
        cap = (a.get("caption") or "").strip()
        if a.get("status") == "ready" and re.fullmatch(r"[\u4e00-\u9fa5]{2,4}", cap) and a.get("fileName", "").lower().endswith(".png"):
            cand = (l["libId"], a["assetId"], a["fileName"], cap)
            break
    if cand:
        break
check("图库里有“人名型描述 + PNG”的图（本场景前提）", cand is not None)
if not cand:
    print("\n结果：前提不成立，跳过")
    sys.exit(0)
lib_id, asset_id, file_name, person = cand
want_bytes = call("GET", f"/ag-ui/image-libs/{lib_id}/assets/{asset_id}/raw", t=token, raw=True)
print(f"   选用：{file_name}（描述“{person}”，{len(want_bytes)} 字节）")

print("\n== A) Word：正文写着人名 + 模型只给通用词 → 应嵌这个人的照片 ==")
d = run_skill("docx_report", {
    "title": "年度颁奖典礼",
    "sections": [
        {"heading": f"高效习惯优秀进步奖 · {person}"},
        {"paragraph": "以下是获奖同事的风采。"},
        {"image": {"imageQuery": "员工 颁奖 舞台", "caption": "获奖同事"}},
    ],
}, token)
print("   images =", d.get("images"))
print("   warnings =", d.get("warnings"))
check("返回里回显了实际用到的检索词", bool(d.get("images")), str(d)[:200])
used_query = (d.get("images") or [{}])[0].get("query", "")
check("胜出的是“上下文文字”（含人名），不是模型的关键词", person in used_query, used_query)
check("且是高分那张（分数 ≥ 0.6）", (d.get("images") or [{}])[0].get("score", 0) >= 0.6, str(d.get("images")))
check("没有降级告警（确实配上了）", not d.get("warnings"), str(d.get("warnings")))
docx_path = (d.get("produce_file") or {}).get("path")
check("拿得到产物路径", bool(docx_path), str(d.get("produce_file"))[:200])
if docx_path:
    body = docker_cp(docx_path)
    with zipfile.ZipFile(io.BytesIO(body)) as z:
        media = [n for n in z.namelist() if "media/" in n]
        got = z.read(media[0]) if media else b""
    check("文档里真的嵌了位图", bool(media), str(z.namelist())[:200])
    check(f"嵌入的字节 == 图库里 {file_name} 的字节（真是这个人的照片）", got == want_bytes,
          f"embed={len(got)} want={len(want_bytes)}")

print("\n== B) Word：无关上下文 + 无关关键词 → 不配图 + 告警（不静默、不误配）==")
d2 = run_skill("docx_report", {
    "title": "无关内容验证",
    "sections": [
        {"heading": "季度工作总结"},                       # 上下文与关键词不同，两条原因都该报出来
        {"image": {"imageQuery": "qzxv-不存在-9987"}},
    ],
}, token)
print("   images =", d2.get("images"))
print("   warnings =", (d2.get("warnings") or [])[:2])
check("没有误配任何图库图片", not d2.get("images"), str(d2.get("images")))
check("降级不静默（有 warning）", bool(d2.get("warnings")), str(d2.get("warnings"))[:200])
check("告警里同时写了关键词与上下文两条原因",
      bool(d2.get("warnings")) and "关键词" in d2["warnings"][0] and "上下文" in d2["warnings"][0],
      str(d2.get("warnings"))[:300])

print("\n== C) PDF：同样能靠上下文配上（且 PDF 里确有位图）==")
d3 = run_skill("pdf_doc", {
    "title": "年度颁奖典礼",
    "blocks": [
        {"type": "h1", "text": f"高效习惯优秀进步奖 · {person}"},
        {"type": "image", "imageQuery": "员工 颁奖 舞台", "caption": "获奖同事"},
    ],
}, token)
print("   images =", d3.get("images"))
pdf_path = (d3.get("produce_file") or {}).get("path")
check("PDF 回显了实际用到的检索词（含人名）", person in ((d3.get("images") or [{}])[0].get("query") or ""), str(d3.get("images")))
check("PDF 没有配图降级告警（字体提示之类不算）",
      not [w for w in (d3.get("warnings") or []) if "配图检索" in w], str(d3.get("warnings")))
if pdf_path:
    pdf = docker_cp(pdf_path)
    check("PDF 里确有位图（/Subtype /Image）", b"/Image" in pdf, f"{len(pdf)} 字节")

print("\n结果：" + ("全部通过" if ok else "存在失败项"))
sys.exit(0 if ok else 1)
