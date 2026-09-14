"""实盘验证：一键组织编排在「要 Excel / PDF」时是否点名内置技能，且不重复新造。

（与 tools/verify_pptx_orchestration.py 同一职责，覆盖 xlsx / pdf。）
用法：PYTHONIOENCODING=utf-8 python tools/verify_extended_orchestration.py
"""
import json
import os
import sys
import urllib.error
import urllib.request

BASE = os.environ.get("AGUI_BASE", "http://127.0.0.1:5200")
USER = os.environ.get("AGUI_USER", "david")
PWD = os.environ.get("AGUI_PWD", "lingtong")

CASES = [
    ("Excel", "xlsx_book",
     "打造一个做经营数据分析的团队，最终要交付一份 .xlsx 报表，团队负责从取数到成表。"),
    ("PDF", "pdf_doc",
     "打造一个做对外白皮书的团队，最终要交付一份 .pdf 文档（要封面、目录、图表），团队负责从资料到成稿。"),
]


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


def main():
    st, res = call("POST", "/ag-ui/user/login", {"username": USER, "password": PWD})
    assert st == 200, res
    token = res["token"]

    all_ok = True
    for label, builtin, requirement in CASES:
        st, plan = call("POST", "/ag-ui/agents/orchestrate", {"requirement": requirement}, token=token)
        if st != 200:
            print(f"[{label}] ✗ 编排失败：HTTP {st} {str(plan)[:300]}")
            all_ok = False
            continue

        agents = plan.get("agents") or []
        new_skills = plan.get("skills") or []
        users = [a for a in agents if builtin in (a.get("skillIds") or [])]
        dup = [s for s in new_skills if (s or {}).get("skillId") == builtin]

        print(f"[{label}] 方案「{plan.get('title')}」岗位 {len(agents)} 个、新造技能 {len(new_skills)} 个、"
              f"deliveryWarning={plan.get('deliveryWarning')}")
        for a in users:
            print(f"     引用 {builtin}：{a.get('agentId')} / {a.get('nickname')}")
        if not users:
            print(f"     ✗ 没有任何岗位引用 {builtin}")
            all_ok = False
        if dup:
            print(f"     ✗ {builtin} 被当作「新造技能」重复产出（应直接复用内置）")
            all_ok = False
        warning = plan.get("deliveryWarning") or ""
        if plan.get("deliveryWarning"):
            # 交付告警分三类：前三类是“排出来的团队缺交付能力 / 交付能力是空壳”，属接线问题；
            # “流程门卫”是模型把交付岗写成“等定稿/等审批才出文件”的质量问题 —— 检测器在正常千活，
            # 与内置技能是否被复用无关（模型输出每次不同），所以只提示、不判失败。
            if "流程门卫" in warning:
                print(f"     注：交付质量告警（非接线问题）：{warning[:80]}…")
            else:
                print(f"     ✗ 编排未识别到内置交付能力：{warning}")
                all_ok = False

    print("\n结果：" + ("通过 ✓" if all_ok else "未通过 ✗"))
    sys.exit(0 if all_ok else 1)


if __name__ == "__main__":
    main()
