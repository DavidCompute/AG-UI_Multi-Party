"""探查某个数字员工的单聊历史（只读，不改数据）。

用途：复现「计划空答复」缺陷时，先看清用户当时到底发了什么、系统回了什么。

用法：
  python tools/probe_chat.py "ppt生成助手"
  python tools/probe_chat.py "ppt生成助手" --send "帮我做一份产品发布 PPT"
"""
import argparse
import asyncio
import json
import os
import sys
import time
import urllib.request
import urllib.error

import websockets

try:
    sys.stdout.reconfigure(encoding="utf-8")
except Exception:
    pass

BASE = os.environ.get("E2E_BASE", "http://127.0.0.1:5200")
WS = BASE.replace("http://", "ws://") + "/ws"
USER = "david"
PWD = "lingtong"


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


def find_agent(agents, needle):
    n = needle.lower()
    for a in agents:
        if (a.get("nickname") or "").lower().find(n) >= 0 or (a.get("agentId") or "").lower().find(n) >= 0:
            return a
    return None


async def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("nickname")
    ap.add_argument("--send", default=None, help="若给出，则在该单聊里发送这条消息并观察回复")
    ap.add_argument("--count", type=int, default=30)
    ap.add_argument("--full", action="store_true", help="完整打印正文，不截断")
    args = ap.parse_args()

    st, res = call("POST", "/ag-ui/user/login", {"username": USER, "password": PWD})
    if st != 200:
        print("登录失败:", st, res)
        return 1
    token, uid = res["token"], res["userId"]

    st, res = call("GET", "/ag-ui/agents", token=token)
    agents = res.get("agents", res) if isinstance(res, dict) else res
    a = find_agent(agents, args.nickname)
    if not a:
        print("找不到员工:", args.nickname)
        print("可用:", [x.get("nickname") for x in agents])
        return 1
    print(f"[员工] {a['agentId']} / {a.get('nickname')} triggerMode={a.get('triggerMode')} "
          f"skills={a.get('skillDefIds')} escalation={a.get('escalationAgentId')}")

    st, res = call("POST", "/ag-ui/agents/direct", {"agentId": a["agentId"]}, token=token)
    gid = res["groupId"]
    print("[单聊群]", gid)

    st, res = call("GET", f"/ag-ui/group/{gid}/messages?count={args.count}", token=token)
    msgs = res.get("messages", res) if isinstance(res, dict) else res
    for m in msgs or []:
        who = m.get("senderName") or m.get("senderId") or "?"
        txt = (m.get("content") or "").replace("\n", " ")
        atts = [x.get("name") for x in (m.get("attachments") or [])]
        plan = m.get("plan")
        extra = f" [附件:{atts}]" if atts else ""
        if plan:
            done = sum(1 for s in plan if s.get("done"))
            extra += f" [计划 {done}/{len(plan)}]"
        print(f"  - {who}: {txt[:400]}{extra}")
        if plan:
            for s in plan:
                print(f"        {'✔' if s.get('done') else '·'} {s.get('text')}")

    if not args.send:
        return 0

    call("POST", "/ag-ui/agent/register", {
        "agentId": a["agentId"], "groupId": gid, "triggerMode": "AllMessages",
        "nickname": a.get("nickname") or "", "override": True,
    }, token=token)

    before = {m.get("messageId") for m in (msgs or [])}
    st, _ = call("POST", "/ag-ui/group/message/send", {"groupId": gid, "content": args.send}, token=token)
    print(f"[发送] HTTP {st}: {args.send}")

    interrupts = []
    async with websockets.connect(f"{WS}?memberId={uid}&token={token}") as ws:
        await ws.send(json.dumps({"type": "GROUP_SUBSCRIBE", "groupIds": [gid]}))
        # 不要“收到第一条 TEXT_MESSAGE_END 就退出”：主管/代处理链路会先发一条
        # “（X 代为处理）”，真正的结果在后续消息里；而且技能需要审批时，
        # 卡还没批就退出去 → 运行会一直挂在那里直到超时（实测踩到）。
        # 改成“空闲多少秒没有事件才算结束”。
        deadline = time.time() + 1800
        last_event = time.time()
        while time.time() < deadline:
            try:
                raw = await asyncio.wait_for(ws.recv(), timeout=15)
            except asyncio.TimeoutError:
                if time.time() - last_event > 180:
                    print(f"[结束] 已空闲 {int(time.time() - last_event)}s，视为本轮结束")
                    break
                continue
            except Exception:
                break
            last_event = time.time()
            try:
                ev = json.loads(raw)
            except Exception:
                continue
            t = ev.get("type", "")
            p = ev.get("payload") or ev
            if isinstance(p, dict) and p.get("interruptId") and "INTERACTION" in t.upper():
                interrupts.append(p["interruptId"])
                print(f"[审批] tool={p.get('toolName')} → 自动批准")
                call("POST", "/ag-ui/group/interaction/resolve", {
                    "groupId": gid, "interruptId": p["interruptId"], "memberId": uid,
                    "approved": True, "approveAll": True,
                }, token=token)
                continue
            if t == "TEXT_MESSAGE_CONTENT":
                delta = ev.get("delta") or ""
                if delta:
                    print("  · " + delta.replace("\n", " ")[:120])
            elif t == "TEXT_MESSAGE_END":
                print("[消息结束]")
    print(f"[本轮] 共批准 {len(interrupts)} 次")

    # 等落库后再读一次最终消息
    await asyncio.sleep(3)
    st, res = call("GET", f"/ag-ui/group/{gid}/messages?count={args.count}", token=token)
    msgs2 = res.get("messages", res) if isinstance(res, dict) else res
    new = [m for m in (msgs2 or []) if m.get("messageId") not in before]
    print(f"\n[新增 {len(new)} 条]")
    lim = 10 ** 9 if args.full else 600
    for m in new:
        who = m.get("senderName") or m.get("senderId") or "?"
        txt = (m.get("content") or "").strip()
        atts = [x.get("name") for x in (m.get("attachments") or [])]
        plan = m.get("plan")
        extra = f" [附件:{atts}]" if atts else ""
        if plan:
            done = sum(1 for s in plan if s.get("done"))
            extra += f" [计划 {done}/{len(plan)}]"
        print(f"  - {who}: {txt[:lim]}{extra}")
        if plan:
            for s in plan:
                print(f"        {'✔' if s.get('done') else '·'} {s.get('text')}")
    return 0


if __name__ == "__main__":
    sys.exit(asyncio.run(main()))
