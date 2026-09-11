"""实盘回归：选一支带文档技能的团队岗位，带附件提问要求 Word，检查是否产出实体文件。

用法：
  AGUI_BASE=http://127.0.0.1:5200 python tools/live_docx_check.py            # 只列出候选
  AGUI_BASE=http://127.0.0.1:5200 python tools/live_docx_check.py --run <agentId>
"""
import json
import os
import sys
import threading
import time
import urllib.request

import websockets

BASE = os.environ.get("AGUI_BASE", "http://127.0.0.1:5200")
DOC_PREFIX = ("docx_",)
DOC_KEYS = ("docx", "word")


def call(method, path, body=None, token=None, raw=False, timeout=600):
    data = json.dumps(body, ensure_ascii=False).encode() if body is not None else None
    headers = {"Content-Type": "application/json; charset=utf-8"} if data else {}
    if token:
        headers["Authorization"] = "Bearer " + token
    req = urllib.request.Request(BASE + path, data=data, headers=headers, method=method)
    try:
        with urllib.request.urlopen(req, timeout=timeout) as r:
            payload = r.read()
            return r.status, (payload if raw else json.loads(payload or b"{}"))
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode("utf-8", "replace")


token = call("POST", "/ag-ui/user/login",
             {"username": os.environ.get("AGUI_USER", "david"),
              "password": os.environ.get("AGUI_PWD", "lingtong")})[1]["token"]

agents = call("GET", "/ag-ui/agents", token=token)[1]
agents = agents.get("agents", agents) if isinstance(agents, dict) else agents

cands = []
for a in agents or []:
    ids = a.get("skillDefIds") or []
    hit = [i for i in ids if any(k in (i or "").lower() for k in DOC_KEYS)]
    if hit:
        cands.append((a, hit))

print("文档交付候选岗位：")
for a, hit in cands:
    print(f"  {a.get('agentId'):24} {a.get('nickname'):14} {hit}")

if "--run" not in sys.argv:
    print("\n（加 --run <agentId> 实测交付）")
    sys.exit(0)

target = sys.argv[sys.argv.index("--run") + 1]
match = next((a for a, _ in cands if a.get("agentId") == target), None)
if not match:
    print(f"未找到候选：{target}")
    sys.exit(1)

st, res = call("POST", "/ag-ui/agents/direct", {"agentId": target}, token=token)
gid = res["groupId"]
print(f"\n单聊群：{gid}")

call("POST", "/ag-ui/agent/register", {
    "agentId": target, "groupId": gid, "triggerMode": "AllMessages",
    "nickname": match.get("nickname") or "", "override": True,
}, token=token)

before = call("GET", f"/ag-ui/group/{gid}/messages?count=50", token=token)[1]
before = before.get("messages", before) if isinstance(before, dict) else before
base_atts = {a.get("url") for m in (before or []) for a in (m.get("attachments") or []) if a.get("url")}

QUESTION = "请写一份 300 字左右的“知聚”平台简介，并导出为 Word 文档。"

# 后台自动批准：docx 技能是 dotnet，强制人工审批；没人批准就永远停在交互卡上，
# 看起来像“没产出文件”，容易误判（实测踩到）。
_stop = threading.Event()


def _auto_approve_bg(uid):
    import asyncio

    async def run():
        ws_url = BASE.replace("http://", "ws://") + f"/ws?memberId={uid}&token={token}"
        seen = set()
        try:
            async with websockets.connect(ws_url) as ws:
                await ws.send(json.dumps({"type": "GROUP_SUBSCRIBE", "groupIds": [gid]}))
                while not _stop.is_set():
                    try:
                        raw = await asyncio.wait_for(ws.recv(), timeout=3)
                    except asyncio.TimeoutError:
                        continue
                    except Exception:
                        return
                    try:
                        ev = json.loads(raw)
                    except Exception:
                        continue
                    if ev.get("type") != "AGENT_INTERACTION_REQUEST":
                        continue
                    iid = ev.get("interruptId")
                    if not iid or iid in seen:
                        continue
                    seen.add(iid)
                    st, res = call("POST", "/ag-ui/group/interaction/resolve", {
                        "groupId": gid, "interruptId": iid, "memberId": uid,
                        "approved": True, "approveAll": True,
                    }, token=token)
                    print(f"    [自动批准] {iid} → {st}")
        except Exception as e:
            print(f"    [自动批准] 退出: {type(e).__name__}: {e}")

    asyncio.run(run())


token_uid = call("GET", "/ag-ui/user/me", token=token)[1]
uid = token_uid.get("userId") if isinstance(token_uid, dict) else None
if uid:
    threading.Thread(target=_auto_approve_bg, args=(uid,), daemon=True).start()

st, res = call("POST", "/ag-ui/group/message/send", {"groupId": gid, "content": QUESTION}, token=token)
print(f"已发送（{st}）：{QUESTION}")

for i in range(120):  # 最多 10 分钟
    time.sleep(5)
    msgs = call("GET", f"/ag-ui/group/{gid}/messages?count=50", token=token)[1]
    msgs = msgs.get("messages", msgs) if isinstance(msgs, dict) else msgs
    new = [a for m in (msgs or []) for a in (m.get("attachments") or [])
           if a.get("url") and a["url"] not in base_atts]
    if new:
        for a in new:
            st, body = call("GET", a["url"], token=token, raw=True)
            ok = isinstance(body, bytes) and body[:2] == b"PK"
            print(f"\n✓ 产出：{a['name']}（{a['size']} 字节）合法docx={ok}")
            print(f"  下载：{BASE}{a['url']}")
        sys.exit(0)
    if i % 6 == 5:
        last = (msgs or [])[-1].get("content") if msgs else ""
        print(f"    ...{(i+1)*5}s {((last or '')[:100]).replace(chr(10),' ')}")

print("\n✗ 超时未产出文件")
_stop.set()
sys.exit(1)
