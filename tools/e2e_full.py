"""端到端测试（真实实例）：
 1. 清空所有数据（全新隔离实例，天然为空）
 2. 注册管理员账号 david / lingtong
 3. 恢复内置组织工具
 4. 与「组织架构构建师」单聊，提出建团请求
 5. 拿到草稿后回复「落库」
 6. 等组织架构落库完毕
 7. 找到合适的团队成员，与之单聊
 8. 带上附件 MARKETING.md，提问「根据附件帮我写"知聚"市场推广文案，我需要word文档」
 9. 校验生成 Word 文档并提供下载链接

这是分步驱动的脚本：每一步都独立可重跑、可人工介入。
用法：
  python tools/e2e_full.py            # 跑到当前步骤并停下，打印下一步指引
  python tools/e2e_full.py --all      # 一路跑到底
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

BASE = os.environ.get("E2E_BASE", "http://127.0.0.1:5299")
WS = BASE.replace("http://", "ws://") + "/ws"
USER = "david"
PWD = "lingtong"
ATTACH = os.environ.get("E2E_ATTACH", r"C:\Users\david\src\AG-UI_Multi-Party\MARKETING.md")
STATE = "tools/.e2e_state.json"


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


def save_state(**kw):
    st = {}
    if os.path.exists(STATE):
        try:
            st = json.load(open(STATE, encoding="utf-8"))
        except Exception:
            st = {}
    st.update(kw)
    with open(STATE, "w", encoding="utf-8") as f:
        json.dump(st, f, ensure_ascii=False, indent=2)


def load_state():
    if not os.path.exists(STATE):
        return {}
    try:
        return json.load(open(STATE, encoding="utf-8"))
    except Exception:
        return {}


def step(n, title):
    print("\n" + "=" * 72)
    print(f"步骤 {n}：{title}")
    print("=" * 72)


# ---------------------------------------------------------------- 步骤 2

def register_admin():
    step(2, "注册管理员账号 david")
    st, res = call("POST", "/ag-ui/user/register", {"username": USER, "password": PWD, "nickname": "David"})
    if st != 200:
        st, res = call("POST", "/ag-ui/user/login", {"username": USER, "password": PWD})
        if st != 200:
            print("  注册/登录失败:", st, str(res)[:300])
            return None
        print("  账号已存在，直接登录")
    token = res["token"]
    save_state(token=token, userId=res.get("userId"))
    print(f"  登录成功 userId={res.get('userId')} isAdmin={res.get('isAdmin')}")
    if not res.get("isAdmin"):
        print("  !! 该账号不是管理员（实例里可能已有其他用户，首个注册者才是超管）")
    return token


# ---------------------------------------------------------------- 步骤 3

def restore_org_tools(token):
    step(3, "恢复内置组织工具")
    st, res = call("POST", "/ag-ui/agents/restore-org-builder", token=token)
    print("  恢复结果:", st, str(res)[:300])
    if st == 200:
        save_state(orgRestored=res)
    return st == 200


# ---------------------------------------------------------------- 步骤 4

async def step4_start(token):
    step(4, "与「组织架构构建师」单聊，提出建团请求")
    st, res = call("GET", "/ag-ui/agents", token=token)
    agents = res.get("agents", res) if isinstance(res, dict) else res
    builder = None
    for a in agents or []:
        aid = str(a.get("agentId", ""))
        if "org" in aid.lower() and ("build" in aid.lower() or "design" in aid.lower() or "架构" in str(a.get("nickname", ""))):
            builder = a
            break
    if not builder:
        print("  未找到组织架构构建师，现有员工：")
        for a in agents or []:
            print("   -", a.get("agentId"), a.get("nickname"))
        return None

    print(f"  找到：{builder.get('agentId')} / {builder.get('nickname')}")
    st, res = call("POST", "/ag-ui/agents/direct", {"agentId": builder["agentId"]}, token=token)
    gid = res["groupId"]
    save_state(builderAgentId=builder["agentId"], builderGroupId=gid)
    print(f"  单聊群：{gid}")

    # 注册触发规则（全量监听，确保一定触发）
    call("POST", "/ag-ui/agents/register", {
        "agentId": builder["agentId"], "groupId": gid, "triggerMode": "AllMessages",
        "nickname": builder.get("nickname") or "", "override": True,
    }, token=token)

    REQ = "打造一个ai产品文案推广的团队（只负责推广文案相关事宜)，我要最终形成word文档"

    async def send():
        st, res = call("POST", "/ag-ui/group/message/send", {"groupId": gid, "content": REQ}, token=token)
        print(f"  已发送请求（{st}）：{REQ}")

    text = await wait_for_reply(token, gid, timeout=600, send=send)
    if text:
        print("\n  --- 构建师回复（前 1500 字）---")
        print("  " + text[:1500].replace("\n", "\n  "))
        save_state(step4Reply=text)
    return gid


# ---------------------------------------------------------------- 工具：等回复

async def wait_for_reply(token, gid, timeout=600, baseline=None, auto_approve=True, send=None):
    """订阅群，等智能体回复稳定（无新内容 8 秒）后返回其正文。

    send: 可选的 (async) 发送回调。**先把 WS 订阅建好再发消息**——否则交互事件
    （AGENT_INTERACTION_REQUEST）可能在订阅前就广播出去，脚本接不到，
    带审批的交付技能就永远停在交互卡上（实测踩到）。

    auto_approve: 收到 AGENT_INTERACTION_REQUEST 时自动批准（并选“本次运行都批准”），
    让带审批的交付技能（如内置 docx_report）能真正跑完。
    """
    st, res = call("GET", "/ag-ui/group/" + gid + "/messages?count=50", token=token)
    msgs = res.get("messages", res) if isinstance(res, dict) else res
    if baseline is None:
        baseline = {m.get("messageId") for m in (msgs or [])}

    my_uid = load_state().get("userId")
    update = {"__last": time.time()}

    async def approve(iid, run_id):
        st, res = call("POST", "/ag-ui/group/interaction/resolve", {
            "groupId": gid, "interruptId": iid, "memberId": my_uid,
            "approved": True, "approveAll": True,
        }, token=token)
        print(f"    [自动批准] interrupt={iid} run={run_id} → {st} {str(res)[:120]}")

    async with websockets.connect(f"{WS}?memberId={my_uid}&token={token}") as ws:
        await ws.send(json.dumps({"type": "GROUP_SUBSCRIBE", "groupIds": [gid]}))
        await asyncio.sleep(0.5)  # 等订阅确认，避免首发事件漏收
        if send is not None:
            await send()

        deadline = time.time() + timeout
        text = ""
        approved = set()
        agent_msg_ids = set()
        saw_agent = False          # 是否已看到智能体开始作答（在此之前不适用静默结束）
        quiet_need = 45.0          # 静默多久才算“说完了”；思考型模型会在 reasoning 间隙长时间静默
        while time.time() < deadline:
            try:
                raw = await asyncio.wait_for(ws.recv(), timeout=5)
            except asyncio.TimeoutError:
                # 无新事件：已看到智能体、有正文、且静默够久 → 认为本轮说完
                if saw_agent and text and time.time() - update["__last"] > quiet_need:
                    break
                continue
            except Exception as e:
                if os.environ.get("E2E_DEBUG_WS"):
                    print(f"    [WS] 连接异常退出: {type(e).__name__}: {e}")
                break
            try:
                ev = json.loads(raw)
            except Exception:
                continue
            t = ev.get("type", "")
            if os.environ.get("E2E_DEBUG_WS"):
                print(f"    [WS] {t} len={len(raw)}")
                if t in ("TEXT_MESSAGE_START", "TEXT_MESSAGE_END", "AGENT_INTERACTION_REQUEST"):
                    print(f"         {raw[:500]}")
            p = ev.get("payload") or ev
            if t == "TEXT_MESSAGE_START":
                # 只跟踪【数字员工本轮新开的那条消息】；
                # 用户自己发的消息也会回音 TEXT_MESSAGE_START/END，
                # 不过滤会在用户消息 END 时就误判“回复结束”（实测踩到）。
                sender = p.get("senderId") or p.get("agentId") or p.get("memberId") or ""
                mid = p.get("messageId") or ev.get("messageId")
                role = str(p.get("senderType") or p.get("role") or "").lower()
                if sender == my_uid or role == "user":
                    continue
                if mid:
                    agent_msg_ids.add(mid)
                    saw_agent = True
                    update["__last"] = time.time()
            if t == "TEXT_MESSAGE_CONTENT":
                mid = p.get("messageId") or ev.get("messageId")
                # 必须已看到“智能体自己的消息开始”才收正文：
                # 用户自己发的那条也会回音 START/CONTENT/END，不拦就会把用户提问当成回复。
                if not agent_msg_ids or (mid and mid not in agent_msg_ids):
                    continue
                d = (ev.get("delta") or p.get("delta") or "")
                if d:
                    text += d
                    update["__last"] = time.time()
            elif t == "TEXT_MESSAGE_END":
                mid = p.get("messageId") or ev.get("messageId")
                if not agent_msg_ids or (mid and mid not in agent_msg_ids):
                    continue  # 用户消息的 END，不代表智能体回完
                if text:
                    break
            elif t in ("TEXT_MESSAGE_REASONING", "TOOL_CALL_START", "TOOL_CALL_RESULT", "GROUP_TYPING"):
                update["__last"] = time.time()  # 仍在干活：刷新静默计时，不要误判结束
            elif t in ("AGENT_INTERACTION_REQUEST",):
                update["__last"] = time.time()
                iid = p.get("interruptId") or ev.get("interruptId")
                if auto_approve and iid and iid not in approved:
                    approved.add(iid)
                    await approve(iid, p.get("runId") or ev.get("runId"))
        if os.environ.get("E2E_DEBUG_WS"):
            print(f"    [WS] 订阅循环结束 textLen={len(text)} agentMsgs={len(agent_msg_ids)} elapsed={int(time.time()-(deadline-timeout))}s")
        return text.strip()


# ---------------------------------------------------------------- 工具：后台自动批准

async def _auto_approve_loop(token, gid, stop: asyncio.Event):
    """后台订阅群，把收到的 AGENT_INTERACTION_REQUEST 全部批准（含“本次运行都批准”）。

    为什么需要：交付类技能（内置 docx_report）是 dotnet → 强制人工审批。
    而思考型模型的思考过程可能长达数分钟，交互卡可能比 step8 的订阅窗口晚到；
    若没人批准，运行就永远停在卡上，产物永远不会生成。
    """
    my_uid = load_state().get("userId")
    approved = set()
    try:
        async with websockets.connect(f"{WS}?memberId={my_uid}&token={token}") as ws:
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
                if not iid or iid in approved:
                    continue
                approved.add(iid)
                st, res = call("POST", "/ag-ui/group/interaction/resolve", {
                    "groupId": gid, "interruptId": iid, "memberId": my_uid,
                    "approved": True, "approveAll": True,
                }, token=token)
                print(f"    [后台批准] interrupt={iid} → {st} {str(res)[:100]}")
    except Exception as e:
        print(f"    [后台批准] 退出: {type(e).__name__}: {e}")


# ---------------------------------------------------------------- 步骤 5

async def step5_commit(token):
    step(5, "回复「落库」")
    gid = load_state().get("builderGroupId")
    if not gid:
        print("  找不到构建师单聊群，请先跑步骤 4")
        return None

    st, res = call("GET", "/ag-ui/group/" + gid + "/messages?count=50", token=token)
    msgs = res.get("messages", res) if isinstance(res, dict) else res
    baseline = {m.get("messageId") for m in (msgs or [])}

    async def send():
        st, res = call("POST", "/ag-ui/group/message/send", {"groupId": gid, "content": "落库"}, token=token)
        print("  已发送「落库」:", st)

    text = await wait_for_reply(token, gid, timeout=900, baseline=baseline, send=send)
    if text:
        print("\n  --- 落库回复（前 1200 字）---")
        print("  " + text[:1200].replace("\n", "\n  "))
        save_state(step5Reply=text)
    return text


# ---------------------------------------------------------------- 步骤 6

def step6_wait_org(token):
    step(6, "等待组织架构落库完毕")
    for i in range(60):
        st, res = call("GET", "/ag-ui/agents", token=token)
        agents = res.get("agents", res) if isinstance(res, dict) else res
        agents = agents or []
        promo = [a for a in agents if any(k in json.dumps(a, ensure_ascii=False) for k in ("推广", "文案", "promo"))]
        if len(agents) > 5:
            print(f"  检测到 {len(agents)} 个数字员工（疑似已落库），其中推广相关 {len(promo)} 个：")
            for a in promo:
                print(f"    - {a.get('agentId'):26} {a.get('nickname')}  skills={a.get('skillDefIds')}")
            save_state(orgAgents=[{"agentId": a.get("agentId"), "nickname": a.get("nickname"),
                                   "skillDefIds": a.get("skillDefIds"), "assignmentIds": a.get("assignmentIds")} for a in agents])
            return agents
        time.sleep(5)
    print("  等待超时，当前员工数未增长")
    return None


# ---------------------------------------------------------------- 步骤 7

def step7_pick_member(token):
    step(7, "找到合适的团队成员准备单聊")
    st, res = call("GET", "/ag-ui/agents", token=token)
    agents = res.get("agents", res) if isinstance(res, dict) else res
    agents = agents or []

    # 优先选：带 docx/word 技能的；其次带文案/推广技能的；再次顶层主管
    # 且必须属于本次编排出的团队（排除诊断/测试时留下的临时员工）
    def score(a):
        ids = " ".join(a.get("skillDefIds") or []).lower()
        s = 0
        if "docx" in ids or "word" in ids:
            s += 100
        if "md_to_docx" in ids:
            s += 50
        nick = str(a.get("nickname") or "")
        desc = str(a.get("description") or "")
        if any(k in nick + desc for k in ("文案", "内容", "文档", "推广", "排版")):
            s += 30
        # 属于组织架构（有上下级关系）才优先：单打独斗的临时员工不加分
        if a.get("escalationAgentId") or a.get("assignmentIds"):
            s += 15
        return s

    # 只考虑本次落库的团队（orgAgents 快照），避免选中诊断残留的临时员工
    org_ids = {a["agentId"] for a in (load_state().get("orgAgents") or [])}
    pool = [a for a in agents if a.get("agentId") in org_ids] or agents
    ranked = sorted(pool, key=score, reverse=True)
    print("  候选人排序：")
    for a in ranked[:6]:
        print(f"    [{score(a):3}] {a.get('agentId'):26} {a.get('nickname'):12} skills={a.get('skillDefIds')}")

    if not ranked:
        return None
    target = ranked[0]
    print(f"\n  选定：{target.get('agentId')} / {target.get('nickname')}")
    save_state(targetAgentId=target["agentId"], targetNickname=target.get("nickname"))
    return target


# ---------------------------------------------------------------- 步骤 8

async def step8_ask_with_attachment(token):
    step(8, f"带附件单聊：{os.path.basename(ATTACH)}")
    agent_id = load_state().get("targetAgentId")
    if not agent_id:
        print("  找不到目标员工，请先跑步骤 7")
        return None
    if not os.path.exists(ATTACH):
        print("  附件不存在：", ATTACH)
        return None

    st, res = call("POST", "/ag-ui/agents/direct", {"agentId": agent_id}, token=token)
    gid = res["groupId"]
    save_state(workGroupId=gid)
    print(f"  单聊群：{gid}")

    call("POST", "/ag-ui/agents/register", {
        "agentId": agent_id, "groupId": gid, "triggerMode": "AllMessages",
        "nickname": load_state().get("targetNickname") or "", "override": True,
    }, token=token)

    # 先记下【上传前】的基线：上传的 MARKETING.md 本身也是“本轮新增附件”，
    # 必须排除掉，否则会被误判为交付产物（旧脚本踩过类似的坑）。
    st, res = call("GET", "/ag-ui/group/" + gid + "/messages?count=50", token=token)
    pre = res.get("messages", res) if isinstance(res, dict) else res
    save_state(step8Baseline=sorted({m.get("messageId") for m in (pre or [])}))
    save_state(step8BaselineAtts=sorted(
        a.get("url") for m in (pre or []) for a in (m.get("attachments") or []) if a.get("url")))

    # 上传附件
    st, res = upload_file(ATTACH, token)
    if st != 200:
        print("  附件上传失败:", st, str(res)[:300])
        return None
    att = res["attachments"][0]
    print(f"  附件已上传：{att['name']}（{att['size']} 字节）→ {att['url']}")
    save_state(uploadedAttachment=att, uploadedAttachmentUrl=att.get("url"))

    # 上传后的消息集合作为“本次新消息”基线
    st, res = call("GET", "/ag-ui/group/" + gid + "/messages?count=50", token=token)
    msgs = res.get("messages", res) if isinstance(res, dict) else res
    baseline = {m.get("messageId") for m in (msgs or [])}
    save_state(step8Baseline=sorted(baseline))

    QUESTION = '根据附件帮我写“知聚”市场推广文案，我需要word文档'

    async def send():
        st, res = call("POST", "/ag-ui/group/message/send", {
            "groupId": gid, "content": QUESTION, "attachments": [att],
        }, token=token)
        print(f"  已发送（{st}）：{QUESTION}")

    text = await wait_for_reply(token, gid, timeout=1200, baseline=baseline, send=send)
    if text:
        print("\n  --- 回复（前 1500 字）---")
        print("  " + text[:1500].replace("\n", "\n  "))
    save_state(step8Reply=text)
    return gid


def upload_file(path, token):
    """multipart 上传（不引第三方依赖）。端点 /ag-ui/upload，字段名 file。"""
    boundary = "----aguiE2E" + str(int(time.time()))
    with open(path, "rb") as f:
        content = f.read()
    fname = os.path.basename(path)
    body = b""
    body += ("--" + boundary + "\r\n").encode()
    body += (f'Content-Disposition: form-data; name="file"; filename="{fname}"\r\n').encode("utf-8")
    body += b"Content-Type: text/markdown\r\n\r\n"
    body += content
    body += ("\r\n--" + boundary + "--\r\n").encode()

    req = urllib.request.Request(BASE + "/ag-ui/upload", data=body, method="POST")
    req.add_header("Content-Type", "multipart/form-data; boundary=" + boundary)
    req.add_header("Authorization", "Bearer " + token)
    try:
        with urllib.request.urlopen(req, timeout=120) as r:
            return r.status, json.loads(r.read() or b"{}")
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode("utf-8", "replace")


# ---------------------------------------------------------------- 步骤 9

async def step9_verify(token):
    step(9, "检查 Word 文档并给出下载链接")
    gid = load_state().get("workGroupId")
    if not gid:
        print("  找不到工作群，请先跑步骤 8")
        return False

    base_urls = set(load_state().get("step8BaselineAtts") or [])
    if load_state().get("uploadedAttachmentUrl"):
        base_urls.add(load_state()["uploadedAttachmentUrl"])

    def new_attachments(msgs):
        """本轮新增的附件（排除发送前已有的）——交付产物可能挂在既有消息上。"""
        return [a for m in (msgs or []) for a in (m.get("attachments") or [])
                if a.get("url") and a["url"] not in base_urls]

    # 交付技能（如内置 docx_report）会弹审批；step8 里若因为模型思考太久、
    # 交互卡比订阅窗口晚到，这里再挂一个后台补批准任务，保证流程总能跑完。
    approve_stop = asyncio.Event()
    approve_task = asyncio.create_task(_auto_approve_loop(token, gid, approve_stop))

    msgs = None
    atts = []
    try:
        for i in range(144):  # 最多 12 分钟
            st, res = call("GET", "/ag-ui/group/" + gid + "/messages?count=50", token=token)
            msgs = res.get("messages", res) if isinstance(res, dict) else res
            atts = new_attachments(msgs)
            if atts:
                break
            print(f"    ...等待交付产物（{(i+1)*5}s）")
            await asyncio.sleep(5)
    finally:
        approve_stop.set()
        try:
            await asyncio.wait_for(approve_task, timeout=5)
        except Exception:
            approve_task.cancel()

    if not atts:
        print("  ✗ 本次未产生附件")
        base = set(load_state().get("step8Baseline") or [])
        new = [m for m in (msgs or []) if m.get("messageId") not in base] if base else (msgs or [])
        print("  本次新消息：")
        for m in new[-4:]:
            print("    -", (m.get("content") or "")[:220].replace("\n", " "))
        return False

    ok = False
    for att in atts:
        if att.get("contentType", "").startswith("application/vnd.openxmlformats-officedocument.wordprocessingml"):
            st, body = call("GET", att["url"], token=token, raw=True)
            size = len(body) if isinstance(body, bytes) else 0
            is_zip = isinstance(body, bytes) and body[:2] == b"PK"
            print(f"\n  ✓ Word 附件：{att['name']}（{att['size']} 字节）")
            print(f"    下载地址：{BASE}{att['url']}")
            print(f"    下载校验：HTTP {st}，{size} 字节，合法 docx={is_zip}")
            if st == 200 and is_zip:
                import io, zipfile, re
                z = zipfile.ZipFile(io.BytesIO(body))
                xml = z.read("word/document.xml").decode("utf-8")
                texts = [t for t in re.findall(r"<w:t[^>]*>([^<]*)</w:t>", xml) if t.strip()]
                print(f"    文档段落数：{len(texts)}，开头：{texts[:5]}")
                ok = True
    if not ok:
        print("\n  ✗ 附件中没有 Word 文档")
    return ok


# ---------------------------------------------------------------- 主流程

async def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--all", action="store_true", help="一路跑到底")
    ap.add_argument("--from", dest="start", type=int, default=2, help="从第几步开始")
    args = ap.parse_args()

    token = load_state().get("token") or register_admin()
    if not token:
        return 1

    if args.start <= 3:
        restore_org_tools(token)
    if args.start <= 4:
        await step4_start(token)
    if args.start <= 5:
        await step5_commit(token)
        step6_wait_org(token)
    if args.start <= 7:
        step7_pick_member(token)
    if args.start <= 8:
        await step8_ask_with_attachment(token)
    ok = await step9_verify(token)

    print("\n" + "=" * 72)
    print("端到端结果：", "通过 ✓" if ok else "未通过 ✗")
    print("=" * 72)
    return 0 if ok else 1


sys.exit(asyncio.run(main()))
