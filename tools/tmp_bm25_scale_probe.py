"""临时：量 BM25 词面兜底在接口分数上的表现（定“门槛是否该管词面命中”用）。

对比三类查询在**同一个知识库**里的接口分数：
  A) 真实提问（答案在文档里）
  B) 只共用一个**常用词**（“公司”）——语义其实不相干
  C) 命中一个**罕见词 / 专有号**（语义上是找这个东西）
跑完删库。
"""
import json
import subprocess
import sys
import time
import urllib.error
import urllib.request
import uuid

BASE = "http://127.0.0.1:5200"
DOC = """# 知聚平台内部说明

## 一、产品定位
知聚是数字员工协作平台，核心是“组织架构 + 技能库 + 知识库 + 图库”。
首创把公司组织架构搬进聊天：用户打造一个团队，平台自动生成对应的数字员工并建群。

## 二、创新点
组织即能力；数字员工可相互委派（子数字员工）；记忆有拟人特征；
交付物直出 Word / Excel / PPT / PDF。

## 三、项目代号
本平台内部项目代号 ORION-7788，仅供内部文档引用。
"""
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
            return x.status, json.loads(x.read() or b"{}")
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode("utf-8", "replace")[:300]


def psql(sql):
    r = subprocess.run(["docker", "exec", "agui-group-chat-pg", "psql", "-U", "postgres", "-d", "agui", "-tAc", sql],
                       capture_output=True, text=True, encoding="utf-8")
    return (r.stdout or "").strip()


_, login = call("POST", "/ag-ui/user/login", {"username": "david", "password": "lingtong"})
token = login["token"]
kb_id = None
try:
    _, kb = call("POST", "/ag-ui/kb", {"name": "词面尺度测量-" + uuid.uuid4().hex[:6]}, t=token)
    kb_id = kb["kbId"]
    boundary = "----agui" + uuid.uuid4().hex
    body = (("--" + boundary + '\r\nContent-Disposition: form-data; name="file"; filename="说明.md"\r\n')
            + "Content-Type: text/markdown\r\n\r\n").encode() + DOC.encode() + ("\r\n--" + boundary + "--\r\n").encode()
    req = urllib.request.Request(BASE + "/ag-ui/upload", data=body, method="POST",
                                headers={"Content-Type": f"multipart/form-data; boundary={boundary}",
                                         "Authorization": "Bearer " + token})
    with urllib.request.urlopen(req, timeout=120) as x:
        att = json.loads(x.read())["attachments"][0]["attachmentId"]
    call("POST", f"/ag-ui/kb/{kb_id}/documents", {"attachmentId": att}, t=token)
    for _ in range(60):
        time.sleep(2)
        _, kbs = call("GET", "/ag-ui/kb", t=token)
        cur = next((k for k in kbs if k["kbId"] == kb_id), None)
        d = next(iter((cur or {}).get("documents", [])), None)
        if d and d.get("status") in ("ready", "error"):
            print("文档状态：", d.get("status"), "切片", d.get("chunkCount"))
            break

    queries = [
        ("A 真实提问", "知聚平台有什么创新点"),
        ("B 只共用常用词“公司”", "公司食堂今天中午吃什么"),
        ("C 命中罕见号 ORION-7788", "ORION-7788 是什么"),
        ("D 完全无词面交集", "紫罗兰色潜水艇在珊瑚礁间穿行"),
    ]
    print("\n接口分数（minScore=0.05 放开，看全部返回）：")
    for label, q in queries:
        st, d = call("POST", f"/ag-ui/kb/{kb_id}/search", {"query": q, "topK": 5, "minScore": 0.05}, t=token)
        hits = d.get("hits") if isinstance(d, dict) else None
        print(f"   {label}: count={d.get('count') if isinstance(d, dict) else d}"
              + (f"  top={[(h['score']) for h in hits[:2]]}" if hits else ""))

    # 单独的向量分（psql，同一条公式）用于对比
    import urllib.request as u
    def embed(text):
        b = json.dumps({"model": "bge-m3:latest", "input": text}).encode()
        r2 = u.Request("http://127.0.0.1:11435/v1/embeddings", data=b,
                       headers={"Content-Type": "application/json"}, method="POST")
        with u.urlopen(r2, timeout=120) as x:
            return json.loads(x.read())["data"][0]["embedding"]
    print("\n纯向量分（psql，1-余弦距离）：")
    for label, q in queries:
        vec = "[" + ",".join(f"{x:.6f}" for x in embed(q)) + "]"
        out = psql(f"SELECT round(max(1-(embedding <=> '{vec}'::vector))::numeric,4) "
                   f"FROM agui_message_memory WHERE group_id='kb:{kb_id}' AND embedding IS NOT NULL")
        print(f"   {label}: {out}")
finally:
    if kb_id:
        st, _ = call("DELETE", f"/ag-ui/kb/{kb_id}", t=token)
        print("\n清理：", st)
