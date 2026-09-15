#!/usr/bin/env python3
"""技能正文的本地语法/编译自检。

平台里的技能正文（`tools/*-skills/*.cs`）**不在任何 csproj 里**，是运行时由 DotnetSkillHost
用 Roslyn 编译的，所以 `dotnet build` 看不到它们——改完只能靠跑测试间接发现编译错误，
反馈慢且报错被包在测试失败里。

这里用 .NET 10 的「file-based app」在本地直接编译一份：
  1) 把 `#r "nuget: X, Y"` 换成 file-based app 的 `#:package X@Y`；
  2) 追加一个 Main 壳（技能本身是库，没有入口点）；
  3) `dotnet build` 并把编译器错误原样打出。

用法：
  python tools/check-skill.py tools/pptx-skills/pptx_deck.cs
  python tools/check-skill.py tools/docx-skills/out/docx_report.cs tools/xlsx-skills/xlsx_book.cs
"""
import pathlib
import re
import subprocess
import sys
import tempfile

R_NUGET = re.compile(r'#r "nuget:\s*([^,]+),\s*([^"]+)"')

MAIN_SHIM = """
public static class __CheckMain
{
    public static void Main() { }
}
"""


def convert(src: str) -> str:
    return R_NUGET.sub(lambda m: "#:package %s@%s" % (m.group(1).strip(), m.group(2).strip()), src)


def check(path: pathlib.Path) -> tuple[bool, str]:
    src = path.read_text(encoding="utf-8")
    body = convert(src) + MAIN_SHIM
    with tempfile.TemporaryDirectory(prefix="agui-skill-check-") as tmp:
        target = pathlib.Path(tmp) / "skill.cs"
        target.write_text(body, encoding="utf-8", newline="")
        # 技能目录里可能有 nuget.config 等，这里在独立临时目录里编译，避免受仓库配置影响
        proc = subprocess.run(["dotnet", "build", str(target), "--nologo", "-v", "q"],
                              capture_output=True, text=True, encoding="utf-8", errors="replace", cwd=tmp)
    out = (proc.stdout or "") + (proc.stderr or "")
    errors = [ln for ln in out.splitlines() if "error CS" in ln or "error NU" in ln]
    if errors or proc.returncode != 0:
        return False, "\n".join(errors) if errors else out
    return True, ""


def main():
    files = [pathlib.Path(a) for a in sys.argv[1:]]
    if not files:
        print(__doc__)
        sys.exit(2)
    bad = 0
    for f in files:
        ok, msg = check(f)
        if ok:
            print("OK    %s" % f)
        else:
            bad += 1
            print("FAIL  %s" % f)
            print("\n".join("      " + ln for ln in msg.splitlines()))
    sys.exit(1 if bad else 0)


if __name__ == "__main__":
    main()
