#!/usr/bin/env python3
"""把 .pptx / .docx 渲成逐页 PNG，供「肉眼版式校验」与自动检查。

为什么要有这个工具：我们自己的生成器与自检（pptx_deck 的 qa）只能验证**我们写出的 XML**，
验不了“别的渲染器看到的是什么”。渲染走 LibreOffice 就相当于请了**第二个独立的裁判**：

  * 文字溢出文本框 —— 我们按字号×字数估算高度，估错了自己看不出来，渲染出来就露馅；
  * 字体/字形缺失 —— 容器里没装某个字体时，渲染图会变成方块/空白；
  * 版本兼容性 —— 包结构/XML 有问题时 LibreOffice 直接打不开或报错。

所以除了给人看图，它还提供 `--check` 自动检查：
  1) 每页都渲染出来了（PDF 页数 == 期望页数）；
  2) 每页都有内容（不是空白页）；
  3) 内容没画到画布外（用 pdftoppm 出的像素求墨迹包围盒，留边阈值可调）；
  4) 用 pdftotext 抽出文本，断言指定的关键词**真的渲染出来了**（缺字体会直接失败）。

前置条件：容器里装了 LibreOffice + poppler（`AGUI_PREVIEW_TOOLS=true` 重建镜像）。
本机若已装 soffice + pdftoppm，则自动走本机、不需要容器。

用法：
  # 渲一份样张（内置“全部页型与变体”的 deck，先生成再渲染）——做版式回归时最常用
  python tools/preview-pptx.py --sample

  # 渲任意产物
  python tools/preview-pptx.py /path/to/xx.pptx
  python tools/preview-pptx.py xx.pptx --pages 1-6 --dpi 130

  # 自动检查（不靠眼睛）：页数 / 空白页 / 越界 / 关键词是否渲染出来
  python tools/preview-pptx.py --sample --check --expect "封面标题,进度"

输出：
  tools/.preview/<name>/*.png   （零散产物；已在 .gitignore 之外，用完可删）
  tools/.preview/<name>/report.txt

环境变量：AGUI_CONTAINER（默认 agui-group-chat-web）、AGUI_DPI（默认 110）
"""
import argparse
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import zipfile

CONTAINER = os.environ.get("AGUI_CONTAINER", "agui-group-chat-web")
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PREVIEW_DIR = os.path.join(ROOT, "tools", ".preview")
RUN_SKILL = os.path.join(ROOT, "tools", "run-skill.py")
SKILL = os.path.join(ROOT, "tools", "pptx-skills", "pptx_deck.cs")

try:
    sys.stdout.reconfigure(encoding="utf-8")
except Exception:
    pass


