#!/usr/bin/env python3
"""在本地「编译 + 运行」一份技能正文（不依赖平台/容器），用于快速迭代。

平台里的技能正文（`tools/*-skills/*.cs`）不在任何 csproj 里，是运行时由 DotnetSkillHost
用 Roslyn 编译的。改完要验证渲染效果，以前只能起容器跑真实链路——一次几十秒到几分钟。
这个脚本把那份编译搬到本地，并把技能当普通函数调用：

  # 直接给 JSON
  python tools/run-skill.py tools/pptx-skills/pptx_deck.cs --json '{"title":"T","slides":[{"type":"cover"}]}'

  # 从文件读 JSON（长输入更方便，注意用 UTF-8 保存）
  python tools/run-skill.py tools/pptx-skills/pptx_deck.cs --file tools/.tmp/req.json

  # 出稿后把返回里的 produce_file.path 拷到指定目录（容器路径 /app/docs 在本地不存在时会自动改写）
  python tools/run-skill.py tools/pptx-skills/pptx_deck.cs --file req.json --out tools/.tmp

退出码：技能返回 `"ok":true` 为 0，否则 1；编译失败为 2。

设计要点：
  * 与 check-skill.py 共用同一套 `#r "nuget: X, Y"` → `#:package X@Y` 转换与 TPA 补包规则，
    否则本地跑得通、平台跑不通（或反过来）。
  * 技能是库、没有入口点，这里补一个 Main 壳直接调 `Skill.Run`。
  * 技能把产物路径写成 `/app/docs/...`（容器路径）时本地必然写不进去：脚本会把返回 JSON 里
    的路径替换为 `--out` 目录下的同名文件，并把该 JSON 重写成实际路径，方便直接打开产物。
"""
import argparse
import json
import pathlib
import re
import subprocess
import sys
import tempfile

R_NUGET = re.compile(r'#r "nuget:\s*([^,]+),\s*([^"]+)"')

# 与 check-skill.py 保持一致：平台通过 TPA 仓库直接提供、技能正文故意不写 #r 的包
TPA_PACKAGES = [
    ("using PdfSharp", "#:package PDFsharp@6.2.4"),
]

MAIN_SHIM = """
public static class __RunShim
{
    public static void Main(string[] args)
    {
        var input = System.IO.File.ReadAllText(args[0], System.Text.Encoding.UTF8);
        var output = Skill.Run(input);
        System.IO.File.WriteAllText(args[1], output, new System.Text.UTF8Encoding(false));
    }
}
"""


def convert(src: str) -> str:
    body = R_NUGET.sub(lambda m: "#:package %s@%s" % (m.group(1).strip(), m.group(2).strip()), src)
    extra = []
    for probe, pkg in TPA_PACKAGES:
        if probe not in body:
            continue
        name = pkg.split("@")[0].split(":", 1)[1]
        declared = re.search(r'#(?:r "nuget:\s*%s[, ]|:package %s@)' % (re.escape(name), re.escape(name)),
                             body, re.IGNORECASE)
        if not declared:
            extra.append(pkg)
    if extra:
        idx = body.find("\nusing ")
        block = "\n".join(extra) + "\n"
        body = (body[:idx + 1] + block + body[idx + 1:]) if idx > 0 else (block + body)
    return body


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("skill")
    ap.add_argument("--json", help="技能入参 JSON 字面量")
    ap.add_argument("--file", help="从文件读入参 JSON（UTF-8）")
    ap.add_argument("--out", help="把产物拷到这里，并改写返回 JSON 里的路径")
    args = ap.parse_args()

    if bool(args.json) == bool(args.file):
        print("必须且只能给 --json 或 --file 之一", file=sys.stderr)
        return 2

    src_path = pathlib.Path(args.skill)
    payload = args.json if args.json else src_path.parent.joinpath(args.file).read_text(encoding="utf-8")

    body = convert(src_path.read_text(encoding="utf-8")) + MAIN_SHIM
    with tempfile.TemporaryDirectory(prefix="agui-skill-run-") as tmp:
        tmpdir = pathlib.Path(tmp)
        target = tmpdir / "skill.cs"
        target.write_text(body, encoding="utf-8", newline="")
        req = tmpdir / "req.json"
        req.write_text(payload, encoding="utf-8", newline="")
        res = tmpdir / "res.json"

        build = subprocess.run(["dotnet", "build", str(target), "--nologo", "-v", "q"],
                               capture_output=True, text=True, encoding="utf-8", errors="replace", cwd=tmp)
        out = (build.stdout or "") + (build.stderr or "")
        errors = [ln for ln in out.splitlines() if "error CS" in ln or "error NU" in ln]
        if errors or build.returncode != 0:
            print("编译失败：")
            print("\n".join("  " + ln for ln in (errors or out.splitlines())))
            return 2

        # 文件式应用（file-based app）：直接 `dotnet run 文件.cs 参数…`，不能用 --project
        run = subprocess.run(["dotnet", "run", str(target), str(req), str(res)],
                             capture_output=True, text=True, encoding="utf-8", errors="replace", cwd=tmp)
        if run.returncode != 0:
            print("运行失败（returncode=%d）：" % run.returncode)
            print((run.stdout or "") + (run.stderr or ""))
            return 2

        raw = res.read_text(encoding="utf-8")

        # 容器路径 → 本地可打开的路径。必须在这里拷：临时目录一退出就被清掉了。
        local_copy = None
        if args.out:
            try:
                obj_out = json.loads(raw)
                src_file = (obj_out.get("produce_file") or {}).get("path") or obj_out.get("path")
            except Exception:
                src_file = None
            if src_file:
                sp = pathlib.Path(src_file)
                if sp.exists():
                    outdir = pathlib.Path(args.out)
                    outdir.mkdir(parents=True, exist_ok=True)
                    dst = outdir / sp.name
                    dst.write_bytes(sp.read_bytes())
                    local_copy = dst

    if local_copy is not None:
        print("产物已拷到：%s（%d 字节）" % (local_copy, local_copy.stat().st_size))

    try:
        obj = json.loads(raw)
    except Exception:
        print(raw)
        return 0

    print(json.dumps(obj, ensure_ascii=False, indent=2))
    return 0 if obj.get("ok") else 1


if __name__ == "__main__":
    sys.exit(main())
