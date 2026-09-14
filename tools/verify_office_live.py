"""实盘验证：内置产出技能（xlsx_book / pdf_doc / pptx_deck）能否经真实对话产出可下载的成品。

流程（每个格式一次）：
  1) 登录 → 确认技能库里已有该内置技能
  2) 建一个临时数字员工（挂该技能）→ 与它单聊
  3) 后台自动批准（dotnet 技能强制审批）
  4) 等产物附件 → 下载 → 校验结构与体积 → 清理临时员工

用法：
  PYTHONIOENCODING=utf-8 python tools/verify_office_live.py            # 跑 xlsx + pdf
  PYTHONIOENCODING=utf-8 python tools/verify_office_live.py xlsx
环境变量：AGUI_BASE（默认 http://127.0.0.1:5200）、AGUI_USER、AGUI_PWD
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

# 每种格式：技能 id / 员工名 / 提问 / 产物后缀 / 体积上限（防止字体没子集化之类的回归）
FORMATS = {
    "xlsx": {
        "skill": "xlsx_book",
        "nickname": "Excel 交付验证员",
        "ext": ".xlsx",
        "max_bytes": 200_000,
        "question": (
            "请做一份《2026 年第一季度经营分析》Excel：两个工作表。"
            "第一个叫「损益表」，列为：月份、收入、成本、毛利、毛利率；"
            "数据三行（一月/二月/三月，收入 120000/150000/180000，成本 72000/88000/96000），"
            "毛利与毛利率请用 Excel 公式（不要写死数字）；"
            "最后加一行合计。第二个表叫「汇总」，列出总收入，用跨表公式引用损益表。"
        ),
    },
    "pdf": {
        "skill": "pdf_doc",
        "nickname": "PDF 交付验证员",
        "ext": ".pdf",
        "max_bytes": 900_000,
        "question": (
            "请出一份《知聚平台能力白皮书》PDF，docType 用 report，"
            "docType:report，正文要有：一、平台概述（一段正文 + 三条要点）；"
            "二、核心能力（一张三列的表格）；三、增长趋势（一个柱状图）；"
            "四、小结（三条要点）。要封面和目录。"
        ),
    },
    "pptx": {
        "skill": "pptx_deck",
        "nickname": "PPT 交付验证员",
        "ext": ".pptx",
        "max_bytes": 900_000,
        "question": (
            "请做一份《知聚平台介绍》的 PPT，8 页左右：封面、目录、产品定位、核心能力、"
            "一张对比表格、一个能力增长柱状图、小结、结束页。"
        ),
    },
}


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


def run_format(fmt, token, uid):
    spec = FORMATS[fmt]
    ok = True
    print(f"\n{'=' * 60}\n[{fmt}] 技能 {spec['skill']}\n{'=' * 60}")

    st, skills = call("GET", "/ag-ui/skills", token=token)
    skills = skills.get("skills", skills) if isinstance(skills, dict) else skills
    hit = next((s for s in (skills or []) if s.get("skillId") == spec["skill"]), None)
    if hit is None:
        print(f"✗ 技能库里没有 {spec['skill']}（内置未播种）")
        return False
    print(f"[1] 技能库已内置：{hit.get('name')}（正文 {len(hit.get('body') or '')} 字符）")

    agent_id = f"{fmt}_verify_" + str(int(time.time()))[-6:]
    st, res = call("POST", "/ag-ui/agents", {
        "agentId": agent_id,
        "nickname": spec["nickname"],
        "description": f"临时验证用：用 {spec['skill']} 产出可下载的成品文件。",
        "instructions": f"用户要文件时，直接调用 {spec['skill']} 技能产出文件。",
        "triggerMode": "AllMessages",
        "skillDefIds": [spec["skill"]],
        "assignmentIds": [],
        "escalationAgentId": None,
        "relayToAgentId": None,
    }, token=token)
    if st != 200:
        print(f"✗ 建临时员工失败：HTTP {st} {str(res)[:200]}")
        return False

    try:
        st, res = call("POST", "/ag-ui/agents/direct", {"agentId": agent_id}, token=token)
        gid = res["groupId"]
        st, reg = call("POST", "/ag-ui/agents/register", {
            "agentId": agent_id, "groupId": gid, "triggerMode": "AllMessages",
            "nickname": spec["nickname"], "override": True,
        }, token=token)
        if st != 200:
            print(f"✗ 触发规则注册失败：HTTP {st} {str(reg)[:200]}")
            return False
        print(f"[2] 单聊群：{gid}")

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

        st, _ = call("POST", "/ag-ui/group/message/send", {"groupId": gid, "content": spec["question"]}, token=token)
        print(f"[3] 已发送（{st}）：{spec['question'][:60]}…")

        found = None
        for i in range(180):  # 最多 15 分钟
            time.sleep(5)
            msgs = call("GET", f"/ag-ui/group/{gid}/messages?count=60", token=token)[1]
            msgs = msgs.get("messages", msgs) if isinstance(msgs, dict) else msgs
            new = [a for m in (msgs or []) for a in (m.get("attachments") or [])
                   if a.get("url") and a["url"] not in base_atts]
            hit2 = next((a for a in new if str(a.get("name", "")).lower().endswith(spec["ext"])), None)
            if hit2:
                found = hit2
                break
            if i % 6 == 5:
                last = (msgs or [])[-1].get("content") if msgs else ""
                print(f"    ...{(i + 1) * 5}s {((last or '')[:100]).replace(chr(10), ' ')}")

        stop.set()

        if not found:
            print(f"\n✗ 超时未产出 {spec['ext']}")
            return False

        st, body = call("GET", found["url"], token=token, raw=True)
        size = len(body) if isinstance(body, bytes) else 0
        print(f"\n[4] ✓ 产出：{found['name']}（{size} 字节）HTTP {st}")
        print(f"    下载地址：{BASE}{found['url']}")

        if fmt == "pdf":
            head = body[:5] if isinstance(body, bytes) else b""
            ok_head = head == b"%PDF-"
            text = body.decode("latin-1") if isinstance(body, bytes) else ""
            ok_font = "/FontFile" in text
            ok_cid = "/Type0" in text and "CIDFontType" in text
            print(f"    %PDF- 头={ok_head}；嵌入字体={ok_font}；CID 结构={ok_cid}")
            ok = ok and ok_head and ok_font and ok_cid
        else:
            ok_zip = isinstance(body, bytes) and body[:2] == b"PK"
            z = zipfile.ZipFile(io.BytesIO(body)) if ok_zip else None
            names = z.namelist() if z else []
            if fmt == "xlsx":
                sheets = [n for n in names if re.fullmatch(r"xl/worksheets/sheet\d+\.xml", n)]
                wb = z.read("xl/workbook.xml").decode("utf-8") if z and "xl/workbook.xml" in names else ""
                formulas = sum(z.read(s).decode("utf-8").count("<f>") for s in sheets) if z else 0
                print(f"    合法zip={ok_zip}；工作表 {len(sheets)} 个；公式 {formulas} 个；"
                      f"含 styles.xml={'xl/styles.xml' in names}")
                ok = ok and ok_zip and len(sheets) >= 2 and formulas >= 1
            else:
                slides = [n for n in names if re.fullmatch(r"ppt/slides/slide\d+\.xml", n)]
                print(f"    合法zip={ok_zip}；幻灯片 {len(slides)} 页")
                ok = ok and ok_zip and len(slides) >= 3

        if size > spec["max_bytes"]:
            print(f"    ✗ 体积异常：{size} > 上限 {spec['max_bytes']}（疑似字体未子集化等回归）")
            ok = False
        else:
            print(f"    体积 {size} 字节，在上限 {spec['max_bytes']} 之内 ✓")

        print(f"\n{fmt} 结果：" + ("通过 ✓" if ok else "未通过 ✗"))
        return ok
    finally:
        st, _ = call("DELETE", f"/ag-ui/agents/{agent_id}", token=token)
        print(f"[清理] 删除临时员工 {agent_id}：HTTP {st}")


def main():
    want = sys.argv[1:] or ["xlsx", "pdf"]
    st, res = call("POST", "/ag-ui/user/login", {"username": USER, "password": PWD})
    assert st == 200, res
    token, uid = res["token"], res["userId"]
    print(f"已登录 userId={uid}")
    results = {f: run_format(f, token, uid) for f in want}
    print("\n" + "=" * 60)
    for f, ok in results.items():
        print(f"  {f}: " + ("通过 ✓" if ok else "未通过 ✗"))
    sys.exit(0 if all(results.values()) else 1)


if __name__ == "__main__":
    main()
