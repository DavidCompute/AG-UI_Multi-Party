#!/usr/bin/env python3
"""用「第二个渲染器」复核：文字有没有装进**自己的文本框**。

为什么需要这个工具：我们自己的估算（`BlockHeightEmu`）是自家人，改错了自己看不出来。
真正要回答的问题有两个：

  1. **渲染器是不是在替我们兜底？** 生成的 pptx 里带 `<a:normAutofit/>`，
     LibreOffice 打开时会**自己把放不下的文字缩小**（实测：标题两行需要 81pt、框只有 60pt，
     LO 直接缩到 74%）—— 于是样张看着没问题，用户那边的 PowerPoint 却溢出了
     （PowerPoint 打开时并不重算 autofit，PptxGenJS 的 `shrinkText` 被抱怨
     “编辑一下才生效”是同一个坑）。
     所以本工具默认**先把 autofit 摘掉**再渲染：这样渲染出来的版式完全由我们写进去的字号决定，
     估算错了就会立刻现形，没地方躲。

  2. **文字有没有越出自己的框？** 从 pptx 的 XML 里量出每个文本框的矩形（EMU → pt），
     从 PDF 里取出每个词的包围盒（`pdftotext -bbox`），
     把词按「中心点落在哪个框里」归位，再检查它有没有越界。

用法：
  # 生成一份「文字压力」样张并检查（最常用）
  python tools/verify-pptx-textfit.py --stress

  # 检查任意产物（默认摘掉 autofit）
  python tools/verify-pptx-textfit.py /path/to/x.pptx
  python tools/verify-pptx-textfit.py x.pptx --keep-autofit     # 保留 autofit 对照
  python tools/verify-pptx-textfit.py x.pptx --tol 6           # 放宽容差（pt）

退出码：0 = 通过；1 = 发现越界；2 = 工具/环境问题。

环境变量：AGUI_CONTAINER（默认 agui-group-chat-web）
"""
import argparse
import os
import re
import shutil
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET
import zipfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CONTAINER = os.environ.get("AGUI_CONTAINER", "agui-group-chat-web")

NS = {
    "a": "http://schemas.openxmlformats.org/drawingml/2006/main",
    "p": "http://schemas.openxmlformats.org/presentationml/2006/main",
}
EMU_PER_PT = 12700.0

try:
    sys.stdout.reconfigure(encoding="utf-8")
except Exception:
    pass


