"""实盘端到端：一键编排（要 PPT）→ 落库 → 与交付岗单聊 → 真的产出 .pptx → 清理。

用法：
  PYTHONIOENCODING=utf-8 python tools/verify_pptx_orchestration_e2e.py
环境变量：
  AGUI_BASE（默认 http://127.0.0.1:5200）、AGUI_USER、AGUI_PWD、
  E2E_KEEP=1 保留落库产物（默认清理：删除本轮新建的数字员工）
"""
import asyncio
import io
import json
import os
import re
import sys
import threading
import time
import urllib.error
import urllib.request
import zipfile

import websockets

BASE = os.environ.get("AGUI_BASE", "http://127.0.0.1:5200")
USER = os.environ.get("AGUI_USER", "david")
PWD = os.environ.get("AGUI_PWD", "lingtong")
KEEP = os.environ.get("E2E_KEEP") == "1"
TEAM_KEY = os.environ.get("E2E_TEAM_KEY") or ("pptx_team_" + str(int(time.time()))[-6:])
REQUIREMENT = os.environ.get(
    "E2E_REQUIREMENT",
    "打造一个做产品发布演示文稿的团队：最终要交付一份 .pptx 文件（封面、目录、章节、正文、"
    "一页对比表格、一页柱状图、小结、结束页），团队负责从资料收集到成稿。",
)
ASK = os.environ.get(
    "E2E_ASK",
    "请做一份《知聚平台产品发布》的 PPT，8 页左右：封面、目录、产品定位、核心能力、"
    "一张能力对比表格、一个增长柱状图、小结、结束页。",
)


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


st, res = call("POST", "/ag-ui/user/login", {"username": USER, "password": PWD})
assert st == 200, res
token, uid = res["token"], res["userId"]
print(f"[1] 已登录 userId={uid}")

st, plan = call("POST", "/ag-ui/agents/orchestrate", {"requirement": REQUIREMENT}, token=token)
assert st == 200, plan
agents = plan.get("agents") or []
delivery = [a for a in agents if "pptx_deck" in (a.get("skillIds") or [])]
if not delivery:
    print("✗ 方案里没有引用 pptx_deck 的岗位")
    sys.exit(1)
target = delivery[-1]  # 取最末（通常是最下游交付岗）
print(f"[2] 方案「{plan.get('title')}」岗位 {len(agents)} 个；交付岗 = {target.get('agentId')} / {target.get('nickname')}")

st, applied = call("POST", "/ag-ui/agents/orchestrate/apply", {
    "title": plan.get("title"),
    "agents": plan.get("agents"),
    "skills": plan.get("skills"),
    "createSupportCircle": False,
}, token=token)
if st != 200:
    print(f"✗ 落库失败：HTTP {st} {str(applied)[:500]}")
    sys.exit(1)
# apply 返回的 agents 是最终 agentId 字符串数组（落库时可能为避让改名）
created_ids = [x for x in (applied.get("agents") or []) if isinstance(x, str)]
print(f"[3] 已落库：{len(created_ids)} 个数字员工 {created_ids}")

AGENT_ID = target.get("agentId")
if AGENT_ID not in created_ids:
    # 被改名避让：按昵称回查最终 id
    st2, defs = call("GET", "/ag-ui/agents/", token=token)
    defs = defs.get("agents", defs) if isinstance(defs, dict) else defs
    hit = next((d for d in (defs or []) if (d.get("agentId") in created_ids)
                and d.get("nickname") == target.get("nickname")), None)
    if hit is None:
        print(f"✗ 落库后找不到交付岗（原 id={AGENT_ID}）")
        sys.exit(1)
    AGENT_ID = hit["agentId"]
