"""诊断：编排器是否产出「能交付最终文件」的岗位结构。

跑多个不同需求，检查每个方案里：
 - 有没有挂着 docx_*/xlsx_* 等「能产出文件」技能的岗位；
 - 该岗位是否处于交付链末端（无下级）；
 - 顶层主管到交付岗的链路是否连通。
"""
import json
import urllib.request
import urllib.error

BASE = "http://127.0.0.1:5299"


def call(method, path, body=None, token=None, timeout=900):
    data = json.dumps(body, ensure_ascii=False).encode() if body is not None else None
    headers = {"Content-Type": "application/json; charset=utf-8"} if data else {}
    if token:
        headers["Authorization"] = "Bearer " + token
    req = urllib.request.Request(BASE + path, data=data, headers=headers, method=method)
    try:
        with urllib.request.urlopen(req, timeout=timeout) as r:
            return r.status, json.loads(r.read() or b"{}")
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode("utf-8", "replace")


st, res = call("POST", "/ag-ui/user/login", {"username": "david", "password": "lingtong"})
token = res["token"]

CASES = [
    "打造一个ai产品文案推广的团队（只负责推广文案相关事宜)，我要最终形成word文档",
    "做一个市场周报团队，每周把数据整理成 Excel 表发给我",
    "做一个合同审核团队，最终输出审核意见的 Word 文档",
]

FILE_PREFIXES = ("docx_", "xlsx_", "pptx_", "pdf_", "md_to_docx")

for req in CASES:
    st, res = call("POST", "/ag-ui/agents/orchestrate", {"requirement": req}, token=token)
    if st != 200:
        print(f"\n需求: {req}\n  编排失败 {st}: {str(res)[:200]}")
        continue

    agents = res.get("agents") or []
    skills = {s.get("skillId"): s for s in (res.get("skills") or [])}
    print("\n" + "=" * 72)
    print("需求:", req)
    print(f"组织「{res.get('title')}」{len(agents)} 岗 / 新造 {len(skills)} 技能")

    # 找能产出文件的岗位
    deliverers = []
    for a in agents:
        ids = a.get("skillIds") or []
        hit = [i for i in ids if str(i).lower().startswith(FILE_PREFIXES)]
        if hit:
            deliverers.append((a, hit))

    print("  岗位结构:")
    for a in agents:
        ids = " ".join(a.get("skillIds") or [])
        down = len(a.get("assignmentIds") or [])
        up = a.get("escalationAgentId") or "-"
        mark = "  <<< 可交付" if any(str(i).lower().startswith(FILE_PREFIXES) for i in (a.get("skillIds") or [])) else ""
        print(f"    {str(a.get('agentId')):20} {str(a.get('nickname')):14} 下级={down} 上报={str(up):20}{mark}")
        print(f"        skills: {ids}")

    if not deliverers:
        print("  ✗ 问题：没有任何岗位具备「产出文件」的技能 → 用户拿不到交付物")
    else:
        for a, hit in deliverers:
            no_down = not (a.get("assignmentIds") or [])
            print(f"  ✓ 交付岗 {a.get('agentId')}（{a.get('nickname')}）技能={hit}"
                  f"{'（叶子岗，链条末端）' if no_down else '（有下级，可能又派下去）'}")
