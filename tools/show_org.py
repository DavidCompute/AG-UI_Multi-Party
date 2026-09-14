"""列出实例里的团队结构：岗位、技能、上下级、以及是否有“流程门卫”式人设。"""
import json
import os
import urllib.request

BASE = os.environ.get("AGUI_BASE", "http://127.0.0.1:5200")


def call(method, path, body=None, token=None):
    data = json.dumps(body, ensure_ascii=False).encode() if body is not None else None
    headers = {"Content-Type": "application/json; charset=utf-8"} if data else {}
    if token:
        headers["Authorization"] = "Bearer " + token
    req = urllib.request.Request(BASE + path, data=data, headers=headers, method=method)
    with urllib.request.urlopen(req, timeout=120) as r:
        return json.loads(r.read() or b"{}")


token = call("POST", "/ag-ui/user/login",
             {"username": os.environ.get("AGUI_USER", "david"),
              "password": os.environ.get("AGUI_PWD", "lingtong")})["token"]

agents = call("GET", "/ag-ui/agents", token=token)
agents = agents.get("agents", agents) if isinstance(agents, dict) else agents
skills = call("GET", "/ag-ui/skills", token=token)
skills = skills.get("skills", skills) if isinstance(skills, dict) else skills
kind = {s.get("skillId"): s.get("kind") for s in (skills or [])}

DOC_KEYS = ("docx", "xlsx", "pptx", "pdf")
BLOCKERS = ["仅接收", "不接受未", "未过合规", "未过审", "须先经", "需先经", "必须先经",
            "定稿后才", "定稿后再", "审核通过后", "审批通过后", "确认后再", "通过后才"]

print(f"共 {len(agents or [])} 个数字员工，{len(skills or [])} 个技能\n")

# 按 escalation 链找根，推断团队分组
by_id = {a.get("agentId"): a for a in agents or []}
roots = [a for a in agents or []
         if not a.get("escalationAgentId") and (a.get("assignmentIds") or [])]

def tree(root, depth=0):
    subs = root.get("assignmentIds") or []
    line = ("  " * depth) + f"{root.get('agentId'):22} {str(root.get('nickname')):16}"
    ids = root.get("skillDefIds") or []
    doc = [f"{i}({kind.get(i,'?')})" for i in ids if any(k in (i or "").lower() for k in DOC_KEYS)]
    line += f" skills={len(ids)}" + (f"  ★交付={doc}" if doc else "")
    text = (root.get("instructions") or "") + (root.get("description") or "")
    hit = next((b for b in BLOCKERS if b in text), None)
    if hit:
        line += f"  ⚠门卫语(“{hit}”)"
    print(line)
    for sid in subs:
        if sid in by_id:
            tree(by_id[sid], depth + 1)

print("== 组织树（根岗位起） ==")
seen = set()
for r in roots:
    tree(r)
    seen.add(r.get("agentId"))

orphans = [a for a in agents or [] if a.get("agentId") not in seen
           and not a.get("escalationAgentId") and not (a.get("assignmentIds") or [])]
if orphans:
    print("\n== 独立/叶子岗位 ==")
    for a in orphans:
        ids = a.get("skillDefIds") or []
        doc = [f"{i}({kind.get(i,'?')})" for i in ids if any(k in (i or "").lower() for k in DOC_KEYS)]
        text = (a.get("instructions") or "") + (a.get("description") or "")
        hit = next((b for b in BLOCKERS if b in text), None)
        line = f"  {a.get('agentId'):22} {str(a.get('nickname')):16} skills={ids}"
        if doc:
            line += f"  ★交付={doc}"
        if hit:
            line += f"  ⚠门卫语(“{hit}”)"
        print(line)
