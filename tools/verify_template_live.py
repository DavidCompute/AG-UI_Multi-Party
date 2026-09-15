#!/usr/bin/env python3
"""实盘验证：读取既有 pptx、套模板出稿、原生可编辑图表（全部走真容器）。

重点验证**用户上传**这条链路：模型只拿得到附件 ID（att_xxx），技能要吃的是路径。
这里真的走一遍 POST /ag-ui/upload 拿到 att_xxx，再把它当作 path/template 传给技能，
确认平台那层解析生效、技能真的读到了文件。

用法：
  PYTHONIOENCODING=utf-8 python tools/verify_template_live.py
环境变量：AGUI_BASE（默认 http://127.0.0.1:5200）、AGUI_USER、AGUI_PWD、AGUI_CONTAINER
"""
import json
import os
import subprocess
import sys
import urllib.request
import uuid
import xml.etree.ElementTree as ET
import zipfile

BASE = os.environ.get("AGUI_BASE", "http://127.0.0.1:5200")
USER = os.environ.get("AGUI_USER", "david")
PWD = os.environ.get("AGUI_PWD", "lingtong")
CONTAINER = os.environ.get("AGUI_CONTAINER", "agui-group-chat-web")
SLIDE_W, SLIDE_H = 12192000, 6858000
TMP = os.environ.get("TEMP", "/tmp")


def _req(path, data=None, headers=None, method="POST", raw=False):
    req = urllib.request.Request(BASE + path, data=data, method=method)
    for k, v in (headers or {}).items():
        req.add_header(k, v)
    with urllib.request.urlopen(req, timeout=900) as r:
        body = r.read()
        return body if raw else json.loads(body.decode())


def login():
    return _req("/ag-ui/user/login", json.dumps({"username": USER, "password": PWD}).encode(),
                {"Content-Type": "application/json"})["token"]


def run_skill(skill_id, payload, token):
    r = _req("/ag-ui/skills/%s/run" % skill_id,
             json.dumps({"query": json.dumps(payload)}).encode(),
             {"Content-Type": "application/json", "Authorization": "Bearer " + token})
    return json.loads(r["result"]) if isinstance(r.get("result"), str) else r


def upload(local_path, token):
    """multipart/form-data，字段名 file（见 AttachmentApi）。"""
    boundary = "----agui" + uuid.uuid4().hex
    name = os.path.basename(local_path)
    with open(local_path, "rb") as f:
        blob = f.read()
    body = (
        ("--%s\r\n" % boundary).encode()
        + ('Content-Disposition: form-data; name="file"; filename="%s"\r\n' % name).encode()
        + b"Content-Type: application/octet-stream\r\n\r\n"
        + blob
        + ("\r\n--%s--\r\n" % boundary).encode()
    )
    r = _req("/ag-ui/upload", body,
             {"Content-Type": "multipart/form-data; boundary=" + boundary,
              "Authorization": "Bearer " + token})
    return r["attachments"][0]["attachmentId"]


def fetch(remote, local):
    try:
        subprocess.run(["docker", "cp", CONTAINER + ":" + remote, local], check=True, capture_output=True)
        return local
    except (FileNotFoundError, subprocess.CalledProcessError):
        return remote


def geometry_ok(local_path):
    ns = {"a": "http://schemas.openxmlformats.org/drawingml/2006/main"}
    with zipfile.ZipFile(local_path) as z:
        for n in sorted(x for x in z.namelist() if x.startswith("ppt/slides/slide") and x.endswith(".xml")):
            root = ET.fromstring(z.read(n))
            for xfrm in root.iter("{%s}xfrm" % ns["a"]):
                off, ext = xfrm.find("{%s}off" % ns["a"]), xfrm.find("{%s}ext" % ns["a"])
                if off is None or ext is None:
                    continue
                if (int(off.get("x")) + int(ext.get("cx")) > SLIDE_W + 1000
                        or int(off.get("y")) + int(ext.get("cy")) > SLIDE_H + 1000
                        or int(off.get("x")) < 0 or int(off.get("y")) < 0):
                    return False, "%s 有形状超出版面" % n
    return True, "形状全部在版面内"


def package_ok(local_path):
    p = subprocess.run([sys.executable, "tools/verify_office_package.py", local_path],
                       capture_output=True, text=True, encoding="utf-8", errors="replace")
    lines = [ln for ln in (p.stdout or "").strip().splitlines() if ln.strip()]
    return p.returncode == 0, (lines[-1] if lines else "（无输出）")


