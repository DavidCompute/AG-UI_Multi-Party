"""三项真实验证（临时脚本）：
A) 无意义 imageQuery：应当取不到图 → 生成题图 + 明确 warnings；
B) 正常关键词：应当取到图库真实照片，且产物被瘦身（体积可接受）；
C) 改图片描述后：语义检索应命中**新描述**，旧描述失效。
"""
import json
import sys
import time
import urllib.request
import urllib.error

BASE = "http://127.0.0.1:5200"
try:
    sys.stdout.reconfigure(encoding="utf-8")
except Exception:
    pass


def call(m, p, b=None, t=None):
    d = json.dumps(b, ensure_ascii=False).encode() if b is not None else None
    h = {"Content-Type": "application/json"} if d else {}
    if t:
        h["Authorization"] = "Bearer " + t
    r = urllib.request.Request(BASE + p, data=d, headers=h, method=m)
    try:
        with urllib.request.urlopen(r, timeout=600) as x:
            return json.loads(x.read() or b"{}")
    except urllib.error.HTTPError as e:
        return {"_http": e.code, "_body": e.read().decode("utf-8", "replace")[:300]}


tok = call("POST", "/ag-ui/user/login", {"username": "david", "password": "lingtong"})["token"]
ok = True


def check(label, cond, extra=""):
    global ok
    print(("  OK   " if cond else "  FAIL ") + label + (("  " + extra) if extra and not cond else ""))
    if not cond:
        ok = False


def run_deck(label, slides):
    r = call("POST", "/ag-ui/skills/pptx_deck/run",
             {"query": json.dumps({"title": label, "slides": slides}, ensure_ascii=False)}, t=tok)
    d = json.loads(r["result"])
    return d


print("== A) 无意义的 imageQuery：不该“命中”任意照片，且要报警 ==")
d = run_deck("无匹配验证", [{"type": "image", "title": "配图页", "imageQuery": "qzxv-不存在-9987"}])
print("   bytes=", (d.get("produce_file") or {}).get("bytes"), "| images=", [i.get("title") for i in (d.get("images") or [])])
check("没有误用照片（images 为空）", not (d.get("images") or []), str(d.get("images")))
check("给出了 warnings（降级不静默）", bool(d.get("warnings")), str(d.get("warnings")))

print("\n== B) 正常关键词：应取到图库真实照片且产物已瘦身 ==")
d2 = run_deck("命中有图", [{"type": "image", "title": "配图页", "imageQuery": "城市天际线 黄昏 楼群剪影"}])
used = d2.get("images") or []
bytes2 = (d2.get("produce_file") or {}).get("bytes") or 0
print("   bytes=", bytes2, "| images=", [(i.get("source"), i.get("title"), i.get("library")) for i in used])
check("取到了图库照片", any(i.get("source") == "library" for i in used), str(used))
check("产物体积可控（< 6MB）", bytes2 < 6_000_000, f"{bytes2} 字节")

print("\n== C) 改图片描述 → 语义检索按新描述命中 ==")
libs = call("GET", "/ag-ui/image-libs", t=tok)["libraries"]
lib = next(l for l in libs if any(a["fileName"] == "skyline.png" for a in (l.get("assets") or [])))
asset = next(a for a in lib["assets"] if a["fileName"] == "skyline.png")
old_cap = asset.get("caption") or ""
print("   目标：", asset["fileName"], "| 旧描述片段：", old_cap[:40])

marker = "紫罗兰色潜水艇在珊瑚礁间穿行"
before = call("POST", "/ag-ui/images/search", {"query": marker, "topK": 3, "minScore": 0.25}, t=tok)
print("   改前用独特词检索命中：", before.get("count"))

st = call("PUT", f"/ag-ui/image-libs/{lib['libId']}/assets/{asset['assetId']}", {"caption": marker}, t=tok)
print("   改描述返回：status=", st.get("status"))

ready = False
for _ in range(40):
    time.sleep(2)
    cur = call("GET", "/ag-ui/image-libs", t=tok)["libraries"]
    a = next((x for l in cur if l["libId"] == lib["libId"] for x in (l.get("assets") or []) if x["assetId"] == asset["assetId"]), None)
    if a and a.get("status") == "ready":
        ready = True
        break
    if a and a.get("status") == "error":
        print("   向量化失败：", a.get("error"))
        break
check("改描述后重新向量化完成", ready)

after = call("POST", "/ag-ui/images/search", {"query": marker, "topK": 3, "minScore": 0.25}, t=tok)
hits = after.get("images") or []
print("   改后用同一独特词检索：", after.get("count"), [(x.get("fileName"), x.get("score")) for x in hits])
check("新描述能匹配到该图", any(x.get("fileName") == "skyline.png" for x in hits), str(hits))
check("同库其它图不与新描述抢位（排序第一就是它）", bool(hits) and hits[0].get("fileName") == "skyline.png", str(hits[:1]))

old = call("POST", "/ag-ui/images/search", {"query": "城市天际线 黄昏 楼群剪影", "topK": 3, "minScore": 0.6}, t=tok)
print("   旧描述短语（minScore 0.6）命中：", old.get("count"), [(x.get("fileName"), x.get("score")) for x in (old.get("images") or [])])
check("旧描述不再强命中该图（已被新描述覆盖）",
      not any(x.get("fileName") == "skyline.png" for x in (old.get("images") or [])),
      str(old.get("images")))

call("PUT", f"/ag-ui/image-libs/{lib['libId']}/assets/{asset['assetId']}", {"caption": old_cap}, t=tok)
print("   已还原原描述")

print("\n结果：" + ("全部通过" if ok else "存在失败项"))
sys.exit(0 if ok else 1)
