"""实测 DeepSeek 各候选模型：是否可用、是否支持思考、耗时。"""
import json
import time
import urllib.request
import urllib.error

KEY = "sk-9645f4e0664a46019fa501cffc8605eb"
CANDIDATES = ["deepseek-flash", "deepseek-v4-pro", "deepseek-reasoner", "deepseek-chat"]

PROMPT = "请用一句话说明什么是组织架构。"
BODY = {"messages": [{"role": "user", "content": PROMPT}], "max_tokens": 300}

for model in CANDIDATES:
    body = dict(BODY, model=model)
    data = json.dumps(body, ensure_ascii=False).encode("utf-8")
    req = urllib.request.Request(
        "https://api.deepseek.com/chat/completions", data=data,
        headers={"Content-Type": "application/json", "Authorization": "Bearer " + KEY},
        method="POST")
    t0 = time.time()
    try:
        with urllib.request.urlopen(req, timeout=180) as r:
            res = json.load(r)
        dt = time.time() - t0
        msg = res["choices"][0]["message"]
        text = (msg.get("content") or "").strip()
        reasoning = (msg.get("reasoning_content") or "").strip()
        usage = res.get("usage", {})
        print(f"[OK] {model:20} {dt:6.1f}s  content={len(text)}字 reasoning={len(reasoning)}字 tokens={usage.get('completion_tokens')}")
        print(f"     answer: {text[:120]}")
    except urllib.error.HTTPError as e:
        print(f"[ERR] {model:20} HTTP {e.code}: {e.read().decode('utf-8','replace')[:200]}")
    except Exception as e:
        print(f"[ERR] {model:20} {type(e).__name__}: {e}")
