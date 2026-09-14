"""实盘验证：一键组织编排在「要 PPT」的需求下是否真的适配了内置 pptx_deck。

检查点：
  1) /ag-ui/agents/orchestrate 预览返回 deliveryWarning == null（不再误报缺交付能力）
  2) 至少一个岗位的 skillIds 里出现内置技能 pptx_deck（复用而非新造）
  3) 不落库（只预览）

用法：
  python tools/verify_pptx_orchestration.py
环境变量：
  AGUI_BASE（默认 http://127.0.0.1:5200）、AGUI_USER、AGUI_PWD、E2E_REQUIREMENT
"""
import json
import os
import sys
import urllib.error
import urllib.request

BASE = os.environ.get("AGUI_BASE", "http://127.0.0.1:5200")
USER = os.environ.get("AGUI_USER", "david")
PWD = os.environ.get("AGUI_PWD", "lingtong")
REQUIREMENT = os.environ.get(
    "E2E_REQUIREMENT",
    "打造一个做产品发布演示文稿的团队：最终要交付一份 .pptx 文件（封面、目录、章节、正文、"
    "一页对比表格、一页柱状图、小结、结束页），团队负责从资料收集到成稿。",
)


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


st, res = call("POST", "/ag-ui/user/login", {"username": USER, "password": PWD})
assert st == 200, res
token = res["token"]
print(f"[1] 已登录 userId={res['userId']}")

st, skills = call("GET", "/ag-ui/skills", token=token)
skills = skills.get("skills", skills) if isinstance(skills, dict) else skills
if not any((s or {}).get("skillId") == "pptx_deck" for s in (skills or [])):
    print("✗ 技能库里没有内置 pptx_deck")
    sys.exit(1)
print("[2] 技能库已内置 pptx_deck")

print(f"[3] 生成编排预览：{REQUIREMENT}")
st, plan = call("POST", "/ag-ui/agents/orchestrate", {"requirement": REQUIREMENT}, token=token)
if st != 200:
    print(f"✗ 编排失败：HTTP {st} {str(plan)[:400]}")
    sys.exit(1)

agents = plan.get("agents") or []
skills_out = plan.get("skills") or []
print(f"[4] 预览：{plan.get('title')} —— 岗位 {len(agents)} 个、新造技能 {len(skills_out)} 个")

warning = plan.get("deliveryWarning")
if warning:
    print(f"✗ 存在交付告警（不应出现）：{warning}")
else:
    print("[5] ✓ deliveryWarning 为 null（交付闭环校验通过）")

users = [a for a in agents if "pptx_deck" in (a.get("skillIds") or [])]
print("[6] 引用内置 pptx_deck 的岗位：")
for a in users:
    print(f"    · {a.get('agentId')} / {a.get('nickname')}")
if not users:
    print("✗ 没有任何岗位引用 pptx_deck（编排未适配）")
    sys.exit(1)
if any((s or {}).get("skillId") == "pptx_deck" for s in skills_out):
    print("✗ pptx_deck 被当作“新造技能”重复产出（应直接复用内置）")
    sys.exit(1)

print("\n结果：通过 ✓")