# ---------------------------------------------------------------- 样张
def sample_deck_json() -> str:
    """内置样张：覆盖**全部页型与全部 variant**，用于一眼看出版式回归。"""
    img_note = "（--sample 会在生成前把一张小图写进临时目录供图文页使用）"
    return json.dumps({
        "title": "版式样张",
        "theme": "pure-tech-blue",
        "style": "soft",
        "slides": [
            {"type": "cover", "title": "版式样张：封面 left", "subtitle": "variant=left（默认）", "author": "质量校验", "date": "2026-09"},
            {"type": "cover", "title": "封面 center", "subtitle": "居中、留白最大", "variant": "center"},
            {"type": "cover", "title": "封面 split", "subtitle": "左文右图", "variant": "split", "path": "__IMG__"},
            {"type": "cover", "title": "封面 image", "subtitle": "整页背景图 + 蒙层", "variant": "image", "path": "__IMG__"},

            {"type": "toc", "title": "目录 list", "variant": "list",
             "items": ["一、产品概览", "二、核心能力", "三、交付流程", "四、总结展望"]},
            {"type": "toc", "title": "目录 grid", "variant": "grid",
             "items": ["一、产品概览", "二、核心能力", "三、交付流程", "四、总结展望"]},
            {"type": "toc", "title": "目录 sidebar", "variant": "sidebar",
             "items": ["一、产品概览", "二、核心能力", "三、交付流程"]},

            {"type": "section", "title": "章节 number", "subtitle": "满页色 + 大序号", "variant": "number"},
            {"type": "section", "title": "章节 bar", "subtitle": "左侧色块", "variant": "bar"},
            {"type": "section", "title": "章节 full", "subtitle": "水印序号", "variant": "full"},

            {"type": "content", "title": "要点页（小标题：说明 两行排版）",
             "bullets": ["协作：多角色同场会商", "记忆：RAG 长期记忆并可治理", "交付：直接产出可下载文件"]},
            {"type": "twoCol", "title": "两栏对比",
             "left": {"heading": "传统单聊助手", "bullets": ["单人单会话", "无组织概念"]},
             "right": {"heading": "知聚", "bullets": ["多人多角色协作", "直接交付文件"]}},
            {"type": "table", "title": "表格", "headers": ["维度", "私有化", "SaaS"],
             "rows": [["数据位置", "内网", "云端"], ["运维成本", "较高", "低"], ["定制能力", "强", "弱"]]},
            {"type": "kpi", "title": "指标卡", "items": [
                {"value": "12", "label": "内置岗位"}, {"value": "98.9%", "label": "可用性"},
                {"value": "< 2s", "label": "首字延迟"}]},
            {"type": "stats", "title": "大数字看板", "cols": 3, "items": [
                {"value": "3.2×", "label": "交付提速"}, {"value": "25", "label": "角色包"},
                {"value": "4", "label": "交付技能"}]},
            {"type": "grid", "title": "网格卡片", "cols": 2, "items": [
                {"title": "协作", "text": "多角色同场会商"}, {"title": "记忆", "text": "长期可治理"},
                {"title": "交付", "text": "直接出可下载文件"}, {"title": "治理", "text": "权限与审计闭环"}]},
            {"type": "timeline", "title": "时间轴", "items": [
                {"title": "调研", "detail": "梳理岗位与流程"}, {"title": "试点", "detail": "单团队验证"},
                {"title": "推广", "detail": "全公司铺开"}, {"title": "沉淀", "detail": "固化为组织能力"}]},
            {"type": "iconRows", "title": "图标行（新图标）", "items": [
                {"icon": "rocket", "title": "上线", "text": "快速交付"},
                {"icon": "shield", "title": "安全", "text": "企业级防护"},
                {"icon": "gauge", "title": "指标", "text": "可观测"},
                {"icon": "users", "title": "团队", "text": "多角色协作"}]},
            {"type": "quote", "text": "让组织知道什么、记得什么，比单个模型有多强更重要。", "cite": "产品原则"},
            {"type": "progress", "title": "进度条（variant=bar）", "items": [
                {"label": "需求确认", "value": 100}, {"label": "开发", "value": 72},
                {"label": "测试", "value": 35}]},
            {"type": "progress", "title": "环形仪表（variant=ring）", "variant": "ring", "items": [
                {"label": "覆盖率", "value": 86}, {"label": "可用率", "value": 62},
                {"label": "满意度", "value": 94}]},
            {"type": "chart", "title": "柱状图", "chartType": "bar", "categories": ["Q1", "Q2", "Q3", "Q4"],
             "series": [{"name": "活跃团队", "values": [120, 260, 430, 610]}], "yLabel": "个"},
            {"type": "chart", "title": "散点图", "chartType": "scatter", "xLabel": "投入(人日)", "yLabel": "收益",
             "series": [{"name": "试点", "points": [[1, 2], [2, 3.5], [3, 4], [5, 7]]},
                        {"name": "对照", "points": [[1, 1.2], [3, 2.2], [5, 3.1]]}]},
            {"type": "chart", "title": "雷达图", "chartType": "radar",
             "categories": ["协作", "记忆", "交付", "安全", "生态"],
             "series": [{"name": "知聚", "values": [9, 8, 9, 7, 8]},
                        {"name": "基座", "values": [6, 5, 4, 7, 6]}]},

            {"type": "image", "title": "配图 full", "variant": "full", "path": "__IMG__", "caption": "整块图居中"},
            {"type": "image", "title": "图文 left", "variant": "left", "path": "__IMG__",
             "heading": "图左文右", "bullets": ["文字在右侧一栏", "图片按框裁切"]},
            {"type": "image", "title": "图文 right", "variant": "right", "path": "__IMG__",
             "bullets": ["图片在右侧", "文字在左侧"]},
            {"type": "image", "title": "半出血 bleed", "variant": "bleed", "path": "__IMG__",
             "bullets": ["右半页铺满整高图", "文字叠在主色底上"]},
            {"type": "image", "title": "图廊 gallery", "variant": "gallery",
             "images": [{"path": "__IMG__", "caption": "第一张"}, {"path": "__IMG__", "caption": "第二张"}]},

            {"type": "summary", "title": "小结 list", "variant": "list",
             "bullets": ["配色：命名调色板", "版式：style 控制留白与圆角"]},
            {"type": "summary", "title": "小结 cta", "variant": "cta",
             "items": ["确认试点范围", "排期联调", "上线评估"], "contact": "team@example.com"},
            {"type": "summary", "title": "小结 split", "variant": "split",
             "bullets": ["协作：多角色会商", "记忆：长期可治理"],
             "actions": ["确认试点", "排期联调"], "contact": "team@example.com"},

            {"type": "end", "title": "谢谢", "subtitle": "欢迎提问"},
        ],
    }, ensure_ascii=False)


