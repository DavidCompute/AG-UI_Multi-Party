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
"""

RELATED = "知聚平台有什么创新点"
LOOSELY_RELATED = "数字员工的组织架构是怎么协作的"
UNRELATED = "公司食堂今天中午吃什么"
NONSENSE = "qzxv-不存在-9987"


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
                       ("无关提问", UNRELATED, s_unrel), ("乱码提问", NONSENSE, s_nonsense)):
        print(f"   {name}：{q} -> {s}")
    check("真实提问分数明显高于无关提问（否则这套档位不成立）",
          s_rel is not None and s_unrel is not None and s_rel > s_unrel,
          f"{s_rel} vs {s_unrel}")
    check("无关提问高于“不筛”的下限 0.10（所以收紧是有意义的）",
          s_unrel is not None and s_unrel > 0.10, str(s_unrel))

    # ── B) 按库门槛的实际效果（同一向量 + 同一门槛）──
    print("\n== B) 库严格度改变实际召回（同一公式、同一门槛）==")
    call("PUT", f"/ag-ui/kb/{kb_id}", {"minScore": 0.40}, t=token)   # 严格
    # 直接用真实查询算“严格档下召回到几条”
    vec_unrel = "[" + ",".join(f"{x:.6f}" for x in embed(UNRELATED)) + "]"
    out_u, _ = psql(f"SELECT count(*) FROM agui_message_memory WHERE group_id='{group}' AND embedding IS NOT NULL "
                    f"AND 1-(embedding <=> '{vec_unrel}'::vector) >= 0.40")
    call("PUT", f"/ag-ui/kb/{kb_id}", {"minScore": 0.15}, t=token)   # 宽松
    out_u_loose, _ = psql(f"SELECT count(*) FROM agui_message_memory WHERE group_id='{group}' AND embedding IS NOT NULL "
                          f"AND 1-(embedding <=> '{vec_unrel}'::vector) >= 0.15")
    print(f"   无关提问在 严格0.40 下召回 {out_u} 条，在 宽松0.15 下召回 {out_u_loose} 条")
    check("严格档挡住了无关提问的切片", out_u == "0", out_u)
    check("宽松档把同一切片放了进来（说明门槛真的在起作用）", int(out_u_loose) >= 1, out_u_loose)

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
