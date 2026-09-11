"""验证：在实盘点“恢复内置组织工具”后，已存在的 org_architect 指令是否被刷新为最新内置版。"""
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

print("[1] 调用恢复内置组织工具…")
res = call("POST", "/ag-ui/agents/restore-org-builder", token=token)
print("    返回:", json.dumps(res, ensure_ascii=False))

print("\n[2] 检查 org_architect 指令…")
agents = call("GET", "/ag-ui/agents", token=token)
agents = agents.get("agents", agents) if isinstance(agents, dict) else agents
for a in agents or []:
    if a.get("agentId") != "org_architect":
        continue
    text = a.get("instructions") or ""
    has_new = "不要自己判断用户是不是管理员" in text
    has_old = "非管理员发" in text
    print(f"    含新口径（不要自己判断管理员身份）: {has_new}")
    print(f"    含旧口径（非管理员发…绝不写库）  : {has_old}")
    print(f"    结论: {'✓ 已刷新' if has_new and not has_old else '✗ 仍是旧版'}")