TINY_PNG_B64 = ("iVBORw0KGgoAAAANSUhEUgAAABAAAAAQCAIAAACQkWg2AAAAL0lEQVR4nGM8YWPDQApgIkk1AxkaWOCsMJsePOpWHSmhl5OYRjUQAUgOJcbBl/gA3ngFoWwd6YkAAAAASUVORK5CYII=")


# ---------------------------------------------------------------- 生成 / 渲染
def build_sample(workdir: str) -> str:
    """用 run-skill.py 在本地生成样张（不依赖容器），返回 pptx 路径。"""
    img = os.path.join(workdir, "sample.png")
    with open(img, "wb") as f:
        import base64
        f.write(base64.b64decode(TINY_PNG_B64))
    deck = sample_deck_json().replace("__IMG__", img.replace("\\", "/"))
    req = os.path.join(workdir, "sample-deck.json")
    with open(req, "w", encoding="utf-8", newline="") as f:
        f.write(deck)
    out = os.path.join(workdir, "out")
    os.makedirs(out, exist_ok=True)
    # 指定落盘目录：否则技能会写到 <用户主目录>/agui-pptx，样张会在那里越积越多
    # （而且同名时技能会自动加 _2/_3 后缀，输出目录名也跟着变）
    env = dict(os.environ, AGUI_PPTX_OUT=out)
    proc = subprocess.run([sys.executable, RUN_SKILL, SKILL, "--file", req, "--out", out],
                          capture_output=True, text=True, encoding="utf-8", errors="replace",
                          cwd=ROOT, env=env)
    if proc.returncode not in (0, 1):
        print("生成样张失败：")
        print(proc.stdout[-3000:] if proc.stdout else proc.stderr[-3000:])
        sys.exit(2)
    ppts = [os.path.join(out, f) for f in os.listdir(out) if f.endswith(".pptx")]
    if not ppts:
        print("生成样张失败：没拿到 .pptx\n" + (proc.stdout or "")[-2000:])
        sys.exit(2)
    return ppts[0]


def run(cmd, **kw):
    return subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8",
                          errors="replace", **kw)


def have_local_tools() -> bool:
    return bool(shutil.which("soffice")) and bool(shutil.which("pdftoppm"))


def render_with_container(pptx: str, outdir: str, dpi: int) -> bool:
    """把文件塞进容器 → soffice 转 PDF → pdftoppm 出 PNG → 拷回来。"""
    probe = run(["docker", "exec", CONTAINER, "bash", "-lc", "command -v soffice && command -v pdftoppm"])
    if probe.returncode != 0:
        return False
    tmp = "/tmp/agui-preview-" + str(os.getpid())
    name = os.path.basename(pptx)
    ext = os.path.splitext(name)[1] or ".pptx"
    run(["docker", "exec", "-u", "0", CONTAINER, "bash", "-lc", f"rm -rf {tmp} && mkdir -p {tmp} && chmod 777 {tmp}"])
    if run(["docker", "cp", pptx, f"{CONTAINER}:{tmp}/in{ext}"]).returncode != 0:
        print("docker cp 失败（文件是否在工作区内？容器路径不能含盘符）")
        return False
    conv = run(["docker", "exec", CONTAINER, "bash", "-lc",
                f"cd {tmp} && soffice --headless --norestore -env:UserInstallation=file://{tmp}/profile "
                f"--convert-to pdf --outdir {tmp} in{ext} >/dev/null 2>&1 "
                f"&& pdftoppm -png -r {dpi} in.pdf page "
                f"&& pdftotext -layout in.pdf text.txt "
                # 逐页文本：--pages 过滤后做关键词检查时不能拿整份文本去比
                f"&& n=$(pdfinfo in.pdf | awk '/^Pages:/{{print $2}}') "
                f"&& for i in $(seq 1 $n); do pdftotext -f $i -l $i in.pdf page-$i.txt; done "
                f"&& echo $n"])
    pages = (conv.stdout or "").strip().splitlines()[-1] if conv.returncode == 0 else ""
    if conv.returncode != 0 or not pages.isdigit() or int(pages) == 0:
        print("容器内转换失败：")
        print((conv.stdout or "") + (conv.stderr or ""))
        return False
    os.makedirs(outdir, exist_ok=True)
    listing = run(["docker", "exec", CONTAINER, "bash", "-lc",
                   f"ls {tmp}/page*.png {tmp}/page-*.txt {tmp}/text.txt {tmp}/in.pdf"]).stdout.split()
    for remote in listing:
        run(["docker", "cp", f"{CONTAINER}:{remote}", os.path.join(outdir, os.path.basename(remote))])
    run(["docker", "exec", "-u", "0", CONTAINER, "bash", "-lc", f"rm -rf {tmp}"])
    return True


