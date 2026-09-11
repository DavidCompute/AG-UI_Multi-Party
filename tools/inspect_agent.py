"""查看编排出的数字员工的 instructions / 技能,排查“岗位以流程为由拒绝出文件”。"""
import json
import os
import sys
import urllib.request

BASE = os.environ.get("E2E_BASE", "http://127.0.0.1:5299")
st = json.load(open("tools/.e2e_state.json", encoding="utf-8"))
token = st["token"]

req = urllib.request.Request(f"{BASE}/ag-ui/agents", headers={"Authorization": "Bearer " + token})
with urllib.request.urlopen(req, timeout=60) as r:
    data = json.load(r)
agents = data.get("agents", data) if isinstance(data, dict) else data

want = sys.argv[1] if len(sys.argv) > 1 else "deliver"
for a in agents or []:
    aid = str(a.get("agentId") or "")
    nick = str(a.get("nickname") or "")
    if want.lower() not in (aid + nick).lower():
        continue
    print(f"=== {aid} / {nick} ===")
    print(f"skills      : {a.get('skillDefIds')}")
    print(f"assignments : {a.get('assignmentIds')}")
    print(f"escalation  : {a.get('escalationAgentId')}")
    print(f"triggerMode : {a.get('triggerMode')}")
    print("\n--- description ---")
    print(a.get("description"))
    print("\n--- instructions ---")
    print(a.get("instructions"))
    print()
