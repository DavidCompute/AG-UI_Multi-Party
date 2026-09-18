"""真实场景验证：PPT 页面上写着人名，图库描述也是人名 → 应自动配上该人的照片。

背景：模型不知道图库里有什么，只会写通用关键词（“员工 颁奖 舞台”）→ 检索不命中。
修法：第一次拿不到图（或只拿到网图）时，用**本页文字**在图库里再查一次，且自有素材优先。

本脚本不硬编码任何名字：从真实图库里挑一个“像人名”的描述来构造页面。
"""
import json
import re
import sys
import urllib.request
import urllib.error

BASE = "http://127.0.0.1:5200"
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


def call(m, p, b=None, t=None):
    d = json.dumps(b, ensure_ascii=False).encode() if b is not None else None
    h = {"Content-Type": "application/json"} if d else {}
    if t:
        h["Authorization"] = "Bearer " + t
    r = urllib.request.Request(BASE + p, data=d, headers=h, method=m)
    try:
        with urllib.request.urlopen(r, timeout=900) as x:
            return json.loads(x.read() or b"{}")
    except urllib.error.HTTPError as e:
        return {"_http": e.code, "_body": e.read().decode("utf-8", "replace")[:300]}


tok = call("POST", "/ag-ui/user/login", {"username": "david", "password": "lingtong"})["token"]


def run_deck(title, slides):
    r = call("POST", "/ag-ui/skills/pptx_deck/run",
             {"query": json.dumps({"title": title, "slides": slides}, ensure_ascii=False)}, t=tok)
    return json.loads(r["result"])


# ── 1) 从真实图库里挑一个“像人名”的描述（2–4 个汉字，无空格）──
libs = call("GET", "/ag-ui/image-libs", t=tok)["libraries"]
names = []
for l in libs:
    for a in (l.get("assets") or []):
        cap = (a.get("caption") or "").strip()
        if a.get("status") == "ready" and re.fullmatch(r"[\u4e00-\u9fa5]{2,4}", cap):
            names.append((l["name"], a["fileName"], cap))

print("== 0) 图库里“像人名”的描述（前 8 个）==")
for n in names[:8]:
    print("   ", n)
check("图库里确实存在人名型描述（这是本场景的前提）", bool(names))
if not names:
    print("\n结果：前提不成立，跳过")
    sys.exit(0)

lib_name, file_name, person = names[0]
print(f"   选用：库“{lib_name}” / {file_name} / 描述“{person}”")

# 用名字直接检索应能命中（证明机制本身没问题）
s = call("POST", "/ag-ui/images/search", {"query": person, "topK": 3, "minScore": 0.6}, t=tok)
print("   直接用该名字检索：", s.get("count"), [(x.get("fileName"), x.get("score")) for x in (s.get("images") or [])])
check("用名字能直接检索到图（机制可用）",
      any(x.get("fileName") == file_name for x in (s.get("images") or [])), str(s.get("images")))

# ── 2) 真实场景：页面上写着人名，但模型只给了通用关键词 ──
print("\n== A) 页面文字含人名 + 模型只给通用关键词 → 应配上该人的图 ==")
d = run_deck("年度颁奖盛典", [
    {"type": "image", "title": f"高效习惯优秀进步奖 · {person}", "imageQuery": "员工 颁奖 舞台"},
])
used = d.get("images") or []
warns = d.get("warnings") or []
print("   images =", [(i.get("source"), i.get("title"), i.get("query")) for i in used])
for w in warns:
    print("   warn   =", w[:160])
check("用上了图库里的图（source=library）", any(i.get("source") == "library" for i in used), str(used))
check("而且命中的是“本页文字”那条（query 含人名）",
      any(i.get("source") == "library" and person in (i.get("query") or "") for i in used), str(used))
check("没报“已改用自动生成的题图”（配上图了就不该是假消息）",
      not any("已改用自动生成的题图" in w for w in warns), str(warns))
print("   产物字节 =", (d.get("produce_file") or {}).get("bytes"))

# ── 3) 对照：页面也没有相关文字、关键词也不相干 → 不该乱配一张人物照 ──
print("\n== B) 对照：页面文字与图库无关 → 不该乱配人物照 ==")
d2 = run_deck("无关内容验证", [{"type": "image", "title": "qzxv-不存在-9987", "imageQuery": "qzxv-不存在-9987"}])
used2 = d2.get("images") or []
warns2 = d2.get("warnings") or []
print("   images =", [(i.get("source"), i.get("title"), i.get("query")) for i in used2])
print("   warnings 条数 =", len(warns2))
check("没有误配图库里的照片", not any(i.get("source") == "library" for i in used2), str(used2))
check("降级不静默（有 warning）", bool(warns2), str(warns2))

print("\n结果：" + ("全部通过" if ok else "存在失败项"))
sys.exit(0 if ok else 1)