# ---------------------------------------------------------------- 生成压力样张
def stress_json() -> str:
    """文字压力样张：把「长标题 / 长引言 / 超长卡片说明 / 超多要点」一次给全。"""
    long_zh = ("平台治理需要统一账号体系权限模型与审计日志覆盖组织架构的全部层级，"
               "确保任何一次权限变更都能追溯到操作人、时间与影响范围，并能一键回滚。")
    bullets = [f"{i + 1}. {long_zh}" for i in range(14)]
    return (
        '{"title":"文字压力样张","theme":"pure-tech-blue","style":"soft",'
        '"slides":['
        '{"type":"cover","title":"文字压力样张","subtitle":"长标题 / 长引言 / 长卡片 / 超多要点"},'
        '{"type":"quote","cite":"压力测试",'
        '"text":"这段话是刻意写得很长的引言，用来验证当引言文字远远超过文本框容量时，'
        '排版是否会自动缩小字号或者换行，而不至于把文字画到页面外面去。'
        '一个好的生成器不应该把这类责任推给使用者，也不应该依赖打开文件的软件去猜我们的意图。'
        '真正可靠的做法是在生成阶段就把字数量测清楚，按可用宽度算出行数，再据此决定字号缩放。"},'
        '{"type":"content","title":"这是一个刻意写得非常非常长以至于在标题区域一行放不下'
        '并且需要折成两行才能完整显示的内容页标题，用来验证标题自动缩放是否生效",'
        '"bullets":["要点一：验证标题过长时是否会溢出到正文区。"]},'
        '{"type":"content","title":"二十条要点压力","bullets":' + _json_list(bullets) + '},'
        '{"type":"grid","title":"长卡片说明","cols":2,"items":['
        '{"title":"卡片一","text":"' + long_zh + long_zh + '"},'
        '{"title":"卡片二","text":"' + long_zh + '"},'
        '{"title":"卡片三","text":"' + long_zh + '"},'
        '{"title":"卡片四","text":"' + long_zh + '"}]},'
        '{"type":"matrix","title":"四象限长文案","xTitle":"投入","yTitle":"价值",'
        '"items":[{"title":"左上","text":"' + long_zh + '"},{"title":"右上","text":"' + long_zh + '"},'
        '{"title":"左下","text":"' + long_zh + '"},{"title":"右下","text":"' + long_zh + '"}]},'
        '{"type":"pyramid","title":"金字塔长文案","items":['
        '{"title":"第一层","text":"' + long_zh + '"},{"title":"第二层","text":"' + long_zh + '"},'
        '{"title":"第三层","text":"' + long_zh + '"}]},'
        '{"type":"stack","title":"架构分层长文案","items":['
        '{"title":"接入层","text":"' + long_zh + '"},{"title":"服务层","text":"' + long_zh + '"},'
        '{"title":"数据层","text":"' + long_zh + '"}]},'
        '{"type":"summary","title":"小结","bullets":' + _json_list(bullets[:10]) + ','
        '"actions":["第一步：' + long_zh + '","第二步：' + long_zh + '","第三步：' + long_zh + '"],'
        '"variant":"split"},'
        '{"type":"table","title":"长单元格表格","headers":["项目","说明","负责人"],'
        '"rows":[' + ",".join('["项目%d","%s","张三"]' % (i + 1, long_zh) for i in range(6)) + ']},'
        '{"type":"iconrows","title":"图标行长说明","items":['
        '{"icon":"chart","title":"数据","text":"' + long_zh + '"},'
        '{"icon":"gear","title":"配置","text":"' + long_zh + '"},'
        '{"icon":"shield","title":"安全","text":"' + long_zh + '"}]}'
        "]}"
    )


def _json_list(items) -> str:
    import json
    return json.dumps(items, ensure_ascii=False)


def build_stress(workdir: str) -> str:
    req = os.path.join(workdir, "stress.json")
    with open(req, "w", encoding="utf-8", newline="") as f:
        f.write(stress_json())
    out = os.path.join(workdir, "out")
    os.makedirs(out, exist_ok=True)
    env = dict(os.environ, AGUI_PPTX_OUT=out)
    proc = subprocess.run(
        [sys.executable, os.path.join(ROOT, "tools", "run-skill.py"),
         os.path.join(ROOT, "tools", "pptx-skills", "pptx_deck.cs"), "--json", stress_json(), "--out", out],
        capture_output=True, text=True, encoding="utf-8", errors="replace", cwd=ROOT, env=env)
    ppts = [os.path.join(out, f) for f in os.listdir(out) if f.endswith(".pptx")]
    if not ppts:
        print("生成压力样张失败：")
        print((proc.stdout or "")[-3000:])
        sys.exit(2)
    return max(ppts, key=os.path.getmtime)


# ---------------------------------------------------------------- 摘掉 autofit
AUTOFIT_RE = re.compile(rb"<a:normAutofit[^/>]*/?>")


def strip_autofit(src: str, dst: str) -> int:
    """删掉幻灯片里的 <a:normAutofit/>，让渲染器无法替我们缩小文字。"""
    n = 0
    with zipfile.ZipFile(src) as zin, zipfile.ZipFile(dst, "w", zipfile.ZIP_DEFLATED) as zout:
        for item in zin.infolist():
            data = zin.read(item.filename)
            if re.fullmatch(r"ppt/slides/slide\d+\.xml", item.filename):
                data, k = AUTOFIT_RE.subn(b"", data)
                n += k
            zout.writestr(item, data)
    return n


