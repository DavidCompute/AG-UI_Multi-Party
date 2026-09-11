"""下载实盘产出的 docx 并检查段落数 / 正文开头（验证“只有标题没内容”是否已修）。"""
import io
import json
import os
import re
import sys
import urllib.request
import zipfile

BASE = os.environ.get("AGUI_BASE", "http://127.0.0.1:5200")

token = json.loads(urllib.request.urlopen(urllib.request.Request(
    BASE + "/ag-ui/user/login",
    data=json.dumps({"username": "david", "password": "lingtong"}).encode(),
    headers={"Content-Type": "application/json"}, method="POST"), timeout=60).read())["token"]

url = sys.argv[1]
req = urllib.request.Request(BASE + url, headers={"Authorization": "Bearer " + token})
body = urllib.request.urlopen(req, timeout=120).read()
z = zipfile.ZipFile(io.BytesIO(body))
xml = z.read("word/document.xml").decode("utf-8")
texts = [t for t in re.findall(r"<w:t[^>]*>([^<]*)</w:t>", xml) if t.strip()]
print(f"字节={len(body)}  段落数={len(texts)}  合法docx={body[:2] == b'PK'}")
print("正文开头 12 段：")
for t in texts[:12]:
    print("  -", t[:90])
