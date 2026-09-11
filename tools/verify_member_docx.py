"""对指定的执行岗跑完整验证（步骤 7-9），确保只认本次新增消息。"""
import asyncio
import json
import os
import sys
import time
import urllib.request
import urllib.error

import websockets

BASE = os.environ.get("E2E_BASE", "http://127.0.0.1:5299")
WS = BASE.replace("http://", "ws://") + "/ws"
ATTACH = r"C:\Users\david\src\AG-UI_Multi-Party\MARKETING.md"
TARGET = os.environ.get("E2E_TARGET", "word_typesetter")


def call(method, path, body=None, token=None, raw=False, timeout=900):
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


def upload(path, token):
    boundary = "----fin" + str(int(time.time()))
    with open(path, "rb") as f:
        content = f.read()
    body = b""
    body += ("--" + boundary + "\r\n").encode()
    body += (f'Content-Disposition: form-data; name="file"; filename="{os.path.basename(path)}"\r\n').encode()
    body += b"Content-Type: text/markdown\r\n\r\n" + content
    body += ("\r\n--" + boundary + "--\r\n").encode()
    req = urllib.request.Request(BASE + "/ag-ui/upload", data=body, method="POST")
    req.add_header("Content-Type", "multipart/form-data; boundary=" + boundary)
    req.add_header("Authorization", "Bearer " + token)
    try:
        with urllib.request.urlopen(req, timeout=180) as r:
            return r.status, json.loads(r.read() or b"{}")
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode("utf-8", "replace")


async def main():
    st, res = call("POST", "/ag-ui/user/login", {"username": "david", "password": "lingtong"})
    token, uid = res["token"], res["userId"]

    st, res = call("GET", "/ag-ui/agents", token=token)
    agents = res.get("agents", res) if isinstance(res, dict) else res
    a = next((x for x in agents if x["agentId"] == TARGET), None)
    if not a:
        print("找不到目标员工:", TARGET)
        return 1
    print(f"[1] 目标：{a['agentId']} / {a.get('nickname')} triggerMode={a.get('triggerMode')}")
    print(f"    技能：{a.get('skillDefIds')} | 上报：{a.get('escalationAgentId')}")

    st, res = call("POST", "/ag-ui/agents/direct", {"agentId": TARGET}, token=token)
    gid = res["groupId"]
    print("[2] 单聊群:", gid)
    call("POST", "/ag-ui/agent/register", {
        "agentId": TARGET, "groupId": gid, "triggerMode": "AllMessages",
        "nickname": a.get("nickname") or "", "override": True,
    }, token=token)

    st, res = upload(ATTACH, token)
    att = res["attachments"][0]
    print("[3] 附件:", att["name"], att["size"], "字节")

    st, res = call("GET", f"/ag-ui/group/{gid}/messages?count=50", token=token)
    msgs = res.get("messages", res) if isinstance(res, dict) else res
    before = {m.get("messageId") for m in (msgs or [])}
    print(f"    基线消息 {len(before)} 条")

    st, res = call("POST", "/ag-ui/group/message/send", {
        "groupId": gid, "content": '根据附件帮我写“知聚”市场推广文案，我需要word文档', "attachments": [att],
    }, token=token)
    print("[4] 已发送:", st)

    interrupt = None
    async with websockets.connect(f"{WS}?memberId={uid}&token={token}") as ws:
        await ws.send(json.dumps({"type": "GROUP_SUBSCRIBE", "groupIds": [gid]}))
        deadline = time.time() + 600
        text = ""
        while time.time() < deadline:
            try:
                raw = await asyncio.wait_for(ws.recv(), timeout=25)
            except asyncio.TimeoutError:
                continue
            except Exception:
                break
            try:
                ev = json.loads(raw)
            except Exception:
                continue
            t = ev.get("type", "")
            p = ev.get("payload") or ev
            if isinstance(p, dict) and p.get("interruptId") and "INTERACTION" in t.upper():
                interrupt = p["interruptId"]
                print(f"[5] 审批中断 tool={p.get('toolName')}")
                break
            if t == "TEXT_MESSAGE_CONTENT":
                text += ev.get("delta") or ""
            if t == "TEXT_MESSAGE_END" and text:
                print("[5] 消息结束（无审批）:", text[:250].replace("\n", " "))
                break

    if interrupt:
        st, res = call("POST", "/ag-ui/group/interaction/resolve", {
            "groupId": gid, "interruptId": interrupt, "memberId": uid,
            "approved": True, "approveAll": True,
        }, token=token)
        print("[6] 已批准:", st)

    for i in range(90):
        await asyncio.sleep(5)
        st, res = call("GET", f"/ag-ui/group/{gid}/messages?count=50", token=token)
        msgs = res.get("messages", res) if isinstance(res, dict) else res
        new = [m for m in (msgs or []) if m.get("messageId") not in before]
        hit = next((m for m in new if m.get("attachments")
                    and m["attachments"][0].get("contentType", "").startswith("application/vnd.openxml")), None)
        if hit:
            a2 = hit["attachments"][0]
            st, body = call("GET", a2["url"], token=token, raw=True)
            print(f"\n[OK] Word 附件（{(i+1)*5}s）")
            print(f"     文件名: {a2['name']}")
            print(f"     大小  : {a2['size']} 字节")
            print(f"     下载  : {BASE}{a2['url']}")
            print(f"     校验  : HTTP {st} | 合法 docx: {isinstance(body, bytes) and body[:2] == b'PK'}")
            if isinstance(body, bytes) and body[:2] == b"PK":
                import io, zipfile, re
                z = zipfile.ZipFile(io.BytesIO(body))
                xml = z.read("word/document.xml").decode("utf-8")
                texts = [t for t in re.findall(r"<w:t[^>]*>([^<]*)</w:t>", xml) if t.strip()]
                print(f"     段落数: {len(texts)}")
                print("     开头  :")
                for t in texts[:8]:
                    print("        ", t[:80])
            return 0
        if i % 6 == 5:
            print(f"    ...{(i+1)*5}s 新增 {len(new)} 条")

    print("\n[FAIL] 未生成 Word")
    st, res = call("GET", f"/ag-ui/group/{gid}/messages?count=50", token=token)
    msgs = res.get("messages", res) if isinstance(res, dict) else res
    for m in [m for m in (msgs or []) if m.get("messageId") not in before][-4:]:
        print("   -", (m.get("content") or "")[:200].replace("\n", " "))
    return 1


sys.exit(asyncio.run(main()))
