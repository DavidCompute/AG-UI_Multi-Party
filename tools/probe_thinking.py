"""验证：deepseek-chat / deepseek-flash 在“是否思考”上的真实差异。

思考的判定口径：响应 message 里是否带非空 reasoning_content。
同时用同一道长文任务量化耗时差（这才是拖慢端到端的主因）。
"""
import json
import os
import time
import urllib.request
import urllib.error

KEY = os.environ.get("DS_KEY", "sk-9645f4e0664a46019fa501cffc8605eb")

TASKS = {
    "短问答": "请用一句话说明什么是组织架构。",
    "长文生成": "请设计一个 6 人规模的 AI 文案推广团队，"
             "输出 JSON，包含 agents 数组（每项有 agentId/nickname/description/instructions/skillIds）。只输出 JSON。",
}


def call(model, prompt, max_tokens=2000):
    body = {"model": model, "messages": [{"role": "user", "content": prompt}],
            "max_tokens": max_tokens}
    data = json.dumps(body, ensure_ascii=False).encode("utf-8")
    req = urllib.request.Request(
        "https://api.deepseek.com/chat/completions", data=data,
        headers={"Content-Type": "application/json", "Authorization": "Bearer " + KEY},
        method="POST")
    t0 = time.time()
    with urllib.request.urlopen(req, timeout=600) as r:
        res = json.load(r)
    dt = time.time() - t0
    msg = res["choices"][0]["message"]
    return dt, (msg.get("content") or ""), (msg.get("reasoning_content") or ""), res.get("usage", {})


for task_name, prompt in TASKS.items():
    print(f"\n===== {task_name} =====")
    for model in ["deepseek-chat", "deepseek-flash", "deepseek-v4-pro"]:
        try:
            dt, content, reasoning, usage = call(model, prompt)
            think = "思考" if reasoning else "不思考"
            print(f"  {model:18} {dt:6.1f}s  {think}  reasoning={len(reasoning)}字 content={len(content)}字 "
                  f"out_tokens={usage.get('completion_tokens')} reasoning_tokens={usage.get('completion_tokens_details', {}).get('reasoning_tokens')}")
        except urllib.error.HTTPError as e:
            print(f"  {model:18} HTTP {e.code}: {e.read().decode('utf-8','replace')[:160]}")
        except Exception as e:
            print(f"  {model:18} {type(e).__name__}: {e}")
