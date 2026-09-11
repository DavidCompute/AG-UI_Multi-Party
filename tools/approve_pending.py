"""手工批准最近的待决交互，并检查产物是否生成。

用于排查：中断确实发出，但测试脚本没接住时，链路本身是否正常。
"""
import json
import os
import sys
import time
import urllib.request
import urllib.error

BASE = "http://127.0.0.1:5299"


def call(method, path, body=None, token=None, raw=False, timeout=300):
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


st, res = call("POST", "/ag-ui/user/login", {"username": "david", "password": "lingtong"})
token, uid = res["token"], res["userId"]

# 从日志已知的 interrupt（可经参数覆盖）
iid = sys.argv[1] if len(sys.argv) > 1 else None
gid = sys.argv[2] if len(sys.argv) > 2 else None
if not iid or not gid:
    print("用法: python tools/approve_pending.py <interruptId> <groupId>")
    sys.exit(1)

print(f"[1] 批准 interrupt={iid} group={gid}")
st, res = call("POST", "/ag-ui/group/interaction/resolve", {
    "groupId": gid, "interruptId": iid, "memberId": uid,
    "approved": True, "approveAll": True,
}, token=token)
print("    结果:", st, str(res)[:200])

st, res = call("GET", f"/ag-ui/group/{gid}/messages?count=50", token=token)
msgs = res.get("messages", res) if isinstance(res, dict) else res
before = {m.get("messageId") for m in (msgs or [])}

for i in range(60):
    time.sleep(5)
    st, res = call("GET", f"/ag-ui/group/{gid}/messages?count=50", token=token)
    msgs = res.get("messages", res) if isinstance(res, dict) else res
    new = [m for m in (msgs or []) if m.get("messageId") not in before]
    hit = next((m for m in new if m.get("attachments")
                and m["attachments"][0].get("contentType", "").startswith("application/vnd.openxml")), None)
    if hit:
        a = hit["attachments"][0]
        st, body = call("GET", a["url"], token=token, raw=True)
        print(f"\n[OK] Word（{(i+1)*5}s）: {a['name']} {a['size']} 字节")
        print(f"     下载: {BASE}{a['url']} → HTTP {st} 合法docx={isinstance(body, bytes) and body[:2] == b'PK'}")
        sys.exit(0)
    if i % 3 == 2:
        txt = new[-1].get("content") if new else ""
        print(f"    ...{(i+1)*5}s 新增{len(new)}条 {('| ' + txt[:120].replace(chr(10),' ')) if txt else ''}")

print("\n[FAIL] 超时无产物")
sys.exit(1)
