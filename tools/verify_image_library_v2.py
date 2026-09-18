"""图库二期上线核查（临时脚本，不入库）：技能刷新 / 图库接口 / 前端资源是否都到位。

用法：python tools/verify_image_library_v2.py
"""
import json
import sys
import urllib.request

BASE = "http://127.0.0.1:5200"
USER, PWD = "david", "lingtong"

try:
    sys.stdout.reconfigure(encoding="utf-8")
except Exception:
    pass


def call(method, path, body=None, token=None, raw=False):
    data = json.dumps(body, ensure_ascii=False).encode() if body is not None else None
    headers = {"Content-Type": "application/json; charset=utf-8"} if data else {}
    if token:
        headers["Authorization"] = "Bearer " + token
    req = urllib.request.Request(BASE + path, data=data, headers=headers, method=method)
    with urllib.request.urlopen(req, timeout=60) as r:
        payload = r.read()
        return r.status, (payload if raw else json.loads(payload or b"{}"))


def text(path):
    _, b = call("GET", path, raw=True)
    return b.decode("utf-8", "replace")


ok = True


def check(label, cond, extra=""):
    global ok
    print(("  OK   " if cond else "  FAIL ") + label + (("  " + extra) if extra and not cond else ""))
    if not cond:
        ok = False


st, res = call("POST", "/ag-ui/user/login", {"username": USER, "password": PWD})
assert st == 200, (st, res)
token = res["token"]
print("登录成功")

# 1) 内置文档技能是否已用新正文刷新（新正文里才有 imageQuery / 自调用端点）
st, skills = call("GET", "/ag-ui/skills", token=token)
items = skills.get("skills", skills) if isinstance(skills, dict) else skills
by_id = {s["skillId"]: s for s in items}
for sid in ("docx_report", "docx_gongwen", "docx_notice", "pdf_doc", "pptx_deck"):
    s = by_id.get(sid)
    check(sid + " 存在", s is not None)
    if not s:
        continue
    body = s.get("body") or ""
    check(sid + " 正文含 imageQuery", "imageQuery" in body)
    check(sid + " 正文含自调用端点", "AGUI_SELF_TOKEN" in body)

# 内置正文随版本刷新（DTO 不暴露 builtinVersion，就看二期新增的标记是否在库里）
check("docx_report 已含二期图库实现", "ResolveQueryImage" in (by_id.get("docx_report", {}).get("body") or ""))
check("pdf_doc 已含二期图库实现", "IsPdfEmbeddable" in (by_id.get("pdf_doc", {}).get("body") or ""))

# 2) 图库接口可用
st, libs = call("GET", "/ag-ui/image-libs", token=token)
check("GET /ag-ui/image-libs 200", st == 200)
lib_list = libs.get("libraries", []) if isinstance(libs, dict) else []
print("  当前图库数：", len(lib_list))
for lib in lib_list:
    assets = lib.get("assets") or []
    print("   -", lib["name"], lib["libId"], "图片", len(assets),
          "状态", sorted({a.get("status") for a in assets}))

# 3) 数字员工 DTO 带 imageLibraryIds（前端要按它回显「已选图库」）
st, agents = call("GET", "/ag-ui/agents", token=token)
alist = agents.get("agents", agents) if isinstance(agents, dict) else agents
check("数字员工 DTO 含 imageLibraryIds", all("imageLibraryIds" in a for a in alist))
bound = [(a.get("nickname"), a.get("imageLibraryIds")) for a in alist if a.get("imageLibraryIds")]
print("  已绑定图库的岗位：", bound or "（无）")

# 4) 前端资源包含二期改动（样式 + i18n）
for path, needle in (
    ("/style.css", ".imglib-grid"),
    ("/style.css", ".imglib-thumb"),
    ("/app.js", "renderImgLibModal"),
    ("/app.js", "imglib-docs"),
    ("/i18n/zh.js", "imgLib.title"),
    ("/i18n/zh.js", "agent.form.section.imglib"),
    ("/i18n/en.js", "imgLib.title"),
    ("/", "agentImgLibManageBtn"),
    ("/", "imgLibModal"),
):
    body = text(path)
    check("静态资源 " + path + " 含 " + needle, needle in body)
    # 必填的 i18n key 不应缺失（缺了界面会直接显示 key 原文）
check("zh.js 无未翻译的 imgLib key 残留", "imgLib.search" in text("/i18n/zh.js"))

# ===================== 端到端：建库 → 传图 → 检索 → 经平台跑文档技能配图 =====================

import io
import os
import time
import uuid

from PIL import Image, ImageDraw