def main():
    token = login()
    print("登录成功：%s（%s）" % (USER, BASE))
    ok = True

    # ---- 1. 先造一份“用户模板” ----
    print("\n[1] 生成一份模板文件")
    made = run_skill("pptx_deck", {
        "title": "公司模板",
        "theme": "business-authority",
        "slides": [
            {"type": "cover", "title": "公司模板封面", "subtitle": "内部使用"},
            {"type": "content", "title": "模板原有页", "bullets": ["这页默认应该被清掉"]},
        ],
    }, token)
    if not made.get("ok"):
        print("   !! 生成失败：" + json.dumps(made, ensure_ascii=False)[:400])
        sys.exit(1)
    template_remote = made["produce_file"]["path"]
    print("   -> " + template_remote)

    # ---- 2. 走真实上传，拿到 att_xxx ----
    print("\n[2] 上传为附件，取回 att_xxx")
    local_template = fetch(template_remote, os.path.join(TMP, "e2e_template.pptx"))
    att = upload(local_template, token)
    print("   attachmentId=%s" % att)
    if not att.startswith("att_"):
        print("   !! 附件 ID 形态不对")
        ok = False

    # ---- 3. 用 att_xxx 读取 ----
    print("\n[3] action=read + path=att_xxx（验证平台把附件 ID 解析成了路径）")
    rd = run_skill("pptx_deck", {"action": "read", "path": att}, token)
    print("   ok=%s slides=%s" % (rd.get("ok"), rd.get("slides")))
    if not rd.get("ok"):
        print("   !! 读取失败：" + str(rd.get("message"))[:300])
        ok = False
    else:
        text = rd.get("text", "")
        for want in ("公司模板封面", "模板原有页"):
            if want not in text:
                print("   !! 读回来的文本里缺少 %s" % want)
                ok = False
        print("   读回文本片段：" + text.replace("\n", " | ")[:110])

    # ---- 4. 用 att_xxx 套模板出稿 ----
    print("\n[4] template=att_xxx（套模板出稿，不动原件）")
    out_remote = "/app/docs/e2e套模板产物.pptx"
    r2 = run_skill("pptx_deck", {
        "title": "套模板产物",
        "template": att,
        "outputPath": out_remote,
        "slides": [
            {"type": "cover", "title": "套模板产物", "subtitle": "沿用模板的皮"},
            {"type": "stats", "title": "关键数据", "cols": 3, "items": [
                {"value": "3.2×", "label": "交付提速"}, {"value": "98.9%", "label": "可用性"},
                {"value": "12", "label": "内置岗位"}]},
            {"type": "timeline", "title": "落地节奏", "items": [
                {"title": "调研", "detail": "梳理岗位"}, {"title": "试点", "detail": "单团队验证"},
                {"title": "推广", "detail": "全公司铺开"}]},
        ],
    }, token)
    print("   ok=%s slides=%s palette.primary=%s"
          % (r2.get("ok"), r2.get("slides"), (r2.get("palette") or {}).get("primary")))
    if not r2.get("ok"):
        print("   !! 套模板失败：" + str(r2.get("message"))[:400])
        ok = False
    else:
        local_out = fetch(out_remote, os.path.join(TMP, "e2e_template_out.pptx"))
        g_ok, g_msg = geometry_ok(local_out)
        p_ok, p_msg = package_ok(local_out)
        print("   版面：" + g_msg)
        print("   结构：" + p_msg)
        if not (g_ok and p_ok):
            ok = False
        with zipfile.ZipFile(local_out) as z:
            names = z.namelist()
            masters = [n for n in names if n.startswith("ppt/slideMasters/slideMaster") and n.endswith(".xml")]
            slides = [n for n in names if n.startswith("ppt/slides/slide") and n.endswith(".xml")]
            themes = [n for n in names if "/theme" in n and n.endswith(".xml")]
            joined = "\n".join(z.read(n).decode("utf-8", "replace") for n in slides)
        print("   母版=%d 主题=%d 幻灯片=%d" % (len(masters), len(themes), len(slides)))
        if len(masters) != 1 or len(themes) != 1 or len(slides) != 3:
            print("   !! 期望 1 母版 / 1 主题 / 3 页（+1 封面）")
            ok = False
        if "这页默认应该被清掉" in joined:
            print("   !! 模板原有页没有被清掉")
            ok = False

    # ---- 5. 原生可编辑图表 ----
    print("\n[5] 原生可编辑图表（chartType=bar-native）")
    r3 = run_skill("pptx_deck", {
        "title": "原生图表检查",
        "slides": [
            {"type": "cover", "title": "原生图表检查"},
            {"type": "chart", "title": "活跃团队", "chartType": "bar-native",
             "categories": ["Q1", "Q2", "Q3"],
             "series": [{"name": "团队数", "values": [120.0, 260.0, 430.0]}]},
        ],
    }, token)
    print("   nativeCharts=%s fallback=%s" % (r3.get("nativeCharts"), r3.get("nativeChartFallback")))
    if r3.get("nativeCharts") != 1:
        print("   !! 期望生成 1 张原生图表")
        ok = False
    else:
        local_native = fetch(r3["produce_file"]["path"], os.path.join(TMP, "e2e_native.pptx"))
        with zipfile.ZipFile(local_native) as z:
            charts = [n for n in z.namelist() if "/charts/chart" in n and n.endswith(".xml")]
            embeds = [n for n in z.namelist() if "/charts/embeddings/" in n]
            chart_xml = z.read(charts[0]).decode("utf-8", "replace") if charts else ""
        print("   chart 部件=%d 嵌入工作簿=%d" % (len(charts), len(embeds)))
        if len(charts) != 1 or len(embeds) != 1:
            print("   !! 期望 1 个 chart 部件 + 1 个嵌入工作簿")
            ok = False
        if "430" not in chart_xml:
            print("   !! ChartSpace 缓存值里没有最后一个数值")
            ok = False
        p_ok, p_msg = package_ok(local_native)
        print("   结构：" + p_msg)
        if not p_ok:
            ok = False

    print("\n==== %s ====" % ("全部通过" if ok else "存在失败项"))
    sys.exit(0 if ok else 1)


main()
