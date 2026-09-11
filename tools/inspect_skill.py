"""查看指定技能的完整定义（类型/执行位置/是否需审批/正文摘要）。"""
import json
import os
import sys
import urllib.request

BASE = os.environ.get("E2E_BASE", "http://127.0.0.1:5200")


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

wanted = sys.argv[1:] or ["md_to_docx"]
skills = call("GET", "/ag-ui/skills", token=token)
skills = skills.get("skills", skills) if isinstance(skills, dict) else skills

for s in skills or []:
    if s.get("skillId") not in wanted:
        continue
    print(f"=== {s.get('skillId')} / {s.get('name')} ===")
    print(f"kind           : {s.get('kind')}")
    print(f"executionLoc   : {s.get('executionLocation')}")
    print(f"requiresApproval: {s.get('requiresApproval')}")
    print(f"ownerId        : {s.get('ownerId')}")
    print(f"description    : {s.get('description')}")
    body = s.get("body") or ""
    print(f"body({len(body)} 字符):")
    print(body[:700])
    print()