def render_local(pptx: str, outdir: str, dpi: int) -> bool:
    import tempfile as _tf
    with _tf.TemporaryDirectory() as t:
        conv = run(["soffice", "--headless", "--norestore",
                    f"-env:UserInstallation=file://{t}/profile",
                    "--convert-to", "pdf", "--outdir", t, pptx])
        pdfs = [f for f in os.listdir(t) if f.endswith(".pdf")]
        if not pdfs:
            print("本机转换失败：" + (conv.stdout or "") + (conv.stderr or ""))
            return False
        pdf = os.path.join(t, pdfs[0])
        os.makedirs(outdir, exist_ok=True)
        run(["pdftoppm", "-png", "-r", str(dpi), pdf, os.path.join(outdir, "page")])
        shutil.copy(pdf, os.path.join(outdir, "in.pdf"))
        txt = run(["pdftotext", "-layout", pdf, "-"])
        with open(os.path.join(outdir, "text.txt"), "w", encoding="utf-8", newline="") as f:
            f.write(txt.stdout or "")
        npages = len([f for f in os.listdir(outdir) if re.fullmatch(r"page-?\d+\.png", f)])
        for i in range(1, npages + 1):
            one = run(["pdftotext", "-f", str(i), "-l", str(i), pdf, "-"])
            with open(os.path.join(outdir, f"page-{i}.txt"), "w", encoding="utf-8", newline="") as f:
                f.write(one.stdout or "")
    return True