def make_skyline(path):
    """生成一张**语义上可描述的**图（日落城市天际线：渐变天空 + 太阳 + 楼群剪影）。

    为何不用 4×4 纯色图：图库靠视觉模型写描述、再靠向量检索命中，
    纯色图描述不出可用关键词，测不出“检索→配图”这一段。
    """
    w, h = 960, 540
    img = Image.new("RGB", (w, h))
    d = ImageDraw.Draw(img)
    for y in range(h):
        t = y / h
        d.line([(0, y), (w, y)], fill=(int(255 - 120 * t), int(140 - 60 * t), int(90 + 60 * t)))
    d.ellipse([w * 0.68, h * 0.42, w * 0.68 + 110, h * 0.42 + 110], fill=(255, 214, 120))
    towers = [(0.04, 0.30), (0.12, 0.44), (0.2, 0.22), (0.3, 0.38), (0.38, 0.5),
              (0.47, 0.26), (0.56, 0.42), (0.64, 0.34), (0.72, 0.48), (0.8, 0.28), (0.9, 0.4)]
    for x, top in towers:
        d.rectangle([w * x, h * top, w * x + w * 0.062, h], fill=(28, 28, 48))
    img.save(path)
    return path


def upload(path):
    boundary = "----agui" + uuid.uuid4().hex
    with open(path, "rb") as f:
        data = f.read()
    name = os.path.basename(path)
    body = (
        ("--" + boundary + "\r\nContent-Disposition: form-data; name=\"file\"; filename=\"" + name + "\"\r\n")
        + "Content-Type: image/png\r\n\r\n"
    ).encode() + data + ("\r\n--" + boundary + "--\r\n").encode()
    req = urllib.request.Request(BASE + "/ag-ui/upload", data=body, method="POST", headers={
        "Content-Type": "multipart/form-data; boundary=" + boundary,
        "Authorization": "Bearer " + token,
    })
    with urllib.request.urlopen(req, timeout=120) as r:
        return json.loads(r.read())["attachments"][0]["attachmentId"]


print("\n[端到端] 建图库 → 传图 → 描述入库")
lib_name = "二期验证图库-" + uuid.uuid4().hex[:6]
st, lib = call("POST", "/ag-ui/image-libs", {"name": lib_name, "description": "二期端到端验证用"}, token=token)
check("创建图库 200", st == 200, str(lib))
lib_id = lib["libId"]

png = make_skyline(os.path.join(os.environ.get("TEMP", "/tmp"), "agui-e2e-skyline.png"))
att_id = upload(png)
st, asset = call("POST", f"/ag-ui/image-libs/{lib_id}/assets",
                 {"attachmentId": att_id, "fileName": "skyline.png"}, token=token)
check("登记图片 200", st == 200, str(asset))
asset_id = asset["assetId"]

ready = False
caption = ""
for _ in range(60):
    time.sleep(3)
    st, libs2 = call("GET", "/ag-ui/image-libs", token=token)
    cur = [x for x in libs2.get("libraries", []) if x["libId"] == lib_id]
    if not cur:
        continue
    a = [x for x in cur[0].get("assets", []) if x["assetId"] == asset_id]
    if not a:
        continue
    if a[0].get("status") == "ready":
        ready, caption = True, a[0].get("caption") or ""
        break
    if a[0].get("status") == "error":
        print("   图片处理失败：", a[0].get("error"))
        break
check("图片已生成描述并向量化（status=ready）", ready)
if caption:
    print("   自动描述：", caption[:120])

query = "城市天际线 黄昏 楼群剪影"
st, hits = call("POST", "/ag-ui/images/search", {"query": query, "topK": 3}, token=token)
check("语义检索命中", isinstance(hits, dict) and hits.get("count", 0) >= 1, str(hits)[:200])
print("   命中：", [(h.get("fileName"), h.get("score")) for h in (hits.get("images") or [])])

print("\n[端到端] 经平台跑 docx_report（带 imageQuery，验证注入链路）")
payload = {
    "title": "图库配图端到端验证",
    "sections": [
        {"paragraph": "本段用于验证图库配图链路：平台注入检索范围 → 技能回环回调 → 嵌入图片。"},
        {"image": {"imageQuery": query, "caption": "图 1 城市天际线"}},
    ],
}
st, run = call("POST", "/ag-ui/skills/docx_report/run", {"query": json.dumps(payload, ensure_ascii=False)}, token=token)
check("试运行接口 200", st == 200, str(run)[:200])
raw = (run or {}).get("result") or ""
print("   技能返回：", raw[:400])
try:
    sr = json.loads(raw)
except Exception:
    sr = {}
check("docx 生成成功", bool(sr.get("ok")), raw[:300])
check("配图无告警（说明确实从图库拿到了图）", "配图检索未成功" not in raw, raw[:400])
check("返回 produce_file 下载标记", "produce_file" in raw)
if sr.get("path"):
    print("   产物路径（容器内）：", sr["path"])
if sr.get("chartFont") is not None:
    print("   图表字体：", sr.get("chartFont"), "CJK=", sr.get("chartFontCjk"))

print("\n结果：" + ("全部通过" if ok else "存在失败项"))
sys.exit(0 if ok else 1)