print(f"     交付岗最终 id = {AGENT_ID}")
try:
    st, res = call("POST", "/ag-ui/agents/direct", {"agentId": AGENT_ID}, token=token)
    gid = res["groupId"]
    print(f"[4] 单聊群：{gid}")

    st, reg = call("POST", "/ag-ui/agents/register", {
        "agentId": AGENT_ID, "groupId": gid, "triggerMode": target.get("triggerMode") or "mentioned",
        "nickname": target.get("nickname"), "override": True,
    }, token=token)
    if st != 200:
        print(f"✗ 触发规则注册失败：HTTP {st} {str(reg)[:200]}")
        sys.exit(1)

    before = call("GET", f"/ag-ui/group/{gid}/messages?count=50", token=token)[1]
    before = before.get("messages", before) if isinstance(before, dict) else before
    base_atts = {a.get("url") for m in (before or []) for a in (m.get("attachments") or []) if a.get("url")}

    stop = threading.Event()

    def auto_approve():
        async def run():
            ws_url = BASE.replace("http://", "ws://") + f"/ws?memberId={uid}&token={token}"
            seen = set()
            try:
                async with websockets.connect(ws_url) as ws:
                    await ws.send(json.dumps({"type": "GROUP_SUBSCRIBE", "groupIds": [gid]}))
                    while not stop.is_set():
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
                        st2, _ = call("POST", "/ag-ui/group/interaction/resolve", {
                            "groupId": gid, "interruptId": iid, "memberId": uid,
                            "approved": True, "approveAll": True,
                        }, token=token)
                        print(f"    [自动批准] {iid} → {st2}")
            except Exception as e:
                print(f"    [自动批准] 退出: {type(e).__name__}: {e}")

        asyncio.run(run())

    threading.Thread(target=auto_approve, daemon=True).start()

    st, _ = call("POST", "/ag-ui/group/message/send", {"groupId": gid, "content": ASK}, token=token)
    print(f"[5] 已发送（{st}）：{ASK}")

    pptx = None
    for i in range(180):  # 最多 15 分钟
        time.sleep(5)
        msgs = call("GET", f"/ag-ui/group/{gid}/messages?count=60", token=token)[1]
        msgs = msgs.get("messages", msgs) if isinstance(msgs, dict) else msgs
        new = [a for m in (msgs or []) for a in (m.get("attachments") or [])
               if a.get("url") and a["url"] not in base_atts]
        hit = next((a for a in new if str(a.get("name", "")).lower().endswith(".pptx")), None)
        if hit:
            pptx = hit
            break
        if i % 6 == 5:
            last = (msgs or [])[-1].get("content") if msgs else ""
            print(f"    ...{(i+1)*5}s {((last or '')[:110]).replace(chr(10), ' ')}")

    stop.set()

    if not pptx:
        print("\n✗ 超时未产出 .pptx")
        sys.exit(1)

    st, body = call("GET", pptx["url"], token=token, raw=True)
    ok_zip = isinstance(body, bytes) and body[:2] == b"PK"
    print(f"\n[6] ✓ 产出：{pptx['name']}（{pptx.get('size')} 字节）HTTP {st} 合法zip={ok_zip}")
    print(f"    下载地址：{BASE}{pptx['url']}")
    if not ok_zip:
        sys.exit(1)

    z = zipfile.ZipFile(io.BytesIO(body))
    slides = [e for e in z.namelist() if re.fullmatch(r"ppt/slides/slide\d+\.xml", e)]
    texts = sum(len(re.findall(r"<a:t>", z.read(s).decode("utf-8"))) for s in slides)
    print(f"    结构：{len(z.namelist())} 条目；幻灯片 {len(slides)} 页；文本段 {texts} 个；"
          f"母版={'ppt/slideMasters/' in ' '.join(z.namelist())}")
    assert slides, "无幻灯片"
    print("\n结果：通过 ✓")
finally:
    if KEEP:
        print("[清理] E2E_KEEP=1，保留落库产物")
    else:
        for aid in created_ids:
            st, _ = call("DELETE", f"/ag-ui/agents/{aid}", token=token)
            print(f"[清理] 删除数字员工 {aid}：HTTP {st}")