# ---------------------------------------------------------------- 自动检查
def png_ink_analysis(path: str):
    """返回 (w, h, 墨迹包围盒, 墨迹占比)。

    不依赖 Pillow：PNG 解码用 zlib 手搓（只处理我们渲出的 RGB8/RGBA8）。

    “墨迹”不能简单定义为“不是白色”——页面底色常常是**浅彩色**（如 CAF0F8），
    那样整页都会被当成墨迹，检查就失去意义。这里先取**全图出现最多的颜色**当底色
    （幻灯片里底色占比最高），再看哪些像素明显不同。
    """
    import zlib
    raw = open(path, "rb").read()
    pos, w, h, ct = 8, 0, 0, 0
    idat = b""
    while pos + 8 <= len(raw):
        ln = int.from_bytes(raw[pos:pos + 4], "big")
        typ = raw[pos + 4:pos + 8]
        data = raw[pos + 8:pos + 8 + ln]
        if typ == b"IHDR":
            w, h = int.from_bytes(data[0:4], "big"), int.from_bytes(data[4:8], "big")
            ct = data[9]
        elif typ == b"IDAT":
            idat += data
        elif typ == b"IEND":
            break
        pos += 12 + ln
    ch = {0: 1, 2: 3, 4: 2, 6: 4}[ct]
    buf = zlib.decompress(idat)
    stride = w * ch
    prev = bytearray(stride)
    # 第一遍：解滤波 + 颜色直方图（每通道降 4 位），找出底色
    rows = []
    hist = {}
    p = 0
    for _ in range(h):
        f = buf[p]; p += 1
        cur = bytearray(buf[p:p + stride]); p += stride
        if f == 1:
            for i in range(ch, stride):
                cur[i] = (cur[i] + cur[i - ch]) & 0xFF
        elif f == 2:
            for i in range(stride):
                cur[i] = (cur[i] + prev[i]) & 0xFF
        elif f == 3:
            for i in range(stride):
                a = cur[i - ch] if i >= ch else 0
                cur[i] = (cur[i] + ((a + prev[i]) >> 1)) & 0xFF
        elif f == 4:
            for i in range(stride):
                a = cur[i - ch] if i >= ch else 0
                b = prev[i]
                c = prev[i - ch] if i >= ch else 0
                pa, pb, pc = abs(b - c), abs(a - c), abs(a + b - 2 * c)
                pr = a if (pa <= pb and pa <= pc) else (b if pb <= pc else c)
                cur[i] = (cur[i] + pr) & 0xFF
        rows.append(bytes(cur))
        prev = cur
        for x in range(0, stride, ch):
            key = (cur[x] >> 4) << 8 | (cur[x + 1] >> 4) << 4 | (cur[x + 2] >> 4)
            hist[key] = hist.get(key, 0) + 1

    bg = max(hist.items(), key=lambda kv: kv[1])[0]
    br, bgc, bb = (bg >> 8 & 0xF) << 4, (bg >> 4 & 0xF) << 4, (bg & 0xF) << 4

    # 第二遍：明显不同于底色的像素算墨迹（阈值 48/通道，避开抗锯齿与轻微渐变）
    minx, miny, maxx, maxy, ink = w, h, -1, -1, 0
    for y, cur in enumerate(rows):
        for x in range(w):
            o = x * ch
            if ct == 6 and cur[o + 3] < 40:
                continue
            r, g, b = cur[o], cur[o + 1], cur[o + 2]
            if abs(r - br) < 48 and abs(g - bgc) < 48 and abs(b - bb) < 48:
                continue
            ink += 1
            if x < minx: minx = x
            if x > maxx: maxx = x
            if y < miny: miny = y
            if y > maxy: maxy = y
    return w, h, (minx, miny, maxx, maxy), ink / float(w * h)