# ---------------------------------------------------------------- 渲染（容器）
def render(src: str, workdir: str) -> str:
    tmp = "/tmp/agui-textfit-" + str(os.getpid())
    run(["docker", "exec", "-u", "0", CONTAINER, "bash", "-lc",
         f"rm -rf {tmp} && mkdir -p {tmp} && chmod 777 {tmp}"])
    if run(["docker", "cp", src, f"{CONTAINER}:{tmp}/in.pptx"]).returncode != 0:
        print("docker cp 失败（容器是否在跑？）")
        sys.exit(2)
    cmd = (f"cd {tmp} && soffice --headless --norestore "
           f"-env:UserInstallation=file://{tmp}/profile --convert-to pdf --outdir {tmp} in.pptx "
           f">/dev/null 2>&1 && pdftotext -bbox in.pdf out.xml && echo OK")
    conv = run(["docker", "exec", CONTAINER, "bash", "-lc", cmd])
    if "OK" not in (conv.stdout or ""):
        print("容器内转换失败：")
        print((conv.stdout or "") + (conv.stderr or ""))
        sys.exit(2)
    dst = os.path.join(workdir, "bbox.xml")
    run(["docker", "cp", f"{CONTAINER}:{tmp}/out.xml", dst])
    run(["docker", "exec", "-u", "0", CONTAINER, "bash", "-lc", f"rm -rf {tmp}"])
    return dst


def run(cmd, **kw):
    return subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8",
                          errors="replace", **kw)


# ---------------------------------------------------------------- 解析
class Box:
    __slots__ = ("x0", "y0", "x1", "y1", "text")

    def __init__(self, x, y, w, h, text):
        self.x0, self.y0 = x / EMU_PER_PT, y / EMU_PER_PT
        self.x1, self.y1 = (x + w) / EMU_PER_PT, (y + h) / EMU_PER_PT
        self.text = text

    @property
    def area(self):
        return (self.x1 - self.x0) * (self.y1 - self.y0)


def boxes_of_slide(pptx: str) -> list:
    """每页的文本框矩形（pt）+ 里面的文字。"""
    pages = []
    with zipfile.ZipFile(pptx) as z:
        names = sorted((n for n in z.namelist() if re.fullmatch(r"ppt/slides/slide\d+\.xml", n)),
                       key=lambda n: int(re.findall(r"\d+", n)[0]))
        for name in names:
            root = ET.fromstring(z.read(name))
            boxes = []
            for sp in root.iter("{%s}sp" % NS["p"]):
                body = sp.find("{%s}txBody" % NS["p"])
                if body is None:
                    continue
                xfrm = sp.find("{%s}spPr/{%s}xfrm" % (NS["p"], NS["a"]))
                if xfrm is None:
                    continue
                off, ext = xfrm.find("{%s}off" % NS["a"]), xfrm.find("{%s}ext" % NS["a"])
                if off is None or ext is None:
                    continue
                text = "".join(t.text or "" for t in body.iter("{%s}t" % NS["a"]))
                if not text.strip():
                    continue
                boxes.append(Box(int(off.get("x")), int(off.get("y")),
                                 int(ext.get("cx")), int(ext.get("cy")), text))
            pages.append(boxes)
    return pages


def words_of_pdf(bbox_xml: str) -> list:
    """每页的 (词, 包围盒) 列表（pt）。

    pdftotext -bbox 输出的是带默认命名空间的 XHTML，ElementTree 的 `iter("page")`
    匹配不到（实测踩到：0 页）。这里直接用正则，反而更简单、也更稳。
    """
    text = open(bbox_xml, encoding="utf-8", errors="replace").read()
    pages = []
    for chunk in text.split("<page")[1:]:
        w = float(re.search(r'width="([0-9.]+)"', chunk).group(1))
        h = float(re.search(r'height="([0-9.]+)"', chunk).group(1))
        words = [(m.group(5), float(m.group(1)), float(m.group(2)), float(m.group(3)), float(m.group(4)))
                 for m in re.finditer(
                     r'<word xMin="([0-9.]+)" yMin="([0-9.]+)" xMax="([0-9.]+)" yMax="([0-9.]+)">([^<]*)</word>',
                     chunk)]
        pages.append((w, h, words))
    return pages


