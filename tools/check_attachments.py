"""检查指定群里本轮新增的附件（含挂到既有消息上的交付产物）。"""
import json
import sys
import urllib.request

BASE = "http://127.0.0.1:5299"
st = json.load(open("tools/.e2e_state.json", encoding="utf-8"))
token = st["token"]
gid = sys.argv[1] if len(sys.argv) > 1 else st.get("workGroupId")

req = urllib.request.Request(f"{BASE}/ag-ui/group/{gid}/messages?count=50",
                             headers={"Authorization": "Bearer " + token})
with urllib.request.urlopen(req, timeout=60) as r:
    data = json.load(r)
msgs = data.get("messages", data) if isinstance(data, dict) else data
for m in msgs:
    for a in (m.get("attachments") or []):
        ct = a.get("contentType", "")
        url = a.get("url")
        req2 = urllib.request.Request(BASE + url, headers={"Authorization": "Bearer " + token})
        try:
            with urllib.request.urlopen(req2, timeout=60) as r2:
                body = r2.read()
            print(f"OK  {a.get('name')}  {a.get('size')} 字节  HTTP 200  合法docx={body[:2] == b'PK'}")
            print(f"    下载: {BASE}{url}")
        except Exception as e:
            print(f"ERR {a.get('name')} {e}")
