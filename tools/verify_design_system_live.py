#!/usr/bin/env python3
"""实盘验证：ppt Deck 的设计系统（18 套调色板 + 4 种 style + 新页型）在真容器里表现正常。

不与 verify_chart_geometry.py 合并：那个只管图表像素，这个管版式与配色，
两者失败的处置完全不同（一个是渲染器几何、一个是设计系统映射）。

检查项：
  1) 用命名调色板 + 非默认 style 渲染一份含全部新页型的 deck，能落盘；
  2) 返回 JSON 回显的 palette/style 与请求一致；
  3) 产物通过 OOXML 结构与必备关系检查（tools/verify_office_package.py）；
  4) 每一页上的形状都没跑到画布外（取所有 <a:off>/<a:ext> 求右/下边界）。

用法：
  PYTHONIOENCODING=utf-8 python tools/verify_design_system_live.py
环境变量：AGUI_BASE（默认 http://127.0.0.1:5200）、AGUI_USER、AGUI_PWD、AGUI_CONTAINER
"""
import json
import os
import re
import subprocess
import sys
import urllib.request
import xml.etree.ElementTree as ET
import zipfile

BASE = os.environ.get("AGUI_BASE", "http://127.0.0.1:5200")
USER = os.environ.get("AGUI_USER", "david")
PWD = os.environ.get("AGUI_PWD", "lingtong")
CONTAINER = os.environ.get("AGUI_CONTAINER", "agui-group-chat-web")

SLIDE_W, SLIDE_H = 12192000, 6858000


def post(path, body, token=None):
    req = urllib.request.Request(BASE + path, data=json.dumps(body).encode(), method="POST")
    req.add_header("Content-Type", "application/json")
    if token:
        req.add_header("Authorization", "Bearer " + token)
    with urllib.request.urlopen(req, timeout=900) as r:
        return json.loads(r.read().decode())


def fetch(remote, local):
    try:
        subprocess.run(["docker", "cp", CONTAINER + ":" + remote, local], check=True, capture_output=True)
        return local
    except (FileNotFoundError, subprocess.CalledProcessError):
        return remote


def deck(palette, style):
    return {
        "title": "设计系统实盘校验",
        "theme": palette,
        "style": style,
        "slides": [
            {"type": "cover", "title": "设计系统实盘校验", "subtitle": palette + " + " + style,
             "author": "质量校验", "date": "2026-09"},
            {"type": "section", "title": "一、版式与配色", "subtitle": "换调色板与风格不该影响结构"},
            {"type": "stats", "title": "关键数据", "cols": 3, "items": [
                {"value": "3.2×", "label": "交付提速"},
                {"value": "98.9%", "label": "可用性"},
                {"value": "12", "label": "内置岗位"}]},
            {"type": "grid", "title": "能力矩阵", "cols": 2, "items": [
                {"title": "协作", "text": "多角色同场会商"},
                {"title": "记忆", "text": "RAG 长期记忆并可治理"},
                {"title": "交付", "text": "直接产出可下载文件"},
                {"title": "治理", "text": "权限与审计闭环"}]},
            {"type": "timeline", "title": "落地节奏", "items": [
                {"title": "调研", "detail": "梳理岗位与流程"},
                {"title": "试点", "detail": "单团队验证"},
                {"title": "推广", "detail": "全公司铺开"},
                {"title": "沉淀", "detail": "固化为组织能力"}]},
            {"type": "iconRows", "title": "核心价值", "items": [
                {"icon": "1", "title": "更省事", "text": "一句话产出成品文件"},
                {"icon": "2", "title": "更懂行", "text": "记忆沉淀行业经验"}]},
            {"type": "content", "title": "子类型别名", "layout": "grid", "items": [
                {"title": "layout=grid", "text": "content 可直接指定子类型"},
                {"title": "少记几个 type", "text": "降低模型选错页型的概率"}]},
            {"type": "table", "title": "选型对照",
             "headers": ["维度", "私有化", "SaaS"],
             "rows": [["数据位置", "内网", "云端"], ["运维成本", "较高", "低"]]},
            {"type": "chart", "title": "增长趋势", "chartType": "pie",
             "categories": ["国内", "海外", "其他"],
             "series": [{"name": "占比", "values": [55.0, 35.0, 10.0]}]},
            {"type": "summary", "title": "小结", "bullets": ["配色：命名调色板", "版式：style 控制留白与圆角"]},
            {"type": "end", "title": "谢谢", "subtitle": "欢迎提问"},
        ],
    }


