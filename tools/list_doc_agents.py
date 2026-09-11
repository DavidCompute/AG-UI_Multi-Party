"""列出实例里所有数字员工及其技能，标出谁具备文档生成能力。"""
import json
import os
import sys
import urllib.request

BASE = os.environ.get("E2E_BASE", "http://127.0.0.1:5200")
USER = os.environ.get("AGUI_USER", "david")
PWD = os.environ.get("AGUI_PWD", "lingtong")

DOCX_PREFIXES = ("docx_", "xlsx_", "pptx_", "pdf_")


def call(method, path, body=None, token=None):
    data = json.dumps(body, ensure_ascii=False).encode() if body is not None else None
    headers = {"Content-Type": "application/json; charset=utf-8"} if data else {}
    if token:
        headers["Authorization"] = "Bearer " + token
    req = urllib.request.Request(BASE + path, data=data, headers=headers, method=method)
    with urllib.request.urlopen(req, timeout=120) as r:
        return json.loads(r.read() or b"{}")


def login():
    try:
        res = call("POST", "/ag-ui/user/login", {"username": USER, "password": PWD})
        return res["token"]
    except Exception as e:
        print(f"登录失败: {e}")
        sys.exit(1)


token = login()

agents = call("GET", "/ag-ui/agents", token=token)
agents = agents.get("agents", agents) if isinstance(agents, dict) else agents

skills = call("GET", "/ag-ui/skills", token=token)
skills = skills.get("skills", skills) if isinstance(skills, dict) else skills
skill_kind = {s.get("skillId"): s.get("kind") for s in (skills or [])}

print(f"共 {len(agents or [])} 个数字员工\n")
print("== 具备文档/表格生成能力的数字员工 ==")
found = False
for a in agents or []:
    ids = a.get("skillDefIds") or []
    hits = [i for i in ids if any((i or "").startswith(p) for p in DOCX_PREFIXES)]
    if hits:
        found = True
        kinds = ", ".join(f"{h}({skill_kind.get(h, '?')})" for h in hits)
        print(f"  {a.get('agentId'):24} {a.get('nickname')}")
        print(f"      交付技能: {kinds}")
        print(f"      职责    : {(a.get('description') or '')[:90]}")
if not found:
    print("  （没有找到）")

print("\n== 全部数字员工概览 ==")
for a in agents or []:
    ids = a.get("skillDefIds") or []
    print(f"  {a.get('agentId'):24} {str(a.get('nickname')):16} skills={ids}")
