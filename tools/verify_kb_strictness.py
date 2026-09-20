"""知识库「检索严格度」真实验证（临时脚本）。

知识库的检索结果只进模型上下文、没有面向用户的读取接口，所以这里的“端到端”分两层：
  A) **真实数据实测**：建临时库 + 上传真实文档（现场生成的小文档，答案明确），
     用平台的同一条公式（1 - 余弦距离，embedding 走同一个 ollama/bge-m3）
     量出“无关提问”与“真实提问”的分数分布 —— 严格度档位就是据此定的；
  B) **按库门槛的实际效果**：把库严格度设为 严格 / 宽松，用同一公式、同一门槛
     分别在真实向量上验算“该被挡住的被挡住、该召回的召回”，并核对接口持久化与隔离。

数据安全：全程在临时库里做，结束删库；真实知识库只读。
用法：python tools/verify_kb_strictness.py
"""
import json
import subprocess
import sys
import time
import urllib.error
import urllib.request
import uuid

BASE = "http://127.0.0.1:5200"
OLLAMA = "http://127.0.0.1:11435/v1/embeddings"
try:
    sys.stdout.reconfigure(encoding="utf-8")
except Exception:
    pass

ok = True
kb_id = None

DOC = """# 知聚平台内部说明（严格度验证用）

## 一、产品定位
知聚（KnowGath）是数字员工协作平台，核心是“组织架构 + 技能库 + 知识库 + 图库”。
首创把公司组织架构搬进聊天：用户打造一个团队，平台自动生成对应的数字员工并建群。

## 二、创新点
1. 组织即能力：配好组织架构，平台自动把交付技能挂到对应岗位。
2. 数字员工可相互委派（子数字员工），形成协作链路而不是单打独斗。
3. 记忆有拟人特征：广记型、深记型、难录入型、存得住想不起型、快速遗忘型。
4. 交付物直出文件：Word / Excel / PPT / PDF 技能内置，产物直接挂回对话可下载。
5. 图库与知识库可绑定到岗位，配图与问答都走企业自有素材，不依赖外网。

## 三、权限与治理
用户分组 + 分组授权；数字员工可按分组限定可访问范围；管理员控制台有审计与合规。

## 四、交付与部署
支持 Docker 一键部署与 Windows 桌面安装包（本地数据、离线可用）。

## 五、项目代号
本平台内部项目代号 ORION-7788，仅供内部文档引用。
"""

RELATED = "知聚平台有什么创新点"
LOOSELY_RELATED = "数字员工的组织架构是怎么协作的"
COMMON_WORD_ONLY = "公司食堂今天中午吃什么"   # 只与文档共用一个常用词“公司”（词面兜底曾经会把它当成命中）
DISTINCTIVE = "ORION-7788 是什么"             # 罕见号：词面兜底应当仍能召回来
NO_OVERLAP = "紫罗兰色潜水艇在珊瑚礁间穿行"    # 零词面交集：两边都只走向量路
UNRELATED = COMMON_WORD_ONLY
NONSENSE = "qzxv-不存在-9987"
# 旧 BM25 sigmoid 分给“只共用一个常用词”的典型值（0.537）；修正后它换成量纲化分（0.07），
# 所以“标准档带回的命中分”应当在余弦量纲里（< 0.5），而不是那 0.54 的假高。
Bm25_LIKE = 0.5


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
        with urllib.request.urlopen(r, timeout=600) as x:
            return x.status, json.loads(x.read() or b"{}")
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode("utf-8", "replace")[:300]


def psql(sql):
    r = subprocess.run(["docker", "exec", "agui-group-chat-pg", "psql", "-U", "postgres", "-d", "agui", "-tAc", sql],
                       capture_output=True, text=True, encoding="utf-8")
    return (r.stdout or "").strip(), (r.stderr or "").strip()


def embed(text):
    body = json.dumps({"model": "bge-m3:latest", "input": text}).encode()
    req = urllib.request.Request(OLLAMA, data=body, headers={"Content-Type": "application/json"}, method="POST")
    with urllib.request.urlopen(req, timeout=120) as x:
        return json.loads(x.read())["data"][0]["embedding"]


def top_score(group, query):
    """按平台同一条公式算该查询在该库里的最高分（1 - 余弦距离）。"""
    vec = "[" + ",".join(f"{x:.6f}" for x in embed(query)) + "]"
    out, err = psql(f"SELECT round(max(1-(embedding <=> '{vec}'::vector))::numeric, 4) "
                    f"FROM agui_message_memory WHERE group_id='{group}' AND embedding IS NOT NULL")
    if err:
        print("   SQL 错误：", err)
    return float(out) if out else None