def check(all_pngs, text_path, expect, min_margin_ratio, expect_pages=None, check_margins=False):
    """返回 (问题列表, 报告行列表)。"""
    problems, lines = [], []
    lines.append(f"渲染页数：{len(all_pngs)}")
    if expect_pages is not None and len(all_pngs) != expect_pages:
        problems.append(f"页数不符：渲染出 {len(all_pngs)} 页，期望 {expect_pages} 页")
    for path in all_pngs:
        w, h, (minx, miny, maxx, maxy), ratio = png_ink_analysis(path)
        name = os.path.basename(path)
        if maxx < 0:
            problems.append(f"{name}：整页空白（没渲染出任何内容）")
            lines.append(f"  {name}: 空白")
            continue
        margins = (minx, miny, w - 1 - maxx, h - 1 - maxy)
        # 满版底色/背景图页（封面 image/split、深色主题、带整页图的页）天然会贴到四条边，
        # 这不是溢出——若不排除，每份稿子的封面都会报错，把真问题淹没。
        full_bleed = ratio > 0.85 or (minx <= 1 and miny <= 1 and margins[2] <= 1 and margins[3] <= 1)
        note = "满版底色/背景图（跳过贴边检查）" if full_bleed else \
            f"墨迹 L{margins[0]} T{margins[1]} R{margins[2]} B{margins[3]}"
        lines.append(f"  {name}: {w}x{h} 墨迹占比 {ratio:.1%} {note}")
        if not full_bleed and check_margins:
            need = max(3, int(min(w, h) * min_margin_ratio))
            for side, val in zip(("左", "上", "右", "下"), margins):
                if val < need:
                    problems.append(f"{name}：内容贴到{side}边（仅留 {val}px < {need}px），可能溢出页面")
    if expect:
        # 只拿**选中那几页**的文本做关键词检查（--pages 过滤后，整份文本会让人误以为验过了）
        chunks = []
        for p in all_pngs:
            n = re.findall(r"\d+", os.path.basename(p))
            if not n:
                continue
            side = os.path.join(os.path.dirname(p), f"page-{int(n[0])}.txt")
            if os.path.exists(side):
                chunks.append(open(side, encoding="utf-8", errors="replace").read())
        text = "\n".join(chunks)
        if not text and os.path.exists(text_path):
            text = open(text_path, encoding="utf-8", errors="replace").read()
        for kw in [k for k in expect.split(",") if k.strip()]:
            if kw.strip() in text:
                lines.append(f"  文本检查（仅选中页）：命中「{kw.strip()}」")
            else:
                problems.append(f"文本检查：渲染结果里找不到「{kw.strip()}」——可能是没渲染出来、字体缺失或内容被裁掉")
    return problems, lines


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("pptx", nargs="?", help="要渲染的 pptx（省略则需 --sample）")
    ap.add_argument("--sample", action="store_true", help="先本地生成“全部页型与变体”样张再渲染")
    ap.add_argument("--out", default=None, help="PNG 输出目录（默认 tools/.preview/<名字>）")
    ap.add_argument("--dpi", type=int, default=int(os.environ.get("AGUI_DPI", "110")))
    ap.add_argument("--pages", help="只渲染这几页，如 1-6,9（渲染后按文件名过滤）")
    ap.add_argument("--check", action="store_true", help="自动检查：空白页 / 页数 / 关键词是否渲染出来")
    ap.add_argument("--expect", default="", help="期望在渲染文本里出现的关键词，逗号分隔")
    ap.add_argument("--expect-pages", type=int, default=None)
    ap.add_argument("--margins", action="store_true",
                    help="额外检查“内容是否贴边/溢出”（满版底色与背景图页会有误报，故默认不开）")
    ap.add_argument("--margin-ratio", type=float, default=0.004, help="允许的最小留边（占画布比例）")
    ap.add_argument("--keep", action="store_true", help="保留上次输出（默认清理同名目录）")
    args = ap.parse_args()

    if not args.pptx and not args.sample:
        ap.error("要么给一个 pptx 路径，要么用 --sample")

    work = tempfile.mkdtemp(prefix="agui-preview-")
    try:
        if args.sample:
            if not os.path.exists(RUN_SKILL):
                print("找不到 tools/run-skill.py（--sample 需要它先生成样张）")
                return 2
            pptx = build_sample(work)
            print(f"样张已生成：{pptx}")
        else:
            pptx = os.path.abspath(args.pptx)
            if not os.path.exists(pptx):
                print("找不到文件：" + pptx)
                return 2

        name = os.path.splitext(os.path.basename(pptx))[0]
        outdir = args.out or os.path.join(PREVIEW_DIR, name)
        if os.path.isdir(outdir) and not args.keep:
            shutil.rmtree(outdir)
        os.makedirs(outdir, exist_ok=True)

        if have_local_tools():
            print(f"用本机 LibreOffice 渲染（{args.dpi} dpi）→ {outdir}")
            ok = render_local(pptx, outdir, args.dpi)
        else:
            print(f"用容器 {CONTAINER} 渲染（{args.dpi} dpi）→ {outdir}")
            ok = render_with_container(pptx, outdir, args.dpi)
        if not ok:
            print("\n渲染失败。容器里没装 LibreOffice / poppler 时：\n"
                  "  在 .env 里设 AGUI_PREVIEW_TOOLS=true 后 `docker compose build web && docker compose up -d web`")
            return 2

        pngs = sorted([os.path.join(outdir, f) for f in os.listdir(outdir)
                       if re.fullmatch(r"page-?\d+\.png", f)],
                      key=lambda p: int(re.findall(r"\d+", os.path.basename(p))[0]))
        if args.pages:
            keep = set()
            for part in args.pages.split(","):
                if "-" in part:
                    a, b = part.split("-")
                    keep.update(range(int(a), int(b) + 1))
                else:
                    keep.add(int(part))
            pngs = [p for p in pngs if int(re.findall(r"\d+", os.path.basename(p))[0]) in keep]

        print(f"\n{len(pngs)} 张 PNG：")
        for p in pngs:
            print("  " + p)

        lines = []
        if args.check:
            problems, lines = check(pngs, os.path.join(outdir, "text.txt"),
                                    args.expect, args.margin_ratio, args.expect_pages,
                                    check_margins=args.margins)
            report = os.path.join(outdir, "report.txt")
            with open(report, "w", encoding="utf-8", newline="") as f:
                f.write("\n".join(lines + [""] + ["问题："] + problems) + "\n")
            print("\n检查结果：")
            print("\n".join(lines))
            if problems:
                print("\n发现问题：")
                for p in problems:
                    print("  ✗ " + p)
                return 1
            print("\n✓ 检查通过")
        return 0
    finally:
        shutil.rmtree(work, ignore_errors=True)


if __name__ == "__main__":
    sys.exit(main())
