"""实盘验证：内置 pptx_deck 技能能否经真实对话产出可用的 .pptx。

流程：
  1) 登录 → 确认技能库里已有 pptx_deck
  2) 建一个临时数字员工（挂 pptx_deck）→ 与它单聊
  3) 后台自动批准（dotnet 技能强制审批）
  4) 等产物附件 → 下载 → 校验 zip 结构 / 幻灯片数 → 清理临时员工
"""
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
AGENT_ID = os.environ.get("E2E_AGENT_ID") or ("pptx_verify_" + str(int(time.time()))[-6:])
QUESTION = os.environ.get(
    "E2E_QUESTION",
    "请做一份《知聚平台介绍》的 PPT，8 页左右：封面、目录、产品定位、核心能力、"
    "一张对比表格、一个能力增长柱状图、小结、结束页。",
)


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


st, res = call("POST", "/ag-ui/user/login", {"username": USER, "password": PWD})
assert st == 200, res
token, uid = res["token"], res["userId"]
print(f"[1] 已登录 userId={uid}")

# ---- 技能库里是否有 pptx_deck ----
st, skills = call("GET", "/ag-ui/skills", token=token)
skills = skills.get("skills", skills) if isinstance(skills, dict) else skills
deck = next((s for s in (skills or []) if s.get("skillId") == "pptx_deck"), None)
if deck is None:
    print("✗ 技能库里没有 pptx_deck（内置技能未播种）")
    sys.exit(1)
print(f"[2] 技能库已内置：{deck.get('skillId')} / {deck.get('name')}（kind={deck.get('kind')}，"
      f"审批={deck.get('requiresApproval')}，正文 {len(deck.get('body') or '')} 字符）")

# ---- 建临时数字员工并挂该技能 ----
st, res = call("POST", "/ag-ui/agents", {
    "agentId": AGENT_ID,
    "nickname": "PPT 交付验证员",
    "description": "临时验证用：把内容整理成 PowerPoint 演示文稿。",
    "instructions": "用户要演示文稿时，直接调用 pptx_deck 技能产出 .pptx 文件。",
    "triggerMode": "AllMessages",
    "skillDefIds": ["pptx_deck"],
    "assignmentIds": [],
    "escalationAgentId": None,
    "relayToAgentId": None,
}, token=token)
created = st == 200
print(f"[3] 建临时员工：HTTP {st} {'（已创建）' if created else str(res)[:200]}")

try:
    st, res = call("POST", "/ag-ui/agents/direct", {"agentId": AGENT_ID}, token=token)
    gid = res["groupId"]
    print(f"[4] 单聊群：{gid}")

    # 注册返回必须校验：早期不校验时曾出现“员工建了但没触发规则”，表现为消息发出去无人回应
    st, reg = call("POST", "/ag-ui/agents/register", {
        "agentId": AGENT_ID, "groupId": gid, "triggerMode": "AllMessages",
        "nickname": "PPT 交付验证员", "override": True,
    }, token=token)
    if st != 200:
        print(f"✗ 触发规则注册失败：HTTP {st} {str(reg)[:200]}")
        sys.exit(1)

    before = call("GET", f"/ag-ui/group/{gid}/messages?count=50", token=token)[1]
    before = before.get("messages", before) if isinstance(before, dict) else before
    base_atts = {a.get("url") for m in (before or []) for a in (m.get("attachments") or []) if a.get("url")}

    # ---- 后台自动批准（dotnet 技能强制审批）----
    stop = threading.Event()

    def auto_approve():
        import asyncio

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

    st, _ = call("POST", "/ag-ui/group/message/send", {"groupId": gid, "content": QUESTION}, token=token)
    print(f"[5] 已发送（{st}）：{QUESTION}")

    pptx = None
    for i in range(150):  # 最多 12.5 分钟
        time.sleep(5)
        msgs = call("GET", f"/ag-ui/group/{gid}/messages?count=50", token=token)[1]
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
    print(f"    结构：{len(z.namelist())} 个条目；幻灯片 {len(slides)} 页；"
          f"母版={'ppt/slideMasters/' in ' '.join(z.namelist())}；"
          f"版式={'ppt/slideLayouts/' in ' '.join(z.namelist())}")
    text = z.read("ppt/presentation.xml").decode("utf-8")
    assert "sldIdLst" in text, "presentation.xml 缺 sldIdLst"
    print("    presentation.xml 含 sldIdLst ✓")
    print("\n结果：通过 ✓")
finally:
    stop = globals().get("stop")
    st, _ = call("DELETE", f"/ag-ui/agents/{AGENT_ID}", token=token)
    print(f"[清理] 删除临时员工：HTTP {st}")
