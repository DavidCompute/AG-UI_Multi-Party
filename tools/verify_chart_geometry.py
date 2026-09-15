"""实盘验证：内置产出技能（pptx_deck / docx_report）的**图表几何**是否正常。

为什么单独有这一个：`verify_pptx_live.py` 等走的是“真实对话 → 模型选技能”的重链路，
适合验证端到端可用性；但**图表画得对不对**用像素量最直接，也没必要每次都惊动模型。
所以这里直接打 `/ag-ui/skills/{id}/run` 试运行通路（仍是真的容器、真的 Roslyn 编译执行、
真的落盘产物），再看产物里的 PNG。

量两件事：
  1) **墨迹不越界**：白底图上任何非白像素都算墨迹，包围盒四周必须留边（≥2px）。
     内容一多就画出画布时，ImageSharp 会静默裁到边框 —— “贴边”就是越界的痕迹。
  2) **图表真的画出来了**：彩色像素（max-min 通道 > 40）占比。饼图圆盘应占画布约 27%。
     只查越界是不够的：扇区被填成“弓形”时只剩贴外缘的发丝线，完全在画布内却等于没画。

用法：
  PYTHONIOENCODING=utf-8 python tools/verify_chart_geometry.py
  PYTHONIOENCODING=utf-8 python tools/verify_chart_geometry.py --keep   # 保留产物不删

环境变量：AGUI_BASE（默认 http://127.0.0.1:5200）、AGUI_USER、AGUI_PWD
"""
import io
import json
import os
import sys
import urllib.request
import zipfile

from PIL import Image

BASE = os.environ.get("AGUI_BASE", "http://127.0.0.1:5200")
USER = os.environ.get("AGUI_USER", "david")
PWD = os.environ.get("AGUI_PWD", "lingtong")

MARGIN = 2
MIN_PIE_FILL = 0.20


# ---------- HTTP ----------

def post(path, body, token=None):
    req = urllib.request.Request(
        BASE + path, data=json.dumps(body).encode(), method="POST")
    req.add_header("Content-Type", "application/json")
    if token:
        req.add_header("Authorization", "Bearer " + token)
    with urllib.request.urlopen(req, timeout=900) as r:
        return json.loads(r.read().decode())


def login():
    return post("/ag-ui/user/login", {"username": USER, "password": PWD})["token"]


def run_skill(skill_id, payload, token):
    """试运行技能。响应外层是 {skillId, result: "<技能 Run 的 JSON 字符串>", autoFix}。"""
    r = post("/ag-ui/skills/%s/run" % skill_id, {"query": json.dumps(payload)}, token)
    inner = json.loads(r["result"]) if isinstance(r.get("result"), str) else r
    if not inner.get("ok"):
        print("  !! %s 执行失败：%s" % (skill_id, json.dumps(inner, ensure_ascii=False)))
        sys.exit(1)
    print("  %s -> %s（chartFont=%s cjk=%s）"
          % (skill_id, inner["produce_file"]["path"], inner.get("chartFont"), inner.get("chartFontCjk")))
    return inner["produce_file"]["path"]


# ---------- 产物落盘 / 取图 ----------

def fetch_artifact(remote_path, local_name):
    """容器内路径 → 本地文件（经 docker cp）。非容器部署时直接把路径当本地路径。"""
    import subprocess
    container = os.environ.get("AGUI_CONTAINER", "agui-group-chat-web")
    local = os.path.join(os.environ.get("TEMP", "/tmp"), local_name)
    try:
        subprocess.run(["docker", "cp", container + ":" + remote_path, local],
                       check=True, capture_output=True)
        return local
    except (FileNotFoundError, subprocess.CalledProcessError):
        if os.path.exists(remote_path):
            return remote_path
        raise


def measure_png(data):
    im = Image.open(io.BytesIO(data)).convert("RGB")
    w, h = im.size
    px = im.load()
    minx, miny, maxx, maxy = w, h, -1, -1
    coloured = 0
    for y in range(h):
        for x in range(w):
            r, g, b = px[x, y]
            if not (r >= 250 and g >= 250 and b >= 250):
                if x < minx:
                    minx = x
                if x > maxx:
                    maxx = x
                if y < miny:
                    miny = y
                if y > maxy:
                    maxy = y
            if max(r, g, b) - min(r, g, b) > 40:
                coloured += 1
    return w, h, (minx, miny, maxx, maxy), coloured / float(w * h)


