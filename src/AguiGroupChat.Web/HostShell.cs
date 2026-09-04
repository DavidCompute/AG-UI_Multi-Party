using System.Diagnostics;
using System.Text;

namespace AguiGroupChat.Web;

/// <summary>
/// 宿主本机 shell 执行器：在“Web 宿主所在的机器”运行一段命令/脚本。
/// 使用时机：桌面版/自托管（宿主即用户本机）跑 <c>ExecutionLocation=Client</c> 的本机技能时不需要独立的本机桥，直接在此宿主上执行即可；
/// Docker + 远端浏览器场景宿主与用户不是同一台机器，Client 技能才需要本机桥（见 <see cref="AguiGroupChat.Agents.NativeTunnelService"/>）。
/// 运行隔离在由调用方给定的沙箱目录内 + 超时 + 输出截断（同 <c>/ag-ui/client-tool</c> 语义）。
/// </summary>
public static class HostShell
{
    private const int MaxTimeoutSec = 30;
    private const int MaxOutputChars = 12_000;

    /// <summary>在宿主 OS 上执行命令。Windows 用 PowerShell -EncodedCommand；否则写 bash 脚本用 /bin/bash。返回输出文本。</summary>
    public static async Task<string> RunAsync(string rootDir, string command, string? cwd, int? timeoutSec, string? query, CancellationToken ct)
    {
        // 工作目录：默认沙箱根；允许相对子目录但不允许逃逸（路径穿越防御）
        var workDir = rootDir;
        if (!string.IsNullOrWhiteSpace(cwd) && cwd != ".")
        {
            var candidate = Path.GetFullPath(Path.Combine(rootDir, cwd));
            if (candidate.StartsWith(rootDir + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                workDir = candidate;
        }
        Directory.CreateDirectory(workDir);

        // 明显 Windows PowerShell 正文(如 $ErrorActionPreference/try{/Get-CimInstance 等)在非 Windows 宿主会被写成 .sh 交给 bash →
        // 变成 “Not running in PowerShell / command not found / 退出码2” 一类的假报错。这里直接把 PS 内容拦掉并给可执行指示，
        // 而不是用 bash 去“瞎跑”它。Windows 宿主仍走 PowerShell(下方正常分支)，不受影响。
        if (!OperatingSystem.IsWindows() && LooksPowerShell(command))
            return "【需要 PowerShell 环境】该命令是 Windows PowerShell 正文，但当前执行宿主不是 Windows(无 PowerShell)。"
                + "请在本机的 Windows + PowerShell 环境(经 AguiGroupChat.NativeBridge / 桌面版宿主)执行它，"
                + "不要在服务端/Linux 宿主把它当作脚本运行——否则只会出现假报错(Not running in PowerShell / command not found)。";

        string fileName, argsText;
        if (OperatingSystem.IsWindows())
        {
            // PowerShell -EncodedCommand：命令经 base64(UTF-16LE) 编码，规避 cmd 引号 / 中文代码页问题；
            // 前置强制控制台 UTF-8，配合下方 UTF-8 解码保证中文不乱码。
            var ps = "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8;" + command;
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(ps));
            fileName = "powershell.exe";
            argsText = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + encoded;
        }
        else
        {
            var scriptPath = Path.Combine(workDir, "client_run.sh");
            // UTF8Encoding(false) 无 BOM：带 BOM 的脚本首行会被 bash 当 \xEF\xBB\xBF 前缀 → command not found
            await File.WriteAllTextAsync(scriptPath, command, new UTF8Encoding(false), ct);
            fileName = "/bin/bash";
            argsText = "\"" + scriptPath + "\"";
        }
        var argvJson = System.Text.Json.JsonSerializer.Serialize(new { query = query ?? "" });

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = argsText,
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.Environment["QUERY"] = query ?? "";
        psi.Environment["ARGV_JSON"] = argvJson;

        var timeoutMs = Math.Clamp(timeoutSec.GetValueOrDefault(MaxTimeoutSec), 1, MaxTimeoutSec) * 1000;
        using var proc = new Process { StartInfo = psi };
        if (!proc.Start()) throw new InvalidOperationException("无法启动命令进程。");
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);
        var completed = proc.WaitForExit(timeoutMs);
        if (!completed) proc.Kill(entireProcessTree: true);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (!completed) return "（客户端技能执行超时，已终止）";

        var sb = new StringBuilder();
        if (stdout.Length > 0) sb.AppendLine(stdout.TrimEnd());
        if (stderr.Length > 0) sb.AppendLine("stderr: " + stderr.TrimEnd());
        sb.AppendLine($"（退出码 {proc.ExitCode}）");
        return Truncate(sb.ToString().TrimEnd());
    }

    /// <summary>粗略判断一段命令正文是否明显是 Windows PowerShell 语法(前几行出现 PS 惯用标记或含典型 cmdlet)。</summary>
    private static bool LooksPowerShell(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return false;
        var c = command.TrimStart();
        if (c.StartsWith("#!", StringComparison.Ordinal)) return false; // bash shebang 优先
        var head = 0;
        foreach (var line in command.Split('\n'))
        {
            var t = line.Trim();
            if (t.Length == 0) continue;
            if (t.StartsWith("$ErrorActionPreference", StringComparison.OrdinalIgnoreCase)
                || t.StartsWith("try{", StringComparison.Ordinal)
                || t.StartsWith("catch", StringComparison.OrdinalIgnoreCase)
                || t.StartsWith("param(", StringComparison.OrdinalIgnoreCase))
                return true;
            if (++head >= 3) break;
        }
        return command.Contains("SilentlyContinue", StringComparison.OrdinalIgnoreCase)
            || command.Contains("$PSVersionTable", StringComparison.OrdinalIgnoreCase)
            || command.Contains("Get-CimInstance", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>把 userId 等做成可作目录名的安全分段。</summary>
    public static string SanitizeSegment(string s)
    {
        var chars = s.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.').ToArray();
        var v = new string(chars);
        return string.IsNullOrWhiteSpace(v) ? "anonymous" : v;
    }

    private static string Truncate(string? s)
        => string.IsNullOrWhiteSpace(s) ? "（命令无输出）" : (s.Length <= MaxOutputChars ? s : s[..MaxOutputChars] + "\n…（输出已截断）");
}