# ---------------------------------------------------------------- 检查
def check(pptx: str, bbox_xml: str, tol: float):
    boxes = boxes_of_slide(pptx)
    pages = words_of_pdf(bbox_xml)
    problems, notes = [], []
    if len(boxes) != len(pages):
        problems.append(f"页数不符：pptx 有 {len(boxes)} 页，渲染出 {len(pages)} 页")
    for i, (pw, ph, words) in enumerate(pages, 1):
        outside = [w for w in words if w[1] < -tol or w[2] < -tol or w[3] > pw + tol or w[4] > ph + tol]
        if outside:
            problems.append(f"第 {i} 页：有 {len(outside)} 个词画到页面外（如 “{outside[0][0][:20]}” "
                            f"y={outside[0][2]:.1f}~{outside[0][4]:.1f}，页面高 {ph:.0f}）")
        unmatched, escaped = 0, []
        for (txt, x0, y0, x1, y1) in words:
            cx, cy = (x0 + x1) / 2, (y0 + y1) / 2
            page_boxes = boxes[i - 1] if i - 1 < len(boxes) else []
            # 先找**完整包住这个词**的框：同一篇里框会相邻/重叠（闭环节点圆与它的说明文字就在一起），
            # 只按“中心点落在哪 + 面积最小”会挑错盒子 —— 实测跏到：说明文字“操作审计”被归到了
            # 旁边那个更小的节点圆框上，于是误报“越出右边框 17.8pt”。
            inside = [b for b in page_boxes
                      if b.x0 - tol <= x0 and x1 <= b.x1 + tol
                      and b.y0 - tol <= y0 and y1 <= b.y1 + tol]
            if inside:
                continue   # 归属明确，不存在“越界”
            cands = [b for b in page_boxes
                     if b.x0 - tol <= cx <= b.x1 + tol and b.y0 - tol <= cy <= b.y1 + tol]
            if not cands:
                # 中心点也不在任何框里：再认一类**明明装不下这个词的框**（比词还窄/还矮、又与它相交）。
                # 为什么要这一道：文字撑出小框时中心点也在框外（文字居中溢出），只按中心点会把它当成
                # “未匹配”而静默放过 —— 实测把节点文本框改窄到 20pt，工具原本报“通过”。
                inter = [b for b in page_boxes
                         if b.x0 - tol < x1 and x0 < b.x1 + tol
                         and b.y0 - tol < y1 and y0 < b.y1 + tol
                         and ((b.x1 - b.x0) < (x1 - x0) - tol or (b.y1 - b.y0) < (y1 - y0) - tol)]
                if not inter:
                    unmatched += 1
                    continue
                cands = inter
            b = min(cands, key=lambda b: b.area)
            over_b = y1 - (b.y1 + tol)
            over_t = (b.y0 - tol) - y0
            over_r = x1 - (b.x1 + tol)
            over_l = (b.x0 - tol) - x0
            worst = max(over_b, over_t, over_r, over_l)
            side = max((("下", over_b), ("上", over_t), ("右", over_r), ("左", over_l)), key=lambda t: t[1])[0]
            # 上/左边框放宽到 6pt：汉字的**字形墨迹**本来就会略微超出首行行框
            # （标题首行尤其明显）；下/右边界才是“压到别人身上”，保持严格
            if worst > (6.0 if side in ("上", "左") else 1.5):
                escaped.append((worst, side, txt, b))
        if escaped:
            escaped.sort(reverse=True, key=lambda e: e[0])
            for worst, side, txt, b in escaped[:3]:
                problems.append(f"第 {i} 页：文字越出{side}边框 {worst:.2f}pt——“{txt[:24]}”"
                                f"（框 y {b.y0:.0f}~{b.y1:.0f} pt，x {b.x0:.0f}~{b.x1:.0f} pt）")
        notes.append(f"  第 {i} 页：文本框 {len(boxes[i - 1]) if i - 1 < len(boxes) else 0} 个，"
                     f"词 {len(words)} 个，越界 {len(escaped)}，未匹配 {unmatched}（表格/图表里的文字不在框内，属正常）")
    return problems, notes