def check_geometry(local_path, label):
    """取每一页所有形状的右/下边界，断言不越出画布。"""
    ns = {"a": "http://schemas.openxmlformats.org/drawingml/2006/main",
          "p": "http://schemas.openxmlformats.org/presentationml/2006/main"}
    worst = []
    with zipfile.ZipFile(local_path) as z:
        names = sorted(n for n in z.namelist()
                       if re.fullmatch(r"ppt/slides/slide\d+\.xml", n))
        for n in names:
            root = ET.fromstring(z.read(n))
            for xfrm in root.iter("{%s}xfrm" % ns["a"]):
                off = xfrm.find("{%s}off" % ns["a"])
                ext = xfrm.find("{%s}ext" % ns["a"])
                if off is None or ext is None:
                    continue
                x, y = int(off.get("x")), int(off.get("y"))
                cx, cy = int(ext.get("cx")), int(ext.get("cy"))
                # 封面/结束页/章节页刻意用整页色块，这里只看是否超出画布
                if x < 0 or y < 0 or x + cx > SLIDE_W + 1000 or y + cy > SLIDE_H + 1000:
                    worst.append("%s x=%d y=%d cx=%d cy=%d -> 右=%d 下=%d"
                                 % (n, x, y, cx, cy, x + cx, y + cy))
    if worst:
        print("   !! %s 有形状超出版面：" % label)
        for w in worst[:8]:
            print("      " + w)
        return False
    print("   %s：%d 页形状全部在版面内" % (label, len(names)))
    return True


def main():
    token = post("/ag-ui/user/login", {"username": USER, "password": PWD})["token"]
    print("登录成功：%s（%s）" % (USER, BASE))

    # 挑两套气质差距大的组合：浅底 + 大留白、深底 + 紧凑
    cases = [("education-charts", "soft"), ("tech-night", "pill"), ("forest-eco", "sharp")]
    ok = True
    for palette, style in cases:
        print("\n[%s + %s]" % (palette, style))
        r = post("/ag-ui/skills/pptx_deck/run", {"query": json.dumps(deck(palette, style))}, token)
        inner = json.loads(r["result"]) if isinstance(r.get("result"), str) else r
        if not inner.get("ok"):
            print("   !! 执行失败：" + json.dumps(inner, ensure_ascii=False)[:800])
            ok = False
            continue
        path = inner["produce_file"]["path"]
        print("   -> %s" % path)
        if inner.get("style") != style:
            print("   !! style 回显不一致：%s vs %s" % (inner.get("style"), style))
            ok = False
        pal = inner.get("palette") or {}
        print("   palette: primary=%s accent=%s bg=%s text=%s onAccent=%s"
              % (pal.get("primary"), pal.get("accent"), pal.get("bg"), pal.get("text"), pal.get("onAccent")))
        if pal.get("bg") is None:
            print("   !! 返回里没有 palette")
            ok = False

        local = fetch(path, os.path.join(os.environ.get("TEMP", "/tmp"),
                                        "e2e_deck_%s_%s.pptx" % (palette, style)))
        if not check_geometry(local, palette + "+" + style):
            ok = False
        pkg = subprocess.run([sys.executable, "tools/verify_office_package.py", local],
                             capture_output=True, text=True, encoding="utf-8", errors="replace")
        last = [ln for ln in (pkg.stdout or "").strip().splitlines() if ln.strip()]
        print("   结构检查：" + (last[-1] if last else pkg.stdout.strip()))
        if pkg.returncode != 0:
            ok = False

    print("\n==== %s ====" % ("全部通过" if ok else "存在失败项"))
    sys.exit(0 if ok else 1)


main()