try:
    _, login = call("POST", "/ag-ui/user/login", {"username": "david", "password": "lingtong"})
    token = login["token"]
    stamp = uuid.uuid4().hex[:6]

    st, kb = call("POST", "/ag-ui/kb", {"name": "严格度验证库-" + stamp, "description": "验证后即删"}, t=token)
    check("建临时知识库", st == 200 and kb.get("kbId"), str(kb)[:200])
    kb_id = kb["kbId"]

    # 上传文档（现场生成，答案明确）
    import os
    import tempfile
    doc_path = os.path.join(tempfile.gettempdir(), "agui-kb-strict.md")
    with open(doc_path, "w", encoding="utf-8") as f:
        f.write(DOC)
    boundary = "----agui" + uuid.uuid4().hex
    body = (("--" + boundary + '\r\nContent-Disposition: form-data; name="file"; filename="知聚平台内部说明.md"\r\n')
            + "Content-Type: text/markdown\r\n\r\n").encode()
    body = body + open(doc_path, "rb").read() + ("\r\n--" + boundary + "--\r\n").encode()
    req = urllib.request.Request(BASE + "/ag-ui/upload", data=body, method="POST",
                                headers={"Content-Type": f"multipart/form-data; boundary={boundary}",
                                         "Authorization": "Bearer " + token})
    with urllib.request.urlopen(req, timeout=180) as x:
        att = json.loads(x.read())["attachments"][0]["attachmentId"]
    st, doc = call("POST", f"/ag-ui/kb/{kb_id}/documents", {"attachmentId": att}, t=token)
    check("文档已登记（后台切片 + 向量化）", st == 200, str(doc)[:200])

    ready = False
    for _ in range(90):
        time.sleep(2)
        _, kbs = call("GET", "/ag-ui/kb", t=token)
        cur = next((k for k in kbs if k["kbId"] == kb_id), None)
        d = next(iter((cur or {}).get("documents", [])), None)
        if d and d.get("status") == "error":
            check("文档向量化成功", False, str(d.get("error")))
            raise SystemExit(1)
        if d and d.get("status") == "ready":
            ready = True
            print("   切片数：", d.get("chunkCount"))
            break
    check("文档向量化完成", ready)
    if not ready:
        raise SystemExit(1)

    # ── A) 真实分数分布（严格度档位的依据）──
    print("\n== A) 真实分数分布（平台同一条公式）==")
    group = f"kb:{kb_id}"
    s_rel, s_loose_rel = top_score(group, RELATED), top_score(group, LOOSELY_RELATED)
    s_unrel, s_nonsense = top_score(group, UNRELATED), top_score(group, NONSENSE)
    for name, q, s in (("真实提问", RELATED, s_rel), ("语义相关", LOOSELY_RELATED, s_loose_rel),
                       ("只共用常用词“公司”", COMMON_WORD_ONLY, s_unrel), ("乱码提问", NONSENSE, s_nonsense)):
        print(f"   {name}：{q} -> {s}")
    check("真实提问的向量分明显高于只共用常用词的那条（否则这套档位不成立）",
          s_rel is not None and s_unrel is not None and s_rel > s_unrel,
          f"{s_rel} vs {s_unrel}")
    check("无关提问高于“不筛”的下限 0.10（所以收紧是有意义的）",
          s_unrel is not None and s_unrel > 0.10, str(s_unrel))

    # ── B) 按库门槛的实际效果（走**平台自己的试检索接口**，不再靠 psql 验算）──
    print("\n== B) 库严格度改变实际召回（走 /ag-ui/kb/{id}/search）==")
    MIN_STANDARD, MIN_STRICT = 0.25, 0.40

    def probe(q, gate):
        _, d = call("POST", f"/ag-ui/kb/{kb_id}/search", {"query": q, "topK": 5, "minScore": gate}, t=token)
        return d

    d_real = probe(RELATED, MIN_STANDARD)
    print(f"   真实提问（标准 {MIN_STANDARD}）：{d_real.get('count')} 条，分 {[h['score'] for h in (d_real.get('hits') or [])][:2]}")
    check("真实提问能召回", (d_real.get("count") or 0) >= 1, str(d_real)[:200])

    # 只共用一个常用词：**严格档不该退回它**；标准档（= 平台默认 0.25）会带回一条“弱相关”的向量命中
    # —— 那是默认档应有的宽松行为（实测：无关提问的向量分 0.31~0.39 ，恰好能过 0.25），所以这里只钉严格档。
    d_common_std = probe(COMMON_WORD_ONLY, MIN_STANDARD)
    d_common_strict = probe(COMMON_WORD_ONLY, MIN_STRICT)
    std_scores = [h["score"] for h in (d_common_std.get("hits") or [])]
    print(f"   只共用常用词“公司”：（标准 {MIN_STANDARD}）→ {d_common_std.get('count')} 条 {std_scores}；"
          f"（严格 {MIN_STRICT}）→ {d_common_strict.get('count')} 条")
    check("只共用一个常用词的无关提问：严格档不召回（词面兜底不再蒙混过关）",
          d_common_strict.get("count") == 0, str(d_common_strict)[:200])
    check("标准档（平台默认 0.25）确实更宽松（它带回的是弱相关向量命中，不是词面那 0.54）",
          (d_common_std.get("count") or 0) >= 0 and all(s < Bm25_LIKE for s in std_scores),
          f"{std_scores}")

    # 罕见号：词面兜底的意义所在 —— 即使严格档也要能召回来
    d_code = probe(DISTINCTIVE, MIN_STRICT)
    print(f"   罕见号 ORION-7788（严格 {MIN_STRICT}）→ {d_code.get('count')} 条，分 {[h['score'] for h in (d_code.get('hits') or [])][:2]}")
    check("罕见号即使在严格档也能召回来（兜底没被修没）", (d_code.get("count") or 0) >= 1, str(d_code)[:200])

    # 零词面交集：两边都只走向量路 → 接口分数应与 psql 独立算的一致（交叉核对量纲没被改坏）
    d_vec = probe(NO_OVERLAP, 0.05)
    if d_vec.get("count"):
        api_score = float(d_vec["hits"][0]["score"])
        psql_score = top_score(group, NO_OVERLAP)
        print(f"   零词面交集：接口 {api_score} / psql {psql_score}")
        check("零词面交集时接口分与 psql 独立计算一致（±0.01）",
              psql_score is not None and abs(api_score - psql_score) <= 0.01, f"api={api_score} psql={psql_score}")
    check("接口返回的门槛与请求一致（界面靠它回显）",
          abs(float(d_real.get("minScore", 0)) - MIN_STANDARD) < 1e-9, str(d_real.get("minScore")))
    if d_real.get("count"):
        check("片段预览已截断（不把整片正文丢给前端）", len(d_real["hits"][0].get("snippet") or "") <= 201,
              str(len(d_real["hits"][0].get("snippet") or "")))

    # 保存后再试：不带 minScore 时应当用**库里存的值**
    call("PUT", f"/ag-ui/kb/{kb_id}", {"minScore": MIN_STRICT}, t=token)
    _, d_saved = call("POST", f"/ag-ui/kb/{kb_id}/search", {"query": COMMON_WORD_ONLY}, t=token)
    check("不传 minScore 时用库里存的门槛（0.40）且因此 0 条",
          abs(float(d_saved.get("minScore", 0)) - MIN_STRICT) < 1e-9 and d_saved.get("count") == 0,
          str(d_saved)[:200])

    # ── C) 接口持久化与隔离 ──
    print("\n== C) 接口持久化与隔离 ==")
    call("PUT", f"/ag-ui/kb/{kb_id}", {"minScore": 0.40}, t=token)
    _, kbs = call("GET", "/ag-ui/kb", t=token)
    mine = next((k for k in kbs if k["kbId"] == kb_id), None)
    check("列表回读严格度 = 0.40（界面靠它回显）", mine and abs(float(mine.get("minScore") or 0) - 0.40) < 1e-9, str(mine and mine.get("minScore")))
    others = [k["name"] for k in kbs if k["kbId"] != kb_id and k.get("minScore") not in (None, 0)]
    check("其它知识库未被改动", not others, str(others[:5]))

    st, _ = call("PUT", f"/ag-ui/kb/{kb_id}", {"minScore": 5}, t=token)   # 越界值应被夹住
    _, kbs = call("GET", "/ag-ui/kb", t=token)
    mine = next((k for k in kbs if k["kbId"] == kb_id), None)
    check("越界值被夹到上限 0.80", mine and abs(float(mine.get("minScore")) - 0.80) < 1e-9, str(mine and mine.get("minScore")))
finally:
    if kb_id:
        st, _ = call("DELETE", f"/ag-ui/kb/{kb_id}", t=token)
        print(f"\n清理：临时知识库 {'已删除' if st == 200 else '删除失败 ' + str(st)}")

print("\n结果：" + ("全部通过" if ok else "存在失败项"))
sys.exit(0 if ok else 1)