def check(label, local_path, expect_pie_fill):
    """expect_pie_fill=True 时额外断言实心度（该产物只含一张饼图）。"""
    print("  %s：%s" % (label, local_path))
    ok = True
    with zipfile.ZipFile(local_path) as z:
        names = sorted(n for n in z.namelist() if n.lower().endswith(".png"))
        if not names:
            print("    没有找到任何 PNG 图表部件！")
            return False
        for n in names:
            w, h, ink, coloured = measure_png(z.read(n))
            l, t, r, b = ink[0], ink[1], w - 1 - ink[2], h - 1 - ink[3]
            inside = l >= MARGIN and t >= MARGIN and r >= MARGIN and b >= MARGIN
            flag = "OK"
            if not inside:
                flag = "越界!"
                ok = False
            if expect_pie_fill and coloured < MIN_PIE_FILL:
                flag += " 饼图没画出来(实心度 %.2f%% < %.0f%%)" % (coloured * 100, MIN_PIE_FILL * 100)
                ok = False
            print("     %-22s canvas=%dx%d ink=(%d,%d)-(%d,%d) margins=L%d T%d R%d B%d coloured=%.2f%% %s"
                  % (n, w, h, ink[0], ink[1], ink[2], ink[3], l, t, r, b, coloured * 100, flag))
    return ok


# ---------- 夹具 ----------

LONG_TITLE = "长" * 200
CATS = ["分类%02d" % i + "字" * 18 for i in range(30)]
SERIES = [{"name": "系列名称很长需要换行排版的第%d组数据" % s,
           "values": [1234567.0 + s * 1000 + c * 37 for c in range(30)]}
          for s in range(4)]
PIE_CATS = ["饼图分类项第%d个很长很长的名字" % i for i in range(40)]
PIE_VALUES = [float(i + 1) for i in range(40)]


def stress_deck():
    return {"title": "图表越界压力测试", "slides": [
        {"type": "cover", "title": "图表越界压力测试"},
        {"type": "chart", "title": LONG_TITLE, "chartType": "bar",
         "yLabel": "单位：这是一个很长的纵轴单位说明文字", "categories": CATS, "series": SERIES},
        {"type": "chart", "title": LONG_TITLE, "chartType": "line",
         "yLabel": "纵轴单位", "categories": CATS, "series": SERIES},
        {"type": "chart", "title": LONG_TITLE, "chartType": "pie",
         "categories": PIE_CATS, "series": [{"name": "占比", "values": PIE_VALUES}]},
    ]}


def pie_deck():
    return {"title": "饼图填充检查", "slides": [
        {"type": "chart", "title": "饼图填充检查", "chartType": "pie",
         "categories": ["甲", "乙", "丙", "丁"],
         "series": [{"name": "占比", "values": [40.0, 30.0, 20.0, 10.0]}]},
    ]}


def pie_docx():
    return {"title": "饼图填充检查", "sections": [
        {"chart": {"type": "pie", "title": "饼图填充检查",
                   "categories": ["甲", "乙", "丙", "丁"],
                   "values": [40.0, 30.0, 20.0, 10.0]}},
    ]}


def main():
    keep = "--keep" in sys.argv
    token = login()
    print("登录成功：%s（%s）" % (USER, BASE))

    cases = [
        ("pptx 压力图表（200 字标题 / 30 分类 / 4 系列 / 饼图 40 项）",
         "pptx_deck", stress_deck(), "e2e_pptx_stress.pptx", False),
        ("pptx 饼图实心度（4 项）", "pptx_deck", pie_deck(), "e2e_pptx_pie.pptx", True),
        ("docx 饼图实心度（4 项）", "docx_report", pie_docx(), "e2e_docx_pie.docx", True),
    ]

    all_ok = True
    for label, skill, payload, local_name, pie in cases:
        print("\n[%s]" % label)
        remote = run_skill(skill, payload, token)
        if not check(label, fetch_artifact(remote, local_name), pie):
            all_ok = False
        if not keep:
            try:
                os.remove(os.path.join(os.environ.get("TEMP", "/tmp"), local_name))
            except OSError:
                pass

    print("\n==== %s ====" % ("全部通过" if all_ok else "存在失败项"))
    sys.exit(0 if all_ok else 1)


main()
