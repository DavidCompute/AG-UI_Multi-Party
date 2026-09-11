"""诊断：打印指定群的最近消息（含工具调用与附件），用于排查交付链路。"""
import json
import os
import sys
import urllib.request

BASE = os.environ.get("E2E_BASE", "http://127.0.0.1:5299")
STATE = "tools/.e2e_state.json"

st = json.load(open(STATE, encoding="utf-8"))
token = st["token"]
gid = sys.argv[1] if len(sys.argv) > 1 else st.get("workGroupId")

req = urllib.request.Request(
    f"{BASE}/ag-ui/group/{gid}/messages?count=30",
    headers={"Authorization": "Bearer " + token})
with urllib.request.urlopen(req, timeout=60) as r:
    data = json.load(r)

msgs = data.get("messages", data) if isinstance(data, dict) else data
for m in msgs:
    who = m.get("senderNickname") or m.get("senderId") or m.get("authorId") or "?"
    atts = m.get("attachments") or []
    att_s = ",".join(a.get("name", "") for a in atts)
    content = (m.get("content") or "").replace("\n", " ")
    print(f"[{m.get('messageId')}] {who} | att=[{att_s}]")
    print(f"    {content[:600]}")
