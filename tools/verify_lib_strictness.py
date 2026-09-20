"""图库「检索严格度」真实验证（临时脚本）。

验什么（都是可量化的，不靠“看起来”）：
  1. 库设了严格度 → 覆盖调用方传的 minScore（用同一查询在不同门槛下的命中数对比）；
  2. 走**技能链路**同样生效：docx_report 用同一个 imageQuery，
     库设为「严格」时配不上（不配图 + warning），改为「宽松」后能配上（图真的嵌进文档）；
  3. 严格度只影响本库，不影响别的库。

数据安全：全程在一个**临时图库**里做，结束删库；真实图库只读。
用法：python tools/verify_lib_strictness.py
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
lib_id = None


def check(label, cond, extra=""):
    global ok
    ok = ok and bool(cond)
    print(("  OK   " if cond else "  FAIL ") + label + (("  " + extra) if extra and not cond else ""))


def call(m, p, b=None, t=None):
    d = json.dumps(b, ensure_ascii=False).encode() if b is not None else None
    h = {"Content-Type": "application/json"} if d else {}
    if t:
        h["Authorization"] = "Bearer " + t
    r = urllib.request.Request(BASE + p, data=d, headers=h, method=m)
    try:
        with urllib.request.urlopen(r, timeout=900) as x:
            return x.status, json.loads(x.read() or b"{}")
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode("utf-8", "replace")[:300]


def upload_png(name, w=640, h=360):
    """生成一张 PNG 并经 /ag-ui/upload 拿 attachmentId（图库只认图片）。"""
    from PIL import Image, ImageDraw
    img = Image.new("RGB", (w, h))
    d = ImageDraw.Draw(img)
    for y in range(h):
        t = y / h
        d.line([(0, y), (w, y)], fill=(int(30 + 40 * t), int(20 + 30 * t), int(90 + 60 * t)))
    for i in range(6):
        x = w * (0.1 + i * 0.14)
        d.rectangle([x, h * 0.45, x + w * 0.07, h], fill=(20, 20, 40))
    buf = io.BytesIO()
    img.save(buf, format="PNG")
    boundary = "----agui" + uuid_hex()
    body = (("--" + boundary + f'\r\nContent-Disposition: form-data; name="file"; filename="{name}"\r\n')
            + "Content-Type: image/png\r\n\r\n").encode() + buf.getvalue() + ("\r\n--" + boundary + "--\r\n").encode()
    return body, boundary


def uuid_hex():
    import uuid
    return uuid.uuid4().hex


def post_multipart(path, body, boundary, token):
    r = urllib.request.Request(BASE + path, data=body, method="POST",
                               headers={"Content-Type": f"multipart/form-data; boundary={boundary}",
                                        "Authorization": "Bearer " + token})
    with urllib.request.urlopen(r, timeout=120) as x:
        return json.loads(x.read() or b"{}")


def run_skill(skill_id, payload, token):
    _, r = call("POST", f"/ag-ui/skills/{skill_id}/run", {"query": json.dumps(payload, ensure_ascii=False)}, t=token)
    return json.loads(r["result"])


def docx_media_bytes(container_path):
    tmp = tempfile.mkdtemp() + "/out.docx"
    subprocess.run(["docker", "cp", f"{CONTAINER}:{container_path}", tmp], check=True, capture_output=True)
    with zipfile.ZipFile(tmp) as z:
        media = [n for n in z.namelist() if "media/" in n]
        return len(media)


_, login = call("POST", "/ag-ui/user/login", {"username": "david", "password": "lingtong"})
token = login["token"]
stamp = uuid_hex()[:6]

try:
    # ── 建临时图库 + 一张带可控描述的图 ──
    st, lib = call("POST", "/ag-ui/image-libs", {"name": "严格度验证库-" + stamp, "description": "验证后即删"}, t=token)
    check("建临时图库", st == 200 and lib.get("libId") is not None, str(lib)[:200])
    lib_id = lib["libId"]

    body, boundary = upload_png("strictness-probe.png")
    att = post_multipart("/ag-ui/upload", body, boundary, token)["attachments"][0]["attachmentId"]
    # 描述写成长句（真实图库里也很常见）：英文查询与它零词面重叠、但语义相近，
    # 分数刚好落在中间带，能用来验证门槛开关。
    st, asset = call("POST", f"/ag-ui/image-libs/{lib_id}/assets",
                     {"attachmentId": att, "fileName": "严格度探针.png",
                      "caption": "深海紫色潜水艇在珊瑚礁间穿行，深蓝渐变海水与发光舷窗，科幻插画风格"}, t=token)
    check("登记探针图", st == 200, str(asset)[:200])
    asset_id = asset["assetId"]
    for _ in range(60):   # 等向量化完成（ready 才可检索）
        import time
        time.sleep(2)
        _, libs = call("GET", "/ag-ui/image-libs", t=token)
        cur = next((x for x in libs["libraries"] if x["libId"] == lib_id), None)
        a = next((x for x in (cur or {}).get("assets", []) if x["assetId"] == asset_id), None)
        if a and a.get("status") in ("ready", "error"):
            check("探针图已向量化", a["status"] == "ready", str(a.get("error")))
            break

    # ── 1) 先用低门槛量出真实分数（minScore=0.3 放开），再按它构造两个门槛 ──
    #     查询必须与描述**零词面重叠**（这里用英文查中文描述），否则 BM25 词面命中那条路会绕过门槛
    #     （那是刻意的：词都对上了是另一种信号，见 ImageLibrary.MinScore 注释），就测不出向量门槛了。
    print("\n== 1) 先量真实分数，再按它构造“更紧/更松”两个库门槛 ==")
    probes = [
        "submarine under the deep sea, science fiction illustration",
        "a long scene about a mysterious vessel among ocean reefs",
        "underwater vehicle artwork",
    ]
    scores = {}
    for q in probes:
        assert not any(ord(c) > 127 for c in q), "探针查询必须是纯 ASCII（与中文描述零词面重叠）"
        _, r = call("POST", "/ag-ui/images/search", {"query": q, "topK": 3, "minScore": 0.3}, t=token)
        hit = next((h for h in (r.get("images") or []) if h.get("assetId") == asset_id), None)
        scores[q] = hit["score"] if hit else None
        print(f"   {q}  ->  {scores[q]}")
    usable = [(q, s) for q, s in scores.items() if s and 0.40 <= s <= 0.85]
    check("有可用于验证的分数样本（0.40~0.85，两个门槛都在合法区间内）", bool(usable), f"scores={scores}")
    if not usable:
        raise SystemExit(1)
    q, s = usable[0]
    gate_tight = round(min(0.95, s + 0.06), 2)   # 比分数高 → 该被挡
    gate_loose = round(max(0.30, s - 0.06), 2)   # 比分数低 → 该放行
    print(f"   选用：\"{q}\"（分数 {s}）→ 紧门槛 {gate_tight} / 松门槛 {gate_loose}")

    # ── 2) 库严格度覆盖调用方传的 minScore（两个方向各一遍）──
    print("\n== 2) 库严格度覆盖调用方传的 minScore ==")
    call("PUT", f"/ag-ui/image-libs/{lib_id}", {"minScore": gate_tight}, t=token)
    _, r_tight = call("POST", "/ag-ui/images/search", {"query": q, "topK": 3, "minScore": 0.5}, t=token)
    call("PUT", f"/ag-ui/image-libs/{lib_id}", {"minScore": gate_loose}, t=token)
    _, r_loose = call("POST", "/ag-ui/images/search", {"query": q, "topK": 3, "minScore": 0.6}, t=token)
    hit_tight = any(h.get("assetId") == asset_id for h in (r_tight.get("images") or []))
    hit_loose = any(h.get("assetId") == asset_id for h in (r_loose.get("images") or []))
    print(f"   库={gate_tight}、调用方传 0.5 → 命中={hit_tight}（该为 False：库比分数紧）")
    print(f"   库={gate_loose}、调用方传 0.6 → 命中={hit_loose}（该为 True：库比分数松，且比调用方的 0.6 还松）")
    check("库收紧时能挡住调用方给的宽松值", not hit_tight)
    check("库放松时能越过调用方给的更严值（证明以库为准）", hit_loose)

    # ── 3) 技能链路也生效（严格方向）──
    print("\n== 3) 走 docx_report 技能链路（真实配图行为）==")
    call("PUT", f"/ag-ui/image-libs/{lib_id}", {"minScore": gate_tight}, t=token)
    d_strict = run_skill("docx_report", {
        "title": "严格度验证", "sections": [{"image": {"imageQuery": q}}]}, token)
    call("PUT", f"/ag-ui/image-libs/{lib_id}", {"minScore": gate_loose}, t=token)
    d_loose = run_skill("docx_report", {
        "title": "严格度验证", "sections": [{"image": {"imageQuery": q}}]}, token)
    strict_hit = bool(d_strict.get("images"))
    loose_hit = bool(d_loose.get("images"))
    med_strict = docx_media_bytes((d_strict.get("produce_file") or {})["path"]) if d_strict.get("produce_file") else 0
    med_loose = docx_media_bytes((d_loose.get("produce_file") or {})["path"]) if d_loose.get("produce_file") else 0
    print("   库收紧：images=", d_strict.get("images"), "| 文档内位图数=", med_strict)
    print("   库放松：images=", d_loose.get("images"), "| 文档内位图数=", med_loose)
    check("库收紧时配不上（无图 + 有 warning 说明）", (not strict_hit) and bool(d_strict.get("warnings")),
          str(d_strict.get("warnings"))[:200])
    check("库收紧时文档里确实没有位图", med_strict == 0)
    check("库放松时配上了（回显 + 文档里真有位图）", loose_hit and med_loose == 1, str(d_loose.get("images")))

    # ── 4) 只影响本库 ──
    print("\n== 4) 严格度只影响本库 ==")
    _, libs = call("GET", "/ag-ui/image-libs", t=token)
    others = [x for x in libs["libraries"] if x["libId"] != lib_id]
    changed = [x["name"] for x in others if x.get("minScore") not in (None, 0)]
    check("其它图库的严格度没被改动", not changed, str(changed))
finally:
    if lib_id:
        st, _ = call("DELETE", f"/ag-ui/image-libs/{lib_id}", t=token)
        print(f"\n清理：临时图库 {'已删除' if st == 200 else '删除失败 ' + str(st)}")

print("\n结果：" + ("全部通过" if ok else "存在失败项"))
sys.exit(0 if ok else 1)
