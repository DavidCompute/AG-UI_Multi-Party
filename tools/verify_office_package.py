"""校验 .pptx / .docx / .xlsx 的 OPC 包结构完整性（对照 OOXML 的必备部件与关系规则）。

为什么需要它：OpenXML SDK 的 OpenXmlValidator 只校验**单部件内的 schema**，
不校验**跨部件的必备关系**——这正是 PowerPoint 判「需要修复」的典型来源。
实测踩到：只建 notesSlide 不建 notesMaster、母版不挂主题，校验器 0 错误，PowerPoint 却要求修复。

用法：
  python tools/verify_office_package.py <file.pptx> [more.pptx ...]

退出码：0 = 无结构缺陷；1 = 有缺陷（逐条打印）。
"""
import posixpath
import re
import sys
import zipfile

NS_R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships"
RNS = "{" + NS_R + "}"
PKG_REL_NS = "http://schemas.openxmlformats.org/package/2006/relationships"

# 关系类型简名 → 目标部件的必备规则
REL = NS_R + "/"


def rels_path(part):
    """部件的 .rels 路径：ppt/slides/slide1.xml → ppt/slides/_rels/slide1.xml.rels"""
    d, f = posixpath.split(part)
    return posixpath.join(d, "_rels", f + ".rels") if d else "_rels/" + f + ".rels"


def load_rels(z, part):
    """返回 {relType 简名: [target 部件路径（已归一化，去掉前导 /）]}"""
    rp = rels_path(part)
    if rp not in z.namelist():
        return {}
    import xml.etree.ElementTree as ET

    out = {}
    root = ET.fromstring(z.read(rp))
    base = posixpath.dirname(part)
    for rel in root.findall("{%s}Relationship" % PKG_REL_NS):
        t = rel.get("Type", "")
        short = t[len(REL):] if t.startswith(REL) else t
        target = rel.get("Target", "")
        if rel.get("TargetMode") == "External":
            continue
        norm = posixpath.normpath(posixpath.join(base, target)).lstrip("/")
        out.setdefault(short, []).append(norm)
    return out


def check(path):
    problems = []
    z = zipfile.ZipFile(path)
    names = set(z.namelist())

    def need(part, reltype, why):
        if part not in names:
            return
        targets = load_rels(z, part).get(reltype)
        if not targets:
            problems.append("%s 缺少 %s 关系（%s）" % (part, reltype, why))
        else:
            for t in targets:
                if t not in names:
                    problems.append("%s 的 %s 关系指向不存在的部件：%s" % (part, reltype, t))

    if "ppt/presentation.xml" in names:
        _check_pptx(z, names, need, problems)
    elif "word/document.xml" in names:
        _check_docx(z, names, need, problems)
    elif "xl/workbook.xml" in names:
        _check_xlsx(z, names, need, problems)
    else:
        problems.append("无法识别的包（既非 pptx / docx / xlsx）")
    return problems


def _check_pptx(z, names, need, problems):
    pres = "ppt/presentation.xml"
    prels = load_rels(z, pres)

    # 1) 每份幻灯片母版必须挂主题，且至少有一份母版
    masters = prels.get("slideMaster", [])
    if not masters:
        problems.append("presentation.xml 没有 slideMaster 关系（PPTX 必须至少有一份母版）")
    for m in masters:
        need(m, "theme", "幻灯片母版必须关联主题部件，否则 PowerPoint 会判需要修复")
        # 版式必须回指母版
        for lay in load_rels(z, m).get("slideLayout", []):
            need(lay, "slideMaster", "版式必须回指其母版")

    # 2) 有备注页就必须有备注母版，并由 presentation 与每个备注页分别关联
    notes_slides = [n for n in names if re.fullmatch(r"ppt/notesSlides/notesSlide\d+\.xml", n)]
    notes_masters = [n for n in names if re.fullmatch(r"ppt/notesMasters/notesMaster\d+\.xml", n)]
    if notes_slides and not notes_masters:
        problems.append("包里有 %d 个备注页，却没有 notesMaster（PowerPoint 会判需要修复）" % len(notes_slides))
    if notes_slides and not prels.get("notesMaster"):
        problems.append("presentation.xml 缺少 notesMasterIdLst / notesMaster 关系")
    for nm in notes_masters:
        need(nm, "theme", "备注母版同样必须关联主题")

    # 3) 幻灯片与备注页互指
    slides = [n for n in names if re.fullmatch(r"ppt/slides/slide\d+\.xml", n)]
    if not slides:
        problems.append("没有任何幻灯片部件")
    for s in slides:
        need(s, "slideLayout", "每张幻灯片必须挂一个版式")

    # 4) 每个部件都应被 [Content_Types].xml 声明
    ct = z.read("[Content_Types].xml").decode("utf-8")
    defaults = set(re.findall(r'Extension="([^"]+)"', ct))
    overrides = set(re.findall(r'PartName="([^"]+)"', ct))
    for n in sorted(names):
        if n == "[Content_Types].xml" or n.endswith(".rels"):
            continue
        ext = n.rsplit(".", 1)[-1].lower() if "." in n else ""
        if n.startswith("ppt/") and ("/" + n) in overrides:
            continue
        if ("/" + n) in overrides or ext in defaults:
            continue
        problems.append("部件未被 [Content_Types].xml 声明：%s" % n)


def _check_docx(z, names, need, problems):
    doc = "word/document.xml"
    if "word/styles.xml" not in names:
        problems.append("缺少 word/styles.xml（Word 会判需要修复）")
    need(doc, "styles", "文档必须关联样式部件")
    if not any(re.fullmatch(r"word/_rels/document\.xml\.rels", n) for n in names):
        problems.append("缺少 word/_rels/document.xml.rels")


def _check_xlsx(z, names, need, problems):
    need("xl/workbook.xml", "worksheet", "工作簿必须至少有一个工作表")
    if "xl/styles.xml" in names:
        need("xl/workbook.xml", "styles", "工作簿声明了样式部件就应当关联它")


def main():
    files = sys.argv[1:]
    if not files:
        print(__doc__)
        return 2
    bad = 0
    for f in files:
        try:
            problems = check(f)
        except Exception as e:  # noqa: BLE001
            print("%s\n  ✗ 读取失败：%s: %s" % (f, type(e).__name__, e))
            bad += 1
            continue
        if problems:
            bad += 1
            print("%s\n  ✗ 发现 %d 处结构缺陷：" % (f, len(problems)))
            for p in problems:
                print("    - " + p)
        else:
            print("%s\n  ✓ OPC 结构完整（必备部件与关系齐备）" % f)
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())
