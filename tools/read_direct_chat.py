"""找到与指定数字员工的单聊群，打印聊天记录（含附件），并可选下载最后一个 docx 检查段落。"""
import io
import json
import os
import re
import sys
import urllib.request
import zipfile

BASE = os.environ.get("AGUI_BASE", "http://127.0.0.1:5200")


def call(method, path, body=None, token=None, raw=False, timeout=180):
    data = json.dumps(body, ensure_ascii=False).encode() if body is not None else None
    headers = {"Content-Type": "application/json; charset=utf-8"} if data else {}
    if token:
        headers["Authorization"] = "Bearer " + token
    req = urllib.request.Request(BASE + path, data=data, headers=headers, method=method)
    with urllib.request.urlopen(req, timeout=timeout) as r:
        payload = r.read()
        return json.loads(payload or b"{}") if not raw else payload


token = call("POST", "/ag-ui/user/login",
             {"username": os.environ.get("AGUI_USER", "david"),
              "password": os.environ.get("AGUI_PWD", "lingtong")})["token"]

target = sys.argv[1] if len(sys.argv) > 1 else "doc_exporter"
nick = sys.argv[2] if len(sys.argv) > 2 else "Word交付专员"

me = call("GET", "/ag-ui/user/me", token=token)
uid = me.get("userId") if isinstance(me, dict) else None
groups = call("GET", f"/ag-ui/member/{uid}/groups", token=token)
groups = groups.get("groups", groups) if isinstance(groups, dict) else groups

found = None
for g in groups or []:
    name = str(g.get("groupName") or g.get("name") or "")
    if target in name or nick in name:
        found = g
        break

if not found:
    print("未找到包含该岗位的单聊群。现有群：")
    for g in groups or []:
        print("  ", g.get("groupId"), g.get("groupName"), g.get("kind"))
    sys.exit(1)

gid = found.get("groupId")
print(f"群：{gid}  {found.get('groupName')}  kind={found.get('kind')}\n")

msgs = call("GET", f"/ag-ui/group/{gid}/messages?count=60", token=token)
msgs = msgs.get("messages", msgs) if isinstance(msgs, dict) else msgs

last_docx = None
for m in msgs or []:
    who = m.get("senderNickname") or m.get("senderType") or "?"
    atts = m.get("attachments") or []
    tag = ("  附件=" + ",".join(f"{a.get('name')}({a.get('size')})" for a in atts)) if atts else ""
    content = (m.get("content") or "").replace("\n", " ")
    print(f"[{who}] {content[:400]}{tag}")
    for a in atts:
        if str(a.get("name", "")).lower().endswith(".docx"):
            last_docx = a

print()
if last_docx:
    print(f"== 检查最后一个 docx：{last_docx['name']} ==")
    body = call("GET", last_docx["url"], token=token, raw=True)
    z = zipfile.ZipFile(io.BytesIO(body))
    xml = z.read("word/document.xml").decode("utf-8")
    texts = [t for t in re.findall(r"<w:t[^>]*>([^<]*)</w:t>", xml) if t.strip()]
    print(f"字节={len(body)} 段落数={len(texts)}")
    for t in texts[:15]:
        print("  -", t[:100])
else:
    print("（该群没有 docx 附件）")