def run_live(workdir: str) -> str:
    """把同一份压力样张交给**平台接口**在容器里跑（真实字体环境），再把产物拉回本地。

    为什么要有这一条：本机跑 run-skill.py 用的是本机字体（Windows 上通常是微软雅黑，行高 1.27em），
    而线上跑在容器里（Noto Sans CJK SC，1.45em）—— 行高差 14%，本机通过不代表线上通过。
    """
    import json as _json
    import urllib.request

    base = os.environ.get("AGUI_BASE", "http://127.0.0.1:5200")
    user = os.environ.get("AGUI_USER", "david")
    pwd = os.environ.get("AGUI_PWD", "lingtong")

    def post(path, body, token=None):
        req = urllib.request.Request(base + path, data=_json.dumps(body).encode(), method="POST")
        req.add_header("Content-Type", "application/json")
        if token:
            req.add_header("Authorization", "Bearer " + token)
        with urllib.request.urlopen(req, timeout=900) as r:
            return _json.loads(r.read().decode())

    token = post("/ag-ui/user/login", {"username": user, "password": pwd})["token"]
    res = post("/ag-ui/skills/pptx_deck/run", {"query": stress_json()}, token)
    inner = _json.loads(res["result"]) if isinstance(res.get("result"), str) else res
    if not inner.get("ok"):
        print("平台执行失败：" + _json.dumps(inner, ensure_ascii=False)[:800])
        sys.exit(2)
    remote = inner["produce_file"]["path"]
    print("  平台产物：" + remote + "（chartFont=" + str(inner.get("chartFont"))
          + " cjk=" + str(inner.get("chartFontCjk")) + "）")
    if inner.get("warnings"):
        print("  warnings：" + _json.dumps(inner["warnings"], ensure_ascii=False))
    if inner.get("qa", {}).get("issueCount"):
        print("  qa 报问题：" + _json.dumps(inner["qa"].get("issues"), ensure_ascii=False))
    local = os.path.join(workdir, os.path.basename(remote))
    cp = run(["docker", "cp", CONTAINER + ":" + remote, local])
    if cp.returncode != 0 or not os.path.exists(local):
        if os.path.exists(remote):
            return remote
        print("拉取产物失败：" + (cp.stderr or ""))
        sys.exit(2)
    return local


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("pptx", nargs="?", help="要检查的 pptx")
    ap.add_argument("--stress", action="store_true", help="先生成本地“文字压力样张”再检查（本机字体）")
    ap.add_argument("--live", action="store_true", help="经平台接口在容器里跑压力样张（线上字体）")
    ap.add_argument("--keep-autofit", action="store_true", help="保留 <a:normAutofit/>（对照组）")
    ap.add_argument("--tol", type=float, default=4.0, help="允许的越界容差（pt，默认 4）")
    ap.add_argument("--keep", action="store_true", help="保留临时产物")
    args = ap.parse_args()
    if not args.pptx and not args.stress and not args.live:
        ap.error("给一个 pptx 路径，或加 --stress / --live")

    work = tempfile.mkdtemp(prefix="agui-textfit-")
    try:
        src = run_live(work) if args.live else (build_stress(work) if args.stress else args.pptx)
        if not os.path.exists(src):
            print("找不到文件：" + src)
            return 2
        target = src
        tag = "保留 autofit（渲染器可兜底，参考用）"
        if not args.keep_autofit:
            target = os.path.join(work, "no-autofit.pptx")
            n = strip_autofit(src, target)
            tag = f"已摘掉 {n} 处 <a:normAutofit/>（渲染器无法替我们缩小文字）"
        print("检查：" + os.path.basename(src))
        print("模式：" + tag + "，容差 " + str(args.tol) + "pt")

        bbox = render(target, work)
        problems, notes = check(target, bbox, args.tol)
        for line in notes:
            print(line)
        if problems:
            print(f"\n发现 {len(problems)} 个问题：")
            for p in problems:
                print("  ✗ " + p)
            if not args.keep:
                pass
            return 1
        print("\n✓ 全部文字都在自己的框里（且不是渲染器兜底的结果）")
        return 0
    finally:
        if not args.keep:
            shutil.rmtree(work, ignore_errors=True)


if __name__ == "__main__":
    sys.exit(main())
